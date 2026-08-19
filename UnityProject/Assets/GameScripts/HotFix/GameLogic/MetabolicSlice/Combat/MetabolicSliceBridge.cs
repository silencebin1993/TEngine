using System;
using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Core;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Carrier;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Environment;
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

        /// <summary>story-003：Spin/Orbit 命中延迟到期的最小 ephemeral 状态（数量与弹体数同级，非池化数组，见 Decision D7）。</summary>
        private struct PendingMotionHit
        {
            public float2 Origin;
            public float Radius;
            public float Damage;
            public float Phase;
            public float Spin;
            public float Orbit;
            public float TimeLeft;
        }

        private Engine _engine;
        private MetabolicSliceRunner _runner;
        private SimBridge _sim;
        private StatSheet _stats;
        private WorldEnvironment _environment;
        private readonly List<PendingMotionHit> _pendingMotion = new List<PendingMotionHit>();
        private float _timer;
        private int _seed;
        private float _playerShield;

        /// <summary>轴 A 局内提示：最近一次新增的地形/残留白话描述，供 HUD 展示（不需要打开面板也能看见）。</summary>
        public string LastEnvironmentPrompt { get; private set; } = "地面：潮湿（尚无残留反应）";

        /// <summary>story-004：最近一次非 Damage 出口（Shield/Displace/Spin/Orbit）的白话摘要，供 HUD/日志读。</summary>
        public string LastAbilityPrompt { get; private set; } = "";

        /// <summary>story-004：Shield 出口的本地累加值——Sim 无护盾吸收结算，先记账做最小可读，不接伤害减免。</summary>
        public float PlayerShield => _playerShield;

        /// <summary>story-003：当前挂起的 Spin/Orbit 延迟命中数量，供 execute_code 断言"生成即挂起、tick 完即清空"。</summary>
        public int PendingMotionCount => _pendingMotion?.Count ?? 0;

        /// <summary>story-006：LookDev 沙盒抑制玩家真实网格的常规 1.5s 装配 Tick（噪声源），不影响
        /// <see cref="TickPendingMotion"/> 与 <see cref="ApplyEvent"/>（沙盒发射的夹具仍要正常播完延迟命中动画）。</summary>
        public bool Suppressed { get; set; }

        public void Bind(SimBridge sim, StatSheet stats)
        {
            _sim = sim;
            _stats = stats;
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

            if (Suppressed)
            {
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
                float radius = DamageAreaRadius * MathF.Max(0.1f, evt.Scale);

                if (evt.Spin != 0f || evt.Orbit != 0f)
                {
                    // story-003：Spin/Orbit 命中改延迟到期采样点，而不是原地瞬时突发（D2/D4）。
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
                        });
                    }
                }
                else
                {
                    for (int h = 0; h < hits; h++)
                    {
                        _sim.DamageArea(_sim.PlayerPosition, radius, evt.Damage, BinGames.Sim.SimFaction.Hostile);
                    }
                }
                applied = true;

                if (evt.ExplodeOnHit)
                {
                    _sim.DamageArea(_sim.PlayerPosition, radius * ExplodeRadiusMult, evt.Damage * ExplodeDamageMult,
                        BinGames.Sim.SimFaction.Hostile);
                }
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
                Signals.Publish(new ComposeCastSignal
                {
                    Shape = evt.Shape,
                    Scale = evt.Scale,
                    Count = evt.Count,
                    Spin = evt.Spin,
                    Orbit = evt.Orbit,
                    ExplodeOnHit = evt.ExplodeOnHit,
                    Tags = evt.Tags,
                    Origin = _sim.PlayerPosition,
                    Direction = DefaultForward,
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
                    _sim.DamageArea(strikePos, hit.Radius, hit.Damage, BinGames.Sim.SimFaction.Hostile);
                    _pendingMotion.RemoveAt(i);
                }
                else
                {
                    _pendingMotion[i] = hit;
                }
            }
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

        public override void OnExit()
        {
            _engine = null;
            _runner = null;
            _environment = null;
            _pendingMotion?.Clear();
        }
    }
}
