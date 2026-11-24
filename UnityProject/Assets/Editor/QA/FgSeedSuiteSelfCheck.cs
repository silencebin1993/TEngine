using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.EditorTools.JourneyBots;
using GameLogic.Localization;
using GameLogic.Settings;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-QA-03 种子测试集（FG17 FGR-GEN-090 / 091 / 061；FGT-GEN-003 / 004 / 006 / 009；FG00 B25）。
    /// 固定 50 个测试种子（<see cref="FgWorldGenHomeSelfCheck.SeedSet"/>）× 新战役的生成器版本：
    /// 起始区四级保证、领地布局、可达（四个阵营领地、白潮滩头预留区、全部家园区侦察巢与遗迹点走层级寻路内核；全部锚点走生产的可达校验）、
    /// 规划层粗粒度连通性判定（v5 起，被地形围住按子种子换候选）；极端世界设置组合；种子 × 生成器版本回归哈希（基准 FgSeedSuiteBaseline.tsv，随版本绑定）；
    /// 旧生成器版本的存档；失败报告带种子、世界设置、表面、区块坐标和失败项。行军截断率在 <see cref="FgExpeditionMarchSelfCheck.RunSeedSuiteMarch"/>（另一段）。
    /// 一键运行：菜单“QA/自检/FG 种子测试集”，或 <c>bash tools/seed-suite.sh</c>（两段自检；加 --journeys 再跑 3 个种子的主菜单到结局旅程与极端设置旅程）。
    /// </summary>
    public static class FgSeedSuiteSelfCheck
    {
        public const string SegmentName = "FgSeedSuiteSelfCheck";
        public const string MarchSegmentName = "FgSeedSuiteMarchSelfCheck";
        private const int ChunkSize = 32;
        private const int Slot = 6;

        /// <summary>FGR-GEN-091“其中 3 个种子跑完整的旅程机器人”：主菜单 → 结局（FGJ-M13 的步骤）。42 = FGJ-M13 本身，另两个是 FGJ-SEED-1 / FGJ-SEED-17。</summary>
        public static readonly int[] JourneySeeds = { 42, 1, 17 };

        /// <summary>极端世界设置组合（资源 R / 据点 O / 污染 P / 领地距离 D / 起始区 S 的取值端点）：每个轴的两个端点都出现，且含“资源低 + 据点高 + 污染高 + 领地近 / 远”。</summary>
        public static readonly string[] ExtremeCodes = { FgWorldGenHomeSelfCheck.ExtremeCode, "R0O2P2D2S0", "R2O0P0D2S1", "R2O2P2D0S1" };

        /// <summary>极端组合下跑真寻路可达的种子数（起始区与布局仍是全部 50 个）。</summary>
        private const int ExtremeReachSeeds = 5;

        public const string BaselineFile = "Assets/Editor/QA/FgSeedSuiteBaseline.tsv";

        private static StringBuilder _report;
        private static int _fail;
        private static string _dir;

        private static int[] Seeds => FgWorldGenHomeSelfCheck.SeedSet;

        [MenuItem("QA/自检/FG 种子测试集")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report) + FgExpeditionMarchSelfCheck.RunSeedSuiteMarch(report);
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
            Line($"\n[种子测试集] {Seeds.Length} 个种子 × 生成器 v{WorldGenVersions.Current}：起始区 / 领地布局 / 可达 / 锚点粗粒度连通性、极端世界设置、种子 × 版本回归哈希、旧版本存档（FG15-QA-03）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-seedsuite-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Directory.CreateDirectory(_dir);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，Burst 编译={Unity.Burst.BurstCompiler.IsEnabled}（Editor 托管 + Burst 混合，真机只会更快）");
                Step(CheckSeedSet);
                Step(CheckStandard);
                Step(() => _fail += FgTerritorySelfCheck.RunReachSeedSet(_report));
                Step(CheckExtremes);
                Step(CheckEscapeRegen);
                Step(CheckHashes);
                Step(CheckOldVersionSaves);
                Step(CheckReportFormat);
                Step(CheckJourneyWiring);
                Step(CheckBeamCounting);
            }
            finally
            {
                TerritoryPlanner.EnclosureForTests = null;
                WorldGenContent.ResetForTests();
                GridContent.ResetForTests();
                HomeGridService.Invalidate();
                WorldGenService.Invalidate();
                WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null && originalSlot >= 0)
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
                catch (IOException)
                {
                }
            }
            Line(_fail == 0 ? "  种子测试集：全部通过" : $"  种子测试集：{_fail} 项失败");
            return _fail;
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
            finally
            {
                TerritoryPlanner.EnclosureForTests = null;
                WorldGenContent.ResetForTests();
                HomeGridService.Invalidate();
            }
        }

        private static void Line(string s) => _report.AppendLine(s);

        private static void Fail(string s)
        {
            _fail++;
            Line("  ✗ " + s);
        }

        private static void Expect(bool ok, string s)
        {
            if (ok)
            {
                Line("  ✓ " + s);
            }
            else
            {
                Fail(s);
            }
        }

        private static int Fd(int v) => (int)WorldGenMath.FloorDiv(v, ChunkSize);

        /// <summary>失败定位：种子｜世界设置｜表面｜区块坐标｜格｜失败项（卡片“失败时报告种子、表面、区块坐标和失败项”）。</summary>
        internal static string Where(int seed, string code, string surface, int x, int y, string item) =>
            $"种子 {seed}｜设置 {code}｜表面 {surface}｜区块 ({Fd(x)},{Fd(y)})｜格 ({x},{y})｜{item}";

        private static readonly Regex CellInText = new Regex(@"\((-?\d+),\s*(-?\d+)\)");

        /// <summary>起始区校验的失败原因里带格坐标时按它定位，否则按归还核心（起始区就在核心周围）。</summary>
        private static string WhereText(int seed, string code, string why)
        {
            Match m = CellInText.Match(why);
            int x = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            int y = m.Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            return Where(seed, code, WorldGenContent.EarthSurfaceId, x, y, why);
        }

        // ── A. 种子集本身 ─────────────────────────────────────────────────────

        private static void CheckSeedSet()
        {
            bool fixedSet = Seeds.Length == 50 && Seeds.Distinct().Count() == 50 && Seeds[0] == 1 && Seeds[1] == 42 && Seeds.Any(s => s < 0)
                            && JourneySeeds.All(s => Seeds.Contains(s)) && JourneySeeds.Distinct().Count() == 3;
            bool codes = ExtremeCodes.All(c => WorldSettings.Resolve(WorldGenVersions.Current, c) != null) && ExtremeCodes.Distinct().Count() == ExtremeCodes.Length;
            var ends = new HashSet<string>();
            foreach (string c in ExtremeCodes)
            {
                for (int i = 0; i + 1 < c.Length; i += 2)
                {
                    ends.Add(c.Substring(i, 2));
                }
            }
            bool allEnds = new[] { "R0", "R2", "O0", "O2", "P0", "P2", "D0", "D2", "S0", "S1" }.All(ends.Contains);
            Expect(fixedSet && codes && allEnds,
                $"FGR-GEN-091 种子测试集固定 {Seeds.Length} 个（含旅程种子 1 / 42、正负都有、无重复），其中 {string.Join(" / ", JourneySeeds)} 跑主菜单到结局旅程；" +
                $"极端世界设置 {ExtremeCodes.Length} 组（{string.Join("、", ExtremeCodes)}）覆盖每个轴的两个取值端点（{allEnds}），都能按 v{WorldGenVersions.Current} 解析（{codes}）");
        }

        // ── B. 50 个种子 × 标准设置（FGT-GEN-003 / 004）──────────────────────────

        private sealed class SeedResult
        {
            public readonly List<string> Start = new List<string>();
            public readonly List<string> Layout = new List<string>();
            public readonly List<string> Reach = new List<string>();
            public readonly List<string> Escape = new List<string>();
            /// <summary>换过候选的锚点明细（写进报告）与“原候选其实走得到”的误判（断言为空）。</summary>
            public readonly List<string> RegenDetail = new List<string>();
            public readonly List<string> FalseRegen = new List<string>();
            public int Routes;
            public int Nests;
            public int Relics;
            public int AnchorEntries;
            public int EscapeChecked;
            public int EscapeRegen;
            public double Ms;
        }

        private static SeedResult Validate(int seed, string code, bool reach)
        {
            var r = new SeedResult();
            var sw = Stopwatch.StartNew();
            WorldGenContext ctx = FgWorldGenHomeSelfCheck.Ctx(seed, code);
            // 起始区四级保证（托管代码独立重算，与生产报告一致）。
            var reg = new FgWorldGenHomeSelfCheck.Region(ctx.Source, -5, 4);
            foreach (string why in FgWorldGenHomeSelfCheck.VerifyStart(ctx, reg, new Dictionary<string, bool>()))
            {
                r.Start.Add(WhereText(seed, code, why));
            }
            // 领地布局：方向夹角、互不重叠、不进家园区、规划层没有失败项。
            WorldPlan p = ctx.Plan;
            for (int a = 0; a < p.Territories.Count; a++)
            {
                PlannedTerritory t = p.Territories[a];
                long d = WorldGenMath.Isqrt((long)t.CenterX * t.CenterX + (long)t.CenterY * t.CenterY);
                if (d - t.OuterRadius <= ctx.Row.HomeZoneRadius)
                {
                    r.Layout.Add(Where(seed, code, WorldGenContent.EarthSurfaceId, t.CenterX, t.CenterY, $"领地 {t.Id} 进入家园区"));
                }
                for (int b = a + 1; b < p.Territories.Count; b++)
                {
                    PlannedTerritory u = p.Territories[b];
                    long dx = t.CenterX - u.CenterX;
                    long dy = t.CenterY - u.CenterY;
                    long need = (long)t.OuterRadius + u.OuterRadius;
                    if (dx * dx + dy * dy < need * need)
                    {
                        r.Layout.Add(Where(seed, code, WorldGenContent.EarthSurfaceId, t.CenterX, t.CenterY, $"领地 {t.Id} / {u.Id} 重叠"));
                    }
                    if (t.IsFaction && u.IsFaction && WorldPlan.AngleBetween(t.AngleDeg, u.AngleDeg) < ctx.Row.TerritoryMinAngle)
                    {
                        r.Layout.Add(Where(seed, code, WorldGenContent.EarthSurfaceId, t.CenterX, t.CenterY, $"领地 {t.Id} / {u.Id} 夹角 {WorldPlan.AngleBetween(t.AngleDeg, u.AngleDeg)}°"));
                    }
                }
            }
            foreach (string f in p.Failures)
            {
                r.Layout.Add(Where(seed, code, WorldGenContent.EarthSurfaceId, 0, 0, "规划失败：" + f));
            }
            // v5 起：非 Demo 锚点的粗粒度连通性判定。
            r.EscapeChecked = p.EscapeChecked;
            r.EscapeRegen = p.EscapeRegen.Count;
            int need4 = p.Anchors.Count(TerritoryPlanner.NeedsEscapeCheck);
            if (ctx.Version >= TerritoryPlanner.ReachRegenFromVersion && p.EscapeChecked != need4)
            {
                r.Escape.Add(Where(seed, code, WorldGenContent.EarthSurfaceId, 0, 0, $"粗粒度连通性判定只判了 {p.EscapeChecked}/{need4} 个锚点"));
            }
            foreach (string id in p.EscapeFailed)
            {
                PlannedAnchor a = p.FindAnchor(id);
                r.Escape.Add(Where(seed, code, WorldGenContent.EarthSurfaceId, a?.EntryX ?? 0, a?.EntryY ?? 0, $"锚点 {id} 的全部候选都被地形围住"));
            }
            if (p.EscapeRegen.Count > 0)
            {
                // 换过候选的：原候选（= 上一版本 v4 的位置）在 v4 地形上从核心真寻路——走得到 = 粗粒度判定误判（不必要地改了世界），断言为空。
                WorldGenContext old = FgWorldGenHomeSelfCheck.Ctx(seed, code, TerritoryPlanner.ReachRegenFromVersion - 1);
                NavConfig cfg = NavService.ConfigFromTuning();
                cfg.MaxExpansions = Math.Max(cfg.MaxExpansions, 400000);
                using var k = new NavKernel(cfg, NavService.TerrainTable(), true, old.Source.Params, old.Source.Rects, old.Source.Zones);
                foreach (KeyValuePair<string, int> kv in p.EscapeRegen)
                {
                    PlannedAnchor was = old.Plan.FindAnchor(kv.Key);
                    PlannedAnchor now = p.FindAnchor(kv.Key);
                    if (was == null || now == null)
                    {
                        continue;
                    }
                    TerritoryPlanner.EscapesLocally(old.Source, was, out int reached);
                    NavResult res = k.FindNow(new NavRequest { OwnerTag = 9, OwnerKey = 1, Serial = 1, Class = NavConst.ClassPlayer, Start = new int2(ctx.Core.X, ctx.Core.Y), Goal = new int2(was.EntryX, was.EntryY) },
                        new List<int2>(), false, out double _);
                    string d = Where(seed, code, WorldGenContent.EarthSurfaceId, was.EntryX, was.EntryY,
                        $"锚点 {kv.Key} 原候选（第 {was.Attempts + 1} 次）入口被围住（漫水 {reached} 格），v{old.Version} 真寻路 {res.Status} → 换到第 {now.Attempts + 1} 次候选 ({now.OriginX},{now.OriginY})");
                    r.RegenDetail.Add(d);
                    if (res.Status == NavStatus.Ok)
                    {
                        r.FalseRegen.Add(d);
                    }
                }
            }
            if (reach)
            {
                Reach(seed, code, ctx, r);
            }
            sw.Stop();
            r.Ms = sw.Elapsed.TotalMilliseconds;
            return r;
        }

        /// <summary>层级寻路内核在生成出来的地形上（与存档无关）从核心走到：每个领地中心、白潮滩头预留区、全部家园区侦察巢与遗迹点。</summary>
        private static void Reach(int seed, string code, WorldGenContext ctx, SeedResult r)
        {
            NavConfig cfg = NavService.ConfigFromTuning();
            cfg.MaxExpansions = Math.Max(cfg.MaxExpansions, 400000);
            using var k = new NavKernel(cfg, NavService.TerrainTable(), true, ctx.Source.Params, ctx.Source.Rects, ctx.Source.Zones);
            var targets = new List<(string what, int x, int y)>();
            foreach (PlannedTerritory t in ctx.Plan.Territories)
            {
                targets.Add((t.IsFaction ? "领地 " + t.Id : "白潮滩头预留区 " + t.Id, t.CenterX, t.CenterY));
            }
            List<WorldFeature> nests = WorldFeatures.HomeZoneNests(ctx);
            r.Nests = nests.Count;
            foreach (WorldFeature f in nests)
            {
                targets.Add(("侦察巢 " + f.Id, f.X, f.Y));
            }
            int hz = ctx.Row.HomeZoneRadius;
            var features = new List<WorldFeature>();
            WorldFeatures.Query(ctx, -hz, -hz, hz, hz, features);
            foreach (WorldFeature f in features.Where(x => x.Kind == WorldFeatureKind.Relic))
            {
                r.Relics++;
                targets.Add(("遗迹点 " + f.Id, f.X, f.Y));
            }
            if (code != WorldGenContent.DefaultPresetId)
            {
                // 修复轮（FGT-GEN-004“所有锚点、室内入口”）：极端设置会挪动锚点，两个室内入口、洗礼平台、白潮滩头的入口也走真寻路；
                // 标准设置下这几处由 FgTerritorySelfCheck.RunReachSeedSet 走生产校验（50 个种子），这里不重复。
                foreach (PlannedAnchor a in ctx.Plan.Anchors.Where(TerritoryPlanner.NeedsEscapeCheck))
                {
                    r.AnchorEntries++;
                    targets.Add(("锚点入口 " + a.Id, a.EntryX, a.EntryY));
                }
            }
            var pts = new List<int2>();
            foreach ((string what, int x, int y) in targets)
            {
                if (!FgWorldGenHomeSelfCheck.NearestWalkable(ctx, x, y, 40, out int gx, out int gy))
                {
                    r.Reach.Add(Where(seed, code, WorldGenContent.EarthSurfaceId, x, y, $"{what}：40 格内没有连通块 ≥ {FgWorldGenHomeSelfCheck.MinTargetPatch} 格的可走格"));
                    continue;
                }
                var req = new NavRequest
                {
                    OwnerTag = 9, OwnerKey = r.Routes + 1, Serial = 1, Class = NavConst.ClassPlayer, Flags = NavRequestFlags.None,
                    Start = new int2(ctx.Core.X, ctx.Core.Y), Goal = new int2(gx, gy),
                };
                NavResult res = k.FindNow(req, pts, false, out double _);
                r.Routes++;
                if (res.Status != NavStatus.Ok)
                {
                    r.Reach.Add(Where(seed, code, WorldGenContent.EarthSurfaceId, gx, gy, $"{what} 从核心出发走不到（寻路 {res.Status} / {res.Reason}）"));
                }
            }
        }

        private static void CheckStandard()
        {
            string code = WorldGenContent.DefaultPresetId;
            var all = new List<SeedResult>();
            foreach (int seed in Seeds)
            {
                all.Add(Validate(seed, code, true));
            }
            Report($"标准设置 × {Seeds.Length} 个种子", all, Seeds.Length);
        }

        private static void Report(string label, List<SeedResult> all, int reachSeeds)
        {
            List<string> start = all.SelectMany(x => x.Start).ToList();
            List<string> layout = all.SelectMany(x => x.Layout).ToList();
            List<string> reach = all.SelectMany(x => x.Reach).ToList();
            List<string> escape = all.SelectMany(x => x.Escape).ToList();
            int routes = all.Sum(x => x.Routes);
            Expect(start.Count == 0, $"FGT-GEN-003【{label}】起始区四级保证全部成立（托管独立逐格重算 + 生产报告）" + Tail(start));
            Expect(layout.Count == 0, $"FGT-GEN-004【{label}】四个阵营领地方向两两夹角达标、领地与白潮滩头预留区互不重叠、都不进家园区、规划层没有失败项" + Tail(layout));
            Expect(reach.Count == 0 && routes >= reachSeeds * 5,
                $"FGT-GEN-004 可达【{label}】{reachSeeds} 个种子共 {routes} 条层级寻路（与存档无关、从归还核心出发）全部走到：四个领地、白潮滩头预留区、" +
                $"家园区侦察巢 {all.Sum(x => x.Nests)} 个、家园区遗迹点 {all.Sum(x => x.Relics)} 个" +
                (all.Sum(x => x.AnchorEntries) > 0 ? $"、锚点入口（两个室内入口 / 洗礼平台 / 白潮滩头）{all.Sum(x => x.AnchorEntries)} 个" : string.Empty) + Tail(reach));
            List<string> falseRegen = all.SelectMany(x => x.FalseRegen).ToList();
            Expect(escape.Count == 0 && falseRegen.Count == 0,
                $"FGR-GEN-090【{label}】生成器 v{WorldGenVersions.Current} 粗粒度连通性判定：判定 {all.Sum(x => x.EscapeChecked)} 个非 Demo 锚点，换候选 {all.Sum(x => x.EscapeRegen)} 个" +
                $"（每个都核过：原候选在 v{TerritoryPlanner.ReachRegenFromVersion - 1} 地形上从核心真寻路走不到，误判 {falseRegen.Count} 个），全部候选都被围住 0 个" + Tail(escape) + Tail(falseRegen));
            foreach (string d in all.SelectMany(x => x.RegenDetail).Take(16))
            {
                Line("  · 换候选：" + d);
            }
            Line($"  · 【{label}】单个种子最长 {all.Max(x => x.Ms):0} ms、平均 {all.Average(x => x.Ms):0} ms（含建世界、Burst 区块生成、托管起始区重算与寻路）");
        }

        private static string Tail(List<string> fails) => fails.Count == 0 ? string.Empty : $"——失败 {fails.Count} 处：" + string.Join("；", fails.Take(12));

        // ── C. 极端世界设置组合（卡片负向 / DEBT-FG3GEN01-10 复核）────────────────

        private static void CheckExtremes()
        {
            foreach (string code in ExtremeCodes)
            {
                var all = new List<SeedResult>();
                for (int i = 0; i < Seeds.Length; i++)
                {
                    all.Add(Validate(Seeds[i], code, i < ExtremeReachSeeds));
                }
                Report($"极端 {code} × {Seeds.Length} 个种子（可达 {ExtremeReachSeeds} 个）", all, ExtremeReachSeeds);
            }
        }

        // ── D. 粗粒度连通性判定与换候选（DEBT-FG8GEN02-09）────────────────────────

        private static void CheckEscapeRegen()
        {
            const int seed = 1;
            int v = WorldGenVersions.Current;
            WorldGenContext baseCtx = FgWorldGenHomeSelfCheck.Ctx(seed);
            PlannedAnchor target = baseCtx.Plan.Anchors.FirstOrDefault(a => TerritoryPlanner.NeedsEscapeCheck(a) && (a.Row?.WaterRing ?? 0) == 0);
            if (target == null)
            {
                Fail("前提：种子 1 没有不带水环的非 Demo 锚点");
                return;
            }
            int k0 = target.Attempts;
            string id = target.Id;
            bool baseEscapes = TerritoryPlanner.EscapesLocally(baseCtx.Source, target, out int baseReached);

            // 真地形：只把原候选围上一圈悬崖。v4 不判定（旧版本路径不变）→ 锚点不动、判定函数在真实地形上判“被围住”。
            TerritoryPlanner.EnclosureForTests = a => a.Id == id && a.Attempts == k0;
            WorldGenContext v4 = FgWorldGenHomeSelfCheck.Ctx(seed, WorldGenContent.DefaultPresetId, TerritoryPlanner.ReachRegenFromVersion - 1);
            PlannedAnchor at4 = v4.Plan.FindAnchor(id);
            bool enclosed = at4 != null && at4.OriginX == target.OriginX && at4.OriginY == target.OriginY && !TerritoryPlanner.EscapesLocally(v4.Source, at4, out int encReached)
                            && v4.Plan.EscapeChecked == 0 && v4.Plan.EscapeRegen.Count == 0;
            Expect(baseEscapes && enclosed,
                $"FGR-GEN-090 判定函数：种子 1 的 {id}（第 {k0 + 1} 次候选）在真实地形上没被围住（漫水 {baseReached} 格碰到判定框边）；同一候选外围一圈悬崖 → 判“被围住”；" +
                $"v{TerritoryPlanner.ReachRegenFromVersion - 1} 不做判定（锚点不动，旧版本生成路径不变）");

            // v5：被围住 → 只对这个锚点按子种子换下一个候选，写日志；确定；新入口从核心真寻路可达。
            WorldGenContext c1 = FgWorldGenHomeSelfCheck.Ctx(seed);
            WorldGenContext c2 = FgWorldGenHomeSelfCheck.Ctx(seed);
            PlannedAnchor moved = c1.Plan.FindAnchor(id);
            bool regen = moved != null && moved.Attempts > k0 && c1.Plan.EscapeRegen.TryGetValue(id, out int from) && from == k0 + 1
                         && (moved.OriginX != target.OriginX || moved.OriginY != target.OriginY) && TerritoryPlanner.EscapesLocally(c1.Source, moved, out _);
            bool logged = c1.Plan.Log.Any(l => l.Contains(id) && l.Contains("被地形围住") && l.Contains("换第"));
            bool same = c1.Plan.FullFingerprint() == c2.Plan.FullFingerprint();
            bool othersSame = c1.Plan.Anchors.Where(a => a.Index < target.Index).All(a => baseCtx.Plan.FindAnchor(a.Id) is PlannedAnchor b && b.OriginX == a.OriginX && b.OriginY == a.OriginY);
            var single = new SeedResult();
            if (moved != null)
            {
                // 新入口从核心走得到（内核寻路，与存档无关）。
                NavConfig cfg = NavService.ConfigFromTuning();
                cfg.MaxExpansions = Math.Max(cfg.MaxExpansions, 400000);
                using var k = new NavKernel(cfg, NavService.TerrainTable(), true, c1.Source.Params, c1.Source.Rects, c1.Source.Zones);
                FgWorldGenHomeSelfCheck.NearestWalkable(c1, moved.EntryX, moved.EntryY, 40, out int gx, out int gy);
                NavResult res = k.FindNow(new NavRequest { OwnerTag = 9, OwnerKey = 1, Serial = 1, Class = NavConst.ClassPlayer, Start = new int2(0, 0), Goal = new int2(gx, gy) },
                    new List<int2>(), false, out double _);
                if (res.Status != NavStatus.Ok)
                {
                    single.Reach.Add($"{res.Status}/{res.Reason}");
                }
            }
            Expect(regen && logged && same && othersSame && single.Reach.Count == 0,
                $"FGR-GEN-090 局部重生成（v{v}）：{id} 的原候选被围住 → 只对它按子种子换到第 {moved?.Attempts + 1} 次候选 ({moved?.OriginX},{moved?.OriginY})（{regen}），写了日志（{logged}），" +
                $"同一种子两次结果相同（{same}），排在它前面的锚点不动（{othersSame}），新入口从核心真寻路可达（{single.Reach.Count == 0}）");

            // 全部候选都被围住 → 回到原位置、写进失败表与日志（运行时可达校验写原因、不软锁）；种子测试集按失败报出。
            TerritoryPlanner.EnclosureForTests = a => a.Id == id;
            WorldGenContext all = FgWorldGenHomeSelfCheck.Ctx(seed);
            PlannedAnchor back = all.Plan.FindAnchor(id);
            bool gaveUp = all.Plan.EscapeFailed.Contains(id) && back != null && back.OriginX == target.OriginX && back.OriginY == target.OriginY && back.Attempts == k0
                          && !all.Plan.EscapeRegen.ContainsKey(id) && all.Plan.Log.Any(l => l.Contains(id) && l.Contains("都被地形围住"));
            SeedResult rep = Validate(seed, WorldGenContent.DefaultPresetId, false);
            bool reported = rep.Escape.Any(x => x.Contains("种子 1｜") && x.Contains("表面 earth") && x.Contains("区块 (") && x.Contains(id));
            TerritoryPlanner.EnclosureForTests = null;
            Expect(gaveUp && reported,
                $"负向：{id} 的 {all.Row.PlanMaxAttempts} 次候选全部被围住 → 回到原位置（{gaveUp}），种子测试集报失败并带种子 / 表面 / 区块 / 失败项（{(rep.Escape.Count > 0 ? rep.Escape[0] : "无")}）");
        }

        // ── E. 种子 × 生成器版本回归哈希（FGT-GEN-006 / FGR-GEN-061）────────────────

        internal struct HashRow
        {
            public int Seed;
            public int Version;
            public string Part;
            public ulong Hash;
        }

        /// <summary>一个（种子, 版本）的样本：规划层全指纹、核心区块、第一个领地中心区块、白潮滩头预留区中心区块、第 3 个锚点（v3 起）原点区块、两个室内表面。</summary>
        internal static List<HashRow> Sample(int seed, int version)
        {
            var rows = new List<HashRow>();
            WorldGenContext ctx = FgWorldGenHomeSelfCheck.Ctx(seed, WorldGenContent.DefaultPresetId, version);
            rows.Add(new HashRow { Seed = seed, Version = version, Part = "plan", Hash = WorldGenInputs.Digest(ctx.Plan.FullFingerprint()) });
            var chunks = new List<(int cx, int cy)> { (0, 0) };
            WorldPlan p = ctx.Plan;
            if (p.Territories.Count > 0)
            {
                chunks.Add((Fd(p.Territories[0].CenterX), Fd(p.Territories[0].CenterY)));
                PlannedTerritory reserve = p.Territories.FirstOrDefault(t => !t.IsFaction) ?? p.Territories[p.Territories.Count - 1];
                chunks.Add((Fd(reserve.CenterX), Fd(reserve.CenterY)));
            }
            chunks.Add(p.Anchors.Count > 2 ? (Fd(p.Anchors[2].OriginX), Fd(p.Anchors[2].OriginY)) : (7, -5));
            foreach ((int cx, int cy) in chunks)
            {
                rows.Add(new HashRow { Seed = seed, Version = version, Part = $"earth:{cx},{cy}", Hash = ChunkHash(ctx.Source, cx, cy) });
            }
            foreach (Surface s in WorldGenContent.Surfaces.Where(WorldGenContent.IsInterior))
            {
                var src = new WorldTerrainSource(seed, version, ctx.Settings, s, new GridCell(0, 0), ChunkSize, null);
                rows.Add(new HashRow { Seed = seed, Version = version, Part = $"{s.Id}:0,0", Hash = ChunkHash(src, 0, 0) });
            }
            return rows;
        }

        private static ulong ChunkHash(IGridTerrainSource src, int cx, int cy)
        {
            var t = new byte[ChunkSize * ChunkSize];
            var p = new byte[ChunkSize * ChunkSize];
            src.FillChunk(cx, cy, ChunkSize, t, p);
            return WorldGenKernel.Hash64(t, p);
        }

        private static string BaselinePath => Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", BaselineFile);

        internal static List<HashRow> LoadBaseline()
        {
            var rows = new List<HashRow>();
            if (!File.Exists(BaselinePath))
            {
                return rows;
            }
            foreach (string line in File.ReadAllLines(BaselinePath))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string[] f = line.Split('\t');
                if (f.Length == 4)
                {
                    rows.Add(new HashRow
                    {
                        Seed = int.Parse(f[0], CultureInfo.InvariantCulture), Version = int.Parse(f[1], CultureInfo.InvariantCulture), Part = f[2],
                        Hash = ulong.Parse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    });
                }
            }
            return rows;
        }

        /// <summary>按基准逐个比较；返回失败项（种子 / 版本 / 表面 / 区块 / 基准 / 实际），以及基准里缺的版本。</summary>
        internal static List<string> Compare(List<HashRow> baseline, IEnumerable<int> versions, IEnumerable<int> seeds)
        {
            var wrong = new List<string>();
            var lookup = baseline.GroupBy(r => (r.Seed, r.Version, r.Part)).ToDictionary(g => g.Key, g => g.First().Hash);
            foreach (int v in versions)
            {
                if (!baseline.Any(r => r.Version == v))
                {
                    wrong.Add($"生成器 v{v} 没有种子测试集基准（生成器新版本必须补一组：菜单“QA/探针/FG 种子测试集：补写新版本基准”，旧版本行不许改）");
                    continue;
                }
                foreach (int seed in seeds)
                {
                    foreach (HashRow a in Sample(seed, v))
                    {
                        string[] sp = a.Part.Split(':');
                        string where = sp.Length == 2 ? $"表面 {(sp[0] == "earth" ? WorldGenContent.EarthSurfaceId : sp[0])}｜区块 ({sp[1]})" : "规划层";
                        if (!lookup.TryGetValue((seed, v, a.Part), out ulong h))
                        {
                            wrong.Add($"种子 {seed}｜v{v}｜{where}：缺基准（实际 0x{a.Hash:X16}）");
                        }
                        else if (h != a.Hash)
                        {
                            wrong.Add($"种子 {seed}｜v{v}｜{where}：基准 0x{h:X16} 实际 0x{a.Hash:X16}");
                        }
                    }
                }
            }
            return wrong;
        }

        private static void CheckHashes()
        {
            List<HashRow> baseline = LoadBaseline();
            int[] versions = Enumerable.Range(1, WorldGenVersions.Current).ToArray();
            var sw = Stopwatch.StartNew();
            List<string> wrong = Compare(baseline, versions, Seeds);
            sw.Stop();
            int samples = baseline.Count(r => r.Version >= 1 && r.Version <= WorldGenVersions.Current);
            bool bound = versions.All(v => Seeds.All(s => baseline.Any(r => r.Seed == s && r.Version == v)));
            Expect(wrong.Count == 0 && bound && samples >= Seeds.Length * versions.Length * 5,
                $"FGT-GEN-006 回归哈希（FGR-GEN-061）：{Seeds.Length} 个种子 × 生成器 v1～v{WorldGenVersions.Current} 共 {samples} 个样本（规划层全指纹、核心 / 领地 / 白潮滩头 / 锚点区块、室内表面）" +
                $"与 {BaselineFile} 逐个一致，每个版本都有基准（绑定生成器版本号，{bound}；{sw.ElapsedMilliseconds} ms）" + Tail(wrong));

            // v5 与 v4：不需要换候选的种子样本逐个相同（v5 只多了判定）；换过候选的种子只有规划层与锚点附近不同，家园核心区块不变。
            int cur = WorldGenVersions.Current;
            var diff = new List<string>();
            var regenSeeds = new List<int>();
            foreach (int seed in Seeds)
            {
                bool regen = FgWorldGenHomeSelfCheck.Ctx(seed).Plan.EscapeRegen.Count > 0;
                Dictionary<string, ulong> a = baseline.Where(r => r.Seed == seed && r.Version == cur).ToDictionary(r => r.Part, r => r.Hash);
                Dictionary<string, ulong> b = baseline.Where(r => r.Seed == seed && r.Version == cur - 1).ToDictionary(r => r.Part, r => r.Hash);
                bool same = a.Count > 0 && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out ulong h) && h == kv.Value);
                bool coreSame = a.TryGetValue("earth:0,0", out ulong c5) && b.TryGetValue("earth:0,0", out ulong c4) && c5 == c4;
                if (regen)
                {
                    regenSeeds.Add(seed);
                    if (same || !coreSame)
                    {
                        diff.Add($"种子 {seed}（换过候选）：与 v{cur - 1} 逐个相同 {same}、核心区块相同 {coreSame}");
                    }
                }
                else if (!same)
                {
                    diff.Add($"种子 {seed}（没换候选）：样本与 v{cur - 1} 不同");
                }
            }
            Expect(cur == TerritoryPlanner.ReachRegenFromVersion && diff.Count == 0,
                $"生成器 v{cur} 对 v{cur - 1}：{Seeds.Length - regenSeeds.Count} 个不需要换候选的种子样本逐个相同（只多了判定）；{regenSeeds.Count} 个换过候选的种子（{string.Join("、", regenSeeds)}）" +
                $"只有规划层 / 锚点附近不同、家园核心区块不变（旧 v{cur - 1} 存档继续按 v{cur - 1} 生成，见旧版本存档一步）" + Tail(diff));

            // 负向 1：直接改已发布版本 v1 的参数行而不升版本 → 回归哈希报出种子 / 版本 / 表面 / 区块。
            WorldGenContent.OverrideForTests(versions: new TbWorldGenVersion(FgWorldGenSelfCheck.VersionBuf(WorldGenContent.Versions.ToList(), ("cliffThreshold", 0.70f))));
            List<string> caught = Compare(baseline, new[] { 1 }, Seeds.Take(3));
            WorldGenContent.ResetForTests();
            Expect(caught.Count > 0 && caught.Any(x => x.Contains("｜v1｜表面 earth｜区块 (")),
                $"负向：不升版本直接把 v1 的悬崖阈值 0.78 改成 0.70 → 回归哈希报出 {caught.Count} 处（例：{caught.FirstOrDefault()}）");

            // 负向 2：生成器新版本没补基准 → 被拦（版本绑定）。
            List<string> missing = Compare(baseline.Where(r => r.Version != cur).ToList(), new[] { cur }, Seeds.Take(1));
            Expect(missing.Count == 1 && missing[0].Contains($"v{cur} 没有种子测试集基准"),
                $"负向：去掉 v{cur} 的基准行 → 报“生成器 v{cur} 没有种子测试集基准”（{missing.FirstOrDefault()}）");
        }

        /// <summary>只补基准里还没有的生成器版本（旧版本行一律不动）。batchmode：bash tools/unity-validate.sh GameLogic.EditorTools.FgSeedSuiteSelfCheck.WriteMissingBaseline</summary>
        [MenuItem("QA/探针/FG 种子测试集：补写新版本基准")]
        public static void WriteMissingBaseline()
        {
            int code = 0;
            try
            {
                ConfigSystem.Instance.Load();
                GridContent.Reload();
                WorldGenContent.Reload();
                List<HashRow> have = LoadBaseline();
                var add = new StringBuilder();
                if (!File.Exists(BaselinePath))
                {
                    add.AppendLine("# FG15-QA-03 种子测试集回归基准：种子\t生成器版本\t样本（plan = 规划层全指纹；表面:区块）\t哈希（FNV / WorldGenKernel.Hash64，16 进制）。");
                    add.AppendLine("# 只追加新版本，已有行永远不许改（改了生成算法或已发布参数而不升版本 = FgSeedSuiteSelfCheck 失败）。");
                }
                for (int v = 1; v <= WorldGenVersions.Current; v++)
                {
                    if (have.Any(r => r.Version == v))
                    {
                        continue;
                    }
                    add.AppendLine($"# {DateTime.Now:yyyy-MM-dd} 补生成器 v{v}（{Seeds.Length} 个种子）");
                    foreach (int seed in Seeds)
                    {
                        foreach (HashRow r in Sample(seed, v))
                        {
                            add.Append(r.Seed.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(r.Version.ToString(CultureInfo.InvariantCulture)).Append('\t')
                               .Append(r.Part).Append('\t').AppendLine(r.Hash.ToString("X16", CultureInfo.InvariantCulture));
                        }
                    }
                }
                File.AppendAllText(BaselinePath, add.ToString().Replace("\r\n", "\n"));
                Debug.Log($"[种子测试集] 基准已补写：{BaselinePath}（追加 {add.Length} 字符）");
            }
            catch (Exception e)
            {
                Debug.LogError("[种子测试集] 补写基准失败：" + e);
                code = 1;
            }
            finally
            {
                WorldGenContent.ResetForTests();
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }

        // ── F. 旧生成器版本的存档（卡片负向 / FGT-GEN-006）─────────────────────────

        private static void CheckOldVersionSaves()
        {
            // 修复轮：前一步嵌套跑的 FgTerritorySelfCheck 收尾曾把存档目录覆盖置 null，这里的存档落进了玩家真实目录（campaign_slot6.json）——写盘前先确认还在本段临时目录。
            string saveDir = CampaignSaveService.SaveDirectory;
            if (!string.Equals(Path.GetFullPath(saveDir), Path.GetFullPath(_dir), StringComparison.OrdinalIgnoreCase))
            {
                Expect(false, $"FGT-GEN-006 旧生成器版本的存档：存档目录 {saveDir} 不是本段的临时目录 {_dir}（会写进玩家真实存档目录），不写盘");
                return;
            }
            List<HashRow> baseline = LoadBaseline();
            var problems = new List<string>();
            int n = 0;
            for (int v = 1; v <= WorldGenVersions.Current; v++)
            {
                int seed = Seeds[(v * 7) % Seeds.Length];
                HomeGridService.Invalidate();
                WorldGenService.Invalidate();
                CampaignState s = CampaignState.CreateNew("seedsuite-" + seed, "Standard", seed);
                WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(v, WorldGenContent.DefaultPresetId));
                CampaignFgStateDomains.EnsureAll(s);
                SaveResult sv = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
                HomeGridService.Invalidate();
                WorldGenService.Invalidate();
                LoadResult ld = CampaignSaveService.Load(Slot);
                if (!sv.Success || !ld.Success)
                {
                    problems.Add($"v{v} 种子 {seed}：存 {sv.Success} / 读 {ld.Success}（{ld.Message}）");
                    continue;
                }
                n++;
                WorldGenContext ctx = WorldGenService.ContextFor(ld.State);
                if (ld.State.World.GeneratorVersion != v || ctx.Version != v)
                {
                    problems.Add($"v{v} 种子 {seed}：读回版本 {ld.State.World.GeneratorVersion} / 生成上下文 v{ctx.Version}");
                    continue;
                }
                foreach (HashRow r in baseline.Where(x => x.Seed == seed && x.Version == v && x.Part.StartsWith("earth:", StringComparison.Ordinal)))
                {
                    string[] xy = r.Part.Substring(6).Split(',');
                    int cx = int.Parse(xy[0], CultureInfo.InvariantCulture);
                    int cy = int.Parse(xy[1], CultureInfo.InvariantCulture);
                    ulong got = ChunkHash(ctx.Source, cx, cy);
                    if (got != r.Hash)
                    {
                        problems.Add($"种子 {seed}｜v{v}｜表面 earth｜区块 ({cx},{cy})：读档后未修改区块 0x{got:X16} ≠ v{v} 基准 0x{r.Hash:X16}");
                    }
                }
                bool planOk = baseline.Any(x => x.Seed == seed && x.Version == v && x.Part == "plan" && x.Hash == WorldGenInputs.Digest(ctx.Plan.FullFingerprint()));
                if (!planOk)
                {
                    problems.Add($"种子 {seed}｜v{v}｜规划层：读档后规划层与 v{v} 基准不同");
                }
            }
            Expect(problems.Count == 0 && n == WorldGenVersions.Current,
                $"FGT-GEN-006 旧生成器版本的存档：v1～v{WorldGenVersions.Current} 各存一份新档再读回，存档记录的版本不变、按那个版本生成，未修改区块与规划层与该版本的种子测试集基准一致（{n} 份；存档目录 = 本段临时目录，不碰玩家真实槽位）" + Tail(problems));
        }

        // ── G. 失败报告（卡片“失败时报告种子、表面、区块坐标和失败项”）─────────────────

        private static void CheckReportFormat()
        {
            // 起始区保证不满足：把油井保证收紧到装不下 → 报种子 / 设置 / 表面 / 区块 / 失败项。
            List<(string item, string terrain, int radius, int cells, int sq, int stamp)> rows = FgWorldGenHomeSelfCheck.RealGuarantees()
                .Select(r => r.item == "ruin" ? (r.item, r.terrain, 5, 60, 4, 4) : r).ToList();
            WorldGenContent.OverrideForTests(guarantees: new TbWorldStartGuarantee(FgWorldGenHomeSelfCheck.GuaranteeBuf(rows)));
            SeedResult bad = Validate(Seeds[2], WorldGenContent.DefaultPresetId, false);
            WorldGenContent.ResetForTests();
            string s1 = bad.Start.FirstOrDefault() ?? string.Empty;
            bool startOk = bad.Start.Count > 0 && s1.Contains($"种子 {Seeds[2]}｜设置 default｜表面 earth｜区块 (") && s1.Contains("ruin");

            // 可达失败：v4（不做判定）把一个室内入口围死 → 内核寻路报种子 / 表面 / 区块 / 失败项；同一世界的 v5 自动换了候选、不报。
            WorldGenContext c = FgWorldGenHomeSelfCheck.Ctx(Seeds[2]);
            PlannedAnchor a = c.Plan.Anchors.First(x => TerritoryPlanner.NeedsEscapeCheck(x) && (x.Row?.WaterRing ?? 0) == 0);
            string id = a.Id;
            TerritoryPlanner.EnclosureForTests = x => x.Id == id && x.Attempts == a.Attempts;
            WorldGenContext v4 = FgWorldGenHomeSelfCheck.Ctx(Seeds[2], WorldGenContent.DefaultPresetId, TerritoryPlanner.ReachRegenFromVersion - 1);
            TerritoryPlanner.EnclosureForTests = null;
            PlannedAnchor a4 = v4.Plan.FindAnchor(id);
            var r = new SeedResult();
            NavConfig cfg = NavService.ConfigFromTuning();
            cfg.MaxExpansions = Math.Max(cfg.MaxExpansions, 400000);
            using (var k = new NavKernel(cfg, NavService.TerrainTable(), true, v4.Source.Params, v4.Source.Rects, v4.Source.Zones))
            {
                NavResult res = k.FindNow(new NavRequest { OwnerTag = 9, OwnerKey = 1, Serial = 1, Class = NavConst.ClassPlayer, Start = new int2(0, 0), Goal = new int2(a4.EntryX, a4.EntryY) },
                    new List<int2>(), false, out double _);
                if (res.Status != NavStatus.Ok)
                {
                    r.Reach.Add(Where(Seeds[2], WorldGenContent.DefaultPresetId, WorldGenContent.EarthSurfaceId, a4.EntryX, a4.EntryY, $"锚点 {id} 从核心出发走不到（寻路 {res.Status}）"));
                }
            }
            string s2 = r.Reach.FirstOrDefault() ?? string.Empty;
            bool reachOk = s2.Contains($"区块 ({Fd(a4.EntryX)},{Fd(a4.EntryY)})") && s2.Contains(id);
            Expect(startOk && reachOk,
                $"负向：失败报告带种子、世界设置、表面、区块坐标和失败项——起始区保证放不下（“{s1}”）；入口被地形围死时从核心走不到（“{s2}”）");
        }

        // ── I. DEBT-EMGREXIT-02：FGJ-EMG-R 切割束步按攻击结果事件计段（出手者 = T 车）──────────────

        private static void CheckBeamCounting()
        {
            var squad = new RegionSquadCommandSystem();
            CombatEvent Outcome(CombatFireResult r) => new CombatEvent { Kind = CombatEventKind.AttackOutcome, Code = (byte)r, Unit = 11, Other = 99 };
            squad.OnKernelEvent(Outcome(CombatFireResult.Ok), 7, "enemy", null);
            squad.OnKernelEvent(Outcome(CombatFireResult.StillAiming), 7, "enemy", null);
            squad.OnKernelEvent(Outcome(CombatFireResult.Launched), 7, "enemy", null); // 飞行弹体“已发射”不是命中
            squad.OnKernelEvent(Outcome(CombatFireResult.Covered), 7, "enemy", null); // 被残骸挡下不是命中
            squad.OnKernelEvent(Outcome(CombatFireResult.Ok), 8, "enemy", null); // 别的出手者打同一个目标
            bool counted = squad.HitOutcomes(7) == 2 && squad.HitOutcomes(8) == 1 && squad.HitOutcomes(9) == 0;
            string src = File.ReadAllText(Path.Combine(Application.dataPath, "Editor/JourneyBots/FgjEmgRJourney.cs"));
            int at = src.IndexOf("private static StepOutcome TickBeam", StringComparison.Ordinal);
            string body = at >= 0 ? src.Substring(at, Math.Min(2600, src.Length - at)) : string.Empty;
            bool journey = body.Contains("HitOutcomes(c.GetInt(\"m.t\"))") && !body.Contains("beam.last") && body.Contains("n < 5 || n > 7");
            Expect(counted && journey,
                $"DEBT-EMGREXIT-02：编队攻击“命中”结果事件按出手者计数（同一台 2 次命中计 2、已发射 / 被挡下不计、别的出手者另计：{counted}）；FGJ-EMG-R 切割束步改按 T 车的命中结果事件计段、" +
                $"不再按目标掉血计，窗口仍是 5～7 段（{journey}；旅程复跑随 FG15-E2E-01 整套旧旅程）");
        }

        // ── H. 3 个种子的主菜单到结局旅程 + 极端设置旅程（FGR-GEN-091 / FGT-GEN-009）──────────

        private static void CheckJourneyWiring()
        {
            JourneyDef m13 = JourneyCatalog.Create(FgjM13Journey.Id);
            var defs = JourneySeeds.Select(s => (s, def: JourneyCatalog.Create(FgjM13Journey.SeedJourneyId(s)))).ToList();
            bool steps = m13 != null && defs.All(d => d.def != null && d.def.Seed == d.s && d.def.Steps.Select(x => x.Id).SequenceEqual(m13.Steps.Select(x => x.Id))
                                                     && d.def.Steps.Last().Id == m13.Steps.Last().Id);
            bool ending = m13 != null && m13.Steps.Any(x => x.Id == "menu_new") && m13.Steps.Any(x => x.Id == "e_stats") && m13.Steps.Any(x => x.Id == "e_continue");
            bool extreme = JourneyCatalog.Create(FgjGenExtremeJourney.Id) is JourneyDef g && g.Steps.Count > 40;
            string script = Path.Combine(FgProductionSelfCheck.LocateRepo(), "tools", "seed-suite.sh");
            string text = File.Exists(script) ? File.ReadAllText(script) : string.Empty;
            bool oneClick = text.Contains(SegmentName) && text.Contains(MarchSegmentName) && JourneySeeds.All(s => text.Contains(FgjM13Journey.SeedJourneyId(s)))
                            && text.Contains(FgjGenExtremeJourney.Id);
            // 修复轮：旅程“种子”一步（JourneyCommon.TickSeed）只认 FgWorldGenSelfCheck.Baseline，当前版本缺哪个旅程种子的地球表面基准，那条旅程第 4 步必失败（v3 / v4 / v5 上线三次漏补）。
            int v = WorldGenVersions.Current;
            List<int> noBase = JourneySeeds.Where(s => !FgWorldGenSelfCheck.Baseline.Any(b => b.seed == s && b.version == v && b.surface == WorldGenContent.EarthSurfaceId)).ToList();
            Expect(noBase.Count == 0,
                $"FGR-GEN-061：旅程种子 {string.Join(" / ", JourneySeeds)} 在生成器 v{v} 下都有旅程“种子”一步用的地球表面区块基准（FgWorldGenSelfCheck.Baseline）" +
                (noBase.Count == 0 ? string.Empty : $"——缺：种子 {string.Join(" / ", noBase)}"));
            Expect(steps && ending && extreme && oneClick,
                $"FGR-GEN-091 / FGT-GEN-009：3 个种子（{string.Join(" / ", JourneySeeds)}）的主菜单到结局旅程 {string.Join("、", JourneySeeds.Select(FgjM13Journey.SeedJourneyId))} 复用 FGJ-M13 同一套步骤（{steps}，" +
                $"主菜单“新建”→ 结算 →“继续”：{ending}）、种子各自生效；极端设置第一幕旅程 {FgjGenExtremeJourney.Id} 在册（{extreme}）；一键脚本 tools/seed-suite.sh 列全两段自检与这 4 条旅程（{oneClick}）");
        }
    }
}
