using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-PRG-01 程序编译器的自动验收（FG19 FGR-EMG-012、016、017、030～033、039、044、045；FGT-EMG-010、014（阶段 1）、015、017（阶段 1）；ADR-EMG-001 ②⑩、T3、T9）。
    /// 起真实编译链（BlueprintCircuitCompiler / UplinkCompiler → EmgProgramCompiler → CombatKernel.AddProgram）断言行为：
    /// A 12 组以上固定程序的树形、容量、继承、溢出、代际已满（含第 3.2 节三例、单位级提升与提升溢出、混合展开）；
    /// B 内核三张表的行（节点、指令行、监听掩码、需要飞行、核心、动词、序号）与登记去重；C 同端口幂等；D 修饰读端随继承；E 热量分项；
    /// F 程序顺序 = 接入固件在前（反例：拿 FirmwareIds 编出来不一样）、AI / 不在路径上 / 超配额；G 签名；H 负向；I 旧内容版本提示；
    /// J 正式流程（家园地点里机器按装配编程序、接入中途换装配重编、断开回本地、真实存读档后程序下标写回、emg.stage = 0 逐字不变）；K 编译耗时。
    /// 声明表里还没落表的固件（漏油、酸液、集群协议、FG19A N12 过热泄放）按 FG19A 的声明写成测试行注入，emg.stage 注入 1；其余全部是真实表。
    /// </summary>
    public static class FgEmgProgramSelfCheck
    {
        private const string Homing = "fw_homing", Split = "fw_split", Pierce = "fw_pierce", Delay = "fw_delayfuse", Scatter = "fw_scatter";
        private const string Ricochet = "fw_ricochet", Wallslam = "fw_wallslam", Corrode = "fw_corrode", LockCache = "fw_lockcache";
        private const string Oil = "fw_oilleak", Acid = "fw_acid", Swarm = "fw_swarm", Vent = "fw_ventarc", EtchShell = "fw_mix_etchshell";
        private const string ArcChain = "fw_arcchain", MarkTag = "fw_marktag";
        /// <summary>只给“同键取幅度大者”用的测试固件：读热信号、写寻的 0.3（与寻的 0.85 同键）。</summary>
        private const string WeakHoming = "fw_selftest_weakhoming";
        private const string Bp = "bp_emgprg_gun";
        private const int Slot = 0;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("BinGames/QA/自检/涌现程序编译器")]
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
            _pass = 0;
            Line("\n[涌现程序] EMG1-PRG-01：程序编译器——槽位顺序、程序树、容量与继承（FGT-EMG-010 / 014 / 015 / 017）");
            Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；编译器在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行，耗时只作量级参考）");
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            GameLanguage originalLanguage = GameSettings.Language;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-emgprg-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FirmwareKinds.ResetForTests();
                CarrierReadings.Reload();
                FirmwareCatalog.Invalidate();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                UseStage1Catalog();
                Step(CheckFixedPrograms);
                Step(CheckKernelRows);
                Step(CheckIdempotence);
                Step(CheckModifierReads);
                Step(CheckHeat);
                Step(CheckOrderAndUplink);
                Step(CheckSignature);
                Step(CheckNegatives);
                Step(CheckStaleContent);
                Step(CheckSitePipeline);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"程序编译器自检抛异常：{e}");
            }
            finally
            {
                EmgCatalog.ResetForTests();
                WorldSimulation.UnloadAll();
                MachineLoadoutRegistry.Clear();
                GameClock.ResetSession();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
            Line($"  · [涌现程序] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 测试目录：真实五张表 + FG19A 已定、还没落表的 4 条固件声明；emg.stage = 1 ──

        /// <summary>EMG1-E2E-01（DEBT-EMG1KRN01-02）：正式表缺省改 1 之后，阶段 0（回滚对照）显式注入。</summary>
        internal static void UseStage0Catalog()
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, 0f) : tuning(id);
            EmgCatalog.OverrideForTests(src);
        }

        internal static void UseStage1Catalog()
        {
            EmgSource src = EmgCatalog.FromTables();
            Func<string, (bool ok, float value)> tuning = src.Tuning;
            src.Tuning = id => id == EmgCatalog.StageKey ? (true, 1f) : tuning(id);
            Func<string, string, bool> owners = src.OwnerExists;
            src.OwnerExists = (o, k) => o == Vent || o == WeakHoming || owners(o, k); // N12 过热泄放随 EMG1-FW-02 落表，这里用测试行代替（FGT-EMG-010 原文）
            AddDecls(src, Oil, ("read", "evt.expire", "trigger", 1f, 0f, 0f), ("write", "mat.oil", "emit", 1f, 0f, 0f));
            AddDecls(src, Acid, ("read", "evt.expire", "trigger", 1f, 0f, 0f), ("write", "mat.acid", "emit", 1f, 0f, 0f));
            AddDecls(src, Vent, ("read", "evt.overheat", "trigger", 1f, 0f, 0f), ("write", "en.elec", "inject", 1f, 4f, 0f));
            AddDecls(src, Swarm, ("read", "evt.fire", "trigger", 1f, 0f, 0f), ("read", "prop.charge", "sense", 1f, 0f, 0f),
                ("write", "ent.drone", "spawn", 1f, 0f, 0f), ("write", "grm.inherit", "modify", 1f, 0f, 0f));
            AddDecls(src, WeakHoming, ("read", "prop.heat", "sense", 1f, 0f, 0f), ("write", "grm.mod.homing", "modify", 0.3f, 0f, 0f));
            // FG19A 第 40 行电弧、第 20 行标记跳转（随 EMG1-FW-01 落表）：同写 grm.mod.chain，偏好分别来自 prop.conductive / evt.marked 读端
            AddDecls(src, ArcChain, ("read", "prop.conductive", "sense", 1f, 0f, 0f), ("write", "grm.mod.chain", "modify", 2f, 0f, 0f), ("write", "en.elec", "inject", 0.3f, 0f, 0f));
            AddDecls(src, MarkTag, ("read", "evt.marked", "condition", 1f, 0f, 0f), ("write", "grm.mod.chain", "modify", 2f, 0f, 0f));
            // 测试声明写到的 carrierImpl 端口若还没有“端口 × 载体”行（随 EMG1-FW-01 落表），补阶段 1 过渡动词行（物质 → 驻留区，FG19A 第 0.3 节口径 3），只为目录零问题
            foreach (string port in new[] { "mat.oil", "mat.acid", "en.elec", "ent.drone", "grm.inherit", "grm.mod.chain" })
            {
                foreach (string carrier in EmgCatalog.Carriers)
                {
                    if (!src.PortCarriers.Any(r => r.Port == port && r.Carrier == carrier))
                    {
                        src.PortCarriers.Add(new EmgSource.PortCarrier { Id = $"{port}.{carrier}.selftest", Port = port, Carrier = carrier, Verb = "zone", Mag = "a", TextKey = "emg.pc.selftest" });
                    }
                }
            }
            EmgCatalog.OverrideForTests(src);
            EmgCompiled c = EmgCatalog.Current;
            string[] need = { Homing, Split, Pierce, Delay, Scatter, Ricochet, Wallslam, Corrode, LockCache, Oil, Acid, Swarm, Vent, WeakHoming, ArcChain, MarkTag, "fw_fork" };
            string[] missing = need.Where(o => !c.OwnerIndex.ContainsKey(o)).ToArray();
            Expect(c.Problems.Count == 0 && c.EffectiveStage == 1 && missing.Length == 0,
                $"测试目录：真实表 + 6 条测试声明编译无问题（{c.Problems.Count} 条{(c.Problems.Count > 0 ? "：" + c.Problems[0] : string.Empty)}），生效阶段 {c.EffectiveStage}；用到的 {need.Length} 个 owner 都在声明轨{(missing.Length > 0 ? "，缺 " + string.Join("、", missing) : string.Empty)}");
        }

        private static void AddDecls(EmgSource src, string owner, params (string dir, string port, string role, float amount, float p2, float heat)[] decls)
        {
            src.Decls.RemoveAll(d => d.Owner == owner);
            for (int i = 0; i < decls.Length; i++)
            {
                src.Decls.Add(new EmgSource.Decl
                {
                    Id = owner + "." + (i + 1), Owner = owner, OwnerKind = "firmware", Seq = i + 1, Dir = decls[i].dir, Port = decls[i].port, Role = decls[i].role,
                    Amount = decls[i].amount, Param2 = decls[i].p2, Heat = decls[i].heat, Carriers = "all", TextKey = $"emg.decl.{owner}.{i + 1}", StageGate = 1,
                });
            }
        }

        private static EmgProgramResult P(string[] uplink, string[] local, int cap = 0, FirmwareCarrier carrier = FirmwareCarrier.Projectile) =>
            EmgProgramCompiler.Compile(uplink ?? Array.Empty<string>(), local ?? Array.Empty<string>(), carrier, cap);

        private static string[] A(params string[] ids) => ids;

        private static string Short(string id) => id != null && id.StartsWith("fw_", StringComparison.Ordinal) ? id.Substring(3) : id;

        /// <summary>树的规范文本：节点 “序号&lt;父 g代 占用/容量 事件×份数: 自有 ^继承”，节点之间用 “ | ”。</summary>
        internal static string Tree(EmgProgramResult r) => string.Join(" | ", r.Nodes.Select(n =>
            $"{n.Index}{(n.Parent >= 0 ? "<" + n.Parent : string.Empty)} g{n.Gen} {n.Used}/{n.Capacity}{(n.Parent >= 0 ? $" {n.TrigEvent.ToString().ToLowerInvariant()}×{n.TrigCount}" : string.Empty)}:" +
            string.Concat(n.Entries.Select(e => " " + (e.Inherited ? "^" : string.Empty) + Short(r.Steps[e.Ordinal].OwnerId)))));

        internal static string Notes(EmgProgramResult r) => string.Join(", ", r.Notes.Select(n =>
            $"{n.Key.Substring("emg.compile.".Length)}:{Short(r.Steps[n.Ordinal].OwnerId)}{(n.Node >= 0 ? "@" + n.Node : string.Empty)}"));

        // ── A. 固定程序（FGT-EMG-010）────────────────────────────────────────────────

        private static void CheckFixedPrograms()
        {
            Line("  · A. 固定程序（FGT-EMG-010）：树形、容量占用、继承、溢出、代际已满逐项比对期望（第 3.2 节三例、单位级提升与提升溢出、混合展开、grm.inherit）");
            (string Name, EmgProgramResult R, string Tree, string Notes)[] cases = FixedCases();
            foreach ((string name, EmgProgramResult r, string tree, string notes) in cases)
            {
                string t = Tree(r), n = Notes(r);
                Expect(t == tree && n == notes, $"{name}：{t}{(n.Length > 0 ? "；原因 " + n : string.Empty)}" + (t == tree && n == notes ? string.Empty : $"（期望 {tree}；{notes}）"));
            }
            CheckFixedProgramsTail(cases);
        }

        /// <summary>FGT-EMG-010 的固定程序与期望树（EMG1-UX-01 的 FGT-EMG-018 用同一组程序比对预览）。调用前须 <see cref="UseStage1Catalog"/>。</summary>
        internal static (string Name, EmgProgramResult R, string Tree, string Notes)[] FixedCases()
        {
            return new (string Name, EmgProgramResult R, string Tree, string Notes)[]
            {
                ("例 1 [寻的, 分裂, 分裂]（接入 1 + 机器 2）", P(A(Homing), A(Split, Split)),
                    "0 g0 2/4: homing split | 1<0 g1 2/2 hit×3: split ^homing | 2<1 g2 1/1 hit×3: ^homing", ""),
                ("例 2 [分裂, 漏油]（AI 机器 2 槽）", P(null, A(Split, Oil)),
                    "0 g0 1/4: split | 1<0 g1 1/2 hit×3: oilleak", ""),
                ("例 3 [延时引信, 贯穿, 漏油, 寻的]", P(A(Delay, Pierce), A(Oil, Homing)),
                    "0 g0 2/4: delayfuse homing | 1<0 g1 2/2 timer×3: pierce oilleak", ""),
                ("单位级提升 [分裂, 寻的, 贯穿, 过热泄放]", P(A(Split, Homing), A(Pierce, Vent)),
                    "0 g0 2/4: split ventarc | 1<0 g1 2/2 hit×3: homing pierce", ""),
                ("提升溢出（C0 = 2）[过热泄放 ×3, 分裂]", P(A(Vent, Vent), A(Vent, Split), cap: 2),
                    "0 g0 2/2: ventarc ventarc", "overflow:ventarc, overflow:split, duplicate:ventarc@0"),
                ("代际已满 [分裂, 分裂, 分裂]", P(A(Split), A(Split, Split)),
                    "0 g0 1/4: split | 1<0 g1 1/2 hit×3: split | 2<1 g2 1/1 hit×3: split", "gen_full:split@2"),
                ("混合展开溢出 [溶蚀弹头, 溶蚀弹头, 寻的, 贯穿]（1 槽 2 条）", P(A(EtchShell, EtchShell), A(Homing, Pierce)),
                    "0 g0 4/4: corrode oilleak corrode oilleak", "overflow:homing, overflow:pierce, duplicate:corrode@0, duplicate:corrode@0, duplicate:oilleak@0"),
                ("未刷写 [寻的, 贯穿, 分裂, 漏油]", P(A(Homing, Pierce), A(Split, Oil)),
                    "0 g0 3/4: homing pierce split | 1<0 g1 2/2 hit×3: oilleak ^homing", "not_flashed:pierce@1"),
                ("修饰随继承 [霰射, 跳弹, 分裂]", P(A(Scatter, Ricochet), A(Split)),
                    "0 g0 3/4: scatter ricochet split | 1<0 g1 2/2 hit×3: ^scatter ^ricochet", ""),
                ("近炸 [分叉, 寻的]", P(null, A("fw_fork", Homing)),
                    "0 g0 1/4: fork | 1<0 g1 1/2 prox×2: homing", ""),
                ("扩大继承 [集群协议, 腐蚀弹头, 分裂]", P(A(Swarm), A(Corrode, Split)),
                    "0 g0 3/4: swarm corrode split | 1<0 g1 2/2 hit×3: ^swarm ^corrode", ""),
                ("扩大继承：继承来的触发器再放子节点 [集群协议, 延时引信, 溶蚀弹头, 分裂]", P(A(Swarm, Delay), A(EtchShell, Split)),
                    "0 g0 3/4: swarm delayfuse split | 1<0 g1 2/2 timer×3: corrode oilleak | 2<0 g1 2/2 hit×3: ^swarm ^delayfuse | 3<2 g2 1/1 timer×3: ^swarm",
                    "not_flashed:swarm@1"),
                ("只有触发器没有下游 [分裂]", P(null, A(Split)), "0 g0 1/4: split | 1<0 g1 0/2 hit×3:", ""),
                ("对照：不扩大继承 [腐蚀弹头, 分裂]", P(null, A(Corrode, Split)), "0 g0 2/4: corrode split | 1<0 g1 0/2 hit×3:", ""),
                ("提升的指令不进子弹药 [过热泄放, 集群协议, 分裂]（扩大继承也不带）", P(A(Vent, Swarm), A(Split)),
                    "0 g0 3/4: ventarc swarm split | 1<0 g1 1/2 hit×3: ^swarm", ""),
            };
        }

        private static void CheckFixedProgramsTail((string Name, EmgProgramResult R, string Tree, string Notes)[] cases)
        {
            EmgProgramResult ex3 = cases[2].R;
            Expect(ex3.Nodes[1].TrigParam == 0.6f && cases[9].R.Nodes[1].TrigParam == 4f && cases[0].R.Nodes[1].TrigCount == 3 && cases[9].R.Nodes[1].TrigCount == 2,
                $"触发参数取声明：延时引信定时 {ex3.Nodes[1].TrigParam} 秒、分叉近炸半径 {cases[9].R.Nodes[1].TrigParam} 米，份数 = grm.trig 幅度（分裂 3、分叉 2）");
        }

        // ── B. 内核三张表（FGR-EMG-045）──────────────────────────────────────────────

        private static void CheckKernelRows()
        {
            Line("  · B. 内核三张表：节点与树一致、指令行 = 声明逐行（读写各一行，序号 = 程序位置）、继承行带标记、动词按载体解析、监听掩码 / 需要飞行 / 核心标志、登记去重");
            EmgCompiled c = EmgCatalog.Current;
            EmgProgramResult ex1 = P(A(Homing), A(Split, Split));
            EmgProgramResult ex3 = P(A(Delay, Pierce), A(Oil, Homing));
            EmgProgramResult ex2 = P(null, A(Split, Oil));
            EmgProgramResult sw = P(A(Swarm), A(Corrode, Split));
            bool nodesOk = true;
            foreach (EmgProgramResult r in new[] { ex1, ex2, ex3, sw })
            {
                for (int i = 0; i < r.Nodes.Count; i++)
                {
                    EmgNode k = r.KernelNodes[i];
                    EmgTreeNode n = r.Nodes[i];
                    int rows = n.Entries.Sum(e => RowsOf(c, r, e));
                    nodesOk &= k.Parent == n.Parent && k.Gen == n.Gen && k.TrigEvent == n.TrigEvent && k.TrigCount == n.TrigCount && k.TrigParam == n.TrigParam
                               && k.InstrCount == rows && (n.Parent < 0 ? k.SubForm == 0 : k.SubForm == SubFormOf(c, 0));
                }
            }
            Expect(nodesOk, "内核节点 = 程序树（父、代、触发事件 / 份数 / 参数、子形态 = 射弹子形态码），每个节点的指令行数 = 自有声明 + 继承行（默认只带修饰写端与读端）");

            // 例 1：根 = 寻的（读 prop.heat、写 grm.mod.homing）+ 分裂（读 evt.hit、写 grm.trig）；1 代 = 分裂 + 寻的继承副本；2 代 = 寻的继承副本
            List<EmgInstr> x = ex1.KernelInstrs;
            bool ordinals = x.Take(4).Select(i => i.Ordinal).SequenceEqual(new byte[] { 0, 0, 1, 1 });
            bool inheritFlags = x.Where(i => (i.Flags & EmgInstrFlags.Inherited) != 0).All(i => i.Ordinal == 0) && x.Count(i => (i.Flags & EmgInstrFlags.Inherited) != 0) == 4;
            Expect(ordinals && inheritFlags && ex1.KernelNodes[1].InheritMask == 1 && ex1.KernelNodes[2].InheritMask == 1,
                $"例 1 指令行：{x.Count} 行，前 4 行序号 {string.Join(",", x.Take(4).Select(i => i.Ordinal))}；继承行 {x.Count(i => (i.Flags & EmgInstrFlags.Inherited) != 0)} 行全部来自序号 0（寻的）；1 / 2 代继承掩码 = 第 0 代");

            bool verbs = true;
            foreach (EmgProgramResult r in new[] { ex1, ex2, ex3, sw })
            {
                foreach (EmgInstr i in r.KernelInstrs)
                {
                    bool write = IsWriteRole((EmgRole)i.Role);
                    EmgPortCarrierRow pc = c.PortCarriers.FirstOrDefault(p => p.Port == i.Port && p.Carrier == 0);
                    bool has = c.PortCarriers.Any(p => p.Port == i.Port && p.Carrier == 0);
                    verbs &= write ? (has ? i.Verb == pc.Verb && i.A == pc.A && i.B == pc.B && i.C == pc.C : i.Verb == 0) : i.Verb == 0;
                }
            }
            EmgProgramResult melee = EmgProgramCompiler.Compile(A(Homing), A(Split, Split), FirmwareCarrier.Melee);
            Expect(verbs && melee.KernelNodes[1].SubForm == SubFormOf(c, 1) && melee.KernelNodes[1].SubForm != ex1.KernelNodes[1].SubForm,
                "写端动词 / 参数按“端口 × 本武器载体”查 fg.TbPortCarrier（查不到记 0，读端恒为 0）；同一程序装在格斗载体上子形态换成格斗那一行");

            uint Bit(EmgEventKind e) => 1u << (int)e;
            Expect((ex3.Program.ListenMask & Bit(EmgEventKind.Timer)) != 0 && (ex3.Program.ListenMask & Bit(EmgEventKind.Expire)) != 0 && (ex3.Program.ListenMask & Bit(EmgEventKind.Hit)) == 0
                   && (ex2.Program.ListenMask & Bit(EmgEventKind.Hit)) != 0 && (ex2.Program.ListenMask & 1u) == 0,
                $"监听掩码只含程序读的事件：例 3 = 定时 + 终结（{ex3.Program.ListenMask:X8}，不含命中）；例 2 = 命中 + 终结（{ex2.Program.ListenMask:X8}）");
            Expect((ex1.Program.Flags & EmgProgramFlags.NeedsFlight) != 0 && (ex3.Program.Flags & EmgProgramFlags.NeedsFlight) != 0 && (ex2.Program.Flags & EmgProgramFlags.NeedsFlight) == 0,
                "需要飞行（ADR ③）：根节点有寻的（例 1）或根挂定时子节点（例 3）= 是；[分裂, 漏油] 命中放子弹药 = 否（保持即时命中）");
            bool coreRows = sw.KernelInstrs.Where(i => i.Ordinal == 0).All(i => (i.Flags & EmgInstrFlags.Core) != 0)
                            && sw.KernelInstrs.Where(i => i.Ordinal != 0).All(i => (i.Flags & EmgInstrFlags.Core) == 0);
            Expect(FirmwareKinds.IsCore(Swarm) && coreRows && (sw.Program.Flags & EmgProgramFlags.HasCore) != 0 && (ex1.Program.Flags & EmgProgramFlags.HasCore) == 0,
                "核心固件（集群协议，只能经接入口）的指令行与继承副本都带“核心”标记，程序带 HasCore（信号离开时剥离用，FGR-EMG-037）");

            using (var k = new CombatKernel(CombatConfig.Default, 8))
            {
                int a = CombatSite.RegisterProgram(k, ex1), b = CombatSite.RegisterProgram(k, ex3), again = CombatSite.RegisterProgram(k, P(A(Homing), A(Split, Split)));
                k.TryGetProgram(a, out EmgProgram kp);
                bool rowsEqual = KernelRowsEqual(k, a, ex1);
                Expect(a == 0 && b == 1 && again == 0 && k.ProgramCount == 2 && kp.HeatPerShot == ex1.HeatTotal && rowsEqual,
                    $"登记进内核程序表：两个不同程序得到 0 / 1，同一程序再编再登记返回原下标 {again}（按完整字节去重，表 {k.ProgramCount} 行）；表里读回的节点 / 指令 / 热量与编译结果逐项相同");
            }
        }

        private static int RowsOf(EmgCompiled c, EmgProgramResult r, EmgTreeEntry e)
        {
            EmgProgramStep s = r.Steps[e.Ordinal];
            bool widened = r.Steps.Any(t => !t.Overflow && t.Decls.Any(d => c.Decls[d].Dir == EmgDirs.Write && c.PortIdByCode[c.Decls[d].Port] == "grm.inherit"));
            return s.Decls.Count(d => c.Decls[d].Dir == EmgDirs.Read || !e.Inherited || widened || c.PortIdByCode[c.Decls[d].Port].StartsWith("grm.mod.", StringComparison.Ordinal));
        }

        private static byte SubFormOf(EmgCompiled c, byte carrier) => c.SubForms.FirstOrDefault(s => s.Carrier == carrier).Code;

        private static bool IsWriteRole(EmgRole r) => r >= EmgRole.Emit && r <= EmgRole.Apply;

        private static bool KernelRowsEqual(CombatKernel k, int index, EmgProgramResult r)
        {
            if (!k.TryGetProgram(index, out EmgProgram p) || p.NodeCount != r.KernelNodes.Count || p.InstrTotal != r.KernelInstrs.Count
                || p.Flags != r.Program.Flags || p.ListenMask != r.Program.ListenMask || p.HeatPerShot != r.Program.HeatPerShot)
            {
                return false;
            }
            k.TryGetEmgNode(p.FirstNode, out EmgNode root);
            int baseInstr = root.FirstInstr;
            for (int j = 0; j < p.NodeCount; j++)
            {
                k.TryGetEmgNode(p.FirstNode + j, out EmgNode n);
                EmgNode e = r.KernelNodes[j];
                e.FirstInstr += baseInstr;
                if (!n.Equals(e))
                {
                    return false;
                }
            }
            for (int j = 0; j < p.InstrTotal; j++)
            {
                k.TryGetEmgInstr(baseInstr + j, out EmgInstr x);
                if (!x.Equals(r.KernelInstrs[j]))
                {
                    return false;
                }
            }
            return true;
        }

        // ── C. 同端口幂等（FGT-EMG-014 阶段 1）─────────────────────────────────────────

        private static void CheckIdempotence()
        {
            Line("  · C. 同端口幂等（FGR-EMG-017）：同节点同键只生效一次取大；面过滤不同的反弹、生效事件不同的排放各自生效");
            EmgCompiled c = EmgCatalog.Current;
            ushort bounce = Code(c, "grm.mod.bounce"), homing = Code(c, "grm.mod.homing"), acidPort = Code(c, "mat.acid");
            EmgProgramResult walls = P(A(Ricochet, Wallslam), null);
            var bounces = walls.KernelInstrs.Where(i => i.Port == bounce).ToList();
            Expect(walls.Notes.Count == 0 && bounces.Count == 2 && bounces.Select(i => i.Param2).OrderBy(v => v).SequenceEqual(new[] { 1f, 2f }),
                "[跳弹, 壁撞]：两种反弹都生效（撞单位 / 撞非单位面是键的一部分，各 1 行，面过滤 1 / 2）");
            EmgProgramResult twice = P(A(Homing, Homing), null);
            var homings = twice.KernelInstrs.Where(i => i.Port == homing).ToList();
            Expect(homings.Count == 1 && Notes(twice) == "duplicate:homing@0" && twice.Steps.Count == 2 && twice.Nodes[0].Used == 2,
                $"[寻的, 寻的]：第二条“重复修饰无效”（原因键 emg.compile.duplicate），只留 1 行；两条都占容量（{twice.Nodes[0].Used}/4）");
            EmgProgramResult bigger = P(A(WeakHoming, Homing), A(LockCache));
            var h = bigger.KernelInstrs.Where(i => i.Port == homing).ToList();
            Expect(h.Count == 2 && h[0].Ordinal == 0 && h[0].Amount == 0.85f && h[1].Ordinal == 2 && h[1].Amount == 0.5f && Notes(bigger) == "duplicate:homing@0"
                   && h[0].Flags.HasFlag(EmgInstrFlags.PreferHeat) && !h[0].Flags.HasFlag(EmgInstrFlags.PreferMarked) && h[1].Flags.HasFlag(EmgInstrFlags.PreferMarked),
                $"同键取幅度大者：[测试寻的 0.3, 寻的 0.85, 锁定缓存] 出手寻的留在先到的位置、幅度取 {(h.Count > 0 ? h[0].Amount : 0f):0.##}；锁定缓存的寻的生效事件是命中，与出手不同键，另占 1 行");
            EmgProgramResult acid = P(A(Corrode, Acid), null);
            Expect(acid.Notes.Count == 0 && acid.KernelInstrs.Count(i => i.Port == acidPort) == 2,
                "[腐蚀弹头, 酸液]：命中排酸、终结排酸生效事件不同，各 1 行、都计热量");
            EmgProgramResult dup = P(A(Corrode, Corrode), null);
            Expect(dup.KernelInstrs.Count(i => i.Port == acidPort) == 1 && dup.Notes.Count == 2 && dup.Notes.All(n => n.Key == EmgProgramCompiler.NoteDuplicate && n.Ordinal == 1),
                "[腐蚀弹头, 腐蚀弹头]：第二条的两个写端（腐蚀注入、排酸）都标重复无效，各只留 1 行");

            // [电弧, 标记跳转]（FGT-EMG-014 阶段 1、FG19A A5）：连锁同键只留 1 行、跳数取大、偏好按读端并（已标记 + 导电，内核按“已标记 > 导电 > 最近”挑）；en.elec 与连锁无关、单独一行
            ushort chain = Code(c, "grm.mod.chain"), elec = Code(c, "en.elec");
            const EmgInstrFlags both = EmgInstrFlags.PreferMarked | EmgInstrFlags.PreferConductive;
            EmgProgramResult arcMark = P(A(ArcChain, MarkTag), null), markArc = P(A(MarkTag, ArcChain), null), markOnly = P(A(MarkTag), null);
            var ch = arcMark.KernelInstrs.Where(i => i.Port == chain).ToList();
            var chRev = markArc.KernelInstrs.Where(i => i.Port == chain).ToList();
            Expect(ch.Count == 1 && ch[0].Amount == 2f && (ch[0].Flags & both) == both && arcMark.KernelInstrs.Count(i => i.Port == elec) == 1 && Notes(arcMark) == "duplicate:marktag@0"
                   && chRev.Count == 1 && (chRev[0].Flags & both) == both && chRev[0].Amount == 2f && chRev[0].Ordinal == 0,
                $"[电弧, 标记跳转]：连锁 {ch.Count} 行、跳数 {(ch.Count > 0 ? ch[0].Amount : 0f)}、偏好位 {(ch.Count > 0 ? ch[0].Flags & both : 0)}（已标记 + 导电，固定顺序已标记在前）；注电 en.elec 单独 1 行；第二条“重复无效”；换槽序 [标记跳转, 电弧] 合并结果相同");
            var mo = markOnly.KernelInstrs.Where(i => i.Port == chain).ToList();
            Expect(FirmwareKinds.IsCore(MarkTag) && !FirmwareKinds.IsCore(ArcChain) && mo.Count == 1 && (mo[0].Flags & (both | EmgInstrFlags.Core)) == (EmgInstrFlags.PreferMarked | EmgInstrFlags.Core)
                   && ch.Count == 1 && (ch[0].Flags & EmgInstrFlags.Core) == 0 && chRev.Count == 1 && (chRev[0].Flags & EmgInstrFlags.Core) == 0,
                "核心 + 常规同键：只有标记跳转（核心）时连锁行带核心、只偏好已标记；与电弧（常规）合并后不论谁先到都不带核心（信号离开剥离核心时常规那份留下）");
        }

        private static ushort Code(EmgCompiled c, string port) => (ushort)c.PortIdByCode.First(kv => kv.Value == port).Key;

        // ── D. 修饰读端与继承（FGT-EMG-015）───────────────────────────────────────────

        private static void CheckModifierReads()
        {
            Line("  · D. 修饰读端（FGR-EMG-012、033）：霰射（evt.fire）与跳弹（evt.hit）的读端只决定生效时机，不放子载荷；被继承时读端一并带上；锁定缓存默认只继承寻的写端");
            EmgCompiled c = EmgCatalog.Current;
            ushort fire = Code(c, "evt.fire"), hit = Code(c, "evt.hit"), multi = Code(c, "grm.mod.multi"), bounce = Code(c, "grm.mod.bounce"), marked = Code(c, "evt.marked"), homing = Code(c, "grm.mod.homing");
            EmgProgramResult r = P(A(Scatter, Ricochet), A(Split));
            int first = r.KernelNodes[1].FirstInstr, count = r.KernelNodes[1].InstrCount;
            List<EmgInstr> child = r.KernelInstrs.Skip(first).Take(count).ToList();
            bool carries = child.Any(i => i.Port == fire && i.Role == (byte)EmgRole.Trigger && i.Ordinal == 0) && child.Any(i => i.Port == multi && i.Ordinal == 0)
                           && child.Any(i => i.Port == hit && i.Role == (byte)EmgRole.Trigger && i.Ordinal == 1) && child.Any(i => i.Port == bounce && i.Ordinal == 1)
                           && child.All(i => (i.Flags & EmgInstrFlags.Inherited) != 0);
            Expect(r.Nodes.Count == 2 && r.Nodes[1].TrigEvent == EmgEventKind.Hit && carries,
                "[霰射, 跳弹, 分裂]：只有分裂放子节点（共 2 个节点，子节点挂命中）；子节点里霰射的 evt.fire 读端 + 多份写端、跳弹的 evt.hit 读端 + 反弹写端都作为继承行带上");
            EmgProgramResult lc = P(A(LockCache), A(Split));
            List<EmgInstr> lcChild = lc.KernelInstrs.Skip(lc.KernelNodes[1].FirstInstr).Take(lc.KernelNodes[1].InstrCount).ToList();
            List<EmgInstr> lcRoot = lc.KernelInstrs.Take(lc.KernelNodes[0].InstrCount).ToList();
            Expect(lcChild.Any(i => i.Port == homing) && !lcChild.Any(i => i.Port == marked && IsWriteRole((EmgRole)i.Role)) && lcChild.Any(i => i.Port == marked && i.Role == (byte)EmgRole.Sense)
                   && lcRoot.Any(i => i.Port == marked && i.Role == (byte)EmgRole.Emit),
                "[锁定缓存, 分裂]：子节点继承寻的写端与两个读端，不继承“发 evt.marked”效果写端（根上照常有，FG19A 第 0.3 节口径 5）");
        }

        // ── E. 热量分项（FGT-EMG-017 阶段 1）──────────────────────────────────────────

        private static void CheckHeat()
        {
            Line("  · E. 热量分项（FGR-EMG-016）：指令表值 + 声明 heat + 触发节点（trigger_gen × 代号）+ 子载荷份数 × per_child；溢出不计；对照组去掉触发器热量下降");
            float tg = Tuning("emg.heat.trigger_gen"), pc = Tuning("emg.heat.per_child");
            float H(string id) => FirmwareKinds.HeatOf(id);
            var groups = new (string Name, EmgProgramResult R, float Instr, float Decl, float Trig, int Children)[]
            {
                ("例 1 [寻的, 分裂, 分裂]", P(A(Homing), A(Split, Split)), H(Homing) + 2 * H(Split), 0f, tg * 1 + tg * 2, 3 + 9),
                ("例 2 [分裂, 漏油]", P(null, A(Split, Oil)), H(Split) + H(Oil), 0f, tg, 3),
                ("代际已满 [分裂 ×3]", P(A(Split), A(Split, Split)), 3 * H(Split), 0f, tg * 1 + tg * 2, 3 + 9),
                ("混合溢出", P(A(EtchShell, EtchShell), A(Homing, Pierce)), 2 * H(Corrode) + 2 * H(Oil), 0f, 0f, 0),
                ("霰射声明热量（继承副本不重复计）", P(A(Scatter, Ricochet), A(Split)), H(Scatter) + H(Ricochet) + H(Split), DeclHeat(Scatter), tg, 3),
                ("扩大继承二层", P(A(Swarm, Delay), A(EtchShell, Split)), H(Swarm) + H(Delay) + H(Corrode) + H(Oil) + H(Split), 0f, tg * 1 + tg * 1 + tg * 2, 3 + 3 + 9),
            };
            foreach ((string name, EmgProgramResult r, float instr, float decl, float trig, int children) in groups)
            {
                bool ok = Near(r.HeatInstr, instr) && Near(r.HeatDecl, decl) && Near(r.HeatTrigger, trig) && r.ChildPayloads == children && Near(r.HeatChildren, children * pc)
                          && Near(r.Program.HeatPerShot, r.HeatTotal) && Near(r.HeatTotal, instr + decl + trig + children * pc);
                Expect(ok, $"{name}：指令 {r.HeatInstr:0.##} / 声明 {r.HeatDecl:0.##} / 触发节点 {r.HeatTrigger:0.##} / 子载荷 {r.ChildPayloads} 份 × {pc:0.##} = 每发 {r.HeatTotal:0.##}（内核程序行 {r.Program.HeatPerShot:0.##}）");
            }
            EmgProgramResult over = groups[3].R;
            Expect(Near(over.HeatOverflowInstr, H(Homing) + H(Pierce)) && Near(over.WeaponHeatDelta, -(H(Homing) + H(Pierce))),
                $"溢出的寻的 / 贯穿不计热量：武器积热在旧算法（按装着的固件）之上扣回 {over.HeatOverflowInstr:0.##}");
            EmgProgramResult withTrig = P(null, A(Split, Oil)), noTrig = P(null, A(Oil));
            Expect(withTrig.HeatTotal > noTrig.HeatTotal + H(Split) && Near(noTrig.HeatTrigger + noTrig.HeatChildren, 0f),
                $"对照组：去掉分裂后每发热量 {withTrig.HeatTotal:0.##} → {noTrig.HeatTotal:0.##}（触发节点与子载荷两项归零）");
        }

        private static float DeclHeat(string owner)
        {
            EmgCompiled c = EmgCatalog.Current;
            int oi = c.OwnerIndex[owner];
            return c.Decls.Where(d => d.Owner == oi).Sum(d => d.Heat);
        }

        private static float Tuning(string id) => GridContent.TryGetTuning(id, out float v) ? v : float.NaN;

        private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;

        // ── F. 程序顺序与接入（FGR-EMG-030、T3、事实 1.18）──────────────────────────────

        internal static BlueprintCircuitBoard GunBoard(bool uplink, params string[] firmware)
        {
            BlueprintCircuitBoard b = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, firmware);
            if (uplink && !b.TrySetUplink(2).Success)
            {
                Fail("测试准备：连射器蓝图 2 号格标接入口失败");
            }
            return b;
        }

        private static void CheckOrderAndUplink()
        {
            Line("  · F. 程序顺序：接入固件（实际插入的、信号核槽序）在前、机器固件槽在后；预览 FirmwareIds 顺序相反（反例）；AI / 接入口不在路径上只编机器槽；超配额的不进程序");
            BlueprintCircuitBoard board = GunBoard(true, Pierce, Oil);
            BlueprintCircuitPreview up = UplinkCompiler.CompileUplinked(board, new[] { Split });
            EmgProgramResult r = EmgProgramCompiler.Compile(up);
            Expect(up.UplinkOnPath && up.UplinkFirmwareIds.SequenceEqual(new[] { Split }) && up.LocalFirmwareIds.SequenceEqual(new[] { Pierce, Oil })
                   && up.FirmwareIds.SequenceEqual(new[] { Pierce, Oil, Split })
                   && string.Join(",", r.Steps.Select(s => Short(s.OwnerId))) == "split,pierce,oilleak" && r.Steps[0].FromUplink && !r.Steps[1].FromUplink
                   && Tree(r) == "0 g0 1/4: split | 1<0 g1 2/2 hit×3: pierce oilleak",
                $"真实接入编译：信号核 [分裂] + 机器 [贯穿, 漏油] → 程序 {string.Join(",", r.Steps.Select(s => Short(s.OwnerId)))}（预览 FirmwareIds = {string.Join(",", up.FirmwareIds.Select(Short))} 是机器在前）；接入把机器固件装进子弹药：{Tree(r)}");
            EmgProgramResult wrong = P(null, up.FirmwareIds);
            Expect(wrong.Signature != r.Signature && Tree(wrong) != Tree(r),
                $"反例：拿 FirmwareIds（机器在前）编出来的是另一棵树（{Tree(wrong)}），签名不同——编译器只认 UplinkFirmwareIds + LocalFirmwareIds");
            EmgProgramResult ai = EmgProgramCompiler.Compile(BlueprintCircuitCompiler.CompilePreview(board));
            Expect(Tree(ai) == "0 g0 2/4: pierce oilleak" && ai.Steps.All(s => !s.FromUplink), $"AI 驾驶（接入口按空槽）：只编 [S1, S2]：{Tree(ai)}");

            BlueprintCircuitBoard off = GunBoard(false, Pierce, Oil);
            int offSlot = Enumerable.Range(1, BlueprintCircuitLayout.SlotCount - 2).FirstOrDefault(i => BlueprintCircuitLayout.IsUplinkCandidate(i) && string.IsNullOrEmpty(off.SlotContentIds[i]) && !off.IsOnSourceSinkPath(i));
            bool placed = offSlot > 0 && off.TrySetUplink(offSlot).Success;
            BlueprintCircuitPreview offPrev = UplinkCompiler.CompileUplinked(off, new[] { Split });
            EmgProgramResult offProg = EmgProgramCompiler.Compile(offPrev);
            Expect(placed && !offPrev.UplinkOnPath && offPrev.UplinkFirmwareIds.Length == 0 && Tree(offProg) == "0 g0 2/4: pierce oilleak",
                $"接入口（{offSlot} 号格）不在任何源→汇路径上：信号核固件不接通，程序只有机器槽：{Tree(offProg)}");

            BlueprintCircuitPreview quota = UplinkCompiler.CompileUplinked(GunBoard(true), new[] { Homing, Pierce, Split });
            EmgProgramResult q = EmgProgramCompiler.Compile(quota);
            Expect(UplinkCompiler.Quota == 2 && quota.UplinkFirmwareIds.Length == 2 && string.Join(",", q.Steps.Select(s => Short(s.OwnerId))) == "homing,pierce" && q.RootCapacity == 4,
                $"T3：信号核 3 条、接入口配额 {UplinkCompiler.Quota}（不随等级提升，按推荐默认执行）→ 只有实际插入的前 2 条进程序（{string.Join(",", q.Steps.Select(s => Short(s.OwnerId)))}）；C0 = 槽 {BlueprintCircuitBoard.FirmwareSlotCount} + 配额 = {q.RootCapacity}");

            BlueprintCircuitBoard inert = GunBoard(true, Swarm, Pierce);
            EmgProgramResult inertProg = EmgProgramCompiler.Compile(BlueprintCircuitCompiler.CompilePreview(inert));
            Expect(BlueprintCircuitCompiler.CompilePreview(inert).InertCoreFirmwareIds.Contains(Swarm) && string.Join(",", inertProg.Steps.Select(s => Short(s.OwnerId))) == "pierce",
                "T3：机器固件槽里残留的核心固件（集群协议）AI 不用、不进程序；AiUsable 保持槽序（剩下的贯穿）");
        }

        // ── G. 签名 ─────────────────────────────────────────────────────────────

        private static void CheckSignature()
        {
            Line("  · G. 签名：同一程序同一内容版本得到同一棵树与同一签名；换槽序、换接入状态签名不同");
            EmgProgramResult a = P(A(Delay, Pierce), A(Oil, Homing)), b = P(A(Delay, Pierce), A(Oil, Homing));
            EmgProgramResult swapped = P(A(Delay, Pierce), A(Homing, Oil)), local = P(null, A(Delay, Pierce));
            Expect(a.Signature == b.Signature && Tree(a) == Tree(b) && a.KernelInstrs.SequenceEqual(b.KernelInstrs) && a.Signature.StartsWith(CampaignSaveService.CurrentContentVersion + "-", StringComparison.Ordinal),
                $"重编同一输入：签名 {a.Signature} 相同、指令行逐字节相同；签名带内容版本 {CampaignSaveService.CurrentContentVersion}");
            Expect(swapped.Signature != a.Signature && Tree(swapped) != Tree(a) && local.Signature != a.Signature,
                $"同一蓝图换槽序重编：树变了（{Tree(swapped)}）、签名不同；接入状态不同签名也不同");
        }

        // ── H. 负向 ─────────────────────────────────────────────────────────────

        private static void CheckNegatives()
        {
            Line("  · H. 负向：全空程序 / 只有旧轨固件 / emg.stage = 0 不登记程序；表满给原因并退回无程序；编译器源码无内容 ID 判断");
            EmgProgramResult none = P(null, null);
            EmgProgramResult legacy = P(null, A("fw_coolant"));
            bool coolantDeclared = EmgCatalog.Current.OwnerIndex.ContainsKey("fw_coolant");
            using (var k = new CombatKernel(CombatConfig.Default, 8))
            {
                Expect(none.IsEmpty && CombatSite.RegisterProgram(k, none) == -1 && (coolantDeclared || (legacy.IsEmpty && legacy.LegacyOwners.SequenceEqual(new[] { "fw_coolant" })))
                       && k.ProgramCount == 0,
                    "全空程序（没装固件，或只装了还没声明的冷却液）：不登记程序（−1，表 0 行），没声明的固件记“仍走旧读法”，武器照常基础攻击");
            }
            UseStage0Catalog();
            EmgProgramResult stage0 = P(A(Homing), A(Split, Split));
            Expect(EmgCatalog.EffectiveStage == 0 && stage0.IsEmpty && stage0.LegacyOwners.Count == 3,
                $"emg.stage = 0（回滚对照；正式表阶段 1 出口起缺省 1）：全部 owner 走旧轨，程序为空（旧路径照旧，FGR-EMG-019）");
            UseStage1Catalog();

            BlueprintCircuitPreview pv = UplinkCompiler.CompileUplinked(GunBoard(true, Pierce, Oil), new[] { Split });
            using (var site = new CombatSite("emgprg-full", CombatConfig.Default))
            {
                for (int i = 0; i < EmgConst.MaxPrograms; i++)
                {
                    site.Kernel.AddProgram(FgEmergenceKernelSelfCheck.SampleProgram(i), FgEmergenceKernelSelfCheck.SampleNodes(), FgEmergenceKernelSelfCheck.SampleInstrs());
                }
                CombatWeapon baseW = CombatSite.MachineWeaponFrom(pv);
                CombatWeapon w = site.WithProgram(pv, baseW, out EmgProgramResult res, out string note);
                Expect(site.Kernel.ProgramCount == EmgConst.MaxPrograms && !res.IsEmpty && w.Program == -1 && note == EmgProgramCompiler.NoteTableFull
                       && CombatKernel.WeaponKey(w) == CombatKernel.WeaponKey(baseW) && GameText.Get(note) != note,
                    $"程序表满（{EmgConst.MaxPrograms} 行）：武器退回无程序（键与不编程序时逐字相同），原因键 {note} =“{GameText.Get(note)}”（DEBT-EMG1KRN01-01 的 −1 原因）");
            }

            string src = File.ReadAllText(Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic/Campaign/Emergence/EmgProgramCompiler.cs"));
            string[] hits = EmgCatalog.ContentIdPrefixes.Where(pfx => src.Contains("\"" + pfx)).ToArray();
            Expect(hits.Length == 0 && !src.Contains("Catalog.Fw") && !src.Contains("Catalog.Comp"),
                $"编译器源码里没有内容 ID 字面量与内容常量引用（FGR-EMG-013）{(hits.Length > 0 ? "：" + string.Join("、", hits) : string.Empty)}");
        }

        // ── I. 旧内容版本提示（ADR ⑩、T9）──────────────────────────────────────────────

        private static void CheckStaleContent()
        {
            Line("  · I. 内容版本升到 2：旧内容版本编译保存的蓝图提示“基于已变化内容编译，请重新编译”，重新保存后不再提示");
            BlueprintVersionRecord fresh = GunBoard(false, Pierce).ToVersion(1, 0f);
            BlueprintVersionRecord old = GunBoard(false, Pierce).ToVersion(1, 0f);
            old.ContentVersionAtCompile = CampaignSaveService.CurrentContentVersion - 1;
            string notice = BlueprintEditorService.StaleContentNotice(old);
            BlueprintVersionRecord resaved = BlueprintCircuitBoard.FromVersion(old).ToVersion(2, 0f);
            Expect(CampaignSaveService.CurrentContentVersion == 2 && fresh.ContentVersionAtCompile == 2 && !BlueprintEditorService.IsCompiledOnOldContent(fresh)
                   && BlueprintEditorService.IsCompiledOnOldContent(old) && notice != null && notice.Contains("重新编译") && BlueprintEditorService.StaleContentNotice(resaved) == null
                   && BlueprintEditorService.StaleContentNotice(null) == null,
                $"CurrentContentVersion = {CampaignSaveService.CurrentContentVersion}；内容版本 1 编译的版本被标记（“{notice}”），按当前内容重新保存的新版本不再提示；当前版本保存的不提示");
            CampaignState s = CampaignState.CreateNew("emgprg-factory", "Standard", 7101);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            int staleSeeded = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).SelectMany(b => b.Versions ?? Array.Empty<BlueprintVersionRecord>()).Count(BlueprintEditorService.IsCompiledOnOldContent);
            Expect(staleSeeded == 0, $"新战役种下的默认蓝图版本都不是“旧内容编译”（{staleSeeded} 个被误标）");
        }

        // ── J. 正式流程（家园地点、接入、存读档）──────────────────────────────────────────

        private static CampaignState NewWorld(int seed)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            SignalCoreService.ResetForTests();
            MachineLoadoutRegistry.Clear();
            SignalUplinkService.ResetForTests();
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, true, 2000);
            FgProductionSelfCheck.PowerUp(s);
            GridCell? g = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 6f, 22f);
            if (g.HasValue)
            {
                FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeGenerator2, "emgprg_gen", g.Value);
            }
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            HomeValleyPowerGrid.Recompute(s);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void SetBlueprint(CampaignState s, params BlueprintCircuitBoard[] versions)
        {
            BlueprintVersionRecord[] v = versions.Select((b, i) => b.ToVersion(i + 1, 0f)).ToArray();
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != Bp)
                .Append(new BlueprintRecord { BlueprintId = Bp, DisplayName = Bp, ActiveVersion = 1, Versions = v }).ToArray();
        }

        /// <summary>机器在地点内核里的武器（经单位 → 武器表）。</summary>
        private static bool KernelWeapon(CombatSite site, int logicId, out CombatWeapon w)
        {
            w = default;
            return site != null && site.TryGetMachineUnit(logicId, out int u) && site.Kernel.TryGetUnit(u, out CombatUnitView v) && site.Kernel.TryGetWeapon(v.Weapon, out w);
        }

        private static CombatSite Home => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        private static void CheckSitePipeline()
        {
            Line("  · J. 正式流程（家园）：机器按装配编程序并写回武器；接入（信号核 [分裂]）后程序 = 接入在前；接入中途换装配重编；断开回本地程序；真实存档读档后程序下标写回、去重不长表；emg.stage = 0 武器逐字不变");
            CampaignState s = NewWorld(7102);
            CombatSite site = Home;
            SetBlueprint(s, GunBoard(true, Pierce, Oil), GunBoard(true, Oil, Pierce));
            MachineOpResult m = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, Bp, HomeValleyLayout.RegionId, new Vector2(4f, 4f), 200f, 200f);
            MachineLoadoutRegistry.Register(s, m.LogicId, Bp, 1);
            WorldSimulation.StepMany(2);
            int id = m.LogicId;
            if (site != null && site.TryGetMachineUnit(id, out _))
            {
                site.RefreshMachineWeapon(s, id); // 上场那一步的结算 + 这里再走一次 Changed 同一个入口（不改结果，只为不依赖上场时序）
            }
            MachineWeaponInfo i1 = default;
            site?.TryGetMachineWeapon(id, out i1);
            bool aiOk = KernelWeapon(site, id, out CombatWeapon w1) && i1.Program != null
                        && Tree(i1.Program) == "0 g0 2/4: pierce oilleak" && w1.Program >= 0 && KernelRowsEqual(site.Kernel, w1.Program, i1.Program);
            BlueprintCircuitPreview aiPrev = MachineLoadoutRegistry.ResolveForPilot(s, id, s.RandomSeed).Preview;
            float expectHeat = CombatSite.MachineWeaponFrom(aiPrev).HeatPerShot + i1.Program.WeaponHeatDelta;
            Expect(aiOk && Near(w1.HeatPerShot, expectHeat),
                $"装配登记（MachineLoadoutRegistry.Changed → RefreshMachineWeapon）后：内核武器程序下标 {w1.Program}，表里的程序 = 编译结果（{(i1.Program != null ? Tree(i1.Program) : "无")}）；每发积热 {w1.HeatPerShot:0.##} = 旧算法 + 新分项");

            SignalCoreTestEquip(s, Split, 0);
            s.SignalCore.UplinkMachineLogicId = id;
            s.SignalCore.UplinkSiteId = HomeValleyLayout.RegionId;
            MachineLoadoutRegistry.NotifyChanged(id);
            MachineWeaponInfo i2 = default;
            site?.TryGetMachineWeapon(id, out i2);
            bool upOk = KernelWeapon(site, id, out CombatWeapon w2) && i2.Uplinked
                        && Tree(i2.Program) == "0 g0 1/4: split | 1<0 g1 2/2 hit×3: pierce oilleak" && w2.Program >= 0 && w2.Program != w1.Program
                        && KernelRowsEqual(site.Kernel, w2.Program, i2.Program);
            Expect(upOk, $"接入（信号核 1 号槽 = 分裂）：重编为 {(i2.Program != null ? Tree(i2.Program) : "无")}，内核武器换到新程序 {w2.Program}");

            if (MachineRegistry.TryGetRecord(id, out MachineRecord rec))
            {
                rec.BlueprintVersion = 2; // 回厂改造换到第 2 版（记录与登记一起改）
            }
            MachineLoadoutRegistry.Register(s, id, Bp, 2);
            MachineWeaponInfo i3 = default;
            site?.TryGetMachineWeapon(id, out i3);
            bool swapOk = KernelWeapon(site, id, out CombatWeapon w3) && i3.Uplinked
                          && Tree(i3.Program) == "0 g0 1/4: split | 1<0 g1 2/2 hit×3: oilleak pierce" && w3.Program != w2.Program && KernelRowsEqual(site.Kernel, w3.Program, i3.Program);
            Expect(swapOk, $"接入中途换装配（第 2 版机器槽 [漏油, 贯穿]）：随 Changed 重编为 {(i3.Program != null ? Tree(i3.Program) : "无")}，程序下标 {w2.Program} → {w3.Program}");

            s.SignalCore.UplinkMachineLogicId = 0;
            s.SignalCore.UplinkSiteId = string.Empty;
            MachineLoadoutRegistry.NotifyChanged(id);
            MachineWeaponInfo i4 = default;
            site?.TryGetMachineWeapon(id, out i4);
            bool backOk = KernelWeapon(site, id, out CombatWeapon w4) && !i4.Uplinked
                          && Tree(i4.Program) == "0 g0 2/4: oilleak pierce" && KernelRowsEqual(site.Kernel, w4.Program, i4.Program);
            Expect(backOk, $"断开：武器表重编译回本地程序 {(i4.Program != null ? Tree(i4.Program) : "无")}（FGR-EMG-037 第 3 条）");

            int rows = site.Kernel.ProgramCount;
            site.RefreshMachineWeapon(s, id);
            KernelWeapon(site, id, out CombatWeapon w5);
            Expect(w5.Program == w4.Program && site.Kernel.ProgramCount == rows, $"同一装配再结算：程序下标不变（{w5.Program}），表不增长（{rows} 行，只在装配变化时新增）");

            WorldSimulation.SyncAllForSave();
            SaveResult saved = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineLoadoutRegistry.Clear();
            RestoreResult restored = CampaignRestoreOrchestrator.Restore(Slot);
            bool loadOk = false;
            string loadInfo = restored.Success ? string.Empty : restored.Message;
            if (saved.Success && restored.Success)
            {
                CampaignSession.Set(Slot, restored.State);
                WorldSimulation.LoadHome(resume: true);
                WorldSimulation.StepMany(2);
                CombatSite after = Home;
                CampaignState rs = restored.State;
                int rid = rs.MachineRecords.First(r => r.BlueprintId == Bp).LogicId;
                EmgProgramResult expect = EmgProgramCompiler.Compile(MachineLoadoutRegistry.ResolveForPilot(rs, rid, rs.RandomSeed).Preview);
                loadOk = KernelWeapon(after, rid, out CombatWeapon lw) && lw.Program >= 0 && KernelRowsEqual(after.Kernel, lw.Program, expect)
                         && Tree(expect) == "0 g0 2/4: oilleak pierce" && lw.Program == w4.Program;
                int before = after.Kernel.ProgramCount;
                after.RefreshMachineWeapon(rs, rid);
                KernelWeapon(after, rid, out CombatWeapon lw2);
                loadOk &= lw2.Program == lw.Program && after.Kernel.ProgramCount == before;
                loadInfo = $"读档后程序下标 {lw.Program}、表 {before} 行，再结算仍是 {lw2.Program}";
            }
            Expect(loadOk, $"真实存档（CampaignAutoSaveService）→ 读档（CampaignRestoreOrchestrator）→ 家园载入：机器武器的程序下标写回（不回到“没有程序”），读档带回的程序表里已有的程序返回原下标（{loadInfo}）");

            UseStage0Catalog();
            CampaignState cs = CampaignSession.Current;
            int lid = cs.MachineRecords.First(r => r.BlueprintId == Bp).LogicId;
            Home.RefreshMachineWeapon(cs, lid);
            BlueprintCircuitPreview p0 = MachineLoadoutRegistry.ResolveForPilot(cs, lid, cs.RandomSeed).Preview;
            bool same = KernelWeapon(Home, lid, out CombatWeapon w0) && w0.Program == -1 && CombatKernel.WeaponKey(w0) == CombatKernel.WeaponKey(CombatSite.MachineWeaponFrom(p0));
            UseStage1Catalog();
            Expect(same, "emg.stage = 0（回滚对照）：同一台机器重新结算后武器没有程序，参数与只走旧路径时逐字相同（旧旅程不变）");
        }

        private static void SignalCoreTestEquip(CampaignState s, string firmwareId, int slot)
        {
            SignalCoreResult pr = SignalCoreService.TryPrintFirmwareChip(s, firmwareId);
            if (!pr.Success)
            {
                Fail($"测试准备：刻印 {firmwareId} 失败（{pr.Message}）");
                return;
            }
            Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
            SignalCoreResult r = SignalCoreService.TryEquip(s, pr.CreatedId, slot);
            SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
            if (!r.Success)
            {
                Fail($"测试准备：{firmwareId} 装入 {slot + 1} 号槽失败：{r.Message}");
            }
        }

        // ── K. 耗时 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · K. 编译耗时（只在装配变化时运行；不按帧）");
            var sw = Stopwatch.StartNew();
            const int n = 2000;
            for (int i = 0; i < n; i++)
            {
                P(A(Swarm, Delay), A(EtchShell, Split));
            }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            Expect(us < 1000.0, $"最大的固定程序（5 条指令、4 个节点、扩大继承）编译 {n} 次平均 {us:0.#} 微秒 / 次（阈值 1 毫秒；Editor batchmode Mono，真机 HybridCLR 复测归 FG15-SYS-02）");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

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
                _pass++;
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
