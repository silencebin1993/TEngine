using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Core;
using GameLogic.Stage;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER5-EXP-01 STORY-EXECUTION-CARDS.md：远征准备面板的数据源 + 出发确认事务的唯一实现。
    ///
    /// ── 与 ER5-REGION-01 的分工（DIGEST 已登记，非本 Story 自行裁剪）──
    /// <see cref="FracturedCityController.Enter"/> 是"最小可用切场入口"（把已经决定好的 LogicId 列表
    /// 转移过去），本类是它前面缺的那一层：真正的人数/装配/带宽/武器/货位校验、工作机中断确认、
    /// 冻结输入→自动安全存档→出发快照→区域卸载/载入→机器生成/装配登记→控制恢复→解冻的完整编排，
    /// 以及"只有整个切换成功才增加 expeditionCount；中途任何异常回出发前档"的事务保证。
    ///
    /// ── 回滚机制 ──
    /// "出发前档"就是本类在冻结输入后立即触发的 <see cref="CampaignAutoSaveService.SaveAuto"/>
    /// （<see cref="SaveReason.ExpeditionDepartConfirm"/>）——这正是 ERD-SAV-002 六类自动存档点之一，
    /// 不是本类另起的第二套快照机制。切换过程中任何一步失败，直接复用 ER1-SAVE-02 已验证的
    /// <see cref="CampaignRestoreOrchestrator.Restore"/> 从刚写的这份档重新读回内存（同一条"回出发前档"
    /// 路径，不再手写一套并行的深拷贝/回滚逻辑）。
    ///
    /// ── 装配登记的真实必要性（不是仪式性步骤）──
    /// <see cref="HomeValleyController.Exit"/> 会无条件 <see cref="MachineLoadoutRegistry.Clear"/>
    /// （区域卸载与登记表解绑成对，ER4-BLP-02 既定纪律），而 <see cref="FracturedCityController.Enter"/>
    /// 此前不会重新登记——这意味着"区域卸载/载入"这一步如果不显式补登记，出征机器进入破碎都市后
    /// 会立刻查不到自己的装配（<see cref="MachineLoadoutRegistry.Resolve"/> 拒绝生成默认强力替身），
    /// 战斗直接失效。本 Story 在 <see cref="FracturedCityController.Enter"/> 内补上这一步（见该类），
    /// 本类调用它、不重复实现。</summary>
    public static class ExpeditionDepartureService
    {
        public const int MinRosterSize = 3;
        public const int MaxRosterSize = 5;

        /// <summary>ER6-REGION-01：本类此前硬编码目标破碎都市（DIGEST 已登记的已知缺口），现推广为
        /// 支持第二次出征目标铸造前哨外围。None＝两区域都还 Locked（还没修信号塔/生产 ERC-003，或
        /// 还没保存跨派系蓝图/改造 ERC-003）；进攻核心分区不是"出发去一个新地图"，是同一
        /// foundry_outpost 会话内的门禁（见 <see cref="FoundryOutpostRegion.CanEnterCoreZone"/>），
        /// 不在本枚举范围内。</summary>
        public enum ExpeditionTarget
        {
            None = 0,
            SilentRuins = 1,
            FoundryOutpost = 2,
        }

        /// <summary>目标解析唯一入口——铸造前哨外围一旦解锁（DEMO-CONTENT-LOCK.md §4.2 前置："跨派系
        /// 蓝图已保存且 ERC-003 已改造"，<see cref="FoundryOutpostRegion.RecomputeUnlock"/> 真实判定），
        /// 后续出征即以它为目标；玩家不会再被要求回破碎都市（该区域已完成"带回两件技术"的一次性
        /// 目标，DEMO-CONTENT-LOCK.md §4.4 OBJ-05～07 是线性推进，不循环）。</summary>
        public static ExpeditionTarget ResolveTarget(CampaignState state)
        {
            if (state == null)
            {
                return ExpeditionTarget.None;
            }
            RegionRecord foundry = FoundryOutpostRegion.Find(state);
            if (foundry != null && foundry.State != RegionState.Locked)
            {
                return ExpeditionTarget.FoundryOutpost;
            }
            RegionRecord fractured = FracturedCityRegion.Find(state);
            if (fractured != null && fractured.State != RegionState.Locked)
            {
                return ExpeditionTarget.SilentRuins;
            }
            return ExpeditionTarget.None;
        }

        /// <summary>"主作战组件"＝会伤敌的主组件（连射器/切割束/铸造重炮）。维修束是主组件但
        /// "只修友军不能伤敌"（DEMO-CONTENT-LOCK.md §5），不算战斗力，出征队伍全员维修束应当仍被
        /// "无武器"拦截。</summary>
        private static readonly HashSet<string> WeaponPrimaryIds = new HashSet<string>
        {
            ComponentCatalog.CompGunId, ComponentCatalog.CompBeamId, ComponentCatalog.CompCannonId,
        };

        /// <summary>"不可中断任务"的真实判定：<see cref="HomeValleyWorkOrders.TryCreateEmergencyRepair"/>
        /// 产出的紧急免费修复——唯一识别特征是 <see cref="WorkOrderRecord.ResourceTransactionId"/> 为空
        /// （常规 Repair 订单必然有一笔 <see cref="CampaignEconomyLedger"/> 事务 id，只有紧急救援机的
        /// 免费修复没有，见该方法类注释）。这台机器（<see cref="HomeValleyLayout.ErcRescueChassisId"/>）
        /// 由 <see cref="HomeValleySoftlockGuard"/> 在经济卡死时自动生成，专门去修当前最急的 Damaged
        /// 建筑；此刻把它拉去出征等于放弃唯一的破局手段，这是系统里真正"打断即不可逆"的场景——不是
        /// 核心维修（Core 从未出现在 <see cref="HomeValleyLayout.RepairProfile"/> 里，实测确认核心在
        /// 当前内容锁定表下不会进入可修复的 Damaged 态，"打断核心抢修"这个设想在现有产品范围内根本
        /// 不会发生，用它当判定条件是空判据）。</summary>
        private static bool IsUninterruptibleOrder(WorkOrderRecord order) =>
            order != null && order.Kind == WorkOrderKind.Repair && string.IsNullOrEmpty(order.ResourceTransactionId);

        // ── 单机情报（面板展示 + 校验共用同一份解析结果，不允许面板和校验各算一套数字）───────

        public readonly struct MachineIntel
        {
            public readonly int LogicId;
            public readonly int DisplayNumber;
            public readonly string ChassisId;
            public readonly string BlueprintId;
            public readonly int BlueprintVersion;
            public readonly float Health;
            public readonly float MaxHealth;
            public readonly int CargoSlots;
            public readonly float BandwidthCost;
            public readonly bool HasWeapon;
            public readonly bool IsAlive;
            public readonly bool IsInFactory;
            public readonly bool InHomeRegion;
            public readonly string CurrentWorkOrderId;
            public readonly WorkOrderKind? BusyKind;
            public readonly bool BusyUninterruptible;
            public readonly bool Eligible;
            public readonly string IneligibleReason;

            public MachineIntel(int logicId, int displayNumber, string chassisId, string blueprintId,
                int blueprintVersion, float health, float maxHealth, int cargoSlots, float bandwidthCost,
                bool hasWeapon, bool isAlive, bool isInFactory, bool inHomeRegion, string currentWorkOrderId,
                WorkOrderKind? busyKind, bool busyUninterruptible, bool eligible, string ineligibleReason)
            {
                LogicId = logicId;
                DisplayNumber = displayNumber;
                ChassisId = chassisId;
                BlueprintId = blueprintId;
                BlueprintVersion = blueprintVersion;
                Health = health;
                MaxHealth = maxHealth;
                CargoSlots = cargoSlots;
                BandwidthCost = bandwidthCost;
                HasWeapon = hasWeapon;
                IsAlive = isAlive;
                IsInFactory = isInFactory;
                InHomeRegion = inHomeRegion;
                CurrentWorkOrderId = currentWorkOrderId;
                BusyKind = busyKind;
                BusyUninterruptible = busyUninterruptible;
                Eligible = eligible;
                IneligibleReason = ineligibleReason;
            }
        }

        /// <summary>单台机器的静态情报解析——面板列表与 <see cref="ValidateRoster"/> 唯一共用来源。
        /// 不查 <see cref="MachineLoadoutRegistry"/>（那张表只在机器已经登记过时才有数据，归还谷地
        /// 机器随时可能因为刚从工厂驶出而尚未走过 <c>HomeValleyController.Enter</c> 的整批登记），
        /// 直接读 <see cref="MachineRecord.BlueprintId"/>/<see cref="MachineRecord.BlueprintVersion"/>
        /// 现场解析 <see cref="BlueprintVersionRecord"/>——与 <see cref="MachineLoadoutRegistry.Resolve"/>
        /// 读的是同一份底层数据，只是不经过那张登记表的中间状态。</summary>
        public static MachineIntel BuildIntel(CampaignState state, MachineRecord record)
        {
            HomeValleyLayout.MachineCargoSlots.TryGetValue(record.ChassisId, out int cargoSlots);
            HomeValleyLayout.MachineBandwidthCost.TryGetValue(record.ChassisId, out float baseBandwidth);

            // 总带宽＝机型基础占用（HomeValleyLayout.MachineBandwidthCost）+ 装配额外加成
            // （BlueprintVersionRecord.BandwidthCost，目前唯一来源是信号中继结构）——两者独立叠加，
            // 见 HomeValleyLayout.MachineBandwidthCost 类注释，不能只读其中一个。
            float bandwidthCost = baseBandwidth;
            bool hasWeapon = false;
            if (!string.IsNullOrEmpty(record.BlueprintId))
            {
                BlueprintRecord bp = BlueprintEditorService.Find(state, record.BlueprintId);
                BlueprintVersionRecord version = bp?.Versions?.FirstOrDefault(v => v.Version == record.BlueprintVersion);
                if (version != null)
                {
                    bandwidthCost += version.BandwidthCost;
                    hasWeapon = !string.IsNullOrEmpty(version.PrimaryId) && WeaponPrimaryIds.Contains(version.PrimaryId);
                }
            }

            WorkOrderRecord activeOrder = HomeValleyWorkOrders.FindActiveOrderForMachine(state, record.LogicId);
            bool uninterruptible = IsUninterruptibleOrder(activeOrder);

            bool inHomeRegion = record.RegionId == HomeValleyLayout.RegionId;
            bool eligible = record.IsAlive && inHomeRegion && !record.IsInFactory && !uninterruptible;
            string ineligibleReason = eligible ? null
                : !record.IsAlive ? "dead"
                : !inHomeRegion ? "already-deployed:" + record.RegionId
                : record.IsInFactory ? "in-factory"
                : "busy-uninterruptible:" + activeOrder.WorkOrderId;

            return new MachineIntel(record.LogicId, record.DisplayNumber, record.ChassisId, record.BlueprintId,
                record.BlueprintVersion, record.Health, record.MaxHealth, cargoSlots, bandwidthCost, hasWeapon,
                record.IsAlive, record.IsInFactory, inHomeRegion, activeOrder?.WorkOrderId,
                activeOrder?.Kind, uninterruptible, eligible, ineligibleReason);
        }

        // ── 面板快照 ─────────────────────────────────────────────────────────────

        public readonly struct PrepSnapshot
        {
            public readonly bool RegionReachable;
            public readonly string BlockedReason;
            public readonly MachineIntel[] Machines;
            public readonly float BandwidthCapacity;
            public readonly int MinRecommendedCargoSlots;
            public readonly string EnemyIntelText;
            public readonly float EnemyAlertLevel;
            public readonly int ExpeditionCount;
            /// <summary>ER6-REGION-01：本次出征目标区域，供 UI 显示正确的目标名/敌情，不再硬编码
            /// "破碎都市"。</summary>
            public readonly ExpeditionTarget Target;
            /// <summary>DEMO-CONTENT-LOCK.md 行203"第二次准备：'外围侦察，重炮回收后撤离；主核心区
            /// 暂不可进入'"——目标明确文案，Target 为 FoundryOutpost 时给出。</summary>
            public readonly string ObjectivePreviewText;

            /// <summary>ER6-ADAPT-01：<see cref="EnemyAdaptationService.ComputeAdaptation"/> 的实时预览
            /// （未锁定，出发确认时才真正写入 <see cref="RegionRecord.AdaptationId"/>）。DEMO-CONTENT-LOCK.md
            /// 行205"第三次准备：敌方反制名、来源和具体应对建议；未见情报时显示None"——恒为
            /// <see cref="AdaptationCatalog.None"/> 时四个文案字段自然落到"无反制"那一条，不需要调用方
            /// 额外判空。</summary>
            public readonly string AdaptationId;
            public readonly string AdaptationDisplayName;
            public readonly string AdaptationSourceText;
            public readonly string AdaptationHazardText;
            public readonly string AdaptationCounterHintText;
            /// <summary>ER6-ADAPT-01 STORY-EXECUTION-CARDS.md 第3条"90暴露额外入口护甲机单独预告，
            /// 不把它伪装成adaptation"——独立字段，不并入上面四个 Adaptation* 字段。</summary>
            public readonly bool CoreReinforcementForecast;
            public readonly string CoreReinforcementForecastText;

            public PrepSnapshot(bool regionReachable, string blockedReason, MachineIntel[] machines,
                float bandwidthCapacity, int minRecommendedCargoSlots, string enemyIntelText,
                float enemyAlertLevel, int expeditionCount, ExpeditionTarget target, string objectivePreviewText,
                string adaptationId, string adaptationDisplayName, string adaptationSourceText,
                string adaptationHazardText, string adaptationCounterHintText,
                bool coreReinforcementForecast, string coreReinforcementForecastText)
            {
                RegionReachable = regionReachable;
                BlockedReason = blockedReason;
                Machines = machines;
                BandwidthCapacity = bandwidthCapacity;
                MinRecommendedCargoSlots = minRecommendedCargoSlots;
                EnemyIntelText = enemyIntelText;
                EnemyAlertLevel = enemyAlertLevel;
                ExpeditionCount = expeditionCount;
                Target = target;
                ObjectivePreviewText = objectivePreviewText;
                AdaptationId = adaptationId;
                AdaptationDisplayName = adaptationDisplayName;
                AdaptationSourceText = adaptationSourceText;
                AdaptationHazardText = adaptationHazardText;
                AdaptationCounterHintText = adaptationCounterHintText;
                CoreReinforcementForecast = coreReinforcementForecast;
                CoreReinforcementForecastText = coreReinforcementForecastText;
            }
        }

        /// <summary>ER6-ADAPT-01：三份面板快照共用同一份反制情报计算——"面板显示与出发时锁定必须是
        /// 同一套算法"，不允许面板自己再算一遍。<paramref name="state"/> 为 null 时直接给 None 默认值，
        /// 不调用 <see cref="EnemyAdaptationService.ComputeAdaptation"/>（该方法本身也对 null 安全，这里
        /// 提前短路只是避免无意义的一次函数调用）。</summary>
        private static (string id, string name, string source, string hazard, string counter,
            bool reinforcement, string reinforcementText) BuildAdaptationDisplay(CampaignState state)
        {
            if (state == null)
            {
                AdaptationCatalog.AdaptationInfo noneInfo = AdaptationCatalog.Describe(AdaptationCatalog.None);
                return (AdaptationCatalog.None, noneInfo.DisplayName, noneInfo.SourceText, noneInfo.HazardText,
                    noneInfo.CounterHintText, false, null);
            }
            string adaptationId = EnemyAdaptationService.ComputeAdaptation(state);
            AdaptationCatalog.AdaptationInfo info = AdaptationCatalog.Describe(adaptationId);
            bool reinforcement = CampaignExposureLedger.HasReachedCoreReinforcement(state);
            string reinforcementText = reinforcement
                ? "信号暴露已突破90：下一次进攻核心分区时，入口会额外增援一台铸造护甲机布防" +
                  "（出发前预告，不是本次适应，届时在核心战入口实装）。"
                : null;
            return (adaptationId, info.DisplayName, info.SourceText, info.HazardText, info.CounterHintText,
                reinforcement, reinforcementText);
        }

        /// <summary>面板展示用的整份快照。<paramref name="state"/> 为空或区域尚未 Available 时
        /// <see cref="PrepSnapshot.RegionReachable"/> 为假并给出 <see cref="PrepSnapshot.BlockedReason"/>，
        /// 面板据此只显示"未解锁"而不是空列表。</summary>
        public static PrepSnapshot BuildPrepSnapshot(CampaignState state)
        {
            if (state == null)
            {
                return new PrepSnapshot(false, "no-active-campaign", Array.Empty<MachineIntel>(), 0f, 0, null, 0f, 0,
                    ExpeditionTarget.None, null, AdaptationCatalog.None, null, null, null, null, false, null);
            }

            ExpeditionTarget target = ResolveTarget(state);
            if (target == ExpeditionTarget.None)
            {
                RegionRecord fractured = FracturedCityRegion.Find(state);
                return new PrepSnapshot(false, "region-locked", Array.Empty<MachineIntel>(),
                    HomeValleySignal.BandwidthCapacity(state), 0, null, fractured?.EnemyAlertLevel ?? 0f,
                    fractured?.ExpeditionCount ?? 0, ExpeditionTarget.None, null,
                    AdaptationCatalog.None, null, null, null, null, false, null);
            }

            MachineIntel[] machines = MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId)
                .OrderBy(m => m.DisplayNumber)
                .Select(m => BuildIntel(state, m))
                .ToArray();

            if (target == ExpeditionTarget.FoundryOutpost)
            {
                RegionRecord foundry = FoundryOutpostRegion.Find(state);
                bool enemiesSeeded = state.RegionEnemies != null &&
                    state.RegionEnemies.Any(e => e.RegionId == FoundryOutpostLayout.RegionId);
                int aliveArmorBots = enemiesSeeded
                    ? state.RegionEnemies.Count(e => e.RegionId == FoundryOutpostLayout.RegionId && e.EnemyTypeId == EnemyCatalog.ArmorBotId && e.IsAlive)
                    : 2;
                int aliveStriders = enemiesSeeded
                    ? state.RegionEnemies.Count(e => e.RegionId == FoundryOutpostLayout.RegionId && e.EnemyTypeId == EnemyCatalog.StriderId && e.IsAlive)
                    : 1;
                int aliveRepairBots = enemiesSeeded
                    ? state.RegionEnemies.Count(e => e.RegionId == FoundryOutpostLayout.RegionId && e.EnemyTypeId == EnemyCatalog.RepairBotId && e.IsAlive)
                    : 1;
                string foundryIntel = $"铸造护甲机 x{aliveArmorBots}（HP{FoundryOutpostLayout.ArmorBotMaxHealth:F0}，正面减伤40%）｜" +
                    $"铸造步进炮 x{aliveStriders}（HP{FoundryOutpostLayout.StriderMaxHealth:F0}，1秒瞄准线）｜" +
                    $"铸造维修机 x{aliveRepairBots}（HP{FoundryOutpostLayout.RepairBotMaxHealth:F0}）";

                // ER6-ADAPT-01：第二次出征起（目标已切到铸造前哨外围）才有"上次战斗暴露"这个前提——
                // 见 EnemyAdaptationService 类注释范围裁决，第一次出征（破碎都市）恒 None，走下面的
                // else 分支不调用 ComputeAdaptation。
                var adapt = BuildAdaptationDisplay(state);

                return new PrepSnapshot(true, null, machines, HomeValleySignal.BandwidthCapacity(state),
                    MinRecommendedCargoSlots(target, foundry), foundryIntel, foundry?.EnemyAlertLevel ?? 0f,
                    foundry?.ExpeditionCount ?? 0, target,
                    "外围侦察，重炮回收后撤离；主核心区暂不可进入。",
                    adapt.id, adapt.name, adapt.source, adapt.hazard, adapt.counter,
                    adapt.reinforcement, adapt.reinforcementText);
            }

            RegionRecord region = FracturedCityRegion.Find(state);
            // 敌人是内容锁定表里固定的 2 侦察机+1 干扰机（不随机变化），首次进入前
            // RegionEnemies 尚未播种（EnsureEnemiesSeeded 在 FracturedCityController.Enter 内），
            // 此时展示固定编制而不是"x0"——"没播种"不等于"这个区域没有敌人"，否则第一次准备面板
            // 会误导玩家以为区域已清空。已播种后展示真实存活数，随敌人被击破递减。
            bool ruinsEnemiesSeeded = state.RegionEnemies != null && state.RegionEnemies.Any(e => e.RegionId == FracturedCityLayout.RegionId);
            int aliveScouts = ruinsEnemiesSeeded
                ? state.RegionEnemies.Count(e => e.RegionId == FracturedCityLayout.RegionId && e.EnemyTypeId == EnemyCatalog.ScoutId && e.IsAlive)
                : 2;
            int aliveJammers = ruinsEnemiesSeeded
                ? state.RegionEnemies.Count(e => e.RegionId == FracturedCityLayout.RegionId && e.EnemyTypeId == EnemyCatalog.JammerId && e.IsAlive)
                : 1;
            string intel = $"静默侦察机 x{aliveScouts}（HP{FracturedCityLayout.ScoutMaxHealth:F0}，标记周期8秒）｜" +
                $"静默干扰机 x{aliveJammers}（HP{FracturedCityLayout.JammerMaxHealth:F0}，干扰半径{FracturedCityLayout.JammerRadius:F0}米）";

            // ER6-ADAPT-01：第一次出征（破碎都市）没有"上次战斗"这个前提，恒 None——不调用
            // EnemyAdaptationService.ComputeAdaptation，直接用 None 的文案兜底（AC-ADP-001"无历史为
            // None"字面要求）。90暴露核心增援预告与本次出征目标无关，仍照常计算展示。
            AdaptationCatalog.AdaptationInfo noneInfo = AdaptationCatalog.Describe(AdaptationCatalog.None);
            bool reinforcementForecast = CampaignExposureLedger.HasReachedCoreReinforcement(state);
            string reinforcementForecastText = reinforcementForecast
                ? "信号暴露已突破90：下一次进攻核心分区时，入口会额外增援一台铸造护甲机布防（出发前预告，届时在核心战入口实装）。"
                : null;

            return new PrepSnapshot(true, null, machines, HomeValleySignal.BandwidthCapacity(state),
                MinRecommendedCargoSlots(target, region), intel, region.EnemyAlertLevel, region.ExpeditionCount,
                target, "破碎都市：侦察带回静默技术并撤离。",
                AdaptationCatalog.None, noneInfo.DisplayName, noneInfo.SourceText, noneInfo.HazardText,
                noneInfo.CounterHintText, reinforcementForecast, reinforcementForecastText);
        }

        /// <summary>DEMO-CONTENT-LOCK.md §2.3："第一次出征必需标记器、协议数据盒和三箱废料共5货位；
        /// 第二次出征必需重炮与三箱废料共4货位。"仅供面板展示参考，不是出发硬性拦截项——关键物要在
        /// 区域内才能拾取，出发时机器货舱通常还是空的。铸造前哨外围自己的 <see cref="RegionRecord.ExpeditionCount"/>
        /// 从 0 起（它是"第二次"全局出征但对它自己是"第一次"进入），不能沿用破碎都市那套
        /// "ExpeditionCount&lt;=0 即 5 货位"的判据，按 <paramref name="target"/> 直接分流。</summary>
        public static int MinRecommendedCargoSlots(ExpeditionTarget target, RegionRecord region)
        {
            if (target == ExpeditionTarget.FoundryOutpost)
            {
                return 4;
            }
            return (region?.ExpeditionCount ?? 0) <= 0 ? 5 : 4;
        }

        // ── 出发校验 ─────────────────────────────────────────────────────────────

        public readonly struct RosterValidation
        {
            public readonly bool Success;
            public readonly string[] BlockingReasons;
            public readonly int[] BusyInterruptibleLogicIds;
            public readonly int TotalCargoSlots;
            public readonly float TotalBandwidth;
            public readonly float BandwidthCapacity;
            public readonly bool HasWeapon;

            public RosterValidation(bool success, string[] blockingReasons, int[] busyInterruptibleLogicIds,
                int totalCargoSlots, float totalBandwidth, float bandwidthCapacity, bool hasWeapon)
            {
                Success = success;
                BlockingReasons = blockingReasons ?? Array.Empty<string>();
                BusyInterruptibleLogicIds = busyInterruptibleLogicIds ?? Array.Empty<int>();
                TotalCargoSlots = totalCargoSlots;
                TotalBandwidth = totalBandwidth;
                BandwidthCapacity = bandwidthCapacity;
                HasWeapon = hasWeapon;
            }
        }

        /// <summary>逐项列出全部阻塞原因（不是发现第一条就短路返回）——DEMO-IMPLEMENTATION-SPEC.md
        /// §出征准备"失败时逐项列出阻塞，不只禁用按钮"。<see cref="RosterValidation.BusyInterruptibleLogicIds"/>
        /// 非空且 <see cref="RosterValidation.BlockingReasons"/> 为空时，调用方（UI/<see cref="TryDepart"/>）
        /// 应先走确认中断流程，不是硬阻塞。</summary>
        public static RosterValidation ValidateRoster(CampaignState state, IReadOnlyList<int> selectedLogicIds)
        {
            var reasons = new List<string>();
            selectedLogicIds ??= Array.Empty<int>();

            if (selectedLogicIds.Count < MinRosterSize)
            {
                reasons.Add($"roster-too-small:need>={MinRosterSize}:have={selectedLogicIds.Count}");
            }
            if (selectedLogicIds.Count > MaxRosterSize)
            {
                reasons.Add($"roster-too-large:max={MaxRosterSize}:have={selectedLogicIds.Count}");
            }

            var seen = new HashSet<int>();
            var busyInterruptible = new List<int>();
            int totalCargo = 0;
            float totalBandwidth = 0f;
            bool hasWeapon = false;

            foreach (int logicId in selectedLogicIds)
            {
                if (!seen.Add(logicId))
                {
                    reasons.Add($"duplicate-selection:{logicId}");
                    continue;
                }
                if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord record))
                {
                    reasons.Add($"machine-unknown:{logicId}");
                    continue;
                }

                MachineIntel intel = BuildIntel(state, record);
                if (!intel.IsAlive)
                {
                    reasons.Add($"machine-dead:{logicId}");
                    continue;
                }
                if (!intel.InHomeRegion)
                {
                    reasons.Add($"machine-already-deployed:{logicId}");
                    continue;
                }
                if (intel.IsInFactory)
                {
                    reasons.Add($"machine-in-factory:{logicId}");
                    continue;
                }
                if (intel.BusyUninterruptible)
                {
                    reasons.Add($"machine-busy-uninterruptible:{logicId}:{intel.CurrentWorkOrderId}");
                    continue;
                }

                totalCargo += intel.CargoSlots;
                totalBandwidth += intel.BandwidthCost;
                hasWeapon |= intel.HasWeapon;
                if (intel.BusyKind.HasValue)
                {
                    busyInterruptible.Add(logicId);
                }
            }

            float bandwidthCapacity = HomeValleySignal.BandwidthCapacity(state);
            if (totalBandwidth > bandwidthCapacity)
            {
                reasons.Add($"bandwidth-exceeded:need={totalBandwidth:F0}:cap={bandwidthCapacity:F0}");
            }
            if (!hasWeapon)
            {
                reasons.Add("no-weapon");
            }
            if (totalCargo <= 0)
            {
                reasons.Add("no-cargo-capacity");
            }

            return new RosterValidation(reasons.Count == 0, reasons.ToArray(), busyInterruptible.ToArray(),
                totalCargo, totalBandwidth, bandwidthCapacity, hasWeapon);
        }

        // ── 出发事务 ─────────────────────────────────────────────────────────────

        public enum DepartureOutcome
        {
            Success = 0,
            Blocked = 1,
            NeedsInterruptConfirmation = 2,
            RolledBack = 3,
        }

        public readonly struct DepartureResult
        {
            public readonly DepartureOutcome Outcome;
            public readonly string[] Reasons;
            public readonly int[] InterruptibleLogicIds;

            public DepartureResult(DepartureOutcome outcome, string[] reasons, int[] interruptibleLogicIds)
            {
                Outcome = outcome;
                Reasons = reasons ?? Array.Empty<string>();
                InterruptibleLogicIds = interruptibleLogicIds ?? Array.Empty<int>();
            }

            public bool Success => Outcome == DepartureOutcome.Success;
        }

        /// <summary>唯一出发事务入口。<paramref name="interruptConfirmed"/>＝false 且存在可中断在办工作时，
        /// 不做任何改动，返回 <see cref="DepartureOutcome.NeedsInterruptConfirmation"/> 连同将被中断的
        /// LogicId 列表，供 UI 弹确认框；玩家确认后调用方带 true 重新调用本方法。
        ///
        /// 事务步骤（严格按 STORY-EXECUTION-CARDS.md ER5-EXP-01 第2条顺序）：
        /// 冻结输入 → 中断已确认的在办工作单（"按 WorkOrder 规则交还/保留货物"＝复用
        /// <see cref="HomeValleyWorkOrders.CancelOrder"/> 既有的资源退款+货舱落地行为）→
        /// 自动安全存档（同时是"出发前档"）→ 出发快照（选中名单，用于日志/后续步骤）→
        /// 区域卸载（<see cref="HomeValleyController.Exit"/>）/载入（<see cref="GameLogic.Stage.GameRoot.StartFracturedCity"/>）→
        /// 机器生成/装配登记（<see cref="FracturedCityController.Enter"/> 内部完成，见该类）→
        /// 控制恢复（战略镜头，见下）→ 解冻。任一步异常/失败 → 从"出发前档"回滚（
        /// <see cref="CampaignRestoreOrchestrator.Restore"/>）+ 重新进入归还谷地，不解冻前不返回。
        /// 只有全部步骤成功才 <c>region.ExpeditionCount += 1</c>。</summary>
        public static DepartureResult TryDepart(IReadOnlyList<int> selectedLogicIds, bool interruptConfirmed)
        {
            CampaignState precheckState = CampaignSession.Current;
            if (precheckState == null)
            {
                return new DepartureResult(DepartureOutcome.Blocked, new[] { "no-active-campaign" }, null);
            }

            ExpeditionTarget target = ResolveTarget(precheckState);
            if (target == ExpeditionTarget.None)
            {
                return new DepartureResult(DepartureOutcome.Blocked, new[] { "region-locked" }, null);
            }

            RosterValidation validation = ValidateRoster(precheckState, selectedLogicIds);
            if (!validation.Success)
            {
                return new DepartureResult(DepartureOutcome.Blocked, validation.BlockingReasons, null);
            }

            if (validation.BusyInterruptibleLogicIds.Length > 0 && !interruptConfirmed)
            {
                return new DepartureResult(DepartureOutcome.NeedsInterruptConfirmation, null,
                    validation.BusyInterruptibleLogicIds);
            }

            int activeSlot = CampaignSession.ActiveSlotIndex;
            InputRouter.SetModalUi(true);
            try
            {
                CampaignState state = CampaignSession.Current;

                // ── 中断已确认的在办工作（交还货物/退款，见 CancelOrder 类注释）──────────
                foreach (int logicId in validation.BusyInterruptibleLogicIds)
                {
                    WorkOrderRecord order = HomeValleyWorkOrders.FindActiveOrderForMachine(state, logicId);
                    if (order == null)
                    {
                        continue;
                    }
                    Vector2 dropPos = MachineRegistry.TryGetRecord(logicId, out MachineRecord busyRecord)
                        ? busyRecord.WorldPosition : Vector2.zero;
                    HomeValleyWorkOrders.CancelOrder(state, order.WorkOrderId, dropPos);
                }

                // ── 自动安全存档＝出发前档（回滚基线）───────────────────────────────
                SaveResult saveResult = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionDepartConfirm);
                if (!saveResult.Success)
                {
                    Log.Warning($"[ExpeditionDepartureService] 出发前自动存档未成功（{saveResult.Outcome} " +
                        $"{saveResult.Message}），中止出发，不切换区域。");
                    InputRouter.SetModalUi(false);
                    return new DepartureResult(DepartureOutcome.Blocked,
                        new[] { $"autosave-failed:{saveResult.Outcome}" }, null);
                }

                // ── 出发快照（本次事务的名单，供日志与后续步骤复用，不重新查询）─────────
                int[] manifest = selectedLogicIds.ToArray();

                // ── ER6-ADAPT-01：反制锁定必须在 StartFoundryOutpost（进而 Enter()→
                // EnsureEnemiesSeeded→ReconcileAdaptiveSupportEnemy）之前写入 RegionRecord.AdaptationId，
                // 否则本次进场时 Flanker/JammerSupport 的增援槽位还读不到刚锁定的值。RegionRecord 骨架
                // 记录在玩家第一次进入归还谷地时就已播种（见 HomeValleyController.Enter），此处必然能
                // Find 到，不需要再 EnsureRegionRecordSeeded 一次。────────────────────────
                if (target == ExpeditionTarget.FoundryOutpost)
                {
                    RegionRecord foundryPreLock = FoundryOutpostRegion.Find(state);
                    if (foundryPreLock != null)
                    {
                        EnemyAdaptationService.LockAdaptation(state, foundryPreLock);
                    }
                }

                // ── ER6-LOOP-01：出发时机的 CampaignPhase 前进（FirstExpedition/FoundryScouting）——
                // 与下方 OBJ 完成结算是两条独立触发线，见 CampaignObjectiveTracker.OnDeparted 类注释。
                CampaignObjectiveTracker.OnDeparted(state, target);

                // ── 区域卸载/载入 + 机器生成/装配登记 ───────────────────────────────
                GameRoot.HomeValley?.Exit();
                RegionRecord regionAfter;
                string targetLabel;
                if (target == ExpeditionTarget.FoundryOutpost)
                {
                    GameRoot.StartFoundryOutpost(manifest);
                    if (GameRoot.FoundryOutpost == null || !GameRoot.FoundryOutpost.IsActive)
                    {
                        throw new InvalidOperationException("FoundryOutpostController.Enter 未能激活（区域状态在校验后被并发改变？）。");
                    }
                    regionAfter = FoundryOutpostRegion.Find(state);
                    targetLabel = "铸造前哨外围";
                }
                else
                {
                    GameRoot.StartFracturedCity(manifest);
                    if (GameRoot.FracturedCity == null || !GameRoot.FracturedCity.IsActive)
                    {
                        throw new InvalidOperationException("FracturedCityController.Enter 未能激活（区域状态在校验后被并发改变？）。");
                    }
                    regionAfter = FracturedCityRegion.Find(state);
                    targetLabel = "破碎都市";
                }

                // ── 只有整个切换成功才增加 expeditionCount ──────────────────────────
                if (regionAfter != null)
                {
                    regionAfter.ExpeditionCount += 1;
                    // ER6-EXPOSE-01："一次远征中每累计30秒直控"——"一次远征"的边界就是这里，新的
                    // 出发必须从0开始累计，不带着上一次远征剩下的零头。
                    regionAfter.DirectControlAccumulatedSeconds = 0f;
                }

                // ── 控制恢复：新区域固定以战略视角开场、无遗留直控目标（见
                // FracturedCityController/FoundryOutpostController.SetupCameraDirector 的
                // startInStrategy:true），本步骤无需额外动作，这里只显式记一条日志确认该不变量，
                // 供故障注入测试断言。────────────────────────────────────────────
                Log.Info($"[ExpeditionDepartureService] 出发成功：{manifest.Length} 台机器进入{targetLabel}，" +
                    $"第 {regionAfter?.ExpeditionCount ?? -1} 次出击。");

                // 立即把 ExpeditionCount 增量也持久化，不必等下一次自然存档点才落盘
                // （FracturedCityController.Enter 内部已经存过一次，这里的改动在那之后发生，
                // 需要再存一次才不会在存读档间产生"内存比磁盘新"的窗口）。
                SaveResult postResult = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionDepartConfirm);
                if (!postResult.Success)
                {
                    Log.Warning($"[ExpeditionDepartureService] 出发后确认存档未成功（{postResult.Outcome} " +
                        $"{postResult.Message}），expeditionCount 增量暂未落盘，下次自然存档点会补上。");
                }

                InputRouter.SetModalUi(false);
                // ER8-CONTENT-01：出发事务提交成功的唯一出口。
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.ExpeditionDepart, $"{selectedLogicIds.Count} 台机器");
                return new DepartureResult(DepartureOutcome.Success, null, null);
            }
            catch (Exception e)
            {
                Log.Error($"[ExpeditionDepartureService] 出发事务中途异常，回滚到出发前档：{e}");
                bool rolledBack = RollbackToPreDepartureSave(activeSlot);
                InputRouter.SetModalUi(false);
                return new DepartureResult(DepartureOutcome.RolledBack,
                    new[] { rolledBack ? $"transaction-failed-rolled-back:{e.Message}" : $"transaction-failed-rollback-also-failed:{e.Message}" },
                    null);
            }
        }

        /// <summary>复用 ER1-SAVE-02 的 <see cref="CampaignRestoreOrchestrator"/> 从刚写的"出发前档"
        /// 磁盘文件重新读回内存态，并把可能已经半激活的破碎都市/归还谷地控制器收拾回"归还谷地
        /// 激活中"的一致状态——不留一个两边都没激活或两边都激活的中间态。</summary>
        private static bool RollbackToPreDepartureSave(int slotIndex)
        {
            if (slotIndex < 0)
            {
                Log.Error("[ExpeditionDepartureService] 回滚失败：没有有效的存档槽位（从未成功存过出发前档）。");
                return false;
            }

            GameRoot.FracturedCity?.Exit(evacuateSuccess: false);
            GameRoot.FoundryOutpost?.Exit(evacuateSuccess: false);
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                GameRoot.HomeValley.Exit();
            }

            RestoreResult restore = CampaignRestoreOrchestrator.Restore(slotIndex);
            if (!restore.Success)
            {
                Log.Error($"[ExpeditionDepartureService] 回滚失败：从出发前档重新读取未成功（{restore.FailedStep} " +
                    $"{restore.Message}）。战役会话可能处于不一致状态，建议玩家从主菜单重新读档。");
                return false;
            }

            CampaignSession.Set(slotIndex, restore.State);
            GameRoot.ResumeHomeValley();
            Log.Info("[ExpeditionDepartureService] 已回滚到出发前档并重新进入归还谷地，未复制任何机器/货物/奖励。");
            return true;
        }
    }
}
