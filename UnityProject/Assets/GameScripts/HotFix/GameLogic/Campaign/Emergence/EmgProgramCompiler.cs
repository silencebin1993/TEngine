using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Emergence
{
    /// <summary>指令类别（FGR-EMG-012：由声明的写端推导，不另填）。</summary>
    public enum EmgStepKind : byte
    {
        Effect = 0,
        Trigger = 1,
        Anchor = 2,
        Meta = 3,
        Modify = 4,
    }

    /// <summary>程序里的一条指令（一个程序位置）。混合固件展开为两条，父 A 在前（FGR-EMG-044）。</summary>
    public sealed class EmgProgramStep
    {
        /// <summary>EMG4-HIJ-02：浅拷贝（持有型截断把被截掉的步骤副本记为溢出，不改原程序的步骤对象）。</summary>
        public EmgProgramStep CloneStep() => (EmgProgramStep)MemberwiseClone();

        /// <summary>程序序号（0 起，= 在指令序列里的位置）。</summary>
        public int Ordinal;
        /// <summary>EMG4-CLEAN-01（FG-GAP-208）：逆序树的步骤对应正序树里的哪一条（正序序号）；正序树自己为 −1。</summary>
        public int Src = -1;
        /// <summary>插在槽里的固件（混合固件是混合固件本身）。</summary>
        public string FirmwareId;
        /// <summary>声明的 owner（混合固件展开后是父固件）。</summary>
        public string OwnerId;
        public bool FromUplink;
        public bool Core;
        /// <summary>读单位级事件，第 0 步提升到根（FGR-EMG-031）。</summary>
        public bool Promoted;
        public EmgStepKind Kind;
        public EmgEventKind TrigEvent;
        public float TrigParam;
        public int TrigCount;
        /// <summary>“生效事件”端口码（FGR-EMG-017 重复判定键；没有事件触发读端记为出手 evt.fire）。</summary>
        public ushort EventPort;
        /// <summary>指令热量（fg.TbFirmwareKind.heat）。</summary>
        public float TableHeat;
        /// <summary>本载体上生效的声明（<see cref="EmgCompiled.Decls"/> 下标，按 seq）。</summary>
        public readonly List<int> Decls = new List<int>(4);
        public bool Overflow;
        /// <summary>EMG3-META-01（FGR-EMG-040 反转）：逆序树里位于反转之后的指令——写端 fld.pull ↔ fld.push 互换、grm.mod.return（回拉）→ grm.mod.pierce（前冲）。</summary>
        public bool Flip;
        /// <summary>EMG3-FUSE-01（FGR-EMG-044）：熔合芯片展开出的内置接线（不占节点容量、不占刷写带宽、不显示为独立固件）。</summary>
        public bool Builtin;
        /// <summary>EMG4-LOOT-01 修复轮 1（FGR-FAC-009）：主组件自带的程序声明编成的内置指令（放在根节点、另加一格容量，不挤占固件槽、不占刷写带宽）。</summary>
        public bool Innate;
        /// <summary>EMG3-FUSE-01（FG19A 第 4.2 节）：这条指令的写端产生的落点种类（EmgAnchorConst；0 = 不接线）。</summary>
        public byte AnchorKind;
    }

    /// <summary>EMG3-FUSE-01：一处接线在预览里的说明——接线的程序序号、A / B 的序号、落点种类、每次结算最多执行几个落点、是否生效（无效时 Note = 原因文本键）、编译期热量。</summary>
    public struct EmgAnchorInfo
    {
        public int Ordinal;
        public int A;
        public int B;
        public byte Kind;
        public int MaxLandings;
        public bool Valid;
        public string Note;
        public float Heat;
        /// <summary>复制到落点的 B 的效果写端行数（预览“每个落点执行 N 条”）。</summary>
        public int Copies;
    }

    /// <summary>EMG3-META-01（FGR-EMG-038 / 042）：一条元程序在预览里的说明——作用的节点、序号、种类（EmgMetaConst）、下游段（程序序号）、每次结算执行次数、是否生效、累计延后秒数。</summary>
    public struct EmgMetaInfo
    {
        public int Node;
        public int Ordinal;
        public int Kind;
        public List<int> Down;
        public int Exec;
        public bool Valid;
        public float Delay;
    }

    public struct EmgTreeEntry
    {
        public int Ordinal;
        public bool Inherited;
        /// <summary>继承副本来自哪个节点（自有指令 = 本节点）。</summary>
        public int FromNode;
    }

    /// <summary>程序树的一个节点（预览与内核共用的编译结果）。</summary>
    public sealed class EmgTreeNode
    {
        public int Index;
        public int Parent = -1;
        public int Gen;
        public int Capacity;
        /// <summary>放出本节点的触发器的程序序号（根 = −1）。</summary>
        public int TriggerOrdinal = -1;
        public EmgEventKind TrigEvent;
        public int TrigCount;
        public float TrigParam;
        public readonly List<EmgTreeEntry> Entries = new List<EmgTreeEntry>(4);
        public int Used => Entries.Count;
    }

    /// <summary>编译原因（FGR-EMG-038 的文本键 + 指令 / 节点 / 端口）。</summary>
    public struct EmgCompileNote
    {
        public string Key;
        public int Ordinal;
        public int Node;
        public ushort Port;
    }

    /// <summary>一把武器的编译结果：指令序列、程序树、原因、热量分项与内核三张表的行。</summary>
    /// <summary>FG-GAP-173：合并行的一个来源。</summary>
    public struct EmgMergeSource
    {
        public byte Ordinal;
        public bool Core;
        public float Amount;
        public float Param2;
        public EmgInstrFlags Flags;
    }

    public sealed class EmgProgramResult
    {
        public readonly List<EmgProgramStep> Steps = new List<EmgProgramStep>(8);
        /// <summary>装着、但声明此刻不生效（stageGate 未到或没有声明）的固件：继续走旧读法，不进程序（FGR-EMG-019 双轨）。</summary>
        public readonly List<string> LegacyOwners = new List<string>(4);
        public readonly List<EmgTreeNode> Nodes = new List<EmgTreeNode>(4);
        public readonly List<EmgCompileNote> Notes = new List<EmgCompileNote>(4);
        public readonly List<EmgNode> KernelNodes = new List<EmgNode>(4);
        public readonly List<EmgInstr> KernelInstrs = new List<EmgInstr>(16);
        /// <summary>
        /// FG-GAP-173（EMG1-FW-01）：同节点同键合并成一行的指令，记下每个来源（程序序、是否核心、幅度、第二参数、它自己那份的标记位）；键 = KernelInstrs 下标。
        /// 刷写截断 / 信号离开剥离核心（<see cref="EmgProgramCompiler.Truncate"/>）按剩下的来源重算这一行，不再“核心的大幅度留在常规那行里”。
        /// </summary>
        public readonly Dictionary<int, List<EmgMergeSource>> MergeSources = new Dictionary<int, List<EmgMergeSource>>();
        public EmgProgram Program;
        public int RootCapacity;
        /// <summary>EMG4-HIJ-02（FG19 FGR-EMG-164）：根节点的代号。0 = 普通程序；1 = 镜像复制品（1 代程序：根容量 C(1)，触发器子载荷从 2 代起，2 代再触发 = 代际已满）。</summary>
        public int BaseGen;
        public bool IsCopy => BaseGen > 0;
        public int ContentVersion;
        public byte Carrier;
        /// <summary>EMG1-GRM-01：主组件可抛射（false = 光束：lob 动词不执行，给原因 emg.compile.lob_straight）。</summary>
        public bool LobAllowed = true;
        /// <summary>EMG2-MAT-01：主组件是修复出口（没有攻击出口、声明 prop.integrity modify，与 CombatSite.MachineWeaponFrom 同一判定）——修复弹体不反弹（FG-GAP-178 ④），读 evt.bounce 的固件整条“本载体不适用”。</summary>
        public bool HealOnly;
        /// <summary>EMG3-SQD-01（FGR-EMG-132）：这台车的结构模块提供 sqd.relay scope（struct_relay）——根节点上白名单事件的 trigger 读端可被中继转发（预览标注，<see cref="EmgProgramCompiler.RelayedOf"/>）。</summary>
        public bool Relay;
        /// <summary>热量分项（FGR-EMG-016）：指令表值、声明 heat、触发节点（trigger_gen × 代号）、子载荷份数 × per_child。</summary>
        public float HeatInstr, HeatDecl, HeatTrigger, HeatChildren;
        /// <summary>溢出指令的表值热量：旧积热（BlueprintCircuitBoard.ComputeFirmwareHeat）按装着的固件算过，溢出“不计热量”所以要扣回。</summary>
        public float HeatOverflowInstr;
        /// <summary>EMG3-CHS-02（FGR-EMG-140 取水）：写 mat.water / mat.coolant 的指令（未溢出、非继承）的表值热量 + 声明热量；在水面上的取水单位出手时不计（CombatWeapon.DrawHeat）。</summary>
        public float HeatDraw;
        /// <summary>一次根出手全树的子载荷份数（逐代份数相乘后求和）。</summary>
        public int ChildPayloads;
        /// <summary>程序完整字节键的 64 位哈希 + 内容版本（同一程序同一内容版本同签名，IC-REQ-010）。空程序为空串。</summary>
        public string Signature = string.Empty;
        public bool IsEmpty => Steps.Count == 0;
        public float HeatTotal => HeatInstr + HeatDecl + HeatTrigger + HeatChildren + HeatMeta + HeatAnchor;
        /// <summary>EMG3-FUSE-01（ADR ②、FGR-EMG-016）：每处生效的接线编译期计 emg.fuse.anchor_max × emg.fuse.anchor_heat，不按实际落点数计。</summary>
        public float HeatAnchor;
        /// <summary>EMG3-FUSE-01：接线说明（预览分项显示，与内核同一份编译结果）。</summary>
        public readonly List<EmgAnchorInfo> Anchors = new List<EmgAnchorInfo>(1);
        /// <summary>武器每发积热要在旧算法之上加的量（旧算法已含指令表值，这里只加新分项并扣回溢出指令）。</summary>
        public float WeaponHeatDelta => HeatDecl + HeatTrigger + HeatChildren + HeatMeta + HeatAnchor - HeatOverflowInstr;
        /// <summary>EMG3-META-01（FGR-EMG-042）：元程序加成——复制段 ×（执行次数 − 1）、循环段 × (emg.heat.loop_factor − 1)、随机段按平均值（少于原和时为负）；条件 / 反转 / 延后不加段热量。</summary>
        public float HeatMeta;
        /// <summary>EMG3-META-01：元程序说明（预览“下游段与执行次数”；与内核同一份编译结果）。</summary>
        public readonly List<EmgMetaInfo> Metas = new List<EmgMetaInfo>(2);
        /// <summary>EMG3-META-01（FGR-EMG-040 反转）：程序里有反转时的逆序树（同一套步骤按反转后的顺序编译，序号 = 逆序后的位置）；没有 = null。</summary>
        public EmgProgramResult Reverse;
        /// <summary>
        /// EMG4-CLEAN-01（FG-GAP-207 / 208、DEBT-EMG3META01-01）：根节点上位于条件 / 随机下游（正序或逆序树任一棵）的固件读法贡献——不折进静态读法，
        /// 由 CombatSite.ProgramReading 记成 <see cref="EmgInstrFlags.LegacyRoot"/> 行（序号 = 正序序号），登记程序时接在尾段（逆序树按 <see cref="EmgProgramStep.Src"/> 换成逆序序号）。没有为 null。
        /// </summary>
        public List<EmgInstr> LegacyOps;
    }

    /// <summary>
    /// EMG1-PRG-01 程序编译器（FG19 FGR-EMG-012、016、017、030～033、039、044、045；ADR-EMG-001 ②）。
    /// 每把武器的程序 = [接入固件 U1…Uq（实际接通的那几条，信号核槽序）, 机器固件槽 S1, S2]（AI 驾驶 / 没有接入口 / 接入口不在路径上时只有 S1, S2）；
    /// 混合固件展开为两条。按声明推导类别 → 第 0 步提升单位级读端 → 节点分配（容量 C0 = 槽数 + 配额，逐代减半，代际 ≤ 2，溢出）→
    /// 编译期继承（最近祖先优先、同一祖先按程序位置；默认只继承修饰写端，程序里有 grm.inherit 时触发器 / 效果也进候选）→
    /// 同节点同键写端幂等 → 热量分项 → 内核三张表的行与签名。
    /// 只在装配变化时运行（CombatSite.ResolveMachineWeapon，随 MachineLoadoutRegistry.Changed / 读档重算），O(程序长度)。
    /// 只认端口码与声明，不出现任何内容 ID 判断（FGR-EMG-013，FgEmergenceRuleAuditSelfCheck 扫本目录）。
    /// </summary>
    public static class EmgProgramCompiler
    {
        public const string NoteOverflow = "emg.compile.overflow";
        public const string NoteGenFull = "emg.compile.gen_full";
        public const string NoteNotFlashed = "emg.compile.not_flashed";
        public const string NoteDuplicate = "emg.compile.duplicate";
        public const string NoteTableFull = "emg.compile.table_full";
        public const string StaleContentKey = "emg.compile.stale_content";
        /// <summary>EMG3-META-01（FGR-EMG-042）：嵌套复制相乘超过 emg.meta.max_exec / 一条链累计延后超过 emg.meta.defer_total_max（超出部分不执行）/ 元程序没有下游段。</summary>
        public const string NoteMetaExecCap = "emg.compile.meta_exec_cap", NoteMetaDeferCap = "emg.compile.meta_defer_cap", NoteMetaEmpty = "emg.compile.meta_empty";

        private const ushort EventPortBase = EmgConst.EventPortBase;

        /// <summary>单位级事件（FGR-EMG-031 第 0 步、FG19A A3）：阵亡、被击、过热、冷却完成、接入、断开、拾取、夺控、归还。</summary>
        private static bool IsUnitLevel(EmgEventKind e) => e == EmgEventKind.Death || e == EmgEventKind.Hurt || e == EmgEventKind.Overheat
                                                         || e == EmgEventKind.Cooled || e == EmgEventKind.Uplink || e == EmgEventKind.Unlink
                                                         || e == EmgEventKind.Pickup || e == EmgEventKind.Seize || e == EmgEventKind.Release;

        /// <summary>根节点触发读这些事件 = 程序需要飞行（ADR ③）。EMG2-MAT-01：加上 evt.enter / evt.leave——载荷“进入 / 离开斑块”只有飞行弹体会发生，
        /// 即时命中没有飞行路径（入场引信装在连射器上永远不触发，FGR-EMG-221 零反应；按推荐默认执行，2026-11-02，待用户确认）。</summary>
        private static bool NeedsFlightEvent(EmgEventKind e) => e == EmgEventKind.Timer || e == EmgEventKind.Prox || e == EmgEventKind.Expire
                                                              || e == EmgEventKind.Bounce || e == EmgEventKind.Pierce
                                                              || e == EmgEventKind.Enter || e == EmgEventKind.Leave
                                                              || e == EmgEventKind.Beat; // EMG3-TMP-01：射弹读法“每 N 拍从弹体当前位置放出子弹药（飞行中布弹）”只有飞行弹体做得到（FG19A fw_clock 行）

        /// <summary>根节点写这些端口 = 程序需要飞行（ADR ③）。EMG1-GRM-01：加上 grm.mod.multi——霰射“一次打出 3 份真实弹丸，每份各自命中、各自触发”只有飞行弹体做得到（待用户复核）。</summary>
        private static readonly string[] FlightMods =
            { "grm.mod.homing", "grm.mod.pierce", "grm.mod.bounce", "grm.mod.return", "grm.mod.orbit", "grm.mod.lob", "grm.mod.drift", "grm.mod.multi", "meta.loop" }; // EMG3-META-01：根节点循环 = 飞行中周期执行（FGR-EMG-043）

        /// <summary>EMG1-GRM-01：光束类主组件（作战组件表 flightLob = 0）上的曲射不越障，预览写原因（FG19A 第 2.1.1 节）。</summary>
        public const string NoteLobStraight = "emg.compile.lob_straight";

        /// <summary>EMG1-FW-02（FG-GAP-177）：声明的适用载体（carriers）不含当前主组件 → 这条指令整条不编译，预览写原因（仍占程序位置）。</summary>
        public const string NoteCarrierNa = "emg.compile.carrier_na";

        /// <summary>EMG3-FUSE-01（FGR-EMG-044）：接线前面或后面没有指令（首位 / 末位，或那条溢出了）/ A 与 B 不在同一节点 / A 的写端没有落点（预混：A、B 同点执行）/
        /// B 只有修饰写端（修饰照常作用于节点、不接线）/ 这种落点内核尚未接通（承接卡见 FG-GAP）/ A 一次结算的落点可能超过上限（超出的不执行）。</summary>
        public const string NoteAnchorDangling = "emg.compile.anchor_dangling", NoteAnchorSplit = "emg.compile.anchor_split", NoteAnchorNone = "emg.compile.anchor_none",
            NoteAnchorModOnly = "emg.compile.anchor_mod_only", NoteAnchorPending = "emg.compile.anchor_pending", NoteAnchorCapped = "emg.compile.anchor_capped", NoteAnchorNoExec = "emg.compile.anchor_noexec",
            NoteAnchorFollowSolo = "emg.compile.anchor_follow_solo", NoteAnchorFollowChild = "emg.compile.anchor_follow_child";

        // ── 目录派生的查找表（目录换了才重建；只在编译时用，不按帧）──
        private static EmgCompiled _for;
        private static readonly Dictionary<int, List<int>> DeclsByOwner = new Dictionary<int, List<int>>();
        private static readonly Dictionary<int, int> PortCarrierAt = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> SpreadAt = new Dictionary<int, int>();
        private static readonly Dictionary<ushort, string> PortIds = new Dictionary<ushort, string>();
        private static readonly HashSet<string> WarnedTuning = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>按预览编：接入固件取 <see cref="BlueprintCircuitPreview.UplinkFirmwareIds"/>，机器固件取 <see cref="BlueprintCircuitPreview.LocalFirmwareIds"/>（不读 FirmwareIds），载体取主组件。</summary>
        public static EmgProgramResult Compile(BlueprintCircuitPreview p) => Compile(p, null);

        /// <param name="skipUplink">EMG1-E2E-01 修复轮（FGR-SIG-033 声明轨）：接入口里正在冷却的核心固件——不编进程序（冷却中这条反应不发动，固件照样插着）；null = 不跳过。</param>
        public static EmgProgramResult Compile(BlueprintCircuitPreview p, ICollection<string> skipUplink, int baseGen = 0)
        {
            if (p == null)
            {
                return new EmgProgramResult();
            }
            CarrierReadings.TryGetCarrier(p.PrimaryId, out FirmwareCarrier carrier); // 查不到时是射弹（与读法同一默认）
            bool lob = !CarrierReadings.TryGetComponent(p.PrimaryId, out GameConfig.fg.CombatComponent comp) || comp.Flight != "yes" || comp.FlightLob != 0;
            bool healOnly = false;
            if (!p.HasCombatOutput && !string.IsNullOrEmpty(p.PrimaryId))
            {
                EmgCatalog.PartPropModify("prop.integrity", new[] { p.PrimaryId }, out float heal);
                healOnly = heal > 0f;
            }
            EmgProgramResult res = Compile(p.UplinkFirmwareIds, p.LocalFirmwareIds, carrier, 0, lob, skipUplink, healOnly, baseGen, p.PrimaryId);
            // EMG3-SQD-01：结构模块声明 sqd.relay scope（按声明判，不认模块 ID）；与 CombatSite.ApplyRelay 同一判定
            res.Relay = !string.IsNullOrEmpty(p.StructureId) && EmgCatalog.PartReads("sqd.relay", EmgRole.Scope, new[] { p.StructureId });
            AddNeverNotes(res, p.ChassisId, carrier == FirmwareCarrier.Melee && comp != null && comp.Cone >= 179.9f);
            return res;
        }

        /// <summary>EMG4-CLEAN-01（FG-GAP-215）：条件门对这台机器恒不成立（读 chs.airborne、底盘不会离地；读 evt.marked、修复出口目标是友军）。</summary>
        public const string NoteGateNever = "emg.compile.gate_never";
        /// <summary>EMG4-CLEAN-01（FG-GAP-215）：只写侧偏（grm.mod.drift）的指令装在挥击已是整圈的格斗主组件上。</summary>
        public const string NoteDriftFullCircle = "emg.compile.drift_fullcircle";

        /// <summary>
        /// EMG4-CLEAN-01（FG-GAP-215，与 emg.compile.carrier_na 同一口径）：按底盘特性 / 修复出口 / 挥击扇形给“本机恒不成立”的指令写预览原因（指令照常编译、占位、计热）。
        /// 按端口判，不认内容 ID（FGR-EMG-013）。O(指令数)，只在装配结算 / 预览时。
        /// shortcut: 整圈只看主组件表的 cone（固件再加的扇角不计），以后有固件能把扇形加到整圈时改读折完的读法。
        /// </summary>
        public static void AddNeverNotes(EmgProgramResult r, string chassisId, bool fullCircle)
        {
            if (r == null || r.IsEmpty)
            {
                return;
            }
            EmgCompiled c = EmgCatalog.Current;
            ushort Code(string id) => c.PortIndexById.TryGetValue(id, out int i) ? c.Ports[i].Code : (ushort)0;
            ushort metaIf = Code("meta.if"), air = Code("chs.airborne"), marked = Code("evt.marked"), drift = Code("grm.mod.drift");
            bool canAir = EmgCatalog.TryGetTrait(chassisId, out EmgTraitRow t) && !t.Reserved
                          && BinGames.Sim.Combat.EmgTraitConst.IsAirborneKind((byte)t.Kind);
            var gates = new HashSet<int>();
            var driftOnly = new Dictionary<int, bool>();
            foreach (EmgInstr x in r.KernelInstrs)
            {
                if (x.Role > EmgConst.RoleLastRead && (x.Flags & EmgInstrFlags.Overflow) == 0)
                {
                    if (x.Port == metaIf && metaIf != 0)
                    {
                        gates.Add(x.Ordinal);
                    }
                    driftOnly[x.Ordinal] = (!driftOnly.TryGetValue(x.Ordinal, out bool d) || d) && x.Port == drift && drift != 0;
                }
            }
            var noted = new HashSet<int>();
            foreach (EmgInstr x in r.KernelInstrs)
            {
                if (gates.Contains(x.Ordinal) && x.Role >= EmgConst.RoleTrigger && x.Role <= EmgConst.RoleLastRead
                    && ((x.Port == air && air != 0 && !canAir) || (x.Port == marked && marked != 0 && r.HealOnly)) && noted.Add(x.Ordinal))
                {
                    r.Notes.Add(new EmgCompileNote { Key = NoteGateNever, Ordinal = x.Ordinal, Node = -1 });
                }
            }
            if (fullCircle)
            {
                foreach (KeyValuePair<int, bool> kv in driftOnly)
                {
                    if (kv.Value)
                    {
                        r.Notes.Add(new EmgCompileNote { Key = NoteDriftFullCircle, Ordinal = kv.Key, Node = -1 });
                    }
                }
            }
        }

        // ── EMG1-FLASH-01（FG19 FGR-EMG-036 / 037；ADR ③）：刷写变体——无人机 / 哨戒桩只持有程序的前 k 条（带宽截断、距离暂停）或剥离核心后的前 k 条 ──

        /// <summary>参与刷写的指令（不溢出的程序位置，按程序序）；<paramref name="noCore"/> = 去掉核心指令后的列表。条数 = 持续实体占用的刷写带宽。</summary>
        public static List<EmgProgramStep> FlashSteps(EmgProgramResult r, bool noCore)
        {
            var list = new List<EmgProgramStep>(r?.Steps.Count ?? 0);
            if (r == null)
            {
                return list;
            }
            foreach (EmgProgramStep s in r.Steps)
            {
                if (!s.Overflow && !s.Builtin && !s.Innate && !(noCore && s.Core)) // EMG3-FUSE-01：内置接线不占刷写带宽；EMG4-LOOT-01：主组件内置指令同样
                {
                    list.Add(s);
                }
            }
            return list;
        }

        /// <summary>
        /// 只保留刷写列表前 <paramref name="k"/> 条的程序（节点结构、代号、触发参数都不变，指令行按 Ordinal 过滤：Ordinal ≤ 第 k 条的程序序；<paramref name="noCore"/> 时再去掉核心行）。
        /// 继承副本与合并行沿用编译器的口径（合并行的序号取最早来源、“核心”只在各来源都是核心时保留），所以“截到所有来源都被截才丢这行”。
        /// 监听掩码、需要飞行、含核心按剩下的行重算；热量分项沿用原程序（无人机开火不积热，热量留母机，FGR-SIG-033）。没有剩下的行返回 null（= 只有基础攻击）。
        /// FG-GAP-173（EMG1-FW-01）：合并行按 <see cref="EmgProgramResult.MergeSources"/> 记下的各来源重算，剥离核心 / 截断后只留常规 / 未截来源那份。
        /// </summary>
        private static bool ReMerge(ref EmgInstr x, List<EmgMergeSource> srcs, int cut, bool noCore)
        {
            EmgInstrFlags all = EmgInstrFlags.None, keep = EmgInstrFlags.None;
            bool any = false, allCore = true;
            float amount = 0f, p2 = 0f;
            int ord = int.MaxValue;
            foreach (EmgMergeSource m in srcs)
            {
                all |= m.Flags;
                if (m.Ordinal > cut || (noCore && m.Core))
                {
                    continue;
                }
                if (!any || m.Amount > amount)
                {
                    amount = m.Amount;
                    p2 = m.Param2;
                }
                any = true;
                keep |= m.Flags;
                allCore &= m.Core;
                ord = Math.Min(ord, m.Ordinal);
            }
            if (!any)
            {
                return false;
            }
            x.Amount = amount;
            x.Param2 = p2;
            x.Ordinal = (byte)ord;
            x.Flags = (x.Flags & ~all) | keep;
            if (!allCore)
            {
                x.Flags &= ~EmgInstrFlags.Core;
            }
            return true;
        }

        public static EmgProgramResult Truncate(EmgProgramResult r, int k, bool noCore)
        {
            List<EmgProgramStep> steps = FlashSteps(r, noCore);
            if (r == null || r.IsEmpty || k <= 0 || steps.Count == 0)
            {
                return null;
            }
            int cut = steps[Math.Min(k, steps.Count) - 1].Ordinal;
            EmgCompiled c = EmgCatalog.Current;
            EnsureIndex(c);
            var t = new EmgProgramResult
            {
                RootCapacity = r.RootCapacity, BaseGen = r.BaseGen, ContentVersion = r.ContentVersion, Carrier = r.Carrier, LobAllowed = r.LobAllowed, HealOnly = r.HealOnly,
                HeatInstr = r.HeatInstr, HeatDecl = r.HeatDecl, HeatTrigger = r.HeatTrigger, HeatChildren = r.HeatChildren, HeatOverflowInstr = r.HeatOverflowInstr,
                HeatDraw = r.HeatDraw, ChildPayloads = r.ChildPayloads, HeatMeta = r.HeatMeta,
            };
            t.Steps.AddRange(r.Steps);
            t.LegacyOwners.AddRange(r.LegacyOwners);
            uint listen = 0u;
            bool flight = false, core = false;
            for (int j = 0; j < r.KernelNodes.Count; j++)
            {
                EmgNode n = r.KernelNodes[j];
                int first = t.KernelInstrs.Count;
                for (int i = n.FirstInstr; i < n.FirstInstr + n.InstrCount && i < r.KernelInstrs.Count; i++)
                {
                    EmgInstr x = r.KernelInstrs[i];
                    if (r.MergeSources.TryGetValue(i, out List<EmgMergeSource> srcs))
                    {
                        // FG-GAP-173：合并行按剩下的来源重算（幅度取剩下来源的最大、偏好按位并、“核心”只在剩下的都是核心时保留、序号取剩下来源的最早）；一个都不剩才丢这行
                        if (!ReMerge(ref x, srcs, cut, noCore))
                        {
                            continue;
                        }
                    }
                    else if (x.Ordinal > cut || (noCore && (x.Flags & EmgInstrFlags.Core) != 0))
                    {
                        continue;
                    }
                    t.KernelInstrs.Add(x);
                    bool write = x.Role > EmgConst.RoleLastRead;
                    if (!write && IsEventPort(x.Port))
                    {
                        listen |= 1u << (x.Port - EventPortBase);
                    }
                    flight |= j == 0 && write && (x.Flags & EmgInstrFlags.Overflow) == 0 && PortIds.TryGetValue(x.Port, out string pid) && Array.IndexOf(FlightMods, pid) >= 0;
                    core |= (x.Flags & EmgInstrFlags.Core) != 0;
                }
                n.FirstInstr = first;
                n.InstrCount = (byte)(t.KernelInstrs.Count - first);
                t.KernelNodes.Add(n);
            }
            if (t.KernelInstrs.Count == 0)
            {
                return null;
            }
            // EMG3-FUSE-01：落点接线尾段按 B 的序号截（A 的序号总在 B 之前）
            bool anchored = false;
            if (r.KernelNodes.Count > 0)
            {
                EmgNode lastNode = r.KernelNodes[r.KernelNodes.Count - 1];
                for (int i = lastNode.FirstInstr + lastNode.InstrCount; i < r.KernelInstrs.Count; i++)
                {
                    EmgInstr x = r.KernelInstrs[i];
                    if (x.Ordinal > cut || (noCore && (x.Flags & EmgInstrFlags.Core) != 0))
                    {
                        continue;
                    }
                    t.KernelInstrs.Add(x);
                    anchored = true;
                }
            }
            // 根节点的触发读端还在、它放出的子节点读的是“飞行中”事件 → 仍需飞行（与编译器同一口径）
            foreach (EmgNode n in t.KernelNodes)
            {
                flight |= n.Parent == 0 && NeedsFlightEvent(n.TrigEvent) && (listen & (1u << (int)n.TrigEvent)) != 0;
            }
            t.Program = new EmgProgram
            {
                Flags = (r.Program.Flags & ~(EmgProgramFlags.NeedsFlight | EmgProgramFlags.HasCore | EmgProgramFlags.HasAnchor))
                        | (flight ? EmgProgramFlags.NeedsFlight : EmgProgramFlags.None) | (core ? EmgProgramFlags.HasCore : EmgProgramFlags.None)
                        | (anchored ? EmgProgramFlags.HasAnchor : EmgProgramFlags.None) | (r.BaseGen > 0 ? EmgProgramFlags.IsCopy : EmgProgramFlags.None),
                ListenMask = listen, HeatPerShot = r.Program.HeatPerShot,
                // EMG3-FUSE-01：刷写截断 / 剥核心的变体按留下的指令重算线索掩码（B 被截掉就不再算同现）
                ClueMask = r.Program.ClueMask == 0u ? 0u : ClueMaskOf(steps.GetRange(0, Math.Min(k, steps.Count)), c),
            };
            t.HeatAnchor = r.HeatAnchor;
            if (r.Reverse != null)
            {
                // EMG3-E2E-01（DEBT-EMG3META01-02 ②，FGR-EMG-040）：刷写变体按留下的步骤重编逆序树（逆序树的序号是逆序后的位置，不能按原序号截），
                // 登记时成对进程序表（CombatSite.RegisterProgram），刷写态下 [逆序编译] 照常按积热切树；截掉了反转本身 = 没有逆序树
                var kept = new EmgProgramResult { RootCapacity = r.RootCapacity, BaseGen = r.BaseGen, ContentVersion = r.ContentVersion, Carrier = r.Carrier, LobAllowed = r.LobAllowed, HealOnly = r.HealOnly };
                foreach (EmgProgramStep s in r.Steps)
                {
                    if (s.Ordinal <= cut && !(noCore && s.Core))
                    {
                        kept.Steps.Add(s);
                    }
                }
                t.Reverse = BuildReverse(kept, c);
            }
            return t;
        }

        /// <summary>前 <paramref name="k"/> 条之外、整条固件的全部程序位置都被截掉的固件（变体武器的旧读法字段一并去掉：截到 0 条 = 只有基础攻击）。</summary>
        public static HashSet<string> FlashDroppedFirmware(EmgProgramResult r, int k, bool noCore)
        {
            var kept = new HashSet<string>(StringComparer.Ordinal);
            var all = new HashSet<string>(StringComparer.Ordinal);
            List<EmgProgramStep> steps = FlashSteps(r, false);
            List<EmgProgramStep> keep = FlashSteps(r, noCore);
            for (int i = 0; i < keep.Count && i < k; i++)
            {
                kept.Add(keep[i].FirmwareId);
                kept.Add(keep[i].OwnerId);
            }
            foreach (EmgProgramStep s in steps)
            {
                all.Add(s.FirmwareId);
                all.Add(s.OwnerId);
            }
            all.ExceptWith(kept);
            return all;
        }

        /// <summary>
        /// 编译预览的刷写预判（FGR-EMG-038 原因 emg.compile.flash_truncated）：载体一次放 <paramref name="drones"/> 架、母机带宽 <paramref name="bandwidth"/> 条时，
        /// 后放出的那几架会从末尾少装哪些指令——每条至少被一架截掉的指令记一条原因（Node = −1，Ordinal = 程序序）。运行时按真实在场数截断（内核），这里只是提醒。
        /// </summary>
        public static void AddFlashForecast(EmgProgramResult r, int drones, int bandwidth)
        {
            List<EmgProgramStep> steps = FlashSteps(r, false);
            if (r == null || r.IsEmpty || drones <= 0 || steps.Count == 0)
            {
                return;
            }
            int used = 0, worst = steps.Count;
            for (int i = 0; i < drones; i++)
            {
                int grant = Math.Max(0, Math.Min(steps.Count, bandwidth - used));
                used += grant;
                worst = Math.Min(worst, grant);
            }
            for (int i = worst; i < steps.Count; i++)
            {
                r.Notes.Add(new EmgCompileNote { Key = EmgProgramTreeText.NoteFlashTruncated, Ordinal = steps[i].Ordinal, Node = -1 });
            }
        }

        /// <summary>EMG1-GRM-01：程序里写了修饰端口（grm.mod.*）的固件（切到声明轨后，这些固件的旧读法修饰字段不再结算）。</summary>
        public static HashSet<string> ModifierFirmware(EmgProgramResult r)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (r == null || r.IsEmpty)
            {
                return set;
            }
            EmgCompiled c = EmgCatalog.Current;
            EnsureIndex(c);
            foreach (EmgProgramStep s in r.Steps)
            {
                foreach (int di in s.Decls)
                {
                    if (c.Decls[di].Dir == EmgDirs.Write && PortIds.TryGetValue(c.Decls[di].Port, out string pid) && IsMod(pid))
                    {
                        set.Add(s.FirmwareId);
                        break;
                    }
                }
            }
            // EMG1-FW-01（DEBT-EMG1GRM01-05 已关）：子区 / 脉冲子节点上的修饰、任何子节点上的数值行都由子载荷自己结算（ChildLegacyVerb + 内核 CombatLogic.ChildMods），
            // 写修饰端口的固件一律切到声明轨，不再保留旧读法字段。
            return set;
        }

        /// <summary><see cref="LegacySkip"/> 用的伪字段名：(固件, TagsField) → true 时这条固件的旧标签不进读法（CarrierReadings.Build 同名约定）。</summary>
        public const string TagsField = "#tags";

        /// <summary>
        /// EMG1-FW-01（FG19A 第 0.3 节口径 3、第 1.5 节、第 3.2 节“阶段 1 过渡”列）：效果写端口在阶段 1 的过渡实现 = 该固件自己的旧读法字段。
        /// 按端口（族）给，不按固件 ID（FGR-EMG-013）：物质排放 → 驻留区 / 拖尾；热 → 过热爆圈；动能 → 余震；无人机 → 伴飞；追射 → 回波（EMG1-FIX-01 起只用于子节点，根节点由内核真补射）；
        /// 处决 / 腐蚀 → 爆开；导电连通 → 减速网。其余端口没有过渡字段（新动词由内核执行，或承接阶段才有实现）。
        /// </summary>
        public static string[] TransitionFields(string port)
        {
            if (port == null)
            {
                return Array.Empty<string>();
            }
            if (port.StartsWith("mat.", StringComparison.Ordinal))
            {
                return MatterTransition;
            }
            return port switch
            {
                // EMG2-PHS-01：阶段 ≥ 2 注热由内核落点结算进结构积热累积量（命中注能要物质层，EMG2-RULE-01 接通），过渡字段 heatburst 退役（DEBT-EMG1FW01-03）；
                // emg.stage = 1 的回滚对照里内核不注能，仍保留 heatburst（不让过载在阶段 1 变成死指令）
                "en.heat" => Array.Empty<string>(), // EMG4-CLEAN-01：emg.stage 只保留 4，阶段 1 回滚对照的 heatburst 过渡删掉
                "en.kinetic" => new[] { "echo" },
                "ent.drone" => new[] { "inherit" },
                "ent.projectile" => new[] { "feedback" },
                "prop.integrity" or "phs.corrode" => new[] { "explode" },
                // EMG2-RECL-01：阶段 ≥ 2 桥接由内核拉真导线（CombatLogic.EmgLink），旧减速网过渡字段退役；emg.stage = 1 的回滚对照仍用减速网
                "prop.conductive" => Array.Empty<string>(), // EMG4-CLEAN-01：阶段 1 回滚对照的减速网过渡删掉
                _ => Array.Empty<string>(),
            };
        }

        private static readonly string[] MatterTransition = { "linger", "trail" };

        /// <summary>
        /// EMG2-PHS-01（承接 DEBT-EMG1FW01-03 注入类，FGR-EMG-019 单轨）：能量注入写端由内核在落点写累积量（结构积热 → 熔穿、电荷 → 麻痹、磁化度 → 磁化）后，
        /// 同一写端原先在本机出手上挂的旧标签退役。按端口给，不按固件 ID（FGR-EMG-013）。
        /// 电击 Shock 随 EMG2-REACT-02 退役（短路 / 电解 / 绝缘都读注入量，不读标签）。
        /// </summary>
        public static string[] RetiredTags(string port) => port switch
        {
            "en.heat" => new[] { "Fire" },
            "en.elec" => new[] { "Charged", "Shock" }, // EMG2-REACT-02：电击 = 一次 en.elec 注入（FG19B §3.1 位 4、§12 #3）
            "en.mag" => new[] { "Charged", "Magnet" },
            _ => Array.Empty<string>(),
        };


        /// <summary><see cref="LegacySkip"/> 的逐标签伪字段：(固件, TagField(tag)) → true 时这个旧标签不进读法（CarrierReadings.Build 同名约定）。</summary>
        public static string TagField(string tag) => TagsField + ":" + tag;

        /// <summary>
        /// 固件此刻退役的旧标签（与 <see cref="LegacySkip"/> 同口径的 owner 级视图）：走声明轨（<see cref="EmgCatalog.IsOwnerOnDeclTrack"/>）且声明里有能量注入写端时，
        /// 该端口的 <see cref="RetiredTags"/>。固件库 / 修复 / 反应提示 / 反制统计按它显示，不再说“挂燃烧”而实际不挂。
        /// </summary>
        public static HashSet<string> RetiredTagsOf(string firmwareId)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            EmgCompiled c = EmgCatalog.Current;
            if (firmwareId == null || !c.OwnerIndex.TryGetValue(firmwareId, out int oi) || !c.OwnerActive(oi))
            {
                return set;
            }
            EnsureIndex(c);
            foreach (EmgDeclRow d in c.Decls)
            {
                if (d.Owner == oi && d.Dir == EmgDirs.Write && PortIds.TryGetValue(d.Port, out string pid))
                {
                    set.UnionWith(RetiredTags(pid));
                }
            }
            return set;
        }

        /// <summary>此刻真正会挂上的旧标签 = 表里的标签去掉 <see cref="RetiredTagsOf"/>（玩家可见的标签列表用）。</summary>
        public static string[] LiveTagsOf(string firmwareId)
        {
            string[] tags = Signal.FirmwareKinds.TagsOf(firmwareId);
            HashSet<string> gone = tags.Length == 0 ? null : RetiredTagsOf(firmwareId);
            return gone == null || gone.Count == 0 ? tags : Array.FindAll(tags, t => !gone.Contains(t));
        }

        /// <summary>只改数字的端口 → 旧读法字段（FG19A 第 1.5 节：扩口 scale、广角 spread、装甲击穿 armor、电容蓄力 capacitor）。根节点上由 CombatSite.FoldProgramReadings 按旧字段折一次。</summary>
        public static string[] NumericLegacyFields(string port) => port switch
        {
            "grm.mod.size" => new[] { "scale", "spread" },
            "grm.mod.armor" => new[] { "armor" },
            "grm.mod.power" => new[] { "capacitor" },
            _ => Array.Empty<string>(),
        };

        /// <summary>
        /// EMG1-FW-01（FGR-EMG-019 同一 owner 只走一轨）：程序里的固件（声明已生效）不再按旧读法结算——全部旧字段与旧标签都跳过，只留下：
        /// ① 编译进去的效果写端的过渡字段与旧标签（<see cref="TransitionFields"/>，按端口，阶段 2 换斑块 / 传导时去掉）：根节点全部效果写端；
        /// 子节点上除物质（子载荷 emit 由内核执行）以外的效果写端（阶段 1 内核在子载荷上不执行，过渡口径作用在本机出手上）；
        /// （子节点上的修饰与数值行由子载荷自己结算，不再保留旧字段：DEBT-EMG1GRM01-05 已关。）
        /// 溢出 / 截断的指令不编译，它的固件旧字段全部跳过（不两轨、也不回退旧轨）。没进程序的固件（stageGate 未到、没有声明）照常走旧轨。
        /// </summary>
        public static Func<string, string, bool> LegacySkip(EmgProgramResult r)
        {
            if (r == null || r.IsEmpty)
            {
                return null;
            }
            EmgCompiled c = EmgCatalog.Current;
            EnsureIndex(c);
            var onDecl = new HashSet<string>(StringComparer.Ordinal);
            foreach (EmgProgramStep s in r.Steps)
            {
                onDecl.Add(s.FirmwareId);
            }
            var keep = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var retired = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            void Keep(string fw, string field)
            {
                if (!keep.TryGetValue(fw, out HashSet<string> set))
                {
                    keep[fw] = set = new HashSet<string>(StringComparer.Ordinal);
                }
                set.Add(field);
            }
            var beatOrds = new HashSet<int>();
            foreach (EmgNode n in r.KernelNodes)
            {
                // EMG3-TMP-01 审查修复（第 1 轮，FGR-EMG-019 单轨）：同节点同序号读 evt.beat 的效果写端由内核按拍执行（StepEmgBeat / EmgBeatKinetic），不再保留旧读法过渡字段（如谐振的 echo）
                beatOrds.Clear();
                for (int i = n.FirstInstr; i < n.FirstInstr + n.InstrCount && i < r.KernelInstrs.Count; i++)
                {
                    EmgInstr x = r.KernelInstrs[i];
                    if (x.Role <= EmgConst.RoleLastRead && (x.Flags & EmgInstrFlags.Overflow) == 0 && PortIds.TryGetValue(x.Port, out string bp) && bp == "evt.beat")
                    {
                        beatOrds.Add(x.Ordinal);
                    }
                }
                for (int i = n.FirstInstr; i < n.FirstInstr + n.InstrCount && i < r.KernelInstrs.Count; i++)
                {
                    EmgInstr x = r.KernelInstrs[i];
                    if (x.Role <= EmgConst.RoleLastRead || (x.Flags & (EmgInstrFlags.Overflow | EmgInstrFlags.Inherited)) != 0 || x.Ordinal >= r.Steps.Count
                        || !PortIds.TryGetValue(x.Port, out string pid))
                    {
                        continue;
                    }
                    string fw = r.Steps[x.Ordinal].FirmwareId;
                    EmgFamily fam = EmgCatalog.FamilyOf(pid);
                    // 审查修复（第 1 轮）：子节点上的效果写端，除物质排放（内核子载荷 emit 执行）外，阶段 1 内核在子载荷上还不执行——
                    // 与根节点同一过渡口径保留该固件的过渡字段与旧标签（作用在本机出手上），否则 [触发, 效果] 的第二条成了死指令（FGR-EMG-015 规则二）。
                    // EMG2-PHS-01（承接 DEBT-EMG1FW01-03 注入类）：en.heat / en.elec / en.mag 的注入由内核在落点结算（EmgHitInject：根节点按本机命中、子节点按子弹药 / 子区落点），
                    // 写进结构积热 / 电荷 / 磁化度累积量并由相态表现（熔穿 / 麻痹 / 磁化）——过渡字段 heatburst 退役（TransitionFields("en.heat") 为空），
                    // 该写端的旧标签（Fire / Charged / Magnet，RetiredTags）也不再挂在本机出手上：同一写端只走内核一轨（FGR-EMG-019）。
                    foreach (string t in RetiredTags(pid))
                    {
                        if (!retired.TryGetValue(fw, out HashSet<string> rs))
                        {
                            retired[fw] = rs = new HashSet<string>(StringComparer.Ordinal);
                        }
                        rs.Add(t);
                    }
                    // EMG2-REACT-02（DEBT-EMG1FW01-03 余部）：子节点上的效果写端全部由内核在落点结算（CombatLogic.EmgChildHitWrite / EmgChildWrites / 排放），本机出手上不再留过渡字段与旧标签（FGR-EMG-019 单轨）；
                    // emg.stage = 1 的回滚对照里内核不注能，仍按旧过渡口径
                    if (fam != EmgFamily.Grammar && fam != EmgFamily.Meta && n.Parent < 0)
                    {
                        // EMG1-FIX-01：根节点上由内核直接执行的写端（RootKernel，如追射 ent.projectile）不再保留旧读法过渡字段（否则旧回波与真补射两轨同时结算，FGR-EMG-019）
                        bool kernelRoot = (n.Parent < 0 && (x.Flags & EmgInstrFlags.RootKernel) != 0) || beatOrds.Contains(x.Ordinal);
                        foreach (string f in kernelRoot ? Array.Empty<string>() : TransitionFields(pid))
                        {
                            // EMG4-FUSE-02：物质过渡字段只留这条指令的 owner 自己有的那个（混合芯片的读法是两父相加：燃迹的沿途排放改由内核执行后，漏油的驻留区不能把燃迹的 trail 也留下来）
                            if (fam != EmgFamily.Matter || OwnerHasField(r.Steps[x.Ordinal].OwnerId, f))
                            {
                                Keep(fw, f);
                            }
                        }
                        Keep(fw, TagsField);
                    }
                }
            }
            string tagPrefix = TagsField + ":";
            return (fw, field) =>
            {
                if (fw == null || !onDecl.Contains(fw))
                {
                    // EMG4-CLEAN-01（FGR-EMG-019：stageGate = 4 之外没有旧轨）：没进程序的固件（冷却中的核心固件等）不按读法字段结算；旧标签照旧
                    return fw != null && !field.StartsWith(TagsField, StringComparison.Ordinal);
                }
                if (field.StartsWith(tagPrefix, StringComparison.Ordinal))
                {
                    return retired.TryGetValue(fw, out HashSet<string> rs) && rs.Contains(field.Substring(tagPrefix.Length));
                }
                return !(keep.TryGetValue(fw, out HashSet<string> k) && k.Contains(field));
            };
        }

        /// <summary>根节点上写某个只改数字端口（<paramref name="port"/>）且已编译的固件（同一条装两次只算一次）：数值折叠只汇总这些固件的旧字段。</summary>
        public static HashSet<string> RootWriters(EmgProgramResult r, ushort port)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (r == null || r.IsEmpty || r.Nodes.Count == 0)
            {
                return set;
            }
            EmgCompiled c = EmgCatalog.Current;
            EnsureIndex(c);
            string pid = PortIds.TryGetValue(port, out string id) ? id : string.Empty;
            // 根节点自有（非继承）、未溢出的指令里写这个端口的（同键幂等后内核只留 1 行，但旧字段按各条固件相加，与 Build 同一累加）
            foreach (EmgTreeEntry e in r.Nodes[0].Entries)
            {
                EmgProgramStep s = r.Steps[e.Ordinal];
                if (!e.Inherited && !s.Overflow && HasWrite(c, s, pid))
                {
                    set.Add(s.FirmwareId);
                }
            }
            return set;
        }

        /// <param name="rootCapacity">≤ 0 = 按公式 C0 = 机器固件槽数 + 接入口配额；自检可给别的值验证“配额变了公式不变”（FGR-EMG-039）。</param>
        /// <param name="lobAllowed">主组件可抛射（作战组件表 flightLob）；false 时 lob 动词不执行并给原因 <see cref="NoteLobStraight"/>（光束：曲射只留落点扬尘）。</param>
        /// <param name="baseGen">EMG4-HIJ-02：根节点代号（1 = 镜像复制品，见 <see cref="CompileCopy"/>）；容量公式不变 C(gen) = max(1, C0 / 2^gen)。</param>
        /// <param name="innateOwner">EMG4-LOOT-01 修复轮 1：主组件 ID——它自己的声明里有程序写端时编成一条内置指令（<see cref="AddInnate"/>）；null = 没有。</param>
        public static EmgProgramResult Compile(IReadOnlyList<string> uplinkFirmwareIds, IReadOnlyList<string> localFirmwareIds, FirmwareCarrier carrier, int rootCapacity = 0,
            bool lobAllowed = true, ICollection<string> skipUplink = null, bool healOnly = false, int baseGen = 0, string innateOwner = null)
        {
            EmgCompiled c = EmgCatalog.Current;
            EnsureIndex(c);
            var r = new EmgProgramResult
            {
                RootCapacity = rootCapacity > 0 ? rootCapacity : BlueprintCircuitBoard.FirmwareSlotCount + UplinkCompiler.Quota,
                BaseGen = Math.Max(0, Math.Min(EmgConst.MaxGen, baseGen)),
                ContentVersion = CampaignSaveService.CurrentContentVersion,
                Carrier = (byte)carrier,
                LobAllowed = lobAllowed,
                HealOnly = healOnly,
            };
            AddSlots(r, c, uplinkFirmwareIds, true, skipUplink);
            AddSlots(r, c, localFirmwareIds, false, null);
            AddInnate(r, c, innateOwner);
            HealInertPass(r, c);
            if (r.IsEmpty)
            {
                return r; // 全空程序：武器照常基础攻击，不登记程序（Program = −1）
            }
            BuildTree(r);
            HealInertPass(r, c); // 触发器全部溢出（根节点容量放不下）时程序其实没有子载荷节点：建树后再判一次（#276，触发器之前已判过的不重复）
            Inherit(r, c);
            Emit(r, c);
            MetaPass(r, c);
            TempoPass(r);
            r.Reverse = BuildReverse(r, c);
            if (r.Reverse != null)
            {
                // 两棵树都计入编译签名（FGR-EMG-040；FG19A 申请 A7）
                r.Signature = r.ContentVersion + "-" + Fnv64(Combat.CombatSite.ProgramKey(r.Program, r.KernelNodes, r.KernelInstrs) + "|"
                                                             + Combat.CombatSite.ProgramKey(r.Reverse.Program, r.Reverse.KernelNodes, r.Reverse.KernelInstrs)).ToString("X16");
            }
            return r;
        }

        /// <summary>
        /// EMG4-HIJ-02（FGR-EMG-164 / 036）：持有型截断——镜像复制品按剩余带宽只持有前 <paramref name="k"/> 条：<see cref="Truncate"/> 之后把第 k 条之后的步骤（副本）记为溢出，
        /// 这样 <see cref="FlashSteps"/>、刷写变体表全长与内核“复制品占母机带宽”都按 k 条算。k ≤ 0 返回 null（= 只有基础攻击）。
        /// </summary>
        public static EmgProgramResult TruncateHeld(EmgProgramResult r, int k)
        {
            EmgProgramResult t = Truncate(r, k, false);
            List<EmgProgramStep> kept = t != null ? FlashSteps(t, false) : null;
            if (kept == null || kept.Count == 0)
            {
                return null;
            }
            int cut = kept[Math.Min(k, kept.Count) - 1].Ordinal;
            for (int i = 0; i < t.Steps.Count; i++)
            {
                if (t.Steps[i].Ordinal > cut && !t.Steps[i].Overflow && !t.Steps[i].Builtin && !t.Steps[i].Innate)
                {
                    EmgProgramStep c = t.Steps[i].CloneStep();
                    c.Overflow = true;
                    t.Steps[i] = c;
                }
            }
            return t;
        }

        /// <summary>镜像复制品不能再被复制（FGR-EMG-164 代际）的原因键。</summary>
        public const string NoteCopyRefused = "emg.mirror.refuse_copy";

        /// <summary>
        /// EMG4-HIJ-02（FG19 FGR-EMG-164；FG09 镜像体）：镜像复制——把玩家最近一次发动的固件按“本地版本”编成复制品：
        /// 1 代程序（根代号 1、容量 C(1)，现值 2），混合固件照常展开为 2 条后再受容量约束；复制触发器时它放出的子载荷是 2 代（容量 C(2)），
        /// 2 代里的触发器“代际已满”——子载荷按容量截断。来源本身就是复制品（<paramref name="sourceIsCopy"/>）时拒绝（返回 null，<paramref name="refuseKey"/> = <see cref="NoteCopyRefused"/>）。
        /// 占镜像体刷写带宽由 <see cref="Combat.CombatSite.MirrorCopy"/> 截断并登记（内核 FlashSelfCopy 计入母机占用）。只按端口与位置编，不认内容 ID（FGR-EMG-013）。
        /// </summary>
        /// <param name="p">镜像体的预览：主组件 = 镜像体自己的作战组件（载体），机器固件 = 复制来的固件（本地版本，不插接入口，见 <see cref="Combat.CombatSite.EnemyPreview"/>）。</param>
        public static EmgProgramResult CompileCopy(BlueprintCircuitPreview p, bool sourceIsCopy, out string refuseKey)
        {
            refuseKey = sourceIsCopy ? NoteCopyRefused : null;
            return sourceIsCopy ? null : Compile(p, null, 1);
        }

        // ── 6. 节拍（EMG3-TMP-01；FG19 FGR-EMG-171 / 172）──

        /// <summary>
        /// 节拍指令的周期与相位落进内核行（预览与内核共用这一份，热更层不另算拍序）：读 tmp.beat 的行幅度 = 周期（取整，≥ 1），param2 = 相位——
        /// 声明 param2 &gt; 0 是显式相位（对周期取模），0 = 缺省相位 = 程序序号 % 周期（FGR-EMG-171“玩家调槽位顺序即调相位”）。
        /// 程序有节拍读端（tmp.beat / tmp.sync）时子节点记放出它的触发器序号（MetaMask，与元程序同一口径），内核按拍只放出本拍执行的那条触发器的子载荷；
        /// 程序有 chs.* condition 时同样记（DEBT-EMG3CHS01-02：内核入队时按出手者门控放出它的触发器）。O(指令数 + 节点数)。
        /// </summary>
        private static void TempoPass(EmgProgramResult r)
        {
            bool any = false;
            for (int i = 0; i < r.KernelInstrs.Count; i++)
            {
                EmgInstr x = r.KernelInstrs[i];
                if (x.Role > EmgConst.RoleLastRead || (x.Port != EmgTempoConst.PortBeat && x.Port != EmgTempoConst.PortSync))
                {
                    continue;
                }
                any = true;
                if (x.Port == EmgTempoConst.PortBeat)
                {
                    int period = Math.Max(1, (int)Math.Round(x.Amount));
                    x.Amount = period;
                    x.Param2 = x.Param2 > 0f ? (int)Math.Round(x.Param2) % period : InstrPosition(r, x.Ordinal) % period;
                    r.KernelInstrs[i] = x;
                }
            }
            // DEBT-EMG3CHS01-02：读 chs.* condition 的程序同样记（内核入队时按出手者、按放出它的触发器门控子载荷，EmgQueueChildren）；现有正式固件只有元程序 [若离地] 读它，没有元程序 / 节拍 / chs 的程序逐字不变
            for (int i = 0; i < r.KernelInstrs.Count && !any; i++)
            {
                any = r.KernelInstrs[i].Role == EmgConst.RoleCondition && r.KernelInstrs[i].Port >= EmgConst.PortChsGround && r.KernelInstrs[i].Port <= EmgConst.PortChsWater;
            }
            if (!any)
            {
                return;
            }
            for (int j = 1; j < r.KernelNodes.Count && j < r.Nodes.Count; j++)
            {
                EmgNode kn = r.KernelNodes[j];
                int t = r.Nodes[j].TriggerOrdinal;
                kn.MetaMask = (byte)(t >= 0 && t < 255 ? t + 1 : 0);
                r.KernelNodes[j] = kn;
            }
            r.Signature = r.ContentVersion + "-" + Fnv64(Combat.CombatSite.ProgramKey(r.Program, r.KernelNodes, r.KernelInstrs)).ToString("X16");
        }

        /// <summary>EMG3-FUSE-01：程序序号对应的“指令在程序里的序号”（FGR-EMG-171 缺省相位）——接线（内置或独立）只是 A、B 之间的连接，不计：
        /// 熔合芯片与 [A, 接线, B] 的相位相同（FGT-EMG-054 逐字节等价），也与 [A, B] 相同（混合固件展开为 2 条，FGR-EMG-044）。</summary>
        internal static int InstrPosition(EmgProgramResult r, int ordinal)
        {
            int n = ordinal;
            for (int i = 0; i < ordinal && i < r.Steps.Count; i++)
            {
                if (r.Steps[i].Kind == EmgStepKind.Anchor)
                {
                    n--;
                }
            }
            return n;
        }

        /// <summary>预览用：这条指令在节点里读 tmp.beat 的周期与相位（编译结果），没有返回 false。</summary>
        public static bool BeatOf(EmgProgramResult r, int node, int ordinal, out int period, out int phase)
        {
            period = phase = 0;
            if (r == null || node < 0 || node >= r.KernelNodes.Count)
            {
                return false;
            }
            EmgNode n = r.KernelNodes[node];
            for (int i = n.FirstInstr; i < n.FirstInstr + n.InstrCount && i < r.KernelInstrs.Count; i++)
            {
                EmgInstr x = r.KernelInstrs[i];
                if (x.Ordinal == ordinal && x.Port == EmgTempoConst.PortBeat && x.Role <= EmgConst.RoleLastRead)
                {
                    period = (int)Math.Round(x.Amount);
                    phase = (int)Math.Round(x.Param2);
                    return true;
                }
            }
            return false;
        }

        /// <summary>预览用：这条指令在节点里读 tmp.sync（相位对齐的拍上执行）。</summary>
        /// <summary>
        /// EMG3-SQD-01（FGR-EMG-132）：这条指令“可被中继转发”——车上有中继（<see cref="EmgProgramResult.Relay"/>）、在根节点（转发按执行方的根节点执行）、
        /// 有 role = trigger 且端口属于白名单（evt.hit / kill / marked / hurt / bounce / pierce）的读端。与内核 CombatLogic.EmgRootTrigGate 同口径。
        /// </summary>
        public static bool RelayedOf(EmgProgramResult r, int node, int ordinal)
        {
            if (r == null || !r.Relay || node != 0 || r.KernelNodes.Count == 0)
            {
                return false;
            }
            EmgNode n = r.KernelNodes[0];
            for (int i = n.FirstInstr; i < n.FirstInstr + n.InstrCount && i < r.KernelInstrs.Count; i++)
            {
                EmgInstr x = r.KernelInstrs[i];
                if (x.Ordinal == ordinal && x.Role == EmgConst.RoleTrigger && (x.Flags & EmgInstrFlags.Overflow) == 0 && x.Port > EventPortBase
                    && EmgSquadConst.Relayable((EmgEventKind)(x.Port - EventPortBase)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// EMG3-FW-02（FGR-EMG-133；卡片负向“没有中继时写 evt.ping 的固件无友机接收，预览写原因，不报错”）：这条指令写 evt.ping（<paramref name="send"/> = true）或在根节点以 trigger 收听
        /// evt.ping（false），<paramref name="channel"/> = 频道。子节点里的收听不会被触发（脉冲只按接收方根节点执行），返回 true 且 <paramref name="rootOnly"/> = true。与内核 EmgPingOut / EmgRootTrigGate 同口径。
        /// </summary>
        /// <summary>EMG4-FW-01（FG19A N40）：这一条指令是劫持中继（读 hij.hijacked condition 的指令行在内核行里，且整个程序带 HijackFlash）。只在预览重建时调用。</summary>
        public static bool HijackFlashOf(EmgProgramResult r, int ordinal)
        {
            if (!HasHijackFlash(r))
            {
                return false;
            }
            foreach (EmgInstr x in r.KernelInstrs)
            {
                if (x.Ordinal == ordinal && x.Port == EmgConst.PortHijacked && x.Role == EmgConst.RoleCondition && (x.Flags & EmgInstrFlags.Overflow) == 0)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool PingOf(EmgProgramResult r, int node, int ordinal, out bool send, out int channel, out bool rootOnly)
        {
            send = rootOnly = false;
            channel = 0;
            if (r == null || node < 0 || node >= r.KernelNodes.Count)
            {
                return false;
            }
            ushort ping = (ushort)(EventPortBase + (int)EmgEventKind.Ping);
            EmgNode n = r.KernelNodes[node];
            for (int i = n.FirstInstr; i < n.FirstInstr + n.InstrCount && i < r.KernelInstrs.Count; i++)
            {
                EmgInstr x = r.KernelInstrs[i];
                if (x.Ordinal != ordinal || x.Port != ping || (x.Flags & EmgInstrFlags.Overflow) != 0)
                {
                    continue;
                }
                channel = EmgSquadConst.ChannelOf(x.Amount);
                send = x.Role > EmgConst.RoleLastRead;
                rootOnly = !send && node != 0;
                return send || x.Role == EmgConst.RoleTrigger;
            }
            return false;
        }

        public static bool SyncOf(EmgProgramResult r, int node, int ordinal)
        {
            if (r == null || node < 0 || node >= r.KernelNodes.Count)
            {
                return false;
            }
            EmgNode n = r.KernelNodes[node];
            for (int i = n.FirstInstr; i < n.FirstInstr + n.InstrCount && i < r.KernelInstrs.Count; i++)
            {
                EmgInstr x = r.KernelInstrs[i];
                if (x.Ordinal == ordinal && x.Port == EmgTempoConst.PortSync && x.Role <= EmgConst.RoleLastRead)
                {
                    return true;
                }
            }
            return false;
        }

        // ── 5. 元程序（EMG3-META-01；FG19 FGR-EMG-040～043）──

        private static int MetaKindOf(EmgCompiled c, EmgProgramStep s)
        {
            foreach (int di in s.Decls)
            {
                EmgDeclRow d = c.Decls[di];
                int k = d.Dir == EmgDirs.Write ? EmgMetaConst.KindOf(d.Port) : -1;
                if (k >= 0)
                {
                    return k;
                }
            }
            return -1;
        }

        private static float StepHeat(EmgCompiled c, EmgProgramStep s)
        {
            float h = s.TableHeat;
            foreach (int di in s.Decls)
            {
                h += Math.Max(0f, c.Decls[di].Heat);
            }
            return h;
        }

        /// <summary>
        /// 元程序的编译期部分（预览与内核共用这一份结果，FGR-EMG-038 / IC-REQ-010）：每条元程序的下游段（同节点、序号更大的非元程序指令）、每次结算执行次数（复制各层 ×2 相乘，
        /// 超过 emg.meta.max_exec 的那一层不生效、写原因）、一条链累计延后（祖先节点里罩住放出本节点的触发器的延后 + 本节点内层层延后，超过 emg.meta.defer_total_max 的不执行、写原因）；
        /// 结果写进内核行：元程序写端行 A = 1 生效 / 0 超上限，子节点 MetaMask = 放出它的触发器序号 + 1，程序带 HasMeta。热量按 FGR-EMG-042 加 HeatMeta。O(节点数 × 指令数)。
        /// </summary>
        private static void MetaPass(EmgProgramResult r, EmgCompiled c)
        {
            bool any = false;
            foreach (EmgProgramStep s in r.Steps)
            {
                any |= !s.Overflow && s.Kind == EmgStepKind.Meta;
            }
            if (!any)
            {
                return;
            }
            int maxExec = Math.Max(1, TuningInt("emg.meta.max_exec", EmgMetaConst.DefaultMaxExec));
            int loopMax = Math.Max(1, TuningInt("emg.meta.loop_max", EmgMetaConst.DefaultLoopMax));
            float deferSec = Tuning("emg.meta.defer_seconds", EmgMetaConst.DefaultDefer), deferMax = Tuning("emg.meta.defer_total_max", EmgMetaConst.DefaultDeferMax);
            float loopFactor = Tuning("emg.heat.loop_factor", 2f), trigGen = Tuning("emg.heat.trigger_gen", 2f), perChild = Tuning("emg.heat.per_child", 1f);
            int count = r.Nodes.Count;
            var mult = new int[count];
            var subHeat = new float[count];
            var deferPath = new int[count];
            foreach (EmgTreeNode n in r.Nodes)
            {
                mult[n.Index] = n.Parent < 0 ? 1 : n.TrigCount * (n.Parent > 0 ? mult[n.Parent] : 1);
            }
            for (int i = count - 1; i >= 0; i--)
            {
                // 子树热量（父节点总在子节点之前建，倒序即自底向上）：自有指令热量 + 子节点的触发节点 / 子载荷份数热量（与 Emit 同一口径）
                EmgTreeNode n = r.Nodes[i];
                float h = n.Parent >= 0 ? trigGen * n.Gen + perChild * mult[i] : 0f;
                foreach (EmgTreeEntry e in n.Entries)
                {
                    h += e.Inherited ? 0f : StepHeat(c, r.Steps[e.Ordinal]);
                }
                foreach (EmgTreeNode ch in r.Nodes)
                {
                    h += ch.Parent == i ? subHeat[ch.Index] : 0f;
                }
                subHeat[i] = h;
            }
            var metas = new List<int>(4);
            var units = new List<int>(4);
            float extra = 0f;
            foreach (EmgTreeNode n in r.Nodes)
            {
                metas.Clear();
                units.Clear();
                foreach (EmgTreeEntry e in n.Entries)
                {
                    if (!e.Inherited)
                    {
                        (r.Steps[e.Ordinal].Kind == EmgStepKind.Meta ? metas : units).Add(e.Ordinal);
                    }
                }
                metas.Sort();
                units.Sort();
                int repeatValid = 0, deferIn = 0;
                var validMeta = new Dictionary<int, bool>(metas.Count);
                foreach (int m in metas)
                {
                    int kind = MetaKindOf(c, r.Steps[m]);
                    var info = new EmgMetaInfo { Node = n.Index, Ordinal = m, Kind = kind, Down = units.FindAll(u => u > m), Exec = 1, Valid = true };
                    if (info.Down.Count == 0)
                    {
                        r.Notes.Add(new EmgCompileNote { Key = NoteMetaEmpty, Ordinal = m, Node = n.Index });
                    }
                    if (kind == EmgMetaConst.Repeat)
                    {
                        info.Valid = (1 << repeatValid) < maxExec; // 这一层还能多执行至少一次才生效；超出部分不执行（FGR-EMG-042）
                        repeatValid += info.Valid ? 1 : 0;
                        info.Exec = Math.Min(1 << repeatValid, maxExec);
                        if (!info.Valid)
                        {
                            r.Notes.Add(new EmgCompileNote { Key = NoteMetaExecCap, Ordinal = m, Node = n.Index });
                        }
                    }
                    else if (kind == EmgMetaConst.Defer)
                    {
                        deferIn++;
                        info.Delay = (deferPath[n.Index] + deferIn) * deferSec;
                        info.Valid = info.Delay <= deferMax + 1e-4f;
                        if (!info.Valid)
                        {
                            r.Notes.Add(new EmgCompileNote { Key = NoteMetaDeferCap, Ordinal = m, Node = n.Index });
                        }
                    }
                    else if (kind == EmgMetaConst.Loop)
                    {
                        info.Exec = loopMax;
                        info.Delay = Tuning("emg.meta.loop_period", EmgMetaConst.DefaultLoopPeriod); // 循环：Delay 记周期秒数（预览用）
                    }
                    validMeta[m] = info.Valid;
                    r.Metas.Add(info);
                    SetMetaRow(r, n.Index, m, info.Valid);
                }
                foreach (EmgTreeNode ch in r.Nodes)
                {
                    if (ch.Parent != n.Index)
                    {
                        continue;
                    }
                    int path = deferPath[n.Index];
                    foreach (int m in metas)
                    {
                        path += m < ch.TriggerOrdinal && MetaKindOf(c, r.Steps[m]) == EmgMetaConst.Defer ? 1 : 0;
                    }
                    deferPath[ch.Index] = path;
                }
                // 热量（FGR-EMG-042）：每个下游单位按“执行次数 × 循环系数 ÷ 随机段条数”加权，多出的部分计入每发
                foreach (int u in units)
                {
                    float f = 1f;
                    int kr = 0;
                    foreach (int m in metas)
                    {
                        if (m >= u)
                        {
                            continue;
                        }
                        int kind = MetaKindOf(c, r.Steps[m]);
                        kr += kind == EmgMetaConst.Repeat && validMeta[m] ? 1 : 0;
                        f *= kind == EmgMetaConst.Loop ? loopFactor : 1f;
                        if (kind == EmgMetaConst.Random)
                        {
                            f /= Math.Max(1, units.FindAll(x => x > m).Count);
                        }
                    }
                    f *= Math.Min(1 << Math.Min(kr, 6), maxExec);
                    float h = StepHeat(c, r.Steps[u]);
                    foreach (EmgTreeNode ch in r.Nodes)
                    {
                        h += ch.Parent == n.Index && ch.TriggerOrdinal == u ? subHeat[ch.Index] : 0f;
                    }
                    extra += h * (f - 1f);
                }
            }
            r.HeatMeta = extra;
            for (int j = 1; j < r.KernelNodes.Count && j < r.Nodes.Count; j++)
            {
                EmgNode kn = r.KernelNodes[j];
                int t = r.Nodes[j].TriggerOrdinal;
                kn.MetaMask = (byte)(t >= 0 && t < 255 ? t + 1 : 0);
                r.KernelNodes[j] = kn;
            }
            EmgProgram p = r.Program;
            p.Flags |= EmgProgramFlags.HasMeta;
            p.HeatPerShot = Math.Max(0f, r.HeatTotal);
            r.Program = p;
            r.Signature = r.ContentVersion + "-" + Fnv64(Combat.CombatSite.ProgramKey(r.Program, r.KernelNodes, r.KernelInstrs)).ToString("X16");
        }

        /// <summary>元程序写端行（内核按它判这一层是否生效）：A = 1 生效 / 0 超上限。</summary>
        private static void SetMetaRow(EmgProgramResult r, int node, int ordinal, bool valid)
        {
            if (node >= r.KernelNodes.Count)
            {
                return;
            }
            EmgNode kn = r.KernelNodes[node];
            for (int i = kn.FirstInstr; i < kn.FirstInstr + kn.InstrCount && i < r.KernelInstrs.Count; i++)
            {
                EmgInstr x = r.KernelInstrs[i];
                if (x.Ordinal == ordinal && x.Role > EmgConst.RoleLastRead && EmgMetaConst.KindOf(x.Port) >= 0)
                {
                    x.A = valid ? 1f : 0f;
                    r.KernelInstrs[i] = x;
                }
            }
        }

        /// <summary>
        /// 反转（FGR-EMG-040 / 041）：按程序位置从前往后做全部反转（每条反转把它后面的指令序列倒过来），对逆序序列重新做节点分配、继承、内核行与元程序；
        /// 反转之后的指令 fld.pull ↔ fld.push、回拉 → 前冲。没有生效的反转返回 null。
        /// </summary>
        private static EmgProgramResult BuildReverse(EmgProgramResult r, EmgCompiled c)
        {
            var order = new List<EmgProgramStep>(r.Steps);
            int firstRev = -1;
            for (int i = 0; i < order.Count; i++)
            {
                if (!order[i].Overflow && MetaKindOf(c, order[i]) == EmgMetaConst.Reverse)
                {
                    order.Reverse(i + 1, order.Count - i - 1);
                    firstRev = firstRev < 0 ? i : firstRev;
                }
            }
            if (firstRev < 0)
            {
                return null;
            }
            var t = new EmgProgramResult
            {
                RootCapacity = r.RootCapacity, BaseGen = r.BaseGen, ContentVersion = r.ContentVersion, Carrier = r.Carrier, LobAllowed = r.LobAllowed, HealOnly = r.HealOnly,
            };
            for (int i = 0; i < order.Count; i++)
            {
                EmgProgramStep s = order[i];
                var x = new EmgProgramStep
                {
                    Ordinal = i, Src = s.Ordinal, FirmwareId = s.FirmwareId, OwnerId = s.OwnerId, FromUplink = s.FromUplink, Core = s.Core, Promoted = s.Promoted, Kind = s.Kind,
                    TrigEvent = s.TrigEvent, TrigParam = s.TrigParam, TrigCount = s.TrigCount, EventPort = s.EventPort, TableHeat = s.TableHeat, Flip = i > firstRev,
                    Builtin = s.Builtin, Innate = s.Innate, AnchorKind = s.AnchorKind, // EMG3-FUSE-01：逆序树里接线照常按新序列的前后两条判
                };
                x.Decls.AddRange(s.Decls);
                t.Steps.Add(x);
            }
            BuildTree(t);
            Inherit(t, c);
            Emit(t, c);
            MetaPass(t, c);
            TempoPass(t); // 逆序树的缺省相位按逆序后的序号 % 周期（FGR-EMG-171）
            return t;
        }

        /// <summary>逆序树里的端口取反：fld.pull ↔ fld.push，grm.mod.return（回拉）→ grm.mod.pierce（前冲，穿过目标继续向前）。</summary>
        private static ushort FlipPort(ushort port)
        {
            string id = PortIds.TryGetValue(port, out string pid) ? pid : string.Empty;
            string to = id == "fld.pull" ? "fld.push" : id == "fld.push" ? "fld.pull" : id == "grm.mod.return" ? "grm.mod.pierce" : null;
            if (to != null)
            {
                foreach (KeyValuePair<ushort, string> kv in PortIds)
                {
                    if (kv.Value == to)
                    {
                        return kv.Key;
                    }
                }
            }
            return port;
        }

        /// <summary>原因的玩家文本（UX-01 的程序树预览也用它）。</summary>
        public static string NoteText(EmgProgramResult r, in EmgCompileNote n)
        {
            string name = n.Ordinal >= 0 && n.Ordinal < r.Steps.Count ? FirmwareKinds.DisplayName(r.Steps[n.Ordinal].OwnerId) ?? r.Steps[n.Ordinal].OwnerId : string.Empty;
            return GameText.Format(n.Key, name, n.Node, NoteLimit(n.Key));
        }

        /// <summary>上限类原因文本的 {2}：当前配置里的上限（emg.meta.max_exec / emg.meta.defer_total_max，与编译期判定、内核同一来源）；其余原因为空。</summary>
        public static string NoteLimit(string key) =>
            key == NoteMetaExecCap ? Math.Max(1, TuningInt("emg.meta.max_exec", EmgMetaConst.DefaultMaxExec)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : key == NoteMetaDeferCap ? Tuning("emg.meta.defer_total_max", EmgMetaConst.DefaultDeferMax).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
            : key == NoteAnchorCapped ? Math.Max(1, TuningInt("emg.fuse.anchor_max", 4)).ToString(System.Globalization.CultureInfo.InvariantCulture) // EMG3-FUSE-01
            : string.Empty;

        // ── 1. 指令序列 ──

        private static void AddSlots(EmgProgramResult r, EmgCompiled c, IReadOnlyList<string> ids, bool fromUplink, ICollection<string> skip)
        {
            for (int i = 0; ids != null && i < ids.Count; i++)
            {
                string id = ids[i];
                if (string.IsNullOrEmpty(id) || (skip != null && skip.Contains(id)))
                {
                    continue;
                }
                bool core = FirmwareKinds.IsCore(id);
                if (FirmwareKinds.TryGetParents(id, out string a, out string b))
                {
                    // EMG3-FUSE-01（FGR-EMG-044、FG19A 第 4.1 节“A / B 的取法”）：熔合 = [A, 接线, B] 压进 1 槽；落点更丰富（单次落点上限更大，第 4.2 节“单次上限”列）的一方作 A，
                    // 一样丰富 / 都没有落点时按配方表父 A（第 4.3 节 #24 湮灭镜像“偏折 → 处决”即场 4 > 处决 1）；写 grm.trig 的父 A 不换（B 本来就进它的子节点）
                    if (!OwnerWrites(c, a, "grm.trig") && OwnerLandingCap(c, b, r.Carrier) > OwnerLandingCap(c, a, r.Carrier))
                    {
                        (a, b) = (b, a);
                    }
                    // 修复轮（FGT-EMG-054 逐条等价）：两半的“核心指令”按各自父固件的类别记，与玩家手摆的 [A, 接线, B] 一致（原来按混合固件整体记，B 半边会多出 Core 标记）
                    AddOwner(r, c, id, a, fromUplink, FirmwareKinds.IsCore(a));
                    r.Steps.Add(new EmgProgramStep
                    {
                        Ordinal = r.Steps.Count, FirmwareId = id, OwnerId = string.Empty, FromUplink = fromUplink, Core = core, Kind = EmgStepKind.Anchor, Builtin = true,
                        EventPort = (ushort)(EventPortBase + (int)EmgEventKind.Fire),
                    });
                    AddOwner(r, c, id, b, fromUplink, FirmwareKinds.IsCore(b));
                }
                else
                {
                    AddOwner(r, c, id, id, fromUplink, core);
                }
            }
        }

        /// <summary>
        /// EMG4-LOOT-01 修复轮 1（FGR-FAC-009，承接 FG-GAP-150；规则只看端口，FGR-EMG-013）：主组件自己的声明里有程序写端（mat.* 排放、hij.* 注入、grm.* 语法修饰）时，
        /// 与固件同一套编译编成一条内置指令——放在根节点、另加一格容量（不挤占固件槽与接入配额）、不计指令热量、不占刷写带宽。
        /// 阵营标志组件（喷淋臂 / 环形喷淋架“命中 → 排放酸液”、干扰发射器“注入入侵度”、驻锄曲射炮“抛射”）靠它生效；
        /// EMGR-TRIG-01 起，没有场写端的主组件上的命中注能写端（切割束 en.heat）同样编成内置指令（<see cref="InnateHitInject"/>）；
        /// 其余组件的声明只写动能 / 实体 / 场 / 属性端口（由读法与部件场在别处结算），没有程序写端，不受影响。
        /// </summary>
        private static void AddInnate(EmgProgramResult r, EmgCompiled c, string owner)
        {
            if (string.IsNullOrEmpty(owner) || !c.OwnerIndex.TryGetValue(owner, out int oi) || !c.OwnerActive(oi) || !DeclsByOwner.TryGetValue(oi, out List<int> all)
                || !(all.Exists(x => ProgramWrite(c.Decls[x])) || InnateHitInject(c, all)))
            {
                return;
            }
            int before = r.Steps.Count;
            AddOwner(r, c, owner, owner, false, false);
            if (r.Steps.Count > before)
            {
                EmgProgramStep s = r.Steps[r.Steps.Count - 1];
                s.Innate = true;
                s.TableHeat = 0f;
            }
        }

        /// <summary>
        /// EMGR-TRIG-01（FG19A:348 comp_beam“en.heat inject 每段 20 热量单位”，此前没有任何执行点）：主组件声明了命中注能写端（en.heat / en.elec / en.mag inject）、且没有场写端
        /// （场组件的注能随场结算，FGR-EMG-120）时，同样编成内置指令——每次命中给目标与命中点斑块注能，与固件的注能写端同一条执行路径。
        /// 动能（en.kinetic）是载体动能系数（FG19B §6.3），不在这里。按端口 × 角色判，不认组件 ID。
        /// </summary>
        private static bool InnateHitInject(EmgCompiled c, List<int> all)
        {
            bool inject = false;
            foreach (int x in all)
            {
                EmgDeclRow d = c.Decls[x];
                if (d.Dir != EmgDirs.Write || !PortIds.TryGetValue(d.Port, out string pid))
                {
                    continue;
                }
                if (EmgCatalog.FamilyOf(pid) == EmgFamily.Field)
                {
                    return false;
                }
                inject |= d.Role == EmgRole.Inject && (pid == "en.heat" || pid == "en.elec" || pid == "en.mag");
            }
            return inject;
        }

        private static bool ProgramWrite(EmgDeclRow d)
        {
            if (d.Dir != EmgDirs.Write || !PortIds.TryGetValue(d.Port, out string pid))
            {
                return false;
            }
            EmgFamily f = EmgCatalog.FamilyOf(pid);
            return f == EmgFamily.Matter || f == EmgFamily.Hijack || f == EmgFamily.Grammar;
        }

        private static void AddOwner(EmgProgramResult r, EmgCompiled c, string firmwareId, string owner, bool fromUplink, bool core)
        {
            if (string.IsNullOrEmpty(owner) || !c.OwnerIndex.TryGetValue(owner, out int oi) || !DeclsByOwner.TryGetValue(oi, out List<int> all)
                || all.Count == 0 || !c.OwnerActive(oi))
            {
                r.LegacyOwners.Add(owner ?? firmwareId);
                return;
            }
            var s = new EmgProgramStep
            {
                Ordinal = r.Steps.Count, FirmwareId = firmwareId, OwnerId = owner, FromUplink = fromUplink, Core = core,
                TableHeat = FirmwareKinds.HeatOf(owner), EventPort = (ushort)(EventPortBase + (int)EmgEventKind.Fire),
            };
            byte bit = (byte)(1 << r.Carrier);
            bool trig = false, anchor = false, meta = false, mod = false, eventSet = false;
            EmgDeclRow trigWrite = default;
            // EMG2-MAT-01（FGR-EMG-221 零反应）：修复出口上读“反弹”的固件永远不触发（修复弹体不反弹），按端口整条判本载体不适用，预览写原因而不是静默死指令
            // EMG3-CHS-01（阶段 3 开启后固定集重抽发现，按推荐默认执行（2026-11-05），待用户复核）：修复出口打不死任何东西，读“击杀”的固件同样整条按本载体不适用（同一口径，预览写原因）
            bool bounceNa = r.HealOnly && all.Exists(x => c.Decls[x].Dir == EmgDirs.Read
                                                         && (c.Decls[x].Port == EventPortBase + (int)EmgEventKind.Bounce || c.Decls[x].Port == EventPortBase + (int)EmgEventKind.Kill));
            foreach (int di in all)
            {
                EmgDeclRow d = c.Decls[di];
                if ((d.Carriers & bit) == 0 || bounceNa)
                {
                    continue;
                }
                s.Decls.Add(di);
                string port = PortIds.TryGetValue(d.Port, out string pid) ? pid : string.Empty;
                if (d.Dir == EmgDirs.Read && IsEventPort(d.Port))
                {
                    var e = (EmgEventKind)(d.Port - EventPortBase);
                    s.Promoted |= IsUnitLevel(e);
                    if (d.Role == EmgRole.Trigger && !eventSet)
                    {
                        eventSet = true;
                        s.EventPort = d.Port;
                        s.TrigEvent = e;
                        s.TrigParam = d.Param2;
                    }
                }
                else if (d.Dir == EmgDirs.Write)
                {
                    if (port == "grm.trig")
                    {
                        trig = true;
                        trigWrite = d;
                    }
                    anchor |= port == "grm.anchor";
                    meta |= EmgCatalog.FamilyOf(port) == EmgFamily.Meta;
                    mod |= IsMod(port);
                }
            }
            if (s.Decls.Count == 0)
            {
                r.Notes.Add(new EmgCompileNote { Key = NoteCarrierNa, Ordinal = s.Ordinal, Node = -1 });
            }
            s.Kind = trig ? EmgStepKind.Trigger : anchor ? EmgStepKind.Anchor : meta ? EmgStepKind.Meta : mod ? EmgStepKind.Modify : EmgStepKind.Effect;
            s.AnchorKind = AnchorKindOf(c, s.Decls); // EMG3-FUSE-01：按写端口种类（FG19A 第 4.2 节），不看固件 ID
            if (trig)
            {
                if (s.TrigParam == 0f)
                {
                    s.TrigParam = trigWrite.Param2;
                }
                s.TrigCount = Math.Max(1, Math.Min(TuningInt("emg.trig.max_children", 4), (int)Math.Round(trigWrite.Amount)));
            }
            r.Steps.Add(s);
        }

        /// <summary>
        /// EMG4-FUZZ-01（DEBT-EMG4FUZZ01-02；FG-GAP-178 ④“本载体不适用”同一口径扩展，按推荐默认执行（2026-11-11），待用户复核）：修复出口的命中结算只修复——
        /// 内核修复弹体 / 当场修复命中友军时只调 Heal（CombatLogic.cs 弹体命中 heal 分支），不执行命中结算类写端：入侵（hij.*，只对敌对目标，CombatLogic.Hijack）、
        /// 无生效事件的注能 / 施加 / 展开场 / 打击修饰（连锁、反弹、体积、破甲、载荷质量）、“命中”生效的打击修饰、走旧读法过渡（驻留区 / 拖尾）的物质排放（修复命中不结算读法；
        /// 模糊测试器实测：维修束上单装酸液 / 漏油 / 黏胶 0 块斑块）。整条程序没有触发器（没有子载荷节点）时，
        /// 写端全是这些的固件整条按本载体不适用（预览写 emg.compile.carrier_na，仍占程序位置）；有触发器的程序不动（治疗子弹药另有执行点，见 EmgNodeEffects）。按端口 / 角色判，不认内容 ID。
        /// </summary>
        private static void HealInertPass(EmgProgramResult r, EmgCompiled c)
        {
            if (!r.HealOnly || r.Steps.Exists(st => st.Kind == EmgStepKind.Trigger && !st.Overflow))
            {
                return;
            }
            foreach (EmgProgramStep s in r.Steps)
            {
                if (s.Innate || s.Builtin || s.Overflow || s.Decls.Count == 0 || !HealInert(c, s, r.LobAllowed))
                {
                    continue;
                }
                s.Decls.Clear();
                s.Promoted = false;
                s.Kind = EmgStepKind.Effect;
                s.TrigEvent = EmgEventKind.None;
                s.EventPort = (ushort)(EventPortBase + (int)EmgEventKind.Fire);
                s.AnchorKind = AnchorKindOf(c, s.Decls);
                r.Notes.Add(new EmgCompileNote { Key = NoteCarrierNa, Ordinal = s.Ordinal, Node = -1 });
            }
        }

        private static readonly string[] HealInertMods = { "grm.mod.chain", "grm.mod.bounce", "grm.mod.size", "grm.mod.armor", "prop.mass" };

        private static bool HealInert(EmgCompiled c, EmgProgramStep s, bool lobAllowed)
        {
            bool write = false, untriggered = true, hitOnly = true;
            foreach (int di in s.Decls)
            {
                EmgDeclRow d = c.Decls[di];
                if (d.Dir == EmgDirs.Read && d.Role == EmgRole.Trigger)
                {
                    untriggered = false;
                    hitOnly &= d.Port == EventPortBase + (int)EmgEventKind.Hit;
                }
            }
            foreach (int di in s.Decls)
            {
                EmgDeclRow d = c.Decls[di];
                if (d.Dir != EmgDirs.Write)
                {
                    continue;
                }
                write = true;
                string port = PortIds.TryGetValue(d.Port, out string pid) ? pid : string.Empty;
                EmgFamily f = EmgCatalog.FamilyOf(port);
                // 物质排放：走旧读法过渡（驻留区 / 拖尾，修复命中不结算读法）的不执行；由内核在生效事件处直接排斑块的（EmgRootGated，见 Emit 里 RootKernel 的判定）照常执行
                bool legacyEmit = d.Role == EmgRole.Emit && f == EmgFamily.Matter
                                  && !(IsRootKernelOwner(s.OwnerId) || IsMediumEmit(c, s, port, d.Role) || IsTriggeredEmitWithoutTransition(c, s, port, d.Role));
                bool inert = f == EmgFamily.Hijack || legacyEmit
                             || (hitOnly && d.Role == EmgRole.Modify && Array.IndexOf(HealInertMods, port) >= 0)
                             || (untriggered && (d.Role == EmgRole.Inject || d.Role == EmgRole.Apply || (d.Role == EmgRole.Spawn && f == EmgFamily.Field)))
                             // 审查 P1 修复轮（按行为判死，#302 / #328）：“命中”生效的注能只在造成伤害的命中里结算（CombatLogic.EmgHitInject），追加弹体（ent.projectile）对修复宿主直接返回（EmgQueueFollow）
                             || (hitOnly && (d.Role == EmgRole.Inject || (d.Role == EmgRole.Spawn && port == "ent.projectile")))
                             // 修复轮 1 第 2 棒（#276）：光束不可曲射（NoteLobStraight），修复光束连落点扬尘也不结算，曲射修饰在修复出口上同样不适用
                             || (!lobAllowed && d.Role == EmgRole.Modify && port == "grm.mod.lob");
                if (!inert)
                {
                    return false;
                }
            }
            return write;
        }

        // ── 2. 节点分配（FGR-EMG-031、032）──

        private static int Cap(EmgProgramResult r, int gen) => Math.Max(1, r.RootCapacity >> gen);

        private static void BuildTree(EmgProgramResult r)
        {
            EmgTreeNode root = NewNode(r, -1, r.BaseGen, null); // EMG4-HIJ-02：镜像复制品的根是 1 代
            foreach (EmgProgramStep s in r.Steps)
            {
                if (s.Innate)
                {
                    root.Capacity++; // EMG4-LOOT-01：主组件内置指令另占一格，不挤固件
                    Place(r, root, s);
                }
            }
            foreach (EmgProgramStep s in r.Steps)
            {
                if (!s.Promoted || s.Innate)
                {
                    continue;
                }
                // 第 0 步：单位级读端先放进根；根满记溢出。被提升的触发器照样放子节点，但不把后续指令带进去（后续从根开始）。
                if (root.Used >= root.Capacity)
                {
                    MarkOverflow(r, s);
                    continue;
                }
                Place(r, root, s);
            }
            EmgTreeNode cur = root;
            foreach (EmgProgramStep s in r.Steps)
            {
                if (s.Promoted || s.Builtin || s.Innate) // EMG3-FUSE-01：内置接线不另占容量；EMG4-LOOT-01：主组件内置指令已放进根
                {
                    continue;
                }
                while (cur.Used >= cur.Capacity && cur.Parent >= 0)
                {
                    cur = r.Nodes[cur.Parent];
                }
                if (cur.Used >= cur.Capacity)
                {
                    MarkOverflow(r, s); // 回到根仍满：溢出、不编译、不计热量，继续下一条
                    continue;
                }
                EmgTreeNode child = Place(r, cur, s);
                if (child != null)
                {
                    cur = child;
                }
            }
            // 根里被提升的指令先放、所以先排在前；放完按程序位置重排，继承候选、同键“后来者无效”与内核行顺序都按程序位置（FGR-EMG-033）
            root.Entries.Sort((x, y) => x.Ordinal.CompareTo(y.Ordinal));
        }

        private static void MarkOverflow(EmgProgramResult r, EmgProgramStep s)
        {
            s.Overflow = true;
            r.HeatOverflowInstr += s.TableHeat;
            r.Notes.Add(new EmgCompileNote { Key = NoteOverflow, Ordinal = s.Ordinal, Node = -1 });
        }

        /// <summary>把指令放进节点；是触发器且节点代号 &lt; 2 时新建子节点并返回它，代号已到 2 时占位、记“代际已满”。</summary>
        private static EmgTreeNode Place(EmgProgramResult r, EmgTreeNode node, EmgProgramStep s)
        {
            node.Entries.Add(new EmgTreeEntry { Ordinal = s.Ordinal, FromNode = node.Index });
            return SpawnChild(r, node, s);
        }

        private static EmgTreeNode SpawnChild(EmgProgramResult r, EmgTreeNode node, EmgProgramStep s)
        {
            if (s.Kind != EmgStepKind.Trigger)
            {
                return null;
            }
            if (node.Gen >= EmgConst.MaxGen)
            {
                r.Notes.Add(new EmgCompileNote { Key = NoteGenFull, Ordinal = s.Ordinal, Node = node.Index });
                return null;
            }
            return NewNode(r, node.Index, node.Gen + 1, s);
        }

        private static EmgTreeNode NewNode(EmgProgramResult r, int parent, int gen, EmgProgramStep trigger)
        {
            var n = new EmgTreeNode { Index = r.Nodes.Count, Parent = parent, Gen = gen, Capacity = Cap(r, gen) };
            if (trigger != null)
            {
                n.TriggerOrdinal = trigger.Ordinal;
                n.TrigEvent = trigger.TrigEvent;
                n.TrigCount = trigger.TrigCount;
                n.TrigParam = trigger.TrigParam;
            }
            r.Nodes.Add(n);
            return n;
        }

        // ── 3. 编译期继承（FGR-EMG-033）──

        private static void Inherit(EmgProgramResult r, EmgCompiled c)
        {
            bool widened = false; // 程序里有写 grm.inherit 的指令：触发器 / 效果也进继承候选
            foreach (EmgProgramStep s in r.Steps)
            {
                widened |= !s.Overflow && WidensInherit(c, s);
            }
            var candidates = new List<EmgTreeEntry>(8);
            // 按建节点顺序处理（父节点总在子节点之前）；继承来的触发器在 2 代以内再放子节点，追加到表尾、随后处理。
            for (int i = 1; i < r.Nodes.Count; i++)
            {
                EmgTreeNode n = r.Nodes[i];
                EmgTreeNode p = r.Nodes[n.Parent];
                candidates.Clear();
                // 最近的祖先优先：先是父节点自己位于触发器之前的指令（按程序位置），再是父节点继承来的（来源越近越先，同一来源按程序位置）
                foreach (EmgTreeEntry e in p.Entries)
                {
                    if (!e.Inherited && e.Ordinal < n.TriggerOrdinal)
                    {
                        candidates.Add(e);
                    }
                }
                int own = candidates.Count;
                foreach (EmgTreeEntry e in p.Entries)
                {
                    // 同一条位置规则：只继承位于放出本节点的触发器之前的指令（继承来的触发器再放子节点时，不把它自己和它之后的指令带下去）
                    if (e.Inherited && e.Ordinal < n.TriggerOrdinal)
                    {
                        candidates.Add(e);
                    }
                }
                candidates.Sort(own, candidates.Count - own, Comparer<EmgTreeEntry>.Create((x, y) =>
                {
                    int gx = r.Nodes[x.FromNode].Gen, gy = r.Nodes[y.FromNode].Gen;
                    return gx != gy ? gy.CompareTo(gx) : x.Ordinal.CompareTo(y.Ordinal);
                }));
                foreach (EmgTreeEntry e in candidates)
                {
                    EmgProgramStep s = r.Steps[e.Ordinal];
                    // 被提升的单位级指令只在本机程序上生效，不进子弹药（FG19A A3：否则是死指令）
                    bool eligible = !s.Promoted && (s.Kind == EmgStepKind.Modify || (widened && (s.Kind == EmgStepKind.Trigger || s.Kind == EmgStepKind.Effect)));
                    if (!eligible || Contains(n, e.Ordinal))
                    {
                        continue;
                    }
                    if (n.Used >= n.Capacity)
                    {
                        r.Notes.Add(new EmgCompileNote { Key = NoteNotFlashed, Ordinal = e.Ordinal, Node = n.Index });
                        continue;
                    }
                    n.Entries.Add(new EmgTreeEntry { Ordinal = e.Ordinal, Inherited = true, FromNode = e.Inherited ? e.FromNode : p.Index });
                    SpawnChild(r, n, s);
                }
            }
        }

        private static bool Contains(EmgTreeNode n, int ordinal)
        {
            foreach (EmgTreeEntry e in n.Entries)
            {
                if (e.Ordinal == ordinal)
                {
                    return true;
                }
            }
            return false;
        }

        // ── 4. 内核行、幂等、热量、签名（FGR-EMG-016、017、045）──

        private static void Emit(EmgProgramResult r, EmgCompiled c)
        {
            bool widened = false, hijackFlash = false;
            foreach (EmgProgramStep s in r.Steps)
            {
                widened |= !s.Overflow && WidensInherit(c, s);
                hijackFlash |= !s.Overflow && IsHijackFlash(c, s);
            }
            float trigGen = Tuning("emg.heat.trigger_gen", 2f), perChild = Tuning("emg.heat.per_child", 1f);
            var mult = new int[r.Nodes.Count];
            var keys = new Dictionary<(ushort port, EmgRole role, ushort evt, float filter), int>(8);
            var magOf = new List<byte>(16); // EMG3-FUSE-01：每行的 fg.TbPortCarrier mag 列（接线复制行按它乘幅度）
            uint listen = 0;
            bool flight = false, core = false;
            foreach (EmgTreeNode n in r.Nodes)
            {
                int first = r.KernelInstrs.Count;
                byte inheritMask = 0;
                keys.Clear();
                foreach (EmgTreeEntry e in n.Entries)
                {
                    EmgProgramStep s = r.Steps[e.Ordinal];
                    if (s.Kind == EmgStepKind.Anchor)
                    {
                        if (!e.Inherited)
                        {
                            r.HeatInstr += s.TableHeat; // 独立接线：只占 1 条容量；不生成节点、不生成可执行行（ADR ②），代价在接线热量里
                        }
                        continue;
                    }
                    bool draw = !e.Inherited && (HasWrite(c, s, "mat.water") || HasWrite(c, s, "mat.coolant")); // EMG3-CHS-02 取水：按写端端口判，不按固件 ID
                    if (e.Inherited)
                    {
                        inheritMask |= (byte)(1 << r.Nodes[e.FromNode].Gen);
                    }
                    else
                    {
                        r.HeatInstr += s.TableHeat;
                        r.HeatDraw += draw ? s.TableHeat : 0f;
                    }
                    foreach (int di in s.Decls)
                    {
                        EmgDeclRow d = c.Decls[di];
                        if (s.Flip && d.Dir == EmgDirs.Write)
                        {
                            d.Port = FlipPort(d.Port); // EMG3-META-01：逆序树里反转之后的场 / 位移写端取反
                        }
                        string port = PortIds.TryGetValue(d.Port, out string pid) ? pid : string.Empty;
                        bool write = d.Dir == EmgDirs.Write;
                        // 默认只继承修饰写端；效果写端只在程序里有 grm.inherit 时一并继承（FG19A 第 0.3 节口径 5）。读端随修饰一并带上。
                        if (write && e.Inherited && !widened && !IsMod(port))
                        {
                            continue;
                        }
                        if (write && !IsStructural(port))
                        {
                            var key = (d.Port, d.Role, s.EventPort, port == "grm.mod.bounce" || port == "ent.wreck" ? d.Param2 : 0f); // EMG2-RECL-01：回收修复（param2 > 0 修复）与拆解钳（param2 = 0 只拆）是两种拆解，不互相判重复
                            if (keys.TryGetValue(key, out int at))
                            {
                                // 同节点同键：只生效一次，取幅度大者；后来的这条显示“重复无效”，不计它的声明热量。
                                // 合并行：目标偏好按位并（FGR-EMG-017）；“核心”只在各条都是核心时保留（信号离开剥离核心时常规那份留下）；序号取最早（刷写截断到所有来源都被截才丢这行）
                                EmgInstr kept = r.KernelInstrs[at];
                                if (!r.MergeSources.TryGetValue(at, out List<EmgMergeSource> srcs))
                                {
                                    srcs = new List<EmgMergeSource>(2)
                                    {
                                        new EmgMergeSource { Ordinal = kept.Ordinal, Core = (kept.Flags & EmgInstrFlags.Core) != 0, Amount = kept.Amount, Param2 = kept.Param2, Flags = kept.Flags },
                                    };
                                    r.MergeSources[at] = srcs;
                                }
                                srcs.Add(new EmgMergeSource
                                {
                                    Ordinal = (byte)s.Ordinal, Core = s.Core, Amount = d.Amount, Param2 = d.Param2,
                                    Flags = (e.Inherited ? EmgInstrFlags.Inherited : EmgInstrFlags.None) | (s.Core ? EmgInstrFlags.Core : EmgInstrFlags.None) | PreferenceOf(c, s, port),
                                });
                                if (d.Amount > kept.Amount)
                                {
                                    kept.Amount = d.Amount;
                                    kept.Param2 = d.Param2;
                                }
                                kept.Flags |= PreferenceOf(c, s, port);
                                if (!s.Core)
                                {
                                    kept.Flags &= ~EmgInstrFlags.Core;
                                }
                                kept.Ordinal = Math.Min(kept.Ordinal, (byte)s.Ordinal);
                                r.KernelInstrs[at] = kept;
                                r.Notes.Add(new EmgCompileNote { Key = NoteDuplicate, Ordinal = s.Ordinal, Node = n.Index, Port = d.Port });
                                continue;
                            }
                            keys[key] = r.KernelInstrs.Count;
                        }
                        if (!e.Inherited)
                        {
                            r.HeatDecl += Math.Max(0f, d.Heat);
                            r.HeatDraw += draw ? Math.Max(0f, d.Heat) : 0f;
                        }
                        var x = new EmgInstr
                        {
                            Port = d.Port, Role = (byte)d.Role, Ordinal = (byte)s.Ordinal, Amount = d.Amount, Param2 = d.Param2,
                            Flags = (e.Inherited ? EmgInstrFlags.Inherited : EmgInstrFlags.None) | (s.Core ? EmgInstrFlags.Core : EmgInstrFlags.None)
                                    | (write ? PreferenceOf(c, s, port) : EmgInstrFlags.None),
                        };
                        byte mag = 0;
                        int pcKey = d.Port << 3 | ResolveCarrier(c, r.Carrier, n.Parent);
                        // EMG1-FW-01：物质端口同一对有排放 / 扩散两行，按声明角色选行（modify = 扩散 / 随车，emit = 排放；FG19A 第 3.2 节）
                        if (write && (d.Role == EmgRole.Modify && SpreadAt.TryGetValue(pcKey, out int pci) || PortCarrierAt.TryGetValue(pcKey, out pci)))
                        {
                            EmgPortCarrierRow pc = c.PortCarriers[pci];
                            mag = (byte)pc.Mag;
                            x.Verb = pc.Verb;
                            x.A = pc.A;
                            x.B = pc.B;
                            x.C = pc.C;
                            if (n.Parent >= 0 && pc.Verb >= 1 && pc.Verb <= 25)
                            {
                                ChildLegacyVerb(ref x, pc, s.FirmwareId, port, (FirmwareCarrier)ResolveCarrier(c, r.Carrier, n.Parent));
                            }
                            if (!r.LobAllowed && pc.Verb == EmgCatalog.VerbCode("lob"))
                            {
                                x.Verb = 0; // EMG1-GRM-01：光束直线，曲射不越障（同条固件的落点扬尘照常）
                                r.Notes.Add(new EmgCompileNote { Key = NoteLobStraight, Ordinal = s.Ordinal, Node = n.Index, Port = d.Port });
                            }
                            // EMG1-FW-02：只走声明的固件（没有旧读法轨可过渡，FG19A 第 6 节新固件）在根节点上的效果写端，阶段 1 由内核在这条指令的生效事件处
                            // 直接执行（CombatLogic.EmgRootGated：排放 → 驻留区、注电 → 电击区，FG19A 第 0.3 节口径 3）；按数据列判（FirmwareKinds.IsDeclOnlyRow），不按内容 ID。
                            bool medium = n.Parent < 0 && IsMediumEmit(c, s, port, d.Role);
                            if (medium)
                            {
                                // 声明 param2 = 这摊介质的半径；格斗 / 力场读法只让已有斑块扩散、不留水（FG19A 第 1.3 节蔓延读法），写端仍标内核，旧驻留区同样退役
                                var mediumCarrier = (FirmwareCarrier)ResolveCarrier(c, r.Carrier, n.Parent);
                                x.A = mediumCarrier == FirmwareCarrier.Melee || mediumCarrier == FirmwareCarrier.Aura ? 0f : d.Param2 > 0f ? d.Param2 : x.A;
                            }
                            if (n.Parent < 0 && x.Verb != 0 && (IsRootKernelOwner(s.OwnerId) || IsRootKernelPort(port) || IsRootKernelWrite(port, d.Role) || medium || IsTriggeredEmitWithoutTransition(c, s, port, d.Role)) && EmgCatalog.FamilyOf(port) is var rkFam && rkFam != EmgFamily.Grammar && rkFam != EmgFamily.Meta
                                && rkFam != EmgFamily.Hijack) // EMG4-FW-01：hij.hack 注入由内核命中注入执行者执行（EmgHitInject → EmgHackInject，FGR-EMG-163），不走根节点生效事件
                            {
                                x.Flags |= EmgInstrFlags.RootKernel | (pc.Mag switch { 1 => EmgInstrFlags.MagA, 2 => EmgInstrFlags.MagB, 3 => EmgInstrFlags.MagC, _ => EmgInstrFlags.None });
                            }
                        }
                        if (!write && IsEventPort(d.Port))
                        {
                            listen |= 1u << (d.Port - EventPortBase);
                        }
                        flight |= n.Index == 0 && write && Array.IndexOf(FlightMods, port) >= 0;
                        core |= s.Core;
                        r.KernelInstrs.Add(x);
                        magOf.Add(mag);
                    }
                }
                if (n.Parent >= 0)
                {
                    if (n.TrigEvent != EmgEventKind.None)
                    {
                        listen |= 1u << (int)n.TrigEvent;
                    }
                    flight |= n.Parent == 0 && NeedsFlightEvent(n.TrigEvent);
                    mult[n.Index] = n.TrigCount * (n.Parent > 0 ? mult[n.Parent] : 1);
                    r.ChildPayloads += mult[n.Index];
                    r.HeatTrigger += trigGen * n.Gen;
                }
                r.KernelNodes.Add(new EmgNode
                {
                    Parent = (sbyte)n.Parent, Gen = (byte)n.Gen, TrigEvent = n.TrigEvent, TrigCount = (byte)n.TrigCount, TrigParam = n.TrigParam,
                    FirstInstr = first, InstrCount = (byte)(r.KernelInstrs.Count - first), SubForm = n.Parent >= 0 ? SubFormOf(c, r.Carrier) : (byte)0,
                    InheritMask = inheritMask,
                });
            }
            r.HeatChildren = perChild * r.ChildPayloads;
            bool anchored = AnchorPass(r, c, magOf);
            r.Program = new EmgProgram
            {
                Flags = (flight ? EmgProgramFlags.NeedsFlight : EmgProgramFlags.None) | (core ? EmgProgramFlags.HasCore : EmgProgramFlags.None)
                        | (anchored ? EmgProgramFlags.HasAnchor : EmgProgramFlags.None) | (r.BaseGen > 0 ? EmgProgramFlags.IsCopy : EmgProgramFlags.None) // EMG4-HIJ-02：镜像复制品
                        | (hijackFlash ? EmgProgramFlags.HijackFlash : EmgProgramFlags.None), // EMG4-FW-01：N40 劫持中继
                ListenMask = listen, HeatPerShot = r.HeatTotal, ClueMask = ClueMaskOf(r.Steps, c),
            };
            r.Signature = r.ContentVersion + "-" + Fnv64(Combat.CombatSite.ProgramKey(r.Program, r.KernelNodes, r.KernelInstrs)).ToString("X16");
        }

        /// <summary>
        /// EMG1-GRM-01 审查修复：写端行按哪个载体查 fg.TbPortCarrier。根节点按主组件载体；子节点的子形态是飞行弹体（子弹 / 余波 / 无人机子弹）时按射弹列解析
        /// （否则格斗 / 无人机子节点上的修饰解析成旧读法动词，而旧动词只在根节点折进读法，子弹药上装了等于没装）。子区 / 脉冲子节点仍按主载体（见 <see cref="ModifierFirmware"/>）。
        /// </summary>
        private static byte ResolveCarrier(EmgCompiled c, byte carrier, int parent) =>
            parent >= 0 && IsFlyingSubForm(SubFormOf(c, carrier)) ? (byte)FirmwareCarrier.Projectile : carrier;

        /// <summary>
        /// EMG1-FW-01（DEBT-EMG1GRM01-05；FG19A 第 3.2 节）：子节点上的旧读法动词行由子载荷自己结算（内核 CombatLogic.ChildMods）。
        /// 一般行：fg.TbPortCarrier 的 mag 列写成 MagA / B / C 位（内核按当前幅度乘，刷写剥离改幅度后照样对）；
        /// 只改数字的端口（体积 / 破甲 / 威力）且固件自带对应旧字段时，与根节点同一口径（CombatSite.FoldLegacyNumeric：旧字段的读法行 × 旧幅度），
        /// 在子形态的载体列上解析成最终值（广角 spread:60 → 射弹列余击、力场列 +2 米），动词改成解析出的那一个、不再乘声明幅度。
        /// </summary>
        private static void ChildLegacyVerb(ref EmgInstr x, EmgPortCarrierRow pc, string firmwareId, string port, FirmwareCarrier car)
        {
            x.Flags |= pc.Mag switch { 1 => EmgInstrFlags.MagA, 2 => EmgInstrFlags.MagB, 3 => EmgInstrFlags.MagC, _ => EmgInstrFlags.None };
            string[] fields = NumericLegacyFields(port);
            if (fields.Length == 0)
            {
                return;
            }
            var baseR = new CombatReading { DamageScale = 1f, CooldownScale = 1f };
            CombatReading rd = baseR;
            bool any = false;
            foreach ((string field, float mag) in EmgCatalog.DeclLegacyOf(firmwareId)) // EMG4-CLEAN-01：声明参数 legacy 列，不读 readFields
            {
                if (Array.IndexOf(fields, field) >= 0)
                {
                    any |= CarrierReadings.ApplyField(ref rd, car, field, mag, false);
                }
            }
            if (!any)
            {
                return; // 新固件没有旧字段：端口行 × 声明幅度（上面的 Mag 位）
            }
            x.Flags &= ~(EmgInstrFlags.MagA | EmgInstrFlags.MagB | EmgInstrFlags.MagC);
            x.A = x.B = x.C = 0f;
            if (rd.BlastRadius > 0f && rd.BlastRatio > 0f)
            {
                (x.Verb, x.A, x.B) = ((byte)EmgVerb.Blast, rd.BlastRadius, rd.BlastRatio);
            }
            else if (rd.ExtraHits > 0)
            {
                (x.Verb, x.A, x.B, x.C) = ((byte)EmgVerb.Extra, rd.ExtraHits, rd.ExtraRatio, rd.ExtraRadius);
            }
            else if (rd.Area > 0f)
            {
                (x.Verb, x.A) = ((byte)EmgVerb.Reach, rd.Area);
            }
            else if (rd.ArmorPierce > 0f)
            {
                (x.Verb, x.A) = ((byte)EmgVerb.Armor, rd.ArmorPierce);
            }
            else if (Math.Abs(rd.DamageScale - 1f) > 1e-4f)
            {
                (x.Verb, x.A) = ((byte)EmgVerb.Dmg, rd.DamageScale);
            }
            else
            {
                x.Verb = 0; // 旧字段在子形态的载体列上没有子载荷能执行的读法（如只改出手间隔）：不执行
            }
        }

        /// <summary>EMG1-FIX-01（FG19A 第 1.2 节第 26 行 / D-14）：根节点上由内核直接执行、不再走旧读法过渡的写端口（任何 owner）：ent.projectile = 追射（CombatLogic.EmgQueueFollow）。按端口，不按内容 ID。</summary>
        public static bool IsRootKernelPort(string port) => port == "ent.projectile" || (port != null && port.StartsWith("fld.", StringComparison.Ordinal)); // EMG2-FLD-01：场写端由内核在结算点 / 生效事件处展开

        /// <summary>
        /// EMG2-RECL-01：阶段 ≥ 2（物质层开着）时根节点上由内核在生效事件处直接执行的写端——ent.wreck（回收修复 / 拆解钳拆残骸）、prop.conductive（桥接导线）、
        /// 地面物质的 modify（蔓延）。按端口 × 角色，不按内容 ID。阶段 1 回滚对照不标（桥接走减速网过渡）。
        /// </summary>
        public static bool IsRootKernelWrite(string port, EmgRole role) =>
            port != null && (port == "ent.wreck" || port == "prop.conductive" || (role == EmgRole.Modify && port.StartsWith("mat.", StringComparison.Ordinal)));

        /// <summary>
        /// EMG2-RECL-01（FG19A 第 1.3 节第 35 行）：同一条声明里还改写地面物质（modify）的 owner，它的物质排放是给改写当介质的：阶段 ≥ 2 由内核排真斑块
        /// （走旧驻留区过渡的话物质按状态位映射成冷却液、又是按区域载荷结算的迁入斑块，排的就不是声明写的那种液体）。按声明判，不按内容 ID。
        /// </summary>
        private static bool IsMediumEmit(EmgCompiled c, EmgProgramStep s, string port, EmgRole role)
        {
            if (role != EmgRole.Emit || port == null || !port.StartsWith("mat.", StringComparison.Ordinal))
            {
                return false;
            }
            foreach (int di in s.Decls)
            {
                EmgDeclRow o = c.Decls[di];
                if (o.Dir == EmgDirs.Write && o.Role == EmgRole.Modify && PortIds.TryGetValue(o.Port, out string pid) && pid.StartsWith("mat.", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// EMG3-FW-01（DEBT-EMG1E2E01-02，FGR-EMG-019 单轨）：阶段 ≥ 2 根节点上的物质排放，由事件触发（同一 owner 有 trigger 读端）、而这条固件没有物质过渡字段（linger / trail）可执行时，
        /// 由内核在生效事件处直接排真斑块（EmgRootGated → EmgEmitPatch）——否则这条声明写端没有任何执行者（腐蚀弹头“命中排酸”0 块）。按声明与数据列判，不按内容 ID。
        /// </summary>
        private static bool IsTriggeredEmitWithoutTransition(EmgCompiled c, EmgProgramStep s, string port, EmgRole role)
        {
            if (role != EmgRole.Emit || port == null || !port.StartsWith("mat.", StringComparison.Ordinal))
            {
                return false;
            }
            bool trig = false, fireTrig = false;
            foreach (int di in s.Decls)
            {
                trig |= c.Decls[di].Dir == EmgDirs.Read && c.Decls[di].Role == EmgRole.Trigger;
                fireTrig |= c.Decls[di].Dir == EmgDirs.Read && c.Decls[di].Role == EmgRole.Trigger && c.Decls[di].Port == EventPortBase + (int)EmgEventKind.Fire;
            }
            if (!trig)
            {
                return false;
            }
            if (fireTrig)
            {
                return true; // EMG4-FUSE-02（FG19A 第 4.2 节“沿途排放的 mat.*（读 evt.fire）”）：沿途排放由内核按长度均分段点排真斑块（CombatLogic.EmgTrailEmit），过渡读法 trail 退役（FG-GAP-205）
            }
            // 审查修复（第 1 轮）：按这条声明的 owner（混合固件 = 父固件）自己的旧字段判——混合行的 readFields 是两父相加，另一父的驻留区 / 拖尾只排它自己那种物质
            foreach ((string f, float _) in EmgCatalog.DeclLegacyOf(s.OwnerId))
            {
                if (Array.IndexOf(MatterTransition, f) >= 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool OwnerHasField(string owner, string field)
        {
            foreach ((string f, float _) in EmgCatalog.DeclLegacyOf(owner))
            {
                if (f == field)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsRootKernelOwner(string owner) =>
            FirmwareKinds.TryGetRow(owner, out GameConfig.fg.FirmwareKind row) && FirmwareKinds.IsDeclOnlyRow(row);

        private static bool IsFlyingSubForm(byte code) => code == EmgConst.SubBullet || code == EmgConst.SubWave || code == EmgConst.SubDroneBullet;

        private static byte SubFormOf(EmgCompiled c, byte carrier)
        {
            foreach (EmgSubFormRow s in c.SubForms)
            {
                if (s.Carrier == carrier)
                {
                    return s.Code;
                }
            }
            return 0;
        }

        // ── 小工具 ──

        private static void EnsureIndex(EmgCompiled c)
        {
            if (ReferenceEquals(_for, c))
            {
                return;
            }
            _for = c;
            DeclsByOwner.Clear();
            PortCarrierAt.Clear();
            SpreadAt.Clear();
            PortIds.Clear();
            for (int i = 0; i < c.Decls.Length; i++)
            {
                if (!DeclsByOwner.TryGetValue(c.Decls[i].Owner, out List<int> list))
                {
                    DeclsByOwner[c.Decls[i].Owner] = list = new List<int>(4);
                }
                list.Add(i);
            }
            foreach (List<int> list in DeclsByOwner.Values)
            {
                EmgDeclRow[] d = c.Decls;
                list.Sort((a, b) => d[a].Seq.CompareTo(d[b].Seq));
            }
            for (int i = 0; i < c.PortCarriers.Length; i++)
            {
                int key = c.PortCarriers[i].Port << 3 | c.PortCarriers[i].Carrier;
                if (c.PortCarriers[i].Verb == SpreadVerb && !SpreadAt.ContainsKey(key))
                {
                    SpreadAt[key] = i;
                    continue;
                }
                if (!PortCarrierAt.ContainsKey(key))
                {
                    PortCarrierAt[key] = i; // 同一对有多行时取第一行（主动词；表里现只有 grm.mod.size × 格斗一对是两行，多动词执行归 EMG1-GRM-01）
                }
            }
            foreach (KeyValuePair<ushort, string> kv in c.PortIdByCode)
            {
                PortIds[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// EMG4-FW-01（FG19A N40 劫持中继）：写 grm.inherit、且同一条指令读 hij.hijacked condition——作用对象是被劫持的敌机（它改刷本程序，内核 EmgTryHijack），
        /// 不扩大本机自己的编译期继承（目标没被劫持时不执行，FGR-EMG-033 的扩大继承只看无条件的 grm.inherit）。按端口判，不认内容 ID。
        /// </summary>
        private static bool IsHijackFlash(EmgCompiled c, EmgProgramStep s) => HasWrite(c, s, "grm.inherit") && HasCondRead(c, s, "hij.hijacked");

        private static bool WidensInherit(EmgCompiled c, EmgProgramStep s) => HasWrite(c, s, "grm.inherit") && !HasCondRead(c, s, "hij.hijacked");

        private static bool HasCondRead(EmgCompiled c, EmgProgramStep s, string port)
        {
            foreach (int di in s.Decls)
            {
                if (c.Decls[di].Dir == EmgDirs.Read && c.Decls[di].Role == EmgRole.Condition && PortIds.TryGetValue(c.Decls[di].Port, out string pid) && pid == port)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>程序（编译结果）带 N40 劫持中继：它劫持的敌机改刷本程序（预览 / 悬停 / 武器结算登记刷写变体用）。</summary>
        public static bool HasHijackFlash(EmgProgramResult r) => r != null && (r.Program.Flags & EmgProgramFlags.HijackFlash) != 0;

        private static bool HasWrite(EmgCompiled c, EmgProgramStep s, string port)
        {
            foreach (int di in s.Decls)
            {
                if (c.Decls[di].Dir == EmgDirs.Write && PortIds.TryGetValue(c.Decls[di].Port, out string pid) && pid == port)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>寻的 / 连锁写端的目标偏好 = 本指令的读端（FGR-EMG-017、FG19A A5）：读 evt.marked → 已标记；寻的读 prop.heat → 热信号；连锁读 prop.conductive → 导电。其他写端为 None。</summary>
        private static EmgInstrFlags PreferenceOf(EmgCompiled c, EmgProgramStep s, string writePort)
        {
            bool homing = writePort == "grm.mod.homing";
            if (!homing && writePort != "grm.mod.chain")
            {
                return EmgInstrFlags.None;
            }
            EmgInstrFlags f = EmgInstrFlags.None;
            foreach (int di in s.Decls)
            {
                if (c.Decls[di].Dir != EmgDirs.Read || !PortIds.TryGetValue(c.Decls[di].Port, out string pid))
                {
                    continue;
                }
                if (pid == "evt.marked")
                {
                    f |= EmgInstrFlags.PreferMarked;
                }
                else if (homing ? pid == "prop.heat" : pid == "prop.conductive")
                {
                    f |= homing ? EmgInstrFlags.PreferHeat : EmgInstrFlags.PreferConductive;
                }
            }
            return f;
        }

        private static bool IsEventPort(ushort port) => port > EventPortBase && port <= EventPortBase + EmgConst.EventKinds;

        private static readonly int SpreadVerb = EmgCatalog.VerbCode("spread");

        private static bool IsMod(string port) => port.StartsWith("grm.mod.", StringComparison.Ordinal);

        /// <summary>决定程序结构的写端（触发器 / 接线 / 元程序）：每条各成结构，不参与同键幂等。</summary>
        private static bool IsStructural(string port) => port == "grm.trig" || port == "grm.anchor" || EmgCatalog.FamilyOf(port) == EmgFamily.Meta;

        private static float Tuning(string id, float fallback)
        {
            if (GridContent.TryGetTuning(id, out float v))
            {
                return v;
            }
            if (WarnedTuning.Add(id))
            {
                Log.Error($"[EmgProgramCompiler] fg.TbHomeTuning 缺少 {id}，暂用规格初值 {fallback}（改 tools/cell_tables/fgdata_emg.py 后重新生成）。");
            }
            return fallback;
        }

        private static int TuningInt(string id, int fallback) => (int)Math.Round(Tuning(id, fallback));

        private static ulong Fnv64(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char ch in s)
            {
                h = (h ^ ch) * 1099511628211UL;
            }
            return h;
        }

        // ── 7. 落点接线（EMG3-FUSE-01；FG19 FGR-EMG-044；FG19A 第 4.1、4.2 节；ADR-EMG-001 ②）──


        /// <summary>
        /// 指令写端产生的落点种类（FG19A 第 4.2 节表，按写端口种类取、不看固件 ID）：多个写端都有落点时取表里靠前的一种。
        /// grm.trig（B 本来就进子节点）、落点排放的物质、homing / multi / drift / lob、纯数值、相态 = 不接线（预混，A、B 同点执行）。
        /// </summary>
        private static byte AnchorKindOf(EmgCompiled c, List<int> decls)
        {
            bool fireTrig = false;
            foreach (int di in decls)
            {
                EmgDeclRow d = c.Decls[di];
                fireTrig |= d.Dir == EmgDirs.Read && d.Role == EmgRole.Trigger && d.Port == EventPortBase + (int)EmgEventKind.Fire;
            }
            byte best = EmgAnchorConst.None;
            foreach (int di in decls)
            {
                EmgDeclRow d = c.Decls[di];
                if (d.Dir != EmgDirs.Write || !PortIds.TryGetValue(d.Port, out string port))
                {
                    continue;
                }
                EmgFamily fam = EmgCatalog.FamilyOf(port);
                byte k = port switch
                {
                    "grm.mod.chain" => EmgAnchorConst.Chain,
                    "grm.mod.bounce" => EmgAnchorConst.Bounce,
                    "grm.mod.pierce" => EmgAnchorConst.Pierce,
                    "grm.mod.return" => EmgAnchorConst.Return,
                    "grm.mod.orbit" => EmgAnchorConst.Orbit,
                    "ent.projectile" when d.Role == EmgRole.Spawn => EmgAnchorConst.Follow,
                    "ent.drone" => EmgAnchorConst.Drone,
                    "ent.wreck" => EmgAnchorConst.Wreck,
                    "prop.integrity" => EmgAnchorConst.Execute,
                    "prop.conductive" => EmgAnchorConst.Wire,
                    "evt.marked" => EmgAnchorConst.Marked,
                    _ => fam == EmgFamily.Matter && d.Role == EmgRole.Modify ? EmgAnchorConst.Spread
                        : fam == EmgFamily.Matter && d.Role == EmgRole.Emit && fireTrig ? EmgAnchorConst.Trail
                        : fam == EmgFamily.Field ? EmgAnchorConst.Field
                        : fam == EmgFamily.Energy && d.Role == EmgRole.Inject ? EmgAnchorConst.Inject
                        : EmgAnchorConst.None,
                };
                if (k != EmgAnchorConst.None && (best == EmgAnchorConst.None || k < best))
                {
                    best = k;
                }
            }
            return best;
        }

        /// <summary>一条 owner 在本载体上的落点种类（熔合芯片按它决定 A / B 的先后）。</summary>
        private static byte OwnerAnchorKind(EmgCompiled c, string owner, byte carrier)
        {
            if (string.IsNullOrEmpty(owner) || !c.OwnerIndex.TryGetValue(owner, out int oi) || !DeclsByOwner.TryGetValue(oi, out List<int> all))
            {
                return EmgAnchorConst.None;
            }
            byte bit = (byte)(1 << carrier);
            var on = new List<int>(all.Count);
            foreach (int di in all)
            {
                if ((c.Decls[di].Carriers & bit) != 0)
                {
                    on.Add(di);
                }
            }
            return AnchorKindOf(c, on);
        }

        /// <summary>一条 owner 在本载体上的单次落点上限（没有落点 = 0）：熔合芯片按它比“落点更丰富”。</summary>
        private static int OwnerLandingCap(EmgCompiled c, string owner, byte carrier)
        {
            byte kind = OwnerAnchorKind(c, owner, carrier);
            return kind == EmgAnchorConst.None || !c.OwnerIndex.TryGetValue(owner, out int oi) || !DeclsByOwner.TryGetValue(oi, out List<int> all) ? 0 : MaxLandingsOf(c, kind, all);
        }

        private static bool OwnerWrites(EmgCompiled c, string owner, string port)
        {
            if (string.IsNullOrEmpty(owner) || !c.OwnerIndex.TryGetValue(owner, out int oi) || !DeclsByOwner.TryGetValue(oi, out List<int> all))
            {
                return false;
            }
            foreach (int di in all)
            {
                if (c.Decls[di].Dir == EmgDirs.Write && PortIds.TryGetValue(c.Decls[di].Port, out string p) && p == port)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ReadsTrigger(EmgCompiled c, List<int> decls)
        {
            foreach (int di in decls)
            {
                if (c.Decls[di].Dir == EmgDirs.Read && c.Decls[di].Role == EmgRole.Trigger)
                {
                    return true;
                }
            }
            return false;
        }

        private static int NodeOfOwn(EmgProgramResult r, int ordinal)
        {
            foreach (EmgTreeNode n in r.Nodes)
            {
                foreach (EmgTreeEntry e in n.Entries)
                {
                    if (!e.Inherited && e.Ordinal == ordinal)
                    {
                        return n.Index;
                    }
                }
            }
            return -1;
        }

        /// <summary>
        /// 每处接线 [A, 接线, B]（独立接线或内置接线）：B 的效果写端（物质、能量、相态、实体、事件、场）复制一份、加 Anchored 与 A 的落点种类，放进程序尾段；A 的写端行记落点种类；
        /// 生效的接线编译期计 emg.fuse.anchor_max × emg.fuse.anchor_heat 热量（ADR ②、FGR-EMG-016）。无效时写原因（首位 / 末位 / 溢出、A B 不在同一节点、A 没有落点、B 只有修饰、落点内核未接通），不复制、不计热量。
        /// 返回 true = 程序有 Anchored 尾段。O(程序长度 × 节点行数)，只在装配变化时。
        /// </summary>
        private static bool AnchorPass(EmgProgramResult r, EmgCompiled c, List<byte> magOf)
        {
            int amax = Math.Max(1, TuningInt("emg.fuse.anchor_max", 4));
            float aheat = Math.Max(0f, Tuning("emg.fuse.anchor_heat", 1f));
            List<EmgInstr> tail = null;
            foreach (EmgProgramStep w in r.Steps)
            {
                if (w.Kind != EmgStepKind.Anchor || w.Overflow)
                {
                    continue;
                }
                var info = new EmgAnchorInfo { Ordinal = w.Ordinal, A = -1, B = -1, MaxLandings = amax };
                EmgProgramStep a = w.Ordinal > 0 ? r.Steps[w.Ordinal - 1] : null;
                EmgProgramStep b = w.Ordinal + 1 < r.Steps.Count ? r.Steps[w.Ordinal + 1] : null;
                int node = -1;
                if (a == null || b == null || a.Overflow || b.Overflow || a.Kind == EmgStepKind.Anchor || b.Kind == EmgStepKind.Anchor)
                {
                    info.Note = NoteAnchorDangling;
                }
                else
                {
                    info.A = a.Ordinal;
                    info.B = b.Ordinal;
                    info.Kind = a.AnchorKind;
                    node = NodeOfOwn(r, a.Ordinal);
                    if (a.Kind == EmgStepKind.Trigger || a.AnchorKind == EmgAnchorConst.None)
                    {
                        info.Note = NoteAnchorNone; // 触发器：B 本来就进 A 的子节点；其余：预混
                    }
                    else if (node < 0 || NodeOfOwn(r, b.Ordinal) != node)
                    {
                        info.Note = NoteAnchorSplit;
                    }
                    else if (!EmgAnchorConst.InKernel(a.AnchorKind))
                    {
                        info.Note = NoteAnchorPending; // EMG4-FUSE-02：15 种落点内核全部接通（含事件门控注能的脉冲分支 EmgRootGated），只剩防御性分支
                    }
                    else if (a.AnchorKind == EmgAnchorConst.Follow && node != 0)
                    {
                        info.Note = NoteAnchorFollowChild; // EMG4-FUSE-02 修复第 2 轮：补射记录固定挂根节点，子节点里的追射接线内核不改打、不带 B（CombatLogic.EmgFollowRetarget）——不复制、不计热，不做静默死指令
                    }
                }
                if (info.Note == null)
                {
                    EmgNode kn = r.KernelNodes[node];
                    int copies = 0, noExec = 0, emitVerb = EmgCatalog.VerbCode("emit");
                    for (int i = kn.FirstInstr; i < kn.FirstInstr + kn.InstrCount; i++)
                    {
                        EmgInstr x = r.KernelInstrs[i];
                        bool ping = x.Port == EmgConst.EventPortBase + (int)EmgEventKind.Ping; // 频道广播按端口 + 角色执行、没有动词（CombatLogic.EmgRootGated / EmgAnchorExec），不能当“只有修饰”跳过
                        if (x.Ordinal != b.Ordinal || x.Role <= EmgConst.RoleLastRead || (x.Verb == 0 && !ping) || (x.Flags & (EmgInstrFlags.Inherited | EmgInstrFlags.Overflow)) != 0
                            || !PortIds.TryGetValue(x.Port, out string port) || IsMod(port) || IsStructural(port))
                        {
                            continue;
                        }
                        EmgFamily fam = EmgCatalog.FamilyOf(port);
                        if (fam != EmgFamily.Matter && fam != EmgFamily.Energy && fam != EmgFamily.Phase && fam != EmgFamily.Entity && fam != EmgFamily.Event && fam != EmgFamily.Field
                            && port != "prop.integrity") // EMG4-FUSE-02：处决（prop.integrity 改写）在落点执行（FG19A 第 4.3 节湮灭镜像）
                        {
                            continue; // 修饰写端照常作用于节点、不接线（FG19A 第 4.1 节）
                        }
                        if (!EmgAnchorConst.Executable(x.Verb, x.Port, emitVerb))
                        {
                            noExec++; // 内核在落点上没有这族写端的执行者（en.kinetic 注入、ent.drone、evt.* 等）：写原因、不复制，不做静默死指令
                            continue;
                        }
                        byte mag = i < magOf.Count ? magOf[i] : (byte)0;
                        EmgInstr y = x;
                        y.Flags = (x.Flags & ~(EmgInstrFlags.Inherited | EmgInstrFlags.RootKernel | EmgInstrFlags.MagA | EmgInstrFlags.MagB | EmgInstrFlags.MagC)) | EmgInstrFlags.Anchored
                                  | (mag switch { 1 => EmgInstrFlags.MagA, 2 => EmgInstrFlags.MagB, 3 => EmgInstrFlags.MagC, _ => EmgInstrFlags.None });
                        y.AnchorKind = a.AnchorKind;
                        (tail ??= new List<EmgInstr>(4)).Add(y);
                        copies++;
                    }
                    if (copies == 0)
                    {
                        info.Note = noExec > 0 ? NoteAnchorNoExec : NoteAnchorModOnly;
                    }
                    else
                    {
                        for (int i = kn.FirstInstr; i < kn.FirstInstr + kn.InstrCount; i++)
                        {
                            EmgInstr x = r.KernelInstrs[i];
                            if (x.Ordinal == a.Ordinal && x.Role > EmgConst.RoleLastRead && (x.Flags & EmgInstrFlags.Inherited) == 0)
                            {
                                x.AnchorKind = a.AnchorKind;
                                r.KernelInstrs[i] = x;
                            }
                        }
                        info.Valid = true;
                        info.Copies = copies;
                        info.Heat = amax * aheat;
                        r.HeatAnchor += info.Heat;
                        if (MaxLandingsOf(c, a.AnchorKind, a.Decls) > amax)
                        {
                            r.Notes.Add(new EmgCompileNote { Key = NoteAnchorCapped, Ordinal = w.Ordinal, Node = node });
                        }
                        if (noExec > 0)
                        {
                            r.Notes.Add(new EmgCompileNote { Key = NoteAnchorNoExec, Ordinal = w.Ordinal, Node = node }); // 生效，但 B 的部分写端在落点上不执行
                        }
                        if (a.AnchorKind == EmgAnchorConst.Follow)
                        {
                            r.Notes.Add(new EmgCompileNote { Key = NoteAnchorFollowSolo, Ordinal = w.Ordinal, Node = node }); // EMG4-FUSE-02：没有第二个目标 = 重复无效（FGR-EMG-017），预览写原因
                        }
                    }
                }
                if (info.Note != null && !(w.Builtin && info.Note == NoteAnchorNone))
                {
                    r.Notes.Add(new EmgCompileNote { Key = info.Note, Ordinal = w.Ordinal, Node = node });
                }
                r.Anchors.Add(info);
            }
            if (tail == null)
            {
                return false;
            }
            r.KernelInstrs.AddRange(tail);
            return true;
        }

        /// <summary>A 一次结算最多产生几个落点（FG19A 第 4.2 节“单次上限”列：跳数 / 反弹数 / 穿透数取 A 的幅度，其余按表）——超过 emg.fuse.anchor_max 时预览写“超出的落点不执行”。</summary>
        private static int MaxLandingsOf(EmgCompiled c, byte kind, List<int> decls)
        {
            if (kind == EmgAnchorConst.Chain || kind == EmgAnchorConst.Bounce || kind == EmgAnchorConst.Pierce)
            {
                string want = kind == EmgAnchorConst.Chain ? "grm.mod.chain" : kind == EmgAnchorConst.Bounce ? "grm.mod.bounce" : "grm.mod.pierce";
                foreach (int di in decls)
                {
                    EmgDeclRow d = c.Decls[di];
                    if (d.Dir == EmgDirs.Write && PortIds.TryGetValue(d.Port, out string port) && port == want)
                    {
                        return (int)Math.Round(d.Amount);
                    }
                }
            }
            return kind == EmgAnchorConst.Return || kind == EmgAnchorConst.Wire ? 2 : kind >= EmgAnchorConst.Follow ? 1 : 4;
        }

        // ── 8. 熔合线索的端口对（EMG3-FUSE-01；FGR-EMG-044“两父的端口在同一条因果链里同现”；FG05 FGR-RND-044 线索来源改写）──

        private static EmgCompiled _clueFor;
        private static readonly Dictionary<string, HashSet<ushort>> CluePorts = new Dictionary<string, HashSet<ushort>>(StringComparer.Ordinal);

        /// <summary>
        /// 一条父固件的线索端口 = 它声明的写端口（语法结构 grm.anchor / grm.inherit 与语法修饰 grm.mod.* 除外：fw_overload / fw_capacitor 都写 grm.mod.power，不能让 [电容, 漏油] 算闪燃过载）
        /// + 节拍族读端（时钟脉冲按 tmp.beat 认，FG19A 第 4.3 节线索端口对列）。按端口，不按固件 ID（FGR-EMG-013）。
        /// </summary>
        public static HashSet<ushort> CluePortsOf(string owner)
        {
            EmgCompiled c = EmgCatalog.Current;
            EnsureIndex(c);
            if (!ReferenceEquals(_clueFor, c))
            {
                CluePorts.Clear();
                _clueFor = c;
            }
            if (string.IsNullOrEmpty(owner))
            {
                return new HashSet<ushort>();
            }
            if (CluePorts.TryGetValue(owner, out HashSet<ushort> set))
            {
                return set;
            }
            set = new HashSet<ushort>();
            if (c.OwnerIndex.TryGetValue(owner, out int oi) && DeclsByOwner.TryGetValue(oi, out List<int> all))
            {
                foreach (int di in all)
                {
                    if (IsCluePort(c.Decls[di]))
                    {
                        set.Add(ClueCodeOf(c.Decls[di]));
                    }
                }
            }
            CluePorts[owner] = set;
            return set;
        }

        /// <summary>
        /// 线索端口码：地面液体端口的 modify 写端（蔓延的“液体扩散”，FG19A 第 236 行）记作 <see cref="EmgConst.PortClueSpread"/>，不记成那种液体的排放——
        /// 只装 [冷却液, 电弧] 不算漫流电弧、[蔓延, 电弧] 不算湿弧（DEBT-EMG3EXIT-01 / FG-GAP-206 ③）。按端口 + 角色，不看固件 ID（FGR-EMG-013）。
        /// </summary>
        private static ushort ClueCodeOf(EmgDeclRow d) =>
            d.Role == EmgRole.Modify && PortIds.TryGetValue(d.Port, out string id) && id.StartsWith("mat.", StringComparison.Ordinal) ? EmgConst.PortClueSpread : d.Port;

        /// <summary>线索端口口径（父固件与程序里的指令同一口径）：写端口（grm.anchor / grm.inherit / grm.mod.* 除外）或节拍族读端。</summary>
        private static bool IsCluePort(EmgDeclRow d)
        {
            if (!PortIds.TryGetValue(d.Port, out string port) || port == "grm.anchor" || port == "grm.inherit" || port.StartsWith("grm.mod.", StringComparison.Ordinal))
            {
                return false;
            }
            return d.Dir == EmgDirs.Write || EmgCatalog.FamilyOf(port) == EmgFamily.Tempo;
        }

        /// <summary>
        /// EMG4-FUSE-02（DEBT-EMG3EXIT-01；FG19A 第 4.3 节“线索端口对”列）：配方 <paramref name="r"/> 的线索端口对两侧的端口码集合。
        /// 端口写明的一侧只认那个端口；"mat.*" 一侧 = 4.3 节“液体扩散”：只认液体端口的 modify 写端（<see cref="EmgConst.PortClueSpread"/>，内核在蔓延真的扩开斑块时记），不认任一液体排放。
        /// 侧与父的对应：clueA 归父 A、clueB 归父 B；父 A 没有 clueA 而父 B 有（4.3 节 A / B 与配方表相反的几条）时两侧交换。按端口，不看固件 ID（FGR-EMG-013）。
        /// 端口对没登记 / 端口不在表里 → 两侧为空（这条配方不计线索，fgdata_fusion validate 保证 30 条都登记）。
        /// </summary>
        public static void CluePairOf(Economy.FusionRecipeDef r, out HashSet<ushort> sideA, out HashSet<ushort> sideB)
        {
            sideA = new HashSet<ushort>();
            sideB = new HashSet<ushort>();
            if (r == null || string.IsNullOrEmpty(r.ClueA) || string.IsNullOrEmpty(r.ClueB))
            {
                return;
            }
            HashSet<ushort> pa = CluePortsOf(r.ParentA), pb = CluePortsOf(r.ParentB);
            bool swap = !SideHas(pa, r.ClueA) && SideHas(pb, r.ClueA) && SideHas(pa, r.ClueB);
            Side(sideA, r.ClueA, swap ? pb : pa);
            Side(sideB, r.ClueB, swap ? pa : pb);
        }

        private static bool SideHas(HashSet<ushort> ports, string want)
        {
            if (want == "mat.*")
            {
                return ports.Contains(EmgConst.PortClueSpread);
            }
            return PortCodeOf(want, out ushort code) && ports.Contains(code);
        }

        private static bool PortCodeOf(string id, out ushort code)
        {
            EmgCompiled c = EmgCatalog.Current;
            bool ok = c.PortIndexById.TryGetValue(id, out int pi) && pi >= 0 && pi < c.Ports.Length;
            code = ok ? c.Ports[pi].Code : (ushort)0;
            return ok;
        }

        private static void Side(HashSet<ushort> into, string want, HashSet<ushort> parent)
        {
            if (want == "mat.*")
            {
                if (parent.Contains(EmgConst.PortClueSpread))
                {
                    into.Add(EmgConst.PortClueSpread); // 4.3 节“液体扩散”= 液体端口的 modify 写端（蔓延），不是该父任一液体排放
                }
            }
            else if (PortCodeOf(want, out ushort code))
            {
                into.Add(code);
            }
        }

        /// <summary>
        /// 程序一次出手里同现的熔合配方端口对（位 i = 配方书第 i 条，FusionCatalog.Recipes 顺序，≤ 32）：两条不同的、未溢出的指令分别带端口对的一侧与另一侧
        /// （EMG4-FUSE-02：按 FG19A 第 4.3 节逐条端口对，<see cref="CluePairOf"/>；原来按“父 A 任一线索端口 × 父 B 任一线索端口”，13 条配方比设计宽）。
        /// 熔合芯片自己展开的两条也算（一槽两指令）；位只表示“这一发可能同现”，内核在链上两侧端口真的出现时才计数（EmgClueSeen）。O(配方 × 指令²)，只在装配变化时。
        /// </summary>
        private static uint ClueMaskOf(IReadOnlyList<EmgProgramStep> steps, EmgCompiled c)
        {
            IReadOnlyList<Economy.FusionRecipeDef> recipes = Economy.FusionCatalog.Recipes;
            if (recipes == null || recipes.Count == 0 || steps.Count < 2)
            {
                return 0u;
            }
            var portsOf = new List<HashSet<ushort>>(steps.Count);
            foreach (EmgProgramStep s in steps)
            {
                var ps = new HashSet<ushort>();
                if (!s.Overflow && s.Kind != EmgStepKind.Anchor)
                {
                    foreach (int di in s.Decls)
                    {
                        if (IsCluePort(c.Decls[di])) // 读端（节拍族除外）不算同现：只读 mat.fire 的指令不等于链上有明火
                        {
                            ps.Add(ClueCodeOf(c.Decls[di]));
                        }
                    }
                }
                portsOf.Add(ps);
            }
            uint mask = 0u;
            for (int i = 0; i < recipes.Count && i < 32; i++)
            {
                CluePairOf(recipes[i], out HashSet<ushort> pa, out HashSet<ushort> pb);
                if (pa.Count == 0 || pb.Count == 0)
                {
                    continue;
                }
                for (int x = 0; x < portsOf.Count && (mask & (1u << i)) == 0; x++)
                {
                    if (!portsOf[x].Overlaps(pa))
                    {
                        continue;
                    }
                    for (int y = 0; y < portsOf.Count; y++)
                    {
                        if (y != x && portsOf[y].Overlaps(pb))
                        {
                            mask |= 1u << i;
                            break;
                        }
                    }
                }
            }
            return mask;
        }
    }
}
