using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.UI.Analysis;
using GameLogic.UI.CircuitBoard;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG2-E2E-01：FGJ-M2 / FGJ-M2R 共用的步骤（全部走正式输入：世界层键鼠经 InputRouter、UI Toolkit 控件派发真实指针事件、下拉 = 设值）。
    /// - 解析台“数据复原”（FG-GAP-050 的临时来源）：点解析台、下拉选固件、点“复原”、确认框点“复原 / 取消”。
    /// - 信号核刻印、固件库筛选与选行、蓝图固件槽、装配站生产。
    /// - 预备区（低威胁靶前 16 格，超出训练靶 12 格的自动交战距离，不让机器在玩家下令前自己开火）、框选编队、右键敌人攻击（RTS 口径）。
    /// - 反应核对：首次触发记录 / 慢放 / 镜头轻推 / 弹字 / 图鉴解锁 / 日志；远征场次的伤害归因；统计面板。
    /// </summary>
    internal static class FgjM2Common
    {
        public const string Conduct = "reaction_conduct";
        public const string FwWet = "fw_coolant";
        public const string FwShock = "fw_arcchain";
        /// <summary>数据复原稀有档的反面样本（技术数据不够时拒绝）。</summary>
        public const string FwEpic = "fw_amplify";

        /// <summary>进度夹具：技术数据（DEBT-FG2E2E01-01）。等同带回并解析了破碎都市的协议数据盒（+12）。</summary>
        public const int TechFixture = 12;

        /// <summary>预备区离低威胁靶的距离（格）：大于训练靶自动交战距离 12，机器到了预备区不会自己开火。</summary>
        public const float StagingDistance = 16f;

        public const string AnalysisHost = "[HomeValleyAnalysisHost]";
        public const string CircuitHost = "[HomeValleyCircuitBoardHost]";
        public const string FactoryHost = "[HomeValleyFactoryHost]";

        internal static CampaignState St => CampaignSession.Current;

        internal static string Label(int id) => SignalPresence.MachineLabel(id);

        internal static string FwName(string id) => FirmwareKinds.DisplayName(id) ?? id;

        internal static string ConductName => NamedReactionCatalog.NameOf(Conduct);

        // ── 进度夹具 ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 进度夹具（DEBT-FG2E2E01-01）：技术数据 +12，走经济账本的生产事务（与解析台“解析完成”同一个写法）。
        /// 新档没有技术数据，Demo 要先远征破碎都市带回模块再解析；本旅程只补这一项，复原 / 刻印 / 蓝图 / 生产 / 出征全部走正式入口。
        /// 顺带按编号记下开局的两台家园机器（左键点、右键派工用），不写死编号。
        /// </summary>
        internal static void ApplyTechFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null)
            {
                return;
            }
            c.SetInt("tech0", s.TechData);
            string tx = "journey_fixture_techdata_" + c.Seed.ToString(CultureInfo.InvariantCulture);
            CampaignEconomyLedger.ProposeProduce(s, tx, "journey_fixture", CampaignEconomyLedger.ResourceTechData, TechFixture);
            CampaignEconomyLedger.Reserve(s, tx);
            CampaignEconomyLedger.MarkRunning(s, tx);
            CampaignEconomyLedger.Commit(s, tx);
            List<MachineRecord> home = MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && !m.IsInFactory && m.RegionId == HomeValleyLayout.RegionId)
                .OrderBy(m => m.LogicId).ToList();
            c.SetInt("workerA", home.Count > 0 ? home[0].LogicId : 0);
            c.SetInt("workerB", home.Count > 1 ? home[1].LogicId : 0);
            c.Log($"开局家园机器 {home.Count} 台：{string.Join("，", home.Select(m => $"#{m.DisplayNumber}({m.ChassisId})"))}；废料 {s.Scrap}");
        }

        /// <summary>
        /// 机制图鉴是跨存档的本机文件：旅程把它换到本次的临时目录（与存档同一处），开局是空的——“看到图鉴解锁”从“未解锁”开始断言，
        /// 也不往本机的图鉴文件里写（与存档目录改到临时目录同一做法）。收尾时恢复。
        /// </summary>
        internal static void IsolateCodex(JourneyContext c)
        {
            MechanicCodex.ResetForTests();
            MechanicCodex.FilePathOverrideForTests = System.IO.Path.Combine(c.Get("saves"), "codex_mechanics.json");
        }

        internal static void RestoreCodex() => MechanicCodex.ResetForTests();

        internal static StepOutcome TickTechFixture(JourneyContext c)
        {
            c.SetInt("codexConduct0", MechanicCodex.IsUnlocked(MechanicCodex.ReactionEntryId(Conduct)) ? 1 : 0);
            CampaignState s = St;
            if (s == null || s.TechData != c.GetInt("tech0") + TechFixture)
            {
                return StepOutcome.Fail($"夹具没有生效：技术数据 {s?.TechData}（原 {c.GetInt("tech0")}）");
            }
            if (c.GetInt("workerA") == 0 || c.GetInt("workerB") == 0)
            {
                return StepOutcome.Fail("家园里找不到两台可以派工的机器");
            }
            return StepOutcome.Done($"技术数据 {c.GetInt("tech0")} → {s.TechData}（经济账本生产事务；其余进度走正式入口）；工程机 {Label(c.GetInt("workerA"))}、{Label(c.GetInt("workerB"))}");
        }

        // ── 家园建筑 / 残骸（先把它平移进画面再点）────────────────────────────────────

        internal static Transform FindNamed(string name) => FgjM1Journey.FindNamed(name);

        /// <summary>左 / 右键点一个有名字的场景物件（建筑 “Building_类型”、残骸 “Wreckage_节点”）。不在画面里时先按方向键平移，这一次不点（重试时再点）。</summary>
        internal static void ClickNamed(JourneyContext c, string objectName, int button, Func<bool> when = null)
        {
            if (when != null && !when())
            {
                c.Set("flyFirst", string.Empty);
                return;
            }
            Transform t = FindNamed(objectName);
            if (t == null)
            {
                c.Set("flyFirst", "missing");
                return;
            }
            var ground = new Vector2(t.position.x, t.position.z);
            if (JourneyInput.Holding || !JourneyCommon.PanToward(ground, 0.1f))
            {
                c.Set("flyFirst", "1");
                return;
            }
            c.Set("flyFirst", string.Empty);
            JourneyInput.ClickWorld(t.position, button);
            c.SetLong("clickedAtMs", NowMs());
        }

        internal static string PanNote(JourneyContext c) =>
            c.Get("flyFirst") == "1" ? "目标不在画面里，先按方向键平移镜头" : c.Get("flyFirst") == "missing" ? "场景里找不到这个物件" : string.Empty;

        /// <summary>
        /// 上一次点击因为目标不在画面里而改成了“按方向键平移镜头”：平移没结束就等，结束了再执行一次同一个点击动作（它会接着平移或真正点下去）。
        /// 返回 true = 还在平移 / 刚补点，调用方这一帧返回 Wait。整步的超时兜底。
        /// </summary>
        internal static bool PanPending(JourneyContext c, Action<JourneyContext> again)
        {
            if (c.Get("flyFirst") != "1")
            {
                return false;
            }
            if (JourneyInput.Holding)
            {
                c.SetLong("panEndAtMs", 0);
                return true;
            }
            // 松开方向键后镜头还有一小段缓动：等它停稳（0.35 真实秒）再点，否则光标换算出的地面点会偏（点空地 = 移动命令）。
            if (c.GetLong("panEndAtMs") == 0)
            {
                c.SetLong("panEndAtMs", Math.Max(1, NowMs()));
                return true;
            }
            if (NowMs() - c.GetLong("panEndAtMs") < 350)
            {
                return true;
            }
            c.SetLong("panEndAtMs", 0);
            c.SetInt("panPresses", c.GetInt("panPresses") + 1);
            again(c);
            c.SetLong("clickedAtMs", NowMs());
            return true;
        }

        /// <summary>刚补点完的前 0.8 秒里不下结论（点击要两帧才送达，结果还要几帧）。</summary>
        internal static bool JustClicked(JourneyContext c) => NowMs() - c.GetLong("clickedAtMs") < 800;

        /// <summary>真实时钟（毫秒）：跨步骤比较“刚点过 / 刚平移完”用，不受步骤计时归零影响。</summary>
        internal static long NowMs() => (long)(Time.realtimeSinceStartupAsDouble * 1000.0);

        internal static BuildingRecord Building(string typeId) => FgjM1Journey.Building(typeId);

        // ── 解析台：数据复原（FG-GAP-050）────────────────────────────────────────────

        internal static void OpenAnalysis(JourneyContext c) =>
            ClickNamed(c, "Building_" + HomeValleyLayout.BuildingTypeAnalysisBench, 0, () => !GameRoot.HomeValley.IsAnalysisPanelOpen);

        internal static StepOutcome TickAnalysisOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.8 || PanPending(c, OpenAnalysis) || JustClicked(c))
            {
                return StepOutcome.Wait;
            }
            AnalysisPanelUIToolkit p = AnalysisPanelUIToolkit.Instance;
            if (!GameRoot.HomeValley.IsAnalysisPanelOpen || p == null)
            {
                return StepOutcome.Retry("点解析台后解析面板没有打开" + (PanNote(c).Length > 0 ? "：" + PanNote(c) : string.Empty));
            }
            if (p.RestoreChoiceIds.Count == 0)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"解析面板打开：“数据复原”一栏 {p.RestoreChoiceIds.Count} 条可复原固件；技术数据 {St.TechData}；解析台 {(FirmwareRestoreService.BenchReady(St) ? "有电" : "没电")}");
        }

        /// <summary>下拉选中 <paramref name="fwId"/>（设值 = 玩家在弹出菜单里点那一项，同一个 ChangeEvent 回调），再点“复原”。</summary>
        internal static void PickAndRestore(JourneyContext c, string fwId)
        {
            CampaignState s = St;
            c.SetInt("tech0", s.TechData);
            c.SetInt("restored0", FirmwareRestoreService.RestoredCount);
            c.SetInt("rejected0", FirmwareRestoreService.RejectedCount);
            c.SetInt("denied0", FeedbackCues.CountOf(FeedbackCueId.Denied));
            c.SetInt("scrap0", s.Scrap);
            AnalysisPanelUIToolkit p = AnalysisPanelUIToolkit.Instance;
            DropdownField dd = JourneyInput.FindUitk<DropdownField>(AnalysisHost, "RestoreDropdown");
            int idx = p != null ? IndexOf(p.RestoreChoiceIds, fwId) : -1;
            c.SetInt("pickIdx", idx);
            if (dd == null || idx < 0 || idx >= dd.choices.Count)
            {
                c.Set("uiFail", $"下拉里没有“{FwName(fwId)}”（{dd?.choices?.Count ?? 0} 条）");
                return;
            }
            dd.value = dd.choices[idx];
            // 换了选项这一帧布局还没重算：下一次检查（≥ 0.2 秒后）再点“复原”，与玩家先选再点的节奏一致。
            c.SetInt("restoreClickPending", 1);
            c.Set("uiFail", string.Empty);
        }

        /// <summary>选好下拉之后点“复原”（真实指针事件，检查遮挡）。返回 true = 刚点、调用方这一帧 Wait。</summary>
        internal static bool ClickRestoreIfPending(JourneyContext c)
        {
            if (c.GetInt("restoreClickPending") == 0)
            {
                return false;
            }
            if (c.StepElapsed < 0.2)
            {
                return true;
            }
            c.SetInt("restoreClickPending", 0);
            FgjM1Journey.ClickUi(c, AnalysisHost, "RestoreButton");
            c.SetLong("clickedAtMs", NowMs());
            return true;
        }

        private static int IndexOf(IReadOnlyList<string> list, string id)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == id)
                {
                    return i;
                }
            }
            return -1;
        }

        internal static StepOutcome TickConfirmShown(JourneyContext c, string fwId)
        {
            if (ClickRestoreIfPending(c) || JustClicked(c))
            {
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点“复原”失败：" + FgjM1Journey.UiFail(c));
            }
            ConfirmRequest req = UiConfirmDialog.Current;
            if (req == null || req.Title != GameText.Get("analysis.restore.confirm.title"))
            {
                return StepOutcome.Retry($"点“复原”后没有弹出确认框（“{AnalysisPanelUIToolkit.Instance?.ResultText}”）");
            }
            int cost = FirmwareRestoreService.CostOf(fwId);
            bool lineOk = req.Lines.Any(l => l.Contains(FwName(fwId)) && l.Contains(cost.ToString(CultureInfo.InvariantCulture)));
            if (!lineOk || !req.Irreversible || St.TechData != c.GetInt("tech0"))
            {
                return StepOutcome.Fail($"确认框内容不对：{string.Join(" / ", req.Lines)}（不可撤销 {req.Irreversible}）；或确认前就扣了技术数据（{c.GetInt("tech0")} → {St.TechData}）");
            }
            return StepOutcome.Done($"选“{FwName(fwId)}”点“复原”：弹出确认框“{req.Lines.FirstOrDefault()}”（标明不可撤销，确认前不扣任何东西）");
        }

        internal static void ClickConfirm(JourneyContext c, bool ok)
        {
            if (!JourneyInput.ClickUitk("[UiKitOverlayHost]", ok ? "ConfirmOk" : "ConfirmCancel"))
            {
                c.Set("uiFail", JourneyInput.LastUiFailure);
            }
            else
            {
                c.Set("uiFail", string.Empty);
            }
        }

        internal static StepOutcome TickRestored(JourneyContext c, string fwId)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            int cost = FirmwareRestoreService.CostOf(fwId);
            bool unlocked = MechanicalContentUnlock.IsUnlocked(s, fwId);
            bool cracked = !FirmwareKinds.IsRaw(s, fwId);
            bool printable = SignalCoreService.PrintableFirmware(s).Contains(fwId);
            bool machine = FirmwareKinds.CanInstall(s, fwId, FirmwareHost.MachineCircuit, out _);
            string result = AnalysisPanelUIToolkit.Instance?.ResultText ?? string.Empty;
            bool ok = !UiConfirmDialog.IsOpen && unlocked && cracked && printable && machine && s.TechData == c.GetInt("tech0") - cost
                      && FirmwareRestoreService.RestoredCount == c.GetInt("restored0") + 1 && result == GameText.Format("analysis.restore.ok", FwName(fwId), cost)
                      && s.Scrap == c.GetInt("scrap0") && MechanicCodex.IsUnlocked("codex.firmware.restore");
            if (!ok)
            {
                return c.StepElapsed < 3
                    ? StepOutcome.Wait
                    : StepOutcome.Fail($"复原“{FwName(fwId)}”后状态不对：解锁 {unlocked}、已破解 {cracked}、可刻印 {printable}、可装进机器电路 {machine}、技术数据 {c.GetInt("tech0")} → {s.TechData}（应 −{cost}）、" +
                                       $"复原计数 {FirmwareRestoreService.RestoredCount - c.GetInt("restored0")}、废料 {c.GetInt("scrap0")} → {s.Scrap}、图鉴“数据复原”{MechanicCodex.IsUnlocked("codex.firmware.restore")}；结果“{result}”");
            }
            string still = AnalysisPanelUIToolkit.Instance != null && AnalysisPanelUIToolkit.Instance.RestoreChoiceIds.Contains(fwId) ? "（下拉里还有它）" : "（已从下拉里移除）";
            return StepOutcome.Done($"点确认框的“复原”：“{result}”；技术数据 {c.GetInt("tech0")} → {s.TechData}；{FwName(fwId)} 已解锁、视为已破解、可在信号核刻印、可装进机器电路{still}；图鉴“数据复原”系统说明解锁");
        }

        /// <summary>取消确认框：什么都不变（B03 / B04）。</summary>
        internal static StepOutcome TickCancelled(JourneyContext c, string fwId)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            bool same = !UiConfirmDialog.IsOpen && !MechanicalContentUnlock.IsUnlocked(s, fwId) && s.TechData == c.GetInt("tech0")
                        && FirmwareRestoreService.RestoredCount == c.GetInt("restored0");
            return same
                ? StepOutcome.Done($"确认框点“取消”：没有复原、技术数据仍是 {s.TechData}，下拉里仍有“{FwName(fwId)}”")
                : StepOutcome.Fail($"取消后状态变了：确认框开着 {UiConfirmDialog.IsOpen}、解锁 {MechanicalContentUnlock.IsUnlocked(s, fwId)}、技术数据 {c.GetInt("tech0")} → {s.TechData}");
        }

        /// <summary>被拒绝：不弹确认框、原因写在面板结果行（与服务层的预检同一句）、拒绝音、什么都不扣、不解锁。</summary>
        internal static StepOutcome TickRestoreDenied(JourneyContext c, string fwId, string expectCode)
        {
            if (ClickRestoreIfPending(c) || JustClicked(c))
            {
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点“复原”失败：" + FgjM1Journey.UiFail(c));
            }
            CampaignState s = St;
            FirmwareRestoreService.Result check = FirmwareRestoreService.Check(s, fwId);
            string result = AnalysisPanelUIToolkit.Instance?.ResultText ?? string.Empty;
            bool ok = !UiConfirmDialog.IsOpen && !check.Success && check.Code == expectCode && result == check.Message
                      && !MechanicalContentUnlock.IsUnlocked(s, fwId) && s.TechData == c.GetInt("tech0") && s.Scrap == c.GetInt("scrap0")
                      && FeedbackCues.CountOf(FeedbackCueId.Denied) > c.GetInt("denied0");
            return ok
                ? StepOutcome.Done($"“{FwName(fwId)}”被拒绝、不弹确认框：“{result}”+ 拒绝音；技术数据 {s.TechData}、废料 {s.Scrap} 都没动，没有解锁")
                : StepOutcome.Fail($"拒绝时反馈不对：确认框 {UiConfirmDialog.IsOpen}、原因码 {check.Code}（应 {expectCode}）、结果行“{result}”、预检“{check.Message}”、" +
                                   $"技术数据 {c.GetInt("tech0")} → {s.TechData}、拒绝音 {FeedbackCues.CountOf(FeedbackCueId.Denied) - c.GetInt("denied0")}");
        }

        internal static void CloseAnalysis(JourneyContext c) =>
            FgjM1Journey.ClickUi(c, AnalysisHost, "CloseButton", when: () => GameRoot.HomeValley.IsAnalysisPanelOpen);

        internal static StepOutcome TickAnalysisClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return !GameRoot.HomeValley.IsAnalysisPanelOpen ? StepOutcome.Done("点“关闭”：解析面板关闭") : StepOutcome.Retry("解析面板还开着：" + FgjM1Journey.UiFail(c));
        }

        internal static StepOutcome TickBenchPowered(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            BuildingRecord g = Building(HomeValleyLayout.BuildingTypeGenerator);
            if (g == null || g.ConstructionState != BuildingConstructionState.Operational || !FirmwareRestoreService.BenchReady(St))
            {
                if (g != null && g.ConstructionState != BuildingConstructionState.Operational && FgjM1Journey.RepairOrder(HomeValleyLayout.BuildingTypeGenerator) == null && c.StepElapsed > 5)
                {
                    return StepOutcome.Fail($"发电机还没修好，修复工单却没了（废料 {St.Scrap}）");
                }
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"发电机修好：解析台有电（{c.StepElapsed:F0} 秒，3 倍速）；废料 {St.Scrap}");
        }

        // ── 残骸拆解（Demo 正式早期循环：两台 ERC-003 的废料）────────────────────────────

        internal static void RightClickWreck(JourneyContext c)
        {
            c.SetInt("scrapBeforeSalvage", St.Scrap);
            ClickNamed(c, "Wreckage_" + HomeValleyLayout.Wreckage1NodeId, 1);
        }

        internal static StepOutcome TickSalvageOrdered(JourneyContext c)
        {
            if (c.StepElapsed < 0.8 || PanPending(c, RightClickWreck) || JustClicked(c))
            {
                return StepOutcome.Wait;
            }
            WorkOrderRecord o = St.WorkOrders?.LastOrDefault(x => x != null && x.Kind == WorkOrderKind.Salvage && x.TargetId == HomeValleyLayout.Wreckage1NodeId);
            return o != null
                ? StepOutcome.Done($"右键点残骸（情境命令：拆解），工单 {o.State}")
                : StepOutcome.Retry($"右键后没有拆解工单（{PanNote(c)}；{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}）");
        }

        internal static StepOutcome TickSalvageDone(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            int gain = St.Scrap - c.GetInt("scrapBeforeSalvage");
            if (gain < HomeValleyLayout.WreckageScrapYield)
            {
                WorkOrderRecord o = St.WorkOrders?.LastOrDefault(x => x != null && x.Kind == WorkOrderKind.Salvage && x.TargetId == HomeValleyLayout.Wreckage1NodeId);
                if (o != null && (o.State == WorkOrderState.Failed || o.State == WorkOrderState.Cancelled))
                {
                    return StepOutcome.Fail($"拆解工单 {o.State}");
                }
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"残骸拆完：废料 +{gain}（现有 {St.Scrap}）");
        }

        // ── 信号核刻印 ──────────────────────────────────────────────────────────────

        internal static void PrintFirmware(JourneyContext c, string fwId)
        {
            DropdownField print = JourneyInput.FindUitk<DropdownField>("[SignalCoreHost]", "SignalPrintChoice");
            string name = FwName(fwId);
            string choice = print?.choices?.FirstOrDefault(x => x.Contains(name));
            c.Set("printChoice", choice ?? string.Empty);
            c.SetInt("scrapBeforePrint", St.Scrap);
            c.SetInt("chips0", St.PrimitiveChips?.Length ?? 0);
            if (print != null && choice != null)
            {
                print.value = choice;
                FgjM1Journey.ClickUi(c, "[SignalCoreHost]", "SignalPrint");
            }
        }

        internal static StepOutcome TickPrinted(JourneyContext c, string fwId)
        {
            if (c.Get("printChoice").Length == 0)
            {
                return StepOutcome.Fail($"刻印下拉里没有“{FwName(fwId)}”（复原后应当可以刻印）");
            }
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            PrimitiveChipRecord chip = PrimitiveInventory.Find(St, hud?.SelectedPartId);
            int cost = SignalCoreService.FirmwareChipPrintScrap;
            return chip != null && chip.CardDefId == fwId && St.Scrap == c.GetInt("scrapBeforePrint") - cost
                ? StepOutcome.Done($"刻印一枚“{FwName(fwId)}”芯片（来源：{chip.Origin}），扣 {cost} 废料")
                : StepOutcome.Retry($"刻印没成功（{hud?.FeedbackText}；{FgjM1Journey.UiFail(c)}）");
        }

        // ── 固件库：筛选流体类（FGJ-M2 第 1 步）──────────────────────────────────────────

        internal static StepOutcome TickLibraryOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            FirmwareLibraryPanelUIToolkit lib = FirmwareLibraryPanelUIToolkit.Instance;
            if (!FirmwareLibraryPanelUIToolkit.IsOpen || lib == null || !lib.PanelVisible)
            {
                return StepOutcome.Retry("按固件库键后固件库没有打开");
            }
            c.SetInt("libTotal", lib.RowCount);
            return StepOutcome.Done($"固件库打开：{lib.CountText}；全部 {lib.RowCount} 行");
        }

        internal static void FilterFluid(JourneyContext c)
        {
            DropdownField dd = JourneyInput.FindUitk<DropdownField>("[FirmwareLibraryHost]", "FwLibFilterCategory");
            string want = GameText.Get("firmware.category.fluid");
            string choice = dd?.choices?.FirstOrDefault(x => x == want);
            c.Set("fluidChoice", choice ?? string.Empty);
            if (dd != null && choice != null && JourneyInput.IsClickable(dd))
            {
                dd.value = choice;
            }
        }

        internal static StepOutcome TickFilteredFluid(JourneyContext c)
        {
            if (c.Get("fluidChoice").Length == 0)
            {
                return StepOutcome.Fail("类别下拉里没有“流体”");
            }
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            FirmwareLibraryPanelUIToolkit lib = FirmwareLibraryPanelUIToolkit.Instance;
            var rows = new List<FirmwareLibraryRow>();
            for (int i = 0; lib != null && i < lib.RowCount; i++)
            {
                rows.Add(lib.Row(i));
            }
            bool allFluid = rows.Count > 0 && rows.All(r => r.Category == FirmwareCategory.Fluid);
            bool hasWet = rows.Any(r => r.FirmwareId == FwWet);
            // 对照：库里其实还有别的类别的芯片（筛选真的筛掉了东西）。
            var all = new List<FirmwareLibraryRow>();
            FirmwareLibrary.Query(St, new FirmwareLibraryFilter(), all);
            int nonFluidOwned = all.Count(r => r.Category != FirmwareCategory.Fluid);
            if (!allFluid || !hasWet || nonFluidOwned == 0)
            {
                return StepOutcome.Fail($"筛选“流体”后列表不对：{rows.Count} 行，全是流体 {allFluid}、有冷却液 {hasWet}；库里非流体芯片 {nonFluidOwned} 枚");
            }
            return StepOutcome.Done($"类别下拉选“流体”：{rows.Count} 行全是流体类（{string.Join("、", rows.Select(r => r.Name).Distinct())}），筛掉了 {nonFluidOwned} 枚其他类别的芯片；{lib.CountText}");
        }

        /// <summary>点选冷却液那一行（虚拟化列表：先滚到可见，再派发真实指针事件）。</summary>
        internal static void ClickWetRow(JourneyContext c)
        {
            FirmwareLibraryPanelUIToolkit lib = FirmwareLibraryPanelUIToolkit.Instance;
            int idx = -1;
            for (int i = 0; lib != null && i < lib.RowCount; i++)
            {
                if (lib.Row(i).FirmwareId == FwWet)
                {
                    idx = i;
                    break;
                }
            }
            c.SetInt("wetRow", idx);
            if (lib?.ListView == null || idx < 0)
            {
                return;
            }
            lib.ListView.ScrollToItem(idx);
            VisualElement row = null;
            lib.ListView.Query<VisualElement>(className: "fl-row").ForEach(r =>
            {
                if (row == null && r.userData is int i && i == idx)
                {
                    row = r;
                }
            });
            Label name = row?.Q<Label>("FwLibRowName");
            c.Set("uiFail", name != null && JourneyInput.ClickElement(name) ? string.Empty : name == null ? "冷却液那一行还没建出" : JourneyInput.LastUiFailure);
        }

        internal static StepOutcome TickWetDetail(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            FirmwareLibraryPanelUIToolkit lib = FirmwareLibraryPanelUIToolkit.Instance;
            int idx = c.GetInt("wetRow", -1);
            if (lib == null || idx < 0)
            {
                return StepOutcome.Fail("列表里没有冷却液");
            }
            string partId = lib.Row(idx)?.Chip?.PartId;
            string body = lib.DetailBodyText;
            if (lib.SelectedPartId != partId)
            {
                return StepOutcome.Retry($"点冷却液那一行后没有选中（{FgjM1Journey.UiFail(c)}）");
            }
            // 还没打出过的反应不剧透名字（固件库详情的既有规则）：参与的反应一行写“？？？（尚未发现）”；打出短路之后同一行会写名字。
            string reactionsLine = body.Split('\n').FirstOrDefault(l => l.StartsWith(GameText.Format("fwlib.detail.reactions", string.Empty), StringComparison.Ordinal)) ?? string.Empty;
            bool reaction = reactionsLine.Contains(GameText.Get("fwlib.detail.reaction_unknown")) || reactionsLine.Contains(ConductName);
            bool wet = body.Contains(StatusTagCatalog.NameOf("Wet") ?? "浸湿") && body.Contains(FirmwareKinds.AcquireText(FwWet));
            return reaction && wet
                ? StepOutcome.Done($"点选冷却液：详情“{lib.DetailTitleText}”写明产生“{StatusTagCatalog.NameOf("Wet")}”、获取途径，“{reactionsLine.Trim()}”（还没打出过的反应不剧透名字）")
                : StepOutcome.Fail($"冷却液详情没写标签 / 反应 / 获取途径：{body.Replace("\n", " / ")}");
        }

        internal static StepOutcome TickLibraryClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            return !FirmwareLibraryPanelUIToolkit.IsOpen && !PauseMenuUIToolkit.IsOpen ? StepOutcome.Done("再按固件库键关闭固件库") : StepOutcome.Retry("固件库还开着");
        }

        // ── 蓝图固件槽 / 生产 ────────────────────────────────────────────────────────

        internal static void PickFirmwareSlot0(JourneyContext c, string fwId)
        {
            DropdownField dd = JourneyInput.FindUitk<DropdownField>(CircuitHost, "Firmware0Dropdown");
            string name = FwName(fwId);
            string choice = dd?.choices?.FirstOrDefault(x => x != null && x.StartsWith(name, StringComparison.Ordinal));
            c.Set("fwChoice", choice ?? string.Empty);
            c.Set("fwChoices", dd?.choices == null ? string.Empty : string.Join("／", dd.choices.Take(12)));
            if (dd != null && choice != null && JourneyInput.IsClickable(dd))
            {
                dd.value = choice;
            }
        }

        internal static StepOutcome TickFirmwareSlot0(JourneyContext c, string fwId)
        {
            if (c.Get("fwChoice").Length == 0)
            {
                return StepOutcome.Fail($"固件槽下拉里没有“{FwName(fwId)}”（{c.Get("fwChoices")}）");
            }
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            BlueprintCircuitBoard b = FgjM1Journey.Panel()?.Board;
            string reading = JourneyInput.FindUitk<Label>(CircuitHost, "FirmwareReadingLabel")?.text ?? string.Empty;
            return b != null && b.FirmwareSlots[0] == fwId
                ? StepOutcome.Done($"固件槽 1 选“{c.Get("fwChoice")}”；读法说明“{reading.Replace("\n", " / ")}”")
                : StepOutcome.Retry($"选了“{c.Get("fwChoice")}”，固件槽仍是 {b?.FirmwareSlots[0]}");
        }

        internal static StepOutcome TickSavedWithFirmware(JourneyContext c, string fwId, string verKey)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            BlueprintRecord r = FgjM1Journey.Erc003Record();
            BlueprintVersionRecord v = r?.Versions?.FirstOrDefault(x => x.Version == r.ActiveVersion);
            BlueprintCircuitBoard saved = v != null ? BlueprintCircuitBoard.FromVersion(v) : null;
            string label = JourneyInput.FindUitk<Label>(CircuitHost, "SaveResultLabel")?.text ?? string.Empty;
            if (saved == null || saved.FirmwareSlots[0] != fwId)
            {
                return StepOutcome.Retry($"保存后 ERC-003 蓝图的现役版本固件槽不是“{FwName(fwId)}”（v{r?.ActiveVersion}：{saved?.FirmwareSlots[0]}；“{label}”；点击：{(FgjM1Journey.UiFail(c).Length == 0 ? "已送达" : FgjM1Journey.UiFail(c))}）");
            }
            c.SetInt(verKey, v.Version);
            return StepOutcome.Done($"保存成功：ERC-003 蓝图现役 v{v.Version}（固件槽 1 = {FwName(fwId)}，造价 {v.ScrapCost} 废料）；“{label}”");
        }

        internal static void ClickProduce(JourneyContext c) => FgjM1Journey.ClickUi(c, FactoryHost, "ProduceBtn_Erc003");

        internal static StepOutcome TickProduceQueued(JourneyContext c, string verKey)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            FactoryQueueItemRecord item = St.FactoryQueues?.LastOrDefault(q => q != null && q.Kind == FactoryQueueKind.Produce && q.BlueprintId == HomeValleyLayout.BlueprintErc003Id);
            return item != null && item.BlueprintVersion == c.GetInt(verKey)
                ? StepOutcome.Done($"生产队列：ERC-003 v{item.BlueprintVersion}（{item.State}，{item.Duration:F0} 秒）")
                : StepOutcome.Retry($"点“生产”后队列里没有 ERC-003 v{c.GetInt(verKey)}（{FgjM1Journey.UiFail(c)}；{JourneyInput.FindUitk<Label>(FactoryHost, "ProduceHintLabel")?.text}）");
        }

        internal static StepOutcome TickProduced(JourneyContext c, string verKey, string idKey)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            int ver = c.GetInt(verKey);
            MachineRecord m = MachineRegistry.AllRecords.FirstOrDefault(x => x != null && x.IsAlive && x.ChassisId == HomeValleyLayout.Erc003ChassisId
                && x.BlueprintId == HomeValleyLayout.BlueprintErc003Id && x.BlueprintVersion == ver && x.RegionId == HomeValleyLayout.RegionId);
            if (m == null)
            {
                FactoryQueueItemRecord item = St.FactoryQueues?.LastOrDefault(q => q != null && q.Kind == FactoryQueueKind.Produce && q.BlueprintVersion == ver
                                                                                     && q.BlueprintId == HomeValleyLayout.BlueprintErc003Id);
                if (item != null && (item.State == FactoryQueueState.Failed || item.State == FactoryQueueState.Cancelled))
                {
                    return StepOutcome.Fail($"ERC-003 v{ver} 生产 {item.State}：{item.BlockedReason}");
                }
                if (item != null && item.State == FactoryQueueState.WaitingResources && c.StepElapsed > 20 && c.GetInt("blockLogged" + ver) == 0)
                {
                    c.SetInt("blockLogged" + ver, 1);
                    c.Log($"生产受阻：{item.State} {item.BlockedReason}（废料 {St.Scrap}）");
                }
                return StepOutcome.Wait;
            }
            if (!FgjM1Journey.HomeMarker(m.LogicId, out _))
            {
                return StepOutcome.Wait;
            }
            c.SetInt(idKey, m.LogicId);
            GameRoot.HomeValley.Combat.TryGetMachineWeapon(m.LogicId, out MachineWeaponInfo info);
            return StepOutcome.Done($"{Label(m.LogicId)} 出厂（ERC-003 v{ver}，生效固件 [{string.Join("、", (info.FirmwareIds ?? Array.Empty<string>()).Select(FwName))}]，" +
                                    $"机身 {MachineMorph.Describe(info.Morph)}），停在装配站出口（占着出口 = {m.IsInFactory}）");
        }

        // ── 预备区 ──────────────────────────────────────────────────────────────────

        /// <summary>预备区中心：低威胁靶朝 <paramref name="from"/> 方向 16 格（按布局锚点与机器实际位置算，不写死坐标；走不到时绕靶换方向）。</summary>
        internal static Vector2 StagingPoint(Vector2 from)
        {
            Vector2 t = HomeValleyLayout.LowThreatTargetPosition;
            Vector2 dir = from - t;
            if (dir.sqrMagnitude < 1e-4f)
            {
                dir = Vector2.down;
            }
            dir.Normalize();
            using (var k = FgjM1ReverseJourney.NewProbe(out _))
            {
                GridCell start = NavService.CellOf(from.x, from.y);
                for (int step = 0; step < 18; step++)
                {
                    float ang = (step % 2 == 0 ? 1 : -1) * ((step + 1) / 2) * 20f * Mathf.Deg2Rad;
                    Vector2 d = new Vector2(dir.x * Mathf.Cos(ang) - dir.y * Mathf.Sin(ang), dir.x * Mathf.Sin(ang) + dir.y * Mathf.Cos(ang));
                    Vector2 p = t + d * StagingDistance;
                    GridCell g = NavService.CellOf(p.x, p.y);
                    if (k == null || (k.Passable(g.X, g.Y, BinGames.Sim.Nav.NavConst.ClassPlayer) && FgjM1ReverseJourney.Probe(k, start, g, 700 + step, out _)))
                    {
                        return p;
                    }
                }
            }
            return t + dir * StagingDistance;
        }

        internal static void SetPoint(JourneyContext c, string key, Vector2 p)
        {
            c.Set(key + "X", p.x.ToString("R", CultureInfo.InvariantCulture));
            c.Set(key + "Y", p.y.ToString("R", CultureInfo.InvariantCulture));
        }

        internal static Vector2 Point(JourneyContext c, string key) =>
            new Vector2(float.Parse(c.Get(key + "X", "0"), CultureInfo.InvariantCulture), float.Parse(c.Get(key + "Y", "0"), CultureInfo.InvariantCulture));

        internal static bool HomePos(int logicId, out Vector2 p)
        {
            p = default;
            return GameRoot.HomeValley?.Combat != null && GameRoot.HomeValley.Combat.TryGetMachinePosition(logicId, out p);
        }

        /// <summary>右键点预备区地面（选中的机器接到移动命令；新机因此驶出装配站出口）。预备区不在画面里先平移。</summary>
        internal static void RightClickStaging(JourneyContext c)
        {
            Vector2 p = Point(c, "stage");
            if (JourneyInput.Holding || !JourneyCommon.PanToward(p, 0.25f))
            {
                c.Set("flyFirst", "1");
                return;
            }
            c.Set("flyFirst", string.Empty);
            JourneyInput.Click(p, button: 1);
            c.SetLong("clickedAtMs", NowMs());
        }

        internal static StepOutcome TickMovingToStaging(JourneyContext c, string idKey)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.StepElapsed < 0.8 || PanPending(c, RightClickStaging) || JustClicked(c))
            {
                return StepOutcome.Wait;
            }
            int id = c.GetInt(idKey);
            CombatSite site = GameRoot.HomeValley.Combat;
            // 厂内新机不进编队选择集（还没驶出），右键地面走控制器的单机移动兜底（工作赶路 WorkMove，第一条命令即驶出装配站出口）；驶出后的机器走编队移动（Move）。两种都是“开过去”。
            bool moving = site.TryGetMachineUnit(id, out int unit) && site.TryGetCommand(unit, out BinGames.Sim.Combat.CombatCommand cmd)
                          && (cmd.Kind == BinGames.Sim.Combat.CombatCommandKind.Move || cmd.Kind == BinGames.Sim.Combat.CombatCommandKind.WorkMove);
            HomePos(id, out Vector2 at);
            bool there = Vector2.Distance(at, Point(c, "stage")) < 3f;
            return moving || there
                ? StepOutcome.Done($"右键预备区地面：{Label(id)} 接到移动命令（不再占着装配站出口）")
                : StepOutcome.Retry($"右键后 {Label(id)} 没有接到移动命令（{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}）");
        }

        internal static StepOutcome TickBothStaged(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            Vector2 stage = Point(c, "stage");
            int a = c.GetInt("wetM");
            int b = c.GetInt("shockM");
            if (!HomePos(a, out Vector2 pa) || !HomePos(b, out Vector2 pb))
            {
                return StepOutcome.Fail("组合的两台机器不见了");
            }
            float da = Vector2.Distance(pa, stage);
            float db = Vector2.Distance(pb, stage);
            if (da > 4f || db > 4f)
            {
                return StepOutcome.Wait;
            }
            float dt = Vector2.Distance(stage, HomeValleyLayout.LowThreatTargetPosition);
            if (ReactionFeedback.IsFirstTriggered(St, Conduct) || ReactionCount(GameRoot.HomeValley.Combat) > 0)
            {
                // 玩家还没下令就打出了反应 = 机器自己开火（例如开进了训练靶自动交战距离），违反“只执行玩家的命令”（FGR-BASE-020）。
                return StepOutcome.Fail($"两台到预备区之前就打出了短路（反应计数 {ReactionCount(GameRoot.HomeValley.Combat)}）：机器没接攻击命令就对训练靶开火");
            }
            c.SetInt("reactions0", St.ReactionFirstTriggers?.Length ?? 0);
            return StepOutcome.Done($"两台都到了预备区（离低威胁靶 {dt:F1} 格，超出训练靶自动交战距离 {HomeValleyCombatTargets.EngageRange:F0}：没有命令就不开火）：" +
                                    $"{Label(a)} 离中心 {da:F1} 格、{Label(b)} 离中心 {db:F1} 格");
        }

        /// <summary>
        /// 拖框选中组合的两台（拖出的框与地面指示器同一个矩形）：框按两台此刻的实际位置拉（外扩 2 格），它们打过靶、不在预备区中心时也框得到；
        /// 框里若还有别的机器（例如路过的工程机），选择集就不止两台——检查会如实报出来并重拉。
        /// </summary>
        internal static void BoxSelectStaging(JourneyContext c)
        {
            if (!HomePos(c.GetInt("wetM"), out Vector2 pa) || !HomePos(c.GetInt("shockM"), out Vector2 pb))
            {
                c.Set("flyFirst", "missing");
                return;
            }
            Vector2 mid = (pa + pb) * 0.5f;
            if (JourneyInput.Holding || !JourneyCommon.PanToward(mid, 0.25f))
            {
                c.Set("flyFirst", "1");
                return;
            }
            c.Set("flyFirst", string.Empty);
            var lo = new Vector2(Mathf.Min(pa.x, pb.x) - 2f, Mathf.Min(pa.y, pb.y) - 2f);
            var hi = new Vector2(Mathf.Max(pa.x, pb.x) + 2f, Mathf.Max(pa.y, pb.y) + 2f);
            JourneyInput.Drag(lo, hi);
            c.SetLong("clickedAtMs", NowMs());
        }

        internal static StepOutcome TickPairSelected(JourneyContext c, RegionSquadCommandSystem squad)
        {
            if (c.StepElapsed < 0.8 || PanPending(c, BoxSelectStaging) || JustClicked(c))
            {
                return StepOutcome.Wait;
            }
            int a = c.GetInt("wetM");
            int b = c.GetInt("shockM");
            IReadOnlyList<int> sel = squad.Selection;
            return sel.Count == 2 && sel.Contains(a) && sel.Contains(b)
                ? StepOutcome.Done($"选中组合的两台：{Label(a)}（{FwName(FwWet)}）+ {Label(b)}（{FwName(FwShock)}）")
                : StepOutcome.Retry($"选择集是 [{string.Join(",", sel.Select(Label))}]");
        }

        // ── 右键攻击低威胁靶 / 反应核对 ────────────────────────────────────────────────

        internal static void RightClickDummy(JourneyContext c)
        {
            Vector2 t = HomeValleyLayout.LowThreatTargetPosition;
            c.SetInt("slow0", GameClock.SlowMotionStarts);
            c.SetInt("nudge0", CameraNudge.Starts);
            c.SetLong("spawn0", ReactionPopups.SpawnedCount);
            c.SetInt("conduct0", ReactionCount(GameRoot.HomeValley.Combat));
            if (JourneyInput.Holding || !JourneyCommon.PanToward(t, 0.3f))
            {
                c.Set("flyFirst", "1");
                return;
            }
            c.Set("flyFirst", string.Empty);
            MarkEvents(c, GameRoot.HomeValley?.SquadCommands);
            JourneyInput.Click(t, button: 1);
            c.SetLong("clickedAtMs", NowMs());
        }

        /// <summary>点击前记下编队命令事件的位置（事件表有上限、会挤掉最老的：记条数 + 最后一条）。</summary>
        internal static void MarkEvents(JourneyContext c, RegionSquadCommandSystem squad)
        {
            IReadOnlyList<string> ev = squad?.RecentEvents;
            c.SetInt("ev0", ev?.Count ?? 0);
            c.Set("evLast0", ev != null && ev.Count > 0 ? ev[ev.Count - 1] : string.Empty);
        }

        /// <summary>点击之后新出现的编队命令事件（“攻击 已下达（2 台）。”这类）。</summary>
        internal static List<string> NewEvents(JourneyContext c, RegionSquadCommandSystem squad)
        {
            var list = new List<string>();
            IReadOnlyList<string> ev = squad?.RecentEvents;
            if (ev == null)
            {
                return list;
            }
            int n0 = c.GetInt("ev0");
            string last0 = c.Get("evLast0", string.Empty);
            int start = n0;
            if (ev.Count <= n0 || (n0 > 0 && ev[n0 - 1] != last0))
            {
                // 表满后挤掉了老事件：从后往前找点击前的最后一条。
                start = 0;
                for (int i = ev.Count - 1; i >= 0; i--)
                {
                    if (last0.Length > 0 && ev[i] == last0)
                    {
                        start = i + 1;
                        break;
                    }
                }
            }
            for (int i = start; i < ev.Count; i++)
            {
                list.Add(ev[i]);
            }
            return list;
        }

        /// <summary>右键敌人后编队下达了攻击命令（两台接令）：“攻击 已下达（2 台）。”。</summary>
        internal static bool AttackIssued(JourneyContext c, RegionSquadCommandSystem squad, int count) =>
            NewEvents(c, squad).Any(e => e.StartsWith("攻击", StringComparison.Ordinal) && e.Contains("（" + count + " 台）"));

        /// <summary>
        /// 训练靶在两台还没凑出短路之前就被打空（两台出手的先后不巧）：两台的攻击命令随之结束（不会自己再找目标）。
        /// 像玩家一样等靶满血复位后再右键它（至多 5 次）；只在家园（有训练靶）时生效，远征地点由 <see cref="TickFcConduct"/> 处理。
        /// </summary>
        internal static StepOutcome? ReissueIfDummyGone(JourneyContext c, CombatSite site)
        {
            if (site == null || site.SiteId != HomeValleyLayout.RegionId || c.GetInt("cmdSeen") == 0)
            {
                return null;
            }
            int a = c.GetInt("wetM");
            int b = c.GetInt("shockM");
            if (IsAttacking(site, a) || IsAttacking(site, b) || JourneyInput.Holding)
            {
                return null;
            }
            CombatTargetRecord t = HomeValleyCombatTargets.Find(St, HomeValleyCombatTargets.LowThreatTargetId);
            if (t == null || t.Health < t.MaxHealth || NowMs() - c.GetLong("reissueAtMs") < 1500)
            {
                return null; // 还在复位：等
            }
            int n = c.GetInt("dummyReissue");
            if (n >= 5)
            {
                return StepOutcome.Fail($"训练靶打空 / 复位后又右键了 {n} 次，仍没打出短路（反应计数 {ReactionCount(site) - c.GetInt("conduct0")}）");
            }
            c.SetInt("dummyReissue", n + 1);
            c.SetLong("reissueAtMs", NowMs());
            c.Log($"两台的攻击命令已结束（训练靶被打空后复位、没有自己再找目标），像玩家一样再右键训练靶（第 {n + 1} 次）");
            Vector2 tp = HomeValleyLayout.LowThreatTargetPosition;
            if (JourneyCommon.PanToward(tp, 0.3f))
            {
                MarkEvents(c, GameRoot.HomeValley?.SquadCommands);
                JourneyInput.Click(tp, button: 1);
                c.SetLong("clickedAtMs", NowMs());
            }
            return null;
        }

        internal static int ReactionCount(CombatSite site)
        {
            int idx = RuleIndex();
            return site != null && idx >= 0 ? site.Kernel.ReactionCountOf(idx) : 0;
        }

        internal static int RuleIndex()
        {
            for (int i = 0; i < BinGames.Sim.Combat.CombatConst.MaxReactions; i++)
            {
                if (NamedReactionCatalog.IdOfRule(i) == Conduct)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>两台都接到了攻击命令（右键敌人 = 攻击，RTS 口径）。</summary>
        internal static bool BothAttacking(CombatSite site, int a, int b) =>
            IsAttacking(site, a) && IsAttacking(site, b);

        internal static string CommandOf(CombatSite site, int id) =>
            site != null && site.TryGetMachineUnit(id, out int unit) && site.TryGetCommand(unit, out BinGames.Sim.Combat.CombatCommand cmd) ? cmd.Kind.ToString() : "无单位";

        internal static bool IsAttacking(CombatSite site, int id) =>
            site != null && site.TryGetMachineUnit(id, out int unit) && site.TryGetCommand(unit, out BinGames.Sim.Combat.CombatCommand cmd)
            && cmd.Kind == BinGames.Sim.Combat.CombatCommandKind.Attack;

        /// <summary>
        /// FGJ-M2 第 3 步：两台机器打同一个目标，第一次打出“短路”——首次触发记录（= 图鉴解锁）、首次慢放、镜头轻推、首次弹字“新反应：短路！”、日志写明触发者与参与的固件。
        /// </summary>
        internal static StepOutcome TickFirstConduct(JourneyContext c, CombatSite site, string siteLabel)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            SampleFrame();
            int a = c.GetInt("wetM");
            int b = c.GetInt("shockM");
            // 首次弹字停留约 1.2 秒：每一帧都看一眼，一出现就记下来。
            ReactionPopups.Popup firstPop = ReactionPopups.Active.FirstOrDefault(p => p.First && p.Key == ReactionFeedback.PopupKey(Conduct));
            if (firstPop != null && c.Get("firstPopup").Length == 0)
            {
                c.Set("firstPopup", firstPop.Text);
            }
            if (c.GetInt("cmdSeen") == 0)
            {
                if (c.StepElapsed < 0.8 || PanPending(c, RightClickDummy) || JustClicked(c))
                {
                    return StepOutcome.Wait;
                }
                // 下令成功 = 编队记下“攻击 已下达（2 台）”（靶只有 60 血，两台可能几发就把它打空、命令随之结束——那是目标死亡，另行处理）。
                if (!AttackIssued(c, GameRoot.HomeValley.SquadCommands, 2))
                {
                    return StepOutcome.Retry($"右键目标后两台没有都接到攻击命令（{Label(a)} {CommandOf(site, a)}、{Label(b)} {CommandOf(site, b)}；" +
                                             $"最近事件“{string.Join(" / ", GameRoot.HomeValley.SquadCommands.RecentEvents.Skip(Math.Max(0, GameRoot.HomeValley.SquadCommands.RecentEvents.Count - 2)))}”；" +
                                             $"选择集 [{string.Join(",", GameRoot.HomeValley.SquadCommands.Selection)}]；目标屏幕位置 {JourneyInput.ScreenOf(HomeValleyLayout.LowThreatTargetPosition)}）");
                }
                c.SetInt("cmdSeen", 1);
                c.Log($"右键目标：{Label(a)}、{Label(b)} 都接到攻击命令（{siteLabel}；“{NewEvents(c, GameRoot.HomeValley.SquadCommands).LastOrDefault()}”）");
            }
            if (!ReactionFeedback.IsFirstTriggered(St, Conduct))
            {
                return ReissueIfDummyGone(c, site) ?? StepOutcome.Wait;
            }
            ReactionFirstTriggerRecord rec = ReactionFeedback.FirstRecordOf(St, Conduct);
            bool slow = GameClock.SlowMotionStarts > c.GetInt("slow0");
            bool nudge = CameraNudge.Starts > c.GetInt("nudge0");
            bool codex = MechanicCodex.IsUnlocked(MechanicCodex.ReactionEntryId(Conduct));
            ReactionLogEntry entry = ReactionLog.All.LastOrDefault(e => e.ReactionId == Conduct);
            string line = entry != null ? ReactionLog.Describe(St, entry) : string.Empty;
            bool logOk = entry != null && entry.Source.Kind == BinGames.Sim.Combat.CombatUnitKind.Machine && (entry.Source.LogicId == a || entry.Source.LogicId == b)
                         && entry.FirmwareIds.Length >= 1 && entry.First;
            if (c.Get("firstPopup").Length == 0 && c.GetInt("popupWait") < 30)
            {
                c.SetInt("popupWait", c.GetInt("popupWait") + 1); // 弹字与首次记录同一步推入，给界面几帧
                return StepOutcome.Wait;
            }
            bool popupOk = c.Get("firstPopup").Contains(ConductName);
            bool codexFresh = c.GetInt("codexConduct0") == 0; // 开局时图鉴里没有短路（图鉴文件已隔离到本次临时目录）
            if (!slow || !nudge || !codex || !codexFresh || !logOk || !popupOk || rec.SiteId != site.SiteId)
            {
                return StepOutcome.Fail($"第一次短路的反馈不全：首次记录地点 {rec?.SiteId}（应 {site.SiteId}）、慢放 {slow}、镜头轻推 {nudge}、图鉴解锁 {codex}（开局未解锁 {codexFresh}）、日志“{line}”、首次弹字“{c.Get("firstPopup")}”");
            }
            c.SetInt("slowAfterFirst", GameClock.SlowMotionStarts);
            return StepOutcome.Done($"{siteLabel}第一次打出“{ConductName}”：首次慢放（{ReactionFeedback.SlowMotionSeconds} 秒 × {ReactionFeedback.SlowMotionFactor}）、镜头轻推、弹字“{c.Get("firstPopup")}”、" +
                                    $"图鉴反应条目解锁；日志“{line}”（{c.StepElapsed:F1} 秒）");
        }

        // ── 图鉴：看到反应条目解锁（FGJ-M2 第 3 步后半）────────────────────────────────────

        internal static StepOutcome TickCodexOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            return MechanicCodexPanelUIToolkit.IsOpen && MechanicCodexPanelUIToolkit.Instance != null && MechanicCodexPanelUIToolkit.Instance.PanelVisible
                ? StepOutcome.Done($"按图鉴键打开图鉴（{MechanicCodexPanelUIToolkit.Instance.CountText}）")
                : StepOutcome.Retry("按图鉴键后图鉴没有打开");
        }

        internal static void ClickReactionTab(JourneyContext c) => FgjM1Journey.ClickUi(c, "[MechanicCodexHost]", "CodexTabReaction");

        internal static StepOutcome TickReactionTab(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            MechanicCodexPanelUIToolkit p = MechanicCodexPanelUIToolkit.Instance;
            return p != null && p.CurrentTab == MechanicCodex.TabReaction
                ? StepOutcome.Done($"点“反应”页签：{p.CountText}")
                : StepOutcome.Retry("点页签后不在反应页签：" + FgjM1Journey.UiFail(c));
        }

        internal static void ClickConductEntry(JourneyContext c)
        {
            MechanicCodexPanelUIToolkit p = MechanicCodexPanelUIToolkit.Instance;
            string id = MechanicCodex.ReactionEntryId(Conduct);
            for (int i = 0; p != null && i < p.ItemCount; i++)
            {
                if (p.ItemId(i) == id)
                {
                    Button b = p.ItemButton(i);
                    JourneyInput.ScrollIntoView(JourneyInput.FindUitk<ScrollView>("[MechanicCodexHost]", "CodexList"), b);
                    c.Set("uiFail", JourneyInput.ClickElement(b) ? string.Empty : JourneyInput.LastUiFailure);
                    return;
                }
            }
            c.Set("uiFail", "反应页签里没有短路条目");
        }

        internal static StepOutcome TickConductEntry(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            MechanicCodexPanelUIToolkit p = MechanicCodexPanelUIToolkit.Instance;
            string id = MechanicCodex.ReactionEntryId(Conduct);
            if (p == null || p.SelectedId != id)
            {
                return StepOutcome.Retry("点短路条目后没有选中：" + FgjM1Journey.UiFail(c));
            }
            bool unlocked = p.EntryTitleText == ConductName && !p.EntryTitleText.Contains("？");
            bool body = p.EntryBodyText.Contains(StatusTagCatalog.NameOf("Wet") ?? "浸湿") && p.EntryBodyText.Contains(FwName(FwWet));
            return unlocked && body
                ? StepOutcome.Done($"图鉴反应条目“{p.EntryTitleText}”已解锁（不是剪影）：{p.EntryBodyText.Replace("\n", " / ")}")
                : StepOutcome.Fail($"图鉴短路条目不对：标题“{p.EntryTitleText}”、正文“{p.EntryBodyText.Replace("\n", " / ")}”");
        }

        internal static StepOutcome TickCodexClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            return !MechanicCodexPanelUIToolkit.IsOpen && !PauseMenuUIToolkit.IsOpen ? StepOutcome.Done("再按图鉴键关闭图鉴") : StepOutcome.Retry("图鉴还开着");
        }

        // ── 出征名单（组合的两台必去，再挑一台满足人数）──────────────────────────────────

        internal static StepOutcome TickRosterPicked(JourneyContext c)
        {
            CampaignState s = St;
            if (c.Get("roster", string.Empty).Length == 0)
            {
                ExpeditionDepartureService.PrepSnapshot snap = ExpeditionDepartureService.BuildPrepSnapshot(s);
                int a = c.GetInt("wetM");
                int b = c.GetInt("shockM");
                List<ExpeditionDepartureService.MachineIntel> eligible = snap.Machines.Where(m => m.Eligible).OrderBy(m => m.BusyKind.HasValue).ThenBy(m => m.LogicId).ToList();
                List<int> chosen = null;
                foreach (IEnumerable<int> combo in FgjM1Journey.Combos(eligible.Where(m => m.LogicId != a && m.LogicId != b).Select(m => m.LogicId).ToList(),
                             Math.Max(0, ExpeditionDepartureService.MinRosterSize - 2)))
                {
                    var ids = new List<int> { a, b };
                    ids.AddRange(combo);
                    if (ExpeditionDepartureService.ValidateRoster(s, ids).Success)
                    {
                        chosen = ids;
                        break;
                    }
                }
                if (chosen == null)
                {
                    return StepOutcome.Fail($"找不到带上组合两台的出征名单（可出征 {eligible.Count} 台）");
                }
                c.Set("roster", string.Join(",", chosen.Select(x => x.ToString(CultureInfo.InvariantCulture))));
                c.SetInt("rosterIdx", 0);
            }
            List<int> roster = FgjM1Journey.Roster(c);
            int idx = c.GetInt("rosterIdx");
            if (idx < roster.Count)
            {
                if (c.StepElapsed < (idx + 1) * 0.6)
                {
                    return StepOutcome.Wait;
                }
                Toggle t = FgjM1Journey.RowToggle(roster[idx]);
                if (t == null)
                {
                    return StepOutcome.Fail($"名单表里找不到 {Label(roster[idx])} 这一行");
                }
                if (!JourneyInput.ScrollIntoView(JourneyInput.FindUitk<ScrollView>("[HomeValleyExpeditionPrepHost]", "MachineList"), t))
                {
                    c.SetInt("wheel", c.GetInt("wheel") + 1);
                    return c.GetInt("wheel") > 40 ? StepOutcome.Fail($"{Label(roster[idx])} 这一行滚不到可见区") : StepOutcome.Wait;
                }
                if (!t.value && !JourneyInput.ClickElement(t))
                {
                    return StepOutcome.Fail($"勾选 {Label(roster[idx])} 失败：{JourneyInput.LastUiFailure}");
                }
                c.SetInt("rosterIdx", idx + 1);
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < (roster.Count + 1) * 0.6)
            {
                return StepOutcome.Wait;
            }
            string summary = JourneyInput.FindUitk<Label>("[HomeValleyExpeditionPrepHost]", "SummaryLabel")?.text ?? string.Empty;
            string reasons = JourneyInput.FindUitk<Label>("[HomeValleyExpeditionPrepHost]", "ReasonsLabel")?.text ?? string.Empty;
            bool allOn = roster.All(id => FgjM1Journey.RowToggle(id)?.value == true);
            return allOn && reasons.Length == 0
                ? StepOutcome.Done($"勾选 {string.Join("、", roster.Select(Label))}：“{summary}”")
                : StepOutcome.Fail($"勾选后名单不对：全部勾上 {allOn}；“{summary}”“{reasons}”");
        }

        // ── 破碎都市：点选 + Shift 加选、右键敌人 ──────────────────────────────────────────

        internal static bool FcPos(int id, out Vector2 p) => FgjM1Journey.FcPos(id, out p);

        internal static void ClickFc(JourneyContext c, int logicId, bool additive)
        {
            c.SetInt("fcClickId", logicId);
            c.SetInt("fcAdd", additive ? 1 : 0);
            if (!FgjM1Journey.FcMarker(logicId, out HomeValleyMachineMarker m) || m.View == null)
            {
                c.Set("flyFirst", "missing");
                return;
            }
            if (JourneyInput.Holding || !JourneyCommon.PanToward(m.Position, 0.25f))
            {
                c.Set("flyFirst", "1");
                return;
            }
            c.Set("flyFirst", string.Empty);
            c.SetLong("clickedAtMs", NowMs());
            if (additive)
            {
                JourneyInput.ShiftClickWorld(m.View.transform.position);
            }
            else
            {
                JourneyInput.ClickWorld(m.View.transform.position);
            }
        }

        internal static StepOutcome TickFcSelection(JourneyContext c, params int[] ids)
        {
            if (c.StepElapsed < 0.8 || PanPending(c, cc => ClickFc(cc, cc.GetInt("fcClickId"), cc.GetInt("fcAdd") == 1)) || JustClicked(c))
            {
                return StepOutcome.Wait;
            }
            IReadOnlyList<int> sel = GameRoot.FracturedCity.SquadCommands.Selection;
            return sel.Count == ids.Length && ids.All(sel.Contains)
                ? StepOutcome.Done($"选择集 [{string.Join("、", sel.Select(Label))}]")
                : StepOutcome.Retry($"选择集是 [{string.Join(",", sel.Select(Label))}]，应为 [{string.Join(",", ids.Select(Label))}]");
        }

        /// <summary>离组合两台最近、在干扰场外的活敌人（按布局锚点与内核实时位置找，不写死）。</summary>
        internal static bool NearestEnemy(Vector2 from, out RegionEnemyRecord enemy, out Vector2 at, string exclude = null)
        {
            enemy = null;
            at = default;
            CombatSite site = GameRoot.FracturedCity?.Combat;
            float best = float.MaxValue;
            foreach (RegionEnemyRecord e in St.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
            {
                if (e == null || e.RegionId != FracturedCityLayout.RegionId || e.EnemyInstanceId == exclude || site == null || !site.IsEnemyAlive(e.EnemyInstanceId)
                    || !site.TryGetEnemyPosition(e.EnemyInstanceId, out Vector2 p))
                {
                    continue;
                }
                if (Vector2.Distance(p, FracturedCityLayout.ListeningNode.Position) <= FracturedCityLayout.JammerRadius + 2f)
                {
                    continue;
                }
                float d = Vector2.Distance(from, p);
                if (d < best)
                {
                    best = d;
                    enemy = e;
                    at = p;
                }
            }
            return enemy != null;
        }

        internal static void RightClickFcEnemy(JourneyContext c)
        {
            int a = c.GetInt("wetM");
            if (!FcPos(a, out Vector2 me) || !NearestEnemy(me, out RegionEnemyRecord e, out Vector2 p))
            {
                c.Set("enemy", string.Empty);
                return;
            }
            c.Set("enemy", e.EnemyInstanceId);
            if (JourneyInput.Holding || !JourneyCommon.PanToward(p, 0.3f))
            {
                c.Set("flyFirst", "1");
                return;
            }
            c.Set("flyFirst", string.Empty);
            MarkEvents(c, GameRoot.FracturedCity?.SquadCommands);
            JourneyInput.Click(p, button: 1);
            c.SetLong("clickedAtMs", NowMs());
        }

        /// <summary>
        /// FGJ-M2 第 4 步前半：在远征地点两台一起打同一个敌人，打出短路，远征场次里记下这条反应的伤害。
        /// 目标被打死而还没打出短路时（两台出手的先后不巧），像玩家一样再右键下一个最近的敌人（至多 5 个）。
        /// </summary>
        internal static StepOutcome TickFcConduct(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            SampleFrame();
            if (c.Get("enemy").Length == 0)
            {
                return StepOutcome.Fail("破碎都市里找不到干扰场外活着的敌人");
            }
            if (PanPending(c, RightClickFcEnemy))
            {
                return StepOutcome.Wait;
            }
            ReactionSessionRecord sess = ReactionAttribution.Current(St, FracturedCityLayout.RegionId, create: false);
            ReactionShareRecord row = sess?.Reactions?.FirstOrDefault(r => r.ReactionId == Conduct);
            if (row != null && row.Count >= 1 && row.Damage > 0)
            {
                return StepOutcome.Done($"远征场次“{ReactionAttribution.Title(sess)}”记下“{ConductName}” {row.Count} 次、反应伤害 {row.Damage:0.0} / 敌方受到全部伤害 {sess.TotalDamage:0.0}" +
                                        $"（本存档已经触发过：慢放次数不变 {GameClock.SlowMotionStarts - c.GetInt("slowAfterFirst")}）");
            }
            CombatSite site = GameRoot.FracturedCity.Combat;
            int a = c.GetInt("wetM");
            int b = c.GetInt("shockM");
            if (c.StepElapsed > 1.5 && !IsAttacking(site, a) && !IsAttacking(site, b))
            {
                // 目标死了（或没够着）：命令结束、不自己找下一个（玩家没要求）——像玩家一样右键下一个。
                int n = c.GetInt("reissue");
                if (n >= 5)
                {
                    return StepOutcome.Fail($"右键了 {n + 1} 个敌人都没打出短路（{ReactionFeedback.ReactionsSeen} 次反应）");
                }
                c.SetInt("reissue", n + 1);
                c.Log($"目标 {c.Get("enemy")} 已不在（两台的攻击命令结束、没有自己去找别的敌人），右键下一个最近的敌人");
                RightClickFcEnemy(c);
                c.SetLong("reissueAt", (long)(c.StepElapsed * 1000));
            }
            return StepOutcome.Wait;
        }

        // ── 统计面板（FGJ-M2 第 4 步后半）──────────────────────────────────────────────

        internal static StepOutcome TickStatsOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            StatsPanelUIToolkit p = StatsPanelUIToolkit.Instance;
            if (!StatsPanelUIToolkit.IsOpen || p == null || !p.PanelVisible)
            {
                return StepOutcome.Retry("点“统计”后统计面板没有打开：" + FgjM1Journey.UiFail(c));
            }
            // FG4-E2E-01（M4 出口回归）：FG4-ECO-08 起统计面板默认停在“生产”页签（ADR-ECO-008），反应伤害归因在“战斗”页签——像玩家一样先点“战斗”。
            if (p.CurrentTab != StatsTab.Combat)
            {
                if (c.GetInt("statsTabClicks") >= 3)
                {
                    return StepOutcome.Fail($"点了 3 次“战斗”页签仍停在 {p.CurrentTab}：{JourneyInput.LastUiFailure}");
                }
                c.SetInt("statsTabClicks", c.GetInt("statsTabClicks") + 1);
                JourneyInput.ClickUitk("[StatsPanelHost]", "StatsTabCombat");
                return StepOutcome.Wait;
            }
            var rows = new List<string>();
            for (int i = 0; i < p.VisibleRowCount; i++)
            {
                rows.Add(p.RowText(i));
            }
            string conductRow = rows.FirstOrDefault(r => r.Contains(ConductName) && r.Contains("%"));
            bool session = rows.Any(r => r.Contains(CombatSites.SiteName(FracturedCityLayout.RegionId)));
            return conductRow != null && session
                ? StepOutcome.Done($"统计面板“{p.SectionText}”：远征场次在列，“{conductRow}”（{p.CountText}）")
                : StepOutcome.Fail($"统计面板里没有短路的伤害归因：{string.Join(" / ", rows)}");
        }

        internal static StepOutcome TickStatsClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return !StatsPanelUIToolkit.IsOpen && PauseMenuUIToolkit.IsOpen ? StepOutcome.Done("点“关闭”：统计面板关闭，回到暂停菜单") : StepOutcome.Retry("统计面板还开着：" + FgjM1Journey.UiFail(c));
        }

        internal static StepOutcome TickPauseClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            return !PauseMenuUIToolkit.IsOpen ? StepOutcome.Done("按 Esc 关闭暂停菜单，世界继续") : StepOutcome.Retry("暂停菜单还开着");
        }

        internal static StepOutcome TickPauseOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            return PauseMenuUIToolkit.IsOpen ? StepOutcome.Done("按 Esc 打开暂停菜单（世界暂停）") : StepOutcome.Retry("按 Esc 后暂停菜单没有打开");
        }

        // ── 帧耗时（120 帧口径的参照，Editor batchmode 无图形设备）──────────────────────────

        private static readonly List<float> FrameMs = new List<float>(4096);
        private static int _lastFrame = -1;
        private static bool _sampling;

        internal static void ResetSampling()
        {
            FrameMs.Clear();
            _lastFrame = -1;
            _sampling = false;
        }

        internal static void BeginSampling() => _sampling = true;

        internal static void SampleFrame()
        {
            if (!_sampling || Time.frameCount == _lastFrame)
            {
                return;
            }
            _lastFrame = Time.frameCount;
            FrameMs.Add(Time.unscaledDeltaTime * 1000f);
        }

        internal static string FrameReport()
        {
            if (FrameMs.Count < 10)
            {
                return "帧耗时采样不足";
            }
            List<float> sorted = FrameMs.OrderBy(x => x).ToList();
            return $"组合攻击 / 反应段帧耗时（Editor batchmode 无图形设备，帧率上限 120，{sorted.Count} 帧）p50 {sorted[sorted.Count / 2]:F2} ms、" +
                   $"p95 {sorted[(int)(sorted.Count * 0.95)]:F2} ms、最大 {sorted[sorted.Count - 1]:F1} ms（120 帧预算 8.33 ms 仅作参照）";
        }
    }
}
