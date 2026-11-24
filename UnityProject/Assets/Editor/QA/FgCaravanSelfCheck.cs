using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameConfig.fg;
using GameLogic.Campaign;
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
using GameLogic.UI.Kit;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG12-EVT-06 流浪商队的自动验收（FG10 FGR-EVT-050 / 051，FGU-39，FGT-EVT-007；承接 DEBT-FG12EVT01-02 的商队部分）。
    /// 全部起真实世界（家园星球 + 事件导演 + 真实 caravan 处理器），走 <c>WorldSimulation.StepMany / Frame</c>；商队经导演的正式入口 <c>EventDirectorService.Request</c> 来访。
    /// D 数据；A1 第一幕末门槛；A2 间隔 4～7 日；B1 一次来访（停靠点、3～5 件三类商品、通知、停留 1 游戏日后离开、商品清空）；B2 FGT-EVT-007 交易（固件 / 遗产 / 蓝图首次与重复）；
    /// R1 每次来访刷新、同种子同序号确定、多种子停靠点合格（B25）；N1 材料不足说明差多少且不改状态；N2 售完；N3 突袭将至提前离开 / 突袭在路上不来；
    /// N4 中立不进战斗内核；N5 不准入的固件不上架；N6 闭环产出 &lt; 投入；N7 遗产每个世界一件；U1 交易面板 / 暂停菜单 / 通知打开 / 点商队；
    /// T1 暂停与 0.5x～3x；T2 观察一致；S1 停留中存读档。已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgCaravanSelfCheck）。
    /// </summary>
    public static class FgCaravanSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("QA/自检/FG 流浪商队")]
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
            Line("\n[流浪商队] 第一幕末起来访、停留 1 游戏日、中立、3～5 件三类商品（价格 / 库存 / 差额）、交易（固件准入 / 遗产唯一 / 部件重复只加库存）、突袭将至提前离开、倍速 / 观察 / 存读档（FG12-EVT-06）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgcaravan-selfcheck-" + Guid.NewGuid().ToString("N"));
            GameObject root = null;
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
                CaravanService.Reload();
                HeritageService.Reload();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "商队与交易全在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），不进战斗内核；真机另测（FG15-SYS-02）");
                root = new GameObject("CaravanSelfCheckRoot");
                GameObject viewRoot = root;

                Step(CheckData);
                Step(CheckGates);
                Step(CheckVisit);
                Step(CheckTrade);
                Step(CheckRefreshAndSeeds);
                Step(CheckShortfallAndSoldOut);
                Step(CheckRaid);
                Step(() => CheckNeutral(viewRoot.transform));
                Step(CheckAdmission);
                Step(CheckLoopAndHeritageUnique);
                Step(CheckUi);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSaveLoad);
                Step(CheckPerformance);
            }
            catch (Exception e)
            {
                Fail($"流浪商队自检抛异常：{e}");
            }
            finally
            {
                CaravanService.AdmissionOverrideForTests = null;
                CaravanViews.Clear();
                if (root != null)
                {
                    Object.DestroyImmediate(root);
                }
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾 UnloadAll 抛异常：" + e.Message);
                }
                EventDirectorService.ResetForTests();
                CaravanService.ResetSessionState();
                GameClock.ResetSession();
                StrategyClock.Reset();
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ProductionService.ResetForTests();
                ResearchService.ResetForTests();
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
            Line($"  · [流浪商队] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 夹具 ─────────────────────────────────────────────────────────────────

        private static long Sec(double s) => GameClock.TicksFor(s);
        private static long DayTicks => GameClock.TicksFor(GameClock.DaySeconds);
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

        private static CombatSite Site => WorldSimulation.Home?.Combat;

        /// <summary>真实家园 + 事件导演 + 真实 caravan 处理器；战役在第 <paramref name="act"/> 幕。</summary>
        private static CampaignState World(int seed, bool observe, int act = 2)
        {
            EventDirectorService.ResetForTests();
            CaravanService.ResetSessionState();
            CaravanService.AdmissionOverrideForTests = null;
            HomeValleyPowerGrid.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, 3000);
            FgProductionSelfCheck.PowerUp(s);
            SignalCoreService.EnsureInitialized(s);
            EventDirectorService.Install();
            EventDirectorService.Register(new CaravanService.Handler());
            s.Progress.Act = act;
            EnsureWarehouse(s);
            WorldSimulation.StepMany(2);
            return s;
        }

        /// <summary>家园里有一座运转的仓库（合金 / 精密零件 / 组件库存要放在仓库里；与解析台自检同一做法）。</summary>
        private static void EnsureWarehouse(CampaignState s)
        {
            BuildingRecord wh = s.BuildingRecords.FirstOrDefault(b => b != null && BuildingOps.IsWarehouse(b) && !HomeGridService.IsRelocationGhost(b));
            if (wh != null)
            {
                wh.ConstructionState = BuildingConstructionState.Operational;
                wh.Health = BuildingOps.MaxDurability(wh.BuildingTypeId);
            }
            else
            {
                GridCell? c = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeWarehouse, 6f, 30f);
                if (c.HasValue)
                {
                    FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "cv_wh", c.Value);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
        }

        /// <summary>立即让事件导演评估一次（不推进世界：突袭夹具不会被突袭导演改掉）。</summary>
        private static void EvaluateNow(CampaignState s)
        {
            D(s).NextCheckTick = -1;
            EventDirectorService.WorldStep(s, GameClock.Ticks);
        }

        private static EventDirectorInstanceRecord Inst(CampaignState s) => CaravanService.Present(s);

        private static EventDirectorHistoryRecord LastHistory(CampaignState s) =>
            EventDirectorService.History(s).LastOrDefault(h => h.EventId == CaravanService.EventId);

        /// <summary>经导演正式入口请求一次，跑到商队到达（没有预警，下一次评估即开始）。</summary>
        private static EventDirectorInstanceRecord Arrive(CampaignState s)
        {
            if (!EventDirectorService.Request(s, CaravanService.EventId))
            {
                return null;
            }
            StepUntil(() => Inst(s) != null, 15);
            return Inst(s);
        }

        private static DirectorEventState D(CampaignState s) => EventDirectorService.StateOf(s);

        /// <summary>把这次来访的商品换成指定的几件（库存取表值）——只替换抽中的结果，交易仍走正式入口。</summary>
        private static void Offer(CampaignState s, params string[] wareIds)
        {
            D(s).CaravanWares = wareIds.Select(id => new CaravanWareRecord { WareId = id, Left = Math.Max(1, CaravanService.Row(id).Stock) }).ToArray();
        }

        private static ItemDef Item(string id) => ItemCatalog.Find(id);
        private static int Stock(CampaignState s, string id) => HomeInventory.Stock(s, id);

        private static void Give(CampaignState s, int alloy, int precision, int tech)
        {
            HomeInventory.Add(s, CaravanService.AlloyId, alloy);
            HomeInventory.Add(s, CaravanService.PrecisionId, precision);
            s.TechData += tech;
            HomeInventory.Touch();
        }

        private static int Chips(CampaignState s, string fw) => (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Count(c => c != null && c.CardDefId == fw);

        private static bool Unlocked(CampaignState s, string id) => Array.IndexOf(s.UnlockedContentIds ?? Array.Empty<string>(), id) >= 0;

        private static RaidPlanRecord AddRaid(CampaignState s, int state)
        {
            var p = new RaidPlanRecord { PlanId = "raid-cv-" + (s.Raids.Director.Plans?.Length ?? 0), StoryId = string.Empty, State = state, Trigger = "auto" };
            s.Raids.Director.Plans = (s.Raids.Director.Plans ?? Array.Empty<RaidPlanRecord>()).Concat(new[] { p }).ToArray();
            return p;
        }

        private static List<string> CaravanNotes() =>
            NotificationCenter.History.Where(e => e.Type?.Id == CaravanService.NotifyType).SelectMany(e => e.Members).Select(m => m.Detail ?? string.Empty).ToList();

        // ── D 数据 ─────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            EventDirectorDef d = EventDirectorService.Def(CaravanService.EventId);
            Expect(d != null && !d.Threat && !d.Major && d.HomeOnly && d.Trigger == EventDirectorService.TriggerRandom && Math.Abs(d.IntervalMinDays - 4) < 1e-6
                   && Math.Abs(d.IntervalMaxDays - 7) < 1e-6 && Math.Abs(d.DurationSeconds - GameClock.DaySeconds) < 1e-6 && d.WarningSeconds <= 0,
                "D1 fg.TbWorldEvent caravan：正向事件（只要通知、没有预警）、只在家园（突袭逼近时不来）、导演随机 4～7 日、停留 = 1 个游戏日（FGR-EVT-050）");
            IReadOnlyList<CaravanWare> rows = CaravanService.Rows;
            int dismantle = LootPickCatalog.DismantleTech;
            var bad = new List<string>();
            foreach (CaravanWare w in rows)
            {
                bool ok = w.Category switch
                {
                    CaravanService.CatFirmware => FirmwareKinds.IsFirmware(w.ContentId) && CaravanService.Admitted(w.ContentId) && FirmwareKinds.IsEnemyProtocol(w.ContentId),
                    CaravanService.CatHeritage => HeritageService.Find(w.ContentId) is { Story: 0 } && w.Stock == 1,
                    CaravanService.CatBlueprint => MechanicalContentFacade.TryGet(w.ContentId, out MechanicalContentDef md)
                                                   && (md.Category == MechanicalContentCategory.MainComponent || md.Category == MechanicalContentCategory.FunctionComponent || md.Category == MechanicalContentCategory.Structure),
                    _ => false,
                };
                if (!ok || w.Tech <= dismantle || w.Stock < 1 || w.Alloy + w.Precision + w.Tech <= 0)
                {
                    bad.Add(w.Id);
                }
            }
            string[] cats = rows.Select(r => r.Category).Distinct().OrderBy(x => x).ToArray();
            string[] keys = { "caravan.panel.title", "caravan.panel.stay", "caravan.panel.absent", "caravan.row", "caravan.buy", "caravan.buy.short", "caravan.notify.arrive",
                "caravan.notify.left_raid", "caravan.block.act", "pause.caravan", "caravan.cat.firmware", "caravan.cat.heritage", "caravan.cat.blueprint" };
            string[] missingK = keys.Where(k => !GameText.Has(k)).ToArray();
            string[] tun = { "wares_min", "wares_max", "stop_min", "stop_max", "entry_extra", "arrive_seconds", "place_attempts", "click_radius" };
            string[] missingT = tun.Where(k => !GridContent.TryGetTuning("event.caravan." + k, out _)).ToArray();
            string src = File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/Stage/GameRoot.cs");
            Expect(rows.Count >= 9 && bad.Count == 0 && cats.SequenceEqual(new[] { "blueprint", "firmware", "heritage" }) && missingK.Length == 0 && missingT.Length == 0
                   && CaravanService.WaresMin == 3 && CaravanService.WaresMax == 5 && NotificationCatalog.TryGetType(CaravanService.NotifyType, out _)
                   && src.Contains("Register(new Campaign.Env.CaravanService.Handler())"),
                $"D2 fg.TbCaravanWare {rows.Count} 行三类（{string.Join("/", cats)}）：固件过 FG19 准入、遗产非剧情且库存 1、蓝图是作战 / 功能组件或结构模块、每件技术数据价格 > 拆一件得到的 {dismantle}（不合格 {string.Join("、", bad)}）；" +
                $"调参齐（缺 {string.Join("、", missingT)}；3～5 件）、文本齐（缺 {string.Join("、", missingK)}）、通知类型 caravan、GameRoot 登记了处理器");
        }

        // ── A 门槛与间隔 ───────────────────────────────────────────────────────

        private static void CheckGates()
        {
            CampaignState s = World(6101, false, act: 1);
            EventDirectorService.Request(s, CaravanService.EventId);
            StepSec(25);
            bool held = Inst(s) == null && EventDirectorService.Schedule(s, CaravanService.EventId).BlockedKey == CaravanService.BlockAct;
            s.Progress.Pacing = (s.Progress.Pacing ?? Array.Empty<ActPaceRecord>()).Concat(new[] { new ActPaceRecord { Id = CaravanService.Act1EndPace, Tick = GameClock.Ticks } }).ToArray();
            bool came = StepUntil(() => Inst(s) != null, 20);
            Expect(held && came && s.Progress.Act == 1,
                $"A1 FGR-EVT-050“第一幕末起”：第 1 幕、铸造主核心没倒时请求也不来（推迟原因“第一幕末后商队才会来”，{held}）；击败铸造主核心（节奏点 foundry_core）后仍在第 1 幕即可到来（{came}）");

            CampaignState g = World(6102, false);
            StepSec(15);
            EventDirectorScheduleRecord rec = EventDirectorService.Schedule(g, CaravanService.EventId);
            Expect(rec != null && rec.GapTicks >= GameClock.TicksFor(4 * GameClock.DaySeconds) && rec.GapTicks <= GameClock.TicksFor(7 * GameClock.DaySeconds) && rec.AnchorTick >= 0,
                $"A2 来访间隔：进入生效阶段后导演抽到的间隔在 4～7 个游戏日（{rec?.GapTicks / (double)Math.Max(1, DayTicks):0.##} 日），由事件导演按频率倍率安排");
        }

        // ── B 一次来访 ─────────────────────────────────────────────────────────

        private static void CheckVisit()
        {
            CampaignState s = World(6201, true);
            int notes0 = CaravanNotes().Count;
            EventDirectorInstanceRecord r = Arrive(s);
            if (r == null)
            {
                Fail("B1 商队没有到来");
                return;
            }
            Vector2 core = SignalUplinkService.CorePosition(s);
            float dist = Vector2.Distance(core, new Vector2(r.X, r.Y));
            bool stop = dist >= CaravanService.StopMin - 1e-3f && dist <= CaravanService.StopMax + 1e-3f && WorldGenQuery.IsWalkableGenerated(s, Mathf.FloorToInt(r.X), Mathf.FloorToInt(r.Y));
            CaravanWareRecord[] wares = D(s).CaravanWares;
            string[] cats = wares.Select(w => CaravanService.Row(w.WareId).Category).Distinct().ToArray();
            bool goods = wares.Length >= 3 && wares.Length <= 5 && cats.Length == 3 && wares.All(w => w.Left == CaravanService.Row(w.WareId).Stock) && D(s).CaravanVisit == r.Serial;
            List<string> notes = CaravanNotes();
            bool notified = notes.Count == notes0 + 1 && notes.Last().Contains(wares.Length.ToString()) && notes.Last().Contains(CaravanService.WareName(CaravanService.Row(wares[0].WareId)));
            bool oneDay = r.EndTick - r.StartTick == DayTicks;
            StepSec(GameClock.DaySeconds - 5);
            bool still = Inst(s) != null && CaravanService.Wares(s).Count == wares.Length;
            StepSec(15);
            EventDirectorHistoryRecord h = LastHistory(s);
            bool left = Inst(s) == null && h != null && h.Outcome == EventDirectorService.OutcomeExpired && D(s).CaravanWares.Length == 0 && D(s).CaravanVisit == 0
                        && !CaravanService.Buy(s, wares[0].WareId, out string gone) && gone.Contains("离开");
            Expect(stop && goods && notified && oneDay && still && left,
                $"B1 一次来访：停在核心外 {dist:F1} 格的可走地面（{stop}）；{wares.Length} 件商品三类齐全、库存 = 表值（{goods}）；到达通知列出件数与商品名（{notified}）；" +
                $"停留正好 1 个游戏日（{oneDay}），快到时仍在（{still}）；到时离开、历史“到时离开”、商品清空、之后买不了（{left}）");
        }

        // ── B2 交易（FGT-EVT-007） ─────────────────────────────────────────────

        private static void CheckTrade()
        {
            CampaignState s = World(6301, false);
            if (Arrive(s) == null)
            {
                Fail("B2 商队没有到来");
                return;
            }
            Give(s, 600, 120, 1200);
            // 稀有固件：解锁 + 一枚芯片；库存 2 的再买一次 = 再一枚（芯片本来可多份）。
            CaravanWare fw = CaravanService.Row("cw_fw_scatter");
            Offer(s, fw.Id, "cw_bp_cannon", "cw_her_arc_climate");
            int a0 = Stock(s, CaravanService.AlloyId), p0 = Stock(s, CaravanService.PrecisionId), t0 = s.TechData, c0 = Chips(s, fw.ContentId);
            bool raw0 = FirmwareKinds.IsRaw(s, fw.ContentId);
            bool b1 = CaravanService.Buy(s, fw.Id, out string m1);
            bool paid = Stock(s, CaravanService.AlloyId) == a0 - fw.Alloy && Stock(s, CaravanService.PrecisionId) == p0 - fw.Precision && s.TechData == t0 - fw.Tech;
            bool fwOk = b1 && raw0 && !FirmwareKinds.IsRaw(s, fw.ContentId) && Unlocked(s, fw.ContentId) && Chips(s, fw.ContentId) == c0 + 1 && D(s).CaravanWares[0].Left == fw.Stock - 1;
            bool b2 = CaravanService.Buy(s, fw.Id, out _);
            bool fwMore = b2 && Chips(s, fw.ContentId) == c0 + 2 && D(s).CaravanWares[0].Left == 0;
            Expect(paid && fwOk && fwMore && m1.Contains(FirmwareKinds.DisplayName(fw.ContentId)),
                $"B2a 买稀有固件：扣 合金 {fw.Alloy} / 精密零件 {fw.Precision} / 技术数据 {fw.Tech}（{paid}），解锁并得到一枚可用芯片、库存 -1（{fwOk}）；再买一次再得一枚（芯片可多份，{fwMore}）");

            // 蓝图：第一次 = 解锁；已掌握再买 = 只加组件库存（FGR-EMG-193），不改任何数值。
            CaravanWare bp = CaravanService.Row("cw_bp_cannon");
            bool lock0 = !MechanicalContentUnlock.IsUnlocked(s, bp.ContentId);
            int cc0 = Stock(s, "combat_component");
            bool bpFirst = CaravanService.Buy(s, bp.Id, out _) && MechanicalContentUnlock.IsUnlocked(s, bp.ContentId) && Stock(s, "combat_component") == cc0;
            int unlockedCount = s.UnlockedContentIds.Count(x => x == bp.ContentId);
            bool bpDup = CaravanService.Buy(s, bp.Id, out string m3) && Stock(s, "combat_component") == cc0 + 1 && s.UnlockedContentIds.Count(x => x == bp.ContentId) == unlockedCount;
            Expect(lock0 && bpFirst && bpDup && m3.Contains("库存"),
                $"B2b 买蓝图：第一次解锁 {bp.ContentId} 蓝图、不加库存（{bpFirst}）；已掌握再买只加 1 件作战组件库存（多一台车能装）、不重复解锁（{bpDup}）");

            // 人类遗产：与带回入库同一效果（数据档案 = 技术数据），之后不再能买。
            CaravanWare her = CaravanService.Row("cw_her_arc_climate");
            bool can0 = HeritageService.CanTrade(s, her.ContentId);
            int tech0 = s.TechData;
            bool hb = CaravanService.Buy(s, her.Id, out _);
            Heritage hr = HeritageService.Find(her.ContentId);
            bool herOk = can0 && hb && HeritageService.IsObtained(s, her.ContentId) && s.TechData == tech0 - her.Tech + hr.TechData && !HeritageService.CanTrade(s, her.ContentId)
                         && !CaravanService.Available(s, her);
            Expect(herOk, $"B2c 买人类遗产（数据档案）：直接入库、按类别生效（技术数据 -{her.Tech} +{hr.TechData}），之后不再上架 / 不能再买（{herOk}）");
        }

        // ── R 刷新与种子 ───────────────────────────────────────────────────────

        private static void CheckRefreshAndSeeds()
        {
            CampaignState s = World(6401, false);
            var lists = new HashSet<string>();
            bool sizes = true, cats = true, det = true;
            for (int serial = 1; serial <= 12; serial++)
            {
                CaravanWareRecord[] a = CaravanService.RollWares(s, serial);
                CaravanWareRecord[] b = CaravanService.RollWares(s, serial);
                det &= string.Join(",", a.Select(x => x.WareId)) == string.Join(",", b.Select(x => x.WareId));
                sizes &= a.Length >= 3 && a.Length <= 5 && a.Select(x => x.WareId).Distinct().Count() == a.Length;
                cats &= a.Select(x => CaravanService.Row(x.WareId).Category).Distinct().Count() == 3;
                lists.Add(string.Join(",", a.Select(x => x.WareId)));
            }
            // 两次真实来访：商品按来访序号重新抽。
            EventDirectorInstanceRecord r1 = Arrive(s);
            string first = string.Join(",", D(s).CaravanWares.Select(x => x.WareId));
            EventDirectorService.Complete(s, r1.Serial, EventDirectorService.OutcomeExpired);
            EventDirectorService.Schedule(s, CaravanService.EventId).LastEndTick = -1; // 跳过冷却：只看“下一次来访换了一批”
            EventDirectorInstanceRecord r2 = Arrive(s);
            string second = string.Join(",", D(s).CaravanWares.Select(x => x.WareId));
            bool refreshed = r2 != null && r2.Serial != r1.Serial && D(s).CaravanVisit == r2.Serial && second.Length > 0;
            Expect(det && sizes && cats && lists.Count >= 8 && refreshed,
                $"R1 每次来访刷新（FGR-EVT-051）：同种子同序号结果确定（{det}）；12 个序号每次 3～5 件不重复、三类都有（{sizes} / {cats}），共 {lists.Count} 种组合；两次真实来访商品重新抽（{first} → {second}）");

            int[] seeds = { 6411, 6422, 6433, 6444, 6455 };
            int bad = 0, total = 0, coreMiss = 0;
            foreach (int seed in seeds)
            {
                CampaignState w = World(seed, false);
                Vector2 core = SignalUplinkService.CorePosition(w);
                if (!CaravanService.NearBuilding(w, core))
                {
                    coreMiss++; // 归还核心本身是建筑：占格查询必须查得到，否则“停靠点避开建筑”是空断言
                }
                for (int serial = 1; serial <= 20; serial++)
                {
                    total++;
                    if (!CaravanService.PickStop(w, serial, out Vector2 p) || !WorldGenQuery.IsWalkableGenerated(w, Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y))
                        || Vector2.Distance(p, core) < CaravanService.StopMin - 1e-3f || Vector2.Distance(p, core) > CaravanService.StopMax + 1e-3f
                        || CaravanService.NearBuilding(w, p))
                    {
                        bad++;
                    }
                }
            }
            Expect(bad == 0 && coreMiss == 0, $"R2 B25：{seeds.Length} 个种子 × 20 次来访的停靠点都在核心外 {CaravanService.StopMin}～{CaravanService.StopMax} 格的可走地面、车身 ±2 格内没有建筑（不合格 {bad}/{total}；核心格查得到建筑，漏查 {coreMiss}），只取决于种子与序号");
        }

        // ── N 负向 ─────────────────────────────────────────────────────────────

        private static void CheckShortfallAndSoldOut()
        {
            CampaignState s = World(6501, false);
            Arrive(s);
            CaravanWare w = CaravanService.Row("cw_bp_marker");
            Offer(s, w.Id);
            HomeInventory.RemoveUpTo(s, Item(CaravanService.AlloyId), Stock(s, CaravanService.AlloyId));
            HomeInventory.RemoveUpTo(s, Item(CaravanService.PrecisionId), Stock(s, CaravanService.PrecisionId));
            s.TechData = 0;
            Give(s, w.Alloy - 7, w.Precision, 0);
            string before = JsonUtility.ToJson(D(s)) + Stock(s, CaravanService.AlloyId) + "/" + s.TechData + "/" + string.Join(",", s.UnlockedContentIds ?? Array.Empty<string>());
            bool denied = !CaravanService.Buy(s, w.Id, out string msg);
            string after = JsonUtility.ToJson(D(s)) + Stock(s, CaravanService.AlloyId) + "/" + s.TechData + "/" + string.Join(",", s.UnlockedContentIds ?? Array.Empty<string>());
            string alloyName = Item(CaravanService.AlloyId).Name;
            string techName = Item(ItemCatalog.TechDataId).Name;
            bool says = msg.Contains(alloyName + " ×7") && msg.Contains(techName + " ×" + w.Tech) && !msg.Contains(Item(CaravanService.PrecisionId).Name + " ×");
            string shortRow = CaravanService.Shortfall(s, w) ?? string.Empty;
            Expect(denied && before == after && says && shortRow.Contains("×7"),
                $"N1 材料不足：买不了、不改任何状态（{before == after}），说明逐项写差多少——合金差 7、技术数据差 {w.Tech}，够的精密零件不列（“{msg.Replace('\n', '；')}”）；面板行里同样写明");
            Give(s, 7 + w.Alloy * 2, w.Precision * 2, w.Tech * 3); // 补上差的 7 之后再够买两件
            bool b1 = CaravanService.Buy(s, w.Id, out _), b2 = CaravanService.Buy(s, w.Id, out _);
            bool b3 = CaravanService.Buy(s, w.Id, out string sold);
            Expect(b1 && b2 && !b3 && sold.Contains("售完") && D(s).CaravanWares[0].Left == 0,
                $"N2 库存：这件库存 {w.Stock}，买满后第 {w.Stock + 1} 次被拒（“{sold}”），库存不会变负");
        }

        private static void CheckRaid()
        {
            CampaignState s = World(6601, false);
            EventDirectorInstanceRecord r = Arrive(s);
            Give(s, 500, 100, 800);
            RaidPlanRecord p = AddRaid(s, RaidDirectorService.StateWarned);
            EvaluateNow(s);
            EventDirectorHistoryRecord h = LastHistory(s);
            string ware = r != null ? "cw_bp_cannon" : null;
            bool left = r != null && Inst(s) == null && h != null && h.Outcome == EventDirectorService.OutcomeLeftRaid && D(s).CaravanWares.Length == 0
                        && CaravanNotes().Any(n => n.Contains("提前离开")) && !CaravanService.Buy(s, ware, out _);
            EventDirectorService.Schedule(s, CaravanService.EventId).LastEndTick = -1; // 跳过冷却：只看“突袭在路上不来”
            EventDirectorService.Request(s, CaravanService.EventId);
            EvaluateNow(s);
            bool notWhileRaid = Inst(s) == null && EventDirectorService.Schedule(s, CaravanService.EventId).BlockedKey == EventDirectorService.BlockRaid;
            p.State = RaidDirectorService.StateEnded;
            bool after = StepUntil(() => Inst(s) != null, 25);
            Expect(left && notWhileRaid && after,
                $"N3 负向“商队停留期间突袭即将到来”（FG10 第 11 节）：突袭预警 → 商队提前离开（历史“突袭将至，提前离开”、商队通知、商品清空、买不了，{left}）；" +
                $"突袭在路上时请求的商队不来（推迟原因“突袭正在逼近家园”，{notWhileRaid}）；突袭结束后才来（{after}）——商队与突袭不同时出现在家园");
        }

        private static void CheckNeutral(Transform viewRoot)
        {
            CampaignState s = World(6701, true);
            CombatSite site = Site;
            int slots0 = site?.Kernel.SlotCount ?? -1;
            EventDirectorInstanceRecord r = Arrive(s);
            int slots1 = site?.Kernel.SlotCount ?? -2;
            StepSec(60);
            int slots2 = site?.Kernel.SlotCount ?? -3;
            CaravanViews.FrameUpdate(s, viewRoot);
            bool drawn = CaravanViews.Visible && CaravanViews.Body != null && CaravanViews.Body.GetComponent<Collider>() == null;
            float dToStop = r != null ? Vector2.Distance(new Vector2(CaravanViews.Position.x, CaravanViews.Position.z), new Vector2(r.X, r.Y)) : -1f;
            Expect(site != null && r != null && slots0 == slots1 && slots1 == slots2 && drawn && dToStop < 0.05f && Inst(s) == r,
                $"N4 中立（FGR-EVT-050“炮塔不会攻击它”）：商队不进战斗内核（到达前后内核单位槽 {slots0} → {slots1} → {slots2}；不在内核里，所以炮塔 / 敌人 / 玩家的选目标都无从选到它）；" +
                $"主视图画占位外形且没有碰撞体（{drawn}），走到停靠点（偏差 {dToStop:F2}），停留中照常在");

            // U4c 点中商队（单点选中分支调 TryClick）：射线落在画着的商队上 = 打开面板；偏开不算。
            Vector3 cp = CaravanViews.Position;
            int opens0 = CaravanViews.ClickOpens;
            bool clickHit = CaravanViews.TryClick(new Ray(cp + new Vector3(0f, 30f, -20f), (cp + Vector3.up - (cp + new Vector3(0f, 30f, -20f))).normalized));
            bool clickMiss = !CaravanViews.TryClick(new Ray(cp + new Vector3(6f, 30f, -20f), Vector3.down));
            CaravanTradePanelUIToolkit.Close();
            Expect(clickHit && clickMiss && CaravanViews.ClickOpens == opens0 + 1,
                $"U4c 点中商队：单点选中分支传来的射线落在商队上打开交易面板（{clickHit}），偏开不打开（{clickMiss}），打开次数 +1（{CaravanViews.ClickOpens - opens0}）");

            // 从迷雾外走来：到达那一刻在出发点（停靠点外 entry_extra 格）。
            CampaignState w = World(6702, true);
            EventDirectorInstanceRecord r2 = Arrive(w);
            Vector2 at0 = CaravanService.ViewPosition(w, r2);
            Vector2 core = SignalUplinkService.CorePosition(w);
            bool fromFog = r2 != null && Vector2.Distance(at0, core) > Vector2.Distance(new Vector2(r2.X, r2.Y), core) + CaravanService.EntryExtra * 0.8f;
            Expect(fromFog, $"N4b FGR-EVT-050“从迷雾外来到家园附近”：到达时在停靠点外约 {CaravanService.EntryExtra} 格，{CaravanService.ArriveSeconds} 秒内走到停靠点（{fromFog}）");
        }

        private static void CheckAdmission()
        {
            CampaignState s = World(6801, false);
            CaravanService.AdmissionOverrideForTests = id => id != "fw_scatter";
            bool rollSkips = Enumerable.Range(1, 30).All(serial => CaravanService.RollWares(s, serial).All(x => x.WareId != "cw_fw_scatter"));
            Arrive(s);
            Give(s, 500, 100, 800);
            Offer(s, "cw_fw_scatter");
            bool denied = !CaravanService.Buy(s, "cw_fw_scatter", out string msg) && Chips(s, "fw_scatter") == 0;
            CaravanService.AdmissionOverrideForTests = _ => false;
            bool noFw = Enumerable.Range(1, 10).All(serial => CaravanService.RollWares(s, serial).All(x => CaravanService.Row(x.WareId).Category != CaravanService.CatFirmware)
                                                               && CaravanService.RollWares(s, serial).Length >= 3);
            CaravanService.AdmissionOverrideForTests = null;
            Expect(rollSkips && denied && noFw,
                $"N5 FG19 FGR-EMG-014：没通过准入的固件不上架（30 次抽取都没有，{rollSkips}），已列出的也买不了（“{msg}”，{denied}）；所有固件都不准入时照样凑满 3 件其余商品（{noFw}）");
        }

        private static void CheckLoopAndHeritageUnique()
        {
            CampaignState s = World(6901, false);
            Arrive(s);
            Give(s, 2000, 400, 4000);
            CaravanWare bp = CaravanService.Row("cw_bp_armor");
            Offer(s, bp.Id);
            CaravanService.Buy(s, bp.Id, out _); // 第一次 = 解锁
            ItemDef stockItem = Item(LootPickService.DuplicateStockItem(s, bp.ContentId));
            HomeInventory.Add(s, stockItem, HomeInventory.Space(s, stockItem)); // 仓库塞满：再买的那件只能拆成技术数据
            int tech0 = s.TechData;
            int alloy0 = Stock(s, CaravanService.AlloyId), prec0 = Stock(s, CaravanService.PrecisionId);
            D(s).CaravanWares[0].Left = 1;
            bool bought = CaravanService.Buy(s, bp.Id, out _);
            int gained = s.TechData - (tech0 - bp.Tech);
            bool loop = bought && gained == LootPickCatalog.DismantleTech && gained < bp.Tech && Stock(s, CaravanService.AlloyId) == alloy0 - bp.Alloy && Stock(s, CaravanService.PrecisionId) == prec0 - bp.Precision;
            bool allRows = CaravanService.Rows.All(r => r.Tech > LootPickCatalog.DismantleTech);
            Expect(loop && allRows,
                $"N6 负向“买入 → 分解 → 再买”闭环：仓库满时再买的部件拆成技术数据 {gained}（= 拆一件 {LootPickCatalog.DismantleTech}），付出 合金 {bp.Alloy} + 精密零件 {bp.Precision} + 技术数据 {bp.Tech}，" +
                $"产出 < 投入（{loop}）；表里每件技术数据价格都高于拆解所得（{allRows}；整体无限循环检查交 FG15-BAL-01）");

            CampaignState h = World(6902, false);
            Arrive(h);
            Give(h, 500, 100, 800);
            CaravanWare her = CaravanService.Row("cw_her_mem_badge");
            string relic = HeritageService.RelicOf(h, her.ContentId);
            Offer(h, her.Id);
            bool bought2 = CaravanService.Buy(h, her.Id, out _);
            bool relicEmpty = relic != null && HeritageService.OnRelicRecovered(h, relic, 0, 0, "relic", 0) == null && HeritageService.FindItem(h, her.ContentId) == null;
            bool notRolled = Enumerable.Range(1, 30).All(serial => CaravanService.RollWares(h, serial).All(x => x.WareId != her.Id));
            Expect(bought2 && relicEmpty && notRolled,
                $"N7 人类遗产每个世界只有一件（FG8-EXP-04）：从商队买到后，原本放它的遗迹（{relic}）回收时不再出现它（{relicEmpty}），之后的来访也不再卖它（{notRolled}）");
        }

        // ── U 界面 ─────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            CampaignState s = World(7001, false);
            VisualElement root = MountUxml(UiKitFolder + "CaravanTradePanel.uxml", out GameObject go);
            CaravanTradePanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                CaravanTradePanelUIToolkit panel = go.AddComponent<CaravanTradePanelUIToolkit>();
                panel.BindView(root);
                panel.SetOpen(true);
                panel.Refresh();
                bool absent = panel.PanelVisible && panel.VisibleRowCount == 0 && panel.StayText.Contains("4～7") && panel.EmptyText.Length == 0;
                panel.SetOpen(false);
                Arrive(s);
                Offer(s, "cw_fw_cluster", "cw_bp_fin", "cw_her_mem_musicbox");
                HomeInventory.RemoveUpTo(s, Item(CaravanService.AlloyId), Stock(s, CaravanService.AlloyId));
                s.TechData = 0;
                NotificationEntry note = NotificationCenter.History.LastOrDefault(e => e.Type?.Id == CaravanService.NotifyType);
                bool opened = note != null && NotificationCenter.HasOpenHandler(note) && NotificationCenter.TryOpen(note) && panel.PanelVisible;
                panel.Refresh();
                CaravanWare fw = CaravanService.Row("cw_fw_cluster");
                string row0 = panel.RowText(0);
                bool rows = panel.VisibleRowCount == 3 && row0.Contains("稀有固件") && row0.Contains(FirmwareKinds.DisplayName(fw.ContentId)) && row0.Contains("库存 " + fw.Stock)
                            && row0.Contains("×" + fw.Alloy) && row0.Contains("缺") && panel.StayText.Contains("还会停留") && panel.FooterText.Contains("合金")
                            && panel.RowText(1).Contains("蓝图") && panel.RowText(2).Contains("人类遗产");
                Click(panel.BuyButton(0));
                bool deniedNote = panel.FooterText.Contains("材料不够") && D(s).CaravanWares[0].Left == fw.Stock;
                Give(s, 500, 100, 800);
                panel.Refresh();
                Click(panel.BuyButton(0));
                bool boughtNote = panel.FooterText.Contains("买入") && D(s).CaravanWares[0].Left == fw.Stock - 1 && panel.RowText(0).Contains("库存 " + (fw.Stock - 1));
                D(s).CaravanWares[2].Left = 0;
                s.TechData += 1; // 只动技术数据（不碰仓库 / 商队版本号）：面板刷新键含技术数据，仍会重建
                panel.Refresh();
                Button soldBtn = panel.BuyButton(2);
                bool soldOut = panel.VisibleRowCount == 3 && panel.RowText(2).Contains("已售完") && soldBtn != null && !soldBtn.enabledSelf && soldBtn.text == "已售完";
                GameSettings.SetLanguage(GameLanguage.En);
                panel.Refresh();
                bool en = panel.RowText(0).Contains("Rare firmware") && panel.RowText(0).Contains("left");
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                panel.SetOpen(false);
                Expect(absent && opened && rows && deniedNote && boughtNote && soldOut && en,
                    $"U1 交易面板（FGU-39：商品、价格、库存）：商队不在时写明何时会来（{absent}）；到达通知点“打开”即打开面板（{opened}）；每行写类别、名称、效果、价格（含仓库现有）、库存、差多少（{rows}）；" +
                    $"材料不够点买入不扣、页脚写原因（{deniedNote}）；够了买入、库存 -1（{boughtNote}）；售完的行留着写“已售完”、买入禁用（{soldOut}）；中英文（{en}）");
            }
            finally
            {
                CaravanTradePanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
            string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "CaravanTradePanel.uxml", "CaravanRoot");
            Expect(probe.Contains("PASS"), "U2 交易面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + probe.Split('\n').FirstOrDefault());

            VisualElement pauseRoot = MountUxml(UiKitFolder + "PauseMenu.uxml", out GameObject pgo);
            try
            {
                Button b = pauseRoot.Q<Button>("PauseCaravan");
                string pauseSrc = File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/UI/Kit/PauseMenuUIToolkit.cs");
                string viewSrc = File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/Campaign/Regions/HomeValleyController.cs");
                Expect(b != null && pauseSrc.Contains("\"PauseCaravan\", \"pause.caravan\", CaravanTradePanelUIToolkit.Open") && viewSrc.Contains("CaravanViews.FrameUpdate("),
                    "U3 入口：暂停菜单有“流浪商队”按钮（打开交易面板）、家园主视图每帧画商队并接受左键点中打开");
            }
            finally
            {
                Object.DestroyImmediate(pgo);
            }
            // 点中判定：从商队正上方的射线命中，偏开 click_radius 以外不命中。
            var pos = new Vector3(10f, 0f, 10f);
            bool hit = CaravanViews.HitsCaravan(new Ray(pos + new Vector3(0f, 30f, -20f), (pos + Vector3.up - (pos + new Vector3(0f, 30f, -20f))).normalized), pos);
            bool miss = !CaravanViews.HitsCaravan(new Ray(pos + new Vector3(6f, 30f, -20f), Vector3.down), pos);
            Expect(hit && miss, $"U4 主视图点商队：射线命中车身 {CaravanService.ClickRadius} 米内算点中（{hit}），偏开就不算（{miss}）");
            CheckClickScope();
        }

        /// <summary>U4b 审查 P1：点商队与机器 / 建筑点选共用 HomeValleyController.TryReadPrimaryClick（Strategy 域 + 按键映射 + 本帧未被框选 / 武装命令消费），
        /// CaravanViews 自己不再读原始鼠标键。</summary>
        private static void CheckClickScope()
        {
            var reader = new ClickReader();
            InputRouter.DebugSetReader(reader);
            try
            {
                reader.Up.Add(0);
                InputRouter.SetScope(InputScope.Direct);
                bool direct = !HomeValleyController.TryReadPrimaryClick(false, out _);
                InputRouter.SetScope(InputScope.Strategy);
                bool armed = !HomeValleyController.TryReadPrimaryClick(true, out _);
                bool plain = HomeValleyController.TryReadPrimaryClick(false, out Vector3 at) && at == reader.MousePosition;
                reader.Up.Clear();
                reader.Up.Add(1);
                bool secondary = !HomeValleyController.TryReadPrimaryClick(false, out _);
                string viewSrc = File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/View/CaravanViews.cs");
                string homeSrc = File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/Campaign/Regions/HomeValleyController.cs");
                int select = homeSrc.IndexOf("SquadCommands.SelectSingle(marker.LogicId", StringComparison.Ordinal);
                int caravan = homeSrc.IndexOf("CaravanViews.TryClick(BinGames.Sim.ViewOrigin.ToWorld(ray)))", StringComparison.Ordinal); // 机器分支之后那一处（无命中分支里写的是“TryClick(…) ||”）；FG15-SYS-02 浮动原点后射线先换回世界坐标（FG15-QA-01 回归包查出，断言跟改）
                bool routed = !viewSrc.Contains("GetMouseButton") && !viewSrc.Contains("MousePosition") && select > 0 && caravan > select
                              && homeSrc.Contains("if (!TryReadPrimaryClick(SquadCommands.ConsumedClickThisFrame, out Vector3 pointer))");
                Expect(direct && armed && plain && secondary && routed,
                    $"U4b 负向（审查 P1，B01 输入）：直控域左键不算点中（{direct}）；框选 / 武装命令确认已消费的那次点击不算（{armed}）；功能键（右键）抬起不算（{secondary}）；" +
                    $"战略域主动作抬起才算、指针取自同一次读取（{plain}）；商队点中只在家园单点选中分支里、排在机器点选之后，CaravanViews 不读原始鼠标键（{routed}）");
            }
            finally
            {
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
            }
        }

        private sealed class ClickReader : IInputReader
        {
            public readonly HashSet<int> Up = new HashSet<int>();
            public bool GetKey(KeyCode key) => false;
            public bool GetKeyDown(KeyCode key) => false;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => Up.Contains(button);
            public Vector3 MousePosition => new Vector3(400f, 300f, 0f);
            public float MouseScrollDelta => 0f;
        }

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgCaravanSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
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

        // ── T 暂停 / 倍速 / 观察 ──────────────────────────────────────────────

        private static string Snap(CampaignState s)
        {
            var sb = new StringBuilder(JsonUtility.ToJson(s.DirectorEvents));
            sb.Append("|alloy=").Append(Stock(s, CaravanService.AlloyId)).Append("|prec=").Append(Stock(s, CaravanService.PrecisionId)).Append("|tech=").Append(s.TechData);
            sb.Append("|chips=").Append(s.PrimitiveChips?.Length ?? 0).Append("|unlocked=").Append(string.Join(",", s.UnlockedContentIds ?? Array.Empty<string>()));
            sb.Append("|cc=").Append(Stock(s, "combat_component")).Append("|sm=").Append(Stock(s, "structure_module"));
            return sb.ToString();
        }

        private static void FrameTo(long target, ref int frames)
        {
            int guard = 0;
            while (GameClock.Ticks < target && guard++ < 60 * 1200)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
        }

        /// <summary>请求 → 到达 → 买两件 → 停留到期离开（暂停中 120 帧不动；按倍速跑完整个 1 游戏日）。</summary>
        private static string RunScenario(int seed, bool observe, float speed, out bool pausedHeld, out int frames)
        {
            CampaignState s = World(seed, observe);
            frames = 0;
            Give(s, 600, 120, 1200);
            EventDirectorService.Request(s, CaravanService.EventId);
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
            FrameTo(t0 + Sec(30), ref frames);
            CaravanWareRecord[] wares = D(s).CaravanWares;
            if (wares.Length > 0)
            {
                CaravanService.Buy(s, wares[0].WareId, out _);
                CaravanService.Buy(s, wares[wares.Length - 1].WareId, out _);
            }
            string mid = Snap(s);
            GameClock.SetPaused(true);
            for (int i = 0; i < 60; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            pausedHeld &= Snap(s) == mid;
            GameClock.SetPaused(false);
            FrameTo(t0 + Sec(150), ref frames);
            string late = Snap(s);
            GameClock.SetSpeed(1f);
            StepSec(GameClock.DaySeconds - 100); // 余下的停留按世界步跑完（倍速只影响真实帧数，上面已量）
            return mid + "||" + late + "||" + Snap(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true, paused = true, proportional = true;
            var shown = new List<string>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(7101, false, speed, out bool held, out int frames);
                paused &= held;
                reference ??= snap;
                if (snap != reference && same)
                {
                    Line($"  · T1 {speed}x 与 0.5x 的首个差异：{Diff(reference, snap)}");
                }
                same &= snap == reference;
                proportional &= Math.Abs(frames * speed - Sec(150)) <= 4f;
                shown.Add($"{speed}x {frames} 帧");
            }
            Expect(paused && same && proportional && reference.Contains("\"CaravanBought\":2") && reference.Contains("\"Outcome\":\"expired\""),
                $"T1 暂停与 0.5x～3x：暂停中商队不来、停留计时不走；{string.Join(" / ", shown)} 跑同样的到达 → 买两件 → 停留 1 游戏日后离开，商品、库存、材料逐字段一致，" +
                $"所需真实帧与倍速成反比（暂停 {paused} / 一致 {same} / 成反比 {proportional}）");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(7102, true, 3f, out _, out _);
            string unseen = RunScenario(7102, false, 3f, out _, out _);
            if (seen != unseen)
            {
                Line("  · T2 首个差异：" + Diff(seen, unseen));
            }
            Expect(seen == unseen && seen.Contains("\"CaravanBought\":2"), "T2 家园观察与不观察时，同一段（到达、商品、交易、停留到期离开）结果逐字段一致（FGR-BASE-021）");
        }

        private static string Diff(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i])
            {
                i++;
            }
            int from = Math.Max(0, i - 60);
            return $"…{a.Substring(from, Math.Min(140, a.Length - from))} ⇔ …{b.Substring(from, Math.Min(140, b.Length - from))}";
        }

        // ── S 存读档 ───────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = World(7201, true);
            Give(s, 600, 120, 1200);
            EventDirectorInstanceRecord r0 = Arrive(s);
            CaravanWareRecord[] wares = D(s).CaravanWares;
            CaravanService.Buy(s, wares[0].WareId, out _);
            StepSec(120);
            string before = Snap(s);
            string waresAtSave = string.Join(",", D(s).CaravanWares.Select(x => x.WareId + ":" + x.Left));
            WorldSimulation.SyncAllForSave();
            int slot = CampaignSession.ActiveSlotIndex;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(slot, SaveReason.Manual);
            StepSec(300);
            CaravanService.Buy(s, wares[wares.Length - 1].WareId, out _);
            StepSec(GameClock.DaySeconds);
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
            CampaignSession.Set(slot, rr.State);
            WorldSimulation.LoadHome(resume: true);
            CampaignState l = rr.State;
            string loaded = Snap(l);
            EventDirectorInstanceRecord lr = Inst(l);
            bool same = lr != null && r0 != null && lr.Serial == r0.Serial && Mathf.Approximately(lr.X, r0.X) && lr.EndTick == r0.EndTick
                        && string.Join(",", D(l).CaravanWares.Select(x => x.WareId + ":" + x.Left)) == waresAtSave && waresAtSave.Length > 0;
            StepSec(300);
            CaravanService.Buy(l, wares[wares.Length - 1].WareId, out _);
            StepSec(GameClock.DaySeconds);
            string after = Snap(l);
            Expect(save.Success && loaded == before && same && after == continuous && continuous.Contains("\"Outcome\":\"expired\""),
                $"S1 负向“事件进行中存档”——商队停留中、已买一件：真文件存档 → 读档后停靠点、剩余停留时间、商品与剩余库存一致（读回 {loaded == before}，{same}），" +
                $"继续买一件、停留到期离开后与不存档逐字段一致（{after == continuous}" + (after == continuous ? string.Empty : "，首个差异 " + Diff(continuous, after)) + "）");
        }

        // ── P 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = World(7301, false);
            Arrive(s);
            const int n = 100000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                CaravanService.Present(s);
            }
            double presentUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            sw.Restart();
            for (int i = 0; i < 1000; i++)
            {
                CaravanService.RollWares(s, 100 + i);
            }
            double rollUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 1000;
            Line($"  · 性能：CaravanService.Present（每帧主视图 / 面板）{presentUs:0.###} µs；抽商品 RollWares（只在到达那一刻，{CaravanService.Rows.Count} 行）{rollUs:0.#} µs");
            Expect(presentUs < 5.0 && rollUs < 5000.0, $"P1 每帧开销与单位 / 建筑数无关（{presentUs:0.###} µs，线 5 µs）；抽商品 {rollUs:0.#} µs 只发生在到达那一刻");
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
