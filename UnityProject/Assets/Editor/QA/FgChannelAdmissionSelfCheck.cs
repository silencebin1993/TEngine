using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using GameConfig.fg;
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
    /// EMG1-DATA-01 新固件准入框架的自动验收（FG19 FGR-EMG-014；FGT-EMG-002 阶段 1 的“框架与假固件拒绝”部分）。
    /// 起真实系统：真实 fg.TbChannelPort / TbChannelDecl → 真实 <see cref="EmgCatalog.Admit"/> 与 <see cref="EmgCatalog.Compile"/>（正式编译路径同一函数）。
    /// A 已声明的固件逐条准入（本卡落 FG19A 第 1.1 节 17 条）；全部 74 条（含 30 条混合）的全量判定在 EMG1-FW-01 开启（DEBT-EMG1DATA01-01，最迟 EMG-1 出口）；
    /// B 三条假固件必须被拒：只有 numericOnly 写端的、读写都在实体族的（读 prop.mass 写 prop.mass）、只以恒真事件 evt.fire 作 trigger 的；对照：同形状但带世界量读端的准入；
    ///   假固件走正式编译路径同样被拒，且它的声明不进内核数组；
    /// C 混合固件按展开后的两条父声明各判一次；
    /// D 双轨（FGR-EMG-019）：emg.stage = 0 时没有任何 owner 走声明轨；
    /// P 性能：真实五张表编译一次的耗时（只在载入 / 表重载时发生）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgChannelAdmissionSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgChannelAdmissionSelfCheck.RunFromMenu。
    /// </summary>
    public static class FgChannelAdmissionSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/QA/自检/涌现通道准入")]
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
            Line("\n[涌现通道准入] FGR-EMG-014：已声明固件逐条准入、三条假固件被拒、混合固件按两父判、双轨 emg.stage（EMG1-DATA-01）");
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FirmwareKinds.ResetForTests();
                EmgCatalog.ResetForTests();
                Step(CheckDeclared);
                Step(CheckFakes);
                Step(CheckMixed);
                Step(CheckDualTrack);
                Step(CheckFlightColumns);
                Step(CheckPerformance);
            }
            finally
            {
                EmgCatalog.ResetForTests();
            }
            return _fail;
        }

        private static Dictionary<string, EmgSource.Port> Ports(EmgSource s) => s.Ports.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);

        private static void CheckDeclared()
        {
            Line("  · A. 已声明固件逐条准入");
            EmgSource src = EmgCatalog.FromTables();
            Dictionary<string, EmgSource.Port> ports = Ports(src);
            var fwOwners = src.Decls.Where(d => d.OwnerKind == "firmware").Select(d => d.Owner).Distinct().ToList();
            var rejected = new List<string>();
            foreach (string owner in fwOwners)
            {
                if (!EmgCatalog.Admit(EmgCatalog.DeclsOf(src, owner), ports, out string why))
                {
                    rejected.Add(owner + "：" + why);
                }
            }
            HashSet<string> kinds = new HashSet<string>(FirmwareKinds.Rows.Select(r => r.Id), StringComparer.Ordinal);
            Expect(fwOwners.Count >= 17 && rejected.Count == 0 && fwOwners.All(kinds.Contains),
                $"已声明的 {fwOwners.Count} 条固件（FG19A 第 1.1 节引信与弹芯 17 条）全部准入且都在 fg.TbFirmwareKind{(rejected.Count > 0 ? "；被拒：" + string.Join("；", rejected) : string.Empty)}");
            // func_spikes 是部件，不在准入范围，但按同标准自查（FGR-EMG-233 第 5 条）
            Expect(EmgCatalog.Admit(EmgCatalog.DeclsOf(src, "func_spikes"), ports, out string spikes),
                $"爆炸反应装甲（func_spikes，部件）按同标准自查通过：读 evt.hurt（事件族）写 ent.fragment（实体族）→ {spikes}");
            int total = FirmwareKinds.Rows.Count;
            int undeclared = FirmwareKinds.Rows.Count(r => !fwOwners.Contains(r.Id));
            Line($"    · 全量判定范围：fg.TbFirmwareKind {total} 条，已声明 {fwOwners.Count} 条，未声明 {undeclared} 条（含 30 条混合）由 EMG1-FW-01 写齐并开启全量判定（DEBT-EMG1DATA01-01，最迟 EMG-1 出口，自动测试 FGT-EMG-002）");
        }

        private static void CheckFakes()
        {
            Line("  · B. 三条假固件必须被拒（FGT-EMG-002）");
            Dictionary<string, EmgSource.Port> ports = Ports(EmgCatalog.FromTables());
            (string title, (string, EmgDirs, EmgRole)[] decls, string reason)[] fakes =
            {
                ("只有 numericOnly 写端（读当前热量、只改威力）", new[] { ("prop.heat", EmgDirs.Read, EmgRole.Sense), ("grm.mod.power", EmgDirs.Write, EmgRole.Modify) }, "非纯数值写端"),
                ("读写都在实体族（读 prop.mass、写 prop.mass）", new[] { ("prop.mass", EmgDirs.Read, EmgRole.Sense), ("prop.mass", EmgDirs.Write, EmgRole.Modify) }, "同一个通道族"),
                ("只以恒真事件 evt.fire 作 trigger", new[] { ("evt.fire", EmgDirs.Read, EmgRole.Trigger), ("grm.mod.multi", EmgDirs.Write, EmgRole.Modify) }, "感知读端"),
            };
            foreach ((string title, (string, EmgDirs, EmgRole)[] decls, string reason) in fakes)
            {
                bool ok = EmgCatalog.Admit(decls, ports, out string why);
                Expect(!ok && why.Contains(reason), $"假固件「{title}」被拒：{why}");
            }
            // 对照：同形状但补上世界量读端 / 非数值写端就准入（判定不是“一律拒绝”）
            Expect(EmgCatalog.Admit(new[] { ("evt.fire", EmgDirs.Read, EmgRole.Trigger), ("ter.cover", EmgDirs.Read, EmgRole.Sense), ("grm.mod.multi", EmgDirs.Write, EmgRole.Modify) }, ports, out _)
                   && EmgCatalog.Admit(new[] { ("prop.heat", EmgDirs.Read, EmgRole.Sense), ("grm.mod.power", EmgDirs.Write, EmgRole.Modify), ("evt.marked", EmgDirs.Write, EmgRole.Emit) }, ports, out _)
                   && EmgCatalog.Admit(new[] { ("evt.hit", EmgDirs.Read, EmgRole.Trigger), ("prop.mass", EmgDirs.Write, EmgRole.Modify) }, ports, out _),
                "对照：evt.fire 触发 + 掩体感知 + 多份载荷（霰射形状）准入；纯数值写端外加标记排放准入；evt.hit 触发 + 写质量（扩口形状，跨事件 / 实体族）准入");
            // 走正式编译路径：只有 numericOnly 写端的假固件被拒，且它的声明不进内核数组
            EmgSource src = EmgCatalog.FromTables();
            int realDecls = EmgCatalog.Compile(src).Decls.Length;
            EmgSource.Decl tpl = src.Decls.First(d => d.Owner == "fw_homing" && d.Seq == 1);
            EmgSource.Decl r1 = FgEmergenceRuleAuditSelfCheck.CopyDecl(tpl, "fw_fakenumeric", 1);
            EmgSource.Decl w1 = FgEmergenceRuleAuditSelfCheck.CopyDecl(tpl, "fw_fakenumeric", 2);
            w1.Dir = "write";
            w1.Port = "grm.mod.power";
            w1.Role = "modify";
            src.Decls.Add(r1);
            src.Decls.Add(w1);
            Func<string, string, bool> realOwner = src.OwnerExists;
            src.OwnerExists = (o, k) => o == "fw_fakenumeric" || realOwner(o, k); // 只测准入：假 owner 视为存在（外键断另有 D 段负向）
            EmgCompiled c = EmgCatalog.Compile(src);
            Expect(c.Problems.Any(p => p.Contains("fw_fakenumeric") && p.Contains("准入")) && !c.OwnerIndex.ContainsKey("fw_fakenumeric") && c.Decls.Length == realDecls,
                $"正式编译路径：假固件 fw_fakenumeric（只改威力）被拒——{c.Problems.FirstOrDefault(p => p.Contains("fw_fakenumeric"))}；它的 2 条声明不进内核数组");
        }

        private static void CheckMixed()
        {
            Line("  · C. 混合固件按展开后的两条父声明判（FGR-EMG-014）");
            EmgSource src = EmgCatalog.FromTables();
            Dictionary<string, EmgSource.Port> ports = Ports(src);
            List<(string, EmgDirs, EmgRole)> split = EmgCatalog.DeclsOf(src, "fw_split");
            List<(string, EmgDirs, EmgRole)> homing = EmgCatalog.DeclsOf(src, "fw_homing");
            var numericOnly = new List<(string, EmgDirs, EmgRole)> { ("prop.heat", EmgDirs.Read, EmgRole.Sense), ("grm.mod.power", EmgDirs.Write, EmgRole.Modify) };
            bool good = EmgCatalog.AdmitMixed(split, homing, ports, out string w1);
            bool bad = EmgCatalog.AdmitMixed(split, numericOnly, ports, out string w2);
            Expect(good && !bad && w2.StartsWith("第二条父固件"), $"[分裂 + 寻的] 两父都准入 → 准入；[分裂 + 只改数字的父] → 拒绝：{w2}");
            // 真实混合固件里两父都已声明的，逐条判
            var declared = new HashSet<string>(src.Decls.Select(d => d.Owner), StringComparer.Ordinal);
            int judged = 0, mixes = 0;
            var rejected = new List<string>();
            foreach (GameConfig.fg.FirmwareKind row in FirmwareKinds.Rows.Where(r => r.Id.StartsWith("fw_mix_", StringComparison.Ordinal)))
            {
                mixes++;
                List<string> parents = FirmwareKinds.ExpandMixed(new[] { row.Id });
                if (parents.Count != 2 || !parents.All(declared.Contains))
                {
                    continue;
                }
                judged++;
                if (!EmgCatalog.AdmitMixed(EmgCatalog.DeclsOf(src, parents[0]), EmgCatalog.DeclsOf(src, parents[1]), ports, out string why))
                {
                    rejected.Add(row.Id + "：" + why);
                }
            }
            Expect(mixes == 30 && rejected.Count == 0,
                $"30 条混合固件里两父都已声明的 {judged} 条逐条准入；其余 {mixes - judged} 条随 EMG1-FW-01 声明写齐后判（DEBT-EMG1DATA01-01）{(rejected.Count > 0 ? "；被拒：" + string.Join("；", rejected) : string.Empty)}");
        }

        private static void CheckDualTrack()
        {
            Line("  · D. 双轨（FGR-EMG-019）：emg.stage = 0 时全部 owner 走旧 readFields");
            EmgCompiled c = EmgCatalog.Current;
            int onDecl = c.OwnerIds.Count(EmgCatalog.IsOwnerOnDeclTrack);
            Expect(c.RawStage == 0f && c.EffectiveStage == 0 && onDecl == 0 && c.OwnerIds.Length > 0,
                $"fg.TbHomeTuning emg.stage = {c.RawStage:0}，生效阶段 {c.EffectiveStage}；{c.OwnerIds.Length} 个已声明 owner 里走声明轨的 {onDecl} 个（新路径全部关闭，战斗行为逐字不变）");
        }

        /// <summary>EMG1-TRIG-01（FG19A 第 2.1.1 节；FG19 FGR-EMG-035）：作战组件表的根载荷实体化列——允许实体化的组件缺值即失败（运行时不退回即时命中）；0 弹速只对不实体化的行合法。</summary>
        private static void CheckFlightColumns()
        {
            Line("  · E. 根载荷实体化列（EMG1-TRIG-01）：射弹 / 无人机主组件 flight = yes 且弹速 / 半径 / 寿命系数 > 0；其余 flight = no 且弹速 0；缺值判失败、不退回即时命中");
            var bad = new List<string>();
            int yes = 0, no = 0;
            foreach (CombatComponent c in GameLogic.Campaign.Content.CarrierReadings.Components)
            {
                string problem = GameLogic.Campaign.Combat.CombatSite.FlightProblem(c.Flight, c.FlightSpeed, c.FlightRadius, c.FlightLifeScale);
                bool wantYes = c.Slot == "main" && (c.Carrier == "projectile" || c.Carrier == "summon");
                if (problem != null || (c.Flight == "yes") != wantYes)
                {
                    bad.Add($"{c.Id}（{c.Carrier}/{c.Slot} flight={c.Flight}：{problem ?? "射弹 / 无人机主组件才允许实体化"}）");
                }
                yes += c.Flight == "yes" ? 1 : 0;
                no += c.Flight == "no" ? 1 : 0;
            }
            Expect(bad.Count == 0 && yes >= 5 && no >= 5, $"fg.TbCombatComponent：允许实体化 {yes} 行、不实体化 {no} 行，逐行成立{(bad.Count > 0 ? "；不成立：" + string.Join("、", bad) : string.Empty)}");
            // 负向（同一判定函数，表校验 R27 有对应的 2 条负向自测）：允许实体化的行缺弹速 / 半径 / 寿命系数、不实体化的行写了弹速、flight 写错都判失败；0 弹速只对 no 合法。
            var cases = new (string Name, string Flight, float Speed, float Radius, float Life, bool Ok)[]
            {
                ("yes 缺弹速", "yes", 0f, 0.15f, 1.2f, false), ("yes 缺半径", "yes", 30f, 0f, 1.2f, false), ("yes 缺寿命系数", "yes", 30f, 0.15f, 0f, false),
                ("yes 弹速非数", "yes", float.NaN, 0.15f, 1.2f, false), ("no 写了弹速", "no", 30f, 0f, 0f, false), ("flight 拼错", "Yes", 30f, 0.15f, 1.2f, false),
                ("yes 完整", "yes", 30f, 0.15f, 1.2f, true), ("no 弹速 0", "no", 0f, 0f, 0f, true),
            };
            string wrong = string.Join("、", cases.Where(x => (GameLogic.Campaign.Combat.CombatSite.FlightProblem(x.Flight, x.Speed, x.Radius, x.Life) == null) != x.Ok).Select(x => x.Name));
            // 运行时：缺值照样置 SpawnFlight（弹体不动、寿命到即止）并给原因键，不退回即时命中。
            var w = new BinGames.Sim.Combat.CombatWeapon { Mode = BinGames.Sim.Combat.CombatWeaponMode.Instant, Range = 30f, Damage = 10f };
            string note = GameLogic.Campaign.Combat.CombatSite.ApplySpawnFlight(ref w, 0f, 0.15f, 1.2f);
            var okW = new BinGames.Sim.Combat.CombatWeapon { Mode = BinGames.Sim.Combat.CombatWeaponMode.Instant, Range = 30f, Damage = 10f };
            string okNote = GameLogic.Campaign.Combat.CombatSite.ApplySpawnFlight(ref okW, 30f, 0.15f, 1.2f);
            Expect(wrong.Length == 0 && note == GameLogic.Campaign.Combat.CombatSite.NoteFlightMissing && w.SpawnFlight == 1 && w.Mode == BinGames.Sim.Combat.CombatWeaponMode.Instant
                   && okNote == null && okW.SpawnFlight == 1 && Mathf.Approximately(okW.ProjectileLife, 1.2f) && Mathf.Approximately(okW.ProjectileSpeed, 30f),
                $"负向 {cases.Length} 例判定正确{(wrong.Length > 0 ? "（判错：" + wrong + "）" : string.Empty)}；运行时缺弹速：仍置 SpawnFlight、Mode 不变、原因键 {note}（不退回即时命中）；完整参数：寿命 = 30 ÷ 30 × 1.2 = {okW.ProjectileLife:0.##} 秒、无原因键");
        }

        private static void CheckPerformance()
        {
            Line("  · P. 性能（Editor batchmode，影子工程；真机另测）");
            EmgSource src = EmgCatalog.FromTables();
            EmgCatalog.Compile(src); // 预热（JIT）
            var sw = Stopwatch.StartNew();
            const int n = 20;
            for (int i = 0; i < n; i++)
            {
                EmgCatalog.Compile(src);
            }
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / n;
            // 只在载入 / 表重载时编译一次，不按帧；预算 10 ms（一次表重载的卡顿上限），超线不到 2 倍记性能警告
            const double budget = 10.0;
            PerfGate.Expect(true, $"真实五张表编译一次 {ms:0.###} ms（预算 {budget} ms；只在载入 / 表重载时发生，每帧 0 开销）",
                new[] { PerfGate.Le(ms, budget, "涌现表编译 ms") }, Expect, Line);
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
