using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Regions;
using GameConfig.fg;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG13-FAC-01（FG09 第 8 章、FGR-FAC-008 白潮专属语法）热更层门面：内核玩法事件（<see cref="CombatEventKind.FleetEvent"/>）落地——轨道打击伤建筑、登陆舱落地投放；
    /// 灭活焦土格查询（建造校验逐格调）、屏蔽塔接入判定（<see cref="Signal.SignalUplinkService"/> 唯一入口调）、可复用的轨道打击排程（FG13-END-03 的“落点提前 30 秒标出”直接调这里）。
    /// 逐单位 / 逐格的循环全在内核（Main/Sim）；这里每条事件 O(1)（投放 O(投放台数)），焦土格缓存每个内核步最多重建一次（O(焦土格 ≤ 256)）。
    /// </summary>
    public sealed partial class CombatSite
    {
        private readonly HashSet<long> _inertCells = new HashSet<long>();
        private long _inertSteps = -1;
        private int _inertCount = -1;
        private double _inertTime = -1;

        /// <summary>自检读：本会话登陆舱投放的单位数 / 轨道打击伤到的建筑数。</summary>
        public static int PodCargoSpawned { get; private set; }
        public static int StrikeBuildingHits { get; private set; }

        public static void ResetFleetCountersForTests()
        {
            PodCargoSpawned = 0;
            StrikeBuildingHits = 0;
        }

        // FG13-E2E-01（FG14 §5 硬约束 3）：白潮稀疏表 / 护盾闸门 / 单位视图 / 内核时钟的只读门面——Campaign/Combat 以外（战略地图、旗舰与清洗波次服务）经这里读，不直接碰内核。
        public double KernelTime => Kernel.Time;
        public int FleetCount => Kernel.FleetCount;
        public int FleetStrikeCount => Kernel.FleetStrikeCount;
        public int FleetMarkCount => Kernel.FleetMarkCount;
        public CombatFleetStrike FleetStrikeAt(int i) => Kernel.FleetStrikeAt(i);
        public CombatFleetMark FleetMarkAt(int i) => Kernel.FleetMarkAt(i);
        public bool TryGetFleetAt(int i, out CombatFleet state) => Kernel.TryGetFleetAt(i, out state);
        public bool UplinkBlockerAt(int n, out int unitId, out double2 pos, out float range) => Kernel.UplinkBlockerAt(n, out unitId, out pos, out range);
        public bool TryGetGate(int unitId, out byte family, out float gate, out float max) => Kernel.TryGetGate(unitId, out family, out gate, out max);
        public bool TryGetUnitView(int unitId, out CombatUnitView view) => Kernel.TryGetUnit(unitId, out view);

        private static long CellKey(int x, int y) => ((long)x << 32) ^ (uint)y;

        /// <summary>格 (x, y) 是不是灭活焦土（恢复到点前都算）。灭活焦土只存在内核快照段 42，不写家园格网地形、与燃烧战痕 reaction_scorch 无关（ADR-FAC-008 ①）。</summary>
        public bool InertAtCell(int x, int y)
        {
            if (IsDisposed || Kernel.FleetMarkCount == 0)
            {
                return false;
            }
            if (_inertSteps != Kernel.Steps || _inertCount != Kernel.FleetMarkCount || _inertTime != Kernel.Time)
            {
                _inertCells.Clear();
                for (int i = 0; i < Kernel.FleetMarkCount; i++)
                {
                    CombatFleetMark m = Kernel.FleetMarkAt(i);
                    _inertCells.Add(CellKey(m.Cell.x, m.Cell.y));
                }
                _inertSteps = Kernel.Steps;
                _inertCount = Kernel.FleetMarkCount;
                _inertTime = Kernel.Time;
            }
            return _inertCells.Contains(CellKey(x, y));
        }

        /// <summary>家园格是不是灭活焦土（家园建造 / 传送带校验逐格调）。</summary>
        public static bool InertHomeCell(int x, int y) => CombatSites.Get(HomeValleyLayout.RegionId)?.InertAtCell(x, y) ?? false;

        /// <summary>点 <paramref name="p"/> 是否在本地点任一存活屏蔽塔的范围内（按表列 flBlockRange，不认 ID）。</summary>
        public bool AnyUplinkBlockAt(Vector2 p, out float range)
        {
            range = 0f;
            return !IsDisposed && Kernel.AnyUplinkBlockAt(new double2(p.x, p.y), out _, out range);
        }

        /// <summary>可复用的轨道打击排程（FG13-END-03 的提前标落点直接调）：返回打击 ID（0 = 失败）；倒计时按游戏秒（暂停不走、倍速同比）。</summary>
        public int ScheduleOrbitalStrike(Vector2 center, float radius, float damage, float delay, float buildingFrac) =>
            IsDisposed ? 0 : Kernel.ScheduleOrbitalStrike(new double2(center.x, center.y), radius, damage, Mathf.Max(0.8f, delay), buildingFrac);

        public bool CancelOrbitalStrike(int strikeId) => !IsDisposed && Kernel.CancelOrbitalStrike(strikeId);

        /// <summary>FG13-END-04：在格 (x, y) 写一格灭活焦土，来源 = 具名敌人 <paramref name="sourceEnemyId"/> 的内核单位（它活着焦土就不恢复）。</summary>
        public bool AddInertMark(int x, int y, string sourceEnemyId, float recover, float stagger) =>
            !IsDisposed && TryGetEnemyUnit(sourceEnemyId, out int unit) && Kernel.AddInertMark(new int2(x, y), unit, recover, stagger);

        private void HandleFleetEvent(CampaignState state, in CombatEvent e)
        {
            var at = new Vector2((float)e.Pos.x, (float)e.Pos.y);
            switch (e.Code)
            {
                case CombatFleetConst.EvtStrike:
                    // 建筑按世界坐标受损（与轨道坠落事件同一个结算，比例 = 表列 flStrikeBuilding）；单位伤害内核已结算。
                    // 只在长期留存地点（家园，世界坐标）结算：其余地点内核是地点局部坐标，同坐标不是同一处（与 GroundMark 同一门槛）。
                    if (PersistSite && state != null && e.Value2 > 0f)
                    {
                        StrikeBuildingHits += Env.OrbitalDropService.HitBuildings(state, at, e.Value, e.Value2);
                    }
                    return;
                case CombatFleetConst.EvtPodLanded:
                    SpawnPodCargo(state, e.Unit, e.Other, at);
                    return;
            }
        }

        /// <summary>
        /// 登陆舱落地投放：读舱行的 flPodCargo × flPodCount，排在落点周围（fleet.pod.cargo_ring 米的圆上，按序号等分角度）。
        /// 攻城队伍里的舱 → 同一队伍的攻城单位；区域里的舱 → 敌人记录（进存档；落地状态在快照段 40，读档不会重复落地）。
        /// </summary>
        private void SpawnPodCargo(CampaignState state, int podUnit, int groupKey, Vector2 at)
        {
            MechEnemy pod = null;
            RegionEnemyRecord podRec = null;
            if (groupKey > 0)
            {
                if (Kernel.TryGetUnit(podUnit, out CombatUnitView v))
                {
                    RaidUnitDef pd = RaidCatalog.UnitOfKind(SiegeKindOf(-v.ExtKey, groupKey));
                    EnemyDefs.TryGet(pd?.EnemyTypeId, out pod);
                }
            }
            else
            {
                podRec = FindEnemyRecord(state, podUnit);
                EnemyDefs.TryGet(podRec?.EnemyTypeId, out pod);
            }
            if (!EnemyFleetParams.Cargo(pod, out MechEnemy cargo, out int count))
            {
                return;
            }
            float ring = Mathf.Clamp(Tuning("fleet.pod.cargo_ring", 2.5f), 0.5f, 8f);
            if (groupKey > 0)
            {
                RaidUnitDef def = null;
                foreach (RaidUnitDef u in RaidCatalog.Units)
                {
                    if (u.EnemyTypeId == cargo.Id)
                    {
                        def = u;
                        break;
                    }
                }
                if (def == null)
                {
                    TEngine.Log.Warning($"[FleetPod] 攻城队伍 {groupKey} 的登陆舱投放 {cargo.Id}：编成表里没有这一行，跳过");
                    return;
                }
                Vector2 target = at;
                float str = 1f;
                foreach (TransitGroupRecord g in SiegeService.SiegingGroups(state))
                {
                    if (SiegeService.KeyOf(g) == groupKey)
                    {
                        target = new Vector2((float)g.TargetX, (float)g.TargetY);
                        str = SiegeService.StrengthOf(state, g); // FG14-SBX-01：舱里投放的单位与这一波同强度
                        break;
                    }
                }
                for (int k = 0; k < count; k++)
                {
                    if (SiegeService.SpawnOne(this, groupKey, RingAt(at, ring, k, count), target, def, false,
                            DifficultyService.EnemyHealthMul(state) * str, DifficultyService.EnemyDamageMul(state) * str) > 0)
                    {
                        PodCargoSpawned++;
                    }
                }
                return;
            }
            if (state == null)
            {
                return;
            }
            string baseId = (podRec?.EnemyInstanceId ?? "fleet.pod." + podUnit.ToString(CultureInfo.InvariantCulture)) + ".c";
            string region = podRec?.RegionId ?? SiteId;
            var list = new List<RegionEnemyRecord>(state.RegionEnemies ?? Array.Empty<RegionEnemyRecord>());
            var fresh = new List<RegionEnemyRecord>(count);
            for (int k = 0; k < count; k++)
            {
                string id = baseId + k.ToString(CultureInfo.InvariantCulture);
                if (list.Exists(r => r != null && r.EnemyInstanceId == id))
                {
                    continue; // 同一舱的投放已经在记录里：不重复投放
                }
                float hp = EnemyDefs.SeedHealth(state, cargo);
                var rec = new RegionEnemyRecord
                {
                    EnemyInstanceId = id, RegionId = region, EnemyTypeId = cargo.Id, Position = RingAt(at, ring, k, count), Health = hp, MaxHealth = hp, IsAlive = true,
                };
                list.Add(rec);
                fresh.Add(rec);
            }
            if (fresh.Count == 0)
            {
                return;
            }
            state.RegionEnemies = list.ToArray();
            foreach (RegionEnemyRecord rec in fresh)
            {
                if (EnemySpawnBuilder.TryBuild(this, state, rec, RegionEnemySites.For(rec.RegionId), out CombatSpawn spawn) && SpawnEnemy(rec, spawn) > 0)
                {
                    PodCargoSpawned++;
                }
            }
        }

        private static Vector2 RingAt(Vector2 at, float ring, int k, int n)
        {
            float a = Mathf.PI * 2f * k / Mathf.Max(1, n);
            return at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * ring;
        }
    }
}
