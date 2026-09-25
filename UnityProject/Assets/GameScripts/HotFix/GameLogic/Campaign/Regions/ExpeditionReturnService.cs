using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Core;
using GameLogic.Stage;
using TEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER5-RETURN-01 STORY-EXECUTION-CARDS.md：撤离确认面板的数据源 + 回城结算事务的唯一实现——
    /// 与 <see cref="ExpeditionDepartureService"/> 是同一份"面板数据 + 事务编排"分工在返程方向的镜像。
    ///
    /// ── 与既有系统的分工（不重复实现）──
    /// 真正的"谁存活/谁阵亡/关键物 Recovered 还是 Lost"结算已经是 <see cref="FracturedCityRegion.ResolveExtraction"/>
    /// （ER5-REGION-01）；"卸载区域/机器 RegionId 写回家园"已经是 <see cref="FracturedCityController.Exit"/>
    /// （同一 Story）。本类只补两件此前真正缺失的事：①一个可取消的确认面板数据源（此前
    /// <c>FracturedCityController</c> 的"evac:"交互候选是硬编码占位，见该类 DEBT-ER5INT01-01）；
    /// ②确认后的收尾——经历/伤势标记（<see cref="MachineExperienceFlags.Expedition"/>/<c>Returned</c>/
    /// <c>SeverelyInjured</c> 八项经历表里此前从未有过真实触发源，登记在 DEBT-ER4MCH01-01）与远征结算后
    /// 自动存档（<see cref="SaveReason.ExpeditionResolutionComplete"/>，该枚举值此前只有 TODO 注释、
    /// 从未被任何代码使用过）。
    ///
    /// ── 全灭与主动撤离共用同一条 Exit 路径 ──
    /// <see cref="FracturedCityController.Exit"/> 内部靠 <c>_wipeResolved</c>（本类通过
    /// <see cref="FracturedCityController.IsWiped"/> 只读）区分：全灭时 <c>TickWipeDetection</c> 已经
    /// 提前调用过 <see cref="FracturedCityRegion.ResolveExtraction"/>（结算为全 Lost），
    /// <c>Exit(true)</c> 内的"评估"分支会因为 <c>_wipeResolved</c> 已真而跳过重复结算——两条路径
    /// （<see cref="TryConfirmEvacuation"/>/<see cref="TryConfirmAbandon"/>）因此都能安全调用同一个
    /// <c>Exit(true)</c>，不需要在本类里分叉出第二套卸载逻辑。</summary>
    public static class ExpeditionReturnService
    {
        /// <summary>伤势判定阈值——DEMO-CONTENT-LOCK.md/ERD-MCH 系列均未点名具体百分比，本 Story 按
        /// "明显但不用刻意打空血才触发"取一个保守默认值，与 ER5-SILENT-01 类似情形（数值未点名时
        /// 记录取舍理由，不臆造成文档摘录）一致。</summary>
        private const float InjuredHealthFraction = 0.99f; // 只要挨过打（未满血）即记一条战斗损伤。
        private const float SeverelyInjuredHealthFraction = 0.3f;

        /// <summary>ER6-REGION-01：本类此前硬编码 <see cref="GameRoot.FracturedCity"/>，现推广支持
        /// 铸造前哨外围——两个 Controller 具体类型不同但都暴露 IsActive/IsWiped/Exit(bool) 同一形状，
        /// 用委托打包成一份统一上下文，不给两者加共享接口（避免为了这一件事改动两个已验收 Story 的
        /// 类型结构）。</summary>
        private readonly struct ActiveExpedition
        {
            public readonly bool Active;
            public readonly string RegionId;
            public readonly bool IsWiped;
            public readonly Action<bool> Exit;

            public ActiveExpedition(bool active, string regionId, bool isWiped, Action<bool> exit)
            {
                Active = active;
                RegionId = regionId;
                IsWiped = isWiped;
                Exit = exit;
            }

            public static readonly ActiveExpedition None = new ActiveExpedition(false, null, false, null);
        }

        private static ActiveExpedition ResolveActive()
        {
            FracturedCityController fc = GameRoot.FracturedCity;
            if (fc != null && fc.IsActive)
            {
                return new ActiveExpedition(true, FracturedCityLayout.RegionId, fc.IsWiped, fc.Exit);
            }
            FoundryOutpostController fo = GameRoot.FoundryOutpost;
            if (fo != null && fo.IsActive)
            {
                return new ActiveExpedition(true, FoundryOutpostLayout.RegionId, fo.IsWiped, fo.Exit);
            }
            return ActiveExpedition.None;
        }

        public readonly struct ManifestEntry
        {
            public readonly int LogicId;
            public readonly int DisplayNumber;
            public readonly string ChassisId;
            public readonly bool IsAlive;
            public readonly float Health;
            public readonly float MaxHealth;

            public ManifestEntry(int logicId, int displayNumber, string chassisId, bool isAlive, float health, float maxHealth)
            {
                LogicId = logicId;
                DisplayNumber = displayNumber;
                ChassisId = chassisId;
                IsAlive = isAlive;
                Health = health;
                MaxHealth = maxHealth;
            }
        }

        public readonly struct KeyTechEntry
        {
            public readonly string ContentId;
            public readonly RegionQuestItemState State;
            /// <summary>玩家可见现状（与常驻目标条同一份 <see cref="CampaignObjectiveCatalog.QuestStatusText"/>）。</summary>
            public readonly string StatusText;

            public KeyTechEntry(string contentId, RegionQuestItemState state, string statusText)
            {
                ContentId = contentId;
                State = state;
                StatusText = statusText;
            }
        }

        public readonly struct ReturnSnapshot
        {
            public readonly bool Available;
            public readonly bool IsWipe;
            public readonly ManifestEntry[] Roster;
            public readonly KeyTechEntry[] KeyTech;
            public readonly bool ObjectivesComplete;
            public readonly int GroundScrapItemCount;
            /// <summary>ER6-REGION-01：撤离结算的目标区域 RegionId，UI 据此选择"破碎都市"/"铸造前哨
            /// 外围（侦察成功）"两套用语，不再假设永远是破碎都市。</summary>
            public readonly string RegionId;
            /// <summary>ER8：这次是核心进攻（封锁门已开或主核心已激活）——撤离面板改用核心进攻用语，
            /// 关键物改列核心数据（此前仍列早已带回的重炮，并提示“核心区仍封锁”）。</summary>
            public readonly bool CoreAssault;
            public readonly bool BossDestroyed;

            public ReturnSnapshot(bool available, bool isWipe, ManifestEntry[] roster, KeyTechEntry[] keyTech,
                bool objectivesComplete, int groundScrapItemCount, string regionId, bool coreAssault = false, bool bossDestroyed = false)
            {
                Available = available;
                IsWipe = isWipe;
                Roster = roster ?? Array.Empty<ManifestEntry>();
                KeyTech = keyTech ?? Array.Empty<KeyTechEntry>();
                ObjectivesComplete = objectivesComplete;
                GroundScrapItemCount = groundScrapItemCount;
                RegionId = regionId;
                CoreAssault = coreAssault;
                BossDestroyed = bossDestroyed;
            }

            public static readonly ReturnSnapshot Unavailable = new ReturnSnapshot(
                false, false, Array.Empty<ManifestEntry>(), Array.Empty<KeyTechEntry>(), false, 0, null);
        }

        /// <summary>撤离面板要列的关键物（自检直接断言）：破碎都市两件（标记器/协议数据盒），铸造前哨外围一件
        /// （重炮模块，三种可选缓存不计入"关键模块"门槛，DEMO-CONTENT-LOCK.md §4.2第3条）；核心进攻（封锁门已开
        /// 或主核心已激活）只列核心数据——重炮早在侦察时带回。</summary>
        public static string[] KeyContentIdsFor(CampaignState state, string regionId, out bool coreAssault, out bool bossDestroyed)
        {
            coreAssault = false;
            bossDestroyed = false;
            if (regionId != FoundryOutpostLayout.RegionId)
            {
                return new[] { FracturedCityLayout.MarkerModuleContentId, FracturedCityLayout.ProtocolDataboxContentId };
            }
            RegionRecord foundry = FoundryOutpostRegion.Find(state);
            coreAssault = foundry != null
                && (FoundryOutpostCoreBoss.IsInitialized(foundry) || FoundryOutpostRegion.ComputeCoreGateLights(state).AllReady);
            bossDestroyed = coreAssault && FoundryOutpostCoreBoss.GetState(foundry) == CoreBossState.Destroyed;
            return coreAssault
                ? new[] { FoundryOutpostLayout.CoreDataContentId }
                : new[] { FoundryOutpostLayout.CannonModuleContentId };
        }

        /// <summary>面板刷新用的整份快照——已上车（存活成员，撤离后其携带的 Carried 关键物变
        /// Recovered）/遗留货物（地面废料条目数）/幸存阵亡（<see cref="ManifestEntry.IsAlive"/>）/
        /// 未完成目标（<see cref="ReturnSnapshot.ObjectivesComplete"/>）/当前技术是否仍不可解析
        /// （<see cref="KeyTechEntry.State"/> 非 Recovered 即"仍不可解析"）一次性给全。ER6-REGION-01：
        /// 泛化为两个远征区域共用（<see cref="ResolveActive"/>），不再硬编码破碎都市。</summary>
        public static ReturnSnapshot BuildSnapshot(CampaignState state)
        {
            ActiveExpedition active = ResolveActive();
            if (state == null || !active.Active)
            {
                return ReturnSnapshot.Unavailable;
            }

            ManifestEntry[] roster = MachineRegistry.AllRecords
                .Where(m => m != null && m.RegionId == active.RegionId)
                .OrderBy(m => m.DisplayNumber)
                .Select(m => new ManifestEntry(m.LogicId, m.DisplayNumber, m.ChassisId, m.IsAlive, m.Health, m.MaxHealth))
                .ToArray();

            RegionRecord region = active.RegionId == FoundryOutpostLayout.RegionId
                ? FoundryOutpostRegion.Find(state)
                : FracturedCityRegion.Find(state);
            string[] keyContentIds = KeyContentIdsFor(state, active.RegionId, out bool coreAssault, out bool bossDestroyed);

            // 同一关键物多条记录时的取舍（已带回 > 已装车 > 在地面 > 已丢失）与现状文字都走目标表的同一份实现。
            KeyTechEntry[] keyTech = keyContentIds
                .Select(contentId => new KeyTechEntry(contentId,
                    CampaignObjectiveCatalog.BestQuestItem(state, contentId)?.State ?? RegionQuestItemState.OnGround,
                    CampaignObjectiveCatalog.QuestStatusText(state, contentId)))
                .ToArray();
            int groundScrap = state.GroundItems?.Count(g => g.RegionId == active.RegionId) ?? 0;

            return new ReturnSnapshot(true, active.IsWiped, roster, keyTech,
                region != null && region.State == RegionState.Cleared, groundScrap, active.RegionId, coreAssault, bossDestroyed);
        }

        public readonly struct ReturnResult
        {
            public readonly bool Success;
            public readonly string FailureReason;

            private ReturnResult(bool success, string failureReason)
            {
                Success = success;
                FailureReason = failureReason;
            }

            public static ReturnResult Ok() => new ReturnResult(true, null);
            public static ReturnResult Fail(string reason) => new ReturnResult(false, reason);
        }

        /// <summary>玩家主动确认撤离（非全灭）。幂等防护：区域已不活跃（上一次点击已经切场成功，
        /// 这是重复点击的第二次事件）直接安全拒绝，不重复标记经历/不重复存档。</summary>
        public static ReturnResult TryConfirmEvacuation()
        {
            ActiveExpedition active = ResolveActive();
            if (!active.Active)
            {
                return ReturnResult.Fail("not-active");
            }
            if (active.IsWiped)
            {
                return ReturnResult.Fail("use-abandon-for-wipe");
            }
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return ReturnResult.Fail("no-active-campaign");
            }

            InputRouter.SetModalUi(true);
            try
            {
                MarkExperienceAndInjuryBeforeExit(state, active.RegionId);
                active.Exit(true);
                // ER6-LOOP-01：撤离事务已提交（active.Exit 内部完成 ResolveExtraction），OBJ-05/07 的
                // "战利品判定发生在撤离事务提交时"字面要求就是这一刻，不等回到家园下一帧。
                CampaignObjectiveTracker.Recompute(state);
                // ER7-FAIL-01：主动撤离（非全灭）如果 Boss 还没打完（Phase!=Destroyed），本次尝试作废，
                // 下次进核心分区重新开始，不允许带着半血 Boss 状态跨出击继续磨。
                if (active.RegionId == FoundryOutpostLayout.RegionId)
                {
                    FoundryOutpostCoreBoss.ResetToPreBossState(state, FoundryOutpostRegion.Find(state));
                }
                GameRoot.ResumeHomeValley();

                SaveResult saveResult = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionResolutionComplete);
                if (!saveResult.Success)
                {
                    Log.Warning($"[ExpeditionReturnService] 远征结算后自动存档未成功（{saveResult.Outcome} " +
                        $"{saveResult.Message}）——切场本身已完成，不回滚，下次自然存档点会补上。");
                }
                // ER8-CONTENT-01：撤离事务提交并回到家园（全灭放弃不走这里，全灭那一刻已有失败提示）。
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Evacuate);
                return ReturnResult.Ok();
            }
            finally
            {
                InputRouter.SetModalUi(false);
            }
        }

        /// <summary>全灭"放弃远征"确认——唯一区别是不要求玩家有任何存活机器（本就没有）。
        /// 结算已经由 <c>TickWipeDetection</c> 提前做过（全 Lost），这里只做切场+经历标记（全员
        /// <see cref="MachineExperienceFlags.Expedition"/>，没有幸存者所以不会有 <c>Returned</c>）+存档。</summary>
        public static ReturnResult TryConfirmAbandon()
        {
            ActiveExpedition active = ResolveActive();
            if (!active.Active)
            {
                return ReturnResult.Fail("not-active");
            }
            if (!active.IsWiped)
            {
                return ReturnResult.Fail("not-wiped");
            }
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return ReturnResult.Fail("no-active-campaign");
            }

            InputRouter.SetModalUi(true);
            try
            {
                MarkExperienceAndInjuryBeforeExit(state, active.RegionId);
                active.Exit(true); // _wipeResolved 已真，内部跳过重复结算，见类注释。
                // ER6-LOOP-01：同 TryConfirmEvacuation——全灭放弃同样是一次"撤离事务提交"（结算为全
                // Lost），Recompute 幂等，即使本次不会推进任何 OBJ 也不会产生副作用。
                CampaignObjectiveTracker.Recompute(state);
                // ER7-FAIL-01：全灭同样作废未完成的 Boss 尝试——同 TryConfirmEvacuation 分支。
                if (active.RegionId == FoundryOutpostLayout.RegionId)
                {
                    FoundryOutpostCoreBoss.ResetToPreBossState(state, FoundryOutpostRegion.Find(state));
                }
                GameRoot.ResumeHomeValley();

                SaveResult saveResult = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionResolutionComplete);
                if (!saveResult.Success)
                {
                    Log.Warning($"[ExpeditionReturnService] 放弃远征后自动存档未成功（{saveResult.Outcome} " +
                        $"{saveResult.Message}）——切场本身已完成，不回滚。");
                }
                return ReturnResult.Ok();
            }
            finally
            {
                InputRouter.SetModalUi(false);
            }
        }

        /// <summary>必须在 Controller.Exit 之前调用——之后存活机器的
        /// <see cref="MachineRecord.RegionId"/> 已经被改写回家园，"当前还在本区域"这个筛选条件会失效。
        /// 全员（含阵亡者）记一次 <see cref="MachineExperienceFlags.Expedition"/>（"首次参与远征"与
        /// 是否生还无关）；只有存活者额外记 <see cref="MachineExperienceFlags.Returned"/>、递增
        /// <see cref="MachineRecord.ExpeditionsCompleted"/>、按当前 HP 比例写伤势（
        /// <see cref="MachineRecord.InjuryFlags"/> 此前是从未被写过的骨架字段，本 Story 首次接入真实
        /// 触发源——ER5-SILENT-01 起机器在区域内才会真的掉血，此前没有数据可写）。ER6-REGION-01：
        /// 按 <paramref name="regionId"/> 泛化，不再硬编码破碎都市。</summary>
        private static void MarkExperienceAndInjuryBeforeExit(CampaignState state, string regionId)
        {
            List<MachineRecord> inRegion = MachineRegistry.AllRecords
                .Where(m => m != null && m.RegionId == regionId)
                .ToList();

            foreach (MachineRecord m in inRegion)
            {
                MachineRegistry.TryMarkExperience(m.LogicId, MachineExperienceFlags.Expedition);
                if (!m.IsAlive)
                {
                    continue;
                }
                MachineRegistry.TryMarkExperience(m.LogicId, MachineExperienceFlags.Returned);
                MachineRegistry.RecordExpeditionCompleted(m.LogicId);

                if (m.MaxHealth > 0f && m.Health < m.MaxHealth * InjuredHealthFraction)
                {
                    MachineRegistry.TryMarkInjury(m.LogicId, $"战斗损伤（HP {m.Health:F0}/{m.MaxHealth:F0}）");
                }
                if (m.MaxHealth > 0f && m.Health <= m.MaxHealth * SeverelyInjuredHealthFraction)
                {
                    MachineRegistry.TryMarkExperience(m.LogicId, MachineExperienceFlags.SeverelyInjured);
                }
            }
        }
    }
}
