using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using Unity.Mathematics;
using UnityEngine;
using FwRow = GameConfig.fg.FirmwareKind;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG3-FW-01 固件与部件全量单轨切换（FG19 FGR-EMG-014 / 015 / 019 / 020；ADR-EMG-001 ⑪）。阶段 3 口径（真实表，emg.stage = 3）：
    /// A 清单（FGT-EMG-055）：stageGate ≤ 3 的 owner 全部走声明轨、没有声明用到未接通端口、混合固件两父都在声明轨；列 stageGate = 4 清单。
    /// B 单轨比对（FGT-EMG-055 / 003）：名表 + 新固件 + 混合固件 × 5 载体——程序轨读法 = 独立按声明推出的“只留本机出手仍由过渡 / 折叠字段执行的写端字段”读法；
    ///   没有 owner 回落旧轨（LegacyOwners 为空）；ComposeEngine 不再按已切换固件改连射器的编译伤害；负向：两轨同时结算被比出来。
    /// C 零死对（FGT-EMG-003 阶段 3）：固件 × 5 载体 × 程序首位 / 末位行为指纹差异；混合固件与每条父固件单独装都不同（展开后的两条都有行为；固定场景看不出的进 8 秒 / 慢速长飞 /
    ///   击杀 / 被击 / 落点靶场景，仍看不出判失败）；
    ///   反应增幅（读 evt.react）在“本载荷引发交互”的场景里 5 载体都多放出子载荷，没有交互时不放（对照）。
    /// D 部件开 / 关（自身宿主）：散热鳍——装 / 不装、泡在水 / 冷却液斑块里 ×2、旧轨对照；阶段 2 / 3 作战组件装 / 不装行为指纹不同。
    /// E 有源（规则三）：阶段 3 已生效声明读到的端口都有写者或世界源。
    /// F 存读档：反应增幅 + 散热鳍战斗中途快照往返逐位一致、续跑一致；战役存档的固件库存与蓝图往返后不变、全部仍可编出程序。
    /// </summary>
    public static partial class FgReadingMatrixSelfCheck
    {
        private static void CheckStage3SingleTrack()
        {
            Line("  · C5. EMG3-FW-01（FGT-EMG-055 / 003 / 002 阶段 3 全量）：stageGate ≤ 3 的 owner 全部单轨；固件 × 5 载体零死对；部件自身宿主开 / 关；无源端口 0；存读档");
            EmgCatalog.ResetForTests();
            EmgCompiled c = EmgCatalog.Current;
            EmgSource src = EmgCatalog.FromTables();
            Expect(c.EffectiveStage >= 3, $"真实表 emg.stage 生效阶段 = {c.EffectiveStage}（阶段 3 口径，EMG4-HIJ-01 起缺省 4）");
            CheckStage3Inventory(src, c);
            CheckStage3SingleTrackCompare(src);
            CheckStage3ZeroDead(src);
            CheckAmplify();
            CheckHeatSinkFin();
            CheckRootEmitAndSubZoneTicks();
            CheckStage3Parts(src);
            CheckStage3Sourced(src);
            CheckStage3SaveLoad();
            CheckDeclLegacy(src);
        }

        // ── C5b. EMG4-CLEAN-01（FGR-EMG-019 第 3 点）：战斗不读 readFields ──
        private static void CheckDeclLegacy(EmgSource src)
        {
            Line("  · C5b. EMG4-CLEAN-01（FGR-EMG-019 第 3 点）：战斗不读 readFields——过渡 / 数值折叠字段取声明参数 legacy 列；readFields 只留作对账");
            EmgCatalog.ResetForTests();
            IReadOnlyList<(string Field, float Magnitude)> Expected(string owner)
            {
                var allow = new HashSet<string>(StringComparer.Ordinal);
                foreach (EmgSource.Decl d in src.Decls.Where(d => d != null && d.Owner == owner && d.Dir == "write"))
                {
                    allow.UnionWith(EmgProgramCompiler.TransitionFields(d.Port));
                    allow.UnionWith(EmgProgramCompiler.NumericLegacyFields(d.Port));
                }
                return CarrierReadings.FieldsOf(owner).Where(f => allow.Contains(f.Field)).ToList();
            }
            string Text(IEnumerable<(string Field, float Magnitude)> l) => string.Join(";", l.Select(f => f.Field + ":" + f.Magnitude.ToString("0.###", CultureInfo.InvariantCulture)));
            // 1. 对账：每条固件（含混合芯片 = 两父相加）的声明参数 = 旧 readFields 里属于它写端口的那几个字段（同名同幅度同序）
            var bad = new List<string>();
            int n = 0, withLegacy = 0;
            foreach (FwRow row in FirmwareKinds.Rows.Where(r => r != null))
            {
                n++;
                IReadOnlyList<(string Field, float Magnitude)> got = EmgCatalog.DeclLegacyOf(row.Id), want;
                if (GameLogic.Campaign.Economy.FusionCatalog.TryGetByMix(row.Id, out GameLogic.Campaign.Economy.FusionRecipeDef def))
                {
                    var merged = new List<(string Field, float Magnitude)>();
                    foreach ((string f, float m) in Expected(def.ParentA).Concat(Expected(def.ParentB)))
                    {
                        int k = merged.FindIndex(e => e.Field == f);
                        if (k < 0) merged.Add((f, m)); else merged[k] = (f, merged[k].Magnitude + m);
                    }
                    for (int i = 0; i < merged.Count; i++)
                    {
                        if (GameLogic.Campaign.Economy.FusionCatalog.TryGetCap(merged[i].Field, out float cap))
                        {
                            merged[i] = (merged[i].Field, Mathf.Min(merged[i].Magnitude, cap));
                        }
                    }
                    want = merged;
                    // 两父相加后的字段也必须是芯片自己 readFields 的子集（同幅度）
                    foreach ((string f, float m) in merged)
                    {
                        if (!CarrierReadings.FieldsOf(row.Id).Any(x => x.Field == f && Mathf.Abs(x.Magnitude - m) < 1e-4f))
                        {
                            bad.Add($"{row.Id}：两父声明参数 {f}:{m} 不在芯片 readFields（{row.ReadFields}）里");
                        }
                    }
                }
                else
                {
                    want = Expected(row.Id);
                }
                withLegacy += got.Count > 0 ? 1 : 0;
                if (Text(got) != Text(want))
                {
                    bad.Add($"{row.Id}：声明参数 [{Text(got)}] ≠ readFields 对账 [{Text(want)}]");
                }
            }
            Expect(bad.Count == 0 && withLegacy > 0, $"声明参数 legacy 列与旧 readFields 对账：{n} 条固件、其中 {withLegacy} 条带过渡 / 数值折叠字段，全部一致{(bad.Count > 0 ? "；不一致：" + string.Join("；", bad.Take(8)) : string.Empty)}");
            // 2. 行为：战斗读法从声明参数来——临时拿掉某条固件的声明参数（readFields 不动），程序读法随之改变；恢复后回到原值
            foreach (string fw in new[] { "fw_wide", "fw_oilleak", "fw_echo" }) // 广角：声明幅度 30 ≠ 旧字段 spread:60（扩口两者都是 1.5，拿掉看不出差别）
            {
                BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
                board.TrySetUplink(UplinkSlot);
                BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { fw });
                EmgProgramResult prog = EmgProgramCompiler.Compile(p, null);
                CombatReading baseR = CombatSite.MachineWeaponFrom(p).Reading;
                CombatReading with = CombatSite.ProgramReading(p, baseR, prog);
                EmgCatalog.Current.LegacyByOwner.Remove(fw);
                CombatReading without = CombatSite.ProgramReading(p, baseR, EmgProgramCompiler.Compile(p, null));
                EmgCatalog.ResetForTests();
                CombatReading again = CombatSite.ProgramReading(p, baseR, EmgProgramCompiler.Compile(p, null));
                bool still = CarrierReadings.FieldsOf(fw).Count > 0;
                Expect(!with.Equals(without) && with.Equals(again) && still,
                    $"{fw}（连射器）：拿掉声明参数后程序读法改变（{!with.Equals(without)}：体积 {with.BlastRadius:0.##}→{without.BlastRadius:0.##}、驻留 {with.ZoneSeconds:0.##}→{without.ZoneSeconds:0.##}、余震 {with.EchoCount}→{without.EchoCount}），" +
                    $"恢复后与原值相同（{with.Equals(again)}）；readFields 仍在（{still}，只作对账）");
            }
            // 3. 战斗入口（机器装配 / 炮塔 / 试验场共用的 MachineWeaponFrom(p, 冷却) 与敌方组装武器）也不读 readFields：
            //    程序为空 / 程序表满退回时的基础读法 = 只按声明参数结算；readFields 里不属于声明参数的字段（霰射额外目标、电弧连锁……）不进战斗读法。
            //    独立推导：用旧 readFields 但跳过 Expected 以外的字段（幅度来自旧表，与声明参数来源不同）。单参数重载（预览 / 旧读法对照）仍按旧口径。
            int differ = 0, compared = 0;
            var badEntry = new List<string>();
            foreach (FwRow row in FirmwareKinds.Rows.Where(r => r != null && CarrierReadings.FieldsOf(r.Id).Count > 0 && !r.Id.StartsWith("fw_mix_", StringComparison.Ordinal)))
            {
                BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
                board.TrySetUplink(UplinkSlot);
                BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { row.Id });
                if (!p.FirmwareIds.Contains(row.Id))
                {
                    continue;
                }
                compared++;
                var allow = new HashSet<string>(Expected(row.Id).Select(f => f.Field), StringComparer.Ordinal);
                bool fixedDamage = CarrierReadings.TryGetComponent(p.PrimaryId, out GameConfig.fg.CombatComponent comp) && comp.Damage > 0f;
                CombatReading oracle = CarrierReadings.Build(p.PrimaryId, p.UtilityId, p.ChassisId, CombatSite.ReadingFirmware(p), !fixedDamage,
                    (fw, field) => fw == row.Id && !field.StartsWith("#", StringComparison.Ordinal) && !allow.Contains(field)); // 旧标签（tags 列，不是 readFields）照旧
                CombatReading battle = CombatSite.MachineWeaponFrom(p, false).Reading;
                CombatReading legacy = CombatSite.MachineWeaponFrom(p).Reading;
                if (!battle.Equals(oracle))
                {
                    badEntry.Add(row.Id);
                }
                if (!battle.Equals(legacy))
                {
                    differ++;
                }
            }
            CombatReading wideBattle, wideNoDecl;
            {
                BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
                board.TrySetUplink(UplinkSlot);
                BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { "fw_wide" });
                wideBattle = CombatSite.MachineWeaponFrom(p, false).Reading;
                EmgCatalog.Current.LegacyByOwner.Remove("fw_wide");
                wideNoDecl = CombatSite.MachineWeaponFrom(p, false).Reading;
                EmgCatalog.ResetForTests();
            }
            Expect(compared > 20 && badEntry.Count == 0 && differ > 0 && !wideBattle.Equals(wideNoDecl),
                $"战斗入口基础读法只按声明参数：{compared} 条固件（连射器）全部 = 独立推导（跳过非声明参数字段）{(badEntry.Count > 0 ? "；不一致：" + string.Join("/", badEntry.Take(8)) : string.Empty)}；" +
                $"其中 {differ} 条与旧 readFields 口径不同（> 0：旧字段确实不再进战斗）；拿掉 fw_wide 声明参数后战斗读法改变（{!wideBattle.Equals(wideNoDecl)}）");
        }

        // ── A. 清单 ──
        private static void CheckStage3Inventory(EmgSource src, EmgCompiled c)
        {
            var owners = src.Decls.GroupBy(d => d.Owner).Select(g => (Owner: g.Key, Kind: g.First().OwnerKind, Gate: g.First().StageGate,
                Pending: g.Any(d => EmgCatalog.PendingPorts.Contains(d.Port)))).ToList();
            var off = owners.Where(o => o.Gate <= c.EffectiveStage && !EmgCatalog.IsOwnerOnDeclTrack(o.Owner)).Select(o => o.Owner).ToList();
            var pending = owners.Where(o => o.Pending).Select(o => o.Owner).ToList();
            var gate4 = owners.Where(o => o.Gate > 3).Select(o => o.Owner).ToList();
            var mixedOff = FirmwareKinds.Rows.Where(r => FirmwareKinds.IsMixed(r.Id)).Where(r => FirmwareKinds.TryGetParents(r.Id, out string a, out string b)
                && !(EmgCatalog.IsOwnerOnDeclTrack(a) && EmgCatalog.IsOwnerOnDeclTrack(b))).Select(r => r.Id).ToList();
            int mixed = FirmwareKinds.Rows.Count(r => FirmwareKinds.IsMixed(r.Id));
            string dist = string.Join("；", owners.GroupBy(o => (o.Kind, o.Gate)).OrderBy(g => g.Key.Kind, StringComparer.Ordinal).ThenBy(g => g.Key.Gate)
                .Select(g => $"{g.Key.Kind} 阶段 {g.Key.Gate}：{g.Count()}"));
            int Count(string kind) => owners.Count(o => o.Kind == kind);
            Expect(off.Count == 0 && pending.Count == 0 && mixedOff.Count == 0 && Count("firmware") >= 52 && mixed == 30 && Count("component") >= 15 && Count("structure") == 6 /* EMG4-LOOT-01：+ 吸波伪装罩、装甲裙板 */ && Count("chassis") == 7,
                $"FGT-EMG-055 清单：声明 owner 分布（{dist}）；stageGate ≤ 3 而不在声明轨的 {off.Count} 个{Detail(off)}；用到未接通端口（{string.Join("、", EmgCatalog.PendingPorts)}）的 {pending.Count} 个{Detail(pending)}；" +
                $"30 条混合固件两父都在声明轨（不在的 {mixedOff.Count}）");
            Line($"    stageGate = 4（仍走旧轨）的 owner：表内 {gate4.Count} 个{(gate4.Count > 0 ? "：" + string.Join("、", gate4) : string.Empty)}；" +
                 "FG19A 第 2.1 节 comp_claw 的“拆解时拾取 2 米内金属碎屑（mat.debris sense → evt.pickup）”这一条 stageGate = 4 预留、没有承接卡，未落表，不计入本卡（拆解钳其余声明阶段 2 已生效）");
        }

        // ── B. 单轨比对 ──
        private static void CheckStage3SingleTrackCompare(EmgSource src)
        {
            var ids = FirmwareKinds.Rows.Select(r => r.Id).ToList();
            var dual = new List<string>();
            var legacy = new List<string>();
            var compose = new List<string>();
            int pairs = 0;
            var sw = Stopwatch.StartNew();
            float baseDmg = PreviewDamage(ComponentCatalog.CompGunId, Array.Empty<string>());
            foreach (string fw in ids)
            {
                if (FirmwareKinds.IsCore(fw))
                {
                    // 核心固件只经接入口生效：同样走这里（DeclDigest / SingleTrack 都把被测固件插进接入口）
                }
                float dmg = PreviewDamage(ComponentCatalog.CompGunId, new[] { fw });
                if (Math.Abs(dmg - baseDmg) > 1e-3f)
                {
                    compose.Add($"{fw}（{baseDmg:0.##} → {dmg:0.##}）");
                }
                foreach ((FirmwareCarrier carrier, string comp) in CarrierComponents)
                {
                    pairs++;
                    EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, carrier);
                    if (pr.LegacyOwners.Count > 0)
                    {
                        legacy.Add($"{carrier}/{fw}：{string.Join("、", pr.LegacyOwners)}");
                    }
                    string why = SingleTrackProblem3(comp, carrier, fw, false);
                    if (why != null)
                    {
                        dual.Add($"{carrier}/{fw}：{why}");
                    }
                }
            }
            sw.Stop();
            Expect(legacy.Count == 0, $"{ids.Count} 条固件（名表 44 + 新固件 8 + 混合 30）× 5 载体编程序：没有任何 owner 回落旧 readFields 轨（LegacyOwners 为空）{Detail(legacy)}");
            Expect(dual.Count == 0 && pairs >= 400, $"单轨比对（阶段 3）：{pairs} 对程序轨读法与独立推导的单轨读法逐字段一致（{sw.ElapsedMilliseconds} 毫秒）{Detail(dual)}");
            Expect(compose.Count == 0, $"ComposeEngine 固件语义撤出（ADR ⑪）：连射器（编译伤害型）插任一条已切换固件，电路编译伤害与不插时相同（{baseDmg:0.##}）{Detail(compose)}");
            string caught = SingleTrackProblem3("comp_gun", FirmwareCarrier.Projectile, "fw_split", true);
            string caught2 = SingleTrackProblem3("comp_ram", FirmwareCarrier.Melee, "fw_homing", true);
            Expect(caught != null && caught2 != null, $"负向：故意两轨同时结算（不跳任何旧字段）在阶段 3 被单轨比对报出——分裂：{caught}；寻的 · 格斗：{caught2}");
        }

        private static float PreviewDamage(string comp, string[] fws)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, comp, null, null, Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            return UplinkCompiler.CompileUplinked(board, fws).TotalNormalizedDamage;
        }

        /// <summary>
        /// 阶段 3 单轨比对：独立推导只看声明表（混合固件取两父）——旧字段只保留“本机出手上仍由过渡字段执行”的写端字段：
        /// 根节点效果写端的 <see cref="EmgProgramCompiler.TransitionFields"/>，除去内核直接执行的（追射 / 场、拆残骸 / 导线 / 蔓延、只走声明的新固件、给改写当介质的排放、读节拍的动能）；
        /// 排在触发器之后（子节点）的写端由内核在落点结算，不留字段；旧标签有根节点效果写端才挂，注入写端的标签退役。只改数字的端口由折叠按旧字段加回（两边同一折叠）。
        /// </summary>
        private static string SingleTrackProblem3(string comp, FirmwareCarrier carrier, string fw, bool forceDual)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, comp, null, null, Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { fw });
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            EmgProgramResult pr = EmgProgramCompiler.Compile(p);
            if (pr.IsEmpty || w.Mode == CombatWeaponMode.Projectile)
            {
                return pr.IsEmpty ? "程序为空（声明未生效）" : null;
            }
            bool damageFromCompile = !p.HasCannonPrimary && !(CarrierReadings.TryGetComponent(p.PrimaryId, out GameConfig.fg.CombatComponent cc) && cc.Damage > 0f);
            CombatReading actual = forceDual
                ? CombatSite.FoldProgramReadings(CarrierReadings.Build(p.PrimaryId, p.UtilityId, p.ChassisId, CombatSite.ReadingFirmware(p), damageFromCompile), pr, damageFromCompile)
                : CombatSite.ProgramReading(p, w.Reading, pr);
            EmgCompiled cat = EmgCatalog.Current;
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var retired = new HashSet<string>(StringComparer.Ordinal);
            bool tags = false;
            string[] owners = FirmwareKinds.TryGetParents(fw, out string pa, out string pb) ? new[] { pa, pb } : new[] { fw };
            byte bit = (byte)(1 << (int)carrier);
            bool afterTrig = false; // 程序序：触发器之后的指令落进子节点
            foreach (string o in owners)
            {
                if (!cat.OwnerIndex.TryGetValue(o, out int oi))
                {
                    return $"owner {o} 没有声明";
                }
                var mine = cat.Decls.Where(d => d.Owner == oi).ToList();
                bool declOnly = FirmwareKinds.TryGetRow(o, out GameConfig.fg.FirmwareKind row) && FirmwareKinds.IsDeclOnlyRow(row);
                bool medium = mine.Any(d => d.Dir == EmgDirs.Write && d.Role == EmgRole.Modify && cat.PortIdByCode[d.Port].StartsWith("mat.", StringComparison.Ordinal));
                bool beat = mine.Any(d => d.Dir == EmgDirs.Read && cat.PortIdByCode[d.Port] == "evt.beat");
                bool fireTrig = mine.Any(d => d.Dir == EmgDirs.Read && d.Role == EmgRole.Trigger && cat.PortIdByCode[d.Port] == "evt.fire");
                bool trigHere = false;
                foreach (EmgDeclRow d in mine.Where(d => d.Dir == EmgDirs.Write && (d.Carriers & bit) != 0))
                {
                    string port = cat.PortIdByCode[d.Port];
                    EmgFamily fam = EmgCatalog.FamilyOf(port);
                    trigHere |= port == "grm.trig";
                    retired.UnionWith(EmgProgramCompiler.RetiredTags(port));
                    if (fam == EmgFamily.Grammar || fam == EmgFamily.Meta || afterTrig)
                    {
                        continue;
                    }
                    tags = true;
                    bool kernel = EmgProgramCompiler.IsRootKernelPort(port) || EmgProgramCompiler.IsRootKernelWrite(port, d.Role) || declOnly
                                  || (medium && d.Role == EmgRole.Emit && port.StartsWith("mat.", StringComparison.Ordinal)) || (beat && port == "en.kinetic")
                                  || (fireTrig && d.Role == EmgRole.Emit && port.StartsWith("mat.", StringComparison.Ordinal)); // EMG4-FUSE-02：读 evt.fire 的物质排放 = 沿途排放，由内核按段点执行（FG19A 第 4.2 节）
                    if (!kernel)
                    {
                        // EMG4-FUSE-02：物质过渡字段只留 owner 自己有的那个（混合行读法是两父相加，与编译器 LegacySkip 同口径）
                        keep.UnionWith(EmgProgramCompiler.TransitionFields(port).Where(f => !port.StartsWith("mat.", StringComparison.Ordinal) || CarrierReadings.FieldsOf(o).Any(of => of.Item1 == f)));
                    }
                }
                afterTrig |= trigHere;
            }
            CombatReading expected = CombatSite.FoldProgramReadings(
                CarrierReadings.Build(p.PrimaryId, p.UtilityId, p.ChassisId, CombatSite.ReadingFirmware(p), damageFromCompile,
                    (f, field) => f == fw && (field == EmgProgramCompiler.TagsField ? !tags
                        : field.StartsWith(EmgProgramCompiler.TagsField + ":", StringComparison.Ordinal) ? retired.Contains(field.Substring(EmgProgramCompiler.TagsField.Length + 1))
                        : !keep.Contains(field))),
                pr, damageFromCompile);
            string a = ReadingFields(actual), e = ReadingFields(expected);
            if (a == e)
            {
                return null;
            }
            string[] aa = a.Split(';'), ee = e.Split(';');
            var diff = aa.Zip(ee, (x, y) => (x, y)).Where(t => t.x != t.y).Select(t => t.x + "≠" + t.y.Substring(t.y.IndexOf('=') + 1)).Take(3);
            return "读法不一致 " + string.Join("，", diff);
        }

        // ── C. 零死对 ──
        private static void CheckStage3ZeroDead(EmgSource src)
        {
            var gate = src.Decls.GroupBy(d => d.Owner).ToDictionary(g => g.Key, g => g.First().StageGate, StringComparer.Ordinal);
            // 读 evt.react 的反应增幅在 CheckAmplify 的交互场景里比；新固件里条件型（阵亡 / 反弹 / 穿透 / 过热 / 冷却完成 / 入场 / 接入）由 FgEmgAddonFirmwareSelfCheck 的条件场景比；
            // 阶段 3 的元程序（写 meta.*）与协同 / 齐射（读 evt.kill / evt.ping / evt.marked / tmp.sync）由 FgEmgStage3FirmwareSelfCheck B 段与 G 段（5 载体 × 首位 / 末位条件场景）比
            List<string> list = FirmwareKinds.BaseRows.Select(r => r.Id).Where(id => !Stage3Conditional(src, id))
                .Concat(FirmwareKinds.AddonRows.Select(r => r.Id).Where(id => !IsConditionalAddon(src, id)))
                .Where(id => gate.TryGetValue(id, out int g) && g <= 3)
                // EMG3-FUSE-01：接线（写 grm.anchor）是语法结构，单独放首位 / 末位按设计不起作用、预览写原因（FgEmgFuseSelfCheck B6）；行为由 [A, 接线, B] 的 C1 / C3 比
                .Where(id => !src.Decls.Any(d => d.Owner == id && d.Port == "grm.anchor")).ToList();
            var cond = FirmwareKinds.BaseRows.Select(r => r.Id).Where(id => Stage3Conditional(src, id)).ToList();
            Line($"    条件型固件 {cond.Count} 条（{string.Join("、", cond)}）：行为要先造出条件（交互 / 被击 / 击杀残骸 / 地面液体 / 中继组对齐），" +
                 "反应增幅见下方交互场景，其余由各自专段在阶段 ≥ 2 的条件场景里逐载体比对（FgEmgFieldSelfCheck 偏折、FgEmgReclaimSelfCheck 回收 / 蔓延、FgEmgTempoSelfCheck 谐振）");
            var dead = new List<string>();
            int combos = 0;
            var sw = Stopwatch.StartNew();
            foreach ((FirmwareCarrier carrier, string comp) in CarrierComponents)
            {
                string baseline = DeclDigest(comp, new[] { DeclFiller }, out _, out _);
                string none = DeclDigest(comp, Array.Empty<string>(), out _, out _);
                foreach (string fw in list)
                {
                    if (fw == DeclFiller)
                    {
                        combos++;
                        if (DeclDigest(comp, new[] { fw }, out _, out _) == none)
                        {
                            dead.Add($"{carrier}/{fw}（单条）");
                        }
                        continue;
                    }
                    combos += 2;
                    if (DeclDigest(comp, new[] { fw, DeclFiller }, out _, out _) == baseline)
                    {
                        dead.Add($"{carrier}/{fw}（首位）");
                    }
                    if (DeclDigest(comp, new[] { DeclFiller, fw }, out _, out _) == baseline)
                    {
                        dead.Add($"{carrier}/{fw}（末位）");
                    }
                }
            }
            // 混合固件：整条装与不装行为指纹不同；展开后的两条每条都有行为——整条与“只装一条父固件”也不同（另一半看得出来），父固件是条件型的同样比（四个场景里含条件场景）。
            // 固定 4 秒场景里看不出来的半边（审查修复第 1 轮，FG-GAP-201 定因）再进延长场景：8 秒（布区落区 4 秒到期后才放子区）、射弹另加慢速长飞弹体（飞行中按拍布弹）；仍看不出来判失败
            var mixDead = new List<string>();
            var halfHidden = new List<string>();
            var halfLate = new List<string>();
            int mixPairs = 0;
            foreach ((FirmwareCarrier carrier, string comp) in CarrierComponents)
            {
                string none = DeclDigest(comp, Array.Empty<string>(), out _, out _);
                foreach (FwRow r in FirmwareKinds.Rows.Where(r => FirmwareKinds.IsMixed(r.Id)))
                {
                    FirmwareKinds.TryGetParents(r.Id, out string a, out string b);
                    mixPairs++;
                    string m = DeclDigest(comp, new[] { r.Id }, out _, out _);
                    if (m == none)
                    {
                        mixDead.Add($"{carrier}/{r.Id}");
                        continue;
                    }
                    foreach (string only in new[] { a, b })
                    {
                        if (m != DeclDigest(comp, new[] { only }, out _, out _))
                        {
                            continue;
                        }
                        if (LongDigest(carrier, comp, r.Id) != LongDigest(carrier, comp, only))
                        {
                            halfLate.Add($"{carrier}/{r.Id}（固定场景 = 只装 {only}）");
                        }
                        else
                        {
                            halfHidden.Add($"{carrier}/{r.Id}（= 只装 {only}；落点靶场景 {EventDigestOf(comp, r.Id, 2)} 对 {EventDigestOf(comp, only, 2)}）");
                        }
                    }
                }
            }
            sw.Stop();
            Expect(list.Count >= 35 && dead.Count == 0,
                $"阶段 3 零死对：{list.Count} 条固件 × 5 载体 × 首位 / 末位共 {combos} 对行为指纹都与只有填充指令不同，死对 {dead.Count}{Detail(dead)}");
            Expect(mixPairs >= 150 && mixDead.Count == 0 && halfHidden.Count == 0,
                $"混合固件 30 条 × 5 载体共 {mixPairs} 对：整条装与不装行为指纹都不同（死对 {mixDead.Count}）；展开后两条各有行为（整条 ≠ 只装任一父固件），" +
                $"固定场景里看不出、延长场景里看得出的 {halfLate.Count} 处{(halfLate.Count > 0 ? "：" + string.Join("、", halfLate) : string.Empty)}，仍看不出 {halfHidden.Count}（{sw.ElapsedMilliseconds} 毫秒）{Detail(mixDead)}{Detail(halfHidden)}");
        }

        /// <summary>延长场景指纹：同一正式编译武器跑 8 游戏秒（布区落区存续 4 秒，到期才放子区）；射弹另加慢速长飞弹体（2 米 / 秒飞 4.5 秒，飞行中按拍布弹，固定场景的弹体 0.1 秒就命中、跨不到拍界）。</summary>
        private static string LongDigest(FirmwareCarrier carrier, string comp, string fw)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, comp, null, null, Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { fw });
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            EmgProgramResult program = EmgProgramCompiler.Compile(p);
            if (!program.IsEmpty && w.Mode != CombatWeaponMode.Projectile)
            {
                w.Reading = CombatSite.ProgramReading(p, w.Reading, program);
            }
            string digest = RunArena(w, 8f, prepared: true, program, comp).Digest
                            + "#" + EventDigest(w, program, comp, 0) + "#" + EventDigest(w, program, comp, 1) + "#" + EventDigest(w, program, comp, 2);
            return carrier == FirmwareCarrier.Projectile ? digest + "#" + SlowFlightDigest(program) : digest;
        }

        /// <summary>
        /// 条件事件场景（父固件是条件型时用）：<paramref name="mode"/> 0 = 击杀场景——敌人只剩 4 血（evt.kill → 残骸回收修复出手者，出手者 300 / 500 血）；
        /// 1 = 被击场景——加一个带武器的敌人每 0.5 秒打本机一次（evt.hurt → 偏折等）；2 = 落点目标场景——只打一发，每份子载荷（子弹药 / 子区）一出现就在它的落点前放一个 1 血靶
        /// （排在触发器后的那一半要子载荷自己击杀才触发，如锈蚀回收弹；与 FgEmgReclaimSelfCheck B2 同一思路，按子载荷实际位置放、不按载体写死）。
        /// 跑 6 游戏秒，指纹 = 各单位血量 / 存活、靶被击杀数、斑块 / 场 / 子载荷 / 触发计数。
        /// </summary>
        private static string EventDigestOf(string comp, string fw, int mode)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, comp, null, null, Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { fw });
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            EmgProgramResult program = EmgProgramCompiler.Compile(p);
            if (!program.IsEmpty && w.Mode != CombatWeaponMode.Projectile)
            {
                w.Reading = CombatSite.ProgramReading(p, w.Reading, program);
            }
            string tree = string.Join(" / ", program.KernelNodes.Select((n, ni) => $"n{ni}<{n.Parent}:{n.TrigEvent}:{n.SubForm}[" +
                string.Join(",", program.KernelInstrs.Skip(n.FirstInstr).Take(n.InstrCount).Select(x => $"{x.Ordinal}:{x.Port}/{x.Role}/v{x.Verb}/f{(int)x.Flags}")) + "]"));
            return EventDigest(w, program, comp, mode) + " 树 " + tree;
        }

        private static string EventDigest(CombatWeapon w, EmgProgramResult program, string comp, int mode)
        {
            bool hurt = mode == 1;
            using Arena a = Arena.Build(w, true, null, program, comp);
            int shooter = 0;
            if (hurt)
            {
                CombatSpawn sp = Hostile(new double2(-1.5, -1.5), 5000f, 5000f);
                sp.Weapon = a.K.AddWeapon(new CombatWeapon
                {
                    Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 5f, Cooldown = 0.5f,
                    Program = -1, Reading = new CombatReading { Carrier = CombatCarrier.Projectile, DamageScale = 1f, CooldownScale = 1f },
                });
                shooter = a.K.Spawn(sp);
            }
            else if (mode == 0)
            {
                for (int i = 1; i < ArenaCount; i++)
                {
                    a.K.SetHealth(a.Ids[i], 4f, 5000f, true);
                }
            }
            var sb = new StringBuilder();
            var handled = new HashSet<long>();
            var dummies = new List<int>();
            for (int s = 0; s < 360; s++)
            {
                if (hurt && s % 30 == 0)
                {
                    a.K.FireAt(shooter, a.Ids[ArenaAttacker], a.K.Time);
                }
                if (mode == 2 && s == 3)
                {
                    a.K.SetPosition(a.Ids[ArenaAttacker], new double2(-80, 0)); // 只打一发：出手者挪出射程，后续出手的根节点（布区落区）不抢先打死落点靶
                }
                for (int i = 0; mode == 2 && dummies.Count < 16 && i < a.K.ZoneCount; i++)
                {
                    if (a.K.TryGetZone(i, out CombatZone z) && z.EmgSub != 0 && handled.Add(z.EmgId))
                    {
                        dummies.Add(a.K.Spawn(Hostile(z.Pos, 1f, 1f)));
                    }
                }
                for (int i = 0; mode == 2 && dummies.Count < 16 && i < a.K.ProjectileCount; i++)
                {
                    if (a.K.TryGetProjectile(i, out CombatProjectile pr) && (pr.Flags & EmgProjFlags.Sub) != 0 && handled.Add(-1L - pr.Id))
                    {
                        double2 v = (double2)pr.Vel;
                        dummies.Add(a.K.Spawn(Hostile(pr.Pos + (math.lengthsq(v) > 1e-9 ? math.normalize(v) * 0.8 : double2.zero), 1f, 1f)));
                    }
                }
                a.K.Step(Dt, a.K.Time + Dt);
            }
            sb.Append(dummies.Count(id => !a.K.TryGetUnit(id, out CombatUnitView dv) || !dv.Alive)).Append('k');
            for (int i = 0; i < ArenaCount; i++)
            {
                bool ok = a.K.TryGetUnit(a.Ids[i], out CombatUnitView v);
                sb.Append(ok ? v.Health.ToString("F1", CultureInfo.InvariantCulture) : "-").Append(ok && v.Alive ? 'A' : 'D').Append('|');
            }
            EmgBusStats st = a.K.EmgBusStats;
            sb.Append('w').Append(a.K.WreckCount).Append('s').Append(a.K.EmgScalars.Salvages).Append('/');
            sb.Append(a.K.PatchCount).Append('/').Append(a.K.FieldCount).Append('/').Append(st.SubSpawned).Append('/').Append(st.TriggerWakes).Append('/').Append(a.K.Counters.ZonesSpawned);
            return sb.ToString();
        }

        /// <summary>慢速长飞弹体：程序同正式编译，弹体 2 米 / 秒、寿命 10 秒，目标在 9 米外（飞 4.5 秒，跨 9 个拍界）；指纹 = 子载荷数、剩血、各物质斑块数。</summary>
        private static string SlowFlightDigest(EmgProgramResult program)
        {
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using var k = new CombatKernel(cfg, 32);
            CombatSite.ApplyMatters(k);
            CombatSite.ApplyRules(k);
            CombatSite.ApplyPhaseRules(k);
            var w = new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 10f, Cooldown = 10f,
                Program = program.IsEmpty ? -1 : CombatSite.RegisterProgram(k, program), SpawnFlight = 1, ProjectileSpeed = 2f, ProjectileRadius = 0.15f, ProjectileLife = 10f,
                Reading = new CombatReading { Carrier = CombatCarrier.Projectile, DamageScale = 1f, CooldownScale = 1f },
            };
            int me = k.Spawn(Machine(double2.zero, k.AddWeapon(w), 300f, 500f));
            int foe = k.Spawn(Hostile(new double2(0, 9), 5000f, 5000f));
            k.FireAt(me, foe, k.Time);
            for (int s = 0; s < 330; s++)
            {
                k.Step(Dt, k.Time + Dt);
            }
            var mats = new int[32];
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter < mats.Length)
                {
                    mats[z.Matter]++;
                }
            }
            float hp = k.TryGetUnit(foe, out CombatUnitView v) ? v.Health : 0f;
            return $"{k.EmgBusStats.SubSpawned}|{hp.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}|{string.Join(",", mats)}";
        }

        /// <summary>阶段 3 条件型：触发事件在常规场景里不自然发生（被击 / 击杀 / 交互……；命中、出手、终结、节拍除外），或要先有残骸 / 地面液体 / 中继组对齐才看得见。按声明判，不按 ID。</summary>
        private static bool Stage3Conditional(EmgSource src, string id) =>
            src.Decls.Any(d => d.Owner == id && ((d.Dir == "read" && d.Role == "trigger" && d.Port != "evt.hit" && d.Port != "evt.fire" && d.Port != "evt.expire" && d.Port != "evt.beat")
                                                 || (d.Dir == "read" && d.Port == "tmp.sync") || (d.Dir == "write" && d.Port == "ent.wreck")
                                                 || (d.Dir == "write" && d.Role == "modify" && d.Port.StartsWith("mat.", StringComparison.Ordinal))));

        /// <summary>反应增幅（FG19A 第 1 节第 22 行）：[注热, 反应增幅] / [反应增幅, 注热] 在敌人脚下有油污斑块（注热点燃 = 本载荷引发的交互）时比 [注热] 多放出子载荷；没有斑块时不多放（对照）。</summary>
        private static void CheckAmplify()
        {
            const string amp = "fw_amplify", heat = FirmwareCatalog.FwOverloadId;
            var bad = new List<string>();
            var detail = new List<string>();
            foreach ((FirmwareCarrier carrier, string comp) in CarrierComponents)
            {
                (long s0, float h0) = AmplifyRun(comp, new[] { heat }, true);
                (long s1, float h1) = AmplifyRun(comp, new[] { heat, amp }, true);
                (long s2, float h2) = AmplifyRun(comp, new[] { amp, heat }, true);
                (long d1, _) = AmplifyRun(comp, new[] { heat, amp }, false);
                (long q, _) = AmplifyRun(comp, new[] { amp }, true);
                detail.Add($"{carrier}：有油 放出批数 [注热]{s0} / [注热, 增幅]{s1} / [增幅, 注热]{s2}（剩血 {h0:0}/{h1:0}/{h2:0}），无油 [注热, 增幅]{d1}，只有增幅 {q}");
                // 末位：本载荷（根节点注热）引发的交互点放出子载荷，油污斑块让交互更多；首位：注热落进增幅的子节点（触发器后的指令），根节点不再注热 → 行为与 [注热] 不同、也没有上游交互可放；
                // 对照：只装增幅（没有任何会引发交互的写端）一次也不放
                if (!(s1 > s0 && s1 > d1 && Mathf.Abs(h2 - h0) > 1e-3f && s2 == 0 && q == 0))
                {
                    bad.Add(carrier.ToString());
                }
            }
            Expect(bad.Count == 0, $"反应增幅 × 5 载体：末位——本载荷引发的交互（注热点燃油污 / 着火）处放出子载荷、斑块越多放得越多；首位——下游注热进子节点、行为与 [注热] 不同；只装增幅不放（{string.Join("；", detail)}）{Detail(bad)}");
        }

        private static (long splits, float hostileHp) AmplifyRun(string comp, string[] fws, bool oil)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, comp, null, null, Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, fws);
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            EmgProgramResult program = EmgProgramCompiler.Compile(p);
            if (!program.IsEmpty && w.Mode != CombatWeaponMode.Projectile)
            {
                w.Reading = CombatSite.ProgramReading(p, w.Reading, program);
            }
            using Arena a = Arena.Build(w, true, null, program, comp);
            if (oil)
            {
                for (int i = 1; i < ArenaCount; i++)
                {
                    if (a.K.TryGetUnit(a.Ids[i], out CombatUnitView v))
                    {
                        a.K.SpawnPatch(EmgMatterConst.Oil, v.Position, 1.6f, 3f, 30f);
                    }
                }
            }
            a.Run(4f);
            float hp = 0f;
            for (int i = 1; i < ArenaCount; i++)
            {
                hp += a.K.TryGetUnit(a.Ids[i], out CombatUnitView v) ? v.Health : 0f;
            }
            return (a.K.EmgBusStats.TriggerWakes, hp); // 触发读端被唤起的次数 = 放出子载荷的批数
        }

        /// <summary>
        /// DEBT-EMG1E2E01-02：腐蚀弹头（命中 trigger + 排酸，没有物质过渡字段）阶段 3 命中处排真酸斑块；DEBT-EMG1TRIG01-01：[触发器, 酸液] 装在布区上，
        /// 子区存续期间每个区域节拍排一次（不是只在到期排一次）——数子区落下后、到期前出现的酸斑块。
        /// </summary>
        private static void CheckRootEmitAndSubZoneTicks()
        {
            int Acid(CombatKernel k) => LivePatches(k, EmgMatterConst.Acid);
            (int acid, int acidOff) = (CountAfter("comp_gun", new[] { "fw_corrode" }, 2f, Acid), CountAfter("comp_gun", Array.Empty<string>(), 2f, Acid));
            EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_corrode" }, FirmwareCarrier.Projectile);
            bool kernel = pr.KernelInstrs.Any(x => (x.Flags & EmgInstrFlags.RootKernel) != 0 && x.Role > EmgConst.RoleLastRead && x.Verb == EmgCatalog.VerbCode("emit"));
            Expect(kernel && acid > 0 && acidOff == 0, $"腐蚀弹头（DEBT-EMG1E2E01-02）：命中排酸写端由内核在命中处执行（RootKernel {kernel}），2 秒后场上酸斑块 {acid} 块（不装 {acidOff}）");
            string fieldComp = CarrierComponents.First(t => t.Carrier == FirmwareCarrier.Field).Component;
            int early = CountAfter(fieldComp, new[] { "fw_split", "fw_acid" }, 1.6f, Acid);
            int earlyBase = CountAfter(fieldComp, new[] { "fw_split" }, 1.6f, Acid);
            Expect(early > earlyBase, $"布区子区按节拍排放（DEBT-EMG1TRIG01-01，FGR-EMG-035）：[分裂, 酸液] 布区开打 1.6 秒（子区还没到期）场上酸斑块 {early} 块 > 只有分裂 {earlyBase} 块");
            // 审查修复（第 1 轮）：混合固件里腐蚀弹头那一半按父固件自己的旧字段判过渡（混合行 readFields 两父相加）——另一父带驻留区 / 拖尾时，命中排酸照样由内核执行
            var mixAcid = new List<string>();
            bool mixOk = true;
            foreach ((string mix, string other) in new[] { ("fw_mix_etchshell", "fw_oilleak"), ("fw_mix_causticshell", "fw_burntrail"), ("fw_mix_stickyshell", "fw_adhesive") })
            {
                int on = CountAfter("comp_gun", new[] { mix }, 2f, Acid), off = CountAfter("comp_gun", new[] { other }, 2f, Acid);
                bool rk = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { mix }, FirmwareCarrier.Projectile).KernelInstrs
                    .Any(x => (x.Flags & EmgInstrFlags.RootKernel) != 0 && x.Port == EmgMatterConst.PortBase + EmgMatterConst.Acid);
                mixOk &= rk && on > 0 && off == 0;
                mixAcid.Add($"{mix} 酸斑块 {on}（只装 {other} {off}，排酸 RootKernel {rk}）");
            }
            // 溶蚀弹头另一半（漏油）的油污会被酸溶掉（溶蚀反应），它的行为由零死对的“整条 ≠ 只装一条父固件”覆盖
            Expect(mixOk, $"混合固件腐蚀那一半命中排酸（DEBT-EMG1E2E01-02 混合部分）：{string.Join("；", mixAcid)}");
            CheckSubZoneTickMeta(fieldComp, Acid);
        }

        /// <summary>某种物质此刻在场的斑块数（斑块表有墓碑，逐槽跳过 Matter = 0）。</summary>
        private static int LivePatches(CombatKernel k, int matter)
        {
            int n = 0;
            for (int i = 0; i < k.PatchSlots; i++)
            {
                n += k.TryGetPatch(i, out CombatZone z) && z.Matter == matter && z.Until > k.Time ? 1 : 0;
            }
            return n;
        }

        /// <summary>
        /// 审查修复（第 1 轮，FGR-EMG-040～043）：子区按区域节拍的排放与到期同一口径过元程序。测试目录 = 真实表 + 一条“若目标被标记”元程序测试行（正式落表归 EMG3-FW-02）；
        /// [分裂, 若目标被标记, 酸液] 布区：圈内敌人都被标记时子区节拍排酸，都没标记时 1.6 秒内（子区还没到期）一块也不排。
        /// </summary>
        private static void CheckSubZoneTickMeta(string fieldComp, Func<CombatKernel, int> acid)
        {
            const string ifm = "fw_selftest_fw01_ifmark";
            EmgSource src = EmgCatalog.FromTables();
            Func<string, string, bool> owners = src.OwnerExists;
            src.OwnerExists = (o, k) => o == ifm || owners(o, k);
            src.Decls.Add(new EmgSource.Decl { Id = ifm + ".1", Owner = ifm, OwnerKind = "firmware", Seq = 1, Dir = "read", Port = "evt.marked", Role = "condition", Amount = 1f, Carriers = "all", TextKey = $"emg.decl.{ifm}.1", StageGate = 3 });
            src.Decls.Add(new EmgSource.Decl { Id = ifm + ".2", Owner = ifm, OwnerKind = "firmware", Seq = 2, Dir = "write", Port = "meta.if", Role = "modify", Amount = 1f, Carriers = "all", TextKey = $"emg.decl.{ifm}.2", StageGate = 3 });
            try
            {
                EmgCatalog.OverrideForTests(src);
                EmgProgramResult prog = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_split", ifm, "fw_acid" }, FirmwareCarrier.Field);
                int Run(bool mark)
                {
                    BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, fieldComp, null, null, Array.Empty<string>());
                    board.TrySetUplink(UplinkSlot);
                    BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { "fw_split", "fw_acid" });
                    CombatWeapon w = CombatSite.MachineWeaponFrom(p);
                    w.Reading = CombatSite.ProgramReading(p, w.Reading, EmgProgramCompiler.Compile(p));
                    using Arena a = Arena.Build(w, false, null, prog, fieldComp); // 不预置（预置场景自带一个已标记的敌人）
                    for (int i = 1; mark && i < ArenaCount; i++)
                    {
                        a.K.SetMarkedUntil(a.Ids[i], 30.0);
                    }
                    a.Run(1.6f);
                    return acid(a.K);
                }
                int on = Run(true), off = Run(false);
                string tree = string.Join(" / ", prog.KernelNodes.Select((n, ni) => $"节点{ni}←{n.Parent}:{n.TrigEvent}[" +
                    string.Join(",", prog.KernelInstrs.Skip(n.FirstInstr).Take(n.InstrCount).Select(x => $"{x.Ordinal}:{x.Port}/{x.Role}")) + "]"));
                Expect(!prog.IsEmpty && on > 0 && off == 0,
                    $"布区子区节拍排放过元程序（FGR-EMG-043）：[分裂, 若目标被标记, 酸液] 圈内已标记 1.6 秒酸斑块 {on} 块、未标记 {off} 块（条件为假时节拍也不排，与到期同一口径）" +
                    $"（程序 {prog.Program.Flags}：{tree}）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        private static int CountAfter(string comp, string[] fws, float seconds, Func<CombatKernel, int> count)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, comp, null, null, Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, fws);
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            EmgProgramResult program = EmgProgramCompiler.Compile(p);
            if (!program.IsEmpty && w.Mode != CombatWeaponMode.Projectile)
            {
                w.Reading = CombatSite.ProgramReading(p, w.Reading, program);
            }
            using Arena a = Arena.Build(w, true, null, program, comp);
            a.Run(seconds);
            return count(a.K);
        }

        // ── D. 部件 ──
        /// <summary>散热鳍（FG19A 第 2.2 节 prop.heat modify 5 / 秒，泡在冷却液或水里 ×2）：起真实内核比 1 游戏秒的散热。</summary>
        private static void CheckHeatSinkFin()
        {
            string fin = ComponentCatalog.StructFinId;
            BlueprintCircuitBoard withFin = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, fin, Array.Empty<string>());
            BlueprintCircuitPreview pf = UplinkCompiler.CompileUplinked(withFin, Array.Empty<string>());
            CombatWeapon wf = CombatSite.MachineWeaponFrom(pf);
            (float mul, uint mask) = EmgCatalog.HeatSinkWet();
            float declAmount = EmgCatalog.PartWriteAmount("prop.heat", EmgRole.Modify, new[] { fin });
            float dry = HeatAfter(wf, true, 0), wet = HeatAfter(wf, true, EmgMatterConst.Water), cool = HeatAfter(wf, true, EmgMatterConst.Coolant),
                oil = HeatAfter(wf, true, EmgMatterConst.Oil), bare = HeatAfter(wf, false, EmgMatterConst.Water);
            float dis = wf.Dissipation;
            bool ok = pf.HasHeatSinkStructure && Mathf.Approximately(wf.HeatSinkBonus, declAmount) && Mathf.Approximately(mul, 2f)
                      && (mask & (1u << EmgMatterConst.Water)) != 0 && (mask & (1u << EmgMatterConst.Coolant)) != 0
                      && Mathf.Abs((80f - bare) - dis) < 0.2f && Mathf.Abs((80f - dry) - (dis + declAmount)) < 0.2f
                      && Mathf.Abs((80f - wet) - (dis + declAmount * mul)) < 0.2f && Mathf.Abs(wet - cool) < 1e-3f && Mathf.Abs(oil - dry) < 1e-3f;
            Expect(ok, $"散热鳍（声明轨）：散热件那份 = 声明幅度 {declAmount}/秒（武器 HeatSinkBonus {wf.HeatSinkBonus}）；1 秒积热 80 → 不装 {bare:0.##}、装 {dry:0.##}、站水里 {wet:0.##}、站冷却液里 {cool:0.##}、站油污里 {oil:0.##}" +
                       $"（基础散热 {dis}，泡液倍率 {mul}，物质掩码 0x{mask:X}）");
            // EMG4-CLEAN-01：原“旧轨对照 emg.stage = 1”改为负向——低值不再生效
            EmgSource s1 = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> t0 = s1.Tuning;
            s1.Tuning = id => id == EmgCatalog.StageKey ? (true, 1f) : t0(id);
            try
            {
                EmgCatalog.OverrideForTests(s1);
                CombatWeapon wl = CombatSite.MachineWeaponFrom(UplinkCompiler.CompileUplinked(withFin, Array.Empty<string>()));
                float legacyWet = HeatAfter(wl, true, EmgMatterConst.Water);
                // EMG4-CLEAN-01（卡片负向）：emg.stage 填回 1 不再生效——散热鳍仍走声明轨、泡水仍加倍（旧轨“常量 +5、泡水不加倍”已删）
                Expect(EmgCatalog.EffectiveStage == 4 && EmgCatalog.IsOwnerOnDeclTrack(fin) && Mathf.Approximately(wl.HeatSinkBonus, declAmount)
                       && Mathf.Abs(legacyWet - wet) < 1e-3f,
                    $"负向：emg.stage 填回 1 不再生效（生效阶段 {EmgCatalog.EffectiveStage}）：散热鳍仍走声明轨，散热件 {wl.HeatSinkBonus}/秒、站水里 1 秒 80 → {legacyWet:0.##}（= 阶段 4 的 {wet:0.##}）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        /// <summary>一台机器积热 80、不开火，1 游戏秒后的积热；<paramref name="matter"/> &gt; 0 = 脚下放一块该物质斑块。</summary>
        private static float HeatAfter(CombatWeapon w, bool sink, byte matter)
        {
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using var k = new CombatKernel(cfg, 8);
            CombatSite.ApplyMatters(k);
            int wi = k.AddWeapon(w);
            CombatSpawn s = Machine(new double2(0, 0), wi);
            s.Heat = 80f;
            if (sink)
            {
                s.Flags |= CombatUnitFlags.HeatSink;
            }
            int id = k.Spawn(s);
            if (matter > 0)
            {
                k.SpawnPatch(matter, new double2(0, 0), 2f, 3f, 30f);
            }
            for (int i = 0; i < 60; i++)
            {
                k.Step(Dt, k.Time + Dt);
            }
            return k.TryGetUnit(id, out CombatUnitView v) ? v.Heat : -1f;
        }

        /// <summary>阶段 2 / 3 的作战组件声明在自身载体上装 / 不装行为指纹不同（主组件：关 = 不出手的空武器）；结构 / 底盘 / 敌方行由各自专段覆盖。</summary>
        private static void CheckStage3Parts(EmgSource src)
        {
            var comps = src.Decls.Where(d => d.OwnerKind == "component" && d.StageGate >= 2 && d.StageGate <= 3).Select(d => d.Owner).Distinct().ToList();
            var dead = new List<string>();
            foreach (string owner in comps)
            {
                CombatWeapon on = HostWeapon(HomeValleyLayout.Erc003ChassisId, owner, null);
                var off = new CombatWeapon { Mode = CombatWeaponMode.Instant, HasOutput = 0 };
                if (RunArena(on, 4f, prepared: true, conditions: true).Digest == RunArena(off, 4f, prepared: true, conditions: true).Digest)
                {
                    dead.Add(owner);
                }
            }
            Expect(comps.Count >= 3 && dead.Count == 0,
                $"阶段 2 / 3 作战组件 {comps.Count} 个（{string.Join("、", comps)}）装 / 不装行为指纹不同{Detail(dead)}；" +
                "结构模块：散热鳍见上、信号中继见 FgEmgSquadSelfCheck；底盘特性 7 条见 FgEmgChassisSelfCheck；澄净敌方排放 4 行见 FgEmgMigrationSelfCheck（各自宿主上的开 / 关）");
        }

        // ── E. 有源 ──
        private static void CheckStage3Sourced(EmgSource src)
        {
            var live = src.Decls.Where(d => d.StageGate <= 3).ToList();
            var writers = new HashSet<string>(live.Where(d => d.Dir == "write").Select(d => d.Port), StringComparer.Ordinal);
            string World(string port)
            {
                if (port.StartsWith("evt.", StringComparison.Ordinal))
                {
                    return "内核事件总线（EMG1-BUS-01；evt.react = 规则结算，evt.beat = 节拍）";
                }
                if (port.StartsWith("ter.", StringComparison.Ordinal))
                {
                    return "地图障碍 / 掩体（PCG 地形）";
                }
                if (port.StartsWith("tmp.", StringComparison.Ordinal))
                {
                    return "内核全局节拍（EMG3-TMP-01）";
                }
                if (port.StartsWith("mat.", StringComparison.Ordinal))
                {
                    return "世界常驻斑块 / 敌方排放（EMG2-MAT-02）";
                }
                switch (port)
                {
                    case "chs.moving":
                    case "chs.water": return "内核单位移动 / 水面状态（EMG3-CHS-01）";
                    case "prop.heat": return "内核武器积热（每次出手）";
                    case "prop.mass":
                    case "prop.magnetic":
                    case "prop.conductive":
                    case "prop.integrity": return "实体公共属性档案（EMG1-ENT-01）";
                    case "prop.charge": return writers.Contains("en.elec") ? "en.elec 写者注入的电荷" : null;
                    case "fld.deflect": return EnemyDefs.TryGet("enemy_prism", out GameConfig.fg.MechEnemy row) && row.Behavior == EnemyDefs.BehaviorDeflect ? "敌方偏折单位" : null;
                    case "sqd.relay": return "信号中继模块（结构模块 scope，按中继组）";
                    default: return null;
                }
            }
            var reads = live.Where(d => d.Dir == "read").Select(d => d.Port).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            var sourceless = reads.Where(p => !writers.Contains(p) && World(p) == null).ToList();
            Expect(reads.Count >= 25 && sourceless.Count == 0,
                $"阶段 3 已生效声明读到 {reads.Count} 个端口，无源 {sourceless.Count}{Detail(sourceless)}；有声明写者的 {reads.Count(writers.Contains)} 个，其余靠世界源");
        }

        // ── F. 存读档 ──
        private static void CheckStage3SaveLoad()
        {
            // 内核：反应增幅（油污斑块 + 注热）战斗中途快照往返、续跑一致
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, ComponentCatalog.StructFinId, Array.Empty<string>());
            board.TrySetUplink(UplinkSlot);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { FirmwareCatalog.FwOverloadId, "fw_amplify" });
            CombatWeapon w = CombatSite.MachineWeaponFrom(p);
            EmgProgramResult program = EmgProgramCompiler.Compile(p);
            using (Arena a = Arena.Build(w, true, null, program, ComponentCatalog.CompGunId))
            {
                for (int i = 1; i < ArenaCount; i++)
                {
                    if (a.K.TryGetUnit(a.Ids[i], out CombatUnitView v))
                    {
                        a.K.SpawnPatch(EmgMatterConst.Oil, v.Position, 1.6f, 3f, 30f);
                    }
                }
                a.K.SetFlag(a.Ids[ArenaAttacker], CombatUnitFlags.HeatSink, true);
                a.Run(1.5f);
                CombatKernel k = a.K;
                long split0 = k.EmgBusStats.TriggerWakes;
                byte[] snap = k.Serialize();
                using var b = new CombatKernel(k.Config, 64);
                CombatSite.ApplyMatters(b); // 内容表（物质 / 规则 / 相态）不进快照，读档前按同一张表重建（与 CombatSite 读档同一做法）
                CombatSite.ApplyRules(b);
                CombatSite.ApplyPhaseRules(b);
                CombatLoadResult lr0 = b.Load(snap);
                bool loaded = lr0 == CombatLoadResult.Ok && b.StateHash() == k.StateHash();
                for (int i = 0; i < 120; i++)
                {
                    double t = k.Time + Dt;
                    k.Step(Dt, t);
                    b.Step(Dt, t);
                }
                Expect(split0 > 0 && loaded && b.StateHash() == k.StateHash() && CombatKernel.PeekFormat(snap) == CombatConst.FormatVersion,
                    $"反应增幅 + 散热鳍战斗中途（已放出子载荷 {split0} 次）存快照：读回 {lr0}、逐位一致 {loaded}，再跑 2 秒仍一致（快照格式 {CombatKernel.PeekFormat(snap)}，本卡不新增内核状态、不升格式）");
            }

            // 战役存档：全部固件芯片 + 带散热鳍 / 混合固件的蓝图往返后不变，读回后全部仍能编出声明轨程序
            CampaignState s = NewState(9131, unlockAll: true);
            foreach (FwRow r in FirmwareKinds.Rows)
            {
                PrimitiveInventory.GrantCrafted(s, r.Id);
            }
            var bp = new BlueprintRecord
            {
                BlueprintId = "bp_emg3fw01", DisplayName = "EMG3-FW-01", ActiveVersion = 1,
                Versions = new[]
                {
                    new BlueprintVersionRecord
                    {
                        Version = 1, ChassisId = HomeValleyLayout.Erc003ChassisId, PrimaryId = ComponentCatalog.CompGunId, StructureId = ComponentCatalog.StructFinId,
                        OrderedFirmwareIds = new[] { "fw_split", FirmwareCatalog.FwHomingId, FirmwareKinds.Rows.First(r => FirmwareKinds.IsMixed(r.Id)).Id },
                    },
                },
            };
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Append(bp).ToArray();
            string Chips(CampaignState st) => string.Join(",", (st.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Where(FirmwareLibrary.IsFirmwareChip).Select(x => x.CardDefId).OrderBy(x => x, StringComparer.Ordinal));
            string Bps(CampaignState st) => string.Join("|", st.BlueprintRecords.Select(JsonUtility.ToJson));
            string chips0 = Chips(s), bps0 = Bps(s);
            SaveResult sr = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            LoadResult lr = CampaignSaveService.Load(Slot);
            CampaignState back = lr.State;
            bool same = sr.Outcome == SaveOutcome.Success && back != null && Chips(back) == chips0 && Bps(back) == bps0;
            int ids = FirmwareKinds.Rows.Count(r => chips0.Contains(r.Id));
            var noProgram = FirmwareKinds.Rows.Where(r => EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { r.Id }, FirmwareCarrier.Projectile).IsEmpty).Select(r => r.Id).ToList();
            BlueprintVersionRecord v1 = back?.BlueprintRecords.First(x => x.BlueprintId == "bp_emg3fw01").Versions[0];
            BlueprintCircuitBoard reb = v1 == null ? null : BlueprintCircuitBoard.CreateDefault(v1.ChassisId, v1.PrimaryId, v1.UtilityId, v1.StructureId, v1.OrderedFirmwareIds);
            EmgProgramResult bpProg = reb == null ? null : EmgProgramCompiler.Compile(UplinkCompiler.CompileUplinked(reb, Array.Empty<string>()));
            Expect(same && ids == FirmwareKinds.Rows.Count && noProgram.Count == 0 && bpProg != null && !bpProg.IsEmpty && bpProg.LegacyOwners.Count == 0,
                $"旧存档的固件库存与蓝图仍有效：{ids} 种固件芯片（含混合 30 条）与带散热鳍 / 混合固件的蓝图存读档后逐字不变（{sr.Outcome} / {lr.Outcome}）；" +
                $"每条固件单独都能编出声明轨程序（编不出的 {noProgram.Count}{Detail(noProgram)}）；读回的蓝图编出 {bpProg?.Steps.Count ?? 0} 条指令、没有回落旧轨");
        }
    }
}
