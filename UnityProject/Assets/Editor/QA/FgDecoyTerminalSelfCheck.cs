using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
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
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG12-EVT-05 诱饵终端的自动验收（FG10 FGR-EVT-040 / 041，FGT-EVT-006；承接 DEBT-FG12EVT01-02 的诱饵部分）。
    /// 全部起真实世界（家园星球战斗内核 + 事件导演 + 真实 decoy_terminal 处理器 + 真实工单到场入口 HomeValleyWorkOrders.OnArrivedAtWork），走 WorldSimulation.StepMany / Frame。
    /// D 数据；A1 B25 多种子：诱饵只在阵营领地、确定、有普通终端也有诱饵；A2 外观破绽（悬停与普通终端同名、多一行破绽）；
    /// B1 不扫描被伏击（触发 → 导演 → 伏击单位 → 全部击毁 → 三选一数据包）；B2 扫描识别（带标记器的机器靠近即识别）→ 安全拆除（少量技术数据、不伏击、无数据包）；
    /// B3 带标记器的工作机到场先扫描、不触发；B4 雷区变体；N1 负向“同一个诱饵终端被重复触发”；N2 伏击超时（剩余撤走、无奖励）；N3 两个终端先后触发排队；
    /// N4 “事件频率：关闭”照样触发；N5 不带标记器 / 太远不识别；T1 暂停与 0.5x～3x；T2 观察一致；S1 伏击中与识别后存读档；R1 内核重建补回伏击单位；P1 性能。
    /// 已并入 CellFrameworkValidate.RunAll（段名 FgDecoyTerminalSelfCheck）。
    /// </summary>
    public static class FgDecoyTerminalSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string BpMarkCombat = "bp-decoy-mark-combat";
        private const string BpPlainCombat = "bp-decoy-plain-combat";
        private const string BpMarkWorker = "bp-decoy-mark-worker";
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("QA/自检/FG 诱饵终端")]
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
            Line("\n[诱饵终端] 生成器点位、外观破绽、标记器扫描识别 / 安全拆除、交互触发伏击 / 雷区、三选一、防重复触发、倍速 / 观察 / 存读档（FG12-EVT-05，FGT-EVT-006）");
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
            string dir = Path.Combine(Path.GetTempPath(), "bingames-fgdecoy-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                CampaignSaveService.SaveDirectoryOverrideForTests = dir;
                Directory.CreateDirectory(dir);
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "诱饵判定 / 扫描 / 处理器在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），伏击单位生成 / 计数 / 地雷伤害在 Main/Sim（Editor 下 Burst 未必开）；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckSeeds);
                Step(CheckTell);
                Step(CheckAmbushPath);
                Step(CheckScanAndDisarm);
                Step(CheckWorkerScanOnArrival);
                Step(CheckMines);
                Step(CheckMinesNoUnits);
                Step(CheckIdentifiedEnRoute);
                Step(CheckRepeat);
                Step(CheckLegacyRead);
                Step(CheckExpiry);
                Step(CheckQueue);
                Step(CheckFrequencyOff);
                Step(CheckNoScan);
                Step(CheckTimingMatrix);
                Step(CheckObserved);
                Step(CheckSaveLoad);
                Step(CheckRebuild);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"诱饵终端自检抛异常：{e}");
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
                DecoyTerminalService.TuningOverrideForTests = null;
                DecoyTerminalService.ResetSessionState();
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
                MachineLoadoutRegistry.Clear();
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
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
            Line($"  · [诱饵终端] 断言通过 {_pass}，失败 {_fail}");
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

        private static CombatSite Site => WorldSimulation.Home?.Combat;

        private static CampaignState World(int seed, bool observe = false)
        {
            EventDirectorService.ResetForTests();
            DecoyTerminalService.ResetSessionState();
            HomeValleyPowerGrid.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            MachineLoadoutRegistry.Clear();
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, 3000);
            SignalCoreService.EnsureInitialized(s);
            EventDirectorService.Install();
            EventDirectorService.Register(new DecoyTerminalService.Handler());
            s.Progress.Act = 2;
            AddBlueprint(s, BpMarkCombat, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, ComponentCatalog.FuncMarkerId, null, Array.Empty<string>()));
            AddBlueprint(s, BpPlainCombat, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()));
            AddBlueprint(s, BpMarkWorker, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc001ChassisId, null, ComponentCatalog.FuncMarkerId, null, Array.Empty<string>()));
            WorldSimulation.StepMany(2);
            return s;
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        /// <summary>登记一台机器（并登记装配）。<paramref name="bp"/> = null 用工作机的默认蓝图（没有标记器）。</summary>
        private static int Spawn(CampaignState s, string chassis, string bp, Vector2 at)
        {
            bool custom = bp != null;
            bp ??= HomeValleyLayout.BlueprintErc001Id;
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, bp, HomeValleyLayout.RegionId, at, 400f, 400f);
            if (!r.Success)
            {
                Fail($"测试准备：登记机器失败：{r.Message}");
                return 0;
            }
            MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec);
            var reg = MachineLoadoutRegistry.Register(s, r.LogicId, bp, custom ? 1 : rec.BlueprintVersion);
            if (!reg.Success)
            {
                Fail($"测试准备：登记装配 {bp} 失败：{reg.Message}");
            }
            return r.LogicId;
        }

        private static Vector2 Core(CampaignState s) => SignalUplinkService.CorePosition(s);

        /// <summary>这个世界里阵营领地中的全部诱饵终端（离核心由近到远）。</summary>
        private static List<WorldFeature> Decoys(CampaignState s, bool decoy = true)
        {
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            var all = new List<WorldFeature>();
            var feats = new List<WorldFeature>();
            foreach (PlannedTerritory t in ctx.Plan.Territories.Where(t => t.IsFaction))
            {
                feats.Clear();
                WorldFeatures.Query(ctx, t.CenterX - t.Radius, t.CenterY - t.Radius, t.CenterX + t.Radius, t.CenterY + t.Radius, feats);
                all.AddRange(feats.Where(f => f.Kind == WorldFeatureKind.Terminal && DecoyTerminalService.IsDecoy(s, f) == decoy && all.All(x => x.Id != f.Id)));
            }
            return all.OrderBy(f => f.Distance).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        }

        private static WorldFeature FirstDecoy(CampaignState s, int variant = -1) =>
            Decoys(s).FirstOrDefault(f => variant < 0 || DecoyTerminalService.VariantOf(s, f) == variant);

        private static EventDirectorInstanceRecord Inst(CampaignState s) =>
            EventDirectorService.Active(s).FirstOrDefault(r => r != null && r.EventId == DecoyTerminalService.EventId);

        private static List<EventDirectorHistoryRecord> Hist(CampaignState s) =>
            EventDirectorService.History(s).Where(h => h.EventId == DecoyTerminalService.EventId).ToList();

        private static HomeExplorationState Ex(CampaignState s) => HomeExplorationService.StateOf(s);

        /// <summary>揭开 → 给机器下“读取”单（右键同一入口）→ 到场（真实到场入口）。返回工单（到场后的状态在工单里）。</summary>
        private static WorkOrderRecord OrderAndArrive(CampaignState s, WorldFeature f, int machine, out bool ordered)
        {
            HomeGridService.RevealArea(s, new Vector2(f.X, f.Y), 6f);
            HomeValleyWorkOrders.WorkOrderOpResult r = HomeExplorationService.OrderRecovery(s, f.Id, machine);
            ordered = r.Success;
            WorkOrderRecord o = s.WorkOrders?.LastOrDefault(x => x.TargetId == f.Id);
            if (o != null)
            {
                HomeValleyWorkOrders.OnArrivedAtWork(s, o.WorkOrderId);
            }
            return o;
        }

        /// <summary>伏击单位全部击毁：在终端处一次大范围打击（测试机器都在家园，不在圈里）。</summary>
        private static void KillAmbush(Vector2 at) => Site?.StrikeCircle(at, DecoyTerminalService.AmbushRing + 8f, 1e7f);

        private static int Alive(CampaignState s, int serial) =>
            DecoyTerminalService.Ambush(s, serial) is DecoyAmbushRecord a && Site != null ? Site.CountSiegeGroup(a.Key) : -1;

        private static string Snap(CampaignState s)
        {
            HomeExplorationState x = Ex(s);
            var sb = new StringBuilder(JsonUtility.ToJson(s.DirectorEvents));
            sb.Append("|id=").Append(string.Join(",", x.DecoysIdentified)).Append("|sp=").Append(string.Join(",", x.DecoysSprung))
                .Append("|dis=").Append(string.Join(",", x.DecoysDisarmed)).Append("|q=").Append(string.Join(",", x.DecoyQueue)).Append("|tech=").Append(x.DecoyTechData);
            foreach (DecoyAmbushRecord a in x.DecoyAmbushes)
            {
                sb.Append($"|amb:{a.Serial}:{a.TerminalId}:{a.Variant}:{a.Key}:{a.Unfolded}:{a.Alive}:{(Site != null ? Site.CountSiegeGroup(a.Key) : -1)}");
            }
            sb.Append("|packets=").Append(LootPickService.Packets(s).Count);
            return sb.ToString();
        }

        // ── D 数据 ─────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            EventDirectorDef d = EventDirectorService.Def(DecoyTerminalService.EventId);
            Expect(d != null && d.Threat && !d.Major && d.Trigger == EventDirectorService.TriggerSite && d.Reward == EventDirectorService.RewardPacket && d.DurationSeconds > 0,
                "D1 fg.TbWorldEvent decoy_terminal：威胁、非重大、地图点位由玩家交互触发（site）、打退伏击给三选一（packet）（FGR-EVT-040 / 041、DEBT-FG12EVT01-02）");
            string[] tun = { "chance", "scan_radius", "disarm_seconds", "disarm_tech", "ambush_units", "mine_units", "ambush_ring", "mine_radius", "mine_damage" };
            string[] missingT = tun.Where(k => !GridContent.TryGetTuning("event.decoy_terminal." + k, out _)).ToArray();
            string[] keys = { "decoy_terminal.tell.scorch", "decoy_terminal.tell.mines", "decoy_terminal.hover.tell", "decoy_terminal.hover.identified", "decoy_terminal.hover.sprung",
                "decoy_terminal.hover.disarmed", "decoy_terminal.notify.identified", "decoy_terminal.notify.sprung", "decoy_terminal.notify.sprung_mines", "decoy_terminal.notify.ambush",
                "decoy_terminal.notify.mines", "decoy_terminal.notify.withdrew", "decoy_terminal.notify.disarmed", "decoy_terminal.order.machine", "decoy_terminal.work.label",
                "decoy_terminal.block.no_site", "ui.map.decoy_terminal.identified", "ui.map.decoy_terminal.sprung", "ui.map.decoy_terminal.disarmed", "codex.event_director.decoy_terminal.body" };
            string[] missingK = keys.Where(k => !GameText.Has(k)).ToArray();
            string src = File.ReadAllText("Assets/GameScripts/HotFix/GameLogic/Stage/GameRoot.cs");
            Expect(missingT.Length == 0 && missingK.Length == 0 && DecoyTerminalService.DisarmTech < 12 && src.Contains("Register(new Campaign.Env.DecoyTerminalService.Handler())")
                   && GuidanceHooks.Known.Contains("event_director.first.decoy_terminal"),
                $"D2 调参齐（缺 {string.Join("、", missingT)}）、文本键齐（缺 {string.Join("、", missingK)}）、拆除给“少量”技术数据（{DecoyTerminalService.DisarmTech} < 普通终端最少 12）、GameRoot 登记处理器、第一次说明钩子");
        }

        // ── A 点位与破绽（B25） ──────────────────────────────────────────────────

        private static void CheckSeeds()
        {
            int[] seeds = { 11, 4242, 90001, 777777, 31337 };
            var lines = new List<string>();
            bool every = true, faction = true, stable = true, both = true;
            int decoys = 0, plain = 0, mines = 0;
            foreach (int seed in seeds)
            {
                CampaignState s = World(seed);
                List<WorldFeature> d = Decoys(s);
                List<WorldFeature> p = Decoys(s, false);
                decoys += d.Count;
                plain += p.Count;
                mines += d.Count(f => DecoyTerminalService.VariantOf(s, f) == DecoyTerminalService.VariantMines);
                every &= d.Count > 0;
                both &= p.Count > 0;
                faction &= d.All(f => !string.IsNullOrEmpty(f.TerritoryId) && WorldGenService.ContextFor(s).Plan.Find(f.TerritoryId)?.IsFaction == true);
                stable &= d.All(f => DecoyTerminalService.IsDecoy(s, f.Id) && DecoyTerminalService.IsDecoy(s, f) && DecoyTerminalService.VariantOf(s, f) == DecoyTerminalService.VariantOf(s, f));
                lines.Add($"{seed}:{d.Count}/{d.Count + p.Count}");
            }
            // 同一种子重新生成世界：诱饵集合逐个相同（纯函数，不进存档）。
            CampaignState a = World(seeds[0]);
            string ida = string.Join(",", Decoys(a).Select(f => f.Id));
            CampaignState b = World(seeds[0]);
            string idb = string.Join(",", Decoys(b).Select(f => f.Id));
            Expect(every && both && faction && stable && ida == idb && mines > 0 && mines < decoys,
                $"A1 FGR-EVT-040 / B25：{seeds.Length} 个种子每个世界都有诱饵终端、也有普通终端（诱饵 / 领地终端 {string.Join("，", lines)}），诱饵都在阵营领地里，" +
                $"按（种子, 分布格）确定（同种子重开相同 {ida == idb}），两种破绽都出现（雷区 {mines} / 共 {decoys}）");
        }

        private static void CheckTell()
        {
            CampaignState s = World(4242);
            WorldFeature d = FirstDecoy(s);
            WorldFeature p = Decoys(s, false).First();
            HomeGridService.RevealArea(s, new Vector2(d.X, d.Y), 6f);
            HomeGridService.RevealArea(s, new Vector2(p.X, p.Y), 6f);
            HomeExplorationService.TryDescribeRelic(s, d.Id, out string dt, out string db);
            HomeExplorationService.TryDescribeRelic(s, p.Id, out string pt, out string pb);
            string tell = GameText.Get(DecoyTerminalService.VariantOf(s, d) == DecoyTerminalService.VariantMines ? "decoy_terminal.tell.mines" : "decoy_terminal.tell.scorch");
            bool sameName = dt != null && pt != null && dt.StartsWith(GameText.Get("world.poi.terminal")) && pt.StartsWith(GameText.Get("world.poi.terminal"));
            bool tells = db != null && db.Contains(tell) && db.Contains("可读取") && pb != null && !pb.Contains("细看");
            HomeExplorationService.MapLabel(s, d, out _);
            bool mapSame = HomeExplorationService.MapLabel(s, d, out _) == HomeExplorationService.MapLabel(s, p, out _);
            Expect(sameName && tells && mapSame,
                $"A2 外观破绽：诱饵终端看起来是普通旧终端（标题“{dt}”、地图标签相同 {mapSame}），悬停多一行破绽“{tell}”（{tells}），普通终端没有");
        }

        // ── B 两条路径（FGT-EVT-006） ───────────────────────────────────────────

        private static void CheckAmbushPath()
        {
            CampaignState s = World(4242);
            WorldFeature d = FirstDecoy(s, DecoyTerminalService.VariantAmbush);
            if (d == null)
            {
                Fail("B1 测试准备：种子 4242 没有伏击变体的诱饵终端");
                return;
            }
            int w = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(3f, -3f));
            bool noScan = !DecoyTerminalService.CanScan(s, w);
            int packets0 = LootPickService.Packets(s).Count;
            WorkOrderRecord o = OrderAndArrive(s, d, w, out bool ordered);
            bool sprung = DecoyTerminalService.IsSprung(s, d.Id) && o?.State == WorkOrderState.Cancelled && HomeExplorationService.IsRecovered(s, d.Id)
                          && !DecoyTerminalService.IsIdentified(s, d.Id) && Ex(s).TerminalTechData == 0;
            bool started = StepUntil(() => Inst(s)?.State == EventDirectorService.StateActive, 20);
            EventDirectorInstanceRecord r = Inst(s);
            DecoyAmbushRecord amb = r != null ? DecoyTerminalService.Ambush(s, r.Serial) : null;
            int alive = r != null ? Alive(s, r.Serial) : -1;
            bool atTerminal = r != null && Mathf.Approximately(r.X, d.X) && Mathf.Approximately(r.Y, d.Y);
            bool owned = amb != null && SiegeService.GroupOwnerOf(s, amb.Key) == SiegeService.OwnerEvent;
            StepSec(3);
            bool stays = r != null && Alive(s, r.Serial) == alive && Inst(s) != null; // 攻城服务对账不把伏击组当无主收走
            KillAmbush(new Vector2(d.X, d.Y));
            bool ended = StepUntil(() => Inst(s) == null, 10);
            EventDirectorHistoryRecord h = Hist(s).LastOrDefault();
            bool packet = h != null && h.Outcome == EventDirectorService.OutcomeSuccess && !string.IsNullOrEmpty(h.PacketId) && LootPickService.Find(s, h.PacketId) != null
                          && LootPickService.Packets(s).Count == packets0 + 1;
            Expect(ordered && noScan && sprung && started && atTerminal && alive == DecoyTerminalService.AmbushUnits && owned && stays && ended && packet
                   && Ex(s).DecoyAmbushes.Length == 0 && DecoyTerminalService.Repelled == 1,
                $"B1 FGT-EVT-006 不扫描被伏击：没有标记器的工作机（{noScan}）右键读取（{ordered}）→ 到场即触发（工单作废、终端作废、不给技术数据 {sprung}）→ 导演开始（{started}，在终端处 {atTerminal}）" +
                $"→ {alive} 台伏击单位（期望 {DecoyTerminalService.AmbushUnits}，攻城组归属“事件” {owned}，3 秒后仍在 {stays}）→ 全部击毁 → 以完成结束（{ended}）并给三选一数据包（{packet}，“{h?.Outcome}”）");
        }

        private static void CheckScanAndDisarm()
        {
            CampaignState s = World(4242);
            WorldFeature d = FirstDecoy(s);
            HomeGridService.RevealArea(s, new Vector2(d.X, d.Y), 6f);
            Vector2 at = new Vector2(d.X, d.Y);
            int m = Spawn(s, HomeValleyLayout.Erc003ChassisId, BpMarkCombat, at + new Vector2(DecoyTerminalService.ScanRadius - 3f, 0f));
            bool canScan = DecoyTerminalService.CanScan(s, m);
            bool identified = StepUntil(() => DecoyTerminalService.IsIdentified(s, d.Id), 4);
            HomeExplorationService.TryDescribeRelic(s, d.Id, out string title, out string body);
            bool hover = title != null && title.StartsWith(GameText.Get("event_director.name.decoy_terminal")) && body.Contains("安全拆除");
            string label = HomeExplorationService.MapLabel(s, d, out _);
            HomeExplorationService.TryGetRelic(s, d.Id, out HomeExplorationService.RelicInfo info);
            bool disarmTerms = Mathf.Approximately(info.Seconds, DecoyTerminalService.DisarmSeconds) && info.TechData == DecoyTerminalService.DisarmTech;
            // 安全拆除：工作机右键（同一入口）→ 到场照常施工（不触发）→ 完工给少量技术数据。
            int w = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(3f, -3f));
            WorkOrderRecord o = OrderAndArrive(s, d, w, out bool ordered);
            bool working = o != null && o.State == WorkOrderState.InProgress && HomeValleyAlarms.WorkKindName(o) == GameText.Get("decoy_terminal.work.label");
            long tech0 = Ex(s).DecoyTechData;
            int paid = HomeExplorationService.CompleteRecovery(s, o);
            int again = HomeExplorationService.CompleteRecovery(s, o);
            StepSec(15);
            bool noAmbush = Inst(s) == null && Hist(s).Count == 0 && !DecoyTerminalService.IsSprung(s, d.Id) && Ex(s).DecoyQueue.Length == 0;
            bool done = DecoyTerminalService.IsDisarmed(s, d.Id) && HomeExplorationService.IsRecovered(s, d.Id) && paid == DecoyTerminalService.DisarmTech && again == 0
                        && Ex(s).DecoyTechData == tech0 + DecoyTerminalService.DisarmTech && LootPickService.Packets(s).Count == 0;
            bool reorder = !HomeExplorationService.OrderRecovery(s, d.Id, w).Success;
            Expect(canScan && identified && hover && label == GameText.Format("ui.map.decoy_terminal.identified", GameText.Get("event_director.name.decoy_terminal")) && disarmTerms
                   && ordered && working && done && noAmbush && reorder,
                $"B2 FGT-EVT-006 扫描识别：带标记器的作战机（按端口声明 {canScan}）走到 {DecoyTerminalService.ScanRadius - 3f} 格内即识别（{identified}），悬停 / 地图写明可安全拆除（{hover} / “{label}”），" +
                $"拆除工期 {info.Seconds} 秒、技术数据 {info.TechData}；工作机右键 = 拆除（施工中 {working}）→ 完工技术数据 +{paid}（重复完工 {again}），不触发伏击、不给三选一（{noAmbush}），之后不能再派单（{reorder}）");
        }

        private static void CheckWorkerScanOnArrival()
        {
            CampaignState s = World(4242);
            WorldFeature d = FirstDecoy(s);
            int w = Spawn(s, HomeValleyLayout.Erc001ChassisId, BpMarkWorker, Core(s) + new Vector2(3f, -3f));
            WorkOrderRecord o = OrderAndArrive(s, d, w, out bool ordered);
            StepSec(12);
            Expect(ordered && DecoyTerminalService.CanScan(s, w) && DecoyTerminalService.IsIdentified(s, d.Id) && !DecoyTerminalService.IsSprung(s, d.Id) && o?.State == WorkOrderState.Cancelled
                   && Inst(s) == null && Hist(s).Count == 0 && !HomeExplorationService.IsRecovered(s, d.Id),
                "B3 带标记器的工作机直接被派去读取：到场先扫描 → 识别、工单作废、不触发伏击、终端还在（可再派单安全拆除）");
        }

        private static void CheckMines()
        {
            CampaignState s = World(4242);
            WorldFeature d = FirstDecoy(s, DecoyTerminalService.VariantMines);
            if (d == null)
            {
                foreach (int seed in new[] { 11, 90001, 777777, 31337 })
                {
                    s = World(seed);
                    d = FirstDecoy(s, DecoyTerminalService.VariantMines);
                    if (d != null)
                    {
                        break;
                    }
                }
            }
            if (d == null)
            {
                Fail("B4 测试准备：5 个种子都没有雷区变体");
                return;
            }
            Vector2 at = new Vector2(d.X, d.Y);
            int near = Spawn(s, HomeValleyLayout.Erc003ChassisId, BpPlainCombat, at + new Vector2(1.5f, 0f));
            int w = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(3f, -3f));
            StepSec(1);
            float hp0 = MachineRegistry.TryGetRecord(near, out MachineRecord m0) ? m0.Health : -1f;
            OrderAndArrive(s, d, w, out _);
            bool started = StepUntil(() => Inst(s)?.State == EventDirectorService.StateActive, 20);
            StepSec(1);
            EventDirectorInstanceRecord r = Inst(s);
            int alive = r != null ? Alive(s, r.Serial) : -1;
            float hp1 = MachineRegistry.TryGetRecord(near, out MachineRecord m1) ? m1.Health : -1f;
            KillAmbush(at);
            bool ended = StepUntil(() => Inst(s) == null, 10);
            EventDirectorHistoryRecord h = Hist(s).LastOrDefault();
            Expect(started && DecoyTerminalService.MineHits > 0 && hp1 < hp0 - 1f && alive == DecoyTerminalService.MineUnits && ended && h?.Outcome == EventDirectorService.OutcomeSuccess
                   && !string.IsNullOrEmpty(h.PacketId),
                $"B4 雷区变体（破绽是半埋的地雷）：触发后地雷炸响（命中 {DecoyTerminalService.MineHits}，旁边机器 {hp0:0}→{hp1:0}），再围上 {alive} 台（期望 {DecoyTerminalService.MineUnits}），打退后同样给三选一（{h?.Outcome}）");
        }

        /// <summary>雷区变体 mine_units = 0：只炸地雷、没有要打退的伏击 → 以失败结束，不给三选一，终端作废、不能再派单。</summary>
        private static void CheckMinesNoUnits()
        {
            DecoyTerminalService.TuningOverrideForTests = id => id == "event.decoy_terminal.mine_units" ? 0f : (float?)null;
            try
            {
                CampaignState s = null;
                WorldFeature d = null;
                foreach (int seed in new[] { 4242, 11, 90001, 777777, 31337 })
                {
                    s = World(seed);
                    d = FirstDecoy(s, DecoyTerminalService.VariantMines);
                    if (d != null)
                    {
                        break;
                    }
                }
                if (d == null)
                {
                    Fail("B5 测试准备：5 个种子都没有雷区变体");
                    return;
                }
                int w = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(3f, -3f));
                OrderAndArrive(s, d, w, out bool ordered);
                bool ended = StepUntil(() => Hist(s).Count > 0 && Inst(s) == null, 20);
                EventDirectorHistoryRecord h = Hist(s).LastOrDefault();
                bool voided = DecoyTerminalService.IsSprung(s, d.Id) && HomeExplorationService.IsRecovered(s, d.Id) && !HomeExplorationService.OrderRecovery(s, d.Id, w).Success;
                Expect(DecoyTerminalService.MineUnits == 0 && ordered && ended && h?.Outcome == EventDirectorService.OutcomeFailed && string.IsNullOrEmpty(h.PacketId)
                       && LootPickService.Packets(s).Count == 0 && Ex(s).DecoyAmbushes.Length == 0 && voided,
                    $"B5 负向 雷区变体 mine_units = 0：地雷炸响后没有伏击单位 → 结局 {h?.Outcome}（期望 failed）、不给三选一、伏击记录清空、终端作废不能再派单（{voided}）");
            }
            finally
            {
                DecoyTerminalService.TuningOverrideForTests = null;
            }
        }

        /// <summary>赶路途中被别的标记器识别：读取单到场改按拆除工期施工，完工按拆除结算、不触发伏击。</summary>
        private static void CheckIdentifiedEnRoute()
        {
            CampaignState s = World(4242);
            WorldFeature d = FirstDecoy(s);
            HomeGridService.RevealArea(s, new Vector2(d.X, d.Y), 6f);
            int w = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(3f, -3f));
            bool ordered = HomeExplorationService.OrderRecovery(s, d.Id, w).Success;
            WorkOrderRecord o = s.WorkOrders?.LastOrDefault(x => x.TargetId == d.Id);
            float readDuration = o?.Duration ?? -1f;
            bool scanned = DecoyTerminalService.OnScanned(s, d.Id, 0); // 另一台标记器在途中扫到
            if (o != null)
            {
                HomeValleyWorkOrders.OnArrivedAtWork(s, o.WorkOrderId);
            }
            bool disarmDuration = o != null && o.State == WorkOrderState.InProgress && Mathf.Approximately(o.Duration, DecoyTerminalService.DisarmSeconds);
            int paid = o != null ? HomeExplorationService.CompleteRecovery(s, o) : -1;
            Expect(ordered && scanned && disarmDuration && paid == DecoyTerminalService.DisarmTech && DecoyTerminalService.IsDisarmed(s, d.Id) && !DecoyTerminalService.IsSprung(s, d.Id),
                $"B6 赶路途中被识别：读取单（工期 {readDuration} 秒）到场改按拆除工期 {o?.Duration} 秒施工（{disarmDuration}），完工按拆除给 {paid}、不触发伏击");
        }

        // ── N 负向 ─────────────────────────────────────────────────────────────

        /// <summary>旧档升级：本卡之前已读过的阵营终端被抽成诱饵 → 照普通已读终端处理（悬停“已读取”、不显示破绽、标记器不识别、不写识别状态）。</summary>
        private static void CheckLegacyRead()
        {
            CampaignState s = World(4242);
            WorldFeature d = FirstDecoy(s);
            HomeGridService.RevealArea(s, new Vector2(d.X, d.Y), 6f);
            HomeExplorationState st = Ex(s);
            st.PoisDone = (st.PoisDone ?? Array.Empty<string>()).Concat(new[] { d.Id }).ToArray(); // 模拟 M8～M11 旧档：早就读过
            HomeExplorationService.Touch();
            int m = Spawn(s, HomeValleyLayout.Erc003ChassisId, BpMarkCombat, new Vector2(d.X, d.Y) + new Vector2(2f, 0f));
            StepSec(3);
            HomeExplorationService.TryDescribeRelic(s, d.Id, out string title, out string body);
            string tell = DecoyTerminalService.TellText(s, d);
            bool hover = body != null && !body.Contains(tell) && !body.Contains(GameText.Get("cache.hover.hint")) && DecoyTerminalService.HoverLine(s, d.Id, "x") == null
                         && title != null && !title.StartsWith(GameText.Get("event_director.name.decoy_terminal"));
            bool noScan = DecoyTerminalService.CanScan(s, m) && !DecoyTerminalService.IsIdentified(s, d.Id) && (st.DecoysIdentified ?? Array.Empty<string>()).Length == 0
                          && !DecoyTerminalService.OnScanned(s, d.Id, m);
            bool label = DecoyTerminalService.MapLabel(s, d.Id) == null && DecoyTerminalService.IsStaleRead(s, d.Id);
            Expect(hover && noScan && label && Inst(s) == null && Hist(s).Count == 0,
                $"N6 负向 旧档里已读过、现在被抽成诱饵的终端：悬停照普通已读终端（{hover}），标记器在旁 3 秒不识别、不写识别状态（{noScan}），地图标签不变（{label}），不触发伏击");
        }

        private static void CheckRepeat()
        {
            CampaignState s = World(4242);
            WorldFeature d = FirstDecoy(s, DecoyTerminalService.VariantAmbush);
            int w = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(3f, -3f));
            int w2 = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(-3f, -3f));
            OrderAndArrive(s, d, w, out _);
            StepUntil(() => Inst(s)?.State == EventDirectorService.StateActive, 20);
            int serial = Inst(s)?.Serial ?? -1;
            // 再次右键 / 另一台机器 / 直接再到场一次 / 再完工一次 / 再扫描一次：都不再触发。
            bool reorder = !HomeExplorationService.OrderRecovery(s, d.Id, w2).Success;
            var fake = new WorkOrderRecord { WorkOrderId = "decoy-fake", Kind = WorkOrderKind.Salvage, TargetId = d.Id, AssignedMachineLogicId = w2, State = WorkOrderState.Reserved };
            bool arrive = DecoyTerminalService.OnArrive(s, fake);
            int paid = HomeExplorationService.CompleteRecovery(s, fake);
            bool scan = !DecoyTerminalService.OnScanned(s, d.Id, w2);
            StepSec(12);
            bool single = DecoyTerminalService.Sprung == 1 && Ex(s).DecoysSprung.Count(x => x == d.Id) == 1 && Ex(s).DecoyQueue.Length == 0
                          && EventDirectorService.Schedule(s, DecoyTerminalService.EventId).Occurrences == 1 && !EventDirectorService.Schedule(s, DecoyTerminalService.EventId).Pending
                          && EventDirectorService.Active(s).Count(x => x.EventId == DecoyTerminalService.EventId) == 1 && Inst(s)?.Serial == serial;
            KillAmbush(new Vector2(d.X, d.Y));
            StepUntil(() => Inst(s) == null, 10);
            StepSec(15);
            bool oneReward = Hist(s).Count == 1 && LootPickService.Packets(s).Count == 1 && Ex(s).TerminalTechData == 0;
            Expect(reorder && arrive && paid == 0 && scan && single && oneReward,
                $"N1 负向“同一个诱饵终端被重复触发”：再派单被拒（{reorder}）、再到场只作废工单（{arrive}）、再完工 0、再扫描不识别（{scan}）；只有一场伏击（{single}）、打退后只给一个数据包、没有读取奖励（{oneReward}）");
        }

        private static void CheckExpiry()
        {
            CampaignState s = World(4242);
            WorldFeature d = FirstDecoy(s, DecoyTerminalService.VariantAmbush);
            int w = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(3f, -3f));
            OrderAndArrive(s, d, w, out _);
            StepUntil(() => Inst(s)?.State == EventDirectorService.StateActive, 20);
            EventDirectorInstanceRecord r = Inst(s);
            int key = r != null ? DecoyTerminalService.Ambush(s, r.Serial)?.Key ?? 0 : 0;
            bool ended = StepUntil(() => Inst(s) == null, EventDirectorService.Def(DecoyTerminalService.EventId).DurationSeconds + 5);
            EventDirectorHistoryRecord h = Hist(s).LastOrDefault();
            Expect(ended && h?.Outcome == EventDirectorService.OutcomeExpired && string.IsNullOrEmpty(h.PacketId) && LootPickService.Packets(s).Count == 0 && Site.CountSiegeGroup(key) == 0
                   && Ex(s).DecoyAmbushes.Length == 0 && SiegeService.GroupOwnerOf(s, key) == null,
                $"N2 负向“没打退伏击”：时限到以“超时”结束（{h?.Outcome}），剩下的伏击单位撤走、没有三选一、记录清空");
        }

        private static void CheckQueue()
        {
            CampaignState s = null;
            List<WorldFeature> two = null;
            foreach (int seed in new[] { 4242, 11, 90001, 777777, 31337 })
            {
                s = World(seed);
                two = Decoys(s);
                if (two.Count >= 2)
                {
                    break;
                }
            }
            if (two == null || two.Count < 2)
            {
                Fail("N3 测试准备：没有一个种子有两个诱饵终端");
                return;
            }
            int w1 = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(3f, -3f));
            int w2 = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(-3f, -3f));
            OrderAndArrive(s, two[0], w1, out _);
            OrderAndArrive(s, two[1], w2, out _); // 同一步触发两个：导演的待处理请求只有一个地点，处理器自己排队
            StepUntil(() => Inst(s)?.State == EventDirectorService.StateActive, 20);
            EventDirectorInstanceRecord r1 = Inst(s);
            bool first = r1 != null && Mathf.Approximately(r1.X, two[0].X) && Ex(s).DecoyQueue.Length == 1 && Ex(s).DecoyQueue[0] == two[1].Id;
            StepSec(15);
            bool oneAtATime = EventDirectorService.Active(s).Count(x => x.EventId == DecoyTerminalService.EventId) == 1;
            KillAmbush(new Vector2(two[0].X, two[0].Y));
            bool second = StepUntil(() => Inst(s) != null && Inst(s).Serial != r1.Serial && Inst(s).State == EventDirectorService.StateActive, 30);
            EventDirectorInstanceRecord r2 = Inst(s);
            bool atSecond = r2 != null && Mathf.Approximately(r2.X, two[1].X) && Mathf.Approximately(r2.Y, two[1].Y) && Ex(s).DecoyQueue.Length == 0;
            Expect(first && oneAtATime && second && atSecond,
                $"N3 负向“两个诱饵终端几乎同时触发”：第一场在第一个终端（{first}），同一时刻只有一场（{oneAtATime}），第二个终端排队、第一场结束后在第二个终端开始（{second} / {atSecond}）——不丢、不串位置");
        }

        private static void CheckFrequencyOff()
        {
            CampaignState s = World(4242);
            EventDirectorService.SetFrequency(s, EventDirectorService.FrequencyOff);
            WorldFeature d = FirstDecoy(s);
            int w = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(3f, -3f));
            OrderAndArrive(s, d, w, out _);
            bool started = StepUntil(() => Inst(s)?.State == EventDirectorService.StateActive, 20);
            Expect(started, "N4 设置“事件频率：关闭”：诱饵终端是玩家交互触发的地图点位事件，照样伏击（FGR-EVT-006 只关导演随机安排的）");
        }

        private static void CheckNoScan()
        {
            CampaignState s = World(4242);
            List<WorldFeature> ds = Decoys(s);
            WorldFeature d = ds[0];
            HomeGridService.RevealArea(s, new Vector2(d.X, d.Y), 6f);
            Vector2 at = new Vector2(d.X, d.Y);
            int plain = Spawn(s, HomeValleyLayout.Erc003ChassisId, BpPlainCombat, at + new Vector2(2f, 0f));
            int far = Spawn(s, HomeValleyLayout.Erc003ChassisId, BpMarkCombat, at + new Vector2(DecoyTerminalService.ScanRadius + 6f, 0f));
            StepSec(4);
            bool none = !DecoyTerminalService.IsIdentified(s, d.Id) && !DecoyTerminalService.CanScan(s, plain) && DecoyTerminalService.CanScan(s, far);
            // 迷雾里（没发现）的诱饵：带标记器的机器在旁边也不识别（不泄露情报）。
            WorldFeature hidden = ds.Skip(1).FirstOrDefault(f => !HomeExplorationService.IsFound(s, f.Id));
            bool fog = true, thenScan = true;
            int before = DecoyTerminalService.Identified;
            if (hidden != null)
            {
                Spawn(s, HomeValleyLayout.Erc003ChassisId, BpMarkCombat, new Vector2(hidden.X + 2f, hidden.Y));
                long tick = GameClock.Ticks - GameClock.Ticks % Math.Max(1, GameClock.StepHz) + GameClock.StepHz;
                DecoyTerminalService.WorldStep(s, tick); // 机器刚到、还没揭开迷雾的这一秒：没发现 = 不是候选
                fog = !HomeExplorationService.IsFound(s, hidden.Id) && !DecoyTerminalService.IsIdentified(s, hidden.Id);
                StepSec(3); // 机器的视野揭开那一片 → 发现 → 下一秒扫描识别
                thenScan = HomeExplorationService.IsFound(s, hidden.Id) && DecoyTerminalService.IsIdentified(s, hidden.Id);
            }
            Expect(none && fog && thenScan && before == 0,
                $"N5 负向：没有标记器的机器站在旁边不识别、带标记器但在 {DecoyTerminalService.ScanRadius} 格外不识别（{none}），迷雾里没发现的不识别（{fog}），机器揭开迷雾发现后才识别（{thenScan}）");
        }

        // ── T 计时 / 观察 ──────────────────────────────────────────────────────

        private static void FrameTo(long target, ref int frames)
        {
            int guard = 0;
            while (GameClock.Ticks < target && guard++ < 60 * 1200)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
        }

        private static string RunScenario(int seed, bool observe, float speed, out bool pausedHeld, out int frames)
        {
            CampaignState s = World(seed, observe);
            frames = 0;
            WorldFeature d = FirstDecoy(s, DecoyTerminalService.VariantAmbush);
            int w = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(3f, -3f));
            OrderAndArrive(s, d, w, out _);
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
            FrameTo(t0 + Sec(200), ref frames); // 触发 → 伏击 180 秒 → 超时撤走
            GameClock.SetSpeed(1f);
            return Snap(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true, paused = true, proportional = true;
            var shown = new List<string>();
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(4242, false, speed, out bool held, out int frames);
                paused &= held;
                reference ??= snap;
                same &= snap == reference;
                proportional &= Math.Abs(frames * speed - Sec(200)) <= 4f;
                shown.Add($"{speed}x {frames} 帧");
            }
            Expect(paused && same && proportional && reference.Contains("\"Outcome\":\"expired\""),
                $"T1 暂停与 0.5x～3x：暂停中 120 帧状态不动；{string.Join(" / ", shown)} 跑同样的触发 → 伏击 → 超时撤走，导演与诱饵状态逐字段一致、真实帧与倍速成反比（暂停 {paused} / 一致 {same} / 成反比 {proportional}）");
        }

        private static void CheckObserved()
        {
            string seen = RunScenario(4242, true, 1f, out _, out _);
            string unseen = RunScenario(4242, false, 1f, out _, out _);
            Expect(seen == unseen && seen.Contains("\"Outcome\":\"expired\""), "T2 家园观察与不观察时，同一段（触发、伏击、超时撤走）结果逐字段一致（FGR-BASE-021）");
        }

        // ── S 存读档 / R 重建 ───────────────────────────────────────────────────

        private static CampaignState SaveAndLoad(out bool ok)
        {
            WorldSimulation.SyncAllForSave();
            int slot = CampaignSession.ActiveSlotIndex;
            SaveResult save = CampaignAutoSaveService.SaveWithExport(slot, SaveReason.Manual);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(slot);
            ok = save.Success && rr.Success;
            if (!rr.Success)
            {
                Fail("读档失败：" + rr.Message);
                return null;
            }
            CampaignSession.Set(slot, rr.State);
            WorldSimulation.LoadHome(resume: true);
            return rr.State;
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = World(4242, true);
            List<WorldFeature> ds = Decoys(s);
            WorldFeature d = ds.First(f => DecoyTerminalService.VariantOf(s, f) == DecoyTerminalService.VariantAmbush);
            WorldFeature other = ds.FirstOrDefault(f => f.Id != d.Id);
            if (other != null)
            {
                DecoyTerminalService.OnScanned(s, other.Id, 0); // 另一个诱饵：已识别（存档里留着）
            }
            int w = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(3f, -3f));
            OrderAndArrive(s, d, w, out _);
            StepUntil(() => Inst(s)?.State == EventDirectorService.StateActive, 20);
            StepSec(20);
            string before = Snap(s);
            CampaignState l = SaveAndLoad(out bool ok);
            if (l == null)
            {
                return;
            }
            string loaded = Snap(l);
            bool idKept = other == null || DecoyTerminalService.IsIdentified(l, other.Id);
            bool stillSprung = !HomeExplorationService.OrderRecovery(l, d.Id, 0).Success;
            KillAmbush(new Vector2(d.X, d.Y));
            bool ended = StepUntil(() => Inst(l) == null, 10);
            EventDirectorHistoryRecord h = Hist(l).LastOrDefault();
            Expect(ok && loaded == before && idKept && stillSprung && ended && h?.Outcome == EventDirectorService.OutcomeSuccess && !string.IsNullOrEmpty(h.PacketId),
                $"S1 伏击进行中真文件存读档：导演实例、伏击记录与内核里的伏击单位逐字段一致（{loaded == before}），已识别 / 已触发状态保留（{idKept} / 不能再派单 {stillSprung}），读档后打退照常给三选一（{h?.Outcome}）" +
                (loaded == before ? string.Empty : $"；读回前 {before.Substring(Math.Max(0, before.Length - 160))} ⇔ 读回后 {loaded.Substring(Math.Max(0, loaded.Length - 160))}"));
        }

        private static void CheckRebuild()
        {
            CampaignState s = World(4242);
            WorldFeature d = FirstDecoy(s, DecoyTerminalService.VariantAmbush);
            int w = Spawn(s, HomeValleyLayout.Erc001ChassisId, null, Core(s) + new Vector2(3f, -3f));
            OrderAndArrive(s, d, w, out _);
            StepUntil(() => Inst(s)?.State == EventDirectorService.StateActive, 20);
            EventDirectorInstanceRecord r = Inst(s);
            DecoyAmbushRecord a = r != null ? DecoyTerminalService.Ambush(s, r.Serial) : null;
            if (a == null)
            {
                Fail("R1 测试准备：伏击没开始");
                return;
            }
            Site.DespawnSiegeGroup(a.Key); // 模拟“内核没有快照、按记录重建”：伏击组不在了（不是被击毁）
            int restored = DecoyTerminalService.RestoreAfterLoad(s, Site);
            StepSec(2);
            bool stillOn = Inst(s) != null && Alive(s, r.Serial) == a.Alive && a.Alive == DecoyTerminalService.AmbushUnits;
            int again = DecoyTerminalService.RestoreAfterLoad(s, Site); // 组还在：不重复补
            Expect(restored == DecoyTerminalService.AmbushUnits && stillOn && again == 0,
                $"R1 负向“内核重建后伏击单位不见了”：读档对账按剩余台数补回 {restored} 台、伏击没有被白白判为打退（{stillOn}），组还在时不重复补（{again}）");
        }

        // ── P 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = World(4242);
            List<WorldFeature> ds = Decoys(s);
            foreach (WorldFeature f in ds)
            {
                HomeGridService.RevealArea(s, new Vector2(f.X, f.Y), 6f);
            }
            for (int i = 0; i < 6; i++)
            {
                Spawn(s, HomeValleyLayout.Erc003ChassisId, BpMarkCombat, Core(s) + new Vector2(4f + i, 6f)); // 带标记器、离诱饵都远
            }
            StepSec(1);
            long tick = GameClock.Ticks - GameClock.Ticks % Math.Max(1, GameClock.StepHz) + GameClock.StepHz;
            const int n = 2000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                DecoyTerminalService.WorldStep(s, tick);
            }
            double scanUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                DecoyTerminalService.WorldStep(s, tick + 1); // 非整秒的步：直接返回
            }
            double idleUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            sw.Restart();
            for (int i = 0; i < n; i++)
            {
                DecoyTerminalService.IsDecoy(s, ds[i % ds.Count].Id);
            }
            double isUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            PerfLines.Add($"扫描 WorldStep（每游戏秒一次，{ds.Count} 个已发现诱饵 × 6 台带标记器的机器）{scanUs:0.##} µs；其余步 {idleUs:0.###} µs；IsDecoy（悬停 / 地图 / 工单到场）{isUs:0.##} µs");
            Expect(idleUs < 1.0 && scanUs < 200.0 && isUs < 50.0, $"P1 扫描每游戏秒 {scanUs:0.##} µs（线 200）、其余步 {idleUs:0.###} µs（线 1）、诱饵判定 {isUs:0.##} µs（线 50），与单位总数无关");
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
