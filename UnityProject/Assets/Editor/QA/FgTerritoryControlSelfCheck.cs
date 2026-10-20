using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.WorldGen;
using GameConfig;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
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
using GameLogic.View;
using TEngine;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG8-EXP-03 领地控制、增援与收复（FG08 FGR-EXP-021、022、030；FG17 FGR-GEN-060；验收 FGT-EXP-005）的自检——起真实系统（新战役 → 家园载入 → 世界模拟逐步推进 → 家园战斗内核），断言行为：
    /// A 数据（源数据 = 运行时表、文本键中英、调参、钩子、通知类型、图鉴）；B 控制度（按活跃领地据点等级算、地图标签、旧世界“待侦察”）；
    /// C 迎战（机器走近 → 驻军就地展开、攻城对账不当孤儿清掉、全灭 → 摧毁 + 掉落 + 搬运单 + 控制度下降；没人 → 收拢；巡逻迎战与歼灭）；
    /// D 阵营增援（敌控每 3 天一次、确定性重建、玩家附近不重建、重建的据点补巡逻）；E 首领 → 平定（正式剧情规则 + 首领 Story 入口；不再增援、休眠补算一致）；
    /// F 收复（控制度 &lt; 20、巡逻撤离、通知 + 核心备忘、未平定不收复、残留据点照常迎战）；G 精英残部（7 天、不叠加、击溃掉落、首领不再刷新）；
    /// H 骚扰（敌控领地里建设允许但会被派兵攻击、到达展开；收复后不再派）；I 收复后的净化计入归还面积、化工再污染后状态不回退；
    /// J 存读档（领地记录、迎战中的据点与内核单位）；K 暂停与 0.5x～3x；L 巡逻标记不跳位（DEBT-FG8GEN02-07）；M 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgTerritoryControlSelfCheck）；单段：bash tools/unity-validate.sh --segment FgTerritoryControlSelfCheck。
    /// </summary>
    public static class FgTerritoryControlSelfCheck
    {
        private const int Slot = 9;
        private const int RunSlot = 10;
        private static StringBuilder _report;
        private static int _pass;
        private static int _fail;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 领地控制、增援与收复")]
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
            Line("\n[领地控制、增援与收复] 控制度、迎战与摧毁、阵营增援、首领 → 平定、收复、精英残部、骚扰、净化与再污染、存读档、倍速（FG8-EXP-03）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgtcontrol-selfcheck-" + Guid.NewGuid().ToString("N"));
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

                Step(CheckData);
                Step(CheckControl);
                Step(CheckEngage);
                Step(CheckRegroupAndPatrol);
                Step(CheckScoutNest);
                Step(CheckReinforce);
                Step(CheckPacify);
                Step(CheckReclaimEliteHarassPurify);
                Step(CheckHarassInEnemyTerritory);
                Step(CheckSpeedMatrix);
                Step(CheckPatrolView);
                Step(CheckSeedSet);
                foreach (string p in PerfLines)
                {
                    Line("  · " + p);
                }
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                WorldGenContent.ResetForTests();
                CameraDirector.RealDeltaTime = originalDelta;
                InputRouter.DebugSetReader(null);
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
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
            Line(_fail == 0 ? $"  领地控制、增援与收复：全部 {_pass} 项通过" : $"  领地控制、增援与收复：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ── A 数据 ──────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Tables t = ConfigSystem.Instance.Tables;
            string root = F.LocateRepo();
            (int code, string output) = F.RunPython(root,
                "-c \"import sys;sys.path.insert(0,'tools/cell_tables');import fgdata_territory_control as p;print('\\n'.join(p.dump_lines()))\"");
            var src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("TCL\t")).ToList();
            var rt = t.TbTerritoryControl.DataList.Select(r => string.Join("\t", "TCL", r.Id, r.LeaderRule, r.LeaderNameKey, r.GarrisonUnits, r.EliteUnits, r.EliteCount, r.EliteScrap,
                r.MemoTitleKey, r.MemoBodyKey)).ToList();
            bool table = code == 0 && src.Count == 4 && src.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(rt.OrderBy(x => x, StringComparer.Ordinal));
            Expect(table, $"A1 源数据 fgdata_territory_control.py 与运行时 fg.TbTerritoryControl（{rt.Count} 行：四个阵营领地）逐字段一致" +
                          (table ? string.Empty : $"（源 {src.Count} / 运行时 {rt.Count}，python 退出码 {code}）"));

            var keys = new List<string>
            {
                "ui.map.territory_control", "ui.map.territory_label", "territory.status.enemy", "territory.status.pacifying", "territory.status.reclaimed",
                "territory.notify.engaged", "territory.notify.patrol_engaged", "territory.notify.destroyed", "territory.notify.destroyed_other", "territory.notify.patrol_routed",
                "territory.notify.rebuilt", "territory.notify.pacified", "territory.notify.reclaimed", "territory.notify.memo", "territory.notify.elite", "territory.notify.elite_routed",
                "territory.notify.harass", "world.poi.remnant", "stats.territory.title", "stats.territory.row", "stats.territory.totals", "codex.world.control.title", "codex.world.control.body", "codex.world.control.hint",
            };
            foreach (GameConfig.fg.TerritoryControl r in t.TbTerritoryControl.DataList)
            {
                keys.Add(r.LeaderNameKey);
                keys.Add(r.MemoTitleKey);
                keys.Add(r.MemoBodyKey);
            }
            var missing = new List<string>();
            foreach (string k in keys)
            {
                if (!GameText.Has(k))
                {
                    missing.Add(k);
                    continue;
                }
                GameSettings.SetLanguage(GameLanguage.En);
                bool en = GameText.Has(k) && !GameText.Get(k).Contains("⟦");
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                if (!en)
                {
                    missing.Add(k + "(en)");
                }
            }
            Expect(missing.Count == 0, $"A2 新文本键 {keys.Count} 个中英两套都在（B16）" + (missing.Count == 0 ? string.Empty : "——缺：" + string.Join("，", missing.Take(8))));

            bool tune = new[]
                {
                    "territory.control.sync_seconds", "territory.control.reclaim_below", "territory.reinforce.interval_days", "territory.reinforce.rebuild_chance", "territory.reinforce.rebuild_max",
                    "territory.reinforce.rebuild_garrison_ratio", "territory.reinforce.block_radius_cells", "territory.elite.interval_days", "territory.harass.interval_hours", "territory.harass.units",
                    "territory.engage.radius_cells", "territory.engage.scout_nest_mode", "territory.engage.regroup_seconds", "territory.engage.max_seconds", "territory.loot.scrap_per_tier", "outpost.patrol_view_catchup", "outpost.patrol_view_snap",
                }
                .All(k => GridContent.TryGetTuning(k, out float v) && v > 0f);
            bool initial = TerritoryControlService.ReclaimBelow == 20 && Math.Abs(TerritoryControlService.ReinforceDays - 3.0) < 1e-6 && Math.Abs(TerritoryControlService.EliteDays - 7.0) < 1e-6
                           && TerritoryControlService.ReinforceTicks == GameClock.TicksFor(3 * GameClock.DaySeconds) && TerritoryControlService.EliteTicks == GameClock.TicksFor(7 * GameClock.DaySeconds);
            bool hooks = new[]
                {
                    GuidanceHooks.WorldFirstOutpostDestroyed, GuidanceHooks.WorldFirstTerritoryPacified, GuidanceHooks.WorldFirstTerritoryReclaimed, GuidanceHooks.WorldFirstEliteRemnant,
                    GuidanceHooks.WorldFirstTerritoryHarassed,
                }
                .All(h => GuidanceHooks.Known.Contains(h));
            bool notify = t.TbNotifyType.GetOrDefault("territory_status")?.AwaySection == "event" && t.TbNotifyType.GetOrDefault("territory_alert")?.AwaySection == "raid";
            bool codex = t.TbCodexEntry.GetOrDefault("codex.world.control") != null;
            // FG9-FAC-01：静默 = 寂听主脑（剧情规则 silent_mind）；澄净 / 超频待首领 Story。
            bool rules = t.TbTerritoryControl.GetOrDefault("foundry")?.LeaderRule == RaidCatalog.StoryRuleFoundryCore
                         && t.TbTerritoryControl.GetOrDefault("silent")?.LeaderRule == RaidCatalog.StoryRuleSilentMind
                         && t.TbTerritoryControl.DataList.All(r => r.LeaderRule == TerritoryControlService.RuleNone || r.LeaderRule == RaidCatalog.StoryRuleFoundryCore
                                                                   || r.LeaderRule == RaidCatalog.StoryRuleSilentMind);
            Expect(tune && initial && hooks && notify && codex && rules,
                $"A3 调参入表（初值：收复阈值 {TerritoryControlService.ReclaimBelow}、增援每 {TerritoryControlService.ReinforceDays} 天、精英残部每 {TerritoryControlService.EliteDays} 天）、五个引导钩子（内容归 FG15-UX-04）、" +
                $"两种通知类型（离家报告“事件 / 突袭”段）、图鉴、首领规则（铸造 = 主核心、静默 = 寂听主脑，其余待首领 Story）：调参 {tune} / 初值 {initial} / 钩子 {hooks} / 通知 {notify} / 图鉴 {codex} / 规则 {rules}");
        }

        // ── B 控制度 ─────────────────────────────────────────────────────────────

        private static void CheckControl()
        {
            CampaignState s = NewWorld(42);
            int engage0 = TerritoryControlService.EngageCount;
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            bool quietStart = TerritoryControlService.EngageCount == engage0 && WorldOutpostSystem.Outposts(s).All(o => !o.Engaged);
            WorldPlan plan = WorldGenService.PlanFor(s);
            var expectIds = plan.Territories.Where(x => x.IsFaction).Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var recs = TerritoryControlService.StateOf(s).Records;
            bool records = recs.Select(r => r.TerritoryId).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expectIds) && recs.All(r => r.Status == TerritoryControlService.StatusEnemy && r.Control == 100)
                           && TerritoryControlService.Find(s, "white_tide") == null;
            List<OutpostRecord> silent = TerritoryOutposts(s, "silent");
            int total = silent.Sum(o => Math.Max(1, o.Tier));
            OutpostRecord a = silent[0];
            OutpostRecord b = silent[1];
            WorldOutpostSystem.MarkDestroyed(s, a.OutpostId);
            WorldOutpostSystem.MarkDestroyed(s, b.OutpostId);
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            int expected = (int)((total - Math.Max(1, a.Tier) - Math.Max(1, b.Tier)) * 100L / total);
            TerritoryControlRecord rs = TerritoryControlService.Find(s, "silent");
            string label = TerritoryControlService.MapLabel(s, plan.Find("silent"));
            PlannedTerritory ts = plan.Find("silent");
            var model = new GameLogic.UI.Kit.WorldMapModel();
            model.Collect(s, new GameLogic.UI.Kit.WorldMapView { CenterX = ts.CenterX, CenterY = ts.CenterY, HalfWidth = ts.OuterRadius * 2, CanvasWidth = 800, CanvasHeight = 800 }, 500);
            bool mapShows = model.Circles.Any(c => c.Label != null && c.Label.StartsWith(label, StringComparison.Ordinal) && c.Radius == ts.Radius); // 有危害的领地后面再接“· 危害：…”
            Expect(quietStart, "B0 负向：新战役开局 5 游戏秒内没有任何据点 / 侦察巢迎战（开局的家园机器与建筑不在任何据点的迎战范围里，不会自己打起来）");
            Expect(records && rs.Control == expected && label.Contains($"控制度 {expected}%") && label.Contains(GameText.Get("territory.status.enemy")) && mapShows,
                $"B1 FGR-EXP-022 控制度：四个阵营领地各一条记录（白潮预留区没有），开局 100%；摧毁 2 个静默据点后 = 剩余等级之和 ÷ 全部等级之和 = {expected}%（{rs.Control}%）；" +
                $"战略地图领地标签“{label}”（地图收集到的是同一条：{mapShows}）");

            CampaignState old = NewWorld(42, version: 2);
            WorldSimulation.StepMany(GameClock.StepHz + 2);
            string oldLabel = TerritoryControlService.MapLabel(old, WorldGenService.PlanFor(old).Find("silent"));
            Expect(oldLabel.Contains("待侦察") && TerritoryOutposts(old, "silent").Count == 0 && TerritoryControlService.StatusOf(old, "silent") == TerritoryControlService.StatusEnemy,
                $"B2 旧版本世界（v2，没有领地据点）：控制度无从计算，地图照旧写“{oldLabel}”，不会因为没有据点被当成已收复");
        }

        // ── C 迎战与摧毁 ─────────────────────────────────────────────────────────

        private static void CheckEngage()
        {
            CampaignState s = NewWorld(42);
            OutpostRecord o = NearestOutpost(s, "silent");
            WorldOutpostSystem.Peek(s, o, out int garrison, out _);
            int alerts = NotifyMembers(TerritoryControlService.NotifyAlert);
            int octants = s.Raids.Director.DestroyedByOctant.Sum();
            Vector2 at = new Vector2(o.CellX, o.CellY);
            int m = Spawn(s, Toward(s, at, 10f));
            bool engaged = RunUntil(() => o.Engaged, GameClock.StepHz * 8);
            CombatSite site = WorldSimulation.Home.Combat;
            int inKernel = engaged ? site.CountSiegeGroup(o.EngageKey) : 0;
            bool keyOk = o.EngageKey >= TerritoryControlService.KeyBase;
            SiegeService.Sync(s, site); // 攻城对账（含“没有主人的队伍”清理）：迎战中的据点是主人，不能被清掉
            int afterSync = site.CountSiegeGroup(o.EngageKey);
            Expect(m > 0 && engaged && o.UnfoldedCount == garrison && inKernel == garrison && keyOk && afterSync == inKernel && NotifyMembers(TerritoryControlService.NotifyAlert) > alerts && !o.Dormant,
                $"C1 迎战（DEBT-FG3GEN01-05）：机器开到静默据点 10 格内 → 据点醒来、驻军 {garrison} 台就地展开成家园战斗内核里的敌人（内核 {inKernel} 台，队伍键 {o.EngageKey}）；" +
                $"攻城对账不把它当孤儿清掉（{afterSync}）；交战通知；迎战期间不休眠");

            // FG8-LOOT-01 起野外击毁另按掉落表留残骸 / 材料（FieldLootService，前缀 fieldloot:，由 FgLootProtocolSelfCheck 断言）；这里只数据点本身的废料堆。
            int ground0 = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Count(g => !IsFieldLoot(g));
            int before = TerritoryControlService.Find(s, "silent").Control;
            KillGroup(site, at, o.EngageKey);
            bool destroyed = RunUntil(() => o.Destroyed, GameClock.StepHz * 3);
            GroundItemRecord loot = HomeValleyCargo.FindGroundItemBySalvageId(s, TerritoryControlService.LootPrefix + o.OutpostId + ":1");
            int scrap = Math.Max(1, o.Tier) * TerritoryControlService.ScrapPerTier;
            bool haul = loot != null && s.WorkOrders.Any(w => w.Kind == WorkOrderKind.Haul && w.TargetId == loot.GroundItemId);
            int after = TerritoryControlService.Find(s, "silent").Control;
            string detail = LatestMember(TerritoryControlService.NotifyAlert)?.DetailText ?? string.Empty;
            Expect(destroyed && loot != null && loot.Amount == scrap && Vector2.Distance(loot.Position, at) < 0.5f && haul && (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Count(g => !IsFieldLoot(g)) == ground0 + 1
                   && after < before && after == TerritoryControlService.ComputeControl(s, "silent", out _, out _) && detail.Contains($"{before}% → {after}%")
                   && s.Raids.Director.DestroyedByOctant.Sum() == octants + 1 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.WorldFirstOutpostDestroyed),
                $"C2 驻军全灭 = 据点被摧毁：废料 {loot?.Amount}（等级 × {TerritoryControlService.ScrapPerTier}）掉在据点原地并开搬运单（{haul}），控制度 {before}% → {after}%（通知“{detail}”），" +
                "突袭导演记下这个方向（摧毁据点降低该方向突袭频率），第一次摧毁的引导钩子");
            List<string> expectTypes = GarrisonEnemyTypes("silent").Take(Math.Max(1, garrison)).ToList(); // 编成循环取：驻军台数不少于种类数时每种都有
            string[] defeated = s.Research?.Range?.DefeatedEnemyTypes ?? Array.Empty<string>();
            Expect(TerritoryControlService.IsEngageKey(o.EngageKey) && expectTypes.Count > 0 && expectTypes.All(x => defeated.Contains(x)),
                $"C2b（复审 P1）迎战队伍键 {o.EngageKey} 落在击毁事件 float 精确范围内：打掉的驻军按种类记进“击败过的敌人种类”（{string.Join("、", expectTypes)} → 已记 {string.Join("、", defeated)}），对应靶子解锁（FGR-RND-030）");
        }

        private static bool IsFieldLoot(GroundItemRecord g) =>
            g?.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith(FieldLootService.SalvagePrefix, StringComparison.Ordinal);

        /// <summary>这片领地驻军编成里的敌人种类（fg.TbRaidUnit → EnemyTypeId）。</summary>
        private static List<string> GarrisonEnemyTypes(string territoryId)
        {
            var list = new List<string>();
            string units = TerritoryControlService.Row(territoryId)?.GarrisonUnits;
            foreach (string id in (units ?? string.Empty).Split(','))
            {
                if (RaidCatalog.TryGetUnit(id.Trim(), out RaidUnitDef d) && !string.IsNullOrEmpty(d.EnemyTypeId) && !list.Contains(d.EnemyTypeId))
                {
                    list.Add(d.EnemyTypeId);
                }
            }
            return list;
        }

        /// <summary>突袭导演的方位计数之和 = 当前处于摧毁状态的出发地据点数（精英残部不算）。</summary>
        private static bool OctantsMatchDestroyed(CampaignState s, out int sum, out int destroyed)
        {
            sum = s.Raids.Director.DestroyedByOctant.Sum();
            destroyed = WorldOutpostSystem.Outposts(s).Count(o => o.Destroyed && o.Kind != WorldOutpostSystem.KindRemnant);
            return sum == destroyed;
        }

        /// <summary>C5：家园区侦察巢默认不迎战（表开关 territory.engage.scout_nest_mode = 1，M1～M7 节奏不变）；开关打开时与领地据点同一套迎战。</summary>
        private static void CheckScoutNest()
        {
            bool OneRun(bool? flag, out int units)
            {
                TerritoryControlService.ScoutNestsEngageOverrideForTests = flag;
                try
                {
                    CampaignState s = NewWorld(42);
                    GridCell core = HomeGridService.CorePivot(s);
                    OutpostRecord nest = WorldOutpostSystem.Outposts(s).Where(o => o.Kind == WorldOutpostSystem.KindScoutNest && !o.Destroyed)
                        .OrderBy(o => Dist(new Vector2(o.CellX, o.CellY), core)).ThenBy(o => o.OutpostId, StringComparer.Ordinal).First();
                    Spawn(s, Toward(s, new Vector2(nest.CellX, nest.CellY), 6f));
                    bool engaged = RunUntil(() => nest.Engaged, GameClock.StepHz * 5);
                    units = engaged ? WorldSimulation.Home.Combat.CountSiegeGroup(nest.EngageKey) : 0;
                    return engaged;
                }
                finally
                {
                    TerritoryControlService.ScoutNestsEngageOverrideForTests = null;
                }
            }
            bool byDefault = OneRun(null, out _);
            bool whenOn = OneRun(true, out int n);
            Expect(!TerritoryControlService.ScoutNestsEngage && !byDefault && whenOn && n > 0,
                $"C5 家园区侦察巢：默认不迎战（机器走到 6 格内 5 秒也不展开：{!byDefault}，M1～M7 已验收的家园区节奏不变）；表开关打开时同一套迎战（{n} 台展开）——是否打开由 FG9-BAL-01 定（DEBT-FG8EXP03-07）");
        }

        private static void CheckRegroupAndPatrol()
        {
            CampaignState s = NewWorld(42);
            CombatSite site = WorldSimulation.Home.Combat;
            OutpostRecord o = NearestOutpost(s, "foundry");
            var at = new Vector2(o.CellX, o.CellY);
            int m = Spawn(s, Toward(s, at, 10f));
            bool engaged = RunUntil(() => o.Engaged, GameClock.StepHz * 8);
            int key = o.EngageKey;
            site.TryGetMachineUnit(m, out int unit);
            site.Kernel.SetPosition(unit, new double2(at.x + 250, at.y + 250)); // 机器走开（离开交战范围）
            int regroups = TerritoryControlService.RegroupCount;
            bool back = RunUntil(() => !o.Engaged, GameClock.TicksFor(TerritoryControlService.RegroupSeconds + 3));
            Expect(engaged && back && site.CountSiegeGroup(key) == 0 && o.Garrison >= 1 && !o.Destroyed && TerritoryControlService.RegroupCount == regroups + 1,
                $"C3 附近没有己方机器满 {TerritoryControlService.RegroupSeconds} 游戏秒：幸存者收拢回据点（驻军 {o.Garrison}，内核里没有这一组了），据点没被摧毁");

            // 巡逻：机器开到一支巡逻附近 → 巡逻迎战；全灭 = 歼灭，从巡逻列表里移除。
            PatrolRecord p = WorldOutpostSystem.Patrols(s).Where(x => TerritoryOutposts(s, "silent").Any(y => y.OutpostId == x.OutpostId))
                .OrderBy(x => Dist(WorldOutpostSystem.PeekPatrolPosition(s, x), HomeGridService.CorePivot(s))).First();
            // 先让一台机器停在据点 60 格外（把据点唤醒、巡逻拿到路线开始走，但不在迎战范围里），再把第二台开到巡逻此刻的位置旁。
            OutpostRecord po = WorldOutpostSystem.Find(s, p.OutpostId);
            Spawn(s, Toward(s, new Vector2(po.CellX, po.CellY), 60f));
            bool routed = RunUntil(() => !po.Dormant && p.RouteState == WorldOutpostSystem.RouteReady, GameClock.StepHz * 10);
            Vector2 pp = new Vector2((float)p.PosX, (float)p.PosY);
            int m2 = Spawn(s, Toward(s, pp, 4f));
            bool pe = routed && RunUntil(() => p.Engaged, GameClock.StepHz * 6);
            int units = pe ? site.CountSiegeGroup(p.EngageKey) : 0;
            var ppos = new Vector2((float)p.PosX, (float)p.PosY);
            KillGroup(site, ppos, p.EngageKey);
            string pid = p.PatrolId;
            bool gone = RunUntil(() => WorldOutpostSystem.Patrols(s).All(x => x.PatrolId != pid), GameClock.StepHz * 3);
            Expect(m2 > 0 && routed && pe && units == p.UnitCount && gone,
                $"C4 巡逻迎战：机器靠近巡逻 → {units} 台就地展开；全灭后这支巡逻从列表里移除（歼灭）");
        }

        // ── D 阵营增援 ───────────────────────────────────────────────────────────

        private static string RunWave(int seed, Func<CampaignState, List<OutpostRecord>, Vector2?> machineAt, out CampaignState state, out long nextAfter, out long nextBefore)
        {
            CampaignState s = NewWorld(seed);
            state = s;
            List<OutpostRecord> silent = TerritoryOutposts(s, "silent");
            foreach (OutpostRecord o in silent)
            {
                WorldOutpostSystem.MarkDestroyed(s, o.OutpostId);
            }
            Vector2? near = machineAt?.Invoke(s, silent);
            if (near.HasValue)
            {
                Spawn(s, near.Value);
            }
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            TerritoryControlRecord r = TerritoryControlService.Find(s, "silent");
            r.NextReinforceTick = GameClock.Ticks + 1;
            nextBefore = r.NextReinforceTick;
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            nextAfter = r.NextReinforceTick;
            return string.Join(",", silent.Where(o => !o.Destroyed).Select(o => o.OutpostId));
        }

        /// <summary>按与服务相同的确定性规则算出“这一次哪个据点该重建”（自检复算，不调服务的私有方法）。</summary>
        private static string ExpectedRebuild(CampaignState s, int wave, Func<OutpostRecord, bool> blocked)
        {
            WorldPlan plan = WorldGenService.PlanFor(s);
            int tIndex = plan.Territories.FindIndex(x => x.Id == "silent");
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            foreach (OutpostRecord o in TerritoryOutposts(s, "silent").OrderBy(x => x.OutpostId, StringComparer.Ordinal))
            {
                uint h = WorldGenMath.Hash(seed, tIndex * 1000 + wave, TerritoryControlService.StableHash(o.OutpostId), 0x5245494Eu);
                if ((h & 0xFFFFFF) / (double)0x1000000 < TerritoryControlService.RebuildChance && !blocked(o))
                {
                    return o.OutpostId;
                }
            }
            return string.Empty;
        }

        private static void CheckReinforce()
        {
            int statusBefore = NotifyMembers(TerritoryControlService.NotifyStatus);
            string rebuilt = RunWave(42, null, out CampaignState s, out long next, out long nextBefore);
            TerritoryControlRecord r = TerritoryControlService.Find(s, "silent");
            string expect = ExpectedRebuild(s, r.Reinforcements, _ => false);
            OutpostRecord o = WorldOutpostSystem.Find(s, rebuilt);
            bool shape = o != null && o.Garrison == Math.Max(1, Mathf.RoundToInt(o.GarrisonCap * TerritoryControlService.RebuildGarrisonRatio)) && o.Dormant && o.RebuildCount == 1
                         && o.NextReinforceTick > GameClock.Ticks - TerritoryControlService.SyncTicks - 2 && o.NextReinforceTick % o.ReinforceIntervalTicks == 0;
            bool timing = next == nextBefore + TerritoryControlService.ReinforceTicks && r.Reinforcements == 1;
            int ctrl = TerritoryControlService.ComputeControl(s, "silent", out _, out _);
            Expect(rebuilt.Length > 0 && !rebuilt.Contains(",") && rebuilt == expect && shape && timing && r.Control == ctrl && ctrl > 0
                   && NotifyMembers(TerritoryControlService.NotifyStatus) > statusBefore,
                $"D1 FGR-EXP-022 敌控时阵营增援：全部静默据点被摧毁后，到增援点重建 1 个（{rebuilt}，按（种子, 领地, 第几次, 据点）确定、与复算一致：{expect}），" +
                $"驻军为上限的一半、接回原增援网格、按休眠处理（{shape}）；下一次增援在 {TerritoryControlService.ReinforceDays} 天后（{timing}）；控制度回到 {ctrl}%；通知");

            string again = RunWave(42, null, out _, out _, out _);
            Expect(again == rebuilt, $"D2 同一种子两次开局，同一次增援重建的是同一个据点（{again}）——确定性（B25）");

            // 玩家附近不重建：在“本该重建”的据点旁放一台机器 → 它不重建，换下一个合格的。
            string blockedId = rebuilt;
            string third = RunWave(42, (st, list) =>
            {
                OutpostRecord b = list.First(x => x.OutpostId == blockedId);
                return new Vector2(b.CellX + 6, b.CellY + 6);
            }, out CampaignState s3, out _, out _);
            OutpostRecord blockedRec = WorldOutpostSystem.Find(s3, blockedId);
            string expect3 = ExpectedRebuild(s3, 1, x => Vector2.Distance(new Vector2(x.CellX, x.CellY), new Vector2(blockedRec.CellX, blockedRec.CellY)) <= TerritoryControlService.RebuildBlockRadius);
            Expect(blockedRec.Destroyed && third == expect3 && !third.Contains(blockedId),
                $"D3 负向：据点 {TerritoryControlService.RebuildBlockRadius} 格内有己方机器时不重建（不在玩家眼前凭空出现）——{blockedId} 仍是废墟，改为重建 {(third.Length > 0 ? third : "（无）")}");

            // 重建的据点补巡逻：生成时带巡逻、巡逻已被歼灭的，重建时重新派一支。
            CampaignState w = NewWorld(42);
            OutpostRecord withPatrol = TerritoryOutposts(w, "silent").First(x => x.PatrolRadius > 0);
            WorldOutpostSystem.MarkDestroyed(w, withPatrol.OutpostId);
            WorldOutpostSystem.RemovePatrols(w, x => x.OutpostId == withPatrol.OutpostId);
            bool rb = WorldOutpostSystem.Rebuild(w, withPatrol.OutpostId, 3);
            PatrolRecord again4 = WorldOutpostSystem.Patrols(w).FirstOrDefault(x => x.OutpostId == withPatrol.OutpostId);
            double turn = again4 != null ? Math.Sqrt(Math.Pow(again4.TurnX - withPatrol.CellX, 2) + Math.Pow(again4.TurnY - withPatrol.CellY, 2)) : -1;
            Expect(rb && !withPatrol.Destroyed && WorldOutpostSystem.Patrols(w).Count(x => x.OutpostId == withPatrol.OutpostId) == 1 && Math.Abs(turn - withPatrol.PatrolRadius) <= 1.5,
                $"D4 重建带巡逻的据点（巡逻折返 {withPatrol.PatrolRadius} 格）：巡逻已被歼灭时重新派一支，折返点离据点 {turn:0.0} 格（与初次生成同一半径）");

            // D5（复审 P1）：摧毁 → 重建 → 再摧毁，突袭导演的方位计数不会无上限增长（= 当前处于摧毁状态的出发地据点数）。
            bool okA = OctantsMatchDestroyed(w, out int sumA, out _);
            WorldOutpostSystem.MarkDestroyed(w, withPatrol.OutpostId);
            bool okB = OctantsMatchDestroyed(w, out int sumB, out _);
            WorldOutpostSystem.Rebuild(w, withPatrol.OutpostId, 3);
            WorldOutpostSystem.MarkDestroyed(w, withPatrol.OutpostId);
            bool okC = OctantsMatchDestroyed(w, out int sumC, out int destroyedC);
            bool okD1 = OctantsMatchDestroyed(s, out int sumD, out int destroyedD);
            Expect(okA && okB && okC && okD1 && sumB == sumA + 1 && sumC == sumB,
                $"D5 负向“摧毁 → 重建 → 再摧毁”：方位计数 {sumA} → 摧毁 {sumB} → 重建再摧毁 {sumC}（= 摧毁状态的据点 {destroyedC} 个，不重复累加）；D1 的世界里 {sumD} = {destroyedD}");

            // D6（复审 P2）：旧档据点（PatrolRadius = 0）补域时按现存巡逻补回半径；没有巡逻的记 −1。
            OutpostRecord legacy = WorldOutpostSystem.Outposts(w).FirstOrDefault(x => x.Kind == WorldOutpostSystem.KindTerritory && x.OutpostId != withPatrol.OutpostId && x.PatrolRadius > 0
                                                                                      && WorldOutpostSystem.Patrols(w).Any(p => p.OutpostId == x.OutpostId));
            OutpostRecord legacyNo = WorldOutpostSystem.Outposts(w).FirstOrDefault(x => x.PatrolRadius < 0);
            int want = legacy?.PatrolRadius ?? -2;
            if (legacy != null)
            {
                legacy.PatrolRadius = 0;
            }
            if (legacyNo != null)
            {
                legacyNo.PatrolRadius = 0;
            }
            CampaignFgStateDomains.EnsureAll(w);
            Expect(legacy != null && Math.Abs(legacy.PatrolRadius - want) <= 1 && (legacyNo == null || legacyNo.PatrolRadius == -1),
                $"D6 旧档（FG8-GEN-02 时期的据点没有巡逻半径）：读档补域后按巡逻折返点补回 {legacy?.PatrolRadius} 格（原 {want}），没有巡逻的记 −1——巡逻被歼灭后重建照样补派");
        }

        // ── E 首领 → 平定 ────────────────────────────────────────────────────────

        private static void CheckPacify()
        {
            CampaignState s = NewWorld(42);
            WorldSimulation.StepMany(GameClock.StepHz + 2);
            int status0 = NotifyMembers(TerritoryControlService.NotifyStatus);
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            FoundryOutpostRegion.Find(s).CoreState = CoreBossState.Destroyed.ToString(); // 主核心被击败（Demo 铸造首领的正式状态）
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            TerritoryControlRecord f = TerritoryControlService.Find(s, "foundry");
            List<OutpostRecord> foundry = TerritoryOutposts(s, "foundry");
            string detail = LatestMember(TerritoryControlService.NotifyStatus)?.DetailText ?? string.Empty;
            Expect(f.Status == TerritoryControlService.StatusPacifying && f.LeaderDefeated && foundry.All(o => o.ReinforceStopped) && TerritoryControlService.StatusOf(s, "silent") == TerritoryControlService.StatusEnemy
                   && NotifyMembers(TerritoryControlService.NotifyStatus) > status0 && detail.Contains(GameText.Get("territory.leader.foundry")) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.WorldFirstTerritoryPacified),
                $"E1 首领被击败（铸造主核心，与突袭导演 / 情报同一判定）→ 铸造领地进入平定：{foundry.Count} 个据点全部停止增援，静默照旧敌控；通知“{detail}”");

            foreach (OutpostRecord o in foundry.Take(3))
            {
                WorldOutpostSystem.MarkDestroyed(s, o.OutpostId);
            }
            int reinf = f.Reinforcements;
            f.NextReinforceTick = GameClock.Ticks + 1;
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            Expect(f.Reinforcements == reinf && foundry.Take(3).All(o => o.Destroyed),
                "E2 平定后不再增援：到了增援点也不重建被摧毁的据点");

            // 首领 Story 的入口 + 休眠补算一致：两个同样的测试据点（增援间隔 0.5 秒），一个一直模拟、一个休眠；平定后都不再增长，任何时候看都相同。
            PlannedTerritory st = WorldGenService.PlanFor(s).Find("silent");
            OutpostRecord twinA = WorldOutpostSystem.SpawnOutpost(s, "silent", new GridCell(st.CenterX, st.CenterY), false, 30);
            OutpostRecord twinB = WorldOutpostSystem.SpawnOutpost(s, "silent", new GridCell(st.CenterX + 3, st.CenterY), false, 30);
            foreach (OutpostRecord tw in new[] { twinA, twinB })
            {
                tw.Kind = WorldOutpostSystem.KindTerritory;
                tw.Tier = 1;
                tw.Garrison = 1;
                tw.GarrisonCap = 40;
            }
            twinA.AlwaysSimulate = true;
            WorldSimulation.StepMany(GameClock.StepHz * 4);
            bool dormant = twinB.Dormant;
            bool first = TerritoryControlService.MarkLeaderDefeated(s, "silent");
            WorldOutpostSystem.Peek(s, twinA, out int ga, out _);
            WorldOutpostSystem.Peek(s, twinB, out int gb, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 4);
            WorldOutpostSystem.Peek(s, twinA, out int ga2, out _);
            WorldOutpostSystem.Peek(s, twinB, out int gb2, out _);
            bool second = TerritoryControlService.MarkLeaderDefeated(s, "silent");
            Expect(first && !second && dormant && ga == gb && ga > 1 && ga2 == ga && gb2 == gb && TerritoryControlService.StatusOf(s, "silent") == TerritoryControlService.StatusPacifying,
                $"E3 首领 Story 入口 MarkLeaderDefeated（寂听主脑由 FG9-FAC-01 调用）：静默进入平定（幂等）；平定前一直模拟 / 休眠的两个据点驻军相同（{ga} = {gb}），" +
                $"平定后 4 秒都不再增长（{ga2} / {gb2}）——休眠补算与一直模拟逐位一致（FGR-BASE-021）");
        }

        // ── F / G / I / J 收复、精英残部、净化、存读档 ─────────────────────────────────

        private static void CheckReclaimEliteHarassPurify()
        {
            // 负向：首领还在时，据点打到控制度 20 以下也不收复（只有平定中的领地才会收复）。
            CampaignState n = NewWorld(42);
            DestroyBelow(n, "silent", 15);
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            Expect(TerritoryControlService.StatusOf(n, "silent") == TerritoryControlService.StatusEnemy && TerritoryControlService.Find(n, "silent").Control < 20,
                $"F1 负向：首领被击败之前，控制度降到 {TerritoryControlService.Find(n, "silent").Control}% 也不收复（阵营还会增援）");

            CampaignState s = NewWorld(42);
            WorldSimulation.StepMany(GameClock.StepHz + 2);
            TerritoryControlService.MarkLeaderDefeated(s, "silent");
            int otherPatrols = WorldOutpostSystem.Patrols(s).Count(p => !OfTerritory(s, p, "silent"));
            int silentPatrols = WorldOutpostSystem.Patrols(s).Count(p => OfTerritory(s, p, "silent"));
            string memo = null;
            Action<string> listener = id => memo = id;
            GameEvent.AddEventListener<string>(PurificationService.CoreMemoEvent, listener);
            int memos = NotifyMembers("core_memo");
            List<OutpostRecord> left;
            try
            {
                left = DestroyBelow(s, "silent", 19);
                WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            }
            finally
            {
                GameEvent.RemoveEventListener<string>(PurificationService.CoreMemoEvent, listener);
            }
            TerritoryControlRecord r = TerritoryControlService.Find(s, "silent");
            string detail = LatestMember(TerritoryControlService.NotifyStatus)?.DetailText ?? string.Empty;
            string label = TerritoryControlService.MapLabel(s, WorldGenService.PlanFor(s).Find("silent"));
            Expect(r.Status == TerritoryControlService.StatusReclaimed && silentPatrols > 0 && WorldOutpostSystem.Patrols(s).Count(p => OfTerritory(s, p, "silent")) == 0
                   && WorldOutpostSystem.Patrols(s).Count(p => !OfTerritory(s, p, "silent")) == otherPatrols && detail.Contains("已收复") && NotifyMembers("core_memo") > memos
                   && memo == TerritoryControlService.MemoPrefix + "silent" && label.Contains(GameText.Get("territory.status.reclaimed")) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.WorldFirstTerritoryReclaimed),
                $"F2 平定中控制度降到 {r.Control}%（< {TerritoryControlService.ReclaimBelow}）→ 已收复：静默的 {silentPatrols} 支巡逻撤离、其他领地的 {otherPatrols} 支不动；通知“{detail}”+ 核心备忘（GameEvent {memo}）；地图“{label}”");

            var stats = new List<(string Text, string Cls)>();
            TerritoryControlService.BuildStatsLines(s, stats);
            string silentRow = stats.Select(x => x.Text).FirstOrDefault(x => x.StartsWith(GameText.Get(WorldGenService.PlanFor(s).Find("silent").NameKey))) ?? string.Empty;
            Expect(stats.Count >= 3 && stats[0].Text == GameText.Get("stats.territory.title") && silentRow.Contains(GameText.Get("territory.status.reclaimed")) && silentRow.Contains($"控制度 {r.Control}%")
                   && stats.Any(x => x.Text.StartsWith("累计：")),
                $"F2b B19 统计面板“领地”段：“{silentRow}”、累计行（{stats.LastOrDefault().Text}）");

            // 负向“平定后还有据点残留”：剩下的据点照常迎战、能被摧毁，领地状态不回退。
            OutpostRecord rest = left.OrderBy(x => Dist(new Vector2(x.CellX, x.CellY), HomeGridService.CorePivot(s))).First();
            var at = new Vector2(rest.CellX, rest.CellY);
            Spawn(s, Toward(s, at, 10f));
            bool eng = RunUntil(() => rest.Engaged, GameClock.StepHz * 8);
            KillGroup(WorldSimulation.Home.Combat, at, rest.EngageKey);
            bool gone = RunUntil(() => rest.Destroyed, GameClock.StepHz * 3);
            Expect(left.Count > 0 && eng && gone && r.Status == TerritoryControlService.StatusReclaimed,
                $"F3 负向：收复后还残留 {left.Count} 个据点——它们照常迎战（{eng}）、能被摧毁（{gone}），领地仍是已收复");

            // G 精英残部：7 天一批、不叠加、击溃掉落、首领不再刷新。
            int ctrl = TerritoryControlService.ComputeControl(s, "silent", out _, out _);
            long before = GameClock.Ticks;
            r.NextEliteTick = GameClock.Ticks + 1;
            long scheduled = r.NextEliteTick;
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            OutpostRecord rem = WorldOutpostSystem.Find(s, r.RemnantId);
            GameConfig.fg.TerritoryControl row = TerritoryControlService.Row("silent");
            Expect(rem != null && rem.Kind == WorldOutpostSystem.KindRemnant && rem.Garrison == row.EliteCount && rem.ReinforceStopped && r.NextEliteTick == scheduled + TerritoryControlService.EliteTicks
                   && TerritoryControlService.ComputeControl(s, "silent", out _, out _) == ctrl && GameSettings.HasSeenGuidanceHook(GuidanceHooks.WorldFirstEliteRemnant)
                   && TerritoryOutposts(s, "silent").All(o => o.Kind == WorldOutpostSystem.KindTerritory),
                $"G1 已收复领地里精英残部刷新：{rem?.OutpostId}（{rem?.Garrison} 台精英，不算控制度、不增援），下一批在 {TerritoryControlService.EliteDays} 天后");

            string firstId = r.RemnantId;
            r.NextEliteTick = GameClock.Ticks + 1;
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            Expect(r.RemnantId == firstId && WorldOutpostSystem.Outposts(s).Count(o => o.Kind == WorldOutpostSystem.KindRemnant) == 1,
                "G2 上一批还没被击溃时到了刷新点：不叠加第二批");

            var ra = new Vector2(rem.CellX, rem.CellY);
            Spawn(s, Toward(s, ra, 10f));
            bool re = RunUntil(() => rem.Engaged, GameClock.StepHz * 8);
            int remUnits = re ? WorldSimulation.Home.Combat.CountSiegeGroup(rem.EngageKey) : 0;
            KillGroup(WorldSimulation.Home.Combat, ra, rem.EngageKey);
            bool routed = RunUntil(() => rem.Destroyed, GameClock.StepHz * 3);
            GroundItemRecord loot = HomeValleyCargo.FindGroundItemBySalvageId(s, TerritoryControlService.LootPrefix + rem.OutpostId + ":1");
            r.NextEliteTick = GameClock.Ticks + 1;
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            bool octOk = OctantsMatchDestroyed(s, out int octSum, out _);
            Expect(re && remUnits == row.EliteCount && routed && loot != null && loot.Amount == row.EliteScrap && r.RemnantId != firstId && r.EliteWaves == 2
                   && r.LeaderDefeated && r.Status == TerritoryControlService.StatusReclaimed && octOk && rem.RaidOctant < 0,
                $"G3 击溃精英残部（{remUnits} 台精英迎战）：废料 {loot?.Amount} 掉在原地（可以用来刷材料）；下一个刷新点出新的一批（{r.RemnantId}）；首领不再刷新、领地仍是已收复；" +
                $"负向（复审 P1）：残部不是突袭出发地，击溃它不降低突袭频率（方位计数 {octSum} = 摧毁状态的出发地据点数）");

            // H 负向（收复后）：领地里有己方建筑也不再派骚扰。
            PlannedTerritory t = WorldGenService.PlanFor(s).Find("silent");
            GridCell bc = FreeCellNear(s, new GridCell(rest.CellX + 30, rest.CellY), "power_pole");
            F.Built(s, "power_pole", "tcl_reclaimed_a", bc);
            F.Built(s, "power_pole", "tcl_reclaimed_b", FreeCellNear(s, new GridCell(bc.X + 3, bc.Y), "power_pole"));
            int groups = WorldTransitSystem.Groups(s).Count;
            r.NextHarassTick = GameClock.Ticks + 1;
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            Expect(WorldTransitSystem.Groups(s).Count == groups && Dist(new Vector2(bc.X, bc.Y), new GridCell(t.CenterX, t.CenterY)) <= t.Radius,
                "H3 收复后在这片领地里建设：不再被据点骚扰（敌方不再派兵）");

            // I 收复后的土地净化后计入归还面积；化工突袭再污染后领地状态不回退、净化塔再净化回来。
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell? dirty = PollutedCell(s, map, t);
            if (dirty == null)
            {
                Fail("I1 静默领地里找不到有污染的格子（无法验证净化）");
            }
            else
            {
                GridCell pivot = FreeCellNear(s, dirty.Value, "purifier");
                BuildingRecord tower = F.Built(s, "purifier", "tcl_purifier", pivot);
                long area0 = PurificationService.ReturnedArea(s);
                int lowered = 0;
                for (int k = 0; k < 40 && PurificationService.ReturnedArea(s) <= area0; k++)
                {
                    lowered += PurificationService.PurifyStep(s, tower, null);
                }
                long area1 = PurificationService.ReturnedArea(s);
                RepolluteResult rr = PurificationService.Repollute(s, pivot, 2, 2);
                long area2 = PurificationService.ReturnedArea(s);
                int again2 = 0;
                for (int k = 0; k < 40 && PurificationService.ReturnedArea(s) < area1; k++)
                {
                    again2 += PurificationService.PurifyStep(s, tower, null);
                }
                WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
                Expect(area1 > area0 && rr.CellsRaised > 0 && area2 < area1 && PurificationService.ReturnedArea(s) >= area1 && r.Status == TerritoryControlService.StatusReclaimed,
                    $"I1 FGR-ENV-042：在已收复的领地里净化（降了 {lowered} 格次），归还面积 {area0} → {area1}；负向“收复后又被化工突袭再污染”：再污染 {rr.CellsRaised} 格 → {area2}，" +
                    $"领地仍是已收复，净化塔再净化回来（{PurificationService.ReturnedArea(s)}）");
            }

            // J 存读档：领地记录、被摧毁 / 残留的据点、撤离后的巡逻、精英残部，以及一处正在迎战的据点（内核单位随快照）。
            OutpostRecord fo = NearestOutpost(s, "foundry");
            var fa = new Vector2(fo.CellX, fo.CellY);
            Spawn(s, Toward(s, fa, 10f));
            bool fe = RunUntil(() => fo.Engaged, GameClock.StepHz * 8);
            int units0 = WorldSimulation.Home.Combat.CountSiegeGroup(fo.EngageKey);
            string snap = Snapshot(s);
            SaveResult sv = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            CampaignState back = Reload(Slot);
            OutpostRecord fo2 = back != null ? WorldOutpostSystem.Find(back, fo.OutpostId) : null;
            int units1 = fo2 != null ? WorldSimulation.Home.Combat.CountSiegeGroup(fo2.EngageKey) : -1;
            string snap2 = back != null ? Snapshot(back) : string.Empty;
            WorldSimulation.StepMany(GameClock.StepHz);
            Expect(sv.Success && fe && back != null && snap2 == snap && fo2 != null && fo2.Engaged && units1 == units0 && units0 > 0 && fo2.Engaged == WorldOutpostSystem.Find(back, fo.OutpostId).Engaged,
                $"J1 B10 存读档（FGR-GEN-060：领地状态随存档的领地记录 + 据点记录走，地形 / 污染走区块差异）：领地记录、据点、巡逻、精英残部逐字段一致（{snap.Length} 字符快照）；" +
                $"正在迎战的据点读回仍在迎战，内核里 {units1} 台（存档时 {units0} 台）");
        }

        /// <summary>新世界里在静默领地（敌控）建两座电线杆，到骚扰检查点派出一支骚扰部队。返回那支队伍（没派出 = null）。</summary>
        private static TransitGroupRecord SendHarass(out CampaignState s, out TerritoryControlRecord r, out GridCell c0, out bool allowed, out string verdictText,
            out int alerts, out int groups0)
        {
            s = NewWorld(42);
            WorldSimulation.StepMany(GameClock.StepHz + 2);
            OutpostRecord o = NearestOutpost(s, "silent");
            PlannedTerritory t = WorldGenService.PlanFor(s).Find("silent");
            // 在敌控领地里建设是允许的（建造校验不拦）。
            var towardCenter = new Vector2(t.CenterX - o.CellX, t.CenterY - o.CellY).normalized;
            var site0 = new GridCell(o.CellX + Mathf.RoundToInt(towardCenter.x * 35), o.CellY + Mathf.RoundToInt(towardCenter.y * 35));
            HomeGridService.RevealArea(s, new Vector2(site0.X, site0.Y), 40f); // 玩家已经探索到这里（迷雾里本来就不能建造）
            c0 = FreeCellNear(s, site0, "power_pole");
            var verdict = HomeGridService.ValidatePlacement(s, "power_pole", c0, 0, asPlayerPlacement: false, checkCost: false);
            allowed = verdict.Ok;
            verdictText = allowed ? string.Empty : "：" + string.Join("、", verdict.Reasons.Select(x => x.Code.ToString()));
            F.Built(s, "power_pole", "tcl_enemy_a", c0);
            F.Built(s, "power_pole", "tcl_enemy_b", FreeCellNear(s, new GridCell(c0.X + 3, c0.Y), "power_pole"));
            WorldSimulation.StepMany(GameClock.StepHz * 2); // 机器 / 建筑把据点唤醒（补算到现在）
            r = TerritoryControlService.Find(s, "silent");
            alerts = NotifyMembers(TerritoryControlService.NotifyAlert);
            groups0 = WorldTransitSystem.Groups(s).Count;
            r.NextHarassTick = GameClock.Ticks + 1;
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            string gid = r.HarassGroupId;
            return WorldTransitSystem.Groups(s).FirstOrDefault(x => x.GroupId == gid);
        }

        private static void CheckHarassInEnemyTerritory()
        {
            TransitGroupRecord g = SendHarass(out CampaignState s, out TerritoryControlRecord r, out GridCell c0, out bool allowed, out string verdictText, out int alerts, out int groups0);
            int waves0 = RaidDirectorService.ArrivedWaves(s);
            OutpostRecord origin = g != null ? WorldOutpostSystem.Find(s, g.OutpostId) : null;
            bool sent = g != null && origin != null && origin.Kind == WorldOutpostSystem.KindTerritory && origin.TerritoryId == "silent" && g.TargetKind == RaidDirectorService.TargetOutpost
                        && Vector2.Distance(new Vector2((float)g.TargetX, (float)g.TargetY), new Vector2(c0.X, c0.Y)) < 6f && g.GarrisonTaken == g.UnitCount && g.UnitCount >= 1
                        && NotifyMembers(TerritoryControlService.NotifyAlert) > alerts && GameSettings.HasSeenGuidanceHook(GuidanceHooks.WorldFirstTerritoryHarassed)
                        && g.UnitIds.Length > 0 && g.UnitIds.All(id => id.StartsWith("silent."));
            r.NextHarassTick = GameClock.Ticks + 1;
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            bool single = WorldTransitSystem.Groups(s).Count == groups0 + 1;
            bool attacked = g != null && RunUntil(() => g.Engaged || g.State == TransitGroupState.Arrived, GameClock.StepHz * 120);
            int waves1 = RaidDirectorService.ArrivedWaves(s);
            Expect(allowed && sent && single && attacked && g.Harass && waves1 == waves0,
                $"H1 负向“玩家在敌控领地里建设”：建造校验允许（{allowed}{verdictText}）；到骚扰检查点，离建筑群最近的静默据点 {origin?.OutpostId} 派出 {g?.UnitCount} 台（静默编成）去攻击它（{sent}）；" +
                $"上一支还在途中时不再派第二支（{single}）；部队沿地形走到建筑群并展开攻城（{attacked}）；骚扰到达不计突袭波次、不占“前 3 次突袭自动暂停”名额（已到达波次 {waves0} → {waves1}）");

            // H4（复审 P2）：收复那一刻还在路上的骚扰部队撤回，不再多打一次。
            TransitGroupRecord g2 = SendHarass(out CampaignState s2, out TerritoryControlRecord r2, out _, out _, out _, out _, out _);
            bool marching = g2 != null && g2.State == TransitGroupState.Marching && !g2.Engaged;
            int recalled = TerritoryControlService.HarassRecalled;
            TerritoryControlService.MarkLeaderDefeated(s2, "silent");
            DestroyBelow(s2, "silent", 19);
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            bool back = g2 != null && g2.State == TransitGroupState.Retreating && !g2.Engaged;
            Expect(marching && r2.Status == TerritoryControlService.StatusReclaimed && back && TerritoryControlService.HarassRecalled == recalled + 1,
                $"H4 负向“收复时骚扰部队还在路上”：领地一收复，在途的骚扰部队（{g2?.GroupId}）就沿原路撤回出发据点（状态 {g2?.State}），不会到达后再攻一次");
        }

        // ── N 种子测试集（B25 / FG17，复审 P1）──────────────────────────────────────

        /// <summary>
        /// 种子测试集前 4 个种子各走一遍主链：每个阵营领地有记录、开局 100%、静默至少 2 个领地据点 → 迎战展开 → 全灭摧毁、控制度按公式下降、敌人种类记进靶场 →
        /// 敌控增援按确定性规则重建（与复算一致、玩家附近不重建）→ 首领 → 平定 → 收复 → 精英残部落在敌方可走格附近、迎战、击溃；全程方位计数 = 摧毁状态的出发地据点数。
        /// </summary>
        private static void CheckSeedSet()
        {
            int[] seeds = FgWorldGenHomeSelfCheck.SeedSet.Take(4).ToArray();
            var bad = new List<string>();
            var ok = new List<string>();
            foreach (int seed in seeds)
            {
                string why;
                string info = string.Empty;
                try
                {
                    why = SeedChain(seed, out info);
                }
                catch (Exception e)
                {
                    why = "抛异常：" + e.Message;
                }
                if (why != null)
                {
                    bad.Add($"{seed}：{why}");
                }
                else
                {
                    ok.Add($"{seed}（{info}）");
                }
            }
            Expect(seeds.Length == 4 && bad.Count == 0,
                $"N1 B25 种子测试集 {string.Join(" / ", seeds)}：控制度、迎战到摧毁、增援重建、平定到收复、精英残部这条主链每个种子都成立——" +
                string.Join("；", ok) + (bad.Count == 0 ? string.Empty : "；失败：" + string.Join("；", bad)));
        }

        /// <summary>一个种子的主链；成立返回 null，否则返回第一处不成立的原因。</summary>
        private static string SeedChain(int seed, out string info)
        {
            info = string.Empty;
            CampaignState s = NewWorld(seed);
            WorldSimulation.StepMany(GameClock.StepHz + 2);
            WorldPlan plan = WorldGenService.PlanFor(s);
            List<string> factions = plan.Territories.Where(x => x.IsFaction).Select(x => x.Id).ToList();
            TerritoryControlRecord[] recs = TerritoryControlService.StateOf(s)?.Records ?? Array.Empty<TerritoryControlRecord>();
            if (factions.Count < 4 || recs.Length != factions.Count)
            {
                return $"阵营领地 {factions.Count} 个、领地记录 {recs.Length} 条";
            }
            foreach (string id in factions)
            {
                TerritoryControlRecord rec = TerritoryControlService.Find(s, id);
                if (rec == null || rec.Control != 100 || rec.Status != TerritoryControlService.StatusEnemy || TerritoryOutposts(s, id).Count == 0)
                {
                    return $"{id} 开局：记录 {rec != null}、控制度 {rec?.Control}、领地据点 {TerritoryOutposts(s, id).Count} 个";
                }
            }
            int silentCount = TerritoryOutposts(s, "silent").Count;
            if (silentCount < 2)
            {
                return $"静默领地据点只有 {silentCount} 个";
            }

            // 迎战 → 摧毁 → 控制度 → 靶场种类。
            OutpostRecord o = NearestOutpost(s, "silent");
            WorldOutpostSystem.Peek(s, o, out int garrison, out _);
            var at = new Vector2(o.CellX, o.CellY);
            int before = TerritoryControlService.Find(s, "silent").Control;
            if (Spawn(s, Toward(s, at, 10f)) <= 0)
            {
                return "据点旁放不下机器";
            }
            if (!RunUntil(() => o.Engaged, GameClock.StepHz * 8))
            {
                return $"机器开到 {o.OutpostId} 10 格内不迎战";
            }
            CombatSite site = WorldSimulation.Home.Combat;
            int units = site.CountSiegeGroup(o.EngageKey);
            if (units != garrison || !TerritoryControlService.IsEngageKey(o.EngageKey))
            {
                return $"展开 {units} 台 / 驻军 {garrison}，队伍键 {o.EngageKey}";
            }
            KillGroup(site, at, o.EngageKey);
            if (!RunUntil(() => o.Destroyed, GameClock.StepHz * 3))
            {
                return "驻军全灭后据点没被摧毁";
            }
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            int after = TerritoryControlService.Find(s, "silent").Control;
            if (after >= before || after != TerritoryControlService.ComputeControl(s, "silent", out _, out _))
            {
                return $"控制度 {before}% → {after}%（公式 {TerritoryControlService.ComputeControl(s, "silent", out _, out _)}%）";
            }
            List<string> types = GarrisonEnemyTypes("silent").Take(Math.Max(1, garrison)).ToList();
            string[] defeated = s.Research?.Range?.DefeatedEnemyTypes ?? Array.Empty<string>();
            if (types.Count == 0 || !types.All(x => defeated.Contains(x)))
            {
                return $"击败的敌人种类没记下（应有 {string.Join("、", types)}，记了 {string.Join("、", defeated)}）";
            }
            if (!OctantsMatchDestroyed(s, out int sum1, out int dest1))
            {
                return $"摧毁后方位计数 {sum1} ≠ 摧毁状态据点 {dest1}";
            }

            // 敌控增援：全部静默据点被摧毁 → 到增援点按确定性规则重建（玩家附近的不重建）。
            foreach (OutpostRecord x in TerritoryOutposts(s, "silent"))
            {
                WorldOutpostSystem.MarkDestroyed(s, x.OutpostId);
            }
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            TerritoryControlRecord r = TerritoryControlService.Find(s, "silent");
            var own = new List<Vector2>();
            WorldOutpostSystem.CollectOwnPositions(s, own);
            float block = TerritoryControlService.RebuildBlockRadius;
            r.NextReinforceTick = GameClock.Ticks + 1;
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            string expect = ExpectedRebuild(s, r.Reinforcements, x => own.Any(p => Vector2.Distance(p, new Vector2(x.CellX, x.CellY)) <= block));
            string rebuilt = string.Join(",", TerritoryOutposts(s, "silent").Where(x => !x.Destroyed).Select(x => x.OutpostId));
            if (rebuilt != expect || r.Control != TerritoryControlService.ComputeControl(s, "silent", out _, out _))
            {
                return $"增援重建 {rebuilt}（复算 {expect}），控制度 {r.Control}%";
            }
            if (!OctantsMatchDestroyed(s, out int sum2, out int dest2))
            {
                return $"重建后方位计数 {sum2} ≠ 摧毁状态据点 {dest2}";
            }

            // 首领 → 平定 → 收复 → 精英残部。
            TerritoryControlService.MarkLeaderDefeated(s, "silent");
            DestroyBelow(s, "silent", 19);
            foreach (OutpostRecord x in TerritoryOutposts(s, "silent"))
            {
                WorldOutpostSystem.MarkDestroyed(s, x.OutpostId);
            }
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            if (r.Status != TerritoryControlService.StatusReclaimed)
            {
                return $"首领被击败、控制度 {r.Control}% 后没收复（状态 {r.Status}）";
            }
            r.NextEliteTick = GameClock.Ticks + 1;
            WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
            OutpostRecord rem = WorldOutpostSystem.Find(s, r.RemnantId);
            if (rem == null || rem.Kind != WorldOutpostSystem.KindRemnant)
            {
                return "收复后到刷新点没出精英残部";
            }
            GridCell landing = SiegeService.NearestPassable(new GridCell(rem.CellX, rem.CellY), 8);
            if (!GameLogic.Campaign.Nav.NavService.PassableNow(landing.X, landing.Y, BinGames.Sim.Nav.NavConst.ClassHostile))
            {
                return $"精英残部 {rem.OutpostId} 落点 ({rem.CellX},{rem.CellY}) 8 格内没有敌方可走格";
            }
            var ra = new Vector2(rem.CellX, rem.CellY);
            Spawn(s, Toward(s, ra, 10f));
            if (!RunUntil(() => rem.Engaged, GameClock.StepHz * 8))
            {
                return "精英残部不迎战";
            }
            int eliteUnits = WorldSimulation.Home.Combat.CountSiegeGroup(rem.EngageKey);
            KillGroup(WorldSimulation.Home.Combat, ra, rem.EngageKey);
            if (!RunUntil(() => rem.Destroyed, GameClock.StepHz * 3))
            {
                return "精英残部全灭后没被击溃";
            }
            if (!OctantsMatchDestroyed(s, out int sum3, out int dest3))
            {
                return $"击溃残部后方位计数 {sum3} ≠ 摧毁状态据点 {dest3}";
            }
            info = $"静默据点 {silentCount} 个、控制度 {before}%→{after}%、增援重建 {(rebuilt.Length > 0 ? rebuilt : "无")}、残部 {eliteUnits} 台已击溃";
            return null;
        }

        // ── K 暂停与倍速 ─────────────────────────────────────────────────────────

        private static void CheckSpeedMatrix()
        {
            string RunAt(float speed, bool pauseHalf)
            {
                CampaignState s = NewWorld(42);
                foreach (OutpostRecord o in TerritoryOutposts(s, "silent"))
                {
                    WorldOutpostSystem.MarkDestroyed(s, o.OutpostId);
                }
                WorldSimulation.StepMany((int)TerritoryControlService.SyncTicks + 1);
                TerritoryControlRecord r = TerritoryControlService.Find(s, "silent");
                r.NextReinforceTick = GameClock.Ticks + GameClock.StepHz * 2;
                long target = GameClock.Ticks + GameClock.StepHz * 4;
                GameClock.SetSpeed(speed);
                int frames = 0;
                bool pausedOk = true;
                while (GameClock.Ticks < target && frames < 20000)
                {
                    if (pauseHalf && frames == 20)
                    {
                        GameClock.SetPaused(true);
                        long t0 = GameClock.Ticks;
                        int reinf = r.Reinforcements;
                        for (int k = 0; k < 20; k++)
                        {
                            WorldSimulation.Frame(1f / 60f, target);
                        }
                        pausedOk = GameClock.Ticks == t0 && r.Reinforcements == reinf;
                        GameClock.SetPaused(false);
                    }
                    WorldSimulation.Frame(1f / 60f, target);
                    frames++;
                }
                GameClock.SetSpeed(1f);
                OutpostRecord rb = TerritoryOutposts(s, "silent").FirstOrDefault(o => !o.Destroyed);
                return (pausedOk ? string.Empty : "PAUSE-MOVED ") + $"{GameClock.Ticks}:{rb?.OutpostId}@{rb?.SimTick}:{r.NextReinforceTick}:{r.Control}";
            }
            string r05 = RunAt(0.5f, false);
            string r1 = RunAt(1f, true);
            string r2 = RunAt(2f, false);
            string r3 = RunAt(3f, false);
            Expect(r05 == r1 && r1 == r2 && r2 == r3 && !r1.Contains("PAUSE") && !r1.Contains(":@"),
                $"K1 B09 暂停与倍速：阵营增援在 0.5x / 1x（中途暂停 20 帧不推进）/ 2x / 3x 下同一步重建同一个据点、下一次增援与控制度逐位相同（{r1}）");
        }

        // ── L 巡逻标记不跳位（DEBT-FG8GEN02-07）────────────────────────────────

        private static void CheckPatrolView()
        {
            Vector2 jump = WorldPlanetView.SmoothPatrol(Vector2.zero, new Vector2(30f, 0f), 0.05f);
            Vector2 normal1 = WorldPlanetView.SmoothPatrol(Vector2.zero, new Vector2(0.075f, 0f), 0.05f); // 1.5 格/秒 × 0.05 秒
            Vector2 normal3 = WorldPlanetView.SmoothPatrol(Vector2.zero, new Vector2(0.225f, 0f), 0.05f); // 3x
            Vector2 snap = WorldPlanetView.SmoothPatrol(Vector2.zero, new Vector2(500f, 0f), 0.05f);
            float catchup = GridContent.TryGetTuning("outpost.patrol_view_catchup", out float cu) ? cu : 0f;
            // 刚醒来跳到折返位置（30 格）：逐帧追上，每帧不超过 catchup × dt，约 2.5 真实秒内到位。
            Vector2 p = Vector2.zero;
            int frames = 0;
            float maxStep = 0f;
            while (Vector2.Distance(p, new Vector2(30f, 0f)) > 1e-3f && frames < 1000)
            {
                Vector2 q = WorldPlanetView.SmoothPatrol(p, new Vector2(30f, 0f), 0.05f);
                maxStep = Mathf.Max(maxStep, Vector2.Distance(p, q));
                p = q;
                frames++;
            }
            Expect(Mathf.Abs(jump.x - catchup * 0.05f) < 1e-4f && normal1 == new Vector2(0.075f, 0f) && normal3 == new Vector2(0.225f, 0f) && snap.x == 500f
                   && maxStep <= catchup * 0.05f + 1e-4f && frames > 1,
                $"L1 DEBT-FG8GEN02-07 巡逻标记不跳位：路线交回后逻辑位置跳 30 格，标记每帧最多走 {catchup} 格/秒 × dt（{frames} 帧追上，最大一步 {maxStep:F3} 格）；" +
                "正常巡逻（1x / 3x）标记与逻辑位置重合；相差超过 snap（读档 / 镜头跳走）直接放过去");

            // M 性能：领地控制每次检查 / 迎战每次对账的开销（Editor batchmode，一次测量）。
            ExpectPerf(true, $"M1 性能（Editor batchmode，本段全部世界里取最慢一次）：领地控制检查 {TerritoryControlService.MaxSyncMs:F3} ms、迎战对账 {TerritoryControlService.MaxCombatMs:F3} ms",
                new PerfGate.Metric("领地控制检查 ms", TerritoryControlService.MaxSyncMs, 1.0, false),
                new PerfGate.Metric("迎战对账 ms", TerritoryControlService.MaxCombatMs, 2.0, false));
            PerfLines.Add($"领地控制：检查最慢 {TerritoryControlService.MaxSyncMs:F3} ms / 迎战对账最慢 {TerritoryControlService.MaxCombatMs:F3} ms（Editor batchmode）");
        }

        // ── 工具 ────────────────────────────────────────────────────────────────

        private static string Snapshot(CampaignState s)
        {
            var sb = new StringBuilder();
            foreach (TerritoryControlRecord r in TerritoryControlService.StateOf(s).Records.OrderBy(x => x.TerritoryId, StringComparer.Ordinal))
            {
                sb.Append(r.TerritoryId).Append(':').Append(r.Status).Append(':').Append(r.Control).Append(':').Append(r.LeaderDefeated).Append(':').Append(r.NextReinforceTick)
                    .Append(':').Append(r.Reinforcements).Append(':').Append(r.NextEliteTick).Append(':').Append(r.RemnantId).Append(':').Append(r.EliteWaves).Append(':').Append(r.NextHarassTick).Append('|');
            }
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(s).Where(x => x.Kind == WorldOutpostSystem.KindTerritory || x.Kind == WorldOutpostSystem.KindRemnant).OrderBy(x => x.OutpostId, StringComparer.Ordinal))
            {
                sb.Append(o.OutpostId).Append(o.Destroyed ? 'D' : '-').Append(o.ReinforceStopped ? 'S' : '-').Append(o.Engaged ? 'E' : '-').Append(o.EngageKey).Append(';');
            }
            sb.Append("P").Append(WorldOutpostSystem.Patrols(s).Count);
            TerritoryControlState st = TerritoryControlService.StateOf(s);
            sb.Append(":").Append(st.NextEngageSerial).Append(':').Append(st.NextRemnantSerial).Append(':').Append(st.OutpostsDestroyed).Append(':').Append(st.RemnantsRouted);
            return sb.ToString();
        }

        /// <summary>按 ID 顺序摧毁领地据点（测试捷径，走正式的摧毁入口），直到控制度降到 <paramref name="below"/>% 以下；返回剩下的。</summary>
        private static List<OutpostRecord> DestroyBelow(CampaignState s, string territoryId, int below)
        {
            foreach (OutpostRecord o in TerritoryOutposts(s, territoryId).OrderBy(x => x.OutpostId, StringComparer.Ordinal))
            {
                if (TerritoryControlService.ComputeControl(s, territoryId, out _, out _) < below)
                {
                    break;
                }
                WorldOutpostSystem.MarkDestroyed(s, o.OutpostId);
            }
            return TerritoryOutposts(s, territoryId).Where(o => !o.Destroyed).ToList();
        }

        private static bool OfTerritory(CampaignState s, PatrolRecord p, string territoryId)
        {
            OutpostRecord o = WorldOutpostSystem.Find(s, p.OutpostId);
            return o != null && o.Kind == WorldOutpostSystem.KindTerritory && o.TerritoryId == territoryId;
        }

        private static List<OutpostRecord> TerritoryOutposts(CampaignState s, string territoryId) =>
            WorldOutpostSystem.Outposts(s).Where(o => o.Kind == WorldOutpostSystem.KindTerritory && o.TerritoryId == territoryId).OrderBy(o => o.OutpostId, StringComparer.Ordinal).ToList();

        private static OutpostRecord NearestOutpost(CampaignState s, string territoryId)
        {
            GridCell core = HomeGridService.CorePivot(s);
            return TerritoryOutposts(s, territoryId).Where(o => !o.Destroyed && o.Garrison > 0)
                .OrderBy(o => Dist(new Vector2(o.CellX, o.CellY), core)).ThenBy(o => o.OutpostId, StringComparer.Ordinal).First();
        }

        /// <summary>从 <paramref name="at"/> 朝核心方向 <paramref name="dist"/> 格的点（机器从家园方向开过来的位置）。</summary>
        private static Vector2 Toward(CampaignState s, Vector2 at, float dist)
        {
            GridCell core = HomeGridService.CorePivot(s);
            Vector2 dir = (new Vector2(core.X, core.Y) - at).normalized;
            return at + dir * dist;
        }

        private static GridCell FreeCellNear(CampaignState s, GridCell from, string type)
        {
            for (int r = 0; r <= 30; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(from.X + dx, from.Y + dy);
                        if (HomeGridService.ValidatePlacement(s, type, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                        {
                            return c;
                        }
                    }
                }
            }
            return from;
        }

        private static GridCell? PollutedCell(CampaignState s, HomeGridMap map, PlannedTerritory t)
        {
            for (int r = 0; r < t.Radius; r += 4)
            {
                for (int a = 0; a < 360; a += 15)
                {
                    var c = new GridCell(t.CenterX + Mathf.RoundToInt(Mathf.Cos(a * Mathf.Deg2Rad) * r), t.CenterY + Mathf.RoundToInt(Mathf.Sin(a * Mathf.Deg2Rad) * r));
                    if (map.GetPollution(c) > 0 && HomeGridService.ValidatePlacement(s, "purifier", c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        return c;
                    }
                }
            }
            return null;
        }

        private static void KillGroup(CombatSite site, Vector2 at, int key)
        {
            for (int i = 0; i < 128 && site.CountSiegeGroup(key) > 0; i++)
            {
                int u = site.FindNearestHostile(at, 80f);
                if (u <= 0)
                {
                    break;
                }
                site.Kernel.Damage(u, 1e7f, 0);
            }
        }

        private static bool RunUntil(Func<bool> done, long maxTicks)
        {
            for (long i = 0; i < maxTicks; i += 6)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(6);
            }
            return done();
        }

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
            CampaignState s = CampaignState.CreateNew("fgtcontrol-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(version, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            WorldSimulation.StepMany(2);
            return s;
        }

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

        private static int Spawn(CampaignState s, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId, at, 120f, 120f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static double Dist(Vector2 a, GridCell b) => Vector2.Distance(a, new Vector2(b.X, b.Y));

        private static int NotifyMembers(string typeId) => NotificationCenter.History.Where(e => e.Type != null && e.Type.Id == typeId).Sum(e => e.Members.Count);

        private static NotificationMember LatestMember(string typeId) => NotificationCenter.History.LastOrDefault(e => e.Type != null && e.Type.Id == typeId)?.Latest;

        private static void ExpectPerf(bool functionalOk, string message, params PerfGate.Metric[] perf) => PerfGate.Expect(functionalOk, message, perf, Expect, Line);

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgTerritoryControlSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
