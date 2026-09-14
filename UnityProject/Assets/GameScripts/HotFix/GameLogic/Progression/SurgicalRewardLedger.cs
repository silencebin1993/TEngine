using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Core;

namespace GameLogic.Progression
{
    /// <summary>
    /// M2-05c 手术窗口的最小奖励账本。
    ///
    /// 模拟层只提供接点摧毁与死亡事实；本热更模块负责把事实解释成玩法奖励：
    /// 玩家直控的低伤几何切离保留一个完整器官存根，带接点身体被整体伤害击杀只给生物质。
    /// 本阶段刻意不接正式器官掉落池、背包或持久化。
    /// </summary>
    public sealed class SurgicalRewardLedger : GameModuleBase
    {
        /// <summary>原型阈值：切离末击不得超过该接点满生命的 25%。非平衡终值。</summary>
        public const float PreciseLastHitMaxFraction = 0.25f;
        public const int BiomassPerRoughKill = 1;

        private readonly List<PreservedOrganStub> _intactOrgans = new List<PreservedOrganStub>(4);
        private readonly HashSet<ResolvedPartKey> _resolvedParts = new HashSet<ResolvedPartKey>();
        private SimBridge _sim;

        public override int Priority => ModulePriority.Progression;
        public int Biomass { get; private set; }
        public int IntactOrganCount => _intactOrgans.Count;
        public IReadOnlyList<PreservedOrganStub> IntactOrgans => _intactOrgans;

        public override void OnInit(ModuleHub hub)
        {
            base.OnInit(hub);
            _sim = hub.Require<SimBridge>();
        }

        public override void OnEnter()
        {
            Biomass = 0;
            _intactOrgans.Clear();
            _resolvedParts.Clear();
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running)
            {
                return;
            }
            SimSnapshot snapshot = _sim.Snapshot;
            ResolveFrame(_sim.World, in snapshot);
        }

        /// <summary>
        /// 只遍历本帧稀疏事件，不扫描单位容量。公开此纯结算入口，供固定种子编辑器回归直调。
        /// </summary>
        public void ResolveFrame(SimWorld world, in SimSnapshot snapshot)
        {
            if (world == null)
            {
                return;
            }

            for (int i = 0; i < snapshot.HitCount; i++)
            {
                HitEvent hit = snapshot.Hits[i];
                // 只有"实际命中具体接点"的直控命中才可能产出完整器官存根，其余命中一概跳过。
                // 这不只是省一次稀疏查询：非精准命中若先进入 ResolvePart，会把 _resolvedParts 的
                // 去重键抢先占掉，同一帧后面那条真正的精准末击就被当成重复事件丢弃——
                // 本帧内事件顺序决定奖励有无，正是 M2-06 场景（友军同时开火）里会撞上的假阴性。
                if (!SimBridge.IsSurgicalAimSource(hit.SourceLogicId))
                {
                    continue;
                }
                int targetIndex = hit.TargetIndex;
                if (targetIndex < 0 || targetIndex >= snapshot.Count ||
                    snapshot.FactionOf(targetIndex) != SimFaction.Hostile)
                {
                    continue;
                }

                SimEntityId entityId = snapshot.EntityId[targetIndex];
                ResolvePart(world, entityId, hit.TargetLogicId, SimBodyPartSlot.Primary, hit.SourceLogicId);
                ResolvePart(world, entityId, hit.TargetLogicId, SimBodyPartSlot.Secondary, hit.SourceLogicId);
            }

            for (int i = 0; i < snapshot.DeathCount; i++)
            {
                DeathEvent death = snapshot.Deaths[i];
                if (death.Faction == SimFaction.Hostile && death.HadSurgicalBody != 0 &&
                    death.CauseKind == DeathCauseKind.Damage)
                {
                    Biomass += BiomassPerRoughKill;
                }
            }
        }

        private void ResolvePart(SimWorld world, SimEntityId entityId, int targetLogicId,
            SimBodyPartSlot slot, int sourceLogicId)
        {
            if (!world.TryGetBodyPart(entityId, slot, out SimBodyPart part) || part.Destroyed == 0)
            {
                return;
            }

            var key = new ResolvedPartKey(entityId.Value, slot);
            if (!_resolvedParts.Add(key))
            {
                return;
            }

            if (!SimBridge.IsSurgicalAimSource(sourceLogicId) ||
                part.DestroyedBySingleTargetHit == 0 || part.MaxHealth <= 0f ||
                part.LastHitAmount > part.MaxHealth * PreciseLastHitMaxFraction)
            {
                return;
            }

            _intactOrgans.Add(new PreservedOrganStub
            {
                SourceLogicId = targetLogicId,
                Slot = slot,
            });
        }

        private readonly struct ResolvedPartKey : System.IEquatable<ResolvedPartKey>
        {
            private readonly ulong _entityId;
            private readonly SimBodyPartSlot _slot;

            public ResolvedPartKey(ulong entityId, SimBodyPartSlot slot)
            {
                _entityId = entityId;
                _slot = slot;
            }

            public bool Equals(ResolvedPartKey other) => _entityId == other._entityId && _slot == other._slot;
            public override bool Equals(object obj) => obj is ResolvedPartKey other && Equals(other);
            public override int GetHashCode() => unchecked(((int)_entityId * 397) ^ (int)(_entityId >> 32) ^ (int)_slot);
        }
    }

    /// <summary>完整器官的里程碑存根；正式掉落 id、品质、背包与持久化留给 M3。</summary>
    public struct PreservedOrganStub
    {
        public int SourceLogicId;
        public SimBodyPartSlot Slot;
    }
}
