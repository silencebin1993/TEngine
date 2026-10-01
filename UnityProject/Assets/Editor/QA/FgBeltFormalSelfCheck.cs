using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Logistics;
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
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG3-LOG-03 传送带正式化与端口的自动验收（FG03 FGR-LOG-020、021、024～028、081、090；FGT-LOG-005、006）。
    /// 全部断言走真实入口：真实载入的家园（<see cref="WorldSimulation.LoadHome"/>：建筑记录、传送带内核、端口对账与仓库转移都在世界步里跑）、
    /// <see cref="HomeValleyBuildMode"/> 的按键与鼠标路径（FakeReader）、真 UXML 的端口面板 / 施工队列、真存档文件。
    /// A 数据：新调参、文本键中英齐全、清带动作、引导钩子、图鉴条目、T2 / T3 菜单行、端口表物流角色。
    /// B 端口绑定生命周期：运转的建筑自动登记端口（核心 / 仓库 = 存量口，其余 = 还不收发）；仓库修好才接上；转向后端口跟着走；端口号不与手工端口冲突。
    /// C 仓库 → 传送带 → 仓库（FGR-LOG-021 正向）：库存真的推上带、送回仓库；全程“库存 + 端口待推 + 端口缓存 + 带上”守恒；悬停写起点 / 终点。
    /// D 三级吞吐（FGT-LOG-005 正式版）：同一条带经真实端口按 T1 / T2 / T3 各跑 60 游戏秒，收下的件数 = 60 / 120 / 240。
    /// E 建造菜单三级 + 原地反转（FGR-LOG-020）：从菜单选 T3、拖拽放虚影、机器建成后是 T3；指着带按旋转键原地反转，物品不丢。
    /// F 堵塞原因（FGR-LOG-025）：仓库满（写库存与容量）、输入口不收（写建筑与物品）、建筑还不收发物品、朝向不对不接、输出口不往指回建筑的带上推。
    /// G 输出过滤（FGR-LOG-021）：停止输出退回待推、带放空、恢复；只能选可存物品；过滤随绑定保存。
    /// H 建筑停用 / 转向：端口删掉、手里的物品退回仓库（守恒）；修好后重新接上继续流。
    /// I 清带工具（FGR-LOG-026，正式输入）：点一格清整条带、拖框只清框里的；仓库放不下时先确认，取消不变、确认丢弃；环；空选区与空带的提示；右键退出。
    /// J 损毁留虚影（FGR-LOG-027）：掉耐久（悬停、存档）；摧毁时物品落地（废料生成搬运单）、原位置留保留设置的虚影；施工队列重建（机器真去建成）/ 移除 / 全部重建。
    /// K 天气（FGR-LOG-028）：沙暴露天减速 25%（三档 45 / 90 / 180 件每分钟实测），顶棚下不受影响，恢复后回到设计速度；悬停写明。
    /// L 存读档：端口绑定与过滤、端口待推 / 缓存、耐久、被摧毁的虚影真文件往返；读档后接着跑与不存档一直跑逐位一致；旧格式 1 的端口块照常读。
    /// M 暂停与 0.5x～3x：暂停不流动；四档跑同样的游戏时间结果逐位一致。
    /// N 观察 / 不观察一致（FGR-BASE-021）。
    /// O FGT-LOG-006：仓库闭环 10 个游戏日（中途关 / 开输出过滤），每 1 万步核对守恒。
    /// P 正式界面：建造模式点仓库打开端口面板（真 UXML：行、过滤下拉框选中即生效、Esc）、施工队列里重建被摧毁的传送带、战略视角悬停、布局探针。
    /// Q 性能（120 帧标准）：15,000 格 / ≥ 30,000 件满载 + 真实建筑端口，按 120 帧 1x / 3x 的节奏逐帧测物流部分的帧耗时（Editor batchmode，无 GPU；GPU 画面帧时间见 FgBeltPerfProbe）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgBeltFormalSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 5;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 传送带正式化与端口")]
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
            Line("\n[传送带正式化与端口] 三级传送带、建筑与仓库端口、输出过滤、堵塞原因、清带、损毁留虚影、天气减速（FG3-LOG-03）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgbeltformal-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程），" +
                     $"Burst={(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；编辑模式下直接起真实家园，没有渲染帧——性能数字是 Editor 托管代码 + Burst 内核，真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckPortBindingLifecycle);
                Step(CheckWarehouseLoop);
                Step(CheckTierThroughput);
                Step(CheckBuildMenuTiersAndReverse);
                Step(CheckBlockReasons);
                Step(CheckOutputFilter);
                Step(CheckBuildingInactiveReturns);
                Step(CheckClearTool);
                Step(CheckDamageAndGhost);
                Step(CheckWeather);
                Step(CheckSaveLoad);
                Step(CheckLegacyPortFormat);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckConservationTenDays);
                Step(CheckFormalUi);
                Step(CheckPerformance120);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"传送带正式化自检抛异常：{e}");
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
                HomeValleyWorkOrders.ResetSessionState();
                ConstructionQueuePanelUIToolkit.InWorldOverrideForTests = false;
                BeltPortPanelUIToolkit.InWorldOverrideForTests = false;
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
            Line($"  · [传送带正式化与端口] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 公共准备 ─────────────────────────────────────────────────────────────

        /// <summary>真实载入家园（传送带内核、端口对账、机器都在）。</summary>
        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 100)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            CampaignState s = CampaignState.CreateNew("fgbeltformal-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            s.Scrap = scrap;
            return s;
        }

        private static BuildingRecord Building(CampaignState s, string typeId) =>
            (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).FirstOrDefault(b => b != null && b.BuildingTypeId == typeId && b.RegionId == HomeValleyLayout.RegionId);

        /// <summary>对账间隔（世界步）+ 1：保证下一次对账已经发生。</summary>
        private static void WaitSync() => WorldSimulation.StepMany(BeltPortService.SyncTicks(GameClock.StepHz) + 1);

        /// <summary>把开局受损的仓库改成运转（测试准备：维修流程本身由 ER3 / FG3-LOG-02 的自检验证），等一次对账。</summary>
        private static BuildingRecord ActivateWarehouse(CampaignState s)
        {
            BuildingRecord wh = Building(s, "warehouse");
            wh.ConstructionState = BuildingConstructionState.Operational;
            WaitSync();
            return wh;
        }

        private static BeltPortService.Binding Port(CampaignState s, string typeId, string portKey)
        {
            BuildingRecord b = Building(s, typeId);
            return b != null ? BeltPortService.Find(b.BuildingId, portKey) : null;
        }

        private static BeltPortInfo PortInfo(BeltPortService.Binding b)
        {
            BeltPortInfo info = default;
            if (b != null)
            {
                BeltNetworkService.Kernel.TryGetPortInfo(b.PortId, out info);
            }
            return info;
        }

        private static BeltDir DirTo(GridCell a, GridCell b) =>
            b.X > a.X ? BeltDir.East : b.X < a.X ? BeltDir.West : b.Y > a.Y ? BeltDir.North : BeltDir.South;

        /// <summary>
        /// 从 <paramref name="from"/> 到 <paramref name="to"/> 的传送带路线（广度优先，只走格网规则允许铺带的格，按 N/E/S/W 固定顺序展开，同一地图同一路线）。
        /// 每格朝向指向下一格，最后一格朝 <paramref name="lastDir"/>。找不到返回 null。
        /// </summary>
        internal static List<(GridCell cell, BeltDir dir)> Route(CampaignState s, GridCell from, GridCell to, BeltDir lastDir, int margin = 14)
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

        /// <summary>经内核入口 <see cref="BeltNetworkService.TryPlace"/> 铺路线（含格网校验；传送带虚影施工由 FG3-LOG-02 验证，这里是测试捷径）。</summary>
        internal static bool Lay(CampaignState s, List<(GridCell cell, BeltDir dir)> path, int tier, out string failure)
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

        /// <summary>仓库输出口 → 路线 → 仓库输入口 的闭环（绕着仓库走），返回路线。</summary>
        internal static List<(GridCell cell, BeltDir dir)> WarehouseLoop(CampaignState s, int tier, out string failure)
        {
            BeltPortService.Binding outP = Port(s, "warehouse", "warehouse.out0");
            BeltPortService.Binding inP = Port(s, "warehouse", "warehouse.in0");
            if (outP == null || inP == null)
            {
                failure = "仓库端口没有登记";
                return null;
            }
            BeltDir intoIn = (BeltDir)(((int)inP.Face + 2) & 3);
            List<(GridCell cell, BeltDir dir)> path = Route(s, outP.BeltCell, inP.BeltCell, intoIn);
            Lay(s, path, tier, out failure);
            return failure == null ? path : null;
        }

        /// <summary>家园物流守恒量：库存 + 各建筑端口待推 + 缓存 + 带上物品（逐格重数）+ 地上的废料。</summary>
        private static long Conserved(CampaignState s)
        {
            long ports = 0;
            foreach (BeltPortService.Binding b in BeltPortService.All)
            {
                if (BeltNetworkService.Kernel.TryGetPortCounts(b.PortId, out int pending, out int buffered))
                {
                    ports += Math.Max(0, pending) + Math.Max(0, buffered);
                }
            }
            return s.Scrap + ports + BeltNetworkService.Kernel.CountItemsSlow() + GroundScrap(s);
        }

        private static int GroundScrap(CampaignState s) =>
            (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => g.Amount);

        private static int NotificationCount(string typeId) => NotificationCenter.History.Count(e => e.Type != null && e.Type.Id == typeId);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds, Action perQuarter = null)
        {
            for (int i = 0; i < maxGameSeconds * 4; i++)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(Math.Max(1, GameClock.StepHz / 4));
                perQuarter?.Invoke();
            }
            return done();
        }

        // ── A. 数据 ──────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            bool tuning = GridContent.TuningInt("logistics.item.scrap_id") == 1 && GridContent.TuningInt("logistics.port.sink_buffer") == 4
                          && GridContent.TuningInt("logistics.port.source_buffer") == 2 && GridContent.TuningInt("logistics.port.source_interval_steps") == 1
                          && Mathf.Approximately(GridContent.Tuning("logistics.port.sync_seconds"), 1f)
                          && GridContent.TuningInt("logistics.belt.hp_t1") == 60 && GridContent.TuningInt("logistics.belt.hp_t2") == 90 && GridContent.TuningInt("logistics.belt.hp_t3") == 120
                          && GridContent.TuningInt("logistics.weather.sandstorm_slow_pct") == 25;
            Expect(tuning, "新调参已入表：废料物品编号 1、输入缓存 4、输出待推 2、推送间隔 1 步、端口对账 1 秒、耐久 60 / 90 / 120、沙暴减速 25%");
            string[] keys =
            {
                "logistics.hover.title", "logistics.hover.items", "logistics.hover.items_empty", "logistics.hover.item_entry", "logistics.hover.speed", "logistics.hover.speed_slowed",
                "logistics.hover.covered", "logistics.hover.throughput", "logistics.hover.throughput_measuring", "logistics.hover.state", "logistics.hover.hp", "logistics.hover.network",
                "logistics.hover.network_loop", "logistics.hover.from_port", "logistics.hover.to_port", "logistics.hover.actions", "logistics.hover.placeholder_item",
                "logistics.block.sink_rejects", "logistics.block.sink_rejects_all", "logistics.block.store_full",
                "logistics.port.title", "logistics.port.close", "logistics.port.store_line", "logistics.port.empty", "logistics.port.hint", "logistics.port.in", "logistics.port.out",
                "logistics.port.connected", "logistics.port.disconnected_in", "logistics.port.disconnected_out", "logistics.port.inactive", "logistics.port.state.damaged",
                "logistics.port.state.planned", "logistics.port.state.other", "logistics.port.role_none", "logistics.port.accept", "logistics.port.accept_none",
                "logistics.port.stats_in", "logistics.port.stats_out", "logistics.port.per_minute", "logistics.port.store_full", "logistics.port.store_empty", "logistics.port.out_blocked",
                "logistics.port.filter_label", "logistics.port.filter_all", "logistics.port.filter_off", "logistics.port.filter_item", "logistics.port.filter_changed",
                "logistics.port.filter_note", "logistics.port.open_hint",
                "ui.build.clear_mode", "ui.build.btn_clear", "ui.build.clear_box", "ui.build.clear_done", "ui.build.clear_done_discard", "ui.build.clear_nothing",
                "ui.build.clear_no_belt", "ui.build.clear_confirm_title", "ui.build.clear_confirm_fit", "ui.build.clear_confirm_over", "ui.build.clear_confirm_reason_full",
                "ui.build.clear_confirm_reason_kind", "ui.build.clear_confirm_ok", "ui.build.clear_confirm_consequence", "ui.build.clear_cancelled", "ui.build.belt_reversed",
                "logistics.destroyed.notify", "build.queue.destroyed_name", "build.queue.destroyed_status", "build.queue.rebuild", "build.queue.rebuild_all", "build.queue.rebuilt",
                "build.queue.ghost_removed", "build.hover.destroyed", "codex.logistics.belt.title", "codex.logistics.belt.body", "codex.logistics.belt.hint",
                "build.tool.belt_t2.desc", "build.tool.belt_t3.desc", "input.action.clear_belt_mode.name",
            };
            var missing = new List<string>();
            foreach (string k in keys)
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    string v = GameText.Get(k);
                    if (string.IsNullOrEmpty(v) || GameText.ContainsMarker(v))
                    {
                        missing.Add($"{k}[{lang}]");
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(missing.Count == 0, $"{keys.Length} 个新文本键中英齐全（缺：{string.Join(",", missing)}）");
            Expect(InputDisplay.ForAction(GameActionId.ClearBeltMode).Contains("J"), $"新动作“清带”已接入且可重绑（默认 {InputDisplay.ForAction(GameActionId.ClearBeltMode)}，建造上下文）");
            string[] hooks = { GuidanceHooks.LogisticsFirstPortConnected, GuidanceHooks.LogisticsPortPanelFirstOpen, GuidanceHooks.LogisticsFirstClear, GuidanceHooks.LogisticsFirstDestroyed };
            Expect(hooks.All(h => GuidanceHooks.Known.Contains(h)), "四个新引导钩子已登记（第一次接上端口 / 打开端口面板 / 清带 / 传送带被摧毁；引导内容在 FG15-UX-04）");
            GameConfig.fg.CodexEntry entry = ConfigSystem.Instance.Tables.TbCodexEntry.GetOrDefault("codex.logistics.belt");
            Expect(entry != null && entry.Hooks.Contains(GuidanceHooks.LogisticsFirstBelt) && entry.Hooks.Contains(GuidanceHooks.LogisticsFirstPortConnected)
                   && entry.Links.Contains("codex.build.grid"),
                "图鉴有“传送带与端口”条目（FG03 第 4 节）：第一次放下传送带 / 堵塞 / 接上端口时解锁，链接到格网建造");
            bool t2 = BuildCatalog.TryGet("belt_t2", out BuildEntry e2) && e2.IsTool && e2.Tool.Tier == 1 && e2.Tool.ScrapPerCell == 2 && e2.CategoryId == "logistics";
            bool t3 = BuildCatalog.TryGet("belt_t3", out BuildEntry e3) && e3.IsTool && e3.Tool.Tier == 2 && e3.Tool.ScrapPerCell == 3 && e3.CategoryId == "logistics";
            Expect(t2 && t3 && BuildCatalog.CountInCategory("logistics") >= 3 && HomeGridService.BeltCostPerCell(1) == 2 && HomeGridService.BeltCostPerCell(2) == 3,
                "建造菜单“物流”有三级传送带（T1 / T2 / T3，每格 1 / 2 / 3 废料），拆除返还按对应造价");
            var roles = GridContent.PortsOf("warehouse").Select(p => p.Id + "=" + p.Role).ToList();
            bool rolesOk = GridContent.PortsOf("core").All(p => p.Role == "store") && GridContent.PortsOf("warehouse").All(p => p.Role == "store")
                           && GridContent.PortsOf("assembly_station").All(p => p.Role == (p.Kind == "in" ? "prod" : "none")) && GridContent.PortsOf("repair_bay").All(p => p.Role == "none");
            Expect(rolesOk, $"端口表的物流角色：核心 / 仓库是家园存量的输入输出口；装配站输入口收机器材料（FG4-ECO-03）、输出口不推；维修台 / 解析台还没有收发物品的配方（{string.Join("，", roles)}）");
        }

        // ── B. 端口绑定生命周期 ─────────────────────────────────────────────────────

        private static void CheckPortBindingLifecycle()
        {
            CampaignState s = NewWorld(7001);
            WaitSync();
            BeltPortService.Binding coreIn = Port(s, "core", "core.in0");
            BeltPortService.Binding asmIn = Port(s, "assembly_station", "assembly_station.in0");
            BeltPortService.Binding asmOut = Port(s, "assembly_station", "assembly_station.out0");
            BeltPortService.Binding whOut = Port(s, "warehouse", "warehouse.out0");
            BeltPortInfo ci = PortInfo(coreIn);
            BeltPortInfo ai = PortInfo(asmIn);
            BuildingRecord core = Building(s, "core");
            GridCell expectCell = GridMath.PortCell(new GridCell(core.GridX, core.GridY), 0, -2, GridMath.NormalizeRotation(core.Rotation));
            bool coreOk = coreIn != null && coreIn.Store && !coreIn.IsOutput && coreIn.PortId >= BeltPortService.PortIdBase && ci.Kind == BeltPortKind.Sink
                          && ci.Accept == BeltConst.AcceptAnyOneKind && coreIn.PortCell == expectCell && ci.X == expectCell.X && ci.Y == expectCell.Y
                          && ci.Face == (byte)GridMath.RotateDir(GridDir.S, GridMath.NormalizeRotation(core.Rotation)) && ci.BufferCap == 4;
            // FG4-ECO-03（DEBT-FG3LOG03-01）：装配站输入口改为收机器材料（收货集合）；输出口仍然不推（机器从出口驶出）。
            bool noneOk = asmIn != null && !asmIn.Store && ai.Accept == BeltConst.AcceptSet && asmOut != null && PortInfo(asmOut).Kind == BeltPortKind.Source;
            Expect(coreOk && noneOk && whOut == null && s.Belts.PortBindings.Length == BeltPortService.Count,
                $"开局 1 秒内运转中的建筑自动登记端口：核心输入口 = 家园存量口（端口号 {coreIn?.PortId}，FG4-ECO-01 起收全部可存物品（缓存一次只放一种）、缓存 4、朝南、位置随核心），" +
                $"装配站的输入口只收机器材料（FG4-ECO-03）、输出口不推；开局受损的仓库没有端口（{BeltPortService.Count} 个绑定，与存档绑定表一致）");

            BuildingRecord wh = ActivateWarehouse(s);
            whOut = Port(s, "warehouse", "warehouse.out0");
            BeltPortService.Binding whIn = Port(s, "warehouse", "warehouse.in0");
            BeltPortInfo wo = PortInfo(whOut);
            Expect(whOut != null && whIn != null && wo.Kind == BeltPortKind.Source && wo.ItemType == BeltItems.ScrapId && whOut.Record.Filter == BeltPortService.FilterAll
                   && whOut.BeltCell != whOut.PortCell,
                $"仓库修好后下一次对账接上输入 / 输出口：输出口推 {BeltItems.Name(wo.ItemType)}（过滤默认“全部可存物品”），推到端口外侧 {whOut?.BeltCell}");

            // 转向：端口跟着建筑转（在铺带之前，免得带挡住出口通道）。
            GridCell oldOut = whOut.BeltCell;
            GridDir oldFace = whOut.Face;
            GridOpResult rot = HomeGridService.TryRotate(s, wh.BuildingId);
            WaitSync();
            BeltPortService.Binding rotated = Port(s, "warehouse", "warehouse.out0");
            bool movedOk = rot.Success && rotated != null && rotated.BeltCell != oldOut && rotated.Face == GridMath.RotateDir(oldFace, 90)
                           && BeltPortService.LastSyncRemoved >= 2 && BeltPortService.LastSyncAdded >= 2;
            Expect(movedOk, $"仓库原地转向 90°：下一次对账把端口挪到新位置、新朝向（输出口 {oldOut}→{rotated?.BeltCell}，朝向 {oldFace}→{rotated?.Face}）" +
                            (rot.Success ? string.Empty : "；转向失败：" + rot.Describe()));
            // 再转三次（某个朝向被出口通道 / 地形挡住时那一次转不了，建筑保持原朝向）：每次对账后端口都与建筑当前的朝向一致。
            var poses = new List<string>();
            bool consistent = true;
            for (int i = 0; i < 3; i++)
            {
                GridOpResult ri = HomeGridService.TryRotate(s, wh.BuildingId);
                WaitSync();
                int rotNow = GridMath.NormalizeRotation(wh.Rotation);
                GridCell expect = GridMath.PortCell(new GridCell(wh.GridX, wh.GridY), 2, 0, rotNow);
                BeltPortService.Binding cur = Port(s, "warehouse", "warehouse.out0");
                bool match = cur != null && cur.PortCell == expect && cur.Face == GridMath.RotateDir(GridDir.E, rotNow);
                consistent &= match;
                poses.Add($"{(ri.Success ? "转到" : "没转（" + ri.Describe() + "）")} {rotNow}° → 输出口 {cur?.PortCell}{(match ? "" : "（应为 " + expect + "）")}");
            }
            Expect(consistent, "再转三次：每次对账后输出口的位置与朝向都与仓库当前朝向一致（" + string.Join("；", poses) + "）");

            // 端口号不与手工端口冲突：先占一个建筑端口号，下一个新端口跳过它。
            int next = Math.Max(BeltPortService.PortIdBase, s.Belts.NextPortId);
            BeltNetworkService.TryAddSink(s, next, new GridCell(core.GridX + 40, core.GridY + 40), 1, 0);
            wh.ConstructionState = BuildingConstructionState.Damaged;
            WaitSync();
            wh.ConstructionState = BuildingConstructionState.Operational;
            WaitSync();
            BeltPortService.Binding reb = Port(s, "warehouse", "warehouse.in0");
            Expect(reb != null && reb.PortId != next && BeltNetworkService.Kernel.TryGetPortInfo(next, out BeltPortInfo manual) && manual.X == core.GridX + 40,
                $"新分配的端口号跳过已被占用的号（{next} 被手工端口占用，仓库输入口拿到 {reb?.PortId}），手工端口不被对账删掉");
        }

        // ── C. 仓库 → 传送带 → 仓库 ──────────────────────────────────────────────────

        private static void CheckWarehouseLoop()
        {
            NotificationCenter.ResetForTests();
            CampaignState s = NewWorld(7101, scrap: 100);
            ActivateWarehouse(s);
            List<(GridCell cell, BeltDir dir)> path = WarehouseLoop(s, 0, out string failure);
            if (path == null)
            {
                Fail("仓库闭环铺不下：" + failure);
                return;
            }
            BeltPortService.Binding outP = Port(s, "warehouse", "warehouse.out0");
            BeltPortService.Binding inP = Port(s, "warehouse", "warehouse.in0");
            long total0 = Conserved(s);
            bool conserved = true;
            long worst = 0;
            StepUntil(() => false, 70, () =>
            {
                long now = Conserved(s);
                conserved &= now == total0;
                worst = Math.Max(worst, Math.Abs(now - total0));
            });
            BeltPortInfo o = PortInfo(outP);
            BeltPortInfo i = PortInfo(inP);
            Expect(o.Connected && i.Connected && o.Total > 0 && i.Total > 0 && conserved && BeltNetworkService.Kernel.Ledger.Balanced
                   && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsFirstPortConnected),
                $"仓库输出口 → {path.Count} 格 T1 → 仓库输入口（FGR-LOG-021）：70 游戏秒推出 {o.Total} 件、收回 {i.Total} 件；" +
                $"库存 + 端口待推 + 缓存 + 带上始终 = {total0}（最大偏差 {worst}）；第一次接上端口发引导钩子");

            GridCell first = path[0].cell;
            GridCell mid = path[path.Count / 2].cell;
            GridCell last = path[path.Count - 1].cell;
            BeltNetworkService.TryDescribeHover(s, mid, out string title, out string body);
            BeltNetworkService.TryDescribeHover(s, first, out _, out string firstBody);
            BeltNetworkService.TryDescribeHover(s, last, out _, out string lastBody);
            string wh = HomeGridService.DisplayName("warehouse");
            bool hover = title.Contains("传送带 T1") && body.Contains("满载速度：60 件/分钟") && body.Contains("实测吞吐") && body.Contains("耐久：60 / 60")
                         && (body.Contains("废料 ×") || body.Contains("物品：空")) && body.Contains("所在网络") && firstBody.Contains("起点：从" + wh)
                         && lastBody.Contains("终点：送进" + wh) && !GameText.ContainsMarker(body + firstBody + lastBody);
            Expect(hover, $"悬停（FGR-LOG-081）：“{title}”｜{body.Replace("\n", " / ")}｜起点格“{firstBody.Split('\n').FirstOrDefault(l => l.StartsWith("起点"))}”｜终点格“{lastBody.Split('\n').FirstOrDefault(l => l.StartsWith("终点"))}”");
        }

        // ── D. 三级吞吐（FGT-LOG-005 正式版）────────────────────────────────────────

        private static void CheckTierThroughput()
        {
            CampaignState s = NewWorld(7201, scrap: 150);
            ActivateWarehouse(s);
            List<(GridCell cell, BeltDir dir)> path = WarehouseLoop(s, 0, out string failure);
            if (path == null)
            {
                Fail("仓库闭环铺不下：" + failure);
                return;
            }
            BeltPortService.Binding inP = Port(s, "warehouse", "warehouse.in0");
            var parts = new List<string>();
            bool ok = true;
            foreach (int tier in new[] { 0, 1, 2 })
            {
                foreach ((GridCell cell, BeltDir _) in path)
                {
                    BeltNetworkService.TrySetTier(s, cell, tier);
                }
                // 稳态：满载一圈（最慢 T1 约 4 秒一格）后再数。
                WorldSimulation.StepMany(GameClock.StepHz * (path.Count * 4 + 10));
                long before = PortInfo(inP).Total;
                WorldSimulation.StepMany(GameClock.StepHz * 60);
                long got = PortInfo(inP).Total - before;
                int rated = 60 << tier;
                BeltNetworkService.Kernel.TryGetCellInfo(path[1].cell.X, path[1].cell.Y, out BeltCellInfo c);
                parts.Add($"T{tier + 1} 60 游戏秒收下 {got} 件（设计 {rated}，格悬停 {c.RatedItemsPerMinute}）");
                ok &= Math.Abs(got - rated) <= 2 && c.RatedItemsPerMinute == rated;
            }
            Expect(ok, "三级传送带经真实仓库端口的满载吞吐（FGR-LOG-020 / FGT-LOG-005）：" + string.Join("；", parts));
        }

        // ── E. 建造菜单三级 + 原地反转 ────────────────────────────────────────────────

        private static void CheckBuildMenuTiersAndReverse()
        {
            CampaignState s = NewWorld(7301, scrap: 200);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            try
            {
                GridCell core = HomeGridService.CorePivot(s);
                GridCell? row = FindRow(s, new GridCell(core.X - 10, core.Y + 26), 4, 16);
                if (row == null || mode == null)
                {
                    Fail("找不到铺 4 格传送带的空地 / 建造模式没绑定");
                    return;
                }
                GridCell a = row.Value;
                mode.Open();
                mode.Select("belt_t3");
                mode.PointerDown(s, a);
                mode.SetHover(s, new GridCell(a.X + 3, a.Y));
                mode.PointerUp(s, new GridCell(a.X + 3, a.Y));
                bool planned = HomeValleyConstruction.TryFindPlannedCell(s, a, out PlannedBeltRecord plan, out _) && plan.Tier == 2 && plan.ScrapPerCell == 3 && plan.Xs.Length == 4;
                bool built = StepUntil(() => Enumerable.Range(0, 4).All(k => BeltNetworkService.Kernel.HasCell(a.X + k, a.Y)), 300);
                BeltNetworkService.Kernel.TryGetCellInfo(a.X, a.Y, out BeltCellInfo info);
                BeltNetworkService.TryDescribeHover(s, a, out string title, out string body);
                Expect(planned && built && info.Tier == 2 && info.Dir == BeltDir.East && title.Contains("传送带 T3") && body.Contains("240"),
                    $"建造菜单选 T3、拖拽 4 格：规划为 T3（每格 3 废料），机器施工建成后内核里是 T3 朝东；悬停“{title}”写满载 240 件/分钟");

                // 原地反转：放下 2 件，空闲状态指着这一格按旋转键（R）。
                mode.ClearSelection();
                BeltNetworkService.Kernel.InsertItemAt(a.X + 1, a.Y, 5000, 7);
                BeltNetworkService.Kernel.InsertItemAt(a.X + 1, a.Y, 30000, 9);
                mode.SetHover(s, new GridCell(a.X + 1, a.Y));
                reader.Press(KeyCode.R);
                mode.Tick(null, s, true);
                Frame(reader);
                BeltNetworkService.Kernel.TryGetCellInfo(a.X + 1, a.Y, out BeltCellInfo rev);
                bool mirrored = rev.Count == 2 && rev.Item0 == 7 && rev.Item1 == 9 && rev.Pos0 == BeltConst.CellLength - 1 - 5000 && rev.Pos1 == BeltConst.CellLength - 1 - 30000;
                Expect(rev.Dir == BeltDir.West && mirrored && mode.LastResult.Outcome == GridOpResult.Kind.BeltReversed && mode.StatusText.Contains("已反转") && mode.StatusText.Contains(GameText.Get(GridMath.DirTextKey(GridDir.W)))
                       && BeltNetworkService.Kernel.Ledger.Balanced,
                    $"指着已建成的传送带按旋转键（FGR-LOG-020 原地反转）：方向东→{rev.Dir}，2 件物品位置镜像、不丢不增；状态行“{mode.StatusText}”");
            }
            finally
            {
                mode?.Close();
                InputRouter.DebugSetReader(null);
            }
        }

        private static GridCell? FindRow(CampaignState s, GridCell from, int length, int radius)
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

        // ── F. 堵塞原因 ──────────────────────────────────────────────────────────

        private static void CheckBlockReasons()
        {
            CampaignState s = NewWorld(7401, scrap: 60);
            ActivateWarehouse(s);
            List<(GridCell cell, BeltDir dir)> path = WarehouseLoop(s, 2, out string failure);
            if (path == null)
            {
                Fail("仓库闭环铺不下：" + failure);
                return;
            }
            BeltPortService.Binding inP = Port(s, "warehouse", "warehouse.in0");
            GridCell last = path[path.Count - 1].cell;
            int cap = HomeValleyCargo.GetStorageCapacity(s, CampaignEconomyLedger.ResourceScrap);
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            // 仓库满：先停掉输出口（否则它从同一个仓库取、输入口就总有空位），带上放一串废料，库存顶满——输入口缓存存不进去，带停下，原因写库存与容量。
            BeltPortService.TrySetFilter(s, Building(s, "warehouse").BuildingId, "warehouse.out0", BeltPortService.FilterOff, out _);
            for (int k = 0; k < path.Count - 1; k++)
            {
                BeltNetworkService.Kernel.InsertItem(path[k].cell.X, path[k].cell.Y, BeltItems.ScrapId);
            }
            s.Scrap = cap;
            bool full = StepUntil(() => BeltNetworkService.Kernel.TryGetCellInfo(last.X, last.Y, out BeltCellInfo c) && c.Block == BeltBlock.SinkFull, 30);
            BeltNetworkService.Kernel.TryGetCellInfo(last.X, last.Y, out BeltCellInfo lastInfo);
            string fullText = BeltNetworkService.DescribeBlock(lastInfo);
            int onBelt = BeltNetworkService.Kernel.CountItemsSlow();
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            bool held = BeltNetworkService.Kernel.CountItemsSlow() == onBelt && PortInfo(inP).Buffered == 4;
            s.Scrap = cap - 50;
            long before = PortInfo(inP).Total;
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            Expect(full && held && fullText.Contains("仓库满了") && fullText.Contains(cap + " / " + cap) && PortInfo(inP).Total > before,
                $"仓库满（FGR-LOG-025）：输入口缓存 4 满了，带停在末端、物品不消失（{onBelt} 件）；原因“{fullText}”；腾出 50 空间后自动继续");

            // FG4-ECO-01：输入口收全部可存物品（物品 #7 = 电子件入库）；物品表里没有的编号（#900，旧档 / 表删掉的物品）放到仓库旁边的地上，不消失、不堵带。
            int electronic0 = GameLogic.Campaign.Economy.HomeInventory.Stock(s, "electronic");
            BeltNetworkService.Kernel.InsertItem(path[path.Count - 3].cell.X, path[path.Count - 3].cell.Y, 7);
            BeltNetworkService.Kernel.InsertItem(path[path.Count - 5].cell.X, path[path.Count - 5].cell.Y, 900);
            bool rejected = StepUntil(() => GameLogic.Campaign.Economy.HomeInventory.Stock(s, "electronic") == electronic0 + 1
                                            && (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Any(g => g.ResourceType == "item:900" && g.Amount == 1), 60);
            BeltNetworkService.Kernel.TryGetCellInfo(last.X, last.Y, out BeltCellInfo rej);
            Expect(rejected && rej.Block != BeltBlock.SinkRejects,
                $"输入口收全部可存物品：物品 #7（电子件）进了家园仓库；物品表里没有的 #900 落在仓库旁边的地上（不消失、不堵带，末端此刻“{BeltNetworkService.DescribeBlock(rej)}”）");

            // 建筑不收这种物品（装配站输入口只收机器材料，FG4-ECO-03；废料不是机器材料）；朝向不对不接。
            BeltPortService.Binding asmIn = Port(s, "assembly_station", "assembly_station.in0");
            BeltDir into = (BeltDir)(((int)asmIn.Face + 2) & 3);
            BeltDir side = (BeltDir)(((int)asmIn.Face + 1) & 3);
            GridCell c0 = asmIn.BeltCell;
            BeltOpResult sideways = BeltNetworkService.TryPlace(s, c0, side, 0);
            WorldSimulation.StepMany(3);
            bool notLinked = sideways.Ok && !PortInfo(asmIn).Connected;
            BeltNetworkService.TrySetDirection(s, c0, into);
            BeltNetworkService.Kernel.InsertItem(c0.X, c0.Y, BeltItems.ScrapId);
            bool none = StepUntil(() => BeltNetworkService.Kernel.TryGetCellInfo(c0.X, c0.Y, out BeltCellInfo c) && c.Block == BeltBlock.SinkRejects, 20);
            BeltNetworkService.Kernel.TryGetCellInfo(c0.X, c0.Y, out BeltCellInfo asmCell);
            string noneText = BeltNetworkService.DescribeBlock(asmCell);
            Expect(notLinked && PortInfo(asmIn).Connected && none && noneText.Contains("只收机器材料") && noneText.Contains(HomeGridService.DisplayName("assembly_station")),
                $"输入口只接“末端正对着它”的带：侧着放不接（没接上），转成朝着建筑才接上；装配站只收机器材料，废料到末端停下，原因“{noneText}”");

            // 输出口不往“指回建筑”的带上推。
            CampaignState s2 = NewWorld(7402, scrap: 60);
            ActivateWarehouse(s2);
            BeltPortService.Binding outP = Port(s2, "warehouse", "warehouse.out0");
            BeltDir back = (BeltDir)(((int)outP.Face + 2) & 3);
            BeltNetworkService.TryPlace(s2, outP.BeltCell, back, 0);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            BeltPortInfo po = PortInfo(outP);
            bool refused = !po.Connected && po.Total == 0;
            BeltNetworkService.TrySetDirection(s2, outP.BeltCell, (BeltDir)outP.Face);
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            BeltPortInfo po2 = PortInfo(outP);
            Expect(refused && po2.Connected && po2.Total > 0,
                $"输出口外侧的带指回仓库时不接、不推（推了会顶在建筑上）；转成背离建筑后接上并推出 {po2.Total} 件");
        }

        // ── G. 输出过滤 ──────────────────────────────────────────────────────────

        private static void CheckOutputFilter()
        {
            CampaignState s = NewWorld(7501, scrap: 100);
            BuildingRecord wh = ActivateWarehouse(s);
            List<(GridCell cell, BeltDir dir)> path = WarehouseLoop(s, 1, out string failure);
            if (path == null)
            {
                Fail("仓库闭环铺不下：" + failure);
                return;
            }
            BeltPortService.Binding outP = Port(s, "warehouse", "warehouse.out0");
            WorldSimulation.StepMany(GameClock.StepHz * 10);
            long total = Conserved(s);
            bool off = BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", BeltPortService.FilterOff, out _);
            long afterOff = Conserved(s);
            int pendingAfter = PortInfo(outP).Pending;
            long emitted = PortInfo(outP).Total;
            WorldSimulation.StepMany(GameClock.StepHz * 60);
            bool stopped = PortInfo(outP).Total == emitted;
            bool drained = BeltNetworkService.Kernel.CountItemsSlow() == 0 && s.Scrap == 100 - PortBuffers(s);
            Expect(off && pendingAfter == 0 && afterOff == total && stopped && drained && outP.Record.Filter == BeltPortService.FilterOff,
                $"输出过滤改成“停止输出”：端口手里待推的物品退回仓库（守恒 {total}），之后不再推；带上的流回仓库、放空（库存回到 {s.Scrap}）");
            bool on = BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", BeltItems.ScrapId, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 5);
            Expect(on && PortInfo(outP).Total > emitted && PortInfo(outP).ItemType == BeltItems.ScrapId && outP.Record.Filter == BeltItems.ScrapId && Conserved(s) == total,
                $"改成“只输出废料”：恢复推送（累计 {PortInfo(outP).Total} 件），过滤记在绑定表里（{outP.Record.Filter}）");
            bool bad = BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", 900, out string reason); // FG4-ECO-01：物品表里没有的编号
            bool inRow = BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.in0", BeltPortService.FilterOff, out string inReason);
            Expect(!bad && !string.IsNullOrEmpty(reason) && !inRow && !string.IsNullOrEmpty(inReason) && outP.Record.Filter == BeltItems.ScrapId,
                $"负向：家园仓库存不了的物品不能选为过滤（“{reason}”）；输入口没有输出过滤（“{inReason}”）");
        }

        private static int PortBuffers(CampaignState s)
        {
            int n = 0;
            foreach (BeltPortService.Binding b in BeltPortService.All)
            {
                if (BeltNetworkService.Kernel.TryGetPortCounts(b.PortId, out int p, out int bu))
                {
                    n += Math.Max(0, p) + Math.Max(0, bu);
                }
            }
            return n;
        }

        // ── H. 建筑停用 → 端口物品退回 ───────────────────────────────────────────────

        private static void CheckBuildingInactiveReturns()
        {
            CampaignState s = NewWorld(7601, scrap: 100);
            BuildingRecord wh = ActivateWarehouse(s);
            List<(GridCell cell, BeltDir dir)> path = WarehouseLoop(s, 0, out string failure);
            if (path == null)
            {
                Fail("仓库闭环铺不下：" + failure);
                return;
            }
            WorldSimulation.StepMany(GameClock.StepHz * 20);
            long total = Conserved(s);
            int held = PortBuffers(s);
            int oldOut = Port(s, "warehouse", "warehouse.out0").PortId;
            wh.ConstructionState = BuildingConstructionState.Damaged;
            WaitSync();
            bool removed = Port(s, "warehouse", "warehouse.out0") == null && Port(s, "warehouse", "warehouse.in0") == null
                           && !BeltNetworkService.Kernel.TryGetPortCounts(oldOut, out _, out _);
            long afterRemove = Conserved(s);
            GridCell last = path[path.Count - 1].cell;
            bool endStop = StepUntil(() => BeltNetworkService.Kernel.TryGetCellInfo(last.X, last.Y, out BeltCellInfo c) && c.Block == BeltBlock.EndOfBelt, 60);
            Expect(held > 0 && removed && afterRemove == total && endStop,
                $"仓库受损停用：下一次对账删掉它的端口，端口手里的 {held} 件（待推 + 缓存）退回仓库，守恒 {total}；带上的物品留在带上，末端写“到头了”");
            wh.ConstructionState = BuildingConstructionState.Operational;
            WaitSync();
            BeltPortService.Binding again = Port(s, "warehouse", "warehouse.out0");
            long t0 = PortInfo(again).Total;
            WorldSimulation.StepMany(GameClock.StepHz * 10);
            Expect(again != null && again.PortId != oldOut && PortInfo(again).Total > t0 && Conserved(s) == total,
                $"修好后重新接上（新端口号 {again?.PortId}），继续流动，守恒不变");
        }

        // ── I. 清带工具（正式输入）─────────────────────────────────────────────────────

        private static void CheckClearTool()
        {
            CampaignState s = NewWorld(7701, scrap: 100);
            ActivateWarehouse(s);
            List<(GridCell cell, BeltDir dir)> path = WarehouseLoop(s, 0, out string failure);
            if (path == null)
            {
                Fail("仓库闭环铺不下：" + failure);
                return;
            }
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            try
            {
                WorldSimulation.StepMany(GameClock.StepHz * 20);
                mode.Open();
                mode.Tick(null, s, true);
                Frame(reader);
                reader.Press(KeyCode.J);
                mode.Tick(null, s, true);
                Frame(reader);
                bool on = mode.ClearMode;
                long total = Conserved(s);
                int onBelt = BeltNetworkService.Kernel.CountItemsSlow();
                int scrap0 = s.Scrap;
                GridCell mid = path[path.Count / 2].cell;
                mode.SetHover(s, mid);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                reader.MouseUp.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                bool clearedNet = BeltNetworkService.Kernel.CountItemsSlow() == 0 && s.Scrap == scrap0 + onBelt && Conserved(s) == total
                                  && mode.LastResult.Outcome == GridOpResult.Kind.BeltsCleared && mode.StatusText.Contains("清空 " + path.Count + " 格")
                                  && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsFirstClear) && BeltNetworkService.Kernel.Ledger.Balanced;
                Expect(on && onBelt > 0 && clearedNet,
                    $"建造模式按 J 进入清带、点闭环上一格（FGR-LOG-026）：整条带 {path.Count} 格上的 {onBelt} 件送进仓库（{scrap0}→{s.Scrap}），守恒；状态行“{mode.StatusText}”");

                // 仓库放不下：先确认。取消什么都不变；确认丢弃放不下的。
                WorldSimulation.StepMany(GameClock.StepHz * 20);
                int cap = HomeValleyCargo.GetStorageCapacity(s, CampaignEconomyLedger.ResourceScrap);
                s.Scrap = cap;
                int items = BeltNetworkService.Kernel.CountItemsSlow();
                mode.SetHover(s, mid);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                reader.MouseUp.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                bool asked = mode.PendingClearConfirm && UiConfirmDialog.IsOpen && UiConfirmDialog.Current.Title.Contains(items.ToString())
                             && UiConfirmDialog.Current.Lines.Any(l => l.Contains("剩余空间 0"));
                UiConfirmDialog.Cancel();
                bool unchanged = BeltNetworkService.Kernel.CountItemsSlow() == items && s.Scrap == cap && mode.StatusText.Contains("已取消清带");
                long discarded0 = s.Belts.Discarded;
                mode.SetHover(s, mid);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                reader.MouseUp.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                int shown = BeltNetworkService.Kernel.CountItemsSlow();
                UiConfirmDialog.Confirm();
                bool discarded = BeltNetworkService.Kernel.CountItemsSlow() == 0 && s.Belts.Discarded == discarded0 + shown && s.Scrap == cap
                                 && mode.StatusText.Contains("已丢弃") && BeltNetworkService.Kernel.Ledger.Balanced;
                Expect(items > 0 && asked && unchanged && discarded,
                    $"仓库满时清带（FG03 负向）：确认框写明放不下 {items} 件、剩余空间 0；取消 → 带和物品原样；确认 → 丢弃 {shown} 件（累计丢弃 {s.Belts.Discarded}），库存不超容量");

                // 仓库存不了的物品：物品 #900（物品表里没有）与废料混在一起，空间够废料 → 只问 #900（FG4-ECO-01 起表里的固体都能存）。
                s.Scrap = 0;
                GridCell c1 = path[2].cell;
                BeltNetworkService.Kernel.InsertItem(c1.X, c1.Y, 900);
                BeltNetworkService.Kernel.InsertItemAt(path[4].cell.X, path[4].cell.Y, 20000, BeltItems.ScrapId);
                BeltClearPlan plan = BeltClearService.PlanNetwork(s, c1);
                bool kindAsk = plan.NeedsConfirm && plan.OverflowItems == 1 && plan.Overflow.ContainsKey(900) && plan.Fit == plan.Items - 1;
                mode.SetHover(s, c1);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                reader.MouseUp.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                bool kindLine = UiConfirmDialog.IsOpen && UiConfirmDialog.Current.Lines.Any(l => l.Contains("还存不了"));
                UiConfirmDialog.Confirm();
                Expect(kindAsk && kindLine && s.Scrap == plan.Fit && BeltNetworkService.Kernel.CountItemsSlow() == 0,
                    $"家园仓库存不了的物品（#900）算“放不下”，确认框写明原因；确认后废料 {plan.Fit} 件入库、#900 丢弃");

                // 拖框：只清框里的格。
                s.Scrap = 100;
                WorldSimulation.StepMany(GameClock.StepHz * 20);
                GridCell b0 = path[1].cell;
                GridCell b1 = path[3].cell;
                int inBox = 0;
                var boxCells = new HashSet<GridCell>();
                for (int y = Math.Min(b0.Y, b1.Y); y <= Math.Max(b0.Y, b1.Y); y++)
                {
                    for (int x = Math.Min(b0.X, b1.X); x <= Math.Max(b0.X, b1.X); x++)
                    {
                        if (BeltNetworkService.Kernel.TryGetCellInfo(x, y, out BeltCellInfo bc))
                        {
                            inBox += bc.Count;
                            boxCells.Add(new GridCell(x, y));
                        }
                    }
                }
                int outside = BeltNetworkService.Kernel.CountItemsSlow() - inBox;
                s.Scrap = 0;
                mode.SetHover(s, b0);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                mode.SetHover(s, b1);
                mode.RefreshVisuals(s);
                bool indicator = mode.ClearPlan != null && !mode.ClearPlan.WholeNetwork && mode.BoxIndicatorVisible && mode.ClearPlan.Items == inBox;
                reader.MouseUp.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                int boxLeft = boxCells.Sum(c => BeltNetworkService.Kernel.TryGetCellInfo(c.X, c.Y, out BeltCellInfo bc) ? bc.Count : 0);
                Expect(inBox > 0 && indicator && boxLeft == 0 && BeltNetworkService.Kernel.CountItemsSlow() == outside && s.Scrap == inBox,
                    $"拖框清带：画出框、HUD 数出 {inBox} 件；松开只清框里 {boxCells.Count} 格（框外 {outside} 件不动）");

                // 空选区 / 空带；右键退出。
                GridCell empty = new GridCell(path[0].cell.X + 30, path[0].cell.Y + 30);
                mode.SetHover(s, empty);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                reader.MouseUp.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                string noBelt = mode.StatusText;
                // 带上没有物品：先经内核把整条带清空（测试准备），再点。
                BeltNetworkService.Kernel.ClearCells(BeltClearService.PlanNetwork(s, path[1].cell).Cells, null);
                mode.SetHover(s, path[1].cell);
                reader.MouseDown.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                reader.MouseUp.Add(0);
                mode.Tick(null, s, true);
                Frame(reader);
                string noItems = mode.StatusText;
                reader.MouseDown.Add(1);
                mode.Tick(null, s, true);
                Frame(reader);
                Expect(noBelt.Contains("这里没有传送带") && noItems.Contains("没有物品") && !mode.ClearMode && mode.IsOpen,
                    $"负向：空地上点 → “{noBelt}”；带上没有物品 → “{noItems}”；右键退出清带模式（建造模式仍开着）");

                // 环：清一个装满物品的环（FG03 负向“环形传送带”）。
                GridCell core = HomeGridService.CorePivot(s);
                GridCell? ringAt = FindRow(s, new GridCell(core.X - 14, core.Y + 30), 4, 20);
                if (ringAt == null)
                {
                    Fail("找不到放环的空地");
                    return;
                }
                GridCell o = ringAt.Value;
                var ring = new List<(GridCell, BeltDir)>
                {
                    (o, BeltDir.East), (new GridCell(o.X + 1, o.Y), BeltDir.East), (new GridCell(o.X + 2, o.Y), BeltDir.North),
                    (new GridCell(o.X + 2, o.Y + 1), BeltDir.West), (new GridCell(o.X + 1, o.Y + 1), BeltDir.West), (new GridCell(o.X, o.Y + 1), BeltDir.South),
                };
                bool laid = Lay(s, ring, 0, out string ringFail);
                foreach ((GridCell c, BeltDir _) in ring)
                {
                    BeltNetworkService.Kernel.InsertItemAt(c.X, c.Y, 1000, BeltItems.ScrapId);
                    BeltNetworkService.Kernel.InsertItemAt(c.X, c.Y, 25000, BeltItems.ScrapId);
                }
                WorldSimulation.StepMany(GameClock.StepHz * 10);
                BeltNetworkService.Kernel.TryGetCellInfo(o.X, o.Y, out BeltCellInfo ri);
                BeltClearPlan rp = BeltClearService.PlanNetwork(s, o);
                s.Scrap = 0;
                bool ringCleared = BeltClearService.Execute(s, rp, false, out _) && BeltClearService.PlanNetwork(s, o).Items == 0 && s.Scrap == 12 && ri.InLoop
                                   && rp.Cells.Count == 6;
                Expect(laid && ringCleared && BeltNetworkService.Kernel.Ledger.Balanced,
                    $"环形传送带（6 格、12 件，一直在转）点一格就清空整个环，12 件入库、账本平衡{(ringFail != null ? "；" + ringFail : string.Empty)}");
            }
            finally
            {
                UiConfirmDialog.DiscardAll();
                mode?.Close();
                InputRouter.DebugSetReader(null);
            }
        }

        // ── J. 损毁留虚影 ─────────────────────────────────────────────────────────

        private static void CheckDamageAndGhost()
        {
            NotificationCenter.ResetForTests();
            CampaignState s = NewWorld(7801, scrap: 100);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? rowAt = FindRow(s, new GridCell(core.X - 10, core.Y + 24), 5, 16);
            if (rowAt == null)
            {
                Fail("找不到铺 5 格传送带的空地");
                return;
            }
            GridCell a = rowAt.Value;
            var row = Enumerable.Range(0, 5).Select(k => (new GridCell(a.X + k, a.Y), BeltDir.East)).ToList();
            Lay(s, row, 1, out _);
            GridCell target = new GridCell(a.X + 2, a.Y);
            BeltNetworkService.TryDamage(s, target, 30, out BeltOpResult partial);
            BeltNetworkService.TryDescribeHover(s, target, out _, out string hpBody);
            bool dmg = partial.Ok && BeltNetworkService.HpOf(target) == 60 && hpBody.Contains("耐久：60 / 90")
                       && s.Belts.Damage.Any(d => d.X == target.X && d.Y == target.Y && d.Lost == 30);
            Expect(dmg, $"掉耐久（FGR-LOG-027）：T2 满耐久 90，挨 30 → 60；悬停写“耐久：60 / 90”，记进存档的耐久表");

            // 摧毁：3 件物品（2 件废料 + 1 件 #900：物品表里没有的编号）落地，原位置留虚影。
            BeltNetworkService.Kernel.InsertItemAt(target.X, target.Y, 2000, BeltItems.ScrapId);
            BeltNetworkService.Kernel.InsertItemAt(target.X, target.Y, 20000, BeltItems.ScrapId);
            BeltNetworkService.Kernel.InsertItemAt(target.X, target.Y, 40000, 900);
            long removed0 = BeltNetworkService.Kernel.Ledger.Removed;
            int ground0 = GroundScrap(s);
            int hauls0 = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(o => o.Kind == WorkOrderKind.Haul);
            bool destroyed = BeltNetworkService.TryDamage(s, target, 100, out _);
            bool gone = !BeltNetworkService.Kernel.HasCell(target.X, target.Y) && !s.Belts.Damage.Any(d => d.X == target.X && d.Y == target.Y);
            bool items = GroundScrap(s) == ground0 + 2 && (s.GroundItems ?? Array.Empty<GroundItemRecord>()).Any(g => g.ResourceType == "item:900" && g.Amount == 1)
                         && (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(o => o.Kind == WorkOrderKind.Haul) > hauls0
                         && BeltNetworkService.Kernel.Ledger.Removed == removed0 + 3 && BeltNetworkService.LastDestroyedItems == 3;
            bool ghost = HomeValleyConstruction.TryFindPlannedCell(s, target, out PlannedBeltRecord gp, out _) && gp.Destroyed && gp.Tier == 1 && gp.Dirs[0] == (int)BeltDir.East
                         && HomeValleyConstruction.IsPlannedMarker(HomeGridService.MapFor(s).GetBelt(target)) && HomeValleyWorkOrders.FindActiveBuild(s, HomeValleyConstruction.BeltPlanPrefix + gp.PlanId) == null;
            bool notified = NotificationCount("failure") >= 1 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsFirstDestroyed);
            Expect(destroyed && gone && items && ghost && notified,
                "摧毁：传送带从内核移除、耐久记录删掉；带上 3 件落地（2 件废料生成搬运单，#900 按编号落地），计入内核“移出”；原位置留下保留朝向与等级（T2 朝东）的虚影、占住格网，" +
                "没有施工单（不自动重建，规则在 FG6-DEF-03）；发“失败”通知与首次钩子");

            HomeValleyConstruction.TryDescribeSite(s, target, out string gTitle, out string gBody);
            var queue = new List<HomeValleyConstruction.QueueEntry>();
            HomeValleyConstruction.CollectQueue(s, queue);
            bool listed = queue.Any(q => q.IsDestroyedGhost && q.DestroyedPlanId == gp.PlanId && q.Order == null && q.Status.Contains("等你确认重建"));
            BeltOpResult placeOnGhost = BeltNetworkService.TryPlace(s, target, BeltDir.North, 0);
            Expect(gTitle.Contains("被摧毁") && gTitle.Contains("T2") && gBody.Contains("施工队列") && listed && !placeOnGhost.Ok,
                $"虚影悬停“{gTitle}｜{gBody.Replace("\n", " ")}”；施工队列列出它（没有施工单、等确认）；别的传送带放不上去");

            // 重建：机器按原设置建回来。
            bool rebuilt = HomeValleyConstruction.RebuildDestroyed(s, gp.PlanId) && HomeValleyWorkOrders.FindActiveBuild(s, HomeValleyConstruction.BeltPlanPrefix + gp.PlanId) != null;
            bool back = StepUntil(() => BeltNetworkService.Kernel.HasCell(target.X, target.Y), 240);
            BeltNetworkService.Kernel.TryGetCellInfo(target.X, target.Y, out BeltCellInfo bi);
            Expect(rebuilt && back && bi.Tier == 1 && bi.Dir == BeltDir.East && BeltNetworkService.HpOf(target) == 90 && HomeValleyConstruction.FindPlan(s, gp.PlanId) == null,
                $"施工队列“重建”：按原设置生成施工单，机器取料施工后内核里又是 T2 朝东、满耐久 90，虚影消失");

            // 两处被摧毁 → 一处在拆除模式里移除虚影（不花不退），一处“全部重建”。
            GridCell g1 = new GridCell(a.X, a.Y);
            GridCell g2 = new GridCell(a.X + 4, a.Y);
            BeltNetworkService.TryDamage(s, g1, 1000, out _);
            BeltNetworkService.TryDamage(s, g2, 1000, out _);
            int scrapBefore = s.Scrap;
            GridOpResult cancel = HomeGridService.TryRemoveBelts(s, new List<GridCell> { g1 });
            bool removedGhost = cancel.Outcome == GridOpResult.Kind.BeltsRemoved && !HomeValleyConstruction.TryFindPlannedCell(s, g1, out _, out _)
                                && HomeGridService.MapFor(s).GetBelt(g1) == 0 && s.Scrap == scrapBefore;
            int n = HomeValleyConstruction.RebuildAllDestroyed(s);
            Expect(removedGhost && n == 1 && HomeValleyConstruction.DestroyedGhostCount(s) == 0,
                $"拆除模式点被摧毁的虚影 = 移除（格网放开、库存不变）；“全部重建”安排了 {n} 处");

            BeltNetworkService.TryDamage(s, new GridCell(a.X + 30, a.Y + 30), 5, out BeltOpResult nf);
            BeltNetworkService.TryDamage(s, new GridCell(a.X + 1, a.Y), 0, out BeltOpResult zero);
            Expect(!nf.Ok && nf.Code == BeltResult.NotFound && !zero.Ok && zero.Code == BeltResult.InvalidArgument,
                $"负向：打空地 → {nf.Describe()}；伤害 0 → {zero.Describe()}");
        }

        // ── K. 天气 ──────────────────────────────────────────────────────────────

        private static void CheckWeather()
        {
            // 内核：三档各一条带、无限供货 / 收货，测 60 游戏秒的实测吞吐。
            string Measure(BeltKernel k, int pct, bool coverT1, out int[] got)
            {
                k.SetExposedSlowPercent(pct);
                for (int x = 0; x < 10; x++)
                {
                    k.SetCovered(x, 0, coverT1);
                }
                k.StepMany(20 * 20); // 满载的带换速度后 20 游戏秒进入稳态
                long[] t0 = new long[3];
                for (int t = 0; t < 3; t++)
                {
                    k.TryGetPortInfo(10 + t, out BeltPortInfo si);
                    t0[t] = si.Total;
                }
                k.StepMany(20 * 60);
                got = new int[3];
                for (int t = 0; t < 3; t++)
                {
                    k.TryGetPortInfo(10 + t, out BeltPortInfo si);
                    got[t] = (int)(si.Total - t0[t]);
                }
                return string.Join(" / ", got);
            }

            using (var k = new BeltKernel(BeltNetworkService.ReadConfig()))
            {
                for (int t = 0; t < 3; t++)
                {
                    for (int x = 0; x < 10; x++)
                    {
                        k.AddCell(x, t * 3, BeltDir.East, t);
                    }
                    k.AddSource(20 + t, 0, t * 3, 1, 1, BeltConst.Unlimited);
                    k.AddSink(10 + t, 10, t * 3, BeltConst.Unlimited, 0);
                }
                k.StepMany(20 * 60);
                string normal = Measure(k, 0, false, out int[] n0);
                string storm = Measure(k, 25, false, out int[] n1);
                string covered = Measure(k, 25, true, out int[] n2);
                string after = Measure(k, 0, false, out int[] n3);
                bool rated = k.EffectiveItemsPerMinute(0, false) == 60 && k.SlowedUnitsPerStep(0) == 600;
                k.SetExposedSlowPercent(25);
                bool slowRated = k.EffectiveItemsPerMinute(0, false) == 45 && k.EffectiveItemsPerMinute(1, false) == 90 && k.EffectiveItemsPerMinute(2, false) == 180
                                 && k.EffectiveItemsPerMinute(0, true) == 60 && k.SlowedUnitsPerStep(2) == 1800;
                bool bad = !k.SetExposedSlowPercent(-1) && !k.SetExposedSlowPercent(BeltConst.MaxSlowPercent + 1) && k.ExposedSlowPercent == 25;
                bool counts = Near(n0, 60, 120, 240) && Near(n1, 45, 90, 180) && n2[0] >= 59 && n2[0] <= 61 && Near(n3, 60, 120, 240);
                Expect(rated && slowRated && bad && counts && k.Ledger.Balanced,
                    $"沙暴露天减速 25%（FGR-LOG-028）：三档 60 游戏秒实测 天气正常 {normal}、沙暴 {storm}、T1 在顶棚下 {covered.Split('/')[0].Trim()}（不受影响）、恢复后 {after}；" +
                    "整数定点换算 45 / 90 / 180，非法百分比拒绝");
            }

            // 热更层：悬停写明减速与顶棚；两个内核同样操作逐位一致。
            CampaignState s = NewWorld(7901, scrap: 0);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? rowAt = FindRow(s, new GridCell(core.X + 8, core.Y + 26), 2, 16);
            GridCell a = rowAt ?? new GridCell(core.X + 8, core.Y + 26);
            Lay(s, new List<(GridCell, BeltDir)> { (a, BeltDir.East), (new GridCell(a.X + 1, a.Y), BeltDir.East) }, 1, out _);
            BeltNetworkService.SetExposedSlowdown(BeltNetworkService.SandstormSlowPercent);
            BeltNetworkService.SetCovered(new GridCell(a.X + 1, a.Y), true);
            BeltNetworkService.TryDescribeHover(s, a, out _, out string exposed);
            BeltNetworkService.TryDescribeHover(s, new GridCell(a.X + 1, a.Y), out _, out string roofed);
            BeltNetworkService.SetExposedSlowdown(0);
            BeltNetworkService.TryDescribeHover(s, a, out _, out string clear);
            Expect(exposed.Contains("满载速度：90 件/分钟（设计 120，露天减速 25%）") && roofed.Contains("在顶棚下") && roofed.Contains("满载速度：120 件/分钟")
                   && clear.Contains("满载速度：120 件/分钟") && !clear.Contains("减速"),
                "悬停写明天气：露天“满载速度：90 件/分钟（设计 120，露天减速 25%）”，顶棚下“在顶棚下：不受天气影响”，沙暴结束后回到 120");
        }

        private static bool Near(int[] got, params int[] want) => got.Length == want.Length && got.Zip(want, (g, w) => Math.Abs(g - w) <= 1).All(x => x);

        // ── L. 存读档 ────────────────────────────────────────────────────────────

        private static string Snapshot(CampaignState s)
        {
            var sb = new StringBuilder();
            sb.Append("hash=").Append(BeltNetworkService.Kernel.ComputeStateHash()).Append(" scrap=").Append(s.Scrap).Append(" ground=").Append(GroundScrap(s)).Append('\n');
            foreach (BeltPortBindingRecord b in (s.Belts.PortBindings ?? Array.Empty<BeltPortBindingRecord>()).OrderBy(b => b.PortId))
            {
                BeltNetworkService.Kernel.TryGetPortInfo(b.PortId, out BeltPortInfo pi);
                sb.Append(b.PortId).Append('|').Append(b.BuildingId).Append('|').Append(b.PortKey).Append('|').Append(b.Filter).Append('|')
                    .Append(pi.Pending).Append('|').Append(pi.Buffered).Append('|').Append(pi.Total).Append('|').Append(pi.Face).Append('|').Append(pi.Accept).Append('\n');
            }
            foreach (BeltDamageRecord d in (s.Belts.Damage ?? Array.Empty<BeltDamageRecord>()).OrderBy(d => d.X).ThenBy(d => d.Y))
            {
                sb.Append("dmg ").Append(d.X).Append(',').Append(d.Y).Append('=').Append(d.Lost).Append('\n');
            }
            foreach (PlannedBeltRecord p in s.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>())
            {
                sb.Append("plan ").Append(p.PlanId).Append('|').Append(p.Destroyed).Append('|').Append(p.Tier).Append('|').Append(string.Join(",", p.Dirs)).Append('\n');
            }
            sb.Append("discarded=").Append(s.Belts.Discarded).Append(" stored=").Append(s.Belts.ClearedToStorage).Append(" next=").Append(s.Belts.NextPortId);
            return sb.ToString();
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(8001, scrap: 120);
            BuildingRecord wh = ActivateWarehouse(s);
            List<(GridCell cell, BeltDir dir)> path = WarehouseLoop(s, 1, out string failure);
            if (path == null)
            {
                Fail("仓库闭环铺不下：" + failure);
                return;
            }
            BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", BeltItems.ScrapId, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 12 + 7);
            BeltNetworkService.TryDamage(s, path[3].cell, 20, out _);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? rowAt = FindRow(s, new GridCell(core.X - 10, core.Y + 30), 2, 16);
            GridCell g = rowAt ?? new GridCell(core.X - 10, core.Y + 30);
            Lay(s, new List<(GridCell, BeltDir)> { (g, BeltDir.North) }, 2, out _);
            BeltNetworkService.TryDamage(s, g, 999, out _);
            string before = Snapshot(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(GameClock.StepHz * 30);
            string continuous = Snapshot(s);

            string RunFromSave(out string loaded)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                loaded = null;
                if (!rr.Success)
                {
                    return "读档失败：" + rr.Message;
                }
                CampaignSession.Set(Slot, rr.State);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                loaded = Snapshot(rr.State);
                WorldSimulation.StepMany(GameClock.StepHz * 30);
                return Snapshot(rr.State);
            }

            string first = RunFromSave(out string loaded1);
            string second = RunFromSave(out _);
            Expect(save.Success && loaded1 == before,
                $"满载中途真文件存读档：内核（格式 {BeltKernel.FormatVersion}）、端口绑定与过滤、端口待推 / 缓存 / 累计、朝向与收货过滤、耐久表、被摧毁的虚影逐字段往返一致"
                + (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}"));
            Expect(first == continuous && second == first,
                "读档后接着跑 30 游戏秒，与不存档一直跑逐位一致（端口对账与转移都按世界步序号）；同一存档读两次结果一致"
                + (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
        }

        /// <summary>格式 1（FG0-ARCH-02）的端口块：没有朝向与收货过滤（每个端口 63 字节）。读进来朝向 = 任意、收什么 = 任何。</summary>
        private static void CheckLegacyPortFormat()
        {
            using (var k = new BeltKernel(BeltNetworkService.ReadConfig()))
            {
                for (int x = 0; x < 6; x++)
                {
                    k.AddCell(x, 0, BeltDir.East, 0);
                }
                k.AddSource(1, 0, 0, 3, 1, 50);
                k.AddSink(2, 6, 0, 5, 0);
                k.StepMany(800); // T1 走 6 格约 24 游戏秒：让输入口缓存里有货
                BeltSnapshot snap = k.Serialize();
                ulong h = k.ComputeStateHash();
                // 把端口块改写成格式 1：去掉每个端口的朝向（1 字节）与收货过滤（2 字节），版本字节改 1，重算校验和。
                byte[] v2 = snap.Ports;
                var v1 = new List<byte>();
                v1.AddRange(new[] { v2[0], v2[1], (byte)1, v2[3] });
                int n = BitConverter.ToInt32(v2, 4);
                v1.AddRange(BitConverter.GetBytes(n));
                int at = 8;
                for (int p = 0; p < n; p++)
                {
                    // Id(4) Kind(1) X(4) Y(4) Owner(4) | Face(1) | Item(2) | Accept(2) | 其余 44 字节
                    v1.AddRange(v2.Skip(at).Take(17));
                    at += 17 + 1;
                    v1.AddRange(v2.Skip(at).Take(2));
                    at += 2 + 2;
                    v1.AddRange(v2.Skip(at).Take(44));
                    at += 44;
                }
                uint sum = 2166136261u;
                foreach (byte b in v1)
                {
                    sum ^= b;
                    sum *= 16777619u;
                }
                v1.AddRange(BitConverter.GetBytes(sum));
                snap.Ports = v1.ToArray();
                snap.FormatVersion = 1;
                using (var d = new BeltKernel(BeltNetworkService.ReadConfig()))
                {
                    bool ok = d.Deserialize(snap, out string err);
                    d.TryGetPortInfo(1, out BeltPortInfo src);
                    d.TryGetPortInfo(2, out BeltPortInfo sink);
                    k.TryGetPortInfo(1, out BeltPortInfo origSrc);
                    k.TryGetPortInfo(2, out BeltPortInfo origSink);
                    bool fields = src.Face == BeltConst.AnyFace && sink.Accept == BeltConst.AcceptAny && src.Pending == origSrc.Pending && src.Total == origSrc.Total
                                  && sink.Buffered == origSink.Buffered && sink.Buffered > 0 && sink.BufferCap == 5;
                    ulong hd = d.ComputeStateHash();
                    // FG3-LOG-04：格式 3 已是当前格式（网络块多了种类与节点设置）；比当前新的格式才是“不认识”。
                    snap.FormatVersion = BeltKernel.FormatVersion + 1;
                    using (var f = new BeltKernel(BeltNetworkService.ReadConfig()))
                    {
                        bool future = !f.Deserialize(snap, out string futureErr) && futureErr == "format_version";
                        Expect(ok && err == null && fields && src.Connected && sink.Connected && hd == h && future,
                            $"旧格式 1 的端口块（FG0-ARCH-02 存档）照常读：朝向 = 任意、收什么 = 任何，状态哈希与原内核一致；不认识的格式 {BeltKernel.FormatVersion + 1} 整体拒绝（{err ?? "无问题"}）");
                    }
                }
            }
        }

        // ── M. 暂停与倍速 ────────────────────────────────────────────────────────

        private static void CheckTimingMatrix()
        {
            var results = new List<string>();
            string reference = null;
            bool same = true;
            bool pausedOk = true;
            const float frame = 1f / 60f;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewWorld(8101, scrap: 100);
                ActivateWarehouse(s);
                WarehouseLoop(s, 1, out _);
                GameClock.SetPaused(true);
                string p0 = Snapshot(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(frame);
                }
                pausedOk &= Snapshot(s) == p0;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                long target = GameClock.Ticks + GameClock.StepHz * 45;
                int frames = 0;
                while (GameClock.Ticks < target && frames < 60 * 400)
                {
                    WorldSimulation.Frame(frame, target);
                    frames++;
                }
                string snap = Snapshot(s);
                reference ??= snap;
                same &= snap == reference;
                results.Add($"{speed}x：{frames} 帧 / 库存 {s.Scrap}");
                GameClock.SetSpeed(1f);
            }
            Expect(pausedOk && same,
                $"暂停中（120 帧）传送带与端口都不动；0.5x / 1x / 2x / 3x 跑同样的 45 游戏秒，内核哈希、库存、端口累计逐位一致（{string.Join("；", results)}）");
        }

        // ── N. 观察 / 不观察 ──────────────────────────────────────────────────────

        private static void CheckObservedEqualsUnobserved()
        {
            string Run(bool observed)
            {
                CampaignState s = NewWorld(8201, observe: observed, scrap: 90);
                BuildingRecord wh = ActivateWarehouse(s);
                WarehouseLoop(s, 0, out _);
                WorldSimulation.StepMany(GameClock.StepHz * 20);
                BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", BeltPortService.FilterOff, out _);
                WorldSimulation.StepMany(GameClock.StepHz * 20);
                BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", BeltPortService.FilterAll, out _);
                WorldSimulation.StepMany(GameClock.StepHz * 20);
                return Snapshot(s);
            }

            string seen = Run(true);
            string unseen = Run(false);
            Expect(seen == unseen, "同一组操作（仓库闭环跑 20 秒、停止输出 20 秒、恢复 20 秒）在观察与不观察家园时逐字段一致（FGR-BASE-021）"
                                   + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── O. FGT-LOG-006 十个游戏日守恒 ─────────────────────────────────────────────

        private static void CheckConservationTenDays()
        {
            CampaignState s = NewWorld(8301, observe: false, scrap: 100);
            BuildingRecord wh = ActivateWarehouse(s);
            List<(GridCell cell, BeltDir dir)> path = WarehouseLoop(s, 2, out string failure);
            if (path == null)
            {
                Fail("仓库闭环铺不下：" + failure);
                return;
            }
            long total = Conserved(s);
            // 10 个游戏日 = 12,000 游戏秒（1 日 20 分钟）= 720,000 个 60 Hz 世界步。直接驱动物流的世界步（内核 + 端口对账 + 转移），不跑机器与战斗。
            long ticks = GameClock.Ticks;
            int hz = GameClock.StepHz;
            long steps = (long)hz * 12000;
            int checks = 0;
            bool ok = true;
            var sw = Stopwatch.StartNew();
            for (long i = 0; i < steps; i++)
            {
                BeltNetworkService.WorldStep(s, ticks + i, hz);
                if (i == steps / 2)
                {
                    BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", BeltPortService.FilterOff, out _);
                }
                if (i == steps / 2 + hz * 600)
                {
                    BeltPortService.TrySetFilter(s, wh.BuildingId, "warehouse.out0", BeltPortService.FilterAll, out _);
                }
                if (i % 10000 == 0)
                {
                    checks++;
                    ok &= Conserved(s) == total && BeltNetworkService.Kernel.Ledger.Balanced;
                }
            }
            sw.Stop();
            BeltPortInfo o = PortInfo(Port(s, "warehouse", "warehouse.out0"));
            // T3 满载 240 件/分钟 × (200 − 10) 分钟 ≈ 45,600 件。
            Expect(ok && Conserved(s) == total && o.Total >= 44000,
                $"FGT-LOG-006：仓库 → T3 闭环 → 仓库跑 10 个游戏日（{steps:N0} 个世界步，第 5 日关输出 10 分钟再开），{checks} 次核对“库存 + 端口 + 带上” = {total}、账本平衡；" +
                $"累计推出 {o.Total:N0} 件（Editor {sw.Elapsed.TotalSeconds:F1} 秒）");
        }

        // ── P. 正式界面 ──────────────────────────────────────────────────────────

        private static void CheckFormalUi()
        {
            CampaignState s = NewWorld(8401, scrap: 100);
            BuildingRecord wh = ActivateWarehouse(s);
            WarehouseLoop(s, 0, out _);
            WorldSimulation.StepMany(GameClock.StepHz * 10);
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            var reader = new FakeReader();
            InputRouter.DebugSetReader(reader);
            InputRouter.SetScope(InputScope.Strategy);
            VisualElement root = MountUxml(UiKitFolder + "BeltPortPanel.uxml", out GameObject go);
            VisualElement qroot = MountUxml(UiKitFolder + "ConstructionQueuePanel.uxml", out GameObject qgo);
            BeltPortPanelUIToolkit.InWorldOverrideForTests = true;
            ConstructionQueuePanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                BeltPortPanelUIToolkit panel = go.AddComponent<BeltPortPanelUIToolkit>();
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "BeltPortRow.uxml"));
                panel.BindView(root);
                // 建造模式里点一下仓库（不拖动）= 打开端口面板。
                mode.Open();
                GridCell whCell = new GridCell(wh.GridX, wh.GridY);
                mode.PointerDown(s, whCell);
                mode.PointerUp(s, whCell);
                // FG4-ECO-05（FGU-09）：点一下建筑先打开它的通用面板，面板上的“端口…”再打开端口面板（ProductionPanelUIToolkit.OpenPorts 同一调用）。
                bool viaBuildingPanel = ProductionPanelUIToolkit.LastRequestedId == wh.BuildingId;
                ProductionPanelUIToolkit.Close();
                BeltPortPanelUIToolkit.Open(wh.BuildingId);
                panel.Refresh();
                int outRow = Enumerable.Range(0, panel.VisibleRowCount).FirstOrDefault(i => panel.RowPortKey(i) == "warehouse.out0");
                int inRow = Enumerable.Range(0, panel.VisibleRowCount).FirstOrDefault(i => panel.RowPortKey(i) == "warehouse.in0");
                bool opened = viaBuildingPanel && BeltPortPanelUIToolkit.IsOpen && panel.PanelVisible && BeltPortPanelUIToolkit.BuildingId == wh.BuildingId && panel.VisibleRowCount == 2
                              && panel.TitleText.Contains(HomeGridService.DisplayName("warehouse")) && panel.StoreText.Contains("家园仓库")
                              && panel.RowText(outRow, "BpTitle").Contains("输出口") && panel.RowText(outRow, "BpState").Contains("已接上")
                              && panel.RowText(outRow, "BpStats").Contains("已推出") && panel.RowFilterVisible(outRow) && !panel.RowFilterVisible(inRow)
                              && panel.RowText(inRow, "BpAccept").Contains("全部可存物品") && GameSettings.HasSeenGuidanceHook(GuidanceHooks.LogisticsPortPanelFirstOpen)
                              && !GameText.ContainsMarker(panel.TitleText + panel.StoreText + panel.HintText + panel.RowText(outRow, "BpStats"));
                Expect(opened, $"建造模式点一下仓库打开端口面板（真 UXML）：{panel.VisibleRowCount} 行，“{panel.RowText(outRow, "BpTitle")}：{panel.RowText(outRow, "BpState")}｜{panel.RowText(outRow, "BpStats")}”，" +
                               $"输入口“{panel.RowText(inRow, "BpAccept")}”；只有仓库输出口有过滤下拉框");
                DropdownField d = panel.RowFilter(outRow);
                d.value = d.choices[d.choices.Count - 1]; // “停止输出”：选中即生效（下拉框回调 → TrySetFilter）
                BeltPortService.Binding outP = Port(s, "warehouse", "warehouse.out0");
                    int solids = GameLogic.Campaign.Economy.ItemCatalog.Items.Count(it => it.Form == GameLogic.Campaign.Economy.ItemForm.Solid);
                Expect(outP.Record.Filter == BeltPortService.FilterOff && d.choices.Count == solids + 2 && d.choices[0].Contains("全部") && d.choices[1].Contains("废料"),
                    $"过滤下拉框（全部可存物品 / 物品表 {solids} 种固体各一项 / 停止输出，FG4-ECO-01）选中即生效：选“{d.value}”后绑定表里的过滤 = {outP.Record.Filter}");
                bool esc = UiEscapeStack.CloseTop() && !BeltPortPanelUIToolkit.IsOpen;
                GridCell core = HomeGridService.CorePivot(s);
                mode.PointerDown(s, core);
                mode.PointerUp(s, core);
                bool coreViaPanel = ProductionPanelUIToolkit.LastRequestedId == Building(s, "core").BuildingId;
                ProductionPanelUIToolkit.Close();
                BeltPortPanelUIToolkit.Open(Building(s, "core").BuildingId);
                bool coreOpened = coreViaPanel && BeltPortPanelUIToolkit.IsOpen && BeltPortPanelUIToolkit.BuildingId == Building(s, "core").BuildingId;
                BeltPortPanelUIToolkit.Close();
                Expect(esc && coreOpened, "Esc 关闭端口面板；点核心（不能拖动的建筑）同样打开它的面板（FG4-ECO-05 起是通用面板，“端口…”进端口面板）");

                // 施工队列：被摧毁的传送带行 → “重建”按钮。
                ConstructionQueuePanelUIToolkit qpanel = qgo.AddComponent<ConstructionQueuePanelUIToolkit>();
                qpanel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "ConstructionQueueRow.uxml"));
                qpanel.BindView(qroot);
                GridCell? rowAt = FindRow(s, new GridCell(core.X - 8, core.Y + 30), 2, 16);
                GridCell g = rowAt ?? new GridCell(core.X - 8, core.Y + 30);
                Lay(s, new List<(GridCell, BeltDir)> { (g, BeltDir.East), (new GridCell(g.X + 1, g.Y), BeltDir.East) }, 0, out _);
                BeltNetworkService.TryDamage(s, g, 999, out _);
                BeltNetworkService.TryDamage(s, new GridCell(g.X + 1, g.Y), 999, out _);
                ConstructionQueuePanelUIToolkit.Open();
                qpanel.Refresh();
                int destroyedRow = Enumerable.Range(0, qpanel.VisibleRowCount).FirstOrDefault(i => qpanel.RowDestroyedPlanId(i) != null);
                Button rebuild = qpanel.RowButton(destroyedRow, "CqRebuild");
                bool rowOk = qpanel.RowDestroyedPlanId(destroyedRow) != null && qpanel.RowName(destroyedRow).Contains("被摧毁") && rebuild != null
                             && !rebuild.ClassListContains("uk-hidden") && qpanel.RowButton(destroyedRow, "CqUp").ClassListContains("uk-hidden") && qpanel.RebuildAllVisible;
                string planId = qpanel.RowDestroyedPlanId(destroyedRow);
                Click(rebuild);
                bool queued = HomeValleyWorkOrders.FindActiveBuild(s, HomeValleyConstruction.BeltPlanPrefix + planId) != null;
                Click(qpanel.RebuildAllButton);
                Expect(rowOk && queued && HomeValleyConstruction.DestroyedGhostCount(s) == 0 && !qpanel.RebuildAllVisible,
                    $"施工队列列出被摧毁的传送带（“{qpanel.RowName(destroyedRow)}”，没有优先级按钮、有“重建”），点“重建”生成施工单，点“全部重建”安排剩下的");
                ConstructionQueuePanelUIToolkit.Close();

                // 战略视角悬停：光标停在已建成的传送带上 → 悬停挂在传送带上；移开就离开。
                var camGo = new GameObject("__belt_hover_cam");
                try
                {
                    Camera cam = camGo.AddComponent<Camera>();
                    cam.orthographic = true;
                    cam.orthographicSize = 20f;
                    GridCell beltCell = Port(s, "warehouse", "warehouse.in0").BeltCell;
                    cam.transform.position = new Vector3(beltCell.X, 40f, beltCell.Y);
                    cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    mode.Close();
                    var view = new ConstructionSiteView();
                    reader.Mouse = cam.WorldToScreenPoint(new Vector3(beltCell.X, 0f, beltCell.Y));
                    view.TickHover(s, cam, true);
                    bool over = view.HoveringBelt;
                    reader.Mouse = cam.WorldToScreenPoint(new Vector3(beltCell.X + 15f, 0f, beltCell.Y + 15f));
                    view.TickHover(s, cam, true);
                    bool left = !view.HoveringBelt;
                    view.Release();
                    Expect(over && left, "战略视角：光标停在已建成的传送带上 → 世界悬停提示挂在传送带上（内容 = 悬停读数），移到空地就离开");
                }
                finally
                {
                    Object.DestroyImmediate(camGo);
                }

                // 布局探针（端口面板，中英、缩放极值、有行的数据态）。
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") })
                    {
                        string result = UiToolkitLayoutProbe.Probe(UiKitFolder + "BeltPortPanel.uxml", "BeltPortWindow", stressFill: true, prepare: pr =>
                        {
                            var probeGo = new GameObject("__probe_bp") { hideFlags = HideFlags.HideAndDontSave };
                            BeltPortPanelUIToolkit p = probeGo.AddComponent<BeltPortPanelUIToolkit>();
                            p.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "BeltPortRow.uxml"));
                            p.BindView(pr.panel.visualTree);
                            BeltPortPanelUIToolkit.Open(wh.BuildingId);
                            p.Refresh();
                            BeltPortPanelUIToolkit.Close();
                            Object.DestroyImmediate(probeGo);
                            pr.panel.visualTree.Q<VisualElement>("BeltPortRoot")?.RemoveFromClassList("uk-hidden");
                        }, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"布局探针 BeltPortPanel.uxml#BeltPortWindow [{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                    }
                }
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
            finally
            {
                BeltPortPanelUIToolkit.Close();
                ConstructionQueuePanelUIToolkit.Close();
                BeltPortPanelUIToolkit.InWorldOverrideForTests = false;
                ConstructionQueuePanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(qgo);
                mode?.Close();
                InputRouter.DebugSetReader(null);
                UiEscapeStack.Clear();
            }
        }

        // ── Q. 性能（120 帧）──────────────────────────────────────────────────────

        private static void CheckPerformance120()
        {
            CampaignState s = NewWorld(8501, scrap: 150);
            ActivateWarehouse(s);
            List<(GridCell cell, BeltDir dir)> path = WarehouseLoop(s, 2, out string failure);
            GridCell core = HomeGridService.CorePivot(s);
            BeltKernel k = BeltNetworkService.Kernel;
            FgBeltKernelSelfCheck.BuildHuge(k, core.X + 3000, core.Y + 3000);
            WorldSimulation.StepMany(GameClock.StepHz * 10);
            k.EnsureTopology();
            Camera cam = WorldView.Camera;
            int hz = GameClock.StepHz;
            GridCell hoverCell = path != null ? path[path.Count / 2].cell : core;
            var lines = new List<string>();
            bool ok = true;
            var perf = new List<PerfGate.Metric>();
            long tick = GameClock.Ticks;
            // 预热：渲染器、实例缓冲第一次分配（不计入稳态分配）。
            for (int f = 0; f < 120; f++)
            {
                BeltNetworkService.WorldStep(s, tick++, hz);
                BeltNetworkService.Render(cam);
            }
            foreach (float speed in new[] { 1f, 3f })
            {
                const int frames = 1200; // 120 帧 × 10 秒
                var frameMs = new List<double>(frames);
                var stepMs = new List<double>(frames);
                var pumpMs = new List<double>(frames);
                double acc = 0;
                double hoverTotal = 0;
                var sw = new Stopwatch();
                GC.Collect();
                long mem0 = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
                for (int f = 0; f < frames; f++)
                {
                    sw.Restart();
                    acc += speed * hz / 120.0;
                    while (acc >= 1.0)
                    {
                        long before = k.StepIndex;
                        BeltNetworkService.WorldStep(s, tick, hz);
                        tick++;
                        acc -= 1.0;
                        if (k.StepIndex != before)
                        {
                            stepMs.Add(k.LastStepMs);
                            pumpMs.Add(BeltPortService.LastPumpMs);
                        }
                    }
                    BeltNetworkService.Render(cam);
                    sw.Stop();
                    frameMs.Add(sw.Elapsed.TotalMilliseconds);
                }
                long alloc = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - mem0;
                // 悬停读数按 UiTooltip 的 0.1 秒刷新：120 帧里 12 次（测单次耗时）。
                var hw = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++)
                {
                    BeltNetworkService.TryDescribeHover(s, hoverCell, out _, out _);
                }
                hw.Stop();
                hoverTotal = hw.Elapsed.TotalMilliseconds / 200;
                frameMs.Sort();
                stepMs.Sort();
                double avg = frameMs.Average();
                double p50 = frameMs[frames / 2];
                double p95 = frameMs[(int)(frames * 0.95)];
                double p99 = frameMs[(int)(frames * 0.99)];
                double max = frameMs[frames - 1];
                double stepAvg = stepMs.Count > 0 ? stepMs.Average() : 0;
                double stepP95 = stepMs.Count > 0 ? stepMs[(int)(stepMs.Count * 0.95)] : 0;
                double pumpAvg = pumpMs.Count > 0 ? pumpMs.Average() * 1000.0 : 0;
                lines.Add($"{speed}x@120 帧（{frames} 帧，{k.CellCount:N0} 格 / {k.ItemCount:N0} 件，{BeltPortService.Count} 个建筑端口）：物流每帧 平均 {avg:F3} ms、p50 {p50:F3}、p95 {p95:F3}、p99 {p99:F3}、最大 {max:F3} ms；" +
                          $"内核步 {stepMs.Count} 次 平均 {stepAvg:F3} ms / p95 {stepP95:F3} ms；端口转移平均 {pumpAvg:F1} µs；悬停读数 {hoverTotal * 1000.0:F0} µs / 次（0.1 秒一次）；托管堆增量 {alloc} 字节");
                perf.Add(PerfGate.Le(p99, 4.0, $"{speed}x 物流每帧 p99 ms"));
                perf.Add(PerfGate.Le(avg, 1.5, $"{speed}x 物流每帧平均 ms"));
                perf.Add(PerfGate.Le(stepP95, 2.0, $"{speed}x 内核单步 p95 ms"));
                perf.Add(PerfGate.Lt(pumpAvg, 50.0, $"{speed}x 端口转移平均 µs"));
            }
            // 稳态分配单独测：3x 节奏 3,000 帧（每帧 1.5 个世界步 + 一次 Render），托管堆增量按每帧 < 8 字节（与 ADR-ARC-004 同一口径；
            // Unity Mono 不支持按线程分配计数，堆块粒度约 8 KB，单次测量有一个堆块的噪声）。
            GC.Collect();
            long memA = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            double accA = 0;
            for (int f = 0; f < 3000; f++)
            {
                accA += 3.0 * hz / 120.0;
                while (accA >= 1.0)
                {
                    BeltNetworkService.WorldStep(s, tick++, hz);
                    accA -= 1.0;
                }
                BeltNetworkService.Render(cam);
            }
            long steadyAlloc = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - memA;
            lines.Add($"稳态分配（3x 节奏 3,000 帧 = 4,500 个世界步 + 3,000 次 Render）：托管堆增量 {steadyAlloc} 字节");
            ok &= steadyAlloc < 3000 * 8;
            PerfLines.AddRange(lines);
            ExpectPerf(ok && path != null && k.CellCount >= 15000 && k.ItemCount >= 30000,
                "120 帧最低标准（8.33 ms / 帧）下满载传送带的物流开销：15,000 格 / ≥ 30,000 件 + 真实仓库端口，1x 与 3x 逐帧实测 p99 ≤ 4 ms、平均 ≤ 1.5 ms、内核单步 p95 ≤ 2 ms、稳态几乎不分配" +
                "（Editor batchmode 无 GPU；GPU 画面帧时间见 FgBeltPerfProbe）：" + string.Join("；", lines), perf.ToArray());
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgBeltFormalSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
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
            public Vector3 Mouse;

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
            public Vector3 MousePosition => Mouse;
            public float MouseScrollDelta => 0f;
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
