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
        /// <summary>EMG3-CHS-01：碾压拖带 / 拖挂单位的运行态（快照段 22；只有这两种特性的单位有一行）。</summary>
        public NativeList<EmgTraitRun> TraitRun;
        /// <summary>EMG3-CHS-01（ADR ⑩ 版本 18）：元程序运行态（快照段 23；本卡只铺结构，执行者归元程序卡，表恒空）。</summary>
        public NativeList<EmgMetaRun> MetaRun;
        /// <summary>EMG3-SQD-01（FGR-EMG-132）：装信号中继的机器（快照段 25；热更层按结构模块的 sqd.relay scope 声明写，只有这些机器做转发遍历，ADR ④）。</summary>
        public NativeList<EmgRelay> Relays;
        /// <summary>EMG4-HIJ-01（FGR-EMG-163，ADR ⑰）：被信号劫持的敌机（快照段 27；每名玩家 ≤ emg.hijack.max，空表时每步一次长度比较）。</summary>
        public NativeList<EmgHijack> Hijacks;
        /// <summary>EMG3-FUSE-01（FGR-EMG-044）：熔合线索同现计数（下标 = 配方书序号，≤ 32）；热更层按 <see cref="EmgScalars.ClueDirty"/> 取走后清零（可靠计数 + 脏位，不按帧扫描；待取走次数与去重环随快照段 26 存读档，脏位读档时由待取走次数重建）。</summary>
        public NativeArray<int> EmgClue;
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
            TraitRun = new NativeList<EmgTraitRun>(4, Allocator.Persistent);
            MetaRun = new NativeList<EmgMetaRun>(0, Allocator.Persistent);
            Relays = new NativeList<EmgRelay>(0, Allocator.Persistent);
            Hijacks = new NativeList<EmgHijack>(0, Allocator.Persistent);
            EmgClue = new NativeArray<int>(EmgConst.ClueLen, Allocator.Persistent); // EMG3-FUSE-01：[0, 32) 待取走的同现次数，其后各配方的环头与最近计过的链号环
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
            EmgAttrRows = new NativeList<EmgAttrRow>(8, Allocator.Persistent);
        }

        private void EmgDispose()
        {
            Patches.Dispose();
            Fields.Dispose();
            Wires.Dispose();
            CoatFx.Dispose();
            TraitRun.Dispose();
            MetaRun.Dispose();
            Relays.Dispose();
            Hijacks.Dispose();
            EmgClue.Dispose();
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
            EmgAttrRows.Dispose();
        }

        private void EmgAppend()
        {
            EProfile.Add(-1);
            EmgUnit.Add(default);
        }

        private void EmgMoveSlot(int from, int to)
        {
            EProfile[to] = EProfile[from];
            EmgUnit[to] = EmgUnit[from];
        }

        private void EmgTruncate(int length)
        {
            EProfile.ResizeUninitialized(length);
            EmgUnit.ResizeUninitialized(length);
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
            TraitRun.Clear();
            MetaRun.Clear();
            Relays.Clear();
            Hijacks.Clear();
            for (int i = 0; i < EmgClue.Length; i++)
            {
                EmgClue[i] = 0;
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
            EmgAttrRows.Clear();
            EmgAttrPublished = 0;
            EmgAttr = default;
            EmgRuleKeyNow = 0;
            EmgRuleOwnersNow = int4.zero;
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
