using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using ComposeEngine.Core;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.DebugTools;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG2-FW-03 反应行为探针与命名的自动验收（FG02 FGR-FW-030～031、040～042；FGT-FW-003；卡片负向“两条反应同时满足条件”“标签叠层上限”）。
    /// 起真实系统跑、断言行为（不是“字段存在”）：
    /// A 源数据 fgdata_reaction.py = 运行时 fg.TbReaction 逐字段；
    /// B 旧引擎规则层：运行时注册表的每一条规则都实测“两个配料 → 触发 / 只有一个 → 不触发”，名字、伤害倍率、去掉 / 加上的标签、残留 = 表；
    /// C 旧引擎内容层 + 世界层：真实固件的旧基因装在真实器官上编译，完整注册表 vs 空注册表各自交给真起的 SimWorld 打一片敌人，
    ///   哪几条规则真的触发、倍率在世界里兑现、单装一条固件不触发；
    /// D 机械内容可达性：44 条固件在正式内核里能挂上的标签位 → 每条反应 reach 列与实测一致，不可达的登记了缺口；
    /// E 正式版战斗内核（FGT-FW-003）：每条可达的标签反应用真实固件的武器在内核里实打——触发组（A 后 B、B 后 A、同一把武器两条固件）与不触发组（只 A、只 B），
    ///   反应额外伤害 = 这一击 × (倍率 − 1)、消耗 / 附加 / 残留真实发生，与“没有反应规则”的同一套开火比对掉血；
    /// F 负向：两条反应同时满足（只结算排在前面的一条，剩下的留给下一击）、叠层上限、消耗清掉叠层、持续伤害按层数；
    /// G 头顶标签图标与悬停读数（FGR-FW-031）；H 命名与开放批次（FGR-FW-041 / 042）、地点报名字、题材审计；
    /// I 存读档（内核快照格式 4 往返、格式 3 兼容、坏值拒绝；战役开放批次真实存读档）；J 正式流程（破碎都市两台机器打出短路；倍速 / 暂停 / 观察 / 真实存读档一致）；K 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgReactionProbeSelfCheck
    {
        private const int Slot = 0;
        private const int RunSlot = 1;
        private const int UplinkSlot = 2;
        private const string BpWet = "bp_fgfw03_wet";
        private const string BpShock = "bp_fgfw03_shock";

        /// <summary>FGR-FW-041 的机械名（设计文档表，反应 ID → 中文名）。</summary>
        private static readonly Dictionary<string, string> SpecNames = new Dictionary<string, string>
        {
            ["reaction_steam"] = "蒸汽", ["reaction_deflagrate"] = "爆燃", ["reaction_conduct"] = "短路", ["reaction_shatter"] = "脆裂",
            ["reaction_sticky"] = "粘滞", ["reaction_electrolysis"] = "电解", ["reaction_insulate"] = "绝缘", ["reaction_thermalshock"] = "热震",
            ["reaction_causticburn"] = "腐蚀灼烧", ["reaction_hemolysis"] = "回路溶断", ["reaction_syrup"] = "胶结", ["reaction_dissolve"] = "溶蚀",
            ["reaction_annihilate"] = "湮灭", ["reaction_vitrify"] = "玻化", ["reaction_mudify"] = "淤塞", ["reaction_sepsis"] = "连锁锈蚀",
            ["reaction_marktag"] = "标记跳转", ["reaction_meltoverload"] = "熔穿过载",
        };

        /// <summary>FGR-FW-042 暂定分配（按探针结果调整：本 Story 探针未推翻任何一条的分配）。</summary>
        private static readonly Dictionary<string, string[]> SpecBatches = new Dictionary<string, string[]>
        {
            ["act1"] = new[] { "reaction_marktag", "reaction_meltoverload", "reaction_conduct", "reaction_thermalshock" },
            ["clarity"] = new[] { "reaction_deflagrate", "reaction_causticburn", "reaction_syrup", "reaction_dissolve" },
            ["overclock"] = new[] { "reaction_electrolysis", "reaction_vitrify", "reaction_hemolysis", "reaction_sepsis" },
            ["cross"] = new[] { "reaction_steam", "reaction_shatter", "reaction_sticky", "reaction_insulate", "reaction_annihilate", "reaction_mudify" },
        };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();
        private static readonly Dictionary<string, LegacyReactionProbe.RuleProbe> RuleProbes = new Dictionary<string, LegacyReactionProbe.RuleProbe>();

        [MenuItem("BinGames/自检：FG 反应行为探针与命名")]
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
            PerfLines.Clear();
            RuleProbes.Clear();
            Line("\n[反应探针] 反应行为探针与命名（FG2-FW-03）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgfw03-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.ResetForTests();
                StatusTagCatalog.Reload();
                CarrierReadings.Reload();
                NamedReactionCatalog.ResetForTests();
                FirmwareCatalog.Invalidate();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "战斗内核是 AOT + Burst，热更层在 Editor 下是 Mono JIT、真机走 HybridCLR 解释执行（热更侧数字只作量级参考，真机复测归 FG15-SYS-02）");

                Step(CheckSource);
                Step(CheckLegacyRules);
                Step(CheckLegacyContent);
                Step(CheckReachability);
                Step(CheckKernelProbes);
                Step(CheckConflictsAndStacks);
                Step(CheckIconsAndHover);
                Step(CheckNamingAndBatches);
                Step(CheckSaveLoad);
                Step(CheckWorldPipeline);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"反应探针自检抛异常：{e}");
            }
            finally
            {
                WorldSimulation.UnloadAll();
                FirmwareKinds.ResetForTests();
                CarrierReadings.ResetForTests();
                NamedReactionCatalog.ResetForTests();
                FirmwareCatalog.Invalidate();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                GameSettings.SetLanguage(originalLanguage);
                CampaignSession.Set(originalSlot, originalSession);
                try
                {
                    if (Directory.Exists(_dir))
                    {
                        Directory.Delete(_dir, true);
                    }
                }
                catch (IOException)
                {
                }
            }
            Line($"  [反应探针] 断言 {_pass} 过 / {_fail} 败");
            return _fail;
        }

        // ── A. 源数据 = 运行时表 ─────────────────────────────────────────────────

        private static void CheckSource()
        {
            Line("  · A. 源数据 fgdata_reaction.py 与运行时 fg.TbReaction 逐字段一致；16 条标签反应 + 2 条装配反应");
            (int code, string output) = RunPython(LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            var rx = output.Replace("\r", string.Empty).Split('\n').Select(l => l.Split('\t')).Where(f => f[0] == "RX").ToList();
            var diffs = new List<string>();
            foreach (string[] f in rx)
            {
                Reaction r = NamedReactionCatalog.Rows.FirstOrDefault(x => x.Id == f[1]);
                string[] mine = r == null ? null : new[]
                {
                    r.Id, r.LegacyName, r.LegacyRules, r.Kind, r.NameKey, r.DescKey, r.TagA, r.TagB, r.Priority.ToString(CultureInfo.InvariantCulture),
                    F(r.DamageMult), r.Consume, r.Grant, r.LegacyResidue, r.Residue, r.ResidueKey, F(r.ResidueSeconds), F(r.ResidueRadius), r.Batch, r.Reach, r.Gap,
                };
                Compare(f, mine, diffs);
            }
            int tags = NamedReactionCatalog.Rows.Count(r => r.Kind == NamedReactionCatalog.KindTag);
            int asm = NamedReactionCatalog.Rows.Count(r => r.Kind == NamedReactionCatalog.KindAssembly);
            Expect(code == 0 && rx.Count > 0 && rx.Count == NamedReactionCatalog.Rows.Count && diffs.Count == 0 && NamedReactionCatalog.LoadError == null && tags == 16 && asm == 2,
                $"fg.TbReaction {NamedReactionCatalog.Rows.Count} 行与源 {rx.Count} 行逐字段一致（标签反应 {tags}、装配反应 {asm}）{Detail(diffs)}");
            Expect(NamedReactionCatalog.TagRules.Select((r, i) => (r, i)).All(x => x.i == 0 || NamedReactionCatalog.TagRules[x.i - 1].Priority < x.r.Priority)
                   && NamedReactionCatalog.BuildKernelRules().Length == tags,
                $"标签反应按 priority 严格递增排成内核规则（{string.Join(" → ", NamedReactionCatalog.TagRules.Select(r => r.LegacyName + r.Priority))}）");
        }

        // ── B. 旧引擎规则层 ─────────────────────────────────────────────────────

        private static void CheckLegacyRules()
        {
            Line("  · B. FGR-FW-040 旧引擎规则层：运行时注册表（内置 + 环境，与 MetabolicSliceBridge 同一套）的每条规则，只注册它自己，" +
                 "同一发基础事件贴“两个配料 / 只贴第一个 / 只贴第二个”，用插桩记录规则是否真被引擎调用");
            IReadOnlyList<ReactionRule> rules = LegacyReactionProbe.RuntimeRules();
            string[] ids = rules.Select(r => r.Id).ToArray();
            string[] covered = NamedReactionCatalog.Rows.Where(r => r.Kind == NamedReactionCatalog.KindTag)
                .SelectMany(r => r.LegacyRules.Split(';')).ToArray();
            Expect(ids.Length == 21 && ids.OrderBy(x => x).SequenceEqual(covered.OrderBy(x => x)),
                $"运行时注册表 {ids.Length} 条规则 = 反应表 legacyRules 覆盖的 {covered.Length} 条（每条只归一行）");
            var bad = new List<string>();
            var ratios = new List<string>();
            foreach (ReactionRule rule in rules)
            {
                LegacyReactionProbe.RuleProbe p = LegacyReactionProbe.ProbeRule(rule);
                RuleProbes[rule.Id] = p;
                Reaction row = RowOfRule(rule.Id);
                if (!p.FiresWithBoth)
                {
                    bad.Add(rule.Id + " 两个配料齐了却不触发");
                }
                if (p.FiresWithOnlyFirst || p.FiresWithOnlySecond)
                {
                    bad.Add(rule.Id + " 只有一个配料也触发");
                }
                if (row == null)
                {
                    bad.Add(rule.Id + " 没归入反应表");
                    continue;
                }
                bool env = rule.Id.StartsWith("env_", StringComparison.Ordinal);
                if (!env && p.ReactionName != row.LegacyName)
                {
                    bad.Add($"{rule.Id} 报的名字 {p.ReactionName} ≠ 表 {row.LegacyName}");
                }
                string[] canon = rule.RequiredTags.Select(StatusTagCatalog.Canonical).OrderBy(t => t, StringComparer.Ordinal).ToArray();
                string[] rowPair = new[] { row.TagA, row.TagB }.OrderBy(t => t, StringComparer.Ordinal).ToArray();
                if (!canon.SequenceEqual(rowPair))
                {
                    bad.Add($"{rule.Id} 配料 {string.Join("+", canon)} ≠ 表 {string.Join("+", rowPair)}");
                }
                ratios.Add($"{rule.Id}×{p.DamageRatio:0.##}{(p.FiresWhenTargetCarries ? "" : "(目标带配料不触发)")}");
            }
            Expect(bad.Count == 0, $"{rules.Count} 条规则全部“两个配料触发、只有一个不触发”，名字与配对 = 反应表（{string.Join("，", ratios)}）{Detail(bad)}");
            bool carried = RuleProbes.Values.All(p => p.FiresWhenTargetCarries && p.FiresWhenTargetCarriesBoth);
            bool physicalInherent = RuleProbes.TryGetValue("frozen_physical_to_shatter", out var sh) && sh.Inherent.Contains("Physical");
            Expect(carried && physicalInherent,
                "旧引擎语义实测：配料一个在这一发、一个在目标格上同样触发（两个都在目标格上也触发）；旧引擎每一发都自带“动能 Physical”（脆裂在旧引擎里见冰就碎）");
        }

        // ── C. 旧引擎内容层 + 世界层 ──────────────────────────────────────────────

        private static void CheckLegacyContent()
        {
            Line("  · C. 内容层 + 世界层：真实固件的旧基因装在真实器官上编译（完整注册表 vs 空注册表），各自交给真起的 SimWorld 打一片静止敌人（两拍）；" +
                 "表里的伤害倍率 = 这对配料真正触发的规则的倍率之积，消耗 / 附加 / 残留 = 这些规则实测的；单装一条固件不触发");
            var bad = new List<string>();
            var lines = new List<string>();
            var unrealized = new List<string>();
            foreach (Reaction row in NamedReactionCatalog.TagRules)
            {
                string[] pair = { row.TagA, row.TagB };
                List<(string Organ, string[] Genes, string Note)> cands = FgReactionProbeDump.Candidates(pair);
                string[] rowRules = row.LegacyRules.Split(';');
                LegacyReactionProbe.CompiledProbe best = null;
                string bestNote = null;
                foreach ((string organ, string[] genes, string note) in cands)
                {
                    LegacyReactionProbe.CompiledProbe c = LegacyReactionProbe.ProbeCompiled(organ, genes);
                    if (!c.Compiled || !c.FiredRules.Any(rowRules.Contains) || c.WorldLossWithout <= 1f)
                    {
                        continue;
                    }
                    if (Math.Abs(c.WorldRatio - c.DamageRatio) > 0.06f)
                    {
                        // 编译层触发了、世界里没兑现（例如弧线弹在这片靶子里几乎打不中，掉血来自不吃倍率的拖尾）：记下来，换下一组生产者。
                        unrealized.Add($"{row.LegacyName}[{note}] 编译×{c.DamageRatio:0.##} 世界×{c.WorldRatio:0.##}");
                        continue;
                    }
                    best = c;
                    bestNote = note;
                    break;
                }
                if (best == null)
                {
                    bad.Add($"{row.LegacyName}：{cands.Count} 组生产者都没有在旧引擎里打出这条反应");
                    continue;
                }
                string[] effective = best.FiredRules.Where(rowRules.Contains).ToArray();
                float expect = effective.Aggregate(1f, (m, id) => m * RuleProbes[id].DamageRatio);
                string[] consume = CanonStatus(effective.SelectMany(id => RuleProbes[id].Removed));
                string[] grant = CanonStatus(effective.SelectMany(id => RuleProbes[id].Added));
                string[] residue = effective.SelectMany(id => RuleProbes[id].Residue).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (Math.Abs(expect - row.DamageMult) > 1e-3f)
                {
                    bad.Add($"{row.LegacyName} 倍率 探针 {expect:0.###} ≠ 表 {row.DamageMult:0.###}");
                }
                if (!consume.SequenceEqual(Split(row.Consume)))
                {
                    bad.Add($"{row.LegacyName} 消耗 探针 {string.Join(";", consume)} ≠ 表 {row.Consume}");
                }
                if (!grant.SequenceEqual(Split(row.Grant)))
                {
                    bad.Add($"{row.LegacyName} 附加 探针 {string.Join(";", grant)} ≠ 表 {row.Grant}");
                }
                if (!residue.SequenceEqual(Split(row.LegacyResidue)))
                {
                    bad.Add($"{row.LegacyName} 残留 探针 {string.Join(";", residue)} ≠ 表 {row.LegacyResidue}");
                }
                // 世界层：倍率在世界里兑现（掉血之比 ≈ 编译层之比；倍率 1 的反应掉血相同）。
                if (Math.Abs(best.WorldRatio - best.DamageRatio) > 0.06f)
                {
                    bad.Add($"{row.LegacyName} 世界掉血之比 {best.WorldRatio:0.###} 与编译倍率 {best.DamageRatio:0.###} 对不上");
                }
                // 不触发组：两条固件各自单装，这一行的规则都不触发、世界掉血与空注册表相同（倍率 1）。
                foreach (string gene in best.Genes)
                {
                    LegacyReactionProbe.CompiledProbe solo = LegacyReactionProbe.ProbeCompiled(best.Organ, new[] { gene });
                    bool inherentOnly = row.TagB == "Physical" || row.TagA == "Physical";
                    float others = solo.FiredRules.Aggregate(1f, (m, id) => m * RuleProbes[id].DamageRatio);
                    if (!inherentOnly && (!solo.Compiled || solo.FiredRules.Any(rowRules.Contains) || Math.Abs(solo.WorldRatio - others) > 0.06f))
                    {
                        bad.Add($"{row.LegacyName} 单装 {gene}：触发了 {string.Join(",", solo.FiredRules)}，世界之比 {solo.WorldRatio:0.###}（其余规则之积 {others:0.###}）");
                    }
                }
                string extra = best.FiredRules.Where(r => !rowRules.Contains(r)).Any() ? $"，同批还触发 {string.Join("/", best.FiredRules.Where(r => !rowRules.Contains(r)))}" : string.Empty;
                lines.Add($"{row.LegacyName}[{bestNote}]×{best.DamageRatio:0.##}/世界×{best.WorldRatio:0.##}{extra}");
            }
            Expect(bad.Count == 0 && lines.Count == 16, $"16 条具名反应在旧引擎内容层 + 世界层实测 = 反应表（{string.Join("；", lines)}）{Detail(bad)}");
            if (unrealized.Count > 0)
            {
                Line("    · 记录：编译层触发但世界里没兑现、已换生产者的组合：" + string.Join("；", unrealized));
            }
        }

        // ── D. 机械内容可达性 ───────────────────────────────────────────────────

        private static void CheckReachability()
        {
            Line("  · D. 机械内容可达性：44 条固件装在连射器上（信号核接入口，核心固件也算）在正式内核里能挂上的状态位；每条反应两个配料都挂得上 = reachable，否则 unreachable 并登记缺口");
            NewState(9301, unlockAll: true);
            uint reach = 0u;
            foreach (GameConfig.fg.FirmwareKind fw in FirmwareKinds.Rows)
            {
                reach |= WeaponFor(ComponentCatalog.CompGunId, fw.Id).Reading.StatusMask;
            }
            string gapText = File.ReadAllText(Path.Combine(LocateRepo(), "production", "design", "full-game", "FG-GAP-REGISTER.md"));
            var bad = new List<string>();
            var unreachable = new List<string>();
            foreach (Reaction row in NamedReactionCatalog.TagRules)
            {
                uint a = NamedReactionCatalog.BitOf(row.TagA);
                uint b = NamedReactionCatalog.BitOf(row.TagB);
                bool can = (reach & a) != 0u && (reach & b) != 0u;
                if ((row.Reach == NamedReactionCatalog.Reachable) != can)
                {
                    bad.Add($"{row.LegacyName} 表 {row.Reach}，实测 {(can ? "可达" : "不可达")}");
                }
                if (!can)
                {
                    unreachable.Add(row.LegacyName);
                    if (row.Gap == "none" || !gapText.Contains("| " + row.Gap + " |"))
                    {
                        bad.Add($"{row.LegacyName} 不可达但缺口 {row.Gap} 没登记在 FG-GAP-REGISTER.md");
                    }
                }
            }
            uint physical = NamedReactionCatalog.BitOf("Physical");
            Expect(bad.Count == 0 && unreachable.SequenceEqual(new[] { "Shatter" }) && physical != 0u && (reach & physical) == 0u,
                $"44 条固件能挂上的状态位 {CountBits(reach)} 个；不可达的只有脆裂（配料“动能”没有任何固件会挂），已登记缺口{Detail(bad)}");
        }

        // ── E. 正式版战斗内核：每条反应触发 / 不触发 ──────────────────────────────

        private static void CheckKernelProbes()
        {
            Line("  · E. FGT-FW-003 正式版战斗内核：每条可达的标签反应用真实固件的武器（连射器 + 信号核接入口）实打：触发组（A 后 B、B 后 A、同一把武器装两条）与" +
                 "不触发组（只 A 打两次、只 B 打两次）；反应额外伤害 = 这一击 × (倍率 − 1)，掉血与“没登记反应规则”的同一套开火相比真的多 / 少；消耗、附加、残留真实发生");
            NewState(9302, unlockAll: true);
            var bad = new List<string>();
            var lines = new List<string>();
            IReadOnlyList<Reaction> rules = NamedReactionCatalog.TagRules;
            for (int ri = 0; ri < rules.Count; ri++)
            {
                Reaction row = rules[ri];
                if (row.Reach != NamedReactionCatalog.Reachable)
                {
                    continue;
                }
                string fa = PickProducer(row.TagA, row.TagB);
                string fb = PickProducer(row.TagB, row.TagA);
                if (fa == null || fb == null)
                {
                    bad.Add($"{row.LegacyName} 找不到配料的固件");
                    continue;
                }
                CombatWeapon wa = WeaponFor(ComponentCatalog.CompGunId, fa);
                CombatWeapon wb = WeaponFor(ComponentCatalog.CompGunId, fb);
                CombatWeapon wab = WeaponFor(ComponentCatalog.CompGunId, fa, fb);

                // B 单独打在干净目标上：这一击的伤害（没有易伤、没有反应）。
                float baseB = Hits(new[] { wb }, new[] { 0 }, true, out _, out _, out _);
                // 触发：A 后 B。
                float withRx = Hits(new[] { wa, wb }, new[] { 0, 1 }, true, out int countAB, out float bonusAB, out KernelAfter after);
                float noRx = Hits(new[] { wa, wb }, new[] { 0, 1 }, false, out _, out _, out KernelAfter afterNo);
                float withRxBA = Hits(new[] { wb, wa }, new[] { 0, 1 }, true, out int countBA, out _, out _);
                Hits(new[] { wab }, new[] { 0 }, true, out int countSame, out _, out _);
                // 不触发：只 A / 只 B 各打两次。
                Hits(new[] { wa }, new[] { 0, 0 }, true, out int countAA, out _, out _);
                Hits(new[] { wb }, new[] { 0, 0 }, true, out int countBB, out _, out _);

                float expectBonus = baseB * (row.DamageMult - 1f);
                bool triggers = countAB == 1 && countBA == 1 && countSame == 1;
                bool quiet = countAA == 0 && countBB == 0;
                bool bonusOk = Math.Abs(bonusAB - expectBonus) <= Math.Max(0.02f, Math.Abs(expectBonus) * 0.01f);
                float diff = withRx - noRx;
                bool realized = row.DamageMult > 1f ? diff >= expectBonus * 0.99f - 0.01f
                    : row.DamageMult < 1f ? diff < -0.01f
                    : Math.Abs(diff) <= 0.01f;
                uint consume = NamedReactionCatalog.BitsOf(row.Consume);
                uint grant = NamedReactionCatalog.BitsOf(row.Grant);
                bool consumed = (after.Mask & consume & ~grant) == 0u;
                bool granted = (after.Mask & grant) == grant;
                int residueZones = after.Zones - afterNo.Zones;
                bool residue = row.Residue == "none" ? residueZones == 0 : residueZones == 1;
                if (!(triggers && quiet && bonusOk && realized && consumed && granted && residue))
                {
                    bad.Add($"{row.LegacyName}（{fa}+{fb}）触发 {countAB}/{countBA}/{countSame} 不触发 {countAA}/{countBB} 额外伤害 {bonusAB:0.##}（应 {expectBonus:0.##}）" +
                            $" 掉血差 {diff:0.##} 消耗{(consumed ? "✓" : "✗")} 附加{(granted ? "✓" : "✗")} 残留{(residue ? "✓" : "✗")}");
                }
                lines.Add($"{row.LegacyName}:{fa}+{fb} Δ{diff:0.#}");
            }
            Expect(bad.Count == 0 && lines.Count == 15, $"15 条可达反应在正式内核里触发 / 不触发两组对照全部成立（{string.Join("；", lines)}）{Detail(bad)}");
        }

        // ── F. 负向：两条同时满足、叠层上限 ───────────────────────────────────────

        private static void CheckConflictsAndStacks()
        {
            Line("  · F. 负向：两条反应同时满足（浸湿 + 油污的目标挨一发燃烧：只结算蒸汽，油污留给下一发的爆燃）；叠层上限；消耗清掉叠层；持续伤害按层数；" +
                 "区域节拍触发的反应留下残留区（对照：目标无配料）；没有伤害的挂状态不结算只靠倍率的反应；残留区阵营按目标定；持续伤害逐位叠层");
            NewState(9303, unlockAll: true);
            CombatWeapon wet = WeaponFor(ComponentCatalog.CompGunId, "fw_coolant");
            CombatWeapon oil = WeaponFor(ComponentCatalog.CompGunId, "fw_oilleak");
            CombatWeapon fire = WeaponFor(ComponentCatalog.CompGunId, "fw_burntrail");
            int steam = RuleIndex("reaction_steam");
            int defl = RuleIndex("reaction_deflagrate");
            using (var arena = new ProbeArena(true))
            {
                int mW = arena.Machine(wet);
                int mO = arena.Machine(oil);
                int mF = arena.Machine(fire);
                int e = arena.Enemy();
                arena.Fire(mW, e);
                arena.Fire(mO, e);
                int beforeFire = arena.K.ReactionCountOf(steam) + arena.K.ReactionCountOf(defl);
                arena.Fire(mF, e);
                int s1 = arena.K.ReactionCountOf(steam);
                int d1 = arena.K.ReactionCountOf(defl);
                bool oilLeft = arena.HasTag(e, "Oil");
                bool wetGone = !arena.HasTag(e, "Wet");
                arena.Fire(mF, e);
                int d2 = arena.K.ReactionCountOf(defl);
                Expect(beforeFire == 0 && s1 == 1 && d1 == 0 && oilLeft && wetGone && d2 == 1,
                    $"浸湿 + 油污（两者之间不反应：{beforeFire} 次）→ 燃烧一发：蒸汽 {s1} 次、爆燃 {d1} 次，浸湿被消耗、油污还在；再一发燃烧 → 爆燃 {d2} 次（按 priority 只结算一条）");
            }
            using (var arena = new ProbeArena(true))
            {
                int mF = arena.Machine(fire);
                int e = arena.Enemy();
                int fireBit = Bit("Fire");
                var stacks = new List<int>();
                for (int i = 0; i < 5; i++)
                {
                    arena.Fire(mF, e);
                    stacks.Add(arena.K.StatusStacksOf(e, fireBit));
                }
                arena.K.TryGetStatus(e, out _, out _, out float dps, out _, out _);
                float fireDps = StatusTagCatalog.TryGet("Fire", out StatusTag ft) ? ft.Amount : 0f;
                int cap = arena.K.Config.StatusStackCap;
                Expect(cap == 3 && stacks.SequenceEqual(new[] { 1, 2, 3, 3, 3 }) && Math.Abs(dps - fireDps * 3f) < 1e-3f,
                    $"同一标签连挂 5 次：层数 {string.Join("→", stacks)}（上限 status.stack_cap = {cap}）；持续伤害 = 燃烧 {fireDps}/秒 × 3 层 = {dps:0.##}/秒");
                // 消耗清掉叠层：浸湿 3 层后被燃烧反应（蒸汽）消耗。
                int mW = arena.Machine(wet);
                int e2 = arena.Enemy();
                for (int i = 0; i < 3; i++)
                {
                    arena.Fire(mW, e2);
                }
                int wetBit = Bit("Wet");
                int wetStacks = arena.K.StatusStacksOf(e2, wetBit);
                arena.Fire(mF, e2);
                Expect(wetStacks == 3 && arena.K.StatusStacksOf(e2, wetBit) == 0 && arena.K.ReactionCountOf(steam) == 1,
                    $"浸湿叠到 {wetStacks} 层后被蒸汽一次消耗干净（剩 {arena.K.StatusStacksOf(e2, wetBit)} 层，只触发 1 次反应，不按层数重复触发）");
            }
            using (var arena = new ProbeArena(true, stackCap: 0))
            {
                int mF = arena.Machine(fire);
                int e = arena.Enemy();
                arena.Fire(mF, e);
                arena.Fire(mF, e);
                Expect(arena.K.StatusStacksOf(e, Bit("Fire")) == 1, "内核没配叠层上限（0）时按 1 层：旧地点 / 测试内核行为不变");
            }
            // 叠层到期：统一时钟推进到状态到期后，标签与叠层一起清空。
            using (var arena = new ProbeArena(true))
            {
                int e = arena.Enemy();
                uint fireBit = NamedReactionCatalog.BitOf("Fire");
                arena.K.ApplyStatus(e, fireBit, 3f, 4f, 0f, 0f, 0);
                arena.K.ApplyStatus(e, fireBit, 3f, 4f, 0f, 0f, 0);
                int two = arena.K.StatusStacksOf(e, Bit("Fire"));
                arena.Run(4f);
                Expect(two == 2 && arena.K.StatusStacksOf(e, Bit("Fire")) == 0 && !arena.HasTag(e, "Fire"), "状态到期（3 游戏秒）后标签与叠层一起清空（到期前 2 层）");
            }
            // 残留区域（爆燃 → 燃烧区）：挨着目标站着的另一个敌人没被打，区域节拍后被挂上燃烧并掉血；没有反应规则时没有这块区域。
            using (var withRx = new ProbeArena(true))
            using (var noRx = new ProbeArena(false))
            {
                var results = new List<(bool Burning, float Loss, int Zones)>();
                CombatWeapon spark = WeaponFor(ComponentCatalog.CompGunId, "fw_overload"); // 燃烧、自己不留区域
                foreach (ProbeArena arena in new[] { withRx, noRx })
                {
                    int mO = arena.Machine(oil);
                    int mF = arena.Machine(spark);
                    int e = arena.Enemy();
                    int bystander = arena.Enemy(new double2(3.9, 0.9));
                    int zones0 = arena.K.ZoneCount;
                    arena.Fire(mO, e);
                    arena.Fire(mF, e);
                    int zones1 = arena.K.ZoneCount;
                    arena.K.TryGetUnit(bystander, out CombatUnitView b0);
                    arena.Run(1.2f);
                    arena.K.TryGetUnit(bystander, out CombatUnitView b1);
                    results.Add((arena.HasTag(bystander, "Fire"), b0.Health - b1.Health, zones1 - zones0));
                }
                Expect(results[0].Burning && results[0].Loss > 0f && results[0].Zones == results[1].Zones + 1 && !results[1].Burning,
                    $"爆燃留下燃烧区：旁边没挨打的敌人 1.2 游戏秒内被挂上燃烧、掉血 {results[0].Loss:0.#}（没有反应规则时不挂燃烧、区域少 1 块）");
            }
            CheckZoneTickReaction();
            CheckZeroHitAndFaction();
            CheckDotStackingConsistency();
        }

        /// <summary>修复轮 P1：区域节拍触发的反应（带油污的敌人站进爆燃残留区 → 节拍挂上燃烧 → 爆燃）留下的残留区域真的存在
        /// （区域数与“生成区域”计数一致，旁边只在新区域里的敌人被挂上燃烧）；对照组：同一布局、目标没有油污 → 不反应、不多区域。</summary>
        private static void CheckZoneTickReaction()
        {
            CombatWeapon spark = WeaponFor(ComponentCatalog.CompGunId, "fw_overload"); // 燃烧、自己不留区域
            int defl = RuleIndex("reaction_deflagrate");
            uint oilBit = NamedReactionCatalog.BitOf("Oil");
            var rows = new List<(int Zones, long Spawned, int Defl, bool OilGone, bool ByBurning, float Loss)>();
            foreach (bool oiled in new[] { true, false })
            {
                using (var arena = new ProbeArena(true))
                {
                    int mF = arena.Machine(spark);
                    int e0 = arena.Enemy(new double2(3.0, 0.2));  // 被直接打出爆燃的敌人 → 残留区 Z1（半径 2 米）
                    int e = arena.Enemy(new double2(4.5, 0.2));   // 站在 Z1 里、没挨打；有油污时被 Z1 的节拍点燃 → 爆燃 → 残留区 Z2
                    int by = arena.Enemy(new double2(6.0, 0.2));  // 在 Z1 外（3.0 米）、在 Z2 里（1.5 米）
                    arena.K.ApplyStatus(e0, oilBit, 5f, 0f, 0f, 0f, 0);
                    if (oiled)
                    {
                        arena.K.ApplyStatus(e, oilBit, 5f, 0f, 0f, 0f, 0);
                    }
                    long spawned0 = arena.K.Counters.ZonesSpawned;
                    arena.Fire(mF, e0);
                    arena.K.TryGetUnit(e, out CombatUnitView e1v);
                    arena.Run(1.6f);
                    arena.K.TryGetUnit(e, out CombatUnitView e2v);
                    rows.Add((arena.K.ZoneCount, arena.K.Counters.ZonesSpawned - spawned0, arena.K.ReactionCountOf(defl), !arena.HasTag(e, "Oil"),
                        arena.HasTag(by, "Fire"), e1v.Health - e2v.Health));
                }
            }
            (int Zones, long Spawned, int Defl, bool OilGone, bool ByBurning, float Loss) hit = rows[0], ctl = rows[1];
            Expect(hit.Zones == 2 && hit.Spawned == 2 && hit.Defl == 2 && hit.OilGone && hit.ByBurning && hit.Loss > 0f
                   && ctl.Zones == 1 && ctl.Spawned == 1 && ctl.Defl == 1 && !ctl.ByBurning,
                $"区域节拍触发的爆燃：残留区真的留下（区域 {hit.Zones} 块 = 生成计数 {hit.Spawned}，爆燃 {hit.Defl} 次，油污被消耗 {hit.OilGone}，" +
                $"只在新区域里的敌人被点燃 {hit.ByBurning}，目标掉血 {hit.Loss:0.#}）；对照（目标无油污）：区域 {ctl.Zones} 块、爆燃 {ctl.Defl} 次、旁边的敌人没被点燃");
        }

        /// <summary>修复轮 P2：没有伤害的挂状态（hit = 0）只结算带附加标签 / 残留区域的反应；残留区域的阵营按目标定（出手者无效时不会打到自己人）。</summary>
        private static void CheckZeroHitAndFaction()
        {
            CombatWeapon wet = WeaponFor(ComponentCatalog.CompGunId, "fw_coolant");
            CombatWeapon shock = WeaponFor(ComponentCatalog.CompGunId, "fw_arcchain");
            int conduct = RuleIndex("reaction_conduct");
            int defl = RuleIndex("reaction_deflagrate");
            using (var arena = new ProbeArena(true))
            {
                int mW = arena.Machine(wet);
                int mS = arena.Machine(shock);
                int quiet = arena.Enemy(new double2(3.0, 40.0)); // 远离挨打的那个：电弧链跳不过来
                int shot = arena.Enemy(new double2(3.0, 4.0));
                arena.K.ApplyStatus(quiet, NamedReactionCatalog.BitOf("Wet"), 3f, 0f, 0f, 0f, 0);
                arena.K.ApplyStatus(quiet, NamedReactionCatalog.BitOf("Shock"), 3f, 0f, 0f, 0f, 0);
                int zeroHit = arena.K.ReactionCountOf(conduct);
                bool bothKept = arena.HasTag(quiet, "Wet") && arena.HasTag(quiet, "Shock");
                arena.Fire(mW, shot);
                arena.Fire(mS, shot);
                int realHit = arena.K.ReactionCountOf(conduct) - zeroHit;
                Expect(zeroHit == 0 && bothKept && realHit == 1,
                    $"没有伤害的挂状态（浸湿 + 电击，hit = 0）：短路只靠倍率，不结算、不消耗、不报名（{zeroHit} 次，两个标签都在 {bothKept}）；对照：真实命中打出短路 {realHit} 次");
            }
            using (var arena = new ProbeArena(true))
            {
                // 己方机器被挂上油污 + 燃烧（出手者无效 = 0，hit = 0）：爆燃带残留区，照样结算；残留区属于敌方、打己方——旁边的机器被点燃，旁边的敌人不被点燃。
                int m1 = arena.Machine(wet);
                int m2 = arena.Machine(wet);
                int foe = arena.Enemy(new double2(1.0, 0.0));
                uint fireBit = NamedReactionCatalog.BitOf("Fire");
                float fireDps = StatusTagCatalog.TryGet("Fire", out StatusTag ft) ? ft.Amount : 0f;
                arena.K.ApplyStatus(m1, NamedReactionCatalog.BitOf("Oil"), 5f, 0f, 0f, 0f, 0);
                arena.K.ApplyStatus(m1, fireBit, 3f, fireDps, 0f, 0f, 0);
                int d1 = arena.K.ReactionCountOf(defl);
                int zones = arena.K.ZoneCount;
                arena.Run(1.2f);
                bool allyBurning = arena.HasTag(m2, "Fire");
                bool foeBurning = arena.HasTag(foe, "Fire");
                Expect(d1 == 1 && zones == 1 && allyBurning && !foeBurning,
                    $"出手者无效时的残留区阵营按目标定：己方机器身上的爆燃（{d1} 次，区域 {zones} 块）点燃旁边的己方机器 {allyBurning}，不点燃旁边的敌人 {!foeBurning}");
            }
        }

        /// <summary>修复轮 P2：持续伤害叠层在挂状态与消耗后重算两处同一算法（逐位 该标签每秒伤害 × 该位层数，取大）。</summary>
        private static void CheckDotStackingConsistency()
        {
            using (var arena = new ProbeArena(true))
            {
                // 找两个不互相反应、每秒伤害不同的持续伤害标签。
                int hi = -1, lo = -1;
                for (int a = 0; a < 31 && hi < 0; a++)
                {
                    CombatStatusFx fa = arena.K.StatusFxOf(a);
                    if (fa.Effect != CombatStatusFx.Dot || fa.Amount <= 0f)
                    {
                        continue;
                    }
                    for (int b = 0; b < 31; b++)
                    {
                        CombatStatusFx fb = arena.K.StatusFxOf(b);
                        if (b == a || fb.Effect != CombatStatusFx.Dot || fb.Amount <= 0f || fb.Amount >= fa.Amount || ReactsTogether(arena.K, a, b))
                        {
                            continue;
                        }
                        hi = a;
                        lo = b;
                        break;
                    }
                }
                if (hi < 0)
                {
                    Fail("测试准备：状态位表里找不到两个互不反应、每秒伤害不同的持续伤害标签");
                    return;
                }
                float hiDps = arena.K.StatusFxOf(hi).Amount;
                float loDps = arena.K.StatusFxOf(lo).Amount;
                int e = arena.Enemy();
                arena.K.ApplyStatus(e, 1u << lo, 5f, loDps, 0f, 0f, 0);
                arena.K.ApplyStatus(e, 1u << lo, 5f, loDps, 0f, 0f, 0);
                // 同一次带来高伤害标签（1 层）+ 低伤害标签（第 3 层）。
                arena.K.ApplyStatus(e, (1u << hi) | (1u << lo), 5f, hiDps, 0f, 0f, 0);
                arena.K.TryGetStatus(e, out _, out _, out float dps, out _, out _);
                float expect = Math.Max(hiDps, loDps * 3f);
                Expect(Math.Abs(dps - expect) < 1e-3f && Math.Abs(dps - hiDps * 3f) > 1e-3f,
                    $"持续伤害逐位算：高 {hiDps}/秒 × 1 层 与 低 {loDps}/秒 × 3 层取大 = {dps:0.##}/秒（期望 {expect:0.##}；旧算法会得出 {hiDps * 3f:0.##}）");
            }
        }

        private static bool ReactsTogether(CombatKernel k, int a, int b)
        {
            uint pair = (1u << a) | (1u << b);
            for (int i = 0; i < k.ReactionRuleCount; i++)
            {
                if ((k.ReactionRule(i).Pair & pair) != 0u && (k.ReactionRule(i).Pair | pair) == pair)
                {
                    return true;
                }
            }
            return false;
        }

        // ── G. 头顶标签图标与悬停读数 ────────────────────────────────────────────

        private static void CheckIconsAndHover()
        {
            Line("  · G. FGR-FW-031：敌人和己方单位头顶的标签图标（内核实例化绘制；形状为主、颜色为辅；底部小点 = 叠层）与悬停读数（名称、剩余时间、叠层）");
            NewState(9304, unlockAll: true);
            float2[] visuals = NamedReactionCatalog.BuildStatusVisuals();
            var badVisual = new List<string>();
            var seen = new HashSet<(int, uint)>();
            foreach (StatusTag row in StatusTagCatalog.Rows.Where(r => r.Kind == "status" && r.AliasOf == "none"))
            {
                float2 v = visuals[row.Bit];
                if (v.x < 0f || NamedReactionCatalog.Shapes[(int)v.x] != row.Shape || !NamedReactionCatalog.TryParseColor(row.Color, out uint rgb) || (uint)v.y != rgb)
                {
                    badVisual.Add(row.Id);
                }
                if (!seen.Add(((int)v.x, (uint)v.y)))
                {
                    badVisual.Add(row.Id + "（形状 + 颜色与别的标签重复）");
                }
            }
            Expect(badVisual.Count == 0 && seen.Count == 26, $"26 个主状态标签都有头顶图标：形状 = 表的字形、颜色 = 表的颜色，形状 + 颜色两两不同{Detail(badVisual)}");

            CombatWeapon plain = WeaponFor(ComponentCatalog.CompGunId);
            using (var arena = new ProbeArena(true))
            {
                int mF = arena.Machine(plain);
                int mX = arena.Machine(plain);
                int e1 = arena.Enemy();
                int e2 = arena.Enemy(new double2(3.0, 3.0));
                int bare = arena.Enemy(new double2(-3.0, 3.0));
                uint fireBit = NamedReactionCatalog.BitOf("Fire");
                arena.K.ApplyStatus(e1, fireBit, 3f, 4f, 0f, 0f, mF);
                arena.K.ApplyStatus(e1, fireBit, 3f, 4f, 0f, 0f, mF);
                // 5 个互不反应的标签（信号核接入口一台机器最多带 2 条固件，这里直接用内核挂状态的同一条规则）。
                uint five = NamedReactionCatalog.BitsOf("Oil;Frozen;SugarFilm;Charged;Magnet");
                arena.K.ApplyStatus(e2, five, 3f, 0f, 0f, 0f, mX);
                // 敌人反打己方：己方单位也显示标签（同一套状态）。
                CarrierReadings.TryGetTagEffect("Charged", out uint charged, out _, out _);
                arena.K.ApplyStatus(mF, charged, 3f, 0f, 0f, 0f, e2);
                var renderer = new CombatRenderer();
                try
                {
                    renderer.SetStatusVisuals(visuals);
                    renderer.Draw(arena.K, null, 1f, double2.zero, 0.6f);
                    CombatInstance[] icons = renderer.IconInstances.ToArray();
                    arena.K.TryGetUnit(e1, out CombatUnitView v1);
                    CombatInstance fireIcon = icons.FirstOrDefault(i => Math.Abs(i.A.x - (float)v1.Position.x) < 0.01f);
                    int e2Tags = CountBits(arena.Mask(e2));
                    int e2Icons = icons.Count(i => i.A.y > 2f && i.A.x > 1f);
                    bool fireIconOk = Math.Abs(fireIcon.B.y - NamedReactionCatalog.ShapeIndex("▲")) < 0.01f && (uint)fireIcon.B.z == 0xD55E00u
                                      && Math.Abs(fireIcon.B.w - 32f) < 0.01f && fireIcon.A.y > (float)v1.Position.y + 0.5f;
                    Expect(renderer.LastIconInstances == 1 + Math.Min(e2Tags, CombatRenderer.MaxIconsPerUnit) + 1 && fireIconOk && e2Tags > CombatRenderer.MaxIconsPerUnit
                           && e2Icons == CombatRenderer.MaxIconsPerUnit,
                        $"图标实例 {renderer.LastIconInstances} 个：燃烧 2 层的敌人头顶 1 个 ▲ 橙色、2 个叠层点、摆在头顶上方；挂了 {e2Tags} 个标签的敌人最多画 {e2Icons} 个；" +
                        "己方机器被挂“充能”也显示；没标签的不画");
                }
                finally
                {
                    renderer.Dispose();
                }
                List<(string Glyph, string Name, float Seconds, int Stacks)> d1 = StatusTagHover.Describe(arena.K, e1);
                TooltipProbe(arena, e1, e2, bare, d1);
            }
        }

        private static void TooltipProbe(ProbeArena arena, int e1, int e2, int bare, List<(string Glyph, string Name, float Seconds, int Stacks)> d1)
        {
            var t1 = StatusTagHover.BuildContent(arena.K, e1);
            var t2 = StatusTagHover.BuildContent(arena.K, e2);
            var t0 = StatusTagHover.BuildContent(arena.K, bare);
            arena.K.TryGetUnit(e1, out CombatUnitView v1);
            arena.K.TryGetUnit(bare, out CombatUnitView vb);
            int picked = arena.K.PickUnit(v1.Position + new double2(0.3, 0.2), StatusTagHover.PickRadius, true);
            int pickedBare = arena.K.PickUnit(vb.Position, StatusTagHover.PickRadius, true);
            int pickedAny = arena.K.PickUnit(vb.Position, StatusTagHover.PickRadius, false);
            Expect(d1 != null && d1.Count == 1 && d1[0].Name == "燃烧" && d1[0].Glyph == "▲" && d1[0].Stacks == 2 && d1[0].Seconds > 2.5f && d1[0].Seconds <= 3.01f
                   && t1 != null && t1.Title == GameText.Get("tag.hover.title") && t1.Body.Contains("燃烧") && t1.Body.Contains("×2")
                   && t2 != null && t2.Body.Contains(GameText.Format("tag.hover.more", (CountBits(arena.Mask(e2)) - CombatRenderer.MaxIconsPerUnit).ToString(CultureInfo.InvariantCulture)))
                   && t0 == null && picked == e1 && pickedBare == 0 && pickedAny == bare,
                $"悬停读数：{t1?.Body?.Replace('\n', '/')}；标签多于 4 个时写明“另有 N 个”；光标旁 0.36 米的带标签单位被拾取，没标签的单位不弹读数");
            arena.Run(1f);
            List<(string Glyph, string Name, float Seconds, int Stacks)> later = StatusTagHover.Describe(arena.K, e1);
            Expect(later != null && later.Count == 1 && Math.Abs((d1[0].Seconds - later[0].Seconds) - 1f) < 0.05f,
                $"剩余时间随游戏时间走：{d1[0].Seconds:0.00} → 1 游戏秒后 {later?.FirstOrDefault().Seconds:0.00}");
        }

        // ── H. 命名与开放批次 ────────────────────────────────────────────────────

        private static void CheckNamingAndBatches()
        {
            Line("  · H. FGR-FW-041 / 042：机械名 = 设计文档名表；开放批次 = 暂定分配；第一幕开局就开放，其余由阵营推进开放；未开放的照样触发、只是不报名字；题材审计");
            var badNames = new List<string>();
            foreach (KeyValuePair<string, string> kv in SpecNames)
            {
                string name = NamedReactionCatalog.NameOf(kv.Key);
                if (name != kv.Value)
                {
                    badNames.Add($"{kv.Key}={name}（应为 {kv.Value}）");
                }
            }
            bool mechOk = MechanicalReactionCatalog.All.All(kv => kv.Value.DisplayName == NamedReactionCatalog.NameOf(kv.Key));
            Expect(badNames.Count == 0 && mechOk, $"18 条反应的机械名 = FG02 3.5 名表（装配反应的目录名与表同名）{Detail(badNames)}");
            var badBatch = new List<string>();
            foreach (KeyValuePair<string, string[]> kv in SpecBatches)
            {
                string[] mine = NamedReactionCatalog.Rows.Where(r => r.Batch == kv.Key).Select(r => r.Id).OrderBy(x => x).ToArray();
                if (!mine.SequenceEqual(kv.Value.OrderBy(x => x)))
                {
                    badBatch.Add($"{kv.Key}: {string.Join(",", mine)}");
                }
            }
            Expect(badBatch.Count == 0, $"开放批次 = FGR-FW-042：第一幕 4 条（标记跳转、熔穿过载、短路、热震）、化工 4 条、超频 4 条、跨阵营 6 条{Detail(badBatch)}");

            CampaignState s = NewState(9305, unlockAll: true);
            bool act1 = NamedReactionCatalog.IsNamed(s, "reaction_conduct") && NamedReactionCatalog.IsNamed(s, "reaction_thermalshock") && NamedReactionCatalog.IsNamed(s, "reaction_marktag");
            bool lockedClarity = !NamedReactionCatalog.IsNamed(s, "reaction_deflagrate");
            bool opened = NamedReactionCatalog.OpenBatch(s, "clarity");
            bool twice = NamedReactionCatalog.OpenBatch(s, "clarity");
            bool bogus = NamedReactionCatalog.OpenBatch(s, "nope");
            bool nowNamed = NamedReactionCatalog.IsNamed(s, "reaction_deflagrate");
            NamedReactionCatalog.OpenBatch(s, "cross");
            bool shatterStill = !NamedReactionCatalog.IsNamed(s, "reaction_shatter");
            Expect(act1 && lockedClarity && opened && !twice && !bogus && nowNamed && shatterStill,
                "新战役：第一幕的短路 / 热震 / 标记跳转报名字，化工的爆燃不报；开放化工后爆燃报名字（重复开放 / 不存在的批次返回 false）；跨阵营开放后脆裂仍不报（机械内容下不可达）");

            // 地点真的按开放状态报名字：同一个 CombatSite 事件流，未开放的反应照样触发、只是不报名字。
            CampaignState fresh = NewState(9306, unlockAll: true);
            (string idC, bool namedC, int cuesC) = SiteCue(fresh, "fw_coolant", "fw_arcchain");
            (string idD, bool namedD, int cuesD) = SiteCue(fresh, "fw_oilleak", "fw_burntrail");
            NamedReactionCatalog.OpenBatch(fresh, "clarity");
            (string idD2, bool namedD2, _) = SiteCue(fresh, "fw_oilleak", "fw_burntrail");
            Expect(idC == "reaction_conduct" && namedC && cuesC >= 1 && idD == "reaction_deflagrate" && !namedD && cuesD >= 1 && idD2 == "reaction_deflagrate" && namedD2
                   && GuidanceHooks.Known.Contains(GuidanceHooks.ReactionFirstNamed) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.ReactionFirstNamed)
                   && GuidanceHooks.Known.Contains(GuidanceHooks.StatusTagFirstSeen),
                "地点事件流（CombatSite.ProcessEvents）：短路触发并报名字；爆燃（化工未开放）照样触发但不报名字；开放化工后同一反应报名字；" +
                "“首次打出具名反应”“首次看到状态标签”两个引导钩子已登记（内容在 FG15-UX-04）");

            // 题材审计：反应名 / 说明（中英）与旧引擎播报入口都没有生物词（FG-GAP-010）。
            var hits = new List<string>();
            foreach (Reaction row in NamedReactionCatalog.Rows)
            {
                foreach (string key in new[] { row.NameKey, row.DescKey, row.ResidueKey }.Where(k => k != "none"))
                {
                    AuditText(key, hits);
                }
            }
            foreach (string legacy in ReactionFeedbackCatalog.AllReactionIds)
            {
                string label = ReactionFeedbackCatalog.GetLabel(legacy);
                string desc = ReactionFeedbackCatalog.GetDescription(legacy);
                string z = ThemeLexicon.FirstHit(label) ?? ThemeLexicon.FirstHit(desc);
                if (z != null || label.Contains(legacy) || desc.Contains(legacy))
                {
                    hits.Add($"{legacy}: {label} / {desc}（{z ?? "漏出内部名"}）");
                }
            }
            GameSettings.SetLanguage(GameLanguage.En);
            foreach (string legacy in ReactionFeedbackCatalog.AllReactionIds)
            {
                string label = ReactionFeedbackCatalog.GetLabel(legacy);
                if (FgFirmwareMigrationSelfCheck.EnglishBioWords.IsMatch(label + " " + ReactionFeedbackCatalog.GetDescription(legacy)))
                {
                    hits.Add("EN " + legacy + ": " + label);
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool auditorBites = ThemeLexicon.FirstHit("溶血！") != null && ThemeLexicon.FirstHit("败血！") != null;
            Expect(hits.Count == 0 && ReactionFeedbackCatalog.AllReactionIds.Count() == 16 && ReactionFeedbackCatalog.GetLabel("Conduct") == "短路！"
                   && ReactionFeedbackCatalog.GetLabel("NotARealReaction") == GameText.Get("reaction.unnamed") && auditorBites,
                $"反应名 / 说明（中英）零禁用词；旧引擎播报入口 ReactionFeedbackCatalog 改走机械名（导电 → 短路！，旧表的溶血 / 糖浆 / 泥泞 / 败血不再出现），查不到的显示“未知反应”不漏内部名；审计器对照能抓到旧名{Detail(hits)}");
        }

        private static (string Id, bool Named, int Cues) SiteCue(CampaignState state, string fa, string fb)
        {
            CombatWeapon wa = WeaponFor(ComponentCatalog.CompGunId, fa);
            CombatWeapon wb = WeaponFor(ComponentCatalog.CompGunId, fb);
            var site = new CombatSite("fgfw03-probe", CombatSite.ConfigFromTuning());
            try
            {
                CombatKernel k = site.Kernel;
                int ia = k.AddWeapon(wa);
                int ib = k.AddWeapon(wb);
                int ma = k.Spawn(ProbeArena.MachineSpawn(new double2(0, 0), ia));
                int mb = k.Spawn(ProbeArena.MachineSpawn(new double2(0, 1), ib));
                int e = k.Spawn(ProbeArena.HostileSpawn(new double2(3, 0)));
                k.FireAt(ma, e, k.Time);
                site.ProcessEvents();
                k.FireAt(mb, e, k.Time);
                site.ProcessEvents();
                return (site.LastTagReactionId, site.LastTagReactionNamed, site.TagReactionCuesHandled);
            }
            finally
            {
                site.Dispose();
            }
        }

        private static void AuditText(string key, List<string> hits)
        {
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            string zh = GameText.Get(key);
            GameSettings.SetLanguage(GameLanguage.En);
            string en = GameText.Get(key);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            string z = ThemeLexicon.FirstHit(zh);
            if (z != null || FgFirmwareMigrationSelfCheck.EnglishBioWords.IsMatch(en) || zh.Contains("⟦") || en.Contains("⟦"))
            {
                hits.Add($"{key}: {zh} / {en}");
            }
        }

        // ── I. 存读档 ───────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · I. 存读档：内核快照格式 4（叠层、每条反应的触发次数与额外伤害、反应总次数）往返逐位一致、续跑一致；格式 3 旧快照照样读（叠层按已有标签各 1 层）；" +
                 "坏值拒绝且内核不变；反应规则是内容（读档沿用）；战役的开放批次真实存读档，旧档没有这个字段 = 只开放第一幕");
            NewState(9307, unlockAll: true);
            CombatWeapon wet = WeaponFor(ComponentCatalog.CompGunId, "fw_coolant");
            CombatWeapon shock = WeaponFor(ComponentCatalog.CompGunId, "fw_arcchain");
            CombatWeapon fire = WeaponFor(ComponentCatalog.CompGunId, "fw_burntrail");
            using (var a = new ProbeArena(true))
            using (var b = new ProbeArena(true))
            {
                int mW = a.Machine(wet);
                int mS = a.Machine(shock);
                int mF = a.Machine(fire);
                int e1 = a.Enemy();
                int e2 = a.Enemy(new double2(3, 3));
                a.Fire(mW, e1);
                a.Fire(mS, e1); // 短路
                a.Fire(mF, e2);
                a.Fire(mF, e2);
                a.Fire(mF, e2); // 燃烧 3 层
                a.K.IssueCommand(mW, CombatCommandKind.Attack, new double2(3, 3), e2, 0.5f, 6f, 0.5f, false);
                a.Run(0.5f);
                byte[] snap = a.K.Serialize();
                b.K.SetReactionRules(null);
                CombatLoadResult r = b.K.Load(snap);
                bool rulesKept = b.K.ReactionRuleCount == NamedReactionCatalog.TagRules.Count;
                // 读档沿用内容：b 的规则被清空后读档，读档不会把规则带回来——正式流程里地点重建时由 CombatSite 登记；这里再登记一次与正式流程一致。
                ProbeArena.Configure(b.K);
                ulong ha = a.K.StateHash();
                ulong hb = b.K.StateHash();
                int conduct = RuleIndex("reaction_conduct");
                int snapConduct = a.K.ReactionCountOf(conduct);
                bool countsKept = b.K.ReactionCountOf(conduct) == a.K.ReactionCountOf(conduct) && a.K.ReactionCountOf(conduct) >= 1
                                  && b.K.Counters.ReactionsFired == a.K.Counters.ReactionsFired && b.K.StatusStacksOf(e2, Bit("Fire")) == a.K.StatusStacksOf(e2, Bit("Fire"));
                a.Run(2f);
                b.Run(2f);
                Expect(r == CombatLoadResult.Ok && ha == hb && countsKept && a.K.StateHash() == b.K.StateHash() && !rulesKept,
                    $"格式 4 快照往返：哈希一致（{ha:X16}）、短路计数 {a.K.ReactionCountOf(conduct)}、燃烧 {a.K.StatusStacksOf(e2, Bit("Fire"))} 层都在，续跑 2 游戏秒仍一致；反应规则不在快照里（读档后由地点登记）");

                byte[] v3 = a.K.SerializeFormatForTests(3);
                using (var c = new ProbeArena(true))
                {
                    CombatLoadResult r3 = c.K.Load(v3);
                    arenaCheck(c, r3, e2);
                }

                byte[] corrupt = CorruptFirstReactionCount(snap);
                ulong before = b.K.StateHash();
                CombatLoadResult rc = b.K.Load(corrupt);
                Expect(rc == CombatLoadResult.InvalidValue && b.K.StateHash() == before, $"反应计数为负的快照被拒绝（{rc}），内核保持原样");

                // 修复轮 P2：计数按反应的稳定键存，不按规则下标——表里 priority 顺序改了（这里整表倒过来登记）读档后、以及重新登记时，短路的次数仍记在短路上；
                // 条数与当前 MaxReactions 不同的快照照样读（多出的丢弃，缺的为 0）。
                CombatReactionRule[] reversed = NamedReactionCatalog.BuildKernelRules().Reverse().ToArray();
                int revConduct = reversed.Length - 1 - conduct;
                int keyedLoad, keyedReregister, shrunk;
                CombatLoadResult rRev, rShrunk;
                using (var c2 = new ProbeArena(false))
                {
                    c2.K.SetReactionRules(reversed);
                    rRev = c2.K.Load(snap);
                    keyedLoad = c2.K.ReactionCountOf(revConduct);
                }
                ProbeArena.Configure(b.K);
                b.K.Load(snap);
                b.K.SetReactionRules(reversed);
                keyedReregister = b.K.ReactionCountOf(revConduct);
                using (var c3 = new ProbeArena(true))
                {
                    rShrunk = c3.K.Load(ShrinkReactionBlock(snap, conduct + 1));
                    shrunk = c3.K.ReactionCountOf(conduct);
                }
                Expect(revConduct != conduct && rRev == CombatLoadResult.Ok && keyedLoad == snapConduct && keyedReregister == snapConduct
                       && rShrunk == CombatLoadResult.Ok && shrunk == snapConduct,
                    $"反应计数按稳定键：规则倒序登记后读档，短路 {keyedLoad} 次（快照里 {snapConduct}）；已读档的内核倒序重新登记，仍是 {keyedReregister} 次；" +
                    $"只存 {conduct + 1} 条计数的快照照样读（{rShrunk}），短路 {shrunk} 次");
            }

            // 战役开放批次：真实存档文件往返；旧档（没有这个字段）只开放第一幕。
            CampaignState s = NewState(9308, unlockAll: true);
            NamedReactionCatalog.OpenBatch(s, "overclock");
            SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            LoadResult loaded = CampaignSaveService.Load(Slot);
            string json = JsonUtility.ToJson(s);
            CampaignState legacy = JsonUtility.FromJson<CampaignState>(SaveMigrationJson.RemoveField(json, "OpenReactionBatches"));
            Expect(saved.Success && loaded.Success && NamedReactionCatalog.IsNamed(loaded.State, "reaction_electrolysis") && !NamedReactionCatalog.IsNamed(loaded.State, "reaction_deflagrate")
                   && legacy != null && legacy.OpenReactionBatches != null && legacy.OpenReactionBatches.Length == 0
                   && NamedReactionCatalog.IsNamed(legacy, "reaction_conduct") && !NamedReactionCatalog.IsNamed(legacy, "reaction_electrolysis"),
                "开放“超频”后真实存档 → 读档：电解仍报名字、爆燃仍不报；旧档没有开放记录：只开放第一幕（短路报名字，电解不报）");

            // 修复轮 P2：反应表重载（版本号变了）后，地点下一步自动重新登记规则，已有的触发次数按稳定键保留。
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using (var site = new CombatSite("probe-reaction-revision", cfg))
            {
                int defl = RuleIndex("reaction_deflagrate");
                int e = site.Kernel.Spawn(ProbeArena.HostileSpawn(new double2(3.0, 0.2)));
                float fireDps = StatusTagCatalog.TryGet("Fire", out StatusTag ft) ? ft.Amount : 0f;
                site.Kernel.ApplyStatus(e, NamedReactionCatalog.BitOf("Oil"), 5f, 0f, 0f, 0f, 0);
                site.Kernel.ApplyStatus(e, NamedReactionCatalog.BitOf("Fire"), 3f, fireDps, 0f, 0f, 0);
                int before = site.Kernel.ReactionCountOf(defl);
                NamedReactionCatalog.Reload();
                bool stale = site.ReactionRevision != NamedReactionCatalog.Revision;
                site.Step(1f / 60f, site.Kernel.Time);
                Expect(before == 1 && stale && site.ReactionRevision == NamedReactionCatalog.Revision
                       && site.Kernel.ReactionRuleCount == NamedReactionCatalog.TagRules.Count && site.Kernel.ReactionCountOf(defl) == 1,
                    $"反应表重载后地点下一步自动重新登记（版本号已对齐 {!stale || site.ReactionRevision == NamedReactionCatalog.Revision}，规则 {site.Kernel.ReactionRuleCount} 条），爆燃计数 {site.Kernel.ReactionCountOf(defl)} 次保留");
            }
        }

        private static void arenaCheck(ProbeArena c, CombatLoadResult r3, int e2)
        {
            int conduct = RuleIndex("reaction_conduct");
            Expect(r3 == CombatLoadResult.Ok && c.K.StatusStacksOf(e2, Bit("Fire")) == 1 && c.K.ReactionCountOf(conduct) == 0 && c.K.Counters.ReactionsFired == 0,
                $"格式 3（FG2-FW-02）旧快照照样读：燃烧按 1 层（原 3 层）、反应计数为 0（{r3}）");
        }

        /// <summary>把快照的反应计数块截成前 <paramref name="keep"/> 条（条数字段一并改），重算校验和。</summary>
        private static byte[] ShrinkReactionBlock(byte[] snap, int keep)
        {
            int body = snap.Length - 4;
            int entry = CombatKernel.ReactionCounterEntryBytes;
            int blockStart = body - CombatConst.MaxReactions * entry - 4;
            int newBody = blockStart + 4 + keep * entry;
            var data = new byte[newBody + 4];
            Buffer.BlockCopy(snap, 0, data, 0, blockStart);
            Buffer.BlockCopy(BitConverter.GetBytes(keep), 0, data, blockStart, 4);
            Buffer.BlockCopy(snap, blockStart + 4, data, blockStart + 4, keep * entry);
            uint h = 2166136261u;
            for (int i = 0; i < newBody; i++)
            {
                h ^= data[i];
                h *= 16777619u;
            }
            data[newBody] = (byte)h;
            data[newBody + 1] = (byte)(h >> 8);
            data[newBody + 2] = (byte)(h >> 16);
            data[newBody + 3] = (byte)(h >> 24);
            return data;
        }

        private static byte[] CorruptFirstReactionCount(byte[] snap)
        {
            var data = (byte[])snap.Clone();
            int body = data.Length - 4;
            // 反应计数块：条数 + 每条（稳定键 int、次数 int、伤害 float），紧挨在校验和前；改坏第一条的次数。
            int first = body - CombatConst.MaxReactions * CombatKernel.ReactionCounterEntryBytes;
            byte[] neg = BitConverter.GetBytes(-1);
            Buffer.BlockCopy(neg, 0, data, first + 4, 4);
            uint h = 2166136261u;
            for (int i = 0; i < body; i++)
            {
                h ^= data[i];
                h *= 16777619u;
            }
            data[body] = (byte)h;
            data[body + 1] = (byte)(h >> 8);
            data[body + 2] = (byte)(h >> 16);
            data[body + 3] = (byte)(h >> 24);
            return data;
        }

        // ── J. 正式流程 ─────────────────────────────────────────────────────────

        private static void CheckWorldPipeline()
        {
            Line("  · J. 正式流程：破碎都市里“连射器 + 冷却液”“连射器 + 电弧链”两台机器经编队攻击命令打侦察机——地点内核真的打出短路并报名字；" +
                 "0.5x / 1x / 2x / 3x 与暂停、观察 / 不观察、真实存档读档后续跑逐位一致");
            BuildWorldSave(9309);
            CombatSite city = CombatSites.Get(FracturedCityLayout.RegionId);
            int conduct = RuleIndex("reaction_conduct");
            int c0 = city?.Kernel.ReactionCountOf(conduct) ?? -1;
            var hashes = new List<ulong>();
            var counts = new List<int>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                RestoreWorld();
                GameClock.SetSpeed(speed);
                long target = GameClock.Ticks + 60 * 6;
                int guard = 0;
                while (GameClock.Ticks < target && guard++ < 20000)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                }
                hashes.Add(SiteHash());
                counts.Add(CombatSites.Get(FracturedCityLayout.RegionId)?.Kernel.ReactionCountOf(conduct) ?? -1);
                GameClock.SetSpeed(1f);
            }
            RestoreWorld();
            ulong before = SiteHash();
            GameClock.SetPaused(true);
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            ulong paused = SiteHash();
            GameClock.SetPaused(false);
            CombatSite site = CombatSites.Get(FracturedCityLayout.RegionId);
            Expect(c0 >= 1 && hashes.Distinct().Count() == 1 && counts.All(c => c >= c0) && before == paused && city.LastTagReactionId == "reaction_conduct" && city.LastTagReactionNamed,
                $"破碎都市地点内核打出短路 {c0} 次（存档前）并报名字；续跑 6 游戏秒 0.5x / 1x / 2x / 3x 哈希一致（{hashes.Distinct().Count()} 种，短路 {string.Join("/", counts)} 次）；暂停 2 秒真实时间不走步");

            RestoreWorld();
            WorldView.Observe(FracturedCityLayout.RegionId);
            WorldSimulation.StepMany(180);
            ulong observed = SiteHash();
            RestoreWorld();
            WorldView.Observe(HomeValleyLayout.RegionId);
            WorldSimulation.StepMany(180);
            ulong unobserved = SiteHash();
            Expect(observed == unobserved, $"观察破碎都市与不观察同样跑 3 游戏秒：地点内核哈希一致（{observed:X16}，含叠层与反应计数）");

            RestoreWorld();
            WorldSimulation.StepMany(90);
            WorldSimulation.SyncAllForSave();
            SaveResult mid = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(90);
            ulong continuous = SiteHash();
            RestoreWorld();
            WorldSimulation.StepMany(90);
            ulong resumed = SiteHash();
            site = CombatSites.Get(FracturedCityLayout.RegionId);
            Expect(mid.Success && continuous == resumed && site != null && site.Kernel.ReactionRuleCount == NamedReactionCatalog.TagRules.Count,
                $"战斗中途真实存档 → 读档 → 续跑 1.5 游戏秒 = 不存档连续跑（{continuous:X16} / {resumed:X16}）；读档重建的地点重新登记了 {site?.Kernel.ReactionRuleCount} 条反应规则");
        }

        private static void BuildWorldSave(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fgfw03-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            AddBlueprint(s, BpWet, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { "fw_coolant" }));
            AddBlueprint(s, BpShock, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { "fw_arcchain" }));
            WorldSimulation.LoadHome(resume: false);
            int a = SpawnMachine(FracturedCityLayout.RegionId, BpWet, FracturedCityLayout.Scout2Spawn.Position + new Vector2(-3f, -3f));
            int b = SpawnMachine(FracturedCityLayout.RegionId, BpShock, FracturedCityLayout.Scout2Spawn.Position + new Vector2(2f, -3f));
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.Find(s).State = RegionState.Available;
            FracturedCityController city = WorldSimulation.LoadFracturedCity(new[] { a, b }, resume: false);
            WorldView.Observe(HomeValleyLayout.RegionId);
            city.SquadCommands.DebugSelectMany(new[] { a, b });
            city.SquadCommands.IssueAttack(FracturedCityLayout.Scout2SpawnId, paused: false);
            city.SquadCommands.ClearSelection();
            WorldSimulation.StepMany(90);
            WorldSimulation.SyncAllForSave();
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("反应战斗存档写入失败：" + r.Message);
            }
        }

        private static void RestoreWorld()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            File.Copy(CampaignSaveService.SlotPath(Slot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读反应战斗存档失败：" + r.Message);
                return;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            int[] ids = r.State.MachineRecords.Where(m => m.RegionId == FracturedCityLayout.RegionId && m.IsAlive).Select(m => m.LogicId).ToArray();
            WorldSimulation.LoadFracturedCity(ids, resume: true);
            WorldView.Observe(HomeValleyLayout.RegionId);
        }

        private static ulong SiteHash() => CombatSites.Get(FracturedCityLayout.RegionId)?.Kernel.StateHash() ?? 0;

        private static int SpawnMachine(string region, string bp, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, region, at, 200f, 200f, "Player", 1);
            if (!r.Success)
            {
                Fail($"登记测试机器失败：{r.Message}");
            }
            return r.LogicId;
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        // ── K. 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · K. 性能：200 台机器（一半冷却液、一半电弧链）编队攻击 200 个敌人，反应在内核里逐命中结算；头顶图标缓冲 400 个单位；热更层每步只处理有上限的提示事件");
            NewState(9310, unlockAll: true);
            CombatWeapon wet = WeaponFor(ComponentCatalog.CompGunId, "fw_coolant");
            CombatWeapon shock = WeaponFor(ComponentCatalog.CompGunId, "fw_arcchain");
            (double withMs, long fired) = BigFight(wet, shock, true);
            (double withoutMs, _) = BigFight(wet, shock, false);
            PerfLines.Add($"200 × 200 编队攻击：内核单步平均 {withMs:0.000} 毫秒（登记反应规则）/ {withoutMs:0.000} 毫秒（不登记），6 游戏秒共触发反应 {fired} 次");
            ExpectPerf(fired > 100, $"大规模反应结算：内核单步 {withMs:0.000} 毫秒（预算 4 毫秒，Editor batchmode；120 帧 = 8.3 毫秒一帧）、反应 {fired} 次", PerfGate.Lt(withMs, 4.0, "内核单步 ms"));

            using (var arena = new ProbeArena(true, capacity: 512))
            {
                int w = arena.K.AddWeapon(wet);
                var ids = new List<int>();
                for (int i = 0; i < 400; i++)
                {
                    ids.Add(arena.K.Spawn(ProbeArena.HostileSpawn(new double2(i % 20, i / 20))));
                }
                CarrierReadings.TryGetTagEffect("Charged", out uint chargedBit, out _, out _);
                CarrierReadings.TryGetTagEffect("Magnet", out uint magnetBit, out _, out _);
                foreach (int id in ids)
                {
                    arena.K.ApplyStatus(id, chargedBit | magnetBit, 5f, 0f, 0f, 0f, 0);
                }
                var renderer = new CombatRenderer();
                try
                {
                    renderer.SetStatusVisuals(NamedReactionCatalog.BuildStatusVisuals());
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < 20; i++)
                    {
                        arena.K.Step(1f / 60f, arena.K.Time + 1f / 60f);
                        renderer.Draw(arena.K, null, 1f, double2.zero, 0.6f);
                    }
                    sw.Stop();
                    double ms = sw.Elapsed.TotalMilliseconds / 20.0;
                    PerfLines.Add($"400 个带标签单位（各 2 个图标）：内核一步 + 重填图标缓冲 平均 {ms:0.000} 毫秒/帧，图标 {renderer.LastIconInstances} 个");
                    ExpectPerf(renderer.LastIconInstances == 800, $"头顶图标缓冲由 Burst 作业一次填好（{renderer.LastIconInstances} 个，{ms:0.000} 毫秒/帧含内核一步）；热更层每帧只调一次 Draw", PerfGate.Lt(ms, 4.0, "图标缓冲每帧 ms"));
                }
                finally
                {
                    renderer.Dispose();
                }
                _ = w;
            }
        }

        private static (double Ms, long Fired) BigFight(CombatWeapon wet, CombatWeapon shock, bool withRules)
        {
            using (var arena = new ProbeArena(withRules, capacity: 512))
            {
                int iw = arena.K.AddWeapon(wet);
                int isk = arena.K.AddWeapon(shock);
                var machines = new List<int>();
                var enemies = new List<int>();
                for (int i = 0; i < 200; i++)
                {
                    machines.Add(arena.K.Spawn(ProbeArena.MachineSpawn(new double2(i % 20, -(i / 20) - 2), i % 2 == 0 ? iw : isk)));
                    enemies.Add(arena.K.Spawn(ProbeArena.HostileSpawn(new double2(i % 20, (i / 20) + 2))));
                }
                for (int i = 0; i < 200; i++)
                {
                    int target = enemies[(i / 2) % 200];
                    arena.K.TryGetUnit(target, out CombatUnitView tv);
                    arena.K.IssueCommand(machines[i], CombatCommandKind.Attack, tv.Position, target, 0.5f, 6f, 0.5f, false);
                }
                var sw = Stopwatch.StartNew();
                int steps = 360;
                for (int s = 0; s < steps; s++)
                {
                    arena.K.Step(1f / 60f, arena.K.Time + 1f / 60f);
                    arena.K.ClearCues();
                    arena.K.DrainGameplay(4096, out _);
                }
                sw.Stop();
                return (sw.Elapsed.TotalMilliseconds / steps, arena.K.Counters.ReactionsFired);
            }
        }

        // ─────────────────────────────── 内核场地 ───────────────────────────────

        private struct KernelAfter
        {
            public uint Mask;
            public int Zones;
        }

        /// <summary>一组武器依次对同一个目标开火（<paramref name="order"/> 是武器下标序列），返回目标掉血总量；并给出这条反应的触发次数、额外伤害与目标最后的状态。
        /// <paramref name="withRules"/> = false：同一套开火但内核不登记反应规则（对照组）。</summary>
        private static float Hits(CombatWeapon[] weapons, int[] order, bool withRules, out int count, out float bonus, out KernelAfter after)
        {
            using (var arena = new ProbeArena(withRules))
            {
                var ms = weapons.Select(w => arena.Machine(w)).ToArray();
                int e = arena.Enemy();
                arena.K.TryGetUnit(e, out CombatUnitView v0);
                foreach (int o in order)
                {
                    arena.Fire(ms[o], e);
                }
                arena.K.TryGetUnit(e, out CombatUnitView v1);
                count = 0;
                bonus = 0f;
                for (int i = 0; i < arena.K.ReactionRuleCount; i++)
                {
                    count += arena.K.ReactionCountOf(i);
                    bonus += arena.K.ReactionDamageOf(i);
                }
                after = new KernelAfter { Mask = arena.Mask(e), Zones = arena.K.ZoneCount };
                return v0.Health - v1.Health;
            }
        }

        /// <summary>内核测试场地：正式配置（ConfigFromTuning：叠层上限等）+ 按表登记反应规则与状态位效果（与 CombatSite 同一套），机器与敌人都静止。</summary>
        private sealed class ProbeArena : IDisposable
        {
            public readonly CombatKernel K;
            public int LastShooter;
            private int _machines;

            public ProbeArena(bool withRules, int stackCap = -1, int capacity = 32)
            {
                CombatConfig cfg = CombatSite.ConfigFromTuning();
                cfg.NavEnabled = 0;
                if (stackCap >= 0)
                {
                    cfg.StatusStackCap = stackCap;
                }
                K = new CombatKernel(cfg, capacity);
                if (withRules)
                {
                    Configure(K);
                }
                else
                {
                    K.SetStatusFx(NamedReactionCatalog.BuildStatusFx());
                }
            }

            public static void Configure(CombatKernel k)
            {
                k.SetReactionRules(NamedReactionCatalog.BuildKernelRules());
                k.SetStatusFx(NamedReactionCatalog.BuildStatusFx());
            }

            public int Machine(CombatWeapon w)
            {
                int wi = K.AddWeapon(w);
                return K.Spawn(MachineSpawn(new double2(0, _machines++ * 0.3), wi));
            }

            public int Enemy() => Enemy(new double2(3.0, 0.2));

            public int Enemy(double2 at) => K.Spawn(HostileSpawn(at));

            public void Fire(int machine, int target)
            {
                LastShooter = machine;
                K.FireAt(machine, target, K.Time);
                K.ClearCues();
            }

            public void Run(float seconds)
            {
                int steps = Mathf.RoundToInt(seconds * 60f);
                for (int i = 0; i < steps; i++)
                {
                    K.Step(1f / 60f, K.Time + 1f / 60f);
                    K.ClearCues();
                }
            }

            public uint Mask(int id) => K.TryGetStatus(id, out uint m, out _, out _, out _, out _) ? m & ~CombatConst.StatusBitZoneSlow : 0u;

            public bool HasTag(int id, string tag) => (Mask(id) & NamedReactionCatalog.BitOf(tag)) != 0u;

            public static CombatSpawn MachineSpawn(double2 at, int weapon) => new CombatSpawn
            {
                ExtKey = 0,
                Kind = CombatUnitKind.Machine,
                Faction = CombatFaction.Player,
                Behavior = CombatBehavior.Commanded,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled,
                Position = at,
                Home = at,
                Radius = 0.4f,
                Speed = 4f,
                Health = 1e6f,
                MaxHealth = 1e6f,
                Weapon = weapon,
                BehaviorProfile = -1,
            };

            public static CombatSpawn HostileSpawn(double2 at) => new CombatSpawn
            {
                ExtKey = 0,
                Kind = CombatUnitKind.Enemy,
                Faction = CombatFaction.Hostile,
                Behavior = CombatBehavior.HoldFire,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                Position = at,
                Home = at,
                Radius = 0.4f,
                Speed = 0f,
                Health = 1e6f,
                MaxHealth = 1e6f,
                Weapon = -1,
                BehaviorProfile = -1,
                Priority = 1,
            };

            public void Dispose() => K?.Dispose();
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        private static CombatWeapon WeaponFor(string component, params string[] firmware)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, component, null, null, Array.Empty<string>());
            CircuitOpResult up = board.TrySetUplink(UplinkSlot);
            if (!up.Success)
            {
                Fail($"测试准备：{component} 蓝图 {UplinkSlot} 号格标接入口失败：{up.Message}");
            }
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, firmware ?? Array.Empty<string>());
            if (firmware != null && firmware.Length > 0 && !firmware.All(f => p.FirmwareIds.Contains(f)))
            {
                Fail($"测试准备：{component} 接入 {string.Join("/", firmware)} 没全部生效（生效 {string.Join("/", p.FirmwareIds)}）");
            }
            return CombatSite.MachineWeaponFrom(p);
        }

        /// <summary>挂得上 <paramref name="tag"/>、又不会同时挂上配对另一个标签的第一条固件（表顺序）。</summary>
        private static string PickProducer(string tag, string other)
        {
            uint want = NamedReactionCatalog.BitOf(tag);
            uint avoid = NamedReactionCatalog.BitOf(other);
            foreach (GameConfig.fg.FirmwareKind fw in FirmwareKinds.Rows)
            {
                uint m = WeaponFor(ComponentCatalog.CompGunId, fw.Id).Reading.StatusMask;
                if ((m & want) != 0u && (m & avoid) == 0u && !WouldReactAlone(m))
                {
                    return fw.Id;
                }
            }
            return null;
        }

        /// <summary>一把武器自己的标签就凑齐某条反应（同一发就会反应），不适合当“只有一个配料”的对照。</summary>
        private static bool WouldReactAlone(uint mask)
        {
            foreach (CombatReactionRule r in NamedReactionCatalog.BuildKernelRules())
            {
                if (r.Pair != 0u && (mask & r.Pair) == r.Pair)
                {
                    return true;
                }
            }
            return false;
        }

        private static CampaignState NewState(int seed, bool unlockAll = false)
        {
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            CampaignState s = CampaignState.CreateNew("fgfw03-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            if (unlockAll)
            {
                s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            }
            return s;
        }

        private static Reaction RowOfRule(string ruleId) =>
            NamedReactionCatalog.Rows.FirstOrDefault(r => r.Kind == NamedReactionCatalog.KindTag && r.LegacyRules.Split(';').Contains(ruleId));

        private static int RuleIndex(string reactionId)
        {
            for (int i = 0; i < NamedReactionCatalog.TagRules.Count; i++)
            {
                if (NamedReactionCatalog.TagRules[i].Id == reactionId)
                {
                    return i;
                }
            }
            return -1;
        }

        private static int Bit(string tag)
        {
            uint b = NamedReactionCatalog.BitOf(tag);
            return b == 0u ? -1 : math.tzcnt(b);
        }

        private static int CountBits(uint m) => math.countbits(m);

        /// <summary>旧引擎标签 → 去重排序的主状态标签（机制标记 / 反应产物名不算）。</summary>
        private static string[] CanonStatus(IEnumerable<string> tags) =>
            tags.Select(StatusTagCatalog.Canonical).Where(StatusTagCatalog.IsStatus).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray();

        private static string[] Split(string v) =>
            string.IsNullOrEmpty(v) || v == "none" ? Array.Empty<string>() : v.Split(';').Select(x => x.Trim()).OrderBy(t => t, StringComparer.Ordinal).ToArray();

        private static void Compare(string[] src, string[] mine, List<string> diffs)
        {
            if (mine == null || src.Length - 1 != mine.Length)
            {
                diffs.Add(src.Length > 1 ? src[1] + "（缺行或列数不同）" : "空行");
                return;
            }
            for (int i = 0; i < mine.Length; i++)
            {
                if (!SameValue(src[i + 1], mine[i]))
                {
                    diffs.Add($"{src[1]}.第{i + 1}列 源={src[i + 1]} 表={mine[i]}");
                }
            }
        }

        private static string F(float v) => v.ToString("0.0###", CultureInfo.InvariantCulture);

        private static bool SameValue(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return true;
            }
            return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                   && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && Math.Abs(x - y) < 1e-4;
        }

        private static string LocateRepo()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "cell_tables", "check_luban.py")))
                {
                    return dir.FullName;
                }
            }
            return Directory.GetCurrentDirectory();
        }

        private static (int code, string output) RunPython(string root, string args)
        {
            var psi = new ProcessStartInfo("python", args)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using (Process proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    var stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(120000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { }
                        return (-1, $"python {args} 超过 120 秒没有结束");
                    }
                    return (proc.ExitCode, stdout.Result + stderr.Result);
                }
            }
            catch (Exception e)
            {
                return (-1, "启动 python 失败：" + e.Message);
            }
        }

        private static string Detail(List<string> items) =>
            items == null || items.Count == 0 ? string.Empty : "；问题：" + string.Join("；", items.Take(12)) + (items.Count > 12 ? $"……共 {items.Count} 条" : string.Empty);

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

        /// <summary>FG-TOOL-01：性能断言只测一次；超阈值不到 2 倍记性能警告（不计失败），超 2 倍才失败。功能条件放 <paramref name="ok"/>。</summary>
        private static void ExpectPerf(bool ok, string message, params PerfGate.Metric[] perf) => PerfGate.Expect(ok, message, perf, Expect, Line);

        private static void Fail(string message)
        {
            _fail++;
            Line("    ✗ " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
