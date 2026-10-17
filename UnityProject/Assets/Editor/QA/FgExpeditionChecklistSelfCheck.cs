using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
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
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Expedition;
using GameLogic.UI.Kit;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>FG8-EXP-01（FG08 FGR-EXP-001 派遣检查单；FGU-31；验收 FGT-EXP-001）自检：
    /// 硬约束（伤势、迷雾深处、带宽超限）真拦得住、出发事务同样拒绝且不改世界；提醒（覆盖、货舱、静默夜、劳动力、伤势偏重、反制）各自按真实条件出现；
    /// 远征队改名 / 设颜色 / 名单记忆、旧档兼容、真实派遣后存读档；派遣准备面板 UXML 布局探针。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgExpeditionChecklistSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgExpeditionChecklistSelfCheck.RunFrom
    /// 测试捷径（登记在证据里）：建筑直接登记为已运行、区域解锁直接置 Available（真实解锁由 FG1 / FG5 覆盖）。
    /// </summary>
    public static class FgExpeditionChecklistSelfCheck
    {
        private const int Slot = 7;
        private const string PrepUxml = "Assets/GameRes/Raw/UI/Expedition/ExpeditionPrepPanel.uxml";
        private static StringBuilder _report;
        private static int _pass;
        private static int _fail;
        private static int _seq = 9100;
        private static double _fakeNow;
        private static string _dir;

        /// <summary>单段入口（批处理）：bash tools/unity-validate.sh GameLogic.EditorTools.FgExpeditionChecklistSelfCheck.RunFrom</summary>
        public static void RunFrom()
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
            Line("\n[派遣检查单] 硬约束（伤势 / 迷雾深处 / 带宽超限）、提醒（覆盖 / 货舱 / 静默夜 / 劳动力 / 伤势偏重）、远征队改名设色与名单记忆、存读档、布局探针（FG8-EXP-01）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }

            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgexpchk-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                GameRoot.BindWorldProviders();

                Step(CheckData);
                Step(CheckHardDamaged);
                Step(CheckHardFog);
                Step(CheckHardBandwidth);
                Step(CheckRemindCoverage);
                Step(CheckRemindOthers);
                Step(CheckRosterMinAndGroup);
                Step(CheckTeamRenameColor);
                Step(CheckDestinationChoice);
                Step(CheckFogEdgeRelease);
                Step(CheckPanelNegativeLogic);
                Step(CheckComposeEntryStash);
                Step(CheckTeamSaveLoadAndDepart);
                Step(CheckLayoutProbe);
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                WorldSimulation.UnloadAll();
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
            Line($"  · [派遣检查单] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 检查 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            bool minOk = GridContent.TryGetTuning("exp.check.min_health_pct", out float minPct) && minPct > 0f && minPct < 1f;
            bool warnOk = GridContent.TryGetTuning("exp.check.warn_health_pct", out float warnPct) && warnPct >= minPct && warnPct <= 1f;
            Expect(minOk && warnOk, $"D1 伤势调参入 Luban（派遣下限 {minPct:F2} ≤ 警告线 {warnPct:F2}）");
            bool fogText = GameText.Get("exp.check.hard.fog") != "exp.check.hard.fog";
            bool reminderTexts = new[] { "exp.check.rem.coverage", "exp.check.rem.cargo", "exp.check.rem.enemy", "exp.check.rem.damaged", "exp.check.rem.hazard", "exp.check.hard.damaged", "exp.check.remind.header", "exp.team.default_name", "exp.team.preselect_hint", "exp.status.too_damaged" }
                .All(k => GameText.Get(k) != k);
            bool colorTexts = Enumerable.Range(0, 8).All(i => GameText.Get("exp.team.color." + i) != "exp.team.color." + i);
            Expect(fogText && reminderTexts && colorTexts, "D2 检查单与远征队文本键全部能取到（不是 ⟦key⟧ 缺键回退）");
            Expect(ExpeditionTeamService.ColorCount >= 1 && ExpeditionTeamService.ColorCount <= 8, $"D3 远征队颜色种数在 1～8（当前 {ExpeditionTeamService.ColorCount}）");
        }

        /// <summary>硬约束：伤势过重的机器不能派遣（检查单 + 面板 + 出发事务同一份结果；出发被拒且不改世界）。</summary>
        private static void CheckHardDamaged()
        {
            CampaignState s = NewWorld(7141);
            PrepareRegion(s, available: true);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f));
            int c = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(8f, -4f));
            int[] roster = { a, b, c };
            MachineRecord hurt = Rec(b);
            if (hurt == null)
            {
                Fail("D4 前提失败：机器生成失败");
                return;
            }
            // 边界：正好等于派遣下限不拦（判定是“低于”）。
            GridContent.TryGetTuning("exp.check.min_health_pct", out float minPct);
            hurt.Health = hurt.MaxHealth * minPct;
            bool atEdgeOk = !ExpeditionDepartureService.IsTooDamaged(hurt);
            // 伤势过重：生命 10%。
            hurt.Health = hurt.MaxHealth * 0.1f;
            ExpeditionDepartureService.RosterValidation v = ExpeditionDepartureService.ValidateRoster(s, roster);
            bool blockedReason = v.BlockingReasons.Contains($"machine-too-damaged:{b}");
            ExpeditionChecklistService.Checklist cl = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool checklistBlocked = cl.Blocked && cl.Hard.Any(h => h.Code == $"machine-too-damaged:{b}" && h.Text.Contains(MachineNaming.Short(hurt)));
            bool notEligible = !ExpeditionDepartureService.BuildIntel(s, hurt).Eligible;
            Expect(atEdgeOk && blockedReason && checklistBlocked && notEligible,
                $"D4 伤势过重（生命10%）的机器：校验拦截、检查单硬约束出现并写明机器与生命、不可派（边界=下限不拦：{atEdgeOk}）");

            int homeBefore = HomeMachines().Count;
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(roster, true);
            bool worldUnchanged = WorldSimulation.ActiveExpedition == null && HomeMachines().Count == homeBefore;
            Expect(dep.Outcome == ExpeditionDepartureService.DepartureOutcome.Blocked
                   && dep.Reasons.Any(r => r.StartsWith("machine-too-damaged:", StringComparison.Ordinal)) && worldUnchanged,
                $"D5 负向：伤势过重的名单出发被拒（{dep.Outcome}），没有远征在外，家园机器数不变");
        }

        /// <summary>硬约束：目的地在迷雾深处（区域仍 Locked）不能派遣，检查单给出迷雾文案。</summary>
        private static void CheckHardFog()
        {
            CampaignState s = NewWorld(7142);
            PrepareRegion(s, available: false);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            bool locked = ruins != null && ruins.State == RegionState.Locked;
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f));
            int c = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(8f, -4f));
            int[] roster = { a, b, c };
            ExpeditionChecklistService.Checklist cl = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool fogBlocked = cl.Blocked && cl.Hard.Any(h => h.Code == "destination-in-fog" && h.Text == GameText.Get("exp.check.hard.fog"));
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(roster, true);
            bool worldUnchanged = WorldSimulation.ActiveExpedition == null;
            Expect(locked && fogBlocked && dep.Outcome == ExpeditionDepartureService.DepartureOutcome.Blocked && worldUnchanged,
                $"D6 负向：目的地在迷雾深处（Locked）：检查单硬约束写明迷雾，出发被拒（{dep.Outcome}），没有远征在外");
        }

        /// <summary>硬约束：带宽超限（逐台加机器直到校验给出 bandwidth-exceeded；检查单与出发同时拦）。</summary>
        private static void CheckHardBandwidth()
        {
            CampaignState s = NewWorld(7143);
            PrepareRegion(s, available: true);
            var ids = new List<int>();
            bool exceeded = false;
            for (int k = 0; k < 12 && !exceeded; k++)
            {
                int id = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f + k * 2f, -8f));
                if (id == 0)
                {
                    break;
                }
                ids.Add(id);
                exceeded = ExpeditionDepartureService.ValidateRoster(s, ids).BlockingReasons.Any(r => r.StartsWith("bandwidth-exceeded", StringComparison.Ordinal));
            }
            ExpeditionChecklistService.Checklist cl = ExpeditionChecklistService.Build(s, ids, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool checklistHard = cl.Hard.Any(h => h.Code.StartsWith("bandwidth-exceeded", StringComparison.Ordinal));
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(ids.ToArray(), true);
            Expect(exceeded && cl.Blocked && checklistHard && dep.Outcome == ExpeditionDepartureService.DepartureOutcome.Blocked && WorldSimulation.ActiveExpedition == null,
                $"D7 负向：带宽超限（{ids.Count} 台）：检查单与出发同时拦截（{dep.Outcome}），没有远征在外");
        }

        /// <summary>提醒：路线走出信号覆盖——覆盖源全部失效时出现“走出覆盖”提醒，全部覆盖时不出现（只是提醒，不拦派遣）。</summary>
        private static void CheckRemindCoverage()
        {
            CampaignState s = NewWorld(7144);
            PrepareRegion(s, available: true);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f));
            int c = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(8f, -4f));
            int[] roster = { a, b, c };
            var snap = ExpeditionDepartureService.BuildPrepSnapshot(s);
            try
            {
                SignalCoverageService.OverrideForTests = (site, pos) =>
                    new SignalCoverageSample(false, true, -10f, pos, 0f, SignalCoverageSourceKind.None);
                ExpeditionChecklistService.Checklist uncovered = ExpeditionChecklistService.Build(s, roster, snap);
                bool warned = uncovered.Reminders.Any(r => r.Code == "coverage");
                SignalCoverageService.OverrideForTests = (site, pos) =>
                    new SignalCoverageSample(true, true, 50f, pos, 200f, SignalCoverageSourceKind.None);
                ExpeditionChecklistService.Checklist covered = ExpeditionChecklistService.Build(s, roster, snap);
                bool quiet = !covered.Reminders.Any(r => r.Code == "coverage");
                Expect(warned && quiet && !uncovered.Blocked,
                    $"D8 路线走出覆盖：全部无覆盖时出现“走出覆盖”提醒（{warned}），全覆盖时没有（{quiet}），且只是提醒、不拦派遣");
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
            }
        }

        /// <summary>提醒：货舱低于建议、劳动力下降、伤势偏重（不拦派遣）、敌方反制（真实计算的 Adaptation 字段）。</summary>
        private static void CheckRemindOthers()
        {
            CampaignState s = NewWorld(7145);
            PrepareRegion(s, available: true);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f));
            int c = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(8f, -4f));
            int[] roster = { a, b, c };
            ExpeditionDepartureService.PrepSnapshot snap = ExpeditionDepartureService.BuildPrepSnapshot(s);
            ExpeditionDepartureService.RosterValidation v = ExpeditionDepartureService.ValidateRoster(s, roster);
            RegionRecord region = FracturedCityRegion.Find(s);
            int suggested = ExpeditionDepartureService.MinRecommendedCargoSlots(ExpeditionDepartureService.ExpeditionTarget.SilentRuins, region);
            ExpeditionChecklistService.Checklist cl = ExpeditionChecklistService.Build(s, roster, snap);
            // 满编：货舱是否低于建议，必须与提醒是否出现一致（两个方向都会被核对）。
            bool cargoRule = (v.TotalCargoSlots > 0 && v.TotalCargoSlots < suggested) == cl.Reminders.Any(r => r.Code == "cargo");

            // D9 强制正向 1：单台机器（舱位 4）低于建议舱位 → 必须出现货舱提醒，文字带实际舱位与建议值。
            int[] solo = { a };
            ExpeditionDepartureService.RosterValidation vSolo = ExpeditionDepartureService.ValidateRoster(s, solo);
            ExpeditionChecklistService.Checklist clSolo = ExpeditionChecklistService.Build(s, solo, snap);
            bool cargoForced = vSolo.TotalCargoSlots > 0 && vSolo.TotalCargoSlots < suggested;
            string cargoText = GameText.Format("exp.check.rem.cargo", vSolo.TotalCargoSlots, suggested);
            bool cargoShown = cargoForced && clSolo.Reminders.Any(r => r.Code == "cargo" && r.Text == cargoText);

            // D9 强制正向 2：劳动力预告恒有文字（“不变”也是预告），必须作为提醒出现且文字一致。
            string labor = MachineRoster.LaborForecastText(s, roster);
            bool laborShown = !string.IsNullOrEmpty(labor) && cl.Reminders.Any(r => r.Code == "labor" && r.Text == labor);

            // D9 强制正向 3：静默夜进行中（Active）→ 检查单必须出现静默夜提醒，且文字与 ExpeditionCrossText 一致。
            string silent = SilentNightService.ExpeditionCrossText(s);
            bool silentRule = (silent == null) == !cl.Reminders.Any(r => r.Code == "silent_night");
            SilentNightState night = s.Weather?.SilentNight;
            bool silentForced = false;
            if (night != null)
            {
                bool wasActive = night.Active;
                long wasStart = night.StartTick;
                long wasEnd = night.EndTick;
                int wasDay = night.NextDay;
                night.NextDay = 1;
                night.StartTick = 0;
                night.EndTick = 1;
                night.Active = true;
                string forcedSilent = SilentNightService.ExpeditionCrossText(s);
                ExpeditionChecklistService.Checklist clSilent = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
                silentForced = !string.IsNullOrEmpty(forcedSilent)
                    && clSilent.Reminders.Any(r => r.Code == "silent_night" && r.Text == forcedSilent);
                night.Active = wasActive;
                night.StartTick = wasStart;
                night.EndTick = wasEnd;
                night.NextDay = wasDay;
            }

            Expect(cargoRule && cargoShown && laborShown && silentRule && silentForced,
                $"D9 三类提醒强制触发：货舱（单台 {vSolo.TotalCargoSlots}/建议 {suggested}，出现={cargoShown}）、劳动力预告（出现={laborShown}）、静默夜进行中（出现={silentForced}）；满编一致性（{cargoRule}/{silentRule}）");

            // 伤势偏重：介于警告线与派遣下限之间，只提醒、不拦。
            MachineRecord hurt = Rec(b);
            GridContent.TryGetTuning("exp.check.min_health_pct", out float minPct);
            GridContent.TryGetTuning("exp.check.warn_health_pct", out float warnPct);
            hurt.Health = hurt.MaxHealth * (minPct + warnPct) / 2f; // 介于派遣下限与警告线之间
            ExpeditionChecklistService.Checklist warn = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool warnReminder = warn.Reminders.Any(r => r.Code == "damaged");
            bool notBlocked = !warn.Hard.Any(h => h.Code == $"machine-too-damaged:{b}");
            hurt.Health = hurt.MaxHealth;
            ExpeditionChecklistService.Checklist healthy = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool noWarnWhenHealthy = !healthy.Reminders.Any(r => r.Code == "damaged");
            Expect(warnReminder && notBlocked && noWarnWhenHealthy,
                $"D10 伤势偏重（警告线与下限之间）：出现伤势提醒且不拦派遣（{warnReminder}/{notBlocked}），满血时没有（{noWarnWhenHealthy}）");

            // 敌方反制：用真实快照的 Adaptation 字段（非 None）喂给检查单，验证提醒文字带出反制名与应对。
            ExpeditionDepartureService.PrepSnapshot counterSnap = new ExpeditionDepartureService.PrepSnapshot(
                true, null, snap.Machines, snap.BandwidthCapacity, snap.MinRecommendedCargoSlots, snap.EnemyIntelText,
                snap.EnemyAlertLevel, snap.ExpeditionCount, ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost,
                snap.ObjectivePreviewText, AdaptationCatalog.Flanker, AdaptationCatalog.Describe(AdaptationCatalog.Flanker).DisplayName,
                snap.AdaptationSourceText, AdaptationCatalog.Describe(AdaptationCatalog.Flanker).HazardText,
                AdaptationCatalog.Describe(AdaptationCatalog.Flanker).CounterHintText, false, null);
            ExpeditionChecklistService.Checklist counter = ExpeditionChecklistService.Build(s, roster, counterSnap);
            ExpeditionChecklistService.Item enemy = counter.Reminders.FirstOrDefault(r => r.Code == "enemy_counter");
            bool counterText = enemy.Text != null && enemy.Text.Contains(AdaptationCatalog.Describe(AdaptationCatalog.Flanker).DisplayName);
            bool noCounterWhenNone = snap.AdaptationId != AdaptationCatalog.None || !cl.Reminders.Any(r => r.Code == "enemy_counter");
            Expect(counterText && noCounterWhenNone, "D11 敌方反制：有反制时出现提醒并带出反制名，无反制时没有（与出发锁定同源的 Adaptation 字段）");
        }

        /// <summary>FG8-EXP-01 修复轮：派遣人数下限（FG08:40 “至少一台机器”，与 OBJ-04 的 3 台门槛分开）、
        /// 连按两次编组键的判定（FG13 第 5 节，纯函数）、编组镜头中心（成员中心）。</summary>
        private static void CheckRosterMinAndGroup()
        {
            CampaignState s = NewWorld(7146);
            PrepareRegion(s, available: true);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            ExpeditionDepartureService.RosterValidation one = ExpeditionDepartureService.ValidateRoster(s, new[] { a });
            ExpeditionDepartureService.RosterValidation none = ExpeditionDepartureService.ValidateRoster(s, Array.Empty<int>());
            bool oneAllowed = !one.BlockingReasons.Any(r => r.StartsWith("roster-too-small", StringComparison.Ordinal));
            bool noneBlocked = none.BlockingReasons.Any(r => r.StartsWith("roster-too-small", StringComparison.Ordinal));
            bool minIsSpec = ExpeditionDepartureService.MinDepartureSize == 1 && ExpeditionDepartureService.MinRosterSize == 3;
            Expect(oneAllowed && noneBlocked && minIsSpec,
                $"D12 人数下限：派遣硬约束至少 1 台（单台可派={oneAllowed}，空名单被拦={noneBlocked}），OBJ-04 的 3 台门槛保持不变（{minIsSpec}）");

            bool tap = RegionSquadCommandSystem.IsGroupDoubleTap(2, 10f, 2, 10.2f);
            bool slowTap = !RegionSquadCommandSystem.IsGroupDoubleTap(2, 10f, 2, 11f);
            bool otherSlot = !RegionSquadCommandSystem.IsGroupDoubleTap(2, 10f, 3, 10.1f);
            bool firstPress = !RegionSquadCommandSystem.IsGroupDoubleTap(0, float.NegativeInfinity, 2, 10f);
            Vector2? centre = RegionSquadCommandSystem.CentroidOf(new[] { new Vector2(0f, 0f), new Vector2(4f, 2f) });
            bool centreOk = centre.HasValue && Mathf.Approximately(centre.Value.x, 2f) && Mathf.Approximately(centre.Value.y, 1f);
            bool noCentre = !RegionSquadCommandSystem.CentroidOf(Array.Empty<Vector2>()).HasValue;
            Expect(tap && slowTap && otherSlot && firstPress && centreOk && noCentre,
                $"D13 连按两次编组键：同键 0.35 秒内判定为双击（{tap}），超时/换键/首次按下不算（{slowTap}/{otherSlot}/{firstPress}），镜头取成员中心（{centreOk}/{noCentre}）");
        }

        /// <summary>远征队：改名（同机器改名规则）、颜色、名单记忆（去重、上限）、旧档兼容。</summary>
        private static void CheckTeamRenameColor()
        {
            CampaignState s = NewWorld(7146);
            string defaultName = GameText.Get("exp.team.default_name");
            Expect(ExpeditionTeamService.DisplayName(s) == defaultName, "E1 新战役默认队名");
            bool renamed = ExpeditionTeamService.TryRename(s, "  铁锤队  ", out _) && ExpeditionTeamService.StateOf(s).Name == "铁锤队";
            bool rejectedAngle = !ExpeditionTeamService.TryRename(s, "a<b", out string angleMsg) && ExpeditionTeamService.StateOf(s).Name == "铁锤队" && angleMsg.Length > 0;
            bool rejectedLong = !ExpeditionTeamService.TryRename(s, new string('队', 17), out _) && ExpeditionTeamService.StateOf(s).Name == "铁锤队";
            bool rejectedBlank = !ExpeditionTeamService.TryRename(s, "   ", out _) && ExpeditionTeamService.StateOf(s).Name == "铁锤队";
            Expect(renamed && rejectedAngle && rejectedLong && rejectedBlank,
                "E2 改名：去首尾空白生效；含 < > 、超 16 字、空白均被拒且原名不变（与机器改名同一套规则）");
            ExpeditionTeamService.ResetName(s);
            Expect(ExpeditionTeamService.DisplayName(s) == defaultName, "E3 清空名字 = 恢复默认队名");

            bool colored = ExpeditionTeamService.TrySetColor(s, 5) && ExpeditionTeamService.ColorIndexOf(s) == 5
                           && ExpeditionTeamService.ColorName(5) == GameText.Get("exp.team.color.5");
            bool badColor = !ExpeditionTeamService.TrySetColor(s, -1) && !ExpeditionTeamService.TrySetColor(s, ExpeditionTeamService.ColorCount)
                            && ExpeditionTeamService.ColorIndexOf(s) == 5;
            Expect(colored && badColor, "E4 颜色：设下标 5 生效；越界（-1 / 种数）被拒且颜色不变");

            int a = 4001, b = 4002, c = 4003, d = 4004, e = 4005, f = 4006;
            ExpeditionTeamService.RememberMembers(s, new[] { a, b, a, c, d, e, f });
            int[] members = ExpeditionTeamService.StateOf(s).Members;
            Expect(members.SequenceEqual(new[] { a, b, c, d, e }), "E5 名单记忆：去重、保持先后、最多 5 台（派遣上限）");

            s.Grid.ExpeditionTeam = null;
            Expect(ExpeditionTeamService.DisplayName(s) == defaultName && ExpeditionTeamService.ColorIndexOf(s) == 0
                   && ExpeditionTeamService.RememberedMembers(s).Length == 0,
                "E6 旧档没有远征队字段：默认名、颜色 0、空名单，不抛异常");
        }

        /// <summary>真实派遣成功后名单写入远征队，改名 / 颜色随存档存下，真文件读回一致。</summary>
        private static void CheckTeamSaveLoadAndDepart()
        {
            CampaignState s = NewWorld(7147);
            PrepareRegion(s, available: true);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f));
            int c = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(8f, -4f));
            int[] roster = { a, b, c };
            CampaignSession.Set(Slot, s);
            ExpeditionTeamService.TryRename(s, "铁锤队", out _);
            ExpeditionTeamService.TrySetColor(s, 3);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(roster, true);
            if (!dep.Success)
            {
                Fail($"E7 前提失败：真实派遣未成功（{dep.Outcome}：{string.Join("，", dep.Reasons)}）");
                return;
            }
            CampaignState now = CampaignSession.Current;
            bool remembered = ExpeditionTeamService.StateOf(now).Members.SequenceEqual(roster);
            bool kept = ExpeditionTeamService.DisplayName(now) == "铁锤队" && ExpeditionTeamService.ColorIndexOf(now) == 3;
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            bool restored = rr.Success && rr.State != null && ExpeditionTeamService.DisplayName(rr.State) == "铁锤队"
                            && ExpeditionTeamService.ColorIndexOf(rr.State) == 3
                            && ExpeditionTeamService.StateOf(rr.State).Members.SequenceEqual(roster);
            Expect(remembered && kept && save.Success && restored,
                $"E7 真实派遣后远征队名单 / 队名 / 颜色随存档存下，真文件读回一致（存档 {save.Success}，读档 {(rr.Success ? "成功" : "失败：" + rr.Message)}）");
        }

        /// <summary>FG8-EXP-01 修复轮（卡片结果“指定目的地”）：可派区域列表、默认优先级不变、玩家指定生效、指定的区域不可派时回落。</summary>
        private static void CheckDestinationChoice()
        {
            CampaignState s = NewWorld(7143);
            PrepareRegion(s, available: true);
            RegionRecord fractured = FracturedCityRegion.Find(s);
            RegionRecord foundry = FoundryOutpostRegion.Find(s);
            if (fractured == null || foundry == null)
            {
                Fail("D14 前提失败：区域记录缺失");
                return;
            }
            foundry.State = RegionState.Locked;
            bool onlyFractured = ExpeditionDepartureService.ReachableTargets(s)
                .SequenceEqual(new[] { ExpeditionDepartureService.ExpeditionTarget.SilentRuins });
            foundry.State = RegionState.Available;
            ExpeditionDepartureService.ExpeditionTarget[] both = ExpeditionDepartureService.ReachableTargets(s);
            bool bothListed = both.Length == 2
                && both[0] == ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost
                && both[1] == ExpeditionDepartureService.ExpeditionTarget.SilentRuins;
            bool defaultKept = ExpeditionDepartureService.ResolveTarget(s) == ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost;
            bool pickedRuins = ExpeditionDepartureService.ResolveTarget(s, ExpeditionDepartureService.ExpeditionTarget.SilentRuins)
                == ExpeditionDepartureService.ExpeditionTarget.SilentRuins
                && ExpeditionDepartureService.BuildPrepSnapshot(s, ExpeditionDepartureService.ExpeditionTarget.SilentRuins).Target
                == ExpeditionDepartureService.ExpeditionTarget.SilentRuins;
            foundry.State = RegionState.Locked;
            bool fallback = ExpeditionDepartureService.ResolveTarget(s, ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost)
                == ExpeditionDepartureService.ExpeditionTarget.SilentRuins;
            Expect(onlyFractured && bothListed && defaultKept && pickedRuins && fallback,
                "D14 指定目的地：只有一处可派时列表只含它；两处都可派时列出两项且默认仍为铸造前哨外围；玩家指定的破碎都市生效；指定的区域不可派时回落");
        }

        /// <summary>FG8-EXP-01 修复轮（FGR-EXP-001 硬约束“目的地已探索或在迷雾边缘”，区域级口径）：迷雾边缘可派并提醒；已探索可派且无此提醒；深迷雾硬拦。</summary>
        private static void CheckFogEdgeRelease()
        {
            CampaignState s = NewWorld(7144);
            PrepareRegion(s, available: true);
            RegionRecord fractured = FracturedCityRegion.Find(s);
            if (fractured == null)
            {
                Fail("D15 前提失败：破碎都市记录缺失");
                return;
            }
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f));
            int c = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(8f, -4f));
            int[] roster = { a, b, c };
            const ExpeditionDepartureService.ExpeditionTarget ruins = ExpeditionDepartureService.ExpeditionTarget.SilentRuins;

            bool edgeClass = ExpeditionDepartureService.ClassifyDestination(s, ruins) == ExpeditionDepartureService.DestinationFog.FogEdge;
            ExpeditionChecklistService.Checklist edge = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool edgeReleased = edgeClass && !edge.Hard.Any(h => h.Code == "destination-in-fog");
            bool edgeReminded = edge.Reminders.Any(r => r.Code == "destination-fog-edge" && r.Text == GameText.Get("exp.check.rem.fog_edge"));

            fractured.State = RegionState.Cleared;
            bool exploredClass = ExpeditionDepartureService.ClassifyDestination(s, ruins) == ExpeditionDepartureService.DestinationFog.Explored;
            ExpeditionChecklistService.Checklist explored = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool exploredReleased = exploredClass && !explored.Hard.Any(h => h.Code == "destination-in-fog")
                && !explored.Reminders.Any(r => r.Code == "destination-fog-edge");

            fractured.State = RegionState.Locked;
            bool deepClass = ExpeditionDepartureService.ClassifyDestination(s, ruins) == ExpeditionDepartureService.DestinationFog.DeepFog;
            ExpeditionChecklistService.Checklist deep = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool deepBlocked = deepClass && deep.Blocked && deep.Hard.Any(h => h.Code == "destination-in-fog");

            Expect(edgeReleased && edgeReminded && exploredReleased && deepBlocked,
                "D15 迷雾边缘放行并写“尚未进入过”的提醒；已探索放行且无此提醒；深迷雾（Locked）硬拦并写出迷雾文字（区域级口径，见 ADR）");
        }

        /// <summary>FG8-EXP-01 修复轮（负向：目的地在迷雾深处 / 伤势过重）：面板层的纯逻辑（迷雾文字、可留在名单里的机器），不起 UI 也能测。</summary>
        private static void CheckPanelNegativeLogic()
        {
            CampaignState deepState = NewWorld(7145);
            PrepareRegion(deepState, available: false);
            string blocked = ExpeditionPrepPanelUIToolkit.ComposeBlockedText(deepState, ExpeditionDepartureService.BuildPrepSnapshot(deepState));
            bool fogText = blocked.Contains(GameText.Get("exp.check.hard.fog"));

            CampaignState s = NewWorld(7146);
            PrepareRegion(s, available: true);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f));
            int c = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(8f, -4f));
            MachineRecord hurt = Rec(b);
            if (hurt == null)
            {
                Fail("D16 前提失败：机器生成失败");
                return;
            }
            hurt.Health = hurt.MaxHealth * 0.1f;
            int[] keep = ExpeditionPrepPanelUIToolkit.SelectableIds(ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool keepsHurt = keep.Contains(b);
            bool keepsHealthy = keep.Contains(a) && keep.Contains(c);
            Expect(fogText && keepsHurt && keepsHealthy,
                "D16 面板层：目的地在迷雾深处时“无法出发”区写出迷雾硬约束文字；伤势过重的机器仍留在名单里（可取消，由检查单写明原因）");
        }

        /// <summary>FG8-EXP-01 修复轮（卡片结果“从名册或战略地图编成远征队”）：入口登记的名单一次性交给面板；空名单不带入（按常规预选）。</summary>
        private static void CheckComposeEntryStash()
        {
            ExpeditionPrepPanelUIToolkit.RequestCompose(new[] { 4101, 4102 });
            int[] stored = ExpeditionPrepPanelUIToolkit.PendingComposeForTests;
            bool stash = stored != null && stored.Length == 2 && stored[0] == 4101 && stored[1] == 4102;
            ExpeditionPrepPanelUIToolkit.RequestCompose(null);
            bool emptyIsNormal = ExpeditionPrepPanelUIToolkit.PendingComposeForTests == null;
            Expect(stash && emptyIsNormal, "D17 名册 / 战略地图“编成远征队”登记的名单交给派遣准备面板；空名单不带入（按常规预选）");
        }

        /// <summary>派遣准备面板 UXML（含检查单提醒、远征队行）布局探针：四种分辨率 + 超长文字 + USS 体检。</summary>
        private static void CheckLayoutProbe()
        {
            string probe = UiToolkitLayoutProbe.Probe(PrepUxml, "ExpeditionPrepPanelRoot");
            Line(probe);
            Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "F1 派遣准备面板布局探针 PASS（检查单提醒 + 远征队改名 / 颜色行）：" + probe.Split('\n')[0]);
        }

        // ── 工具（与 FgMachineRosterSelfCheck 同一套世界构造 / 生成写法）──────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 600)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            StandingRuleService.ResetForTests();
            MachineRoster.ResetForTests();
            _fakeNow = 0;
            MachineRoster.Clock = () => _fakeNow;
            _seq = 9100;
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            FgProductionSelfCheck.PowerUp(s);
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse && b.ConstructionState == BuildingConstructionState.Damaged)
                {
                    b.ConstructionState = BuildingConstructionState.Operational;
                    b.Health = BuildingOps.MaxDurability(b.BuildingTypeId);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            foreach (StandingRuleRecord r in StandingRuleService.Ordered(s).ToList())
            {
                StandingRuleService.TryDelete(s, r.Serial, out _);
            }
            return s;
        }

        /// <summary>测试捷径：信号塔 / 发电机直接置为运行，并把破碎都市解锁（或保持迷雾深处）。真实建设由 FG1 / FG4 覆盖。</summary>
        private static void PrepareRegion(CampaignState s, bool available)
        {
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower || b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator))
                {
                    b.ConstructionState = BuildingConstructionState.Operational;
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && available && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
        }

        private static int Spawn(CampaignState s, string chassis, string blueprint, Vector2 offset, float hp = 120f)
        {
            Vector2 core = HomeValleyLayout.Core.Position;
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, core + offset, hp, hp);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static List<MachineRecord> HomeMachines() =>
            MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).OrderBy(m => m.LogicId).ToList();

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

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
            _report.AppendLine(text);
        }
    }
}
