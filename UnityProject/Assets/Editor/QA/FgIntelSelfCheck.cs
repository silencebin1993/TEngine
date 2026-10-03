using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
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
using GameLogic.UI.Kit;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG5-RND-05 监听站与情报的自动验收（FG05 FGR-RND-050～052；FG13 FGU-25；FGT-RND-008；负向“监听站被摧毁时正在破译的情报”“情报过期”“没有任何情报时的空状态”）。
    /// 全部起真实系统：真实家园（世界模拟、电网、监听站建筑、行进中的突袭部队沿地形寻路）、真文件存读档、真 UXML 面板与战略地图。
    /// A 数据（四张新表与源数据逐字段、初值、研究门槛与关键材料、文本中英、钩子、图鉴、快捷键、通知类型）；
    /// B FGR-RND-052 多座递减（1 座 / 4 座实测出情报的时间；第 4 座不提速；缺电 / 禁用的不算）；
    /// C FGR-RND-050 五类情报（反制预览 = 出发锁定同一算法；首领弱点要先遭遇、击败后不再破译且旧的标“目标已不在”；天气后续版本开放；舰队片段第二幕起按顺序、只截获一次；
    ///   优先级插队时原进度保留）；D 有效期与“已过时”（存档里的显式状态、不删除、同一目标的新情报替代、每类保留上限）；
    /// E FGT-RND-008 突袭预报与实际到来的突袭一致（阵营、规模、方向、时间窗口；两个种子、两个出发领地，B25）+ 地图箭头；
    /// F 负向：监听站全部被毁 → 暂停、进度保留、只告一次 → 重建后接着破译；没有监听站不产出；G 真文件存读档；H 暂停与 0.5x～3x；I 观察 / 不观察一致；
    /// J 面板（真 UXML：Y 键开关、空状态、状态与速度构成、五类状态行、列表 / 新 / 已过时、筛选、在地图上查看、布局探针）；K 建筑面板入口与状态；L 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgIntelSelfCheck）；单段入口 <see cref="RunFromMenu"/>。
    /// </summary>
    public static class FgIntelSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const string PanelUxml = UiKitFolder + "IntelPanel.uxml";
        private const string RowUxml = UiKitFolder + "IntelRow.uxml";
        private const string ProdUxml = UiKitFolder + "ProductionPanel.uxml";
        private const string MapUxml = UiKitFolder + "StrategicMap.uxml";
        private const int Slot = 5;
        private const string Post = IntelCatalog.TypeId;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();
        private static readonly List<string> PostIds = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 监听站与情报")]
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
            Line("\n[监听站与情报] 五类情报按时间产出、有效期与已过时、多座递减、突袭预报与实际一致、情报面板与地图箭头（FG5-RND-05）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgintel-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                AnalysisCatalog.Reload();
                IntelCatalog.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                IntelService.ResetSessionState();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；破译 / 有效期 / 面板在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），突袭部队寻路在 AOT 内核；真机另测（FG15-SYS-02）");
                Step(CheckData);
                Step(CheckStacking);
                Step(CheckKinds);
                Step(CheckOutdated);
                Step(CheckRaidForecast);
                Step(CheckInterrupted);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckPanel);
                Step(CheckBuildingPanel);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"情报自检抛异常：{e}");
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
                IntelPanelUIToolkit.InWorldOverrideForTests = false;
                IntelPanelUIToolkit.Close();
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                StrategicMapUIToolkit.Close();
                IntelService.ResetSessionState();
                FusionService.ResetSessionState();
                HomeValleyAnalysis.ResetSessionState();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
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
                ItemDistribution.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                BuildMaterials.ResetForTests();
                HomeInventory.ResetSessionState();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
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
            Line($"  · [监听站与情报] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        /// <summary>新家园：发电运转、<paramref name="posts"/> 座建成并接上电的监听站（按种子地形找空地，不写死坐标，B25）。</summary>
        private static CampaignState NewWorld(int seed, int posts, bool observe = true)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            BuildMaterials.ResetForTests();
            ResearchService.ResetForTests();
            HomeValleyAnalysis.ResetSessionState();
            FusionService.ResetSessionState();
            IntelService.ResetSessionState();
            CampaignState s = F.NewWorld(seed, observe, 600);
            F.PowerUp(s);
            PostIds.Clear();
            AddPosts(s, posts, seed);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void AddPosts(CampaignState s, int n, int seed)
        {
            for (int i = 0; i < n; i++)
            {
                GridCell? at = F.FindFree(s, Post, 6f, 40f);
                if (!at.HasValue)
                {
                    Fail($"种子 {seed}：核心附近找不到放第 {PostIds.Count + 1} 座监听站的空地");
                    return;
                }
                BuildingRecord b = F.Built(s, Post, "post" + seed + "_" + PostIds.Count, at.Value);
                PostIds.Add(b.BuildingId);
            }
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
        }

        private static IntelState I(CampaignState s) => IntelService.StateOf(s);

        private static long ProducedOf(CampaignState s) => I(s)?.Produced ?? 0;

        /// <summary>每 3 步（世界步节拍）走，直到产出数增加；返回用了多少步（-1 = 超时）。</summary>
        private static long TicksUntilProduced(CampaignState s, int maxSeconds)
        {
            long before = ProducedOf(s);
            long t0 = GameClock.Ticks;
            long limit = t0 + (long)maxSeconds * GameClock.StepHz;
            while (GameClock.Ticks < limit)
            {
                WorldSimulation.StepMany(3);
                if (ProducedOf(s) > before)
                {
                    return GameClock.Ticks - t0;
                }
            }
            return -1;
        }

        private static IntelRecord Latest(CampaignState s, string kind) =>
            (I(s)?.Records ?? Array.Empty<IntelRecord>()).Where(r => r.Kind == kind).OrderByDescending(r => r.Serial).FirstOrDefault();

        private static void Seconds(float sec) => F.Seconds(sec);

        private static RegionRecord EncounterFoundryBoss(CampaignState s)
        {
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            RegionRecord r = FoundryOutpostRegion.Find(s);
            r.CoreState = CoreBossState.Shielded.ToString();
            return r;
        }

        private static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Norm(string pyCell)
        {
            return double.TryParse(pyCell, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && pyCell.Contains(".")
                ? d.ToString("0.###", CultureInfo.InvariantCulture)
                : pyCell;
        }

        // ── A 数据 ────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            (int code, string output) = F.RunPython(F.LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            string[] prefixes = { "IK\t", "IS\t", "IB\t", "IF\t" };
            string[] src = output.Replace("\r", string.Empty).Split('\n').Where(l => prefixes.Any(p => l.StartsWith(p, StringComparison.Ordinal)))
                .Select(l => string.Join("\t", l.Split('\t').Select(Norm))).ToArray();
            var rt = new List<string>();
            rt.AddRange(t.TbIntelKind.DataList.Select(r => string.Join("\t", "IK", r.Id, r.NameKey, r.DescKey, r.Glyph, r.Color, r.SortOrder, Fmt(r.DecipherSeconds), Fmt(r.ValiditySeconds),
                r.Condition, r.OpensIn, r.CodexId)));
            rt.AddRange(t.TbBuildingStack.DataList.Select(r => string.Join("\t", "IS", r.Id, r.TypeId, r.Nth, Fmt(r.Bonus))));
            rt.AddRange(t.TbIntelBoss.DataList.Select(r => string.Join("\t", "IB", r.Id, r.NameKey, r.Rule, r.PhasesKey, r.WeaknessKey, r.AdviceKey, r.SortOrder)));
            rt.AddRange(t.TbIntelFragment.DataList.Select(r => string.Join("\t", "IF", r.Id, r.Act, r.SortOrder, r.TextKey)));
            Expect(code == 0 && src.Length == rt.Count && src.SequenceEqual(rt) && IntelCatalog.Problems.Count == 0,
                $"A1 fg.TbIntelKind（{t.TbIntelKind.DataList.Count} 类）/ fg.TbBuildingStack（{t.TbBuildingStack.DataList.Count} 行）/ fg.TbIntelBoss / fg.TbIntelFragment 与源数据 fgdata_intel.py 逐字段一致，载入校验无问题" +
                (code == 0 ? (src.SequenceEqual(rt) ? string.Empty : $"\n源：{string.Join(" / ", src.Take(3))}\n表：{string.Join(" / ", rt.Take(3))}") : "：" + output)
                + (IntelCatalog.Problems.Count > 0 ? "；" + string.Join("；", IntelCatalog.Problems) : string.Empty));

            string[] kindIds = IntelCatalog.All.Select(k => k.Id).ToArray();
            bool five = kindIds.SequenceEqual(new[] { IntelCatalog.KindRaid, IntelCatalog.KindCounter, IntelCatalog.KindBoss, IntelCatalog.KindWeather, IntelCatalog.KindFleet });
            bool stack = Mathf.Approximately(IntelCatalog.StackBonus(Post, 1), 1f) && Mathf.Approximately(IntelCatalog.StackBonus(Post, 2), 0.5f)
                         && Mathf.Approximately(IntelCatalog.StackBonus(Post, 3), 0.25f) && IntelCatalog.StackBonus(Post, 4) == 0f
                         && IntelCatalog.StackRateMilli(Post, 3) == 1750 && IntelCatalog.StackRateMilli(Post, 5) == 1750
                         && IntelCatalog.StackBonus(ResearchCatalog.LabTypeId, 3) == 1f; // 没登记的建筑：每座独立、相加
            bool grid = GridContent.TryGetBuilding(Post, out GameConfig.fg.BuildingGrid g) && g.FootprintW == 3 && g.FootprintH == 3 && g.Category == "intel" && g.Placeable == 1
                        && g.UnlockRule == "research:signal.listening_post";
            bool weather = IntelCatalog.TryGet(IntelCatalog.KindWeather, out IntelKindDef w) && !w.IsReady && w.ConditionKind == "tier" && w.ConditionArg == 2;
            bool fleet = IntelCatalog.TryGet(IntelCatalog.KindFleet, out IntelKindDef fk) && fk.ConditionKind == "act" && fk.ConditionArg == 2 && IntelCatalog.Fragments.Count >= 1;
            Expect(five && stack && grid && weather && fleet && HomeValleyLayout.PowerProfile.TryGetValue(Post, out var prof) && prof.PowerDemand > 0f,
                $"A2 FGR-RND-050 五类情报按优先级（{string.Join(" → ", IntelCatalog.All.Select(k => k.Name))}）；FGR-RND-052 多座递减初值：第 1 座 100%、第 2 座 +50%、第 3 座 +25%、第 4 座起 0" +
                "（未登记的建筑如仿真实验室每座独立相加）；监听站 3×3、建造菜单“情报”页、耗电、由研发节点解锁；天气要监听站 T2 且后续版本开放；舰队片段第二幕起");

            // 研究门槛：节点要求保管库里有监听阵列核（不消耗），完成后建造菜单才放开（FG08 FGR-QST-020）。
            CampaignState s = NewWorld(5501, 0);
            bool node = ResearchCatalog.TryGet("signal.listening_post", out ResearchNodeDef n) && n.IsReady && n.Unlocks.Contains("build:listening_post")
                        && n.KeyItems.Any(k => k.Item?.Id == "listening_array_core" && k.Amount == 1);
            BuildCatalog.TryGet(Post, out BuildEntry entry);
            bool lockedBefore = entry != null && !BuildCatalog.IsUnlocked(s, entry) && !ResearchService.KeysPresent(s, n);
            HomeInventory.Add(s, "listening_array_core", 1, clampToSpace: false);
            bool keys = ResearchService.KeysPresent(s, n);
            ResearchService.CompleteForTests(s, "signal.listening_post");
            bool unlocked = BuildCatalog.IsUnlocked(s, entry) && HomeInventory.Stock(s, ItemCatalog.Find("listening_array_core")) == 1;
            bool t2Later = ResearchCatalog.TryGet("signal.listening_t2", out ResearchNodeDef t2) && !t2.IsReady && t2.OpensIn == "FG7-ENV-03";
            Expect(node && lockedBefore && keys && unlocked && t2Later,
                "A3 研究门槛：“信号 · 监听站”要求核心保管库里有监听阵列核（研究不消耗它）；完成前建造菜单锁定，完成后放开，关键材料仍在；“监听站 T2”节点在树上、写明后续版本开放（天气系统 FG7-ENV-03）");

            string[] keysText =
            {
                "building.listening_post.name", "building.listening_post.desc", "intel.panel.title", "intel.panel.open", "intel.panel.empty", "intel.panel.status.no_post",
                "intel.panel.status.posts", "intel.panel.status.target", "intel.panel.row_valid", "intel.panel.row_outdated", "intel.panel.map", "intel.panel.footer",
                "intel.raid.line", "intel.counter.region", "intel.boss.line", "intel.boss.foundry_core.phases", "intel.fragment.line", "intel.outdated.expired",
                "intel.outdated.arrived", "intel.outdated.gone", "intel.notify.interrupted", "bs.reason.intel_working", "bs.reason.intel_idle", "ui.map.raid_forecast",
                "analysis.result.core_intel", "codex.intel.listening_post.title", "codex.intel.listening_post.body", "input.action.open_intel.name",
            };
            var missing = keysText.Concat(IntelCatalog.All.SelectMany(k => new[] { k.NameKey, k.DescKey })).Concat(IntelCatalog.Fragments.Select(f => f.TextKey))
                .Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string z) || string.IsNullOrWhiteSpace(z) || !GameText.TryGet(k, GameLanguage.En, out string e) || string.IsNullOrWhiteSpace(e)).ToList();
            string[] hooks = { GuidanceHooks.IntelFirstBuilt, GuidanceHooks.IntelFirstOpen, GuidanceHooks.IntelFirstIntel, GuidanceHooks.IntelFirstRaidForecast, GuidanceHooks.IntelFirstOutdated, GuidanceHooks.IntelFirstInterrupted };
            bool hookOk = hooks.All(h => GuidanceHooks.Known.Contains(h));
            bool codex = IntelCatalog.All.All(k => t.TbCodexEntry.DataList.Any(e => e.Id == k.CodexId)) && t.TbCodexEntry.DataList.Any(e => e.Id == "codex.intel.listening_post" && e.Hooks.Contains(GuidanceHooks.IntelFirstBuilt));
            bool key = InputActionCatalog.TryGet(GameActionId.OpenIntel, out InputActionDef def) && def.Status == InputActionStatus.Wired && InputActionCatalog.DefaultChord(GameActionId.OpenIntel).Key == KeyCode.Y;
            bool notify = t.TbNotifyType.DataList.Any(x => x.Id == "intel_new" && x.AwaySection == "research") && t.TbNotifyType.DataList.Any(x => x.Id == "intel_raid" && x.AwaySection == "raid")
                          && t.TbNotifyType.DataList.Any(x => x.Id == "intel_interrupted" && x.AwaySection == "buildings");
            Expect(missing.Count == 0 && hookOk && codex && key && notify,
                "A4 新文本中英两套（面板 / 状态 / 情报内容 / 首领机制 / 片段 / 建筑状态 / 地图 / 图鉴）；引导钩子 6 个登记；五类情报与监听站都有图鉴条目；" +
                "情报键 Y 已接入（可重绑，动作登记表 wired）；通知类型 3 种（新情报→离家报告研究段、突袭预报→突袭段、破译中断→建筑段）" + (missing.Count > 0 ? "；缺文本：" + string.Join("、", missing) : string.Empty));
        }

        // ── B 多座递减（FGR-RND-052）──────────────────────────────────────────────

        private static void CheckStacking()
        {
            IntelKindDef counter = IntelCatalog.All.First(k => k.Id == IntelCatalog.KindCounter);
            IntelKindDef fleet = IntelCatalog.All.First(k => k.Id == IntelCatalog.KindFleet);
            long need1 = GameClock.TicksFor(counter.DecipherSeconds);
            CampaignState s = NewWorld(5502, 1);
            bool powered = PostIds.All(id => IntelService.IsWorking(HomeGridService.FindBuilding(s, id)));
            long one = TicksUntilProduced(s, (int)counter.DecipherSeconds + 30);
            bool firstIsCounter = IntelService.LastProduced?.Kind == IntelCatalog.KindCounter;
            // 再建 3 座（共 4 座）：速度 = 1 + 0.5 + 0.25 + 0 = 1.75；第二幕起舰队片段需要破译。
            AddPosts(s, 3, 5502);
            s.Progress.Act = 2;
            int rate4 = IntelService.RateMilli(s);
            long need2 = GameClock.TicksFor(fleet.DecipherSeconds);
            long four = TicksUntilProduced(s, (int)fleet.DecipherSeconds + 30);
            bool fleetMade = IntelService.LastProduced?.Kind == IntelCatalog.KindFleet;
            // 禁用 1 座（第 4 座本来就不提速）：仍 1.75；再禁用 1 座：1.5；把禁用的恢复、断掉 1 座的电（标记缺电）：缺电的不算。
            BuildingOps.TrySetEnabled(s, PostIds[3], false, out _);
            HomeValleyPowerGrid.Recompute(s);
            int rateDisabled1 = IntelService.RateMilli(s);
            BuildingOps.TrySetEnabled(s, PostIds[2], false, out _);
            HomeValleyPowerGrid.Recompute(s);
            int rateDisabled2 = IntelService.RateMilli(s);
            BuildingOps.TrySetEnabled(s, PostIds[2], true, out _);
            BuildingOps.TrySetEnabled(s, PostIds[3], true, out _);
            HomeValleyPowerGrid.Recompute(s);
            BuildingRecord cut = HomeGridService.FindBuilding(s, PostIds[1]);
            cut.PowerState = BuildingPowerState.Unpowered;
            int rateUnpowered = IntelService.RateMilli(s);
            HomeValleyPowerGrid.Recompute(s);
            string breakdown = IntelService.RateBreakdown(4);
            bool oneOk = Math.Abs(one - need1) <= 6;
            bool fourOk = Math.Abs(four - (long)Math.Ceiling(need2 * 1000.0 / 1750.0)) <= 9;
            Expect(powered && oneOk && firstIsCounter && rate4 == 1750 && fourOk && fleetMade && rateDisabled1 == 1750 && rateDisabled2 == 1500 && rateUnpowered == 1500
                   && breakdown.Contains("+50%") && breakdown.Contains("+25%") && breakdown.Contains("第 4 座起不再提速"),
                $"B FGR-RND-052 多座递减（真实世界逐步实测）：1 座破译一条反制预览用 {one} 步（应 {need1}）；4 座速度 ×{rate4 / 1000.0:0.##}（第 4 座不提速），舰队片段用 {four} 步" +
                $"（应约 {Math.Ceiling(need2 * 1000.0 / 1750.0)}）；禁用第 4 座仍 ×{rateDisabled1 / 1000.0:0.##}、再禁用 1 座 ×{rateDisabled2 / 1000.0:0.##}；缺电的不算（×{rateUnpowered / 1000.0:0.##}）；速度构成“{breakdown}”");
        }

        // ── C 五类情报（FGR-RND-050）─────────────────────────────────────────────

        private static void CheckKinds()
        {
            CampaignState s = NewWorld(5503, 1);
            long now0 = GameClock.Ticks;
            // 没遭遇过首领、第一幕：只有反制预览需要破译；首领 / 天气 / 舰队各写原因
            IntelKindDef boss = IntelCatalog.All.First(k => k.Id == IntelCatalog.KindBoss);
            IntelKindDef weather = IntelCatalog.All.First(k => k.Id == IntelCatalog.KindWeather);
            IntelKindDef fleet = IntelCatalog.All.First(k => k.Id == IntelCatalog.KindFleet);
            IntelKindDef raid = IntelCatalog.All.First(k => k.Id == IntelCatalog.KindRaid);
            bool nBoss = IntelService.Needed(s, boss, now0, out string whyBoss);
            bool nWeather = IntelService.Needed(s, weather, now0, out string whyWeather);
            bool nFleet = IntelService.Needed(s, fleet, now0, out string whyFleet);
            bool nRaid = IntelService.Needed(s, raid, now0, out string whyRaid);
            bool reasons = !nBoss && (whyBoss ?? "").Contains("遭遇") && !nWeather && (whyWeather ?? "").Contains("后续版本开放")
                           && !nFleet && (whyFleet ?? "").Contains("第 2 幕") && !nRaid && (whyRaid ?? "").Contains("没有正在逼近")
                           && IntelService.PickTarget(s, now0)?.Id == IntelCatalog.KindCounter;
            long t = TicksUntilProduced(s, 200);
            IntelRecord counter = Latest(s, IntelCatalog.KindCounter);
            string expectAdapt = EnemyAdaptationService.ComputeAdaptation(s);
            bool counterOk = t > 0 && counter != null && counter.Source == IntelService.SourcePost && counter.Regions.Contains(FoundryOutpostLayout.RegionId)
                             && counter.Adaptations[Array.IndexOf(counter.Regions, FoundryOutpostLayout.RegionId)] == expectAdapt
                             && counter.ExpiresTick - counter.ProducedTick == GameClock.TicksFor(1200)
                             && IntelService.Summary(s, counter, GameClock.Ticks).Contains(GameLogic.Campaign.Combat.CombatSites.SiteName(FoundryOutpostLayout.RegionId));
            // 遭遇首领 → 首领弱点（数值来自首领常量）；击败后旧情报标“目标已不在”、不再破译
            RegionRecord region = EncounterFoundryBoss(s);
            bool bossNeeded = IntelService.Needed(s, boss, GameClock.Ticks, out _) && IntelService.PickTarget(s, GameClock.Ticks)?.Id == IntelCatalog.KindBoss;
            TicksUntilProduced(s, 260);
            IntelRecord weak = Latest(s, IntelCatalog.KindBoss);
            string weakText = IntelService.Summary(s, weak, GameClock.Ticks);
            bool bossOk = weak != null && weak.Subject == "foundry_core" && weakText.Contains("60%") && weakText.Contains("+20%") && weakText.Contains("5 秒")
                          && IntelService.PickTarget(s, GameClock.Ticks) == null;
            region.CoreState = CoreBossState.Destroyed.ToString();
            WorldSimulation.StepMany(6);
            bool bossGone = weak != null && weak.Outdated && weak.OutdatedReason == IntelService.ReasonGone && !IntelService.Needed(s, boss, GameClock.Ticks, out _);
            // 第二幕：舰队片段按顺序，每个只截获一次；全部截获后写“都已截获”
            s.Progress.Act = 2;
            var heard = new List<string>();
            for (int i = 0; i < IntelCatalog.Fragments.Count; i++)
            {
                TicksUntilProduced(s, (int)fleet.DecipherSeconds + 30);
                heard.Add(Latest(s, IntelCatalog.KindFleet)?.Subject);
            }
            bool fleetOk = heard.SequenceEqual(IntelCatalog.Fragments.Select(f => f.Id)) && I(s).FragmentsHeard.SequenceEqual(heard)
                           && !IntelService.Needed(s, fleet, GameClock.Ticks, out string fleetDone) && fleetDone.Contains("都已截获")
                           && Latest(s, IntelCatalog.KindFleet) != null && IntelService.Summary(s, Latest(s, IntelCatalog.KindFleet), GameClock.Ticks).Contains("截获");
            bool noWeather = I(s).Records.All(r => r.Kind != IntelCatalog.KindWeather);
            Expect(reasons && counterOk && bossNeeded && bossOk && bossGone && fleetOk && noWeather,
                $"C FGR-RND-050 五类情报：第一幕没遭遇首领时只破译反制预览，其余各写原因（首领“{whyBoss}”、天气“{whyWeather}”、片段“{whyFleet}”、突袭“{whyRaid}”）；" +
                $"反制预览与出发锁定同一算法（铸造前哨 = {expectAdapt}），有效 1 个游戏日；遭遇铸造主核心后破译首领弱点（“{Short(weakText)}”，数值来自首领常量）；" +
                $"击败后旧情报标“目标已不在”、不再破译；第二幕舰队片段按顺序各截获一次（{string.Join(" → ", heard)}）；天气在 FG7-ENV-03 前不出现");

            // 优先级插队：反制预览破译到一半时来了突袭 → 先破译突袭预报，反制预览进度保留
            CampaignState p = NewWorld(5504, 1);
            WorldSimulation.StepMany(GameClock.StepHz * 50);
            int pctBefore = IntelService.ProgressPercent(p, IntelCatalog.KindCounter);
            TransitGroupRecord g = WorldTransitSystem.DispatchRaidFromTerritory(p, "silent", 3, out _);
            WorldSimulation.StepMany(6);
            bool switched = I(p).CurrentKind == IntelCatalog.KindRaid;
            long tr = TicksUntilProduced(p, 120);
            bool raidFirst = tr > 0 && IntelService.LastProduced?.Kind == IntelCatalog.KindRaid && IntelService.ProgressPercent(p, IntelCatalog.KindCounter) == pctBefore;
            WorldSimulation.StepMany(6);
            bool resumed = I(p).CurrentKind == IntelCatalog.KindCounter;
            Expect(g != null && pctBefore >= 35 && switched && raidFirst && resumed,
                $"C2 优先级插队：反制预览破译到 {pctBefore}% 时突袭部队出发 → 立刻改破译突袭预报（{tr} 步出预报），反制预览的进度原样保留，之后接着破译反制预览");
        }

        private static string Short(string s) => s == null ? string.Empty : s.Length > 60 ? s.Substring(0, 60) + "…" : s;

        // ── D 有效期与已过时（负向“情报过期”）────────────────────────────────────

        private static void CheckOutdated()
        {
            CampaignState s = NewWorld(5505, 1);
            TicksUntilProduced(s, 200);
            IntelRecord first = Latest(s, IntelCatalog.KindCounter);
            long expires = first.ExpiresTick;
            while (GameClock.Ticks < expires + 6)
            {
                WorldSimulation.StepMany(Math.Max(3, (int)Math.Min(600, expires + 6 - GameClock.Ticks)));
            }
            bool marked = first.Outdated && first.OutdatedReason == IntelService.ReasonExpired && first.OutdatedTick >= expires && first.OutdatedTick <= expires + 6
                          && I(s).Records.Contains(first) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.IntelFirstOutdated);
            string row = IntelService.RowText(s, first, GameClock.Ticks);
            // 过期 → 又需要破译 → 新的反制预览替代旧的（同一目标），旧的从列表里去掉
            TicksUntilProduced(s, 200);
            IntelRecord second = Latest(s, IntelCatalog.KindCounter);
            bool replaced = second != null && second.Serial > first.Serial && I(s).Records.Count(r => r.Kind == IntelCatalog.KindCounter) == 1 && !I(s).Records.Contains(first);
            // 每类已过时的旧情报最多保留 intel.keep_per_kind 条：连续 8 支突袭都到达后，最旧的已过时预报被去掉、有效的不受影响
            CampaignState r = NewWorld(5506, 3);
            int keep = IntelCatalog.KeepPerKind;
            for (int i = 0; i < keep + 2; i++)
            {
                TransitGroupRecord g = WorldTransitSystem.DispatchRaidFromTerritory(r, "silent", 2, out _);
                TicksUntilProduced(r, 120);
                g.State = TransitGroupState.Arrived; // 测试捷径：让部队“到达”（行进本身由 E 段真实跑完）
                g.ArrivedAtTick = GameClock.Ticks;
                WorldSimulation.StepMany(6);
            }
            int outdatedRaid = I(r).Records.Count(x => x.Kind == IntelCatalog.KindRaid && x.Outdated);
            bool trimmed = outdatedRaid == keep && I(r).Records.Where(x => x.Kind == IntelCatalog.KindRaid).All(x => x.OutdatedReason == IntelService.ReasonArrived);
            Expect(marked && row.Contains("已过时") && row.Contains("过了有效期") && replaced && trimmed,
                $"D 情报过期：到期那一步在存档里标“已过时”（Outdated + 原因 + 时刻），不删除、列表行写“{Short(row.Split('\n')[0])}”；过期后重新破译出的同一目标情报替代旧的；" +
                $"突袭到达后预报标“部队已经到达”；每类已过时的旧情报最多保留 {keep} 条（{outdatedRaid} 条），有效的不受影响");
        }

        // ── E FGT-RND-008 突袭预报与实际到来的突袭一致 + 地图箭头 ──────────────────────────

        private static void CheckRaidForecast()
        {
            var lines = new List<string>();
            bool all = true;
            foreach ((int seed, string territory) in new[] { (5507, "silent"), (5508, "foundry") })
            {
                CampaignState s = NewWorld(seed, 1);
                TransitGroupRecord g = WorldTransitSystem.DispatchRaidFromTerritory(s, territory, 4, out string fail);
                if (g == null)
                {
                    all = false;
                    lines.Add($"种子 {seed}：派不出突袭（{fail}）");
                    continue;
                }
                long tp = TicksUntilProduced(s, 120);
                IntelRecord f = Latest(s, IntelCatalog.KindRaid);
                bool produced = tp > 0 && f != null && f.Subject == g.GroupId && NotificationCenter.History.Any(e => e.Type.Id == "intel_raid")
                                && GameSettings.HasSeenGuidanceHook(GuidanceHooks.IntelFirstRaidForecast);
                // 地图箭头：有效预报 → Groups 图层一支红色箭头（箭尾在来袭方向，箭头指向核心）+ 标签
                var model = new WorldMapModel();
                GridCell core = HomeGridService.CorePivot(s);
                var view = new WorldMapView { CenterX = core.X, CenterY = core.Y, HalfWidth = 300, CanvasWidth = 1000, CanvasHeight = 800 };
                model.Collect(s, view, 256);
                WorldMapItem icon = model.Items.FirstOrDefault(i => i.Kind == WorldMapItemKind.RaidForecast);
                WorldMapLine arrow = model.Lines.FirstOrDefault(l => l.Forecast);
                double tailD = arrow.Forecast ? Math.Sqrt((arrow.X0 - core.X) * (arrow.X0 - core.X) + (arrow.Y0 - core.Y) * (arrow.Y0 - core.Y)) : 0;
                double headD = arrow.Forecast ? Math.Sqrt((arrow.X1 - core.X) * (arrow.X1 - core.X) + (arrow.Y1 - core.Y) * (arrow.Y1 - core.Y)) : 0;
                bool map = f != null && arrow.Forecast && headD < tailD && icon.Label != null && icon.Label.Contains(WorldTransitSystem.OriginName(territory))
                           && IntelService.Octant(arrow.X0 - core.X, arrow.Y0 - core.Y) == IntelService.Octant(f.DirX, f.DirY);
                // 跑到部队真的到达
                long limit = GameClock.Ticks + GameClock.StepHz * 1500L;
                while (g.State == TransitGroupState.Marching && GameClock.Ticks < limit)
                {
                    WorldSimulation.StepMany(30);
                }
                bool arrived = g.State != TransitGroupState.Marching && g.ArrivedAtTick > 0;
                double ax = g.PosX - core.X, ay = g.PosY - core.Y;
                double angF = Math.Atan2(f?.DirY ?? 0, f?.DirX ?? 0) * 180 / Math.PI;
                double angA = Math.Atan2(ay, ax) * 180 / Math.PI;
                double diff = Math.Abs(((angF - angA) % 360 + 540) % 360 - 180);
                bool window = f != null && arrived && g.ArrivedAtTick >= f.WindowFromTick && g.ArrivedAtTick <= f.WindowToTick;
                bool faction = f != null && f.Faction == territory && g.OriginId == territory && f.Units == g.UnitCount;
                bool direction = f != null && arrived && diff <= 25.0 && IntelService.Octant(ax, ay) == IntelService.Octant(f.DirX, f.DirY);
                WorldSimulation.StepMany(6);
                bool outdated = f != null && f.Outdated && f.OutdatedReason == IntelService.ReasonArrived;
                model.Collect(s, view, 256);
                bool arrowGone = !model.Lines.Any(l => l.Forecast);
                bool ok = produced && map && arrived && window && faction && direction && outdated && arrowGone;
                all &= ok;
                lines.Add($"种子 {seed} / {territory}：{(ok ? "一致" : "不一致")}（预报于出发后 {tp} 步；窗口 {f?.WindowFromTick}～{f?.WindowToTick}，实际到达 {g.ArrivedAtTick}；" +
                          $"阵营 {f?.Faction} / {g.OriginId}，规模 {f?.Units} / {g.UnitCount}；方向 {IntelService.DirectionName(f?.DirX ?? 0, f?.DirY ?? 0)}，偏差 {diff:F1}°；地图箭头 {map}；到达后已过时 {outdated}、箭头消失 {arrowGone}）");
            }
            Expect(all, "E FGT-RND-008 突袭预报与实际到来的突袭一致（时间窗口、阵营、规模、方向；部队沿地形寻路走完全程；两个种子、两个出发领地）；地图上画出从来袭方向指向家园的箭头，到达后标已过时、箭头消失：\n    "
                        + string.Join("\n    ", lines));
        }

        // ── F 负向：监听站被摧毁时正在破译的情报 / 没有监听站 / 数据核心 ─────────────────────

        private static void CheckInterrupted()
        {
            CampaignState s = NewWorld(5509, 2);
            IntelKindDef counter = IntelCatalog.All.First(k => k.Id == IntelCatalog.KindCounter);
            WorldSimulation.StepMany(GameClock.StepHz * 40);
            int pct = IntelService.ProgressPercent(s, IntelCatalog.KindCounter);
            int before = NotificationCenter.History.Count(e => e.Type.Id == "intel_interrupted");
            foreach (string id in PostIds)
            {
                BuildingOps.ApplyDamage(s, id, BuildingOps.MaxDurability(Post) + 1f);
            }
            WorldSimulation.StepMany(GameClock.StepHz * 30);
            int pctAfter = IntelService.ProgressPercent(s, IntelCatalog.KindCounter);
            int notified = NotificationCenter.History.Count(e => e.Type.Id == "intel_interrupted") - before;
            string status = IntelService.StatusText(s, GameClock.Ticks);
            bool paused = pct > 0 && pctAfter == pct && notified == 1 && I(s).InterruptNotified && ProducedOf(s) == 0 && status.Contains("暂停") && status.Contains("保留")
                          && GameSettings.HasSeenGuidanceHook(GuidanceHooks.IntelFirstInterrupted)
                          && BuildingStatusService.Evaluate(s, HomeGridService.FindBuilding(s, PostIds[0])).Kind != BuildingStatusKind.Working;
            // 修好（与重建完工同一结果：恢复运转），接着破译：剩下的进度按原速度走完
            foreach (string id in PostIds)
            {
                BuildingRecord b = HomeGridService.FindBuilding(s, id);
                b.ConstructionState = BuildingConstructionState.Operational;
                b.Health = BuildingOps.MaxDurability(Post);
            }
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
            long rest = TicksUntilProduced(s, 200);
            long expectRest = (long)Math.Ceiling((GameClock.TicksFor(counter.DecipherSeconds) * 1000.0 - GameClock.TicksFor(counter.DecipherSeconds) * 10.0 * pct) / 1500.0);
            bool resumed = rest > 0 && rest < GameClock.TicksFor(counter.DecipherSeconds) * 1000 / 1500 && !I(s).InterruptNotified;
            Expect(paused && resumed,
                $"F1 负向“监听站被摧毁时正在破译的情报”：破译到 {pct}% 时两座监听站都被毁 → 破译暂停、进度原样保留（30 秒后仍 {pctAfter}%）、只发一次“破译中断”（{notified} 次）、" +
                $"面板写“{Short(status.Split('\n')[0])}”；修好后接着破译，{rest} 步出情报（不从零开始，约 {expectRest}）");

            // 没有监听站：不产出；数据核心照样给一条情报（DEBT-FG5RND02-03，来源写数据核心）
            CampaignState n = NewWorld(5510, 0);
            WorldSimulation.StepMany(GameClock.StepHz * 200);
            bool none = ProducedOf(n) == 0 && I(n).Records.Length == 0 && IntelService.StatusText(n, GameClock.Ticks).Contains("没有工作中的监听站");
            IntelRecord dc = IntelService.GrantFromDataCore(n);
            IntelRecord dc2 = IntelService.GrantFromDataCore(n); // 现有情报都有效时：刷新反制预览（数据核心总能给一条）
            bool dataCore = dc != null && dc.Source == IntelService.SourceDataCore && dc.Kind == IntelCatalog.KindCounter && dc2 != null && dc2.Kind == IntelCatalog.KindCounter
                            && I(n).Records.Length == 1 && IntelService.RowText(n, dc2, GameClock.Ticks).Contains("数据核心");
            Expect(none && dataCore, "F2 没有监听站时 200 秒不产出任何情报、面板写“没有工作中的监听站”；数据核心（解析台首次解读）照样给一条情报、来源写“数据核心”，现有情报都有效时刷新反制预览（同一目标替代，不重复）");
        }

        // ── G 存读档 ────────────────────────────────────────────────────────────

        private static void LayScenario(CampaignState s)
        {
            EncounterFoundryBoss(s);
            WorldTransitSystem.DispatchRaidFromTerritory(s, "silent", 3, out _);
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(9811, 2);
            LayScenario(s);
            WorldSimulation.StepMany(GameClock.StepHz * 170); // 突袭预报（30 秒）、反制预览（80 秒）已出，首领弱点破译到一半
            string before = IntelService.Snapshot(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(GameClock.StepHz * 60);
            string continuous = IntelService.Snapshot(s);

            string RunFromSave(out string loaded)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                ResearchService.ResetForTests();
                IntelService.ResetSessionState();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                loaded = null;
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                CampaignSession.Set(Slot, rr.State);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                loaded = IntelService.Snapshot(rr.State);
                WorldSimulation.StepMany(GameClock.StepHz * 60);
                return IntelService.Snapshot(rr.State);
            }

            string first = RunFromSave(out string loaded1);
            string second = RunFromSave(out _);
            CampaignState r = CampaignSession.Current;
            bool hasOutdatedField = I(r).Records.Length >= 2;
            bool content = before.Contains("|raid_forecast|") && before.Contains("|counter_preview|") && before.Contains("cur=boss_weakness") && !before.Contains("boss_weakness:0:");
            Expect(save.Success && loaded1 == before && content && first == continuous && second == continuous && hasOutdatedField,
                "G 真文件存读档：情报列表（有效期、已过时标记、突袭预报的窗口 / 方向）、各类破译进度、当前目标、已截获片段、统计写进存档，读档后逐字段一致；读档后接着跑 60 秒与不存档连续跑逐字段一致（两次读档同一结果）"
                + (loaded1 == before ? string.Empty : $"\n存前：{before}\n读后：{loaded1}") + (first == continuous ? string.Empty : $"\n连续：{continuous}\n读档：{first}")
                + (content && hasOutdatedField && save.Success ? string.Empty : $"\n快照：{before}（存档 {save.Success}）"));
        }

        // ── H / I 暂停、倍速、观察 ───────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, 2, observe);
            pausedHeld = true;
            LayScenario(s);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = IntelService.Snapshot(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = IntelService.Snapshot(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 150;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 1200)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return IntelService.Snapshot(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(9812, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference.Contains("raid_forecast"),
                "H 暂停中（120 帧）破译进度与有效期不动；0.5x / 1x / 2x / 3x 跑同样的 150 游戏秒（突袭预报、首领弱点、反制预览进度）逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(9813, true, 1f, false, out _);
            string unseen = RunScenario(9813, false, 1f, false, out _);
            Expect(seen == unseen && seen.Contains("raid_forecast"), "I 同一组情报在观察与不观察家园时跑 150 游戏秒逐字段一致（FGR-BASE-021：远征时家园照常破译）"
                                                                    + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── J 面板（真 UXML）──────────────────────────────────────────────────────

        private sealed class Keys : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool GetKey(KeyCode key) => key == Down;
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        private static void PressKey(Keys keys, GameActionId action)
        {
            keys.Down = GameSettings.KeyBindings.GetKey(action);
            UiKitInputPump.ProcessLibraryKeys();
            keys.Down = KeyCode.None;
            InputRouter.DebugClearConsumedKeys();
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

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(9814, 0);
            VisualElement root = F.MountUxml(PanelUxml, out GameObject go);
            var mgo = new GameObject("__fgintel_map") { hideFlags = HideFlags.HideAndDontSave };
            IntelPanelUIToolkit.InWorldOverrideForTests = true;
            StrategicMapUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var panel = go.AddComponent<IntelPanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(RowUxml));
                var keys = new Keys();
                InputRouter.Reset();
                InputRouter.DebugSetReader(keys);
                InputRouter.SetScope(InputScope.Strategy);
                PressKey(keys, GameActionId.OpenIntel);
                panel.Refresh(force: true);
                bool empty = IntelPanelUIToolkit.IsOpen && panel.PanelVisible && panel.TitleText == "情报" && panel.EmptyText.Contains("还没有任何情报") && panel.RowCount == 0
                             && panel.StatusText.Contains("没有工作中的监听站") && panel.KindsText.Split('\n').Length == 5 && panel.FooterText.Contains("Y")
                             && GameSettings.HasSeenGuidanceHook(GuidanceHooks.IntelFirstOpen) && !GameText.ContainsMarker(panel.StatusText + panel.KindsText + panel.FooterText + panel.EmptyText);
                Expect(empty, $"J1 按 Y 打开情报面板（模态）：没有任何情报时的空状态“{Short(panel.EmptyText)}”；状态“{Short(panel.StatusText)}”；五类各一行状态；页脚写情报键");
                PressKey(keys, GameActionId.OpenIntel);
                bool toggled = !IntelPanelUIToolkit.IsOpen;

                // 建监听站、出一条反制预览、来一支突袭出预报：列表两行、新标记、突袭行有“在地图上查看”
                AddPosts(s, 2, 9814);
                WorldSimulation.StepMany(3);
                TicksUntilProduced(s, 200);
                TransitGroupRecord g = WorldTransitSystem.DispatchRaidFromTerritory(s, "silent", 3, out _);
                TicksUntilProduced(s, 120);
                IntelPanelUIToolkit.Open(null);
                panel.Refresh(force: true);
                int raidRow = Enumerable.Range(0, panel.RowCount).FirstOrDefault(i => panel.RowText(i).Contains("突袭预报"));
                bool rows = panel.RowCount == 2 && panel.RowText(0).StartsWith("（新）") && panel.RowText(raidRow).Contains("来自") && panel.RowMapVisible(raidRow)
                            && Enumerable.Range(0, panel.RowCount).Any(i => panel.RowText(i).Contains("敌方反制预览") && !panel.RowMapVisible(i))
                            && panel.StatusText.Contains("2 座监听站工作中") && panel.StatusText.Contains("×1.5") && panel.SectionText.Contains("有效 2 条");
                Click(panel.FilterButton(1));
                bool filtered = panel.CurrentFilter == 1 && panel.RowCount == 1 && panel.RowText(0).Contains("突袭预报");
                Expect(toggled && rows && filtered,
                    $"J2 再按 Y 关闭；建好两座监听站后列表两行（“{Short(panel.RowText(0).Split('\n')[0])}”，新情报带“（新）”，只有突袭预报有“在地图上查看”）；" +
                    $"状态写几座在工作与速度 ×1.5；筛选“突袭预报”后只剩 1 行");

                // 在地图上查看：关面板、打开战略地图并对准箭头
                VisualElement mapRoot = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(MapUxml).CloneTree();
                StrategicMapUIToolkit map = mgo.AddComponent<StrategicMapUIToolkit>();
                map.BindView(mapRoot);
                IntelRecord f = Latest(s, IntelCatalog.KindRaid);
                Vector2 tail = IntelService.ArrowTail(s, f);
                Vector2 head = IntelService.ArrowHead(s, f);
                Click(panel.RowMapButton(0));
                bool mapOk = !IntelPanelUIToolkit.IsOpen && StrategicMapUIToolkit.IsOpen && Math.Abs(map.ViewCenterX - (tail.x + head.x) * 0.5) < 1.0
                             && Math.Abs(map.ViewCenterY - (tail.y + head.y) * 0.5) < 1.0 && map.ViewHalfWidth >= IntelCatalog.MapArrowCells;
                StrategicMapUIToolkit.Close();
                Expect(mapOk, $"J3 突袭预报行“在地图上查看”：关掉情报面板、打开战略地图，视图中心对准箭头（{map.ViewCenterX:F0},{map.ViewCenterY:F0}），整支箭头在视野里");

                // 已过时：部队到达 → 行变暗、写“已过时（部队已经到达）”、不再有“在地图上查看”
                g.State = TransitGroupState.Arrived;
                g.ArrivedAtTick = GameClock.Ticks;
                WorldSimulation.StepMany(6);
                IntelPanelUIToolkit.Open(IntelCatalog.KindRaid);
                panel.Refresh(force: true);
                bool outdated = panel.CurrentFilter == 1 && panel.RowCount == 1 && panel.RowOutdated(0) && panel.RowText(0).Contains("已过时（部队已经到达）") && !panel.RowMapVisible(0)
                                && !panel.RowText(0).StartsWith("（新）");
                Expect(outdated, $"J4 已过时的预报留在列表里：样式变暗、写“{Short(panel.RowText(0).Split('\n')[0])}”、没有“在地图上查看”；再次打开不再标“新”");

                string probe = UiToolkitLayoutProbe.Probe(PanelUxml, "IntelRoot", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("IntelRoot")?.RemoveFromClassList("uk-hidden");
                });
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "J5 情报面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
            }
            finally
            {
                IntelPanelUIToolkit.Close();
                IntelPanelUIToolkit.InWorldOverrideForTests = false;
                StrategicMapUIToolkit.Close();
                StrategicMapUIToolkit.InWorldOverrideForTests = false;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(mgo);
            }
        }

        // ── K 建筑面板入口与状态 ───────────────────────────────────────────────────

        private static void CheckBuildingPanel()
        {
            CampaignState s = NewWorld(9815, 1);
            BuildingStatus working = BuildingStatusService.Evaluate(s, HomeGridService.FindBuilding(s, PostIds[0]));
            TicksUntilProduced(s, 200);
            BuildingStatus idle = BuildingStatusService.Evaluate(s, HomeGridService.FindBuilding(s, PostIds[0]));
            VisualElement root = F.MountUxml(ProdUxml, out GameObject go);
            try
            {
                var panel = go.AddComponent<ProductionPanelUIToolkit>();
                panel.BindView(root);
                ProductionPanelUIToolkit.Open(PostIds[0]);
                bool shown = panel.IntelButton != null && !panel.IntelButton.ClassListContains("bn-hidden") && panel.IntelButton.text == GameText.Get("intel.panel.open")
                             && panel.FusionButton.ClassListContains("bn-hidden");
                string other = s.BuildingRecords.First(b => b != null && BuildingOps.IsWarehouse(b)).BuildingId;
                ProductionPanelUIToolkit.Open(other);
                bool hiddenElse = panel.IntelButton.ClassListContains("bn-hidden");
                Expect(working.Kind == BuildingStatusKind.Working && working.Reason.Contains("敌方反制预览") && working.Reason.Contains("1 座监听站")
                       && idle.Kind == BuildingStatusKind.Idle && idle.Reason == GameText.Get("bs.reason.intel_idle") && shown && hiddenElse,
                    $"K 建筑状态（B05）：破译中“{working.Reason}”、空闲“{idle.Reason}”；点监听站打开的建筑面板有“{panel.IntelButton?.text}”入口（其它建筑不显示）");
            }
            finally
            {
                ProductionPanelUIToolkit.Close();
                Object.DestroyImmediate(go);
            }
        }

        // ── L 性能 ──────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(9816, 3);
            EncounterFoundryBoss(s);
            for (int i = 0; i < 12; i++)
            {
                WorldTransitSystem.DispatchRaidFromTerritory(s, i % 2 == 0 ? "silent" : "foundry", 2, out _);
            }
            s.Progress.Act = 2;
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            var sw = Stopwatch.StartNew();
            const int steps = 5000;
            for (int i = 0; i < steps; i++)
            {
                IntelService.Step(s, 3);
            }
            sw.Stop();
            double perStep = sw.Elapsed.TotalMilliseconds / steps;
            int records = I(s).Records.Length;
            PerfLines.Add($"情报每个世界步（3 座监听站、12 支行进中的突袭、{records} 条情报）{perStep * 1000.0:F2} µs（每 3 个世界步一次 = 20 Hz）");
            PerfGate.Expect(true,
                $"L 性能：情报每个世界步 {perStep:F4} ms（阈值 0.05 ms；O(监听站 + 情报条数 + 行进队伍)，都有上限）（Editor batchmode，真机 HybridCLR 另测 FG15-SYS-02）",
                new[] { PerfGate.Le(perStep, 0.05, "情报每步 ms") }, Expect, Line);
        }

        // ── 断言 ───────────────────────────────────────────────────────────────

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
