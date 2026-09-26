using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG0-ARCH-03：Demo 战斗内容 → 战斗内核数据的唯一翻译处（“与 Demo 战斗规则一致”）。
    /// 每一种 Demo 敌人的行为、武器、护甲、牵引点都在这里按 Demo 常量（FracturedCityLayout / FoundryOutpostLayout / fg.TbMechEnemy）给出；
    /// 内核按这些数据跑 Demo 原来的逐敌人 AI（原 FracturedCityEnemyAi / FoundryOutpostEnemyAi / FoundryOutpostCoreBoss.TickCoreAttack 已删除）。
    /// 按标签与接口入表属于 FG9-DATA-01（DEBT-FG0DATA01-02）。
    /// </summary>
    public static class CombatDemoContent
    {
        private const float EnemyRadius = 0.8f;

        private static CombatSpawn Base(CombatBehavior behavior, CombatUnitKind kind, CombatUnitFlags extra)
        {
            return new CombatSpawn
            {
                Kind = kind,
                Faction = CombatFaction.Hostile,
                Behavior = behavior,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | extra,
                Radius = EnemyRadius,
                Weapon = -1,
                BehaviorProfile = -1,
                Priority = 1,
                ArmorHalfAngleDeg = 90f,
                ArmorFacing = new float2(0f, -1f),
            };
        }

        private static int Weapon(CombatSite site, float range, float damage, float cooldown, float aim = 0f)
        {
            return site.WeaponIndex(new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant,
                HasOutput = 1,
                Range = range,
                Damage = damage,
                Cooldown = cooldown,
                AimSeconds = aim,
                TargetMode = CombatTargetMode.Nearest,
            });
        }

        /// <summary>Demo 敌人记录 → 内核单位模板。认不出的敌人类型返回 false（记日志，不静默替身）。</summary>
        public static bool TryTemplate(CombatSite site, CampaignState state, RegionEnemyRecord e, out CombatSpawn spawn)
        {
            spawn = default;
            if (site == null || e == null)
            {
                return false;
            }
            if (e.RegionId == FracturedCityLayout.RegionId)
            {
                if (e.EnemyTypeId == EnemyCatalog.ScoutId)
                {
                    spawn = Base(CombatBehavior.Scout, CombatUnitKind.Enemy, CombatUnitFlags.Report | CombatUnitFlags.NeedsLos);
                    spawn.BehaviorProfile = site.ProfileIndex(new CombatBehaviorProfile
                    {
                        Speed = FracturedCityLayout.ScoutMoveSpeed,
                        FleeTrigger = FracturedCityLayout.ScoutFleeTriggerRange,
                        Leash = FracturedCityLayout.ScoutFleeLeash,
                        PatrolRadius = FracturedCityLayout.ScoutPatrolRadius,
                        PatrolFreq = 0.6f,
                        SenseRange = FracturedCityLayout.ScoutMarkRange,
                        CycleSeconds = FracturedCityLayout.ScoutMarkIntervalSeconds,
                        EffectSeconds = FracturedCityLayout.MarkDurationSeconds,
                    });
                    Vector2 home = e.EnemyInstanceId == FracturedCityLayout.Scout1SpawnId
                        ? FracturedCityLayout.Scout1Spawn.Position
                        : FracturedCityLayout.Scout2Spawn.Position;
                    spawn.Home = new double2(home.x, home.y);
                    return true;
                }
                if (e.EnemyTypeId == EnemyCatalog.JammerId)
                {
                    spawn = Base(CombatBehavior.Jammer, CombatUnitKind.Enemy, CombatUnitFlags.Report | CombatUnitFlags.NeedsLos | CombatUnitFlags.WeaponEnabled);
                    spawn.Weapon = Weapon(site, FracturedCityLayout.JammerAttackRange, FracturedCityLayout.JammerAttackDamage, FracturedCityLayout.JammerAttackCooldownSeconds);
                    spawn.BehaviorProfile = site.ProfileIndex(new CombatBehaviorProfile { EffectRange = FracturedCityLayout.JammerRadius });
                    spawn.Home = new double2(e.Position.x, e.Position.y);
                    return true;
                }
            }
            else if (e.RegionId == FoundryOutpostLayout.RegionId)
            {
                if (e.EnemyTypeId == FoundryOutpostLayout.BossNodeTypeId)
                {
                    spawn = Base(CombatBehavior.None, CombatUnitKind.Structure, CombatUnitFlags.ExternalHealth);
                    spawn.Radius = 2f;
                    spawn.Home = new double2(e.Position.x, e.Position.y);
                    return true;
                }
                if (e.EnemyTypeId == FoundryOutpostLayout.BossCoreTypeId)
                {
                    spawn = Base(CombatBehavior.HoldFire, CombatUnitKind.Structure,
                        CombatUnitFlags.ExternalHealth | CombatUnitFlags.NeedsLos | CombatUnitFlags.SilentFire);
                    spawn.Priority = 0; // Demo：首领（主核心）先于其它敌人结算。
                    spawn.Radius = 3f;
                    spawn.Weapon = Weapon(site, FoundryOutpostLayout.CoreAttackRange, FoundryOutpostLayout.CoreAttackDamage, FoundryOutpostLayout.CoreAttackCooldownSeconds);
                    spawn.ArmorFraction = 0f;
                    spawn.ArmorHalfAngleDeg = FoundryOutpostLayout.MainCoreFrontalHalfAngleDeg;
                    spawn.ArmorFacing = new float2(FoundryOutpostLayout.MainCoreFacing.x, FoundryOutpostLayout.MainCoreFacing.y);
                    spawn.Home = new double2(e.Position.x, e.Position.y);
                    return true;
                }
                if (e.EnemyTypeId == EnemyCatalog.ArmorBotId)
                {
                    spawn = Base(CombatBehavior.HoldFire, CombatUnitKind.Enemy, CombatUnitFlags.Report | CombatUnitFlags.NeedsLos | CombatUnitFlags.WeaponEnabled);
                    spawn.Weapon = Weapon(site, FoundryOutpostLayout.ArmorBotAttackRange, FoundryOutpostLayout.ArmorBotAttackDamage, FoundryOutpostLayout.ArmorBotAttackCooldownSeconds);
                    spawn.ArmorFraction = FoundryOutpostLayout.ArmorBotFrontalReductionPct;
                    spawn.ArmorHalfAngleDeg = FoundryOutpostLayout.ArmorBotFrontalHalfAngleDeg;
                    Vector2 facing = FoundryOutpostRegion.ArmorFacingOf(e);
                    spawn.ArmorFacing = new float2(facing.x, facing.y);
                    RegionRecord region = FoundryOutpostRegion.Find(state);
                    if (region != null && region.AdaptationId == AdaptationCatalog.HeatResistant)
                    {
                        spawn.Flags |= CombatUnitFlags.HeatResistant;
                    }
                    spawn.Home = new double2(e.Position.x, e.Position.y);
                    return true;
                }
                if (e.EnemyTypeId == EnemyCatalog.StriderId)
                {
                    spawn = Base(CombatBehavior.Telegraph, CombatUnitKind.Enemy, CombatUnitFlags.Report | CombatUnitFlags.NeedsLos | CombatUnitFlags.WeaponEnabled);
                    spawn.Weapon = Weapon(site, FoundryOutpostLayout.StriderAttackRange, FoundryOutpostLayout.StriderAttackDamage,
                        FoundryOutpostLayout.StriderAttackCooldownSeconds, FoundryOutpostLayout.StriderAimSeconds);
                    spawn.Home = new double2(e.Position.x, e.Position.y);
                    return true;
                }
                if (e.EnemyTypeId == EnemyCatalog.RepairBotId)
                {
                    spawn = Base(CombatBehavior.Repair, CombatUnitKind.Enemy, CombatUnitFlags.Report);
                    spawn.BehaviorProfile = site.ProfileIndex(new CombatBehaviorProfile
                    {
                        Speed = FoundryOutpostLayout.RepairBotMoveSpeed,
                        FleeTrigger = FoundryOutpostLayout.RepairBotFleeTriggerRange,
                        Leash = FoundryOutpostLayout.RepairBotFleeLeash,
                        EffectRange = FoundryOutpostLayout.RepairBotHealRange,
                        EffectAmount = FoundryOutpostLayout.RepairBotHealAmount,
                        CycleSeconds = FoundryOutpostLayout.RepairBotHealCooldownSeconds,
                    });
                    // Demo：维修机（包括阶段过渡召唤的那台）后撤都以常规维修机出生点为牵引中心。
                    spawn.Home = new double2(FoundryOutpostLayout.RepairBotSpawn.Position.x, FoundryOutpostLayout.RepairBotSpawn.Position.y);
                    return true;
                }
                if (e.EnemyTypeId == EnemyCatalog.JammerId)
                {
                    // ER6-ADAPT-01 干扰支援：驻守 + 自卫攻击（不清标记）。
                    spawn = Base(CombatBehavior.HoldFire, CombatUnitKind.Enemy, CombatUnitFlags.Report | CombatUnitFlags.NeedsLos | CombatUnitFlags.WeaponEnabled);
                    spawn.Weapon = Weapon(site, FracturedCityLayout.JammerAttackRange, FracturedCityLayout.JammerAttackDamage, FracturedCityLayout.JammerAttackCooldownSeconds);
                    spawn.Home = new double2(e.Position.x, e.Position.y);
                    return true;
                }
            }
            Log.Error($"[CombatDemoContent] 认不出的敌人 {e.EnemyInstanceId}（{e.EnemyTypeId}，区域 {e.RegionId}）：不进战斗内核（不生成默认替身）。");
            return false;
        }

        /// <summary>
        /// 与记录对账：区域里每条敌人记录都有内核单位（缺的补上），记录已经没有的内核单位移除；血量真相在记录里的（首领）回写镜像。
        /// 只在记录数组被替换时做（首领初始化、召唤、进场），O(记录数)，不是每步。
        /// </summary>
        public static void ReconcileEnemies(CombatSite site, CampaignState state, string regionId)
        {
            if (site == null || state == null)
            {
                return;
            }
            var present = new HashSet<string>(StringComparer.Ordinal);
            if (state.RegionEnemies != null)
            {
                foreach (RegionEnemyRecord e in state.RegionEnemies)
                {
                    if (e == null || e.RegionId != regionId)
                    {
                        continue;
                    }
                    present.Add(e.EnemyInstanceId);
                    if (site.TryGetEnemyUnit(e.EnemyInstanceId, out int unit))
                    {
                        if (site.UnitHasFlag(unit, CombatUnitFlags.ExternalHealth))
                        {
                            site.SyncEnemyFromRecord(e);
                        }
                        continue;
                    }
                    if (TryTemplate(site, state, e, out CombatSpawn spawn))
                    {
                        site.SpawnEnemy(e, spawn);
                    }
                }
            }
            foreach (string id in new List<string>(site.EnemyIds))
            {
                if (!present.Contains(id))
                {
                    site.RemoveEnemy(id);
                }
            }
        }

        /// <summary>首领阶段 → 内核标志（O(3)）：供能节点只在护盾阶段可伤；主核心只在阶段一 / 二可伤并开火；阶段二侧后 +20%。</summary>
        public static void SyncBossFlags(CombatSite site, CampaignState state)
        {
            if (site == null || state == null)
            {
                return;
            }
            RegionRecord region = FoundryOutpostRegion.Find(state);
            CoreBossState s = FoundryOutpostCoreBoss.GetState(region);
            bool coreActive = s == CoreBossState.Phase1 || s == CoreBossState.Phase2;
            if (site.TryGetEnemyUnit(FoundryOutpostLayout.CoreNode1Id, out int n1))
            {
                site.SetUnitFlag(n1, CombatUnitFlags.Invulnerable, s != CoreBossState.Shielded);
            }
            if (site.TryGetEnemyUnit(FoundryOutpostLayout.CoreNode2Id, out int n2))
            {
                site.SetUnitFlag(n2, CombatUnitFlags.Invulnerable, s != CoreBossState.Shielded);
            }
            if (site.TryGetEnemyUnit(FoundryOutpostLayout.MainCoreId, out int core))
            {
                site.SetUnitFlag(core, CombatUnitFlags.Invulnerable, !coreActive);
                site.SetUnitFlag(core, CombatUnitFlags.WeaponEnabled, coreActive);
                site.SetBackHitBonus(core, s == CoreBossState.Phase2 ? FoundryOutpostLayout.Phase2BackHitBonusPct : 0f);
            }
        }

        public static List<float3> Obstacles(IEnumerable<(Vector2 Position, float Radius)> anchors)
        {
            var list = new List<float3>();
            foreach ((Vector2 Position, float Radius) a in anchors)
            {
                list.Add(new float3(a.Position.x, a.Position.y, a.Radius));
            }
            return list;
        }
    }

    /// <summary>破碎都市的战斗结算挂点（Demo FracturedCityRegion / FracturedCityEnemyAi 的事件侧）。</summary>
    public sealed class FracturedCityCombatRules : CombatSiteRules
    {
        public Action<string> OnScanPulse;
        public Action<string> OnEnemyVisualChanged;
        private readonly List<FracturedCityLayout.Anchor> _pois = new List<FracturedCityLayout.Anchor>(FracturedCityLayout.AllAnchors());

        public IReadOnlyList<FracturedCityLayout.Anchor> Pois => _pois;

        public override void OnEnemyDamaged(CombatSite site, RegionEnemyRecord enemy, float damage)
        {
            FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, enemy.Position);
        }

        public override void OnEnemyKilled(CombatSite site, RegionEnemyRecord enemy)
        {
            FracturedCityRegion.OnEnemyKilled(CampaignSession.Current, enemy);
            OnEnemyVisualChanged?.Invoke(enemy.EnemyInstanceId);
        }

        public override void OnMachineMarked(CombatSite site, int logicId, float seconds, RegionEnemyRecord scout)
        {
            CampaignState state = CampaignSession.Current;
            FracturedCityRegion.TryMarkMachine(state, logicId, seconds);
            FracturedCityRegion.BumpAlertFromMark(state); // ERD-ENY-001“呼叫干扰”的最小可用代理。
            if (scout != null)
            {
                OnScanPulse?.Invoke(scout.EnemyInstanceId);
                Log.Info($"[FracturedCityCombat] {scout.EnemyInstanceId} 天线扫描命中机器 {logicId}，已标记。");
            }
        }

        public override void OnMarkCleared(CombatSite site, int logicId, RegionEnemyRecord jammer)
        {
            FracturedCityRegion.TryClearMark(CampaignSession.Current, logicId);
        }

        public override void OnMarkMissed(CombatSite site, RegionEnemyRecord scout)
        {
            if (scout != null)
            {
                Log.Info($"[FracturedCityCombat] {scout.EnemyInstanceId} 天线扫描周期触发，但视线内无目标，未产生标记。");
            }
        }

        public override void OnPoiReached(CombatSite site, int poiIndex, int logicId, Vector2 machinePosition)
        {
            if (poiIndex >= 0 && poiIndex < _pois.Count)
            {
                FracturedCityLayout.Anchor a = _pois[poiIndex];
                FracturedCityRegion.TryDiscover(CampaignSession.Current, a.Id, machinePosition, a.Position);
            }
        }
    }

    /// <summary>铸造前哨外围的战斗结算挂点（Demo FoundryOutpostRegion / FoundryOutpostEnemyAi / FoundryOutpostCoreBoss 的事件侧）。</summary>
    public sealed class FoundryOutpostCombatRules : CombatSiteRules
    {
        public Action<string> OnEnemyVisualChanged;
        private readonly List<FoundryOutpostLayout.Anchor> _pois = new List<FoundryOutpostLayout.Anchor>(FoundryOutpostLayout.AllAnchors());

        public IReadOnlyList<FoundryOutpostLayout.Anchor> Pois => _pois;

        public override void OnEnemyDamaged(CombatSite site, RegionEnemyRecord enemy, float damage)
        {
            FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, enemy.Position);
        }

        public override void OnEnemyKilled(CombatSite site, RegionEnemyRecord enemy)
        {
            FoundryOutpostRegion.OnEnemyKilled(CampaignSession.Current, enemy);
            OnEnemyVisualChanged?.Invoke(enemy.EnemyInstanceId);
        }

        public override bool ApplyExternalEnemyDamage(CombatSite site, RegionEnemyRecord enemy, float damage)
        {
            CampaignState state = CampaignSession.Current;
            bool wasAlive = enemy.IsAlive;
            (bool ok, string reason) = FoundryOutpostCoreBoss.ApplyDamage(state, enemy, damage);
            if (!ok)
            {
                Log.Info($"[FoundryOutpostCombat] 对 {enemy.EnemyInstanceId} 的伤害未结算：{reason}");
            }
            else
            {
                FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, enemy.Position);
            }
            // 阶段可能刚变：节点可伤 / 主核心开火 / 侧后加成随之更新（O(3)）。
            CombatDemoContent.SyncBossFlags(site, state);
            if (wasAlive != enemy.IsAlive)
            {
                OnEnemyVisualChanged?.Invoke(enemy.EnemyInstanceId);
            }
            return ok;
        }

        public override bool ApplyExternalEnemyHeal(CombatSite site, RegionEnemyRecord enemy, float amount)
        {
            return FoundryOutpostRegion.TryHealEnemy(CampaignSession.Current, enemy.EnemyInstanceId, amount);
        }

        public override string InvulnerableDetail(RegionEnemyRecord enemy)
        {
            return FoundryOutpostCoreBoss.DisplayPhaseText(FoundryOutpostRegion.Find(CampaignSession.Current));
        }

        public override void OnPoiReached(CombatSite site, int poiIndex, int logicId, Vector2 machinePosition)
        {
            if (poiIndex >= 0 && poiIndex < _pois.Count)
            {
                FoundryOutpostLayout.Anchor a = _pois[poiIndex];
                FoundryOutpostRegion.TryDiscover(CampaignSession.Current, a.Id, machinePosition, a.Position);
            }
        }
    }

    /// <summary>家园的战斗结算挂点：训练靶自动交战（Demo ER4-PRIM-05 TickAutoEngage 的结算侧）。家园突袭的结算由 FG6-DEF-05～08 接在这里。</summary>
    public sealed class HomeValleyCombatRules : CombatSiteRules
    {
        public Func<int, bool> IsDirectControlled;

        public override void OnEngageRequest(CombatSite site, int logicId)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || !MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || !rec.IsAlive || rec.IsInFactory)
            {
                return; // Demo：厂内机器不参与自动交战。
            }
            if (IsDirectControlled != null && IsDirectControlled(logicId))
            {
                return;
            }
            HomeValleyCombatTargets.TryAttack(state, logicId, HomeValleyCombatTargets.LowThreatTargetId, state.RandomSeed, isAiSource: true);
            HomeValleyCombatTargets.SyncDummy(site, state);
        }

        /// <summary>编队攻击命令打到训练靶：与直控 / 自动交战同一结算出口（<see cref="HomeValleyCombatTargets.TryAttack"/>，isAiSource: false）。</summary>
        public override void ApplyExternalDamageByKey(CombatSite site, string key, float damage, int attackerLogicId)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || key != HomeValleyCombatTargets.LowThreatTargetId || attackerLogicId <= 0)
            {
                return;
            }
            HomeValleyCombatTargets.TryAttack(state, attackerLogicId, key, state.RandomSeed, isAiSource: false);
            HomeValleyCombatTargets.SyncDummy(site, state);
        }
    }
}
