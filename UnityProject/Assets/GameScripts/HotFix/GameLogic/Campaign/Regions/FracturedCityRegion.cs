using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>逐帧驱动静默侦察机标记周期（DEMO-CONTENT-LOCK.md §4.1"每8秒标记"）。敌人数量
        /// 个位数，O(1) 量级，不违反热更层性能纪律。标记本身只是"可读"的节奏事件（天线扫描/短鸣），
        /// 真正的检测后果（触发交战/警戒升级 AI）属于 ER5-SILENT-01，本 Story 只保证节奏计时器真实
        /// 存在且写 <see cref="RegionRecord.EnemyAlertLevel"/>（供 HUD/后续 Story 直接读取）。</summary>
        public static void TickEnemies(CampaignState state, float dt)
        {
            if (state?.RegionEnemies == null || dt <= 0f)
            {
                return;
            }
            RegionRecord region = Find(state);
            foreach (RegionEnemyRecord enemy in state.RegionEnemies)
            {
                if (!enemy.IsAlive || enemy.EnemyTypeId != EnemyCatalog.ScoutId)
                {
                    continue;
                }
                enemy.CycleCooldownRemaining -= dt;
                if (enemy.CycleCooldownRemaining <= 0f)
                {
                    enemy.CycleCooldownRemaining = FracturedCityLayout.ScoutMarkIntervalSeconds;
                    if (region != null)
                    {
                        region.EnemyAlertLevel = Mathf.Clamp(region.EnemyAlertLevel + 5f, 0f, 100f);
                    }
                    Log.Info($"[FracturedCityRegion] {enemy.EnemyInstanceId} 天线扫描标记（周期性节奏事件）。");
                }
            }
            if (region != null && region.EnemyAlertLevel > 0f)
            {
                region.EnemyAlertLevel = Mathf.Max(0f, region.EnemyAlertLevel - dt); // 缓慢衰减，非目标：完整警戒 AI。
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
                Log.Info($"[FracturedCityRegion] 敌人 {enemyInstanceId} 已阵亡。");
            }
            return ActionResult.Ok();
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
        /// 待补回收"）。<paramref name="survivingLogicIds"/> 为空集合即代表全灭。每次调用推进
        /// <see cref="RegionRecord.ExpeditionCount"/>；两件关键物均曾 Recovered 过时把区域标记
        /// Cleared（"允许撤离但任务保持待补回收"意味着未集齐前不算 Cleared）。幂等边界：只处理当前
        /// Carried 态的条目，已经 Recovered/Lost 的历史记录不会被重复改写。</summary>
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
            region.ExpeditionCount += 1;

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
