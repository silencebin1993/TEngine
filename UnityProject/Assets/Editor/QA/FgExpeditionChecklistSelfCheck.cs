using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
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
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>FG8-EXP-01（FG08 FGR-EXP-001 派遣检查单；FGU-31；验收 FGT-EXP-001）自检，Opus 返工版：
    /// - 硬约束只有至少一台 / 带宽 / 目的地（区域没开放时写真实的解锁条件），出发事务同样拒绝且不改世界；没有固定人数上限；
    /// - 伤势、无武器、货舱、劳动力、静默夜、反制、迷雾边缘都是提醒（不拦派遣，后果写明），真实派遣能成功；
    /// - “路线走出覆盖”（FG8-EXP-02 交付 DEBT-FG8EXP01-03）：真实路线 = 核心到目的地锚点的层级寻路路线，覆盖外时出现、沿路线铺中继后消失（D8）；危害带仍是接口（DEBT-FG8EXP01-04）；
    /// - 正式入口：编组键连按两次（假键盘 → TickInput → 镜头焦点到编组中心）、名册“编成远征队”按钮（真 UXML，面板关 / 已开 / 关闭清空 / 换存档丢弃）、
    ///   目的地下拉框 → 出发按钮 → TryDepart 的目标、队名框失焦；全部新文字走文本键（中英两列都有、UXML 无字面）；
    /// - 远征队改名 / 颜色 / 名单记忆、旧档兼容、真实派遣后真文件存读档；布局探针。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgExpeditionChecklistSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgExpeditionChecklistSelfCheck.RunFrom
    /// 测试捷径（登记在证据里）：建筑直接登记为已运行、区域解锁直接置 Available、远征中的机器直接移到远征地点（真实解锁 / 出发由 FG1 / FG5 / 本段 D5 覆盖）。
    /// 收尾还原全部全局替身（假时钟、编组时钟、路线 / 危害钩子、输入、镜头、面板开关）。
    /// </summary>
    public static class FgExpeditionChecklistSelfCheck
    {
        private const int Slot = 7;
        private const string PrepUxml = "Assets/GameRes/Raw/UI/Expedition/ExpeditionPrepPanel.uxml";
        private const string PrepRowUxml = "Assets/GameRes/Raw/UI/Expedition/templates/ExpeditionMachineRow.uxml";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const string SignalCoreUxml = UiKitFolder + "SignalCorePanel.uxml";
        private static StringBuilder _report;
        private static int _pass;
        private static int _fail;
        private static double _fakeNow;
        private static float _fakeRealTime;
        private static string _dir;

        /// <summary>本段新增 / 使用的全部文本键（D2 逐个查中英两列）。</summary>
        private static readonly string[] TextKeys =
        {
            "exp.check.remind.header", "exp.check.hard.too_small", "exp.check.hard.bandwidth", "exp.check.hard.machine",
            "exp.check.hard.machine.dead", "exp.check.hard.machine.deployed", "exp.check.hard.machine.factory", "exp.check.hard.machine.busy",
            "exp.check.hard.machine.unknown", "exp.check.hard.machine.duplicate", "exp.check.hard.locked", "exp.check.hard.locked.ruins",
            "exp.check.hard.locked.foundry", "exp.check.mark.done", "exp.check.mark.todo", "exp.check.hard.no_campaign", "exp.check.hard.other",
            "exp.check.hard.autosave_failed", "exp.check.hard.transaction_failed", "exp.check.hard.rollback_failed", "exp.check.sep",
            "exp.compose.unreachable", "exp.panel.counter.name", "exp.panel.counter.source", "exp.panel.counter.hazard", "exp.panel.counter.hint",
            "exp.check.rem.fog_edge", "exp.check.rem.coverage", "exp.check.rem.cargo", "exp.check.rem.cargo_none", "exp.check.rem.no_weapon",
            "exp.check.rem.labor", "exp.check.rem.enemy", "exp.check.rem.severe", "exp.check.rem.damaged", "exp.check.rem.hazard",
            "exp.check.rem.hazard_default", "exp.team.default_name", "exp.team.label", "exp.team.color_label", "exp.team.preselect_hint",
            "exp.compose.button", "exp.compose.button_home_only", "exp.compose.dropped", "exp.compose.applied", "exp.panel.title",
            "exp.panel.title_none", "exp.panel.depart", "exp.panel.close", "exp.panel.broadcast_off", "exp.panel.interrupt_confirm",
            "exp.panel.interrupt_cancel", "exp.panel.blocked", "exp.dest.label", "exp.dest.ruins", "exp.dest.foundry", "exp.dest.fog_edge_tag",
            "exp.list.title", "exp.summary.line", "exp.summary.weapon_yes", "exp.summary.weapon_no", "exp.status.too_damaged",
            "expedition.reason.underway",
        };

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
            Line("\n[派遣检查单] 硬约束（至少一台 / 带宽 / 目的地）、提醒（伤势 / 武器 / 货舱 / 劳动力 / 静默夜 / 反制 / 迷雾边缘；覆盖与危害带接口）、正式入口（编组双击、名册编成、目的地下拉、队名失焦）、远征队、存读档、文本键、布局探针（FG8-EXP-01 Opus 返工）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }

            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            bool hadCamera = Camera.main != null;
            Func<float> originalRealTime = RegionSquadCommandSystem.RealTime;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
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
                CameraDirector.RealDeltaTime = () => 0.05f;
                GameRoot.BindWorldProviders();

                Step(CheckData);
                Step(CheckSevereDamageIsReminder);
                Step(CheckDestinationLocked);
                Step(CheckBandwidthOnlyLimit);
                Step(CheckCoverageAndHazard);
                Step(CheckRemindOthers);
                Step(CheckSingleUnarmedDeparts);
                Step(CheckGroupDoubleTapFormal);
                Step(CheckTeamRenameColor);
                Step(CheckDestinationChoice);
                Step(CheckFogEdgeRelease);
                Step(CheckComposeEntryFormal);
                Step(CheckPanelDestinationDepart);
                Step(CheckPanelTeamNameAndTexts);
                Step(CheckTeamSaveLoadAndDepart);
                Step(CheckLayoutProbe);
            }
            finally
            {
                // 还原全部全局替身（RunAll 里后面的段不受本段影响）。
                ExpeditionChecklistService.RouteProvider = ExpeditionChecklistService.DefaultRoute; // FG8-EXP-02：默认就是真实路线，复位成默认（不是清空）
                ExpeditionChecklistService.HazardProbe = ExpeditionChecklistService.DefaultHazardProbe; // FG8-GEN-02：默认就是真实危害数据，复位成默认（不是清空）
                RegionSquadCommandSystem.RealTime = originalRealTime;
                CameraDirector.RealDeltaTime = originalDelta;
                SignalCoverageService.OverrideForTests = null;
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                MachineRoster.ResetForTests();
                StandingRuleService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                BuildingOps.ResetForTests();
                GameClock.ResetSession();
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                RosterPanelUIToolkit.InWorldOverrideForTests = false;
                RosterPanelUIToolkit.Close();
                UiEscapeStack.Clear();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                GameSettings.SetLanguage(originalLanguage);
                if (!hadCamera && Camera.main != null)
                {
                    UnityEngine.Object.DestroyImmediate(Camera.main.gameObject);
                }
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

        // ── 数据与文本 ─────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            bool severeOk = GridContent.TryGetTuning("exp.check.severe_health_pct", out float severe) && severe > 0f && severe < 1f;
            bool warnOk = GridContent.TryGetTuning("exp.check.warn_health_pct", out float warn) && warn >= severe && warn <= 1f;
            bool tapOk = GridContent.TryGetTuning("squad.group_double_tap_seconds", out float tap) && tap > 0f
                         && Mathf.Approximately(RegionSquadCommandSystem.GroupDoubleTapSeconds, tap);
            bool oldGone = !GridContent.TryGetTuning("exp.check.min_health_pct", out _);
            Expect(severeOk && warnOk && tapOk && oldGone,
                $"D1 调参入 Luban：重伤线 {severe:F2} ≤ 偏重线 {warn:F2}；编组连按间隔 {tap:F2} 秒（代码读的就是它）；旧的“派遣下限”调参已删除（{oldGone}）");

            // 真正查缺键：TryGet 两种语言都要取到（缺键时 Get 返回 ⟦key⟧，不能拿 Get(k) != k 判断）；反证：不存在的键必须查不到。
            var missing = new List<string>();
            foreach (string k in TextKeys)
            {
                if (!GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrEmpty(zh))
                {
                    missing.Add(k + "(中)");
                }
                if (!GameText.TryGet(k, GameLanguage.En, out string en) || string.IsNullOrEmpty(en))
                {
                    missing.Add(k + "(英)");
                }
            }
            bool probeMissing = !GameText.TryGet("exp.__selfcheck_missing_probe__", GameLanguage.ZhCn, out _);
            bool colorTexts = Enumerable.Range(0, 8).All(i => GameText.TryGet("exp.team.color." + i, GameLanguage.ZhCn, out _) && GameText.TryGet("exp.team.color." + i, GameLanguage.En, out _));
            bool oldKeysGone = !GameText.TryGet("exp.check.hard.fog", GameLanguage.ZhCn, out _) && !GameText.TryGet("exp.check.hard.damaged", GameLanguage.ZhCn, out _);
            Expect(missing.Count == 0 && probeMissing && colorTexts && oldKeysGone,
                $"D2 文本键 {TextKeys.Length} 个 + 8 种颜色名中英两列都能取到（缺：{(missing.Count == 0 ? "无" : string.Join("、", missing))}）；反证：不存在的键查不到（{probeMissing}）；旧的迷雾 / 伤势硬约束文本已删除（{oldKeysGone}）");
            Expect(ExpeditionTeamService.ColorCount >= 1 && ExpeditionTeamService.ColorCount <= 8, $"D3 远征队颜色种数在 1～8（当前 {ExpeditionTeamService.ColorCount}）");

            // UXML 不写字面：派遣准备面板（含 Demo 遗留的按钮）全部由代码按文本键填写。
            string uxml = File.ReadAllText(PrepUxml);
            var literal = new Regex("\\b(?:text|label)=\"([^\"]+)\"");
            List<string> hits = literal.Matches(uxml).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Expect(hits.Count == 0, $"D2b 派遣准备面板 UXML 没有 text / label 字面（{(hits.Count == 0 ? "0 处" : string.Join("、", hits))}）");
        }

        // ── 硬约束 / 提醒 ─────────────────────────────────────────────────────────

        /// <summary>伤势是提醒：重伤的机器可以留在名单里、校验通过、检查单逐台点名并写后果（向下取整不出现“25% 低于 25%”），真实派遣成功。</summary>
        private static void CheckSevereDamageIsReminder()
        {
            CampaignState s = NewWorld(7141);
            PrepareRegion(s, available: true);
            int a = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -4f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f));
            int[] roster = { a, b };
            MachineRecord hurt = Rec(b);
            if (hurt == null || Rec(a) == null)
            {
                Fail("D4 前提失败：机器生成失败");
                return;
            }
            float severe = ExpeditionDepartureService.SeverePct;
            hurt.Health = hurt.MaxHealth * (severe - 0.004f); // 例如 24.6%
            ExpeditionDepartureService.RosterValidation v = ExpeditionDepartureService.ValidateRoster(s, roster);
            ExpeditionChecklistService.Checklist cl = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            ExpeditionChecklistService.Item item = cl.Reminders.FirstOrDefault(r => r.Code == "severe:" + b);
            int shownPct = Mathf.FloorToInt((severe - 0.004f) * 100f);
            string want = GameText.Format("exp.check.rem.severe", MachineNaming.Short(hurt), shownPct, Mathf.RoundToInt(severe * 100f));
            bool eligible = ExpeditionDepartureService.BuildIntel(s, hurt).Eligible
                            && ExpeditionPrepPanelUIToolkit.SelectableIds(ExpeditionDepartureService.BuildPrepSnapshot(s)).Contains(b);
            Expect(v.Success && !cl.Blocked && item.Text == want && shownPct < Mathf.RoundToInt(severe * 100f) && eligible,
                $"D4 重伤（生命 {(severe - 0.004f) * 100f:F1}%）只是提醒：校验通过、不拦、检查单写“{item.Text}”、仍可勾选（{v.Success}/{!cl.Blocked}/{eligible}）");

            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(roster, true);
            // FG8-EXP-02：出发 = 沿地形行军；本段要在远征地点里接着测——测试快进到达（走正式的到达事务；行军本身由 FgExpeditionMarchSelfCheck 真实推进验证）。
            if (dep.Success)
            {
                ExpeditionMarchService.ArriveNowForTests(CampaignSession.Current);
            }
            bool away = dep.Success && WorldSimulation.ActiveExpedition != null && Rec(b) != null && Rec(b).RegionId != HomeValleyLayout.RegionId;
            Expect(away, $"D5 带着重伤机器真实派遣成功（{dep.Outcome}{(dep.Success ? string.Empty : "：" + string.Join("，", dep.Reasons))}），重伤机器已离开家园");
        }

        /// <summary>目的地还不能去：区域没开放时硬拦，文字是真实的解锁条件（逐项已完成 / 未完成，随状态翻转），不叫玩家去探索；出发被拒不改世界。</summary>
        private static void CheckDestinationLocked()
        {
            CampaignState s = NewWorld(7142);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            int[] roster = { a };
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null)
            {
                ruins.State = RegionState.Locked;
            }
            RegionRecord foundry = FoundryOutpostRegion.Find(s);
            if (foundry != null)
            {
                foundry.State = RegionState.Locked;
            }
            string done = GameText.Get("exp.check.mark.done");
            string todo = GameText.Get("exp.check.mark.todo");
            bool towerBefore = HomeValleySignal.UnlockTowerOperational(s);
            bool ercBefore = HomeValleySignal.UnlockHasErc003();
            ExpeditionChecklistService.Checklist cl = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            ExpeditionChecklistService.Item hard = cl.Hard.FirstOrDefault(h => h.Code == "destination-locked");
            string wantRuins = GameText.Format("exp.check.hard.locked.ruins", towerBefore ? done : todo, ercBefore ? done : todo);
            bool truthful = hard.Text != null && hard.Text.Contains(wantRuins) && !hard.Text.Contains("探索到") && hard.Text.Contains(GameText.Get("exp.check.hard.locked"));
            // 条件变化 → 文字跟着变（ERC-003 出现后第二项变成已完成）。
            Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(8f, -4f));
            string after = ExpeditionChecklistService.LockedConditionsText(s);
            bool flips = after.Contains(GameText.Format("exp.check.hard.locked.ruins", HomeValleySignal.UnlockTowerOperational(s) ? done : todo, done));
            string panelText = ExpeditionPrepPanelUIToolkit.ComposeBlockedText(s, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool panelSame = panelText == after;
            int homeBefore = HomeMachines().Count;
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(roster, true);
            // FG8-EXP-02：出发 = 沿地形行军；本段要在远征地点里接着测——测试快进到达（走正式的到达事务；行军本身由 FgExpeditionMarchSelfCheck 真实推进验证）。
            if (dep.Success)
            {
                ExpeditionMarchService.ArriveNowForTests(CampaignSession.Current);
            }
            bool unchanged = dep.Outcome == ExpeditionDepartureService.DepartureOutcome.Blocked && WorldSimulation.ActiveExpedition == null && HomeMachines().Count == homeBefore;
            bool refusedText = dep.Reasons.Length > 0 && ExpeditionChecklistService.DescribeHard(dep.Reasons[0]) == after;
            Expect(cl.Blocked && truthful && flips && panelSame && unchanged && refusedText,
                $"D6 负向：目的地没开放 → 硬约束写真实解锁条件（不提“探索”）：“{hard.Text?.Replace('\n', ' ')}”；ERC-003 出现后该项变为已完成（{flips}）；面板“无法出发”区与出发被拒同一份文字（{panelSame}/{refusedText}）；出发被拒、世界不变（{unchanged}）");
        }

        /// <summary>人数只受带宽约束：关闭广播降带宽后，带宽 + 1 台被单独拦下（只有带宽一条原因），少一台即可派；带宽够时 6 台以上也可派（没有 5 台上限）。</summary>
        private static void CheckBandwidthOnlyLimit()
        {
            CampaignState s = NewWorld(7143);
            PrepareRegion(s, available: true);
            float fullCap = HomeValleySignal.BandwidthCapacity(s);
            var ids = new List<int>();
            for (int k = 0; k < 7; k++)
            {
                int id = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f + k * 2f, -8f));
                if (id != 0)
                {
                    ids.Add(id);
                }
            }
            ExpeditionDepartureService.RosterValidation six = ExpeditionDepartureService.ValidateRoster(s, ids.Take(6).ToList());
            bool sixAllowed = fullCap >= 6f && six.Success && six.BlockingReasons.Length == 0;
            Expect(sixAllowed, $"D7a 没有固定人数上限：带宽 {fullCap:F0} 时 6 台 ERC-001（带宽 {six.TotalBandwidth:F0}）校验通过（{string.Join("，", six.BlockingReasons)}）");

            CampaignExposureLedger.SetTowerBroadcastOff(s, true);
            HomeValleyPowerGrid.Recompute(s);
            float cap = HomeValleySignal.BandwidthCapacity(s);
            int over = Mathf.FloorToInt(cap) + 1;
            List<int> tooMany = ids.Take(over).ToList();
            ExpeditionDepartureService.RosterValidation v = ExpeditionDepartureService.ValidateRoster(s, tooMany);
            ExpeditionChecklistService.Checklist cl = ExpeditionChecklistService.Build(s, tooMany, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool onlyBandwidth = cl.Hard.Length == 1 && cl.Hard[0].Code.StartsWith("bandwidth-exceeded", StringComparison.Ordinal)
                                 && cl.Hard[0].Text.Contains(cap.ToString("F0"));
            ExpeditionDepartureService.RosterValidation fits = ExpeditionDepartureService.ValidateRoster(s, ids.Take(over - 1).ToList());
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(tooMany, true);
            // FG8-EXP-02：出发 = 沿地形行军；本段要在远征地点里接着测——测试快进到达（走正式的到达事务；行军本身由 FgExpeditionMarchSelfCheck 真实推进验证）。
            if (dep.Success)
            {
                ExpeditionMarchService.ArriveNowForTests(CampaignSession.Current);
            }
            Expect(cap < fullCap && tooMany.Count == over && over <= 6 && !v.Success && onlyBandwidth && fits.Success
                   && dep.Outcome == ExpeditionDepartureService.DepartureOutcome.Blocked && WorldSimulation.ActiveExpedition == null,
                $"D7 负向：关闭广播后带宽 {cap:F0}，{over} 台被单独拦下（唯一硬约束“{cl.Hard.FirstOrDefault().Text}”），{over - 1} 台可派；出发被拒、没有远征在外");
        }

        /// <summary>路线走出覆盖 / 危害带：当前没有真实路线 → 真实状态下不出现（如实未交付）；接口用真实覆盖几何（不注入覆盖采样）另测，危害后果不留空。</summary>
        private static void CheckCoverageAndHazard()
        {
            CampaignState s = NewWorld(7144);
            PrepareRegion(s, available: true);
            int a = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -4f));
            int[] roster = { a };
            ExpeditionDepartureService.PrepSnapshot snap = ExpeditionDepartureService.BuildPrepSnapshot(s);
            string site = snap.Target == ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost ? FoundryOutpostLayout.RegionId : FracturedCityLayout.RegionId;
            BuildingRecord[] buildingsBefore = s.BuildingRecords;
            try
            {
                // D8（FG8-EXP-02 交付 DEBT-FG8EXP01-03）：真实路线 = 从归还核心到目的地锚点的层级寻路路线（星球格），按与核心连通的覆盖网采样——不注入采样、不换路线。
                // 只有归还核心的 150 格覆盖（信号塔这一段先停下：它的 300 格覆盖可能刚好盖住较近的锚点，这里要的是“走出覆盖”的那一种世界）。
                var towerStates = new List<(BuildingRecord B, BuildingConstructionState St)>();
                foreach (BuildingRecord b in s.BuildingRecords)
                {
                    if (b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower)
                    {
                        towerStates.Add((b, b.ConstructionState));
                        b.ConstructionState = BuildingConstructionState.Damaged;
                    }
                }
                SignalCoverageService.Invalidate();
                ExpeditionChecklistService.Checklist real = ExpeditionChecklistService.Build(s, roster, snap);
                IReadOnlyList<Vector2> route = ExpeditionChecklistService.RouteProvider?.Invoke(s, snap.Target);
                float outside = route != null ? SignalCoverageService.RouteOutsideLength(HomeValleyLayout.RegionId, route, out _) : 0f;
                ExpeditionChecklistService.Item cov = real.Reminders.FirstOrDefault(r => r.Code == "coverage");
                bool appears = ExpeditionSiteAnchors.IsResolved(s, site) && route != null && route.Count >= 2 && outside > 0f
                               && cov.Text == GameText.Format("exp.check.rem.coverage", Mathf.CeilToInt(outside)) && !real.Blocked;
                // 不出现：沿这条路线铺一串已建成的信号中继塔（场景夹具：记录直接登记为已建成，玩家亲手铺中继由 FG1-SIG-07 覆盖）→ 整条路线都在连通的覆盖里 → 提醒消失。
                int relays = PlaceRelayChain(s, route);
                ExpeditionChecklistService.Checklist relayed = ExpeditionChecklistService.Build(s, roster, snap);
                float outsideAfter = route != null ? SignalCoverageService.RouteOutsideLength(HomeValleyLayout.RegionId, route, out _) : -1f;
                bool gone = relays > 0 && outsideAfter == 0f && !relayed.Reminders.Any(r => r.Code == "coverage");
                Expect(appears && gone,
                    $"D8 真实路线下出现 / 不出现：核心→锚点路线 {(route?.Count ?? 0)} 个路点、约 {outside:F0} 格在覆盖外时提醒“{cov.Text}”（只提醒不拦）；沿路线铺 {relays} 座中继塔后覆盖外 {outsideAfter:F0} 格、提醒消失（{gone}）");
                s.BuildingRecords = buildingsBefore;
                foreach ((BuildingRecord B, BuildingConstructionState St) t in towerStates)
                {
                    t.B.ConstructionState = t.St;
                }
                SignalCoverageService.Invalidate();

                ExpeditionChecklistService.HazardProbe = (st, rt) => new ExpeditionChecklistService.HazardHit("酸沼", null);
                ExpeditionChecklistService.Item hz = ExpeditionChecklistService.Build(s, roster, snap).Reminders.FirstOrDefault(r => r.Code == "hazard");
                ExpeditionChecklistService.HazardProbe = (st, rt) => new ExpeditionChecklistService.HazardHit("酸沼", "护甲被腐蚀");
                ExpeditionChecklistService.Item hz2 = ExpeditionChecklistService.Build(s, roster, snap).Reminders.FirstOrDefault(r => r.Code == "hazard");
                ExpeditionChecklistService.HazardProbe = (st, rt) => null;
                bool noneWhenClear = !ExpeditionChecklistService.Build(s, roster, snap).Reminders.Any(r => r.Code == "hazard");
                Expect(hz.Text == GameText.Format("exp.check.rem.hazard", "酸沼", GameText.Get("exp.check.rem.hazard_default")) && hz2.Text != null && hz2.Text.EndsWith("护甲被腐蚀", StringComparison.Ordinal) && noneWhenClear,
                    $"D9b 危害带接口：没给后果时用默认后果“{hz.Text}”（不再以冒号结尾），给了就用它；没有危害时不出现");
            }
            finally
            {
                s.BuildingRecords = buildingsBefore;
                SignalCoverageService.Invalidate();
                ExpeditionChecklistService.RouteProvider = ExpeditionChecklistService.DefaultRoute;
                ExpeditionChecklistService.HazardProbe = ExpeditionChecklistService.DefaultHazardProbe; // FG8-GEN-02：默认就是真实危害数据，复位成默认（不是清空）
            }
        }

        /// <summary>
        /// 场景夹具（FG8-EXP-02 D8）：沿路线每 150 格登记一座已建成的信号中继塔（第一座离起点 140 格，在归还核心的 150 格覆盖里；相邻两座在彼此的 200 格覆盖里），
        /// 终点再放一座——整条链与核心连通。只登记建筑记录（不占格、不挡路），覆盖服务按记录建覆盖源。返回放了几座。
        /// </summary>
        internal static int PlaceRelayChain(CampaignState s, IReadOnlyList<Vector2> route, string tag = "fgexp_relay")
        {
            if (route == null || route.Count < 2)
            {
                return 0;
            }
            var spots = new List<Vector2>();
            float next = 140f;
            float walked = 0f;
            for (int i = 1; i < route.Count; i++)
            {
                float len = Vector2.Distance(route[i - 1], route[i]);
                while (len > 1e-3f && walked + len >= next)
                {
                    spots.Add(Vector2.Lerp(route[i - 1], route[i], (next - walked) / len));
                    next += 150f;
                }
                walked += len;
            }
            spots.Add(route[route.Count - 1]);
            var records = new List<BuildingRecord>(s.BuildingRecords ?? Array.Empty<BuildingRecord>());
            for (int i = 0; i < spots.Count; i++)
            {
                var cell = new GridCell(Mathf.RoundToInt(spots[i].x), Mathf.RoundToInt(spots[i].y));
                records.Add(new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":" + tag + "_" + i,
                    BuildingTypeId = HomeValleyLayout.BuildingTypeSignalRelay,
                    RegionId = HomeValleyLayout.RegionId,
                    GridX = cell.X,
                    GridY = cell.Y,
                    Position = spots[i],
                    Health = 100f,
                    ConstructionState = BuildingConstructionState.Operational,
                    PowerState = BuildingPowerState.NotApplicable,
                });
            }
            s.BuildingRecords = records.ToArray();
            SignalCoverageService.Invalidate();
            return spots.Count;
        }

        /// <summary>提醒：货舱（含 0）、无武器、劳动力只在下降时、静默夜、反制（真实快照一致性 + 有反制时的文字）、伤势偏重；空名单不出队伍类提醒。</summary>
        private static void CheckRemindOthers()
        {
            CampaignState s = NewWorld(7145);
            PrepareRegion(s, available: true);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f));
            int c = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(8f, -4f));
            int[] roster = { a, b, c };
            ExpeditionDepartureService.PrepSnapshot snap = ExpeditionDepartureService.BuildPrepSnapshot(s);
            RegionRecord region = FracturedCityRegion.Find(s);
            int suggested = ExpeditionDepartureService.MinRecommendedCargoSlots(snap.Target, region);

            // 单台 ERC-003（有枪；FG8-LOOT-03 起货舱容量与货舱面板同一规则 LootPickCatalog.CapacityOf——战斗履带 4 格，低于建议）：货舱提醒带实际与建议值；没有“无武器”提醒。
            ExpeditionChecklistService.Checklist solo = ExpeditionChecklistService.Build(s, new[] { c }, snap);
            int soloCap = MachineRegistry.TryGetRecord(c, out MachineRecord cRec) ? LootPickCatalog.CapacityOf(s, cRec) : -1;
            bool cargoShown = soloCap > 0 && soloCap < suggested && solo.Reminders.Any(r => r.Code == "cargo" && r.Text == GameText.Format("exp.check.rem.cargo", soloCap, suggested));
            bool armed = !solo.Reminders.Any(r => r.Code == "no_weapon");
            // 单台 ERC-001（没有主武器）：“无武器”提醒，后果写明。
            ExpeditionChecklistService.Checklist unarmed = ExpeditionChecklistService.Build(s, new[] { a }, snap);
            bool noWeapon = unarmed.Reminders.Any(r => r.Code == "no_weapon" && r.Text == GameText.Get("exp.check.rem.no_weapon")) && !unarmed.Blocked;
            // 空名单：只有“至少一台”硬约束，没有队伍类提醒（货舱 / 武器 / 劳动力 / 伤势）。
            ExpeditionChecklistService.Checklist empty = ExpeditionChecklistService.Build(s, Array.Empty<int>(), snap);
            bool emptyQuiet = empty.Hard.Length == 1 && empty.Hard[0].Code.StartsWith("roster-too-small", StringComparison.Ordinal)
                              && empty.Hard[0].Text == GameText.Format("exp.check.hard.too_small", ExpeditionDepartureService.MinDepartureSize)
                              && !empty.Reminders.Any(r => r.Code == "cargo" || r.Code == "no_weapon" || r.Code == "labor" || r.Code == "damaged");
            Expect(cargoShown && armed && noWeapon && emptyQuiet,
                $"D9 货舱提醒（单台 {soloCap}/建议 {suggested}，{cargoShown}）、有枪不提无武器（{armed}）、无武器提醒且不拦（{noWeapon}）、空名单只有“至少 1 台”硬约束（{emptyQuiet}）");

            // 劳动力：只有真的下降才提醒（与 LaborForecastText 的判定一致）；派出全部家园机器时一定下降。
            List<int> all = HomeMachines().Select(m => m.LogicId).ToList();
            MachineRoster.LaborCount before = MachineRoster.ComputeLabor(s);
            string allLabor = MachineRoster.LaborForecastText(s, all, out bool allDrops);
            ExpeditionChecklistService.Checklist clAll = ExpeditionChecklistService.Build(s, all, snap);
            bool laborWhenDrop = before.Labor > 0 && allDrops && clAll.Reminders.Any(r => r.Code == "labor" && r.Text == GameText.Format("exp.check.rem.labor", allLabor));
            MachineRoster.LaborForecastText(s, roster, out bool rosterDrops);
            bool laborConsistent = rosterDrops == ExpeditionChecklistService.Build(s, roster, snap).Reminders.Any(r => r.Code == "labor");
            Expect(laborWhenDrop && laborConsistent,
                $"D9c 劳动力：派出全部 {all.Count} 台（劳动力 {before.Labor} 下降）时提醒“{allLabor}…”；新造的 3 台是否提醒与“是否下降”一致（下降={rosterDrops}）；不变时不提");

            // 静默夜进行中 → 必须出现，且文字与 ExpeditionCrossText 一致；满编一致性。
            ExpeditionChecklistService.Checklist cl = ExpeditionChecklistService.Build(s, roster, snap);
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
                silentForced = !string.IsNullOrEmpty(forcedSilent) && clSilent.Reminders.Any(r => r.Code == "silent_night" && r.Text == forcedSilent);
                night.Active = wasActive;
                night.StartTick = wasStart;
                night.EndTick = wasEnd;
                night.NextDay = wasDay;
            }
            Expect(silentRule && silentForced, $"D9d 静默夜进行中出现提醒（{silentForced}），平时与 ExpeditionCrossText 一致（{silentRule}）");

            // 伤势偏重：介于重伤线与偏重线之间合并一条；满血时没有；重伤的不重复计入偏重。
            MachineRecord hurt = Rec(b);
            float severe = ExpeditionDepartureService.SeverePct;
            GridContent.TryGetTuning("exp.check.warn_health_pct", out float warnPct);
            hurt.Health = hurt.MaxHealth * (severe + warnPct) / 2f;
            ExpeditionChecklistService.Checklist warn = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool warnReminder = warn.Reminders.Any(r => r.Code == "damaged" && r.Text == GameText.Format("exp.check.rem.damaged", 1, Mathf.RoundToInt(warnPct * 100f)))
                                && !warn.Reminders.Any(r => r.Code.StartsWith("severe:", StringComparison.Ordinal)) && !warn.Blocked;
            hurt.Health = hurt.MaxHealth;
            bool noWarnWhenHealthy = !ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s)).Reminders.Any(r => r.Code == "damaged");
            Expect(warnReminder && noWarnWhenHealthy, $"D10 伤势偏重（重伤线与偏重线之间）出现一条合并提醒且不拦（{warnReminder}），满血时没有（{noWarnWhenHealthy}）");

            // 敌方反制：真实快照（破碎都市恒 None）不提；有反制的快照写出反制名、危险与应对。
            bool realConsistent = (snap.AdaptationId != null && snap.AdaptationId != AdaptationCatalog.None) == cl.Reminders.Any(r => r.Code == "enemy_counter");
            AdaptationCatalog.AdaptationInfo flank = AdaptationCatalog.Describe(AdaptationCatalog.Flanker);
            var counterSnap = new ExpeditionDepartureService.PrepSnapshot(
                true, null, snap.Machines, snap.BandwidthCapacity, snap.MinRecommendedCargoSlots, snap.EnemyIntelText,
                snap.EnemyAlertLevel, snap.ExpeditionCount, ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost,
                snap.ObjectivePreviewText, AdaptationCatalog.Flanker, flank.DisplayName, flank.SourceText, flank.HazardText, flank.CounterHintText, false, null);
            ExpeditionChecklistService.Item enemy = ExpeditionChecklistService.Build(s, roster, counterSnap).Reminders.FirstOrDefault(r => r.Code == "enemy_counter");
            Expect(realConsistent && enemy.Text == GameText.Format("exp.check.rem.enemy", flank.DisplayName, flank.HazardText, flank.CounterHintText),
                "D11 敌方反制：真实快照的有无与提醒一致；有反制时提醒写出反制名、危险与应对（与出发锁定同源的 Adaptation 字段）");
        }

        /// <summary>至少一台：单台没有主武器的 ERC-001 校验通过、真实派遣成功（无武器只是提醒）；空名单被拦，文字不露原因码。</summary>
        private static void CheckSingleUnarmedDeparts()
        {
            CampaignState s = NewWorld(7146);
            PrepareRegion(s, available: true);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            ExpeditionDepartureService.RosterValidation one = ExpeditionDepartureService.ValidateRoster(s, new[] { a });
            ExpeditionDepartureService.RosterValidation none = ExpeditionDepartureService.ValidateRoster(s, Array.Empty<int>());
            bool rawCodesHidden = new[] { "machine-dead:" + a, "machine-already-deployed:" + a, "machine-in-factory:" + a, "machine-busy-uninterruptible:" + a + ":w1", "duplicate-selection:" + a, "bandwidth-exceeded:need=9:cap=8", "roster-too-small:need>=1:have=0",
                    "autosave-failed:IoError", "transaction-failed-rolled-back:probe-boom", "transaction-failed-rollback-also-failed:probe-boom" }
                .Select(ExpeditionChecklistService.DescribeHard).All(t => !t.Contains("machine-") && !t.Contains("bandwidth-") && !t.Contains("roster-")
                    && !t.Contains("autosave") && !t.Contains("IoError") && !t.Contains("transaction") && !t.Contains("probe-boom") && t.Length > 0 && !GameText.ContainsMarker(t))
                && ExpeditionChecklistService.DescribeHard("autosave-failed:IoError") == GameText.Get("exp.check.hard.autosave_failed")
                && ExpeditionChecklistService.DescribeHard("transaction-failed-rolled-back:probe-boom") == GameText.Get("exp.check.hard.transaction_failed")
                && ExpeditionChecklistService.DescribeHard("transaction-failed-rollback-also-failed:probe-boom") == GameText.Get("exp.check.hard.rollback_failed");
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(new[] { a }, true);
            // FG8-EXP-02：出发 = 沿地形行军；本段要在远征地点里接着测——测试快进到达（走正式的到达事务；行军本身由 FgExpeditionMarchSelfCheck 真实推进验证）。
            if (dep.Success)
            {
                ExpeditionMarchService.ArriveNowForTests(CampaignSession.Current);
            }
            Expect(!one.HasWeapon && one.Success && none.BlockingReasons.Any(r => r.StartsWith("roster-too-small", StringComparison.Ordinal)) && rawCodesHidden
                   && dep.Success && WorldSimulation.ActiveExpedition != null,
                $"D12 至少一台：单台没有主武器的 ERC-001 校验通过并真实派遣成功（{dep.Outcome}），空名单被拦；拒绝文字不露原因码（{rawCodesHidden}）");
        }

        /// <summary>正式入口：假键盘按 Ctrl+1 设编组、Alt+1 连按两次 → 家园编队系统（TickInput）→ 宿主镜头焦点落在编组中心；超时的第二下不飞。</summary>
        private static void CheckGroupDoubleTapFormal()
        {
            CampaignState s = NewWorld(7147);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(10f, 2f));
            WorldSimulation.StepMany(2);
            HomeValleyController home = GameRoot.HomeValley;
            Vector2? pa = home?.LivePosition(a);
            Vector2? pb = home?.LivePosition(b);
            if (home == null || !pa.HasValue || !pb.HasValue || !WorldView.Director.IsBound)
            {
                Fail($"D13 前提失败：家园 {home != null}、机器位置 {pa.HasValue}/{pb.HasValue}、镜头 {WorldView.Director.IsBound}");
                return;
            }
            Vector2 centre = (pa.Value + pb.Value) / 2f;
            var reader = new FakeReader();
            InputRouter.Reset();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            RegionSquadCommandSystem squad = home.SquadCommands;
            try
            {
                squad.DebugSelectMany(new[] { a, b });
                reader.Hold(KeyCode.LeftControl);
                reader.Press(KeyCode.Alpha1);
                squad.TickInput(false);
                Frame(reader);
                bool assigned = squad.GroupSize(1) == 2;
                squad.ClearSelection();

                int flights = WorldView.Director.FlightCount;
                _fakeRealTime = 100f;
                RegionSquadCommandSystem.RealTime = () => _fakeRealTime;
                reader.Hold(KeyCode.LeftAlt);
                reader.Press(KeyCode.Alpha1);
                squad.TickInput(false);
                Frame(reader);
                bool firstNoFly = WorldView.Director.FlightCount == flights && squad.Selection.Count == 2;
                _fakeRealTime = 100f + RegionSquadCommandSystem.GroupDoubleTapSeconds * 0.5f;
                reader.Hold(KeyCode.LeftAlt);
                reader.Press(KeyCode.Alpha1);
                squad.TickInput(false);
                Frame(reader);
                Unity.Mathematics.float2 focus = WorldView.Director.StrategyFocus;
                bool flew = WorldView.Director.FlightCount == flights + 1 && Mathf.Abs(focus.x - centre.x) < 0.5f && Mathf.Abs(focus.y - centre.y) < 0.5f;

                // 超时：第三下（重新开始计）后隔 2 倍间隔再按，不飞。
                _fakeRealTime += 1f;
                reader.Hold(KeyCode.LeftAlt);
                reader.Press(KeyCode.Alpha1);
                squad.TickInput(false);
                Frame(reader);
                int flights2 = WorldView.Director.FlightCount;
                _fakeRealTime += RegionSquadCommandSystem.GroupDoubleTapSeconds * 2f;
                reader.Hold(KeyCode.LeftAlt);
                reader.Press(KeyCode.Alpha1);
                squad.TickInput(false);
                Frame(reader);
                bool slowNoFly = WorldView.Director.FlightCount == flights2;
                Expect(assigned && firstNoFly && flew && slowNoFly,
                    $"D13 正式入口：Ctrl+1 设编组（{assigned}）；Alt+1 第一下只选中不飞（{firstNoFly}）；{RegionSquadCommandSystem.GroupDoubleTapSeconds:F2} 秒内第二下 → 镜头焦点 ({focus.x:F1},{focus.y:F1}) = 编组中心 ({centre.x:F1},{centre.y:F1})（{flew}）；超时第二下不飞（{slowNoFly}）");
            }
            finally
            {
                RegionSquadCommandSystem.RealTime = () => Time.unscaledTime;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
            }
        }

        /// <summary>远征队：改名（同机器改名规则）、颜色、名单记忆（去重、没有上限）、旧档兼容。</summary>
        private static void CheckTeamRenameColor()
        {
            CampaignState s = NewWorld(7148);
            string defaultName = GameText.Get("exp.team.default_name");
            Expect(ExpeditionTeamService.DisplayName(s) == defaultName, "E1 新战役默认队名");
            bool renamed = ExpeditionTeamService.TryRename(s, "  铁锤队  ", out _) && ExpeditionTeamService.StateOf(s).Name == "铁锤队";
            bool rejectedAngle = !ExpeditionTeamService.TryRename(s, "a<b", out string angleMsg) && ExpeditionTeamService.StateOf(s).Name == "铁锤队" && angleMsg.Length > 0;
            bool rejectedLong = !ExpeditionTeamService.TryRename(s, new string('队', MachineNaming.MaxLength + 1), out _) && ExpeditionTeamService.StateOf(s).Name == "铁锤队";
            bool rejectedBlank = !ExpeditionTeamService.TryRename(s, "   ", out _) && ExpeditionTeamService.StateOf(s).Name == "铁锤队";
            Expect(renamed && rejectedAngle && rejectedLong && rejectedBlank,
                $"E2 改名：去首尾空白生效；含 < > 、超 {MachineNaming.MaxLength} 字、空白均被拒且原名不变（与机器改名同一套规则）");
            ExpeditionTeamService.ResetName(s);
            Expect(ExpeditionTeamService.DisplayName(s) == defaultName, "E3 清空名字 = 恢复默认队名");

            bool colored = ExpeditionTeamService.TrySetColor(s, 5) && ExpeditionTeamService.ColorIndexOf(s) == 5
                           && ExpeditionTeamService.ColorName(5) == GameText.Get("exp.team.color.5");
            bool badColor = !ExpeditionTeamService.TrySetColor(s, -1) && !ExpeditionTeamService.TrySetColor(s, ExpeditionTeamService.ColorCount)
                            && ExpeditionTeamService.ColorIndexOf(s) == 5;
            Expect(colored && badColor, "E4 颜色：设下标 5 生效；越界（-1 / 种数）被拒且颜色不变");

            int[] seven = { 4001, 4002, 4001, 4003, 4004, 4005, 4006, 4007 };
            ExpeditionTeamService.RememberMembers(s, seven);
            int[] members = ExpeditionTeamService.StateOf(s).Members;
            Expect(members.SequenceEqual(new[] { 4001, 4002, 4003, 4004, 4005, 4006, 4007 }), $"E5 名单记忆：去重、保持先后、没有 5 台上限（记下 {members.Length} 台）");

            s.Grid.ExpeditionTeam = null;
            Expect(ExpeditionTeamService.DisplayName(s) == defaultName && ExpeditionTeamService.ColorIndexOf(s) == 0
                   && ExpeditionTeamService.RememberedMembers(s).Length == 0,
                "E6 旧档没有远征队字段：默认名、颜色 0、空名单，不抛异常");
        }

        /// <summary>可派区域列表、默认优先级、玩家指定生效、指定的区域不可派时回落。</summary>
        private static void CheckDestinationChoice()
        {
            CampaignState s = NewWorld(7149);
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
                == ExpeditionDepartureService.ExpeditionTarget.SilentRuins;
            foundry.State = RegionState.Locked;
            bool fallback = ExpeditionDepartureService.ResolveTarget(s, ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost)
                == ExpeditionDepartureService.ExpeditionTarget.SilentRuins;
            Expect(onlyFractured && bothListed && defaultKept && pickedRuins && fallback,
                "D14 指定目的地：只有一处可派时列表只含它；两处都可派时列出两项且默认仍为铸造前哨外围；玩家指定的破碎都市生效；指定的区域不可派时回落");
        }

        /// <summary>区域级迷雾口径：迷雾边缘（Available）可派并提醒；已探索可派且无此提醒；未开放（Locked）硬拦（写解锁条件）。</summary>
        private static void CheckFogEdgeRelease()
        {
            CampaignState s = NewWorld(7150);
            PrepareRegion(s, available: true);
            RegionRecord fractured = FracturedCityRegion.Find(s);
            if (fractured == null)
            {
                Fail("D15 前提失败：破碎都市记录缺失");
                return;
            }
            int a = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -4f));
            int[] roster = { a };
            const ExpeditionDepartureService.ExpeditionTarget ruins = ExpeditionDepartureService.ExpeditionTarget.SilentRuins;
            bool edgeClass = ExpeditionDepartureService.ClassifyDestination(s, ruins) == ExpeditionDepartureService.DestinationFog.FogEdge;
            ExpeditionChecklistService.Checklist edge = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool edgeReleased = edgeClass && !edge.Blocked && edge.Reminders.Any(r => r.Code == "destination-fog-edge" && r.Text == GameText.Get("exp.check.rem.fog_edge"));
            bool tagged = ExpeditionPrepPanelUIToolkit.DestinationOptionLabel(s, ruins) == GameText.Format("exp.dest.fog_edge_tag", GameText.Get("exp.dest.ruins"));
            fractured.State = RegionState.Cleared;
            ExpeditionChecklistService.Checklist explored = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool exploredReleased = ExpeditionDepartureService.ClassifyDestination(s, ruins) == ExpeditionDepartureService.DestinationFog.Explored
                && !explored.Blocked && !explored.Reminders.Any(r => r.Code == "destination-fog-edge")
                && ExpeditionPrepPanelUIToolkit.DestinationOptionLabel(s, ruins) == GameText.Get("exp.dest.ruins");
            fractured.State = RegionState.Locked;
            ExpeditionChecklistService.Checklist deep = ExpeditionChecklistService.Build(s, roster, ExpeditionDepartureService.BuildPrepSnapshot(s));
            bool deepBlocked = deep.Blocked && deep.Hard.Any(h => h.Code == "destination-locked");
            Expect(edgeReleased && tagged && exploredReleased && deepBlocked,
                "D15 迷雾边缘放行并提醒“尚未进入过”、下拉框带“（迷雾边缘）”文本键标注；已探索放行无此提醒；未开放硬拦（区域级近似，地图级见 DEBT-FG8EXP01-06）");
        }

        // ── 正式入口（真 UXML 面板） ─────────────────────────────────────────────────

        /// <summary>名册“编成远征队”按钮（真 UXML）：面板关着 → 打开并带入勾选；面板已开 → 立即套用、不合格的被略过并写明；
        /// 面板没开时发来的名单被丢弃（不残留到下次打开）；换存档后不套用旧名单。</summary>
        private static void CheckComposeEntryFormal()
        {
            CampaignState s = NewWorld(7151);
            PrepareRegion(s, available: true);
            int a = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -4f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f));
            int c = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(8f, -4f));
            int d = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(10f, -4f));
            MachineRegistry.MoveToRegion(Rec(d), FracturedCityLayout.RegionId); // 夹具：已在远征中的机器（真实出发见 D5）
            WorldSimulation.StepMany(2);
            HomeValleyController hv = GameRoot.HomeValley;
            GameObject rgo = null;
            GameObject pgo = null;
            GameObject mgo = null;
            GameObject sgo = null;
            try
            {
                VisualElement rroot = FgProductionSelfCheck.MountUxml(UiKitFolder + "RosterPanel.uxml", out rgo);
                RosterPanelUIToolkit.InWorldOverrideForTests = true;
                RosterPanelUIToolkit roster = rgo.AddComponent<RosterPanelUIToolkit>();
                roster.BindView(rroot);
                roster.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "RosterRow.uxml"));
                VisualElement proot = FgProductionSelfCheck.MountUxml(PrepUxml, out pgo);
                ExpeditionPrepPanelUIToolkit prep = pgo.AddComponent<ExpeditionPrepPanelUIToolkit>();
                prep.BindView(proot, AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(PrepRowUxml));
                Button compose = rroot.Q<Button>("RosterComposeExpedition");

                // A. 面板关着：名册勾 a、b → 点按钮 → 名册收起、面板打开、带入 a、b。
                hv.SetExpeditionPrepPanelOpen(false);
                prep.Tick(0f);
                RosterPanelUIToolkit.Open();
                roster.Refresh();
                Tick(roster, a, true);
                Tick(roster, b, true);
                bool clickedA = Click(compose);
                prep.Tick(0f);
                prep.RefreshNowForTests();
                bool caseA = clickedA && hv.IsExpeditionPrepPanelOpen && !RosterPanelUIToolkit.IsOpen && prep.PanelVisible
                             && prep.SelectedIdsForTests.SequenceEqual(new[] { a, b }) && prep.SummaryText.Contains(GameText.Format("exp.compose.applied", 2));
                Expect(caseA, $"D17 名册“编成远征队”（面板关着）：名册收起、派遣准备面板打开、带入 {MachineNaming.Short(a)}、{MachineNaming.Short(b)}（选中 {string.Join(",", prep.SelectedIdsForTests)}）");

                // B. 面板已开（入口事件到达时面板仍算开着）：名册与派遣准备面板是互斥整页，名册打开会先收起派遣准备面板；
                //    本例在同一帧内勾选并编成（派遣准备面板还没处理这次关闭），名册改勾 c 与已在远征中的 d → 点按钮 → 立即套用，d 被略过并写明原因。
                //    玩家真实走的“名册开着过了几帧再编成”见 G（D19d）。
                RosterPanelUIToolkit.Open();
                roster.SelectNone();
                roster.Refresh();
                Tick(roster, c, true);
                Tick(roster, d, true);
                bool clickedB = Click(compose);
                bool immediate = prep.SelectedIdsForTests.SequenceEqual(new[] { c });
                string notice = prep.ComposeNoticeForTests;
                prep.RefreshNowForTests();
                bool caseB = clickedB && immediate && prep.SelectedIdsForTests.SequenceEqual(new[] { c }) && hv.IsExpeditionPrepPanelOpen
                             && notice.Contains(MachineNaming.Short(d)) && notice.Contains(GameText.Get("exp.check.hard.machine.deployed"))
                             && prep.SummaryText.Contains(notice);
                Expect(caseB, $"D18 名册“编成远征队”（面板已开着）：点下当即换成 {MachineNaming.Short(c)}（{immediate}），已在远征中的 {MachineNaming.Short(d)} 被略过并写明：“{notice}”");

                // 玩家改勾选后，入口说明不再成立，清掉。
                prep.RowToggleOf(a).value = true;
                prep.RefreshNowForTests();
                bool noticeCleared = prep.ComposeNoticeForTests.Length == 0 && prep.SelectedIdsForTests.SequenceEqual(new[] { a, c });

                // C. 面板关着时发来的名单被丢弃；之后从信号塔打开是常规预选（新战役没有预备 / 上次名单 = 空）。
                hv.SetExpeditionPrepPanelOpen(false);
                prep.Tick(0f);
                ExpeditionTeamService.RequestCompose(new[] { b });
                hv.SetExpeditionPrepPanelOpen(true);
                prep.Tick(0f);
                prep.RefreshNowForTests();
                bool notStale = prep.SelectedIdsForTests.Length == 0 && prep.ComposeNoticeForTests.Length == 0;

                // D. 换存档：打开那一帧前换了战役，旧名单不套用。
                hv.SetExpeditionPrepPanelOpen(false);
                prep.Tick(0f);
                hv.SetExpeditionPrepPanelOpen(true);
                ExpeditionTeamService.RequestCompose(new[] { b });
                CampaignState other = CampaignState.CreateNew("fgexpchk-other", "Standard", 99);
                CampaignSession.Set(Slot, other);
                prep.Tick(0f);
                bool dropOnSwitch = !prep.SelectedIdsForTests.Contains(b);
                CampaignSession.Set(Slot, s);
                hv.SetExpeditionPrepPanelOpen(false);
                prep.Tick(0f);
                Expect(noticeCleared && notStale && dropOnSwitch,
                    $"D19 入口名单不残留：玩家改勾选后入口说明清掉（{noticeCleared}）；面板没开时发来的名单被丢弃，下次打开按常规预选（{notStale}）；换存档后不套用旧名单（{dropOnSwitch}）");

                // E. 战略地图“编成远征队”（正式入口：真点按钮；地图上还不能选机器 = DEBT-FG8EXP01-10，所以只打开面板、按常规预选）。
                VisualElement mroot = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "StrategicMap.uxml").CloneTree();
                mgo = new GameObject("fgexpchk-map");
                StrategicMapUIToolkit.InWorldOverrideForTests = true;
                StrategicMapUIToolkit map = mgo.AddComponent<StrategicMapUIToolkit>();
                map.BindView(mroot);
                Button mapCompose = mroot.Q<Button>("MapComposeExpedition");
                hv.SetExpeditionPrepPanelOpen(false);
                prep.Tick(0f);
                StrategicMapUIToolkit.Open();
                bool mapReady = StrategicMapUIToolkit.IsOpen && mapCompose != null && mapCompose.enabledSelf && mapCompose.text == GameText.Get("exp.compose.button");
                bool mapClicked = Click(mapCompose);
                prep.Tick(0f);
                prep.RefreshNowForTests();
                bool mapOpensPanel = mapReady && mapClicked && !StrategicMapUIToolkit.IsOpen && hv.IsExpeditionPrepPanelOpen && prep.PanelVisible
                                     && prep.SelectedIdsForTests.Length == 0 && prep.ComposeNoticeForTests.Length == 0;
                // 面板已开着、玩家勾了 a：再从地图点一次，当前勾选不动（空名单不覆盖玩家的选择）。
                prep.RowToggleOf(a).value = true;
                prep.RefreshNowForTests();
                int[] before = prep.SelectedIdsForTests.ToArray();
                StrategicMapUIToolkit.Open();
                bool mapClicked2 = Click(mapCompose);
                prep.Tick(0f);
                prep.RefreshNowForTests();
                bool keepsSelection = mapClicked2 && before.Contains(a) && prep.SelectedIdsForTests.SequenceEqual(before) && !StrategicMapUIToolkit.IsOpen;
                Expect(mapOpensPanel && keepsSelection,
                    $"D19b 战略地图“编成远征队”（真点按钮）：面板关着 → 地图收起、派遣准备面板打开、按常规预选（{mapOpensPanel}）；面板已开着 → 勾选不动（{keepsSelection}）");

                // F. 现在根本不能派遣（目的地都没开放；远征队在外走同一分支）时从名册发来名单：整单不带入、当前勾选不动，
                //    给一条写明真实原因的字幕，不把能派的机器写成“抢修中”。
                hv.SetExpeditionPrepPanelOpen(true);
                prep.Tick(0f);
                prep.RefreshNowForTests();
                int[] beforeF = prep.SelectedIdsForTests.ToArray();
                RegionRecord ruinsF = FracturedCityRegion.Find(s);
                RegionRecord foundryF = FoundryOutpostRegion.Find(s);
                RegionState ruinsState = ruinsF?.State ?? RegionState.Locked;
                RegionState foundryState = foundryF?.State ?? RegionState.Locked;
                try
                {
                    if (ruinsF != null)
                    {
                        ruinsF.State = RegionState.Locked;
                    }
                    if (foundryF != null)
                    {
                        foundryF.State = RegionState.Locked;
                    }
                    ExpeditionTeamService.RequestCompose(new[] { b, c });
                    string unreachable = prep.LastComposeUnreachableText;
                    bool notApplied = prep.SelectedIdsForTests.SequenceEqual(beforeF) && prep.ComposeNoticeForTests.Length == 0;
                    bool honest = unreachable.Contains(GameText.Get("exp.check.hard.locked")) && !unreachable.Contains(GameText.Get("exp.check.hard.machine.busy"))
                                  && !GameText.ContainsMarker(unreachable);
                    Expect(notApplied && honest, $"D19c 目的地都没开放时发来的名单整单不带入、勾选不动（{notApplied}），字幕写真实原因而不是“抢修中”：“{unreachable}”");
                }
                finally
                {
                    if (ruinsF != null)
                    {
                        ruinsF.State = ruinsState;
                    }
                    if (foundryF != null)
                    {
                        foundryF.State = foundryState;
                    }
                }
                hv.SetExpeditionPrepPanelOpen(false);
                prep.Tick(0f);

                // G. 返工第 2 轮（冒烟 2026-10-17 回归：编成后点“编辑信号核”，信号核一闪即关）。玩家真实路径：
                //    派遣准备面板开着 → 打开名册（整页互斥，派遣准备面板收起）→ 过一帧 → 勾 a → 编成 → 名册收起、派遣准备面板当即回到 Esc 栈 →
                //    同一帧点“编辑信号核”（复用 FG1-SIG-07 信号核面板）：作为派遣准备面板的子页打开；下一帧派遣准备面板照常同步、带入 a，信号核不被当同级页面关掉；
                //    Esc（取消栈最上层）只关信号核，派遣准备面板还开着且回到最上层。
                VisualElement sroot = FgProductionSelfCheck.MountUxml(SignalCoreUxml, out sgo);
                GameLogic.UI.SignalCore.SignalCoreHudUIToolkit hud = sgo.AddComponent<GameLogic.UI.SignalCore.SignalCoreHudUIToolkit>();
                // 编辑模式下 Unity 不派发 Awake：按 Play 里的生命周期手动调（登记“打开信号核”事件监听与 Instance；收尾由 DestroyHosts 调 OnDestroy）。
                typeof(GameLogic.UI.SignalCore.SignalCoreHudUIToolkit).GetMethod("Awake",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.Invoke(hud, null);
                hud.BindView(sroot);
                GameLogic.UI.SignalCore.SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
                UiEscapeStack.Clear();
                hv.SetExpeditionPrepPanelOpen(true);
                prep.Tick(0f);
                bool prepLayered = UiEscapeStack.Contains(prep);
                RosterPanelUIToolkit.Open();
                bool pageExclusive = RosterPanelUIToolkit.IsOpen && !hv.IsExpeditionPrepPanelOpen && !UiEscapeStack.Contains(prep);
                prep.Tick(0f); // 名册开着过了一帧：派遣准备面板按关闭处理（丢弃半成品勾选）。
                roster.SelectNone();
                roster.Refresh();
                Tick(roster, a, true);
                bool clickedG = Click(compose);
                bool prepBack = hv.IsExpeditionPrepPanelOpen && !RosterPanelUIToolkit.IsOpen && UiEscapeStack.Contains(prep);
                bool editClicked = Click(proot.Q<Button>("SignalCoreEditButton"));
                bool coreOpen = GameLogic.UI.SignalCore.SignalCoreHudUIToolkit.IsOpen && ReferenceEquals(UiEscapeStack.Top, hud);
                prep.Tick(0f); // 下一帧：派遣准备面板 Tick → 带入 a，并照常同步 Esc 层。
                prep.RefreshNowForTests();
                bool coreSurvives = GameLogic.UI.SignalCore.SignalCoreHudUIToolkit.IsOpen && ReferenceEquals(UiEscapeStack.Top, hud)
                                    && prep.SelectedIdsForTests.SequenceEqual(new[] { a });
                bool escClosed = UiEscapeStack.CloseTop();
                prep.Tick(0f);
                bool escOk = escClosed && !GameLogic.UI.SignalCore.SignalCoreHudUIToolkit.IsOpen && hv.IsExpeditionPrepPanelOpen
                             && ReferenceEquals(UiEscapeStack.Top, prep);
                Expect(prepLayered && pageExclusive && clickedG && prepBack && editClicked && coreOpen && coreSurvives && escOk,
                    $"D19d 名册编成后同一帧点“编辑信号核”：名册打开时派遣准备面板收起（{pageExclusive}）；编成后面板当即回到 Esc 栈（{prepBack}）；"
                    + $"信号核作为子页打开（{editClicked}/{coreOpen}），下一帧不被关掉且带入 {MachineNaming.Short(a)}（{coreSurvives}）；Esc 只关信号核、派遣准备面板还开着（{escOk}）");
                hv.SetExpeditionPrepPanelOpen(false);
                prep.Tick(0f);
            }
            finally
            {
                RosterPanelUIToolkit.Close();
                RosterPanelUIToolkit.InWorldOverrideForTests = false;
                StrategicMapUIToolkit.Close();
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                GameLogic.UI.SignalCore.SignalCoreHudUIToolkit.Close();
                GameLogic.UI.SignalCore.SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                GameRoot.HomeValley?.SetExpeditionPrepPanelOpen(false);
                UiEscapeStack.Clear();
                InputRouter.Reset();
                DestroyHosts(rgo, pgo, mgo, sgo);
            }
        }

        /// <summary>收尾：编辑模式下 Unity 不派发 OnDestroy（同 UiDockLayoutSelfCheck / FgSignalUplinkSelfCheck 的做法），
        /// 先手动调各面板的 OnDestroy（解绑 GameEvent 监听、清静态 Instance），再 DestroyImmediate。</summary>
        private static void DestroyHosts(params GameObject[] hosts)
        {
            bool destroyedSignalCore = false;
            foreach (GameObject go in hosts)
            {
                if (go == null)
                {
                    continue;
                }
                foreach (MonoBehaviour mb in go.GetComponents<MonoBehaviour>())
                {
                    if (mb == null)
                    {
                        continue;
                    }
                    destroyedSignalCore |= mb is GameLogic.UI.SignalCore.SignalCoreHudUIToolkit;
                    System.Reflection.MethodInfo onDestroy = mb.GetType().GetMethod("OnDestroy",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    try
                    {
                        onDestroy?.Invoke(mb, null);
                    }
                    catch (Exception e)
                    {
                        Fail($"收尾 {mb.GetType().Name}.OnDestroy 抛异常：{(e.InnerException ?? e).Message}");
                    }
                }
                UnityEngine.Object.DestroyImmediate(go);
            }
            if (ExpeditionPrepPanelUIToolkit.Instance != null || RosterPanelUIToolkit.Instance != null || StrategicMapUIToolkit.Instance != null
                || (destroyedSignalCore && GameLogic.UI.SignalCore.SignalCoreHudUIToolkit.Instance != null))
            {
                Fail("收尾：面板销毁后静态 Instance 仍残留（GameEvent 监听可能没解绑）");
            }
        }

        /// <summary>正式入口：面板目的地下拉框选破碎都市（默认是铸造前哨）→ 勾选 → 点“确认出发”→ TryDepart 去的就是破碎都市；
        /// 硬约束成立时出发按钮置灰并写原因；家园机器超过 8 台时列表照样全部列出（没有人数上限）。</summary>
        private static void CheckPanelDestinationDepart()
        {
            CampaignState s = NewWorld(7152);
            PrepareRegion(s, available: true);
            RegionRecord foundry = FoundryOutpostRegion.Find(s);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (foundry == null || ruins == null)
            {
                Fail("D21 前提失败：区域记录缺失");
                return;
            }
            foundry.State = RegionState.Available;
            var ids = new List<int>();
            for (int k = 0; k < 10; k++)
            {
                ids.Add(Spawn(s, k == 0 ? HomeValleyLayout.Erc003ChassisId : HomeValleyLayout.Erc001ChassisId,
                    k == 0 ? HomeValleyLayout.BlueprintErc003Id : HomeValleyLayout.BlueprintErc001Id, new Vector2(4f + k * 1.5f, -6f)));
            }
            WorldSimulation.StepMany(2);
            HomeValleyController hv = GameRoot.HomeValley;
            GameObject pgo = null;
            GameObject rgo = null;
            GameObject mgo = null;
            try
            {
                VisualElement proot = FgProductionSelfCheck.MountUxml(PrepUxml, out pgo);
                ExpeditionPrepPanelUIToolkit prep = pgo.AddComponent<ExpeditionPrepPanelUIToolkit>();
                prep.BindView(proot, AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(PrepRowUxml));
                hv.SetExpeditionPrepPanelOpen(true);
                prep.Tick(0f);
                prep.RefreshNowForTests();
                int homeCount = HomeMachines().Count;
                bool allListed = ids.All(id => prep.RowToggleOf(id) != null) && homeCount > 8;
                // 空名单：硬约束写在面板上，出发按钮置灰。
                bool emptyBlocked = !prep.DepartButton.enabledSelf
                                    && prep.ReasonsText == GameText.Format("exp.panel.blocked", GameText.Format("exp.check.hard.too_small", ExpeditionDepartureService.MinDepartureSize));
                Expect(allListed && emptyBlocked, $"D21 家园 {homeCount} 台机器全部列出（不再只有 8 行）；空名单时出发按钮置灰、写“{prep.ReasonsText}”");

                DropdownField dest = prep.DestinationField;
                string ruinsLabel = ExpeditionPrepPanelUIToolkit.DestinationOptionLabel(s, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
                int idx = dest.choices.IndexOf(ruinsLabel);
                bool picked = RosterPanelUIToolkit.PickForTests(dest, idx);
                prep.RowToggleOf(ids[0]).value = true;
                prep.RefreshNowForTests();
                bool titled = prep.TitleText == GameText.Format("exp.panel.title", GameText.Get("exp.dest.ruins")) && prep.DepartButton.enabledSelf;
                int ruinsBefore = ruins.ExpeditionCount;
                int foundryBefore = foundry.ExpeditionCount;
                bool clicked = Click(prep.DepartButton);
                // FG8-EXP-02：出发 = 沿地形行军——往破碎都市的锚点走、准备面板关闭、镜头不动；本段后面要在远征地点里接着测，测试快进到达（正式到达事务）。
                GameLogic.Campaign.ExpeditionMarchState march = ExpeditionMarchService.Current(CampaignSession.Current);
                bool marching = clicked && march != null && march.Phase == ExpeditionMarchService.PhaseOutbound && march.SiteId == FracturedCityLayout.RegionId
                                && !hv.IsExpeditionPrepPanelOpen && WorldSimulation.ActiveExpedition == null;
                ExpeditionMarchService.ArriveNowForTests(CampaignSession.Current);
                bool wentRuins = marching && WorldSimulation.ActiveExpedition != null && GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsLoaded
                                 && FracturedCityRegion.Find(CampaignSession.Current).ExpeditionCount == ruinsBefore + 1
                                 && FoundryOutpostRegion.Find(CampaignSession.Current).ExpeditionCount == foundryBefore;
                Expect(picked && titled && wentRuins,
                    $"D22 正式入口：目的地下拉框选“{ruinsLabel}”（默认是铸造前哨）→ 标题“{prep.TitleText}”→ 点“确认出发”→ 往破碎都市行军、面板关闭（{marching}）→ 到达后进的是破碎都市（破碎都市出击 +1、铸造前哨不变：{wentRuins}）");

                // 派出后镜头在远征地点（家园没被观察）：名册的“编成远征队”置灰并在文字上写明原因（B06）；回家园恢复。
                VisualElement rroot = FgProductionSelfCheck.MountUxml(UiKitFolder + "RosterPanel.uxml", out rgo);
                RosterPanelUIToolkit.InWorldOverrideForTests = true;
                RosterPanelUIToolkit roster = rgo.AddComponent<RosterPanelUIToolkit>();
                roster.BindView(rroot);
                roster.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "RosterRow.uxml"));
                RosterPanelUIToolkit.Open();
                roster.Refresh();
                Button compose = rroot.Q<Button>("RosterComposeExpedition");
                bool homeOnly = !GameRoot.HomeValley.IsActive && compose.text == GameText.Get("exp.compose.button_home_only") && !compose.enabledSelf;
                // 战略地图上的同一个入口：不在家园时置灰、写原因，真点不生效；回家园恢复。
                VisualElement mroot = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "StrategicMap.uxml").CloneTree();
                mgo = new GameObject("fgexpchk-map2");
                StrategicMapUIToolkit.InWorldOverrideForTests = true;
                StrategicMapUIToolkit map = mgo.AddComponent<StrategicMapUIToolkit>();
                map.BindView(mroot);
                Button mapCompose = mroot.Q<Button>("MapComposeExpedition");
                StrategicMapUIToolkit.Open();
                bool mapHomeOnly = StrategicMapUIToolkit.IsOpen && mapCompose.text == GameText.Get("exp.compose.button_home_only") && !mapCompose.enabledSelf
                                   && !Click(mapCompose) && StrategicMapUIToolkit.IsOpen;
                StrategicMapUIToolkit.Close();
                WorldView.Observe(GameRoot.HomeValley.SiteId);
                roster.Refresh();
                StrategicMapUIToolkit.Open();
                bool backHome = GameRoot.HomeValley.IsActive && compose.text == GameText.Get("exp.compose.button") && compose.enabledSelf
                                && mapCompose.text == GameText.Get("exp.compose.button") && mapCompose.enabledSelf;
                StrategicMapUIToolkit.Close();
                Expect(homeOnly && mapHomeOnly && backHome,
                    $"D20 不在家园时名册与战略地图的“编成远征队”都置灰并写“{GameText.Get("exp.compose.button_home_only")}”（名册 {homeOnly} / 地图 {mapHomeOnly}，地图真点不生效），回家园恢复（{backHome}）");
            }
            finally
            {
                RosterPanelUIToolkit.Close();
                RosterPanelUIToolkit.InWorldOverrideForTests = false;
                StrategicMapUIToolkit.Close();
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                GameRoot.HomeValley?.SetExpeditionPrepPanelOpen(false);
                UiEscapeStack.Clear();
                InputRouter.Reset();
                DestroyHosts(rgo, pgo, mgo);
            }
        }

        /// <summary>正式入口：队名框失焦——没改就不写（默认名不会被固化进存档，换语言跟着变）；改了才写；非法输入保留并写原因；
        /// 切换语言后标签 / 颜色选项 / 列表标题跟着变（不必重开面板）。</summary>
        private static void CheckPanelTeamNameAndTexts()
        {
            CampaignState s = NewWorld(7153);
            PrepareRegion(s, available: true);
            Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -4f));
            HomeValleyController hv = GameRoot.HomeValley;
            GameObject pgo = null;
            try
            {
                VisualElement proot = FgProductionSelfCheck.MountUxml(PrepUxml, out pgo);
                ExpeditionPrepPanelUIToolkit prep = pgo.AddComponent<ExpeditionPrepPanelUIToolkit>();
                prep.BindView(proot, AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(PrepRowUxml));
                hv.SetExpeditionPrepPanelOpen(true);
                prep.Tick(0f);
                prep.RefreshNowForTests();
                TextField field = prep.TeamNameField;
                bool showsDefault = field.value == GameText.Get("exp.team.default_name");
                FocusOut(field);
                bool untouched = ExpeditionTeamService.StateOf(s).Name == string.Empty;
                field.value = "铁锤队";
                FocusOut(field);
                bool positive = ExpeditionTeamService.StateOf(s).Name == "铁锤队";
                field.value = "a<b";
                FocusOut(field);
                prep.RefreshNowForTests();
                bool keptInput = field.value == "a<b" && prep.TeamNameErrorText.Length > 0 && ExpeditionTeamService.StateOf(s).Name == "铁锤队";
                field.value = GameText.Get("exp.team.default_name");
                FocusOut(field);
                bool resetByDefault = ExpeditionTeamService.StateOf(s).Name == string.Empty && prep.TeamNameErrorText.Length == 0;
                Expect(showsDefault && untouched && positive && keptInput && resetByDefault,
                    $"D23 正式入口（队名框失焦）：没改不写，存档里仍是空串＝默认名（{untouched}）；改成“铁锤队”才写（{positive}，正向对照）；非法输入保留并写原因（{keptInput}）；输入默认名＝恢复默认（{resetByDefault}）");

                GameSettings.SetLanguage(GameLanguage.En);
                prep.RefreshNowForTests();
                string enColor = GameText.Get("exp.team.color.0");
                bool english = prep.DestinationField.label == GameText.Get("exp.dest.label") && prep.TeamNameField.label == GameText.Get("exp.team.label")
                               && proot.Q<DropdownField>("TeamColorDropdown").choices.FirstOrDefault() == enColor
                               && proot.Q<Label>("ListTitle").text == GameText.Format("exp.list.title", ExpeditionDepartureService.MinDepartureSize)
                               && prep.TeamNameField.value == GameText.Get("exp.team.default_name") && !prep.TeamNameField.value.Contains("远征")
                               && proot.Q<Button>("DepartButton").text == GameText.Get("exp.panel.depart") && !GameText.ContainsMarker(prep.SummaryText + prep.RemindText);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                prep.RefreshNowForTests();
                bool backZh = proot.Q<DropdownField>("TeamColorDropdown").choices.FirstOrDefault() == GameText.Get("exp.team.color.0") && prep.TeamNameField.value == GameText.Get("exp.team.default_name");
                Expect(english && backZh, $"D24 切到英文：目的地 / 队名 / 颜色标签、颜色选项（“{enColor}”）、列表标题、按钮、默认队名都跟着变，切回中文恢复（{english}/{backZh}）");
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                GameRoot.HomeValley?.SetExpeditionPrepPanelOpen(false);
                UiEscapeStack.Clear();
                InputRouter.Reset();
                DestroyHosts(pgo);
            }
        }

        /// <summary>真实派遣成功后名单写入远征队，改名 / 颜色随存档存下，真文件读回一致。</summary>
        private static void CheckTeamSaveLoadAndDepart()
        {
            CampaignState s = NewWorld(7154);
            PrepareRegion(s, available: true);
            int a = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(4f, -4f));
            int b = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f));
            int c = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(8f, -4f));
            int[] roster = { a, b, c };
            CampaignSession.Set(Slot, s);
            ExpeditionTeamService.TryRename(s, "铁锤队", out _);
            ExpeditionTeamService.TrySetColor(s, 3);
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(roster, true);
            // FG8-EXP-02：出发 = 沿地形行军；本段要在远征地点里接着测——测试快进到达（走正式的到达事务；行军本身由 FgExpeditionMarchSelfCheck 真实推进验证）。
            if (dep.Success)
            {
                ExpeditionMarchService.ArriveNowForTests(CampaignSession.Current);
            }
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

        /// <summary>测试捷径：信号塔 / 发电机直接置为运行，并把破碎都市解锁（或保持未开放）。真实建设由 FG1 / FG4 覆盖。</summary>
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

        /// <summary>名册行的勾选框（真 UXML 行）。</summary>
        private static void Tick(RosterPanelUIToolkit roster, int logicId, bool on)
        {
            Toggle t = roster.RowSelect(roster.RowIndexOf(logicId));
            if (t != null)
            {
                t.value = on;
            }
        }

        private static bool Click(Button b)
        {
            if (b?.clickable == null || !b.enabledInHierarchy)
            {
                return false;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        /// <summary>向输入框派发一次失焦事件（与玩家点别处时同一个回调）。</summary>
        private static void FocusOut(TextField field)
        {
            using (FocusOutEvent e = FocusOutEvent.GetPooled(field, null, FocusChangeDirection.none, field.focusController))
            {
                field.SendEvent(e);
            }
        }

        private static void Frame(FakeReader reader)
        {
            reader.EndFrame();
            InputRouter.DebugClearConsumedKeys();
        }

        private sealed class FakeReader : IInputReader
        {
            private readonly HashSet<KeyCode> _held = new HashSet<KeyCode>();
            private readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();

            public void Press(KeyCode key) => _down.Add(key);
            public void Hold(KeyCode key) => _held.Add(key);

            public void EndFrame()
            {
                _down.Clear();
                _held.Clear();
            }

            public bool GetKey(KeyCode key) => _held.Contains(key) || _down.Contains(key);
            public bool GetKeyDown(KeyCode key) => _down.Contains(key);
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            // 光标在窗口外：不触发镜头的屏幕边缘推屏。
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
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
