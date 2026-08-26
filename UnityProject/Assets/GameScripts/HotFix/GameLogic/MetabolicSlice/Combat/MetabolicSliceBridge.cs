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

        /// <summary>story-006：Count 多发/Explode 落点结算的飞行距离，白模视觉飞行终点与判定落点必须用同一个数，
        /// 禁止另起系数（比照 D3/D6 先例）。取值对齐既有 Bolt 视觉的 BoltFlightDistance=9f，不改变已调好的观感尺度。</summary>
        public const float ImpactFlightDistance = 9f;

        /// <summary>story-007 R6：近战前方扇形结算的前移距离，判定与白模视觉共用同一个数
        /// （<see cref="GameLogic.Battle.Feedback.WhiteboxComposeProjectileFeedback"/> 的 MeleeMuzzleOffset
        /// 直接引用本常量，禁止另起系数，比照 D3/D6/BoltMuzzleOffset 先例）。小于 DamageAreaRadius，
        /// hits=1（多数近战器官的常见配置）时仍与玩家本体明显重叠，只是圆心整体前移出方向感。</summary>
        public const float MeleeFrontOffset = 2f;

        /// <summary>story-002：Pierce/Bounce 追加命中点沿方向前进的步长，复用命中半径量级，不另起系数分叉。</summary>
        public const float PrimitiveStepDistance = DamageAreaRadius * 1.5f;
        /// <summary>story-002：Pierce/Bounce/Split 追加命中的近似瞬时结算时长（非 0，避免同帧内对同一 List 重入修改）。</summary>
        private const float SecondaryImpactDelay = 0.05f;
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

        /// <summary>story-006：Count 多发/Explode 延迟落点结算的最小 ephemeral 状态（复用 PendingMotionHit
        /// 同一形状扩展——Origin+TimeLeft 语义换成"沿方向飞行的落点"，见 R5）。
        /// story-002：追加 Homing/Pierce/Bounce/Chain/Return/Linger/Pull/SplitOnHit/Trail/TickRate——
        /// 只在 Bolt-tail 落点结算路径生效（与既有 Count/Explode 同一先例：Melee-tail 方向留给 007），
        /// 二级命中（Pierce/Bounce/Split 追加的落点）不再携带 Pierce/Bounce/Split/Trail/Return/Linger，
        /// 避免同一发子弹递归放大出无界的追加命中。</summary>
        private struct PendingImpact
        {
            public float2 ImpactPos;
            public float Radius;
            public float Damage;
            public float TimeLeft;
            public float Duration;
            public float2 Origin;
            public float2 Direction;
            public float Chain;
            public float Pull;
            public int PierceLeft;
            public int BounceLeft;
            public float Trail;
            public bool TrailFired;
            public int SplitOnHit;
            public bool ReturnPending;
            public float Linger;
            public float TickRate;
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
        private readonly List<PendingImpact> _pendingImpact = new List<PendingImpact>();
        private readonly List<PendingLinger> _pendingLinger = new List<PendingLinger>();
        private float _timer;
        private int _seed;
        private float _playerShield;

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

        /// <summary>story-006：当前挂起的 Count 多发/Explode 延迟落点数量，供 execute_code 断言"生成即挂起、tick 完即清空"。</summary>
        public int PendingImpactCount => _pendingImpact?.Count ?? 0;

        /// <summary>story-002：当前挂起的 Linger 留坑数量，供 execute_code 断言。</summary>
        public int PendingLingerCount => _pendingLinger?.Count ?? 0;

        /// <summary>story-006 验收探针：最近一次延迟落点结算的世界坐标，供断言 impactPos != PlayerPosition。</summary>
        public float2 LastImpactPos { get; private set; }

        /// <summary>story-007 验收探针：最近一次 Melee 前方扇形展开产出的全部命中圆心世界坐标（含 hits&gt;1 的
        /// 全部圆），供 execute_code 断言"前方判定生效"（圆心随 AimDirection 偏移，不再恒等于 PlayerPosition）与
        /// "非目标不受击"（给定点到每个圆心的距离 &gt; 半径即不受本次事件影响）。每次 Melee-tail 结算整体替换。</summary>
        public IReadOnlyList<float2> LastMeleeStrikeOrigins => _lastMeleeStrikeOrigins;

        /// <summary>与 <see cref="LastMeleeStrikeOrigins"/> 同批命中圆的公共半径（已按 evt.Scale 缩放）。</summary>
        public float LastMeleeStrikeRadius { get; private set; }

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
            TickPendingImpact(dt);
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
        /// story-006：EMERGENCE §2 Fallback 矩阵落地——Homing/Pierce/Bounce/Return/Trail/SplitOnHit/Linger
        /// 这组"延迟落点"字段在 Projectile/Melee/Field/Aura 四类底盘上语义一致（弹道弯 vs 近战扑 vs 坑
        /// drift vs 持续圈偏移，皆是"落点朝最近敌人挪一截再结算"的同一套数学），差异只在"初始落点怎么摆"
        /// （飞多远、朝几个方向散、半径来源哪个字段）——因此统一走 <see cref="_pendingImpact"/> 复用
        /// <see cref="TickPendingImpact"/> 已有的 Pierce/Bounce/SplitOnHit/Return/Trail/Linger 结算，不按
        /// 底盘各写一份重复实现（也是 EMERGENCE §5"禁止 is/as 具体类型""禁止组合技分支"的同一精神：这里
        /// 禁止的是"每个底盘重复分支硬编码"，判据仍只读 Pattern + 字段）。没有任何这些字段时
        /// （<see cref="HasBallisticPrimitives"/> 为假）各底盘退回各自原有的即时结算，不改变已验证过的
        /// 基线手感——这也是 org_enzyme（Field）之前从不落地 Linger 坑、org_osmotic（Aura）叠加基因字段
        /// 全部失效的根因（Shape 只有 "Bolt"/"Melee" 两条分支能进落点结算）。Summon 底盘不参与本几何重构，
        /// 按 EMERGENCE §2「Summon 无关基因」行维持原地命中，其 Homing/Trail 专属反馈见
        /// <see cref="ApplySummon"/>/<see cref="ApplySummonTrail"/>。
        /// </summary>
        private void ApplyChassisDamage(HitEvent evt, ChassisClass chassis, int hits, float radius, float2 baseDir)
        {
            float2 origin = _sim.PlayerPosition;

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

            if (chassis == ChassisClass.Legacy)
            {
                if (evt.Shape == "Melee")
                {
                    _lastMeleeStrikeOrigins.Clear();
                    LastMeleeStrikeRadius = radius;
                    for (int h = 0; h < hits; h++)
                    {
                        float2 dir = MeleeFanDirection(baseDir, h, hits);
                        float2 strikeOrigin = origin + dir * MeleeFrontOffset;
                        _lastMeleeStrikeOrigins.Add(strikeOrigin);
                        DamageAreaPrimitive(strikeOrigin, radius, evt.Damage, evt);
                    }
                }
                else
                {
                    for (int h = 0; h < hits; h++)
                    {
                        DamageAreaPrimitive(origin, radius, evt.Damage, evt);
                    }
                }
                if (evt.ExplodeOnHit)
                {
                    DamageAreaPrimitive(origin, radius * ExplodeRadiusMult, evt.Damage * ExplodeDamageMult, evt);
                }
                return;
            }

            bool ballistic = HasBallisticPrimitives(evt);

            if (chassis == ChassisClass.Melee && !ballistic)
            {
                _lastMeleeStrikeOrigins.Clear();
                LastMeleeStrikeRadius = radius;
                for (int h = 0; h < hits; h++)
                {
                    float2 dir = MeleeFanDirection(baseDir, h, hits);
                    float2 strikeOrigin = origin + dir * MeleeFrontOffset;
                    _lastMeleeStrikeOrigins.Add(strikeOrigin);
                    DamageAreaPrimitive(strikeOrigin, radius, evt.Damage, evt);
                }
                if (evt.ExplodeOnHit)
                {
                    DamageAreaPrimitive(origin, radius * ExplodeRadiusMult, evt.Damage * ExplodeDamageMult, evt);
                }
                return;
            }

            if (chassis == ChassisClass.Projectile && !ballistic)
            {
                // 真实 org_emitter 自带 BallisticsModule（Speed>0）恒为 ballistic=true，不会走这条分支；
                // 这里只是给"没有任何弹道基元字段的合成 Projectile 事件"（如手搭的 execute_code 探针）
                // 保留原有即时结算，避免平白多一帧延迟。
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

            if (chassis == ChassisClass.Aura && !ballistic)
            {
                DamageAreaPrimitive(origin, radius, evt.Damage, evt);
                return;
            }

            if (chassis == ChassisClass.Field && !ballistic)
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

            // ── Projectile 恒走这条路（org_emitter 自带 Speed>0）；Melee/Field/Aura 只在叠了
            // Homing/Pierce/Bounce/Return/Trail/SplitOnHit/Linger/Speed/Lifetime/Gravity 任一字段时才走 ──
            float flightDistance = chassis switch
            {
                ChassisClass.Melee => MeleeFrontOffset,
                ChassisClass.Aura => 0f,
                _ => ImpactFlightDistance,
            };
            float duration = evt.Speed > 0f
                ? ComposeMotionMath.MotionFlightDuration / MathF.Max(0.1f, evt.Speed)
                : ComposeMotionMath.MotionFlightDuration;
            if (evt.Gravity > 0f)
            {
                flightDistance /= 1f + evt.Gravity;
            }
            if (evt.Lifetime > 0f && evt.Lifetime < duration)
            {
                flightDistance *= evt.Lifetime / duration;
                duration = evt.Lifetime;
            }
            duration = MathF.Max(duration, SecondaryImpactDelay);

            if (chassis == ChassisClass.Melee)
            {
                _lastMeleeStrikeOrigins.Clear();
                LastMeleeStrikeRadius = radius;
            }

            for (int h = 0; h < hits; h++)
            {
                float2 dir = chassis == ChassisClass.Melee
                    ? MeleeFanDirection(baseDir, h, hits)
                    : (evt.SpreadAngle > 0f ? ConeFanDirection(baseDir, h, hits, evt.SpreadAngle) : FanDirection(baseDir, h, hits));
                float2 impactPos = origin + dir * flightDistance;
                if (evt.Homing > 0f)
                {
                    float2? nearest = FindNearestHostile(origin);
                    if (nearest.HasValue)
                    {
                        impactPos = math.lerp(impactPos, nearest.Value, evt.Homing);
                    }
                }
                if (chassis == ChassisClass.Melee)
                {
                    _lastMeleeStrikeOrigins.Add(impactPos);
                }
                _pendingImpact.Add(new PendingImpact
                {
                    ImpactPos = impactPos,
                    Radius = radius,
                    Damage = evt.Damage,
                    TimeLeft = duration,
                    Duration = duration,
                    Origin = origin,
                    Direction = dir,
                    Chain = evt.Chain,
                    Pull = evt.Pull,
                    PierceLeft = evt.Pierce > 0f ? Math.Max(0, (int)MathF.Round(evt.Pierce)) : 0,
                    BounceLeft = evt.Bounce > 0f ? Math.Max(0, (int)MathF.Round(evt.Bounce)) : 0,
                    Trail = evt.Trail,
                    SplitOnHit = evt.SplitOnHit > 0f ? Math.Max(0, (int)MathF.Round(evt.SplitOnHit)) : 0,
                    ReturnPending = evt.Return,
                    Linger = evt.Linger,
                    TickRate = evt.TickRate,
                });
            }

            if (evt.ExplodeOnHit)
            {
                _pendingImpact.Add(new PendingImpact
                {
                    ImpactPos = origin + baseDir * flightDistance,
                    Radius = radius * ExplodeRadiusMult,
                    Damage = evt.Damage * ExplodeDamageMult,
                    TimeLeft = duration,
                    Duration = duration,
                    Origin = origin,
                    Direction = baseDir,
                    Chain = evt.Chain,
                    Pull = evt.Pull,
                });
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

                if (evt.Spin != 0f || evt.Orbit != 0f)
                {
                    // story-003：Spin/Orbit 命中改延迟到期采样点，而不是原地瞬时突发（D2/D4）——底盘无关，
                    // EMERGENCE §2「Orbit/鞭毛绕」行对 5 类底盘都定义了反馈，运动机制处处生效。
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
                // story-002：组合出口形态信号，给表现层一个稳定订阅点（不持有 SimWorld，不做 O(敌人数) 扫描）。
                // story-005：Shape 改经 ComposeShapePresentation 二次映射——CarrierCompiler 链尾判定值仍恒为
                // Bolt/Melee 两种（不改判定），这里只把表现 Shape 按 Spin/Orbit/ExplodeOnHit/Count 细分，
                // 让装了不同 Module 基因的弹道读得出差异（R4，见该类注释）。
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
                    Direction = _abilities != null ? _abilities.AimDirection : DefaultForward,
                    HasProjectile = evt.Damage > 0f,
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
        /// story-006：每帧推进挂起的 Count 多发/Explode 延迟落点（不受 TickInterval 节流，与
        /// <see cref="TickPendingMotion"/> 同一节奏）。到期即在落点调用 DamageArea，语义上是
        /// "扔出去→落地爆炸"，不再是原地瞬时突发。
        /// </summary>
        private void TickPendingImpact(float dt)
        {
            for (int i = _pendingImpact.Count - 1; i >= 0; i--)
            {
                PendingImpact hit = _pendingImpact[i];
                hit.TimeLeft -= dt;

                // story-002：Trail 拖尾伤害——飞行过半时在沿途插值点补一次小额结算，让弹道本身也造成伤害，
                // 不是只有终点算数。只补一次（避免无界追加），半径减半、不携带 Chain/Pull（保持轻量）。
                if (hit.Trail > 0f && !hit.TrailFired && hit.Duration > 0f && hit.TimeLeft <= hit.Duration * 0.5f)
                {
                    float t = math.clamp(1f - hit.TimeLeft / hit.Duration, 0f, 1f);
                    float2 trailPos = math.lerp(hit.Origin, hit.ImpactPos, t);
                    DamageAreaPrimitive(trailPos, hit.Radius * 0.5f, hit.Trail, 0f, 0f);
                    hit.TrailFired = true;
                }

                if (hit.TimeLeft <= 0f)
                {
                    LastImpactPos = hit.ImpactPos;
                    DamageAreaPrimitive(hit.ImpactPos, hit.Radius, hit.Damage, hit.Chain, hit.Pull);

                    // story-002 Required 3：Pierce/Bounce/SplitOnHit/Return 必须真的多打/改路径几处，
                    // 不是只改一个数字——追加的二级命中不再携带 Pierce/Bounce/Split/Trail/Return/Linger，
                    // 防止同一发子弹递归放大出无界的追加命中。
                    if (hit.PierceLeft > 0)
                    {
                        float2 pierceTarget = hit.ImpactPos + hit.Direction * PrimitiveStepDistance;
                        _pendingImpact.Add(new PendingImpact
                        {
                            ImpactPos = pierceTarget,
                            Radius = hit.Radius,
                            Damage = hit.Damage,
                            TimeLeft = SecondaryImpactDelay,
                            Duration = SecondaryImpactDelay,
                            Origin = hit.ImpactPos,
                            Direction = hit.Direction,
                            Chain = hit.Chain,
                            Pull = hit.Pull,
                            PierceLeft = hit.PierceLeft - 1,
                        });
                    }

                    if (hit.BounceLeft > 0)
                    {
                        float2 reflected = ReflectDirection(hit.Direction, hit.BounceLeft);
                        _pendingImpact.Add(new PendingImpact
                        {
                            ImpactPos = hit.ImpactPos + reflected * PrimitiveStepDistance,
                            Radius = hit.Radius,
                            Damage = hit.Damage,
                            TimeLeft = SecondaryImpactDelay,
                            Duration = SecondaryImpactDelay,
                            Origin = hit.ImpactPos,
                            Direction = reflected,
                            Chain = hit.Chain,
                            Pull = hit.Pull,
                            BounceLeft = hit.BounceLeft - 1,
                        });
                    }

                    if (hit.SplitOnHit > 0)
                    {
                        for (int s = 0; s < hit.SplitOnHit; s++)
                        {
                            float angle = 2f * math.PI * s / hit.SplitOnHit;
                            float2 dir = new float2(math.cos(angle), math.sin(angle));
                            _pendingImpact.Add(new PendingImpact
                            {
                                ImpactPos = hit.ImpactPos + dir * (hit.Radius * 1.5f),
                                Radius = hit.Radius * 0.6f,
                                Damage = hit.Damage * 0.5f,
                                TimeLeft = SecondaryImpactDelay,
                                Duration = SecondaryImpactDelay,
                                Origin = hit.ImpactPos,
                                Direction = dir,
                                Chain = hit.Chain,
                                Pull = hit.Pull,
                            });
                        }
                    }

                    if (hit.ReturnPending && _sim != null)
                    {
                        // 飞回发射者「当前」位置（不是发射时的原点）——玩家这段时间可能已经移动。
                        float2 target = _sim.PlayerPosition;
                        _pendingImpact.Add(new PendingImpact
                        {
                            ImpactPos = target,
                            Radius = hit.Radius,
                            Damage = hit.Damage,
                            TimeLeft = hit.Duration > 0f ? hit.Duration : ComposeMotionMath.MotionFlightDuration,
                            Duration = hit.Duration,
                            Origin = hit.ImpactPos,
                            Direction = math.normalizesafe(target - hit.ImpactPos, DefaultForward),
                            Chain = hit.Chain,
                            Pull = hit.Pull,
                        });
                    }

                    if (hit.Linger > 0f)
                    {
                        _pendingLinger.Add(new PendingLinger
                        {
                            Position = hit.ImpactPos,
                            Radius = hit.Radius,
                            DamagePerTick = hit.Damage * 0.5f,
                            Interval = hit.TickRate > 0f ? 1f / hit.TickRate : DefaultLingerTickInterval,
                            NextTick = 0f,
                            TimeLeft = hit.Linger,
                            Chain = hit.Chain,
                            Pull = hit.Pull,
                        });
                    }

                    _pendingImpact.RemoveAt(i);
                }
                else
                {
                    _pendingImpact[i] = hit;
                }
            }
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

        private static bool HasBallisticPrimitives(HitEvent evt) =>
            evt.Homing > 0f || evt.Pierce > 0f || evt.Bounce > 0f || evt.Return ||
            evt.Trail > 0f || evt.SplitOnHit > 0f || evt.Linger > 0f ||
            evt.Speed > 0f || evt.Lifetime > 0f || evt.Gravity > 0f;

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

        /// <summary>story-002：反弹方向——沿入射方向镜像 180°，按 bounceIndex 奇偶各抖 ±30° 制造轨迹变化，
        /// 与 Pierce 的直线延续区分开，不引入 RNG 依赖（保持确定性）。</summary>
        private static float2 ReflectDirection(float2 dir, int bounceIndex)
        {
            float jitter = (bounceIndex % 2 == 0 ? 1f : -1f) * (math.PI / 6f);
            float angle = math.PI + jitter;
            float cos = math.cos(angle);
            float sin = math.sin(angle);
            return new float2(dir.x * cos - dir.y * sin, dir.x * sin + dir.y * cos);
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
            _pendingImpact?.Clear();
            _pendingLinger?.Clear();
            _sandboxCombatScope?.Dispose();
            _sandboxCombatScope = null;
            _sandboxRecentHits.Clear();
        }
    }
}
