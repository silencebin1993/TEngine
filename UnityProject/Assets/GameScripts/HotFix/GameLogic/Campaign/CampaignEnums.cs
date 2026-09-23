using System;

namespace GameLogic.Campaign
{
    /// <summary>ER1-SAVE-01：战役宏观阶段（ERD-DAT-001 封闭枚举）。只能前进，不可跳过第二次编译门禁。</summary>
    [Serializable]
    public enum CampaignPhase
    {
        Landing = 0,
        Rooted = 1,
        FirstExpedition = 2,
        CrossCompiled = 3,
        FoundryScouting = 4,
        SecondCrossCompiled = 5,
        CoreAssault = 6,
        BeaconReady = 7,
        Completed = 8,
        Failed = 9,
    }

    /// <summary>ER1-SAVE-01：六类自动存档触发点（ERD-SAV-002）+ 菜单显式操作。
    /// 触发调用方大多数尚不存在，见 <see cref="CampaignAutoSaveService"/> 各常量旁的 TODO。</summary>
    [Serializable]
    public enum SaveReason
    {
        /// <summary>主菜单"新建"创建初始存档。</summary>
        NewCampaign = 0,
        /// <summary>主菜单显式保存（非六类自动点，预留）。</summary>
        Manual = 1,
        /// <summary>自动点 1/6：家园进入完成。ER2-SCENE-01 起由
        /// <see cref="GameLogic.Campaign.Regions.HomeValleyController.Enter"/> 触发。</summary>
        HomeEntryComplete = 2,
        /// <summary>自动点 2/6：远征出发确认前。TODO(ER5-EXP-01)。</summary>
        ExpeditionDepartConfirm = 3,
        /// <summary>自动点 3/6：远征结算完成后。TODO(ER5-RETURN-01)。</summary>
        ExpeditionResolutionComplete = 4,
        /// <summary>自动点 4/6：蓝图保存后。TODO(ER4-BLP-01)。</summary>
        BlueprintSaved = 5,
        /// <summary>自动点 5/6：Boss 战进入前。TODO(ER7-CORE-01)。</summary>
        BossEngageEnter = 6,
        /// <summary>自动点 6/6：导航信标启动前。TODO(ER7-BEACON-01)。</summary>
        BeaconLaunchEnter = 7,
    }

    /// <summary>ERD-DAT-004 BuildingRecord.constructionState 封闭枚举。</summary>
    [Serializable]
    public enum BuildingConstructionState
    {
        Planned = 0,
        MaterialReserved = 1,
        Building = 2,
        Operational = 3,
        Damaged = 4,
        Disabled = 5,
        Destroyed = 6,
    }

    /// <summary>ERD-DAT-004 BuildingRecord.powerState 封闭枚举（此前是裸 <c>string</c> 占位，
    /// ER2-SCENE-01 起改为类型化）。UI-AND-ONBOARDING-SPEC.md UI-04/UI-16 明确这是与
    /// <see cref="BuildingConstructionState"/> 正交的第二根轴（例如"Operational 但 Brownout"，
    /// 见 UI-16："信标位...Operational 但 Brownout 时给电力缺 20"）：ConstructionState 描述建筑本身
    /// 建成/受损状态，PowerState 描述它当前能否吃到电/出口是否堵塞。<see cref="Brownout"/>（电力容量
    /// 不足降级）与 <see cref="OutputBlocked"/>（工厂出口堵塞）的真实判定分别属于 ER3-PWR-01
    /// （ERD-ECO-002）和 ER4-FAC-01（ERD-FAC-001），本 Story 只建立类型化枚举与可区分表现基础设施，
    /// 新建建筑一律先给 <see cref="NotApplicable"/>（核心/免电建筑）或 <see cref="Unpowered"/>，
    /// 不在这里伪造电网仲裁结果。</summary>
    [Serializable]
    public enum BuildingPowerState
    {
        /// <summary>该建筑不参与电网（当前内容锁定表里没有这类建筑，占位保留）。</summary>
        NotApplicable = 0,
        /// <summary>已建成但尚未接入电网/发电机未修复。</summary>
        Unpowered = 1,
        /// <summary>正常吃到所需电力。</summary>
        Powered = 2,
        /// <summary>电网容量不足，按优先级降级（ERD-ECO-002）。</summary>
        Brownout = 3,
        /// <summary>产物出口堵塞（ERD-FAC-001，仅装配类建筑适用）。</summary>
        OutputBlocked = 4,
    }

    /// <summary>ERD-DAT-005 ResourceTransaction.state。</summary>
    [Serializable]
    public enum ResourceTransactionState
    {
        Proposed = 0,
        Reserved = 1,
        Running = 2,
        Committed = 3,
        Cancelled = 4,
        Failed = 5,
    }

    /// <summary>ERD-WRK-001 WorkOrder.kind 封闭枚举。</summary>
    [Serializable]
    public enum WorkOrderKind
    {
        Haul = 0,
        Build = 1,
        Repair = 2,
        Salvage = 3,
        Recharge = 4,
    }

    /// <summary>ERD-WRK-001 WorkOrder.state。</summary>
    [Serializable]
    public enum WorkOrderState
    {
        Proposed = 0,
        Ready = 1,
        Reserved = 2,
        InProgress = 3,
        Waiting = 4,
        Completed = 5,
        Cancelled = 6,
        Failed = 7,
    }

    /// <summary>ERD-FAC-001 工厂队列 kind。</summary>
    [Serializable]
    public enum FactoryQueueKind
    {
        Produce = 0,
        Retrofit = 1,
    }

    /// <summary>ERD-FAC-001 工厂队列 state。</summary>
    [Serializable]
    public enum FactoryQueueState
    {
        Queued = 0,
        WaitingResources = 1,
        WaitingPower = 2,
        WaitingTarget = 3,
        Running = 4,
        OutputBlocked = 5,
        Completed = 6,
        Cancelled = 7,
        Failed = 8,
    }

    /// <summary>ER4-PRIM-04 STORY-EXECUTION-CARDS.md：合成台两条最小配方。</summary>
    public enum CraftQueueKind
    {
        /// <summary>两件 focus_basic + 5 废料 → 一件 focus_plus。</summary>
        Upgrade = 0,
        /// <summary>任意一件可拆芯片 → 2 废料（固定返还）。</summary>
        Disassemble = 1,
    }

    /// <summary>PRIMITIVE-FULL-DEMO-SPEC.md §4.2 合成/拆解事务逐状态真相。与
    /// <see cref="FactoryQueueState"/> 同构（单 Running 工位 FIFO + 断电暂停），但
    /// Committing/OutputWaiting 是本枚举独有——合成有"到时二次核验+原子提交"这一步骤需要显式建模
    /// （工厂生产的"完工"判定更简单，不需要单独的 Committing 态）。</summary>
    [Serializable]
    public enum CraftQueueState
    {
        Queued = 0,
        WaitingResources = 1,
        WaitingPower = 2,
        Running = 3,
        Committing = 4,
        /// <summary>升级产物因仓满而进入待领取队列时的终态（拆解不会进入本状态，产出是废料无容量限制）。</summary>
        OutputWaiting = 5,
        Completed = 6,
        Cancelled = 7,
        Failed = 8,
    }

    /// <summary>ERD-EXP-001 RegionRecord.state。</summary>
    [Serializable]
    public enum RegionState
    {
        Locked = 0,
        Available = 1,
        Active = 2,
        Cleared = 3,
    }

    /// <summary>DEMO-CONTENT-LOCK.md §逐目标持久化：ObjectiveRecord.state（仅三态）。
    /// 未在 ERD-DAT-001~006 单独编号，但 STORY-EXECUTION-CARDS.md #ER1-SAVE-01 明确要求随本 Story 一并定义。</summary>
    [Serializable]
    public enum ObjectiveState
    {
        Locked = 0,
        Active = 1,
        Completed = 2,
    }

    /// <summary>ER7-CORE-01 STORY-EXECUTION-CARDS.md："Boss 状态仅 Locked→Shielded→Phase1→
    /// Transition→Phase2→Destroyed，非法跳转拒绝并给开发诊断，不在玩家 HUD 泄漏枚举名"——存入
    /// <see cref="RegionRecord.CoreState"/>（该字段此前是 ER6-FOUNDRY-01 留下的纯骨架，从未被写过），
    /// 合法转换表与拒绝逻辑唯一实现见 <see cref="Regions.FoundryOutpostCoreBoss.TryTransition"/>。
    /// 玩家可见文案走 <see cref="Regions.FoundryOutpostCoreBoss.DisplayPhaseText"/>，不直接吐这个
    /// 枚举名。</summary>
    [Serializable]
    public enum CoreBossState
    {
        Locked = 0,
        Shielded = 1,
        Phase1 = 2,
        Transition = 3,
        Phase2 = 4,
        Destroyed = 5,
    }

    /// <summary>ER1-SAVE-01：存档槽四态（STORY-EXECUTION-CARDS.md #ER1-SAVE-01 第一条）。</summary>
    [Serializable]
    public enum CampaignSlotState
    {
        /// <summary>槽位文件不存在。</summary>
        Empty = 0,
        /// <summary>可正常读取。</summary>
        Ready = 1,
        /// <summary>文件存在但校验和/JSON 解析失败。</summary>
        Corrupt = 2,
        /// <summary>schemaVersion 比当前客户端更新，明确拒绝读取。</summary>
        Incompatible = 3,
    }
}
