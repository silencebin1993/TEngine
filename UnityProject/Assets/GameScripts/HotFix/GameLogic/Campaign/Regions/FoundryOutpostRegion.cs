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

            // ER6-ADAPT-01：基线4只敌人播种完成后，按本区域已锁定的 AdaptationId 增/撤反制增援槽位
            // （Flanker/JammerSupport 各一组）。必须在基线播种之后调用，保证幂等 SeedIfMissing 不受
            // 影响；也必须在 Enter() 每次调用（含 resume）时都执行一遍——已锁定值不变时是纯粹的
            // no-op（Reconcile 内部先判断实例是否已存在且类型匹配）。
            ReconcileAdaptiveSupportEnemy(state, Find(state));
            // ER7-CORE-01：90暴露核心入口增援——ER6-ADAPT-01 当时只预告，本方法是真正的实装点
            // （独立于上面 Flanker/JammerSupport 那条 adaptation 增援槽位，"不把它伪装成adaptation"）。
            ReconcileCoreReinforcement(state, Find(state));

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

        /// <summary>ER6-ADAPT-01：反制增援槽位的唯一增/撤入口——<see cref="RegionRecord.AdaptationId"/>
        /// 决定当前应该存在哪一个（Flanker→护甲机类型的 <see cref="FoundryOutpostLayout.FlankerSpawnId"/>，
        /// JammerSupport→复用 <see cref="EnemyCatalog.JammerId"/> 的 <see cref="FoundryOutpostLayout.JammerSupportSpawnId"/>，
        /// None/HeatResistant→都不需要）。每次 <see cref="EnsureEnemiesSeeded"/>（即每次真实 Enter，包含
        /// resume）调用一遍：已存在且类型匹配则不动（保留其当前血量/存活状态，不是每次重置）；不该存在
        /// 的槽位如果还留着（同一 foundry_outpost 跨多次出征、适应从 Flanker 换成 JammerSupport 等场景）
        /// 就撤掉——这一步只会在 Enter() 时（两次真实出征之间）发生，不会在同一场战斗进行到一半时把
        /// 敌人凭空撤走，与"战中不偷换"要求不冲突（锁定的是 AdaptationId 本身，不是这个槽位实例）。</summary>
        private static void ReconcileAdaptiveSupportEnemy(CampaignState state, RegionRecord region)
        {
            if (state == null || region == null)
            {
                return;
            }

            string requiredInstanceId = region.AdaptationId switch
            {
                AdaptationCatalog.Flanker => FoundryOutpostLayout.FlankerSpawnId,
                AdaptationCatalog.JammerSupport => FoundryOutpostLayout.JammerSupportSpawnId,
                _ => null,
            };

            RemoveAdaptiveSlotIfStale(state, FoundryOutpostLayout.FlankerSpawnId,
                keep: requiredInstanceId == FoundryOutpostLayout.FlankerSpawnId);
            RemoveAdaptiveSlotIfStale(state, FoundryOutpostLayout.JammerSupportSpawnId,
                keep: requiredInstanceId == FoundryOutpostLayout.JammerSupportSpawnId);

            if (requiredInstanceId == null || FindEnemy(state, requiredInstanceId) != null)
            {
                return; // 当前适应不需要增援槽位，或需要的槽位已经存在（类型经上面两步保证正确）。
            }

            bool isJammer = requiredInstanceId == FoundryOutpostLayout.JammerSupportSpawnId;
            string enemyTypeId = isJammer ? EnemyCatalog.JammerId : EnemyCatalog.ArmorBotId;
            Vector2 position = isJammer ? FoundryOutpostLayout.JammerSupportSpawn.Position : FoundryOutpostLayout.FlankerSpawn.Position;
            float maxHealth = isJammer ? FracturedCityLayout.JammerMaxHealth : FoundryOutpostLayout.ArmorBotMaxHealth;

            state.RegionEnemies = (state.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Append(new RegionEnemyRecord
            {
                EnemyInstanceId = requiredInstanceId,
                RegionId = RegionId,
                EnemyTypeId = enemyTypeId,
                Position = position,
                Health = maxHealth,
                MaxHealth = maxHealth,
                IsAlive = true,
                CycleCooldownRemaining = 0f,
                SecondaryTimer = 0f,
            }).ToArray();
            Log.Info($"[FoundryOutpostRegion] 反制增援已布防：{requiredInstanceId}（{enemyTypeId}），对应适应 {region.AdaptationId}。");
        }

        private static void RemoveAdaptiveSlotIfStale(CampaignState state, string instanceId, bool keep)
        {
            if (keep || state?.RegionEnemies == null)
            {
                return;
            }
            if (state.RegionEnemies.Any(e => e.EnemyInstanceId == instanceId))
            {
                state.RegionEnemies = state.RegionEnemies.Where(e => e.EnemyInstanceId != instanceId).ToArray();
                Log.Info($"[FoundryOutpostRegion] 反制增援已撤下：{instanceId}（适应已切换，不再需要）。");
            }
        }

        /// <summary>ER7-CORE-01：90暴露核心入口增援护甲机——ER6-ADAPT-01 出发页预告的"核心战一台护甲机
        /// 增援"在这里真正落地。条件＝核心门已解锁（三灯全亮，否则这次出发根本还打不到核心）且暴露
        /// 已越过90阈值（<see cref="CampaignExposureLedger.HasReachedCoreReinforcement"/>）。与
        /// <see cref="ReconcileAdaptiveSupportEnemy"/> 同一增/撤纪律，但完全独立（不是三种 adaptation
        /// 之一，不占用 Flanker/JammerSupport 的槽位判定）。</summary>
        private static void ReconcileCoreReinforcement(CampaignState state, RegionRecord region)
        {
            if (state == null || region == null)
            {
                return;
            }
            bool shouldExist = region.State != RegionState.Locked
                && CanEnterCoreZone(state).Success
                && CampaignExposureLedger.HasReachedCoreReinforcement(state);
            bool exists = FindEnemy(state, FoundryOutpostLayout.CoreReinforcementSpawnId) != null;
            if (shouldExist == exists)
            {
                return;
            }
            if (!shouldExist)
            {
                state.RegionEnemies = state.RegionEnemies.Where(e => e.EnemyInstanceId != FoundryOutpostLayout.CoreReinforcementSpawnId).ToArray();
                return;
            }
            state.RegionEnemies = (state.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Append(new RegionEnemyRecord
            {
                EnemyInstanceId = FoundryOutpostLayout.CoreReinforcementSpawnId,
                RegionId = RegionId,
                EnemyTypeId = EnemyCatalog.ArmorBotId,
                Position = FoundryOutpostLayout.CoreReinforcementSpawn.Position,
                Health = FoundryOutpostLayout.ArmorBotMaxHealth,
                MaxHealth = FoundryOutpostLayout.ArmorBotMaxHealth,
                IsAlive = true,
                CycleCooldownRemaining = 0f,
                SecondaryTimer = 0f,
            }).ToArray();
            Log.Info("[FoundryOutpostRegion] 信号暴露突破90：核心入口增援护甲机已布防（出发页预告已兑现）。");
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

            // ER7-CORE-01：节点/主核心是"护盾/阶段"语义，不是"死了掉废料"，全部委托
            // FoundryOutpostCoreBoss.ApplyDamage（唯一伤害/阶段转换判定入口，见该类类注释），不落入
            // 下面的通用阵亡+掉落分支。
            if (enemy.EnemyTypeId == FoundryOutpostLayout.BossNodeTypeId || enemy.EnemyTypeId == FoundryOutpostLayout.BossCoreTypeId)
            {
                (bool bossOk, string bossReason) = FoundryOutpostCoreBoss.ApplyDamage(state, enemy, damage);
                return bossOk ? ActionResult.Ok() : ActionResult.Fail(bossReason);
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
            // ER6-ADAPT-01：侧袭增援复用护甲机类型时用自己的朝向常量，不套用左/右掩体默认三元判定
            // （它站的位置既不是左也不是右掩体，套用会给出错误的正面锥角基准）。
            if (enemy.EnemyInstanceId == FoundryOutpostLayout.FlankerSpawnId)
            {
                facing = FoundryOutpostLayout.FlankerFacing;
            }
            else if (enemy.EnemyInstanceId == FoundryOutpostLayout.CoreReinforcementSpawnId)
            {
                facing = new Vector2(0f, -1f); // 面朝入口方向（同护甲机驻守语义，朝来袭方向）。
            }
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
                //
                // ER6-ADAPT-01：铸造重炮命中护甲机正面时，与普通武器同一套 IsFrontalHit 判定+
                // ArmorBotFrontalReductionPct 基线，但额外经 CannonCombat.ApplyArmorPierce 按"是否熔穿
                // 过载生效"与"目标是否 HeatResistant 适应"调整穿甲——这正是 ER6-REACT-02 当时把
                // targetHeatResistant 参数留空、注释点名"该系统尚未开工"的落地点。
                bool isFrontalArmored = enemy.EnemyTypeId == EnemyCatalog.ArmorBotId && IsFrontalHit(enemy, attackerPosition);
                RegionRecord adaptRegion = Find(state);
                bool heatResistant = adaptRegion != null && adaptRegion.AdaptationId == AdaptationCatalog.HeatResistant;
                // ER7-CORE-01：Phase2 主核心"侧后+20%"——恒为1（不影响护甲机/其它敌人），只在目标是
                // 主核心且当前恰为 Phase2 时才可能大于1，与 HeatResistant 的穿甲折扣是完全独立的两个
                // 乘数（不会互相抵消/叠加错顺序，先穿甲折算减伤比例，再整体乘伤害倍率）。
                float bossMultiplier = FoundryOutpostCoreBoss.ComputeDamageMultiplier(state, enemy, attackerPosition);
                FracturedCityRegion.ActionResult cannonResult = CannonCombat.TryFire(
                    state, attackerLogicId, enemyInstanceId, resolution, isReachable,
                    isFrontalArmoredHit: isFrontalArmored,
                    armorReductionFraction: FoundryOutpostLayout.ArmorBotFrontalReductionPct,
                    targetHeatResistant: heatResistant,
                    damageMultiplier: bossMultiplier);
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
            damage *= FoundryOutpostCoreBoss.ComputeDamageMultiplier(state, enemy, attackerPosition);

            // ER6-REGION-01：标记跳转在外围实战触发（AC-JRN-014）——与 FracturedCityRegion.TryAttackEnemy
            // 同一顺序，"目标已标记时"才跳转，查询顺序在本次命中造成的新标记之前（第一次命中只留标记，
            // 再次命中已标记目标才跳转）。护甲机正面减伤已在上面应用，跳转伤害基于减伤后的伤害值。
            bool wasMarkedBeforeThisHit = IsEnemyMarked(state, enemyInstanceId);

            ActionResult primaryResult = TryDamageEnemy(state, enemyInstanceId, damage);
            if (!primaryResult.Success)
            {
                return primaryResult;
            }

            if (resolution.Preview.HasMarkerFunction && enemy.IsAlive)
            {
                TryMarkEnemy(state, enemyInstanceId, FracturedCityLayout.EnemyMarkDurationSeconds);
            }

            if (resolution.Preview.ReactionId == MechanicalReactionCatalog.ReactionMarkJumpId && wasMarkedBeforeThisHit)
            {
                ApplyMarkJump(state, enemyInstanceId, enemy.Position, damage, isReachable);
            }

            return primaryResult;
        }

        /// <summary>标记跳转链式伤害——与 <see cref="FracturedCityRegion.ApplyMarkJump"/> 同一实现（候选＝
        /// 存活/同区域/已标记/8米内/可达，按距离→稳定 EnemyInstanceId 排序，最多 2 个，每跳伤害为上一跳
        /// 60%，不重复跳转）。复用 <see cref="FracturedCityLayout"/> 的跳转常量——这是通用反应数值
        /// （DEMO-CONTENT-LOCK.md §2.4/§5），不是破碎都市专属，不重复定义第二份常量。</summary>
        private static void ApplyMarkJump(CampaignState state, string primaryEnemyInstanceId, Vector2 primaryPosition,
            float primaryDamage, Func<Vector2, Vector2, bool> isReachable)
        {
            SweepExpiredEnemyMarks(state);
            RegionRecord region = Find(state);
            if (region?.MarkedEnemies == null || state.RegionEnemies == null)
            {
                return;
            }

            var candidates = new List<RegionEnemyRecord>();
            foreach (RegionEnemyRecord candidate in state.RegionEnemies)
            {
                if (candidate.EnemyInstanceId == primaryEnemyInstanceId || !candidate.IsAlive
                    || candidate.RegionId != RegionId)
                {
                    continue;
                }
                if (!IsEnemyMarked(state, candidate.EnemyInstanceId))
                {
                    continue;
                }
                float dist = Vector2.Distance(primaryPosition, candidate.Position);
                if (dist > FracturedCityLayout.MarkJumpRange)
                {
                    continue;
                }
                if (isReachable != null && !isReachable(primaryPosition, candidate.Position))
                {
                    continue;
                }
                candidates.Add(candidate);
            }

            candidates.Sort((a, b) =>
            {
                float da = Vector2.Distance(primaryPosition, a.Position);
                float db = Vector2.Distance(primaryPosition, b.Position);
                int cmp = da.CompareTo(db);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.EnemyInstanceId, b.EnemyInstanceId);
            });

            float jumpDamage = primaryDamage;
            int jumps = Mathf.Min(FracturedCityLayout.MarkJumpMaxTargets, candidates.Count);
            for (int i = 0; i < jumps; i++)
            {
                jumpDamage *= FracturedCityLayout.MarkJumpDamageFalloff;
                TryDamageEnemy(state, candidates[i].EnemyInstanceId, jumpDamage);
                Log.Info($"[FoundryOutpostRegion] 标记跳转：{primaryEnemyInstanceId} → {candidates[i].EnemyInstanceId}，伤害 {jumpDamage:F1}。");
            }
        }

        // ── 敌方标记（ER6-REGION-01：与 FracturedCityRegion.MarkedEnemies 同一结构，各区域
        // RegionRecord 各自一份 MarkedEnemies 数组，互不共享存储）──────────────────────────

        public static bool IsEnemyMarked(CampaignState state, string enemyInstanceId)
        {
            RegionRecord region = Find(state);
            MarkedEnemyRecord mark = region?.MarkedEnemies?.FirstOrDefault(m => m.EnemyInstanceId == enemyInstanceId);
            return mark != null && mark.ExpireAtPlaySeconds > (state?.PlaySeconds ?? 0f);
        }

        public static void TryMarkEnemy(CampaignState state, string enemyInstanceId, float durationSeconds)
        {
            RegionRecord region = Find(state);
            if (region == null)
            {
                return;
            }
            region.MarkedEnemies ??= Array.Empty<MarkedEnemyRecord>();
            MarkedEnemyRecord existing = region.MarkedEnemies.FirstOrDefault(m => m.EnemyInstanceId == enemyInstanceId);
            float expireAt = state.PlaySeconds + durationSeconds;
            if (existing != null)
            {
                existing.ExpireAtPlaySeconds = expireAt;
            }
            else
            {
                region.MarkedEnemies = region.MarkedEnemies.Append(new MarkedEnemyRecord
                {
                    EnemyInstanceId = enemyInstanceId,
                    ExpireAtPlaySeconds = expireAt,
                }).ToArray();
            }
        }

        public static void SweepExpiredEnemyMarks(CampaignState state)
        {
            RegionRecord region = Find(state);
            if (region?.MarkedEnemies == null || region.MarkedEnemies.Length == 0)
            {
                return;
            }
            float now = state.PlaySeconds;
            region.MarkedEnemies = region.MarkedEnemies.Where(m => m.ExpireAtPlaySeconds > now).ToArray();
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

        /// <summary>ER7-CORE-01：从 private 放宽到 internal——<see cref="FoundryOutpostCoreBoss"/>
        /// 需要用同一条关键物生命周期（OnGround→Carried→Recovered/Lost）落地核心数据盒，不新造第二套
        /// 关键物生成逻辑。仍是同程序集内部细节，不对外公开。</summary>
        internal static void SpawnQuestItemOnGround(CampaignState state, string contentId, Vector2 position)
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

        // ── 核心分区封锁门（ER6-REGION-01）───────────────────────────────────

        /// <summary>三灯——DEMO-CONTENT-LOCK.md §4.2第3条"核心区门显示'重炮解析/熔穿过载蓝图保存/
        /// 实装机器'三项状态"。三项都是对既有系统的只读结构性判定（同
        /// <see cref="RecomputeUnlock"/> 类注释"结构性判定，不新造标记字段"同一纪律），每次查询都
        /// 现场重算——不是一次性解锁后就永久为真的存量标记，"只保存未实装"必须能在玩家后续把蓝图
        /// 从现役机上换下后重新回到锁定状态（验收卡第3条字面要求，与区域 RegionState 的单向终态
        /// 不是同一种语义）。</summary>
        public readonly struct CoreGateLights
        {
            /// <summary>灯1："重炮解析"——<see cref="ComponentCatalog.CompCannonId"/> 已进
            /// <see cref="CampaignState.UnlockedContentIds"/>（<see cref="HomeValleyAnalysis.Complete"/>
            /// 解析铸造重炮模块后写入，唯一权威来源）。</summary>
            public readonly bool CannonAnalyzed;
            /// <summary>灯2："熔穿过载蓝图保存"——<see cref="MechanicalReactionCatalog.ReactionMeltOverloadId"/>
            /// 已被 <see cref="BlueprintEditorService.TrySave"/> 充过一次技术数据（
            /// <see cref="BlueprintEditorService.IsReactionCharged"/>，与 ER6-EXPOSE-01 跨派系判定同一
            /// EventLedger 结构性判定手法）——只要求"保存过"，不要求当前仍装在任何机器上。</summary>
            public readonly bool OverloadBlueprintSaved;
            /// <summary>灯3："现役机实装"——存在至少一台存活机器，其当前蓝图版本编译出的 ReactionId
            /// 恰为熔穿过载（不是"保存过某个版本"，是"现在真的挂在某台活着的机器上"）。</summary>
            public readonly bool MachineEquipped;

            public bool AllReady => CannonAnalyzed && OverloadBlueprintSaved && MachineEquipped;

            public CoreGateLights(bool cannonAnalyzed, bool overloadBlueprintSaved, bool machineEquipped)
            {
                CannonAnalyzed = cannonAnalyzed;
                OverloadBlueprintSaved = overloadBlueprintSaved;
                MachineEquipped = machineEquipped;
            }
        }

        public static CoreGateLights ComputeCoreGateLights(CampaignState state)
        {
            if (state == null)
            {
                return new CoreGateLights(false, false, false);
            }

            bool cannonAnalyzed = state.UnlockedContentIds != null &&
                Array.IndexOf(state.UnlockedContentIds, ComponentCatalog.CompCannonId) >= 0;
            bool overloadSaved = BlueprintEditorService.IsReactionCharged(state, MechanicalReactionCatalog.ReactionMeltOverloadId);

            bool machineEquipped = false;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || string.IsNullOrEmpty(m.BlueprintId))
                {
                    continue;
                }
                BlueprintRecord record = BlueprintEditorService.Find(state, m.BlueprintId);
                BlueprintVersionRecord version = record?.Versions?.FirstOrDefault(v => v.Version == m.BlueprintVersion);
                if (version == null)
                {
                    continue;
                }
                BlueprintCircuitBoard board = BlueprintCircuitBoard.FromVersion(version);
                string reactionId = BlueprintCircuitCompiler.DetectReactionId(board);
                if (reactionId == MechanicalReactionCatalog.ReactionMeltOverloadId)
                {
                    machineEquipped = true;
                    break;
                }
            }

            return new CoreGateLights(cannonAnalyzed, overloadSaved, machineEquipped);
        }

        /// <summary>实时刷新 <see cref="RegionRecord.CoreGateUnlocked"/>——供 UI/门锚点每帧读一个
        /// 布尔值而不必各自重新计算三灯，同时保留 <see cref="ComputeCoreGateLights"/> 供需要逐灯明细
        /// 的调用方（准备页/门旁交互文案）使用。</summary>
        public static bool RecomputeCoreGate(CampaignState state)
        {
            RegionRecord region = Find(state);
            if (region == null)
            {
                return false;
            }
            bool ready = ComputeCoreGateLights(state).AllReady;
            region.CoreGateUnlocked = ready;
            return ready;
        }

        /// <summary>封锁门唯一的"能不能通行"判定入口——四重控制（导航阻挡/物理碰撞/交互拒绝/战役
        /// 目标校验，验收卡第2条）全部调用这一个方法，不各自重算一遍三灯，保证四处判断结果永远
        /// 一致。失败文案逐项列出缺项，供 UI/交互提示直接展示。</summary>
        public static ActionResult CanEnterCoreZone(CampaignState state)
        {
            CoreGateLights lights = ComputeCoreGateLights(state);
            RegionRecord region = Find(state);
            if (region != null)
            {
                region.CoreGateUnlocked = lights.AllReady;
            }
            if (lights.AllReady)
            {
                return ActionResult.Ok();
            }
            var missing = new List<string>();
            if (!lights.CannonAnalyzed)
            {
                missing.Add("重炮解析");
            }
            if (!lights.OverloadBlueprintSaved)
            {
                missing.Add("熔穿过载蓝图保存");
            }
            if (!lights.MachineEquipped)
            {
                missing.Add("现役机实装");
            }
            return ActionResult.Fail("核心分区封锁：缺少 " + string.Join("、", missing) + "。");
        }

        /// <summary>某坐标是否已越过核心分区封锁线——与 X 坐标无关（不给"绕路"留任何有限宽度缺口）。
        /// 门锁定时，直控移动/编队 Move 目的地都要用这个判定做裁剪。</summary>
        public static bool IsBeyondCoreGateLine(Vector2 position) => position.y > FoundryOutpostLayout.CoreGateBlockLineY;

        // ── 自检 ──────────────────────────────────────────────────────────

        public static List<string> SelfCheckNoDuplicates(CampaignState state)
        {
            var violations = new List<string>();
            RegionRecord region = Find(state);
            var allEnemies = state?.RegionEnemies?.Where(e => e.RegionId == RegionId).ToList() ?? new List<RegionEnemyRecord>();

            // ── 外围固定内容：基线4 + ER6-ADAPT-01 Flanker/JammerSupport 反制增援（互斥，最多+1）+
            // ER7-CORE-01 90暴露核心入口增援（与反制增援独立，可能同时存在）───────────────────
            bool hasAdaptiveSlot = region != null &&
                (region.AdaptationId == AdaptationCatalog.Flanker || region.AdaptationId == AdaptationCatalog.JammerSupport);
            bool hasCoreReinforcement = allEnemies.Any(e => e.EnemyInstanceId == FoundryOutpostLayout.CoreReinforcementSpawnId);
            int expectedOutskirtsCount = 4 + (hasAdaptiveSlot ? 1 : 0) + (hasCoreReinforcement ? 1 : 0);
            int outskirtsCount = allEnemies.Count(e =>
                e.EnemyInstanceId != FoundryOutpostLayout.CoreNode1Id && e.EnemyInstanceId != FoundryOutpostLayout.CoreNode2Id &&
                e.EnemyInstanceId != FoundryOutpostLayout.MainCoreId && e.EnemyInstanceId != FoundryOutpostLayout.CoreRepairBotSummonId);
            if (outskirtsCount != expectedOutskirtsCount)
            {
                violations.Add($"铸造前哨外围（不含核心分区）敌人实例数应为 {expectedOutskirtsCount}（基线4" +
                    $"{(hasAdaptiveSlot ? "+1 反制增援" : "")}{(hasCoreReinforcement ? "+1 核心入口增援" : "")}），实际 {outskirtsCount}。");
            }

            // ── 核心分区：未初始化恒为0；已初始化必为两节点+主核心3条，Transition后可能再+1维修机 ──
            bool coreInitialized = FoundryOutpostCoreBoss.IsInitialized(region);
            int coreCount = allEnemies.Count(e =>
                e.EnemyInstanceId == FoundryOutpostLayout.CoreNode1Id || e.EnemyInstanceId == FoundryOutpostLayout.CoreNode2Id ||
                e.EnemyInstanceId == FoundryOutpostLayout.MainCoreId || e.EnemyInstanceId == FoundryOutpostLayout.CoreRepairBotSummonId);
            if (!coreInitialized && coreCount != 0)
            {
                violations.Add($"核心分区尚未初始化（CoreState=Locked）但已存在 {coreCount} 条核心实例，数据不一致。");
            }
            if (coreInitialized && coreCount != 3 && coreCount != 4)
            {
                violations.Add($"核心分区已初始化，敌人实例数应为3（两节点+主核心）或4（Transition后+1维修机），实际 {coreCount}。");
            }

            var dupCheck = allEnemies.GroupBy(e => e.EnemyInstanceId).Where(g => g.Count() > 1).ToList();
            if (dupCheck != null && dupCheck.Count > 0)
            {
                violations.Add($"存在重复的 EnemyInstanceId：{string.Join(",", dupCheck.Select(g => g.Key))}");
            }
            return violations;
        }
    }
}
