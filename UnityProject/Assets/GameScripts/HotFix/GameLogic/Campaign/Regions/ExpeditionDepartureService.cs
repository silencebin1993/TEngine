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

            public PrepSnapshot(bool regionReachable, string blockedReason, MachineIntel[] machines,
                float bandwidthCapacity, int minRecommendedCargoSlots, string enemyIntelText,
                float enemyAlertLevel, int expeditionCount)
            {
                RegionReachable = regionReachable;
                BlockedReason = blockedReason;
                Machines = machines;
                BandwidthCapacity = bandwidthCapacity;
                MinRecommendedCargoSlots = minRecommendedCargoSlots;
                EnemyIntelText = enemyIntelText;
                EnemyAlertLevel = enemyAlertLevel;
                ExpeditionCount = expeditionCount;
            }
        }

        /// <summary>面板展示用的整份快照。<paramref name="state"/> 为空或区域尚未 Available 时
        /// <see cref="PrepSnapshot.RegionReachable"/> 为假并给出 <see cref="PrepSnapshot.BlockedReason"/>，
        /// 面板据此只显示"未解锁"而不是空列表。</summary>
        public static PrepSnapshot BuildPrepSnapshot(CampaignState state)
        {
            if (state == null)
            {
                return new PrepSnapshot(false, "no-active-campaign", Array.Empty<MachineIntel>(), 0f, 0, null, 0f, 0);
            }

            RegionRecord region = FracturedCityRegion.Find(state);
            if (region == null || region.State == RegionState.Locked)
            {
                return new PrepSnapshot(false, "region-locked", Array.Empty<MachineIntel>(),
                    HomeValleySignal.BandwidthCapacity(state), 0, null, region?.EnemyAlertLevel ?? 0f,
                    region?.ExpeditionCount ?? 0);
            }

            MachineIntel[] machines = MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId)
                .OrderBy(m => m.DisplayNumber)
                .Select(m => BuildIntel(state, m))
                .ToArray();

            // 敌人是内容锁定表里固定的 2 侦察机+1 干扰机（不随机变化），首次进入前
            // RegionEnemies 尚未播种（EnsureEnemiesSeeded 在 FracturedCityController.Enter 内），
            // 此时展示固定编制而不是"x0"——"没播种"不等于"这个区域没有敌人"，否则第一次准备面板
            // 会误导玩家以为区域已清空。已播种后展示真实存活数，随敌人被击破递减。
            bool enemiesSeeded = state.RegionEnemies != null && state.RegionEnemies.Any(e => e.RegionId == FracturedCityLayout.RegionId);
            int aliveScouts = enemiesSeeded
                ? state.RegionEnemies.Count(e => e.RegionId == FracturedCityLayout.RegionId && e.EnemyTypeId == EnemyCatalog.ScoutId && e.IsAlive)
                : 2;
            int aliveJammers = enemiesSeeded
                ? state.RegionEnemies.Count(e => e.RegionId == FracturedCityLayout.RegionId && e.EnemyTypeId == EnemyCatalog.JammerId && e.IsAlive)
                : 1;
            string intel = $"静默侦察机 x{aliveScouts}（HP{FracturedCityLayout.ScoutMaxHealth:F0}，标记周期8秒）｜" +
                $"静默干扰机 x{aliveJammers}（HP{FracturedCityLayout.JammerMaxHealth:F0}，干扰半径{FracturedCityLayout.JammerRadius:F0}米）";

            return new PrepSnapshot(true, null, machines, HomeValleySignal.BandwidthCapacity(state),
                MinRecommendedCargoSlots(region), intel, region.EnemyAlertLevel, region.ExpeditionCount);
        }

        /// <summary>DEMO-CONTENT-LOCK.md §2.3："第一次出征必需标记器、协议数据盒和三箱废料共5货位；
        /// 第二次出征必需重炮与三箱废料共4货位。"仅供面板展示参考，不是出发硬性拦截项——关键物要在
        /// 区域内才能拾取，出发时机器货舱通常还是空的。</summary>
        public static int MinRecommendedCargoSlots(RegionRecord region)
        {
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

            RegionRecord region = FracturedCityRegion.Find(precheckState);
            if (region == null || region.State == RegionState.Locked)
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

                // ── 区域卸载/载入 + 机器生成/装配登记 ───────────────────────────────
                GameRoot.HomeValley?.Exit();
                GameRoot.StartFracturedCity(manifest);

                if (GameRoot.FracturedCity == null || !GameRoot.FracturedCity.IsActive)
                {
                    throw new InvalidOperationException("FracturedCityController.Enter 未能激活（区域状态在校验后被并发改变？）。");
                }

                // ── 只有整个切换成功才增加 expeditionCount ──────────────────────────
                RegionRecord regionAfter = FracturedCityRegion.Find(state);
                if (regionAfter != null)
                {
                    regionAfter.ExpeditionCount += 1;
                }

                // ── 控制恢复：新区域固定以战略视角开场、无遗留直控目标（见
                // FracturedCityController.SetupCameraDirector 的 startInStrategy:true），本步骤
                // 无需额外动作，这里只显式记一条日志确认该不变量，供故障注入测试断言。──────
                Log.Info($"[ExpeditionDepartureService] 出发成功：{manifest.Length} 台机器进入破碎都市，" +
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
