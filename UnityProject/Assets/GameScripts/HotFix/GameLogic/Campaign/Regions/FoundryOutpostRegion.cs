using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER6-FOUNDRY-01：铸造前哨外围固定内容的唯一写入口——敌人死亡、容器打开、关键物/可选
    /// 缓存 Lost/Recovered 全部经本类写 <see cref="RegionRecord"/>/<see cref="RegionEnemyRecord"/>/
    /// <see cref="RegionQuestItemRecord"/>，与 <see cref="FracturedCityRegion"/> 同一纪律。核心分区
    /// （封锁门/两供能节点/主核心）属于 ER6-REGION-01/ER7-CORE-01，本类不实现，见
    /// <see cref="FoundryOutpostLayout"/> 类注释的范围裁剪说明。</summary>
    public static class FoundryOutpostRegion
    {
        public const string RegionId = FoundryOutpostLayout.RegionId;

        private const string UnlockEventId = "region_unlock:" + RegionId;

        public readonly struct ActionResult
        {
            public readonly bool Success;
            public readonly string FailureReason;

            private ActionResult(bool success, string failureReason)
            {
                Success = success;
                FailureReason = failureReason;
            }

            public static ActionResult Ok() => new ActionResult(true, null);
            public static ActionResult Fail(string reason) => new ActionResult(false, reason);
        }

        // ── RegionRecord 查询/播种/解锁 ───────────────────────────────────────

        public static RegionRecord Find(CampaignState state) =>
            state?.RegionRecords?.FirstOrDefault(r => r.RegionId == RegionId);

        public static void EnsureRegionRecordSeeded(CampaignState state)
        {
            if (state == null || Find(state) != null)
            {
                return;
            }
            state.RegionRecords = (state.RegionRecords ?? Array.Empty<RegionRecord>()).Append(new RegionRecord
            {
                RegionId = RegionId,
                State = RegionState.Locked,
                DiscoveredNodes = Array.Empty<string>(),
                DestroyedNodeIds = Array.Empty<string>(),
                LootedContainerIds = Array.Empty<string>(),
                LostQuestSalvageIds = Array.Empty<string>(),
                EnemyAlertLevel = 0f,
                AdaptationId = null,
                ExpeditionCount = 0,
                CoreGateUnlocked = false,
                CoreState = null,
            }).ToArray();
        }

        /// <summary>DEMO-CONTENT-LOCK.md 行12："`foundry_outpost`……首次跨派系蓝图已保存且 ERC-003
        /// 已改造"。与 <see cref="HomeValleySignal.RecomputeUnlock"/> 同一结构（Locked→Available 单向
        /// 终态、EventLedger 一次性反馈）。
        ///
        /// ── 两个条件的检测依据（结构性判定，不新造标记字段）──
        /// "首次跨派系蓝图已保存"＝<see cref="CampaignExposureLedger"/> 已授予过
        /// "exposure:cross_faction_firmware:"前缀事件（<see cref="BlueprintEditorService.TrySave"/>
        /// 唯一写入口，ER6-EXPOSE-01 已验证）；"ERC-003 已改造"＝存在一台 ERC-003 底盘机器，其
        /// <see cref="MachineRecord.BlueprintId"/> 不再是出厂默认 <see cref="HomeValleyLayout.BlueprintErc003Id"/>
        /// ——全仓库唯一改写该字段的生产入口是 <see cref="HomeValleyFactory"/> 第539行的回厂改造完工分支
        /// （已核实 grep 无第二处写点），"蓝图已切换"结构上等价于"已改造过"，不需要额外布尔标记。</summary>
        public static bool RecomputeUnlock(CampaignState state)
        {
            RegionRecord region = Find(state);
            if (region == null || region.State != RegionState.Locked)
            {
                return false;
            }

            bool crossFactionSaved = state.EventLedger != null &&
                state.EventLedger.Any(e => e.EventId != null && e.EventId.StartsWith("exposure:cross_faction_firmware:"));
            if (!crossFactionSaved)
            {
                return false;
            }

            bool erc003Retrofitted = MachineRegistry.AllRecords.Any(m =>
                m.IsAlive && m.ChassisId == HomeValleyLayout.Erc003ChassisId &&
                m.BlueprintId != HomeValleyLayout.BlueprintErc003Id);
            if (!erc003Retrofitted)
            {
                return false;
            }

            region.State = RegionState.Available;
            CampaignEventLedger.TryGrant(state, UnlockEventId, "RegionUnlock", state.PlaySeconds, RegionId);
            Log.Info("[FoundryOutpostRegion] 铸造前哨外围（foundry_outpost）已解锁：跨派系蓝图已保存且 ERC-003 已改造。");
            return true;
        }

        // ── 敌人（护甲机 x2、步进炮 x1、维修机 x1）────────────────────────────

        public static RegionEnemyRecord FindEnemy(CampaignState state, string enemyInstanceId) =>
            state?.RegionEnemies?.FirstOrDefault(e => e.EnemyInstanceId == enemyInstanceId);

        public static void EnsureEnemiesSeeded(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            state.RegionEnemies ??= Array.Empty<RegionEnemyRecord>();
            SeedIfMissing(FoundryOutpostLayout.ArmorBotLeftSpawnId, EnemyCatalog.ArmorBotId,
                FoundryOutpostLayout.ArmorBotLeftSpawn.Position, FoundryOutpostLayout.ArmorBotMaxHealth);
            SeedIfMissing(FoundryOutpostLayout.ArmorBotRightSpawnId, EnemyCatalog.ArmorBotId,
                FoundryOutpostLayout.ArmorBotRightSpawn.Position, FoundryOutpostLayout.ArmorBotMaxHealth);
            SeedIfMissing(FoundryOutpostLayout.StriderSpawnId, EnemyCatalog.StriderId,
                FoundryOutpostLayout.StriderSpawn.Position, FoundryOutpostLayout.StriderMaxHealth);
            SeedIfMissing(FoundryOutpostLayout.RepairBotSpawnId, EnemyCatalog.RepairBotId,
                FoundryOutpostLayout.RepairBotSpawn.Position, FoundryOutpostLayout.RepairBotMaxHealth);

            void SeedIfMissing(string instanceId, string typeId, Vector2 position, float maxHealth)
            {
                if (FindEnemy(state, instanceId) != null)
                {
                    return;
                }
                state.RegionEnemies = state.RegionEnemies.Append(new RegionEnemyRecord
                {
                    EnemyInstanceId = instanceId,
                    RegionId = RegionId,
                    EnemyTypeId = typeId,
                    Position = position,
                    Health = maxHealth,
                    MaxHealth = maxHealth,
                    IsAlive = true,
                    CycleCooldownRemaining = 0f,
                    SecondaryTimer = 0f,
                }).ToArray();
            }
        }

        public static void TickEnemies(CampaignState state, float dt)
        {
            if (state?.RegionEnemies == null || dt <= 0f)
            {
                return;
            }
            RegionRecord region = Find(state);
            if (region != null && region.EnemyAlertLevel > 0f)
            {
                region.EnemyAlertLevel = Mathf.Max(0f, region.EnemyAlertLevel - dt);
            }
        }

        /// <summary>唯一伤害结算入口，与 <see cref="FracturedCityRegion.TryDamageEnemy"/> 同一纪律。
        /// 步进炮死亡首次掉落铸造重炮模块（关键物）；其余敌类型只掉普通废料。</summary>
        public static ActionResult TryDamageEnemy(CampaignState state, string enemyInstanceId, float damage)
        {
            RegionEnemyRecord enemy = FindEnemy(state, enemyInstanceId);
            if (enemy == null)
            {
                return ActionResult.Fail($"敌人 {enemyInstanceId} 不存在。");
            }
            if (!enemy.IsAlive)
            {
                return ActionResult.Fail("目标已阵亡。");
            }
            enemy.Health = Mathf.Max(0f, enemy.Health - Mathf.Max(0f, damage));
            if (enemy.Health <= 0f)
            {
                enemy.IsAlive = false;
                MarkDestroyed(state, enemyInstanceId);
                SpawnEnemyLoot(state, enemy);
                Log.Info($"[FoundryOutpostRegion] 敌人 {enemyInstanceId} 已阵亡。");
            }
            return ActionResult.Ok();
        }

        /// <summary>DEMO-CONTENT-LOCK.md §4.1第4条"步进炮首次击破必产生铸造重炮模块"——只在步进炮
        /// 死亡且此前从未产出过该关键物时掉落（同类型敌人理论上只有一个实例，天然幂等；仍显式判重，
        /// 防御与 <see cref="FracturedCityRegion.TryDestroyListeningNode"/> 同一纪律）。其余敌类型/
        /// 重复击破（结构上不会发生，防御性写法）只掉普通废料。</summary>
        private static void SpawnEnemyLoot(CampaignState state, RegionEnemyRecord enemy)
        {
            if (enemy.EnemyTypeId == EnemyCatalog.StriderId)
            {
                bool alreadyDropped = state.RegionQuestItems != null &&
                    state.RegionQuestItems.Any(q => q.ContentId == FoundryOutpostLayout.CannonModuleContentId);
                if (!alreadyDropped)
                {
                    SpawnQuestItemOnGround(state, FoundryOutpostLayout.CannonModuleContentId, enemy.Position);
                    Log.Info("[FoundryOutpostRegion] 步进炮已阵亡：铸造重炮模块已掉落。");
                }
            }

            int amount = enemy.EnemyTypeId switch
            {
                EnemyCatalog.StriderId => FoundryOutpostLayout.StriderScrapLoot,
                EnemyCatalog.ArmorBotId => FoundryOutpostLayout.ArmorBotScrapLoot,
                EnemyCatalog.RepairBotId => FoundryOutpostLayout.RepairBotScrapLoot,
                _ => 10,
            };
            HomeValleyCargo.SpawnGroundItem(state, RegionId, enemy.Position,
                CampaignEconomyLedger.ResourceScrap, amount, enemy.EnemyInstanceId + ":scrap");
        }

        /// <summary>敌人对玩家机器造成伤害的唯一入口，与 <see cref="FracturedCityRegion.TryEnemyAttackMachine"/>
        /// 同一纪律。</summary>
        public static ActionResult TryEnemyAttackMachine(CampaignState state, string enemyInstanceId, int targetLogicId, float damage)
        {
            RegionEnemyRecord enemy = FindEnemy(state, enemyInstanceId);
            if (enemy == null || !enemy.IsAlive)
            {
                return ActionResult.Fail("攻击者不存在或已阵亡。");
            }
            if (!MachineRegistry.TryGetRecord(targetLogicId, out MachineRecord machine) || !machine.IsAlive)
            {
                return ActionResult.Fail("目标机器不存在或已阵亡。");
            }
            if (machine.RegionId != RegionId)
            {
                return ActionResult.Fail("目标机器不在本区域。");
            }
            if (machine.FactionId != "Player")
            {
                return ActionResult.Fail("目标非玩家阵营，拒绝攻击（阵营判断）。");
            }

            MachineOpResult result = MachineRegistry.ApplyDamage(targetLogicId, damage);
            if (!result.Success)
            {
                return ActionResult.Fail(result.Message);
            }
            Log.Info($"[FoundryOutpostRegion] 敌人 {enemyInstanceId} 命中机器 {targetLogicId}，伤害 {damage:F1}。");
            return ActionResult.Ok();
        }

        /// <summary>直控攻击的锥形命中判定，同 <see cref="FracturedCityRegion.TryFindEnemyInAim"/>。</summary>
        public static RegionEnemyRecord TryFindEnemyInAim(CampaignState state, Vector2 origin, Vector2 aimDirection)
        {
            if (state?.RegionEnemies == null || aimDirection.sqrMagnitude < 1e-6f)
            {
                return null;
            }
            Vector2 dirNorm = aimDirection.normalized;
            float cosHalf = Mathf.Cos(FoundryOutpostLayout.DirectAttackAimHalfAngleDeg * Mathf.Deg2Rad);
            foreach (RegionEnemyRecord enemy in state.RegionEnemies)
            {
                if (enemy.RegionId != RegionId || !enemy.IsAlive)
                {
                    continue;
                }
                Vector2 toTarget = enemy.Position - origin;
                float dist = toTarget.magnitude;
                if (dist > FoundryOutpostLayout.DirectAttackRange || dist < 0.01f)
                {
                    continue;
                }
                float cosAngle = Vector2.Dot(dirNorm, toTarget.normalized);
                if (cosAngle >= cosHalf)
                {
                    return enemy;
                }
            }
            return null;
        }

        /// <summary>护甲机"正面减伤40%，侧后不减"的命中方向判定——攻击方向（攻击者→护甲机）与护甲机
        /// 固定朝向（<see cref="FoundryOutpostLayout.ArmorBotLeftFacing"/>/<c>RightFacing</c>，驻守
        /// 掩体不转身）夹角在 <see cref="FoundryOutpostLayout.ArmorBotFrontalHalfAngleDeg"/> 锥角内即
        /// "正面命中"。非护甲机固定恒为 false（无意义）。</summary>
        public static bool IsFrontalHit(RegionEnemyRecord enemy, Vector2 attackerPosition)
        {
            if (enemy == null || enemy.EnemyTypeId != EnemyCatalog.ArmorBotId)
            {
                return false;
            }
            Vector2 facing = enemy.EnemyInstanceId == FoundryOutpostLayout.ArmorBotLeftSpawnId
                ? FoundryOutpostLayout.ArmorBotLeftFacing
                : FoundryOutpostLayout.ArmorBotRightFacing;
            Vector2 toAttacker = attackerPosition - enemy.Position;
            if (toAttacker.sqrMagnitude < 1e-6f)
            {
                return true; // 贴脸命中视为正面，不给一个不可能出现的方向留歧义。
            }
            float cosAngle = Vector2.Dot(facing.normalized, toAttacker.normalized);
            float cosHalf = Mathf.Cos(FoundryOutpostLayout.ArmorBotFrontalHalfAngleDeg * Mathf.Deg2Rad);
            return cosAngle >= cosHalf;
        }

        /// <summary>唯一攻击结算入口——与 <see cref="FracturedCityRegion.TryAttackEnemy"/> 同一装配伤害
        /// 出口（<see cref="MachineLoadoutRegistry"/>），玩家策略命令/直控共用（AC-REA-003"AI/玩家同
        /// 装配"结构性成立）。护甲机命中后按 <see cref="IsFrontalHit"/> 应用正面减伤，铸造重炮走独立
        /// <see cref="CannonCombat"/> 状态机（与破碎都市同一分支顺序：必须在 HasCombatOutput 早退检查
        /// 之前判定 HasCannonPrimary，理由同 <see cref="FracturedCityRegion.TryAttackEnemy"/> 类注释）。</summary>
        public static ActionResult TryAttackEnemy(CampaignState state, int attackerLogicId, string enemyInstanceId,
            int seed, bool isAiSource, Vector2 attackerPosition, Func<Vector2, Vector2, bool> isReachable = null)
        {
            if (state == null)
            {
                return ActionResult.Fail("没有活动的铸造前哨会话。");
            }
            RegionEnemyRecord enemy = FindEnemy(state, enemyInstanceId);
            if (enemy == null)
            {
                return ActionResult.Fail($"敌人 {enemyInstanceId} 不存在。");
            }
            if (!enemy.IsAlive)
            {
                return ActionResult.Fail("目标已阵亡。");
            }

            MachineCombatResolution resolution = isAiSource
                ? MachineLoadoutRegistry.ResolveForAi(state, attackerLogicId, seed)
                : MachineLoadoutRegistry.ResolveForDirectControl(state, attackerLogicId, seed);
            if (!resolution.Success)
            {
                return ActionResult.Fail(resolution.FailureReason);
            }

            if (resolution.Preview.HasCannonPrimary)
            {
                // CannonCombat 是与 FracturedCityRegion 共享的独立状态机，返回类型是它自己的
                // ActionResult（同名不同类型，Success/FailureReason 结构相同）——本方法域内统一用
                // 自己的 ActionResult，这里做一次纯字段转换，不改 CannonCombat 本身（ER6-REACT-02
                // 已验收，不引入非必要改动）。
                FracturedCityRegion.ActionResult cannonResult = CannonCombat.TryFire(
                    state, attackerLogicId, enemyInstanceId, resolution, isReachable);
                return cannonResult.Success ? ActionResult.Ok() : ActionResult.Fail(cannonResult.FailureReason);
            }

            if (!resolution.Preview.HasCombatOutput)
            {
                return ActionResult.Fail("当前装配没有可攻击的主武器出口（8号汇槽为空）。");
            }

            float damage = Mathf.Max(0f, resolution.Preview.TotalNormalizedDamage);
            if (IsFrontalHit(enemy, attackerPosition))
            {
                damage = EnemyCatalog.ComputeFrontalArmorReducedDamage(damage, isFrontalHit: true);
            }

            return TryDamageEnemy(state, enemyInstanceId, damage);
        }

        /// <summary>铸造维修机"优先救低血同伴"的唯一写入口——钳制到 <see cref="RegionEnemyRecord.MaxHealth"/>，
        /// 对已阵亡/满血目标拒绝（幂等，避免 AI 每帧重复调用产生无意义日志）。</summary>
        public static bool TryHealEnemy(CampaignState state, string targetInstanceId, float amount)
        {
            RegionEnemyRecord target = FindEnemy(state, targetInstanceId);
            if (target == null || !target.IsAlive || target.Health >= target.MaxHealth)
            {
                return false;
            }
            target.Health = Mathf.Min(target.MaxHealth, target.Health + Mathf.Max(0f, amount));
            return true;
        }

        // ── 容器/关键物/可选缓存 ──────────────────────────────────────────────

        private static void MarkDestroyed(CampaignState state, string id)
        {
            RegionRecord region = Find(state);
            if (region == null)
            {
                return;
            }
            region.DestroyedNodeIds ??= Array.Empty<string>();
            if (!region.DestroyedNodeIds.Contains(id))
            {
                region.DestroyedNodeIds = region.DestroyedNodeIds.Append(id).ToArray();
            }
        }

        private static void MarkLooted(CampaignState state, string containerId)
        {
            RegionRecord region = Find(state);
            if (region == null)
            {
                return;
            }
            region.LootedContainerIds ??= Array.Empty<string>();
            if (!region.LootedContainerIds.Contains(containerId))
            {
                region.LootedContainerIds = region.LootedContainerIds.Append(containerId).ToArray();
            }
        }

        public static ActionResult TryOpenCrate(CampaignState state, string crateId)
        {
            Vector2 position = crateId switch
            {
                FoundryOutpostLayout.Crate1Id => FoundryOutpostLayout.Crate1.Position,
                FoundryOutpostLayout.Crate2Id => FoundryOutpostLayout.Crate2.Position,
                FoundryOutpostLayout.Crate3Id => FoundryOutpostLayout.Crate3.Position,
                _ => Vector2.zero,
            };
            bool known = crateId == FoundryOutpostLayout.Crate1Id || crateId == FoundryOutpostLayout.Crate2Id || crateId == FoundryOutpostLayout.Crate3Id;
            if (!known)
            {
                return ActionResult.Fail($"未知箱子 {crateId}。");
            }
            RegionRecord region = Find(state);
            if (region == null)
            {
                return ActionResult.Fail("铸造前哨尚未初始化。");
            }
            if (region.LootedContainerIds != null && region.LootedContainerIds.Contains(crateId))
            {
                return ActionResult.Fail("该箱子已打开过。");
            }
            MarkLooted(state, crateId);
            HomeValleyCargo.SpawnGroundItem(state, RegionId, position,
                CampaignEconomyLedger.ResourceScrap, FoundryOutpostLayout.CrateScrapAmount, crateId + ":scrap");
            Log.Info($"[FoundryOutpostRegion] 箱子 {crateId} 已打开：{FoundryOutpostLayout.CrateScrapAmount} 废料已落地。");
            return ActionResult.Ok();
        }

        /// <summary>三种可选技术缓存的拾取点——幂等（同一 cacheId 只产一次地面物）。与关键物共用
        /// <see cref="RegionQuestItemRecord"/> 生命周期，但不进 <see cref="RecoveryLockerCheck"/> 保底
        /// 范围（"此机制不重生已领取的废料或可选奖励"，DEMO-CONTENT-LOCK.md §4.1第4条）。</summary>
        public static ActionResult TryOpenTechCache(CampaignState state, string cacheId)
        {
            (string contentId, Vector2 position) = cacheId switch
            {
                FoundryOutpostLayout.ArmorCacheId => (FoundryOutpostLayout.ArmorCacheContentId, FoundryOutpostLayout.ArmorCache.Position),
                FoundryOutpostLayout.HeatSinkCacheId => (FoundryOutpostLayout.HeatSinkCacheContentId, FoundryOutpostLayout.HeatSinkCache.Position),
                FoundryOutpostLayout.ArmorPierceCacheId => (FoundryOutpostLayout.ArmorPierceCacheContentId, FoundryOutpostLayout.ArmorPierceCache.Position),
                _ => (null, Vector2.zero),
            };
            if (contentId == null)
            {
                return ActionResult.Fail($"未知技术缓存 {cacheId}。");
            }
            RegionRecord region = Find(state);
            if (region == null)
            {
                return ActionResult.Fail("铸造前哨尚未初始化。");
            }
            if (region.LootedContainerIds != null && region.LootedContainerIds.Contains(cacheId))
            {
                return ActionResult.Fail("该技术缓存已领取过。");
            }
            MarkLooted(state, cacheId);
            SpawnQuestItemOnGround(state, contentId, position);
            Log.Info($"[FoundryOutpostRegion] 技术缓存 {cacheId} 已领取：{contentId} 已落地。");
            return ActionResult.Ok();
        }

        public static bool TryDiscover(CampaignState state, string poiId, Vector2 machinePosition, Vector2 poiPosition)
        {
            if (Vector2.Distance(machinePosition, poiPosition) > FoundryOutpostLayout.PoiDiscoveryRadius)
            {
                return false;
            }
            RegionRecord region = Find(state);
            if (region == null)
            {
                return false;
            }
            region.DiscoveredNodes ??= Array.Empty<string>();
            if (region.DiscoveredNodes.Contains(poiId))
            {
                return false;
            }
            region.DiscoveredNodes = region.DiscoveredNodes.Append(poiId).ToArray();
            return true;
        }

        public static RegionQuestItemRecord FindQuestItem(CampaignState state, string salvageInstanceId) =>
            state?.RegionQuestItems?.FirstOrDefault(q => q.SalvageInstanceId == salvageInstanceId);

        private static void SpawnQuestItemOnGround(CampaignState state, string contentId, Vector2 position)
        {
            string salvageInstanceId = $"{RegionId}:{contentId}:{Guid.NewGuid():N}".Substring(0, 40);
            state.RegionQuestItems = (state.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()).Append(
                new RegionQuestItemRecord
                {
                    SalvageInstanceId = salvageInstanceId,
                    RegionId = RegionId,
                    ContentId = contentId,
                    State = RegionQuestItemState.OnGround,
                    CarrierLogicId = 0,
                    Position = position,
                }).ToArray();
        }

        public static ActionResult TryCollectQuestItem(CampaignState state, string salvageInstanceId, int carrierLogicId)
        {
            RegionQuestItemRecord item = FindQuestItem(state, salvageInstanceId);
            if (item == null || item.State != RegionQuestItemState.OnGround)
            {
                return ActionResult.Fail("地面上没有该物品。");
            }
            if (!MachineRegistry.TryGetRecord(carrierLogicId, out MachineRecord machine) || !machine.IsAlive ||
                machine.RegionId != RegionId)
            {
                return ActionResult.Fail("携带机器不存在/已阵亡/不在本区域。");
            }
            item.State = RegionQuestItemState.Carried;
            item.CarrierLogicId = carrierLogicId;
            item.Position = default;
            Log.Info($"[FoundryOutpostRegion] {item.ContentId} 已装入机器 {carrierLogicId} 货舱。");
            return ActionResult.Ok();
        }

        /// <summary>撤离/结算：与 <see cref="FracturedCityRegion.ResolveExtraction"/> 同一纪律——本 Story
        /// 只交付数据层结算（ER6-REGION-01 将在此基础上加撤离确认 UI/封锁门三灯显示，同
        /// ER5-REGION-01→ER5-RETURN-01 先例）。区域 Cleared 条件是"铸造重炮模块已 Recovered"（三种
        /// 可选缓存不影响 Cleared 判定，"可选缓存不阻断核心门"，DEMO-CONTENT-LOCK.md §4.2第3条）。</summary>
        public static void ResolveExtraction(CampaignState state, IReadOnlyCollection<int> survivingLogicIds)
        {
            RegionRecord region = Find(state);
            if (region == null || state.RegionQuestItems == null)
            {
                return;
            }

            var survivors = survivingLogicIds != null ? new HashSet<int>(survivingLogicIds) : new HashSet<int>();
            var lost = new List<string>(region.LostQuestSalvageIds ?? Array.Empty<string>());

            foreach (RegionQuestItemRecord item in state.RegionQuestItems)
            {
                if (item.RegionId != RegionId || item.State != RegionQuestItemState.Carried)
                {
                    continue;
                }
                if (survivors.Contains(item.CarrierLogicId))
                {
                    item.State = RegionQuestItemState.Recovered;
                    Log.Info($"[FoundryOutpostRegion] {item.ContentId}（{item.SalvageInstanceId}）撤离成功，已 Recovered。");
                }
                else
                {
                    item.State = RegionQuestItemState.Lost;
                    if (!lost.Contains(item.SalvageInstanceId))
                    {
                        lost.Add(item.SalvageInstanceId);
                    }
                    Log.Info($"[FoundryOutpostRegion] {item.ContentId}（{item.SalvageInstanceId}）随携带机器阵亡丢失，已标记 Lost。");
                }
            }
            region.LostQuestSalvageIds = lost.ToArray();

            bool cannonRecovered = state.RegionQuestItems.Any(q =>
                q.ContentId == FoundryOutpostLayout.CannonModuleContentId && q.State == RegionQuestItemState.Recovered);
            if (cannonRecovered && region.State != RegionState.Cleared)
            {
                region.State = RegionState.Cleared;
                CampaignEventLedger.TryGrant(state, "region_cleared:" + RegionId, "RegionCleared", state.PlaySeconds, RegionId);
                Log.Info("[FoundryOutpostRegion] 铸造重炮模块已 Recovered：铸造前哨外围标记 Cleared。");
            }
        }

        /// <summary>关键物恢复柜——只覆盖铸造重炮模块（唯一关键物），不覆盖三种可选缓存
        /// （DEMO-CONTENT-LOCK.md §4.1第4条"此机制不重生已领取的废料或可选奖励"）。同
        /// <see cref="FracturedCityRegion.RecoveryLockerCheck"/> 同一纪律。</summary>
        public static void RecoveryLockerCheck(CampaignState state)
        {
            if (state?.RegionQuestItems == null)
            {
                return;
            }
            const string contentId = FoundryOutpostLayout.CannonModuleContentId;
            bool everRecovered = state.RegionQuestItems.Any(q => q.ContentId == contentId && q.State == RegionQuestItemState.Recovered);
            if (everRecovered)
            {
                return;
            }
            bool inPlay = state.RegionQuestItems.Any(q => q.ContentId == contentId &&
                (q.State == RegionQuestItemState.OnGround || q.State == RegionQuestItemState.Carried));
            bool everLost = state.RegionQuestItems.Any(q => q.ContentId == contentId && q.State == RegionQuestItemState.Lost);
            if (inPlay || !everLost)
            {
                return;
            }
            SpawnQuestItemOnGround(state, contentId, FoundryOutpostLayout.RecoveryLocker.Position);
            Log.Info("[FoundryOutpostRegion] 关键物恢复柜：铸造重炮模块已重生成保底件。");
        }

        // ── 自检 ──────────────────────────────────────────────────────────

        public static List<string> SelfCheckNoDuplicates(CampaignState state)
        {
            var violations = new List<string>();
            int enemyCount = state?.RegionEnemies?.Count(e => e.RegionId == RegionId) ?? 0;
            if (enemyCount != 4)
            {
                violations.Add($"铸造前哨外围敌人实例数应为 4（2 护甲机 + 1 步进炮 + 1 维修机），实际 {enemyCount}。");
            }
            var dupCheck = state?.RegionEnemies?.Where(e => e.RegionId == RegionId)
                .GroupBy(e => e.EnemyInstanceId).Where(g => g.Count() > 1).ToList();
            if (dupCheck != null && dupCheck.Count > 0)
            {
                violations.Add($"存在重复的 EnemyInstanceId：{string.Join(",", dupCheck.Select(g => g.Key))}");
            }
            return violations;
        }
    }
}
