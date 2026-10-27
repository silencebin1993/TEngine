using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using BinGames.Sim.Logistics;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using S = GameLogic.EditorTools.FgSiegeSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG10-RAID-01 化工突袭与再污染（FG09 第 6 章“突袭特点”；FG07 FGR-ENV-043；验收 FGT-FAC-007；卡片负向“净化塔无防御时被攻破”，FG09 第 10 节“自动重建规则照常生效”）。
    /// 全部起真实系统：真实家园（世界模拟、寻路、战斗内核攻城剧场与流场、净化塔进生产索引）、突袭导演、攻城服务、净化服务、突袭结算与离家报告。
    /// A 数据：净化塔归“purify”类；别的阵营 purify 偏好 = 其它建筑（行为不变）；澄净在场时三种职能净化塔偏好 0、其它整体后移；调参 / 文本 / 触发表。
    /// B 触发（DEBT-FG6DEF04-01）：第一幕不排；第二幕澄净领地出现 + 有净化塔 → 首次延迟后真的调导演，排出“针对净化塔”的澄净计划；别的阵营普通突袭还没出发时顺延不并进；没有净化塔不排。
    /// C 目标偏好与负向：同一布局里铸造攻城型先打核心、不碰净化塔（对照）；澄净攻城型先打净化塔（核心没挨打），没有防御的净化塔被攻破，自动重建规则照常排重建单。
    /// D FGT-FAC-007（DEBT-FG10FAC01-02 / DEBT-FG7ENV04-02）：澄净攻城时投放目标 = 净化塔，灭活无人机飞过去投放 → 塔周围那一片真的再污染、建筑受污染；
    ///   通知 / 离家报告（天气与环境段）/ 突袭结算（过程 + 汇总）都写明，汇总带重新净化的路径；读档后投放目标在第一步之前重写。
    /// E 腐蚀蜂群边走边腐蚀脚下的传送带（附近没有建筑，伤害只能来自腐蚀行进）。
    /// F 攻城重甲冲锋撞墙（DEBT-FG9FAC02-03）：蓄力预警（提示 ≥ 0.8 秒）→ 冲锋 → 撞上屏障即停、屏障掉耐久。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgClarityRaidSelfCheck）。
    /// </summary>
    public static class FgClarityRaidSelfCheck
    {
        private const int Slot = 7;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string Purifier = "purifier";
        private const string Wall = "barrier_t1";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("BinGames/QA/自检/FG 化工突袭与再污染")]
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
            Line("\n[化工突袭与再污染] 针对净化塔的触发、净化塔为主目标、灭活无人机再污染、离家报告与重新净化路径、腐蚀行进、攻城冲锋（FG10-RAID-01）");
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
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgclarityraid-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                SiegeCatalog.Reload();
                TurretCatalog.Reload();
                DefenseCatalog.Reload();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                ClarityRaidService.ResetForTests();

                Step(CheckData);
                Step(CheckTrigger);
                Step(CheckPurifierFirst);
                Step(CheckDroneRepollution);
                Step(CheckSwarmTrail);
                Step(CheckSiegeCharge);
            }
            finally
            {
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
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
            Line(_fail == 0 ? $"  化工突袭与再污染：全部 {_pass} 项通过" : $"  化工突袭与再污染：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ─────────────────────────────── A 数据 ───────────────────────────────

        private static void CheckData()
        {
            int n = CombatSiegeConst.CategoryCount;
            int purify = 6;
            int other = 5;
            IReadOnlyList<int> b0 = SiegeCatalog.RoleBiasFor(false);
            IReadOnlyList<int> b1 = SiegeCatalog.RoleBiasFor(true);
            IReadOnlyList<int> m1 = SiegeCatalog.RoleMasksFor(true);
            int shift = Mathf.RoundToInt(SiegeCatalog.ClarityOtherShiftMeters * 10f);
            bool same = true, primary = true, shifted = true;
            for (int r = 0; r < CombatSiegeConst.RoleRetreat; r++)
            {
                same &= b0[r * n + purify] == b0[r * n + other] && b0[r * n + purify] < CombatSiegeConst.Inf;
                primary &= b1[r * n + purify] == 0 && (m1[r] & CombatSiegeConst.CatPurify) != 0;
                for (int c = 0; c < n; c++)
                {
                    if (c != purify && b0[r * n + c] < CombatSiegeConst.Inf)
                    {
                        shifted &= b1[r * n + c] == b0[r * n + c] + shift;
                    }
                }
            }
            bool cat = SiegeCatalog.CategoryOf(Purifier) == CombatSiegeConst.CatPurify && SiegeCatalog.CategoryName(CombatSiegeConst.CatPurify) == "purify"
                       && n == 7 && CombatSiegeConst.CatMask == 0x7F && SiegeCatalog.Problems.Count == 0;
            Expect(cat && same && primary && shifted && shift > 0,
                $"A1 净化塔归“purify”类（第 7 个类别位）；别的阵营打它与“其它建筑”同偏好（{same}）；澄净在场时三种职能净化塔偏好 0（{primary}）、其它类别整体后移 {shift / 10} 米（{shifted}）；攻城表无问题");

            float period = 0f, rr = 0f, lv = 0f;
            bool tuning = GridContent.TryGetTuning("raid.clarity.purify_period_days", out period) && period >= 1f
                          && GridContent.TryGetTuning("raid.clarity.purify_first_delay_days", out _)
                          && GridContent.TryGetTuning("raid.clarity.repollute_radius", out rr) && rr >= 1f
                          && GridContent.TryGetTuning("raid.clarity.repollute_levels", out lv) && lv >= 1f
                          && GridContent.TryGetTuning("siege.clarity.other_shift_m", out _);
            bool texts = GameText.Has("purify.repath.working") && GameText.Has("purify.repath.paused") && GameText.Has("purify.repath.uncovered")
                         && GameText.Has("raid.result.tl.repolluted") && GameText.Has("raid.result.repolluted")
                         && GameText.Format("purify.notify.repolluted", "甲", 1, 2, "路径XYZ").Contains("路径XYZ");
            bool trigger = RaidCatalog.TryGetTrigger(RaidCatalog.TriggerPurification, out RaidTriggerDef td) && !td.Exempt;
            Expect(tuning && texts && trigger,
                $"A2 调参（触发周期 {period} 日、再污染半径 {rr} 格 / 升 {lv} 级）、文本（重新净化三种路径、结算两行；再污染通知带路径参数）、触发表“针对净化塔”是普通触发（受最短间隔）");
        }

        // ─────────────────────────────── B 触发 ───────────────────────────────

        private static void CheckTrigger()
        {
            CampaignState s = NewWorld(10701);
            BuildingRecord pur = S.PlaceNear(s, Purifier, S.CoreCenter(s), S.OutsidePoint(s, 30f), 0.25f, 0.7f);
            if (pur == null)
            {
                Fail("B 测试准备：放不下净化塔");
                return;
            }
            F.Seconds(2.1f);
            FactionState f = s.Factions;
            bool act1 = f.ClarityRaidNextTick < 0 && f.ClarityRaidTriggers == 0 && !ClarityRaidService.ClarityVisible(s);
            s.Progress.Act = Math.Max(2, s.Progress.Act); // 第二幕：澄净领地在地图上显示
            F.Seconds(1.1f);
            long due = f.ClarityRaidNextTick;
            long delay = RaidDirectorService.DayTicks(ClarityRaidService.FirstDelayDays);
            bool scheduled = ClarityRaidService.ClarityVisible(s) && due > GameClock.Ticks && due - GameClock.Ticks <= delay && f.ClarityRaidTriggers == 0;
            f.ClarityRaidNextTick = GameClock.Ticks; // 测试捷径：快进到首次延迟的终点（延迟本身在上面断言）
            F.Seconds(1.1f);
            RaidPlanRecord plan = s.Raids.Director.Plans.FirstOrDefault(p => p != null && p.Triggers.Contains(RaidCatalog.TriggerPurification));
            bool planned = f.ClarityRaidTriggers == 1 && plan != null && plan.Faction == RaidCatalog.FactionChemical && f.ClarityRaidNextTick > GameClock.Ticks;
            Expect(act1 && scheduled && planned,
                $"B1 第一幕澄净领地没出现：不排（{act1}）；第二幕 + 家园有净化塔：{(due - GameClock.Ticks) / (float)Math.Max(1, GameClock.StepHz):0} 秒后排首次（{scheduled}）；" +
                $"到点真的调突袭导演，排出澄净的“针对净化塔”计划 {plan?.PlanId}（阵营 {plan?.Faction}，触发 {(plan != null ? string.Join("/", plan.Triggers) : "无")}）（{planned}）");

            // 别的阵营的普通突袭还没出发：顺延 0.25 日，不并进别的阵营（“针对净化塔”不会被吞掉）。
            if (plan != null)
            {
                plan.Faction = "silent";
                f.ClarityRaidNextTick = GameClock.Ticks;
                int tr0 = f.ClarityRaidTriggers;
                F.Seconds(1.1f);
                bool deferred = f.ClarityRaidTriggers == tr0 && f.ClarityRaidNextTick > GameClock.Ticks
                                && f.ClarityRaidNextTick - GameClock.Ticks <= RaidDirectorService.DayTicks(0.25);
                plan.Faction = RaidCatalog.FactionChemical;
                Expect(deferred, $"B2 别的阵营的普通突袭还没出发时顺延（触发次数不变、下次在 0.25 日内），不并进别的阵营的计划（{deferred}）");
            }

            // 没有净化塔：第二幕也不排。
            CampaignState s2 = NewWorld(10702);
            s2.Progress.Act = Math.Max(2, s2.Progress.Act);
            F.Seconds(2.1f);
            bool none = ClarityRaidService.StandingPurifiers(s2) == 0 && s2.Factions.ClarityRaidNextTick < 0 && s2.Factions.ClarityRaidTriggers == 0;
            Expect(none, $"B3 家园没有净化塔：第二幕也不排“针对净化塔”的突袭（{none}）");
        }

        // ─────────────────────────────── C 目标偏好与负向 ───────────────────────────────

        private static void CheckPurifierFirst()
        {
            // 对照：铸造攻城型（职能“攻城”：防御 → 核心 → 其它），净化塔对它只是其它建筑——先打核心，不碰路边的净化塔。
            CampaignState s = NewWorld(10703);
            Vector2 at = S.OutsidePoint(s, 34f);
            BuildingRecord pur = S.PlaceNear(s, Purifier, at, S.CoreCenter(s), 0.3f, 0.65f);
            BuildingRecord core = Core(s);
            if (pur == null)
            {
                Fail("C 测试准备：放不下净化塔");
                return;
            }
            float p0 = Dur(s, pur);
            float c0 = Dur(s, core);
            S.Arrive(s, at, "foundry", new[] { "foundry.armorbot" }, new[] { 4 });
            bool coreHit = F.StepUntil(() => Dur(s, core) < c0 - 10f, 120);
            bool control = coreHit && Dur(s, pur) >= p0 - 0.01f && !SiegeService.AnyClaritySieging(s) && Site.PolluteTargetCount == 0;
            Expect(control, $"C1 对照：铸造攻城型先打核心（{c0:0} → {Dur(s, core):0}），路边的净化塔没挨打（{p0:0} → {Dur(s, pur):0}）；不是澄净 = 不写投放目标（{Site?.PolluteTargetCount}）");

            // 澄净攻城型：同一种职能，净化塔是主目标——先打净化塔，核心没挨打；净化塔没有防御 → 被攻破；自动重建规则照常排重建单。
            s = NewWorld(10703);
            at = S.OutsidePoint(s, 34f);
            pur = S.PlaceNear(s, Purifier, at, S.CoreCenter(s), 0.3f, 0.65f);
            core = Core(s);
            ResearchService.CompleteForTests(s, "defense.repair_drone", "defense.auto_rebuild");
            bool ruleOk = StandingRuleService.TryCreate(s, StandingRuleService.KindRebuild, out StandingRuleRecord rule, out string why);
            c0 = Dur(s, core);
            S.Arrive(s, at, RaidCatalog.FactionChemical, new[] { "clarity.sprayer" }, new[] { 4 });
            bool clarity = SiegeService.AnyClaritySieging(s) && Site.PolluteTargetCount == 1;
            bool down = F.StepUntil(() => pur.ConstructionState == BuildingConstructionState.Damaged, 180);
            float cAt = Dur(s, core);
            Expect(clarity && down && cAt >= c0 - 0.01f,
                $"C2 澄净攻城型先打净化塔（剧场换上澄净职能表、投放目标 = 净化塔 {Site?.PolluteTargetCount} 处）（{clarity}）；没有防御的净化塔被攻破（{down}），此前核心没挨打（{c0:0} → {cAt:0}）");
            bool rebuild = ruleOk && F.StepUntil(() => HomeValleyWorkOrders.FindActiveRepair(s, pur.BuildingId) is WorkOrderRecord o && o.RuleSerial == rule.Serial, 30);
            Expect(rebuild, $"C3 负向（FG09 第 10 节）：净化塔被攻破后自动重建规则照常生效——派出由 R{rule?.Serial} 触发的修复单（{rebuild}）{(ruleOk ? string.Empty : "；建规则失败：" + why)}");
        }

        // ─────────────────────────────── D 再污染（FGT-FAC-007） ───────────────────────────────

        private static void CheckDroneRepollution()
        {
            CampaignState s = NewWorld(10704);
            s.Progress.Act = Math.Max(2, s.Progress.Act);
            Vector2 at = S.OutsidePoint(s, 40f);
            BuildingRecord pur = S.PlaceNear(s, Purifier, at, S.CoreCenter(s), 0.4f, 0.75f);
            if (pur == null)
            {
                Fail("D 测试准备：放不下净化塔");
                return;
            }
            var center = new GridCell(pur.GridX, pur.GridY);
            HomeGridMap map = HomeGridService.MapFor(s);
            int r = ClarityRaidService.RepolluteRadius;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy <= r * r)
                    {
                        map.SetPollution(new GridCell(center.X + dx, center.Y + dy), 0); // 测试捷径：塔周围已经净化回 0 级（已净化区）
                    }
                }
            }
            BuildingRecord pole = S.PlaceNear(s, "power_pole", pur.Position, at, 0.08f, 0.2f);
            int landed0 = ClarityRaidService.Landed;
            int notes0 = NotifyCount("pollution_raised");
            S.Arrive(s, at, RaidCatalog.FactionChemical, new[] { "clarity.deact_drone" }, new[] { 2 });
            bool targets = Site.PolluteTargetCount == 1;
            bool landed = F.StepUntil(() => ClarityRaidService.Landed > landed0, 120);
            int lvl = map.GetPollution(center);
            string note = LastNotify("pollution_raised");
            RaidResultRecord rr = RaidResultService.PrimaryOpen(s);
            RaidTimelineRecord tl = rr?.Timeline.FirstOrDefault(t => t != null && t.Kind == "repolluted");
            bool contaminated = pole == null || PurificationService.IsContaminated(s, pole.BuildingId);
            string pathHere = PurificationService.RepurifyPath(s, center);
            bool pathNamed = pathHere.Contains(BuildingOps.NameOf(pur));
            Expect(targets && landed && lvl > 0 && NotifyCount("pollution_raised") > notes0 && note.Contains("重新净化") && contaminated && s.Factions.ClarityRaidDrops >= 1,
                $"D1 FGT-FAC-007：澄净攻城时投放目标 = 净化塔（{targets}）；灭活无人机飞过去投放（{landed}），塔中心那一格 0 → {lvl} 级，" +
                $"路边电塔受污染（{contaminated}）；通知写明怎么净化回来（“{Short(note)}”）");
            Expect(rr != null && rr.Repollutions >= 1 && rr.RepollutedCells > 0 && tl != null && RaidResultService.TimelineText(tl).Contains(rr.RepollutedCells > 0 ? "格" : string.Empty)
                   && pathNamed,
                $"D2 再污染在突袭结算里说明：结算记 {rr?.Repollutions} 次 / {rr?.RepollutedCells} 格、过程一条可定位（“{(tl != null ? RaidResultService.TimelineText(tl) : "无")}”）；" +
                $"这一片的路径点名覆盖它的净化塔（“{Short(pathHere)}”）");
            // D2b 离家报告：报告只在远征在外时开着，世界一推进就被 Reconcile 按“没有远征”关掉——所以开报告后直接走同一个再污染入口、读这份报告、再结算，中间不推进世界。
            AwayReportRecord away = AwayReportService.Begin(s, null, Array.Empty<int>());
            RepolluteResult again = PurificationService.Repollute(s, center, r, 1);
            AwayEntryRecord entry = away?.Entries.LastOrDefault(e => e != null && e.TypeId == "pollution_raised");
            AwayReportService.End(s, AwayReportService.OutcomeOther, notify: false);
            Expect(again.CellsRaised > 0 && entry != null && entry.Section == "weather" && entry.Detail.Contains("重新净化") && entry.HasLocation,
                $"D2b 再污染记进离家报告：再污染 {again.CellsRaised} 格；“天气与环境”段有这条通知（{entry?.Section ?? "无"}，可定位 {entry?.HasLocation}），带重新净化路径（“{Short(entry?.Detail ?? string.Empty)}”）");
            string far = PurificationService.RepurifyPath(s, new GridCell(center.X + 400, center.Y + 400));
            Expect(far == GameText.Get("purify.repath.uncovered"), $"D3 没有净化塔覆盖的地方：路径写“在附近放一座净化塔”（“{Short(far)}”）");

            // 读档后（内核快照不存投放目标）：剧场重设在第一步之前把投放目标写回。
            Site.SetPolluteTargets(null);
            SiegeService.RestoreAfterLoad(s, Site);
            bool restored = Site.PolluteTargetCount == 1;
            ClarityRaidService.ApplyPolluteTargets(s, Site, false, true);
            bool cleared = Site.PolluteTargetCount == 0;
            Expect(restored && cleared, $"D4 读档后投放目标在第一步之前写回（{restored}）；没有澄净攻城时清空（星球别处的无人机不会被吸过来）（{cleared}）");
            AwayReportService.End(s, AwayReportService.OutcomeOther, notify: false);
        }

        // ─────────────────────────────── E 腐蚀行进 ───────────────────────────────

        private static void CheckSwarmTrail()
        {
            CampaignState s = NewWorld(10705);
            Vector2 at = S.OutsidePoint(s, 34f);
            Vector2 core = S.CoreCenter(s);
            Vector2 mid = Vector2.Lerp(at, core, 0.45f);
            Vector2 dir = (core - at).normalized;
            Vector2 side = new Vector2(-dir.y, dir.x);
            var belts = new List<GridCell>();
            for (int o = -6; o <= 6; o++)
            {
                Vector2 p = mid + side * o;
                var c = new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
                if (BeltNetworkService.TryPlace(s, c, BeltDir.East, 0).Ok)
                {
                    belts.Add(c);
                }
            }
            NavService.SyncGridChanges();
            long gi0 = Site.SiegeStats.GroundImpacts;
            long belt0 = SiegeService.CollateralBeltHits;
            S.Arrive(s, at, RaidCatalog.FactionChemical, new[] { "clarity.corrosion_swarm" }, new[] { 8 });
            bool hit = belts.Count > 0 && F.StepUntil(() => belts.Any(c => BeltNetworkService.DamageOf(c) > 0 || BeltNetworkService.HpOf(c) < 0), 120);
            Expect(hit && Site.SiegeStats.GroundImpacts > gi0 && SiegeService.CollateralBeltHits > belt0,
                $"E1 腐蚀蜂群边走边腐蚀：路上横着的 {belts.Count} 格传送带（附近没有建筑，伤害只能来自腐蚀行进）掉耐久（{hit}），腐蚀行进记了 {Site.SiegeStats.GroundImpacts - gi0} 次、" +
                $"溅射打到传送带 {SiegeService.CollateralBeltHits - belt0} 次");
        }

        // ─────────────────────────────── F 攻城冲锋 ───────────────────────────────

        private static void CheckSiegeCharge()
        {
            CampaignState s = NewWorld(10706);
            Vector2 at = S.OutsidePoint(s, 34f);
            BuildingRecord wall = S.PlaceNear(s, Wall, at, S.CoreCenter(s), 0.35f, 0.7f);
            if (wall == null)
            {
                Fail("F 测试准备：放不下屏障");
                return;
            }
            float w0 = Dur(s, wall);
            long ch0 = Site.Kernel.Counters.Charges;
            long hit0 = Site.SiegeStats.ChargeWallHits;
            int cue0 = FoundryCombatFeedback.ChargeCues;
            S.Arrive(s, at, "foundry", new[] { "foundry.siege_heavy" }, new[] { 2 });
            bool rammed = F.StepUntil(() => Site.SiegeStats.ChargeWallHits > hit0, 150);
            bool warn = EnemyDefs.TryGet("enemy_siege_heavy", out GameConfig.fg.MechEnemy row) && Mathf.Max(0.8f, row.WarnSeconds) >= 0.8f;
            Expect(rammed && Site.Kernel.Counters.Charges > ch0 && FoundryCombatFeedback.ChargeCues > cue0 && warn && Dur(s, wall) < w0,
                $"F1 攻城重甲在突袭里也冲锋（DEBT-FG9FAC02-03）：蓄力预警提示 {FoundryCombatFeedback.ChargeCues - cue0} 次（≥ 0.8 秒），冲锋 {Site.Kernel.Counters.Charges - ch0} 次，" +
                $"撞上屏障即停 {Site.SiegeStats.ChargeWallHits - hit0} 次，屏障 {w0:0} → {Dur(s, wall):0}");
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        private static BuildingRecord Core(CampaignState s) => s.BuildingRecords.First(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);

        private static float Dur(CampaignState s, BuildingRecord b) => RepairDroneService.DurabilityOf(s, b);

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static string LastNotify(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Text ?? string.Empty;

        private static string Short(string s) => s == null ? string.Empty : s.Length > 160 ? s.Substring(0, 160) + "…" : s;

        private static CampaignState NewWorld(int seed)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgclarityraid-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            CampaignFgStateDomains.EnsureAll(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            s.Scrap = 3000;
            F.PowerUp(s);
            ResearchService.CompleteForTests(s, "defense.barrier", "defense.shield", "defense.trap");
            HomeValleyPowerGrid.Recompute(s);
            WorldTransitSystem.ResetCountersForTests();
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgClarityRaidSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
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
