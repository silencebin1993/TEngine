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
        // ── 敌方反制预览 ──
        public string[] Regions = Array.Empty<string>();
        public string[] Adaptations = Array.Empty<string>();
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
        /// <summary>到达时通往核心的路是完全堵住的（停在最近处；攻城在 FG6-DEF-05）。</summary>
        public bool Blocked;
    }

    /// <summary>事件导演（FG10）。</summary>
    [Serializable]
    public sealed class DirectorEventState
    {
        public int DomainVersion = 1;
    }

    /// <summary>任务（FG08）。</summary>
    [Serializable]
    public sealed class QuestState
    {
        public int DomainVersion = 1;
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

    /// <summary>掉落数据包（FG08）。</summary>
    [Serializable]
    public sealed class LootPacketState
    {
        public int DomainVersion = 1;
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
            s.Progress ??= new CampaignProgressState();
            s.Clock ??= new GameClockState();
            s.Grid ??= new GridState();
            s.Grid.Explored ??= Array.Empty<ExploredAreaRecord>();
            s.Grid.TerrainSourceId ??= string.Empty;
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
                }
            }
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
            Defense.TurretService.EnsureState(s); // FG6-DEF-01：炮塔域补成空域（旧档没有 = 没有炮塔）；坏值钳回合法范围。
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
