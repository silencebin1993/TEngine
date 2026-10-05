using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG6-DEF-04 突袭导演与预警的自动验收（FG06 FGR-DEF-020～024；FG16 第 3、5 节；FG17 FGR-GEN-032、034；FGT-DEF-003、004；FG06 第 5 章负向“两次突袭时间重叠”）。
    /// 全部起真实系统：真实家园（世界模拟、寻路内核、侦察巢据点、行进队伍沿地形寻路）、真实暴露账本（裸跑敌方固件记账）、真文件存读档、真 UXML 预警条 / 战略地图 / 暴露面板。
    /// A 数据（六张新表与源数据逐字段、初值、文本中英、钩子、图鉴、通知类型）；
    /// B FGT-DEF-003：暴露 30 → 第一次突袭固定 1 级小规模（宽限期后抵达）、60 并进还没出发的计划、出发 → 到达 → 时间上限后沿原路撤回（战时预案开 / 关）、
    ///   骚扰（第一次突袭之后、频率上限）、90 → 3 级（最短间隔）、超过 90 周期性 4 级、降回阈值以下再越过再次触发；
    /// C 预算与编成（FG16 公式、阵营编成表、单位数上限换精英、后日谈）与阵营内反制（防线固件类别 → 对策、带对策标签的单位更多）；
    /// D FGT-DEF-004 预警：路程 = 预警时间、最短 1.5 游戏小时（太近时集结）、监听站最早提前 1 个游戏日并附带阵营 / 规模 / 方向 / 反制、出发后沿用同一条、与实际到来一致；
    /// E 出发地：离目标最近的活跃据点（两个种子，B25）、据点被毁 → 改道并再次预警、该方向频率降低、没有合适据点 → 迷雾外；目标可以是前哨站；
    /// F 负向：剧情与普通突袭重叠合并为一波、剧情不受最短间隔、建造者只有剧情突袭、严酷频率 / 规模 / 预警；
    /// G 真文件存读档（筹备 / 排定 / 集结 / 行进中各态）与旧档；H 暂停与 0.5x～3x；I 观察 / 不观察一致；J 预警条 / 地图箭头 / 预测路线 / 暴露面板（真 UXML、布局探针、英文）；
    /// K 预热途经区块（DEBT-FG0ARCH06-09：排定的长路线采纳步不硬等）；L 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgRaidDirectorSelfCheck）；单段入口 <see cref="RunFromMenu"/>。
    /// </summary>
    public static class FgRaidDirectorSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const string HudUxml = UiKitFolder + "RaidWarningHud.uxml";
        private const string MapUxml = UiKitFolder + "StrategicMap.uxml";
        private const int Slot = 5;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 突袭导演与预警")]
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
            Line("\n[突袭导演与预警] 六类触发、最短间隔、预算与编成、最受刺激的阵营、阵营内反制、出发地与沿地形行进、预警与情报（FG6-DEF-04）");
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
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgraid-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                BuildingOps.Reload();
                BuildMaterials.Reload();
                ResearchCatalog.Reload();
                IntelCatalog.Reload();
                RaidCatalog.Reload();
                DefenseCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                IntelService.ResetSessionState();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；导演 / 编成 / 预警在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），寻路与预热在 AOT 内核；真机另测（FG15-SYS-02）");
                Step(CheckData);
                Step(CheckExposureJourney);
                Step(CheckLevel4AndRearm);
                Step(CheckWarnedUpgrade);
                Step(CheckRetreatTrail);
                Step(CheckBudgetAndComposition);
                Step(CheckCounter);
                Step(CheckIntelForecast);
                Step(CheckIntelFollowsPlan);
                Step(CheckOrigins);
                Step(CheckTargets);
                Step(CheckStoryMerge);
                Step(CheckDepartedStoryOverlap);
                Step(CheckDifficulty);
                Step(CheckSaveLoad);
                Step(CheckLaneSaveTiming);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckHud);
                Step(CheckMapAndPanels);
                Step(CheckBackgroundLane);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"突袭导演自检抛异常：{e}");
            }
            finally
            {
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                RaidWarningHudUIToolkit.InWorldOverrideForTests = false;
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                StrategicMapUIToolkit.Close();
                IntelService.ResetSessionState();
                RaidDirectorService.ResetSessionState();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ItemDistribution.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                BuildMaterials.ResetForTests();
                HomeInventory.ResetSessionState();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                HomeValleyWorkOrders.ResetSessionState();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                if (!hadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
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
            Line($"  · [突袭导演与预警] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, string difficulty = "Standard")
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            // 与主菜单“新建”同一入口：按种子生成 v2 世界（规划层、家园区侦察巢据点），再载入家园。
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgraid-" + seed, difficulty, seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = 600;
            WorldTransitSystem.ResetCountersForTests();
            WorldSimulation.StepMany(2);
            return s;
        }

        /// <summary>某阵营的据点（家园区侦察巢按最近的本幕领地归属）。</summary>
        private static List<OutpostRecord> OutpostsOf(CampaignState s, string faction) =>
            WorldOutpostSystem.Outposts(s).Where(o => o != null && !o.Destroyed && RaidDirectorService.FactionOfOutpost(s, o) == faction).ToList();

        /// <summary>从 <paramref name="from"/> 起依次试种子，直到世界满足 <paramref name="ok"/>（例如“静默在同一方向有两个据点”）；最多 <paramref name="tries"/> 个。</summary>
        private static CampaignState FindWorld(int from, int tries, Func<CampaignState, bool> ok, out int seed)
        {
            for (int i = 0; i < tries; i++)
            {
                seed = from + i;
                CampaignState s = NewWorld(seed);
                if (ok(s))
                {
                    return s;
                }
            }
            seed = -1;
            return null;
        }

        private static RaidDirectorState D(CampaignState s) => RaidDirectorService.StateOf(s);
        private static RaidPlanRecord[] Plans(CampaignState s) => D(s)?.Plans ?? Array.Empty<RaidPlanRecord>();
        private static RaidPlanRecord LatestPlan(CampaignState s) => Plans(s).OrderByDescending(p => p.Serial).FirstOrDefault();
        private static long Day(double d) => RaidDirectorService.DayTicks(d);

        /// <summary>某阵营的一枚敌方固件（裸跑发动一次 = 该阵营暴露 +2，走真实暴露账本）。</summary>
        private static string FirmwareOf(string faction) =>
            ConfigSystem.Instance.Tables.TbFirmwareKind.DataList.First(r => r != null && r.Faction == faction).Id;

        /// <summary>按真实账本把暴露抬到 ≥ <paramref name="target"/>（贡献记在 <paramref name="faction"/>），再走两步让导演处理触发。</summary>
        private static void Raise(CampaignState s, string faction, float target)
        {
            string fw = FirmwareOf(faction);
            for (int guard = 0; guard < 200 && s.SignalExposure < target; guard++)
            {
                CampaignExposureLedger.GrantRawFire(s, fw);
            }
            WorldSimulation.StepMany(2);
        }

        /// <summary>“读一个第 6 天的存档”：开局宽限已过（只在还没有任何计划时用）。</summary>
        private static void PassGrace(CampaignState s)
        {
            long target = RaidDirectorService.GraceEndTick + Day(0.2);
            if (Plans(s).Length == 0 && GameClock.Ticks < target)
            {
                GameClock.SkipForTests(s, target - GameClock.Ticks);
            }
            WorldSimulation.StepMany(2);
        }

        private static bool RunUntil(Func<bool> done, int maxGameSeconds)
        {
            long limit = GameClock.Ticks + (long)maxGameSeconds * GameClock.StepHz;
            while (GameClock.Ticks < limit)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(15);
            }
            return done();
        }

        /// <summary>
        /// 跳过空档：所有计划都已排定且下一次事件（发预警）在 <paramref name="target"/> 之后、没有行进中 / 已到达 / 撤退中的队伍、没有排队的触发——中间没有任何导演事件，
        /// 等于读一个更晚的存档。条件不满足返回 false（自检按失败处理，不硬跳）。
        /// </summary>
        private static bool SkipIdle(CampaignState s, long target)
        {
            if (target <= GameClock.Ticks)
            {
                return true;
            }
            if (D(s).Pending.Length > 0 || WorldTransitSystem.Groups(s).Any(g => g != null))
            {
                return false;
            }
            foreach (RaidPlanRecord p in Plans(s))
            {
                if (p.State != RaidDirectorService.StateScheduled || p.WarnTick <= target)
                {
                    return false;
                }
            }
            if (D(s).HarassNextTick >= 0 && D(s).HarassNextTick <= target && D(s).RaidCount >= 1)
            {
                return false;
            }
            if (D(s).Level4NextTick >= 0 && D(s).Level4NextTick <= target)
            {
                return false;
            }
            GameClock.SkipForTests(s, target - GameClock.Ticks);
            return true;
        }

        /// <summary>让排好的计划走到出发：跳过筹备空档、真实推进预警 → 集结 → 出发。</summary>
        private static bool AdvanceToDeparture(CampaignState s, RaidPlanRecord p, int maxSeconds = 600)
        {
            if (!RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 120))
            {
                return false;
            }
            if (p.State == RaidDirectorService.StateScheduled && !SkipIdle(s, p.WarnTick - 2))
            {
                return false;
            }
            return RunUntil(() => p.State >= RaidDirectorService.StateDeparted, maxSeconds);
        }

        private static int NotifyCount(string type) => NotificationCenter.History.Where(e => e.Type?.Id == type).Sum(e => Math.Max(1, e.Members.Count));

        private static string LastNotify(string type) =>
            NotificationCenter.History.Where(e => e.Type?.Id == type).OrderByDescending(e => e.Id).FirstOrDefault()?.Latest?.DetailText ?? string.Empty;

        private static string Short(string s) => s == null ? string.Empty : s.Length > 90 ? s.Substring(0, 90) + "…" : s;

        private static int PointsOf(RaidPlanRecord p)
        {
            int sum = 0;
            for (int i = 0; i < p.UnitIds.Length; i++)
            {
                RaidCatalog.TryGetUnit(p.UnitIds[i], out RaidUnitDef u);
                sum += u.Points * p.UnitCounts[i] + (u.ElitePoints - u.Points) * p.EliteCounts[i];
            }
            return sum;
        }

        /// <summary>编成合法：单位都属于这个阵营、等级够；点数不超预算；剩余预算不够最便宜的单位（或单位数到上限）；合计数 = 总数。</summary>
        private static bool CompositionValid(RaidPlanRecord p, out string why)
        {
            var units = new List<RaidUnitDef>();
            RaidCatalog.UnitsFor(p.Faction, Math.Max(0, p.Level), units);
            int cheapest = units.Count > 0 ? units.Min(u => u.Points) : int.MaxValue;
            int total = p.UnitCounts.Sum();
            int pts = PointsOf(p);
            bool ownFaction = p.UnitIds.All(id => RaidCatalog.TryGetUnit(id, out RaidUnitDef u) && u.Faction == p.Faction && u.MinLevel <= Math.Max(0, p.Level));
            bool exhausted = total >= RaidCatalog.MaxUnits || p.Budget - pts < cheapest || total == 1;
            why = $"预算 {p.Budget}、用掉 {pts}、最便宜 {cheapest}、{total} 台（{string.Join("、", p.UnitIds.Select((id, i) => id + "×" + p.UnitCounts[i] + (p.EliteCounts[i] > 0 ? "（精英 " + p.EliteCounts[i] + "）" : string.Empty)))}）";
            return p.UnitTotal == total && total >= 1 && ownFaction && (pts <= p.Budget || total == 1) && exhausted;
        }

        // ── A 数据 ────────────────────────────────────────────────────────────

        private static string Cell(object v) => v is float f ? Norm(f.ToString("R", CultureInfo.InvariantCulture)) : v?.ToString() ?? string.Empty;

        private static string Norm(string pyCell)
        {
            return double.TryParse(pyCell, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && pyCell.Contains(".")
                ? d.ToString("0.###", CultureInfo.InvariantCulture)
                : pyCell;
        }

        private static void CheckData()
        {
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            (int code, string output) = F.RunPython(F.LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            var py = new HashSet<string>(output.Replace("\r", string.Empty).Split('\n')
                .Where(l => l.StartsWith("RDT\t") || l.StartsWith("RDL\t") || l.StartsWith("RDD\t") || l.StartsWith("RDU\t") || l.StartsWith("RDC\t") || l.StartsWith("RDS\t")
                            || (l.StartsWith("CX\tcodex.raid.") && !l.StartsWith("CX\tcodex.raid.siege") && !l.StartsWith("CX\tcodex.raid.hud"))) // 图鉴“攻城行为”归 FgSiegeSelfCheck A1，“突袭 HUD”归 FgUplinkHudSelfCheck 图鉴段比对
                .Select(l => string.Join("\t", l.Split('\t').Select(Norm))));
            var rt = new HashSet<string>();
            foreach (GameConfig.fg.RaidTrigger r in t.TbRaidTrigger.DataList)
            {
                rt.Add(string.Join("\t", "RDT", r.Id, r.NameKey, r.DescKey, r.Exempt, r.LevelRule, r.OpensIn, r.SortOrder));
            }
            foreach (GameConfig.fg.RaidLevel r in t.TbRaidLevel.DataList)
            {
                rt.Add(string.Join("\t", "RDL", r.Id, r.Level, r.Budget, r.NameKey));
            }
            foreach (GameConfig.fg.RaidDifficulty r in t.TbRaidDifficulty.DataList)
            {
                rt.Add(string.Join("\t", "RDD", r.Id, r.StoryOnly, Cell(r.Scale), Cell(r.Frequency), Cell(r.Warning), r.NameKey));
            }
            foreach (GameConfig.fg.RaidUnit r in t.TbRaidUnit.DataList)
            {
                rt.Add(string.Join("\t", "RDU", r.Id, r.Faction, r.EnemyTypeId, r.Role, r.Points, r.MinLevel, Cell(r.Weight), r.ElitePoints, r.Tags));
            }
            foreach (GameConfig.fg.RaidCounter r in t.TbRaidCounter.DataList)
            {
                rt.Add(string.Join("\t", "RDC", r.Id, r.Faction, r.Category, r.Tag, r.NameKey, r.DescKey, Cell(r.Boost)));
            }
            foreach (GameConfig.fg.RaidStory r in t.TbRaidStory.DataList)
            {
                rt.Add(string.Join("\t", "RDS", r.Id, r.Rule, r.Faction, r.Level, r.NameKey));
            }
            GameConfig.fg.CodexEntry cx = t.TbCodexEntry.GetOrDefault("codex.raid.director");
            if (cx != null)
            {
                rt.Add(string.Join("\t", "CX", cx.Id, cx.Tab, cx.TitleKey, cx.BodyKey, cx.HintKey, cx.Links, cx.Hooks, cx.SortOrder));
            }
            var missing = py.Except(rt).ToList();
            var extra = rt.Except(py).ToList();
            Expect(code == 0 && py.Count > 20 && missing.Count == 0 && extra.Count == 0 && RaidCatalog.Problems.Count == 0,
                $"A1 六张新表（触发 / 等级 / 难度 / 编成 / 反制 / 剧情）与图鉴条目逐字段 = 数据源（{py.Count} 行；运行时目录无问题）"
                + (missing.Count + extra.Count > 0 ? $"\n缺：{string.Join(" | ", missing.Take(4))}\n多：{string.Join(" | ", extra.Take(4))}" : string.Empty)
                + (RaidCatalog.Problems.Count > 0 ? "\n目录问题：" + string.Join("；", RaidCatalog.Problems) : string.Empty));

            bool init = Math.Abs(RaidCatalog.MinIntervalDays - 1f) < 1e-4 && Math.Abs(RaidCatalog.MinWarningHours - 1.5f) < 1e-4 && Math.Abs(RaidCatalog.IntelLeadDays - 1f) < 1e-4
                        && Math.Abs(RaidCatalog.TimeLimitHours - 1f) < 1e-4 && RaidCatalog.LevelBudget(1) == 100 && RaidCatalog.LevelBudget(2) == 250 && RaidCatalog.LevelBudget(3) == 500
                        && RaidCatalog.LevelBudget(4) == 900 && Math.Abs(RaidCatalog.ActCoef(1) - 1f) < 1e-4 && Math.Abs(RaidCatalog.ActCoef(2) - 1.6f) < 1e-4
                        && Math.Abs(RaidCatalog.ActCoef(3) - 2.4f) < 1e-4 && RaidCatalog.MaxUnits == 200 && RaidCatalog.Triggers.Count == 6
                        && RaidCatalog.StoryOnly("Builder") && !RaidCatalog.StoryOnly("Standard") && Math.Abs(RaidCatalog.DifficultyFrequency("Harsh") - 1.3f) < 1e-4
                        && Math.Abs(RaidCatalog.DifficultyWarning("Harsh") - 0.8f) < 1e-4 && Math.Abs(RaidCatalog.DifficultyScale("Builder") - 0.5f) < 1e-4
                        && Math.Abs(RaidDirectorService.MinWarningTicks(new CampaignState { DifficultyId = "Standard" }) - GameClock.TicksFor(75)) <= 1;
            Expect(init, "A2 初值（FG06 第 10 节 / FG16）：最短间隔 1 游戏日、最短预警 1.5 游戏小时（1x 下 75 秒）、监听站最长预警 1 游戏日、时间上限 1 游戏小时；"
                         + "1～4 级预算 100 / 250 / 500 / 900；幕系数 1 / 1.6 / 2.4；单位上限 200；建造者只有剧情 ×0.5、严酷频率 ×1.3 预警 ×0.8");

            string[] keys =
            {
                "raid.warning.title", "raid.warning.row", "raid.warning.row_assembling", "raid.warning.row_planned", "raid.warning.row_arrived", "raid.warning.comp_unknown",
                "raid.warning.notify", "raid.warning.notify_assembling", "raid.warning.notify_rerouted", "raid.warning.notify_merged", "raid.withdrawn.notify",
                "codex.raid.director.title", "codex.raid.director.body", "exposure.panel.faction_raid", "exposure.panel.raid_line", "ui.map.raid_warning",
                "overlay.label.raid_planned", "ui.world.focus.raid_assembling", "ui.world.focus.raid_retreating", "intel.raid.with_counter", "difficulty.builder.name", "difficulty.harsh.name",
            };
            var allKeys = keys.Concat(RaidCatalog.Triggers.SelectMany(x => new[] { x.NameKey, x.DescKey }))
                .Concat(RaidCatalog.Counters.SelectMany(c => new[] { c.NameKey, c.DescKey })).ToList();
            var bad = allKeys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrEmpty(zh)
                                          || !GameText.TryGet(k, GameLanguage.En, out string en) || string.IsNullOrEmpty(en)).ToList();
            bool notify = NotificationCatalog.TryGetType("raid_warning", out NotifyTypeDef w) && w.Tier == NotifyLevel.Warning
                          && NotificationCatalog.TryGetType("raid_withdrawn", out NotifyTypeDef wd) && wd.Tier == NotifyLevel.Info;
            bool hooks = new[] { GuidanceHooks.RaidFirstPlanned, GuidanceHooks.RaidFirstWarning, GuidanceHooks.RaidFirstAssembling, GuidanceHooks.RaidFirstWithdrawn, GuidanceHooks.RaidWarningFirstClick }
                .All(h => GuidanceHooks.Known.Contains(h));
            Expect(bad.Count == 0 && notify && hooks && cx != null,
                $"A3 文本键中英齐全（{allKeys.Count} 个）、通知类型 raid_warning（警告）/ raid_withdrawn（信息）、5 个引导钩子已登记、图鉴条目“突袭导演与预警”存在"
                + (bad.Count > 0 ? "；缺：" + string.Join(",", bad.Take(6)) : string.Empty));
            // 触发来源还没做的三类：表里写明承接 Story（不静默）。
            bool opens = RaidCatalog.TryGetTrigger(RaidCatalog.TriggerSilentNight, out RaidTriggerDef sn) && sn.OpensIn == "FG7-ENV-02"
                         && RaidCatalog.TryGetTrigger(RaidCatalog.TriggerMegastructure, out RaidTriggerDef mg) && mg.OpensIn == "FG13-END-01"
                         && RaidCatalog.TryGetTrigger(RaidCatalog.TriggerPurification, out RaidTriggerDef pu) && pu.OpensIn == "FG10-RAID-01"
                         && RaidCatalog.TryGetTrigger(RaidCatalog.TriggerExposure, out RaidTriggerDef ex) && ex.OpensIn == null && !ex.Exempt
                         && RaidCatalog.TryGetTrigger(RaidCatalog.TriggerStory, out RaidTriggerDef st) && st.Exempt;
            Expect(opens, "A4 静默夜 / 巨构阶段 / 净化塔三类触发的来源系统在后续 Story：表里写明承接（FG7-ENV-02 / FG13-END-01 / FG10-RAID-01）；剧情不受最短间隔、暴露受限");
        }

        // ── B FGT-DEF-003：暴露触发的完整旅程 ─────────────────────────────────────────

        private static void CheckExposureJourney()
        {
            CampaignState s = NewWorld(6101);
            // B1：暴露越过 30（铸造贡献）→ 第一次突袭：1 级、规模很小、铸造发动、抵达不早于开局宽限终点。
            int planned0 = Plans(s).Length;
            Raise(s, CampaignExposureLedger.FactionFoundry, 31f);
            RaidPlanRecord p = LatestPlan(s);
            bool planned = p != null && planned0 == 0 && p.Trigger == RaidCatalog.TriggerExposure && p.Level == 1 && p.FirstRaid && p.Faction == "foundry"
                           && p.Budget == (int)Math.Round(100 * 1.0 * 1.0 * RaidDirectorService.IntervalCoef(D(s), p.CreatedTick) * RaidCatalog.FirstRaidScale)
                           && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidFirstPlanned);
            bool comp = p != null && CompositionValid(p, out string compWhy);
            CompositionValid(p ?? new RaidPlanRecord(), out compWhy);
            Expect(planned && comp, $"B1 暴露越过 30（铸造贡献）→ 排定第一次突袭：1 级、预算 {p?.Budget}（100 × 幕 1 × 难度 1 × 间隔 × 0.6）、铸造发动（暴露贡献最多）；编成 {compWhy}");

            // B2：再越过 60（还没出发）→ 并进同一个计划，不另起一波；第一次突袭保持 1 级。
            Raise(s, CampaignExposureLedger.FactionFoundry, 61f);
            bool merged = Plans(s).Length == 1 && D(s).TriggersMerged == 1 && p.Level == 1 && (D(s).ThresholdFired & 3) == 3;
            Expect(merged, $"B2 越过 60 时上一次还没出发：并进同一个计划（计划 {Plans(s).Length} 个、合并 {D(s).TriggersMerged} 次），第一次突袭仍是 1 级");

            // D1：路线就绪 → 预警时间 = 沿地形的路程；不足 1.5 游戏小时推迟出发（集结）；抵达不早于宽限终点。
            bool scheduled = RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 120);
            double along = WorldTransitSystem.PathUntilArrival(p.OriginX, p.OriginY, p.RouteX, p.RouteY, 0, p.TargetX, p.TargetY, WorldTransitSystem.ArrivalRadius, out _, out _, out bool entered);
            long minWarn = RaidDirectorService.MinWarningTicks(s);
            bool timing = scheduled && p.RouteState == RaidDirectorService.RouteReady && p.RouteX.Length > 0 && p.ArrivalTick >= RaidDirectorService.GraceEndTick
                          && p.ArrivalTick - p.WarnTick >= minWarn && p.ArrivalTick - p.DepartTick == p.TravelTicks
                          && p.DepartTick - p.WarnTick == Math.Max(0, minWarn - p.TravelTicks)
                          && Math.Abs(p.TravelTicks - (along / (WorldTransitSystem.RaidSpeed / GameClock.StepHz) + 1)) <= 2;
            Expect(timing, $"D1 路线沿地形算好（{p.RouteX.Length} 个路点、{along:F0} 格）：行进 {p.TravelTicks} 步 = 路程 / 速度；预警 → 抵达 {p.ArrivalTick - p.WarnTick} 步 ≥ 最短 {minWarn}" +
                           $"（集结 {p.DepartTick - p.WarnTick} 步）；抵达 {p.ArrivalTick} ≥ 开局宽限终点 {RaidDirectorService.GraceEndTick}；出发地 {p.OriginKind}:{p.OriginId}");

            // D2：预警发出（倒计时、方向、目标，定位到预计抵达点）→ 出发 → 沿排定路线行进 → 到达时刻 = 计划抵达。
            int warn0 = NotifyCount("raid_warning");
            bool skipped = SkipIdle(s, p.WarnTick - 2);
            bool warned = skipped && RunUntil(() => p.State >= RaidDirectorService.StateWarned, 10) && NotifyCount("raid_warning") == warn0 + 1
                          && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidFirstWarning)
                          && LastNotify("raid_warning").Contains(RaidDirectorService.DirectionText(p));
            NotificationEntry wn = NotificationCenter.History.Where(e => e.Type?.Id == "raid_warning").OrderByDescending(e => e.Id).FirstOrDefault();
            bool located = wn?.Latest != null && wn.Latest.HasLocation && Vector2.Distance(new Vector2(wn.Latest.Location.x, wn.Latest.Location.z), new Vector2(p.ArriveX, p.ArriveY)) < 1f;
            int garrison0 = p.OriginKind == RaidDirectorService.OriginOutpost ? WorldOutpostSystem.Find(s, p.OriginId).Garrison : 0;
            bool departed = RunUntil(() => p.State >= RaidDirectorService.StateDeparted, 300);
            TransitGroupRecord g = WorldTransitSystem.Find(s, p.GroupId);
            bool group = departed && g != null && g.PlanId == p.PlanId && g.Faction == "foundry" && g.UnitCount == p.UnitTotal && g.RouteState == WorldTransitSystem.RouteFollowing
                         && g.RouteX.Length == p.RouteX.Length && g.UnitIds.SequenceEqual(p.UnitIds) && g.HasOrigin
                         && (p.OriginKind != RaidDirectorService.OriginOutpost || WorldOutpostSystem.Find(s, p.OriginId).Garrison == garrison0 - g.GarrisonTaken);
            bool arrived = g != null && RunUntil(() => g.State != TransitGroupState.Marching, 600);
            bool onTime = arrived && Math.Abs(g.ArrivedAtTick - p.ArrivalTick) <= 2 && g.ArrivedAtTick - p.WarnTick >= minWarn;
            Expect(warned && located && group && onTime,
                $"D2 预警：发出“{Short(LastNotify("raid_warning"))}”（定位到预计抵达点 {located}）；出发时派出带计划编号与编成的队伍、沿排定的地形路线走（据点驻军随队 {g?.GarrisonTaken} 台）；" +
                $"实际到达 {g?.ArrivedAtTick}，计划 {p.ArrivalTick}（预警到到达 {g?.ArrivedAtTick - p.WarnTick} 步 ≥ {minWarn}）");

            // B3：到达 = 突袭开始（战时预案判定）；外围停留到时间上限（1 游戏小时）→ 沿原路撤回出发地 → 离场；计划结束进历史。
            bool active = StandingRuleService.RaidActive(s);
            long arrivedAt = g?.ArrivedAtTick ?? 0;
            int withdrawn0 = NotifyCount("raid_withdrawn");
            // FG6-DEF-05 起到达后展开攻城：到时间上限下撤退令（攻城单位走回集结点离场），全部离场后才并回行进队伍沿原路撤回——撤回时刻 = 时间上限 + 走回集结点的时间。
            bool retreat = g != null && RunUntil(() => g.State == TransitGroupState.Retreating, 180)
                           && g.Engaged && g.SiegeRetreatReason == SiegeService.ReasonTime
                           && g.RetreatTick - arrivedAt >= WorldTransitSystem.TimeLimitTicks - 1 && g.RetreatTick - arrivedAt <= WorldTransitSystem.TimeLimitTicks + GameClock.TicksFor(90f)
                           && NotifyCount("raid_withdrawn") == withdrawn0 + 1 && !StandingRuleService.RaidActive(s) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidFirstWithdrawn);
            bool gone = RunUntil(() => WorldTransitSystem.Find(s, p.GroupId) == null && RaidDirectorService.FindPlan(s, p.PlanId) == null, 900);
            RaidHistoryRecord h = RaidDirectorService.History(s).LastOrDefault();
            bool hist = gone && h != null && h.PlanId == p.PlanId && h.EndReason == RaidDirectorService.EndWithdrawn && h.ArrivedTick == arrivedAt
                        && (p.OriginKind != RaidDirectorService.OriginOutpost || WorldOutpostSystem.Find(s, p.OriginId).Garrison >= garrison0 - (g?.GarrisonTaken ?? 0));
            Expect(active && retreat && hist, $"B3 到达即“突袭进行中”（战时预案 {active}）；展开攻城，到时间上限 {WorldTransitSystem.TimeLimitTicks} 步下撤退令（{g?.SiegeRetreatReason}），走回集结点离场后（第 {g?.RetreatTick - arrivedAt} 步）沿原路撤回、发“突袭部队撤退”、" +
                                             $"突袭结束（{!StandingRuleService.RaidActive(s)}）；回到出发地离场，计划进历史（{h?.EndReason}），抽走的驻军还给据点");

            // B4：骚扰——第一次突袭之后、两次至少隔 3 天、最近 2 天没有突袭；只用骚扰也会出现的单位（minLevel 0）。
            long harassAt = D(s).HarassNextTick;
            bool skippedH = SkipIdle(s, harassAt - 1);
            WorldSimulation.StepMany(GameClock.StepHz + 2);
            RaidPlanRecord hp = LatestPlan(s);
            var lv0 = new List<RaidUnitDef>();
            RaidCatalog.UnitsFor(hp?.Faction ?? "foundry", 0, lv0);
            bool harass = skippedH && hp != null && hp.Trigger == RaidCatalog.TriggerHarass && hp.Level == 0 && hp.UnitIds.All(id => lv0.Any(u => u.Id == id))
                          && D(s).HarassNextTick >= GameClock.Ticks + Day(RaidCatalog.HarassMinIntervalDays) - GameClock.StepHz * 2
                          && hp.Budget == RaidDirectorService.ComputeBudget(s, D(s), hp);
            Expect(harass, $"B4 侦察骚扰：第一次突袭之后到了频率上限的时刻才来（{harassAt}），0 级、只出骚扰单位（{string.Join(",", hp?.UnitIds ?? Array.Empty<string>())}）、预算 {hp?.Budget}；下一次骚扰再隔 3 天");

            // B5：90（之前 30 / 60 已触发）→ 3 级，另起一波（骚扰那波已在走）；普通突袭之间最短间隔 1 天。
            bool hpGone = hp != null && AdvanceToDeparture(s, hp) && RunUntil(() => RaidDirectorService.FindPlan(s, hp.PlanId) == null, 1200);
            Raise(s, CampaignExposureLedger.FactionFoundry, 91f);
            RaidPlanRecord p3 = LatestPlan(s);
            bool lv3 = hpGone && p3 != null && p3 != hp && p3.Level == 3 && !p3.FirstRaid && p3.Budget == RaidDirectorService.ComputeBudget(s, D(s), p3)
                       && RunUntil(() => p3.State >= RaidDirectorService.StateScheduled, 120)
                       && p3.ArrivalTick >= p3.IntervalBaseTick + (long)Math.Round(Day(1) * p3.IntervalMul) && p3.IntervalBaseTick >= hp.ArrivalTick
                       && D(s).Level4NextTick > GameClock.Ticks;
            Expect(lv3, $"B5 暴露越过 90 → 3 级突袭（预算 {p3?.Budget} = 500 × 幕 × 难度 × 间隔系数 {RaidDirectorService.IntervalCoef(D(s), p3?.CreatedTick ?? 0):F2}）；" +
                        $"抵达 {p3?.ArrivalTick} ≥ 上一波普通突袭 {p3?.IntervalBaseTick} + 1 天 × {p3?.IntervalMul:F1}；超过 90 开始 4 级周期计时（{D(s).Level4NextTick}）");
        }

        // ── B6 / B7：周期 4 级、阈值重新武装 ─────────────────────────────────────────────

        private static void CheckLevel4AndRearm()
        {
            CampaignState s = NewWorld(6111);
            PassGrace(s);
            Raise(s, CampaignExposureLedger.FactionSilent, 95f);
            RaidPlanRecord first = LatestPlan(s);
            bool once = Plans(s).Length == 1 && first.Level == 1 && first.FirstRaid && (D(s).ThresholdFired & 7) == 7 && D(s).Level4NextTick > 0 && first.Faction == "silent";
            Expect(once, $"B6a 一笔越过 30 / 60 / 90：只排一波（第一次突袭固定 1 级），三个阈值都记为已触发，4 级周期开始计时（{D(s).Level4NextTick}）");
            bool done = AdvanceToDeparture(s, first) && RunUntil(() => RaidDirectorService.FindPlan(s, first.PlanId) == null, 1500);
            long l4 = D(s).Level4NextTick;
            bool skip = done && SkipIdle(s, l4 - 1);
            WorldSimulation.StepMany(GameClock.StepHz + 2);
            RaidPlanRecord p4 = LatestPlan(s);
            bool level4 = skip && p4 != null && p4 != first && p4.Level == 4 && p4.Trigger == RaidCatalog.TriggerExposure && D(s).Level4NextTick >= l4 + Day(RaidCatalog.Level4PeriodDays) - GameClock.StepHz;
            Expect(level4, $"B6b 暴露一直超过 90：到时刻（{l4}）发生 4 级突袭（预算 {p4?.Budget}），下一次再隔 {RaidCatalog.Level4PeriodDays} 天（{D(s).Level4NextTick}）");

            // B7：关闭信号塔广播把暴露降到 90 − 10 以下 → 90 的位清掉、4 级停止计时；再越过 90 → 再次触发（并进还没出发的那一波，等级升到 3 以上）。
            CampaignExposureLedger.SetTowerBroadcastOff(s, true);
            for (int i = 0; i < 20 && s.SignalExposure >= 79f; i++)
            {
                CampaignExposureLedger.TickTowerBroadcastOff(s, 10f);
            }
            CampaignExposureLedger.SetTowerBroadcastOff(s, false);
            WorldSimulation.StepMany(2);
            bool rearmed = (D(s).ThresholdFired & 4) == 0 && D(s).Level4NextTick < 0 && s.SignalExposure < 80f;
            int merged0 = D(s).TriggersMerged;
            Raise(s, CampaignExposureLedger.FactionSilent, 91f);
            bool again = (D(s).ThresholdFired & 4) != 0 && D(s).TriggersMerged == merged0 + 1 && D(s).Level4NextTick > 0;
            Expect(rearmed && again, $"B7 暴露降回 80 以下（{s.SignalExposure:F1}）：90 的阈值重新武装、4 级停止计时；再越过 90 → 再次触发（并进还没出发的那一波）");
        }

        // ── B8 复审 P2：已预警（集结中）的普通突袭不吞掉更高等级的阈值触发 ─────────────────────────

        private static void CheckWarnedUpgrade()
        {
            // 找一个静默据点离家园很近（路程不够最短预警 → 部队先在据点集结）的种子（不写死坐标，B25）。
            CampaignState s = FindWorld(6222, 16, x =>
            {
                GridCell c0 = HomeGridService.CorePivot(x);
                OutpostRecord n0 = Nearest(x, "silent", c0.X, c0.Y);
                return n0 != null && Math.Sqrt((n0.CellX - c0.X) * (double)(n0.CellX - c0.X) + (n0.CellY - c0.Y) * (double)(n0.CellY - c0.Y)) < 95;
            }, out int seed);
            if (s == null)
            {
                Fail("B8 16 个种子里找不到静默据点离家园 95 格以内的世界");
                return;
            }
            PassGrace(s);
            Raise(s, CampaignExposureLedger.FactionSilent, 31f);
            RaidPlanRecord p = LatestPlan(s);
            bool assembling = p != null && RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 120) && SkipIdle(s, p.WarnTick - 2)
                              && RunUntil(() => p.State >= RaidDirectorService.StateWarned, 10) && p.State == RaidDirectorService.StateWarned && p.DepartTick > GameClock.Ticks;
            int level0 = p?.Level ?? -1;
            int units0 = p?.UnitTotal ?? -1;
            int merged0 = D(s).TriggersMerged;
            Raise(s, CampaignExposureLedger.FactionSilent, 61f);
            RaidPlanRecord q = LatestPlan(s);
            bool split = assembling && q != null && q != p && q.Level == 2 && !q.FirstRaid && q.Trigger == RaidCatalog.TriggerExposure && (D(s).ThresholdFired & 2) != 0
                         && p.Level == level0 && p.UnitTotal == units0 && D(s).TriggersMerged == merged0;
            long gap = (long)Math.Round(Day(RaidCatalog.MinIntervalDays) * (q?.IntervalMul ?? 1f) / RaidCatalog.DifficultyFrequency(s.DifficultyId));
            bool spaced = split && RunUntil(() => q.State >= RaidDirectorService.StateScheduled, 200) && q.IntervalBaseTick == p.ArrivalTick && q.ArrivalTick >= p.ArrivalTick + gap;
            Expect(split && spaced, $"B8 种子 {seed}：第一波（{level0} 级）已发预警、在据点集结时暴露越过 60 → 不再并进去被吞掉，另起一波 {q?.Level} 级（已集结的那波规模不变 {p?.UnitTotal} 台），" +
                                    $"按最短间隔排在它后面：抵达 {q?.ArrivalTick} ≥ {p?.ArrivalTick} + {gap}");
        }

        // ── B9 复审 P2：行军途中重新要过路线，撤退仍沿原路 ─────────────────────────────────────────

        private static void CheckRetreatTrail()
        {
            CampaignState s = NewWorld(6151); // E1 同一种子：静默在家园区有侦察巢（行程短）
            PassGrace(s);
            RaidDirectorService.RequestStoryRaid(s, "silent", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p = LatestPlan(s);
            bool departed = p != null && AdvanceToDeparture(s, p);
            TransitGroupRecord g = departed ? WorldTransitSystem.Find(s, p.GroupId) : null;
            bool walking = g != null && RunUntil(() => g.RouteIndex >= 1 || g.State != TransitGroupState.Marching, 400)
                           && g.State == TransitGroupState.Marching && g.RouteState == WorldTransitSystem.RouteFollowing && g.RouteIndex < g.RouteX.Length;
            int walked0 = g?.RouteIndex ?? 0;
            int firstX = g != null && g.RouteX.Length > 0 ? g.RouteX[0] : int.MinValue;
            int firstY = g != null && g.RouteY.Length > 0 ? g.RouteY[0] : int.MinValue;
            GridCell here = g != null ? NavService.CellOf(g.PosX, g.PosY) : default;
            if (walking)
            {
                g.RouteState = WorldTransitSystem.RouteNeed; // 测试捷径：等同 InvalidateRoutes 发现剩余路线被新建筑挡住（放建筑触发它的正式路径见 FgNavSelfCheck）
            }
            WorldSimulation.StepMany(10);
            // 原路 = 已走过的路点 + 重新规划那一刻所在的格（正好停在路点上时与它合并，相邻重复只记一次）。
            int tl = g?.TrailX.Length ?? 0;
            bool trail = walking && tl >= walked0 && g.TrailX[0] == firstX && g.TrailY[0] == firstY && g.TrailX[tl - 1] == here.X && g.TrailY[tl - 1] == here.Y
                         && g.RouteState != WorldTransitSystem.RouteNeed;
            int[] tx = g?.TrailX.ToArray() ?? Array.Empty<int>();
            int[] ty = g?.TrailY.ToArray() ?? Array.Empty<int>();
            bool arrived = trail && RunUntil(() => g.State != TransitGroupState.Marching, 1500);
            bool retreating = arrived && RunUntil(() => g.State == TransitGroupState.Retreating, 200);
            int n = g?.RouteX.Length ?? 0;
            GridCell o = g != null ? NavService.CellOf(g.OriginX, g.OriginY) : default;
            bool path = retreating && tx.Length > 0 && n >= tx.Length + 1 && g.RouteX[n - 1] == o.X && g.RouteY[n - 1] == o.Y
                        && Enumerable.Range(0, tx.Length).All(k => g.RouteX[n - 2 - k] == tx[k] && g.RouteY[n - 2 - k] == ty[k]);
            bool home = path && RunUntil(() => WorldTransitSystem.Find(s, p.GroupId) == null, 2000);
            Expect(trail && path && home, $"B9 行军途中路线失效、从当前位置重新要路线：之前走过的 {walked0} 个路点和重新规划那一点记进原路（{tx.Length} 个点）；" +
                                          $"到达后撤退沿“当前路线已走部分 → 原路 → 出发格”倒着走（{n} 个路点），回到出发地离场（不走直线穿地形）");
        }

        // ── C 预算与编成 ──────────────────────────────────────────────────────────

        private static void CheckBudgetAndComposition()
        {
            CampaignState s = NewWorld(6121);
            PassGrace(s);
            // C1：剧情突袭（不受第一次突袭规则）3 级：预算 = 500 × 幕 × 难度 × 间隔；编成合法。第二幕 ×1.6。
            RaidDirectorService.RequestStoryRaid(s, "foundry", 3);
            WorldSimulation.StepMany(2);
            RaidPlanRecord a = LatestPlan(s);
            double coef = RaidDirectorService.IntervalCoef(D(s), a.CreatedTick);
            bool b1 = a.Exempt && !a.FirstRaid && a.Level == 3 && a.Budget == (int)Math.Round(500 * coef) && CompositionValid(a, out string w1);
            CompositionValid(a, out w1);
            s.Progress.Act = 2;
            RaidDirectorService.RequestStoryRaid(s, "foundry", 3);
            WorldSimulation.StepMany(2);
            RaidPlanRecord b = LatestPlan(s);
            bool b2 = b != a && b.Budget == (int)Math.Round(500 * 1.6 * RaidDirectorService.IntervalCoef(D(s), b.CreatedTick));
            s.Progress.Act = 1;
            Expect(b1 && b2, $"C1 预算（FG16 第 5 节）：3 级 = 500 × 幕 1 × 难度 1 × 间隔 {coef:F2} = {a.Budget}；第二幕 ×1.6 = {b.Budget}；编成 {w1}");

            // C2：间隔系数——距上次突袭越久越大，封顶 +50%。
            var probe = new RaidDirectorState { LastAnyArrivalTick = 0 };
            double c0 = RaidDirectorService.IntervalCoef(probe, Day(1));
            double c3 = RaidDirectorService.IntervalCoef(probe, Day(3));
            double c20 = RaidDirectorService.IntervalCoef(probe, Day(20));
            Expect(Math.Abs(c0 - 1) < 1e-9 && Math.Abs(c3 - 1.2) < 1e-6 && Math.Abs(c20 - 1.5) < 1e-6, $"C2 间隔系数：隔 1 天 ×{c0:F2}、3 天 ×{c3:F2}、20 天 ×{c20:F2}（每多 1 天 +0.1，封顶 +0.5）");

            // C3：单位数上限 200——超出的预算把普通单位换成精英（后日谈大预算）。
            s.Progress.IsPostgame = true;
            RaidDirectorService.RequestStoryRaid(s, "silent", 4);
            WorldSimulation.StepMany(2);
            RaidPlanRecord pg = LatestPlan(s);
            int elites = pg.EliteCounts.Sum();
            bool cap = pg.PostgameIndex == 0 && pg.Budget == (int)Math.Round(RaidCatalog.PostgameBase) && pg.UnitTotal <= RaidCatalog.MaxUnits && CompositionValid(pg, out string w3);
            CompositionValid(pg, out w3);
            RaidDirectorService.RequestStoryRaid(s, "silent", 4);
            WorldSimulation.StepMany(2);
            RaidPlanRecord pg2 = LatestPlan(s);
            bool growth = pg2.PostgameIndex == 1 && pg2.Budget == (int)Math.Round(RaidCatalog.PostgameBase * RaidCatalog.PostgameGrowth);
            s.Progress.IsPostgame = false;
            // 人为放大预算验证“到上限换精英”（同一套编成函数）：
            pg.Budget = 20000;
            RaidDirectorService.Compose(s, pg);
            bool elite = pg.UnitTotal == RaidCatalog.MaxUnits && pg.EliteCounts.Sum() > 0 && PointsOf(pg) <= pg.Budget;
            Expect(cap && growth && elite, $"C3 后日谈预算 1200 × 1.15ⁿ（第 0 次 {RaidCatalog.PostgameBase:F0}、第 1 次 {pg2.Budget}）；编成 {w3}；" +
                                           $"预算远超时单位数停在上限 {pg.UnitTotal}、多出的预算换成精英 {pg.EliteCounts.Sum()} 台（FG16 第 5 节）");

            // C4：确定性——同一世界种子、同一计划序号 → 同一编成；不同序号一般不同。
            var copy = new RaidPlanRecord { Serial = a.Serial, Faction = a.Faction, Level = a.Level, Budget = a.Budget, Counter = a.Counter };
            RaidDirectorService.Compose(s, copy);
            Expect(copy.UnitIds.SequenceEqual(a.UnitIds) && copy.UnitCounts.SequenceEqual(a.UnitCounts), "C4 编成按（世界种子, 计划序号, 第几次抽取）派生：同一计划重算结果逐字段相同（存读档 / 观察不改变编成）");
        }

        // ── C5 阵营内反制 ─────────────────────────────────────────────────────────

        private static string LayTrap(CampaignState s, string firmware, string key)
        {
            GridCell? at = F.FindFree(s, DefenseCatalog.TrapTypeId, 6f, 30f);
            if (!at.HasValue)
            {
                return null;
            }
            BuildingRecord b = F.Built(s, DefenseCatalog.TrapTypeId, key, at.Value);
            if (!MechanicalContentUnlock.IsUnlocked(s, firmware))
            {
                s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(firmware).ToArray(); // 测试捷径：这枚固件已获得
            }
            return DefenseService.TrySetTrapFirmware(s, b.BuildingId, firmware).Ok ? b.BuildingId : null;
        }

        private static void CheckCounter()
        {
            // 防线主要用流体改道（油膜）：铸造的对策 = 耐热外壳（带 heat_resistant 标签的护甲机权重 ×3）。
            CampaignState s = NewWorld(6131);
            PassGrace(s);
            string fw = "fw_oilleak";
            FirmwareCategory cat = GameLogic.Campaign.Signal.FirmwareKinds.CategoryOf(fw);
            string t1 = LayTrap(s, fw, "trap1");
            string t2 = LayTrap(s, fw, "trap2");
            var counts = new int[4];
            string top = RaidDirectorService.TopDefenseCategory(s, counts);
            RaidDirectorService.RequestStoryRaid(s, "foundry", 3);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p = LatestPlan(s);
            GameConfig.fg.RaidCounter c = RaidCatalog.CounterById(p.Counter);
            int tagged = Tagged(p, c?.Tag);
            // 对照：同一种子、没有陷阱的世界，同样的剧情突袭。
            CampaignState n = NewWorld(6131);
            PassGrace(n);
            RaidDirectorService.RequestStoryRaid(n, "foundry", 3);
            WorldSimulation.StepMany(2);
            RaidPlanRecord q = LatestPlan(n);
            int control = Tagged(q, c?.Tag);
            bool ok = t1 != null && t2 != null && top == "fluid" && cat == FirmwareCategory.Fluid && counts[2] == 2 && c != null && c.Faction == "foundry" && c.Category == "fluid"
                      && p.CounterCategory == "fluid" && string.IsNullOrEmpty(q.Counter) && tagged > control && RaidDirectorService.CounterText(p).Contains(GameText.Get(c.NameKey));
            Expect(ok, $"C5 阵营内反制（FGR-DEF-022）：防线两座陷阱装流体类固件 → 用得最多的类别 {top}，铸造的对策“{(c != null ? GameText.Get(c.NameKey) : "?")}”（只在铸造自己的编成表里选）；" +
                       $"带对策标签的单位 {tagged} 台 > 对照世界 {control} 台；对照世界没有反制；文字“{RaidDirectorService.CounterText(p)}”");
        }

        private static int Tagged(RaidPlanRecord p, string tag)
        {
            int n = 0;
            for (int i = 0; i < p.UnitIds.Length; i++)
            {
                if (RaidCatalog.TryGetUnit(p.UnitIds[i], out RaidUnitDef u) && u.HasTag(tag))
                {
                    n += p.UnitCounts[i];
                }
            }
            return n;
        }

        // ── D FGT-DEF-004：监听站提前预报与实际一致 ─────────────────────────────────────

        private static List<string> AddPosts(CampaignState s, int n)
        {
            var ids = new List<string>();
            F.PowerUp(s);
            for (int i = 0; i < n; i++)
            {
                GridCell? at = F.FindFree(s, IntelCatalog.TypeId, 6f, 40f);
                if (!at.HasValue)
                {
                    break;
                }
                ids.Add(F.Built(s, IntelCatalog.TypeId, "rpost" + i, at.Value).BuildingId);
            }
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
            return ids;
        }

        private static void CheckIntelForecast()
        {
            CampaignState s = NewWorld(6141);
            List<string> posts = AddPosts(s, 1);
            PassGrace(s);
            // 先用掉“第一次突袭”的特例：一波剧情突袭不计；这里直接用普通触发（第一次 1 级也一样要预报）。
            Raise(s, CampaignExposureLedger.FactionFoundry, 31f);
            RaidPlanRecord p = LatestPlan(s);
            bool sched = RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 120);
            // 出发前预报：可预报（离发预警不超过 1 天）→ 破译出 Subject = 计划 ID 的突袭预报，产出时刻早于预警、晚于预警前 1 天。
            bool got = RunUntil(() => IntelService.StateOf(s).Records.Any(r => r.Kind == IntelCatalog.KindRaid && r.Subject == p.PlanId), 200);
            IntelRecord f = IntelService.StateOf(s).Records.FirstOrDefault(r => r.Kind == IntelCatalog.KindRaid && r.Subject == p.PlanId);
            bool early = got && f != null && f.ProducedTick < p.WarnTick && f.ProducedTick >= p.WarnTick - Day(RaidCatalog.IntelLeadDays) && p.State == RaidDirectorService.StateScheduled
                         && f.Faction == p.Faction && f.Units == p.UnitTotal && f.Counter == p.Counter && f.Level == p.Level
                         && IntelService.Summary(s, f, GameClock.Ticks).Contains(WorldTransitSystem.OriginName(p.Faction));
            Expect(posts.Count == 1 && sched && early,
                $"D3 有监听站：部队出发前 {(p.WarnTick - (f?.ProducedTick ?? 0)) / (double)GameClock.StepHz:F0} 游戏秒破译出预报（最早提前 1 游戏日），附带阵营 / 规模 {f?.Units} / 方向 / 针对什么：“{Short(f != null ? IntelService.Summary(s, f, GameClock.Ticks) : null)}”");

            // 出发后沿用同一条（不再为队伍另出一条）；到达 → 已过时（部队已经到达）；与实际一致：阵营、规模、方向、时间窗口。
            int raidRecords0 = IntelService.StateOf(s).Records.Count(r => r.Kind == IntelCatalog.KindRaid);
            bool departed = AdvanceToDeparture(s, p);
            TransitGroupRecord g = WorldTransitSystem.Find(s, p.GroupId);
            WorldSimulation.StepMany(GameClock.StepHz * 60);
            bool reuse = departed && g != null && IntelService.RaidSubjectOf(g) == p.PlanId && IntelService.IsValid(f, GameClock.Ticks)
                         && IntelService.StateOf(s).Records.Count(r => r.Kind == IntelCatalog.KindRaid) == raidRecords0 && IntelService.NextRaidSubject(s, GameClock.Ticks) == null;
            bool arrived = g != null && RunUntil(() => g.State != TransitGroupState.Marching, 600);
            GridCell core = HomeGridService.CorePivot(s);
            bool window = arrived && g.ArrivedAtTick >= f.WindowFromTick && g.ArrivedAtTick <= f.WindowToTick;
            bool same = f != null && g != null && f.Faction == g.OriginId && f.Units == g.UnitCount && IntelService.Octant(g.PosX - core.X, g.PosY - core.Y) == IntelService.Octant(f.DirX, f.DirY);
            WorldSimulation.StepMany(6);
            bool outdated = f != null && f.Outdated && f.OutdatedReason == IntelService.ReasonArrived;
            Expect(reuse && window && same && outdated,
                $"D4 FGT-DEF-004 预报与实际一致：出发后沿用同一条（Subject = {p.PlanId}，没有重复预报）；实际到达 {g?.ArrivedAtTick} 在窗口 {f?.WindowFromTick}～{f?.WindowToTick} 里；" +
                $"阵营 {f?.Faction} / {g?.OriginId}、规模 {f?.Units} / {g?.UnitCount}、方向 {IntelService.DirectionName(f?.DirX ?? 0, f?.DirY ?? 0)} 一致；到达后标已过时（{f?.OutdatedReason}）");

            // D5：“最早提前 1 个游戏日”——离发预警超过 1 天的计划不可预报（不进破译目标），1 天以内可预报；还在筹备（路线没好）、已经出发的不按计划预报。
            long now = GameClock.Ticks;
            var far = new RaidPlanRecord { PlanId = "raid-probe-far", State = RaidDirectorService.StateScheduled, WarnTick = now + Day(1.5) };
            var near = new RaidPlanRecord { PlanId = "raid-probe-near", State = RaidDirectorService.StateScheduled, WarnTick = now + Day(0.9) };
            var planning = new RaidPlanRecord { PlanId = "raid-probe-plan", State = RaidDirectorService.StatePlanning, WarnTick = -1 };
            bool lead = !RaidDirectorService.Forecastable(far, now) && RaidDirectorService.Forecastable(near, now) && !RaidDirectorService.Forecastable(planning, now)
                        && RaidDirectorService.Forecastable(far, far.WarnTick - Day(1)) && !RaidDirectorService.Forecastable(far, far.WarnTick - Day(1) - 1);
            Expect(lead, "D5 监听站最早提前 1 个游戏日：离发预警 1.5 天的计划不可预报、0.9 天的可以；正好到预警前 1 天那一步起可预报；还在筹备（没有路线）的不预报");

            // D6：没有监听站：预警照样发（倒计时、方向），编成未知。
            CampaignState n = NewWorld(6142);
            PassGrace(n);
            RaidDirectorService.RequestStoryRaid(n, "silent", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord q = LatestPlan(n);
            bool warned = AdvanceToDeparture(n, q) || q.State >= RaidDirectorService.StateWarned;
            RaidWaveView v = RaidDirectorService.IncomingWaves(n, GameClock.Ticks).FirstOrDefault();
            string row = v.Lead != null ? RaidDirectorService.WaveRowText(n, v, GameClock.Ticks) : string.Empty;
            Expect(warned && !v.IntelKnown && row.Contains(GameText.Get("raid.warning.comp_unknown")) && row.Contains(RaidDirectorService.DirectionText(q)),
                $"D6 没有监听站：照样发预警（倒计时与方向），阵营 / 编成未知——预警条写“{Short(row.Replace('\n', ' '))}”");
        }

        // ── D7～D9 复审 P1（FGT-DEF-004）：预报之后计划变了 → 旧预报当场作废（计划有变），重新破译出的新预报与最终的队伍一致 ───────────────

        private static IntelRecord ValidForecast(CampaignState s, string planId) =>
            IntelService.StateOf(s).Records.FirstOrDefault(r => r != null && r.Kind == IntelCatalog.KindRaid && r.Subject == planId && IntelService.IsValid(r, GameClock.Ticks));

        /// <summary>预报与实际到达一致：规模 = 队伍人数、实际到达落在时间窗口里、来袭方向（从目标看到达点）与预报同一个八分区。计划要已出发。</summary>
        private static bool MatchesArrival(CampaignState s, RaidPlanRecord p, IntelRecord f, out string why)
        {
            TransitGroupRecord g = WorldTransitSystem.Find(s, p.GroupId);
            bool arrived = g != null && RunUntil(() => g.State != TransitGroupState.Marching, 1500);
            bool ok = arrived && f != null && f.Units == g.UnitCount && g.ArrivedAtTick >= f.WindowFromTick && g.ArrivedAtTick <= f.WindowToTick
                      && IntelService.Octant(g.PosX - p.TargetX, g.PosY - p.TargetY) == IntelService.Octant(f.DirX, f.DirY);
            why = $"规模 {f?.Units} / 队伍 {g?.UnitCount}，实际到达 {g?.ArrivedAtTick} 在窗口 {f?.WindowFromTick}～{f?.WindowToTick}，方向 预报{IntelService.DirectionName(f?.DirX ?? 0, f?.DirY ?? 0)} / 实际" +
                  (g != null ? IntelService.DirectionName((float)(g.PosX - p.TargetX), (float)(g.PosY - p.TargetY)) : "?");
            return ok;
        }

        private static void CheckIntelFollowsPlan()
        {
            // D7 升级：已排定、已有出发前预报的普通突袭，出发前又越过更高的阈值 → 等级与编成重算（合并进同一个计划），旧预报当场作废（计划有变）；
            // 监听站重新破译，新预报的等级、规模、方向、时间窗口与最终出发的队伍一致；作废期间预警条 / 地图不再把旧规模当真。
            CampaignState s = NewWorld(6143);
            List<string> posts = AddPosts(s, 1);
            PassGrace(s);
            D(s).RaidCount = 1; // 测试捷径：读一个已经来过第一次突袭的存档（第一次突袭固定 1 级、不升级，见 B2）
            Raise(s, CampaignExposureLedger.FactionFoundry, 31f);
            RaidPlanRecord p = LatestPlan(s);
            bool got1 = p != null && !p.FirstRaid && RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 120) && RunUntil(() => ValidForecast(s, p.PlanId) != null, 200);
            IntelRecord f1 = got1 ? ValidForecast(s, p.PlanId) : null;
            bool first = f1 != null && f1.Level == 1 && f1.Units == p.UnitTotal && f1.PlanRevision == p.Revision && p.State == RaidDirectorService.StateScheduled;
            int units1 = f1?.Units ?? -1;
            Raise(s, CampaignExposureLedger.FactionFoundry, 61f);
            bool upgraded = p != null && p.Level == 2 && p.State < RaidDirectorService.StateWarned && Plans(s).Length == 1;
            bool voided = f1 != null && f1.Outdated && f1.OutdatedReason == IntelService.ReasonChanged && p != null && !RaidDirectorService.IntelKnown(s, p, GameClock.Ticks)
                          && IntelService.NextRaidSubject(s, GameClock.Ticks) == p.PlanId;
            bool got2 = upgraded && RunUntil(() => ValidForecast(s, p.PlanId) != null, 200);
            IntelRecord f2 = got2 ? ValidForecast(s, p.PlanId) : null;
            bool fresh = f2 != null && f2 != f1 && f2.Level == 2 && f2.Units == p.UnitTotal && f2.PlanRevision == p.Revision && f2.ArriveX == p.ArriveX;
            string why7 = string.Empty;
            bool match7 = fresh && AdvanceToDeparture(s, p) && IntelService.IsValid(f2, GameClock.Ticks) && MatchesArrival(s, p, f2, out why7);
            Expect(posts.Count == 1 && first && upgraded && voided && fresh && match7,
                $"D7 预报（1 级、{units1} 台）之后越过 60：计划升到 {p?.Level} 级、重新编成 {p?.UnitTotal} 台，旧预报当场标已过时（{f1?.OutdatedReason}：{(f1 != null && f1.Outdated ? GameText.Get("intel.outdated." + f1.OutdatedReason) : "?")}），" +
                $"预警条不再显示旧编成；重新破译出 {f2?.Level} 级、{f2?.Units} 台的新预报；出发后与实际一致：{why7}");

            // D8 合并吸收：剧情突袭已有预报，之后同时到来的普通突袭被它吸收（预算相加、重新编成）→ 剧情一方的旧预报作废，新预报与合并后的队伍一致。
            CampaignState t = NewWorld(6144);
            AddPosts(t, 1);
            PassGrace(t);
            RaidDirectorService.RequestStoryRaid(t, "foundry", 2);
            WorldSimulation.StepMany(2);
            RaidPlanRecord story = LatestPlan(t);
            bool gotS = story != null && RunUntil(() => story.State >= RaidDirectorService.StateScheduled, 120) && RunUntil(() => ValidForecast(t, story.PlanId) != null, 200);
            IntelRecord fs1 = gotS ? ValidForecast(t, story.PlanId) : null;
            int budget0 = story?.Budget ?? 0;
            int unitsS1 = fs1?.Units ?? -1;
            Raise(t, CampaignExposureLedger.FactionFoundry, 31f);
            RaidPlanRecord normal = LatestPlan(t);
            bool folded = gotS && normal != story && RunUntil(() => normal.State >= RaidDirectorService.StateScheduled, 200) && normal.State == RaidDirectorService.StateMerged
                          && normal.MergedInto == story.PlanId && story.Budget == budget0 + normal.Budget;
            bool voidS = fs1 != null && fs1.Outdated && fs1.OutdatedReason == IntelService.ReasonChanged;
            bool gotS2 = folded && RunUntil(() => ValidForecast(t, story.PlanId) != null, 200);
            IntelRecord fs2 = gotS2 ? ValidForecast(t, story.PlanId) : null;
            bool freshS = fs2 != null && fs2.Units == story.UnitTotal && fs2.PlanRevision == story.Revision;
            string why8 = string.Empty;
            bool match8 = freshS && AdvanceToDeparture(t, story) && IntelService.IsValid(fs2, GameClock.Ticks) && MatchesArrival(t, story, fs2, out why8);
            Expect(folded && voidS && freshS && match8,
                $"D8 剧情突袭预报（{unitsS1} 台）之后吸收同时到来的普通突袭（预算 {budget0} + {normal?.Budget} = {story?.Budget}）：旧预报作废（{fs1?.OutdatedReason}），" +
                $"新预报 {fs2?.Units} 台 = 合并后的编成 {story?.UnitTotal} 台；出发后与实际一致：{why8}");

            // D9 出发前改道（FG-GAP-108）：预报之后出发据点被摧毁 → 出发那一步改道，旧预报（旧方向、旧窗口）作废；重新排定后新预报的方向、窗口与实际一致。
            CampaignState u = NewWorld(6151);
            AddPosts(u, 1);
            PassGrace(u);
            RaidDirectorService.RequestStoryRaid(u, "silent", 2);
            WorldSimulation.StepMany(2);
            RaidPlanRecord q = LatestPlan(u);
            bool gotQ = q != null && RunUntil(() => q.State >= RaidDirectorService.StateScheduled, 120) && q.OriginKind == RaidDirectorService.OriginOutpost
                        && RunUntil(() => ValidForecast(u, q.PlanId) != null, 200);
            IntelRecord fq1 = gotQ ? ValidForecast(u, q.PlanId) : null;
            string origin1 = q?.OriginId;
            string dir1 = fq1 != null ? IntelService.DirectionName(fq1.DirX, fq1.DirY) : "?";
            bool rer = gotQ && WorldOutpostSystem.MarkDestroyed(u, q.OriginId) && SkipIdle(u, q.WarnTick - 2) && RunUntil(() => q.Reroutes > 0, 300);
            bool voidQ = rer && fq1.Outdated && fq1.OutdatedReason == IntelService.ReasonChanged;
            bool gotQ2 = voidQ && RunUntil(() => ValidForecast(u, q.PlanId) != null, 400);
            IntelRecord fq2 = gotQ2 ? ValidForecast(u, q.PlanId) : null;
            bool freshQ = fq2 != null && fq2.PlanRevision == q.Revision && q.OriginId != origin1;
            string why9 = string.Empty;
            bool match9 = freshQ && RunUntil(() => q.State >= RaidDirectorService.StateDeparted, 600) && MatchesArrival(u, q, fq2, out why9);
            Expect(rer && voidQ && freshQ && match9,
                $"D9 预报（方向 {dir1}）之后出发据点 {origin1} 被摧毁：出发时改道到 {q?.OriginKind}:{q?.OriginId}，旧预报作废（{fq1?.OutdatedReason}）；" +
                $"重新排定后新预报方向 {(fq2 != null ? IntelService.DirectionName(fq2.DirX, fq2.DirY) : "?")}；与实际一致：{why9}");
        }

        // ── E 出发地 ────────────────────────────────────────────────────────────

        private static OutpostRecord Nearest(CampaignState s, string faction, int tx, int ty)
        {
            OutpostRecord best = null;
            double bestD = double.MaxValue;
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(s))
            {
                if (o == null || o.Destroyed || RaidDirectorService.FactionOfOutpost(s, o) != faction)
                {
                    continue;
                }
                double d = Math.Sqrt((o.CellX - tx) * (double)(o.CellX - tx) + (o.CellY - ty) * (double)(o.CellY - ty));
                if (d <= RaidCatalog.OriginMaxDistance && (d < bestD - 1e-9 || (Math.Abs(d - bestD) <= 1e-9 && string.CompareOrdinal(o.OutpostId, best.OutpostId) < 0)))
                {
                    best = o;
                    bestD = d;
                }
            }
            return best;
        }

        private static void CheckOrigins()
        {
            var lines = new List<string>();
            bool all = true;
            foreach ((int seed, string faction, bool needNest) in new[] { (6151, "silent", true), (6152, "silent", true), (6156, "foundry", false) })
            {
                CampaignState s = NewWorld(seed);
                PassGrace(s);
                RaidDirectorService.RequestStoryRaid(s, faction, 2);
                WorldSimulation.StepMany(2);
                RaidPlanRecord p = LatestPlan(s);
                OutpostRecord want = Nearest(s, faction, p.TargetX, p.TargetY);
                Line($"  - 种子 {seed}：据点 {WorldOutpostSystem.Outposts(s).Count} 个（归属 {string.Join("，", WorldOutpostSystem.Outposts(s).GroupBy(o => RaidDirectorService.FactionOfOutpost(s, o) ?? "无").Select(gr => gr.Key + "×" + gr.Count()))}），" +
                     $"世界 v{s.World.GeneratorVersion}，侦察巢已登记 {s.World.HomeOutpostsSeeded}");
                bool nearest = (want != null || !needNest)
                               && (want != null ? p.OriginKind == RaidDirectorService.OriginOutpost && p.OriginId == want.OutpostId : p.OriginKind == RaidDirectorService.OriginFog);
                // 出发前据点被摧毁 → 出发那一步改道：重新选出发地（下一个最近的活跃据点 / 迷雾外）、重新寻路与排定、按新方向再发预警，最短预警照样保证；该方向记一次。
                bool scheduled = RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 120);
                int warn0 = NotifyCount("raid_warning");
                string firstOrigin = p.OriginId;
                bool warned = scheduled;
                bool rerouted = true;
                if (p.OriginKind == RaidDirectorService.OriginOutpost)
                {
                    GridCell core = HomeGridService.CorePivot(s);
                    OutpostRecord o = WorldOutpostSystem.Find(s, p.OriginId);
                    int coreOct = IntelService.Octant(o.CellX - core.X, o.CellY - core.Y);
                    WorldOutpostSystem.MarkDestroyed(s, p.OriginId);
                    bool skipped = SkipIdle(s, p.WarnTick - 2);
                    bool depart = skipped && RunUntil(() => p.Reroutes > 0, 300);
                    bool again = depart && RunUntil(() => p.State >= RaidDirectorService.StateWarned, 200);
                    OutpostRecord next = Nearest(s, faction, p.TargetX, p.TargetY);
                    rerouted = depart && again && p.OriginId != firstOrigin && (next != null ? p.OriginId == next.OutpostId : p.OriginKind == RaidDirectorService.OriginFog)
                               && NotifyCount("raid_warning") > warn0 && LastNotify("raid_warning").Contains(RaidDirectorService.DirectionText(p))
                               && p.ArrivalTick - p.WarnTick >= RaidDirectorService.MinWarningTicks(s)
                               && D(s).DestroyedByOctant[coreOct] == 1 && WorldOutpostSystem.Find(s, firstOrigin).Destroyed;
                }
                else
                {
                    warned = SkipIdle(s, p.WarnTick - 2) && RunUntil(() => p.State >= RaidDirectorService.StateWarned, 10);
                }
                bool ok = nearest && warned && rerouted;
                all &= ok;
                lines.Add($"种子 {seed} / {faction}：出发地 {firstOrigin}（离目标最近的活跃据点 {want?.OutpostId ?? "无 → 迷雾外"}）；据点被毁后改道到 {p.OriginKind}:{p.OriginId}、再次预警，" +
                          $"最短预警 {p.ArrivalTick - p.WarnTick} 步（{(ok ? "一致" : "不一致")}）");
            }
            Expect(all, "E1 出发地 = 所选阵营离目标最近的活跃据点（家园区侦察巢按最近的本幕领地归属；三个种子，B25；铸造在家园区没有据点时从迷雾外）；出发前据点被摧毁 → 改道、重新寻路、再发一次预警，最短预警照样保证：\n    "
                        + string.Join("\n    ", lines));

            // E2：摧毁某个方向的据点 → 从那个方向出发的普通突袭最短间隔 ×1.5（频率降低）。第一波普通突袭出发后，摧毁同一方向的另一个据点，第二波从同一据点出发时抵达至少隔 1.5 天。
            // 找一个“静默离家园最近的据点所在方向还有第二个据点”的种子（不写死坐标，B25）。
            CampaignState w = FindWorld(6153, 16, x =>
            {
                List<OutpostRecord> nests = OutpostsOf(x, "silent");
                GridCell c0 = HomeGridService.CorePivot(x);
                OutpostRecord n0 = Nearest(x, "silent", c0.X, c0.Y);
                return n0 != null && nests.Count(o => IntelService.Octant(o.CellX - c0.X, o.CellY - c0.Y) == IntelService.Octant(n0.CellX - c0.X, n0.CellY - c0.Y)) >= 2;
            }, out int wSeed);
            if (w == null)
            {
                Fail("E2 16 个种子里找不到“静默最近据点的方向还有第二个据点”的世界");
                return;
            }
            PassGrace(w);
            RaidDirectorState st = D(w);
            GridCell wc = HomeGridService.CorePivot(w);
            Raise(w, CampaignExposureLedger.FactionSilent, 31f);
            RaidPlanRecord r1 = LatestPlan(w);
            bool r1Out = AdvanceToDeparture(w, r1);
            int oc = r1.Octant;
            int before = st.DestroyedByOctant[oc];
            OutpostRecord victim = WorldOutpostSystem.Outposts(w).FirstOrDefault(o => o != null && !o.Destroyed && o.OutpostId != r1.OriginId
                && IntelService.Octant(o.CellX - wc.X, o.CellY - wc.Y) == oc);
            bool marked = victim != null && WorldOutpostSystem.MarkDestroyed(w, victim.OutpostId) && st.DestroyedByOctant[oc] == before + 1
                          && !WorldOutpostSystem.MarkDestroyed(w, victim.OutpostId) && victim.Destroyed && victim.Garrison == 0;
            Raise(w, CampaignExposureLedger.FactionSilent, 61f);
            RaidPlanRecord r2 = LatestPlan(w);
            bool sched2 = r2 != r1 && RunUntil(() => r2.State >= RaidDirectorService.StateScheduled, 200);
            double mul = 1 + RaidCatalog.DestroyedOutpostIntervalBonus * st.DestroyedByOctant[r2.Octant];
            bool slower = r1Out && marked && sched2 && Math.Abs(r2.IntervalMul - mul) < 1e-4 && r2.ArrivalTick >= r2.IntervalBaseTick + (long)Math.Round(Day(1) * mul)
                          && (r2.Octant != oc || r2.IntervalMul >= 1.5f - 1e-4);
            Expect(slower && r1.OriginKind == RaidDirectorService.OriginOutpost && r2.OriginId == r1.OriginId && Math.Abs(r2.IntervalMul - 1.5f) < 1e-4,
                $"E2 种子 {wSeed}：第一波从据点 {r1.OriginId}（方向 {oc}）出发后，摧毁同方向的另一个据点（幂等，驻军清零）：那个方向记 {st.DestroyedByOctant[oc]} 次；" +
                $"第二波普通突袭从同一据点出发，最短间隔 ×{r2?.IntervalMul:F1}，抵达 {r2?.ArrivalTick} ≥ 上一波 {r2?.IntervalBaseTick} + 1 天 × {mul:F1}（FGR-GEN-034 摧毁据点降低该方向的突袭频率）");

            // E3：一个合适据点都没有（该阵营的据点全被摧毁）→ 从迷雾外来袭：领地朝目标一侧、未探索处。
            CampaignState z = NewWorld(6154);
            PassGrace(z);
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(z).ToArray())
            {
                if (o != null && RaidDirectorService.FactionOfOutpost(z, o) == "foundry")
                {
                    WorldOutpostSystem.MarkDestroyed(z, o.OutpostId);
                }
            }
            RaidDirectorService.RequestStoryRaid(z, "foundry", 2);
            WorldSimulation.StepMany(2);
            RaidPlanRecord fz = LatestPlan(z);
            PlannedTerritory terr = WorldGenService.PlanFor(z).Find("foundry");
            double dc = Math.Sqrt((fz.OriginX - terr.CenterX) * (double)(fz.OriginX - terr.CenterX) + (fz.OriginY - terr.CenterY) * (double)(fz.OriginY - terr.CenterY));
            bool fog = fz.OriginKind == RaidDirectorService.OriginFog && fz.OriginId == "foundry" && dc <= terr.OuterRadius + 1
                       && !HomeGridService.MapFor(z).IsExploredNoLoad(new GridCell(fz.OriginX, fz.OriginY))
                       && RunUntil(() => fz.State >= RaidDirectorService.StateScheduled, 200) && fz.TravelTicks > 0;
            Expect(fog, $"E3 该阵营没有合适据点 → 从迷雾外来袭：出发点在铸造领地朝目标一侧（离领地中心 {dc:F0} 格 ≤ {terr.OuterRadius}）、未探索；照样沿地形寻路（行进 {fz.TravelTicks} 步）");
        }

        // ── E4 目标：家园或前哨站 ─────────────────────────────────────────────────────

        private static void CheckTargets()
        {
            CampaignState s = NewWorld(6161);
            PassGrace(s);
            GridCell core = HomeGridService.CorePivot(s);
            PlannedTerritory terr = WorldGenService.PlanFor(s).Find("foundry");
            // 离核心 300 格外、朝铸造领地方向放一个 4 座建筑的前哨站（测试捷径：远程施工在 FG8-OUT-01）。
            double dx = terr.CenterX - core.X;
            double dy = terr.CenterY - core.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            var at = new GridCell(core.X + (int)Math.Round(dx / len * 340), core.Y + (int)Math.Round(dy / len * 340));
            // 测试捷径：先探明那一片（正式流程里玩家的远征 / 侦察会探明），再按格网规则找能放的格子（不写死坐标，B25）。
            HomeGridService.RevealArea(s, new Vector2(at.X, at.Y), 40f);
            string type = DefenseCatalog.TrapTypeId;
            int built = 0;
            for (int r = 0; r <= 30 && built < 4; r += 2)
            {
                for (int k = 0; k < 16 && built < 4; k++)
                {
                    double ang = k / 16.0 * Math.PI * 2;
                    var c = new GridCell(at.X + (int)Math.Round(Math.Cos(ang) * r), at.Y + (int)Math.Round(Math.Sin(ang) * r));
                    if (HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        F.Built(s, type, "op" + built, c);
                        built++;
                    }
                }
            }
            if (built < 3)
            {
                GridPlacementResult why = HomeGridService.ValidatePlacement(s, type, at, 0, asPlayerPlacement: false, checkCost: false);
                Line($"  - 前哨站放不下：{string.Join("、", why.Reasons.Select(x => x.Code.ToString()))}");
            }
            int outpostTargets = 0;
            int homeTargets = 0;
            for (int i = 0; i < 40; i++)
            {
                RaidDirectorService.RequestStoryRaid(s, "foundry", 1);
            }
            WorldSimulation.StepMany(2);
            foreach (RaidPlanRecord p in Plans(s))
            {
                if (p.TargetKind == RaidDirectorService.TargetOutpost)
                {
                    outpostTargets++;
                }
                else if (p.TargetKind == RaidDirectorService.TargetHome)
                {
                    homeTargets++;
                }
            }
            RaidPlanRecord op = Plans(s).FirstOrDefault(p => p.TargetKind == RaidDirectorService.TargetOutpost);
            bool near = op != null && Math.Sqrt((op.TargetX - at.X) * (double)(op.TargetX - at.X) + (op.TargetY - at.Y) * (double)(op.TargetY - at.Y)) < 30
                        && RaidDirectorService.TargetText(op).Contains(built.ToString());
            Expect(built >= 3 && outpostTargets > 0 && homeTargets > outpostTargets && near,
                $"E4 目标可以是家园或前哨站：离核心 340 格、靠铸造领地的 {built} 座建筑群被当成前哨站；40 波里 {outpostTargets} 波打前哨站、{homeTargets} 波打家园（家园权重更高），" +
                $"目标文字“{(op != null ? RaidDirectorService.TargetText(op) : "?")}”");
            if (op == null)
            {
                return;
            }

            // E4b 复审 P1：目标为前哨站的队伍到达——到达通知如实写“前哨站附近”（不说家园外围），不启动家园战时预案、不进“核心受威胁”告警；
            // 对照：目标为家园的队伍到达照常启动。两支都走导演出发时用的同一个派出函数（DispatchPlanned，目标种类随计划带上队伍）；
            // 测试捷径：出发点放在目标外 30 格（不等筹备与长途行军），计划记录只用来派出、不进导演。
            var toOutpost = new RaidPlanRecord
            {
                PlanId = "raid-probe-outpost", Faction = "foundry", UnitTotal = 3, OriginKind = RaidDirectorService.OriginFog, OriginId = "foundry",
                TargetKind = op.TargetKind, TargetId = op.TargetId, TargetX = op.TargetX, TargetY = op.TargetY,
                OriginX = op.TargetX + (int)Math.Round(dx / len * 30), OriginY = op.TargetY + (int)Math.Round(dy / len * 30),
            };
            TransitGroupRecord go = WorldTransitSystem.DispatchPlanned(s, toOutpost);
            bool opArrived = go != null && go.TargetKind == RaidDirectorService.TargetOutpost && RunUntil(() => go.State != TransitGroupState.Marching, 120);
            string opText = LastNotify("raid_arrival");
            bool honest = opArrived && opText.Contains("前哨站附近") && !opText.Contains("家园外围");
            bool noWar = opArrived && !StandingRuleService.RaidActive(s) && !HomeValleyAlarms.Collect(s).Any(a => a.Key == "raid:" + go.GroupId);
            var toHome = new RaidPlanRecord
            {
                PlanId = "raid-probe-home", Faction = "foundry", UnitTotal = 3, OriginKind = RaidDirectorService.OriginFog, OriginId = "foundry",
                TargetKind = RaidDirectorService.TargetHome, TargetId = RaidDirectorService.TargetHome, TargetX = core.X, TargetY = core.Y,
                OriginX = core.X + (int)Math.Round(dx / len * 30), OriginY = core.Y + (int)Math.Round(dy / len * 30),
            };
            TransitGroupRecord gh = WorldTransitSystem.DispatchPlanned(s, toHome);
            bool homeArrived = gh != null && RunUntil(() => gh.State != TransitGroupState.Marching, 120);
            string homeText = LastNotify("raid_arrival");
            bool war = homeArrived && StandingRuleService.RaidActive(s) && HomeValleyAlarms.Collect(s).Any(a => a.Key == "raid:" + gh.GroupId) && homeText.Contains("家园外围");
            Expect(honest && noWar && war, $"E4b 目标为前哨站的突袭到达：通知写“{Short(opText)}”，家园战时预案不启动、不进核心告警（{noWar}）；" +
                                           $"对照目标为家园的一支到达：“{Short(homeText)}”、战时预案启动、核心告警出现（{war}）");
        }

        // ── F 负向：重叠合并、剧情不受间隔、难度 ───────────────────────────────────────────

        private static void CheckStoryMerge()
        {
            CampaignState s = NewWorld(6171);
            PassGrace(s);
            Raise(s, CampaignExposureLedger.FactionSilent, 31f);
            RaidPlanRecord normal = LatestPlan(s);
            RaidDirectorService.RequestStoryRaid(s, "foundry", 2);
            WorldSimulation.StepMany(2);
            RaidPlanRecord story = LatestPlan(s);
            int normalBudget = normal.Budget;
            int storyBudget = story.Budget;
            bool both = RunUntil(() => normal.State >= RaidDirectorService.StateScheduled && story.State >= RaidDirectorService.StateScheduled, 200);
            bool merged = both && normal.State == RaidDirectorService.StateMerged && normal.MergedInto == story.PlanId && story.Budget == normalBudget + storyBudget
                          && story.Triggers.Contains(RaidCatalog.TriggerExposure) && story.Triggers.Contains(RaidCatalog.TriggerStory);
            WorldSimulation.StepMany(3);
            bool cleaned = RaidDirectorService.FindPlan(s, normal.PlanId) == null && RaidDirectorService.History(s).Any(h => h.PlanId == normal.PlanId && h.EndReason == RaidDirectorService.EndMerged);
            bool departed = AdvanceToDeparture(s, story);
            IReadOnlyList<RaidWaveView> waves = RaidDirectorService.IncomingWaves(s, GameClock.Ticks);
            bool oneWave = departed && waves.Count == 1 && waves[0].Lead == story && WorldTransitSystem.Groups(s).Count(g => g != null) == 1;
            Expect(merged && cleaned && oneWave,
                $"F1 负向“剧情突袭与普通突袭时间重叠”：剧情一方吸收普通一方（预算 {storyBudget} + {normalBudget} = {story.Budget}，触发 {string.Join("+", story.Triggers)}），" +
                $"普通计划标“已合并”进历史；预警条与行进队伍都只有一波（{waves.Count} 波）");

            // F2：剧情突袭不受最短间隔——上一波刚出发就能再来一波剧情突袭，抵达不被推到 1 天后。
            RaidDirectorService.RequestStoryRaid(s, "silent", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord again = LatestPlan(s);
            bool exempt = RunUntil(() => again.State >= RaidDirectorService.StateScheduled, 200) && again.Exempt && again.ArrivalTick < story.ArrivalTick + Day(1);
            Expect(exempt, $"F2 剧情突袭不受最短间隔：上一波计划 {story.ArrivalTick} 抵达，新的剧情突袭 {again.ArrivalTick} 抵达（相隔 {(again.ArrivalTick - story.ArrivalTick) / (double)Day(1):F2} 天 < 1 天）");

            // F3：剧情规则——击败铸造主核心 → 铸造报复（只一次）；旧档里早就击败的不追溯。
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            RegionRecord r = FoundryOutpostRegion.Find(s);
            r.CoreState = CoreBossState.Destroyed.ToString();
            WorldSimulation.StepMany(GameClock.StepHz + 2);
            RaidPlanRecord rev = LatestPlan(s);
            bool story1 = rev != null && rev.StoryId == "foundry_core_retaliation" && rev.Faction == "foundry" && rev.Level == 2 && D(s).StoriesFired.Contains("foundry_core_retaliation");
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool onlyOnce = Plans(s).Count(p => p.StoryId == "foundry_core_retaliation") == 1;
            CampaignState o = NewWorld(6172);
            FoundryOutpostRegion.EnsureRegionRecordSeeded(o);
            FoundryOutpostRegion.Find(o).CoreState = CoreBossState.Destroyed.ToString();
            o.Raids.Director = new RaidDirectorState(); // 旧档：读进来时还没有导演域
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool noRetro = Plans(o).Length == 0 && D(o).StoriesFired.Contains("foundry_core_retaliation");
            Expect(story1 && onlyOnce && noRetro, "F3 剧情节点：击败铸造主核心 → 铸造发动 2 级报复突袭（只一次）；旧档里早就击败的首领在第一次接上导演时记为已触发，不在读档后突然来一波");
        }

        private static void CheckDepartedStoryOverlap()
        {
            // F6 复审 P2：剧情突袭已经出发后，又排定一波时间重叠、但比它晚到的普通突袭——普通突袭不能提前，所以不并成一波：各自一行、各自倒计时，
            // 先到的剧情一支不会把整行写成“已到达”、盖住普通一方的倒计时（ResolveOverlaps 只在普通一方能推迟到同时抵达时才并波）。
            CampaignState s = NewWorld(6173);
            PassGrace(s);
            RaidDirectorService.RequestStoryRaid(s, "foundry", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord story = LatestPlan(s);
            bool departed = story != null && AdvanceToDeparture(s, story);
            Raise(s, CampaignExposureLedger.FactionFoundry, 31f);
            RaidPlanRecord normal = LatestPlan(s);
            bool sched = departed && normal != null && normal != story && RunUntil(() => normal.State >= RaidDirectorService.StateScheduled, 200);
            long arrival0 = normal?.ArrivalTick ?? -1;
            bool overlap = sched && normal.ArrivalTick > story.ArrivalTick && normal.ArrivalTick - story.ArrivalTick <= GameClock.TicksFor(RaidCatalog.MergeWindowSeconds);
            bool separate = overlap && normal.Wave != story.Wave && normal.State == RaidDirectorService.StateScheduled;
            bool warned = separate && RunUntil(() => normal.State >= RaidDirectorService.StateWarned, 900);
            RaidWaveView wn = warned ? RaidDirectorService.IncomingWaves(s, GameClock.Ticks).FirstOrDefault(w => w.Lead == normal) : default;
            bool own = wn.Lead == normal && wn.Forces == 1 && !wn.Arrived && normal.ArrivalTick == arrival0;
            Expect(separate && own, $"F6 剧情突袭已出发（计划抵达 {story?.ArrivalTick}）后排定的普通突袭（抵达 {normal?.ArrivalTick}，相差 {((normal?.ArrivalTick ?? 0) - (story?.ArrivalTick ?? 0)) / (double)GameClock.StepHz:F0} 游戏秒，" +
                                    $"在合并窗口内）比它晚到：不并波（波次 {story?.Wave} / {normal?.Wave}），抵达不被改动；预警后普通一方单独一行、自己的倒计时（{wn.Forces} 支、已到达 {wn.Arrived}）");
        }

        private static void CheckDifficulty()
        {
            // F4：建造者只有剧情突袭（暴露阈值、骚扰都跳过），剧情突袭规模 ×0.5、最短预警 ×1.5。
            CampaignState b = NewWorld(6181, difficulty: "Builder");
            PassGrace(b);
            Raise(b, CampaignExposureLedger.FactionFoundry, 95f);
            bool none = Plans(b).Length == 0 && D(b).TriggersSkipped >= 1;
            RaidDirectorService.RequestStoryRaid(b, "foundry", 2);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p = LatestPlan(b);
            bool story = p != null && p.Budget == (int)Math.Round(250 * 0.5 * RaidDirectorService.IntervalCoef(D(b), p.CreatedTick))
                         && RaidDirectorService.MinWarningTicks(b) == RaidDirectorService.HourTicks(1.5 * 1.5)
                         && RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 200) && p.ArrivalTick - p.WarnTick >= RaidDirectorService.HourTicks(2.25);
            Expect(none && story, $"F4 建造者难度：暴露越过 30 / 60 / 90 都不发动突袭（跳过 {D(b).TriggersSkipped} 次）；剧情突袭照常、规模 ×0.5（预算 {p?.Budget}）、最短预警 ×1.5（{RaidDirectorService.MinWarningTicks(b)} 步）");

            // F5：严酷：规模 ×1.3、普通突袭最短间隔 ÷1.3、预警 ×0.8。
            CampaignState h = NewWorld(6182, difficulty: "Harsh");
            PassGrace(h);
            RaidDirectorService.RequestStoryRaid(h, "foundry", 2);
            WorldSimulation.StepMany(2);
            RaidPlanRecord q = LatestPlan(h);
            bool harsh = q.Budget == (int)Math.Round(250 * 1.3 * RaidDirectorService.IntervalCoef(D(h), q.CreatedTick))
                         && RaidDirectorService.MinWarningTicks(h) == RaidDirectorService.HourTicks(1.5 * 0.8);
            Raise(h, CampaignExposureLedger.FactionFoundry, 31f);
            RaidPlanRecord n1 = LatestPlan(h);
            bool n1ok = AdvanceToDeparture(h, n1);
            Raise(h, CampaignExposureLedger.FactionFoundry, 61f);
            RaidPlanRecord n2 = LatestPlan(h);
            bool gap = n1ok && n2 != n1 && RunUntil(() => n2.State >= RaidDirectorService.StateScheduled, 200)
                       && n2.ArrivalTick >= n2.IntervalBaseTick + (long)Math.Round(Day(1) * n2.IntervalMul / 1.3) - 1
                       && n2.ArrivalTick < n2.IntervalBaseTick + (long)Math.Round(Day(1) * n2.IntervalMul) || (n2.ArrivalTick - n2.WarnTick > RaidDirectorService.MinWarningTicks(h) && n2.ArrivalTick >= n2.IntervalBaseTick + (long)Math.Round(Day(1) * n2.IntervalMul / 1.3) - 1);
            Expect(harsh && gap, $"F5 严酷难度：规模 ×1.3（预算 {q.Budget}）、最短预警 ×0.8（{RaidDirectorService.MinWarningTicks(h)} 步）、普通突袭最短间隔 1 天 ÷ 1.3（第二波抵达 {n2?.ArrivalTick} ≥ {n2?.IntervalBaseTick} + {Day(1) / 1.3:F0}）");
        }

        // ── G 存读档 ─────────────────────────────────────────────────────────────

        [Serializable]
        private sealed class TestEnvelope
        {
            public int SchemaVersion;
            public int ContentVersion;
            public string Checksum;
            public string WrittenAtUtc;
            public string ProductVersion;
            public string CardJson;
            public string PayloadJson;
        }

        private static CampaignState RestoreSlot(out string fail)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                fail = rr.Message;
                return null;
            }
            fail = null;
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            return rr.State;
        }

        /// <summary>一个有三种状态并存的场景：一波在路上（已出发）、一波已预警在集结、一波还在筹备等路线。</summary>
        private static CampaignState LayScenario(int seed, bool observe = true)
        {
            CampaignState s = NewWorld(seed, observe);
            PassGrace(s);
            RaidDirectorService.RequestStoryRaid(s, "silent", 2);
            WorldSimulation.StepMany(2);
            RaidPlanRecord a = LatestPlan(s);
            AdvanceToDeparture(s, a);
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            RaidDirectorService.RequestStoryRaid(s, "foundry", 1);
            WorldSimulation.StepMany(2);
            RaidPlanRecord b = LatestPlan(s);
            RunUntil(() => b.State >= RaidDirectorService.StateScheduled, 120);
            Raise(s, CampaignExposureLedger.FactionFoundry, 31f);
            return s;
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = LayScenario(6191);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            string before = RaidDirectorService.Snapshot(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(GameClock.StepHz * 30);
            string continuous = RaidDirectorService.Snapshot(s);
            CampaignState l = RestoreSlot(out string fail);
            string loaded = l != null ? RaidDirectorService.Snapshot(l) : "读档失败：" + fail;
            if (l != null)
            {
                WorldSimulation.StepMany(GameClock.StepHz * 30);
            }
            string after = l != null ? RaidDirectorService.Snapshot(l) : string.Empty;
            bool states = before.Contains(":3:") && before.Contains("|G");
            Expect(save.Success && loaded == before && after == continuous && states,
                "G1 真文件存读档：突袭计划（筹备 / 排定 / 已出发各态、编成、路线、预警 / 出发 / 抵达时刻）、阈值与周期计时、行进队伍写进存档，读档后逐字段一致；读档后接着跑 30 游戏秒与不存档连续跑逐字段一致"
                + (loaded == before ? string.Empty : $"\n存前：{Short(before)}\n读后：{Short(loaded)}") + (after == continuous ? string.Empty : $"\n连续：{continuous}\n读档：{after}"));

            // G2：旧档（正文里没有导演域）：读档补成空域；已经越过的阈值不追溯触发；之后正常工作。
            WorldSimulation.SyncAllForSave();
            CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            bool stripped = false;
            string path = CampaignSaveService.SlotPath(Slot);
            TestEnvelope env = JsonUtility.FromJson<TestEnvelope>(File.ReadAllText(path));
            int at = env.PayloadJson.IndexOf("\"Director\":{", StringComparison.Ordinal);
            if (at > 0)
            {
                int depth = 0;
                bool inString = false;
                for (int i = at + "\"Director\":".Length; i < env.PayloadJson.Length; i++)
                {
                    char c = env.PayloadJson[i];
                    if (inString)
                    {
                        if (c == '\\')
                        {
                            i++;
                        }
                        else if (c == '"')
                        {
                            inString = false;
                        }
                        continue;
                    }
                    if (c == '"')
                    {
                        inString = true;
                    }
                    else if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}' && --depth == 0)
                    {
                        int from = env.PayloadJson[at - 1] == ',' ? at - 1 : at;
                        env.PayloadJson = env.PayloadJson.Remove(from, i + 1 - from);
                        stripped = true;
                        break;
                    }
                }
            }
            env.Checksum = CampaignSaveService.ComputeChecksum(env.PayloadJson, env.CardJson);
            File.WriteAllText(path, JsonUtility.ToJson(env));
            CampaignState legacy = RestoreSlot(out string fail2);
            bool empty = legacy != null && D(legacy) != null && D(legacy).Plans.Length == 0 && !D(legacy).Initialized;
            if (legacy != null)
            {
                WorldSimulation.StepMany(3);
            }
            bool noRetro = legacy != null && D(legacy).Initialized && (D(legacy).ThresholdFired & 1) == 1 && Plans(legacy).Length == 0 && legacy.SignalExposure >= 30f;
            if (legacy != null)
            {
                RaidDirectorService.RequestStoryRaid(legacy, "silent", 1);
                WorldSimulation.StepMany(2);
            }
            bool works = legacy != null && Plans(legacy).Length == 1;
            Expect(stripped && empty && noRetro && works, $"G2 旧档（没有导演域）：读档补成空域、不报错；第一次推进时按当前暴露 {legacy?.SignalExposure:F0} 记已越过的阈值，不追溯触发；之后正常排定突袭" +
                                                          (fail2 != null ? "；读档失败：" + fail2 : string.Empty));
        }

        // ── G3 复审 P2：两个计划同时筹备、存档时提前取出第一条的结果——第二条仍等到第一条的采纳步之后才开始寻路 ─────────────────

        /// <summary>两波剧情突袭同一步排进来：第一条占着后台通道等采纳，第二条排队。</summary>
        private static CampaignState LayTwoPlanning(int seed)
        {
            CampaignState s = NewWorld(seed);
            PassGrace(s);
            RaidDirectorService.RequestStoryRaid(s, "silent", 1);
            RaidDirectorService.RequestStoryRaid(s, "foundry", 1);
            WorldSimulation.StepMany(2 + GameClock.StepHz);
            return s;
        }

        private static void CheckLaneSaveTiming()
        {
            // 不存档：连续跑 20 游戏秒。
            CampaignState a = LayTwoPlanning(6193);
            RaidPlanRecord[] pa = Plans(a).OrderBy(p => p.Serial).ToArray();
            bool laid = pa.Length == 2 && pa[0].RouteState == RaidDirectorService.RouteAwaiting && pa[1].RouteState == RaidDirectorService.RouteNeed
                        && D(a).LaneBusyUntilTick == pa[0].RouteAdoptTick && D(a).LaneBusyUntilTick > GameClock.Ticks;
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            string continuous = RaidDirectorService.Snapshot(a);
            bool secondAfter = pa.Length == 2 && pa[1].RouteAdoptTick > pa[0].RouteAdoptTick;
            // 同一世界、同一时刻存档（存档时第一条的结果被提前取出、通道空出来），之后接着跑 / 读档再跑，都要与不存档逐字段一致。
            CampaignState b = LayTwoPlanning(6193);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            bool collected = Plans(b).Any(p => p.PendingRoute);
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            string savedThenPlayed = RaidDirectorService.Snapshot(b);
            CampaignState l = RestoreSlot(out string fail);
            if (l != null)
            {
                WorldSimulation.StepMany(GameClock.StepHz * 20);
            }
            string loaded = l != null ? RaidDirectorService.Snapshot(l) : "读档失败：" + fail;
            Expect(laid && secondAfter && save.Success && collected && savedThenPlayed == continuous && loaded == continuous,
                $"G3 两波同时筹备：第一条占着后台通道到采纳步 {pa.ElementAtOrDefault(0)?.RouteAdoptTick}，第二条排队（采纳步 {pa.ElementAtOrDefault(1)?.RouteAdoptTick}）；" +
                $"存档时第一条的结果被提前取出（{collected}），第二条仍按占用步开始——存档后接着跑、读档再跑都与不存档逐字段一致" +
                (savedThenPlayed == continuous ? string.Empty : $"\n不存档：{continuous}\n存档后：{savedThenPlayed}") + (loaded == continuous ? string.Empty : $"\n读档后：{loaded}"));
        }

        // ── H / I 暂停、倍速、观察 ───────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe);
            PassGrace(s);
            RaidDirectorService.RequestStoryRaid(s, "silent", 2);
            WorldSimulation.StepMany(2);
            RaidPlanRecord p = LatestPlan(s);
            RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 120);
            SkipIdle(s, p.WarnTick - GameClock.StepHz * 5);
            pausedHeld = true;
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = RaidDirectorService.Snapshot(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = RaidDirectorService.Snapshot(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 160;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 1200)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return RaidDirectorService.Snapshot(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(6201, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference.Contains(":3:") && reference.Contains("|G"),
                "H 暂停中（120 帧）导演与队伍不动；0.5x / 1x / 2x / 3x 跑同样的 160 游戏秒（预警 → 集结 → 出发 → 行进）逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(6202, true, 1f, false, out _);
            string unseen = RunScenario(6202, false, 1f, false, out _);
            Expect(seen == unseen && seen.Contains("|G"), "I 同一波突袭在观察与不观察家园时跑 160 游戏秒（预警、集结、出发、行进）逐字段一致（FGR-BASE-021：远征时家园照常被突袭）"
                                                          + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── J 预警条（真 UXML） ────────────────────────────────────────────────────

        private static bool HasCjk(string text) => text.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);

        private static void CheckHud()
        {
            CampaignState s = NewWorld(6211);
            PassGrace(s);
            VisualElement root = F.MountUxml(HudUxml, out GameObject go);
            RaidWarningHudUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var hud = go.AddComponent<RaidWarningHudUIToolkit>();
                hud.BindView(root);
                hud.Refresh(force: true);
                bool emptyHidden = !hud.PanelVisible && hud.RowCount == 0;
                // 一波（没有情报）：已发预警、集结中 → 一行，写方向、倒计时、编成未知。
                RaidDirectorService.RequestStoryRaid(s, "silent", 1);
                WorldSimulation.StepMany(2);
                RaidPlanRecord p = LatestPlan(s);
                RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 120);
                // 还没有监听站：预警条只有倒计时、方向、目标，编成未知。
                SkipIdle(s, p.WarnTick - 2);
                RunUntil(() => p.State >= RaidDirectorService.StateWarned, 10);
                hud.Refresh(force: true);
                string row = hud.RowText(0);
                bool noIntel = hud.PanelVisible && hud.RowCount == 1 && hud.TitleText == GameText.Get("raid.warning.title") && row.Contains(RaidDirectorService.DirectionText(p))
                               && row.Contains(GameText.Get("raid.warning.comp_unknown")) && (p.DepartTick <= GameClock.Ticks || row.Contains("集结"))
                               && hud.RowTip(0).Contains(RaidCatalog.TriggerName(RaidCatalog.TriggerStory)) && !GameText.ContainsMarker(row + hud.RowTip(0));
                // 悬停逐行：触发、目标、出发地（方向）、预计抵达、编成未知、点击说明；没有情报时没有空行、没有没填的占位符。
                string tip0 = hud.RowTip(0);
                bool tipLines = tip0.Contains(GameText.Format("raid.warning.tip.target", RaidDirectorService.TargetText(p)))
                                && tip0.Contains(GameText.Format("raid.warning.tip.origin", RaidDirectorService.OriginText(p), RaidDirectorService.DirectionText(p)))
                                && tip0.Contains(GameText.Get("raid.warning.comp_unknown")) && !tip0.Contains("\n\n") && !tip0.Contains("{");
                noIntel &= tipLines;
                // 点一行：镜头飞到预计抵达点、钩子。
                int fly0 = WorldView.FlyCount;
                bool click = hud.ClickRow(0) && WorldView.FlyCount == fly0 + 1 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidWarningFirstClick)
                             && Vector2.Distance(hud.RowTarget(0), new Vector2(p.ArriveX, p.ArriveY)) < 1f;
                Expect(emptyHidden && noIntel && click,
                    $"J1 预警条：没有突袭时隐藏；一波（没有情报）一行“{Short(row.Replace('\n', ' '))}”（倒计时、方向、集结、编成未知），悬停逐行写触发、目标、出发地与方向、预计抵达（{tipLines}）；点一行镜头飞到预计抵达点");

                // 有情报：建一座接上电的监听站（测试捷径：施工由情报自检覆盖）→ 破译 → 行里出现阵营、规模、编成、针对什么。
                List<string> posts = AddPosts(s, 1);
                bool decoded = RunUntil(() => IntelService.HasValid(s, IntelCatalog.KindRaid, p.PlanId, GameClock.Ticks, out _), 200);
                hud.Refresh(force: true);
                string row2 = hud.RowText(0);
                bool withIntel = posts.Count == 1 && decoded && row2.Contains(WorldTransitSystem.OriginName(p.Faction)) && row2.Contains(RaidDirectorService.CompositionText(p))
                                 && row2.Contains(RaidDirectorService.CounterText(p)) && !row2.Contains(GameText.Get("raid.warning.comp_unknown"));
                Expect(withIntel, $"J2 有有效的突袭预报时：行里写阵营、等级、规模、编成与针对什么——“{Short(row2.Replace('\n', ' '))}”");

                // 英文：没有中文残留（标记字也换成 [R]）。
                GameSettings.SetLanguage(GameLanguage.En);
                hud.Refresh(force: true);
                string en = hud.RowText(0) + hud.TitleText + hud.RowTip(0);
                bool english = !HasCjk(en) && en.Contains("[R]");
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Expect(english, $"J3 英文界面：预警条没有中文残留（“{Short(en.Replace('\n', ' '))}”）");

                // 多波：超出上限写“还有 n 波”。
                for (int i = 0; i < RaidCatalog.WarningRowsMax + 1; i++)
                {
                    RaidDirectorService.RequestStoryRaid(s, i % 2 == 0 ? "silent" : "foundry", 1);
                }
                WorldSimulation.StepMany(2);
                RunUntil(() => Plans(s).All(x => x.State >= RaidDirectorService.StateScheduled), 200);
                long lastWarn = Plans(s).Where(x => x.State == RaidDirectorService.StateScheduled).Select(x => x.WarnTick).DefaultIfEmpty(GameClock.Ticks).Max();
                RunUntil(() => Plans(s).All(x => x.State >= RaidDirectorService.StateWarned), (int)((lastWarn - GameClock.Ticks) / GameClock.StepHz) + 30);
                hud.Refresh(force: true);
                int waves = RaidDirectorService.IncomingWaves(s, GameClock.Ticks).Count;
                bool more = waves > RaidCatalog.WarningRowsMax && hud.RowCount == RaidCatalog.WarningRowsMax && hud.MoreText.Contains((waves - RaidCatalog.WarningRowsMax).ToString());
                Expect(more, $"J4 波数超过上限（{waves} 波）：列前 {hud.RowCount} 波，其余写“{hud.MoreText}”");

                string probe = UiToolkitLayoutProbe.Probe(HudUxml, "RaidWarnRoot", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("RaidWarnPanel")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "J5 预警条布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
            }
            finally
            {
                RaidWarningHudUIToolkit.InWorldOverrideForTests = false;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Object.DestroyImmediate(go);
            }
        }

        // ── J6～J8 地图箭头、预测路线、暴露面板、关注点 ─────────────────────────────────

        private static void CheckMapAndPanels()
        {
            CampaignState s = NewWorld(6221);
            PassGrace(s);
            Raise(s, CampaignExposureLedger.FactionFoundry, 31f);
            RaidPlanRecord p = LatestPlan(s);
            RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 120);
            SkipIdle(s, p.WarnTick - 2);
            RunUntil(() => p.State >= RaidDirectorService.StateWarned, 10);
            // 战略地图：没有情报也画来袭方向箭头 + 标签（方向、倒计时）。
            var model = new WorldMapModel();
            GridCell core = HomeGridService.CorePivot(s);
            var view = new WorldMapView { CenterX = core.X, CenterY = core.Y, HalfWidth = 300, CanvasWidth = 1000, CanvasHeight = 800 };
            model.Collect(s, view, 256);
            WorldMapItem icon = model.Items.FirstOrDefault(i => i.Kind == WorldMapItemKind.RaidWarning);
            WorldMapLine arrow = model.Lines.FirstOrDefault(l => l.Forecast);
            double tailD = Math.Sqrt((arrow.X0 - core.X) * (arrow.X0 - core.X) + (arrow.Y0 - core.Y) * (arrow.Y0 - core.Y));
            double headD = Math.Sqrt((arrow.X1 - core.X) * (arrow.X1 - core.X) + (arrow.Y1 - core.Y) * (arrow.Y1 - core.Y));
            bool map = icon.Label != null && icon.Label.Contains(RaidDirectorService.DirectionText(p)) && arrow.Forecast && headD < tailD
                       && IntelService.Octant(arrow.X0 - core.X, arrow.Y0 - core.Y) == IntelService.Octant(p.ArriveX - core.X, p.ArriveY - core.Y);
            Expect(map, $"J6 战略地图：已发预警的突袭（没有情报）画一支从来袭方向指向家园的箭头，标签“{icon.Label}”");
            // 视野比箭头短（地图打开时围着家园）：标签收到视野里、仍在箭头上（离家园比箭尾近，方向不变）。
            var j6bView = new WorldMapView { CenterX = core.X, CenterY = core.Y, HalfWidth = 30, CanvasWidth = 1000, CanvasHeight = 800 };
            var j6bModel = new WorldMapModel();
            j6bModel.Collect(s, j6bView, 256);
            WorldMapItem j6bIcon = j6bModel.Items.FirstOrDefault(i => i.Kind == WorldMapItemKind.RaidWarning);
            double j6bD = j6bIcon.Label != null ? Math.Sqrt((j6bIcon.X - core.X) * (j6bIcon.X - core.X) + (j6bIcon.Y - core.Y) * (j6bIcon.Y - core.Y)) : -1;
            double j6bCross = j6bIcon.Label != null ? (j6bIcon.X - core.X) * (arrow.Y0 - core.Y) - (j6bIcon.Y - core.Y) * (arrow.X0 - core.X) : 1e9;
            bool j6bOk = j6bIcon.Label != null && j6bView.Contains(j6bIcon.X, j6bIcon.Y) && j6bD > 0 && j6bD < tailD && Math.Abs(j6bCross) < 1e-6 * tailD * tailD
                           && j6bIcon.Label.Contains(RaidDirectorService.DirectionText(p));
            Expect(j6bOk, $"J6b 地图视野比箭头短（半宽 30 格 < 箭头 {tailD:F0} 格）：标签收到视野里（离家园 {j6bD:F0} 格）、仍在箭头上，写“{j6bIcon.Label}”");

            // 暴露面板：各阵营贡献里写明“最受刺激：下一次突袭由它发动”；家园突袭一行写正在逼近。
            CheckExposurePanel(s);

            // J7：叠加层的预测路线（关闭 DEBT-FG3LOG08-01）与关注点——找一个静默据点离家园很近（路程不够最短预警 → 集结）的种子。
            CampaignState w = FindWorld(6222, 16, x =>
            {
                GridCell c0 = HomeGridService.CorePivot(x);
                OutpostRecord n0 = Nearest(x, "silent", c0.X, c0.Y);
                return n0 != null && Math.Sqrt((n0.CellX - c0.X) * (double)(n0.CellX - c0.X) + (n0.CellY - c0.Y) * (double)(n0.CellY - c0.Y)) < 95;
            }, out int wSeed);
            if (w == null)
            {
                Fail("J7 16 个种子里找不到静默据点离家园 95 格以内的世界");
                return;
            }
            PassGrace(w);
            RaidDirectorService.RequestStoryRaid(w, "silent", 1);
            WorldSimulation.StepMany(2);
            p = LatestPlan(w);
            RunUntil(() => p.State >= RaidDirectorService.StateScheduled, 120);
            SkipIdle(w, p.WarnTick - 2);
            RunUntil(() => p.State >= RaidDirectorService.StateWarned, 10);
            s = w;
            core = HomeGridService.CorePivot(w);
            OverlayService.Set(OverlayKind.Raid);
            OverlayService.RedrawNow();
            float radius = GridContent.Tuning("overlay.raid_outpost_radius");
            int near = WorldOutpostSystem.Outposts(s).Count(o => o != null && !o.Destroyed && Vector2.Distance(new Vector2(o.CellX, o.CellY), new Vector2(core.X, core.Y)) <= radius);
            int groups = WorldTransitSystem.Groups(s).Count(g => g != null && g.Kind == TransitGroupKind.Raid);
            int planned = Plans(s).Count(x => x.State == RaidDirectorService.StateWarned && x.RouteX.Length > 0);
            int drawn = OverlayService.DrawnLines;
            OverlayService.Set(OverlayKind.None);
            bool overlay = p.State == RaidDirectorService.StateWarned ? planned >= 1 && drawn == groups + near + planned : drawn == groups + near;
            // 关注点：集结中的突袭进 Tab 循环。
            bool focus = p.DepartTick <= GameClock.Ticks || WorldView.FocusTargets(s).Any(t => t.Id == p.PlanId && t.IsRaid);
            bool assembling = p.DepartTick > GameClock.Ticks && p.State == RaidDirectorService.StateWarned && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RaidFirstAssembling)
                              && p.OriginKind == RaidDirectorService.OriginOutpost;
            Expect(assembling && overlay && focus, $"J7 种子 {wSeed}：据点离家园近、路程不够最短预警 → 先发预警、部队在据点集结（{(p.DepartTick - GameClock.Ticks) / GameClock.StepHz} 游戏秒后出发）；" +
                                     $"突袭路径叠加层沿排定路线画出预测路线（共 {drawn} 条 = 行进中 {groups} + 附近据点来路 {near} + 预测 {planned}）；集结中的突袭在关注点（Tab）里");

            // J9：出发时从据点驻军里抽人随队，撤回出发地时还给据点（不超过上限）。
            OutpostRecord nest = WorldOutpostSystem.Find(w, p.OriginId);
            int g0 = nest.Garrison;
            bool departed = RunUntil(() => p.State >= RaidDirectorService.StateDeparted, 200);
            TransitGroupRecord g = WorldTransitSystem.Find(w, p.GroupId);
            int taken = g?.GarrisonTaken ?? -1;
            bool tookOk = departed && g != null && taken == Math.Min(p.UnitTotal, Math.Max(0, g0 - 1)) && nest.Garrison == g0 - taken;
            bool back = g != null && RunUntil(() => WorldTransitSystem.Find(w, p.GroupId) == null, 600);
            bool restored = back && nest.Garrison == Math.Min(nest.GarrisonCap, g0 - taken + taken);
            Expect(tookOk && restored, $"J9 据点是集结点：出发时驻军 {g0} 里抽 {taken} 台随队（至少留 1 台守家），到达后时间上限撤回出发地离场，驻军还回 {nest.Garrison}");
        }

        private static void CheckExposurePanel(CampaignState s)
        {
            Raise(s, CampaignExposureLedger.FactionSilent, s.SignalExposure + 4f);
            string most = RaidDirectorService.MostStimulatedFaction(s);
            VisualElement root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "SignalCorePanel.uxml")?.CloneTree();
            var owner = new object();
            var exposure = new ExposurePanelView(owner);
            try
            {
                if (root != null)
                {
                    exposure.Bind(root, () => { });
                }
                exposure.SetOpen(true);
                exposure.Refresh(s, true);
                int idx = Enumerable.Range(0, exposure.FactionCount).FirstOrDefault(i => exposure.FactionText(i).Contains("最受刺激"));
                bool panel = root != null && exposure.FactionCount >= 2 && most == "foundry" && exposure.FactionText(idx).Contains(CampaignExposureLedger.FactionName("foundry"))
                             && exposure.NextText.Contains(GameText.Get("exposure.panel.raid_line").Split('{')[0]);
                Expect(panel, $"J8 暴露面板：各阵营贡献里最多的一行写“{exposure.FactionText(idx)}”，与导演选的阵营（{most}）一致；“{Short(exposure.NextText.Replace('\n', ' '))}”");
            }
            finally
            {
                exposure.SetOpen(false);
                UiEscapeStack.UnregisterPage(owner);
            }
        }

        // ── K 后台长路线通道（DEBT-FG0ARCH06-09）──────────────────────────────────────────

        /// <summary>按 3 倍速的真实节奏推进 <paramref name="steps"/> 步（每真实帧 3 步、睡到 1/60 秒）：给工作线程真实的时间，像玩家开 3x 时一样。</summary>
        private static void RealPace3x(int steps, Func<bool> sampleDone, ref long doneMs, Stopwatch sw)
        {
            for (int i = 0; i < steps; i += 3)
            {
                WorldSimulation.StepMany(Math.Min(3, steps - i));
                if (doneMs < 0 && sampleDone())
                {
                    doneMs = sw.ElapsedMilliseconds;
                }
                Thread.Sleep(16);
            }
        }

        private static void CheckBackgroundLane()
        {
            CampaignState s = NewWorld(6231);
            NavService.Kernel.CompleteInFlight();
            GridCell core = HomeGridService.CorePivot(s);
            // K1：约 1,800 格的冷长路线（途经区块没生成过）走后台通道：按 3x 真实节奏走过固定的采纳延迟（raid.route_latency_seconds），取结果时不硬等；
            // 同一时刻常规批次照常（单位路线 6 步采纳）。对照：同样长的另一条冷路线走常规批次（6 步延迟），采纳步硬等。
            var a = new GridCell(core.X + 1300, core.Y + 1250);
            var b = new GridCell(core.X - 1300, core.Y - 1250);
            long bgLate0 = NavService.Kernel.BackgroundLateCompletes;
            long mainLate0 = NavService.Kernel.LateCompletes;
            bool started = NavService.StartBackground(NavService.OwnerRaidPlan, 900001, 1, BinGames.Sim.Nav.NavConst.ClassHostile, a, core, allowPartial: true);
            bool busyRefused = !NavService.StartBackground(NavService.OwnerRaidPlan, 900002, 2, BinGames.Sim.Nav.NavConst.ClassHostile, b, core, allowPartial: true);
            int latency = (int)GameClock.TicksFor(RaidCatalog.RouteLatencySeconds);
            var sw = Stopwatch.StartNew();
            long doneMs = -1;
            RealPace3x(latency, () => NavService.Kernel.BackgroundDone, ref doneMs, sw);
            var pts = new List<Unity.Mathematics.int2>();
            bool got = NavService.CollectBackground(pts, out BinGames.Sim.Nav.NavResult r1);
            long bgLate = NavService.Kernel.BackgroundLateCompletes - bgLate0;
            long mainLateDuring = NavService.Kernel.LateCompletes - mainLate0;
            // 对照：常规批次 6 步延迟（同样按 3x 真实节奏）。
            long late1 = NavService.Kernel.LateCompletes;
            NavService.Request(NavService.OwnerRaidPlan, 900003, 3, BinGames.Sim.Nav.NavConst.ClassHostile, b, core, allowPartial: true);
            long dummy = -1;
            RealPace3x(24, () => false, ref dummy, sw);
            long coldLate = NavService.Kernel.LateCompletes - late1;
            PerfLines.Add($"后台长路线通道：约 1,800 格冷路线工作线程 {doneMs} ms 内算完（采纳延迟 {latency} 步 = 3x 下约 {latency / 3 * 16} ms）、取结果硬等 {bgLate} 次、同期常规批次硬等 {mainLateDuring} 次；" +
                          $"对照：同样长的冷路线走常规批次（6 步延迟）硬等 {coldLate} 次；镜像同步 {NavService.Kernel.LastBackgroundSyncedChunks} 块 {NavService.Kernel.LastBackgroundSyncMs:F2} ms");
            Expect(started && busyRefused && got && pts.Count > 0 && bgLate == 0 && mainLateDuring == 0 && doneMs >= 0,
                $"K1 DEBT-FG0ARCH06-09：排定突袭的冷长路线（约 1,800 格，{pts.Count} 个路点，{r1.Status}）走后台通道，{doneMs} ms 内算完、到采纳步取结果不硬等（{bgLate} 次），同期常规批次不受影响；" +
                $"一次一条（正在算时第二条被拒）；对照走常规 6 步延迟硬等 {coldLate} 次");

            // K2：后台通道的结果与常规寻路同一套算法：同一请求逐点相同（确定性；读档后从存下的结果交到也一样，见 G1）。
            WorldSimulation.StepMany(12);
            NavService.Kernel.CompleteInFlight();
            var mainPts = new List<Unity.Mathematics.int2>();
            var req = new BinGames.Sim.Nav.NavRequest
            {
                OwnerTag = 9, OwnerKey = 1, Serial = 1, Class = BinGames.Sim.Nav.NavConst.ClassHostile, Flags = BinGames.Sim.Nav.NavRequestFlags.AllowPartial,
                Start = new Unity.Mathematics.int2(core.X + 600, core.Y - 500), Goal = new Unity.Mathematics.int2(core.X, core.Y),
            };
            BinGames.Sim.Nav.NavResult m = NavService.Kernel.FindNow(req, mainPts, false, out _);
            NavService.StartBackground(9, 1, 1, BinGames.Sim.Nav.NavConst.ClassHostile, new GridCell(core.X + 600, core.Y - 500), core, allowPartial: true);
            var bgPts = new List<Unity.Mathematics.int2>();
            NavService.CollectBackground(bgPts, out BinGames.Sim.Nav.NavResult g);
            Expect(m.Status == g.Status && mainPts.Count > 0 && mainPts.SequenceEqual(bgPts),
                $"K2 后台通道与常规寻路同一套算法：同一条约 780 格路线逐点相同（{mainPts.Count} 个路点，{m.Status}）——采纳时机固定、结果只取决于开始那一刻的格网，确定");
        }

        // ── L 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(6241);
            PassGrace(s);
            for (int i = 0; i < 8; i++)
            {
                RaidDirectorService.RequestStoryRaid(s, i % 2 == 0 ? "silent" : "foundry", 1 + i % 4);
            }
            WorldSimulation.StepMany(2);
            RunUntil(() => Plans(s).All(x => x.State >= RaidDirectorService.StateScheduled), 200);
            // 每步：8 个计划 + 预警条刷新。
            double maxStep = 0;
            double sum = 0;
            int n = 600;
            for (int i = 0; i < n; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                RaidDirectorService.WorldStep(s, GameClock.Ticks);
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                sum += ms;
                maxStep = Math.Max(maxStep, ms);
            }
            // 编成：后日谈大预算（单位数到上限、换精英）一次。
            var big = new RaidPlanRecord { Serial = 77, Faction = "foundry", Level = 4, Budget = 20000 };
            long c0 = Stopwatch.GetTimestamp();
            RaidDirectorService.Compose(s, big);
            double composeMs = (Stopwatch.GetTimestamp() - c0) * 1000.0 / Stopwatch.Frequency;
            long w0 = Stopwatch.GetTimestamp();
            IReadOnlyList<RaidWaveView> waves = RaidDirectorService.IncomingWaves(s, GameClock.Ticks);
            double wavesMs = (Stopwatch.GetTimestamp() - w0) * 1000.0 / Stopwatch.Frequency;
            double avg = sum / n;
            PerfLines.Add($"导演每步（8 个计划）平均 {avg:F4} ms、最大 {maxStep:F4} ms（阈值 0.05 / 0.5 ms）；编成到上限 200 台 + 精英一次 {composeMs:F3} ms（阈值 2 ms）；预警条数据 {wavesMs:F4} ms");
            PerfGate.Expect(true, $"L1 导演每步平均 {avg:F4} ms、最大 {maxStep:F4} ms（8 个计划；阈值平均 0.05 ms，热更层每步 O(计划数)）",
                new[] { PerfGate.Le(avg, 0.05, "导演每步平均 ms") }, Expect, Line);
            PerfGate.Expect(Plans(s).Length == 8, $"L2 编成 200 台 + 精英一次 {composeMs:F3} ms（阈值 2 ms，只在排定 / 升级 / 合并时算）；预警条数据 {wavesMs:F4} ms（阈值 0.2 ms）",
                new[] { PerfGate.Le(composeMs, 2.0, "编成 ms"), PerfGate.Le(wavesMs, 0.2, "预警条数据 ms") }, Expect, Line);
            Expect(big.UnitTotal == RaidCatalog.MaxUnits, $"L3 规模压力：预算 20000 的一波停在单位上限 {big.UnitTotal} 台（FG15 同时存在的敌人 200）");
        }

        // ── 断言 ─────────────────────────────────────────────────────────────────

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
