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
using O = GameLogic.EditorTools.FgOutpostSelfCheck;
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
                Step(CheckPurifierPlanTarget);
                Step(CheckTimingAndObservation);
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

            // 建造者难度（只有剧情突袭）：有净化塔、第二幕也不排；直接调导演入口同样不出计划。
            CampaignState s3 = NewWorld(10703);
            s3.DifficultyId = "Builder"; // 测试捷径：开局就是建造者
            BuildingRecord pur3 = S.PlaceNear(s3, Purifier, S.CoreCenter(s3), S.OutsidePoint(s3, 30f), 0.25f, 0.7f);
            s3.Progress.Act = Math.Max(2, s3.Progress.Act);
            F.Seconds(2.1f);
            RaidDirectorService.OnPurificationTowerTargeted(s3);
            F.Seconds(1.1f);
            bool builder = pur3 != null && DifficultyService.StoryOnly(s3) && s3.Factions.ClarityRaidNextTick < 0 && s3.Factions.ClarityRaidTriggers == 0
                           && !s3.Raids.Director.Plans.Any(p => p != null && p.Triggers.Contains(RaidCatalog.TriggerPurification));
            Expect(builder, $"B4 建造者难度：家园有净化塔（{pur3 != null}）、第二幕也不排“针对净化塔”的突袭，直接调导演入口也不出计划（{builder}）");
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
            long every = Math.Max(1, (long)Math.Round(SiegeCatalog.SyncSeconds * GameClock.StepHz));
            while (GameClock.Ticks % every == 0)
            {
                WorldSimulation.StepMany(1); // 展开那一步不是对账步：职能表只能来自展开本身
            }
            int sync0 = SiegeService.SyncCount;
            S.Arrive(s, at, RaidCatalog.FactionChemical, new[] { "clarity.sprayer" }, new[] { 4 });
            IReadOnlyList<int> want = SiegeCatalog.RoleBiasFor(true);
            bool rolesNow = SiegeService.SyncCount == sync0 && Site != null;
            for (int i = 0; rolesNow && i < want.Count && i < CombatSiegeConst.RoleCount * CombatSiegeConst.CategoryCount; i++)
            {
                rolesNow &= Site.Kernel.SiegeRoleBias(i) == Math.Max(0, Math.Min(CombatSiegeConst.Inf, want[i]));
            }
            Expect(rolesNow, $"C2a 展开当步（这一步没有对账，SyncCount {sync0} → {SiegeService.SyncCount}）内核职能表就是澄净表（净化塔偏好 0），不等 0.5 秒后的对账（读档与不读档一致）");
            bool clarity = SiegeService.AnyClaritySieging(s) && Site.PolluteTargetCount == 1;
            bool down = F.StepUntil(() => pur.ConstructionState == BuildingConstructionState.Damaged, 180);
            float cAt = Dur(s, core);
            Expect(clarity && down && cAt >= c0 - 0.01f,
                $"C2 澄净攻城型先打净化塔（剧场换上澄净职能表、投放目标 = 净化塔 {Site?.PolluteTargetCount} 处）（{clarity}）；没有防御的净化塔被攻破（{down}），此前核心没挨打（{c0:0} → {cAt:0}）");
            // 攻城中虚影净化塔建成（施工完成是原地改建筑状态，生产索引 Revision 不涨）→ 下一步就成为投放目标。
            BuildingRecord pur2 = S.PlaceNear(s, Purifier, S.CoreCenter(s), at, 0.15f, 0.4f);
            int ghostCount = -1, builtCount = -1;
            if (pur2 != null)
            {
                pur2.ConstructionState = BuildingConstructionState.Planned; // 测试捷径：登记后立刻当作规划虚影（与玩家放虚影后的状态一致）
                WorldSimulation.StepMany(2);
                ghostCount = Site.PolluteTargetCount;
                pur2.ConstructionState = BuildingConstructionState.Operational; // 测试捷径：施工完成（与施工系统一样原地改状态）
                WorldSimulation.StepMany(2);
                builtCount = Site.PolluteTargetCount;
            }
            Expect(pur2 != null && ghostCount == 1 && builtCount == 2,
                $"C2b 攻城中：规划虚影的净化塔不是投放目标（{ghostCount} 处）；建成后下一步就成为投放目标（{builtCount} 处）");
            var hs = new HomeRaidStatus();
            HomeRaidAlertService.CollectHome(s, hs);
            int[] first = (int[])hs.Total.Clone();
            HomeRaidAlertService.CollectHome(s, hs);
            Expect(first[HomeRaidStatus.KeyPurify] >= 1 && first.SequenceEqual(hs.Total),
                $"C2c 同一个告警汇总连续汇总两次结果不变（净化塔 {first[HomeRaidStatus.KeyPurify]} → {hs.Total[HomeRaidStatus.KeyPurify]} 座；此前每次刷新往上累加）");
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
            Vector2 poleTo = (Vector2)pur.Position + (at - (Vector2)pur.Position).normalized * (r - 1);
            BuildingRecord pole = S.PlaceNear(s, "power_pole", pur.Position, poleTo, 0.3f, 0.7f);
            if (pole == null || Sq(pole.GridX - center.X) + Sq(pole.GridY - center.Y) > (r - 1) * (r - 1))
            {
                Fail($"D 测试准备：电塔放不进再污染半径 {r} 格（{(pole != null ? $"({pole.GridX},{pole.GridY})" : "放不下")}，塔心 ({center.X},{center.Y})）");
                return;
            }
            var poleCell = new GridCell(pole.GridX, pole.GridY);
            int landed0 = ClarityRaidService.Landed;
            int notes0 = NotifyCount("pollution_raised");
            TransitGroupRecord g = S.Arrive(s, at, RaidCatalog.FactionChemical, new[] { "clarity.deact_drone" }, new[] { 2 });
            bool targets = Site.PolluteTargetCount == 1 && Site.PolluteSiegeOnly;
            // D4 准备：真文件存档，在投放之前（读档后要从这里重放同样的步数）。
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long t0 = GameClock.Ticks;
            bool landed = F.StepUntil(() => ClarityRaidService.Landed > landed0, 120);
            int lvl = map.GetPollution(center);
            string note = LastNotify("pollution_raised");
            RaidResultRecord mine = RaidResultService.OpenFor(s, g.GroupId);
            RaidTimelineRecord tl = mine?.Timeline.FirstOrDefault(t => t != null && t.Kind == "repolluted");
            bool contaminated = PurificationService.IsContaminated(s, pur.BuildingId) && PurificationService.IsContaminated(s, pole.BuildingId);
            string pathHere = PurificationService.RepurifyPath(s, center);
            bool pathNamed = pathHere.Contains(BuildingOps.NameOf(pur));
            Expect(targets && landed && lvl > 0 && NotifyCount("pollution_raised") > notes0 && note.Contains("重新净化") && contaminated && s.Factions.ClarityRaidDrops >= 1,
                $"D1 FGT-FAC-007：澄净攻城时投放目标 = 净化塔、只让攻城单位投放（{targets}）；灭活无人机飞过去投放（{landed}），塔中心那一格 0 → {lvl} 级，" +
                $"净化塔本身与半径内的电塔都受污染（{contaminated}）；通知写明怎么净化回来（“{Short(note)}”）");
            Expect(mine != null && mine.Repollutions >= 1 && mine.RepollutedCells > 0 && tl != null && RaidResultService.TimelineText(tl).Contains("格") && pathNamed,
                $"D2 再污染记在投放者所属队伍（{g.GroupId}）那一份结算：{mine?.Repollutions} 次 / {mine?.RepollutedCells} 格、过程一条可定位（“{(tl != null ? RaidResultService.TimelineText(tl) : "无")}”）；" +
                $"这一片的路径点名覆盖它的净化塔（“{Short(pathHere)}”）");
            // 突袭结算的两处汇总（离家报告的突袭段 / 结算详情）：只有再污染时标题行警告色、不写“没有损失”。
            var away = new List<AwayLine>();
            RaidResultService.AppendAway(s, 0, GameClock.Ticks + 1, away);
            AwayLine title = away.FirstOrDefault(x => x.Action == AwayLineAction.RaidResult && x.Arg == mine?.Serial.ToString(System.Globalization.CultureInfo.InvariantCulture));
            bool awayRow = away.Any(x => x.Section == "raid" && x.Text.Contains("再污染") && x.Text.Contains("重新净化") && x.HasPos);
            var detail = new List<AwayLine>();
            RaidHistoryEntry he = RaidResultService.HistoryEntries(s).FirstOrDefault(x => x.Result == mine);
            if (he.Result != null)
            {
                RaidResultService.BuildDetail(s, he, detail);
            }
            string none = GameText.Get("raid.result.loss.none");
            bool noFalseNone = mine == null || RaidResultService.LossTotal(mine) > 0 || detail.All(x => x.Text != none);
            Expect(title != null && title.Cls == "ar-row-warn" && awayRow && he.Result != null && detail.Any(x => x.Section == "losses" && x.Text.Contains("再污染")) && noFalseNone,
                $"D2c 离家报告的突袭段写再污染（带重新净化路径、可定位）（{awayRow}），这一波标题行警告色（{title?.Cls ?? "无"}）；结算详情的损失段写再污染，" +
                $"只有再污染时不写“没有损失”（损失 {(mine != null ? RaidResultService.LossTotal(mine) : -1)} 处，{noFalseNone}）");
            F.Seconds(1f);
            long elapsed = GameClock.Ticks - t0;
            string continuous = Sig(s, center, poleCell, pur);

            // D2b 离家报告：报告只在远征在外时开着，世界一推进就被 Reconcile 按“没有远征”关掉——所以开报告后直接走同一个再污染入口、读这份报告、再结算，中间不推进世界。
            AwayReportRecord rep = AwayReportService.Begin(s, null, Array.Empty<int>());
            RepolluteResult again = PurificationService.Repollute(s, center, r, 1);
            AwayEntryRecord entry = rep?.Entries.LastOrDefault(e => e != null && e.TypeId == "pollution_raised");
            AwayReportService.End(s, AwayReportService.OutcomeOther, notify: false);
            Expect(again.CellsRaised > 0 && entry != null && entry.Section == "weather" && entry.Detail.Contains("重新净化") && entry.HasLocation,
                $"D2b 再污染记进离家报告：再污染 {again.CellsRaised} 格；“天气与环境”段有这条通知（{entry?.Section ?? "无"}，可定位 {entry?.HasLocation}），带重新净化路径（“{Short(entry?.Detail ?? string.Empty)}”）");
            var farCell = new GridCell(center.X + 400, center.Y + 400);
            map.SetPollution(farCell, 0);
            string farClean = PurificationService.RepurifyPath(s, farCell);
            map.SetPollution(farCell, 2); // 测试捷径：远处一片有污染、没有塔覆盖
            string far = PurificationService.RepurifyPath(s, farCell);
            Expect(far == GameText.Get("purify.repath.uncovered") && farClean == GameText.Get("purify.repath.done"),
                $"D3 没有净化塔覆盖、还有污染的地方：路径写“在附近放一座净化塔”（“{Short(far)}”）；落点已是 0 级时写“已经净化回来了”（“{Short(farClean)}”）");
            // D3b 净化从塔心往外走：塔心先清零、外圈还脏时，这一片的路径仍点名覆盖它的塔（正在净化 / 停着），不写“已经净化回来了”；离家报告的突袭段同一判定。
            byte heart = map.GetPollution(center);
            map.SetPollution(center, 0); // 测试捷径：塔心那一格先净化完
            string done = GameText.Get("purify.repath.done");
            string heartClean = PurificationService.RepurifyPath(s, center, r);
            var away2 = new List<AwayLine>();
            RaidResultService.AppendAway(s, 0, GameClock.Ticks + 1, away2);
            AwayLine row2 = away2.FirstOrDefault(x => x.Section == "raid" && x.Text.Contains("再污染"));
            Expect(heartClean != done && heartClean.Contains(BuildingOps.NameOf(pur)) && row2 != null && !row2.Text.Contains(done),
                $"D3b 塔心已净、外圈还有污染：路径仍写覆盖它的塔（“{Short(heartClean)}”），离家报告突袭段不写“已经净化回来了”（“{Short(row2?.Text ?? "无")}”）");
            map.SetPollution(center, heart);

            // D5 清空：澄净队伍全灭（无人机投放即自毁）→ 对账关剧场 → 投放目标清空；结算通知带再污染次数。
            bool gone = F.StepUntil(() => !SiegeService.StateOf(s).TheaterActive, 180);
            string resultNote = LastNotify("raid_result");
            Expect(gone && Site.PolluteTargetCount == 0 && resultNote.Contains("再污染"),
                $"D5 澄净队伍全灭、对账关剧场后投放目标清空（剧场已关 {gone}，目标 {Site?.PolluteTargetCount} 处；星球别处的无人机不会被吸过来）；结算通知带再污染次数（“{Short(resultNote)}”）");

            // D4 真存读档：读档后第一步之前投放目标已写回（内核快照不存它），再跑同样的步数与不存档连续跑一致。
            CampaignState l = RestoreSlot(out string fail);
            bool written = l != null && Site != null && Site.PolluteTargetCount == 1 && Site.PolluteSiegeOnly;
            if (l != null)
            {
                StepTicks(elapsed);
            }
            string after = l != null ? Sig(l, center, poleCell, HomeGridService.FindBuilding(l, pur.BuildingId)) : "读档失败：" + fail;
            Expect(save.Success && written && after == continuous,
                $"D4 投放前真文件存档 → 读档：第一步之前投放目标已写回（{written}）；接着跑 {elapsed / (float)Math.Max(1, GameClock.StepHz):0.0} 游戏秒与不存档连续跑一致（{after == continuous}）"
                + (after == continuous ? string.Empty : $"\n连续：{Short(continuous)}\n读档：{Short(after)}"));
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
            // 蓄力预警的真实时长：记蓄力提示那一步，逐步推进到这台进入冲锋（Charge == 2），间隔必须 ≥ 0.8 秒。
            int windUnit = 0;
            void OnFoundry(CombatSite site, int unit, byte code)
            {
                if (code == FoundryCombatFeedback.CodeChargeWindup && windUnit == 0 && ReferenceEquals(site, Site))
                {
                    windUnit = unit;
                }
            }
            FoundryCombatFeedback.Changed += OnFoundry;
            long windTick = -1, chargeTick = -1;
            try
            {
                S.Arrive(s, at, "foundry", new[] { "foundry.siege_heavy" }, new[] { 2 });
                int maxTicks = 150 * GameClock.StepHz;
                for (int i = 0; i < maxTicks && chargeTick < 0; i++)
                {
                    if (windUnit != 0 && windTick < 0)
                    {
                        windTick = GameClock.Ticks;
                    }
                    if (windTick >= 0 && Site.TryGetFoundry(windUnit, out CombatFoundryUnit fu) && fu.Charge == 2)
                    {
                        chargeTick = GameClock.Ticks;
                        break;
                    }
                    WorldSimulation.StepMany(1);
                }
            }
            finally
            {
                FoundryCombatFeedback.Changed -= OnFoundry;
            }
            float warnSec = windTick >= 0 && chargeTick >= 0 ? (chargeTick - windTick) / (float)Math.Max(1, GameClock.StepHz) : -1f;
            bool rammed = F.StepUntil(() => Site.SiegeStats.ChargeWallHits > hit0, 150);
            Expect(rammed && Site.Kernel.Counters.Charges > ch0 && FoundryCombatFeedback.ChargeCues > cue0 && warnSec >= 0.8f - 1e-3f && Dur(s, wall) < w0,
                $"F1 攻城重甲在突袭里也冲锋（DEBT-FG9FAC02-03）：蓄力预警提示 {FoundryCombatFeedback.ChargeCues - cue0} 次，实测蓄力到冲锋 {warnSec:0.00} 秒（≥ 0.8），冲锋 {Site.Kernel.Counters.Charges - ch0} 次，" +
                $"撞上屏障即停 {Site.SiegeStats.ChargeWallHits - hit0} 次，屏障 {w0:0} → {Dur(s, wall):0}");
        }

        // ─────────────────────────────── G “针对净化塔”的计划目标 ───────────────────────────────

        private static void CheckPurifierPlanTarget()
        {
            CampaignState s = NewWorld(10707);
            s.Progress.Act = Math.Max(2, s.Progress.Act);
            Vector2? spot = O.FindSpot(s, 340f, 6);
            var placed = new List<BuildingRecord>();
            for (int i = 0; spot.HasValue && i < Math.Max(3, RaidCatalog.TargetOutpostMinBuildings); i++)
            {
                BuildingRecord b = O.PlaceNear(s, Purifier, spot.Value, 0f, 18f);
                if (b != null)
                {
                    placed.Add(b);
                }
            }
            OwnOutpostService.Sync(s);
            OwnOutpostRecord outpost = spot.HasValue ? OwnOutpostService.Nearest(s, spot.Value) : null;
            if (outpost == null || outpost.Intact < RaidCatalog.TargetOutpostMinBuildings || ClarityRaidService.StandingPurifiers(s) != placed.Count)
            {
                Fail($"G 测试准备：前哨站没成形（放下净化塔 {placed.Count} 座，前哨站 {outpost?.OutpostId ?? "无"}、立着 {outpost?.Intact ?? 0} 座）");
                return;
            }
            int n0 = s.Raids.Director.Plans.Length;
            RaidDirectorService.OnPurificationTowerTargeted(s);
            F.Seconds(1.1f);
            RaidPlanRecord plan = s.Raids.Director.Plans.Skip(n0).FirstOrDefault(p => p != null && p.Triggers.Contains(RaidCatalog.TriggerPurification));
            Expect(plan != null && plan.TargetKind == RaidDirectorService.TargetOutpost && plan.TargetId == outpost.OutpostId,
                $"G1 “针对净化塔”的计划目标 = 净化塔所在的那一处：净化塔全在前哨站 {outpost.OutpostId}（家园核心旁一座都没有）→ 计划目标 {plan?.TargetKind}/{plan?.TargetId}");
            if (plan == null)
            {
                return;
            }
            // G2 “针对净化塔”并进一份还没预警、目标不是净化塔那一处的计划：改打净化塔那一处，出发前的预报作废（变更序号 +1）。
            F.StepUntil(() => plan.RouteState == RaidDirectorService.RouteReady || plan.RouteState == RaidDirectorService.RouteFailed, 240);
            plan.TargetKind = RaidDirectorService.TargetHome; // 测试捷径：当作按暴露抽到了家园
            plan.TargetId = RaidDirectorService.TargetHome;
            int rev0 = plan.Revision;
            bool pre = plan.State < RaidDirectorService.StateWarned;
            RaidDirectorService.OnPurificationTowerTargeted(s);
            F.Seconds(1.1f);
            Expect(pre && plan.TargetKind == RaidDirectorService.TargetOutpost && plan.TargetId == outpost.OutpostId && plan.Revision > rev0,
                $"G2 “针对净化塔”并进还没预警的计划（{pre}）：目标改成净化塔那一处 {plan.TargetKind}/{plan.TargetId}，变更序号 {rev0} → {plan.Revision}");
        }

        // ─────────────────────────────── H 倍速 / 暂停 / 观察（FGR-BASE-021） ───────────────────────────────

        /// <summary>同一种子、同一布局：澄净无人机攻城投放、真实再污染，跑 60 游戏秒（按真实帧推进），返回签名。</summary>
        private static string RepolluteTiming(bool observe, float speed, bool pauseFirst, out bool pausedHeld, out bool landed)
        {
            CampaignState s = NewWorld(10708, observe);
            s.Progress.Act = Math.Max(2, s.Progress.Act);
            Vector2 at = S.OutsidePoint(s, 40f);
            BuildingRecord pur = S.PlaceNear(s, Purifier, at, S.CoreCenter(s), 0.4f, 0.75f);
            pausedHeld = true;
            landed = false;
            if (pur == null)
            {
                return "放不下净化塔";
            }
            var center = new GridCell(pur.GridX, pur.GridY);
            HomeGridMap map = HomeGridService.MapFor(s);
            int r = ClarityRaidService.RepolluteRadius;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    map.SetPollution(new GridCell(center.X + dx, center.Y + dy), 0); // 测试捷径：塔周围已净化
                }
            }
            int landed0 = ClarityRaidService.Landed;
            S.Arrive(s, at, RaidCatalog.FactionChemical, new[] { "clarity.deact_drone" }, new[] { 2 });
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Sig(s, center, center, pur);
                for (int i = 0; i < 90; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Sig(s, center, center, pur) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 60;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 600)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            landed = ClarityRaidService.Landed > landed0;
            return Sig(s, center, center, pur);
        }

        private static void CheckTimingAndObservation()
        {
            string reference = null;
            bool same = true, paused = true, landedAll = true;
            string diff = string.Empty;
            foreach ((bool observe, float speed) in new[] { (true, 1f), (true, 0.5f), (true, 3f), (false, 1f) })
            {
                string snap = RepolluteTiming(observe, speed, true, out bool held, out bool landed);
                paused &= held;
                landedAll &= landed;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{(observe ? "观察" : "不观察")} {speed}x：{Short(snap)}\n参照：{Short(reference)}";
                }
                same &= snap == reference;
            }
            Expect(same && paused && landedAll,
                $"H1 暂停 1.5 秒世界不动（{paused}）；观察 1x / 0.5x / 3x 与不观察家园 1x 跑同样 60 游戏秒：投放落地（{landedAll}）、再污染格子 / 受污染 / 结算记账 / 内核哈希逐位一致（{same}）" + diff);
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        private static int Sq(int x) => x * x;

        private static void StepTicks(long n)
        {
            int q = Math.Max(1, GameClock.StepHz / 4);
            while (n > 0)
            {
                int k = (int)Math.Min(q, n);
                WorldSimulation.StepMany(k);
                n -= k;
            }
        }

        /// <summary>存读档比对的签名：攻城快照 + 内核状态哈希 + 塔心 / 电塔格的污染等级 + 投放计数 + 受污染 + 进行中结算的再污染记账。</summary>
        private static string Sig(CampaignState s, GridCell center, GridCell pole, BuildingRecord pur)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            RaidResultRecord open = RaidResultService.PrimaryOpen(s);
            return SiegeService.Snapshot(s) + "|H" + (Site != null ? Site.Kernel.StateHash().ToString("X16") : "-")
                   + $"|P{map.GetPollution(center)},{map.GetPollution(pole)}|D{s.Factions.ClarityRaidDrops},{s.Factions.ClarityRaidCells}"
                   + $"|C{pur != null && PurificationService.IsContaminated(s, pur.BuildingId)}|R{open?.Repollutions},{open?.RepollutedCells}";
        }

        /// <summary>真文件读档（与 FgSiegeSelfCheck 同一写法）：卸载世界、复位会话状态、走正式读档编排、载入家园（resume）。</summary>
        private static CampaignState RestoreSlot(out string fail)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
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

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        private static BuildingRecord Core(CampaignState s) => s.BuildingRecords.First(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);

        private static float Dur(CampaignState s, BuildingRecord b) => RepairDroneService.DurabilityOf(s, b);

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static string LastNotify(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Text ?? string.Empty;

        private static string Short(string s) => s == null ? string.Empty : s.Length > 160 ? s.Substring(0, 160) + "…" : s;

        private static CampaignState NewWorld(int seed, bool observe = true)
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
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
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
