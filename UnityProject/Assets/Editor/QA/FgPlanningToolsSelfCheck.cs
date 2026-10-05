using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
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
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG3-LOG-07 规划工具的自动验收（FG03 FGR-LOG-005、009～011；FGT-LOG-004“撤销和重做 50 步，状态一致”、FGT-LOG-013“布局库：保存、放置、跨存档可用”；
    /// 卡片必须同时交付“所有工具的快捷键可重绑”“撤销已完工建筑时生成拆除任务并提示”；负向“粘贴到非法位置的部分给出明确标记”“布局里包含未解锁的建筑”）。
    /// 全部起真实系统：真实世界模拟与家园（WorldSimulation.LoadHome：建造模式、机器取料施工、拆除工单都在世界步里跑）、真实内核（传送带 / 管线 / 电网）、
    /// 真实存读档文件、真实布局库文件（临时目录）、真 UXML 面板。
    /// D 数据（升级路线逐字段、调参、动作表 9 个动作已接入与默认键、文本中英、钩子、图鉴、通知类型）；
    /// C 复制粘贴（框选复制连同设置、粘贴预览、整体旋转、合法部分放下 + 非法部分红叉与原因、机器施工后设置生效、未解锁 / 未知条目、流体接错、阀门相连、地下传送带旋转）；
    /// U 撤销重做（50 步逐步状态一致、栈深、已完工建筑 → 拆除任务 + 通知、已建成物流件立即拆掉全额返还、搬迁撤销、拆除标记撤销、真实按键、存读档后接着撤销、单步过大不进栈）；
    /// G 升级规划（传送带 / 地下 / 管线 / 电塔原地升级、差额收费与守恒、设置保留、最高等级与升级中的负向、取消退回）；
    /// E 吸管与复制设置（带朝向与设置、兼容类型、不兼容原因、撤销）；L 布局库（真文件、跨存档、缩略图、改名 / 导出导入 / 删除确认、库满、坏文件、面板真 UXML + 布局探针）；
    /// K 快捷键可重绑（9 个动作逐个改键：旧键失效、新键生效）；T 暂停 / 0.5x～3x / 观察一致；P 性能（1024 件粘贴预览、复制、撤销）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgPlanningToolsSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 规划工具")]
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
            Line("\n[规划工具] 复制粘贴 / 布局库 / 撤销重做 50 步 / 升级规划 / 吸管 / 复制设置 / 快捷键可重绑（FG3-LOG-07）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgplan-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                LayoutLibrary.DirectoryOverrideForTests = Path.Combine(_dir, "player-config");
                LayoutLibrary.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                HomeValleyPowerGrid.ResetForTests();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "规划工具全在热更层（Editor 下 Mono JIT，真机走 HybridCLR 解释执行，真机另测：FG3-LOG-09 / FG15-SYS-02），只在玩家操作时运行，每帧开销与建筑数无关");

                Step(CheckData);
                Step(CheckCopyPaste);
                Step(CheckPasteNegative);
                Step(CheckPasteFluids);
                Step(CheckUndoFiftySteps);
                Step(CheckUndoCompleted);
                Step(CheckUndoMisc);
                Step(CheckUndoSaveLoad);
                Step(CheckUpgrade);
                Step(CheckUpgradeNegative);
                Step(CheckEyedropperAndSettings);
                Step(CheckLayoutLibrary);
                Step(CheckLayoutPanel);
                Step(CheckRebind);
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
                Fail($"规划工具自检抛异常：{e}");
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
                HomeValleyBuildMode.SetClipboardForTests(null);
                LayoutLibrary.DirectoryOverrideForTests = null;
                LayoutLibrary.Reload();
                HomeValleyPowerGrid.ResetForTests();
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
                HomeValleyWorkOrders.ResetSessionState();
                LayoutLibraryPanelUIToolkit.InWorldOverrideForTests = false;
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
            Line($"  · [规划工具] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 世界与夹具 ─────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 600)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            HomeValleyPowerGrid.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgplan-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = scrap;
            InputRouter.Reset();
            InputRouter.SetScope(InputScope.Strategy);
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
                WorldSimulation.StepMany(Math.Max(1, GameClock.StepHz / 4));
            }
            return done();
        }

        private static bool CellFree(CampaignState s, GridCell c) =>
            HomeGridService.ValidateBeltCell(s, c).Ok && HomeGridService.ValidatePipeCell(s, c, PipePieceKind.Pipe).Ok
            && HomeGridService.ValidatePlacement(s, "power_pole", c, 0, checkCost: false).Ok;

        /// <summary>按格网规则找一块 w×h 的空地（B25：不写死坐标）；左下角返回。<paramref name="avoid"/> 里的格子不算空（给后续步骤留地方）。</summary>
        private static GridCell? FreeRect(CampaignState s, int w, int h, float fromCore, float toCore, HashSet<GridCell> avoid = null)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (int a = 0; a < 72; a++)
            {
                float ang = a * 5f * Mathf.Deg2Rad;
                for (float d = fromCore; d <= toCore; d += 2f)
                {
                    var o = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * d), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * d));
                    bool ok = true;
                    for (int y = -1; y <= h && ok; y++)
                    {
                        for (int x = -1; x <= w && ok; x++)
                        {
                            var c = new GridCell(o.X + x, o.Y + y);
                            ok = CellFree(s, c) && (avoid == null || !avoid.Contains(c));
                        }
                    }
                    if (ok)
                    {
                        return o;
                    }
                }
            }
            return null;
        }

        private static void Reserve(HashSet<GridCell> avoid, GridCell o, int w, int h)
        {
            for (int y = -1; y <= h; y++)
            {
                for (int x = -1; x <= w; x++)
                {
                    avoid.Add(new GridCell(o.X + x, o.Y + y));
                }
            }
        }

        private static GridCell At(GridCell o, int x, int y) => new GridCell(o.X + x, o.Y + y);

        /// <summary>要让布局里的 (0, 0) 落在 <paramref name="origin"/>，光标（布局中心格）该放哪（中心 = 条目边界的整数中点，与粘贴同一口径）。</summary>
        private static GridCell HoverFor(PlanEntryBlock block, GridCell origin)
        {
            PlanEntries.Bounds(block, out int minX, out int minY, out int maxX, out int maxY);
            return new GridCell(origin.X + GridMath.FloorDiv(minX + maxX, 2), origin.Y + GridMath.FloorDiv(minY + maxY, 2));
        }

        private static BuildingRecord AddBuilt(CampaignState s, string typeId, string key, GridCell pivot, int priority = 0)
        {
            BuildingGrid g = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":selfcheck_" + key,
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = pivot.X,
                GridY = pivot.Y,
                Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, 0),
                Health = 100f,
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = priority > 0 ? priority : prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
                InvestedScrap = HomeValleyLayout.BuildProfile.TryGetValue(typeId, out (int ScrapCost, float Seconds) bp) ? bp.ScrapCost : 0,
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            return r;
        }

        /// <summary>
        /// 源场景（测试捷径：经各服务的正式“建成”入口直接放已建成的件；机器施工由各自 Story 的自检覆盖，本 Story 的粘贴 → 施工路径下面真跑）：
        /// y=0：传送带 T1 ×4 朝东 + 分流器（3:1、优先左口）+ 传送带；y=2：电塔 T1 + 管线 T1 ×3 + 储罐（只进、优先级 3）；y=4：地下传送带 T1（入口 x=0 → 出口 x=3，朝东）。
        /// </summary>
        private static bool LaySource(CampaignState s, GridCell o, out BuildingRecord pole)
        {
            pole = null;
            bool ok = true;
            for (int x = 0; x < 4; x++)
            {
                ok &= BeltNetworkService.TryPlace(s, At(o, x, 0), BeltDir.East, 0).Ok;
            }
            ok &= BeltNetworkService.TryPlaceNode(s, At(o, 4, 0), BeltDir.East, 2, BeltNodeKind.Splitter).Ok;
            ok &= BeltNetworkService.TrySetSplitter(s, At(o, 4, 0), 3, 1, BeltSide.Left, BeltConst.FilterAny, BeltConst.FilterAny).Ok;
            ok &= BeltNetworkService.TryPlace(s, At(o, 5, 0), BeltDir.East, 0).Ok;
            pole = AddBuilt(s, "power_pole", "src_pole", At(o, 0, 2));
            for (int x = 2; x <= 4; x++)
            {
                ok &= PipeNetworkService.TryPlace(s, At(o, x, 2), PipePieceKind.Pipe, 0, 0).Ok;
            }
            ok &= PipeNetworkService.TryPlace(s, At(o, 5, 2), PipePieceKind.Tank, 0, 0).Ok;
            ok &= PipeNetworkService.TrySetTankMode(s, At(o, 5, 2), PipeTankMode.InOnly).Ok;
            ok &= PipeNetworkService.TrySetTankPriority(s, At(o, 5, 2), 3).Ok;
            ok &= BeltNetworkService.TryPlaceUnderground(s, At(o, 0, 4), BeltDir.East, 0, 3).Ok;
            return ok;
        }

        /// <summary>规划状态的指纹（不含实例 ID——撤销后放回的虚影是新 ID）：建筑（类型 / 枢轴 / 朝向 / 状态 / 优先级 / 是否标记拆除）、
        /// 规划中的物流件（格、方向、等级、种类、节点 / 管线设置）、内核传送带与管线格数、库存。</summary>
        private static string PlanSnapshot(CampaignState s)
        {
            var parts = new List<string>();
            foreach (BuildingRecord b in s.BuildingRecords.Where(b => b.RegionId == HomeValleyLayout.RegionId))
            {
                parts.Add($"B:{b.BuildingTypeId}@{b.GridX},{b.GridY}/{GridMath.NormalizeRotation(b.Rotation)}:{b.ConstructionState}:{b.PowerPriority}:{(HomeGridService.IsMarkedForDemolish(s, b.BuildingId) ? "X" : "-")}");
            }
            foreach (PlannedBeltRecord p in s.Grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>())
            {
                for (int i = 0; i < p.Xs.Length; i++)
                {
                    if (p.CellState[i] == 0)
                    {
                        parts.Add($"P:{p.Xs[i]},{p.Ys[i]}:{p.Dirs[i]}:{p.Tier}:{p.NodeKind}:{p.PipePiece}:{p.Upgrade}:{p.RatioL}/{p.RatioR}/{p.PriorityOut}/{p.PriorityIn}/{p.PipeSettings}");
                    }
                }
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join("|", parts) + $"#belt={BeltNetworkService.Kernel.CellCount}#pipe={PipeNetworkService.Kernel.CellCount}#scrap={s.Scrap}";
        }

        private static int NotifyCount(string typeId) => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == typeId).Sum(n => n.Count);

        private static NotificationEntry LastOf(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId);

        private static bool AllPastedBuilt(CampaignState s) =>
            HomeValleyConstruction.PlannedCellCount(s) == 0 && s.BuildingRecords.All(b => !HomeValleyController.IsPlannedGhost(b));

        /// <summary>框选复制（真实建造模式入口：复制模式 → 按下 → 移到对角 → 松开）。</summary>
        private static void CopyBox(HomeValleyBuildMode mode, CampaignState s, GridCell a, GridCell b)
        {
            mode.SetCopyMode(true);
            mode.PointerDown(s, a);
            mode.SetHover(s, b);
            mode.PointerUp(s, b);
        }

        // ── D 数据 ──────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string root = LocateRepo();
            (int code, string output) = RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] src = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("BU\t", StringComparison.Ordinal)).ToArray();
            string[] rt = ConfigSystem.Instance.Tables.TbBuildUpgrade.DataList.Select(r => string.Join("\t", "BU", r.FromId, r.ToId, r.Note)).ToArray();
            bool routes = GridContent.TryGetUpgrade("belt_t1", out string b2) && b2 == "belt_t2" && GridContent.TryGetUpgrade("power_pole", out string p2) && p2 == "power_pole_t2"
                          && !GridContent.TryGetUpgrade("belt_t3", out _) && !GridContent.TryGetUpgrade("splitter", out _)
                          && GridContent.TryGetUpgrade("pipe_underground_t1", out string u2) && u2 == "pipe_underground_t2"; // FG4-ECO-10（DEBT-FG4ECO04-05）
            Expect(code == 0 && src.Length == 9 && src.SequenceEqual(rt) && routes && GridContent.TryGetUpgrade("barrier_t1", out string w2) && w2 == "barrier_t2", // FG6-DEF-02：+ 屏障 T1→T2→T3
                $"D1 升级路线表 fg.TbBuildUpgrade 与源数据 fgdata_plan.BUILD_UPGRADES 逐字段一致（{src.Length} 行：传送带 T1→T2→T3、地下 T1→T2→T3、管线 T1→T2、地下管线 T1→T2、电塔 T1→T2、屏障 T1→T2→T3；T3 / 分流器没有路线）" + (code == 0 ? string.Empty : "：" + Tail(output)));
            bool tuning = PlanHistory.Depth == 50 && PlanningService.MaxEntries == 1024 && LayoutLibrary.Max == 64
                          && GridContent.TuningInt("plan.preview_max_tiles") == 1024 && GridContent.TuningInt("plan.thumbnail_px") == 64;
            Expect(tuning, "D1 调参入表：撤销栈 50 步（FGR-LOG-009 初值）、一次复制 / 一个布局最多 1024 件、布局库 64 个、粘贴预览最多画 1024 格、缩略图 64 像素");
            var expect = new (GameActionId a, KeyCode k, InputModifier m)[]
            {
                (GameActionId.Eyedropper, KeyCode.Q, InputModifier.None), (GameActionId.UpgradePlan, KeyCode.U, InputModifier.None),
                (GameActionId.Copy, KeyCode.C, InputModifier.Ctrl), (GameActionId.Paste, KeyCode.V, InputModifier.Ctrl),
                (GameActionId.Undo, KeyCode.Z, InputModifier.Ctrl), (GameActionId.Redo, KeyCode.Y, InputModifier.Ctrl),
                (GameActionId.LayoutLibrary, KeyCode.B, InputModifier.Ctrl), (GameActionId.CopySettings, KeyCode.C, InputModifier.Alt),
                (GameActionId.PasteSettings, KeyCode.V, InputModifier.Alt),
            };
            bool actions = expect.All(e => InputActionCatalog.TryGet(e.a, out InputActionDef d) && d.Status == InputActionStatus.Wired
                                           && d.DefaultChord.Key == e.k && d.DefaultChord.Mods == e.m);
            Expect(actions, "D2 9 个规划动作全部已接入（wired）：吸管 Q、升级 U、复制 Ctrl+C、粘贴 Ctrl+V、撤销 Ctrl+Z、重做 Ctrl+Y、布局库 Ctrl+B（FG13 第 5 节）、复制设置 Alt+C / 粘贴设置 Alt+V（补全项）");
            // 文本：C# 源码里用到的全部 plan.* 文本键都在表里，中英都不缺、没有占位标记。
            var keys = new HashSet<string>();
            foreach (string file in Directory.GetFiles(Path.Combine(Application.dataPath, "GameScripts", "HotFix", "GameLogic"), "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\"(plan\\.(?:btn|mode|hint|copy|paste|library|upgrade|eyedrop|settings|reason|undo|redo)\\.[a-z0-9_.]+)\""))
                {
                    keys.Add(m.Groups[1].Value);
                }
            }
            foreach (PlanStepKind k in Enum.GetValues(typeof(PlanStepKind)))
            {
                keys.Add("plan.step." + k.ToString().ToLowerInvariant());
            }
            var missing = keys.Where(k => !GameText.Has(k)).ToList();
            GameSettings.SetLanguage(GameLanguage.En);
            var enBad = keys.Where(k => GameText.Has(k) && (GameText.ContainsMarker(GameText.Get(k)) || GameText.Get(k).Length == 0)).ToList();
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(keys.Count >= 120 && missing.Count == 0 && enBad.Count == 0,
                $"D3 源码里用到的 {keys.Count} 个规划文本键（含 12 个步名）全部在 fg.TbLocText，中英都有（扫到条数下限 120，防正则写坏静默通过）" +
                (missing.Count == 0 ? string.Empty : "——缺：" + string.Join(",", missing.Take(8))) + (enBad.Count == 0 ? string.Empty : "——英文坏：" + string.Join(",", enBad.Take(8))));
            string[] hooks = { GuidanceHooks.BuildFirstCopy, GuidanceHooks.BuildFirstPaste, GuidanceHooks.BuildFirstUndo, GuidanceHooks.BuildFirstUpgrade,
                GuidanceHooks.BuildFirstEyedropper, GuidanceHooks.BuildFirstCopySettings, GuidanceHooks.BuildFirstLayoutSaved, GuidanceHooks.LayoutLibraryFirstOpen };
            bool hookOk = hooks.All(h => GuidanceHooks.Known.Contains(h));
            var codex = ConfigSystem.Instance.Tables.TbCodexEntry;
            bool entry = codex.DataList.Any(x => x.Id == "codex.build.planning" && hooks.All(h => x.Hooks.Contains(h)));
            bool notify = NotificationCatalog.TryGetType("plan_undo", out NotifyTypeDef t) && t.Tier == NotifyLevel.Info;
            Expect(hookOk && entry && notify, "D4 8 个引导钩子（第一次复制 / 粘贴 / 撤销 / 升级 / 吸管 / 复制设置 / 存布局 / 打开布局库，内容在 FG15-UX-04）；图鉴“规划工具”由它们解锁；“撤销与重做”信息级通知类型");
        }

        // ── C 复制粘贴 ──────────────────────────────────────────────────────────────

        private static void CheckCopyPaste()
        {
            CampaignState s = NewWorld(9701, scrap: 800);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            var avoid = new HashSet<GridCell>();
            GridCell? src = FreeRect(s, 14, 5, 12f, 40f, avoid);
            if (src == null)
            {
                Fail("C 找不到源场景的空地");
                return;
            }
            Reserve(avoid, src.Value, 14, 5);
            GridCell? dst = FreeRect(s, 10, 5, 12f, 40f, avoid);
            if (dst != null)
            {
                Reserve(avoid, dst.Value, 10, 5);
            }
            GridCell? rot = FreeRect(s, 5, 10, 12f, 40f, avoid);
            GridCell o = src.Value;
            if (dst == null || rot == null || !LaySource(s, o, out BuildingRecord pole))
            {
                Fail("C 放不下源场景 / 找不到粘贴目标");
                return;
            }
            try
            {
                mode.Open();
                int hooks0 = GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildFirstCopy) ? 1 : 0;
                CopyBox(mode, s, o, At(o, 9, 4));
                PlanEntryBlock clip = HomeValleyBuildMode.Clipboard;
                int n = PlanEntries.CountOf(clip);
                int splitIdx = n > 0 ? Array.FindIndex(clip.Ids, id => id == "splitter") : -1;
                int tankIdx = n > 0 ? Array.FindIndex(clip.Ids, id => id == "tank") : -1;
                int underIdx = n > 0 ? Array.FindIndex(clip.Ids, id => id == "underground_t1") : -1;
                bool settings = splitIdx >= 0 && clip.S0[splitIdx] == PlanSettings.PackSplitter(3, 1, 1) && tankIdx >= 0 && clip.S0[tankIdx] == PlanSettings.PackTank(PipeTankMode.InOnly, 3)
                                && underIdx >= 0 && clip.X2s[underIdx] - clip.Xs[underIdx] == 3 && clip.Rots[underIdx] == (int)BeltDir.East;
                Expect(n == 12 && settings && mode.PasteMode && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildFirstCopy),
                    $"C1 复制模式拖框（真实建造模式入口）：复制 {n} 件（电塔 1 + 传送带 5 + 分流器 1 + 管线 3 + 储罐 1 + 地下传送带整条 1 = 12）连同设置（分流器 3:1 优先左口、储罐只进 / 优先级 3、地下出口相距 3）；复制完直接进入粘贴");
                // C2 粘贴预览：布局中心落在光标下，全部合法。
                GridCell hover = HoverFor(clip, dst.Value);
                mode.SetHover(s, hover);
                mode.RefreshPreview(s);
                mode.RefreshVisuals(s);
                PastePlan pv = mode.PastePreview;
                Expect(pv != null && pv.OkCount == 12 && pv.BadCount == 0 && mode.ActiveTileCount == 13 && mode.ActiveMarkCount == 0
                       && BuildModeHudText().Contains("能放 12 件"),
                    $"C2 粘贴预览跟着鼠标：12 件全部能放（绿格 {mode.ActiveTileCount} 个、红叉 {mode.ActiveMarkCount} 个）；建造栏写“{BuildModeHudText().Split('\n').FirstOrDefault()}”");
                // C3 放下：一次粘贴 = 一步；建筑虚影、物流件虚影（设置写进规划）。
                int steps0 = PlanHistory.UndoSteps(s);
                mode.PointerDown(s, hover);
                PlanApplyResult pr = mode.LastPaste;
                BuildingRecord ghostPole = HomeGridService.BuildingAt(s, At(dst.Value, 0, 2));
                HomeValleyConstruction.TryFindPlannedCell(s, At(dst.Value, 4, 0), out PlannedBeltRecord sp, out _);
                HomeValleyConstruction.TryFindPlannedCell(s, At(dst.Value, 5, 2), out PlannedBeltRecord tk, out _);
                HomeValleyConstruction.TryFindPlannedCell(s, At(dst.Value, 0, 4), out PlannedBeltRecord ug, out _);
                bool ghosts = pr != null && pr.PlacedBuildings == 1 && pr.PlacedPieces == 11 && ghostPole != null && ghostPole.BuildingTypeId == "power_pole"
                              && HomeValleyController.IsPlannedGhost(ghostPole)
                              && sp != null && sp.NodeKind == (int)BeltNodeKind.Splitter && sp.RatioL == 3 && sp.RatioR == 1 && sp.PriorityOut == 1
                              && tk != null && tk.PipeSettings == PlanSettings.PackTank(PipeTankMode.InOnly, 3)
                              && ug != null && ug.NodeKind == (int)BeltNodeKind.UndergroundIn && ug.Xs[1] == dst.Value.X + 3 && ug.Ys[1] == dst.Value.Y + 4;
                Expect(ghosts && PlanHistory.UndoSteps(s) == steps0 + 1 && PlanHistory.PeekUndo(s) == PlanStepKind.Paste && mode.PasteMode,
                    $"（放下 {pr?.PlacedBuildings}+{pr?.PlacedPieces}，电塔 {ghostPole?.BuildingTypeId}，分流器规划 {sp?.RatioL}:{sp?.RatioR}，储罐设置 {tk?.PipeSettings}，地下 {ug?.Xs?.Length}，步数 {PlanHistory.UndoSteps(s) - steps0}）" +
                    $"C3 左键放下：1 座建筑虚影 + 11 件物流件虚影（分流器 / 储罐的设置写进规划，建成时生效），整次粘贴是撤销栈里的一步（{PlanHistory.StepName(PlanHistory.PeekUndo(s))}）；粘贴模式保持，可以接着贴");
                // C4 旋转：整体顺时针 90°。
                mode.RotateGhost();
                GridCell rh = At(rot.Value, 2, 5); // 布局中心 (2, 2)：转 90° 后 x' = y − 2 ∈ [−2, 2]，y' = 2 − x ∈ [−3, 2]
                mode.SetHover(s, rh);
                mode.RefreshPreview(s);
                PastePlan rp = mode.PastePreview;
                PasteItem rSplit = rp?.Items.FirstOrDefault(i => i.Id == "splitter");
                PasteItem rUnder = rp?.Items.FirstOrDefault(i => i.Id == "underground_t1");
                PasteItem rPole = rp?.Items.FirstOrDefault(i => i.Id == "power_pole");
                bool rotated = rp != null && rp.Quarter == 1 && rp.OkCount == 12
                               && rSplit != null && rSplit.Cell == At(rh, -2, -2) && rSplit.Rot == (int)BeltDir.South
                               && rUnder != null && rUnder.Cell == At(rh, 2, 2) && rUnder.Cell2 == At(rh, 2, -1) && rUnder.Rot == (int)BeltDir.South
                               && rPole != null && rPole.Cell == At(rh, 0, 2) && rPole.Rot == 90;
                mode.PointerDown(s, rh);
                HomeValleyConstruction.TryFindPlannedCell(s, At(rh, -2, -2), out PlannedBeltRecord rsp, out _);
                Expect(rotated && rsp != null && rsp.Dirs[0] == (int)BeltDir.South && rsp.RatioL == 3,
                    "C4 旋转键把整个布局顺时针转 90°（绕布局中心）：分流器 (4, 0) → (−2, −2) 朝南、地下传送带入口 (0, 4) → (2, 2)、出口 (3, 4) → (2, −1) 朝南、电塔朝向 90°；放下后规划与预览一致、设置照带" +
                    $"（分流器 {rSplit?.Cell.X - rh.X},{rSplit?.Cell.Y - rh.Y}/{rSplit?.Rot}，地下 {rUnder?.Cell.X - rh.X},{rUnder?.Cell.Y - rh.Y}→{rUnder?.Cell2.X - rh.X},{rUnder?.Cell2.Y - rh.Y}，电塔 {rPole?.Cell.X - rh.X},{rPole?.Cell.Y - rh.Y}/{rPole?.Rot}，能放 {rp?.OkCount}）");
                mode.RightClick();
                bool left = !mode.PasteMode && mode.IsOpen;
                // C5 机器施工：粘贴的虚影建成后设置生效。
                int scrap0 = s.Scrap;
                bool built = StepUntil(() => AllPastedBuilt(s), 900);
                bool splitterSet = BeltNetworkService.TryGetNode(At(dst.Value, 4, 0), out BeltNodeInfo node) && node.Kind == BeltNodeKind.Splitter && node.RatioL == 3 && node.RatioR == 1
                                   && node.PriorityOut == BeltSide.Left;
                bool tankSet = PipeNetworkService.Kernel.TryGetCellInfo(dst.Value.X + 5, dst.Value.Y + 2, out PipeCellInfo tank) && tank.TankMode == PipeTankMode.InOnly && tank.Priority == 3;
                bool underOk = BeltNetworkService.TryGetNode(At(dst.Value, 0, 4), out BeltNodeInfo un) && un.Kind == BeltNodeKind.UndergroundIn && un.ExitX == dst.Value.X + 3;
                BuildingRecord builtPole = HomeGridService.BuildingAt(s, At(dst.Value, 0, 2));
                Expect(left && built && splitterSet && tankSet && underOk && builtPole?.ConstructionState == BuildingConstructionState.Operational && s.Scrap < scrap0,
                    $"C5 右键退出粘贴；机器取料施工（库存 {scrap0} → {s.Scrap}）：粘贴的分流器建成后是 3:1 优先左口、储罐只进 / 优先级 3、地下传送带整条、电塔运转——设置随虚影生效（FGR-LOG-005“包括建筑的设置”）");
            }
            finally
            {
                mode?.Close();
            }
        }

        private static string BuildModeHudText()
        {
            HomeValleyBuildMode m = HomeValleyBuildMode.Current;
            PastePlan pp = m?.PastePreview;
            return pp == null ? string.Empty : GameText.Format("plan.paste.preview", pp.OkCount, pp.BadCount, pp.Cost, pp.Stock);
        }

        private static void CheckPasteNegative()
        {
            CampaignState s = NewWorld(9702, scrap: 400);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            var avoid = new HashSet<GridCell>();
            GridCell? src = FreeRect(s, 16, 5, 12f, 40f, avoid);
            if (src == null || !LaySource(s, src.Value, out _))
            {
                Fail("C6 放不下源场景");
                return;
            }
            GridCell o = src.Value;
            try
            {
                mode.Open();
                CopyBox(mode, s, o, At(o, 9, 4));
                // 往右挪 3 格贴：和源场景重叠的件不合法（被占用），其余合法。
                GridCell hover = At(HoverFor(HomeValleyBuildMode.Clipboard, o), 3, 0);
                mode.SetHover(s, hover);
                mode.RefreshPreview(s);
                mode.RefreshVisuals(s);
                PastePlan pv = mode.PastePreview;
                int bad = pv?.BadCount ?? -1;
                bool reasons = pv != null && pv.Items.Where(i => !i.Ok).All(i => i.Reason != null)
                               && pv.Items.Where(i => !i.Ok).Any(i => i.Reason.Value.Code == GridBlockReason.OccupiedBelt || i.Reason.Value.Code == GridBlockReason.OccupiedPipe
                                                                     || i.Reason.Value.Code == GridBlockReason.Occupied);
                string first = pv?.DescribeFirstBad() ?? string.Empty;
                Expect(bad > 0 && pv.OkCount > 0 && reasons && mode.ActiveMarkCount == bad && first.StartsWith("第 ", StringComparison.Ordinal),
                    $"C6 负向“粘贴到非法位置的部分给出明确标记”：挪 3 格贴到源场景上，{bad} 件不合法（被占用），每件都有原因、画红叉 {mode.ActiveMarkCount} 个（颜色 + 形状）；第一处“{first}”");
                int ok = pv.OkCount;
                int belts0 = HomeValleyConstruction.PlannedCellCount(s);
                string before = PlanSnapshot(s);
                mode.PointerDown(s, hover);
                PlanApplyResult r = mode.LastPaste;
                bool partial = r != null && r.Placed == ok && mode.StatusIsError && mode.StatusText.Contains("件没有放") && PlanSnapshot(s) != before;
                Expect(partial, $"C6 左键放下：只放合法的 {r?.Placed} 件（= 预览能放的 {ok} 件），状态行写明“{mode.StatusText.Split('\n').LastOrDefault()}”");
                // 原地贴（全部重叠）：一件都放不了，什么都不变。
                PlanHistory.Undo(s);
                string snap = PlanSnapshot(s);
                mode.StartPaste(s, HomeValleyBuildMode.Clipboard, null);
                GridCell same = HoverFor(HomeValleyBuildMode.Clipboard, o);
                mode.SetHover(s, same);
                mode.RefreshPreview(s);
                int steps = PlanHistory.UndoSteps(s);
                mode.PointerDown(s, same);
                Expect(mode.PastePreview != null && mode.PastePreview.OkCount == 0 && PlanSnapshot(s) == snap && PlanHistory.UndoSteps(s) == steps && mode.StatusText.Contains("一件都放不下"),
                    $"C6 原地贴（全部重叠）：一件都不放、状态不变、不进撤销栈，状态行“{mode.StatusText}”");
                // 负向“布局里包含未解锁的建筑”：信标（摧毁主核心前锁着）+ 本版本不认识的条目 + 一格传送带。
                var b = new PlanEntries.Builder();
                b.Add("beacon", 0, 0, 0);
                b.Add("mystery_building_v9", 4, 0, 0);
                b.Add("belt_t1", 6, 0, 1);
                PlanEntryBlock locked = b.Build();
                GridCell? spot = FreeRect(s, 9, 4, 12f, 44f, null);
                bool started = mode.StartPaste(s, locked, "测试布局");
                string head = mode.StatusText;
                if (spot != null)
                {
                    mode.SetHover(s, At(spot.Value, 3, 1));
                    mode.RefreshPreview(s);
                }
                PastePlan lp = mode.PastePreview;
                PasteItem beacon = lp?.Items.FirstOrDefault(i => i.Id == "beacon");
                PasteItem mystery = lp?.Items.FirstOrDefault(i => i.Kind == PlanEntryKind.Unknown);
                bool marked = lp != null && lp.LockedCount == 1 && lp.UnknownCount == 1 && lp.OkCount == 1 && beacon != null && beacon.Reason?.Code == GridBlockReason.Locked
                              && mystery != null && mystery.Reason?.Code == GridBlockReason.UnknownType && head.Contains("1 件本局还没解锁");
                if (spot != null)
                {
                    mode.PointerDown(s, At(spot.Value, 3, 1));
                }
                Expect(started && marked && mode.LastPaste?.PlacedPieces == 1 && mode.LastPaste.PlacedBuildings == 0,
                    $"C7 负向“布局里包含未解锁的建筑”：开始放置就写明“{head.Split('\n').LastOrDefault()}”；信标标“尚未解锁”、未知条目标“本版本不认识”，两者都画红叉不放，只放下 1 格传送带");
                // 解锁后同一布局里的信标就能放，并带上电力优先级。
                CampaignObjectiveTrackerTestHooks.Complete(s, CampaignObjectiveTracker.Obj09);
                locked.S0[0] = 4;
                GridCell? spot2 = FreeRect(s, 9, 4, 12f, 44f, null);
                mode.StartPaste(s, locked, "测试布局");
                if (spot2 != null)
                {
                    mode.SetHover(s, At(spot2.Value, 3, 1));
                    mode.RefreshPreview(s);
                    mode.PointerDown(s, At(spot2.Value, 3, 1));
                }
                BuildingRecord beaconGhost = s.BuildingRecords.FirstOrDefault(x => x.BuildingTypeId == "beacon");
                Expect(spot2 != null && beaconGhost != null && beaconGhost.PowerPriority == 4 && HomeValleyController.IsPlannedGhost(beaconGhost),
                    $"C7 解锁信标后同一布局能放下信标虚影，电力优先级按布局里的 4（建筑设置随粘贴）");
            }
            finally
            {
                mode?.Close();
            }
        }

        /// <summary>流体规则：粘贴的管线同时挨着两种流体的网络 → 整组标“会接错流体”；阀门直接接着另一个阀门 → 标出来。</summary>
        private static void CheckPasteFluids()
        {
            CampaignState s = NewWorld(9703, scrap: 400);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell? area = FreeRect(s, 9, 4, 12f, 40f, null);
            if (area == null)
            {
                Fail("C8 找不到空地");
                return;
            }
            GridCell o = area.Value;
            int water = PipeNetworkService.FluidId("water");
            int crude = PipeNetworkService.FluidId("crude");
            try
            {
                // 测试捷径：两段已有网络——左边一格水泵（内核直接给来源流体）、右边一格原油泵，中间空两格。
                bool laid = PipeNetworkService.Kernel.Place(o.X, o.Y, PipePieceKind.Pump, 0, 0, water) == PipeResult.Ok
                            && PipeNetworkService.Kernel.Place(o.X + 3, o.Y, PipePieceKind.Pump, 0, 0, crude) == PipeResult.Ok;
                HomeGridService.MapFor(s).SetPipe(o, PipeNetworkService.LayerValue(PipePieceKind.Pump, 0));
                HomeGridService.MapFor(s).SetPipe(At(o, 3, 0), PipeNetworkService.LayerValue(PipePieceKind.Pump, 0));
                var b = new PlanEntries.Builder();
                b.Add("pipe_t1", 0, 0, 0);
                b.Add("pipe_t1", 1, 0, 0);
                PlanEntryBlock bridge = b.Build();
                mode.Open();
                mode.StartPaste(s, bridge, null);
                mode.SetHover(s, At(o, 1, 0)); // 两格落在 (1,0)、(2,0)：一头接水、一头接原油
                mode.RefreshPreview(s);
                PastePlan pv = mode.PastePreview;
                bool conflict = pv != null && pv.BadCount == 2 && pv.Items.All(i => i.Reason?.Code == GridBlockReason.PipeFluidConflict)
                                && pv.DescribeFirstBad().Contains("水") && pv.DescribeFirstBad().Contains("原油");
                Expect(laid && conflict, $"C8 粘贴的一段管线会把水和原油的网络接在一起：整段标“接错流体”不放（“{pv?.DescribeFirstBad()}”）");
                // 阀门接阀门。
                bool valve = PipeNetworkService.TryPlace(s, At(o, 0, 2), PipePieceKind.Valve, 0, (int)BeltDir.East).Ok;
                var vb = new PlanEntries.Builder();
                vb.Add("valve", 0, 0, (int)BeltDir.East);
                mode.StartPaste(s, vb.Build(), null);
                mode.SetHover(s, At(o, 1, 2));
                mode.RefreshPreview(s);
                PastePlan vp = mode.PastePreview;
                Expect(valve && vp != null && vp.BadCount == 1 && vp.Items[0].Reason?.Code == GridBlockReason.PipeValveChained,
                    "C8 粘贴的阀门直接接着已有阀门：标“阀门之间至少隔一格管线”不放");
            }
            finally
            {
                mode?.Close();
            }
        }

        // ── U 撤销重做 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// FGT-LOG-004：暂停中（施工不推进）用真实建造模式入口做 50 步不同的规划操作（放电塔虚影、拖传送带、取消虚影、挪动还没开工的虚影、原地转虚影、
        /// 粘贴、拆掉一格规划中的传送带），每步后记指纹；再逐步撤销 50 次，每一次都回到对应那一步之前的指纹；再逐步重做 50 次，每一次都回到那一步之后的指纹。
        /// </summary>
        private static void CheckUndoFiftySteps()
        {
            CampaignState s = NewWorld(9711, scrap: 5000);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            var snaps = new List<string> { PlanSnapshot(s) };
            var kinds = new List<string>();
            var placed = new List<GridCell>();
            var cursorAvoid = new HashSet<GridCell>();
            GameClock.SetPaused(true);
            InputRouter.SetGameplayPaused(true, strategic: true);
            try
            {
                mode.Open();
                // 粘贴用的小布局：2 格传送带 + 分流器（带设置）。
                var lb = new PlanEntries.Builder();
                lb.Add("belt_t1", 0, 0, 1);
                lb.Add("belt_t1", 1, 0, 1);
                lb.Add("splitter", 2, 0, 1, s0: PlanSettings.PackSplitter(2, 1, 2));
                PlanEntryBlock small = lb.Build();
                int attempts = 0;
                while (kinds.Count < 55 && attempts < 400)
                {
                    attempts++;
                    int op = attempts % 7;
                    int lastBefore = s.Grid.PlanUndo != null && s.Grid.PlanUndo.Count > 0 ? s.Grid.PlanUndo[s.Grid.PlanUndo.Count - 1].Step : 0;
                    string label = null;
                    switch (op)
                    {
                        case 0:
                        {
                            GridCell? c = FreeRect(s, 1, 1, 10f, 44f, cursorAvoid);
                            if (c == null) break;
                            Reserve(cursorAvoid, c.Value, 1, 1);
                            mode.Select("power_pole");
                            mode.ClickCell(s, c.Value);
                            placed.Add(c.Value);
                            label = "放电塔";
                            break;
                        }
                        case 1:
                        {
                            GridCell? c = FreeRect(s, 4, 1, 10f, 44f, cursorAvoid);
                            if (c == null) break;
                            Reserve(cursorAvoid, c.Value, 4, 1);
                            mode.Select("belt_t1");
                            mode.PointerDown(s, c.Value);
                            mode.SetHover(s, At(c.Value, 3, 0));
                            mode.PointerUp(s, At(c.Value, 3, 0));
                            label = "拖传送带";
                            break;
                        }
                        case 2:
                        {
                            GridCell? g = placed.Where(p => HomeGridService.BuildingAt(s, p) is BuildingRecord br && HomeValleyController.IsPlannedGhost(br))
                                .Select(p => (GridCell?)p).LastOrDefault();
                            if (g == null) break;
                            mode.SetDemolishMode(true);
                            mode.ClickCell(s, g.Value);
                            label = "取消虚影";
                            break;
                        }
                        case 3:
                        {
                            GridCell? g = placed.Where(p => HomeGridService.BuildingAt(s, p) is BuildingRecord br && HomeValleyController.IsPlannedGhost(br))
                                .Select(p => (GridCell?)p).FirstOrDefault();
                            GridCell? to = FreeRect(s, 1, 1, 10f, 44f, cursorAvoid);
                            if (g == null || to == null) break;
                            Reserve(cursorAvoid, to.Value, 1, 1);
                            mode.SetRelocateMode(true);
                            mode.ClickCell(s, g.Value);
                            mode.ClickCell(s, to.Value);
                            placed.Remove(g.Value);
                            placed.Add(to.Value);
                            label = "挪虚影";
                            break;
                        }
                        case 4:
                        {
                            GridCell? g = placed.Where(p => HomeGridService.BuildingAt(s, p) is BuildingRecord br && HomeValleyController.IsPlannedGhost(br))
                                .Select(p => (GridCell?)p).FirstOrDefault();
                            if (g == null) break;
                            mode.ClearSelection();
                            mode.SetDemolishMode(false);
                            mode.SetHover(s, g.Value);
                            mode.RotateHovered(s);
                            label = "转虚影";
                            break;
                        }
                        case 5:
                        {
                            GridCell? c = FreeRect(s, 3, 1, 10f, 44f, cursorAvoid);
                            if (c == null) break;
                            Reserve(cursorAvoid, c.Value, 3, 1);
                            mode.StartPaste(s, small, null);
                            mode.SetHover(s, At(c.Value, 1, 0));
                            mode.RefreshPreview(s);
                            mode.PointerDown(s, At(c.Value, 1, 0));
                            mode.RightClick();
                            label = "粘贴";
                            break;
                        }
                        case 6:
                        {
                            PlannedBeltRecord p = (s.Grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).LastOrDefault(x => x.NodeKind == 0 && x.PipePiece == 0 && x.CellState.Contains(0));
                            if (p == null) break;
                            int i = Array.IndexOf(p.CellState, 0);
                            mode.SetDemolishMode(true);
                            mode.ClickCell(s, new GridCell(p.Xs[i], p.Ys[i]));
                            label = "拆一格规划传送带";
                            break;
                        }
                    }
                    int lastAfter = s.Grid.PlanUndo != null && s.Grid.PlanUndo.Count > 0 ? s.Grid.PlanUndo[s.Grid.PlanUndo.Count - 1].Step : 0;
                    if (label != null && lastAfter != lastBefore && PlanHistory.RedoSteps(s) == 0)
                    {
                        kinds.Add(label);
                        snaps.Add(PlanSnapshot(s));
                    }
                }
                int total = kinds.Count;
                Expect(total >= 55 && PlanHistory.UndoSteps(s) == 50,
                    $"U1 用真实建造模式入口做了 {total} 步规划操作（{string.Join("、", kinds.Distinct())}）；撤销栈保留 50 步（FGR-LOG-009 初值），更早的丢掉");
                // 逐步撤销 50 次：第 k 次撤销后 = 倒数第 k 步之前的指纹。
                int undoMismatch = -1;
                for (int k = 1; k <= 50; k++)
                {
                    PlanStepResult r = mode.Undo(s);
                    if (!r.Done || PlanSnapshot(s) != snaps[total - k])
                    {
                        undoMismatch = k;
                        break;
                    }
                }
                PlanStepResult extra = mode.Undo(s);
                Expect(undoMismatch < 0 && PlanHistory.UndoSteps(s) == 0 && PlanHistory.RedoSteps(s) == 50 && !extra.Done && extra.Text.Contains("没有可以撤销"),
                    $"U1 FGT-LOG-004 逐步撤销 50 次：每一次的规划状态都与那一步之前逐项一致（建筑 / 规划中的物流件与设置 / 内核格数 / 库存）" +
                    (undoMismatch < 0 ? string.Empty : $"——第 {undoMismatch} 次撤销（{kinds[total - undoMismatch]}）不一致") + "；再按撤销写明“没有可以撤销的”");
                int redoMismatch = -1;
                for (int k = 0; k < 50; k++)
                {
                    PlanStepResult r = mode.Redo(s);
                    if (!r.Done || PlanSnapshot(s) != snaps[total - 50 + k + 1])
                    {
                        redoMismatch = k + 1;
                        break;
                    }
                }
                Expect(redoMismatch < 0 && PlanHistory.RedoSteps(s) == 0 && PlanHistory.UndoSteps(s) == 50,
                    "U1 FGT-LOG-004 逐步重做 50 次：每一次都回到那一步之后的状态" + (redoMismatch < 0 ? string.Empty : $"——第 {redoMismatch} 次重做不一致"));
                // 新操作清空重做栈。
                mode.Undo(s);
                GridCell? c2 = FreeRect(s, 1, 1, 10f, 44f, cursorAvoid);
                mode.Select("power_pole");
                if (c2 != null)
                {
                    mode.ClickCell(s, c2.Value);
                }
                Expect(c2 != null && PlanHistory.RedoSteps(s) == 0 && GameClock.Paused,
                    "U1 撤销之后再做一个新的规划操作，重做栈清空；全程在战略暂停中（暂停中可以规划和撤销，FG03 第 4 节）");
            }
            finally
            {
                mode?.Close();
                GameClock.SetPaused(false);
                InputRouter.SetGameplayPaused(false);
            }
        }

        /// <summary>卡片“撤销已完工建筑时生成拆除任务并提示”：不会瞬间消失；已建成的物流件立即拆掉并全额返还；搬迁已完成则生成搬回原位任务。</summary>
        private static void CheckUndoCompleted()
        {
            CampaignState s = NewWorld(9712, scrap: 400);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            try
            {
                mode.Open();
                GridCell? c = FreeRect(s, 1, 1, 14f, 30f, null);
                mode.Select("power_pole");
                mode.ClickCell(s, c.Value);
                mode.ClearSelection();
                bool built = StepUntil(() => HomeGridService.BuildingAt(s, c.Value)?.ConstructionState == BuildingConstructionState.Operational, 600);
                BuildingRecord pole = HomeGridService.BuildingAt(s, c.Value);
                int scrapBuilt = s.Scrap + GroundScrap(s);
                int n0 = NotifyCount("plan_undo");
                PlanStepResult r = mode.Undo(s);
                BuildingRecord still = HomeGridService.BuildingAt(s, c.Value);
                NotificationEntry note = LastOf("plan_undo");
                Expect(built && still != null && still.BuildingId == pole.BuildingId && HomeGridService.IsMarkedForDemolish(s, pole.BuildingId) && r.DemolishTasks == 1
                       && NotifyCount("plan_undo") == n0 + 1 && note != null && note.Text.Contains("拆除任务") && mode.StatusText.Contains("拆除任务"),
                    $"U2 撤销一座已经建成的电塔：不会瞬间消失——生成拆除任务（标记拆除），并提示玩家（通知“{note?.Text}”、状态行）");
                bool demolished = StepUntil(() => HomeGridService.BuildingAt(s, c.Value) == null, 600);
                Expect(demolished && s.Scrap + GroundScrap(s) == scrapBuilt + 3, $"U2 机器上门拆掉后全额返还 3 废料（库存 + 地面 {scrapBuilt} → {s.Scrap + GroundScrap(s)}；仓储满时返还落地等机器搬回）");
                PlanStepResult rr = mode.Redo(s);
                BuildingRecord back = HomeGridService.BuildingAt(s, c.Value);
                Expect(rr.Done && back != null && back.BuildingTypeId == "power_pole" && HomeValleyController.IsPlannedGhost(back),
                    "U2 重做：已经拆掉了，就在原处放回电塔虚影（机器再来施工）");
                // 物流件：拖 4 格传送带 → 建成 → 撤销 = 立即拆掉、全额返还。
                GridCell? row = FreeRect(s, 4, 1, 14f, 34f, null);
                mode.Select("belt_t1");
                mode.PointerDown(s, row.Value);
                mode.SetHover(s, At(row.Value, 3, 0));
                mode.PointerUp(s, At(row.Value, 3, 0));
                mode.ClearSelection();
                bool beltsBuilt = StepUntil(() => Enumerable.Range(0, 4).All(i => BeltNetworkService.Kernel.HasCell(row.Value.X + i, row.Value.Y)), 600);
                StepUntil(() => HomeGridService.BuildingAt(s, c.Value)?.ConstructionState == BuildingConstructionState.Operational, 600);
                int scrap1 = s.Scrap;
                int ground1 = GroundScrap(s);
                int notes1 = NotifyCount("plan_undo");
                PlanStepResult ub = mode.Undo(s);
                bool gone = Enumerable.Range(0, 4).All(i => !BeltNetworkService.Kernel.HasCell(row.Value.X + i, row.Value.Y));
                Expect(beltsBuilt && ub.RemovedBuilt == 4 && gone && s.Scrap + GroundScrap(s) == scrap1 + ground1 + 4 && NotifyCount("plan_undo") == notes1 + 1
                       && mode.StatusText.Contains("立即拆掉"),
                    $"U3 撤销一段已经建成的传送带（4 格）：立即拆掉（传送带拆除本来就是即时的），造价全额返还 4 废料，发一条“撤销与重做”通知、状态行“{mode.StatusText}”");
                // 搬迁：还没开工 → 撤销 = 取消搬迁；已经搬完 → 撤销 = 生成搬回原位的任务。
                BuildingRecord p2 = HomeGridService.BuildingAt(s, c.Value);
                GridCell? to = FreeRect(s, 1, 1, 14f, 30f, null);
                mode.SetRelocateMode(true);
                mode.ClickCell(s, c.Value);
                mode.ClickCell(s, to.Value);
                bool planned = HomeGridService.FindRelocationGhost(s, p2.BuildingId) != null;
                mode.Undo(s);
                bool cancelled = HomeGridService.FindRelocationGhost(s, p2.BuildingId) == null && HomeGridService.BuildingAt(s, c.Value)?.BuildingId == p2.BuildingId;
                mode.Redo(s);
                bool moved = StepUntil(() => HomeGridService.BuildingAt(s, to.Value)?.BuildingId == p2.BuildingId && HomeGridService.FindRelocationGhost(s, p2.BuildingId) == null, 600);
                PlanStepResult ur = mode.Undo(s);
                BuildingRecord backGhost = HomeGridService.FindRelocationGhost(s, p2.BuildingId);
                Expect(planned && cancelled && moved && ur.RelocateTasks == 1 && backGhost != null && backGhost.GridX == c.Value.X && backGhost.GridY == c.Value.Y
                       && HomeGridService.BuildingAt(s, to.Value)?.BuildingId == p2.BuildingId,
                    "U4 搬迁：还没开工时撤销 = 取消搬迁（原建筑不动）；重做后机器搬完，再撤销 = 生成“搬回原位”的搬迁任务（建筑先留在新位置，不瞬移）");
            }
            finally
            {
                mode?.Close();
            }
        }

        private static int GroundScrap(CampaignState s) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => g.Amount);

        /// <summary>拆除标记的撤销、单步过大不进栈、真实按键（建造模式开着 / 关着）。</summary>
        private static void CheckUndoMisc()
        {
            CampaignState s = NewWorld(9713, scrap: 400);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            var reader = new KeyReader();
            try
            {
                GridCell? c = FreeRect(s, 1, 1, 14f, 30f, null);
                BuildingRecord pole = AddBuilt(s, "power_pole", "misc_pole", c.Value);
                mode.Open();
                mode.SetDemolishMode(true);
                mode.ClickCell(s, c.Value);
                bool marked = HomeGridService.IsMarkedForDemolish(s, pole.BuildingId);
                mode.Undo(s);
                bool unmarked = !HomeGridService.IsMarkedForDemolish(s, pole.BuildingId);
                mode.Redo(s);
                bool remarked = HomeGridService.IsMarkedForDemolish(s, pole.BuildingId);
                StepUntil(() => HomeGridService.FindBuilding(s, pole.BuildingId) == null, 600);
                PlanStepResult back = mode.Undo(s);
                BuildingRecord ghost = HomeGridService.BuildingAt(s, c.Value);
                Expect(marked && unmarked && remarked && back.Done && ghost != null && HomeValleyController.IsPlannedGhost(ghost) && ghost.BuildingTypeId == "power_pole",
                    "U5 拆除标记：撤销 = 取消标记、重做 = 再标记；已经拆掉了再撤销 = 在原处放回虚影");
                // 真实按键：建造模式开着时 Ctrl+Z / Ctrl+Y；关着（战略视角）时也能撤销。先放一座电塔虚影当作要撤销的一步。
                GridCell? k = FreeRect(s, 1, 1, 14f, 30f, null);
                mode.Select("power_pole");
                mode.ClickCell(s, k.Value);
                mode.ClearSelection();
                InputRouter.DebugSetReader(reader);
                InputRouter.SetScope(InputScope.Strategy);
                string s0 = PlanSnapshot(s);
                mode.SetDemolishMode(false);
                Press(reader, mode, s, GameSettings.KeyBindings.GetChord(GameActionId.Undo));
                string s1 = PlanSnapshot(s);
                Press(reader, mode, s, GameSettings.KeyBindings.GetChord(GameActionId.Redo));
                bool keysOpen = s1 != s0 && PlanSnapshot(s) == s0;
                mode.Close();
                Press(reader, mode, s, GameSettings.KeyBindings.GetChord(GameActionId.Undo));
                bool keysClosed = !mode.IsOpen && PlanSnapshot(s) == s1 && mode.LastStep != null && mode.LastStep.Done;
                Expect(keysOpen && keysClosed, "U6 真实按键：建造模式里 Ctrl+Z 撤销、Ctrl+Y 重做；建造模式关着（战略视角）按 Ctrl+Z 也能撤销");
                // 电路编辑器占用撤销键时（FG1-SIG-02）：Ctrl+Z 归编辑器，建造规划不动。
                var owner = new object();
                bool editorUndo = false;
                UiUndoRouter.Claim(owner, () => { editorUndo = true; return true; }, () => true);
                string beforeEditor = PlanSnapshot(s);
                int stepsBefore = PlanHistory.UndoSteps(s);
                try
                {
                    Press(reader, mode, s, GameSettings.KeyBindings.GetChord(GameActionId.Undo));
                }
                finally
                {
                    UiUndoRouter.Release(owner);
                }
                Expect(editorUndo && PlanSnapshot(s) == beforeEditor && PlanHistory.UndoSteps(s) == stepsBefore,
                    "U6 电路编辑器占用撤销键时 Ctrl+Z 归编辑器（UiUndoRouter），建造规划的撤销栈不动");
                // 单步超过上限：不进栈。
                PlanHistory.BeginStep(s, PlanStepKind.Paste);
                var huge = new PlanEntries.Builder();
                for (int i = 0; i < PlanHistory.StepMaxEntries + 200; i++)
                {
                    huge.Add("belt_t1", 100000 + i, 100000, 0);
                }
                int steps = PlanHistory.UndoSteps(s);
                typeof(PlanHistory).GetMethod("Add", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    ?.Invoke(null, new object[] { s, new PlanOpRecord { Kind = (int)PlanOpKind.PiecesPlaced, Entries = huge.Build() } });
                PlanHistory.EndStep(s);
                Expect(PlanHistory.LastStepTooLarge && PlanHistory.UndoSteps(s) == steps && PlanHistory.RedoSteps(s) == 0,
                    $"U7 单步快照超过上限（{PlanHistory.StepMaxEntries} 件）的操作不进撤销栈（存档体积有上界）；框选拆除遇到这种情况先确认并写明“不能撤销”（B04）");
            }
            finally
            {
                InputRouter.DebugSetReader(null);
                mode?.Close();
            }
        }

        private static void Press(KeyReader reader, HomeValleyBuildMode mode, CampaignState s, InputChord chord)
        {
            if ((chord.Mods & InputModifier.Ctrl) != 0) reader.Hold(KeyCode.LeftControl);
            if ((chord.Mods & InputModifier.Alt) != 0) reader.Hold(KeyCode.LeftAlt);
            if ((chord.Mods & InputModifier.Shift) != 0) reader.Hold(KeyCode.LeftShift);
            reader.Press(chord.Key);
            mode.Tick(null, s, inStrategyView: true);
            UiKitInputPump.ProcessWorldKeys();
            reader.EndFrame();
            InputRouter.DebugClearConsumedKeys();
        }

        /// <summary>撤销栈跟着存档：真文件存读档后接着撤销；旧存档没有这个字段 = 空栈；50 步 × 1024 件的最坏体积。</summary>
        private static void CheckUndoSaveLoad()
        {
            CampaignState s = NewWorld(9714, scrap: 400);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell? src = FreeRect(s, 14, 5, 12f, 40f, null);
            if (src == null || !LaySource(s, src.Value, out _))
            {
                Fail("U8 放不下源场景");
                return;
            }
            try
            {
                mode.Open();
                CopyBox(mode, s, src.Value, At(src.Value, 9, 4));
                var avoid = new HashSet<GridCell>();
                Reserve(avoid, src.Value, 14, 5);
                GridCell? dst = FreeRect(s, 10, 5, 12f, 44f, avoid);
                GridCell h8 = HoverFor(HomeValleyBuildMode.Clipboard, dst.Value);
                mode.SetHover(s, h8);
                mode.RefreshPreview(s);
                mode.PointerDown(s, h8);
                mode.Close();
                string afterPaste = PlanSnapshot(s);
                int steps = PlanHistory.UndoSteps(s);
                WorldSimulation.SyncAllForSave();
                SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                CampaignSession.Set(Slot, rr.State);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                CampaignState l = rr.State;
                bool same = rr.Success && PlanSnapshot(l) == afterPaste && PlanHistory.UndoSteps(l) == steps && PlanHistory.PeekUndo(l) == PlanStepKind.Paste;
                PlanStepResult u = HomeValleyBuildMode.Current.Undo(l);
                bool undone = u.Done && HomeValleyConstruction.PlannedCellCount(l) == 0 && !l.BuildingRecords.Any(b => HomeValleyController.IsPlannedGhost(b));
                Expect(save.Success && same && undone,
                    $"U8 真文件存读档：撤销栈跟着存档（{steps} 步，下一步“{PlanHistory.StepName(PlanStepKind.Paste)}”）；读档后按撤销，刚才粘贴的 12 件虚影全部取消");
                // 旧存档（本 Story 之前）：没有撤销栈字段 → 读档后空栈，不报错。
                l.Grid.PlanUndo = null;
                l.Grid.PlanRedo = null;
                string json = JsonUtility.ToJson(l);
                CampaignState legacy = JsonUtility.FromJson<CampaignState>(json.Replace("\"PlanUndo\":[],", string.Empty).Replace("\"PlanRedo\":[],", string.Empty));
                PlanStepResult none = PlanHistory.Undo(legacy);
                Expect(legacy.Grid != null && PlanHistory.UndoSteps(legacy) == 0 && !none.Done, "U8 旧存档没有撤销栈字段：读成空栈，撤销写明“没有可以撤销的”，不报错");
                // 最坏体积：50 步 × 每步 1024 件。
                var worst = new GridState { PlanUndo = new List<PlanOpRecord>() };
                for (int st = 0; st < 50; st++)
                {
                    var b = new PlanEntries.Builder();
                    for (int i = 0; i < PlanningService.MaxEntries; i++)
                    {
                        b.Add(i % 3 == 0 ? "underground_t1" : "belt_t1", 1200 + i % 64, -1300 - i / 64, i & 3, 1203 + i % 64, -1300 - i / 64, i % 7);
                    }
                    worst.PlanUndo.Add(new PlanOpRecord { Step = st + 1, StepKind = (int)PlanStepKind.Paste, Kind = (int)PlanOpKind.PiecesPlaced, Entries = b.Build() });
                }
                int bytes = Encoding.UTF8.GetByteCount(JsonUtility.ToJson(worst));
                PerfLines.Add($"撤销栈最坏体积（50 步 × 每步 {PlanningService.MaxEntries} 件，按列存）：{bytes / 1024} KB JSON；一般一步几件到几十件，几 KB");
                Expect(bytes < 4 * 1024 * 1024, $"U8 撤销栈存档体积有上界：最坏 50 × {PlanningService.MaxEntries} 件 = {bytes / 1024} KB（< 4 MB）");
            }
            finally
            {
                HomeValleyBuildMode.Current?.Close();
            }
        }

        // ── G 升级规划 ──────────────────────────────────────────────────────────────

        private static void CheckUpgrade()
        {
            CampaignState s = NewWorld(9721, scrap: 400);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell? area = FreeRect(s, 8, 5, 12f, 32f, null);
            if (area == null)
            {
                Fail("G 找不到空地");
                return;
            }
            GridCell o = area.Value;
            try
            {
                bool laid = true;
                for (int x = 0; x < 4; x++)
                {
                    laid &= BeltNetworkService.TryPlace(s, At(o, x, 0), BeltDir.East, 0).Ok;
                }
                laid &= BeltNetworkService.TryPlaceUnderground(s, At(o, 0, 2), BeltDir.East, 0, 4).Ok;
                laid &= PipeNetworkService.TryPlace(s, At(o, 0, 4), PipePieceKind.Pipe, 0, 0).Ok && PipeNetworkService.TryPlace(s, At(o, 1, 4), PipePieceKind.Pipe, 0, 0).Ok;
                BuildingRecord pole = AddBuilt(s, "power_pole", "up_pole", At(o, 6, 4), priority: 2);
                // 传送带上放几件物品（升级时物品不动）。
                bool items = BeltNetworkService.Kernel.InsertItem(o.X, o.Y, BeltItems.ScrapId) == BeltResult.Ok;
                int itemsBefore = BeltNetworkService.Kernel.ItemCount;
                int invest0 = pole.InvestedScrap;
                mode.Open();
                mode.SetUpgradeMode(true);
                mode.PointerDown(s, o);
                mode.SetHover(s, At(o, 7, 4));
                UpgradeBoxPlan pv = mode.UpgradePreview;
                // 差额：传送带 4 × 1 + 地下两端 2 × 3 + 管线 2 × 1 + 电塔 5 = 17。
                bool preview = pv != null && pv.Count == 8 && pv.Cost == 17;
                mode.PointerUp(s, At(o, 7, 4));
                BuildingRecord ghost = HomeGridService.FindRelocationGhost(s, pole.BuildingId);
                bool plans = (s.Grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).Count(p => p.Upgrade) == 3 && ghost != null && HomeGridService.IsUpgradeGhost(ghost)
                             && ghost.BuildingTypeId == "power_pole_t2" && ghost.ConstructionRequired == 5 && PlanHistory.PeekUndo(s) == PlanStepKind.Upgrade;
                bool running = BeltNetworkService.TryGetPiece(o, out _, out int t0) && t0 == 0 && HomeGridService.BuildingAt(s, At(o, 6, 4))?.BuildingId == pole.BuildingId;
                Expect(laid && preview && plans && running,
                    $"G1 升级模式拖框：能升级 {pv?.Count} 件（4 格传送带 + 地下一条 + 2 格管线 + 电塔），差额 {pv?.Cost} 废料（= 新旧造价差，FGR-LOG-010）；松开生成 3 份物流件升级规划与电塔升级虚影（差额 5），一步进撤销栈；升级前照常运转");
                int total0 = s.Scrap + GroundScrap(s);
                bool done = StepUntil(() => (s.Grid.PlannedBelts ?? Array.Empty<PlannedBeltRecord>()).All(p => !p.Upgrade) && HomeGridService.FindRelocationGhost(s, pole.BuildingId) == null, 900);
                BuildingRecord up = HomeGridService.FindBuilding(s, pole.BuildingId);
                bool belts = Enumerable.Range(0, 4).All(i => BeltNetworkService.TryGetPiece(At(o, i, 0), out _, out int t) && t == 1)
                             && BeltNetworkService.TryGetPiece(At(o, 0, 2), out _, out int ut) && ut == 1 && BeltNetworkService.TryGetPiece(At(o, 4, 2), out _, out int ue) && ue == 1
                             && PipeNetworkService.TryGetPiece(At(o, 0, 4), out _, out int pt) && pt == 1 && PipeNetworkService.TryGetPiece(At(o, 1, 4), out _, out int pt2) && pt2 == 1;
                bool poleUp = up != null && up.BuildingTypeId == "power_pole_t2" && up.PowerPriority == 2 && up.InvestedScrap == invest0 + 5
                              && Mathf.Approximately(HomeValleyPowerGrid.CoverRadiusOf(up.BuildingTypeId), 16f);
                bool conserved = s.Scrap + GroundScrap(s) == total0 - 17;
                Expect(done && belts && poleUp && conserved && (!items || BeltNetworkService.Kernel.ItemCount == itemsBefore),
                    $"G2 机器取来差额施工：传送带 / 地下两端 / 管线都变成 T2（物品不动：{itemsBefore} 件），电塔原地换成 T2（同一 ID、优先级 2 保留、投入 +5、覆盖 16 格），库存只少了差额 17（{total0} → {s.Scrap + GroundScrap(s)}）");
                // 再框一次：T2 传送带能升 T3，管线已经最高。
                mode.SetUpgradeMode(true);
                mode.PointerDown(s, At(o, 0, 4));
                mode.SetHover(s, At(o, 1, 4));
                UpgradeBoxPlan top = mode.UpgradePreview;
                mode.PointerUp(s, At(o, 1, 4));
                Expect(top != null && top.Count == 0 && top.Refused == 2 && top.FirstRefusal?.Code == GridBlockReason.NoUpgrade && mode.StatusIsError
                       && mode.StatusText.Contains("最高等级"),
                    $"G3 负向：管线 T2 已经是最高等级——框里 2 件都不能升，写明原因“{mode.StatusText}”，不生成施工");
            }
            finally
            {
                mode?.Close();
            }
        }

        private static void CheckUpgradeNegative()
        {
            CampaignState s = NewWorld(9722, scrap: 0);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell? area = FreeRect(s, 3, 3, 12f, 32f, null);
            if (area == null)
            {
                Fail("G4 找不到空地");
                return;
            }
            GridCell o = area.Value;
            try
            {
                BuildingRecord pole = AddBuilt(s, "power_pole", "neg_pole", o);
                s.Scrap = 3;
                mode.Open();
                mode.SetUpgradeMode(true);
                mode.PointerDown(s, o);
                mode.PointerUp(s, o);
                BuildingRecord ghost = HomeGridService.FindRelocationGhost(s, pole.BuildingId);
                StepUntil(() => ghost != null && ghost.ConstructionDelivered > 0, 300);
                int delivered = ghost?.ConstructionDelivered ?? 0;
                int total = s.Scrap + GroundScrap(s) + delivered;
                // 升级中的建筑：不能搬、不能再升、拆除模式点它 = 取消升级（已到差额全额退回）。
                GridOpResult move = HomeGridService.TryRelocate(s, pole.BuildingId, At(o, 2, 2), 0);
                mode.SetUpgradeMode(true);
                mode.PointerDown(s, o);
                UpgradeBoxPlan again = mode.UpgradePreview;
                mode.PointerUp(s, o);
                bool noSecond = again != null && again.Count == 0 && again.FirstRefusal?.Code == GridBlockReason.Upgrading;
                mode.SetDemolishMode(true);
                mode.ClickCell(s, o);
                bool cancelled = HomeGridService.FindRelocationGhost(s, pole.BuildingId) == null && HomeGridService.FindBuilding(s, pole.BuildingId)?.BuildingTypeId == "power_pole"
                                 && !HomeGridService.IsMarkedForDemolish(s, pole.BuildingId) && mode.StatusText.Contains("取消升级");
                StepUntil(() => s.Scrap + GroundScrap(s) == total, 120);
                Expect(ghost != null && delivered > 0 && move.FirstReason == GridBlockReason.Upgrading && noSecond && cancelled && s.Scrap + GroundScrap(s) == total,
                    $"G4 升级中的电塔（差额运到了 {delivered}）：不能搬迁（“{move.Describe()}”）、不能再升一次；拆除模式点它 = 取消升级（不是拆掉），已到的差额全额退回（守恒 {total}）");
                // 撤销“取消升级” = 重新升级；再撤销 = 取消。
                mode.Undo(s);
                bool reUp = HomeGridService.FindRelocationGhost(s, pole.BuildingId) is BuildingRecord g2 && HomeGridService.IsUpgradeGhost(g2);
                mode.Undo(s);
                bool reCancel = HomeGridService.FindRelocationGhost(s, pole.BuildingId) == null;
                Expect(reUp && reCancel, "G4 撤销“取消升级” = 重新生成升级；再撤销（撤销升级本身）= 取消升级");
            }
            finally
            {
                mode?.Close();
            }
        }

        // ── E 吸管与复制设置 ─────────────────────────────────────────────────────────

        private static void CheckEyedropperAndSettings()
        {
            CampaignState s = NewWorld(9731, scrap: 400);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell? area = FreeRect(s, 8, 5, 12f, 32f, null);
            if (area == null)
            {
                Fail("E 找不到空地");
                return;
            }
            GridCell o = area.Value;
            try
            {
                bool laid = BeltNetworkService.TryPlaceNode(s, o, BeltDir.North, 2, BeltNodeKind.Splitter).Ok
                            && BeltNetworkService.TrySetSplitter(s, o, 4, 1, BeltSide.Right, BeltConst.FilterAny, BeltConst.FilterNone).Ok
                            && BeltNetworkService.TryPlaceNode(s, At(o, 2, 0), BeltDir.East, 2, BeltNodeKind.Splitter).Ok
                            && PipeNetworkService.TryPlace(s, At(o, 4, 0), PipePieceKind.Tank, 0, 0).Ok;
                mode.Open();
                mode.SetHover(s, o);
                mode.Eyedrop(s, o);
                bool picked = mode.SelectedToolId == "splitter" && mode.GhostRotation == 0 && mode.PendingS0 == PlanSettings.PackSplitter(4, 1, 2) && mode.PendingS2 == BeltConst.FilterNone
                              && mode.StatusText.Contains("带上设置") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildFirstEyedropper);
                GridCell target = At(o, 0, 2);
                mode.ClickCell(s, target);
                HomeValleyConstruction.TryFindPlannedCell(s, target, out PlannedBeltRecord p, out _);
                bool carried = p != null && p.RatioL == 4 && p.RatioR == 1 && p.PriorityOut == 2 && p.FilterR == BeltConst.FilterNone && p.Dirs[0] == (int)BeltDir.North;
                Expect(laid && picked && carried,
                    $"E1 吸管（FGR-LOG-011）：指着 4:1、优先右口、右口关闭、朝北的分流器 → 选中分流器、朝向朝北、带上设置（“{mode.StatusText}”）；放下的虚影规划里就是这些设置");
                bool builtSplit = StepUntil(() => BeltNetworkService.TryGetNode(target, out BeltNodeInfo n) && n.RatioL == 4 && n.PriorityOut == BeltSide.Right, 600);
                Expect(builtSplit, "E1 机器建成后分流器就是吸来的设置");
                // 吸管：空地 / 开局建筑（本局不能建）。
                GridCell empty = At(o, 7, 4);
                mode.Eyedrop(s, empty);
                bool nothing = mode.StatusIsError && mode.StatusText.Contains("没有建筑");
                BuildingRecord bay = HomeGridService.FindBuilding(s, HomeValleyLayout.RegionId + ":repair_bay");
                mode.Eyedrop(s, new GridCell(bay.GridX, bay.GridY));
                bool notPlaceable = mode.StatusIsError && mode.StatusText.Contains("本局不能建造");
                Expect(nothing && notPlaceable, "E2 负向：吸管指着空地 / 本局不能建造的开局建筑（维修台）→ 写明原因，不改选择");
                // 复制设置：分流器 A → 分流器 B（真实按键 Alt+C / Alt+V）。
                var reader = new KeyReader();
                InputRouter.DebugSetReader(reader);
                InputRouter.SetScope(InputScope.Strategy);
                mode.ClearSelection();
                mode.SetHover(s, o);
                Press(reader, mode, s, GameSettings.KeyBindings.GetChord(GameActionId.CopySettings));
                bool clip = HomeValleyBuildMode.SettingsClip != null && HomeValleyBuildMode.SettingsClip.Family == PlanSettings.Family.Splitter;
                mode.SetHover(s, At(o, 2, 0));
                Press(reader, mode, s, GameSettings.KeyBindings.GetChord(GameActionId.PasteSettings));
                bool applied = BeltNetworkService.TryGetNode(At(o, 2, 0), out BeltNodeInfo b) && b.RatioL == 4 && b.RatioR == 1 && b.PriorityOut == BeltSide.Right && b.FilterR == BeltConst.FilterNone
                               && b.Dir == BeltDir.East && PlanHistory.PeekUndo(s) == PlanStepKind.Settings;
                mode.Undo(s);
                bool restored = BeltNetworkService.TryGetNode(At(o, 2, 0), out BeltNodeInfo b2) && b2.RatioL == 1 && b2.RatioR == 1 && b2.PriorityOut == BeltSide.None;
                Expect(clip && applied && restored,
                    "E3 复制设置（Alt+C / Alt+V，FGR-LOG-011）：A 分流器的比例 / 优先口 / 过滤写到 B（B 的朝向不变）；进撤销栈，撤销后 B 回到原来的 1:1");
                // 不兼容：分流器 → 储罐。
                mode.SetHover(s, At(o, 4, 0));
                Press(reader, mode, s, GameSettings.KeyBindings.GetChord(GameActionId.PasteSettings));
                bool refused = mode.StatusIsError && mode.StatusText.Contains("设置不兼容");
                // 兼容类型：维修台（用电）→ 解析台（用电，不同类）复制电力优先级。
                BuildingRecord bench = HomeGridService.FindBuilding(s, HomeValleyLayout.RegionId + ":analysis_bench");
                HomeValleyPowerGrid.TrySetPriority(s, bay.BuildingId, 4);
                mode.SetHover(s, new GridCell(bay.GridX, bay.GridY));
                Press(reader, mode, s, GameSettings.KeyBindings.GetChord(GameActionId.CopySettings));
                mode.SetHover(s, new GridCell(bench.GridX, bench.GridY));
                Press(reader, mode, s, GameSettings.KeyBindings.GetChord(GameActionId.PasteSettings));
                bool power = bench.PowerPriority == 4;
                // 电塔不用电：不兼容。
                BuildingRecord pole = AddBuilt(s, "power_pole", "eyepole", At(o, 6, 0));
                mode.SetHover(s, new GridCell(pole.GridX, pole.GridY));
                Press(reader, mode, s, GameSettings.KeyBindings.GetChord(GameActionId.PasteSettings));
                bool poleRefused = mode.StatusIsError && mode.StatusText.Contains("设置不兼容");
                Expect(refused && power && poleRefused,
                    "E4 兼容规则：分流器的设置不能用在储罐上（写明原因）；维修台的电力优先级 4 可以复制到解析台（不同类但都用电）；电塔不用电，拒绝");
                // 复制设置模式（按钮 + 鼠标）：先点 A 再点 B。
                InputRouter.DebugSetReader(null);
                HomeValleyPowerGrid.TrySetPriority(s, bench.BuildingId, 2);
                HomeValleyPowerGrid.TrySetPriority(s, bay.BuildingId, 3);
                mode.SetSettingsMode(true);
                bool fresh = HomeValleyBuildMode.SettingsClip != null; // 上一次的剪贴板还在：先右键退出、清掉后重来
                mode.RightClick();
                mode.SetSettingsMode(true);
                mode.PointerDown(s, new GridCell(bay.GridX, bay.GridY)); // 有剪贴板时点击 = 写上去（维修台 ← 解析台之前的 4）
                bool wrote = bay.PowerPriority == 4;
                Expect(fresh && wrote && mode.SettingsMode, "E5 复制设置模式（建造栏按钮）：鼠标点目标就写上记下的设置；右键退出");
            }
            finally
            {
                InputRouter.DebugSetReader(null);
                mode?.Close();
            }
        }

        // ── L 布局库 ────────────────────────────────────────────────────────────────

        private static void CheckLayoutLibrary()
        {
            CampaignState s = NewWorld(9741, scrap: 400);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell? src = FreeRect(s, 14, 5, 12f, 40f, null);
            if (src == null || !LaySource(s, src.Value, out _))
            {
                Fail("L 放不下源场景");
                return;
            }
            try
            {
                mode.Open();
                CopyBox(mode, s, src.Value, At(src.Value, 9, 4));
                mode.RightClick();
                bool saved = HomeValleyBuildMode.SaveClipboardToLibrary("主干分流", out LayoutRecord rec, out string why);
                bool onDisk = File.Exists(LayoutLibrary.FilePath) && File.ReadAllText(LayoutLibrary.FilePath).Contains("主干分流");
                LayoutLibrary.Reload(); // 模拟重启游戏：从文件重读
                bool reread = LayoutLibrary.Count == 1 && LayoutLibrary.All[0].Name == "主干分流" && PlanEntries.CountOf(LayoutLibrary.All[0].Entries) == 12
                              && LayoutLibrary.All[0].Width == 6 && LayoutLibrary.All[0].Height == 5;
                Expect(saved && onDisk && reread && GameSettings.HasSeenGuidanceHook(GuidanceHooks.BuildFirstLayoutSaved),
                    $"L1 把剪贴板存成命名布局“主干分流”（12 件、6×5 格）：写进玩家配置目录的 {LayoutLibrary.FileName}（与存档目录分开），重启后从文件读回一致" + (saved ? string.Empty : "：" + why));
                // 跨存档：换一个新战役（不同种子），布局库还在，放置到新世界。
                CampaignState s2 = NewWorld(9742, scrap: 400);
                HomeValleyBuildMode mode2 = HomeValleyBuildMode.Current;
                mode2.Open();
                GridCell? dst = FreeRect(s2, 10, 5, 12f, 40f, null);
                bool started = mode2.StartPaste(s2, LayoutLibrary.All[0].Entries, LayoutLibrary.All[0].Name);
                GridCell h2 = HoverFor(LayoutLibrary.All[0].Entries, dst.Value);
                mode2.SetHover(s2, h2);
                mode2.RefreshPreview(s2);
                mode2.PointerDown(s2, h2);
                bool placed = started && mode2.LastPaste != null && mode2.LastPaste.Placed == 12 && HomeGridService.BuildingAt(s2, At(dst.Value, 0, 2))?.BuildingTypeId == "power_pole";
                Expect(LayoutLibrary.Count == 1 && placed, "L2 FGT-LOG-013 跨存档：换一个新战役（另一个种子），布局库里的布局还在，放置到新世界全部 12 件虚影放下");
                mode2.Close();
                // 缩略图：按件画在贴图上。
                Texture2D thumb = LayoutLibrary.BuildThumbnail(LayoutLibrary.All[0].Entries);
                Color32[] px = thumb.GetPixels32();
                bool colors = thumb.width == 64 && px.Any(c => LayoutLibrary.IsColor(c, PlanEntryKind.Building)) && px.Any(c => LayoutLibrary.IsColor(c, PlanEntryKind.Belt))
                              && px.Any(c => LayoutLibrary.IsColor(c, PlanEntryKind.Splitter)) && px.Any(c => LayoutLibrary.IsColor(c, PlanEntryKind.Pipe))
                              && px.Any(c => LayoutLibrary.IsColor(c, PlanEntryKind.Underground));
                LayoutLibrary.ReleaseThumbnail(thumb);
                Expect(colors, "L3 缩略图（FGU-11）：64 像素，建筑 / 传送带 / 分流器 / 地下传送带 / 管线各有颜色（读贴图像素核对）");
                // 导出 / 导入 / 改名 / 删除 / 库满 / 坏文本。
                string text = LayoutLibrary.Export(LayoutLibrary.All[0]);
                bool imported = LayoutLibrary.TryImport(text, out LayoutRecord imp, out _) && LayoutLibrary.Count == 2
                                && imp.Entries.Ids.SequenceEqual(LayoutLibrary.All[0].Entries.Ids) && imp.Entries.S0.SequenceEqual(LayoutLibrary.All[0].Entries.S0);
                bool badText = !LayoutLibrary.TryImport("hello", out _, out string badWhy) && badWhy.Contains("不是布局文本");
                bool renamed = LayoutLibrary.TryRename(1, "  改过的名字  ", out _) && LayoutLibrary.All[1].Name == "改过的名字";
                bool deleted = LayoutLibrary.TryDelete(1, out _) && LayoutLibrary.Count == 1;
                Expect(text.StartsWith(LayoutLibrary.ExportPrefix, StringComparison.Ordinal) && imported && badText && renamed && deleted,
                    $"L4 导出为一行文本（{text.Length} 个字符）、导入回来逐件一致（P2 可选项已做）；不是布局文本拒绝并写明原因；改名去掉首尾空格；删除");
                int filled = LayoutLibrary.Count;
                string lastWhy = null;
                while (LayoutLibrary.Count < LayoutLibrary.Max && HomeValleyBuildMode.SaveClipboardToLibrary(null, out _, out lastWhy))
                {
                    filled++;
                }
                bool full = LayoutLibrary.Count == LayoutLibrary.Max && !HomeValleyBuildMode.SaveClipboardToLibrary("多一个", out _, out string fullWhy) && fullWhy.Contains("已满")
                            && LayoutLibrary.All[1].Name.StartsWith("布局 ", StringComparison.Ordinal);
                Expect(full, $"L4 库满（{LayoutLibrary.Max} 个）再存：拒绝并写明“先删掉一个”；名字空着的叫“布局 N”");
                // 坏文件：改名备份，按空库继续并写明。
                File.WriteAllText(LayoutLibrary.FilePath, "{ 这不是 JSON");
                LayoutLibrary.Reload();
                int count = LayoutLibrary.Count;
                bool backup = Directory.GetFiles(Path.GetDirectoryName(LayoutLibrary.FilePath), LayoutLibrary.FileName + ".bad-*").Length == 1;
                Expect(count == 0 && backup && !string.IsNullOrEmpty(LayoutLibrary.LastLoadProblem),
                    $"L5 布局库文件坏了：原文件改名备份（不覆盖玩家数据）、按空库继续，打开面板写明“{LayoutLibrary.LastLoadProblem}”");
            }
            finally
            {
                HomeValleyBuildMode.Current?.Close();
            }
        }

        private static void CheckLayoutPanel()
        {
            CampaignState s = NewWorld(9743, scrap: 400);
            LayoutLibrary.Reload();
            foreach (string f in Directory.GetFiles(Path.GetDirectoryName(LayoutLibrary.FilePath)))
            {
                File.Delete(f);
            }
            LayoutLibrary.Reload();
            var b = new PlanEntries.Builder();
            b.Add("power_pole", 0, 0, 0);
            b.Add("belt_t1", 2, 0, 1);
            b.Add("beacon", 4, 0, 0);
            HomeValleyBuildMode.SetClipboardForTests(b.Build());
            VisualElement root = MountUxml(UiKitFolder + "LayoutLibraryPanel.uxml", out GameObject go);
            LayoutLibraryPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                LayoutLibraryPanelUIToolkit panel = go.AddComponent<LayoutLibraryPanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "LayoutLibraryRow.uxml"));
                LayoutLibraryPanelUIToolkit.Open();
                bool empty = LayoutLibraryPanelUIToolkit.IsOpen && panel.PanelVisible && panel.VisibleRowCount == 0 && panel.EmptyText.Contains("布局库是空的")
                             && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LayoutLibraryFirstOpen) && panel.SaveButton.enabledSelf;
                panel.SetNameText("带信标的");
                panel.SaveClipboard();
                panel.Refresh();
                bool row = panel.VisibleRowCount == 1 && panel.RowName(0) == "带信标的" && panel.RowInfo(0).Contains("3 件") && panel.RowInfo(0).Contains("1 件本局还没解锁")
                           && panel.RowThumbnail(0) != null && panel.MessageText.Contains("已保存") && !GameText.ContainsMarker(panel.RowInfo(0) + panel.MessageText);
                Expect(empty && row,
                    $"L6 布局库面板（真 UXML）：空库写明怎么存；输入名字点“保存剪贴板”→ 一行“{panel.RowName(0)}”（{panel.RowInfo(0).Replace("\n", " / ")}），带缩略图；含未解锁建筑时行内写明");
                string exported = panel.Export(0);
                panel.ImportText(exported);
                panel.Refresh();
                bool two = panel.VisibleRowCount == 2;
                panel.AskDelete(1);
                bool asked = panel.PendingDeleteConfirm && UiConfirmDialog.IsOpen;
                UiConfirmDialog.Cancel();
                bool kept = LayoutLibrary.Count == 2 && !panel.PendingDeleteConfirm;
                panel.AskDelete(1);
                UiConfirmDialog.Confirm();
                panel.Refresh();
                Expect(two && asked && kept && LayoutLibrary.Count == 1 && panel.VisibleRowCount == 1,
                    "L6 导出 → 导入多一行；删除先弹确认（不可逆，B04），取消什么都不变、确认后删掉");
                panel.Place(0);
                HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
                Expect(!LayoutLibraryPanelUIToolkit.IsOpen && mode.PasteMode && mode.PasteName == "带信标的" && mode.StatusText.Contains("1 件本局还没解锁"),
                    $"L6 点“放置”：面板关掉，建造模式进入粘贴这个布局（“{mode.StatusText.Split('\n').FirstOrDefault()}”）");
                mode.Close();
            }
            finally
            {
                LayoutLibraryPanelUIToolkit.Close();
                LayoutLibraryPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                UiEscapeStack.Clear();
            }
            // 布局探针：中英 × 三种缩放，有数据。
            LayoutLibraryPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") })
                    {
                        string result = UiToolkitLayoutProbe.Probe(UiKitFolder + "LayoutLibraryPanel.uxml", "LayoutLibraryWindow", stressFill: true, prepare: pr =>
                        {
                            var probeGo = new GameObject("__probe_ll") { hideFlags = HideFlags.HideAndDontSave };
                            LayoutLibraryPanelUIToolkit p = probeGo.AddComponent<LayoutLibraryPanelUIToolkit>();
                            p.BindView(pr.panel.visualTree);
                            p.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "LayoutLibraryRow.uxml"));
                            LayoutLibraryPanelUIToolkit.Open();
                            p.Refresh();
                            LayoutLibraryPanelUIToolkit.Close();
                            Object.DestroyImmediate(probeGo);
                            pr.panel.visualTree.Q<VisualElement>("LayoutLibraryRoot")?.RemoveFromClassList("uk-hidden");
                        }, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"L7 布局探针 LayoutLibraryPanel.uxml#LayoutLibraryWindow [{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                    }
                }
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                LayoutLibraryPanelUIToolkit.Close();
                LayoutLibraryPanelUIToolkit.InWorldOverrideForTests = false;
            }
        }

        // ── K 快捷键可重绑 ────────────────────────────────────────────────────────────

        private static void CheckRebind()
        {
            CampaignState s = NewWorld(9751, scrap: 400);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            var reader = new KeyReader();
            GridCell? area = FreeRect(s, 4, 3, 12f, 32f, null);
            if (area == null)
            {
                Fail("K 找不到空地");
                return;
            }
            GridCell o = area.Value;
            BeltNetworkService.TryPlaceNode(s, o, BeltDir.East, 2, BeltNodeKind.Splitter);
            BeltNetworkService.TryPlaceNode(s, At(o, 2, 0), BeltDir.East, 2, BeltNodeKind.Splitter);
            BeltNetworkService.TrySetSplitter(s, o, 2, 1, BeltSide.None, BeltConst.FilterAny, BeltConst.FilterAny);
            HomeValleyBuildMode.SetClipboardForTests(null);
            var conflicts = new List<GameActionId>();
            var results = new List<string>();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            VisualElement root = MountUxml(UiKitFolder + "LayoutLibraryPanel.uxml", out GameObject go);
            LayoutLibraryPanelUIToolkit.InWorldOverrideForTests = true;
            LayoutLibraryPanelUIToolkit panel = go.AddComponent<LayoutLibraryPanelUIToolkit>();
            panel.BindView(root);
            // 9 个动作：逐个改到一个没人用的组合键（Ctrl+Alt+F1…），旧键不再触发、新键触发，最后恢复默认。
            var cases = new (GameActionId a, Func<bool> effect, Action prepare)[]
            {
                (GameActionId.Copy, () => mode.CopyMode, () => { mode.Open(); mode.SetCopyMode(false); }),
                (GameActionId.UpgradePlan, () => mode.UpgradeMode, () => { mode.Open(); mode.SetUpgradeMode(false); }),
                (GameActionId.Eyedropper, () => mode.SelectedToolId == "splitter", () => { mode.Open(); mode.ClearSelection(); mode.SetHover(s, o); }),
                (GameActionId.CopySettings, () => HomeValleyBuildMode.SettingsClip != null,
                    () => { mode.Open(); mode.ClearSelection(); mode.SetHover(s, o); HomeValleyBuildMode.ClearSettingsClipForTests(); }),
                (GameActionId.PasteSettings, () => BeltNetworkService.TryGetNode(At(o, 2, 0), out BeltNodeInfo n) && n.RatioL == 2,
                    () => { mode.Open(); mode.ClearSelection(); mode.SetHover(s, At(o, 2, 0)); }),
                (GameActionId.LayoutLibrary, () => LayoutLibraryPanelUIToolkit.IsOpen, () => { LayoutLibraryPanelUIToolkit.Close(); mode.Open(); }),
                (GameActionId.Paste, () => mode.StatusText.Contains("剪贴板是空的") || mode.PasteMode, () => { mode.Close(); mode.Open(); }),
                (GameActionId.Undo, () => mode.LastStep != null && !mode.LastStep.Redo, () => { mode.Open(); SetLastStepNull(mode); }),
                (GameActionId.Redo, () => mode.LastStep != null && mode.LastStep.Redo, () => { mode.Open(); SetLastStepNull(mode); }),
            };
            int k = 1;
            bool all = true;
            try
            {
                foreach ((GameActionId a, Func<bool> effect, Action prepare) in cases)
                {
                    InputChord old = GameSettings.KeyBindings.GetChord(a);
                    var fresh = new InputChord(KeyCode.F1 + (k++ - 1), InputModifier.Ctrl | InputModifier.Alt);
                    RebindResult rb = GameSettings.KeyBindings.TryRebind(a, fresh, conflicts);
                    prepare();
                    Press(reader, mode, s, old);
                    bool oldDead = !effect();
                    prepare();
                    Press(reader, mode, s, fresh);
                    bool newWorks = effect();
                    GameSettings.KeyBindings.ResetToDefault(a);
                    bool ok = rb == RebindResult.Ok && oldDead && newWorks && GameSettings.KeyBindings.GetChord(a).Key == old.Key;
                    all &= ok;
                    results.Add($"{a}:{(ok ? "✓" : $"✗(改键 {rb}，旧键失效 {oldDead}，新键生效 {newWorks})")}");
                    LayoutLibraryPanelUIToolkit.Close();
                }
            }
            finally
            {
                InputRouter.DebugSetReader(null);
                mode?.Close();
                LayoutLibraryPanelUIToolkit.Close();
                LayoutLibraryPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
            Expect(all, "K1 卡片“所有工具的快捷键可重绑”：9 个动作逐个改到 Ctrl+Alt+F1～F9，旧键失效、新键生效（经真实输入路由与建造模式每帧入口），恢复默认 —— " + string.Join(" ", results));
        }

        private static void SetLastStepNull(HomeValleyBuildMode mode)
        {
            typeof(HomeValleyBuildMode).GetProperty("LastStep")?.SetValue(mode, null);
        }

        // ── T 暂停 / 倍速 / 观察 ─────────────────────────────────────────────────────

        /// <summary>同一个场景：暂停中粘贴 + 升级（暂停里不施工），恢复后按倍速跑 90 游戏秒；各倍速与观察 / 不观察结果逐项一致。</summary>
        private static string RunScenario(int seed, bool observe, float speed, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe, scrap: 500);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            pausedHeld = true;
            var avoid = new HashSet<GridCell>();
            GridCell? src = FreeRect(s, 14, 5, 12f, 40f, avoid);
            if (src == null || !LaySource(s, src.Value, out _))
            {
                return "放不下";
            }
            Reserve(avoid, src.Value, 14, 5);
            GridCell? dst = FreeRect(s, 10, 5, 12f, 44f, avoid);
            try
            {
                GameClock.SetPaused(true);
                InputRouter.SetGameplayPaused(true, strategic: true);
                mode.Open();
                CopyBox(mode, s, src.Value, At(src.Value, 9, 4));
                GridCell ht = HoverFor(HomeValleyBuildMode.Clipboard, dst.Value);
                mode.SetHover(s, ht);
                mode.RefreshPreview(s);
                mode.PointerDown(s, ht);
                mode.SetUpgradeMode(true);
                mode.PointerDown(s, src.Value);
                mode.SetHover(s, At(src.Value, 9, 4));
                mode.PointerUp(s, At(src.Value, 9, 4));
                mode.Close();
                string p0 = PlanSnapshot(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = PlanSnapshot(s) == p0;
                GameClock.SetPaused(false);
                InputRouter.SetGameplayPaused(false);
                GameClock.SetSpeed(speed);
                long target = GameClock.Ticks + GameClock.StepHz * 90;
                int frames = 0;
                while (GameClock.Ticks < target && frames < 60 * 600)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                    frames++;
                }
                GameClock.SetSpeed(1f);
                return PlanSnapshot(s) + "#undo=" + PlanHistory.UndoSteps(s) + "#ticks=" + GameClock.Ticks;
            }
            finally
            {
                mode?.Close();
                GameClock.SetPaused(false);
                InputRouter.SetGameplayPaused(false);
            }
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(9761, true, speed, out bool held);
                paused &= held;
                reference ??= snap;
                same &= snap == reference;
            }
            Expect(paused && same && reference != "放不下",
                "T1 暂停中（120 帧）照样能复制粘贴、升级规划，但施工不推进；恢复后 0.5x / 1x / 2x / 3x 跑同样的 90 游戏秒，粘贴的虚影施工与升级结果逐项一致");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(9771, true, 1f, out _);
            string unseen = RunScenario(9771, false, 1f, out _);
            Expect(seen == unseen && seen != "放不下", "T2 同一组粘贴 + 升级在观察与不观察家园时跑 90 游戏秒逐项一致（FGR-BASE-021）" + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── P 性能 ──────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(9781, scrap: 5000);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            // 1024 件的布局：32 × 32 的传送带 / 管线交错方阵。
            var b = new PlanEntries.Builder();
            for (int i = 0; i < PlanningService.MaxEntries; i++)
            {
                b.Add(i % 4 == 3 ? "pipe_t1" : "belt_t1", i % 32, i / 32, 1);
            }
            PlanEntryBlock big = b.Build();
            // 性能场景：核心外 40 格处揭开一片迷雾（测试捷径），布局中心落在那里；地形上放不了的件照常标红叉（这里测的是开销与一致性）。
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridService.RevealArea(s, new Vector2(core.X + 40, core.Y), 30f);
            GridCell anchor = new GridCell(core.X + 40, core.Y);
            int planned0 = HomeValleyConstruction.PlannedCellCount(s);
            var plan = new PastePlan();
            PlanningService.PlanPaste(s, big, anchor, 0, into: plan);
            var sw = Stopwatch.StartNew();
            const int reps = 10;
            for (int i = 0; i < reps; i++)
            {
                PlanningService.PlanPaste(s, big, new GridCell(anchor.X + (i & 1), anchor.Y), i & 3, into: plan);
            }
            double previewMs = sw.Elapsed.TotalMilliseconds / reps;
            PlanningService.PlanPaste(s, big, anchor, 0, into: plan);
            sw.Restart();
            int ok = plan.OkCount;
            PlanApplyResult r = PlanHistory.Paste(s, plan);
            double applyMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            // 布局 32 × 32、中心格 (15, 15)：落在 anchor − 15 … anchor + 16。
            PlanEntryBlock cap = PlanningService.Capture(s, At(anchor, -15, -15), At(anchor, 16, 16), out GridReason? capWhy);
            double captureMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            PlanStepResult u = PlanHistory.Undo(s);
            double undoMs = sw.Elapsed.TotalMilliseconds;
            bool undone = HomeValleyConstruction.PlannedCellCount(s) == planned0;
            PerfLines.Add($"1024 件布局：粘贴预览（换格 / 转向时重算）平均 {previewMs:F2} ms；放下 {applyMs:F1} ms；框选复制 34×34 {captureMs:F1} ms；撤销整次粘贴 {undoMs:F1} ms（Editor JIT；真机热更层解释执行约 ×5）");
            ExpectPerf(ok >= 512 && r.Placed == ok && cap != null && cap.Count >= r.Placed && u.Done && undone,
                $"P1 1024 件（上限）的布局：预览重算平均 {previewMs:F2} ms（< 8 ms：120 帧整帧预算，只在光标换格 / 转向时算一次）；能放 {ok} 件、放下 {r.Placed} 件（一致）、" +
                $"框选复制回来 {cap?.Count} 件{(cap == null ? "（" + capWhy?.Describe() + "）" : string.Empty)}、一步撤销后规划格数回到 {planned0}（放下 / 复制 / 撤销是一次性的玩家操作）",
                PerfGate.Lt(previewMs, 8.0, "预览重算平均 ms"));
            mode?.Close();
        }

        // ── 通用 ─────────────────────────────────────────────────────────────────

        private sealed class KeyReader : IInputReader
        {
            private readonly HashSet<KeyCode> _held = new HashSet<KeyCode>();
            private readonly HashSet<KeyCode> _down = new HashSet<KeyCode>();

            public void Press(KeyCode key) => _down.Add(key);
            public void Hold(KeyCode key) => _held.Add(key);

            public void EndFrame()
            {
                _down.Clear();
                _held.Clear();
            }

            public bool GetKey(KeyCode key) => _held.Contains(key) || _down.Contains(key);
            public bool GetKeyDown(KeyCode key) => _down.Contains(key);
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => Vector3.zero;
            public float MouseScrollDelta => 0f;
        }

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgPlanningToolsSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private static string LocateRepo()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            for (int i = 0; i < 6 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "tools", "cell_tables", "fgdata.py")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
        }

        private static (int code, string output) RunPython(string root, string args)
        {
            try
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
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(120000);
                    return (p.ExitCode, output);
                }
            }
            catch (Exception e)
            {
                return (-1, e.Message);
            }
        }

        private static string Tail(string s)
        {
            string[] lines = (s ?? string.Empty).Replace("\r", string.Empty).Split('\n').Where(l => l.Trim().Length > 0).ToArray();
            return string.Join(" / ", lines.Skip(Math.Max(0, lines.Length - 2)));
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
                UiEscapeStack.Clear();
                InputRouter.DebugSetReader(null);
                InputRouter.SetBuildMode(false);
                InputRouter.BuildDragActive = false;
                InputRouter.SetGameplayPaused(false);
                StrategyClock.Reset();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                LayoutLibraryPanelUIToolkit.Close();
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

    /// <summary>自检夹具：直接把某个战役目标记为完成（解锁信标用；正式完成路径由目标系统的自检覆盖）。</summary>
    internal static class CampaignObjectiveTrackerTestHooks
    {
        public static void Complete(CampaignState s, string objectiveId)
        {
            ObjectiveRecord rec = s.ObjectiveRecords?.FirstOrDefault(r => r.ObjectiveId == objectiveId);
            if (rec == null)
            {
                rec = new ObjectiveRecord { ObjectiveId = objectiveId };
                s.ObjectiveRecords = (s.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>()).Append(rec).ToArray();
            }
            rec.State = ObjectiveState.Completed;
        }
    }
}
