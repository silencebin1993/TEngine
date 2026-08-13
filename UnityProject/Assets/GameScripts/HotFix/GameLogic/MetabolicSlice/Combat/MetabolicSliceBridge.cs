using System;
using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Core;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Environment;
using GameLogic.MetabolicSlice.Grid;
using GameLogic.Stats;
using GameLogic.UI.Battle;
using Unity.Mathematics;

namespace GameLogic.MetabolicSlice.Combat
{
    /// <summary>
    /// story-006 起的最小可用桥，story-003 改为消费玩家真实网格：把 ComposeEngine 出口事件
    /// （HitEvent）接到战斗伤害路径。
    ///
    /// 默认读 <see cref="MetabolicSlicePanel.Instance"/> 持有的那份 SlotGrid——即玩家在
    /// 002 面板里装/卸/画边的结果，不再固定 organ_core→organ_focus→organ_actuator 三件套。
    /// 旧演示三件套仍在（不删旧 Draft），但只能靠面板里的调试按钮手动覆盖到玩家网格上。
    /// 无输出链（<see cref="MetabolicSliceRunner.Tick"/> 编译不出 source→sink 路径）时
    /// 天然不产出 HitEvent，因此本类什么都不做——不误伤、也不用额外的空链判断。
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
        private const float DamageAreaRadius = 4f;
        private const float ExplodeRadiusMult = 1.6f;
        private const float ExplodeDamageMult = 0.5f;

        /// <summary>整个战场只有这一个环境格——本 story 不建真实坐标网格（沿用 WorldEnvironment 现有约定）。</summary>
        public const string ArenaCellId = "arena";

        private static readonly Dictionary<string, string> TagDisplayNames = new Dictionary<string, string>
        {
            ["Wet"] = "潮湿", ["Oil"] = "油", ["Acid"] = "酸", ["SugarFilm"] = "含糖膜",
            ["Light"] = "光照", ["Shadow"] = "阴影", ["SaltFrost"] = "盐霜",
            ["Steam"] = "蒸汽", ["Fire"] = "火", ["Burning"] = "燃烧", ["BurningGround"] = "燃烧地面",
            ["StickyAcid"] = "粘酸", ["Shock"] = "电击",
        };

        private Engine _engine;
        private MetabolicSliceRunner _runner;
        private SimBridge _sim;
        private StatSheet _stats;
        private WorldEnvironment _environment;
        private float _timer;
        private int _seed;
        private float _playerShield;

        /// <summary>轴 A 局内提示：最近一次新增的地形/残留白话描述，供 HUD 展示（不需要打开面板也能看见）。</summary>
        public string LastEnvironmentPrompt { get; private set; } = "地面：潮湿（尚无残留反应）";

        /// <summary>story-004：最近一次非 Damage 出口（Shield/Displace/Spin/Orbit）的白话摘要，供 HUD/日志读。</summary>
        public string LastAbilityPrompt { get; private set; } = "";

        /// <summary>story-004：Shield 出口的本地累加值——Sim 无护盾吸收结算，先记账做最小可读，不接伤害减免。</summary>
        public float PlayerShield => _playerShield;

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

            SlotGrid grid = MetabolicSlicePanel.Instance != null ? MetabolicSlicePanel.Instance.Grid : null;
            if (grid == null)
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

            IReadOnlyList<IContract> geneContracts = MetabolicSlicePanel.Instance != null
                ? MetabolicSlicePanel.Instance.GeneContracts
                : Array.Empty<IContract>();
            // 传持久 _environment.State（不再是每 Tick 一份新 WorldState）+ ArenaCellId，
            // 让链路里挂了地形反应 tag（如 org_perox 的 TagAttach("Fire")）的事件能真正撞上地形（Wet）。
            var events = _runner.Tick(grid, geneContracts, _environment.State, _seed, ArenaCellId);

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
                for (int h = 0; h < hits; h++)
                {
                    _sim.DamageArea(_sim.PlayerPosition, radius, evt.Damage, BinGames.Sim.SimFaction.Hostile);
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

            if (evt.Spin != 0f || evt.Orbit != 0f)
            {
                // Motion 轴 stub：尚无弹道/绕轨系统时先可读；禁止当成「纯表现永不进机制」。
                LastAbilityPrompt = $"运动机制（待接弹道）：Spin={evt.Spin:0.#} Orbit={evt.Orbit:0.#}";
            }

            return applied;
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
        }
    }
}
