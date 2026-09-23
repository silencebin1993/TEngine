namespace GameLogic.Campaign
{
    /// <summary>ER4-MCH-01 STORY-EXECUTION-CARDS.md 第1条："生产、第一次工作、远征、接管、重伤、
    /// 精英击破、Boss参与、返航分别只记一次'首次'"——<see cref="MachineRecord.ExperienceFlags"/>
    /// 的封闭八项取值表。字符串常量（不是 enum）是刻意的：与 <see cref="MachineRecord.InjuryFlags"/>
    /// 同一既有存储形状（<c>string[]</c>），JsonUtility 原生支持，不需要额外的枚举↔字符串转换层。
    ///
    /// 写入唯一入口是 <see cref="MachineRegistry.TryMarkExperience"/>（幂等：已有则不重复追加）。
    /// 八项里目前只有三项在归还谷地场景有真实触发源（见各常量注释），其余五项依赖尚未实现的
    /// 远征/区域战斗/Boss 系统——按 `.claude/rules/projecta-spec-completeness.md` 第5条登记
    /// DEBT-ER4MCH01-01，不臆造假触发。</summary>
    public static class MachineExperienceFlags
    {
        /// <summary>首次在装配站生产完工（<see cref="Regions.HomeValleyFactory"/> Produce 队列完工，
        /// 不含 Retrofit——回厂改造不是"生产一台新机"）。归还谷地开局自带的 ERC-001/002 从未走生产
        /// 队列，天然不会获得这个标记，正确反映"它们不是被生产出来的"。真实触发源：
        /// <see cref="Regions.HomeValleyFactory.SpawnProducedMachine"/>。</summary>
        public const string Produced = "produced";

        /// <summary>首次完成任意一类工作单（Haul/Build/Repair/Salvage/Recharge，
        /// <see cref="Regions.WorkOrderKind"/>）。真实触发源：
        /// <see cref="Regions.HomeValleyWorkOrders"/> 各 CompleteXxx 完成分支。</summary>
        public const string FirstJob = "first_job";

        /// <summary>首次被玩家直控接管（WASD 亲自开）。真实触发源：
        /// <see cref="Regions.HomeValleyController"/> 的 EnsureDirectTarget/CycleControlTarget。</summary>
        public const string Controlled = "controlled";

        /// <summary>首次参与远征（依赖 ER5-REGION-01 远征系统，当前无真实触发源）。</summary>
        public const string Expedition = "expedition";

        /// <summary>首次重伤（依赖机器在区域战斗中被扣血到阈值以下，当前归还谷地无真实机器受伤
        /// 触发源——低威胁残骸靶是被机器攻击的对象，不是反过来伤机器）。</summary>
        public const string SeverelyInjured = "severely_injured";

        /// <summary>首次击破精英敌人（依赖 ER6/ER7 敌人内容与区域战斗，当前无真实触发源）。</summary>
        public const string EliteKill = "elite_kill";

        /// <summary>首次参与 Boss 战（依赖 ER7 Boss 内容，当前无真实触发源）。</summary>
        public const string BossParticipation = "boss_participation";

        /// <summary>首次远征后成功返航（依赖 ER5-REGION-01 远征系统，当前无真实触发源）。</summary>
        public const string Returned = "returned";

        /// <summary>全部封闭八项，供 UI 按固定顺序展示/自检断言"不超过八项"。</summary>
        public static readonly string[] All =
        {
            Produced, FirstJob, Controlled, Expedition, SeverelyInjured, EliteKill, BossParticipation, Returned,
        };

        public static string DisplayName(string flagId)
        {
            switch (flagId)
            {
                case Produced: return "生产";
                case FirstJob: return "首次工作";
                case Controlled: return "接管";
                case Expedition: return "远征";
                case SeverelyInjured: return "重伤";
                case EliteKill: return "精英击破";
                case BossParticipation: return "Boss参与";
                case Returned: return "返航";
                default: return flagId;
            }
        }
    }
}
