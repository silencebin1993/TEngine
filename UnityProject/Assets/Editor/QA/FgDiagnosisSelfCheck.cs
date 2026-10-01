using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
using BinGames.Sim.WorldGen;
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
using GameLogic.UI.Common;
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
    /// FG3-LOG-08 叠加层与根因诊断的自动验收（FG03 FGR-LOG-080～082；FGU-12；FGT-LOG-009“构造五种根源故障，都能追溯到正确的根源”；
    /// 卡片负向“多重根源（同时缺电又缺料）时的显示顺序”；必须同时交付“第一次出现堵塞时的引导（钩子）”“叠加层的快捷键”；FG-GAP-020 / 071、DEBT-FG3LOG03-09）。
    /// 全部起真实系统：真实世界模拟与家园（WorldSimulation.LoadHome）、真实电网结算、真实端口对账与传送带内核（堵塞追溯在 AOT）、真实管线内核、真实施工单、真 UXML 面板、真实存读档文件。
    /// 断言按原因链的“种类 + 指向谁”判断（DiagCode / TargetId），不按文字。
    /// R 根源（FGT-LOG-009）：R1 缺电 → 电网超载 → 发电机受损（修好后消失）；R1b 电网超载且没有坏的发电机 → 发电不够；R2 未接入电网；R3 输出口 → 传送带堵到装配站（不收物品）；
    ///   R4 输出口 → 传送带到头（格数与坐标）；R5 仓库满（建筑链 + 传送带网络链）；R6 输出口登记冲突（写明被谁占了）；R7 施工缺料；R8 流体网络没来源；R9 队列缺电并入电力链；R10 多重根源顺序。
    /// K 内核：K1 追溯 / 源头 / 锚点（直线、到头、分流、环不死循环）；K2 性能（15,000 格）。
    /// O 叠加层：O1 开关、互斥、O 键重开最近、快捷键（真实按键路径）；O2 表现（传送带着色器模式、流向标签、堵塞标记、施工方框、突袭线、流体标签、污染贴图像素、着色器属性）；
    ///   O3 选择器（真 UXML + 布局探针）；O4 诊断面板（真 UXML：清单、点原因跳镜头、全部正常、布局探针）；O5 悬停；O6 点原因跳镜头时建造模式不退出（FG-GAP-071）。
    /// S 存读档与时间：S1 叠加层状态跟存档（旧档为关）；S2 暂停与 0.5x～3x 诊断结果一致；S3 观察 / 不观察一致。
    /// H 钩子与图鉴；P 性能（800 座建筑诊断、叠加层重建、每帧不重建）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgDiagnosisSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/自检：FG 叠加层与根因诊断")]
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
            Line("\n[叠加层与根因诊断] 为什么不工作（五类以上根源）/ 多重根源顺序 / 点原因跳镜头 / 8 种叠加层与快捷键 / 选择器与面板 / 存读档 / 倍速 / 性能（FG3-LOG-08）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgdiag-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                PlayerPrefs.DeleteKey(SettingsPrefsKey); // 引导钩子“第一次”从干净的本机设置开始（结束时还原）。
                GameSettings.Load();
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
                HomeValleyPowerGrid.ResetForTests();
                PowerCoverageOverlayView.ResetForTests();
                OverlayService.ResetForTests();
                RootCauseDiagnosis.ResetForTests();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程），" +
                     $"图形设备 {SystemInfo.graphicsDeviceType}；诊断与叠加层在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行，按 5 倍折算），逐格追溯在 AOT 内核");

                Step(CheckData);
                Step(CheckKernelTrace);
                Step(CheckRootPowerOverload);
                Step(CheckRootUnconnected);
                Step(CheckRootRejectingSink);
                Step(CheckRootDeadEnd);
                Step(CheckRootStoreFull);
                Step(CheckRootPortTaken);
                Step(CheckRootConstruction);
                Step(CheckRootPipe);
                Step(CheckQueueChain);
                Step(CheckMultiRootOrder);
                Step(CheckOverlaySwitchAndKeys);
                Step(CheckOverlayOutsideHome);
                Step(CheckOverlayDrawing);
                Step(CheckPollutionPaint);
                Step(CheckOverlayHud);
                Step(CheckDiagnosisPanel);
                Step(CheckPanelLiveNumbers);
                Step(CheckDockExclusive);
                Step(CheckSlicedMatchesSync);
                Step(CheckHover);
                Step(CheckLocateKeepsBuildMode);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckHooksAndCodex);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"诊断自检抛异常：{e}");
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
                OverlayService.ResetForTests();
                RootCauseDiagnosis.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                PowerCoverageOverlayView.ResetForTests();
                SignalCoverageOverlayView.SetEnabled(false);
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                HomeGridService.Invalidate();
                WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                DiagnosisPanelUIToolkit.InWorldOverrideForTests = false;
                OverlayHudUIToolkit.InWorldOverrideForTests = false;
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
            Line($"  · [叠加层与根因诊断] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 场景夹具 ───────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 200)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            HomeValleyPowerGrid.ResetForTests();
            RootCauseDiagnosis.ResetForTests();
            CampaignState s = CampaignState.CreateNew("fgdiag-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = scrap;
            return s;
        }

        private static BuildingRecord Rec(CampaignState s, string anchor) => HomeGridService.FindBuilding(s, HomeValleyLayout.RegionId + ":" + anchor);

        private static void WaitSync() => WorldSimulation.StepMany(BeltPortService.SyncTicks(GameClock.StepHz) + 1);

        private static BuildingRecord ActivateWarehouse(CampaignState s)
        {
            BuildingRecord wh = Rec(s, "warehouse");
            wh.ConstructionState = BuildingConstructionState.Operational;
            HomeValleyPowerGrid.Recompute(s);
            WaitSync();
            return wh;
        }

        private static BeltPortService.Binding Port(BuildingRecord b, string portKey) => b != null ? BeltPortService.Find(b.BuildingId, portKey) : null;

        private static BeltPortInfo PortInfo(BeltPortService.Binding b)
        {
            BeltPortInfo info = default;
            if (b != null)
            {
                BeltNetworkService.Kernel.TryGetPortInfo(b.PortId, out info);
            }
            return info;
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

        private static bool Placeable(CampaignState s, string typeId, GridCell c, string ignore = null)
        {
            GridPlacementResult r = HomeGridService.ValidatePlacement(s, typeId, c, 0, asPlayerPlacement: false, ignoreBuildingId: ignore, checkCost: false);
            return r.Ok || r.Reasons.All(x => x.Code == GridBlockReason.Fog);
        }

        private static GridCell? FindFree(CampaignState s, string typeId, float fromCore, float toCore, string ignore = null)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (int a = 0; a < 72; a++)
            {
                float ang = a * 5f * Mathf.Deg2Rad;
                for (float d = fromCore; d <= toCore; d += 1f)
                {
                    var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * d), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * d));
                    if (Placeable(s, typeId, c, ignore))
                    {
                        return c;
                    }
                }
            }
            return null;
        }

        /// <summary>场景夹具：一座已建成的建筑（真实建造路径由 FG3-LOG-01 / 02 验证），可带朝向。</summary>
        private static BuildingRecord AddBuilt(CampaignState s, string typeId, string key, GridCell pivot, int rotation = 0)
        {
            BuildingGrid g = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":diag_" + key,
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = pivot.X,
                GridY = pivot.Y,
                Rotation = rotation,
                Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, rotation),
                Health = 100f,
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.Invalidate();
            HomeGridService.MapFor(s);
            return r;
        }

        private static void MoveTo(CampaignState s, BuildingRecord b, GridCell pivot)
        {
            BuildingGrid g = GridContent.Building(b.BuildingTypeId);
            b.GridX = pivot.X;
            b.GridY = pivot.Y;
            b.Rotation = 0;
            b.Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, 0);
            HomeGridService.Invalidate();
            HomeGridService.MapFor(s);
        }

        private static BeltDir DirTo(GridCell a, GridCell b) =>
            b.X > a.X ? BeltDir.East : b.X < a.X ? BeltDir.West : b.Y > a.Y ? BeltDir.North : BeltDir.South;

        /// <summary>从 from 到 to 的传送带路线（广度优先，只走格网允许铺带的格，同一地图同一路线）。</summary>
        private static List<(GridCell cell, BeltDir dir)> Route(CampaignState s, GridCell from, GridCell to, BeltDir lastDir, int margin = 14)
        {
            bool Ok(GridCell c) => HomeGridService.ValidateBeltCell(s, c).Ok;
            if (!Ok(from) || !Ok(to))
            {
                return null;
            }
            int minX = Math.Min(from.X, to.X) - margin, maxX = Math.Max(from.X, to.X) + margin;
            int minY = Math.Min(from.Y, to.Y) - margin, maxY = Math.Max(from.Y, to.Y) + margin;
            var parent = new Dictionary<GridCell, GridCell> { [from] = from };
            var q = new Queue<GridCell>();
            q.Enqueue(from);
            int[] dx = { 0, 1, 0, -1 };
            int[] dy = { 1, 0, -1, 0 };
            while (q.Count > 0)
            {
                GridCell c = q.Dequeue();
                if (c == to)
                {
                    break;
                }
                for (int d = 0; d < 4; d++)
                {
                    var n = new GridCell(c.X + dx[d], c.Y + dy[d]);
                    if (n.X < minX || n.X > maxX || n.Y < minY || n.Y > maxY || parent.ContainsKey(n) || !Ok(n))
                    {
                        continue;
                    }
                    parent[n] = c;
                    q.Enqueue(n);
                }
            }
            if (!parent.ContainsKey(to))
            {
                return null;
            }
            var cells = new List<GridCell>();
            for (GridCell c = to; ; c = parent[c])
            {
                cells.Add(c);
                if (c == from)
                {
                    break;
                }
            }
            cells.Reverse();
            var path = new List<(GridCell, BeltDir)>(cells.Count);
            for (int i = 0; i < cells.Count; i++)
            {
                path.Add((cells[i], i + 1 < cells.Count ? DirTo(cells[i], cells[i + 1]) : lastDir));
            }
            return path;
        }

        private static bool Lay(CampaignState s, List<(GridCell cell, BeltDir dir)> path, int tier, out string failure)
        {
            failure = null;
            if (path == null)
            {
                failure = "找不到路线";
                return false;
            }
            foreach ((GridCell cell, BeltDir dir) in path)
            {
                BeltOpResult r = BeltNetworkService.TryPlace(s, cell, dir, tier);
                if (!r.Ok)
                {
                    failure = $"{cell}：{r.Describe()}";
                    return false;
                }
            }
            return true;
        }

        private static DiagChain ChainOf(DiagReport r, DiagCategory cat) => r?.Chains.FirstOrDefault(c => c.Category == cat);

        private static string Codes(DiagChain c) => c == null ? "（无）" : string.Join(" → ", c.Steps.Select(x => x.Code.ToString()));

        private static bool CodesAre(DiagChain c, params DiagCode[] codes) => c != null && c.Steps.Select(x => x.Code).SequenceEqual(codes);

        private static DiagReport Find(List<DiagReport> list, string subjectId) => list.FirstOrDefault(r => r.SubjectId == subjectId);

        private static List<DiagReport> CollectNow(CampaignState s)
        {
            var list = new List<DiagReport>();
            RootCauseDiagnosis.Collect(s, list);
            return list;
        }

        // ── 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string[] keys =
            {
                "diag.step.damaged", "diag.step.repair_no_labor", "diag.step.disabled", "diag.step.unconnected", "diag.step.brownout", "diag.step.grid_overload",
                "diag.step.supply_damaged", "diag.step.supply_disabled", "diag.step.grid_short_root", "diag.step.output_blocked", "diag.step.belt_downstream",
                "diag.step.belt_saturated", "diag.step.belt_loop", "diag.step.belt_terminal", "diag.step.store_full", "diag.step.port_taken", "diag.step.store_empty",
                "diag.step.queue_blocked", "diag.step.queue_no_power", "diag.step.queue_exit", "diag.step.site", "diag.subject.belt_net", "diag.step.belt_net",
                "diag.subject.pipe_net", "diag.step.pipe_net", "diag.step.pipe_no_source", "diag.step.pipe_limited", "diag.step.pipe_shortage",
                "diag.cat.structure", "diag.cat.power", "diag.cat.input", "diag.cat.output", "diag.chain.arrow", "diag.hover.title", "diag.hover.line", "diag.hover.more",
                "diag.panel.title", "diag.panel.count", "diag.panel.summary", "diag.panel.all_ok", "diag.panel.subject", "diag.panel.chain", "diag.panel.step", "diag.panel.root",
                "diag.panel.locate_tip", "diag.panel.more", "diag.panel.hint",
                "overlay.label.flow", "overlay.label.flow_measuring", "overlay.label.flow_blocked", "overlay.label.stopped", "overlay.label.fluid", "overlay.label.raid",
                "overlay.label.outpost", "overlay.label.construction", "overlay.hud.button", "overlay.hud.none", "overlay.hud.active", "overlay.hud.tip",
                "overlay.selector.title", "overlay.selector.item", "overlay.selector.item_on", "overlay.selector.off", "overlay.selector.no_legend", "overlay.selector.diag",
                "overlay.selector.hint", "codex.logistics.diagnosis.title", "codex.logistics.diagnosis.body", "codex.logistics.diagnosis.hint",
                "overlay.home_only.selector", "overlay.home_only.kind", "notify.type.overlay_home_only.name", "notify.type.overlay_home_only.single",
            };
            var missing = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string k in keys)
                {
                    if (GameText.ContainsMarker(GameText.Get(k)))
                    {
                        missing.Add(lang + ":" + k);
                    }
                }
                for (int i = 1; i <= OverlayService.KindCount; i++)
                {
                    var kind = (OverlayKind)i;
                    if (GameText.ContainsMarker(OverlayService.Name(kind)) || GameText.ContainsMarker(OverlayService.Legend(kind)))
                    {
                        missing.Add(lang + ":overlay." + OverlayService.Slug(kind));
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(missing.Count == 0, $"数据：诊断 / 叠加层 / 图鉴全部文本键中英都有（{keys.Length} 个 + 8 种叠加层名字与图例）" + (missing.Count == 0 ? string.Empty : "；缺：" + string.Join(", ", missing.Take(8))));

            bool tuning = GridContent.Tuning("diag.refresh_seconds") > 0f && GridContent.TuningInt("diag.max_reports") >= 8 && GridContent.TuningInt("overlay.max_labels") >= 4
                          && GridContent.TuningInt("overlay.max_markers") >= 8 && GridContent.Tuning("overlay.raid_outpost_radius") > 0f && GridContent.Tuning("overlay.refresh_seconds") > 0f
                          && GridContent.TuningInt("diag.slice_buildings") >= 4;
            Expect(tuning, "数据：diag.* / overlay.* 调参在 fg.TbHomeTuning（刷新间隔、清单上限、每帧诊断建筑数、标签 / 标记上限、突袭来路半径）");

            InputBindingSet binds = GameSettings.KeyBindings;
            string o = InputDisplay.ForAction(GameActionId.ToggleOverlay);
            string sel = InputDisplay.ForAction(GameActionId.OverlaySelector);
            string diag = InputDisplay.ForAction(GameActionId.OpenDiagnosis);
            var direct = Enumerable.Range(1, OverlayService.KindCount).Select(i => binds.GetChord(OverlayService.ActionOf((OverlayKind)i))).ToList();
            bool keysOk = binds.GetChord(GameActionId.ToggleOverlay).Key == KeyCode.O && binds.GetChord(GameActionId.OverlaySelector).Key == KeyCode.O
                          && (binds.GetChord(GameActionId.OverlaySelector).Mods & InputModifier.Alt) != 0 && (binds.GetChord(GameActionId.OpenDiagnosis).Mods & InputModifier.Ctrl) != 0
                          && direct.Select((c, i) => c.Key == KeyCode.Alpha1 + i && (c.Mods & (InputModifier.Ctrl | InputModifier.Alt)) == (InputModifier.Ctrl | InputModifier.Alt)).All(x => x)
                          && InputActionCatalog.TryGet(GameActionId.OverlayConstruction, out InputActionDef def) && def.Status == InputActionStatus.Wired;
            Expect(keysOk, $"数据：叠加层快捷键可重绑（fg.TbInputAction wired）：切换 {o}、选择器 {sel}、为什么不工作 {diag}、8 种直达 {InputDisplay.ForAction(GameActionId.OverlayFlow)}～{InputDisplay.ForAction(GameActionId.OverlayConstruction)}");
        }

        // ── K 内核 ─────────────────────────────────────────────────────────────

        private static void CheckKernelTrace()
        {
            using (var k = new BeltKernel(BeltNetworkService.ReadConfig()))
            {
                // 直线 10 格，尽头什么都没接：源头 = 最后一格“到头了”，其余 9 格被连累。
                for (int x = 0; x < 10; x++)
                {
                    k.AddCell(x, 0, BeltDir.East, 0);
                }
                // 另一条独立的 3 格（锚点测试）。
                for (int x = 0; x < 3; x++)
                {
                    k.AddCell(x, 5, BeltDir.East, 0);
                }
                k.AddSource(1, 0, 0, 1, 1, -1);
                k.StepMany(4000);
                bool traced = k.TryTraceBlock(0, 0, out BeltBlockTrace t);
                var roots = new List<BeltCellInfo>();
                int total = k.CollectBlockRoots(roots, 8);
                var anchors = new List<Unity.Mathematics.int3>();
                k.CollectNetworkAnchors(anchors);
                Expect(traced && t.End.X == 9 && t.End.Y == 0 && t.End.Block == BeltBlock.EndOfBelt && t.Hops == 9 && !t.Looped
                       && total == 1 && roots.Count == 1 && roots[0].X == 9 && roots[0].Block == BeltBlock.EndOfBelt
                       && anchors.Count == 2 && anchors.Select(a => a.z).Distinct().Count() == 2,
                    $"K1 堵塞追溯（AOT）：满载的直线从起点追到 ({t.End.X}, {t.End.Y})“{t.End.Block}”、往下游 {t.Hops} 格；堵塞源头只有尽头那 1 格（{total}），" +
                    $"被连累的 9 格不算源头；两条互不相连的带各一个锚点（{anchors.Count}）");
                bool none = k.TryTraceBlock(1, 5, out BeltBlockTrace t2) && t2.Hops == 0 && t2.End.Block == BeltBlock.None;
                bool missing = !k.TryTraceBlock(50, 50, out _);
                Expect(none && missing, "K1 没堵的格：追溯停在自己（原因“没有”，0 格）；没有传送带的格：追溯返回失败");
            }
            using (var k = new BeltKernel(BeltNetworkService.ReadConfig()))
            {
                // 4×4 环，塞满后再追溯：不管环上转不转，追溯都在走满一圈之内结束（不死循环）。
                var ring = new List<(int x, int y, BeltDir d)>();
                for (int x = 0; x < 3; x++) ring.Add((x, 0, BeltDir.East));
                for (int y = 0; y < 3; y++) ring.Add((3, y, BeltDir.North));
                for (int x = 3; x > 0; x--) ring.Add((x, 3, BeltDir.West));
                for (int y = 3; y > 0; y--) ring.Add((0, y, BeltDir.South));
                foreach ((int x, int y, BeltDir d) in ring)
                {
                    k.AddCell(x, y, d, 0);
                }
                for (int i = 0; i < 200; i++)
                {
                    foreach ((int x, int y, BeltDir _) in ring)
                    {
                        k.InsertItem(x, y, 1);
                    }
                    k.Step();
                }
                bool ok = k.TryTraceBlock(0, 0, out BeltBlockTrace t);
                Expect(ok && t.Hops <= ring.Count + 1 && (t.Looped || t.End.Block != BeltBlock.DownstreamFull),
                    $"K1 满载的环形传送带：追溯在一圈之内结束（{t.Hops} 格，整圈满 = {t.Looped}，源头原因 {t.End.Block}），不会死循环");
            }
        }

        // ── R 根源（FGT-LOG-009）──────────────────────────────────────────────

        private static void CheckRootPowerOverload()
        {
            CampaignState s = NewWorld(9801);
            HomeValleyPowerGrid.Recompute(s);
            BuildingRecord asm = Rec(s, "assembly_station");
            BuildingRecord gen = Rec(s, "generator");
            DiagReport r = RootCauseDiagnosis.DiagnoseBuilding(s, asm);
            DiagChain p = ChainOf(r, DiagCategory.Power);
            Expect(asm.PowerState == BuildingPowerState.Brownout && gen.ConstructionState == BuildingConstructionState.Damaged
                   && CodesAre(p, DiagCode.Brownout, DiagCode.GridOverload, DiagCode.SupplyDamaged) && p.Root.TargetId == gen.BuildingId
                   && p.Root.Position == gen.Position && p.Steps[1].Text.Contains("电网 1") && p.Root.Text.Contains(HomeGridService.DisplayName(gen.BuildingTypeId)),
                $"R1 缺电 → 电网超载 → 发电机受损（FGR-LOG-082 例子同构）：新家园发电机受损、装配站缺电，原因链 {Codes(p)}，根源指向发电机（点它镜头到发电机）：" +
                $"“{RootCauseDiagnosis.ChainText(p)}”");
            gen.ConstructionState = BuildingConstructionState.Operational;
            HomeValleyPowerGrid.Recompute(s);
            DiagReport after = RootCauseDiagnosis.DiagnoseBuilding(s, asm);
            Expect(asm.PowerState == BuildingPowerState.Powered && ChainOf(after, DiagCategory.Power) == null,
                "R1 修好发电机、电网重算后装配站有电：它的“为什么不工作”里不再有电力原因");
            // R1b：电网里没有坏的发电机，但用电超过发电 → 根源“发电不够”（指向电网锚点）。
            int added = 0;
            for (int i = 0; i < 8; i++)
            {
                GridCell? c = FindFree(s, HomeValleyLayout.BuildingTypeRepairBay, 8f, 22f);
                if (!c.HasValue)
                {
                    break;
                }
                AddBuilt(s, HomeValleyLayout.BuildingTypeRepairBay, "bay" + i, c.Value);
                added++;
            }
            HomeValleyPowerGrid.Recompute(s);
            BuildingRecord brown = s.BuildingRecords.FirstOrDefault(b => b.PowerState == BuildingPowerState.Brownout);
            DiagChain p2 = ChainOf(RootCauseDiagnosis.DiagnoseBuilding(s, brown), DiagCategory.Power);
            Expect(added >= 5 && brown != null && CodesAre(p2, DiagCode.Brownout, DiagCode.GridOverload, DiagCode.GridShortRoot) && p2.Root.Text.Contains("发电不够"),
                $"R1b 加 {added} 座维修台让用电超过发电（发电机完好）：缺电的 {HomeGridService.DisplayName(brown?.BuildingTypeId)} 原因链 {Codes(p2)}，根源“{p2?.Root?.Text}”");
        }

        private static void CheckRootUnconnected()
        {
            CampaignState s = NewWorld(9802);
            BuildingRecord bay = Rec(s, "repair_bay");
            GridCell home = new GridCell(bay.GridX, bay.GridY);
            GridCell? far = FindFree(s, HomeValleyLayout.BuildingTypeRepairBay, 34f, 46f, bay.BuildingId);
            if (!far.HasValue)
            {
                Fail("R2 找不到覆盖外的空位");
                return;
            }
            MoveTo(s, bay, far.Value);
            HomeValleyPowerGrid.Recompute(s);
            DiagChain p = ChainOf(RootCauseDiagnosis.DiagnoseBuilding(s, bay), DiagCategory.Power);
            Expect(bay.PowerState == BuildingPowerState.Unpowered && CodesAre(p, DiagCode.Unconnected) && p.Root.TargetId == bay.BuildingId && p.Root.Text.Contains("未接入电网"),
                $"R2 未接入电网：维修台挪到核心配电半径外，原因链 {Codes(p)}，根源就是它自己不在覆盖里（“{p?.Root?.Text}”）");
            MoveTo(s, bay, home);
            HomeValleyPowerGrid.Recompute(s);
            Expect(bay.PowerState != BuildingPowerState.Unpowered && ChainOf(RootCauseDiagnosis.DiagnoseBuilding(s, bay), DiagCategory.Power)?.Symptom?.Code != DiagCode.Unconnected,
                "R2 挪回覆盖内后不再报“未接入电网”");
        }

        private static void CheckRootRejectingSink()
        {
            CampaignState s = NewWorld(9803, scrap: 400);
            BuildingRecord wh = ActivateWarehouse(s);
            BuildingRecord asm = Rec(s, "assembly_station");
            BeltPortService.Binding outP = Port(wh, "warehouse.out0");
            BeltPortService.Binding asmIn = Port(asm, "assembly_station.in0");
            if (outP == null || asmIn == null)
            {
                Fail("R3 端口没有登记");
                return;
            }
            bool hookBefore = GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsFirstOutputBlocked);
            BeltDir into = (BeltDir)(((int)asmIn.Face + 2) & 3);
            List<(GridCell cell, BeltDir dir)> path = Route(s, outP.BeltCell, asmIn.BeltCell, into, 20);
            if (!Lay(s, path, 2, out string failure))
            {
                Fail("R3 铺不下仓库 → 装配站的传送带：" + failure);
                return;
            }
            bool blocked = StepUntil(() =>
            {
                BeltPortInfo pi = PortInfo(outP);
                return pi.Pending > 0 && BeltNetworkService.Kernel.TryGetCellInfo(outP.BeltCell.X, outP.BeltCell.Y, out BeltCellInfo h) && h.Block != BeltBlock.None && h.Count > 0;
            }, 240);
            WaitSync();
            DiagReport r = RootCauseDiagnosis.DiagnoseBuilding(s, wh);
            DiagChain c = ChainOf(r, DiagCategory.Output);
            GridCell last = path[path.Count - 1].cell;
            Expect(blocked && c != null && c.Symptom.Code == DiagCode.OutputBlocked && c.Root.Code == DiagCode.BeltTerminal && c.Root.TargetId == asm.BuildingId
                   && c.Root.Text.Contains("暂不接收物品") && c.Steps.Any(x => x.Code == DiagCode.BeltDownstream) && c.Root.Position == asm.Position,
                $"R3 仓库输出口 → 传送带（{path.Count} 格）→ 装配站还不收物品（FG-GAP-020：输出口推不上去追溯到下游）：原因链 {Codes(c)}，根源指向装配站（“{c?.Root?.Text}”）");
            Expect(!hookBefore && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsFirstOutputBlocked),
                "H 卡片“第一次出现堵塞时的引导”：建筑输出口第一次因为下游堵住推不出去时，端口对账发出引导钩子 logistics.port.first_output_blocked（内容在 FG15-UX-04）");
            // 负向：传送带没有接到任何建筑的末端格（最后一格）不是“停工的建筑”，但会作为传送带网络的源头之一出现在清单里——同一个源头不重复列。
            List<DiagReport> all = CollectNow(s);
            bool noDup = all.Count(x => x.Subject == DiagSubject.BeltNetwork && x.Chains.Any(ch => ch.Root.Position == asm.Position)) == 0;
            Expect(Find(all, wh.BuildingId) != null && noDup, $"R3 清单里有仓库一行；这个源头已经在仓库的原因链里解释过，传送带网络不再重复列一遍（{last}）");
        }

        private static void CheckRootDeadEnd()
        {
            CampaignState s = NewWorld(9804, scrap: 200);
            BuildingRecord wh = ActivateWarehouse(s);
            BeltPortService.Binding outP = Port(wh, "warehouse.out0");
            var path = new List<(GridCell cell, BeltDir dir)>();
            var dir = (BeltDir)outP.Face;
            GridCell c0 = outP.BeltCell;
            for (int i = 0; i < 6; i++)
            {
                var c = new GridCell(c0.X + BeltDirs.Dx((int)dir) * i, c0.Y + BeltDirs.Dy((int)dir) * i);
                path.Add((c, dir));
            }
            if (!Lay(s, path, 0, out string failure))
            {
                Fail("R4 铺不下 6 格直线：" + failure);
                return;
            }
            GridCell end = path[path.Count - 1].cell;
            bool full = StepUntil(() => BeltNetworkService.Kernel.TryGetCellInfo(c0.X, c0.Y, out BeltCellInfo h) && h.Block != BeltBlock.None && h.Count > 0 && PortInfo(outP).Pending > 0, 240);
            DiagChain c1 = ChainOf(RootCauseDiagnosis.DiagnoseBuilding(s, wh), DiagCategory.Output);
            Expect(full && CodesAre(c1, DiagCode.OutputBlocked, DiagCode.BeltDownstream, DiagCode.BeltTerminal) && c1.Root.Position == new Vector2(end.X, end.Y)
                   && c1.Steps[1].Text.Contains("5 格") && c1.Root.Text.Contains("到头"),
                $"R4 输出口 → 传送带到头：6 格直线尽头没接东西，原因链 {Codes(c1)}，根源在尽头 ({end.X}, {end.Y})（“{c1?.Root?.Text}”）");
        }

        private static void CheckRootStoreFull()
        {
            CampaignState s = NewWorld(9805, scrap: 60);
            BuildingRecord wh = ActivateWarehouse(s);
            BeltPortService.Binding outP = Port(wh, "warehouse.out0");
            BeltPortService.Binding inP = Port(wh, "warehouse.in0");
            BeltDir intoIn = (BeltDir)(((int)inP.Face + 2) & 3);
            List<(GridCell cell, BeltDir dir)> path = Route(s, outP.BeltCell, inP.BeltCell, intoIn);
            if (!Lay(s, path, 2, out string failure))
            {
                Fail("R5 仓库闭环铺不下：" + failure);
                return;
            }
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", BeltPortService.FilterOff, out _);
            for (int k = 0; k < path.Count - 1; k++)
            {
                BeltNetworkService.Kernel.InsertItem(path[k].cell.X, path[k].cell.Y, BeltItems.ScrapId);
            }
            int cap = HomeValleyCargo.GetStorageCapacity(s, CampaignEconomyLedger.ResourceScrap);
            s.Scrap = cap;
            GridCell last = path[path.Count - 1].cell;
            bool jammed = StepUntil(() => BeltNetworkService.Kernel.TryGetCellInfo(last.X, last.Y, out BeltCellInfo c) && c.Block == BeltBlock.SinkFull, 60);
            List<DiagReport> all = CollectNow(s);
            DiagChain own = ChainOf(Find(all, wh.BuildingId), DiagCategory.Output);
            DiagReport net = all.FirstOrDefault(r => r.Subject == DiagSubject.BeltNetwork);
            DiagChain nc = net?.Primary;
            Expect(jammed && own != null && own.Symptom.Code == DiagCode.StoreFull && own.Symptom.Text.Contains(cap + " / " + cap)
                   && nc != null && CodesAre(nc, DiagCode.BeltNetwork, DiagCode.StoreFullRoot) && nc.Root.TargetId == wh.BuildingId,
                $"R5 仓库满：仓库自己一行“{own?.Symptom?.Text}”；传送带网络一行 {Codes(nc)}，根源指向仓库（“{nc?.Root?.Text}”）");
        }

        private static void CheckRootPortTaken()
        {
            CampaignState s = NewWorld(9806, scrap: 100);
            BuildingRecord w1 = ActivateWarehouse(s);
            // 第二座仓库转 180°、放在第一座东边 6 格：它的输出口朝西，外面那格正好是第一座输出口外面那格（同一格只能登记一个输出口）。
            GridCell p1 = new GridCell(w1.GridX, w1.GridY);
            GridCell p2 = new GridCell(p1.X + 6, p1.Y);
            if (!Placeable(s, "warehouse", p2))
            {
                GridCell? q = null;
                GridCell core = HomeGridService.CorePivot(s);
                for (int a = 0; a < 72 && !q.HasValue; a++)
                {
                    float ang = a * 5f * Mathf.Deg2Rad;
                    for (float d = 14f; d <= 44f && !q.HasValue; d += 1f)
                    {
                        var c = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * d), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * d));
                        if (Placeable(s, "warehouse", c, w1.BuildingId) && Placeable(s, "warehouse", new GridCell(c.X + 6, c.Y), w1.BuildingId))
                        {
                            q = c;
                        }
                    }
                }
                if (!q.HasValue)
                {
                    Fail("R6 找不到能并排放两座仓库的地方");
                    return;
                }
                MoveTo(s, w1, q.Value);
                p1 = q.Value;
                p2 = new GridCell(p1.X + 6, p1.Y);
                WaitSync();
            }
            BuildingRecord w2 = AddBuilt(s, "warehouse", "w2", p2, 180);
            WaitSync();
            BeltPortService.Binding o1 = Port(w1, "warehouse.out0");
            BeltPortService.Binding o2 = Port(w2, "warehouse.out0");
            DiagChain c2 = ChainOf(RootCauseDiagnosis.DiagnoseBuilding(s, w2), DiagCategory.Output);
            Expect(o1 != null && o2 == null && CodesAre(c2, DiagCode.PortTaken) && c2.Root.TargetId == w1.BuildingId && c2.Root.Text.Contains("已被")
                   && c2.Root.Position == new Vector2(o1.BeltCell.X, o1.BeltCell.Y),
                $"R6 输出口登记冲突（DEBT-FG3LOG03-09）：两座仓库的输出口推到同一格，后一座登记不上；原因写明“{c2?.Root?.Text}”，点它镜头到那一格");
        }

        private static void CheckRootConstruction()
        {
            CampaignState s = NewWorld(9807, scrap: 0);
            GridCell? spot = FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 10f, 40f);
            GridOpResult r = spot.HasValue ? HomeGridService.TryPlace(s, HomeValleyLayout.BuildingTypeGenerator2, spot.Value, 0) : default;
            WorkOrderRecord order = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).FirstOrDefault(o => o != null && o.Kind == WorkOrderKind.Build && o.TargetId == r.BuildingId);
            bool waiting = order != null && StepUntil(() => order.State == WorkOrderState.Waiting, 30);
            List<DiagReport> all = CollectNow(s);
            DiagReport site = all.FirstOrDefault(x => x.Subject == DiagSubject.Site && x.SubjectId == order?.WorkOrderId);
            DiagChain c = site?.Primary;
            BuildingRecord ghost = HomeGridService.FindBuilding(s, r.BuildingId);
            Expect(r.Success && waiting && CodesAre(c, DiagCode.SiteStalled, DiagCode.SiteMaterials) && c.Category == DiagCategory.Input && c.Root.Text.Contains("等待材料")
                   && ghost != null && site.Position == ghost.Position,
                $"R7 施工缺料：库存 0 放发电机虚影，施工单等待材料；清单“{site?.Name}”原因链 {Codes(c)}（“{c?.Root?.Text}”），点它镜头到虚影");
        }

        private static void CheckRootPipe()
        {
            CampaignState s = NewWorld(9808, scrap: 200);
            if (!PipeNetworkService.IsRunning)
            {
                Fail("R8 管线内核没有运行");
                return;
            }
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? start = null;
            for (int a = 0; a < 72 && !start.HasValue; a++)
            {
                float ang = a * 5f * Mathf.Deg2Rad;
                for (float d = 12f; d <= 40f && !start.HasValue; d += 1f)
                {
                    var c0 = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(ang) * d), core.Y + Mathf.RoundToInt(Mathf.Sin(ang) * d));
                    var placed = new List<GridCell>();
                    bool ok = true;
                    for (int i = 0; i < 4 && ok; i++)
                    {
                        var ci = new GridCell(c0.X + i, c0.Y);
                        ok = PipeNetworkService.TryPlace(s, ci, PipePieceKind.Pipe, 0, 0).Ok;
                        if (ok)
                        {
                            placed.Add(ci);
                        }
                    }
                    if (ok)
                    {
                        start = c0;
                    }
                    else
                    {
                        foreach (GridCell p in placed)
                        {
                            PipeNetworkService.TryRemove(s, p, out _);
                        }
                    }
                }
            }
            if (!start.HasValue)
            {
                Fail("R8 铺不下 4 格管线");
                return;
            }
            int water = PipeNetworkService.FluidId("water");
            // 耗流体的建筑在 FG4-ECO-02～04（DEBT-FG3LOG05-02）：测试捷径直接在内核挂一个消费者（管线自检同一写法）。
            PipeNetworkService.Kernel.AddConsumer(start.Value.X + 2, start.Value.Y, water, 300, 1);
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            List<DiagReport> all = CollectNow(s);
            DiagReport pr = all.FirstOrDefault(x => x.Subject == DiagSubject.PipeNetwork);
            DiagChain c = pr?.Primary;
            Expect(CodesAre(c, DiagCode.PipeNetwork, DiagCode.PipeNoSource) && c.Category == DiagCategory.Input && c.Root.Text.Contains("没有来源"),
                $"R8 流体网络没有来源（DEBT-FG3LOG05-07）：4 格管线挂 300 升/分的需求、没有泵，清单“{pr?.Name}”原因链 {Codes(c)}（“{c?.Root?.Text}”）");
        }

        private static void CheckQueueChain()
        {
            CampaignState s = NewWorld(9809);
            HomeValleyPowerGrid.Recompute(s);
            BuildingRecord asm = Rec(s, "assembly_station");
            BuildingRecord bench = Rec(s, "analysis_bench");
            // 队列状态由 ER4 / ER6 的生产与解析计时写入（它们各自的自检覆盖）；这里注入“卡在缺电”的队列项，只验证诊断把它接到电力链上。
            s.FactoryQueues = new[] { new FactoryQueueItemRecord { QueueItemId = "diag-q1", State = FactoryQueueState.WaitingPower, BlockedReason = "assembly-station-unpowered" } };
            s.AnalysisQueues = new[] { new AnalysisQueueItemRecord { QueueItemId = "diag-a1", State = AnalysisQueueState.WaitingPower, BlockedReason = "analysis-bench-unpowered" } };
            DiagReport r = RootCauseDiagnosis.DiagnoseBuilding(s, asm);
            DiagReport rb = RootCauseDiagnosis.DiagnoseBuilding(s, bench);
            DiagChain p = ChainOf(r, DiagCategory.Power);
            DiagChain pb = ChainOf(rb, DiagCategory.Power);
            Expect(r.Chains.Count(c => c.Category == DiagCategory.Power) == 1
                   && CodesAre(p, DiagCode.QueueBlocked, DiagCode.Brownout, DiagCode.GridOverload, DiagCode.SupplyDamaged)
                   && pb != null && pb.Symptom.Code == DiagCode.QueueBlocked && pb.Root.Code == DiagCode.SupplyDamaged,
                $"R9 生产 / 解析队列因缺电停住：“队列停住”接在建筑自己的电力链最前面（只一条，不重复列）：装配站 {Codes(p)}；解析台 {Codes(pb)}");
        }

        private static void CheckMultiRootOrder()
        {
            CampaignState s = NewWorld(9810);
            HomeValleyPowerGrid.Recompute(s);
            BuildingRecord asm = Rec(s, "assembly_station");
            // 装配站同时：缺电（电力）、出口被挡（输出）、缺材料（输入）、目标不在（建筑本身）。按加入顺序是 电力 → 输出 → 输入 → 建筑本身，显示必须按固定顺序。
            s.FactoryQueues = new[]
            {
                new FactoryQueueItemRecord { QueueItemId = "m1", State = FactoryQueueState.OutputBlocked, BlockedReason = "exit-blocked" },
                new FactoryQueueItemRecord { QueueItemId = "m2", State = FactoryQueueState.WaitingResources, BlockedReason = "insufficient-scrap" },
                new FactoryQueueItemRecord { QueueItemId = "m3", State = FactoryQueueState.WaitingTarget, BlockedReason = "target-not-alive" },
            };
            DiagReport r = RootCauseDiagnosis.DiagnoseBuilding(s, asm);
            string order = string.Join(" → ", r.Chains.Select(c => RootCauseDiagnosis.CategoryName(c.Category)));
            Expect(r.Chains.Select(c => c.Category).SequenceEqual(new[] { DiagCategory.Structure, DiagCategory.Power, DiagCategory.Input, DiagCategory.Output })
                   && r.Primary.Category == DiagCategory.Structure,
                $"R10 负向“多重根源”：同一座建筑有 4 类原因时按“建筑本身 → 电力 → 输入 → 输出”显示（{order}），第一条是主因（堵塞叠加层与标签用它）");
            // 同时缺电又缺料（卡片原话）：只留这两类。
            s.FactoryQueues = new[] { new FactoryQueueItemRecord { QueueItemId = "m2", State = FactoryQueueState.WaitingResources, BlockedReason = "insufficient-scrap" } };
            DiagReport r2 = RootCauseDiagnosis.DiagnoseBuilding(s, asm);
            string hover = RootCauseDiagnosis.TryDescribeForHover(s, asm, out string h) ? h : string.Empty;
            int iPower = hover.IndexOf("1. 电力", StringComparison.Ordinal);
            int iInput = hover.IndexOf("2. 输入", StringComparison.Ordinal);
            Expect(r2.Chains.Select(c => c.Category).SequenceEqual(new[] { DiagCategory.Power, DiagCategory.Input }) && iPower >= 0 && iInput > iPower,
                $"R10 同时缺电又缺料：先电力、后输入（悬停里也是这个顺序：“{hover.Replace("\n", " / ")}”）");
            // 没有问题的建筑：不在清单里。
            s.FactoryQueues = Array.Empty<FactoryQueueItemRecord>();
            Rec(s, "generator").ConstructionState = BuildingConstructionState.Operational;
            HomeValleyPowerGrid.Recompute(s);
            Expect(RootCauseDiagnosis.DiagnoseBuilding(s, asm) == null && !RootCauseDiagnosis.TryDescribeForHover(s, asm, out _),
                "R10 修好发电机、清空队列后装配站没有任何原因：不进清单、悬停不写“为什么不工作”");
        }

        // ── O 叠加层 ───────────────────────────────────────────────────────────

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
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        private static void Press(KeyReader reader, GameActionId action)
        {
            InputChord chord = GameSettings.KeyBindings.GetChord(action);
            if ((chord.Mods & InputModifier.Ctrl) != 0) reader.Hold(KeyCode.LeftControl);
            if ((chord.Mods & InputModifier.Alt) != 0) reader.Hold(KeyCode.LeftAlt);
            if ((chord.Mods & InputModifier.Shift) != 0) reader.Hold(KeyCode.LeftShift);
            reader.Press(chord.Key);
            UiKitInputPump.ProcessWorldKeys();
            reader.EndFrame();
            InputRouter.DebugClearConsumedKeys();
        }

        private static void CheckOverlaySwitchAndKeys()
        {
            CampaignState s = NewWorld(9811);
            OverlayService.ResetForTests();
            OverlayService.Bind(s);
            var reader = new KeyReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            GameObject hudGo = null;
            GameObject panelGo = null;
            try
            {
                VisualElement hudRoot = MountUxml(UiKitFolder + "OverlayHud.uxml", out hudGo);
                OverlayHudUIToolkit hud = hudGo.AddComponent<OverlayHudUIToolkit>();
                hud.BindView(hudRoot);
                VisualElement panelRoot = MountUxml(UiKitFolder + "DiagnosisPanel.uxml", out panelGo);
                DiagnosisPanelUIToolkit panel = panelGo.AddComponent<DiagnosisPanelUIToolkit>();
                panel.BindView(panelRoot);
                DiagnosisPanelUIToolkit.InWorldOverrideForTests = true;
                OverlayHudUIToolkit.InWorldOverrideForTests = true;

                bool hookBefore = GameSettings.HasSeenGuidanceHook(GuidanceHooks.OverlayFirstOpen);
                Press(reader, GameActionId.ToggleOverlay);
                bool signal = OverlayService.Active == OverlayKind.Signal && SignalCoverageOverlayView.Enabled && s.Grid.OverlayActive == (int)OverlayKind.Signal;
                Press(reader, GameActionId.ToggleOverlay);
                bool off = OverlayService.Active == OverlayKind.None && !SignalCoverageOverlayView.Enabled;
                Expect(!hookBefore && signal && off && GameSettings.HasSeenGuidanceHook(GuidanceHooks.OverlayFirstOpen),
                    "O1 叠加层切换键（O，FG13 第 5 节）：从没用过时打开“信号覆盖”（与 FG1-SIG-07 的 O 键一致），再按关闭；第一次打开发引导钩子；当前叠加层写进战役");
                Press(reader, GameActionId.OverlayBlockage);
                bool blockage = OverlayService.Active == OverlayKind.Blockage && OverlayService.BeltOverlayMode == 2;
                Press(reader, GameActionId.OverlayPower);
                bool power = OverlayService.Active == OverlayKind.Power && PowerCoverageOverlayView.Enabled && !SignalCoverageOverlayView.Enabled;
                Press(reader, GameActionId.OverlaySignal);
                bool excl = OverlayService.Active == OverlayKind.Signal && SignalCoverageOverlayView.Enabled && !PowerCoverageOverlayView.Enabled;
                Press(reader, GameActionId.OverlaySignal);
                bool toggledOff = OverlayService.Active == OverlayKind.None;
                Press(reader, GameActionId.ToggleOverlay);
                bool reopened = OverlayService.Active == OverlayKind.Signal;
                Press(reader, GameActionId.OverlayFlow);
                Press(reader, GameActionId.ToggleOverlay);
                Press(reader, GameActionId.ToggleOverlay);
                bool reopenFlow = OverlayService.Active == OverlayKind.Flow && OverlayService.Last == OverlayKind.Flow && OverlayService.BeltOverlayMode == 1;
                Expect(blockage && power && excl && toggledOff && reopened && reopenFlow,
                    "O1 直达键 Ctrl+Alt+1～8（真实按键路径）：Ctrl+Alt+2 堵塞（传送带着色器模式 2）、Ctrl+Alt+3 电力覆盖、Ctrl+Alt+6 信号覆盖——同时只显示一种（电力 / 信号视图互斥）；" +
                    "同一个直达键再按一次关闭；O 重开最近用过的那一种（信号 → 物品流向）");
                // 电网面板的按钮 / 旧代码直接开关电力视图：服务以视图为准同步，仍然只显示一种。
                PowerCoverageOverlayView.SetEnabled(true);
                OverlayService.Toggle(OverlayKind.Power);
                bool syncOff = OverlayService.Active == OverlayKind.None && !PowerCoverageOverlayView.Enabled;
                Expect(syncOff, "O1 电网面板“显示 / 隐藏电力覆盖”与叠加层服务是同一个开关（面板打开的电力覆盖，再在选择器里点一次就关）");

                Press(reader, GameActionId.OverlaySelector);
                hud.Tick(null);
                bool selOpen = OverlayHudUIToolkit.SelectorOpen && hud.SelectorVisible;
                Press(reader, GameActionId.OverlaySelector);
                bool selClosed = !OverlayHudUIToolkit.SelectorOpen && !hud.SelectorVisible;
                Press(reader, GameActionId.OpenDiagnosis);
                bool diagOpen = DiagnosisPanelUIToolkit.IsOpen && panel.PanelVisible;
                Press(reader, GameActionId.OpenDiagnosis);
                bool diagClosed = !DiagnosisPanelUIToolkit.IsOpen;
                Expect(selOpen && selClosed && diagOpen && diagClosed,
                    $"O1 Alt+O 开关叠加层选择器、Ctrl+O 开关“为什么不工作”（{InputDisplay.ForAction(GameActionId.OverlaySelector)} / {InputDisplay.ForAction(GameActionId.OpenDiagnosis)}，真实按键路径，非模态）");
                // 确认框在最上面时不抢键。
                OverlayService.Set(OverlayKind.None);
                UiConfirmDialog.Show(new ConfirmRequest { Title = "t" });
                Press(reader, GameActionId.ToggleOverlay);
                bool blockedByConfirm = OverlayService.Active == OverlayKind.None;
                UiConfirmDialog.DiscardAll();
                Expect(blockedByConfirm, "O1 负向：确认框开着时叠加层键不生效");
            }
            finally
            {
                InputRouter.DebugSetReader(null);
                DiagnosisPanelUIToolkit.Close();
                OverlayHudUIToolkit.CloseSelector();
                DiagnosisPanelUIToolkit.InWorldOverrideForTests = false;
                OverlayHudUIToolkit.InWorldOverrideForTests = false;
                if (hudGo != null) Object.DestroyImmediate(hudGo);
                if (panelGo != null) Object.DestroyImmediate(panelGo);
                OverlayService.Set(OverlayKind.None);
            }
        }

        /// <summary>
        /// O1b 负向（审查 P2：家园以外 O 开关看不见的叠加层、Alt+O 静默失效）：远征地点（家园没被观察）——
        /// O 在最近用过的是“堵塞”时开关的是信号覆盖；Ctrl+Alt+2（堵塞）不切换、发说明；Alt+O 不打开选择器、发说明。
        /// </summary>
        private static void CheckOverlayOutsideHome()
        {
            CampaignState s = NewWorld(9812, observe: false);
            OverlayService.ResetForTests();
            OverlayService.Bind(s);
            OverlayService.Set(OverlayKind.Blockage);
            OverlayService.Set(OverlayKind.None);
            var reader = new KeyReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            GameObject hudGo = null;
            try
            {
                VisualElement hudRoot = MountUxml(UiKitFolder + "OverlayHud.uxml", out hudGo);
                OverlayHudUIToolkit hud = hudGo.AddComponent<OverlayHudUIToolkit>();
                hud.BindView(hudRoot);
                bool away = !OverlayService.HomeObserved && OverlayService.Last == OverlayKind.Blockage;
                Press(reader, GameActionId.ToggleOverlay);
                bool signal = OverlayService.Active == OverlayKind.Signal && SignalCoverageOverlayView.Enabled;
                Press(reader, GameActionId.ToggleOverlay);
                bool off = OverlayService.Active == OverlayKind.None;
                Expect(away && signal && off, "O1b 家园以外：最近用过的“堵塞”画不出来，O 开关的是信号覆盖（与 FG1-SIG-07 在远征地点的 O 键一致），不开一个看不见的叠加层");
                int rejected = OverlayService.RejectedCount;
                Press(reader, GameActionId.OverlayBlockage);
                bool kindRejected = OverlayService.Active == OverlayKind.None && OverlayService.RejectedCount == rejected + 1;
                Press(reader, GameActionId.OverlaySelector);
                hud.Tick(null);
                bool selRejected = !OverlayHudUIToolkit.SelectorOpen && OverlayService.RejectedCount == rejected + 2;
                NotificationEntry note = NotificationCenter.Toasts.LastOrDefault(e => e.Type != null && e.Type.Id == "overlay_home_only");
                string last = note?.Latest?.DetailText ?? string.Empty;
                Expect(kindRejected && selRejected && note != null && last.Contains(InputDisplay.ForAction(GameActionId.OverlaySignal)) && !GameText.ContainsMarker(last),
                    $"O1b 负向：家园以外 Ctrl+Alt+2（堵塞）不切换、Alt+O 不打开选择器，都发一条说明（“{last}”），不静默失效");
                Press(reader, GameActionId.OverlaySignal);
                Expect(OverlayService.Active == OverlayKind.Signal, "O1b 家园以外信号覆盖的直达键照常可用");
                OverlayService.Set(OverlayKind.None);
            }
            finally
            {
                InputRouter.DebugSetReader(null);
                OverlayHudUIToolkit.CloseSelector();
                if (hudGo != null) Object.DestroyImmediate(hudGo);
                OverlayService.Set(OverlayKind.None);
                UiEscapeStack.Clear();
            }
        }

        private static void CheckOverlayDrawing()
        {
            CampaignState s = NewWorld(9812, scrap: 0);
            OverlayService.ResetForTests();
            OverlayService.Bind(s);
            // 物品流向：两条传送带网络 → 两个标签（离镜头近的优先，≤ 上限）。
            GridCell? a = FindFree(s, "warehouse", 14f, 36f); // 5×3 的空地：两条短传送带放在它的上下两行
            int laid = 0;
            if (a.HasValue)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (BeltNetworkService.TryPlace(s, new GridCell(a.Value.X - 2 + i, a.Value.Y - 1), BeltDir.East, 0).Ok) laid++;
                }
                for (int i = 0; i < 3; i++)
                {
                    if (BeltNetworkService.TryPlace(s, new GridCell(a.Value.X - 2 + i, a.Value.Y + 1), BeltDir.East, 0).Ok) laid++;
                }
            }
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            OverlayService.Set(OverlayKind.Flow);
            OverlayService.RedrawNow();
            int nets = BeltNetworkService.Kernel.NetworkCount;
            bool flow = laid == 7 && nets >= 2 && OverlayService.Labels.Count == Math.Min(nets, GridContent.TuningInt("overlay.max_labels"))
                        && OverlayService.Labels.All(l => l.Text.Contains("传送带网络")) && OverlayService.BeltOverlayMode == 1;
            Expect(flow, $"O2 物品流向与吞吐：每个传送带网络一个标签（{OverlayService.Labels.Count} / 网络 {nets}，例“{OverlayService.Labels.FirstOrDefault().Text}”），传送带着色器切到热度 + 箭头模式");
            // 堵塞：停工建筑头顶标记 = 清单里的建筑数，图标按主因（缺电 = 闪电、受损 = 叉）。
            OverlayService.Set(OverlayKind.Blockage);
            OverlayService.RedrawNow();
            IReadOnlyList<DiagReport> reports = RootCauseDiagnosis.Reports;
            int buildings = reports.Count(r => r.Subject == DiagSubject.Building);
            DiagReport gen = reports.FirstOrDefault(r => r.SubjectId == Rec(s, "generator").BuildingId);
            DiagReport asm = reports.FirstOrDefault(r => r.SubjectId == Rec(s, "assembly_station").BuildingId);
            Expect(buildings > 0 && OverlayService.DrawnBadges == Math.Min(buildings, GridContent.TuningInt("overlay.max_markers")) && OverlayService.BeltOverlayMode == 2
                   && gen != null && OverlayService.IconFor(gen.Primary) == ContentIcons.StateDamaged && asm != null && OverlayService.IconFor(asm.Primary) == ContentIcons.StateBrownout
                   && OverlayService.Labels.Count > 0 && OverlayService.Labels.Any(l => l.Tone > 0),
                $"O2 堵塞：{OverlayService.DrawnBadges} 座停工建筑头顶标记（= 清单里的建筑 {buildings} 座），受损的发电机是叉、缺电的装配站是闪电；标签写主因；传送带着色器切到堵塞模式");
            // 施工状态：每处施工一个方框（颜色 + 线宽按状态），标签写状态。
            GridCell? g = FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 10f, 40f);
            if (g.HasValue)
            {
                HomeGridService.TryPlace(s, HomeValleyLayout.BuildingTypeGenerator2, g.Value, 0);
            }
            StepUntil(() => (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Any(o => o != null && o.Kind == WorkOrderKind.Build && o.State == WorkOrderState.Waiting), 30);
            OverlayService.Set(OverlayKind.Construction);
            OverlayService.RedrawNow();
            var queue = new List<HomeValleyConstruction.QueueEntry>();
            HomeValleyConstruction.CollectQueue(s, queue);
            WorkOrderRecord waiting = queue.Select(q => q.Order).FirstOrDefault(o => o != null && o.State == WorkOrderState.Waiting);
            OverlayService.SiteTone(waiting, out Color wc, out float ww, out byte wt);
            Expect(queue.Count > 0 && OverlayService.DrawnMarkers == queue.Count && OverlayService.Labels.Count == queue.Count && OverlayService.BeltOverlayMode == 0
                   && waiting != null && ww > 0.3f && wt == 1 && OverlayService.Labels.Any(l => l.Text.Contains("等待材料")),
                $"O2 施工状态：{OverlayService.DrawnMarkers} 处施工各一个方框（= 施工队列 {queue.Count}），等材料的是粗黄框（线宽 {ww:0.##}），标签写“等待材料”");
            // 突袭路径：行进中的突袭（粗红线，点数 = 剩余路线 + 当前位置）+ 附近据点来路（细橙线）。
            GridCell core = HomeGridService.CorePivot(s);
            s.Raids.InTransit = s.Raids.InTransit.Append(new TransitGroupRecord
            {
                GroupId = "transit-diag", Kind = TransitGroupKind.Raid, UnitCount = 5, PosX = core.X + 120, PosY = core.Y + 40, TargetX = core.X, TargetY = core.Y,
                RouteX = new[] { core.X + 110, core.X + 80, core.X + 40, core.X }, RouteY = new[] { core.Y + 40, core.Y + 30, core.Y + 10, core.Y }, RouteIndex = 1,
            }).ToArray();
            OverlayService.Set(OverlayKind.Raid);
            OverlayService.RedrawNow();
            float radius = GridContent.Tuning("overlay.raid_outpost_radius");
            int near = (s.Raids.Outposts ?? Array.Empty<OutpostRecord>()).Count(o => Vector2.Distance(new Vector2(o.CellX, o.CellY), new Vector2(core.X, core.Y)) <= radius);
            int raids = s.Raids.InTransit.Count(t => t.Kind == TransitGroupKind.Raid);
            Expect(OverlayService.DrawnLines == raids + near && OverlayService.Labels.Any(l => l.Text.Contains("突袭 5 台")),
                $"O2 突袭路径：{raids} 支行进中的突袭各一条路线、{near} 个 {radius:0} 格内的敌方据点各一条来路（共 {OverlayService.DrawnLines} 条线），标签写台数与距离");
            // 流体网络：每个网络一个标签；流体叠加层时传送带压暗（模式 3）。
            OverlayService.Set(OverlayKind.Fluid);
            OverlayService.RedrawNow();
            int pipeNets = PipeNetworkService.IsRunning ? PipeNetworkService.Kernel.NetworkCount : 0;
            Expect(OverlayService.BeltOverlayMode == 3 && OverlayService.Labels.Count == Math.Min(pipeNets, GridContent.TuningInt("overlay.max_labels")),
                $"O2 流体网络：每个流体网络一个标签（{OverlayService.Labels.Count} / {pipeNets}），传送带压暗让位（着色器模式 3）");
            // 污染：地形贴图按污染等级强调（建造叠加层与普通视角都读服务的当前叠加层）。
            OverlayService.Set(OverlayKind.Pollution);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            bool pollutionFlag = false;
            try
            {
                mode.Open();
                mode.UpdateTerrainOverlay(s, core, completeNow: true);
                pollutionFlag = mode.TerrainOverlayForTests != null && mode.TerrainOverlayForTests.PollutionView;
            }
            finally
            {
                mode.Close();
            }
            Expect(pollutionFlag && OverlayService.BeltOverlayMode == 0, "O2 污染：建造叠加层的地形贴图切到“按污染等级强调”（贴图像素见下一条）");
            // 着色器：传送带实例着色器有 _Overlay 参数（改参数、不重传实例）。
            Shader belt = AssetDatabase.LoadAssetAtPath<Shader>("Assets/GameScripts/Main/Sim/Shaders/BeltInstanced.shader");
            Expect(belt != null && belt.FindPropertyIndex("_Overlay") >= 0, "O2 传送带实例着色器有 _Overlay 参数：物品流向 / 堵塞 / 压暗只改材质参数，CPU 开销与格数无关");
            // 每帧：不在刷新点时不重建（60 帧最多重建 1 次）。
            OverlayService.Set(OverlayKind.Construction);
            OverlayService.FrameTick();
            int redraws = OverlayService.RedrawCount;
            for (int i = 0; i < 60; i++)
            {
                OverlayService.FrameTick();
            }
            Expect(OverlayService.RedrawCount - redraws <= 1, $"O2 叠加层开着时每帧只比较版本号：连续 60 帧重建 {OverlayService.RedrawCount - redraws} 次（按 overlay.refresh_seconds 真实秒节拍）");
            // 家园不被观察时不画（同一叠加层，换一个不观察家园的世界）。
            NewWorld(9819, observe: false);
            OverlayService.Set(OverlayKind.Construction);
            OverlayService.FrameTick();
            Expect(!OverlayService.Visible && OverlayService.DrawnMarkers == 0 && OverlayService.Labels.Count == 0, "O2 家园不被观察时叠加层不画（线、标记、标签都收起；模拟照常）");
            OverlayService.Set(OverlayKind.None);
        }

        private static void CheckPollutionPaint()
        {
            const int size = 4;
            const int ppc = 2;
            var terrain = new byte[size * size];
            var pollution = new byte[size * size];
            var explored = Enumerable.Repeat((byte)1, size * size).ToArray();
            var palette = Enumerable.Repeat(new Color32(100, 100, 100, 255), 256).ToArray();
            var patterns = new byte[256];
            pollution[1] = 1;
            pollution[2] = 3;
            Color32[] Paint(bool pollutionView)
            {
                var q = new TilePaintParams { ChunkSize = size, PixelsPerCell = ppc, BlockLevel = 3, TerrainView = true, PollutionView = pollutionView };
                WorldPaintJob job = WorldGenKernel.SchedulePaint(in q, 0, 0, 1, terrain, pollution, explored, palette, patterns);
                job.Complete();
                var tex = new Texture2D(size * ppc, size * ppc, TextureFormat.RGBA32, false);
                job.Upload(tex);
                Color32[] px = tex.GetPixels32();
                job.Release();
                Object.DestroyImmediate(tex);
                return px;
            }
            Color32 At(Color32[] px, int cell) => px[(cell / size) * ppc * size * ppc + (cell % size) * ppc];
            Color32[] on = Paint(true);
            Color32[] off = Paint(false);
            Color32 clean = At(on, 0);
            Color32 heavy = At(on, 2);
            Color32 light = At(on, 1);
            Expect(clean.r < 60 && clean.g < 60 && heavy.r > heavy.g + 100 && light.b > light.g && At(off, 0).r == 100,
                $"O2 污染叠加层贴图（Burst 工作线程）：无污染的格压暗（{clean.r},{clean.g},{clean.b}）、达到拦截等级的格偏红（{heavy.r},{heavy.g},{heavy.b}）、轻污染偏紫（{light.r},{light.g},{light.b}）；" +
                "关掉叠加层恢复普通地貌颜色");
        }

        private static void CheckOverlayHud()
        {
            CampaignState s = NewWorld(9813);
            OverlayService.ResetForTests();
            OverlayService.Bind(s);
            OverlayHudUIToolkit.InWorldOverrideForTests = true;
            GameObject go = null;
            try
            {
                VisualElement root = MountUxml(UiKitFolder + "OverlayHud.uxml", out go);
                OverlayHudUIToolkit hud = go.AddComponent<OverlayHudUIToolkit>();
                hud.BindView(root);
                hud.Tick(null);
                bool dock = hud.DockVisible && hud.ToggleText.Contains(InputDisplay.ForAction(GameActionId.OverlaySelector)) && hud.ActiveText.Contains("关");
                Click(hud.ToggleButton);
                hud.Tick(null);
                bool open = OverlayHudUIToolkit.SelectorOpen && hud.SelectorVisible;
                Click(hud.KindButton(OverlayKind.Blockage));
                hud.Tick(null);
                bool picked = OverlayService.Active == OverlayKind.Blockage && hud.KindButton(OverlayKind.Blockage).text.StartsWith("▸", StringComparison.Ordinal)
                              && hud.KindButton(OverlayKind.Blockage).text.Contains(InputDisplay.ForAction(GameActionId.OverlayBlockage))
                              && hud.LegendText == OverlayService.Legend(OverlayKind.Blockage) && hud.ActiveText.Contains("堵塞");
                Click(hud.KindButton(OverlayKind.None));
                hud.Tick(null);
                bool offed = OverlayService.Active == OverlayKind.None && hud.LegendText.Contains("没有显示");
                Expect(dock && open && picked && offed && !GameText.ContainsMarker(hud.HintText + hud.DiagButtonText),
                    $"O3 叠加层选择器（FGU-12，真 UXML）：停靠条“{hud.ToggleText}”/“{hud.ActiveText}”；点开选择器、点“堵塞”切过去（按钮有“▸”前缀、写直达键）、图例跟着换；“关闭叠加层”后图例写提示");
                Click(hud.DiagButton);
                bool diag = DiagnosisPanelUIToolkit.IsOpen || DiagnosisPanelUIToolkit.Instance == null;
                DiagnosisPanelUIToolkit.Close();
                UiEscapeStack.CloseTop();
                Expect(diag && !OverlayHudUIToolkit.SelectorOpen, "O3 选择器里的“为什么不工作（N）”打开诊断面板；Esc 关闭选择器");
                // 标签层：每条标签投影到屏幕（自检没有镜头时只数条数）。
                OverlayService.Set(OverlayKind.Blockage);
                OverlayService.RedrawNow();
                OverlayService.FrameTick();
                hud.Tick(null);
                Expect(OverlayService.Visible && hud.VisibleLabelCount == OverlayService.Labels.Count && hud.LabelText(0).StartsWith("!", StringComparison.Ordinal),
                    $"O3 世界标签层：{hud.VisibleLabelCount} 条标签（有问题的带“!”前缀，不只靠颜色）");
                OverlayService.Set(OverlayKind.None);
            }
            finally
            {
                OverlayHudUIToolkit.CloseSelector();
                OverlayHudUIToolkit.InWorldOverrideForTests = false;
                if (go != null) Object.DestroyImmediate(go);
                UiEscapeStack.Clear();
            }
            Probe("OverlayHud.uxml", "OverlaySelector", pr =>
            {
                var probeGo = new GameObject("__probe_oh") { hideFlags = HideFlags.HideAndDontSave };
                OverlayHudUIToolkit.InWorldOverrideForTests = true;
                OverlayHudUIToolkit h = probeGo.AddComponent<OverlayHudUIToolkit>();
                h.BindView(pr.panel.visualTree);
                h.SetSelector(true);
                h.Tick(null);
                h.SetSelector(false);
                OverlayHudUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(probeGo);
                pr.panel.visualTree.Q<VisualElement>("OverlayDock")?.RemoveFromClassList("uk-hidden");
                pr.panel.visualTree.Q<VisualElement>("OverlaySelector")?.RemoveFromClassList("uk-hidden");
            });
        }

        private static void CheckDiagnosisPanel()
        {
            CampaignState s = NewWorld(9814);
            HomeValleyPowerGrid.Recompute(s);
            DiagnosisPanelUIToolkit.InWorldOverrideForTests = true;
            GameObject go = null;
            try
            {
                VisualElement root = MountUxml(UiKitFolder + "DiagnosisPanel.uxml", out go);
                DiagnosisPanelUIToolkit panel = go.AddComponent<DiagnosisPanelUIToolkit>();
                panel.BindView(root);
                DiagnosisPanelUIToolkit.Open();
                IReadOnlyList<DiagReport> reports = RootCauseDiagnosis.Reports;
                int steps = reports.Sum(r => r.Chains.Sum(c => c.Steps.Count));
                bool shown = DiagnosisPanelUIToolkit.IsOpen && panel.PanelVisible && panel.TitleText == "为什么不工作" && panel.RowCount == reports.Count && reports.Count > 0
                             && panel.StepButtonCount == steps && panel.SummaryText.Contains(reports.Count + " 处停工") && panel.HintText.Contains(InputDisplay.ForAction(GameActionId.OpenDiagnosis))
                             && GameSettings.HasSeenGuidanceHook(GuidanceHooks.DiagnosisFirstOpen);
                Expect(shown, $"O4 “为什么不工作”面板（真 UXML）：{panel.RowCount} 个停工对象、{panel.StepButtonCount} 个可点的原因条目；汇总“{panel.SummaryText}”；打开发引导钩子（第一次打开在 O1 的 Ctrl+O 已发）"
                              + (shown ? string.Empty : $"［诊断 {reports.Count} / 步骤 {steps} / 可见 {panel.PanelVisible} / 标题 {panel.TitleText} / 提示 {panel.HintText}］"));
                int rootIndex = -1;
                for (int i = 0; i < panel.StepButtonCount; i++)
                {
                    if (panel.StepTarget(i).Code == DiagCode.SupplyDamaged)
                    {
                        rootIndex = i;
                        break;
                    }
                }
                int flies = WorldView.FlyCount;
                int locates = RootCauseDiagnosis.LocateCount;
                DiagStep target = panel.StepTarget(rootIndex);
                Click(panel.StepButton(rootIndex));
                Expect(rootIndex >= 0 && panel.StepButton(rootIndex).text.StartsWith("根源", StringComparison.Ordinal) && RootCauseDiagnosis.LocateCount == locates + 1
                       && WorldView.FlyCount == flies + 1 && RootCauseDiagnosis.LastLocated == target.Position
                       && Mathf.Abs(WorldView.Director.StrategyFocus.x - target.Position.x) < 0.5f && Mathf.Abs(WorldView.Director.StrategyFocus.y - target.Position.y) < 0.5f,
                    $"O4 点原因条目“{panel.StepButton(rootIndex)?.text}”：镜头飞到受损的发电机（{target?.Position}），根源条目带“根源：”前缀");
                int rebuilds = panel.RebuildCount;
                panel.Refresh();
                Expect(panel.RebuildCount == rebuilds, "O4 诊断内容没变时刷新不重建列表");
                // 全部正常：修好全部建筑、电网重算。
                foreach (BuildingRecord b in s.BuildingRecords)
                {
                    if (b.ConstructionState == BuildingConstructionState.Damaged)
                    {
                        b.ConstructionState = BuildingConstructionState.Operational;
                    }
                }
                HomeValleyPowerGrid.Recompute(s);
                WaitSync();
                RootCauseDiagnosis.Refresh(s, force: true);
                panel.Refresh();
                Expect(RootCauseDiagnosis.Reports.Count == 0 && panel.RowCount == 0 && panel.EmptyText.Contains("一切正常"),
                    $"O4 全部修好后清单为空，写“{panel.EmptyText}”");
                UiEscapeStack.CloseTop();
                Expect(!DiagnosisPanelUIToolkit.IsOpen, "O4 Esc 关闭“为什么不工作”");
            }
            finally
            {
                DiagnosisPanelUIToolkit.Close();
                DiagnosisPanelUIToolkit.InWorldOverrideForTests = false;
                if (go != null) Object.DestroyImmediate(go);
                UiEscapeStack.Clear();
            }
            CampaignState s2 = NewWorld(9815);
            HomeValleyPowerGrid.Recompute(s2);
            Probe("DiagnosisPanel.uxml", "DiagnosisWindow", pr =>
            {
                var probeGo = new GameObject("__probe_dg") { hideFlags = HideFlags.HideAndDontSave };
                DiagnosisPanelUIToolkit.InWorldOverrideForTests = true;
                DiagnosisPanelUIToolkit p = probeGo.AddComponent<DiagnosisPanelUIToolkit>();
                p.BindView(pr.panel.visualTree);
                DiagnosisPanelUIToolkit.Open();
                DiagnosisPanelUIToolkit.Close();
                DiagnosisPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(probeGo);
                pr.panel.visualTree.Q<VisualElement>("DiagnosisRoot")?.RemoveFromClassList("uk-hidden");
            });
        }

        /// <summary>
        /// O4b（审查 P1：面板每次重建泄漏提示绑定、实时数字一变就重建）：施工等材料的“库存 N”随库存变——面板开着时只原地改文字，
        /// 按钮不换、提示绑定表不增长；结构变了（修好发电机）只新建形状变了的行，绑定数 = 当前按钮数（旧行已解绑）。
        /// </summary>
        private static void CheckPanelLiveNumbers()
        {
            CampaignState s = NewWorld(9819, scrap: 0);
            HomeValleyPowerGrid.Recompute(s);
            GridCell? spot = FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 10f, 40f);
            GridOpResult placed = spot.HasValue ? HomeGridService.TryPlace(s, HomeValleyLayout.BuildingTypeGenerator2, spot.Value, 0) : default;
            WorkOrderRecord order = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).FirstOrDefault(o => o != null && o.Kind == WorkOrderKind.Build && o.TargetId == placed.BuildingId);
            bool waiting = order != null && StepUntil(() => order.State == WorkOrderState.Waiting, 30);
            DiagnosisPanelUIToolkit.InWorldOverrideForTests = true;
            GameObject go = null;
            try
            {
                VisualElement root = MountUxml(UiKitFolder + "DiagnosisPanel.uxml", out go);
                DiagnosisPanelUIToolkit panel = go.AddComponent<DiagnosisPanelUIToolkit>();
                panel.BindView(root);
                int bindings0 = UiTooltip.BindingCount;
                DiagnosisPanelUIToolkit.Open();
                int bindingsOpen = UiTooltip.BindingCount;
                int rebuilds = panel.RebuildCount;
                int created = panel.RowsCreated;
                int inPlace = panel.InPlaceUpdateCount;
                Button first = panel.StepButton(0);
                int siteStep = -1;
                for (int i = 0; i < panel.StepButtonCount; i++)
                {
                    if (panel.StepTarget(i).Code == DiagCode.SiteMaterials)
                    {
                        siteStep = i;
                        break;
                    }
                }
                Button siteButton = panel.StepButton(siteStep);
                int revChanges = 0;
                bool textFollows = true;
                const int N = 20;
                for (int i = 1; i <= N; i++)
                {
                    s.Scrap = i % 5; // 始终不够（施工单不动），只有“库存 N”在变
                    int rev = RootCauseDiagnosis.Revision;
                    RootCauseDiagnosis.Refresh(s, force: true);
                    if (RootCauseDiagnosis.Revision != rev)
                    {
                        revChanges++;
                    }
                    panel.Refresh();
                    textFollows &= siteButton != null && siteButton.text.Contains("库存 " + (i % 5));
                }
                bool stable = panel.RebuildCount == rebuilds && panel.RowsCreated == created && panel.InPlaceUpdateCount >= inPlace + revChanges
                              && UiTooltip.BindingCount == bindingsOpen && panel.StepButton(0) == first && panel.StepButton(siteStep) == siteButton;
                Expect(placed.Success && waiting && siteStep >= 0 && revChanges >= N - 1 && textFollows && stable && bindingsOpen - bindings0 == panel.StepButtonCount,
                    $"O4b 面板开着、库存变 {N} 次（内容版本变 {revChanges} 次）：只原地改文字（“{siteButton?.text}”），{panel.InPlaceUpdateCount - inPlace} 次原地更新、0 次新建行；" +
                    $"按钮还是原来那些，悬停提示绑定数不增长（{bindingsOpen - bindings0} = 按钮数 {panel.StepButtonCount}）"
                    + (stable ? string.Empty : $"［重建 {panel.RebuildCount - rebuilds} / 新建行 {panel.RowsCreated - created} / 绑定 {bindingsOpen}→{UiTooltip.BindingCount}］"));
                // 结构变化：修好受损的建筑、电网重算——形状变了的行新建，旧行的提示已解绑（绑定数 = 当前按钮数，不累积）。
                foreach (BuildingRecord b in s.BuildingRecords)
                {
                    if (b.ConstructionState == BuildingConstructionState.Damaged)
                    {
                        b.ConstructionState = BuildingConstructionState.Operational;
                    }
                }
                HomeValleyPowerGrid.Recompute(s);
                RootCauseDiagnosis.Refresh(s, force: true);
                panel.Refresh();
                int reportSteps = RootCauseDiagnosis.Reports.Sum(r => r.Chains.Sum(c => c.Steps.Count));
                Expect(panel.StepButtonCount == reportSteps && UiTooltip.BindingCount - bindings0 == panel.StepButtonCount && panel.RowCount == RootCauseDiagnosis.Reports.Count,
                    $"O4b 结构变了（修好发电机）：清单 {panel.RowCount} 行、{panel.StepButtonCount} 个原因条目，提示绑定数 {UiTooltip.BindingCount - bindings0}（= 当前按钮数，旧行已解绑）");
            }
            finally
            {
                DiagnosisPanelUIToolkit.Close();
                DiagnosisPanelUIToolkit.InWorldOverrideForTests = false;
                if (go != null) Object.DestroyImmediate(go);
                UiEscapeStack.Clear();
            }
        }

        /// <summary>O4c（审查 P2：面板整个盖住停靠条与选择器）：左侧停靠位同一时间只放一个——开面板收起选择器，开选择器收起面板；面板页眉下写当前叠加层。</summary>
        private static void CheckDockExclusive()
        {
            CampaignState s = NewWorld(9820);
            HomeValleyPowerGrid.Recompute(s);
            OverlayService.ResetForTests();
            OverlayService.Bind(s);
            OverlayHudUIToolkit.InWorldOverrideForTests = true;
            DiagnosisPanelUIToolkit.InWorldOverrideForTests = true;
            GameObject hudGo = null;
            GameObject panelGo = null;
            try
            {
                VisualElement hudRoot = MountUxml(UiKitFolder + "OverlayHud.uxml", out hudGo);
                OverlayHudUIToolkit hud = hudGo.AddComponent<OverlayHudUIToolkit>();
                hud.BindView(hudRoot);
                VisualElement panelRoot = MountUxml(UiKitFolder + "DiagnosisPanel.uxml", out panelGo);
                DiagnosisPanelUIToolkit panel = panelGo.AddComponent<DiagnosisPanelUIToolkit>();
                panel.BindView(panelRoot);
                OverlayService.Set(OverlayKind.Blockage);
                hud.Tick(null);
                Click(hud.ToggleButton);
                bool selector = OverlayHudUIToolkit.SelectorOpen;
                Click(hud.DiagButton); // 选择器里的“为什么不工作（N）”
                bool swapped = DiagnosisPanelUIToolkit.IsOpen && !OverlayHudUIToolkit.SelectorOpen && !hud.SelectorVisible
                               && panel.OverlayText.Contains(OverlayService.Name(OverlayKind.Blockage)) && panel.OverlayText.Contains(InputDisplay.ForAction(GameActionId.ToggleOverlay));
                OverlayService.Set(OverlayKind.Flow);
                panel.Refresh();
                bool follows = panel.OverlayText.Contains(OverlayService.Name(OverlayKind.Flow));
                OverlayHudUIToolkit.ToggleSelector();
                bool back = OverlayHudUIToolkit.SelectorOpen && !DiagnosisPanelUIToolkit.IsOpen;
                Expect(selector && swapped && follows && back,
                    $"O4c 左侧停靠位同一时间只放一个：选择器里点“为什么不工作”→ 面板打开、选择器收起，面板页眉下写当前叠加层（“{panel.OverlayText}”，切换后跟着变）；再按选择器键 → 面板收起、选择器打开");
            }
            finally
            {
                DiagnosisPanelUIToolkit.Close();
                OverlayHudUIToolkit.CloseSelector();
                DiagnosisPanelUIToolkit.InWorldOverrideForTests = false;
                OverlayHudUIToolkit.InWorldOverrideForTests = false;
                if (hudGo != null) Object.DestroyImmediate(hudGo);
                if (panelGo != null) Object.DestroyImmediate(panelGo);
                OverlayService.Set(OverlayKind.None);
                UiEscapeStack.Clear();
            }
        }

        /// <summary>分帧诊断（审查 P1：整份诊断不在一帧里做完）：分帧推进一整轮的结果与同步整份逐字一致；一轮用了多帧；同一帧里多处调用只推进一次。</summary>
        private static void CheckSlicedMatchesSync()
        {
            CampaignState s = NewWorld(9823, scrap: 200);
            HomeValleyPowerGrid.Recompute(s);
            BuildingRecord wh = ActivateWarehouse(s);
            BeltPortService.Binding outP = Port(wh, "warehouse.out0");
            var dir = (BeltDir)outP.Face;
            for (int i = 0; i < 5; i++)
            {
                BeltNetworkService.TryPlace(s, new GridCell(outP.BeltCell.X + BeltDirs.Dx((int)dir) * i, outP.BeltCell.Y + BeltDirs.Dy((int)dir) * i), dir, 0);
            }
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            List<DiagReport> sync = CollectNow(s);
            string expect = RootCauseDiagnosis.Fingerprint(sync) + "|" + string.Join("|", sync.SelectMany(r => r.Chains).SelectMany(c => c.Steps).Select(x => x.Text));
            RootCauseDiagnosis.ResetForTests();
            int frames = 0;
            int published = RootCauseDiagnosis.RefreshCount;
            while (frames < 400 && !RootCauseDiagnosis.StepForTests(s))
            {
                frames++;
            }
            frames++;
            List<DiagReport> sliced = RootCauseDiagnosis.Reports.ToList();
            string got = RootCauseDiagnosis.Fingerprint(sliced) + "|" + string.Join("|", sliced.SelectMany(r => r.Chains).SelectMany(c => c.Steps).Select(x => x.Text));
            // 刚发布完、还没到下一轮的间隔：同一帧 / 下一帧的调用不重开一轮。
            bool idle = !RootCauseDiagnosis.StepForTests(s) && RootCauseDiagnosis.RefreshCount == published + 1;
            int buildings = s.BuildingRecords.Length;
            int slice = GridContent.TuningInt("diag.slice_buildings");
            Expect(sync.Count > 0 && got == expect && frames == RootCauseDiagnosis.LastPassFrames && frames >= 4 && idle,
                $"P0 分帧诊断：{buildings} 座建筑、每帧最多 {slice} 座，一整轮 {frames} 帧（建表 + 建筑 + 施工 + 传送带 + 流体各分帧）后才发布；结果与同步整份逐字一致（{sync.Count} 处停工）；" +
                "发布后到下一轮间隔前不重算" + (got == expect ? string.Empty : $"\n同步：{expect}\n分帧：{got}"));
            // 一轮进行中换了战役：丢掉旧的一轮，旧战役的结果不再显示。
            CampaignState other = NewWorld(9824);
            RootCauseDiagnosis.ResetForTests();
            RootCauseDiagnosis.Refresh(s, force: true);
            int before = RootCauseDiagnosis.Reports.Count;
            RootCauseDiagnosis.StepForTests(other);
            Expect(before > 0 && RootCauseDiagnosis.Reports.Count == 0,
                "P0 换了战役：旧战役的清单立刻清掉，新的一轮做完之前不显示别的存档的停工");
        }

        private static void CheckHover()
        {
            CampaignState s = NewWorld(9816);
            HomeValleyPowerGrid.Recompute(s);
            BuildingRecord asm = Rec(s, "assembly_station");
            bool ok = RootCauseDiagnosis.TryDescribeForHover(s, asm, out string text);
            Expect(ok && text.StartsWith("为什么不工作", StringComparison.Ordinal) && text.Contains("电网 1 超载") && text.Contains("→") && text.Contains(InputDisplay.ForAction(GameActionId.OpenDiagnosis))
                   && !GameText.ContainsMarker(text),
                $"O5 悬停建筑（FGR-LOG-081“建筑：状态和原因”）：写出原因链与“为什么不工作”快捷键：“{text?.Replace("\n", " / ")}”");
        }

        private static void CheckLocateKeepsBuildMode()
        {
            CampaignState s = NewWorld(9817);
            HomeValleyPowerGrid.Recompute(s);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            BuildingRecord gen = Rec(s, "generator");
            InputRouter.DebugSetReader(new KeyReader()); // 光标在窗口外：不触发屏幕边缘推屏
            InputRouter.SetScope(InputScope.Strategy);
            try
            {
                for (int i = 0; i < 20; i++)
                {
                    WorldSimulation.Frame(0.05f);
                }
                mode.Open();
                mode.Select("power_pole");
                WorldSimulation.Frame(0.05f);
                int kept = mode.FlightsKept;
                DiagChain p = ChainOf(RootCauseDiagnosis.DiagnoseBuilding(s, Rec(s, "assembly_station")), DiagCategory.Power);
                bool flew = RootCauseDiagnosis.Locate(p.Root);
                bool inTransition = WorldView.Director.InTransition;
                for (int i = 0; i < 30; i++)
                {
                    WorldSimulation.Frame(0.05f);
                }
                Unity.Mathematics.float2 f = WorldView.Director.StrategyFocus;
                Expect(flew && inTransition && mode.IsOpen && mode.FlightsKept > kept && mode.SelectedTypeId == "power_pole"
                       && WorldView.Director.Mode == ViewMode.Strategy && Mathf.Abs(f.x - gen.Position.x) < 0.5f && Mathf.Abs(f.y - gen.Position.y) < 0.5f,
                    $"O6 建造模式里点原因“{p?.Root?.Text}”：镜头飞行过渡到发电机（{gen.Position}），落地后建造模式仍开着、选中的电塔还在（FG-GAP-071：过渡中 {mode.FlightsKept - kept} 帧保持开着、不处理建造输入）"
                    + $"［开着 {mode.IsOpen} / 选中 {mode.SelectedTypeId} / 视角 {WorldView.Director.Mode} / 焦点 ({f.x:0.##}, {f.y:0.##}) / 过渡 {inTransition}］");
            }
            finally
            {
                mode?.Close();
            }
        }

        // ── S 存读档与时间 ─────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(9818);
            OverlayService.ResetForTests();
            OverlayService.Bind(s);
            OverlayService.Set(OverlayKind.Construction);
            OverlayService.Set(OverlayKind.Blockage);
            WorldSimulation.SyncAllForSave();
            // 存档前的诊断（原因链的种类与文字）：读档后按读回的状态重算，应当逐字一致（诊断只读派生，不进存档）。
            List<DiagReport> beforeList = CollectNow(s);
            string beforeSave = RootCauseDiagnosis.Fingerprint(beforeList) + "|" + string.Join("|", beforeList.SelectMany(r => r.Chains).SelectMany(c => c.Steps).Select(x => x.Text));
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            OverlayService.Set(OverlayKind.None);
            OverlayService.ResetForTests();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            CampaignSession.Set(Slot, rr.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            OverlayService.FrameTick();
            bool restored = save.Success && rr.Success && OverlayService.Active == OverlayKind.Blockage && OverlayService.Last == OverlayKind.Blockage
                            && rr.State.Grid.OverlayActive == (int)OverlayKind.Blockage;
            Expect(restored, "S1 当前叠加层跟着存档走（和快捷栏一样）：真文件存档后读档，仍是“堵塞”，O 键重开的也是它");
            // 读档后诊断与存档前一致（诊断只读派生，不进存档）：比较的是存档前与读档后两份，不是同一份算两次。
            List<DiagReport> afterList = CollectNow(rr.State);
            string afterLoad = RootCauseDiagnosis.Fingerprint(afterList) + "|" + string.Join("|", afterList.SelectMany(r => r.Chains).SelectMany(c => c.Steps).Select(x => x.Text));
            Expect(beforeList.Count > 0 && afterLoad == beforeSave,
                $"S1 诊断不进存档：存档前 {beforeList.Count} 处停工，真文件读档后按读回的状态重算 {afterList.Count} 处，原因链与文字逐字一致"
                + (afterLoad == beforeSave ? string.Empty : $"\n存档前：{beforeSave}\n读档后：{afterLoad}"));
            // 旧存档（本 Story 之前）没有这两个字段 = 0：读档后叠加层关着，O 键打开信号覆盖。
            rr.State.Grid.OverlayActive = 0;
            rr.State.Grid.OverlayLast = 0;
            OverlayService.ResetForTests();
            OverlayService.Bind(rr.State);
            bool legacy = OverlayService.Active == OverlayKind.None && OverlayService.Last == OverlayKind.Signal;
            OverlayService.ToggleCurrent();
            Expect(legacy && OverlayService.Active == OverlayKind.Signal, "S1 旧存档没有叠加层字段：读档后不显示叠加层，O 键打开“信号覆盖”（与 FG1-SIG-07 一致）");
            OverlayService.Set(OverlayKind.None);
        }

        /// <summary>同一场景（仓库 → 传送带到头 + 缺电）跑到同一游戏时刻的诊断指纹。</summary>
        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe, scrap: 200);
            pausedHeld = true;
            BuildingRecord wh = ActivateWarehouse(s);
            BeltPortService.Binding outP = Port(wh, "warehouse.out0");
            var dir = (BeltDir)outP.Face;
            for (int i = 0; i < 5; i++)
            {
                BeltNetworkService.TryPlace(s, new GridCell(outP.BeltCell.X + BeltDirs.Dx((int)dir) * i, outP.BeltCell.Y + BeltDirs.Dy((int)dir) * i), dir, 0);
            }
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = RootCauseDiagnosis.Fingerprint(CollectNow(s)) + BeltNetworkService.Kernel.ComputeStateHash();
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = RootCauseDiagnosis.Fingerprint(CollectNow(s)) + BeltNetworkService.Kernel.ComputeStateHash() == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 60;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 400)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            List<DiagReport> list = CollectNow(s);
            return RootCauseDiagnosis.Fingerprint(list) + "|" + string.Join("|", list.SelectMany(r => r.Chains).SelectMany(c => c.Steps).Select(x => x.Text));
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(9821, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                same &= snap == reference;
            }
            Expect(paused && same && reference.Contains(((int)DiagCode.OutputBlocked).ToString()),
                "S2 暂停中（120 帧）诊断与传送带不变；0.5x / 1x / 2x / 3x 跑同样的 60 游戏秒，诊断的原因链与文字逐字一致（堵塞追溯不依赖帧率或倍速）");
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(9822, true, 1f, false, out _);
            string unseen = RunScenario(9822, false, 1f, false, out _);
            Expect(seen == unseen && seen.Length > 0, "S3 同一场景观察与不观察家园时跑 60 游戏秒，诊断结果逐字一致（FGR-BASE-021：诊断只读状态，不读表现）"
                                                      + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── H 钩子与图鉴 ───────────────────────────────────────────────────────

        private static void CheckHooksAndCodex()
        {
            bool known = GuidanceHooks.Known.Contains(GuidanceHooks.OverlayFirstOpen) && GuidanceHooks.Known.Contains(GuidanceHooks.DiagnosisFirstOpen)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.LogisticsFirstOutputBlocked) && GuidanceHooks.Known.Contains(GuidanceHooks.LogisticsFirstBlocked);
            TbCodexEntry table = ConfigSystem.Instance.Tables.TbCodexEntry;
            CodexEntry entry = table.DataList.FirstOrDefault(e => e.Id == "codex.logistics.diagnosis");
            bool codex = entry != null && entry.Hooks.Contains("logistics.overlay.first_open") && entry.Hooks.Contains("logistics.diagnosis.first_open")
                         && entry.Hooks.Contains("logistics.port.first_output_blocked");
            Expect(known && codex, "H 引导钩子登记在 GuidanceHooks.Known（第一次打开叠加层 / 第一次打开“为什么不工作” / 建筑输出第一次堵住；传送带第一次堵塞沿用 FG0-ARCH-02 的钩子）；" +
                                   "图鉴“叠加层与‘为什么不工作’”由这三个钩子解锁（FG00 B14）");
        }

        // ── P 性能 ─────────────────────────────────────────────────────────────

        private const double HotfixInterpretFactor = 5.0;

        private static void CheckPerformance()
        {
            // K2：15,000 格传送带（150 条 × 100 格，全部塞满堵死）——源头扫描、锚点、100 格追溯。
            using (var k = new BeltKernel(BeltNetworkService.ReadConfig()))
            {
                for (int line = 0; line < 150; line++)
                {
                    for (int x = 0; x < 100; x++)
                    {
                        k.AddCell(x, line * 2, BeltDir.East, 2);
                    }
                    k.AddSource(10 + line, 0, line * 2, 1, 1, -1);
                }
                k.StepMany(3000);
                var roots = new List<BeltCellInfo>();
                var anchors = new List<Unity.Mathematics.int3>();
                k.CollectBlockRoots(roots, 64);
                k.CollectNetworkAnchors(anchors);
                k.TryTraceBlock(0, 0, out _);
                var sw = Stopwatch.StartNew();
                const int N = 20;
                for (int i = 0; i < N; i++)
                {
                    k.CollectBlockRoots(roots, 64);
                }
                double rootsMs = sw.Elapsed.TotalMilliseconds / N;
                sw.Restart();
                for (int i = 0; i < N; i++)
                {
                    k.CollectNetworkAnchors(anchors);
                }
                double anchorMs = sw.Elapsed.TotalMilliseconds / N;
                sw.Restart();
                for (int i = 0; i < N; i++)
                {
                    k.TryTraceBlock(0, i * 2, out _);
                }
                double traceMs = sw.Elapsed.TotalMilliseconds / N;
                PerfLines.Add($"K2 15,000 格堵满：源头扫描 {rootsMs:F3} ms、网络锚点 {anchorMs:F3} ms、单条 100 格追溯 {traceMs:F4} ms（AOT 内核，Editor Mono JIT；只在诊断 / 叠加层刷新时调用，每 0.5 真实秒一次）");
                ExpectPerf(k.CellCount == 15000 && anchors.Count == 150,
                    $"K2 性能：15,000 格满载传送带源头扫描 {rootsMs:F3} ms、锚点 {anchorMs:F3} ms（阈值 4 ms = 120 帧半帧；按 0.5 秒节拍摊到每帧 < 0.1 ms）、100 格追溯 {traceMs:F4} ms",
                    PerfGate.Lt(rootsMs, 4.0, "源头扫描 ms"), PerfGate.Lt(anchorMs, 4.0, "锚点 ms"), PerfGate.Lt(traceMs, 0.5, "100 格追溯 ms"));
            }
            // P1：800 座建筑的家园（324 座电塔 + 476 座用电 / 发电建筑，混合有电 / 缺电 / 未接入）——整份诊断、堵塞叠加层重建。
            CampaignState s = NewWorld(9831, scrap: 100);
            GridCell core = HomeGridService.CorePivot(s);
            var list = new List<BuildingRecord>(s.BuildingRecords);
            var rng = new System.Random(77);
            int side = 18;
            for (int i = 0; i < side * side; i++)
            {
                list.Add(Fixture("power_pole", "perf_pole_" + i, new GridCell(core.X + 60 + (i % side) * 10, core.Y + (i / side) * 10)));
            }
            string[] kinds = { HomeValleyLayout.BuildingTypeGenerator2, HomeValleyLayout.BuildingTypeRepairBay, HomeValleyLayout.BuildingTypeAnalysisBench, HomeValleyLayout.BuildingTypeAssemblyStation };
            int others = 800 - list.Count;
            for (int i = 0; i < others; i++)
            {
                BuildingRecord b = Fixture(kinds[i % kinds.Length], "perf_b_" + i, new GridCell(core.X + 62 + rng.Next(0, side * 10), core.Y + 2 + rng.Next(0, side * 10)));
                if (i % 9 == 0)
                {
                    b.ConstructionState = BuildingConstructionState.Damaged;
                }
                list.Add(b);
            }
            s.BuildingRecords = list.ToArray();
            HomeValleyPowerGrid.Recompute(s);
            var reports = new List<DiagReport>();
            RootCauseDiagnosis.Collect(s, reports); // 预热
            var watch = Stopwatch.StartNew();
            const int R = 10;
            for (int i = 0; i < R; i++)
            {
                RootCauseDiagnosis.Collect(s, reports);
            }
            double collectMs = watch.Elapsed.TotalMilliseconds / R;
            // 分帧诊断（界面实际走的路径）：一整轮里每一帧的耗时——任何一帧都与建筑总数无关（每帧最多 diag.slice_buildings 座）。
            RootCauseDiagnosis.ResetForTests();
            for (int warm = 0; warm < 2; warm++)
            {
                RootCauseDiagnosis.Invalidate();
                while (!RootCauseDiagnosis.StepForTests(s))
                {
                }
            }
            double maxSlice = 0;
            double sumSlice = 0;
            int sliceFrames = 0;
            const int Passes = 5;
            for (int pass = 0; pass < Passes; pass++)
            {
                RootCauseDiagnosis.Invalidate();
                bool done = false;
                while (!done)
                {
                    done = RootCauseDiagnosis.StepForTests(s);
                    maxSlice = Math.Max(maxSlice, RootCauseDiagnosis.LastSliceMs);
                    sumSlice += RootCauseDiagnosis.LastSliceMs;
                    sliceFrames++;
                }
            }
            int passFrames = RootCauseDiagnosis.LastPassFrames;
            double avgSlice = sumSlice / sliceFrames;
            bool sameAsSync = RootCauseDiagnosis.Fingerprint(RootCauseDiagnosis.Reports.ToList()) == RootCauseDiagnosis.Fingerprint(reports);
            OverlayService.ResetForTests();
            OverlayService.Bind(s);
            OverlayService.Set(OverlayKind.Blockage);
            OverlayService.RedrawNow();
            watch.Restart();
            for (int i = 0; i < R; i++)
            {
                OverlayService.RedrawNow();
            }
            double redrawMs = watch.Elapsed.TotalMilliseconds / R;
            OverlayService.FrameTick();
            watch.Restart();
            for (int i = 0; i < 600; i++)
            {
                OverlayService.FrameTick();
            }
            double frameUs = watch.Elapsed.TotalMilliseconds * 1000.0 / 600;
            OverlayService.Set(OverlayKind.None);
            double device = maxSlice * HotfixInterpretFactor;
            PerfLines.Add($"P1 800 座建筑（{reports.Count} 处停工）：分帧诊断一整轮 {passFrames} 帧，每帧平均 {avgSlice:F3} ms、最大 {maxSlice:F3} ms（真机解释执行 ×{HotfixInterpretFactor} 折算最大 {device:F2} ms / 帧）；" +
                          $"同步整份（只在打开面板且手上没有新结果时做一次）{collectMs:F3} ms；堵塞叠加层重建（同步整份诊断 + 标记）{redrawMs:F3} ms；不在刷新点的每帧 {frameUs:F2} µs");
            ExpectPerf(s.BuildingRecords.Length >= 800 && reports.Count > 50 && sameAsSync && passFrames >= 800 / GridContent.TuningInt("diag.slice_buildings"),
                $"P1 性能：800 座建筑的家园——分帧诊断任何一帧 ≤ {maxSlice:F3} ms（阈值 1 ms，Editor；平均 {avgSlice:F3} ms，阈值 0.4 ms；真机折算最大 {device:F2} ms / 帧），" +
                $"一整轮 {passFrames} 帧、结果与同步整份一致；叠加层开着时平常每帧 {frameUs:F2} µs（阈值 50 µs，与建筑数无关）",
                PerfGate.Lt(maxSlice, 1.0, "分帧诊断最大一帧 ms"), PerfGate.Lt(avgSlice, 0.4, "分帧诊断平均 ms"), PerfGate.Lt(frameUs, 50.0, "叠加层平常每帧 µs"));

            // P2：“为什么不工作”面板 200 个停工对象（diag.max_reports 上限）：打开时建全部行（一次性）、实时数字变时原地改文字、多一处停工时只新建形状变了的行。
            DiagnosisPanelUIToolkit.InWorldOverrideForTests = true;
            GameObject go = null;
            try
            {
                VisualElement root = MountUxml(UiKitFolder + "DiagnosisPanel.uxml", out go);
                DiagnosisPanelUIToolkit panel = go.AddComponent<DiagnosisPanelUIToolkit>();
                panel.BindView(root);
                RootCauseDiagnosis.ResetForTests();
                DiagnosisPanelUIToolkit.Open();
                double openMs = panel.LastRefreshMs;
                int rows = panel.RowCount;
                int buttons = panel.StepButtonCount;
                int baseBindings = UiTooltip.BindingCount - buttons; // 面板以外的提示绑定
                // 最坏的原地更新：换语言，全部行的文字重写（不新建元素）。
                int created = panel.RowsCreated;
                GameSettings.SetLanguage(GameLanguage.En);
                panel.Refresh();
                double relabelMs = panel.LastRefreshMs;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                panel.Refresh();
                bool noNewRows = panel.RowsCreated == created;
                // 一处新的停工插在清单前面（受损排在最前）：后面的行整体后移一位——形状相同的行原地复用，只新建形状变了的。
                BuildingRecord extra = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeRepairBay && b.ConstructionState == BuildingConstructionState.Operational);
                extra.ConstructionState = BuildingConstructionState.Damaged;
                HomeValleyPowerGrid.Recompute(s);
                RootCauseDiagnosis.Refresh(s, force: true);
                int createdBefore = panel.RowsCreated;
                panel.Refresh();
                double shiftMs = panel.LastRefreshMs;
                int shiftCreated = panel.RowsCreated - createdBefore;
                extra.ConstructionState = BuildingConstructionState.Operational;
                HomeValleyPowerGrid.Recompute(s);
                PerfLines.Add($"P2 面板 {rows} 行 / {buttons} 个原因条目：打开时建全部行 {openMs:F2} ms（一次性）；最坏的原地更新（换语言，全部重写文字、不新建元素）{relabelMs:F2} ms；" +
                              $"清单前面多一处停工（后面全部后移）{shiftMs:F2} ms、新建 {shiftCreated} 行（其余原地复用）；平常只有个别实时数字变时只格式化变了的那几步");
                ExpectPerf(rows == reports.Count && rows > 50 && noNewRows && UiTooltip.BindingCount - baseBindings == panel.StepButtonCount && shiftCreated < rows,
                    $"P2 面板 {rows} 行：打开 {openMs:F2} ms（阈值 60 ms，一次性）、全部重写文字 {relabelMs:F2} ms（阈值 30 ms，只在换语言 / 键位时）、" +
                    $"前面插入一处 {shiftMs:F2} ms 新建 {shiftCreated} 行（其余复用）；提示绑定数 = 当前按钮数 {panel.StepButtonCount}（旧行已解绑，不累积）"
                    + $"［绑定 {UiTooltip.BindingCount - baseBindings}］",
                    PerfGate.Lt(openMs, 60.0, "打开面板 ms"), PerfGate.Lt(relabelMs, 30.0, "全部重写文字 ms"), PerfGate.Lt(shiftMs, 30.0, "前面插入一处 ms"));
            }
            finally
            {
                DiagnosisPanelUIToolkit.Close();
                DiagnosisPanelUIToolkit.InWorldOverrideForTests = false;
                if (go != null) Object.DestroyImmediate(go);
                UiEscapeStack.Clear();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
        }

        private static BuildingRecord Fixture(string typeId, string key, GridCell pivot)
        {
            BuildingGrid g = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            return new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":diag_" + key,
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = pivot.X,
                GridY = pivot.Y,
                Position = GridMath.FootprintCenter(pivot, g.FootprintW, g.FootprintH, 0),
                Health = 100f,
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
        }

        // ── 通用 ─────────────────────────────────────────────────────────────────

        private static void Probe(string uxml, string rootName, Action<VisualElement> prepare)
        {
            try
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") })
                    {
                        string result = UiToolkitLayoutProbe.Probe(UiKitFolder + uxml, rootName, stressFill: true, prepare: prepare, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"布局探针 {uxml}#{rootName} [{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                    }
                }
            }
            finally
            {
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                DiagnosisPanelUIToolkit.Close();
                OverlayHudUIToolkit.CloseSelector();
            }
        }

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgDiagnosisSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
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
                InputRouter.DebugSetReader(null);
                StrategyClock.Reset();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                OverlayService.Set(OverlayKind.None);
                PowerCoverageOverlayView.SetEnabled(false);
                SignalCoverageOverlayView.SetEnabled(false);
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
