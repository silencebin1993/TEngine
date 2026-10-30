using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Settings;
using GameLogic.Localization;
using GameLogic.Progression;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using static GameLogic.EditorTools.FgReadingMatrixSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-FW-02（FG19A 第 6 节 N09～N16“事件补洞新固件落表”；FGT-EMG-002 新增行准入、FGT-EMG-003 stageGate ≤ 1 的 6 条、FGT-EMG-006 新固件可获得）。
    /// 起真实系统：真实 Luban 表（fg.TbFirmwareKind / TbChannelDecl / TbPortCarrier / TbLootPick / TbLocText）、真实声明编译与准入（EmgCatalog）、
    /// 真实蓝图 → 程序编译 → 内核编队攻击（FgReadingMatrixSelfCheck 条件场景）、真实掉落 / 三选一 / 数据复原入口、真实存档往返。
    /// A 数据：8 条追加在表尾、名表 44 条不动；ID = FG19A 暂定 ID；stageGate 按 FG19A；写端口 5 载体行齐；准入通过；文本键（名称 / 描述 / 5 载体读法 / 获取途径）与占位图标齐；
    /// B 行为（阶段 1 条件型 5 条，FG19A 第 1.8 节先造条件）：5 载体 × 程序首位 / 末位都有该固件自己的行为证据（放子载荷 / 本机处驻留区），且首位 ≠ 末位；emg.stage = 0 时不生效；
    ///   命中触发的水箱外排由 FgReadingMatrixSelfCheck C2 常规矩阵覆盖（本段只验驻留区落在命中点、浸湿位）；
    /// C 获取（FGT-EMG-006）：emg.stage = 0 时 8 条都不进掉落表 / 三选一候选池 / 数据复原；= 1 时 stageGate ≤ 1 的 6 条进、N14 / N15 不进；名表固件不受影响；
    /// D 负向：存读档往返不凭空多出新固件（仓库没有改动前的存档样本；读档路径不按固件表批量发放）；外部血量单位“瘫痪”（血量 0 但未判死）不触发自毁装药，真正判死只放一次；AI 驾驶（没有接入）不发 evt.uplink；
    ///   存读档续跑一致（过热记号、驻留区）；0.5x / 1x / 3x 步长下事件与驻留区数一致。
    /// 已并入 CellFrameworkValidate.RunAll（段名 FgEmgAddonFirmwareSelfCheck）；单段：bash tools/unity-validate.sh --segment FgEmgAddonFirmwareSelfCheck。
    /// </summary>
    public static class FgEmgAddonFirmwareSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private const int Slot = 7;
        private static string _dir;

        /// <summary>FG19A 第 6 节 N09～N16 的暂定 ID 与阶段列（定稿 = 暂定，FG19 第 2.8 节）。</summary>
        private static readonly (string Id, int Gate)[] Spec =
        {
            ("fw_scuttle", 1), ("fw_bouncefuse", 1), ("fw_piercefuse", 1), ("fw_ventarc", 1), ("fw_coolback", 1), ("fw_enterfuse", 2), ("fw_uplinkpulse", 2), ("fw_water", 1),
        };

        [MenuItem("BinGames/QA/自检/事件补洞新固件（EMG1-FW-02）")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            Line("\n[事件补洞新固件] FG19A 第 6 节 N09～N16 落表（EMG1-FW-02；FGT-EMG-002 / 003 / 006）");
            _dir = Path.Combine(Path.GetTempPath(), "bingames-emg1fw02");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FirmwareKinds.ResetForTests();
                FirmwareCatalog.Invalidate();
                CarrierReadings.Reload();
                EmgCatalog.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Directory.CreateDirectory(_dir);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Step(CheckData);
                Step(CheckBehavior);
                Step(CheckSources);
                Step(CheckNegatives);
            }
            finally
            {
                EmgCatalog.ResetForTests();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
            }
            return _fail;
        }

        private static void UseStage(int stage, Action<EmgSource> edit = null)
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, stage) : tuning(id);
            edit?.Invoke(src);
            EmgCatalog.OverrideForTests(src);
        }

        // ── A. 数据 ─────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 8 条追加在表尾、名表 44 条不动；stageGate / 端口 × 载体 / 准入 / 文本键 / 图标齐全");
            var rows = FirmwareKinds.Rows.Where(r => r != null && !FirmwareKinds.IsMixedRow(r)).Select(r => r.Id).ToList();
            var addon = FirmwareKinds.AddonRows.Select(r => r.Id).ToList();
            Expect(addon.SequenceEqual(Spec.Select(s => s.Id)) && rows.Skip(44).SequenceEqual(addon) && FirmwareKinds.BaseRows.Count == 44
                   && FirmwareCatalog.BaseCount == FirmwareCatalog.ExpectedCount && addon.All(FirmwareCatalog.IsAddon) && addon.All(id => FirmwareCatalog.All.ContainsKey(id)),
                $"新固件 {addon.Count} 条按 FG19A 顺序追加在正式固件表尾（第 45～52 行）：{string.Join("、", addon)}；名表 44 条 ID 与顺序不变；8 条都进固件目录（可装配 / 图鉴）");
            EmgSource src = EmgCatalog.FromTables();
            var ports = src.Ports.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);
            var bad = new List<string>();
            foreach ((string id, int gate) in Spec)
            {
                var decls = src.Decls.Where(d => d.Owner == id).ToList();
                if (decls.Count == 0 || decls.Any(d => d.StageGate != gate))
                {
                    bad.Add($"{id} stageGate {string.Join("/", decls.Select(d => d.StageGate).Distinct())}≠{gate}");
                }
                if (!EmgCatalog.Admit(EmgCatalog.DeclsOf(src, id), ports, out string why))
                {
                    bad.Add($"{id} 准入被拒：{why}");
                }
                foreach (EmgSource.Decl w in decls.Where(d => d.Dir == "write"))
                {
                    int carriers = src.PortCarriers.Where(pc => pc.Port == w.Port).Select(pc => pc.Carrier).Distinct().Count();
                    if (carriers != 5)
                    {
                        bad.Add($"{id} 写端 {w.Port} 只有 {carriers} 种载体行");
                    }
                }
                FirmwareKinds.TryGetRow(id, out GameConfig.fg.FirmwareKind row);
                string[] keys = { row.NameKey, row.DescKey, row.AcquireKey, row.ReadProjectileKey, row.ReadMeleeKey, row.ReadSummonKey, row.ReadAuraKey, row.ReadFieldKey };
                foreach (string k in keys)
                {
                    string zh = GameText.Get(k, GameLanguage.ZhCn), en = GameText.Get(k, GameLanguage.En);
                    if (string.IsNullOrEmpty(zh) || zh == k || string.IsNullOrEmpty(en) || en == k)
                    {
                        bad.Add($"{id} 文本键 {k} 缺中 / 英");
                    }
                }
                string trig = decls.First(d => d.Dir == "read" && d.Role == "trigger").Port;
                string trigName = GameText.Get("emg.port." + trig + ".name", GameLanguage.ZhCn);
                if (!GameText.Get(row.ReadProjectileKey, GameLanguage.ZhCn).Contains(trigName))
                {
                    bad.Add($"{id} 读法说明不是由声明生成（应含触发事件“{trigName}”）");
                }
                string icon = Path.Combine(Application.dataPath, "GameRes", "Raw", "UI", "Icons", row.Icon + ".png");
                if (row.Icon != "icon_" + id || !File.Exists(icon) || !File.Exists(icon + ".meta"))
                {
                    bad.Add($"{id} 占位图标 {row.Icon} 缺失");
                }
                if (row.Protocol != "enemy" || row.Source != "salvage" || row.ReadFields != "none" || row.LegacyId != "none")
                {
                    bad.Add($"{id} 协议 / 来源 / 读法字段不是“敌方加密 · 杂兵残骸 · 只走声明”");
                }
            }
            Expect(bad.Count == 0,
                $"8 条 stageGate 与 FG19A 阶段列一致（N09～N13、N16 = 1，N14 / N15 = 2）、全部准入、写端口 grm.trig / en.elec / mat.coolant / mat.water / fld.push 各 5 载体行、" +
                $"名称 / 描述 / 获取途径 / 5 载体读法中英齐（读法由“事件 + 端口 × 载体”生成）、占位图标与 .meta 在 GameRes/Raw/UI/Icons（B22 占位）{Detail(bad)}");
            Line("    · 例（射弹）：" + string.Join("；", Spec.Take(4).Select(s => FirmwareKinds.Reading(s.Id, FirmwareCarrier.Projectile))));
            // 详情页的读 / 写端口行由声明生成（EmgProgramTreeText.PortsLine，与 44 条同一函数）
            string detail = CarrierReadings.DetailLines("fw_coolback") ?? string.Empty;
            Expect(detail.Contains(GameText.Get("emg.port.evt.cooled.name")) && detail.Contains(GameText.Get("emg.port.mat.coolant.name")),
                $"固件详情页（固件库 / 信号核心 HUD，CarrierReadings.DetailLines）列出读 / 写端口：{detail.Replace('\n', '｜')}");
            // 三选一卡面（LootPickPanelUIToolkit 的比较行 = LootPickService.CompareText）同样列出由声明生成的端口
            CampaignState cs = CampaignState.CreateNew("emg1fw02-a", "Standard", 1);
            var card = new LootCandidateRecord { Kind = LootPickCatalog.KindFirmware, ItemId = AnalysisCatalog.EncryptedFirmwareId, TargetId = "fw_coolback", Amount = 1, Tier = "common" };
            string cmp = LootPickService.CompareText(cs, card) ?? string.Empty;
            Expect(cmp.Contains(GameText.Get("emg.port.evt.cooled.name")) && cmp.Contains(GameText.Get("emg.port.mat.coolant.name")),
                $"三选一卡面比较行列出读 / 写端口（与详情页同一函数 EmgProgramTreeText.PortsLine）：{cmp.Replace('\n', '｜')}");
        }

        // ── B. 行为 ─────────────────────────────────────────────────────────

        private static void CheckBehavior()
        {
            Line("  · B. 阶段 1 条件型 5 条 × 5 载体 × 首位 / 末位：先造条件，各自的行为证据成立、首位 / 末位都与对照不同；emg.stage = 0 不生效");
            var cases = new (string Fw, AddonCond Cond, string Prefix, string What)[]
            {
                ("fw_scuttle", AddonCond.Death, null, "阵亡处放子载荷"),
                ("fw_bouncefuse", AddonCond.Bounce, "fw_ricochet", "反弹放子载荷"),
                ("fw_piercefuse", AddonCond.Pierce, "fw_pierce", "穿透放子载荷"),
                ("fw_ventarc", AddonCond.Heat, null, "过热时本机处电击区"),
                ("fw_coolback", AddonCond.Heat, null, "冷却完成时本机处冷却液区"),
            };
            try
            {
                UseStage(1);
                var dead = new List<string>();
                var same = new List<string>();
                var noProducer = new List<string>();
                var notApplicable = new List<string>();
                var lines = new List<string>();
                foreach ((string fw, AddonCond cond, string prefix, string what) in cases)
                {
                    foreach ((FirmwareCarrier carrier, string comp) in AddonCarriers)
                    {
                        if (NotApplicable.Contains($"{carrier}/{fw}"))
                        {
                            // FG-GAP-177：声明 carriers 不含本载体（FGR-EMG-060 发出者列不含）→ 整条不编译、预览给原因、读法写明“本载体不适用”
                            EmgProgramResult na = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { fw }, carrier);
                            string reading = FirmwareKinds.Reading(fw, carrier) ?? string.Empty;
                            if (na.Notes.Any(n => n.Key == EmgProgramCompiler.NoteCarrierNa) && na.Steps.All(st => st.Decls.Count == 0)
                                && GameText.Has(EmgProgramCompiler.NoteCarrierNa) && reading != FirmwareKinds.Reading(fw, FirmwareCarrier.Projectile)
                                && (reading.Contains("不适用") || reading.Contains("Not applicable")))
                            {
                                notApplicable.Add($"{carrier}/{fw}");
                            }
                            continue;
                        }
                        float secs = cond == AddonCond.Heat ? 14f : 4f;
                        AddonObs ctl, first, last;
                        if (prefix == null)
                        {
                            ctl = RunAddonCondition(comp, new[] { AddonFiller }, cond, secs);
                            first = RunAddonCondition(comp, new[] { fw, AddonFiller }, cond, secs);
                            last = RunAddonCondition(comp, new[] { AddonFiller, fw }, cond, secs);
                        }
                        else
                        {
                            // 接入口配额 2 + 本地固件位 2，程序序 = 接入口在前、本地在后：条件提供者占接入口第 1 位，被测固件在填充之前 / 之后
                            ctl = RunAddonCondition(comp, new[] { prefix, AddonFiller }, cond, secs);
                            first = RunAddonCondition(comp, new[] { prefix, fw }, cond, secs, local: new[] { AddonFiller });
                            last = RunAddonCondition(comp, new[] { prefix, AddonFiller }, cond, secs, local: new[] { fw });
                        }
                        bool producer = cond switch
                        {
                            AddonCond.Bounce => first.Bounces > 0,
                            AddonCond.Pierce => first.Pierces > 0,
                            AddonCond.Death => first.Deaths > 0,
                            _ => fw == "fw_ventarc" ? first.Overheats > 0 : first.Cooleds > 0,
                        };
                        if (!producer)
                        {
                            noProducer.Add($"{carrier}/{fw}");
                            continue;
                        }
                        bool okF = Evidence(fw, first, ctl), okL = Evidence(fw, last, ctl);
                        if (!okF)
                        {
                            dead.Add($"{carrier}/{fw}（首位）");
                        }
                        if (!okL)
                        {
                            dead.Add($"{carrier}/{fw}（末位）");
                        }
                        if (first.Digest == ctl.Digest || last.Digest == ctl.Digest)
                        {
                            same.Add($"{carrier}/{fw}");
                        }
                        lines.Add($"{carrier}/{fw}：对照 子载荷 {ctl.SubSpawned} 区 {ctl.Zones.Count} · 首位 {first.SubSpawned}/{first.Zones.Count}（阵亡处 {first.SubsAtDeath}）· 末位 {last.SubSpawned}/{last.Zones.Count}" +
                                  $"（首位事件 反弹 {first.Bounces} 穿透 {first.Pierces} 过热 {first.Overheats} 冷却完成 {first.Cooleds}；末位 反弹 {last.Bounces} 穿透 {last.Pierces}；对照 反弹 {ctl.Bounces} 穿透 {ctl.Pierces}）");
                    }
                }
                foreach (string l in lines)
                {
                    Line("      " + l);
                }
                Expect(dead.Count == 0 && lines.Count >= 15,
                    $"有事件生产者的 {lines.Count} 组载体 × 固件，首位 / 末位都有该固件自己的行为（放子载荷数多于对照 / 本机处出现对应状态位驻留区）{Detail(dead)}");
                Expect(same.Count == 0, $"同一条新固件放在程序首位 / 末位，行为指纹都与只有填充指令的对照不同（卡片负向，FGT-EMG-003 口径）{Detail(same)}");
                Expect(noProducer.Count == 0 && notApplicable.Count == NotApplicable.Length,
                    $"声明适用的载体上条件事件都有内核生产者（含格斗挥击穿透发 evt.pierce）{Detail(noProducer)}；声明 carriers 不含的 {notApplicable.Count}/{NotApplicable.Length} 组" +
                    $"（{string.Join("、", notApplicable)}）整条不编译、预览写“本载体不适用”、读法如实写明（FG-GAP-177 关闭）");
                // 水箱外排（命中触发、根节点排放）：驻留区落在命中点、挂浸湿位（零死对在 C2 常规矩阵）
                UseStage(1);
                AddonObs water = RunAddonCondition("comp_gun", new[] { "fw_water" }, AddonCond.Pierce, 3f);
                AddonObs dry = RunAddonCondition("comp_gun", Array.Empty<string>(), AddonCond.Pierce, 3f);
                bool wet = water.Zones.Any(z => (z.Mask & (1u << 2)) != 0 && math.distance(z.Pos, new double2(3.0, 0.3)) < 1.0);
                Expect(wet && !dry.Zones.Any(z => (z.Mask & (1u << 2)) != 0),
                    $"水箱外排：命中主目标处落浸湿位驻留区（{water.Zones.Count} 块）；不装时没有（{dry.Zones.Count} 块）");
                // FGT-EMG-014 阶段 1（EMG1-E2E-01 行为复核）：[腐蚀弹头, 酸液] 编译成两行（命中排酸 / 终结排酸，FgEmgProgramSelfCheck C）；运行时腐蚀弹头在阶段 1 走过渡读法
                // （FG19A 第 9 行“1（过渡）→ 2”：命中再爆一圈 + 腐蚀，不排酸），命中排酸随阶段 2 斑块层落地（DEBT-EMG1E2E01-02）。阶段 1 断言：终结排酸照常、不被同装的腐蚀弹头吞掉。
                AddonObs both = RunAddonCondition("comp_gun", new[] { "fw_corrode", "fw_acid" }, AddonCond.Pierce, 3f);
                AddonObs corrode = RunAddonCondition("comp_gun", new[] { "fw_corrode" }, AddonCond.Pierce, 3f);
                AddonObs acid = RunAddonCondition("comp_gun", new[] { "fw_acid" }, AddonCond.Pierce, 3f);
                Expect(acid.Zones.Count > dry.Zones.Count && both.Zones.Count >= acid.Zones.Count && corrode.Zones.Count == dry.Zones.Count,
                    $"[腐蚀弹头, 酸液]（阶段 1）：终结排酸照常生效、同装不被吞（只装酸液 {acid.Zones.Count} 块、同装 {both.Zones.Count} 块）；腐蚀弹头走过渡读法不排酸（只装 {corrode.Zones.Count} 块 = 不装 {dry.Zones.Count} 块，命中排酸随阶段 2）");
                // emg.stage = 0：声明不生效、程序为空、没有旧读法 → 与不装逐字相同（也拿不到，见 C 段）
                UseStage(0);
                AddonObs off = RunAddonCondition("comp_gun", new[] { "fw_coolback" }, AddonCond.Heat, 14f);
                AddonObs offCtl = RunAddonCondition("comp_gun", Array.Empty<string>(), AddonCond.Heat, 14f);
                EmgProgramResult pr = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_coolback" }, FirmwareCarrier.Projectile);
                Expect(pr.IsEmpty && pr.LegacyOwners.Contains("fw_coolback") && off.Zones.Count == offCtl.Zones.Count && off.Splits == 0,
                    $"emg.stage = 0：冷却回流不进程序（走旧轨且没有旧读法）、不落区（{off.Zones.Count} = 不装 {offCtl.Zones.Count}）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        /// <summary>FG-GAP-177：按 FGR-EMG-060 发出者列，反弹不在格斗 / 力场 / 布区发、穿透不在力场 / 布区发——声明 carriers 收窄（fgdata_emg.DECL_CARRIERS）。</summary>
        private static readonly string[] NotApplicable =
        {
            "Melee/fw_bouncefuse", "Aura/fw_bouncefuse", "Field/fw_bouncefuse",
            "Aura/fw_piercefuse", "Field/fw_piercefuse",
        };

        private static bool Evidence(string fw, AddonObs o, AddonObs ctl)
        {
            switch (fw)
            {
                case "fw_scuttle":
                    return o.SubSpawned > ctl.SubSpawned && o.SubsAtDeath > 0;
                case "fw_ventarc":
                    return ZonesNear(o, 4) > ZonesNear(ctl, 4);
                case "fw_coolback":
                    return ZonesNear(o, 2) > ZonesNear(ctl, 2);
                default:
                    return o.SubSpawned > ctl.SubSpawned;
            }
        }

        /// <summary>本机（事件发生时的位置）1 米内、挂状态位 <paramref name="bit"/> 的驻留区块数。</summary>
        private static int ZonesNear(AddonObs o, int bit) =>
            o.Zones.Count(z => (z.Mask & (1u << bit)) != 0 && z.AtAttacker);

        // ── C. 获取 ─────────────────────────────────────────────────────────

        private static void CheckSources()
        {
            Line("  · C. FGT-EMG-006：来源只在 stageGate ≤ emg.stage 时开放（杂兵残骸掉落 / 精英三选一候选池 / 数据复原），名表固件不受影响");
            CampaignState s = CampaignState.CreateNew("emg1fw02-c", "Standard", 1);
            var pool = new List<LootPickDef>();
            try
            {
                foreach (int stage in new[] { 0, 1 })
                {
                    UseStage(stage);
                    var open = new List<string>();
                    var leaked = new List<string>();
                    foreach ((string id, int gate) in Spec)
                    {
                        string fac = FirmwareKinds.FactionOf(id);
                        LootPickCatalog.PoolFor(fac, pool);
                        bool inDrop = LootCatalog.EnemyFirmwareOf(fac).Contains(id);
                        bool inPick = pool.Any(p => p.Target == id);
                        bool inRestore = FirmwareRestoreService.Candidates(s).Contains(id);
                        bool obtainable = FirmwareKinds.IsObtainable(id) && FirmwareKinds.HasMachineImplementation(id);
                        bool want = gate <= stage;
                        if (inDrop && inPick && inRestore && obtainable)
                        {
                            open.Add(id);
                        }
                        if (inDrop != want || inPick != want || inRestore != want || obtainable != want)
                        {
                            leaked.Add($"{id}（掉落 {inDrop} 三选一 {inPick} 复原 {inRestore} 可装 {obtainable}，应 {want}）");
                        }
                    }
                    int baseSalvage = FirmwareKinds.BaseRows.Count(r => r.Protocol == "enemy" && r.Source == "salvage");
                    int dropBase = new[] { "silent", "foundry", "clarity", "overclock" }.Sum(f => LootCatalog.EnemyFirmwareOf(f).Count(x => !FirmwareCatalog.IsAddon(x)));
                    Expect(leaked.Count == 0 && open.Count == Spec.Count(x => x.Gate <= stage) && dropBase == baseSalvage,
                        $"emg.stage = {stage}：开放 {open.Count} 条（{string.Join("、", open)}）；名表杂兵残骸固件仍全部可掉（{dropBase}/{baseSalvage}）{Detail(leaked)}");
                    // 图鉴：拿不到的新固件不提示获取途径、不计入固件页签总数
                    MechanicCodexEntry closed = MechanicCodex.Entries.FirstOrDefault(e => e.Kind == MechanicCodexKind.Firmware && e.ContentId == "fw_enterfuse");
                    MechanicCodexEntry scuttle = MechanicCodex.Entries.FirstOrDefault(e => e.Kind == MechanicCodexKind.Firmware && e.ContentId == "fw_scuttle");
                    int fwTotal = MechanicCodex.CountIn(MechanicCodex.TabFirmware);
                    int fwWant = MechanicCodex.Entries.Count(e => e.Tab == MechanicCodex.TabFirmware && (e.Kind != MechanicCodexKind.Firmware || FirmwareKinds.IsObtainable(e.ContentId)));
                    string notOpen = GameText.Get("codex.firmware.not_open");
                    Expect(closed != null && scuttle != null && MechanicCodex.Hint(closed, s) == notOpen && (MechanicCodex.Hint(scuttle, s) == notOpen) == (stage < 1)
                           && fwTotal == fwWant && fwTotal == MechanicCodex.Entries.Count(e => e.Tab == MechanicCodex.TabFirmware) - Spec.Count(x => x.Gate > stage),
                        $"emg.stage = {stage}：图鉴里入场引信提示“{MechanicCodex.Hint(closed, s)}”、自毁装药提示“{MechanicCodex.Hint(scuttle, s)}”；固件页签总数 {fwTotal}（拿不到的 {Spec.Count(x => x.Gate > stage)} 条不计）");
                }
                // 三选一候选行在表里排在全部既有行之后（既有行 ID / 顺序不动）
                var rows = LootPickCatalog.Rows.ToList();
                int firstAddon = rows.FindIndex(r => FirmwareCatalog.IsAddon(r.Target));
                Expect(firstAddon > 0 && rows.Skip(firstAddon).All(r => FirmwareCatalog.IsAddon(r.Target)) && rows.Where(r => FirmwareCatalog.IsAddon(r.Target)).All(r => r.SortOrder >= 1000),
                    $"fg.TbLootPick：新固件 {rows.Count - firstAddon} 行追加在既有 {firstAddon} 行之后（sortOrder ≥ 1000，权重 = 同类基础固件，临时值）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
        }

        // ── D. 负向、存读档、倍速 ──────────────────────────────────────────────

        private static void CheckNegatives()
        {
            Line("  · D. 存读档往返不凭空进库存；瘫痪不触发自毁装药、判死只放一次；AI 驾驶无 evt.uplink；存读档续跑一致；0.5x / 1x / 3x 一致");
            try
            {
                UseStage(1);
                // 存读档往返（卡片“旧存档读入”：仓库没有改动前的存档样本，读档升级路径不按固件表批量发放 / 解锁，SyncCodex 只解锁已拿到的）：新建战役（全部已解锁内容 + 芯片）存档 → 读档，新固件不在库存 / 已解锁里
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                CampaignState s = CampaignState.CreateNew("emg1fw02-d", "Standard", 2);
                CampaignSession.Set(Slot, s);
                PrimitiveInventory.EnsureSeeded(s);
                SignalCoreService.EnsureInitialized(s);
                CampaignFgStateDomains.EnsureAll(s);
                int chips = s.PrimitiveChips.Length;
                SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
                LoadResult loaded = CampaignSaveService.Load(Slot);
                string[] addon = Spec.Select(x => x.Id).ToArray();
                bool clean = loaded.Success && loaded.State.PrimitiveChips.Length == chips
                             && !loaded.State.PrimitiveChips.Any(c => addon.Contains(c.CardDefId)) && !addon.Any(id => MechanicalContentUnlock.IsUnlocked(loaded.State, id));
                Expect(saved.Success && clean, $"存档 → 读档（emg.stage = 1）：芯片 {chips} 枚原样、8 条新固件没有凭空进库存或解锁");

                // 瘫痪（外部血量单位血量同步为 0 但未判死）不触发；真正判死（SetHealth 判死 + 随后 Kill）只放一次
                (long splitsPara, long splitsDead, long deaths) = ScuttleExternal();
                Expect(splitsPara == 0 && splitsDead > 0 && deaths == 1,
                    $"evt.death 成立条件 = 阵亡统一入口（FG19 FGR-EMG-113）：血量 0 未判死（瘫痪类）放子载荷 {splitsPara} 次；判死后放 {splitsDead} 次、evt.death {deaths} 次（随后的 Kill 不重复）");

                // AI 驾驶：接入冲击阶段 2 才生效（推斥场随 EMG2-FLD-01）。测试夹具把它的写端代入阶段 1 已有内核实现的“排水”、stageGate 提前到 1，
                // 只验触发条件：没有信号接入（AI 驾驶）时内核不发 evt.uplink、不结算；热更层接入注入后才发并在本机处结算一次
                UseStage(1, src =>
                {
                    foreach (EmgSource.Decl d in src.Decls.Where(d => d.Owner == "fw_uplinkpulse"))
                    {
                        d.StageGate = 1;
                        if (d.Dir == "write")
                        {
                            (d.Port, d.Role, d.Amount, d.Param2) = ("mat.water", "emit", 1f, 0f);
                        }
                    }
                });
                (long ai, int aiZones, EmgInjectResult inj, long linked, int linkedZones) = UplinkProbe();
                Expect(ai == 0 && aiZones == 0 && inj == EmgInjectResult.Enqueued && linked == 1 && linkedZones == 1,
                    $"装了接入冲击的机器在 AI 驾驶下 4 秒 evt.uplink {ai} 次、结算 {aiZones} 次；信号接入注入后 {inj}、evt.uplink {linked} 次、本机处结算 {linkedZones} 次（推斥场本身随 EMG2-FLD-01，DEBT-EMG1FW02-02）");

                UseStage(1);
                string sl = AddonSaveLoad("comp_gun", new[] { "fw_coolback", AddonFiller }, AddonCond.Heat, 1.0f, 13f, out long zones);
                Expect(sl == null && zones > 0, $"冷却回流：过热后 1 秒存档（“过热过”记号在单位段）→ 新内核读档，续跑 13 秒逐步状态哈希 / 涌现哈希 / 区域数一致，冷却完成照常落区（{zones}）{(sl != null ? "：" + sl : string.Empty)}");

                var speed = new List<string>();
                foreach (float dt in new[] { 1f / 120f, 1f / 60f, 1f / 20f })
                {
                    AddonObs o = RunAddonCondition("comp_gun", new[] { "fw_coolback", AddonFiller }, AddonCond.Heat, 14f, dt);
                    speed.Add($"{o.Cooleds}/{ZonesNear(o, 2)}");
                }
                Expect(speed.Distinct().Count() == 1 && speed[0] != "0/0",
                    $"同样 14 游戏秒，步长 1/120、1/60、1/20（= 0.5x / 1x / 3x）冷却完成 / 本机冷却液区：{string.Join(" · ", speed)}（暂停 = 不步进，不产生事件）");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                CampaignSession.Clear();
            }
        }

        private static (long para, long dead, long deaths) ScuttleExternal()
        {
            Campaign.Blueprint.BlueprintCircuitBoard board = Campaign.Blueprint.BlueprintCircuitBoard.CreateDefault(Campaign.Regions.HomeValleyLayout.Erc003ChassisId, "comp_gun", null, null, Array.Empty<string>());
            board.TrySetUplink(2);
            Campaign.Blueprint.BlueprintCircuitPreview p = Campaign.Blueprint.UplinkCompiler.CompileUplinked(board, new[] { "fw_scuttle" });
            CombatWeapon w = Campaign.Combat.CombatSite.MachineWeaponFrom(p);
            EmgProgramResult program = EmgProgramCompiler.Compile(p);
            CombatConfig cfg = Campaign.Combat.CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using var k = new CombatKernel(cfg, 16);
            w.Program = Campaign.Combat.CombatSite.RegisterProgram(k, program);
            int wi = k.AddWeapon(w);
            int me = k.Spawn(new CombatSpawn
            {
                Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled | CombatUnitFlags.ExternalHealth,
                Position = double2.zero, Home = double2.zero, Radius = 0.6f, Speed = 4f, Health = 300f, MaxHealth = 300f, Weapon = wi, BehaviorProfile = -1,
            });
            k.Spawn(new CombatSpawn
            {
                Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.HoldFire, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                Position = new double2(3, 0), Home = new double2(3, 0), Radius = 0.5f, Health = 5000f, MaxHealth = 5000f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
            });
            void Run(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    k.Step(1f / 60f, k.Time + 1f / 60f);
                }
            }
            Run(30);
            k.SetHealth(me, 0f, 300f, true); // 热更层同步血量为 0 但没有判死（瘫痪类状态）：不是阵亡
            Run(30);
            long para = k.EmgBusStats.SubSpawned;
            k.SetHealth(me, 0f, 300f, false); // 热更层判死（SyncMachineHealth）→ 阵亡统一入口
            k.Kill(me, 0); // 随后 CombatSite.OnMachineDied → Kernel.Kill(unit, 0)：Alive 已翻转，不重复处理
            Run(60);
            return (para, k.EmgBusStats.SubSpawned - para, k.EmgEnqueuedOf(EmgEventKind.Death));
        }

        private static (long ai, int aiZones, EmgInjectResult inj, long linked, int linkedZones) UplinkProbe()
        {
            Campaign.Blueprint.BlueprintCircuitBoard board = Campaign.Blueprint.BlueprintCircuitBoard.CreateDefault(Campaign.Regions.HomeValleyLayout.Erc003ChassisId, "comp_gun", null, null, new[] { "fw_uplinkpulse" });
            EmgProgramResult program = EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_uplinkpulse" }, FirmwareCarrier.Projectile);
            CombatConfig cfg = Campaign.Combat.CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using var k = new CombatKernel(cfg, 16);
            CombatWeapon w = Campaign.Combat.CombatSite.MachineWeaponFrom(Campaign.Blueprint.UplinkCompiler.CompileUplinked(board, Array.Empty<string>()));
            w.Program = Campaign.Combat.CombatSite.RegisterProgram(k, program);
            int wi = k.AddWeapon(w);
            int me = k.Spawn(new CombatSpawn
            {
                Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled,
                Position = double2.zero, Home = double2.zero, Radius = 0.6f, Speed = 4f, Health = 300f, MaxHealth = 300f, Weapon = wi, BehaviorProfile = -1,
            });
            for (int i = 0; i < 240; i++)
            {
                k.Step(1f / 60f, k.Time + 1f / 60f);
            }
            long ai = k.EmgEnqueuedOf(EmgEventKind.Uplink);
            int aiZones = (int)k.Counters.ZonesSpawned;
            EmgInjectResult r = k.InjectEmgUnitEvent(EmgEventKind.Uplink, me, 0);
            k.Step(1f / 60f, k.Time + 1f / 60f);
            return (ai, aiZones, r, k.EmgEnqueuedOf(EmgEventKind.Uplink), (int)k.Counters.ZonesSpawned - aiZones);
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        private static string Detail(List<string> bad) => bad.Count == 0 ? string.Empty : "：" + string.Join("；", bad.Take(8));

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                Line("    ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("    ✗ " + message);
        }

        private static void Line(string s) => _report.AppendLine(s);
    }
}
