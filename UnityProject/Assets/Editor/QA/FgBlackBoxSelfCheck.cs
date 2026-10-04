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
    /// FG5-RND-06 黑匣子陈列馆与纪念墙的自动验收（FG05 FGR-RND-060；FG13 FGU-40；FGT-RND-009“黑匣子：产出技术数据，纪念墙名单正确”；
    /// 卡片“必须同时交付”纪念墙按时间 / 地点排序、阵亡通知附黑匣子去向；负向“黑匣子在远征中没被带回（留在区域里，可以再取）”）。
    /// 全部起真实系统：真实家园（世界模拟、电网、陈列馆建筑、机器登记表的伤害入口）、真实出征与撤离事务（破碎都市）、真文件存读档、真 UXML 面板。
    /// A 数据（建筑 / 格网 / 调参初值、文本中英、通知类型、钩子、图鉴）；B 家园阵亡：直接回收、阵亡时刻 / 地点、通知附去向（有 / 没有陈列馆）；
    /// C FGT-RND-009 分析：1 个游戏日共 20 点、按进度逐点入账（账本生产事务）、分析完通知；多个黑匣子排队、多座陈列馆并行；
    /// D 负向：陈列馆缺电 / 禁用 / 被毁 → 暂停、进度保留、修好接着分析；没有陈列馆时保存着不丢；
    /// E 远征：真实出征 → 阵亡的黑匣子掉在阵亡处（区域交互能装载）→ 携带者也阵亡时掉在原地 → 其他机器捡起 → 真实撤离带回进队列；
    /// E2 负向：没带回就撤离 → 留在区域（不 Lost、不进队列）→ 第二次出征再取、带回；E3 携带者活着但没能撤离 → 掉在它的实时位置；
    /// E4 铸造前哨同一撤离结算分支（掉落 / 没撤离留在原地 / 带回进队列）；E5 远征全灭字幕区分“关键物遗失”与“黑匣子留在原地”；
    /// F 纪念墙名单（名字 / 编号 / 经历 / 地点 / 时间、按时间与按地点排序、旧档没记时间的）；G 真文件存读档与旧档兼容；H 暂停与 0.5x～3x；
    /// I 观察 / 不观察一致；J 面板（真 UXML：两页、排序按钮、空状态、名册入口、英文、布局探针）；K 建筑面板入口与状态；L 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgBlackBoxSelfCheck）；单段入口 <see cref="RunFromMenu"/>。
    /// </summary>
    public static class FgBlackBoxSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const string PanelUxml = UiKitFolder + "BlackBoxPanel.uxml";
        private const string RowUxml = UiKitFolder + "BlackBoxRow.uxml";
        private const string ProdUxml = UiKitFolder + "ProductionPanel.uxml";
        private const string RosterUxml = UiKitFolder + "RosterPanel.uxml";
        private const int Slot = 5;
        private const string Gallery = BlackBoxService.TypeId;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();
        private static readonly List<string> GalleryIds = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 黑匣子陈列馆与纪念墙")]
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
            Line("\n[黑匣子陈列馆与纪念墙] 黑匣子回收 / 掉落 / 再取、陈列馆分析产出技术数据、纪念墙名单与排序、阵亡通知附去向（FG5-RND-06）");
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
            Func<bool> originalResearchGate = ResearchGate.TreeAvailableOverrideForTests;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgblackbox-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                ResearchGate.TreeAvailableOverrideForTests = () => true;
                BlackBoxService.ResetSessionState();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程）；" +
                     "黑匣子 / 陈列馆 / 纪念墙在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），真机另测（FG15-SYS-02）");
                Step(CheckData);
                Step(CheckHomeDeath);
                Step(CheckAnalysis);
                Step(CheckQueueAndParallel);
                Step(CheckGalleryDown);
                Step(CheckExpedition);
                Step(CheckNotBroughtBack);
                Step(CheckCarrierLeftBehind);
                Step(CheckFoundryOutpost);
                Step(CheckWipeCaption);
                Step(CheckMemorial);
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
                Fail($"黑匣子自检抛异常：{e}");
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
                BlackBoxPanelUIToolkit.InWorldOverrideForTests = false;
                BlackBoxPanelUIToolkit.Close();
                BlackBoxService.ResetSessionState();
                IntelService.ResetSessionState();
                FusionService.ResetSessionState();
                HomeValleyAnalysis.ResetSessionState();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                StandingRuleService.ResetForTests();
                MachineRoster.ResetForTests();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                ResearchGate.TreeAvailableOverrideForTests = originalResearchGate;
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
            Line($"  · [黑匣子陈列馆与纪念墙] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        /// <summary>新家园：发电运转、<paramref name="galleries"/> 座建成并接上电的陈列馆（按种子地形找空地，不写死坐标，B25）。</summary>
        private static CampaignState NewWorld(int seed, int galleries, bool observe = true)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            BuildMaterials.ResetForTests();
            ResearchService.ResetForTests();
            HomeValleyAnalysis.ResetSessionState();
            FusionService.ResetSessionState();
            IntelService.ResetSessionState();
            BlackBoxService.ResetSessionState();
            StandingRuleService.ResetForTests();
            MachineRoster.ResetForTests();
            CampaignState s = F.NewWorld(seed, observe, 600);
            F.PowerUp(s);
            foreach (StandingRuleRecord r in StandingRuleService.Ordered(s).ToList())
            {
                StandingRuleService.TryDelete(s, r.Serial, out _); // 测试只看自己设的规则：删掉新战役默认开启的“远征卸货”。
            }
            GalleryIds.Clear();
            AddGalleries(s, galleries, seed);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void AddGalleries(CampaignState s, int n, int seed)
        {
            for (int i = 0; i < n; i++)
            {
                // 由近到远一圈圈找接得上电网的空地（按种子地形，不写死坐标）；放下后没接上电就撤掉换下一圈。
                BuildingRecord placed = null;
                string id = HomeValleyLayout.RegionId + ":fgprod_bbg" + seed + "_" + GalleryIds.Count;
                for (float r = 6f; r <= 36f && placed == null; r += 3f)
                {
                    GridCell? at = F.FindFree(s, Gallery, r, r + 3f);
                    if (!at.HasValue)
                    {
                        continue;
                    }
                    BuildingRecord b = F.Built(s, Gallery, "bbg" + seed + "_" + GalleryIds.Count, at.Value);
                    if (b.PowerState == BuildingPowerState.Powered)
                    {
                        placed = b;
                        continue;
                    }
                    s.BuildingRecords = s.BuildingRecords.Where(x => x.BuildingId != id).ToArray();
                    HomeGridService.MapFor(s);
                    HomeValleyPowerGrid.Recompute(s);
                }
                if (placed == null)
                {
                    Fail($"种子 {seed}：核心附近找不到放第 {GalleryIds.Count + 1} 座陈列馆、接得上电网的空地");
                    return;
                }
                GalleryIds.Add(placed.BuildingId);
            }
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
        }

        /// <summary>在家园核心旁登记一台机器（真实登记表入口），返回 LogicId。</summary>
        private static int Spawn(Vector2 offset, string name = null, string chassis = null, string blueprint = null, float hp = 100f)
        {
            CampaignState s = CampaignSession.Current;
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis ?? HomeValleyLayout.Erc001ChassisId, blueprint ?? HomeValleyLayout.BlueprintErc001Id,
                HomeValleyLayout.RegionId, HomeValleyLayout.Core.Position + offset, hp, hp);
            if (!r.Success || !MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                return 0;
            }
            MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            if (name != null)
            {
                MachineNaming.TryRename(rec.LogicId, name, out _);
            }
            return rec.LogicId;
        }

        /// <summary>伤害走生产代码的唯一伤害入口（归零即阵亡）。</summary>
        private static void Kill(int logicId) => MachineRegistry.ApplyDamage(logicId, 1e6f);

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static BlackBoxState B(CampaignState s) => BlackBoxService.StateOf(s);

        private static RegionQuestItemRecord Item(CampaignState s, int id) => BlackBoxService.FindItem(s, id);

        private static string Short(string s) => s == null ? string.Empty : s.Length > 70 ? s.Substring(0, 70) + "…" : s.Replace("\n", " / ");

        private static long Day => BlackBoxService.NeedTicks;

        /// <summary>machine_destroyed 通知里关于这台机器的那一条（聚合的通知逐条看成员）。</summary>
        private static string DeathNote(int logicId)
        {
            string who = MachineNaming.Short(logicId);
            foreach (NotificationEntry e in NotificationCenter.History)
            {
                if (e?.Type?.Id != "machine_destroyed")
                {
                    continue;
                }
                foreach (NotificationMember m in e.Members)
                {
                    if (m != null && m.DetailText.StartsWith(who + " ", StringComparison.Ordinal))
                    {
                        return m.DetailText;
                    }
                }
            }
            return string.Empty;
        }

        /// <summary>直接按 600 步一块推进陈列馆（分析机制的段用；整条世界路径的段走 WorldSimulation）。</summary>
        private static void Analyse(CampaignState s, long ticks)
        {
            while (ticks > 0)
            {
                int n = (int)Math.Min(600, ticks);
                BlackBoxService.Step(s, n);
                ticks -= n;
            }
        }

        // ── A 数据 ────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            bool tuning = GridContent.TryGetTuning("blackbox.points_per_box", out float pts) && Math.Abs(pts - 20f) < 1e-4
                          && GridContent.TryGetTuning("blackbox.days_per_box", out float days) && Math.Abs(days - 1f) < 1e-4
                          && GridContent.TryGetTuning("blackbox.boxes_per_gallery", out float per) && Math.Abs(per - 1f) < 1e-4
                          && BlackBoxService.PointsPerBox == 20 && BlackBoxService.NeedTicks == GameClock.TicksFor(GameClock.DaySeconds);
            bool grid = GridContent.TryGetBuilding(Gallery, out GameConfig.fg.BuildingGrid g) && g.FootprintW == 3 && g.FootprintH == 3 && g.Placeable == 1 && g.Category == "research" && g.UnlockRule == "always";
            bool building = FgContentTables.Buildings.Any(b => b.TypeId == Gallery) && HomeValleyLayout.PowerProfile.TryGetValue(Gallery, out var prof) && Math.Abs(prof.PowerDemand - 5f) < 1e-4;
            Expect(tuning && grid && building,
                $"A1 FGR-RND-060 初值：每个黑匣子 20 点技术数据、1 个游戏日（{BlackBoxService.NeedTicks} 个世界步）产完、每座同时分析 1 个；黑匣子陈列馆 3×3 可放置、耗电 5（{tuning}/{grid}/{building}）");

            string[] keys =
            {
                "building.blackbox_gallery.name", "building.blackbox_gallery.desc", "bs.reason.blackbox_working", "bs.reason.blackbox_idle", "machine.feedback.destroyed_bb",
                "blackbox.where.home_gallery", "blackbox.where.home_no_gallery", "blackbox.where.field", "blackbox.item_name", "blackbox.row", "blackbox.state.ground",
                "blackbox.state.carried", "blackbox.state.queued", "blackbox.state.no_gallery", "blackbox.state.paused", "blackbox.state.analyzing", "blackbox.state.done",
                "blackbox.panel.title", "blackbox.panel.open", "blackbox.panel.memorial_open", "blackbox.panel.status.none", "blackbox.panel.status.working",
                "blackbox.panel.empty_boxes", "blackbox.panel.empty_memorial", "blackbox.panel.footer", "blackbox.memorial.name", "blackbox.memorial.lost", "blackbox.memorial.history", "blackbox.memorial.box", "blackbox.memorial.place",
                "blackbox.memorial.time_unknown", "blackbox.notify.done", "codex.blackbox.gallery.title", "codex.blackbox.gallery.body", "codex.blackbox.gallery.hint",
            };
            List<string> missing = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrEmpty(zh)
                                                   || !GameText.TryGet(k, GameLanguage.En, out string en) || string.IsNullOrEmpty(en)).ToList();
            bool notify = NotificationCatalog.TryGetType(BlackBoxService.NotifyDone, out NotifyTypeDef done) && done.Tier == NotifyLevel.Info && done.AwaySection == "research";
            string[] hooks = { GuidanceHooks.BlackBoxFirstRecovered, GuidanceHooks.BlackBoxFirstLeftBehind, GuidanceHooks.BlackBoxFirstBuilt, GuidanceHooks.BlackBoxFirstOpen, GuidanceHooks.BlackBoxFirstAnalyzed };
            bool hooked = hooks.All(h => GuidanceHooks.Known.Contains(h)) && GuidanceHooks.Known.Distinct().Count() == GuidanceHooks.Known.Count;
            bool codex = ConfigSystem.Instance.Tables.TbCodexEntry.DataList.Any(c => c.Id == "codex.blackbox.gallery");
            bool service = BuildingOps.MaxDurability(Gallery) > 0f;
            Expect(missing.Count == 0 && notify && hooked && codex && service,
                $"A2 文本 {keys.Length} 个键中英都有（缺：{string.Join(",", missing)}）；“黑匣子分析完成”通知（信息级、进离家报告研究段）；5 个引导钩子已登记；图鉴条目“黑匣子陈列馆与纪念墙”；建筑通用行（耐久）（{notify}/{hooked}/{codex}/{service}）");
        }

        // ── B 家园阵亡 ─────────────────────────────────────────────────────────

        private static void CheckHomeDeath()
        {
            // B1 没有陈列馆：直接回收、保存着、通知写“建好陈列馆后开始分析”
            CampaignState s = NewWorld(9601, 0);
            int a = Spawn(new Vector2(5f, -3f), "铁锤");
            MachineRecord ra = Rec(a);
            Vector2 where = ra.WorldPosition;
            WorldSimulation.StepMany(30);
            long t = GameClock.Ticks;
            Kill(a);
            RegionQuestItemRecord q = Item(s, a);
            BlackBoxRecord rec = BlackBoxService.Find(s, a);
            string note = DeathNote(a);
            bool fields = !ra.IsAlive && ra.DeathTick == t && ra.DeathRegionId == HomeValleyLayout.RegionId && Vector2.Distance(ra.DeathPosition, where) < 0.01f;
            bool recovered = q != null && q.State == RegionQuestItemState.Recovered && q.RegionId == HomeValleyLayout.RegionId && rec != null && !rec.Done && rec.Work == 0
                             && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BlackBoxFirstRecovered);
            bool noted = note.Contains("被击毁") && note.Contains("黑匣子已回收，建好黑匣子陈列馆后开始分析");
            int tech0 = s.TechData;
            WorldSimulation.StepMany(GameClock.StepHz * 120);
            bool kept = s.TechData == tech0 && BlackBoxService.Find(s, a)?.Work == 0 && BlackBoxService.BoxStateText(s, a).Contains("等待工作中的黑匣子陈列馆");
            Expect(fields && recovered && noted && kept,
                $"B1 家园阵亡（伤害走生产入口）：记下阵亡时刻（{ra.DeathTick}）/ 地点（归还谷地 {ra.DeathPosition}）；黑匣子直接回收进陈列馆队列；阵亡通知“{note}”；" +
                $"没有陈列馆时保存着、不产出、状态写“{BlackBoxService.BoxStateText(s, a)}”（{fields}/{recovered}/{noted}/{kept}）");

            // B2 有陈列馆：通知写“送往黑匣子陈列馆分析”；补建陈列馆后上面那个开始分析
            AddGalleries(s, 1, 9601);
            WorldSimulation.StepMany(3);
            int b = Spawn(new Vector2(-5f, -3f));
            Kill(b);
            string note2 = DeathNote(b);
            WorldSimulation.StepMany(GameClock.StepHz * 60);
            BlackBoxRecord ar = BlackBoxService.Find(s, a), br = BlackBoxService.Find(s, b);
            bool order = ar != null && br != null && ar.Work > 0 && br.Work == 0 && s.TechData == tech0 + 1;
            Expect(note2.Contains("黑匣子已回收，送往黑匣子陈列馆分析") && order,
                $"B2 有陈列馆时阵亡通知“{note2}”；补建陈列馆后先阵亡的先分析（{ar?.Work} 步 / 后到的 {br?.Work} 步），60 游戏秒入账 1 点技术数据（{s.TechData - tech0}）");
        }

        // ── C FGT-RND-009 分析 ─────────────────────────────────────────────────

        private static void CheckAnalysis()
        {
            CampaignState s = NewWorld(9602, 1);
            int a = Spawn(new Vector2(5f, -3f));
            Kill(a);
            int tech0 = s.TechData;
            long ledger0 = s.ResourceTransactions?.Length ?? 0;
            int done0 = NotificationCenter.History.Where(e => e?.Type?.Id == BlackBoxService.NotifyDone).Sum(e => e.Count);
            WorldSimulation.StepMany((int)(Day / 2));
            int half = s.TechData - tech0;
            BlackBoxRecord r = BlackBoxService.Find(s, a);
            int pctHalf = BlackBoxService.ProgressPercent(r);
            bool notDoneHalf = r != null && !r.Done;
            WorldSimulation.StepMany((int)(Day - Day / 2) + 6);
            int full = s.TechData - tech0;
            int done1 = NotificationCenter.History.Where(e => e?.Type?.Id == BlackBoxService.NotifyDone).Sum(e => e.Count);
            string doneText = NotificationCenter.History.Where(e => e?.Type?.Id == BlackBoxService.NotifyDone).Select(e => e.Text).LastOrDefault() ?? string.Empty;
            var txs = (s.ResourceTransactions ?? Array.Empty<ResourceTransactionRecord>())
                .Where(x => x != null && x.OwnerId == BlackBoxService.InstanceIdOf(a)).ToList();
            bool ledger = txs.Count > 0 && txs.All(x => x.State == ResourceTransactionState.Committed && x.ResourceType == CampaignEconomyLedger.ResourceTechData)
                          && Math.Abs(txs.Sum(x => -x.Requested) - 20f) < 1e-3;
            WorldSimulation.StepMany(GameClock.StepHz * 120);
            bool stays = s.TechData - tech0 == 20;
            Expect(half >= 9 && half <= 10 && pctHalf >= 49 && pctHalf <= 50 && notDoneHalf && full == 20 && r.Done && r.PointsGranted == 20 && B(s).PointsProduced == 20
                   && B(s).Analyzed == 1 && done1 == done0 + 1 && doneText.Contains("技术数据 +20") && ledger && stays
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BlackBoxFirstAnalyzed),
                $"C1 FGT-RND-009 产出技术数据：1 座陈列馆分析 1 个黑匣子，半个游戏日入账 {half} 点（进度 {pctHalf}%，边分析边入账），1 个游戏日共 {full} 点并标分析完；" +
                $"入账走经济账本生产事务（{txs.Count} 笔、全部 Committed、合计 20）；“黑匣子分析完成”通知“{doneText}”；之后不再多给（{stays}）");
        }

        private static void CheckQueueAndParallel()
        {
            // 1 座陈列馆、3 个黑匣子：按回收先后一个接一个
            CampaignState s = NewWorld(9603, 1);
            int[] ids = { Spawn(new Vector2(4f, -3f)), Spawn(new Vector2(6f, -3f)), Spawn(new Vector2(8f, -3f)) };
            foreach (int id in ids)
            {
                Kill(id);
                WorldSimulation.StepMany(3);
            }
            int tech0 = s.TechData;
            Analyse(s, Day);
            bool first = BlackBoxService.Find(s, ids[0]).Done && BlackBoxService.Find(s, ids[1]).Work == 0 && BlackBoxService.Find(s, ids[2]).Work == 0 && s.TechData - tech0 == 20;
            string queued = BlackBoxService.BoxStateText(s, ids[2]);
            Analyse(s, Day * 2 + 600);
            bool all = ids.All(id => BlackBoxService.Find(s, id).Done) && s.TechData - tech0 == 60;

            // 2 座陈列馆、3 个黑匣子：前两个并行，第三个等其中一座空出来
            CampaignState p = NewWorld(9604, 2);
            int[] pid = { Spawn(new Vector2(4f, -3f)), Spawn(new Vector2(6f, -3f)), Spawn(new Vector2(8f, -3f)) };
            foreach (int id in pid)
            {
                Kill(id);
                WorldSimulation.StepMany(3);
            }
            int ptech = p.TechData;
            Analyse(p, Day);
            bool parallel = BlackBoxService.Find(p, pid[0]).Done && BlackBoxService.Find(p, pid[1]).Done && BlackBoxService.Find(p, pid[2]).Work == 0 && p.TechData - ptech == 40;
            Analyse(p, Day + 600);
            bool third = BlackBoxService.Find(p, pid[2]).Done && p.TechData - ptech == 60;
            Expect(first && all && queued.Contains("排队等待分析（第 1 个）") && parallel && third,
                $"C2 排队与多座：1 座陈列馆按回收先后一个接一个（第 1 个日终分析完时另两个还没动，第 2 个接着分析、第 3 个写“{queued}”；3 日共 60 点）；2 座陈列馆前两个并行（1 日 40 点），第 3 个等一座空出来（2 日 60 点）；多座不递减（{first}/{all}/{parallel}/{third}；技术数据 {s.TechData - tech0} / {p.TechData - ptech}；工作中的陈列馆 {BlackBoxService.WorkingCount(p)}：{string.Join(",", BlackBoxService.GalleriesOf(p).Select(x => x.BuildingId + "=" + x.ConstructionState + "/" + x.PowerState))}）");
        }

        // ── D 负向：陈列馆停工 ───────────────────────────────────────────────────

        private static void CheckGalleryDown()
        {
            CampaignState s = NewWorld(9605, 1);
            int a = Spawn(new Vector2(5f, -3f));
            Kill(a);
            WorldSimulation.StepMany((int)(Day / 4));
            BlackBoxRecord r = BlackBoxService.Find(s, a);
            long work0 = r.Work;
            int tech0 = s.TechData;
            BuildingRecord g = HomeGridService.FindBuilding(s, GalleryIds[0]);

            // 禁用
            BuildingOps.TrySetEnabled(s, g.BuildingId, false, out _);
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(GameClock.StepHz * 120);
            bool disabled = r.Work == work0 && s.TechData == tech0 && BlackBoxService.BoxStateText(s, a).Contains("分析暂停") && BlackBoxService.StatusText(s).Contains("没有工作中的黑匣子陈列馆");
            BuildingOps.TrySetEnabled(s, g.BuildingId, true, out _);
            HomeValleyPowerGrid.Recompute(s);

            // 被摧毁（突袭 / 天气的接入点）
            WorldSimulation.RunAsSite(HomeValleyLayout.RegionId, () => HomeValleyPowerGrid.ApplyBuildingDestroyed(s, g.BuildingId));
            long work1 = r.Work;
            WorldSimulation.StepMany(GameClock.StepHz * 120);
            bool destroyed = r.Work == work1 && work1 >= work0 && !BlackBoxService.IsWorking(g);
            g.ConstructionState = BuildingConstructionState.Operational; // 测试捷径：修好（维修流程由 FG4-ECO-05 覆盖）
            g.Health = BuildingOps.MaxDurability(g.BuildingTypeId);
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(GameClock.StepHz * 60);
            bool resumed = r.Work > work1 && !r.Done;

            // 缺电：把陈列馆标成缺电（电网结算的结果字段，与情报自检同一做法；电网本身由 FG3-LOG-06 覆盖），按陈列馆的推进周期走 60 游戏秒
            g.PowerState = BuildingPowerState.Unpowered;
            long work2 = r.Work;
            Analyse(s, GameClock.StepHz * 60);
            bool unpowered = !BlackBoxService.IsWorking(g) && r.Work == work2 && BlackBoxService.WorkingCount(s) == 0;
            g.PowerState = BuildingPowerState.Powered;
            Analyse(s, GameClock.StepHz * 60);
            unpowered &= r.Work > work2;
            Expect(disabled && destroyed && resumed && unpowered,
                $"D 负向：陈列馆禁用 → 分析暂停、进度保留（{work0} 步）、不产出，黑匣子写“{BlackBoxService.BoxStateText(s, a)}”；被摧毁 → 同样暂停；修好后接着分析（{work1} → {r.Work}）；缺电（{g.PowerState}）也暂停（{disabled}/{destroyed}/{resumed}/{unpowered}）");
        }

        // ── E 远征：真实出征、掉落、拾取、带回 ───────────────────────────────────

        private static int[] PrepareDeparture(CampaignState s)
        {
            Spawn(new Vector2(4f, -4f), null, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, 120f);
            Spawn(new Vector2(6f, -4f));
            Spawn(new Vector2(8f, -4f));
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower || b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator))
                {
                    b.ConstructionState = BuildingConstructionState.Operational; // 测试捷径：信号塔修好（出发的带宽要够；修复流程由 FG4-ECO-05 覆盖）
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            return MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && m.ChassisId != HomeValleyLayout.Erc002ChassisId)
                .OrderByDescending(m => m.ChassisId == HomeValleyLayout.Erc003ChassisId).ThenByDescending(m => m.LogicId)
                .Take(3).Select(m => m.LogicId).ToArray();
        }

        private static bool Depart(int[] roster, out string why)
        {
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(roster, true);
            why = dep.Outcome == ExpeditionDepartureService.DepartureOutcome.Success ? null : $"{dep.Outcome}：{string.Join("，", dep.Reasons ?? Array.Empty<string>())}";
            return why == null;
        }

        /// <summary>区域交互候选里有没有装载这个黑匣子的一项（正式 E 交互的候选表；私有方法用反射读）。</summary>
        private static bool HasLoadCandidate(int logicId)
        {
            FracturedCityController c = WorldSimulation.FracturedCity;
            if (c == null)
            {
                return false;
            }
            const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            // 候选表只在玩家直控一台机器时生成（按 E 交互的前提）：临时接管一台还活着的远征机器，读完放开。
            System.Reflection.MethodInfo find = typeof(FracturedCityController).GetMethod("FindMarker", Any);
            System.Reflection.MethodInfo set = typeof(FracturedCityController).GetMethod("SetPossessedMarker", Any);
            System.Reflection.MethodInfo build = typeof(FracturedCityController).GetMethod("BuildInteractCandidates", Any);
            int pilot = MachineRegistry.AllRecords.Where(r => r != null && r.IsAlive && r.RegionId == FracturedCityLayout.RegionId).Select(r => r.LogicId).FirstOrDefault();
            object marker = pilot > 0 ? find?.Invoke(c, new object[] { pilot }) : null;
            if (marker == null || set == null || build == null)
            {
                return false;
            }
            set.Invoke(c, new[] { marker });
            try
            {
                if (!(build.Invoke(c, null) is System.Collections.IEnumerable list))
                {
                    return false;
                }
                string id = "quest:" + BlackBoxService.InstanceIdOf(logicId);
                foreach (object o in list)
                {
                    if (o is RegionInteractCandidate cand && cand.Id == id)
                    {
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                set.Invoke(c, new object[] { null });
            }
        }

        private static void CheckExpedition()
        {
            CampaignState s = NewWorld(9606, 1);
            int[] roster = PrepareDeparture(s);
            string why = null;
            if (roster.Length < 3 || !Depart(roster, out why))
            {
                Fail($"E 真实出征失败（{(roster.Length < 3 ? "出征名单不足 3 台" : why)}）");
                return;
            }
            s = CampaignSession.Current;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            int a = roster[2], b = roster[1], c = roster[0];
            MachineRecord ra = Rec(a);
            Kill(a);
            RegionQuestItemRecord qa = Item(s, a);
            string note = DeathNote(a);
            bool dropped = qa != null && qa.State == RegionQuestItemState.OnGround && qa.RegionId == FracturedCityLayout.RegionId
                           && Vector2.Distance(qa.Position, ra.DeathPosition) < 0.01f && ra.DeathRegionId == FracturedCityLayout.RegionId
                           && BlackBoxService.Find(s, a) == null && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BlackBoxFirstLeftBehind);
            bool noted = note.Contains("掉在破碎都市的阵亡处");
            bool candidate = HasLoadCandidate(a);
            string ground = BlackBoxService.BoxStateText(s, a);

            // b 捡起 a 的黑匣子，然后 b 也阵亡：两个黑匣子都掉在 b 的阵亡处
            FracturedCityRegion.ActionResult got = FracturedCityRegion.TryCollectQuestItem(s, qa.SalvageInstanceId, b);
            bool carried = got.Success && qa.State == RegionQuestItemState.Carried && qa.CarrierLogicId == b && BlackBoxService.BoxStateText(s, a).Contains("携带");
            Kill(b);
            MachineRecord rb = Rec(b);
            RegionQuestItemRecord qb = Item(s, b);
            bool bothDown = qa.State == RegionQuestItemState.OnGround && qa.CarrierLogicId == 0 && Vector2.Distance(qa.Position, rb.DeathPosition) < 0.01f
                            && qb != null && qb.State == RegionQuestItemState.OnGround && Vector2.Distance(qb.Position, rb.DeathPosition) < 0.01f;

            // c 捡起两个，真实撤离 → 都带回、进队列、开始分析
            bool pickA = FracturedCityRegion.TryCollectQuestItem(s, qa.SalvageInstanceId, c).Success;
            bool pickB = FracturedCityRegion.TryCollectQuestItem(s, qb.SalvageInstanceId, c).Success;
            ExpeditionReturnService.ReturnResult ret = ExpeditionReturnService.TryConfirmEvacuation();
            s = CampaignSession.Current;
            qa = Item(s, a);
            qb = Item(s, b);
            bool home = ret.Success && qa.State == RegionQuestItemState.Recovered && qb.State == RegionQuestItemState.Recovered
                        && BlackBoxService.Find(s, a) != null && BlackBoxService.Find(s, b) != null;
            int tech0 = s.TechData;
            WorldSimulation.StepMany(GameClock.StepHz * 120);
            bool analysing = s.TechData - tech0 == 2 && BlackBoxService.Find(s, a).Work > 0;
            Expect(dropped && noted && candidate && carried && bothDown && pickA && pickB && home && analysing,
                $"E 远征阵亡（真实出征破碎都市）：黑匣子掉在阵亡处（{qa?.RegionId}）、不进队列，阵亡通知“{note}”，区域交互有“装载”候选（{candidate}），面板写“{ground}”；" +
                $"另一台捡起（{carried}）后也阵亡 → 两个黑匣子都掉在它的阵亡处（{bothDown}）；第三台捡起两个、真实撤离 → 都带回进陈列馆队列（{home}{(ret.Success ? string.Empty : "；撤离失败：" + ret.FailureReason)}），120 游戏秒入账 {s.TechData - tech0} 点");
        }

        /// <summary>E2 负向（卡片）：黑匣子在远征中没被带回 → 留在区域里（不 Lost、不进队列）→ 第二次出征再取、带回。</summary>
        private static void CheckNotBroughtBack()
        {
            CampaignState s = NewWorld(9607, 1);
            int[] roster = PrepareDeparture(s);
            string why = null;
            if (roster.Length < 3 || !Depart(roster, out why))
            {
                Fail($"E2 第一次真实出征失败（{why}）");
                return;
            }
            s = CampaignSession.Current;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            int a = roster[2];
            Kill(a);
            Vector2 at = Item(s, a).Position;
            ExpeditionReturnService.ReturnResult ret = ExpeditionReturnService.TryConfirmEvacuation();
            s = CampaignSession.Current;
            RegionQuestItemRecord q = Item(s, a);
            bool stayed = ret.Success && q != null && q.State == RegionQuestItemState.OnGround && q.RegionId == FracturedCityLayout.RegionId
                          && Vector2.Distance(q.Position, at) < 0.01f && BlackBoxService.Find(s, a) == null
                          && !(FracturedCityRegion.Find(s)?.LostQuestSalvageIds ?? Array.Empty<string>()).Contains(q.SalvageInstanceId);
            string state = BlackBoxService.BoxStateText(s, a);
            WorldSimulation.StepMany(GameClock.StepHz * 30);

            // 第二次出征（补一台，出征至少 3 台）：同一个黑匣子还在原地，捡起、撤离带回
            Spawn(new Vector2(10f, -4f));
            WorldSimulation.StepMany(2);
            int[] again = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && m.ChassisId != HomeValleyLayout.Erc002ChassisId)
                .OrderByDescending(m => m.LogicId).Take(3).Select(m => m.LogicId).ToArray();
            bool departed = again.Length == 3 && Depart(again, out why);
            s = CampaignSession.Current;
            q = Item(s, a);
            bool stillThere = departed && q.State == RegionQuestItemState.OnGround && HasLoadCandidate(a);
            bool picked = departed && FracturedCityRegion.TryCollectQuestItem(s, q.SalvageInstanceId, again[0]).Success;
            ExpeditionReturnService.ReturnResult ret2 = departed ? ExpeditionReturnService.TryConfirmEvacuation() : default;
            s = CampaignSession.Current;
            q = Item(s, a);
            bool back = departed && ret2.Success && q.State == RegionQuestItemState.Recovered && BlackBoxService.Find(s, a) != null;
            Expect(stayed && stillThere && picked && back,
                $"E2 负向：没带回就撤离 → 黑匣子留在破碎都市阵亡处（不标 Lost、不进队列），面板写“{state}”；第二次出征它还在原地（区域交互可装载 {stillThere}），捡起后撤离带回进陈列馆队列（{back}）" +
                (departed ? string.Empty : $"；第二次出征失败：{why}"));
        }

        /// <summary>E3 负向：携带者活着、但不在撤离幸存名单里（失去行动能力 / 没能撤离）→ 黑匣子掉在携带者的位置，不 Lost（其它关键物照旧 Lost）。</summary>
        private static void CheckCarrierLeftBehind()
        {
            CampaignState s = NewWorld(9608, 0);
            int dead = Spawn(new Vector2(4f, -3f));
            int carrier = Spawn(new Vector2(6f, -3f));
            MachineRecord rd = Rec(dead), rc = Rec(carrier);
            rd.RegionId = FracturedCityLayout.RegionId; // 测试捷径：直接把两台记在破碎都市（真实出征由 E 覆盖），只验证撤离结算的分支
            rc.RegionId = FracturedCityLayout.RegionId;
            Vector2 stale = new Vector2(12f, 7f);
            rc.WorldPosition = stale; // 记录里的坐标平时只在存档前同步：故意写一个过期值，验证掉落用的是实时位置
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            Kill(dead);
            RegionQuestItemRecord q = Item(s, dead);
            bool picked = FracturedCityRegion.TryCollectQuestItem(s, q.SalvageInstanceId, carrier).Success;
            bool hasLive = MachineRegistry.TryGetLivePosition(carrier, out Vector2 live);
            Vector2 expect = hasLive ? live : stale;
            FracturedCityRegion.ResolveExtraction(s, Array.Empty<int>());
            bool left = picked && q.State == RegionQuestItemState.OnGround && Vector2.Distance(q.Position, expect) < 0.01f && BlackBoxService.Find(s, dead) == null
                        && !(FracturedCityRegion.Find(s)?.LostQuestSalvageIds ?? Array.Empty<string>()).Contains(q.SalvageInstanceId)
                        && (!hasLive || Vector2.Distance(live, stale) < 0.01f || Vector2.Distance(q.Position, stale) > 0.01f);
            Expect(left, $"E3 负向：携带黑匣子的机器活着但没能撤离（幸存名单为空）→ 黑匣子掉在它的{(hasLive ? $"实时位置 {q.Position}（不用记录里的过期坐标 {stale}）" : $"记录位置 {q.Position}（此刻没有实时位置，退回记录值）")}、留在区域、不标 Lost（{left}）");
        }

        /// <summary>
        /// E4（审查 P2：铸造前哨与破碎都市同一撤离结算分支，原先只靠对称性成立）：阵亡的黑匣子掉在铸造前哨；一台捡起后活着但不在幸存名单 → 掉在它的位置、不 Lost；
        /// 另一台再捡起、撤离幸存 → 带回进陈列馆队列；铸造前哨的遗失名单里始终没有黑匣子。
        /// </summary>
        private static void CheckFoundryOutpost()
        {
            CampaignState s = NewWorld(9616, 0);
            int dead = Spawn(new Vector2(4f, -3f));
            int stay = Spawn(new Vector2(6f, -3f));
            int evac = Spawn(new Vector2(8f, -3f));
            foreach (int id in new[] { dead, stay, evac })
            {
                Rec(id).RegionId = FoundryOutpostLayout.RegionId; // 测试捷径：直接记在铸造前哨（真实出征由 E 在破碎都市覆盖），只验证铸造前哨撤离结算的黑匣子分支
            }
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            Kill(dead);
            RegionQuestItemRecord q = Item(s, dead);
            bool dropped = q != null && q.State == RegionQuestItemState.OnGround && q.RegionId == FoundryOutpostLayout.RegionId && BlackBoxService.Find(s, dead) == null
                           && DeathNote(dead).Contains("掉在" + BlackBoxService.PlaceName(FoundryOutpostLayout.RegionId));
            bool pick1 = q != null && FoundryOutpostRegion.TryCollectQuestItem(s, q.SalvageInstanceId, stay).Success;
            Vector2 expect = MachineRegistry.TryGetLivePosition(stay, out Vector2 live) ? live : Rec(stay).WorldPosition;
            FoundryOutpostRegion.ResolveExtraction(s, new[] { evac }); // stay 不在幸存名单里
            bool notLost(RegionQuestItemRecord x) => !(FoundryOutpostRegion.Find(s)?.LostQuestSalvageIds ?? Array.Empty<string>()).Contains(x.SalvageInstanceId);
            bool left = pick1 && q.State == RegionQuestItemState.OnGround && q.CarrierLogicId == 0 && Vector2.Distance(q.Position, expect) < 0.01f
                        && BlackBoxService.Find(s, dead) == null && notLost(q);
            bool pick2 = left && FoundryOutpostRegion.TryCollectQuestItem(s, q.SalvageInstanceId, evac).Success;
            FoundryOutpostRegion.ResolveExtraction(s, new[] { evac });
            bool back = pick2 && q.State == RegionQuestItemState.Recovered && BlackBoxService.Find(s, dead) != null && notLost(q);
            Expect(dropped && left && back,
                $"E4 铸造前哨（同一撤离结算分支）：阵亡的黑匣子掉在铸造前哨（{dropped}，通知“{Short(DeathNote(dead))}”）；携带者活着但没撤离 → 掉在它的位置、不进遗失名单（{left}）；" +
                $"另一台捡起后撤离幸存 → 带回进陈列馆队列（{back}）");
        }

        /// <summary>E5（审查 P2：全灭字幕原先固定写“携带中的关键物已遗失”）：全灭时只有黑匣子 → 字幕写“黑匣子留在原地”；还带着别的关键物 → 写遗失件数并提黑匣子；什么都没有 → 只显示默认正文。</summary>
        private static void CheckWipeCaption()
        {
            CampaignState s = NewWorld(9617, 0);
            int dead = Spawn(new Vector2(4f, -3f));
            Rec(dead).RegionId = FracturedCityLayout.RegionId;
            string region = FracturedCityLayout.RegionId;
            string none = BlackBoxService.WipeDetail(s, region, BlackBoxService.CarriedNonBoxItems(s));
            Kill(dead);
            string boxesOnly = BlackBoxService.WipeDetail(s, region, BlackBoxService.CarriedNonBoxItems(s));
            var other = new RegionQuestItemRecord { SalvageInstanceId = "e5:other", RegionId = region, ContentId = "e5_other", State = RegionQuestItemState.Carried, CarrierLogicId = dead };
            s.RegionQuestItems = s.RegionQuestItems.Concat(new[] { other }).ToArray();
            int carried = BlackBoxService.CarriedNonBoxItems(s);
            string both = BlackBoxService.WipeDetail(s, region, carried);
            GameSettings.SetLanguage(GameLanguage.En);
            string bothEn = BlackBoxService.WipeDetail(s, region, carried);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            s.RegionQuestItems = s.RegionQuestItems.Where(x => x != other).ToArray();
            bool ok = none == null && boxesOnly == "黑匣子留在原地，下次远征可以再取" && carried == 1
                      && both == "携带中的 1 件关键物已遗失；黑匣子留在原地，下次远征可以再取" && bothEn.Contains("black boxes stay where they fell");
            Expect(ok, $"E5 全灭字幕：没有关键物 → 只显示默认正文（{none ?? "null"}）；只有黑匣子 → “{boxesOnly}”；还带着 1 件别的关键物 → “{both}” / “{bothEn}”（文本键，中英文）");
        }

        // ── F 纪念墙 ──────────────────────────────────────────────────────────

        private static void CheckMemorial()
        {
            CampaignState s = NewWorld(9609, 1);
            int a = Spawn(new Vector2(4f, -3f), "铁锤");
            int b = Spawn(new Vector2(6f, -3f));
            int c = Spawn(new Vector2(8f, -3f), "老七");
            int old = Spawn(new Vector2(10f, -3f));
            MachineRegistry.TryMarkExperience(a, MachineExperienceFlags.FirstJob);
            Rec(a).KillCount = 3;
            Rec(a).JobsCompleted = 5;
            Kill(a);
            WorldSimulation.StepMany(GameClock.StepHz * 10);
            Rec(c).RegionId = FracturedCityLayout.RegionId; // 测试捷径：记在破碎都市阵亡（真实出征的阵亡由 E 覆盖），验证按地点排序
            Kill(c);
            WorldSimulation.StepMany(GameClock.StepHz * 10);
            Kill(b);
            Kill(old);
            MachineRecord ro = Rec(old);
            ro.DeathTick = 0; // 模拟 FG5-RND-06 之前的旧档：没记时间与地点
            ro.DeathRegionId = null;
            BlackBoxService.Touch();

            List<MachineRecord> byTime = BlackBoxService.Memorial(false);
            List<MachineRecord> byPlace = BlackBoxService.Memorial(true);
            int[] timeIds = byTime.Select(m => m.LogicId).ToArray();
            int[] placeIds = byPlace.Select(m => m.LogicId).ToArray();
            bool allDead = timeIds.Length == MachineRegistry.AllRecords.Count(m => !m.IsAlive) && new[] { a, b, c, old }.All(timeIds.Contains);
            bool timeOrder = Array.IndexOf(timeIds, b) < Array.IndexOf(timeIds, c) && Array.IndexOf(timeIds, c) < Array.IndexOf(timeIds, a) && timeIds.Last() == old;
            string home = BlackBoxService.PlaceName(HomeValleyLayout.RegionId), city = BlackBoxService.PlaceName(FracturedCityLayout.RegionId);
            bool homeFirst = string.CompareOrdinal(home, city) < 0;
            int ia = Array.IndexOf(placeIds, a), ib = Array.IndexOf(placeIds, b), ic = Array.IndexOf(placeIds, c);
            bool placeOrder = (homeFirst ? ib < ia && ia < ic : ic < ib && ib < ia) && placeIds.Last() == old;
            string ta = BlackBoxService.MemorialText(s, Rec(a));
            MachineRecord ma = Rec(a);
            bool textA = ta.StartsWith("铁锤 · 编号 #" + ma.DisplayNumber, StringComparison.Ordinal) && ta.Contains(MachineNaming.Model(ma))
                         && ta.Contains("阵亡：" + home + "（") && ta.Contains(GameClock.FormatDayTime(ma.DeathTick / (double)GameClock.StepHz))
                         && ta.Contains(MachineExperienceFlags.DisplayName(MachineExperienceFlags.FirstJob)) && ta.Contains("击毁 3") && ta.Contains("完成工作 5")
                         && ta.Contains("黑匣子：");
            string tc = BlackBoxService.MemorialText(s, Rec(c));
            string to = BlackBoxService.MemorialText(s, ro);
            bool textC = tc.StartsWith("老七 · 编号 #", StringComparison.Ordinal) && tc.Contains("阵亡：" + city) && tc.Contains("留在" + city);
            bool textOld = to.Contains("地点未记录") && to.Contains("时间未记录（旧存档）");
            string tb = BlackBoxService.MemorialText(s, Rec(b));
            string modelB = MachineNaming.Model(Rec(b));
            string headB = tb.Split('\n')[0];
            bool textB = headB == modelB + " · 编号 #" + Rec(b).DisplayNumber; // 没起名：型号只写一遍（审查 P2）
            Expect(allDead && timeOrder && placeOrder && textA && textB && textC && textOld,
                $"F FGT-RND-009 纪念墙名单正确：列出全部 {timeIds.Length} 台阵亡机器（{allDead}）；按时间最近的在前、旧档没记时间的在最后（{timeOrder}）；按地点（{home} / {city}）分组、同一地点按时间（{placeOrder}）；" +
                $"一行写名字 · 编号 · 型号、阵亡地点与时间、经历与统计、黑匣子去向：“{Short(ta)}”；没起名的型号只写一遍：“{headB}”；远征阵亡的写“{Short(tc)}”；旧档的写“{Short(to)}”");
        }

        // ── G 存读档 ──────────────────────────────────────────────────────────

        [Serializable]
        private sealed class TestEnvelope
        {
            public int SchemaVersion;
            public int ContentVersion;
            public string Checksum;
            public string WrittenAtUtc;
            public string ProductVersion;
            public string CardJson;
            public string PayloadJson;
        }

        private static string FullSnapshot(CampaignState s)
        {
            var sb = new StringBuilder(BlackBoxService.Snapshot(s));
            foreach (MachineRecord m in BlackBoxService.Memorial(false))
            {
                sb.Append("\n#").Append(m.LogicId).Append(':').Append(m.DeathTick).Append(':').Append(m.DeathRegionId).Append(':')
                    .Append(m.DeathPosition.x.ToString("0.###", CultureInfo.InvariantCulture)).Append(',').Append(m.DeathPosition.y.ToString("0.###", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static CampaignState RestoreSlot(out string fail)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            BlackBoxService.ResetSessionState();
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

        private static void RewriteSlot(Func<string, string> payloadEdit)
        {
            string path = CampaignSaveService.SlotPath(Slot);
            TestEnvelope env = JsonUtility.FromJson<TestEnvelope>(File.ReadAllText(path));
            env.PayloadJson = payloadEdit(env.PayloadJson);
            env.Checksum = CampaignSaveService.ComputeChecksum(env.PayloadJson, env.CardJson);
            File.WriteAllText(path, JsonUtility.ToJson(env));
        }

        /// <summary>存档正文里 <c>"BlackBoxes":{…}</c> 这一段的起止（按括号配对，跳过字符串）。</summary>
        private static bool Span(string payload, string key, out int start, out int end)
        {
            start = payload.IndexOf(key, StringComparison.Ordinal);
            end = -1;
            if (start < 0)
            {
                return false;
            }
            int depth = 0;
            bool inString = false;
            for (int i = start + key.Length - 1; i < payload.Length; i++)
            {
                char ch = payload[i];
                if (inString)
                {
                    if (ch == '\\')
                    {
                        i++;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }
                    continue;
                }
                if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}' && --depth == 0)
                {
                    end = i + 1;
                    return true;
                }
            }
            return false;
        }

        private static void LayScenario(CampaignState s, out int[] ids)
        {
            ids = new[] { Spawn(new Vector2(4f, -3f), "铁锤"), Spawn(new Vector2(6f, -3f)), Spawn(new Vector2(8f, -3f)) };
            Kill(ids[0]);
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            Kill(ids[1]);
            Rec(ids[2]).RegionId = FracturedCityLayout.RegionId; // 测试捷径：一台记在破碎都市阵亡（黑匣子留在区域地面；真实出征由 E 覆盖）
            Kill(ids[2]);
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(9610, 1);
            LayScenario(s, out int[] ids);
            WorldSimulation.StepMany(GameClock.StepHz * 400); // 第一个黑匣子分析到三分之一
            string before = FullSnapshot(s);
            int k0 = B(s).Boxes[0].PointsGranted;
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(GameClock.StepHz * 300);
            string continuous = FullSnapshot(s);

            CampaignState l = RestoreSlot(out string fail);
            string loaded = l == null ? string.Empty : FullSnapshot(l);
            if (l != null)
            {
                WorldSimulation.StepMany(GameClock.StepHz * 300);
            }
            string after = l == null ? string.Empty : FullSnapshot(l);
            bool content = before.Contains("\"Work\":") && before.Contains("blackbox:" + ids[2] + ":0:" + FracturedCityLayout.RegionId) && B(s).Boxes.Length == 2;
            Expect(save.Success && l != null && loaded == before && after == continuous && content,
                "G1 真文件存读档：黑匣子分析进度 / 已入账点数 / 队列、留在区域里的黑匣子（地点与坐标）、纪念墙的阵亡时刻与地点写进存档，读档后逐字段一致；读档后接着跑 300 秒与不存档连续跑逐字段一致"
                + (loaded == before ? string.Empty : $"\n存前：{before}\n读后：{loaded}") + (after == continuous ? string.Empty : $"\n连续：{continuous}\n读档：{after}")
                + (fail != null ? "；读档失败：" + fail : string.Empty));

            // G2 旧档（正文里没有黑匣子域）：读档补成空域，已回收的黑匣子按区域关键物补回队列（从 0 开始分析），不报错
            bool stripped = false;
            RewriteSlot(p =>
            {
                if (!Span(p, "\"BlackBoxes\":{", out int a, out int b))
                {
                    return p;
                }
                stripped = true;
                int from = a > 0 && p[a - 1] == ',' ? a - 1 : a;
                int to = from == a && b < p.Length && p[b] == ',' ? b + 1 : b;
                return p.Remove(from, to - from);
            });
            CampaignState l2 = RestoreSlot(out string fail2);
            BlackBoxState f2 = l2 == null ? null : B(l2);
            bool rebuilt = f2 != null && f2.Boxes.Length == 2 && f2.Boxes.All(x => x.Work == 0 && !x.Done) && Item(l2, ids[2])?.State == RegionQuestItemState.OnGround;
            // 账本幂等（AC-ECO-001“最多产物一次”）：存档里已入过账的前 k0 点重新分析到时不再给，超过之后照常入账。
            int tech = l2?.TechData ?? 0;
            if (l2 != null)
            {
                WorldSimulation.StepMany(GameClock.StepHz * (60 * k0 + 30));
            }
            bool noDouble = l2 != null && l2.TechData == tech;
            if (l2 != null)
            {
                WorldSimulation.StepMany(GameClock.StepHz * 40);
            }
            bool works = l2 != null && l2.TechData == tech + 1 && B(l2).PointsProduced == 1;
            Expect(stripped && rebuilt && k0 > 0 && noDouble && works,
                $"G2 旧档兼容（正文里没有黑匣子域）：读档补成空域、已回收的黑匣子按区域关键物补回队列（{f2?.Boxes.Length} 个，从 0 开始），留在区域里的还在原地；" +
                $"已入过账的前 {k0} 点重新分析到时不重复给（账本幂等 {noDouble}），之后照常入账（{works}）"
                + (fail2 != null ? "；读档失败：" + fail2 : string.Empty));
        }

        // ── H / I 暂停、倍速、观察 ───────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, 1, observe);
            pausedHeld = true;
            LayScenario(s, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = FullSnapshot(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = FullSnapshot(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 200;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 1200)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return FullSnapshot(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(9611, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference.Contains("\"PointsGranted\":3"),
                "H 暂停中（120 帧）黑匣子分析不动；0.5x / 1x / 2x / 3x 跑同样的 200 游戏秒（分析进度、入账点数、技术数据）逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(9612, true, 1f, false, out _);
            string unseen = RunScenario(9612, false, 1f, false, out _);
            Expect(seen == unseen && seen.Contains("\"PointsGranted\":3"), "I 观察与不观察家园时跑 200 游戏秒，黑匣子分析与入账逐字段一致（FGR-BASE-021：远征时家园照常分析）"
                                                                  + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── J 面板（真 UXML）──────────────────────────────────────────────────────

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

        private static bool HasCjk(string text) => text.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF || ch >= 0x3000 && ch <= 0x303F || ch >= 0xFF00 && ch <= 0xFFEF);

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(9613, 0);
            VisualElement root = F.MountUxml(PanelUxml, out GameObject go);
            VisualElement rroot = F.MountUxml(RosterUxml, out GameObject rgo);
            BlackBoxPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var panel = go.AddComponent<BlackBoxPanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(RowUxml));
                InputRouter.Reset();

                // J1 空状态（两页）
                BlackBoxPanelUIToolkit.Open(BlackBoxPanelUIToolkit.TabBoxes);
                panel.Refresh(force: true);
                bool emptyBoxes = BlackBoxPanelUIToolkit.IsOpen && panel.PanelVisible && panel.TitleText == "黑匣子陈列馆" && panel.RowCount == 0
                                  && panel.EmptyText.Contains("还没有黑匣子") && panel.StatusText.Contains("没有工作中的黑匣子陈列馆") && !panel.SortBarVisible
                                  && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BlackBoxFirstOpen);
                Click(panel.TabMemorialButton);
                bool emptyWall = panel.CurrentTab == BlackBoxPanelUIToolkit.TabMemorial && panel.RowCount == 0 && panel.EmptyText.Contains("纪念墙上还没有名字") && panel.SortBarVisible;
                Expect(emptyBoxes && emptyWall, $"J1 打开陈列馆面板（模态）：黑匣子页空状态“{Short(panel.EmptyText)}”、状态写怎么建陈列馆；纪念墙页空状态、显示排序按钮（{emptyBoxes}/{emptyWall}）");
                BlackBoxPanelUIToolkit.Close();

                // J2 有数据：家园两台阵亡 + 一台远征阵亡（留在区域）
                AddGalleries(s, 1, 9613);
                WorldSimulation.StepMany(3);
                int a = Spawn(new Vector2(4f, -3f), "铁锤");
                int b = Spawn(new Vector2(6f, -3f));
                int c = Spawn(new Vector2(8f, -3f), "老七");
                Kill(a);
                WorldSimulation.StepMany(GameClock.StepHz * 30);
                Kill(b);
                Rec(c).RegionId = FracturedCityLayout.RegionId; // 测试捷径：远征阵亡（真实出征由 E 覆盖）
                Kill(c);
                WorldSimulation.StepMany(6);

                // 名册“纪念墙…”入口：关名册、打开纪念墙页
                var roster = rgo.AddComponent<RosterPanelUIToolkit>();
                roster.BindView(rroot);
                RosterPanelUIToolkit.Open();
                bool rosterBtn = roster.MemorialButton != null && roster.MemorialButton.text == "纪念墙…";
                Click(roster.MemorialButton);
                panel.Refresh(force: true);
                bool fromRoster = rosterBtn && !RosterPanelUIToolkit.IsOpen && BlackBoxPanelUIToolkit.IsOpen && panel.CurrentTab == BlackBoxPanelUIToolkit.TabMemorial;
                // 默认按时间：最近阵亡的（c）在前
                bool wallTime = panel.RowCount == 3 && panel.RowLogicId(0) == c && panel.RowLogicId(2) == a && panel.RowText(2).StartsWith("铁锤 · 编号 #", StringComparison.Ordinal)
                                && panel.TabMemorialButton.text == "纪念墙（3）" && !panel.SortByPlace;
                Click(panel.SortPlaceButton);
                string home = BlackBoxService.PlaceName(HomeValleyLayout.RegionId), city = BlackBoxService.PlaceName(FracturedCityLayout.RegionId);
                int cityRow = Enumerable.Range(0, panel.RowCount).First(i => panel.RowLogicId(i) == c);
                bool wallPlace = panel.SortByPlace && (string.CompareOrdinal(home, city) < 0 ? cityRow == 2 && panel.RowLogicId(0) == b : cityRow == 0 && panel.RowLogicId(1) == b);
                Click(panel.SortTimeButton);
                bool backTime = !panel.SortByPlace && panel.RowLogicId(0) == c;
                Expect(fromRoster && wallTime && wallPlace && backTime,
                    $"J2 名册“纪念墙…”入口打开纪念墙页（{fromRoster}）；按时间最近的在前（第一行 #{panel.RowLogicId(0)}）、“按地点”把{city}的那台排到{(string.CompareOrdinal(home, city) < 0 ? "最后" : "最前")}（{wallPlace}）、再点“按时间”恢复；页签写名单人数");

                // J3 黑匣子页：区域里的在前（高亮），然后分析中 / 排队
                Click(panel.TabBoxesButton);
                bool boxes = panel.CurrentTab == BlackBoxPanelUIToolkit.TabBoxes && panel.RowCount == 3 && panel.RowLogicId(0) == c && panel.RowHasClass(0, "bb-row-field")
                             && panel.RowText(0).Contains("留在" + city) && panel.RowText(1).Contains("分析中") && panel.RowText(2).Contains("排队等待分析（第 1 个）")
                             && panel.StatusText.Contains("1 座陈列馆工作中") && panel.TabBoxesButton.text == "黑匣子（3）";
                Expect(boxes, $"J3 黑匣子页：远征里的那个在前、高亮、写“{Short(panel.RowText(0))}”；分析中的写“{Short(panel.RowText(1))}”；排队的写“{Short(panel.RowText(2))}”；状态“{Short(panel.StatusText)}”");

                // J4 英文界面
                var bad = new List<string>();
                var texts = new List<string>();
                try
                {
                    GameSettings.SetLanguage(GameLanguage.En);
                    foreach (int tab in new[] { BlackBoxPanelUIToolkit.TabBoxes, BlackBoxPanelUIToolkit.TabMemorial })
                    {
                        panel.SelectTab(tab);
                        panel.Refresh(force: true);
                        texts.Add(panel.TitleText);
                        texts.Add(panel.StatusText);
                        texts.Add(panel.FooterText);
                        texts.Add(panel.TabBoxesButton.text);
                        texts.Add(panel.TabMemorialButton.text);
                        texts.Add(panel.SortTimeButton.text);
                        texts.Add(panel.SortPlaceButton.text);
                        for (int i = 0; i < panel.RowCount; i++)
                        {
                            texts.Add(panel.RowText(i).Replace("铁锤", "N").Replace("老七", "N")); // 玩家起的名字不是界面文字
                        }
                    }
                    texts.Add(BlackBoxService.StatusOf(s, HomeGridService.FindBuilding(s, GalleryIds[0])).Reason.Replace("铁锤", "N"));
                    texts.Add(GameText.Format("machine.feedback.destroyed_bb", "#1", GameText.Format("blackbox.where.field", BlackBoxService.PlaceName(FracturedCityLayout.RegionId))));
                    foreach (string raw in texts)
                    {
                        string t = raw ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(t) || HasCjk(t) || GameText.ContainsMarker(t))
                        {
                            bad.Add("“" + Short(t) + "”");
                        }
                    }
                }
                finally
                {
                    GameSettings.SetLanguage(GameLanguage.ZhCn);
                }
                Expect(bad.Count == 0, $"J4 英文界面：标题、状态、页签、排序按钮、黑匣子行、纪念墙行、建筑状态、阵亡通知共 {texts.Count} 段文字没有中文与缺键标记" + (bad.Count > 0 ? "；有问题：" + string.Join("、", bad) : string.Empty));

                // J5 关闭与布局探针
                Click(panel.CloseButton);
                bool closed = !BlackBoxPanelUIToolkit.IsOpen && !panel.PanelVisible;
                string probe = UiToolkitLayoutProbe.Probe(PanelUxml, "BlackBoxRoot", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("BlackBoxRoot")?.RemoveFromClassList("uk-hidden");
                });
                Expect(closed && probe.Contains("PASS") && !probe.Contains("FAIL"), $"J5 关闭按钮收起面板（{closed}）；陈列馆面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]));
            }
            finally
            {
                BlackBoxPanelUIToolkit.Close();
                BlackBoxPanelUIToolkit.InWorldOverrideForTests = false;
                RosterPanelUIToolkit.Close();
                InputRouter.Reset();
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(rgo);
            }
        }

        // ── K 建筑面板入口与状态 ───────────────────────────────────────────────────

        private static void CheckBuildingPanel()
        {
            CampaignState s = NewWorld(9614, 1);
            BuildingStatus idle = BuildingStatusService.Evaluate(s, HomeGridService.FindBuilding(s, GalleryIds[0]));
            int a = Spawn(new Vector2(4f, -3f), "铁锤");
            int b = Spawn(new Vector2(6f, -3f));
            Kill(a);
            Kill(b);
            WorldSimulation.StepMany(GameClock.StepHz * 61);
            BuildingStatus working = BuildingStatusService.Evaluate(s, HomeGridService.FindBuilding(s, GalleryIds[0]));
            VisualElement root = F.MountUxml(ProdUxml, out GameObject go);
            VisualElement broot = F.MountUxml(PanelUxml, out GameObject bgo);
            BlackBoxPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var bb = bgo.AddComponent<BlackBoxPanelUIToolkit>();
                bb.BindView(broot);
                var panel = go.AddComponent<ProductionPanelUIToolkit>();
                panel.BindView(root);
                ProductionPanelUIToolkit.Open(GalleryIds[0]);
                bool shown = panel.BlackBoxButton != null && !panel.BlackBoxButton.ClassListContains("bn-hidden") && panel.BlackBoxButton.text == "陈列馆…"
                             && panel.IntelButton.ClassListContains("bn-hidden");
                Click(panel.BlackBoxButton);
                bool opened = BlackBoxPanelUIToolkit.IsOpen && bb.CurrentTab == BlackBoxPanelUIToolkit.TabBoxes && !ProductionPanelUIToolkit.IsOpen;
                BlackBoxPanelUIToolkit.Close();
                string other = s.BuildingRecords.First(x => x != null && BuildingOps.IsWarehouse(x)).BuildingId;
                ProductionPanelUIToolkit.Open(other);
                bool hiddenElse = panel.BlackBoxButton.ClassListContains("bn-hidden");
                Expect(idle.Kind == BuildingStatusKind.Idle && idle.Reason == GameText.Get("bs.reason.blackbox_idle")
                       && working.Kind == BuildingStatusKind.Working && working.Reason.Contains("铁锤") && working.Reason.Contains("技术数据 +1/20") && working.Reason.Contains("还有 1 个排队")
                       && shown && opened && hiddenElse,
                    $"K 建筑状态（B05）：空闲“{idle.Reason}”、分析中“{working.Reason}”；点陈列馆打开的建筑面板有“{panel.BlackBoxButton?.text}”入口、点它打开陈列馆面板（{opened}），其它建筑不显示");
            }
            finally
            {
                ProductionPanelUIToolkit.Close();
                BlackBoxPanelUIToolkit.Close();
                BlackBoxPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(bgo);
            }
        }

        // ── L 性能 ──────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(9615, 4);
            for (int i = 0; i < 200; i++)
            {
                int id = Spawn(new Vector2(3f + i % 20, -3f - i / 20));
                Kill(id);
            }
            WorldSimulation.StepMany(6);
            BlackBoxService.ResetStepStats();
            var sw = Stopwatch.StartNew();
            const int steps = 5000;
            for (int i = 0; i < steps; i++)
            {
                BlackBoxService.Step(s, 3);
            }
            sw.Stop();
            double perStep = sw.Elapsed.TotalMilliseconds / steps;
            // 审查 P1：EnsureState 经 CampaignFgStateDomains.EnsureAll 每个研究推进周期都会调到（ResearchService.Step 开头），平时必须早退（O(1)、不重建数组）。
            BlackBoxRecord[] boxesRef = B(s).Boxes;
            RegionQuestItemRecord[] itemsRef = s.RegionQuestItems;
            const int ensures = 20000;
            var ew = Stopwatch.StartNew();
            for (int i = 0; i < ensures; i++)
            {
                BlackBoxService.EnsureState(s);
            }
            ew.Stop();
            double perEnsure = ew.Elapsed.TotalMilliseconds / ensures;
            bool noRebuild = ReferenceEquals(boxesRef, B(s).Boxes) && ReferenceEquals(itemsRef, s.RegionQuestItems);
            const int alls = 2000;
            var aw = Stopwatch.StartNew();
            for (int i = 0; i < alls; i++)
            {
                CampaignFgStateDomains.EnsureAll(s);
            }
            aw.Stop();
            double perAll = aw.Elapsed.TotalMilliseconds / alls;
            const int rsteps = 2000;
            var rw = Stopwatch.StartNew();
            for (int i = 0; i < rsteps; i++)
            {
                ResearchService.Step(s, 3, GameClock.StepHz);
            }
            rw.Stop();
            double perResearch = rw.Elapsed.TotalMilliseconds / rsteps;
            // 读档后第一次（数组换了）的整次扫描：O(关键物 + 黑匣子)，不是平方。
            var sw2 = Stopwatch.StartNew();
            BlackBoxService.ResetSessionState();
            BlackBoxService.EnsureState(s);
            sw2.Stop();
            double rescan = sw2.Elapsed.TotalMilliseconds;
            Expect(noRebuild && B(s).Boxes.Length >= 200,
                $"L1 EnsureState 平时早退：{ensures} 次调用后黑匣子数组与关键物数组都没被重建（{noRebuild}；{B(s).Boxes.Length} 个已回收黑匣子）");
            var mw = Stopwatch.StartNew();
            List<MachineRecord> wall = BlackBoxService.Memorial(true);
            int rows = 0;
            foreach (MachineRecord m in wall)
            {
                rows += BlackBoxService.MemorialText(s, m).Length > 0 ? 1 : 0;
            }
            mw.Stop();
            var bw = Stopwatch.StartNew();
            List<int> order = BlackBoxService.BoxOrder(s);
            int boxRows = 0;
            foreach (int id in order)
            {
                boxRows += BlackBoxService.BoxRowText(s, id).Length > 0 ? 1 : 0;
            }
            bw.Stop();
            PerfLines.Add($"黑匣子每个推进周期（4 座陈列馆、{B(s).Boxes.Length} 个已回收黑匣子）{perStep * 1000.0:F2} µs（每 3 个世界步一次 = 20 Hz）；EnsureState 早退 {perEnsure * 1000.0:F3} µs / 次；" +
                          $"EnsureAll {perAll * 1000.0:F2} µs / 次；ResearchService.Step {perResearch * 1000.0:F2} µs / 次；读档后整次扫描 {rescan:F3} ms；" +
                          $"纪念墙 {rows} 行排序 + 拼文字 {mw.Elapsed.TotalMilliseconds:F2} ms、黑匣子页 {boxRows} 行 {bw.Elapsed.TotalMilliseconds:F2} ms（只在面板刷新时）");
            PerfGate.Expect(true,
                $"L 性能（{B(s).Boxes.Length} 个已回收黑匣子）：黑匣子每个推进周期 {perStep:F4} ms（阈值 0.05 ms）；EnsureState 每次 {perEnsure:F5} ms（阈值 0.005 ms，平时早退）；" +
                $"EnsureAll 每次 {perAll:F4} ms（阈值 0.2 ms）；ResearchService.Step 每次 {perResearch:F4} ms（阈值 0.5 ms，同研发自检）；读档后整次扫描 {rescan:F3} ms（阈值 1 ms，线性）；" +
                $"纪念墙 {rows} 行一次刷新 {mw.Elapsed.TotalMilliseconds:F2} ms（阈值 30 ms）；黑匣子页 {boxRows} 行一次刷新 {bw.Elapsed.TotalMilliseconds:F2} ms（阈值 10 ms）（Editor batchmode，真机 HybridCLR 另测 FG15-SYS-02）",
                new[]
                {
                    PerfGate.Le(perStep, 0.05, "黑匣子每步 ms"), PerfGate.Le(perEnsure, 0.005, "EnsureState ms"), PerfGate.Le(perAll, 0.2, "EnsureAll ms"),
                    PerfGate.Le(perResearch, 0.5, "研究推进 ms"), PerfGate.Le(rescan, 1.0, "整次扫描 ms"),
                    PerfGate.Le(mw.Elapsed.TotalMilliseconds, 30.0, "纪念墙刷新 ms"), PerfGate.Le(bw.Elapsed.TotalMilliseconds, 10.0, "黑匣子页刷新 ms"),
                }, Expect, Line);
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
