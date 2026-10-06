using System;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG6-DEF-08（FG06 FGR-DEF-050～053；第 6 节存档“突袭历史”）：突袭结算的存档域，挂在 <see cref="RaidState.Results"/>。唯一写入口 <see cref="Defense.RaidResultService"/>。
    /// 只加字段、不升域版本（ADR FG0-SAVE-01）：旧档没有 = 没有结算记录（旧的突袭历史照常显示，标“没有攻城结算”）。
    /// 全部按统一时钟的步数与世界种子派生，存读档、观察 / 不观察、暂停与 0.5x～3x 下逐字段一致。
    /// </summary>
    [Serializable]
    public sealed class RaidResultState
    {
        public int NextSerial = 1;
        /// <summary>结算记录（进行中的 EndTick &lt; 0；新的在后；结束的最多保留 raid.history_max 条）。</summary>
        public RaidResultRecord[] Results = Array.Empty<RaidResultRecord>();
        /// <summary>残骸去向（0 先送解析台 / 1 先送回收站 / 2 留在仓库 / 3 不自动搬运；fgdata_raidresult.WRECK_ROUTES 同一顺序）。玩家在突袭历史面板里改。</summary>
        public int WreckRouting;
        /// <summary>下一次检查仓库残骸去向的步（-1 = 从下一步起算）。</summary>
        public long NextRouteTick = -1;
        /// <summary>残骸地面物序号（地面物 ID 确定性，不用 GUID）。</summary>
        public int NextWreckSerial = 1;
        /// <summary>FGR-DEF-053：归还核心被突袭摧毁（战役失败）。此后不再写任何存档。</summary>
        public bool CoreLost;
        public long CoreLostTick = -1;
        public int CoreLostResult;
        /// <summary>突袭预警自动存档已经存过的波次（同一波只存一次；最近 8 个）。</summary>
        public int[] AutosavedWaves = Array.Empty<int>();
        /// <summary>统计：累计留下的残骸份数、掉落件数、送去解析台 / 回收站的份数。</summary>
        public long TotalWrecks;
        public long TotalDrops;
        public long WrecksToBench;
        public long WrecksToRecycler;
    }

    /// <summary>FG6-DEF-08：一支突袭部队的一次攻城结算（FGR-DEF-050）。</summary>
    [Serializable]
    public sealed class RaidResultRecord
    {
        public int Serial;
        public string PlanId = string.Empty;
        public string GroupId = string.Empty;
        public int Wave;
        public string Faction = string.Empty;
        public int Level;
        public string Trigger = string.Empty;
        public string TargetKind = string.Empty;
        public long WarnTick = -1;
        public long ArrivedTick = -1;
        public long UnfoldTick = -1;
        /// <summary>结束步（-1 = 进行中）。</summary>
        public long EndTick = -1;
        /// <summary>open / destroyed / withdrawn / core_lost。</summary>
        public string Outcome = "open";
        /// <summary>撤退原因（time / losses；全歼时为空）。</summary>
        public string RetreatReason = string.Empty;
        public int Unfolded;
        public int Killed;
        public int EliteKilled;
        public int Exited;
        /// <summary>展开那一刻家园战斗内核的累计读数（敌对伤害 / 己方伤害），结束时的差 = 这次造成 / 承受的伤害。</summary>
        public long BaseDealt;
        public long BaseTaken;
        public long Dealt;
        public long Taken;
        /// <summary>防御建筑与维修无人机的统计（DEBT-FG6DEF02-07 / FG6-DEF-03）：展开那一刻的累计读数（基准）与结束时的差额——护盾过载次数、陷阱铺设轮数、维修无人机修好的耐久与用掉的维修件。</summary>
        public int BaseOverloads;
        public long BaseLays;
        public double BaseRepaired;
        public int BaseKits;
        public int Overloads;
        public long Lays;
        public double Repaired;
        public int Kits;
        /// <summary>反应归因的突袭场次（结束时复制占比，场次被上限挤掉后结算仍完整）。</summary>
        public string ReactionSessionId = string.Empty;
        public double ReactionTotal;
        public ReactionShareRecord[] Reactions = Array.Empty<ReactionShareRecord>();
        public RaidTimelineRecord[] Timeline = Array.Empty<RaidTimelineRecord>();
        public int TimelineDropped;
        public RaidLossRecord[] Losses = Array.Empty<RaidLossRecord>();
        public int LossesDropped;
        /// <summary>损失合计（按种类：建筑 / 炮塔 / 防御建筑 / 机器 / 维修无人机），不受明细上限影响。</summary>
        public int LostBuildings;
        public int LostTurrets;
        public int LostDefenses;
        public int LostMachines;
        public int LostDrones;
        public RaidContribRecord[] Contrib = Array.Empty<RaidContribRecord>();
        /// <summary>击毁者不明（反应区域 / 场地 / 超出记录上限）的击毁数。</summary>
        public int OtherKills;
        public ItemAmountRecord[] Loot = Array.Empty<ItemAmountRecord>();
        /// <summary>这次留下的残骸堆（地面物 ID 与位置；合并用）。</summary>
        public RaidWreckPileRecord[] Piles = Array.Empty<RaidWreckPileRecord>();
        /// <summary>按敌人种类的击毁数（fg.TbRaidUnit.id）。</summary>
        public ItemAmountRecord[] KilledKinds = Array.Empty<ItemAmountRecord>();
    }

    /// <summary>FG6-DEF-08：时间线的一条（过程）。</summary>
    [Serializable]
    public sealed class RaidTimelineRecord
    {
        public long Tick;
        /// <summary>warn / arrive / unfold / first_loss / retreat_losses / retreat_time / end_destroyed / end_withdrawn / core_lost / choice_jump / choice_stay。</summary>
        public string Kind = string.Empty;
        public string Arg = string.Empty;
        public bool HasPos;
        public float X;
        public float Y;
    }

    /// <summary>FG6-DEF-08：一处损失。</summary>
    [Serializable]
    public sealed class RaidLossRecord
    {
        public long Tick;
        /// <summary>building / turret / defense / machine / drone。</summary>
        public string Kind = string.Empty;
        /// <summary>建筑 ID 或机器 LogicId（文字）。</summary>
        public string Id = string.Empty;
        /// <summary>损失那一刻的显示名（之后改名 / 拆掉也能看懂）。</summary>
        public string Name = string.Empty;
        public float X;
        public float Y;
    }

    /// <summary>FG6-DEF-08：一个击毁者的贡献。</summary>
    [Serializable]
    public sealed class RaidContribRecord
    {
        /// <summary>turret / machine / defense / drone。</summary>
        public string Kind = string.Empty;
        public string Id = string.Empty;
        public string Name = string.Empty;
        public int Kills;
        public int Elites;
    }

    /// <summary>FG6-DEF-08：一堆残骸（地面物）。</summary>
    [Serializable]
    public sealed class RaidWreckPileRecord
    {
        public string GroundItemId = string.Empty;
        public float X;
        public float Y;
    }
}
