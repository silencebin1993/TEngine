using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
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
using GameLogic.UI.Kit;
using GameLogic.View;
using Luban;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG3-LOG-01 格网建造正式化的自动验收（FG03 FGR-LOG-001～004、007、008、012、013；FG13 FGU-07 / FGU-08；FGT-LOG-001）。
    /// 全部断言走真实入口（<see cref="HomeGridService"/>、<see cref="BuildCatalog"/>、<see cref="HomeValleyBuildMode"/> 的按键 / 鼠标路径、
    /// 真 UXML 的建造菜单、真实载入的家园（<see cref="WorldSimulation.LoadHome"/>：机器真的走过去施工，传送带内核真的在跑）、真存档文件）：
    /// A 数据：两张新表与源数据逐字段一致；分类 12 类；新调参、文本键中英齐全；新动作已接入；引导钩子与图鉴条目。
    /// B 建造菜单：分类、空分类说明、跨分类搜索（名字 / 用途 / 大小写）、未解锁条件、条目写成本；快捷栏拖放 / 点选放入 / 右键清空 / 快捷键选取 / 空格子提示。
    /// C 非法原因矩阵（FGT-LOG-001）：每一种原因都给正确的原因且状态不变；只警告的轻度污染；每个原因码都有中英文本。
    /// D 拖拽铺设：自动转角、长度与成本显示、全有或全无、超长 / 缺料 / 未解锁 / 经过建筑都拒绝并写明第几格。
    /// E 拆除：拆传送带全额返还、带上有物品不拆；框选批量拆除（>20 座确认、含关键建筑确认、取消不改状态、禁拆的写原因、框指示器）。
    /// F 搬迁：已建成建筑的组合任务（机器真的走过去施工，完工后原建筑换位、设置保留）、挪动规划、取消搬迁、原建筑中途消失、出口随建筑、
    ///   搬迁中的原建筑不能拆 / 转、开局建筑可以搬、正式输入（搬迁键 + 两次点击、空闲拖拽）。
    /// G 迷雾：机器走进迷雾后周围变为已探索、可以建造；原地不动不重复记圆；每步只查 O(1) 台机器。
    /// H 格线开关：叠加层像素随开关变化；按键开关；设置持久。
    /// I 存读档：快捷栏、搬迁中的虚影（读档后照样完工）。
    /// J 暂停与 0.5x～3x：搬迁施工需要的游戏时间相同，暂停中可以规划不推进。
    /// K 观察 / 不观察一致（FGR-BASE-021）：同一组操作经建造模式（观察）与直接调服务（不观察）跑同样的游戏时间，结果逐字段一致。
    /// L 让位（FG-GAP-015）：新建筑压住的地面物与机器挪到最近的空格。
    /// M 界面：HUD 绑真 UXML 的各行文字；布局探针（中英文 × 缩放 0.8 / 1 / 1.5 × 四种分辨率）。
    /// N 性能（Editor batchmode 数字）：拖拽规划、框选规划、菜单搜索与建筑数 / 条目数的关系。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgBuildFormalSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static FakeReader _reader;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 格网建造正式化")]
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
            Line("\n[格网建造正式化] 建造菜单、拖拽、拆除、搬迁、迷雾、格线（FG3-LOG-01）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgbuild-selfcheck-" + Guid.NewGuid().ToString("N"));
            _reader = new FakeReader();
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "编辑模式下直接起真实家园（机器走路与施工在战斗内核里真跑），没有渲染帧——性能数字是 Editor 托管代码，真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckMenuAndSearch);
                Step(CheckHotbar);
                Step(CheckReasonMatrix);
                Step(CheckBeltDrag);
                Step(CheckBeltRemove);
                Step(CheckBatchDemolish);
                Step(CheckRelocationWorld);
                Step(CheckRelocationEdges);
                Step(CheckExitFollowsBuilding);
                Step(CheckExitTerrainAndBusyRelocation);
                Step(CheckFormalInput);
                Step(CheckMachineExploration);
                Step(CheckGridLines);
                Step(CheckSaveLoad);
                Step(CheckTiming);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckGiveWay);
                Step(CheckHudAndLayout);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"格网建造正式化自检抛异常：{e}");
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
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
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
            Line($"  · [格网建造正式化] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 公共准备 ─────────────────────────────────────────────────────────────

        /// <summary>只有战役状态的家园（经家园控制器的真实播种入口），不载入地点：服务层断言用。</summary>
        private static CampaignState NewHome(int seed, bool withHauler = true)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            CampaignState s = CampaignState.CreateNew("fgbuild-" + seed, "Standard", seed);
            MethodInfo seedMethod = typeof(HomeValleyController).GetMethod("EnsureRegionSeeded", BindingFlags.NonPublic | BindingFlags.Static);
            seedMethod.Invoke(null, new object[] { s });
            s.Scrap = 500;
            if (withHauler)
            {
                MachineRegistry.SpawnMachine(HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, HomeValleyLayout.RegionId,
                    new Vector2(-10f, -6f), 100f, 100f);
            }
            CampaignSession.Set(0, s);
            return s;
        }

        /// <summary>真实载入家园（战斗内核、传送带内核、机器句柄都在）；<paramref name="observe"/> 决定镜头是否在家园。</summary>
        private static CampaignState NewWorld(int seed, bool observe, out HomeValleyController home)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            CampaignState s = CampaignState.CreateNew("fgbuildw-" + seed, "Standard", seed);
            CampaignSession.Set(0, s);
            home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = 280;
            return s;
        }

        private static bool StepUntil(Func<bool> done, int maxGameSeconds)
        {
            for (int i = 0; i < maxGameSeconds * 4; i++)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(GameClock.StepHz / 4);
            }
            return done();
        }

        private static void TickOrders(CampaignState s, float seconds, float dt = 0.1f)
        {
            int steps = Mathf.CeilToInt(seconds / dt);
            for (int i = 0; i < steps; i++)
            {
                HomeValleyWorkOrders.MarkAssignmentDirty();
                HomeValleyWorkOrders.Tick(s, dt, _ => null, _ => { }, _ => false,
                    order => HomeValleyWorkOrders.OnArrivedAtWork(s, order.WorkOrderId));
            }
        }

        private static GridCell? FindValid(CampaignState s, string typeId, GridCell from, int radius, int rotation = 0, bool asPlayer = true, string ignore = null)
        {
            for (int r = 0; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(from.X + dx, from.Y + dy);
                        if (HomeGridService.ValidatePlacement(s, typeId, c, rotation, asPlayerPlacement: asPlayer, ignoreBuildingId: ignore, checkCost: asPlayer).Ok)
                        {
                            return c;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>从 <paramref name="from"/> 往 +x 方向找一条 <paramref name="length"/> 格都能铺传送带的直线起点。</summary>
        private static GridCell? FindBeltRow(CampaignState s, GridCell from, int length, int radius)
        {
            for (int r = 0; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var start = new GridCell(from.X + dx, from.Y + dy);
                        bool ok = true;
                        for (int k = 0; k < length && ok; k++)
                        {
                            ok = HomeGridService.ValidateBeltCell(s, new GridCell(start.X + k, start.Y)).Ok
                                 && HomeGridService.ValidateBeltCell(s, new GridCell(start.X + k, start.Y + 1)).Ok;
                        }
                        if (ok)
                        {
                            return start;
                        }
                    }
                }
            }
            return null;
        }

        private static string BuildingsJson(CampaignState s) =>
            string.Join("\n", (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).OrderBy(b => b.BuildingId, StringComparer.Ordinal)
                .Select(b => $"{b.BuildingId}|{b.BuildingTypeId}|{b.GridX},{b.GridY}|{b.Rotation}|{b.Position.x:F2},{b.Position.y:F2}|{b.ConstructionState}|{b.Health:F1}|{b.PowerPriority}|{b.RelocateFromId}"));

        private static bool SnapshotMatches(CampaignState s)
        {
            Dictionary<GridCell, string> cache = HomeGridService.MapFor(s).SnapshotOccupancy();
            Dictionary<GridCell, string> fresh = HomeGridService.RebuildSnapshotFromRecords(s);
            return cache.Count == fresh.Count && fresh.All(kv => cache.TryGetValue(kv.Key, out string v) && v == kv.Value);
        }

        private static BuildingRecord Rec(CampaignState s, string local) => HomeGridService.FindBuilding(s, HomeValleyLayout.RegionId + ":" + local);

        private static HomeValleyBuildMode NewMode()
        {
            var mode = new HomeValleyBuildMode();
            HomeValleyBuildMode.Bind(mode);
            return mode;
        }

        private static void EndMode(HomeValleyBuildMode mode)
        {
            mode.Shutdown();
            HomeValleyBuildMode.Unbind(mode);
            UiEscapeStack.Clear();
            UiConfirmDialog.DiscardAll();
        }

        // ── A. 数据 ──────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Expect(GridContent.LoadError == null && GridContent.Categories.Count == 12 && GridContent.Tools.Count >= 1,
                $"建造菜单两张新表已加载：分类 {GridContent.Categories.Count}（FGR-LOG-002 十二类）、工具 {GridContent.Tools.Count}");
            (int code, string output) = RunPython(LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            var diffs = new List<string>();
            int rows = 0;
            if (code != 0)
            {
                Fail("fgdata.py --dump 失败：" + Tail(output));
                return;
            }
            foreach (string raw in output.Replace("\r", string.Empty).Split('\n'))
            {
                string[] f = raw.Split('\t');
                if (f.Length < 2)
                {
                    continue;
                }
                if (f[0] == "BC")
                {
                    rows++;
                    if (!GridContent.TryGetCategory(f[1], out BuildCategory c) || c.NameKey != f[2] || c.SortOrder != int.Parse(f[3]))
                    {
                        diffs.Add("分类 " + f[1]);
                    }
                }
                else if (f[0] == "BT")
                {
                    rows++;
                    if (!GridContent.TryGetTool(f[1], out BuildTool t) || t.Kind != f[2] || t.Tier != int.Parse(f[3]) || t.Category != f[4] || t.NameKey != f[5]
                        || t.DescKey != f[6] || t.ScrapPerCell != int.Parse(f[7]) || t.UnlockRule != f[8] || t.UnlockHintKey != f[9] || t.SortOrder != int.Parse(f[10]))
                    {
                        diffs.Add("工具 " + f[1]);
                    }
                }
            }
            Expect(diffs.Count == 0 && rows == GridContent.Categories.Count + GridContent.Tools.Count,
                $"源数据 fgdata_build.py 与运行时 fg.TbBuildCategory / fg.TbBuildTool 逐字段一致（{rows} 行）{string.Join("，", diffs)}");
            var badCat = GridContent.Buildings.Where(b => !GridContent.TryGetCategory(b.Category, out _)).Select(b => b.TypeId).ToList();
            Expect(badCat.Count == 0, $"每座建筑都有合法的建造菜单分类（缺：{string.Join(",", badCat)}）");

            string[] tunings = { "grid.relocate_seconds", "grid.batch_demolish_confirm", "grid.pollution_warn_level", "grid.drag_max_cells",
                "grid.explore_machine_radius", "grid.explore_lattice", "grid.explore_machines_per_step" };
            var missingTuning = tunings.Where(t => !GridContent.TryGetTuning(t, out float v) || v <= 0f).ToList();
            Expect(missingTuning.Count == 0 && GridContent.TuningInt("grid.batch_demolish_confirm") == 20 && Mathf.Approximately(GridContent.Tuning("grid.relocate_seconds"), 20f),
                $"新调参入表（批量拆除确认阈值 20、搬迁 20 秒……）{string.Join(",", missingTuning)}");

            // FG-TOOL-01：同一轮全量自检里 check_luban 自测只真跑一次（输入 = tools/cell_tables 全部文件，指纹相同才复用），各段在同一份输出里核对自己的规则。
            (code, output) = QaPython.RunShared(LocateRepo(), "tools/cell_tables/check_luban.py --selftest", out bool selftestReused);
            if (selftestReused)
            {
                Line($"    · check_luban 自测：本轮前面的段已真跑过、输入指纹相同，复用同一次的输出（本轮真跑 {QaPython.SharedRuns} 次、复用 {QaPython.SharedHits} 次）");
            }
            Expect(code == 0 && output.Contains("建筑分类拼错") && output.Contains("出口没写所属建筑") && output.Contains("工具每格废料为 0"),
                $"check_luban R32（建造菜单）规则自测真跑：{Tail(output)}");

            var keys = new[] { "build.category.logistics", "build.category.endgame", "ui.build.search_placeholder", "ui.build.drag_info", "ui.build.box_info",
                "ui.build.batch_confirm_title", "ui.build.relocate_planned", "ui.hotbar.slot_empty", "grid.reason.exit_blocked", "grid.reason.relocating",
                "grid.warn.pollution", "ui.build.grid_on", "ui.build.undo_hint", "codex.build.grid.body", "input.action.relocate_mode.name",
                "input.action.toggle_grid_lines.name", "input.action.hotbar10.name", "ui.build.relocated_done", "ui.build.relocate_order" };
            var badKeys = keys.Where(k => !GameText.TryGet(k, GameLanguage.ZhCn, out string zh) || string.IsNullOrEmpty(zh)
                                          || !GameText.TryGet(k, GameLanguage.En, out string en) || string.IsNullOrEmpty(en)).ToList();
            Expect(badKeys.Count == 0, $"新文本键中英两列齐全（抽查 {keys.Length} 条）{string.Join(",", badKeys)}");

            var actions = new[] { GameActionId.Hotbar1, GameActionId.Hotbar10, GameActionId.ToggleGridLines, GameActionId.RelocateMode };
            bool wired = actions.All(a => InputActionCatalog.TryGet(a, out InputActionDef d) && d.Status == InputActionStatus.Wired);
            bool keysOk = GameSettings.KeyBindings.GetKey(GameActionId.Hotbar1) == KeyCode.F1 && GameSettings.KeyBindings.GetKey(GameActionId.ToggleGridLines) == KeyCode.G
                          && GameSettings.KeyBindings.GetKey(GameActionId.RelocateMode) == KeyCode.E;
            Expect(wired && keysOk, "快捷栏 1～10（F1～F10）、格线开关（G）、搬迁（E）已接入玩法（wired），可重绑");

            string[] hooks = { GuidanceHooks.BuildFirstRelocate, GuidanceHooks.BuildFirstBatchDemolish, GuidanceHooks.BuildFirstHotbar, GuidanceHooks.BuildFirstDrag };
            Expect(hooks.All(h => GuidanceHooks.Known.Contains(h)) && MechanicCodex.Find("codex.build.grid") != null,
                "引导钩子（首次搬迁 / 批量拆除 / 放快捷栏 / 拖拽）已登记；图鉴有“格网建造”条目（FG00 B14）");
        }

        // ── B. 建造菜单与快捷栏 ─────────────────────────────────────────────────────

        private static void CheckMenuAndSearch()
        {
            CampaignState s = NewHome(3101);
            var list = new List<BuildEntry>();
            BuildCatalog.List("logistics", null, list);
            // FG3-LOG-03：物流里有三级传送带，T1 排第一。FG3-LOG-04：其后是分流器、合流器与三级地下传送带（共 8 项，全是工具）。
            // FG3-LOG-05：+ 管线 T1 / T2、泵、储罐、阀门。FG4-ECO-04（FG-GAP-082）：+ 地下管线 T1 / T2。
            string[] logisticsOrder = { "belt_t1", "belt_t2", "belt_t3", "splitter", "merger", "underground_t1", "underground_t2", "underground_t3", "pipe_t1", "pipe_t2", "pump", "tank", "valve",
                "pipe_underground_t1", "pipe_underground_t2" };
            bool logistics = list.Select(x => x.Id).SequenceEqual(logisticsOrder) && list.All(x => x.IsTool);
            string logisticsIds = string.Join(",", list.Select(x => x.Id));
            BuildCatalog.List("energy", null, list);
            bool energy = list.Any(e => e.Id == "generator_2");
            BuildCatalog.List("signal", null, list);
            bool signal = list.Any(e => e.Id == "signal_relay");
            BuildCatalog.List("defense", null, list);
            bool emptyCat = list.Count == 0;
            Expect(logistics && energy && signal && emptyCat && BuildCatalog.FirstNonEmptyCategory() == "logistics",
                $"分类：物流里依次有传送带 T1 / T2 / T3、分流器、合流器、地下传送带 T1 / T2 / T3、管线 T1 / T2、泵、储罐、阀门、地下管线 T1 / T2（实际 {logisticsIds}）、能源里有发电机、信号里有信号中继塔；防御类暂时为空；默认打开第一个有条目的分类");

            BuildCatalog.List("defense", "中继", list);
            bool byName = list.Count == 1 && list[0].Id == "signal_relay";
            BuildCatalog.List("logistics", "额外电力", list);
            bool byUse = list.Count >= 1 && list.All(e => e.Id == "generator_2");
            BuildCatalog.List("logistics", "  不存在的东西 ", list);
            bool none = list.Count == 0;
            GameSettings.SetLanguage(GameLanguage.En);
            BuildCatalog.List("logistics", "RELAY", list);
            bool english = list.Count == 1 && list[0].Id == "signal_relay";
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(byName && byUse && none && english,
                "搜索跨分类：按名字（“中继”→信号中继塔）、按用途（“额外电力”→发电机）、查不到给空结果；英文界面不区分大小写（“RELAY”）");

            Expect(BuildCatalog.TryGet("beacon", out BuildEntry beacon) && !BuildCatalog.IsUnlocked(s, beacon)
                   && GameText.Get(beacon.UnlockHintKey).Contains("铸造前哨"),
                $"未解锁的建筑说明解锁条件（信标：“{GameText.Get(beacon?.UnlockHintKey ?? "grid.unlock.beacon")}”）");
        }

        private static void CheckHotbar()
        {
            CampaignState s = NewHome(3201);
            HomeValleyBuildMode mode = NewMode();
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            InputRouter.SetGameplayPaused(false);
            GameObject go = null;
            try
            {
                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/UiKit/BuildModeHud.uxml");
                VisualElement root = vta.CloneTree();
                go = new GameObject("__fgbuild_hotbar") { hideFlags = HideFlags.HideAndDontSave };
                BuildModeHudUIToolkit hud = go.AddComponent<BuildModeHudUIToolkit>();
                hud.BindView(root);
                mode.Open();
                hud.Refresh();

                bool dragged = hud.DragEntryToSlot("signal_relay", 0);
                hud.Refresh();
                bool slot0 = BuildCatalog.HotbarId(s, 0) == "signal_relay" && hud.HotbarSlotText(0).Contains("信号中继塔") && hud.HotbarSlotText(0).Contains("F1");
                Expect(dragged && slot0 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildFirstHotbar),
                    $"把建造菜单条目拖到快捷栏第 1 格：存进存档，格子写“{hud.HotbarSlotText(0).Replace("\n", " ")}”；发出“首次放快捷栏”钩子");

                mode.Select("belt_t1");
                hud.ClickSlot(1);
                hud.Refresh();
                bool clickAssign = BuildCatalog.HotbarId(s, 1) == "belt_t1";
                mode.ClearSelection();
                hud.ClickSlot(0);
                bool pick = mode.SelectedTypeId == "signal_relay";
                Expect(clickAssign && pick, "选中传送带后点空的快捷栏格子 = 放进去；点有东西的格子 = 选取它（信号中继塔）");

                mode.Close();
                reader.Press(KeyCode.F2);
                mode.Tick(null, s, true);
                Frame(reader);
                bool keyOpens = mode.IsOpen && mode.SelectedToolId == "belt_t1";
                reader.Press(KeyCode.F5);
                mode.Tick(null, s, true);
                Frame(reader);
                bool emptyHint = mode.StatusIsError && mode.StatusText.Contains("快捷栏 5 是空的");
                Expect(keyOpens && emptyHint, $"建造模式关着时按 F2：打开并选中快捷栏里的传送带；按空格子 F5 给提示“{mode.StatusText}”");

                hud.ClearSlot(0);
                bool cleared = BuildCatalog.HotbarId(s, 0) == null && mode.StatusText.Contains("已清空快捷栏 1");
                bool bogus = !BuildCatalog.TrySetHotbar(s, 3, "no_such_entry") && !BuildCatalog.TrySetHotbar(s, 10, "belt_t1") && BuildCatalog.HotbarId(s, 3) == null;
                Expect(cleared && bogus, "右键清空快捷栏格子；不存在的条目 / 越界格子被拒绝，状态不变");
            }
            finally
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
                EndMode(mode);
                InputRouter.DebugSetReader(null);
            }
        }

        // ── C. 非法原因矩阵（FGT-LOG-001）──────────────────────────────────────────────

        private static void AssertRejected(CampaignState s, Func<GridOpResult> op, GridBlockReason code, string what, string textContains)
        {
            string before = BuildingsJson(s);
            int scrap = s.Scrap;
            int orders = s.WorkOrders?.Length ?? 0;
            GridOpResult r = op();
            string text = r.Describe();
            Expect(!r.Success && r.FirstReason == code && text.Contains(textContains) && BuildingsJson(s) == before && s.Scrap == scrap
                   && (s.WorkOrders?.Length ?? 0) == orders,
                $"{what}：拒绝，原因“{text}”（{r.FirstReason}），建筑 / 废料 / 工单都不变");
        }

        private static void CheckReasonMatrix()
        {
            CampaignState s = NewHome(3301);
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell spot = FindValid(s, "generator_2", new GridCell(core.X - 8, core.Y + 26), 10) ?? new GridCell(0, 30);
            HomeGridService.TryPlace(s, "generator_2", spot, 0);

            AssertRejected(s, () => HomeGridService.TryPlace(s, "generator_2", spot, 0), GridBlockReason.Occupied, "放在已有建筑上", "已被“发电机”占用");
            AssertRejected(s, () => HomeGridService.TryPlace(s, "generator_2", new GridCell(core.X, core.Y + 4), 0), GridBlockReason.CoreReserve, "放在核心通道上", "通道");
            AssertRejected(s, () => HomeGridService.TryPlace(s, "generator_2", new GridCell(core.X + 90, core.Y), 0), GridBlockReason.Fog, "放在迷雾里", "超出已探索区域");
            AssertRejected(s, () => HomeGridService.TryPlace(s, "warehouse", new GridCell(core.X - 20, core.Y + 26), 0), GridBlockReason.NotPlaceable, "放不能手动放置的建筑", "不能手动放置");
            AssertRejected(s, () => HomeGridService.TryPlace(s, "beacon", new GridCell(core.X - 20, core.Y + 26), 0), GridBlockReason.Locked, "放未解锁的建筑", "尚未解锁");
            GridCell wreck = HomeGridService.AnchorCell(s, HomeValleyLayout.Wreckage1NodeId);
            AssertRejected(s, () => HomeGridService.TryPlace(s, "generator_2", wreck, 0), GridBlockReason.Obstacle, "压在残骸上", "残骸");
            GridCell exit = HomeGridService.ExitCell(s, "assembly_exit");
            AssertRejected(s, () => HomeGridService.TryPlace(s, "generator_2", exit, 0), GridBlockReason.Obstacle, "压在装配站出口上", "出口通道");
            AssertRejected(s, () => HomeGridService.TryPlace(s, "generator_2", new GridCell(20_000_000, 0), 0), GridBlockReason.WorldLimit, "超出世界坐标上限", "超出世界范围");
            GridCell? cliff = FindTerrainCell(s, "cliff", 40);
            if (cliff != null)
            {
                string before = BuildingsJson(s);
                GridOpResult onCliff = HomeGridService.TryPlace(s, "generator_2", cliff.Value, 0);
                Expect(!onCliff.Success && onCliff.Placement != null && onCliff.Placement.Has(GridBlockReason.Terrain) && onCliff.Describe().Contains("悬崖")
                       && BuildingsJson(s) == before,
                    $"放在悬崖上：拒绝，原因“{onCliff.Describe()}”，建筑不变");
            }
            else
            {
                Fail("探索区里找不到悬崖格：地形不符原因没有覆盖到");
            }

            // 污染：2 级及以上不能建（注入真实格网层），1 级只警告。
            GridCell? clean = FindValid(s, "generator_2", new GridCell(core.X + 20, core.Y + 26), 10);
            if (clean != null)
            {
                map.SetPollution(clean.Value, 2);
                AssertRejected(s, () => HomeGridService.TryPlace(s, "generator_2", clean.Value, 0), GridBlockReason.Pollution, "放在 2 级污染上", "污染过重");
                map.SetPollution(clean.Value, 1);
                GridPlacementResult warn = HomeGridService.ValidatePlacement(s, "generator_2", clean.Value, 0);
                Expect(warn.Ok && warn.Warnings.Count == 1 && warn.Warnings[0].Contains("天气损伤加倍"),
                    $"放在 1 级污染上：允许，只警告“{string.Join("；", warn.Warnings)}”（FGR-LOG-012）");
                map.SetPollution(clean.Value, 0);
            }
            else
            {
                Fail("找不到干净的空地做污染断言");
            }

            s.Scrap = 5;
            GridCell spot2 = FindValid(s, "generator_2", new GridCell(core.X + 20, core.Y + 26), 10, asPlayer: false) ?? new GridCell(10, 30);
            // FG3-LOG-02（FGR-LOG-003“不足也允许放置虚影”）：废料不够不再是非法原因——放下虚影、只警告还差多少，库存不变。
            GridOpResult shortPlace = HomeGridService.TryPlace(s, "generator_2", spot2, 0);
            bool shortOk = shortPlace.Success && s.Scrap == 5 && shortPlace.Placement != null && shortPlace.Placement.Warnings.Any(w => w.Contains("还差 55"));
            Expect(shortOk, $"废料不够（5 < 60）：照样放下虚影、库存不变，只警告“{shortPlace.Placement?.Warnings.FirstOrDefault()}”");
            if (shortPlace.Success)
            {
                HomeGridService.TryToggleDemolish(s, shortPlace.BuildingId); // 取消规划，后面不受影响
            }
            s.Scrap = 500;

            // 每个原因码都有中英文本。
            var codes = (GridBlockReason[])Enum.GetValues(typeof(GridBlockReason));
            var withKey = new Dictionary<GridBlockReason, string>
            {
                [GridBlockReason.Locked] = "grid.reason.locked", [GridBlockReason.MaxCount] = "grid.reason.max_count", [GridBlockReason.CoreReserve] = "grid.reason.core_reserve",
                [GridBlockReason.Terrain] = "grid.reason.terrain", [GridBlockReason.NeedsTerrain] = "grid.reason.needs_terrain", [GridBlockReason.Pollution] = "grid.reason.pollution",
                [GridBlockReason.Occupied] = "grid.reason.occupied", [GridBlockReason.Obstacle] = "grid.reason.obstacle", [GridBlockReason.InsufficientScrap] = "grid.reason.insufficient_scrap",
                [GridBlockReason.WorldLimit] = "grid.reason.world_limit", [GridBlockReason.ExitBlocked] = "grid.reason.exit_blocked", [GridBlockReason.ExitTerrain] = "grid.reason.exit_terrain",
                [GridBlockReason.DragTooLong] = "grid.reason.drag_too_long", [GridBlockReason.BeltHasItems] = "grid.reason.belt_has_items", [GridBlockReason.ToolLocked] = "grid.reason.not_unlocked_tool",
                // FG3-LOG-04：跨度超限、地下同向重叠带参数，由 HomeGridService.CheckUndergroundShape 构造（行为断言在 FgBeltNodeSelfCheck F2）。
                [GridBlockReason.UndergroundSpan] = "grid.reason.under_too_far", [GridBlockReason.UndergroundOccupied] = "grid.reason.under_occupied",
            };
            var missing = new List<string>();
            foreach (GridBlockReason c in codes)
            {
                if (c == GridBlockReason.None)
                {
                    continue;
                }
                string key = withKey.TryGetValue(c, out string k) ? k : GridReason.Of(c).TextKey;
                if (key == "grid.reason.unknown_type" && c != GridBlockReason.UnknownType
                    || !GameText.TryGet(key, GameLanguage.ZhCn, out string zh) || string.IsNullOrEmpty(zh) || !GameText.TryGet(key, GameLanguage.En, out string en) || string.IsNullOrEmpty(en))
                {
                    missing.Add(c.ToString());
                }
            }
            Expect(missing.Count == 0, $"全部 {codes.Length - 1} 个原因码都有稳定的中英文本键（FG00 B06）{string.Join(",", missing)}");
        }

        private static GridCell? FindTerrainCell(CampaignState s, string terrainId, int radius)
        {
            byte code = GridContent.TerrainCode(terrainId);
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell core = HomeGridService.CorePivot(s);
            for (int y = -radius; y <= radius; y++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    var c = new GridCell(core.X + x, core.Y + y);
                    if (map.GetTerrain(c) == code && map.OccupantAt(c) == null && AllExplored(map, c))
                    {
                        return c;
                    }
                }
            }
            return null;
        }

        private static bool AllExplored(HomeGridMap map, GridCell c)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!map.IsExplored(new GridCell(c.X + dx, c.Y + dy)))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        // ── D. 拖拽铺设传送带 ────────────────────────────────────────────────────────

        private static void CheckBeltDrag()
        {
            CampaignState s = NewWorld(3401, observe: false, out _);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? rowStart = FindBeltRow(s, new GridCell(core.X - 12, core.Y + 26), 8, 12);
            if (rowStart == null || !BeltNetworkService.IsRunning)
            {
                Fail($"找不到能铺 8 格的空地或传送带内核没运行（{BeltNetworkService.IsRunning}）");
                return;
            }
            GridCell a = rowStart.Value;
            GridCell b = new GridCell(a.X + 5, a.Y + 1);
            HomeValleyBuildMode mode = NewMode();
            try
            {
                mode.Open();
                mode.Select("belt_t1");
                int scrap0 = s.Scrap;
                mode.PointerDown(s, a);
                mode.SetHover(s, b);
                BeltPathPlan plan = mode.BeltPlan;
                bool corner = plan != null && plan.Length == 7 && plan.Cells[5] == new GridCell(a.X + 5, a.Y) && plan.Dirs[0] == BeltDir.East
                              && plan.Dirs[5] == BeltDir.North && plan.Dirs[6] == BeltDir.North;
                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/UiKit/BuildModeHud.uxml");
                var go = new GameObject("__fgbuild_drag") { hideFlags = HideFlags.HideAndDontSave };
                string info;
                try
                {
                    BuildModeHudUIToolkit hud = go.AddComponent<BuildModeHudUIToolkit>();
                    hud.BindView(vta.CloneTree());
                    hud.Refresh();
                    info = hud.DragInfoText;
                }
                finally
                {
                    Object.DestroyImmediate(go);
                }
                mode.RefreshVisuals(s);
                bool tiles = mode.ActiveTileCount == 7;
                Expect(corner && plan.Ok && info.Contains("长度 7 格") && info.Contains("成本 7 废料") && tiles,
                    $"按住左键从 {a} 拖到 {b}：先走长的一边再转角（第 6 格朝北），7 格逐格预览，HUD 显示“{info}”");
                List<GridCell> pathCells = plan.Cells.ToList();
                mode.PointerUp(s, b);
                // FG3-LOG-02（DEBT-FG3LOG01-01）：松开后放下的是 7 格传送带虚影（规划），不扣材料；机器取料后按路径一格一格建成。
                bool placed = pathCells.All(c => HomeValleyConstruction.TryFindPlannedCell(s, c, out _, out _) && !BeltNetworkService.Kernel.HasCell(c.X, c.Y))
                              && s.Scrap == scrap0 && mode.LastResult.Outcome == GridOpResult.Kind.BeltsPlaced && mode.StatusText.Contains("7 格");
                Expect(placed && mode.BeltPlan == null && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildFirstDrag),
                    $"松开：7 格一次放下虚影（不扣材料，{scrap0}→{s.Scrap}），状态行“{mode.StatusText}”；发出“首次拖拽”钩子");
                bool built = StepUntil(() => pathCells.All(c => BeltNetworkService.Kernel.HasCell(c.X, c.Y)), 240);
                Expect(built && s.Scrap == scrap0 - 7 && !pathCells.Any(c => HomeValleyConstruction.TryFindPlannedCell(s, c, out _, out _)),
                    $"机器取料施工：7 格全部建成进传送带内核，共扣 7 废料（{scrap0}→{s.Scrap}）");

                // 全有或全无：路径穿过刚铺好的带 → 一格都不铺，写明第几格。
                int cells0 = BeltNetworkService.Kernel.CellCount;
                int scrap1 = s.Scrap;
                GridOpResult cross = HomeGridService.TryPlaceBeltPath(s, "belt_t1", new GridCell(a.X + 2, a.Y - 2), new GridCell(a.X + 2, a.Y + 3), BeltDir.North);
                BeltPathPlan crossPlan = HomeGridService.PlanBeltPath(s, "belt_t1", new GridCell(a.X + 2, a.Y - 2), new GridCell(a.X + 2, a.Y + 3), BeltDir.North);
                Expect(!cross.Success && BeltNetworkService.Kernel.CellCount == cells0 && s.Scrap == scrap1 && crossPlan.Describe().Contains("第 3 格")
                       && crossPlan.Describe().Contains("传送带"),
                    $"路径经过已有传送带：一格都不铺、不扣废料，原因“{crossPlan.Describe()}”");

                s.Scrap = 3;
                BeltPathPlan poor = HomeGridService.PlanBeltPath(s, "belt_t1", new GridCell(a.X, a.Y + 3), new GridCell(a.X + 5, a.Y + 3), BeltDir.East);
                int plannedBefore = HomeValleyConstruction.PlannedCellCount(s);
                GridOpResult poorR = HomeGridService.TryPlaceBeltPath(s, "belt_t1", new GridCell(a.X, a.Y + 3), new GridCell(a.X + 5, a.Y + 3), BeltDir.East);
                Expect(poor.Ok && poorR.Success && BeltNetworkService.Kernel.CellCount == cells0 && s.Scrap == 3 && poor.TotalCost == 6 && poor.Stock == 3
                       && HomeValleyConstruction.PlannedCellCount(s) == plannedBefore + 6,
                    "缺料（需要 6，库存 3）：FG3-LOG-02 起照样放下 6 格虚影（HUD 写还差 3），不扣材料、内核不变");
                HomeGridService.TryRemoveBelts(s, Enumerable.Range(0, 6).Select(k => new GridCell(a.X + k, a.Y + 3)).ToList()); // 取消这份规划
                s.Scrap = 500;

                // 污染豁免（FGR-LOG-012 / FGR-ENV-040 原文“2 级及以上不能建造（净化塔和传送带除外）”）：
                // 2 级、3 级（危害带）污染格上照样拖拽铺带；同一格放建筑仍被“污染过重”拦下。
                GridCell? dirtyRow = FindBeltRow(s, new GridCell(core.X + 10, core.Y - 26), 6, 14);
                if (dirtyRow == null)
                {
                    Fail("找不到能铺 6 格的空地做传送带污染豁免断言");
                }
                else
                {
                    HomeGridMap pmap = HomeGridService.MapFor(s);
                    List<GridCell> dirty = Enumerable.Range(0, 6).Select(k => new GridCell(dirtyRow.Value.X + k, dirtyRow.Value.Y)).ToList();
                    List<byte> cleanLevels = dirty.Select(c => pmap.GetPollution(c)).ToList();
                    for (int k = 0; k < dirty.Count; k++)
                    {
                        pmap.SetPollution(dirty[k], (byte)(k < 3 ? 2 : 3));
                    }
                    GridPlacementResult oneCell = HomeGridService.ValidateBeltCell(s, dirty[4]);
                    GridPlacementResult building = HomeGridService.ValidatePlacement(s, "generator_2", dirty[4], 0);
                    BeltPathPlan dirtyPlan = HomeGridService.PlanBeltPath(s, "belt_t1", dirty[0], dirty[5], BeltDir.East);
                    int cellsP = BeltNetworkService.Kernel.CellCount;
                    int scrapP = s.Scrap;
                    GridOpResult laid = HomeGridService.TryPlaceBeltPath(s, "belt_t1", dirty[0], dirty[5], BeltDir.East);
                    bool allLaid = laid.Success && dirty.All(c => HomeValleyConstruction.TryFindPlannedCell(s, c, out _, out _))
                                   && BeltNetworkService.Kernel.CellCount == cellsP && s.Scrap == scrapP; // FG3-LOG-02：放下的是虚影，施工后才进内核
                    Expect(oneCell.Ok && !oneCell.Has(GridBlockReason.Pollution) && oneCell.Warnings.Count == 0 && dirtyPlan.Ok
                           && building.Has(GridBlockReason.Pollution) && allLaid,
                        $"传送带污染豁免：3 格 2 级 + 3 格 3 级污染上一笔铺下 6 格（{laid.Outcome}，扣 6 废料），单格校验无污染原因；同一格放发电机仍被拒（“{building.Describe()}”）（FGR-LOG-012）");
                    for (int k = 0; k < dirty.Count; k++)
                    {
                        pmap.SetPollution(dirty[k], cleanLevels[k]);
                    }
                }

                int max = GridContent.TuningInt("grid.drag_max_cells");
                BeltPathPlan longPlan = HomeGridService.PlanBeltPath(s, "belt_t1", a, new GridCell(a.X + max + 10, a.Y), BeltDir.East);
                Expect(!longPlan.Ok && longPlan.Reason.Value.Code == GridBlockReason.DragTooLong && longPlan.Describe().Contains(max.ToString()),
                    $"一笔超过 {max} 格：拒绝（“{longPlan.Describe()}”）");

                BeltPathPlan fog = HomeGridService.PlanBeltPath(s, "belt_t1", new GridCell(core.X + 80, core.Y), new GridCell(core.X + 84, core.Y), BeltDir.East);
                Expect(!fog.Ok && fog.Reason.Value.Code == GridBlockReason.Fog && fog.Describe().Contains("第 1 格"), $"铺进迷雾：拒绝（“{fog.Describe()}”）");

                // 单击 = 一格，方向按旋转键。
                GridCell single = new GridCell(a.X, a.Y + 4);
                mode.Select("belt_t1");
                mode.RotateGhost(); // 0 → 90（朝东）
                mode.PointerDown(s, single);
                mode.PointerUp(s, single);
                bool oneBuilt = StepUntil(() => BeltNetworkService.Kernel.HasCell(single.X, single.Y), 240);
                bool one = oneBuilt && BeltNetworkService.Kernel.TryGetCellInfo(single.X, single.Y, out BeltCellInfo info1) && info1.Dir == BeltDir.East;
                Expect(one, $"选中传送带单击一格：放一格虚影，机器建成后方向按旋转键（朝东）");

                // 未解锁的工具：注入一张 beacon 解锁规则的工具表。
                var tools = new TbBuildTool(ToolBuf(("belt_t1", "belt", 0, "logistics", "logistics.tier.t1", "build.tool.belt_t1.desc", 1, "beacon", "grid.unlock.beacon", 10)));
                GridContent.OverrideForTests(tools: tools);
                BeltPathPlan locked = HomeGridService.PlanBeltPath(s, "belt_t1", new GridCell(a.X, a.Y + 5), new GridCell(a.X + 2, a.Y + 5), BeltDir.East);
                GridContent.ResetForTests();
                Expect(!locked.Ok && locked.Reason.Value.Code == GridBlockReason.ToolLocked && locked.Describe().Contains("铸造前哨"),
                    $"未解锁的工具：拒绝并写明解锁条件（“{locked.Describe()}”）；改表 → 行为跟着变");
            }
            finally
            {
                EndMode(mode);
                GridContent.ResetForTests();
            }
        }

        private static ByteBuf ToolBuf(params (string id, string kind, int tier, string cat, string name, string desc, int cost, string rule, string hint, int order)[] rows)
        {
            var buf = new ByteBuf();
            buf.WriteSize(rows.Length);
            foreach (var r in rows)
            {
                buf.WriteString(r.id);
                buf.WriteString(r.kind);
                buf.WriteInt(r.tier);
                buf.WriteString(r.cat);
                buf.WriteString(r.name);
                buf.WriteString(r.desc);
                buf.WriteInt(r.cost);
                buf.WriteString(r.rule);
                buf.WriteString(r.hint);
                buf.WriteInt(r.order);
            }
            return buf;
        }

        // ── E. 拆除 ───────────────────────────────────────────────────────────────

        private static void CheckBeltRemove()
        {
            CampaignState s = NewWorld(3501, observe: false, out _);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? rowStart = FindBeltRow(s, new GridCell(core.X - 12, core.Y + 26), 6, 12);
            if (rowStart == null)
            {
                Fail("找不到铺传送带的空地");
                return;
            }
            GridCell a = rowStart.Value;
            HomeGridService.TryPlaceBeltPath(s, "belt_t1", a, new GridCell(a.X + 5, a.Y), BeltDir.East);
            StepUntil(() => Enumerable.Range(0, 6).All(k => BeltNetworkService.Kernel.HasCell(a.X + k, a.Y)), 240); // FG3-LOG-02：机器施工建成
            WorldSimulation.StepMany(2);
            s.Scrap = 50; // 开局仓库受损，只有核心缓存 180：留出空位，返还才能直接入库（满仓的情况下面单独断言）。
            int scrap = s.Scrap;
            var cells = new List<GridCell> { a, new GridCell(a.X + 1, a.Y) };
            GridOpResult r = HomeGridService.TryRemoveBelts(s, cells);
            bool removed = r.Outcome == GridOpResult.Kind.BeltsRemoved && !BeltNetworkService.Kernel.HasCell(a.X, a.Y)
                           && HomeGridService.MapFor(s).GetBelt(a) == 0 && s.Scrap == scrap + 2;
            Expect(removed, $"拆 2 格空传送带：格网传送带层清空，全额返还 2 废料（{scrap}→{s.Scrap}，FGR-LOG-007 100%）");

            GridCell busy = new GridCell(a.X + 3, a.Y);
            // FG3-LOG-03 起物品 1 = 废料（logistics.item.scrap_id），拆除时回到仓库库存；这里要验证“仓库存不了的物品按种类落地”，改用物品 7。
            BeltResult ins = BeltNetworkService.Kernel.InsertItem(busy.X, busy.Y, 7);
            BeltNetworkService.Kernel.TryGetCellInfo(busy.X, busy.Y, out BeltCellInfo busyInfo);
            int onBelt = busyInfo.Count;
            int scrap2 = s.Scrap;
            GridOpResult withItems = HomeGridService.TryRemoveBelts(s, new List<GridCell> { busy });
            int itemGround = (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == HomeGridService.BeltItemResource(7)).Sum(g => g.Amount);
            bool returned = ins == BeltResult.Ok && onBelt >= 1 && withItems.Outcome == GridOpResult.Kind.BeltsRemoved && !BeltNetworkService.Kernel.HasCell(busy.X, busy.Y)
                            && s.Scrap == scrap2 + 1 && HomeGridService.LastBeltItemsReturned == onBelt && itemGround == onBelt;
            Expect(returned, $"FG3-LOG-02（DEBT-FG3LOG01-02）：带上有 {onBelt} 件物品的传送带照样拆，造价全额返还、物品按种类落在拆除处（{itemGround} 件，物品表之前家园仓库只存废料）");

            // 仓库满时返还变成地面物（不消失）。
            s.Scrap = HomeValleyCargo.GetStorageCapacity(s, CampaignEconomyLedger.ResourceScrap);
            int ground0 = s.GroundItems?.Length ?? 0;
            GridCell last = new GridCell(a.X + 5, a.Y);
            WorldSimulation.StepMany(60 * 5); // 让物品流走
            GridOpResult full = HomeGridService.TryRemoveBelts(s, new List<GridCell> { last });
            bool dropped = full.Outcome == GridOpResult.Kind.BeltsRemoved && (s.GroundItems?.Length ?? 0) == ground0 + 1
                           && s.GroundItems.Last().ResourceType == CampaignEconomyLedger.ResourceScrap;
            Expect(dropped, "仓库全满时拆传送带：返还的废料留在地上等机器搬（不会凭空消失）");
        }

        private static void CheckBatchDemolish()
        {
            CampaignState s = NewHome(3601);
            s.Scrap = 5000;
            GridCell core = HomeGridService.CorePivot(s);
            var placed = new List<string>();
            for (int y = core.Y + 14; y <= core.Y + 38 && placed.Count < 22; y += 4)
            {
                for (int x = core.X - 36; x <= core.X + 36 && placed.Count < 22; x += 4)
                {
                    GridOpResult r = HomeGridService.TryPlace(s, "generator_2", new GridCell(x, y), 0);
                    if (r.Success)
                    {
                        placed.Add(r.BuildingId);
                    }
                }
            }
            if (placed.Count < 21)
            {
                Fail($"只放下了 {placed.Count} 座规划中的发电机，凑不够“超过 20 座”");
                return;
            }
            var mins = placed.Select(id => HomeGridService.FindBuilding(s, id)).ToList();
            var boxMin = new GridCell(mins.Min(b => b.GridX) - 2, mins.Min(b => b.GridY) - 2);
            var boxMax = new GridCell(mins.Max(b => b.GridX) + 2, mins.Max(b => b.GridY) + 2);
            DemolishBoxPlan plan = HomeGridService.PlanDemolishBox(s, boxMin, boxMax);
            HomeValleyBuildMode mode = NewMode();
            try
            {
                mode.Open();
                mode.SetDemolishMode(true);
                GridCell emptyStart = boxMin;
                while (HomeGridService.BuildingAt(s, emptyStart) != null)
                {
                    emptyStart = new GridCell(emptyStart.X - 1, emptyStart.Y);
                }
                string before = BuildingsJson(s);
                int scrap = s.Scrap;
                mode.PointerDown(s, emptyStart);
                mode.SetHover(s, boxMax);
                mode.RefreshVisuals(s);
                bool indicator = mode.BoxIndicatorVisible && mode.BoxPlan != null && mode.BoxPlan.BuildingCount >= placed.Count;
                mode.PointerUp(s, boxMax);
                ConfirmRequest req = UiConfirmDialog.Current;
                bool asks = mode.PendingBatchConfirm && req != null && req.Title.Contains(plan.BuildingCount.ToString()) && req.Lines.Any(l => l.Contains("超过 20 座"))
                            && BuildingsJson(s) == before;
                Expect(indicator && asks && plan.NeedsConfirm,
                    $"拆除模式在空地上框选 {plan.BuildingCount} 座：拖的时候画出框（指示器），松开后先弹确认框“{req?.Title}”（超过 20 座，FGR-LOG-007），确认前什么都不变");
                UiConfirmDialog.Cancel();
                Expect(BuildingsJson(s) == before && s.Scrap == scrap && !mode.PendingBatchConfirm, "确认框点“保留”：全部原样，不扣不退");

                mode.PointerDown(s, emptyStart);
                mode.SetHover(s, boxMax);
                mode.PointerUp(s, boxMax);
                UiConfirmDialog.Confirm();
                // FG3-LOG-02：规划放下时不扣材料（还没有机器取过料），取消后库存不变；已到现场材料的全额退回由 FgConstructionSelfCheck 断言。
                bool allCancelled = placed.All(id => HomeGridService.FindBuilding(s, id) == null) && s.Scrap == scrap;
                Expect(allCancelled && mode.LastResult.Outcome == GridOpResult.Kind.BatchDemolished && mode.StatusText.Contains("取消规划 " + placed.Count)
                       && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildFirstBatchDemolish),
                    $"确认后：{placed.Count} 座规划全部取消、库存不变（还没取料，{scrap}→{s.Scrap}），状态行“{mode.StatusText}”");

                // 少量、不含关键建筑：直接执行不打扰（B04 可逆操作不弹确认）。
                GridCell? g1 = FindValid(s, "generator_2", new GridCell(core.X - 20, core.Y + 26), 8);
                GridOpResult p1 = HomeGridService.TryPlace(s, "generator_2", g1.Value, 0);
                BuildingRecord gen = HomeGridService.FindBuilding(s, p1.BuildingId);
                TickOrders(s, 60f);
                DemolishBoxPlan small = HomeGridService.PlanDemolishBox(s, new GridCell(gen.GridX - 2, gen.GridY - 2), new GridCell(gen.GridX + 2, gen.GridY + 2));
                Expect(gen.ConstructionState == BuildingConstructionState.Operational && !small.NeedsConfirm && small.ToMark.Contains(gen.BuildingId),
                    "框住 1 座已建成的普通发电机：不需要确认（可逆操作不弹框）");
                GridOpResult ex = HomeGridService.ExecuteDemolishBox(s, small);
                Expect(ex.Success && HomeGridService.IsMarkedForDemolish(s, gen.BuildingId), "执行后这座发电机被标记拆除（机器上门拆）");

                // 含关键建筑（信号中继塔 critical=1）：哪怕只有 1 座也先确认。
                GridCell? rc = FindValid(s, "signal_relay", new GridCell(core.X + 18, core.Y + 26), 8);
                GridOpResult rr = HomeGridService.TryPlace(s, "signal_relay", rc.Value, 0);
                TickOrders(s, 120f);
                BuildingRecord relay = HomeGridService.FindBuilding(s, rr.BuildingId);
                DemolishBoxPlan crit = HomeGridService.PlanDemolishBox(s, new GridCell(relay.GridX - 1, relay.GridY - 1), new GridCell(relay.GridX + 2, relay.GridY + 2));
                Expect(relay.ConstructionState == BuildingConstructionState.Operational && crit.NeedsConfirm && crit.CriticalNames.Count == 1 && crit.BuildingCount == 1,
                    "框里有关键建筑（信号中继塔）：只有 1 座也要先确认，确认框写明关键建筑名");

                // 禁拆的开局建筑、核心：写原因，不拆。
                BuildingRecord wh = Rec(s, "warehouse");
                BuildingRecord coreRec = Rec(s, "core");
                DemolishBoxPlan refuse = HomeGridService.PlanDemolishBox(s, new GridCell(coreRec.GridX - 2, coreRec.GridY - 2), new GridCell(wh.GridX + 3, wh.GridY + 2));
                bool refusedBoth = refuse.Refused.Any(kv => kv.Key == coreRec.BuildingId && kv.Value.Code == GridBlockReason.CannotDemolishCore)
                                   && refuse.Refused.Any(kv => kv.Key == wh.BuildingId && kv.Value.Code == GridBlockReason.NotRebuildable)
                                   && !refuse.ToMark.Contains(wh.BuildingId);
                Expect(refusedBoth, "框住核心与仓库：核心“不能拆除”、仓库“无法重建，不能拆除”分别写原因，都不会被标记");

                mode.SetDemolishMode(true);
                GridCell nowhere = new GridCell(core.X - 30, core.Y - 30);
                mode.PointerDown(s, nowhere);
                mode.SetHover(s, new GridCell(nowhere.X + 2, nowhere.Y + 2));
                mode.PointerUp(s, new GridCell(nowhere.X + 2, nowhere.Y + 2));
                Expect(mode.StatusIsError && mode.StatusText.Contains("框里没有"), $"框里什么都没有：提示“{mode.StatusText}”");
            }
            finally
            {
                EndMode(mode);
            }
        }

        // ── F. 搬迁 ───────────────────────────────────────────────────────────────

        private static void CheckRelocationWorld()
        {
            CampaignState s = NewWorld(3701, observe: false, out HomeValleyController home);
            BuildingRecord bay = Rec(s, "repair_bay");
            string bayId = bay.BuildingId;
            float health = bay.Health;
            int priority = bay.PowerPriority;
            bay.Inventory = new[] { new CargoEntry { ResourceType = "Scrap", Amount = 7 } };
            GridCell from = new GridCell(bay.GridX, bay.GridY);
            GridCell? target = FindValid(s, "repair_bay", new GridCell(from.X - 6, from.Y - 8), 10, asPlayer: false, ignore: bayId);
            if (target == null)
            {
                Fail("找不到维修台的新位置");
                return;
            }
            int scrap = s.Scrap;
            int count = s.BuildingRecords.Length;
            GridOpResult r = HomeGridService.TryRelocate(s, bayId, target.Value, 90);
            BuildingRecord ghost = HomeGridService.FindRelocationGhost(s, bayId);
            WorkOrderRecord order = s.WorkOrders?.FirstOrDefault(o => ghost != null && o.TargetId == ghost.BuildingId && o.Kind == WorkOrderKind.Build);
            bool planned = r.Outcome == GridOpResult.Kind.RelocationPlanned && ghost != null && ghost.RelocateFromId == bayId && order != null
                           && s.Scrap == scrap && bay.ConstructionState == BuildingConstructionState.Operational && bay.GridX == from.X
                           && HomeGridService.BuildingAt(s, from)?.BuildingId == bayId && SnapshotMatches(s);
            Expect(planned && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildFirstRelocate),
                $"搬迁已建成的维修台（本局无法重建也可以搬）：新位置出现搬迁目标虚影 + 施工工作单，不花材料，原建筑照常在原地（{r.Outcome}）");
            AssertRejected(s, () => HomeGridService.TryToggleDemolish(s, bayId), GridBlockReason.NotRebuildable, "搬迁中拆维修台（本来就禁拆）", "无法重建");
            AssertRejected(s, () => HomeGridService.TryRotate(s, bayId), GridBlockReason.Relocating, "搬迁中转原建筑", "正在搬迁");
            Expect(HomeValleyWorkOrders.DescribeTarget(s, order).Contains("搬迁"), $"工作单写明是搬迁：“{HomeValleyWorkOrders.DescribeTarget(s, order)}”");

            bool done = StepUntil(() => HomeGridService.FindRelocationGhost(s, bayId) == null, 240);
            BuildingRecord moved = HomeGridService.FindBuilding(s, bayId);
            bool swapped = done && moved != null && moved.GridX == target.Value.X && moved.GridY == target.Value.Y && Mathf.Approximately(moved.Rotation, 90f)
                           && Mathf.Approximately(moved.Health, health) && moved.PowerPriority == priority && moved.Inventory.Length == 1 && moved.Inventory[0].Amount == 7
                           && moved.ConstructionState == BuildingConstructionState.Operational && moved.RelocateFromId == null
                           && s.BuildingRecords.Length == count && HomeGridService.BuildingAt(s, from)?.BuildingId != bayId
                           && s.Scrap == scrap && order.State == WorkOrderState.Completed && SnapshotMatches(s);
            Expect(swapped, $"机器走过去施工，完工后维修台换到新位置（{moved?.GridX},{moved?.GridY}，朝 90°），ID、生命、电力优先级、库存都保留，原位置空出，建筑总数不变，不花废料");
        }

        private static void CheckRelocationEdges()
        {
            CampaignState s = NewHome(3801);
            GridCell core = HomeGridService.CorePivot(s);
            AssertRejected(s, () => HomeGridService.TryRelocate(s, Rec(s, "core").BuildingId, new GridCell(core.X + 30, core.Y + 30), 0),
                GridBlockReason.CannotRelocateCore, "搬迁核心", "不能搬迁");
            BuildingRecord gen = Rec(s, "generator"); // 开局受损
            AssertRejected(s, () => HomeGridService.TryRelocate(s, gen.BuildingId, new GridCell(gen.GridX + 6, gen.GridY), 0),
                GridBlockReason.RelocateDamaged, "搬迁受损的建筑", "请先修复");
            BuildingRecord bay = Rec(s, "repair_bay");
            AssertRejected(s, () => HomeGridService.TryRelocate(s, bay.BuildingId, new GridCell(bay.GridX, bay.GridY), (int)bay.Rotation),
                GridBlockReason.RelocateSame, "搬到原地", "一样");
            AssertRejected(s, () => HomeGridService.TryRelocate(s, bay.BuildingId, new GridCell(core.X, core.Y + 5), 0),
                GridBlockReason.CoreReserve, "搬到核心通道里", "通道");
            AssertRejected(s, () => HomeGridService.TryRelocate(s, bay.BuildingId, new GridCell(core.X + 95, core.Y), 0),
                GridBlockReason.Fog, "搬进迷雾", "超出已探索区域");

            // 重叠搬迁：往旁边挪 1 格也可以（重叠的格子在完工前归原建筑）。
            GridOpResult nudge = HomeGridService.TryRelocate(s, bay.BuildingId, new GridCell(bay.GridX + 1, bay.GridY), (int)bay.Rotation);
            bool overlapOk = nudge.Outcome == GridOpResult.Kind.RelocationPlanned
                             && HomeGridService.BuildingAt(s, new GridCell(bay.GridX + 1, bay.GridY))?.BuildingId == bay.BuildingId
                             && HomeGridService.BuildingAt(s, new GridCell(bay.GridX + 2, bay.GridY))?.BuildingId == bay.BuildingId + HomeGridService.RelocationGhostSuffix
                             && SnapshotMatches(s);
            Expect(overlapOk, "往旁边挪 1 格（新旧占地重叠）：允许；重叠的格子在完工前仍归原建筑，只多出来的那一列归虚影；占用缓存与按记录重建一致");

            // 再搬一次 = 挪虚影；取消 = 在虚影上点拆除。
            GridCell? t2 = FindValid(s, "repair_bay", new GridCell(bay.GridX, bay.GridY - 9), 8, asPlayer: false, ignore: bay.BuildingId);
            GridOpResult again = HomeGridService.TryRelocate(s, bay.BuildingId, t2.Value, 0);
            BuildingRecord ghost = HomeGridService.FindRelocationGhost(s, bay.BuildingId);
            Expect(again.Outcome == GridOpResult.Kind.PlanMoved && ghost != null && ghost.GridX == t2.Value.X && ghost.GridY == t2.Value.Y
                   && s.BuildingRecords.Count(b => b.RelocateFromId == bay.BuildingId) == 1,
                "已经在搬迁的建筑再搬一次：把目标虚影挪到新位置（只有一个虚影）");
            GridOpResult cancel = HomeGridService.TryToggleDemolish(s, ghost.BuildingId);
            Expect(cancel.Outcome == GridOpResult.Kind.PlanCancelled && HomeGridService.FindRelocationGhost(s, bay.BuildingId) == null
                   && HomeGridService.FindBuilding(s, bay.BuildingId).ConstructionState == BuildingConstructionState.Operational,
                "在虚影上点拆除 = 取消搬迁：虚影消失，维修台留在原地照常运转");

            // 可重建的建筑在搬迁中：拆除给“正在搬迁”（先取消搬迁）。
            GridCell? gp = FindValid(s, "generator_2", new GridCell(core.X + 16, core.Y + 26), 10);
            GridOpResult gpr = HomeGridService.TryPlace(s, "generator_2", gp.Value, 0);
            TickOrders(s, 60f);
            BuildingRecord builtGen = HomeGridService.FindBuilding(s, gpr.BuildingId);
            GridCell? gto = FindValid(s, "generator_2", new GridCell(gp.Value.X + 6, gp.Value.Y + 4), 8, asPlayer: false, ignore: builtGen.BuildingId);
            GridOpResult gmove = HomeGridService.TryRelocate(s, builtGen.BuildingId, gto.Value, 0);
            Expect(builtGen.ConstructionState == BuildingConstructionState.Operational && gmove.Outcome == GridOpResult.Kind.RelocationPlanned,
                "玩家自己建成的发电机：可以搬迁");
            AssertRejected(s, () => HomeGridService.TryToggleDemolish(s, builtGen.BuildingId), GridBlockReason.Relocating, "搬迁中拆原建筑", "正在搬迁");
            HomeGridService.TryToggleDemolish(s, HomeGridService.FindRelocationGhost(s, builtGen.BuildingId).BuildingId);

            // 规划中的建筑：直接挪，不花钱。
            GridCell? p = FindValid(s, "generator_2", new GridCell(core.X - 8, core.Y + 26), 10);
            GridOpResult placed = HomeGridService.TryPlace(s, "generator_2", p.Value, 0);
            int scrap = s.Scrap;
            GridCell? p2 = FindValid(s, "generator_2", new GridCell(p.Value.X + 8, p.Value.Y), 8, asPlayer: false, ignore: placed.BuildingId);
            GridOpResult moved = HomeGridService.TryRelocate(s, placed.BuildingId, p2.Value, 90);
            BuildingRecord plan = HomeGridService.FindBuilding(s, placed.BuildingId);
            Expect(moved.Outcome == GridOpResult.Kind.PlanMoved && plan.GridX == p2.Value.X && Mathf.Approximately(plan.Rotation, 90f) && s.Scrap == scrap
                   && HomeGridService.BuildingAt(s, p.Value) == null && SnapshotMatches(s),
                "还没开工的规划：直接挪到新位置并转向，不花钱，原格子空出");

            // 原建筑在施工期间消失（例如被摧毁）：搬迁作废，虚影移除，工单失败，不留幽灵。
            BuildingRecord bench = Rec(s, "analysis_bench");
            GridCell? bt = FindValid(s, "analysis_bench", new GridCell(bench.GridX - 8, bench.GridY - 6), 10, asPlayer: false, ignore: bench.BuildingId);
            HomeGridService.TryRelocate(s, bench.BuildingId, bt.Value, 0);
            BuildingRecord benchGhost = HomeGridService.FindRelocationGhost(s, bench.BuildingId);
            s.BuildingRecords = s.BuildingRecords.Where(b => b.BuildingId != bench.BuildingId).ToArray();
            // FG3-LOG-02：上面挪过的发电机规划也在待分配池里（取料腿目的地是仓库，可能比搬迁虚影近，机器先去建它），跑够两张单的时间。
            TickOrders(s, 150f);
            WorkOrderRecord failed = s.WorkOrders.First(o => o.TargetId == benchGhost.BuildingId);
            Expect(failed.State == WorkOrderState.Failed && failed.FailureReason == "relocate-source-gone" && HomeGridService.FindBuilding(s, benchGhost.BuildingId) == null
                   && s.BuildingRecords.All(b => b.RelocateFromId != bench.BuildingId),
                "原建筑在搬迁途中消失：搬迁作废（工单失败 relocate-source-gone），虚影移除，不会凭空多出一座建筑");
        }

        private static void CheckExitFollowsBuilding()
        {
            CampaignState s = NewHome(3901);
            BuildingRecord station = Rec(s, "assembly_station");
            GridCell exit0 = HomeGridService.ExitCell(s, "assembly_exit");
            bool atLayout = exit0 == HomeGridService.AnchorCell(s, "assembly_exit");
            GridOpResult rot = HomeGridService.TryRotate(s, station.BuildingId);
            GridCell exit1 = HomeGridService.ExitCell(s, "assembly_exit");
            var ports = new List<PortPlacement>();
            HomeGridService.PortsOf(station, ports);
            PortPlacement outPort = ports.First(p => p.IsOutput);
            Vector2Int d = GridMath.DirVector(outPort.Dir);
            bool followsPort = rot.Success && exit1 == new GridCell(station.GridX, station.GridY - 8) && d == new Vector2Int(0, -1);
            Expect(atLayout && followsPort,
                $"装配站转 90°：出口跟着转到南边 {exit1}（输出端口也朝南），箭头、出口、出厂位置同一来源（DEBT-FG0ARCH04-03）");

            // 出口位置不能被别的建筑压住：在南边出口处放一座发电机后，再转回去就被“出口通道”挡住。
            MethodInfo spawnAt = typeof(HomeValleyFactory).GetMethod("SpawnProducedMachine", BindingFlags.NonPublic | BindingFlags.Static);
            s.BuildingRecords.First(b => b.BuildingId == station.BuildingId).PowerState = BuildingPowerState.Powered;
            var item = new FactoryQueueItemRecord { QueueItemId = "q-test", BlueprintId = HomeValleyLayout.BlueprintErc003Id, BlueprintVersion = 1, TransactionId = "tx-none" };
            int before = MachineRegistry.AllRecords.Count;
            spawnAt.Invoke(null, new object[] { s, item });
            MachineRecord produced = MachineRegistry.AllRecords.Skip(before).FirstOrDefault();
            Expect(produced != null && GridCell.FromWorld(produced.WorldPosition) == exit1,
                $"转过的装配站出厂：新机器出现在新出口 {exit1}（实际 {(produced != null ? GridCell.FromWorld(produced.WorldPosition).ToString() : "无")}）");

            // 再转 90°（到 180°）：出口会落到西边 8 格，正好压在开局解析台上——拒绝，写明“出口通道会被‘解析台’挡住”，状态不变。
            AssertRejected(s, () => HomeGridService.TryRotate(s, station.BuildingId), GridBlockReason.ExitBlocked, "旋转后出口会被解析台挡住", "出口通道会被“解析台”挡住");
            Expect(HomeGridService.ExitCell(s, "assembly_exit") == exit1, "被拒后装配站仍朝南、出口仍在南边");
        }

        /// <summary>
        /// 负向矩阵补全（修复轮 1）：① 旋转 / 搬迁后出口落在机器走不了的地形（悬崖 / 水源）→ ExitTerrain；
        /// ② 施工中的规划与已开工的搬迁虚影不能再挪 → RelocateUnderConstruction；
        /// 以及搬迁完工保留玩家关停状态、虚影不计入数量、挪走已分配的规划时停下赶路机器。
        /// </summary>
        private static void CheckExitTerrainAndBusyRelocation()
        {
            CampaignState s = NewHome(3901); // 与 CheckExitFollowsBuilding 同一张图：装配站转 90° 在这张图上合法（对照组）
            HomeGridMap map = HomeGridService.MapFor(s);
            BuildingRecord station = Rec(s, "assembly_station");
            int rot0 = GridMath.NormalizeRotation(station.Rotation);
            GridCell pivot0 = new GridCell(station.GridX, station.GridY);
            GridCell exit0 = HomeGridService.ExitCell(s, "assembly_exit");

            // ① a. 搬迁：新位置的出口落在水源上。
            GridCell? target = FindValid(s, "assembly_station", new GridCell(pivot0.X - 12, pivot0.Y - 10), 14, rotation: rot0, asPlayer: false,
                ignore: station.BuildingId);
            if (target == null)
            {
                Fail("找不到装配站的合法新位置（出口地形断言）");
            }
            else
            {
                GridCell newExit = new GridCell(target.Value.X + (exit0.X - pivot0.X), target.Value.Y + (exit0.Y - pivot0.Y));
                byte saved = map.GetTerrain(newExit);
                map.SetTerrain(newExit, GridContent.TerrainCode("water"));
                AssertRejected(s, () => HomeGridService.TryRelocate(s, station.BuildingId, target.Value, rot0), GridBlockReason.ExitTerrain,
                    "搬迁装配站到出口会落在水源上的位置", "水源");
                Expect(HomeGridService.FindRelocationGhost(s, station.BuildingId) == null && HomeGridService.ExitCell(s, "assembly_exit") == exit0,
                    "出口落水被拒后：没有搬迁虚影，出口仍在原处");
                map.SetTerrain(newExit, saved);
                GridPlacementResult control = HomeGridService.ValidatePlacement(s, "assembly_station", target.Value, rot0, asPlayerPlacement: false,
                    ignoreBuildingId: station.BuildingId, checkCost: false);
                Expect(control.Ok, $"对照：水源还原后同一位置可以搬（被拒只因为出口地形）（{control.Describe()}）");
            }

            // ① b. 旋转：转 90° 后出口在南边 8 格，把那一格设成悬崖。
            GridCell southExit = new GridCell(station.GridX, station.GridY - 8);
            byte southSaved = map.GetTerrain(southExit);
            map.SetTerrain(southExit, GridContent.TerrainCode("cliff"));
            AssertRejected(s, () => HomeGridService.TryRotate(s, station.BuildingId), GridBlockReason.ExitTerrain, "旋转装配站后出口会落在悬崖上", "悬崖");
            Expect(HomeGridService.ExitCell(s, "assembly_exit") == exit0 && GridMath.NormalizeRotation(Rec(s, "assembly_station").Rotation) == rot0,
                "出口落悬崖被拒后：装配站朝向、出口位置都不变");
            map.SetTerrain(southExit, southSaved);
            GridOpResult rotOk = HomeGridService.TryRotate(s, station.BuildingId);
            Expect(rotOk.Success && HomeGridService.ExitCell(s, "assembly_exit") == southExit, $"对照：悬崖还原后同一次旋转成功，出口到 {southExit}");

            // ② a. 施工中的规划不能挪。
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? gp = FindValid(s, "generator_2", new GridCell(core.X + 16, core.Y + 26), 10);
            GridOpResult placed = gp != null ? HomeGridService.TryPlace(s, "generator_2", gp.Value, 0) : default;
            WorkOrderRecord genOrder = placed.Success ? s.WorkOrders.FirstOrDefault(o => o.TargetId == placed.BuildingId && o.Kind == WorkOrderKind.Build) : null;
            bool genStarted = TickOrdersUntil(s, () => genOrder != null && genOrder.State == WorkOrderState.InProgress, 30f);
            if (!genStarted || gp == null)
            {
                Fail($"发电机规划没有开工（{genOrder?.State}），施工中挪规划的断言没跑到");
            }
            else
            {
                GridCell? gp2 = FindValid(s, "generator_2", new GridCell(gp.Value.X + 8, gp.Value.Y), 8, asPlayer: false, ignore: placed.BuildingId);
                AssertRejected(s, () => HomeGridService.TryRelocate(s, placed.BuildingId, gp2 ?? new GridCell(gp.Value.X + 8, gp.Value.Y), 0),
                    GridBlockReason.RelocateUnderConstruction, "挪一座已经开工的规划", "正在施工");
            }
            TickOrders(s, 60f);

            // ② b. 搬迁虚影开工后不能再挪；搬迁中虚影不计入数量；原建筑被玩家关停后完工仍保持关停。
            BuildingRecord bay = Rec(s, "repair_bay");
            int bayCount = HomeGridService.CountOfType(s, "repair_bay");
            GridCell? bt = FindValid(s, "repair_bay", new GridCell(bay.GridX - 6, bay.GridY - 8), 10, asPlayer: false, ignore: bay.BuildingId);
            GridOpResult rel = bt != null ? HomeGridService.TryRelocate(s, bay.BuildingId, bt.Value, 0) : default;
            BuildingRecord ghost = HomeGridService.FindRelocationGhost(s, bay.BuildingId);
            Expect(rel.Outcome == GridOpResult.Kind.RelocationPlanned && ghost != null && HomeGridService.CountOfType(s, "repair_bay") == bayCount,
                $"搬迁中的维修台：数量仍记 {bayCount} 座（虚影不算新建筑，“已有 N”不多 1）（实际 {HomeGridService.CountOfType(s, "repair_bay")}）");
            WorkOrderRecord relOrder = ghost != null ? s.WorkOrders.FirstOrDefault(o => o.TargetId == ghost.BuildingId && o.Kind == WorkOrderKind.Build) : null;
            bool relStarted = TickOrdersUntil(s, () => relOrder != null && relOrder.State == WorkOrderState.InProgress, 30f);
            if (!relStarted || bt == null)
            {
                Fail($"搬迁施工单没有开工（{relOrder?.State}），施工中再搬的断言没跑到");
            }
            else
            {
                AssertRejected(s, () => HomeGridService.TryRelocate(s, bay.BuildingId, new GridCell(bt.Value.X + 8, bt.Value.Y), 0),
                    GridBlockReason.RelocateUnderConstruction, "搬迁虚影已开工后再挪", "正在施工");
                HomeValleyPowerGrid.TryToggleShutdown(s, bay.BuildingId);
                bool disabledBefore = HomeGridService.FindBuilding(s, bay.BuildingId)?.ConstructionState == BuildingConstructionState.Disabled;
                TickOrders(s, 40f);
                BuildingRecord moved = HomeGridService.FindBuilding(s, bay.BuildingId);
                Expect(disabledBefore && HomeGridService.FindRelocationGhost(s, bay.BuildingId) == null && moved != null && moved.GridX == bt.Value.X
                       && moved.ConstructionState == BuildingConstructionState.Disabled,
                    $"搬迁途中玩家关停了维修台：完工换到新位置后仍是关停（{moved?.ConstructionState}），不会被悄悄重新启用（FGR-BASE-020）");
            }

            // ③ 挪走一条已分配、机器正在赶路的规划：机器收到停止移动，工单按新位置重新分配。
            GridCell? p3 = FindValid(s, "generator_2", new GridCell(core.X - 8, core.Y + 26), 10);
            GridOpResult plan3 = p3 != null ? HomeGridService.TryPlace(s, "generator_2", p3.Value, 0) : default;
            WorkOrderRecord o3 = plan3.Success ? s.WorkOrders.FirstOrDefault(o => o.TargetId == plan3.BuildingId && o.Kind == WorkOrderKind.Build) : null;
            var released = new List<int>();
            for (int i = 0; i < 20 && o3 != null && o3.State != WorkOrderState.Reserved; i++)
            {
                HomeValleyWorkOrders.MarkAssignmentDirty();
                HomeValleyWorkOrders.Tick(s, 0.1f, _ => null, id => released.Add(id), _ => false, _ => { }); // 不“到达”：机器停在赶路阶段
            }
            int walker = o3?.AssignedMachineLogicId ?? 0;
            GridCell? p4 = p3 != null ? FindValid(s, "generator_2", new GridCell(p3.Value.X + 8, p3.Value.Y), 8, asPlayer: false, ignore: plan3.BuildingId) : null;
            GridOpResult moved3 = o3 != null && o3.State == WorkOrderState.Reserved && p4 != null
                ? HomeGridService.TryRelocate(s, plan3.BuildingId, p4.Value, 0)
                : default;
            bool backToPool = o3 != null && o3.State == WorkOrderState.Ready && o3.AssignedMachineLogicId == 0;
            released.Clear();
            HomeValleyWorkOrders.MarkAssignmentDirty();
            HomeValleyWorkOrders.Tick(s, 0.1f, _ => null, id => released.Add(id), _ => false, _ => { });
            Expect(walker > 0 && moved3.Outcome == GridOpResult.Kind.PlanMoved && backToPool && released.Contains(walker)
                   && HomeValleyWorkOrders.PendingMovementReleaseCount == 0,
                $"挪走机器正在赶去的规划：工单回待分配池，原先领单的机器 {walker} 收到停止移动（{string.Join(",", released)}），不会白跑到空了的旧位置");
        }

        /// <summary>按固定 0.1 秒推进工作单，直到条件成立或超时（游戏秒）。</summary>
        private static bool TickOrdersUntil(CampaignState s, Func<bool> done, float maxSeconds)
        {
            for (float t = 0f; t < maxSeconds; t += 0.1f)
            {
                if (done())
                {
                    return true;
                }
                TickOrders(s, 0.1f);
            }
            return done();
        }

        // ── 正式输入（FGT-LOG-001：放置、旋转、拆除、搬迁走正式输入）─────────────────────────

        private static void CheckFormalInput()
        {
            CampaignState s = NewHome(4001);
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            InputRouter.SetGameplayPaused(false);
            HomeValleyBuildMode mode = NewMode();
            try
            {
                BuildingRecord bay = Rec(s, "repair_bay");
                GridCell from = new GridCell(bay.GridX, bay.GridY);
                GridCell? to = FindValid(s, "repair_bay", new GridCell(from.X - 6, from.Y - 8), 10, asPlayer: false, ignore: bay.BuildingId);
                // 搬迁键（默认 E）在战略上下文打开建造模式并进入搬迁模式。
                reader.Press(KeyCode.E);
                mode.Tick(null, s, true);
                Frame(reader);
                bool relocateOn = mode.IsOpen && mode.RelocateMode;
                mode.Tick(null, s, true);
                mode.SetHover(s, from);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                bool carrying = mode.CarryBuildingId == bay.BuildingId;
                reader.Press(KeyCode.R);
                mode.Tick(null, s, true);
                Frame(reader);
                mode.SetHover(s, to.Value);
                mode.RefreshPreview(s);
                bool preview = mode.Preview != null && mode.Preview.Ok && mode.Preview.Rotation == GridMath.NormalizeRotation(bay.Rotation + 90);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                BuildingRecord ghost = HomeGridService.FindRelocationGhost(s, bay.BuildingId);
                Expect(relocateOn && carrying && preview && ghost != null && ghost.GridX == to.Value.X && mode.CarryBuildingId == null,
                    $"按搬迁键（E）→ 左键点维修台（跟着鼠标）→ R 转向 → 左键点新位置：生成搬迁目标虚影（{mode.StatusText}）");

                // 右键逐层退出：搬迁模式 → 建造模式。
                reader.MouseDown.Add(1);
                mode.Tick(null, s, true);
                Frame(reader);
                bool relocateOff = mode.IsOpen && !mode.RelocateMode;
                reader.MouseDown.Add(1);
                mode.Tick(null, s, true);
                Frame(reader);
                Expect(relocateOff && !mode.IsOpen, "右键先退出搬迁模式，再右键退出建造模式（FGR-UX-001）");

                // 空闲状态下直接按住一座建筑拖过去（拖放搬迁）。
                HomeGridService.TryToggleDemolish(s, ghost.BuildingId); // 先取消上一次搬迁
                mode.Open();
                mode.Tick(null, s, true);
                BuildingRecord bench = Rec(s, "analysis_bench");
                GridCell grab = new GridCell(bench.GridX, bench.GridY);
                GridCell? dropPivot = FindValid(s, "analysis_bench", new GridCell(grab.X - 7, grab.Y - 6), 8, (int)bench.Rotation, asPlayer: false, ignore: bench.BuildingId);
                mode.SetHover(s, grab);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                bool dragging = mode.Drag == HomeValleyBuildMode.DragKind.Relocate;
                mode.SetHover(s, dropPivot.Value);
                bool dragPreview = mode.Preview != null && mode.Preview.Ok;
                reader.MouseUp.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                BuildingRecord benchGhost = HomeGridService.FindRelocationGhost(s, bench.BuildingId);
                Expect(dragging && dragPreview && benchGhost != null && benchGhost.GridX == dropPivot.Value.X && benchGhost.GridY == dropPivot.Value.Y,
                    "空闲状态下按住解析台拖到新位置、松开：拖的时候有虚影预览，松开后生成搬迁目标");

                // 手抖：按住建筑只拖过 1 格边界就松开 → 不搬（要挪 1 格用搬迁模式）。
                BuildingRecord bay2 = Rec(s, "repair_bay");
                var bayCell = new GridCell(bay2.GridX, bay2.GridY);
                mode.SetHover(s, bayCell);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                mode.SetHover(s, new GridCell(bayCell.X + 1, bayCell.Y));
                reader.MouseUp.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                Expect(HomeGridService.FindRelocationGhost(s, bay2.BuildingId) == null && mode.Drag == HomeValleyBuildMode.DragKind.None,
                    $"空闲状态下按住维修台只拖 1 格就松开：不搬迁（拖着搬迁至少 {HomeValleyBuildMode.IdleDragMinCells} 格，防手抖误搬）");

                // 选中建筑后放置（R 旋转）、Esc 退出：与原型相同的正式路径仍然可用。
                mode.Select("generator_2");
                GridCell core = HomeGridService.CorePivot(s);
                GridCell? spot = FindValid(s, "generator_2", new GridCell(core.X - 8, core.Y + 26), 10);
                mode.SetHover(s, spot.Value);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                bool placed = HomeGridService.BuildingAt(s, spot.Value)?.BuildingTypeId == "generator_2";
                bool esc = UiEscapeStack.CloseTop() && !mode.IsOpen;
                Expect(placed && esc, "选中发电机左键放置、Esc 退出建造模式（正式输入路径不变）");
            }
            finally
            {
                EndMode(mode);
                InputRouter.DebugSetReader(null);
            }
        }

        // ── G. 迷雾：机器探索 ────────────────────────────────────────────────────────

        private static void CheckMachineExploration()
        {
            CampaignState s = NewWorld(4101, observe: false, out HomeValleyController home);
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridMap map = HomeGridService.MapFor(s);
            var far = new Vector2(core.X + 60, core.Y);
            bool fogBefore = !map.IsExploredNoLoad(new GridCell((int)far.x, (int)far.y));
            MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId,
                far, 100f, 100f);
            int reveals0 = MachineExploration.Reveals;
            int areas0 = s.Grid.Explored.Length;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool explored = map.IsExploredNoLoad(new GridCell((int)far.x, (int)far.y)) && s.Grid.Explored.Length > areas0 && MachineExploration.Reveals > reveals0;
            GridCell? now = FindValid(s, "generator_2", new GridCell((int)far.x + 3, (int)far.y + 3), 6);
            Expect(fogBefore && explored && now != null,
                $"一台机器站进迷雾（{far}）：两秒游戏时间内周围 {GridContent.TuningInt("grid.explore_machine_radius")} 格记为已探索（探索圆 {areas0}→{s.Grid.Explored.Length}），那里可以建造了（FGR-LOG-013）");
            int areas1 = s.Grid.Explored.Length;
            WorldSimulation.StepMany(GameClock.StepHz * 10);
            Expect(s.Grid.Explored.Length == areas1, $"机器原地不动 10 秒：不重复记圆（仍 {s.Grid.Explored.Length} 个，已探索圆数量随面积增长、不随时间增长）");

            // O(1)：每步只看 1 台（计数与机器数无关）。
            int per = GridContent.TuningInt("grid.explore_machines_per_step");
            Expect(per == 1, $"每个模拟步只轮流检查 {per} 台机器（热更层每步 O(1)，与机器数无关）");

            // 存读档后已探索区域还在。
            SaveResult saved = CampaignSaveService.Save(2, s, SaveReason.Manual);
            LoadResult loaded = CampaignSaveService.Load(2);
            Expect(saved.Success && loaded.Success && loaded.State.Grid.Explored.Length == s.Grid.Explored.Length,
                "机器探索出的区域进存档，读档后仍可建造");
            CampaignSession.Set(0, s);
        }

        // ── H. 格线开关 ──────────────────────────────────────────────────────────

        private static void CheckGridLines()
        {
            CampaignState s = NewHome(4201);
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            HomeValleyBuildMode mode = NewMode();
            bool original = GameSettings.BuildGridLinesEnabled;
            try
            {
                GameSettings.SetBuildGridLinesEnabled(true);
                mode.Open();
                mode.CompleteOverlayNow(s);
                GridCell core = HomeGridService.CorePivot(s);
                GridCell probe = FindValid(s, "generator_2", new GridCell(core.X - 8, core.Y + 26), 10) ?? new GridCell(0, 30);
                mode.TryGetOverlayPixel(probe, 0, 3, out Color32 lineOn);
                mode.TryGetOverlayPixel(probe, 3, 3, out Color32 inner);
                mode.Tick(null, s, true);
                reader.Press(KeyCode.G);
                mode.Tick(null, s, true);
                Frame(reader);
                bool off = !GameSettings.BuildGridLinesEnabled;
                mode.CompleteOverlayNow(s);
                mode.TryGetOverlayPixel(probe, 0, 3, out Color32 lineOff);
                bool pixels = lineOn.r < inner.r && lineOff.r == inner.r && lineOff.g == inner.g;
                reader.Press(KeyCode.G);
                mode.Tick(null, s, true);
                Frame(reader);
                bool back = GameSettings.BuildGridLinesEnabled;
                Expect(off && back && pixels && mode.TerrainOverlay.GridLines,
                    $"按格线开关（默认 G）：叠加层格线像素 {lineOn} → 关掉后与格内同色 {lineOff}（格内 {inner}）；再按恢复；开关记在本机设置（普通视角地貌层始终不画格线，FG-GAP-021）");
            }
            finally
            {
                GameSettings.SetBuildGridLinesEnabled(original);
                EndMode(mode);
                InputRouter.DebugSetReader(null);
            }
        }

        // ── I. 存读档 ────────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewHome(4301);
            BuildCatalog.TrySetHotbar(s, 0, "belt_t1");
            BuildCatalog.TrySetHotbar(s, 9, "signal_relay");
            BuildingRecord bay = Rec(s, "repair_bay");
            GridCell? to = FindValid(s, "repair_bay", new GridCell(bay.GridX - 6, bay.GridY - 8), 10, asPlayer: false, ignore: bay.BuildingId);
            HomeGridService.TryRelocate(s, bay.BuildingId, to.Value, 180);
            string expect = BuildingsJson(s);
            SaveResult saved = CampaignSaveService.Save(1, s, SaveReason.Manual);
            HomeGridService.Invalidate();
            LoadResult loaded = CampaignSaveService.Load(1);
            CampaignState l = loaded.State;
            bool same = saved.Success && loaded.Success && BuildingsJson(l) == expect && BuildCatalog.HotbarId(l, 0) == "belt_t1" && BuildCatalog.HotbarId(l, 9) == "signal_relay"
                        && BuildCatalog.HotbarId(l, 1) == null && HomeGridService.FindRelocationGhost(l, bay.BuildingId) != null && SnapshotMatches(l);
            Expect(same, "真文件存读档：快捷栏 10 格、搬迁中的目标虚影（RelocateFromId）逐字段往返，占用重建一致");
            if (loaded.Success)
            {
                CampaignSession.Set(0, l);
                MachineRegistry.SpawnMachine(HomeValleyLayout.Erc002ChassisId, HomeValleyLayout.BlueprintHaulerId, HomeValleyLayout.RegionId, new Vector2(-10f, -6f), 100f, 100f);
                TickOrders(l, 40f);
                BuildingRecord after = HomeGridService.FindBuilding(l, bay.BuildingId);
                Expect(HomeGridService.FindRelocationGhost(l, bay.BuildingId) == null && after.GridX == to.Value.X && Mathf.Approximately(after.Rotation, 180f),
                    "读档后搬迁照常完工：维修台换到新位置");
            }

            // 旧存档（没有 Hotbar 字段）：按全空处理。
            CampaignState old = NewHome(4302);
            string json = JsonUtility.ToJson(old).Replace("\"Hotbar\":[],", string.Empty);
            CampaignState legacy = JsonUtility.FromJson<CampaignState>(json);
            Expect(BuildCatalog.HotbarEntry(legacy, 0) == null && BuildCatalog.TrySetHotbar(legacy, 2, "belt_t1") && legacy.Grid.Hotbar.Length == 10,
                "旧存档没有快捷栏字段：读成全空，放入第一项时补齐 10 格");
        }

        // ── J. 暂停与倍速 ────────────────────────────────────────────────────────

        private static void CheckTiming()
        {
            var results = new List<string>();
            bool allSame = true;
            float seconds = GridContent.Tuning("grid.relocate_seconds");
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewHome(4400);
                BuildingRecord bay = Rec(s, "repair_bay");
                GridCell? to = FindValid(s, "repair_bay", new GridCell(bay.GridX - 6, bay.GridY - 8), 10, asPlayer: false, ignore: bay.BuildingId);
                InputRouter.SetGameplayPaused(true, strategic: true);
                GridOpResult r = HomeGridService.TryRelocate(s, bay.BuildingId, to.Value, 0);
                InputRouter.SetGameplayPaused(false);
                StrategyClock.Reset();
                StrategyClock.SetSpeed(speed);
                const float frame = 1f / 60f;
                for (int i = 0; i < 120; i++)
                {
                    HomeValleyWorkOrders.MarkAssignmentDirty();
                    HomeValleyWorkOrders.Tick(s, 0f, _ => null, _ => { }, _ => false, o => HomeValleyWorkOrders.OnArrivedAtWork(s, o.WorkOrderId));
                }
                bool pausedNoProgress = r.Outcome == GridOpResult.Kind.RelocationPlanned && HomeGridService.FindRelocationGhost(s, bay.BuildingId) != null;
                float game = 0f;
                int frames = 0;
                while (HomeGridService.FindRelocationGhost(s, bay.BuildingId) != null && frames < 60 * 200)
                {
                    float dt = StrategyClock.GetScaledDt(frame, false);
                    HomeValleyWorkOrders.MarkAssignmentDirty();
                    HomeValleyWorkOrders.Tick(s, dt, _ => null, _ => { }, _ => false, o => HomeValleyWorkOrders.OnArrivedAtWork(s, o.WorkOrderId));
                    game += dt;
                    frames++;
                }
                float real = frames * frame;
                results.Add($"{speed}x：游戏 {game:F1} 秒 / 真实 {real:F1} 秒");
                allSame &= pausedNoProgress && Mathf.Abs(game - seconds) < 0.6f && Mathf.Abs(real * speed - game) < 0.6f;
            }
            StrategyClock.Reset();
            Expect(allSame, $"倍速矩阵：战略暂停中可以规划搬迁、施工不推进；0.5x / 1x / 2x / 3x 下搬迁都需要约 {seconds} 秒游戏时间，真实时间按倍速缩放（{string.Join("；", results)}）");
        }

        // ── K. 观察 / 不观察一致 ─────────────────────────────────────────────────────

        private static void CheckObservedEqualsUnobserved()
        {
            string Run(bool observed)
            {
                CampaignState s = NewWorld(4501, observe: observed, out HomeValleyController home);
                GridCell core = HomeGridService.CorePivot(s);
                BuildingRecord bay = Rec(s, "repair_bay");
                GridCell to = FindValid(s, "repair_bay", new GridCell(bay.GridX - 6, bay.GridY - 8), 10, asPlayer: false, ignore: bay.BuildingId) ?? default;
                GridCell a = FindBeltRow(s, new GridCell(core.X - 12, core.Y + 26), 6, 12) ?? default;
                GridCell gen = FindValid(s, "generator_2", new GridCell(core.X + 12, core.Y + 26), 10) ?? default;
                if (observed)
                {
                    HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
                    mode.Open();
                    mode.SetRelocateMode(true);
                    mode.ClickCell(s, new GridCell(bay.GridX, bay.GridY));
                    mode.ClickCell(s, to);
                    mode.Select("belt_t1");
                    mode.RotateGhost();
                    mode.PointerDown(s, a);
                    mode.SetHover(s, new GridCell(a.X + 5, a.Y));
                    mode.PointerUp(s, new GridCell(a.X + 5, a.Y));
                    mode.Select("generator_2");
                    mode.ClickCell(s, gen);
                    mode.RefreshVisuals(s);
                    mode.Close();
                }
                else
                {
                    HomeGridService.TryRelocate(s, bay.BuildingId, to, (int)bay.Rotation);
                    HomeGridService.TryPlaceBeltPath(s, "belt_t1", a, new GridCell(a.X + 5, a.Y), BeltDir.East);
                    HomeGridService.TryPlace(s, "generator_2", gen, 90); // 建造模式里虚影朝向保持上一次旋转（90°）
                }
                WorldSimulation.StepMany(GameClock.StepHz * 90);
                string belts = string.Join(",", Enumerable.Range(0, 6).Select(i => BeltNetworkService.Kernel.HasCell(a.X + i, a.Y) ? "1" : "0"));
                return BuildingsJson(s) + "\n" + s.Scrap + "\n" + belts + "\n" + s.Grid.Explored.Length;
            }

            string seen = Run(true);
            string unseen = Run(false);
            Expect(seen == unseen && seen.Contains("repair_bay") && !seen.Contains("@move"),
                "同一组操作（搬迁维修台、拖一条传送带、放一座发电机）经建造模式（观察）与直接调服务（不观察）各跑 90 游戏秒：建筑、废料、传送带、已探索区域逐字段一致（FGR-BASE-021）"
                + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── L. 让位（FG-GAP-015）──────────────────────────────────────────────────────

        private static void CheckGiveWay()
        {
            CampaignState s = NewWorld(4601, observe: false, out HomeValleyController home);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell spot = FindValid(s, "generator_2", new GridCell(core.X - 8, core.Y + 26), 10) ?? new GridCell(0, 30);
            GroundItemRecord pile = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, new Vector2(spot.X, spot.Y), CampaignEconomyLedger.ResourceScrap, 9, "fgbuild:pile");
            int machineId = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, HomeValleyLayout.RegionId,
                new Vector2(spot.X + 1, spot.Y), 100f, 100f).LogicId;
            WorldSimulation.StepMany(2);
            GridOpResult r = HomeGridService.TryPlace(s, "generator_2", spot, 0);
            var cells = new List<GridCell>();
            HomeGridService.FootprintOf(HomeGridService.FindBuilding(s, r.BuildingId), cells);
            Vector2? live = WorldSimulation.LivePosition(machineId);
            bool pileOut = !cells.Contains(GridCell.FromWorld(pile.Position)) && pile.Amount == 9;
            bool machineOut = live != null && !cells.Contains(GridCell.FromWorld(live.Value)) && HomeGridService.LastMachinesPushed >= 1;
            Expect(r.Success && pileOut && machineOut,
                $"在地面物与机器上放建筑：9 废料的地面物挪到 {GridCell.FromWorld(pile.Position)}，机器挪到 {(live != null ? GridCell.FromWorld(live.Value).ToString() : "?")}（都在占地外，FG-GAP-015）");
        }

        // ── M. 界面 ───────────────────────────────────────────────────────────────

        private static void CheckHudAndLayout()
        {
            CampaignState s = NewHome(4701);
            HomeValleyBuildMode mode = NewMode();
            var go = new GameObject("__fgbuild_hud") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                InputRouter.SetScope(InputScope.Strategy);
                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/UiKit/BuildModeHud.uxml");
                VisualElement root = vta.CloneTree();
                BuildModeHudUIToolkit hud = go.AddComponent<BuildModeHudUIToolkit>();
                hud.BindView(root);
                mode.Open();
                hud.Refresh();
                bool tabs = hud.CategoryCount == 12 && hud.SelectedCategoryId == "logistics" && hud.CategoryText(0).Contains("物流（15）") && hud.ItemCount == 15
                            && hud.ItemText(0).Contains("传送带 T1") && hud.ItemText(0).Contains("每格 1 废料"); // FG3-LOG-03：T1 / T2 / T3 三级；FG3-LOG-04：+ 分流器、合流器、三级地下带；FG3-LOG-05：+ 两级管线、泵、储罐、阀门；FG4-ECO-04：+ 两级地下管线
                Expect(tabs, $"建造菜单：12 个分类页签（“{hud.CategoryText(0)}”），默认物流，条目“{hud.ItemText(0).Replace("\n", " ")}”");

                hud.SelectCategory("energy");
                bool energy = hud.ItemCount == 6 && hud.ItemText(0).Contains("60 废料") && hud.ItemText(0).Contains("40 秒") && hud.ItemText(0).Contains("已有"); // FG3-LOG-06：能源页签 = 发电机 2 + 电塔 T1 / T2；FG4-ECO-04：+ 燃油发电机、太阳能阵列、储能站
                hud.SelectCategory("endgame");
                bool locked = hud.ItemCount == 1 && hud.ItemText(0).Contains("尚未解锁");
                hud.SelectCategory("defense");
                bool emptyCat = hud.ItemCount == 0 && hud.EmptyText.Contains("暂时没有");
                Expect(energy && locked && emptyCat, $"分类切换：能源条目写成本、工期、已有数量；终局的信标写“尚未解锁”；空分类说明“{hud.EmptyText}”");

                hud.SetSearch("中继");
                bool search = hud.ItemCount == 1 && hud.ItemId(0) == "signal_relay" && hud.CaptionText.Contains("搜索");
                hud.SetSearch("没有这种东西");
                bool noMatch = hud.ItemCount == 0 && hud.EmptyText.Contains("没有找到");
                hud.SelectCategory("logistics");
                bool cleared = hud.ItemCount == 15 && hud.ItemId(0) == "belt_t1";
                Expect(search && noMatch && cleared, $"搜索“中继”→ 1 项（“{hud.CaptionText}”）；查不到给说明；点分类页签清掉搜索回到分类");

                mode.Select("generator_2");
                s.Scrap = 20;
                hud.Refresh();
                bool costShort = hud.CostLabelText.Contains("成本 60 废料") && hud.CostLabelText.Contains("还差 40");
                s.Scrap = 500;
                mode.SetRelocateMode(true);
                hud.Refresh();
                bool modes = hud.ModeText.Contains("搬迁") && hud.GridToggleText.Contains("格线") && hud.UndoHintText.Contains("Ctrl");
                Expect(costShort && modes, $"HUD：选中建筑显示成本与库存（不够写还差多少：“{hud.CostLabelText}”）；搬迁模式标题“{hud.ModeText}”；格线按钮“{hud.GridToggleText}”；撤销提示");

                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { 0.8f, 1f, 1.5f })
                    {
                        foreach (string rootName in new[] { "BuildPanel", "BuildHotbar" })
                        {
                            string result = UiToolkitLayoutProbe.Probe("Assets/GameRes/Raw/UI/UiKit/BuildModeHud.uxml", rootName, stressFill: true,
                                prepare: r =>
                                {
                                    var probeGo = new GameObject("__probe_build") { hideFlags = HideFlags.HideAndDontSave };
                                    BuildModeHudUIToolkit h = probeGo.AddComponent<BuildModeHudUIToolkit>();
                                    h.BindView(r.panel.visualTree);
                                    h.Refresh();
                                    Object.DestroyImmediate(probeGo);
                                }, uiScale: scale);
                            bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                            Expect(pass, $"布局探针 BuildModeHud.uxml#{rootName} [{lang}] 缩放 {scale:0.#}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(500, result.Length)))}");
                        }
                    }
                }
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
            finally
            {
                Object.DestroyImmediate(go);
                EndMode(mode);
            }
        }

        // ── N. 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewHome(4801, withHauler: false);
            GridCell core = HomeGridService.CorePivot(s);
            var sw = new Stopwatch();
            int max = GridContent.TuningInt("grid.drag_max_cells");
            var plan = new BeltPathPlan();
            HomeGridService.PlanBeltPath(s, "belt_t1", new GridCell(core.X - 20, core.Y + 20), new GridCell(core.X + 20, core.Y + 30), BeltDir.East, plan);
            sw.Restart();
            for (int i = 0; i < 50; i++)
            {
                HomeGridService.PlanBeltPath(s, "belt_t1", new GridCell(core.X - 20, core.Y + 20), new GridCell(core.X - 20 + max / 2, core.Y + 20 + max / 2 - 1), BeltDir.East, plan);
            }
            double beltMs = sw.Elapsed.TotalMilliseconds / 50;
            PerfLines.Add($"拖拽规划 {plan.Length} 格：{beltMs:F3} ms / 次（只在拖拽终点换格时算）");
            ExpectPerf(true, $"拖拽规划最长一笔（{plan.Length} 格）{beltMs:F3} ms < 8 ms", PerfGate.Lt(beltMs, 8.0, "拖拽规划 ms"));

            // 框选规划：7 座 vs 807 座建筑。
            double BoxMs(CampaignState st)
            {
                var box = new DemolishBoxPlan();
                sw.Restart();
                for (int i = 0; i < 20; i++)
                {
                    HomeGridService.PlanDemolishBox(st, new GridCell(core.X - 30, core.Y - 30), new GridCell(core.X + 30, core.Y + 30), box);
                }
                return sw.Elapsed.TotalMilliseconds / 20;
            }
            double small = BoxMs(s);
            CampaignState big = NewHome(4802, withHauler: false);
            var records = new List<BuildingRecord>(big.BuildingRecords);
            for (int i = 0; i < 800; i++)
            {
                int x = 200 + (i % 40) * 4;
                int y = 200 + (i / 40) * 4;
                records.Add(new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":generator_2#p" + i, BuildingTypeId = "generator_2", RegionId = HomeValleyLayout.RegionId,
                    GridX = x, GridY = y, Position = new Vector2(x, y), ConstructionState = BuildingConstructionState.Operational, Health = 100f,
                    Inventory = Array.Empty<CargoEntry>(), QueueIds = Array.Empty<string>(),
                });
            }
            big.BuildingRecords = records.ToArray();
            HomeGridService.MapFor(big);
            double large = BoxMs(big);
            PerfLines.Add($"框选规划（61×61 框）：7 座建筑 {small:F3} ms，807 座建筑 {large:F3} ms（O(建筑数)，只在框的终点换格时算）");
            ExpectPerf(true, $"框选规划在 807 座建筑下 {large:F3} ms < 10 ms（FG03 第 7 节后期 800 座）", PerfGate.Lt(large, 10.0, "框选规划 ms"));

            sw.Restart();
            var list = new List<BuildEntry>();
            for (int i = 0; i < 200; i++)
            {
                BuildCatalog.List("logistics", "传送", list);
            }
            double searchMs = sw.Elapsed.TotalMilliseconds / 200;
            PerfLines.Add($"建造菜单搜索：{searchMs:F4} ms / 次（{BuildCatalog.All.Count} 个条目）");
            ExpectPerf(true, $"建造菜单搜索 {searchMs:F4} ms < 1 ms", PerfGate.Lt(searchMs, 1.0, "搜索 ms"));

            CheckExplorationPerformance();
        }

        /// <summary>机器探索在 1000 个已探索圆下：每步开销（常见 / 最坏）、单次揭示 + 窗口区块遮罩重算、重画了几个区块。</summary>
        private static void CheckExplorationPerformance()
        {
            CampaignState ex = NewHome(4803, withHauler: false);
            HomeGridMap map = HomeGridService.MapFor(ex);
            GridCell core = HomeGridService.CorePivot(ex);
            var circles = new List<ExploredAreaRecord>(ex.Grid.Explored);
            for (int i = 0; circles.Count < 1000; i++)
            {
                circles.Add(new ExploredAreaRecord { CenterX = core.X + 400 + (i % 40) * 8, CenterY = core.Y + 400 + (i / 40) * 8, Radius = 12 });
            }
            ex.Grid.Explored = circles.ToArray();
            map.SetExplored(ex.Grid.Explored);
            var sw = new Stopwatch();
            var home = new Vector2(core.X, core.Y);
            MachineExploration.TryRevealAround(ex, home);
            const int N = 20000;
            sw.Restart();
            for (int i = 0; i < N; i++)
            {
                MachineExploration.TryRevealAround(ex, home);
            }
            double steadyUs = sw.Elapsed.TotalMilliseconds * 1000.0 / N;
            var fogCell = new GridCell(core.X - 300, core.Y - 300);
            sw.Restart();
            for (int i = 0; i < N; i++)
            {
                map.IsExploredNoLoad(fogCell);
            }
            double worstUs = sw.Elapsed.TotalMilliseconds * 1000.0 / N;

            // 单次揭示：先把揭示点周围 9×9 个区块装进来（相当于叠加层窗口），记下遮罩版本，再揭示并重算全部已装区块。
            GridCell spot = new GridCell(core.X + 70, core.Y + 5);
            int size = map.ChunkSize;
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    map.ChunkAt(new GridCell(spot.X + dx * size, spot.Y + dy * size), out _);
                }
            }
            List<HomeGridMap.Chunk> loaded = map.LoadedChunks.ToList();
            foreach (HomeGridMap.Chunk c in loaded)
            {
                map.TryGetLoaded(c.ChunkX, c.ChunkY);
            }
            Dictionary<long, int> revs = loaded.ToDictionary(c => HomeGridMap.Key(c.ChunkX, c.ChunkY), c => c.ExploredMaskRevision);
            int areasBefore = ex.Grid.Explored.Length;
            sw.Restart();
            bool revealed = MachineExploration.TryRevealAround(ex, new Vector2(spot.X, spot.Y));
            foreach (HomeGridMap.Chunk c in loaded)
            {
                map.TryGetLoaded(c.ChunkX, c.ChunkY);
            }
            double revealMs = sw.Elapsed.TotalMilliseconds;
            int changed = loaded.Count(c => c.ExploredMaskRevision != revs[HomeGridMap.Key(c.ChunkX, c.ChunkY)]);
            int radius = GridContent.TuningInt("grid.explore_machine_radius");
            int maxTouched = (2 * radius / size + 2) * (2 * radius / size + 2);
            PerfLines.Add($"机器探索（{areasBefore} 个已探索圆）：常见一步 {steadyUs:F3} µs，最坏一步（遍历全部圆）{worstUs:F3} µs；" +
                          $"单次揭示 + {loaded.Count} 个已装区块遮罩重算 {revealMs:F2} ms，只有 {changed} 个区块遮罩变了（叠加层只重画这几块）");
            Expect(revealed && ex.Grid.Explored.Length == areasBefore + 1 && changed >= 1 && changed <= maxTouched && changed < loaded.Count,
                $"揭示一个半径 {radius} 的圆只让被圆碰到的 {changed} 个区块（上限 {maxTouched}）换遮罩版本，其余 {loaded.Count - changed} 个不重画");
            ExpectPerf(true,
                $"1000 个圆下：常见一步 {steadyUs:F3} µs < 20 µs，最坏一步 {worstUs:F3} µs < 200 µs，单次揭示连重算 {revealMs:F2} ms < 50 ms（只在走进迷雾那一步发生）",
                PerfGate.Lt(steadyUs, 20.0, "常见一步 µs"), PerfGate.Lt(worstUs, 200.0, "最坏一步 µs"), PerfGate.Lt(revealMs, 50.0, "揭示连重算 ms"));
        }

        // ── 小工具 ───────────────────────────────────────────────────────────────

        private static void Frame(FakeReader reader)
        {
            reader.EndFrame();
            InputRouter.DebugClearConsumedKeys();
        }

        private sealed class FakeReader : IInputReader
        {
            private readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();
            public readonly HashSet<int> MouseDown = new HashSet<int>();
            public readonly HashSet<int> MouseUp = new HashSet<int>();

            public void Press(KeyCode key) => _down.Add(key);

            public void EndFrame()
            {
                _down.Clear();
                MouseDown.Clear();
                MouseUp.Clear();
            }

            public bool GetKey(KeyCode key) => _down.Contains(key);
            public bool GetKeyDown(KeyCode key) => _down.Contains(key);
            public bool GetMouseButtonDown(int button) => MouseDown.Contains(button);
            public bool GetMouseButtonUp(int button) => MouseUp.Contains(button);
            public Vector3 MousePosition => Vector3.zero;
            public float MouseScrollDelta => 0f;
        }

        private static string LocateRepo()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "cell_tables", "check_luban.py")))
                {
                    return dir.FullName;
                }
            }
            return Directory.GetCurrentDirectory();
        }

        private static (int code, string output) RunPython(string root, string args)
        {
            var psi = new ProcessStartInfo("python", args)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using (Process proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    var stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(120000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { }
                        return (-1, $"python {args} 超过 120 秒没有结束");
                    }
                    return (proc.ExitCode, stdout.Result + stderr.Result);
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                return (-1, $"无法启动 python：{e.Message}");
            }
        }

        private static string Tail(string output) =>
            string.Join(" / ", output.Replace("\r", string.Empty).Split('\n').Where(l => l.Length > 0).Reverse().Take(2).Reverse());

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

        /// <summary>FG-TOOL-01：性能断言只测一次；超阈值不到 2 倍记性能警告（不计失败），超 2 倍才失败。功能条件放 <paramref name="ok"/>。</summary>
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
