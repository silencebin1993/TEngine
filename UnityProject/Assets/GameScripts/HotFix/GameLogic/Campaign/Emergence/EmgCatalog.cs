using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using TEngine;

namespace GameLogic.Campaign.Emergence
{
    /// <summary>13 个通道族（FG19 第 2.3 节）。</summary>
    public enum EmgFamily : byte
    {
        None = 0, Grammar, Meta, Event, Matter, Energy, Entity, Field, Squad, Phase, Chassis, Terrain, Hijack, Tempo,
    }

    [Flags]
    public enum EmgDirs : byte
    {
        None = 0, Read = 1, Write = 2, Both = 3,
    }

    /// <summary>声明角色（FGR-EMG-011）：前 4 个是读端，后 5 个是写端。</summary>
    public enum EmgRole : byte
    {
        None = 0, Trigger, Condition, Sense, Scope, Emit, Modify, Spawn, Inject, Apply,
    }

    /// <summary>底盘特性的机制枚举（ADR-EMG-001 ⑫）：内核只认它 + 4 个参数，“哪种底盘用哪条特性”只在表里（FGR-EMG-141）。</summary>
    public enum EmgTraitKind : byte
    {
        None = 0, TrackDrag, WheelTow, HoverFloat, FixedAnchor, LeggedStomp, AirDrop, HullDraw,
    }

    /// <summary>端口行（内核整数形式）。</summary>
    public struct EmgPortRow
    {
        public const byte FlagNumericOnly = 1, FlagPerceive = 2, FlagCarrierImpl = 4;
        public ushort Code;
        public EmgFamily Family;
        public EmgDirs Dirs;
        public byte Flags;
        public byte Stage;
    }

    /// <summary>声明行（内核整数形式；Owner = <see cref="EmgCompiled.OwnerIds"/> 下标，字符串 ID 只留在热更层）。</summary>
    public struct EmgDeclRow
    {
        public int Owner;
        public byte Seq;
        public ushort Port;
        public EmgDirs Dir;
        public EmgRole Role;
        public float Amount;
        public float Param2;
        public byte Carriers; // 位 0～4 = projectile/melee/summon/aura/field；全部 = 0x1F
        public float Heat;
        public byte StageGate;
    }

    /// <summary>端口 × 载体 → 内核动词（FGR-EMG-018）。Verb = <see cref="EmgCatalog.Verbs"/> 下标 + 1（前 25 个与 CarrierReadingVerb 同值）。</summary>
    public struct EmgPortCarrierRow
    {
        public ushort Port;
        public byte Carrier;
        public byte Verb;
        public float A, B, C;
        public byte Mag; // 0 none / 1 a / 2 b / 3 c
    }

    /// <summary>子形态（FGR-EMG-035；EmgNode.SubForm = Code）。</summary>
    public struct EmgSubFormRow
    {
        public byte Code;
        public byte Carrier;
        public float Speed, Life, LifeRatio, Radius, RadiusRatio, Kinetic;
        public bool KineticInherit;
    }

    /// <summary>底盘特性（FGR-EMG-140）：机制 + 设置的 chs.* 状态端口码 + 4 个参数（取自 fg.TbHomeTuning 的 emg.trait.*）。</summary>
    public struct EmgTraitRow
    {
        public EmgTraitKind Kind;
        public ushort StatePort0, StatePort1;
        public float P0, P1, P2, P3;
        public byte Stage;
        public bool Reserved;
    }

    /// <summary>编译输入（与五张表一一对应的平铺行）：正式路径由 <see cref="EmgCatalog.FromTables"/> 从 Luban 表生成；自检直接构造坏行做负向矩阵。</summary>
    public sealed class EmgSource
    {
        public sealed class Port { public string Id, Family, Dirs, Unit, NameKey, DescKey, Shape, Color; public int Code, NumericOnly, Perceive, CarrierImpl, Stage; }
        public sealed class Decl { public string Id, Owner, OwnerKind, Dir, Port, Role, Carriers, TextKey; public int Seq, StageGate; public float Amount, Param2, Heat; }
        public sealed class PortCarrier { public string Id, Port, Carrier, Verb, Mag, TextKey; public float A, B, C; }
        public sealed class Sub { public string Id, Carrier, NameKey; public int Code, KineticInherit; public float Speed, Life, LifeRatio, Radius, RadiusRatio, Kinetic; }
        public sealed class Trait { public string Id, Chassis, Kind, AppliesTo, StatePorts, ParamKeys, NameKey, DescKey; public int Stage, Reserved; }

        public readonly List<Port> Ports = new List<Port>();
        public readonly List<Decl> Decls = new List<Decl>();
        public readonly List<PortCarrier> PortCarriers = new List<PortCarrier>();
        public readonly List<Sub> Subs = new List<Sub>();
        public readonly List<Trait> Traits = new List<Trait>();
        /// <summary>调参查询（emg.stage、emg.trait.*）；返回 false = 表里没有。</summary>
        public Func<string, (bool ok, float value)> Tuning = id => GridContent.TryGetTuning(id, out float v) ? (true, v) : (false, 0f);
        /// <summary>底盘 ID 是否存在（非预留底盘的外键）。</summary>
        public Func<string, bool> ChassisExists = id => Content.ChassisCatalog.TryGet(id, out _) || id == Content.CarrierReadings.FixedChassisId;
        /// <summary>声明 owner 外键：按 ownerKind 查对应的内容目录（底盘另由 <see cref="ChassisExists"/> + 预留底盘特性行判定）。</summary>
        public Func<string, string, bool> OwnerExists = DefaultOwnerExists;

        private static bool DefaultOwnerExists(string owner, string kind)
        {
            switch (kind)
            {
                case "firmware": return Signal.FirmwareKinds.TryGetRow(owner, out _);
                case "component":
                case "structure": return Content.ComponentCatalog.TryGet(owner, out _);
                case "enemy":
                    try
                    {
                        return ConfigSystem.Instance.Tables.TbMechEnemy.GetOrDefault(owner) != null;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                default: return false;
            }
        }
    }

    /// <summary>编译结果：内核整数数组 + 热更层的字符串索引 + 问题清单。规则 / 声明是内容不是状态，不进快照；表重载时整份重建（ADR-EMG-001 ①）。</summary>
    public sealed class EmgCompiled
    {
        public EmgPortRow[] Ports = Array.Empty<EmgPortRow>();
        public EmgDeclRow[] Decls = Array.Empty<EmgDeclRow>();
        public EmgPortCarrierRow[] PortCarriers = Array.Empty<EmgPortCarrierRow>();
        public EmgSubFormRow[] SubForms = Array.Empty<EmgSubFormRow>();
        public EmgTraitRow[] Traits = Array.Empty<EmgTraitRow>();
        public string[] OwnerIds = Array.Empty<string>();
        public readonly Dictionary<string, int> PortIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly Dictionary<ushort, string> PortIdByCode = new Dictionary<ushort, string>();
        public readonly Dictionary<string, int> OwnerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly Dictionary<string, int> TraitByChassis = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>carrierImpl 写端口却没有任何 fg.TbPortCarrier 行、也没有声明写到它的端口：FGR-EMG-015 规则三的“已登记缺口”（不静默通过，由承接卡清零）。</summary>
        public readonly List<string> RegisteredGaps = new List<string>();
        public readonly List<string> Problems = new List<string>();
        public float RawStage;
        public int EffectiveStage;
    }

    /// <summary>
    /// EMG1-DATA-01（FG19 FGR-EMG-010～013、018～021、035、140；ADR-EMG-001 ①、⑮ 第 4 步 1a）：把 fg.TbChannelPort / TbChannelDecl / TbPortCarrier /
    /// TbSubPayload / TbChassisTrait 编成内核只认整数码的数组，并跑运行时版本的表校验——是 tools/cell_tables/check_luban.py R37～R41 的子集，以 check_luban 为准
    ///   （运行时不查：端口 unit 列、R41 调参取值范围；枚举名大小写不敏感）。坏行一律不进数组。
    /// - 本卡不接战斗：没有任何战斗代码读这些数组；正式表 emg.stage = 0，全部 owner 走旧 readFields 路径（行为逐字不变）。
    ///   EMG1-KRN-01 铺好阶段 1 内核结构（程序 / 节点 / 指令表、总线、残骸、快照 16）后，<see cref="ImplementedStage"/> 提到 1：emg.stage 允许填 1（对照测试 / 回滚开关），
    ///   表里缺省仍是 0——新轨（编译器、总线消费、触发器、动词）在 EMG1-PRG-01 / BUS-01 / TRIG-01 / GRM-01 接通前不得把缺省提到 1（裁决见 EMG1-KRN-01 证据“待用户复核”）。
    /// - 规则只认端口 × 端口：这里不出现任何 fw_ / comp_ / func_ / struct_ 判断（FGR-EMG-013，FgEmergenceRuleAuditSelfCheck 扫本文件）。
    /// 只在载入 / 表重载时编译（O(表行数)），不按帧。
    /// </summary>
    public static class EmgCatalog
    {
        /// <summary>已实现的最高 FG-EMG 阶段：emg.stage 填得更高时钳到这里（卡片负向“emg.stage 填高于已实现阶段”）。EMG1-KRN-01 铺好阶段 1 内核结构后为 1；阶段 2 内核（快照 17）落地时提到 2。</summary>
        public const int ImplementedStage = 1;
        public const string StageKey = "emg.stage";

        public static readonly string[] Carriers = { "projectile", "melee", "summon", "aura", "field" };
        public const byte AllCarriers = 0x1F;

        /// <summary>动词清单：前 25 个 = CarrierReadingVerb（Dmg=1……Cone=25），其后是 FG19A 第 3.1 节新动词（与 fgdata_reading.ALL_PORT_VERBS 同序）。
        /// 码 = 下标 + 1，内核枚举 <see cref="BinGames.Sim.Combat.EmgVerb"/> 与之同值（EMG1-GRM-01；FgEmgGrammarSelfCheck A 段逐个比对）。</summary>
        public static readonly string[] Verbs =
        {
            "dmg", "cooldown", "extra", "pierce", "chain", "blast", "sweep", "echo", "pull", "knock", "lunge", "armor", "execute", "leech", "amp",
            "zone", "zonegrow", "zonetick", "weave", "escort", "drones", "heatburst", "jump", "reach", "cone",
            "steer", "passthrough", "ricochet", "multi", "return", "orbit", "lob", "drift", "subpayload", "mark", "emit", "spread",
            "inject", "spawn", "fragment", "salvage", "mass", "link", "field", "inherit",
        };

        /// <summary>内容 ID 前缀（与 fgdata_emg.CONTENT_ID_PREFIXES 同）：规则 / 声明的非 owner 列不得出现。</summary>
        public static readonly string[] ContentIdPrefixes = { "fw_", "comp_", "func_", "struct_", "chassis_", "gene_", "org_", "reaction_", "enemy_" };

        /// <summary>恒真事件：作 trigger 时不计准入读端（FGR-EMG-014 第 2 条）。</summary>
        public static readonly string[] AlwaysTrueTriggers = { "evt.fire", "evt.beat", "tmp.beat" };

        private static readonly Regex PortIdPattern = new Regex(@"^[a-z]+(\.[a-z_]+)+$", RegexOptions.CultureInvariant);

        private static readonly (string prefix, EmgFamily family)[] Prefixes =
        {
            ("grm.", EmgFamily.Grammar), ("meta.", EmgFamily.Meta), ("evt.", EmgFamily.Event), ("mat.", EmgFamily.Matter), ("en.", EmgFamily.Energy),
            ("ent.", EmgFamily.Entity), ("prop.", EmgFamily.Entity), ("fld.", EmgFamily.Field), ("sqd.", EmgFamily.Squad), ("phs.", EmgFamily.Phase),
            ("chs.", EmgFamily.Chassis), ("ter.", EmgFamily.Terrain), ("hij.", EmgFamily.Hijack), ("tmp.", EmgFamily.Tempo),
        };

        private static EmgCompiled _current;
        private static EmgSource _override;

        public static int Revision { get; private set; }

        /// <summary>当前编译结果（懒加载；表重载后调 <see cref="Reload"/>）。</summary>
        public static EmgCompiled Current
        {
            get
            {
                if (_current == null)
                {
                    _current = Compile(_override ?? FromTables());
                    foreach (string p in _current.Problems)
                    {
                        Log.Error("[EmgCatalog] " + p);
                    }
                }
                return _current;
            }
        }

        public static void Reload()
        {
            _current = null;
            Revision++;
        }

        /// <summary>测试注入：用构造的输入代替五张表（null = 真实表）。用完必须 <see cref="ResetForTests"/>。</summary>
        public static void OverrideForTests(EmgSource source)
        {
            _override = source;
            Reload();
        }

        public static void ResetForTests() => OverrideForTests(null);

        /// <summary>此刻生效的 FG-EMG 阶段（emg.stage 钳到 0～<see cref="ImplementedStage"/>）。</summary>
        public static int EffectiveStage => Current.EffectiveStage;

        /// <summary>某 owner 的声明此刻是否生效（stageGate ≤ 生效阶段）；不生效时该 owner 继续走旧 readFields（FGR-EMG-019 双轨）。</summary>
        public static bool IsOwnerOnDeclTrack(string owner)
        {
            EmgCompiled c = Current;
            if (owner == null || !c.OwnerIndex.TryGetValue(owner, out int oi))
            {
                return false;
            }
            foreach (EmgDeclRow d in c.Decls)
            {
                if (d.Owner == oi)
                {
                    return d.StageGate <= c.EffectiveStage;
                }
            }
            return false;
        }

        public static int ClampStage(float raw, int implemented) => Math.Max(0, Math.Min(implemented, (int)Math.Round(raw)));

        public static EmgFamily FamilyOf(string portId)
        {
            if (string.IsNullOrEmpty(portId))
            {
                return EmgFamily.None;
            }
            foreach ((string prefix, EmgFamily family) in Prefixes)
            {
                if (portId.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return family;
                }
            }
            return EmgFamily.None;
        }

        public static EmgFamily ParseFamily(string s) => TryParseName(s, true, out EmgFamily f) && f != EmgFamily.None ? f : EmgFamily.None;

        /// <summary>FG19 第 2.7 节：状态端口读的时候只能作 condition（与 fgdata_emg.CONDITION_ONLY_PORTS 同表）。</summary>
        public static readonly string[] ConditionOnlyPorts = { "hij.hijacked", "hij.seized" };

        /// <summary>只认枚举名（Enum.TryParse 会接受 "3" 这类数字串，R37 不认）。</summary>
        private static bool TryParseName<T>(string s, bool ignoreCase, out T v) where T : struct
        {
            v = default;
            return !string.IsNullOrEmpty(s) && char.IsLetter(s[0]) && Enum.TryParse(s, ignoreCase, out v);
        }

        public static int SegmentBase(EmgFamily f) => f == EmgFamily.None ? -1 : 100 * (int)f;

        public static int FamilyStage(EmgFamily f) => f switch
        {
            EmgFamily.Grammar => 1, EmgFamily.Meta => 3, EmgFamily.Event => 1, EmgFamily.Matter => 2, EmgFamily.Energy => 2, EmgFamily.Entity => 1,
            EmgFamily.Field => 2, EmgFamily.Squad => 3, EmgFamily.Phase => 2, EmgFamily.Chassis => 3, EmgFamily.Terrain => 2, EmgFamily.Hijack => 4,
            EmgFamily.Tempo => 3, _ => 0,
        };

        /// <summary>FGR-EMG-010 推导规则（与 fgdata_emg.derive_port 是同一规则的两份独立实现，自检逐行比对表值）。</summary>
        public static (EmgDirs dirs, bool perceive, bool carrierImpl, int stage, bool numericOnly) DerivePort(string id, EmgFamily f)
        {
            EmgDirs dirs = f == EmgFamily.Grammar || f == EmgFamily.Meta ? EmgDirs.Write
                : f == EmgFamily.Event ? (id == "evt.marked" || id == "evt.ping" ? EmgDirs.Both : EmgDirs.Read)
                : EmgDirs.Both;
            bool perceive = f != EmgFamily.Grammar && f != EmgFamily.Meta;
            bool impl = f == EmgFamily.Grammar || f == EmgFamily.Matter || f == EmgFamily.Energy || f == EmgFamily.Field || f == EmgFamily.Entity;
            int stage = id switch
            {
                "ter.obstacle" or "ter.cover" or "fld.deflect" or "chs.moving" => 1,
                "grm.anchor" => 3,
                _ => FamilyStage(f),
            };
            bool numeric = id == "grm.mod.size" || id == "grm.mod.armor" || id == "grm.mod.power";
            return (dirs, perceive, impl, stage, numeric);
        }

        /// <summary>声明用到端口时 owner 的 stageGate 至少要多少：物质 / 能量 / 相态写端阶段 1 走过渡动词（FG19A 第 0.3 节口径 3）。</summary>
        public static int RequiredStage(int portStage, EmgFamily f, EmgDirs dir) =>
            dir == EmgDirs.Write && (f == EmgFamily.Matter || f == EmgFamily.Energy || f == EmgFamily.Phase) ? 1 : portStage;

        public static bool IsContentId(string v)
        {
            if (string.IsNullOrEmpty(v))
            {
                return false;
            }
            foreach (string p in ContentIdPrefixes)
            {
                if (v.StartsWith(p, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        public static EmgDirs ParseDirs(string s) => s switch { "read" => EmgDirs.Read, "write" => EmgDirs.Write, "both" => EmgDirs.Both, _ => EmgDirs.None };

        public static EmgRole ParseRole(string s) => TryParseName(s, true, out EmgRole r) ? r : EmgRole.None;

        public static bool IsReadRole(EmgRole r) => r >= EmgRole.Trigger && r <= EmgRole.Scope;

        public static bool IsWriteRole(EmgRole r) => r >= EmgRole.Emit && r <= EmgRole.Apply;

        public static int CarrierIndex(string c) => Array.IndexOf(Carriers, c);

        public static int VerbCode(string verb) => Array.IndexOf(Verbs, verb) + 1;

        /// <summary>
        /// FGR-EMG-014 准入（与 fgdata_emg.admit 同一规则）：至少 1 条感知读端（恒真事件作 trigger 不计）、至少 1 条非纯数值写端、
        /// 且读写分属至少 2 个通道族（prop.* 与 ent.* 同属实体族）。混合固件按展开后的两条父声明各判一次（<see cref="AdmitMixed"/>）。
        /// </summary>
        public static bool Admit(IReadOnlyList<(string port, EmgDirs dir, EmgRole role)> decls, IReadOnlyDictionary<string, EmgSource.Port> ports, out string reason)
        {
            var readFamilies = new List<EmgFamily>();
            var writeFamilies = new List<EmgFamily>();
            foreach ((string port, EmgDirs dir, EmgRole role) in decls)
            {
                if (port == null || !ports.TryGetValue(port, out EmgSource.Port p))
                {
                    continue;
                }
                EmgFamily f = FamilyOf(port);
                if (dir == EmgDirs.Read && p.Perceive == 1 && !(role == EmgRole.Trigger && Array.IndexOf(AlwaysTrueTriggers, port) >= 0))
                {
                    readFamilies.Add(f);
                }
                else if (dir == EmgDirs.Write && p.NumericOnly == 0)
                {
                    writeFamilies.Add(f);
                }
            }
            if (readFamilies.Count == 0)
            {
                reason = "没有感知读端（恒真事件作触发不计）";
                return false;
            }
            if (writeFamilies.Count == 0)
            {
                reason = "没有非纯数值写端（只改数字的不准入）";
                return false;
            }
            foreach (EmgFamily r in readFamilies)
            {
                foreach (EmgFamily w in writeFamilies)
                {
                    if (r != w)
                    {
                        reason = "准入";
                        return true;
                    }
                }
            }
            reason = "感知读端与写端在同一个通道族（至少接 2 个通道）";
            return false;
        }

        /// <summary>混合固件：两条父声明各自准入才准入（FGR-EMG-014“按展开后的两条父声明判”）。</summary>
        public static bool AdmitMixed(IReadOnlyList<(string, EmgDirs, EmgRole)> parentA, IReadOnlyList<(string, EmgDirs, EmgRole)> parentB,
            IReadOnlyDictionary<string, EmgSource.Port> ports, out string reason)
        {
            if (!Admit(parentA, ports, out reason))
            {
                reason = "第一条父固件：" + reason;
                return false;
            }
            if (!Admit(parentB, ports, out reason))
            {
                reason = "第二条父固件：" + reason;
                return false;
            }
            return true;
        }

        /// <summary>某 owner 在编译输入里的全部声明（准入用）。</summary>
        public static List<(string, EmgDirs, EmgRole)> DeclsOf(EmgSource src, string owner)
        {
            var list = new List<(string, EmgDirs, EmgRole)>();
            foreach (EmgSource.Decl d in src.Decls)
            {
                if (d.Owner == owner)
                {
                    list.Add((d.Port, ParseDirs(d.Dir), ParseRole(d.Role)));
                }
            }
            return list;
        }

        // ─────────────────────────────── 表 → 输入 ───────────────────────────────

        public static EmgSource FromTables()
        {
            var src = new EmgSource();
            GameConfig.Tables t = null;
            try
            {
                t = ConfigSystem.Instance.Tables;
            }
            catch (Exception e)
            {
                Log.Error("[EmgCatalog] 读取配置表失败：" + e.Message);
            }
            if (t?.TbChannelPort == null || t.TbChannelDecl == null || t.TbPortCarrier == null || t.TbSubPayload == null || t.TbChassisTrait == null)
            {
                return src;
            }
            foreach (ChannelPort r in t.TbChannelPort.DataList)
            {
                src.Ports.Add(new EmgSource.Port
                {
                    Id = r.Id, Family = r.Family, Code = r.Code, Dirs = r.Dirs, NumericOnly = r.NumericOnly, Perceive = r.Perceive, CarrierImpl = r.CarrierImpl,
                    Unit = r.Unit, NameKey = r.NameKey, DescKey = r.DescKey, Shape = r.Shape, Color = r.Color, Stage = r.Stage,
                });
            }
            foreach (ChannelDecl r in t.TbChannelDecl.DataList)
            {
                src.Decls.Add(new EmgSource.Decl
                {
                    Id = r.Id, Owner = r.Owner, OwnerKind = r.OwnerKind, Seq = r.Seq, Dir = r.Dir, Port = r.Port, Role = r.Role, Amount = r.Amount,
                    Param2 = r.Param2, Carriers = r.Carriers, Heat = r.Heat, TextKey = r.TextKey, StageGate = r.StageGate,
                });
            }
            foreach (PortCarrier r in t.TbPortCarrier.DataList)
            {
                src.PortCarriers.Add(new EmgSource.PortCarrier { Id = r.Id, Port = r.Port, Carrier = r.Carrier, Verb = r.Verb, A = r.A, B = r.B, C = r.C, Mag = r.Mag, TextKey = r.TextKey });
            }
            foreach (SubPayload r in t.TbSubPayload.DataList)
            {
                src.Subs.Add(new EmgSource.Sub
                {
                    Id = r.Id, Code = r.Code, Carrier = r.Carrier, Speed = r.Speed, Life = r.Life, LifeRatio = r.LifeRatio, Radius = r.Radius,
                    RadiusRatio = r.RadiusRatio, Kinetic = r.Kinetic, KineticInherit = r.KineticInherit, NameKey = r.NameKey,
                });
            }
            foreach (ChassisTrait r in t.TbChassisTrait.DataList)
            {
                src.Traits.Add(new EmgSource.Trait
                {
                    Id = r.Id, Chassis = r.Chassis, Kind = r.Kind, AppliesTo = r.AppliesTo, StatePorts = r.StatePorts, ParamKeys = r.ParamKeys,
                    Stage = r.Stage, Reserved = r.Reserved, NameKey = r.NameKey, DescKey = r.DescKey,
                });
            }
            return src;
        }

        // ─────────────────────────────── 编译与校验 ───────────────────────────────

        /// <summary>纯函数：输入 → 内核数组 + 问题清单。有问题的行不进数组（不残留半条）。O(表行数)。</summary>
        public static EmgCompiled Compile(EmgSource src)
        {
            var c = new EmgCompiled();
            List<string> bad = c.Problems;
            if (src == null || src.Ports.Count == 0)
            {
                bad.Add("fg.TbChannelPort 为空或不存在（改 tools/cell_tables/fgdata_emg.py 后重新生成）");
                return c;
            }
            var portById = new Dictionary<string, EmgSource.Port>(StringComparer.Ordinal);
            var codeOwner = new Dictionary<int, string>();
            var ports = new List<EmgPortRow>();
            foreach (EmgSource.Port p in src.Ports)
            {
                if (p == null)
                {
                    continue;
                }
                int before = bad.Count;
                CheckContentIds(bad, "端口", p.Id, ("family", p.Family), ("dirs", p.Dirs), ("unit", p.Unit), ("nameKey", p.NameKey), ("descKey", p.DescKey), ("shape", p.Shape), ("color", p.Color));
                if (bad.Count != before)
                {
                    continue;
                }
                EmgFamily fam = ParseFamily(p.Family);
                if (p.Id == null || !PortIdPattern.IsMatch(p.Id) || fam == EmgFamily.None || FamilyOf(p.Id) != fam)
                {
                    bad.Add($"端口 {p.Id} 的 ID 格式或族 {p.Family} 不对");
                    continue;
                }
                if (portById.ContainsKey(p.Id))
                {
                    bad.Add($"端口 {p.Id} 重复");
                    continue;
                }
                int seg = SegmentBase(fam);
                if (p.Code < seg || p.Code >= seg + 100)
                {
                    bad.Add($"端口 {p.Id} 码 {p.Code} 不在 {fam} 段 {seg}～{seg + 99}（跨段）");
                    continue;
                }
                if (codeOwner.TryGetValue(p.Code, out string other))
                {
                    bad.Add($"端口 {p.Id} 码 {p.Code} 与 {other} 复用（端口码永不复用）");
                    continue;
                }
                (EmgDirs dirs, bool perceive, bool impl, int stage, bool numeric) = DerivePort(p.Id, fam);
                if (ParseDirs(p.Dirs) != dirs || (p.Perceive == 1) != perceive || (p.CarrierImpl == 1) != impl || p.Stage != stage || (p.NumericOnly == 1) != numeric)
                {
                    bad.Add($"端口 {p.Id} 的 dirs/perceive/carrierImpl/stage/numericOnly 与 FGR-EMG-010 推导（{dirs}/{perceive}/{impl}/{stage}/{numeric}）不一致");
                    continue;
                }
                codeOwner[p.Code] = p.Id;
                portById[p.Id] = p;
                c.PortIndexById[p.Id] = ports.Count;
                c.PortIdByCode[(ushort)p.Code] = p.Id;
                ports.Add(new EmgPortRow
                {
                    Code = (ushort)p.Code, Family = fam, Dirs = dirs, Stage = (byte)stage,
                    Flags = (byte)((numeric ? EmgPortRow.FlagNumericOnly : 0) | (perceive ? EmgPortRow.FlagPerceive : 0) | (impl ? EmgPortRow.FlagCarrierImpl : 0)),
                });
            }
            c.Ports = ports.ToArray();

            // 声明
            var seen = new HashSet<(string, int)>();
            var gate = new Dictionary<string, int>(StringComparer.Ordinal);
            var kindOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var badOwners = new HashSet<string>(StringComparer.Ordinal);
            var written = new HashSet<string>(StringComparer.Ordinal);
            var decls = new List<EmgDeclRow>();
            var owners = new List<string>();
            foreach (EmgSource.Decl d in src.Decls)
            {
                if (d == null)
                {
                    continue;
                }
                string where = $"声明 ({d.Owner}, {d.Seq})";
                int before = bad.Count;
                CheckContentIds(bad, where, null, ("ownerKind", d.OwnerKind), ("dir", d.Dir), ("port", d.Port), ("role", d.Role), ("carriers", d.Carriers), ("textKey", d.TextKey));
                if (string.IsNullOrEmpty(d.Owner) || !OwnerMatchesKind(d.Owner, d.OwnerKind))
                {
                    bad.Add($"{where} owner 与种类 {d.OwnerKind} 不符");
                }
                else if (!(d.OwnerKind == "chassis"
                        ? src.ChassisExists(d.Owner) || src.Traits.Exists(t => t != null && t.Reserved == 1 && t.Chassis == d.Owner)
                        : src.OwnerExists(d.Owner, d.OwnerKind)))
                {
                    bad.Add($"{where} owner 不存在（外键断：{d.OwnerKind} 目录里没有 {d.Owner}）");
                }
                if (d.Seq < 1 || !seen.Add((d.Owner, d.Seq)))
                {
                    bad.Add($"{where} 序号 < 1 或 (owner, seq) 重复");
                }
                EmgDirs dir = ParseDirs(d.Dir);
                EmgRole role = ParseRole(d.Role);
                if (d.Port == null || !portById.TryGetValue(d.Port, out EmgSource.Port p))
                {
                    bad.Add($"{where} 引用未注册端口 {d.Port}");
                }
                else
                {
                    EmgFamily fam = FamilyOf(d.Port);
                    EmgDirs pd = ParseDirs(p.Dirs);
                    if ((dir != EmgDirs.Read && dir != EmgDirs.Write) || (pd & dir) == 0)
                    {
                        bad.Add($"{where} 方向 {d.Dir} 与端口 {d.Port} 的方向 {p.Dirs} 不符");
                    }
                    if ((dir == EmgDirs.Read ? !IsReadRole(role) : !IsWriteRole(role)) || (role == EmgRole.Trigger && fam != EmgFamily.Event)
                        || (role == EmgRole.Scope && d.Port != "sqd.relay")
                        || (dir == EmgDirs.Read && role != EmgRole.Condition && Array.IndexOf(ConditionOnlyPorts, d.Port) >= 0))
                    {
                        bad.Add($"{where} 角色 {d.Role} 不能用在 {d.Dir} {d.Port}");
                    }
                    if (d.StageGate < 1 || d.StageGate > 4)
                    {
                        bad.Add($"{where} stageGate={d.StageGate} 必须是 1～4");
                    }
                    else if (d.StageGate < RequiredStage(p.Stage, fam, dir))
                    {
                        bad.Add($"{where} stageGate={d.StageGate} 早于端口 {d.Port} 的可用阶段 {p.Stage}");
                    }
                }
                byte carriers = ParseCarriers(d.Carriers);
                if (carriers == 0)
                {
                    bad.Add($"{where} 适用载体 {d.Carriers} 不合法");
                }
                if (!(d.Amount >= 0f) || !(d.Param2 >= 0f) || !(d.Heat >= 0f))
                {
                    bad.Add($"{where} amount / param2 / heat 必须 >= 0");
                }
                if (d.Owner != null)
                {
                    if (gate.TryGetValue(d.Owner, out int g) && g != d.StageGate)
                    {
                        bad.Add($"{d.Owner} 的声明 stageGate 不一致（{g} 与 {d.StageGate}，FGR-EMG-019 整条切轨）");
                        badOwners.Add(d.Owner);
                    }
                    gate[d.Owner] = d.StageGate;
                    kindOf[d.Owner] = d.OwnerKind;
                }
                if (bad.Count != before || d.Owner == null)
                {
                    if (d.Owner != null)
                    {
                        badOwners.Add(d.Owner);
                    }
                    continue;
                }
                if (dir == EmgDirs.Write)
                {
                    written.Add(d.Port);
                }
                int oi = owners.IndexOf(d.Owner);
                if (oi < 0)
                {
                    oi = owners.Count;
                    owners.Add(d.Owner);
                }
                decls.Add(new EmgDeclRow
                {
                    Owner = oi, Seq = (byte)d.Seq, Port = (ushort)portById[d.Port].Code, Dir = dir, Role = role, Amount = d.Amount, Param2 = d.Param2,
                    Carriers = carriers, Heat = d.Heat, StageGate = (byte)d.StageGate,
                });
            }
            foreach (KeyValuePair<string, string> kv in kindOf)
            {
                if (kv.Value == "firmware" && !badOwners.Contains(kv.Key) && !Admit(DeclsOf(src, kv.Key), portById, out string why))
                {
                    bad.Add($"固件 {kv.Key} 未通过准入（FGR-EMG-014）：{why}");
                    badOwners.Add(kv.Key);
                }
            }
            // 整条切轨：一个 owner 有任何坏行，它的全部声明都不进数组（不留半条声明），owner 下标按留下的 owner 重新编号
            var kept = new List<string>();
            var finalDecls = new List<EmgDeclRow>(decls.Count);
            foreach (EmgDeclRow r in decls)
            {
                string owner = owners[r.Owner];
                if (badOwners.Contains(owner))
                {
                    continue;
                }
                if (!c.OwnerIndex.TryGetValue(owner, out int ni))
                {
                    ni = kept.Count;
                    kept.Add(owner);
                    c.OwnerIndex[owner] = ni;
                }
                EmgDeclRow row = r;
                row.Owner = ni;
                finalDecls.Add(row);
            }
            c.Decls = finalDecls.ToArray();
            c.OwnerIds = kept.ToArray();

            // 端口 × 载体
            var have = new HashSet<(string, int)>();
            var firstPair = new HashSet<(string, int)>();
            var pcs = new List<EmgPortCarrierRow>();
            foreach (EmgSource.PortCarrier r in src.PortCarriers)
            {
                if (r == null)
                {
                    continue;
                }
                int before = bad.Count;
                CheckContentIds(bad, "端口 × 载体 " + r.Id, null, ("port", r.Port), ("carrier", r.Carrier), ("verb", r.Verb), ("mag", r.Mag), ("textKey", r.TextKey));
                int car = CarrierIndex(r.Carrier);
                int verb = VerbCode(r.Verb);
                int mag = Array.IndexOf(new[] { "none", "a", "b", "c" }, r.Mag);
                if (r.Port == null || !portById.TryGetValue(r.Port, out EmgSource.Port p) || (ParseDirs(p.Dirs) & EmgDirs.Write) == 0)
                {
                    bad.Add($"端口 × 载体 {r.Id} 的端口 {r.Port} 未注册或不可写");
                }
                if (car < 0 || verb <= 0 || mag < 0 || !(r.A >= 0f) || !(r.B >= 0f) || !(r.C >= 0f))
                {
                    bad.Add($"端口 × 载体 {r.Id}：载体 {r.Carrier} / 动词 {r.Verb} / 幅度目标 {r.Mag} / 参数不合法");
                }
                if (bad.Count != before)
                {
                    continue;
                }
                if (firstPair.Add((r.Port, car)) && (string.IsNullOrEmpty(r.TextKey) || r.TextKey == "none"))
                {
                    bad.Add($"端口 × 载体 {r.Port}.{r.Carrier} 是这对的第一行，必须有短语文本键");
                }
                have.Add((r.Port, car));
                pcs.Add(new EmgPortCarrierRow { Port = (ushort)portById[r.Port].Code, Carrier = (byte)car, Verb = (byte)verb, A = r.A, B = r.B, C = r.C, Mag = (byte)mag });
            }
            c.PortCarriers = pcs.ToArray();
            foreach (string port in written)
            {
                if (portById[port].CarrierImpl != 1)
                {
                    continue;
                }
                for (int i = 0; i < Carriers.Length; i++)
                {
                    if (!have.Contains((port, i)))
                    {
                        bad.Add($"写端口 {port}（carrierImpl）在载体 {Carriers[i]} 上没有 fg.TbPortCarrier 行（FGR-EMG-015 零死对）");
                    }
                }
            }
            foreach (EmgSource.Port p in portById.Values)
            {
                bool anyRow = false;
                foreach ((string port, int _) in have)
                {
                    if (port == p.Id)
                    {
                        anyRow = true;
                        break;
                    }
                }
                if (p.CarrierImpl == 1 && !anyRow && !written.Contains(p.Id))
                {
                    c.RegisteredGaps.Add(p.Id);
                }
            }

            // 子形态
            var subs = new List<EmgSubFormRow>();
            var subCar = new HashSet<int>();
            var subCode = new HashSet<int>();
            foreach (EmgSource.Sub s in src.Subs)
            {
                int car = s == null ? -1 : CarrierIndex(s.Carrier);
                if (s == null || car < 0 || !subCar.Add(car) || s.Code < 1 || s.Code > 5 || !subCode.Add(s.Code)
                    || !(s.Speed >= 0f) || !(s.Kinetic >= 0f) || !(s.Life >= 0f) || !(s.LifeRatio >= 0f) || !(s.Radius >= 0f) || !(s.RadiusRatio >= 0f)
                    || (s.Life > 0f) == (s.LifeRatio > 0f) || (s.Radius > 0f) == (s.RadiusRatio > 0f))
                {
                    bad.Add($"子形态 {s?.Id}：载体重复 / 码不在 1～5 或重复 / 寿命与半径必须二选一");
                    continue;
                }
                subs.Add(new EmgSubFormRow
                {
                    Code = (byte)s.Code, Carrier = (byte)car, Speed = s.Speed, Life = s.Life, LifeRatio = s.LifeRatio, Radius = s.Radius, RadiusRatio = s.RadiusRatio,
                    Kinetic = s.Kinetic, KineticInherit = s.KineticInherit == 1,
                });
            }
            for (int i = 0; i < Carriers.Length; i++)
            {
                if (!subCar.Contains(i))
                {
                    bad.Add($"子形态缺载体 {Carriers[i]}（FGR-EMG-035：5 行）");
                }
            }
            c.SubForms = subs.ToArray();

            // 底盘特性
            var traits = new List<EmgTraitRow>();
            var kinds = new HashSet<EmgTraitKind>();
            foreach (EmgSource.Trait t in src.Traits)
            {
                if (t == null)
                {
                    continue;
                }
                int before = bad.Count;
                CheckContentIds(bad, "底盘特性 " + t.Id, null, ("id", t.Id), ("kind", t.Kind), ("appliesTo", t.AppliesTo), ("statePorts", t.StatePorts),
                    ("paramKeys", t.ParamKeys), ("nameKey", t.NameKey), ("descKey", t.DescKey));
                EmgTraitKind kind = TryParseName(t.Kind, false, out EmgTraitKind k) ? k : EmgTraitKind.None;
                if (kind == EmgTraitKind.None || !kinds.Add(kind))
                {
                    bad.Add($"底盘特性 {t.Id} 机制 {t.Kind} 不合法或重复");
                }
                if (t.Reserved != 1 && !src.ChassisExists(t.Chassis))
                {
                    bad.Add($"底盘特性 {t.Id} 的底盘 {t.Chassis} 不存在（预留底盘要标 reserved = 1）");
                }
                string[] sp = (t.StatePorts ?? string.Empty).Split(';');
                if (sp.Length > 2)
                {
                    bad.Add($"底盘特性 {t.Id} 的状态端口超过 2 个（内核行只有 2 个槽位）");
                }
                var spCodes = new List<ushort>();
                foreach (string port in sp)
                {
                    if (!portById.TryGetValue(port, out EmgSource.Port p) || FamilyOf(port) != EmgFamily.Chassis)
                    {
                        bad.Add($"底盘特性 {t.Id} 的状态端口 {port} 不是已注册的 chs.* 端口");
                    }
                    else
                    {
                        spCodes.Add((ushort)p.Code);
                    }
                }
                var param = new float[4];
                string[] keys = string.IsNullOrEmpty(t.ParamKeys) || t.ParamKeys == "none" ? Array.Empty<string>() : t.ParamKeys.Split(';');
                if (keys.Length > 4)
                {
                    bad.Add($"底盘特性 {t.Id} 参数键超过 4 个");
                }
                for (int i = 0; i < keys.Length && i < 4; i++)
                {
                    (bool ok, float v) = src.Tuning(keys[i]);
                    if (!ok)
                    {
                        bad.Add($"底盘特性 {t.Id} 参数键 {keys[i]} 不在 fg.TbHomeTuning");
                    }
                    param[i] = v;
                }
                if (bad.Count != before)
                {
                    continue;
                }
                c.TraitByChassis[t.Chassis] = traits.Count;
                traits.Add(new EmgTraitRow
                {
                    Kind = kind, StatePort0 = spCodes.Count > 0 ? spCodes[0] : (ushort)0, StatePort1 = spCodes.Count > 1 ? spCodes[1] : (ushort)0,
                    P0 = param[0], P1 = param[1], P2 = param[2], P3 = param[3], Stage = (byte)t.Stage, Reserved = t.Reserved == 1,
                });
            }
            c.Traits = traits.ToArray();

            // 运行时总开关
            (bool has, float raw) = src.Tuning(StageKey);
            if (!has)
            {
                bad.Add("fg.TbHomeTuning 缺少 emg.stage（FG19 第 18 节）");
            }
            c.RawStage = raw;
            c.EffectiveStage = ClampStage(raw, ImplementedStage);
            return c;
        }

        private static bool OwnerMatchesKind(string owner, string kind) => kind switch
        {
            "firmware" => owner.StartsWith("fw_", StringComparison.Ordinal),
            "component" => owner.StartsWith("comp_", StringComparison.Ordinal) || owner.StartsWith("func_", StringComparison.Ordinal),
            "structure" => owner.StartsWith("struct_", StringComparison.Ordinal),
            "chassis" => owner.StartsWith("chassis_", StringComparison.Ordinal),
            "enemy" => true,
            _ => false,
        };

        private static byte ParseCarriers(string s)
        {
            if (s == "all")
            {
                return AllCarriers;
            }
            if (string.IsNullOrEmpty(s))
            {
                return 0;
            }
            byte m = 0;
            foreach (string part in s.Split(';'))
            {
                int i = CarrierIndex(part);
                if (i < 0)
                {
                    return 0;
                }
                m |= (byte)(1 << i);
            }
            return m;
        }

        private static void CheckContentIds(List<string> bad, string where, string id, params (string col, string value)[] cols)
        {
            if (IsContentId(id))
            {
                bad.Add($"{where} 的 id 是内容 ID {id}（FGR-EMG-013）");
            }
            foreach ((string col, string value) in cols)
            {
                if (IsContentId(value))
                {
                    bad.Add($"{where} 的非 owner 列 {col} 出现内容 ID {value}（FGR-EMG-011 / 013）");
                }
            }
        }
    }
}
