using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.EditorTools.JourneyBots;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using TEngine;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG3-E2E-01 自检 [M3 出口]：里程碑出口的自动化部分里，能在编辑模式下逐语义断言的那些（旅程本身在 Play 里由 tools/unity-journey.sh 跑）。
    ///
    /// A. 旅程登记与正式输入：FGJ-M3 覆盖出口旅程原文每一步（输入种子开新档 / 起始区保证 / 搭线 / 堵塞 / 诊断 / 修好 / 布局库 / 另一处放一份 / 撤销 / 升级 / 存读档 / 远征 / 后台一致）；
    ///    FGJ-M3R 覆盖 IC-REQ-022 六类、每类“出错 → 恢复”两端；两条种子不同；三份旅程源码只走输入通道，不直接调放置 / 施工 / 撤销 / 复制 / 布局库 / 诊断 / 端口过滤 / 出征等业务方法。
    /// B. 在途库存（M3 出口旅程抓到并修掉）：仓库输出口推上传送带的件数 = 带上件数 + 输出口待推件数，与库存减少量守恒；施工等材料时原因写明“另有 N 件在传送带上”
    ///    与办法（N 与读数一致）；没有仓库输出口的带时回到原来的写法；中英两套文本。
    /// C. 缺口清零（里程碑出口第 5 条）：最迟里程碑是 M3 的缺口全部 Closed；首个门禁是 FG-M3 的延后项全部 Closed / 部分关闭 / 顺延；本 Story 的登记在表里；
    ///    DEBT-FG3GEN01-08（地图底图不随改过的格子变）改派 FG4 的前提——生产代码里没有任何改地形 / 污染的调用——自动守护。
    /// D. 试玩包：FG-M3 的试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板齐全，写着 FGR-BAL-061 的 FG-M3 标准与 FG03 / FG17 第 9 节的问题；README 登记“待用户试玩”。
    /// E. 需求覆盖（IC-REQ-020）：FG03 FGT-LOG-001～013、FG17 FGT-GEN-001～010 每一项都有自检段 / 旅程，且那一段登记在全量自检（CellFrameworkValidate）或旅程登记表里。
    /// F. 建造栏分层（FGJ-M1 / FGJ-M2 回归抓到并修掉）：建造模式关着时入口与快捷栏排在所有窗口之下，开着时回到建造栏层；
    ///    FGJ-M3 在 HUD 层真实点过快捷栏格子与入口按钮（第 1 轮审查补）。
    /// </summary>
    public static class FgMilestoneM3SelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private const int Slot = 7;

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line("\n[M3 出口] FG3-E2E-01：旅程登记与正式输入、在途库存提示、缺口清零、试玩包、需求覆盖、建造栏分层");
            Step(CheckJourneyCatalog);
            Step(CheckJourneySourcesUseInput);
            Step(CheckStoreItemsOnBelts);
            Step(CheckGapGate);
            Step(CheckTerrainWritersGuard);
            Step(CheckPlaytestPack);
            Step(CheckRequirementCoverage);
            Step(CheckBuildHudLayer);
            Line($"  · [M3 出口] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

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
        }

        // ── A. 旅程登记 ────────────────────────────────────────────────────────────

        private static bool Wellformed(JourneyDef d) => d != null && d.Steps.Count > 0 && d.Steps.Select(x => x.Id).Distinct().Count() == d.Steps.Count
                                                        && d.Steps.All(x => x.TimeoutSeconds > 0 && x.Tick != null) && d.TotalTimeoutSeconds > 0;

        private static void CheckJourneyCatalog()
        {
            Line("  · A. 旅程登记：FGJ-M3 覆盖出口旅程原文每一步；FGJ-M3R 覆盖 IC-REQ-022 六类（每类出错 → 恢复两端）；种子不同");
            JourneyDef m3 = JourneyCatalog.Create(FgjM3Journey.Id);
            JourneyDef m3r = JourneyCatalog.Create(FgjM3ReverseJourney.Id);
            var exit = new (string Step, string[] Ids)[]
            {
                ("用输入的种子开新档", new[] { "new_game", "seed" }), ("确认起始区保证", new[] { "seed", "plan", "pump_place" }),
                ("搭建产线（M3 用物品线 + 抽水线代替，ADR-QA-019）", new[] { "pipe_lay", "tank_place", "belt_lay", "built", "rate_t1" }),
                ("人为制造一次堵塞", new[] { "jam", "jam_stall" }), ("用诊断找到根源", new[] { "diag_open", "diag_click" }), ("修好", new[] { "fix", "flow_back" }),
                ("把整条产线存进布局库", new[] { "copy_box", "lib_save" }), ("在另一处放一份", new[] { "site2", "lib_place", "paste_aim", "paste_click", "copy_built" }),
                ("撤销", new[] { "undo", "redo" }), ("升级传送带", new[] { "up_boxes", "up_wait", "rate_t2" }), ("存读档", new[] { "save_quit", "menu_back", "loaded" }),
                ("远征一次", new[] { "prep_depart", "away", "evac_confirm" }), ("回来确认产量与后台一致", new[] { "bg_check" }),
            };
            List<string> missing = exit.Where(x => m3 == null || x.Ids.Any(id => m3.Steps.All(s => s.Id != id))).Select(x => x.Step).ToList();
            Expect(Wellformed(m3) && missing.Count == 0 && m3.Seed == FgjM3Journey.TestSeed,
                $"FGJ-M3：{m3?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m3?.Seed}；出口旅程各步都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            var reverse = new (string Kind, string[] Ids)[]
            {
                ("资源不足", new[] { "r1_drag", "r1_wait", "r1_queue", "r1_salvage", "r1_done" }),
                ("路径失败", new[] { "r2_block", "r2_detour", "r2_bad", "r2_ok" }),
                ("暂停", new[] { "r3_pump", "r3_frozen", "r3_resume", "r3_half", "r3_triple", "r3_pause2" }),
                ("目标死亡", new[] { "r4_carry", "r4_cancel", "r4_refund" }),
                ("断网 / 失联", new[] { "r6_moved", "r6_diag", "r6_go", "r6_powered", "r6_ask", "r6_cancel", "r6_cut", "r6_undo", "r6_back" }),
                ("存读档", new[] { "r5_mid", "r5_menu", "r5_loaded", "r5_done", "r5_undo" }),
            };
            List<string> missingR = reverse.Where(x => m3r == null || x.Ids.Any(id => m3r.Steps.All(s => s.Id != id))).Select(x => x.Kind).ToList();
            Expect(Wellformed(m3r) && missingR.Count == 0 && m3r.Seed != m3.Seed,
                $"FGJ-M3R：{m3r?.Steps.Count} 步；六类反向场景齐全{(missingR.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missingR))}；种子 {m3r?.Seed} ≠ {m3?.Seed}（B25）");
            Expect(JourneyCatalog.Ids.Contains(FgjM3Journey.Id) && JourneyCatalog.Ids.Contains(FgjM3ReverseJourney.Id) && JourneyCatalog.Ids.Contains(FgjM2Journey.Id),
                "两条 M3 旅程登记在旅程登记表（bash tools/unity-journey.sh FGJ-M3 / FGJ-M3R 可跑；菜单 BinGames/旅程机器人），前序里程碑的旅程仍在");
        }

        /// <summary>业务方法（放置 / 施工 / 撤销 / 复制 / 布局库 / 诊断定位 / 端口过滤 / 出征 / 存档 / 改地形）在旅程源码里出现 = 绕开了玩家输入。</summary>
        private static readonly string[] ForbiddenCalls =
        {
            "PlanHistory.Undo(", "PlanHistory.Redo(", "PlanHistory.Paste(", "PlanHistory.Upgrade(", "PlanHistory.ReverseBelt(", "PlanHistory.Place(",
            "PlanHistory.ToggleDemolish(", "PlanHistory.Relocate(", "PlanHistory.PlaceBeltPath(", "PlanHistory.ExecuteDemolishBox(", "PlanHistory.RemoveCells(",
            "HomeGridService.TryPlace", "HomeGridService.TryRemove", "BeltNetworkService.TryPlace", "BeltNetworkService.TrySet", "BeltNetworkService.TryRemove",
            "PipeNetworkService.TryPlace", "PipeNetworkService.TryRemove", "PipeNetworkService.TrySet", "BeltPortService.TrySetFilter", ".SetFilter(",
            "LayoutLibrary.TrySave", "SaveClipboardToLibrary", ".StartPaste(", "RootCauseDiagnosis.Locate", ".LocateStep(", ".ClickStep(",
            "HomeValleyWorkOrders.TryAssign", "HomeValleyWorkOrders.TryCreate", "HomeValleyWorkOrders.YieldToPool", "ExpeditionDepartureService.TryDepart",
            "TryToggleShutdown", "CampaignSaveService.Save(", "UiConfirmDialog.Confirm(", "UiConfirmDialog.Cancel(", ".SetTerrain(", ".SetPollution(",
            "Mode.Select(", "RotateHovered(", "ReverseHoveredBelt(", ".RotateGhost(", ".SetDemolishMode(", ".SetRelocateMode(", "HomeValleyConstruction.CancelSite(",
        };

        private static void CheckJourneySourcesUseInput()
        {
            Line("  · A2. 三份旅程源码只走输入通道（键鼠后端 / UI Toolkit 指针事件 / uGUI EventSystem），不直接调业务方法");
            string root = RepoRoot();
            string[] files = { "FgjM3Common.cs", "FgjM3Journey.cs", "FgjM3ReverseJourney.cs" };
            var hits = new List<string>();
            var src = new StringBuilder();
            foreach (string f in files)
            {
                string text = ReadRepo("TEngine/UnityProject/Assets/Editor/JourneyBots/" + f);
                if (text == null)
                {
                    Fail("找不到旅程源码 " + f);
                    return;
                }
                src.Append(text);
                string code = StripComments(text);
                hits.AddRange(ForbiddenCalls.Where(code.Contains).Select(x => f + ":" + x));
            }
            string all = src.ToString();
            Expect(root != null && hits.Count == 0, $"旅程源码里没有业务方法调用（扫描 {ForbiddenCalls.Length} 种）{(hits.Count == 0 ? string.Empty : "；发现：" + string.Join("、", hits))}");
            string[] inputs =
            {
                "GameActionId.OpenBuildMenu", "GameActionId.Rotate", "GameActionId.Copy", "GameActionId.LayoutLibrary", "GameActionId.Undo", "GameActionId.Redo",
                "GameActionId.UpgradePlan", "GameActionId.OpenDiagnosis", "GameActionId.ConstructionQueue", "GameActionId.DemolishMode", "GameActionId.RelocateMode",
                "GameActionId.TogglePause", "GameActionId.SpeedHalf", "GameActionId.SpeedTriple", "JourneyInput.Drag(", "JourneyInput.ClickUitk(", "JourneyInput.ClickElement(",
                "JourneyInput.ScrollIntoView(", "JourneyInput.UiCoverAt(", "\"NewGameSeed\"", "\"LlPlace\"", "\"LayoutLibrarySave\"", "\"BeltPortClose\"", "\"ConfirmCancel\"", "\"ConfirmOk\"",
            };
            List<string> missing = inputs.Where(x => !all.Contains(x)).ToList();
            bool rts = Regex.IsMatch(all, @"TryClick\([^;]*,\s*1\)") && all.Contains("ClickMachine(");
            Expect(missing.Count == 0 && rts,
                $"旅程用到的正式输入：建造 / 旋转 / 复制 / 布局库 / 撤销 / 重做 / 升级 / 诊断 / 施工队列 / 拆除 / 搬迁 / 暂停 / 倍速键、拖拽、UI Toolkit 点击与滚轮、点地面前查界面遮挡；" +
                $"RTS：左键选机器、右键地面 / 工作目标{(missing.Count == 0 ? string.Empty : "；缺：" + string.Join("、", missing))}");
        }

        private static string StripComments(string code)
        {
            var sb = new StringBuilder(code.Length);
            foreach (string raw in code.Split('\n'))
            {
                string l = raw;
                int i = l.IndexOf("//", StringComparison.Ordinal);
                if (i >= 0 && !l.Substring(0, i).Contains("\"")) // 字符串里的 // 不算注释（本扫描只关心调用，保守处理）
                {
                    l = l.Substring(0, i);
                }
                sb.Append(l).Append('\n');
            }
            return sb.ToString();
        }

        // ── B. 在途库存 ────────────────────────────────────────────────────────────

        private static void CheckStoreItemsOnBelts()
        {
            Line("  · B. 在途库存：仓库输出口推上带的件数与库存减少量守恒；施工等材料时写明“另有 N 件在传送带上”与办法");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            bool hadCamera = Camera.main != null;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string dir = Path.Combine(Path.GetTempPath(), "bingames-m3exit-" + Guid.NewGuid().ToString("N"));
            string oldDir = CampaignSaveService.SaveDirectoryOverrideForTests;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Directory.CreateDirectory(dir);
                CampaignSaveService.SaveDirectoryOverrideForTests = dir;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();

                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                GameClock.SetSpeed(1f);
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                HomeGridService.Invalidate();
                HomeValleyWorkOrders.ResetSessionState();
                HomeValleyPowerGrid.ResetForTests();
                NotificationCenter.ResetForTests();
                CampaignState s = CampaignState.CreateNew("m3exit", "Standard", 90303);
                CampaignSession.Set(Slot, s);
                NotificationCenter.Bind(s);
                WorldSimulation.LoadHome(resume: false);
                s.Scrap = 60;
                BuildingRecord wh = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse && b.RegionId == HomeValleyLayout.RegionId);
                wh.ConstructionState = BuildingConstructionState.Operational; // 夹具：开局受损的仓库直接当作已修好（修复由 [虚影施工] 自检覆盖）
                HomeValleyPowerGrid.Recompute(s);
                WorldSimulation.StepMany(BeltPortService.SyncTicks(GameClock.StepHz) + 1);

                // 没有接带：施工等材料时是原来的写法。
                BeltPortService.Binding outP = BeltPortService.Find(wh.BuildingId, "warehouse.out0");
                Expect(outP != null && BeltPortService.StoreItemsOnBelts(s) == 0, $"仓库输出口已登记（{outP?.BeltCell}），还没接传送带：在途 {BeltPortService.StoreItemsOnBelts(s)} 件");

                // 仓库输出口外侧接一段 8 格的死路传送带（按输出口朝向往外铺）：仓库把库存往外推，推满为止。
                var path = new List<(GridCell cell, BeltDir dir)>();
                int face = (int)outP.Face;
                for (int i = 0; i < 8; i++)
                {
                    path.Add((new GridCell(outP.BeltCell.X + BeltDirs.Dx(face) * i, outP.BeltCell.Y + BeltDirs.Dy(face) * i), (BeltDir)face));
                }
                bool laid = FgBeltFormalSelfCheck.Lay(s, path, 0, out string layFail);
                WorldSimulation.StepMany(GameClock.StepHz * 40);
                int onBelts = BeltPortService.StoreItemsOnBelts(s);
                long kernelItems = BeltNetworkService.Kernel.CountItemsSlow();
                BeltNetworkService.Kernel.TryGetPortCounts(outP.PortId, out int pending, out _);
                Expect(laid && onBelts > 0 && onBelts == kernelItems + Math.Max(0, pending) && s.Scrap + onBelts == 60,
                    $"接上 8 格死路传送带、跑 40 游戏秒：在途 {onBelts} 件（带上 {kernelItems} + 输出口待推 {pending}），库存 60 → {s.Scrap}（库存 + 在途 = 60，守恒）{(laid ? string.Empty : "；铺带失败：" + layFail)}");

                // 这时放一个库存不够的施工：施工单等材料，原因写明在途件数与办法；件数与读数一致。
                GridCell core = HomeGridService.CorePivot(s);
                GridCell? far = null;
                for (int r = 10; r <= 30 && far == null; r++)
                {
                    for (int a = 0; a < 16 && far == null; a++)
                    {
                        double ang = a * Math.PI / 8;
                        var p = new GridCell(core.X + (int)Math.Round(Math.Cos(ang) * r), core.Y + (int)Math.Round(Math.Sin(ang) * r));
                        if (Enumerable.Range(0, 20).All(i => HomeGridService.ValidateBeltCell(s, new GridCell(p.X + i, p.Y)).Ok && HomeGridService.ValidateBeltCell(s, new GridCell(p.X + i, p.Y + 1)).Ok))
                        {
                            far = p;
                        }
                    }
                }
                GridOpResult placed = far.HasValue
                    ? HomeGridService.TryPlaceBeltPath(s, "belt_t3", far.Value, new GridCell(far.Value.X + 19, far.Value.Y), BeltDir.East)
                    : GridOpResult.Fail(GridReason.Of(GridBlockReason.NoBuilding));
                string planId = HomeGridService.LastPlanId;
                WorkOrderRecord order = null;
                for (int i = 0; i < 240 && (order == null || order.State != WorkOrderState.Waiting); i++)
                {
                    WorldSimulation.StepMany(GameClock.StepHz / 2);
                    order = HomeValleyWorkOrders.FindActiveBuild(s, HomeValleyConstruction.BeltPlanPrefix + planId);
                }
                string status = HomeValleyConstruction.DescribeStatus(s, order);
                int n = BeltPortService.StoreItemsOnBelts(s);
                Expect(placed.Success && order != null && order.State == WorkOrderState.Waiting && status.Contains($"另有 {n} 件在传送带上") && status.Contains("停止输出") && status.Contains("端口面板")
                       && !GameText.ContainsMarker(status),
                    $"库存不够的 20 格 T3 传送带：施工单等材料，原因“{status}”（在途读数 {n}）");
                GameSettings.SetLanguage(GameLanguage.En);
                string en = HomeValleyConstruction.DescribeStatus(s, order);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Expect(en.Contains($"{n} more are on belts") && en.Contains("Stop output") && !GameText.ContainsMarker(en), $"英文同一句：“{en}”");
                // 施工队列（Alt+B）一次刷新只算一次在途件数（第 1 轮审查：此前每张施工单各算一次）；队列里这一行与单独描述同一句。
                var queue = new List<HomeValleyConstruction.QueueEntry>();
                HomeValleyConstruction.CollectQueue(s, queue);
                HomeValleyConstruction.QueueEntry row = queue.FirstOrDefault(e => e.Order == order);
                Expect(row.Order == order && row.Status == status && queue.Count >= 1,
                    $"施工队列 {queue.Count} 行，这张施工单那一行“{row.Status}”与单独描述同一句（在途件数一次刷新共用一次结果）");

                // 拆掉这段死路传送带：带上的物品全额退回仓库（放不下的落地），不再在途；施工单拿到料就接着干，原因里不再提传送带。
                int before = s.Scrap;
                HomeGridService.TryRemoveBelts(s, path.Select(x => x.cell).ToList());
                WorldSimulation.StepMany(GameClock.StepHz);
                string plain = HomeValleyConstruction.DescribeStatus(s, order);
                Expect(BeltPortService.StoreItemsOnBelts(s) == 0 && !plain.Contains("在传送带上") && (order.State == WorkOrderState.Waiting ? plain.Contains("等待材料") : true),
                    $"拆掉那段传送带后：在途 {BeltPortService.StoreItemsOnBelts(s)} 件（{n} 件退回，库存 {before} → {s.Scrap}），施工单“{plain}”不再提传送带");
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
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = oldDir;
                MachineRegistry.ResetForNewCampaign();
                MachineLoadoutRegistry.Clear();
                HomeValleyWorkOrders.ResetSessionState();
                HomeValleyPowerGrid.ResetForTests();
                UI.Kit.UiConfirmDialog.DiscardAll();
                if (!hadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
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
        }

        // ── C. 缺口清零 ────────────────────────────────────────────────────────────

        private static readonly Regex FirstMilestone = new Regex(@"M(\d+)");

        private static int FirstMilestoneOf(string s)
        {
            Match m = FirstMilestone.Match(s ?? string.Empty);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        private static void CheckGapGate()
        {
            Line("  · C. 里程碑出口第 5 条：属于 FG-M3 的缺口全部 Closed、延后项全部 Closed / 部分关闭 / 顺延（按门禁列的第一个里程碑判定）");
            string text = ReadRepo("production/design/full-game/FG-GAP-REGISTER.md");
            if (text == null)
            {
                Fail("找不到 production/design/full-game/FG-GAP-REGISTER.md（仓库根定位失败）");
                return;
            }
            var openGaps = new List<string>();
            var openDebts = new List<string>();
            int gapRows = 0, debtRows = 0, m3Rows = 0;
            var status = new Dictionary<string, string>(StringComparer.Ordinal);
            var gate = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                if (!raw.StartsWith("| FG-GAP-", StringComparison.Ordinal) && !raw.StartsWith("| DEBT-", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] cols = raw.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                string st = cols[cols.Length - 1];
                status[cols[0]] = st;
                if (raw.StartsWith("| FG-GAP-", StringComparison.Ordinal))
                {
                    gapRows++;
                    if (cols.Length >= 7 && FirstMilestoneOf(cols[5]) == 3)
                    {
                        m3Rows++;
                        gate[cols[0]] = cols[5];
                        if (!st.StartsWith("Closed", StringComparison.Ordinal))
                        {
                            openGaps.Add(cols[0]);
                        }
                    }
                    continue;
                }
                debtRows++;
                if (cols.Length < 9)
                {
                    continue;
                }
                gate[cols[0]] = cols[6];
                if (FirstMilestoneOf(cols[6]) != 3)
                {
                    continue;
                }
                m3Rows++;
                bool ok = st.StartsWith("Closed", StringComparison.Ordinal) || st.StartsWith("部分关闭", StringComparison.Ordinal) || st.StartsWith("顺延", StringComparison.Ordinal);
                if (!ok)
                {
                    openDebts.Add(cols[0]);
                }
            }
            Expect(gapRows > 80 && debtRows > 200 && m3Rows >= 30, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中属于 FG-M3 的 {m3Rows} 行（解析没有漏行）");
            Expect(openGaps.Count == 0 && openDebts.Count == 0,
                openGaps.Count + openDebts.Count == 0
                    ? "属于 FG-M3 的缺口全部 Closed，延后项全部 Closed / 部分关闭（剩余部分另有门禁）/ 顺延（占位美术：2026-09-30 用户已同意）"
                    : $"FG-M3 出口前还开着：缺口 [{string.Join("、", openGaps)}]，延后项 [{string.Join("、", openDebts)}]");
            string[] mine = { "DEBT-FG3E2E01-01", "DEBT-FG3E2E01-02", "DEBT-FG3E2E01-03", "FG-GAP-090" };
            bool registered = mine.All(status.ContainsKey);
            bool closedHere = new[] { "DEBT-FG0QA01-02", "DEBT-FG3LOG02-10", "DEBT-FG3LOG04-05" }.All(id => status.TryGetValue(id, out string v) && v.StartsWith("Closed", StringComparison.Ordinal));
            bool pending = status.TryGetValue("DEBT-FG3E2E01-02", out string p2) && p2.Contains("待用户");
            Expect(registered && closedHere && pending,
                "本 Story 的登记在表里（DEBT-FG3E2E01-01 原文产线 → FGJ-M4、-02 真人试玩待用户、-03 离家报告 → FG4-ECO-09；FG-GAP-090 在途库存 → FG4-ECO-01）；" +
                "DEBT-FG0QA01-02（施工完成后再拆）、DEBT-FG3LOG02-10（右键虚影“去建这个”）、DEBT-FG3LOG04-05（分流器贴合流器侧面）由本 Story 关闭；真人试玩写“待用户”");
        }

        /// <summary>
        /// DEBT-FG3GEN01-08 改派 FG4 的前提：战略地图底图不读区块差异，但 M3 的生产代码里没有任何改地形 / 污染的调用（拆废墟在 FG4-ECO-02、净化在 FG7），
        /// 玩家在 M3 改不了地形，底图不会过期。FG4 起一旦有生产代码调 SetTerrain / SetPollution，这条就失败，提醒承接 Story 把底图同步做完。
        /// </summary>
        private static void CheckTerrainWritersGuard()
        {
            Line("  · C2. DEBT-FG3GEN01-08 改派守护：生产代码里没有改地形 / 污染的调用（只有自检与旅程夹具在改）");
            string root = RepoRoot();
            if (root == null)
            {
                Fail("仓库根定位失败");
                return;
            }
            string scripts = Path.Combine(root, "TEngine", "UnityProject", "Assets", "GameScripts");
            var writers = new List<string>();
            foreach (string f in Directory.GetFiles(scripts, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(f) == "HomeGridMap.cs")
                {
                    continue;
                }
                string code = StripComments(File.ReadAllText(f, Encoding.UTF8));
                if (code.Contains(".SetTerrain(") || code.Contains(".SetPollution("))
                {
                    writers.Add(Path.GetFileName(f));
                }
            }
            string reg = ReadRepo("production/design/full-game/FG-GAP-REGISTER.md") ?? string.Empty;
            string row = reg.Split('\n').FirstOrDefault(l => l.StartsWith("| DEBT-FG3GEN01-08 ", StringComparison.Ordinal)) ?? string.Empty;
            Expect(writers.Count == 0 && row.Contains("FG4-ECO-02") && row.Contains("FG-M4"),
                writers.Count == 0
                    ? "GameScripts 下没有生产代码调 SetTerrain / SetPollution：M3 里玩家改不了地形，地图底图不会与近景不一致；DEBT-FG3GEN01-08 承接 FG4-ECO-02、门禁 FG-M4"
                    : $"生产代码开始改地形了（{string.Join("、", writers)}）：战略地图底图要随区块差异重画（DEBT-FG3GEN01-08）");
        }

        // ── F. 建造栏分层（FGJ-M1 / FGJ-M2 回归抓到）────────────────────────────────

        /// <summary>
        /// FG3-LOG-01 起底部快捷栏在家园战略视角下常驻，而整份建造栏文档排在 30030（浮动窗口层 33～30000 之上）：蓝图编辑器（8）的“保存”
        /// 落在快捷栏位置时被盖住、点不到（FGJ-M1 cb_save / FGJ-M2 cb_save 失败）。建造模式关着时文档降到 HUD 层（叠加层 HUD 4 之上、
        /// 最低的窗口层 6 之下），开着时回到 30030。行为由 FGJ-M1 / FGJ-M2 的真实点击覆盖；这里守住分层规则本身。
        /// </summary>
        private static void CheckBuildHudLayer()
        {
            Line("  · F. 建造栏分层：建造模式关着时入口与快捷栏排在所有窗口之下，开着时回到建造栏层");
            int closed = GameLogic.UI.Kit.BuildModeHudUIToolkit.LayerFor(false);
            int open = GameLogic.UI.Kit.BuildModeHudUIToolkit.LayerFor(true);
            const int lowestWindowLayer = 6; // 家园装配站生产面板 6、蓝图编辑器 / 远征准备 8、浮动窗口 33～30000（UI_WORKFLOW_GUIDE 第 4 节）
            Expect(closed > GameLogic.UI.Kit.OverlayHudUIToolkit.Order && closed < lowestWindowLayer,
                $"建造模式关着：建造栏文档分层 {closed}（叠加层 HUD {GameLogic.UI.Kit.OverlayHudUIToolkit.Order} 之上、最低窗口层 {lowestWindowLayer} 之下）——快捷栏不会盖住窗口里的按钮");
            Expect(open == GameLogic.UI.Kit.BuildModeHudUIToolkit.Order,
                $"建造模式开着：建造栏文档分层 {open}（= 建造栏层 {GameLogic.UI.Kit.BuildModeHudUIToolkit.Order}，浮动窗口之上、信号核 30035 之下）");
            // 第 1 轮审查：降到 HUD 层之后，快捷栏与入口按钮在这一层还点得到（没被同一面板上更高层的常驻界面挡住）——由 FGJ-M3 的真实点击覆盖：
            // hb_closed（建造模式关着时点快捷栏格子 → 打开并选中）、jam_build（点入口按钮 → 打开、手上不拿条目），两步都走 JourneyInput.ClickUitk（面板拾取 + 更高面板核对）。
            JourneyDef m3 = JourneyCatalog.Create(FgjM3Journey.Id);
            string src = ReadRepo("TEngine/UnityProject/Assets/Editor/JourneyBots/FgjM3Journey.cs") ?? string.Empty;
            string[] ids = { "hb_put", "hb_closed", "hb_close", "jam_build" };
            List<string> missing = ids.Where(id => m3 == null || m3.Steps.All(x => x.Id != id)).ToList();
            bool real = src.Contains("JourneyInput.ClickUitk(FgjM3Common.BuildHost, \"HotbarSlot\"") && src.Contains("JourneyInput.ClickUitk(FgjM3Common.BuildHost, \"BuildEntryButton\")")
                        && src.Contains("BuildModeHudUIToolkit.ClosedHudOrder");
            Expect(missing.Count == 0 && real,
                $"FGJ-M3 在建造模式关着（HUD 层）时真实点快捷栏格子与入口按钮：步骤 {string.Join(" / ", ids)}{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}；走真实指针事件并核对分层 {real}");
        }

        // ── D. 试玩包 ──────────────────────────────────────────────────────────────

        private static void CheckPlaytestPack()
        {
            Line("  · D. 试玩包：FGR-BAL-060 / 061 的 FG-M3 材料齐全（真人试玩由用户择时进行，不阻塞）");
            const string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M3-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M3-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M3-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M3-问题清单模板.md");
            string readme = ReadRepo(dir + "README.md");
            bool exist = script != null && table != null && form != null && issues != null && readme != null;
            Expect(exist, "四份材料（试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板）与目录说明都在");
            if (!exist)
            {
                return;
            }
            bool criterion = table.Contains("30 秒") && table.Contains("80%") && table.Contains("FGR-BAL-061") && table.Contains("停工事件");
            bool drills = script.Contains("D1") && script.Contains("D2") && script.Contains("D3") && script.Contains("固定台词") && script.Contains("不能说的");
            bool questions = new[] { "FG03 §9 Q1", "FG03 §9 Q2", "FG03 §9 Q3", "FG17 §9 Q1", "FG17 §9 Q2", "FG17 §9 Q3" }.All(q => form.Contains(q) || table.Contains(q));
            bool curve = script.Contains("DEBT-FG3LOG06-10") && form.Contains("DEBT-FG3LOG06-10");
            bool registered = readme.Contains("FG-M3") && readme.Contains("DEBT-FG3E2E01-02") && readme.Contains("不阻塞");
            Expect(criterion && drills && questions && curve && registered,
                $"通过标准写着“≥ 80% 在 30 秒内找到原因”并拆成可计数的停工事件（{criterion}）；脚本有三种停工演练、固定台词与不能说的话（{drills}）；" +
                $"问卷覆盖 FG03 / FG17 第 9 节六个问题（{questions}）；电网曲线看一眼并入观察点（{curve}）；目录登记“待用户试玩、不阻塞”（{registered}）");
        }

        // ── E. 需求覆盖 ────────────────────────────────────────────────────────────

        private static void CheckRequirementCoverage()
        {
            Line("  · E. 需求覆盖（IC-REQ-020）：FGT-LOG-001～013、FGT-GEN-001～010 各有自检段或旅程，且都登记在全量自检 / 旅程登记表里");
            string validate = ReadRepo("TEngine/UnityProject/Assets/Editor/CellFrameworkValidate.cs") ?? string.Empty;
            string qa = "TEngine/UnityProject/Assets/Editor/QA/";
            var map = new (string Test, string File, string Registered)[]
            {
                ("FGT-LOG-001", "FgBuildFormalSelfCheck.cs", "FgBuildFormalSelfCheck.Run(Report)"), ("FGT-LOG-002", "FgConstructionSelfCheck.cs", "FgConstructionSelfCheck.Run(Report)"),
                ("FGT-LOG-003", "FgConstructionSelfCheck.cs", "FgConstructionSelfCheck.Run(Report)"), ("FGT-LOG-004", "FgPlanningToolsSelfCheck.cs", "FgPlanningToolsSelfCheck.Run(Report)"),
                ("FGT-LOG-005", "FgBeltNodeSelfCheck.cs", "FgBeltNodeSelfCheck.Run(Report)"), ("FGT-LOG-006", "FgBeltFormalSelfCheck.cs", "FgBeltFormalSelfCheck.Run(Report)"),
                ("FGT-LOG-007", "FgPipeSelfCheck.cs", "FgPipeSelfCheck.Run(Report)"), ("FGT-LOG-008", "FgPowerGridSelfCheck.cs", "FgPowerGridSelfCheck.Run(Report)"),
                ("FGT-LOG-009", "FgDiagnosisSelfCheck.cs", "FgDiagnosisSelfCheck.Run(Report)"), ("FGT-LOG-010", "FgLogisticsGateSelfCheck.cs", "FgLogisticsGateSelfCheck.Run(Report)"),
                ("FGT-LOG-011", "FgLogisticsGateSelfCheck.Day.cs", "FgLogisticsGateSelfCheck.Run(Report)"), ("FGT-LOG-012", "FgLogisticsGateSelfCheck.Gates.cs", "FgLogisticsGateSelfCheck.Run(Report)"),
                ("FGT-LOG-013", "FgPlanningToolsSelfCheck.cs", "FgPlanningToolsSelfCheck.Run(Report)"),
                ("FGT-GEN-001", "FgWorldGenSelfCheck.cs", "FgWorldGenSelfCheck.Run(Report)"), ("FGT-GEN-002", "FgWorldGenSelfCheck.cs", "FgWorldGenSelfCheck.Run(Report)"),
                ("FGT-GEN-003", "FgWorldGenHomeSelfCheck.cs", "FgWorldGenHomeSelfCheck.Run(Report)"), ("FGT-GEN-004", "FgWorldGenHomeSelfCheck.cs", "FgWorldGenHomeSelfCheck.Run(Report)"),
                ("FGT-GEN-005", "FgWorldGenSelfCheck.cs", "FgWorldGenSelfCheck.Run(Report)"), ("FGT-GEN-006", "FgWorldGenSelfCheck.cs", "FgWorldGenSelfCheck.Run(Report)"),
                ("FGT-GEN-007", "FgWorldGenSelfCheck.cs", "FgWorldGenSelfCheck.Run(Report)"), ("FGT-GEN-008", "FgWorldGenHomeSelfCheck.cs", "FgWorldGenHomeSelfCheck.Run(Report)"),
                ("FGT-GEN-010", "FgNavSelfCheck.cs", "FgNavSelfCheck.Run(Report)"),
            };
            var bad = new List<string>();
            foreach ((string test, string file, string registered) in map)
            {
                string src = ReadRepo(qa + file);
                if (src == null || !src.Contains(test) || !validate.Contains(registered))
                {
                    bad.Add($"{test}（{file}）");
                }
            }
            bool gen9 = JourneyCatalog.Ids.Contains(FgjGenExtremeJourney.Id);
            bool m3 = validate.Contains("FgMilestoneM3SelfCheck.Run(Report)");
            Expect(bad.Count == 0 && gen9 && m3,
                $"{map.Length} 项自动验收各有自检段且登记在全量自检；FGT-GEN-009 是旅程 {FgjGenExtremeJourney.Id}（登记 {gen9}）；[M3 出口] 本身登记在全量自检（{m3}）" +
                (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
        }

        // ── 工具 ────────────────────────────────────────────────────────────────

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(Application.dataPath);
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "production", "design", "full-game")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "TEngine", "UnityProject", "DesignDocs")))
                {
                    return dir.FullName;
                }
            }
            return null;
        }

        private static string ReadRepo(string relative)
        {
            string root = RepoRoot();
            string path = root == null ? null : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            return path != null && File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") : null;
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

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
            Debug.LogWarning("[M3 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
