using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Core;
using GameLogic.Spawning;
using UnityEngine;

namespace GameLogic.Stage.CellStage
{
    /// <summary>
    /// 首领三阶段（TR-cell-011）。完全数据驱动：阶段定义在 <see cref="BossPhaseSpec"/>
    /// 里（血量阈值、切换后的行为原型），本类只做"找首领 → 读血量 → 对表 → 切原型"，
    /// 新增/调整阶段只改配置表，本类不用动。
    ///
    /// 定位与 <see cref="PhaseTimeline"/>（生态时期）不同：那是"局内第几个大阶段"，
    /// 这是"首领这一场战斗内部的第几个小阶段"，两者不是同一概念。
    /// </summary>
    public sealed class BossPhaseController : GameModuleBase
    {
        public override int Priority => ModulePriority.Resolution;

        private SimBridge _sim;

        /// <summary>找首领是 O(容量) 扫描，节流到这个间隔，只在"当前没跟丢首领"时才付这个成本。</summary>
        private const float ScanInterval = 0.5f;
        private float _scanAccum;

        private int _bossUnitIndex = SimConst.InvalidIndex;
        private int _bossEnemyId;
        private float _bossMaxHealth;
        private IReadOnlyList<BossPhaseSpec> _phases;

        /// <summary>当前阶段序号，-1 表示尚未跟踪到首领。局外 HUD/自检读它。</summary>
        public int CurrentPhaseIndex { get; private set; } = -1;

        public void Bind(SimBridge sim)
        {
            _sim = sim;
        }

        public override void OnEnter()
        {
            // 阶段 0 应该在首领刚生成的下一帧就生效，不必等第一个节流周期。
            _scanAccum = ScanInterval;
            _bossUnitIndex = SimConst.InvalidIndex;
            _bossEnemyId = 0;
            _bossMaxHealth = 0f;
            _phases = null;
            CurrentPhaseIndex = -1;
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running)
            {
                return;
            }

            SimSnapshot snap = _sim.Snapshot;

            bool tracking = _bossUnitIndex != SimConst.InvalidIndex
                && snap.IsAlive(_bossUnitIndex)
                && snap.HasStatus(_bossUnitIndex, SimStatus.Boss);

            if (!tracking)
            {
                _scanAccum += dt;
                if (_scanAccum < ScanInterval)
                {
                    return;
                }
                _scanAccum = 0f;
                FindBoss(in snap);
                if (_bossUnitIndex == SimConst.InvalidIndex)
                {
                    return;
                }
                // 找到即评估阶段，不必再等一个节流周期——首领刚生成就该处于阶段 0。
            }

            EvaluatePhase(in snap);
        }

        /// <summary>
        /// 节流的 O(容量) 扫描。只有"没跟踪到首领"时才会跑到这里，
        /// 一局里最多发生几次（首领生成时一次、意外跟丢后重找），不是每帧成本。
        /// </summary>
        private void FindBoss(in SimSnapshot snap)
        {
            for (int i = 1; i < snap.Count; i++)
            {
                if (snap.Alive[i] == 0 || snap.Faction[i] != (byte)SimFaction.Hostile)
                {
                    continue;
                }
                if ((snap.Status[i] & (uint)SimStatus.Boss) == 0u)
                {
                    continue;
                }

                int enemyId = SpawnDirector.DecodeEnemyId(snap.LogicId[i]);
                IReadOnlyList<BossPhaseSpec> phases = DataRegistry.Instance.GetBossPhases(enemyId);
                EnemySpec spec = DataRegistry.Instance.GetEnemy(enemyId);
                if (phases == null || phases.Count == 0 || spec == null || spec.Health <= 0f)
                {
                    continue;
                }

                _bossUnitIndex = i;
                _bossEnemyId = enemyId;
                _bossMaxHealth = spec.Health;
                _phases = phases;
                CurrentPhaseIndex = -1;
                return;
            }
        }

        /// <summary>
        /// 按血量百分比对表：取所有"血量% ≤ 阈值"里阈值最小的一项——
        /// 即血量掉到哪一档就用哪一档，与配置表里的行顺序无关。
        /// </summary>
        private void EvaluatePhase(in SimSnapshot snap)
        {
            if (_phases == null || _bossMaxHealth <= 0f)
            {
                return;
            }

            float hpPct = Mathf.Clamp01(snap.Health[_bossUnitIndex] / _bossMaxHealth);

            BossPhaseSpec target = null;
            for (int i = 0; i < _phases.Count; i++)
            {
                BossPhaseSpec p = _phases[i];
                if (hpPct <= p.HpThreshold && (target == null || p.HpThreshold < target.HpThreshold))
                {
                    target = p;
                }
            }

            if (target == null || target.PhaseIndex == CurrentPhaseIndex)
            {
                return;
            }

            CurrentPhaseIndex = target.PhaseIndex;
            _sim.SwapArchetype(_bossUnitIndex, target.ArchetypeIndex);

            Signals.Publish(new BossPhaseChangedSignal
            {
                BossEnemyId = _bossEnemyId,
                PhaseIndex = target.PhaseIndex,
                PhaseName = target.Name,
            });

            TEngine.Log.Info($"[BossPhaseController] 首领 {_bossEnemyId} 进入阶段 {target.PhaseIndex}"
                + $"（{target.Name}，血量 {hpPct:P0}）→ 行为原型 {target.ArchetypeIndex}");
        }

        public override void OnExit()
        {
            _bossUnitIndex = SimConst.InvalidIndex;
            _phases = null;
            CurrentPhaseIndex = -1;
        }
    }
}
