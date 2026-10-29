using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace BinGames.Sim.Combat
{
    /// <summary>
    /// FG0-ARCH-03（FG14 FGR-ARC-003 战斗逐单位逻辑下沉内核；FG15 FGR-SYS-041/042 规模与帧预算）：战斗内核的常量。
    ///
    /// 内核只认位置、半径、阵营、行为、武器参数与状态位，不认识“侦察机”“铸造重炮”之类的玩法概念（与 <see cref="SimWorld"/> 同一纪律）：
    /// Demo 的每一种敌人、每一套装配在热更层翻译成 <see cref="CombatWeapon"/> / <see cref="CombatBehaviorProfile"/> 数据后交给内核。
    /// 位置用 double2（1 单位 = 1 格 = 1 米），离原点一百万格仍是亚毫米精度（FGR-GEN-051）；画面换算在热更层按原点区块做。
    /// </summary>
    public static class CombatConst
    {
        /// <summary>存档快照格式版本（<see cref="CombatKernel.Serialize"/>）。不认识的版本整块不读、原样保留。
        /// 2 = FG0-ARCH-06：每个单位追加寻路状态与路线路点、待交给寻路内核的请求（读取仍认 1：寻路字段取默认）。
        /// 3 = FG2-FW-02：武器追加载体与读法参数；每个单位追加状态标签（掩码 / 到期 / 持续伤害 / 减速 / 易伤 / 来源）；
        /// 追加区域、回波、无人机三张表与三个计数（读取仍认 1、2：读法取“无”，状态与三张表为空）。
        /// 4 = FG2-FW-03：每个单位追加状态标签叠层；追加每条反应的触发次数 / 反应额外伤害与“反应总次数”计数（读取仍认 1～3：叠层按已有标签各 1 层，反应计数为 0）。
        /// 5 = FG2-FW-04（DEBT-FG2FW03-02）：每个单位追加每个已挂状态位自己的到期时间（读取仍认 1～4：各位的到期 = 整组到期）。
        /// 6 = FG2-FW-04 修复：每个单位追加区域减速位自己的减速值（读取仍认 1～5：有区域减速位时取整组减速值，否则 0）。
        /// 7 = FG2-VFX-02：武器读法追加布区落点、无人机定点、反伤（比例 / 固定值 / 触及）；区域追加外观种类；无人机追加定点锚点
        /// （读取仍认 1～6：布区落在命中点、无人机伴飞、没有反伤、区域外观按“液池”、无人机无锚点）。
        /// 8 = FG2-E2E-01（FG-GAP-043）：武器追加“引信弹迹”标记（读取仍认 1～7：没有弹迹）。弹迹本身是表现，不进快照。
        /// 9 = FG6-DEF-01（FG06 FGR-DEF-002 / 004）：武器追加炮塔转速与每发补给；每个单位追加补给存量（读取仍认 1～8：转速 0 = 瞬间转向、不需要补给、存量 0）。
        /// 10 = FG6-DEF-02（FG06 FGR-DEF-012）：追加护盾表（读取仍认 1～9：没有护盾，热更层按记录重新登记）。
        /// 11 = FG6-DEF-05（FG06 FGR-DEF-030～032）：每个单位追加攻城属性（职能 / 撤退 / 目标类别 / 所属队伍 / 占地 / 对建筑倍率 / 随队维修计时 / 破墙目标）；
        /// 追加还没结算的溅射命中格（读取仍认 1～10：攻城属性为空、没有溅射）。流场不进快照：它是格网与结构单位的纯函数，读档后按同一输入重算（增量 = 全量，自检对照）。
        /// 12 = FG6-LOG-10（FG03 FGR-LOG-046）：区域种类追加“管线泄漏液洼”（<see cref="ZoneKindLeak"/>），区域追加阶段字节（液洼 / 已反应）（读取仍认 1～11：没有液洼，阶段 0）。
        /// 13 = FG9-FAC-01（FG09 FGR-FAC-008 静默专属语法）：行为参数追加潜伏 / 偏折场 / 断链束参数；每个单位追加静默状态（<see cref="CombatSilentUnit"/>：潜伏、现形倒计时、
        /// 偏折场阶段与计时、断链束目标与累计）（读取仍认 1～12：参数为 0 = 没有这些行为，状态为空）。
        /// 14 = FG9-FAC-02（FG09 FGR-FAC-008 铸造专属语法）：行为参数追加齐射 / 布雷 / 装填 / 冲锋 / 架炮驻锄 / 盲区参数；每个单位追加铸造状态（<see cref="CombatFoundryUnit"/>）；
        /// 计数追加齐射 / 对空拦截 / 布雷 / 引爆 / 冲锋次数（读取仍认 1～13：参数为 0 = 没有这些行为，状态与计数为 0）。
        /// 15 = FG10-FAC-01（FG09 FGR-FAC-008 澄净专属语法）：行为参数追加喷淋布区 / 枢轴喷灌 / 自爆与油迹 / 分裂 / 飞行与投放 / 抗性参数；每个单位追加澄净状态（<see cref="CombatClarityUnit"/>）；
        /// 区域种类追加“喷淋池”“油迹”（<see cref="ZoneKindPool"/> / <see cref="ZoneKindSlick"/>），区域追加受寒量；追加待结算的死亡效果（分裂 / 爆燃）；计数追加喷淋 / 冻结 / 自爆 / 爆燃 / 油迹点燃 / 分裂 / 再污染 / 酸沼腐蚀 / 冲洗次数
        /// （读取仍认 1～14：参数为 0 = 没有这些行为，状态与计数为 0，没有新种类的区域）。
        /// 16 = EMG1-KRN-01（FG19 FGR-EMG-045 / 211，ADR-EMG-001 ⑩）：在待结算死亡效果之后、读法表之前追加一块自描述的 Emg 段（段标记 + 段长度 + 字段数 + 记录数，
        /// 短段缺的字段取缺省、长段与不认识的段按长度跳过；读写器见 CombatKernel.Emergence.cs 的 EmgSegmentWriter / EmgSegmentReader）：发号器与总线序号、程序 / 节点 / 指令三张表（全表）、
        /// 武器的程序下标与 SpawnFlight、每单位档案下标与涌现状态基本段、每发弹体的稳定 Id 与宿主五元组 / 弹道 / 物质字段、每架无人机的稳定 Id、未处理的涌现事件、残骸
        /// （读取仍认 1～15：弹体 Prog = −1（按旧逻辑飞完）、0 代、按读入顺序补发弹体 / 无人机 Id、发号器取补发后的下一个、掩码与计时 0、档案取单位种类默认（−1）、
        /// 总线空且序号 0、没有残骸、武器没有程序）。</summary>
        public const int FormatVersion = 16;

        /// <summary>FG6-DEF-02：一个地点最多登记几座护盾（存储与逐弹体判定的上限；超出的不登记并由热更层写原因）。</summary>
        public const int MaxShields = 64;

        /// <summary>仍能读取的最老格式版本。</summary>
        public const int MinReadableFormat = 1;

        /// <summary>快照魔数 “CBK1”。</summary>
        public const uint Magic = 0x314B4243;

        public const int None = -1;

        /// <summary>标记跳转一次最多跳几个目标（存储上限；Demo 数值是 2，见 FracturedCityLayout.MarkJumpMaxTargets）。</summary>
        public const int MaxJumpTargets = 4;

        /// <summary>空间网格的格边长下限（米）。</summary>
        public const float MinGridCell = 1f;

        /// <summary>直控移动的坐标钳制“不限”。</summary>
        public const double NoClamp = 1e30;

        /// <summary>FG2-FW-02：配置里没填（0）时的区域 / 回波 / 无人机容量（超出的不生成并计数，不抛异常）。</summary>
        public const int DefaultZoneCapacity = 512;
        public const int DefaultEchoCapacity = 1024;

        /// <summary>FG6-DEF-02 复审修复：配置里没填（0）时，陷阱发射器场地自己的上限（与读法区域分开计数，互不挤占）。</summary>
        public const int DefaultFieldZoneCapacity = 512;

        /// <summary>区域种类（<see cref="CombatZone.Kind"/>）。</summary>
        public const byte ZoneKindReading = 0;
        public const byte ZoneKindField = 1;
        /// <summary>FG6-LOG-10（FGR-LOG-046）：管线被击穿后地上的液洼（不属于任何单位、中立：站进去的己方机器与敌人都挂上标签；
        /// 遇到能和它的标签起反应、且这条反应会留下残留区域的标签时整片反应成残留区域）。由热更层按液洼记录登记 / 更新 / 移除，数量由热更层封顶，不占读法与场地的上限。</summary>
        public const byte ZoneKindLeak = 2;
        /// <summary>液洼区域的阶段（<see cref="CombatZone.Phase"/>）：0 = 液洼（挂流体标签）；1 = 已反应（整片变成反应的残留区域，到期消失）。</summary>
        public const byte LeakPhasePuddle = 0;
        public const byte LeakPhaseReacted = 1;
        /// <summary>FG10-FAC-01（FGR-FAC-008“喷淋布区”）：澄净单位喷出的地面持续池（喷洒车的酸液池、液氮泵车的液氮池）。属于敌方阵营（只结算己方单位，炮塔 / 建筑也挨），
        /// 和读法区域一样按节拍伤害 / 挂标签，另带受寒量（<see cref="CombatZone.Chill"/>）。被冲洗（重叠的带湿润标签的区域冲淡酸液、带燃烧标签的区域蒸干液氮）时阶段记 1 并很快消失。
        /// 与油迹共用一个上限（<see cref="CombatData.PoolZoneCap"/>），不挤占读法区域。</summary>
        public const byte ZoneKindPool = 3;
        /// <summary>FG10-FAC-01（油污爬行者“留下油迹”）：中立的油迹（站进去的己方机器与敌人都挂上油）。与管线液洼同一套整片反应（遇到燃烧等能和油起反应、会留残留的标签就整片变成残留区域，
        /// 双方都挨——“可以用燃迹反过来利用”），但由内核按时长自己消失，不进热更层的液洼对账。阶段同液洼（<see cref="LeakPhasePuddle"/> / <see cref="LeakPhaseReacted"/>）。</summary>
        public const byte ZoneKindSlick = 4;
        /// <summary>FG10-FAC-01：配置没填（0）时喷淋池 + 油迹的共用上限（超出的不生成并计数）。</summary>
        public const int DefaultPoolZoneCapacity = 192;
        /// <summary>FG10-FAC-01（审查修复）：阵亡 / 攻城击毁事件的 Code2 里这一位 = 自毁（不是被谁击毁；SiegeKilled 的第 0 位仍是精英）。</summary>
        public const byte KillSelfDestructBit = 2;

        /// <summary>FG2-E2E-01（FG-GAP-043）：同时留在画面上的引信弹迹上限（满了挤掉最老的一条；每条只活零点几游戏秒）。</summary>
        public const int TraceCapacity = 128;

        /// <summary>FG2-E2E-01（FG-GAP-043）：弹迹在弹体渲染缓冲里的种类（B.w）；炮口装定闪光在区域渲染缓冲里的种类。</summary>
        public const float TraceInstanceKind = 10f;
        public const float MuzzleFlashKind = 26f;
        public const int DefaultDroneCapacity = 512;

        /// <summary>FG2-FW-02：一次命中的读法里“额外目标 / 连锁 / 穿透”每种最多几个（存储与耗时上限；表里的值超过按此截断）。</summary>
        public const int MaxReadingTargets = 6;
        /// <summary>一台单位同时最多挂几架无人机（蜂群舱 + 集群协议伴飞；表里的值超过按此截断）。</summary>
        public const int MaxDronesPerOwner = 8;
        /// <summary>一次命中最多排几次回波（残影 / 节拍 / 追射 / 回旋叠加时的上限）。</summary>
        public const int MaxEchoesPerHit = 4;
        /// <summary>状态位 31 保留给“区域减速”（连网 / 没有标签的减速区域）：固件标签的状态位是 fg.TbStatusTag 的 bit 列（0～30）。</summary>
        public const uint StatusBitZoneSlow = 1u << 31;

        /// <summary>FG2-FW-03：一个地点最多登记几条具名标签反应（fg.TbReaction 标签反应行；快照里反应计数按此定长）。</summary>
        public const int MaxReactions = 32;

        /// <summary>FG2-FW-03：状态标签叠层的存储上限（每位 2 比特）。</summary>
        public const int MaxStatusStacks = 3;

        /// <summary>FG2-FW-04：每个单位逐状态位到期时间的格数（位 0～30 固件标签 + 位 31 区域减速）。</summary>
        public const int StatusBitStride = 32;

        /// <summary>FG2-FW-04：读法进给的种类（<see cref="CombatData.ReadingFeedCount"/> 的下标）：区域 / 回波 / 无人机。</summary>
        public const int ReadingFeedZone = 0;
        public const int ReadingFeedEcho = 1;
        public const int ReadingFeedDrone = 2;
        /// <summary>FG2-VFX-02：尖刺外装反伤（被近身攻击时把伤害反弹给攻击者）。</summary>
        public const int ReadingFeedThorns = 3;
        public const int ReadingFeedKinds = 4;

        /// <summary>FG2-VFX-02（DEBT-FG2FW02-02）：区域 / 无人机渲染实例的种类（<see cref="CombatInstance"/> B.w）：区域 = 本值 + <see cref="CombatZoneLook"/>。</summary>
        public const float EffectKindZone = 20f;
        /// <summary>伴飞无人机（蜂群舱 / 集群协议）。</summary>
        public const float EffectKindDrone = 24f;
        /// <summary>定点哨戒桩。</summary>
        public const float EffectKindPost = 25f;
        /// <summary>区域 / 无人机没有状态色时的阵营默认色（打包 0xRRGGBB）：己方青、敌方橙红、中立灰。</summary>
        public const int EffectFriendColor = 0x40D9F2;
        /// <summary>FG2-E2E-01（FG-GAP-043）：引信弹迹 / 炮口装定闪光的颜色（琥珀白，与阵营色和状态色都区分开）。</summary>
        public const int MuzzleFlashColor = 0xFFD27A;
        public const int EffectFoeColor = 0xFA7330;
        public const int EffectNeutralColor = 0xB0B0B0;
        /// <summary>FG10-FAC-01：澄净专属行为的预警画面（喷嘴蓄压落点、枢轴扫臂前方扇区、自爆引信半径、投放落点）用的琥珀黄，与阵营色、状态色都区分开。</summary>
        public const int EffectWarnColor = 0xFFD23C;
    }

    /// <summary>
    /// FG2-FW-03（FG02 FGR-FW-040～042，反应读标签）：一条具名标签反应在内核里的规则。热更层按 fg.TbReaction（探针核实的触发配对与效果）翻译，
    /// 内核不认识反应名：只认“两个配料位、消耗位、附加位、伤害倍率、残留”。
    /// 触发：单位身上已有的标签 ∪ 这一次挂上的标签含 <see cref="Pair"/> 的两位，且这一次至少带来其中一位（同一次满足多条时只结算排在前面的一条）。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatReactionRule
    {
        /// <summary>两个配料的状态位（恰好两位）。</summary>
        public uint Pair;
        /// <summary>反应消耗的位（目标身上的与这一次带来的都去掉，含叠层）。</summary>
        public uint Consume;
        /// <summary>反应附加的位（按 <see cref="CombatData.StatusFx"/> 的效果挂上）。</summary>
        public uint Grant;
        /// <summary>这一击的伤害倍率：&gt;1 = 额外伤害 = 这一击伤害 × (倍率 − 1)；&lt;1 = 克制类，少掉的那部分算回给目标。</summary>
        public float DamageMult;
        /// <summary>残留区域挂的位（0 = 不留）、持续游戏秒、半径（米）。</summary>
        public uint ResidueBit;
        public float ResidueSeconds;
        public float ResidueRadius;
        /// <summary>稳定键（热更层按反应 ID 算的非零哈希；0 = 没有键，计数按规则下标对应）。触发次数 / 伤害按它进存档，表里 priority 顺序改了也记在同一条反应上。</summary>
        public int Key;
    }

    /// <summary>FG2-FW-03：一个状态位的效果（fg.TbStatusTag 的 effect / amount；反应附加标签、残留区域用）。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatStatusFx
    {
        /// <summary>0 = 无（反应底料），1 = 持续伤害（每秒），2 = 减速比例，3 = 易伤比例，4 = 回收，5 = 处决。</summary>
        public byte Effect;
        public byte Pad0;
        public byte Pad1;
        public byte Pad2;
        public float Amount;

        public const byte None = 0;
        public const byte Dot = 1;
        public const byte Slow = 2;
        public const byte Vuln = 3;
        public const byte Leech = 4;
        public const byte Execute = 5;
    }

    /// <summary>FG2-FW-02（FG02 FGR-FW-010，设计案 5.1）：作战组件的载体——固件在不同载体上有不同读法。
    /// 取值与热更层 GameLogic.Campaign.Signal.FirmwareCarrier 一一对应：射弹 / 格斗 / 无人机 / 力场 / 布区。</summary>
    public enum CombatCarrier : byte
    {
        /// <summary>射弹：一次命中一个目标（即时命中 / 重炮 / 弹体）。</summary>
        Projectile = 0,
        /// <summary>格斗：命中攻击者前方扇形内、触及范围内的全部敌对单位。</summary>
        Melee = 1,
        /// <summary>无人机：开火时补足伴飞无人机，无人机自己索敌、命中时带读法。</summary>
        Summon = 2,
        /// <summary>力场：以攻击者为中心的一圈脉冲，命中圈内全部敌对单位。</summary>
        Aura = 3,
        /// <summary>布区：在目标脚下铺一块区域，区域按节拍对区域内的敌对单位造成伤害并挂状态。</summary>
        Field = 4,
    }

    /// <summary>FG2-VFX-02（DEBT-FG2FW02-02）：区域画成什么样（只影响画面，不影响结算）。颜色取区域挂的第一个状态标签的图标色（表 fg.TbStatusTag），没有标签取阵营色。</summary>
    public enum CombatZoneLook : byte
    {
        /// <summary>液池：布区 / 驻留 / 拖尾——一圈圈向外的波纹。</summary>
        Pool = 0,
        /// <summary>冲击波：震荡脉冲器落在自己脚下的脉冲区——外扩的冲击环。</summary>
        Pulse = 1,
        /// <summary>减速网：连网读法——网格。</summary>
        Web = 2,
        /// <summary>反应残留（蒸汽残留等）——稀疏的斑点。</summary>
        Residue = 3,
    }

    /// <summary>FG2-FW-02：区域（布区载体、驻留 / 拖尾 / 连网读法留下的）放在哪。</summary>
    public enum CombatZonePlacement : byte
    {
        /// <summary>命中点（目标脚下）。</summary>
        HitPoint = 0,
        /// <summary>攻击者与命中点的中点（拖尾：飞行路径上）。</summary>
        Midpoint = 1,
        /// <summary>攻击者脚下。</summary>
        Attacker = 2,
    }

    /// <summary>
    /// FG2-FW-02（FGR-FW-010 读法按载体分类和字段实现）：一套武器的读法参数。热更层按“作战组件的载体 × 生效固件的读法字段”查表
    /// （fg.TbCarrierReading）累加出这些数，内核只认这些数，不认识任何固件 ID。全 0 = 没有读法（Demo 的基础武器、敌人武器）。
    /// 命中结算顺序见 <see cref="CombatLogic"/> 的 ApplyReadingHit：伤害（×增幅 / 易伤）→ 处决 → 状态 → 回收修复 → 额外目标 / 穿透 / 连锁 / 溅射 / 环扫（只结算伤害与状态，
    /// 不再触发读法，防止连锁爆炸）→ 牵引 / 击退 / 跃击 → 区域 / 连网 → 回波 → 标记跳转 → 过热爆发。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatReading
    {
        // ── 载体投送（来自作战组件表；固件的“触及 / 扇角”读法加在上面）──
        public CombatCarrier Carrier;
        public CombatZonePlacement ZonePlacement;
        /// <summary>FG2-VFX-02：布区载体的区域放在哪（震荡脉冲器 = 攻击者脚下；其余 = 命中点）。落在自己脚下的布区像力场一样要贴近才出手。</summary>
        public CombatZonePlacement FieldPlacement;
        /// <summary>FG2-VFX-02：无人机载体是定点的（哨戒桩）：无人机插在母机身前不动，只打锚点周围 <see cref="DroneLeash"/> 米内的敌人。</summary>
        public byte DroneAnchored;
        /// <summary>接近距离：&gt;0 时编队攻击命令按 min(命令射程, 本值) 接近（格斗 / 力场要贴近）。</summary>
        public float Approach;
        /// <summary>格斗：扇形半角（度）；&gt;=180 = 一整圈。</summary>
        public float Cone;
        /// <summary>格斗触及 / 力场半径 / 布区半径（米）。</summary>
        public float Area;
        /// <summary>布区：区域持续秒数；区域每秒伤害 = 武器伤害 × <see cref="FieldDpsRatio"/>。</summary>
        public float FieldSeconds;
        public float FieldDpsRatio;
        /// <summary>无人机：同时挂几架、每架存活秒数、每架伤害占武器伤害的比例、出手间隔、离母机最远多远（牵引绳）。</summary>
        public int Drones;
        public float DroneSeconds;
        public float DroneRatio;
        public float DroneCooldown;
        public float DroneLeash;

        // ── 固件读法（按字段累加）──
        /// <summary>伤害倍率（1 = 不变；0 视为 1）与出手间隔倍率（编队攻击的冷却 × 本值；0 视为 1）。</summary>
        public float DamageScale;
        public float CooldownScale;
        /// <summary>额外目标：离命中点 <see cref="ExtraRadius"/> 内最近的几个其他敌对单位，各吃 伤害 × <see cref="ExtraRatio"/>。</summary>
        public int ExtraHits;
        public float ExtraRatio;
        public float ExtraRadius;
        /// <summary>穿透：攻击者→目标方向、目标身后 <see cref="PierceRange"/> 米内的几个敌对单位。</summary>
        public int PierceHits;
        public float PierceRange;
        public float PierceRatio;
        /// <summary>连锁：从命中点依次跳向最近的未命中敌对单位（每跳 × 衰减）。</summary>
        public int ChainHits;
        public float ChainRange;
        public float ChainFalloff;
        /// <summary>溅射：命中点周围半径内的其他敌对单位吃 伤害 × 比例。</summary>
        public float BlastRadius;
        public float BlastRatio;
        /// <summary>环扫：攻击者周围半径内的其他敌对单位吃 伤害 × 比例（绕轨 / 旋风）。</summary>
        public float SweepRadius;
        public float SweepRatio;
        /// <summary>回波：命中后隔 <see cref="EchoDelay"/> 秒对同一目标再结算几次（伤害 × 比例）。</summary>
        public int EchoCount;
        public float EchoDelay;
        public float EchoRatio;
        /// <summary>牵引：把命中点（或攻击者，<see cref="PullToAttacker"/>）周围半径内的敌对单位拉近若干米。</summary>
        public float PullStrength;
        public float PullRadius;
        public byte PullToAttacker;
        public byte Pad2;
        public short Pad3;
        /// <summary>击退：把被命中的单位沿攻击方向推开若干米。</summary>
        public float Knockback;
        /// <summary>跃击：攻击者朝目标扑近若干米（不越过目标）。</summary>
        public float Lunge;
        /// <summary>穿甲：正面装甲减伤比例减去本值。</summary>
        public float ArmorPierce;
        /// <summary>处决：目标剩余血量比例 ≤ 本值时一击击毁。</summary>
        public float ExecuteBelow;
        /// <summary>回收修复：造成伤害 × 本值 修复攻击者。</summary>
        public float Lifesteal;
        /// <summary>增幅：对带任何状态标签的目标伤害 × (1 + 本值)。</summary>
        public float StatusAmp;
        /// <summary>状态：命中时挂的状态标签位、持续秒数，以及持续伤害（每秒）/ 减速比例 / 易伤比例（同类取大）。</summary>
        public uint StatusMask;
        public float StatusSeconds;
        public float StatusDps;
        public float StatusSlow;
        public float StatusVuln;
        /// <summary>区域：命中后留下的区域（半径、秒数、每秒伤害、随时间扩张速度、放在哪）；区域里的敌对单位挂 <see cref="StatusMask"/>。</summary>
        public float ZoneRadius;
        public float ZoneSeconds;
        public float ZoneDps;
        public float ZoneGrowth;
        /// <summary>区域结算节拍倍率（乱流：节拍更密；0 视为 1）。</summary>
        public float ZoneTickScale;
        /// <summary>连网：命中目标与 <see cref="WeaveRadius"/> 内最近的其他敌对单位之间拉一块减速网（区域）：持续 <see cref="WeaveSeconds"/> 秒、
        /// 减速 <see cref="WeaveSlow"/>（与状态减速取大）、没有别的区域伤害时每秒伤害 = 命中伤害 × <see cref="WeaveDpsRatio"/>（都来自 fg.TbCarrierReading weave 行 / fg.TbHomeTuning reading.weave.*）。</summary>
        public float WeaveRadius;
        public float WeaveSeconds;
        public float WeaveSlow;
        public float WeaveDpsRatio;
        /// <summary>集群协议伴飞：非无人机载体开火时补足这么多架继承读法的伴飞无人机（无人机载体则直接加进 <see cref="Drones"/>）。</summary>
        public int EscortDrones;
        /// <summary>过热爆发：攻击者积热 ≥ 过热阈值 × <see cref="HeatBurstAt"/> 时，这一发在命中点再爆一圈。</summary>
        public float HeatBurstAt;
        public float HeatBurstRadius;
        public float HeatBurstRatio;
        /// <summary>通用标记跳转（不需要标记器；只在目标已被标记时跳）：范围、衰减、最多几个。</summary>
        public float JumpRange;
        public float JumpFalloff;
        public int JumpMax;

        // ── FG2-VFX-02：功能组件“尖刺外装”（格斗·反伤，被动）──
        /// <summary>反伤：这台单位被近身即时攻击（攻击者在触及 <see cref="ThornsReach"/> + 双方半径以内，不含弹体 / 重炮 / 区域 / 无人机）时，
        /// 攻击者吃 <see cref="ThornsFlat"/> + 这一击伤害 × <see cref="Thorns"/>。反伤本身不再触发反伤。</summary>
        public float Thorns;
        public float ThornsFlat;
        public float ThornsReach;

        /// <summary>有没有任何固件读法（投送参数不算）。</summary>
        public bool HasFirmwareReading =>
            (DamageScale != 0f && DamageScale != 1f) || (CooldownScale != 0f && CooldownScale != 1f) || ExtraHits > 0 || PierceHits > 0 || ChainHits > 0
            || BlastRadius > 0f || SweepRadius > 0f || EchoCount > 0 || PullStrength > 0f || Knockback > 0f || Lunge > 0f || ArmorPierce > 0f
            || ExecuteBelow > 0f || Lifesteal > 0f || StatusAmp > 0f || StatusMask != 0u || ZoneSeconds > 0f || WeaveRadius > 0f || EscortDrones > 0
            || HeatBurstAt > 0f || JumpMax > 0;
    }

    /// <summary>FG2-FW-02：一块区域（布区 / 驻留 / 拖尾 / 连网）。按节拍对区域内的敌对阵营单位造成伤害并挂状态；到期消失。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatZone
    {
        public double2 Pos;
        public float Radius;
        public float Growth;
        /// <summary>生成时刻（画面上外圈的剩余时间比例 = (到期 - 现在) / (到期 - 生成)）。</summary>
        public double Born;
        public double Until;
        public double NextTick;
        public float TickInterval;
        public float Dps;
        public uint StatusMask;
        public float StatusSeconds;
        public float StatusDps;
        public float StatusSlow;
        public float StatusVuln;
        /// <summary>留下区域的单位 ID（伤害归属；单位阵亡后区域照常到期）。</summary>
        public int Owner;
        public CombatFaction Faction;
        /// <summary>FG2-VFX-02：画成什么样（只影响画面）。</summary>
        public CombatZoneLook Look;
        /// <summary>FG6-DEF-02 复审修复：0 = 读法区域（武器 / 反应留下的），1 = 场地（陷阱发射器铺的）。两种各有自己的上限，场地铺满不挤掉读法区域。随快照（格式 10）。
        /// FG6-LOG-10：2 = 管线泄漏液洼（<see cref="CombatConst.ZoneKindLeak"/>；<see cref="Owner"/> 存液洼编号的相反数，不归属任何单位）。</summary>
        public byte Kind;
        /// <summary>FG6-LOG-10：液洼区域的阶段（<see cref="CombatConst.LeakPhasePuddle"/> / <see cref="CombatConst.LeakPhaseReacted"/>）；其余区域恒为 0。随快照（格式 12）。
        /// FG10-FAC-01：油迹同液洼；喷淋池 1 = 已被冲洗（很快消失，只提示一次）。</summary>
        public byte Phase;
        /// <summary>FG10-FAC-01（液氮池）：每个节拍给站在里面的单位加多少受寒（0 = 没有；受寒满 1 冻结，见 <see cref="CombatClarityUnit.Chill"/>）。随快照（格式 15）。</summary>
        public float Chill;
    }

    /// <summary>FG2-FW-02：一次待结算的回波（残影 / 节拍 / 追射 / 回旋）：到点后对同一目标再结算一次伤害与状态。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatEcho
    {
        public double At;
        public int Target;
        public int Owner;
        public float Damage;
        public uint StatusMask;
        public float StatusSeconds;
        public float StatusDps;
        public float StatusSlow;
        public float StatusVuln;
    }

    /// <summary>FG2-FW-02：一架无人机（蜂群舱 / 集群协议伴飞）：在母机牵引绳范围内追最近的敌对单位，按间隔命中，命中带母机武器的读法（<see cref="Weapon"/>）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatDrone
    {
        public double2 Pos;
        public double2 Prev;
        public double Until;
        public double NextHit;
        public int Owner;
        public int Weapon;
        public float Damage;
        public float Leash;
        public float Cooldown;
        public CombatFaction Faction;
        /// <summary>FG2-VFX-02：定点无人机（哨戒桩）：不移动，只打 <see cref="Anchor"/> 周围 <see cref="Leash"/> 米内的敌人。</summary>
        public byte Anchored;
        public short Pad1;
        public double2 Anchor;
        /// <summary>EMG1-KRN-01（ADR ④，格式 16）：稳定 Id（每地点单调，EmgScalars.NextDroneId；无人机每步压实，跨步引用用它）。</summary>
        public int Id;
    }

    /// <summary>
    /// FG6-DEF-02（FG06 FGR-DEF-012 护盾发生器）：一座圆形护盾。展开时（<see cref="Active"/> = 1）从圈外飞进圈内的敌对阵营弹体被吸收（扣护盾值、弹体作废）；
    /// 起点已在圈内的弹体（圈内开火）不挡。护盾值归零 → 内核当场收起（Active = 0、耗尽次数 +1），过载 / 重启的计时与状态机在热更层（按 fg.TbShieldState）。
    /// <see cref="RegenPerSec"/> 每步回复（热更层按状态写，没电时写 0）。随快照进存档（格式 10）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatShield
    {
        /// <summary>热更层的护盾序号（&gt; 0，不复用）。</summary>
        public int ExtKey;
        public CombatFaction Faction;
        public byte Active;
        public short Pad;
        public double2 Pos;
        public float Radius;
        public float Hp;
        public float MaxHp;
        public float RegenPerSec;
        /// <summary>累计吸收的伤害 / 弹体数 / 耗尽次数（面板读数、耗电按窗口差分）。</summary>
        public double Absorbed;
        public int Hits;
        public int Depletions;
    }

    /// <summary>阵营。己方（玩家）与敌方互为目标；中立单位（训练靶）只接受显式指向它的攻击。</summary>
    public enum CombatFaction : byte
    {
        Player = 0,
        Hostile = 1,
        Neutral = 2,
    }

    /// <summary>单位种类（只用于统计与画面；行为由 <see cref="CombatBehavior"/> 决定）。</summary>
    public enum CombatUnitKind : byte
    {
        Machine = 0,
        Enemy = 1,
        Turret = 2,
        /// <summary>不会移动、不会主动开火的目标（首领的供能节点、训练靶）。</summary>
        Structure = 3,
    }

    /// <summary>
    /// 单位每步做什么。Demo 的六种敌人行为逐条翻译自 FracturedCityEnemyAi / FoundryOutpostEnemyAi / FoundryOutpostCoreBoss（数值由热更层按 Demo 常量传入）；
    /// 突袭者与炮塔是正式版（FG06）的原型行为。
    /// </summary>
    public enum CombatBehavior : byte
    {
        /// <summary>不动、不开火（供能节点、训练靶）。</summary>
        None = 0,
        /// <summary>己方机器：只执行玩家显式下达的命令（移动 / 攻击 / 守备 / 撤退 / 工作赶路 / 直控），不自动找目标（FGR-BASE-020）。</summary>
        Commanded = 1,
        /// <summary>驻守开火：冷却到点时打射程内最近的可见目标；没有目标不重置冷却（护甲机、干扰支援、主核心、炮塔）。</summary>
        HoldFire = 2,
        /// <summary>瞄准线两段式：发现目标先瞄准 <see cref="CombatWeapon.AimSeconds"/>，到点重新取射程内最近目标开火（步进炮）。</summary>
        Telegraph = 3,
        /// <summary>侦察：受威胁后撤（不超出出生点牵引半径）、无威胁时出生点附近摆动巡逻；按周期标记视线内最近的机器（静默侦察机）。</summary>
        Scout = 4,
        /// <summary>干扰：驻守，清除半径内己方机器身上的标记与友军身上的标记，外加驻守开火（静默干扰机）。</summary>
        Jammer = 5,
        /// <summary>维修：受威胁后撤；否则走向血量百分比最低的友军，进入范围按周期治疗（维修机）。</summary>
        Repair = 6,
        /// <summary>突袭者（FG06 原型）：扑向感知范围内最近的敌对目标，进入射程后开火；没有目标时朝目标点（出生时给定）前进。</summary>
        Raider = 7,
        /// <summary>家园训练靶自动交战（Demo ER4-PRIM-05）：空闲、未被接入的机器在射程内按间隔请求一次对训练靶的攻击（结算在热更层）。</summary>
        AutoEngage = 8,
        /// <summary>FG9-FAC-01（回声猎手，FGR-FAC-008“静默潜伏”）：在出生点潜伏伏击；感知范围内出现己方机器（优先装有信号中继的）就现形（预警）、追上去，
        /// 进入射程后按瞄准线两段式出手；目标丢失后回到出生点重新潜伏（不超出牵引半径追击）。</summary>
        Stalker = 9,
        /// <summary>FG9-FAC-02（对空高炮 / 空爆炮台，FGR-FAC-008“齐射弹幕”）：固定；装填 → 装填完成灯（预警）→ 一轮多发齐射。参数 Salvo*；
        /// SalvoMode 1 = 只打空中目标（己方无人机与飞行中的弹体），2 = 在目标处空爆（半径内地面单位受伤、无人机被击落）。</summary>
        Salvo = 10,
        /// <summary>FG9-FAC-02（布雷车）：在牵引中心附近巡逻、受威胁后撤；铺雷由行为参数 Mine* 决定（任何行为都可以带，攻城单位也一样）。</summary>
        MineLayer = 11,
        /// <summary>FG9-FAC-02：一颗子母雷（布雷车在内核里铺下的结构单位）：己方单位进入半径 → 引信预警 → 爆炸；被任何伤害打到或被挪动 → 提前引爆；存活时长 MineLife。</summary>
        Mine = 12,
        /// <summary>FG9-FAC-02（攻城重甲，FGR-FAC-008“正面推进”）：转向受限、只沿正面推进；目标在正前方时蓄力 → 冲锋（撞到的己方单位受伤并被击退）→ 冷却；贴身时用武器攻击。</summary>
        Charger = 13,
        /// <summary>FG9-FAC-02（装填机）：受威胁后撤、否则回到牵引中心；装填光环由行为参数 Reload* 决定（任何行为都可以带）。</summary>
        Loader = 14,
        /// <summary>FG10-FAC-01（沉淀巨罐被击毁后的“会爬行的腐蚀液”）：内核在巨罐阵亡处生成的匿名小单位——慢速爬向感知范围内最近的己方地面单位，接触时按节拍腐蚀
        /// （多滩之间不叠加，见 <see cref="CombatClarityUnit.PuddleTick"/>），存活 SplitLife 秒后蒸发。参数取母体的行为参数 Split*。</summary>
        Puddle = 15,
    }

    /// <summary>单位状态位。</summary>
    [Flags]
    public enum CombatUnitFlags : uint
    {
        None = 0,
        Alive = 1 << 0,
        /// <summary>可以被选为攻击目标 / 被弹体命中。</summary>
        Targetable = 1 << 1,
        /// <summary>血量真相在热更层（机器记录、首领状态机、训练靶）：内核不扣血，发出伤害 / 治疗请求事件，由热更层结算后回写镜像血量。</summary>
        ExternalHealth = 1 << 2,
        /// <summary>内核扣血的单位要逐次报告（Demo 的具名敌人：受伤 / 阵亡 / 被治疗都同步到记录）。突袭规模的单位不报告，只计汇总。</summary>
        Report = 1 << 3,
        /// <summary>武器可以开火（主核心只在阶段一 / 阶段二打开）。</summary>
        WeaponEnabled = 1 << 4,
        /// <summary>正在被玩家接入（直控）：不执行编队命令，移动来自直控输入。</summary>
        Possessed = 1 << 5,
        /// <summary>开火前要求视线不被障碍遮挡（Demo 敌人“标记不魔法穿墙”）。</summary>
        NeedsLos = 1 << 6,
        /// <summary>武器过热（迟滞：降到 <see cref="CombatWeapon.RecoverBelow"/> 以下才恢复）。</summary>
        Overheated = 1 << 7,
        /// <summary>装了散热鳍：散热加 <see cref="CombatWeapon.HeatSinkBonus"/>。</summary>
        HeatSink = 1 << 8,
        /// <summary>敌方“耐热”反制：熔穿过载的额外穿甲对它无效。</summary>
        HeatResistant = 1 << 9,
        /// <summary>阵亡后自动从内核移除（突袭规模的匿名单位）。具名单位阵亡后留作墓碑，热更层决定何时移除。</summary>
        RemoveOnDeath = 1 << 10,
        /// <summary>用实例化渲染画出来（没有 GameObject 表现的单位：突袭者、炮塔原型）。</summary>
        Instanced = 1 << 11,
        /// <summary>开火不发“敌人开火”提示（主核心：Demo 直接扣血，不出敌人开火音）。</summary>
        SilentFire = 1 << 12,
        /// <summary>这一步有待执行的“暂停中下达”的命令（UI 显示“已排队”）；第一次执行时清掉。</summary>
        PendingCommand = 1 << 13,
        /// <summary>当前不能被击伤（首领供能节点只在护盾阶段可伤、主核心只在阶段一 / 二可伤；热更层在阶段变化时改）。
        /// 开火照常（冷却、积热照算），伤害不落地，开火结果为 <see cref="CombatFireResult.Invulnerable"/>。</summary>
        Invulnerable = 1 << 14,
        /// <summary>暂不自动交战（家园里仍占用工厂出口的机器）：自动交战的冷却照常走，但不发交战请求、不消耗冷却
        /// （Demo TickAutoEngage 对厂内机器直接跳过）；热更层在机器进出工厂时写。</summary>
        EngageHold = 1 << 15,
        /// <summary>FG1-SIG-03（FGR-SIG-033）：这台单位当前武器的具名反应来自信号带进来的核心固件（热更层按接入结算写，冷却值 &gt; 0 才写）。
        /// 反应一发动，内核当场置 <see cref="ReactionSpent"/> 把它压住——压住不依赖玩法事件什么时候被热更层处理（大规模战斗里事件可能顺延几步）。</summary>
        ReactionGated = 1 << 16,
        /// <summary>FG1-SIG-03：门控反应已经发动、正等热更层按信号上的冷却重新下发武器参数；期间反应不发动（熔穿过载不额外积热、不穿甲；标记跳转不跳）。
        /// 热更层下发不带门控反应的武器参数时（冷却中压住的行、离开后的本地配置）清掉。</summary>
        ReactionSpent = 1 << 17,
        /// <summary>FG1-SIG-06（FGR-SIG-061）：这台单位接入口里插着信号裸跑的未破解常规固件，且信号上的裸跑计次间隔已过——下一次开火算一次“发动”：
        /// 内核当场置 <see cref="RawSpent"/> 并发不丢的玩法事件 <see cref="CombatEventKind.RawFirmwareFired"/>（暴露按它结算）。热更层按接入结算写。</summary>
        RawGated = 1 << 18,
        /// <summary>FG1-SIG-06：裸跑已计过一次、正等热更层按计次间隔重新下发（期间开火照常、固件照常生效，只是不再重复计暴露）。
        /// 热更层下发不带 <see cref="RawGated"/> 的参数时清掉。</summary>
        RawSpent = 1 << 19,
        /// <summary>FG1-SIG-07（FGR-SIG-053）：这台己方机器在与归还核心连通的信号覆盖之外（收不到远程命令、不能接入）。
        /// 只由 <see cref="CombatKernel.EvaluateCoverage"/> 按热更层下发的覆盖源写（世界模拟步里按游戏时间定期评估，与是否被观察无关），随快照进存档。</summary>
        OutOfCoverage = 1 << 20,
        /// <summary>FG6-DEF-01（FGR-DEF-003“精英优先”）：精英 / 首领级单位（热更层按敌人表的等级在生成时写）。</summary>
        Elite = 1 << 21,
        /// <summary>FG6-DEF-01（FGR-DEF-003“优先攻击正在破坏建筑的敌人”）：这个敌方单位最近一次出手打的是己方建筑（炮塔 / 结构单位）。
        /// 由内核在敌方单位出手时写（打建筑置位、打别的清掉），随快照进存档。</summary>
        SiegeAttack = 1 << 22,
        /// <summary>FG6-DEF-01（必须同时交付“击杀数”）：这个单位打死敌对单位时发不丢的玩法事件 <see cref="CombatEventKind.TurretKill"/>（只有炮塔带）。</summary>
        CountKills = 1 << 23,
        /// <summary>FG6-DEF-01：血量在内核、但阵亡要交给热更层结算的单位（炮塔：阵亡 = 建筑被摧毁）。只在阵亡时发 <see cref="CombatEventKind.Killed"/>，受伤不逐次报告。</summary>
        ReportDeath = 1 << 24,
        /// <summary>FG6-DEF-05：耐久不会被打到 0 以下（停在 <see cref="CombatSiegeConst.HealthFloor"/>，不阵亡）——归还核心：被打空的后果（战役失败）在 FG6-DEF-08，
        /// 攻城期间敌人照常打它、耐久记成极小值（与 BuildingOps.CoreFloorHealth 同一口径）。</summary>
        HealthFloor = 1 << 25,
        /// <summary>FG9-FAC-01（FGR-FAC-008“静默潜伏”）：潜伏中——不能被远程锁定（索敌 / 编队攻击 / 直控点击都选不中），小地图与头顶标记隐藏；
        /// 范围伤害（区域 / 力场 / 溅射……）照常命中并立即现形。由内核按行为参数 <see cref="CombatBehaviorProfile.LurkSeconds"/> 写（静止够久进入、移动 / 开火 / 受伤 / 被侦测退出）。</summary>
        Stealthed = 1 << 26,
        /// <summary>FG9-FAC-01：偏折场可以运转（带偏折场参数的单位生成时置位；首领由状态机按阶段开关——寂听主脑只在阶段二展开全场偏折场）。</summary>
        DeflectArmed = 1 << 27,
        /// <summary>FG9-FAC-01（回声猎手“优先攻击装有信号中继的机器”；潜伏单位被信号中继覆盖揭示）：己方机器的结构槽是信号中继模块（热更层按装配写）。</summary>
        SignalRelay = 1 << 28,
        /// <summary>FG9-FAC-01（DEBT-FG6DEF05-04 干扰部分）：攻城中的干扰机自带信号干扰场（半径 = 行为参数 EffectRange）——家园里场内的机器 / 炮塔接入被拒、被接入的断链。</summary>
        JamEmitter = 1 << 29,
        /// <summary>FG9-FAC-02（子母雷）：不能被单体锁定（索敌 / 编队攻击 / 直控点击都选不中，开火结果 <see cref="CombatFireResult.AreaOnly"/>）；范围伤害与牵引照常作用（会提前引爆）。</summary>
        AreaOnly = 1u << 30,
    }

    /// <summary>
    /// FG10-FAC-01（FG09 FGR-FAC-008 澄净专属语法；第 6 章五类单位与精英）：每个单位的澄净状态（随快照进存档，格式 15）。全 0 = 没有澄净行为、没有受寒。
    /// 喷淋布区：<see cref="Spray"/> 0 = 冷却（<see cref="SprayT"/> 还剩几秒）、1 = 喷嘴蓄压预警（定在 <see cref="Anchor"/>，方向 <see cref="SprayDir"/>，到点喷出）。
    /// 枢轴喷灌：<see cref="Pivot"/> 0 = 待机、1 = 蓄压预警（<see cref="PivotT"/>）、2 = 扫臂（臂角 <see cref="PivotAngle"/> 度，PivotT = 周围没有目标的累计秒数）。
    /// 自爆：<see cref="Fuse"/> 0 = 爬行、1 = 引信燃烧（<see cref="FuseT"/>，定在 Anchor）、2 = 已自爆（阵亡时不再按“被火击毁”爆燃）；油迹 <see cref="TrailT"/> / <see cref="TrailLast"/>。
    /// 飞行：<see cref="Air"/> = 1（灭活无人机）——不受地形与地面区域影响，格斗 / 力场 / 布区打不到（FG18 FGR-AIR-010）。投放：<see cref="Pollute"/> 1 = 投放预警（<see cref="PolluteT"/>，目标 <see cref="PolluteAt"/>）。
    /// 受寒（任何单位都可能挂）：<see cref="Chill"/> 0～1，越高越慢；满 1 冻结 <see cref="FrozenT"/> 秒（不能移动、不能开火），解冻后 <see cref="ThawGuardT"/> 秒内不再受寒。
    /// 抗性：<see cref="AcidResist"/>（酸沼 / 酸液池伤害 × (1 − 本值)）、<see cref="ImmuneMask"/>（挂不上的状态位：澄净单位不挂酸蚀、带绝缘涂层的单位不挂电击）。
    /// 分裂液：<see cref="Life"/> 剩余存活秒数；<see cref="PuddleTick"/> = 这个单位最近一次被分裂液腐蚀的节拍序号（同一节拍多滩分裂液只算一次）。
    /// 审查修复：<see cref="SprayAt"/> = 喷嘴蓄压那一刻锁定的落点（目标所在处；喷淋池落在“到它的距离”与射程取小处，不是固定的射程中段）；
    /// <see cref="PivotPrev"/> = 上一个区域节拍时的臂角（节拍结算“这段时间扫过的整段弧”，不只是节拍那一刻的臂角）。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatClarityUnit
    {
        public byte Spray;
        public byte Pivot;
        public byte Fuse;
        public byte Air;
        public byte Pollute;
        public byte Pad0;
        public short Pad1;
        public float SprayT;
        public float2 SprayDir;
        public double2 Anchor;
        public float PivotAngle;
        public float PivotT;
        public float FuseT;
        public float TrailT;
        public double2 TrailLast;
        public float PolluteT;
        public double2 PolluteAt;
        public float Chill;
        public float FrozenT;
        public float ThawGuardT;
        public float AcidResist;
        public uint ImmuneMask;
        public float Life;
        public long PuddleTick;
        public double2 SprayAt;
        public float PivotPrev;
    }

    /// <summary>
    /// FG10-FAC-01：一条待结算的死亡效果（单位阵亡那一刻记下，下一个澄净阶段统一生成——阵亡可能发生在弹体 / 区域的遍历中途，不在遍历里追加单位或区域）。
    /// <see cref="Kind"/> 1 = 自爆单位阵亡（<see cref="Mult"/> &gt; 0：被火击毁 → 中立爆燃，伤害 × Mult；= 0：只留一摊油迹）；2 = 分裂（生成 Profile 的 SplitCount 滩分裂液）。随快照进存档（格式 15）。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatClarityPending
    {
        public byte Kind;
        public byte Pad0;
        public short Pad1;
        public int Profile;
        public int Source;
        public float Mult;
        public double2 Pos;
    }

    /// <summary>
    /// FG9-FAC-02（FG09 FGR-FAC-008 铸造专属语法）：每个单位的铸造状态（随快照进存档，格式 14）。全 0 = 没有铸造行为。
    /// 齐射：<see cref="Salvo"/> 0 = 装填（<see cref="SalvoT"/> 还剩几秒）、1 = 装填完成灯预警、2 = 齐射中（还剩 <see cref="SalvoLeft"/> 发，SalvoT = 下一发）；<see cref="SalvoTarget"/> 预警时锁定的目标。
    /// 架炮驻锄：<see cref="Deploy"/> 0 = 未架设、1 = 架设中（<see cref="DeployT"/>）、2 = 已架设；<see cref="AnchorPos"/> = 开始架设的位置（被挪开就回到未架设）。
    /// 冲锋：<see cref="Charge"/> 0 = 推进、1 = 蓄力、2 = 冲锋中（<see cref="ChargeT"/>、方向 <see cref="ChargeDir"/>、第几次 <see cref="ChargeSeq"/>）；被撞者记 <see cref="RamBy"/> / <see cref="RamSeq"/>（一次冲锋只撞一次）。
    /// 对空形态：<see cref="AirMode"/> = 1 时（热更层按首领阶段写）这个单位按齐射参数只打空中目标。
    /// 地雷：<see cref="MineState"/> 0 = 待触发、1 = 引信燃烧（<see cref="MineT"/>）；<see cref="MineLife"/> 剩余存活秒数；<see cref="MineOwner"/> 铺雷者 ID。
    /// 布雷 / 装填：<see cref="LayT"/> 下一颗雷的倒计时；<see cref="BoostStep"/> 最近一次被装填机加速的步序号（同一步不叠加）。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatFoundryUnit
    {
        public byte Salvo;
        public byte Deploy;
        public byte Charge;
        public byte AirMode;
        public byte MineState;
        public byte Pad0;
        public short Pad1;
        public float SalvoT;
        public int SalvoLeft;
        public int SalvoTarget;
        public float DeployT;
        public double2 AnchorPos;
        public float ChargeT;
        public float2 ChargeDir;
        public int ChargeSeq;
        public int RamBy;
        public int RamSeq;
        public float MineT;
        public float MineLife;
        public int MineOwner;
        public float LayT;
        public long BoostStep;
    }

    /// <summary>
    /// FG9-FAC-01（FG09 FGR-FAC-008 静默专属语法）：每个单位的静默状态（随快照进存档，格式 13）。全 0 = 没有静默行为。
    /// 潜伏：<see cref="Still"/> 累计静止秒数，够 <see cref="CombatBehaviorProfile.LurkSeconds"/> 进入潜伏；现形后 <see cref="Reveal"/> 倒计时（展桅预警，期间不开火）。
    /// 偏折场：<see cref="Deflect"/> 0 = 关闭（间隙）、1 = 预警、2 = 展开；<see cref="DeflectT"/> = 当前阶段还剩几秒。
    /// 断链束：<see cref="SeverTarget"/> 正在照射的己方机器（单位 ID），<see cref="SeverT"/> 累计照射秒数（负数 = 断链后的冷却）。
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatSilentUnit
    {
        public byte Deflect;
        public byte Pad0;
        public short Pad1;
        public float Still;
        public float Reveal;
        public float DeflectT;
        public int SeverTarget;
        public float SeverT;
    }

    /// <summary>己方机器的命令。与 Demo RegionSquadCommandSystem / HomeValleyMachineMarker 的语义逐条一致。</summary>
    public enum CombatCommandKind : byte
    {
        None = 0,
        Move = 1,
        Attack = 2,
        Guard = 3,
        Retreat = 4,
        /// <summary>工作赶路（Demo 的 HomeValleyMachineMarker.CommandMoveTo）：直线走到目标，进入到达半径即对齐到目标点，发“到达”事件。</summary>
        WorkMove = 5,
    }

    /// <summary>FG0-ARCH-06：单位的寻路状态（只在 <see cref="CombatConfig.NavEnabled"/> 的地点使用）。</summary>
    public enum CombatNavState : byte
    {
        None = 0,
        /// <summary>需要一条路线：下一次推进移动时发出请求。</summary>
        NeedRoute = 1,
        /// <summary>请求已发出，等寻路内核按固定延迟交回结果；有旧路线时继续沿它移动，否则原地等待。</summary>
        Awaiting = 2,
        /// <summary>沿路线走。</summary>
        Following = 3,
        /// <summary>寻路失败：命令以“无法到达”结束（原因随事件交给热更层显示）。</summary>
        Failed = 4,
    }

    /// <summary>FG0-ARCH-06：内核交给寻路内核的一条请求（热更层每步一次性转交，O(1) 次调用）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatNavRequest
    {
        public int UnitId;
        public int Serial;
        public int2 Start;
        public int2 Goal;
        public byte Class;
        public byte Flags;
        public short Pad;
    }

    /// <summary>命令结束原因（热更层据此写编队事件文本与反馈）。</summary>
    public enum CombatEndReason : byte
    {
        None = 0,
        Arrived = 1,
        TargetLost = 2,
        TargetDestroyed = 3,
        Stuck = 4,
        Cancelled = 5,
        /// <summary>执行者阵亡或离场。</summary>
        Dead = 6,
        /// <summary>FG0-ARCH-06：寻路失败（被完全阻断 / 目标处无处可站 / 超出搜索范围……）。事件的 Value = <see cref="BinGames.Sim.Nav.NavFailReason"/>。</summary>
        Unreachable = 7,
    }

    /// <summary>
    /// 内核事件。前半段是**玩法事件**（热更层必须处理：永不丢弃，按步限量排空、跨步保留、进存档）；
    /// 后半段是**提示事件**（声音 / 字幕 / 特效，只影响反馈）：每步有上限，超出的丢弃并计数（B17 同类音效有数量上限）。
    /// </summary>
    public enum CombatEventKind : byte
    {
        None = 0,
        // ── 玩法事件 ──
        /// <summary>内核扣血的报告单位阵亡。Unit=阵亡者，Other=击杀者。</summary>
        Killed = 1,
        /// <summary>内核扣血的报告单位受伤。Value=伤害，Value2=剩余血量。</summary>
        Damaged = 2,
        /// <summary>内核扣血的报告单位被治疗。Value=治疗量，Value2=治疗后血量。</summary>
        Healed = 3,
        /// <summary>对血量在热更层的单位的伤害请求。Unit=目标，Other=攻击者，Value=伤害（护甲 / 穿甲 / 侧后加成已算好）。</summary>
        DamageRequest = 4,
        /// <summary>对血量在热更层的单位的治疗请求。Unit=目标，Other=治疗者，Value=治疗量。</summary>
        HealRequest = 5,
        /// <summary>命令结束。Unit=执行者，Code=<see cref="CombatEndReason"/>，Code2=命令种类，Other=攻击目标（攻击命令）。</summary>
        CommandEnded = 6,
        /// <summary>路径受阻第 N 次（N = Value，还没放弃）。</summary>
        CommandStuckStrike = 7,
        /// <summary>编队攻击命令的一次开火尝试的结果。Unit=攻击者，Other=目标，Code=<see cref="CombatFireResult"/>。</summary>
        AttackOutcome = 8,
        /// <summary>侦察单位标记了一台己方机器。Unit=被标记的机器，Other=侦察单位，Value=持续秒数。</summary>
        MachineMarked = 9,
        /// <summary>侦察单位的标记周期到了但视线内没有目标（Demo 写日志）。Unit=侦察单位。</summary>
        MarkMissed = 10,
        /// <summary>己方单位第一次进入兴趣点范围。Unit=单位，Other=兴趣点序号。</summary>
        PoiReached = 11,
        /// <summary>工作赶路到达。Unit=机器。</summary>
        WorkArrived = 12,
        /// <summary>训练靶自动交战：请求热更层结算一次攻击。Unit=攻击者，Other=训练靶。</summary>
        EngageRequest = 13,
        /// <summary>干扰单位清掉了一台机器身上的标记。Unit=机器，Other=干扰单位。</summary>
        MarkCleared = 14,
        /// <summary>FG0-ARCH-06：工作赶路寻路失败（目标无法到达）。Unit=机器，Code=<see cref="BinGames.Sim.Nav.NavFailReason"/>，Pos=机器位置。</summary>
        WorkBlocked = 15,
        /// <summary>FG1-SIG-03（FGR-SIG-033）：具名反应真正发动了一次（熔穿过载这一发 / 标记跳转真的跳出去）。Unit=攻击者，Other=主目标，
        /// Code=<see cref="CombatReaction"/>，Code2=1 表示发动时这台单位带 <see cref="CombatUnitFlags.ReactionGated"/>（信号带进来的核心固件）。
        /// 核心固件冷却、暴露都按它结算，所以走玩法事件（不丢）；同名的提示事件（<see cref="MeltOverload"/> / <see cref="MarkJump"/>）只管反馈，超出上限可以丢。</summary>
        ReactionFired = 16,
        /// <summary>FG1-SIG-06（FGR-SIG-061）：带 <see cref="CombatUnitFlags.RawGated"/> 的单位开火了——信号裸跑的未破解固件“发动”一次（暴露按它结算，走玩法事件，不丢）。
        /// Unit=攻击者，Other=目标。</summary>
        RawFirmwareFired = 17,
        /// <summary>FG6-DEF-01：带 <see cref="CombatUnitFlags.CountKills"/> 的单位（炮塔）打死了一个敌对单位（击杀数按它记，走玩法事件，不丢）。
        /// Unit=击杀者，Other=阵亡者，Code=1 表示阵亡者带 <see cref="CombatUnitFlags.Elite"/>。</summary>
        TurretKill = 18,
        /// <summary>FG6-DEF-05（FGR-DEF-032）：撤退中的攻城单位走到集结点、离开家园（内核当场移除，不算阵亡）。Unit=单位，Other=所属队伍键（<see cref="CombatSiegeUnit.Group"/>）。</summary>
        SiegeExited = 19,
        /// <summary>FG6-DEF-08（FGR-DEF-050 / 051）：攻城 / 拦截单位（<see cref="CombatSiegeUnit.Role"/> ≠ 0 的敌方单位）被击毁——结算、残骸与贡献按它记（走玩法事件，不丢）。
        /// Unit=阵亡者（随后按 <see cref="CombatUnitFlags.RemoveOnDeath"/> 移除），Other=击杀者（0 = 来源不明），Code=职能，Code2=1 表示精英，
        /// Value=所属队伍键（<see cref="CombatSiegeUnit.Group"/>），Value2=阵亡者外部键的相反数（热更层从中解出敌人种类），Pos=阵亡位置。</summary>
        SiegeKilled = 20,
        /// <summary>FG9-FAC-01（寂听者“断链束”）：断链束持续照射被接入的机器满 <see cref="CombatBehaviorProfile.SeverSeconds"/> 秒——热更层强制断链（走玩法事件，不丢）。
        /// Unit=被照射的机器，Other=照射者。</summary>
        LinkSevered = 21,
        /// <summary>FG10-FAC-01（灭活无人机）：投放完成——热更层让目标点（净化塔）周围再次污染（走玩法事件，不丢）。Unit=无人机（投放后自毁），Pos=目标点，Value=目标点半径。</summary>
        Repolluted = 22,

        // ── 提示事件（有上限，可丢弃）──
        /// <summary>己方普通武器开火（Unit=攻击者，Other=目标）。</summary>
        Fired = 32,
        /// <summary>重炮开始 1 秒瞄准线（蓄力）。</summary>
        CannonCharge = 33,
        /// <summary>重炮开火。</summary>
        CannonFire = 34,
        /// <summary>武器过热停火。</summary>
        Overheat = 35,
        /// <summary>熔穿过载生效（这一发）。</summary>
        MeltOverload = 36,
        /// <summary>命中正面装甲（减伤）。</summary>
        ArmorHit = 37,
        /// <summary>标记跳转真正跳出去了（Value=跳了几个）。</summary>
        MarkJump = 38,
        /// <summary>敌方开火（Unit=敌人，Other=目标）。</summary>
        EnemyFired = 39,
        /// <summary>弹体命中（Pos=命中点）。</summary>
        ProjectileHit = 40,
        /// <summary>步进炮开始瞄准线 / 瞄准线结束落空（Code：0 开始，1 落空，2 命中）。</summary>
        Telegraph = 41,
        /// <summary>FG2-FW-03：具名标签反应触发（Unit=挂上标签的单位，Other=被反应的单位 ID，Code=反应规则下标，Value=反应额外伤害（克制类为负），Pos=被反应的单位）。
        /// 反馈用，超出上限可以丢；可靠的计数在 <see cref="CombatData.ReactionCount"/>（进存档）。</summary>
        TagReaction = 42,
        /// <summary>FG6-DEF-02：护盾吸收了一发弹体（Other=护盾序号，Value=吸收的伤害，Pos=入圈点，Code=1 表示这一发把护盾打空）。</summary>
        ShieldAbsorb = 43,
        /// <summary>FG9-FAC-01：偏折场开始预警（Unit=展开者，Value=半径，Value2 不用，Pos=圆心，Code=1 预警 / 2 展开 / 0 收起）。</summary>
        DeflectPhase = 44,
        /// <summary>FG9-FAC-01：一发射弹被偏折场反弹（Unit=展开者，Other=被反弹伤到的开火者 ID（弹体反弹时为 0），Value=反弹伤害，Pos=反弹点）。</summary>
        Reflected = 45,
        /// <summary>FG9-FAC-01：潜伏状态变化（Unit=单位，Code=1 进入潜伏 / 0 现形）。</summary>
        StealthChanged = 46,
        /// <summary>FG9-FAC-02：铸造专属语法的提示（Unit=行为者，Other=目标 / 被撞者，Value=预警秒数或半径，Pos=位置）。
        /// Code：1 齐射装填完成灯（预警，Code2 = 1 对空 / 2 空爆）、2 对空拦截、3 空爆、4 子母雷引信触发、5 子母雷爆炸、6 冲锋蓄力、7 步进炮开始架设、8 铺下一颗雷、9 冲锋撞击。</summary>
        FoundryCue = 47,
        /// <summary>FG10-FAC-01：澄净专属语法的提示（Unit=行为者 / 受影响者，Other=目标，Value=预警秒数或半径，Pos=位置）。
        /// Code：1 喷嘴蓄压（预警，Code2 = 1 酸液 / 2 液氮）、2 喷出、3 枢轴喷灌蓄压（预警）、4 自爆引信（预警）、5 爆炸（Code2 = 1 被火击毁的爆燃）、6 油迹被点燃、
        /// 7 冻结（Unit = 被冻结者）、8 分裂、9 投放预警（灭活无人机）、10 喷淋池被冲洗、11 酸沼腐蚀（Unit = 受伤者，只在它这一秒第一次挨时发）。</summary>
        ClarityCue = 48,
    }

    /// <summary>一次开火尝试的结果（编队攻击、直控点击都走 <see cref="CombatKernel.FireAt"/>；热更层映射成 Demo 同一套原因文本）。</summary>
    public enum CombatFireResult : byte
    {
        Ok = 0,
        /// <summary>重炮：这一次调用只推进了瞄准线，还没打出去（不是失败）。</summary>
        StillAiming = 1,
        NoAttacker = 2,
        AttackerDead = 3,
        /// <summary>没有登记武器（装配解析失败）。</summary>
        NoWeapon = 4,
        /// <summary>装配没有可攻击的主武器出口（8 号汇槽为空）。</summary>
        NoCombatOutput = 5,
        Overheated = 6,
        Cooldown = 7,
        TargetDead = 8,
        TargetMissing = 9,
        OutOfRange = 10,
        /// <summary>目标不可被攻击（己方 / 不可选中）。</summary>
        NotHostile = 11,
        /// <summary>目标当前无法被击伤（首领阶段）：开火照常，伤害不落地。</summary>
        Invulnerable = 12,
        /// <summary>FG6-DEF-01（FGR-DEF-004）：武器每发要消耗补给（流体类固件的流体），单位的补给存量不够一发——停火（原因由热更层按缺哪种流体写明）。</summary>
        NoAmmo = 13,
        /// <summary>FG9-FAC-01：目标在潜伏中，不能被远程锁定（用范围伤害、标记器或信号中继揭示）。</summary>
        Hidden = 14,
        /// <summary>FG9-FAC-02：目标只能被范围伤害 / 牵引作用（子母雷）。</summary>
        AreaOnly = 15,
        /// <summary>FG10-FAC-01：开火者被冻结（液氮泵车），解冻前不能开火。</summary>
        Frozen = 16,
        /// <summary>FG10-FAC-01（FG18 FGR-AIR-010）：目标在空中（灭活无人机），格斗 / 力场 / 布区打不到——用射弹、无人机或对空武器。</summary>
        CannotHitAir = 17,
    }

    /// <summary>武器开火方式。</summary>
    public enum CombatWeaponMode : byte
    {
        /// <summary>即时命中（Demo 的连射器、切割束、敌人自卫攻击）。</summary>
        Instant = 0,
        /// <summary>铸造重炮：两段式调用（第一次开始 1 秒瞄准线，到点后的下一次调用才开火），带冷却与热量（Demo CannonCombat）。</summary>
        Cannon = 1,
        /// <summary>弹体：生成一枚直线飞行的弹体，飞行中与敌对单位碰撞才结算（炮塔、突袭者；FGR-ARC-003“弹道唯一真相在内核”）。</summary>
        Projectile = 2,
    }

    /// <summary>具名反应（Demo：标记跳转、熔穿过载）。</summary>
    public enum CombatReaction : byte
    {
        None = 0,
        MarkJump = 1,
        MeltOverload = 2,
    }

    /// <summary>
    /// 炮塔选目标模式（FG06 FGR-DEF-003：最近、最高威胁、精英优先、最低耐久、优先攻击正在破坏建筑的敌人；FG6-DEF-01 补齐后三种）。
    /// 炮塔只按玩家选的模式选目标：射程内（视线要求照常）按模式的比较规则取最优，并列一律取更近者、再取槽位大者（与“最近”同一套并列规则）。
    /// - 最高威胁：威胁 = 目标武器每秒能打出的基础伤害（伤害 × 读法伤害倍率 ÷ 实际出手间隔）；没有武器的目标威胁为 0。
    /// - 精英优先：射程内有带 <see cref="CombatUnitFlags.Elite"/> 的目标就打其中最近的，没有就打最近的。
    /// - 优先攻击正在破坏建筑的敌人：射程内有带 <see cref="CombatUnitFlags.SiegeAttack"/> 的目标就打其中最近的，没有就打最近的。
    /// 取值进存档（武器参数里），顺序不能改、只能在末尾追加。
    /// </summary>
    public enum CombatTargetMode : byte
    {
        Nearest = 0,
        LowestHealth = 1,
        HighestThreat = 2,
        EliteFirst = 3,
        SiegeFirst = 4,
    }

    /// <summary>武器参数（一套装配或一种敌人一份）。数值全部由热更层给出（Demo 常量 / Luban 表），内核不读表。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatWeapon
    {
        public CombatWeaponMode Mode;
        public CombatReaction Reaction;
        /// <summary>1 = 装配有可攻击的主武器出口（Demo HasCombatOutput）；重炮不看它。</summary>
        public byte HasOutput;
        public CombatTargetMode TargetMode;
        /// <summary>射程（驻守开火 / 炮塔 / 突袭者用；编队攻击命令用命令自带的接战距离）。</summary>
        public float Range;
        public float Damage;
        public float Cooldown;
        /// <summary>瞄准线秒数（重炮 1 秒 / 步进炮 1 秒）。</summary>
        public float AimSeconds;
        public float ProjectileSpeed;
        public float ProjectileRadius;
        public float ProjectileLife;
        /// <summary>重炮每发基础积热。</summary>
        public float HeatPerShot;
        /// <summary>熔穿过载额外积热。</summary>
        public float OverloadExtraHeat;
        public float OverheatAt;
        public float RecoverBelow;
        public float Dissipation;
        public float HeatSinkBonus;
        /// <summary>熔穿过载的额外穿甲（从护甲减伤比例里扣掉）。</summary>
        public float PierceBonus;
        /// <summary>命中后给目标打标记的秒数（0 = 没有标记功能）。</summary>
        public float MarkSeconds;
        public float JumpRange;
        public float JumpFalloff;
        public int JumpMax;
        /// <summary>FG2-FW-02：载体与固件读法（全 0 = 射弹载体、没有读法；Demo 的敌人武器、炮塔原型都是这样）。
        /// FG2-FW-02 起 <see cref="HeatPerShot"/> 对所有开火方式生效（DEBT-FG1SIG06-02：即时命中武器也按固件积热、过热停火）。</summary>
        public CombatReading Reading;
        /// <summary>FG2-E2E-01（FG-GAP-043，设计案 5.3“引信与弹芯类主要体现在弹体特效上，炮口有一下装定闪光”）：
        /// 1 = 这套装配里有生效的引信类固件——每次开火在内核里记一条弹迹（炮口 → 命中点）与炮口装定闪光，渲染缓冲画出来；0 = 没有。只影响表现，不影响结算。</summary>
        public byte FuseTrace;
        /// <summary>FG6-DEF-01（FGR-DEF-002“默认 360° 旋转，转速由组件决定”）：驻守开火的单位（炮塔）朝目标转动的速度（度 / 游戏秒）；
        /// 朝向与目标方向的夹角在 <see cref="CombatConfig.TurretAimToleranceDeg"/> 以内才开火。0 = 瞬间转向（机器、敌人、原型炮塔）。朝向存在单位的装甲朝向里。</summary>
        public float TurnRate;
        /// <summary>FG6-DEF-01（FGR-DEF-004）：每发要消耗的补给量（流体类固件的流体，按“发”计）。0 = 不需要补给。存量不够一发时开火结果为 <see cref="CombatFireResult.NoAmmo"/>。</summary>
        public float AmmoPerShot;
        /// <summary>EMG1-KRN-01（ADR-EMG-001 ②，格式 16）：程序表行号 + 1（0 = 没有程序）。按 +1 存是为了让结构默认值就是“没有程序”——热更层与旧代码不写它也不会误指第 0 行；
        /// 读写一律用 <see cref="Program"/>（−1 = 没有）。</summary>
        public int ProgramRef;
        /// <summary>EMG1-KRN-01（ADR ③）：1 = 程序需要飞行，即时命中 / 重炮在“当场结算”那一刻改为生成一发弹体（EMG1-TRIG-01 接上执行）；不改 <see cref="Mode"/>。</summary>
        public byte SpawnFlight;

        /// <summary>程序下标（−1 = 没有程序）。</summary>
        public int Program
        {
            get => ProgramRef - 1;
            set => ProgramRef = value < 0 ? 0 : value + 1;
        }
    }

    /// <summary>FG2-E2E-01（FG-GAP-043）：一条引信弹迹（表现数据：不进快照、不进状态哈希；按游戏时间到期，暂停时不消失）。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatShotTrace
    {
        public double2 From;
        public double2 To;
        public double Born;
        public byte Faction;
        /// <summary>1 = 画弹迹线（即时命中 / 重炮）；0 = 只有炮口闪光（弹体武器：弹体自己飞）。</summary>
        public byte Line;
    }

    /// <summary>行为参数（一种敌人一份）。</summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatBehaviorProfile
    {
        public float Speed;
        /// <summary>威胁进入这个距离就后撤（侦察 / 维修）。</summary>
        public float FleeTrigger;
        /// <summary>后撤不超出出生点这个半径。</summary>
        public float Leash;
        public float PatrolRadius;
        /// <summary>巡逻摆动的角频率（sin(游戏秒 × 该值)）。</summary>
        public float PatrolFreq;
        /// <summary>侦察标记距离 / 突袭者感知距离。</summary>
        public float SenseRange;
        /// <summary>周期（侦察标记间隔 / 维修冷却）。</summary>
        public float CycleSeconds;
        /// <summary>效果持续（侦察标记秒数）。</summary>
        public float EffectSeconds;
        /// <summary>效果量（维修治疗量）。</summary>
        public float EffectAmount;
        /// <summary>效果范围（维修距离 / 干扰清标记半径）。</summary>
        public float EffectRange;

        // ── FG9-FAC-01 静默专属语法（全 0 = 没有；格式 13 起进快照）──
        /// <summary>静默潜伏：静止（不移动、不开火）这么多秒后进入潜伏；0 = 不潜伏。</summary>
        public float LurkSeconds;
        /// <summary>现形预警（展桅）：现形后这么多秒内不开火（≥ 0.8 秒，FGR-FAC-008 ①）。</summary>
        public float RevealSeconds;
        /// <summary>侦测距离：装有信号中继或标记器的己方机器在这个距离内时不能潜伏、已潜伏的立即现形。</summary>
        public float RevealRange;
        /// <summary>偏折场：半径（米，0 = 没有）、预警秒数、展开秒数、间隙秒数、反弹比例（射弹伤害 × 本值打回开火者）。</summary>
        public float DeflectRadius;
        public float DeflectWarn;
        public float DeflectUp;
        public float DeflectGap;
        public float DeflectReflect;
        /// <summary>断链束：射程（米，0 = 没有）、持续照射多少秒强制断链、断链后的冷却秒数。</summary>
        public float SeverRange;
        public float SeverSeconds;
        public float SeverCooldown;

        // ── FG9-FAC-02 铸造专属语法（全 0 = 没有；格式 14 起进快照）──
        /// <summary>齐射弹幕：一轮几发（0 = 不齐射）、每发间隔、装填秒数、装填完成灯预警秒数（≥ 0.8）、空爆半径、模式（1 = 只打空中目标，2 = 空爆）。</summary>
        public float SalvoCount;
        public float SalvoGap;
        public float SalvoReload;
        public float SalvoWarn;
        public float SalvoRadius;
        public float SalvoMode;
        /// <summary>布雷：同时最多几颗（0 = 不布雷）、间隔、触发与爆炸半径、伤害、引信秒数（≥ 0.8）、每颗存活秒数。</summary>
        public float MineMax;
        public float MineCycle;
        public float MineRadius;
        public float MineDamage;
        public float MineFuse;
        public float MineLife;
        /// <summary>装填光环：半径（0 = 没有）、额外装填速度（0.6 = 冷却多走 60%）。</summary>
        public float ReloadRange;
        public float ReloadRate;
        /// <summary>冲锋：速度（0 = 不冲锋）、蓄力秒数（≥ 0.8）、冲锋持续、冷却、击退米数、转向速度（度/秒；正面推进）。</summary>
        public float ChargeSpeed;
        public float ChargeWarn;
        public float ChargeSeconds;
        public float ChargeCooldown;
        public float ChargeKnock;
        public float TurnRate;
        /// <summary>架炮驻锄：开火前架设秒数（0 = 不用架设）；高抛曲射近距盲区（米，0 = 没有）。</summary>
        public float DeploySeconds;
        public float MinRange;

        // ── FG10-FAC-01 澄净专属语法（全 0 = 没有；格式 15 起进快照）──
        /// <summary>喷淋布区：锥形射程（米，0 = 不喷）、锥形半角（度）、喷嘴蓄压预警（≥ 0.8 秒）、两次喷淋的间隔、命中伤害、每次给命中者加的受寒（液氮）、
        /// 命中挂的状态位与时长；落地池的半径、秒数、每秒伤害、每节拍受寒。</summary>
        public float SprayRange;
        public float SprayCone;
        public float SprayWarn;
        public float SprayCycle;
        public float SprayDamage;
        public float SprayChill;
        public uint SprayStatus;
        public float SprayStatusSeconds;
        public float PoolRadius;
        public float PoolSeconds;
        public float PoolDps;
        public float PoolChill;
        /// <summary>枢轴喷灌：喷淋臂长（米，0 = 没有）、臂宽（度）、转速（度 / 秒）、蓄压预警（≥ 0.8 秒；也决定臂前方预警扇区的宽度）、扫中时每秒伤害（挂 <see cref="SprayStatus"/>）。</summary>
        public float PivotRadius;
        public float PivotWidth;
        public float PivotSpeed;
        public float PivotWarn;
        public float PivotDps;
        /// <summary>自爆：爆炸半径（米，0 = 不自爆）、伤害、引信（≥ 0.8 秒）、触发距离、被火击毁时的爆燃倍率；油迹：间隔、半径、秒数、挂的状态位（油；也是它身上一直带着的“油污”）。</summary>
        public float BurstRadius;
        public float BurstDamage;
        public float BurstFuse;
        public float BurstTrigger;
        public float DeflagMult;
        public float TrailInterval;
        public float TrailRadius;
        public float TrailSeconds;
        public uint TrailStatus;
        /// <summary>审查修复：油迹 / 自爆溅油挂的状态持续秒数（表 statusSeconds；0 = 区域默认）。</summary>
        public float TrailStatusSeconds;
        /// <summary>分裂：阵亡时分成几滩（0 = 不分裂）、每滩耐久、爬行速度、存活秒数、接触每秒伤害、接触半径。</summary>
        public float SplitCount;
        public float SplitHp;
        public float SplitSpeed;
        public float SplitLife;
        public float SplitDps;
        public float SplitRadius;
        /// <summary>飞行（1 = 空中单位）；投放：接近距离（米，0 = 不投放）、投放预警（≥ 0.8 秒）。</summary>
        public float Airborne;
        public float PolluteRange;
        public float PolluteChannel;
        /// <summary>抗性：酸蚀抗性（0～1）与挂不上的状态位（生成时写进每单位状态，读档后沿用单位自己的值）。</summary>
        public float AcidResist;
        public uint ImmuneMask;
        /// <summary>腐蚀蜂群“专门腐蚀建筑”：1 = 感知范围内有己方建筑 / 炮塔时优先打它们。</summary>
        public float StructPref;
    }

    /// <summary>生成一个单位的参数。</summary>
    public struct CombatSpawn
    {
        public int ExtKey;
        public CombatUnitKind Kind;
        public CombatFaction Faction;
        public CombatBehavior Behavior;
        public CombatUnitFlags Flags;
        public double2 Position;
        /// <summary>出生点 / 牵引中心 / 突袭者目标点。</summary>
        public double2 Home;
        public float Radius;
        public float Speed;
        public float Health;
        public float MaxHealth;
        public int Weapon;
        public int BehaviorProfile;
        /// <summary>正面装甲减伤比例（0 = 没有装甲概念）。</summary>
        public float ArmorFraction;
        public float ArmorHalfAngleDeg;
        public float2 ArmorFacing;
        /// <summary>侧后命中加成（主核心阶段二 +20%；热更层在阶段变化时改）。</summary>
        public float BackHitBonus;
        /// <summary>同一步内的结算顺序：0 先于 1（Demo 铸造前哨里主核心先于其它敌人）。</summary>
        public byte Priority;
        public float Heat;
        public double AimReadyAt;
        public double NextFireAt;
        /// <summary>行为周期计时的初值（Demo 记录里的 CycleCooldownRemaining / SecondaryTimer）。</summary>
        public float Cycle;
        public float Secondary;
        /// <summary>FG6-DEF-01：补给存量的初值（按“发”计；武器不需要补给时无意义）。</summary>
        public float Ammo;
        /// <summary>FG6-DEF-05：攻城属性（突袭单位的职能 / 队伍、结构单位的目标类别 / 占地；全 0 = 不参与攻城）。</summary>
        public CombatSiegeUnit Siege;
    }

    /// <summary>内核事件（40 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatEvent
    {
        public CombatEventKind Kind;
        public byte Code;
        public byte Code2;
        public byte Pad;
        public int Unit;
        public int Other;
        public float Value;
        public float Value2;
        /// <summary>事件序号（玩法事件与提示事件共用一个递增计数）：热更层按它合并两条队列，保持与 Demo 逐调用一致的先后顺序。</summary>
        public int Seq;
        public double2 Pos;
    }

    /// <summary>单位的只读快照（查询用）。</summary>
    public struct CombatUnitView
    {
        public int Id;
        public int ExtKey;
        public CombatUnitKind Kind;
        public CombatFaction Faction;
        public CombatBehavior Behavior;
        public CombatUnitFlags Flags;
        public double2 Position;
        public double2 PrevPosition;
        public float Health;
        public float MaxHealth;
        public float Heat;
        public double AimReadyAt;
        public double NextFireAt;
        public float Cycle;
        public float Secondary;
        public CombatCommandKind Command;
        public int CommandTarget;
        public double2 CommandPos;
        public double MarkedUntil;
        public int Weapon;
        /// <summary>FG6-DEF-01：补给存量（发）与朝向（炮塔的炮口朝向 = 装甲朝向）。</summary>
        public float Ammo;
        public float2 Facing;
        /// <summary>FG6-DEF-05：攻城属性（职能 / 撤退 / 目标类别 / 队伍 / 占地 / 破墙目标）。</summary>
        public CombatSiegeUnit Siege;
        /// <summary>FG9-FAC-01：静默状态（潜伏 / 偏折场 / 断链束）。</summary>
        public CombatSilentUnit Silent;
        /// <summary>FG10-FAC-01：澄净状态（喷淋 / 枢轴 / 自爆 / 飞行 / 受寒 / 抗性）。</summary>
        public CombatClarityUnit Clarity;
        public bool Alive => (Flags & CombatUnitFlags.Alive) != 0;
    }

    /// <summary>内核配置（热更层读 combat.* 调参行组装）。</summary>
    [Serializable]
    public struct CombatConfig
    {
        /// <summary>空间网格格边长（米）。</summary>
        public float GridCell;
        /// <summary>每步最多交给热更层处理的玩法事件数（超出的留到下一步，不丢；FGR-SYS-042 热更层开销与数量无关）。</summary>
        public int MaxGameplayEventsPerStep;
        /// <summary>每步最多保留的提示事件数（超出丢弃并计数）。</summary>
        public int MaxCueEventsPerStep;
        /// <summary>同时存在的弹体上限（超出的开火不生成弹体并计数）。</summary>
        public int ProjectileCapacity;
        /// <summary>墓碑（已移除的空槽）超过这个比例时在步首压实。</summary>
        public float CompactRatio;
        /// <summary>编队命令路径受阻判定：每隔几秒核对一次进展、至少前进多少米、几次不达标放弃（Demo 1 / 0.5 / 3）。</summary>
        public float ProgressInterval;
        public float MinProgress;
        public int MaxStuckStrikes;
        /// <summary>局部避障：前瞻距离、单位半径、绕行权重（Demo 3 / 0.9 / 1.35）。</summary>
        public float Lookahead;
        public float AvoidRadius;
        public float AvoidWeight;
        /// <summary>FG0-ARCH-06：1 = 本地点在星球格网上，移动走层级寻路路线、不穿越不可通行的格子（家园）；0 = Demo 的独立表面（破碎都市、铸造前哨：圆形障碍 + 局部绕障）。</summary>
        public byte NavEnabled;
        /// <summary>FG0-ARCH-06：分离（同阵营移动单位互相推开，不叠在同一处）的最大推开速度占移动速度的比例；0 = 关。</summary>
        public float SeparationFactor;
        /// <summary>FG2-FW-02：同时存在的区域 / 待结算回波 / 无人机上限（0 = 用 <see cref="CombatConst"/> 的默认值；超出的不生成并计数）。</summary>
        public int ZoneCapacity;
        /// <summary>FG6-DEF-02 复审修复：陷阱发射器场地的上限（0 = <see cref="CombatConst.DefaultFieldZoneCapacity"/>）；与 <see cref="ZoneCapacity"/> 分开计数。</summary>
        public int FieldZoneCapacity;
        public int EchoCapacity;
        public int DroneCapacity;
        /// <summary>FG2-FW-02：无人机移动速度（米 / 秒）、触及距离（米，另加目标半径）、状态持续伤害节拍与区域节拍（游戏秒）。0 = 内核默认值。</summary>
        public float DroneSpeed;
        public float DroneReach;
        public float StatusTick;
        public float ZoneTick;
        /// <summary>FG2-FW-02：区域给站在里面的单位挂状态时，读法没带状态时长（没有状态标签固件）用的时长（游戏秒，fg.TbHomeTuning reading.zone.status_seconds）。0 = 内核默认值。</summary>
        public float ZoneStatusSeconds;
        /// <summary>FG2-FW-02：连网的减速网比两端之间的半距再宽多少米（fg.TbHomeTuning reading.weave.margin）。0 = 内核默认值。</summary>
        public float WeaveMargin;
        /// <summary>FG2-FW-03：同一状态标签最多叠几层（fg.TbHomeTuning status.stack_cap，1～3）。0 = 不叠层（按 1 层）。</summary>
        public int StatusStackCap;
        /// <summary>FG2-E2E-01（FG-GAP-043）：引信弹迹 / 炮口装定闪光在画面上停留的游戏秒（fg.TbHomeTuning combat.fuse_trace_seconds）。0 = 不记弹迹。</summary>
        public float FuseTraceSeconds;
        /// <summary>FG6-DEF-01（FGR-DEF-002）：有转速的炮塔，炮口朝向与目标方向夹角在这个度数以内才开火（fg.TbHomeTuning turret.aim_tolerance_deg）。0 = 内核默认 6°。</summary>
        public float TurretAimToleranceDeg;
        /// <summary>FG6-LOG-10（FGR-LOG-046）：液洼整片反应成残留区域后至少持续多少游戏秒（fg.TbHomeTuning logistics.leak.react_seconds；与反应自己的残留时长取大）。0 = 只用反应的残留时长。</summary>
        public float LeakReactSeconds;
        /// <summary>FG10-FAC-01：喷淋池 + 油迹的共用上限（0 = <see cref="CombatConst.DefaultPoolZoneCapacity"/>）。</summary>
        public int PoolZoneCapacity;
        /// <summary>FG10-FAC-01（液氮“先减速再冻结”）：受寒满时的最大减速比例、每秒自然消退量、冻结秒数、解冻后的免疫秒数（fg.TbHomeTuning clarity.chill.*；0 = 内核默认 0.6 / 0.25 / 2.5 / 1.5）。</summary>
        public float ChillSlowMax;
        public float ChillDecay;
        public float FreezeSeconds;
        public float ThawGuardSeconds;

        public static CombatConfig Default => new CombatConfig
        {
            GridCell = 8f,
            MaxGameplayEventsPerStep = 64,
            MaxCueEventsPerStep = 24,
            ProjectileCapacity = 4096,
            CompactRatio = 0.5f,
            ProgressInterval = 1f,
            MinProgress = 0.5f,
            MaxStuckStrikes = 3,
            Lookahead = 3f,
            AvoidRadius = 0.9f,
            AvoidWeight = 1.35f,
            NavEnabled = 0,
            SeparationFactor = 0.5f,
            FuseTraceSeconds = 0.15f,
        };
    }

    /// <summary>汇总计数（内核每步累计；统计面板 / 自检 / 性能证据用）。</summary>
    [Serializable]
    public struct CombatCounters
    {
        public long Steps;
        public long ShotsFired;
        public long ProjectilesSpawned;
        public long ProjectilesHit;
        public long ProjectilesExpired;
        /// <summary>弹体池满导致没能生成的开火次数。</summary>
        public long ProjectilesRefused;
        public long KillsPlayer;
        public long KillsHostile;
        public long DamageToPlayer;
        public long DamageToHostile;
        public long CuesDropped;
        public long GameplayEventsDeferred;
        public long Compactions;
        /// <summary>FG2-FW-02（格式 3）：读法生成的区域 / 回波 / 无人机，以及因容量满没生成的次数（三类合计）。</summary>
        public long ZonesSpawned;
        public long EchoesQueued;
        public long DronesLaunched;
        public long ReadingRefused;
        /// <summary>FG2-FW-03（格式 4）：具名标签反应触发总次数。</summary>
        public long ReactionsFired;
        /// <summary>FG9-FAC-01（格式 13）：被偏折场反弹的己方射弹次数（即时 / 重炮 / 弹体合计）。</summary>
        public long ShotsReflected;
        /// <summary>FG9-FAC-02（格式 14）：齐射轮数、对空拦截（击落无人机 + 弹体）、铺雷、引爆（含提前引爆）、冲锋次数。</summary>
        public long Salvos;
        public long AirIntercepts;
        public long MinesLaid;
        public long MinesDetonated;
        public long Charges;
        /// <summary>FG10-FAC-01（格式 15）：喷淋次数、冻结次数、自爆、被火击毁的爆燃、油迹被点燃、分裂、再污染投放、酸沼腐蚀（单位·秒）、喷淋池被冲洗、枢轴扫中（单位·节拍）。</summary>
        public long Sprays;
        public long Freezes;
        public long Bursts;
        public long Deflagrations;
        public long SlicksIgnited;
        public long Splits;
        public long Repollutions;
        public long AcidTicks;
        public long PoolsWashed;
        public long PivotHits;
    }

    /// <summary>渲染实例（32 字节，与 CombatInstanced.shader 一致）：A = (当前 x, 当前 z, 上一步 x, 上一步 z)，B = (半径, 血量比例, 阵营, 种类)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CombatInstance
    {
        public float4 A;
        public float4 B;
    }
}
