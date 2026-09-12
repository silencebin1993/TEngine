using System;
using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Core;
using GameLogic.Ability;
using GameLogic.Battle;
using GameLogic.Battle.Feedback;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Environment;
using GameLogic.Spawning;
using GameLogic.Stats;
using GameLogic.UI.Battle;
using Unity.Mathematics;

namespace GameLogic.MetabolicSlice.Combat
{
    /// <summary>
    /// story-006 起的最小可用桥，story-003 改为消费玩家真实网格：把 ComposeEngine 出口事件
    /// （HitEvent）接到战斗伤害路径。
    ///
    /// story-007 起改读 <see cref="MetabolicSlicePanel.Instance"/> 持有的 <see cref="CarrierRegistry"/>/
    /// <see cref="GeneReserve"/>（新插槽装配），不再读旧的 SlotGrid/全局 GeneContracts；
    /// 改调 <see cref="MetabolicSliceRunner.TickCarrier"/>，只跑激活 Carrier（W12）。
    /// 零 Carrier 或激活 Carrier 三槽全空时，<see cref="MetabolicSliceRunner.TickCarrier"/>/
    /// <see cref="Carrier.CarrierCompiler"/> 自身早退，Bridge 层不重复判空。
    ///
    /// story-004 起，<see cref="ApplyEvent"/> 把 HitEvent 一等字段（Damage/Heal/Shield/Displace/
    /// Count/Scale/Spin/Orbit/ExplodeOnHit）全部接到战场，不再只消费 Damage。Shield/Displace 无
    /// 专门的内核系统（Sim 无护盾吸收/击退结算），按 story 要求做最小可读实现：Shield 记本地累加值
    /// +日志，Displace 与 `EffectDash` 都通过 <see cref="SimBridge.SetControlledPosition"/>
    /// 挪动当前受控实体，不新增 AOT 内核结构。
    ///
    /// story-007 轴 A 接线：此前每 Tick 都传 <c>new WorldState()</c>，地形/残留从未真正落地
    /// （<see cref="EnvironmentReactionCatalog"/> 也从未注册进 <see cref="_engine"/>）。
    /// 现在持有一份跨 Tick 存活的 <see cref="WorldEnvironment"/>，代表整个战场的单一格子
    /// （沿用 GDD「不建真实坐标/寻路网格」的简化），地形 tag 用 <see cref="TerrainCatalog"/> 落地，
    /// 反应留下的残留经 Payload["LeaveResidue"] 回收进 <see cref="_environment"/>。
    /// </summary>
    public sealed class MetabolicSliceBridge : GameModuleBase
    {
        public override int Priority => ModulePriority.MetabolicBridge;

        // ══════════════════════════════════════════════════════════════════════
        //  攻击节奏
        //
        //  改动前是写死的 1.5 秒一拍。在敌人只会贴上来撞你的时候还能忍；
        //  enemy-ranged-and-parry 之后场上开始有**弹幕**，1.5 秒一拍就彻底不成立了——
        //  你需要边走位边输出、需要在弹飞过来的那一瞬挥刀格挡，而不是每 1.5 秒被动放一次技能。
        //
        //  改法刻意**不动 DPS**：间隔缩短多少，每拍伤害就等比例削多少
        //  （`LegacyAttackInterval` 是归一化基准）。所以这纯粹是手感改动，
        //  整条数值曲线一个数都不用重调——想调强度是另一件事，别混在一起。
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>基准开火间隔（秒）。0.6s 是动作游戏里"连续输出"读得出来的下限附近；
        /// 再快就变成噪声，再慢就回到"放技能"而不是"打架"。</summary>
        public const float AttackIntervalBase = 0.6f;
        /// <summary>间隔下限——再快玩家分辨不出单次攻击。</summary>
        public const float AttackIntervalMin = 0.22f;
        /// <summary>间隔上限——沿用旧节奏作为最慢值（重武器可以慢，但不能比以前更慢）。</summary>
        public const float AttackIntervalMax = 1.5f;
        /// <summary>DPS 归一化基准 = 旧的固定节奏。每拍伤害 × (实际间隔 / 它)。</summary>
        public const float LegacyAttackInterval = 1.5f;

        /// <summary>本次要等多久才打下一拍。由**上一拍**产出的 <see cref="HitEvent.Speed"/> 决定——
        /// 快武器打得密而轻，重武器打得疏而重，两者 DPS 相同。</summary>
        private float _attackInterval = AttackIntervalBase;

        /// <summary>当前生效的开火间隔（秒）。持续区域"留到下次攻击为止"这类默认时长也读它。</summary>
        public float CurrentAttackInterval => _attackInterval;

        /// <summary>story-004：白模弹道 Presenter 与 <see cref="ApplyEvent"/> 共用同一个半径公式，
        /// 提升可见性避免视觉尺寸与结算半径分叉（Decision D3）。数值不变，仍是 4f。</summary>
        public const float DamageAreaRadius = 4f;
        /// <summary>story-007：白模爆炸环 Presenter 与 <see cref="ApplyEvent"/> 共用同一个半径系数，禁止另起系数（Decision D6）。</summary>
        public const float ExplodeRadiusMult = 1.6f;
        private const float ExplodeDamageMult = 0.5f;

        /// <summary>
        /// **遗留**：非弹道底盘（Field/Aura/Legacy 且未叠任何弹道基元）落点结算的飞行距离。
        ///
        /// combat-primitive-overhaul 之后，真弹道**不再**走这个数——弹体是内核实体，射程由
        /// <see cref="CombatBallistics.BaseRange"/> / 速度 / 寿命共同决定，而不是一个恒定的 9。
        /// 保留它只为不改动那些确实没有弹道语义的即时结算路径。
        /// </summary>
        public const float ImpactFlightDistance = 9f;

        /// <summary>近战扇形的圆心前移量。扇形判定本身已经解决"打背后"的问题
        /// （见 <see cref="CombatBallistics.MeleeNearRadius"/>），这个小前移只是让扇心离开身体、
        /// 读得出"往前挥"。远小于 <see cref="CombatBallistics.MeleeReach"/>，不吃掉贴身覆盖。</summary>
        public const float MeleeFrontOffset = 0.8f;

        /// <summary>story-002：Linger 未指定 TickRate 时的默认跳伤间隔秒数。</summary>
        private const float DefaultLingerTickInterval = 0.5f;

        // ══════════════════════════════════════════════════════════════════════
        //  chassis-native-primitives：底盘专属读法系数
        //
        //  立的新规矩：**每条轨迹基元在每个底盘上都必须有一个"同一直觉的本地读法"，
        //  禁止静默失效。** 此前近战/召唤/光环三个底盘的执行路径只读弹道词汇表的一个子集，
        //  于是 42 条基因里分别有 8/8/11 条挂上去毫无反应——玩家装了个基因，游戏里什么都没变。
        //  下面这些系数就是把"穿透/反弹/抛投/回旋/分裂"翻译成各底盘自己的动词。
        // ══════════════════════════════════════════════════════════════════════

        // ── 近战：这些读法合起来才让近战有"放弃射程换来的东西" ──

        /// <summary>穿透 → 触及距离 +35%/层。弹道上"穿过一个继续飞"，近战上就是"这一刀够得更远"。</summary>
        public const float MeleeReachPerPierce = 0.35f;
        /// <summary>抛投 → 跃击距离（gene_arc gravity=4 → 6.4）。弹道上是抛物线越过前排，
        /// 近战上就是"跳过去砍"——这是近战补射程差距的主要手段，参考 Hades / Dead Cells 的突进斩。</summary>
        public const float MeleeLungePerGravity = 1.6f;
        /// <summary>跃击距离上限。再远就不是突进而是传送了，玩家读不出自己去了哪。</summary>
        public const float MeleeMaxLunge = 9f;
        /// <summary>反弹 → 击退距离/层。弹道上是"撞墙弹开"，近战上是"把敌人弹开"——
        /// gene_mirror 的文案「近战则弹开敌人的弹」本来就是这个意思，此前一直没实现。</summary>
        public const float MeleeKnockbackPerBounce = 1.8f;
        /// <summary><see cref="HitEvent.Knockback"/> 字段本身 → 击退距离（org_pseudopod 的击退终于生效）。</summary>
        public const float MeleeKnockbackPerForce = 0.6f;
        /// <summary>弹反后弹体的伤害倍率。挡下来还打不疼的话没人会去挡；
        /// 1.5 让"顶着弹幕挥一刀"成为一个有回报的主动选择，而不是纯防御动作。</summary>
        public const float MeleeDeflectDamageMul = 1.5f;
        /// <summary>只配了 Orbit 没配 Spin 时的默认旋风角速度（度/秒）。</summary>
        public const float MeleeSweepRateFromOrbit = 240f;
        /// <summary>连击/旋风的默认挥击窗口秒数。单刀无旋风时根本不建窗口，逐字退化成改动前的瞬时挥击。</summary>
        public const float MeleeSwingWindow = 0.45f;
        public const float MeleeMaxSwingWindow = 1.5f;
        /// <summary>旋风摊开出刀的节拍。0.09s ≈ 窗口内 5 刀，扫过的角度读得出是一次连贯横扫。</summary>
        public const float MeleeSweepStrikeInterval = 0.09f;
        /// <summary>旋风额外刀的伤害系数。旋风买的是**覆盖面**不是 DPS——不摊薄的话
        /// 装一条 gene_flagella 等于白嫖数倍伤害，那就又变成"哪条基因数值大就用哪条"。</summary>
        public const float MeleeSweepStrikeDamageMul = 0.35f;
        /// <summary>单次挥击的出刀硬上限。防止 Count/Lifetime 配极端值时刀数无界。</summary>
        public const int MeleeMaxStrikesPerSwing = 24;
        /// <summary>近战软锁定（Homing）搜敌半径。略大于触及，够得着才吸——不是全屏自动瞄准。</summary>
        public const float MeleeHomingRange = 9f;
        /// <summary>回旋 → 二段回砍那一刀的伤害系数。</summary>
        public const float MeleeReturnDamageMul = 0.6f;
        /// <summary>分裂 → 从扇形外缘飞出的余波小弹伤害系数（近战→远程的唯一桥）。</summary>
        public const float MeleeSplitShotDamageMul = 0.45f;
        /// <summary>Return + Blood（gene_血珠）在近战底盘的读法：命中吸血转化率。</summary>
        public const float MeleeLifestealRatio = 0.25f;
        /// <summary>吸血认领的存活窗口：伤害在下一次内核 Step 才结算，得跨帧回收。</summary>
        private const float LifestealClaimWindow = 0.6f;

        // ── 召唤：召唤物的属性第一次能被基因改写 ──

        /// <summary>召唤物基准移速。<see cref="HitEvent.Speed"/> 是倍率，乘上来。</summary>
        public const float SummonBaseSpeed = 4f;
        /// <summary>召唤物基准生命。穿透/寿命在召唤底盘的读法是"这只小弟更耐打/活得更久"。</summary>
        public const float SummonBaseHealth = 1f;
        public const float SummonHealthPerPierce = 2f;
        public const float SummonHealthPerLifetime = 1.5f;
        /// <summary>召唤物基准体型。<see cref="HitEvent.Scale"/> 乘上来。</summary>
        public const float SummonBaseRadius = 0.4f;
        /// <summary>召唤物默认集结环半径。</summary>
        public const float SummonMusterRingRadius = 1.2f;
        /// <summary>Return（护卫型）时收紧的集结环半径——贴着你结阵，不往前压。</summary>
        public const float SummonGuardRingRadius = 2.2f;
        /// <summary>召唤物死亡分裂：每次裂成几只。</summary>
        public const int MinionSplitFanout = 2;
        /// <summary>召唤物死亡分裂：代数硬上限。没有这个封顶，"死了就裂"会指数爆炸。</summary>
        public const int MinionSplitMaxGenerations = 2;
        /// <summary>下一代的体型/生命缩放系数（速度反向按 2−该值 放大：小的跑得快）。</summary>
        public const float MinionSplitScale = 0.6f;

        // ── 光环：贴身圈此前吃不下 Count/Explode/Pierce/Bounce/Split ──

        /// <summary>穿透 → 光环半径 +30%/层。</summary>
        public const float AuraRadiusPerPierce = 0.3f;
        /// <summary>反弹 → 斥力场：把圈内敌人推开的距离/层。</summary>
        public const float AuraKnockbackPerBounce = 2.2f;
        /// <summary>Count &gt; 1 时脉冲摊开的窗口秒数（一次开火脉冲 N 下，而不是同一帧叠 N 次）。</summary>
        public const float AuraPulseWindow = 1.2f;
        /// <summary>Return 回旋 → 呼吸回弹的半径系数与延迟。</summary>
        public const float AuraReboundRadiusMul = 1.5f;
        public const float AuraReboundDelay = 0.35f;
        /// <summary>光环弹反只覆盖圈内层——整圈站桩就能挡的话弹幕机制会直接失效。</summary>
        public const float AuraDeflectRadiusMul = 0.55f;
        /// <summary>光环弹反的伤害倍率。低于近战的 <see cref="MeleeDeflectDamageMul"/>：
        /// 近战格挡是"朝弹飞来的方向主动挥一刀"，光环是被动的，回报理应更小。</summary>
        public const float AuraDeflectDamageMul = 1.1f;

        // ── 场地/毒：三个"为场地设计但从没接线"的字段 ──

        /// <summary>GrowthRate 扩张的半径上限，防止一个坑吃掉整张图。</summary>
        public const float LingerMaxGrowthRadius = 9f;
        /// <summary>Weave 单次连接的最大桥数——坑数是个位数，但仍要有硬上限。</summary>
        public const int WeaveMaxLinksPerZone = 3;
        /// <summary>Field 底盘 Trail 在投掷路径上洒落的摊数。</summary>
        public const int FieldTrailDrops = 3;
        /// <summary>Field 底盘带扇角时沿扇面泼开的落点数下限。</summary>
        public const int FieldSpraySpots = 3;
        /// <summary>GrowthRate 在召唤底盘的读法：孢子会长大，体型按此系数放大。</summary>
        public const float SummonRadiusPerGrowth = 0.35f;
        /// <summary>Weave 连桥小坑的半径系数（取两端较小者 × 此值）。</summary>
        public const float WeaveBridgeRadiusMul = 0.6f;

        /// <summary>残影（Delay）二次结算的伤害系数。不给等额——否则 gene_echo 就只是"伤害 ×2"，
        /// 玩家读不出"这是同一刀的回响"，而且延迟本身已经是一种收益（覆盖走出去的敌人）。</summary>
        public const float EchoDamageMul = 0.6f;
        /// <summary>节律（RhythmRate）每拍的伤害系数。节拍是白给的额外结算，单拍必须明显弱于主攻击。</summary>
        public const float RhythmDamageMul = 0.4f;

        /// <summary>story-002：全仓库 SimBridge/SimSnapshot 无玩家速度/朝向字段，Direction 定为默认前向常量。</summary>
        private static readonly float2 DefaultForward = new float2(0f, 1f);

        /// <summary>整个战场只有这一个环境格——本 story 不建真实坐标网格（沿用 WorldEnvironment 现有约定）。</summary>
        public const string ArenaCellId = "arena";

        private static readonly Dictionary<string, string> TagDisplayNames = new Dictionary<string, string>
        {
            ["Wet"] = "潮湿", ["Oil"] = "油", ["Acid"] = "酸", ["SugarFilm"] = "含糖膜",
            ["Light"] = "光照", ["Shadow"] = "阴影", ["SaltFrost"] = "盐霜",
            ["Steam"] = "蒸汽", ["Fire"] = "火", ["Burning"] = "燃烧", ["BurningGround"] = "燃烧地面",
            ["StickyAcid"] = "粘酸", ["Shock"] = "电击",
        };

        /// <summary>story-003：Spin/Orbit 命中延迟到期的最小 ephemeral 状态（数量与弹体数同级，非池化数组，见 Decision D7）。
        /// story-002：追加 Chain/Pull——这两个字段对任何命中都生效（经 <see cref="DamageAreaPrimitive"/> 统一消费），不局限于 Bolt 落点结算。</summary>
        private struct PendingMotionHit
        {
            public float2 Origin;
            public float Radius;
            public float Damage;
            public float Phase;
            public float Spin;
            public float Orbit;
            public float TimeLeft;
            public float Chain;
            public float Pull;
        }

        /// <summary>
        /// combat-primitive-overhaul：一次开火的元数据。内核弹体只认数字，不认 Tag/反应名，
        /// 而 Linger 残留要写哪种地形、gene_blood 的回程要结算成治疗、命中要播报哪个反应，
        /// 都得等弹体**真的**终结之后才知道往哪儿放。发射时把这些语义按 shotId 存一份，
        /// 弹体终结事件回来时按 <see cref="ProjectileEndEvent.SourceLogicId"/> 取回。
        ///
        /// 固定长度环形数组：一次 Tick 的开火数是个位数，环长 64 足够，且天然不会无界增长。
        /// </summary>
        private struct ShotMeta
        {
            public int ShotId;
            public float TickRate;
            public float Chain;
            public float Pull;
            public bool HealOnReturn;
            public string ReactionName;
            public string ResidueTag;

            // ── chassis-native-primitives：这三个同样只有等弹体**真的**落地才知道往哪儿放 ──
            /// <summary>残影延迟（<see cref="HitEvent.Delay"/>）：在真实落点稍后再打一次。</summary>
            public float EchoDelay;
            /// <summary>留坑扩张速率（<see cref="HitEvent.GrowthRate"/>）。</summary>
            public float GrowthRate;
            /// <summary>留坑连网半径（<see cref="HitEvent.Weave"/>）。</summary>
            public float WeaveRadius;
            /// <summary>元素配色。落点留坑要和打出它的那一发同色，玩家才认得出是谁留下的。</summary>
            public uint Tint;
        }

        /// <summary>story-002：Linger 留坑的最小 ephemeral 状态——命中点持续按 TickRate（或默认间隔）
        /// 周期性结算范围伤害，直到秒数耗尽，不受开火节奏（<see cref="CurrentAttackInterval"/>）节流。</summary>
        private struct PendingLinger
        {
            public float2 Position;
            public float Radius;
            public float DamagePerTick;
            public float Interval;
            public float NextTick;
            public float TimeLeft;
            public float Chain;
            public float Pull;

            /// <summary>chassis-native-primitives：<see cref="HitEvent.GrowthRate"/> —— 半径每秒扩张多少。
            /// gene_ripple「扩散波」的签名字段，此前写进了 HitEvent 但全仓无人读取（死字段）。</summary>
            public float GrowthRate;
        }

        /// <summary>
        /// chassis-native-primitives：<see cref="HitEvent.Delay"/> —— 命中点稍后自己再打一次。
        ///
        /// gene_echo「残影」/ gene_harmonic「谐振」/ gene_bloomlate「迟绽」的签名字段。
        /// 此前 <c>evt.Delay</c> 在整个 GameLogic 里**一次都没被读过**，所以 gene_echo 在
        /// 五个底盘上全是空装——装了跟没装完全一样。这是它第一次真的存在。
        /// </summary>
        private struct PendingEcho
        {
            public float2 Position;
            public float Radius;
            public float Damage;
            public float TimeLeft;
            public float Chain;
            public float Pull;
        }

        /// <summary>
        /// chassis-native-primitives：<see cref="HitEvent.RhythmRate"/> —— 按固定节拍自触发再打一圈。
        ///
        /// gene_rhythm「节律」的签名字段，同样此前无人读取。节律的直觉是"你自己在按心跳脉冲"，
        /// 所以每一拍都打在玩家**当前**位置（跟着走），而不是钉在开火那一刻的坐标上。
        /// 持续到下一次装配 Tick 为止，正好填满两次攻击之间的空档。
        /// </summary>
        private struct PendingRhythm
        {
            public float Radius;
            public float Damage;
            public float Interval;
            public float NextBeat;
            public float TimeLeft;
            public float Chain;
            public float Pull;
            /// <summary>false 时钉在 <see cref="Anchor"/>（光环脉冲摊开用），true 时跟随玩家（节律用）。</summary>
            public bool FollowPlayer;
            public float2 Anchor;
            /// <summary>GrowthRate 在光环上的读法：圈本身随时间越扩越大（每秒 +N）。</summary>
            public float RadiusGrowth;
        }

        /// <summary>
        /// chassis-native-primitives：近战挥击的持续窗口。
        ///
        /// 旋风（Spin→扇形逐帧旋转）、连击（Count→多刀在时间上摊开）、二段回砍（Return→反向补一刀）
        /// 都挂在它上面。<b>第一刀恒在 <see cref="ApplyMeleeChassis"/> 里立刻打完</b>，
        /// 所以「单刀 + 无旋风 + 无回砍」时这个列表根本不会被创建，逐语句退化成改动前的瞬时挥击。
        /// </summary>
        private struct PendingSwing
        {
            public float2 Dir;
            public float Reach;
            public float HalfAngleDeg;
            /// <summary>连击刀的伤害（Count 的语义本就是"多打几下"，所以满伤）。</summary>
            public float Damage;
            /// <summary>旋风摊开出来的额外刀伤害。旋风给的是**覆盖面**不是 DPS，所以必须摊薄，
            /// 否则装一条 gene_flagella 等于白嫖数倍伤害。</summary>
            public float SweepDamage;
            public float SweepRateDeg;
            public float Interval;
            public float NextStrike;
            /// <summary>还剩几刀吃满伤；耗尽后剩下的刀走 <see cref="SweepDamage"/>。</summary>
            public int FullStrikesLeft;
            public int StrikesLeft;
            public float Chain;
            public float Pull;
            public float Knockback;
            public int ShotId;
            /// <summary>最后一刀是否要反向（Return → 二段回砍）。</summary>
            public bool FinalReverse;
            /// <summary>GrowthRate 在近战上的读法：刀风随挥击越扫越大（触及每秒 +N）。</summary>
            public float ReachGrowth;
            /// <summary>这一刀是否同时格挡（Bounce → 弹反）。旋风期间等于持续格挡面。</summary>
            public bool Deflect;
        }

        /// <summary>
        /// chassis-native-primitives：吸血认领。
        ///
        /// 近战 <see cref="SimBridge.DamageCone"/> 发出的伤害要到**下一次内核 Step** 才结算，
        /// 所以"打了多少就回多少"必须跨帧回收：按 shotId 在 <see cref="BinGames.Sim.SimSnapshot.Hits"/>
        /// 里认领属于自己的命中。列表为空时 <see cref="TickLifesteal"/> 直接早退，
        /// 不存在"每帧扫命中流"的常驻开销。
        /// </summary>
        private struct LifestealClaim
        {
            public int ShotId;
            public float Ratio;
            public float TimeLeft;
        }

        private Engine _engine;
        private MetabolicSliceRunner _runner;
        private SimBridge _sim;
        private StatSheet _stats;
        private AbilitySystem _abilities;
        private WorldEnvironment _environment;
        private readonly List<PendingMotionHit> _pendingMotion = new List<PendingMotionHit>();
        private readonly List<PendingLinger> _pendingLinger = new List<PendingLinger>();
        private readonly List<PendingEcho> _pendingEcho = new List<PendingEcho>();
        private readonly List<PendingRhythm> _pendingRhythm = new List<PendingRhythm>();
        private readonly List<PendingSwing> _pendingSwing = new List<PendingSwing>();
        private readonly List<LifestealClaim> _lifestealClaims = new List<LifestealClaim>();

        /// <summary>
        /// enemy-ranged-and-parry：召唤物的分裂预算，按小弟的 LogicId 索引。
        ///
        /// <c>SplitOnHit</c> 在召唤底盘的读法是「**死了会裂开**」——弹道上"命中裂成小弹"，
        /// 召唤上就是"这只小弟死的时候分成两只更小的"。要落地就必须知道
        /// "哪只小弟带着分裂"以及"还能裂几代"，所以需要这张表：内核只回报 DeathEvent，
        /// 它不认识"分裂预算"这种玩法概念。
        /// 数量恒被 MinionCap 卡在个位数，Dictionary 开销可忽略。
        /// </summary>
        private readonly Dictionary<int, MinionSplitBudget> _minionSplitBudget =
            new Dictionary<int, MinionSplitBudget>();

        private struct MinionSplitBudget
        {
            /// <summary>还能再裂几代。归 0 后这一支到此为止。</summary>
            public int Generations;
            /// <summary>这一只自己是第几代。原样传给内核 <see cref="BinGames.Sim.SpawnRequest.Generation"/>——
            /// 内核那边有一条无论怎么配都越不过的硬顶（<c>SimConst.MaxSpawnGeneration</c>），
            /// 热更层这份预算算错也不至于把单位容量吃干净。</summary>
            public int Generation;
            /// <summary>每次裂成几只。</summary>
            public int Fanout;
            public int ArchetypeId;
            public float Speed;
            public float Health;
            public float Radius;
        }
        private float _timer;
        private int _seed;
        private float _playerShield;

        /// <summary>本次 <see cref="ApplyEvent"/> 里击退是否已由底盘分支自己按几何形状结算过
        /// （近战按扇形、光环按圈）。为真时通用兜底分支不再补一次全向推开，避免推两遍。</summary>
        private bool _knockbackHandledThisCast;

        // ── combat-primitive-overhaul：开火元数据环 ──
        private const int ShotMetaRing = 64;
        private readonly ShotMeta[] _shotMeta = new ShotMeta[ShotMetaRing];
        private int _nextShotId = 1;

        // ── story-004：沙盒累计 DPS/击杀（Decision D3，局内本地累加，不持久化）──

        /// <summary>滚动窗口秒数：近 N 秒伤害 / N 作为「近期 DPS」，与「开火起总均值」并列展示。</summary>
        public const float SandboxRollingWindowSeconds = 5f;

        private SignalScope _sandboxCombatScope;
        private readonly Queue<(float clock, float damage)> _sandboxRecentHits = new Queue<(float, float)>();
        private float _sandboxClock;
        private float _sandboxElapsedSinceFirstHit;
        private bool _sandboxHasFired;
        private float _sandboxTotalDamage;
        private int _sandboxHitCount;
        private int _sandboxKillCount;

        /// <summary>轴 A 局内提示：最近一次新增的地形/残留白话描述，供 HUD 展示（不需要打开面板也能看见）。</summary>
        public string LastEnvironmentPrompt { get; private set; } = "地面：潮湿（尚无残留反应）";

        /// <summary>story-004：最近一次非 Damage 出口（Shield/Displace/Spin/Orbit）的白话摘要，供 HUD/日志读。</summary>
        public string LastAbilityPrompt { get; private set; } = "";

        /// <summary>story-004：Shield 出口的本地累加值——Sim 无护盾吸收结算，先记账做最小可读，不接伤害减免。</summary>
        public float PlayerShield => _playerShield;

        /// <summary>story-003：当前挂起的 Spin/Orbit 延迟命中数量，供 execute_code 断言"生成即挂起、tick 完即清空"。</summary>
        public int PendingMotionCount => _pendingMotion?.Count ?? 0;

        /// <summary>
        /// combat-primitive-overhaul：当前在飞的**真实**内核弹体数量。
        /// 语义已从"挂起的伪落点条数"改为"场上还活着几发弹"——前者是被删掉的假弹道遗物。
        /// </summary>
        public int PendingImpactCount => _sim?.LiveProjectileCount ?? 0;

        /// <summary>story-002：当前挂起的 Linger 留坑数量，供 execute_code 断言。</summary>
        public int PendingLingerCount => _pendingLinger?.Count ?? 0;

        // ── chassis-native-primitives 验收探针 ──

        /// <summary>当前挂起的残影（Delay）二次结算数。gene_echo 此前恒为"不存在"。</summary>
        public int PendingEchoCount => _pendingEcho?.Count ?? 0;
        /// <summary>当前挂起的节律（RhythmRate）脉冲源数。</summary>
        public int PendingRhythmCount => _pendingRhythm?.Count ?? 0;
        /// <summary>当前挂起的近战挥击窗口数（旋风/连击/回砍）。单刀瞬时挥击恒为 0。</summary>
        public int PendingSwingCount => _pendingSwing?.Count ?? 0;
        /// <summary>最近一次挥击的旋风角速度（度/秒，0 = 不旋转）。</summary>
        public float LastMeleeSweepRateDeg { get; private set; }
        /// <summary>最近一次挥击的跃击距离（Gravity → Lunge，0 = 原地挥）。</summary>
        public float LastMeleeLungeDistance { get; private set; }
        /// <summary>最近一次挥击的击退距离（Bounce/Knockback → 推开，0 = 不推）。</summary>
        public float LastMeleeKnockbackDistance { get; private set; }
        /// <summary>最近一次挥击被击退推动的单位数（内核回报的真实值）。</summary>
        public int LastKnockbackUnitsMoved { get; private set; }
        /// <summary>最近一次挥击的实际扇心方向（Homing 软锁定后的结果，可能≠瞄准方向）。</summary>
        public float2 LastMeleeAimDirection { get; private set; }
        /// <summary>本局累计吸血回复量。</summary>
        public float LifestealHealed { get; private set; }
        /// <summary>最近一次召唤实际请求的数量（Count 在召唤底盘 = 召唤数量）。</summary>
        public int LastSummonRequested { get; private set; }
        /// <summary>最近一次召唤物的属性（移速/生命/体型），供断言"基因真的改到了小弟身上"。</summary>
        public float LastSummonSpeed { get; private set; }
        public float LastSummonHealth { get; private set; }
        public float LastSummonRadius { get; private set; }
        /// <summary>最近一次光环的实际半径（已含 Pierce 加成）。</summary>
        public float LastAuraRadius { get; private set; }
        /// <summary>本局累计因 Weave 连出的桥接小坑数。</summary>
        public int WeaveBridgesSpawned { get; private set; }

        /// <summary>本局累计弹反回去的敌方弹体数。</summary>
        public int DeflectedProjectiles { get; private set; }
        /// <summary>最近一次挥击弹反了几发。</summary>
        public int LastDeflectedCount { get; private set; }
        /// <summary>本局累计因召唤物死亡而裂出的下一代数量。</summary>
        public int MinionSplitSpawned { get; private set; }

        /// <summary>最近一次开火首发的**完整**弹体发射参数。逐字段验收用——
        /// 单挑几个字段做探针会漏掉（Radius/Weave/Pierce/Flags 这些同样是基因改出来的差异）。</summary>
        public BinGames.Sim.ProjectileRequest LastFiredRequest { get; private set; }

        /// <summary>
        /// 把"这次攻击在战场上留下了什么"压成一行文本，供
        /// <see cref="GameLogic.MetabolicSlice.DebugTools.ChassisPrimitiveMatrixSmokeReport"/>
        /// 判定「装这条基因到底有没有引起可观察变化」。
        ///
        /// 必须**穷举挂起状态的内容**而不只是条数：Linger 多留 3 秒、坑半径每秒多扩 1、
        /// 挥击窗口多转 120°/s，这些都是玩家实打实能感觉到的差别，但条数完全一样。
        /// 之前只比条数时，42×5 里有 52 对被误判成"空装"。
        /// </summary>
        public string DebugStateDigest()
        {
            var sb = new System.Text.StringBuilder(512);
            sb.Append("L:");
            for (int i = 0; i < _pendingLinger.Count; i++)
            {
                PendingLinger p = _pendingLinger[i];
                sb.Append(D(p.Position.x)).Append(',').Append(D(p.Position.y)).Append(',')
                  .Append(D(p.Radius)).Append(',').Append(D(p.DamagePerTick)).Append(',')
                  .Append(D(p.Interval)).Append(',').Append(D(p.TimeLeft)).Append(',')
                  .Append(D(p.Chain)).Append(',').Append(D(p.Pull)).Append(',')
                  .Append(D(p.GrowthRate)).Append(';');
            }
            sb.Append("|E:");
            for (int i = 0; i < _pendingEcho.Count; i++)
            {
                PendingEcho e = _pendingEcho[i];
                sb.Append(D(e.Position.x)).Append(',').Append(D(e.Position.y)).Append(',')
                  .Append(D(e.Radius)).Append(',').Append(D(e.Damage)).Append(',')
                  .Append(D(e.TimeLeft)).Append(';');
            }
            sb.Append("|R:");
            for (int i = 0; i < _pendingRhythm.Count; i++)
            {
                PendingRhythm r = _pendingRhythm[i];
                sb.Append(D(r.Radius)).Append(',').Append(D(r.Damage)).Append(',')
                  .Append(D(r.Interval)).Append(',').Append(D(r.TimeLeft)).Append(',')
                  .Append(D(r.RadiusGrowth)).Append(',')
                  .Append(r.FollowPlayer ? '1' : '0').Append(';');
            }
            sb.Append("|S:");
            for (int i = 0; i < _pendingSwing.Count; i++)
            {
                PendingSwing s = _pendingSwing[i];
                sb.Append(D(s.Dir.x)).Append(',').Append(D(s.Dir.y)).Append(',')
                  .Append(D(s.Reach)).Append(',').Append(D(s.HalfAngleDeg)).Append(',')
                  .Append(D(s.Damage)).Append(',').Append(D(s.SweepDamage)).Append(',')
                  .Append(D(s.SweepRateDeg)).Append(',').Append(D(s.Interval)).Append(',')
                  .Append(s.StrikesLeft).Append(',').Append(s.FullStrikesLeft).Append(',')
                  .Append(D(s.Knockback)).Append(',').Append(D(s.ReachGrowth)).Append(',')
                  .Append(s.FinalReverse ? '1' : '0').Append(';');
            }
            sb.Append("|M:").Append(_pendingMotion.Count);
            sb.Append("|C:").Append(_lifestealClaims.Count);
            // 已登记分裂预算的小弟数。死亡分裂要等小弟真死了才看得见效果，
            // 但"这批小弟带着分裂"本身就是一次真实的状态变化，必须计入指纹。
            sb.Append("|B:").Append(_minionSplitBudget.Count);

            BinGames.Sim.ProjectileRequest q = LastFiredRequest;
            sb.Append("|P:").Append(LastFiredProjectileCount).Append(',')
              .Append(D(q.Direction.x)).Append(',').Append(D(q.Direction.y)).Append(',')
              .Append(D(q.Speed)).Append(',').Append(D(q.Lifetime)).Append(',')
              .Append(D(q.Radius)).Append(',').Append(D(q.Damage)).Append(',')
              .Append(q.Pierce).Append(',').Append(q.BounceCount).Append(',')
              .Append(q.SplitCount).Append(',').Append(q.ChainCount).Append(',')
              .Append(D(q.Homing)).Append(',').Append(D(q.HomingRange)).Append(',')
              .Append(D(q.TurnRateDeg)).Append(',').Append(D(q.Drag)).Append(',')
              .Append(D(q.AreaRadius)).Append(',').Append(D(q.TrailDamage)).Append(',')
              .Append(D(q.LingerSeconds)).Append(',').Append(D(q.LingerRadius)).Append(',')
              .Append(D(q.WeaveRateDeg)).Append(',').Append(D(q.WeaveAmp)).Append(',')
              .Append((int)q.Flags).Append(',').Append((uint)q.ApplyStatus).Append(',')
              .Append(q.Tint);

            sb.Append("|X:").Append(D(LastMeleeStrikeRadius)).Append(',')
              .Append(D(LastMeleeConeHalfAngleDeg)).Append(',')
              .Append(D(LastMeleeSweepRateDeg)).Append(',')
              .Append(D(LastMeleeLungeDistance)).Append(',')
              .Append(D(LastMeleeKnockbackDistance)).Append(',')
              .Append(D(LastMeleeAimDirection.x)).Append(',').Append(D(LastMeleeAimDirection.y)).Append(',')
              .Append(D(LastAuraRadius)).Append(',')
              .Append(LastSummonRequested).Append(',')
              .Append(D(LastSummonSpeed)).Append(',').Append(D(LastSummonHealth)).Append(',')
              .Append(D(LastSummonRadius)).Append(',')
              .Append(D(LastDeployPos.x)).Append(',').Append(D(LastDeployPos.y)).Append(',')
              .Append(D(LifestealHealed)).Append(',')
              .Append(LastKnockbackUnitsMoved).Append(',').Append(WeaveBridgesSpawned).Append(',')
              .Append(DeflectedProjectiles).Append(',').Append(MinionSplitSpawned);
            return sb.ToString();
        }

        private static string D(float v) => v.ToString("F3");

        /// <summary>验收探针：最近一次弹体**真实**终结坐标（由内核回传，不是热更层预测的）。</summary>
        public float2 LastImpactPos { get; private set; }

        /// <summary>验收探针：最近一次近战扇形的扇心世界坐标（列表恒为 1 项——一次挥击就是一个扇形）。</summary>
        public IReadOnlyList<float2> LastMeleeStrikeOrigins => _lastMeleeStrikeOrigins;

        /// <summary>最近一次近战扇形的触及距离（已按 evt.Scale 缩放）。</summary>
        public float LastMeleeStrikeRadius { get; private set; }

        /// <summary>最近一次近战扇形的半角（度），由器官自己的 SpreadAngle 决定。</summary>
        public float LastMeleeConeHalfAngleDeg { get; private set; }

        // ── 弹道验收探针：最近一次开火实际发出的弹体参数 ──
        /// <summary>最近一次开火发出的弹体数（= Count）。</summary>
        public int LastFiredProjectileCount { get; private set; }
        /// <summary>最近一次开火首发的世界速度（u/s）。</summary>
        public float LastFiredSpeed { get; private set; }
        /// <summary>最近一次开火首发的寿命（秒）。射程 ≈ 速度 × 寿命。</summary>
        public float LastFiredLifetime { get; private set; }
        /// <summary>最近一次开火首发的出膛方向。</summary>
        public float2 LastFiredDirection { get; private set; }
        /// <summary>最近一次开火首发的追踪搜敌半径（0 = 不追踪）。</summary>
        public float LastFiredHomingRange { get; private set; }
        /// <summary>本局累计消费的弹体终结事件数，供断言"终结事件真的回到了热更层"。</summary>
        public int ProjectileEndsConsumed { get; private set; }

        /// <summary>最近一次 Field 底盘（无轨迹基元）的布场坐标。表现层直接读它，不自己再算一次偏移。
        /// 未布过场时为玩家坐标语义的零向量。</summary>
        public float2 LastDeployPos { get; private set; }

        private readonly List<float2> _lastMeleeStrikeOrigins = new List<float2>();

        /// <summary>story-006：LookDev 沙盒抑制玩家真实网格的常规 1.5s 装配 Tick（噪声源），不影响
        /// <see cref="TickPendingMotion"/> 与 <see cref="ApplyEvent"/>（沙盒发射的夹具仍要正常播完延迟命中动画）。</summary>
        public bool Suppressed { get; set; }

        /// <summary>story-010 J4：暴露 seed 只读属性，供 <see cref="GameLogic.Battle.Feedback.WhiteboxComposeAimIndicator"/>
        /// 读取当前随机数状态预测下一发形状。**禁止外部自增或写回**——写了会让真实开火的随机数漂掉。</summary>
        public int Seed => _seed;

        /// <summary>story-010 J4：暴露 Engine 供指示器读取（只读，不可写）。</summary>
        internal Engine GetEngine() => _engine;

        /// <summary>story-010 J4：暴露 Environment 供指示器读取（只读，不可写）。</summary>
        internal WorldEnvironment GetEnvironment() => _environment;

        /// <summary>供 <see cref="GameLogic.Battle.Feedback.WhiteboxComposeAimIndicator"/> 读取玩家实时世界坐标——
        /// 该指示器 story-010b(J6) 曾把预览标记的落点硬编码成世界 (0,0) 简化实现，玩家不在原点时预览就飘在
        /// 别处，跟角色完全脱节；改用真实 <see cref="SimBridge.PlayerPosition"/>（未绑定/未运行时退化为原点，
        /// 与旧行为兼容，不会新增空引用风险）。</summary>
        internal float2 GetPlayerPosition() => _sim != null ? _sim.PlayerPosition : float2.zero;

        /// <summary>story-004：沙盒累计伤害（自本局 <see cref="OnEnter"/> 起，只在 <see cref="Suppressed"/>
        /// 为真——即沙盒态——时累加，真实战斗不计入）。</summary>
        public float SandboxTotalDamage => _sandboxTotalDamage;

        /// <summary>story-004：沙盒累计命中次数（HitSignal 计数，含多发/爆炸等展开后的真实命中数）。</summary>
        public int SandboxHitCount => _sandboxHitCount;

        /// <summary>story-004：沙盒累计击杀数（KillSignal 计数；木桩默认 Health=999999 近不可摧毁，正常为 0）。</summary>
        public int SandboxKillCount => _sandboxKillCount;

        /// <summary>story-004：自沙盒内第一次命中起经过的秒数，未命中过时为 0。</summary>
        public float SandboxElapsedSinceFirstHit => _sandboxElapsedSinceFirstHit;

        /// <summary>story-004：开火起总均值 DPS = 总伤害 / 自首次命中经过秒数。</summary>
        public float SandboxAverageDps => _sandboxHasFired
            ? _sandboxTotalDamage / MathF.Max(0.001f, _sandboxElapsedSinceFirstHit)
            : 0f;

        /// <summary>story-004：近 <see cref="SandboxRollingWindowSeconds"/> 秒滚动 DPS。</summary>
        public float SandboxRollingDps
        {
            get
            {
                float sum = 0f;
                foreach ((float clock, float damage) in _sandboxRecentHits)
                {
                    sum += damage;
                }
                return sum / SandboxRollingWindowSeconds;
            }
        }

        /// <summary>story-010 J4：静态半径计算辅助方法，供指示器复用伤害区域半径计算逻辑。</summary>
        public static float DamageAreaRadiusFor(float damage, float scale)
        {
            return DamageAreaRadius * MathF.Max(0.1f, scale);
        }

        public void Bind(SimBridge sim, StatSheet stats, AbilitySystem abilities = null)
        {
            _sim = sim;
            _stats = stats;
            _abilities = abilities;
        }

        public override void OnEnter()
        {
            _engine = new Engine();
            ReactionCatalog.RegisterDefaults(_engine);
            EnvironmentReactionCatalog.Register(_engine);

            _runner = new MetabolicSliceRunner(_engine);
            _environment = new WorldEnvironment();
            foreach (string tag in TerrainCatalog.GetTags("ter_wet") ?? Array.Empty<string>())
            {
                _environment.AddTerrainTag(ArenaCellId, tag);
            }
            LastEnvironmentPrompt = "地面：潮湿（尚无残留反应）";
            LastAbilityPrompt = "";
            _playerShield = 0f;
            _timer = 0f;
            _seed = 0;

            // 局间清空所有挂起状态——否则上一局的坑/挥击窗口会漏进新一局。
            _pendingMotion.Clear();
            _pendingLinger.Clear();
            _pendingEcho.Clear();
            _pendingRhythm.Clear();
            _pendingSwing.Clear();
            _lifestealClaims.Clear();
            LifestealHealed = 0f;
            WeaveBridgesSpawned = 0;
            LastKnockbackUnitsMoved = 0;
            DeflectedProjectiles = 0;
            LastDeflectedCount = 0;
            MinionSplitSpawned = 0;
            _minionSplitBudget.Clear();

            _sandboxRecentHits.Clear();
            _sandboxClock = 0f;
            _sandboxElapsedSinceFirstHit = 0f;
            _sandboxHasFired = false;
            _sandboxTotalDamage = 0f;
            _sandboxHitCount = 0;
            _sandboxKillCount = 0;
            _sandboxCombatScope = new SignalScope();
            _sandboxCombatScope.On<HitSignal>(OnSandboxHit).On<KillSignal>(OnSandboxKill);
        }

        /// <summary>轴 A HUD 只读入口：当前战场格上的地形/残留 tag（含未过期残留）。</summary>
        public IEnumerable<string> ArenaTags => _environment != null ? _environment.GetTags(ArenaCellId) : Array.Empty<string>();

        public static string DisplayTag(string tag) => TagDisplayNames.TryGetValue(tag, out var name) ? name : tag;

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running)
            {
                return;
            }

            TickPendingMotion(dt);
            TickProjectileEnds();
            TickPendingLinger(dt);
            // chassis-native-primitives：这四条推进的是"此前根本不存在的机制"——
            // 残影/节律来自两个全仓无人读取的死字段，挥击窗口是近战第一次有持续时间，
            // 吸血是近战第一次有回报。四个列表空时逐个早退，静默期零成本。
            TickPendingSwing(dt);
            TickPendingEcho(dt);
            TickPendingRhythm(dt);
            TickLifesteal(dt);
            TickMinionSplit();

            if (Suppressed)
            {
                TickSandboxCombat(dt);
                return;
            }

            if (MetabolicSlicePanel.Instance == null)
            {
                return;
            }

            _timer += dt;
            if (_timer < _attackInterval)
            {
                return;
            }
            _timer = 0f;
            _seed++;

            CarrierRegistry registry = MetabolicSlicePanel.Instance.CarrierRegistry;
            GeneReserve reserve = MetabolicSlicePanel.Instance.GeneReserve;
            // 传持久 _environment.State（不再是每 Tick 一份新 WorldState）+ ArenaCellId，
            // 让链路里挂了地形反应 tag（如 org_perox 的 TagAttach("Fire")）的事件能真正撞上地形（Wet）。
            var events = _runner.TickCarrier(registry, reserve, _environment.State, _seed, ArenaCellId);

            // 下一拍等多久，由**这一拍**装出来的武器决定：Speed 是倍率，快武器打得更密。
            _attackInterval = ResolveAttackInterval(events);
            // DPS 归一化：间隔缩短多少，每拍就削多少伤害。
            // 只在这条真实开火路径上做——execute_code / smoke 直调 ApplyEvent 时数字保持原样，
            // 否则所有既有断言的期望值都会莫名其妙地漂。
            float cadenceMul = _attackInterval / LegacyAttackInterval;

            int consumed = 0;
            for (int i = 0; i < events.Count; i++)
            {
                HitEvent evt = events[i];
                evt.Damage *= cadenceMul;
                evt.Trail *= cadenceMul;
                DepositResidue(evt);
                if (ApplyEvent(evt))
                {
                    consumed++;
                }
            }
            _environment.Tick(1);

            TEngine.Log.Info($"[MetabolicSliceBridge] Tick 产出 {events.Count} 个 HitEvent，已应用 {consumed} 个"
                + $"（间隔 {_attackInterval:0.00}s，伤害 ×{cadenceMul:0.00} 保持 DPS 中性）");
        }

        /// <summary>
        /// 这一套装配该多久打一拍。
        ///
        /// <see cref="HitEvent.Speed"/> 是倍率（org_emitter=1.3 / org_drill=2.2 / gene_arc 更慢），
        /// 在弹道上它是弹速，在这里同一个数也决定**出手频率**——快武器打得密而轻、
        /// 重武器打得疏而重，两者 DPS 相同（伤害按间隔归一化，见调用处）。
        /// 一次装配产出多条 HitEvent 时取最快的那条：手里有一件快武器，整体节奏就跟着它走。
        /// </summary>
        private static float ResolveAttackInterval(List<HitEvent> events)
        {
            float fastest = AttackIntervalMax;
            for (int i = 0; i < events.Count; i++)
            {
                float speedMul = events[i].Speed > 0f ? math.clamp(events[i].Speed, 0.25f, 4f) : 1f;
                float interval = math.clamp(AttackIntervalBase / speedMul,
                    AttackIntervalMin, AttackIntervalMax);
                if (interval < fastest)
                {
                    fastest = interval;
                }
            }
            return events.Count > 0 ? fastest : AttackIntervalBase;
        }

        /// <summary>story-006（EMERGENCE §2）：5 类底盘的最小分类，只读 <see cref="HitEvent.AttackPattern"/>
        /// （链尾攻击模块写死、基因禁止改写，见 DESIGN §3/§4）与 <see cref="HitEvent.AuraRadius"/>，
        /// 禁止按 organId 分支。<see cref="Legacy"/> 承接 Beam/Orbit/Wave/Chain/Boomerang 等不在
        /// EMERGENCE §2 五类代表底盘范围内的既有 Pattern——原样保留迁移前行为，不纳入本 story 的
        /// 落点重构范围，避免波及未要求变动的现役器官（org_lensbeam/org_orbitcilia/org_wave）。</summary>
        private enum ChassisClass { Projectile, Melee, Field, Aura, Summon, Legacy }

        private static ChassisClass ClassifyChassis(HitEvent evt)
        {
            if (evt.AuraRadius > 0f)
            {
                return ChassisClass.Aura;
            }
            switch (evt.AttackPattern)
            {
                case AttackPattern.Projectile:
                    return ChassisClass.Projectile;
                case AttackPattern.Melee:
                case AttackPattern.Cone:
                case AttackPattern.Dash:
                case AttackPattern.Thorns:
                    return ChassisClass.Melee;
                case AttackPattern.Pool:
                case AttackPattern.Rain:
                    return ChassisClass.Field;
                case AttackPattern.SummonFollow:
                case AttackPattern.SummonAnchor:
                    return ChassisClass.Summon;
                default:
                    return ChassisClass.Legacy;
            }
        }

        /// <summary>
        /// combat-primitive-overhaul：按底盘把一次攻击落到战场上。四条互斥路径，判据只读
        /// <see cref="HitEvent.AttackPattern"/> + 字段，**禁止按 organId / geneId 分支**。
        ///
        /// - **近战**（Melee/Cone/Dash/Thorns）→ 内核扇形判定，扇角来自器官自己的 SpreadAngle。
        /// - **弹道**（Projectile，以及叠了弹道基元的 Field）→ 内核真弹体，会飞会撞会拐弯。
        /// - **光环**（AuraRadius&gt;0）→ 贴身圈，叠 Linger 时变成脚下的持续区域。
        /// - **场地/遗留** → 原地范围结算 + 可选留坑。
        ///
        /// 与改动前最大的区别：**没有"预测落点"这个概念了**。旧实现对四类底盘统一用
        /// "开火瞬间算一个 impactPos + 倒计时 + 到点打一圈"来模拟弹道，于是弹体在飞行途中并不存在
        /// （途经的敌人永远打不到），而表现层另算一条直线飞行轨迹——两条曲线只要遇上任何拐弯/反弹/
        /// 障碍就分叉，这正是"看得见的打不到、打得到的看不见"的根因。现在弹道的唯一真相是内核里那个
        /// 真实弹体，判定与渲染读的是同一份 <c>ProjectileState</c>。
        /// Summon 底盘不参与几何重构，维持原地命中（其 Homing/Trail 反馈见
        /// <see cref="ApplySummon"/>/<see cref="ApplySummonTrail"/>）。
        /// </summary>
        private void ApplyChassisDamage(HitEvent evt, ChassisClass chassis, int hits, float radius, float2 baseDir)
        {
            float2 origin = _sim.PlayerPosition;
            // 默认结算点=玩家自身；只有 Field 布场会把它推到瞄准方向上（见下方分支）。
            LastDeployPos = origin;

            if (chassis == ChassisClass.Summon)
            {
                // chassis-native-primitives：Count 在召唤底盘的读法是**召唤数量**（见 ApplySummon），
                // 不是"原地多打几下"。旧实现两头都用 hits，等于 gene_spindle 只会让宿主原地空挥，
                // 一个小弟都不多——召唤流最核心的资源轴当时完全没有基因入口。
                DamageAreaPrimitive(origin, radius, evt.Damage, evt);
                if (evt.ExplodeOnHit)
                {
                    DamageAreaPrimitive(origin, radius * ExplodeRadiusMult, evt.Damage * ExplodeDamageMult, evt);
                }
                return;
            }

            // ── 近战：真扇形，不是"身前放个大圆" ───────────────────────────────
            // 旧实现在身前 MeleeFrontOffset=2 处摆 hits 个半径 4 的圆——圆比前移量大一倍，
            // 等于背后的敌人照样挨打，玩家读不到任何"我朝哪打"。现在走内核 DamageCone：
            // 圆形范围 + 面朝锥双重判据，扇角由器官自己的 SpreadAngle 决定
            // （org_cilia ±20 精准刺 / org_pseudopod ±35 挥砍 / org_wave ±90 半圆横扫）。
            if (chassis == ChassisClass.Melee || (chassis == ChassisClass.Legacy && evt.Shape == "Melee"))
            {
                ApplyMeleeChassis(evt, hits, baseDir, origin);
                return;
            }

            if (chassis == ChassisClass.Legacy)
            {
                for (int h = 0; h < hits; h++)
                {
                    DamageAreaPrimitive(origin, radius, evt.Damage, evt);
                }
                if (evt.ExplodeOnHit)
                {
                    DamageAreaPrimitive(origin, radius * ExplodeRadiusMult, evt.Damage * ExplodeDamageMult, evt);
                }
                return;
            }

            bool ballistic = HasBallisticPrimitives(evt);

            // ── 弹道：交给内核真弹体 ─────────────────────────────────────────
            // Projectile 底盘恒走这条（org_emitter 自带 Speed>0）；Field 叠了任一弹道基元时也走
            // （"把酶雾扔出去"本来就是一次抛投）。这里**不再预测落点**——弹体自己飞、自己撞、
            // 自己拐弯，判定与渲染读同一份 ProjectileState，从架构上不可能再分叉。
            if (chassis == ChassisClass.Projectile || (chassis == ChassisClass.Field && ballistic))
            {
                FireBallistic(evt, hits, baseDir, origin, scale: MathF.Max(0.1f, evt.Scale));
                return;
            }

            if (chassis == ChassisClass.Aura)
            {
                // ── 光环：贴身持续圈，没有"飞出去"的语义 ──────────────────────
                // chassis-native-primitives：这是此前最饿的底盘——42 条基因里 11 条完全无效、
                // 15 条只剩化学味，因为它的执行路径只读 Damage/Scale/AuraRadius/Linger/Chain/Pull。
                // Count / ExplodeOnHit / Pierce / Bounce / SplitOnHit / Homing 全都掉在地上。
                ApplyAuraChassis(evt, hits, radius, origin);
                return;
            }

            // ── Field（无轨迹基元）：朝瞄准方向布场 ──────────────────────────
            // "把酶雾扔到落点"——布场点是一个纯常量偏移，没有任何飞行动力学，因此不存在
            // "预测点与真实点分叉"的问题（那是弹道才有的病）。落点算一次、存进 LastDeployPos，
            // 表现层直接读这个坐标，不自己再乘一遍系数。
            float2 deployDir = math.normalizesafe(baseDir, DefaultForward);
            float2 deployPos = origin + deployDir * CombatBallistics.FieldThrowRange;
            LastDeployPos = deployPos;

            // SpreadAngle 扇角 → **泼开**：不是把一坨扔到一个点，而是沿扇面洒成一片。
            // 这是场地底盘唯一的"形状"手段，此前 Field 分支完全不读 SpreadAngle，
            // gene_fan 挂酶雾上毫无变化。
            if (evt.SpreadAngle > 0f && evt.SpreadAngle < 360f)
            {
                int spots = Math.Max(FieldSpraySpots, hits);
                float aimAngle = math.atan2(deployDir.y, deployDir.x);
                float half = math.radians(evt.SpreadAngle) * 0.5f;
                float spotRadius = MathF.Max(radius / MathF.Sqrt(spots), CombatBallistics.LingerMinRadius * 0.5f);
                for (int j = 0; j < spots; j++)
                {
                    float u = spots > 1 ? (float)j / (spots - 1) : 0.5f;
                    float ang = aimAngle + math.lerp(-half, half, u);
                    float2 spot = origin
                        + new float2(math.cos(ang), math.sin(ang)) * CombatBallistics.FieldThrowRange;
                    DamageAreaPrimitive(spot, spotRadius, evt.Damage / spots, evt);
                    if (evt.Linger > 0f)
                    {
                        SpawnLingerZone(spot, MathF.Max(spotRadius, CombatBallistics.LingerMinRadius * 0.6f),
                            evt.Linger, evt.Damage * 0.5f / spots, evt.TickRate, evt.Chain, evt.Pull,
                            growthRate: evt.GrowthRate, weaveRadius: evt.Weave,
                            tint: ResolveProjectileTint(evt));
                    }
                }
                if (evt.ExplodeOnHit)
                {
                    DamageAreaPrimitive(deployPos, radius * ExplodeRadiusMult, evt.Damage * ExplodeDamageMult, evt);
                }
                return;
            }

            for (int h = 0; h < hits; h++)
            {
                DamageAreaPrimitive(deployPos, radius, evt.Damage, evt);
            }
            if (evt.ExplodeOnHit)
            {
                DamageAreaPrimitive(deployPos, radius * ExplodeRadiusMult, evt.Damage * ExplodeDamageMult, evt);
            }
            if (evt.Linger > 0f)
            {
                // chassis-native-primitives：GrowthRate（坑越扩越大）与 Weave（坑连成网）终于接上了。
                // 这两个字段本来就是**专为场地/毒玩法设计**的——gene_ripple / gene_weave 的签名字段，
                // 一路写到 HitEvent，宿主却一行没读，于是"放毒流"从来没有过自己的轴。
                SpawnLingerZone(deployPos, MathF.Max(radius, CombatBallistics.LingerMinRadius), evt.Linger,
                    evt.Damage * 0.5f, evt.TickRate, evt.Chain, evt.Pull,
                    growthRate: evt.GrowthRate, weaveRadius: evt.Weave,
                    tint: ResolveProjectileTint(evt));
            }

            // Trail 拖尾 → **滴落**：从你到落点这一路上等距留几摊。
            // Field 底盘没有飞行体（那是"扔"不是"射"），但"甩出去的路上洒了一路"读得通，
            // 此前 Trail 在无轨迹基元的 Field 上直接掉地上（gene_pyro/gene_slime 挂酶雾全废）。
            if (evt.Trail > 0f)
            {
                for (int t = 1; t <= FieldTrailDrops; t++)
                {
                    float u = (float)t / (FieldTrailDrops + 1);
                    SpawnLingerZone(math.lerp(origin, deployPos, u),
                        MathF.Max(radius * 0.4f, CombatBallistics.LingerMinRadius * 0.6f),
                        _attackInterval, evt.Trail, evt.TickRate, 0f, 0f,
                        growthRate: evt.GrowthRate, tint: ResolveProjectileTint(evt));
                }
            }
        }

        /// <summary>
        /// chassis-native-primitives：光环底盘的完整读法。
        ///
        /// 光环没有"飞出去"这个语义，所以轨迹基元不能照搬——但每一条都能找到一个同直觉的本地读法：
        /// 穿透 = 圈更大、反弹 = 把人推开（斥力场）、分裂 = 外缘甩出小弹、
        /// 追踪 = 圈心偏向敌人（不对称场）、发数 = 脉冲几次。
        /// </summary>
        private void ApplyAuraChassis(HitEvent evt, int hits, float radius, float2 origin)
        {
            // 光环也登记一次开火——吸血要凭它在命中流里认领自己打出的伤害。
            int shotId = NextShotId(evt);

            // ① Pierce 穿透 → 圈更大。
            radius *= 1f + MathF.Max(0f, evt.Pierce) * AuraRadiusPerPierce;
            LastAuraRadius = radius;

            // ② Homing 追踪 → 圈心朝最近敌人偏移，变成一个不对称的场。
            //    偏移量夹在半径内，圈始终罩得住自己——否则"光环"就不贴身了。
            float2 center = origin;
            if (evt.Homing > 0f)
            {
                float2? target = FindNearestHostileWithin(origin, radius * 2f);
                if (target.HasValue)
                {
                    float2 offset = target.Value - origin;
                    float len = math.length(offset);
                    if (len > 1e-4f)
                    {
                        center = origin + offset / len
                            * MathF.Min(radius * 0.5f, len * math.saturate(evt.Homing));
                    }
                }
            }

            // ③ SpreadAngle 扇角 → **探照灯**：整圈收成一个扇形。
            //    同样的能量集中在一个方向上——这是光环底盘唯一的"选朝向"手段，
            //    也让 gene_fan 在这里不再是空装。
            float coneHalf = evt.SpreadAngle > 0f && evt.SpreadAngle < 360f
                ? evt.SpreadAngle * 0.5f
                : 0f;
            float2 facing = _abilities != null ? _abilities.AimDirection : DefaultForward;
            if (coneHalf > 0f)
            {
                int chainCount = evt.Chain > 0f ? Math.Max(0, (int)MathF.Round(evt.Chain)) : 0;
                _sim.DamageCone(center, radius, math.normalizesafe(facing, DefaultForward),
                    coneHalf, evt.Damage, BinGames.Sim.SimFaction.Hostile,
                    chainCount: chainCount, nearRadius: CombatBallistics.MeleeNearRadius,
                    sourceLogicId: shotId);
                if (evt.Pull > 0f)
                {
                    _sim.ApplyStatusArea(center, radius,
                        BinGames.Sim.SimStatus.Slowed | BinGames.Sim.SimStatus.Pulled,
                        true, BinGames.Sim.SimFaction.Hostile);
                }
            }
            else
            {
                DamageAreaPrimitive(center, radius, evt.Damage, evt.Chain, evt.Pull, shotId);
            }

            // ④ Count 发数 → 脉冲次数。摊在窗口里，而不是同一帧叠 N 次
            //    （叠在同一帧玩家只会看到一次更痛的闪光，读不出"这是脉冲式光环"）。
            if (hits > 1)
            {
                _pendingRhythm.Add(new PendingRhythm
                {
                    Radius = radius,
                    Damage = evt.Damage,
                    Interval = AuraPulseWindow / hits,
                    NextBeat = AuraPulseWindow / hits,
                    TimeLeft = AuraPulseWindow,
                    Chain = evt.Chain,
                    Pull = evt.Pull,
                    FollowPlayer = true,
                    Anchor = center,
                });
            }

            // ⑤ Speed 速度 / Lifetime 寿命 → **光环持续**：开火后圈还留在你身上一段时间，
            //    按 Speed 决定跳得多快。弹道上是"飞多快/飞多久"，光环上就是"响多快/持续多久"。
            // GrowthRate 一并开窗口——"圈越扩越大"同样需要时间才长得出来。
            if (evt.Speed > 0f || evt.Lifetime > 0f || evt.GrowthRate > 0f)
            {
                float sustain = evt.Lifetime > 0f
                    ? math.clamp(evt.Lifetime, 0.2f, _attackInterval)
                    : _attackInterval * 0.5f;
                float beat = DefaultLingerTickInterval
                    / (evt.Speed > 0f ? math.clamp(evt.Speed, 0.25f, 4f) : 1f);
                _pendingRhythm.Add(new PendingRhythm
                {
                    Radius = radius,
                    Damage = evt.Damage * RhythmDamageMul,
                    Interval = beat,
                    NextBeat = beat,
                    TimeLeft = sustain,
                    Chain = evt.Chain,
                    Pull = evt.Pull,
                    FollowPlayer = true,
                    Anchor = center,
                    RadiusGrowth = evt.GrowthRate,
                });
            }

            // ⑥ Return 回旋 → **呼吸**：稍后回弹一圈更大的。
            //    弹道上是"打出去的东西会飞回来"，贴身圈上就是"压出去再收回来"。
            if (evt.Return)
            {
                _pendingEcho.Add(new PendingEcho
                {
                    Position = center,
                    Radius = radius * AuraReboundRadiusMul,
                    Damage = evt.Damage * MeleeReturnDamageMul,
                    TimeLeft = AuraReboundDelay,
                    Chain = evt.Chain,
                    Pull = evt.Pull,
                });
                if (evt.Tags.Contains("Blood"))
                {
                    // 血珠在光环上同样读作吸血——贴身场本来就是"离得近才有回报"。
                    _lifestealClaims.Add(new LifestealClaim
                    {
                        ShotId = shotId,
                        Ratio = MeleeLifestealRatio,
                        TimeLeft = LifestealClaimWindow,
                    });
                }
            }

            // ⑦ ExplodeOnHit → 爆一圈（此前 Aura 分支根本不看这个字段）。
            if (evt.ExplodeOnHit)
            {
                DamageAreaPrimitive(center, radius * ExplodeRadiusMult, evt.Damage * ExplodeDamageMult, evt);
            }

            // ⑤ Bounce 反弹 → 斥力场：把圈内的敌人推开。
            float knock = MathF.Max(0f, evt.Bounce) * AuraKnockbackPerBounce
                + MathF.Max(0f, evt.Knockback) * MeleeKnockbackPerForce;
            if (knock > 0f)
            {
                LastKnockbackUnitsMoved = _sim.Knockback(center, radius, knock);
                LastMeleeKnockbackDistance = knock;
                _knockbackHandledThisCast = true;

                // enemy-mechanics-parity：Bounce 在光环上的第二重读法同样是**弹反**，
                // 但代价与近战不同——近战的格挡面是你主动挥出去的扇形，光环是**整圈**，
                // 站着不动就能挡。所以这里刻意做得比近战弱：
                //   · 只覆盖圈内层（AuraDeflectRadiusMul），不是整个光环半径；
                //   · 弹回去的伤害倍率更低（不给近战那份 1.5）。
                // 否则"光环流免疫弹幕"——玩家会发现最优解是站桩开圈，弹幕机制直接失效。
                int n = _sim.DeflectProjectiles(center, radius * AuraDeflectRadiusMul,
                    float2.zero, 180f, shotId, AuraDeflectDamageMul);
                if (n > 0)
                {
                    DeflectedProjectiles += n;
                    LastDeflectedCount = n;
                    Signals.Publish(new ComposeChainSignal
                    {
                        Kind = "Parry", Position = center, Direction = facing,
                        Radius = radius * AuraDeflectRadiusMul, Duration = 0f,
                    });
                }
            }

            // ⑥ SplitOnHit 分裂 → 从光环外缘环形甩出小弹。
            if (evt.SplitOnHit > 0f)
            {
                int shots = Math.Max(1, (int)MathF.Round(evt.SplitOnHit));
                uint tint = ResolveProjectileTint(evt);
                for (int s = 0; s < shots; s++)
                {
                    float ring = 2f * math.PI * s / shots;
                    float2 sd = new float2(math.cos(ring), math.sin(ring));
                    BinGames.Sim.ProjectileRequest req = CombatBallistics.Build(
                        evt, center + sd * radius, sd, 0, 1,
                        MathF.Max(0.1f, evt.Scale) * 0.7f, shotId, (uint)s);
                    req.Damage = evt.Damage * MeleeSplitShotDamageMul;
                    req.SplitCount = 0;
                    req.Tint = tint;
                    _sim.FireProjectile(req);
                    if (s == 0)
                    {
                        LastFiredRequest = req;
                        LastFiredProjectileCount = shots;
                    }
                }
            }

            // ⑨ Trail 拖尾 / Linger 留坑 → 站过的地方有残留。光环没有飞行路径，两者在这里合流
            //    （同近战的处理），此前 Aura 分支只看 Linger，gene_pyro/gene_slime 挂上去全废。
            if (evt.Trail > 0f || evt.Linger > 0f)
            {
                float seconds = evt.Linger > 0f ? evt.Linger : _attackInterval;
                float perTick = evt.Linger > 0f ? evt.Damage * 0.5f : evt.Trail;
                SpawnLingerZone(center, MathF.Max(radius, CombatBallistics.LingerMinRadius), seconds,
                    perTick, evt.TickRate, evt.Chain, evt.Pull,
                    growthRate: evt.GrowthRate, weaveRadius: evt.Weave,
                    tint: ResolveProjectileTint(evt));
            }
        }

        /// <summary>
        /// 近战底盘。一次挥击 = 一个以瞄准方向为中轴的扇形；<c>Count&gt;1</c> 表示同一次挥击**打多下**
        /// （连击），不是把扇形拆成几个互不相连的小圆——后者在几何上会漏掉扇形中间的敌人，
        /// 而且玩家完全读不出"多段"和"更宽"的区别。
        /// </summary>
        private void ApplyMeleeChassis(HitEvent evt, int hits, float2 baseDir, float2 origin)
        {
            float scale = MathF.Max(0.1f, evt.Scale);

            // ① Homing 追踪 → **软锁定**：把挥击方向朝触及范围内最近的敌人拧过去。
            //    不是全屏自动瞄准——够不着的敌人不吸，所以"走位到位"仍然是玩家的活。
            float2 dir = math.normalizesafe(baseDir, DefaultForward);
            if (evt.Homing > 0f)
            {
                float2? target = FindNearestHostileWithin(origin, MeleeHomingRange);
                if (target.HasValue)
                {
                    float2 want = math.normalizesafe(target.Value - origin, dir);
                    dir = math.normalizesafe(math.lerp(dir, want, math.saturate(evt.Homing)), dir);
                }
            }
            LastMeleeAimDirection = dir;

            // ② Gravity 抛投 → **跃击**：先把自己送过去再挥。
            //    弹道上"抛物线越过前排"，近战上就是"跳过去砍"——这是近战补射程差距的主要手段
            //    （Hades 的冲刺攻击、Dead Cells 的突进斩都是这个动词）。
            float lunge = evt.Gravity > 0f
                ? MathF.Min(MeleeMaxLunge, evt.Gravity * MeleeLungePerGravity)
                : 0f;
            LastMeleeLungeDistance = lunge;
            if (lunge > 0f && _sim.World != null)
            {
                float half = _sim.ArenaHalfExtent;
                origin = math.clamp(origin + dir * lunge,
                    new float2(-half, -half), new float2(half, half));
                _sim.SetControlledPosition(origin);
            }

            // ③ Pierce 穿透 → **触及 +**。"穿过一个继续飞" 在近战上就是 "这一刀够得更远"。
            float reach = CombatBallistics.MeleeReach * scale
                * (1f + MathF.Max(0f, evt.Pierce) * MeleeReachPerPierce);
            float halfAngle = CombatBallistics.MeleeHalfAngle(evt);

            // ④ Spin/Orbit → **旋风**：扇形在挥击窗口里绕着你转。
            //    此前这两个字段会把近战整个劫持进环绕采样分支，扇形直接消失。
            float sweepRate = evt.Spin;
            if (MathF.Abs(sweepRate) < 0.01f && evt.Orbit > 0f)
            {
                sweepRate = MeleeSweepRateFromOrbit;
            }
            LastMeleeSweepRateDeg = sweepRate;

            // ⑤ Bounce 反弹 / Knockback → **击退**。gene_mirror 的文案「近战则弹开敌人的弹」
            //    终于有了实现，org_pseudopod 的 KnockbackModule 也是第一次真的推动敌人。
            float knock = MathF.Max(0f, evt.Bounce) * MeleeKnockbackPerBounce
                + MathF.Max(0f, evt.Knockback) * MeleeKnockbackPerForce;
            LastMeleeKnockbackDistance = knock;
            _knockbackHandledThisCast = knock > 0f;

            int shotId = NextShotId(evt);

            // ⑥ Return + Blood（gene_血珠）→ **吸血**。近战进圈的回报。
            //    纯 Return（无 Blood）在下面表达为二段回砍，两条读法不冲突。
            if (evt.Return && evt.Tags.Contains("Blood"))
            {
                _lifestealClaims.Add(new LifestealClaim
                {
                    ShotId = shotId,
                    Ratio = MeleeLifestealRatio,
                    TimeLeft = LifestealClaimWindow,
                });
            }

            float2 coneOrigin = origin + dir * MeleeFrontOffset;
            LastMeleeStrikeRadius = reach;
            LastMeleeConeHalfAngleDeg = halfAngle;
            _lastMeleeStrikeOrigins.Clear();
            _lastMeleeStrikeOrigins.Add(coneOrigin);

            // Bounce 的第二重读法：**弹反**（gene_mirror 文案「近战则弹开敌人的弹」）。
            // 与击退共用 Bounce 字段——同一个"把冲你来的东西打回去"的直觉，
            // 对单位是推开，对弹体是打回去。
            bool deflect = evt.Bounce > 0f;

            // 第一刀恒在本帧打完——探针与"单刀无旋风时逐字等于改动前"都依赖这一点。
            MeleeStrike(origin, dir, reach, halfAngle, evt.Damage,
                evt.Chain, evt.Pull, knock, shotId, deflect);

            // ⑦ 连击 / 回砍 / 旋风摊开进挥击窗口。
            //    Count>1 = 同一次挥击打多下（满伤）；Return = 末刀反向补一刀；
            //    旋风则按固定节拍补若干**摊薄**的刀，把扇形扫成一圈。
            int comboStrikes = Math.Max(0, hits - 1);
            bool sweeping = MathF.Abs(sweepRate) > 0.01f;
            // GrowthRate 也要开窗口——"越挥越大"必须有时间才能长得出来。
            if (comboStrikes > 0 || evt.Return || sweeping || evt.GrowthRate > 0f)
            {
                // Speed 挥速 / Lifetime 挥击时长——弹道上是"飞多快/飞多久"，近战上是"挥多快/挥多久"。
                float speedMul = evt.Speed > 0f ? math.clamp(evt.Speed, 0.25f, 4f) : 1f;
                float window = evt.Lifetime > 0f
                    ? math.clamp(evt.Lifetime, 0.1f, MeleeMaxSwingWindow)
                    : MeleeSwingWindow;
                window /= speedMul;

                int total = comboStrikes + (evt.Return ? 1 : 0);
                if (sweeping || evt.GrowthRate > 0f)
                {
                    total = Math.Max(total,
                        (int)MathF.Ceiling(window / MeleeSweepStrikeInterval));
                }
                total = Math.Min(total, MeleeMaxStrikesPerSwing);

                _pendingSwing.Add(new PendingSwing
                {
                    Dir = dir,
                    Reach = reach,
                    HalfAngleDeg = halfAngle,
                    Damage = evt.Damage,
                    SweepDamage = evt.Damage * MeleeSweepStrikeDamageMul,
                    SweepRateDeg = sweepRate,
                    Interval = window / MathF.Max(1, total),
                    NextStrike = window / MathF.Max(1, total),
                    FullStrikesLeft = comboStrikes,
                    StrikesLeft = total,
                    Chain = evt.Chain,
                    Pull = evt.Pull,
                    Knockback = knock,
                    ShotId = shotId,
                    FinalReverse = evt.Return,
                    ReachGrowth = evt.GrowthRate,
                    Deflect = deflect,
                });
            }

            // ⑧ SplitOnHit 分裂 → **余波**：从扇形外缘甩出小弹。
            //    这是近战唯一的远程投射手段，也是"近战 build 怎么处理够不着的敌人"的答案。
            if (evt.SplitOnHit > 0f)
            {
                FireMeleeAftershock(evt, dir, origin, reach, halfAngle, scale, shotId);
            }

            if (evt.ExplodeOnHit)
            {
                // 爆是全向的，不吃扇形筛选——"炸开"本来就没有面朝方向。
                DamageAreaPrimitive(coneOrigin, reach * 0.8f, evt.Damage * ExplodeDamageMult, evt);
            }

            if (evt.Trail > 0f || evt.Linger > 0f)
            {
                // 近战没有飞行路径，Trail 与 Linger 在语义上合流成"挥完在脚下留一片"。
                float seconds = evt.Linger > 0f ? evt.Linger : _attackInterval;
                float perTick = evt.Linger > 0f ? evt.Damage * 0.5f : evt.Trail;
                SpawnLingerZone(coneOrigin, MathF.Max(reach * 0.6f, CombatBallistics.LingerMinRadius),
                    seconds, perTick, evt.TickRate, evt.Chain, evt.Pull,
                    growthRate: evt.GrowthRate, weaveRadius: evt.Weave,
                    tint: ResolveProjectileTint(evt));
            }
        }

        /// <summary>
        /// 一刀。<paramref name="origin"/> 传玩家坐标，扇心前移在这里统一加——
        /// 挥击窗口里玩家还在移动，刀必须跟着人走，不能钉在开火那一帧的坐标上。
        /// </summary>
        private void MeleeStrike(float2 origin, float2 dir, float reach, float halfAngleDeg,
            float damage, float chain, float pull, float knockback, int shotId, bool deflect = false)
        {
            float2 coneOrigin = origin + dir * MeleeFrontOffset;
            int chainCount = chain > 0f ? Math.Max(0, (int)MathF.Round(chain)) : 0;

            _sim.DamageCone(coneOrigin, reach, dir, halfAngleDeg, damage,
                BinGames.Sim.SimFaction.Hostile, chainCount: chainCount,
                nearRadius: CombatBallistics.MeleeNearRadius, sourceLogicId: shotId);

            if (deflect)
            {
                // enemy-ranged-and-parry：Bounce 在近战上的**第二重**读法——弹反。
                // gene_mirror 的文案是「弹会反弹；**近战则弹开敌人的弹**」，一直只实现了前半句
                // （而且此前根本没有敌方弹体可弹，见 DESIGN §前提）。挥击的扇形同时就是格挡面：
                // 你得朝着弹飞来的方向挥，不是站着自动挡。
                int n = _sim.DeflectProjectiles(coneOrigin, reach, dir, halfAngleDeg,
                    shotId, MeleeDeflectDamageMul);
                if (n > 0)
                {
                    DeflectedProjectiles += n;
                    LastDeflectedCount = n;
                    Signals.Publish(new ComposeChainSignal
                    {
                        Kind = "Parry", Position = coneOrigin, Direction = dir,
                        Radius = reach, Duration = 0f,
                    });
                }
            }

            if (pull > 0f)
            {
                _sim.ApplyStatusArea(coneOrigin, reach,
                    BinGames.Sim.SimStatus.Slowed | BinGames.Sim.SimStatus.Pulled,
                    true, BinGames.Sim.SimFaction.Hostile);
            }

            if (knockback > 0f)
            {
                LastKnockbackUnitsMoved = _sim.Knockback(coneOrigin, reach, knockback);
            }
        }

        /// <summary>
        /// 近战余波弹：从扇形外缘沿扇面甩出 <see cref="HitEvent.SplitOnHit"/> 发小弹。
        /// 走的仍是内核真弹体（<see cref="CombatBallistics.Build"/> 唯一翻译处），
        /// 只是起点在刀锋而不是身上——**禁止**在这里另造一套飞行模拟。
        /// 余波弹自身不再分裂，避免代际递归。
        /// </summary>
        private void FireMeleeAftershock(HitEvent evt, float2 dir, float2 origin,
            float reach, float halfAngle, float scale, int shotId)
        {
            int shots = Math.Max(1, (int)MathF.Round(evt.SplitOnHit));
            float2 edge = origin + dir * reach;
            uint tint = ResolveProjectileTint(evt);

            for (int s = 0; s < shots; s++)
            {
                BinGames.Sim.ProjectileRequest req = CombatBallistics.Build(
                    evt, edge, dir, s, shots, scale * 0.7f, shotId, (uint)s);
                req.Damage = evt.Damage * MeleeSplitShotDamageMul;
                req.SplitCount = 0;
                req.Tint = tint;
                _sim.FireProjectile(req);
                if (s == 0)
                {
                    LastFiredRequest = req;
                    LastFiredProjectileCount = shots;
                }
            }
        }

        /// <summary>
        /// 弹道底盘。每发都是内核里的一个真实弹体——会飞、会撞途中的敌人、会拐弯、会被障碍挡住。
        ///
        /// 多发按 <see cref="HitEvent.SpreadAngle"/> 以**瞄准方向**为中轴左右展开
        /// （所以"纺锤分裂"是绕鼠标方向左右裂，不是绕世界 X 轴裂）。
        /// 表现层不需要也不允许再自己算一遍飞行——它读内核弹体位置。
        /// </summary>
        private void FireBallistic(HitEvent evt, int hits, float2 baseDir, float2 origin, float scale)
        {
            int shotId = NextShotId(evt);
            uint tint = ResolveProjectileTint(evt);
            LastFiredProjectileCount = 0;
            for (int h = 0; h < hits; h++)
            {
                BinGames.Sim.ProjectileRequest req = CombatBallistics.Build(
                    evt, origin, baseDir, h, hits, scale, shotId, (uint)(_seed * 397 + h));
                req.Tint = tint;
                _sim.FireProjectile(req);
                LastFiredProjectileCount++;
                if (h == 0)
                {
                    LastFiredSpeed = req.Speed;
                    LastFiredLifetime = req.Lifetime;
                    LastFiredDirection = req.Direction;
                    LastFiredHomingRange = req.HomingRange;
                    LastFiredRequest = req;
                }
            }
        }

        /// <summary>
        /// story-004：把一个 HitEvent 的全部一等字段应用到战场，不再只消费 Damage。
        /// 独立于 <see cref="OnUpdate"/> 的 Tick 节奏，供 execute_code/DebugTools 直接传合成事件验证
        /// （验收优先代码断言，见根 CLAUDE.md）。返回是否产生了任何可观察效果。
        /// </summary>
        public bool ApplyEvent(HitEvent evt)
        {
            bool applied = false;
            _knockbackHandledThisCast = false;

            if (evt.Damage > 0f)
            {
                int hits = Math.Max(1, (int)MathF.Round(evt.Count));
                // organ-gene-rebalance-v3 story-006：底盘分类只读 evt.AttackPattern（链尾攻击模块写死，
                // 基因禁止改写）+ evt.AuraRadius，禁止 organId 分支（见 ChassisClass 注释）。
                ChassisClass chassis = ClassifyChassis(evt);
                float radius = chassis == ChassisClass.Aura
                    ? evt.AuraRadius * MathF.Max(0.1f, evt.Scale)
                    : DamageAreaRadius * MathF.Max(0.1f, evt.Scale);
                float2 baseDir = _abilities != null ? _abilities.AimDirection : DefaultForward;

                // combat-primitive-overhaul：Spin/Orbit（鞭毛绕/涡旋）在**弹道底盘**上的正确表达是
                // "打出去的弹自己绕着飞"，交给内核弹体的蛇行参数（见 CombatBallistics.Build）；
                // 在**非弹道底盘**上才是"绕着你转"，走下面这条原地环绕采样。
                // 旧实现不分底盘一律走环绕采样，于是给 org_emitter 装 gene_flagella 时，
                // 弹道整个消失、只剩玩家身边几个转圈的判定点，与文案"攻击绕圈飞"完全对不上。
                // chassis-native-primitives：近战底盘也退出环绕采样。Spin/Orbit 在近战上的正确读法是
                // **旋风横扫**（扇形绕着你转一圈），而不是把扇形整个丢掉、换成几个绕圈的判定点——
                // 后者等于给近战装个基因就把近战本身取消了。
                bool ballisticCast = HasBallisticPrimitives(evt);
                bool orbitAroundSelf = (evt.Spin != 0f || evt.Orbit != 0f)
                    && chassis != ChassisClass.Projectile
                    && chassis != ChassisClass.Melee
                    && !(chassis == ChassisClass.Field && ballisticCast);

                if (orbitAroundSelf)
                {
                    float2 origin = _sim.PlayerPosition;
                    for (int h = 0; h < hits; h++)
                    {
                        float phase = 2f * math.PI * h / hits;
                        _pendingMotion.Add(new PendingMotionHit
                        {
                            Origin = origin,
                            Radius = radius,
                            Damage = evt.Damage,
                            Phase = phase,
                            Spin = evt.Spin,
                            Orbit = evt.Orbit,
                            TimeLeft = ComposeMotionMath.MotionFlightDuration,
                            Chain = evt.Chain,
                            Pull = evt.Pull,
                        });
                    }

                    if (evt.ExplodeOnHit)
                    {
                        // 与 Spin/Orbit 组合的罕见情形：沿用原「瞬时二次扩圈」，不纳入本 story 落点重构范围。
                        DamageAreaPrimitive(origin, radius * ExplodeRadiusMult, evt.Damage * ExplodeDamageMult, evt);
                    }
                }
                else
                {
                    // organ-gene-rebalance-v3 story-006（Required 1/2）：EMERGENCE §2 矩阵在此落地，
                    // 详见 ApplyChassisDamage 注释。
                    ApplyChassisDamage(evt, chassis, hits, radius, baseDir);
                }
                applied = true;

                // ── chassis-native-primitives：残影与节律是**载荷/节奏**基元，不属于任何一个底盘 ──
                // 它们决定"这次攻击在时间上怎么重复"，而不是"怎么飞"，所以任何打得出伤害的底盘
                // 都必须吃得下。此前 evt.Delay / evt.RhythmRate 在整个 GameLogic 里一次都没被读过，
                // gene_echo/gene_rhythm 因此在五个底盘上全是空装。
                bool kernelBallistic = chassis == ChassisClass.Projectile
                    || (chassis == ChassisClass.Field && ballisticCast);

                if (evt.Delay > 0f && !kernelBallistic)
                {
                    // 弹道底盘的残影不在这里挂——它要落在弹体**真实**终结点，
                    // 由 ShotMeta 带到 TickProjectileEnds 去（不能在开火瞬间猜落点，那是旧病根）。
                    _pendingEcho.Add(new PendingEcho
                    {
                        Position = LastDeployPos,
                        Radius = radius,
                        Damage = evt.Damage * EchoDamageMul,
                        TimeLeft = evt.Delay,
                        Chain = evt.Chain,
                        Pull = evt.Pull,
                    });
                }

                if (evt.RhythmRate > 0f)
                {
                    // 节拍持续到下一次装配 Tick 为止，正好填满两次攻击之间的空档；
                    // 跟着玩家走，因为"按心跳脉冲"的主语是你自己。
                    _pendingRhythm.Add(new PendingRhythm
                    {
                        Radius = radius,
                        Damage = evt.Damage * RhythmDamageMul,
                        Interval = 1f / MathF.Max(0.05f, evt.RhythmRate),
                        NextBeat = 1f / MathF.Max(0.05f, evt.RhythmRate),
                        TimeLeft = _attackInterval,
                        Chain = evt.Chain,
                        Pull = evt.Pull,
                        FollowPlayer = true,
                        Anchor = _sim.PlayerPosition,
                    });
                }

                if (evt.Tags.Contains("InheritPattern"))
                {
                    // story-006 Required 3（gene_swarm 最小实现）：宿主器官命中时，让每个存活玩家召唤物
                    // 也在自己坐标补一次同 Damage/同 Chain/Pull 的命中，即"学会这件器官的打法"——不新起
                    // 召唤物专属模式系统，复用 DamageAreaPrimitive 同一出口。底盘无关，处处检查。
                    ApplySwarmInherit(evt);
                }
            }

            if (evt.SummonId > 0 && evt.SummonCount > 0f)
            {
                // story-002 Required 4：走现有 Minion 管线（MinionRegistry 配额 + SimBridge.Spawn），
                // 不新增 Sim 公共方法，与 EffectSpawn 的生成方式一致。
                if (ApplySummon(evt))
                {
                    applied = true;
                }

                if (evt.Trail > 0f || evt.Linger > 0f)
                {
                    // story-006（EMERGENCE §2 Summon 列「迹跟随」）：只要这条链路带 SummonId，就按 Trail
                    // 字段生效，与本次是否新召唤成功（MinionCap 已满时 ApplySummon 可能返回 false）无关——
                    // 已存活的召唤物同样应该"跟随留迹"。
                    // chassis-native-primitives：Linger 同理并入——"留坑"和"留迹"在召唤底盘是同一件事
                    // （小弟走到哪渗到哪），此前只认 Trail，Linger 系基因挂召唤上全废。
                    ApplySummonTrail(evt);
                    applied = true;
                }
            }

            if (evt.Knockback > 0f && !_knockbackHandledThisCast)
            {
                // chassis-native-primitives：击退终于是真的位移了。
                //
                // 此前这里只有一行日志——注释写着"Sim 无'位移单位'公共 API"，那条 API 现在补上了
                // （SimWorld.ApplyKnockback）。近战/光环底盘在各自分支里已按扇形/圈形推过人了
                // （_knockbackHandledThisCast），剩下的底盘在结算点补一次全向推开。
                float radius = DamageAreaRadius * MathF.Max(0.1f, evt.Scale);
                LastMeleeKnockbackDistance = evt.Knockback * MeleeKnockbackPerForce;
                LastKnockbackUnitsMoved = _sim.Knockback(LastDeployPos, radius, LastMeleeKnockbackDistance);
                LastAbilityPrompt = $"击退 {LastMeleeKnockbackDistance:0.#}（推动 {LastKnockbackUnitsMoved} 个单位）";
                applied = true;
            }

            if (evt.Heal > 0f)
            {
                float maxHp = _stats?.Get(StatId.MaxHealth) ?? 100f;
                _sim.HealPlayer(evt.Heal, maxHp);
                applied = true;
            }

            if (evt.Tags.Contains("Shield") && evt.Payload.TryGetValue("ShieldAmount", out var shieldRaw)
                && shieldRaw is float shieldAmount && shieldAmount > 0f)
            {
                _playerShield += shieldAmount;
                LastAbilityPrompt = $"获得护盾 +{shieldAmount:0.#}（当前 {_playerShield:0.#}）";
                TEngine.Log.Info($"[MetabolicSliceBridge] {LastAbilityPrompt}");
                applied = true;
            }

            if (evt.Tags.Contains("Displace") && evt.Payload.TryGetValue("DisplaceDistance", out var dispRaw)
                && dispRaw is float distance && distance > 0f && _sim.World != null)
            {
                float2 pos = _sim.PlayerPosition;
                float2 dir = math.normalizesafe(pos, new float2(1f, 0f));
                float half = _sim.ArenaHalfExtent;
                float2 target = math.clamp(pos + dir * distance, new float2(-half, -half), new float2(half, half));
                _sim.SetControlledPosition(target);
                LastAbilityPrompt = $"击退位移 {distance:0.#}";
                TEngine.Log.Info($"[MetabolicSliceBridge] {LastAbilityPrompt}");
                applied = true;
            }

            if (evt.Damage <= 0f && (evt.Spin != 0f || evt.Orbit != 0f))
            {
                // story-003：Damage>0 的 Spin/Orbit 已走上面的延迟命中状态机；
                // 这里只保留 Heal/Shield/Displace 等非 Damage 出口叠加 Spin/Orbit 时的可读摘要 stub。
                LastAbilityPrompt = $"运动机制（待接弹道）：Spin={evt.Spin:0.#} Orbit={evt.Orbit:0.#}";
                applied = true;
            }

            if (applied)
            {
                float2 aimDir = _abilities != null ? _abilities.AimDirection : DefaultForward;
                ChassisClass castChassis = ClassifyChassis(evt);
                bool kernelProjectile = evt.Damage > 0f &&
                    (castChassis == ChassisClass.Projectile ||
                     (castChassis == ChassisClass.Field && HasBallisticPrimitives(evt)));
                bool melee = evt.Damage > 0f &&
                    (castChassis == ChassisClass.Melee ||
                     (castChassis == ChassisClass.Legacy && evt.Shape == "Melee"));

                // combat-primitive-overhaul：这里**不再**替表现层预测任何落点或追踪朝向。
                //
                // 旧代码在这一段独立跑了一次 FindNearestHostile，算出一个 HomingDirection 交给白模，
                // 让它"朝这个方向弯着飞 9 个单位"。问题在于判定那边用的是绝对落点
                // （lerp(origin+aim*9, enemy, homing)），而白模用的是固定飞行距离 + 方向插值——
                // 敌人在 25 米外时白模飞 10 米就散了（"只看到敌人死了，看不到弹道"），
                // 敌人在 2 米内时白模又飞过头（"看着命中了却没伤害"）。两个公式根本不是同一条曲线。
                //
                // 现在弹体是内核实体，追踪/射程/落点全在那里，表现层没有第二份真相可算。
                Signals.Publish(new ComposeCastSignal
                {
                    Shape = ComposeShapePresentation.Resolve(evt),
                    Scale = evt.Scale,
                    Count = evt.Count,
                    Spin = evt.Spin,
                    Orbit = evt.Orbit,
                    ExplodeOnHit = evt.ExplodeOnHit,
                    Tags = evt.Tags,
                    Origin = _sim.PlayerPosition,
                    Direction = aimDir,
                    Homing = evt.Homing,
                    HomingDirection = aimDir,
                    HasProjectile = evt.Damage > 0f,
                    KernelProjectile = kernelProjectile,
                    SpreadAngle = evt.SpreadAngle,
                    MeleeReach = melee ? CombatBallistics.MeleeReach * MathF.Max(0.1f, evt.Scale) : 0f,
                    MeleeHalfAngleDeg = melee ? CombatBallistics.MeleeHalfAngle(evt) : 0f,
                    // 非弹道效果的**实际**结算坐标（Field 布场点 / Aura 贴身点），已由判定侧算好。
                    // 表现层照抄这个数即可，禁止再自己乘一遍 FieldThrowRange。
                    ImpactOrigin = kernelProjectile || melee ? _sim.PlayerPosition : LastDeployPos,
                    // reaction-depth-and-combat-feel story-002：具名反应（ReactionCatalog.RegisterDefaults/
                    // EnvironmentReactionCatalog）命中时都会写 evt.Payload["Reaction"]，转发给表现层播报。
                    ReactionName = evt.Payload.TryGetValue("Reaction", out var reactionObj) ? reactionObj as string : null,
                });
            }

            return applied;
        }

        /// <summary>
        /// story-003：每帧推进挂起的 Spin/Orbit 延迟命中（不受开火节奏（<see cref="CurrentAttackInterval"/>）节流，运动必须每帧可见）。
        /// 到期条目按 <see cref="ComposeMotionMath.Offset"/> 算出真实采样点，回调与瞬时命中同一个
        /// <see cref="SimBridge.DamageArea"/> API，只是位置/时机不同。
        /// </summary>
        private void TickPendingMotion(float dt)
        {
            for (int i = _pendingMotion.Count - 1; i >= 0; i--)
            {
                PendingMotionHit hit = _pendingMotion[i];
                hit.TimeLeft -= dt;
                if (hit.TimeLeft <= 0f)
                {
                    float elapsed = ComposeMotionMath.MotionFlightDuration - hit.TimeLeft;
                    float2 strikePos = hit.Origin + ComposeMotionMath.Offset(hit.Phase, hit.Spin, hit.Orbit, elapsed);
                    DamageAreaPrimitive(strikePos, hit.Radius, hit.Damage, hit.Chain, hit.Pull);
                    _pendingMotion.RemoveAt(i);
                }
                else
                {
                    _pendingMotion[i] = hit;
                }
            }
        }

        /// <summary>
        /// combat-primitive-overhaul：消费内核回传的弹体终结事件。
        ///
        /// 这是替代旧 <c>TickPendingImpact</c> 的东西。旧做法是热更层自己维护一堆"预测落点 + 倒计时"，
        /// 到点在预测点打一发范围伤害，并在那里追加 Pierce/Bounce/Split/Return/Linger 的二级命中——
        /// 全部基于**开火瞬间**猜出来的坐标。只要弹体真的会拐弯/反弹/撞障，那个坐标就是错的，
        /// 于是"表现层看到很远命中了但没伤害"。
        ///
        /// 现在 Pierce/Bounce/Split/Return/Trail 全在内核弹体自己身上发生（真的撞、真的弹、真的裂），
        /// 热更层只需要在**真实**终结点做两件内核管不着的事：留坑（区域是玩法概念）与表现播报。
        /// 事件条数 = 本帧终结的弹体数，与敌人数无关，不违反热更层性能红线。
        /// </summary>
        private void TickProjectileEnds()
        {
            if (_sim == null || !_sim.Running)
            {
                return;
            }

            int n = _sim.ProjectileEndCount;
            for (int i = 0; i < n; i++)
            {
                BinGames.Sim.ProjectileEndEvent e = _sim.GetProjectileEnd(i);
                LastImpactPos = e.Position;
                ProjectileEndsConsumed++;

                bool hasMeta = TryGetShotMeta(e.SourceLogicId, out ShotMeta meta);

                if (hasMeta && meta.EchoDelay > 0f)
                {
                    // chassis-native-primitives：残影落在弹体**真实**终结点。
                    // 绝不能在开火瞬间按预测落点排残影——那正是本仓上一轮修掉的老病根
                    // （"表现层看到很远命中了，但那儿压根没伤害"）。
                    _pendingEcho.Add(new PendingEcho
                    {
                        Position = e.Position,
                        Radius = e.AreaRadius > 0f ? e.AreaRadius : DamageAreaRadius * 0.5f,
                        Damage = e.Damage * EchoDamageMul,
                        TimeLeft = meta.EchoDelay,
                        Chain = meta.Chain,
                        Pull = meta.Pull,
                    });
                }

                if (e.LingerSeconds > 0f)
                {
                    SpawnLingerZone(e.Position, MathF.Max(e.LingerRadius, CombatBallistics.LingerMinRadius),
                        e.LingerSeconds, e.Damage * 0.5f,
                        hasMeta ? meta.TickRate : 0f,
                        hasMeta ? meta.Chain : 0f,
                        hasMeta ? meta.Pull : 0f,
                        growthRate: hasMeta ? meta.GrowthRate : 0f,
                        weaveRadius: hasMeta ? meta.WeaveRadius : 0f,
                        tint: hasMeta ? meta.Tint : 0u);

                    if (hasMeta && !string.IsNullOrEmpty(meta.ResidueTag))
                    {
                        // 留坑落在哪里，地形残留就写在哪里——此前残留恒挂在"整个战场"这一个格上，
                        // 位置信息完全丢失。现在至少让 HUD 播报的位置是真的。
                        bool isNew = !_environment.GetTags(ArenaCellId).Contains(meta.ResidueTag);
                        _environment.AddResidue(ArenaCellId, meta.ResidueTag, 1f, (int)MathF.Ceiling(e.LingerSeconds));
                        if (isNew)
                        {
                            LastEnvironmentPrompt = $"地上起反应了：新增残留「{DisplayTag(meta.ResidueTag)}」";
                        }
                    }
                }

                if (hasMeta && meta.HealOnReturn && e.Reason == BinGames.Sim.ProjectileEndReason.HitTarget)
                {
                    // gene_blood「血珠」：打出的伤害溅出血珠飞回治疗。回程弹由内核标 Returning，
                    // 但内核不认识"治疗"这个玩法概念，所以在这里按 shot 元数据兑现。
                    float maxHp = _stats?.Get(StatId.MaxHealth) ?? 100f;
                    _sim.HealPlayer(e.Damage * BloodReturnHealRatio, maxHp);
                }

                // 表现层：在**真实**落点播命中/爆炸标记。Kind 用 "Impact"，与 Pierce/Bounce/Split
                // 那几个延续标记区分开（那些现在由内核在真实位置触发，见 OnProjectileChain）。
                Signals.Publish(new ComposeChainSignal
                {
                    Kind = e.Reason == BinGames.Sim.ProjectileEndReason.HitTarget ? "Impact" : "Fizzle",
                    Position = e.Position,
                    Direction = e.Direction,
                    Radius = e.AreaRadius > 0f ? e.AreaRadius : 1.2f,
                    Duration = e.LingerSeconds,
                });
            }
        }

        /// <summary>gene_blood 回程结算成治疗时的转化率。</summary>
        private const float BloodReturnHealRatio = 0.35f;

        /// <summary>
        /// 弹体实例色。复用表现层已有的元素配色表（<see cref="FxRecipeCatalog"/>），
        /// 让"弹道改走内核真弹体"之后**不丢**此前只有白模才有的元素染色——
        /// 火是橙的、雷是紫的、酸是绿的，玩家仍然一眼看得出这一发是什么属性。
        /// 打包成 RGBA8 随发射参数带进内核，渲染时逐实例取用（<c>SimRenderer.DrawProjectiles</c>）。
        /// </summary>
        private static uint ResolveProjectileTint(HitEvent evt)
        {
            UnityEngine.Color c;
            string element = null;
            string[] order = FxRecipeCatalog.ElementPriorityOrder;
            for (int i = 0; i < order.Length; i++)
            {
                if (evt.Tags.Contains(order[i]))
                {
                    element = order[i];
                    break;
                }
            }

            if (element != null)
            {
                c = FxRecipeCatalog.GetElementColor(element);
            }
            else if (FxRecipeCatalog.TryGetShapeRecipe(ComposeShapePresentation.Resolve(evt), out var recipe))
            {
                c = recipe.Color;
            }
            else
            {
                c = UnityEngine.Color.white;
            }

            return Pack(c);
        }

        private static uint Pack(UnityEngine.Color c)
        {
            uint r = (uint)math.clamp((int)(c.r * 255f), 0, 255);
            uint g = (uint)math.clamp((int)(c.g * 255f), 0, 255);
            uint b = (uint)math.clamp((int)(c.b * 255f), 0, 255);
            uint a = (uint)math.clamp((int)(c.a * 255f), 1, 255); // a 恒 >0，否则整包为 0 会被当成"未设色"
            return (r << 24) | (g << 16) | (b << 8) | a;
        }

        /// <summary>
        /// 登记一次开火的语义元数据，返回 shotId（写进 <see cref="BinGames.Sim.ProjectileRequest.SourceLogicId"/>）。
        /// 弹体终结时凭它取回"这一发该留什么残留 / 要不要治疗 / 跳伤频率多少"。
        /// </summary>
        private int NextShotId(HitEvent evt)
        {
            int id = _nextShotId++;
            if (_nextShotId <= 0)
            {
                _nextShotId = 1;
            }
            _shotMeta[id % ShotMetaRing] = new ShotMeta
            {
                ShotId = id,
                TickRate = evt.TickRate,
                Chain = evt.Chain,
                Pull = evt.Pull,
                HealOnReturn = evt.Return && evt.Tags.Contains("Blood"),
                ReactionName = evt.Payload.TryGetValue("Reaction", out var r) ? r as string : null,
                ResidueTag = ResolveResidueTag(evt),
                EchoDelay = evt.Delay,
                GrowthRate = evt.GrowthRate,
                WeaveRadius = evt.Weave,
                Tint = ResolveProjectileTint(evt),
            };
            return id;
        }

        private bool TryGetShotMeta(int shotId, out ShotMeta meta)
        {
            if (shotId <= 0)
            {
                meta = default;
                return false;
            }
            meta = _shotMeta[shotId % ShotMetaRing];
            // 环被绕回覆盖过就认不出来了——弹体活得比 64 次开火还久属于异常，按"无元数据"处理即可。
            return meta.ShotId == shotId;
        }

        /// <summary>留坑该写哪种地形残留：取事件 Tag 里第一个在残留词表里的。
        /// 顺序固定（不是遍历 HashSet），否则同一套 Tag 每次可能得到不同结果。</summary>
        private static string ResolveResidueTag(HitEvent evt)
        {
            for (int i = 0; i < ResiduePriority.Length; i++)
            {
                if (evt.Tags.Contains(ResiduePriority[i]))
                {
                    return ResiduePriority[i];
                }
            }
            return null;
        }

        private static readonly string[] ResiduePriority =
        {
            "Oil", "Fire", "Acid", "Wet", "SugarFilm", "Frozen", "Poison", "Shock",
        };

        /// <summary>
        /// 在落点铺一块持续区域。Linger/坑/洼/膜全部走这一个出口——它们在玩法上是同一件事
        /// （"这块地在一段时间内持续结算"），此前却分散在三套各写一遍的实现里。
        /// </summary>
        private void SpawnLingerZone(float2 pos, float radius, float seconds, float damagePerTick,
            float tickRate, float chain, float pull, float growthRate = 0f, float weaveRadius = 0f,
            uint tint = 0u)
        {
            // chassis-native-primitives：Weave「编织」——新坑落地时，与半径内已有的坑之间架桥。
            // 必须**先连后加**，否则新坑会跟自己连一条零长度的桥。
            if (weaveRadius > 0f)
            {
                WeaveLink(pos, radius, seconds, damagePerTick, tickRate, chain, pull, weaveRadius, tint);
            }

            _pendingLinger.Add(new PendingLinger
            {
                Position = pos,
                Radius = radius,
                DamagePerTick = damagePerTick,
                Interval = tickRate > 0f ? 1f / tickRate : DefaultLingerTickInterval,
                NextTick = 0f,
                TimeLeft = seconds,
                Chain = chain,
                Pull = pull,
                GrowthRate = growthRate,
            });

            // enemy-mechanics-parity：真正的持续结算已经下沉到内核区域实体
            // （玩家与敌人共用一套，见 SimBridge.SpawnZone）。
            // 上面那份 _pendingLinger 只留作**热更层账本**：Weave 要知道场上有哪些坑才能连桥，
            // 验收探针也要读得到坑的时长/半径/扩张率——内核不认识"编织"这种玩法概念。
            // 跳伤本身不再由 TickPendingLinger 发（那会打两遍），见该方法注释。
            _sim.SpawnZone(pos, radius, seconds, damagePerTick,
                tickRate > 0f ? 1f / tickRate : DefaultLingerTickInterval,
                BinGames.Sim.SimFaction.Hostile,
                growthRate: growthRate,
                maxRadius: LingerMaxGrowthRadius,
                applyStatus: pull > 0f
                    ? BinGames.Sim.SimStatus.Slowed | BinGames.Sim.SimStatus.Pulled
                    : BinGames.Sim.SimStatus.None,
                chainCount: chain > 0f ? Math.Max(0, (int)MathF.Round(chain)) : 0,
                tint: tint);

            Signals.Publish(new ComposeChainSignal
            {
                Kind = "Linger", Position = pos, Direction = new float2(0f, 1f),
                Radius = radius, Duration = seconds,
            });
        }

        /// <summary>
        /// chassis-native-primitives：<see cref="HitEvent.Weave"/> —— 把新坑和附近的旧坑连成一张网。
        ///
        /// gene_weave「编织」的文案是「把留下的坑/迹跟附近的连起来」，字段一直写到了 HitEvent，
        /// 但宿主从没读过。连法：在两坑中点铺一块较小的桥接坑，寿命取两者较短者——
        /// 这样毒网会随源坑一起消退，而不是自己长住。
        ///
        /// O(坑数)，坑数是个位数；另加 <see cref="WeaveMaxLinksPerZone"/> 硬上限，
        /// 且桥接坑本身 weaveRadius=0，不会再触发下一轮连接（无递归爆炸）。
        /// </summary>
        private void WeaveLink(float2 pos, float radius, float seconds, float damagePerTick,
            float tickRate, float chain, float pull, float weaveRadius, uint tint)
        {
            int links = 0;
            float w2 = weaveRadius * weaveRadius;
            for (int i = 0; i < _pendingLinger.Count && links < WeaveMaxLinksPerZone; i++)
            {
                PendingLinger other = _pendingLinger[i];
                if (math.distancesq(other.Position, pos) > w2)
                {
                    continue;
                }

                float bridgeRadius = MathF.Max(CombatBallistics.LingerMinRadius * 0.5f,
                    MathF.Min(radius, other.Radius) * WeaveBridgeRadiusMul);
                _pendingLinger.Add(new PendingLinger
                {
                    Position = (pos + other.Position) * 0.5f,
                    Radius = bridgeRadius,
                    DamagePerTick = damagePerTick,
                    Interval = tickRate > 0f ? 1f / tickRate : DefaultLingerTickInterval,
                    NextTick = 0f,
                    TimeLeft = MathF.Min(seconds, other.TimeLeft),
                    Chain = chain,
                    Pull = pull,
                    GrowthRate = 0f,
                });
                _sim.SpawnZone((pos + other.Position) * 0.5f, bridgeRadius,
                    MathF.Min(seconds, other.TimeLeft), damagePerTick,
                    tickRate > 0f ? 1f / tickRate : DefaultLingerTickInterval,
                    BinGames.Sim.SimFaction.Hostile,
                    chainCount: chain > 0f ? Math.Max(0, (int)MathF.Round(chain)) : 0,
                    tint: tint);
                links++;
                WeaveBridgesSpawned++;
            }
        }

        /// <summary>story-002：Linger 留坑周期结算，独立于开火节奏（<see cref="CurrentAttackInterval"/>）——DoT 不该被出手间隔卡住。
        /// chassis-native-primitives：追加 <see cref="PendingLinger.GrowthRate"/> —— 坑随时间外扩
        /// （gene_ripple「扩散波」文案「波/圈会随时间越扩越大，不是一下子变大」的字面实现）。</summary>
        private void TickPendingLinger(float dt)
        {
            for (int i = _pendingLinger.Count - 1; i >= 0; i--)
            {
                PendingLinger p = _pendingLinger[i];
                p.TimeLeft -= dt;
                if (p.GrowthRate > 0f)
                {
                    p.Radius = MathF.Min(LingerMaxGrowthRadius, p.Radius + p.GrowthRate * dt);
                }

                // enemy-mechanics-parity：**这里不再发跳伤**。
                // 跳伤由内核区域实体负责（SpawnLingerZone 里已同步下发），
                // 热更层这份只是账本——留着是因为 Weave 要知道"场上有哪些坑"才能连桥，
                // 而"编织"是玩法概念、内核不认识。两边同时打就是打两遍。
                if (p.TimeLeft <= 0f)
                {
                    _pendingLinger.RemoveAt(i);
                }
                else
                {
                    _pendingLinger[i] = p;
                }
            }
        }

        /// <summary>
        /// chassis-native-primitives：残影（<see cref="HitEvent.Delay"/>）到期二次结算。
        /// gene_echo「命中点稍后自己再打一次」的字面实现——此前这条基因在任何底盘上都毫无效果。
        /// </summary>
        private void TickPendingEcho(float dt)
        {
            for (int i = _pendingEcho.Count - 1; i >= 0; i--)
            {
                PendingEcho e = _pendingEcho[i];
                e.TimeLeft -= dt;
                if (e.TimeLeft <= 0f)
                {
                    DamageAreaPrimitive(e.Position, e.Radius, e.Damage, e.Chain, e.Pull);
                    Signals.Publish(new ComposeChainSignal
                    {
                        Kind = "Echo", Position = e.Position, Direction = new float2(0f, 1f),
                        Radius = e.Radius, Duration = 0f,
                    });
                    _pendingEcho.RemoveAt(i);
                }
                else
                {
                    _pendingEcho[i] = e;
                }
            }
        }

        /// <summary>
        /// chassis-native-primitives：节律（<see cref="HitEvent.RhythmRate"/>）按拍自触发。
        /// <see cref="PendingRhythm.FollowPlayer"/> 为真时每拍打在玩家**当前**位置——
        /// 「按固定节拍自己再打一圈」说的是你自己在脉冲，不是某个钉死的坐标在脉冲。
        /// </summary>
        private void TickPendingRhythm(float dt)
        {
            for (int i = _pendingRhythm.Count - 1; i >= 0; i--)
            {
                PendingRhythm r = _pendingRhythm[i];
                r.TimeLeft -= dt;
                r.NextBeat -= dt;
                if (r.RadiusGrowth > 0f)
                {
                    r.Radius = MathF.Min(LingerMaxGrowthRadius, r.Radius + r.RadiusGrowth * dt);
                    LastAuraRadius = r.Radius;
                }
                if (r.NextBeat <= 0f)
                {
                    float2 at = r.FollowPlayer ? _sim.PlayerPosition : r.Anchor;
                    DamageAreaPrimitive(at, r.Radius, r.Damage, r.Chain, r.Pull);
                    Signals.Publish(new ComposeChainSignal
                    {
                        Kind = "Pulse", Position = at, Direction = new float2(0f, 1f),
                        Radius = r.Radius, Duration = 0f,
                    });
                    r.NextBeat = r.Interval;
                }

                if (r.TimeLeft <= 0f)
                {
                    _pendingRhythm.RemoveAt(i);
                }
                else
                {
                    _pendingRhythm[i] = r;
                }
            }
        }

        /// <summary>
        /// chassis-native-primitives：近战挥击窗口推进——旋风逐帧转向、连击按间隔出刀、末刀反向回砍。
        /// 列表为空时（单刀瞬时挥击）整个方法零成本早退。
        /// </summary>
        private void TickPendingSwing(float dt)
        {
            for (int i = _pendingSwing.Count - 1; i >= 0; i--)
            {
                PendingSwing s = _pendingSwing[i];

                // 旋风：扇心方向每帧按角速度旋转。转的是"下一刀往哪砍"，
                // 所以哪怕出刀间隔很稀，玩家也能读出这是一次连贯的横扫而不是几次独立挥击。
                if (s.SweepRateDeg != 0f)
                {
                    s.Dir = RotateDeg(s.Dir, s.SweepRateDeg * dt);
                }

                // GrowthRate → 刀风越扫越大（gene_ripple 文案「随时间越扩越大」在近战上的读法）。
                if (s.ReachGrowth > 0f)
                {
                    s.Reach = MathF.Min(LingerMaxGrowthRadius, s.Reach + s.ReachGrowth * dt);
                    LastMeleeStrikeRadius = s.Reach;
                }

                s.NextStrike -= dt;
                if (s.NextStrike <= 0f && s.StrikesLeft > 0)
                {
                    s.StrikesLeft--;
                    // 扇心永远跟着玩家走：挥击窗口里玩家还在移动，刀不该留在原地。
                    float2 origin = _sim.PlayerPosition;
                    bool reverse = s.FinalReverse && s.StrikesLeft == 0;
                    bool full = s.FullStrikesLeft > 0;
                    if (full)
                    {
                        s.FullStrikesLeft--;
                    }

                    float2 dir = reverse ? -s.Dir : s.Dir;
                    float damage = reverse ? s.Damage * MeleeReturnDamageMul
                        : (full ? s.Damage : s.SweepDamage);
                    MeleeStrike(origin, dir, s.Reach, s.HalfAngleDeg, damage,
                        s.Chain, s.Pull, s.Knockback, s.ShotId, s.Deflect);
                    s.NextStrike = s.Interval;
                }

                if (s.StrikesLeft <= 0)
                {
                    _pendingSwing.RemoveAt(i);
                }
                else
                {
                    _pendingSwing[i] = s;
                }
            }
        }

        /// <summary>
        /// chassis-native-primitives：吸血回收。
        ///
        /// 内核伤害在下一次 Step 才结算，所以这里按 shotId 在本帧命中流里认领属于自己的伤害。
        /// <b>没有认领时整个方法在第一行返回</b>——不是每帧都在扫命中流。
        /// </summary>
        private void TickLifesteal(float dt)
        {
            if (_lifestealClaims.Count == 0 || _sim == null || !_sim.Running)
            {
                return;
            }

            BinGames.Sim.SimSnapshot snap = _sim.Snapshot;
            int n = snap.Hits.IsCreated ? snap.HitCount : 0;
            float maxHp = _stats?.Get(StatId.MaxHealth) ?? 100f;

            for (int c = _lifestealClaims.Count - 1; c >= 0; c--)
            {
                LifestealClaim claim = _lifestealClaims[c];
                float dealt = 0f;
                for (int h = 0; h < n; h++)
                {
                    if (snap.Hits[h].SourceLogicId == claim.ShotId)
                    {
                        dealt += snap.Hits[h].Damage;
                    }
                }
                if (dealt > 0f)
                {
                    float heal = dealt * claim.Ratio;
                    _sim.HealPlayer(heal, maxHp);
                    LifestealHealed += heal;
                }

                claim.TimeLeft -= dt;
                if (claim.TimeLeft <= 0f)
                {
                    _lifestealClaims.RemoveAt(c);
                }
                else
                {
                    _lifestealClaims[c] = claim;
                }
            }
        }

        private static float2 RotateDeg(float2 v, float deg)
        {
            math.sincos(math.radians(deg), out float sn, out float cs);
            return new float2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
        }

        /// <summary>
        /// enemy-ranged-and-parry：召唤物死亡分裂。
        ///
        /// 从内核回传的死亡事件里认领**自己登记过分裂预算**的小弟，就地裂出更小的下一代。
        /// 下一代按 <see cref="MinionSplitScale"/> 缩小/减弱，代数由预算封顶——
        /// 没有这个封顶，"死了就裂"是会指数爆炸的。
        ///
        /// 预算表为空时整个方法第一行返回，静默期零成本。
        /// </summary>
        private void TickMinionSplit()
        {
            if (_minionSplitBudget.Count == 0 || _sim == null || !_sim.Running || _sim.World == null)
            {
                return;
            }

            BinGames.Sim.SimSnapshot snap = _sim.Snapshot;
            int n = snap.Deaths.IsCreated ? snap.DeathCount : 0;
            for (int i = 0; i < n; i++)
            {
                BinGames.Sim.DeathEvent d = snap.Deaths[i];
                if (d.Faction != BinGames.Sim.SimFaction.PlayerMinion)
                {
                    continue;
                }
                if (!_minionSplitBudget.TryGetValue(d.LogicId, out MinionSplitBudget budget))
                {
                    continue;
                }
                _minionSplitBudget.Remove(d.LogicId);
                if (budget.Generations <= 0)
                {
                    continue;
                }

                float childHealth = MathF.Max(1f, budget.Health * MinionSplitScale);
                float childRadius = MathF.Max(0.15f, budget.Radius * MinionSplitScale);
                // 更小的孩子跑得更快——读起来像"炸成一群小的散开"，而不是"原地复制两份"。
                float childSpeed = budget.Speed * (2f - MinionSplitScale);

                for (int c = 0; c < budget.Fanout; c++)
                {
                    float angle = 2f * math.PI * c / budget.Fanout;
                    int childLogicId = _sim.NextLogicId();
                    _sim.Spawn(new BinGames.Sim.SpawnRequest
                    {
                        Position = d.Position + new float2(math.cos(angle), math.sin(angle))
                            * (childRadius + 0.3f),
                        Velocity = float2.zero,
                        Health = childHealth,
                        Radius = childRadius,
                        MaxSpeed = childSpeed,
                        ArchetypeId = budget.ArchetypeId,
                        Faction = BinGames.Sim.SimFaction.PlayerMinion,
                        LogicId = childLogicId,
                        VisualId = budget.ArchetypeId,
                        // 与内核同一套血统计数：热更层的分裂预算算错时，
                        // 内核的 MaxSpawnGeneration 硬顶仍然兜得住。
                        Generation = (byte)Math.Min(budget.Generation + 1,
                            BinGames.Sim.SimConst.MaxSpawnGeneration),
                    });
                    MinionSplitSpawned++;

                    if (budget.Generations > 1)
                    {
                        _minionSplitBudget[childLogicId] = new MinionSplitBudget
                        {
                            Generations = budget.Generations - 1,
                            Generation = budget.Generation + 1,
                            Fanout = budget.Fanout,
                            ArchetypeId = budget.ArchetypeId,
                            Speed = childSpeed,
                            Health = childHealth,
                            Radius = childRadius,
                        };
                    }
                }

                Signals.Publish(new ComposeChainSignal
                {
                    Kind = "MinionSplit", Position = d.Position, Direction = new float2(0f, 1f),
                    Radius = childRadius * 2f, Duration = 0f,
                });
            }
        }

        /// <summary>story-002：召唤——经现有 Minion 管线（<see cref="MinionRegistry"/> 配额 + <see cref="SimBridge.Spawn"/>），
        /// 与 <see cref="GameLogic.Ability.Executors.EffectSpawn"/> 同一生成方式，不新增 Sim 公共方法签名。</summary>
        private bool ApplySummon(HitEvent evt)
        {
            if (_sim?.World == null)
            {
                return false;
            }

            // chassis-native-primitives：Count 发数 → **召唤数量**。
            // 这是召唤流此前完全缺失的入口——42 条基因里没有任何一条能写 SummonCount，
            // 所以"多召唤一个"在整个游戏里做不到。Count 默认值是 1，故减掉那一份基数。
            int extra = Math.Max(0, (int)MathF.Round(evt.Count) - 1);
            int requested = Math.Max(1, (int)MathF.Round(evt.SummonCount) + extra);
            MinionRegistry minions = Hub?.Get<MinionRegistry>();
            int cap = (int)(_stats?.Get(StatId.MinionCap) ?? requested);
            int granted = minions != null ? minions.Reserve(requested, cap) : requested;
            if (granted <= 0)
            {
                return false;
            }

            float2 origin = _sim.PlayerPosition;
            // organ-gene-rebalance-v3 story-006（EMERGENCE §2 Summon 列「Homing/趋化：小弟追敌」）：
            // Sim 没有暴露"改召唤物追击强度"的公共 API（改会碰 BinGames.Sim 契约，本 story 未 AOT-allowed），
            // 最小可读实现——出生点先偏向最近敌人，后续追击交给 MinionSeekAttack 行为原型自身完成，
            // Bridge 不新增按帧扫描。只读 evt.Homing 字段，禁止按 organId 分支。
            // ── chassis-native-primitives：召唤物属性第一次能被基因改写 ────────────
            // 弹道基元在召唤底盘的读法：你造的不是弹，是**兵**，所以"飞多快"就是"跑多快"、
            // "穿几个/活多久"就是"多耐打"、"多大"就是"多大只"。
            // 此前这些字段在 Summon 底盘上全部掉在地上（8 条基因完全无效）。
            float summonScale = MathF.Max(0.1f, evt.Scale);
            float summonSpeed = SummonBaseSpeed
                * (evt.Speed > 0f ? math.clamp(evt.Speed, 0.25f, 4f) : 1f);
            float summonHealth = SummonBaseHealth
                + MathF.Max(0f, evt.Pierce) * SummonHealthPerPierce
                + MathF.Max(0f, evt.Lifetime) * SummonHealthPerLifetime;
            // GrowthRate 在召唤底盘的读法：**孢子会长大**（gene_ripple「越扩越大」的本地读法）。
            float summonRadius = SummonBaseRadius * summonScale
                * (1f + MathF.Max(0f, evt.GrowthRate) * SummonRadiusPerGrowth);

            LastSummonRequested = requested;
            LastSummonSpeed = summonSpeed;
            LastSummonHealth = summonHealth;
            LastSummonRadius = summonRadius;

            // Gravity 抛投 → **空投**：小弟不是从你脚边冒出来，而是被扔到瞄准方向前方。
            // 弹道上"抛物线落在远处"，召唤上就是"把兵投过去"。
            float2 aim = math.normalizesafe(
                _abilities != null ? _abilities.AimDirection : DefaultForward, DefaultForward);
            float2 muster = origin;
            if (evt.Gravity > 0f)
            {
                float half = _sim.ArenaHalfExtent;
                muster = math.clamp(
                    origin + aim * MathF.Min(CombatBallistics.FieldThrowRange, evt.Gravity * MeleeLungePerGravity),
                    new float2(-half, -half), new float2(half, half));
            }

            // Return 回旋 → **护卫型**：贴着你结阵，不往前压（Homing 的反面）。
            bool guard = evt.Return;
            float ringRadius = guard ? SummonGuardRingRadius : SummonMusterRingRadius;

            // SpreadAngle 扇角 → 出生阵型从整圈收成一个扇面（配合空投 = 一次投一排）。
            float spreadRad = evt.SpreadAngle > 0f && evt.SpreadAngle < 360f
                ? math.radians(evt.SpreadAngle)
                : 2f * math.PI;
            float aimAngle = math.atan2(aim.y, aim.x);

            float2? homingTarget = evt.Homing > 0f && !guard ? FindNearestHostile(origin) : null;
            var spawned = new List<float2>(granted);
            for (int s = 0; s < granted; s++)
            {
                float angle;
                if (spreadRad >= 2f * math.PI - 1e-3f)
                {
                    angle = 2f * math.PI * s / granted;
                }
                else if (granted > 1)
                {
                    angle = aimAngle + ((float)s / (granted - 1) - 0.5f) * spreadRad;
                }
                else
                {
                    // 只召一只时扇角仍要有意义：给一个**确定性**的落位抖动，
                    // 与 CombatBallistics.FanDirection 对单发弹的处理同一套路
                    // （否则"扇角"就成了只有多发才生效的死参数）。
                    uint h = (uint)evt.SummonId * 2654435761u + 0x9E3779B9u;
                    h ^= h >> 15;
                    angle = aimAngle + ((h & 0xFFFFu) / 65535f - 0.5f) * spreadRad;
                }
                float2 offset = new float2(math.cos(angle), math.sin(angle)) * ringRadius;
                float2 spawnPos = muster + offset;
                if (homingTarget.HasValue)
                {
                    spawnPos = math.lerp(spawnPos, homingTarget.Value, evt.Homing * 0.5f);
                }
                spawned.Add(spawnPos);
                int logicId = _sim.NextLogicId();
                float minionSpeed = summonSpeed * (1f + math.saturate(evt.Homing) * 0.5f);
                _sim.Spawn(new BinGames.Sim.SpawnRequest
                {
                    Position = spawnPos,
                    Velocity = float2.zero,
                    Health = summonHealth,
                    Radius = summonRadius,
                    // Homing 在召唤底盘的另一半读法：追踪强度 → 小弟更主动（跑得更急）。
                    MaxSpeed = minionSpeed,
                    ArchetypeId = evt.SummonId,
                    Faction = BinGames.Sim.SimFaction.PlayerMinion,
                    LogicId = logicId,
                    VisualId = evt.SummonId,
                    Generation = 0,
                });

                // SplitOnHit 分裂 → **死了会裂开**。登记这一只的分裂预算，
                // 死亡事件回来时凭 LogicId 认领（见 TickMinionSplit）。
                if (evt.SplitOnHit > 0f)
                {
                    _minionSplitBudget[logicId] = new MinionSplitBudget
                    {
                        Generations = Math.Min(MinionSplitMaxGenerations,
                            Math.Max(1, (int)MathF.Round(evt.SplitOnHit))),
                        Generation = 0,
                        Fanout = MinionSplitFanout,
                        ArchetypeId = evt.SummonId,
                        Speed = minionSpeed,
                        Health = summonHealth,
                        Radius = summonRadius,
                    };
                }
            }

            // Bounce 反弹 → **落地冲击**：小弟出场时把周围的敌人弹开，给它们腾出站位。
            float summonKnock = MathF.Max(0f, evt.Bounce) * MeleeKnockbackPerBounce
                + MathF.Max(0f, evt.Knockback) * MeleeKnockbackPerForce;
            if (summonKnock > 0f)
            {
                for (int s = 0; s < spawned.Count; s++)
                {
                    LastKnockbackUnitsMoved = _sim.Knockback(spawned[s], summonRadius * 4f, summonKnock);
                }
                LastMeleeKnockbackDistance = summonKnock;
                _knockbackHandledThisCast = true;
            }

            // Linger 留坑 / Trail 拖尾 → 小弟脚下开始渗，**这一拍就渗**。
            //
            // 不能等 ApplySummonTrail 去扫快照：新生成的单位要到下一次 Step 才进快照，
            // 于是"召唤 + 毒坑"这个 build 的第一拍永远是空的（实测：Summon 底盘上
            // gene_tide / 四条膜基因 / gene_pyro / gene_slime 全都毫无反应）。
            // 这里直接用刚才记下的出生坐标铺，与 ApplySummonTrail 覆盖的"已存活小弟"不重叠。
            // Weave 编织在这里顺带把相邻小弟的坑连成菌网。
            if (evt.Linger > 0f || evt.Trail > 0f)
            {
                bool hasLinger = evt.Linger > 0f;
                float zoneSeconds = hasLinger ? evt.Linger : _attackInterval;
                float zonePerTick = hasLinger ? evt.Damage * 0.5f : evt.Trail;
                for (int s = 0; s < spawned.Count; s++)
                {
                    SpawnLingerZone(spawned[s],
                        MathF.Max(summonRadius * 2f, CombatBallistics.LingerMinRadius),
                        zoneSeconds, zonePerTick, evt.TickRate,
                        hasLinger ? evt.Chain : 0f, hasLinger ? evt.Pull : 0f,
                        growthRate: evt.GrowthRate, weaveRadius: evt.Weave,
                        tint: ResolveProjectileTint(evt));
                }
            }

            LastAbilityPrompt = $"召唤 {granted} 个随行单位（ArchetypeId={evt.SummonId}，" +
                $"速度 {summonSpeed:0.#} 生命 {summonHealth:0.#} 体型 {summonRadius:0.##}" +
                (guard ? "，护卫阵" : "") + (evt.Gravity > 0f ? "，空投" : "") + "）";
            TEngine.Log.Info($"[MetabolicSliceBridge] {LastAbilityPrompt}");
            return true;
        }

        /// <summary>organ-gene-rebalance-v3 story-006（EMERGENCE §2 Summon 列「Trail/燃径/粘液：迹跟随」）：
        /// 只读 evt.SummonId&gt;0（"这是一条召唤链路"的字段判据，非 organId）+ evt.Trail 字段——任意存活
        /// 玩家召唤物脚下补一个短时小额跳伤区，复用 <see cref="_pendingLinger"/> 同一出口，不新增专属状态机。</summary>
        private void ApplySummonTrail(HitEvent evt)
        {
            if (_sim == null || !_sim.Running)
            {
                return;
            }

            float radius = DamageAreaRadius * 0.5f * MathF.Max(0.1f, evt.Scale);
            // chassis-native-primitives：Linger 在召唤底盘的读法 = "小弟脚下持续渗"。
            // 此前 Summon 分支根本不看 Linger，于是 gene_tide / 四条膜基因挂在 org_bud 上
            // 全部静默失效——毒系召唤这条 build 在实现层面根本不存在。
            bool hasLinger = evt.Linger > 0f;
            float seconds = hasLinger ? evt.Linger : _attackInterval;
            float perTick = hasLinger ? evt.Damage * 0.5f : evt.Trail;
            float interval = evt.TickRate > 0f ? 1f / evt.TickRate : DefaultLingerTickInterval;

            BinGames.Sim.SimSnapshot snap = _sim.Snapshot;
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] == 0 || snap.Faction[i] != (byte)BinGames.Sim.SimFaction.PlayerMinion)
                {
                    continue;
                }
                _pendingLinger.Add(new PendingLinger
                {
                    Position = snap.Position[i],
                    Radius = radius,
                    DamagePerTick = perTick,
                    Interval = interval,
                    NextTick = 0f,
                    TimeLeft = seconds,
                    Chain = hasLinger ? evt.Chain : 0f,
                    Pull = hasLinger ? evt.Pull : 0f,
                    GrowthRate = evt.GrowthRate,
                });
            }
        }

        /// <summary>story-006 Required 3（gene_swarm 最小实现）：让每个存活玩家召唤物在自己坐标补一次
        /// 宿主器官这次命中的同 Damage/Chain/Pull 结算——"召唤物学会这件器官的打法"的最小可读版本，
        /// 复用 <see cref="DamageAreaPrimitive"/> 同一出口，不新起召唤物专属的攻击模式系统。只在
        /// <see cref="ApplyEvent"/> 的 Tick 节奏（或 execute_code 直调）触发，不是每帧扫描（同
        /// <see cref="FindNearestHostile"/> 先例，召唤物数量恒被 MinionCap 卡在个位数）。</summary>
        private void ApplySwarmInherit(HitEvent evt)
        {
            if (_sim == null || !_sim.Running)
            {
                return;
            }

            float radius = DamageAreaRadius * MathF.Max(0.1f, evt.Scale);
            BinGames.Sim.SimSnapshot snap = _sim.Snapshot;
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] == 0 || snap.Faction[i] != (byte)BinGames.Sim.SimFaction.PlayerMinion)
                {
                    continue;
                }
                DamageAreaPrimitive(snap.Position[i], radius, evt.Damage, evt);
            }
        }

        /// <summary>story-002：任意命中的统一出口——Chain 原样传给内核已实现的连锁命中（JobDamage.Chain），
        /// Pull 用内核已实现的 Slowed 减速（JobIntegrate 的 SlowMul）模拟"被拖拽锚定"，都不是新起模拟，
        /// 只是把此前恒 0/未接线的参数真正传下去。</summary>
        private void DamageAreaPrimitive(float2 pos, float radius, float amount, float chain, float pull,
            int sourceLogicId = 0)
        {
            int chainCount = chain > 0f ? Math.Max(0, (int)MathF.Round(chain)) : 0;
            _sim.DamageArea(pos, radius, amount, BinGames.Sim.SimFaction.Hostile,
                chainCount: chainCount, sourceLogicId: sourceLogicId);
            if (pull > 0f)
            {
                _sim.ApplyStatusArea(pos, radius, BinGames.Sim.SimStatus.Slowed | BinGames.Sim.SimStatus.Pulled,
                    true, BinGames.Sim.SimFaction.Hostile);
            }
        }

        private void DamageAreaPrimitive(float2 pos, float radius, float amount, HitEvent evt) =>
            DamageAreaPrimitive(pos, radius, amount, evt.Chain, evt.Pull);

        /// <summary>
        /// 这个事件有没有**轨迹**语义（需要一个真的会飞的弹体）。
        ///
        /// combat-primitive-overhaul：把 Linger/Trail 从这个判据里拿掉了。它们是**载荷**基元
        /// （"命中之后留下什么"），不是轨迹基元（"怎么飞过去"）——同一条 Linger 挂在弹道上是
        /// "弹落地留坑"，挂在近战上是"挥完脚下留一片"，挂在光环上是"站过的地方有残留"，
        /// 三者都合理，但都不该因此把底盘变成弹道。
        /// 旧判据把它们算进来，导致 org_enzyme（酶雾，只有 Linger）被当成弹道走落点结算，
        /// 圈直接跑到 9 米外去了。
        /// </summary>
        private static bool HasBallisticPrimitives(HitEvent evt) =>
            evt.Homing > 0f || evt.Pierce > 0f || evt.Bounce > 0f || evt.Return ||
            evt.SplitOnHit > 0f || evt.Speed > 0f || evt.Lifetime > 0f || evt.Gravity > 0f;

        /// <summary>story-002：最近敌对单位查找，供 Homing 一次性偏移落点用。只在生成 PendingImpact 时调用
        /// 一次（每次开火，不在 Tick 的每帧循环内），不违反热更层"每帧不得 O(敌人数)"红线（与既有
        /// <see cref="BinGames.Sim.SimSnapshot.CountHostiles"/> 同类用法先例一致）。</summary>
        /// <summary>
        /// chassis-native-primitives：带搜敌半径的最近敌人查询。
        ///
        /// <see cref="FindNearestHostile"/> 是**全场**扫描（没有射程概念），近战软锁定不能用它——
        /// 否则一挥刀就转向半张地图外的敌人。同 <see cref="CombatBallistics.HomingRange"/> 的道理：
        /// 够不着的目标不该影响你的朝向。
        /// </summary>
        private float2? FindNearestHostileWithin(float2 from, float range)
        {
            float2? nearest = FindNearestHostile(from);
            if (!nearest.HasValue)
            {
                return null;
            }
            return math.distancesq(nearest.Value, from) <= range * range ? nearest : null;
        }

        private float2? FindNearestHostile(float2 from)
        {
            if (_sim == null || !_sim.Running)
            {
                return null;
            }

            BinGames.Sim.SimSnapshot snap = _sim.Snapshot;
            float bestSq = float.MaxValue;
            float2 best = default;
            bool found = false;
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] == 0 || snap.Faction[i] != (byte)BinGames.Sim.SimFaction.Hostile)
                {
                    continue;
                }
                float d = math.distancesq(snap.Position[i], from);
                if (d < bestSq)
                {
                    bestSq = d;
                    best = snap.Position[i];
                    found = true;
                }
            }
            return found ? (float2?)best : null;
        }

        /// <summary>story-002：SpreadAngle 收窄的锥形扇散——与 <see cref="MeleeFanDirection"/> 同一公式，
        /// 参数化成通用角度供 Bolt-tail 多发复用，不新起一套系数。</summary>
        public static float2 ConeFanDirection(float2 baseDir, int index, int count, float angleDegrees)
        {
            float2 n = math.normalizesafe(baseDir, DefaultForward);
            if (count <= 1)
            {
                return n;
            }
            float halfRad = angleDegrees * math.PI / 180f * 0.5f;
            float t = (float)index / (count - 1);
            float angle = math.lerp(-halfRad, halfRad, t);
            float cos = math.cos(angle);
            float sin = math.sin(angle);
            return new float2(n.x * cos - n.y * sin, n.x * sin + n.y * cos);
        }

        /// <summary>story-006：Count 多发/Explode 落点方向扇形展开，与
        /// <see cref="GameLogic.Battle.Feedback.WhiteboxComposeProjectileFeedback"/> 视觉飞行用同一公式
        /// （禁止另起系数分叉，比照 D3/D6 先例）。count&lt;=1 时原样返回归一化后的 baseDir。</summary>
        public static float2 FanDirection(float2 baseDir, int index, int count)
        {
            float2 n = math.normalizesafe(baseDir, DefaultForward);
            if (count <= 1)
            {
                return n;
            }
            float angle = 2f * math.PI * index / count;
            float cos = math.cos(angle);
            float sin = math.sin(angle);
            return new float2(n.x * cos - n.y * sin, n.x * sin + n.y * cos);
        }

        /// <summary>story-007 R6：近战前方扇形展开——只在 ±<see cref="FxRecipeCatalog.Global"/>.ArcHalfAngleDeg
        /// 范围内分布（复用该已有全局系数，不新增系数），与 <see cref="FanDirection"/>（全向散射，Bolt/AOE 多发用）
        /// 不同。count&lt;=1 时原样返回归一化后的 baseDir（居中不偏转，对应"多数近战器官 hits=1"的常见情形）。</summary>
        public static float2 MeleeFanDirection(float2 baseDir, int index, int count)
        {
            float2 n = math.normalizesafe(baseDir, DefaultForward);
            if (count <= 1)
            {
                return n;
            }
            float halfRad = FxRecipeCatalog.Global.ArcHalfAngleDeg * math.PI / 180f;
            float t = (float)index / (count - 1);
            float angle = math.lerp(-halfRad, halfRad, t);
            float cos = math.cos(angle);
            float sin = math.sin(angle);
            return new float2(n.x * cos - n.y * sin, n.x * sin + n.y * cos);
        }

        /// <summary>
        /// 轴 A 落地：反应结果若带 Payload["LeaveResidue"]，把 OnHit 触发的残留真正写进 <see cref="_environment"/>
        /// （此前只有 <see cref="WorldEnvironment.ResolveHit"/> 这条独立辅助方法会做，Bridge 从未调用它，等于反应
        /// 从未真正在局内落地）。命中新残留 tag 时刷新 <see cref="LastEnvironmentPrompt"/>，给 HUD 用白话展示。
        /// </summary>
        private void DepositResidue(HitEvent evt)
        {
            if (!evt.Payload.TryGetValue("LeaveResidue", out var raw) || !(raw is List<ResidueDeposit> deposits))
            {
                return;
            }
            foreach (ResidueDeposit deposit in deposits)
            {
                if (deposit.Trigger != ResidueTrigger.OnHit)
                {
                    continue;
                }
                bool isNew = !_environment.GetTags(ArenaCellId).Contains(deposit.Tag);
                _environment.AddResidue(ArenaCellId, deposit.Tag, deposit.Amount, deposit.Ttl);
                if (isNew)
                {
                    LastEnvironmentPrompt = $"地上起反应了：新增残留「{DisplayTag(deposit.Tag)}」";
                    TEngine.Log.Info($"[MetabolicSliceBridge] 轴A 残留反应：{deposit.Tag}（ttl={deposit.Ttl}）");
                }
            }
        }

        /// <summary>story-004：只在沙盒态（<see cref="Suppressed"/>）推进——真实战斗不产生沙盒 DPS 数值。</summary>
        private void TickSandboxCombat(float dt)
        {
            _sandboxClock += dt;
            if (_sandboxHasFired)
            {
                _sandboxElapsedSinceFirstHit += dt;
            }

            float cutoff = _sandboxClock - SandboxRollingWindowSeconds;
            while (_sandboxRecentHits.Count > 0 && _sandboxRecentHits.Peek().clock < cutoff)
            {
                _sandboxRecentHits.Dequeue();
            }
        }

        private void OnSandboxHit(HitSignal s)
        {
            if (!Suppressed)
            {
                return;
            }

            _sandboxHasFired = true;
            _sandboxHitCount++;
            _sandboxTotalDamage += s.Damage;
            _sandboxRecentHits.Enqueue((_sandboxClock, s.Damage));
        }

        private void OnSandboxKill(KillSignal s)
        {
            if (!Suppressed)
            {
                return;
            }

            _sandboxKillCount++;
        }

        public override void OnExit()
        {
            _engine = null;
            _runner = null;
            _environment = null;
            _pendingMotion?.Clear();
            _pendingLinger?.Clear();
            _sandboxCombatScope?.Dispose();
            _sandboxCombatScope = null;
            _sandboxRecentHits.Clear();
        }
    }
}
