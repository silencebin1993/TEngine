using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Content;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER7-CORE-01 STORY-EXECUTION-CARDS.md：主核心 Boss 战——两供能节点+主核心状态机的唯一
    /// 写入口。仍是同一 <c>foundry_outpost</c> 会话内部的第三个空间子区（ER6-REGION-01 已确立"核心
    /// 分区门禁在外围场景内部解决，不是出发去新地图"），不新开 Controller，挂在
    /// <see cref="FoundryOutpostController"/> 既有 Update 循环里。
    ///
    /// ── 两供能节点/主核心是 RegionEnemyRecord，不是新数据类型 ──
    /// EnemyTypeId 为 <see cref="FoundryOutpostLayout.BossNodeTypeId"/>/<see cref="FoundryOutpostLayout.BossCoreTypeId"/>
    /// 的三条 <see cref="RegionEnemyRecord"/>，复用它已有的 Health/MaxHealth/IsAlive/
    /// CycleCooldownRemaining 字段，与既有 <see cref="FoundryOutpostRegion.TryAttackEnemy"/>/
    /// <see cref="CannonCombat.TryFire"/> 伤害结算路径完全共用（含铸造重炮/熔穿过载/标记等既有反应），
    /// 不新造第二套伤害管线——"熔穿过载在本战必须能实际触发一次"因此结构上直接成立，不需要专门为
    /// Boss 战重新接一遍反应判定。<see cref="FoundryOutpostRegion.TryDamageEnemy"/>/
    /// <see cref="FracturedCityRegion.TryDamageEnemy"/>（铸造重炮命中固定走后者，见该类类注释）都在
    /// 顶部拦截这两个 EnemyTypeId，转发到本类 <see cref="ApplyDamage"/>，不落入"死了掉废料"的通用
    /// 阵亡分支——护盾/阶段语义与掉落完全不同。
    ///
    /// ── 状态机（RegionRecord.CoreState 唯一写入口）──
    /// <see cref="CoreBossState"/> 六态，合法转换 Locked→Shielded→Phase1→Transition→Phase2→Destroyed
    /// 是唯一路径；<see cref="TryTransition"/> 拒绝任何非法跳转并打 <see cref="Log.Error"/> 级开发诊断
    /// （"不在玩家HUD泄漏枚举名"——玩家可见文案走 <see cref="DisplayPhaseText"/>）。</summary>
    public static class FoundryOutpostCoreBoss
    {
        private static readonly Dictionary<CoreBossState, CoreBossState> LegalNext = new Dictionary<CoreBossState, CoreBossState>
        {
            [CoreBossState.Locked] = CoreBossState.Shielded,
            [CoreBossState.Shielded] = CoreBossState.Phase1,
            [CoreBossState.Phase1] = CoreBossState.Transition,
            [CoreBossState.Transition] = CoreBossState.Phase2,
            [CoreBossState.Phase2] = CoreBossState.Destroyed,
        };

        public static CoreBossState GetState(RegionRecord region)
        {
            if (region == null || string.IsNullOrEmpty(region.CoreState))
            {
                return CoreBossState.Locked;
            }
            return Enum.TryParse(region.CoreState, out CoreBossState s) ? s : CoreBossState.Locked;
        }

        public static bool IsInitialized(RegionRecord region) => GetState(region) != CoreBossState.Locked;

        /// <summary>唯一状态写入口——非法跳转（不在 <see cref="LegalNext"/> 表内）一律拒绝，只打开发
        /// 诊断日志，不抛异常、不改状态、调用方按 false 处理（防御性——正常游戏流程中不应触发）。</summary>
        public static bool TryTransition(CampaignState state, RegionRecord region, CoreBossState to)
        {
            CoreBossState current = GetState(region);
            if (!LegalNext.TryGetValue(current, out CoreBossState legalNext) || legalNext != to)
            {
                Log.Error($"[FoundryOutpostCoreBoss] 非法 Boss 状态转换：{current} → {to}，已拒绝（开发诊断，" +
                    "正常游戏流程不应出现，说明调用方逻辑有误）。");
                return false;
            }
            region.CoreState = to.ToString();
            Log.Info($"[FoundryOutpostCoreBoss] Boss 状态 {current} → {to}。");
            return true;
        }

        /// <summary>玩家可见文案——"不在玩家HUD泄漏枚举名"，节点/主核心血条面板读这个而不是
        /// <see cref="RegionRecord.CoreState"/> 原始字符串。</summary>
        public static string DisplayPhaseText(RegionRecord region) => GetState(region) switch
        {
            CoreBossState.Locked => "核心分区未激活",
            CoreBossState.Shielded => "核心护盾防护中",
            CoreBossState.Phase1 => "核心暴露",
            CoreBossState.Transition => "核心过载冷却中",
            CoreBossState.Phase2 => "散热口暴露·弱点开启",
            CoreBossState.Destroyed => "核心已摧毁",
            _ => "未知",
        };

        /// <summary>首次跨过核心分区封锁线时调用一次——幂等（已初始化直接返回）。生成两节点+主核心
        /// 三条 <see cref="RegionEnemyRecord"/>，状态推进到 <see cref="CoreBossState.Shielded"/>。
        /// 唯一调用点 <see cref="FoundryOutpostController"/> 的越线检测（见该类"进入核心分区"分支）。</summary>
        public static void EnsureInitialized(CampaignState state, RegionRecord region)
        {
            if (state == null || region == null || IsInitialized(region))
            {
                return;
            }

            // 防御：理论上不会有残留（同一存档不会反复触发这条初始化路径），读档异常场景兜底不重复。
            state.RegionEnemies = (state.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
                .Where(e => e.EnemyInstanceId != FoundryOutpostLayout.CoreNode1Id
                    && e.EnemyInstanceId != FoundryOutpostLayout.CoreNode2Id
                    && e.EnemyInstanceId != FoundryOutpostLayout.MainCoreId)
                .ToArray();

            SpawnBossRecord(state, FoundryOutpostLayout.CoreNode1Id, FoundryOutpostLayout.BossNodeTypeId,
                FoundryOutpostLayout.CoreNode1.Position, FoundryOutpostLayout.NodeMaxHealth);
            SpawnBossRecord(state, FoundryOutpostLayout.CoreNode2Id, FoundryOutpostLayout.BossNodeTypeId,
                FoundryOutpostLayout.CoreNode2.Position, FoundryOutpostLayout.NodeMaxHealth);
            SpawnBossRecord(state, FoundryOutpostLayout.MainCoreId, FoundryOutpostLayout.BossCoreTypeId,
                FoundryOutpostLayout.MainCore.Position, FoundryOutpostLayout.MainCoreMaxHealth);

            region.CoreTransitionEndAtPlaySeconds = 0f;
            region.CoreTransitionRepairBotSummoned = false;
            region.CoreDataDropped = false;
            region.CoreLockoutWarnAtPlaySeconds = 0f;
            region.CoreLockoutActive = false;

            TryTransition(state, region, CoreBossState.Shielded);

            // ER7-CREDITS-01：MachineExperienceFlags.BossParticipation 此前"依赖尚未实现的 ER7 Boss
            // 内容，当前无真实触发源"（ER4-MCH-01 DEBT-ER4MCH01-01 原文）——ER7-CORE-01 落地后这个
            // 触发源第一次真实存在：越过核心分区封锁线的那一刻，在场全部机器记一次"参与过 Boss 战"。
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && m.RegionId == FoundryOutpostRegion.RegionId)
                {
                    MachineRegistry.TryMarkExperience(m.LogicId, MachineExperienceFlags.BossParticipation);
                }
            }

            Log.Info("[FoundryOutpostCoreBoss] Boss 战已初始化：两供能节点+主核心就位。");
        }

        private static void SpawnBossRecord(CampaignState state, string instanceId, string typeId, Vector2 position, float maxHealth)
        {
            state.RegionEnemies = state.RegionEnemies.Append(new RegionEnemyRecord
            {
                EnemyInstanceId = instanceId,
                RegionId = FoundryOutpostRegion.RegionId,
                EnemyTypeId = typeId,
                Position = position,
                Health = maxHealth,
                MaxHealth = maxHealth,
                IsAlive = true,
                CycleCooldownRemaining = 0f,
                SecondaryTimer = 0f,
            }).ToArray();
        }

        /// <summary>唯一伤害/阶段转换判定入口——<paramref name="target"/> 的存活/EnemyTypeId 已由
        /// 调用方（<see cref="FoundryOutpostRegion.TryDamageEnemy"/>/<see cref="FracturedCityRegion.TryDamageEnemy"/>）
        /// 校验过，这里只管"当前状态允不允许这条伤害生效"+"生效后要不要转换阶段"。伤害倍率（Phase2
        /// 侧后+20%）已由更上层调用方（<see cref="FoundryOutpostRegion.TryAttackEnemy"/>）用真实
        /// attackerPosition 算好烘进 <paramref name="damage"/>，本方法不重复计算方向。</summary>
        public static (bool success, string reason) ApplyDamage(CampaignState state, RegionEnemyRecord target, float damage)
        {
            RegionRecord region = FoundryOutpostRegion.Find(state);
            if (region == null || !IsInitialized(region))
            {
                return (false, "boss-not-initialized");
            }
            CoreBossState s = GetState(region);

            if (target.EnemyTypeId == FoundryOutpostLayout.BossNodeTypeId)
            {
                if (s != CoreBossState.Shielded)
                {
                    return (false, "node-invulnerable-in-state:" + s);
                }
                target.Health = Mathf.Max(0f, target.Health - Mathf.Max(0f, damage));
                if (target.Health <= 0f && target.IsAlive)
                {
                    target.IsAlive = false;
                    int shieldsRemaining = 2 - CountDestroyedNodes(state);
                    Log.Info($"[FoundryOutpostCoreBoss] 供能节点 {target.EnemyInstanceId} 已摧毁：护盾 {shieldsRemaining}/2。");
                    if (shieldsRemaining <= 0)
                    {
                        TryTransition(state, region, CoreBossState.Phase1);
                    }
                }
                return (true, null);
            }

            if (target.EnemyTypeId == FoundryOutpostLayout.BossCoreTypeId)
            {
                if (s != CoreBossState.Phase1 && s != CoreBossState.Phase2)
                {
                    return (false, "core-invulnerable-in-state:" + s);
                }
                target.Health = Mathf.Max(0f, target.Health - Mathf.Max(0f, damage));
                if (s == CoreBossState.Phase1 &&
                    target.Health <= target.MaxHealth * FoundryOutpostLayout.Phase1TransitionHealthFraction)
                {
                    TriggerTransition(state, region);
                }
                else if (s == CoreBossState.Phase2 && target.Health <= 0f && target.IsAlive)
                {
                    target.IsAlive = false;
                    TryTransition(state, region, CoreBossState.Destroyed);
                    DropCoreDataOnce(state, region);
                }
                return (true, null);
            }

            return (false, "not-a-boss-target");
        }

        private static int CountDestroyedNodes(CampaignState state)
        {
            int count = 0;
            RegionEnemyRecord n1 = FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.CoreNode1Id);
            RegionEnemyRecord n2 = FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.CoreNode2Id);
            if (n1 != null && !n1.IsAlive) count++;
            if (n2 != null && !n2.IsAlive) count++;
            return count;
        }

        /// <summary>Phase1→Transition：5秒计时+一次性召唤维修机（DEMO-CONTENT-LOCK.md"只召一台维修机"
        /// 字面要求，<see cref="RegionRecord.CoreTransitionRepairBotSummoned"/> 幂等标记）。</summary>
        private static void TriggerTransition(CampaignState state, RegionRecord region)
        {
            if (!TryTransition(state, region, CoreBossState.Transition))
            {
                return;
            }
            region.CoreTransitionEndAtPlaySeconds = state.PlaySeconds + FoundryOutpostLayout.TransitionDurationSeconds;
            if (!region.CoreTransitionRepairBotSummoned)
            {
                SummonTransitionRepairBot(state);
                region.CoreTransitionRepairBotSummoned = true;
            }
        }

        /// <summary>复用既有 <see cref="EnemyCatalog.RepairBotId"/> 类型——召唤出的维修机与外围驻守的
        /// 那台使用同一套 AI（<see cref="FoundryOutpostEnemyAi.Tick"/> 已有的 RepairBotId 分支自动
        /// 接管，不新写第二套维修机行为）。</summary>
        private static void SummonTransitionRepairBot(CampaignState state)
        {
            if (FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.CoreRepairBotSummonId) != null)
            {
                return; // 双重防御：正常流程不会重复调用（受 CoreTransitionRepairBotSummoned 保护）。
            }
            SpawnBossRecord(state, FoundryOutpostLayout.CoreRepairBotSummonId, EnemyCatalog.RepairBotId,
                FoundryOutpostLayout.CoreRepairBotSummon.Position, FoundryOutpostLayout.RepairBotMaxHealth);
            Log.Info("[FoundryOutpostCoreBoss] Transition：已召唤一台维修机支援（一次性，不会再召第二台）。");
        }

        /// <summary>"主核心毁灭只掉一次核心数据盒"——与既有关键物同一生命周期
        /// （<see cref="RegionQuestItemState"/>），复用 <see cref="FoundryOutpostRegion.SpawnQuestItemOnGround"/>
        /// 不新造第二套关键物落地逻辑。</summary>
        private static void DropCoreDataOnce(CampaignState state, RegionRecord region)
        {
            if (region.CoreDataDropped)
            {
                return;
            }
            region.CoreDataDropped = true;
            FoundryOutpostRegion.SpawnQuestItemOnGround(state, FoundryOutpostLayout.CoreDataContentId, FoundryOutpostLayout.MainCore.Position);
            Log.Info("[FoundryOutpostCoreBoss] 主核心已摧毁：核心数据盒已掉落（唯一一次，Destroyed后不再新增任何敌方生产）。");
        }

        /// <summary>Phase2"侧后+20%"倍率——只在目标是主核心且当前恰为 Phase2 时才可能大于 1，其余
        /// 一律 1（不影响节点/其它阶段伤害）。唯一调用点 <see cref="FoundryOutpostRegion.TryAttackEnemy"/>，
        /// 在真正结算伤害之前用真实 attackerPosition 算好，再传给 <see cref="CannonCombat.TryFire"/>/
        /// 直接乘进普通武器伤害——两条路径复用同一份倍率计算，不允许出现"直控算一套、AI算另一套"。</summary>
        public static float ComputeDamageMultiplier(CampaignState state, RegionEnemyRecord target, Vector2 attackerPosition)
        {
            if (target == null || target.EnemyTypeId != FoundryOutpostLayout.BossCoreTypeId)
            {
                return 1f;
            }
            RegionRecord region = FoundryOutpostRegion.Find(state);
            if (GetState(region) != CoreBossState.Phase2)
            {
                return 1f;
            }
            return IsBackHit(target.Position, attackerPosition) ? 1f + FoundryOutpostLayout.Phase2BackHitBonusPct : 1f;
        }

        public static bool IsBackHit(Vector2 corePosition, Vector2 attackerPosition)
        {
            Vector2 toAttacker = attackerPosition - corePosition;
            if (toAttacker.sqrMagnitude < 1e-6f)
            {
                return false; // 贴脸算正面，不给侧后奖励留歧义（护甲机同类判定的反向语义：弱点不是默认态）。
            }
            float cosAngle = Vector2.Dot(FoundryOutpostLayout.MainCoreFacing.normalized, toAttacker.normalized);
            float cosHalf = Mathf.Cos(FoundryOutpostLayout.MainCoreFrontalHalfAngleDeg * Mathf.Deg2Rad);
            return cosAngle < cosHalf; // 不在正面锥角内＝侧后（散热口暴露方向）。
        }

        /// <summary>ER7-FAIL-01 STORY-EXECUTION-CARDS.md 第2条："Boss Phase2撤离/全灭/退出下次从Boss前
        /// 自动档恢复主核心与节点状态，不允许跨出击磨血或重复阶段奖励；已安全结算的外围回收和蓝图仍
        /// 保留"——唯一调用点 <see cref="ExpeditionReturnService.TryConfirmEvacuation"/>/
        /// <see cref="ExpeditionReturnService.TryConfirmAbandon"/>，在"撤离事务已提交"之后立即调用。
        /// 只重置 Boss 专属字段（三条 RegionEnemyRecord 的 HP/存活+ <see cref="RegionRecord.CoreState"/>
        /// 等 FSM 字段），不碰蓝图/机队/仓储/关键物——"从 Boss 前自动档恢复"字面上等价于把这些字段
        /// 恢复成 <see cref="SaveReason.BossEngageEnter"/> 那次存档时的样子（该次存档正是在
        /// <see cref="EnsureInitialized"/> 之前打的，此时 Boss 尚是 Locked），不需要真的做一次磁盘
        /// 读档来达到同样效果——比对全量回滚（<see cref="ExpeditionDepartureService"/> 那种"回出发前档"）
        /// 更精确：不会把本次已经安全结算的外围战利品/蓝图/机队变更也一并撤销。
        ///
        /// Destroyed 后不重置（"不允许重复阶段奖励"的反面——已经拿到的胜利不会被撤离动作抹掉，核心
        /// 数据盒不管有没有装车都原样留在地面/货舱，供下次再来捡，同既有关键物"未拾取则留在场上"
        /// 生命周期，不特殊处理）。</summary>
        public static void ResetToPreBossState(CampaignState state, RegionRecord region)
        {
            if (state == null || region == null || !IsInitialized(region) || GetState(region) == CoreBossState.Destroyed)
            {
                return;
            }

            CoreBossState before = GetState(region);
            state.RegionEnemies = (state.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
                .Where(e => e.EnemyInstanceId != FoundryOutpostLayout.CoreRepairBotSummonId) // 一次性召唤物，撤走。
                .ToArray();
            ResetBossRecord(state, FoundryOutpostLayout.CoreNode1Id, FoundryOutpostLayout.NodeMaxHealth);
            ResetBossRecord(state, FoundryOutpostLayout.CoreNode2Id, FoundryOutpostLayout.NodeMaxHealth);
            ResetBossRecord(state, FoundryOutpostLayout.MainCoreId, FoundryOutpostLayout.MainCoreMaxHealth);

            region.CoreState = null; // Locked——下次越线重新 EnsureInitialized，天然拿到满血全新一轮。
            region.CoreTransitionEndAtPlaySeconds = 0f;
            region.CoreTransitionRepairBotSummoned = false;
            region.CoreLockoutWarnAtPlaySeconds = 0f;
            region.CoreLockoutActive = false;
            // CoreDataDropped 不重置于此——本方法只在"未 Destroyed"时执行（上面已早退），Destroyed
            // 之后才可能为真，本分支内它必然仍是 false，写不写都一样，保留默认值不做无意义赋值。

            Log.Info($"[FoundryOutpostCoreBoss] 未完成的 Boss 尝试已重置（原状态 {before}）：两节点+主核心" +
                "恢复满血，下次进入核心分区重新开始，不跨出击磨血。");
        }

        private static void ResetBossRecord(CampaignState state, string instanceId, float maxHealth)
        {
            RegionEnemyRecord record = FoundryOutpostRegion.FindEnemy(state, instanceId);
            if (record == null)
            {
                return;
            }
            record.Health = maxHealth;
            record.MaxHealth = maxHealth;
            record.IsAlive = true;
            record.CycleCooldownRemaining = 0f;
        }

        /// <summary>每帧驱动——Transition 计时→自动进 Phase2；Phase2 区域封锁预警→生效；Phase1/Phase2
        /// 主核心自卫攻击。<see cref="CoreBossState.Destroyed"/> 后整体 no-op（"停止新增敌方生产"，
        /// 不会再召维修机/不会再触发任何转换）。唯一调用点 <see cref="FoundryOutpostController.Update"/>。</summary>
        public static void Tick(CampaignState state, float dt, IReadOnlyList<FoundryOutpostEnemyAi.VisibleMachine> machines,
            FoundryOutpostEnemyAi.LineOfSightCheck hasLineOfSight)
        {
            RegionRecord region = FoundryOutpostRegion.Find(state);
            if (region == null || !IsInitialized(region) || dt <= 0f)
            {
                return;
            }
            CoreBossState s = GetState(region);
            if (s == CoreBossState.Destroyed)
            {
                return;
            }

            if (s == CoreBossState.Transition)
            {
                if (state.PlaySeconds >= region.CoreTransitionEndAtPlaySeconds)
                {
                    if (TryTransition(state, region, CoreBossState.Phase2))
                    {
                        region.CoreLockoutWarnAtPlaySeconds = state.PlaySeconds + FoundryOutpostLayout.CoreLockoutWarnSeconds;
                        Log.Info("[FoundryOutpostCoreBoss] Phase2 区域封锁预警：2秒后核心分区入口锁死。");
                    }
                }
                return; // Transition 期间不攻击（DEMO-CONTENT-LOCK.md 字面要求）。
            }

            if (s == CoreBossState.Phase2 && !region.CoreLockoutActive && region.CoreLockoutWarnAtPlaySeconds > 0f
                && state.PlaySeconds >= region.CoreLockoutWarnAtPlaySeconds)
            {
                region.CoreLockoutActive = true;
                Log.Info("[FoundryOutpostCoreBoss] 区域封锁已生效：核心分区不能再退回外围。");
            }

            if (s == CoreBossState.Phase1 || s == CoreBossState.Phase2)
            {
                TickCoreAttack(state, region, dt, machines, hasLineOfSight);
            }
        }

        /// <summary>主核心自卫攻击——DEMO-CONTENT-LOCK.md 未点名具体数字，按铸造步进炮同一量级取保守
        /// judgment call（同 <see cref="FoundryOutpostEnemyAi"/> 类注释先例，非文档摘录）。</summary>
        private static void TickCoreAttack(CampaignState state, RegionRecord region, float dt,
            IReadOnlyList<FoundryOutpostEnemyAi.VisibleMachine> machines, FoundryOutpostEnemyAi.LineOfSightCheck hasLineOfSight)
        {
            RegionEnemyRecord core = FoundryOutpostRegion.FindEnemy(state, FoundryOutpostLayout.MainCoreId);
            if (core == null || !core.IsAlive)
            {
                return;
            }
            core.CycleCooldownRemaining -= dt;
            if (core.CycleCooldownRemaining > 0f)
            {
                return;
            }
            FoundryOutpostEnemyAi.VisibleMachine? target = FindNearestVisible(core.Position, machines, hasLineOfSight, FoundryOutpostLayout.CoreAttackRange);
            if (!target.HasValue)
            {
                return;
            }
            core.CycleCooldownRemaining = FoundryOutpostLayout.CoreAttackCooldownSeconds;
            MachineRegistry.ApplyDamage(target.Value.LogicId, FoundryOutpostLayout.CoreAttackDamage);
            Log.Info($"[FoundryOutpostCoreBoss] 主核心命中机器 {target.Value.LogicId}，伤害 {FoundryOutpostLayout.CoreAttackDamage:F0}。");
        }

        private static FoundryOutpostEnemyAi.VisibleMachine? FindNearestVisible(Vector2 from,
            IReadOnlyList<FoundryOutpostEnemyAi.VisibleMachine> machines, FoundryOutpostEnemyAi.LineOfSightCheck hasLineOfSight, float maxRange)
        {
            if (machines == null)
            {
                return null;
            }
            FoundryOutpostEnemyAi.VisibleMachine? best = null;
            float bestDist = maxRange;
            foreach (FoundryOutpostEnemyAi.VisibleMachine m in machines)
            {
                float dist = Vector2.Distance(from, m.Position);
                if (dist > bestDist)
                {
                    continue;
                }
                if (hasLineOfSight != null && !hasLineOfSight(from, m.Position))
                {
                    continue;
                }
                bestDist = dist;
                best = m;
            }
            return best;
        }
    }
}
