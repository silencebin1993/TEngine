using BinGames.Sim;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Battle
{
    /// <summary>
    /// AOT 内核的热更侧门面。**整个热更层只有这一个类碰内核。**
    ///
    /// 纪律（框架文档 §2.3）：
    /// - 写内核只能через命令缓冲，绝不直接改 NativeArray
    /// - 读内核只能через只读快照
    /// - 本类不做任何逐单位循环——那是内核的活
    ///
    /// 之所以要这层：HybridCLR 解释执行泛型原生容器是风险区，全部隔在 AOT 侧最安全。
    /// </summary>
    public sealed class SimBridge : GameModuleBase
    {
        public override int Priority => ModulePriority.Simulation;

        private ISimBackend _backend;
        private SimCommandBuffer _cmds;
        private SimSnapshot _snapshot;
        private SimConfig _cfg;
        private bool _running;

        /// <summary>逻辑 id 分配器。0 保留给玩家/环境。</summary>
        private int _nextLogicId = 1;

        public SimSnapshot Snapshot => _snapshot;
        public bool Running => _running;
        public float ArenaHalfExtent => _cfg.ArenaHalfExtent;

        /// <summary>本帧玩家意图。由输入模块与能力系统写，Update 时提交。</summary>
        public PlayerIntent Intent = PlayerIntent.Idle;

        public void Begin(SimConfig cfg, BehaviorArchetype[] archetypes)
        {
            End();

            _cfg = cfg;
            var world = new SimWorld();
            world.Initialize(cfg);
            world.SetArchetypes(archetypes);
            _backend = world;

            _cmds = default;
            _cmds.Initialize(Unity.Collections.Allocator.Persistent);
            _nextLogicId = 1;
            _running = true;
            _snapshot = _backend.GetSnapshot();
        }

        public void End()
        {
            if (_backend != null)
            {
                _backend.Dispose();
                _backend = null;
            }
            if (_cmds.IsCreated)
            {
                _cmds.Dispose();
            }
            _running = false;
            _snapshot = default;
        }

        public override void OnUpdate(float dt)
        {
            if (!_running || _backend == null)
            {
                return;
            }

            _cmds.SetPlayerIntent(Intent);
            _backend.Step(dt, ref _cmds);
            _snapshot = _backend.GetSnapshot();

            // 意图每帧重置，避免上一帧的冲刺倍率粘住
            Intent = PlayerIntent.Idle;
        }

        public override void OnExit()
        {
            End();
        }

        public int NextLogicId() => _nextLogicId++;

        // ── 写入接口（全部只是入队，实际生效在内核 Step）──

        public int Spawn(in SpawnRequest req)
        {
            if (!_running)
            {
                return SimConst.InvalidIndex;
            }
            _cmds.Spawn(req);
            return req.LogicId;
        }

        public void Despawn(int unitIndex)
        {
            if (_running) { _cmds.Despawn(unitIndex); }
        }

        /// <summary>圆形范围伤害。</summary>
        public void DamageArea(float2 origin, float radius, float amount,
            SimFaction targetFaction = SimFaction.Hostile,
            SimStatus applyStatus = SimStatus.None,
            SimStatus requireStatus = SimStatus.None,
            int chainCount = 0, float chainRange = 4f, float chainFalloff = 0.75f,
            int sourceLogicId = 0)
        {
            if (!_running) { return; }
            _cmds.Damage(new DamageRequest
            {
                Origin = origin,
                Radius = radius,
                TargetIndex = SimConst.InvalidIndex,
                Amount = amount,
                TargetFaction = targetFaction,
                ApplyStatus = applyStatus,
                RequireStatus = requireStatus,
                ChainCount = chainCount,
                ChainRange = chainRange,
                ChainFalloff = chainFalloff,
                SourceLogicId = sourceLogicId,
            });
        }

        /// <summary>单体伤害。</summary>
        public void DamageUnit(int unitIndex, float amount,
            SimStatus applyStatus = SimStatus.None,
            int chainCount = 0, float chainRange = 4f, float chainFalloff = 0.75f,
            int sourceLogicId = 0)
        {
            if (!_running) { return; }
            _cmds.Damage(new DamageRequest
            {
                Origin = float2.zero,
                Radius = -1f,
                TargetIndex = unitIndex,
                Amount = amount,
                TargetFaction = SimFaction.None,
                ApplyStatus = applyStatus,
                RequireStatus = SimStatus.None,
                ChainCount = chainCount,
                ChainRange = chainRange,
                ChainFalloff = chainFalloff,
                SourceLogicId = sourceLogicId,
            });
        }

        public void ApplyStatusArea(float2 origin, float radius, SimStatus status,
            bool add = true, SimFaction targetFaction = SimFaction.Hostile)
        {
            if (!_running) { return; }
            _cmds.Status(new StatusRequest
            {
                Origin = origin,
                Radius = radius,
                TargetIndex = SimConst.InvalidIndex,
                Status = status,
                TargetFaction = targetFaction,
                Add = add,
            });
        }

        public void ApplyStatusUnit(int unitIndex, SimStatus status, bool add = true)
        {
            if (!_running) { return; }
            _cmds.Status(new StatusRequest
            {
                Origin = float2.zero,
                Radius = -1f,
                TargetIndex = unitIndex,
                Status = status,
                TargetFaction = SimFaction.None,
                Add = add,
            });
        }

        public void FireProjectile(float2 pos, float2 dir, float speed, float damage,
            float radius = 0.25f, float lifetime = 2.5f, int pierce = 1,
            SimFaction targetFaction = SimFaction.Hostile,
            SimStatus applyStatus = SimStatus.None,
            int sourceLogicId = 0, int visualId = 0)
        {
            if (!_running) { return; }
            _cmds.Projectile(new ProjectileRequest
            {
                Position = pos,
                Direction = dir,
                Speed = speed,
                Damage = damage,
                Radius = radius,
                Lifetime = lifetime,
                Pierce = pierce,
                TargetFaction = targetFaction,
                ApplyStatus = applyStatus,
                SourceLogicId = sourceLogicId,
                VisualId = visualId,
            });
        }

        // ── 玩家读写 ──

        public float2 PlayerPosition => _running ? _snapshot.PlayerPosition : float2.zero;
        public float PlayerHealth => _running ? _snapshot.PlayerHealth : 0f;
        public float PlayerRadius => _running ? _snapshot.PlayerRadius : 1f;
        /// <summary>本帧玩家受到的接触伤害。由 Resolution 阶段消费。</summary>
        public float PlayerContactDamage => _running ? _snapshot.PlayerContactDamage : 0f;

        public void SetPlayerStats(float maxHp, float currentHp, float radius, float speed)
        {
            (_backend as SimWorld)?.SetPlayerStats(maxHp, currentHp, radius, speed);
        }

        public void DamagePlayer(float amount)
        {
            (_backend as SimWorld)?.DamagePlayer(amount);
        }

        public void HealPlayer(float amount, float maxHp)
        {
            (_backend as SimWorld)?.HealPlayer(amount, maxHp);
        }

        /// <summary>吞噬结算：直接击杀并生成死亡事件。</summary>
        public void ConsumeUnit(int unitIndex)
        {
            (_backend as SimWorld)?.KillUnit(unitIndex, 0);
        }

        public SimWorld World => _backend as SimWorld;

        public override void OnDispose()
        {
            End();
        }
    }
}
