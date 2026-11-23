using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG12-EVT-02 震动值与掘进蠕虫的自动验收（FG10 FGR-EVT-010～014；FGT-EVT-002 / 003；承接 DEBT-FG4ECO02-04、DEBT-FG12EVT01-02 的蠕虫部分、DEBT-FG5RND01-01 的减震基座）。
    /// 全部起真实家园（星球战斗内核 + 生产 + 事件导演 + 真实 worm 处理器），走 <c>WorldSimulation.StepMany / Frame</c>。
    /// D 数据；A1～A5 FGT-EVT-002 累积 / 衰减 / 等级 / 减震基座 / 按地点 / 爆炸 / 旧档迁移；B1～B4 阈值（50 预兆与首次钩子、80 警告与解除、到点请求、100 立即请求）；
    /// C1～C7 FGT-EVT-003（破土预警 30 秒裂纹、破土与原因、地上可打 / 地下打不到、被牵引变慢、分节击毁掉落、建筑受创、击杀给三选一、超时潜回地下）；
    /// N1～N3 负向（静默夜期间到 100 推迟、事件频率关闭不来、同一时刻只请求一处）；T1 暂停与 0.5x～3x；T2 观察一致；S1 存读档；U1 统计面板与战略地图；P1 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgWormSelfCheck）。
    /// </summary>
    public static class FgWormSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();
        private static readonly List<string> Hooks = new List<string>();

        [MenuItem("QA/自检/FG 掘进蠕虫")]
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
            Line("\n[掘进蠕虫] 震动值按地点累积 / 衰减 / 减震基座 / 爆炸、三个阈值、蠕虫破土 / 分节 / 地上地下 / 掉落 / 建筑受创 / 击杀 / 超时、负向、倍速 / 观察 / 存读档、面板与地图（FG12-EVT-02）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgworm-selfcheck-" + Guid.NewGuid().ToString("N"));
            Action<string> onHook = id => Hooks.Add(id);
            GuidanceHooks.Touched += onHook;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                ResearchCatalog.Reload();
                GameClock.ReloadTuning();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "震动值与蠕虫处理器在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），链推进在 Main/Sim（Editor 下 Burst 未必开，真机 IL2CPP + Burst）；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckAccumulation);
                Step(CheckSitesAndBlasts);
                Step(CheckMigration);
                Step(CheckThresholdsAndWorm);
                Step(CheckTimeout);
                Step(CheckHotspotDrillsOff);
                Step(CheckSilentNight);
                Step(CheckFrequencyOff);
                Step(CheckOneAtATime);
                Step(CheckMap);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSaveLoad);
                Step(CheckSaveLoadMidSurfacing);
                Step(CheckPerformance);
                Step(ProbeHotfixCost);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"掘进蠕虫自检抛异常：{e}");
            }
            finally
            {
                GuidanceHooks.Touched -= onHook;
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                EventDirectorService.ResetForTests();
                VibrationService.ResetSessionState();
                WormEventService.ResetSessionState();
                GameClock.ResetSession();
                StrategyClock.Reset();
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ProductionService.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                if (hadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, savedPrefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
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
            Line($"  · [掘进蠕虫] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────────

        private static long Sec(double s) => GameClock.TicksFor(s);

        private static CampaignState World(int seed, bool observe = true)
        {
            EventDirectorService.ResetForTests();
            VibrationService.ResetSessionState();
            WormEventService.ResetSessionState();
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, 300);
            EventDirectorService.Install();
            EventDirectorService.Register(new WormEventService.Handler());
            WorldSimulation.StepMany(2);
            return s;
        }

        private static CombatSite Site => WorldSimulation.Home?.Combat;
        private static VibrationState V(CampaignState s) => VibrationService.StateOf(s);
        private static VibrationSiteRecord HomeSite(CampaignState s) => VibrationService.Site(V(s), VibrationService.HomeSite, true);
        private static void StepSec(double s) => WorldSimulation.StepMany((int)Math.Max(1, Sec(s)));

        private static bool StepUntil(Func<bool> done, double maxSeconds)
        {
            long end = GameClock.Ticks + Sec(maxSeconds);
            while (GameClock.Ticks < end)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(Math.Max(1, GameClock.StepHz / 10));
            }
            return done();
        }

        private static List<string> EventTexts() =>
            NotificationCenter.History.Where(e => e.Type?.Id == "world_event").SelectMany(e => e.Members).Select(m => m.Detail ?? string.Empty).ToList();

        private static EventDirectorInstanceRecord WormInstance(CampaignState s) => EventDirectorService.Active(s).FirstOrDefault(r => r.EventId == WormEventService.EventId);

        private static WormRecord OnlyWorm(CampaignState s) => WormEventService.Worms(s).FirstOrDefault();

        private static List<int> Segs(WormRecord w)
        {
            var l = new List<int>();
            Site?.CopyWormSegments(w?.Chain ?? 0, l);
            return l;
        }

        /// <summary>核心附近 n 座在稀土矿上工作的提取钻（有电）。</summary>
        private static List<BuildingRecord> Drills(CampaignState s, int n, string key)
        {
            var list = new List<BuildingRecord>();
            GridCell? o = FgProductionSelfCheck.FindArea(s, n * 3 + 1, 3);
            if (o == null)
            {
                return list;
            }
            FgProductionSelfCheck.PowerUp(s);
            for (int i = 0; i < n; i++)
            {
                GridCell c = FgProductionSelfCheck.At(o.Value, i * 3, 1);
                FgProductionSelfCheck.SetFootprint(s, "extraction_drill", c, "ore_rare");
                list.Add(FgProductionSelfCheck.Built(s, "extraction_drill", key + i, c));
            }
            return list;
        }

        // ── D 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            EventDirectorDef def = EventDirectorService.Def(WormEventService.EventId);
            bool row = def != null && def.Trigger == EventDirectorService.TriggerCondition && Math.Abs(def.WarningSeconds - 30.0) < 1e-6
                       && Math.Abs(def.DurationSeconds * 2 - GameClock.DaySeconds) < 1e-6 && def.Reward == EventDirectorService.RewardPacket && def.Major;
            bool building = GridContent.TryGetBuilding(VibrationService.DamperTypeId, out var g) && g.FootprintW == 2 && g.FootprintH == 2 && g.Category == "environment"
                            && g.UnlockRule.Contains("research:environment.damper");
            ResearchNodeDef node = ResearchCatalog.Find("environment.damper");
            bool research = node != null && string.IsNullOrEmpty(node.OpensIn) && node.Unlocks.Contains("build:" + VibrationService.DamperTypeId);
            var rows = new List<LootDropDef>();
            LootCatalog.RowsFor(LootCatalog.SourceWorm, string.Empty, rows);
            bool loot = rows.Any(r => r.Item.Id == "rare_earth_ore") && rows.Any(r => r.Item.Id == "combat_component");
            bool tuning = VibrationService.Omen == 50 && VibrationService.Warn == 80 && VibrationService.Burst == 100 && VibrationService.DamperMult == 0.5f
                          && VibrationService.DecayPerMinute == 2.0 && GridContent.Tuning("worm.segments_min") == 8f && GridContent.Tuning("worm.segments_max") == 12f;
            bool hook = GuidanceHooks.Known.Contains(GuidanceHooks.VibrationFirstOmen);
            bool texts = new[] { "vibration.notify.omen", "vibration.notify.warn", "vibration.notify.cause", "vibration.notify.cracks", "building.damper_base.name" }
                .All(k => GameText.Get(k) != k);
            Expect(row && building && research && loot && tuning && hook && texts,
                $"D 数据：worm 行 condition、破土前 30 秒预警、时限 = 半个游戏日、击杀三选一（{row}）；减震基座 2×2 环境页、研究门控（{building}）；研发节点开放并解锁（{research}）；" +
                $"掉落来源 worm 有稀有矿与作战组件（{loot}）；FG10 第 15 节初值（{tuning}）；首次 50 钩子登记（{hook}）；文本（{texts}）");
        }

        // ── A FGT-EVT-002 累积 / 衰减 / 等级 / 减震基座 ───────────────────────────────

        private static void CheckAccumulation()
        {
            CampaignState s = World(9201);
            List<BuildingRecord> d = Drills(s, 4, "acc");
            if (d.Count < 4)
            {
                Fail("A1 找不到放 4 座提取钻的空地");
                return;
            }
            // 减震基座只盖住第 1 座（离它 8 格；其余 11 / 14 / 17 格在半径 10 外）。
            GridCell? dc = FgProductionSelfCheck.FindFree(s, VibrationService.DamperTypeId, 3f, 30f);
            BuildingRecord damper = dc.HasValue ? FgProductionSelfCheck.Built(s, VibrationService.DamperTypeId, "damper", dc.Value) : null;
            if (damper == null)
            {
                Fail("A3 找不到放减震基座的空地");
                return;
            }
            damper.Position = d[0].Position - new Vector2(8f, 0f);
            int covered = d.Count(b => VibrationService.IsDamped(s, b));
            double v0 = ProductionService.Vibration(s);
            FgProductionSelfCheck.Seconds(12f);
            double v12 = ProductionService.Vibration(s);
            double expect = (3 * 1.0 + 0.5 - 2.0) * 12.0 / 60.0;
            Expect(covered == 1 && Math.Abs(v12 - v0 - expect) < 0.03,
                $"A1 累积与衰减（生产步）：4 座 T1 提取钻工作 12 游戏秒，其中 {covered} 座在减震基座里，家园震动 +{v12 - v0:0.###}（期望 (3 × 1 + 0.5 − 2) × 0.2 = {expect:0.###}）");
            string panel = VibrationService.SourcesText(s, VibrationService.HomeSite, 8);
            var lines = new List<(string Text, string Cls)>();
            PurificationService.BuildStatsLines(s, lines);
            bool stats = lines.Any(l => l.Text == GameText.Get("vibration.stats.title")) && lines.Any(l => l.Text.Contains(GameText.Get("vibration.site.home")) && l.Text.Contains("/ 100"))
                         && lines.Any(l => l.Text.StartsWith(GameText.Format("vibration.stats.sources", string.Empty), StringComparison.Ordinal) && l.Text.Contains("+1"));
            // 统计面板只列增量最大的 eco.prod.sources_shown 座：减震后 +0.5 的那座排在 3 座 +1 之后，不要求出现在统计面板（提取钻悬停 / 生产面板列全）。
            // 等级：T2 +2、T3 +3（FGR-EVT-010）。
            d[1].Tier = 2;
            d[2].Tier = 3;
            double r2 = VibrationService.DrillRate(s, d[1], 1f), r3 = VibrationService.DrillRate(s, d[2], 1f), r1 = VibrationService.DrillRate(s, d[3], 1f);
            d[1].Tier = 0;
            d[2].Tier = 0;
            Expect(r1 == 1 && r2 == 2 && r3 == 3, $"A2 提取钻 T1 / T2 / T3 每分钟 +{r1} / +{r2} / +{r3}（FGR-EVT-010 初值 1 / 2 / 3）");
            bool half = VibrationService.DrillRate(s, d[0], 1f) == 0.5;
            damper.ConstructionState = BuildingConstructionState.Disabled;
            bool disabledOff = !VibrationService.IsDamped(s, d[0]);
            damper.ConstructionState = BuildingConstructionState.Operational;
            Expect(half && disabledOff && panel.Contains("+0.5") && stats,
                $"A3 减震基座：覆盖内的提取钻每分钟 +0.5（×0.5，FG10 第 15 节，{half}）；禁用后失效（{disabledOff}）；主要来源列出减震后的增量（{panel}）；统计面板环境段列出家园震动值与来源（{stats}）");
        }

        private static void CheckSitesAndBlasts()
        {
            CampaignState s = World(9202);
            Vector2 core = GameLogic.Campaign.Signal.SignalUplinkService.CorePosition(s);
            // 前哨站：离核心 300 格以外的建筑群（FG8-OUT-01）里的提取钻记到那个前哨站。
            GridCell coreCell = HomeGridService.CorePivot(s);
            GridCell? far = new GridCell(coreCell.X + 340, coreCell.Y); // 测试捷径：直接登记（不走放置校验），只验地点归属与按地点积分
            bool outpostOk = false;
            string opId = null;
            if (far.HasValue)
            {
                FgProductionSelfCheck.PowerUp(s);
                var ds = new List<BuildingRecord>();
                for (int i = 0; i < 4; i++)
                {
                    var c = new GridCell(far.Value.X + i * 2, far.Value.Y);
                    BuildingRecord b = FgProductionSelfCheck.Built(s, "extraction_drill", "op" + i, c);
                    ds.Add(b);
                }
                FgProductionSelfCheck.Seconds(3f);
                opId = VibrationService.SiteOfDrill(s, ds[0]);
                double home0 = VibrationService.Value(s, VibrationService.HomeSite);
                double op0 = VibrationService.Value(s, opId);
                VibrationService.BeginProductionStep();
                foreach (BuildingRecord b in ds)
                {
                    VibrationService.AddDrill(s, b, 1f);
                }
                VibrationService.Integrate(s, 1.0);
                outpostOk = opId != VibrationService.HomeSite && OwnOutpostService.Find(s, opId) != null && Math.Abs(VibrationService.Value(s, opId) - (op0 + 2.0)) < 1e-6
                            && Math.Abs(VibrationService.Value(s, VibrationService.HomeSite) - Math.Max(0, home0 - 2.0)) < 1e-6;
            }
            Expect(outpostOk, $"A4 按地点：前哨站（{opId ?? "没找到地方"}）里的 4 座钻 1 分钟 +4 − 2 = 2 只记在前哨站，家园只衰减（FGR-EVT-011 末条）");
            // 大规模爆炸：内核里半径 ≥ 4 的爆炸记一处，世界步取走加到所在地点；小爆炸 / 野外不算。
            CombatSite site = Site;
            double h0 = VibrationService.Value(s, VibrationService.HomeSite);
            site.NoteBlastForTests(core + new Vector2(6f, 0f), 5f);
            site.NoteBlastForTests(core + new Vector2(7f, 1f), 6f); // 同一步、相距 12 格内：合并计 2 次
            site.NoteBlastForTests(core + new Vector2(-6f, 0f), 2f); // 太小
            site.NoteBlastForTests(core + new Vector2(5000f, 0f), 9f); // 野外：没有己方存在
            WorldSimulation.StepMany(1);
            double h1 = VibrationService.Value(s, VibrationService.HomeSite);
            VibrationSiteRecord hr = HomeSite(s);
            bool blast = Math.Abs(h1 - h0 - 2 * VibrationService.BlastAdd) < 0.05 && hr.BlastCount == 2 && V(s).Blasts == 2;
            // 远征队所在的地方：队伍中心附近的爆炸记到那支队伍。
            MachineRecord m = s.MachineRecords.FirstOrDefault(x => x != null && x.IsAlive && x.RegionId == HomeValleyLayout.RegionId);
            bool team = false;
            string teamSite = null;
            if (m != null && site.TryGetMachinePosition(m.LogicId, out Vector2 mp))
            {
                s.Grid.ExpeditionTeam ??= new ExpeditionTeamState();
                ExpeditionMarchState saved = s.Grid.ExpeditionTeam.March;
                s.Grid.ExpeditionTeam.March = new ExpeditionMarchState { Phase = ExpeditionMarchService.PhaseOutbound, Members = new[] { m.LogicId }, TeamNumber = 1 };
                if (site.TryGetMachineUnit(m.LogicId, out int unit))
                {
                    site.Kernel.SetPosition(unit, new Unity.Mathematics.double2(core.x + 900f, core.y));
                }
                WorldSimulation.StepMany(1);
                teamSite = VibrationService.SiteOfPoint(s, core + new Vector2(905f, 3f));
                team = teamSite == VibrationService.TeamPrefix + "1" && VibrationService.SiteOfPoint(s, core + new Vector2(2000f, 0f)) == null;
                s.Grid.ExpeditionTeam.March = saved;
            }
            Expect(blast && team, $"A5 大规模爆炸：家园两处合并计 2 次、+{h1 - h0:0.#}（每次 +{VibrationService.BlastAdd}），半径 2 的不算、野外的不算（{blast}）；远征队中心附近的爆炸记到 {teamSite}（{team}）");
        }

        private static void CheckMigration()
        {
            CampaignState s = CampaignState.CreateNew("worm-legacy", "Standard", 9203);
            s.Vibration = null;
            s.Economy.Vibration = 42.5;
            VibrationState v = VibrationService.StateOf(s);
            Expect(v.Migrated && Math.Abs(VibrationService.Value(s, VibrationService.HomeSite) - 42.5) < 1e-9 && s.Economy.Vibration == 0.0,
                "A6 旧档：FG4-ECO-02 的家园震动值迁进家园那一行（之后旧字段不再写）");
        }

        // ── B 阈值 + C 蠕虫 ───────────────────────────────────────────────────────

        private static void CheckThresholdsAndWorm()
        {
            CampaignState s = World(9301);
            List<BuildingRecord> drills = Drills(s, 3, "thr");
            FgProductionSelfCheck.Seconds(2f);
            VibrationSiteRecord home = HomeSite(s);
            Hooks.Clear();
            home.Value = 55;
            StepSec(1.2);
            bool omen = home.Level == VibrationService.LevelOmen && Hooks.Contains(GuidanceHooks.VibrationFirstOmen) && EventTexts().Any(t => t.Contains("地面在轻微震颤"));
            Expect(omen, $"B1 50 预兆：地面震颤提示（带地点、提示音），第一次到达发引导钩子（等级 {home.Level}）");
            home.Value = 85;
            StepSec(1.2);
            bool warn = home.Level == VibrationService.LevelWarn && EventTexts().Any(t => t.Contains("掘进蠕虫会来") && t.Contains("采矿区"))
                        && home.DueTick - home.WarnTick == Math.Max(0, Sec(GameClock.DaySeconds / 24.0) - Sec(30) - EventDirectorService.CheckTicks);
            var model = new WorldMapModel();
            GridCell core = HomeGridService.CorePivot(s);
            model.Collect(s, new WorldMapView { CenterX = core.X, CenterY = core.Y, HalfWidth = 200, CanvasWidth = 1000, CanvasHeight = 800 }, 256);
            bool marked = model.Items.Any(i => i.Id == "vibration:home") && Math.Abs(home.HotX - drills.Average(b => b.Position.x)) < 3.5f;
            home.Value = 70;
            StepSec(1.2);
            bool lifted = home.Level == VibrationService.LevelOmen && EventTexts().Any(t => t.Contains("警告解除"));
            Expect(warn && marked && lifted, $"B2 80 警告：通知写“1 个游戏小时内会来”与哪片采矿区，计时到点 = 1 小时 − 30 秒破土预警 − 评估间隔（{warn}）；战略地图标出最集中处（在 3 座钻中间，{marked}）；" +
                                             $"回落到 80 以下警告解除（{lifted}）");
            home.Value = 85;
            StepSec(1.2);
            long warnTick = home.WarnTick;
            bool requested = StepUntil(() => WormInstance(s) != null, 25);
            EventDirectorInstanceRecord r = WormInstance(s);
            WormRecord w = OnlyWorm(s);
            bool cracks = r != null && r.State == EventDirectorService.StateWarned && r.StartTick - r.WarnTick == Sec(30) && w != null && w.State == WormEventService.StateWarned
                          && w.RouteX.Length >= 3 && Site.WormCount == 0 && EventTexts().Any(t => t.Contains("地面开裂") && t.Contains("采矿区"));
            Expect(requested && home.Level == VibrationService.LevelRequested && cracks,
                $"B3 / C1 警告到点仍在 80 以上 → 向事件导演请求；导演预警 = 破土前 30 秒，裂纹沿路线（{w?.RouteX.Length ?? 0} 个顶点，地图画出）蔓延，此时还没有蠕虫（{cracks}）");
            if (r == null || w == null)
            {
                return;
            }
            // 路线上放一座屏障：破土那一刻整条身子压到的建筑受重创。
            Vector2 p0 = new Vector2(w.RouteX[0], w.RouteY[0]);
            Vector2 p1 = new Vector2(w.RouteX[1], w.RouteY[1]);
            Vector2 hitAt = p0 + (p1 - p0).normalized * 4f;
            BuildingRecord barrier = FgProductionSelfCheck.Built(s, "power_pole", "wormhit", new GridCell(Mathf.FloorToInt(hitAt.x), Mathf.FloorToInt(hitAt.y)));
            barrier.Health = BuildingOps.MaxDurability("power_pole");
            float bh0 = barrier.Health;
            StepUntil(() => w.State == WormEventService.StateActive, 35);
            long breakTick = GameClock.Ticks;
            CombatSite site = Site;
            bool spawned = site.TryGetWorm(w.Chain, out CombatWorm cw) && cw.SegCount >= 8 && cw.SegCount <= 12 && cw.Phase == CombatWormConst.PhaseSurface;
            bool reset = home.Value < 0.1 && home.Level == VibrationService.LevelCalm;
            bool cause = EventTexts().Any(t => t.Contains("这次是因为") && t.Contains("采矿区") && t.Contains("3 座提取钻"));
            Expect(spawned && reset && cause && breakTick - warnTick <= Sec(GameClock.DaySeconds / 24.0) + 2,
                $"C2 破土：{cw.SegCount} 节（8～12）从最集中处钻出，80 警告后 {(breakTick - warnTick) / (double)GameClock.StepHz:0.#} 秒（≤ 1 游戏小时）；该地点震动值归零（{reset}）；" +
                $"通知说明原因“这次是因为 …采矿区震动过高（3 座提取钻）”（{cause}）");
            float max = BuildingOps.MaxDurability("power_pole");
            bool hit = Math.Abs(barrier.Health - (bh0 - 0.6f * max)) < 0.01f && w.BuildingsHit >= 1;
            float bhAfter = barrier.Health;
            StepSec(1);
            bool once = Math.Abs(barrier.Health - bhAfter) < 0.01f;
            Expect(hit && once, $"C3 经过的地表建筑受重创：路线上的电线杆耐久 {bh0} → {bhAfter}（最大耐久的 60%），同一次钻出不再算（{once}）");
            // 地面上：可以被锁定、受伤；地下：打不到。
            List<int> segs = Segs(w);
            float hpA = site.Kernel.TryGetUnit(segs[1], out CombatUnitView va) ? va.Health : -1f;
            site.Kernel.Damage(segs[1], 30f, 0);
            float hpB = site.Kernel.TryGetUnit(segs[1], out CombatUnitView vb) ? vb.Health : -1f;
            bool upHit = (va.Flags & CombatUnitFlags.Targetable) != 0 && hpB < hpA - 29f;
            // 被牵引：有一节被外力推开的那一步，整条速度 × worm.drag_mult。
            site.TryGetWorm(w.Chain, out cw);
            site.Kernel.TryGetUnit(segs[2], out CombatUnitView v2);
            site.Kernel.SetPosition(segs[2], v2.Position + new Unity.Mathematics.double2(1.5, 0));
            WorldSimulation.StepMany(1);
            site.TryGetWorm(w.Chain, out CombatWorm cwDrag);
            bool drag = Math.Abs(cwDrag.LastSpeed - cw.Speed * GridContent.Tuning("worm.drag_mult")) < 1e-3f;
            WorldSimulation.StepMany(1);
            site.TryGetWorm(w.Chain, out CombatWorm cwFree);
            bool free = Math.Abs(cwFree.LastSpeed - cw.Speed) < 1e-3f;
            StepUntil(() => site.TryGetWorm(w.Chain, out CombatWorm c2) && c2.Phase == CombatWormConst.PhaseUnder, 10);
            site.TryGetWorm(w.Chain, out CombatWorm under);
            site.Kernel.TryGetUnit(segs[1], out CombatUnitView vu);
            float hpU0 = vu.Health;
            site.Kernel.Damage(segs[1], 50f, 0);
            site.Kernel.TryGetUnit(segs[1], out CombatUnitView vu2);
            bool downSafe = under.Phase == CombatWormConst.PhaseUnder && (vu.Flags & CombatUnitFlags.Targetable) == 0 && (vu.Flags & CombatUnitFlags.Invulnerable) != 0
                            && Math.Abs(vu2.Health - hpU0) < 1e-3f;
            StepUntil(() => site.TryGetWorm(w.Chain, out CombatWorm c3) && c3.Phase == CombatWormConst.PhaseSurface, 15);
            site.TryGetWorm(w.Chain, out CombatWorm again);
            Expect(upHit && drag && free && downSafe && again.Surfacings >= 2,
                $"C4 地上 / 地下：钻出时可锁定、受伤（{upHit}）；地下潜行时不能锁定、伤害无效（{downSafe}）；沿路线反复钻出（第 {again.Surfacings} 次）；" +
                $"有一节被牵引那一步整条速度 × {GridContent.Tuning("worm.drag_mult")}（{drag}），之后恢复（{free}）");
            // 分节击毁：单独击毁一节 → 那一节掉落稀有矿 / 组件，其余照常。
            int ground0 = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Count(g => g?.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith("fieldloot:worm:", StringComparison.Ordinal));
            site.Kernel.Kill(segs[0], 0);
            WorldSimulation.StepMany(2);
            int ground1 = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Count(g => g?.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith("fieldloot:worm:", StringComparison.Ordinal));
            bool rare = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Any(g => g?.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith("fieldloot:worm:", StringComparison.Ordinal)
                                                                                     && g.ResourceType == ItemCatalog.ResourceTypeOf("rare_earth_ore"));
            bool comp = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Any(g => g?.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith("fieldloot:worm:", StringComparison.Ordinal)
                                                                                     && g.ResourceType == ItemCatalog.ResourceTypeOf("combat_component"));
            site.TryGetWorm(w.Chain, out CombatWorm afterKill);
            Expect((w.DroppedMask & 1u) != 0 && ground1 > ground0 && rare && comp && afterKill.SegCount - WormEventService.Popcount(afterKill.DeadMask) == afterKill.SegCount - 1 && V(s).SegmentsKilled == 1,
                $"C5 分节击毁：第 1 节被击毁 → 地上多了 {ground1 - ground0} 堆蠕虫掉落（含稀有矿 {rare}、作战组件 {comp}：FGR-EVT-013“每一节掉落稀有矿和组件”），其余 {afterKill.SegCount - 1} 节照常");
            // 击杀：累计伤害达到总耐久的 70% → 其余节一起倒下、每节都掉落，导演按 success 给三选一。
            int packets0 = LootPickService.StateOf(s).Packets.Length;
            StepUntil(() => site.TryGetWorm(w.Chain, out CombatWorm c4) && c4.Phase == CombatWormConst.PhaseSurface, 25);
            foreach (int u in Segs(w))
            {
                site.Kernel.Damage(u, 600f * 0.75f * DifficultyService.EnemyHealthMul(s), 0);
            }
            WorldSimulation.StepMany(2);
            EventDirectorHistoryRecord h = EventDirectorService.History(s).LastOrDefault(x => x.EventId == WormEventService.EventId);
            bool killed = h != null && h.Outcome == EventDirectorService.OutcomeSuccess && !string.IsNullOrEmpty(h.PacketId) && LootPickService.StateOf(s).Packets.Length > packets0
                          && OnlyWorm(s) == null && site.WormCount == 0 && V(s).Kills == 1 && V(s).SegmentsKilled == cw.SegCount && EventTexts().Any(t => t.Contains("被击杀"));
            Expect(killed, $"C6 击杀：累计伤害够了其余节一起倒下（共掉落 {V(s).SegmentsKilled} / {cw.SegCount} 节），事件历史 success、给三选一数据包（{h?.PacketId}），链与记录移除");
            // FG15-BAL-01（DEBT-FG12EVT02-01，FGR-EVT-013“只能从蠕虫获得的格斗类钻头组件”）：蠕虫的节段掉未解析模块，身份固定是掘进钻头；别的来源的模块池里没有它。
            var drillTags = (LootPickService.StateOf(s).GroundTags ?? Array.Empty<LootGroundTagRecord>())
                .Where(t => t != null && t.SalvageId != null && t.SalvageId.StartsWith("fieldloot:worm:", StringComparison.Ordinal) && t.TargetId == "comp_worm_drill").ToList();
            var moduleRows = LootCatalog.Rows.Where(r => r.Identity == LootCatalog.IdentityModule).ToList();
            bool onlyWorm = moduleRows.Where(r => r.Pool.Contains("comp_worm_drill")).All(r => r.Source == "worm") && moduleRows.Any(r => r.Source == "worm" && r.Pool.Contains("comp_worm_drill"));
            bool drillKnown = ComponentCatalog.TryGet("comp_worm_drill", out MechanicalContentDef drillDef);
            bool lockedUntilDecoded = drillKnown && !MechanicalContentUnlock.IsUnlocked(s, "comp_worm_drill")
                                      && drillDef.Category == MechanicalContentCategory.MainComponent && !string.IsNullOrEmpty(drillDef.LockedHintText) && drillDef.LockedHintText != GameText.Get("component.source.enemy");
            Expect(drillTags.Count > 0 && onlyWorm && lockedUntilDecoded,
                $"C6b 掘进钻头：这条蠕虫 {cw.SegCount} 节共掉 {drillTags.Count} 件身份为掘进钻头的未解析模块（地上，搬回解析台破解后写进蓝图）；模块池里只有蠕虫来源有它（{onlyWorm}）；破解前锁定，锁定提示写获取途径（“{(drillKnown ? drillDef.LockedHintText : string.Empty)}”）");
        }

        private static void CheckTimeout()
        {
            CampaignState s = World(9302);
            HomeSite(s).Value = 100;
            StepUntil(() => OnlyWorm(s)?.State == WormEventService.StateActive, 50);
            EventDirectorInstanceRecord r = WormInstance(s);
            WormRecord w = OnlyWorm(s);
            if (r == null || w == null)
            {
                Fail("C7 蠕虫没有破土");
                return;
            }
            bool halfDay = r.EndTick - r.StartTick == Sec(GameClock.DaySeconds / 2.0);
            r.EndTick = GameClock.Ticks + Sec(3); // 测试捷径：时限 = 半日由上一行与 D 段断言，这里把剩余时间缩到 3 秒
            StepUntil(() => w.State == WormEventService.StateLeaving, 6);
            List<int> segs = Segs(w);
            bool sunk = Site.Kernel.TryGetUnit(segs[0], out CombatUnitView v) && (v.Flags & CombatUnitFlags.Targetable) == 0;
            StepSec(GridContent.Tuning("worm.dive_seconds") + 1);
            EventDirectorHistoryRecord h = EventDirectorService.History(s).LastOrDefault(x => x.EventId == WormEventService.EventId);
            Expect(halfDay && sunk && Site.WormCount == 0 && OnlyWorm(s) == null && h?.Outcome == EventDirectorService.OutcomeExpired && string.IsNullOrEmpty(h.PacketId)
                   && V(s).Escapes == 1 && EventTexts().Any(t => t.Contains("潜回地下")),
                $"C7 超时离开：时限半个游戏日（{halfDay}），到点潜回地下（打不到，{sunk}）、潜入时长后离开，历史 expired、不给三选一");
        }

        private static void CheckHotspotDrillsOff()
        {
            CampaignState s = World(9303);
            List<BuildingRecord> drills = Drills(s, 3, "off");
            FgProductionSelfCheck.Seconds(2f);
            VibrationSiteRecord home = HomeSite(s);
            home.Value = 85;
            StepSec(1.2);
            bool warned = home.Level == VibrationService.LevelWarn && home.HotDrills == 3;
            // 玩家照 80 警告的建议把提取钻全部关停：衰减每分钟 2，到点时仍 ≥ 80，照常请求。
            foreach (BuildingRecord d in drills)
            {
                HomeValleyPowerGrid.TryToggleShutdown(s, d.BuildingId);
            }
            Func<string, bool> causeText = t => t.Contains("这次是因为") && t.Contains("采矿区") && t.Contains("3 座提取钻");
            int causes0 = EventTexts().Count(causeText);
            bool came = StepUntil(() => OnlyWorm(s)?.State == WormEventService.StateActive, 90);
            WormRecord w = OnlyWorm(s);
            float ax = drills.Average(b => b.Position.x), ay = drills.Average(b => b.Position.y);
            bool stopped = drills.All(d => FgProductionSelfCheck.P(s, d)?.State == ProdState.Disabled);
            bool atDrills = w != null && w.CauseDrills == 3 && Math.Abs(w.HotX - ax) < 3.5f && Math.Abs(w.HotY - ay) < 3.5f;
            bool cause = EventTexts().Count(causeText) > causes0;
            Expect(warned && came && stopped && atDrills && cause,
                $"C8 负向“80 警告后把提取钻全部关停”：到点照常请求、破土（{came}，钻已关停 {stopped}）；破土仍在那片采矿区（{w?.HotX:0.#}, {w?.HotY:0.#}；钻中心 {ax:0.#}, {ay:0.#}），" +
                $"原因仍写“3 座提取钻”（{cause}），不会改成核心或“大规模爆炸”（FGR-EVT-011 / 卡片“说明原因”）");
        }

        // ── N 负向 ───────────────────────────────────────────────────────────────

        private static void CheckSilentNight()
        {
            CampaignState s = World(9401);
            SilentNightState sn = SilentNightService.StateOf(s);
            long now = GameClock.Ticks;
            sn.WarnTick = now;
            sn.StartTick = now + Sec(5);
            sn.EndTick = now + Sec(60);
            sn.Warned = true;
            sn.NextDay = Math.Max(1, sn.NextDay);
            HomeSite(s).Value = 100;
            StepSec(20);
            bool held = WormInstance(s) == null && HomeSite(s).Level == VibrationService.LevelRequested
                        && EventDirectorService.Schedule(s, WormEventService.EventId)?.BlockedKey == EventDirectorService.BlockSilentNight;
            // 不承诺“1 小时内”：80 警告通知写明会晚、为什么；统计面板状态写“推迟中：与静默夜重叠……”。
            string heldState = VibrationService.StateText(s, HomeSite(s), GameClock.Ticks);
            bool told = heldState.Contains("推迟中") && heldState.Contains("静默夜") && EventTexts().Any(t => t.Contains("掘进蠕虫会来，但") && t.Contains("静默夜"));
            bool came = StepUntil(() => WormInstance(s) != null, 80);
            EventDirectorInstanceRecord r = WormInstance(s);
            Expect(held && told && came && r.WarnTick >= sn.EndTick,
                $"N1 负向“震动值在静默夜期间达到 100”：推迟（原因 = 静默夜，{held}），警告与状态写明推迟原因（“{heldState}”，{told}），静默夜结束后才预警破土（预警步 {r?.WarnTick} ≥ 结束步 {sn.EndTick}）");
        }

        private static void CheckFrequencyOff()
        {
            CampaignState s = World(9402);
            EventDirectorService.SetFrequency(s, EventDirectorService.FrequencyOff);
            int rejected = s.DirectorEvents.Rejected;
            HomeSite(s).Value = 100;
            StepSec(15);
            bool none = WormInstance(s) == null && HomeSite(s).Level <= VibrationService.LevelOmen && s.DirectorEvents.Rejected == rejected && OnlyWorm(s) == null;
            EventDirectorService.SetFrequency(s, EventDirectorService.FrequencyStandard);
            bool back = StepUntil(() => WormInstance(s) != null, 20);
            Expect(none && back, $"N2 事件频率“关闭”：震动到 100 也不警告、不请求、蠕虫不来（{none}）；改回标准后照常来（{back}）");
        }

        private static void CheckOneAtATime()
        {
            CampaignState s = World(9403);
            VibrationState v = V(s);
            HomeSite(s).Value = 100;
            VibrationSiteRecord other = VibrationService.Site(v, "op-test", true);
            other.Value = 100;
            other.HotX = HomeSite(s).HotX + 50;
            StepSec(2);
            int req = v.Sites.Count(x => x.Level == VibrationService.LevelRequested);
            // 第一条结束后，另一处（两次检查之间衰减到 99.9x 也算到过 100）立即请求。
            bool next = false;
            VibrationSiteRecord waiting = v.Sites.FirstOrDefault(x => x.Level != VibrationService.LevelRequested);
            if (req == 1 && waiting != null && StepUntil(() => OnlyWorm(s)?.State == WormEventService.StateActive && WormInstance(s) != null, 60))
            {
                // 等的那一处不承诺倒计时：状态写“等当前这条蠕虫结束就来”，警告通知写明要等。
                bool waited = waiting.Level == VibrationService.LevelWarn && VibrationService.StateText(s, waiting, GameClock.Ticks).Contains("等当前这条蠕虫结束")
                              && EventTexts().Any(t => t.Contains("掘进蠕虫会来，但") && t.Contains("要等它结束"));
                EventDirectorService.Complete(s, WormInstance(s).Serial, EventDirectorService.OutcomeSuccess);
                StepSec(2);
                next = waited && waiting.Level == VibrationService.LevelRequested && v.Requests == 2;
            }
            Expect(req == 1 && v.Requests >= 1 && next, $"N3 两处同时到 100：同一时刻只请求一处（{req}），另一处等这条蠕虫结束（状态与警告写明在等，不承诺倒计时）、结束后立即请求（{next}，请求 {v.Requests}，等级 {string.Join("/", v.Sites.Select(x => x.SiteId + ":" + x.Level + ":" + x.Value.ToString("0.##")))}，排程 {EventDirectorService.Schedule(s, WormEventService.EventId)?.BlockedKey}；重大威胁 2 日一次由导演管）");
        }

        // ── U 战略地图 ───────────────────────────────────────────────────────────

        private static void CheckMap()
        {
            CampaignState s = World(9404);
            VibrationSiteRecord home = HomeSite(s);
            home.Value = 85;
            StepSec(2);
            var model = new WorldMapModel();
            var view = new WorldMapView { CenterX = home.HotX, CenterY = home.HotY, HalfWidth = 120, CanvasWidth = 1000, CanvasHeight = 800 };
            model.Collect(s, view, 256);
            bool ring = home.Level == VibrationService.LevelWarn
                        && model.Items.Any(i => i.Id == "vibration:" + VibrationService.HomeSite && i.Label.Contains("可能在这里破土"))
                        && model.Circles.Any(c => Math.Abs(c.X - home.HotX) < 1e-3 && Math.Abs(c.Y - home.HotY) < 1e-3);
            home.Value = 30;
            StepSec(2);
            model.Collect(s, view, 256);
            bool lifted = home.Level < VibrationService.LevelWarn && !model.Items.Any(i => i.Id == "vibration:" + VibrationService.HomeSite);
            home.Value = 100;
            StepSec(2); // 先过 80 警告再到 100
            bool came = StepUntil(() => OnlyWorm(s) != null, 20);
            WormRecord w = OnlyWorm(s);
            model.Collect(s, view, 256);
            bool route = came && w != null && w.RouteX.All(x => model.Lines.Any(l => Math.Abs(l.X0 - x) < 1e-3))
                         && model.Items.Any(i => i.Id == "worm:" + w.Serial && i.Label.Contains("掘进蠕虫路线"));
            Expect(ring && lifted && route,
                $"U1 战略地图（FGR-EVT-011“地图上标出震动最集中的区域”）：80 警告时在最集中处画圈并标“可能在这里破土”（{ring}）；回落后撤掉（{lifted}）；预警 / 在场的蠕虫画出整条路线并标名（{route}）");
        }

        // ── T 倍速 / 观察，S 存读档 ──────────────────────────────────────────────

        private static string Snap(CampaignState s)
        {
            var sb = new StringBuilder(JsonUtility.ToJson(s.Vibration));
            sb.Append('|').Append(JsonUtility.ToJson(s.DirectorEvents));
            CombatSite site = Site;
            foreach (WormRecord w in WormEventService.Worms(s))
            {
                if (site != null && site.TryGetWorm(w.Chain, out CombatWorm cw))
                {
                    sb.Append($"|w{cw.Id}:{cw.Phase}:{cw.S:R}:{cw.DeadMask}:{cw.HpSum:R}:{cw.Surfacings}:{cw.PhaseT:R}");
                }
            }
            sb.Append("|packets=").Append(string.Join(",", LootPickService.StateOf(s).Packets.Select(p => p.PacketId)));
            return sb.ToString();
        }

        private static string RunScenario(int seed, bool observe, float speed, out bool pausedHeld, out int frames)
        {
            CampaignState s = World(seed, observe);
            frames = 0;
            HomeSite(s).Value = 100;
            GameClock.SetPaused(true);
            string p0 = Snap(s);
            long t0 = GameClock.Ticks;
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            pausedHeld = Snap(s) == p0 && GameClock.Ticks == t0;
            GameClock.SetPaused(false);
            GameClock.SetSpeed(speed);
            long target = t0 + Sec(75); // 请求 → 30 秒裂纹 → 破土 → 钻出 / 潜入几轮
            int guard = 0;
            while (GameClock.Ticks < target && guard++ < 60 * 600)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return Snap(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true, paused = true, proportional = true;
            var shown = new List<string>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(9501, true, speed, out bool held, out int frames);
                paused &= held;
                reference ??= snap;
                same &= snap == reference;
                proportional &= Math.Abs(frames * speed - Sec(75)) <= 4f;
                shown.Add($"{speed}x {frames} 帧");
            }
            Expect(paused && same && proportional && reference.Contains("|w"),
                $"T1 暂停与 0.5x～3x：暂停中 120 帧震动与蠕虫不动；{string.Join(" / ", shown)} 跑同样的请求 → 30 秒裂纹 → 破土 → 钻出 / 潜入，震动、导演、链状态逐字段一致，" +
                $"所需真实帧与倍速成反比（暂停 {paused} / 一致 {same} / 成反比 {proportional}）");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(9502, true, 1f, out _, out _);
            string unseen = RunScenario(9502, false, 1f, out _, out _);
            Expect(seen == unseen && seen.Contains("|w"), "T2 家园观察与不观察时，同一段（请求、裂纹、破土、钻出 / 潜入、咬与建筑受创）结果逐字段一致（FGR-BASE-021）");
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = World(9601);
            Drills(s, 3, "sav");
            HomeSite(s).Value = 100;
            StepUntil(() => OnlyWorm(s)?.State == WormEventService.StateActive, 50);
            WormRecord w = OnlyWorm(s);
            if (w == null)
            {
                Fail("S1 蠕虫没有破土");
                return;
            }
            Site.Kernel.Kill(Segs(w)[0], 0);
            StepSec(3);
            HomeSite(s).Value = 37; // 家园在破土后重新累积的一段
            string before = Snap(s);
            SaveResult save = SaveNow(out int slot);
            StepSec(20);
            string continuous = Snap(s);
            CampaignState l = Reload(slot, "S1");
            if (l == null)
            {
                return;
            }
            string loaded = Snap(l);
            StepSec(20);
            string after = Snap(l);
            int diffAt = 0;
            while (diffAt < after.Length && diffAt < continuous.Length && after[diffAt] == continuous[diffAt])
            {
                diffAt++;
            }
            Expect(save.Success && loaded == before && after == continuous && loaded.Contains("|w"),
                "S1 负向“事件进行中存档”：蠕虫在地面上（已击毁 1 节）时真文件存档 → 读档后各地点震动值、蠕虫记录、导演实例与内核里的链（阶段、弧长、存活掩码、耐久、破土次数）完全一致，" +
                $"继续跑与不存档的结果逐字段一致（读回 {loaded == before}，继续 {after == continuous}" +
                (after == continuous ? string.Empty : $"，首个差异 …{continuous.Substring(Math.Max(0, diffAt - 40), Math.Min(100, continuous.Length - Math.Max(0, diffAt - 40)))}") + "）");
        }

        private static SaveResult SaveNow(out int slot)
        {
            WorldSimulation.SyncAllForSave();
            slot = CampaignSession.ActiveSlotIndex;
            return CampaignAutoSaveService.SaveWithExport(slot, SaveReason.Manual);
        }

        /// <summary>同一进程里读档（静态缓存不额外清：正式读档路径也不会替它们清）。</summary>
        private static CampaignState Reload(int slot, string tag)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            VibrationService.ResetSessionState();
            WormEventService.ResetSessionState();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(slot);
            if (!rr.Success)
            {
                Fail(tag + " 读档失败：" + rr.Message);
                return null;
            }
            CampaignSession.Set(slot, rr.State);
            WorldSimulation.LoadHome(resume: true);
            return rr.State;
        }

        private static GridCell CellOn(in CombatWorm cw, double at)
        {
            Vector2 p = CombatSite.WormPoint(cw, at);
            return new GridCell(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
        }

        /// <summary>头部前方 [<paramref name="from"/>, <paramref name="to"/>] 米（沿路线）里第一个空格、且路线在这一格里走过至少 0.6 米：放一座满耐久的电线杆（找不到返回 null）。</summary>
        private static BuildingRecord PoleAhead(CampaignState s, in CombatWorm cw, double from, double to, string key, out double at)
        {
            for (at = from; at <= to; at += 0.25)
            {
                GridCell c = CellOn(cw, cw.S + at), a = CellOn(cw, cw.S + at - 0.3), b = CellOn(cw, cw.S + at + 0.3);
                if (c.X == a.X && c.Y == a.Y && c.X == b.X && c.Y == b.Y && HomeGridService.BuildingAt(s, c) == null)
                {
                    BuildingRecord pole = FgProductionSelfCheck.Built(s, "power_pole", key, c);
                    pole.Health = BuildingOps.MaxDurability("power_pole");
                    return pole;
                }
            }
            return null;
        }

        private static string Poles(CampaignState s, params string[] ids) =>
            string.Concat(ids.Select(id => "|" + id + "=" + (s.BuildingRecords.FirstOrDefault(b => b?.BuildingId == id)?.Health ?? -1f).ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

        private static void CheckSaveLoadMidSurfacing()
        {
            CampaignState s = World(9602);
            Drills(s, 3, "mid");
            HomeSite(s).Value = 100;
            StepUntil(() => OnlyWorm(s)?.State == WormEventService.StateActive, 50);
            WormRecord w = OnlyWorm(s);
            if (w == null || !Site.TryGetWorm(w.Chain, out CombatWorm cw) || cw.Phase != CombatWormConst.PhaseSurface)
            {
                Fail("S2 蠕虫没有钻出地面");
                return;
            }
            // 同一次钻出里：X 在存档前被钻过（存档里 HitIds 有它），Y 在存档之后才被钻过。
            BuildingRecord x = PoleAhead(s, cw, 1.0, 4.0, "midx", out double xAt);
            BuildingRecord y = x != null ? PoleAhead(s, cw, xAt + 3.0, xAt + 7.0, "midy", out _) : null;
            if (x == null || y == null)
            {
                Fail($"S2 头部前方 1～{xAt + 7.0:0.#} 米沿路线找不到空格放测试用电线杆");
                return;
            }
            string xi = x.BuildingId, yi = y.BuildingId;
            bool xHit = StepUntil(() => w.HitIds.Contains(xi), 2.5) && !w.HitIds.Contains(yi);
            SaveResult save = SaveNow(out int slot);
            StepSec(3);
            bool yHit = w.HitIds.Contains(yi) && Site.TryGetWorm(w.Chain, out CombatWorm c2) && c2.Surfacings == cw.Surfacings;
            string continuous = Snap(s) + Poles(s, xi, yi);
            // 同一进程读档：Y 已经在上一段的受创缓存里，读档后必须照存档（只有 X）重新受创一次。
            CampaignState l = Reload(slot, "S2");
            if (l == null)
            {
                return;
            }
            StepSec(3);
            string after = Snap(l) + Poles(l, xi, yi);
            int diffAt = 0;
            while (diffAt < after.Length && diffAt < continuous.Length && after[diffAt] == continuous[diffAt])
            {
                diffAt++;
            }
            Expect(save.Success && xHit && yHit && after == continuous,
                $"S2 负向“同一次钻出中途存档”：存档时 X 已受创、Y 还没有（{xHit}），继续跑 Y 受创（{yHit}）；同一进程读档后照存档重来，Y 同样受创一次、X 不再受创，" +
                $"建筑耐久与受创计数和不读档逐字段一致（{after == continuous}" +
                (after == continuous ? string.Empty : $"，首个差异 …{continuous.Substring(Math.Max(0, diffAt - 40), Math.Min(100, continuous.Length - Math.Max(0, diffAt - 40)))}") + "；B10 / FGR-BASE-021）");
        }

        // ── P 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = World(9701);
            HomeSite(s).Value = 100;
            StepUntil(() => OnlyWorm(s)?.State == WormEventService.StateWarned, 20);
            WormRecord w = OnlyWorm(s);
            if (w != null)
            {
                w.Segments = 12; // 测满额 12 节
            }
            StepUntil(() => OnlyWorm(s)?.State == WormEventService.StateActive, 40);
            int steps = (int)Sec(20);
            double max = 0;
            for (int i = 0; i < steps; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                WorldSimulation.StepMany(1);
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                max = Math.Max(max, ms);
                PerfAcc += ms;
            }
            double avg = PerfAcc / Math.Max(1, steps);
            PerfAcc = 0;
            int segs = Site.TryGetWorm(w?.Chain ?? 0, out CombatWorm cw) ? cw.SegCount : 0;
            // 120 帧 = 每帧 8.33 ms；1x 下每帧约 1 个世界步（60 Hz 步 / 120 帧 = 0.5 步），3x 每帧约 1.5 步。
            double frame3x = avg * GameClock.StepHz * 3.0 / 120.0;
            PerfLines.Add($"P1 蠕虫 {segs} 节在家园破土、钻出 / 潜入、开局机器自动交战的 20 游戏秒：世界步平均 {avg:0.###} ms、最慢 {max:0.###} ms；3x 每帧约 {frame3x:0.###} ms（120 帧预算 8.33 ms）");
            if (frame3x > 8.33 * 2)
            {
                Fail($"P1 掘进蠕虫满额 12 节的世界步 3x 每帧 {frame3x:0.###} ms 超过 120 帧预算 2 倍");
            }
            else
            {
                Expect(segs == 12, $"P1 性能：满额 12 节在场，3x 每帧 {frame3x:0.###} ms（≤ 8.33 ms 判通过；超出但不到 2 倍记警告）");
                if (frame3x > 8.33)
                {
                    Line($"  ⚠ 性能警告：P1 3x 每帧 {frame3x:0.###} ms > 8.33 ms（不到 2 倍）");
                }
            }
        }

        /// <summary>P2 探针（总控第 5 条“量一次写清来源”）：本卡在热更层每个世界步新增的开销——生产步里按地点记提取钻、积分，加上震动 / 蠕虫两个世界步钩子（没有蠕虫时）。</summary>
        private static void ProbeHotfixCost()
        {
            CampaignState s = World(9702);
            List<BuildingRecord> d = Drills(s, 8, "probe");
            const int n = 3000;
            long tick = GameClock.Ticks;
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                VibrationService.BeginProductionStep();
                foreach (BuildingRecord b in d)
                {
                    VibrationService.AddDrill(s, b, 1f);
                }
                VibrationService.Integrate(s, 1.0 / 3600.0);
                VibrationService.WorldStep(s, tick + i);
                WormEventService.WorldStep(s);
            }
            double perStep = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency / n;
            PerfLines.Add($"P2 探针：本卡热更层每世界步新增（{d.Count} 座提取钻按地点记账 + 积分 + 震动 / 蠕虫世界步钩子，没有蠕虫）{perStep:0.####} ms（{n} 次取平均；生产步实际不是每个世界步都跑，这是上限）");
            Expect(d.Count == 8 && perStep < 0.1, $"P2 探针：本卡热更层每世界步新增 {perStep:0.####} ms（8 座提取钻；判据 < 0.1 ms，用于定位世界步退化来源）");
        }

        private static double PerfAcc;

        // ── 通用 ─────────────────────────────────────────────────────────────────

        private static void Step(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Fail($"{a.Method.Name} 抛异常：{e}");
            }
        }

        private static void Expect(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
                Line("  ✓ " + what);
            }
            else
            {
                Fail(what);
            }
        }

        private static void Fail(string what)
        {
            _fail++;
            Line("  ✗ " + what);
        }

        private static void Line(string s) => _report?.AppendLine(s);
    }
}
