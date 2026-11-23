using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-BAL-01 全局平衡（FG16 第 12 节）：
    /// A FGT-BAL-001 无硬编码——扫描热更层源码里所有按字面键取调参的调用（GridContent.TryGetTuning / Tuning 与各文件里转调它们的小帮手），每个键都在调参表里，代码里的兜底值从不生效；扫描器自己的负向对照。
    /// B FGT-BAL-002 无限循环——用运行时物品 / 配方表重算废料当量，每条配方产出送回收站得到的废料都少于投入当量（分解 → 再生产每圈都亏）；FG16 §9 子载荷与元程序重复执行不重复计暴露（同一次冷却只计一次）。
    ///   模糊测试器四类检查（崩溃、卡死、帧耗超标、零反应）在 FgEmergenceFuzzSelfCheck，商队“买入 → 分解 → 再买”在 FgCaravanSelfCheck D2 / N6。
    /// C FGT-BAL-003 难度预设每个参数都生效——三档难度各开一局，读正式玩法用的消费函数（预警、静默夜间隔、事件频率、突袭预算、突袭频率、敌人耐久 / 伤害、只有剧情突袭）。
    ///   战斗内核里的实际耐久 / 伤害、改难度后的突袭重排等真实行为在 FgDifficultySelfCheck。
    /// FGT-BAL-004 节奏机器人在 FgAct1BalanceSelfCheck。
    /// </summary>
    public static class FgGlobalBalanceSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("QA/自检/FG 全局平衡")]
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
            Line("\n[全局平衡] FG15-BAL-01：FGT-BAL-001 无硬编码、FGT-BAL-002 无限循环 / 暴露只按根计、FGT-BAL-003 难度预设每项生效");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过");
                return 0;
            }
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                UiTuningValues.Reload();
                ItemCatalog.Reload();
                GameClock.ReloadTuning();
                Step(CheckNoHardcode);
                Step(CheckRecycleLoops);
                Step(CheckExposureRootOnly);
                Step(CheckDifficultyParams);
                Step(CheckHighPowerProductionOnly);
                Step(CheckBossDuration);
            }
            finally
            {
                GameClock.ResetSession();
            }
            Line($"  · [全局平衡] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static void Step(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Fail($"{a.Method.Name} 抛异常：{e.GetType().Name} {e.Message}\n{e.StackTrace}");
            }
        }

        // ── A FGT-BAL-001 ─────────────────────────────────────────────────────

        private static readonly Regex HelperDef = new Regex(@"static\s+\w+\s+(\w+)\s*\(\s*string\s+\w+\s*,\s*\w+\s+\w+\s*\)", RegexOptions.Compiled);

        /// <summary>一份源码里按字面键取调参的全部键：直接调 TryGetTuning / Tuning / UiTuningValues.TryGet，或调本文件里转调它们的两参小帮手（形如 T("key", 兜底)）。</summary>
        internal static List<string> TuningKeysIn(string src)
        {
            var names = new HashSet<string>(StringComparer.Ordinal) { "TryGetTuning", "Tuning", "TryGet" };
            foreach (Match m in HelperDef.Matches(src))
            {
                int end = Math.Min(src.Length, m.Index + m.Length + 400);
                string body = src.Substring(m.Index + m.Length, end - m.Index - m.Length);
                if (body.Contains("TryGetTuning(") || body.Contains("Tuning(") || body.Contains("UiTuningValues.TryGet("))
                {
                    names.Add(m.Groups[1].Value);
                }
            }
            var call = new Regex(@"(?<![\w])(?:GridContent\.|CombatSite\.|UiTuningValues\.)?(" + string.Join("|", names.Select(Regex.Escape)) + @")\(\s*""([a-z][a-z0-9_]*(?:\.[a-z0-9_]+)+)""");
            var keys = new List<string>();
            foreach (Match m in call.Matches(src))
            {
                if (m.Groups[1].Value == "TryGet" && !src.Substring(Math.Max(0, m.Index - 16), Math.Min(16, m.Index)).EndsWith("UiTuningValues.", StringComparison.Ordinal))
                {
                    continue; // 别的 TryGet（字典、表）不是调参
                }
                keys.Add(m.Groups[2].Value);
            }
            return keys;
        }

        private static bool InTable(string key) => GridContent.TryGetTuning(key, out _) || UiTuningValues.TryGet(key, out _);

        private static void CheckNoHardcode()
        {
            string root = Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic");
            var used = new Dictionary<string, string>(StringComparer.Ordinal);
            int files = 0;
            foreach (string f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                files++;
                foreach (string k in TuningKeysIn(File.ReadAllText(f)))
                {
                    if (!used.ContainsKey(k))
                    {
                        used[k] = Path.GetFileName(f);
                    }
                }
            }
            List<string> missing = used.Where(kv => !InTable(kv.Key)).Select(kv => $"{kv.Key}（{kv.Value}）").ToList();
            Expect(files > 500 && used.Count > 500 && missing.Count == 0,
                $"A1 FGT-BAL-001：扫描热更层 {files} 个源文件、按字面键取调参 {used.Count} 个键，全部在 fg.TbHomeTuning / 界面调参表里（缺 {missing.Count}：{string.Join("、", missing.Take(8))}）——代码里的兜底值从不生效，数值都来自表");
            // 负向：扫描器认得出“表里没有的键”与本文件小帮手，不认别的 TryGet
            const string probe = "static float T(string id, float fb) => GridContent.TryGetTuning(id, out float v) ? v : fb;\n float a = T(\"bal.selfcheck.not_in_table\", 3f);\n dict.TryGet(\"bal.selfcheck.dict_key\", out x);\n float b = GridContent.Tuning(\"siege.sync_seconds\");";
            List<string> pk = TuningKeysIn(probe);
            bool neg = pk.Contains("bal.selfcheck.not_in_table") && !InTable("bal.selfcheck.not_in_table") && !pk.Contains("bal.selfcheck.dict_key") && pk.Contains("siege.sync_seconds");
            Expect(neg, $"A2 负向：扫描器认出本文件转调调参的小帮手 T(...) 与直接调用（{string.Join("、", pk)}），表里没有的键会被报出来，字典的 TryGet 不算调参");

            // A3 / A4（复审 P1：A1 只证明“取调参的键都在表里”，看不见直接写死的数字）：热更层全部数值字面量常量（const / static readonly 数值类型 = 数字字面量）
            // 与存量基线 FgHardcodeBaseline.txt 比对——基线以外新出现的、或值被改过的一律报出（新游戏数值必须进表；技术常量要在基线里写理由）。
            // shortcut: 只扫“具名常量”这一种写法，方法体里直接写的字面量（如 damage = 40f）不扫；全量字面量归类交 FG15-ACC-02（DEBT-FG15BAL01-03，ADR-BAL-002 第 7 节写明覆盖范围）。
            HashSet<string> baseline = LoadHardcodeBaseline(out int baseRows);
            var current = new List<string>();
            foreach (string f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = f.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').TrimStart('/');
                current.AddRange(LiteralConstsIn(rel, File.ReadAllText(f)));
            }
            List<string> added = current.Where(k => !baseline.Contains(k)).Distinct().ToList();
            Expect(baseRows > 800 && current.Count > 800 && added.Count == 0,
                $"A3 FGT-BAL-001 数值字面量常量：热更层扫出 {current.Count} 条，全部在存量基线（{baseRows} 条，FgHardcodeBaseline.txt）里；新出现或改过值的 {added.Count} 条：{string.Join("、", added.Take(8))}");
            const string probe2 = "public const float RaidDamage = 40f;\n private static readonly int Cap = 12;\n const string Name = \"x\";\n public const float RetrofitDurationSeconds = 30f;";
            List<string> hits = LiteralConstsIn("Campaign/Regions/HomeValleyFactory.cs", probe2);
            List<string> flagged = hits.Where(k => !baseline.Contains(k)).ToList();
            bool neg2 = flagged.Contains("Campaign/Regions/HomeValleyFactory.cs|RaidDamage|40f") && flagged.Contains("Campaign/Regions/HomeValleyFactory.cs|Cap|12")
                        && flagged.Contains("Campaign/Regions/HomeValleyFactory.cs|RetrofitDurationSeconds|30f") && hits.Count == 3
                        && baseline.Contains("Campaign/Regions/HomeValleyFactory.cs|RetrofitDurationSeconds|25f");
            Expect(neg2, $"A4 负向：新写的 const float RaidDamage = 40f、static readonly int Cap = 12 与把已有常量 25f 改成 30f 都会被报出（{string.Join("、", flagged)}），字符串常量不算");
        }

        private static readonly Regex LiteralConst = new Regex(@"(?:\bconst|\bstatic\s+readonly)\s+(?:float|double|int|long|uint|short|ushort|byte|sbyte|decimal)\s+(\w+)\s*=\s*(-?[0-9][0-9_.eE+\-]*[fFdDmMuUlL]*)\s*[;,]", RegexOptions.Compiled);

        /// <summary>一份源码里的数值字面量常量，键 = 相对 GameLogic 的路径|常量名|字面值（与 FgHardcodeBaseline.txt 同一格式、同一正则）。</summary>
        internal static List<string> LiteralConstsIn(string rel, string src) =>
            LiteralConst.Matches(src).Cast<Match>().Select(m => $"{rel}|{m.Groups[1].Value}|{m.Groups[2].Value}").ToList();

        private static HashSet<string> LoadHardcodeBaseline(out int rows)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            string path = Path.Combine(Application.dataPath, "Editor/QA/FgHardcodeBaseline.txt");
            foreach (string line in File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>())
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string[] p = line.Split('|');
                if (p.Length >= 3)
                {
                    set.Add(p[0] + "|" + p[1] + "|" + p[2]); // 第 4 列是理由
                }
            }
            rows = set.Count;
            return set;
        }

        // ── B FGT-BAL-002 ─────────────────────────────────────────────────────

        private static void CheckRecycleLoops()
        {
            // 与生成端 fgdata_eco.py _scrap_equivalents 同一口径：废料 1、金属矿 2、稀土矿 1、流体 0；其余固体 = 最便宜那条配方的输入当量 ÷ 产量（迭代到不再变）。
            var eq = new Dictionary<string, double>(StringComparer.Ordinal) { ["scrap"] = 1, ["metal_ore"] = 2, ["rare_earth_ore"] = 1 };
            foreach (ItemDef it in ItemCatalog.Items)
            {
                if (it.Form == ItemForm.Fluid)
                {
                    eq[it.Id] = 0;
                }
            }
            IReadOnlyList<RecipeDef> recipes = ItemCatalog.Recipes;
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (RecipeDef r in recipes)
                {
                    RecipeLine[] ins = r.Lines.Where(l => l.Role == RecipeRole.In).ToArray();
                    RecipeLine[] outs = r.Lines.Where(l => l.Role == RecipeRole.Out).ToArray();
                    if (outs.Length == 0 || ins.Any(l => l.Item == null || !eq.ContainsKey(l.Item.Id)) || outs[0].Item == null)
                    {
                        continue;
                    }
                    ItemDef main = outs[0].Item;
                    if (main.Form != ItemForm.Solid || main.Id == "scrap" || main.Id == "metal_ore" || main.Id == "rare_earth_ore")
                    {
                        continue;
                    }
                    double per = ins.Sum(l => eq[l.Item.Id] * l.Amount) / Math.Max(1, outs[0].Amount);
                    if (!eq.TryGetValue(main.Id, out double cur) || per < cur - 1e-9)
                    {
                        eq[main.Id] = per;
                        changed = true;
                    }
                }
            }
            int checkedRecipes = 0;
            var bad = new List<string>();
            double worst = 0;
            string worstId = "";
            foreach (RecipeDef r in recipes)
            {
                RecipeLine[] ins = r.Lines.Where(l => l.Role == RecipeRole.In).ToArray();
                if (ins.Length == 0 || ins.Any(l => l.Item == null || !eq.ContainsKey(l.Item.Id)))
                {
                    continue;
                }
                double cost = ins.Sum(l => eq[l.Item.Id] * l.Amount);
                int back = r.Lines.Where(l => l.Role != RecipeRole.In && l.Item != null && l.Item.Form == ItemForm.Solid).Sum(l => l.Item.RecycleScrap * l.Amount);
                checkedRecipes++;
                if (back > 0 && !(back < cost))
                {
                    bad.Add($"{r.Id}（回收 {back} ≥ 投入 {cost:0.##}）");
                }
                if (back > 0 && cost > 0 && back / cost > worst)
                {
                    worst = back / cost;
                    worstId = r.Id;
                }
            }
            Expect(checkedRecipes >= 10 && checkedRecipes * 2 >= recipes.Count && bad.Count == 0, // 输入含远征物（没有配方来源、当量未知）的配方算不了当量，但它们本身只能靠拾取补料，不成循环
                $"B1 FGT-BAL-002 / FGR-BAL-050“分解 → 再生产”：运行时配方表 {recipes.Count} 条、可算当量的 {checkedRecipes} 条，每条产出送回收站的废料都少于投入的废料当量（违规 {bad.Count}：{string.Join("、", bad.Take(6))}）；回收率最高 {worst:P0}（{worstId}），每转一圈都亏");
            // 每件可回收的固体：回收得到的废料少于它的当量（没有配方来源的远征物当量未知，按 1 件得 1 废料，只能拾取不能生产，不成循环）
            var itemBad = ItemCatalog.Items.Where(it => it.Form == ItemForm.Solid && it.Id != "scrap" && eq.TryGetValue(it.Id, out double e) && e > 0 && it.RecycleScrap >= e && it.RecycleScrap > 1)
                .Select(it => $"{it.Id}（{it.RecycleScrap} ≥ {eq[it.Id]:0.##}）").ToList();
            bool scrapSelf = ItemCatalog.TryGet("scrap", out ItemDef scrap) && scrap.RecycleScrap == 1;
            Expect(itemBad.Count == 0 && scrapSelf,
                $"B2 每件可生产的固体回收得到的废料少于做它的当量（违规 {itemBad.Count}：{string.Join("、", itemBad.Take(6))}）；废料本身回收 1 得 1（不增不减，{scrapSelf}）");
        }

        /// <summary>FG16 §9 / FG19 第 14 节第 10 条（推荐默认，待用户确认）：子载荷与元程序重复执行不重复计暴露，只按根载荷的发动计一次。</summary>
        private static void CheckExposureRootOnly()
        {
            string fw = FirmwareCatalog.FwMarkTagId;
            string reaction = MechanicalReactionCatalog.ReactionMarkJumpId;
            string[] assembly = { fw };
            float Fire(int echoes, out CampaignState s)
            {
                GameClock.ResetSession();
                s = CampaignState.CreateNew("fgbal15-exp", DifficultyService.Standard, 15015);
                GameClock.Bind(s);
                float before = s.SignalExposure;
                // 一次根发动 + 同一次出手里子载荷 / 元程序（复制、循环）再报上来的发动：同一步内连续到达宿主（炮塔接入口走的正式入口）
                for (int i = 0; i <= echoes; i++)
                {
                    SignalUplinkService.OnHostReactionFired(s, reaction, assembly);
                }
                return s.SignalExposure - before;
            }
            float one = Fire(0, out _);
            float many = Fire(1 + 4 + 2, out CampaignState st); // 子载荷 2 代（1 + 4 份）+ 复制两次
            float cd = FirmwareKinds.CoreCooldownSeconds(fw);
            GameClock.SkipForTests(st, GameClock.TicksFor(cd) + 1);
            float before2 = st.SignalExposure;
            SignalUplinkService.OnHostReactionFired(st, reaction, assembly);
            float next = st.SignalExposure - before2;
            Expect(one > 0f && Mathf.Approximately(many, one) && cd > 0f && Mathf.Approximately(next, one),
                $"B3 FG16 §9 暴露只按根计：一次根发动暴露 +{one:0.##}；同一次出手里子载荷 2 代与复制两次共再报 7 次，暴露仍只 +{many:0.##}；冷却 {cd:0.#} 秒结束后的下一次根发动照常再计 +{next:0.##}");
        }

        // ── C FGT-BAL-003 ─────────────────────────────────────────────────────

        private static void CheckDifficultyParams()
        {
            var rows = new Dictionary<string, (long Warn, int Silent, double Events, int Budget, float Freq, float Hp, float Dmg, bool StoryOnly)>();
            foreach (string id in new[] { DifficultyService.Builder, DifficultyService.Standard, DifficultyService.Harsh })
            {
                GameClock.ResetSession();
                CampaignState s = CampaignState.CreateNew("fgbal15-diff-" + id, id, 15016);
                GameClock.Bind(s);
                RaidDirectorService.EnsureState(s);
                RaidDirectorState d = RaidDirectorService.StateOf(s);
                var plan = new RaidPlanRecord { Level = 2, PostgameIndex = -1, CreatedTick = 0, Trigger = RaidCatalog.TriggerExposure };
                rows[id] = (RaidDirectorService.MinWarningTicks(s), SilentNightService.IntervalDays(s), EventDirectorService.Rate(s),
                    RaidDirectorService.ComputeBudget(s, d, plan), DifficultyService.RaidFrequency(s),
                    DifficultyService.EnemyHealthMul(s), DifficultyService.EnemyDamageMul(s), DifficultyService.StoryOnly(s));
            }
            var b = rows[DifficultyService.Builder];
            var n = rows[DifficultyService.Standard];
            var h = rows[DifficultyService.Harsh];
            bool raid = b.StoryOnly && !n.StoryOnly && !h.StoryOnly && Near(b.Budget, n.Budget * 0.5) && Near(h.Budget, n.Budget * 1.3) && Mathf.Approximately(h.Freq, n.Freq * 1.3f);
            bool enemy = Mathf.Approximately(b.Hp, 0.8f * n.Hp) && Mathf.Approximately(h.Hp, 1.2f * n.Hp) && Mathf.Approximately(b.Dmg, 0.8f * n.Dmg) && Mathf.Approximately(h.Dmg, 1.2f * n.Dmg);
            bool warn = Near(b.Warn, n.Warn * 1.5) && Near(h.Warn, n.Warn * 0.8);
            bool silent = b.Silent == 7 && n.Silent == 5 && h.Silent == 4;
            bool events = b.Events < n.Events && n.Events < h.Events;
            Expect(raid, $"C1 突袭：建造者只有剧情突袭（{b.StoryOnly}）、2 级预算 {b.Budget} = 标准 {n.Budget} × 0.5；严酷预算 {h.Budget} = × 1.3、频率 ×{h.Freq / n.Freq:0.##}（突袭导演 ComputeBudget / 排期用的倍率）");
            Expect(enemy, $"C2 敌人耐久 / 伤害：建造者 ×{b.Hp / n.Hp:0.##} / ×{b.Dmg / n.Dmg:0.##}、严酷 ×{h.Hp / n.Hp:0.##} / ×{h.Dmg / n.Dmg:0.##}（播种耐久与战斗内核用的倍率；内核实测在 FgDifficultySelfCheck H / H2）");
            Expect(warn, $"C3 预警时间：建造者 {b.Warn} 步 = 标准 {n.Warn} × 1.5、严酷 {h.Warn} = × 0.8（突袭导演最短预警）");
            Expect(silent, $"C4 静默夜间隔：建造者每 {b.Silent} 日、标准每 {n.Silent} 日、严酷每 {h.Silent} 日（静默夜服务排期）");
            Expect(events, $"C5 事件频率：建造者 {b.Events:0.##} < 标准 {n.Events:0.##} < 严酷 {h.Events:0.##}（事件导演总倍率 = 难度 eventRate × 设置档位）");
        }

        // ── D FG-GAP-151 / 111：高功率暴露只按生产用电 ─────────────────────────────

        private static float HourExposure(float total, float defense, bool viaSave = false)
        {
            GameClock.ResetSession();
            CampaignState s = CampaignState.CreateNew("fgbal15-hp", DifficultyService.Standard, 15017);
            s.PowerDemand = total;
            s.DefensePowerDemand = defense;
            if (viaSave)
            {
                s = JsonUtility.FromJson<CampaignState>(JsonUtility.ToJson(s)); // 复审 P2：存档往返（与 FgSiegeSelfCheck L8d 同一序列化）后再结算
            }
            GameClock.Bind(s);
            float before = s.SignalExposure;
            long steps = GameClock.TicksFor(CampaignExposureLedger.HighPowerSettleSeconds) + 2;
            for (long i = 0; i < steps; i++)
            {
                CampaignExposureLedger.SimStepHighPower(s, GameClock.StepSeconds);
            }
            return s.SignalExposure - before;
        }

        private static void CheckHighPowerProductionOnly()
        {
            float thr = CampaignExposureLedger.HighPowerThreshold;
            float withDefense = HourExposure(thr + 60f, 70f);   // 生产 thr − 10：不到阈值
            float control = HourExposure(thr + 60f, 0f);        // 同样总用电、没有防御
            float clamp = HourExposure(20f, 50f);               // 负向：防御记账大于总用电也不出负数
            float expect = CampaignExposureLedger.HighPowerRatePerHour(thr + 60f);
            Expect(withDefense == 0f && control > 0f && clamp == 0f
                   && Mathf.Approximately(CampaignExposureLedger.ProductionPowerDemand(new CampaignState { PowerDemand = 150f, DefensePowerDemand = 40f }), 110f),
                $"D1 FG-GAP-151 高功率生产只按生产用电换算（FG16 第 9 节）：总用电 {thr + 60f:0}、其中防御 70 → 一游戏小时暴露 +{withDefense:0.##}；同样总用电没有防御 → +{control:0.##}（速率 {expect:0.##}/游戏小时）；防御记账大于总用电 → +{clamp:0.##}。电网重算写防御用电由 FgAct1BalanceSelfCheck B 段（真实炮塔）断言");
            // D1b 存读档：DefensePowerDemand 随存档保留，读档后下一个结算周期的暴露增量与连续运行一致。
            var src = new CampaignState { PowerDemand = thr + 60f, DefensePowerDemand = 50f };
            CampaignState back = JsonUtility.FromJson<CampaignState>(JsonUtility.ToJson(src));
            float cont = HourExposure(thr + 60f, 50f);
            float loaded = HourExposure(thr + 60f, 50f, viaSave: true);
            Expect(back != null && back.DefensePowerDemand == 50f && back.PowerDemand == src.PowerDemand
                   && Mathf.Approximately(CampaignExposureLedger.ProductionPowerDemand(back), CampaignExposureLedger.ProductionPowerDemand(src)) && cont > 0f && Mathf.Approximately(cont, loaded),
                $"D1b 存读档：防御用电读回 {back?.DefensePowerDemand:0}（应 50）、生产用电 {CampaignExposureLedger.ProductionPowerDemand(back):0}；读档后一游戏小时暴露 +{loaded:0.###}，与连续运行 +{cont:0.###} 一致");
        }

        // ── E FG-GAP-148：铸造主核心首领战时长 ─────────────────────────────────

        private static void CheckBossDuration()
        {
            // 复审 P1：原 E1 用 FGJ-M9 夹具队伍的输出按算术折算“约 5.2 分”就判过——那是估算，不是实测，已删。
            // 首领战真打时长只认 FgAct1BalanceSelfCheck H 段（真实出厂耐久的节奏机器人右键真打，三种子）；与 FG16 第 8 节 5～8 分钟的差距登记 DEBT-FG15BAL01-02（承接 FG15-E2E-01）。
            // 本段只做数据核对（不是时长断言）：主核心耐久 = 调参记录 BAL-REC-025 的 600，且出厂机器耐久 = BAL-REC-024 的 800（两者是 H 段实测的前提）。
            float hp = GameLogic.Campaign.Regions.FoundryOutpostLayout.MainCoreMaxHealth;
            float shipped = GameLogic.Campaign.Regions.HomeValleyFactory.ShippedHealth;
            Expect(Mathf.Approximately(hp, 600f) && Mathf.Approximately(shipped, 800f),
                $"E1 数据核对（只核对数值，时长以 FgAct1BalanceSelfCheck H 段真打为准，差距见 DEBT-FG15BAL01-02）：铸造主核心耐久 {hp:0}（BAL-REC-025 应 600）、出厂机器耐久 {shipped:0}（BAL-REC-024 应 800）");
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) <= Math.Max(1.0, Math.Abs(b) * 0.01);

        private static void Line(string s) => _report?.AppendLine(s);

        private static void Expect(bool ok, string message)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }
    }
}
