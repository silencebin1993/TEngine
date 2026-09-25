using System;
using System.Collections.Generic;
using System.Linq;

namespace GameLogic.Campaign
{
    /// <summary>ERD-SAV-003 固定恢复顺序里的每一步，供调用方/测试精确断言失败发生在哪一步。
    /// 顺序即 <see cref="CampaignRestoreOrchestrator.Restore"/> 内部执行顺序，不得重排：
    /// 校验版本与校验和 → 领域记录 → 内容解锁/蓝图 → 建筑/事务/队列 → 机器 → 区域 SimWorld →
    /// 装配登记 → 控制恢复 → Objective/HUD。</summary>
    public enum RestoreStep
    {
        None = 0,
        VersionAndChecksum,
        DomainRecords,
        ContentAndBlueprints,
        BuildingsAndQueues,
        MachineRecords,
        RegionSimWorld,
        SpawnAndLoadoutRegistration,
        ControlRestore,
        ObjectiveAndHud,
    }

    public enum RestoreOutcome
    {
        Success = 0,
        Fail = 1,
    }

    /// <summary>一次 <see cref="CampaignRestoreOrchestrator.Restore"/> 调用的结果。失败时
    /// <see cref="State"/> 恒为 null——编排器不会把"半恢复"的状态交给调用方冒充可用数据，
    /// 调用方此时不得触碰 <see cref="CampaignSession"/>，磁盘上的原档/备份也不受任何影响
    /// （本类全程只读 <see cref="CampaignSaveService.Load"/>，不写盘）。</summary>
    public readonly struct RestoreResult
    {
        public readonly RestoreOutcome Outcome;
        public readonly RestoreStep FailedStep;
        public readonly string Message;
        public readonly CampaignState State;
        public readonly bool HasBackup;
        public readonly string[] Warnings;
        /// <summary>FG0-SAVE-01：VersionAndChecksum 步骤的读档结果与稳定原因码（玩家文字据此生成）。</summary>
        public readonly LoadOutcome LoadOutcome;
        public readonly SaveFailureReason Reason;
        public readonly int FileSchemaVersion;
        /// <summary>FG0-SAVE-01：读档时新产生的通知（已移除内容转废料等），进入游戏后展示给玩家。</summary>
        public readonly SaveNoticeRecord[] Notices;

        public RestoreResult(RestoreOutcome outcome, RestoreStep failedStep, string message,
            CampaignState state, bool hasBackup, string[] warnings,
            LoadOutcome loadOutcome = LoadOutcome.Success, SaveFailureReason reason = SaveFailureReason.None,
            int fileSchemaVersion = -1, SaveNoticeRecord[] notices = null)
        {
            Outcome = outcome;
            FailedStep = failedStep;
            Message = message;
            State = state;
            HasBackup = hasBackup;
            Warnings = warnings ?? Array.Empty<string>();
            LoadOutcome = loadOutcome;
            Reason = reason;
            FileSchemaVersion = fileSchemaVersion;
            Notices = notices ?? Array.Empty<SaveNoticeRecord>();
        }

        public bool Success => Outcome == RestoreOutcome.Success;
    }

    /// <summary>
    /// ER1-SAVE-02：ERD-SAV-003 读档恢复编排器——固定恢复顺序的唯一入口，取代
    /// <c>MainMenuUI.LoadIntoSession</c> 此前直接串联 <c>CampaignSaveService.Load</c> +
    /// <c>MachineRegistry.LoadFromCampaignState</c> 的写法。
    ///
    /// ── 步骤粒度与失败语义 ──
    /// 每一步单独 try/catch；任一步判定为**致命**（结构性损坏，如 checksum 不匹配、
    /// 重复 ID、反序列化异常）立即中止并返回失败结果，不再执行后续步骤、不触碰
    /// <see cref="CampaignSession"/>——调用方据此展示"恢复备份/返回菜单"，原档在磁盘上
    /// 原封不动（本类不写盘）。单条记录级别的数据质量问题（例如某几条 <see cref="MachineRecord"/>
    /// 缺字段）沿用 <see cref="MachineRegistry"/> 已有的 Reject-to-Safe 粒度——非致命，计入
    /// <see cref="RestoreResult.Warnings"/> 继续往下走，不让"一条记录坏了"变成"整份存档打不开"。
    ///
    /// ── 各步骤当前真实程度（诚实登记，不夸大）──
    /// <list type="number">
    /// <item>VersionAndChecksum：真实——委托 <see cref="CampaignSaveService.Load"/>（头部/schema/
    /// checksum 校验）。</item>
    /// <item>DomainRecords：真实——确认反序列化产出的 <see cref="CampaignState"/> 非空。</item>
    /// <item>ContentAndBlueprints：真实的结构校验（内容 ID / BlueprintId 不重复），但**没有消费方**——
    /// ER4-BLP-01 建立正式蓝图目录前，恢复出来的 <see cref="CampaignState.BlueprintRecords"/>
    /// 目前恒为空数组，本步骤先把"经过这一步"的编排位置占住。</item>
    /// <item>BuildingsAndQueues：同上，结构校验建筑/工作单/工厂队列 ID 不重复，无消费方
    /// （ER3-ECO-01/ER3-WRK-01/ER4-FAC-01 前恒为空数组）。</item>
    /// <item>MachineRecords：真实——本 Story 系列里唯一有真实生产数据的步骤，委托
    /// <see cref="MachineRegistry.LoadFromCampaignState"/>（ER1-ID-01 已验证）。</item>
    /// <item>RegionSimWorld：结构校验区域 ID 不重复；**真正创建区域 SimWorld 是 no-op**——
    /// ER2-SCENE-01/ER5-REGION-01 建立正式区域系统前无事可做，计入 Warnings 明确声明。</item>
    /// <item>SpawnAndLoadoutRegistration：**纯 no-op 占位**——按 ERD-SAV-003 顺序，"正式" Spawn
    /// 应在区域 SimWorld 建立后进行；当前主菜单阶段不创建任何 SimWorld 实体，真正的 Spawn 由
    /// 具体区域入口（例如 <c>CellStageFlow.SetupSim</c>）在玩家进入该区域时驱动，不在本编排器
    /// 职责内提前发生。</item>
    /// <item>ControlRestore：真实——校验 <see cref="CampaignState.ControlHandoff"/> 指向的
    /// LogicId 若已不存在/已阵亡则合法回退为 0（不再指向非法目标），依赖 MachineRecords 步骤
    /// 已把 <see cref="MachineRegistry"/> 内存态填好。真正把这份数据变成 SimBridge 的实际控制
    /// 切换发生在后续进入具体区域时（见 <c>CellStageFlow</c> 的 <c>ResolveCampaignControlHandoff</c>），
    /// 本步骤只负责"数据层面的合法性"，不持有任何 SimWorld 引用。</item>
    /// <item>ObjectiveAndHud：结构校验 ObjectiveId 不重复；无消费方（Objective 系统未接入前
    /// 恒为空数组），计入 Warnings。</item>
    /// </list>
    /// </summary>
    public static class CampaignRestoreOrchestrator
    {
        public static RestoreResult Restore(int slotIndex)
        {
            var warnings = new List<string>();

            // ── 1. VersionAndChecksum ─────────────────────────
            LoadResult loadResult;
            try
            {
                loadResult = CampaignSaveService.Load(slotIndex);
            }
            catch (Exception e)
            {
                return Fail(RestoreStep.VersionAndChecksum, $"读档抛出未捕获异常：{e.Message}", slotIndex);
            }

            if (!loadResult.Success)
            {
                bool backup = CampaignSaveService.GetSlotMetadata(slotIndex).HasBackup;
                return new RestoreResult(RestoreOutcome.Fail, RestoreStep.VersionAndChecksum,
                    $"存档头部/校验和/版本检查未通过（{loadResult.Outcome}）：{loadResult.Message}", null, backup,
                    Array.Empty<string>(), loadResult.Outcome, loadResult.Reason,
                    loadResult.MigrationFailedFromVersion > 0 ? loadResult.MigrationFailedFromVersion : loadResult.FileSchemaVersion);
            }

            // ── 2. DomainRecords ──────────────────────────────
            CampaignState state = loadResult.State;
            if (state == null)
            {
                return Fail(RestoreStep.DomainRecords, "读档成功但 CampaignState 为空。", slotIndex);
            }

            // ── 3. ContentAndBlueprints ───────────────────────
            try
            {
                RequireNoDuplicates(state.UnlockedContentIds, "UnlockedContentIds");
                RequireNoDuplicateBlueprintIds(state.BlueprintRecords);
            }
            catch (Exception e)
            {
                return Fail(RestoreStep.ContentAndBlueprints, e.Message, slotIndex, state);
            }
            if (state.BlueprintRecords == null || state.BlueprintRecords.Length == 0)
            {
                warnings.Add("[ContentAndBlueprints] 无消费方：ER4-BLP-01 前 BlueprintRecords 恒为空数组。");
            }

            // ── 4. BuildingsAndQueues ─────────────────────────
            try
            {
                RequireNoDuplicates(state.BuildingRecords?.Select(r => r?.BuildingId), "BuildingRecords.BuildingId");
                RequireNoDuplicates(state.WorkOrders?.Select(r => r?.WorkOrderId), "WorkOrders.WorkOrderId");
                RequireNoDuplicates(state.FactoryQueues?.Select(r => r?.QueueItemId), "FactoryQueues.QueueItemId");
                RequireNoDuplicates(state.ResourceTransactions?.Select(r => r?.TransactionId), "ResourceTransactions.TransactionId");
            }
            catch (Exception e)
            {
                return Fail(RestoreStep.BuildingsAndQueues, e.Message, slotIndex, state);
            }
            if (state.FactoryQueues == null || state.FactoryQueues.Length == 0)
            {
                warnings.Add("[BuildingsAndQueues] 无消费方：ER4-FAC-01 前 FactoryQueues 恒为空数组。");
            }

            // ── 5. MachineRecords（唯一真正有生产数据的恢复步骤）──
            MachineOpResult machineResult;
            try
            {
                machineResult = MachineRegistry.LoadFromCampaignState(state);
                // MachineRegistry 拒绝的记录只从它自己的内存态里剔除，不会回头改 state.MachineRecords
                // 这个原始反序列化数组——如果不在这里同步一次，result.State.MachineRecords 会在
                // "刚读档但还没发生下一次存档"这段窗口期继续带着已被拒绝的脏记录，与 MachineRegistry
                // 的真实内存态不一致。立刻按 MachineRegistry 的口径重写一次，保证编排器返回的
                // CampaignState 从这一刻起就是唯一准确的视图，不必等到下次 SaveAuto 才纠正。
                MachineRegistry.ExportToCampaignState(state);
            }
            catch (Exception e)
            {
                return Fail(RestoreStep.MachineRecords,
                    $"MachineRegistry 恢复抛出未捕获异常：{e.Message}", slotIndex, state);
            }
            if (!machineResult.Success)
            {
                // 部分记录被拒绝、其余记录已正常加载——非致命，与 MachineRegistry 自身的
                // Reject-to-Safe 粒度一致（ER1-ID-01 已验证），不让单条记录损坏拖垮整份存档。
                warnings.Add($"[MachineRecords] {machineResult.Message}");
            }

            // ── 6. RegionSimWorld ─────────────────────────────
            try
            {
                RequireNoDuplicates(state.RegionRecords?.Select(r => r?.RegionId), "RegionRecords.RegionId");
            }
            catch (Exception e)
            {
                return Fail(RestoreStep.RegionSimWorld, e.Message, slotIndex, state);
            }
            warnings.Add("[RegionSimWorld] no-op：ER2-SCENE-01/ER5-REGION-01 前无正式区域 SimWorld 可创建。");

            // ── 7. SpawnAndLoadoutRegistration ────────────────
            warnings.Add("[SpawnAndLoadoutRegistration] no-op：主菜单阶段不创建 SimWorld 实体，" +
                "正式 Spawn 由玩家进入具体区域时驱动（例如 CellStageFlow.SetupSim）。");

            // ── 8. ControlRestore ──────────────────────────────
            try
            {
                ReconcileControlHandoff(state);
            }
            catch (Exception e)
            {
                return Fail(RestoreStep.ControlRestore, e.Message, slotIndex, state);
            }

            // ── 9. ObjectiveAndHud ─────────────────────────────
            try
            {
                RequireNoDuplicates(state.ObjectiveRecords?.Select(r => r?.ObjectiveId), "ObjectiveRecords.ObjectiveId");
            }
            catch (Exception e)
            {
                return Fail(RestoreStep.ObjectiveAndHud, e.Message, slotIndex, state);
            }
            if (state.ObjectiveRecords == null || state.ObjectiveRecords.Length == 0)
            {
                warnings.Add("[ObjectiveAndHud] 无消费方：Objective/HUD 系统未接入前恒为空数组。");
            }

            // 主档刚通过完整读档：这里只需要知道备份文件在不在，不必再整份读一遍（大存档省几百毫秒）。
            bool hasBackup = CampaignSaveService.BackupFileExists(slotIndex);
            return new RestoreResult(RestoreOutcome.Success, RestoreStep.None, null, state, hasBackup,
                warnings.ToArray(), LoadOutcome.Success, SaveFailureReason.None, loadResult.FileSchemaVersion, loadResult.Notices);
        }

        /// <summary>ControlRestore 步骤的实现：<see cref="CampaignState.ControlHandoff"/> 指向的
        /// LogicId 若在刚恢复的 <see cref="MachineRegistry"/> 里查无此机器或已阵亡，合法回退为 0
        /// （"明确为无"，而不是让调用方后续拿着一个非法 LogicId 去尝试控制切换）。要求
        /// <see cref="MachineRegistry.LoadFromCampaignState"/>（步骤 5）已经执行过。</summary>
        private static void ReconcileControlHandoff(CampaignState state)
        {
            if (state.ControlHandoff == null)
            {
                state.ControlHandoff = new ControlHandoffRecord();
                return;
            }

            if (state.ControlHandoff.ControlledLogicId == 0)
            {
                return;
            }

            bool valid = MachineRegistry.TryGetRecord(state.ControlHandoff.ControlledLogicId, out MachineRecord record)
                && record.IsAlive;
            if (!valid)
            {
                state.ControlHandoff.ControlledLogicId = 0;
            }
        }

        private static void RequireNoDuplicates(IEnumerable<string> ids, string fieldName)
        {
            if (ids == null)
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                if (!seen.Add(id))
                {
                    throw new InvalidOperationException($"{fieldName} 出现重复 ID：{id}，存档结构损坏，拒绝恢复。");
                }
            }
        }

        private static void RequireNoDuplicateBlueprintIds(BlueprintRecord[] records)
        {
            if (records == null)
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (BlueprintRecord r in records)
            {
                string id = r?.BlueprintId;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                if (!seen.Add(id))
                {
                    throw new InvalidOperationException($"BlueprintRecords 出现重复 BlueprintId：{id}，存档结构损坏，拒绝恢复。");
                }
            }
        }

        /// <summary><paramref name="_"/> 只是标注"这一步失败前已经拿到过哪个 state 实例"方便调用点
        /// 阅读，返回结果里始终不带它出去——失败结果的 <see cref="RestoreResult.State"/> 恒为 null，
        /// 调用方不得拿到半恢复的状态当可用数据用。</summary>
        private static RestoreResult Fail(RestoreStep step, string message, int slotIndex, CampaignState _ = null)
        {
            // FG0-SAVE-01（FGR-SYS-003）：头部与校验和都通过、但恢复失败的主档登记为"读档失败"，之后列表把它显示为损坏、
            // "读取备份"真正可用，不再反复读同一份坏档；hasBackup 因此按完整校验过的备份给出。
            CampaignSaveService.RecordLoadFailure(slotIndex, SaveFailureReason.Payload, CampaignSaveService.CurrentSchemaVersion);
            bool hasBackup = CampaignSaveService.GetSlotMetadata(slotIndex).HasBackup;
            // 头部与校验都通过、但结构校验失败（重复 ID 等）：对玩家就是"存档内容无法解析"。
            return new RestoreResult(RestoreOutcome.Fail, step, message, null, hasBackup, Array.Empty<string>(),
                LoadOutcome.Corrupt, SaveFailureReason.Payload);
        }
    }
}
