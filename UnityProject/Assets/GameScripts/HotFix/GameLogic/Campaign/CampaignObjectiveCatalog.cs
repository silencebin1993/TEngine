using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Localization;
using GameLogic.Campaign.Regions;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>目标清单里的一项：玩家可见文字 + 是否已达成（结构性读取既有权威字段）+ 这一步在归还谷地
    /// 的哪里做（世界目标标记的落点；null＝不在某个具体位置，例如在蓝图编辑器里保存）+ 未完成时的现状
    /// （UI-10：关键物“在地面/已装车/已带回”，null＝没有可补充的现状）。</summary>
    public sealed class ObjectiveItemDef
    {
        /// <summary>FG1-SIG-05 / FG15-UX-03：文字在读取时才解析（清单在静态初始化时建好，不锁死语言也不依赖表的加载时机）。</summary>
        private readonly Func<string> _label;
        public readonly Func<CampaignState, bool> IsDone;
        public readonly Func<CampaignState, Vector2?> HomeLocation;
        public readonly Func<CampaignState, string> Status;

        public string Label => _label();

        private ObjectiveItemDef(Func<string> label, Func<CampaignState, bool> isDone, Func<CampaignState, Vector2?> homeLocation,
            Func<CampaignState, string> status)
        {
            _label = label;
            IsDone = isDone;
            HomeLocation = homeLocation;
            Status = status;
        }

        /// <summary>文案用文本键（fg.TbLocText）的清单项。</summary>
        public static ObjectiveItemDef Keyed(string labelKey, Func<CampaignState, bool> isDone, Func<CampaignState, Vector2?> homeLocation = null,
            Func<CampaignState, string> status = null) =>
            new ObjectiveItemDef(() => GameText.Get(labelKey), isDone, homeLocation, status);

        /// <summary>带参数的文案（参数里的物品名等同样现取）。</summary>
        public static ObjectiveItemDef Lazy(Func<string> label, Func<CampaignState, bool> isDone, Func<CampaignState, Vector2?> homeLocation = null,
            Func<CampaignState, string> status = null) =>
            new ObjectiveItemDef(label, isDone, homeLocation, status);
    }

    /// <summary>OBJ-01～10 的一行定义（DEMO-CONTENT-LOCK.md §4.4）。<see cref="RegionNote"/>：人在该目标区域时
    /// 目标条额外显示的一行（可选物资进度，例如废料箱），null＝没有。</summary>
    public sealed class ObjectiveDef
    {
        public string Id;
        /// <summary>标题文本键；<see cref="Title"/> 按当前语言现取（FG15-UX-03：游戏中切语言即时生效）。</summary>
        public string TitleKey;
        public string Title => GameText.Get(TitleKey);
        public string RegionId;
        public ObjectiveItemDef[] Items;
        public Func<CampaignState, string> RegionNote;
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

        /// <summary>FG8-QST-01（DEBT-FG0UX01-12 目标类、FGR-QST-003 地图标记）：某个 OBJ 目标在星球上的位置——在家园做的取第一项未完成清单的位置
        /// （这一步不在具体位置上就取下一项有位置的；全部没有 = null），在远征地点做的取该地点在星球上的锚点（FG8-EXP-02 锚点，按种子解析；还没解析时取信号塔）。</summary>
        public static Vector2? LocationOf(CampaignState state, string objectiveId)
        {
            ObjectiveDef def = Get(objectiveId);
            if (def == null || state == null)
            {
                return null;
            }
            if (def.RegionId != HomeValleyLayout.RegionId)
            {
                if (Regions.ExpeditionSiteAnchors.TryGet(state, def.RegionId, out Vector2 anchor))
                {
                    return anchor;
                }
                return BuildingPosition(state, HomeValleyLayout.BuildingTypeSignalTower);
            }
            for (int i = 0; i < def.Items.Length; i++)
            {
                if (!def.Items[i].IsDone(state))
                {
                    Vector2? at = def.Items[i].HomeLocation?.Invoke(state);
                    if (at.HasValue)
                    {
                        return at;
                    }
                }
            }
            return null;
        }

        /// <summary>同一关键物可能有多条记录（丢失后恢复柜补发新实例）：按 已带回 &gt; 已装车 &gt; 在地面 &gt; 已丢失
        /// 取最能代表现状的一条（撤离面板与目标条同一口径）；从未出现过返回 null。</summary>
        public static RegionQuestItemRecord BestQuestItem(CampaignState state, string contentId) =>
            state?.RegionQuestItems?
                .Where(q => q != null && q.ContentId == contentId)
                .OrderBy(q => q.State == RegionQuestItemState.Recovered ? 0
                    : q.State == RegionQuestItemState.Carried ? 1
                    : q.State == RegionQuestItemState.OnGround ? 2
                    : 3)
                .FirstOrDefault();

        /// <summary>UI-10：关键物现状文字。“装车”不等于“带回”——只有撤离结算后才算带回。</summary>
        public static string QuestStatusText(CampaignState state, string contentId)
        {
            RegionQuestItemRecord item = BestQuestItem(state, contentId);
            if (item == null)
            {
                // 还没出现过：写明怎么获得（此前标记器/数据盒只写“尚未发现”，玩家不知道去哪找）。
                return contentId == FracturedCityLayout.MarkerModuleContentId ? GameText.Get("objective.drops_when_a_listening")
                    : contentId == FracturedCityLayout.ProtocolDataboxContentId ? GameText.Get("objective.obtained_by_reading_the")
                    : contentId == FoundryOutpostLayout.CannonModuleContentId ? GameText.Get("objective.drops_when_a_strider")
                    : contentId == FoundryOutpostLayout.CoreDataContentId ? GameText.Get("objective.drops_when_the_main")
                    : GameText.Get("objective.not_found_yet");
            }
            switch (item.State)
            {
                case RegionQuestItemState.Recovered:
                    return GameText.Get("objective.brought_back");
                case RegionQuestItemState.Carried:
                    string carrier = Feedback.FeedbackCues.MachineLabel(item.CarrierLogicId);
                    return string.IsNullOrEmpty(carrier) ? GameText.Get("objective.loaded_counts_as_brought") : GameText.Format("objective.loaded_on_0_counts", carrier);
                case RegionQuestItemState.Lost:
                    return GameText.Get("objective.lost_the_recovery_locker");
                default:
                    return GameText.Get("objective.on_the_ground_not");
            }
        }

        /// <summary>可选物资：废料箱开了几个、地面还剩几堆没装车（废料不影响目标完成，只影响回家后的资源）。</summary>
        public static string CrateNote(CampaignState state, string regionId, string[] crateIds)
        {
            RegionRecord region = state?.RegionRecords?.FirstOrDefault(r => r != null && r.RegionId == regionId);
            int opened = crateIds.Count(id => region?.LootedContainerIds != null && region.LootedContainerIds.Contains(id));
            int piles = state?.GroundItems?.Count(g => g != null && g.RegionId == regionId) ?? 0;
            return piles > 0
                ? GameText.Format("objective.optional_scrap_crates_opened", opened, crateIds.Length, piles)
                : GameText.Format("objective.optional_scrap_crates_opened_2", opened, crateIds.Length);
        }

        private static readonly string[] RuinsCrates = { FracturedCityLayout.Crate1Id, FracturedCityLayout.Crate2Id, FracturedCityLayout.Crate3Id };
        private static readonly string[] FoundryCrates = { FoundryOutpostLayout.Crate1Id, FoundryOutpostLayout.Crate2Id, FoundryOutpostLayout.Crate3Id };

        private static string NodeStatus(CampaignState s)
        {
            RegionRecord foundry = FoundryOutpostRegion.Find(s);
            if (foundry == null || !FoundryOutpostCoreBoss.IsInitialized(foundry))
            {
                return GameText.Get("objective.appears_after_entering_the");
            }
            int destroyed = new[] { FoundryOutpostLayout.CoreNode1Id, FoundryOutpostLayout.CoreNode2Id }
                .Count(id => FoundryOutpostRegion.FindEnemy(s, id) is RegionEnemyRecord node && !node.IsAlive);
            return GameText.Format("objective.destroyed_0_2", destroyed);
        }

        /// <summary>玩家可见的区域名。</summary>
        public static string RegionDisplayName(string regionId)
        {
            if (regionId == HomeValleyLayout.RegionId)
            {
                return GameText.Get("world.site.home_valley.name");
            }
            if (regionId == FracturedCityLayout.RegionId)
            {
                return GameText.Get("world.site.silent_ruins.name");
            }
            if (regionId == FoundryOutpostLayout.RegionId)
            {
                return GameText.Get("combat.site.foundry_outpost");
            }
            return GameText.Get("objective.unknown_area");
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

        private static Func<CampaignState, string> QuestStatus(string contentId) => s => QuestStatusText(s, contentId);

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
            return string.IsNullOrEmpty(name) || name == GameText.Get("feedback.cues.key_item") ? fallback : name;
        }

        private static ObjectiveDef[] Build()
        {
            string home = HomeValleyLayout.RegionId;
            string ruins = FracturedCityLayout.RegionId;
            string foundry = FoundryOutpostLayout.RegionId;
            // 物品名也现取：读取时按当前语言解析。
            Func<string> marker = () => Quest(FracturedCityLayout.MarkerModuleContentId, GameText.Get("objective.silent_marker_module"));
            Func<string> databox = () => Quest(FracturedCityLayout.ProtocolDataboxContentId, GameText.Get("objective.protocol_data_box"));
            Func<string> cannon = () => Quest(FoundryOutpostLayout.CannonModuleContentId, GameText.Get("analysis.quest_name.cannon_module"));

            return new[]
            {
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj01, TitleKey = "objective.restore_power_in_reclamation", RegionId = home,
                    Items = new[] { ObjectiveItemDef.Keyed("objective.repair_the_generator", s => Operational(s, HomeValleyLayout.BuildingTypeGenerator),
                        At(HomeValleyLayout.BuildingTypeGenerator)) },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj02, TitleKey = "objective.restore_the_warehouse_and", RegionId = home,
                    Items = new[]
                    {
                        ObjectiveItemDef.Keyed("objective.repair_the_warehouse", s => Operational(s, HomeValleyLayout.BuildingTypeWarehouse),
                            At(HomeValleyLayout.BuildingTypeWarehouse)),
                        ObjectiveItemDef.Keyed("objective.power_up_the_assembly", s => OperationalAndPowered(s, HomeValleyLayout.BuildingTypeAssemblyStation),
                            At(HomeValleyLayout.BuildingTypeAssemblyStation)),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj03, TitleKey = "objective.build_your_first_combat", RegionId = home,
                    Items = new[]
                    {
                        ObjectiveItemDef.Keyed("objective.produce_a_combat_tracks", HasErc003, At(HomeValleyLayout.BuildingTypeAssemblyStation)),
                        ObjectiveItemDef.Keyed("objective.control_a_machine_directly", s => EventGranted(s, DirectHitEventId), CombatTargetPosition),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj04, TitleKey = "objective.repair_the_signal_tower", RegionId = home,
                    Items = new[]
                    {
                        ObjectiveItemDef.Keyed("objective.repair_the_signal_tower_2", s => Operational(s, HomeValleyLayout.BuildingTypeSignalTower),
                            At(HomeValleyLayout.BuildingTypeSignalTower)),
                        ObjectiveItemDef.Lazy(() => GameText.Format("objective.at_least_0_machines", ExpeditionDepartureService.MinRosterSize),
                            s => ReadyForExpedition(s) >= ExpeditionDepartureService.MinRosterSize,
                            At(HomeValleyLayout.BuildingTypeAssemblyStation)),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj05, TitleKey = "objective.fractured_city_bring_back", RegionId = ruins,
                    Items = new[]
                    {
                        ObjectiveItemDef.Keyed("objective.depart_from_the_signal", s => Departures(s, ruins) > 0),
                        ObjectiveItemDef.Lazy(() => GameText.Format("objective.evacuate_home_with_0", marker()), s => QuestRecovered(s, FracturedCityLayout.MarkerModuleContentId),
                            status: QuestStatus(FracturedCityLayout.MarkerModuleContentId)),
                        ObjectiveItemDef.Lazy(() => GameText.Format("objective.evacuate_home_with_0", databox()), s => QuestRecovered(s, FracturedCityLayout.ProtocolDataboxContentId),
                            status: QuestStatus(FracturedCityLayout.ProtocolDataboxContentId)),
                    },
                    RegionNote = s => CrateNote(s, ruins, RuinsCrates),
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj06, TitleKey = "objective.analyze_and_install_mark", RegionId = home,
                    Items = new[]
                    {
                        ObjectiveItemDef.Lazy(() => GameText.Format("objective.analyze_0_at_the", marker()), s => Unlocked(s, ComponentCatalog.FuncMarkerId),
                            At(HomeValleyLayout.BuildingTypeAnalysisBench)),
                        ObjectiveItemDef.Lazy(() => GameText.Format("objective.analyze_0_at_the", databox()), s => Unlocked(s, FirmwareCatalog.FwMarkTagId),
                            At(HomeValleyLayout.BuildingTypeAnalysisBench)),
                        // FG1-SIG-05：标记跳转是核心固件，只在接入时插进接入口——“标记跳转蓝图” = 接入时能打出标记跳转的蓝图。
                        ObjectiveItemDef.Keyed("objective.obj06.save_markjump",
                            s => BlueprintEditorService.IsReactionCharged(s, MechanicalReactionCatalog.ReactionMarkJumpId)),
                        ObjectiveItemDef.Keyed("objective.refit_the_combat_tracks", Erc003Retrofitted, At(HomeValleyLayout.BuildingTypeAssemblyStation)),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj07, TitleKey = "objective.foundry_outskirts_bring_back", RegionId = foundry,
                    Items = new[]
                    {
                        ObjectiveItemDef.Keyed("objective.depart_for_the_foundry", s => Departures(s, foundry) > 0),
                        ObjectiveItemDef.Lazy(() => GameText.Format("objective.evacuate_home_with_0", cannon()),
                            s => FoundryOutpostRegion.Find(s)?.State == RegionState.Cleared,
                            status: QuestStatus(FoundryOutpostLayout.CannonModuleContentId)),
                    },
                    RegionNote = s => CrateNote(s, foundry, FoundryCrates),
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj08, TitleKey = "objective.analyze_and_install_melt", RegionId = home,
                    Items = new[]
                    {
                        ObjectiveItemDef.Lazy(() => GameText.Format("objective.analyze_0_at_the", cannon()), s => FoundryOutpostRegion.ComputeCoreGateLights(s).CannonAnalyzed,
                            At(HomeValleyLayout.BuildingTypeAnalysisBench)),
                        // FG1-SIG-05：过载是核心固件——保存 / 实装按“接入时能打出熔穿过载”判定（与核心门三灯同一判定）。
                        ObjectiveItemDef.Keyed("objective.obj08.save_overload", s => FoundryOutpostRegion.ComputeCoreGateLights(s).OverloadBlueprintSaved),
                        ObjectiveItemDef.Keyed("objective.obj08.machine_ready", s => FoundryOutpostRegion.ComputeCoreGateLights(s).MachineEquipped,
                            At(HomeValleyLayout.BuildingTypeAssemblyStation)),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj09, TitleKey = "objective.destroy_the_main_core", RegionId = foundry,
                    Items = new[]
                    {
                        ObjectiveItemDef.Keyed("objective.destroy_both_power_nodes", s => BossState(s) >= CoreBossState.Phase1, status: NodeStatus),
                        ObjectiveItemDef.Keyed("objective.destroy_the_foundry_outpost", s => BossState(s) == CoreBossState.Destroyed),
                        ObjectiveItemDef.Keyed("objective.evacuate_home_with_the", s => QuestRecovered(s, FoundryOutpostLayout.CoreDataContentId),
                            status: QuestStatus(FoundryOutpostLayout.CoreDataContentId)),
                    },
                },
                new ObjectiveDef
                {
                    Id = CampaignObjectiveTracker.Obj10, TitleKey = "objective.build_and_launch_the", RegionId = home,
                    Items = new[]
                    {
                        ObjectiveItemDef.Keyed("objective.build_the_navigation_beacon", s => Operational(s, HomeValleyLayout.BuildingTypeBeacon), BeaconPosition),
                        ObjectiveItemDef.Keyed("objective.power_the_navigation_beacon", s => OperationalAndPowered(s, HomeValleyLayout.BuildingTypeBeacon), BeaconPosition),
                        ObjectiveItemDef.Keyed("objective.press_interact_at_the", s => EventGranted(s, CampaignObjectiveTracker.BeaconLaunchEventId), BeaconPosition),
                    },
                },
            };
        }
    }
}
