using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Content
{
    /// <summary>fg.TbCarrierReading 的内核动作（与 tools/cell_tables/fgdata_reading.VERBS 一一对应；check_luban R26 钉死取值）。</summary>
    public enum CarrierReadingVerb : byte
    {
        Unknown = 0,
        Dmg, Cooldown, Extra, Pierce, Chain, Blast, Sweep, Echo, Pull, Knock, Lunge, Armor, Execute, Leech, Amp,
        Zone, ZoneGrow, ZoneTick, Weave, Escort, Drones, HeatBurst, Jump, Reach, Cone,
    }

    /// <summary>
    /// FG2-FW-02（FG02 FGR-FW-010～012；设计案 5.1、5.6；ADR-FW-002）：作战组件的载体与固定底盘兼容表（fg.TbCombatComponent），
    /// 以及“载体 × 读法字段 → 内核读法参数”的唯一翻译处（fg.TbCarrierReading）。
    ///
    /// - 读法按载体分类和字段实现（FGR-FW-010）：每条固件在 fg.TbFirmwareKind.readFields 声明它改动的读法字段与幅度；
    ///   <see cref="Build"/> 把生效固件的字段按幅度相加，再按“作战组件的载体 × 字段”逐行累加成 <see cref="CombatReading"/>。
    ///   这里与内核都不认识任何固件 ID，不写两两特例；新增固件只需要在表里声明字段。
    /// - 状态标签的效果（燃烧 = 持续伤害、减速、冻结 = 易伤、回收 = 修复、处决）在 fg.TbStatusTag 的 effect / amount 列，与载体无关。
    /// - 读法说明（FGR-FW-011）：<see cref="Reading"/> = 固件表的读法文本键（由同一套短语生成，数值与这里一致）。
    /// 全部按表版本缓存；调用只发生在装配结算（接入 / 离开 / 装配变更），不按帧。
    /// </summary>
    public static class CarrierReadings
    {
        private static TbCombatComponent _components;
        private static TbCarrierReading _readings;
        private static TbCombatComponent _componentsOverride;
        private static TbCarrierReading _readingsOverride;
        private static bool _loaded;
        private static string _loadError;
        /// <summary>(字段, 载体) → 行（按表里的顺序）。</summary>
        private static readonly Dictionary<(string, FirmwareCarrier), List<CarrierReading>> _byFieldCarrier =
            new Dictionary<(string, FirmwareCarrier), List<CarrierReading>>();
        private static readonly Dictionary<string, (uint Bit, string Effect, float Amount)> _tagEffects =
            new Dictionary<string, (uint, string, float)>(StringComparer.Ordinal);
        private static int _tagEffectsRevision = -1;

        /// <summary>重载 / 测试注入时 +1。</summary>
        public static int Revision { get; private set; } = 1;

        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        public static IReadOnlyList<CombatComponent> Components
        {
            get
            {
                EnsureLoaded();
                return _components?.DataList ?? (IReadOnlyList<CombatComponent>)Array.Empty<CombatComponent>();
            }
        }

        public static IReadOnlyList<CarrierReading> Rows
        {
            get
            {
                EnsureLoaded();
                return _readings?.DataList ?? (IReadOnlyList<CarrierReading>)Array.Empty<CarrierReading>();
            }
        }

        public static bool TryGetComponent(string componentId, out CombatComponent row)
        {
            row = null;
            if (string.IsNullOrEmpty(componentId))
            {
                return false;
            }
            EnsureLoaded();
            return _components != null && _components.DataMap.TryGetValue(componentId, out row) && row != null;
        }

        /// <summary>主组件的载体（射弹 / 格斗 / 无人机 / 力场 / 布区）。没有主组件或表里查不到时 false。</summary>
        public static bool TryGetCarrier(string componentId, out FirmwareCarrier carrier)
        {
            carrier = FirmwareCarrier.Projectile;
            return TryGetComponent(componentId, out CombatComponent row) && TryParseCarrier(row.Carrier, out carrier);
        }

        public static bool TryParseCarrier(string value, out FirmwareCarrier carrier)
        {
            switch (value)
            {
                case "projectile": carrier = FirmwareCarrier.Projectile; return true;
                case "melee": carrier = FirmwareCarrier.Melee; return true;
                case "summon": carrier = FirmwareCarrier.Summon; return true;
                case "aura": carrier = FirmwareCarrier.Aura; return true;
                case "field": carrier = FirmwareCarrier.Field; return true;
                default: carrier = FirmwareCarrier.Projectile; return false;
            }
        }

        public static string CarrierKey(FirmwareCarrier c) => c switch
        {
            FirmwareCarrier.Melee => "melee",
            FirmwareCarrier.Summon => "summon",
            FirmwareCarrier.Aura => "aura",
            FirmwareCarrier.Field => "field",
            _ => "projectile",
        };

        /// <summary>载体名（当前语言）。</summary>
        public static string CarrierName(FirmwareCarrier c) => GameText.Get("firmware.carrier." + CarrierKey(c));

        /// <summary>组件的载体细分名（“格斗·扇形”等）；查不到返回 null。</summary>
        public static string SubtypeName(string componentId) =>
            TryGetComponent(componentId, out CombatComponent row) && HasKey(row.SubtypeKey) ? GameText.Get(row.SubtypeKey) : null;

        /// <summary>固定底盘（炮塔）= 底盘特性表里机制为“锚定”的底盘（fg.TbChassisTrait 的 FixedAnchor 行；FG-GAP-169 ③，不认底盘 ID）。
        /// 不看 emg.stage：兼容表、炮塔宿主从 FG2-FW-02 / FG6-DEF-01 起就生效，回滚到阶段 0 也照旧；机器记录的具体机型先由 EmgCatalog.TryGetTrait 译成底盘内容 ID。</summary>
        public static bool IsFixedChassis(string chassisId) =>
            Emergence.EmgCatalog.TryGetTrait(chassisId, out Emergence.EmgTraitRow row) && row.Kind == Emergence.EmgTraitKind.FixedAnchor;

        // ─────────────────────────────── 固定底盘兼容（FGR-FW-012）───────────────────────────────

        /// <summary>组件能不能装上这个底盘：移动底盘一律可以；固定底盘（炮塔）按表——不兼容时给原因文本键（一个参数：组件名），
        /// “兼容但读法调整”时给炮塔读法文本键。唯一判定：蓝图的主组件 / 功能组件 / 换底盘 / 保存校验都调这里。</summary>
        public static bool CanMount(string chassisId, string componentId, out string reasonKey, out string adjustedReadingKey)
        {
            reasonKey = null;
            adjustedReadingKey = null;
            if (string.IsNullOrEmpty(componentId))
            {
                return true;
            }
            if (!IsFixedChassis(chassisId))
            {
                // EMG4-LOOT-01（FGR-FAC-009 环形喷淋架“仅固定底盘”）：只能装在固定底盘的组件，移动底盘拒绝并给原因
                if (TryGetComponent(componentId, out CombatComponent fixedRow) && fixedRow.FixedOnly == 1)
                {
                    reasonKey = "component.mount.fixed_only";
                    return false;
                }
                return true;
            }
            if (!TryGetComponent(componentId, out CombatComponent row))
            {
                // 结构模块等不在作战组件表里的组件：固定底盘上照常可装（兼容表只管作战组件）。
                return true;
            }
            switch (row.Turret)
            {
                case "no":
                    reasonKey = HasKey(row.TurretReasonKey) ? row.TurretReasonKey : "component.turret.reason.dash";
                    return false;
                case "adjusted":
                    adjustedReadingKey = HasKey(row.TurretReadingKey) ? row.TurretReadingKey : null;
                    return true;
                default:
                    return true;
            }
        }

        // ─────────────────────────────── 读法字段 ───────────────────────────────

        /// <summary>一条固件声明的读法字段（fg.TbFirmwareKind.readFields）。不是固件或没有时为空。</summary>
        public static IReadOnlyList<(string Field, float Magnitude)> FieldsOf(string firmwareId)
        {
            if (!FirmwareKinds.TryGetRow(firmwareId, out GameConfig.fg.FirmwareKind row))
            {
                return Array.Empty<(string, float)>();
            }
            return ParseFields(row.ReadFields);
        }

        public static List<(string Field, float Magnitude)> ParseFields(string spec)
        {
            var list = new List<(string, float)>();
            if (string.IsNullOrEmpty(spec) || spec == "none")
            {
                return list;
            }
            foreach (string part in spec.Split(';'))
            {
                int colon = part.IndexOf(':');
                string name = (colon >= 0 ? part.Substring(0, colon) : part).Trim();
                float mag = 1f;
                if (colon >= 0 && !float.TryParse(part.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out mag))
                {
                    mag = 1f;
                }
                if (name.Length > 0)
                {
                    list.Add((name, mag));
                }
            }
            return list;
        }

        /// <summary>这条固件在机器电路里有没有可结算的读法（有旧基因等价实现，或者有原生读法字段——装甲击穿）。</summary>
        public static bool HasReading(string firmwareId) => FieldsOf(firmwareId).Count > 0;

        /// <summary>FGR-FW-011：这条固件装在 <paramref name="carrier"/> 上会怎样（当前语言）。不是固件时 null。</summary>
        public static string Reading(string firmwareId, FirmwareCarrier carrier) => FirmwareKinds.Reading(firmwareId, carrier);

        /// <summary>FGR-FW-011“固件详情页逐条列出”：5 种载体各一行（“装在射弹上：……”）。不是固件时 null。</summary>
        public static string DetailLines(string firmwareId)
        {
            if (!FirmwareKinds.IsFirmware(firmwareId))
            {
                return null;
            }
            var sb = new System.Text.StringBuilder();
            foreach (FirmwareCarrier c in AllCarriers)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(GameText.Format("reading.detail.line", CarrierName(c), Reading(firmwareId, c)));
            }
            // EMG1-UX-01（FG19 第 13 节 B01）：详情页 / 悬停同时列出这条固件读 / 写的端口（只读声明表）。
            string ports = Emergence.EmgProgramTreeText.PortsLine(firmwareId);
            if (ports != null)
            {
                sb.Append('\n').Append(ports);
            }
            return sb.ToString();
        }

        public static readonly FirmwareCarrier[] AllCarriers =
        {
            FirmwareCarrier.Projectile, FirmwareCarrier.Melee, FirmwareCarrier.Summon, FirmwareCarrier.Aura, FirmwareCarrier.Field,
        };

        // ─────────────────────────────── 翻译成内核读法 ───────────────────────────────

        /// <summary>
        /// 装配 → 内核读法参数（<see cref="Combat.CombatSite.MachineWeaponFrom(Blueprint.BlueprintCircuitPreview, bool)"/> 调用，唯一翻译处）。
        /// <paramref name="damageFromCompile"/>：武器伤害已经来自电路编译（连射器 / 切割束——电容蓄力等能量改动已经算进去），
        /// 这时“伤害倍率”读法不再乘第二遍。
        /// </summary>
        public static CombatReading Build(string primaryId, string chassisId, IReadOnlyList<string> firmwareIds, bool damageFromCompile) =>
            Build(primaryId, null, chassisId, firmwareIds, damageFromCompile);

        /// <summary>FG2-VFX-02：同上，另读功能组件的被动参数（尖刺外装的反伤）。武器载体与投送仍只由主组件决定。</summary>
        public static CombatReading Build(string primaryId, string utilityId, string chassisId, IReadOnlyList<string> firmwareIds, bool damageFromCompile) =>
            Build(primaryId, utilityId, chassisId, firmwareIds, damageFromCompile, null);

        /// <param name="skipField">EMG1-GRM-01：(固件, 字段) → true 时这一字段不按旧读法结算（已切到声明轨、由程序动词承接的修饰字段，FGR-EMG-019 同一 owner 不两轨同时结算）。</param>
        /// <param name="fieldsOf">EMG4-CLEAN-01（FGR-EMG-019 第 3 点）：固件 → 参与结算的读法字段。缺省 = 旧 readFields（只剩炮塔 / 重炮弹体武器的兼容读法与预览、对账用）；
        /// 声明轨传 <see cref="Emergence.EmgCatalog.DeclLegacyOf"/>（声明参数 legacy 列）。</param>
        public static CombatReading Build(string primaryId, string utilityId, string chassisId, IReadOnlyList<string> firmwareIds, bool damageFromCompile,
            Func<string, string, bool> skipField, Func<string, IReadOnlyList<(string Field, float Magnitude)>> fieldsOf = null)
        {
            fieldsOf ??= FieldsOf;
            var r = new CombatReading();
            FirmwareCarrier carrier = FirmwareCarrier.Projectile;
            if (TryGetComponent(primaryId, out CombatComponent comp))
            {
                TryParseCarrier(comp.Carrier, out carrier);
                r.Approach = Math.Max(0f, comp.Approach);
                r.Cone = Math.Max(0f, comp.Cone);
                r.Area = Math.Max(0f, comp.Area);
                r.FieldSeconds = Math.Max(0f, comp.FieldSeconds);
                r.FieldDpsRatio = Math.Max(0f, comp.FieldDpsRatio);
                r.Drones = Math.Max(0, comp.Drones);
                r.DroneSeconds = Math.Max(0f, comp.DroneSeconds);
                r.DroneRatio = Math.Max(0f, comp.DroneRatio);
                r.DroneCooldown = Math.Max(0f, comp.DroneCooldown);
                r.DroneLeash = Math.Max(0f, comp.DroneLeash);
                bool turretAdjusted = IsFixedChassis(chassisId) && comp.Turret == "adjusted";
                r.Knockback = Math.Max(0f, turretAdjusted && comp.TurretKnockback > 0f ? comp.TurretKnockback : comp.Knockback);
                // FG2-VFX-02（FG-GAP-051）：布区·脉冲落在自己脚下；无人机·定点（哨戒桩）；拆解钳自带回波（夹住后持续拆解）。
                r.FieldPlacement = comp.FieldPlace == "self" ? CombatZonePlacement.Attacker : CombatZonePlacement.HitPoint;
                r.DroneAnchored = (byte)(comp.DroneMode == "post" ? 1 : 0);
                if (comp.EchoCount > 0 && comp.EchoDelay > 0f && comp.EchoRatio > 0f)
                {
                    r.EchoCount = comp.EchoCount;
                    r.EchoDelay = comp.EchoDelay;
                    r.EchoRatio = comp.EchoRatio;
                }
            }
            // FG2-VFX-02：功能组件的被动反伤（尖刺外装）：固定值 = 表 damage 列，比例 = thorns，触及 = thornsReach。
            if (TryGetComponent(utilityId, out CombatComponent util) && util.Slot == "function" && util.Thorns > 0f)
            {
                r.Thorns = Math.Max(0f, util.Thorns);
                r.ThornsFlat = Math.Max(0f, util.Damage);
                r.ThornsReach = Math.Max(0f, util.ThornsReach);
            }
            r.Carrier = (CombatCarrier)(byte)carrier;

            // 字段按幅度相加（同一字段装两条：幅度叠加），标签取并集。
            var fields = new Dictionary<string, float>(StringComparer.Ordinal);
            var order = new List<string>();
            var tags = new HashSet<string>(StringComparer.Ordinal);
            if (firmwareIds != null)
            {
                foreach (string fw in firmwareIds)
                {
                    foreach ((string field, float mag) in fieldsOf(fw))
                    {
                        if (skipField != null && skipField(fw, field))
                        {
                            continue;
                        }
                        if (!fields.ContainsKey(field))
                        {
                            order.Add(field);
                            fields[field] = 0f;
                        }
                        fields[field] += mag;
                    }
                    // EMG1-FW-01：已切声明轨的固件旧标签也只走一轨（skipField(fw, "#tags")，见 EmgProgramCompiler.LegacySkip）
                    if (skipField != null && skipField(fw, Emergence.EmgProgramCompiler.TagsField))
                    {
                        continue;
                    }
                    foreach (string tag in FirmwareKinds.TagsOf(fw))
                    {
                        // EMG2-PHS-01：能量注入写端退役的旧标签（EmgProgramCompiler.RetiredTags，FGR-EMG-019 单轨）
                        if (skipField != null && skipField(fw, Emergence.EmgProgramCompiler.TagField(tag)))
                        {
                            continue;
                        }
                        tags.Add(tag);
                    }
                }
            }
            EnsureLoaded();
            foreach (string field in order)
            {
                if (!_byFieldCarrier.TryGetValue((field, carrier), out List<CarrierReading> rows))
                {
                    Log.Error($"[CarrierReadings] 读法字段 {field} 在载体 {carrier} 上没有行（check_luban R26 应已拦下）。");
                    continue;
                }
                foreach (CarrierReading row in rows)
                {
                    Accumulate(ref r, carrier, row, fields[field], damageFromCompile);
                }
            }

            // 状态标签（与载体无关）：内核位 + 效果。
            foreach (string tag in tags)
            {
                if (!TryGetTagEffect(tag, out uint bit, out string effect, out float amount))
                {
                    continue;
                }
                r.StatusMask |= bit;
                switch (effect)
                {
                    case "dot": r.StatusDps = Math.Max(r.StatusDps, amount); break;
                    case "slow": r.StatusSlow = Math.Max(r.StatusSlow, amount); break;
                    case "vuln": r.StatusVuln = Math.Max(r.StatusVuln, amount); break;
                    case "execute": r.ExecuteBelow = Math.Max(r.ExecuteBelow, amount); break;
                }
            }
            if (r.StatusMask != 0u)
            {
                r.StatusSeconds = Tuning("reading.status_seconds", 3f);
            }
            // 集群协议伴飞（非无人机载体）：无人机参数取调参；无人机载体已经有自己的。
            if (r.EscortDrones > 0 && carrier != FirmwareCarrier.Summon)
            {
                r.DroneSeconds = Tuning("reading.escort.seconds", 8f);
                r.DroneRatio = Tuning("reading.escort.ratio", 0.4f);
                r.DroneCooldown = Tuning("reading.escort.cooldown", 0.8f);
                r.DroneLeash = Tuning("reading.escort.leash", 10f);
            }
            if (r.WeaveRadius > 0f)
            {
                r.WeaveDpsRatio = Tuning("reading.weave.dps_ratio", 0.15f);
            }
            r.Cone = Math.Min(180f, r.Cone);
            return r;
        }

        private static void Accumulate(ref CombatReading r, FirmwareCarrier carrier, CarrierReading row, float mag, bool damageFromCompile) =>
            ApplyVerb(ref r, carrier, row.Verb, row.A, row.B, row.C, row.Mag, mag, damageFromCompile, row.Place, row.Id);

        /// <summary>
        /// 一条“动词 + 参数”按旧读法结算进武器读法（幅度 <paramref name="mag"/> 乘到 <paramref name="magTarget"/> 指的参数上）。旧 readFields 轨（<see cref="Build(string, string, string, IReadOnlyList{string}, bool)"/>）
        /// 与 EMG1-GRM-01 的程序轨（根节点上 fg.TbPortCarrier 解析出的旧动词行，CombatSite.FoldProgramReadings）共用这一份结算，同一动词两轨结果一致。
        /// </summary>
        /// <summary>EMG1-GRM-01：把一个旧读法字段（幅度 <paramref name="mag"/>）按本载体的读法行折进读法（与 <see cref="Build"/> 同一累加）。没有行返回 false。</summary>
        public static bool ApplyField(ref CombatReading r, FirmwareCarrier carrier, string field, float mag, bool damageFromCompile)
        {
            EnsureLoaded();
            if (!_byFieldCarrier.TryGetValue((field, carrier), out List<CarrierReading> rows))
            {
                return false;
            }
            foreach (CarrierReading row in rows)
            {
                Accumulate(ref r, carrier, row, mag, damageFromCompile);
            }
            return true;
        }

        public static void ApplyVerb(ref CombatReading r, FirmwareCarrier carrier, string verb, float a, float b, float c, string magTarget, float mag, bool damageFromCompile,
            string place = "none", string rowId = null)
        {
            MulMag(ref a, ref b, ref c, magTarget, mag);
            CarrierReadingVerb v = VerbOf(verb);
            if (v == CarrierReadingVerb.Dmg && damageFromCompile)
            {
                return; // 伤害由电路编译给出（固件的能量改动已算在里面）
            }
            // EMG4-CLEAN-01：动作结算与内核补执行根节点读法行共用 BinGames.Sim.Combat.CombatReadingVerbs（同一动作两处结果一致）
            if (!CombatReadingVerbs.Apply(ref r, (byte)v, a, b, c, PlaceCode(place), carrier == FirmwareCarrier.Summon))
            {
                Log.Error($"[CarrierReadings] 读法行 {rowId ?? "(程序)"} 的动作 {verb} 不认识（check_luban R26 / R39 应已拦下）。");
            }
        }

        private static void MulMag(ref float a, ref float b, ref float c, string magTarget, float mag)
        {
            switch (magTarget)
            {
                case "a": a *= mag; break;
                case "b": b *= mag; break;
                case "c": c *= mag; break;
            }
        }

        private static byte PlaceCode(string place) => place switch { "mid" => 1, "self" => 2, _ => 0 };

        /// <summary>
        /// EMG4-CLEAN-01（FG-GAP-207 / 208、DEBT-EMG3META01-01）：根节点上位于条件 / 随机元程序下游的那条指令，它的读法贡献不折进静态读法，
        /// 记成一条内核“根节点读法行”（<see cref="EmgInstrFlags.LegacyRoot"/>，参数已乘幅度；内核 CombatLogic.EmgGatedReading 按放行位补执行）。
        /// 出手间隔（cooldown，电容蓄力）属于武器节奏、在出手前就要定，仍折进静态读法（返回 false，调用方照常 <see cref="ApplyVerb"/>）。
        /// </summary>
        public static bool TryRecordVerb(List<EmgInstr> ops, int ordinal, string verb, float a, float b, float c, string magTarget, float mag, bool damageFromCompile, string place = "none")
        {
            CarrierReadingVerb v = VerbOf(verb);
            if (v == CarrierReadingVerb.Cooldown || v == CarrierReadingVerb.Unknown)
            {
                return false;
            }
            MulMag(ref a, ref b, ref c, magTarget, mag);
            if (v == CarrierReadingVerb.Dmg && (damageFromCompile || a <= 0f))
            {
                return true; // 与 ApplyVerb 同口径：不改读法
            }
            ops.Add(new EmgInstr
            {
                Port = 0, Role = (byte)Emergence.EmgRole.Modify, Ordinal = (byte)Math.Min(255, Math.Max(0, ordinal)), Verb = (byte)v, A = a, B = b, C = c,
                Param2 = PlaceCode(place), Amount = 1f, Flags = EmgInstrFlags.LegacyRoot,
            });
            return true;
        }

        /// <summary>EMG4-CLEAN-01：一个旧读法字段按本载体的读法行记成根节点读法行（与 <see cref="ApplyField"/> 同一组行）；出手间隔行照常折进 <paramref name="r"/>。没有行返回 false。</summary>
        public static bool RecordField(ref CombatReading r, List<EmgInstr> ops, int ordinal, FirmwareCarrier carrier, string field, float mag, bool damageFromCompile)
        {
            EnsureLoaded();
            if (!_byFieldCarrier.TryGetValue((field, carrier), out List<CarrierReading> rows))
            {
                return false;
            }
            foreach (CarrierReading row in rows)
            {
                if (!TryRecordVerb(ops, ordinal, row.Verb, row.A, row.B, row.C, row.Mag, mag, damageFromCompile, row.Place))
                {
                    Accumulate(ref r, carrier, row, mag, damageFromCompile);
                }
            }
            return true;
        }

        private static int Count(float v) => v <= 0f ? 0 : Math.Max(1, (int)Math.Round(v));

        public static CarrierReadingVerb VerbOf(string verb) => verb switch
        {
            "dmg" => CarrierReadingVerb.Dmg,
            "cooldown" => CarrierReadingVerb.Cooldown,
            "extra" => CarrierReadingVerb.Extra,
            "pierce" => CarrierReadingVerb.Pierce,
            "chain" => CarrierReadingVerb.Chain,
            "blast" => CarrierReadingVerb.Blast,
            "sweep" => CarrierReadingVerb.Sweep,
            "echo" => CarrierReadingVerb.Echo,
            "pull" => CarrierReadingVerb.Pull,
            "knock" => CarrierReadingVerb.Knock,
            "lunge" => CarrierReadingVerb.Lunge,
            "armor" => CarrierReadingVerb.Armor,
            "execute" => CarrierReadingVerb.Execute,
            "leech" => CarrierReadingVerb.Leech,
            "amp" => CarrierReadingVerb.Amp,
            "zone" => CarrierReadingVerb.Zone,
            "zonegrow" => CarrierReadingVerb.ZoneGrow,
            "zonetick" => CarrierReadingVerb.ZoneTick,
            "weave" => CarrierReadingVerb.Weave,
            "escort" => CarrierReadingVerb.Escort,
            "drones" => CarrierReadingVerb.Drones,
            "heatburst" => CarrierReadingVerb.HeatBurst,
            "jump" => CarrierReadingVerb.Jump,
            "reach" => CarrierReadingVerb.Reach,
            "cone" => CarrierReadingVerb.Cone,
            _ => CarrierReadingVerb.Unknown,
        };

        /// <summary>状态标签（旧引擎标签字符串，含同义写法）→ 内核位与效果。机制标记、查不到的返回 false。</summary>
        public static bool TryGetTagEffect(string tag, out uint bit, out string effect, out float amount)
        {
            bit = 0u;
            effect = "none";
            amount = 0f;
            if (string.IsNullOrEmpty(tag))
            {
                return false;
            }
            if (_tagEffectsRevision != StatusTagCatalog.Revision)
            {
                _tagEffects.Clear();
                foreach (StatusTag row in StatusTagCatalog.Rows)
                {
                    if (row == null || row.Kind != "status" || row.Bit < 0 || row.Bit > 30)
                    {
                        continue;
                    }
                    _tagEffects[row.Id] = (1u << row.Bit, row.Effect ?? "none", row.Amount);
                }
                _tagEffectsRevision = StatusTagCatalog.Revision;
            }
            if (!_tagEffects.TryGetValue(tag, out (uint Bit, string Effect, float Amount) e))
            {
                return false;
            }
            bit = e.Bit;
            effect = e.Effect;
            amount = e.Amount;
            return true;
        }

        private static float Tuning(string id, float fallback) => Combat.CombatSite.Tuning(id, fallback);

        private static bool HasKey(string key) => !string.IsNullOrEmpty(key) && key != "none";

        // ─────────────────────────────── 加载 / 测试注入 ───────────────────────────────

        public static void Reload()
        {
            _loaded = false;
            _components = null;
            _readings = null;
            _loadError = null;
            _byFieldCarrier.Clear();
            _tagEffectsRevision = -1;
            Revision++;
            EnsureLoaded();
        }

        /// <summary>测试注入：替换作战组件表 / 读法表（null = 用真实表）。用完必须 <see cref="ResetForTests"/>。</summary>
        public static void OverrideForTests(TbCombatComponent components, TbCarrierReading readings)
        {
            _componentsOverride = components;
            _readingsOverride = readings;
            Reload();
        }

        public static void ResetForTests()
        {
            _componentsOverride = null;
            _readingsOverride = null;
            Reload();
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            try
            {
                _components = _componentsOverride ?? ConfigSystem.Instance.Tables?.TbCombatComponent;
                _readings = _readingsOverride ?? ConfigSystem.Instance.Tables?.TbCarrierReading;
                if (_components == null || _readings == null)
                {
                    _loadError = "配置表 fg.TbCombatComponent / fg.TbCarrierReading 不存在";
                }
            }
            catch (Exception ex)
            {
                _loadError = $"配置表读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[CarrierReadings] {_loadError}");
            }
            _byFieldCarrier.Clear();
            if (_readings != null)
            {
                foreach (CarrierReading row in _readings.DataList)
                {
                    if (row == null || !TryParseCarrier(row.Carrier, out FirmwareCarrier c))
                    {
                        continue;
                    }
                    if (!_byFieldCarrier.TryGetValue((row.Field, c), out List<CarrierReading> list))
                    {
                        list = new List<CarrierReading>();
                        _byFieldCarrier[(row.Field, c)] = list;
                    }
                    list.Add(row);
                }
            }
        }
    }
}
