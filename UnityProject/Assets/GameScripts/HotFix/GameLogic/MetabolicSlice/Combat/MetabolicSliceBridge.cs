using System;
using ChemEngine;
using ChemEngine.Builtin.Catalog;
using ChemEngine.Core;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Bag;
using GameLogic.MetabolicSlice.Grid;

namespace GameLogic.MetabolicSlice.Combat
{
    /// <summary>
    /// story-006 最小可用桥：把 ChemEngine 出口事件（HitEvent）接到战斗伤害路径。
    ///
    /// 范围刻意很小：固定 organ_core→organ_focus→organ_actuator 三件套演示装配，
    /// 不接真实玩家 Bag/Deck（那是后续 story 的活），只验证"链路通"。
    /// 只消费 HitEvent.Damage，其余字段（Heal/Shield/Displace 等）留给后续 story。
    /// </summary>
    public sealed class MetabolicSliceBridge : GameModuleBase
    {
        public override int Priority => ModulePriority.MetabolicBridge;

        private const float TickInterval = 1.5f;
        private const float DamageAreaRadius = 4f;

        private Engine _engine;
        private SlotGrid _grid;
        private MetabolicSliceRunner _runner;
        private SimBridge _sim;
        private float _timer;
        private int _seed;

        public void Bind(SimBridge sim)
        {
            _sim = sim;
        }

        public override void OnEnter()
        {
            _engine = new Engine();
            ReactionCatalog.RegisterDefaults(_engine);

            _grid = new SlotGrid(SlotType.Cytoplasm);
            _grid.Slots[0].Part = new PartInstance("bridge_core", "organ_core", PartLocation.Slot(0));
            _grid.Slots[1].Part = new PartInstance("bridge_focus", "organ_focus", PartLocation.Slot(1));
            _grid.Slots[4].Part = new PartInstance("bridge_actuator", "organ_actuator", PartLocation.Slot(4));
            _grid.TryAddEdge(0, 1);
            _grid.TryAddEdge(1, 4);

            _runner = new MetabolicSliceRunner(_engine);
            _timer = 0f;
            _seed = 0;
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running)
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

            var events = _runner.Tick(_grid, Array.Empty<IContract>(), new WorldState(), _seed);

            int consumed = 0;
            for (int i = 0; i < events.Count; i++)
            {
                HitEvent evt = events[i];
                if (evt.Damage <= 0f)
                {
                    continue;
                }
                _sim.DamageArea(_sim.PlayerPosition, DamageAreaRadius, evt.Damage, BinGames.Sim.SimFaction.Hostile);
                consumed++;
            }

            TEngine.Log.Info($"[MetabolicSliceBridge] Tick 产出 {events.Count} 个 HitEvent，已转 {consumed} 次 DamageArea");
        }

        public override void OnExit()
        {
            _engine = null;
            _grid = null;
            _runner = null;
        }
    }
}
