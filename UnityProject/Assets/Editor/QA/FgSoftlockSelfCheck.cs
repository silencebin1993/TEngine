using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
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
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG4-ECO-10 软锁保底与副产品去处的自动验收（FG04 FGR-ECO-070～072；FGT-ECO-005 / 006；沿用并扩展 ER3-SOFTLOCK-01；
    /// 承接 DEBT-FG4ECO08-03 顶栏流体存量、DEBT-FG4ECO04-05 地下管线升级）。全部起真实系统：真实世界模拟与家园（WorldSimulation.LoadHome）、
    /// 真实传送带 / 管线 / 寻路内核、真实存读档文件、真实建造菜单与施工工单。场地按种子生成的地形找（B25）。
    /// A 数据（调参、文本中英、图鉴逐字段、钩子、通知类型与离家报告分段、反馈时刻、升级路线）；
    /// B FGT-ECO-005 旅程：清空所有机器和废料 → 当天应急打印 → 应急产废料攒到回收站造价 → 真实建造菜单放回收站 → 打印的搬运机施工 → 回收站工作、应急产废料结束；第二天再打印、够 2 台后不再打印；
    /// C 应急打印与产废料的负向矩阵（机器够、今天已打印、核心被毁、废料 &gt; 0、回收站在工作 / 输出堵塞、出生点被占、Demo 紧急救援机的分层、读档不再补发开局机器、只剩战斗履带也打印、能施工的都在远征时照旧派救援机）；
    /// D 传送带闭环卡死（警告一次、告警栏、清带恢复、物品减少重新计时、非闭环不报、接上更小坐标的支线不丢计时）；E 施工 / 维修目标到不了（被围住警告与原因、告警栏、拆掉一面恢复、能到的不报）；
    /// F FGR-ECO-071：任何固体都能经传送带进回收站分解、任何流体都能经管线进废液池销毁；FGT-ECO-006 副产品无处可去 → 原因与引导钩子 → 接上废液池恢复；固体副产品指向回收站；
    /// G 顶栏流体存量（DEBT-FG4ECO08-03）；H 地下管线成对升级（DEBT-FG4ECO04-05）；I 真文件存读档接着跑一致；J 暂停与 0.5x～3x；K 观察 / 不观察一致；L 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgSoftlockSelfCheck）。
    /// </summary>
    public static class FgSoftlockSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 软锁保底与副产品去处")]
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
            Line("\n[软锁保底与副产品去处] 应急打印、核心应急产废料、传送带闭环卡死、施工 / 维修目标到不了、任何固体 / 流体都有去处（FG4-ECO-10）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsoftlock-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "软锁服务在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），寻路泛洪 / 传送带 / 管线内核在 AOT（Burst；真机 IL2CPP）；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckRecoveryJourney);
                Step(CheckPrintNegatives);
                Step(CheckCoreScrapNegatives);
                Step(CheckLoopDeadlock);
                Step(CheckUnreachableSite);
                Step(CheckAnySolidToRecycler);
                Step(CheckAnyFluidToWastePond);
                Step(CheckByproductJourney);
                Step(CheckFluidStockTopBar);
                Step(CheckUndergroundPipeUpgrade);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"软锁保底自检抛异常：{e}");
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
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.DebugSetReader(null);
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
                SoftlockService.ResetSessionState();
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
            Line($"  · [软锁保底与副产品去处] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 300)
        {
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            NotificationCenter.ResetForTests();
            NotificationCenter.Bind(s);
            FeedbackCues.ResetForTests();
            return s;
        }

        private static void Seconds(float s) => FgProductionSelfCheck.Seconds(s);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        private static GridCell At(GridCell o, int dx, int dy) => new GridCell(o.X + dx, o.Y + dy);

        private static SoftlockState St(CampaignState s) => SoftlockService.StateOf(s);

        /// <summary>测试捷径：清空全部机器（真实阵亡入口 MachineRegistry.ApplyDamage）。</summary>
        private static void KillAllMachines()
        {
            foreach (int id in MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive).Select(m => m.LogicId).ToArray())
            {
                MachineRegistry.ApplyDamage(id, 99999f);
            }
        }

        private static int Alive() => SoftlockService.CountHomeMachines();

        private static int NotifyCount(string type) => NotificationCenter.History.Where(e => e.Type?.Id == type).Sum(e => e.Count);

        private static bool AnyNotify(string type, string contains) =>
            NotificationCenter.History.Any(e => e.Type?.Id == type && e.Members.Any(m => (m.DetailText ?? string.Empty).Contains(contains)));

        /// <summary>走到下一个游戏日开头之后 <paramref name="extraSeconds"/> 秒（真实世界步）。</summary>
        private static void ToNextDay(float extraSeconds)
        {
            int day = GameClock.DayOf(GameClock.GameSeconds);
            double startHour = GridContent.Tuning("clock.start_hour");
            double since = GameClock.GameSeconds + startHour / 24.0 * GameClock.DaySeconds;
            double left = day * GameClock.DaySeconds - since;
            Seconds((float)left + extraSeconds);
        }

        private static GridCell? FindRuinSquare(CampaignState s, int radius)
        {
            byte ruin = GridContent.TerrainCode("ruin");
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell core = HomeGridService.CorePivot(s);
            for (int r = 4; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var p = new GridCell(core.X + dx, core.Y + dy);
                        bool all = true;
                        for (int y = -1; y <= 2 && all; y++)
                        {
                            for (int x = -1; x <= 2 && all; x++)
                            {
                                all = map.GetTerrain(At(p, x, y)) == ruin;
                            }
                        }
                        if (all && HomeGridService.ValidatePlacement(s, "recycler", p, 0, asPlayerPlacement: true, checkCost: false).Ok)
                        {
                            return p;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>软锁指纹：打印、应急产废料、废料、机器台数、闭环与目标的计时（确定性对照）。</summary>
        private static string Snap(CampaignState s)
        {
            SoftlockState st = St(s);
            var sb = new StringBuilder();
            sb.Append("day:").Append(st.LastPrintDay).Append("|prints:").Append(st.PrintCount).Append("|last:").Append(st.LastPrintLogicId)
                .Append("|cs:").Append(st.CoreScrapActive).Append('/').Append(st.CoreScrapTicks).Append('/').Append(st.CoreScrapProduced)
                .Append("|scrap:").Append(s.Scrap).Append("|alive:").Append(Alive()).Append("|tick:").Append(GameClock.Ticks);
            foreach (LoopWatchRecord w in st.Loops)
            {
                sb.Append("|loop:").Append(w.X).Append(',').Append(w.Y).Append('@').Append(w.SinceTick).Append('/').Append(w.LastItems).Append('/').Append(w.Alerted);
            }
            foreach (ReachWatchRecord w in st.Sites)
            {
                sb.Append("|site:").Append(w.BuildingId).Append('@').Append(w.SinceTick).Append('/').Append(w.Alerted).Append('/').Append(w.Enclosed);
            }
            return sb.ToString();
        }

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string[] tuning =
            {
                "eco.softlock.check_seconds", "eco.softlock.print_min_machines", "eco.softlock.print_health", "eco.softlock.core_scrap_per_minute",
                "eco.softlock.loop_check_seconds", "eco.softlock.loop_stall_seconds", "eco.softlock.reach_check_seconds", "eco.softlock.unreachable_seconds",
            };
            string[] missing = tuning.Where(id => !GridContent.TryGetTuning(id, out _)).ToArray();
            bool spec = SoftlockService.MinMachines == 2 && Math.Abs(SoftlockService.CoreScrapPerMinute - 1f) < 1e-6f
                        && SoftlockService.CoreScrapTarget == HomeValleyLayout.BuildProfile["recycler"].ScrapCost && SoftlockService.CoreScrapTarget > 0;
            string[] keys =
            {
                "softlock.print.caption", "softlock.print.next", "softlock.core_scrap.notify", "softlock.core_scrap.status", "softlock.core_scrap.done",
                "softlock.core_scrap.done_target", "softlock.core_scrap.done_recycler", "softlock.loop.notify", "softlock.loop.alarm", "softlock.reach.notify",
                "softlock.reach.alarm", "softlock.reach.enclosed", "softlock.reach.disconnected", "softlock.reach.terrain", "softlock.reach.kind.build",
                "softlock.reach.kind.repair", "eco.reason.byproduct_no_room_solid", "topbar.fluid_stock", "topbar.fluid_stock_tip", "plan.reason.pipe_underground_unpaired",
                "feedback.tag.emergency_print", "feedback.caption.emergency_print", "codex.economy.softlock.title", "codex.economy.softlock.body", "codex.economy.softlock.hint",
                "notify.type.core_emergency_scrap.single", "notify.type.belt_loop_stall.single", "notify.type.site_unreachable.single", "notify.type.emergency_rescue.single",
            };
            var badText = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string k in keys)
                {
                    if (!GameText.Has(k) || GameText.ContainsMarker(GameText.Get(k)))
                    {
                        badText.Add(lang + ":" + k);
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            CodexEntry codex = ConfigSystem.Instance.Tables.TbCodexEntry.GetOrDefault("codex.economy.softlock");
            string[] hooks = { GuidanceHooks.SoftlockFirstPrint, GuidanceHooks.SoftlockFirstCoreScrap, GuidanceHooks.SoftlockFirstLoopStall, GuidanceHooks.SoftlockFirstUnreachable,
                GuidanceHooks.EconomyFirstByproductBlocked };
            bool codexOk = codex != null && hooks.All(h => codex.Hooks.Contains(h)) && hooks.All(h => GuidanceHooks.Known.Contains(h));
            var expectTier = new Dictionary<string, NotifyLevel>
            {
                { "core_emergency_scrap", NotifyLevel.Info }, { "belt_loop_stall", NotifyLevel.Warning }, { "site_unreachable", NotifyLevel.Warning },
                { "emergency_rescue", NotifyLevel.Warning },
            };
            bool tiers = expectTier.All(kv => NotificationCatalog.TryGetType(kv.Key, out NotifyTypeDef d) && d.Tier == kv.Value && d.KeepInHistory);
            var expectSection = new Dictionary<string, string>
            {
                { "core_emergency_scrap", "production" }, { "belt_loop_stall", "bottleneck" }, { "site_unreachable", "buildings" }, { "emergency_rescue", "machines" },
            };
            string[] badSection = expectSection.Where(kv => !NotificationCatalog.TryGetType(kv.Key, out NotifyTypeDef d) || d.AwaySection != kv.Value).Select(kv => kv.Key).ToArray();
            bool cue = NotificationCatalog.TryGetType("emergency_rescue", out NotifyTypeDef rescueType) && rescueType.Cues.Contains(FeedbackCueId.EmergencyPrint)
                       && rescueType.Cues.Contains(FeedbackCueId.EmergencyRescue);
            FeedbackCueDef printCue = FeedbackCueCatalog.Get(FeedbackCueId.EmergencyPrint);
            bool cueDef = printCue != null && !string.IsNullOrEmpty(printCue.SfxId) && printCue.ResolvedTag == "核心应急" && printCue.ResolvedCaption == "归还核心";
            bool upgrade = GridContent.TryGetUpgrade("pipe_underground_t1", out string upTo) && upTo == "pipe_underground_t2";
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string dump) = FgProductionSelfCheck.RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string cx = dump.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("CX\tcodex.economy.softlock\t", StringComparison.Ordinal)) ?? string.Empty;
            string runtime = codex == null ? string.Empty : string.Join("\t", "CX", codex.Id, codex.Tab, codex.TitleKey, codex.BodyKey, codex.HintKey, codex.Links, codex.Hooks,
                codex.SortOrder.ToString(CultureInfo.InvariantCulture));
            Expect(missing.Length == 0 && spec && badText.Count == 0 && codexOk && tiers && badSection.Length == 0 && cue && cueDef && upgrade && code == 0 && cx == runtime,
                $"A1 数据：eco.softlock.* 调参齐（机器少于 2 台、每分钟 1 件，FGR-ECO-070 初值），应急产废料攒到回收站造价 {SoftlockService.CoreScrapTarget}；文本中英齐（{keys.Length} 条）；" +
                $"图鉴“保底与死锁检测”逐字段与源数据一致、5 个钩子已登记；通知类型等级 / 进历史 / 离家报告分段正确；应急打印反馈时刻有声音与字幕并转成“核心应急”通知；地下管线 T1 → T2 升级路线" +
                (missing.Length + badText.Count + badSection.Length == 0 ? string.Empty : $"（缺调参 {string.Join(",", missing)}；坏文本 {string.Join(",", badText)}；分段 {string.Join(",", badSection)}）") +
                (cx == runtime ? string.Empty : $"\n源数据：{cx}\n运行时：{runtime}"));
        }

        // ── B FGT-ECO-005 旅程 ─────────────────────────────────────────────────────

        private static void CheckRecoveryJourney()
        {
            CampaignState s = NewWorld(10101, scrap: 0);
            GridCell? ruin = FindRuinSquare(s, 30);
            if (ruin == null)
            {
                Fail("B 核心 30 格内找不到 4×4 整块废墟");
                return;
            }
            KillAllMachines();
            int deadBefore = Alive();
            int day1 = GameClock.DayOf(GameClock.GameSeconds);
            bool printed = StepUntil(() => St(s).PrintCount == 1, 3);
            MachineRecord hauler = MachineRegistry.TryGetRecord(St(s).LastPrintLogicId, out MachineRecord m1) ? m1 : null;
            bool haulerOk = hauler != null && hauler.IsAlive && hauler.ChassisId == HomeValleyLayout.Erc002ChassisId && hauler.BlueprintId == HomeValleyLayout.BlueprintHaulerId
                            && hauler.RegionId == HomeValleyLayout.RegionId && hauler.FactionId == "Player" && Math.Abs(hauler.MaxHealth - SoftlockService.PrintHealth) < 0.01f;
            NotificationEntry printNote = NotificationCenter.History.FirstOrDefault(e => e.Type?.Id == "emergency_rescue");
            string caption = printNote?.Text ?? string.Empty;
            bool cueOk = FeedbackCues.CountOf(FeedbackCueId.EmergencyPrint) == 1;
            bool noteOk = NotifyCount("emergency_rescue") == 1 && caption.Contains("核心应急") && caption.Contains("少于 2 台") && printNote.Latest != null && printNote.Latest.HasLocation;
            bool hookOk = GameSettings.HasSeenGuidanceHook(GuidanceHooks.SoftlockFirstPrint);
            bool codexOk = MechanicCodex.IsUnlocked("codex.economy.softlock");
            bool feedback = cueOk && noteOk && hookOk && codexOk;
            bool scrapping = St(s).CoreScrapActive && NotifyCount("core_emergency_scrap") == 1 && AnyNotify("core_emergency_scrap", "每分钟产出 1 废料")
                             && GameSettings.HasSeenGuidanceHook(GuidanceHooks.SoftlockFirstCoreScrap);
            Expect(deadBefore == 0 && printed && haulerOk && feedback && scrapping && St(s).LastPrintDay == day1 && s.Scrap == 0,
                $"B1 清空所有机器和废料（FGT-ECO-005）：1 游戏秒内归还核心免费打印 {(hauler == null ? "?" : MachineNaming.Short(hauler))}（搬运机 ERC-002 / bp_hauler，第 {day1} 天），" +
                $"声音 + 字幕 + 可定位通知“{caption}”+ 引导钩子 + 图鉴（{cueOk}/{noteOk}/{hookOk}/{codexOk}）；同时进入核心应急产废料（通知一次，{scrapping}）");
            Seconds(61f);
            int afterMinute = s.Scrap;
            long statsScrap = ProductionStats.RateOf(s, 0, ItemCatalog.Find(ItemCatalog.ScrapId)).ProducedPerMinute > 0 ? 1 : 0;
            Seconds(5f);
            bool noSecondToday = St(s).PrintCount == 1 && Alive() == 1;
            Expect(afterMinute == 1 && St(s).CoreScrapProduced == 1 && statsScrap == 1 && noSecondToday,
                $"B2 应急产废料每游戏分钟 1 件（61 秒后废料 {afterMinute}，进生产统计）；家园只有 1 台但今天已打印过，不再打印（FGR-ECO-070“每个游戏日一台”）");
            // 跨到第二天：再打印一台（2 台后不再打印）。
            ToNextDay(2f);
            int day2 = GameClock.DayOf(GameClock.GameSeconds);
            bool second = St(s).PrintCount == 2 && Alive() == 2 && St(s).LastPrintDay == day2 && day2 == day1 + 1;
            // 应急产废料一直攒到回收站造价为止（真实世界步）。
            int target = SoftlockService.CoreScrapTarget;
            bool reached = StepUntil(() => s.Scrap >= target, target * 60 + 120);
            Seconds(2f);
            bool stopped = !St(s).CoreScrapActive && AnyNotify("core_emergency_scrap", "已攒够造一座回收站的废料");
            Seconds(130f);
            Expect(second && reached && stopped && s.Scrap == target && St(s).CoreScrapProduced == target,
                $"B3 第二天（第 {day2} 天）再打印一台、够 2 台；应急产废料攒到 {s.Scrap} = 回收站造价 {target} 为止（通知“已攒够”），之后 130 秒不再产" +
                $"（打印 {St(s).PrintCount}、机器 {Alive()}、到达 {reached}、结束 {stopped}、累计产 {St(s).CoreScrapProduced}）");
            ToNextDay(5f);
            bool noThird = St(s).PrintCount == 2;
            // 玩家用真实建造菜单在废墟上放回收站 → 两台打印出来的搬运机取料施工 → 回收站开工 → 应急产废料结束（恢复路径）。
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            BuildingRecord ghost = null;
            try
            {
                mode.Open();
                mode.Select("recycler");
                mode.PointerDown(s, ruin.Value);
                mode.PointerUp(s, ruin.Value);
                ghost = HomeGridService.BuildingAt(s, ruin.Value);
                mode.ClearSelection();
            }
            finally
            {
                mode.Close();
            }
            bool placed = ghost != null && ghost.BuildingTypeId == "recycler";
            bool built = placed && StepUntil(() => ghost.ConstructionState == BuildingConstructionState.Operational, 600);
            ProductionService.Producer rp = placed && ProductionService.TryGet(s, ghost.BuildingId, out ProductionService.Producer pr) ? pr : null;
            bool working = built && StepUntil(() => rp != null && (rp.State == ProdState.Working || rp.Rec.RuinRecovered > 0), 60);
            Seconds(3f);
            bool ended = !St(s).CoreScrapActive;
            Expect(noThird && placed && built && working && ended && rp.Rec.RuinRecovered > 0,
                $"B4 第三天机器已够 2 台，不再打印；真实建造菜单在废墟上放回收站 → 打印出来的搬运机取料施工建成 → 回收站开工（已拆出 {rp?.Rec.RuinRecovered} 废料），" +
                $"施工把废料用完后核心又短暂应急产废料、回收站一工作就结束（应急产废料 {(St(s).CoreScrapActive ? "仍在" : "已结束")}）——从零恢复到正常产线");
        }

        // ── C 负向：应急打印 ─────────────────────────────────────────────────────────

        private static void CheckPrintNegatives()
        {
            // 1) 开局两台：不打印。
            CampaignState s = NewWorld(10201, scrap: 0);
            Seconds(3f);
            bool none = St(s).PrintCount == 0 && Alive() == 2 && St(s).StartMachinesSeeded;
            // 2) 只剩一台（不是全灭）：也打印（家园机器少于 2 台）。
            MachineRecord one = MachineRegistry.AllRecords.First(m => m.IsAlive);
            MachineRegistry.ApplyDamage(one.LogicId, 99999f);
            bool printedOne = StepUntil(() => St(s).PrintCount == 1, 3) && Alive() == 2;
            Expect(none && printedOne, "C1 开局两台机器：不打印；阵亡一台、只剩一台（少于 2 台）时当天打印一台补到 2 台");

            // 3) 核心被毁：家园冻结，不打印、不产废料。
            CampaignState d = NewWorld(10202, scrap: 0);
            HomeValleySoftlockGuard.DebugDestroyCore(d);
            KillAllMachines();
            Seconds(70f);
            Expect(St(d).PrintCount == 0 && d.Scrap == 0 && !St(d).CoreScrapActive && St(d).CoreScrapProduced == 0,
                "C2 负向：核心被毁（终态）后不打印、不产废料（家园冻结，只剩失败页；AC-ECO-011“核心被毁则不能触发”）");

            // 4) 出生锚点被建筑占了：打印在最近一格机器能站的地方（不卡在建筑里）。
            CampaignState b = NewWorld(10203, scrap: 300);
            Vector2 anchor = HomeValleyLayout.Erc002Spawn.Position;
            GridCell ac = GameLogic.Campaign.Nav.NavService.CellOf(anchor.x, anchor.y);
            BuildingRecord blocker = null;
            for (int r = 0; r <= 3 && blocker == null; r++)
            {
                foreach (GridCell c in new[] { At(ac, 0, 0), At(ac, -r, 0), At(ac, 0, -r), At(ac, -r, -r) })
                {
                    GridCell pivot = At(c, 1, 1);
                    if (HomeGridService.ValidatePlacement(b, HomeValleyLayout.BuildingTypeGenerator2, pivot, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        blocker = FgProductionSelfCheck.Built(b, HomeValleyLayout.BuildingTypeGenerator2, "spawnblock", pivot);
                        break;
                    }
                }
            }
            KillAllMachines();
            bool printed = StepUntil(() => St(b).PrintCount == 1, 3);
            Vector2 at = MachineRegistry.TryGetRecord(St(b).LastPrintLogicId, out MachineRecord pm) ? pm.WorldPosition : anchor;
            GridCell pc = GameLogic.Campaign.Nav.NavService.CellOf(at.x, at.y);
            bool standable = GameLogic.Campaign.Nav.NavService.Kernel.Passable(pc.X, pc.Y, BinGames.Sim.Nav.NavConst.ClassPlayer) && HomeGridService.BuildingAt(b, pc) == null;
            Expect(printed && standable && (blocker == null || HomeGridService.BuildingAt(b, ac) == null || pc.X != ac.X || pc.Y != ac.Y),
                $"C3 出生锚点（{ac.X}, {ac.Y}）{(blocker != null ? "被发电机 2 占住" : "（附近放不下发电机，只验证能站）")}：打印在 ({pc.X}, {pc.Y})，那一格机器能站、没有建筑");

            // 5) Demo 紧急救援机的分层：今天的应急打印没用时不派救援机；用掉之后又全灭（废料不够造新的）才派救援机。
            CampaignState r2 = NewWorld(10204, scrap: 0);
            KillAllMachines();
            Seconds(3f);
            int rescueAfterPrint = MachineRegistry.AllRecords.Count(m => m.IsAlive && m.ChassisId == HomeValleyLayout.ErcRescueChassisId);
            KillAllMachines();
            Seconds(3f);
            int rescueLater = MachineRegistry.AllRecords.Count(m => m.IsAlive && m.ChassisId == HomeValleyLayout.ErcRescueChassisId);
            Expect(St(r2).PrintCount == 1 && rescueAfterPrint == 0 && rescueLater == 1 && SoftlockService.CountHomeMachines() == 0,
                $"C4 与 ER3-SOFTLOCK-01 分层：先打印正式搬运机、不同时派紧急救援机（{rescueAfterPrint}）；当天的打印机也阵亡、废料不够时才派只会维修的紧急救援机（{rescueLater}，不计入家园机器）");

            // 6) B10：开局机器全灭后存档、读档——不再免费补发开局的 ERC-001 / 搬运机（此前每次进家园都补，存读档就能刷机器）。
            CampaignState l = NewWorld(10205, scrap: 300);
            St(l).LastPrintDay = GameClock.DayOf(GameClock.GameSeconds); // 今天的应急打印已用掉：只看读档补不补机器
            KillAllMachines();
            Seconds(1f);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            int aliveAfterLoad = -1;
            if (rr.Success)
            {
                CampaignSession.Set(Slot, rr.State);
                WorldSimulation.LoadHome(resume: true);
                aliveAfterLoad = SoftlockService.CountHomeMachines();
            }
            Expect(save.Success && rr.Success && aliveAfterLoad == 0 && rr.State.Economy.Softlock.StartMachinesSeeded,
                $"C5 负向（B10）：开局机器全灭后存读档，读回来家园机器仍是 {aliveAfterLoad} 台——不再借“进家园补开局机器”刷机器，补机器只走每日应急打印");

            // 7) 审查修复 P1：2 台只会充电的战斗履带（ERC-003）活着、搬运机全灭、发电机受损、废料 0——此前“够 2 台”永不打印、救援机又被“今天还没打印”挡住，永久卡死。
            CampaignState c3 = NewWorld(10206, scrap: 0);
            Vector2 near = HomeValleyLayout.Erc002Spawn.Position;
            KillAllMachines();
            for (int i = 0; i < 2; i++)
            {
                MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId,
                    near + new Vector2(2f + i, 2f), 120f, 120f);
            }
            BuildingRecord gen = c3.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator);
            if (gen != null)
            {
                gen.ConstructionState = BuildingConstructionState.Damaged;
            }
            int combatAlive = MachineRegistry.AllRecords.Count(m => m.IsAlive && m.ChassisId == HomeValleyLayout.Erc003ChassisId);
            bool printedForCombat = StepUntil(() => St(c3).PrintCount == 1, 3);
            MachineRecord builder = MachineRegistry.TryGetRecord(St(c3).LastPrintLogicId, out MachineRecord bm) ? bm : null;
            bool canBuild = builder != null && builder.IsAlive && HomeValleyWorkOrders.CanDoKind(builder.ChassisId, WorkOrderKind.Build)
                            && HomeValleyWorkOrders.CanDoKind(builder.ChassisId, WorkOrderKind.Repair);
            bool scrapping = StepUntil(() => St(c3).CoreScrapActive, 3);
            int rescueC3 = MachineRegistry.AllRecords.Count(m => m.IsAlive && m.ChassisId == HomeValleyLayout.ErcRescueChassisId);
            Expect(combatAlive == 2 && gen != null && printedForCombat && canBuild && scrapping && Alive() == 1 && rescueC3 == 0,
                $"C8 负向（P1 回归）：2 台战斗履带活着（{combatAlive}）、搬运机全灭、发电机受损、废料 0——战斗履带不会施工 / 维修，不算家园机器（计 {Alive()} 台），" +
                "当天照样打印一台能施工、能维修的搬运机，核心开始应急产废料（有了恢复路径）；不重复派救援机");

            // 8) 审查修复 P1：今天的打印还没用，但能施工的机器都在远征（够 2 台、不在家）、家里发电机受损、废料 0——不打印也不再干等，照旧派 Demo 紧急救援机去修发电机。
            CampaignState aw = NewWorld(10207, scrap: 0);
            foreach (MachineRecord m in MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive).ToArray())
            {
                MachineRegistry.MoveToRegion(m, "fg-softlock-away");
            }
            BuildingRecord gen2 = aw.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator);
            if (gen2 != null)
            {
                gen2.ConstructionState = BuildingConstructionState.Damaged;
            }
            bool pending = SoftlockService.PrintPending(aw);
            Seconds(3f);
            MachineRecord rescueAw = MachineRegistry.AllRecords.FirstOrDefault(m => m.IsAlive && m.ChassisId == HomeValleyLayout.ErcRescueChassisId);
            bool rescueRepairs = rescueAw != null && (aw.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Any(o =>
                o.Kind == WorkOrderKind.Repair && o.AssignedMachineLogicId == rescueAw.LogicId);
            Expect(gen2 != null && !pending && St(aw).PrintCount == 0 && Alive() == 2 && rescueAw != null && rescueRepairs,
                $"C9 负向（P1）：能施工的机器都在远征（计 {Alive()} 台、不打印），家里没人修——Demo 紧急救援机不再被“今天还没打印”挡住，照样派出并去修发电机（{(rescueRepairs ? "已接修复单" : "未接单")}）");
        }

        // ── C' 负向：应急产废料 ─────────────────────────────────────────────────────

        private static void CheckCoreScrapNegatives()
        {
            // 1) 废料 > 0：不产。
            CampaignState s = NewWorld(10301, scrap: 1);
            Seconds(70f);
            bool noWhenHave = !St(s).CoreScrapActive && St(s).CoreScrapProduced == 0 && s.Scrap == 1;
            // 2) 废料为 0、但有回收站在工作：不产。
            CampaignState w = NewWorld(9411, scrap: 0); // 与 FgProductionSelfCheck C1 同一种子：起始区 4×4 废墟在电网覆盖里
            FgProductionSelfCheck.PowerUp(w);
            GridCell? ruin = FindRuinSquare(w, 30);
            if (ruin == null)
            {
                Fail("C' 找不到 4×4 废墟");
                return;
            }
            BuildingRecord rec = FgProductionSelfCheck.Built(w, "recycler", "cs_rc", ruin.Value);
            ProductionService.Producer p = FgProductionSelfCheck.P(w, rec);
            bool recyclerWorking = StepUntil(() => ProductionService.AnyRecyclerWorking(w), 10);
            string recyclerState = $"{p.State}/{rec.PowerState}/{p.Rec.Running}";
            Seconds(40f);
            bool noWhenRecycler = recyclerWorking && St(w).CoreScrapProduced == 0;
            // 3) 回收站输出堵塞（没接传送带，废料送不回家）：不算“能工作”，开始应急产废料。
            bool blocked = StepUntil(() => p.State == ProdState.OutputBlocked && !p.Rec.Running, 120);
            Seconds(2f);
            bool startsWhenBlocked = blocked && !ProductionService.AnyRecyclerWorking(w) && St(w).CoreScrapActive;
            // 4) 回收站缺电：同样不算。
            rec.ConstructionState = BuildingConstructionState.Disabled;
            Seconds(2f);
            bool disabledNotWorking = !ProductionService.AnyRecyclerWorking(w);
            Expect(noWhenHave && noWhenRecycler && startsWhenBlocked && disabledNotWorking,
                $"C6 负向：废料 > 0 时不产（{noWhenHave}）；有回收站在工作时不产（{recyclerWorking}，{recyclerState}，产 {St(w).CoreScrapProduced}）；回收站输出堵塞（没接传送带）/ 停用时不算“能工作的回收站”，开始应急产废料（{startsWhenBlocked}/{disabledNotWorking}）");
            // 5) 产出被消耗：废料用掉回到 0，接着产（不会因为攒过一次就停）。
            CampaignState c = NewWorld(10303, scrap: 0);
            KillAllMachines();
            Seconds(62f);
            int got = c.Scrap;
            c.Scrap = 0; // 测试捷径：玩家把这 1 件花掉
            Seconds(61f);
            Expect(got == 1 && c.Scrap == 1 && St(c).CoreScrapProduced == 2 && St(c).CoreScrapActive,
                "C7 应急产废料期间废料被花掉：照常每分钟产（攒到回收站造价或有回收站工作才结束），不会产 1 件就停（否则永远造不起回收站，ADR-ECO-010）");
        }

        // ── D 传送带闭环卡死 ─────────────────────────────────────────────────────────

        /// <summary>核心附近 6×6 空地上铺一圈 5×5 的闭环（16 格），每隔一格放一件废料。返回代表格（左下角）。</summary>
        private static GridCell? LayLoop(CampaignState s, out int items, out GridCell origin)
        {
            items = 0;
            origin = default;
            GridCell? o = FgProductionSelfCheck.FindArea(s, 6, 6);
            if (o == null)
            {
                return null;
            }
            origin = o.Value;
            GridCell a = o.Value;
            bool ok = FgProductionSelfCheck.Belts(s, a, BeltDir.East, 4)
                      && FgProductionSelfCheck.Belts(s, At(a, 4, 0), BeltDir.North, 4)
                      && FgProductionSelfCheck.Belts(s, At(a, 4, 4), BeltDir.West, 4)
                      && FgProductionSelfCheck.Belts(s, At(a, 0, 4), BeltDir.South, 4);
            if (!ok)
            {
                return null;
            }
            ushort scrap = BeltItems.ScrapId;
            foreach (GridCell c in new[] { At(a, 1, 0), At(a, 3, 0), At(a, 4, 2), At(a, 2, 4), At(a, 0, 2) })
            {
                if (BeltNetworkService.Kernel.InsertItem(c.X, c.Y, scrap) == BeltResult.Ok)
                {
                    items++;
                }
            }
            return a;
        }

        private static void CheckLoopDeadlock()
        {
            CampaignState s = NewWorld(10401, scrap: 300);
            GridCell? key = LayLoop(s, out int items, out GridCell o);
            if (key == null || items == 0)
            {
                Fail("D 找不到 6×6 空地 / 铺不了闭环");
                return;
            }
            // 非闭环对照：一条 4 格死路，末端堆满。
            GridCell? lineAt = FgProductionSelfCheck.FindArea(s, 5, 1, 12f, 30f);
            bool lineOk = lineAt != null && FgProductionSelfCheck.Belts(s, lineAt.Value, BeltDir.East, 4);
            if (lineOk)
            {
                BeltNetworkService.Kernel.InsertItem(lineAt.Value.X, lineAt.Value.Y, BeltItems.ScrapId);
            }
            Seconds(6f);
            SoftlockState st = St(s);
            bool watched = st.Loops.Length == 1 && st.Loops[0].X == key.Value.X && st.Loops[0].Y == key.Value.Y && !st.Loops[0].Alerted;
            Seconds(60f);
            bool notYet = !st.Loops[0].Alerted && NotifyCount("belt_loop_stall") == 0;
            bool alerted = StepUntil(() => St(s).Loops.Length == 1 && St(s).Loops[0].Alerted, 80);
            List<HomeValleyAlarms.AlertRecord> alarms = HomeValleyAlarms.Collect(s);
            string loopKey = "loop:" + key.Value.X.ToString(CultureInfo.InvariantCulture) + "," + key.Value.Y.ToString(CultureInfo.InvariantCulture);
            HomeValleyAlarms.AlertRecord alarm = alarms.FirstOrDefault(a => a.Key == loopKey);
            bool alarmOk = alarm.Key == loopKey && alarm.Severity == HomeValleyAlarms.Severity.FactoryJammed && alarm.HasLocation && alarm.Message.Contains("卡死")
                           && alarm.RowText.Contains("[") && !GameText.ContainsMarker(alarm.RowText);
            bool notified = NotifyCount("belt_loop_stall") == 1 && AnyNotify("belt_loop_stall", $"（{key.Value.X}, {key.Value.Y}）") && AnyNotify("belt_loop_stall", "清带")
                            && GameSettings.HasSeenGuidanceHook(GuidanceHooks.SoftlockFirstLoopStall);
            Seconds(30f);
            bool once = NotifyCount("belt_loop_stall") == 1;
            Expect(watched && notYet && alerted && alarmOk && notified && once && St(s).Loops.Length == 1,
                $"D1 传送带闭环（16 格、{items} 件废料、没有出口）：盯上（代表格 ({key.Value.X}, {key.Value.Y})）；66 秒时还不报；物品 {SoftlockService.LoopStallTicks / GameClock.StepHz} 秒既不减少也没被取走 → " +
                $"警告一次（可定位、写明怎么办、钩子）、告警栏“{alarm.RowText}”；之后不重复刷；旁边一条末端堆满的直线不是闭环，不报");
            // 清带（玩家工具）：环上空了 → 有进展，告警消失。
            BeltClearPlan plan = BeltClearService.PlanNetwork(s, key.Value);
            bool cleared = BeltClearService.Execute(s, plan, true, out _);
            Seconds(6f);
            bool gone = !HomeValleyAlarms.Collect(s).Any(a => a.Key == loopKey) && St(s).Loops.Length == 1 && !St(s).Loops[0].Alerted;
            Expect(cleared && gone, "D2 恢复：用清带工具清空闭环 → 下一次检查（5 秒内）告警从告警栏消失、计时重新开始");
            // 物品在减少 = 有进展：放回物品，过半段时间拿走一件，计时从那一刻重新开始。
            BeltNetworkService.Kernel.InsertItem(At(o, 1, 0).X, At(o, 1, 0).Y, BeltItems.ScrapId);
            BeltNetworkService.Kernel.InsertItem(At(o, 4, 2).X, At(o, 4, 2).Y, BeltItems.ScrapId);
            Seconds(70f);
            long since0 = St(s).Loops[0].SinceTick;
            // 拿走一件：整圈清空后马上放回一件（2 → 1，少于这一段见过的最少件数）。
            BeltClearService.Execute(s, BeltClearService.PlanNetwork(s, key.Value), true, out _);
            BeltNetworkService.Kernel.InsertItem(At(o, 2, 4).X, At(o, 2, 4).Y, BeltItems.ScrapId);
            Seconds(6f);
            long since1 = St(s).Loops[0].SinceTick;
            Seconds(70f);
            bool reset = since1 > since0 && !St(s).Loops[0].Alerted;
            // 拆掉一格：不再是环，记录清掉。
            bool removed = BeltNetworkService.TryRemove(s, At(o, 2, 4), null).Ok;
            Seconds(6f);
            Expect(reset && removed && St(s).Loops.Length == 0 && !HomeValleyAlarms.Collect(s).Any(a => a.Key.StartsWith("loop:", StringComparison.Ordinal)),
                $"D3 环上物品减少（被拿走一件）= 有进展：计时从那一刻重新开始（{since0} → {since1}），又 70 秒不报；拆掉环上一格（不再是闭环）→ 记录清掉、不报（{removed}，剩 {St(s).Loops.Length}）");

            // 审查修复 P2：已告警的卡死环所在网络上接一段坐标更小的喂料支线——代表格不跳到支线上，计时与已告警标记接着用，告警栏不闪、不重复通知。
            CampaignState f = NewWorld(10402, scrap: 300);
            GridCell? area = FgProductionSelfCheck.FindArea(f, 6, 7);
            if (area == null)
            {
                Fail("D4 找不到 6×7 空地");
                return;
            }
            GridCell la = At(area.Value, 0, 1); // 环整体上移一行，下面留一行给支线
            bool laid = FgProductionSelfCheck.Belts(f, la, BeltDir.East, 4)
                        && FgProductionSelfCheck.Belts(f, At(la, 4, 0), BeltDir.North, 4)
                        && FgProductionSelfCheck.Belts(f, At(la, 4, 4), BeltDir.West, 4)
                        && FgProductionSelfCheck.Belts(f, At(la, 0, 4), BeltDir.South, 4);
            foreach (GridCell c in new[] { At(la, 1, 0), At(la, 3, 0), At(la, 4, 2), At(la, 2, 4), At(la, 0, 2) })
            {
                BeltNetworkService.Kernel.InsertItem(c.X, c.Y, BeltItems.ScrapId);
            }
            bool alertedF = laid && StepUntil(() => St(f).Loops.Length == 1 && St(f).Loops[0].Alerted, 200);
            int notesBeforeSpur = NotifyCount("belt_loop_stall");
            GridCell spur = At(area.Value, 1, 0);
            bool spurOk = FgProductionSelfCheck.Belts(f, spur, BeltDir.North, 1);
            Seconds(6f);
            bool sameNet = BeltNetworkService.Kernel.NetworkOf(spur.X, spur.Y) >= 0
                           && BeltNetworkService.Kernel.NetworkOf(spur.X, spur.Y) == BeltNetworkService.Kernel.NetworkOf(la.X, la.Y);
            bool keptKey = St(f).Loops.Length == 1 && St(f).Loops[0].X == la.X && St(f).Loops[0].Y == la.Y && St(f).Loops[0].Alerted;
            string fKey = "loop:" + la.X.ToString(CultureInfo.InvariantCulture) + "," + la.Y.ToString(CultureInfo.InvariantCulture);
            bool alarmKept = HomeValleyAlarms.Collect(f).Any(x => x.Key == fKey);
            Seconds(130f);
            bool noRepeat = NotifyCount("belt_loop_stall") == notesBeforeSpur && notesBeforeSpur == 1;
            Expect(alertedF && spurOk && sameNet && keptKey && alarmKept && noRepeat,
                $"D4 已告警的卡死环接上一段坐标更小的喂料支线（({spur.X}, {spur.Y})，并入同一网络 {sameNet}）：代表格仍是环上的 ({la.X}, {la.Y})（{keptKey}），" +
                $"告警栏不闪（{alarmKept}），130 秒后也不重复通知（通知 {notesBeforeSpur} → {NotifyCount("belt_loop_stall")}）");
        }

        // ── E 施工 / 维修目标到不了 ─────────────────────────────────────────────────

        /// <summary>用真实建造菜单放一座电塔虚影（施工单进待分配池）。</summary>
        private static BuildingRecord PlaceGhost(CampaignState s, GridCell cell, out string status)
        {
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            try
            {
                mode.Open();
                mode.Select("power_pole");
                mode.PointerDown(s, cell);
                mode.PointerUp(s, cell);
                status = mode.StatusText;
                mode.ClearSelection();
            }
            finally
            {
                mode.Close();
            }
            return HomeGridService.BuildingAt(s, cell);
        }

        /// <summary>以 <paramref name="center"/> 为中心、半径 <paramref name="r"/> 的一圈电塔（测试捷径：直接登记建成）。</summary>
        private static int Wall(CampaignState s, GridCell center, int r, string key)
        {
            int n = 0;
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                    {
                        continue;
                    }
                    FgProductionSelfCheck.Built(s, "power_pole", key + n, At(center, dx, dy));
                    n++;
                }
            }
            return n;
        }

        private static void CheckUnreachableSite()
        {
            CampaignState s = NewWorld(10501, scrap: 400);
            GridCell? area = FgProductionSelfCheck.FindArea(s, 7, 7, 10f, 24f);
            GridCell? open = FgProductionSelfCheck.FindArea(s, 3, 3, 14f, 30f);
            if (area == null || open == null)
            {
                Fail("E 找不到空地");
                return;
            }
            // 一圈电塔围出一块 3×3 的封闭空地（离施工点两格），施工点在正中：机器真去施工、寻路说到不了，检测确认“从核心和家园机器都走不到”。
            GridCell site = At(area.Value, 3, 3);
            int walls = Wall(s, site, 2, "pocket");
            Seconds(1f);
            BuildingRecord ghost = PlaceGhost(s, site, out string status);
            BuildingRecord ghostOk = PlaceGhost(s, At(open.Value, 1, 1), out _);
            if (ghost == null || ghostOk == null)
            {
                Fail($"E1 电塔虚影没放下（建造模式：“{status}”）");
                return;
            }
            bool alerted = StepUntil(() => St(s).Sites.Any(x => x.BuildingId == ghost.BuildingId && x.Alerted), 120);
            ReachWatchRecord w = St(s).Sites.FirstOrDefault(x => x.BuildingId == ghost.BuildingId);
            WorkOrderRecord order = HomeValleyWorkOrders.FindActiveBuild(s, ghost.BuildingId);
            HomeValleyAlarms.AlertRecord alarm = HomeValleyAlarms.Collect(s).FirstOrDefault(a => a.Key == "reach:" + ghost.BuildingId);
            bool alarmOk = alarm.Key == "reach:" + ghost.BuildingId && alarm.Severity == HomeValleyAlarms.Severity.WorkBlocked && alarm.BuildingId == ghost.BuildingId
                           && alarm.HasLocation && alarm.Message.Contains("机器无法到达") && alarm.Message.Contains("施工");
            bool notified = NotifyCount("site_unreachable") == 1 && AnyNotify("site_unreachable", "走不到它") && AnyNotify("site_unreachable", "施工永远无法完成")
                            && GameSettings.HasSeenGuidanceHook(GuidanceHooks.SoftlockFirstUnreachable);
            bool controlOk = ghostOk.ConstructionState == BuildingConstructionState.Operational && !St(s).Sites.Any(x => x.BuildingId == ghostOk.BuildingId);
            Seconds(30f);
            bool once = NotifyCount("site_unreachable") == 1;
            Expect(walls == 16 && alerted && w != null && !w.Enclosed && order != null && order.UnreachableNotified && alarmOk && notified && controlOk && once,
                $"E1 施工点被一圈电塔围在一块封闭空地里（真实建造菜单放的虚影 + 施工单）：机器真去、寻路说到不了（{order?.FailureReason}）→ 检测确认从核心和家园机器都走不到 → " +
                $"连续 {SoftlockService.UnreachableTicks / GameClock.StepHz} 秒后警告一次（可定位，“施工永远无法完成”+ 原因 + 怎么办，钩子）、告警栏“{alarm.RowText}”，之后不重复；" +
                $"圈外的那座照常建成、不报（{alerted}/{w?.Enclosed}/{alarmOk}/{notified}/{controlOk}/{once}）");
            // 恢复：拆掉一面的电塔（真实拆除工单）→ 能走到了 → 告警消失，施工照常完成。
            BuildingRecord wall = HomeGridService.BuildingAt(s, At(site, 0, -2));
            GridOpResult demo = PlanHistory.ToggleDemolish(s, wall.BuildingId);
            bool wallGone = StepUntil(() => HomeGridService.FindBuilding(s, wall.BuildingId) == null, 240);
            bool cleared = StepUntil(() => !St(s).Sites.Any(x => x.BuildingId == ghost.BuildingId) && !HomeValleyAlarms.Collect(s).Any(a => a.Key == "reach:" + ghost.BuildingId), 15);
            bool built = StepUntil(() => ghost.ConstructionState == BuildingConstructionState.Operational, 300);
            Expect(demo.Success && wallGone && cleared && built,
                $"E2 恢复：拆掉挡路的一座电塔（真实拆除工单）→ 下一次检查能走到，记录与告警消失 → 机器重试时照常把它建完（{wallGone}/{cleared}/{built}）");

            // 负向：贴身一圈建筑——机器从旁边仍能施工（以真实工单结果为准）：照常建成，不报。
            CampaignState t = NewWorld(10502, scrap: 400);
            GridCell? a2 = FgProductionSelfCheck.FindArea(t, 5, 5, 10f, 24f);
            if (a2 == null)
            {
                Fail("E3 找不到空地");
                return;
            }
            GridCell tight = At(a2.Value, 2, 2);
            Wall(t, tight, 1, "tight");
            Seconds(1f);
            BuildingRecord tg = PlaceGhost(t, tight, out _);
            bool tightBuilt = tg != null && StepUntil(() => tg.ConstructionState == BuildingConstructionState.Operational, 120);
            Expect(tightBuilt && St(t).Sites.Length == 0 && NotifyCount("site_unreachable") == 0,
                "E3 负向：施工点四周贴着一圈建筑、但机器从旁边仍能施工 → 照常建成，不报（检测以机器真实去过、寻路说到不了为前提，不凭格子误报）");

            // 维修单的目标显示“维修”（施工 / 维修两种都查）。
            BuildingRecord damaged = HomeGridService.BuildingAt(t, At(tight, 1, 1));
            HomeValleyWorkOrders.WorkOrderOpResult rep = HomeValleyWorkOrders.TryCreateRepairPool(t, damaged.BuildingId, null, 0, 5f);
            string kind = SoftlockService.SiteKindText(t, damaged.BuildingId);
            Expect(rep.Success && kind == "维修", $"E4 维修单的目标在警告与告警栏里写“{kind}”（施工 / 维修两种工单都检查）");
        }

        // ── F FGR-ECO-071 任何东西都有去处 ─────────────────────────────────────────────

        private static void CheckAnySolidToRecycler()
        {
            CampaignState s = NewWorld(10601, scrap: 300);
            FgProductionSelfCheck.PowerUp(s);
            GridCell? o = FgProductionSelfCheck.FindArea(s, 10, 4);
            if (o == null)
            {
                Fail("F1 找不到 10×4 的空地");
                return;
            }
            // 回收站枢轴 r（占地 r−1～r+2）；脚下没有废墟（测试捷径：直接登记建成），只分解送来的东西。
            GridCell r = At(o.Value, 4, 1);
            BuildingRecord rec = FgProductionSelfCheck.Built(s, "recycler", "anysolid", r);
            ProductionService.Producer p = FgProductionSelfCheck.P(s, rec);
            GridCell outBelt = At(r, 3, 0);
            bool laid = FgProductionSelfCheck.Belts(s, outBelt, BeltDir.East, 2) && FgProductionSelfCheck.Belts(s, At(r, -3, 0), BeltDir.East, 2);
            int sink = FgProductionSelfCheck.TestSink(s, At(outBelt, 2, 0));
            FgProductionSelfCheck.Resync(s);
            var solids = ItemCatalog.Items.Where(d => d.Form == ItemForm.Solid && d.BeltId != 0).ToList();
            var bad = new List<string>();
            long expected = 0;
            foreach (ItemDef item in solids)
            {
                long before = p.Rec.ItemsRecycled;
                BeltResult put = BeltNetworkService.Kernel.InsertItem(At(r, -3, 0).X, At(r, -3, 0).Y, item.BeltId);
                bool done = put == BeltResult.Ok && StepUntil(() => p.Rec.ItemsRecycled == before + 1 && !p.Rec.Running, 30);
                if (!done)
                {
                    bad.Add(item.Id);
                }
                expected += Math.Max(1, item.RecycleScrap);
            }
            Seconds(8f);
            BeltNetworkService.Kernel.TryGetPortInfo(sink, out BeltPortInfo si);
            long delivered = si.Consumed + si.Buffered + FgProductionSelfCheck.OnBelts(outBelt, BeltDir.East, 2) + FgProductionSelfCheck.PortPending(s, rec, "recycler.out0")
                             + ProductionService.Count(p.Rec.Out, ItemCatalog.ScrapId);
            Expect(laid && solids.Count >= 10 && bad.Count == 0 && p.Rec.ItemsRecycled == solids.Count && delivered == expected,
                $"F1 FGR-ECO-071“任何固体都可以送进回收站分解为废料”：物品表里全部 {solids.Count} 种上传送带的固体逐一用传送带送进回收站，每一种都被收下、分解（{p.Rec.ItemsRecycled} 件），" +
                $"得到的废料 {delivered} = 各自回收产出之和 {expected}" + (bad.Count == 0 ? string.Empty : $"（没被收下：{string.Join("、", bad)}）"));
        }

        private static void CheckAnyFluidToWastePond()
        {
            CampaignState s = NewWorld(10602, scrap: 300);
            FgProductionSelfCheck.PowerUp(s);
            GridCell? o = FgProductionSelfCheck.FindArea(s, 8, 5);
            if (o == null)
            {
                Fail("F2 找不到 8×5 的空地");
                return;
            }
            BuildingRecord pond = FgProductionSelfCheck.Built(s, "waste_pond", "anyfluid", At(o.Value, 5, 2));
            ProductionService.Producer pp = FgProductionSelfCheck.P(s, pond);
            GridCell pipeCell = pp.Fluids[0].PipeCell;
            GridCell feed = At(pipeCell, -1, 0);
            bool laid = PipeNetworkService.TryPlace(s, pipeCell, PipePieceKind.Pipe, 0, 0).Ok && PipeNetworkService.TryPlace(s, feed, PipePieceKind.Pipe, 0, 0).Ok;
            FgProductionSelfCheck.Resync(s);
            Seconds(2f);
            PipeKernel k = PipeNetworkService.Kernel;
            int consumer = pp.Rec.FluidHandles[0];
            var fluids = ItemCatalog.Items.Where(d => d.Form == ItemForm.Fluid && d.FluidId > 0).ToList();
            var bad = new List<string>();
            foreach (ItemDef f in fluids)
            {
                k.TryGetConsumer(consumer, out PipeConsumerInfo c0);
                int id = k.AddProducer(feed.X, feed.Y, f.FluidId, 10000, 10000);
                bool gone = id >= 0 && StepUntil(() => k.TryGetProducer(id, out PipeProducerInfo pi) && pi.StockMl == 0, 30);
                k.TryGetConsumer(consumer, out PipeConsumerInfo c1);
                if (!gone || c1.TotalDeliveredMl - c0.TotalDeliveredMl != 10000)
                {
                    bad.Add(f.Id);
                }
                if (id >= 0)
                {
                    k.RemoveProducer(id, out _);
                }
                PipeNetworkService.TryFlush(s, feed, out _, out _);
                Seconds(1f);
            }
            Expect(laid && fluids.Count >= 4 && bad.Count == 0 && pp.Def.Mode == ProducerMode.Waste,
                $"F2 FGR-ECO-071“任何流体都可以送进废液池销毁”：物品表里全部 {fluids.Count} 种流体（{string.Join("、", fluids.Select(x => x.Name))}）逐一经管线送进废液池，每种 10 升都被销毁" +
                (bad.Count == 0 ? string.Empty : $"（没被销毁：{string.Join("、", bad)}）"));
        }

        /// <summary>FGT-ECO-006：精炼塔的副产品酸液没有去处 → 堵塞并给出原因与引导钩子；接上废液池 → 恢复。</summary>
        private static void CheckByproductJourney()
        {
            CampaignState s = NewWorld(10603, scrap: 300);
            FgProductionSelfCheck.PowerUp(s);
            // “第一次副产品堵塞”的引导钩子每个进程只查一次（ProductionService 里的标记）；全量自检里别的段可能先触发过，这里复位后再验证这一次真的发出（测试捷径：反射复位私有标记）。
            System.Reflection.FieldInfo hookedField = typeof(ProductionService).GetField("_byproductHooked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            hookedField?.SetValue(null, false);
            GridCell? o = FgProductionSelfCheck.FindArea(s, 9, 12);
            if (o == null)
            {
                Fail("F3 找不到 9×12 的空地");
                return;
            }
            GridCell t = At(o.Value, 1, 6);
            BuildingRecord tower = FgProductionSelfCheck.Built(s, "refinery_tower", "by_tw", t);
            ProductionService.Producer p = FgProductionSelfCheck.P(s, tower);
            GridCell well = At(t, 0, -5);
            FgProductionSelfCheck.SetTerrain(s, well, "oil");
            bool laid = Pipe(s, well, PipePieceKind.Pump) && Pipe(s, At(t, 0, -4)) && Pipe(s, At(t, 0, -3)) && Pipe(s, At(t, 0, 3)) && Pipe(s, At(t, 0, 4))
                        && Pipe(s, At(t, 0, 5), PipePieceKind.Tank);
            bool blocked = StepUntil(() => p.State == ProdState.OutputBlocked, 120);
            string why = ProductionService.ReasonText(s, p);
            List<HomeValleyAlarms.AlertRecord> before = HomeValleyAlarms.Collect(s);
            bool reason = p.Reason == ProdReason.ByproductNoRoom && why.Contains("副产品酸液无处可去") && why.Contains("废液池") && why.Contains("酸液口没接管线")
                          && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EconomyFirstByproductBlocked) && hookedField != null && (bool)hookedField.GetValue(null);
            // 只有副产品而没有去处：持续堵塞会进告警（工厂堵塞）。
            bool jammed = StepUntil(() => HomeValleyAlarms.Collect(s).Any(a => a.BuildingId == tower.BuildingId && a.Severity == HomeValleyAlarms.Severity.FactoryJammed), 90);
            long completed = p.Rec.Completed;
            bool pondPipes = Pipe(s, At(t, 2, 0)) && Pipe(s, At(t, 3, 0));
            BuildingRecord pond = FgProductionSelfCheck.Built(s, "waste_pond", "by_pond", At(t, 5, 0));
            ProductionService.Producer pp = FgProductionSelfCheck.P(s, pond);
            bool recovered = StepUntil(() => p.Rec.Completed > completed, 60);
            Seconds(30f);
            long consumed = pp.Rec.FluidHandles[0] >= 0 && PipeNetworkService.Kernel.TryGetConsumer(pp.Rec.FluidHandles[0], out PipeConsumerInfo ci) ? ci.TotalDeliveredMl : 0;
            bool alarmGone = StepUntil(() => !HomeValleyAlarms.Collect(s).Any(a => a.BuildingId == tower.BuildingId), 20);
            Expect(laid && blocked && reason && jammed && pondPipes && recovered && p.Rec.Completed > completed && consumed > 0 && alarmGone,
                $"F3 FGT-ECO-006（{laid}/{blocked}/{reason}/{jammed}/{pondPipes}/{recovered}/{alarmGone}）：精炼塔接上油井、酸液口不接 → 做了 {completed} 份后“{ProductionService.StateText(p)}”，原因“{why.Replace("\n", " · ")}”，第一次副产品堵塞的引导钩子（废液池、回收站）埋了；" +
                $"持续堵塞进告警栏；接上废液池 → 恢复工作（又做了 {p.Rec.Completed - completed} 份），废液池销毁了 {consumed / 1000} 升酸液，告警消失");
            // 固体副产品（目前表里只有流体副产品）：原因指向回收站；流体副产品指向废液池。
            ItemCatalog.TryGet("alloy", out ItemDef alloy);
            ItemCatalog.TryGet("acid", out ItemDef acid);
            ProdReason saved = p.Reason;
            ItemDef savedItem = p.ReasonItem;
            int savedPort = p.ReasonPort;
            p.Reason = ProdReason.ByproductNoRoom;
            p.ReasonItem = alloy;
            p.ReasonPort = -1;
            string solid = ProductionService.ReasonText(s, p);
            p.ReasonItem = acid;
            string fluid = ProductionService.ReasonText(s, p);
            p.Reason = saved;
            p.ReasonItem = savedItem;
            p.ReasonPort = savedPort;
            Expect(solid.Contains("回收站") && solid.Contains("任何固体") && fluid.Contains("废液池") && !fluid.Contains("回收站"),
                $"F4 副产品的去处按形态写：固体“{solid.Split('\n')[0]}”；流体“{fluid.Split('\n')[0]}”");
        }

        private static bool Pipe(CampaignState s, GridCell c, PipePieceKind kind = PipePieceKind.Pipe) => PipeNetworkService.TryPlace(s, c, kind, 0, 0).Ok;

        // ── G 顶栏流体存量（DEBT-FG4ECO08-03）────────────────────────────────────────

        private static void CheckFluidStockTopBar()
        {
            CampaignState s = NewWorld(10701, scrap: 300);
            GridCell? o = FgProductionSelfCheck.FindArea(s, 6, 3);
            if (o == null)
            {
                Fail("G 找不到 6×3 的空地");
                return;
            }
            GridCell w = At(o.Value, 0, 1);
            FgProductionSelfCheck.SetTerrain(s, w, "water");
            bool laid = Pipe(s, w, PipePieceKind.Pump) && Pipe(s, At(w, 1, 0)) && Pipe(s, At(w, 2, 0), PipePieceKind.Tank) && Pipe(s, At(w, 3, 0))
                        && Pipe(s, At(w, 4, 0), PipePieceKind.Tank);
            Seconds(20f);
            ItemCatalog.TryGet("water", out ItemDef water);
            long tanks = 0;
            foreach (GridCell c in new[] { At(w, 2, 0), At(w, 4, 0) })
            {
                if (PipeNetworkService.Kernel.TryGetCellInfo(c.X, c.Y, out PipeCellInfo ci))
                {
                    tanks += ci.TankStockMl;
                }
            }
            ResourcePins.TryPin(s, water.Id, null, out _);
            PinnedItemRecord pin = ResourcePins.Pins(s).FirstOrDefault(x => x.ItemId == water.Id);
            ResourcePins.PinView v = ResourcePins.View(s, pin);
            ItemCatalog.TryGet("fuel", out ItemDef fuel);
            long none = PipeNetworkService.FluidStockMl(fuel.FluidId, out int fuelNets);
            Expect(laid && tanks > 0 && v.FluidLiters == tanks / 1000 && v.FluidNetworks == 1 && v.Stock == -1 && none == 0 && fuelNets == 0
                   && GameText.Format("topbar.fluid_stock", v.FluidLiters.ToString("N0", CultureInfo.InvariantCulture)).EndsWith("升"),
                $"G1 关闭 DEBT-FG4ECO08-03：资源顶栏固定的“水”显示储罐存量合计 {v.FluidLiters} 升（两座储罐 {tanks / 1000} 升、1 个网络），不再写“管线”；没有储罐装着的燃油 = 0");
        }

        // ── H 地下管线成对升级（DEBT-FG4ECO04-05）────────────────────────────────────

        private static void CheckUndergroundPipeUpgrade()
        {
            CampaignState s = NewWorld(10801, scrap: 300);
            GridCell? o = FgProductionSelfCheck.FindArea(s, 12, 3);
            if (o == null)
            {
                Fail("H 找不到 12×3 的空地");
                return;
            }
            GridCell a = At(o.Value, 1, 1);
            GridCell b = At(o.Value, 8, 1);
            GridCell lone = At(o.Value, 10, 0);
            bool laid = PipeNetworkService.TryPlace(s, a, PipePieceKind.Underground, 0, (int)BeltDir.East).Ok
                        && PipeNetworkService.TryPlace(s, b, PipePieceKind.Underground, 0, (int)BeltDir.West).Ok
                        && PipeNetworkService.TryPlace(s, lone, PipePieceKind.Underground, 0, (int)BeltDir.North).Ok;
            PipeKernel k = PipeNetworkService.Kernel;
            bool linked = k.TryGetCellInfo(a.X, a.Y, out PipeCellInfo ia) && ia.UndergroundLinked && ia.PartnerX == b.X && ia.PartnerY == b.Y;
            UpgradeBoxPlan plan = UpgradePlanner.Plan(s, At(o.Value, 0, 0), At(o.Value, 11, 2));
            UpgradeGroup g = plan.Groups.FirstOrDefault(x => x.Kind == PlanEntryKind.PipeUnderground);
            bool planned = g != null && g.Pieces == 1 && g.Cells.Count == 2 && g.ToId == "pipe_underground_t2" && plan.Cost == 6 && plan.Refused == 1
                           && plan.FirstRefusal.HasValue && plan.FirstRefusal.Value.Describe().Contains("没配对");
            int scrap0 = s.Scrap;
            string planId = g != null ? UpgradePlanner.PlanGroup(s, g) : null;
            bool done = planId != null && StepUntil(() => k.TryGetCellInfo(a.X, a.Y, out PipeCellInfo x) && x.Tier == 1, 300);
            k.TryGetCellInfo(a.X, a.Y, out PipeCellInfo fa);
            k.TryGetCellInfo(b.X, b.Y, out PipeCellInfo fb);
            k.TryGetCellInfo(lone.X, lone.Y, out PipeCellInfo fl);
            HomeGridMap map = HomeGridService.MapFor(s);
            bool layer = map.GetPipe(a) == PipeNetworkService.LayerValue(PipePieceKind.Underground, 1) && map.GetPipe(b) == PipeNetworkService.LayerValue(PipePieceKind.Underground, 1);
            Expect(laid && linked && planned && done && fa.Tier == 1 && fb.Tier == 1 && fa.UndergroundLinked && fa.PartnerX == b.X && fl.Tier == 0 && layer && scrap0 - s.Scrap == 6,
                $"H1 关闭 DEBT-FG4ECO04-05：框选升级把已配对的一对地下管线 T1 → T2 当一件（差额 2 × 3 = 6 废料），没配对的那一口拒绝（“{plan.FirstRefusal?.Describe()}”）；" +
                $"机器施工后两口都是 T2、仍互相配对、格网层同步；没配对的口不动");
            // 负向：内核拒绝没配对的口 / 跨度装不下的等级。
            PipeResult unpaired = k.SetUndergroundPairTier(lone.X, lone.Y, 1);
            PipeResult wrongKind = k.SetUndergroundPairTier(At(o.Value, 5, 2).X, At(o.Value, 5, 2).Y, 1);
            Expect(unpaired == PipeResult.InvalidArgument && wrongKind == PipeResult.NotFound,
                $"H2 负向：没配对的地下管线口不能改等级（{unpaired}）；格上没有地下管线口时（{wrongKind}）");
        }

        // ── I 真文件存读档 ─────────────────────────────────────────────────────────

        private static void LayStall(CampaignState s)
        {
            KillAllMachines();
            LayLoop(s, out _, out _);
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(10901, scrap: 0);
            LayStall(s);
            Seconds(150.4f);
            string before = Snap(s);
            int notesBefore = NotifyCount("belt_loop_stall") + NotifyCount("emergency_rescue") + NotifyCount("core_emergency_scrap");
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            Seconds(70f);
            string continuous = Snap(s);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("I 读档失败：" + rr.Message);
                return;
            }
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            NotificationCenter.Bind(rr.State);
            string loaded = Snap(rr.State);
            int notesLoaded = NotifyCount("belt_loop_stall") + NotifyCount("emergency_rescue") + NotifyCount("core_emergency_scrap");
            Seconds(70f);
            string after = Snap(rr.State);
            int notesAfter = NotifyCount("belt_loop_stall") + NotifyCount("emergency_rescue") + NotifyCount("core_emergency_scrap");
            SoftlockState st = rr.State.Economy.Softlock;
            Expect(save.Success && loaded == before && st.PrintCount == 1 && st.CoreScrapActive && st.Loops.Length == 1 && st.Loops[0].Alerted,
                "I1 真文件存读档：应急打印的日子与台数、应急产废料的进行状态与零头、闭环卡死的计时与已告警标记逐字段往返一致" + (loaded == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded}"));
            Expect(after == continuous && notesAfter == notesLoaded && notesLoaded == notesBefore,
                $"I2 读档后接着跑 70 游戏秒，与不存档一直跑逐位一致；已告过警的闭环读档后不重复通知（通知 {notesBefore} → {notesLoaded} → {notesAfter}）" +
                (after == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{after}"));
            // 旧存档（没有软锁域）：走与 CampaignSaveService 读档相同的 JsonUtility.FromJson + EnsureAll（审查修复 P2：此前把字段手动设成 null，测不到真实读档——
            // JsonUtility 遇到缺字段会保留初始化器生成的空对象，不是 null）。存档 JSON 里去掉 "Softlock" 键（改成不存在的键名，JsonUtility 忽略未知键）。
            CampaignState legacySrc = NewWorld(10902, scrap: 100);
            WorldSimulation.SyncAllForSave();
            string json = JsonUtility.ToJson(legacySrc);
            const string key = "\"Softlock\":";
            int keyHits = (json.Length - json.Replace(key, string.Empty).Length) / key.Length;
            CampaignState legacy = JsonUtility.FromJson<CampaignState>(json.Replace(key, "\"SoftlockAbsentInLegacySave\":"));
            bool notNullBeforeEnsure = legacy?.Economy?.Softlock != null; // 证实真实读档路径下它不是 null（旧迁移分支走不到的原因）
            CampaignFgStateDomains.EnsureAll(legacy);
            SoftlockState ls = legacy?.Economy?.Softlock;
            bool legacyOk = keyHits == 1 && (legacySrc.MachineRecords?.Length ?? 0) > 0 && ls != null && ls.Loops.Length == 0 && ls.Sites.Length == 0
                            && ls.StartMachinesSeeded && ls.LastPrintDay == 0 && ls.PrintCount == 0;
            // 反例：新战役第一次进家园前（存档里还没有机器记录）——不能误判成旧档，否则开局机器不播种。
            CampaignState fresh = JsonUtility.FromJson<CampaignState>(JsonUtility.ToJson(CampaignState.CreateNew("fg-softlock-fresh", "Standard", 50)).Replace(key, "\"SoftlockAbsentInLegacySave\":"));
            CampaignFgStateDomains.EnsureAll(fresh);
            bool freshOk = fresh.Economy.Softlock != null && !fresh.Economy.Softlock.StartMachinesSeeded;
            Expect(legacyOk && freshOk,
                $"I3 旧存档没有软锁字段（真实 JsonUtility 反序列化，读入时该字段{(notNullBeforeEnsure ? "是初始化器的空对象" : "为 null")}）：补空域；已有机器记录 = 开局机器视为已播种（读档不补发）；" +
                $"新战役还没有机器记录时不误判（键命中 {keyHits} 次，旧档 {legacyOk}，新战役 {freshOk}）");
        }

        // ── J 暂停与倍速、K 观察一致 ──────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe, scrap: 0);
            pausedHeld = true;
            LayStall(s);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Snap(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Snap(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 130;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 600)
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
            bool same = true;
            bool paused = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(11001, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                same &= snap == reference;
            }
            Expect(paused && same && reference.Contains("prints:1") && reference.Contains("cs:True") && reference.Contains("scrap:2") && reference.Contains("/True"),
                "J1 暂停中（120 帧）不打印、不产废料、闭环不计时；0.5x / 1x / 2x / 3x 跑同样的 130 游戏秒：打印、应急产废料（2 件）、闭环卡死告警逐位一致（按世界步序号）"
                + $"\n{reference}");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(11101, true, 1f, false, out _);
            string unseen = RunScenario(11101, false, 1f, false, out _);
            Expect(seen == unseen, "K1 观察与不观察家园时，应急打印、应急产废料、闭环卡死检测跑 130 游戏秒逐字段一致（FGR-BASE-021）" + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── L 性能 ─────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(11201, scrap: 300);
            // 规模：40 个闭环（每个 16 格）+ 一条 600 格长带（网络数与格数都放大）；12 个施工目标。
            int loops = 0;
            for (int i = 0; i < 40; i++)
            {
                if (LayLoop(s, out int n, out _) != null && n > 0)
                {
                    loops++;
                }
            }
            GridCell core = HomeGridService.CorePivot(s);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            int sites = 0;
            try
            {
                mode.Open();
                mode.Select("power_pole");
                for (int i = 0; i < 12; i++)
                {
                    GridCell? c = FgProductionSelfCheck.FindArea(s, 1, 1, 14f + i, 40f);
                    if (c == null)
                    {
                        continue;
                    }
                    mode.PointerDown(s, c.Value);
                    mode.PointerUp(s, c.Value);
                    sites++;
                }
                mode.ClearSelection();
            }
            finally
            {
                mode.Close();
            }
            SoftlockService.CheckLoopsNow(s); // 拓扑变了：找代表格（O(有环网络数 × 格数)，只在网络重建后一次）
            int scans0 = SoftlockService.LoopRepresentativeScans;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 100; i++)
            {
                SoftlockService.CheckLoopsNow(s);
            }
            sw.Stop();
            double loopMs = sw.Elapsed.TotalMilliseconds / 100.0;
            bool noRescan = SoftlockService.LoopRepresentativeScans == scans0;
            // 可达性泛洪（机器真去过、到不了时才做）：直接量 12 个施工目标一次泛洪的开销（从核心外一圈 + 家园机器出发）。
            var ghosts = s.BuildingRecords.Where(b => b != null && b.BuildingTypeId == "power_pole" && b.ConstructionState != BuildingConstructionState.Operational).ToList();
            var machineCells = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive).Select(m => GameLogic.Campaign.Nav.NavService.CellOf(m.WorldPosition.x, m.WorldPosition.y)).ToList();
            var reached = new bool[ghosts.Count];
            var enclosed = new bool[ghosts.Count];
            var blockers = ghosts.Select(_ => new List<string>()).ToArray();
            bool floodOk = GameLogic.Campaign.Nav.NavService.SitesReachable(s, ghosts, machineCells, reached, enclosed, blockers, "地形") && reached.All(x => x);
            sw.Restart();
            for (int i = 0; i < 20; i++)
            {
                GameLogic.Campaign.Nav.NavService.SitesReachable(s, ghosts, machineCells, reached, enclosed, blockers, "地形");
            }
            sw.Stop();
            double reachMs = sw.Elapsed.TotalMilliseconds / 20.0;
            // 每个世界步的开销：跑 600 步（含两次闭环检查、一次可达性检查、10 次打印 / 产废料检查），按世界步平均。
            long baseTick = GameClock.Ticks;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 1; i <= 600; i++)
            {
                SoftlockService.StepAt(s, baseTick + i);
            }
            double stepUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency / 600.0;
            PerfLines.Add($"{loops} 个闭环 + {BeltNetworkService.Kernel.NetworkCount} 个传送带网络：闭环检查 {loopMs:0.###} ms / 次（每 5 游戏秒一次）；" +
                          $"{sites} 个施工目标可达性检查 {reachMs:0.###} ms / 次（每 10 游戏秒一次，Burst 泛洪）；软锁服务每世界步平均 {stepUs:0.##} µs（Editor batchmode / Mono；真机另测 FG15-SYS-02）");
            ExpectPerf(loops >= 20 && sites >= 6 && noRescan && floodOk,
                $"L1 性能：{loops} 个闭环检查 {loopMs:0.###} ms、{sites} 个施工目标可达性 {reachMs:0.###} ms、每世界步 {stepUs:0.##} µs；拓扑不变时不重找代表格（只按网络数读汇总）",
                PerfGate.Le(loopMs, 1.0, "闭环检查 ms"), PerfGate.Le(reachMs, 4.0, "可达性检查 ms"), PerfGate.Le(stepUs, 50.0, "每世界步 µs"));
        }

        // ── 框架 ─────────────────────────────────────────────────────────────────

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
                UiEscapeStack.Clear();
                InputRouter.SetBuildMode(false);
                InputRouter.BuildDragActive = false;
                InputRouter.SetGameplayPaused(false);
                StrategyClock.Reset();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
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

        private static void ExpectPerf(bool ok, string message, params PerfGate.Metric[] perf) => PerfGate.Expect(ok, message, perf, Expect, Line);

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
