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
        /// <summary>程序序号（0 起，= 在指令序列里的位置）。</summary>
        public int Ordinal;
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
        public int ContentVersion;
        public byte Carrier;
        /// <summary>EMG1-GRM-01：主组件可抛射（false = 光束：lob 动词不执行，给原因 emg.compile.lob_straight）。</summary>
        public bool LobAllowed = true;
        /// <summary>热量分项（FGR-EMG-016）：指令表值、声明 heat、触发节点（trigger_gen × 代号）、子载荷份数 × per_child。</summary>
        public float HeatInstr, HeatDecl, HeatTrigger, HeatChildren;
        /// <summary>溢出指令的表值热量：旧积热（BlueprintCircuitBoard.ComputeFirmwareHeat）按装着的固件算过，溢出“不计热量”所以要扣回。</summary>
        public float HeatOverflowInstr;
        /// <summary>一次根出手全树的子载荷份数（逐代份数相乘后求和）。</summary>
        public int ChildPayloads;
        /// <summary>程序完整字节键的 64 位哈希 + 内容版本（同一程序同一内容版本同签名，IC-REQ-010）。空程序为空串。</summary>
        public string Signature = string.Empty;
        public bool IsEmpty => Steps.Count == 0;
        public float HeatTotal => HeatInstr + HeatDecl + HeatTrigger + HeatChildren;
        /// <summary>武器每发积热要在旧算法之上加的量（旧算法已含指令表值，这里只加新分项并扣回溢出指令）。</summary>
        public float WeaponHeatDelta => HeatDecl + HeatTrigger + HeatChildren - HeatOverflowInstr;
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

        private const ushort EventPortBase = EmgConst.EventPortBase;

        /// <summary>单位级事件（FGR-EMG-031 第 0 步、FG19A A3）：阵亡、被击、过热、冷却完成、接入、断开、拾取、夺控、归还。</summary>
        private static bool IsUnitLevel(EmgEventKind e) => e == EmgEventKind.Death || e == EmgEventKind.Hurt || e == EmgEventKind.Overheat
                                                         || e == EmgEventKind.Cooled || e == EmgEventKind.Uplink || e == EmgEventKind.Unlink
                                                         || e == EmgEventKind.Pickup || e == EmgEventKind.Seize || e == EmgEventKind.Release;

        /// <summary>根节点触发读这些事件 = 程序需要飞行（ADR ③）。</summary>
        private static bool NeedsFlightEvent(EmgEventKind e) => e == EmgEventKind.Timer || e == EmgEventKind.Prox || e == EmgEventKind.Expire
                                                              || e == EmgEventKind.Bounce || e == EmgEventKind.Pierce;

        /// <summary>根节点写这些端口 = 程序需要飞行（ADR ③）。EMG1-GRM-01：加上 grm.mod.multi——霰射“一次打出 3 份真实弹丸，每份各自命中、各自触发”只有飞行弹体做得到（待用户复核）。</summary>
        private static readonly string[] FlightMods =
            { "grm.mod.homing", "grm.mod.pierce", "grm.mod.bounce", "grm.mod.return", "grm.mod.orbit", "grm.mod.lob", "grm.mod.drift", "grm.mod.multi" };

        /// <summary>EMG1-GRM-01：光束类主组件（作战组件表 flightLob = 0）上的曲射不越障，预览写原因（FG19A 第 2.1.1 节）。</summary>
        public const string NoteLobStraight = "emg.compile.lob_straight";

        /// <summary>EMG1-FW-02（FG-GAP-177）：声明的适用载体（carriers）不含当前主组件 → 这条指令整条不编译，预览写原因（仍占程序位置）。</summary>
        public const string NoteCarrierNa = "emg.compile.carrier_na";

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
        public static EmgProgramResult Compile(BlueprintCircuitPreview p, ICollection<string> skipUplink)
        {
            if (p == null)
            {
                return new EmgProgramResult();
            }
            CarrierReadings.TryGetCarrier(p.PrimaryId, out FirmwareCarrier carrier); // 查不到时是射弹（与读法同一默认）
            bool lob = !CarrierReadings.TryGetComponent(p.PrimaryId, out GameConfig.fg.CombatComponent comp) || comp.Flight != "yes" || comp.FlightLob != 0;
            return Compile(p.UplinkFirmwareIds, p.LocalFirmwareIds, carrier, 0, lob, skipUplink);
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
                if (!s.Overflow && !(noCore && s.Core))
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
                RootCapacity = r.RootCapacity, ContentVersion = r.ContentVersion, Carrier = r.Carrier, LobAllowed = r.LobAllowed,
                HeatInstr = r.HeatInstr, HeatDecl = r.HeatDecl, HeatTrigger = r.HeatTrigger, HeatChildren = r.HeatChildren, HeatOverflowInstr = r.HeatOverflowInstr,
                ChildPayloads = r.ChildPayloads,
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
            // 根节点的触发读端还在、它放出的子节点读的是“飞行中”事件 → 仍需飞行（与编译器同一口径）
            foreach (EmgNode n in t.KernelNodes)
            {
                flight |= n.Parent == 0 && NeedsFlightEvent(n.TrigEvent) && (listen & (1u << (int)n.TrigEvent)) != 0;
            }
            t.Program = new EmgProgram
            {
                Flags = (r.Program.Flags & ~(EmgProgramFlags.NeedsFlight | EmgProgramFlags.HasCore))
                        | (flight ? EmgProgramFlags.NeedsFlight : EmgProgramFlags.None) | (core ? EmgProgramFlags.HasCore : EmgProgramFlags.None),
                ListenMask = listen, HeatPerShot = r.Program.HeatPerShot,
            };
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
                "en.heat" => new[] { "heatburst" },
                "en.kinetic" => new[] { "echo" },
                "ent.drone" => new[] { "inherit" },
                "ent.projectile" => new[] { "feedback" },
                "prop.integrity" or "phs.corrode" => new[] { "explode" },
                "prop.conductive" => new[] { "weave" },
                _ => Array.Empty<string>(),
            };
        }

        private static readonly string[] MatterTransition = { "linger", "trail" };

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
            void Keep(string fw, string field)
            {
                if (!keep.TryGetValue(fw, out HashSet<string> set))
                {
                    keep[fw] = set = new HashSet<string>(StringComparer.Ordinal);
                }
                set.Add(field);
            }
            foreach (EmgNode n in r.KernelNodes)
            {
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
                    // shortcut: 子载荷自己执行这些效果动词随阶段 2 斑块 / 传导去掉过渡字段时一起做（DEBT-EMG1FW01-03）。
                    if (fam != EmgFamily.Grammar && fam != EmgFamily.Meta && (n.Parent < 0 || fam != EmgFamily.Matter))
                    {
                        // EMG1-FIX-01：根节点上由内核直接执行的写端（RootKernel，如追射 ent.projectile）不再保留旧读法过渡字段（否则旧回波与真补射两轨同时结算，FGR-EMG-019）
                        bool kernelRoot = n.Parent < 0 && (x.Flags & EmgInstrFlags.RootKernel) != 0;
                        foreach (string f in kernelRoot ? Array.Empty<string>() : TransitionFields(pid))
                        {
                            Keep(fw, f);
                        }
                        Keep(fw, TagsField);
                    }
                }
            }
            return (fw, field) => fw != null && onDecl.Contains(fw) && !(keep.TryGetValue(fw, out HashSet<string> k) && k.Contains(field));
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
        public static EmgProgramResult Compile(IReadOnlyList<string> uplinkFirmwareIds, IReadOnlyList<string> localFirmwareIds, FirmwareCarrier carrier, int rootCapacity = 0,
            bool lobAllowed = true, ICollection<string> skipUplink = null)
        {
            EmgCompiled c = EmgCatalog.Current;
            EnsureIndex(c);
            var r = new EmgProgramResult
            {
                RootCapacity = rootCapacity > 0 ? rootCapacity : BlueprintCircuitBoard.FirmwareSlotCount + UplinkCompiler.Quota,
                ContentVersion = CampaignSaveService.CurrentContentVersion,
                Carrier = (byte)carrier,
                LobAllowed = lobAllowed,
            };
            AddSlots(r, c, uplinkFirmwareIds, true, skipUplink);
            AddSlots(r, c, localFirmwareIds, false, null);
            if (r.IsEmpty)
            {
                return r; // 全空程序：武器照常基础攻击，不登记程序（Program = −1）
            }
            BuildTree(r);
            Inherit(r, c);
            Emit(r, c);
            return r;
        }

        /// <summary>原因的玩家文本（UX-01 的程序树预览也用它）。</summary>
        public static string NoteText(EmgProgramResult r, in EmgCompileNote n)
        {
            string name = n.Ordinal >= 0 && n.Ordinal < r.Steps.Count ? FirmwareKinds.DisplayName(r.Steps[n.Ordinal].OwnerId) ?? r.Steps[n.Ordinal].OwnerId : string.Empty;
            return GameText.Format(n.Key, name, n.Node);
        }

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
                    AddOwner(r, c, id, a, fromUplink, core);
                    AddOwner(r, c, id, b, fromUplink, core);
                }
                else
                {
                    AddOwner(r, c, id, id, fromUplink, core);
                }
            }
        }

        private static void AddOwner(EmgProgramResult r, EmgCompiled c, string firmwareId, string owner, bool fromUplink, bool core)
        {
            if (string.IsNullOrEmpty(owner) || !c.OwnerIndex.TryGetValue(owner, out int oi) || !DeclsByOwner.TryGetValue(oi, out List<int> all)
                || all.Count == 0 || c.Decls[all[0]].StageGate > c.EffectiveStage)
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
            foreach (int di in all)
            {
                EmgDeclRow d = c.Decls[di];
                if ((d.Carriers & bit) == 0)
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

        // ── 2. 节点分配（FGR-EMG-031、032）──

        private static int Cap(EmgProgramResult r, int gen) => Math.Max(1, r.RootCapacity >> gen);

        private static void BuildTree(EmgProgramResult r)
        {
            EmgTreeNode root = NewNode(r, -1, 0, null);
            foreach (EmgProgramStep s in r.Steps)
            {
                if (!s.Promoted)
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
                if (s.Promoted)
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
                widened |= !s.Overflow && HasWrite(c, s, "grm.inherit");
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
            bool widened = false;
            foreach (EmgProgramStep s in r.Steps)
            {
                widened |= !s.Overflow && HasWrite(c, s, "grm.inherit");
            }
            float trigGen = Tuning("emg.heat.trigger_gen", 2f), perChild = Tuning("emg.heat.per_child", 1f);
            var mult = new int[r.Nodes.Count];
            var keys = new Dictionary<(ushort port, EmgRole role, ushort evt, float filter), int>(8);
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
                    if (e.Inherited)
                    {
                        inheritMask |= (byte)(1 << r.Nodes[e.FromNode].Gen);
                    }
                    else
                    {
                        r.HeatInstr += s.TableHeat;
                    }
                    foreach (int di in s.Decls)
                    {
                        EmgDeclRow d = c.Decls[di];
                        string port = PortIds.TryGetValue(d.Port, out string pid) ? pid : string.Empty;
                        bool write = d.Dir == EmgDirs.Write;
                        // 默认只继承修饰写端；效果写端只在程序里有 grm.inherit 时一并继承（FG19A 第 0.3 节口径 5）。读端随修饰一并带上。
                        if (write && e.Inherited && !widened && !IsMod(port))
                        {
                            continue;
                        }
                        if (write && !IsStructural(port))
                        {
                            var key = (d.Port, d.Role, s.EventPort, port == "grm.mod.bounce" ? d.Param2 : 0f);
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
                        }
                        var x = new EmgInstr
                        {
                            Port = d.Port, Role = (byte)d.Role, Ordinal = (byte)s.Ordinal, Amount = d.Amount, Param2 = d.Param2,
                            Flags = (e.Inherited ? EmgInstrFlags.Inherited : EmgInstrFlags.None) | (s.Core ? EmgInstrFlags.Core : EmgInstrFlags.None)
                                    | (write ? PreferenceOf(c, s, port) : EmgInstrFlags.None),
                        };
                        int pcKey = d.Port << 3 | ResolveCarrier(c, r.Carrier, n.Parent);
                        // EMG1-FW-01：物质端口同一对有排放 / 扩散两行，按声明角色选行（modify = 扩散 / 随车，emit = 排放；FG19A 第 3.2 节）
                        if (write && (d.Role == EmgRole.Modify && SpreadAt.TryGetValue(pcKey, out int pci) || PortCarrierAt.TryGetValue(pcKey, out pci)))
                        {
                            EmgPortCarrierRow pc = c.PortCarriers[pci];
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
                            if (n.Parent < 0 && x.Verb != 0 && (IsRootKernelOwner(s.OwnerId) || IsRootKernelPort(port)) && EmgCatalog.FamilyOf(port) is var rkFam && rkFam != EmgFamily.Grammar && rkFam != EmgFamily.Meta)
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
            r.Program = new EmgProgram
            {
                Flags = (flight ? EmgProgramFlags.NeedsFlight : EmgProgramFlags.None) | (core ? EmgProgramFlags.HasCore : EmgProgramFlags.None),
                ListenMask = listen, HeatPerShot = r.HeatTotal,
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
            foreach ((string field, float mag) in CarrierReadings.FieldsOf(firmwareId))
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
        public static bool IsRootKernelPort(string port) => port == "ent.projectile";

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
    }
}
