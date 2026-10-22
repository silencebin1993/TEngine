using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using BinGames.Sim.WorldGen;
using GameConfig;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using Luban;
using TEngine;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG8-GEN-02 阵营领地与室内场地（FG17 FGR-GEN-021～024、040、042、090；FG08 FGR-EXP-020；验收 FGT-GEN-004 / 006）的自检——起真实系统、断言行为：
    /// A 数据（源数据 = 运行时表、文本键、调参、钩子、图鉴）；B 生成器版本（v3 新增、v1 / v2 不变、旧版本存档：未修改区块哈希一致、没有领地内容）；
    /// C 规划层规则（3 个种子：确定性、锚点在领地里且互不重叠、平整范围与过渡带、地貌风格、领地内危害区；冲突时确定性重试的负向）；
    /// D 可达性 FGT-GEN-004（3 个种子：全部锚点、室内入口、白潮预留区从家园出发可达；Demo 远征锚点 = 模板入口）；
    /// E 领地内容（据点越往中心越密越强、巡逻、净空、访问顺序无关、开局登记幂等、外延区按需登记且与登记时机无关）；
    /// F 资源缓存与旧终端（揭开发现、派机器打开 / 读取、奖励只发一次）；G 室内入口保护（复用建造校验、原因、传送带不拦）；
    /// H 室内场地（进入前自动存档、进出与过渡、同时运行、暂停与 0.5x～3x、存读档、负向）；I 领地名称提示与远征队“进入领地”提醒；
    /// J 检查单“路线经过危害”；K 远征地点内部覆盖按锚点（开关两种模式、正负向）；L Demo 模板回归；M 性能；O 界面布局探针。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgTerritorySelfCheck）；单段：bash tools/unity-validate.sh --segment FgTerritorySelfCheck。
    /// </summary>
    public static class FgTerritorySelfCheck
    {
        private const int Slot = 7;
        private const int RunSlot = 8;
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        /// <summary>FGR-GEN-091：本 Story 的种子集（卡片“至少 3 个测试种子各跑一次”）。</summary>
        private static readonly int[] Seeds = { 1, 42, 20261017 };
        private static StringBuilder _report;
        private static int _pass;
        private static int _fail;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 阵营领地与室内场地")]
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

        /// <summary>
        /// 一次性探针（不在全量自检里）：种子测试集（50 个）× 标准设置，6 个锚点从家园出发的层级寻路可达——DEBT-FG8GEN02-06 / -09 的风险摸底，结果写进证据。
        /// 单独跑：bash tools/unity-validate.sh GameLogic.EditorTools.FgTerritorySelfCheck.ProbeReachSeedSet（约 2 分钟）。
        /// </summary>
        [MenuItem("BinGames/QA/探针/FG 锚点可达（种子测试集）")]
        public static void ProbeReachSeedSet()
        {
            _probeOnly = true;
            try
            {
                RunFromMenu();
            }
            finally
            {
                _probeOnly = false;
            }
        }

        private static bool _probeOnly;

        private static void ProbeReach()
        {
            var failed = new List<string>();
            int moved = 0;
            double worst = 0;
            foreach (int seed in FgWorldGenHomeSelfCheck.SeedSet)
            {
                CampaignState s = NewWorld(seed);
                WorldPlan plan = WorldGenService.PlanFor(s);
                var sw = Stopwatch.StartNew();
                bool all = TerritoryAnchorChecks.EnsureAll(s);
                sw.Stop();
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                foreach (PlannedAnchor a in plan.Anchors.Where(x => x.Row.ReachCheck == 1))
                {
                    int st = TerritoryAnchorChecks.StateOf(s, a);
                    moved += st == TerritoryAnchorChecks.StateMoved ? 1 : 0;
                    if (st != TerritoryAnchorChecks.StateOk && st != TerritoryAnchorChecks.StateMoved)
                    {
                        failed.Add($"种子 {seed} {a.Id}:{st}");
                    }
                }
                if (!all)
                {
                    failed.Add($"种子 {seed} 有锚点没校验完");
                }
            }
            Expect(failed.Count == 0,
                $"P1 探针 FGR-GEN-091：{FgWorldGenHomeSelfCheck.SeedSet.Length} 个种子 × 标准设置，全部锚点从家园出发可达（挪位 {moved} 个；单个种子最长 {worst:0} ms）" +
                (failed.Count == 0 ? string.Empty : $"——走不到 {failed.Count} 处：" + string.Join("，", failed.Take(10))));
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            PerfLines.Clear();
            Line("\n[阵营领地与室内场地] 领地模板、手工锚点与可达、Demo 模板、据点 / 缓存 / 终端 / 外延区、室内进出、名称提示、旧版本存档（FG8-GEN-02）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgterritory-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                BuildingOps.Reload();
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                GameRoot.BindWorldProviders();

                if (_probeOnly)
                {
                    Step(ProbeReach);
                    return _fail;
                }
                Step(CheckData);
                Step(CheckVersions);
                Step(CheckPlanRules);
                Step(CheckAnchorConflict);
                Step(CheckPlanSeedSet);
                Step(CheckReachability);
                Step(CheckReachFailedState);
                Step(CheckContent);
                Step(CheckOuterZone);
                Step(CheckLazyPatrols);
                Step(CheckPois);
                Step(CheckEntranceProtect);
                Step(CheckInterior);
                Step(CheckInteriorSpeedAndSave);
                Step(CheckInteriorRollbackAndDataCenter);
                Step(CheckBannerAndCue);
                Step(CheckChecklistHazard);
                Step(CheckSiteCoverageRoot);
                Step(CheckDemoTemplates);
                Step(CheckPerf);
                Step(CheckLayoutProbe);
                foreach (string p in PerfLines)
                {
                    Line("  · " + p);
                }
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                ExpeditionChecklistService.HazardProbe = ExpeditionChecklistService.DefaultHazardProbe;
                WorldGenContent.ResetForTests();
                CameraDirector.RealDeltaTime = originalDelta;
                InputRouter.DebugSetReader(null);
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                WorldPlacesHudUIToolkit.InWorldOverrideForTests = false;
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾卸载世界抛异常：" + e.Message);
                }
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
                    if (Directory.Exists(_dir))
                    {
                        Directory.Delete(_dir, true);
                    }
                }
                catch (IOException)
                {
                }
            }
            Line(_fail == 0 ? $"  阵营领地与室内场地：全部 {_pass} 项通过" : $"  阵营领地与室内场地：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ── A 数据 ──────────────────────────────────────────────────────────────

        private static string Py(float v)
        {
            string r = v.ToString("R", CultureInfo.InvariantCulture);
            return r.Contains(".") || r.Contains("E") ? r : r + ".0";
        }

        private static void CheckData()
        {
            Tables t = ConfigSystem.Instance.Tables;
            string root = F.LocateRepo();
            (int code, string output) = F.RunPython(root,
                "-c \"import sys;sys.path.insert(0,'tools/cell_tables');import fgdata_territory as p;print('\\n'.join(p.dump_lines()))\"");
            var src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("TTP\t") || l.StartsWith("ANC\t") || l.StartsWith("ATP\t")).ToList();
            var rt = t.TbTerritoryTemplate.DataList.Select(r => string.Join("\t", "TTP", r.Key, r.Set, r.TerritoryId, r.StyleTerrain1, Py(r.StyleBonus1), r.StyleTerrain2, Py(r.StyleBonus2),
                    r.HazardZones, r.HazardRadiusMin, r.HazardRadiusMax, Py(r.HazardRadialMax), Py(r.OutpostEdgeChance), Py(r.OutpostCenterChance), r.OutpostTierEdge, r.OutpostTierCenter,
                    r.GarrisonPerTier, r.PatrolEvery, r.PatrolRadius, Py(r.CacheChance), r.CacheScrap, Py(r.CacheSeconds), Py(r.TerminalChance), r.TerminalTech, Py(r.TerminalSeconds),
                    r.TerminalLoreKeys, Py(r.RelicFactor), Py(r.SideChance), r.AnchorClearance, r.IntroKey))
                .Concat(t.TbAnchorSite.DataList.Select(a => string.Join("\t", "ANC", a.Key, a.Set, a.Id, a.TerritoryId, a.Kind, a.TemplateId, Py(a.RadialMin), Py(a.RadialMax), a.AngleMode,
                    a.JitterDeg, a.FootprintRadius, a.BlendWidth, a.SurfaceId, a.EntryRadius, a.ProtectRadius, a.MinGap, a.ReachCheck, a.MapAct, a.NameKey, a.DescKey)))
                .Concat(t.TbAnchorTemplatePoint.DataList.Select(p => string.Join("\t", "ATP", p.Key, p.Template, p.PointId, p.Kind, Py(p.LocalX), Py(p.LocalY), Py(p.Radius), p.NameKey)))
                .ToList();
            bool table = code == 0 && src.Count == rt.Count && src.Count >= 40 && src.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(rt.OrderBy(x => x, StringComparer.Ordinal));
            Expect(table, $"A1 源数据 fgdata_territory.py 与运行时三张新表（领地模板 {t.TbTerritoryTemplate.DataList.Count} / 手工锚点 {t.TbAnchorSite.DataList.Count} / 模板点位 {t.TbAnchorTemplatePoint.DataList.Count}）逐字段一致" +
                                 (table ? string.Empty : $"（源 {src.Count} 行 / 运行时 {rt.Count} 行，python 退出码 {code}）"));

            WorldGenVersion v3 = WorldGenContent.Version(3);
            Expect(WorldGenVersions.Current == 3 && v3.TemplateSet == "tt3" && v3.AnchorSet == "as3" && v3.OuterZoneDistance == 2500 && v3.TerritorySet == "t1"
                   && WorldGenContent.Version(2).TemplateSet == WorldGenContent.NoSet && WorldGenContent.Version(1).AnchorSet == WorldGenContent.NoSet
                   && WorldGenContent.TemplatesFor(v3).Count == 4 && WorldGenContent.AnchorsFor(v3).Count == 6,
                $"A2 生成器 v3 入表并成为新战役版本：模板 tt3（四个阵营领地）、锚点 as3（{WorldGenContent.AnchorsFor(v3).Count} 个）、外延区起点 {v3.OuterZoneDistance} 格；v1 / v2 行这些列都是 none（旧世界不读）");

            var keys = new List<string>
            {
                "ui.territory.banner.enter", "ui.territory.banner.leave", "ui.territory.banner.belt", "ui.territory.banner.outer", "ui.territory.outer", "exp.cue.territory", "exp.cue.territory_detail",
                "anchor.hover.title", "anchor.hover.entry", "anchor.hover.indoor", "anchor.hover.demo", "anchor.hover.boss", "anchor.hover.moved", "anchor.hover.unreachable",
                "ui.interior.title", "ui.interior.enter", "ui.interior.exit", "ui.interior.close", "ui.interior.near", "ui.interior.exit_near", "ui.interior.info", "ui.interior.protect",
                "ui.interior.transition.enter", "ui.interior.transition.exit", "interior.reason.none_near", "interior.reason.exit_none_near", "interior.reason.unreachable",
                "interior.reason.not_found", "interior.reason.out_of_coverage", "interior.reason.legacy_world", "interior.reason.save_failed", "interior.reason.marching", "interior.reason.busy", "ui.interior.interrupt",
                "interior.notify.entered", "interior.notify.exited", "interior.notify.wiped", "interior.cue.tag", "interior.cue.enter", "interior.cue.exit", "grid.reason.entrance_protect",
                "world.poi.cache", "world.poi.terminal", "world.poi.outpost", "world.poi.scatter_outpost", "cache.notify.found", "terminal.notify.found", "cache.notify.opened",
                "terminal.notify.read", "cache.hover.available", "cache.hover.opened", "terminal.hover.available", "terminal.hover.read", "cache.work.label", "terminal.work.label",
                "ui.map.cache.opened", "ui.map.terminal.read", "ui.map.anchor.moved", "ui.map.hazard_zone", "signal.site.anchor_uncovered", "ui.world.focus.interior",
                "codex.world.territories.title", "codex.world.territories.body", "codex.world.interior.title", "codex.world.interior.body",
            };
            foreach (TerritoryTemplate tpl in WorldGenContent.TemplatesFor(v3))
            {
                keys.Add(tpl.IntroKey);
                keys.AddRange(tpl.TerminalLoreKeys.Split(','));
            }
            foreach (AnchorSite a in WorldGenContent.AnchorsFor(v3))
            {
                keys.Add(a.NameKey);
                keys.Add(a.DescKey);
                keys.Add("world.anchor.kind." + a.Kind);
            }
            foreach (string f in new[] { "silent", "foundry", "clarity", "overclock" })
            {
                keys.Add("world.faction." + f);
            }
            var missing = new List<string>();
            foreach (string k in keys)
            {
                if (!GameText.Has(k))
                {
                    missing.Add(k);
                }
                else
                {
                    GameSettings.SetLanguage(GameLanguage.En);
                    bool en = GameText.Has(k) && !GameText.Get(k).Contains("⟦");
                    GameSettings.SetLanguage(GameLanguage.ZhCn);
                    if (!en)
                    {
                        missing.Add(k + "(en)");
                    }
                }
            }
            Expect(missing.Count == 0, $"A3 新文本键 {keys.Count} 个中英两套都在（B16）" + (missing.Count == 0 ? string.Empty : "——缺：" + string.Join("，", missing.Take(8))));

            bool tune = new[] { "territory.banner_seconds", "territory.belt_warn_cells", "interior.pick_radius", "interior.exit_radius", "interior.transition_seconds", "interior.spawn_spacing",
                    "anchor.reach_max_attempts", "outpost.bucket_cells", "outer.reveal_margin_cells", "outer.cache_scrap_base", "outer.cache_scrap_per_1000", "outer.cache_seconds",
                    "signal.coverage.site_root_mode" }
                .All(k => GridContent.TryGetTuning(k, out float v) && v > 0f);
            bool hooks = new[] { GuidanceHooks.WorldFirstTerritoryEntered, GuidanceHooks.WorldFirstInteriorEntered, GuidanceHooks.WorldFirstPoiFound, GuidanceHooks.WorldFirstEntranceBlocked }
                .All(h => GuidanceHooks.Known.Contains(h));
            bool codex = new[] { "codex.world.territories", "codex.world.interior" }.All(id => t.TbCodexEntry.GetOrDefault(id) != null);
            bool notify = new[] { "territory_entered", "poi_found", "interior_transit" }.All(id => t.TbNotifyType.GetOrDefault(id)?.AwaySection == "event");
            Expect(tune && hooks && codex && notify, $"A4 调参入表（数值不写死）、四个引导钩子埋好（内容归 FG15-UX-04）、两条图鉴、三种通知类型（离家报告“事件”段）：调参 {tune} / 钩子 {hooks} / 图鉴 {codex} / 通知 {notify}");
        }

        // ── B 生成器版本（FGT-GEN-006）────────────────────────────────────────

        private static void CheckVersions()
        {
            // v1 / v2 的生成输入清单里没有 FG8 部分（与此前的基准逐字相同；FgWorldGenSelfCheck / FgWorldGenHomeSelfCheck 守护）；v3 有。
            List<KeyValuePair<string, string>> m2 = WorldGenInputs.Manifest(2);
            List<KeyValuePair<string, string>> m3 = WorldGenInputs.Manifest(3);
            bool v2clean = !m2.Any(p => p.Key.Contains("fg8") || p.Key.Contains("templates") || p.Key.Contains("anchors"));
            bool v3has = m3.Any(p => p.Key == "v3.version.fg8") && m3.Any(p => p.Key == "v3.templates[tt3]") && m3.Any(p => p.Key == "v3.anchors[as3]") && m3.Any(p => p.Key == "v3.template_points");
            string v2Base = string.Join("|", m2.Select(p => p.Key + "=" + WorldGenInputs.Digest(p.Value).ToString("X16")).Where(x => x.StartsWith("v2.")));
            string v3Base = string.Join("|", m3.Where(p => p.Key.StartsWith("v3.") && !p.Key.Contains("fg8") && !p.Key.Contains("templates") && !p.Key.Contains("anchors") && !p.Key.Contains("template_points"))
                .Select(p => p.Key.Substring(3) + "=" + WorldGenInputs.Digest(p.Value).ToString("X16")));
            bool sameBase = v2Base.Replace("v2.", string.Empty).Split('|').Where(x => !x.StartsWith("version=")).SequenceEqual(v3Base.Split('|').Where(x => !x.StartsWith("version=")));
            Expect(v2clean && v3has && sameBase,
                $"B1 FGR-GEN-061：v3 的生成输入清单多了模板 / 锚点 / 模板点位 / 零散前哨四部分，其余部分（领地、起始区、河流、矿带、保证、设置）与 v2 逐字相同；v2 清单没有任何 FG8 部分（{v2clean} / {v3has} / {sameBase}）");

            // 变异：直接改已发布的模板集合 tt3 而不升版本 → v3 清单摘要变（基准会拦下）。
            ulong before = WorldGenInputs.Digest(m3.First(p => p.Key == "v3.templates[tt3]").Value);
            WorldGenContent.OverrideForTests(templates: new TbTerritoryTemplate(TemplateBuf(r => r.TerritoryId == "silent" ? r.With(outpostEdge: r.OutpostEdgeChance + 0.1f) : r)));
            ulong after = WorldGenInputs.Digest(WorldGenInputs.Manifest(3).First(p => p.Key == "v3.templates[tt3]").Value);
            WorldGenContent.ResetForTests();
            Expect(before != after, $"B2 负向：不升版本直接改 tt3 的据点概率 → 生成输入清单的模板部分摘要变化（0x{before:X16} → 0x{after:X16}），回归基准会拦下");

            // 同一种子的 v2 与 v3：家园附近的区块逐位相同（v3 只在领地 / 锚点里改地形），领地里的区块不同（风格 / 平整）。
            CampaignState s = NewWorldState(20261017, 3);
            WorldGenContext c3 = WorldGenService.ContextFor(s);
            s.World.GeneratorVersion = 2;
            WorldGenService.Invalidate();
            WorldGenContext c2 = WorldGenService.ContextFor(s);
            s.World.GeneratorVersion = 3;
            WorldGenService.Invalidate();
            PlannedAnchor fc = c3.Plan.FindAnchor("fractured_city");
            bool homeSame = ChunkHash(c2.Source, 0, 0) == ChunkHash(c3.Source, 0, 0) && ChunkHash(c2.Source, -1, 1) == ChunkHash(c3.Source, -1, 1);
            bool siteDiff = fc != null && ChunkHash(c2.Source, Fd(fc.OriginX, 32), Fd(fc.OriginY, 32)) != ChunkHash(c3.Source, Fd(fc.OriginX, 32), Fd(fc.OriginY, 32));
            bool v2NoContent = c2.Plan.Anchors.Count == 0 && c2.Plan.HazardZones.Count == 0 && !c2.Plan.HasTemplates && c3.Plan.Anchors.Count == 6 && c3.Plan.HasTemplates;
            Expect(homeSame && siteDiff && v2NoContent,
                $"B3 同一种子：家园附近区块 v2 / v3 逐位相同（{homeSame}），破碎都市锚点所在区块 v3 平整过、与 v2 不同（{siteDiff}）；v2 规划层没有锚点 / 模板 / 危害区（{v2NoContent}）");

            // 旧版本（v2）存档：读回后未修改区块按 v2 重建（哈希一致）、修改过的区块按差异恢复、没有领地据点 / 锚点 / 入口、建造不受入口保护影响。
            CampaignState w = NewWorld(4242, version: 2);
            HomeGridMap map = HomeGridService.MapFor(w);
            GridCell core = HomeGridService.CorePivot(w);
            ChunkAddress probe = GridMath.Address(new GridCell(core.X + 70, core.Y - 70), map.ChunkSize);
            ulong untouchedBefore = MapChunkHash(map, probe.ChunkX + 3, probe.ChunkY - 2);
            var editCell = new GridCell(core.X + 70, core.Y - 70);
            byte oldT = map.GetTerrain(editCell);
            map.SetTerrain(editCell, GridContent.TerrainCode(oldT == GridContent.TerrainCode("ruin") ? "buildable" : "ruin"));
            byte edited = map.GetTerrain(editCell);
            int outpostsV2 = WorldOutpostSystem.Outposts(w).Count;
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = Reload(Slot);
            HomeGridMap map2 = back != null ? HomeGridService.MapFor(back) : null;
            WorldGenContext genV2 = back != null ? WorldGenService.ContextFor(back) : null;
            bool oldVersion = back != null && back.World.GeneratorVersion == 2 && genV2 != null && genV2.Version == 2;
            bool untouched = map2 != null && MapChunkHash(map2, probe.ChunkX + 3, probe.ChunkY - 2) == untouchedBefore
                             && MapChunkHash(map2, probe.ChunkX + 3, probe.ChunkY - 2) == ChunkHash(genV2.Source, probe.ChunkX + 3, probe.ChunkY - 2);
            bool diffKept = map2 != null && map2.GetTerrain(editCell) == edited;
            bool noTerritory = back != null && !back.World.TerritoryOutpostsSeeded && WorldOutpostSystem.Outposts(back).Count == outpostsV2
                               && WorldOutpostSystem.Outposts(back).All(o => o.Kind == WorldOutpostSystem.KindScoutNest)
                               && InteriorSiteService.EntranceFor(back, "relay_hall") == null && WorldGenService.PlanFor(back).Anchors.Count == 0;
            string legacyReason = back != null ? InteriorSiteService.CheckEnter(back, null, out _) : null;
            Expect(sv.Success && oldVersion && untouched && diffKept && noTerritory && legacyReason != null && legacyReason.Contains("v2"),
                $"B4 FGT-GEN-006 / 负向“读取旧生成器版本的存档”：v2 存档读回仍按 v2 生成（{oldVersion}），未修改区块哈希与 v2 生成结果一致（{untouched}），修改过的区块按差异恢复（{diffKept}），" +
                $"没有补出领地据点 / 锚点 / 室内入口（{noTerritory}），进室内给原因“{legacyReason}”");
        }

        // ── C 规划层规则（3 个种子）───────────────────────────────────────────────

        private static void CheckPlanRules()
        {
            foreach (int seed in Seeds)
            {
                CampaignState s = NewWorldState(seed, 3);
                WorldGenContext ctx = WorldGenService.ContextFor(s);
                WorldPlan plan = ctx.Plan;
                WorldGenContext again = WorldGenContext.Build(seed, 3, ctx.Settings, ctx.Core, 32);
                bool determinism = again.Plan.FullFingerprint() == plan.FullFingerprint();
                var bad = new List<string>();
                foreach (PlannedAnchor a in plan.Anchors)
                {
                    PlannedTerritory t = a.Territory;
                    if (t == null || t.Id != a.Row.TerritoryId)
                    {
                        bad.Add(a.Id + " 领地");
                        continue;
                    }
                    if (Dist(a.OriginX, a.OriginY, t.CenterX, t.CenterY) > t.Radius || Dist(a.EntryX, a.EntryY, t.CenterX, t.CenterY) > t.Radius)
                    {
                        bad.Add(a.Id + " 不在领地里");
                    }
                    if (Dist(a.OriginX, a.OriginY, ctx.Core.X, ctx.Core.Y) - a.OuterRadius <= plan.HomeZoneRadius)
                    {
                        bad.Add(a.Id + " 进家园区");
                    }
                    foreach (PlannedAnchor o in plan.Anchors)
                    {
                        if (o != a && o.Index > a.Index && Dist(a.OriginX, a.OriginY, o.OriginX, o.OriginY) < a.FootprintRadius + o.FootprintRadius + Math.Max(a.Row.MinGap, o.Row.MinGap))
                        {
                            bad.Add(a.Id + "×" + o.Id + " 重叠");
                        }
                    }
                    if (a.IsDemoSite && Dist(a.EntryX, a.EntryY, ctx.Core.X, ctx.Core.Y) >= Dist(a.OriginX, a.OriginY, ctx.Core.X, ctx.Core.Y))
                    {
                        bad.Add(a.Id + " 入口没朝家园");
                    }
                    if (a.Fallback)
                    {
                        bad.Add(a.Id + " 兜底放置");
                    }
                }
                Expect(determinism && bad.Count == 0 && plan.Anchors.Count == 6 && plan.Failures.Count == 0,
                    $"C1 种子 {seed}：规划层确定（同一输入两次算指纹相同 {determinism}）；6 个锚点都在所属领地里（原点与入口）、不进家园区、平整范围互不重叠、Demo 地点入口朝家园；没有兜底 / 失败" +
                    (bad.Count == 0 ? $"（{string.Join("，", plan.Anchors.Select(a => $"{a.Id}@({a.OriginX},{a.OriginY})k{a.Attempts}"))}）" : "——" + string.Join("，", bad)));

                // 平整范围：里面全是可建空地（悬崖 / 水面 / 河道 / 矿脉都让开）；过渡带内半圈的悬崖 + 水面比例不高于带外。
                byte buildable = GridContent.TerrainCode("buildable");
                byte cliff = GridContent.TerrainCode("cliff");
                byte water = GridContent.TerrainCode("water");
                int inside = 0, insideBad = 0, ringN = 0, ringBlocked = 0, outN = 0, outBlocked = 0;
                foreach (PlannedAnchor a in plan.Anchors.Where(x => x.FootprintRadius > 0))
                {
                    for (int dy = -a.OuterRadius - 24; dy <= a.OuterRadius + 24; dy += 2)
                    {
                        for (int dx = -a.OuterRadius - 24; dx <= a.OuterRadius + 24; dx += 2)
                        {
                            float d = Mathf.Sqrt(dx * dx + dy * dy);
                            ctx.Source.Sample(a.OriginX + dx, a.OriginY + dy, out byte tt, out _);
                            bool blocked = tt == cliff || tt == water;
                            if (d <= a.FootprintRadius)
                            {
                                inside++;
                                insideBad += tt != buildable ? 1 : 0;
                            }
                            else if (d <= a.FootprintRadius + a.BlendWidth * 0.5f)
                            {
                                ringN++;
                                ringBlocked += blocked ? 1 : 0;
                            }
                            else if (d > a.OuterRadius + 4 && d <= a.OuterRadius + 24)
                            {
                                outN++;
                                outBlocked += blocked ? 1 : 0;
                            }
                        }
                    }
                }
                float ringFrac = ringN > 0 ? (float)ringBlocked / ringN : 0f;
                float outFrac = outN > 0 ? (float)outBlocked / outN : 0f;
                Expect(inside > 1000 && insideBad == 0 && ringFrac <= outFrac + 0.005f,
                    $"C2 种子 {seed}：锚点平整范围里 {inside} 个采样格全部是可建空地（FG17 第 5 节“与悬崖、水域冲突 → 平整地形”）；过渡带内半圈悬崖 + 水面 {ringFrac:P1} ≤ 带外 {outFrac:P1}（边缘平滑过渡）");

                // 地貌风格：破碎都市的废墟、沉淀湖的水面越往中心越多（FGR-GEN-021 / 023）。
                (float silentCenter, float silentEdge) = StyleFraction(ctx, plan.Find("silent"), GridContent.TerrainCode("ruin"));
                (float lakeCenter, float lakeEdge) = StyleFraction(ctx, plan.Find("clarity"), water);
                Expect(silentCenter > silentEdge && lakeCenter > lakeEdge,
                    $"C3 种子 {seed}：越往领地中心越接近阵营风格——静默废墟 中心 {silentCenter:P1} > 边缘 {silentEdge:P1}；澄净水面 中心 {lakeCenter:P1} > 边缘 {lakeEdge:P1}");

                // 领地内危害区：每个有模板的领地都有、圆心在领地里、不压锚点；HazardAt 读模板（区内有、区外没有、外圈危害带照旧）。
                var zoneBad = new List<string>();
                foreach (TerritoryTemplate tpl in WorldGenContent.TemplatesFor(ctx.Row))
                {
                    PlannedTerritory t = plan.Find(tpl.TerritoryId);
                    List<PlannedHazardZone> zones = plan.HazardZones.Where(z => z.TerritoryId == tpl.TerritoryId).ToList();
                    if (zones.Count < Math.Max(1, tpl.HazardZones - 2))
                    {
                        zoneBad.Add($"{tpl.TerritoryId} 只有 {zones.Count} 处");
                    }
                    foreach (PlannedHazardZone z in zones)
                    {
                        if (Dist(z.CenterX, z.CenterY, t.CenterX, t.CenterY) > t.Radius || plan.Anchors.Any(a => Dist(z.CenterX, z.CenterY, a.OriginX, a.OriginY) < z.Radius + a.OuterRadius))
                        {
                            zoneBad.Add($"{tpl.TerritoryId}#{z.Index}");
                        }
                    }
                }
                PlannedHazardZone z0 = plan.HazardZones.FirstOrDefault(z => z.TerritoryId == "silent");
                Hazard inZone = z0 != null ? HomeExplorationService.HazardAt(s, z0.CenterX, z0.CenterY, visibleOnly: false, out _, out bool beltA) : null;
                (int fx, int fy) = FreeTerritoryPoint(plan, plan.Find("silent"));
                Hazard outZone = HomeExplorationService.HazardAt(s, fx, fy, visibleOnly: false, out _, out _);
                PlannedTerritory lake = plan.Find("clarity");
                int bx = lake.CenterX + lake.Radius + lake.HazardWidth / 2;
                Hazard belt = HomeExplorationService.HazardAt(s, bx, lake.CenterY, visibleOnly: false, out _, out bool isBelt);
                Expect(zoneBad.Count == 0 && inZone?.Id == "emi_noise" && outZone == null && belt?.Id == "acid_marsh" && isBelt,
                    $"C4 种子 {seed}：领地内危害区按模板分布（共 {plan.HazardZones.Count} 处，圆心在领地里、不压锚点）；HazardAt 读模板：静默危害区里 = 电磁噪声、领地里区外 = 无、澄净外圈 = 酸沼带（DEBT-FG7ENV05-02）" +
                    (zoneBad.Count == 0 ? string.Empty : "——" + string.Join("，", zoneBad)));
            }
        }

        /// <summary>
        /// C6 FGR-GEN-091 / B25（审查 P2）：v3 锚点规划规则在种子测试集（FgWorldGenHomeSelfCheck.SeedSet，50 个）× 领地 标准 / 近 / 远 三档设置下都成立——
        /// 纯规划层（不建区块、不寻路，约 30 ms / 世界）：没有失败 / 兜底；6 个锚点的原点与入口都在所属领地里，入口离领地边界至少一个平整半径；不进家园区；平整范围互不重叠；Demo 入口朝家园。
        /// 可达性（层级寻路）仍只在 3 个种子上全量跑（D1），50 个种子的可达归 FG15-QA-03（DEBT-FG8GEN02-06）。核心枢轴格是固定布局，与种子无关。
        /// </summary>
        private static void CheckPlanSeedSet()
        {
            WorldGenContext ctx0 = WorldGenService.ContextFor(NewWorldState(1, 3));
            GridCell core = ctx0.Core;
            WorldGenVersion v3 = WorldGenContent.Version(3);
            var sw = Stopwatch.StartNew();
            int worlds = 0;
            foreach ((string code, string label) in new[] { (WorldGenContent.DefaultPresetId, "标准"), ("R1O1P1D0S0", "领地近"), ("R1O1P1D2S0", "领地远") })
            {
                var bad = new List<string>();
                int minDepth = int.MaxValue;
                foreach (int seed in FgWorldGenHomeSelfCheck.SeedSet)
                {
                    WorldPlan plan = WorldPlan.Compute(seed, v3, WorldSettings.Resolve(3, code), core.X, core.Y);
                    worlds++;
                    if (plan.Failures.Count > 0)
                    {
                        bad.Add($"种子 {seed} 规划失败：{plan.Failures[0]}");
                    }
                    if (plan.Anchors.Count != 6)
                    {
                        bad.Add($"种子 {seed} 锚点 {plan.Anchors.Count} 个");
                    }
                    foreach (PlannedAnchor a in plan.Anchors)
                    {
                        PlannedTerritory t = a.Territory;
                        if (t == null)
                        {
                            bad.Add($"种子 {seed} {a.Id} 没有领地");
                            continue;
                        }
                        double entryDepth = t.Radius - Dist(a.EntryX, a.EntryY, t.CenterX, t.CenterY);
                        minDepth = Math.Min(minDepth, (int)Math.Floor(entryDepth));
                        if (a.Fallback)
                        {
                            bad.Add($"种子 {seed} {a.Id} 兜底放置");
                        }
                        if (Dist(a.OriginX, a.OriginY, t.CenterX, t.CenterY) > t.Radius || entryDepth < a.FootprintRadius)
                        {
                            bad.Add($"种子 {seed} {a.Id} 原点 / 入口不在领地里或入口深度 {entryDepth:0} < {a.FootprintRadius}");
                        }
                        if (Dist(a.OriginX, a.OriginY, core.X, core.Y) - a.OuterRadius <= plan.HomeZoneRadius)
                        {
                            bad.Add($"种子 {seed} {a.Id} 进家园区");
                        }
                        if (a.IsDemoSite && Dist(a.EntryX, a.EntryY, core.X, core.Y) >= Dist(a.OriginX, a.OriginY, core.X, core.Y))
                        {
                            bad.Add($"种子 {seed} {a.Id} 入口没朝家园");
                        }
                        foreach (PlannedAnchor o in plan.Anchors)
                        {
                            if (o.Index > a.Index && Dist(a.OriginX, a.OriginY, o.OriginX, o.OriginY) < a.FootprintRadius + o.FootprintRadius + Math.Max(a.Row.MinGap, o.Row.MinGap))
                            {
                                bad.Add($"种子 {seed} {a.Id}×{o.Id} 重叠");
                            }
                        }
                    }
                }
                Expect(bad.Count == 0,
                    $"C6 FGR-GEN-091【{label}】{FgWorldGenHomeSelfCheck.SeedSet.Length} 个种子（纯规划层）：没有失败 / 兜底，6 个锚点都在所属领地里、入口离领地边界至少一个平整半径（最浅 {minDepth} 格）、不进家园区、平整范围互不重叠、Demo 入口朝家园" +
                    (bad.Count == 0 ? string.Empty : "——" + string.Join("；", bad.Take(5)) + (bad.Count > 5 ? $" 等 {bad.Count} 处" : string.Empty)));
            }
            sw.Stop();
            PerfLines.Add($"种子测试集纯规划层：{worlds} 个世界 {sw.Elapsed.TotalMilliseconds:0} ms（{sw.Elapsed.TotalMilliseconds / Math.Max(1, worlds):0.0} ms / 世界）");
        }

        /// <summary>FGR-GEN-090 负向：锚点之间约束冲突 → 确定性地局部重试（子种子换候选，只重算这一个锚点），同一输入两次结果相同，写日志。</summary>
        private static void CheckAnchorConflict()
        {
            CampaignState s = NewWorldState(7, 3);
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            try
            {
                // 在静默领地里塞两个大平整范围的锚点、方向由种子决定、距离范围很窄：第二个很难一次放下。
                WorldGenContent.OverrideForTests(anchors: new TbAnchorSite(AnchorBuf(
                    ("crowd_a", "silent", 0.45f, 0.55f, 80, 30),
                    ("crowd_b", "silent", 0.45f, 0.55f, 80, 30))));
                WorldGenContext c1 = WorldGenContext.Build(7, 3, ctx.Settings, ctx.Core, 32);
                WorldGenContext c2 = WorldGenContext.Build(7, 3, ctx.Settings, ctx.Core, 32);
                PlannedAnchor b = c1.Plan.FindAnchor("crowd_b");
                PlannedAnchor a = c1.Plan.FindAnchor("crowd_a");
                bool retried = b != null && (b.Attempts > 0 || b.Fallback);
                bool apart = a != null && b != null && (b.Fallback || Dist(a.OriginX, a.OriginY, b.OriginX, b.OriginY) >= 160 + 30);
                bool logged = c1.Plan.Log.Concat(c1.Plan.Failures).Any(l => l.Contains("crowd_b"));
                bool same = c1.Plan.FullFingerprint() == c2.Plan.FullFingerprint();
                Expect(retried && apart && logged && same,
                    $"C5 FGR-GEN-090 负向：两个锚点在同一领地抢位置 → 第二个按子种子确定性重试（第 {b?.Attempts + 1} 次{(b != null && b.Fallback ? "，逐度扫描兜底" : string.Empty)}）、不重叠（{apart}）、写日志（{logged}）、同一输入两次结果相同（{same}）");
            }
            finally
            {
                WorldGenContent.ResetForTests();
                WorldGenService.Invalidate();
            }
        }

        // ── D 可达性（FGT-GEN-004，3 个种子）───────────────────────────────────────

        private static void CheckReachability()
        {
            foreach (int seed in Seeds)
            {
                CampaignState s = NewWorld(seed);
                WorldPlan plan = WorldGenService.PlanFor(s);
                var sw = Stopwatch.StartNew();
                bool all = TerritoryAnchorChecks.EnsureAll(s);
                sw.Stop();
                var states = plan.Anchors.Where(a => a.Row.ReachCheck == 1).Select(a => (a, st: TerritoryAnchorChecks.StateOf(s, a))).ToList();
                bool reachable = states.All(x => x.st == TerritoryAnchorChecks.StateOk || x.st == TerritoryAnchorChecks.StateMoved);
                bool indoor = states.Where(x => x.a.IsIndoorEntrance).All(x => x.st == TerritoryAnchorChecks.StateOk || x.st == TerritoryAnchorChecks.StateMoved) && states.Count(x => x.a.IsIndoorEntrance) == 2;
                bool reserve = states.Any(x => x.a.IsReserve && (x.st == TerritoryAnchorChecks.StateOk || x.st == TerritoryAnchorChecks.StateMoved));
                // Demo 远征地点：远征锚点的第 0 次试探就是模板入口（入口在平整范围里，走得到就不挪），不另算一套。
                bool demo = new[] { (FracturedCityLayout.RegionId, "fractured_city"), (FoundryOutpostLayout.RegionId, "foundry_outpost") }.All(p =>
                {
                    PlannedAnchor a = plan.FindAnchor(p.Item2);
                    ExpeditionSiteAnchorRecord r = ExpeditionSiteAnchors.Find(s, p.Item1);
                    return a != null && r != null && r.State == ExpeditionSiteAnchors.StateResolved && r.Attempt == 0 && r.X == a.EntryX && r.Y == a.EntryY;
                });
                // 存档：校验结果进存档，读回不重算（请求数不变）。
                int requests = TerritoryAnchorChecks.RequestCount;
                CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
                CampaignState back = Reload(Slot);
                bool persisted = back != null && plan.Anchors.Where(a => a.Row.ReachCheck == 1 && TerritoryAnchorChecks.ExpeditionSiteOf(a.Id) == null)
                    .All(a => TerritoryAnchorChecks.Find(back, a.Id) is AnchorCheckRecord r && (r.State == TerritoryAnchorChecks.StateOk || r.State == TerritoryAnchorChecks.StateMoved));
                TerritoryAnchorChecks.EnsureAll(back);
                bool noRecheck = TerritoryAnchorChecks.RequestCount == requests;
                Expect(all && reachable && indoor && reserve && demo && persisted && noRecheck,
                    $"D1 FGT-GEN-004 种子 {seed}：{states.Count} 个锚点从家园出发都可达（{string.Join("，", states.Select(x => x.a.Id + ":" + x.st))}），两个室内入口可达、白潮预留区可达；" +
                    $"Demo 远征锚点 = 模板入口（第 0 次试探，{demo}）；校验结果进存档、读回不重算（{persisted} / {noRecheck}）；校验耗时 {sw.Elapsed.TotalMilliseconds:0} ms（一次性，按需）");
                PerfLines.Add($"种子 {seed} 锚点可达性校验（层级寻路，同步，编辑器）：{sw.Elapsed.TotalMilliseconds:0} ms / {states.Count} 个");
            }
        }

        /// <summary>
        /// D2 负向（审查 P1：失败态下入口面板每 0.25 秒同步重跑一条长寻路并刷错误日志）：入口可达校验失败时——
        /// 点入口打开面板 = 玩家动作，校验一次（待定 → 失败，报一次错）；面板开着 4 秒（16 次定时刷新）不发寻路请求、不再报错，按钮禁用并写原因；
        /// 玩家点“进入”= 再校验一次（结论没变不再报错）；挡路消除后（撤掉注入，相当于玩家拆掉了挡路的墙）重新打开面板 → 恢复可达。
        /// 失败用注入（<see cref="TerritoryAnchorChecks.ForceUnreachableForTests"/>：寻路请求照常发出、照常计数，只把结果当作没有路线）。
        /// </summary>
        private static void CheckReachFailedState()
        {
            CampaignState s = NewWorld(42);
            PlannedAnchor gate = InteriorSiteService.EntranceFor(s, "relay_hall");
            int errors = 0;
            Application.LogCallback onLog = (msg, stack, type) =>
            {
                if ((type == LogType.Error || type == LogType.Exception) && msg != null && msg.Contains("[TerritoryAnchorChecks]"))
                {
                    errors++;
                }
            };
            var go = new GameObject("FgTerritoryHudReachFail");
            WorldPlacesHudUIToolkit hud = null;
            Application.logMessageReceived += onLog;
            TerritoryAnchorChecks.ForceUnreachableForTests = id => id == gate.Id;
            try
            {
                VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "WorldPlacesHud.uxml");
                VisualElement root = tree.CloneTree();
                hud = go.AddComponent<WorldPlacesHudUIToolkit>();
                hud.BindView(root);
                WorldPlacesHudUIToolkit.InWorldOverrideForTests = true;
                int req0 = TerritoryAnchorChecks.RequestCount;
                hud.SetPanel(gate.Id, false);
                int stOpen = TerritoryAnchorChecks.StateOf(s, gate);
                int reqOpen = TerritoryAnchorChecks.RequestCount - req0;
                int errOpen = errors;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 16; i++)
                {
                    hud.RefreshPanel();
                }
                sw.Stop();
                int reqRefresh = TerritoryAnchorChecks.RequestCount - req0 - reqOpen;
                int errRefresh = errors - errOpen;
                string reason = hud.PanelReasonText;
                bool goOff = !hud.GoEnabled;
                string unreachable = GameText.Format("interior.reason.unreachable", string.Empty);
                bool entered = InteriorSiteService.TryEnter(s, gate.Id, out string why);
                int reqEnter = TerritoryAnchorChecks.RequestCount - req0 - reqOpen - reqRefresh;
                int errEnter = errors - errOpen - errRefresh;
                TerritoryAnchorChecks.ForceUnreachableForTests = null;
                hud.SetPanel(null, false);
                hud.SetPanel(gate.Id, false);
                int stBack = TerritoryAnchorChecks.StateOf(s, gate);
                bool reasonGone = string.IsNullOrEmpty(hud.PanelReasonText) || !hud.PanelReasonText.StartsWith(unreachable.TrimEnd(), StringComparison.Ordinal);
                Expect(stOpen == TerritoryAnchorChecks.StateFailed && reqOpen == 1 && errOpen == 1 && reqRefresh == 0 && errRefresh == 0
                       && reason.StartsWith(unreachable.TrimEnd(), StringComparison.Ordinal) && goOff
                       && !entered && why != null && why.StartsWith(unreachable.TrimEnd(), StringComparison.Ordinal) && reqEnter == 1 && errEnter == 0
                       && (stBack == TerritoryAnchorChecks.StateOk || stBack == TerritoryAnchorChecks.StateMoved) && reasonGone,
                    $"D2 负向 FGR-GEN-090 入口走不到：点入口打开面板校验一次（{reqOpen} 条寻路、报错 {errOpen} 次，状态 {stOpen}）；面板开着 4 秒刷新 16 次不重算（{reqRefresh} 条寻路、报错 {errRefresh} 次，{sw.Elapsed.TotalMilliseconds:0.0} ms），" +
                    $"按钮禁用、原因“{reason}”；点“进入”再校验一次（{reqEnter} 条，结论没变不再报错 {errEnter} 次）被拒；挡路消除后重新打开面板恢复可达（状态 {stBack}，原因已清 {reasonGone}）");
            }
            finally
            {
                Application.logMessageReceived -= onLog;
                TerritoryAnchorChecks.ForceUnreachableForTests = null;
                if (hud != null)
                {
                    hud.SetPanel(null, false);
                }
                WorldPlacesHudUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
        }

        // ── E 领地内容（据点 / 巡逻 / 缓存 / 终端 / 支线地点）───────────────────────────

        private static void CheckContent()
        {
            CampaignState s = NewWorld(42);
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            WorldPlan plan = ctx.Plan;
            int r = ctx.Row.OuterZoneDistance;
            var a = new List<WorldFeature>();
            WorldFeatures.Query(ctx, ctx.Core.X - r, ctx.Core.Y - r, ctx.Core.X + r, ctx.Core.Y + r, a);
            // 访问顺序无关：换一个干净的上下文、先查远处再查近处，点位集合完全相同。
            WorldGenContext fresh = WorldGenContext.Build(ctx.Seed, ctx.Version, ctx.Settings, ctx.Core, 32);
            var b = new List<WorldFeature>();
            WorldFeatures.Query(fresh, ctx.Core.X, ctx.Core.Y, ctx.Core.X + r, ctx.Core.Y + r, b);
            WorldFeatures.Query(fresh, ctx.Core.X - r, ctx.Core.Y - r, ctx.Core.X + r, ctx.Core.Y + r, b);
            string Key(WorldFeature f) => f.Id + "@" + f.X + "," + f.Y + "t" + f.Tier + (f.Patrol ? "p" : string.Empty);
            bool order = new HashSet<string>(a.Select(Key)).SetEquals(b.Select(Key));
            var perTerritory = new Dictionary<string, (int posts, int caches, int terms, int sides)>();
            var bad = new List<string>();
            foreach (WorldFeature f in a)
            {
                if (f.Kind == WorldFeatureKind.ScoutNest || f.Kind == WorldFeatureKind.Relic)
                {
                    continue;
                }
                if (f.TerritoryId != null)
                {
                    perTerritory.TryGetValue(f.TerritoryId, out var c);
                    perTerritory[f.TerritoryId] = (c.posts + (f.Kind == WorldFeatureKind.Outpost ? 1 : 0), c.caches + (f.Kind == WorldFeatureKind.Cache ? 1 : 0),
                        c.terms + (f.Kind == WorldFeatureKind.Terminal ? 1 : 0), c.sides + (f.Kind == WorldFeatureKind.SideSite ? 1 : 0));
                }
                if (TerritoryPlanner.NearAnyAnchor(plan, f.X, f.Y, 1))
                {
                    bad.Add(f.Id + " 压在锚点上");
                }
                if (Dist(f.X, f.Y, ctx.Core.X, ctx.Core.Y) <= ctx.Row.HomeZoneRadius)
                {
                    bad.Add(f.Id + " 进家园区");
                }
                ctx.Source.Sample(f.X, f.Y, out byte tt, out _);
                if (tt == GridContent.TerrainCode("cliff") || tt == GridContent.TerrainCode("water"))
                {
                    bad.Add(f.Id + " 落在悬崖 / 水面");
                }
            }
            bool everyTerritory = new[] { "silent", "foundry", "clarity", "overclock" }.All(id => perTerritory.TryGetValue(id, out var c) && c.posts >= 3 && c.caches >= 1 && c.terms >= 1);
            Expect(order && everyTerritory && bad.Count == 0,
                $"E1 FGR-GEN-023 领地模板点位：访问顺序无关（{order}）；四个领地都有据点 / 资源缓存 / 旧终端 / 支线地点（{string.Join("，", perTerritory.Select(p => $"{p.Key} 据点{p.Value.posts} 缓存{p.Value.caches} 终端{p.Value.terms} 支线{p.Value.sides}"))}）；" +
                   $"都不压锚点、不进家园区、不在悬崖 / 水面上" + (bad.Count == 0 ? string.Empty : "——" + string.Join("，", bad.Take(6))));

            // 越往中心越密越强：静默 + 铸造 + 澄净 + 超频合计，内半径（< 0.5R）的面积密度与平均等级都高于外环。
            int innerN = 0, outerN = 0;
            double innerTier = 0, outerTier = 0, innerArea = 0, outerArea = 0;
            foreach (PlannedTerritory t in plan.Territories.Where(x => x.IsFaction))
            {
                innerArea += Math.PI * Math.Pow(t.Radius * 0.5, 2);
                outerArea += Math.PI * (t.Radius * (double)t.Radius - Math.Pow(t.Radius * 0.5, 2));
                foreach (WorldFeature f in a.Where(f => f.Kind == WorldFeatureKind.Outpost && f.TerritoryId == t.Id))
                {
                    if (Dist(f.X, f.Y, t.CenterX, t.CenterY) < t.Radius * 0.5)
                    {
                        innerN++;
                        innerTier += f.Tier;
                    }
                    else
                    {
                        outerN++;
                        outerTier += f.Tier;
                    }
                }
            }
            double innerDensity = innerN / innerArea * 1e5;
            double outerDensity = outerN / outerArea * 1e5;
            Expect(innerN > 0 && outerN > 0 && innerDensity > outerDensity && innerTier / innerN > outerTier / outerN,
                $"E2 FGR-GEN-021 越往领地中心据点越密越强：内半径密度 {innerDensity:0.00} > 外环 {outerDensity:0.00}（个 / 10 万格），平均等级 {innerTier / Math.Max(1, innerN):0.0} > {outerTier / Math.Max(1, outerN):0.0}");

            // 开局登记：外延区以内的据点全部登记成据点（ID = 点位 ID，阵营 / 种类 / 等级 / 驻军），部分派巡逻；幂等。
            List<OutpostRecord> posts = WorldOutpostSystem.Outposts(s).ToList();
            List<WorldFeature> inner = a.Where(f => f.Kind == WorldFeatureKind.Outpost && !f.Outer).ToList();
            bool seeded = s.World.TerritoryOutpostsSeeded && inner.All(f => posts.Any(o => o.OutpostId == f.Id));
            bool kinds = posts.Where(o => o.Kind == WorldOutpostSystem.KindTerritory).All(o => plan.Find(o.TerritoryId) != null && o.Tier >= 1 && o.Garrison >= o.Tier)
                         && posts.Count(o => o.Kind == WorldOutpostSystem.KindScatter) > 0 && posts.Count(o => o.Kind == WorldOutpostSystem.KindTerritory) > 0;
            int patrols = WorldOutpostSystem.Patrols(s).Count(p => posts.Any(o => o.OutpostId == p.OutpostId && o.Kind == WorldOutpostSystem.KindTerritory));
            int before = posts.Count;
            int again = WorldGenService.SeedTerritoryOutposts(s);
            s.World.TerritoryOutpostsSeeded = false;
            int again2 = WorldGenService.SeedTerritoryOutposts(s);
            bool idempotent = again == 0 && again2 == 0 && WorldOutpostSystem.Outposts(s).Count == before;
            bool scatterFaction = posts.Where(o => o.Kind == WorldOutpostSystem.KindScatter).All(o => !string.IsNullOrEmpty(o.TerritoryId) && plan.Find(o.TerritoryId)?.IsFaction == true);
            bool raidOrigin = posts.Where(o => o.Kind != WorldOutpostSystem.KindScoutNest).All(o => GameLogic.Campaign.Defense.RaidDirectorService.FactionOfOutpost(s, o) == o.TerritoryId);
            Expect(seeded && kinds && patrols > 0 && idempotent && scatterFaction && raidOrigin,
                $"E3 DEBT-FG0ARCH06-01：开局登记阵营领地主力据点 {posts.Count(o => o.Kind == WorldOutpostSystem.KindTerritory)} 个（派巡逻 {patrols} 支）+ 领地之间零散前哨 {posts.Count(o => o.Kind == WorldOutpostSystem.KindScatter)} 个；" +
                   $"ID = 点位 ID、幂等（{idempotent}）；零散前哨归方向最近的阵营（突袭导演按阵营取出发地，{raidOrigin}）");

            // 每步只推进醒着的据点（DEBT-FG0ARCH06-06）：全部休眠时每步遍历 0 个；休眠判定粗筛后逐个比较的据点只是家园附近的几个。
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            int awakeStepped = WorldOutpostSystem.LastSteppedOutposts;
            int dormant = WorldOutpostSystem.Outposts(s).Count(o => o.Dormant);
            int awakeNow = WorldOutpostSystem.Outposts(s).Count(o => !o.Destroyed && (!o.Dormant || o.AlwaysSimulate));
            int exact = WorldOutpostSystem.LastExactChecks;
            Expect(dormant > before / 2 && awakeStepped == awakeNow && exact < before,
                $"E4 性能（DEBT-FG0ARCH06-06）：{before} 个据点里 {dormant} 个休眠，每步只推进醒着的 {awakeStepped} 个；休眠判定按己方实体分桶粗筛，逐个比较 {exact} 个（不是全部）");

            // 星球 / 地图上的据点按空间桶查（与据点总数无关）+ 标签写阵营、等级与驻军（DEBT-FG0ARCH06-08）。
            OutpostRecord one = posts.First(o => o.Kind == WorldOutpostSystem.KindTerritory);
            var near = new List<OutpostRecord>();
            WorldOutpostSystem.OutpostsNear(s, one.CellX, one.CellY, 4f, near);
            string label = WorldMapModel.OutpostLabel(s, one);
            // 巡逻：按据点找它的巡逻（与巡逻总数无关），地图上画在巡逻所在的已探索格，标签写人数；路线还在规划时写“正在规划路线…”。
            OutpostRecord withPatrol = posts.First(o => o.Kind == WorldOutpostSystem.KindTerritory && WorldOutpostSystem.Patrols(s).Any(q => q.OutpostId == o.OutpostId));
            var mine = new List<PatrolRecord>();
            WorldOutpostSystem.PatrolsOf(s, withPatrol.OutpostId, mine);
            PatrolRecord pr = mine.FirstOrDefault();
            HomeGridService.RevealArea(s, new Vector2(withPatrol.CellX, withPatrol.CellY), 60f);
            var pm = new WorldMapModel();
            pm.Collect(s, new WorldMapView { CenterX = withPatrol.CellX, CenterY = withPatrol.CellY, HalfWidth = 120, CanvasWidth = 900, CanvasHeight = 700 }, 200);
            bool pFound = pr != null && pm.Items.Any(i => i.Id == pr.PatrolId);
            WorldMapItem pItem = pFound ? pm.Items.First(i => i.Id == pr.PatrolId) : default;
            string pLabel = pFound ? pItem.Label ?? string.Empty : string.Empty;
            int prev = pr?.RouteState ?? 0;
            if (pr != null)
            {
                pr.RouteState = WorldOutpostSystem.RouteAwaiting;
            }
            string awaiting = pr != null ? WorldMapModel.PatrolLabel(pr) : string.Empty;
            if (pr != null)
            {
                pr.RouteState = prev;
            }
            bool patrolOk = pr != null && mine.All(q => q.OutpostId == withPatrol.OutpostId) && pFound && pItem.Kind == WorldMapItemKind.Patrol
                            && pLabel.Contains(GameText.Get("outpost.patrol.name")) && awaiting.Contains(GameText.Get("nav.route.awaiting")) && !GameText.ContainsMarker(pLabel);
            Expect(near.Contains(one) && near.Count < 6 && label.Contains("级") && label.Contains("驻军") && !GameText.ContainsMarker(label) && patrolOk,
                $"E5 据点按空间桶查附近（4 格内 {near.Count} 个）；地图标签“{label}”写阵营、等级与驻军（休眠的按只读补算，不改状态）；" +
                $"巡逻按据点索引查到 {mine.Count} 支，地图上“{pLabel}”，路线规划中写“{awaiting}”（DEBT-FG0ARCH06-08 的未用文本键接上）");
        }

        /// <summary>FGR-GEN-040 无限外延：外延区在揭开时按需登记零散前哨（越远越强、缓存越丰厚）；登记即“从第 0 步起一直在休眠”，先后揭开 / 何时揭开都不改变结果；与首领状态无关。</summary>
        private static void CheckOuterZone()
        {
            CampaignState s = NewWorld(1);
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            int outerStart = ctx.Row.OuterZoneDistance;
            int before = WorldOutpostSystem.Outposts(s).Count;
            int dirDeg = ctx.Plan.Territories.First(t => t.Id == "silent").AngleDeg + 180;
            var far = new Vector2(ctx.Core.X + Mathf.Cos(dirDeg * Mathf.Deg2Rad) * (outerStart + 900), ctx.Core.Y + Mathf.Sin(dirDeg * Mathf.Deg2Rad) * (outerStart + 900));
            var sw = Stopwatch.StartNew();
            HomeGridService.RevealArea(s, far, 600f);
            sw.Stop();
            List<OutpostRecord> outer = WorldOutpostSystem.Outposts(s).Where(o => o.Kind == WorldOutpostSystem.KindOuter).ToList();
            bool registered = outer.Count > 0 && outer.All(o => o.Dormant && o.SimTick == 0 && Dist(o.CellX, o.CellY, ctx.Core.X, ctx.Core.Y) >= outerStart);
            var farFeatures = new List<WorldFeature>();
            WorldFeatures.Query(ctx, (int)far.x - 700, (int)far.y - 700, (int)far.x + 700, (int)far.y + 700, farFeatures);
            double outerTier = outer.Count > 0 ? outer.Average(o => o.Tier) : 0;
            double scatterTier = WorldOutpostSystem.Outposts(s).Where(o => o.Kind == WorldOutpostSystem.KindScatter).Select(o => (double)o.Tier).DefaultIfEmpty(0).Average();
            WorldFeature cache = farFeatures.FirstOrDefault(f => f.Kind == WorldFeatureKind.Cache && f.Outer);
            HomeExplorationService.RelicInfo info = default;
            bool richer = cache != null && HomeExplorationService.TryGetRelic(s, cache.Id, out info) && info.Scrap > (GridContent.TryGetTuning("scatter.cache_scrap", out float sc) ? sc : 50f);
            Expect(registered && outerTier > scatterTier && richer && WorldOutpostSystem.Outposts(s).Count > before,
                $"E6 FGR-GEN-040 无限外延：揭开离核心 {outerStart + 900} 格的一片 → 登记外延区零散前哨 {outer.Count} 个（全部“从第 0 步起休眠”）；平均等级 {outerTier:0.0} > 领地之间 {scatterTier:0.0}；" +
                $"外延区缓存废料 {info.Scrap}（越远越多）；揭开耗时 {sw.Elapsed.TotalMilliseconds:0.0} ms");
            PerfLines.Add($"揭开外延区 600 格（登记零散前哨 + 找缓存 / 遗迹）：{sw.Elapsed.TotalMilliseconds:0.0} ms（一次性）");

            // 与登记时机无关：A = 开局就揭开（第 0 步登记），B = 跑 N 步后才揭开（晚登记）；同一步唤醒后驻军 / 增援 / 下次增援逐字段相同。
            string DigestAt(bool early)
            {
                CampaignState w = NewWorld(1);
                if (early)
                {
                    HomeGridService.RevealArea(w, far, 300f);
                }
                WorldSimulation.StepMany(GameClock.StepHz * 30);
                if (!early)
                {
                    HomeGridService.RevealArea(w, far, 300f);
                }
                List<OutpostRecord> list = WorldOutpostSystem.Outposts(w).Where(o => o.Kind == WorldOutpostSystem.KindOuter).OrderBy(o => o.OutpostId, StringComparer.Ordinal).ToList();
                foreach (OutpostRecord o in list)
                {
                    WorldOutpostSystem.WakeNow(w, o.OutpostId, WorldOutpostSystem.WakeEvent);
                }
                return string.Join("|", list.Select(o => $"{o.OutpostId}:{o.Garrison}/{o.GarrisonCap}:{o.NextReinforceTick}:{o.ReinforcementsApplied}:{o.SimTick}"));
            }
            string early = DigestAt(true);
            string late = DigestAt(false);
            Expect(early.Length > 0 && early == late,
                $"E7 FGR-GEN-002 与访问顺序 / 时机无关：外延前哨开局登记与 30 游戏秒后才登记，唤醒后状态逐字段相同（{early.Split('|').Length} 个）");

            // 规则对首领状态无条件成立（FGR-GEN-040 第 2 条“首领被击败后更远处仍可能生成该阵营的新前哨”）：推进幕次 / 标记首领战后，再揭开更远处照样登记同阵营前哨。
            s = CampaignSession.Current;
            s.Progress.Act = 3;
            var farther = far * 1.4f;
            int n0 = WorldOutpostSystem.Outposts(s).Count;
            HomeGridService.RevealArea(s, farther, 400f);
            int n1 = WorldOutpostSystem.Outposts(s).Count;
            Expect(n1 > n0, $"E8 FGR-GEN-040：进入第三幕后再揭开更远处（{farther.magnitude:0} 格），仍登记新的零散前哨 {n1 - n0} 个——生成规则与剧情进度无关（确定）");
        }

        /// <summary>
        /// E9 生成据点的巡逻按需要路线（WorldOutpostSystem.IsGenerated）：开局休眠的领地据点不请求路线（不占寻路工作线程）；
        /// 己方机器走近、据点醒来后才请求，路线视为第 0 步就绪——早醒与晚醒，到同一时刻巡逻进度与位置逐位相同（与何时第一次醒来无关，FGR-BASE-021）。
        /// </summary>
        private static void CheckLazyPatrols()
        {
            bool idleAtStart = false;
            int idleCount = 0;
            string PatrolAt(int wakeSeconds)
            {
                CampaignState w = NewWorld(42);
                GridCell core = HomeGridService.CorePivot(w);
                OutpostRecord o = WorldOutpostSystem.Outposts(w)
                    .Where(x => WorldOutpostSystem.IsGenerated(x) && x.Dormant && Dist(x.CellX, x.CellY, core.X, core.Y) > 700
                                && WorldOutpostSystem.Patrols(w).Any(q => q.OutpostId == x.OutpostId))
                    .OrderBy(x => x.OutpostId, StringComparer.Ordinal).FirstOrDefault();
                if (o == null)
                {
                    return "没有远处带巡逻的生成据点";
                }
                PatrolRecord p = WorldOutpostSystem.Patrols(w).First(q => q.OutpostId == o.OutpostId);
                WorldSimulation.StepMany(GameClock.StepHz * wakeSeconds);
                var dormantPatrols = WorldOutpostSystem.Patrols(w).Where(q => WorldOutpostSystem.Outposts(w).Any(x => x.OutpostId == q.OutpostId && WorldOutpostSystem.IsGenerated(x) && x.Dormant)).ToList();
                idleCount = dormantPatrols.Count;
                idleAtStart = dormantPatrols.Count > 0 && dormantPatrols.All(q => q.RouteState == WorldOutpostSystem.RouteNeed);
                // 一台机器出现在据点 30 格内（= 玩家把机器开过去；即使它之后自己走开，40 秒内也走不出唤醒范围）：下一次休眠判定排进唤醒队列、醒来，再请求巡逻路线。
                var toward = new Vector2(core.X - o.CellX, core.Y - o.CellY).normalized;
                Spawn(w, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, Vector2.zero, new Vector2(o.CellX, o.CellY) + toward * 30f);
                WorldSimulation.StepMany(GameClock.StepHz * (40 - wakeSeconds));
                return $"{o.OutpostId}:{(o.Dormant ? "休眠" : "醒")}:{p.RouteState}:{p.ProgressMilli}:{p.PosX:F3},{p.PosY:F3}";
            }
            string early = PatrolAt(5);
            bool idleEarly = idleAtStart;
            string late = PatrolAt(25);
            bool idleLate = idleAtStart;
            Expect(idleEarly && idleLate && early == late && early.Contains(":醒:" + WorldOutpostSystem.RouteReady + ":"),
                $"E9 生成据点的巡逻按需要路线：开局休眠的 {idleCount} 支巡逻都不请求路线（{idleEarly && idleLate}）；机器走近据点醒来后才请求，路线视为第 0 步就绪——" +
                $"第 5 秒与第 25 秒才醒，到第 40 秒巡逻状态逐位相同（{early}{(early == late ? string.Empty : " ≠ " + late)}）");
        }

        // ── F 资源缓存与旧终端 ──────────────────────────────────────────────────

        private static void CheckPois()
        {
            CampaignState s = NewWorld(42);
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            PlannedTerritory silent = ctx.Plan.Find("silent");
            var feats = new List<WorldFeature>();
            WorldFeatures.Query(ctx, silent.CenterX - silent.Radius, silent.CenterY - silent.Radius, silent.CenterX + silent.Radius, silent.CenterY + silent.Radius, feats);
            WorldFeature cache = feats.First(f => f.Kind == WorldFeatureKind.Cache);
            WorldFeature term = feats.First(f => f.Kind == WorldFeatureKind.Terminal);
            int notes = HomeExplorationService.PoiNotices;
            bool fogFirst = HomeExplorationService.CheckRecoverable(s, cache.Id, out _) != null && HomeExplorationService.RelicAt(s, new Vector2(cache.X, cache.Y)) == null;
            HomeGridService.RevealArea(s, new Vector2(cache.X, cache.Y), 6f);
            HomeGridService.RevealArea(s, new Vector2(term.X, term.Y), 6f);
            bool found = HomeExplorationService.IsFound(s, cache.Id) && HomeExplorationService.IsFound(s, term.Id) && HomeExplorationService.PoiNotices >= notes + 2
                         && !HomeExplorationService.IsFound(s, "relic:0:0");
            bool pick = HomeExplorationService.RelicAt(s, new Vector2(cache.X + 0.5f, cache.Y)) == cache.Id;
            bool desc = HomeExplorationService.TryDescribeRelic(s, cache.Id, out string ct, out string cb) && ct.Contains("资源缓存") && cb.Contains("废料")
                        && HomeExplorationService.TryDescribeRelic(s, term.Id, out string tt, out string tb) && tt.Contains("旧终端") && tb.Contains("技术数据");
            Expect(fogFirst && found && pick && desc,
                $"F1 资源缓存 / 旧终端：迷雾里不能下令、悬停不到（{fogFirst}）；揭开后记为已发现并通知（{found}），点得中（{pick}），悬停写名字、工期与奖励（“{ct}”）");

            // 下令（右键同一入口）：派机器的工单是“打开缓存 / 读取终端”；完工奖励只发一次。
            int m = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(3f, -3f));
            HomeValleyWorkOrders.WorkOrderOpResult order = HomeExplorationService.OrderRecovery(s, cache.Id, m);
            WorkOrderRecord rec = s.WorkOrders?.FirstOrDefault(o => o.TargetId == cache.Id);
            bool label = rec != null && HomeValleyAlarms.WorkKindName(rec) == GameText.Get("cache.work.label");
            HomeExplorationService.TryGetRelic(s, cache.Id, out HomeExplorationService.RelicInfo ci);
            int ground0 = s.GroundItems?.Length ?? 0;
            int scrap = HomeExplorationService.CompleteRecovery(s, rec ?? new WorkOrderRecord { TargetId = cache.Id });
            int scrapAgain = HomeExplorationService.CompleteRecovery(s, new WorkOrderRecord { TargetId = cache.Id });
            GroundItemRecord g = HomeValleyCargo.FindGroundItemBySalvageId(s, cache.Id);
            bool cacheOk = order.Success && label && scrap == ci.Scrap && scrapAgain == 0 && g != null && g.Amount == ci.Scrap && Vector2.Distance(g.Position, new Vector2(cache.X, cache.Y)) < 0.5f
                           && (s.GroundItems?.Length ?? 0) == ground0 + 1 && HomeExplorationService.IsRecovered(s, cache.Id)
                           && s.WorkOrders.Any(o => o.Kind == WorkOrderKind.Haul && o.TargetId == g.GroundItemId);
            long techBefore = HomeExplorationService.StateOf(s).TerminalTechData;
            long statBefore = GameLogic.Campaign.Economy.TechDataFlow.IncomeOf(s, GameLogic.Campaign.Economy.TechDataFlow.Terminal);
            HomeExplorationService.TryGetRelic(s, term.Id, out HomeExplorationService.RelicInfo ti);
            int tech = HomeExplorationService.CompleteRecovery(s, new WorkOrderRecord { TargetId = term.Id });
            int techAgain = HomeExplorationService.CompleteRecovery(s, new WorkOrderRecord { TargetId = term.Id });
            bool termOk = tech == ti.TechData && tech > 0 && techAgain == 0 && HomeExplorationService.StateOf(s).TerminalTechData == techBefore + tech
                          && CampaignEconomyLedger.Find(s, "poi-read:" + term.Id) != null && !string.IsNullOrEmpty(HomeExplorationService.LoreFor(ti));
            long statGain = GameLogic.Campaign.Economy.TechDataFlow.IncomeOf(s, GameLogic.Campaign.Economy.TechDataFlow.Terminal) - statBefore;
            var statLines = new List<(string Text, string Cls)>();
            GameLogic.Campaign.Economy.ResearchStats.Build(s, statLines);
            bool statRow = statGain == tech && statLines.Any(l => l.Text.Contains(GameText.Get("stats.research.src.terminal")));
            string mapLabel = HomeExplorationService.MapLabel(s, cache, out bool done);
            Expect(cacheOk && termOk && statRow && done && mapLabel.Contains("已打开"),
                $"F2 打开缓存：工单显示“{GameText.Get("cache.work.label")}”，完工后废料 {scrap} 放在缓存原地并开搬运单（{cacheOk}），重复完工不再发；读取终端：技术数据 +{tech} 入账（事务号 poi-read:…）+ 一条记录，重复不再发（{termOk}），统计面板“{GameText.Get("stats.research.src.terminal")}”行 +{statGain}（B19）；地图标签“{mapLabel}”");

            // 存读档：已发现 / 已完成的缓存与终端、统计都进存档。
            CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = Reload(Slot);
            Expect(back != null && HomeExplorationService.IsFound(back, term.Id) && HomeExplorationService.IsRecovered(back, cache.Id) && HomeExplorationService.IsRecovered(back, term.Id)
                   && HomeExplorationService.StateOf(back).CacheScrap == scrap,
                "F3 存读档：已发现 / 已打开 / 已读取与统计（废料、技术数据）读回一致（B10）");
        }

        // ── G 室内入口保护（卡片负向）──────────────────────────────────────────────

        private static void CheckEntranceProtect()
        {
            CampaignState s = NewWorld(1);
            PlannedAnchor gate = InteriorSiteService.EntranceFor(s, "relay_hall");
            Vector2 e = TerritoryAnchorChecks.EntryOf(s, gate);
            HomeGridService.RevealArea(s, e, gate.ProtectRadius + 20f);
            var inside = new GridCell(Mathf.RoundToInt(e.x) + gate.ProtectRadius - 3, Mathf.RoundToInt(e.y));
            var outside = new GridCell(Mathf.RoundToInt(e.x) + gate.ProtectRadius + 4, Mathf.RoundToInt(e.y));
            GridPlacementResult rin = HomeGridService.ValidatePlacement(s, "power_pole", inside, 0, checkCost: false);
            GridPlacementResult rout = HomeGridService.ValidatePlacement(s, "power_pole", outside, 0, checkCost: false);
            GridReason reason = rin.Reasons.FirstOrDefault(x => x.Code == GridBlockReason.EntranceProtect);
            string text = reason.TextKey != null ? reason.Describe() : string.Empty;
            bool beltOk = !HomeGridService.ValidateBeltCell(s, inside).Reasons.Any(x => x.Code == GridBlockReason.EntranceProtect);
            GridOpResult placed = PlanHistory.Place(s, "power_pole", inside, 0);
            bool refused = !placed.Success && placed.Placement != null && placed.Placement.Reasons.Exists(x => x.Code == GridBlockReason.EntranceProtect);
            bool guardAt = HomeGridService.EntranceGuardAt(s, inside) == gate && HomeGridService.EntranceGuardAt(s, outside) == null;
            Expect(reason.TextKey != null && text.Contains(GameText.Get(gate.Row.NameKey)) && text.Contains(gate.ProtectRadius.ToString()) && !rout.Reasons.Any(x => x.Code == GridBlockReason.EntranceProtect)
                   && beltOk && refused && guardAt,
                $"G1 卡片负向“室内入口被己方建筑堵住”：入口 {gate.ProtectRadius} 格内放建筑被现有建造校验拦下并写原因“{text}”（B06），圈外照常；地面传送带不挡路、不拦（{beltOk}）；真实放置同样被拒（{refused}）");
        }

        // ── H 室内场地 ────────────────────────────────────────────────────────

        private static void CheckInterior()
        {
            CampaignState s = NewWorld(42);
            PlannedAnchor gate = InteriorSiteService.EntranceFor(s, "relay_hall");
            Vector2 e = TerritoryAnchorChecks.EntryOf(s, gate);
            HomeGridService.RevealArea(s, e, 30f);
            // 负向 1：入口旁没有机器。
            string none = InteriorSiteService.CheckEnter(s, gate, out _);
            int a = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, Vector2.zero, e + new Vector2(1f, 1f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, Vector2.zero, e + new Vector2(-1f, 1f));
            WorldSimulation.StepMany(3);
            // 负向 2：机器在入口旁但在覆盖之外（入口离核心几百格，没有中继）——收不到“进入”命令。
            string outsideCoverage = InteriorSiteService.CheckEnter(s, gate, out List<int> ready0);
            Expect(none != null && none.Contains(gate.EntryRadius.ToString()) && outsideCoverage != null && outsideCoverage.Contains("覆盖") && ready0.Count == 0,
                $"H1 负向：入口旁没有机器 →“{none}”；机器在入口旁但在信号覆盖之外 →“{outsideCoverage}”（FGR-SIG-053，进入是远程命令）");

            // 有覆盖（测试注入：入口附近视为在覆盖里）→ 进入：自动存档（首领战前）、机器转进室内、镜头切过去、过渡、家园句柄移走。
            SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            string savePath = CampaignSaveService.SlotPath(Slot);
            DateTime saveBefore = File.Exists(savePath) ? File.GetLastWriteTimeUtc(savePath) : DateTime.MinValue;
            System.Threading.Thread.Sleep(20);
            int notes0 = NotifyMembers("interior_transit");
            bool entered = InteriorSiteService.TryEnter(s, gate.Id, out string why);
            int enterNotes = NotifyMembers("interior_transit") - notes0;
            bool noteLocated = LatestMember("interior_transit")?.HasLocation == true;
            InteriorSiteController site = WorldSimulation.Interior("relay_hall");
            bool saved = File.Exists(savePath) && File.GetLastWriteTimeUtc(savePath) > saveBefore;
            bool moved = Rec(a)?.RegionId == "relay_hall" && Rec(b)?.RegionId == "relay_hall";
            WorldSimulation.StepMany(2);
            bool homeDropped = !WorldSimulation.Home.MachineMarkersForTests.Any(m => m.LogicId == a || m.LogicId == b);
            bool inKernel = site != null && site.LiveMachineCount == 2 && site.PillarCount > 0 && site.ObstacleCount > site.PillarCount;
            bool observed = WorldView.IsObserved("relay_hall") && InteriorSiteService.TransitionActive && InteriorSiteService.TransitionText.Contains("地下中继大厅");
            Expect(entered && saved && moved && homeDropped && inKernel && observed && enterNotes == 1 && noteLocated,
                $"H2 FGR-GEN-042 进入：进入前写首领战前自动存档（BossEngageEnter，{saved}）；两台机器转进地下中继大厅（{moved}），家园句柄移走（{homeDropped}），室内内核有 {site?.LiveMachineCount} 台、立柱 {site?.PillarCount} 根；" +
                $"镜头切到室内并开始过渡“{InteriorSiteService.TransitionText}”（{observed}）；通知中心记一条“室内进出”（{enterNotes} 条，带入口坐标 {noteLocated}，不重复）" + (entered ? string.Empty : $"——拒绝：{why}"));

            // 室内与地表同时运行：镜头在家园时室内照样推进（命令一台机器往里走），家园也在推进。
            WorldView.Observe(HomeValleyLayout.RegionId);
            CombatSite ic = site.Combat;
            Vector2 start = site.LivePosition(a) ?? Vector2.zero;
            var goal = new Vector2(site.Width / 2f, site.Height * 0.6f);
            site.SquadCommands.SelectSingle(a, false);
            site.SquadCommands.IssueMoveTo(goal, paused: false);
            bool cmd = site.SquadCommands.Selection.Contains(a);
            long homeTicks = GameClock.Ticks;
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            Vector2 now = site.LivePosition(a) ?? Vector2.zero;
            Expect(cmd && Vector2.Distance(now, start) > 3f && GameClock.Ticks > homeTicks && !WorldView.IsObserved("relay_hall"),
                $"H3 FGR-ARC-002 室内与地表同时运行：镜头在家园时，室内的机器照样按命令移动 {Vector2.Distance(now, start):0.0} 格（与是否被观察无关）");

            // 离开：出口旁没有机器 → 原因；把机器带到出口旁 → 离开，回到地表入口旁；室内空了卸载。
            if (ic.TryGetMachineUnit(b, out int ubFar))
            {
                ic.SetUnitPosition(ubFar, new Vector2(site.Width / 2f - 8f, site.Height * 0.6f));
            }
            WorldSimulation.StepMany(2);
            string exitNone = InteriorSiteService.TryExit(s, "relay_hall", out string exitWhy) ? null : exitWhy;
            if (ic.TryGetMachineUnit(a, out int ua) && ic.TryGetMachineUnit(b, out int ub))
            {
                ic.SetUnitPosition(ua, site.ExitPoint + new Vector2(0.5f, 1f));
                ic.SetUnitPosition(ub, site.ExitPoint + new Vector2(-0.5f, 1f));
            }
            bool exited = InteriorSiteService.TryExit(s, "relay_hall", out string exitWhy2);
            WorldSimulation.StepMany(3);
            bool backHome = Rec(a)?.RegionId == HomeValleyLayout.RegionId && Rec(b)?.RegionId == HomeValleyLayout.RegionId
                            && Vector2.Distance(Rec(a).WorldPosition, e) < 15f && WorldSimulation.Home.MachineMarkersForTests.Any(m => m.LogicId == a);
            bool unloaded = WorldSimulation.Interior("relay_hall") == null && CombatSites.Get("relay_hall") == null;
            Expect(exitNone != null && exitNone.Contains("出口") && exited && backHome && unloaded,
                $"H4 离开：出口旁没有机器 →“{exitNone}”；带到出口旁 → 离开室内，回到地表入口旁（{backHome}），室内空了自动卸载（{unloaded}）" + (exited ? string.Empty : "——" + exitWhy2));

            // 负向 3：远征行军中的机器不能顺路进室内（它属于远征队）；负向 4：存档失败就不进去，机器原地不动。
            SignalCoverageService.OverrideForTests = null;
            SignalCoverageService.Invalidate();
            int c = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, Vector2.zero, e + new Vector2(0f, 2f));
            WorldSimulation.StepMany(2);
            SignalCoverageService.OverrideForTests = (site2, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            string blocked = Path.Combine(_dir, "blocked-dir");
            File.WriteAllText(blocked, "not a directory");
            CampaignSaveService.SaveDirectoryOverrideForTests = blocked; // 存档目录是一个文件：建目录失败 = 写盘必然失败
            bool refusedSave = !InteriorSiteService.TryEnter(s, gate.Id, out string saveWhy);
            CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
            bool stayed = Rec(c)?.RegionId == HomeValleyLayout.RegionId && WorldSimulation.Interior("relay_hall") == null;
            Expect(refusedSave && saveWhy == GameText.Get("interior.reason.save_failed") && stayed,
                $"H5 负向（B10）：进入前的自动存档失败 → 不进去“{saveWhy}”，机器原地不动（{stayed}）");

            // 负向 5（B11）：室内机器全部阵亡 → 发通知、卸载场地（不软锁），入口照样可以再进。
            bool reentered = InteriorSiteService.TryEnter(s, gate.Id, out string reWhy);
            InteriorSiteController again = WorldSimulation.Interior("relay_hall");
            int wipeNotes0 = NotifyMembers("interior_transit");
            // 入口旁的机器都会一起进去（含 H4 离开后站在入口旁的两台）：把室内的每一台都打到阵亡。
            int[] inside = MachineRegistry.AllRecords.Where(r => r != null && r.IsAlive && r.RegionId == "relay_hall").Select(r => r.LogicId).ToArray();
            foreach (int id in inside)
            {
                if (again != null && again.Combat.TryGetMachineUnit(id, out int uc))
                {
                    again.Combat.SetUnitHealth(uc, 0f, 100f, false);
                }
                MachineRegistry.MarkDeadByLogicId(id);
            }
            WorldSimulation.StepMany(3);
            bool wipedUnloaded = WorldSimulation.Interior("relay_hall") == null && CombatSites.Get("relay_hall") == null;
            bool wipeNote = NotifyMembers("interior_transit") > wipeNotes0
                            && (LatestMember("interior_transit")?.DetailText ?? string.Empty).Contains(InteriorSiteService.SurfaceName("relay_hall"));
            int d = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, Vector2.zero, e + new Vector2(1f, -1f));
            WorldSimulation.StepMany(2);
            bool canAgain = InteriorSiteService.CheckEnter(s, gate, out List<int> readyAgain) == null && readyAgain.Contains(d);
            Expect(reentered && wipedUnloaded && wipeNote && canAgain,
                $"H8 负向（B11）：室内 {inside.Length} 台机器全部阵亡 → 场地自动卸载（{wipedUnloaded}）并发通知“{LatestMember("interior_transit")?.DetailText}”（{wipeNote}）；入口照样可以再进（{canAgain}）" +
                (reentered ? string.Empty : "——再次进入被拒：" + reWhy));
            SignalCoverageService.OverrideForTests = null;
            SignalCoverageService.Invalidate();
        }

        /// <summary>H 续：暂停与 0.5x～3x 倍速矩阵、室内中途存读档接着跑（读档时室内一起恢复、镜头放回）。</summary>
        private static void CheckInteriorSpeedAndSave()
        {
            string RunAt(float speed, bool pauseHalf)
            {
                CampaignState s = NewWorld(42);
                PlannedAnchor gate = InteriorSiteService.EntranceFor(s, "relay_hall");
                Vector2 e = TerritoryAnchorChecks.EntryOf(s, gate);
                HomeGridService.RevealArea(s, e, 30f);
                int a = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, Vector2.zero, e + new Vector2(1f, 1f));
                WorldSimulation.StepMany(3);
                SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
                InteriorSiteService.TryEnter(s, gate.Id, out _);
                InteriorSiteController site = WorldSimulation.Interior("relay_hall");
                site.SquadCommands.SelectSingle(a, false);
                site.SquadCommands.IssueMoveTo(new Vector2(site.Width / 2f + 10f, site.Height * 0.7f), paused: false);
                long target = GameClock.Ticks + GameClock.StepHz * 6;
                GameClock.SetSpeed(speed);
                int frames = 0;
                bool pausedNoProgress = true;
                while (GameClock.Ticks < target && frames < 20000)
                {
                    if (pauseHalf && frames == 30)
                    {
                        GameClock.SetPaused(true);
                        long t0 = GameClock.Ticks;
                        Vector2 p0 = site.LivePosition(a) ?? Vector2.zero;
                        for (int k = 0; k < 20; k++)
                        {
                            WorldSimulation.Frame(1f / 60f, target);
                        }
                        pausedNoProgress = GameClock.Ticks == t0 && Vector2.Distance(p0, site.LivePosition(a) ?? Vector2.zero) < 1e-4f;
                        GameClock.SetPaused(false);
                    }
                    WorldSimulation.Frame(1f / 60f, target);
                    frames++;
                }
                GameClock.SetSpeed(1f);
                Vector2 p = site.LivePosition(a) ?? Vector2.zero;
                SignalCoverageService.OverrideForTests = null;
                return (pausedNoProgress ? string.Empty : "PAUSE-MOVED ") + GameClock.Ticks + "@" + p.x.ToString("R") + "," + p.y.ToString("R");
            }
            string r05 = RunAt(0.5f, false);
            string r1 = RunAt(1f, true);
            string r2 = RunAt(2f, false);
            string r3 = RunAt(3f, false);
            Expect(r05 == r1 && r1 == r2 && r2 == r3 && !r1.Contains("PAUSE"),
                $"H6 B09 倍速矩阵：室内机器移动 6 游戏秒后的位置在 0.5x / 1x（中途暂停 20 帧不走）/ 2x / 3x 下逐位相同（{r1}）");

            // 室内中途存读档：读档时室内一起恢复（从战斗快照），机器位置接着走；存档时正看着室内 → 镜头放回室内。
            CampaignState w = NewWorld(1);
            PlannedAnchor g2 = InteriorSiteService.EntranceFor(w, "relay_hall");
            Vector2 e2 = TerritoryAnchorChecks.EntryOf(w, g2);
            HomeGridService.RevealArea(w, e2, 30f);
            int m = Spawn(w, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, Vector2.zero, e2 + new Vector2(1f, 1f));
            WorldSimulation.StepMany(3);
            SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            InteriorSiteService.TryEnter(w, g2.Id, out _);
            InteriorSiteController s1 = WorldSimulation.Interior("relay_hall");
            s1.SquadCommands.SelectSingle(m, false);
            s1.SquadCommands.IssueMoveTo(new Vector2(s1.Width / 2f - 12f, s1.Height * 0.8f), paused: false);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            Vector2 continued = s1.LivePosition(m) ?? Vector2.zero;
            long tickContinued = GameClock.Ticks;
            CampaignState back = Reload(Slot);
            int resumed = InteriorSiteService.ResumeAll(back);
            InteriorSiteController s2 = WorldSimulation.Interior("relay_hall");
            bool focus = WorldView.IsObserved("relay_hall") && back.World.InteriorFocusId == "relay_hall";
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            Vector2 afterLoad = s2?.LivePosition(m) ?? Vector2.zero;
            bool same = GameClock.Ticks == tickContinued && Vector2.Distance(afterLoad, continued) < 1e-3f;
            Expect(sv.Success && resumed == 1 && s2 != null && s2.LiveMachineCount == 1 && Rec(m)?.RegionId == "relay_hall" && focus && same,
                $"H7 B10 室内中途存读档：读档时室内一起恢复（{resumed} 个，机器 {s2?.LiveMachineCount} 台），镜头回到室内（{focus}）；读档后接着走 2 游戏秒与不存档时逐位相同（{same}：{afterLoad} vs {continued}）");
            SignalCoverageService.OverrideForTests = null;
            SignalCoverageService.Invalidate();
        }

        /// <summary>
        /// H 续（修复轮 2）：H9 室内有机器时远征出发事务回滚（正式回滚入口 ExpeditionDepartureService → GameRoot.ReenterHomeValley）→ 室内一起恢复、机器继续推进（审查 P1，B10 / B24）；
        /// H10 数据中心也走一遍进入 → 离开（审查 P2：此前只在地下中继大厅上测过）；H11 入口旁机器手上有工作时的中断规则（审查 P2）。
        /// </summary>
        private static void CheckInteriorRollbackAndDataCenter()
        {
            CampaignState w = NewWorld(1);
            RegionRecord ruins = FracturedCityRegion.Find(w);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            PlannedAnchor g = InteriorSiteService.EntranceFor(w, "relay_hall");
            Vector2 e = TerritoryAnchorChecks.EntryOf(w, g);
            HomeGridService.RevealArea(w, e, 30f);
            int inside = Spawn(w, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, Vector2.zero, e + new Vector2(1f, 1f));
            int traveller = Spawn(w, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -6f));
            WorldSimulation.StepMany(3);
            SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            ExpeditionDepartureService.DepartureResult dep = default;
            InteriorSiteController s1 = null;
            bool entered = false;
            try
            {
                entered = InteriorSiteService.TryEnter(w, g.Id, out _);
                s1 = WorldSimulation.Interior("relay_hall");
                var goal = new Vector2(s1.Width / 2f - 12f, s1.Height * 0.85f);
                s1.SquadCommands.SelectSingle(inside, false);
                s1.SquadCommands.IssueMoveTo(goal, paused: false);
                WorldView.Observe(HomeValleyLayout.RegionId); // 出发是在家园看着的
                WorldSimulation.StepMany(GameClock.StepHz);
                ExpeditionMarchService.BeginFaultInjectionForTests = () => throw new InvalidOperationException("selfcheck-begin-fault");
                try
                {
                    dep = ExpeditionDepartureService.TryDepart(new[] { traveller }, true);
                }
                finally
                {
                    ExpeditionMarchService.BeginFaultInjectionForTests = null;
                }
                CampaignState now = CampaignSession.Current;
                InteriorSiteController s2 = WorldSimulation.Interior("relay_hall");
                bool rolled = dep.Outcome == ExpeditionDepartureService.DepartureOutcome.RolledBack && !ReferenceEquals(now, w);
                bool resumed = s2 != null && s2.IsLoaded && !ReferenceEquals(s2, s1) && s2.LiveMachineCount == 1 && Rec(inside)?.RegionId == "relay_hall"
                               && Rec(traveller)?.RegionId == HomeValleyLayout.RegionId && WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded;
                Vector2 p0 = s2?.LivePosition(inside) ?? Vector2.zero;
                WorldSimulation.StepMany(GameClock.StepHz * 2);
                Vector2 p1 = s2?.LivePosition(inside) ?? Vector2.zero;
                bool advancing = Vector2.Distance(p0, p1) > 0.3f || Vector2.Distance(p1, goal) < 1.5f;
                bool homeView = WorldView.IsObserved(HomeValleyLayout.RegionId);
                Expect(entered && rolled && resumed && advancing && homeView,
                    $"H9 B10 / B24 室内有机器时出发事务回滚（{dep.Outcome}）：读回出发前档、重进家园的同时室内一起恢复（{resumed}：室内 {s2?.LiveMachineCount} 台），机器接着按命令走 {Vector2.Distance(p0, p1):0.0} 格（{advancing}）；镜头在家园（{homeView}）");
            }
            finally
            {
                ExpeditionMarchService.BeginFaultInjectionForTests = null;
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
            }

            // H10 数据中心：进入 → 室内内核建好、镜头切过去 → 带到出口旁离开 → 回到地表入口旁、室内卸载。
            CampaignState d = NewWorld(42);
            PlannedAnchor dg = InteriorSiteService.EntranceFor(d, "data_center");
            Vector2 de = TerritoryAnchorChecks.EntryOf(d, dg);
            HomeGridService.RevealArea(d, de, 30f);
            int x = Spawn(d, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, Vector2.zero, de + new Vector2(1f, 1f));
            WorldSimulation.StepMany(3);
            SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            try
            {
                bool ok = InteriorSiteService.TryEnter(d, dg.Id, out string why);
                InteriorSiteController dc = WorldSimulation.Interior("data_center");
                WorldSimulation.StepMany(2);
                bool inKernel = ok && dc != null && dc.IsLoaded && dc.LiveMachineCount == 1 && Rec(x)?.RegionId == "data_center" && WorldView.IsObserved("data_center")
                                && InteriorSiteService.TransitionText.Contains(InteriorSiteService.SurfaceName("data_center"));
                string enterText = InteriorSiteService.TransitionText;
                if (dc != null && dc.Combat.TryGetMachineUnit(x, out int ux))
                {
                    dc.Combat.SetUnitPosition(ux, dc.ExitPoint + new Vector2(0.5f, 1f));
                }
                bool exited = dc != null && InteriorSiteService.TryExit(d, "data_center", out _);
                WorldSimulation.StepMany(3);
                bool back = Rec(x)?.RegionId == HomeValleyLayout.RegionId && Vector2.Distance(Rec(x).WorldPosition, de) < 15f
                            && WorldSimulation.Interior("data_center") == null && CombatSites.Get("data_center") == null;
                Expect(inKernel && exited && back,
                    $"H10 数据中心（种子 42，入口 {dg.Id}）：进入 → 机器转进室内、镜头切过去、过渡“{enterText}”（{inKernel}）；带到出口旁离开 → 回到地表入口旁、室内卸载（{back}）" + (ok ? string.Empty : "——拒绝：" + why));
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
            }

            // H11（审查 P2）：入口旁的机器手上有工作——与远征出发同一条中断规则。两台都在做不可中断的紧急修复 → 拒绝并写原因；
            // 一台改成普通搬运 → 面板计数“会中断 1 台”，进入时它的搬运单被取消（不跟着进室内挂着），紧急修复那台留在门外、工单不动。
            // 工单是直接写进存档的最小记录（不推进世界步，避免工单系统去处理这两条假单）。
            CampaignState h = NewWorld(42);
            PlannedAnchor hg = InteriorSiteService.EntranceFor(h, "relay_hall");
            Vector2 he = TerritoryAnchorChecks.EntryOf(h, hg);
            HomeGridService.RevealArea(h, he, 30f);
            int ma = Spawn(h, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, Vector2.zero, he + new Vector2(1f, 1f));
            int mb = Spawn(h, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, Vector2.zero, he + new Vector2(-1f, 1f));
            WorldSimulation.StepMany(3);
            SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            var ra = new WorkOrderRecord { WorkOrderId = "selfcheck-interior-repair", Kind = WorkOrderKind.Repair, State = WorkOrderState.InProgress, AssignedMachineLogicId = ma, ResourceTransactionId = string.Empty };
            var rb = new WorkOrderRecord { WorkOrderId = "selfcheck-interior-haul", Kind = WorkOrderKind.Repair, State = WorkOrderState.InProgress, AssignedMachineLogicId = mb, ResourceTransactionId = string.Empty };
            WorkOrderRecord[] ordersBefore = h.WorkOrders ?? Array.Empty<WorkOrderRecord>();
            h.WorkOrders = ordersBefore.Concat(new[] { ra, rb }).ToArray();
            try
            {
                string busyWhy = InteriorSiteService.CheckEnter(h, hg, out List<int> busyReady);
                rb.Kind = WorkOrderKind.Haul;
                string okWhy = InteriorSiteService.CheckEnter(h, hg, out List<int> ready2);
                int interrupting = InteriorSiteService.CountInterrupting(h, ready2);
                bool went = InteriorSiteService.TryEnter(h, hg.Id, out string goWhy);
                bool cancelled = rb.State == WorkOrderState.Cancelled && rb.AssignedMachineLogicId == 0;
                bool split = Rec(mb)?.RegionId == "relay_hall" && Rec(ma)?.RegionId == HomeValleyLayout.RegionId
                             && ra.State == WorkOrderState.InProgress && ra.AssignedMachineLogicId == ma;
                Expect(busyWhy == GameText.Format("interior.reason.busy", 2) && busyReady.Count == 0 && okWhy == null && ready2.Count == 1 && ready2[0] == mb && interrupting == 1
                       && went && cancelled && split,
                    $"H11 B06 / FGR-BASE-020 入口旁的机器手上有工作（与远征出发同一条中断规则）：两台都在紧急修复 →“{busyWhy}”；一台改成搬运 → 可以进 {ready2.Count} 台、面板计数会中断 {interrupting} 台；" +
                    $"进入后它的搬运单已取消（{cancelled}），紧急修复那台留在门外、工单不动（{split}）" + (went ? string.Empty : "——拒绝：" + goWhy));
            }
            finally
            {
                ra.State = WorkOrderState.Cancelled;
                rb.State = WorkOrderState.Cancelled;
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
            }
        }

        // ── I 领地名称提示 ─────────────────────────────────────────────────────

        private static void CheckBannerAndCue()
        {
            CampaignState s = NewWorld(1);
            WorldPlan plan = WorldGenService.PlanFor(s);
            var go = new GameObject("FgTerritoryHud");
            try
            {
                VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "WorldPlacesHud.uxml");
                VisualElement root = tree.CloneTree();
                WorldPlacesHudUIToolkit hud = go.AddComponent<WorldPlacesHudUIToolkit>();
                hud.BindView(root);
                hud.ResetBannerForTests();
                PlannedTerritory silent = plan.Find("silent");
                var home = new Vector2(plan.CoreX, plan.CoreY);
                var inside = new Vector2(silent.CenterX, silent.CenterY);
                hud.Scan(s, home); // 基线（第一次看到焦点不弹）
                bool quietBaseline = !hud.BannerVisible && hud.BannerShowCount == 0;
                hud.Scan(s, inside);
                string enter = hud.BannerTitleText;
                string sub = hud.BannerSubText;
                hud.Scan(s, inside);
                int afterStay = hud.BannerShowCount;
                hud.Scan(s, home);
                string leave = hud.BannerTitleText;
                PlannedTerritory lake = plan.Find("clarity");
                var nearBelt = new Vector2(lake.CenterX + lake.OuterRadius + 10, lake.CenterY);
                hud.Scan(s, nearBelt);
                string belt = hud.BannerTitleText;
                var outer = new Vector2(plan.CoreX - 3200, plan.CoreY + 900);
                hud.Scan(s, new Vector2(plan.CoreX, plan.CoreY));
                hud.Scan(s, outer);
                string outerText = hud.BannerTitleText;
                Expect(quietBaseline && enter == GameText.Format("ui.territory.banner.enter", GameText.Get(silent.NameKey)) && sub == GameText.Get(plan.TemplateFor("silent").IntroKey)
                       && afterStay == 1 && leave.Contains("离开") && belt.Contains("接近") && belt.Contains(GameText.Get(lake.HazardKey)) && outerText == GameText.Get("ui.territory.outer"),
                    $"I1 FG17 第 4 节 / DEBT-FG8EXP02-05：进入领地顶部提示“{enter}”+“{sub}”，在领地里不重复；离开“{leave}”；接近外圈危害带“{belt}”；进入外延区“{outerText}”；开局 / 读档第一次看到焦点不弹（{quietBaseline}）");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }

            // 远征队“进入敌方领地”提醒（行军判定在模拟步里，与观察无关；存档去重）：队伍中心进入领地 → 一次；在领地里不重复；离开后再进 → 再一次。
            var m = new ExpeditionMarchState { SiteId = FracturedCityLayout.RegionId, EncounteredIds = Array.Empty<string>() };
            System.Reflection.MethodInfo eval = typeof(ExpeditionMarchService).GetMethod("EvaluateEncounters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            PlannedTerritory st = plan.Find("silent");
            int cues0 = ExpeditionMarchService.TerritoryCueCount;
            int terrNotes0 = NotifyMembers("territory_entered");
            eval.Invoke(null, new object[] { s, m, new Vector2(plan.CoreX, plan.CoreY) });
            eval.Invoke(null, new object[] { s, m, new Vector2(st.CenterX, st.CenterY) });
            eval.Invoke(null, new object[] { s, m, new Vector2(st.CenterX + 5, st.CenterY) });
            int once = ExpeditionMarchService.TerritoryCueCount - cues0;
            eval.Invoke(null, new object[] { s, m, new Vector2(plan.CoreX, plan.CoreY) });
            eval.Invoke(null, new object[] { s, m, new Vector2(st.CenterX, st.CenterY) });
            int twice = ExpeditionMarchService.TerritoryCueCount - cues0;
            int terrNotes = NotifyMembers("territory_entered") - terrNotes0;
            Expect(once == 1 && twice == 2 && terrNotes == 2 && m.TerritoryId == "silent" && FeedbackCueCatalog.Get(FeedbackCueId.ExpeditionMarchTerritory) != null,
                $"I2 远征队行军进入敌方领地：提醒一次（{once}）、在领地里不重复、离开再进再提醒（{twice}）；每次提醒在通知中心记一条“进入领地”（{terrNotes} 条，离家报告进“事件”段）；当前领地记进存档字段（{m.TerritoryId}）");
        }

        // ── J 检查单“路线经过危害”（DEBT-FG8EXP01-04）──────────────────────────────────

        private static void CheckChecklistHazard()
        {
            CampaignState s = NewWorldState(1, 3);
            WorldPlan plan = WorldGenService.PlanFor(s);
            PlannedTerritory lake = plan.Find("clarity");
            var core = new Vector2(plan.CoreX, plan.CoreY);
            var through = new List<Vector2> { core, new Vector2(lake.CenterX, lake.CenterY) };
            var clear = new List<Vector2> { core, core + new Vector2(30f, 0f) };
            PlannedHazardZone z = plan.HazardZones.First(x => x.TerritoryId == "silent");
            PlannedTerritory silent = plan.Find("silent");
            var toZone = new List<Vector2> { new Vector2(silent.CenterX + silent.Radius * 0.9f, silent.CenterY), new Vector2(z.CenterX, z.CenterY) };
            ExpeditionChecklistService.HazardHit? hb = ExpeditionChecklistService.DefaultHazardProbe(s, through);
            ExpeditionChecklistService.HazardHit? hn = ExpeditionChecklistService.DefaultHazardProbe(s, clear);
            ExpeditionChecklistService.HazardHit? hz = ExpeditionChecklistService.DefaultHazardProbe(s, toZone);
            Expect(hb.HasValue && hb.Value.Name.Contains(GameText.Get(lake.HazardKey)) && hb.Value.Consequence.Contains(AcidMarshService.HoverLine())
                   && !hn.HasValue && hz.HasValue && hz.Value.Name.Contains(GameText.Get("hazard.emi_noise.name"))
                   && ExpeditionChecklistService.HazardProbe == (Func<CampaignState, IReadOnlyList<Vector2>, ExpeditionChecklistService.HazardHit?>)ExpeditionChecklistService.DefaultHazardProbe,
                $"J1 DEBT-FG8EXP01-04 检查单接入真实危害数据：路线穿过沉淀湖外圈 →“{hb?.Name}”（后果写效果 + 酸沼“生效中”与涂层状态，FG10-REG-01 起与悬停同一口径），穿过静默危害区 →“{hz?.Name}”；不经过时不提醒；默认探测就是真实数据");
        }

        // ── K 远征地点内部的覆盖根（DEBT-FG8EXP02-02）─────────────────────────────────

        private static void CheckSiteCoverageRoot()
        {
            CampaignState s = NewWorld(1);
            ExpeditionSiteAnchors.EnsureResolved(s);
            ExpeditionSiteAnchorRecord r = ExpeditionSiteAnchors.Find(s, FracturedCityLayout.RegionId);
            int mode = SignalCoverageService.SiteRootMode;
            SignalCoverageService.Invalidate();
            bool m1 = SignalCoverageService.SiteRootConnected(s, FracturedCityLayout.RegionId) && SignalCoverageService.Sample(FracturedCityLayout.RegionId, SignalCoverageService.ExpeditionUplinkPosition(FracturedCityLayout.RegionId)).Covered;
            try
            {
                SignalCoverageService.SiteRootModeOverrideForTests = 2;
                SignalCoverageService.Invalidate();
                bool farOff = !SignalCoverageService.SiteRootConnected(s, FracturedCityLayout.RegionId);
                bool sampleOff = !SignalCoverageService.Sample(FracturedCityLayout.RegionId, SignalCoverageService.ExpeditionUplinkPosition(FracturedCityLayout.RegionId)).Covered;
                // 正向：锚点落进核心覆盖（测试把锚点挪到核心 100 格内）→ 地点里有覆盖。
                int ox = r.X, oy = r.Y;
                GridCell core = HomeGridService.CorePivot(s);
                r.X = core.X + 100;
                r.Y = core.Y;
                SignalCoverageService.Invalidate();
                bool nearOn = SignalCoverageService.SiteRootConnected(s, FracturedCityLayout.RegionId)
                              && SignalCoverageService.Sample(FracturedCityLayout.RegionId, SignalCoverageService.ExpeditionUplinkPosition(FracturedCityLayout.RegionId)).Covered;
                r.X = ox;
                r.Y = oy;
                Expect(mode == 1 && m1 && farOff && sampleOff && nearOn,
                    $"K1 DEBT-FG8EXP02-02：默认模式 {mode}（接入点恒连通，沿用 FG1-SIG-07，待用户定是否切换）；切到模式 2 后——锚点（{ox},{oy}）不在核心连通的覆盖里 → 地点里没有覆盖（{farOff} / {sampleOff}），" +
                    $"锚点在核心覆盖里 → 地点里有覆盖（{nearOn}）");
            }
            finally
            {
                SignalCoverageService.SiteRootModeOverrideForTests = null;
                SignalCoverageService.Invalidate();
            }
        }

        // ── L Demo 模板回归 ───────────────────────────────────────────────────

        private static void CheckDemoTemplates()
        {
            bool Same(IEnumerable<(string id, Vector2 pos, float r)> layout, string template)
            {
                List<AnchorTemplatePoint> pts = WorldGenContent.TemplatePoints(template);
                List<(string id, Vector2 pos, float r)> l = layout.ToList();
                return pts.Count == l.Count && l.Zip(pts, (a, p) => a.id == p.PointId && Mathf.Approximately(a.pos.x, p.LocalX) && Mathf.Approximately(a.pos.y, p.LocalY) && Mathf.Approximately(a.r, p.Radius)).All(x => x);
            }
            bool city = Same(FracturedCityLayout.AllAnchors().Select(a => (a.Id, a.Position, a.ClearanceRadius)), "fractured_city");
            bool foundry = Same(FoundryOutpostLayout.AllAnchors().Select(a => (a.Id, a.Position, a.ClearanceRadius)), "foundry_outpost");
            bool entries = WorldGenContent.TemplatePoints("fractured_city").Single(p => p.Kind == "entry").PointId == FracturedCityLayout.EntryEvacId
                           && WorldGenContent.TemplatePoints("foundry_outpost").Single(p => p.Kind == "entry").PointId == FoundryOutpostLayout.EntryEvacId;
            bool validate = FracturedCityLayout.Validate().Count == 0 && FoundryOutpostLayout.Validate().Count == 0;
            Expect(city && foundry && entries && validate,
                $"L1 Demo 回归：破碎都市 {FracturedCityLayout.AllAnchors().Count()} 个、铸造前哨（外围 + 核心区）{FoundryOutpostLayout.AllAnchors().Count()} 个固定物件逐点转成锚点模板（ID / 局部坐标 / 净空一致，入口 = 出生撤离点），Demo 布局自检仍为零违规（Demo 地点内部玩法回归见 FgFracturedCity / FgFoundry 各段）");

            // 嵌进星球（3 个种子）：每个模板点位落在平整范围里、脚下可走；入口朝家园、等于远征队行军目的地。
            var bad = new List<string>();
            var depths = new List<string>();
            foreach (int seed in Seeds)
            {
                CampaignState s = NewWorldState(seed, 3);
                WorldGenContext ctx = WorldGenService.ContextFor(s);
                foreach (string id in new[] { "fractured_city", "foundry_outpost" })
                {
                    PlannedAnchor a = ctx.Plan.FindAnchor(id);
                    foreach (PlannedAnchorPoint p in a.Points)
                    {
                        ctx.Source.Sample(p.X, p.Y, out byte t, out _);
                        if (t != GridContent.TerrainCode("buildable") || Dist(p.X, p.Y, a.OriginX, a.OriginY) > a.FootprintRadius + 0.5)
                        {
                            bad.Add($"{seed}:{id}.{p.PointId}");
                        }
                    }
                    GridCell cand = ExpeditionSiteAnchors.Candidate(s, TerritoryAnchorChecks.ExpeditionSiteOf(id), 0, out string tid);
                    if (cand.X != a.EntryX || cand.Y != a.EntryY || tid != a.Row.TerritoryId)
                    {
                        bad.Add($"{seed}:{id} 远征锚点 ≠ 模板入口");
                    }
                    // FG8-GEN-02 修复轮（冒烟 StepMarchArrived“途中进入阵营领地”0 次）：任何走到入口的路线在领地里至少走过“入口离领地边界的距离”，
                    // 减去到达半径（12 台编队）后还要剩下一游戏秒的行军（行军判定每游戏秒一次；10 格/秒比现有最快的行军速度还快），到达前才一定提醒“进入领地”。
                    PlannedTerritory terr = a.Territory;
                    float depth = (float)(terr.Radius - Dist(a.EntryX, a.EntryY, terr.CenterX, terr.CenterY));
                    float window = depth - ExpeditionMarchService.GoalRadius(ExpeditionMarchService.PhaseOutbound, 12);
                    depths.Add($"{seed}:{id} 入口深 {depth:0} 格、到达前领地内 ≥ {window:0} 格");
                    if (depth < a.FootprintRadius || window < 10f)
                    {
                        bad.Add($"{seed}:{id} 入口离领地边界 {depth:0} 格（平整半径 {a.FootprintRadius}、到达前领地内只剩 {window:0} 格）");
                    }
                }
            }
            Expect(bad.Count == 0, $"L2 三个种子：Demo 两张手工地图的全部关键点在星球上都落在平整过的空地上（形状按模板旋转、入口朝家园），远征锚点第 0 次试探 = 模板入口；" +
                                   $"入口离领地边界至少一个平整半径，远征队到达前一定先走进领地（{string.Join("，", depths)}）" + (bad.Count == 0 ? string.Empty : "——" + string.Join("，", bad.Take(6))));
        }

        // ── M 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerf()
        {
            CampaignState s = NewWorldState(42, 3);
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            int n = 5;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                WorldGenContext.Build(42 + i, 3, ctx.Settings, ctx.Core, 32);
            }
            sw.Stop();
            double v3Ms = sw.Elapsed.TotalMilliseconds / n;
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                WorldGenContext.Build(42 + i, 2, WorldSettings.Resolve(2, WorldGenContent.DefaultPresetId), ctx.Core, 32);
            }
            sw.Stop();
            double v2Ms = sw.Elapsed.TotalMilliseconds / n;
            // 领地里的区块生成（风格 + 平整形状）：Burst 单块耗时。
            PlannedAnchor fc = ctx.Plan.FindAnchor("foundry_outpost");
            var terrain = new byte[32 * 32];
            var pollution = new byte[32 * 32];
            sw.Restart();
            int chunks = 0;
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    ctx.Source.FillChunk(Fd(fc.OriginX, 32) + dx, Fd(fc.OriginY, 32) + dy, 32, terrain, pollution);
                    chunks++;
                }
            }
            sw.Stop();
            double chunkMs = sw.Elapsed.TotalMilliseconds / chunks;
            ExpectPerf(true, $"M1 规划层（含锚点 / 风格 / 危害区）v3 每个世界 {v3Ms:0.0} ms（v2 {v2Ms:0.0} ms，读档时一次）；领地锚点附近的区块生成每块 {chunkMs:0.00} ms（Burst）",
                new PerfGate.Metric("规划层 v3 每世界 ms", v3Ms, Math.Max(60.0, v2Ms * 3), false),
                new PerfGate.Metric("锚点附近单区块生成 ms", chunkMs, 5.0, false));
            PerfLines.Add($"规划层 v3 {v3Ms:0.0} ms / v2 {v2Ms:0.0} ms；锚点附近单区块 {chunkMs:0.00} ms（Editor batchmode，Burst JIT）");

            // 地图 / 星球标记查附近据点：与据点总数无关（几百个据点里只查镜头附近）。
            CampaignState w = NewWorld(42);
            HomeGridService.RevealArea(w, new Vector2(HomeGridService.CorePivot(w).X + 3500, HomeGridService.CorePivot(w).Y), 1500f);
            int total = WorldOutpostSystem.Outposts(w).Count;
            var near = new List<OutpostRecord>();
            sw.Restart();
            for (int i = 0; i < 1000; i++)
            {
                near.Clear();
                WorldOutpostSystem.OutpostsNear(w, HomeGridService.CorePivot(w).X, HomeGridService.CorePivot(w).Y, 200f, near);
            }
            sw.Stop();
            double queryUs = sw.Elapsed.TotalMilliseconds; // 1000 次的毫秒 = 每次微秒
            sw.Restart();
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            sw.Stop();
            double stepMs = sw.Elapsed.TotalMilliseconds / (GameClock.StepHz * 5);
            ExpectPerf(true, $"M2 {total} 个据点（含外延区按需登记）：查镜头附近据点每次 {queryUs:0.0} µs；世界每步 {stepMs:0.000} ms（只推进醒着的 {WorldOutpostSystem.LastSteppedOutposts} 个据点）",
                new PerfGate.Metric("附近据点查询 µs", queryUs, 50.0, false),
                new PerfGate.Metric("世界每步 ms", stepMs, 1.0, false));
            PerfLines.Add($"{total} 个据点：附近查询 {queryUs:0.0} µs / 次；世界每步 {stepMs:0.000} ms");
        }

        // ── O 界面布局探针 ─────────────────────────────────────────────────────

        private static void CheckLayoutProbe()
        {
            string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "WorldPlacesHud.uxml", "EntranceWindow", stressFill: true, prepare: rr =>
            {
                rr.panel.visualTree.Q<VisualElement>("EntranceRoot")?.RemoveFromClassList("uk-hidden");
                rr.panel.visualTree.Q<VisualElement>("PlaceBanner")?.RemoveFromClassList("uk-hidden");
            });
            Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "O1 地点界面（入口 / 出口面板、领地名称提示）布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        /// <summary>只建世界状态（不载入家园）：规划层 / 生成结果类的检查用。</summary>
        private static CampaignState NewWorldState(int seed, int version)
        {
            WorldSimulation.UnloadAll();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            CampaignState s = CampaignState.CreateNew("fgterritory-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(version, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            return s;
        }

        /// <summary>新世界：家园载入（开局登记据点）、镜头在家园。</summary>
        private static CampaignState NewWorld(int seed, int version = 3)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fgterritory-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(version, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            return s;
        }

        /// <summary>读档：卸载世界 → 从槽位恢复 → 载入家园（resume）→ 镜头在家园。</summary>
        private static CampaignState Reload(int fromSlot)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            File.Copy(CampaignSaveService.SlotPath(fromSlot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(HomeValleyLayout.RegionId);
            return r.State;
        }

        private static int Spawn(CampaignState s, string chassis, string blueprint, Vector2 offset, Vector2? at = null)
        {
            Vector2 p = at ?? HomeValleyLayout.Core.Position + offset;
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, p, 120f, 120f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static double Dist(int x0, int y0, int x1, int y1)
        {
            double dx = x0 - x1;
            double dy = y0 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static int Fd(int v, int c) => (int)WorldGenMath.FloorDiv(v, c);

        private static int NotifyMembers(string typeId) => GameLogic.Notifications.NotificationCenter.History
            .Where(e => e.Type != null && e.Type.Id == typeId).Sum(e => e.Members.Count);

        private static GameLogic.Notifications.NotificationMember LatestMember(string typeId) => GameLogic.Notifications.NotificationCenter.History
            .LastOrDefault(e => e.Type != null && e.Type.Id == typeId)?.Latest;

        private static ulong ChunkHash(WorldTerrainSource src, int cx, int cy)
        {
            var t = new byte[32 * 32];
            var p = new byte[32 * 32];
            src.FillChunk(cx, cy, 32, t, p);
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < t.Length; i++)
            {
                h = (h ^ t[i]) * 1099511628211UL;
                h = (h ^ p[i]) * 1099511628211UL;
            }
            return h;
        }

        private static ulong MapChunkHash(HomeGridMap map, int cx, int cy)
        {
            ulong h = 14695981039346656037UL;
            for (int y = 0; y < 32; y++)
            {
                for (int x = 0; x < 32; x++)
                {
                    var c = new GridCell(cx * 32 + x, cy * 32 + y);
                    h = (h ^ map.GetTerrain(c)) * 1099511628211UL;
                    h = (h ^ map.GetPollution(c)) * 1099511628211UL;
                }
            }
            return h;
        }

        /// <summary>领地中心 0.3 倍半径以内 / 0.85～1 倍半径的环里某种地形的比例（每 3 格采样）。</summary>
        private static (float center, float edge) StyleFraction(WorldGenContext ctx, PlannedTerritory t, byte code)
        {
            int cn = 0, ch = 0, en = 0, eh = 0;
            for (int dy = -t.Radius; dy <= t.Radius; dy += 3)
            {
                for (int dx = -t.Radius; dx <= t.Radius; dx += 3)
                {
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    int x = t.CenterX + dx;
                    int y = t.CenterY + dy;
                    if (TerritoryPlanner.NearAnyAnchor(ctx.Plan, x, y, 0))
                    {
                        continue; // 平整范围里不算风格
                    }
                    if (d <= t.Radius * 0.3)
                    {
                        ctx.Source.Sample(x, y, out byte tt, out _);
                        cn++;
                        ch += tt == code ? 1 : 0;
                    }
                    else if (d >= t.Radius * 0.85 && d <= t.Radius)
                    {
                        ctx.Source.Sample(x, y, out byte tt, out _);
                        en++;
                        eh += tt == code ? 1 : 0;
                    }
                }
            }
            return (cn > 0 ? (float)ch / cn : 0f, en > 0 ? (float)eh / en : 0f);
        }

        /// <summary>领地里不在任何危害区、不在锚点上的一个格（找不到返回中心）。</summary>
        private static (int, int) FreeTerritoryPoint(WorldPlan plan, PlannedTerritory t)
        {
            for (int k = 0; k < 360; k += 7)
            {
                int x = t.CenterX + (int)(Math.Cos(k * Math.PI / 180) * t.Radius * 0.6);
                int y = t.CenterY + (int)(Math.Sin(k * Math.PI / 180) * t.Radius * 0.6);
                if (plan.HazardZoneAt(x, y) == null && !TerritoryPlanner.NearAnyAnchor(plan, x, y, 0))
                {
                    return (x, y);
                }
            }
            return (t.CenterX, t.CenterY);
        }

        /// <summary>模板表：真实表全部行，<paramref name="mutate"/> 可以就地改某一行的边缘据点概率（模拟“不升版本直接改已发布集合”）。</summary>
        private static ByteBuf TemplateBuf(Func<TemplateRowView, TemplateRowView> mutate)
        {
            var buf = new ByteBuf();
            IReadOnlyList<TerritoryTemplate> rows = WorldGenContent.TemplateRows;
            buf.WriteSize(rows.Count);
            foreach (TerritoryTemplate r in rows)
            {
                TemplateRowView v = mutate(new TemplateRowView(r, r.OutpostEdgeChance));
                buf.WriteString(r.Key);
                buf.WriteString(r.Set);
                buf.WriteString(r.TerritoryId);
                buf.WriteString(r.StyleTerrain1);
                buf.WriteFloat(r.StyleBonus1);
                buf.WriteString(r.StyleTerrain2);
                buf.WriteFloat(r.StyleBonus2);
                buf.WriteInt(r.HazardZones);
                buf.WriteInt(r.HazardRadiusMin);
                buf.WriteInt(r.HazardRadiusMax);
                buf.WriteFloat(r.HazardRadialMax);
                buf.WriteFloat(v.OutpostEdgeChance);
                buf.WriteFloat(r.OutpostCenterChance);
                buf.WriteInt(r.OutpostTierEdge);
                buf.WriteInt(r.OutpostTierCenter);
                buf.WriteInt(r.GarrisonPerTier);
                buf.WriteInt(r.PatrolEvery);
                buf.WriteInt(r.PatrolRadius);
                buf.WriteFloat(r.CacheChance);
                buf.WriteInt(r.CacheScrap);
                buf.WriteFloat(r.CacheSeconds);
                buf.WriteFloat(r.TerminalChance);
                buf.WriteInt(r.TerminalTech);
                buf.WriteFloat(r.TerminalSeconds);
                buf.WriteString(r.TerminalLoreKeys);
                buf.WriteFloat(r.RelicFactor);
                buf.WriteFloat(r.SideChance);
                buf.WriteInt(r.AnchorClearance);
                buf.WriteString(r.IntroKey);
            }
            return buf;
        }

        private readonly struct TemplateRowView
        {
            public readonly TerritoryTemplate Row;
            public readonly float OutpostEdgeChance;
            public string TerritoryId => Row.TerritoryId;

            public TemplateRowView(TerritoryTemplate row, float edge)
            {
                Row = row;
                OutpostEdgeChance = edge;
            }

            public TemplateRowView With(float outpostEdge) => new TemplateRowView(Row, outpostEdge);
        }

        /// <summary>手工锚点表：只放给定的几个测试锚点（seeded 方向、可建平整范围、无模板）。</summary>
        private static ByteBuf AnchorBuf(params (string id, string territory, float rmin, float rmax, int footprint, int gap)[] rows)
        {
            var buf = new ByteBuf();
            buf.WriteSize(rows.Length);
            foreach (var r in rows)
            {
                buf.WriteString("as3." + r.id);
                buf.WriteString("as3");
                buf.WriteString(r.id);
                buf.WriteString(r.territory);
                buf.WriteString("boss_site");
                buf.WriteString("none");
                buf.WriteFloat(r.rmin);
                buf.WriteFloat(r.rmax);
                buf.WriteString("seeded");
                buf.WriteInt(0);
                buf.WriteInt(r.footprint);
                buf.WriteInt(6);
                buf.WriteString("none");
                buf.WriteInt(0);
                buf.WriteInt(0);
                buf.WriteInt(r.gap);
                buf.WriteInt(1);
                buf.WriteInt(1);
                buf.WriteString("world.anchor.baptism_platform.name");
                buf.WriteString("world.anchor.baptism_platform.desc");
            }
            return buf;
        }

        private static void ExpectPerf(bool functionalOk, string message, params PerfGate.Metric[] perf) =>
            PerfGate.Expect(functionalOk, message, perf, Expect, Line);

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgTerritorySelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
            }
        }

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

        private static void Line(string text)
        {
            _report?.AppendLine(text);
        }
    }
}
