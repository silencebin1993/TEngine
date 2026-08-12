using System;
using System.Collections.Generic;
using ChemEngine;
using ChemEngine.Builtin.Catalog;
using ChemEngine.Core;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Environment;
using GameLogic.MetabolicSlice.Grid;
using GameLogic.UI.Battle;

namespace GameLogic.MetabolicSlice.Combat
{
    /// <summary>
    /// story-006 起的最小可用桥，story-003 改为消费玩家真实网格：把 ChemEngine 出口事件
    /// （HitEvent）接到战斗伤害路径。
    ///
    /// 默认读 <see cref="MetabolicSlicePanel.Instance"/> 持有的那份 SlotGrid——即玩家在
    /// 002 面板里装/卸/画边的结果，不再固定 organ_core→organ_focus→organ_actuator 三件套。
    /// 旧演示三件套仍在（不删旧 Draft），但只能靠面板里的调试按钮手动覆盖到玩家网格上。
    /// 无输出链（<see cref="MetabolicSliceRunner.Tick"/> 编译不出 source→sink 路径）时
    /// 天然不产出 HitEvent，因此本类什么都不做——不误伤、也不用额外的空链判断。
    /// 只消费 HitEvent.Damage，其余字段（Heal/Shield/Displace 等）留给后续 story。
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
        private WorldEnvironment _environment;
        private float _timer;
        private int _seed;

        /// <summary>轴 A 局内提示：最近一次新增的地形/残留白话描述，供 HUD 展示（不需要打开面板也能看见）。</summary>
        public string LastEnvironmentPrompt { get; private set; } = "地面：潮湿（尚无残留反应）";

        public void Bind(SimBridge sim)
        {
            _sim = sim;
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
                if (evt.Damage <= 0f)
                {
                    continue;
                }
                _sim.DamageArea(_sim.PlayerPosition, DamageAreaRadius, evt.Damage, BinGames.Sim.SimFaction.Hostile);
                consumed++;
            }
            _environment.Tick(1);

            TEngine.Log.Info($"[MetabolicSliceBridge] Tick 产出 {events.Count} 个 HitEvent，已转 {consumed} 次 DamageArea");
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
