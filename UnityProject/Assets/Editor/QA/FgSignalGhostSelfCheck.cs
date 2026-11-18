using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Feedback;
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
using GameLogic.UI.Kit;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG12-EVT-03 信号幽灵的自动验收（FG10 FGR-EVT-020～023，FGT-EVT-004；FG19 FGR-EMG-165；承接 DEBT-FG12EVT01-02 幽灵部分、DEBT-EMG4HIJ02-01、DEBT-EMG4FW01-01 幽灵用例）。
    /// 全部起真实世界（家园 / 碎城远征地点的战斗内核 + 事件导演 + 真实 signal_ghost 处理器），走 <c>WorldSimulation.StepMany / Frame</c>，反向接入走正式入口 <c>SignalUplinkService.Request</c>。
    /// D 数据；A1 第一次剧情固定（第 2 幕前不来、“事件频率：关闭”也来、引导钩子）；A2 5 秒预警（预警中不夺控、屏幕杂波）；A3 夺控 45 秒（翻敌对、来源、通知、特效标记、攻击同伴）；
    /// A4 不应对 → 超时归还、无奖励；A5 之后随机（关闭时不随机）；B1 反向接入正式入口 3 秒夺回 + 奖励只给一次；B2 远征地点：干扰中 / 覆盖外拒绝，事件照常；
    /// C1 击倒 → 瘫痪不阵亡、事件结束后恢复、给奖励；C2 时限先到时已瘫痪 = 已应对；E1 选目标排除接入中 / 维修台、没有可选的不安排；F1 反夺控固件广播（DEBT-EMG4FW01-01）；
    /// T1 暂停与 0.5x～3x；T2 观察一致；S1 预警中存读档；S2 夺控 + 反向接入引导中存读档；U1 事件历史奖励行；P1 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgSignalGhostSelfCheck）。
    /// </summary>
    public static class FgSignalGhostSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UxmlPath = "Assets/GameRes/Raw/UI/UiKit/ReactionPopupHud.uxml";
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();
        private static readonly List<string> Hooks = new List<string>();

        [MenuItem("QA/自检/FG 信号幽灵")]
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
            Line("\n[信号幽灵] 第一次剧情固定 / 之后随机、5 秒预警杂波、夺控 45 秒、反向接入 3 秒夺回、击倒瘫痪不阵亡、奖励、负向、倍速 / 观察 / 存读档（FG12-EVT-03）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgghost-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                     "处理器在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），夺控状态机在 Main/Sim（Editor 下 Burst 未必开）；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckFirstStoryAndExpire);
                Step(CheckReclaimFormal);
                Step(CheckExpeditionRefused);
                Step(CheckKnockdown);
                Step(CheckTargetRules);
                Step(CheckStartRetract);
                Step(CheckLeaveSite);
                Step(CheckFailsafe);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSaveLoadWarned);
                Step(CheckSaveLoadReclaiming);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"信号幽灵自检抛异常：{e}");
            }
            finally
            {
                GuidanceHooks.Touched -= onHook;
                SignalCoverageService.OverrideForTests = null;
                SignalUplinkService.OnRepairBayProvider = null;
                ReactionPopupHudUIToolkit.InWorldOverrideForTests = false;
                SeizeViews.Clear();
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                EventDirectorService.ResetForTests();
                SignalGhostService.ResetSessionState();
                SignalSeizeService.ResetForTests();
                SignalUplinkService.ResetForTests();
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
            Line($"  · [信号幽灵] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────────

        private static long Sec(double s) => GameClock.TicksFor(s);
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
                WorldSimulation.StepMany(1);
            }
            return done();
        }

        private static void ResetServices()
        {
            EventDirectorService.ResetForTests();
            SignalGhostService.ResetSessionState();
            SignalSeizeService.ResetForTests();
            SignalUplinkService.ResetForTests();
            SignalCoverageService.OverrideForTests = null;
            SignalUplinkService.OnRepairBayProvider = null;
            Hooks.Clear();
        }

        private static void InstallDirector()
        {
            EventDirectorService.Install();
            EventDirectorService.Register(new SignalGhostService.Handler());
        }

        /// <summary>真实家园（星球战斗内核）+ 事件导演 + 真实 signal_ghost 处理器；在归还核心旁刷 <paramref name="machines"/> 台 ERC-003。战役在第 1 幕。</summary>
        private static CampaignState World(int seed, bool observe, int machines, out List<int> ids)
        {
            ResetServices();
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, 300);
            SignalCoreService.EnsureInitialized(s);
            InstallDirector();
            ids = new List<int>();
            // 带枪的装配（ERC-003 出厂蓝图没有武器）：被夺控时才能“用自己的装配攻击你”。
            BlueprintVersionRecord version = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()).ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != BpGun)
                .Append(new BlueprintRecord { BlueprintId = BpGun, DisplayName = BpGun, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            Vector2 core = HomeValleyLayout.Core.Position;
            for (int i = 0; i < machines; i++)
            {
                int id = Spawn(HomeValleyLayout.RegionId, core + new Vector2(-40f - 3f * i, 30f), BpGun); // 离归还核心旁的出厂机器够远：它们不会参与（否则会把被夺控的机器打瘫）
                CircuitOpResult reg = MachineLoadoutRegistry.Register(s, id, BpGun, 1);
                if (!reg.Success)
                {
                    Fail($"测试准备：登记装配失败：{reg.Message}");
                }
                ids.Add(id);
            }
            WorldSimulation.StepMany(2);
            return s;
        }

        private const string BpGun = "bp_selfcheck_ghost_gun";

        /// <summary>诊断：<paramref name="logicId"/> 周围 20 米内带武器的单位（种类 / 阵营 / 武器行）。</summary>
        private static string Nearby(int logicId)
        {
            CombatSite site = SiteOf(logicId);
            if (site == null || !site.TryGetMachinePosition(logicId, out Vector2 p))
            {
                return "-";
            }
            var sb = new StringBuilder();
            for (int id = 1; id < 4000; id++)
            {
                if (site.Kernel.TryGetUnit(id, out CombatUnitView v) && v.Weapon >= 0 && math.distance(v.Position, new double2(p.x, p.y)) < 20.0)
                {
                    sb.Append($"{id}:{v.Kind}/{v.Faction}/w{v.Weapon} ");
                }
            }
            return sb.ToString();
        }

        private static int Spawn(string region, Vector2 at, string bp = HomeValleyLayout.BlueprintErc003Id)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, region, at, 400f, 400f, "Player", 1);
            if (!r.Success)
            {
                Fail($"测试准备：登记机器失败：{r.Message}");
            }
            return r.LogicId;
        }

        private static EventDirectorInstanceRecord Inst(CampaignState s) => SignalGhostService.Active(s);

        /// <summary>进入第 2 幕（剧情节点 setAct = 2 的效果）后跑到事件开始（夺控中）。</summary>
        private static EventDirectorInstanceRecord ToSeized(CampaignState s, int forced)
        {
            SignalGhostService.ForcedTargetForTests = forced;
            s.Progress.Act = 2;
            StepUntil(() => Inst(s)?.State == EventDirectorService.StateActive && SignalSeizeService.IsSeized(Inst(s).Target), 30);
            return Inst(s);
        }

        private static CombatSite SiteOf(int logicId) =>
            MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) ? CombatSites.Get(rec.RegionId) : null;

        private static CombatFaction FactionOf(int logicId)
        {
            CombatSite site = SiteOf(logicId);
            return site != null && site.TryGetMachineUnit(logicId, out int u) && site.Kernel.TryGetUnit(u, out CombatUnitView v) ? v.Faction : CombatFaction.Neutral;
        }

        private static EventDirectorHistoryRecord LastHistory(CampaignState s) =>
            EventDirectorService.History(s).LastOrDefault(h => h.EventId == SignalGhostService.EventId);

        private static List<string> EventTexts() =>
            NotificationCenter.History.Where(e => e.Type?.Id == "world_event").SelectMany(e => e.Members).Select(m => m.Detail ?? string.Empty).ToList();

        private static int Heard(CampaignState s) => IntelService.StateOf(s)?.FragmentsHeard?.Length ?? 0;

        private static UplinkRequestResult Reclaim(int logicId) => SignalUplinkService.Request(logicId, UplinkSource.MachineList, startCamera: false);

        private sealed class Reader : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool GetKey(KeyCode key) => key == Down;
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        private const CombatUnitFlags Live = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled;

        // ── D 数据 ────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            EventDirectorDef d = EventDirectorService.Def(SignalGhostService.EventId);
            Expect(d != null && d.StoryFirst && d.ActMin == 2 && d.Trigger == EventDirectorService.TriggerRandom && Math.Abs(d.IntervalMinDays - 3) < 1e-6 && Math.Abs(d.IntervalMaxDays - 6) < 1e-6
                   && Math.Abs(d.WarningSeconds - 5) < 1e-6 && Math.Abs(d.DurationSeconds - 45) < 1e-6 && d.Reward == "fixed" && d.Major,
                "D1 fg.TbWorldEvent signal_ghost：第一次剧情固定、第 2 幕起、之后 3～6 日随机、预警 5 秒、持续 45 秒、奖励由处理器给（fixed）、重大威胁（FGR-EVT-020～023）");
            bool tuned = GridContent.TryGetTuning("event.signal_ghost.tech_data", out float tech) && tech > 0f;
            string[] keys = { "event_director.reward.fixed", "event_director.reward.tech_only", "signal_ghost.notify.reward", "signal_ghost.static", "signal_ghost.notify.no_target", "emg.seize.source.ghost" };
            string[] missing = keys.Where(k => !GameText.Has(k)).ToArray();
            Expect(tuned && missing.Length == 0 && Mathf.Approximately(SignalSeizeService.SeizeSeconds, 45f) && Mathf.Approximately(SignalSeizeService.ReclaimSeconds, 3f)
                   && GuidanceHooks.Known.Contains("event_director.first.signal_ghost"),
                $"D2 调参 event.signal_ghost.tech_data = {tech}、夺控 45 秒 / 反向接入 3 秒（emg.seize.*）、文本键齐（缺 {string.Join("、", missing)}）、第一次说明的引导钩子已登记");
            VisualTreeAsset vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            VisualElement root = vta != null ? vta.CloneTree() : null;
            Expect(root?.Q<VisualElement>("SignalStaticLayer") != null && root.Q<VisualElement>("SignalStaticBar5") != null && root.Q<Label>("SignalStaticLabel") != null
                   && root.Q<VisualElement>("SignalStaticLayer").ClassListContains("uk-hidden") && root.Q<VisualElement>("SignalStaticLayer").pickingMode == PickingMode.Ignore,
                "D3 ReactionPopupHud.uxml 有信号杂波层（6 条噪点带 + 提示，平时隐藏、不拦截点击）");
            string probe = BinGames.EditorTools.UiToolkitLayoutProbe.Probe(UxmlPath, "SignalStaticLayer", stressFill: true, prepare: r =>
            {
                r.Q<VisualElement>("SignalStaticLayer")?.RemoveFromClassList("uk-hidden");
                if (r.Q<Label>("SignalStaticLabel") is { } l)
                {
                    l.text = GameText.Get("signal_ghost.static");
                }
            });
            Expect(probe.StartsWith("PASS"), "D4 布局探针（四种分辨率 + 超长文字 + USS 体检）信号杂波层：" + probe.Split('\n')[0]);
        }

        // ── A 第一次剧情固定 → 预警 → 夺控 → 超时；之后随机 ────────────────────────

        private static void CheckFirstStoryAndExpire()
        {
            CampaignState s = World(9701, true, 1, out List<int> m); // 只有一台、周围没有别人：A4 验“没人管”的超时路径
            int a = m[0];
            SignalGhostService.ForcedTargetForTests = a;
            EventDirectorService.SetFrequency(s, EventDirectorService.FrequencyOff);
            StepSec(20);
            bool none1 = Inst(s) == null && SignalGhostService.StoryRequests == 0;
            s.Progress.Act = 2; // 第二幕剧情节点推进幕
            StepSec(0.5);
            EventDirectorInstanceRecord r = Inst(s);
            Expect(none1 && r != null && r.Story && r.State == EventDirectorService.StateWarned && SignalGhostService.StoryRequests == 1 && r.Target == a
                   && Hooks.Contains("event_director.first.signal_ghost") && Hooks.Contains(GuidanceHooks.EventDirectorFirstEvent),
                $"A1 第一次剧情固定（FGR-EVT-020）：第 1 幕 20 秒不来；进入第 2 幕（“事件频率：关闭”也一样）下一次评估就以剧情请求开始预警（story = {r?.Story}），" +
                "发第一次说明的引导钩子（event_director.first.signal_ghost，说明内容归 FG15-UX-04）");
            if (r == null)
            {
                return;
            }
            long warnTicks = r.StartTick - r.WarnTick;
            string near = "-";
            var go = new GameObject("sg-selfcheck-hud");
            var hud = go.AddComponent<ReactionPopupHudUIToolkit>();
            try
            {
                VisualElement root = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath).CloneTree();
                hud.BindView(root);
                ReactionPopupHudUIToolkit.InWorldOverrideForTests = true;
                ReactionPopups.Clear();
                hud.Tick(null, 0f);
                bool staticOn = hud.StaticVisible && hud.StaticText == GameText.Get("signal_ghost.static") && SignalGhostService.StaticActive(s);
                // 降低闪光：噪点带两次刷新位置不变（帧号不同也一样）
                GameSettings.SetFlashReductionEnabled(true);
                hud.Tick(null, 0.1f);
                float calm0 = hud.StaticBarTop(0);
                hud.Tick(null, 0.2f);
                bool calm = Mathf.Approximately(calm0, hud.StaticBarTop(0)) && hud.StaticVisible;
                GameSettings.SetFlashReductionEnabled(false);
                StepSec(4.5);
                bool notYet = !SignalSeizeService.IsSeized(a) && Inst(s)?.State == EventDirectorService.StateWarned;
                Expect(warnTicks == Sec(5) && staticOn && notYet && calm,
                    $"A2 5 秒预警（FGR-EVT-021）：预警 {warnTicks} 步 = 5 游戏秒；预警中屏幕信号杂波显示（“{hud.StaticText}”），4.5 秒时还没有夺控；设置“降低闪光”时噪点带不跳动（{calm}）");
                StepUntil(() => SignalSeizeService.IsSeized(a), 2);
                WorldSimulation.StepMany(3); // 夺控事件下一步才从内核取出（通知、钩子）
                near = Nearby(a);
                ReactionPopups.Clear();
                hud.Tick(null, 1f);
                bool staticOff = !hud.StaticVisible;
                SignalSeizeService.TryGetSeize(a, out bool downed, out int src);
                CombatSite site = SiteOf(a);
                site.TryGetSeizedMachine(a, out float remaining, out _, out _, out _);
                var note = NotificationCenter.History.Concat(NotificationCenter.Toasts).LastOrDefault(n => n.Type?.Id == "emg_seized");
                var vroot = new GameObject("sg-selfcheck-root");
                SeizeViews.FrameUpdate(site, vroot.transform);
                bool hasView = site.TryGetSeizedView(0, out Transform view, out _, out _);
                bool marked = SeizeViews.VisibleCount == 1 && hasView && SeizeViews.MarkAt(0).activeSelf && SeizeViews.MarkAt(0).transform.position.y > view.position.y + 1.5f
                              && SeizeViews.MarkAt(0).GetComponent<Renderer>().sharedMaterial.color.r > 0.8f && SeizeViews.MarkAt(0).GetComponent<Collider>() == null;
                int marks = SeizeViews.VisibleCount;
                SeizeViews.Clear();
                UnityEngine.Object.DestroyImmediate(vroot);
                Expect(src == SignalSeizeService.SourceGhost && !downed && remaining > 44f && FactionOf(a) == CombatFaction.Hostile
                       && Inst(s)?.State == EventDirectorService.StateActive && SignalGhostService.Seizes == 1 && note != null && staticOff && marked,
                    $"A3 夺控 45 秒（FGR-EVT-021 / FGR-EMG-165）：预警结束夺控 {SignalPresence.MachineLabel(a)}（共用状态机：内核夺控表、来源 = 信号幽灵、剩余 {remaining:0.#} 秒），翻敌对阵营、" +
                    $"警告级通知 emg_seized；杂波收起；机身上方红色信号标记与故障外壳（画着 {marks} 个，不挡点选）" +
                    $"[通知 {note != null}、杂波收起 {staticOff}、武器 {(site.TryGetMachineWeapon(a, out MachineWeaponInfo wi) ? wi.PrimaryId : "-")}]");
            }
            finally
            {
                ReactionPopupHudUIToolkit.InWorldOverrideForTests = false;
                UnityEngine.Object.DestroyImmediate(go);
            }
            int tech0 = s.TechData, heard0 = Heard(s);
            long seizedAt = GameClock.Ticks;
            StepUntil(() => Inst(s) == null, 60);
            long lasted = GameClock.Ticks - seizedAt;
            EventDirectorHistoryRecord h = LastHistory(s);
            Expect(h != null && h.Outcome == EventDirectorService.OutcomeExpired && Math.Abs(lasted - Sec(45)) <= Sec(1) && h.Story && h.RewardTech == 0 && s.TechData == tech0 && Heard(s) == heard0 && SignalGhostService.Rewards == 0
                   && !SignalSeizeService.IsSeized(a) && FactionOf(a) == CombatFaction.Player,
                $"A4 不应对：45 秒后自己恢复（FGR-EVT-022“等 45 秒”），事件历史“{EventDirectorService.OutcomeText(h?.Outcome)}”（夺控持续 {lasted} 步 ≈ 45 秒）、不给奖励，机器回到己方，通知“{CombatSite.LastSeizeEndText}”（周围带武器的单位：{near}）");
            EventDirectorScheduleRecord rec = EventDirectorService.Schedule(s, SignalGhostService.EventId);
            bool offNoRandom = EventDirectorService.DueTick(s, SignalGhostService.EventId) == long.MaxValue;
            EventDirectorService.SetFrequency(s, EventDirectorService.FrequencyStandard);
            long due = EventDirectorService.DueTick(s, SignalGhostService.EventId);
            double days = (due - rec.AnchorTick) / (double)Sec(GameClock.DaySeconds) * EventDirectorService.Rate(s);
            StepSec(30);
            Expect(rec.Occurrences == 1 && offNoRandom && due < long.MaxValue && days >= 3 - 1e-3 && days <= 6 + 1e-3 && SignalGhostService.StoryRequests == 1 && Inst(s) == null,
                $"A5 之后随机（FGR-EVT-020 / 006）：剧情请求只发一次；“关闭”档不随机安排；改回“标准”后按 3～6 游戏日随机（这次抽到 {days:0.##} 日 ÷ 频率倍率）");
            // 随机那一路也走同一处理器、同样写事件历史：把排程拨到到期（只改锚点、上一次重大威胁与结束时间，等价于时间过去了几天）。
            DirectorEventState d = EventDirectorService.StateOf(s);
            rec = EventDirectorService.Schedule(s, SignalGhostService.EventId);
            rec.AnchorTick = Math.Max(0, GameClock.Ticks - 10); // 锚点不能为负（负 = 还没排程，会从现在重新抽）：把间隔缩成 1 步
            rec.GapTicks = 1;
            rec.LastEndTick = -1;
            d.LastMajorTick = -1;
            StepUntil(() => Inst(s) != null, 15);
            EventDirectorInstanceRecord random = Inst(s);
            var candDiag = new List<int>();
            SignalGhostService.Candidates(s, candDiag);
            string blocked = $"候选 {candDiag.Count}、无目标 {SignalGhostService.NoTarget}、机器 {MachineRegistry.RecordCount}（{string.Join(",", MachineRegistry.AllRecords.Select(x => x.LogicId + ":" + x.IsAlive + ":" + x.RegionId))}）、挡住原因 {rec.BlockedKey}、到期步 {EventDirectorService.DueTick(s, SignalGhostService.EventId)} / 现在 {GameClock.Ticks}";
            StepUntil(() => Inst(s) == null, 60);
            EventDirectorHistoryRecord[] ghosts = EventDirectorService.History(s).Where(x => x.EventId == SignalGhostService.EventId).ToArray();
            Expect(random != null && !random.Story && SignalGhostService.Seizes == 2 && ghosts.Length == 2 && ghosts[0].Story && !ghosts[1].Story && ghosts[1].Outcome == EventDirectorService.OutcomeExpired,
                $"A6 随机安排的那次（story = {random?.Story}）同样预警 → 夺控 → 收尾，两条路径都记进事件历史（{ghosts.Length} 条：剧情 {ghosts.FirstOrDefault()?.Story} / 随机 {ghosts.LastOrDefault()?.Story}）" +
                (random == null ? "；" + blocked : string.Empty));
        }

        // ── B 反向接入 ─────────────────────────────────────────────────────────

        private static void CheckReclaimFormal()
        {
            CampaignState s = World(9702, true, 1, out List<int> m);
            int a = m[0];
            EventDirectorInstanceRecord r = ToSeized(s, a);
            if (r == null)
            {
                Fail("B1 事件没有开始");
                return;
            }
            int tech0 = s.TechData, heard0 = Heard(s);
            bool hasFragment = IntelService.NextFragment(s) != null;
            // 正式入口：左键选中的同一入口选中它 → 按“接入 / 退出接入”键（区域控制器的直控目标钩子 → SignalUplinkService.RequestFromSelection → Request → 反向接入）
            var keys = new Reader();
            InputRouter.Reset();
            InputRouter.DebugSetReader(keys);
            bool selected = WorldSimulation.Home.TrySelectMachine(a);
            keys.Down = GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView);
            InputRouter.DebugClearConsumedKeys();
            WorldSimulation.Frame(1f / 60f);
            keys.Down = KeyCode.None;
            long t0 = GameClock.Ticks;
            CombatSite site0 = SiteOf(a);
            bool began = site0.TryGetSeizedMachine(a, out _, out _, out float reclaimLeft, out _) && reclaimLeft > 0f;
            bool cameraStayed = WorldView.Director == null || !WorldView.Director.HeadingDirect && WorldView.Director.Mode != ViewMode.Direct;
            string feedback = SignalUplinkService.LastFeedbackText;
            StepSec(2.9);
            bool held = SignalSeizeService.IsSeized(a) && Inst(s) != null;
            StepUntil(() => !SignalSeizeService.IsSeized(a), 1);
            long took = GameClock.Ticks - t0;
            EventDirectorHistoryRecord h = LastHistory(s);
            int tech = Mathf.RoundToInt(GridContent.TryGetTuning("event.signal_ghost.tech_data", out float tv) ? tv : 40f);
            string rewardText = hasFragment ? GameText.Format("event_director.reward.fixed", tech) : GameText.Format("event_director.reward.tech_only", tech);
            bool noted = EventTexts().Any(x => x.Contains(rewardText));
            InputRouter.Reset();
            Expect(selected && began && cameraStayed && held && Math.Abs(took - Sec(3)) <= 3 && Inst(s) == null && h?.Outcome == EventDirectorService.OutcomeSuccess && SignalGhostService.Reclaims == 1
                   && FactionOf(a) == CombatFaction.Player && SignalUplinkService.CurrentMachine(s) != a,
                $"B1 反向接入正式入口（DEBT-EMG4HIJ02-01，FGR-EVT-022）：选中被夺控的机器按接入键（{GameSettings.KeyBindings.GetKey(GameActionId.ToggleCameraView)}，覆盖判定沿用 FG01）→ “{feedback}” → 引导 3 秒（{took} 步）后夺回；" +
                $"信号不进机器、镜头不切直控（{cameraStayed}，镜头导演 {(WorldView.Director != null ? WorldView.Director.Mode.ToString() : "无")}）；" +
                $"事件立即以“{EventDirectorService.OutcomeText(h?.Outcome)}”收尾");
            Expect(s.TechData == tech0 + tech && Heard(s) == heard0 + (hasFragment ? 1 : 0) && h != null && h.RewardTech == tech && (h.RewardFragment.Length > 0) == hasFragment
                   && SignalGhostService.Rewards == 1 && noted,
                $"B1 奖励（FGR-EVT-023）：技术数据 +{s.TechData - tech0}、加密片段（舰队伏笔，收进资料库）+{Heard(s) - heard0}，记进事件历史，通知“{rewardText}”");
            StepSec(60);
            Expect(s.TechData == tech0 + tech && SignalGhostService.Rewards == 1 && Heard(s) == heard0 + (hasFragment ? 1 : 0),
                "B1 奖励只给一次（再跑 60 秒不重复发放）");
        }

        /// <summary>B2 负向：选中的机器在远处的远征队里（碎城远征地点）——事件照常发生；在干扰场里 / 覆盖外反向接入被拒，回到覆盖内才能夺回。</summary>
        private static void CheckExpeditionRefused()
        {
            ResetServices();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            CampaignState s = CampaignState.CreateNew("fgghost-city", "Standard", 9703);
            CampaignSession.Set(7, s);
            s.BuildingRecords = new[]
            {
                new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeAssemblyStation, BuildingTypeId = HomeValleyLayout.BuildingTypeAssemblyStation,
                    RegionId = HomeValleyLayout.RegionId, ConstructionState = BuildingConstructionState.Operational, PowerState = BuildingPowerState.Powered,
                },
            };
            SignalCoreService.EnsureInitialized(s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            int x = Spawn(FracturedCityLayout.RegionId, new Vector2(-6f, -24f));
            int jam = Spawn(FracturedCityLayout.RegionId, new Vector2(2f, -24f));
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.Find(s).State = RegionState.Available;
            FracturedCityController city = WorldSimulation.LoadFracturedCity(new[] { x, jam }, resume: false);
            WorldView.Observe(city.SiteId);
            InstallDirector();
            WorldSimulation.StepMany(2);
            if (city.Combat.TryGetMachineUnit(jam, out int ju))
            {
                city.Combat.Kernel.SetPosition(ju, new double2(FracturedCityLayout.ListeningNode.Position.x + 3f, FracturedCityLayout.ListeningNode.Position.y));
            }
            EventDirectorInstanceRecord r = ToSeized(s, jam);
            bool away = MachineRoster.IsAway(MachineRegistry.TryGetRecord(jam, out MachineRecord jr) ? jr : null) && r?.RegionId == FracturedCityLayout.RegionId;
            UplinkRequestResult jammed = Reclaim(jam);
            StepSec(4);
            bool stillJ = SignalSeizeService.IsSeized(jam);
            if (city.Combat.TryGetMachineUnit(jam, out ju))
            {
                city.Combat.Kernel.SetPosition(ju, new double2(-6f, -20f));
            }
            SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(false, true, -20f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            UplinkRequestResult outside = Reclaim(jam);
            StepSec(4);
            bool stillO = SignalSeizeService.IsSeized(jam);
            SignalCoverageService.OverrideForTests = null;
            UplinkRequestResult inside = Reclaim(jam);
            StepSec(3.5);
            Expect(r != null && away && jammed.Failure == UplinkFailure.Jammed && !jammed.Accepted && stillJ && outside.Failure == UplinkFailure.OutOfCoverage && stillO
                   && inside.Accepted && !SignalSeizeService.IsSeized(jam) && LastHistory(s)?.Outcome == EventDirectorService.OutcomeSuccess,
                $"B2 负向“选中的机器在远征”：远征队（碎城）里的机器照常被夺控；在干扰场里反向接入被拒（{jammed.Failure}：{jammed.Text}）、覆盖外被拒（{outside.Failure}），" +
                "被拒时仍被夺控；回到覆盖内再接入 3 秒后夺回");
        }

        // ── C 击倒 ─────────────────────────────────────────────────────────────

        private static void CheckKnockdown()
        {
            for (int round = 0; round < 2; round++)
            {
                CampaignState s = World(9704 + round, true, 2, out List<int> m);
                int a = m[0];
                EventDirectorInstanceRecord r = ToSeized(s, a);
                CombatSite site = SiteOf(a);
                if (r == null || site == null || !site.TryGetMachineUnit(a, out int unit))
                {
                    Fail("C 事件没有开始");
                    return;
                }
                CombatKernel k = site.Kernel;
                site.TryGetMachinePosition(a, out Vector2 pa);
                int gun = k.AddWeapon(new CombatWeapon
                {
                    Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 14f, Damage = 120f, Cooldown = 0.3f, Program = -1,
                    Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
                });
                int shooter = k.Spawn(new CombatSpawn
                {
                    ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.Commanded, Flags = Live | CombatUnitFlags.Report,
                    Position = new double2(pa.x, pa.y + 5f), Home = new double2(pa.x, pa.y + 5f), Radius = 0.5f, Speed = 4f, Health = 20000f, MaxHealth = 20000f, Weapon = gun, BehaviorProfile = -1, Priority = 1,
                });
                MachineRegistry.TryGetRecord(m[1], out MachineRecord buddy);
                float buddy0 = buddy?.Health ?? 0f;
                StepSec(6); // 先让它用自己的装配打一会儿原来的同伴（同伴就在 3 米外，也会还手）
                float buddy1 = buddy?.Health ?? 0f;
                site.IssueCommand(shooter, CombatCommandKind.Attack, pa, unit, 0.5f, 14f, 0.5f, false);
                int downs = CombatSite.SeizeDownedCount;
                StepUntil(() => SignalSeizeService.TryGetSeize(a, out bool dn, out _) && dn, 20);
                StepSec(0.2);
                MachineRegistry.TryGetRecord(a, out MachineRecord rec);
                bool downedOk = SignalSeizeService.TryGetSeize(a, out bool downed, out _) && downed && rec.IsAlive && rec.Health <= 0f && CombatSite.SeizeDownedCount == downs + 1
                                && Inst(s)?.State == EventDirectorService.StateActive;
                k.Kill(shooter, 0);
                int tech0 = s.TechData;
                if (round == 0)
                {
                    Expect(buddy1 < buddy0, $"C0 被夺控的机器用自己的装配（机枪）攻击原来的同伴（同伴耐久 {buddy0:0.#} → {buddy1:0.#}）");
                    Expect(downedOk, $"C1 击倒（FGR-EVT-022 / D-10）：被夺控的己方机器打到 0 → 瘫痪（记录 IsAlive = {rec.IsAlive}、耐久 {rec.Health:0.#}），不阵亡；事件继续到时限");
                    StepUntil(() => Inst(s) == null, 50);
                    EventDirectorHistoryRecord h = LastHistory(s);
                    Expect(h?.Outcome == EventDirectorService.OutcomeSuccess && h.RewardTech > 0 && s.TechData > tech0 && rec.IsAlive && rec.Health > 0f && FactionOf(a) == CombatFaction.Player
                           && SignalGhostService.Rewards == 1,
                        $"C1 事件结束后恢复（耐久 {rec.Health:0.#}、回到己方）；击倒算已应对（“{EventDirectorService.OutcomeText(h?.Outcome)}”），给奖励（技术数据 +{s.TechData - tech0}）");
                }
                else
                {
                    // 导演的时限比内核先到（同一步的先后）：瘫痪中的实例按“已应对”收尾（FinalOutcome），并释放夺控让它恢复。
                    bool done = EventDirectorService.Complete(s, r.Serial, EventDirectorService.OutcomeExpired);
                    WorldSimulation.StepMany(2);
                    EventDirectorHistoryRecord h = LastHistory(s);
                    Expect(downedOk && done && h?.Outcome == EventDirectorService.OutcomeSuccess && s.TechData > tech0 && !SignalSeizeService.IsSeized(a) && rec.Health > 0f && SignalGhostService.Rewards == 1,
                        $"C2 时限先到时机器已瘫痪：结果改写为“{EventDirectorService.OutcomeText(h?.Outcome)}”、给奖励、释放夺控并恢复（耐久 {rec.Health:0.#}）");
                }
            }
        }

        // ── E 选目标 ───────────────────────────────────────────────────────────

        private static void CheckTargetRules()
        {
            CampaignState s = World(9706, true, 3, out List<int> m);
            int a = m[0], b = m[1], c = m[2];
            s.SignalCore.UplinkMachineLogicId = a; // 测试捷径：信号在 a 里（接入流程由 FgSignalUplinkSelfCheck 覆盖）
            SignalUplinkService.OnRepairBayProvider = id => id == b;
            var cand = new List<int>();
            SignalGhostService.Candidates(s, cand);
            EventDirectorInstanceRecord r = ToSeized(s, 0);
            Expect(!cand.Contains(a) && !cand.Contains(b) && cand.Contains(c) && r != null && r.Target != a && r.Target != b && SignalSeizeService.IsSeized(r.Target),
                $"E1 选目标（FGR-EVT-021）：不选玩家正在接入的（{SignalPresence.MachineLabel(a)}）、不选在维修台上的（{SignalPresence.MachineLabel(b)}）；候选 {cand.Count} 台，选中 {SignalPresence.MachineLabel(r?.Target ?? 0)}");
            CampaignState s2 = World(9707, true, 1, out _);
            SignalUplinkService.OnRepairBayProvider = id => true;
            s2.Progress.Act = 2;
            StepSec(25);
            EventDirectorScheduleRecord rec = EventDirectorService.Schedule(s2, SignalGhostService.EventId);
            bool waited = Inst(s2) == null && rec != null && rec.Pending && rec.PendingStory && SignalGhostService.NoTarget > 0;
            SignalUplinkService.OnRepairBayProvider = null;
            StepSec(12);
            Expect(waited && Inst(s2) != null && Inst(s2).Story,
                "E1 一台可选的都没有：不安排（没有半成品事件），剧情请求留着；有可选的机器后下一次评估照常开始");
        }

        /// <summary>E2（修复轮 1）：预警 5 秒里目标全部失效（都送修）→ 剧情的第一次撤回成推迟：不进历史、发生次数退回 0、剧情请求留着；有可选的机器后补上，仍是剧情事件。</summary>
        private static void CheckStartRetract()
        {
            CampaignState s = World(9711, true, 2, out List<int> m);
            SignalGhostService.ForcedTargetForTests = m[0];
            s.Progress.Act = 2;
            bool warned = StepUntil(() => Inst(s)?.State == EventDirectorService.StateWarned, 30);
            int hist0 = EventDirectorService.History(s).Count(h => h.EventId == SignalGhostService.EventId);
            SignalUplinkService.OnRepairBayProvider = id => true; // 预警期间两台都上了维修台
            StepUntil(() => Inst(s) == null, 10);
            EventDirectorScheduleRecord rec = EventDirectorService.Schedule(s, SignalGhostService.EventId);
            bool retracted = Inst(s) == null && rec != null && rec.Pending && rec.PendingStory && rec.Occurrences == 0
                             && EventDirectorService.History(s).Count(h => h.EventId == SignalGhostService.EventId) == hist0 && !m.Any(SignalSeizeService.IsSeized);
            string postponed = GameText.Format("event_director.notify.postponed", "", GameText.Get("signal_ghost.block.no_target"));
            bool told = EventTexts().Any(t => t.Contains(GameText.Get("signal_ghost.block.no_target")));
            SignalUplinkService.OnRepairBayProvider = null;
            StepUntil(() => Inst(s) != null, 20);
            Expect(warned && retracted && told && Inst(s) != null && Inst(s).Story && rec.Occurrences == 1,
                $"E2 预警中目标全部失效：剧情的第一次撤回成推迟（不进历史、发生次数退回、剧情请求留着，通知“…{postponed}”），有可选的机器后补上、仍是剧情事件");
        }

        /// <summary>
        /// G（修复轮 1，审查 P1）：夺控中的机器离开地点。G1 瘫痪中被派走 / 走出室内（共用入口 CombatSite.RemoveMachine）→ 当场归还、耐久恢复、不阵亡、算已应对并给奖励；
        /// G2 没瘫痪的离开 → 归还并通知、事件作罢（不给奖励）；G3 地点整体卸载（撤离 = FlushPendingEvents）时瘫痪中 → 同样恢复、已应对。
        /// </summary>
        private static void CheckLeaveSite()
        {
            for (int round = 0; round < 3; round++)
            {
                CampaignState s = World(9712 + round, true, 2, out List<int> m);
                int a = m[0];
                EventDirectorInstanceRecord r = ToSeized(s, a);
                CombatSite site = SiteOf(a);
                if (r == null || site == null || !site.TryGetMachineUnit(a, out int unit))
                {
                    Fail($"G{round + 1} 事件没有开始");
                    return;
                }
                bool down = round != 1;
                if (down)
                {
                    site.Kernel.DownSeized(unit, 0);
                    StepSec(0.2);
                }
                MachineRegistry.TryGetRecord(a, out MachineRecord rec);
                bool downedOk = !down || rec.IsAlive && rec.Health <= 0f && SignalSeizeService.TryGetSeize(a, out bool dn, out _) && dn;
                int tech0 = s.TechData, rewards0 = SignalGhostService.Rewards;
                if (round == 2)
                {
                    site.FlushPendingEvents();
                }
                else
                {
                    site.RemoveMachine(a, export: true);
                }
                EventDirectorHistoryRecord h = LastHistory(s);
                bool common = downedOk && Inst(s) == null && rec.IsAlive && rec.Health > 0f && !site.TryGetSeizedMachine(a, out _, out _, out _, out _);
                if (round == 1)
                {
                    Expect(common && h?.Outcome == EventDirectorService.OutcomeCancelled && s.TechData == tech0 && SignalGhostService.Rewards == rewards0
                           && CombatSite.LastSeizeEndText == GameText.Format("emg.seize.notify.ended", SignalPresence.MachineLabel(a)),
                        $"G2 没瘫痪的被夺控机器离开地点：归还并通知（“{CombatSite.LastSeizeEndText}”），事件“{EventDirectorService.OutcomeText(h?.Outcome)}”、不给奖励，不再静默丢掉夺控记录");
                }
                else
                {
                    Expect(common && h?.Outcome == EventDirectorService.OutcomeSuccess && s.TechData > tech0 && SignalGhostService.Rewards == rewards0 + 1,
                        $"G{round + 1} 瘫痪中{(round == 0 ? "离开地点（RemoveMachine：派遣 / 走出室内 / 对账）" : "地点整体卸载（撤离时的 FlushPendingEvents）")}：" +
                        $"耐久恢复到 {rec.Health:0.#}、不阵亡，事件“{EventDirectorService.OutcomeText(h?.Outcome)}”、技术数据 +{s.TechData - tech0}（FGR-EVT-022 / 023、D-10）");
                }
                StepSec(50);
                Expect(SignalGhostService.Rewards == rewards0 + (round == 1 ? 0 : 1) && rec.IsAlive, $"G{round + 1} 之后再跑 50 秒：奖励不重复、机器仍存活");
            }
        }

        // ── F 反夺控固件（DEBT-EMG4FW01-01）────────────────────────────────────

        private static void CheckFailsafe()
        {
            CampaignState s = World(9708, true, 2, out List<int> m);
            int a = m[0], b = m[1];
            CombatSite c = WorldSimulation.Home.Combat;
            CombatKernel k = c.Kernel;
            c.TryGetMachineUnit(a, out int ua);
            c.TryGetMachineUnit(b, out int ub);
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(ChassisCatalog.ChassisWheelId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            board.TrySetUplink(2);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, new[] { "fw_failsafe" });
            k.SetUnitWeapon(ua, c.WeaponIndex(c.WithProgram(p, CombatSite.MachineWeaponFrom(p), null, out _, out _)));
            int listen = CombatSite.RegisterProgram(k, EmgProgramCompiler.Compile(Array.Empty<string>(), new[] { "fw_pingfire" }, FirmwareCarrier.Projectile, 0));
            k.SetUnitWeapon(ub, k.AddWeapon(new CombatWeapon
            {
                Mode = CombatWeaponMode.Instant, HasOutput = 1, TargetMode = CombatTargetMode.Nearest, Range = 30f, Damage = 1f, Cooldown = 0.5f, Program = listen,
                Reading = new CombatReading { Carrier = CombatCarrier.Projectile },
            }));
            k.SetUnitRelay(ub, true);
            c.TryGetMachinePosition(b, out Vector2 pb);
            k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None, Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                Position = new double2(pb.x, pb.y + 8f), Home = new double2(pb.x, pb.y + 8f), Radius = 0.8f, Speed = 0f, Health = 5000f, MaxHealth = 5000f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
            });
            long sent0 = k.EmgBusStats.PingsSent, delivered0 = k.EmgBusStats.PingsDelivered, sub0 = k.EmgBusStats.SubSpawned;
            EventDirectorInstanceRecord r = ToSeized(s, a);
            WorldSimulation.StepMany(6);
            long sent = k.EmgBusStats.PingsSent - sent0, delivered = k.EmgBusStats.PingsDelivered - delivered0, sub = k.EmgBusStats.SubSpawned - sub0;
            Expect(r != null && r.Target == a && sent >= 1 && delivered >= 1 && sub >= 1,
                $"F1 DEBT-EMG4FW01-01 信号幽灵正式来源：装反夺控（fw_failsafe）的机器被夺控 → 向中继组广播 0 号频道 {sent} 次、装中继应答的友机收到 {delivered} 次并开火、放出子载荷 {sub} 份");
        }

        // ── T 暂停 / 倍速 / 观察 ──────────────────────────────────────────────

        private static string Snap(CampaignState s, int target)
        {
            var sb = new StringBuilder(JsonUtility.ToJson(s.DirectorEvents));
            sb.Append("|tech=").Append(s.TechData).Append("|heard=").Append(Heard(s));
            CombatSite site = SiteOf(target);
            if (site != null && site.TryGetSeizedMachine(target, out float rem, out bool dn, out float rl, out int src))
            {
                sb.Append($"|seize:{rem:R}:{dn}:{rl:R}:{src}");
            }
            sb.Append("|faction=").Append(FactionOf(target));
            return sb.ToString();
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

        private static string RunScenario(int seed, bool observe, float speed, out bool pausedHeld, out int frames)
        {
            CampaignState s = World(seed, observe, 1, out List<int> m);
            int a = m[0];
            frames = 0;
            SignalGhostService.ForcedTargetForTests = a;
            s.Progress.Act = 2;
            GameClock.SetPaused(true);
            string p0 = Snap(s, a);
            long t0 = GameClock.Ticks;
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            pausedHeld = Snap(s, a) == p0 && GameClock.Ticks == t0;
            GameClock.SetPaused(false);
            GameClock.SetSpeed(speed);
            FrameTo(t0 + Sec(9), ref frames); // 请求 → 5 秒预警 → 夺控中
            Reclaim(a);
            FrameTo(t0 + Sec(16), ref frames); // 3 秒后夺回、给奖励
            GameClock.SetSpeed(1f);
            return Snap(s, a);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true, paused = true, proportional = true;
            var shown = new List<string>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(9801, true, speed, out bool held, out int frames);
                paused &= held;
                reference ??= snap;
                same &= snap == reference;
                proportional &= Math.Abs(frames * speed - Sec(16)) <= 4f;
                shown.Add($"{speed}x {frames} 帧");
            }
            Expect(paused && same && proportional && reference.Contains("\"Outcome\":\"success\""),
                $"T1 暂停与 0.5x～3x：暂停中 120 帧预警 / 夺控不动；{string.Join(" / ", shown)} 跑同样的剧情请求 → 5 秒预警 → 夺控 → 反向接入 3 秒夺回 → 奖励，导演、夺控、奖励逐字段一致，" +
                $"所需真实帧与倍速成反比（暂停 {paused} / 一致 {same} / 成反比 {proportional}）");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(9802, true, 1f, out _, out _);
            string unseen = RunScenario(9802, false, 1f, out _, out _);
            Expect(seen == unseen && seen.Contains("\"Outcome\":\"success\""), "T2 家园观察与不观察时，同一段（剧情请求、预警、夺控、反向接入夺回、奖励）结果逐字段一致（FGR-BASE-021）");
        }

        // ── S 存读档 ───────────────────────────────────────────────────────────

        private static SaveResult SaveNow(out int slot)
        {
            WorldSimulation.SyncAllForSave();
            slot = CampaignSession.ActiveSlotIndex;
            return CampaignAutoSaveService.SaveWithExport(slot, SaveReason.Manual);
        }

        private static CampaignState Reload(int slot, string tag)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
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

        private static void SaveLoadCase(string tag, CampaignState s, int target, double runAfter, Action beforeSave, string what)
        {
            beforeSave?.Invoke();
            string before = Snap(s, target);
            SaveResult save = SaveNow(out int slot);
            StepSec(runAfter);
            string continuous = Snap(s, target);
            int rewards = SignalGhostService.Rewards;
            CampaignState l = Reload(slot, tag);
            if (l == null)
            {
                return;
            }
            string loaded = Snap(l, target);
            StepSec(runAfter);
            string after = Snap(l, target);
            int diffAt = 0;
            while (diffAt < after.Length && diffAt < continuous.Length && after[diffAt] == continuous[diffAt])
            {
                diffAt++;
            }
            Expect(save.Success && loaded == before && after == continuous,
                $"{tag} {what}：真文件存档 → 读档后导演实例、内核夺控（剩余 / 瘫痪 / 引导 / 来源）、奖励完全一致，继续跑与不存档逐字段一致（读回 {loaded == before}，继续 {after == continuous}" +
                (after == continuous ? string.Empty : $"，首个差异 …{continuous.Substring(Math.Max(0, diffAt - 40), Math.Min(100, continuous.Length - Math.Max(0, diffAt - 40)))}") + $"；不存档那一路共发奖励 {rewards} 次）");
        }

        private static void CheckSaveLoadWarned()
        {
            CampaignState s = World(9901, true, 1, out List<int> m);
            SignalGhostService.ForcedTargetForTests = m[0];
            s.Progress.Act = 2;
            StepUntil(() => Inst(s)?.State == EventDirectorService.StateWarned, 5);
            StepSec(2);
            SaveLoadCase("S1", s, m[0], 10, null, "负向“事件进行中存档”——预警中（还剩 3 秒）");
        }

        private static void CheckSaveLoadReclaiming()
        {
            CampaignState s = World(9902, true, 1, out List<int> m);
            ToSeized(s, m[0]);
            StepSec(3);
            SaveLoadCase("S2", s, m[0], 6, () =>
            {
                Reclaim(m[0]);
                StepSec(1);
            }, "夺控中、反向接入引导到一半（还剩 2 秒）");
        }

        // ── P 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = World(9903, false, 12, out List<int> m);
            ToSeized(s, m[0]);
            const int n = 100000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                SignalGhostService.WorldStep(s);
            }
            double stepUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                SignalGhostService.StaticActive(s);
            }
            double staticUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            var cand = new List<int>();
            sw.Restart();
            for (int i = 0; i < 1000; i++)
            {
                SignalGhostService.Candidates(s, cand);
            }
            double candUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 1000;
            PerfLines.Add($"SignalGhostService.WorldStep（每世界步，事件进行中）{stepUs:0.###} µs；StaticActive（每帧 HUD）{staticUs:0.###} µs；选目标 Candidates（只在事件开始时，{MachineRegistry.RecordCount} 台机器）{candUs:0.#} µs");
            Expect(stepUs < 5.0 && staticUs < 5.0, $"P1 每步 / 每帧开销与机器数无关（O(1)：{stepUs:0.###} / {staticUs:0.###} µs，线 5 µs）；选目标 O(机器数) 只在事件开始那一刻");
        }

        // ── 断言 ───────────────────────────────────────────────────────────────

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
            finally
            {
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                SignalGhostService.ForcedTargetForTests = 0;
                SignalCoverageService.OverrideForTests = null;
                SignalUplinkService.OnRepairBayProvider = null;
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
