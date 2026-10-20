using System;
using System.Collections.Generic;

namespace GameLogic.Campaign
{
    // FG0-SAVE-01（FGR-ARC-008）：存档 v2 新增的状态域。
    //
    // 这里只放"骨架"：每个域一个独立的可序列化类型、自带 DomainVersion，挂在 CampaignState 上并随存档往返。
    // 域里的业务字段由表中"承接 Story"各自加（加字段不需要升 SchemaVersion——JsonUtility 对缺失字段补默认值；
    // 改名、拆分、语义变化才需要写 CampaignSaveMigrations 里的逐级升级器）。
    //
    // 纪律：
    // - 每个域的唯一写入口是承接 Story 建立的系统；本 Story 之外不要直接改这些字段。
    // - 新建战役一律经 CampaignState.CreateNew；读档后缺失的域由 CampaignState.NormalizeForSave /
    //   CampaignFgStateDomains.EnsureAll 补成空域，保证任何代码拿到的都不是 null。
    // - 世界种子 / 生成器版本 / 区块差异（FGR-GEN-060/061）在 WorldGenState；只保存被修改过的区块。

    /// <summary>世界种子、生成器版本与各表面的区块差异（FG0-ARCH-05 / FG17 填写）。</summary>
    [Serializable]
    public sealed class WorldGenState
    {
        public int DomainVersion = 1;
        /// <summary>世界种子。新建战役时等于 <see cref="CampaignState.RandomSeed"/>；玩法随机流由它派生（FGR-ARC-010）。</summary>
        public int WorldSeed;
        /// <summary>生成器版本。0 = 尚未启用程序生成（沿用 Demo 的固定地图）；FG0-ARCH-05 起写入真实版本号，
        /// 读档时未修改的区块用这里记录的版本重新生成（FGR-GEN-061）。</summary>
        public int GeneratorVersion;
        /// <summary>世界设置（地图大小、资源丰度等）的预设 ID；FG0-ARCH-05 定义取值。</summary>
        public string WorldSettingsId = "default";
        /// <summary>被修改过的区块差异（FGR-GEN-060）。未修改的区块不进存档。</summary>
        public ChunkDiffRecord[] ChunkDiffs = Array.Empty<ChunkDiffRecord>();
        /// <summary>FG3-GEN-01：家园区的侦察巢已经登记成据点（新战役一次，幂等；之后据点状态在 RaidState 里）。</summary>
        public bool HomeOutpostsSeeded;
        /// <summary>FG3-GEN-01（FG17 第 4、6 节）：玩家在战略地图上加的标记与备注。唯一写入口 <c>WorldGen.WorldMapMarkers</c>。</summary>
        public MapMarkerRecord[] MapMarkers = Array.Empty<MapMarkerRecord>();
        public int NextMapMarkerSerial = 1;
        /// <summary>FG8-GEN-02：外延区以内的阵营领地主力据点与领地之间的零散前哨已经登记成据点（新战役一次，幂等；外延区的按揭开范围按需登记）。</summary>
        public bool TerritoryOutpostsSeeded;
        /// <summary>FG8-GEN-02（FGR-GEN-090）：手工锚点从家园出发的可达性校验结果（开局用层级寻路校验一次，之后只读；规划层本身不存档）。
        /// 唯一写入口 <c>WorldGen.TerritoryAnchorChecks</c>。</summary>
        public AnchorCheckRecord[] AnchorChecks = Array.Empty<AnchorCheckRecord>();
        /// <summary>FG8-GEN-02：存档时镜头正看着的室内场地（表面 ID；不在室内为空串）。只决定读档后镜头放在哪里，不影响模拟。</summary>
        public string InteriorFocusId = string.Empty;
    }

    /// <summary>FG8-GEN-02：一个手工锚点的可达性校验结果。</summary>
    [Serializable]
    public sealed class AnchorCheckRecord
    {
        public string AnchorId = string.Empty;
        /// <summary>0 = 待校验；1 = 可达（入口就在规划的位置）；2 = 原定入口走不到，已挪到平整范围里最近的可达点；3 = 从核心出发一条路线都没有（写日志，入口照规划位置显示并说明）。</summary>
        public int State;
        /// <summary>校验后实际使用的入口（格）。</summary>
        public int X;
        public int Y;
        /// <summary>从核心到入口的路线长度（格）。</summary>
        public float RouteLength;
        /// <summary>发过几次寻路请求（含没交回结果的重发）与最近一次的请求序号。</summary>
        public int Attempts;
        public int Serial;
        public string Note = string.Empty;
    }

    /// <summary>FG3-GEN-01：战略地图上的一个玩家标记（表面 + 格网坐标 + 备注）。</summary>
    [Serializable]
    public sealed class MapMarkerRecord
    {
        public string MarkerId;
        public string SurfaceId;
        public int CellX;
        public int CellY;
        /// <summary>序号（显示“标记 N”）。</summary>
        public int Serial;
        public string Note = string.Empty;
    }

    /// <summary>一个被玩家或事件修改过的区块。<see cref="DiffPayload"/> 的编码由 FG0-ARCH-05 定义。</summary>
    [Serializable]
    public sealed class ChunkDiffRecord
    {
        public string SurfaceId;
        public int ChunkX;
        public int ChunkY;
        public string DiffPayload;
    }

    /// <summary>战役进度：幕、后日谈 / 沙盒标记（存档卡 FGR-SYS-007 显示）。幕推进由 FG11 叙事 Story 写入，
    /// 沙盒由 FG12 写入。0.2 起步内容整体相当于第一幕（GDD 0.2 §幕结构），新战役 Act = 1。</summary>
    [Serializable]
    public sealed class CampaignProgressState
    {
        public int DomainVersion = 1;
        public int Act = 1;
        public bool IsPostgame;
        public bool IsSandbox;
        /// <summary>FG9-NAR-01（FGR-NAR-003 / FGR-UX-051“资料库按存档保存”）：本存档已收进资料库的条目（收到的先后、幕、日、已读）。
        /// 唯一写入口 <c>GameLogic.Narrative.NarrativeService</c>。新存档为空——不会看到其他存档的剧情。</summary>
        public NarrativeEntryRecord[] Narrative = Array.Empty<NarrativeEntryRecord>();
        /// <summary>FG9-NAR-01（FG11 第 9 节“揭示演出中退出游戏 → 读档后回到演出前的自动存档”）：已触发、还没看完 / 跳过的首次演出（空 = 没有）。
        /// 演出开始前的自动存档里它不为空，读档后演出重新播放；看完或跳过才收进资料库并清空。</summary>
        public string PendingCutscene = string.Empty;
        /// <summary>下一条资料库记录的序号（收到的先后，单调递增）。</summary>
        public int NarrativeSerial = 1;
    }

    /// <summary>FG9-NAR-01：资料库里的一条记录（条目内容在 fg.TbNarrativeEntry，存档只记“收到了哪条、何时、在哪一幕、读过没有”）。</summary>
    [Serializable]
    public sealed class NarrativeEntryRecord
    {
        public string Id;
        /// <summary>收到的先后（剧情回顾按它排列）。</summary>
        public int Serial;
        /// <summary>收进资料库时归到第几幕（条目写明幕的按条目，0 = 收到时战役所在的幕）。</summary>
        public int Act;
        /// <summary>收到时的统一时钟步数与游戏日。</summary>
        public long Tick;
        public int Day;
        public bool Read;
    }

    /// <summary>统一游戏时钟（FGR-ARC-009 / FGR-ENV-001）。唯一写入口 <see cref="Core.GameClock"/>（FG0-ARCH-01 起每个模拟步写一次）。
    /// <see cref="Day"/> = 0 表示时钟尚未接入（FG0-ARCH-01 之前的存档），存档卡不显示"第几日"；读档时由 GameClock 补算。</summary>
    [Serializable]
    public sealed class GameClockState
    {
        public int DomainVersion = 1;
        /// <summary>已模拟的游戏秒数（= <see cref="Ticks"/> / <see cref="StepHz"/>；1x 下与真实秒数相同）。</summary>
        public double GameSeconds;
        /// <summary>第几个游戏日（从 1 起；由 GameSeconds 与 clock.day_seconds / clock.start_hour 推导，存下来供存档卡直接显示）。</summary>
        public int Day;
        /// <summary>FG0-ARCH-01：已执行的固定模拟步数（确定性回放的时间轴）。</summary>
        public long Ticks;
        /// <summary>FG0-ARCH-01：写入 <see cref="Ticks"/> 时的步长频率；读档时与当前 clock.sim_step_hz 不同则按 GameSeconds 换算。</summary>
        public int StepHz;
    }

    /// <summary>格网建造（FG0-ARCH-04 / FG03）。唯一写入口 <see cref="Grid.HomeGridService"/>。
    /// 占用层不进存档：它由 <see cref="CampaignState.BuildingRecords"/>（枢轴格 + 朝向）唯一推导，读档后重建，
    /// 所以存读档不可能出现“记录与占用不一致”。地形 / 污染层由（种子, 地形来源）确定性生成，只有被修改过的区块才进
    /// <see cref="WorldGenState.ChunkDiffs"/>（FGR-GEN-060，编码由 FG0-ARCH-05 定义；本 Story 没有修改地形的玩法）。</summary>
    [Serializable]
    public sealed class GridState
    {
        public int DomainVersion = 1;
        /// <summary>开局布局 / 旧档迁移的版本。0 = 这份存档里的建筑还没有格网字段（FG0-ARCH-04 之前的 v2 存档），
        /// 读档时按 Position 迁移后写成 <see cref="Grid.HomeGridService.LayoutVersion"/>。</summary>
        public int LayoutVersion;
        /// <summary>归还核心的枢轴格（开局布局以它为锚点，FG00 B25）。世界生成（FG3-GEN-01）落地前恒为原点。</summary>
        public int CorePivotX;
        public int CorePivotY;
        /// <summary>地形来源标识（<see cref="Grid.IGridTerrainSource.SourceId"/>），读档时用同一来源重建地形层。</summary>
        public string TerrainSourceId = string.Empty;
        /// <summary>同类建筑第 2 座起的实例序号（BuildingId = home_valley:&lt;type&gt;#&lt;n&gt;），单调递增，读档后继续往后编。</summary>
        public int NextInstanceSerial = 2;
        /// <summary>迷雾层：已探索的圆形区域（FGR-LOG-013）。开局 = 核心周围 grid.explored_radius_start 格；
        /// 信号塔覆盖、机器探索扩张由 FG3-LOG-01 / FG1-SIG-07 追加。</summary>
        public ExploredAreaRecord[] Explored = Array.Empty<ExploredAreaRecord>();
        /// <summary>FG3-LOG-01（FGR-LOG-002 快捷栏）：10 个快捷栏格子里放的条目 ID（建筑类型 ID 或建造菜单工具 ID，空串 = 空格子）。
        /// 跟着存档走（每局解锁的东西不同）；旧存档没有这个字段时为 null，读取时按全空处理（<see cref="Grid.BuildCatalog"/>）。</summary>
        public string[] Hotbar;
        /// <summary>FG3-LOG-02（FGR-LOG-006；DEBT-FG3LOG01-01）：规划中的传送带（虚影）。一次拖拽 = 一份规划 + 一张施工单；
        /// 机器取料施工，按路径顺序一格一格建成、进传送带内核。旧存档没有这个字段时为 null（= 没有规划）。唯一写入口 <see cref="Regions.HomeValleyConstruction"/>。</summary>
        public PlannedBeltRecord[] PlannedBelts;
        /// <summary>传送带规划的编号（单调递增，读档后继续往后编）。</summary>
        public int NextBeltPlanSerial = 1;
        /// <summary>FG3-LOG-07（FGR-LOG-009 撤销和重做）：撤销栈（旧的在前）。同一步的操作 <see cref="PlanOpRecord.Step"/> 相同，一次撤销整步。
        /// 保留 plan.undo_depth（50）步（更早的丢掉）；单步快照超过 plan.layout_max_entries 件的操作不进栈，存档体积有上界。
        /// 旧存档没有这个字段时为 null（= 空栈）。唯一写入口 <see cref="Grid.PlanHistory"/>。</summary>
        public List<PlanOpRecord> PlanUndo;
        /// <summary>重做栈（最近撤销的在后）。任何新的规划操作都会清空它。</summary>
        public List<PlanOpRecord> PlanRedo;
        /// <summary>下一步的编号（单调递增）。</summary>
        public int NextPlanStep = 1;
        /// <summary>FG3-LOG-08（FGR-LOG-080；FGU-12）：当前打开的叠加层（<see cref="GameLogic.View.OverlayKind"/>，0 = 不显示）。跟着存档走（和快捷栏一样），
        /// 读档后按存档恢复。旧存档没有这个字段时为 0。唯一写入口 <see cref="GameLogic.View.OverlayService"/>。</summary>
        public int OverlayActive;
        /// <summary>FG3-LOG-08：最近一次打开的叠加层（叠加层切换键 O 关着时按它重新打开）。0 = 从没打开过（按“信号覆盖”，与 FG1-SIG-07 的 O 键一致）。</summary>
        public int OverlayLast;
        /// <summary>FG7-ENV-05（FGR-ENV-050 / 051 / 060）：家园探索——发现过 / 回收过的遗迹点、揭开过的危害。唯一写入口 <see cref="Env.HomeExplorationService"/>。
        /// 只加字段、不升域版本：旧档没有 = 空域，第一次揭开新区域前按已探索区域静默补登（不补发通知）。</summary>
        public HomeExplorationState Exploration;
        /// <summary>FG8-EXP-01（FGR-EXP-001 / FGU-31）：远征队——名字、颜色、上次派出的名单。唯一写入口 <see cref="Regions.ExpeditionTeamService"/>。
        /// 只加字段、不升域版本：旧档没有 = 空队（名字空串＝显示默认名，颜色 0，名单空）。</summary>
        public ExpeditionTeamState ExpeditionTeam;
        /// <summary>FG8-DROP-01（FGR-EXP-013～016）：投送站与投送舱（站里的油、在站 / 冷却 / 在飞的舱、载荷、航线）。唯一写入口 <see cref="Regions.DropPodService"/>。
        /// 只加字段、不升域版本：旧档没有 = 空域（没有投送站）。</summary>
        public DropPodState Drop;
        /// <summary>FG8-OUT-01（FGR-EXP-017～019）：己方前哨站（离核心 300 格以外的建筑群自动归为前哨站）——编号、名字、上次的位置与座数、全毁通知标记。
        /// 唯一写入口 <see cref="WorldSim.OwnOutpostService"/>。只加字段、不升域版本：旧档没有 = 空域，第一次对账时按现有建筑群静默补登（不补发“建立前哨站”通知）。</summary>
        public OwnOutpostState OwnOutposts;
    }

    /// <summary>FG8-OUT-01：己方前哨站的存档域（见 <see cref="GridState.OwnOutposts"/>）。前哨站本身由建筑群推导（不设第二个核心），这里只存需要跨读档稳定的东西。</summary>
    [Serializable]
    public sealed class OwnOutpostState
    {
        public OwnOutpostRecord[] Outposts = Array.Empty<OwnOutpostRecord>();
        /// <summary>下一个前哨站编号（单调递增，默认名“前哨站 N”用它；读档后接着往后编）。</summary>
        public int NextSerial = 1;
        /// <summary>已经补登过（旧档第一次对账静默补登，之后新出现的建筑群才发“建立前哨站”通知）。</summary>
        public bool Seeded;
        // ── 统计（总览与离家报告读）──
        public int Founded;
        public int WipedOut;
        public int PacksDelivered;
        public int PackUnitsPoured;
        public int BeltUnitsPoured;
    }

    /// <summary>FG8-OUT-01：一个己方前哨站。</summary>
    [Serializable]
    public sealed class OwnOutpostRecord
    {
        /// <summary>稳定编号（"op-N"），机器的“前哨劳动”岗位、离家报告的分组、突袭目标都按它认。</summary>
        public string OutpostId = string.Empty;
        public int Serial;
        /// <summary>玩家起的名字（空 = 默认名“前哨站 N”）。唯一写入口 <see cref="WorldSim.OwnOutpostService.TryRename"/>。</summary>
        public string CustomName = string.Empty;
        /// <summary>认领建筑群用的锚点建筑（群里 ID 最小的那座；被拆后按上次位置就近重新认领并换锚点）。</summary>
        public string AnchorBuildingId = string.Empty;
        /// <summary>上一次对账时建筑群的平均位置（星球格）与座数 / 完好座数。</summary>
        public float X;
        public float Y;
        public int Count;
        public int Intact;
        public long FoundedTick;
        /// <summary>全部建筑都已被摧毁（废墟）时只通知一次；有建筑修好 / 重建后清掉，下次全毁再通知。</summary>
        public bool WipedNotified;
    }

    /// <summary>FG8-DROP-01：投送站与投送舱的存档域（见 <see cref="GridState.Drop"/>）。全部字段进存档；航线只按步序号推进，读档后接着飞。</summary>
    [Serializable]
    public sealed class DropPodState
    {
        public DropStationRecord[] Stations = Array.Empty<DropStationRecord>();
        /// <summary>全部投送舱（在站、冷却、在飞都在这里；舱归属 <see cref="DropPodRecord.StationId"/>）。</summary>
        public DropPodRecord[] Pods = Array.Empty<DropPodRecord>();
        public int NextPodSerial = 1;
        /// <summary>发射批次编号（一次“发射 / 呼叫回收”= 一批，通知与面板按批合并）。</summary>
        public int NextGroupSerial = 1;
        /// <summary>已探明的敌方防空（据点 ID）：瞄准时（选点悬停 / 发射被拒）或投送舱在落点被防空逼回时探明，之后即使据点格还在迷雾里，战略地图也画出它的红色区域（拒绝与红区同一口径，FGR-EXP-014 / B06）。</summary>
        public string[] KnownAntiAir = Array.Empty<string>();
        // ── 统计（面板与离家报告读）──
        public int Launches;
        public int Recalls;
        public int Delivered;
        public int Recovered;
        public int Diverted;
        public int Aborted;
    }

    /// <summary>FG8-DROP-01：一座投送站（燃油缓存、自动补满开关、统计）。</summary>
    [Serializable]
    public sealed class DropStationRecord
    {
        public string BuildingId = string.Empty;
        /// <summary>没有接上管线消费者时留在站里的燃油（毫升）；接着的那部分在管线内核的消费者缓存里（随管线快照存档）。</summary>
        public long HeldFuelMl;
        /// <summary>管线内核里的燃油消费者（0 = 没接上管线）。</summary>
        public int ConsumerId;
        /// <summary>仓库里有投送舱时自动装进站里补满（玩家可在投送站面板关掉；FGR-BASE-020 来源可追溯）。</summary>
        public bool AutoFill = true;
        public int Launches;
        /// <summary>站的中心（星球格；对账时更新）。建筑记录被拆掉后，退回的舱放不下时落在这里。</summary>
        public float X;
        public float Y;
    }

    /// <summary>FG8-DROP-01：一个投送舱。位置按（起点、终点、出发步、到达步）线性插值，不逐帧写坐标。</summary>
    [Serializable]
    public sealed class DropPodRecord
    {
        public int Serial;
        /// <summary>归属的投送站（建筑 ID；改降后改成新的那一座）。</summary>
        public string StationId = string.Empty;
        /// <summary>0 在站待命；1 冷却中（在站）；2 去程（飞往落点 / 回收点）；3 返程（飞回投送站）。</summary>
        public int State;
        /// <summary>0 无；1 投送；2 回收。</summary>
        public int Mission;
        public float FromX;
        public float FromY;
        public float ToX;
        public float ToY;
        public long StartTick;
        /// <summary>这一程着陆完成的步（飞行 + 着陆）。</summary>
        public long ArriveTick;
        public long CooldownUntilTick;
        /// <summary>任务落点 / 回收点（去程的终点；返程时保留，面板写“从哪里回来”）。</summary>
        public float TargetX;
        public float TargetY;
        /// <summary>舱里的机器（0 = 没有）。在舱里时机器记录的区域是 <see cref="Regions.DropPodService.InPodRegionId"/>。</summary>
        public int MachineId;
        public string CargoItem = string.Empty;
        public int CargoAmount;
        /// <summary>回收：呼叫时指定要接的机器（0 = 接地面上的货物）。</summary>
        public int RecallMachineId;
        /// <summary>这次任务已付的燃油（毫升，往返）。</summary>
        public long FuelMl;
        /// <summary>批次（同一次发射 / 回收）。</summary>
        public int Group;
        /// <summary>最近一次改降 / 中止的原因码（空 = 没有）。</summary>
        public string Note = string.Empty;
        /// <summary>FG8-OUT-01（FGR-EXP-018“投送舱送施工包”）：这一舱货物是施工包——着陆时先倒进落点附近还缺这种材料的施工现场，剩下的才落在地上。只加字段（旧档 = false = 普通货物）。</summary>
        public bool ConstructionPack;
    }

    /// <summary>FG8-EXP-01：远征队（见 <see cref="GridState.ExpeditionTeam"/>）。名单只记“上次派出 / 编成的机器 LogicId”，不是运行中的远征状态。</summary>
    [Serializable]
    public sealed class ExpeditionTeamState
    {
        /// <summary>远征队名字（经 <see cref="MachineNaming.Validate"/> 同一套规则；空串 = 默认名）。</summary>
        public string Name = string.Empty;
        /// <summary>远征队颜色下标（0 起，见 <see cref="Regions.ExpeditionTeamService.ColorCount"/>）。</summary>
        public int ColorIndex;
        /// <summary>上次编成 / 派出的机器 LogicId（去重；没有固定上限，派遣人数只受带宽约束）。</summary>
        public int[] Members = Array.Empty<int>();
        /// <summary>FG8-EXP-02：这支远征队此刻在外的行军（<see cref="ExpeditionMarchState.Phase"/> = 0 表示没有远征在外）。唯一写入口 <see cref="Regions.ExpeditionMarchService"/>。
        /// 只加字段、不升域版本：旧档没有 = 0 = 待命（读档时正在远征地点里的旧档由 <see cref="Regions.ExpeditionMarchService.Reconcile"/> 补成“在地点”）。</summary>
        public ExpeditionMarchState March = new ExpeditionMarchState();
        /// <summary>FG8-EXP-02 第 0 批（FG8-GEN-02“远征地点迁入星球格网”的最小部分前移）：两个远征地点在星球表面上的锚点——
        /// 按世界种子确定、经层级寻路校验从家园可达。唯一写入口 <see cref="Regions.ExpeditionSiteAnchors"/>。只加字段：旧档没有 = 读档后重新解析。</summary>
        public ExpeditionSiteAnchorRecord[] Anchors = Array.Empty<ExpeditionSiteAnchorRecord>();
    }

    /// <summary>
    /// FG8-EXP-02（FGR-EXP-010 行军、FGR-EXP-011 远程指挥与覆盖、FGR-EXP-012 远征队状态）：远征队在星球表面上的运行记录。
    /// 成员在行军中仍是家园所在星球表面上的真实机器（家园战斗内核里的单位：真实寻路、真实交战、能被接入），这里只记“这是一支远征队”与它的命令 / 提醒去重；
    /// 位置、路线、血量都在内核与机器记录里，随内核快照进存档。全部字段进存档。
    /// </summary>
    [Serializable]
    public sealed class ExpeditionMarchState
    {
        /// <summary>0 = 没有远征在外；1 = 去程（星球上，往目的地锚点走）；2 = 在远征地点里（地点已载入）；3 = 返程（星球上，往家园走）；4 = 已返回（记录保留到下一次出发，显示“已返回”）。</summary>
        public int Phase;
        /// <summary>目的地地点（<see cref="Regions.FracturedCityLayout.RegionId"/> / <see cref="Regions.FoundryOutpostLayout.RegionId"/>）。</summary>
        public string SiteId = string.Empty;
        /// <summary>出发时的名单（LogicId 升序；阵亡的留在名单里，只按存活的算）。</summary>
        public int[] Members = Array.Empty<int>();
        /// <summary>行军速度（格/秒，出发时取名单里最慢的那台；初值见 <see cref="Regions.ExpeditionMarchService.ChassisSpeed"/>）。</summary>
        public float Speed;
        /// <summary>最后一条远征命令（<see cref="Regions.ExpeditionMarchService.Command"/>：0 前进 / 1 待命 / 2 返回）。覆盖外只执行它。</summary>
        public int Command;
        /// <summary>出发那一刻有成员还没进家园战斗内核（刚出厂）：下一步补下命令。</summary>
        public bool OrdersPending;
        /// <summary>出发的世界步、当前阶段开始的世界步（耗时、提示用）。</summary>
        public long DepartTick;
        public long PhaseTick;
        /// <summary>整队是否在与核心连通的覆盖里（全部存活成员都在 = 真；按内核每 0.5 秒的评估）。用于进出覆盖的提醒去重。</summary>
        public bool InCoverage = true;
        /// <summary>“快走出覆盖”的提醒已发（余量回升后复位）。</summary>
        public bool EdgeWarned;
        /// <summary>有成员身边有敌对单位（交战中）。每秒刷新。</summary>
        public bool Engaged;
        /// <summary>停下的原因：0 没停；1 路线被截断（寻路只到最近可达点）；2 到达但没能进入远征地点（已回滚；队伍照最后的“前进”在集结点等候，到 <see cref="ArrivalRetryTick"/> 自动重试）。</summary>
        public int Halted;
        /// <summary>上一次到达失败的原因码（<see cref="Regions.ExpeditionTextKeys.ReasonCodes"/>；同一原因重试再失败不重复提示；进入成功 / 玩家改命令时清空）。</summary>
        public string ArrivalFailReason = string.Empty;
        /// <summary>到达失败后按最后的“前进”自动重试进入的世界步（只看步序号，与观察无关；FG00 B11 不软锁）。</summary>
        public long ArrivalRetryTick;
        /// <summary>当前所在危害带的领地 ID（没有 = 空串；进入时提醒一次，离开后复位）。</summary>
        public string HazardZoneId = string.Empty;
        /// <summary>FG8-GEN-02：队伍中心当前所在的阵营领地 ID（不在领地里 = 空串；进入时提醒一次“进入 ××”，离开后复位）。</summary>
        public string TerritoryId = string.Empty;
        /// <summary>途中已提醒过的敌方据点 / 巡逻 ID（每个只提醒一次，读档后不重复）。</summary>
        public string[] EncounteredIds = Array.Empty<string>();
        /// <summary>FG8-EXP-05（FGR-EXP-031 / 050“走回家园或前哨站”）：返程的目的地——下“返回家园”或从远征地点撤离那一刻离队伍最近的家园 / 带仓库的前哨站（核心或仓库的位置）。
        /// 没有（旧档）= 归还核心。那座仓库途中被毁时改去下一处最近的。</summary>
        public bool HasReturnGoal;
        public float ReturnX;
        public float ReturnY;
        /// <summary>FG8-EXP-05：这一趟去过远征地点（从地点撤离后走回家园）。</summary>
        public bool VisitedSite;
    }

    /// <summary>
    /// FG8-EXP-02 第 0 批（FG17 FGR-GEN-024 放置规则的最小部分）：一个远征地点在星球表面上的锚点。
    /// 候选点只由（世界种子, 地点, 第几次试探）和规划层的领地决定；可达性用层级寻路（从归还核心出发、允许部分路线）校验：
    /// 能走到就取寻路给出的实际终点；走不到时取最近可达点（仍在领地内才算合格），换下一个候选；全部不合格时取离领地最近的那个可达点并记日志（FGR-GEN-090）。
    /// </summary>
    [Serializable]
    public sealed class ExpeditionSiteAnchorRecord
    {
        public string SiteId = string.Empty;
        /// <summary>所在领地（规划层 <see cref="WorldGen.PlannedTerritory.Id"/>；没有规划层的旧原型世界为空串）。</summary>
        public string TerritoryId = string.Empty;
        /// <summary>0 = 待定（正在寻路校验）；1 = 已定；2 = 失败（从核心出发一条路线都没有）。</summary>
        public int State;
        /// <summary>第几次试探（0 起）与发过的请求序号（后台通道当场取回）。</summary>
        public int Attempt;
        public int Serial;
        /// <summary>当前候选点（格）。</summary>
        public int CandidateX;
        public int CandidateY;
        /// <summary>锚点（格；State = 1 时有效）。</summary>
        public int X;
        public int Y;
        /// <summary>锚点被挪到了最近可达点（候选点本身走不到）。</summary>
        public bool Moved;
        /// <summary>所有候选都不在领地内可达，取了离领地最近的可达点（已记日志）。</summary>
        public bool Fallback;
        /// <summary>从归还核心到锚点的路线（格，层级寻路给出的路点；检查单“路线走出覆盖”、预计时间都按它算）。</summary>
        public int[] RouteX = Array.Empty<int>();
        public int[] RouteY = Array.Empty<int>();
        /// <summary>路线长度（格）。</summary>
        public float RouteLength;
        /// <summary>试探过程中离领地最近的可达点（兜底用）。</summary>
        public bool HasBest;
        public int BestX;
        public int BestY;
        public float BestGap = float.MaxValue;
        public int[] BestRouteX = Array.Empty<int>();
        public int[] BestRouteY = Array.Empty<int>();
        public float BestRouteLength;
        /// <summary>解析日志（挪位 / 兜底的原因；不给玩家看）。</summary>
        public string Note = string.Empty;
    }

    /// <summary>FG7-ENV-05：家园探索（见 <see cref="GridState.Exploration"/>）。遗迹点 ID = 生成器点位 ID（relic:分布格X:分布格Y，只取决于种子，B25）。</summary>
    [Serializable]
    public sealed class HomeExplorationState
    {
        /// <summary>已按开局 / 旧档的已探索区域补登过（之后只有新揭开的才发通知）。</summary>
        public bool Baselined;
        /// <summary>发现过的遗迹点（被揭开的先后）与发现时的世界步。</summary>
        public string[] RelicsFound = Array.Empty<string>();
        public long[] RelicsFoundTicks = Array.Empty<long>();
        /// <summary>回收过的遗迹点（完工先后）与完工时的世界步。</summary>
        public string[] RelicsRecovered = Array.Empty<string>();
        public long[] RelicsRecoveredTicks = Array.Empty<long>();
        /// <summary>统计：回收遗迹累计得到的技术数据。</summary>
        public long TechData;
        /// <summary>揭开过的危害（fg.TbHazard.id）：第一次揭开发通知；更后幕的领地揭开后在地图上显示。</summary>
        public string[] HazardsSeen = Array.Empty<string>();
        /// <summary>FG8-GEN-02：发现过的资源缓存 / 旧终端（点位 ID cache:… / term:…，揭开先后）；打开 / 读取过的（完工先后）。与遗迹点分开记，遗迹统计不变。</summary>
        public string[] PoisFound = Array.Empty<string>();
        public string[] PoisDone = Array.Empty<string>();
        /// <summary>FG8-GEN-02 统计：打开缓存累计得到的废料、读取终端累计得到的技术数据。</summary>
        public long CacheScrap;
        public long TerminalTechData;
        /// <summary>FG8-EXP-04（FGR-GEN-033“手工设计的遗产每个世界各出现一次”）：遗产放在哪处遗迹点（遗产ID ↔ 遗迹点 ID，同一下标）。
        /// 第一次需要时按（种子, 遗产ID）在生成器给出的遗迹点里挑好后写进来，之后不再变；表里新增的遗产下次补挑（不动已挑好的）。</summary>
        public string[] HeritageIds = Array.Empty<string>();
        public string[] HeritageRelics = Array.Empty<string>();
        /// <summary>挑遗迹点时用的世界身份（种子 | 版本 | 设置 | 核心）：身份变了（理论上不会）就整组重挑。</summary>
        public string HeritageWorld = string.Empty;
        /// <summary>FG8-EXP-04 修复轮（FGR-GEN-033“剧情需要的遗产保证可以到达”）：剧情遗产所在遗迹点的可达校验结论（与 <see cref="HeritageIds"/> 同一下标；0 待校验 / 1 可达 / 3 换了几次仍走不到），
        /// 以及校验走不到、被换掉的遗迹点（“遗产ID@遗迹点ID”，换掉的先后；之后挑选时跳过这些遗迹点）。非剧情遗产不校验（恒 0）。</summary>
        public int[] HeritageReach = Array.Empty<int>();
        public string[] HeritageReachRejected = Array.Empty<string>();
        /// <summary>正在算的那一条可达校验（遗迹点 ID，空 = 没有）、它的采纳步、提前取出的结果（0 还在后台通道上 / 1 到得了 / 2 到不了；存档前或让出通道时取出）与寻路状态码。</summary>
        public string HeritageReachRelic = string.Empty;
        public long HeritageReachTick;
        public int HeritageReachResult;
        public int HeritageReachStatus;
        /// <summary>已存入核心保管库的遗产（入库先后）与入库时的世界步。遗产在世界里的位置（留在遗迹 / 装在货舱）记在区域关键物（human_legacy，heritage:遗产ID）。</summary>
        public string[] HeritageObtained = Array.Empty<string>();
        public long[] HeritageObtainedTicks = Array.Empty<long>();
        /// <summary>统计：数据档案累计给出的技术数据。</summary>
        public long HeritageTechData;
    }

    /// <summary>
    /// FG3-LOG-07：一组规划条目（布局库的布局、剪贴板、撤销栈里“放下 / 拆掉了哪些件”的快照共用同一格式）。按列存（每列一个数组），
    /// 一件 = 同一下标：条目 ID（建造菜单条目：建筑类型 ID 或工具 ID）、位置（布局里是相对坐标，撤销栈里是绝对格）、朝向、地下传送带出口、设置。
    /// 设置编码见 <see cref="Grid.PlanSettings"/>。按列存让一千件的布局在 JSON 里只有几十 KB（件数有上限：plan.layout_max_entries）。
    /// </summary>
    [Serializable]
    public sealed class PlanEntryBlock
    {
        public string[] Ids = Array.Empty<string>();
        public int[] Xs = Array.Empty<int>();
        public int[] Ys = Array.Empty<int>();
        /// <summary>建筑：朝向（0 / 90 / 180 / 270）；传送带类与管线类：方向（BeltDir 0 北 1 东 2 南 3 西）。</summary>
        public int[] Rots = Array.Empty<int>();
        /// <summary>地下传送带出口（其余件为 0）。</summary>
        public int[] X2s = Array.Empty<int>();
        public int[] Y2s = Array.Empty<int>();
        /// <summary>设置（0 = 默认）。建筑：S0 = 电力优先级；分流器：S0 = 左比例 | 右比例 &lt;&lt; 8 | 优先口 &lt;&lt; 16，S1 / S2 = 左 / 右过滤；
        /// 合流器：S0 = 优先入口；储罐：S0 = 1 | 模式 &lt;&lt; 1 | 优先级 &lt;&lt; 4；阀门：S0 = 1 | 开着 &lt;&lt; 1。</summary>
        public int[] S0 = Array.Empty<int>();
        public int[] S1 = Array.Empty<int>();
        public int[] S2 = Array.Empty<int>();

        public int Count => Ids?.Length ?? 0;
    }

    /// <summary>
    /// FG3-LOG-07：撤销栈里的一个操作（一步 = 编号相同的一组操作，一次撤销整步）。字段按种类使用，见 <see cref="Grid.PlanOpKind"/>。
    /// 建筑类操作记类型、位置、朝向与当前 ID（重做放回的虚影是新 ID，会写回这里）；传送带 / 管线类操作记 <see cref="Entries"/>（绝对格）。
    /// </summary>
    [Serializable]
    public sealed class PlanOpRecord
    {
        public int Step;
        /// <summary>这一步是什么（整步的名字：放置 / 拆除 / 粘贴 / 升级……，<see cref="Grid.PlanStepKind"/>）。</summary>
        public int StepKind;
        public int Kind;
        public string BuildingId;
        public string TypeId;
        public int X;
        public int Y;
        public int Rot;
        public int X2;
        public int Y2;
        public int Rot2;
        /// <summary>建筑的设置（电力优先级，0 = 默认）或操作的附加值（撤销时做了什么：见 <see cref="Grid.PlanHistory"/>）。</summary>
        public int S0;
        public int Flag;
        public string PlanId;
        public PlanEntryBlock Entries = new PlanEntryBlock();
        /// <summary>复制设置：改之前的设置（<see cref="Entries"/> 是改之后的）。</summary>
        public PlanEntryBlock Before = new PlanEntryBlock();
    }

    /// <summary>FG3-LOG-02：一份规划中的传送带（一次拖拽铺设）。格按路径顺序；<see cref="CellState"/>：0 = 规划中，1 = 已建成（已进内核），2 = 已取消。
    /// 材料按格计价（<see cref="ScrapPerCell"/>）：还没建成的格子要的材料 = 未建格数 × 单价；<see cref="Delivered"/> 是运到现场、还没被建成的格子用掉的材料。</summary>
    [Serializable]
    public sealed class PlannedBeltRecord
    {
        public string PlanId;
        public int Tier;
        public int ScrapPerCell;
        public int[] Xs = Array.Empty<int>();
        public int[] Ys = Array.Empty<int>();
        /// <summary>每格朝向（BeltDir：0 北 1 东 2 南 3 西）。</summary>
        public int[] Dirs = Array.Empty<int>();
        public int[] CellState = Array.Empty<int>();
        /// <summary>已运到现场、尚未被建成格子用掉的材料（废料）。</summary>
        public int Delivered;
        /// <summary>FG3-LOG-03（FGR-LOG-027）：这份规划是传送带被摧毁后留下的虚影（保留原朝向与等级）。
        /// 在玩家确认重建之前没有施工单、机器不会去建（自动重建规则在 FG6-DEF-03）；拆除模式点它 = 移除虚影。</summary>
        public bool Destroyed;
        /// <summary>FG3-LOG-04（FGR-LOG-022 / 023）：这份规划放的是什么——0 = 传送带（旧存档没有这个字段也是 0）；1 = 分流器；2 = 合流器（各一格）；
        /// 3 = 地下传送带（Xs / Ys = [入口, 出口]，两端一起建成、一起取消；<see cref="ScrapPerCell"/> 是每端造价）。值同 BinGames.Sim.Logistics.BeltNodeKind。</summary>
        public int NodeKind;
        /// <summary>FG3-LOG-04：分流器 / 合流器的设置（被摧毁留下的虚影保留原设置，重建时按它恢复；新放的 = 默认 1:1、不设优先口、全部物品）。
        /// 比例 0 按 1 处理；过滤同内核（0 = 全部物品，65535 = 关闭，其余 = 只放这种物品）；优先口 0 不设 / 1 左 / 2 右。</summary>
        public int RatioL;
        public int RatioR;
        public int PriorityOut;
        public int FilterL;
        public int FilterR;
        public int PriorityIn;
        /// <summary>FG3-LOG-05：0 = 传送带层的规划（上面的 NodeKind 有效）；1 管线 / 2 泵 / 3 储罐 / 4 阀门（= PipePieceKind + 1，占管线层；Tier = 管线等级，Dirs = 阀门流向）。</summary>
        public int PipePiece;
        /// <summary>FG3-LOG-05：泵的虚影在哪种流体的来源上（规划时由地形认定，建成时再核一次）。</summary>
        public int PipeFluid;
        /// <summary>FG3-LOG-07（粘贴 / 吸管带设置）：储罐 / 阀门建成时要写的设置（<see cref="Grid.PlanSettings"/> 的 S0 编码，0 = 默认）。旧存档没有这个字段 = 0。</summary>
        public int PipeSettings;
        /// <summary>FG3-LOG-07（FGR-LOG-010 升级规划）：这份规划是“原地升级”——格子上已经是建成的件（照常运转），机器取来新旧差额的材料，
        /// 一格一格改成 <see cref="PlannedBeltRecord.Tier"/>（物品、流体、设置都不动）。<see cref="ScrapPerCell"/> = 每件差额。
        /// 升级规划不占格网的“规划”标记（格子上是真件）。旧存档没有这个字段 = false。</summary>
        public bool Upgrade;
        /// <summary>升级前的等级（撤销 / 说明用）。</summary>
        public int FromTier;
        /// <summary>FG7-ENV-03（FGR-ENV-030）：这份规划是顶棚（存在 <see cref="RoofState.Plans"/>，不在 <see cref="GridState.PlannedBelts"/>；不占格网任何层）。
        /// 每格 = 一块 4×4 顶棚：Xs / Ys 是这一块里机器施工站的格（块 = 格坐标 ÷ 4 向下取整）；<see cref="ScrapPerCell"/> = 每块造价。</summary>
        public bool Roof;
    }

    /// <summary>一块已探索的圆形区域（格网坐标）。</summary>
    [Serializable]
    public sealed class ExploredAreaRecord
    {
        public int CenterX;
        public int CenterY;
        public int Radius;
    }

    /// <summary>传送带与带上物品（FG0-ARCH-02 / FG03）。唯一写入口 <see cref="Logistics.BeltNetworkService"/>（存档前
    /// <c>WorldSimulation.SyncAllForSave</c> 调它把内核快照写进来；读档后它从这里恢复内核）。
    /// FGR-ARC-004“存档按网络分块”：每个物流网络一块（格、物品位置与种类、汇入轮次），base64 编码的二进制，自带校验和；
    /// 单块损坏只丢那一块并如实告知（物品数计入“移出”，账本仍平衡），其余网络照常恢复。
    /// 域版本：1 = FG0-ARCH-02 之前的空骨架（读档得到空网络）；2 = 本格式。统计窗口（吞吐）不进存档，读档后重新累计。</summary>
    [Serializable]
    public sealed class BeltItemState
    {
        public int DomainVersion = 2;
        /// <summary>内核快照格式版本（<see cref="BinGames.Sim.Logistics.BeltKernel.FormatVersion"/>）；0 = 没有传送带数据。</summary>
        public int FormatVersion;
        /// <summary>内核已执行的固定步数（端口节拍、统计窗口对齐都以它为时间轴）。</summary>
        public long KernelSteps;
        /// <summary>物品守恒账（FGT-LOG-006）：推上 / 放入 / 收下 / 移出的累计数。</summary>
        public long Emitted;
        public long Inserted;
        public long Delivered;
        public long Removed;
        /// <summary>FG3-LOG-09（内核格式 4，DEBT-FG0ARCH02-10）：统计窗口已走满的桶数。窗口本身在各网络块 / 端口块的统计尾段里；
        /// 旧存档（格式 3 及更早）为 0，读档后窗口从头累计。</summary>
        public long CompletedBuckets;
        /// <summary>存档卡与自检用的摘要（真相在 <see cref="Networks"/>）。</summary>
        public int CellCount;
        public int ItemCount;
        public BeltNetworkRecord[] Networks = Array.Empty<BeltNetworkRecord>();
        /// <summary>端口块（base64）：建筑的输入输出端口状态（缓存、节拍、累计数）。</summary>
        public string Ports = string.Empty;
        /// <summary>输入端口所属建筑的名字文本键（堵塞原因“下游 X 的输入已满”用；端口本身在 <see cref="Ports"/>）。</summary>
        public BeltSinkNameRecord[] SinkNames = Array.Empty<BeltSinkNameRecord>();
        /// <summary>FG3-LOG-03（FGR-LOG-021）：建筑端口（建筑 + fg.TbBuildingPort 的端口 ID）↔ 内核端口号，以及仓库输出口的过滤器。
        /// 端口本身的状态（待推、缓存、累计）在 <see cref="Ports"/> 块里；唯一写入口 <see cref="Logistics.BeltPortService"/>。</summary>
        public BeltPortBindingRecord[] PortBindings = Array.Empty<BeltPortBindingRecord>();
        /// <summary>FG3-LOG-03：下一个建筑端口号（0 = 还没分配过，从 <see cref="Logistics.BeltPortService.PortIdBase"/> 开始）。</summary>
        public int NextPortId;
        /// <summary>FG3-LOG-03（FGR-LOG-027）：掉了耐久的传送带格（满耐久的不记）。唯一写入口 <see cref="Logistics.BeltNetworkService"/>。</summary>
        public BeltDamageRecord[] Damage = Array.Empty<BeltDamageRecord>();
        /// <summary>FG3-LOG-03（FGR-LOG-026）：清带送进仓库 / 玩家确认丢弃的累计件数（统计与守恒核对）。</summary>
        public long ClearedToStorage;
        public long Discarded;
        /// <summary>FG3-LOG-04（卡片“过滤器预设”）：玩家在分流器节点面板里“存为自定义预设”的设置，按保存先后；最多 logistics.splitter.custom_presets_max 个。
        /// 跟着存档走（每局能放的物品不同）；唯一写入口 <see cref="Logistics.BeltNodeService"/>。旧存档没有这个字段时补空。</summary>
        public BeltFilterPresetRecord[] FilterPresets = Array.Empty<BeltFilterPresetRecord>();
        /// <summary>下一个自定义预设的编号（单调递增，“自定义 N”的 N；删掉的编号不复用）。</summary>
        public int NextFilterPresetSerial = 1;
    }

    /// <summary>FG3-LOG-04：一个自定义过滤器预设（分流器设置：比例、优先输出口 0 / 1 左 / 2 右、左右口过滤 0 全部 / 65535 关闭 / 物品编号）。</summary>
    [Serializable]
    public sealed class BeltFilterPresetRecord
    {
        public int Serial;
        public int RatioL = 1;
        public int RatioR = 1;
        public int Priority;
        public int FilterL;
        public int FilterR;
    }

    /// <summary>FG3-LOG-03：一个建筑端口的绑定。</summary>
    [Serializable]
    public sealed class BeltPortBindingRecord
    {
        public int PortId;
        public string BuildingId;
        /// <summary>fg.TbBuildingPort 的端口 ID（如 "warehouse.out0"）。</summary>
        public string PortKey;
        /// <summary>输出口过滤：-1 = 全部可存物品，0 = 停止输出，&gt; 0 = 只输出这个物品编号。输入口不用。</summary>
        public int Filter = -1;
        /// <summary>FG4-ECO-05（FG-GAP-093）：仓库输出口“保留 N 件”——每种物品在家园库存里至少留这么多，多出来的才推上传送带。0 = 不保留。</summary>
        public int Keep;
    }

    /// <summary>FG3-LOG-03：一格传送带掉了多少耐久。</summary>
    [Serializable]
    public sealed class BeltDamageRecord
    {
        public int X;
        public int Y;
        public int Lost;
        /// <summary>FG6-DEF-05（承接 DEBT-FG6DEF03-03）：最近一次挨打的统一时钟步（0 = 旧档 / 没记）。维修无人机突袭中先修最近挨打的格。</summary>
        public long LastHitTick;
    }

    /// <summary>FG6-DEF-05（承接 DEBT-FG3LOG05-12 / DEBT-FG6DEF03-02）：一格掉了耐久的管线件（与传送带同一做法：满耐久的不记）。</summary>
    [Serializable]
    public sealed class PipeDamageRecord
    {
        public int X;
        public int Y;
        public int Lost;
        public long LastHitTick;
    }

    /// <summary>输入端口编号 → 所属建筑名字的文本键。</summary>
    [Serializable]
    public sealed class BeltSinkNameRecord
    {
        public int PortId;
        public string NameKey;
    }

    /// <summary>一个物流网络（弱连通的一组传送带）的存档块。</summary>
    [Serializable]
    public sealed class BeltNetworkRecord
    {
        public int Cells;
        public int Items;
        /// <summary>base64 编码的网络块（格式见 BeltKernel.Serialize）。</summary>
        public string Payload;
    }

    /// <summary>战斗内核（FG0-ARCH-03 / FG14 FGR-ARC-003）：每个已载入地点一份内核快照（单位位置、耐久镜像、武器热量与冷却、
    /// 编队命令（FG-GAP-018：读档后单位继续执行读档前的命令）、标记、飞行中的弹体、未处理完的玩法事件）。
    /// 唯一写入口 <see cref="Combat.CombatSites.WriteTo"/>（<c>WorldSimulation.SyncAllForSave</c> 调它）；读档时地点载入用它恢复。
    /// 快照是 base64 编码的二进制（带校验和）；损坏或格式不认识时按机器 / 敌人记录重建并发通知（记录仍是存在性与耐久的真相）。
    /// 位置在快照里是双精度，离原点一百万格仍是亚毫米精度（DEBT-FG0ARCH05-01 第 ② 项）。</summary>
    [Serializable]
    public sealed class CombatState
    {
        public int DomainVersion = 1;
        public CombatSiteRecord[] Sites = Array.Empty<CombatSiteRecord>();
    }

    /// <summary>一个地点的战斗内核快照。</summary>
    [Serializable]
    public sealed class CombatSiteRecord
    {
        public string SiteId;
        /// <summary>内核快照格式版本（<see cref="BinGames.Sim.Combat.CombatConst.FormatVersion"/>）。</summary>
        public int FormatVersion;
        public long KernelSteps;
        /// <summary>摘要（存档卡 / 自检用；真相在 <see cref="Payload"/>）。</summary>
        public int Units;
        public int Projectiles;
        public string Payload;
        /// <summary>内核单位外部键表：敌人实例 ID（单位的 ExtKey 是这张表的下标；机器的 ExtKey 直接是 LogicId）。</summary>
        public string[] Keys = Array.Empty<string>();
    }

    /// <summary>
    /// FG3-LOG-05 管线与流体（FG03 第 6 节“流体网络与储量、结冰状态”）：管线内核快照（每格一行、按铺设顺序；外部消费者一行）。
    /// 唯一写入口 <see cref="Logistics.PipeNetworkService.WriteTo"/>（WorldSimulation.SyncAllForSave 调它），读档时 <see cref="Logistics.PipeNetworkService.Load"/> 恢复。
    /// 网络编号、连接、网络流体是派生量，读档后按拓扑重算；每格的流体、储罐存量 / 模式 / 优先级、阀门开关 / 缓冲、“最近流动步”（结冰计时接口）进存档。
    /// DomainVersion 1 = FG0-SAVE-01 的空骨架（没有管线）；2 = 本格式。<see cref="FormatVersion"/> = 内核快照格式（不认识时整体保留、这局不能改管线）。
    /// </summary>
    [Serializable]
    public sealed class PipeFluidState
    {
        public int DomainVersion = 1;
        public int FormatVersion;
        public long KernelSteps;
        public long TotalPumpedMl;
        public long TotalDeliveredMl;
        public long TotalFlushedMl;
        public long TotalRemovedMl;
        public int[] Xs = Array.Empty<int>();
        public int[] Ys = Array.Empty<int>();
        public int[] Kinds = Array.Empty<int>();
        public int[] Tiers = Array.Empty<int>();
        public int[] Dirs = Array.Empty<int>();
        public int[] Fluids = Array.Empty<int>();
        public long[] Stocks = Array.Empty<long>();
        public int[] Modes = Array.Empty<int>();
        public int[] Priorities = Array.Empty<int>();
        public int[] Open = Array.Empty<int>();
        public long[] Buffers = Array.Empty<long>();
        public int[] BufferFluids = Array.Empty<int>();
        public long[] LastFlow = Array.Empty<long>();
        public long[] PumpTotals = Array.Empty<long>();
        public int[] ConsumerIds = Array.Empty<int>();
        public int[] ConsumerXs = Array.Empty<int>();
        public int[] ConsumerYs = Array.Empty<int>();
        public int[] ConsumerFluids = Array.Empty<int>();
        public int[] ConsumerLpm = Array.Empty<int>();
        public int[] ConsumerPriorities = Array.Empty<int>();
        public long[] ConsumerTotals = Array.Empty<long>();
        public int NextConsumerId = 1;
        /// <summary>FG4-ECO-02：消费者缓存 / 容量（建筑的流体输入缓存；旧存档没有 = 0，即“送达即消耗”）。</summary>
        public long[] ConsumerBuffers = Array.Empty<long>();
        public long[] ConsumerCapacities = Array.Empty<long>();
        /// <summary>FG4-ECO-02：外部供给者（建筑的流体出口：口外那一格、流体、存量、上限、累计送出）。</summary>
        public int[] ProducerIds = Array.Empty<int>();
        public int[] ProducerXs = Array.Empty<int>();
        public int[] ProducerYs = Array.Empty<int>();
        public int[] ProducerFluids = Array.Empty<int>();
        public long[] ProducerStocks = Array.Empty<long>();
        public long[] ProducerCapacities = Array.Empty<long>();
        public long[] ProducerTotals = Array.Empty<long>();
        public int NextProducerId = 1;
        public long TotalProducedOutMl;
        /// <summary>寒潮进行中（FGR-LOG-045 预留：FG7-ENV-03 的天气系统写它；本 Story 只保存，不产生结冰）。</summary>
        public bool ColdSnap;
        /// <summary>FG6-DEF-05（承接 DEBT-FG3LOG05-12 / DEBT-FG6DEF03-02）：掉了耐久的管线件（满耐久的不记；唯一写入口 <see cref="Logistics.PipeNetworkService"/>）。
        /// 只加字段：旧档没有 = 全部满耐久。</summary>
        public PipeDamageRecord[] Damage = Array.Empty<PipeDamageRecord>();
        /// <summary>FG6-LOG-10（FGR-LOG-046）：管线被击穿后地上的液洼（唯一写入口 <see cref="Logistics.PipeLeakService"/>）。只加字段：旧档没有 = 没有液洼。
        /// 液洼在战斗内核里的区域随家园地点快照进存档（格式 12），读档后按这里的记录对账。</summary>
        public PipeLeakRecord[] Leaks = Array.Empty<PipeLeakRecord>();
        public int NextLeakSerial = 1;
    }

    /// <summary>FG6-LOG-10（FGR-LOG-046）：一摊管线泄漏的液洼（一格管线最多一摊）。时间都按游戏步序号记（与观察、倍速无关）。</summary>
    [Serializable]
    public sealed class PipeLeakRecord
    {
        /// <summary>液洼编号（&gt; 0；内核区域的外部编号）。</summary>
        public int Id;
        /// <summary>破口所在的格。</summary>
        public int X;
        public int Y;
        /// <summary>漏出来的流体（fg.TbFluid.id）。</summary>
        public int Fluid;
        /// <summary>0 = 还在漏（半径随时间扩大）；1 = 破口已堵上、正在消退；2 = 已整片反应成残留区域（燃油遇火 = 燃烧区）。</summary>
        public int State;
        /// <summary>开始扩大的步（还在漏：半径 = 初始 + (最大 − 初始) × 已漏时长 / 扩散时长）。</summary>
        public long GrowTick;
        /// <summary>进入当前阶段的步。</summary>
        public long StateTick;
        /// <summary>开始消退时的半径（米；消退：半径 = 本值 × (1 − 已消退时长 / 消退时长)）。</summary>
        public float FromRadius;
        /// <summary>已反应：反应规则下标（fg.TbReaction 标签反应按 priority 排序后的下标）、残留区域到期的步、残留区域是否带持续伤害（会烧坏设施）。</summary>
        public int Rule = -1;
        public long ReactUntilTick;
        public bool Damaging;
    }

    /// <summary>
    /// FG3-LOG-06 电力子网与电塔（FG03 第 6 节“电网拓扑”；FGR-LOG-061 每个电网的曲线）：电网内核里不能从建筑记录推出来的部分。
    /// 电网拓扑本身（哪些建筑接在哪个电网、谁供电谁断电）是派生量：读档后按建筑记录与电力节点表重算；这里存
    /// ① 电网编号（每个电网一个锚点建筑 ID，读档后锚点所在的电网沿用这个编号），② 每个电网的曲线（发电、需要、实际用电、储能），③ 储能建筑的存量。
    /// 唯一写入口 <see cref="Regions.HomeValleyPowerGrid.WriteTo"/>（WorldSimulation.SyncAllForSave 调它），读档时第一次重算前恢复。
    /// DomainVersion 1 = 本 Story 之前的存档（没有这个域）；2 = 本格式；3 = FG4-ECO-04（加储能站设置、曲线的各类别发电）。<see cref="FormatVersion"/> = 内核快照格式（不认识时原样保留不覆盖）。
    /// </summary>
    [Serializable]
    public sealed class PowerGridState
    {
        public int DomainVersion = 1;
        public int FormatVersion;
        public int NextSerial = 1;
        public int[] SubnetSerials = Array.Empty<int>();
        /// <summary>每个电网的锚点建筑 ID（归还核心的虚拟配电中心写 “@core”）。</summary>
        public string[] SubnetAnchors = Array.Empty<string>();
        public int[] CurveCounts = Array.Empty<int>();
        public float[] CurveSupply = Array.Empty<float>();
        public float[] CurveDemand = Array.Empty<float>();
        public float[] CurveDelivered = Array.Empty<float>();
        public float[] CurveStored = Array.Empty<float>();
        public string[] StorageIds = Array.Empty<string>();
        public double[] StorageStored = Array.Empty<double>();
        /// <summary>FG4-ECO-04（内核格式 2）：每个曲线点的各类别发电（按点拼接，每点 PowerKernel.MaxSourceClasses 个）。旧存档没有这一列 = 读成全 0。</summary>
        public float[] CurveClassSupply = Array.Empty<float>();
        /// <summary>FG4-ECO-04（卡片“储能站的充放电设置”）：玩家改过设置的储能站（建筑 ID）与设置。没有记录的储能站 = 默认（充电开、放电给所有建筑）。
        /// 唯一写入口 <see cref="Regions.HomeValleyPowerGrid.TrySetStorageSettings"/>；拆掉的储能站在下一次重算时清掉。</summary>
        public string[] StorageSettingIds = Array.Empty<string>();
        public bool[] StorageNoCharge = Array.Empty<bool>();
        public bool[] StorageNoDischarge = Array.Empty<bool>();
        /// <summary>放电只给优先级 ≤ 这个数（1～3）；0 = 给所有建筑。</summary>
        public int[] StorageReserve = Array.Empty<int>();
    }

    /// <summary>研究（FG05）。FG5-RND-01：域版本 2——研发树开放（研究门槛生效）。读到版本 1 的旧档时由 <see cref="Economy.ResearchService.MigrateFromV1"/>
    /// 把“开放前就能建造的内容”对应的解锁节点记为已完成（旧档里已经在用的东西不会突然造不了），效率 / 容量节点不送。唯一写入口 <see cref="Economy.ResearchService"/>。</summary>
    [Serializable]
    public sealed class ResearchState
    {
        public const int CurrentVersion = 2;
        public int DomainVersion = CurrentVersion;
        /// <summary>FG4-ECO-01（FGR-ECO-001“数字资源：技术数据、研究点”）：研究点余额（数字资源，不占物理空间、不走传送带）。
        /// 产出与消耗由 FG5-RND-01（仿真实验室、研发树）写入；物资面板与图鉴从这里读。</summary>
        public int Points;
        /// <summary>FG4-ECO-11（研究门槛接口）：已完成的研究节点 ID（研发树在 FG5-RND-01 写入；超控阵列等建筑的 unlockRule = research:&lt;节点&gt; 读这里，
        /// 见 <see cref="Economy.ResearchGate"/>）。旧存档没有 = 空。</summary>
        public string[] CompletedNodes = Array.Empty<string>();
        /// <summary>FG5-RND-01：研究点的零头（千分之一点；实验室按周期产出 0.5 点这类小数，满 1000 进 <see cref="Points"/>，不丢）。</summary>
        public int PointsMilli;
        /// <summary>FG5-RND-01（FGR-RND-002“已投入的进度保留在节点上”）：投入过但还没完成的节点与已投入的研究点（按节点 ID 升序）。</summary>
        public ResearchProgressRecord[] Progress = Array.Empty<ResearchProgressRecord>();
        /// <summary>FG5-RND-01（FGR-RND-013）：研究队列（最多 research.queue.max 项，按顺序投入）。</summary>
        public string[] Queue = Array.Empty<string>();
        /// <summary>FG5-RND-01（FGR-RND-001）：每座仿真实验室的周期进度（按建筑 ID 升序）；建筑没了随之清掉。</summary>
        public LabRecord[] Labs = Array.Empty<LabRecord>();
        /// <summary>FG5-RND-01（FGR-RND-013“新解锁的建筑在建造菜单上显示‘新’，直到玩家看过一次”）：还没看过的建造菜单条目 ID。</summary>
        public string[] NewEntries = Array.Empty<string>();
        /// <summary>统计：实验室累计消耗的技术数据、累计产出 / 投入的研究点、完成的节点数。</summary>
        public long TechConsumed;
        public long PointsProduced;
        public long PointsInvested;
        /// <summary>上一次发出“研究暂停”通知的原因码（同一原因只告一次；研究恢复后清空）。</summary>
        public string StallNotified = string.Empty;
        /// <summary>FG5-RND-02：解析台 2.0（敌方物品身份清单、残骸缓存与进度、读过的资料、统计）。旧存档没有 = 空状态。</summary>
        public AnalysisBenchState Analysis = new AnalysisBenchState();
        /// <summary>FG5-RND-03：靶场（击败过的敌人类型、靶子布置与预设、已结束的测试结果）。投影与进行中的测试不存档。旧存档没有 = 空状态。</summary>
        public TestRangeState Range = new TestRangeState();
        /// <summary>FG5-RND-04：熔合（配方书、线索、模拟记录、正式熔合队列、待分析的战斗记录、统计）。旧存档没有 = 空状态。</summary>
        public FusionState Fusion = new FusionState();
        /// <summary>FG5-RND-05：监听站与情报（情报列表与有效期、已过时标记、各类破译进度、截获过的片段、统计）。旧存档没有 = 空状态。</summary>
        public IntelState Intel = new IntelState();
        /// <summary>FG5-RND-06：黑匣子陈列馆（已回收黑匣子的分析队列与进度、已入账的技术数据、统计）。黑匣子在区域里的位置 / 携带者沿用远征关键物
        /// （<see cref="CampaignState.RegionQuestItems"/>），纪念墙名单就是机器登记表里的阵亡记录。旧存档没有 = 空状态（读档时按已回收的黑匣子补队列）。</summary>
        public BlackBoxState BlackBoxes = new BlackBoxState();
        /// <summary>FG5-E2E-01（FGR-RND-070“统计面板里可以看到技术数据的收入和支出”；DEBT-FG5RND06-04）：技术数据按来源 / 去处的累计收支。
        /// 唯一写入口 <see cref="Economy.TechDataFlow"/>。旧存档没有 = 空（从读档那一刻起累计）。只加字段不升版本（ADR FG0-SAVE-01）。</summary>
        public TechFlowRecord[] TechFlow = Array.Empty<TechFlowRecord>();
        /// <summary>FG5-E2E-01（DEBT-FG5RND01-08）：新战役开局带来的技术数据（research.start_tech_data，过渡初值，见 ADR-QA-022）。旧档 = 0。统计面板对账用。</summary>
        public int TechStart;
    }

    /// <summary>FG5-E2E-01：技术数据的一类来源 / 去处的累计（<see cref="Economy.TechDataFlow"/> 的键）。退回的支出从 <see cref="Expense"/> 里扣回，不算收入。</summary>
    [Serializable]
    public sealed class TechFlowRecord
    {
        public string Key = string.Empty;
        public long Income;
        public long Expense;
    }

    /// <summary>FG5-RND-06（FG05 FGR-RND-060）：黑匣子陈列馆的存档域。唯一写入口 <see cref="Economy.BlackBoxService"/>。</summary>
    [Serializable]
    public sealed class BlackBoxState
    {
        /// <summary>已回收（送进陈列馆）的黑匣子，按回收先后排列（同一步回收的按机器 LogicId）；分析完的留着（陈列馆的陈列 / 纪念墙读“技术数据 +N”）。</summary>
        public BlackBoxRecord[] Boxes = Array.Empty<BlackBoxRecord>();
        /// <summary>统计：黑匣子累计入账的技术数据、分析完的个数。</summary>
        public long PointsProduced;
        public int Analyzed;
    }

    /// <summary>FG5-RND-06：一个已回收的黑匣子（身份 = 阵亡机器的 LogicId，一台机器只有一个）。</summary>
    [Serializable]
    public sealed class BlackBoxRecord
    {
        public int MachineLogicId;
        /// <summary>回收的世界步（家园阵亡 = 阵亡那一步；远征 = 撤离结算那一步）。</summary>
        public long RecoveredTick;
        /// <summary>已分析的世界步数（满 = 分析完；陈列馆停工时不动）。</summary>
        public long Work;
        /// <summary>已入账的技术数据（按进度逐点入账，满 = blackbox.points_per_box）。</summary>
        public int PointsGranted;
        public bool Done;
        /// <summary>分析完的世界步（未完成 = 0）。</summary>
        public long DoneTick;
    }

    /// <summary>FG5-RND-04（FG05 FGR-RND-040～045）：熔合的存档域。唯一写入口 <see cref="Economy.FusionService"/>。</summary>
    [Serializable]
    public sealed class FusionState
    {
        /// <summary>任务 / 线索 / 模拟记录的序号（确定性：观察 / 不观察、存读档前后同一结果）。</summary>
        public int NextSerial = 1;
        /// <summary>FGR-RND-042 / 045：已发现的配方（fg.TbFusionRecipe.id，按发现先后）。</summary>
        public string[] Discovered = Array.Empty<string>();
        /// <summary>FGR-RND-041：正式熔合队列（含已结束的最近几项，面板显示结果）。</summary>
        public FusionJobRecord[] Jobs = Array.Empty<FusionJobRecord>();
        /// <summary>FGR-RND-044：线索（部分 / 完整），按序号。</summary>
        public FusionClueRecord[] Clues = Array.Empty<FusionClueRecord>();
        /// <summary>FGR-RND-040：模拟熔合记录（这一对模拟过：有配方 / 无反应）。</summary>
        public FusionSimRecord[] SimLog = Array.Empty<FusionSimRecord>();
        /// <summary>没有可用的仿真实验室时先存着的战斗记录（有实验室了再分析）。</summary>
        public FusionPendingRecord[] Pending = Array.Empty<FusionPendingRecord>();
        /// <summary>已经分析过的战斗场次 ID（同一场不重复给线索；只保留最近若干个）。</summary>
        public string[] AnalyzedSessions = Array.Empty<string>();
        /// <summary>玩家在配方书里看过的最大线索序号（大于它的线索标“新”）。</summary>
        public int SeenClueSerial;
        /// <summary>“第一次拿到线索”的引导钩子已对这个存档扫过（钩子本身按玩家只广播一次）。</summary>
        public bool FirstClueSeen;
        /// <summary>统计：模拟次数 / 无反应次数 / 正式熔合完成次数 / 回滚次数 / 花在熔合上的技术数据。</summary>
        public int Simulations;
        public int SimulationMisses;
        public int Fused;
        public int RolledBack;
        public long TechSpent;
    }

    /// <summary>FG5-RND-04：正式熔合的状态（存档里存整数）。</summary>
    public enum FusionJobState
    {
        Queued = 0,
        Running = 1,
        Done = 2,
        Cancelled = 3,
        RolledBack = 4,
    }

    /// <summary>FG5-RND-04：一项正式熔合。两枚父固件芯片在固件库里被本项预留（<see cref="PrimitiveChipRecord.ReservedByTransactionId"/> = <see cref="JobId"/>），
    /// 芯片基板与技术数据入队时已从仓库取走、记在本项上（取消 / 回滚按记录退回，完成时消耗）。</summary>
    [Serializable]
    public sealed class FusionJobRecord
    {
        public string JobId = string.Empty;
        public int Serial;
        public string BuildingId = string.Empty;
        public string RecipeId = string.Empty;
        public string PartA = string.Empty;
        public string PartB = string.Empty;
        public int Substrate;
        public int Tech;
        public float Progress;
        public float Duration;
        public FusionJobState State;
        /// <summary>等待 / 结束原因码（no_power / disabled / destroyed / removed / chip_missing）。</summary>
        public string Reason = string.Empty;
        /// <summary>完成时产出的混合固件芯片实例 ID。</summary>
        public string OutputPartId = string.Empty;
        /// <summary>合成台的位置（入队时记下）：合成台被拆除后回滚，仓库放不下的芯片基板落在这里由机器搬回。</summary>
        public float PosX;
        public float PosY;
    }

    /// <summary>FG5-RND-04（FGR-RND-044）：一条线索。部分线索只知道 <see cref="KnownParent"/>；完整线索两条父固件都知道（显示时从配方表取）。</summary>
    [Serializable]
    public sealed class FusionClueRecord
    {
        public int Serial;
        public string RecipeId = string.Empty;
        public bool Full;
        public string KnownParent = string.Empty;
        /// <summary>线索来自哪条反应（标签 A + B 同时出现）与这一场出现了几次。</summary>
        public string ReactionId = string.Empty;
        public int Count;
        /// <summary>来源场次（expedition / raid / sim = 模拟熔合确认）、地点与第几次出击（标题按当前语言现拼）。</summary>
        public string SessionKind = string.Empty;
        public string SiteId = string.Empty;
        public int Ordinal;
        public long Tick;
    }

    /// <summary>FG5-RND-04（FGR-RND-040）：一次模拟熔合的结果（同一对只记最新一次）。</summary>
    [Serializable]
    public sealed class FusionSimRecord
    {
        public int Serial;
        /// <summary>两条固件 ID 按序号拼成的键（<see cref="Economy.FusionCatalog.PairKey"/>）。</summary>
        public string PairKey = string.Empty;
        /// <summary>有配方 = 配方 ID；无反应 = 空。</summary>
        public string RecipeId = string.Empty;
    }

    /// <summary>FG5-RND-04：一场等仿真实验室分析的战斗记录（反应 ID 与这一场的触发次数，按反应 ID 排序）。</summary>
    [Serializable]
    public sealed class FusionPendingRecord
    {
        public string SessionId = string.Empty;
        public string Kind = string.Empty;
        public string SiteId = string.Empty;
        public int Ordinal;
        public long EndTick;
        public string[] Reactions = Array.Empty<string>();
        public int[] Counts = Array.Empty<int>();
    }

    /// <summary>
    /// FG5-RND-05（FG05 FGR-RND-050～052、第 6 节“情报列表与有效期”）：情报的存档域。唯一写入口 <see cref="Economy.IntelService"/>。
    /// 进度与时间一律存整数（统一时钟的步数、千分之一倍率 × 步数），存读档往返逐字段一致。
    /// </summary>
    [Serializable]
    public sealed class IntelState
    {
        /// <summary>情报序号（确定性：观察 / 不观察、存读档前后同一结果）。</summary>
        public int NextSerial = 1;
        /// <summary>情报列表（有效的与已过时的；已过时的不删除，直到同一目标的新情报替代它，或超过每类保留上限时去掉最旧的已过时条目）。</summary>
        public IntelRecord[] Records = Array.Empty<IntelRecord>();
        /// <summary>每类情报的破译进度（被更紧急的一类插队、监听站全部失效时保留）。</summary>
        public IntelProgressRecord[] Progress = Array.Empty<IntelProgressRecord>();
        /// <summary>上一个世界步正在破译的那一类（面板与建筑状态显示；空 = 没有）。</summary>
        public string CurrentKind = string.Empty;
        /// <summary>“破译中断”已经通知过（监听站全部失效时只告一次；恢复后清空）。</summary>
        public bool InterruptNotified;
        /// <summary>已截获的舰队信号片段（fg.TbIntelFragment.id，按截获先后；情报条目被替换 / 裁掉也不会重复截获）。</summary>
        public string[] FragmentsHeard = Array.Empty<string>();
        /// <summary>玩家在情报面板看过的最大序号（大于它的标“新”）。</summary>
        public int SeenSerial;
        /// <summary>统计：累计产出的情报条数（含数据核心给的）。</summary>
        public long Produced;
        /// <summary>FG5-E2E-01（DEBT-FG5RND05-06）：统计——“破译中断”发生的次数（每次中断只记一次，与通知同一时刻）。旧档没有 = 0。</summary>
        public int Interruptions;
    }

    /// <summary>FG5-RND-05：一条情报。<see cref="Outdated"/> 是存档里的显式状态（不是界面按时间隐藏）。</summary>
    [Serializable]
    public sealed class IntelRecord
    {
        public int Serial;
        /// <summary>fg.TbIntelKind.id。</summary>
        public string Kind = string.Empty;
        /// <summary>针对的目标：突袭预报 = 行进队伍 ID；反制预览 = expedition；首领弱点 = 首领 ID；舰队片段 = 片段 ID。同一类同一目标的新情报替代旧的。</summary>
        public string Subject = string.Empty;
        /// <summary>来源：post = 监听站破译；data_core = 解析台解读数据核心。</summary>
        public string Source = string.Empty;
        public long ProducedTick;
        /// <summary>到这一步过期（含）。</summary>
        public long ExpiresTick;
        /// <summary>已过时（过了有效期 / 突袭已到达 / 目标已不在）。</summary>
        public bool Outdated;
        public long OutdatedTick;
        /// <summary>expired / arrived / gone。</summary>
        public string OutdatedReason = string.Empty;
        // ── 突袭预报 ──
        /// <summary>出发地（领地 ID）。</summary>
        public string Faction = string.Empty;
        public int Units;
        /// <summary>从归还核心指向来袭方向的单位向量（格网 X / Y）。</summary>
        public float DirX;
        public float DirY;
        /// <summary>预计进入到达半径的那一点（格）。</summary>
        public float ArriveX;
        public float ArriveY;
        /// <summary>抵达时间窗口（统一时钟步数）。</summary>
        public long WindowFromTick;
        public long WindowToTick;
        /// <summary>FG6-DEF-04：这次突袭的阵营内反制（fg.TbRaidCounter.id；空 = 没有）、目标种类（home / outpost）、等级。旧档没有 = 空 / 0。</summary>
        public string Counter = string.Empty;
        public string TargetKind = string.Empty;
        public int Level;
        /// <summary>FG6-DEF-04 复审：产出时突袭计划的变更序号（<see cref="RaidPlanRecord.Revision"/>）。之后计划升级 / 合并 / 改道 / 抵达改期 = 对不上 → 已过时（计划有变），监听站重新破译。旧档 = 0（与旧计划的 0 一致）。</summary>
        public int PlanRevision;
        // ── 敌方反制预览 ──
        public string[] Regions = Array.Empty<string>();
        public string[] Adaptations = Array.Empty<string>();
        /// <summary>FG9-FAC-01（FGR-FAC-004）：这条反制预览带着地下中继大厅的静默反制——产出时当天锁定的对策与下一次日切的预测（编成条件；空串 = 没有针对性反制）。
        /// 旧档没有 = false（不写这一行）。</summary>
        public bool HasSilentCounter;
        public string SilentCounter = string.Empty;
        public string SilentCounterNext = string.Empty;
    }

    /// <summary>FG5-RND-05：一类情报的破译进度（千分之一倍率 × 统一时钟步数；一座监听站一步 = 1000）与累计产出。</summary>
    [Serializable]
    public sealed class IntelProgressRecord
    {
        public string Kind = string.Empty;
        public long Work;
        public int Produced;
        /// <summary>
        /// 进度在破译哪个目标（突袭预报 = 行进队伍 ID、首领弱点 = 首领 ID；其它类为空）。目标换了进度清零，不把旧目标的进度算到新目标上；
        /// 旧档没有此字段（空）= 沿用现有进度。只加字段不升版本（ADR FG0-SAVE-01）。
        /// </summary>
        public string Subject = string.Empty;
    }

    /// <summary>FG5-RND-01：一个节点已投入的研究点（取消 / 移出队列后保留）。</summary>
    [Serializable]
    public sealed class ResearchProgressRecord
    {
        public string NodeId;
        public int Invested;
    }

    /// <summary>FG5-RND-01：一座仿真实验室的周期（周期开始时取 1 件技术数据 = Loaded，满一个周期按转换效率记研究点）。</summary>
    [Serializable]
    public sealed class LabRecord
    {
        public string BuildingId;
        /// <summary>本周期已推进的世界步（缺电 / 禁用时停住，不清零）。</summary>
        public long ProgressTicks;
        /// <summary>本周期的技术数据已经取了（拆除时退回）。</summary>
        public bool Loaded;
    }

    /// <summary>
    /// FG4-ECO-01（FG04 FGR-ECO-001 物品表、第 6 节“统计按时间窗口聚合保存”）：家园物资。
    /// 废料仍是 <see cref="CampaignState.Scrap"/>（Demo 起的唯一真相，所有旧调用方照用）、技术数据仍是 <see cref="CampaignState.TechData"/>；
    /// 本域存其余固体（仓库）与核心保管库（关键材料、人类遗产），以及净速率的库存采样。唯一写入口 <see cref="Economy.HomeInventory"/> / <see cref="Economy.ItemFlowStats"/>。
    /// </summary>
    [Serializable]
    public sealed class EconomyState
    {
        public int DomainVersion = 1;
        /// <summary>仓库里废料以外的固体（按物品 ID 升序，数量 &gt; 0）。</summary>
        public ItemStackRecord[] Items = Array.Empty<ItemStackRecord>();
        /// <summary>核心保管库（form = vault：关键材料、人类遗产；不上传送带）。</summary>
        public ItemStackRecord[] Vault = Array.Empty<ItemStackRecord>();
        /// <summary>净速率采样里每列对应的物品 ID（表变了就整体重采）。</summary>
        public string[] FlowItemIds = Array.Empty<string>();
        /// <summary>库存采样（按世界步，每 eco.flow.sample_seconds 游戏秒一条，最多 eco.flow.window_samples + 1 条，旧的在前）。</summary>
        public ItemFlowSampleRecord[] FlowSamples = Array.Empty<ItemFlowSampleRecord>();
        /// <summary>FG4-ECO-02：每座生产建筑（采集 / 加工）的配方、进度、输入 / 输出缓存、流体口句柄（按建筑 ID 升序）。唯一写入口 <see cref="Economy.ProductionService"/>。
        /// 旧存档没有 = 空（建筑出现后补建）。</summary>
        public ProducerRecord[] Producers = Array.Empty<ProducerRecord>();
        /// <summary>FG4-ECO-02：拆到一半的废墟格（剩余储量）；没拆过的格按 eco.ruin.scrap_per_cell，拆完的格已变成可建空地（区块差异）。按坐标排序。</summary>
        public RuinCellRecord[] RuinCells = Array.Empty<RuinCellRecord>();
        /// <summary>FG4-ECO-02（FG10 FGR-EVT-010 接口）：家园震动值 0～eco.vibration.max（提取钻工作时累积、按分钟衰减；蠕虫事件在 FG10）。</summary>
        public double Vibration;
        /// <summary>FG4-ECO-03：装配站的材料缓存（西侧输入口用传送带送来的机器材料，每种最多 eco.assembly.buffer_per_item）。唯一写入口 <see cref="Economy.AssemblyMaterials"/>。</summary>
        public ItemStackRecord[] AssemblyBuffer = Array.Empty<ItemStackRecord>();
        /// <summary>FG4-ECO-03：装配站“缺材料时用废料代付”：0 = 玩家没设置过（用 eco.assembly.scrap_substitute_default）、1 = 开、2 = 关。</summary>
        public int AssemblyScrapSubstitute;
        /// <summary>FG4-ECO-03（卡片“配方选择记住上一次的设置”）：玩家最近一次给每类生产建筑选的配方 / 刻录目标；新建的同类建筑沿用（按建筑类型 ID 升序）。</summary>
        public RecipeMemoryRecord[] RecipeMemory = Array.Empty<RecipeMemoryRecord>();
        /// <summary>FG4-ECO-03：装配站用产线材料（没有废料代付）造出的机器累计台数（图鉴“装配站”与引导钩子）。</summary>
        public long MachinesFromLine;
        /// <summary>FG4-ECO-10：软锁保底（应急打印、核心应急产废料）与死锁检测（传送带闭环、施工 / 维修目标到不了）的计时与已告警标记。
        /// 唯一写入口 <see cref="Economy.SoftlockService"/>。旧存档没有 = 空（从读档那一刻开始计；开局机器视为已经播种过）。</summary>
        public SoftlockState Softlock = new SoftlockState();
    }

    /// <summary>FG4-ECO-10（FG04 FGR-ECO-070～072）：软锁保底与死锁检测的存档状态。全部按世界步序号计时（暂停不走、倍速一帧多走几步、与观察无关）。</summary>
    [Serializable]
    public sealed class SoftlockState
    {
        /// <summary>开局的两台机器（ERC-001 / 搬运机）已经播种过：之后读档 / 重进家园不再补发（补机器只走每日应急打印，B10 不复制实体）。</summary>
        public bool StartMachinesSeeded;
        /// <summary>最近一次应急打印是第几个游戏日（0 = 从没打印过；每个游戏日最多一台）。</summary>
        public int LastPrintDay;
        /// <summary>应急打印累计台数 / 最近一台的机器 LogicId。</summary>
        public int PrintCount;
        public int LastPrintLogicId;
        /// <summary>核心应急产废料是否在进行（废料为 0 且没有能工作的回收站时进入；攒够造一座回收站或有回收站开始工作时结束）。</summary>
        public bool CoreScrapActive;
        /// <summary>应急产废料这一段已经累计的世界步（满一分钟产一次，零头留到下一次）。</summary>
        public long CoreScrapTicks;
        /// <summary>应急产废料累计产出的件数（统计 / 图鉴 / 自检读数）。</summary>
        public long CoreScrapProduced;
        /// <summary>正在被盯着的传送带闭环（按环上最小的格排序；网络重建后按代表格接回原来的计时）。</summary>
        public LoopWatchRecord[] Loops = Array.Empty<LoopWatchRecord>();
        /// <summary>正在被盯着的施工 / 维修目标（机器到不了的那一段；按建筑 ID 排序）。</summary>
        public ReachWatchRecord[] Sites = Array.Empty<ReachWatchRecord>();
    }

    /// <summary>FG4-ECO-10：一条传送带闭环的卡死计时。</summary>
    [Serializable]
    public sealed class LoopWatchRecord
    {
        /// <summary>代表格（第一次盯上时这个网络里 (y, x) 最小的格；之后网络变化但这一格还在网络里就沿用，计时与已告警标记不丢）。</summary>
        public int X;
        public int Y;
        /// <summary>这一段开始的世界步。</summary>
        public long SinceTick;
        /// <summary>上一次检查时环上的物品数（这一次比它少 = 有物品被拿走 = 有进展，重新计时；增加不算进展）。</summary>
        public int LastItems;
        /// <summary>这一段已经告过警（读档后不重复通知，静默恢复进告警栏）。</summary>
        public bool Alerted;
    }

    /// <summary>FG4-ECO-10：一座施工 / 维修目标建筑“机器到不了”的计时。</summary>
    [Serializable]
    public sealed class ReachWatchRecord
    {
        public string BuildingId;
        public long SinceTick;
        public bool Alerted;
        /// <summary>到不了的原因（文本键 + 参数，界面按当前语言重组）。</summary>
        public bool Enclosed;
        public string Blockers = string.Empty;
    }

    /// <summary>FG4-ECO-03：一类生产建筑“上一次的设置”（新建的同类建筑沿用）。</summary>
    [Serializable]
    public sealed class RecipeMemoryRecord
    {
        public string TypeId;
        /// <summary>配方（空 = 玩家上一次选的是“不选：待机”）。</summary>
        public string RecipeId = string.Empty;
        /// <summary>固件刻录台：刻哪条固件（空 = 不选）。</summary>
        public string BurnTarget = string.Empty;
    }

    /// <summary>FG4-ECO-02：一座生产建筑的运行状态（建筑本身的位置 / 朝向 / 电力在 <see cref="BuildingRecord"/>）。</summary>
    [Serializable]
    public sealed class ProducerRecord
    {
        public string BuildingId;
        /// <summary>玩家选的配方（多配方建筑；空 = 没选、待机）。只有一条配方的建筑不写（那就是它的固定功能）。</summary>
        public string RecipeId = string.Empty;
        /// <summary>正在做一个周期（开工时已扣料）。</summary>
        public bool Running;
        /// <summary>本周期已走的世界步数 / 本周期要走的世界步数（游戏时钟，倍速只改每帧跑几步）。</summary>
        public long Progress;
        public long Duration;
        /// <summary>回收站 / 提取钻本周期完成时产出的物品与件数。</summary>
        public string PendingItem = string.Empty;
        public int PendingAmount;
        /// <summary>本周期是在拆废墟（回收站；否则是在分解送来的物品）。</summary>
        public bool PendingRuin;
        /// <summary>固体输入 / 输出缓存。</summary>
        public ItemStackRecord[] In = Array.Empty<ItemStackRecord>();
        public ItemStackRecord[] Out = Array.Empty<ItemStackRecord>();
        /// <summary>每个流体口（按 fg.TbBuildingFluidPort 的顺序）在管线内核里的消费者 / 供给者编号（-1 = 没登记）。</summary>
        public int[] FluidHandles = Array.Empty<int>();
        /// <summary>每个流体口是不是输出口（true = 句柄是供给者编号，false = 消费者编号）。管线内核里两类编号各自从 1 起、会重号，
        /// 撤口必须按这里的类型撤对的那一类（建筑已不在时也能撤，见 ProductionService.RemoveFluidHandles）。每次建索引按建筑类型重写。</summary>
        public bool[] FluidOut = Array.Empty<bool>();
        /// <summary>流体口没登记（建筑没在运转）时，建筑自己留着的流体（毫升）。</summary>
        public long[] FluidHeld = Array.Empty<long>();
        /// <summary>累计完成的周期数；回收站累计拆出的废墟废料、分解的物品件数。</summary>
        public long Completed;
        public long RuinRecovered;
        public long ItemsRecycled;
        /// <summary>流体泵累计抽出的流体（毫升；本周期的零头记在 <see cref="Progress"/>）。</summary>
        public long PumpedMl;
        /// <summary>脚下废墟拆完的通知已经发过（只发一次）。</summary>
        public bool RuinDepletedNotified;
        /// <summary>FG4-ECO-03：固件刻录台要刻的固件（空 = 没选、待机）。</summary>
        public string BurnTarget = string.Empty;
        /// <summary>FG4-ECO-03：这座建筑的配方 / 刻录目标是新建时沿用的“上一次的设置”（面板写明；玩家改过就清掉）。</summary>
        public bool Inherited;
        /// <summary>FG4-ECO-04 燃油发电机：有油在发电（false = 烧空停机，机内攒够 power.fuel.restart_seconds 秒满负荷的油后重新发电）。
        /// 电网组装实体时按它开关这座的发电（HomeValleyPowerGrid）。新建 = false（没油）。</summary>
        public bool Fueled;
        /// <summary>FG4-ECO-04 燃油发电机：累计烧掉的燃油（毫升；本步的零头记在 <see cref="Progress"/>，单位 毫升 × 60 × 世界频率）。</summary>
        public long FuelBurnedMl;
        /// <summary>FG4-ECO-05（FGR-ECO-011 效率与最近 10 分钟产出）：按 building.stats.bucket_seconds 一桶的环形统计（桶数 = 窗口 ÷ 桶长）。
        /// 完成一份时累加产出与份数，推进时累加“理论份数”（这段时间满速能做几份）；不每帧遍历。读档后接着同一桶。</summary>
        public ProducerStatBucket[] Stats = Array.Empty<ProducerStatBucket>();
        /// <summary>FG4-ECO-08 废液池：上次对账时内核消费口的累计送达量（毫升）与口编号——统计按差额记销毁量。
        /// 进存档（审查修复）：读档后接着同一基准对账，读档到第一次对账之间的销毁量不丢（内核的累计量本身也进存档）。-1 = 还没对过账。</summary>
        public int WasteSeenHandle = -1;
        public long WasteSeenMl;
        /// <summary>FG4-ECO-09（FGR-ECO-080“持续缺料 / 输出持续堵塞”）：当前卡住的种类（0 = 没卡，1 = 缺料，2 = 输出堵塞）、从哪一世界步开始、这一段是否已经告过警。
        /// 随生产步 O(1) 更新（不每帧遍历）；进存档：读档后接着同一段计时，不会因为读档重新告警或晚告警。</summary>
        public int JamKind;
        public long JamSinceTick;
        public bool JamAlerted;
        /// <summary>FG4-ECO-09（离家报告的瓶颈）：这座建筑在第 <see cref="AwaySerial"/> 份离家报告期间缺每种物品的世界步数（随生产步 O(1) 累加，结算时汇总）。</summary>
        public int AwaySerial;
        public ItemStackRecord[] AwayStarve = Array.Empty<ItemStackRecord>();
        /// <summary>FG7-ENV-04 净化塔：已累计、还没换成一次降级的“有电有冷却液”世界步数（满 purify.hours_per_level 游戏小时降一环 1 级；读档后接着同一进度）。</summary>
        public long PurifyTicks;
        /// <summary>FG7-ENV-04 净化塔：上一个生产步是否有电有冷却液在净化（净化区 = 这类塔的范围；进存档，读档后第一步酸雨结算就与一直跑的一致）。</summary>
        public bool PurifySupplied;
        /// <summary>FG7-ENV-04 净化塔：累计降级的格次（每格降 1 级记 1）与累计用掉的冷却液（毫升；本步零头记在 <see cref="Progress"/>，单位 毫升 × 60 × 世界频率）。</summary>
        public long PurifiedLevels;
        public long CoolantUsedMl;
        /// <summary>FG7-ENV-04 净化塔：这一段缺冷却液已经发过“净化暂停”通知（接上后清掉，下次断供再发一次）。</summary>
        public bool PurifyStallNotified;
    }

    /// <summary>FG4-ECO-05：生产统计的一桶（游戏时钟的第 <see cref="Index"/> 桶）。</summary>
    [Serializable]
    public sealed class ProducerStatBucket
    {
        /// <summary>桶序号 = 游戏步 ÷ 每桶步数；-1 = 空桶。</summary>
        public long Index = -1;
        /// <summary>这一桶完成的份数。</summary>
        public int Done;
        /// <summary>这一桶的理论份数 × 1000（满速工作能完成的份数；施工中 / 被摧毁时不计）。</summary>
        public long TheoryMilli;
        /// <summary>这一桶的产出（固体按件，流体按升）。</summary>
        public ItemStackRecord[] Out = Array.Empty<ItemStackRecord>();
        /// <summary>FG4-ECO-08（FGR-ECO-050“瓶颈查找：列出最常缺的物品，以及缺它的建筑”）：这一桶里缺每种物品的世界步数（缺料 / 缺流体；随生产步累加，不每帧遍历）。</summary>
        public ItemStackRecord[] Starve = Array.Empty<ItemStackRecord>();
    }

    /// <summary>FG4-ECO-02：拆到一半的废墟格。</summary>
    [Serializable]
    public sealed class RuinCellRecord
    {
        public int X;
        public int Y;
        public int Remaining;
    }

    /// <summary>FG4-ECO-01：一种物品的持有量。</summary>
    [Serializable]
    public sealed class ItemStackRecord
    {
        public string ItemId;
        public int Amount;
    }

    /// <summary>FG4-ECO-01：一次库存采样（<see cref="Stocks"/> 与 <see cref="EconomyState.FlowItemIds"/> 一一对应）。</summary>
    [Serializable]
    public sealed class ItemFlowSampleRecord
    {
        public long Tick;
        public int[] Stocks = Array.Empty<int>();
    }

    /// <summary>天气（FG07；时间在 <see cref="GameClockState"/>）。</summary>
    [Serializable]
    public sealed class WeatherState
    {
        public int DomainVersion = 1;
        /// <summary>FG7-ENV-02（FG07 FGR-ENV-010～015）：静默夜计划、进行中的静默夜、施工暂缓名单、风暴残骸与结束总结的基线。
        /// 唯一写入口 <see cref="Env.SilentNightService"/>。只加字段、不升域版本：旧档没有 = 第一次推进时按当前游戏日排好下一次（不追溯）。</summary>
        public SilentNightState SilentNight = new SilentNightState();
        /// <summary>FG7-ENV-03（FG07 FGR-ENV-020 / 021）：天气排定与进行中的天气、酸雨结算进度。唯一写入口 <see cref="Env.WeatherService"/>。
        /// 只加字段、不升域版本：旧档没有 = 第一次推进时从当前游戏日起按种子排定（不追溯）。</summary>
        public WeatherFrontState Fronts = new WeatherFrontState();
        /// <summary>FG7-ENV-03（FGR-ENV-030）：顶棚（已建成的块、施工中的规划、是否隐藏）。唯一写入口 <see cref="Env.RoofService"/>。</summary>
        public RoofState Roofs = new RoofState();
        /// <summary>FG7-ENV-04（FGR-ENV-040～043）：归还面积里程碑、稀土富矿、受污染的建筑、净化 / 再污染统计。唯一写入口 <see cref="Env.PurificationService"/>。
        /// 污染格网本身在格网区块里（随区块生成、被改过的格子随区块差异进存档）；净化塔的进度在 <see cref="ProducerRecord"/>。只加字段、不升域版本：旧档没有 = 空。</summary>
        public PurificationState Purification = new PurificationState();
    }

    /// <summary>FG7-ENV-04：污染与净化（见 <see cref="WeatherState.Purification"/>）。</summary>
    [Serializable]
    public sealed class PurificationState
    {
        /// <summary>已达成的归还面积里程碑（fg.TbReturnMilestone.id，按达成先后）与达成时的世界步。奖励由它派生（天气损伤、信号覆盖），达成后一直有效。</summary>
        public string[] Reached = Array.Empty<string>();
        public long[] ReachedTicks = Array.Empty<long>();
        /// <summary>揭示过的稀土富矿（归还 20,000 格的奖励）。</summary>
        public RichDepositRecord[] RichDeposits = Array.Empty<RichDepositRecord>();
        /// <summary>处于“受污染”状态的建筑 ID（被再污染的格子上的建筑；天气损伤加倍，所在格子全部净化到 0 级后解除）。按进入先后。</summary>
        public string[] Contaminated = Array.Empty<string>();
        /// <summary>统计：净化塔累计降级的格次、再污染累计升级的格次、再污染次数。</summary>
        public long LevelsPurified;
        public long LevelsRaised;
        public int Repollutions;
        /// <summary>统计面板的趋势（B13）：每个游戏小时记一次归还面积（最多保留 purify 的 25 个样本），与记录时的游戏小时序号。</summary>
        public long[] AreaSamples = Array.Empty<long>();
        public long SampleHour = -1;
    }

    /// <summary>FG7-ENV-04：一处稀土富矿（圆心格与半径；范围内压在稀土矿脉上的提取钻产出 × purify.rich_ore.yield_mult）。</summary>
    [Serializable]
    public sealed class RichDepositRecord
    {
        public int X;
        public int Y;
        public int Radius;
        /// <summary>来自哪个里程碑（fg.TbReturnMilestone.id）。</summary>
        public string Source = string.Empty;
    }

    /// <summary>FG7-ENV-03：一段恶劣天气（排定后写死；时刻是统一时钟的整数步）。</summary>
    [Serializable]
    public sealed class WeatherFrontRecord
    {
        /// <summary>序号（确定性；随机数按 (世界种子, 序号) 取）。</summary>
        public int Serial;
        /// <summary>天气 ID（fg.TbWeather.id：acid_rain / sandstorm / cold_snap）。</summary>
        public string Kind = string.Empty;
        public long StartTick;
        public long EndTick;
        /// <summary>已经发过开始前的预警。</summary>
        public bool Warned;
    }

    /// <summary>FG7-ENV-03：天气状态（见 <see cref="WeatherState.Fronts"/>）。</summary>
    [Serializable]
    public sealed class WeatherFrontState
    {
        /// <summary>已排定、还没结束的恶劣天气（按开始步升序；结束的移走）。</summary>
        public WeatherFrontRecord[] Fronts = Array.Empty<WeatherFrontRecord>();
        /// <summary>排定已经覆盖到哪一步（之后的天气还没排；-1 = 还没开始排）。</summary>
        public long PlannedUntilTick = -1;
        /// <summary>下一段（晴天或恶劣天气）排定用的随机数序号。</summary>
        public int NextSerial = 1;
        /// <summary>正在进行的天气（序号；0 = 晴）。</summary>
        public int ActiveSerial;
        /// <summary>正在进行的天气 ID（空 = 晴）。与 <see cref="ActiveSerial"/> 一起写。</summary>
        public string ActiveKind = string.Empty;
        /// <summary>酸雨：下一次按游戏分钟结算的步；这一段酸雨已经结算了几个游戏分钟（整数预算的分子，读档后接着算）。</summary>
        public long NextBatchTick = -1;
        public long AcidMinutes;
        /// <summary>这一段天气期间受损的建筑 / 传送带格数、被摧毁的数量、结冰过的网络峰值（结束通知与离家报告用）。</summary>
        public int DamagedBuildings;
        public int DamagedBelts;
        public int Destroyed;
        public int FrozenPeak;
        /// <summary>这一段酸雨里受损过的建筑 ID / 传送带格（(x &lt;&lt; 32) ^ y），去重用（读档后接着数）。</summary>
        public string[] HitBuildings = Array.Empty<string>();
        public long[] HitBelts = Array.Empty<long>();
        /// <summary>上一次发“天气损伤”汇总通知的步（同一段酸雨里按 env.weather.damage_notify_hours 限频）。</summary>
        public long LastDamageNotifyTick = -1;
        /// <summary>已经开始过几段恶劣天气（第一次引导钩子、统计）。</summary>
        public int Count;
    }

    /// <summary>FG7-ENV-03：一块已建成的顶棚（顶棚格坐标：格坐标 ÷ 4 向下取整；覆盖 [Bx×4, Bx×4+3] × [By×4, By×4+3]）。</summary>
    [Serializable]
    public sealed class RoofRecord
    {
        public int Bx;
        public int By;
    }

    /// <summary>FG7-ENV-03：顶棚（见 <see cref="WeatherState.Roofs"/>）。</summary>
    [Serializable]
    public sealed class RoofState
    {
        /// <summary>已建成的顶棚块（按建成先后）。</summary>
        public RoofRecord[] Built = Array.Empty<RoofRecord>();
        /// <summary>施工中的顶棚规划（与传送带规划同一种记录，<see cref="PlannedBeltRecord.Roof"/> = true；每格 = 一块，Xs / Ys 是机器施工站的格）。</summary>
        public PlannedBeltRecord[] Plans = Array.Empty<PlannedBeltRecord>();
        public int NextPlanSerial = 1;
        /// <summary>玩家用快捷键隐藏了顶棚（跟着存档走，同叠加层开关）。</summary>
        public bool Hidden;
    }

    /// <summary>FG7-ENV-02：静默夜状态（见 <see cref="WeatherState.SilentNight"/>）。时刻一律是统一时钟的整数步（存读档逐位一致）。</summary>
    [Serializable]
    public sealed class SilentNightState
    {
        /// <summary>下一次（或正在进行的）静默夜在第几个游戏日的夜里（0 = 还没排）。</summary>
        public int NextDay;
        /// <summary>那一次的预警 / 开始 / 结束步（排定时按当时的调参算好写死，之后调参或难度变化只影响再下一次）。</summary>
        public long WarnTick = -1;
        public long StartTick = -1;
        public long EndTick = -1;
        /// <summary>这一次已经发过预警。</summary>
        public bool Warned;
        /// <summary>静默夜正在进行。</summary>
        public bool Active;
        /// <summary>已经开始过几次静默夜（第一次说明、风暴残骸编号的种子）。</summary>
        public int Count;
        /// <summary>静默夜期间新放的施工规划（工单 ID，按放下先后）：结束后才开工（FGR-ENV-011“新的施工规划”）。</summary>
        public string[] HeldOrderIds = Array.Empty<string>();
        /// <summary>开始那一刻突袭结算的下一个序号（结束总结只数这之后的结算）。</summary>
        public int ResultSerialAtStart;
        /// <summary>开始那一刻进入安全模式的远征机器（编号；结束总结与自检用）。</summary>
        public int[] ExpeditionSafeIds = Array.Empty<int>();
        /// <summary>风暴残骸地面物序号（地面物 ID 确定性，不用 GUID）。</summary>
        public int NextDebrisSerial = 1;
        /// <summary>最近一次静默夜留下的风暴残骸（地面物 ID）。</summary>
        public string[] LastDebrisIds = Array.Empty<string>();
        /// <summary>最近一次静默夜结束总结（已拼好的文字；日历 / 自检读）。</summary>
        public string LastSummary = string.Empty;
    }

    /// <summary>突袭（FG06）。FG0-ARCH-01 起保存"行进中的队伍"（星球表面上的突袭部队，由 <see cref="WorldSim.WorldTransitSystem"/> 唯一写入）；
    /// FG0-ARCH-06 起保存敌方据点与巡逻（突袭的出发地；休眠与唤醒，由 <see cref="WorldSim.WorldOutpostSystem"/> 唯一写入）。
    /// 突袭导演、编成、攻城与结算由 FG6-DEF-04～08 在本域追加字段；据点的正式生成由 FG3-GEN-01 / FG8-GEN-02，增援规则由 FG8-EXP-03 接手。</summary>
    [Serializable]
    public sealed class RaidState
    {
        public int DomainVersion = 1;
        /// <summary>FG0-ARCH-01：星球上行进中 / 已到达、尚未结算的队伍。</summary>
        public TransitGroupRecord[] InTransit = Array.Empty<TransitGroupRecord>();
        /// <summary>FG0-ARCH-01：下一个队伍序号（GroupId = "transit-" + 序号，确定性，不用 GUID）。</summary>
        public int NextGroupSerial = 1;
        /// <summary>FG0-ARCH-06：队伍寻路请求的序号计数（队伍只认领序号一致的结果）。</summary>
        public int NextNavSerial = 1;
        /// <summary>FG0-ARCH-06：敌方据点（FGR-GEN-034；休眠与唤醒 FGR-GEN-052 第 3 条 / FGR-ARC-016）。</summary>
        public OutpostRecord[] Outposts = Array.Empty<OutpostRecord>();
        /// <summary>FG0-ARCH-06：据点派出的巡逻（与所属据点一起休眠 / 唤醒）。</summary>
        public PatrolRecord[] Patrols = Array.Empty<PatrolRecord>();
        public int NextOutpostSerial = 1;
        public int NextPatrolSerial = 1;
        /// <summary>FG0-ARCH-06：排队等唤醒的据点（每步至多 outpost.wakes_per_step 个，分帧进行；存档保留顺序）。</summary>
        public string[] PendingWakeIds = Array.Empty<string>();
        public string[] PendingWakeReasons = Array.Empty<string>();
        /// <summary>FG6-DEF-01（FG06 FGR-DEF-001～005）：炮塔（固定底盘的机器）——每座炮塔装的蓝图、目标模式、击毁数、存着的流体，信号是否在炮塔里。
        /// 唯一写入口 <see cref="Defense.TurretService"/>。只加字段、不升域版本（ADR FG0-SAVE-01）：旧档没有 = 没有炮塔。</summary>
        public TurretState Turrets = new TurretState();
        /// <summary>FG6-DEF-02（FG06 FGR-DEF-010～013）：屏障 / 闸门 / 护盾发生器 / 陷阱发射器——战斗内核里的结构单位序号、护盾状态机（状态与到期步）、陷阱装的固件 / 铺设方式 / 存着的流体。
        /// 唯一写入口 <see cref="Defense.DefenseService"/>。只加字段、不升域版本：旧档没有 = 没有这些防御建筑。</summary>
        public DefenseState Defense = new DefenseState();
        /// <summary>FG6-DEF-04（FG06 FGR-DEF-020～024）：突袭导演——排定的突袭计划、阈值与周期触发的计时、最短间隔、各方向被摧毁的据点、突袭历史。
        /// 唯一写入口 <see cref="Defense.RaidDirectorService"/>。只加字段、不升域版本：旧档没有 = 导演第一次推进时按当前暴露补好（不追溯触发）。</summary>
        public RaidDirectorState Director = new RaidDirectorState();
        /// <summary>FG6-DEF-05（FG06 FGR-DEF-030～032）：攻城——攻城剧场矩形、进了战斗内核的建筑（序号 ↔ 建筑 ID）、施工虚影的溅射伤害、破墙通知。
        /// 唯一写入口 <see cref="Defense.SiegeService"/>。只加字段、不升域版本：旧档没有 = 没有进行中的攻城。</summary>
        public SiegeState Siege = new SiegeState();
        /// <summary>FG6-DEF-08（FG06 FGR-DEF-050～053）：突袭结算（伤害 / 击毁 / 反应占比 / 损失 / 贡献 / 战利品 / 时间线）、残骸去向、核心被摧毁与预警自动存档。
        /// 唯一写入口 <see cref="Defense.RaidResultService"/>。只加字段、不升域版本：旧档没有 = 没有结算记录。</summary>
        public RaidResultState Results = new RaidResultState();
        /// <summary>FG6-DEF-09（FG06 FGR-DEF-060；FG15 FGR-SYS-081）：难度——开局难度、自定义滑条的三个倍率、游戏中途的修改记录（成就判定用）。
        /// 当前难度 ID 仍是 <see cref="CampaignState.DifficultyId"/>（存档卡显示它）。唯一写入口 <see cref="Defense.DifficultyService"/>。
        /// 只加字段、不升域版本：旧档没有 = 开局难度按当前难度补、没有修改记录、自定义倍率按 ×1。</summary>
        public RaidDifficultyState Difficulty = new RaidDifficultyState();
        /// <summary>FG8-EXP-03（FG08 FGR-EXP-021 / 022）：领地控制度、平定 / 收复、阵营增援、精英残部、骚扰。唯一写入口 <see cref="WorldSim.TerritoryControlService"/>。
        /// 只加字段、不升域版本：旧档没有 = 第一次推进时按当前据点补好（敌控、按此刻的据点算控制度）。</summary>
        public TerritoryControlState Territory = new TerritoryControlState();
    }

    /// <summary>FG6-DEF-09：难度状态（见 <see cref="RaidState.Difficulty"/>）。</summary>
    [Serializable]
    public sealed class RaidDifficultyState
    {
        /// <summary>开局时的难度 ID（新建战役时写入；旧档读进来时按当前难度补）。</summary>
        public string StartDifficultyId = string.Empty;
        /// <summary>自定义难度的突袭频率 / 规模 / 预警时间倍率（按 difficulty.custom_step 取整；只在 DifficultyId = Custom 时生效）。</summary>
        public float CustomFrequency = 1f;
        public float CustomScale = 1f;
        public float CustomWarning = 1f;
        /// <summary>是否曾经离开过开局难度（修改记录被截断后仍永久保留；“全程严酷”成就据此判定）。</summary>
        public bool EverLeftStart;
        /// <summary>修改总次数（含已截断的更早记录）。</summary>
        public int ChangeCount;
        /// <summary>最近的修改记录（最多 difficulty.history_max 条，旧的在前）。</summary>
        public DifficultyChangeRecord[] Changes = Array.Empty<DifficultyChangeRecord>();
    }

    /// <summary>FG6-DEF-09：一次难度修改。</summary>
    [Serializable]
    public sealed class DifficultyChangeRecord
    {
        public long Tick;
        public string FromId = string.Empty;
        public string ToId = string.Empty;
        public float FromFrequency;
        public float FromScale;
        public float FromWarning;
        public float ToFrequency;
        public float ToScale;
        public float ToWarning;
        /// <summary>这次修改取消了几波还没发预警的普通突袭（改成建造者时）。</summary>
        public int CancelledRaids;
    }

    /// <summary>
    /// FG0-ARCH-06：一个敌方据点（聚合体：驻军人数 + 增援计时）。
    /// 离所有己方实体都很远时<b>休眠</b>（不参与每步模拟）；被惊动时按确定性规则补算休眠期间应有的变化（增援），
    /// 结果与“一直在模拟”逐字段一致（FGT-GEN-010）。<see cref="SimTick"/> = 下一个要模拟的步：比它小的步都已经算过。
    /// </summary>
    [Serializable]
    public sealed class OutpostRecord
    {
        public string OutpostId;
        /// <summary>所属领地（fg.TbTerritory 的领地 ID；显示“来自哪里”）。</summary>
        public string TerritoryId;
        public int CellX;
        public int CellY;
        public int Garrison;
        public int GarrisonCap;
        /// <summary>增援间隔（模拟步）与下一次增援的步。增援点落在固定网格上（出生步 + k × 间隔），满员时这一次作废。</summary>
        public long ReinforceIntervalTicks;
        public long NextReinforceTick;
        public long ReinforcementsApplied;
        public long SimTick;
        public bool Dormant;
        public long DormantSinceTick = -1;
        public int WakeCount;
        public long LastWakeTick = -1;
        /// <summary>最近一次被惊动的原因（文本键 outpost.wake.*）。</summary>
        public string LastWakeReason = string.Empty;
        /// <summary>始终完整模拟、不休眠（“一直在模拟”的对照组；也留给以后不能休眠的特殊据点）。</summary>
        public bool AlwaysSimulate;
        /// <summary>FG3-GEN-01：据点种类（scout_nest = 家园区侦察巢；空 = FG0-ARCH-06 的测试捷径据点）。</summary>
        public string Kind = string.Empty;
        /// <summary>FG3-GEN-01：等级（侦察巢离核心越远越高，FGR-GEN-032）；0 = 未分级。</summary>
        public int Tier;
        /// <summary>FG6-DEF-04（FGR-GEN-034“摧毁据点会降低这个方向的突袭频率”）：已被摧毁（不再休眠 / 增援 / 作为突袭出发地）。
        /// 唯一写入口 <see cref="WorldSim.WorldOutpostSystem.MarkDestroyed"/>（交战摧毁由 FG8-EXP-03 调用）。</summary>
        public bool Destroyed;
        public long DestroyedTick = -1;

        // ── FG8-EXP-03（FGR-EXP-022）：增援停止、迎战、重建。增援停止 / 重建由 WorldOutpostSystem 写，迎战字段（Engaged / EngageKey / UnfoldedCount / LastContactTick / EngageTick）由 TerritoryControlService 写。只加字段：旧档没有 = 都是默认值。──
        /// <summary>阵营首领被击败（领地平定）后不再增援：驻军不再按增援点增长（增援点照常前进，补算逐位一致）。</summary>
        public bool ReinforceStopped;
        /// <summary>驻军已就地展开成家园战斗内核里的敌人（己方机器走近）；展开期间驻军不增长。</summary>
        public bool Engaged;
        /// <summary>迎战时内核里这一组敌人的队伍键（领地控制服务分配，确定；0 = 还没分配）。</summary>
        public int EngageKey;
        public int UnfoldedCount;
        public long LastContactTick = -1;
        /// <summary>上一次展开 / 僵持收拢的步（僵持收拢后 territory.engage.max_seconds 内不再展开）。</summary>
        public long EngageTick = -1;
        /// <summary>被摧毁过几次 / 被阵营增援重建过几次（掉落事务号、统计）。</summary>
        public int DestroyCount;
        public int RebuildCount;
        /// <summary>生成时带巡逻的据点：巡逻折返半径（格；重建时按它重新派巡逻）。−1 = 不派巡逻；0 = FG8-EXP-03 之前的旧档（补域时按现存巡逻的折返点补回，没有巡逻 = −1）。</summary>
        public int PatrolRadius;
        /// <summary>复审 P1：这次摧毁记进突袭导演哪个方位（0～7；−1 = 没记——没被摧毁、精英残部、旧档）。重建时按它减回，同一据点同一时刻最多只算一次，
        /// “摧毁 → 重建 → 再摧毁”不会无上限地压低突袭频率。</summary>
        public int RaidOctant = -1;
    }

    /// <summary>
    /// FG8-EXP-03（FGR-EXP-022）：领地控制——每个阵营领地一条（控制度、敌控 / 平定中 / 已收复、首领是否被击败、增援 / 精英残部 / 骚扰的下一次步）。
    /// 唯一写入口 <see cref="WorldSim.TerritoryControlService"/>。挂在 <see cref="RaidState.Territory"/>：据点本身已在 <see cref="RaidState.Outposts"/>，
    /// 地形 / 污染变化走区块差异（FGR-GEN-060 只记被改过的区块），这里只放每个领地一条的小记录（不另建存档格式）。
    /// </summary>
    [Serializable]
    public sealed class TerritoryControlState
    {
        public TerritoryControlRecord[] Records = Array.Empty<TerritoryControlRecord>();
        /// <summary>迎战队伍键的下一个序号（键 = <c>TerritoryControlService.KeyBase</c> + 序号，与突袭队伍键不重叠）。</summary>
        public int NextEngageSerial = 1;
        /// <summary>精英残部的下一个序号（据点 ID = remnant:领地:序号）。</summary>
        public int NextRemnantSerial = 1;
        /// <summary>统计：玩家摧毁的据点 / 歼灭的巡逻 / 击溃的精英残部、阵营重建的据点、派出的骚扰部队。</summary>
        public int OutpostsDestroyed;
        public int PatrolsRouted;
        public int RemnantsRouted;
        public int OutpostsRebuilt;
        public int HarassSent;
    }

    /// <summary>FG8-EXP-03：一个阵营领地的控制状态。</summary>
    [Serializable]
    public sealed class TerritoryControlRecord
    {
        public string TerritoryId = string.Empty;
        /// <summary>0 = 敌控；1 = 平定中（首领被击败，不再增援）；2 = 已收复（控制度低于阈值：巡逻撤离、可以放心建设，精英残部定期刷新）。</summary>
        public int Status;
        /// <summary>控制度 0～100（活跃的领地主力据点等级之和 ÷ 全部领地主力据点等级之和；没有任何领地据点 = 100、不会变）。派生量，存档只为显示与通知。</summary>
        public int Control = 100;
        public bool LeaderDefeated;
        public long LeaderDefeatedTick = -1;
        public long ReclaimedTick = -1;
        /// <summary>下一次阵营增援（敌控时；固定网格 k × 间隔）与已经增援过几次。</summary>
        public long NextReinforceTick = -1;
        public int Reinforcements;
        /// <summary>下一次精英残部刷新（已收复后）与当前这一批的据点 ID（空 = 没有活着的）。</summary>
        public long NextEliteTick = -1;
        public string RemnantId = string.Empty;
        public int EliteWaves;
        /// <summary>下一次骚扰检查与最近一支骚扰部队的队伍 ID（还在途中 / 攻城时不再派）。</summary>
        public long NextHarassTick = -1;
        public string HarassGroupId = string.Empty;
    }

    /// <summary>
    /// FG0-ARCH-06：据点派出的一支巡逻（聚合体）。沿寻路路线在据点与折返点之间来回走；进度按定点数（千分之一格）记，
    /// 每步前进 <see cref="SpeedMilliPerTick"/>，休眠补算 = 一次乘法，与逐步累加逐位相同。
    /// </summary>
    [Serializable]
    public sealed class PatrolRecord
    {
        public string PatrolId;
        public int Serial;
        public string OutpostId;
        public int UnitCount;
        public int TurnX;
        public int TurnY;
        /// <summary>去程路线（格坐标，首项 = 据点格）；回程按原路返回。</summary>
        public int[] RouteX = Array.Empty<int>();
        public int[] RouteY = Array.Empty<int>();
        /// <summary>一个来回的长度（千分之一格）。</summary>
        public long LoopMilli;
        public long ProgressMilli;
        public int SpeedMilliPerTick;
        /// <summary>0 = 需要路线，1 = 等寻路结果，2 = 路线就绪，3 = 寻路失败（原地驻守）。</summary>
        public int RouteState;
        public int NavSerial;
        public int NavReason;
        /// <summary>路线交到的那一步（从这一步起按路线前进）。</summary>
        public long RouteReadyTick = -1;
        public double PosX;
        public double PosY;

        // ── FG8-EXP-03：巡逻迎战（己方机器走近时就地展开；全灭 = 巡逻被歼灭，从列表里移除）。只加字段。──
        public bool Engaged;
        public int EngageKey;
        public int UnfoldedCount;
        public long LastContactTick = -1;
        public long EngageTick = -1;
    }

    /// <summary>
    /// FG0-ARCH-06：星球表面寻路内核的待处理状态——排队的请求、已算好还没到采纳步的结果、存档那一刻还没同步的格网变化。
    /// 唯一写入口是 <c>GameLogic.Campaign.Nav.NavService.WriteTo</c>；格网本身不在这里（读档时按种子 + 区块差异 + 建筑重建）。
    /// </summary>
    [Serializable]
    public sealed class NavState
    {
        public int DomainVersion = 1;
        public string SurfaceId = string.Empty;
        /// <summary>base64 二进制快照（BinGames.Sim.Nav.NavKernel.SerializePending；魔数、格式版本、校验和）。空 = 没有待处理的寻路。</summary>
        public string Payload = string.Empty;
    }

    /// <summary>队伍种类。</summary>
    public enum TransitGroupKind
    {
        Raid = 0,
    }

    /// <summary>队伍状态。</summary>
    public enum TransitGroupState
    {
        Marching = 0,
        Arrived = 1,
        /// <summary>FG6-DEF-04（FGR-DEF-032 时间上限）：沿原路撤回出发地（攻城行为 FG6-DEF-05 之前，到达后在外围停留 raid.time_limit_hours 后撤退）。</summary>
        Retreating = 2,
    }

    /// <summary>FG0-ARCH-01：星球表面上的一支行进中的队伍（聚合体：只存人数与位置，逐单位模拟在 FG0-ARCH-03 的战斗内核）。
    /// 位置用双精度格坐标（格心在整数处），远离原点也不丢精度（FGR-GEN-051）。</summary>
    [Serializable]
    public sealed class TransitGroupRecord
    {
        public string GroupId;
        public TransitGroupKind Kind;
        public TransitGroupState State;
        /// <summary>出发地（领地 ID，如 silent；显示"来自哪里"与 B25 种子无关性证明用）。</summary>
        public string OriginId;
        public int UnitCount;
        public double PosX;
        public double PosY;
        public double TargetX;
        public double TargetY;
        /// <summary>行进速度（格 / 游戏秒）。</summary>
        public float Speed;
        public long DispatchedAtTick;
        /// <summary>到达时的模拟步（未到达为 -1）。</summary>
        public long ArrivedAtTick = -1;
        /// <summary>FG0-ARCH-06：寻路请求的键（= 队伍序号）。</summary>
        public int NavKey;
        /// <summary>FG0-ARCH-06：0 = 需要路线，1 = 等寻路结果，2 = 沿路线走，3 = 寻路失败。</summary>
        public int RouteState;
        public int NavSerial;
        /// <summary>寻路失败 / 部分路线的原因（BinGames.Sim.Nav.NavFailReason 数值）；0 = 无。</summary>
        public int NavReason;
        /// <summary>沿地形的路线（格坐标路点，不含出发格）与下一个要走向的路点下标。</summary>
        public int[] RouteX = Array.Empty<int>();
        public int[] RouteY = Array.Empty<int>();
        public int RouteIndex;
        /// <summary>FG6-E2E-01：地形变化时查出路线后面某段被挡（不是脚下这一段）——走到这个路点下标（RouteIndex 增到它）时复查下一段，还挡着就从那里重新要路线；-1 = 没有。
        /// 旧存档没有这个字段，读回来是 -1（照原路走，与修复前一致）。</summary>
        public int ReplanAt = -1;
        /// <summary>到达时通往核心的路是完全堵住的（停在最近处；攻城在 FG6-DEF-05）。</summary>
        public bool Blocked;
        // ── FG6-DEF-04：突袭导演派出的队伍（测试 / 调试派出的队伍这些字段为空）──
        /// <summary>出发地（双精度格坐标）：撤退时沿原路回到这里。<see cref="HasOrigin"/> = false（FG6-DEF-04 之前的存档）时撤退直接离场。</summary>
        public bool HasOrigin;
        public double OriginX;
        public double OriginY;
        /// <summary>所属突袭计划（<see cref="RaidPlanRecord.PlanId"/>）与波次。</summary>
        public string PlanId = string.Empty;
        public int Wave;
        /// <summary>阵营键（silent / foundry ……）；<see cref="OriginId"/> 是显示用的出发地（导演派出的 = 阵营领地 ID）。</summary>
        public string Faction = string.Empty;
        /// <summary>出发据点 ID（迷雾外来袭为空）与从据点驻军里抽走的人数（撤回时还给据点）。</summary>
        public string OutpostId = string.Empty;
        public int GarrisonTaken;
        /// <summary>编成（fg.TbRaidUnit.id、数量、其中精英）——FG6-DEF-05 到达展开成战斗单位时按它生成（职能、精英标记）。</summary>
        public string[] UnitIds = Array.Empty<string>();
        public int[] UnitCounts = Array.Empty<int>();
        public int[] EliteCounts = Array.Empty<int>();
        /// <summary>目标种类（home / outpost）。</summary>
        public string TargetKind = string.Empty;
        /// <summary>FG8-EXP-03：领地据点派出的骚扰部队（不是突袭导演排的一波：到达不计“已到达波次”、不占“前 3 次突袭自动暂停”名额）。只加字段：旧档 = false。</summary>
        public bool Harass;
        /// <summary>FG6-DEF-05 把到达的队伍展开成战斗单位后置真：撤退由攻城逻辑接管，不再按聚合体的时间上限撤退。</summary>
        public bool Engaged;
        /// <summary>开始撤退的步（-1 = 没有撤退）。</summary>
        public long RetreatTick = -1;
        /// <summary>FG6-DEF-04 复审：行军途中重新要过路线时，之前已经走过的路点（含重新规划那一刻所在的格）：撤退时接在当前路线已走过的部分后面倒着走回去（真正的“原路”）。</summary>
        public int[] TrailX = Array.Empty<int>();
        public int[] TrailY = Array.Empty<int>();
        // ── FG6-DEF-05：攻城（展开成战斗单位之后；聚合体本身停在集结点）──
        /// <summary>展开成内核单位的台数、走回集结点离场的台数（损失 = 展开 − 活着 − 离场）。</summary>
        public int UnfoldedCount;
        public int ExitedCount;
        /// <summary>集结点（格）：展开的位置，也是撤退的终点。</summary>
        public int GatherX;
        public int GatherY;
        /// <summary>已下令撤退（时间上限 / 损失超过阈值）与原因（time / losses / regroup）。</summary>
        public bool SiegeRetreat;
        public string SiegeRetreatReason = string.Empty;
        /// <summary>FG6-DEF-05 复审修复（B11 软锁保底）：下撤退令的步（-1 / 0 = 没有）；满 siege.retreat_max_seconds 还有单位走不回集结点（被困、拆不出去）就按离场收尾。</summary>
        public long SiegeRetreatTick = -1;
        /// <summary>FG6-DEF-05（DEBT-FG6DEF04-03）：行进途中被己方机器拦截、就地展开交战（不是到达家园的攻城）。</summary>
        public bool Intercepted;
        /// <summary>拦截交战中最后一次附近有己方机器的步（附近没人满 siege.regroup_seconds 就收拢继续走）。</summary>
        public long LastContactTick = -1;
        /// <summary>这次拦截开始的步（就地交战满 siege.intercept_max_seconds 判僵持收拢）；僵持收拢后改记收拢的步，这段时间内不再拦。-1 = 没有。</summary>
        public long InterceptTick = -1;
    }

    /// <summary>事件导演（FG10）。</summary>
    [Serializable]
    public sealed class DirectorEventState
    {
        public int DomainVersion = 1;
    }

    /// <summary>任务（FG08）。FG8-QST-01：任务本身的状态在 <see cref="CampaignState.ObjectiveRecords"/>（与 OBJ-01～10 同一份记录，CampaignObjectiveTracker 唯一写入口）；
    /// 这里只放跨任务的簿记。只加字段、不升域版本：旧档读进来是默认值（第一次检查时按已探索区域补登支线地点，不发通知）。</summary>
    [Serializable]
    public sealed class QuestState
    {
        public int DomainVersion = 1;
        /// <summary>已经按已探索区域补登过支线地点（旧档 / 开局探索圆里的支线地点不补发通知）。</summary>
        public bool SideBaselined;
        /// <summary>追踪栏折叠（FG08 第 7 节“追踪栏可以折叠”）。</summary>
        public bool TrackerCollapsed;
        /// <summary>统计（B19）：完成 / 失败 / 放弃 / 重新开始的次数。</summary>
        public int Completed;
        public int Failed;
        public int Abandoned;
        public int Restarted;
    }

    /// <summary>
    /// 常驻规则（FG04 FGR-ECO-030 / 031；FG4-ECO-06）。唯一写入口 <c>Economy.StandingRuleService</c>。
    /// 存：规则本身（七类的设置、启用、优先级、最近触发、冲突与原因）、规则正在“持有”的改动（用来恢复原样与追溯“由规则 R3 触发”）、
    /// 触发日志（只留最近 rules.log_max 条）、等着下一个模拟步处理的事件（建筑被摧毁、远征返回、等材料的自动重建）、下一次定时检查的步。
    /// 只加字段不升版本（ADR FG0-SAVE-01）：旧档读进来这些字段是空的，<see cref="CampaignFgStateDomains.EnsureAll"/> 补空数组并补上默认开启的“远征卸货”。
    /// </summary>
    [Serializable]
    public sealed class StandingRuleState
    {
        public int DomainVersion = 1;
        public StandingRuleRecord[] Rules = Array.Empty<StandingRuleRecord>();
        /// <summary>下一条规则的编号（R1、R2……永不复用）。</summary>
        public int NextSerial = 1;
        public RuleHoldRecord[] Holds = Array.Empty<RuleHoldRecord>();
        public RuleLogRecord[] Log = Array.Empty<RuleLogRecord>();
        public int NextLogSerial = 1;
        public RuleEventRecord[] Pending = Array.Empty<RuleEventRecord>();
        /// <summary>下一次定时检查的统一时钟步（每 rules.check_seconds 游戏秒一次）。</summary>
        public long NextCheckTick;
        /// <summary>有改动（新建 / 修改 / 启停 / 突袭到达）：下一个模拟步立刻检查一次（存档里保留，读档后行为不变）。</summary>
        public bool EvaluateNow;
        /// <summary>新战役 / 旧档第一次读进来时已补过默认规则（“远征卸货”默认开启，FGR-ECO-030）。</summary>
        public bool DefaultsSeeded;
        /// <summary>规则派出的工单编号计数（工单 ID 确定性，不用 GUID）。</summary>
        public int NextOrderSerial = 1;
        /// <summary>FG6-DEF-03：下一个重建区域编号（Z1、Z2……永不复用）。</summary>
        public int NextZoneSerial = 1;
        /// <summary>FG6-DEF-03：玩家刚启用自动重建 / 改了范围 / 圈了或打开了区域——下一个模拟步把已经被摧毁、现在落在范围里的东西补排重建（存档里保留）。</summary>
        public bool RebuildSweep;
    }

    /// <summary>FG6-DEF-03（FGR-DEF-015“开启自动重建规则的区域”）：自动重建规则的一个重建区域（格网矩形，含两端），可以单独开 / 关。</summary>
    [Serializable]
    public sealed class RebuildZoneRecord
    {
        public int Serial;
        public int X0;
        public int Y0;
        public int X1;
        public int Y1;
        public bool Enabled = true;
    }

    /// <summary>一条常驻规则。字段按类型取用（见 <c>StandingRuleService</c> 各类说明）。</summary>
    [Serializable]
    public sealed class StandingRuleRecord
    {
        public int Serial;
        /// <summary>类型（fg.TbRuleKind.id）：stock_keep / supply / war_plan / silent_night / machine_repair / expedition_unload / auto_rebuild。</summary>
        public string Kind = string.Empty;
        public bool Enabled;
        /// <summary>优先级：1 最先执行（同一实体被两条规则要时由小的执行）。</summary>
        public int Priority = 1;
        public string ItemId = string.Empty;
        /// <summary>数值：库存维持 / 阈值补给 = 件数；静默夜预案 = 提前秒数；机器维修 = 伤势百分比。</summary>
        public int Threshold;
        /// <summary>阈值补给：每次送几件。</summary>
        public int Batch;
        /// <summary>库存维持：排产的配方。</summary>
        public string RecipeId = string.Empty;
        /// <summary>建筑目标：库存维持 = 工厂（1 座）；阈值补给 = 补给对象；战时预案 = 要暂停的建筑；静默夜预案 = 储能站；自动重建 = 范围（建筑类型 ID，空 = 全部）。</summary>
        public string[] Targets = Array.Empty<string>();
        /// <summary>机器（LogicId）：静默夜预案 = 回驻防点的机器；机器维修 = 范围（空 = 全部家园机器）。</summary>
        public int[] Machines = Array.Empty<int>();
        /// <summary>静默夜预案 = 驻防点（建筑 ID，空 = 归还核心）；远征卸货 = 卸到哪座仓库（空 = 共用库存）。</summary>
        public string PointId = string.Empty;
        /// <summary>战时预案：维修工作单提到最高优先级。</summary>
        public bool BoostRepair;
        /// <summary>
        /// FG6-DEF-06（承接 DEBT-FG4ECO06-03“阈值补给 / 管线消费者优先级规则”）：战时预案执行期间把全部炮塔 / 陷阱发射器的管线补给优先级提到最高（PipeConst.PriorityMin），
        /// 突袭结束恢复默认（turret.supply.pipe_priority / trap.pipe_priority）。只加字段：旧档 = 关。
        /// </summary>
        public bool BoostDefenseSupply;
        /// <summary>FG8-EXP-05（关闭 DEBT-FG4ECO06-04；FGR-EXP-050“货物按远征卸货规则送到仓库或解析台”）：远征卸货时，需要解析的敌方物品（未解析模块 / 数据核心）
        /// 卸下后直接排进解析台队列（解析台满 / 没建 / 不能用时留在仓库，写明原因）。只加字段：旧档 / 新规则默认关。</summary>
        public bool SendToAnalysis;
        public long LastFiredTick = -1;
        public int FireCount;
        /// <summary>条件现在成立、正在执行（库存维持 / 战时 / 静默夜；用来只在开始与结束时各写一条日志）。</summary>
        public bool Active;
        /// <summary>最近一次冲突：赢的那条规则编号（0 = 没有冲突）与争的实体。</summary>
        public int ConflictWith;
        public string ConflictEntity = string.Empty;
        /// <summary>没能执行的原因（文本键 + 参数；空 = 没有问题）。</summary>
        public string IssueKey = string.Empty;
        public string IssueArg = string.Empty;
        /// <summary>FG6-DEF-03：自动重建的重建区域（空 = 整个家园；有区域 = 只重建开着的区域里的东西）。</summary>
        public RebuildZoneRecord[] Zones = Array.Empty<RebuildZoneRecord>();
    }

    /// <summary>规则正持有的一项改动：恢复原样用的旧值、规则设下的值是否被玩家改掉（被改掉 = 这次不再接管）、对应的工单。</summary>
    [Serializable]
    public sealed class RuleHoldRecord
    {
        public int Rule;
        /// <summary>recipe / disabled / storage / repair_priority / garrison / machine_repair / supply。</summary>
        public string Kind = string.Empty;
        /// <summary>实体键：b:建筑 ID、m:机器 LogicId、o:工单 ID、s:规则编号:建筑 ID。</summary>
        public string EntityId = string.Empty;
        public string Prev = string.Empty;
        public int PrevInt;
        public long SinceTick;
        public bool Overridden;
        public string OrderId = string.Empty;
        /// <summary>规则实际设下的值（库存维持：工厂被切到的配方 ID）。判断“玩家改掉了没有”拿它比，不拿规则现在的设置比——
        /// 玩家在面板改了规则的配方 / 物品不是“手动改动工厂”（审查修复）。空 = 旧持有，按规则当前设置比。</summary>
        public string Applied = string.Empty;
    }

    /// <summary>触发日志的一条（文本键 + 参数，显示时按当前语言翻译；参数里的 @item: / @b: / @m: / @recipe: / @key: 显示时换成名字）。</summary>
    [Serializable]
    public sealed class RuleLogRecord
    {
        public int Serial;
        public long Tick;
        public int Rule;
        public string Key = string.Empty;
        public string[] Args = Array.Empty<string>();
        public string EntityId = string.Empty;
        public bool HasPos;
        public float X;
        public float Y;
    }

    /// <summary>等下一个模拟步处理的事件：destroyed（建筑被摧毁）/ expedition（远征返回，带机器）/ rebuild_retry（自动重建等材料）。</summary>
    [Serializable]
    public sealed class RuleEventRecord
    {
        public string Kind = string.Empty;
        public string EntityId = string.Empty;
        public int[] Machines = Array.Empty<int>();
        public long Tick;
        public int Rule;
    }

    /// <summary>信号核（FG1-SIG-01 / FG01 FGR-SIG-010～012、第 6 章存档）。唯一写入口 <c>Signal.SignalCoreService</c>。
    /// 本 Story 只存槽位与预设；后续 Story 在本域追加：各槽核心固件冷却（FG1-SIG-03）、信号位置与“存档时在机器里”
    /// （FG1-SIG-03）、远距离跳转冷却（FG1-SIG-07）。只加字段不升版本（ADR FG0-SAVE-01）。</summary>
    [Serializable]
    public sealed class SignalCoreState
    {
        public int DomainVersion = 1;
        /// <summary>按槽位顺序（下标 0 = 1 号槽，优先插入接入口）记录装着的固件实例
        /// （<see cref="PrimitiveChipRecord.PartId"/>，该实例 <see cref="PrimitiveChipRecord.State"/> = SignalCore）；
        /// 空槽为空串（不用 null：JsonUtility 会把数组里的 null 读回成空串，两种写法混用会让指纹在第一次存读档后漂移）。
        /// 长度 = 最多槽位数（fg.TbHomeTuning signal.core.max_slots），未解锁的槽位也占位。</summary>
        public string[] SlotPartIds = Array.Empty<string>();
        /// <summary>玩家保存的预设（按 <see cref="SignalCorePresetRecord.PresetId"/> 排序 = 创建顺序）。</summary>
        public SignalCorePresetRecord[] Presets = Array.Empty<SignalCorePresetRecord>();
        /// <summary>最近一次切换 / 保存 / 覆盖的预设；空串 = 当前配置没有对应预设。</summary>
        public string ActivePresetId = string.Empty;
        /// <summary>下一个预设的序号（预设 ID 与默认名称“配置 N”都用它，删除后不复用）。</summary>
        public int NextPresetSerial = 1;

        // ── FG1-SIG-03（FGR-SIG-002、030～033；FG01 第 6 章“存档内容：信号位置……核心固件冷却”）──
        // 只加字段、不升域版本：旧档缺这些字段时读回 0 / 空串 / 空数组 = “信号在归还核心、没有冷却”，正是旧档的真实含义。

        /// <summary>信号现在在哪台机器里（<see cref="MachineRecord.LogicId"/>）；0 = 在归还核心。唯一写入口是
        /// <c>SignalUplinkService</c>（接入完成、离开、阵亡回弹、断链、地点卸载都经它）。接入过渡中的“目标”不写这里（过渡没完成，信号还在原处）。</summary>
        public int UplinkMachineLogicId;
        /// <summary>接入的机器所在的地点（区域 ID）；在归还核心时为空串。</summary>
        public string UplinkSiteId = string.Empty;
        /// <summary>核心固件的冷却（FGR-SIG-033：冷却属于信号，跳到别的机器不会重置）。按固件内容 ID，存“到哪个游戏秒可以再发动”
        /// （<c>GameClock.GameSeconds</c>，暂停不走、倍速按游戏时间走）；已到期的条目在下一个模拟步里清掉。</summary>
        public SignalCoreCooldownRecord[] CoreCooldowns = Array.Empty<SignalCoreCooldownRecord>();

        // ── FG1-SIG-04（FGR-SIG-041 安全模式）── 只加字段、不升域版本：旧档缺这个字段读回空数组 = 没有机器在安全模式里。

        /// <summary>因断链（干扰 / 走出覆盖 / 静默夜）失去信号、处于安全模式的机器（按 LogicId 升序）。唯一写入口 <c>SignalLinkService</c>。</summary>
        public SignalSafeModeRecord[] SafeModes = Array.Empty<SignalSafeModeRecord>();

        /// <summary>旧档字段（FG1-SIG-06 起存游戏秒）：只在读档迁移时读一次，换成 <see cref="RawChargeReadyTick"/> 后清零；新代码不读写。</summary>
        public double RawChargeReadyAtGameSeconds;

        /// <summary>FG1-SIG-06（FGR-SIG-061）：常规裸跑固件下一次“发动”能计暴露的统一时钟步（0 = 随时）。记在信号上：跳到别的机器不重置（防刷）。
        /// FG1-E2E-01（DEBT-FG1SIG07-05）起存整数步（旧档的游戏秒由 <c>SignalTimeMigration</c> 换算）。唯一写入口 <see cref="Signal.RawFirmwareService"/>。</summary>
        public long RawChargeReadyTick;

        // ── FG1-SIG-07（FGR-SIG-051、052；FG01 第 6 章“远距离跳转冷却”）── 只加字段、不升域版本：旧档读成 0 / 空数组 = 没有冷却、没有上一台。

        /// <summary>远距离跳转冷却到统一时钟的哪一步结束（<c>GameClock.Ticks</c>；0 = 没有冷却）。存整数步而不是游戏秒：
        /// 游戏秒是 1/60 的倍数，存成浮点经 JsonUtility 读回会差 1 ulp，存读档往返逐字段对照就会不一致。唯一写入口 <c>SignalUplinkService</c>。</summary>
        public long JumpCooldownReadyTick;
        /// <summary>最近接入过的机器（LogicId，去重、最新在前，最多 4 台）：“跳回上一台机器”取第一台不是当前这台的。</summary>
        public int[] RecentUplinks = Array.Empty<int>();

        // ── FG1-HUD-01（FGR-SIG-082 机器经历“与信号同行”累计时长）── 只加字段、不升域版本：旧档读成 0 = 从读档那一刻起开始计这一段。

        /// <summary>信号进入当前这台机器的统一时钟步（<c>GameClock.Ticks</c>）；信号在归还核心时无意义。离开时这一段并进那台机器的
        /// <see cref="MachineRecord.SignalUplinkTicks"/>。唯一写入口 <c>SignalUplinkService.SetUplink</c>。</summary>
        public long UplinkSinceTick;

        // ── FG4-ECO-11（FGR-ECO-020 超控阵列）── 只加字段、不升域版本：旧档读成 0 = 上次看到的超控阵列等级为 0（没有阵列）。

        /// <summary>上一次电网结算后看到的“已建成的超控阵列等级”与“生效的等级”（<c>OverrideArrayService.OnPowerApplied</c> 唯一写入）。
        /// 只用来判断“这一次是不是真的变了”——失效 / 恢复的通知与引导钩子只在真正翻转时发，读档后不重复提醒。</summary>
        public int OverrideBuiltTierSeen;
        public int OverrideActiveTierSeen;
    }

    /// <summary>FG1-SIG-04：一台处于安全模式的机器（只运行本地常规固件，接入口为空，继续执行最后一条命令或 AI 教义）。</summary>
    [Serializable]
    public sealed class SignalSafeModeRecord
    {
        public int LogicId;
        /// <summary>进入安全模式的原因（<c>SignalLinkBreakReason</c> 的数值：1 走出覆盖 / 2 干扰场 / 3 静默夜）。</summary>
        public int Reason;
        /// <summary>旧档字段（存游戏秒）：只在读档迁移时读一次，换成 <see cref="SinceTick"/> 后清零；新代码不读写。</summary>
        public double SinceGameSeconds;
        /// <summary>旧档字段（存游戏秒，-1 = 条件仍在）：只在读档迁移时读一次，换成 <see cref="ClearSinceTick"/> 后置 -1；新代码不读写。</summary>
        public double ClearSinceGameSeconds = -1;
        /// <summary>进入安全模式的统一时钟步（FG1-E2E-01 起存整数步，DEBT-FG1SIG07-05）。</summary>
        public long SinceTick;
        /// <summary>断链条件从哪一步起已经消失（持续 signal.safe_mode.exit_seconds 后退出安全模式）；-1 = 条件仍在。</summary>
        public long ClearSinceTick = -1;
    }

    /// <summary>FG1-SIG-03：一条核心固件冷却（信号侧，不属于任何机体）。</summary>
    [Serializable]
    public sealed class SignalCoreCooldownRecord
    {
        public string ContentId = string.Empty;
        /// <summary>旧档字段（存游戏秒）：只在读档迁移时读一次，换成 <see cref="ReadyTick"/> 后清零；新代码不读写。</summary>
        public double ReadyAtGameSeconds;
        /// <summary>到统一时钟的这一步（<c>GameClock.Ticks</c>）冷却结束。FG1-E2E-01 起存整数步（DEBT-FG1SIG07-05）。</summary>
        public long ReadyTick;
    }

    /// <summary>信号核预设（FG01 第 4 章“保存为预设、一键切换（只能在家园切换）”）。按槽位记固件内容 ID，
    /// 不记实例：实例会被消耗、拆解或换位置，切换时按内容从信号核与基元仓里找实例。</summary>
    [Serializable]
    public sealed class SignalCorePresetRecord
    {
        public string PresetId = string.Empty;
        public string Name = string.Empty;
        /// <summary>按槽位顺序的固件内容 ID（<see cref="PrimitiveChipRecord.CardDefId"/>），空槽为空串。</summary>
        public string[] SlotContentIds = Array.Empty<string>();
    }

    /// <summary>掉落数据包（FG08）。FG8-LOOT-01 起记野外普通掉落的确定性序号与地上敌方物品的身份；三选一数据包由 FG8-LOOT-02 加在这里。</summary>
    [Serializable]
    public sealed class LootPacketState
    {
        public int DomainVersion = 1;
        /// <summary>FG8-LOOT-01：野外击毁了第几个敌人（掉落按“世界种子 + 这个序号 + 行号”确定；读档后接着编，存读档前后同一结果）。</summary>
        public long FieldKills;
        /// <summary>FG8-LOOT-01：第几个敌方据点被摧毁（据点掉落的确定性序号）。</summary>
        public long OutpostLoots;
        /// <summary>FG8-LOOT-01：地上（或正被机器搬着）的敌方加密物品的身份——搬进家园时按它登记（加密固件变成固件库里的未破解芯片，其余进仓库并登记解析身份）。
        /// 条数上限 fg.TbHomeTuning loot.tags_max（超出丢最早的身份，物品本身不丢）。唯一写入口 <see cref="Economy.FieldLootService"/>。</summary>
        public LootGroundTagRecord[] GroundTags = Array.Empty<LootGroundTagRecord>();
        /// <summary>统计：野外掉落生成的地面堆数、其中敌方加密物品件数、拆解钳多回收的废料 / 残骸件数。</summary>
        public int TotalDrops;
        public int TotalEnemyItems;
        public int ClawBonusItems;

        // ── FG8-LOOT-02 三选一与保底（FGR-LOOT-020～022）。只加字段、不升域版本：旧档没有 = 空 / 0。唯一写入口 Economy.LootPickService。──
        /// <summary>待分拣的数据包（携带着的 + 留在地上的）。分拣后移走；数据包永不消失（D-13，<see cref="Economy.KeyItemRetention"/>），没有条数上限。</summary>
        public LootPackageRecord[] Packets = Array.Empty<LootPackageRecord>();
        /// <summary>在野外战略暂停中分拣、装进机器货舱的物品（回到家园按“远征卸货”规则卸下；机器阵亡时掉在原地）。</summary>
        public LootHeldRecord[] Held = Array.Empty<LootHeldRecord>();
        /// <summary>三选一的选择记录（最近 loot.pick.log_max 条；远征结算 FG8-EXP-05 读）。</summary>
        public LootPickLogRecord[] Picks = Array.Empty<LootPickLogRecord>();
        /// <summary>见过的候选（种类 + 物品 + 身份的键）：从没见过的候选标“新”。</summary>
        public string[] SeenKeys = Array.Empty<string>();
        /// <summary>下一个数据包的序号（候选按“世界种子 + 序号 + 候选位”确定；读档后接着编）。</summary>
        public long NextPacketSerial = 1;
        /// <summary>保底计数：连续多少个数据包没有出现稀有及以上（≥ loot.pick.pity 时下一个必有一个稀有位）。</summary>
        public int NoRareStreak;
        /// <summary>统计：生成 / 分拣的数据包数、保底触发次数、没选的候选转成的技术数据。</summary>
        public int PacketsMade;
        public int PacketsResolved;
        public int PityHits;
        public long LeftoverTech;

        // ── FG8-LOOT-03 货舱、自动拾取、阵亡掉落（FGR-LOOT-030、FGR-EXP-032）。只加字段、不升域版本：旧档没有 = 空 / 0（全部物品开着自动拾取）。唯一写入口 Economy.CargoService。──
        /// <summary>关掉自动拾取的物品（fg.TbEcoItem.id，升序）。默认全部开着；关键材料始终拾取、不在这里。进存档：自动拾取影响世界结果，读档 / 换机器后一致。</summary>
        public string[] AutoPickupOff = Array.Empty<string>();
        /// <summary>自动拾取 / 拆开转移生成的货舱记录序号（"cargo:" + 序号；确定性、读档后接着编）。</summary>
        public long NextCargoSerial = 1;
        /// <summary>玩家刚丢弃的货（回收实例 ID）：附近还有队员时不被自动拾取捡回去，队员都走开（loot.cargo.discard_clear_radius 格外）后清掉。条数上限 loot.cargo.discard_max。</summary>
        public string[] CargoDiscarded = Array.Empty<string>();
        /// <summary>已经提示过“快满 / 已满”的机器与提示到的级别（1 = 快满、2 = 已满；一一对应）。降下来就移走，之后再满再提示。</summary>
        public int[] CargoWarned = Array.Empty<int>();
        public int[] CargoWarnLevels = Array.Empty<int>();
        /// <summary>统计：自动拾取的份数、机器之间转移的次数、丢弃的次数。</summary>
        public int CargoAutoPicked;
        public int CargoTransfers;
        public int CargoDiscards;
    }

    /// <summary>FG8-LOOT-02：一个待分拣数据包。</summary>
    [Serializable]
    public sealed class LootPackageRecord
    {
        public string PacketId = string.Empty;
        public long Serial;
        /// <summary>来源：elite（精英被击毁）/ event（事件完成，FG10）/ boss（首领，另加的一个）。</summary>
        public string Source = string.Empty;
        /// <summary>阵营键（候选池 = 该阵营池 + 通用池；空 = 只用通用池）。</summary>
        public string Faction = string.Empty;
        public long Tick;
        /// <summary>携带它的机器（LogicId；0 = 在地上）。数据包跟着远征队走、不占货舱（FGR-LOOT-022）。</summary>
        public int Holder;
        /// <summary>在地上时的地点与位置（格）。地上的数据包永不消失，己方机器走近就捡起来。</summary>
        public string RegionId = string.Empty;
        public float X;
        public float Y;
        /// <summary>这一包是保底包（有一个候选位强制抽稀有及以上）。</summary>
        public bool Pity;
        public LootCandidateRecord[] Candidates = Array.Empty<LootCandidateRecord>();
    }

    /// <summary>FG8-LOOT-02：数据包里的一个候选（生成时就定下，存读档前后一致）。</summary>
    [Serializable]
    public sealed class LootCandidateRecord
    {
        /// <summary>fg.TbLootPick.id。</summary>
        public string RowId = string.Empty;
        /// <summary>item / firmware / firmware_cracked / module / core。</summary>
        public string Kind = string.Empty;
        public string ItemId = string.Empty;
        /// <summary>身份：固件 ID / 组件 ID / 背景资料 ID；普通物品为空。</summary>
        public string TargetId = string.Empty;
        public int Amount;
        /// <summary>common / fine / rare / prototype。</summary>
        public string Tier = string.Empty;
    }

    /// <summary>FG8-LOOT-02：野外分拣后装在某台机器货舱里的一件（一份）物品。</summary>
    [Serializable]
    public sealed class LootHeldRecord
    {
        /// <summary>唯一 ID（"pick:" + 数据包 ID）：入库幂等、掉在地上时当回收实例 ID 用。</summary>
        public string HeldId = string.Empty;
        public int Machine;
        public string Kind = string.Empty;
        public string ItemId = string.Empty;
        public string TargetId = string.Empty;
        public int Amount;
        public int Space;
        public string Faction = string.Empty;
    }

    /// <summary>FG8-LOOT-02：一次三选一的选择记录。</summary>
    [Serializable]
    public sealed class LootPickLogRecord
    {
        public string PacketId = string.Empty;
        public string Source = string.Empty;
        public string Faction = string.Empty;
        public string ChosenRowId = string.Empty;
        public string ChosenTier = string.Empty;
        /// <summary>depot = 直接入库；cargo = 装进货舱。</summary>
        public string Destination = string.Empty;
        public int TechData;
        public long Tick;
        /// <summary>FG8-EXP-05 修复第 2 轮：分拣时数据包的携带机器（0 = 地上 / 旧档）。远征结算只收本次成员做的三选一。</summary>
        public int Holder;
    }

    /// <summary>FG8-LOOT-01：地上一件带身份的敌方加密物品（按地面物的回收实例 ID 对应）。</summary>
    [Serializable]
    public sealed class LootGroundTagRecord
    {
        public string SalvageId = string.Empty;
        public string ItemId = string.Empty;
        /// <summary>身份：加密固件 = 固件 ID；未解析模块 = 组件 / 模块 ID；数据核心 = fg.TbAnalysisLore.id。</summary>
        public string TargetId = string.Empty;
        /// <summary>来源（field = 野外击毁、outpost = 据点被摧毁）与阵营键。</summary>
        public string Origin = string.Empty;
        public string Faction = string.Empty;
        public long Tick;
    }

    /// <summary>统计（FG15 统计面板 / FG16 试玩数据）。</summary>
    [Serializable]
    public sealed class StatsState
    {
        public int DomainVersion = 1;
        /// <summary>FG2-FW-04（FGR-FW-043“伤害归因：按反应统计每次远征和每次突袭的伤害占比”）：最近若干场远征 / 突袭的反应伤害归因
        /// （唯一写入口 <see cref="Combat.ReactionAttribution"/>；条数上限 fg.TbUiTuning reaction.attribution_sessions，超出丢最旧的已结束场次）。
        /// 统计面板（FG4-ECO-08）与离家报告（FG4-ECO-09 / FG6-DEF-08）读这里。</summary>
        public ReactionSessionRecord[] ReactionSessions = Array.Empty<ReactionSessionRecord>();
        /// <summary>下一场归因的序号（场次 ID = "rs-" + 序号，确定性、读档后接着编）。</summary>
        public int NextReactionSessionSerial = 1;
        /// <summary>FG4-ECO-08（FGR-ECO-050 / 051；FG04 第 6 节“统计（按时间窗口聚合保存）”）：生产统计（四个时间窗口的产量 / 消耗桶、持续赤字）与资源顶栏的固定物品。
        /// 唯一写入口 <see cref="Economy.ProductionStats"/> / <see cref="Economy.ResourcePins"/>。旧存档没有 = 空（读档时补空域，从读档那一刻开始统计）。</summary>
        public ProductionStatsState Production = new ProductionStatsState();
        /// <summary>FG4-ECO-09（FGR-ECO-060 离家报告；FG04 第 6 节“离家报告（最近 3 份）”）：正在进行的那份（远征出发成功时开、撤离 / 放弃结算时关）与最近几份已结算的报告。
        /// 唯一写入口 <see cref="Economy.AwayReportService"/>。旧存档没有 = 空（没有报告；读档时正在远征的旧档从读档那一刻开一份，不伪造之前的记录）。</summary>
        public AwayReportState AwayReports = new AwayReportState();
    }

    /// <summary>FG4-ECO-09：离家报告域。</summary>
    [Serializable]
    public sealed class AwayReportState
    {
        /// <summary>有一份正在记的报告（远征在外）。JsonUtility 不存 null，所以用标志位 + 常驻对象。</summary>
        public bool HasOpen;
        public AwayReportRecord Open = new AwayReportRecord();
        /// <summary>已结算的报告，旧的在前、最新的在最后（只保留 away.reports_keep 份）。</summary>
        public AwayReportRecord[] Reports = Array.Empty<AwayReportRecord>();
        /// <summary>下一份报告的序号（确定性，读档后接着编）。</summary>
        public int NextSerial = 1;
    }

    /// <summary>FG4-ECO-09：一份离家报告。所有数量都在事情发生的那一刻累加（与观察无关，FGR-BASE-021），结算时一次性补上“开始 / 结束差额”类的读数。</summary>
    [Serializable]
    public sealed class AwayReportRecord
    {
        public int Serial;
        /// <summary>远征的目标地点（区域 ID）。</summary>
        public string RegionId = string.Empty;
        public long StartTick;
        /// <summary>结算的世界步（-1 = 还在进行）。</summary>
        public long EndTick = -1;
        /// <summary>0 = 进行中，1 = 撤离回家，2 = 全灭放弃，3 = 其它方式结束（如读档时远征已不在）。</summary>
        public int Outcome;
        /// <summary>出发时的远征队（机器 LogicId）。</summary>
        public int[] Members = Array.Empty<int>();
        /// <summary>FG4-ECO-09 修复轮：结算时固化的远征队结局（已结算的报告是历史记录，之后名单里的机器再阵亡也不改写它）。
        /// TeamSettled = 已固化；MembersLost = 结算那一刻名单里已阵亡的台数。旧档没有这两项 = 未固化，显示时按此刻的机器状态现算。</summary>
        public bool TeamSettled;
        public int MembersLost;
        /// <summary>生产：离家期间的产出 / 消耗（与统计面板同一记账口径；固体按件，流体按毫升）。</summary>
        public ItemAmountRecord[] Produced = Array.Empty<ItemAmountRecord>();
        public ItemAmountRecord[] Consumed = Array.Empty<ItemAmountRecord>();
        /// <summary>瓶颈：结算时从各生产建筑的离家缺料计数汇总（物品 + 建筑 + 世界步数，缺得最久的在前）。</summary>
        public AwayStarveRecord[] Starve = Array.Empty<AwayStarveRecord>();
        /// <summary>电力：停电段（有建筑因缺电停机的连续时间）。</summary>
        public AwayOutageRecord[] Outages = Array.Empty<AwayOutageRecord>();
        public int OutagesDropped;
        /// <summary>超出条数上限的那一段停电还没恢复（只计数、不逐条记时用它判断“同一段”）。</summary>
        public bool OutageOverflowOpen;
        /// <summary>结算时最后一段停电还没恢复（显示“到结算时还没恢复”）。</summary>
        public bool OutageOngoingAtEnd;
        /// <summary>电力：离家期间每游戏秒累加的发电 / 需要 / 实际供上（电 × 秒）、缺电秒数、记了多少秒、各发电类别（电 × 秒）、储能充入 / 放出（电 × 秒）。</summary>
        public long PowerSeconds;
        public double SupplySum;
        public double DemandSum;
        public double DeliveredSum;
        public long ShortSeconds;
        public double[] ClassSum = Array.Empty<double>();
        public double ChargedSum;
        public double DischargedSum;
        /// <summary>出发时的累计读数（结算时求差）：燃油累计（毫升）、燃油耗尽次数、管线四项累计（毫升）、清带送回 / 丢弃、分流器累计分出。</summary>
        public long BaseFuelMl;
        public long BaseFuelOuts;
        public long BasePumpedMl;
        public long BaseDeliveredMl;
        public long BaseFlushedMl;
        public long BaseRemovedMl;
        public long BaseCleared;
        public long BaseDiscarded;
        public long BaseSplit;
        /// <summary>传送带各端口累计收下（送达建筑）/ 推出（推上传送带）之和。</summary>
        public long BaseBeltIn;
        public long BaseBeltOut;
        /// <summary>结算时求出的差额（进行中的报告显示时现算）。</summary>
        public long FuelMl;
        public long FuelOuts;
        public long PumpedMl;
        public long DeliveredMl;
        public long FlushedMl;
        public long RemovedMl;
        public long Cleared;
        public long Discarded;
        public long Split;
        public long BeltIn;
        public long BeltOut;
        /// <summary>机器变化：离家期间受伤 / 阵亡的机器（家园与远征队）。</summary>
        public AwayMachineRecord[] Machines = Array.Empty<AwayMachineRecord>();
        public int MachinesDropped;
        /// <summary>事件：离家期间发生、按 fg.TbNotifyType.awaySection 记进报告的通知（突袭、天气、事件、研究、建筑受损、电力事件…）。</summary>
        public AwayEntryRecord[] Entries = Array.Empty<AwayEntryRecord>();
        public int EntriesDropped;
        /// <summary>常驻规则：离家期间的规则动作（与规则触发日志同一条记录；FG-GAP-098）。</summary>
        public RuleLogRecord[] Rules = Array.Empty<RuleLogRecord>();
        public int RulesTotal;
        /// <summary>远征的反应伤害归因场次（FG2-FW-04；GAP-055 远征段）。空 = 没有。</summary>
        public string ReactionSessionId = string.Empty;
        /// <summary>FG8-EXP-05（FGR-EXP-050“暴露变化”）：出发时的暴露值与游戏秒（旧档的报告没有 = HasStart 假，结算时不写暴露变化）。</summary>
        public bool HasStart;
        public float ExposureAtStart;
        public float PlaySecondsAtStart;
        /// <summary>FG8-EXP-05：远征结算（回到家园 / 带仓库的前哨站、投送舱接回、全灭时写；JsonUtility 不存 null，用标志位 + 常驻对象）。
        /// 远征途中带回的关键物在带回那一刻就记进 <see cref="ExpeditionSettlementRecord.Items"/>（与观察无关，FGR-BASE-021）。</summary>
        public bool HasSettlement;
        public ExpeditionSettlementRecord Settlement = new ExpeditionSettlementRecord();
    }

    /// <summary>FG8-EXP-05（FGR-EXP-050 远征结算）：一次远征的结算。唯一写入口 <c>Economy.ExpeditionSettlementService</c>。</summary>
    [Serializable]
    public sealed class ExpeditionSettlementRecord
    {
        /// <summary>怎么结束的：foot = 走回家园 / 带仓库的前哨站；pod = 投送舱接回；wiped = 全灭（行军途中 / 远征地点里）。</summary>
        public string Via = string.Empty;
        /// <summary>带回的物品（货舱里的、携带的关键物 / 黑匣子 / 遗产、数据包）与去处。</summary>
        public SettleItemRecord[] Items = Array.Empty<SettleItemRecord>();
        /// <summary>这次远征里做的三选一（<see cref="LootPacketState.Picks"/> 里出发之后的那些）。</summary>
        public LootPickLogRecord[] Picks = Array.Empty<LootPickLogRecord>();
        /// <summary>远征期间完成 / 失败的任务（ObjectiveRecord.ObjectiveId 与结局）。</summary>
        public SettleQuestRecord[] Quests = Array.Empty<SettleQuestRecord>();
        /// <summary>远征期间生成的熔合线索（<see cref="FusionClueRecord.Serial"/>）。</summary>
        public int[] Clues = Array.Empty<int>();
        public float ExposureStart;
        public float ExposureEnd;
        public bool HasExposure;
        /// <summary>机器经历：每台的结局与累计远征次数（结算那一刻固化）。</summary>
        public SettleMachineRecord[] Machines = Array.Empty<SettleMachineRecord>();
        /// <summary>没带走、留在原地的东西（普通战利品带回收倒计时；关键物 / 黑匣子 / 遗产 / 数据包永不消失）。</summary>
        public SettleLeftRecord[] Left = Array.Empty<SettleLeftRecord>();
        public int LeftMore;
        /// <summary>“远征卸货”规则关着：货留在机器货舱里（FGR-BASE-020，写明原因）。</summary>
        public bool CargoKept;
        /// <summary>数据包：带回 / 留在原地的个数（带回的回家后分拣）。</summary>
        public int PacketsHome;
        /// <summary>
        /// 修复第 3 轮（审查 P1）：远征还没结束时就随投送舱落回投送站的成员（分批接回）。它们的货舱货与携带的关键物在落地那一刻记进 <see cref="Items"/>，
        /// 远征结束时的结算不再重复记它们的货（只记机器经历与数据包）。旧档没有这个字段 = 空。
        /// </summary>
        public int[] Landed = Array.Empty<int>();
    }

    /// <summary>FG8-EXP-05：带回的一份东西。</summary>
    [Serializable]
    public sealed class SettleItemRecord
    {
        /// <summary>cargo = 货舱里分拣 / 拾取的货（Kind 写在 <see cref="CargoKind"/>）；key = 区域关键物；box = 黑匣子；heritage = 人类遗产。</summary>
        public string Kind = string.Empty;
        public string CargoKind = string.Empty;
        /// <summary>物品 ID（货）或区域关键物的内容 ID / 回收实例 ID。</summary>
        public string ItemId = string.Empty;
        public string TargetId = string.Empty;
        public int Amount;
        /// <summary>去处：warehouse（仓库 / 共用库存）/ firmware（固件库）/ analysis（解析台队列）/ analysis_wait（进仓库，等解析）/ cargo（留在货舱里）/
        /// pod（随投送舱返回，落地后卸货）/ vault（核心保管库）/ gallery（陈列馆）/ recovered（已带回）。</summary>
        public string Dest = string.Empty;
        /// <summary>修复第 2 轮（审查 P1）：“送到解析台”的实际结果已回写（卸货那一步按 TryEnqueueItem 的真实结果改 <see cref="Dest"/>）。旧档没有这个字段 = false。</summary>
        public bool Confirmed;
        /// <summary>没能排进解析台的原因码（HomeValleyAnalysis 的失败原因；<see cref="Dest"/> = analysis_wait 时显示）。</summary>
        public string Reason = string.Empty;
    }

    /// <summary>FG8-EXP-05：远征期间的一条任务进度。</summary>
    [Serializable]
    public sealed class SettleQuestRecord
    {
        public string ObjectiveId = string.Empty;
        /// <summary><see cref="ObjectiveState"/>（Completed / Failed）。</summary>
        public int State;
    }

    /// <summary>FG8-EXP-05：一台远征机器的经历更新。</summary>
    [Serializable]
    public sealed class SettleMachineRecord
    {
        public int LogicId;
        public bool Alive;
        /// <summary>结算后这台机器累计完成的远征次数。</summary>
        public int Expeditions;
        public bool Injured;
    }

    /// <summary>FG8-EXP-05：留在原地的一份东西（结算时固化；位置 = 星球格 / 地点格）。</summary>
    [Serializable]
    public sealed class SettleLeftRecord
    {
        /// <summary>loot = 普通战利品；key = 区域关键物 / 关键材料；box = 黑匣子；heritage = 人类遗产；packet = 数据包。</summary>
        public string Kind = string.Empty;
        public string ItemId = string.Empty;
        public int Amount;
        public string RegionId = string.Empty;
        public float X;
        public float Y;
        /// <summary>被敌方回收的统一时钟步（0 = 永不消失）。</summary>
        public long ExpireTick;
    }

    /// <summary>FG4-ECO-09：一座建筑在离家期间缺一种物品的累计步数。</summary>
    [Serializable]
    public sealed class AwayStarveRecord
    {
        public string ItemId;
        public string BuildingId;
        public long Ticks;
        public float X;
        public float Y;
    }

    /// <summary>FG4-ECO-09：一段停电。</summary>
    [Serializable]
    public sealed class AwayOutageRecord
    {
        public long StartTick;
        /// <summary>-1 = 还没恢复。</summary>
        public long EndTick = -1;
        /// <summary>同时缺电停机的建筑最多几座。</summary>
        public int Peak;
        /// <summary>第一座停机的建筑与位置（定位用）。</summary>
        public string BuildingId = string.Empty;
        public float X;
        public float Y;
    }

    /// <summary>FG4-ECO-09：一台机器在离家期间的变化。</summary>
    [Serializable]
    public sealed class AwayMachineRecord
    {
        public int LogicId;
        /// <summary>出事时在远征队里（否则在家园 / 别处）。</summary>
        public bool Expedition;
        public float MinHealth;
        public float MaxHealth;
        public bool Died;
        public long DiedTick;
        public string RegionId = string.Empty;
        public float X;
        public float Y;
        /// <summary>FG4-ECO-09 修复轮：结算时固化的耐久（受伤未阵亡的机器；已结算的报告不随之后的维修 / 再受伤变化）。旧档没有 = 显示时取此刻的耐久。</summary>
        public bool HasEndHealth;
        public float EndHealth;
        public float EndMaxHealth;
    }

    /// <summary>FG4-ECO-09：一条从通知转来的报告条目（细节是文本或文本键，显示时按当前语言解析）。</summary>
    [Serializable]
    public sealed class AwayEntryRecord
    {
        /// <summary>FG8-OUT-01（卡片“前哨站的统计和离家报告分组”）：发生在哪个己方前哨站（记账那一刻按位置认定；空 = 家园 / 不在前哨站）。只加字段。</summary>
        public string OutpostId = string.Empty;
        public string Section = string.Empty;
        public string TypeId = string.Empty;
        public string Detail = string.Empty;
        public long Tick;
        public bool HasLocation;
        public string RegionId = string.Empty;
        public float X;
        public float Y;
        public float Z;
    }

    /// <summary>FG4-ECO-08：生产统计与资源顶栏。</summary>
    [Serializable]
    public sealed class ProductionStatsState
    {
        /// <summary>开始统计的世界步（新游戏 = 0；旧存档 = 读档那一步）。窗口还没满时速率按已统计的时长算（不把没统计的时间当作 0 产量）。-1 = 还没开始。</summary>
        public long StartTick = -1;
        /// <summary>四个时间窗口（1 分钟 / 10 分钟 / 1 小时 / 10 小时）各自的环形桶（桶长与桶数见 eco.stats.*；调参变了整档重建）。</summary>
        public ProdStatTierRecord[] Tiers = Array.Empty<ProdStatTierRecord>();
        /// <summary>正在赤字（消耗大于产出）的物品与开始时刻；超过 eco.stats.deficit_minutes 发警告一次（<see cref="DeficitRecord.Warned"/>），恢复后删掉。</summary>
        public DeficitRecord[] Deficits = Array.Empty<DeficitRecord>();
        /// <summary>资源顶栏上固定的物品（顺序 = 顶栏顺序），可带目标产量（产线规划助手“一键固定为目标”）。</summary>
        public PinnedItemRecord[] Pins = Array.Empty<PinnedItemRecord>();
        /// <summary>顶栏默认固定已经放过（新游戏默认固定废料；玩家全部取消后保持为空，不再自动补回）。</summary>
        public bool PinsInitialized;
        /// <summary>燃油发电机燃油耗尽的累计次数（统计面板“物流与能源”；DEBT-FG4ECO04-03）。</summary>
        public long FuelOuts;
    }

    /// <summary>FG4-ECO-08：一个时间窗口的环形桶。</summary>
    [Serializable]
    public sealed class ProdStatTierRecord
    {
        public int BucketSeconds;
        public ProdStatBucketRecord[] Buckets = Array.Empty<ProdStatBucketRecord>();
    }

    /// <summary>FG4-ECO-08：一桶（世界步 ÷ 每桶步数 = <see cref="Index"/>）里每种物品的产量与消耗（固体按件，流体按毫升）。</summary>
    [Serializable]
    public sealed class ProdStatBucketRecord
    {
        public long Index = -1;
        public ItemAmountRecord[] Produced = Array.Empty<ItemAmountRecord>();
        public ItemAmountRecord[] Consumed = Array.Empty<ItemAmountRecord>();
    }

    /// <summary>FG4-ECO-08：一种物品的数量（long：流体按毫升累计）。</summary>
    [Serializable]
    public sealed class ItemAmountRecord
    {
        public string ItemId;
        public long Amount;
    }

    /// <summary>FG4-ECO-08：一种物品正在持续赤字。</summary>
    [Serializable]
    public sealed class DeficitRecord
    {
        public string ItemId;
        /// <summary>赤字从哪一世界步开始（检测到时往前推一个检测窗口）。</summary>
        public long SinceTick;
        /// <summary>这一段赤字已经发过警告（同一段只发一次）。</summary>
        public bool Warned;
    }

    /// <summary>FG4-ECO-08：顶栏上固定的一种物品。</summary>
    [Serializable]
    public sealed class PinnedItemRecord
    {
        public string ItemId;
        /// <summary>目标产量（件或升 / 游戏分钟；0 = 没有目标，只看库存与速率）。</summary>
        public float TargetPerMinute;
    }

    /// <summary>FG2-FW-04：一场远征或一次突袭的反应伤害归因。</summary>
    [Serializable]
    public sealed class ReactionSessionRecord
    {
        public string SessionId = string.Empty;
        /// <summary>expedition = 远征（地点 = 远征区域）；raid = 突袭（地点 = 家园）。</summary>
        public string Kind = string.Empty;
        public string SiteId = string.Empty;
        /// <summary>远征：该区域第几次出击（RegionRecord.ExpeditionCount）；突袭：突袭的外部键（队伍 ID 等）。</summary>
        public int Ordinal;
        public string RaidKey = string.Empty;
        /// <summary>开始 / 结束于统一时钟第几步（结束 -1 = 进行中）。</summary>
        public long StartTick;
        public long EndTick = -1;
        /// <summary>这一场里敌对阵营受到的全部伤害（占比的分母）。</summary>
        public double TotalDamage;
        public ReactionShareRecord[] Reactions = Array.Empty<ReactionShareRecord>();
    }

    /// <summary>FG2-FW-04：一场里某条反应的触发次数与额外伤害（打在敌对阵营身上的）。未开放命名的反应记在自己的 ID 下，显示时不报名字。</summary>
    [Serializable]
    public sealed class ReactionShareRecord
    {
        public string ReactionId = string.Empty;
        public int Count;
        public double Damage;
    }

    /// <summary>存档自身的历史（本 Story）：读档时的内容迁移通知，供通知中心历史（FG0-UX-01）回看。</summary>
    [Serializable]
    public sealed class SaveHistoryState
    {
        public int DomainVersion = 1;
        public SaveNoticeRecord[] Notices = Array.Empty<SaveNoticeRecord>();
    }

    /// <summary>FG0-UX-01（FGR-UX-020）：通知中心历史。按等级 / 类型筛选、点击定位都读这里；
    /// 只存类型 ID 与细节参数，正文在显示时按当前语言用文本键拼出。条数上限见 fg.TbUiTuning notify.history_capacity。</summary>
    [Serializable]
    public sealed class NotificationHistoryState
    {
        public int DomainVersion = 1;
        /// <summary>下一条通知的 ID（单调递增，读档后继续往后编，不会与历史撞号）。</summary>
        public long NextId = 1;
        /// <summary>已经转进通知历史的读档变更（<see cref="SaveNoticeRecord.NoticeId"/>），同一条不会转两次。</summary>
        public string[] ImportedSaveNoticeIds = Array.Empty<string>();
        public NotificationRecord[] Entries = Array.Empty<NotificationRecord>();
    }

    /// <summary>一条（可能是聚合后的）通知。</summary>
    [Serializable]
    public sealed class NotificationRecord
    {
        public long Id;
        /// <summary>fg.TbNotifyType 的类型 ID。</summary>
        public string TypeId;
        /// <summary>聚合了几次（“3 座建筑缺电”的 3）。</summary>
        public int Count = 1;
        /// <summary>来源说明的文本键与参数（FGR-BASE-020：由哪条规则 / 哪个系统触发）。空 = 系统事件。</summary>
        public string SourceKey = string.Empty;
        public string SourceArg = string.Empty;
        public NotificationMemberRecord[] Members = Array.Empty<NotificationMemberRecord>();
    }

    /// <summary>聚合里的一条成员：细节、位置、时间。</summary>
    [Serializable]
    public sealed class NotificationMemberRecord
    {
        /// <summary>细节参数（例如建筑名）。若它本身是文本键，显示时按当前语言解析。</summary>
        public string Detail = string.Empty;
        public bool HasLocation;
        public string RegionId = string.Empty;
        public float X;
        public float Y;
        public float Z;
        public string CreatedAtUtc = string.Empty;
        /// <summary>产生时的游戏日与游戏秒（统一时钟接入前为 0，界面改显示本地时间）。</summary>
        public int GameDay;
        public double GameSeconds;
    }

    /// <summary>一条读档通知。文本不落盘，只存文本键与参数，切换语言后回看仍是当前语言。</summary>
    [Serializable]
    public sealed class SaveNoticeRecord
    {
        /// <summary>稳定 ID（幂等：同一条迁移不会记两次）。</summary>
        public string NoticeId;
        public string TextKey;
        public string[] Args = Array.Empty<string>();
        /// <summary>产生通知时存档的内容版本 → 当前内容版本。</summary>
        public int FromContentVersion;
        public int ToContentVersion;
        public string CreatedAtUtc;
    }

    /// <summary>状态域登记表：名称、承接 Story、取值 / 补齐方式。自检据此逐个验证往返与缺失补齐，
    /// 新增域必须在这里登记，否则自检会发现 CampaignState 上有未登记的域字段。</summary>
    public static class CampaignFgStateDomains
    {
        public readonly struct DomainInfo
        {
            public readonly string FieldName;
            public readonly string OwnerStory;
            public readonly Func<CampaignState, object> Get;

            public DomainInfo(string fieldName, string ownerStory, Func<CampaignState, object> get)
            {
                FieldName = fieldName;
                OwnerStory = ownerStory;
                Get = get;
            }
        }

        public static readonly IReadOnlyList<DomainInfo> All = new[]
        {
            new DomainInfo(nameof(CampaignState.World), "FG0-ARCH-05（世界生成与区块流式加载）", s => s.World),
            new DomainInfo(nameof(CampaignState.Progress), "FG11 叙事 / FG12 沙盒", s => s.Progress),
            new DomainInfo(nameof(CampaignState.Clock), "FG0-ARCH-01（统一时钟）", s => s.Clock),
            new DomainInfo(nameof(CampaignState.Grid), "FG0-ARCH-04（格网建造）", s => s.Grid),
            new DomainInfo(nameof(CampaignState.Belts), "FG0-ARCH-02（传送带内核）", s => s.Belts),
            new DomainInfo(nameof(CampaignState.Combat), "FG0-ARCH-03（战斗内核）", s => s.Combat),
            new DomainInfo(nameof(CampaignState.Pipes), "FG03 管线与流体", s => s.Pipes),
            new DomainInfo(nameof(CampaignState.Power), "FG3-LOG-06 电力子网与电塔", s => s.Power),
            new DomainInfo(nameof(CampaignState.Research), "FG05 研究", s => s.Research),
            new DomainInfo(nameof(CampaignState.Weather), "FG07 天气", s => s.Weather),
            new DomainInfo(nameof(CampaignState.Raids), "FG06 突袭", s => s.Raids),
            new DomainInfo(nameof(CampaignState.DirectorEvents), "FG10 事件导演", s => s.DirectorEvents),
            new DomainInfo(nameof(CampaignState.Quests), "FG08 任务", s => s.Quests),
            new DomainInfo(nameof(CampaignState.StandingRules), "FG04 常驻规则", s => s.StandingRules),
            new DomainInfo(nameof(CampaignState.SignalCore), "FG1-SIG-01（信号核）", s => s.SignalCore),
            new DomainInfo(nameof(CampaignState.LootPackets), "FG08 掉落数据包", s => s.LootPackets),
            new DomainInfo(nameof(CampaignState.Stats), "FG15 / FG16 统计", s => s.Stats),
            new DomainInfo(nameof(CampaignState.SaveHistory), "FG0-SAVE-01", s => s.SaveHistory),
            new DomainInfo(nameof(CampaignState.Notifications), "FG0-UX-01（通知中心历史）", s => s.Notifications),
            new DomainInfo(nameof(CampaignState.Nav), "FG0-ARCH-06（层级寻路：排队请求与待采纳结果）", s => s.Nav),
            new DomainInfo(nameof(CampaignState.Economy), "FG4-ECO-01 / 02（物品、流体与配方：仓库物资、核心保管库、净速率采样；生产建筑、废墟储量、家园震动）", s => s.Economy),
            new DomainInfo(nameof(CampaignState.Factions), "FG9-FAC-01 起（阵营扩充：寂听主脑首领战、电磁噪声发射器、阵营反制每日锁定、室内伏笔地点）", s => s.Factions),
        };

        /// <summary>把缺失（null）的域补成空域。读档后与存档前都会调用；已有数据的域原样保留。</summary>
        public static void EnsureAll(CampaignState s)
        {
            if (s == null)
            {
                return;
            }
            s.World ??= new WorldGenState();
            s.World.ChunkDiffs ??= Array.Empty<ChunkDiffRecord>();
            s.World.MapMarkers ??= Array.Empty<MapMarkerRecord>();
            s.World.AnchorChecks ??= Array.Empty<AnchorCheckRecord>(); // FG8-GEN-02：旧存档没有这一项
            s.World.InteriorFocusId ??= string.Empty;
            s.Progress ??= new CampaignProgressState();
            s.Progress.Narrative ??= Array.Empty<NarrativeEntryRecord>(); // FG9-NAR-01：旧存档没有资料库 = 空资料库
            s.Progress.PendingCutscene ??= string.Empty;
            if (s.Progress.NarrativeSerial < 1)
            {
                s.Progress.NarrativeSerial = 1;
            }
            s.Clock ??= new GameClockState();
            // FG9-FAC-01：旧档没有阵营扩充域 = 空域（首领未遭遇、发射器全在、反制未锁定）。
            s.Factions ??= new FactionState();
            s.Factions.SilentBoss ??= new SilentBossRecord();
            s.Factions.NoiseEmittersDestroyed ??= Array.Empty<string>();
            s.Factions.NoiseZonesNoticed ??= Array.Empty<string>();
            s.Factions.SilentCounterId ??= string.Empty;
            s.Factions.PoisRead ??= Array.Empty<string>();
            s.Factions.SilentBoss.CounterId ??= string.Empty;
            s.Grid ??= new GridState();
            s.Grid.Explored ??= Array.Empty<ExploredAreaRecord>();
            s.Grid.TerrainSourceId ??= string.Empty;
            s.Grid.Exploration ??= new HomeExplorationState();
            s.Grid.Exploration.RelicsFound ??= Array.Empty<string>();
            s.Grid.Exploration.RelicsFoundTicks ??= Array.Empty<long>();
            s.Grid.Exploration.RelicsRecovered ??= Array.Empty<string>();
            s.Grid.Exploration.RelicsRecoveredTicks ??= Array.Empty<long>();
            s.Grid.Exploration.HazardsSeen ??= Array.Empty<string>();
            s.Grid.Exploration.PoisFound ??= Array.Empty<string>(); // FG8-GEN-02：旧档没有缓存 / 终端
            s.Grid.Exploration.PoisDone ??= Array.Empty<string>();
            s.Grid.Exploration.HeritageIds ??= Array.Empty<string>(); // FG8-EXP-04：旧档没有人类遗产
            s.Grid.Exploration.HeritageRelics ??= Array.Empty<string>();
            s.Grid.Exploration.HeritageWorld ??= string.Empty;
            s.Grid.Exploration.HeritageReach ??= Array.Empty<int>();
            s.Grid.Exploration.HeritageReachRejected ??= Array.Empty<string>();
            s.Grid.Exploration.HeritageReachRelic ??= string.Empty;
            s.Grid.Exploration.HeritageObtained ??= Array.Empty<string>();
            s.Grid.Exploration.HeritageObtainedTicks ??= Array.Empty<long>();
            // FG8-EXP-01：旧档没有远征队字段 = 空队（默认名、颜色 0、空名单）。
            s.Grid.ExpeditionTeam ??= new ExpeditionTeamState();
            s.Grid.ExpeditionTeam.Name ??= string.Empty;
            s.Grid.ExpeditionTeam.Members ??= Array.Empty<int>();
            // FG8-EXP-02：旧档没有行军 / 锚点字段 = 待命 / 读档后重新解析锚点。
            s.Grid.ExpeditionTeam.March ??= new ExpeditionMarchState();
            s.Grid.ExpeditionTeam.March.SiteId ??= string.Empty;
            s.Grid.ExpeditionTeam.March.Members ??= Array.Empty<int>();
            s.Grid.ExpeditionTeam.March.HazardZoneId ??= string.Empty;
            s.Grid.ExpeditionTeam.March.EncounteredIds ??= Array.Empty<string>();
            s.Grid.ExpeditionTeam.Anchors ??= Array.Empty<ExpeditionSiteAnchorRecord>();
            if (s.Grid.NextInstanceSerial < 2)
            {
                s.Grid.NextInstanceSerial = 2;
            }
            s.Belts ??= new BeltItemState();
            s.Belts.Networks ??= Array.Empty<BeltNetworkRecord>();
            s.Belts.Ports ??= string.Empty;
            s.Belts.SinkNames ??= Array.Empty<BeltSinkNameRecord>();
            s.Belts.PortBindings ??= Array.Empty<BeltPortBindingRecord>();
            s.Belts.Damage ??= Array.Empty<BeltDamageRecord>();
            s.Belts.FilterPresets ??= Array.Empty<BeltFilterPresetRecord>();
            if (s.Belts.NextFilterPresetSerial < 1)
            {
                s.Belts.NextFilterPresetSerial = 1;
            }
            s.Combat ??= new CombatState();
            s.Combat.Sites ??= Array.Empty<CombatSiteRecord>();
            foreach (CombatSiteRecord r in s.Combat.Sites)
            {
                if (r != null)
                {
                    r.Keys ??= Array.Empty<string>();
                    r.Payload ??= string.Empty;
                }
            }
            s.Pipes ??= new PipeFluidState();
            s.Pipes.Damage ??= Array.Empty<PipeDamageRecord>();
            Logistics.PipeLeakService.EnsureState(s); // FG6-LOG-10：液洼（旧档没有 = 没有液洼；坏记录丢弃）
            s.Power ??= new PowerGridState();
            s.Power.SubnetSerials ??= Array.Empty<int>();
            s.Power.SubnetAnchors ??= Array.Empty<string>();
            s.Power.CurveCounts ??= Array.Empty<int>();
            s.Power.CurveSupply ??= Array.Empty<float>();
            s.Power.CurveDemand ??= Array.Empty<float>();
            s.Power.CurveDelivered ??= Array.Empty<float>();
            s.Power.CurveStored ??= Array.Empty<float>();
            s.Power.StorageIds ??= Array.Empty<string>();
            s.Power.StorageStored ??= Array.Empty<double>();
            s.Power.CurveClassSupply ??= Array.Empty<float>();
            s.Power.StorageSettingIds ??= Array.Empty<string>();
            s.Power.StorageNoCharge ??= Array.Empty<bool>();
            s.Power.StorageNoDischarge ??= Array.Empty<bool>();
            s.Power.StorageReserve ??= Array.Empty<int>();
            s.Research ??= new ResearchState();
            s.Research.CompletedNodes ??= Array.Empty<string>();
            s.Research.Progress ??= Array.Empty<ResearchProgressRecord>();
            s.Research.Queue ??= Array.Empty<string>();
            s.Research.Labs ??= Array.Empty<LabRecord>();
            s.Research.NewEntries ??= Array.Empty<string>();
            s.Research.StallNotified ??= string.Empty;
            s.Research.Analysis ??= new AnalysisBenchState();
            s.Research.Analysis.Tags ??= Array.Empty<EnemyItemTagRecord>();
            s.Research.Analysis.LoreRead ??= Array.Empty<string>();
            if (s.Research.Analysis.NextSerial < 1)
            {
                s.Research.Analysis.NextSerial = 1;
            }
            Economy.TestRangeService.EnsureState(s); // FG5-RND-03：靶场域补成空域；旧档按已击毁的敌人记录补“击败过的敌人类型”。
            Economy.FusionService.EnsureState(s); // FG5-RND-04：熔合域补成空域（旧档没有 = 没发现配方、没有线索）。
            Economy.IntelService.EnsureState(s); // FG5-RND-05：情报域补成空域（旧档没有 = 没有情报、没有进度）。
            Economy.BlackBoxService.EnsureState(s); // FG5-RND-06：黑匣子域补成空域；已回收却不在队列里的黑匣子补进队列（只加不删）。
            if (s.Research.DomainVersion < ResearchState.CurrentVersion)
            {
                Economy.ResearchService.MigrateFromV1(s); // FG5-RND-01：旧档迁移（研发树开放前就能建造的内容记为已研究）
            }
            s.Economy ??= new EconomyState();
            s.Economy.Items ??= Array.Empty<ItemStackRecord>();
            s.Economy.Vault ??= Array.Empty<ItemStackRecord>();
            s.Economy.FlowItemIds ??= Array.Empty<string>();
            s.Economy.FlowSamples ??= Array.Empty<ItemFlowSampleRecord>();
            foreach (ItemFlowSampleRecord r in s.Economy.FlowSamples)
            {
                if (r != null)
                {
                    r.Stocks ??= Array.Empty<int>();
                }
            }
            // FG4-ECO-02：生产建筑与拆到一半的废墟格。
            s.Economy.Producers ??= Array.Empty<ProducerRecord>();
            s.Economy.RuinCells ??= Array.Empty<RuinCellRecord>();
            foreach (ProducerRecord r in s.Economy.Producers)
            {
                if (r != null)
                {
                    r.RecipeId ??= string.Empty;
                    r.PendingItem ??= string.Empty;
                    r.In ??= Array.Empty<ItemStackRecord>();
                    r.Out ??= Array.Empty<ItemStackRecord>();
                    r.FluidHandles ??= Array.Empty<int>();
                    r.FluidOut ??= Array.Empty<bool>();
                    r.FluidHeld ??= Array.Empty<long>();
                    r.BurnTarget ??= string.Empty;
                    r.AwayStarve ??= Array.Empty<ItemStackRecord>(); // FG4-ECO-09
                }
            }
            // FG4-ECO-10：软锁保底与死锁检测（旧存档没有 = 空；开局机器视为已经播种过——那时早就有机器记录了）。
            // 审查修复 P2：JsonUtility 读旧档时字段保留初始化器生成的空对象（不是 null），所以按内容判：还没标播种、从没打印过、但存档里已经有机器记录
            // （机器记录只在开局播种之后才会同步进存档）= 旧存档，开局机器早就播种过了。新战役第一次进家园前存档里没有机器记录，不受影响。
            s.Economy.Softlock ??= new SoftlockState();
            SoftlockState sl = s.Economy.Softlock;
            if (!sl.StartMachinesSeeded && sl.LastPrintDay == 0 && sl.PrintCount == 0 && (s.MachineRecords?.Length ?? 0) > 0)
            {
                sl.StartMachinesSeeded = true;
            }
            s.Economy.Softlock.Loops ??= Array.Empty<LoopWatchRecord>();
            s.Economy.Softlock.Sites ??= Array.Empty<ReachWatchRecord>();
            foreach (ReachWatchRecord w in s.Economy.Softlock.Sites)
            {
                if (w != null)
                {
                    w.Blockers ??= string.Empty;
                }
            }
            // FG4-ECO-03：装配站材料缓存、废料代付开关、配方记忆（旧存档没有 = 空 / 未设置）。
            s.Economy.AssemblyBuffer ??= Array.Empty<ItemStackRecord>();
            s.Economy.RecipeMemory ??= Array.Empty<RecipeMemoryRecord>();
            foreach (RecipeMemoryRecord m in s.Economy.RecipeMemory)
            {
                if (m != null)
                {
                    m.RecipeId ??= string.Empty;
                    m.BurnTarget ??= string.Empty;
                }
            }
            s.Weather ??= new WeatherState();
            s.Raids ??= new RaidState();
            s.Raids.InTransit ??= Array.Empty<TransitGroupRecord>();
            if (s.Raids.NextGroupSerial < 1)
            {
                s.Raids.NextGroupSerial = 1;
            }
            foreach (TransitGroupRecord g in s.Raids.InTransit)
            {
                if (g != null)
                {
                    g.RouteX ??= Array.Empty<int>();
                    g.RouteY ??= Array.Empty<int>();
                    g.PlanId ??= string.Empty;
                    g.Faction ??= string.Empty;
                    g.OutpostId ??= string.Empty;
                    g.TargetKind ??= string.Empty;
                    g.UnitIds ??= Array.Empty<string>();
                    g.UnitCounts ??= Array.Empty<int>();
                    g.EliteCounts ??= Array.Empty<int>();
                    g.TrailX ??= Array.Empty<int>();
                    g.TrailY ??= Array.Empty<int>();
                    g.SiegeRetreatReason ??= string.Empty;
                    if (g.TrailX.Length != g.TrailY.Length)
                    {
                        g.TrailX = Array.Empty<int>();
                        g.TrailY = Array.Empty<int>();
                    }
                }
            }
            Defense.RaidDirectorService.EnsureState(s); // FG6-DEF-04：突袭导演域补成空域（旧档没有 = 第一次推进时按当前暴露补好，不追溯触发）。
            Defense.SiegeService.EnsureState(s); // FG6-DEF-05：攻城域补成空域（旧档没有 = 没有进行中的攻城）。
            Defense.DifficultyService.EnsureState(s); // FG6-DEF-09：难度域补成空域（旧档没有 = 开局难度按当前难度、没有修改记录）；坏值钳回滑条范围。
            s.Raids.Outposts ??= Array.Empty<OutpostRecord>();
            s.Raids.Patrols ??= Array.Empty<PatrolRecord>();
            foreach (OutpostRecord o in s.Raids.Outposts)
            {
                if (o != null)
                {
                    o.LastWakeReason ??= string.Empty;
                }
            }
            foreach (PatrolRecord p in s.Raids.Patrols)
            {
                if (p != null)
                {
                    p.RouteX ??= Array.Empty<int>();
                    p.RouteY ??= Array.Empty<int>();
                }
            }
            s.Raids.PendingWakeIds ??= Array.Empty<string>();
            s.Raids.PendingWakeReasons ??= Array.Empty<string>();
            WorldSim.WorldOutpostSystem.BackfillPatrolRadius(s); // FG8-EXP-03 复审 P2：旧档据点补巡逻半径（重建时补派巡逻）。
            WorldSim.TerritoryControlService.EnsureState(s); // FG8-EXP-03：领地控制域补成空域（旧档没有 = 第一次推进时按当前据点补好）。
            Defense.TurretService.EnsureState(s); // FG6-DEF-01：炮塔域补成空域（旧档没有 = 没有炮塔）；坏值钳回合法范围。
            Defense.DefenseService.EnsureState(s); // FG6-DEF-02：防御建筑域补成空域（旧档没有 = 没有屏障 / 闸门 / 护盾 / 陷阱）；坏值钳回。
            Defense.RepairDroneService.EnsureState(s); // FG6-DEF-03：维修无人机站域补成空（旧档没有 = 没有维修无人机站）；坏值钳回、重复序号重编。
            WorldSim.OwnOutpostService.EnsureState(s); // FG8-OUT-01：己方前哨站域补成空域（旧档没有 = 第一次对账时静默补登）；坏值钳回、重复编号去掉。
            Regions.DropPodService.EnsureState(s); // FG8-DROP-01：投送站与投送舱域补成空域（旧档没有 = 没有投送站）；坏值钳回、重复序号重编。
            if (s.Raids.NextNavSerial < 1)
            {
                s.Raids.NextNavSerial = 1;
            }
            if (s.Raids.NextOutpostSerial < 1)
            {
                s.Raids.NextOutpostSerial = 1;
            }
            if (s.Raids.NextPatrolSerial < 1)
            {
                s.Raids.NextPatrolSerial = 1;
            }
            s.Nav ??= new NavState();
            s.Nav.SurfaceId ??= string.Empty;
            s.Nav.Payload ??= string.Empty;
            s.DirectorEvents ??= new DirectorEventState();
            s.Quests ??= new QuestState();
            s.StandingRules ??= new StandingRuleState();
            EnsureRules(s.StandingRules);
            s.SignalCore ??= new SignalCoreState();
            s.SignalCore.SlotPartIds ??= Array.Empty<string>();
            for (int i = 0; i < s.SignalCore.SlotPartIds.Length; i++)
            {
                s.SignalCore.SlotPartIds[i] ??= string.Empty;
            }
            s.SignalCore.Presets ??= Array.Empty<SignalCorePresetRecord>();
            foreach (SignalCorePresetRecord p in s.SignalCore.Presets)
            {
                if (p != null)
                {
                    p.PresetId ??= string.Empty;
                    p.Name ??= string.Empty;
                    p.SlotContentIds ??= Array.Empty<string>();
                    for (int i = 0; i < p.SlotContentIds.Length; i++)
                    {
                        p.SlotContentIds[i] ??= string.Empty;
                    }
                }
            }
            s.SignalCore.ActivePresetId ??= string.Empty;
            if (s.SignalCore.NextPresetSerial < 1)
            {
                s.SignalCore.NextPresetSerial = 1;
            }
            s.SignalCore.UplinkSiteId ??= string.Empty;
            s.SignalCore.CoreCooldowns ??= Array.Empty<SignalCoreCooldownRecord>();
            s.SignalCore.SafeModes ??= Array.Empty<SignalSafeModeRecord>();
            s.SignalCore.RecentUplinks ??= Array.Empty<int>();
            s.LootPackets ??= new LootPacketState();
            s.Stats ??= new StatsState();
            s.Stats.ReactionSessions ??= Array.Empty<ReactionSessionRecord>();
            foreach (ReactionSessionRecord r in s.Stats.ReactionSessions)
            {
                if (r != null)
                {
                    r.SessionId ??= string.Empty;
                    r.Kind ??= string.Empty;
                    r.SiteId ??= string.Empty;
                    r.RaidKey ??= string.Empty;
                    r.Reactions ??= Array.Empty<ReactionShareRecord>();
                }
            }
            if (s.Stats.NextReactionSessionSerial < 1)
            {
                s.Stats.NextReactionSessionSerial = 1;
            }
            // FG4-ECO-08：生产统计与资源顶栏（旧存档没有 = 空域）。
            s.Stats.Production ??= new ProductionStatsState();
            ProductionStatsState ps = s.Stats.Production;
            ps.Tiers ??= Array.Empty<ProdStatTierRecord>();
            foreach (ProdStatTierRecord t in ps.Tiers)
            {
                if (t == null)
                {
                    continue;
                }
                t.Buckets ??= Array.Empty<ProdStatBucketRecord>();
                foreach (ProdStatBucketRecord b in t.Buckets)
                {
                    if (b != null)
                    {
                        b.Produced ??= Array.Empty<ItemAmountRecord>();
                        b.Consumed ??= Array.Empty<ItemAmountRecord>();
                    }
                }
            }
            ps.Deficits ??= Array.Empty<DeficitRecord>();
            ps.Pins ??= Array.Empty<PinnedItemRecord>();
            // FG4-ECO-09：离家报告（旧存档没有 = 空域）。
            s.Stats.AwayReports ??= new AwayReportState();
            AwayReportState ar = s.Stats.AwayReports;
            ar.Open ??= new AwayReportRecord();
            ar.Reports ??= Array.Empty<AwayReportRecord>();
            if (ar.NextSerial < 1)
            {
                ar.NextSerial = 1;
            }
            EnsureAwayReport(ar.Open);
            foreach (AwayReportRecord r in ar.Reports)
            {
                EnsureAwayReport(r);
            }
            s.SaveHistory ??= new SaveHistoryState();
            s.SaveHistory.Notices ??= Array.Empty<SaveNoticeRecord>();
            s.Notifications ??= new NotificationHistoryState();
            s.Notifications.Entries ??= Array.Empty<NotificationRecord>();
            s.Notifications.ImportedSaveNoticeIds ??= Array.Empty<string>();
            foreach (NotificationRecord r in s.Notifications.Entries)
            {
                if (r != null)
                {
                    r.Members ??= Array.Empty<NotificationMemberRecord>();
                    r.SourceKey ??= string.Empty;
                    r.SourceArg ??= string.Empty;
                }
            }
        }

        /// <summary>FG4-ECO-09：一份离家报告补空数组。</summary>
        public static void EnsureAwayReport(AwayReportRecord r)
        {
            if (r == null)
            {
                return;
            }
            r.RegionId ??= string.Empty;
            r.Members ??= Array.Empty<int>();
            r.Produced ??= Array.Empty<ItemAmountRecord>();
            r.Consumed ??= Array.Empty<ItemAmountRecord>();
            r.Starve ??= Array.Empty<AwayStarveRecord>();
            r.Outages ??= Array.Empty<AwayOutageRecord>();
            r.ClassSum ??= Array.Empty<double>();
            r.Machines ??= Array.Empty<AwayMachineRecord>();
            r.Entries ??= Array.Empty<AwayEntryRecord>();
            r.Rules ??= Array.Empty<RuleLogRecord>();
            r.ReactionSessionId ??= string.Empty;
            foreach (RuleLogRecord e in r.Rules)
            {
                if (e != null)
                {
                    e.Args ??= Array.Empty<string>();
                    e.Key ??= string.Empty;
                    e.EntityId ??= string.Empty;
                }
            }
        }

        /// <summary>FG4-ECO-06：规则域补空数组；新战役 / 旧档第一次补上默认开启的“远征卸货”（FGR-ECO-030“默认开启”）。</summary>
        public static void EnsureRules(StandingRuleState r)
        {
            r.Rules ??= Array.Empty<StandingRuleRecord>();
            r.Holds ??= Array.Empty<RuleHoldRecord>();
            r.Log ??= Array.Empty<RuleLogRecord>();
            r.Pending ??= Array.Empty<RuleEventRecord>();
            if (r.NextSerial < 1)
            {
                r.NextSerial = 1;
            }
            if (r.NextLogSerial < 1)
            {
                r.NextLogSerial = 1;
            }
            if (r.NextOrderSerial < 1)
            {
                r.NextOrderSerial = 1;
            }
            int maxZone = 0;
            foreach (StandingRuleRecord x in r.Rules)
            {
                if (x == null)
                {
                    continue;
                }
                x.Kind ??= string.Empty;
                x.ItemId ??= string.Empty;
                x.RecipeId ??= string.Empty;
                x.Targets ??= Array.Empty<string>();
                x.Machines ??= Array.Empty<int>();
                x.PointId ??= string.Empty;
                x.ConflictEntity ??= string.Empty;
                x.IssueKey ??= string.Empty;
                x.IssueArg ??= string.Empty;
                // FG6-DEF-03：重建区域——去掉空的、两端颠倒的摆正、重复编号的重编（下面按最大编号补齐 NextZoneSerial）。
                x.Zones ??= Array.Empty<RebuildZoneRecord>();
                if (x.Zones.Length > 0)
                {
                    var keep = new List<RebuildZoneRecord>(x.Zones.Length);
                    foreach (RebuildZoneRecord z in x.Zones)
                    {
                        if (z == null)
                        {
                            continue;
                        }
                        if (z.X1 < z.X0)
                        {
                            (z.X0, z.X1) = (z.X1, z.X0);
                        }
                        if (z.Y1 < z.Y0)
                        {
                            (z.Y0, z.Y1) = (z.Y1, z.Y0);
                        }
                        keep.Add(z);
                    }
                    if (keep.Count != x.Zones.Length)
                    {
                        x.Zones = keep.ToArray();
                    }
                }
            }
            var seenZones = new HashSet<int>();
            foreach (StandingRuleRecord x in r.Rules)
            {
                foreach (RebuildZoneRecord z in x?.Zones ?? Array.Empty<RebuildZoneRecord>())
                {
                    if (z.Serial <= 0 || !seenZones.Add(z.Serial))
                    {
                        z.Serial = 0;
                    }
                    else
                    {
                        maxZone = Math.Max(maxZone, z.Serial);
                    }
                }
            }
            if (r.NextZoneSerial <= maxZone)
            {
                r.NextZoneSerial = maxZone + 1;
            }
            foreach (StandingRuleRecord x in r.Rules)
            {
                foreach (RebuildZoneRecord z in x?.Zones ?? Array.Empty<RebuildZoneRecord>())
                {
                    if (z.Serial <= 0)
                    {
                        z.Serial = r.NextZoneSerial++;
                    }
                }
            }
            foreach (RuleHoldRecord h in r.Holds)
            {
                if (h != null)
                {
                    h.Kind ??= string.Empty;
                    h.EntityId ??= string.Empty;
                    h.Prev ??= string.Empty;
                    h.OrderId ??= string.Empty;
                    h.Applied ??= string.Empty;
                }
            }
            foreach (RuleLogRecord l in r.Log)
            {
                if (l != null)
                {
                    l.Key ??= string.Empty;
                    l.Args ??= Array.Empty<string>();
                    l.EntityId ??= string.Empty;
                }
            }
            foreach (RuleEventRecord e in r.Pending)
            {
                if (e != null)
                {
                    e.Kind ??= string.Empty;
                    e.EntityId ??= string.Empty;
                    e.Machines ??= Array.Empty<int>();
                }
            }
            if (!r.DefaultsSeeded)
            {
                r.DefaultsSeeded = true;
                var unload = new StandingRuleRecord
                {
                    Serial = r.NextSerial++,
                    Kind = "expedition_unload",
                    Enabled = true,
                    Priority = 1,
                };
                var list = new System.Collections.Generic.List<StandingRuleRecord>(r.Rules.Length + 1) { unload };
                foreach (StandingRuleRecord x in r.Rules)
                {
                    if (x != null)
                    {
                        x.Priority++;
                        list.Add(x);
                    }
                }
                r.Rules = list.ToArray();
            }
        }
    }
}
