using System;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using TEngine;

namespace GameLogic.Campaign
{
    /// <summary>ER6-LOOP-01 STORY-EXECUTION-CARDS.md：OBJ-05～08 的 <see cref="ObjectiveRecord"/>/
    /// <see cref="CampaignPhase"/> 唯一写入口——两个字段都是 ER1-SAVE-01 留下的纯骨架（<see cref="ObjectiveRecord"/>
    /// 全仓库此前从未被任何生产代码实例化过一条真实记录；<see cref="CampaignState.CampaignPhase"/> 除
    /// <see cref="CampaignState.CreateNew"/> 的默认值外从未被赋值），本类是它们第一次真正驱动的地方。
    ///
    /// ── 范围声明 ──
    /// DEMO-CONTENT-LOCK.md §逐目标持久化表完整列出 OBJ-01～10，但 STORY-EXECUTION-CARDS.md
    /// ER6-LOOP-01 卡片原文只点名"ObjectiveRecord OBJ-05～08"——OBJ-01～04 是 ER1～ER4 已完成 Story 的
    /// 既有产物（当时只要求维护 <see cref="CampaignState.CompletedObjectiveIds"/> 派生快照，未点名
    /// <see cref="ObjectiveRecord"/>），OBJ-09/10 是 ER7-CORE-01/ER7-BEACON-01 的 Boss 战/信标产出。
    /// 本类只负责 OBJ-05～08 四项 + 它们各自触发的 <see cref="CampaignPhase"/> 前进
    /// （FirstExpedition/CrossCompiled/FoundryScouting/SecondCrossCompiled），不抢先实现
    /// CoreAssault/BeaconReady/Completed（那三个值仍保留在枚举里等 ER7 系列使用）。
    ///
    /// ── 结构性判定，不新造标记字段（同 <see cref="FoundryOutpostRegion.RecomputeUnlock"/> 同一纪律）──
    /// 每个 OBJ 的完成条件全部复用既有系统已经维护的权威真相（<see cref="RegionRecord.State"/>/
    /// <see cref="CampaignState.UnlockedContentIds"/>/<see cref="BlueprintEditorService.IsReactionCharged"/>/
    /// <see cref="FoundryOutpostRegion.ComputeCoreGateLights"/>……），本类只做"观察+记一次幂等完成事件+
    /// 推进 CampaignPhase"，不引入第二套判定逻辑，也不能被"点击完成"之类的捷径绕过——玩家唯一能让这些
    /// 条件变真的路径就是真的把游戏内容做完。
    ///
    /// ── 幂等 ──
    /// 每个目标的完成事件 id 是 <c>objective_complete:{campaignId}:{objectiveId}</c>，经
    /// <see cref="CampaignEventLedger.TryGrant"/> 幂等——重复调用 <see cref="Recompute"/>（多个调用点
    /// 天然会重复触发，见下）、重复读档都不会二次记录/二次推进阶段/二次发奖（AC-CAM-001 字面要求）。</summary>
    public static class CampaignObjectiveTracker
    {
        public const string Obj05 = "OBJ-05";
        public const string Obj06 = "OBJ-06";
        public const string Obj07 = "OBJ-07";
        public const string Obj08 = "OBJ-08";
        /// <summary>ER7-BEACON-01：OBJ-09/10 补齐 ER6-LOOP-01 当时明确排除在范围外的两项（"OBJ-09/10是
        /// ER7产出，均不在本Story范围"）——ER7-CORE-01/ER7-FAIL-01 落地后，OBJ-09 的四个子条件
        /// （核心进攻正式出发/两节点毁/Boss Destroyed/核心数据入货舱且成功撤离）已经全部有真实权威
        /// 字段可读，本 Story 是它们第一次被真正聚合判定的地方。</summary>
        public const string Obj09 = "OBJ-09";
        public const string Obj10 = "OBJ-10";

        /// <summary>ER7-BEACON-01：信标启动一次性事件 id——<see cref="Regions.HomeValleyBeacon"/> 的
        /// 10秒不可取消演出结束时唯一写入口 <see cref="CampaignEventLedger.TryGrant"/> 授予，OBJ-10 的
        /// 完成条件就是"这个事件是否已被授予过"（结构性判定，不额外发明第二个"已启动"标记字段）。</summary>
        public const string BeaconLaunchEventId = "beacon_launch_confirmed";

        /// <summary>唯一重算入口——调用点：<see cref="ExpeditionReturnService.TryConfirmEvacuation"/>/
        /// <see cref="ExpeditionReturnService.TryConfirmAbandon"/>（撤离事务提交时，OBJ-05/07"战利品
        /// 判定发生在撤离事务提交时，不在拾取瞬间"字面要求）、<see cref="BlueprintEditorService.TrySave"/>
        /// （蓝图保存时，OBJ-06/08 的"蓝图保存"条件）、<see cref="HomeValleyAnalysis"/> 解析完成时
        /// （OBJ-06/08 的"解析"条件）、<see cref="HomeValleyFactory"/> 回厂改造完工时（OBJ-06 的
        /// "ERC-003 改造"条件）。全部是纯查询+幂等写，重复调用/调用先后顺序颠倒都不会产生错误结果，
        /// 不需要调用方保证唯一或最先调用——这也是"全链每一步核对"能够只靠一份逻辑覆盖多个触发点的
        /// 原因（不必在每个调用点各自判断"这一步是不是该轮到我更新目标了"）。</summary>
        public static void Recompute(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            RecomputeObj05(state);
            RecomputeObj06(state);
            RecomputeObj07(state);
            RecomputeObj08(state);
            RecomputeObj09(state);
            RecomputeObj10(state);
        }

        /// <summary>出发时机的 CampaignPhase 前进——与 OBJ 完成结算是两条独立的触发线
        /// （DEMO-CONTENT-LOCK.md"其中 FirstExpedition、FoundryScouting 在对应出发时即可进入，完成后
        /// 仍保留该阶段直到下一次编译"）。唯一调用点 <see cref="ExpeditionDepartureService.TryDepart"/>。
        ///
        /// ── ER7-BEACON-01 追加：CoreAssault 判定 ──
        /// "第三次出击（核心进攻）"与"第二次出击（外围侦察）"共用同一个
        /// <see cref="ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost"/>（ER6-REGION-01 既定
        /// 架构，核心分区门禁在外围场景内部解决），本类用 <see cref="FoundryOutpostRegion.CanEnterCoreZone"/>
        /// 在出发那一刻是否已经为真来区分这是"去外围"还是"去核心"——与 90暴露核心入口增援
        /// （<see cref="FoundryOutpostRegion.ReconcileCoreReinforcement"/>）同一判别信号，不新造第二套。</summary>
        public static void OnDeparted(CampaignState state, ExpeditionDepartureService.ExpeditionTarget target)
        {
            if (state == null)
            {
                return;
            }
            if (target == ExpeditionDepartureService.ExpeditionTarget.SilentRuins)
            {
                AdvancePhase(state, CampaignPhase.FirstExpedition);
            }
            else if (target == ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost)
            {
                bool isCoreAssault = FoundryOutpostRegion.CanEnterCoreZone(state).Success;
                AdvancePhase(state, isCoreAssault ? CampaignPhase.CoreAssault : CampaignPhase.FoundryScouting);
            }
        }

        /// <summary>只前进不后退——失败重试/读档不会让已经达到的阶段倒退（AC-CAM-002"失败重试不跳阶段"
        /// 的另一半含义：也不倒退阶段）。</summary>
        private static void AdvancePhase(CampaignState state, CampaignPhase phase)
        {
            if (phase > state.CampaignPhase)
            {
                CampaignPhase before = state.CampaignPhase;
                state.CampaignPhase = phase;
                Log.Info($"[CampaignObjectiveTracker] campaignPhase {before} → {phase}。");
            }
        }

        // ── 查询/写入 ObjectiveRecord 的公共骨架 ─────────────────────────────

        private static ObjectiveRecord FindRecord(CampaignState state, string objectiveId) =>
            state.ObjectiveRecords?.FirstOrDefault(r => r.ObjectiveId == objectiveId);

        public static ObjectiveState StateOf(CampaignState state, string objectiveId) =>
            state == null ? ObjectiveState.Locked : FindRecord(state, objectiveId)?.State ?? ObjectiveState.Locked;

        public static bool IsCompleted(CampaignState state, string objectiveId) =>
            StateOf(state, objectiveId) == ObjectiveState.Completed;

        private static ObjectiveRecord EnsureRecord(CampaignState state, string objectiveId)
        {
            ObjectiveRecord rec = FindRecord(state, objectiveId);
            if (rec != null)
            {
                return rec;
            }
            rec = new ObjectiveRecord
            {
                ObjectiveId = objectiveId,
                State = ObjectiveState.Locked,
                StartedAtPlaySeconds = 0f,
                CompletedAtPlaySeconds = 0f,
                EventId = null,
            };
            state.ObjectiveRecords = (state.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>()).Append(rec).ToArray();
            return rec;
        }

        private static void Activate(CampaignState state, string objectiveId)
        {
            ObjectiveRecord rec = EnsureRecord(state, objectiveId);
            if (rec.State != ObjectiveState.Locked)
            {
                return; // 已经 Active 或 Completed——不倒退、不重置 StartedAtPlaySeconds。
            }
            rec.State = ObjectiveState.Active;
            rec.StartedAtPlaySeconds = state.PlaySeconds;
        }

        /// <summary>完成一个目标——幂等（已完成直接返回），可选联动推进 <see cref="CampaignPhase"/>。</summary>
        private static void Complete(CampaignState state, string objectiveId, CampaignPhase? resultPhase)
        {
            ObjectiveRecord rec = EnsureRecord(state, objectiveId);
            if (rec.State == ObjectiveState.Completed)
            {
                return;
            }
            if (rec.State == ObjectiveState.Locked)
            {
                Activate(state, objectiveId); // 防御：理论上调用方总会先 Activate，这里兜底不留缺口。
            }
            string eventId = $"objective_complete:{state.CampaignId}:{objectiveId}";
            CampaignEventLedger.TryGrant(state, eventId, "ObjectiveComplete", state.PlaySeconds, objectiveId);
            rec.State = ObjectiveState.Completed;
            rec.CompletedAtPlaySeconds = state.PlaySeconds;
            rec.EventId = eventId;
            state.CompletedObjectiveIds = (state.CompletedObjectiveIds ?? Array.Empty<string>())
                .Where(id => id != objectiveId).Append(objectiveId).ToArray();
            if (resultPhase.HasValue)
            {
                AdvancePhase(state, resultPhase.Value);
            }
            Log.Info($"[CampaignObjectiveTracker] {objectiveId} 已完成。");
        }

        // ── 逐目标判定（结构性，复用既有权威字段，不重复实现判定逻辑）─────────

        /// <summary>DEMO-CONTENT-LOCK.md："OBJ-05 破碎都市：带回静默技术｜OBJ-04，区域 silent_ruins
        /// 正式出发，标记器与协议数据盒均在货舱且撤离结算成功｜FirstExpedition 在出发时进入，结算后
        /// 保持"——本类不追踪 OBJ-04，用"已正式出发过"（ExpeditionCount&gt;0）近似其前置；完成条件是
        /// 两件关键物均已 Recovered（不是拾取瞬间，是撤离结算之后）。结算后不改 CampaignPhase（已经由
        /// <see cref="OnDeparted"/> 在出发时设置好，这里"保持"）。</summary>
        private static void RecomputeObj05(CampaignState state)
        {
            if (IsCompleted(state, Obj05))
            {
                return;
            }
            RegionRecord ruins = FracturedCityRegion.Find(state);
            if (ruins == null || ruins.ExpeditionCount <= 0)
            {
                return; // 尚未正式出发过，维持 Locked。
            }
            Activate(state, Obj05);

            bool markerRecovered = state.RegionQuestItems != null && state.RegionQuestItems.Any(q =>
                q.ContentId == FracturedCityLayout.MarkerModuleContentId && q.State == RegionQuestItemState.Recovered);
            bool databoxRecovered = state.RegionQuestItems != null && state.RegionQuestItems.Any(q =>
                q.ContentId == FracturedCityLayout.ProtocolDataboxContentId && q.State == RegionQuestItemState.Recovered);
            if (markerRecovered && databoxRecovered)
            {
                Complete(state, Obj05, resultPhase: null);
            }
        }

        /// <summary>"OBJ-06 解析并实装标记跳转｜OBJ-05，两技术解析、蓝图保存且 ERC-003 正式回厂改造
        /// 完成｜CrossCompiled；开放铸造外围侦察"——四个子条件全部结构性读取既有权威字段，不新造
        /// 任何标记：func_marker/fw_marktag 是否已解锁、标记跳转反应是否被 <see cref="BlueprintEditorService.TrySave"/>
        /// 充过技术数据、是否存在一台 ERC-003 底盘且蓝图已不是出厂默认（与
        /// <see cref="FoundryOutpostRegion.RecomputeUnlock"/> 的 erc003Retrofitted 同一判据，本类独立
        /// 复查一次而非依赖该方法的私有布尔，避免跨类耦合内部实现细节）。</summary>
        private static void RecomputeObj06(CampaignState state)
        {
            if (IsCompleted(state, Obj06))
            {
                return;
            }
            if (!IsCompleted(state, Obj05))
            {
                return;
            }
            Activate(state, Obj06);

            bool markerFuncUnlocked = state.UnlockedContentIds != null && state.UnlockedContentIds.Contains(ComponentCatalog.FuncMarkerId);
            bool markTagFwUnlocked = state.UnlockedContentIds != null && state.UnlockedContentIds.Contains(FirmwareCatalog.FwMarkTagId);
            bool markJumpCharged = BlueprintEditorService.IsReactionCharged(state, MechanicalReactionCatalog.ReactionMarkJumpId);
            bool erc003Retrofitted = MachineRegistry.AllRecords.Any(m =>
                m.IsAlive && m.ChassisId == HomeValleyLayout.Erc003ChassisId && m.BlueprintId != HomeValleyLayout.BlueprintErc003Id);

            if (markerFuncUnlocked && markTagFwUnlocked && markJumpCharged && erc003Retrofitted)
            {
                Complete(state, Obj06, CampaignPhase.CrossCompiled);
            }
        }

        /// <summary>"OBJ-07 铸造外围：带回重炮｜OBJ-06，foundry_outpost 正式出发、重炮入货舱、主动撤离
        /// 结算成功｜FoundryScouting 在出发时进入，结算后保持"——直接读
        /// <see cref="RegionRecord.State"/>==<see cref="RegionState.Cleared"/>，这正是
        /// <see cref="FoundryOutpostRegion.ResolveExtraction"/> 判定"铸造重炮模块已 Recovered"后写入的
        /// 同一个权威字段，不重复判定一遍。</summary>
        private static void RecomputeObj07(CampaignState state)
        {
            if (IsCompleted(state, Obj07))
            {
                return;
            }
            if (!IsCompleted(state, Obj06))
            {
                return;
            }
            RegionRecord foundry = FoundryOutpostRegion.Find(state);
            if (foundry == null || foundry.State == RegionState.Locked)
            {
                return;
            }
            Activate(state, Obj07);

            if (foundry.State == RegionState.Cleared)
            {
                Complete(state, Obj07, resultPhase: null);
            }
        }

        /// <summary>"OBJ-08 解析并实装熔穿过载｜OBJ-07，重炮解析、反应蓝图保存、至少一台现役机实装｜
        /// SecondCrossCompiled；核心门三项灯全亮"——条件与 <see cref="FoundryOutpostRegion.CanEnterCoreZone"/>
        /// 的三灯逐字一致，直接复用 <see cref="FoundryOutpostRegion.ComputeCoreGateLights"/>，不重新
        /// 判定第二遍（"核心门解锁"与"OBJ-08 完成"在设计上就是同一件事的两种呈现）。</summary>
        private static void RecomputeObj08(CampaignState state)
        {
            if (IsCompleted(state, Obj08))
            {
                return;
            }
            if (!IsCompleted(state, Obj07))
            {
                return;
            }
            Activate(state, Obj08);

            if (FoundryOutpostRegion.ComputeCoreGateLights(state).AllReady)
            {
                Complete(state, Obj08, CampaignPhase.SecondCrossCompiled);
            }
        }

        /// <summary>"OBJ-09 摧毁主核心并回收数据｜OBJ-08，核心进攻正式出发（正常为第三次）、两供能
        /// 节点毁、Boss Destroyed、核心数据入货舱且成功撤离｜CoreAssault 在进攻出发时进入；结算后
        /// BeaconReady，解锁导航信标配方"——"两供能节点毁"结构上蕴含在 Boss 能到达
        /// <see cref="CoreBossState.Destroyed"/> 这一事实里（节点不毁就进不了 Phase1，进不了 Phase1
        /// 就打不到 Destroyed，见 <see cref="FoundryOutpostCoreBoss.ApplyDamage"/> 的门禁），不需要
        /// 再单独查一遍两条 RegionEnemyRecord。"核心数据入货舱且成功撤离"就是
        /// <see cref="FoundryOutpostLayout.CoreDataContentId"/> 已 Recovered（撤离结算时写入，同
        /// OBJ-05/07 的"战利品判定发生在撤离事务提交时"字面要求）。</summary>
        private static void RecomputeObj09(CampaignState state)
        {
            if (IsCompleted(state, Obj09))
            {
                return;
            }
            if (!IsCompleted(state, Obj08))
            {
                return;
            }
            RegionRecord foundry = FoundryOutpostRegion.Find(state);
            if (foundry == null || foundry.State == RegionState.Locked)
            {
                return;
            }
            Activate(state, Obj09);

            bool bossDestroyed = FoundryOutpostCoreBoss.GetState(foundry) == CoreBossState.Destroyed;
            bool coreDataRecovered = state.RegionQuestItems != null && state.RegionQuestItems.Any(q =>
                q.ContentId == FoundryOutpostLayout.CoreDataContentId && q.State == RegionQuestItemState.Recovered);
            if (bossDestroyed && coreDataRecovered)
            {
                Complete(state, Obj09, CampaignPhase.BeaconReady);
            }
        }

        /// <summary>"OBJ-10 建造并启动返航信标｜OBJ-09，信标Operational、Powered、玩家用E确认启动｜
        /// Completed；10秒演出与一次性结算"——完成条件是 <see cref="BeaconLaunchEventId"/> 事件已被
        /// 授予（唯一写入口 <see cref="Regions.HomeValleyBeacon"/> 的10秒不可取消演出结束时），本方法
        /// 只负责观察这个事件并推进 <see cref="CampaignPhase.Completed"/>，不重复判定
        /// Operational/Powered/二次确认——那些是 <see cref="Regions.HomeValleyBeacon.TryStartLaunch"/>
        /// 自己的门禁，事件只有真正通过门禁才会被授予。</summary>
        private static void RecomputeObj10(CampaignState state)
        {
            if (IsCompleted(state, Obj10))
            {
                return;
            }
            if (!IsCompleted(state, Obj09))
            {
                return;
            }
            Activate(state, Obj10);

            bool launched = state.EventLedger != null && state.EventLedger.Any(e => e.EventId == BeaconLaunchEventId);
            if (launched)
            {
                Complete(state, Obj10, CampaignPhase.Completed);
            }
        }
    }
}
