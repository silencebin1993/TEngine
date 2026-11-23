using Unity.Collections;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>EMG1-KRN-01：涌现容器的建 / 放与每单位段随单位的追加、搬移、截断（字段声明在 CombatData.cs，同一结构体的 partial）。</summary>
    public partial struct CombatData
    {
        /// <summary>EMG2-MAT-01（ADR-EMG-001 ⑤ B′）：物质斑块——扩展字段的 <see cref="CombatZone"/>，独立于读法区域 <see cref="Zones"/>，容量分开计；不压实，Matter = 0 是墓碑（生成时复用）。</summary>
        public NativeList<CombatZone> Patches;
        /// <summary>EMG2-MAT-01：活跃场（EMG2-FLD-01 生成；本卡铺好结构与快照段）。</summary>
        public NativeList<EmgField> Fields;
        /// <summary>EMG2-RECL-01：桥接导线（传导图的临时边，≤ <see cref="EmgWireConst.Capacity"/>；进快照段 20）。</summary>
        public NativeList<EmgWire> Wires;
        /// <summary>EMG2-REACT-02：单位身上正在走的 C05 / C07 计时（只有中招的单位才有一行）。</summary>
        public NativeList<EmgCoatFx> CoatFx;
        /// <summary>FG11-ATTR-01 ②：斑块挂上的沾染位的归因（稀疏表，见 <see cref="EmgCoatAttr"/>；进快照段 37）。</summary>
        public NativeList<EmgCoatAttr> CoatAttr;
        /// <summary>FG15-SYS-02（DEBT-FG11ATTR01-02）：沾染来源表的索引（(单位 Id &lt;&lt; 5) | 位 → 行下标）。派生数据，不进快照、不进哈希；
        /// <see cref="CoatIndexValid"/> = 0（清表、读档、保序删行之后）时下一次查询整表重建，其余增删就地维护——查询由 O(表长) 变 O(1)，结果逐字不变。</summary>
        public NativeParallelHashMap<long, int> CoatIndex;
        /// <summary>[0] = 1：索引与表一致。放在原生容器里而不是值字段——内核一步在 CombatStepJob 的 CombatData 副本上跑，值字段的修改带不回主线程（索引会过期）。</summary>
        public NativeArray<byte> CoatIndexValid;
        /// <summary>自检对照组：1 = 沾染来源表退回逐行扫描（FG11-ATTR-01 原实现），用来逐步比状态哈希证明索引版逐字等价。</summary>
        public byte CoatIndexOff;
        /// <summary>FG15-SYS-02（DEBT-FG2FW02-06）：单位位置写入计数（StepZones 建表到查表之间可能执行的 Pos 写入都 +1——区域效果、反应、底盘、场、熔炉、蠕虫等；攻城位移 CombatSiege 与蠕虫出土 CombatKernel.Worm 不在 StepZones 里执行，没有 +1；
        /// 新增在区域节拍里挪单位的写点必须 +1。每次 StepZones 开始都重建，只在一步之内比较，不进快照 / 哈希）。
        /// 区域节拍的单位格链（<see cref="EmgHash.UnitHead"/>）按它判断建表后有没有单位被挪动——挪动了就退回逐个扫描或重建，结果与逐单位扫描逐字一致。</summary>
        public int PosEpoch;
        public int ZoneGridEpoch;
        public int ZoneGridN;
        public int ZoneScans;
        public int ZoneGridBuilds;
        public float ZoneGridMaxR;
        public byte ZoneGridOn;
        public byte ZoneGridBusy;
        /// <summary>自检对照组：1 = 区域节拍退回逐单位扫描（原实现）。</summary>
        public byte ZoneGridOff;
        /// <summary>FG11-ATTR-01 ① / ④：每单位每相态（含累积阶段）的来源单位 Id（单位 × <see cref="EmgPhaseConst.Count"/>，不进 EmgUnitState；进快照段 36）。</summary>
        public NativeList<int> EmgPhaseSrc;
        /// <summary>FG11-ATTR-01 ④：持久链归属（直映射 <see cref="EmgConst.ChainOwnSlots"/> 格；进快照段 38）。</summary>
        public NativeArray<EmgChainOwn> EmgChainOwns;
        /// <summary>EMG3-CHS-01：碾压拖带 / 拖挂单位的运行态（快照段 22；只有这两种特性的单位有一行）。</summary>
        public NativeList<EmgTraitRun> TraitRun;
        /// <summary>EMG3-CHS-01（ADR ⑩ 版本 18）：元程序运行态（快照段 23；本卡只铺结构，执行者归元程序卡，表恒空）。</summary>
        public NativeList<EmgMetaRun> MetaRun;
        /// <summary>EMG3-SQD-01（FGR-EMG-132）：装信号中继的机器（快照段 25；热更层按结构模块的 sqd.relay scope 声明写，只有这些机器做转发遍历，ADR ④）。</summary>
        public NativeList<EmgRelay> Relays;
        /// <summary>EMG4-HIJ-01（FGR-EMG-163，ADR ⑰）：被信号劫持的敌机（快照段 27；每名玩家 ≤ emg.hijack.max，空表时每步一次长度比较）。</summary>
        public NativeList<EmgHijack> Hijacks;
        /// <summary>FG11-FAC-01（FGR-FAC-008 超频专属语法）：带超频参数的单位的运行状态（快照段 34；空表时每步一次长度比较）。</summary>
        public NativeList<CombatOverclock> Overclocks;
        /// <summary>FG12-EVT-02：掘进蠕虫（分节链；进快照段 39 与状态哈希）。</summary>
        public NativeList<CombatWorm> Worms;
        /// <summary>FG13-FAC-01：白潮单位运行状态 / 轨道打击 / 灭活焦土（快照段 40～42）。</summary>
        public NativeList<CombatFleet> Fleets;
        public NativeList<CombatFleetStrike> FleetStrikes;
        public NativeList<CombatFleetMark> FleetMarks;
        /// <summary>FG12-EVT-02：本步的大规模爆炸（x, y, 计次, 最大半径）；热更层每个世界步取走，不进快照。</summary>
        public NativeList<float4> WormBlasts;
        /// <summary>FG12-EVT-02：半径不小于多少格的爆炸算大规模（热更层按 env.vibration.blast_radius 写；0 = 不记）。派生量，不进快照。</summary>
        public float BlastMinRadius;
        /// <summary>FG11-FAC-01（DEBT-FG7ENV05-01 / FG8EXP02-03 高温辐射）：高温辐射区（x, y, 内半径, 外半径；派生量不进快照，热更层打开 / 读档时写）。</summary>
        public NativeList<float4> RadZones;
        /// <summary>高温辐射区内的热量上限倍率与散热倍率（不在区里 = 1）。</summary>
        public float RadCapMult;
        public float RadDissMult;
        /// <summary>FG11-REG-01（FG09 第 7 章“站在运转中的冷却塔附近可以恢复热量上限”）：运转中的冷却塔遗址（x, y, 半径；格）。派生量不进快照，热更层打开 / 读档 / 塔被摧毁时写。
        /// 圈内高温辐射的热量上限倍率不生效（散热倍率照常）。</summary>
        public NativeList<float3> CoolZones;
        /// <summary>EMG4-TER-01（FGR-EMG-152）：熔坑（快照段 28；≤ emg.terrain.pit_max，空表时移动只多一次长度比较）。</summary>
        public NativeList<EmgPit> Pits;
        /// <summary>EMG4-TER-01（FG19B V06 / V07“同一位置累计”）：累计点（x, y, 熔融金属份量, 酸份量；快照段 29，≤ <see cref="EmgPitConst.AccCapacity"/>）。</summary>
        public NativeList<float4> PitAcc;
        /// <summary>EMG4-TER-01：熔坑计数与发号（下标见 <see cref="EmgPitConst"/>；快照段 30）。</summary>
        public NativeArray<int> PitStats;
        /// <summary>EMG4-TER-01：可熔地面圆（x, y, 半径；远征地点按生成内容里的废墟格写）。派生量（热更层按生成规则写），不进快照，读档沿用当前的。</summary>
        public NativeList<float3> MeltGround;
        /// <summary>EMG4-TER-02（FG19B V03）：焦土“同一位置”累计点（x, y, 累计燃烧秒, 最大火势半径；快照段 31，≤ <see cref="EmgPitConst.AccCapacity"/>）。</summary>
        public NativeList<float4> ScorchAcc;
        /// <summary>EMG4-TER-02：焦土圈（x, y, 半径；热更层按战役存档里的战痕写，≤ <see cref="EmgGroundMarkConst.ScorchGroundCapacity"/>）。派生量，不进快照，读档沿用当前的。</summary>
        public NativeList<float3> ScorchGround;
        /// <summary>EMG3-FUSE-01（FGR-EMG-044）：熔合线索同现计数（下标 = 配方书序号，≤ 32）；热更层按 <see cref="EmgScalars.ClueDirty"/> 取走后清零（可靠计数 + 脏位，不按帧扫描；待取走次数与去重环随快照段 26 存读档，脏位读档时由待取走次数重建）。</summary>
        public NativeArray<int> EmgClue;
        /// <summary>EMG4-FUSE-02：线索在途登记（[配方 × ClueHalfRing]：x = 链号、y = 已出现的侧位 1 / 2），随快照段 32。EMGR-FUSE-01：每配方 128 格按链号哈希开放寻址（CombatLogic.EmgClueProbe），32 × 128 × 8 字节 = 32 KB。</summary>
        public NativeArray<int2> EmgClueHalf;
        /// <summary>EMG4-FUSE-02：端口码 → 线索侧位（位 2i = 配方 i 的第 1 侧、2i + 1 = 第 2 侧）。表数据（热更层按 FG19A 第 4.3 节端口对下发），不进快照、读档沿用当前表。</summary>
        public NativeArray<ulong> EmgClueSides;
        /// <summary>EMG3-CHS-01：底盘特性参数（下标 = <see cref="EmgTraitConst"/> 机制，4 个参数 = fg.TbChassisTrait paramKeys 的 emg.trait.* 值；表数据，读档沿用当前的）。</summary>
        public NativeArray<float4> EmgTraitParams;
        /// <summary>EMG3-CHS-02：自检用测试水域（x, y = 圆心，z = 半径；z ≤ 0 = 没有）。正式水面地形（FG18-SEA-01）落地前 chs.water 只由它置位，正式路径恒 0；不进快照，读档沿用当前的。</summary>
        public float4 WaterTest;
        /// <summary>EMG2-MAT-01：物质数值表（下标 = 物质码 1～17；热更层按 fg.TbMatter 写，是表数据，读档沿用当前的）。</summary>
        public NativeArray<EmgMatter> EmgMatters;
        /// <summary>EMG2-RULE-01：编好的通道规则（fg.TbChannelRule，≤ 256；表数据，读档沿用当前的）与按物质码的索引（int2 = EmgRuleOrder 里的起点、条数；按优先级升序）。</summary>
        public NativeList<EmgRule> EmgRules;
        public NativeArray<Unity.Mathematics.int2> EmgRuleIdx;
        public NativeList<short> EmgRuleOrder;
        /// <summary>EMG2-MAT-01（DEBT-EMG1ENT01-07）：实体档案表每行的稳定键（热更层按 fg.TbEntityProfile 行 ID 写；读档时按键把单位 / 残骸的档案下标挪到当前表）。</summary>
        public NativeList<int> EmgProfileKeys;
        /// <summary>EMG2-MAT-02（ADR ⑤）：设施受击聚合——待发布（按键合并、不限条数但键数 ≤ 斑块 + 液洼）与已发布（每步 ≤ 64 条、热更层取走）。派生量，不进快照。</summary>
        public NativeList<EmgStructHit> EmgStructPending;
        public NativeList<EmgStructHit> EmgStructOut;
        /// <summary>EMG2-MAT-02：最近一次 SpawnZone 落在哪（≥ 0 = Zones 下标；&lt; 0 = 迁入斑块，Patches 下标 = −1 − 值；int.MinValue = 没生成）。调用方改“刚生成的那块”的外观 / 阶段 / 受寒用。</summary>
        public int LastZoneAt;
        /// <summary>EMG2-MAT-02（FG19B §1.5“液氮 → mat.cryo”）：冻结标签在本地点的状态位（热更层按 fg.TbStatusTag 写；液氮驻留区 / 液氮池迁入斑块时据此映射物质码）。派生量。</summary>
        public uint FrozenBit;
        /// <summary>EMG2-PHS-01：相态规则（fg.TbPhaseRule 7 行，下标 = 累积量下标；表数据，读档沿用当前的）。</summary>
        public NativeArray<EmgPhaseRule> EmgPhases;
        /// <summary>EMG2-FLD-01：场 × 载体规则（fg.TbFieldCarrierRule 20 行，下标 = EmgFieldConst 行号；表数据，读档沿用当前的）。</summary>
        public NativeArray<EmgFieldRule> EmgFieldRules;
        /// <summary>EMG2-PHS-01（ADR ⑭ #9）：电击标签在本地点的状态位（热更层按 fg.TbStatusTag 写；读 15 / 16 版快照把 ImmuneMask 的电击位映射成电抗覆盖）。派生量。</summary>
        public uint ShockBit;

        private void EmgCreate(int capacity)
        {
            Patches = new NativeList<CombatZone>(8, Allocator.Persistent);
            Fields = new NativeList<EmgField>(4, Allocator.Persistent);
            Wires = new NativeList<EmgWire>(4, Allocator.Persistent);
            CoatFx = new NativeList<EmgCoatFx>(4, Allocator.Persistent);
            CoatAttr = new NativeList<EmgCoatAttr>(4, Allocator.Persistent);
            CoatIndex = new NativeParallelHashMap<long, int>(64, Allocator.Persistent);
            CoatIndexValid = new NativeArray<byte>(1, Allocator.Persistent);
            EmgPhaseSrc = new NativeList<int>(capacity * EmgPhaseConst.Count, Allocator.Persistent);
            EmgChainOwns = new NativeArray<EmgChainOwn>(EmgConst.ChainOwnSlots, Allocator.Persistent);
            TraitRun = new NativeList<EmgTraitRun>(4, Allocator.Persistent);
            MetaRun = new NativeList<EmgMetaRun>(0, Allocator.Persistent);
            Relays = new NativeList<EmgRelay>(0, Allocator.Persistent);
            Hijacks = new NativeList<EmgHijack>(0, Allocator.Persistent);
            Overclocks = new NativeList<CombatOverclock>(0, Allocator.Persistent);
            Worms = new NativeList<CombatWorm>(0, Allocator.Persistent);
            Fleets = new NativeList<CombatFleet>(0, Allocator.Persistent);
            FleetStrikes = new NativeList<CombatFleetStrike>(0, Allocator.Persistent);
            FleetMarks = new NativeList<CombatFleetMark>(0, Allocator.Persistent);
            WormBlasts = new NativeList<float4>(0, Allocator.Persistent);
            RadZones = new NativeList<float4>(0, Allocator.Persistent);
            CoolZones = new NativeList<float3>(0, Allocator.Persistent);
            Pits = new NativeList<EmgPit>(0, Allocator.Persistent);
            PitAcc = new NativeList<float4>(0, Allocator.Persistent);
            PitStats = new NativeArray<int>(EmgPitConst.StatLen, Allocator.Persistent);
            MeltGround = new NativeList<float3>(0, Allocator.Persistent);
            ScorchAcc = new NativeList<float4>(0, Allocator.Persistent);
            ScorchGround = new NativeList<float3>(0, Allocator.Persistent);
            EmgClue = new NativeArray<int>(EmgConst.ClueLen, Allocator.Persistent); // EMG3-FUSE-01：[0, 32) 待取走的同现次数，其后各配方的环头与最近计过的链号环
            EmgClueHalf = new NativeArray<int2>(EmgConst.ClueSlots * EmgConst.ClueHalfRing, Allocator.Persistent);
            EmgClueSides = new NativeArray<ulong>(EmgConst.ClueSidePorts, Allocator.Persistent);
            EmgTraitParams = new NativeArray<float4>(EmgTraitConst.Count, Allocator.Persistent);
            EmgMatters = new NativeArray<EmgMatter>(EmgMatterConst.Count + 1, Allocator.Persistent);
            EmgRules = new NativeList<EmgRule>(8, Allocator.Persistent);
            EmgRuleIdx = new NativeArray<Unity.Mathematics.int2>(EmgRuleConst.IndexSlots, Allocator.Persistent);
            EmgRuleOrder = new NativeList<short>(8, Allocator.Persistent);
            EmgPhases = new NativeArray<EmgPhaseRule>(EmgPhaseConst.Count, Allocator.Persistent);
            for (int k = 0; k < EmgPhaseConst.Count; k++)
            {
                EmgPhases[k] = EmgPhaseConst.Default(k);
            }
            EmgFieldRules = new NativeArray<EmgFieldRule>(EmgFieldConst.RuleCount, Allocator.Persistent);
            for (int k = 0; k < EmgFieldConst.RuleCount; k++)
            {
                EmgFieldRules[k] = EmgFieldConst.Default(k);
            }
            EmgProfileKeys = new NativeList<int>(EmgConst.ProfileSlots, Allocator.Persistent);
            EmgStructPending = new NativeList<EmgStructHit>(8, Allocator.Persistent);
            EmgStructOut = new NativeList<EmgStructHit>(8, Allocator.Persistent);
            LastZoneAt = int.MinValue;
            EmgS = new NativeArray<EmgScalars>(1, Allocator.Persistent);
            EmgS[0] = EmgScalars.Initial;
            EmgPrograms = new NativeList<EmgProgram>(4, Allocator.Persistent);
            EmgNodes = new NativeList<EmgNode>(8, Allocator.Persistent);
            EmgInstrs = new NativeList<EmgInstr>(16, Allocator.Persistent);
            FlashHead = new NativeList<Unity.Mathematics.int4>(4, Allocator.Persistent);
            FlashMap = new NativeList<int>(16, Allocator.Persistent);
            EmgBus = new NativeList<EmgEvent>(16, Allocator.Persistent);
            Wrecks = new NativeList<EmgWreck>(4, Allocator.Persistent);
            EProfile = new NativeList<short>(capacity, Allocator.Persistent);
            EmgUnit = new NativeList<EmgUnitState>(capacity, Allocator.Persistent);
            EmgStats = new NativeArray<EmgBusStats>(1, Allocator.Persistent);
            EmgKindEnq = new NativeArray<long>(EmgConst.EventKinds + 1, Allocator.Persistent);
            EmgLastByKind = new NativeArray<EmgEvent>(EmgConst.EventKinds + 1, Allocator.Persistent);
            EmgAct = new NativeList<int>(0, Allocator.Persistent);
            EmgRowAct = new NativeList<int>(0, Allocator.Persistent);
            EmgListen = 0u;
            EmgCtx = EmgCtx.None;
            EmgPending = new NativeList<EmgSpawn>(4, Allocator.Persistent);
            EmgSubForms = new NativeArray<EmgSubForm>(6, Allocator.Persistent);
            for (int k = 1; k <= 5; k++)
            {
                EmgSubForms[k] = EmgSubForm.Default(k);
            }
            EmgHash = EmgHash.Create();
            EmgProfiles = new NativeList<EmgProfile>(EmgConst.ProfileSlots, Allocator.Persistent);
            EmgProfiles.Add(EmgProfile.Steel(1f));                  // 弹体
            EmgProfiles.Add(EmgProfile.Steel(4f));                  // 重型弹体
            EmgProfiles.Add(EmgProfile.Steel(0.5f, 0.5f, 0.1f, 0.5f)); // 无人机
            EmgProfiles.Add(EmgProfile.Steel(6f, 0.9f, 0.05f, 0.9f, 0)); // 哨戒桩（插地不位移）
            EmgProfiles.Add(EmgProfile.Steel(0.2f, 0.9f, 0f, 0.9f)); // 破片
            EmgProfiles.Add(EmgProfile.Steel(6f));                  // 机器（轮式）
            EmgProfiles.Add(EmgProfile.Steel(0f));                  // 敌人（8 × 半径²）
            EmgProfiles.Add(EmgProfile.Steel(200f, 0.9f, 0.1f, 0.9f, 0)); // 首领
            EmgProfiles.Add(EmgProfile.Steel(20f, 0.9f, 0.1f, 0.9f, 0));  // 炮塔
            EmgProfiles.Add(EmgProfile.Steel(20f, 0.9f, 0.1f, 0.9f, 0));  // 结构
            EmgProfiles.Add(EmgProfile.Steel(0f));                  // 残骸（继承原单位）
            EmgEntParams = new NativeArray<EmgEntityParams>(1, Allocator.Persistent);
            EmgEntParams[0] = EmgEntityParams.Default;
            EmgMerge = new NativeList<EmgMergeReq>(4, Allocator.Persistent);
            EmgMergeCont = 0;
            EmgRing = new NativeArray<EmgChainRec>(EmgConst.ChainRing, Allocator.Persistent);
            EmgRingHead = new NativeArray<int>(2, Allocator.Persistent);
            EmgRuleKey = new NativeArray<int>(EmgConst.RuleSlots, Allocator.Persistent);
            EmgRuleCount = new NativeArray<int>(EmgConst.RuleSlots, Allocator.Persistent);
            EmgPortCount = new NativeArray<long>(EmgConst.EventKinds + 1, Allocator.Persistent);
            EmgRuleFeed = new NativeArray<EmgRuleFeed>(EmgConst.RuleSlots, Allocator.Persistent);
            EmgDirty = new NativeList<short>(8, Allocator.Persistent);
            EmgDirtyMark = new NativeArray<byte>(EmgConst.RuleSlots, Allocator.Persistent);
            EmgEraDirty = new NativeList<int>(8, Allocator.Persistent);
            EmgFriendly = new NativeList<int3>(8, Allocator.Persistent);
            EmgExtreme = new NativeList<int>(4, Allocator.Persistent);
            EmgChainTally = new NativeArray<int4>(EmgConst.ChainTally, Allocator.Persistent);
            EmgTallyOver = new NativeParallelHashMap<int, int4>(EmgConst.ChainTallyOverflow, Allocator.Persistent);
            EmgTallyDropMax = new NativeArray<int>(1, Allocator.Persistent);
            EmgAttrRows = new NativeList<EmgAttrRow>(8, Allocator.Persistent);
        }

        private void EmgDispose()
        {
            Patches.Dispose();
            Fields.Dispose();
            Wires.Dispose();
            CoatFx.Dispose();
            CoatAttr.Dispose();
            CoatIndex.Dispose();
            CoatIndexValid.Dispose();
            EmgPhaseSrc.Dispose();
            EmgChainOwns.Dispose();
            TraitRun.Dispose();
            MetaRun.Dispose();
            Relays.Dispose();
            Hijacks.Dispose();
            Overclocks.Dispose();
            Worms.Dispose();
            Fleets.Dispose();
            FleetStrikes.Dispose();
            FleetMarks.Dispose();
            WormBlasts.Dispose();
            RadZones.Dispose();
            CoolZones.Dispose();
            Pits.Dispose();
            PitAcc.Dispose();
            PitStats.Dispose();
            MeltGround.Dispose();
            ScorchAcc.Dispose();
            ScorchGround.Dispose();
            EmgClue.Dispose();
            EmgClueHalf.Dispose();
            EmgClueSides.Dispose();
            EmgTraitParams.Dispose();
            EmgMatters.Dispose();
            EmgRules.Dispose();
            EmgRuleIdx.Dispose();
            EmgRuleOrder.Dispose();
            EmgPhases.Dispose();
            EmgFieldRules.Dispose();
            EmgProfileKeys.Dispose();
            EmgStructPending.Dispose();
            EmgStructOut.Dispose();
            EmgS.Dispose();
            EmgPrograms.Dispose();
            EmgNodes.Dispose();
            EmgInstrs.Dispose();
            FlashHead.Dispose();
            FlashMap.Dispose();
            EmgBus.Dispose();
            Wrecks.Dispose();
            EProfile.Dispose();
            EmgUnit.Dispose();
            EmgStats.Dispose();
            EmgKindEnq.Dispose();
            EmgLastByKind.Dispose();
            EmgAct.Dispose();
            EmgRowAct.Dispose();
            EmgPending.Dispose();
            EmgSubForms.Dispose();
            EmgHash.Dispose();
            EmgProfiles.Dispose();
            EmgEntParams.Dispose();
            EmgMerge.Dispose();
            EmgRing.Dispose();
            EmgRingHead.Dispose();
            EmgRuleKey.Dispose();
            EmgRuleCount.Dispose();
            EmgPortCount.Dispose();
            EmgRuleFeed.Dispose();
            EmgDirty.Dispose();
            EmgDirtyMark.Dispose();
            EmgEraDirty.Dispose();
            EmgFriendly.Dispose();
            EmgExtreme.Dispose();
            EmgChainTally.Dispose();
            EmgTallyOver.Dispose();
            EmgTallyDropMax.Dispose();
            EmgAttrRows.Dispose();
        }

        private void EmgAppend()
        {
            EProfile.Add(-1);
            EmgUnit.Add(default);
            for (int k = 0; k < EmgPhaseConst.Count; k++)
            {
                EmgPhaseSrc.Add(0);
            }
        }

        private void EmgMoveSlot(int from, int to)
        {
            EProfile[to] = EProfile[from];
            EmgUnit[to] = EmgUnit[from];
            for (int k = 0; k < EmgPhaseConst.Count; k++)
            {
                EmgPhaseSrc[to * EmgPhaseConst.Count + k] = EmgPhaseSrc[from * EmgPhaseConst.Count + k];
            }
        }

        private void EmgTruncate(int length)
        {
            EProfile.ResizeUninitialized(length);
            EmgUnit.ResizeUninitialized(length);
            EmgPhaseSrc.ResizeUninitialized(length * EmgPhaseConst.Count);
        }

        /// <summary>清掉随战斗走的涌现状态（总线、残骸、发号器）。程序表与武器表一样是“表”，另行覆盖。</summary>
        private void EmgClearState()
        {
            EmgBus.Clear();
            Wrecks.Clear();
            Patches.Clear();
            Fields.Clear();
            Wires.Clear();
            CoatFx.Clear();
            CoatAttr.Clear();
            CoatIndexValid[0] = 0;
            TraitRun.Clear();
            MetaRun.Clear();
            Relays.Clear();
            Hijacks.Clear();
            Overclocks.Clear();
            Worms.Clear();
            Fleets.Clear();
            FleetStrikes.Clear();
            FleetMarks.Clear();
            WormBlasts.Clear();
            Pits.Clear();
            PitAcc.Clear();
            ScorchAcc.Clear();
            for (int i = 0; i < PitStats.Length; i++)
            {
                PitStats[i] = 0;
            }
            for (int i = 0; i < EmgClue.Length; i++)
            {
                EmgClue[i] = 0;
            }
            for (int i = 0; i < EmgClueHalf.Length; i++)
            {
                EmgClueHalf[i] = default;
            }
            EmgStructPending.Clear();
            EmgStructOut.Clear();
            EmgPending.Clear();
            EmgS[0] = EmgScalars.Initial; // 游标归 0、EmgHash 置脏（EmgScalars.Initial）
            EmgResetStats();
            EmgCtx = EmgCtx.None;
            EmgClearChain();
        }

        /// <summary>EMG1-CHAIN-01：清因果链环、可靠计数、进给与脏列表（清战斗状态时；读档换一份新数据时天然为空 / 由快照填计数）。</summary>
        internal void EmgClearChain()
        {
            EmgRingHead[0] = 0;
            EmgRingHead[1] = 0;
            for (int i = 0; i < EmgConst.RuleSlots; i++)
            {
                EmgRuleKey[i] = 0;
                EmgRuleCount[i] = 0;
                EmgRuleFeed[i] = default;
                EmgDirtyMark[i] = 0;
            }
            for (int k = 0; k < EmgPortCount.Length; k++)
            {
                EmgPortCount[k] = 0;
            }
            EmgDirty.Clear();
            EmgEraDirty.Clear();
            EmgEraDirtyPublished = 0;
            EmgFriendly.Clear();
            EmgFriendlyPublished = 0;
            EmgExtreme.Clear();
            EmgExtremePublished = 0;
            for (int i = 0; i < EmgChainTally.Length; i++)
            {
                EmgChainTally[i] = int4.zero;
            }
            EmgTallyOver.Clear();
            EmgTallyDropMax[0] = 0;
            for (int i = 0; i < EmgChainOwns.Length; i++)
            {
                EmgChainOwns[i] = default;
            }
            EmgAttrRows.Clear();
            EmgAttrPublished = 0;
            EmgAttr = default;
            EmgRuleKeyNow = 0;
            EmgRuleOwnersNow = default;
            EmgDepthPublished = 0;
        }

        /// <summary>EMG1-BUS-01：清调试计数（清战斗状态时；读档换一份新数据时天然为 0）。</summary>
        internal void EmgResetStats()
        {
            EmgStats[0] = default;
            for (int k = 0; k < EmgKindEnq.Length; k++)
            {
                EmgKindEnq[k] = 0;
                EmgLastByKind[k] = default;
            }
        }

        /// <summary>未处理的事件条数。</summary>
        public int EmgBusPending => EmgBus.Length - EmgS[0].BusHead;
    }
}
