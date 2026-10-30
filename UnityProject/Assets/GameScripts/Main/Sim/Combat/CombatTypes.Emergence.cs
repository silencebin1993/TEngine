using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// EMG1-KRN-01（FG19 FGR-EMG-045、061、202、211、213；ADR-EMG-001 ②③④⑦⑨⑩）：涌现通道阶段 1 的内核常量。
    /// 内核只认整数码与值类型表（程序 / 节点 / 指令），不认任何内容 ID（FGR-EMG-013）；数值由热更层给出，内核不读表。
    /// 本卡只铺结构、发号与快照（格式 16）：程序表为空、总线为空、没有残骸时，行为与 <see cref="CombatKernel.StateHash"/> 与 M10 基线逐字不变；
    /// 执行（编译器 / 总线消费 / 触发器 / 动词 / 残骸生成）由 EMG1-PRG-01、BUS-01、TRIG-01、GRM-01、ENT-01 接上。
    /// </summary>
    public static class EmgConst
    {
        /// <summary>每个程序最多几个节点 / 几条指令行（ADR ②：理论最大 4 槽 × 混合 2 条 = 8 条自有指令 + 继承副本、节点 ≤ 13，留余量）。
        /// EMG1-PRG-01：内核指令行是“一条声明一行”（读端、写端各一行，同一条程序指令的行共用 Ordinal），一条固件 2～4 行，
        /// 满树 ≤ 20 个指令位 × 每位 ≤ 5 行，所以上限按行数取 128（InstrTotal / InstrCount 仍是 byte）；只放宽读档与登记的校验上界，不改任何字段。</summary>
        public const int MaxNodes = 16;
        public const int MaxInstr = 128;
        /// <summary>每地点程序表上限（只追加、不压实；ADR ②：阶段 1 实测超过再做“程序 / 弹体 / 无人机 / 区域统一重映射”的压实）。满了拒绝登记。</summary>
        public const int MaxPrograms = 4096;
        /// <summary>子载荷代 ≤ 2（编译期保证，FGR-EMG-031）；链深 ≤ 3（出队处截断，FGR-EMG-064）。</summary>
        public const int MaxGen = 2;
        public const int MaxDepth = 3;
        /// <summary>事件队列容量 <c>emg.bus.capacity</c>（FGR-EMG-063 / 202）。</summary>
        public const int BusCapacity = 16384;
        /// <summary>残骸容量 <c>emg.wreck.capacity</c>（FGR-EMG-202；满了的崩解归 EMG1-ENT-01）。</summary>
        public const int DefaultWreckCapacity = 256;
        /// <summary>事件端口码 = 300 + 事件码（FGR-EMG-060）。</summary>
        public const int EventPortBase = 300;
        public const int EventKinds = 29;

        // ── EMG1-BUS-01（FGR-EMG-063～065）：每步处理条数 / 扇出 / 每宿主上限 / 冷却完成比例的初值（运行时取 CombatConfig 里热更层按 fg.TbHomeTuning emg.* 写的值，≤ 0 用这里）──
        public const int DefaultBusPerStep = 512;
        public const int DefaultMaxFanout = 8;
        /// <summary>每宿主同一事件码上限（弹体 TrigUsed 每条事件读端 2 比特，所以最多 3）。</summary>
        public const int DefaultMaxPerHost = 3;
        public const float DefaultCoolRatio = 0.5f;
        /// <summary>积压保护（ADR ④）：未处理条数到 BusCapacity − MergeHeadroom 起，新事件先合并进同一步同类同源的事件（Value 累加、Code2 记合并数），找不到再合并进队尾 <see cref="MergeScan"/> 条里同类的一条。
        /// 余量 32 &gt; 事件种类 29：只有“队尾 64 条里还没有这一类”时才追加，追加的又都在队尾，所以这段时间里追加的条数 ≤ 29，未处理条数 ≤ BusCapacity − 32 + 29，不会到容量。</summary>
        public const int MergeHeadroom = 32;
        /// <summary>两轮合并查找都从队尾往前最多看几条（逼近容量时每条事件 O(1)，不随队列长度变）。</summary>
        public const int MergeScan = 64;
        /// <summary>指令行角色（与热更层 EmgRole 同值）：1 = 触发读端；1～4 = 读端（触发 / 条件 / 感知 / 作用域），5 起是写端。</summary>
        public const byte RoleTrigger = 1;
        public const byte RoleLastRead = 4;
        /// <summary>每宿主计数（弹体 TrigUsed / 出手上下文）能记几条事件读端（32 位 / 每条 2 比特）；一个节点的事件读端行超过这个数的程序登记 / 读档时拒绝。</summary>
        public const int HostCounterSlots = 16;

        // ── 快照 Emg 段的段标记（格式 16 起；永不复用，新段取未用过的下一个号，不分阶段——阶段 1 后续卡要加的快照内容（CHAIN-01 规则计数槽位、TRIG-01 区域宿主字段）同样另开新段，不改 WriteReadings）──
        public const int SegScalars = 1;
        public const int SegPrograms = 2;
        public const int SegNodes = 3;
        public const int SegInstrs = 4;
        public const int SegWeapons = 5;
        public const int SegUnits = 6;
        public const int SegProjectiles = 7;
        public const int SegDrones = 8;
        public const int SegBus = 9;
        public const int SegWrecks = 10;
        /// <summary>EMG1-TRIG-01：待生成的子载荷 / 根弹体与即时宿主的定时（顺延不丢，ADR ③）；区域、回波的宿主五元组（随读法表的区域 / 回波逐条，读档中转同无人机 Id）。</summary>
        public const int SegPending = 11;
        public const int SegZones = 12;
        public const int SegEchoes = 13;
        /// <summary>EMG1-FLASH-01（FGR-EMG-036；ADR ③）：刷写变体表（每把无人机武器 “截到 k 条 / 剥离核心后截到 k 条” 的执行武器下标 + 剥离换算），一条记录一个 int。</summary>
        public const int SegFlash = 14;
        /// <summary>EMG1-CHAIN-01（FGR-EMG-067 / 180；ADR ⑨）：可靠计数——按稳定键的规则计数槽（一条记录 = 键 + 次数）与按事件端口的入队计数（一条记录 = 次数，下标 = 事件码）。</summary>
        public const int SegRuleCounts = 15;
        public const int SegPortCounts = 16;
        /// <summary>EMG1-CHAIN-01：每地点因果链环形记录条数 emg.chain.ring（2 的幂，按位与取下标）与规则计数槽数（按稳定键开放寻址，槽位一经分配本场不挪）。</summary>
        public const int ChainRing = 256;
        public const int RuleSlots = 256;
        // ── EMG1-FLASH-01（FGR-EMG-036；fg.TbHomeTuning emg.flash.*）：刷写带宽初值（运行时取 CombatConfig 里热更层写的值，≤ 0 用这里）──
        public const int DefaultFlashBandwidth = 8;
        public const float DefaultFlashRange = 8f;
        public const float DefaultFlashStep = 4f;
        /// <summary>距离衰减的重算节拍（游戏秒；按模拟时间对齐，暂停不走、倍速同步，ADR ③“每 0.25 秒重算一次”）。</summary>
        public const double FlashInterval = 0.25;
        /// <summary>一个程序能参与刷写的指令条数上限（Active / Pause 是 byte；变体表每把武器 ≤ 3 × 256 条）。</summary>
        public const int MaxFlashLen = 250;
        // ── EMG1-TRIG-01（FGR-EMG-034 / 035；FG19A 第 2.1.1 节）：触发与子载荷的初值（运行时取 CombatConfig / 子形态表里热更层按 fg.TbHomeTuning、fg.TbSubPayload 写的值，≤ 0 用这里）──
        public const float DefaultSubDmgRatio = 0.5f;
        public const int DefaultMaxChildren = 4;
        public const int DefaultMaxPayloads = 16;
        /// <summary>命中触发的子弹药指向“5 米内其他敌对单位”（FGR-EMG-034 表第 1 行）。</summary>
        public const float ChildSeekRadius = 5f;
        /// <summary>待生成队列容量（满了拒绝并计数 SpawnLost，正常为 0；与总线同一口径，模糊测试器报告）。</summary>
        public const int PendingCapacity = 4096;
        /// <summary>子形态码 1～5（fg.TbSubPayload code 列）：子弹 / 短射程余波 / 无人机子弹（进弹体池），子区（小场脉冲）/ 子区（子区域）（进区域）。</summary>
        public const byte SubBullet = 1, SubWave = 2, SubDroneBullet = 3, SubPulse = 4, SubZone = 5;
        /// <summary>待生成种类：子载荷 / 即时宿主（格斗、力场）的定时 / 根载荷（SpawnFlight 弹体池满时顺延）。</summary>
        public const byte PendSub = 0, PendTimer = 1, PendRoot = 2;

        /// <summary>一份快照最多几个 Emg 段（读档防坏值）。</summary>
        public const int MaxSegments = 256;

        // ── EMG1-ENT-01（FG19 FGR-EMG-110～114；FG19B 第 6 节；fg.TbHomeTuning emg.wreck.* / emg.impact.v_min / emg.push.*）：初值（运行时取 CombatConfig 里热更层写的值，≤ 0 用这里）──
        public const float DefaultWreckSeconds = 90f;
        public const float DefaultWreckHpRatio = 0.5f;
        public const float DefaultWreckSlow = 0.5f;
        public const float DefaultImpactVMin = 4f;
        public const float DefaultPushDMin = 0.5f;
        public const float DefaultKnockSeconds = 0.2f;
        public const float DefaultShoveRefMass = 10f;
        /// <summary>残骸半径 = 原单位半径 × 本值（FGR-EMG-113）。</summary>
        public const float WreckRadiusRatio = 0.9f;
        /// <summary>实体档案种类默认的槽位（fg.TbEntityProfile 的 entity.* 行按此顺序写在档案表最前面；热更层 EmgEntityProfiles.SlotIds 同序）。</summary>
        public const short SlotProjectile = 0, SlotHeavy = 1, SlotDrone = 2, SlotSentry = 3, SlotFragment = 4, SlotMachine = 5, SlotEnemy = 6,
            SlotBoss = 7, SlotTurret = 8, SlotStructure = 9, SlotWreck = 10;
        public const int ProfileSlots = 11;
        /// <summary>材质码（evt.kill Code = 阵亡者材质；0 = 未知）。</summary>
        public const byte MatSteel = 1, MatAlloy = 2, MatComposite = 3, MatCeramic = 4, MatGlass = 5;
        /// <summary>阵亡标志：直接击毁（碎裂 / 湮灭 / 处决等不留残骸，ADR ⑦ 第 3 条）。</summary>
        public const byte DeathNoWreck = 1;
        /// <summary>evt.push 的原因端口码：击退 / 推铲（grm.mod.bounce 的击退读法）、牵引读法（fld.pull）。</summary>
        public const ushort PushCauseKnock = 112, PushCausePull = 701;
        /// <summary>残骸滑行速度低于本值视为停住（米 / 秒）。</summary>
        public const float WreckRestSpeed = 0.05f;
    }

    /// <summary>FG19 FGR-EMG-060 事件清单（封闭 29 条；码 = 端口码 − 300）。与 <see cref="CombatEventKind"/> 完全分开，不占玩法段也不占提示段。</summary>
    public enum EmgEventKind : byte
    {
        None = 0,
        Fire = 1,
        Hit = 2,
        Pierce = 3,
        Bounce = 4,
        Split = 5,
        Expire = 6,
        Prox = 7,
        Timer = 8,
        Kill = 9,
        Death = 10,
        Hurt = 11,
        Overheat = 12,
        Cooled = 13,
        Enter = 14,
        Leave = 15,
        Marked = 16,
        Uplink = 17,
        Unlink = 18,
        Pickup = 19,
        Impact = 20,
        Push = 21,
        React = 22,
        Phase = 23,
        Absorb = 24,
        Hijack = 25,
        Seize = 26,
        Release = 27,
        Beat = 28,
        Ping = 29,
    }

    /// <summary>
    /// EMG1-BUS-01：事件 Code 的取值（FGR-EMG-060 “带的数据”列；内核只认这些整数）。
    /// 载荷宿主的事件（发射 / 命中 / 穿透 / 反弹 / 终结 / 近炸 / 定时 / 击杀 / 被标记 / 吸收）：EntKind / Ent = 宿主载荷（弹体 Id 或即时出手的单位 Id），Other = 被作用的单位；
    /// 单位级事件（阵亡 / 被击 / 过热 / 冷却完成 / 接入 / 断开 / 拾取 / 夺控 / 归还）：Ent = 单位自己，Other = 对方（击杀者 / 攻击者）。
    /// Code2 平时为 0；积压保护合并时记“又合并进来几条”（ADR ④）。
    /// </summary>
    public static class EmgCode
    {
        // evt.hit：命中种类
        public const ushort HitDirect = 0, HitAfterPierce = 1, HitAfterBounce = 2, HitZoneTick = 3, HitPatch = 4, HitEcho = 5, HitChain = 6, HitHeal = 7;
        // evt.expire：原因（含“命中”，FG19A 申请 A8）
        public const ushort ExpireLife = 1, ExpireGround = 2, ExpireAbsorbed = 3, ExpireHit = 4, ExpireBounds = 5;
        /// <summary>EMG1-TRIG-01：SpawnFlight 弹体 / 子弹药撞上障碍圆（经 EmgHash 线段检测；还有障碍面反弹次数时改为反弹，EMG1-GRM-01）。</summary>
        public const ushort ExpireObstacle = 6;
        /// <summary>EMG1-GRM-01：折返的弹体回到发射者身边终结（FG19A 第 1.4 节第 41 行“在发射者身边终结”）。</summary>
        public const ushort ExpireReturn = 7;
        // evt.bounce：面
        public const ushort FaceUnit = 1, FaceObstacle = 2, FaceWreck = 3, FaceMatter = 4, FaceField = 5;
        // evt.hurt：来源
        public const ushort SrcOther = 0, SrcMelee = 1, SrcProjectile = 2, SrcZone = 3, SrcPatch = 4, SrcField = 5, SrcImpact = 6;
        // evt.unlink：原因（主动 / 切换 / 阵亡 / 断链 / 静默夜 / 区域卸载）
        public const ushort UnlinkOther = 0, UnlinkLeave = 1, UnlinkSwitch = 2, UnlinkDeath = 3, UnlinkSevered = 4, UnlinkSilentNight = 5, UnlinkUnload = 6;
        // evt.pickup：物品类
        public const ushort PickupDrop = 1, PickupScrap = 2, PickupDebris = 3;
    }

    /// <summary>EMG1-BUS-01（FGR-EMG-066）：热更层注入一条单位事件的结果。</summary>
    public enum EmgInjectResult : byte
    {
        Enqueued = 0,
        /// <summary>按需：这台单位的程序不读这个事件码，不入队（不是错误）。</summary>
        NoListener = 1,
        NoUnit = 2,
        /// <summary>只有接入 / 断开 / 拾取 / 夺控 / 归还可由热更层注入。</summary>
        NotInjectable = 3,
        /// <summary>队列已到容量且没有可合并的同类事件（不该发生，模糊测试器报告）。</summary>
        Full = 4,
    }

    /// <summary>
    /// EMG1-BUS-01：战斗事件总线的调试计数（调试层可见：<see cref="CombatKernel.EmgBusStats"/>）。不进快照、不进状态哈希，读档后从 0 计；
    /// 进存档的可靠计数（按规则 StableKey / 端口 / 实体种类）归 EMG1-CHAIN-01。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgBusStats
    {
        public long Enqueued;
        public long Processed;
        /// <summary>积压保护合并掉的条数（计数在被合并事件的 Code2 里，不丢）。</summary>
        public long Merged;
        /// <summary>到容量仍无处合并而没入队的条数（正常为 0；不为 0 = 设计缺陷，FGR-EMG-221）。</summary>
        public long Lost;
        /// <summary>链深已到 3、不再生成后代的次数（FGR-EMG-064 EmgDepthCut）。</summary>
        public long DepthCut;
        /// <summary>一条事件唤起的监听超过 8 条被截掉的条数（FGR-EMG-065）。</summary>
        public long FanoutCut;
        /// <summary>同一载荷节点同一事件码到 3 次后不再入队的次数（FGR-EMG-065）。</summary>
        public long PerHostCut;
        /// <summary>EMG1-TRIG-01：触发器放出子载荷的次数（= evt.split 次数，不论有没有程序读 evt.split）、生成了几份子载荷、因弹体池 / 区域满或同链存活已满而顺延的步次、
        /// 未被唤起（链深截断 / 事件被合并）而作废的待生成请求、队列满没入队的请求（应为 0）、即时命中被残骸挡下的次数、SpawnFlight 弹体 / 子弹药撞障碍的次数、根弹体实体化次数。</summary>
        public long Splits;
        public long SubSpawned;
        public long SpawnDeferred;
        public long SpawnDropped;
        public long SpawnLost;
        public long CoverAbsorbed;
        public long ObstacleHits;
        public long FlightShots;
        /// <summary>唤起的触发读端（子载荷生成归 EMG1-TRIG-01）与条件 / 感知读端（效果执行归 EMG1-FW-01 / FW-02）。</summary>
        public long TriggerWakes;
        public long ListenWakes;
        /// <summary>近炸求值次数（没有程序读 evt.prox 时应为 0）。</summary>
        public long ProxEvals;
        /// <summary>出队顺序不是 Seq 递增的次数（应为 0）。</summary>
        public long OrderErrors;
        /// <summary>处理完一步仍有剩余（顺延到下一步）的步数。</summary>
        public long StepsDeferred;
        /// <summary>EMG1-ENT-01：飞行弹体被残骸吸收 / 撞残骸反弹的次数、生成的破片、弹体池满没放出的破片（正常为 0，模糊测试器报告）、撞击与位移次数（不论有没有程序读）。</summary>
        public long WreckAbsorbs;
        public long WreckBounces;
        public long FragSpawned;
        public long FragLost;
        public long Impacts;
        public long Pushes;
        public int LastSeq;
        public int HasLast;
        public int LastDepthCutChain;
        public int LastFanoutCutChain;
        public int MaxPending;
        public int LastStepProcessed;
    }

    /// <summary>
    /// EMG1-BUS-01：当前正在结算的出手 / 载荷（宿主五元组的运行时副本）。只在一次出手（<c>EmgShotBegin</c> → <c>EmgShotEnd</c>）或一发弹体的命中结算期间有效，
    /// 让统一扣血入口 DamageUnit / Kill 知道这次伤害归哪个载荷（evt.hit / evt.kill 的宿主、evt.hurt 的来源与链）。瞬时量：不进快照、不进哈希。
    /// </summary>
    public struct EmgCtx
    {
        /// <summary>宿主程序（−1 = 不在载荷结算里）。</summary>
        public int Prog;
        public int Chain;
        /// <summary>宿主实体的稳定 ID（弹体 Id / 出手单位 Id）。</summary>
        public int Ent;
        /// <summary>出手单位的槽位（−1 = 不明）：只有它造成的伤害算本载荷的命中 / 击杀（反伤、别人的伤害不算）。</summary>
        public int Owner;
        public byte Node;
        public byte Depth;
        public EmgEntKind EntKind;
        public byte HurtSrc;
        public ushort HitCode;
        /// <summary>本次出手已命中几个单位（evt.hit 的 HitIdx 放在 Value2）。</summary>
        public ushort Hits;
        /// <summary>DamageUnit → Kill 的溢出伤害（evt.kill Value）。</summary>
        public float Overflow;
        /// <summary>本宿主每条事件读端已入队几次（2 比特一条，与弹体 TrigUsed 同口径；弹体宿主进出时与弹体互相拷贝）。</summary>
        public uint TrigUsed;
        // ── EMG1-TRIG-01：本载荷的“母载荷参数”（触发器放子载荷时用，生产点随事件记进待生成队列；瞬时量）──
        /// <summary>母载荷伤害（子载荷 = × emg.sub.dmg_ratio）。</summary>
        public float Damage;
        /// <summary>母载荷所属武器下标（子弹药的外观与归属；−1 = 没有）。</summary>
        public int Weapon;
        /// <summary>出手单位 Id（出手者已不在时 0）。</summary>
        public int OwnerId;
        public byte Faction;
        /// <summary>载体（CombatCarrier）：格斗 / 力场的触发只按主目标一次。</summary>
        public byte Carrier;
        /// <summary>主目标单位 Id（格斗 / 力场按它只触发一次）。</summary>
        public int MainTarget;
        /// <summary>母落区半径 / 存续（布区子区按比例）。</summary>
        public float ZoneRadius;
        public float ZoneLife;
        /// <summary>要带给子弹药的弹体标志（治疗 / 无人机出手）。</summary>
        public EmgProjFlags PFlags;
        /// <summary>1 = 本宿主是 SpawnFlight 根弹体（击毁改由命中致死回报）。</summary>
        public byte Flight;

        public static EmgCtx None => new EmgCtx { Prog = -1, Owner = -1 };

        /// <summary>
        /// 只记伤害来源、不做宿主（Prog = −1）：区域节拍等由 <paramref name="owner"/> 的载体造成的伤害据此给 evt.hurt 填来源码。
        /// EntKind 非 None 表示“这段结算里出手者 = owner 的伤害来自这个载体”，owner 可以是 −1（出手者已不在）。
        /// </summary>
        public static EmgCtx Source(int owner, EmgEntKind kind, byte src) => new EmgCtx { Prog = -1, Owner = owner, EntKind = kind, HurtSrc = src };
    }

    /// <summary>事件里实体的种类（ADR ④：实体用稳定 ID，不用下标）。</summary>
    public enum EmgEntKind : byte
    {
        None = 0,
        Unit = 1,
        Projectile = 2,
        Drone = 3,
        Zone = 4,
        Patch = 5,
        Wreck = 6,
        Field = 7,
    }

    /// <summary>
    /// 一条涌现事件（FGR-EMG-061；ADR ④ 布局，固定 56 字节）：
    /// 0 Kind / Depth / EntKind / OtherKind；4 Code / Code2（ushort，放得下规则下标与端口码）；8 Ent / Other / Prog / Chain / Seq；28 Node + 3 字节填充；32 Value / Value2；40 Pos。
    /// <see cref="Seq"/> 是总线自己的计数器（<see cref="EmgScalars.EmgSeq"/>，不与 CombatEvent.Seq 共用，FGR-EMG-213），只在总线内比先后（回绕安全的差值比较）。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgEvent
    {
        public EmgEventKind Kind;
        public byte Depth;
        public EmgEntKind EntKind;
        public EmgEntKind OtherKind;
        public ushort Code;
        public ushort Code2;
        public int Ent;
        public int Other;
        /// <summary>宿主程序下标（−1 = 没有程序）。</summary>
        public int Prog;
        public int Chain;
        public int Seq;
        public byte Node;
        public byte Pad0;
        public short Pad1;
        public float Value;
        public float Value2;
        public double2 Pos;
    }

    [Flags]
    public enum EmgProgramFlags : ushort
    {
        None = 0,
        /// <summary>根节点需要飞行（ADR ③：武器置 SpawnFlight）。</summary>
        NeedsFlight = 1,
        /// <summary>含接入固件的核心指令（FGR-EMG-037：信号离开时剥离）。</summary>
        HasCore = 2,
    }

    [Flags]
    public enum EmgInstrFlags : ushort
    {
        None = 0,
        /// <summary>编译期继承来的副本（FGR-EMG-033）。</summary>
        Inherited = 1,
        /// <summary>接入固件的核心指令。</summary>
        Core = 2,
        /// <summary>容量溢出（预览可见、不执行）。</summary>
        Overflow = 4,
        /// <summary>落点接线复制来的效果写端（ADR ②，阶段 3）。</summary>
        Anchored = 8,
        // ── 目标偏好（FGR-EMG-017 / FG19A A5）：只在 grm.mod.homing / grm.mod.chain 写端行上，由编译器按“写同键的各条指令的读端”按位并好；
        //    内核挑目标按固定顺序：homing 已标记 > 热信号 > 最近；chain 已标记 > 导电 > 最近（chain 只有“已标记”一种偏好时只跳已标记目标）。执行归 EMG1-GRM-01。
        /// <summary>偏好已标记目标（读 evt.marked）。</summary>
        PreferMarked = 16,
        /// <summary>寻的偏好热信号（读 prop.heat）。</summary>
        PreferHeat = 32,
        /// <summary>连锁偏好导电目标（读 prop.conductive）。</summary>
        PreferConductive = 64,
    }

    [Flags]
    public enum EmgProjFlags : byte
    {
        None = 0,
        /// <summary>子弹药：跳过根读法，只执行自己节点的指令（ADR ③）。</summary>
        Sub = 1,
        /// <summary>破片：2 代、没有程序（FGR-EMG-114）。</summary>
        Fragment = 2,
        /// <summary>抛射弹道。</summary>
        Lob = 4,
        /// <summary>EMG1-BUS-01：在近炸读端半径内（进入时发一次 evt.prox，离开后清掉；重复进入受每宿主上限约束）。</summary>
        ProxIn = 8,
        /// <summary>EMG1-TRIG-01：根载荷实体化（武器 SpawnFlight）——命中时按原“当场结算”那一下结算（读法、标记、标记跳转、重炮读法），撞障碍即止。</summary>
        Flight = 16,
        /// <summary>EMG1-TRIG-01：治疗载荷（维修命中放出的子载荷）：碰己方单位，命中 = 修复（evt.hit Code = 修复）。</summary>
        Heal = 32,
        /// <summary>EMG1-TRIG-01：重炮熔穿过载的这一发（开火时已积热 / 发反应，命中时加穿甲）。</summary>
        Overload = 64,
        /// <summary>EMG1-TRIG-01：无人机开火实体化的弹体（命中按无人机读法：伤害基数是无人机伤害、以无人机为锚）。</summary>
        ViaDrone = 128,
    }

    /// <summary>
    /// EMG1-GRM-01（FG19A 第 3.1 节；ADR-EMG-001 ②）：内核动词码 = 指令行 <see cref="EmgInstr.Verb"/>（热更层 EmgCatalog.Verbs 下标 + 1，按武器载体解析好）。
    /// 1～25 是旧读法动词（CarrierReadingVerb 同值，不改）：根节点上的由热更层折进武器读法，内核只认其中的 <see cref="Chain"/>（连锁按目标偏好挑目标）；
    /// 26 起是新动词，内核按动词执行，不认端口名以外的任何内容 ID（FGR-EMG-013）。
    /// </summary>
    public enum EmgVerb : byte
    {
        None = 0,
        /// <summary>旧读法“连锁”（= CarrierReadingVerb.Chain）。</summary>
        Chain = 5,
        Steer = 26,
        PassThrough = 27,
        Ricochet = 28,
        Multi = 29,
        Return = 30,
        Orbit = 31,
        Lob = 32,
        Drift = 33,
        SubPayload = 34,
        Mark = 35,
        Emit = 36,
        Spread = 37,
        Inject = 38,
        Spawn = 39,
        Fragment = 40,
        Salvage = 41,
        Mass = 42,
        Link = 43,
        Field = 44,
        /// <summary>grm.inherit：编译期扩大继承（FGR-EMG-033），内核不执行。</summary>
        Inherit = 45,
    }

    /// <summary>EMG1-GRM-01：在飞弹体的弹道修饰状态（进快照：Emg 弹体段尾部追加）。</summary>
    [Flags]
    public enum EmgProjMods : byte
    {
        None = 0,
        /// <summary>寻的转向中（剩余修正角 <see cref="CombatProjectile.SteerLeft"/>）。</summary>
        Steer = 1,
        /// <summary>命中后会折返。</summary>
        Return = 2,
        /// <summary>正在折返（飞回发射者）。</summary>
        Returning = 4,
        /// <summary>正在绕发射者转圈（<see cref="CombatProjectile.ModT"/> = 已转弧度）。</summary>
        Orbit = 8,
        /// <summary>侧偏过（穿过偏折场时按概率不被反射）。</summary>
        Drift = 16,
        /// <summary>穿透过（之后的命中 evt.hit Code = 穿透后）。</summary>
        Pierced = 32,
        /// <summary>反弹过（之后的命中 evt.hit Code = 反弹后）。</summary>
        Bounced = 64,
        /// <summary>折返途中已命中过 1 次（之后只飞回发射者，不再碰单位）；或已回到发射者身边。</summary>
        ReturnHit = 128,
    }

    /// <summary>程序表一行（ADR ②，16 字节）。<see cref="FirstNode"/> 是节点表里的绝对下标；表只追加、不改写、不压实。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgProgram
    {
        public int FirstNode;
        public byte NodeCount;
        public byte InstrTotal;
        public EmgProgramFlags Flags;
        /// <summary>本程序监听的事件码（位 k = 事件码 k，1～29）。</summary>
        public uint ListenMask;
        /// <summary>每次出手的热量（编译期算好，FGR-EMG-016）。</summary>
        public float HeatPerShot;
    }

    /// <summary>程序树的一个节点（ADR ②，16 字节）。<see cref="FirstInstr"/> 是指令表里的绝对下标；根节点 Parent = −1、Gen = 0。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgNode
    {
        /// <summary>父节点（程序内序号；根 = −1）。</summary>
        public sbyte Parent;
        /// <summary>代号 0～2（FGR-EMG-031）。</summary>
        public byte Gen;
        public EmgEventKind TrigEvent;
        /// <summary>份数（子载荷个数）。</summary>
        public byte TrigCount;
        /// <summary>触发参数（定时秒 / 近炸半径）。</summary>
        public float TrigParam;
        public int FirstInstr;
        /// <summary>子形态（fg.TbSubPayload 行码）。</summary>
        public byte SubForm;
        public byte InstrCount;
        /// <summary>继承掩码（FGR-EMG-045：位 g = 从第 g 代祖先继承了修饰）。</summary>
        public byte InheritMask;
        /// <summary>元程序掩码（阶段 3）。</summary>
        public byte MetaMask;
    }

    /// <summary>一条指令（ADR ②，28 字节）：内核按 <see cref="Verb"/> 分派，端口码只用于监听掩码与规则匹配。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgInstr
    {
        public ushort Port;
        public byte Role;
        /// <summary>A 的落点种类（落点接线，阶段 3；与 Role 共用填充）。</summary>
        public byte AnchorKind;
        /// <summary>fg.TbPortCarrier 按本武器载体解析好的内核动词（EmgCatalog.Verbs 下标）。</summary>
        public byte Verb;
        /// <summary>程序序号（刷写带宽从末尾截断用，FGR-EMG-036）。</summary>
        public byte Ordinal;
        public EmgInstrFlags Flags;
        public float Amount;
        public float Param2;
        public float A;
        public float B;
        public float C;
    }

    /// <summary>
    /// 每单位的涌现状态“基本段”（ADR ⑦ / ⑩ 格式 16）。阶段 2 起在段尾追加温度、电荷、累积量、相态、抗性覆盖；
    /// 爆炸反应装甲 EraMask / EraRefillT 由 EMG1-FW-01 追加在段尾（短段读到时按满块、计时 0，ADR ⑩）。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgUnitState
    {
        /// <summary>上一个接触节拍所在物质的掩码（evt.enter / evt.leave 做差用，ADR ④）。</summary>
        public uint PrevMatterMask;
        /// <summary>外部血量单位最后一次受击的攻击者单位 ID 与链 ID（击杀归属，ADR ⑦ 第 2 条）。</summary>
        public int LastHitBy;
        public int LastHitChain;
        /// <summary>EMG1-BUS-01（FGR-EMG-060 第 13 行）：1 = 过热过、还没回落到阈值 × emg.heat.cool_ratio 以下（回落时发 evt.cooled 并清 0）。
        /// 只在地点有程序读 evt.cooled 时才置位（没有程序的地点逐字不变）。格式 16 单位段尾部追加的第 5 个字段，短段读到取 0。</summary>
        public byte HeatLatch;
        /// <summary>EMG1-ENT-01：本单位质量（0 = 按档案 / 种类推算，见 CombatLogic.EmgUnitMass）。单位段第 6 个字段，短段读到取 0。</summary>
        public float Mass;
        /// <summary>EMG1-ENT-01：待处理的阵亡标志（外部血量单位收到“直接击毁”致死请求时置 <see cref="EmgConst.DeathNoWreck"/>，热更层判死回到阵亡入口时读走）。单位段第 7 个字段。</summary>
        public byte DeathFlags;
    }

    /// <summary>残骸（ADR ⑦；独立小结构，不是单位，不被索敌 / 编队 / 攻城当目标）。生成与挡弹归 EMG1-ENT-01。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgWreck
    {
        public double2 Pos;
        public double Born;
        /// <summary>到期时刻（野外 90 秒；家园 / 前哨长期 = 正无穷）。</summary>
        public double Until;
        /// <summary>驻留物质开始时刻（W01 / W02“在斑块里 ≥ 2 / 3 秒”）。</summary>
        public double DwellSince;
        public float Radius;
        public float Hp;
        public float MaxHp;
        public float Mass;
        public float Temp;
        public float Charge;
        /// <summary>稳定 ID（每地点单调，<see cref="EmgScalars.NextWreckId"/>）。</summary>
        public int Id;
        public int Chain;
        /// <summary>来源单位的外部键（回收与对账用）。</summary>
        public int SrcExtKey;
        public uint Coat;
        public uint PrevMatterMask;
        /// <summary>实体档案下标 + 1（0 = 种类默认）。与 <see cref="CombatWeapon.ProgramRef"/> 同一做法：结构默认值就是“种类默认”，生成残骸时漏写不会误指第 0 号档案；读写一律用 <see cref="Profile"/>。</summary>
        public short ProfileRef;
        /// <summary>来源单位种类（<see cref="CombatUnitKind"/>）。</summary>
        public byte SrcKind;
        public byte DwellMatter;
        /// <summary>1 = 不可拆解回收（己方机器的残骸，ADR ⑦ T5 核实前一律带）。</summary>
        public byte NoSalvage;
        public byte Pad0;
        public short Pad1;
        // ── EMG1-ENT-01：被推动（推铲 / 击退）的滑行状态（残骸段尾部追加，短段读到取 0 = 静止）──
        /// <summary>当前速度（米 / 秒）与匀减速度（米 / 秒²）；速度降到 <see cref="EmgConst.WreckRestSpeed"/> 以下停住。</summary>
        public float2 Vel;
        public float Decel;
        /// <summary>本次推动已滑行的距离（停住时 ≥ emg.push.d_min 发 evt.push）。</summary>
        public float PushDist;
        /// <summary>推动它的单位 ID（撞击伤害的攻击者）与原因端口码。</summary>
        public int PushBy;
        public ushort PushCause;
        /// <summary>EMG1-CHAIN-01（ADR ⑨ 产物归属，修复轮）：击毁者单位 ID（持久，进残骸段第 27 个字段；占 PushCause 后的对齐空位，结构仍 120 字节）。环被覆盖 / 读档后悬停“谁造成的”至少还有它。0 = 不明。</summary>
        public int Killer;

        /// <summary>实体档案下标（继承原单位；−1 = 种类默认）。不钳位：−2 这类坏值照原样留着，由校验拒绝。</summary>
        public short Profile
        {
            get => (short)(ProfileRef - 1);
            set => ProfileRef = (short)(value + 1);
        }
    }

    /// <summary>EMG1-CHAIN-01（FGR-EMG-180；ADR ⑨）：因果链节点种类。ADR 列的出手 / 触发 / 规则 / 相变 / 转发 / 合并 / 教义之外，阶段 1 还记热更注入、残骸、破片与链深截断（因果链末端“链深已达上限”）。</summary>
    public enum EmgChainNode : byte
    {
        None = 0,
        Shot = 1,
        Trigger = 2,
        Rule = 3,
        Phase = 4,
        Relay = 5,
        Merge = 6,
        Doctrine = 7,
        Inject = 8,
        Wreck = 9,
        Fragment = 10,
        DepthCut = 11,
    }

    /// <summary>
    /// EMG1-CHAIN-01（FGR-EMG-180；ADR ⑨）：一条因果链记录，48 字节、只含值类型。每地点 <see cref="EmgConst.ChainRing"/> 条环形缓冲，满了覆盖最旧的；
    /// <b>不进快照、不进 StateHash</b>（与“反应日志不存档”一致），读档后从空环开始。热更层只在打开因果链叠加 / 悬停时读（O(环长)）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgChainRec
    {
        public int Chain;
        /// <summary>引起它的另一条链（跨链：破片的链是打击者的，Parent = 残骸的链；没有 0）。</summary>
        public int Parent;
        public EmgChainNode Kind;
        public byte Depth;
        /// <summary>1 字节附加码（触发：子代号；破片：片数；反应装甲块位，ADR ⑨ 2026-10-26）。</summary>
        public byte Aux;
        public byte Pad0;
        /// <summary>端口码或规则稳定键（出手 0；触发 = 唤起的事件码；规则 = 稳定键；残骸 = 来源单位种类；注入 = 事件码）。</summary>
        public int Key;
        /// <summary>源单位 ID（出手者 / 打击者 / 被击毁者；0 = 不明）。</summary>
        public int SrcUnit;
        /// <summary>第二个单位 ID（规则：目标；残骸：击毁者；其余 0）。</summary>
        public int Other;
        public double2 Pos;
        public double Time;
    }

    /// <summary>EMG1-CHAIN-01：规则计数槽的反馈进给（最后一次的位置 / 来源 / 目标；不进快照，热更层弹字与日志用）。</summary>
    public struct EmgRuleFeed
    {
        public double2 Pos;
        public int Src;
        public int Target;
    }

    /// <summary>EMG1-ENT-01（ADR ⑦ 第 5 条“合并请求”）：一步内某个外部血量单位累计的持续类伤害（区域节拍 / 状态持续伤害 / 斑块接触），步末合成一条伤害请求。瞬时量，不进快照。</summary>
    public struct EmgMergeReq
    {
        public int Slot;
        public float Damage;
        /// <summary>伤害最大的那一份的攻击者单位 ID 与它的伤害。</summary>
        public int By;
        public float ByDamage;
    }

    /// <summary>
    /// EMG1-ENT-01（FG19 FGR-EMG-111；ADR ⑦“静态属性走档案表”）：一行实体档案（热更层按 fg.TbEntityProfile 编好：实体 / 底盘行的 −1 已换成材质值）。
    /// 前 <see cref="EmgConst.ProfileSlots"/> 行是种类默认（槽位见 EmgConst.Slot*），其后是“种类 × 材质 / 底盘”的组合行，单位的 EProfile 指向它们。不进快照（表数据，读档沿用当前的）。
    /// </summary>
    public struct EmgProfile
    {
        /// <summary>质量（0 = 按规则推算：敌人 8 × 半径²，限 1～80）。</summary>
        public float Mass;
        public float Conductive;
        public float Flammable;
        public float Magnetic;
        /// <summary>寒、热、电、腐蚀、磁、入侵六列抗性（相变累积速度读，阶段 2）。</summary>
        public float ResistCold, ResistHeat, ResistElec, ResistCorrode, ResistMag, ResistHack;
        public float Brittle;
        /// <summary>材质码（EmgConst.Mat*）。</summary>
        public byte Material;
        /// <summary>1 = 可被击退 / 推铲 / 场推动。</summary>
        public byte Displace;

        /// <summary>FG19B 第 6 节钢材质初值（没有表的自检夹具用；热更层载入表后整份覆盖）。</summary>
        public static EmgProfile Steel(float mass, float cond = 0.9f, float flam = 0.1f, float mag = 0.9f, byte displace = 1) => new EmgProfile
        {
            Mass = mass, Conductive = cond, Flammable = flam, Magnetic = mag, ResistHeat = 0.1f, Brittle = 1f, Material = EmgConst.MatSteel, Displace = displace,
        };
    }

    /// <summary>EMG1-ENT-01（FG19B 第 6.3 节、第 2.9 节）：载体动能系数与“残骸重击”破片参数（fg.TbEntityProfile 的 kinetic.* / fragment.wreck_heavy 行）。表数据，不进快照。</summary>
    public struct EmgEntityParams
    {
        public float KineticProjectile, KineticHeavy, KineticBeam, KineticMelee, KineticDrone, KineticAura, KineticField, KineticFragment;
        /// <summary>撞击动能 = 质量 × 速度² × 本值。</summary>
        public float KineticImpact;
        public int FragCount;
        public float FragDamage, FragSpeed, FragRange, FragThreshold;

        public static EmgEntityParams Default => new EmgEntityParams
        {
            KineticProjectile = 1f, KineticHeavy = 2f, KineticBeam = 0f, KineticMelee = 2.2f, KineticDrone = 0.5f, KineticAura = 0.2f, KineticField = 0.3f,
            KineticFragment = 2.5f, KineticImpact = 0.05f, FragCount = 2, FragDamage = 5f, FragSpeed = 16f, FragRange = 5f, FragThreshold = 40f,
        };
    }

    /// <summary>
    /// EMG1-ENT-01（FGR-EMG-110 / 111：“不合并数据结构，只统一公共属性语义”）：任一实体（弹体 / 无人机 / 哨戒桩 / 单位 / 残骸 / 破片）的公共属性块，
    /// 由 <see cref="CombatKernel.TryGetEntityProps"/> 按实体种类从各自的结构 + 档案表拼出来。热量：单位取武器积热、残骸取自带；电荷阶段 2 起（现为残骸自带 / 0）。
    /// </summary>
    public struct EmgEntityProps
    {
        public EmgEntKind Kind;
        /// <summary>0 = 本种类；1 = 哨戒桩（无人机结构里插地的那种）；2 = 破片（弹体池里的 Fragment）。</summary>
        public byte Sub;
        public float Mass;
        public float Conductive;
        public float Flammable;
        public float Magnetic;
        public float Heat;
        public float Charge;
        /// <summary>结构完整度 = 剩余耐久比例（单位、残骸；弹体 / 无人机 / 破片恒 1）。</summary>
        public float Integrity;
        public byte Material;
        public byte Displace;
        public float ResistCold, ResistHeat, ResistElec, ResistCorrode, ResistMag, ResistHack;
    }

    /// <summary>EMG1-FLASH-01：一架无人机 / 哨戒桩的刷写读数（悬停“刷写 3 / 4 条（带宽已满）”“离母机过远，暂停 1 条”与自检）。</summary>
    public struct CombatDroneFlash
    {
        public int Id;
        public int Owner;
        /// <summary>放出它的武器（变体表挂在这把武器上）。</summary>
        public int Weapon;
        /// <summary>此刻开火用的武器（变体表里“前 Held − Pause 条”那一行）与它的程序（−1 = 只有基础攻击）。</summary>
        public int ExecWeapon;
        public int Program;
        /// <summary>持有的条数（带宽截断后；剥离后按去核心列表）。</summary>
        public int Held;
        /// <summary>当前列表的全长（剥离后 = 去核心长度）；Length − Held = 被带宽截掉的条数。</summary>
        public int Length;
        /// <summary>程序全长（含核心）。</summary>
        public int FullLength;
        /// <summary>离母机过远暂停的末尾条数。</summary>
        public int Pause;
        public bool Stripped;
        public bool Anchored;
        public int Truncated => Length - Held;
        public int Effective => Held - Pause;
    }

    /// <summary>涌现发号器与总线计数（独立的长度 1 数组，不并进 <see cref="CombatScalars"/>：那里有多处“读出—改—写回”的局部副本，混在一起会把中途发的号写丢）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgScalars
    {
        /// <summary>下一个弹体 / 无人机 / 残骸 / 斑块 / 场的稳定 ID（从 1 起；0 = 没有）。读档后接着发号。</summary>
        public int NextProjId;
        public int NextDroneId;
        public int NextWreckId;
        public int NextPatchId;
        public int NextFieldId;
        /// <summary>下一个因果链 ID（ADR ⑨：每次根出手 / 世界源排放 / 热更注入分配一个；从 1 起，0 = 没有链）。</summary>
        public int NextChain;
        /// <summary>总线序号（下一条事件的 Seq）。</summary>
        public int EmgSeq;
        /// <summary>总线头指针（已处理到哪；运行时量——快照只写未处理的事件，读档后为 0）。</summary>
        public int BusHead;
        /// <summary>EMG1-BUS-01：本步开始时的 EmgSeq（Seq ≥ 它的事件是本步入队的；积压保护只合并同一步的）。运行时量，不进快照。</summary>
        public int StepFirstSeq;
        /// <summary>EMG1-TRIG-01：下一个区域的稳定 ID（只有带程序的区域发号，做事件宿主；Emg 标量段第 8 个字段，短段读到取 1）。</summary>
        public int NextZoneId;
        /// <summary>EMG1-TRIG-01：待生成表里第一条“未决”请求的位置（运行时游标，不进快照；取 0 总是正确，只是多扫几条）。
        /// 和 <see cref="HashDirty"/> 一样放在 EmgS 里：内核一步在 CombatStepJob 的 CombatData 副本上跑，值字段的修改带不回主线程。</summary>
        public int ArmCursor;
        /// <summary>EMG1-TRIG-01：EmgHash 要重建（障碍 / 残骸变了；读档、清战斗后为 1）。运行时量，不进快照。</summary>
        public byte HashDirty;
        /// <summary>EMG1-ENT-01：本地点生成过的残骸、到期崩解、满容量时最早的崩解、被打掉的残骸（可靠计数，Emg 标量段尾部追加，短段读到取 0；崩解“计数可见、不静默”）。</summary>
        public int WrecksBorn;
        public int WrecksAged;
        public int WrecksCapped;
        public int WrecksDestroyed;

        public static EmgScalars Initial => new EmgScalars { NextProjId = 1, NextDroneId = 1, NextWreckId = 1, NextPatchId = 1, NextFieldId = 1, NextChain = 1, NextZoneId = 1, HashDirty = 1 };
    }

    /// <summary>
    /// EMG1-TRIG-01（FGR-EMG-034 / 063；ADR ③“弹体池满或区域满：生成请求留在总线里顺延，生成时才开始计寿命”）：一条待生成请求。
    /// 生产点（命中 / 终结 / 定时……事件入队那一刻）按宿主节点的触发子节点记下“母载荷参数”（伤害、武器、阵营、位置、方向、母落区）；总线处理到那条事件（<see cref="Seq"/>）、
    /// 触发读端被唤起且链深未到 3 时置 <see cref="Armed"/>（同时发 evt.split）；之后每步按先进先出生成，弹体池 / 区域满或同链同时存活已满就留到下一步。
    /// 没被唤起的（链深截断 / 事件被积压保护合并）在那条事件处理过后作废（计 SpawnDropped）。同一结构也承载格斗 / 力场的定时（从出手起算，到点发 evt.timer）
    /// 与弹体池满时顺延的 SpawnFlight 根弹体。进快照（Emg 段 11）。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgSpawn
    {
        /// <summary>定时：到点时刻；其余 0。</summary>
        public double At;
        public double2 Pos;
        public float2 Dir;
        public float Damage;
        public float ZoneRadius;
        public float ZoneLife;
        public int Prog;
        public int Chain;
        /// <summary>出手单位 Id（0 = 已不在）。</summary>
        public int Owner;
        public int Weapon;
        /// <summary>命中触发：被命中的单位 Id（子弹药不打它、找“其他”敌对单位）；根弹体：目标单位 Id。</summary>
        public int Other;
        /// <summary>定时：宿主实体 Id。</summary>
        public int Ent;
        /// <summary>唤起它的那条事件的 Seq（定时 / 根弹体不用）。</summary>
        public int Seq;
        /// <summary>子载荷：子节点（程序内序号）；定时：宿主节点；根弹体：0。</summary>
        public byte Node;
        /// <summary>生成物的链深（子载荷 = 事件链深 + 1；定时 / 根弹体 = 宿主链深）。</summary>
        public byte Depth;
        /// <summary>还要生成几份。</summary>
        public byte Count;
        public byte Kind;
        public byte Faction;
        public EmgProjFlags Flags;
        public EmgEntKind EntKind;
        /// <summary>1 = 已被唤起（可以生成）。</summary>
        public byte Armed;
        /// <summary>子形态码（1～5）；根弹体 0。</summary>
        public byte SubForm;
        /// <summary>子载荷的代（= 子节点的 Gen）。</summary>
        public byte Gen;
        /// <summary>已生成几份（第 k 份指向第 k 近的目标 / 径向均分的第 k 个方向）。</summary>
        public short Done;
    }

    /// <summary>EMG1-TRIG-01：fg.TbSubPayload 一行的内核副本（下标 = 子形态码；热更层按表写入，缺省为 FG19A 第 2.1.1 节初值）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EmgSubForm
    {
        public float Speed;
        public float Life;
        public float LifeRatio;
        public float Radius;
        public float RadiusRatio;

        /// <summary>FG19A 第 2.1.1 节 5 行初值（临时值，由 FG15-BAL-01 重定）。</summary>
        public static EmgSubForm Default(int code) => code switch
        {
            EmgConst.SubBullet => new EmgSubForm { Speed = 28f, Life = 0.5f, Radius = 0.1f },
            EmgConst.SubWave => new EmgSubForm { Speed = 20f, Life = 0.15f, Radius = 0.3f },
            EmgConst.SubDroneBullet => new EmgSubForm { Speed = 24f, Life = 0.2f, Radius = 0.1f },
            EmgConst.SubPulse => new EmgSubForm { Speed = 0f, Life = 0.3f, Radius = 1.2f },
            EmgConst.SubZone => new EmgSubForm { Speed = 0f, LifeRatio = 0.5f, RadiusRatio = 0.5f },
            _ => default,
        };
    }

    /// <summary>
    /// 内核随机（FGR-EMG-213；ADR ⑬“确定性”、T10）：一律用无状态哈希，不维护随机数状态、与求值顺序无关、不进快照。
    /// 核实：合并云端 M10 后 Sim/Combat/ 里没有任何随机数流（本卡开工 grep Random 无命中），所以不存在“沿用旧流”的问题。
    /// 用法：<c>EmgRng.Unit(chain, seq, salt, siteSeed)</c>；salt 区分同一事件里的不同用途（寻的 / 侧偏 / 破片方向……）。
    /// </summary>
    public static class EmgRng
    {
        public static uint Hash(uint a, uint b, uint salt, uint seed) => math.hash(new uint4(a, b, salt, seed));

        /// <summary>[0, 1) 均匀值（取哈希高 24 位）。</summary>
        public static float Unit(uint a, uint b, uint salt, uint seed) => (Hash(a, b, salt, seed) >> 8) * (1f / 16777216f);
    }
}
