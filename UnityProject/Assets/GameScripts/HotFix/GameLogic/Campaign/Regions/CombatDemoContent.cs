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
    /// FG0-ARCH-03 起区域敌人与战斗内核的对账 / 首领标志同步处。FG9-DATA-01 起敌人的内核模板不再在这里逐 ID 翻译：
    /// 全部经 <see cref="EnemySpawnBuilder"/> 按敌人表（fg.TbMechEnemy 的读法 + 标签 + 数值）与编成表生成（DEBT-FG0DATA01-02 / DEBT-FG0ARCH03-05 关闭）。
    /// </summary>
    public static class CombatDemoContent
    {
        /// <summary>敌人记录 → 内核单位模板（表驱动，见 <see cref="EnemySpawnBuilder"/>）。表行缺失 / 不完整时可见报错并返回 false（不生成默认替身）。</summary>
        public static bool TryTemplate(CombatSite site, CampaignState state, RegionEnemyRecord e, out CombatSpawn spawn)
        {
            spawn = default;
            if (site == null || e == null)
            {
                return false;
            }
            return EnemySpawnBuilder.TryBuild(site, state, e, RegionEnemySites.For(e.RegionId), out spawn);
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

        /// <summary>首领阶段 → 内核标志（O(3)）：供能节点只在护盾阶段可伤；主核心在阶段一 / 二 / 对空形态 / 狂暴可伤；
        /// 对地开火只在阶段一 / 二 / 狂暴（对空形态改为只打空中目标，FG9-FAC-02）；阶段二与狂暴侧后 +20%；狂暴开火间隔缩短。</summary>
        public static void SyncBossFlags(CombatSite site, CampaignState state)
        {
            if (site == null || state == null)
            {
                return;
            }
            RegionRecord region = FoundryOutpostRegion.Find(state);
            CoreBossState s = FoundryOutpostCoreBoss.GetState(region);
            bool coreVulnerable = s == CoreBossState.Phase1 || s == CoreBossState.Phase2 || s == CoreBossState.AntiAir || s == CoreBossState.Berserk;
            bool groundFire = s == CoreBossState.Phase1 || s == CoreBossState.Phase2 || s == CoreBossState.Berserk;
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
                site.SetUnitFlag(core, CombatUnitFlags.Invulnerable, !coreVulnerable);
                site.SetUnitFlag(core, CombatUnitFlags.WeaponEnabled, groundFire);
                site.SetBackHitBonus(core, FoundryOutpostCoreBoss.HasBackHitBonus(s) ? FoundryOutpostLayout.Phase2BackHitBonusPct : 0f);
                // FG9-FAC-02：阶段三·对空形态——所有炮口转向天空（内核按敌人表的齐射参数只打无人机与飞行中的弹体）。
                site.SetAirMode(core, s == CoreBossState.AntiAir);
                // 狂暴：开火间隔 × boss.foundry.berserk_cooldown_mult（其余阶段恢复敌人表原值）。武器参数没变时 O(1) 跳过。
                if (EnemyDefs.TryGet(FoundryOutpostLayout.BossCoreTypeId, out GameConfig.fg.MechEnemy row) && row.WeaponRange > 0f
                    && site.TryGetUnitWeapon(core, out CombatWeapon current))
                {
                    float want = row.WeaponCooldown * (s == CoreBossState.Berserk ? FoundryOutpostCoreBoss.BerserkCooldownMult : 1f);
                    if (!Mathf.Approximately(current.Cooldown, want))
                    {
                        CombatWeapon w = EnemySpawnBuilder.WeaponOf(state, row);
                        w.Cooldown = want;
                        site.SetUnitWeapon(core, w);
                    }
                }
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

        /// <summary>
        /// 旧档快照里训练靶还是“外部扣血”（ExternalHealth）时排在队列里的扣血请求（读档后 <see cref="HomeValleyCombatTargets.EnsureDummyUnit"/> 已把靶子迁成内核扣血，
        /// 之后不会再产生）。FG6-DEF-01 审查修复（P0）：伤害内核已经算好，直接按这份伤害记一条命中（与“受伤”事件同一出口 OnKernelHit）——
        /// 不再经 <see cref="HomeValleyCombatTargets.TryAttack"/> 另开一发（那会在事件处理中重入开火，还多打一发）。
        /// </summary>
        public override void ApplyExternalDamageByKey(CombatSite site, string key, float damage, int attackerLogicId)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null || key != HomeValleyCombatTargets.LowThreatTargetId || attackerLogicId <= 0)
            {
                return;
            }
            CombatTargetRecord target = HomeValleyCombatTargets.Find(state, key);
            if (target == null || target.Health <= 0f)
            {
                return;
            }
            float after = Mathf.Max(0f, target.Health - Mathf.Max(0f, damage));
            bool direct = IsDirectControlled != null && IsDirectControlled(attackerLogicId);
            HomeValleyCombatTargets.OnKernelHit(state, key, Mathf.Max(0f, damage), after, attackerLogicId, false, direct);
            HomeValleyCombatTargets.SyncDummy(site, state);
        }

        /// <summary>FG6-DEF-01（DEBT-FG2FW02-07）：训练靶的血量在家园战斗内核里——编队攻击 / 直控 / 自动交战都经内核开火（读法在靶上生效），结算结果交回记录。</summary>
        public override void OnKeyedTargetDamaged(CombatSite site, string key, float damage, float healthAfter, int attackerLogicId, bool killed)
        {
            CampaignState state = CampaignSession.Current;
            if (Env.EmNoiseService.IsEmitterKey(key))
            {
                // FG9-FAC-01：电磁噪声发射器被打空——这一片噪声消失（受伤只在内核里扣，不另记）。
                if (killed)
                {
                    FeedbackCues.RaiseAt(FeedbackCueId.EnemyDestroyed, site.TryGetEnemyPosition(key, out Vector2 at) ? at : Vector2.zero);
                    Env.EmNoiseService.OnEmitterDestroyed(state, key);
                }
                else
                {
                    FeedbackCues.RaiseAt(FeedbackCueId.EnemyHit, site.TryGetEnemyPosition(key, out Vector2 hit) ? hit : Vector2.zero);
                }
                return;
            }
            if (state == null || key != HomeValleyCombatTargets.LowThreatTargetId)
            {
                return;
            }
            bool direct = attackerLogicId > 0 && IsDirectControlled != null && IsDirectControlled(attackerLogicId);
            HomeValleyCombatTargets.OnKernelHit(state, key, damage, healthAfter, attackerLogicId, killed, direct);
        }

        /// <summary>FG0-ARCH-06：工作地点无法到达——工单转为等待（带原因，30 秒后重试），并发一条可定位的“无法到达”通知。</summary>
        public override void OnWorkBlocked(CombatSite site, int logicId, BinGames.Sim.Nav.NavFailReason reason, Vector2 at)
        {
            HomeValleyWorkOrders.OnWorkUnreachable(CampaignSession.Current, logicId, reason, at);
        }
    }
}
