using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>目标清单里的一项：玩家可见文字 + 是否已达成（结构性读取既有权威字段）+ 这一步在归还谷地
    /// 的哪里做（世界目标标记的落点；null＝不在某个具体位置，例如在蓝图编辑器里保存）。</summary>
    public sealed class ObjectiveItemDef
    {
        public readonly string Label;
        public readonly Func<CampaignState, bool> IsDone;
        public readonly Func<CampaignState, Vector2?> HomeLocation;

        public ObjectiveItemDef(string label, Func<CampaignState, bool> isDone, Func<CampaignState, Vector2?> homeLocation = null)
        {
            Label = label;
            IsDone = isDone;
            HomeLocation = homeLocation;
        }
    }

    /// <summary>OBJ-01～10 的一行定义（DEMO-CONTENT-LOCK.md §4.4）。</summary>
    public sealed class ObjectiveDef
    {
        public string Id;
        public string Title;
        public string RegionId;
        public ObjectiveItemDef[] Items;
    }

    /// <summary>DEBT-ER6LOOP01-01 / AC-CAM-001：OBJ-01～10 的玩家可见定义——标题、所在区域、进度清单。
    /// 此前全仓库没有任何目标标题的运行时字符串，玩家只能在远征准备面板看到一行原始 OBJ 编号。
    ///
    /// 清单每一项都结构性读取既有权威字段（建筑状态、解锁列表、关键物状态、Boss 状态机、事件账本……），
    /// 与 <see cref="CampaignObjectiveTracker"/> 的完成判定同源；OBJ-01～04 的完成判定直接用这里的清单
    /// （全部完成＝目标完成），OBJ-05～10 的判定仍在追踪器里（已验证的既有实现），这里的清单逐项对应，
    /// 自检核对“清单全部完成 ⇔ 追踪器判定完成”。</summary>
    public static class CampaignObjectiveCatalog
    {
        /// <summary>OBJ-03“直控命中低威胁靶”的一次性事件（<see cref="HomeValleyCombatTargets.TryAttack"/> 直控命中时授予）。</summary>
        public const string DirectHitEventId = "objective_direct_hit_low_threat";

        public static readonly ObjectiveDef[] All = Build();

        public static ObjectiveDef Get(string objectiveId)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Id == objectiveId)
                {
                    return All[i];
                }
            }
            return null;
        }

        public static string TitleOf(string objectiveId) => Get(objectiveId)?.Title ?? string.Empty;

        /// <summary>清单是否全部达成。</summary>
        public static bool AllItemsDone(CampaignState state, string objectiveId)
        {
            ObjectiveDef def = Get(objectiveId);
            if (def == null || state == null)
            {
                return false;
            }
            for (int i = 0; i < def.Items.Length; i++)
            {
                if (!def.Items[i].IsDone(state))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>UI-14“区域地图和世界标记使用同一目标状态”：归还谷地里的世界目标标记该落在哪。
        /// 当前目标在家园时，取第一项未完成清单的位置（这一步不在具体位置上就不标，不指错地方）；
        /// 当前目标在远征区域时，家园里要做的只剩“从信号塔出发”，标信号塔。</summary>
        public static bool TryGetHomeMarker(CampaignState state, out Vector2 position)
        {
            position = default;
            ObjectiveDef def = Get(CampaignObjectiveTracker.CurrentObjectiveId(state));
            if (def == null)
            {
                return false;
            }
            Vector2? where = null;
            if (def.RegionId != HomeValleyLayout.RegionId)
            {
                where = BuildingPosition(state, HomeValleyLayout.BuildingTypeSignalTower);
            }
            else
            {
                for (int i = 0; i < def.Items.Length; i++)
                {
                    if (!def.Items[i].IsDone(state))
                    {
                        where = def.Items[i].HomeLocation?.Invoke(state);
                        break;
                    }
                }
            }
            if (!where.HasValue)
            {
                return false;
            }
            position = where.Value;
            return true;
        }

        /// <summary>玩家可见的区域名。</summary>
        public static string RegionDisplayName(string regionId)
        {
            if (regionId == HomeValleyLayout.RegionId)
            {
                return "归还谷地";
            }
            if (regionId == FracturedCityLayout.RegionId)
            {
                return "破碎都市";
            }
            if (regionId == FoundryOutpostLayout.RegionId)
            {
                return "铸造前哨外围";
            }
            return "未知区域";
        }

        // ── 结构性读取的小工具 ────────────────────────────────────────

        private static BuildingRecord Building(CampaignState s, string typeId) =>
            s.BuildingRecords?.FirstOrDefault(b => b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == typeId);

        private static bool Operational(CampaignState s, string typeId)
        {
            BuildingRecord b = Building(s, typeId);
            return b != null && b.ConstructionState == BuildingConstructionState.Operational;
        }

        private static bool OperationalAndPowered(CampaignState s, string typeId)
        {
            BuildingRecord b = Building(s, typeId);
            return b != null && b.ConstructionState == BuildingConstructionState.Operational && b.PowerState == BuildingPowerState.Powered;
        }

        private static bool Unlocked(CampaignState s, string contentId) =>
            s.UnlockedContentIds != null && s.UnlockedContentIds.Contains(contentId);

        private static bool QuestRecovered(CampaignState s, string contentId) =>
            s.RegionQuestItems != null && s.RegionQuestItems.Any(q => q.ContentId == contentId && q.State == RegionQuestItemState.Recovered);

        private static bool EventGranted(CampaignState s, string eventId) =>
            s.EventLedger != null && s.EventLedger.Any(e => e.EventId == eventId);

        private static int Departures(CampaignState s, string regionId) =>
            s.RegionRecords?.FirstOrDefault(r => r != null && r.RegionId == regionId)?.ExpeditionCount ?? 0;

        private static bool HasErc003(CampaignState s) =>
            MachineRegistry.AllRecords.Any(m => m != null && m.ChassisId == HomeValleyLayout.Erc003ChassisId)
            || (s.MachineRecords != null && s.MachineRecords.Any(m => m != null && m.ChassisId == HomeValleyLayout.Erc003ChassisId));

        private static bool Erc003Retrofitted(CampaignState s) =>
            MachineRegistry.AllRecords.Any(m =>
                m.IsAlive && m.ChassisId == HomeValleyLayout.Erc003ChassisId && m.BlueprintId != HomeValleyLayout.BlueprintErc003Id);

        private static int ReadyForExpedition(CampaignState s) =>
            MachineRegistry.AllRecords.Count(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && !m.IsInFactory);

        private static CoreBossState BossState(CampaignState s)
        {
            RegionRecord foundry = FoundryOutpostRegion.Find(s);
            return foundry == null ? CoreBossState.Locked : FoundryOutpostCoreBoss.GetState(foundry);
        }

        private static Vector2? BuildingPosition(CampaignState s, string typeId) => Building(s, typeId)?.Position;

        /// <summary>信标建成前标在预留建造位上。</summary>
        private static Vector2? BeaconPosition(CampaignState s) =>
            BuildingPosition(s, HomeValleyLayout.BuildingTypeBeacon) ?? HomeValleyLayout.BeaconSlot.Position;

        /// <summary>第一个未在再生冷却中的归还谷地低威胁靶。</summary>
        private static Vector2? CombatTargetPosition(CampaignState s) =>
            s.CombatTargets?.FirstOrDefault(t => t != null && t.RegionId == HomeValleyLayout.RegionId && t.Health > 0f)?.Position;

        private static Func<CampaignState, Vector2?> At(string buildingTypeId) => s => BuildingPosition(s, buildingTypeId);

        private static string Quest(string contentId, string fallback)
        {
            string name = Feedback.FeedbackCues.QuestItemName(contentId);
            return string.IsNullOrEmpty(name) || name == "关键物" ? fallback : name;
        }

        private static ObjectiveDef[] Build()
        {
            string home = HomeValleyLayout.RegionId;
            string ruins = FracturedCityLayout.RegionId;
            string foundry = FoundryOutpostLayout.RegionId;
            string marker = Quest(FracturedCityLayout.MarkerModuleContentId, "静默标记模块");
            string databox = Quest(FracturedCityLayout.ProtocolDataboxContentId, "协议数据盒");
            string cannon = Quest(FoundryOutpostLayout.CannonModuleContentId, "铸造重炮模块");

            return new[]
            {
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj01, Title = "修复归还谷地供电", RegionId = home,
                    Items = new[] { new ObjectiveItemDef("修复发电机", s => Operational(s, HomeValleyLayout.BuildingTypeGenerator),
                        At(HomeValleyLayout.BuildingTypeGenerator)) },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj02, Title = "恢复仓库与装配站", RegionId = home,
                    Items = new[]
                    {
                        new ObjectiveItemDef("修复仓库", s => Operational(s, HomeValleyLayout.BuildingTypeWarehouse),
                            At(HomeValleyLayout.BuildingTypeWarehouse)),
                        new ObjectiveItemDef("装配站通电运转", s => OperationalAndPowered(s, HomeValleyLayout.BuildingTypeAssemblyStation),
                            At(HomeValleyLayout.BuildingTypeAssemblyStation)),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj03, Title = "建造第一台战斗机并接管", RegionId = home,
                    Items = new[]
                    {
                        new ObjectiveItemDef("在装配站生产战斗履带机", HasErc003, At(HomeValleyLayout.BuildingTypeAssemblyStation)),
                        new ObjectiveItemDef("直接操控机器命中低威胁残骸靶", s => EventGranted(s, DirectHitEventId), CombatTargetPosition),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj04, Title = "修复信号塔，准备远征", RegionId = home,
                    Items = new[]
                    {
                        new ObjectiveItemDef("修复信号塔", s => Operational(s, HomeValleyLayout.BuildingTypeSignalTower),
                            At(HomeValleyLayout.BuildingTypeSignalTower)),
                        new ObjectiveItemDef($"至少 {ExpeditionDepartureService.MinRosterSize} 台机器可以出征",
                            s => ReadyForExpedition(s) >= ExpeditionDepartureService.MinRosterSize,
                            At(HomeValleyLayout.BuildingTypeAssemblyStation)),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj05, Title = "破碎都市：带回静默技术", RegionId = ruins,
                    Items = new[]
                    {
                        new ObjectiveItemDef("从信号塔出发前往破碎都市", s => Departures(s, ruins) > 0),
                        new ObjectiveItemDef($"带着{marker}撤离回家", s => QuestRecovered(s, FracturedCityLayout.MarkerModuleContentId)),
                        new ObjectiveItemDef($"带着{databox}撤离回家", s => QuestRecovered(s, FracturedCityLayout.ProtocolDataboxContentId)),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj06, Title = "解析并实装标记跳转", RegionId = home,
                    Items = new[]
                    {
                        new ObjectiveItemDef($"在解析台解析{marker}", s => Unlocked(s, ComponentCatalog.FuncMarkerId),
                            At(HomeValleyLayout.BuildingTypeAnalysisBench)),
                        new ObjectiveItemDef($"在解析台解析{databox}", s => Unlocked(s, FirmwareCatalog.FwMarkTagId),
                            At(HomeValleyLayout.BuildingTypeAnalysisBench)),
                        new ObjectiveItemDef("在蓝图编辑器保存含“标记跳转”的蓝图",
                            s => BlueprintEditorService.IsReactionCharged(s, MechanicalReactionCatalog.ReactionMarkJumpId)),
                        new ObjectiveItemDef("战斗履带机回装配站改造", Erc003Retrofitted, At(HomeValleyLayout.BuildingTypeAssemblyStation)),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj07, Title = "铸造外围：带回重炮", RegionId = foundry,
                    Items = new[]
                    {
                        new ObjectiveItemDef("出发前往铸造前哨外围", s => Departures(s, foundry) > 0),
                        new ObjectiveItemDef($"带着{cannon}撤离回家",
                            s => FoundryOutpostRegion.Find(s)?.State == RegionState.Cleared),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj08, Title = "解析并实装熔穿过载", RegionId = home,
                    Items = new[]
                    {
                        new ObjectiveItemDef($"在解析台解析{cannon}", s => FoundryOutpostRegion.ComputeCoreGateLights(s).CannonAnalyzed,
                            At(HomeValleyLayout.BuildingTypeAnalysisBench)),
                        new ObjectiveItemDef("在蓝图编辑器保存含“熔穿过载”的蓝图", s => FoundryOutpostRegion.ComputeCoreGateLights(s).OverloadBlueprintSaved),
                        new ObjectiveItemDef("在装配站给一台现役机器实装熔穿过载", s => FoundryOutpostRegion.ComputeCoreGateLights(s).MachineEquipped,
                            At(HomeValleyLayout.BuildingTypeAssemblyStation)),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj09, Title = "摧毁主核心并回收数据", RegionId = foundry,
                    Items = new[]
                    {
                        new ObjectiveItemDef("摧毁两个供能节点", s => BossState(s) >= CoreBossState.Phase1),
                        new ObjectiveItemDef("摧毁铸造前哨主核心", s => BossState(s) == CoreBossState.Destroyed),
                        new ObjectiveItemDef("带着核心数据撤离回家", s => QuestRecovered(s, FoundryOutpostLayout.CoreDataContentId)),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj10, Title = "建造并启动返航信标", RegionId = home,
                    Items = new[]
                    {
                        new ObjectiveItemDef("建成导航信标", s => Operational(s, HomeValleyLayout.BuildingTypeBeacon), BeaconPosition),
                        new ObjectiveItemDef("让导航信标通电", s => OperationalAndPowered(s, HomeValleyLayout.BuildingTypeBeacon), BeaconPosition),
                        new ObjectiveItemDef("对信标按交互键确认启动", s => EventGranted(s, CampaignObjectiveTracker.BeaconLaunchEventId), BeaconPosition),
                    },
                },
            };
        }
    }
}
