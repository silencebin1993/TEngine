using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Nav;
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
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG6-LOG-10 管线破损泄漏的自动验收（FG03 FGR-LOG-046；卡片验收“行为探针：燃油泄漏遇到燃烧产生燃烧区”；必须同时交付“图鉴和提示写明有意设计、修好管线后液洼逐渐消失”；
    /// 负向“泄漏引发连锁反应烧到自己的产线（玩法，但要有告警）”）。全部起真实系统：真实家园（世界模拟、管线内核、战斗内核与反应规则、建筑耐久）、真文件存读档。
    /// A 数据（新表与源数据逐字段、调参、文本中英、钩子、图鉴、通知类型与离家报告分段）；K 战斗内核（独立内核：液洼挂标签、遇火整片反应、两摊液洼不互相反应、快照格式 12 / 11 / 坏值）；
    /// B 击穿（阈值、空管线不漏、扩散、通知 / 钩子 / 图鉴 / 悬停）；C 验收探针与连锁（燃油遇燃烧单位 / 燃烧场地 → 燃烧区；烧到敌人和己方机器；烧坏管线 / 传送带 / 建筑；被烧穿的管线再漏、再被点燃；告警）；
    /// N 负向（水 / 冷却液不整片反应、酸液挂腐蚀、修好 / 冲洗 / 拆虚影后消退、虚影在一直漏、消退中又被打穿、上限、烧完破口还在重新积起）；
    /// F 正式旅程（突袭溅射打穿燃油管线）；G 存读档；H 暂停与倍速；I 观察一致；J 种子集；P 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgPipeLeakSelfCheck）；单段入口 <see cref="RunFromMenu"/>。
    /// </summary>
    public static class FgPipeLeakSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 5;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 管线破损泄漏")]
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
            Line("\n[管线破损泄漏] 击穿积起带标签的液洼、与战斗标签反应互动（燃油遇燃烧 = 燃烧区）、连锁烧毁与告警、修好后消退（FG6-LOG-10）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgleak-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                IntelCatalog.Reload();
                RaidCatalog.Reload();
                SiegeCatalog.Reload();
                TurretCatalog.Reload();
                DefenseCatalog.Reload();
                PipeLeakService.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；液洼挂标签 / 遇火整片反应在 AOT 战斗内核（Burst），击穿 / 扩散 / 消退 / 火烧设施在热更层（Editor 下 Mono JIT，真机另测 FG15-SYS-02）");
                Step(CheckData);
                Step(CheckKernel);
                Step(CheckKernelSnapshot);
                Step(CheckBreach);
                Step(CheckProbeAndChain);
                Step(CheckTrapIgnition);
                Step(CheckNonFlammable);
                Step(CheckRepairFades);
                Step(CheckFlushAndGhost);
                Step(CheckRebuildFades);
                Step(CheckCapAndReform);
                Step(CheckFormalJourney);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSeedSet);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"管线泄漏自检抛异常：{e}");
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
                PipeLeakService.ResetSessionState();
                SiegeService.ResetSessionState();
                DefenseService.ResetSessionState();
                TurretService.ResetSessionState();
                RepairDroneService.ResetSessionState();
                IntelService.ResetSessionState();
                RaidDirectorService.ResetSessionState();
                ResearchService.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
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
            Line($"  · [管线破损泄漏] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        private static uint Bit(string tag) => NamedReactionCatalog.BitOf(tag);

        private static int Fluid(string key) => PipeNetworkService.FluidId(key);

        /// <summary>与主菜单“新建”同一入口（v2 世界），家园有电。不写死坐标（B25）。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
            PipeLeakService.ResetSessionState();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgleak-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = 3000;
            F.PowerUp(s);
            ResearchService.CompleteForTests(s, "defense.barrier", "defense.shield", "defense.trap");
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            return s;
        }

        private static CampaignState RestoreSlot(out string fail)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            ResearchService.ResetForTests();
            IntelService.ResetSessionState();
            SiegeService.ResetSessionState();
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

        /// <summary>一段东西向的管线（<paramref name="n"/> 格，按种子找空地；B25），在第一格挂一个供给者让整条网络是 <paramref name="fluidKey"/>（空串 = 不挂，空管线）。</summary>
        private static List<GridCell> Line(CampaignState s, int n, string fluidKey, float from = 9f, float to = 26f) => Line(s, n, fluidKey, out _, from, to);

        private static List<GridCell> Line(CampaignState s, int n, string fluidKey, out int producer, float from = 9f, float to = 26f)
        {
            producer = -1;
            GridCell? area = F.FindArea(s, n, 1, from, to);
            if (!area.HasValue)
            {
                return null;
            }
            var cells = new List<GridCell>(n);
            for (int i = 0; i < n; i++)
            {
                GridCell c = F.At(area.Value, i, 0);
                if (!PipeNetworkService.TryPlace(s, c, PipePieceKind.Pipe, 0, 0).Ok)
                {
                    return null;
                }
                cells.Add(c);
            }
            if (!string.IsNullOrEmpty(fluidKey))
            {
                producer = PipeNetworkService.Kernel.AddProducer(cells[0].X, cells[0].Y, Fluid(fluidKey), 1_000_000L, 1_000_000L);
            }
            WorldSimulation.StepMany(3);
            return cells;
        }

        private static int CellFluid(GridCell c) => PipeNetworkService.Kernel.TryGetCellInfo(c.X, c.Y, out PipeCellInfo i) ? i.Fluid : -1;

        /// <summary>把这一格管线打到恰好击穿（剩余耐久 = 最大 × 击穿阈值，向下取整）。</summary>
        private static void Puncture(CampaignState s, GridCell c)
        {
            int max = PipeNetworkService.MaxHp(PipePieceKind.Pipe, 0);
            int hp = PipeNetworkService.HpOf(c);
            int target = Mathf.FloorToInt(max * PipeLeakService.BreachRatio);
            if (hp > target)
            {
                PipeNetworkService.TryDamage(s, c, hp - target, out _);
            }
        }

        private static int Dummy(Vector2 at, float hp = 5000f, CombatFaction faction = CombatFaction.Hostile) => Site.Kernel.Spawn(new CombatSpawn
        {
            ExtKey = -1,
            Kind = CombatUnitKind.Enemy,
            Faction = faction,
            Behavior = CombatBehavior.None,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.Instanced | CombatUnitFlags.RemoveOnDeath,
            Position = new double2(at.x, at.y),
            Home = new double2(at.x, at.y),
            Radius = 0.4f,
            Health = hp,
            MaxHealth = hp,
            Weapon = -1,
            BehaviorProfile = -1,
            Priority = 1,
        });

        private static uint StatusOf(int id) => Site.Kernel.TryGetStatus(id, out uint m, out double until, out _, out _, out _) && until > Site.Kernel.Time ? m : 0u;

        private static float HpOfUnit(int id) => Site.Kernel.TryGetUnit(id, out CombatUnitView v) ? v.Health : -1f;

        private static bool Zone(PipeLeakRecord r, out CombatZone z)
        {
            z = default;
            return r != null && Site != null && Site.TryGetLeak(r.Id, out z);
        }

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static NotificationMember LastMember(string typeId) =>
            NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Members.LastOrDefault();

        private static int DeflagrateRule()
        {
            IReadOnlyList<GameConfig.fg.Reaction> rules = NamedReactionCatalog.TagRules;
            for (int i = 0; i < rules.Count; i++)
            {
                if (rules[i].Id == "reaction_deflagrate")
                {
                    return i;
                }
            }
            return -1;
        }

        private static string Short(string s) => s == null ? string.Empty : s.Length > 200 ? s.Substring(0, 200) + "…" : s;

        // ── A 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string root = LocateRepo();
            var psi = new ProcessStartInfo("python", "tools/cell_tables/fgdata.py --dump")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = root,
                CreateNoWindow = true,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            var src = new List<string>();
            try
            {
                using (Process p = Process.Start(psi))
                {
                    string all = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(60000);
                    src = all.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("FLK\t", StringComparison.Ordinal) || l.StartsWith("CX\tcodex.logistics.leak\t", StringComparison.Ordinal)).ToList();
                }
            }
            catch (Exception e)
            {
                Fail("A1 跑 fgdata.py --dump 失败：" + e.Message.Replace("\0", string.Empty));
            }
            var runtime = new List<string>();
            foreach (GameConfig.fg.FluidLeak r in ConfigSystem.Instance.Tables.TbFluidLeak.DataList)
            {
                runtime.Add(string.Join("\t", "FLK", r.Fluid.ToString(CultureInfo.InvariantCulture), r.Tag, Norm(r.RadiusScale), r.SortOrder.ToString(CultureInfo.InvariantCulture)));
            }
            GameConfig.fg.CodexEntry cx = ConfigSystem.Instance.Tables.TbCodexEntry.DataList.FirstOrDefault(e => e.Id == "codex.logistics.leak");
            if (cx != null)
            {
                runtime.Add(string.Join("\t", "CX", cx.Id, cx.Tab, cx.TitleKey, cx.BodyKey, cx.HintKey, cx.Links, cx.Hooks, cx.SortOrder.ToString()));
            }
            var diff = runtime.Except(src).Concat(src.Except(runtime)).ToList();
            Expect(src.Count == 6 && diff.Count == 0,
                $"A1 fg.TbFluidLeak {runtime.Count - 1} 行与图鉴“管线泄漏与液洼”与 fgdata_leak.py 源数据逐字段一致（源 {src.Count} 行）{(diff.Count > 0 ? "；不一致：" + string.Join(" | ", diff.Take(4)).Replace("	", "→") : string.Empty)}");

            bool tags = PipeLeakService.TagOf(Fluid("fuel")) == "Oil" && PipeLeakService.TagOf(Fluid("coolant")) == "Wet" && PipeLeakService.TagOf(Fluid("acid")) == "Acid"
                        && PipeLeakService.MaskOf(Fluid("fuel")) == Bit("Oil") && Bit("Oil") != 0u && PipeLeakService.Problems.Count == 0
                        && new[] { "water", "crude", "coolant", "fuel", "acid" }.All(k => PipeLeakService.MaskOf(Fluid(k)) != 0u);
            string[] tuning = { "logistics.leak.breach_hp_ratio", "logistics.leak.base_radius", "logistics.leak.max_radius", "logistics.leak.grow_seconds", "logistics.leak.fade_seconds",
                "logistics.leak.react_seconds", "logistics.leak.fire_dps", "logistics.leak.fire_tick_seconds", "logistics.leak.sync_seconds", "logistics.leak.max_puddles", "logistics.leak.warn_margin_cells",
                "logistics.leak.perf.puddles", "logistics.leak.perf.sync_ms" };
            bool tuned = tuning.All(k => GridContent.TryGetTuning(k, out _));
            Expect(tags && tuned,
                $"A2 FGR-LOG-046 流体 → 液洼标签：燃油 → 油污、冷却液 → 浸湿、酸液 → 腐蚀（水 → 浸湿、原油 → 油污，待用户复核），每种流体都有内核位；logistics.leak.* 调参 {tuning.Length} 条齐全（{tags} / {tuned}）" +
                (PipeLeakService.Problems.Count > 0 ? "；问题：" + string.Join("；", PipeLeakService.Problems) : string.Empty));

            string[] keys = { "leak.notify.started", "leak.notify.react", "leak.notify.react_far", "leak.hover.title", "leak.hover.tag", "leak.hover.leaking", "leak.hover.fading",
                "leak.hover.reacted", "leak.hover.reacted_safe", "leak.hover.design", "leak.hover.react_none", "leak.hover.placeholder", "leak.pipe.breached", "leak.reason.cap",
                "leak.residue.unknown", "codex.logistics.leak.title", "codex.logistics.leak.body", "codex.logistics.leak.hint" };
            bool zh = keys.All(GameText.Has);
            string bodyZh = GameText.Get("codex.logistics.leak.body");
            string hoverZh = GameText.Get("leak.hover.design");
            GameSettings.SetLanguage(GameLanguage.En);
            string bodyEn = GameText.Get("codex.logistics.leak.body");
            bool en = keys.All(k => GameText.Has(k) && !ContainsCjk(GameText.Get(k)));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            MechanicCodexEntry entry = MechanicCodex.Find("codex.logistics.leak");
            bool hooks = new[] { GuidanceHooks.LogisticsLeakFirst, GuidanceHooks.LogisticsLeakFirstReact, GuidanceHooks.LogisticsLeakFirstFaded }.All(GuidanceHooks.Known.Contains)
                         && entry != null && entry.Hooks.Contains(GuidanceHooks.LogisticsLeakFirst) && entry.Hooks.Contains(GuidanceHooks.LogisticsLeakFirstReact);
            bool notify = NotificationCatalog.TryGetType("pipe_leak", out NotifyTypeDef t1) && NotificationCatalog.TryGetType("leak_fire", out NotifyTypeDef t2)
                          && t1.Tier == NotifyLevel.Warning && t2.Tier == NotifyLevel.Warning
                          // 通知音走警告级的提示音（同级冷却）；不占用任何反馈时刻 → 通知的映射（否则别处的同名时刻会被误转成泄漏通知）。
                          && t1.Cues.Length == 0 && t2.Cues.Length == 0 && NotificationCatalog.ValidationErrors.Count == 0
                          && FgAwaySection("pipe_leak") == "buildings" && FgAwaySection("leak_fire") == "buildings";
            Expect(zh && en && bodyZh.Contains("有意设计") && hoverZh.Contains("有意设计") && bodyEn.Contains("intended") && hooks && notify,
                $"A3 卡片“必须同时交付”：图鉴“管线泄漏与液洼”与悬停提示写明连锁烧毁是有意设计的涌现玩法（中 / 英）；文本键 {keys.Length} 个中英都在（英文不含中文）；" +
                $"钩子 logistics.leak.first / first_react / first_faded 是真钩子且解锁图鉴（{hooks}）；通知“管线泄漏 / 泄漏起火”为警告级、记进离家报告建筑段（{notify}）");
        }

        private static string LocateRepo()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "cell_tables", "fgdata.py")))
                {
                    return dir.FullName;
                }
            }
            return Directory.GetCurrentDirectory();
        }

        private static string FgAwaySection(string typeId) =>
            ConfigSystem.Instance.Tables.TbNotifyType.DataList.FirstOrDefault(r => r.Id == typeId)?.AwaySection ?? string.Empty;

        private static bool ContainsCjk(string s) => s != null && s.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);

        private static string Norm(float v)
        {
            string r = v.ToString("R", CultureInfo.InvariantCulture);
            return r.Contains(".") || r.Contains("E") ? r : r + ".0";
        }

        // ── K 战斗内核（独立内核）──────────────────────────────────────────────────

        private static CombatKernel NewKernel()
        {
            var k = new CombatKernel(CombatSite.ConfigFromTuning(), 64);
            k.SetReactionRules(NamedReactionCatalog.BuildKernelRules());
            k.SetStatusFx(NamedReactionCatalog.BuildStatusFx());
            return k;
        }

        private static int KDummy(CombatKernel k, double2 at, CombatFaction f = CombatFaction.Hostile, CombatUnitKind kind = CombatUnitKind.Enemy) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1,
            Kind = kind,
            Faction = f,
            Behavior = CombatBehavior.None,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
            Position = at,
            Home = at,
            Radius = 0.4f,
            Health = 1000f,
            MaxHealth = 1000f,
            Weapon = -1,
            BehaviorProfile = -1,
            Priority = 1,
        });

        private static void KRun(CombatKernel k, float seconds, ref double time)
        {
            float dt = 1f / 60f;
            int n = Mathf.RoundToInt(seconds * 60f);
            for (int i = 0; i < n; i++)
            {
                k.Step(dt, time);
                time += dt;
            }
        }

        private static uint KStatus(CombatKernel k, int id) => k.TryGetStatus(id, out uint m, out double until, out _, out _, out _) && until > k.Time ? m : 0u;

        private static void CheckKernel()
        {
            uint oil = Bit("Oil"), fire = Bit("Fire"), wet = Bit("Wet");
            int defl = DeflagrateRule();
            double time = 0;
            using CombatKernel k = NewKernel();
            bool up = k.UpsertLeakZone(7, new double2(0, 0), 1.5f, oil);
            int foe = KDummy(k, new double2(0.5, 0));
            int ally = KDummy(k, new double2(-0.5, 0), CombatFaction.Player, CombatUnitKind.Machine);
            int wall = KDummy(k, new double2(0, 0.5), CombatFaction.Player, CombatUnitKind.Structure);
            int outside = KDummy(k, new double2(4, 0));
            KRun(k, 1.2f, ref time);
            bool tagged = (KStatus(k, foe) & oil) != 0u && (KStatus(k, ally) & oil) != 0u && KStatus(k, wall) == 0u && KStatus(k, outside) == 0u;
            bool stillPuddle = k.TryGetLeakZone(7, out CombatZone z0) && z0.Phase == CombatConst.LeakPhasePuddle && z0.Faction == CombatFaction.Neutral && z0.Kind == CombatConst.ZoneKindLeak;
            Expect(up && tagged && stillPuddle && k.LeakZoneCount == 1,
                $"K1 液洼 = 中立内核区域：站进去的敌人与己方机器都按节拍挂上油污、结构单位（建筑 / 炮塔）不在这里结算、圈外的不挂（{tagged}）；没有火时一直是液洼（{stillPuddle}）");

            // 燃烧的敌人走进油污液洼 → 整片反应成燃烧区。
            int count0 = k.ReactionCountOf(defl);
            int burner = KDummy(k, new double2(0, -0.6));
            k.ApplyStatus(burner, fire, 20f, 4f, 0f, 0f, 0);
            KRun(k, 0.6f, ref time);
            var drained = new List<int2>();
            k.DrainLeakReactions(drained);
            bool reacted = k.TryGetLeakZone(7, out CombatZone z1) && z1.Phase == CombatConst.LeakPhaseReacted && z1.StatusMask == fire && z1.Radius >= 1.5f - 1e-4f
                           && z1.Look == CombatZoneLook.Residue && z1.StatusDps > 0f && drained.Count == 1 && drained[0].x == 7 && drained[0].y == defl
                           && k.ReactionCountOf(defl) >= count0 + 1;
            KRun(k, 0.6f, ref time);
            bool burns = (KStatus(k, foe) & fire) != 0u && (KStatus(k, ally) & fire) != 0u;
            // 结构单位：液洼与它反应成的燃烧区都不在内核里结算（建筑 / 炮塔挨的火由热更层按建筑耐久结算一次）。单独一块：只有着火的敌人和一座结构单位。
            using CombatKernel ks = NewKernel();
            double ts = 0;
            ks.UpsertLeakZone(9, new double2(0, 0), 1.5f, oil);
            int structure = KDummy(ks, new double2(0.6, 0.6), CombatFaction.Player, CombatUnitKind.Structure);
            int lone = KDummy(ks, new double2(-0.6, -0.6));
            ks.ApplyStatus(lone, fire, 20f, 4f, 0f, 0f, 0);
            KRun(ks, 2f, ref ts);
            bool structSkipped = ks.TryGetLeakZone(9, out CombatZone zs) && zs.Phase == CombatConst.LeakPhaseReacted && KStatus(ks, structure) == 0u;
            burns &= structSkipped;
            float seconds = (float)(z1.Until - z1.Born);
            string kdiag = $"阶段 {z1.Phase}、标签 {z1.StatusMask:X}/{fire:X}、外观 {z1.Look}、持续伤害 {z1.StatusDps:F2}、反应记录 {drained.Count}（{string.Join(",", drained.Select(d => d.x + ":" + d.y))}，爆燃 {defl}）、" +
                           $"爆燃计数 {count0} → {k.ReactionCountOf(defl)}；敌人 {KStatus(k, foe):X}、机器 {KStatus(k, ally):X}、结构 {KStatus(k, wall):X}";
            Expect(reacted && burns && seconds >= CombatSite.Tuning("logistics.leak.react_seconds", 12f) - 0.01f,
                $"K2 验收探针（内核层）：着火的敌人站进燃油液洼 → 整片液洼反应成燃烧区（挂燃烧、半径 {z1.Radius:F2} 米 ≥ 液洼、持续 {seconds:F1} 秒 = max(反应残留, 液洼反应时长)），" +
                $"爆燃计数增加、给热更层记一条液洼反应（{reacted}）；燃烧区点燃里面的敌人与己方机器、不重复结算结构单位（{burns}）" + (reacted && burns ? string.Empty : "；" + kdiag));

            // 两摊不同的液洼挨着、浸湿液洼遇火：都不整片反应（浸湿 + 燃烧的“蒸汽”不留残留区域）。
            using CombatKernel k2 = NewKernel();
            double t2 = 0;
            k2.UpsertLeakZone(1, new double2(0, 0), 1.5f, oil);
            k2.UpsertLeakZone(2, new double2(1.2, 0), 1.5f, wet);
            int b2 = KDummy(k2, new double2(5, 5));
            KRun(k2, 2f, ref t2);
            bool apart = k2.TryGetLeakZone(1, out CombatZone a) && a.Phase == 0 && k2.TryGetLeakZone(2, out CombatZone b) && b.Phase == 0;
            int wetBurner = KDummy(k2, new double2(1.6, 0));
            k2.ApplyStatus(wetBurner, fire, 20f, 4f, 0f, 0f, 0);
            KRun(k2, 2f, ref t2);
            bool wetStays = k2.TryGetLeakZone(2, out CombatZone w) && w.Phase == 0 && w.StatusMask == wet;
            // 移除 / 更新：更新只改还没反应的那块；移除连同残留区域。
            bool update = k2.UpsertLeakZone(2, new double2(1.2, 0), 2.2f, wet) && k2.TryGetLeakZone(2, out CombatZone w2) && Mathf.Abs(w2.Radius - 2.2f) < 1e-4f;
            bool updateReacted = !k.UpsertLeakZone(7, new double2(0, 0), 3f, oil);
            int removed = k.RemoveLeakZone(7);
            Expect(apart && wetStays && update && updateReacted && removed == 1 && k.LeakZoneCount == 0 && b2 > 0,
                $"K3 负向：油污液洼与浸湿液洼挨着不互相反应（{apart}）；浸湿液洼里站着着火的单位不整片反应（蒸汽不留残留区域，FG-GAP-053）（{wetStays}）；" +
                $"更新只改还没反应的液洼（{update} / 已反应的不动 {updateReacted}）；移除连同残留区域（{removed}）");
        }

        private static void CheckKernelSnapshot()
        {
            uint oil = Bit("Oil"), fire = Bit("Fire");
            double time = 0;
            using CombatKernel k = NewKernel();
            k.UpsertLeakZone(3, new double2(0, 0), 1.4f, oil);
            k.UpsertLeakZone(4, new double2(10, 0), 1.4f, oil);
            int burner = KDummy(k, new double2(10.3, 0));
            k.ApplyStatus(burner, fire, 30f, 4f, 0f, 0f, 0);
            KRun(k, 1f, ref time);
            ulong hash = k.StateHash();
            byte[] cur = k.Serialize();
            byte[] old = k.SerializeFormatForTests(11);
            using var a = new CombatKernel(k.Config, 64);
            CombatLoadResult ra = a.Load(cur);
            bool same = ra == CombatLoadResult.Ok && a.StateHash() == hash && a.TryGetLeakZone(3, out CombatZone p) && p.Phase == 0 && p.StatusMask == oil
                        && a.TryGetLeakZone(4, out CombatZone q) && q.Phase == CombatConst.LeakPhaseReacted && q.StatusMask == fire;
            using var b = new CombatKernel(k.Config, 64);
            CombatLoadResult rb = b.Load(old);
            bool oldOk = rb == CombatLoadResult.Ok && b.LeakZoneCount == 0 && CombatKernel.PeekFormat(old) == 11;
            // 坏值：把液洼的外部编号改成正数（与单位编号混淆）→ 整份拒绝。
            byte[] bad = CorruptLeakOwner(cur);
            using var c = new CombatKernel(k.Config, 64);
            CombatLoadResult rc = bad != null ? c.Load(bad) : CombatLoadResult.Ok;
            Expect(CombatKernel.PeekFormat(cur) == CombatConst.FormatVersion && CombatConst.FormatVersion == 12 && same && oldOk && bad != null && rc != CombatLoadResult.Ok,
                $"K4 内核快照格式 12：液洼与已反应的燃烧区（阶段 / 标签）往返、状态哈希一致（{same}）；写格式 11 时略过液洼、旧格式照常读（{oldOk}，由热更层按记录重新登记）；" +
                $"液洼外部编号被改坏 → 整份拒绝（{rc}）");
        }

        /// <summary>在快照里找到液洼区域的 Owner 字段（-3，紧跟着阵营字节 2 = 中立）改成 +3。</summary>
        private static byte[] CorruptLeakOwner(byte[] data)
        {
            byte[] owner = BitConverter.GetBytes(-3);
            for (int i = 0; i + 5 < data.Length; i++)
            {
                if (data[i] == owner[0] && data[i + 1] == owner[1] && data[i + 2] == owner[2] && data[i + 3] == owner[3] && data[i + 4] == (byte)CombatFaction.Neutral)
                {
                    byte[] copy = (byte[])data.Clone();
                    byte[] plus = BitConverter.GetBytes(3);
                    Array.Copy(plus, 0, copy, i, 4);
                    return copy;
                }
            }
            return null;
        }

        // ── B 击穿 ───────────────────────────────────────────────────────────────

        private static void CheckBreach()
        {
            CampaignState s = NewWorld(7401);
            List<GridCell> line = Line(s, 3, "fuel");
            List<GridCell> empty = Line(s, 3, string.Empty, 14f, 30f);
            if (line == null || empty == null)
            {
                Fail("B 测试准备：放不下管线");
                return;
            }
            GridCell c = line[1];
            int max = PipeNetworkService.MaxHp(PipePieceKind.Pipe, 0);
            int fuel = Fluid("fuel");
            int n0 = NotifyCount("pipe_leak");
            PipeNetworkService.TryDamage(s, c, Mathf.FloorToInt(max * 0.4f), out _);
            bool noneAbove = PipeLeakService.At(s, c) == null && CellFluid(c) == fuel;
            Puncture(s, c);
            PipeLeakRecord r = PipeLeakService.At(s, c);
            bool started = r != null && r.Fluid == fuel && r.State == PipeLeakService.StateLeaking && PipeLeakService.Count(s) == 1;
            NotificationMember m = LastMember("pipe_leak");
            bool told = NotifyCount("pipe_leak") == n0 + 1 && m != null && m.HasLocation && Mathf.RoundToInt(m.Location.x) == c.X && Mathf.RoundToInt(m.Location.z) == c.Y
                        && m.DetailText.Contains("燃油");
            WorldSimulation.StepMany(2);
            bool zone = Zone(r, out CombatZone z) && z.Phase == 0 && z.StatusMask == Bit("Oil") && Mathf.Abs(z.Radius - PipeLeakService.RadiusOf(r, GameClock.Ticks)) < 0.1f;
            bool codex = MechanicCodex.IsUnlocked("codex.logistics.leak");
            PipeNetworkService.TryDescribeHover(s, c, out _, out string body);
            bool hover = body != null && body.Contains("已击穿") && body.Contains("油污") && body.Contains("有意设计");
            Expect(noneAbove && started && told && zone && codex && hover,
                $"B1 FGR-LOG-046 击穿：耐久还剩 60% 时不漏（{noneAbove}）；打到 {PipeLeakService.BreachRatio:P0} 及以下 → 破口处积起燃油液洼（{started}）、内核里是挂油污的中立区域（{zone}）；" +
                $"发“管线泄漏”警告且可定位（{told}：{Short(m?.DetailText)}）；图鉴解锁（{codex}）；管线悬停写明已击穿、液洼标签与有意设计（{hover}）");

            PipeNetworkService.TryDamage(s, empty[1], max, out _);
            bool emptyNone = PipeLeakService.At(s, empty[1]) == null && PipeLeakService.Count(s) == 1;
            Expect(emptyNone, "B2 负向：空管线（网络里没有流体）被打掉不积液洼");

            float r0 = PipeLeakService.RadiusOf(r, GameClock.Ticks);
            var radii = new List<float>();
            for (int i = 0; i < 6; i++)
            {
                F.Seconds(PipeLeakService.GrowSeconds / 5f);
                radii.Add(PipeLeakService.RadiusOf(r, GameClock.Ticks));
            }
            bool grows = radii.Zip(radii.Skip(1), (x, y) => y >= x - 1e-4f).All(b => b) && radii.Last() > r0 && Mathf.Abs(radii.Last() - PipeLeakService.MaxRadius) < 0.01f
                         && Zone(r, out CombatZone zz) && Mathf.Abs(zz.Radius - radii.Last()) < 0.05f;
            Expect(grows, $"B3 一直漏：液洼半径 {r0:F2} → {radii.Last():F2} 米（最大 {PipeLeakService.MaxRadius:F2}）逐步扩大，内核区域同步（{string.Join(" / ", radii.Select(x => x.ToString("F2")))}）");

            int foe = Dummy(new Vector2(c.X + 0.4f, c.Y));
            int far = Dummy(new Vector2(c.X + 8f, c.Y + 8f));
            F.Seconds(1.2f);
            bool tagged = (StatusOf(foe) & Bit("Oil")) != 0u && (StatusOf(far) & Bit("Oil")) == 0u;
            F.Seconds(10f);
            bool noFire = r.State == PipeLeakService.StateLeaking && Zone(r, out CombatZone z2) && z2.Phase == 0;
            Expect(tagged && noFire, $"B4 带标签的液洼：站进去的敌人挂上油污、圈外的不挂（{tagged}）；没有火 10 秒都不反应（{noFire}）");
        }

        // ── C 验收探针与连锁 ──────────────────────────────────────────────────────

        private static void CheckProbeAndChain()
        {
            CampaignState s = NewWorld(7402);
            List<GridCell> line = Line(s, 6, "fuel");
            if (line == null)
            {
                Fail("C 测试准备：放不下 6 格燃油管线");
                return;
            }
            // 管线旁边铺一格传送带、立一座电塔：连锁烧毁要烧到“自己的产线”。
            GridCell beltCell = new GridCell(line[1].X, line[1].Y + 1);
            bool belt = BeltNetworkService.TryPlace(s, beltCell, BeltDir.East, 0).Ok;
            GridOpResult pole = HomeGridService.TryPlace(s, "power_pole", new GridCell(line[2].X, line[2].Y - 1), 0);
            BuildingRecord poleRec = pole.Success ? HomeGridService.FindBuilding(s, pole.BuildingId) : null;
            if (poleRec != null)
            {
                poleRec.ConstructionState = BuildingConstructionState.Operational; // 测试捷径：直接建成（真实施工由 FG3-LOG-02 覆盖）
                poleRec.Health = BuildingOps.MaxDurability("power_pole");
                HomeValleyPowerGrid.Recompute(s);
            }
            int machine = SpawnMachine(s, new Vector2(line[0].X - 0.3f, line[0].Y + 0.4f));
            float machineHp0 = MachineRegistry.TryGetRecord(machine, out MachineRecord mr0) ? mr0.Health : -1f;
            PipeNetworkService.TryDamage(s, line[0], PipeNetworkService.MaxHp(PipePieceKind.Pipe, 0), out _); // 打掉第一格：虚影在、相邻还有燃油 → 一直漏
            PipeLeakRecord r = PipeLeakService.At(s, line[0]);
            F.Seconds(1f);
            int defl = DeflagrateRule();
            int fires0 = NotifyCount("leak_fire");
            int warn0 = PipeLeakService.FireWarnings;
            int bystander = Dummy(new Vector2(line[0].X + 0.5f, line[0].Y - 0.5f));
            float byHp0 = HpOfUnit(bystander);
            int burner = Dummy(new Vector2(line[0].X - 0.2f, line[0].Y));
            Site.Kernel.ApplyStatus(burner, Bit("Fire"), 30f, 4f, 0f, 0f, 0);
            bool lit = F.StepUntil(() => r.State == PipeLeakService.StateReacted, 3);
            Zone(r, out CombatZone z);
            bool burningZone = lit && z.Phase == CombatConst.LeakPhaseReacted && z.StatusMask == Bit("Fire") && z.Radius >= PipeLeakService.BaseRadius - 0.01f && r.Damaging
                               && r.Rule == defl && PipeLeakService.ResidueName(r.Rule) == GameText.Get("reaction.deflagrate.residue");
            NotificationMember m = LastMember("leak_fire");
            bool warned = NotifyCount("leak_fire") > fires0 && PipeLeakService.FireWarnings > warn0 && m != null && m.HasLocation && m.DetailText.Contains("烧坏");
            F.Seconds(2f);
            bool hurts = (StatusOf(bystander) & Bit("Fire")) != 0u && HpOfUnit(bystander) < byHp0;
            float machineHp1 = MachineRegistry.TryGetRecord(machine, out MachineRecord mr1) ? mr1.Health : -1f;
            bool ownMachine = machine > 0 && machineHp1 < machineHp0;
            Expect(burningZone && warned && hurts,
                $"C1 卡片验收·行为探针：燃油泄漏遇到燃烧（着火的敌人站进液洼）→ 整片变成燃烧区（{burningZone}：挂燃烧、半径 {z.Radius:F2} 米、名字“{PipeLeakService.ResidueName(r.Rule)}”、爆燃规则）；" +
                $"燃烧区点燃并烧伤旁边的敌人（{hurts}）；起火时发“泄漏起火”告警、可定位、写明会烧坏设施（{warned}：{Short(m?.DetailText)}）");
            Expect(ownMachine, $"C2 卡片负向“烧到自己的产线”：燃烧区不分敌我，站在里面的己方机器也挨烧（{machineHp0:F0} → {machineHp1:F0}）");

            // 连锁：燃烧区烧坏范围里的管线 / 传送带 / 电塔；被烧穿的管线再漏、再被点燃，火沿着管线蔓延。
            float poleHp0 = poleRec != null ? BuildingOps.Durability(poleRec) : 0f;
            int beltLost0 = belt ? BeltNetworkService.DamageOf(beltCell) : 0;
            int reacted0 = PipeLeakService.ReactedCount;
            int farthest = 0;
            for (int sec = 0; sec < 24; sec++)
            {
                F.Seconds(1f);
                for (int i = 0; i < line.Count; i++)
                {
                    if (PipeLeakService.At(s, line[i]) != null)
                    {
                        farthest = Math.Max(farthest, i);
                    }
                }
            }
            int puddles = PipeLeakService.StartedCount;
            int reacted = PipeLeakService.ReactedCount - reacted0;
            bool beltBurnt = belt && (BeltNetworkService.DamageOf(beltCell) > beltLost0 || BeltNetworkService.HpOf(beltCell) < 0);
            bool poleBurnt = poleRec != null && (BuildingOps.Durability(poleRec) < poleHp0 || poleRec.ConstructionState != BuildingConstructionState.Operational);
            Expect(puddles >= 2 && reacted >= 1 && farthest >= 1 && beltBurnt && poleBurnt && PipeLeakService.FireHits > 0,
                $"C3 连锁烧毁（有意设计的涌现玩法）：燃烧区烧坏范围里的管线 / 传送带 / 电塔（传送带 {beltBurnt}、电塔 {poleBurnt}、命中 {PipeLeakService.FireHits} 次）；" +
                $"被烧穿的燃油管线再泄漏（共积起 {puddles} 摊，火蔓延到第 {farthest + 1} 格）、再被点燃（又反应 {reacted} 次）");
        }

        private static int SpawnMachine(CampaignState s, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId, at, 100f, 100f);
            if (!r.Success)
            {
                return 0;
            }
            if (MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                if (!string.IsNullOrEmpty(rec.BlueprintId))
                {
                    MachineLoadoutRegistry.Register(s, r.LogicId, rec.BlueprintId, rec.BlueprintVersion);
                }
                rec.WorkPriorities ??= WorkPriorities.Default();
                foreach (WorkOrderKind kind in Enum.GetValues(typeof(WorkOrderKind)))
                {
                    rec.WorkPriorities.Set(kind, 0); // 站着不动：不让工单引擎把它派走
                }
            }
            WorldSimulation.StepMany(2);
            return r.LogicId;
        }

        private static void CheckTrapIgnition()
        {
            CampaignState s = NewWorld(7403);
            List<GridCell> line = Line(s, 3, "crude");
            if (line == null)
            {
                Fail("C4 测试准备：放不下原油管线");
                return;
            }
            Puncture(s, line[1]);
            PipeLeakRecord r = PipeLeakService.At(s, line[1]);
            F.Seconds(1f);
            // 燃烧场地（陷阱发射器 / 燃烧读法铺的区域）压到液洼上：液洼本身遇火，不需要单位在场。
            bool laid = Site.SpawnTrapField(new Vector2(line[1].X + 1.5f, line[1].Y), 1.0f, 6f, 0f, Bit("Fire"), 4f, 0f, 0f);
            bool lit = F.StepUntil(() => r != null && r.State == PipeLeakService.StateReacted, 3);
            Expect(laid && lit && r.Damaging,
                $"C4 原油液洼（油污）被压上来的燃烧场地点燃（不需要单位在场）：整片变成燃烧区（{lit}）");
        }

        // ── N 负向 ───────────────────────────────────────────────────────────────

        private static void CheckNonFlammable()
        {
            CampaignState s = NewWorld(7404);
            List<GridCell> water = Line(s, 3, "coolant");
            List<GridCell> acid = Line(s, 3, "acid", 14f, 30f);
            if (water == null || acid == null)
            {
                Fail("N1 测试准备：放不下管线");
                return;
            }
            Puncture(s, water[1]);
            Puncture(s, acid[1]);
            PipeLeakRecord rw = PipeLeakService.At(s, water[1]);
            PipeLeakRecord ra = PipeLeakService.At(s, acid[1]);
            F.Seconds(0.6f);
            int burner = Dummy(new Vector2(water[1].X, water[1].Y));
            Site.Kernel.ApplyStatus(burner, Bit("Fire"), 30f, 4f, 0f, 0f, 0);
            int foe = Dummy(new Vector2(acid[1].X + 0.3f, acid[1].Y));
            float hp0 = HpOfUnit(foe);
            F.Seconds(4f);
            bool waterStays = rw != null && rw.State == PipeLeakService.StateLeaking && Zone(rw, out CombatZone zw) && zw.Phase == 0 && zw.StatusMask == Bit("Wet");
            bool acidTag = ra != null && (StatusOf(foe) & Bit("Acid")) != 0u;
            PipeLeakService.TryDescribe(s, rw, out _, out string body);
            bool hover = body != null && body.Contains(GameText.Get("leak.hover.react_none")) && !PipeLeakService.CanReactAsWhole(Fluid("coolant")) && PipeLeakService.CanReactAsWhole(Fluid("fuel"));
            Expect(waterStays && acidTag && hover,
                $"N1 负向：冷却液液洼（浸湿）里站着着火的单位也不整片反应（{waterStays}）；酸液液洼给站进去的敌人挂腐蚀（{acidTag}，耐久 {hp0:F0} → {HpOfUnit(foe):F0}）；" +
                $"悬停写明“这种液洼本身不会整片反应”（{hover}）");
        }

        private static void CheckRepairFades()
        {
            CampaignState s = NewWorld(7405);
            List<GridCell> line = Line(s, 3, "fuel");
            if (line == null)
            {
                Fail("N2 测试准备：放不下管线");
                return;
            }
            GridCell c = line[1];
            Puncture(s, c);
            PipeLeakRecord r = PipeLeakService.At(s, c);
            F.Seconds(6f);
            float before = PipeLeakService.RadiusOf(r, GameClock.Ticks);
            PipeNetworkService.TryRepair(s, c, PipeNetworkService.MaxHp(PipePieceKind.Pipe, 0));
            F.Seconds(PipeLeakService.SyncSeconds + 0.1f);
            bool fading = r.State == PipeLeakService.StateFading;
            var radii = new List<float>();
            for (int i = 0; i < 4; i++)
            {
                F.Seconds(PipeLeakService.FadeSeconds / 5f);
                radii.Add(PipeLeakService.RadiusOf(r, GameClock.Ticks));
            }
            bool shrinks = radii.Zip(radii.Skip(1), (x, y) => y < x).All(b => b) && radii[0] < before && Zone(r, out CombatZone zf) && Mathf.Abs(zf.Radius - radii.Last()) < 0.1f;
            PipeLeakService.TryDescribe(s, r, out _, out string body);
            bool text = body != null && body.Contains("消退");
            bool gone = F.StepUntil(() => PipeLeakService.At(s, c) == null, Mathf.CeilToInt(PipeLeakService.FadeSeconds));
            bool zoneGone = Site.TryGetLeak(r.Id, out _) == false && Site.LeakZoneCount == 0;
            Expect(fading && shrinks && text && gone && zoneGone,
                $"N2 卡片“修好管线后液洼逐渐消失”：耐久修回阈值以上 → 下一次对账转为消退（{fading}）、半径 {before:F2} → {string.Join(" → ", radii.Select(x => x.ToString("F2")))} 米逐步缩小（{shrinks}）、" +
                $"悬停写“正在消退”（{text}）、{PipeLeakService.FadeSeconds:F0} 秒内消失且内核区域移除（{gone} / {zoneGone}）");

            // 消退中又被打穿：从当前半径接着扩大，不跳变。
            Puncture(s, c);
            PipeLeakRecord r2 = PipeLeakService.At(s, c);
            F.Seconds(5f);
            PipeNetworkService.TryRepair(s, c, PipeNetworkService.MaxHp(PipePieceKind.Pipe, 0));
            F.Seconds(PipeLeakService.FadeSeconds / 3f);
            float mid = PipeLeakService.RadiusOf(r2, GameClock.Ticks);
            Puncture(s, c);
            float after = PipeLeakService.RadiusOf(r2, GameClock.Ticks);
            F.Seconds(2f);
            float floor = PipeLeakService.BaseRadius;
            Expect(r2 != null && r2.State == PipeLeakService.StateLeaking && PipeLeakService.At(s, c) == r2 && Mathf.Abs(after - Math.Max(mid, floor)) < 0.05f
                   && PipeLeakService.RadiusOf(r2, GameClock.Ticks) > after,
                $"N3 消退中又被打穿：同一摊液洼回到“还在漏”（不新开一摊），从当前半径 {mid:F2} 米（不小于刚漏出时的 {floor:F2} 米）接着扩大（{after:F2} → {PipeLeakService.RadiusOf(r2, GameClock.Ticks):F2}）");
        }

        private static void CheckFlushAndGhost()
        {
            CampaignState s = NewWorld(7406);
            List<GridCell> line = Line(s, 4, "fuel", out int producer);
            if (line == null)
            {
                Fail("N4 测试准备：放不下管线");
                return;
            }
            // 被打掉：虚影在、两侧还有燃油 → 一直漏。
            PipeNetworkService.TryDamage(s, line[2], PipeNetworkService.MaxHp(PipePieceKind.Pipe, 0), out _);
            PipeLeakRecord r = PipeLeakService.At(s, line[2]);
            F.Seconds(10f);
            bool keeps = r != null && r.State == PipeLeakService.StateLeaking && PipeLeakService.IsBreached(s, r);
            PlannedBeltRecord ghost = (s.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>())
                .FirstOrDefault(p => p != null && p.Destroyed && p.PipePiece > 0 && p.Xs != null && p.Xs.Length > 0 && p.Xs[0] == line[2].X && p.Ys[0] == line[2].Y);
            bool removed = ghost != null && HomeValleyConstruction.RemoveDestroyedGhost(s, ghost.PlanId);
            F.Seconds(PipeLeakService.SyncSeconds + 0.1f);
            bool fadesAfterGhost = r != null && r.State == PipeLeakService.StateFading;
            Expect(keeps && removed && fadesAfterGhost,
                $"N4 被打掉的管线：待重建的虚影还在、两侧还有燃油时 10 秒后仍在漏（{keeps}）；拆掉虚影（不打算修）→ 两侧封口，液洼开始消退（{fadesAfterGhost}）");

            // 冲洗：网络里没有燃油了 → 击穿的管线不再漏。
            Puncture(s, line[0]);
            PipeLeakRecord r2 = PipeLeakService.At(s, line[0]);
            PipeNetworkService.Kernel.RemoveProducer(producer, out _);
            PipeOpResult fr = PipeNetworkService.TryFlush(s, line[0], out _, out _);
            F.Seconds(PipeLeakService.SyncSeconds + 0.1f);
            Expect(r2 != null && fr.Ok && r2.State == PipeLeakService.StateFading,
                $"N5 冲洗网络（先拆掉供给）：击穿的管线里没有燃油了 → 液洼开始消退（{r2?.State}）");
        }

        private static void CheckRebuildFades()
        {
            CampaignState s = NewWorld(7407);
            List<GridCell> line = Line(s, 3, "fuel");
            if (line == null)
            {
                Fail("N6 测试准备：放不下管线");
                return;
            }
            PipeNetworkService.TryDamage(s, line[1], PipeNetworkService.MaxHp(PipePieceKind.Pipe, 0), out _);
            PipeLeakRecord r = PipeLeakService.At(s, line[1]);
            ResearchService.CompleteForTests(s, "defense.repair_drone", "defense.auto_rebuild");
            bool rule = StandingRuleService.TryCreate(s, StandingRuleService.KindRebuild, out StandingRuleRecord rr, out string msg)
                        && StandingRuleService.TryAddTarget(s, rr.Serial, StandingRuleService.BeltTarget, out _);
            bool rebuilt = rule && F.StepUntil(() => PipeNetworkService.HpOf(line[1]) == PipeNetworkService.MaxHp(PipePieceKind.Pipe, 0), 240);
            F.Seconds(PipeLeakService.SyncSeconds + 0.1f);
            bool fading = r != null && (r.State == PipeLeakService.StateFading || PipeLeakService.At(s, line[1]) == null);
            Expect(rebuilt && fading,
                $"N6 正式修复路径：自动重建规则（传送带与物流节点）让机器按原设置重建被打掉的管线（{rebuilt}{(rule ? string.Empty : "：" + msg)}）→ 破口堵上、液洼开始消退（{fading}）");
        }

        private static void CheckCapAndReform()
        {
            CampaignState s = NewWorld(7408);
            int cap = PipeLeakService.MaxPuddles;
            List<GridCell> a = Line(s, 25, "fuel", 9f, 40f);
            List<GridCell> b = Line(s, 25, "fuel", 12f, 44f);
            if (a == null || b == null)
            {
                Fail("N7 测试准备：放不下两条 25 格管线");
                return;
            }
            int refused0 = PipeLeakService.RefusedCount;
            foreach (GridCell c in a.Concat(b))
            {
                Puncture(s, c);
            }
            Expect(PipeLeakService.Count(s) == cap && PipeLeakService.RefusedCount - refused0 == 50 - cap,
                $"N7 B12 极端：同时击穿 50 格，液洼封顶 {cap} 摊（{PipeLeakService.Count(s)}），其余 {PipeLeakService.RefusedCount - refused0} 格不新增并计数");

            // 烧完了破口还在：重新积起一摊（从初始大小开始）。
            CampaignState s2 = NewWorld(7409);
            List<GridCell> line = Line(s2, 3, "fuel");
            if (line == null)
            {
                Fail("N8 测试准备：放不下管线");
                return;
            }
            Puncture(s2, line[1]);
            PipeLeakRecord r = PipeLeakService.At(s2, line[1]);
            F.Seconds(0.6f);
            int burner = Dummy(new Vector2(line[1].X, line[1].Y), 100000f);
            Site.Kernel.ApplyStatus(burner, Bit("Fire"), 2f, 0f, 0f, 0f, 0);
            bool lit = F.StepUntil(() => r.State == PipeLeakService.StateReacted, 3);
            Site.Kernel.Despawn(burner);
            // 测试捷径（相当于维修无人机一直在补、但没修好）：燃烧期间每半秒把三格管线补回击穿阈值，火烧不穿；残留到期后破口还在 → 重新积起。
            int keepHp = Mathf.FloorToInt(PipeNetworkService.MaxHp(PipePieceKind.Pipe, 0) * PipeLeakService.BreachRatio);
            bool reform = false;
            for (int i = 0; lit && i < 80 && !reform; i++)
            {
                foreach (GridCell c in line)
                {
                    int hp = PipeNetworkService.HpOf(c);
                    if (hp >= 0 && hp < keepHp)
                    {
                        PipeNetworkService.TryRepair(s2, c, keepHp - hp);
                    }
                }
                F.Seconds(0.5f);
                reform = r.State == PipeLeakService.StateLeaking || PipeLeakService.At(s2, line[1]) == null;
            }
            bool back = r.State == PipeLeakService.StateLeaking && PipeLeakService.At(s2, line[1]) == r && Zone(r, out CombatZone z) && z.Phase == 0
                        && PipeLeakService.RadiusOf(r, GameClock.Ticks) < PipeLeakService.BaseRadius + 0.5f;
            Expect(lit && reform && back,
                $"N8 燃烧区烧完（{GridContent.Tuning("logistics.leak.react_seconds")} 秒）而破口还在（管线还在漏）：同一处重新积起液洼、从初始大小开始（{back}）");
        }

        // ── F 正式旅程 ───────────────────────────────────────────────────────────

        private static void CheckFormalJourney()
        {
            CampaignState s = NewWorld(7410);
            Vector2 at = FgSiegeSelfCheck.OutsidePoint(s, 34f);
            BuildingRecord wall = FgSiegeSelfCheck.PlaceNear(s, "barrier_t1", at, FgSiegeSelfCheck.CoreCenter(s), 0.35f, 0.7f);
            if (wall == null)
            {
                Fail("F1 测试准备：放不下屏障");
                return;
            }
            var pipes = new List<GridCell>();
            foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1) })
            {
                var c = new GridCell(wall.GridX + dx, wall.GridY + dy);
                if (PipeNetworkService.TryPlace(s, c, PipePieceKind.Pipe, 0, 0).Ok)
                {
                    pipes.Add(c);
                    PipeNetworkService.Kernel.AddProducer(c.X, c.Y, Fluid("fuel"), 1_000_000L, 1_000_000L);
                }
            }
            NavService.SyncGridChanges();
            WorldSimulation.StepMany(3);
            long pipe0 = SiegeService.CollateralPipeHits;
            TransitGroupRecord g = FgSiegeSelfCheck.Arrive(s, at, "foundry", new[] { "foundry.armorbot" }, new[] { 4 });
            bool leaked = pipes.Count > 0 && F.StepUntil(() => pipes.Any(c => PipeLeakService.At(s, c) != null), 150);
            PipeLeakRecord r = pipes.Select(c => PipeLeakService.At(s, c)).FirstOrDefault(x => x != null);
            Expect(leaked && SiegeService.CollateralPipeHits > pipe0 && r != null && r.Fluid == Fluid("fuel") && g != null,
                $"F1 正式旅程：突袭部队到达展开、拆屏障时溅射打穿墙边的燃油管线（命中 {SiegeService.CollateralPipeHits - pipe0} 次）→ 破口积起燃油液洼（{leaked}）");
        }

        // ── G / H / I / J ────────────────────────────────────────────────────────

        /// <summary>一段进行中的泄漏：燃油管线被打掉一格（一直漏）+ 另一格击穿；着火的敌人走进第一摊 → 燃烧区 → 火烧管线；跑 <paramref name="seconds"/> 游戏秒。</summary>
        private static CampaignState LeakScenario(int seed, bool observe, float seconds)
        {
            CampaignState s = NewWorld(seed, observe);
            List<GridCell> line = Line(s, 6, "fuel");
            if (line == null)
            {
                return s;
            }
            PipeNetworkService.TryDamage(s, line[0], PipeNetworkService.MaxHp(PipePieceKind.Pipe, 0), out _);
            Puncture(s, line[4]);
            int burner = Dummy(new Vector2(line[0].X - 0.2f, line[0].Y), 100000f);
            Site.Kernel.ApplyStatus(burner, Bit("Fire"), 30f, 4f, 0f, 0f, 0);
            Dummy(new Vector2(line[4].X + 0.3f, line[4].Y));
            F.Seconds(seconds);
            return s;
        }

        private static string Signature(CampaignState s)
        {
            CombatSite site = Site;
            var pd = new StringBuilder();
            foreach (PipeDamageRecord d in s.Pipes?.Damage ?? Array.Empty<PipeDamageRecord>())
            {
                pd.Append(d.X).Append(',').Append(d.Y).Append(':').Append(d.Lost).Append(';');
            }
            return PipeLeakService.Snapshot(s) + "|P" + pd + "|H" + (site != null ? site.Kernel.StateHash().ToString("X16") : "-");
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = LeakScenario(7420, true, 3f);
            string before = Signature(s);
            bool mixed = PipeLeakService.Records(s).Any(r => r.State == PipeLeakService.StateReacted) && PipeLeakService.Records(s).Any(r => r.State == PipeLeakService.StateLeaking);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            F.Seconds(15f);
            string continuous = Signature(s);
            CampaignState l = RestoreSlot(out string fail);
            string loaded = l != null ? Signature(l) : "读档失败：" + fail;
            if (l != null)
            {
                F.Seconds(15f);
            }
            string after = l != null ? Signature(l) : string.Empty;
            Expect(save.Success && mixed && loaded == before && after == continuous && PipeLeakService.OrphansRemoved == 0,
                "G1 B10 真文件存读档（一摊在燃烧、一摊还在漏）：液洼记录（阶段 / 各个步 / 规则）、管线耐久、内核里的液洼与燃烧区（随地点快照）逐位一致；读档后接着跑 15 游戏秒与不存档连续跑逐位一致" +
                (loaded == before ? string.Empty : $"\n存前：{Short(before)}\n读后：{Short(loaded)}") + (after == continuous ? string.Empty : $"\n连续：{Short(continuous)}\n读档：{Short(after)}"));

            // 旧档 / 坏记录：没有 Leaks 字段 = 没有液洼；重复编号、同一格两摊、没有流体的记录丢弃。
            var st = new CampaignState { Pipes = new PipeFluidState { Leaks = null, NextLeakSerial = 0 } };
            PipeLeakService.EnsureState(st);
            bool legacy = st.Pipes.Leaks != null && st.Pipes.Leaks.Length == 0 && st.Pipes.NextLeakSerial == 1;
            st.Pipes.Leaks = new[]
            {
                new PipeLeakRecord { Id = 5, X = 1, Y = 1, Fluid = 4 },
                new PipeLeakRecord { Id = 5, X = 2, Y = 1, Fluid = 4 },
                new PipeLeakRecord { Id = 6, X = 1, Y = 1, Fluid = 4 },
                new PipeLeakRecord { Id = 7, X = 3, Y = 1, Fluid = 0 },
                new PipeLeakRecord { Id = 0, X = 4, Y = 1, Fluid = 4 },
                null,
                new PipeLeakRecord { Id = 9, X = 5, Y = 1, Fluid = 5, State = 1, FromRadius = 1f },
            };
            PipeLeakService.EnsureState(st);
            bool cleaned = st.Pipes.Leaks.Length == 2 && st.Pipes.Leaks[0].Id == 5 && st.Pipes.Leaks[1].Id == 9 && st.Pipes.NextLeakSerial == 10;
            Expect(legacy && cleaned, $"G2 旧档（没有液洼字段）补成空域（{legacy}）；坏记录（重复编号 / 同一格两摊 / 没有流体 / 编号 0 / 空）丢弃、编号计数器跟上（{cleaned}）");

            // D-17：机制图鉴跨存档共享——新建另一局、从磁盘重读图鉴文件后，“管线泄漏与液洼”仍是解锁的。
            NewWorld(7423);
            MechanicCodex.Reload();
            bool shared = MechanicCodex.IsUnlocked("codex.logistics.leak") && File.Exists(MechanicCodex.FilePath) && PipeLeakService.Count(CampaignSession.Current) == 0;
            Expect(shared, $"G3 D-17 图鉴机制条目跨存档共享：新建另一局（没有液洼）并从磁盘重读图鉴文件后，“管线泄漏与液洼”仍解锁（{shared}）");
        }

        private static string RunTiming(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = LeakScenario(seed, observe, 1f);
            pausedHeld = true;
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Signature(s);
                for (int i = 0; i < 90; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Signature(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 30;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 600)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return Signature(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunTiming(7421, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{Short(snap)}\n参照：{Short(reference)}";
                }
                same &= snap == reference;
            }
            Expect(paused && same, "H B09 暂停中（90 帧）液洼不扩大、燃烧区不走、管线不挨烧；0.5x / 1x / 2x / 3x 跑同样的 30 游戏秒泄漏（扩散、遇火反应、连锁烧毁、重新积起）逐位一致" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunTiming(7422, true, 1f, false, out _);
            string unseen = RunTiming(7422, false, 1f, false, out _);
            Expect(seen == unseen, "I B24 / FGR-BASE-021 同一场泄漏在观察与不观察家园时跑 30 游戏秒逐位一致（远征时家园的泄漏照常扩散、起火、消退）"
                                   + (seen == unseen ? string.Empty : $"\n观察：{Short(seen)}\n不观察：{Short(unseen)}"));
        }

        private static void CheckSeedSet()
        {
            var lines = new List<string>();
            bool all = true;
            foreach (int seed in new[] { 7501, 7602 })
            {
                CampaignState s = LeakScenario(seed, true, 3f);
                bool reacted = PipeLeakService.Records(s).Any(r => r.State == PipeLeakService.StateReacted) || PipeLeakService.ReactedCount > 0;
                all &= PipeLeakService.StartedCount >= 2 && reacted;
                lines.Add($"种子 {seed}：积起 {PipeLeakService.StartedCount} 摊、反应 {PipeLeakService.ReactedCount} 次");
            }
            Expect(all, "J B25 种子测试集：两个种子的家园地形不同，管线按生成规则找空地铺设，击穿积液洼、遇火成燃烧区都成立（不依赖固定坐标）：" + string.Join("；", lines));
        }

        // ── P 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(7430);
            int cap = PipeLeakService.MaxPuddles;
            List<GridCell> a = Line(s, cap / 2, "fuel", 9f, 40f);
            List<GridCell> b = Line(s, cap / 2, "coolant", 12f, 44f);
            if (a == null || b == null)
            {
                Fail("P1 测试准备：放不下管线");
                return;
            }
            foreach (GridCell c in a.Concat(b))
            {
                Puncture(s, c);
            }
            var rnd = new System.Random(7);
            for (int i = 0; i < 100; i++)
            {
                GridCell c = (i % 2 == 0 ? a : b)[rnd.Next(cap / 2)];
                Dummy(new Vector2(c.X + (float)rnd.NextDouble() - 0.5f, c.Y + (float)rnd.NextDouble() - 0.5f), 1_000_000f);
            }
            // 一半液洼（燃油那条）点着：燃烧区烧管线，被烧穿的接着漏。
            for (int i = 0; i < a.Count; i += 4)
            {
                int burner = Dummy(new Vector2(a[i].X, a[i].Y), 1_000_000f);
                Site.Kernel.ApplyStatus(burner, Bit("Fire"), 60f, 0f, 0f, 0f, 0);
            }
            F.Seconds(2f);
            CombatSite site = Site;
            var kernel = new List<double>();
            var frame = new List<double>();
            double maxSync = 0;
            int steps = GameClock.StepHz * 10;
            for (int i = 0; i < steps; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                WorldSimulation.StepMany(1);
                frame.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                kernel.Add(site.LastKernelMs);
                maxSync = Math.Max(maxSync, PipeLeakService.LastSyncMs);
            }
            double kAvg = kernel.Average(), fAvg = frame.Average(), fMax = frame.Max();
            double limitSync = GridContent.Tuning("logistics.leak.perf.sync_ms");
            int burning = PipeLeakService.Records(s).Count(r => r.State == PipeLeakService.StateReacted);
            PerfLines.Add($"{PipeLeakService.Count(s)} 摊液洼（{burning} 摊在燃烧）+ {site.Kernel.SlotCount} 个内核单位：内核单步平均 {kAvg:F3} ms；热更层液洼对账最长 {maxSync:F3} ms（阈值 {limitSync} ms）；" +
                          $"世界一步平均 {fAvg:F3} / 最长 {fMax:F3} ms（120 帧预算 8.33 ms / 帧；Editor batchmode 下 Mono，真机另测 FG15-SYS-02）");
            PerfGate.Expect(PipeLeakService.Count(s) >= cap - 2,
                $"P1 {PipeLeakService.Count(s)} 摊液洼（{burning} 摊在燃烧）+ 100 个站在液洼里的单位：热更层液洼对账最长 {maxSync:F3} ms（阈值 {limitSync} ms）、内核单步平均 {kAvg:F3} ms（阈值 {SiegeCatalog.PerfStepMs} ms）、世界一步平均 {fAvg:F3} ms（≤ 8.33 ms）",
                new[]
                {
                    PerfGate.Le(maxSync, limitSync, "液洼对账最长 ms"),
                    PerfGate.Le(kAvg, SiegeCatalog.PerfStepMs, "液洼场景内核单步平均 ms"),
                    PerfGate.Le(fAvg, 8.33, "液洼场景世界一步平均 ms（120 帧预算）"),
                }, Expect, Line);
        }

        // ── 断言 ─────────────────────────────────────────────────────────────────

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

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
