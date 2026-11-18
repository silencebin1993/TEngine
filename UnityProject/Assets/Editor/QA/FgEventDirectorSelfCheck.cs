using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG12-EVT-01 事件导演框架的自动验收（FG10 FGR-EVT-001～006；验收 FGT-EVT-001 / 008；承接 FG-GAP-099、DEBT-FG0ARCH06-02、DEBT-FG6DEF09-02、
    /// DEBT-FG8LOOT02-05、DEBT-FG9UX01-03）。规则段在只有存档的战役上逐步驱动真实的 <see cref="EventDirectorService.WorldStep"/>（夹具事件 + 夹具处理器，
    /// 静默夜 / 突袭计划直接写进存档当输入——导演只读它们）；时间段起真实家园、走 <c>WorldSimulation.Frame</c>；存读档走真文件。
    /// D 数据；F1～F7 FGT-EVT-001 频率上限与互斥（负向矩阵）；R1～R4 FGT-EVT-008 频率档位与难度相乘、关闭只留剧情 / 点位；C1 完成结算（三选一 +1、来源事件）；
    /// W1 事件触发唤醒据点；N1 通知 / 离家报告“事件”段 / 图鉴首次解锁 / 提示音与突袭、静默夜不同；T1 暂停与 0.5x～3x；T2 观察一致；S1 存读档；U1 事件历史面板与暂停菜单；P1 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEventDirectorSelfCheck）。
    /// </summary>
    public static class FgEventDirectorSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("QA/自检/FG 事件导演")]
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
            Line("\n[事件导演] 事件表、频率上限与互斥、频率档位、完成结算与唤醒、通知 / 图鉴 / 提示音、倍速 / 观察 / 存读档、事件历史面板（FG12-EVT-01）");
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
            NotificationCenter.LocateDelegate originalLocate = NotificationCenter.LocateHandler;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgeventdir-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                GameClock.ReloadTuning();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "事件导演在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），每步 O(进行中事件数)，每 check_seconds 评估 O(事件种类 + 突袭计划)；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckMajorGap);
                Step(CheckSilentNight);
                Step(CheckStoryRaid);
                Step(CheckPositiveVsRaid);
                Step(CheckWarnedPostponed);
                Step(CheckGroupAndCooldown);
                Step(CheckHandlerGates);
                Step(CheckFrequencyLevels);
                Step(CheckOffKeepsStory);
                Step(CheckCompletion);
                Step(CheckWakeOutposts);
                Step(CheckNoticesCodexSound);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSaveLoad);
                Step(CheckUi);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"事件导演自检抛异常：{e}");
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
                EventDirectorService.ResetForTests();
                EventHistoryPanelUIToolkit.InWorldOverrideForTests = false;
                GameClock.ResetSession();
                StrategyClock.Reset();
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                NotificationCenter.LocateHandler = originalLocate;
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ProductionService.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
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
            Line($"  · [事件导演] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────────

        private sealed class FixtureHandler : EventDirectorHandler
        {
            private readonly string _id;
            public bool Allow = true;
            public bool PrepareOk = true;
            public int Warned, Started, Ended;
            public string LastOutcome;
            public FixtureHandler(string id) { _id = id; }
            public override string EventId => _id;
            public override bool CanStart(CampaignState s, EventDirectorDef def, out string reasonKey)
            {
                reasonKey = Allow ? null : EventDirectorService.BlockGroup;
                return Allow;
            }
            public override bool Prepare(CampaignState s, EventDirectorInstanceRecord r) => PrepareOk;
            public override void OnWarn(CampaignState s, EventDirectorInstanceRecord r) => Warned++;
            public override void OnStart(CampaignState s, EventDirectorInstanceRecord r) => Started++;
            public override void OnEnd(CampaignState s, EventDirectorInstanceRecord r, string outcome)
            {
                Ended++;
                LastOutcome = outcome;
            }
        }

        private static EventDirectorDef Fx(string id, bool major, string trigger, double ivMin, double ivMax, double warn, double dur,
            string group = "", bool homeOnly = false, bool storyFirst = false, string reward = "none", double cooldown = 0, float wake = 0f)
        {
            return new EventDirectorDef
            {
                Id = id, Threat = !homeOnly, Major = major, Trigger = trigger, IntervalMinDays = ivMin, IntervalMaxDays = ivMax, CooldownDays = cooldown, Weight = 1,
                ActMin = 1, ActMax = 9, Group = group, WarningSeconds = warn, DurationSeconds = dur, Reward = reward, RewardFaction = string.Empty, StoryFirst = storyFirst,
                HomeOnly = homeOnly, WakeRadius = wake, NameKey = "event_director.name.worm", CodexId = "codex.event_director.worm", Hook = string.Empty,
                SfxWarn = "sfx_beacon_launch", SortOrder = 0,
            };
        }

        private static readonly Dictionary<string, FixtureHandler> Fixture = new Dictionary<string, FixtureHandler>();

        /// <summary>只有存档的战役（规则段）：导演按夹具表、夹具处理器，从第 0 步起逐步推进。</summary>
        private static CampaignState Bare(int seed, params EventDirectorDef[] defs)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            EventDirectorService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("evd-" + seed, "Standard", seed);
            CampaignFgStateDomains.EnsureAll(s);
            RaidDirectorService.EnsureState(s);
            CampaignSession.Set(Slot, s);
            EventDirectorService.Install();
            EventDirectorService.OverrideDefsForTests(defs.ToList());
            Fixture.Clear();
            foreach (EventDirectorDef d in defs)
            {
                var h = new FixtureHandler(d.Id);
                Fixture[d.Id] = h;
                EventDirectorService.Register(h);
            }
            return s;
        }

        private static long Day => GameClock.TicksFor(GameClock.DaySeconds);
        private static long Sec(double s) => GameClock.TicksFor(s);

        /// <summary>逐步推进 [from, to)（只跑事件导演；预警 / 时限按整数步）。</summary>
        private static void Drive(CampaignState s, long from, long to, Action<long> each = null)
        {
            for (long t = from; t < to; t++)
            {
                EventDirectorService.WorldStep(s, t);
                each?.Invoke(t);
            }
        }

        /// <summary>world_event 通知的全部成员（同类通知会聚合进同一条，按成员核对）。</summary>
        private static List<NotificationMember> EventMembers() =>
            NotificationCenter.History.Where(e => e.Type?.Id == "world_event").SelectMany(e => e.Members).ToList();

        private static List<EventDirectorHistoryRecord> Hist(CampaignState s) => EventDirectorService.History(s).ToList();

        // ── D 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            EventDirectorService.ResetForTests();
            EventDirectorService.ReloadDefs();
            IReadOnlyList<EventDirectorDef> defs = EventDirectorService.Defs;
            string[] ids = { "worm", "signal_ghost", "orbital_drop", "decoy_terminal", "caravan" };
            bool rows = defs.Count == 5 && ids.All(id => EventDirectorService.Def(id) != null);
            bool threatsWarn = defs.Where(d => d.Threat && d.Trigger != EventDirectorService.TriggerSite).All(d => d.WarningSeconds > 0);
            bool majors = new[] { "worm", "signal_ghost", "orbital_drop" }.All(id => EventDirectorService.Def(id).Major && EventDirectorService.Def(id).Group == "major_threat")
                          && !EventDirectorService.Def("caravan").Major && EventDirectorService.Def("caravan").HomeOnly && !EventDirectorService.Def("caravan").Threat;
            bool ghostStory = EventDirectorService.Def("signal_ghost").StoryFirst && EventDirectorService.Def("signal_ghost").ActMin == 2
                              && Math.Abs(EventDirectorService.Def("signal_ghost").WarningSeconds - 5) < 1e-6 && Math.Abs(EventDirectorService.Def("signal_ghost").DurationSeconds - 45) < 1e-6;
            bool noEvt = defs.All(d => !d.Id.StartsWith("evt", StringComparison.Ordinal) && d.NameKey.StartsWith("event_director.", StringComparison.Ordinal));
            var raidSilentSfx = new HashSet<string> { "mus_raid_warning", "mus_raid_battle", "sfx_alarm_warning", "sfx_ui_deny" };
            bool sfx = defs.All(d => d.SfxWarn.Length > 0 && !raidSilentSfx.Contains(d.SfxWarn)
                                     && File.Exists("Assets/GameRes/Raw/Audios/Sfx/" + d.SfxWarn + ".wav"));
            bool texts = defs.All(d => GameText.Has(d.NameKey) && GameText.Has(d.CodexId + ".title") && GameText.Has(d.CodexId + ".body"));
            bool codex = defs.All(d => MechanicCodex.Find(d.CodexId) != null && MechanicCodex.Find(d.CodexId).Tab == "event")
                         && MechanicCodex.Find("codex.event_director") != null;
            bool hooks = defs.All(d => GuidanceHooks.Known.Contains(d.Hook)) && GuidanceHooks.Known.Contains(GuidanceHooks.EventDirectorFirstEvent)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.EventDirectorHistoryFirstOpen);
            Expect(rows && threatsWarn && majors && ghostStory && noEvt && sfx && texts && codex && hooks,
                "D1 fg.TbWorldEvent 五个事件入表（FGR-EVT-001：触发方式 / 间隔 / 冷却 / 权重 / 幕 / 互斥组 / 预警 / 时限 / 奖励）；威胁事件都有预警（FGR-EVT-003）；" +
                "蠕虫 / 信号幽灵 / 轨道坠落是重大威胁同组，商队是只在家园的正向事件；第一次信号幽灵是剧情固定（第二幕起、预警 5 秒、夺控 45 秒）；" +
                "ID 不带 evt、文本键 event_director.*（FGR-EMG-068）；每个事件有名称、图鉴条目（事件页签）、首次钩子、提示音文件且与突袭 / 静默夜不同" +
                $"（rows={rows} warn={threatsWarn} major={majors} ghost={ghostStory} evt={noEvt} sfx={sfx} text={texts} codex={codex} hooks={hooks}）");
            NotificationCatalog.TryGetType("world_event", out NotifyTypeDef nt);
            string diffLine = GameText.Get("difficulty.line.events");
            Expect(nt != null && nt.Cues.Contains(FeedbackCueId.WorldEventWarning) && nt.Cues.Contains(FeedbackCueId.WorldEventNotice) && nt.KeepInHistory
                   && !diffLine.Contains("上线后生效") && diffLine.Contains("事件频率"),
                "D2 通知类型 world_event 由事件导演的两种反馈时刻转换、进历史（FG-GAP-099）；难度说明的事件频率行去掉“上线后生效”、写明与设置相乘（DEBT-FG6DEF09-02）：" + diffLine);
        }

        // ── F FGT-EVT-001 频率上限与互斥 ──────────────────────────────────────────

        private static void CheckMajorGap()
        {
            CampaignState s = Bare(8101, Fx("fx_a", true, "random", 0.3, 0.3, 20, 40, "g_a"), Fx("fx_b", true, "random", 0.4, 0.4, 20, 40, "g_b"));
            Drive(s, 0, 12 * Day);
            List<long> starts = Hist(s).Select(h => h.WarnTick).OrderBy(t => t).ToList();
            bool spaced = true;
            for (int i = 1; i < starts.Count; i++)
            {
                spaced &= starts[i] - starts[i - 1] >= 2 * Day;
            }
            bool blocked = EventDirectorService.StateOf(s).Postponed > 0;
            Expect(starts.Count >= 5 && starts.Count <= 6 && spaced && blocked && Hist(s).Any(h => h.EventId == "fx_a") && Hist(s).Any(h => h.EventId == "fx_b"),
                $"F1 重大威胁每 2 个游戏日最多一次（FGR-EVT-002）：两种间隔 0.3 / 0.4 日的重大威胁跑 12 日，只发生 {starts.Count} 次、相邻至少 2 日，其余到期的推迟（推迟 {EventDirectorService.StateOf(s).Postponed} 次）");
        }

        private static void CheckSilentNight()
        {
            CampaignState s = Bare(8102, Fx("fx_a", true, "random", 0.5, 0.5, 30, 60));
            // 静默夜时间窗（含预警）正好盖住第一次到期：[0.4 日, 0.9 日)
            SilentNightState sn = SilentNightService.StateOf(s);
            sn.NextDay = 1;
            sn.WarnTick = (long)(0.4 * Day);
            sn.StartTick = (long)(0.5 * Day);
            sn.EndTick = (long)(0.9 * Day);
            string blockedKey = null;
            bool startedInside = false;
            Drive(s, 0, (long)(1.2 * Day), t =>
            {
                sn.Warned = t >= sn.WarnTick && t < sn.EndTick;
                sn.Active = t >= sn.StartTick && t < sn.EndTick;
                if (t == (long)(0.6 * Day))
                {
                    blockedKey = EventDirectorService.Schedule(s, "fx_a")?.BlockedKey;
                }
                foreach (EventDirectorInstanceRecord r in EventDirectorService.Active(s))
                {
                    long end = r.State == EventDirectorService.StateWarned ? r.StartTick + Sec(60) : r.EndTick;
                    startedInside |= r.WarnTick < sn.EndTick && end > sn.WarnTick;
                }
            });
            EventDirectorHistoryRecord first = Hist(s).FirstOrDefault();
            Expect(!startedInside && blockedKey == EventDirectorService.BlockSilentNight && first != null && first.WarnTick >= sn.EndTick,
                $"F2 负向“蠕虫将要破土时静默夜开始”：0.5 日到期的重大威胁落在静默夜时间窗（含预警，0.4～0.9 日）里——期间没有任何一段（预警 + 时限）与时间窗重叠、" +
                $"排程写明推迟原因 {blockedKey}，静默夜结束后才开始（第一次预警 {(first != null ? (first.WarnTick / (double)Day).ToString("0.000") : "-")} 日）");
        }

        private static RaidPlanRecord AddPlan(CampaignState s, string storyId, int state)
        {
            var p = new RaidPlanRecord { PlanId = "raid-fx-" + (s.Raids.Director.Plans?.Length ?? 0), StoryId = storyId ?? string.Empty, State = state,
                Trigger = storyId != null ? RaidCatalog.TriggerStory : "auto" };
            s.Raids.Director.Plans = (s.Raids.Director.Plans ?? Array.Empty<RaidPlanRecord>()).Concat(new[] { p }).ToArray();
            return p;
        }

        private static void CheckStoryRaid()
        {
            CampaignState s = Bare(8103, Fx("fx_a", true, "random", 0.2, 0.2, 10, 20));
            RaidPlanRecord p = AddPlan(s, "story_fx", RaidDirectorService.StateWarned);
            Drive(s, 0, (long)(0.5 * Day));
            bool none = Hist(s).Count == 0 && EventDirectorService.Active(s).Count == 0
                        && EventDirectorService.Schedule(s, "fx_a").BlockedKey == EventDirectorService.BlockStoryRaid;
            p.State = RaidDirectorService.StateEnded;
            Drive(s, (long)(0.5 * Day), (long)(0.6 * Day));
            bool after = EventDirectorService.Active(s).Count + Hist(s).Count == 1;
            // 普通（非剧情）突袭不挡重大威胁。
            CampaignState s2 = Bare(8104, Fx("fx_a", true, "random", 0.2, 0.2, 10, 20));
            AddPlan(s2, null, RaidDirectorService.StateWarned);
            Drive(s2, 0, (long)(0.3 * Day));
            bool normal = EventDirectorService.Active(s2).Count + Hist(s2).Count == 1;
            // 剧情节点正式入口 RequestStoryRaid 的 storyId 可以为空：按触发方式认剧情突袭。
            CampaignState s3 = Bare(8110, Fx("fx_a", true, "random", 0.2, 0.2, 10, 20));
            AddPlan(s3, string.Empty, RaidDirectorService.StateWarned);
            Drive(s3, 0, (long)(0.3 * Day));
            bool emptyId = EventDirectorService.Active(s3).Count + Hist(s3).Count == 0 && EventDirectorService.Schedule(s3, "fx_a").BlockedKey == EventDirectorService.BlockStoryRaid;
            Expect(none && after && normal && emptyId,
                "F3 重大威胁不与剧情突袭重叠（FGR-EVT-002）：剧情突袭预警 / 在路上期间到期的重大威胁推迟（原因写“剧情突袭进行中”），突袭结束后的下一次评估开始；普通突袭不挡；没有剧情 ID 的剧情突袭（触发方式 = 剧情）同样挡" +
                $"（挡住 {none}，结束后开始 {after}，普通突袭 {normal}，空剧情 ID {emptyId}）");
        }

        private static void CheckPositiveVsRaid()
        {
            CampaignState s = Bare(8105, Fx("fx_pos", false, "random", 0.2, 0.2, 0, 600, homeOnly: true));
            RaidPlanRecord p = AddPlan(s, null, RaidDirectorService.StateWarned);
            Drive(s, 0, (long)(0.4 * Day));
            bool blocked = EventDirectorService.Active(s).Count == 0 && EventDirectorService.Schedule(s, "fx_pos").BlockedKey == EventDirectorService.BlockRaid;
            p.State = RaidDirectorService.StateEnded;
            Drive(s, (long)(0.4 * Day), (long)(0.45 * Day));
            bool arrived = EventDirectorService.Active(s).Count == 1 && EventDirectorService.Active(s)[0].State == EventDirectorService.StateActive;
            AddPlan(s, null, RaidDirectorService.StateWarned);
            Drive(s, (long)(0.45 * Day), (long)(0.47 * Day));
            EventDirectorHistoryRecord h = Hist(s).LastOrDefault();
            bool left = EventDirectorService.Active(s).Count == 0 && h != null && h.Outcome == EventDirectorService.OutcomeLeftRaid && Fixture["fx_pos"].LastOutcome == EventDirectorService.OutcomeLeftRaid;
            Expect(blocked && arrived && left,
                "F4 负向“商队停留期间突袭即将到来”：突袭逼近时正向家园事件不来（推迟，原因“突袭正在逼近家园”）；突袭结束后到来（没有预警、直接开始）；" +
                $"停留中又有突袭预警 → 提前离开，历史结果“突袭将至，提前离开”，处理器收到同一结果（{blocked} / {arrived} / {left}）");
        }

        private static void CheckWarnedPostponed()
        {
            CampaignState s = Bare(8106, Fx("fx_c", true, "condition", 0, 0, 120, 60));
            EventDirectorService.Request(s, "fx_c", at: new Vector2(12f, 34f));
            Drive(s, 0, Sec(20));
            EventDirectorInstanceRecord warned = EventDirectorService.Active(s).FirstOrDefault();
            bool isWarned = warned != null && warned.State == EventDirectorService.StateWarned && Math.Abs(warned.X - 12f) < 1e-3;
            // 预警中静默夜提前开始（例如难度改了间隔）→ 撤回预警、推迟；结束后补上。
            SilentNightState sn = SilentNightService.StateOf(s);
            sn.NextDay = 1;
            sn.WarnTick = Sec(20);
            sn.StartTick = Sec(25);
            sn.EndTick = Sec(200);
            sn.Active = true;
            sn.Warned = true;
            int noticesBefore = EventMembers().Count;
            FixtureHandler fh = Fixture["fx_c"];
            Drive(s, Sec(20), Sec(40));
            bool pulled = EventDirectorService.Active(s).Count == 0 && EventDirectorService.Schedule(s, "fx_c").Pending
                          && EventMembers().Any(m => m.DetailText.Contains("推迟") && m.DetailText.Contains("静默夜"));
            // 撤回也闭合处理器的生命周期：收到 OnEnd(postponed)，以便收回预警效果（审查 P1）。
            bool handlerClosed = fh.Ended == 1 && fh.LastOutcome == EventDirectorService.OutcomePostponed && fh.Started == 0;
            sn.Active = false;
            sn.Warned = false;
            sn.NextDay = 0;
            Drive(s, Sec(40), Sec(60));
            EventDirectorInstanceRecord again = EventDirectorService.Active(s).FirstOrDefault();
            bool resumed = again != null && again.State == EventDirectorService.StateWarned && Math.Abs(again.X - 12f) < 1e-3 && EventDirectorService.Schedule(s, "fx_c").Occurrences == 1;
            bool newSerial = again != null && warned != null && again.Serial != warned.Serial && fh.Warned == 2 && fh.Ended == 1;
            Expect(isWarned && pulled && handlerClosed && resumed && newSerial,
                "F5 预警中的重大威胁遇上静默夜开始：撤回预警、发“推迟”通知、处理器收到结束（结果“推迟”）以收回预警效果、请求保留（地点不变），" +
                "静默夜结束后以新实例（新 Serial）重新预警；撤回的那次不计次数" +
                $"（预警 {isWarned} / 撤回 {pulled} / 处理器闭合 {handlerClosed}（结束 {fh.Ended}，{fh.LastOutcome}） / 补上 {resumed} / 新实例 {newSerial}（预警 {fh.Warned}））");
        }

        private static void CheckGroupAndCooldown()
        {
            CampaignState s = Bare(8107, Fx("fx_g1", false, "condition", 0, 0, 5, 100, "grp"), Fx("fx_g2", false, "condition", 0, 0, 5, 100, "grp"),
                Fx("fx_cd", false, "condition", 0, 0, 0, 10, cooldown: 0.5));
            EventDirectorService.Request(s, "fx_g1");
            EventDirectorService.Request(s, "fx_g2");
            Drive(s, 0, Sec(30));
            bool one = EventDirectorService.Active(s).Count(r => r.EventId.StartsWith("fx_g", StringComparison.Ordinal)) == 1
                       && EventDirectorService.Schedule(s, "fx_g2").BlockedKey == EventDirectorService.BlockGroup;
            Drive(s, Sec(30), Sec(200));
            bool second = Hist(s).Count(h => h.EventId.StartsWith("fx_g", StringComparison.Ordinal)) == 2 || EventDirectorService.Active(s).Any(r => r.EventId == "fx_g2");
            EventDirectorService.Request(s, "fx_cd");
            Drive(s, Sec(200), Sec(240));
            EventDirectorService.Request(s, "fx_cd");
            Drive(s, Sec(240), Sec(300));
            bool cooling = Hist(s).Count(h => h.EventId == "fx_cd") == 1 && EventDirectorService.Schedule(s, "fx_cd").BlockedKey == EventDirectorService.BlockCooldown;
            Drive(s, Sec(300), Sec(240) + (long)(0.55 * Day));
            bool afterCd = Hist(s).Count(h => h.EventId == "fx_cd") == 2;
            Expect(one && second && cooling && afterCd,
                "F6 互斥组同一时刻最多一个进行中（另一个推迟，原因“同类事件正在进行”），前一个结束后再开始；冷却期内的请求推迟到冷却结束" +
                $"（{one} / {second} / {cooling} / {afterCd}）");
        }

        private static void CheckHandlerGates()
        {
            CampaignState s = Bare(8108, Fx("fx_h", false, "condition", 0, 0, 0, 10), Fx("fx_p", false, "condition", 0, 0, 0, 10));
            EventDirectorService.Unregister("fx_h");
            EventDirectorService.Request(s, "fx_h");
            Fixture["fx_p"].PrepareOk = false;
            EventDirectorService.Request(s, "fx_p");
            Drive(s, 0, Sec(30));
            bool noHandler = Hist(s).Count == 0 && EventDirectorService.Active(s).Count == 0 && EventDirectorService.Schedule(s, "fx_h").Pending;
            bool prepareFail = !EventDirectorService.Schedule(s, "fx_p").Pending && EventDirectorService.Schedule(s, "fx_p").Occurrences == 0;
            // 剧情请求 / 点位事件遇上 Prepare 失败：请求留着，下次评估再试（storyFirst 的第一次不能被吃掉）。
            CampaignState s3 = Bare(8111, Fx("fx_ps", true, "random", 0.2, 0.2, 5, 10, storyFirst: true), Fx("fx_site", false, "site", 0, 0, 0, 10));
            Fixture["fx_ps"].PrepareOk = false;
            Fixture["fx_site"].PrepareOk = false;
            EventDirectorService.Request(s3, "fx_ps", story: true);
            EventDirectorService.Request(s3, "fx_site");
            Drive(s3, 0, Sec(30));
            bool kept = EventDirectorService.Schedule(s3, "fx_ps").Pending && EventDirectorService.Schedule(s3, "fx_ps").PendingStory
                        && EventDirectorService.Schedule(s3, "fx_site").Pending && EventDirectorService.Active(s3).Count == 0;
            Fixture["fx_ps"].PrepareOk = true;
            Fixture["fx_site"].PrepareOk = true;
            Drive(s3, Sec(30), Sec(60));
            bool retried = (EventDirectorService.Active(s3).Any(r => r.EventId == "fx_ps" && r.Story) || Hist(s3).Any(h => h.EventId == "fx_ps" && h.Story))
                           && (EventDirectorService.Active(s3).Any(r => r.EventId == "fx_site") || Hist(s3).Any(h => h.EventId == "fx_site"));
            s.Progress.Act = 1;
            CampaignState s2 = Bare(8109, Fx("fx_act2", false, "random", 0.1, 0.1, 0, 10));
            EventDirectorService.OverrideDefsForTests(new List<EventDirectorDef> { Fx("fx_act2", false, "random", 0.1, 0.1, 0, 10) });
            EventDirectorService.Def("fx_act2").ActMin = 2;
            s2.Progress.Act = 1;
            Drive(s2, 0, (long)(0.5 * Day));
            bool act1None = Hist(s2).Count == 0;
            s2.Progress.Act = 2;
            Drive(s2, (long)(0.5 * Day), (long)(0.75 * Day));
            bool act2 = Hist(s2).Count + EventDirectorService.Active(s2).Count >= 1;
            Expect(noHandler && prepareFail && kept && retried && act1None && act2,
                "F7 没有处理器的事件不安排（后续卡登记前不会凭空发生）；处理器找不到合适地点：随机 / 条件的放弃这次，剧情请求与点位事件留着、下次评估再试；" +
                "生效的幕之前不发生，进入那一幕后从那时起算间隔" +
                $"（{noHandler} / {prepareFail} / 剧情点位留着 {kept} / 恢复后发生 {retried} / {act1None} / {act2}）");
        }

        // ── R FGT-EVT-008 事件频率各档位 ─────────────────────────────────────────

        private static int CountOver(int level, string difficulty, int days)
        {
            CampaignState s = Bare(8200 + level, Fx("fx_r", false, "random", 1.0, 1.0, 0, 10));
            s.DifficultyId = difficulty;
            EventDirectorService.SetFrequency(s, level);
            Drive(s, 0, days * Day);
            return Hist(s).Count;
        }

        private static void CheckFrequencyLevels()
        {
            const int days = 30;
            int off = CountOver(EventDirectorService.FrequencyOff, "Standard", days);
            int low = CountOver(EventDirectorService.FrequencyLow, "Standard", days);
            int std = CountOver(EventDirectorService.FrequencyStandard, "Standard", days);
            int high = CountOver(EventDirectorService.FrequencyHigh, "Standard", days);
            int harsh = CountOver(EventDirectorService.FrequencyStandard, "Harsh", days);
            int builderHigh = CountOver(EventDirectorService.FrequencyHigh, "Builder", days);
            float rh = DifficultyService.EventRate(new CampaignState { DifficultyId = "Harsh" });
            float rb = DifficultyService.EventRate(new CampaignState { DifficultyId = "Builder" });
            bool Near(int n, double expect) => Math.Abs(n - expect) <= 1.0;
            Expect(off == 0 && Near(low, days * 0.5) && Near(std, days) && Near(high, days * 1.5) && Near(harsh, days * rh) && Near(builderHigh, days * rb * 1.5),
                $"R1 FGT-EVT-008 事件频率各档位（间隔 1 日的随机事件跑 {days} 日）：关闭 {off} / 少 {low} / 标准 {std} / 多 {high} 次；" +
                $"难度倍率相乘——严酷（×{rh}）标准 {harsh} 次、建造者（×{rb}）多 {builderHigh} 次（DEBT-FG6DEF09-02 事件频率生效）");
            CampaignState s = Bare(8210, Fx("fx_r", false, "random", 1.0, 1.0, 0, 10));
            Drive(s, 0, Sec(20));
            long dueStd = EventDirectorService.DueTick(s, "fx_r");
            EventDirectorService.SetFrequency(s, EventDirectorService.FrequencyHigh);
            long dueHigh = EventDirectorService.DueTick(s, "fx_r");
            EventDirectorService.SetFrequency(s, EventDirectorService.FrequencyOff);
            long dueOff = EventDirectorService.DueTick(s, "fx_r");
            Expect(dueHigh < dueStd && dueOff == long.MaxValue && Math.Abs((dueHigh - EventDirectorService.Schedule(s, "fx_r").AnchorTick) * 1.5 - (dueStd - EventDirectorService.Schedule(s, "fx_r").AnchorTick)) <= 2,
                "R2 中途改档位立即生效：到期步按新倍率重算（标准 → 多提前到 1/1.5，关闭 = 不再到期），不用等下一次");
        }

        private static void CheckOffKeepsStory()
        {
            CampaignState s = Bare(8220, Fx("fx_story", true, "random", 0.2, 0.2, 5, 10, storyFirst: true), Fx("fx_cond", true, "condition", 0, 0, 5, 10),
                Fx("fx_site", false, "site", 0, 0, 0, 10));
            EventDirectorService.SetFrequency(s, EventDirectorService.FrequencyOff);
            Drive(s, 0, (long)(0.5 * Day));
            bool storyNotRandom = Hist(s).Count == 0;
            bool condRejected = !EventDirectorService.Request(s, "fx_cond") && EventDirectorService.StateOf(s).Rejected == 1;
            bool storyOk = EventDirectorService.Request(s, "fx_story", story: true);
            bool siteOk = EventDirectorService.Request(s, "fx_site");
            Drive(s, (long)(0.5 * Day), (long)(0.6 * Day));
            bool happened = Hist(s).Any(h => h.EventId == "fx_story" && h.Story) && Hist(s).Any(h => h.EventId == "fx_site") && !Hist(s).Any(h => h.EventId == "fx_cond");
            Drive(s, (long)(0.6 * Day), 3 * Day);
            bool noRepeat = Hist(s).Count(h => h.EventId == "fx_story") == 1;
            // 标准档：第一次只能由剧情请求，之后才随机。
            CampaignState s2 = Bare(8221, Fx("fx_story", false, "random", 0.2, 0.2, 5, 10, storyFirst: true));
            Drive(s2, 0, (long)(0.5 * Day));
            bool waits = Hist(s2).Count == 0;
            EventDirectorService.Request(s2, "fx_story", story: true);
            Drive(s2, (long)(0.5 * Day), (long)(1.5 * Day));
            bool thenRandom = Hist(s2).Count >= 3;
            // 先挂起、后关闭：之前挂起的条件请求在“关闭”期间不开始，改回标准后补上。
            CampaignState s3 = Bare(8222, Fx("fx_cond", false, "condition", 0, 0, 0, 10));
            Fixture["fx_cond"].Allow = false;
            EventDirectorService.Request(s3, "fx_cond");
            Drive(s3, 0, Sec(30));
            EventDirectorService.SetFrequency(s3, EventDirectorService.FrequencyOff);
            Fixture["fx_cond"].Allow = true;
            Drive(s3, Sec(30), Sec(90));
            bool heldOff = Hist(s3).Count == 0 && EventDirectorService.Active(s3).Count == 0 && EventDirectorService.Schedule(s3, "fx_cond").Pending;
            EventDirectorService.SetFrequency(s3, EventDirectorService.FrequencyStandard);
            Drive(s3, Sec(90), Sec(150));
            bool resumedStd = Hist(s3).Count + EventDirectorService.Active(s3).Count == 1;
            Expect(storyNotRandom && condRejected && storyOk && siteOk && happened && noRepeat && waits && thenRandom && heldOff && resumedStd,
                "R3 负向“事件频率设为关闭”：只保留剧情固定事件（剧情请求照常发生、记为剧情）与地图点位事件；条件触发的请求被拒、随机事件不再发生；" +
                "标准档下“第一次是剧情固定”的事件第一次只由剧情请求、之后才随机；先挂起、后切到关闭的条件请求在关闭期间不开始，改回标准后补上" +
                $"（{storyNotRandom} / {condRejected} / {storyOk} / {siteOk} / {happened} / {noRepeat} / {waits} / {thenRandom} / 关闭期间挂住 {heldOff} / 改回补上 {resumedStd}）");
        }

        // ── C 完成结算 ───────────────────────────────────────────────────────────

        private static void CheckCompletion()
        {
            CampaignState s = Bare(8301, Fx("fx_loot", false, "condition", 0, 0, 0, 100, reward: "packet"), Fx("fx_exp", false, "condition", 0, 0, 0, 5, reward: "packet"));
            int before = LootPickService.StateOf(s)?.Packets.Length ?? 0;
            EventDirectorService.Request(s, "fx_loot", at: new Vector2(20f, 30f));
            EventDirectorService.Request(s, "fx_exp");
            Drive(s, 0, Sec(2));
            EventDirectorInstanceRecord r = EventDirectorService.Active(s).First(x => x.EventId == "fx_loot");
            bool done = EventDirectorService.Complete(s, r.Serial, EventDirectorService.OutcomeSuccess);
            Drive(s, Sec(2), Sec(20));
            LootPackageRecord[] packets = LootPickService.StateOf(s).Packets;
            EventDirectorHistoryRecord hl = Hist(s).First(h => h.EventId == "fx_loot");
            EventDirectorHistoryRecord he = Hist(s).First(h => h.EventId == "fx_exp");
            bool one = packets.Length == before + 1 && packets.Last().Source == LootPickService.SourceEvent && hl.PacketId == packets.Last().PacketId
                       && Math.Abs(packets.Last().X - 20f) < 1e-3 && hl.Outcome == EventDirectorService.OutcomeSuccess;
            bool expired = he.Outcome == EventDirectorService.OutcomeExpired && he.PacketId.Length == 0 && Fixture["fx_exp"].LastOutcome == EventDirectorService.OutcomeExpired;
            Expect(done && one && expired,
                "C1 DEBT-FG8LOOT02-05：事件完成走 LootPickService.GrantEventPacket——数据包 +1、来源 = 事件、落在事件地点、历史记下数据包；到时限没应对的按“到时离开”收尾、不给奖励" +
                $"（完成 {done} / 数据包 {one} / 超时 {expired}）");
        }

        private static void CheckWakeOutposts()
        {
            CampaignState s = Bare(8401, Fx("fx_w", true, "condition", 0, 0, 0, 30, wake: 15f));
            OutpostRecord near = WorldOutpostSystem.SpawnOutpost(s, "terr-fx", new GridCell(10, 10), false);
            OutpostRecord far = WorldOutpostSystem.SpawnOutpost(s, "terr-fx", new GridCell(80, 80), false);
            near.Dormant = true;
            far.Dormant = true;
            EventDirectorService.Request(s, "fx_w", at: new Vector2(14f, 12f));
            Drive(s, 0, Sec(2));
            Expect(!near.Dormant && near.LastWakeReason == WorldOutpostSystem.WakeEvent && far.Dormant,
                "W1 DEBT-FG0ARCH06-02“事件触发”唤醒：事件开始时把唤醒半径内休眠的据点经 WorldOutpostSystem.WakeNow 补算唤醒（原因 outpost.wake.event），半径外的继续休眠" +
                $"（近处 {near.LastWakeReason} / 远处休眠 {far.Dormant}）");
        }

        // ── N 通知 / 离家报告 / 图鉴 / 提示音 ─────────────────────────────────────

        private static void CheckNoticesCodexSound()
        {
            CampaignState s = Bare(8501);
            EventDirectorService.OverrideDefsForTests(null);
            EventDirectorService.ReloadDefs();
            var worm = new FixtureHandler("worm");
            EventDirectorService.Register(worm);
            bool lockedBefore = !MechanicCodex.IsUnlocked("codex.event_director.worm") && !EventDirectorService.HasOccurred(s, "worm");
            AwayReportService.Begin(s, FracturedCityLayout.RegionId, Array.Empty<int>());
            int n0 = EventMembers().Count;
            EventDirectorService.Request(s, "worm", at: new Vector2(30f, 40f));
            Drive(s, 0, Sec(5));
            string warnSfx = EventDirectorService.LastCueSfx;
            NotificationMember warn = EventMembers().LastOrDefault(m => m.DetailText.Contains("掘进蠕虫") && m.DetailText.Contains("到来")); // 同类通知聚合、成员有上限：按正文找
            bool warned = warn != null && warn.HasLocation && Math.Abs(warn.Location.x - 30f) < 1e-3 && Math.Abs(warn.Location.z - 40f) < 1e-3
                          && warn.DetailText.Contains("掘进蠕虫") && worm.Warned == 1;
            Drive(s, Sec(5), Sec(60));
            EventDirectorInstanceRecord r = EventDirectorService.Active(s).FirstOrDefault();
            bool started = r != null && r.State == EventDirectorService.StateActive && worm.Started == 1 && r.StartTick - r.WarnTick == Sec(30); // FG12-EVT-02：worm 预警 50 → 30 秒（FGR-EVT-011 破土前 30 秒裂纹）
            EventDirectorService.Complete(s, r.Serial, EventDirectorService.OutcomeSuccess);
            AwayReportRecord rep = AwayReportService.Current(s);
            int awayEvents = rep?.Entries.Count(e => e.Section == "event" && e.TypeId == "world_event") ?? 0;
            AwayReportService.End(s, AwayReportService.OutcomeOther, notify: false);
            bool unlocked = MechanicCodex.IsUnlocked("codex.event_director.worm") && MechanicCodex.IsUnlocked("codex.event_director") && EventDirectorService.HasOccurred(s, "worm");
            var raidSilent = new HashSet<string>
            {
                FeedbackCueCatalog.Get(FeedbackCueId.SignalLinkWarning)?.SfxId, "mus_raid_warning", NotificationCatalog.Tier(NotifyLevel.Warning)?.SfxId, NotificationCatalog.Tier(NotifyLevel.Urgent)?.SfxId,
            };
            string defaultWarn = FeedbackCueCatalog.Get(FeedbackCueId.WorldEventWarning)?.SfxId;
            bool sound = warnSfx == "sfx_beacon_launch" && !raidSilent.Contains(warnSfx) && !raidSilent.Contains(defaultWarn);
            Expect(lockedBefore && warned && started && awayEvents >= 3 && unlocked && sound,
                "N1 FGR-EVT-003 / 004 + FG-GAP-099：蠕虫请求 → 预警通知（world_event，带地点、写事件名、提示音 sfx_beacon_launch）→ 30 游戏秒后开始（破土前 30 秒裂纹，FGR-EVT-011）→ 结束通知；" +
                $"离家期间预警 / 开始 / 结束都记进离家报告“事件”段（{awayEvents} 条）；第一次发生前图鉴条目锁着，发生后解锁（含总条目）；" +
                $"事件提示音与突袭（mus_raid_warning / 警告级 {NotificationCatalog.Tier(NotifyLevel.Warning)?.SfxId}）、静默夜（{FeedbackCueCatalog.Get(FeedbackCueId.SignalLinkWarning)?.SfxId}）都不同" +
                $"（{lockedBefore} / {warned} / {started} / {unlocked} / {sound}）");
        }

        // ── T 倍速 / 观察 / S 存读档（真实家园） ─────────────────────────────────

        private static CampaignState RealWorld(int seed, bool observe)
        {
            EventDirectorService.ResetForTests();
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, 300);
            EventDirectorService.Install();
            EventDirectorService.OverrideDefsForTests(new List<EventDirectorDef> { Fx("fx_t", false, "condition", 0, 0, 6, 12, reward: "packet") }); // 非重大：同一段里来两次（重大威胁 2 日一次由 F1 覆盖）
            Fixture.Clear();
            Fixture["fx_t"] = new FixtureHandler("fx_t");
            EventDirectorService.Register(Fixture["fx_t"]);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void FrameTo(long target, ref int frames)
        {
            int guard = 0;
            while (GameClock.Ticks < target && guard++ < 60 * 600)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
        }

        private static string Snap(CampaignState s) =>
            JsonUtility.ToJson(s.DirectorEvents) + "|packets=" + string.Join(",", LootPickService.StateOf(s).Packets.Select(p => p.PacketId + "@" + p.Source + "@" + p.Tick));

        private static string RunScenario(int seed, bool observe, float speed, out bool pausedHeld, out int frames)
        {
            CampaignState s = RealWorld(seed, observe);
            frames = 0;
            EventDirectorService.Request(s, "fx_t", at: SignalCoreCore(s));
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
            long start = GameClock.Ticks;
            FrameTo(start + Sec(10), ref frames); // 预警 6 秒 → 开始
            EventDirectorInstanceRecord r = EventDirectorService.Active(s).FirstOrDefault();
            if (r != null)
            {
                EventDirectorService.Complete(s, r.Serial, EventDirectorService.OutcomeSuccess);
            }
            EventDirectorService.Request(s, "fx_t", at: SignalCoreCore(s));
            FrameTo(start + Sec(40), ref frames); // 第二次：不应对 → 超时
            GameClock.SetSpeed(1f);
            return Snap(s);
        }

        private static Vector2 SignalCoreCore(CampaignState s) => GameLogic.Campaign.Signal.SignalUplinkService.CorePosition(s);

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true, paused = true, proportional = true;
            var shown = new List<string>();
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(8601, true, speed, out bool held, out int frames);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    int k = 0;
                    while (k < snap.Length && k < reference.Length && snap[k] == reference[k])
                    {
                        k++;
                    }
                    diff = $"\n    {speed}x 与 0.5x 不同：…{reference.Substring(Math.Max(0, k - 60), Math.Min(140, reference.Length - Math.Max(0, k - 60)))} ≠ …{snap.Substring(Math.Max(0, k - 60), Math.Min(140, snap.Length - Math.Max(0, k - 60)))}";
                }
                same &= snap == reference;
                proportional &= Math.Abs(frames * speed - Sec(40)) <= 4f;
                shown.Add($"{speed}x {frames} 帧");
            }
            Expect(paused && same && proportional && reference.Contains("\"Outcome\":\"success\"") && reference.Contains("\"Outcome\":\"expired\"") && reference.Contains("@event@"),
                $"T1 暂停与 0.5x～3x：暂停中 120 帧事件状态不动；{string.Join(" / ", shown)} 跑同样的预警 → 开始 → 完成（三选一）→ 再一次超时，事件导演状态与数据包逐字段一致，" +
                $"所需真实帧与倍速成反比（预警按游戏时间）（暂停 {paused} / 一致 {same} / 成反比 {proportional}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(8602, true, 1f, out _, out _);
            string unseen = RunScenario(8602, false, 1f, out _, out _);
            Expect(seen == unseen && seen.Contains("@event@"), "T2 家园观察与不观察时，同一段事件（预警、开始、完成给三选一、超时）结果逐字段一致（FGR-BASE-021）");
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = RealWorld(8701, true);
            EventDirectorService.Request(s, "fx_t", at: SignalCoreCore(s));
            WorldSimulation.StepMany((int)Sec(12));
            EventDirectorService.Request(s, "fx_t", at: SignalCoreCore(s) + new Vector2(3f, 0f));
            WorldSimulation.StepMany((int)Sec(2)); // 第二次在预警中存档
            string before = Snap(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(CampaignSession.ActiveSlotIndex, SaveReason.Manual);
            int slot = CampaignSession.ActiveSlotIndex;
            WorldSimulation.StepMany((int)Sec(30));
            string continuous = Snap(s);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(slot);
            if (!rr.Success)
            {
                Fail("S1 读档失败：" + rr.Message);
                return;
            }
            CampaignState l = rr.State;
            CampaignSession.Set(slot, l);
            WorldSimulation.LoadHome(resume: true);
            string loaded = Snap(l);
            WorldSimulation.StepMany((int)Sec(30));
            string after = Snap(l);
            Expect(save.Success && loaded == before && after == continuous && l.DirectorEvents.Seeded && l.DirectorEvents.RngSeed == s.DirectorEvents.RngSeed,
                "S1 负向“事件进行中存档”：预警中真文件存档 → 读档后事件导演状态（排程、进行中、历史、随机数种子与序号、首次说明、频率）完全一致，继续跑与不存档的结果逐字段一致" +
                $"（读回 {loaded == before}，继续 {after == continuous}）");
        }

        // ── U 界面 ───────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            CampaignState s = Bare(8801, Fx("fx_u", false, "condition", 0, 0, 0, 100, reward: "packet"), Fx("fx_v", true, "condition", 0, 0, 50, 100));
            EventDirectorService.Request(s, "fx_u", at: new Vector2(5f, 6f));
            Drive(s, 0, Sec(2));
            EventDirectorService.Complete(s, EventDirectorService.Active(s)[0].Serial, EventDirectorService.OutcomeSuccess);
            EventDirectorService.Request(s, "fx_v", at: new Vector2(7f, 8f));
            Drive(s, Sec(2), Sec(15));
            string locatedRegion = null;
            Vector3 locatedAt = default;
            NotificationCenter.LocateHandler = (string region, Vector3 at, out string key) =>
            {
                locatedRegion = region;
                locatedAt = at;
                key = null;
                return true;
            };
            VisualElement root = MountUxml(UiKitFolder + "EventHistoryPanel.uxml", out GameObject go);
            EventHistoryPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                EventHistoryPanelUIToolkit panel = go.AddComponent<EventHistoryPanelUIToolkit>();
                panel.BindView(root);
                panel.SetOpen(true);
                bool rows = panel.PanelVisible && panel.VisibleRowCount == 2 && panel.RowText(0).Contains("进行中") && panel.RowText(0).Contains("预警中")
                            && panel.RowText(1).Contains("已应对") && panel.RowText(1).Contains("三选一") && panel.RowText(1).Contains("第 1 日")
                            && panel.FooterText.Contains("标准") && panel.CountText.Contains("2");
                Click(panel.RowButton(1));
                bool located = locatedRegion == HomeValleyLayout.RegionId && Math.Abs(locatedAt.x - 5f) < 1e-3 && Math.Abs(locatedAt.z - 6f) < 1e-3 && !panel.PanelVisible;
                CampaignState empty = Bare(8802);
                panel.SetOpen(true);
                panel.Refresh();
                bool emptyOk = panel.VisibleRowCount == 0 && panel.EmptyText.Contains("还没有");
                GameSettings.SetLanguage(GameLanguage.En);
                panel.Refresh();
                bool en = panel.EmptyText.Contains("No world events");
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                panel.SetOpen(false);
                Expect(rows && located && emptyOk && en,
                    "U1 事件历史面板（FG10 第 10 节）：进行中的事件在最前（预警中 / 剩余时间），历史写时间、事件、结果、奖励；点一行定位到事件地点并关掉面板；没有事件时写明；中英文" +
                    $"（{rows} / {located} / {emptyOk} / {en}）");
            }
            finally
            {
                EventHistoryPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
            string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "EventHistoryPanel.uxml", "EventHistoryRoot");
            Expect(probe.Contains("PASS"), "U2 事件历史面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + probe.Split('\n').FirstOrDefault());

            CampaignState st = Bare(8803);
            VisualElement pauseRoot = MountUxml(UiKitFolder + "PauseMenu.uxml", out GameObject pgo);
            try
            {
                PauseMenuUIToolkit pause = pgo.AddComponent<PauseMenuUIToolkit>();
                pause.BindView(pauseRoot);
                bool label = pause.EventHistoryButton?.text == "事件历史" && pause.EventFrequencyButton?.text == "事件频率：标准";
                Click(pause.EventFrequencyButton);
                bool high = EventDirectorService.Frequency(st) == EventDirectorService.FrequencyHigh && pause.EventFrequencyButton.text == "事件频率：多";
                Click(pause.EventFrequencyButton);
                bool off = EventDirectorService.Frequency(st) == EventDirectorService.FrequencyOff && pause.EventFrequencyButton.text == "事件频率：关闭";
                Click(pause.EventFrequencyButton);
                Click(pause.EventFrequencyButton);
                bool back = EventDirectorService.Frequency(st) == EventDirectorService.FrequencyStandard && st.DirectorEvents.Frequency == EventDirectorService.FrequencyStandard;
                Expect(label && high && off && back,
                    "U3 暂停菜单（FGR-EVT-006）：“事件历史”入口；“事件频率”按钮显示当前档位，点击依次切换 多 → 关闭 → 少 → 标准，写进本局存档" +
                    $"（{label} / {high} / {off} / {back}）");
            }
            finally
            {
                Object.DestroyImmediate(pgo);
            }
        }

        // ── P 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            var defs = new List<EventDirectorDef>();
            for (int i = 0; i < 12; i++)
            {
                defs.Add(Fx("fx_p" + i, i < 3, i % 2 == 0 ? "random" : "condition", 0.2, 0.6, 5, 60, i < 3 ? "major_threat" : ""));
            }
            CampaignState s = Bare(8901, defs.ToArray());
            for (int i = 0; i < 40; i++)
            {
                AddPlan(s, i % 7 == 0 ? "story_p" : null, RaidDirectorService.StateEnded);
            }
            long steps = 5 * Day;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Drive(s, 0, steps, t =>
            {
                if (t % Sec(37) == 0)
                {
                    EventDirectorService.Request(s, "fx_p" + (1 + (t / Sec(37)) % 6 * 2));
                }
            });
            sw.Stop();
            double perStepUs = sw.Elapsed.TotalMilliseconds * 1000.0 / steps;
            double maxEval = EventDirectorService.MaxEvaluateMs;
            PerfLines.Add($"12 种事件 + 40 个突袭计划跑 5 游戏日（{steps} 步）：平均每步 {perStepUs:0.000} μs，单次评估最慢 {maxEval:0.000} ms，评估 {EventDirectorService.Evaluations} 次，" +
                          $"发生 {Hist(s).Count} 次（Editor batchmode / Mono；真机 HybridCLR 解释执行另测，FG15-SYS-02）");
            Expect(perStepUs < 20.0 && maxEval < 2.0,
                $"P1 事件导演每步平均 {perStepUs:0.000} μs（< 20 μs）、单次评估最慢 {maxEval:0.000} ms（< 2 ms，帧预算 120 帧 = 8.3 ms 的一小部分）");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgEventDirectorSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private static void Click(Button b)
        {
            if (b?.clickable == null)
            {
                Fail($"按钮 {b?.name ?? "（空）"} 没有 Clickable");
                return;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
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
                UiConfirmDialog.DiscardAll();
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
