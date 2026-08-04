using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Core;
using Unity.Mathematics;

namespace GameLogic.Battle
{
    /// <summary>
    /// 持续区域系统。酸雾、菌毯、导电区、根须、毒区都是它。
    ///
    /// 为什么在热更层而不是内核：区域数量是几十个量级（不是几万），
    /// 而它需要读很多玩法概念（菌毯要影响吞噬收益、导电区要配合电系 build）。
    /// 每个区域每 tick 调一次内核的范围伤害/范围状态，成本可忽略。
    ///
    /// 菌毯路线（Spec §7.5）整条路线都建立在这个系统上。
    /// </summary>
    public sealed class AreaZoneSystem : GameModuleBase
    {
        public override int Priority => ModulePriority.Status;

        /// <summary>一个持续区域。</summary>
        public struct Zone
        {
            public float2 Center;
            public float Radius;
            public float TimeLeft;
            /// <summary>每次 tick 的伤害。0 表示无伤害区（如菌毯）。</summary>
            public float DamagePerTick;
            /// <summary>持续施加的状态。</summary>
            public SimStatus Status;
            public SimFaction TargetFaction;
            /// <summary>是否跟随玩家（自身周围的光环类效果）。</summary>
            public bool FollowPlayer;
            /// <summary>区域类型，供玩法查询（菌毯加成等）。</summary>
            public ZoneKind Kind;
            public int SourceId;
        }

        public enum ZoneKind
        {
            Generic = 0,
            /// <summary>菌毯：玩家在其中回复更快、吞噬收益更高。</summary>
            Mycelium = 1,
            /// <summary>导电区：其中的敌人持续获得导电。</summary>
            Conductive = 2,
            /// <summary>酸雾/毒区：持续伤害 + 腐蚀。</summary>
            Caustic = 3,
            /// <summary>根须：减速。</summary>
            Roots = 4,
        }

        private readonly List<Zone> _zones = new List<Zone>(64);
        private SimBridge _sim;
        private StatusSystem _status;

        /// <summary>区域生效节拍。不必每帧结算。</summary>
        private const float TickPeriod = 0.4f;
        private float _tickAccum;

        /// <summary>同时存在的区域上限。超出时丢弃最老的，防止菌毯类卡牌刷爆。</summary>
        private const int MaxZones = 64;

        public IReadOnlyList<Zone> Zones => _zones;
        public int ZoneCount => _zones.Count;

        public void Bind(SimBridge sim, StatusSystem status)
        {
            _sim = sim;
            _status = status;
        }

        public override void OnEnter()
        {
            _zones.Clear();
            _tickAccum = 0f;
        }

        public override void OnExit()
        {
            _zones.Clear();
        }

        public void Spawn(in Zone zone)
        {
            if (_zones.Count >= MaxZones)
            {
                _zones.RemoveAt(0);
            }
            _zones.Add(zone);
        }

        /// <summary>玩家是否处于某类友方区域内。菌毯加成、阵地类卡牌读它。</summary>
        public bool PlayerInZone(ZoneKind kind)
        {
            if (_sim == null || !_sim.Running)
            {
                return false;
            }
            float2 p = _sim.PlayerPosition;
            for (int i = 0; i < _zones.Count; i++)
            {
                if (_zones[i].Kind != kind)
                {
                    continue;
                }
                if (math.distancesq(_zones[i].Center, p) <= _zones[i].Radius * _zones[i].Radius)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>某类区域的总面积。菌毯路线的资源引擎（面积 → 收益）读它。</summary>
        public float TotalArea(ZoneKind kind)
        {
            float sum = 0f;
            for (int i = 0; i < _zones.Count; i++)
            {
                if (_zones[i].Kind == kind)
                {
                    sum += math.PI * _zones[i].Radius * _zones[i].Radius;
                }
            }
            return sum;
        }

        /// <summary>
        /// 区域消失时清掉它施加的状态。
        ///
        /// 已知取舍：若两个同类区域重叠，其中一个到期会把重叠处的状态一起清掉，
        /// 下一个 tick（≤0.4s）又会被另一个区域重新施加，视觉上是一次短暂闪断。
        /// 用"逐单位引用计数"能消除它，但要为此每 tick 遍历全快照——
        /// 对一个 0.4 秒内自愈的瑕疵来说不值得。
        /// </summary>
        private void ExpireZoneStatuses(in Zone z)
        {
            if (z.Status == SimStatus.None || _sim == null || !_sim.Running)
            {
                return;
            }
            _sim.ApplyStatusArea(z.Center, z.Radius, z.Status, false, z.TargetFaction);
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || !_sim.Running || _zones.Count == 0)
            {
                return;
            }

            bool tick = false;
            _tickAccum += dt;
            if (_tickAccum >= TickPeriod)
            {
                _tickAccum -= TickPeriod;
                tick = true;
            }

            float2 playerPos = _sim.PlayerPosition;

            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                Zone z = _zones[i];

                z.TimeLeft -= dt;
                if (z.TimeLeft <= 0f)
                {
                    ExpireZoneStatuses(in z);
                    _zones.RemoveAt(i);
                    continue;
                }

                if (z.FollowPlayer)
                {
                    z.Center = playerPos;
                }
                _zones[i] = z;

                if (!tick)
                {
                    continue;
                }

                if (z.DamagePerTick > 0f)
                {
                    _sim.DamageArea(z.Center, z.Radius, z.DamagePerTick,
                        z.TargetFaction, SimStatus.None, SimStatus.None,
                        0, 0f, 0f, z.SourceId);
                }

                if (z.Status != SimStatus.None)
                {
                    // 直接走内核的范围施加（它有空间哈希，是 O(k) 而非 O(N)）。
                    //
                    // 刻意不走 StatusSystem.ApplyTimedArea：那个方法要遍历整个快照来
                    // 登记逐单位到期时间，64 个区域 × 每 tick 一次会变成百万级迭代。
                    // 区域类状态不需要逐单位计时——区域每 tick 重新施加，
                    // 到期统一由下面的 ExpireZoneStatuses 一次性清掉。
                    _sim.ApplyStatusArea(z.Center, z.Radius, z.Status, true, z.TargetFaction);
                }
            }
        }
    }
}
