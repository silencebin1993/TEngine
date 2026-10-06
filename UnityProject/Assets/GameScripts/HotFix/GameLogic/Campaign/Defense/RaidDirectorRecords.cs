using System;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG6-DEF-04（FG06 FGR-DEF-020～024；第 6 章“突袭导演状态（下次突袭的时间、预算、阵营、随机种子）”）：突袭导演的存档域，挂在 <see cref="RaidState.Director"/>。
    /// 唯一写入口 <see cref="Defense.RaidDirectorService"/>。全部时间按统一时钟的步数（整数），随机抽取按（世界种子, 计划序号, 第几次抽取）派生，
    /// 不存随机数发生器的状态：存读档、观察 / 不观察、暂停与 0.5x～3x 下结果逐字段一致。
    /// 只加字段、不升域版本（ADR FG0-SAVE-01）：旧档没有 = 导演第一次推进时按当前暴露补好阈值状态（不追溯触发已经越过的阈值）。
    /// </summary>
    [Serializable]
    public sealed class RaidDirectorState
    {
        /// <summary>已经接上导演（新档开局 / 旧档第一次读进来时按当前暴露补好阈值状态）。</summary>
        public bool Initialized;
        public int NextPlanSerial = 1;
        public int NextWaveSerial = 1;
        /// <summary>进行中的突袭计划（还没结束：筹备 / 已排定 / 已预警 / 已出发）。结束的移进 <see cref="History"/>。</summary>
        public RaidPlanRecord[] Plans = Array.Empty<RaidPlanRecord>();
        /// <summary>突袭历史（最多 raid.history_max 条，新的在后）。结算明细由 FG6-DEF-08 追加。</summary>
        public RaidHistoryRecord[] History = Array.Empty<RaidHistoryRecord>();
        /// <summary>等下一次导演推进处理的触发（暴露越线可能发生在模拟步之外，先排队，下一个模拟步按顺序处理）。</summary>
        public RaidTriggerRecord[] Pending = Array.Empty<RaidTriggerRecord>();
        /// <summary>已触发的暴露阈值（位 0 = 30、1 = 60、2 = 90）；暴露降到阈值 − raid.threshold_rearm 以下清位，之后再越过会再次触发。</summary>
        public int ThresholdFired;
        /// <summary>暴露超过 90 时下一次 4 级突袭的触发步（-1 = 没有在计时）。</summary>
        public long Level4NextTick = -1;
        /// <summary>下一次可以骚扰的最早步（-1 = 还没开始计时）。</summary>
        public long HarassNextTick = -1;
        /// <summary>上一波普通突袭（受最短间隔约束的）的计划抵达步（-1 = 还没有）。</summary>
        public long LastArrivalTick = -1;
        /// <summary>上一波任何突袭的计划抵达步（骚扰的安静期、预算的间隔系数）。</summary>
        public long LastAnyArrivalTick = -1;
        /// <summary>已排定的普通突袭波数（第一次 = 0 时排定的那一波固定 1 级、规模很小）。</summary>
        public int RaidCount;
        /// <summary>后日谈已经发生的清洗次数（预算 = 基数 × 增长^n）。</summary>
        public int PostgameCount;
        /// <summary>8 个方位（从家园核心看，0 东、1 东北……7 东南）各被摧毁了几个据点：从那个方向出发的突袭最短间隔变长（FGR-DEF-023；与 <see cref="RaidPlanRecord.Octant"/> 同一参照）。</summary>
        public int[] DestroyedByOctant = new int[8];
        /// <summary>
        /// 后台长路线通道被突袭计划占用到哪一步（含；-1 = 空闲）：= 正在算的那条的采纳步。存档时会提前把结果取出来存进计划、通道随之空出，
        /// 但新的计划仍要等过了这一步才开始寻路——占用只取决于采纳步，存档与否逐步一致（复审 P2）。
        /// </summary>
        public long LaneBusyUntilTick = -1;
        /// <summary>已经触发过的剧情突袭（fg.TbRaidStory.id；每个只触发一次）。</summary>
        public string[] StoriesFired = Array.Empty<string>();
        /// <summary>统计：收到的触发次数、因难度（建造者只有剧情突袭）跳过的次数、合并进已有计划的次数。</summary>
        public int TriggersReceived;
        public int TriggersSkipped;
        public int TriggersMerged;

        // ── FG6-DEF-06（FGR-DEF-042“突袭到达时自动暂停（设置项，默认对前 3 次突袭开启）”）：只加字段、不升域版本 ──
        /// <summary>本存档已经到达过的突袭波数（同一波分几支先后到达只算一次）。旧档没有 = 从 0 起算（不追溯，旧档之后的前 3 次默认暂停）。</summary>
        public int ArrivedWaveCount;
        /// <summary>最近计过数的波次序号（最多 8 个，判断同一波的后到队伍不重复计数）。</summary>
        public int[] CountedWaves = Array.Empty<int>();

        // ── FG6-DEF-07（FGR-DEF-041“远征时家园遇袭：跳回家园 / 让信号留在远征队那边”）：只加字段、不升域版本 ──
        /// <summary>
        /// 玩家在远征中对每一波家园遇袭做的选择（最近 raid.away.decision_keep 条；读档后同一波不再弹出紧急通知）。
        /// 只由玩家点击写入（<see cref="Defense.HomeRaidAlertService"/>），不影响任何模拟。旧档没有 = 空。
        /// </summary>
        public RaidAwayDecisionRecord[] AwayDecisions = Array.Empty<RaidAwayDecisionRecord>();
    }

    /// <summary>FG6-DEF-07：远征中对一波家园遇袭的选择。</summary>
    [Serializable]
    public sealed class RaidAwayDecisionRecord
    {
        /// <summary>突袭波次序号（RaidPlanRecord.Wave；0 = 没有计划的突袭队伍，调试 / 旧档）。</summary>
        public int Wave;
        /// <summary>1 = 跳回家园，2 = 留在远征队（Defense.HomeRaidAlertService.ChoiceJumpHome / ChoiceStay）。</summary>
        public int Choice;
        /// <summary>做选择时的世界步。</summary>
        public long Tick;
        /// <summary>做选择时玩家所在的远征地点。</summary>
        public string SiteId = string.Empty;
    }

    /// <summary>FG6-DEF-04：一次等待处理的触发。</summary>
    [Serializable]
    public sealed class RaidTriggerRecord
    {
        /// <summary>fg.TbRaidTrigger.id。</summary>
        public string Kind = string.Empty;
        /// <summary>等级（暴露阈值 / 调用方给出；tier / harass 规则在处理时算）。-1 = 按规则算。</summary>
        public int Level = -1;
        /// <summary>指定的阵营（剧情突袭）；空 = 最受刺激的阵营。</summary>
        public string Faction = string.Empty;
        /// <summary>剧情突袭 ID（fg.TbRaidStory.id）；其它触发为空。</summary>
        public string StoryId = string.Empty;
        public long Tick;
    }

    /// <summary>
    /// FG6-DEF-04：一次突袭计划——从触发到部队出发、到达、撤退 / 被消灭。计划是导演的唯一真相：预警条、情报预报、地图箭头、叠加层的预测路线都读它；
    /// 出发后由行进队伍（<see cref="TransitGroupRecord.PlanId"/>）接着走，计划记下队伍 ID。
    /// </summary>
    [Serializable]
    public sealed class RaidPlanRecord
    {
        /// <summary>raid-序号（确定性，不用 GUID）；突袭预报的 Subject 用它，出发后沿用同一条（DEBT-FG5RND05-02）。</summary>
        public string PlanId = string.Empty;
        public int Serial;
        /// <summary>波次序号：合并的计划、同时到达的剧情与普通突袭同一波（预警条显示成一波）。</summary>
        public int Wave;
        /// <summary>0 筹备（预热途经区块、等寻路）/ 1 已排定 / 2 已预警（集结或等出发）/ 3 已出发 / 4 结束 / 5 已合并进别的计划 / 6 取消。</summary>
        public int State;
        /// <summary>主触发（fg.TbRaidTrigger.id）与合并进来的全部触发。</summary>
        public string Trigger = string.Empty;
        public string[] Triggers = Array.Empty<string>();
        /// <summary>不受最短间隔限制（剧情 / 静默夜 / 巨构阶段）。</summary>
        public bool Exempt;
        public string StoryId = string.Empty;
        public int Level;
        public int Budget;
        /// <summary>开局第一次突袭（固定 1 级、规模 × raid.first_raid_scale）。</summary>
        public bool FirstRaid;
        public string Faction = string.Empty;
        /// <summary>阵营内反制（fg.TbRaidCounter.id；空 = 没有）与被针对的固件类别。</summary>
        public string Counter = string.Empty;
        public string CounterCategory = string.Empty;
        /// <summary>目标：home = 归还核心；outpost = 前哨站（己方建筑群，FG8-OUT-01 之前按地图上的建筑群）。</summary>
        public string TargetKind = string.Empty;
        public string TargetId = string.Empty;
        public int TargetX;
        public int TargetY;
        /// <summary>出发地：outpost = 敌方据点；fog = 迷雾外（没有合适据点时，从阵营领地朝目标一侧来袭）。</summary>
        public string OriginKind = string.Empty;
        /// <summary>据点 ID（fog 时为阵营领地 ID）。</summary>
        public string OriginId = string.Empty;
        public int OriginX;
        public int OriginY;
        /// <summary>出发地相对家园核心的方位（0 东……7 东南；与 <see cref="RaidDirectorState.DestroyedByOctant"/> 同一参照，目标是前哨站时也不变）。</summary>
        public int Octant;
        /// <summary>这个方向被摧毁的据点让最短间隔放大的倍率（≥ 1）。</summary>
        public float IntervalMul = 1f;
        /// <summary>编成：fg.TbRaidUnit.id、各自数量、其中精英数量（平行数组）。</summary>
        public string[] UnitIds = Array.Empty<string>();
        public int[] UnitCounts = Array.Empty<int>();
        public int[] EliteCounts = Array.Empty<int>();
        public int UnitTotal;
        public long CreatedTick;
        /// <summary>最早请求寻路的步（排定当步；出发前改道时为改道那一步）。</summary>
        public long RouteRequestTick;
        public int NavSerial;
        /// <summary>0 需要路线 / 1 等寻路结果 / 2 路线就绪 / 3 寻路失败（按直线估算）。</summary>
        public int RouteState;
        public int NavReason;
        /// <summary>后台长路线通道的采纳步（请求步 + raid.route_latency_seconds；DEBT-FG0ARCH06-09：固定的较晚时刻取结果，冷启动早已算完，不硬等）。</summary>
        public long RouteAdoptTick = -1;
        /// <summary>存档那一刻后台通道已经替这个计划算好、还没到采纳步的结果（读档后在同一采纳步交到，与不存档一致）。</summary>
        public bool PendingRoute;
        public int PendingStatus;
        public int PendingReason;
        public int[] PendingX = Array.Empty<int>();
        public int[] PendingY = Array.Empty<int>();
        /// <summary>沿地形的路线（格坐标路点，不含出发格）。</summary>
        public int[] RouteX = Array.Empty<int>();
        public int[] RouteY = Array.Empty<int>();
        /// <summary>路线就绪的步、行进要的步数、预计进入到达半径的那一点。</summary>
        public long RouteReadyTick = -1;
        public long TravelTicks;
        public float ArriveX;
        public float ArriveY;
        /// <summary>发预警的步、部队出发的步（路程不够最短预警时晚于预警：在出发地集结）、计划抵达的步。</summary>
        public long WarnTick = -1;
        public long DepartTick = -1;
        public long ArrivalTick = -1;
        /// <summary>出发后的行进队伍 ID。</summary>
        public string GroupId = string.Empty;
        /// <summary>队伍到达的步（-1 = 还没到）。</summary>
        public long ArrivedTick = -1;
        /// <summary>合并进的计划 ID（State = 5）。</summary>
        public string MergedInto = string.Empty;
        /// <summary>结束原因：withdrawn / destroyed / merged / cancelled / no_route ……（文本键 raid.end.*）。</summary>
        public string EndReason = string.Empty;
        /// <summary>出发前出发地变了（据点被毁）：改道次数。</summary>
        public int Reroutes;
        /// <summary>后日谈第几次清洗（-1 = 不是后日谈的突袭）。</summary>
        public int PostgameIndex = -1;
        /// <summary>排定时用的“上一波普通突袭抵达步”（改道重新排定时沿用它，不把自己算成上一波）。</summary>
        public long IntervalBaseTick = -1;
        /// <summary>
        /// 预报相关内容（等级、编成、出发地、路线与抵达时刻）的变更序号：排定、升级重新编成、合并吸收、出发前改道、重叠对齐时 +1。
        /// 突袭预报记下产出时的序号（<see cref="IntelRecord.PlanRevision"/>），对不上 = 计划有变、预报已过时，监听站重新破译（FGT-DEF-004“与实际突袭一致”）。
        /// </summary>
        public int Revision;
        /// <summary>
        /// FG6-DEF-09：重叠合并时吸收进来的普通计划的预算（已含在 <see cref="Budget"/> 里）与计划 ID。中途改成“只有剧情突袭”（建造者）时剧情计划去掉这部分、
        /// 归还第一次突袭名额、被吸收的计划改记“取消”（不再算最短间隔的参照）；其他难度修改按规模比例与 <see cref="Budget"/> 一起缩放（复审 P1：吸收的预算不丢）。旧档没有 = 0 / 空。
        /// </summary>
        public int AbsorbedBudget;
        public string[] AbsorbedIds = Array.Empty<string>();
    }

    /// <summary>FG6-DEF-04：突袭历史的一条（FG06 第 4 节“突袭历史”；结算明细、贡献最大者由 FG6-DEF-08 追加字段）。</summary>
    [Serializable]
    public sealed class RaidHistoryRecord
    {
        public string PlanId = string.Empty;
        public int Wave;
        public string Trigger = string.Empty;
        public string Faction = string.Empty;
        public int Level;
        public int Units;
        public string TargetKind = string.Empty;
        public string OriginKind = string.Empty;
        public long WarnTick = -1;
        public long ArrivedTick = -1;
        public long EndTick;
        public string EndReason = string.Empty;
        /// <summary>FG6-DEF-09：排定的抵达步（-1 = 没排定；旧档没有）。中途改难度取消计划后，最短间隔的参照按它重算（已并波 / 途中被全歼的普通突袭也算，复审 P2）。</summary>
        public long ArrivalTick = -1;
    }
}
