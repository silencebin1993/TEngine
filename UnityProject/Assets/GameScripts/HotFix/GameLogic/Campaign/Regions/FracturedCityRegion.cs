using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER5-REGION-01 STORY-EXECUTION-CARDS.md：破碎都市固定内容的唯一写入口——节点摧毁、
    /// 容器打开、终端读取、敌人死亡、关键物 Lost/Recovered 全部经本类写 <see cref="RegionRecord"/>/
    /// <see cref="RegionEnemyRecord"/>/<see cref="RegionQuestItemRecord"/>，不在 Controller/UI 里散落
    /// 直接改字段（同 <see cref="HomeValleySignal"/>/<see cref="HomeValleyCombatTargets"/> 既有纪律）。
    ///
    /// ── 范围边界（有 REQUIREMENT-TO-PLAYABLE-TRACE.md 依据，非本 Story 自行裁剪）──
    /// 真正的敌方 AI 行为树/阵型属于 ER5-SILENT-01；E 交互正式输入链路（按 E 拾取/读取/开箱）属于
    /// ER5-INT-01；远征准备/出发确认/往返快照事务属于 ER5-EXP-01。本类只交付这些系统将要调用的
    /// 真实状态机与数据写入口，用可被 execute_code/Play Mode 直接调用的方法暴露（同
    /// <see cref="HomeValleyCombatTargets.TryAttack"/> 一样，不等上游系统落地才能验证正确性）。</summary>
    public static class FracturedCityRegion
    {
        public const string RegionId = FracturedCityLayout.RegionId;

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

        // ── RegionRecord 查询/防御性播种 ──────────────────────────────────────

        public static RegionRecord Find(CampaignState state) =>
            state?.RegionRecords?.FirstOrDefault(r => r.RegionId == RegionId);

        /// <summary>正常路径下 <see cref="HomeValleySignal.EnsureSeeded"/> 已在归还谷地首次进入时播种
        /// 好 Locked 态记录；本方法只是防御性兜底（旧档/单测直接构造 <see cref="CampaignState"/> 时），
        /// 不假设调用顺序，幂等。</summary>
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

        // ── 敌人（静默侦察机 x2、静默干扰机 x1）──────────────────────────────

        public static RegionEnemyRecord FindEnemy(CampaignState state, string enemyInstanceId) =>
            state?.RegionEnemies?.FirstOrDefault(e => e.EnemyInstanceId == enemyInstanceId);

        /// <summary>首次进入才生成三个敌人实例；已存在（第二次进入/读档）原样跳过——与
        /// <see cref="HomeValleyCombatTargets.EnsureSeeded"/> 同一幂等纪律。已死亡的实例不会被
        /// 复活（<see cref="RegionEnemyRecord.IsAlive"/> 落盘保留），符合"再次进入保留已清除状态"。</summary>
        public static void EnsureEnemiesSeeded(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            state.RegionEnemies ??= Array.Empty<RegionEnemyRecord>();
            SeedIfMissing(FracturedCityLayout.Scout1SpawnId, EnemyCatalog.ScoutId,
                FracturedCityLayout.Scout1Spawn.Position, FracturedCityLayout.ScoutMaxHealth);
            SeedIfMissing(FracturedCityLayout.Scout2SpawnId, EnemyCatalog.ScoutId,
                FracturedCityLayout.Scout2Spawn.Position, FracturedCityLayout.ScoutMaxHealth);
            SeedIfMissing(FracturedCityLayout.JammerSpawnId, EnemyCatalog.JammerId,
                FracturedCityLayout.JammerSpawn.Position, FracturedCityLayout.JammerMaxHealth);

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
                    CycleCooldownRemaining = FracturedCityLayout.ScoutMarkIntervalSeconds,
                }).ToArray();
            }
        }

        /// <summary>ER5-SILENT-01 起：本方法只做"警戒等级缓慢衰减"这一项与具体敌人无关的背景行为。
        /// 静默侦察机真正的标记周期（冷却计时+是否命中目标+呼叫干扰）已迁移到
        /// <see cref="FracturedCityEnemyAi.Tick"/>——那里需要 Controller 提供的实时友军位置/视线遮挡
        /// 判定（"标记不魔法穿墙"），本类不持有可视化 Transform，做不到这一层判定，故不再在此处
        /// 盲目按计时器触发标记（此前版本的行为——ER5-REGION-01 骨架阶段合理，敌人 AI 真正落地后
        /// 不再合理）。</summary>
        public static void TickEnemies(CampaignState state, float dt)
        {
            if (state?.RegionEnemies == null || dt <= 0f)
            {
                return;
            }
            RegionRecord region = Find(state);
            if (region != null && region.EnemyAlertLevel > 0f)
            {
                region.EnemyAlertLevel = Mathf.Max(0f, region.EnemyAlertLevel - dt); // 缓慢衰减，非目标：完整警戒 AI。
            }
        }

        /// <summary>静默侦察机成功标记目标时调用——由 <see cref="FracturedCityEnemyAi"/> 驱动，
        /// 抬升 <see cref="RegionRecord.EnemyAlertLevel"/>（ERD-ENY-001"呼叫干扰"的最小可用代理：
        /// 本 Story 不新增敌人增援生成机制，用警戒等级上升作为其可被 HUD/后续 Story 读取的真实后果）。</summary>
        public static void BumpAlertFromMark(CampaignState state)
        {
            RegionRecord region = Find(state);
            if (region != null)
            {
                region.EnemyAlertLevel = Mathf.Clamp(region.EnemyAlertLevel + 5f, 0f, 100f);
            }
        }

        /// <summary>唯一伤害结算入口。命中致死时写 <see cref="RegionRecord.DestroyedNodeIds"/>
        /// （"敌人死亡…写 RegionRecord"，与固定节点共用同一"场上物件已清除"语义桶，见类注释）。
        /// 幂等：已死亡目标重复调用直接拒绝，不重复触发死亡副作用。</summary>
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
                Log.Info($"[FracturedCityRegion] 敌人 {enemyInstanceId} 已阵亡。");
            }
            return ActionResult.Ok();
        }

        /// <summary>ER5-SILENT-01"战利品生成...使用正式货物链"——复用 <see cref="HomeValleyCargo.SpawnGroundItem"/>
        /// （同箱子废料掉落同一入口），不新造第二套地面物类型。数量按敌人类型区分（ERD-ENY-001"掉落"
        /// 列只是 flavor 文案，DEMO-CONTENT-LOCK.md 未点名具体数字——真正的两件关键技术仍然只来自
        /// 监听节点摧毁/终端读取，见类注释"关键掉落和终端物由正式货物链处理"，本方法产出的是普通废料，
        /// 不与关键物生命周期混淆）。<paramref name="enemy"/> 调用时刻其 <see cref="RegionEnemyRecord.IsAlive"/>
        /// 已经置为 false，只会在死亡转换的那一帧触发一次（幂等由调用方 <see cref="TryDamageEnemy"/>
        /// 的早退保证）。</summary>
        private static void SpawnEnemyLoot(CampaignState state, RegionEnemyRecord enemy)
        {
            int amount = enemy.EnemyTypeId == EnemyCatalog.JammerId
                ? FracturedCityLayout.JammerScrapLoot
                : FracturedCityLayout.ScoutScrapLoot;
            HomeValleyCargo.SpawnGroundItem(state, RegionId, enemy.Position,
                CampaignEconomyLedger.ResourceScrap, amount, enemy.EnemyInstanceId + ":scrap");
            Log.Info($"[FracturedCityRegion] 敌人 {enemy.EnemyInstanceId} 掉落 {amount} 废料。");
        }

        /// <summary>ER5-SILENT-01：敌人对玩家机器造成伤害的唯一入口，与 <see cref="TryDamageEnemy"/>
        /// 对称。写入通过 <see cref="MachineRegistry.ApplyDamage"/>。阵营判断是字面运行时校验——只接受
        /// <see cref="MachineRecord.FactionId"/>=="Player" 的存活机器，且必须仍在本区域（跨区域/已
        /// 切场的旧 LogicId 直接拒绝，不允许"隔空打击"）。</summary>
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
            Log.Info($"[FracturedCityRegion] 敌人 {enemyInstanceId} 命中机器 {targetLogicId}，伤害 {damage:F1}。");
            return ActionResult.Ok();
        }

        /// <summary>直控攻击的锥形命中判定——同 <see cref="HomeValleyCombatTargets.TryFindTargetInAim"/>
        /// 同一设计语言的独立最小实现（目标类型是 <see cref="RegionEnemyRecord"/> 而非
        /// <see cref="CombatTargetRecord"/>，破碎都市此前完全没有直控攻击入口，ER5-SILENT-01 首次补上，
        /// 满足验收卡"玩家策略/直控各打一场"里的直控那一半）。</summary>
        public static RegionEnemyRecord TryFindEnemyInAim(CampaignState state, Vector2 origin, Vector2 aimDirection)
        {
            if (state?.RegionEnemies == null || aimDirection.sqrMagnitude < 1e-6f)
            {
                return null;
            }
            Vector2 dirNorm = aimDirection.normalized;
            float cosHalf = Mathf.Cos(FracturedCityLayout.DirectAttackAimHalfAngleDeg * Mathf.Deg2Rad);
            foreach (RegionEnemyRecord enemy in state.RegionEnemies)
            {
                if (enemy.RegionId != RegionId || !enemy.IsAlive)
                {
                    continue;
                }
                Vector2 toTarget = enemy.Position - origin;
                float dist = toTarget.magnitude;
                if (dist > FracturedCityLayout.DirectAttackRange || dist < 0.01f)
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

        // ── 标记（静默侦察机施加 / 静默干扰机清除，ERD-ENY-001"标记"/"清除标记"）─────────

        /// <summary>当前是否处于被标记状态（未过期）。</summary>
        public static bool IsMachineMarked(CampaignState state, int machineLogicId)
        {
            RegionRecord region = Find(state);
            MarkedMachineRecord mark = region?.MarkedMachines?.FirstOrDefault(m => m.MachineLogicId == machineLogicId);
            return mark != null && mark.ExpireAtPlaySeconds > (state?.PlaySeconds ?? 0f);
        }

        /// <summary>施加/刷新标记——幂等（同一机器重复标记只刷新过期时间，不产生重复条目）。</summary>
        public static void TryMarkMachine(CampaignState state, int machineLogicId, float durationSeconds)
        {
            RegionRecord region = Find(state);
            if (region == null)
            {
                return;
            }
            region.MarkedMachines ??= Array.Empty<MarkedMachineRecord>();
            MarkedMachineRecord existing = region.MarkedMachines.FirstOrDefault(m => m.MachineLogicId == machineLogicId);
            float expireAt = state.PlaySeconds + durationSeconds;
            if (existing != null)
            {
                existing.ExpireAtPlaySeconds = expireAt;
            }
            else
            {
                region.MarkedMachines = region.MarkedMachines.Append(new MarkedMachineRecord
                {
                    MachineLogicId = machineLogicId,
                    ExpireAtPlaySeconds = expireAt,
                }).ToArray();
            }
            Log.Info($"[FracturedCityRegion] 机器 {machineLogicId} 已被标记（{durationSeconds:F0}秒）。");
        }

        /// <summary>清除标记（静默干扰机在其半径内的效果）。返回是否真的清除了一条存在的标记，
        /// 供调用方判断是否要播放"清标记"反馈。</summary>
        public static bool TryClearMark(CampaignState state, int machineLogicId)
        {
            RegionRecord region = Find(state);
            if (region?.MarkedMachines == null || region.MarkedMachines.Length == 0)
            {
                return false;
            }
            int before = region.MarkedMachines.Length;
            region.MarkedMachines = region.MarkedMachines.Where(m => m.MachineLogicId != machineLogicId).ToArray();
            bool cleared = region.MarkedMachines.Length != before;
            if (cleared)
            {
                Log.Info($"[FracturedCityRegion] 干扰机已清除机器 {machineLogicId} 的标记。");
            }
            return cleared;
        }

        /// <summary>惰性清理已过期的标记（每帧调用，个位数条目，O(1) 量级）。</summary>
        public static void SweepExpiredMarks(CampaignState state)
        {
            RegionRecord region = Find(state);
            if (region?.MarkedMachines == null || region.MarkedMachines.Length == 0)
            {
                return;
            }
            float now = state.PlaySeconds;
            region.MarkedMachines = region.MarkedMachines.Where(m => m.ExpireAtPlaySeconds > now).ToArray();
        }

        /// <summary>ER5-CMD-01：战略 Attack 命令的唯一命中结算入口——与
        /// <see cref="HomeValleyCombatTargets.TryAttack"/> 同一模式（不重新计算伤害，直接用
        /// <see cref="MachineLoadoutRegistry"/> 解析出的编译结果），保证"AI/玩家使用同装配"的规则
        /// 在破碎都市同样成立，也保证本区域只有一处代码真正调用 <see cref="TryDamageEnemy"/>
        /// 结算武器伤害。</summary>
        /// <summary>ER6-REACT-01：<paramref name="isReachable"/> 可选——squad/直控两条调用方都能提供
        /// Controller 侧真实视线遮挡判定（复用 ER5-SILENT-01 同一套锚点净空算法，"额外目标按合法阵营、
        /// 可达、距离、稳定LogicId排序"字面要求）；传 null 时退化为"总是可达"（不破坏本方法早于本
        /// Story 就已存在的调用方兼容性——旧调用点不需要同步改造才能继续工作）。</summary>
        public static ActionResult TryAttackEnemy(CampaignState state, int attackerLogicId, string enemyInstanceId, int seed,
            bool isAiSource, Func<Vector2, Vector2, bool> isReachable = null)
        {
            if (state == null)
            {
                return ActionResult.Fail("没有活动的破碎都市会话。");
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
            // ER6-REACT-02：铸造重炮走独立的瞄准线/冷却/热量状态机，不复用下面连射器/切割束的
            // 即时命中+标记跳转路径（重炮反应槽只对熔穿过载生效，见 CannonCombat 类注释）。必须在
            // HasCombatOutput 早退检查**之前**判定——铸造重炮在 MechanicalContentFacade 里没有
            // LegacyFacadeId（DEBT-ER4CONTENT01-05，尚未接入共享 ComposeEngine 装配链），
            // HasCombatOutput 对它结构上恒为 false；但重炮伤害本来就是 CannonCombat 里的固定设计
            // 常量、不读 TotalNormalizedDamage，不需要 HasCombatOutput 为真。
            if (resolution.Preview.HasCannonPrimary)
            {
                return CannonCombat.TryFire(state, attackerLogicId, enemyInstanceId, resolution, isReachable);
            }

            if (!resolution.Preview.HasCombatOutput)
            {
                return ActionResult.Fail("当前装配没有可攻击的主武器出口（8号汇槽为空）。");
            }

            float damage = Mathf.Max(0f, resolution.Preview.TotalNormalizedDamage);

            // ER6-REACT-01："目标已标记时"才跳转——查询顺序在本次命中造成的新标记之前，第一次命中
            // （目标当时还没有标记）只留下标记、不跳转；同一目标被再次命中时才触发跳转，符合
            // "无标记/只有一个目标退化为正常攻击"字面语义（本方法内联判定，不是靠额外状态机）。
            bool wasMarkedBeforeThisHit = IsEnemyMarked(state, enemyInstanceId);

            ActionResult primaryResult = TryDamageEnemy(state, enemyInstanceId, damage);
            if (!primaryResult.Success)
            {
                return primaryResult;
            }

            if (resolution.Preview.HasMarkerFunction)
            {
                // 内容目录原文"标记主目标及附近至多两个敌人"——标记只要装了标记器就打，与是否配齐
                // 完整反应组合无关；目标已阵亡时不再需要标记（TryDamageEnemy 内部已经把它从活着的
                // 敌人里摘掉，重新标记一个尸体没有意义）。
                if (enemy.IsAlive)
                {
                    TryMarkEnemy(state, enemyInstanceId, FracturedCityLayout.EnemyMarkDurationSeconds);
                }
            }

            if (resolution.Preview.ReactionId == MechanicalReactionCatalog.ReactionMarkJumpId && wasMarkedBeforeThisHit)
            {
                ApplyMarkJump(state, enemyInstanceId, enemy.Position, damage, isReachable);
            }

            return primaryResult;
        }

        /// <summary>标记跳转链式伤害——按"合法阵营（本方法域内只处理 RegionEnemyRecord，结构上不可能
        /// 打到友军）、可达（<paramref name="isReachable"/>）、距离（8米内）、稳定 LogicId（此处用
        /// EnemyInstanceId 字符串序作稳定排序键，敌人没有整数 LogicId）"排序，最多再打 2 个目标，
        /// 每跳伤害为上一跳的 60%（DEMO-CONTENT-LOCK.md §2.4）。跳转目标本身不需要重新标记/不再次
        /// 递归跳转——"不循环连锁"（验收卡字面要求）。</summary>
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
                Log.Info($"[FracturedCityRegion] 标记跳转：{primaryEnemyInstanceId} → {candidates[i].EnemyInstanceId}，伤害 {jumpDamage:F1}。");
            }
        }

        // ── 敌方标记（ER6-REACT-01：静默标记器命中打标记，标记跳转固件识别跳转目标）───────

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

        /// <summary>"干扰清标记"（DEMO-CONTENT-LOCK.md §2.4 反制手段）——静默干扰机在其半径内清除
        /// 敌方标记，供 <see cref="FracturedCityEnemyAi.TickJammer"/> 调用。</summary>
        public static bool TryClearEnemyMark(CampaignState state, string enemyInstanceId)
        {
            RegionRecord region = Find(state);
            if (region?.MarkedEnemies == null || region.MarkedEnemies.Length == 0)
            {
                return false;
            }
            int before = region.MarkedEnemies.Length;
            region.MarkedEnemies = region.MarkedEnemies.Where(m => m.EnemyInstanceId != enemyInstanceId).ToArray();
            return region.MarkedEnemies.Length != before;
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

        // ── 干扰（DEMO-CONTENT-LOCK.md §4.1 第2条）───────────────────────────

        /// <summary>干扰场是否覆盖某坐标——中心是监听节点位置（干扰机"守"节点，不是干扰机自身的
        /// 位置，节点摧毁后"干扰区消失，控制恢复"是字面原文，与干扰机自身是否存活无关）。</summary>
        public static bool IsPositionJammed(CampaignState state, Vector2 position)
        {
            RegionRecord region = Find(state);
            if (region?.DestroyedNodeIds != null &&
                region.DestroyedNodeIds.Contains(FracturedCityLayout.ListeningNodeId))
            {
                return false;
            }
            float dist = Vector2.Distance(position, FracturedCityLayout.ListeningNode.Position);
            return dist <= FracturedCityLayout.JammerRadius;
        }

        /// <summary>新的接管（控制切换）请求——干扰场内直接拒绝返回 SignalJammed，不改变当前控制目标。
        /// 已经在控制中的机器不经过本方法（"原已受控机器 2 秒宽限后回弹"是 Controller 侧的计时器，
        /// 不是每帧重新请求一次）。</summary>
        public static ActionResult TryRequestControl(CampaignState state, Vector2 position)
        {
            return IsPositionJammed(state, position)
                ? ActionResult.Fail("SignalJammed")
                : ActionResult.Ok();
        }

        // ── 固定节点/容器/终端（DEMO-CONTENT-LOCK.md §4.1 第2～3条）──────────

        private static void MarkDestroyed(CampaignState state, string nodeId)
        {
            RegionRecord region = Find(state);
            if (region == null)
            {
                return;
            }
            region.DestroyedNodeIds ??= Array.Empty<string>();
            if (!region.DestroyedNodeIds.Contains(nodeId))
            {
                region.DestroyedNodeIds = region.DestroyedNodeIds.Append(nodeId).ToArray();
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

        /// <summary>摧毁监听节点：幂等（重复调用对已摧毁节点直接拒绝，不重复产出模块）；成功时
        /// 首次产出"静默标记器模块"关键物（DEMO-CONTENT-LOCK.md §4.1 第3条），干扰场随之失效
        /// （见 <see cref="IsPositionJammed"/>）。节点是原子摧毁（无中间血量态）——DEMO-CONTENT-LOCK.md
        /// 未点名节点 HP，本 Story 只需要"摧毁"这个离散事件真实可发生、幂等、写盘；带按住/点击进度
        /// 的连续判定属于 ER5-INT-01 的"残骸拆解"式 E 交互，不在此重复发明中间态（也避免节点 HP
        /// 需要落盘持久化的额外 schema 负担——DestroyedNodeIds 存在性本身已是完整真相）。</summary>
        public static ActionResult TryDestroyListeningNode(CampaignState state)
        {
            RegionRecord region = Find(state);
            if (region == null)
            {
                return ActionResult.Fail("破碎都市尚未初始化。");
            }
            if (region.DestroyedNodeIds != null && region.DestroyedNodeIds.Contains(FracturedCityLayout.ListeningNodeId))
            {
                return ActionResult.Fail("监听节点已被摧毁。");
            }

            MarkDestroyed(state, FracturedCityLayout.ListeningNodeId);
            SpawnQuestItemOnGround(state, FracturedCityLayout.MarkerModuleContentId, FracturedCityLayout.ListeningNode.Position);
            // ER6-EXPOSE-01："摧毁监听节点同时记录战斗暴露+8与监听链断开-15，两笔来源独立，净值-7"
            // （DEMO-IMPLEMENTATION-SPEC.md ERD-ECO-004 原文）。
            CampaignExposureLedger.GrantNodeDestroyed(state, FracturedCityLayout.ListeningNodeId);
            Log.Info("[FracturedCityRegion] 监听节点已摧毁：干扰区消失，标记器模块已掉落。");
            return ActionResult.Ok();
        }

        /// <summary>读取终端：幂等，首次读取产出"协议数据盒"。</summary>
        public static ActionResult TryReadTerminal(CampaignState state)
        {
            RegionRecord region = Find(state);
            if (region == null)
            {
                return ActionResult.Fail("破碎都市尚未初始化。");
            }
            if (region.LootedContainerIds != null && region.LootedContainerIds.Contains(FracturedCityLayout.TerminalId))
            {
                return ActionResult.Fail("终端已读取过。");
            }
            MarkLooted(state, FracturedCityLayout.TerminalId);
            SpawnQuestItemOnGround(state, FracturedCityLayout.ProtocolDataboxContentId, FracturedCityLayout.Terminal.Position);
            Log.Info("[FracturedCityRegion] 协议终端已读取：协议数据盒已产出。");
            return ActionResult.Ok();
        }

        /// <summary>打开一处废料箱：幂等，成功后在箱子位置生成 40 废料地面物（复用
        /// <see cref="HomeValleyCargo.SpawnGroundItem"/>——纯资源类掉落物，不需要
        /// <see cref="RegionQuestItemRecord"/> 那套 Lost/Recovered 生命周期，DEMO-CONTENT-LOCK.md
        /// §4.1 第4条明确"此机制不重生已领取的废料"）。</summary>
        public static ActionResult TryOpenCrate(CampaignState state, string crateId)
        {
            bool known = crateId == FracturedCityLayout.Crate1Id ||
                crateId == FracturedCityLayout.Crate2Id ||
                crateId == FracturedCityLayout.Crate3Id;
            if (!known)
            {
                return ActionResult.Fail($"未知箱子 {crateId}。");
            }
            Vector2 position = crateId == FracturedCityLayout.Crate1Id ? FracturedCityLayout.Crate1.Position
                : crateId == FracturedCityLayout.Crate2Id ? FracturedCityLayout.Crate2.Position
                : FracturedCityLayout.Crate3.Position;
            RegionRecord region = Find(state);
            if (region == null)
            {
                return ActionResult.Fail("破碎都市尚未初始化。");
            }
            if (region.LootedContainerIds != null && region.LootedContainerIds.Contains(crateId))
            {
                return ActionResult.Fail("该箱子已打开过。");
            }
            MarkLooted(state, crateId);
            HomeValleyCargo.SpawnGroundItem(state, RegionId, position,
                CampaignEconomyLedger.ResourceScrap, FracturedCityLayout.CrateScrapAmount, crateId + ":scrap");
            Log.Info($"[FracturedCityRegion] 箱子 {crateId} 已打开：{FracturedCityLayout.CrateScrapAmount} 废料已落地。");
            return ActionResult.Ok();
        }

        /// <summary>地图标记/任务日志：任一机器进入某固定物件的发现半径即视为"发现"，幂等写
        /// <see cref="RegionRecord.DiscoveredNodes"/>。</summary>
        public static bool TryDiscover(CampaignState state, string poiId, Vector2 machinePosition, Vector2 poiPosition)
        {
            if (Vector2.Distance(machinePosition, poiPosition) > FracturedCityLayout.PoiDiscoveryRadius)
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
            Log.Info($"[FracturedCityRegion] 已发现 {poiId}（任务日志/地图标记）。");
            return true;
        }

        // ── 关键任务物（标记器模块/协议数据盒）Lost/Recovered 生命周期 ───────

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

        /// <summary>拾取一件地面关键物到某台存活机器的货舱（"战利品装载"最小可用版本——正式 E 交互
        /// 属于 ER5-INT-01，本方法是它将要调用的真实底层动作）。</summary>
        public static ActionResult TryCollectQuestItem(CampaignState state, string salvageInstanceId, int carrierLogicId)
        {
            RegionQuestItemRecord item = FindQuestItem(state, salvageInstanceId);
            if (item == null || item.State != RegionQuestItemState.OnGround)
            {
                return ActionResult.Fail("地面上没有该关键物。");
            }
            if (!MachineRegistry.TryGetRecord(carrierLogicId, out MachineRecord machine) || !machine.IsAlive ||
                machine.RegionId != RegionId)
            {
                return ActionResult.Fail("携带机器不存在/已阵亡/不在本区域。");
            }
            item.State = RegionQuestItemState.Carried;
            item.CarrierLogicId = carrierLogicId;
            item.Position = default;
            Log.Info($"[FracturedCityRegion] {item.ContentId} 已装入机器 {carrierLogicId} 货舱。");
            return ActionResult.Ok();
        }

        /// <summary>撤离/结算：把当前 Carried 状态的关键物按携带机器是否存活分流成 Recovered/Lost；
        /// OnGround（未拾取）的原样保留供下次进入继续拾取（"缺任一关键技术时允许撤离但任务保持
        /// 待补回收"）。<paramref name="survivingLogicIds"/> 为空集合即代表全灭。两件关键物均曾
        /// Recovered 过时把区域标记 Cleared（"允许撤离但任务保持待补回收"意味着未集齐前不算
        /// Cleared）。幂等边界：只处理当前 Carried 态的条目，已经 Recovered/Lost 的历史记录不会被
        /// 重复改写。
        ///
        /// ── ExpeditionCount 不在本方法推进（ER5-EXP-01 修正）──
        /// 此前版本在这里 <c>region.ExpeditionCount += 1</c>，语义是"每次撤离/全灭结算算一次"；
        /// ER5-EXP-01 STORY-EXECUTION-CARDS.md 明确写"只有整个切换成功才增加 expeditionCount"，
        /// 指的是出发那一刻的区域切换事务，不是撤离结算——两个时机都写同一个字段会让它在一次完整
        /// 往返里被计两次，字段含义自相矛盾。唯一写入口现在是
        /// <see cref="ExpeditionDepartureService.TryDepart"/>，本方法不再改写该字段（当时没有任何
        /// 消费方读它，改动不影响 ER5-REGION-01 已验证的行为）。</summary>
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
                if (item.State != RegionQuestItemState.Carried)
                {
                    continue;
                }
                if (survivors.Contains(item.CarrierLogicId))
                {
                    item.State = RegionQuestItemState.Recovered;
                    Log.Info($"[FracturedCityRegion] {item.ContentId}（{item.SalvageInstanceId}）撤离成功，已 Recovered。");
                }
                else
                {
                    item.State = RegionQuestItemState.Lost;
                    if (!lost.Contains(item.SalvageInstanceId))
                    {
                        lost.Add(item.SalvageInstanceId);
                    }
                    Log.Info($"[FracturedCityRegion] {item.ContentId}（{item.SalvageInstanceId}）随携带机器阵亡丢失，已标记 Lost。");
                }
            }
            region.LostQuestSalvageIds = lost.ToArray();

            bool markerRecovered = state.RegionQuestItems.Any(q =>
                q.ContentId == FracturedCityLayout.MarkerModuleContentId && q.State == RegionQuestItemState.Recovered);
            bool databoxRecovered = state.RegionQuestItems.Any(q =>
                q.ContentId == FracturedCityLayout.ProtocolDataboxContentId && q.State == RegionQuestItemState.Recovered);
            if (markerRecovered && databoxRecovered && region.State != RegionState.Cleared)
            {
                region.State = RegionState.Cleared;
                CampaignEventLedger.TryGrant(state, "region_cleared:" + RegionId, "RegionCleared", state.PlaySeconds, RegionId);
                Log.Info("[FracturedCityRegion] 两件关键技术均已 Recovered：破碎都市标记 Cleared。");
            }
        }

        /// <summary>关键物恢复柜：每次 Enter() 调用。对每个内容 ID，若存在处于 Lost 状态、且当前没有
        /// 任何 OnGround/Carried 的同 contentId 条目在流通（即"确实需要保底"），在恢复柜位置重生成
        /// 一份同 contentId、不同 salvageInstanceId 的新条目——不重生已经 Recovered 过的内容
        /// （"一旦该内容成功解析，恢复柜关闭"，本 Story 没有解析系统，用"已 Recovered 过一次"近似
        /// 该收口条件，ER6-ANA-01 落地后可进一步细化，不是本 Story 缺口）。幂等：同一轮 Lost 只会
        /// 补一份在场的保底件，不会无限重复生成。</summary>
        public static void RecoveryLockerCheck(CampaignState state)
        {
            if (state?.RegionQuestItems == null)
            {
                return;
            }
            foreach (string contentId in new[] { FracturedCityLayout.MarkerModuleContentId, FracturedCityLayout.ProtocolDataboxContentId })
            {
                bool everRecovered = state.RegionQuestItems.Any(q => q.ContentId == contentId && q.State == RegionQuestItemState.Recovered);
                if (everRecovered)
                {
                    continue; // 已成功解析过一次，恢复柜关闭。
                }
                bool inPlay = state.RegionQuestItems.Any(q => q.ContentId == contentId &&
                    (q.State == RegionQuestItemState.OnGround || q.State == RegionQuestItemState.Carried));
                bool everLost = state.RegionQuestItems.Any(q => q.ContentId == contentId && q.State == RegionQuestItemState.Lost);
                if (inPlay || !everLost)
                {
                    continue; // 尚未丢失过，或已有一份在场，不需要保底。
                }
                SpawnQuestItemOnGround(state, contentId, FracturedCityLayout.RecoveryLocker.Position);
                Log.Info($"[FracturedCityRegion] 关键物恢复柜：{contentId} 已重生成保底件。");
            }
        }

        // ── 自检 ──────────────────────────────────────────────────────────

        public static List<string> SelfCheckNoDuplicates(CampaignState state)
        {
            var violations = new List<string>();
            int enemyCount = state?.RegionEnemies?.Count(e => e.RegionId == RegionId) ?? 0;
            if (enemyCount != 3)
            {
                violations.Add($"破碎都市敌人实例数应为 3（2 侦察 + 1 干扰），实际 {enemyCount}。");
            }
            var dupCheck = state?.RegionEnemies?.GroupBy(e => e.EnemyInstanceId).Where(g => g.Count() > 1).ToList();
            if (dupCheck != null && dupCheck.Count > 0)
            {
                violations.Add($"存在重复的 EnemyInstanceId：{string.Join(",", dupCheck.Select(g => g.Key))}");
            }
            return violations;
        }
    }
}
