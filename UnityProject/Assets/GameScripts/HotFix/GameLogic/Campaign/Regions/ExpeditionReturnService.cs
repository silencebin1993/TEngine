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

            public KeyTechEntry(string contentId, RegionQuestItemState state)
            {
                ContentId = contentId;
                State = state;
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

            public ReturnSnapshot(bool available, bool isWipe, ManifestEntry[] roster, KeyTechEntry[] keyTech,
                bool objectivesComplete, int groundScrapItemCount)
            {
                Available = available;
                IsWipe = isWipe;
                Roster = roster ?? Array.Empty<ManifestEntry>();
                KeyTech = keyTech ?? Array.Empty<KeyTechEntry>();
                ObjectivesComplete = objectivesComplete;
                GroundScrapItemCount = groundScrapItemCount;
            }

            public static readonly ReturnSnapshot Unavailable = new ReturnSnapshot(
                false, false, Array.Empty<ManifestEntry>(), Array.Empty<KeyTechEntry>(), false, 0);
        }

        /// <summary>面板刷新用的整份快照——已上车（存活成员，撤离后其携带的 Carried 关键物变
        /// Recovered）/遗留货物（地面废料条目数）/幸存阵亡（<see cref="ManifestEntry.IsAlive"/>）/
        /// 未完成目标（<see cref="ReturnSnapshot.ObjectivesComplete"/>）/当前技术是否仍不可解析
        /// （<see cref="KeyTechEntry.State"/> 非 Recovered 即"仍不可解析"）一次性给全。</summary>
        public static ReturnSnapshot BuildSnapshot(CampaignState state)
        {
            FracturedCityController fc = GameRoot.FracturedCity;
            if (state == null || fc == null || !fc.IsActive)
            {
                return ReturnSnapshot.Unavailable;
            }

            ManifestEntry[] roster = MachineRegistry.AllRecords
                .Where(m => m != null && m.RegionId == FracturedCityLayout.RegionId)
                .OrderBy(m => m.DisplayNumber)
                .Select(m => new ManifestEntry(m.LogicId, m.DisplayNumber, m.ChassisId, m.IsAlive, m.Health, m.MaxHealth))
                .ToArray();

            KeyTechEntry[] keyTech = new[] { FracturedCityLayout.MarkerModuleContentId, FracturedCityLayout.ProtocolDataboxContentId }
                .Select(contentId =>
                {
                    // 展示优先级 Recovered（已带回）> Carried（在手，正准备带回）> OnGround（未拾取）>
                    // Lost（本轮丢失，恢复柜会在下次进入时补一份新实例）——不是枚举数值顺序（Lost=3
                    // 数值最大但展示优先级最低），恢复柜可能已经为同一 contentId 生成第二条 OnGround
                    // 记录，此时应展示"还能捡"而不是旧的"已丢失"。
                    RegionQuestItemRecord item = state.RegionQuestItems?
                        .Where(q => q.ContentId == contentId)
                        .OrderBy(q => q.State == RegionQuestItemState.Recovered ? 0
                            : q.State == RegionQuestItemState.Carried ? 1
                            : q.State == RegionQuestItemState.OnGround ? 2
                            : 3)
                        .FirstOrDefault();
                    return new KeyTechEntry(contentId, item?.State ?? RegionQuestItemState.OnGround);
                })
                .ToArray();

            RegionRecord region = FracturedCityRegion.Find(state);
            int groundScrap = state.GroundItems?.Count(g => g.RegionId == FracturedCityLayout.RegionId) ?? 0;

            return new ReturnSnapshot(true, fc.IsWiped, roster, keyTech,
                region != null && region.State == RegionState.Cleared, groundScrap);
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
            FracturedCityController fc = GameRoot.FracturedCity;
            if (fc == null || !fc.IsActive)
            {
                return ReturnResult.Fail("not-active");
            }
            if (fc.IsWiped)
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
                MarkExperienceAndInjuryBeforeExit(state);
                fc.Exit(evacuateSuccess: true);
                GameRoot.ResumeHomeValley();

                SaveResult saveResult = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionResolutionComplete);
                if (!saveResult.Success)
                {
                    Log.Warning($"[ExpeditionReturnService] 远征结算后自动存档未成功（{saveResult.Outcome} " +
                        $"{saveResult.Message}）——切场本身已完成，不回滚，下次自然存档点会补上。");
                }
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
            FracturedCityController fc = GameRoot.FracturedCity;
            if (fc == null || !fc.IsActive)
            {
                return ReturnResult.Fail("not-active");
            }
            if (!fc.IsWiped)
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
                MarkExperienceAndInjuryBeforeExit(state);
                fc.Exit(evacuateSuccess: true); // _wipeResolved 已真，内部跳过重复结算，见类注释。
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

        /// <summary>必须在 <see cref="FracturedCityController.Exit"/> 之前调用——之后存活机器的
        /// <see cref="MachineRecord.RegionId"/> 已经被改写回家园，"当前还在本区域"这个筛选条件会失效。
        /// 全员（含阵亡者）记一次 <see cref="MachineExperienceFlags.Expedition"/>（"首次参与远征"与
        /// 是否生还无关）；只有存活者额外记 <see cref="MachineExperienceFlags.Returned"/>、递增
        /// <see cref="MachineRecord.ExpeditionsCompleted"/>、按当前 HP 比例写伤势（
        /// <see cref="MachineRecord.InjuryFlags"/> 此前是从未被写过的骨架字段，本 Story 首次接入真实
        /// 触发源——ER5-SILENT-01 起机器在区域内才会真的掉血，此前没有数据可写）。</summary>
        private static void MarkExperienceAndInjuryBeforeExit(CampaignState state)
        {
            List<MachineRecord> inRegion = MachineRegistry.AllRecords
                .Where(m => m != null && m.RegionId == FracturedCityLayout.RegionId)
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
