using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Endgame;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using GameLogic.View;
using TEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG13-END-01 返航工程的自动验收（FG11 FGR-END-001 / 002；验收 FGT-END-002“巨构：物资送入、进度计算、受损暂停、不倒退、阶段突袭”；
    /// 卡片负向“工地被摧毁”“物资供给中断”；承接 DEBT-FG6DEF04-01（巨构阶段完工的真实触发）、DEBT-FG8QST01-03（有巨构蓝图才能开工））。
    /// 全部起真实系统：真实世界模拟与家园（世界步里跑传送带 / 管线 / 生产 / 突袭导演）、真实传送带与管线内核（物资从测试源经传送带 / 管线送进工地的口）、
    /// 正式的任务完成入口（反射调 CampaignObjectiveTracker.CompleteQuest，奖励 → 事件账本 → 资料库 → 第三幕）、真实突袭导演排定、真文件存读档、真 UXML 生产面板。
    /// 电力：电网是否供得上 500 由真实 Recompute 判（N3）；推进段把结算结果字段 PowerState 直接置为 Powered / Unpowered（与黑匣子 / 情报自检同一做法，电网本身由 FG3-LOG-06 覆盖）。
    /// A 数据；B 解锁与上限（DEBT-FG8QST01-03）；C 正常推进一个阶段（FGT-END-002 物资送入 / 进度 / 预计时间 / 阶段突袭 / 阶段消息）；D 五个阶段全部完工（突袭等级不降、语气按节点表、信标点火事件）；
    /// N 负向（受损暂停不倒退、被摧毁暂停 + 重建接着做、物资供给中断、缺电、电网供不上 500、完工后不再收料）；T 暂停与 0.5x～3x；O 观察一致；S 真文件存读档；U 工地面板；P 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgMegastructureSelfCheck）；单段：bash tools/unity-validate.sh FgMegastructureSelfCheck。
    /// </summary>
    public static partial class FgMegastructureSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;
        private const string Site = MegastructureService.TypeId;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

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

        public static int Run(StringBuilder report) =>
            RunCore(report, "返航工程", "\n[返航工程] 五个巨构阶段、传送带 / 管线送入、持续供电、进度与预计时间、阶段突袭、受损暂停不倒退、舰队消息、倍速 / 观察 / 存读档、工地面板（FG13-END-01，FGT-END-002）",
                new Action[] { CheckData, CheckUnlock, CheckStageOne, CheckAllStages, CheckWounded, CheckDestroyedAndRebuilt, CheckSupplyCut, CheckPower, CheckTimingMatrix, CheckObserved, CheckSaveLoad, CheckUi, CheckPerformance });

        /// <summary>同一套环境准备与收尾（FG13-END-02 揭示段 <see cref="RunReveal"/> 共用）。</summary>
        private static int RunCore(StringBuilder report, string tag, string title, Action[] steps)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            PerfLines.Clear();
            Line(title);
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
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgmega-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                Campaign.WorldGen.WorldGenContent.Reload();
                Campaign.Content.FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                MegastructureService.Reload();
                NarrativeCatalog.Reload();
                GameClock.ReloadTuning();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                NotificationCenter.AutoPauseHandler = null;
                Stage.GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "工地推进在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），每个生产步 O(1)；真机另测（FG15-SYS-02）");

                foreach (Action step in steps)
                {
                    Step(step);
                }
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"返航工程自检抛异常：{e}");
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
                HomeValleyPowerGrid.ResetForTests();
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.Close();
                GameClock.ResetSession();
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                GridContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                MegastructureService.Reload(); // 复审 P2：巨构缓存按 ItemDef 实例比较，物品目录重置后要跟着重载
                ProductionService.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
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
            Line($"  · [{tag}] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static int Hz => Math.Max(1, GameClock.StepHz);
        private static MegaStageDef Stage0 => MegastructureService.Stages[0];
        private static long PowerNeed => MegastructureService.PowerTicksFor(Stage0);

        private static CampaignState NewWorld(int seed, bool observe = true)
        {
            HomeValleyPowerGrid.ResetForTests();
            CampaignState s = F.NewWorld(seed, observe, 2000);
            WorldSimulation.StepMany(1);
            return s;
        }

        internal sealed class Rig
        {
            public BuildingRecord Site;
            public GridCell Pivot;
            public int SourceW = -1;
            public int SourceS = -1;
            public GridCell FeedCell;
            public int Feed = -1;
        }

        /// <summary>按种子地形在核心周围找一块能放 12×12 工地、西口 / 南口外各两格传送带、东口外两格管线的地方（不写死坐标，B25）。</summary>
        internal static GridCell? FindSitePivot(CampaignState s, Func<GridCell, bool> extra = null)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (int r = 12; r <= 48; r++)
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
                        if (!HomeGridService.ValidatePlacement(s, Site, p, 0, asPlayerPlacement: false, checkCost: false).Ok || extra != null && !extra(p))
                        {
                            continue;
                        }
                        bool ok = true;
                        foreach (GridCell c in new[] { F.At(p, -6, 0), F.At(p, -7, 0), F.At(p, 0, -6), F.At(p, 0, -7), F.At(p, 7, 0), F.At(p, 8, 0) })
                        {
                            ok &= HomeGridService.ValidatePipeCell(s, c, PipePieceKind.Pipe).Ok
                                  && HomeGridService.ValidatePlacement(s, "power_pole", c, 0, asPlayerPlacement: false, checkCost: false).Ok;
                        }
                        if (ok)
                        {
                            return p;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>放下一座已建成的工地（测试捷径，施工流程由 FG3-LOG-02 覆盖）：西口 / 南口外铺传送带并挂测试源（各 <paramref name="partsEach"/> 件巨构构件），
        /// 东口外铺管线并挂测试供冷却液点（<paramref name="liters"/> 升）。<paramref name="powered"/> = 电网结算结果置为有电。</summary>
        private static Rig Lay(CampaignState s, string key, int partsEach, long liters, bool powered = true, GridCell? at = null)
        {
            GridCell? p = at ?? FindSitePivot(s);
            if (p == null)
            {
                return null;
            }
            BuildingRecord site = F.Built(s, Site, key, p.Value);
            site.Health = BuildingOps.MaxDurability(Site); // 正式施工完工时耐久回满（HomeValleyWorkOrders）；夹具 F.Built 记 100
            return Supply(s, site, p.Value, partsEach, liters, powered);
        }

        /// <summary>给已建成的工地 <paramref name="site"/>（中心格 <paramref name="p"/>）铺供给：西口 / 南口外传送带 + 测试源、东口外管线 + 测试供冷却液点（FGJ-M13 夹具也用它）。</summary>
        internal static Rig Supply(CampaignState s, BuildingRecord site, GridCell p, int partsEach, long liters, bool powered = true)
        {
            var rig = new Rig { Pivot = p, FeedCell = F.At(p, 8, 0), Site = site };
            F.Belts(s, F.At(p, -7, 0), BeltDir.East, 2);
            F.Belts(s, F.At(p, 0, -7), BeltDir.North, 2);
            PipeNetworkService.TryPlace(s, F.At(p, 7, 0), PipePieceKind.Pipe, 0, 0);
            PipeNetworkService.TryPlace(s, rig.FeedCell, PipePieceKind.Pipe, 0, 0);
            F.Resync(s);
            if (partsEach > 0)
            {
                rig.SourceW = F.TestSource(s, F.At(p, -7, 0), "megastructure_part", partsEach);
                rig.SourceS = F.TestSource(s, F.At(p, 0, -7), "megastructure_part", partsEach);
            }
            if (liters > 0)
            {
                rig.Feed = PipeNetworkService.Kernel.AddProducer(rig.FeedCell.X, rig.FeedCell.Y, PipeNetworkService.FluidId("coolant"), liters * 1000L, liters * 1000L);
            }
            SetPower(rig.Site, powered);
            F.Resync(s);
            return rig;
        }

        internal static void SetPower(BuildingRecord b, bool on) => b.PowerState = on ? BuildingPowerState.Powered : BuildingPowerState.Unpowered;

        private static void StopSources(Rig r)
        {
            foreach (int id in new[] { r.SourceW, r.SourceS })
            {
                if (id >= 0)
                {
                    BeltNetworkService.Kernel.TakeSourcePending(id);
                }
            }
            if (r.Feed >= 0)
            {
                PipeNetworkService.Kernel.RemoveProducer(r.Feed, out _);
                r.Feed = -1;
            }
        }

        private static MegastructureState M(CampaignState s) => MegastructureService.StateOf(s);

        private static string MegaLine(CampaignState s)
        {
            MegastructureState m = M(s);
            return $"阶段 {m.StageIndex}，构件 {m.SolidDelivered}，冷却液 {m.FluidDeliveredMl / 1000} 升，供电 {m.PoweredTicks}/{PowerNeed} 步，完工 [{string.Join(",", m.Done.Select(d => d.Id))}]";
        }

        private static bool StepUntil(Func<bool> done, long maxTicks)
        {
            long end = GameClock.Ticks + maxTicks;
            while (GameClock.Ticks < end)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany((int)Math.Min(Hz / 2, end - GameClock.Ticks));
            }
            return done();
        }

        private static int CountOf(string typeId) => NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == typeId).Sum(e => e.Count);

        private static RaidPlanRecord LatestMegaPlan(CampaignState s, int fromSerial) =>
            RaidDirectorService.Plans(s).Where(p => p != null && p.Serial >= fromSerial && Array.IndexOf(p.Triggers ?? Array.Empty<string>(), "megastructure") >= 0)
                .OrderBy(p => p.Serial).LastOrDefault();

        /// <summary>预置当前阶段的进度（测试捷径：跳过已由 C 段真实走过的长时间送料，直接从“差一点就够”开始）。</summary>
        private static void Prefill(CampaignState s, long solidLeft, long fluidLeftMl, long powerLeftTicks)
        {
            MegastructureState m = M(s);
            MegaStageDef st = MegastructureService.Current(s);
            m.SolidDelivered = st.SolidAmount - solidLeft;
            m.FluidDeliveredMl = st.FluidMl - fluidLeftMl;
            m.PoweredTicks = MegastructureService.PowerTicksFor(st) - powerLeftTicks;
        }

        /// <summary>正式的任务完成入口（CampaignObjectiveTracker.CompleteQuest，私有；反射调用）：主线“巨构蓝图”完成 → 发蓝图（事件账本）→ 资料库 → 第三幕。</summary>
        internal static void CompleteMegaQuest(CampaignState s)
        {
            GameConfig.fg.Quest q = QuestCatalog.Get("main.megastructure");
            var r = new ObjectiveRecord { ObjectiveId = "main.megastructure", DefId = "main.megastructure", State = ObjectiveState.Active };
            MethodInfo mi = typeof(CampaignObjectiveTracker).GetMethod("CompleteQuest", BindingFlags.NonPublic | BindingFlags.Static);
            CampaignFgStateDomains.EnsureAll(s);
            mi.Invoke(null, new object[] { s, q, r, GameClock.Ticks });
        }

        private static string FirstDiff(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < n && a[i] == b[i])
            {
                i++;
            }
            int from = Math.Max(0, i - 80);
            return $"第 {i} 字符：…{a.Substring(from, Math.Min(160, a.Length - from))}… ≠ …{b.Substring(from, Math.Min(160, b.Length - from))}…";
        }

        // ── A 数据 ────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            IReadOnlyList<MegaStageDef> st = MegastructureService.Stages;
            bool five = st.Count == 5 && st.Select(x => x.Id).SequenceEqual(new[] { "anchor", "energy", "antenna", "orbit", "ignition" })
                        && st.Select(x => x.Name).SequenceEqual(new[] { "地基锚定", "能源矩阵", "信号天线阵", "轨道校准", "信标点火" });
            bool initial = st.All(x => x.Solid.Id == "megastructure_part" && x.SolidAmount == 400 && x.Fluid.Id == "coolant" && x.FluidMl == 2000L * 1000L && Math.Abs(x.PowerHours - 2f) < 1e-4f);
            bool raids = st.Select(x => x.RaidLevel).Zip(st.Select(x => x.RaidLevel).Skip(1), (a, b) => b >= a).All(x => x) && st[0].RaidLevel >= 2;
            bool grid = GridContent.TryGetBuilding(Site, out GameConfig.fg.BuildingGrid g) && g.FootprintW == 12 && g.FootprintH == 12 && g.MaxCount == 1 && g.Placeable == 1
                        && g.UnlockRule == "blueprint:megastructure_blueprint" && g.Category == "endgame";
            bool power = HomeValleyLayout.PowerProfile.TryGetValue(Site, out (float PowerDemand, int PowerPriority) prof) && Mathf.Approximately(prof.PowerDemand, 500f);
            bool prod = ProducerCatalog.TryGet(Site, out ProducerDef pd) && pd.Mode == ProducerMode.Megastructure && pd.FluidPorts.Count == 1 && pd.FluidPorts[0].Fluid?.Id == "coolant"
                        && GridContent.PortsOf(Site).Count(p => p.Role == "prod" && p.Kind == "in") == 2;
            bool raidRule = RaidCatalog.TryGetTrigger(RaidCatalog.TriggerMegastructure, out RaidTriggerDef def) && def.Exempt && def.LevelRule == "tier_up";
            bool nodes = NarrativeCatalog.TryGetByTrigger("blueprint:megastructure_blueprint", out NarrativeEntryDef bp) && bp.SetAct == 3 && bp.Act == 3 && bp.Node
                         && st.All(x => NarrativeCatalog.TryGetByTrigger("mega:" + x.Id, out NarrativeEntryDef d) && d.Node && d.Act == 3);
            var keys = new List<string> { "building.megastructure_site.name", "building.megastructure_site.desc", "mega.unlock_hint", "prod.place.megastructure", "mega.panel.header",
                "mega.panel.solid", "mega.panel.fluid", "mega.panel.power", "mega.panel.eta", "mega.panel.eta_unknown", "mega.panel.paused", "mega.panel.destroyed", "mega.notify.stage_done",
                "mega.notify.paused", "mega.notify.starved", "mega.reason.wounded", "mega.reason.starved", "codex.mega.site.title", "codex.mega.site.body" };
            keys.AddRange(st.SelectMany(x => new[] { x.NameKey, x.DescKey }));
            var missing = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                missing.AddRange(keys.Where(k => !GameText.Has(k) || string.IsNullOrEmpty(GameText.Get(k))).Select(k => lang + ":" + k));
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool problems = MegastructureService.Problems.Count == 0 && ProducerCatalog.Problems.Count == 0;
            Expect(five && initial && raids && grid && power && prod && raidRule && nodes && missing.Count == 0 && problems,
                $"A1 数据：五个阶段按顺序（{string.Join(" → ", st.Select(x => x.Name))}）{five}；每阶段 巨构构件 400、冷却液 2,000 升、持续供电 2 游戏小时（FG11 第 13 节初值）{initial}；" +
                $"完工突袭等级 {string.Join("/", st.Select(x => x.RaidLevel))} 不降 {raids}；工地 12×12、只能一座、巨构蓝图解锁、终局页 {grid}；耗电 500 {power}；生产模式 megastructure、两个物品口、一个冷却液口 {prod}；" +
                $"巨构突袭不受最短间隔、等级 tier_up {raidRule}；舰队消息（蓝图 → 第三幕、五条阶段消息）{nodes}；文本中英齐全 {missing.Count == 0}{(missing.Count > 0 ? "（缺 " + string.Join("、", missing) + "）" : string.Empty)}；表无问题 {problems}");
        }

        // ── B 解锁（DEBT-FG8QST01-03）─────────────────────────────────────────────

        private static void CheckUnlock()
        {
            CampaignState s = NewWorld(8301);
            GridCell? p = FindSitePivot(s);
            if (p == null)
            {
                Fail("B1 找不到放工地的空地");
                return;
            }
            GridPlacementResult before = HomeGridService.ValidatePlacement(s, Site, p.Value, 0, asPlayerPlacement: true, checkCost: false);
            bool locked = !before.Ok && before.Reasons.Any(r => r.Code == GridBlockReason.Locked) && !MegastructureService.HasBlueprint(s);
            string lockedText = string.Join(" / ", before.Reasons.Select(r => r.Describe()));
            int act0 = s.Progress.Act;
            CompleteMegaQuest(s);
            bool granted = MegastructureService.HasBlueprint(s) && NarrativeService.Has(s, "node.mega_blueprint") && s.Progress.Act == 3;
            GridPlacementResult after = HomeGridService.ValidatePlacement(s, Site, p.Value, 0, asPlayerPlacement: true, checkCost: false);
            bool open = !after.Reasons.Any(r => r.Code == GridBlockReason.Locked);
            BuildingRecord b = F.Built(s, Site, "b1", p.Value);
            GridPlacementResult second = HomeGridService.ValidatePlacement(s, Site, F.At(p.Value, 30, 30), 0, asPlayerPlacement: true, checkCost: false);
            bool maxOne = second.Reasons.Any(r => r.Code == GridBlockReason.MaxCount);
            // 重复完成（重新开始后的同一任务）不重复发蓝图、不重复收资料库条目。
            int narr = NarrativeService.Count(s);
            CompleteMegaQuest(s);
            bool once = NarrativeService.Count(s) == narr;
            Expect(locked && granted && open && b != null && maxOne && once,
                $"B1 有巨构蓝图才能开工（DEBT-FG8QST01-03）：没有蓝图时放置被拦，原因“{lockedText}”{locked}；主线“巨构蓝图”完成（正式入口）→ 事件账本有蓝图、资料库收到“巨构蓝图”舰队消息、" +
                $"战役从第 {act0} 幕进入第 {s.Progress.Act} 幕 {granted}；之后放置不再被锁 {open}；只能建一座（第二座拦 MaxCount）{maxOne}；同一任务再完成不重复投放 {once}");
        }

        // ── C 正常推进一个阶段（FGT-END-002）────────────────────────────────────────

        private static void CheckStageOne()
        {
            CampaignState s = NewWorld(8302);
            Rig rig = Lay(s, "c1", 220, 3000);
            if (rig == null)
            {
                Fail("C1 找不到放工地的空地");
                return;
            }
            int serial0 = RaidDirectorService.StateOf(s)?.NextPlanSerial ?? 0;
            int done0 = CountOf("mega_stage_done");
            long prevSolid = 0, prevFluid = 0, prevPower = 0;
            bool monotonic = true;
            double eta = -2;
            string panelMid = null;
            long windowTicks = GameClock.TicksFor(GridContent.Tuning("mega.rate_window_seconds"));
            bool done = StepUntil(() =>
            {
                MegastructureState m = M(s);
                if (m.StageIndex > 0)
                {
                    return true;
                }
                monotonic &= m.SolidDelivered >= prevSolid && m.FluidDeliveredMl >= prevFluid && m.PoweredTicks >= prevPower;
                prevSolid = m.SolidDelivered;
                prevFluid = m.FluidDeliveredMl;
                prevPower = m.PoweredTicks;
                if (eta == -2 && m.RateWindows >= 1 && m.SolidDelivered > 0)
                {
                    eta = MegastructureService.StageSecondsLeft(s, true);
                    panelMid = MegastructureService.PanelText(s, rig.Site, true);
                }
                return false;
            }, 40L * 60 * Hz);
            MegastructureState mm = M(s);
            // 物资守恒：两个测试源送出的件 = 计入阶段的 400 + 工地缓存 + 带上 / 口里还没进的。
            int leftW = BeltNetworkService.Kernel.TryGetPortCounts(rig.SourceW, out int lw, out _) ? lw : -1;
            int leftS = BeltNetworkService.Kernel.TryGetPortCounts(rig.SourceS, out int ls, out _) ? ls : -1;
            ProductionService.Producer pr = F.P(s, rig.Site);
            int inBuffer = ProductionService.Count(pr.Rec.In, "megastructure_part");
            int onBelts = F.OnBelts(F.At(rig.Pivot, -7, 0), BeltDir.East, 2) + F.OnBelts(F.At(rig.Pivot, 0, -7), BeltDir.North, 2)
                          + F.PortPending(s, rig.Site, Site + ".in0") + F.PortPending(s, rig.Site, Site + ".in1");
            int sent = 440 - Math.Max(0, leftW) - Math.Max(0, leftS);
            long nextCounted = mm.SolidDelivered;
            bool conserved = leftW >= 0 && leftS >= 0 && sent == 400 + inBuffer + onBelts + (int)nextCounted;
            WorldSimulation.StepMany(3);
            RaidPlanRecord plan = LatestMegaPlan(s, serial0);
            bool raid = plan != null && plan.Exempt && plan.Level >= Stage0.RaidLevel;
            bool message = NarrativeService.Has(s, "node.mega.anchor");
            bool notified = CountOf("mega_stage_done") == done0 + 1;
            bool record = mm.Done.Length == 1 && mm.Done[0].Id == "anchor" && mm.StageIndex == 1 && mm.SolidDelivered < 400 && mm.PoweredTicks < PowerNeed;
            bool etaOk = eta > 0 && eta < 3600 && panelMid != null && panelMid.Contains("地基锚定") && panelMid.Contains("巨构构件") && panelMid.Contains("冷却液") && panelMid.Contains("持续供电")
                         && panelMid.Contains("预计还要");
            Expect(done && monotonic && conserved && raid && message && notified && record && etaOk,
                $"C1 FGT-END-002 正常推进第一阶段（传送带从西口 / 南口送巨构构件、管线从东口送冷却液、持续有电）：在 {GameClock.Ticks / Hz} 游戏秒内完工 {done}，三项只增不减 {monotonic}；" +
                $"构件守恒（送出 {sent} = 计入 400 + 缓存 {inBuffer} + 带上 / 口里 {onBelts} + 下一阶段已计 {nextCounted}）{conserved}；一个统计窗口（{windowTicks} 步）后按当前供给速度预计本阶段还要 {eta:0} 游戏秒、" +
                $"面板写三项进度与预计时间 {etaOk}；完工 → 突袭导演排出巨构突袭（{(plan == null ? "无" : D(plan))}）{raid}、舰队阶段消息进资料库 {message}、通知 {notified}、记完工并进入第二阶段（{MegaLine(s)}）{record}" +
                (etaOk ? string.Empty : "\n    面板：" + panelMid?.Replace("\n", " / ")));
        }

        private static string D(RaidPlanRecord p) => $"{p.PlanId} 触发 {string.Join("+", p.Triggers)} {p.Level} 级 {p.Faction}";

        // ── D 五个阶段全部完工 ────────────────────────────────────────────────────

        private static void CheckAllStages()
        {
            CampaignState s = NewWorld(8303);
            Rig rig = Lay(s, "d1", 300, 6000);
            if (rig == null)
            {
                Fail("D1 找不到放工地的空地");
                return;
            }
            var levels = new List<int>();
            var messages = new List<string>();
            int allEvents = 0;
            var stageEvents = new List<string>();
            Action<string> onStage = id => stageEvents.Add(id);
            Action onAll = () => allEvents++;
            GameEvent.AddEventListener<string>(MegastructureService.StageCompletedEvent, onStage);
            GameEvent.AddEventListener(MegastructureService.AllCompletedEvent, onAll);
            bool ok = true;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    int serial0 = RaidDirectorService.StateOf(s).NextPlanSerial;
                    Prefill(s, 2, 50_000, Hz);
                    bool done = StepUntil(() => M(s).StageIndex > i, 30L * Hz);
                    WorldSimulation.StepMany(3);
                    RaidPlanRecord p = LatestMegaPlan(s, serial0);
                    levels.Add(p?.Level ?? -1);
                    MegaStageDef st = MegastructureService.Stages[i];
                    string body = NarrativeCatalog.TryGetByTrigger("mega:" + st.Id, out NarrativeEntryDef nd) ? GameText.Get(nd.BodyKey) : string.Empty;
                    messages.Add(body);
                    ok &= done && p != null && NarrativeService.Has(s, nd.Id);
                }
            }
            finally
            {
                GameEvent.RemoveEventListener<string>(MegastructureService.StageCompletedEvent, onStage);
                GameEvent.RemoveEventListener(MegastructureService.AllCompletedEvent, onAll);
            }
            bool nonDecreasing = levels.Zip(levels.Skip(1), (a, b) => b >= a).All(x => x) && levels.All(l => l >= 2) && levels.Last() == 4;
            // 语气按节点表：第一条热情（感叹），越往后越像回执（不再有感叹号，最后一条是“回执”）。
            bool tone = messages.Count == 5 && messages[0].Contains("！") && !messages[3].Contains("！") && !messages[4].Contains("！") && messages[4].Contains("回执");
            MegastructureState m = M(s);
            bool allDone = MegastructureService.AllDone(s) && m.Done.Length == 5 && allEvents == 1 && stageEvents.SequenceEqual(MegastructureService.Stages.Select(x => x.Id));
            // 全部完工后不再收料：物品口不收、缓存不再进。
            ProductionService.Producer pr = F.P(s, rig.Site);
            int buf0 = ProductionService.Count(pr.Rec.In, "megastructure_part");
            WorldSimulation.StepMany(5 * Hz);
            F.Resync(s);
            WorldSimulation.StepMany(5 * Hz);
            bool closed = ProductionService.SinkAcceptFor(s, rig.Site, Site + ".in0") == BeltConst.AcceptNone && ProductionService.Count(pr.Rec.In, "megastructure_part") <= buf0
                          && pr.Reason == ProdReason.MegaDone && MegastructureService.PanelText(s, rig.Site, true).Contains("全部完工");
            Expect(ok && nonDecreasing && tone && allDone && closed,
                $"D1 五个阶段依次完工（每阶段从“差一点”开始真实走完最后一段）：每个阶段都排出巨构突袭并收到舰队消息 {ok}；突袭等级 {string.Join("/", levels)}（不降、至少 2、最后 4 级，FGR-END-002）{nonDecreasing}；" +
                $"舰队语气按节点表从热情变成程式化（“{messages.FirstOrDefault()?.Substring(0, Math.Min(12, messages.FirstOrDefault()?.Length ?? 0))}…” → “{messages.LastOrDefault()?.Substring(0, Math.Min(16, messages.LastOrDefault()?.Length ?? 0))}…”）{tone}；" +
                $"全部完工、阶段事件按顺序、信标点火事件一次（揭示 FG13-END-02 订阅它）{allDone}；完工后物品口不再收、面板写全部完工 {closed}");
        }

        // ── N 负向 ────────────────────────────────────────────────────────────────

        private static void CheckWounded()
        {
            CampaignState s = NewWorld(8304);
            Rig rig = Lay(s, "n1", 220, 3000);
            if (rig == null)
            {
                Fail("N1 找不到放工地的空地");
                return;
            }
            StepUntil(() => M(s).SolidDelivered >= 20 && M(s).FluidDeliveredMl > 0, 120L * Hz);
            int pause0 = CountOf("mega_alert");
            BuildingOps.ApplyDamage(s, rig.Site.BuildingId, 100f);
            WorldSimulation.StepMany(2);
            string before = MegaLine(s);
            WorldSimulation.StepMany(20 * Hz);
            string during = MegaLine(s);
            ProductionService.Producer pr = F.P(s, rig.Site);
            bool paused = before == during && pr.Reason == ProdReason.MegaWounded && CountOf("mega_alert") == pause0 + 1
                          && MegastructureService.PanelText(s, rig.Site, true).Contains("工地受损");
            string reason = ProductionService.ReasonText(s, pr);
            // 复审 P1：受损（还活着）的统一状态是“受损”，不是“已摧毁”（建造模式悬停 / 状态行都读它）。
            BuildingStatusKind woundedKind = BuildingStatusService.Evaluate(s, rig.Site).Kind;
            paused &= woundedKind == BuildingStatusKind.Damaged;
            // 维修好（耐久回满）→ 自动继续，从原来的数接着加。
            rig.Site.Health = BuildingOps.MaxDurability(Site);
            long solidAt = M(s).SolidDelivered;
            WorldSimulation.StepMany(20 * Hz);
            bool resumed = M(s).SolidDelivered > solidAt && pr.Reason != ProdReason.MegaWounded;
            Expect(paused && resumed && reason.Contains("暂停"),
                $"N1 负向“工地受损”（耐久被打掉 100）：之后 20 游戏秒三项都不动、不倒退（{before}）{paused}，状态写“{reason}”、通知一次；统一状态 {woundedKind}（应为 Damaged）；维修回满后自动继续（构件 {solidAt} → {M(s).SolidDelivered}）{resumed}");
        }

        private static void CheckDestroyedAndRebuilt()
        {
            CampaignState s = NewWorld(8305);
            Rig rig = Lay(s, "n2", 220, 3000);
            if (rig == null)
            {
                Fail("N2 找不到放工地的空地");
                return;
            }
            StepUntil(() => M(s).SolidDelivered >= 20 && M(s).FluidDeliveredMl > 0, 120L * Hz);
            WorldSimulation.StepMany(2 * Hz);
            BuildingOps.ApplyDamage(s, rig.Site.BuildingId, 1_000_000f);
            WorldSimulation.StepMany(2);
            string before = MegaLine(s);
            WorldSimulation.StepMany(20 * Hz);
            bool destroyed = rig.Site.ConstructionState == BuildingConstructionState.Damaged && MegaLine(s) == before
                             && MegastructureService.PanelText(s, rig.Site, true).Contains("被摧毁")
                             && BuildingStatusService.Evaluate(s, rig.Site).Kind == BuildingStatusKind.Destroyed;
            // 被摧毁的工地拆掉（缓存按正式拆除入口退回），在原地重建一座：进度在战役状态里，从原来的数接着做。
            int returned = ProductionService.OnDemolished(s, rig.Site, "mega-test");
            s.BuildingRecords = s.BuildingRecords.Where(b => b != rig.Site).ToArray();
            HomeGridService.Invalidate();
            F.Resync(s);
            BuildingRecord rebuilt = F.Built(s, Site, "n2b", rig.Pivot);
            rebuilt.Health = BuildingOps.MaxDurability(Site);
            SetPower(rebuilt, true);
            F.Resync(s);
            bool sameProgress = MegaLine(s) == before;
            long solidAt = M(s).SolidDelivered;
            StepUntil(() => M(s).SolidDelivered > solidAt, 60L * Hz);
            bool resumed = M(s).SolidDelivered > solidAt && M(s).StageIndex == 0;
            Expect(destroyed && sameProgress && resumed,
                $"N2 负向“工地被摧毁”：被摧毁后三项停在原处（{before}）、面板写被摧毁 {destroyed}；拆掉（退回缓存 {returned} 件）后原地重建，进度不倒退 {sameProgress}，接着送料继续推进 {resumed}（FG11 第 9 节“重建后继续”）");
        }

        private static void CheckSupplyCut()
        {
            CampaignState s = NewWorld(8306);
            Rig rig = Lay(s, "n3", 60, 3000); // 120 件不够一个阶段：断供时构件还没送够
            if (rig == null)
            {
                Fail("N4 找不到放工地的空地");
                return;
            }
            long window = GameClock.TicksFor(GridContent.Tuning("mega.rate_window_seconds"));
            StepUntil(() => M(s).RateWindows >= 1 && M(s).SolidMilliPerMinute > 0 && M(s).FluidMlPerMinute > 0, 4 * window);
            int alert0 = CountOf("mega_alert");
            StopSources(rig);
            // 带上 / 口里 / 缓存里剩的先送完，再整整两个统计窗口没有任何送入。
            WorldSimulation.StepMany((int)(3 * window));
            string held = MegaLine(s);
            WorldSimulation.StepMany((int)window);
            ProductionService.Producer pr = F.P(s, rig.Site);
            MegastructureState m = M(s);
            bool cut = MegaLine(s) == held && m.SolidStarvedNotified && m.FluidStarvedNotified && CountOf("mega_alert") >= alert0 + 2
                       && MegastructureService.StageSecondsLeft(s, true) < 0 && MegastructureService.PanelText(s, rig.Site, true).Contains("供给中断");
            // 只剩供电在计时：状态仍是推进中；把电也断掉 → 状态写缺电。
            SetPower(rig.Site, false);
            WorldSimulation.StepMany(Hz);
            bool starvedState = pr.State == ProdState.NoPower || pr.State == ProdState.MissingInput;
            SetPower(rig.Site, true);
            // 恢复供给 → 接着加，中断标记清掉。
            BeltNetworkService.Kernel.AddSourceItems(rig.SourceW, 50); // 同一个上游重新来货
            long solidAt = m.SolidDelivered;
            int alertResume = CountOf("mega_alert");
            WorldSimulation.StepMany((int)(4 * window));
            // 来货后中断标记清掉；这 50 件送完又断 → 再通知一次（每次中断通知一次，不刷屏也不漏）。
            bool resumed = m.SolidDelivered == solidAt + 50 && CountOf("mega_alert") == alertResume + 1 && m.SolidStarvedNotified;
            Expect(cut && starvedState && resumed,
                $"N4 负向“物资供给中断”（构件源与冷却液同时断）：进度停在原处不倒退（{held}）、构件与冷却液各通知一次、预计完成时间改为“要等供给恢复”、面板写供给中断 {cut}；" +
                $"再断电状态写缺电 / 缺料（{pr.State}）{starvedState}；恢复 50 件构件后接着推进（{solidAt} → {m.SolidDelivered}）、来货清掉中断标记、送完再断再通知一次 {resumed}");
        }

        private static void CheckPower()
        {
            CampaignState s = NewWorld(8307);
            Rig rig = Lay(s, "n5", 220, 3000);
            if (rig == null)
            {
                Fail("N5 找不到放工地的空地");
                return;
            }
            // 真实电网：开局只有核心旁的小发电机，供不上 500 → 工地不是“有电”（持续供电 500 不能靠 80 的电网凑数）。
            HomeValleyPowerGrid.Recompute(s);
            bool gridShort = rig.Site.PowerState != BuildingPowerState.Powered;
            SetPower(rig.Site, false);
            long p0 = M(s).PoweredTicks;
            long s0 = M(s).SolidDelivered;
            WorldSimulation.StepMany(30 * Hz);
            bool frozen = M(s).PoweredTicks == p0 && M(s).SolidDelivered > s0 && MegastructureService.PowerSecondsLeft(s, false) < 0
                          && MegastructureService.PanelText(s, rig.Site, false).Contains("缺电");
            SetPower(rig.Site, true);
            WorldSimulation.StepMany(30 * Hz);
            long gained = M(s).PoweredTicks - p0;
            bool counts = Math.Abs(gained - 30 * Hz) <= 3;
            Expect(gridShort && frozen && counts,
                $"N5 持续供电：真实电网供不上 500 时工地没电 {gridShort}；缺电 30 游戏秒供电计时不动、物资照常送入、预计时间写缺电 {frozen}；来电后供电计时与游戏时间一比一（30 秒计 {gained} 步）{counts}");
        }

        // ── T 暂停与倍速、O 观察一致 ────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld, out int frames)
        {
            CampaignState s = NewWorld(seed, observe);
            pausedHeld = true;
            frames = 0;
            Rig rig = Lay(s, "tm", 220, 3000);
            if (rig == null)
            {
                return "no-rig";
            }
            WorldSimulation.StepMany(10 * Hz);
            // 从“差一点”开始：倍速段里真实跨过一次完工（突袭排定、舰队消息、进入下一阶段）。
            Prefill(s, 5, 100_000, 15 * Hz);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                WorldSimulation.SyncAllForSave();
                string p0 = JsonUtility.ToJson(s);
                long t0 = GameClock.Ticks;
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                WorldSimulation.SyncAllForSave();
                pausedHeld = JsonUtility.ToJson(s) == p0 && GameClock.Ticks == t0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + 40 * Hz;
            while (GameClock.Ticks < target && frames < 60 * 600)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            WorldSimulation.SyncAllForSave();
            ProducerRecord rec = F.P(s, rig.Site).Rec;
            string plans = string.Join(";", RaidDirectorService.Plans(s).Select(p => $"{p.Trigger}:{p.Level}:{p.Faction}:{p.State}"));
            return "mega=" + JsonUtility.ToJson(M(s)) + "|in=" + JsonUtility.ToJson(rec) + "|plans=" + plans + "|narr=" + string.Join(",", s.Progress.Narrative.Select(n => n.Id + "@" + n.Tick));
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true, paused = true;
            string diff = string.Empty;
            var shown = new List<string>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(8310, true, speed, true, out bool held, out int frames);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"{speed}x 与 0.5x 不同：" + FirstDiff(reference, snap);
                }
                same &= snap == reference;
                shown.Add($"{speed}x {frames} 帧");
            }
            bool crossed = reference != null && reference.Contains("\"StageIndex\":1") && reference.Contains("megastructure:") && reference.Contains("node.mega.anchor");
            Expect(paused && same && crossed,
                $"T1 暂停与 0.5x～3x（暂停 {paused}、逐字段一致 {same}、期间跨过一次完工 {crossed}）：暂停中 120 帧整份战役状态不动；{string.Join(" / ", shown)} 跑同样的 40 游戏秒，" +
                "工地进度、统计窗口、缓存、突袭计划、资料库逐字段一致" + (diff.Length == 0 ? string.Empty : "\n" + diff));
        }

        private static void CheckObserved()
        {
            string seen = RunScenario(8311, true, 1f, false, out _, out _);
            string unseen = RunScenario(8311, false, 1f, false, out _, out _);
            Expect(seen == unseen && seen != "no-rig" && seen.Contains("\"StageIndex\":1"),
                "O1 家园在观察与不观察时跨过一次巨构完工（物资送入、供电计时、突袭排定、舰队消息）结果逐字段一致（FGR-BASE-021）" + (seen == unseen ? string.Empty : "\n" + FirstDiff(seen, unseen)));
        }

        // ── S 真文件存读档 ─────────────────────────────────────────────────────────

        private static string Snap(CampaignState s, string siteId)
        {
            ProducerRecord rec = ProductionService.TryGet(s, siteId, out ProductionService.Producer p) ? p.Rec : null;
            return $"t={GameClock.Ticks}|mega={JsonUtility.ToJson(MegastructureService.StateOf(s))}|in={(rec == null ? "-" : string.Join(",", rec.In.Select(x => x.ItemId + "x" + x.Amount)))}|act={s.Progress.Act}" +
                   $"|narr={string.Join(",", s.Progress.Narrative.Select(n => n.Id))}";
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(8308);
            CompleteMegaQuest(s);
            // 存档前电网是真实结算（供不上 500 → 没电），读档后同样没电：两边一致，供电计时只来自存前预置的进度。
            Rig rig = Lay(s, "s1", 220, 3000, powered: false);
            if (rig == null)
            {
                Fail("S1 找不到放工地的空地");
                return;
            }
            HomeValleyPowerGrid.Recompute(s);
            StepUntil(() => M(s).SolidDelivered >= 30 && M(s).RateWindows >= 1, 300L * Hz);
            M(s).PoweredTicks = 1234; // 已累计的持续供电（存档要带着）
            M(s).Done = new[] { new MegaStageDoneRecord { Id = "anchor", Tick = 1, Day = 1 } };
            M(s).StageIndex = 1;
            string before = Snap(s, rig.Site.BuildingId);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long until = GameClock.Ticks + 60 * Hz;
            void Continue() => WorldSimulation.StepMany((int)(until - GameClock.Ticks));
            Continue();
            string continuous = Snap(s, rig.Site.BuildingId);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            if (!rr.Success)
            {
                Fail("S1 读档失败：" + rr.Message);
                return;
            }
            CampaignState l = rr.State;
            CampaignSession.Set(Slot, l);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            string loaded = Snap(l, rig.Site.BuildingId);
            Continue();
            string after = Snap(l, rig.Site.BuildingId);
            // 旧存档没有返航工程（Mega = null）：补成第一阶段、全部为 0。
            var old = new CampaignState { Progress = new CampaignProgressState { Mega = null } };
            CampaignFgStateDomains.EnsureAll(old);
            bool legacy = old.Progress.Mega != null && old.Progress.Mega.StageIndex == 0 && old.Progress.Mega.Done.Length == 0;
            // 复审 P2：进入战役对已有“巨构蓝图”节点的档什么也不改；返航工程上线前就拿到蓝图的旧档（资料库没有该节点、还在第二幕）→ 补投节点、进第三幕。
            string narrBefore = string.Join(",", l.Progress.Narrative.Select(n => n.Id)) + "|act=" + l.Progress.Act;
            NarrativeService.OnCampaignEntered(l);
            bool enterNoop = narrBefore == string.Join(",", l.Progress.Narrative.Select(n => n.Id)) + "|act=" + l.Progress.Act;
            l.Progress.Narrative = l.Progress.Narrative.Where(n => n.Id != "node.mega_blueprint").ToArray();
            l.Progress.Act = 2;
            NarrativeService.OnCampaignEntered(l);
            bool oldBlueprint = NarrativeService.Has(l, "node.mega_blueprint") && l.Progress.Act == 3;
            legacy &= enterNoop && oldBlueprint;
            bool ok = save.Success && loaded == before && after == continuous && legacy;
            Expect(ok, $"S1 推进中真文件存读档（阶段、三项进度、已累计供电、统计窗口与速度、中断 / 暂停标记、已完工、缓存、第三幕、资料库）：读回与存前一致 {loaded == before}，接着跑与一直跑一致 {after == continuous}；" +
                       $"旧存档补成第一阶段、进入战役不改已有节点 {enterNoop}、早于本卡拿到蓝图的旧档补投“巨构蓝图”并进第三幕 {oldBlueprint}：{legacy}\n    存前 {before}" + (loaded == before ? string.Empty : "\n    读回 " + loaded) +
                       (after == continuous ? string.Empty : "\n    一直跑 " + continuous + "\n    读档后 " + after));
        }

        // ── U 工地面板 ─────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            CampaignState s = NewWorld(8309);
            Rig rig = Lay(s, "u1", 220, 3000);
            if (rig == null)
            {
                Fail("U1 找不到放工地的空地");
                return;
            }
            long window = GameClock.TicksFor(GridContent.Tuning("mega.rate_window_seconds"));
            StepUntil(() => M(s).RateWindows >= 1 && M(s).SolidDelivered > 0, 3 * window);
            VisualElement proot = F.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject pgo);
            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
            string detail = string.Empty, progress = string.Empty, state = string.Empty, en = string.Empty;
            bool panelOk = false;
            try
            {
                ProductionPanelUIToolkit panel = pgo.AddComponent<ProductionPanelUIToolkit>();
                panel.BindView(proot);
                ProductionPanelUIToolkit.Open(rig.Site.BuildingId);
                panel.Refresh();
                detail = panel.DetailText;
                progress = panel.ProgressText;
                state = panel.StateText;
                panelOk = panel.PanelVisible && detail.Contains("第 1/5 阶段") && detail.Contains("地基锚定") && detail.Contains("巨构构件") && detail.Contains("/ 400")
                          && detail.Contains("冷却液") && detail.Contains("/ 2000 升") && detail.Contains("持续供电") && detail.Contains("预计还要") && detail.Contains("至少 2 级")
                          && progress.Contains("本阶段进度") && state.Length > 0;
                GameSettings.SetLanguage(GameLanguage.En);
                panel.Refresh();
                en = panel.DetailText;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
            finally
            {
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.Close();
                Object.DestroyImmediate(pgo);
            }
            bool english = en.Contains("Return project") && en.Contains("Megastructure part") && !en.Contains("阶段");
            bool hooks = GameSettings.HasSeenGuidanceHook(GuidanceHooks.MegaFirstSite);
            Expect(panelOk && english && hooks,
                $"U1 工地面板（真 UXML 生产面板）：“{detail.Replace("\n", " / ")}”“{progress}”状态“{state}” {panelOk}；英文 {english}；第一次放下工地的引导钩子 {hooks}");
        }

        // ── P 性能 ─────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(8312);
            Rig rig = Lay(s, "p1", 220, 3000);
            if (rig == null)
            {
                Fail("P1 找不到放工地的空地");
                return;
            }
            WorldSimulation.StepMany(5 * Hz);
            ProductionService.Producer pr = F.P(s, rig.Site);
            var sw = Stopwatch.StartNew();
            const int n = 20000;
            for (int i = 0; i < n; i++)
            {
                MegastructureService.Accrue(s, rig.Site, 0, 0, 0, 3);
            }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            ProductionService.ResetStepStats();
            WorldSimulation.StepMany(10 * Hz);
            double stepMax = ProductionService.MaxStepMs;
            PerfLines.Add($"MegastructureService.Accrue 每次 {us:0.###} 微秒（{n} 次平均，O(1)）；有工地时生产步最慢 {stepMax:0.###} 毫秒（Editor batchmode）");
            Expect(us < 50.0 && pr != null, $"P1 工地每个生产步的结算是 O(1)：Accrue 平均 {us:0.###} 微秒（阈值 50 微秒）");
        }

        // ── 断言 ─────────────────────────────────────────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgMegastructureSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
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
