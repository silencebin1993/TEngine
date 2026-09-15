using System;

namespace GameLogic.Command.Formation
{
    /// <summary>
    /// M4-03：教义 → 参数的不可变查表结果。三个轴对应里程碑验收原文的
    /// "目标选择、距离和撤退阈值"：<see cref="TargetPreference"/>/<see cref="EngagementRange"/>/
    /// <see cref="RetreatHealthThreshold"/>。
    ///
    /// **边界（D1，不是可选项）**：这里只是"定义 + 只读查询"，不接任何真实 AI 决策/内核 Job/
    /// 命令自动下达逻辑——同 M4-01（死亡信号未接线）、M4-02（完成条件由调用方触发）的既定技术债
    /// 模式。真正拿这些参数去驱动 AI 决策是后续故事（M4-04/M4-05 附近或更后）按需接线。
    ///
    /// 六个教义的参数是设计常量（先锋/猎手/护送/潜行/回收/坚守是产品定义好的固定教义，不是策划
    /// 要频繁调的运营数值），数值定义与依据见
    /// production/session-state/preflight-decisions.md「M4-03 六种教义」D2 表格。
    /// 不加持久化/序列化代码，不加运行时可调接口（D5）。
    /// </summary>
    public readonly struct FormationDoctrineProfile
    {
        public FormationTargetPreference TargetPreference { get; }

        /// <summary>交战距离，必须 &gt; 0。是本 story 新建的、只属于 Formation 层的独立距离定义，
        /// 不复用/不修改 <c>BehaviorArchetype</c>/<c>AiHandoffSystem.EngageThreatRange</c>/
        /// <c>SimWorld.RetreatThreatRange</c>/<c>SquadCommandSystem</c> 命令判定半径等既有概念
        /// （互不通用，语义各不相同）。</summary>
        public float EngagementRange { get; }

        /// <summary>撤退血量阈值（占比），必须 (0, 1]。</summary>
        public float RetreatHealthThreshold { get; }

        public FormationDoctrineProfile(
            FormationTargetPreference targetPreference,
            float engagementRange,
            float retreatHealthThreshold)
        {
            TargetPreference = targetPreference;
            EngagementRange = engagementRange;
            RetreatHealthThreshold = retreatHealthThreshold;
        }

        /// <summary>
        /// 教义查表入口。穷举 switch（每个 <see cref="FormationDoctrine"/> 值一个 case），
        /// 缺项（理论上不会出现，除非未来加了新教义枚举值却忘了在这里补 case）退化到
        /// <see cref="FormationDoctrine.None"/> 档，而不是抛异常——决策见 D3：
        /// 查表本身不应该让调用方（比如渲染/自检代码）因为一个未来加的新枚举值而崩溃，
        /// 但**不允许用 Dictionary 初始化器**，因为那样漏掉某个枚举值时会静默返回错误默认值
        /// 且编译期/运行期都不会有任何提示；穷举 switch 至少能在 code review 时被人看到
        /// "这个 case 没写"。
        /// </summary>
        public static FormationDoctrineProfile For(FormationDoctrine doctrine)
        {
            switch (doctrine)
            {
                case FormationDoctrine.None:
                    // 中性默认，呼应 CellGlobal.LowHealthPercent 0.3 基准。
                    return new FormationDoctrineProfile(FormationTargetPreference.Nearest, 4f, 0.3f);

                case FormationDoctrine.Vanguard:
                    // 先锋：专挑硬目标冲锋，死战到底。
                    return new FormationDoctrineProfile(FormationTargetPreference.Strongest, 10f, 0.15f);

                case FormationDoctrine.Hunter:
                    // 猎手：专挑落单弱者，主动出击但不硬拼。
                    return new FormationDoctrineProfile(FormationTargetPreference.Weakest, 8f, 0.3f);

                case FormationDoctrine.Escort:
                    // 护送：只打威胁到护送对象的敌人，谨慎优先保对象。
                    return new FormationDoctrineProfile(FormationTargetPreference.ProtectWard, 4f, 0.45f);

                case FormationDoctrine.Stealth:
                    // 潜行：尽量不交战，早早脱离。
                    return new FormationDoctrineProfile(FormationTargetPreference.AvoidCombat, 2f, 0.6f);

                case FormationDoctrine.Salvage:
                    // 回收：专注拾取，比潜行略敢靠近但仍以躲为主。
                    return new FormationDoctrineProfile(FormationTargetPreference.AvoidCombat, 3f, 0.55f);

                case FormationDoctrine.HoldGround:
                    // 坚守：站桩防御，不挑不追，守到底。
                    return new FormationDoctrineProfile(FormationTargetPreference.Nearest, 6f, 0.2f);

                default:
                    // 未来新增枚举值但忘了补 case 时，退化到 None 档（见方法注释，D3 决策）。
                    return new FormationDoctrineProfile(FormationTargetPreference.Nearest, 4f, 0.3f);
            }
        }
    }
}
