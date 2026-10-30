using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameConfig;
using GameConfig.fg;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-DATA-01 通道端口注册表、声明表与规则审计的自动验收（FG19 FGT-EMG-001、FGT-EMG-003 端口表推导部分、FGT-EMG-004 阶段 1 部分；卡片“负向”）。
    /// 全部起真实系统：真实 Luban 表 → 真实 <see cref="EmgCatalog.Compile"/>（与正式路径同一函数）；负向矩阵把真实输入复制一份再改坏一处，断言被拒且不残留。
    /// A 源 = 表：fgdata.py --dump 的 EP / ED / EC / ES / ET 行与运行时五张表逐字段一致；
    /// B 真实表编译：0 个问题、数组行数 = 表行数、端口码在本族码段、事件码 = 300 + 事件码、物质码 = 400 + 物质码、动词码前 25 个 = CarrierReadingVerb、中英文本齐全；
    /// C FGT-EMG-003（端口表部分）：每一行的 dirs / perceive / carrierImpl / stage / numericOnly 与 FGR-EMG-010 推导规则一致，另按 FG19 原文抽查 10 个端口；
    /// D 负向矩阵（FGT-EMG-001 + 卡片负向）：码跨段 / 复用、推导列被手改、(owner, seq) 重复、引用未注册端口、非 owner 列写 fw_*、同一 owner stageGate 不一致（整条不进数组）、
    ///   stageGate 早于端口阶段、底盘特性非底盘列写 comp_*、写端口缺载体、表重载后不残留旧行、emg.stage 填高钳到已实现阶段；
    /// E FGT-EMG-004（源码部分，FGR-EMG-013 / 021）：编译器、CarrierReadings、Sim/Combat、Emergence 源码里没有内容 ID 判断；豁免清单只有两条装配反应（写死）；
    ///   存量违例只减不增（登记 FG-GAP-169，承接卡见证据）；扫描器自身的对照（字面量命中、注释不命中、前缀不命中）；
    /// F FGT-EMG-004（回归部分，阶段 1 口径）：铸造重炮 + 过载 = 熔穿过载、每发积热 65；切割束 + 过载 = 基础 + 15、不触发熔穿过载。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEmergenceRuleAuditSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgEmergenceRuleAuditSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgEmergenceRuleAuditSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;

        /// <summary>FGR-EMG-021：过渡豁免只有两条现存装配反应，写死不得新增（阶段 2 端口化后清零，EMG2 卡把它改成空数组）。</summary>
        public static readonly string[] Exemptions =
        {
            "BlueprintCircuitCompiler.cs|MechanicalReactionCatalog.DetectMarkJump|1",
            "BlueprintCircuitCompiler.cs|MechanicalReactionCatalog.DetectMeltOverload|1",
        };

        /// <summary>
        /// 存量违例（不是豁免）：审计发现的、早于 FG19 的按内容 ID 判断，登记 FG-GAP-169，只减不增。格式“文件|符号|出现次数”，按次数比对——
        /// 同一符号多写一处、出现清单外的新违例、或清单里的某条修掉了（次数变少）却没同步改清单，都判失败。承接：
        /// 编译器：静默标记器 → EMG1-FW-01（func_marker 声明切轨），铸造重炮开火方式 → EMG1-TRIG-01（根载荷实体化改按组件行数据），散热鳍 → EMG2-RULE-01（struct_fin 声明阶段 2）；
        /// 编译器调用的 BlueprintCircuitBoard 热量 / 带宽函数：重炮 + 过载配对积热（熔穿过载的热量侧）→ EMG2-REACT-01（装配反应端口化），
        /// 中继带宽 → EMG1-FW-01 写 struct_relay 声明、EMG3-SQD-01 中继转发切轨；CarrierReadings 固定底盘分支 → EMG3-CHS-01（锚定特性按 chs.* 状态端口生效）。
        /// </summary>
        public static readonly string[] LegacyBaseline =
        {
            "BlueprintCircuitCompiler.cs|ComponentCatalog.FuncMarkerId|1",
            "BlueprintCircuitCompiler.cs|ComponentCatalog.CompCannonId|1",
            "BlueprintCircuitCompiler.cs|ComponentCatalog.StructFinId|1",
            "BlueprintCircuitBoard.cs|ComponentCatalog.StructRelayId|1",
            "BlueprintCircuitBoard.cs|ComponentCatalog.CompCannonId|2",
            "BlueprintCircuitBoard.cs|FirmwareCatalog.FwOverloadId|2",
            "CarrierReadings.cs|\"chassis_fixed\"|1",
        };

        /// <summary>形似内容 ID、实为别的东西的字面量（BlueprintCircuitBoard 的 CircuitOpResult 错误码），不计入审计。只放经核实的条目。</summary>
        private static readonly string[] NotContentIds = { "\"chassis_required\"", "\"chassis_locked\"" };

        [MenuItem("BinGames/QA/自检/涌现通道表与规则审计")]
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
            Line("\n[涌现通道表与规则审计] 端口注册表 / 声明表 / 端口 × 载体 / 子形态 / 底盘特性、FGR-EMG-010 推导、负向矩阵、源码内容 ID 审计（EMG1-DATA-01）");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                EmgCatalog.ResetForTests();
                Step(CheckSourceEqualsTables);
                Step(CheckCompileRealTables);
                Step(CheckDerivation);
                Step(CheckNegativeMatrix);
                Step(CheckSourceAudit);
                Step(CheckRegression);
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            return _fail;
        }

        // ── A 源 = 表 ──
        private static void CheckSourceEqualsTables()
        {
            Line("  · A. 源数据 fgdata_emg.py / fgdata_reading.py（--dump 的 EP / ED / EC / ES / ET / EE）= 运行时六张表");
            (int code, string output) = FgProductionSelfCheck.RunPython(FgProductionSelfCheck.LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            string[] tags = { "EP\t", "ED\t", "EC\t", "ES\t", "ET\t", "EE\t" };
            List<string> src = output.Replace("\r", string.Empty).Split('\n').Where(l => tags.Any(t => l.StartsWith(t, StringComparison.Ordinal))).ToList();
            Tables t = ConfigSystem.Instance.Tables;
            var rt = new List<string>();
            rt.AddRange(t.TbChannelPort.DataList.Select(r => J("EP", r.Id, r.Family, r.Code, r.Dirs, r.NumericOnly, r.Perceive, r.CarrierImpl, r.Unit, r.NameKey, r.DescKey, r.Shape, r.Color, r.Stage)));
            rt.AddRange(t.TbChannelDecl.DataList.Select(r => J("ED", r.Id, r.Owner, r.OwnerKind, r.Seq, r.Dir, r.Port, r.Role, Py(r.Amount), Py(r.Param2), r.Carriers, Py(r.Heat), r.TextKey, r.StageGate)));
            rt.AddRange(t.TbPortCarrier.DataList.Select(r => J("EC", r.Id, r.Port, r.Carrier, r.Verb, Py(r.A), Py(r.B), Py(r.C), r.Mag, r.TextKey)));
            rt.AddRange(t.TbSubPayload.DataList.Select(r => J("ES", r.Id, r.Code, r.Carrier, Py(r.Speed), Py(r.Life), Py(r.LifeRatio), Py(r.Radius), Py(r.RadiusRatio), Py(r.Kinetic), r.KineticInherit, r.NameKey)));
            rt.AddRange(t.TbChassisTrait.DataList.Select(r => J("ET", r.Id, r.Chassis, r.Kind, r.AppliesTo, r.StatePorts, r.ParamKeys, r.Stage, r.Reserved, r.NameKey, r.DescKey)));
            // EMG1-ENT-01 实体档案（EE）同样走 --dump 逐字段比对；FgDataPipelineSelfCheck 因此排除 EE。
            rt.AddRange(t.TbEntityProfile.DataList.Select(r => J("EE", r.Id, r.Kind, r.Material, Py(r.Mass), Py(r.Conductive), Py(r.Flammable), Py(r.Magnetic),
                Py(r.ResistCold), Py(r.ResistHeat), Py(r.ResistElec), Py(r.ResistCorrode), Py(r.ResistMag), Py(r.ResistHack), Py(r.Brittle), r.Displace, Py(r.Kinetic),
                r.Count, Py(r.Damage), Py(r.Speed), Py(r.Range), Py(r.Threshold), r.NameKey)));
            List<string> onlySrc = src.Except(rt).ToList();
            List<string> onlyRt = rt.Except(src).ToList();
            Expect(code == 0 && src.Count > 0 && onlySrc.Count == 0 && onlyRt.Count == 0 && src.Count == rt.Count,
                $"源数据 {src.Count} 行 = 运行时表 {rt.Count} 行，逐字段一致（端口 {t.TbChannelPort.DataList.Count}、声明 {t.TbChannelDecl.DataList.Count}、端口 × 载体 {t.TbPortCarrier.DataList.Count}、子形态 {t.TbSubPayload.DataList.Count}、底盘特性 {t.TbChassisTrait.DataList.Count}、实体档案 {t.TbEntityProfile.DataList.Count}）"
                + (onlySrc.Count + onlyRt.Count > 0 ? "；不一致：" + string.Join(" | ", onlySrc.Concat(onlyRt).Take(4)) : string.Empty) + (code != 0 ? "；python 退出码 " + code : string.Empty));
        }

        // ── B 真实表编译 ──
        private static void CheckCompileRealTables()
        {
            Line("  · B. 真实表编译成内核整数数组");
            Tables t = ConfigSystem.Instance.Tables;
            EmgCompiled c = EmgCatalog.Current;
            Expect(c.Problems.Count == 0, $"真实表运行时校验 0 个问题（与 check_luban R37～R41 同一套判定）{(c.Problems.Count > 0 ? "：" + string.Join("；", c.Problems.Take(5)) : string.Empty)}");
            Expect(c.Ports.Length == t.TbChannelPort.DataList.Count && c.Decls.Length == t.TbChannelDecl.DataList.Count && c.PortCarriers.Length == t.TbPortCarrier.DataList.Count
                   && c.SubForms.Length == 5 && c.Traits.Length == 7,
                $"数组行数 = 表行数：端口 {c.Ports.Length}、声明 {c.Decls.Length}、端口 × 载体 {c.PortCarriers.Length}、子形态 {c.SubForms.Length}（= 5）、底盘特性 {c.Traits.Length}（= 7）");
            int segBad = c.Ports.Count(p => p.Code < EmgCatalog.SegmentBase(p.Family) || p.Code >= EmgCatalog.SegmentBase(p.Family) + 100);
            var evtBad = new List<string>();
            foreach (ChannelPort r in t.TbChannelPort.DataList)
            {
                if ((r.Family == "event" && (r.Code < 301 || r.Code > 329)) || (r.Family == "matter" && (r.Code < 401 || r.Code > 417)))
                {
                    evtBad.Add(r.Id);
                }
            }
            Expect(segBad == 0 && evtBad.Count == 0 && c.Ports.Select(p => p.Code).Distinct().Count() == c.Ports.Length
                   && Code(c, "evt.fire") == 301 && Code(c, "evt.ping") == 329 && Code(c, "mat.oil") == 401 && Code(c, "mat.sludge") == 417 && Code(c, "tmp.sync") == 1302,
                $"端口码全部在本族码段、不重复；事件 29 条 = 300 + 事件码（evt.fire 301 … evt.ping 329），物质 17 条 = 400 + 物质码（mat.oil 401 … mat.sludge 417）{(evtBad.Count > 0 ? "；不符：" + string.Join(",", evtBad) : string.Empty)}");
            Expect(t.TbChannelPort.DataList.Count(r => r.Family == "event") == 29 && t.TbChannelPort.DataList.Count(r => r.Family == "matter") == 17
                   && t.TbChannelPort.DataList.Select(r => r.Family).Distinct().Count() == 13,
                "13 个通道族都有端口；事件端口 29 条（FGR-EMG-060 封闭清单）、物质端口 17 条（2.5 节）");
            var verbBad = new List<string>();
            for (int i = 0; i < 25; i++)
            {
                if (!Enum.TryParse(EmgCatalog.Verbs[i], true, out CarrierReadingVerb v) || (int)v != i + 1)
                {
                    verbBad.Add(EmgCatalog.Verbs[i]);
                }
            }
            Expect(verbBad.Count == 0 && EmgCatalog.Verbs.Length == 45 && EmgCatalog.Verbs.Distinct().Count() == 45,
                $"动词码：前 25 个与 CarrierReadingVerb 同值（Dmg = 1 … Cone = 25），FG19A 第 3.1 节新动词 19 个接在 26 起，EMG1-GRM-01 末尾追加 inherit（45）{(verbBad.Count > 0 ? "；不符：" + string.Join(",", verbBad) : string.Empty)}");
            // 端口 × 载体：被声明写到的 carrierImpl 端口 5 载体齐全（FGR-EMG-015 规则一）——编译器已查，这里独立按数组复核
            var written = new HashSet<ushort>(c.Decls.Where(d => d.Dir == EmgDirs.Write).Select(d => d.Port));
            int incomplete = written.Count(code => c.Ports.First(p => p.Code == code).Flags is var f && (f & EmgPortRow.FlagCarrierImpl) != 0
                                                    && Enumerable.Range(0, 5).Any(car => !c.PortCarriers.Any(pc => pc.Port == code && pc.Carrier == car)));
            Expect(incomplete == 0 && written.Count > 0,
                $"种子声明写到的 {written.Count} 个端口里，carrierImpl 端口在 5 种载体上都有 fg.TbPortCarrier 行（零死对规则一）；其余 {c.RegisteredGaps.Count} 个 carrierImpl 端口无人写、无载体行，列为已登记缺口（DEBT-EMG1DATA01-01 → EMG1-FW-01 / 阶段 2～4 承接卡）");
            // 文本：中英齐全
            var missing = new List<string>();
            IEnumerable<string> keys = t.TbChannelPort.DataList.SelectMany(r => new[] { r.NameKey, r.DescKey })
                .Concat(t.TbChannelDecl.DataList.Select(r => r.TextKey))
                .Concat(t.TbPortCarrier.DataList.Where(r => r.TextKey != "none").Select(r => r.TextKey))
                .Concat(t.TbSubPayload.DataList.Select(r => r.NameKey))
                .Concat(t.TbChassisTrait.DataList.SelectMany(r => new[] { r.NameKey, r.DescKey }));
            int n = 0;
            foreach (string k in keys)
            {
                n++;
                LocText lt = t.TbLocText.GetOrDefault(k);
                if (lt == null || string.IsNullOrWhiteSpace(lt.Zh) || string.IsNullOrWhiteSpace(lt.En))
                {
                    missing.Add(k);
                }
            }
            Expect(missing.Count == 0 && n > 300, $"五张表引用的 {n} 个文本键中英齐全（端口名 / 说明、声明说明由端口 × 载体短语生成）{(missing.Count > 0 ? "；缺：" + string.Join(",", missing.Take(5)) : string.Empty)}");
            string desc = GameText.Get("emg.port.grm.mod.homing.desc");
            Expect(desc.Contains("飞行中转向") || desc.Contains("steers in flight"),
                $"端口说明由“端口 × 载体”短语生成（emg.port.grm.mod.homing.desc 含射弹短语）：{desc}");
        }

        // ── C 推导 ──
        private static void CheckDerivation()
        {
            Line("  · C. FGT-EMG-003（端口表部分）：四列 + numericOnly 与 FGR-EMG-010 推导规则逐行一致");
            var bad = new List<string>();
            foreach (ChannelPort r in ConfigSystem.Instance.Tables.TbChannelPort.DataList)
            {
                EmgFamily f = EmgCatalog.ParseFamily(r.Family);
                (EmgDirs dirs, bool perceive, bool impl, int stage, bool numeric) = EmgCatalog.DerivePort(r.Id, f);
                if (EmgCatalog.ParseDirs(r.Dirs) != dirs || (r.Perceive == 1) != perceive || (r.CarrierImpl == 1) != impl || r.Stage != stage || (r.NumericOnly == 1) != numeric)
                {
                    bad.Add(r.Id);
                }
            }
            Expect(bad.Count == 0, $"{ConfigSystem.Instance.Tables.TbChannelPort.DataList.Count} 个端口逐行与推导一致{(bad.Count > 0 ? "；不符：" + string.Join(",", bad) : string.Empty)}");
            // 按 FG19 原文抽查（防止两份推导实现一起写错）
            (string id, string dirs, int perceive, int impl, int stage, int numeric)[] spec =
            {
                ("grm.trig", "write", 0, 1, 1, 0), ("grm.mod.size", "write", 0, 1, 1, 1), ("grm.anchor", "write", 0, 1, 3, 0), ("meta.repeat", "write", 0, 0, 3, 0),
                ("evt.hit", "read", 1, 0, 1, 0), ("evt.marked", "both", 1, 0, 1, 0), ("mat.oil", "both", 1, 1, 2, 0), ("ter.obstacle", "both", 1, 0, 1, 0),
                ("chs.moving", "both", 1, 0, 1, 0), ("fld.deflect", "both", 1, 1, 1, 0), ("prop.mass", "both", 1, 1, 1, 0), ("hij.hack", "both", 1, 0, 4, 0),
            };
            var wrong = new List<string>();
            foreach ((string id, string dirs, int perceive, int impl, int stage, int numeric) in spec)
            {
                ChannelPort r = ConfigSystem.Instance.Tables.TbChannelPort.GetOrDefault(id);
                if (r == null || r.Dirs != dirs || r.Perceive != perceive || r.CarrierImpl != impl || r.Stage != stage || r.NumericOnly != numeric)
                {
                    wrong.Add(id);
                }
            }
            Expect(wrong.Count == 0, $"按 FG19 原文抽查 {spec.Length} 个端口（语法写、numericOnly 三个、grm.anchor 阶段 3、事件只读 / 可发出、阶段 1 例外 ter.obstacle / chs.moving / fld.deflect）{(wrong.Count > 0 ? "；不符：" + string.Join(",", wrong) : string.Empty)}");
        }

        // ── D 负向矩阵 ──
        private static void CheckNegativeMatrix()
        {
            Line("  · D. 负向矩阵（FGT-EMG-001 + 卡片负向）：改坏一处必须被拒、坏行不进数组");
            EmgSource real = EmgCatalog.FromTables();
            EmgCompiled clean = EmgCatalog.Compile(real);
            Expect(clean.Problems.Count == 0, "对照：真实输入编译 0 个问题");
            int realDecls = clean.Decls.Length;

            Neg("端口码跨段（grm.trig 码改成 250）", s => s.Ports.First(p => p.Id == "grm.trig").Code = 250,
                c => !c.PortIndexById.ContainsKey("grm.trig"), "跨段");
            Neg("端口码复用（grm.inherit 用 grm.trig 的 100）", s => s.Ports.First(p => p.Id == "grm.inherit").Code = 100,
                c => !c.PortIndexById.ContainsKey("grm.inherit") && c.PortIdByCode[100] == "grm.trig", "复用");
            Neg("推导列被手改（evt.hit 改成可写）", s => s.Ports.First(p => p.Id == "evt.hit").Dirs = "both",
                c => !c.PortIndexById.ContainsKey("evt.hit"), "推导");
            Neg("(owner, seq) 重复", s => s.Decls.Add(CopyDecl(s.Decls.First(d => d.Owner == "fw_split" && d.Seq == 1), "fw_split", 1)),
                c => !c.OwnerIndex.ContainsKey("fw_split"), "重复");
            Neg("声明引用未注册端口", s => s.Decls.First(d => d.Owner == "fw_pierce" && d.Seq == 2).Port = "grm.mod.warp",
                c => !c.OwnerIndex.ContainsKey("fw_pierce"), "未注册端口");
            Neg("声明非 owner 列写了 fw_*", s => s.Decls.First(d => d.Owner == "fw_homing" && d.Seq == 1).TextKey = "fw_split",
                c => !c.OwnerIndex.ContainsKey("fw_homing"), "内容 ID");
            Neg("同一 owner stageGate 不一致（整条切轨）", s => s.Decls.First(d => d.Owner == "fw_flare" && d.Seq == 1).StageGate = 2,
                c => !c.OwnerIndex.ContainsKey("fw_flare") && c.Decls.Length == realDecls - real.Decls.Count(d => d.Owner == "fw_flare"), "不一致");
            Neg("stageGate 早于端口可用阶段（阶段 1 读 fld.pull）", s => s.Decls.First(d => d.Owner == "fw_jitter" && d.Seq == 1).Port = "fld.pull",
                c => !c.OwnerIndex.ContainsKey("fw_jitter"), "早于端口");
            Neg("owner 外键断（固件 fw_split 整条改名为不存在的 fw_nosuch）", s =>
                {
                    foreach (EmgSource.Decl d in s.Decls.Where(d => d.Owner == "fw_split"))
                    {
                        d.Owner = "fw_nosuch";
                        d.Id = "fw_nosuch." + d.Seq;
                    }
                },
                c => !c.OwnerIndex.ContainsKey("fw_nosuch") && c.Decls.Length == realDecls - real.Decls.Count(d => d.Owner == "fw_split"), "外键断");
            Neg("owner 外键断（部件拼错 comp_canon）", s => s.Decls.Add(CopyDecl(s.Decls.First(d => d.Owner == "func_spikes" && d.Seq == 1), "comp_canon", 1)),
                c => !c.OwnerIndex.ContainsKey("comp_canon"), "外键断");
            Neg("状态端口 hij.hijacked 读作 sense（只能作 condition）", s =>
                {
                    EmgSource.Decl d = s.Decls.First(x => x.Owner == "fw_jitter" && x.Seq == 1);
                    d.Port = "hij.hijacked";
                    d.Role = "sense";
                },
                c => !c.OwnerIndex.ContainsKey("fw_jitter"), "角色");
            Neg("trigger 读非事件端口", s => s.Decls.First(d => d.Owner == "fw_pierce" && d.Seq == 1).Role = "trigger",
                c => !c.OwnerIndex.ContainsKey("fw_pierce"), "角色");
            Neg("底盘特性非底盘列写了 comp_*", s => s.Traits.First(tr => tr.Id == "trait.track.drag").AppliesTo = "comp_gun",
                c => !c.TraitByChassis.ContainsKey("chassis_track"), "内容 ID");
            Neg("写端口缺一种载体（grm.trig 去掉布区行）", s => s.PortCarriers.RemoveAll(r => r.Port == "grm.trig" && r.Carrier == "field"),
                c => true, "零死对");
            Neg("子形态寿命填负数", s => s.Subs.First(r => r.Carrier == "projectile").Life = -1f, c => c.SubForms.Length == 4, "子形态");
            Neg("底盘特性状态端口超过 2 个", s => s.Traits.First(tr => tr.Id == "trait.track.drag").StatePorts = "chs.moving;chs.moving;chs.moving",
                c => !c.TraitByChassis.ContainsKey("chassis_track"), "超过 2 个");
            // 结构件没有表：fgdata_emg.STRUCTURE_IDS（check_luban R38 查 structure 类 owner 用）必须与 ComponentCatalog.Struct*Id 一致
            string emgPy = File.ReadAllText(Path.Combine(FgProductionSelfCheck.LocateRepo(), "tools", "cell_tables", "fgdata_emg.py"));
            Match sm = Regex.Match(emgPy, @"^STRUCTURE_IDS = \(([^)]*)\)", RegexOptions.Multiline);
            var pyStruct = new HashSet<string>(Regex.Matches(sm.Groups[1].Value, "\"([a-z0-9_]+)\"").Cast<Match>().Select(m => m.Groups[1].Value));
            var csStruct = new HashSet<string>(typeof(ComponentCatalog).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.IsLiteral && f.Name.StartsWith("Struct", StringComparison.Ordinal) && f.Name.EndsWith("Id", StringComparison.Ordinal))
                .Select(f => (string)f.GetRawConstantValue()));
            Expect(sm.Success && pyStruct.Count > 0 && pyStruct.SetEquals(csStruct),
                $"结构件 owner 外键清单：fgdata_emg.STRUCTURE_IDS（{string.Join(",", pyStruct)}）= ComponentCatalog.Struct*Id（{string.Join(",", csStruct)}）");
            Neg("子形态缺一种载体", s => s.Subs.RemoveAll(r => r.Carrier == "aura"), c => c.SubForms.Length == 4, "子形态缺载体");

            // 表重载后内核数组重登记、不残留旧行
            EmgSource withExtra = Clone(real);
            foreach (EmgSource.Decl d in real.Decls.Where(d => d.Owner == "fw_split"))
            {
                withExtra.Decls.Add(CopyDecl(d, "fw_reloadprobe", d.Seq));
            }
            withExtra.OwnerExists = (o, k) => o == "fw_reloadprobe" || real.OwnerExists(o, k); // 只测重载：探针 owner 视为存在
            int rev0 = EmgCatalog.Revision;
            EmgCatalog.OverrideForTests(withExtra);
            bool had = EmgCatalog.Current.OwnerIndex.ContainsKey("fw_reloadprobe") && EmgCatalog.Current.Decls.Length == realDecls + 2;
            EmgCatalog.OverrideForTests(real);
            EmgCompiled after = EmgCatalog.Current;
            Expect(had && !after.OwnerIndex.ContainsKey("fw_reloadprobe") && after.Decls.Length == realDecls && !after.OwnerIds.Contains("fw_reloadprobe")
                   && EmgCatalog.Revision == rev0 + 2,
                $"表重载：多一条 owner 的表编出 {realDecls + 2} 行，换回原表后整份重建为 {after.Decls.Length} 行，旧 owner 不残留（版本号 +2）");

            // emg.stage 钳到已实现最高阶段
            EmgSource high = Clone(real);
            high.Tuning = id => id == EmgCatalog.StageKey ? (true, 3f) : GridContent.TryGetTuning(id, out float v) ? (true, v) : (false, 0f);
            EmgCatalog.OverrideForTests(high);
            EmgCompiled hc = EmgCatalog.Current;
            bool track = EmgCatalog.IsOwnerOnDeclTrack("fw_split");
            EmgCatalog.OverrideForTests(null);
            // EMG1-KRN-01 起已实现阶段 = 1：填 3 钳到 1，stageGate 1 的 fw_split 走声明轨（只在调参被改高时；正式表仍是 0，见下一条）。
            Expect(hc.RawStage == 3f && hc.EffectiveStage == EmgCatalog.ImplementedStage && EmgCatalog.ImplementedStage == 1 && track
                   && EmgCatalog.ClampStage(4f, 2) == 2 && EmgCatalog.ClampStage(1f, 2) == 1 && EmgCatalog.ClampStage(-1f, 2) == 0,
                $"emg.stage 填 3（高于已实现阶段 {EmgCatalog.ImplementedStage}）→ 生效阶段钳到 {hc.EffectiveStage}，stageGate 1 的 fw_split 随之走声明轨；钳位函数：4→2、1→1、−1→0（已实现 2 时）");
            // EMG1-E2E-01（DEBT-EMG1KRN01-02）：阶段 1 出口起正式表缺省 1——stageGate ≤ 1 的 owner 走声明轨，≥ 2 的仍走旧轨。
            Expect(EmgCatalog.EffectiveStage == 1 && real.Decls.GroupBy(d => d.Owner).All(g => EmgCatalog.IsOwnerOnDeclTrack(g.Key) == (g.First().StageGate <= 1)),
                "正式表 emg.stage = 1（阶段 1 出口起缺省）：stageGate ≤ 1 的声明 owner 走声明轨，stageGate ≥ 2 的仍走旧 readFields");
        }

        private static void Neg(string title, Action<EmgSource> mutate, Func<EmgCompiled, bool> notLeaked, string reasonPart)
        {
            EmgSource s = Clone(EmgCatalog.FromTables());
            mutate(s);
            EmgCompiled c = EmgCatalog.Compile(s);
            bool rejected = c.Problems.Any(p => p.Contains(reasonPart));
            Expect(rejected && notLeaked(c), $"{title}：被拒（{(c.Problems.Count > 0 ? c.Problems[0] : "无问题")}），坏行不进内核数组");
        }

        private static EmgSource Clone(EmgSource src)
        {
            var s = new EmgSource { Tuning = src.Tuning, ChassisExists = src.ChassisExists, OwnerExists = src.OwnerExists };
            s.Ports.AddRange(src.Ports.Select(p => (EmgSource.Port)Copy(p)));
            s.Decls.AddRange(src.Decls.Select(d => (EmgSource.Decl)Copy(d)));
            s.PortCarriers.AddRange(src.PortCarriers.Select(p => (EmgSource.PortCarrier)Copy(p)));
            s.Subs.AddRange(src.Subs.Select(p => (EmgSource.Sub)Copy(p)));
            s.Traits.AddRange(src.Traits.Select(p => (EmgSource.Trait)Copy(p)));
            return s;
        }

        private static object Copy(object o)
        {
            object c = Activator.CreateInstance(o.GetType());
            foreach (var f in o.GetType().GetFields())
            {
                f.SetValue(c, f.GetValue(o));
            }
            return c;
        }

        internal static EmgSource.Decl CopyDecl(EmgSource.Decl d, string owner, int seq)
        {
            var c = (EmgSource.Decl)Copy(d);
            c.Owner = owner;
            c.Seq = seq;
            c.Id = owner + "." + seq;
            c.TextKey = "emg.decl." + owner + "." + seq;
            return c;
        }

        // ── E 源码审计 ──
        private static readonly Regex Literal = new Regex("\"(fw|comp|func|struct|chassis)_[a-z0-9][a-z0-9_]*\"", RegexOptions.CultureInvariant);
        private static readonly Regex CatalogConst = new Regex(@"\b(ComponentCatalog|FirmwareCatalog|ChassisCatalog)\.(Comp|Func|Struct|Fw|Chassis)[A-Za-z0-9]*Id\b", RegexOptions.CultureInvariant);
        private static readonly Regex PairDetector = new Regex(@"\bMechanicalReactionCatalog\.Detect[A-Za-z]+\b", RegexOptions.CultureInvariant);

        /// <summary>扫一段 C# 源码：去掉注释后，找内容 ID 字面量（fw_ / comp_ / func_ / struct_ / chassis_）、组件 / 固件 / 底盘 ID 常量、ID 配对检测函数。
        /// 返回每一处命中（同一符号出现几次就有几条，不去重；不含文件名）。</summary>
        public static List<string> ScanSource(string text)
        {
            string code = StripComments(text);
            var hits = new List<string>();
            foreach (Regex r in new[] { Literal, CatalogConst, PairDetector })
            {
                foreach (Match m in r.Matches(code))
                {
                    if (Array.IndexOf(NotContentIds, m.Value) < 0)
                    {
                        hits.Add(m.Value);
                    }
                }
            }
            return hits;
        }

        private static string StripComments(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool inStr = false, verbatim = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (inStr)
                {
                    sb.Append(ch);
                    if (!verbatim && ch == '\\' && i + 1 < s.Length)
                    {
                        sb.Append(s[++i]);
                    }
                    else if (ch == '"')
                    {
                        if (verbatim && i + 1 < s.Length && s[i + 1] == '"')
                        {
                            sb.Append(s[++i]);
                        }
                        else
                        {
                            inStr = false;
                        }
                    }
                    continue;
                }
                if (ch == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n')
                    {
                        i++;
                    }
                    sb.Append('\n');
                    continue;
                }
                if (ch == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 1;
                    continue;
                }
                if (ch == '"')
                {
                    inStr = true;
                    verbatim = i > 0 && (s[i - 1] == '@' || (s[i - 1] == '$' && i > 1 && s[i - 2] == '@'));
                }
                else if (ch == '\'' && i + 2 < s.Length)
                {
                    // 字符字面量（含 '"'）原样跳过
                    int end = s.IndexOf('\'', i + (s[i + 1] == '\\' ? 3 : 2));
                    if (end > i && end - i <= 8)
                    {
                        sb.Append(s, i, end - i + 1);
                        i = end;
                        continue;
                    }
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        public static IEnumerable<string> AuditFiles()
        {
            string scripts = Path.Combine(Application.dataPath, "GameScripts");
            foreach (string f in Directory.GetFiles(Path.Combine(scripts, "Main", "Sim", "Combat"), "*.cs", SearchOption.AllDirectories))
            {
                yield return f;
            }
            string gl = Path.Combine(scripts, "HotFix", "GameLogic", "Campaign");
            yield return Path.Combine(gl, "Blueprint", "BlueprintCircuitCompiler.cs");
            // 编译器 Compile 调用的热量 / 带宽函数（ComputeHeatBudget / ComputeFirmwareHeat / 中继带宽）在这个文件里，整文件纳入
            yield return Path.Combine(gl, "Blueprint", "BlueprintCircuitBoard.cs");
            yield return Path.Combine(gl, "Content", "CarrierReadings.cs");
            foreach (string f in Directory.GetFiles(Path.Combine(gl, "Emergence"), "*.cs", SearchOption.AllDirectories))
            {
                yield return f;
            }
        }

        private static void CheckSourceAudit()
        {
            Line("  · E. FGT-EMG-004（源码部分，FGR-EMG-013 / 021）：编译器、CarrierReadings、Sim/Combat、Emergence 不按内容 ID 判断");
            // 扫描器自身的对照
            Expect(ScanSource("if (id == \"fw_split\") { }").SequenceEqual(new[] { "\"fw_split\"" })
                   && ScanSource("// if (id == \"fw_split\")\n/* \"comp_gun\" */ var p = \"fw_\";").Count == 0
                   && ScanSource("bool b = x == ComponentCatalog.CompGunId;").SequenceEqual(new[] { "ComponentCatalog.CompGunId" })
                   && ScanSource("var s = \"a // \\\"fw_x\\\"\";").Count == 0
                   && ScanSource("MechanicalReactionCatalog.DetectFoo(a)").Count == 1
                   && ScanSource("a == ComponentCatalog.CompGunId || b == ComponentCatalog.CompGunId").Count == 2
                   && ScanSource("if (c == \"chassis_track\" || c == ChassisCatalog.ChassisHoverId) { }").Count == 2
                   && ScanSource("return Fail(\"chassis_locked\");").Count == 0,
                "扫描器对照：字面量 \"fw_split\" 命中、注释里的不命中、只有前缀 \"fw_\" 不命中、组件常量命中、ID 配对检测函数命中、同一常量写两处计 2 次、底盘字面量与常量命中、已核实的错误码不计");
            Expect(Exemptions.Length == 2 && Exemptions.All(e => e.Contains("DetectMarkJump") || e.Contains("DetectMeltOverload")),
                "豁免清单写死为两条装配反应（标记跳转、熔穿过载，FGR-EMG-021），不得新增");
            var raw = new List<string>();
            int files = 0;
            foreach (string f in AuditFiles())
            {
                files++;
                foreach (string hit in ScanSource(File.ReadAllText(f)))
                {
                    raw.Add(Path.GetFileName(f) + "|" + hit);
                }
            }
            // 按“文件|符号|次数”比对：多一处、少一处都和清单对不上
            List<string> findings = raw.GroupBy(h => h).Select(g => g.Key + "|" + g.Count()).ToList();
            List<string> unexpected = findings.Except(Exemptions).Except(LegacyBaseline).ToList();
            List<string> fixedButListed = LegacyBaseline.Concat(Exemptions).Except(findings).ToList();
            Expect(files >= 16 && unexpected.Count == 0,
                $"扫描 {files} 个源文件（Sim/Combat 全部、BlueprintCircuitCompiler、BlueprintCircuitBoard、CarrierReadings、Emergence）：除 2 条豁免与 {LegacyBaseline.Length} 条已登记存量（FG-GAP-169，按出现次数）外没有内容 ID 判断"
                + (unexpected.Count > 0 ? "；新违例或次数变多：" + string.Join("、", unexpected) : string.Empty));
            Expect(fixedButListed.Count == 0,
                "存量与豁免只减不增：清单里每一条都仍在源码里、次数一致（修掉一处就必须同步改清单，清单不会悄悄变成新的豁免）"
                + (fixedButListed.Count > 0 ? "；与源码次数不符：" + string.Join("、", fixedButListed) : string.Empty));
            // 表的非 owner 列：编译器逐列查过；这里独立统计扫过的格子
            Tables t = ConfigSystem.Instance.Tables;
            int cells = 0, contentIds = 0;
            foreach (ChannelDecl r in t.TbChannelDecl.DataList)
            {
                foreach (string v in new[] { r.OwnerKind, r.Dir, r.Port, r.Role, r.Carriers, r.TextKey })
                {
                    cells++;
                    contentIds += EmgCatalog.IsContentId(v) ? 1 : 0;
                }
            }
            foreach (ChassisTrait r in t.TbChassisTrait.DataList)
            {
                foreach (string v in new[] { r.Id, r.Kind, r.AppliesTo, r.StatePorts, r.ParamKeys, r.NameKey, r.DescKey })
                {
                    cells++;
                    contentIds += EmgCatalog.IsContentId(v) ? 1 : 0;
                }
            }
            foreach (PortCarrier r in t.TbPortCarrier.DataList)
            {
                foreach (string v in new[] { r.Id, r.Port, r.Carrier, r.Verb, r.Mag, r.TextKey })
                {
                    cells++;
                    contentIds += EmgCatalog.IsContentId(v) ? 1 : 0;
                }
            }
            Expect(contentIds == 0 && cells > 500, $"声明表、底盘特性、端口 × 载体的非 owner 列共 {cells} 格，内容 ID 0 个");
        }

        // ── F 回归 ──
        private static void CheckRegression()
        {
            Line("  · F. FGT-EMG-004（回归，阶段 1 口径；与 FGJ-M1 同）：熔穿过载与过载积热不变");
            FirmwareKinds.ResetForTests();
            string cannon = ComponentCatalog.CompCannonId, beam = ComponentCatalog.CompBeamId, overload = FirmwareCatalog.FwOverloadId;
            float cannonHeat = BlueprintCircuitBoard.ComputeHeatBudget(cannon, new[] { overload });
            float beamBase = BlueprintCircuitBoard.ComputeHeatBudget(beam, Array.Empty<string>());
            float beamOver = BlueprintCircuitBoard.ComputeHeatBudget(beam, new[] { overload });
            string cannonRx = BlueprintCircuitCompiler.DetectReactionId(cannon, null, new[] { overload });
            string beamRx = BlueprintCircuitCompiler.DetectReactionId(beam, null, new[] { overload });
            Expect(Mathf.Approximately(cannonHeat, 65f) && cannonRx == MechanicalReactionCatalog.ReactionMeltOverloadId,
                $"铸造重炮 + 过载：熔穿过载（{cannonRx}），每发积热 {cannonHeat:0.#}（= 65；内核首发积热 65 另由 FgCombatKernelSelfCheck / FgCoreFirmwareBoundarySelfCheck 真开火断言）");
            Expect(Mathf.Approximately(beamOver - beamBase, 15f) && beamRx == null,
                $"切割束 + 过载：每发积热 = 基础 {beamBase:0.#} + 15 = {beamOver:0.#}，不触发熔穿过载（光束动能 0）");
        }

        // ── 工具 ──
        private static int Code(EmgCompiled c, string id) => c.PortIndexById.TryGetValue(id, out int i) ? c.Ports[i].Code : -1;

        private static string J(params object[] v) => string.Join("\t", v.Select(x => x?.ToString() ?? string.Empty));

        private static string Py(float v)
        {
            string r = v.ToString("R", CultureInfo.InvariantCulture);
            return r.Contains(".") || r.Contains("E") ? r : r + ".0";
        }

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

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
