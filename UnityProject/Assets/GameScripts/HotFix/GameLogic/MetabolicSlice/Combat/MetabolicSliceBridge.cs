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
    /// +日志，Displace 复用已有的 <see cref="BinGames.Sim.SimWorld.SetPlayerPosition"/>（与
    /// `EffectDash` 同一模式）直接挪玩家坐标，不新增 AOT 内核结构。
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

        private const float TickInterval = 1.5f;

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
        }

        /// <summary>story-002：Linger 留坑的最小 ephemeral 状态——命中点持续按 TickRate（或默认间隔）
        /// 周期性结算范围伤害，直到秒数耗尽，不受 <see cref="TickInterval"/> 节流。</summary>
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
        }

        private Engine _engine;
        private MetabolicSliceRunner _runner;
        private SimBridge _sim;
        private StatSheet _stats;
        private AbilitySystem _abilities;
        private WorldEnvironment _environment;
        private readonly List<PendingMotionHit> _pendingMotion = new List<PendingMotionHit>();
        private readonly List<PendingLinger> _pendingLinger = new List<PendingLinger>();
        private float _timer;
        private int _seed;
        private float _playerShield;

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
            if (_timer < TickInterval)
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

            int consumed = 0;
            for (int i = 0; i < events.Count; i++)
            {
                HitEvent evt = events[i];
                DepositResidue(evt);
                if (ApplyEvent(evt))
                {
                    consumed++;
                }
            }
            _environment.Tick(1);

            TEngine.Log.Info($"[MetabolicSliceBridge] Tick 产出 {events.Count} 个 HitEvent，已应用 {consumed} 个");
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
                // 光环：贴身持续圈，没有"飞出去"的语义。叠 Linger 时把它变成一个真的持续区域
                // （此前 Aura 叠任何基因字段都会掉进落点结算，圈跑到 9 米外去了）。
                DamageAreaPrimitive(origin, radius, evt.Damage, evt);
                if (evt.Linger > 0f)
                {
                    SpawnLingerZone(origin, MathF.Max(radius, CombatBallistics.LingerMinRadius), evt.Linger,
                        evt.Damage * 0.5f, evt.TickRate, evt.Chain, evt.Pull);
                }
                return;
            }

            // ── Field（无轨迹基元）：朝瞄准方向布场 ──────────────────────────
            // "把酶雾扔到落点"——布场点是一个纯常量偏移，没有任何飞行动力学，因此不存在
            // "预测点与真实点分叉"的问题（那是弹道才有的病）。落点算一次、存进 LastDeployPos，
            // 表现层直接读这个坐标，不自己再乘一遍系数。
            float2 deployDir = math.normalizesafe(baseDir, DefaultForward);
            float2 deployPos = origin + deployDir * CombatBallistics.FieldThrowRange;
            LastDeployPos = deployPos;

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
                SpawnLingerZone(deployPos, MathF.Max(radius, CombatBallistics.LingerMinRadius), evt.Linger,
                    evt.Damage * 0.5f, evt.TickRate, evt.Chain, evt.Pull);
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
            float reach = CombatBallistics.MeleeReach * scale;
            float halfAngle = CombatBallistics.MeleeHalfAngle(evt);
            float2 dir = math.normalizesafe(baseDir, DefaultForward);
            float2 coneOrigin = origin + dir * MeleeFrontOffset;

            LastMeleeStrikeRadius = reach;
            LastMeleeConeHalfAngleDeg = halfAngle;
            _lastMeleeStrikeOrigins.Clear();
            _lastMeleeStrikeOrigins.Add(coneOrigin);

            int chainCount = evt.Chain > 0f ? Math.Max(0, (int)MathF.Round(evt.Chain)) : 0;
            for (int h = 0; h < hits; h++)
            {
                _sim.DamageCone(coneOrigin, reach, dir, halfAngle, evt.Damage,
                    BinGames.Sim.SimFaction.Hostile, chainCount: chainCount,
                    nearRadius: CombatBallistics.MeleeNearRadius);
            }

            if (evt.Pull > 0f)
            {
                _sim.ApplyStatusArea(coneOrigin, reach,
                    BinGames.Sim.SimStatus.Slowed | BinGames.Sim.SimStatus.Pulled,
                    true, BinGames.Sim.SimFaction.Hostile);
            }

            if (evt.ExplodeOnHit)
            {
                // 爆是全向的，不吃扇形筛选——"炸开"本来就没有面朝方向。
                DamageAreaPrimitive(coneOrigin, reach * 0.8f, evt.Damage * ExplodeDamageMult, evt);
            }

            if (evt.Trail > 0f || evt.Linger > 0f)
            {
                // 近战没有飞行路径，Trail 与 Linger 在语义上合流成"挥完在脚下留一片"。
                float seconds = evt.Linger > 0f ? evt.Linger : TickInterval;
                float perTick = evt.Linger > 0f ? evt.Damage * 0.5f : evt.Trail;
                SpawnLingerZone(coneOrigin, MathF.Max(reach * 0.6f, CombatBallistics.LingerMinRadius),
                    seconds, perTick, evt.TickRate, evt.Chain, evt.Pull);
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
                bool orbitAroundSelf = (evt.Spin != 0f || evt.Orbit != 0f)
                    && chassis != ChassisClass.Projectile
                    && !(chassis == ChassisClass.Field && HasBallisticPrimitives(evt));

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

                if (evt.Trail > 0f)
                {
                    // story-006（EMERGENCE §2 Summon 列「迹跟随」）：只要这条链路带 SummonId，就按 Trail
                    // 字段生效，与本次是否新召唤成功（MinionCap 已满时 ApplySummon 可能返回 false）无关——
                    // 已存活的召唤物同样应该"跟随留迹"。
                    ApplySummonTrail(evt);
                    applied = true;
                }
            }

            if (evt.Knockback > 0f)
            {
                // 击退的是命中目标而非玩家自身；Sim 无"位移单位"公共 API（只有 SetPlayerPosition 位移玩家），
                // 不新增 Sim 签名，先记可读摘要（与下方 Damage<=0 时 Spin/Orbit 的 stub 同一先例）。
                LastAbilityPrompt = $"击退（宿主暂无单位位移 API，仅记录）：Knockback={evt.Knockback:0.#}";
                TEngine.Log.Info($"[MetabolicSliceBridge] {LastAbilityPrompt}");
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
                _sim.World.SetPlayerPosition(target);
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
        /// story-003：每帧推进挂起的 Spin/Orbit 延迟命中（不受 <see cref="TickInterval"/> 节流，运动必须每帧可见）。
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

                if (e.LingerSeconds > 0f)
                {
                    SpawnLingerZone(e.Position, MathF.Max(e.LingerRadius, CombatBallistics.LingerMinRadius),
                        e.LingerSeconds, e.Damage * 0.5f,
                        hasMeta ? meta.TickRate : 0f,
                        hasMeta ? meta.Chain : 0f,
                        hasMeta ? meta.Pull : 0f);

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
            float tickRate, float chain, float pull)
        {
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
            });

            Signals.Publish(new ComposeChainSignal
            {
                Kind = "Linger", Position = pos, Direction = new float2(0f, 1f),
                Radius = radius, Duration = seconds,
            });
        }

        /// <summary>story-002：Linger 留坑周期结算，独立于 <see cref="TickInterval"/>（DoT 不该被 1.5s 节流卡住）。</summary>
        private void TickPendingLinger(float dt)
        {
            for (int i = _pendingLinger.Count - 1; i >= 0; i--)
            {
                PendingLinger p = _pendingLinger[i];
                p.TimeLeft -= dt;
                p.NextTick -= dt;
                if (p.NextTick <= 0f)
                {
                    DamageAreaPrimitive(p.Position, p.Radius, p.DamagePerTick, p.Chain, p.Pull);
                    p.NextTick = p.Interval;
                }

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

        /// <summary>story-002：召唤——经现有 Minion 管线（<see cref="MinionRegistry"/> 配额 + <see cref="SimBridge.Spawn"/>），
        /// 与 <see cref="GameLogic.Ability.Executors.EffectSpawn"/> 同一生成方式，不新增 Sim 公共方法签名。</summary>
        private bool ApplySummon(HitEvent evt)
        {
            if (_sim?.World == null)
            {
                return false;
            }

            int requested = Math.Max(1, (int)MathF.Round(evt.SummonCount));
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
            float2? homingTarget = evt.Homing > 0f ? FindNearestHostile(origin) : null;
            for (int s = 0; s < granted; s++)
            {
                float angle = 2f * math.PI * s / granted;
                float2 offset = new float2(math.cos(angle), math.sin(angle)) * 1.2f;
                float2 spawnPos = origin + offset;
                if (homingTarget.HasValue)
                {
                    spawnPos = math.lerp(spawnPos, homingTarget.Value, evt.Homing * 0.5f);
                }
                _sim.Spawn(new BinGames.Sim.SpawnRequest
                {
                    Position = spawnPos,
                    Velocity = float2.zero,
                    Health = 1f,
                    Radius = 0.4f,
                    MaxSpeed = 4f,
                    ArchetypeId = evt.SummonId,
                    Faction = BinGames.Sim.SimFaction.PlayerMinion,
                    LogicId = _sim.NextLogicId(),
                    VisualId = evt.SummonId,
                });
            }

            LastAbilityPrompt = $"召唤 {granted} 个随行单位（ArchetypeId={evt.SummonId}）";
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
                    DamagePerTick = evt.Trail,
                    Interval = DefaultLingerTickInterval,
                    NextTick = 0f,
                    TimeLeft = TickInterval,
                    Chain = 0f,
                    Pull = 0f,
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
        private void DamageAreaPrimitive(float2 pos, float radius, float amount, float chain, float pull)
        {
            int chainCount = chain > 0f ? Math.Max(0, (int)MathF.Round(chain)) : 0;
            _sim.DamageArea(pos, radius, amount, BinGames.Sim.SimFaction.Hostile, chainCount: chainCount);
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
