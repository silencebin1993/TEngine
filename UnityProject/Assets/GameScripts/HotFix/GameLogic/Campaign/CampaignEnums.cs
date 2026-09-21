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
        /// <summary>自动点 1/6：家园进入完成。TODO(ER2-SCENE-01)。</summary>
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
