using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Analysis;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using UnityEngine;
using UnityEngine.UIElements;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FGJ-M5 研发段：黑匣子（家园机器阵亡，伤害夹具）→ 第 1 步裸跑与破解 → 第 4、5 步模拟 / 正式熔合与配方书 → 刻录台量产 → 混合固件装进机器蓝图 →
    /// 第 6 步从蓝图编辑器送到靶场、接入投影、两次测试对比 → 靶场测试中存档、读档后靶场空闲（FG05 第 5 章负向）。
    /// </summary>
    public static partial class FgjM5Journey
    {
        private static IEnumerable<JourneyStep> LabSteps() => new[]
        {
            // ── 黑匣子：家园机器阵亡（伤害夹具）→ 直接回收进陈列馆 ──
            S("bb_kill", "伤害夹具：冷却液机在家园被击毁（家园还没有正式突袭，FG6-DEF-04 / 08；伤害走生产代码的唯一伤害入口）：黑匣子直接回收、陈列馆开始分析", 20,
                ApplyDeathFixture, TickBlackBoxRecovered),

            // ── 第 1 步：带回的加密固件裸跑 → 送进解析台 → 破解后裸跑标记消失 ──
            S("sc_open2", "按信号核键打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
            S("sc_raw", "点 2 号槽、点基元仓里那枚未破解的固件、点“装入”：信号核 2 号槽装上它（裸跑）", 20, null,
                c => M.TickEquipChip(c, 1, () => c.Get("rawPart"), "未破解的固件装进信号核 2 号槽"), retries: 2),
            S("sc_raw_read", "信号核 2 号槽写着“▲未破解”（裸跑标记：每发增加暴露、积热更高）", 10, null, TickRawMarked),
            S("sc_close2", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),
            S("ana_open2", "左键点解析台打开解析面板", 20, FgjM2Common.OpenAnalysis, TickAnalysisOpen2, retries: 4),
            S("ana_chip", "“待解析”下拉选那枚未破解的固件芯片，点“送入解析”：排进队列（芯片只被引用、留在信号核里）", 15, null, TickEnqueueRawChip, retries: 1),
            S("ana_cracked", "解析台破解（耗电、按游戏时间）：固件变成已破解，芯片不消耗、仍在信号核 2 号槽，刻录台可以刻", 180, null, TickCracked),
            S("ana_close2", "点“关闭”关闭解析面板", 10, FgjM2Common.CloseAnalysis, FgjM2Common.TickAnalysisClosed, retries: 1),
            S("sc_open3", "按信号核键打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
            S("sc_unmarked", "信号核 2 号槽的“▲未破解”没了（同一枚芯片，不用重装）", 10, null, TickRawGone),
            S("sc_close3", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),

            // ── 第 4、5 步：模拟熔合（失败不消耗固件）→ 正式熔合 → 配方书 ──
            S("fu_open", "建造模式里左键点电路合成台，通用面板上点“熔合…”：合成台面板打开", 40, null,
                c => M.TickSubPanel(c, "synth", "PrFusion", () => FusionPanelUIToolkit.IsOpen && FusionPanelUIToolkit.Instance != null && FusionPanelUIToolkit.Instance.PanelVisible, "合成台面板打开"), retries: 2),
            S("fu_clue", "配方书里这条线索点“按这条线索选固件”：固件 A 选上线索点名的那条", 15, null, TickUseClue, retries: 1),
            S("fu_fail_pick", "固件 B 选一条和它没有配方的固件（过载）", 10, null, c => TickPickPartner(c, miss: true), retries: 1),
            S("fu_fail", "点“模拟熔合”：结果“无反应”，只花模拟费（技术数据），两条固件一枚不少", 10, null, c => TickSimulate(c, miss: true), retries: 1),
            S("fu_hit_pick", "固件 B 改选线索指向的另一条", 10, null, c => TickPickPartner(c, miss: false), retries: 1),
            S("fu_hit", "点“模拟熔合”：结果“有配方”，写出混合固件预览与正式熔合成本", 10, null, c => TickSimulate(c, miss: false), retries: 1),
            S("fu_formal", "点“正式熔合…”，确认框点“确认”：两枚父固件被预留、芯片基板 ×2 与技术数据取走记在任务上", 20, null, TickFormalQueued, retries: 1),
            S("fu_done", "熔合完成：两枚父固件消耗、产出一枚混合固件（进固件库）、配方记入配方书、刻录台解锁对应刻录蓝图", 300, null, TickFusionDone),
            S("fu_book", "配方书（合成台面板右栏）：已发现里有这条配方，“还剩几个未发现”少了一个", 15, null, TickRecipeBook),
            S("fu_close", "按 Esc 关闭合成台面板（通用面板也关）", 15, null, c => M.TickCloseAll(c, () => !FusionPanelUIToolkit.IsOpen, "合成台面板"), retries: 1),

            // ── 发现后在刻录台量产（芯片基板 ×3，不再消耗父固件）──
            S("burn_open", "建造模式里左键点固件刻录台：通用面板打开", 40, null, c => P.TickOpenPanel(c, M.Burner, P.P(c, "burner")), retries: 2),
            S("burn_pick", "刻录目标下拉选刚发现的混合固件：面板写“芯片基板 ×3”", 15, null, TickBurnTarget, retries: 1),
            S("burn_close", "点“关闭”关闭通用面板", 10, null, P.TickClosePanel, retries: 1),
            S("port_open2", "左键点仓库，通用面板上点“端口…”：打开端口面板", 20, null, c => FgjM3Common.TickPortOpen(c, HomeValleyLayout.BuildingTypeWarehouse), retries: 2),
            S("port_sub2", "仓库输出口的过滤下拉选“只输出芯片基板”（刻录台只收芯片基板，别的物品会把带堵住）：开始送料", 20, null, TickPortSubstrate, retries: 1),
            S("port_close2", "点“关闭”关闭端口面板（通用面板也关）", 10, c => FgjM1Journey.ClickUi(c, FgjM3Common.PortHost, "BeltPortClose"), TickPortClosed, retries: 1),
            S("build_close3", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("burn_chip", "仓库输出口只把芯片基板送上带、刻录台收齐 ×3 开刻：固件库多一枚混合固件，父固件一枚不动", 400, null, TickBurned),

            // ── 混合固件装进机器（ERC-003 蓝图）──
            S("cb_open3", "点“蓝图编辑器”入口", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "EntryToggleButton", when: () => !GameRoot.HomeValley.IsCircuitBoardPanelOpen),
                FgjM1Journey.TickEditorOpen, retries: 1),
            S("cb_pick3", "在蓝图列表点 ERC-003 蓝图", 10, FgjM1Journey.ClickErc003Row, FgjM1Journey.TickErc003Open, retries: 1),
            S("cb_mix", "固件槽 1 下拉选混合固件", 10, c => FgjM2Common.PickFirmwareSlot0(c, c.Get("mixId")), c => FgjM2Common.TickFirmwareSlot0(c, c.Get("mixId")), retries: 1),
            S("cb_save3", "点“保存”：ERC-003 新版本装着混合固件（之后出厂的机器带着它）", 10, c => FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "SaveButton"),
                c => FgjM2Common.TickSavedWithFirmware(c, c.Get("mixId"), "verMix"), retries: 1),

            // ── 第 6 步：在靶场投影新蓝图并接入测试；两次对比 ──
            S("rng_send", "蓝图编辑器里点“送到靶场测试”：编辑器收起、靶场面板打开，这张蓝图的投影出现在靶场里（不消耗任何资源）", 20,
                c => { RecordResources(c); FgjM1Journey.ClickUi(c, FgjM2Common.CircuitHost, "RangeSendButton"); }, TickRangeSent, retries: 1),
            S("rng_uplink", "投影那一行点“接入”：信号进入投影（按接入态重编译，信号核里的过载与已破解的固件生效）", 15, null, TickRangeUplinked, retries: 1),
            S("rng_run1", "测试 20 游戏秒：每秒伤害、反应次数、标签覆盖率、热量曲线、能耗实时刷新", 90, null, c => TickRangeRun(c, 20f)),
            S("rng_end1", "点“结束测试”：读数进测试记录（记“接入”），信号回到归还核心", 15, c => FgjM1Journey.ClickUi(c, M.RangeHost, "TestRangeEnd"), c => TickRangeEnded(c, "rt1", true), retries: 1),
            S("rng_proj2", "蓝图下拉仍是这张 ERC-003，点“投影”：对照组，不接入", 15, null, TickRangeProject, retries: 1),
            S("rng_run2", "测试 20 游戏秒", 90, null, c => TickRangeRun(c, 20f)),
            S("rng_end2", "点“结束测试”", 15, c => FgjM1Journey.ClickUi(c, M.RangeHost, "TestRangeEnd"), c => TickRangeEnded(c, "rt2", false), retries: 1),
            S("rng_compare", "对比栏两个下拉分别选这两次测试：并排写出每秒伤害、反应次数、覆盖率、能耗（接入 vs 不接入）", 15, null, TickRangeCompare, retries: 1),
            S("rng_proj3", "再点“投影”开第三次测试（下一步在测试中存档）", 15, null, TickRangeProject, retries: 1),
            S("rng_close", "按 Esc 关闭靶场面板（测试照常进行）", 15, null, c => M.TickCloseAll(c, () => !TestRangePanelUIToolkit.IsOpen, "靶场面板"), retries: 1),

            // ── 存读档（FG05 第 5 章：靶场测试中存档 → 测试结束、投影不进存档；读档后靶场空闲；研发域逐项一致）──
            S("save_esc", "按 Esc 打开暂停菜单（靶场还在测试）", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseForSave, retries: 1),
            S("save_quit", "点“保存并返回主菜单”，确认", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), FgjM1Journey.TickSaveConfirm),
            S("save_menu", "回到主菜单：存档里没有投影，靶场测试按“存档”结束进了测试记录；研究 / 熔合 / 情报 / 黑匣子 / 技术数据收支与存档那一刻逐项一致", 60, null, TickMenuAfterSave),
            S("save_load", "点“读取”，点刚才的存档槽", 20, c => FgjM1Journey.ClickUgui(c, "m_btn_Load"), FgjM1Journey.TickLoadSlot),
            S("save_loaded", "读档进入家园：靶场空闲（没有投影、没在测试），研发域第一帧逐项一致；读档后研究接着推进", 120, null, TickLoaded),
            S("speed3b", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),

            // ── 修复轮（审查 P2，DEBT-FG5RND04-05“装进机器”）：按装着混合固件的 ERC-003 新版本真正出厂一台，核对它的生效固件里有混合固件 ──
            S("fac_open3", "左键点装配站打开生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen),
                FgjM1Journey.TickFactoryOpen, retries: 2),
            S("fac_mix", "点“生产 ERC-003”（装着混合固件的新版本）", 10, FgjM2Common.ClickProduce, c => FgjM2Common.TickProduceQueued(c, "verMix"), retries: 1),
            S("fac_close3", "再点装配站关闭生产面板", 10, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen),
                FgjM1Journey.TickFactoryClosed, retries: 2),
            S("wait_mix", "等混合固件版 ERC-003 出厂：它的生效固件里有混合固件（废料不够时等回收站拆出来）", 600, null, TickMixProduced),
        };

        private static StepOutcome TickMixProduced(JourneyContext c)
        {
            StepOutcome o = FgjM2Common.TickProduced(c, "verMix", "mixM");
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            string mix = c.Get("mixId");
            GameRoot.HomeValley.Combat.TryGetMachineWeapon(c.GetInt("mixM"), out Campaign.Combat.MachineWeaponInfo info);
            bool fitted = info.FirmwareIds != null && info.FirmwareIds.Contains(mix);
            return fitted
                ? StepOutcome.Done($"{o.Message}；生效固件里有混合固件“{M.FwName(mix)}”（混合固件真正装进了出厂的机器）")
                : StepOutcome.Fail($"混合固件版 ERC-003 出厂了，生效固件里却没有“{M.FwName(mix)}”：{o.Message}");
        }

        // ── 黑匣子 ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 伤害夹具（DEBT-FG5RND06-03 已登记，承接 FG6-DEF-08）：家园还没有正式突袭，冷却液机（远征回来、已经不需要它）在家园被击毁。
        /// 伤害走生产代码的唯一伤害入口（<see cref="MachineRegistry.ApplyDamage"/>）；之后的黑匣子回收、通知、陈列馆分析、技术数据入账全是正式流程。
        /// </summary>
        private static void ApplyDeathFixture(JourneyContext c)
        {
            int victim = c.GetInt("wetM");
            c.SetInt("victim", victim);
            c.SetLong("bb.lab0", TechDataFlow.ExpenseOf(St, TechDataFlow.Lab));
            c.SetLong("bb.tick0", GameClock.Ticks);
            MachineRegistry.ApplyDamage(victim, 1e6f);
        }

        private static StepOutcome TickBlackBoxRecovered(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            int victim = c.GetInt("victim");
            BlackBoxState bb = BlackBoxService.StateOf(St);
            bool queued = bb?.Boxes?.Any(b => b != null && b.MachineLogicId == victim) ?? false;
            string who = MachineNaming.Short(victim);
            string note = NotificationCenter.History.Where(e => e.Type?.Id == "machine_destroyed").SelectMany(e => e.Members).Select(m => m.DetailText)
                .LastOrDefault(t => t != null && t.StartsWith(who + " ", StringComparison.Ordinal)) ?? string.Empty;
            bool alive = MachineRegistry.TryGetRecord(victim, out MachineRecord r) && r.IsAlive;
            return !alive && queued && note.Contains("黑匣子") && note.Contains(M.Name(M.Gallery))
                ? StepOutcome.Done($"{who} 被击毁：阵亡通知“{note}”；黑匣子直接回收、排进陈列馆（{bb.Boxes.Length} 个）")
                : (c.StepElapsed < 5 ? StepOutcome.Wait : StepOutcome.Fail($"阵亡后：还活着 {alive}、进了陈列馆队列 {queued}、通知“{note}”"));
        }

        // ── 裸跑与破解 ────────────────────────────────────────────────────────────────

        private static string RawTag => " " + GameText.Get("signal.core.raw_tag");

        private static StepOutcome TickRawMarked(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            string fw = c.Get("rawFw");
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            string slot = hud?.SlotText(1) ?? string.Empty;
            bool marked = FirmwareKinds.IsRaw(St, fw) && slot.Contains(GameText.Get("signal.core.raw_tag")) && FirmwareKinds.RawTagSuffix(St, fw) == RawTag
                          && SignalCoreService.SlotContentId(St, 1) == fw;
            c.Set("rawSlotText", slot);
            return marked
                ? StepOutcome.Done($"信号核 2 号槽“{slot.Replace("\n", " ")}”：带着“{GameText.Get("signal.core.raw_tag")}”标记（裸跑）")
                : StepOutcome.Fail($"2 号槽没有裸跑标记：“{slot}”（未破解 {FirmwareKinds.IsRaw(St, fw)}、槽里 {SignalCoreService.SlotContentId(St, 1)}）");
        }

        private static StepOutcome TickAnalysisOpen2(JourneyContext c)
        {
            if (c.StepElapsed < 0.8 || FgjM2Common.PanPending(c, FgjM2Common.OpenAnalysis) || FgjM2Common.JustClicked(c))
            {
                return StepOutcome.Wait;
            }
            AnalysisPanelUIToolkit p = AnalysisPanelUIToolkit.Instance;
            return GameRoot.HomeValley.IsAnalysisPanelOpen && p != null
                ? StepOutcome.Done($"解析面板打开：“{p.StatusText}”；待解析 {p.HeldChoices.Count} 行")
                : StepOutcome.Retry("点解析台后解析面板没有打开" + (FgjM2Common.PanNote(c).Length > 0 ? "：" + FgjM2Common.PanNote(c) : string.Empty));
        }

        private static StepOutcome TickEnqueueRawChip(JourneyContext c)
        {
            AnalysisPanelUIToolkit p = AnalysisPanelUIToolkit.Instance;
            string part = c.Get("rawPart");
            if (p == null || !GameRoot.HomeValley.IsAnalysisPanelOpen)
            {
                return StepOutcome.Fail("解析面板没开");
            }
            if (!FgjM3Common.Done(c, "pick"))
            {
                int idx = -1;
                for (int i = 0; i < p.HeldChoices.Count; i++)
                {
                    if (p.HeldChoices[i].Source == HomeValleyAnalysis.SourceChip && p.HeldChoices[i].ChipPartId == part)
                    {
                        idx = i;
                    }
                }
                DropdownField dd = JourneyInput.FindUitk<DropdownField>(FgjM2Common.AnalysisHost, "WarehouseDropdown");
                if (idx < 0 || dd == null || idx >= dd.choices.Count)
                {
                    return c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Fail($"待解析下拉里没有这枚芯片（{p.HeldChoices.Count} 行）");
                }
                if (!JourneyInput.IsClickable(dd))
                {
                    return StepOutcome.Retry("待解析下拉点不到");
                }
                dd.value = dd.choices[idx];
                c.Set(FgjM3Common.SK(c, "label"), dd.choices[idx]);
                c.SetInt(FgjM3Common.SK(c, "q0"), St.AnalysisQueues?.Length ?? 0);
                c.SetLong("ana.inc0", TechDataFlow.IncomeOf(St, TechDataFlow.Analysis)); // 修复轮：破解带来的技术数据单独对账（统计页“解析台”收入）
                FgjM3Common.Mark(c, "pick");
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, "enq", () => JourneyInput.ClickUitk(FgjM2Common.AnalysisHost, "EnqueueButton")))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "enq") < 600)
            {
                return StepOutcome.Wait;
            }
            AnalysisQueueItemRecord q = St.AnalysisQueues?.LastOrDefault(x => x != null && x.Source == HomeValleyAnalysis.SourceChip && x.TargetId == c.Get("rawFw"));
            bool stillInSlot = SignalCoreService.SlotChip(St, 1)?.PartId == part;
            return q != null && stillInSlot
                ? StepOutcome.Done($"选“{c.Get(FgjM3Common.SK(c, "label"))}”点“送入解析”：排进解析队列（{HomeValleyAnalysis.StateText(q.State)}，{q.Duration:0.#} 秒）；芯片仍在信号核 2 号槽（只被引用、不取走）；预览“{p.HeldPreviewText}”")
                : StepOutcome.Retry($"点了“送入解析”后队列里没有这枚芯片（芯片还在槽里 {stillInSlot}；“{p.ResultText}”）");
        }

        private static StepOutcome TickCracked(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            string fw = c.Get("rawFw");
            string part = c.Get("rawPart");
            if (FirmwareKinds.IsRaw(St, fw))
            {
                return StepOutcome.Wait;
            }
            PrimitiveChipRecord chip = PrimitiveInventory.Find(St, part);
            bool printable = SignalCoreService.PrintableFirmware(St).Contains(fw);
            long crack = TechDataFlow.IncomeOf(St, TechDataFlow.Analysis) - c.GetLong("ana.inc0");
            c.SetLong("ana.crack", crack);
            return chip != null && SignalCoreService.SlotChip(St, 1)?.PartId == part && FirmwareKinds.RawTagSuffix(St, fw).Length == 0
                ? StepOutcome.Done($"解析台破解完成：“{M.FwName(fw)}”已破解，芯片 {part} 没被消耗、仍在信号核 2 号槽；刻录{(printable ? "可以" : "不可以")}量产；解析结果“{AnalysisPanelUIToolkit.Instance?.ResultText}”；" +
                                   $"统计“解析台”收入 +{crack}")
                : StepOutcome.Fail($"破解后：芯片还在 {chip != null}、仍在 2 号槽 {SignalCoreService.SlotChip(St, 1)?.PartId == part}、标记后缀“{FirmwareKinds.RawTagSuffix(St, fw)}”");
        }

        private static StepOutcome TickRawGone(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            string slot = SignalCoreHudUIToolkit.Instance?.SlotText(1) ?? string.Empty;
            return !slot.Contains(GameText.Get("signal.core.raw_tag")) && slot.Length > 0
                ? StepOutcome.Done($"信号核 2 号槽“{slot.Replace("\n", " ")}”：裸跑标记消失（破解前是“{c.Get("rawSlotText").Replace("\n", " ")}”）")
                : StepOutcome.Fail($"破解后 2 号槽仍写着“{slot}”");
        }

        // ── 熔合 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickUseClue(JourneyContext c)
        {
            FusionPanelUIToolkit p = M.Fusion();
            if (p == null || !FusionPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail("合成台面板没开");
            }
            string known = c.Get("clue.known");
            if (!FgjM3Common.Done(c, "use"))
            {
                int row = -1;
                for (int i = 0; i < p.ClueRowCount; i++)
                {
                    if (p.ClueRowText(i).Contains(M.FwName(known)))
                    {
                        row = i;
                    }
                }
                if (row < 0)
                {
                    return c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Fail($"配方书线索栏里没有点名“{M.FwName(known)}”的线索（{p.ClueRowCount} 行）");
                }
                c.Set(FgjM3Common.SK(c, "row"), p.ClueRowText(row));
                bool? clicked = P.ClickInView(p.ClueUseButton(row));
                if (clicked == null)
                {
                    return StepOutcome.Wait;
                }
                if (clicked == false)
                {
                    return M.UiRetry("点不到“按这条线索选固件”：");
                }
                FgjM3Common.Mark(c, "use");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            p = M.Fusion();
            return p.SelectedA == known || p.SelectedB == known
                ? StepOutcome.Done($"线索“{c.Get(FgjM3Common.SK(c, "row")).Replace("\n", " ")}”点“按这条线索选固件”：固件 A = {M.FwName(p.SelectedA)}、B = {M.FwName(p.SelectedB)}")
                : StepOutcome.Retry($"点了之后固件 A / B 是 {p.SelectedA} / {p.SelectedB}");
        }

        /// <summary>失败组：在固件库里有、已破解、不是混合固件、和线索点名的固件没有配方的那一条（过载优先）。命中组：线索配方的另一条父固件。</summary>
        private static string Partner(JourneyContext c, bool miss)
        {
            string known = c.Get("clue.known");
            if (!miss)
            {
                return c.Get("clue.other");
            }
            foreach (string fw in new[] { FirmwareCatalog.FwOverloadId, FirmwareCatalog.FwHomingId, FgjM2Common.FwWet, FgjM2Common.FwShock })
            {
                if (fw != known && M.ChipCount(fw) > 0 && !FirmwareKinds.IsRaw(St, fw) && !FusionCatalog.TryGetByPair(known, fw, out _))
                {
                    return fw;
                }
            }
            return null;
        }

        private static StepOutcome TickPickPartner(JourneyContext c, bool miss)
        {
            string fw = Partner(c, miss);
            if (fw == null)
            {
                return StepOutcome.Fail("固件库里找不到能和线索固件配对的另一条");
            }
            FusionPanelUIToolkit p = M.Fusion();
            bool slotA = p.SelectedA != c.Get("clue.known");
            if ((slotA ? p.SelectedA : p.SelectedB) == fw)
            {
                return c.StepElapsed < 0.5 ? StepOutcome.Wait : StepOutcome.Done($"固件 {(slotA ? "A" : "B")} 选“{M.FwName(fw)}”（{(miss ? "和线索固件没有配方" : "线索配方的另一条父固件")}）");
            }
            if (!M.PickParent(slotA, fw, out string why))
            {
                return why == "滚动中" ? StepOutcome.Wait : StepOutcome.Retry(why);
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickSimulate(JourneyContext c, bool miss)
        {
            FusionPanelUIToolkit p = M.Fusion();
            string a = p.SelectedA;
            string b = p.SelectedB;
            if (!FgjM3Common.Done(c, "sim"))
            {
                c.SetInt(FgjM3Common.SK(c, "tech0"), St.TechData);
                c.SetInt(FgjM3Common.SK(c, "ca"), M.ChipCount(a));
                c.SetInt(FgjM3Common.SK(c, "cb"), M.ChipCount(b));
                c.SetInt(FgjM3Common.SK(c, "sims0"), FusionService.StateOf(St)?.Simulations ?? 0);
                if (!JourneyInput.ClickElement(p.SimulateButton))
                {
                    return M.UiRetry("点不到“模拟熔合”：");
                }
                FgjM3Common.Mark(c, "sim");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            FusionState f = FusionService.StateOf(St);
            int cost = FusionCatalog.SimTech;
            int spent = c.GetInt(FgjM3Common.SK(c, "tech0")) - St.TechData;
            bool chipsSame = M.ChipCount(a) == c.GetInt(FgjM3Common.SK(c, "ca")) && M.ChipCount(b) == c.GetInt(FgjM3Common.SK(c, "cb"));
            bool ran = f != null && f.Simulations == c.GetInt(FgjM3Common.SK(c, "sims0")) + 1;
            string result = p.ResultText.Replace("\n", " / ");
            if (miss)
            {
                bool noReaction = !FusionCatalog.TryGetByPair(a, b, out _) && (FusionService.StateOf(St)?.SimulationMisses ?? 0) > 0;
                c.SetInt("fu.missTech", spent);
                // 回收站同时在往家园送废料、实验室在取技术数据：技术数据只看“至少花了模拟费、不多于模拟费 + 实验室这几秒取的”。
                return ran && chipsSame && noReaction && spent >= cost && !p.FormalButton.enabledSelf
                    ? StepOutcome.Done($"模拟“{M.FwName(a)} + {M.FwName(b)}”：“{result}”；技术数据 −{spent}（模拟费 {cost}），两条固件一枚不少（{M.ChipCount(a)} / {M.ChipCount(b)} 枚）；“正式熔合…”不能点")
                    : StepOutcome.Fail($"失败组模拟不对：跑了 {ran}、固件不变 {chipsSame}、结果“{result}”、技术数据 −{spent}、正式按钮 {p.FormalButton.enabledSelf}");
            }
            bool hit = FusionCatalog.TryGetByPair(a, b, out FusionRecipeDef def) && def.Id == c.Get("clue.recipe");
            return ran && chipsSame && hit && spent >= cost && p.FormalButton.enabledSelf
                ? StepOutcome.Done($"模拟“{M.FwName(a)} + {M.FwName(b)}”：“{result}”；技术数据 −{spent}；两条固件仍一枚不少；“正式熔合…”可以点了")
                : StepOutcome.Fail($"命中组模拟不对：跑了 {ran}、固件不变 {chipsSame}、配方 {hit}、结果“{result}”、正式按钮 {p.FormalButton.enabledSelf}");
        }

        private static StepOutcome TickFormalQueued(JourneyContext c)
        {
            FusionPanelUIToolkit p = M.Fusion();
            string synthId = P.Bld(c, "synth")?.BuildingId;
            if (!FgjM3Common.Done(c, "formal"))
            {
                c.SetInt("fu.jobs0", FusionService.JobsOf(St, synthId).Count());
                c.SetInt("fu.sub0", HomeInventory.Stock(St, M.Substrate));
                c.SetInt("fu.tech0", St.TechData);
                if (!JourneyInput.ClickElement(p.FormalButton))
                {
                    return M.UiRetry("点不到“正式熔合…”：");
                }
                FgjM3Common.Mark(c, "formal");
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Done(c, "ok"))
            {
                if (c.StepElapsed < 0.6)
                {
                    return StepOutcome.Wait;
                }
                if (!UiConfirmDialog.IsOpen || UiConfirmDialog.Current?.Title != GameText.Get("fusion.confirm.title"))
                {
                    return StepOutcome.Retry("点“正式熔合…”后没有弹出确认框：" + p.MessageText);
                }
                c.Set("fu.confirm", string.Join(" / ", UiConfirmDialog.Current.Lines));
                if (!JourneyInput.ClickUitk(M.OverlayHost, "ConfirmOk"))
                {
                    return M.UiRetry("确认框点“确认”失败：");
                }
                FgjM3Common.Mark(c, "ok");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            FusionJobRecord job = FusionService.JobsOf(St, synthId).OrderByDescending(j => j.Serial).FirstOrDefault(); // 最新的一项（JobsOf 把进行中的排前面）
            if (job == null || FusionService.JobsOf(St, synthId).Count() <= c.GetInt("fu.jobs0"))
            {
                return StepOutcome.Retry("确认后队列里没有这项熔合：" + p.MessageText);
            }
            int reserved = St.PrimitiveChips?.Count(x => x != null && x.ReservedByTransactionId == job.JobId) ?? 0;
            c.Set("fu.job", job.JobId);
            c.SetInt("fu.jobTech", job.Tech);
            c.Set("fu.pa", M.DefOf(job.PartA));
            c.Set("fu.pb", M.DefOf(job.PartB));
            c.SetInt("fu.ca", M.ChipCount(c.Get("fu.pa")));
            c.SetInt("fu.cb", M.ChipCount(c.Get("fu.pb")));
            return reserved == 2 && job.Substrate == FusionCatalog.FormalSubstrate && HomeInventory.Stock(St, M.Substrate) == c.GetInt("fu.sub0") - job.Substrate
                ? StepOutcome.Done($"确认框“{c.Get("fu.confirm")}”点“确认”：入队（{FusionService.StateText(job)}，{job.Duration:0.#} 秒）；两枚父固件被这项预留、芯片基板 {job.Substrate} 件与技术数据 {job.Tech} 记在任务上")
                : StepOutcome.Fail($"入队后：预留 {reserved} 枚、任务上芯片基板 {job.Substrate}、仓库芯片基板 {c.GetInt("fu.sub0")} → {HomeInventory.Stock(St, M.Substrate)}");
        }

        private static StepOutcome TickFusionDone(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FusionJobRecord job = FusionService.StateOf(St)?.Jobs?.FirstOrDefault(j => j != null && j.JobId == c.Get("fu.job"));
            if (job == null)
            {
                return StepOutcome.Fail("熔合任务不见了");
            }
            if (job.State == FusionJobState.Cancelled || job.State == FusionJobState.RolledBack)
            {
                return StepOutcome.Fail($"熔合 {job.State}：{job.Reason}");
            }
            if (job.State != FusionJobState.Done)
            {
                return StepOutcome.Wait;
            }
            string mix = c.Get("mixId");
            bool discovered = FusionService.IsDiscovered(St, c.Get("clue.recipe"));
            bool burnable = SignalCoreService.PrintableFirmware(St, includeMixed: true).Contains(mix);
            int ca = M.ChipCount(c.Get("fu.pa"));
            int cb = M.ChipCount(c.Get("fu.pb"));
            bool consumed = ca == c.GetInt("fu.ca") - 1 && cb == c.GetInt("fu.cb") - 1;
            bool output = M.ChipCount(mix) == 1 && PrimitiveInventory.Find(St, job.OutputPartId)?.CardDefId == mix;
            c.SetInt("mix.after", M.ChipCount(mix));
            return discovered && burnable && consumed && output
                ? StepOutcome.Done($"熔合完成：产出一枚“{M.FwName(mix)}”（{job.OutputPartId}，进固件库），两枚父固件各消耗一枚（{M.FwName(c.Get("fu.pa"))} {ca}、{M.FwName(c.Get("fu.pb"))} {cb}）；配方记入配方书；固件刻录台可以刻它")
                : StepOutcome.Fail($"熔合完成后：发现 {discovered}、可刻录 {burnable}、父固件消耗 {consumed}（{ca} / {cb}）、产出 {output}");
        }

        private static StepOutcome TickRecipeBook(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            FusionPanelUIToolkit p = M.Fusion();
            FusionCatalog.TryGet(c.Get("clue.recipe"), out FusionRecipeDef def);
            string name = def != null ? GameText.Get(def.NameKey) : "?";
            int remaining = FusionService.Remaining(St, null);
            bool ok = p != null && p.BookText.Contains(name) && remaining == FusionCatalog.Recipes.Count - (FusionService.StateOf(St)?.Discovered?.Length ?? 0)
                      && p.RemainingText.Length > 0;
            return ok
                ? StepOutcome.Done($"配方书：已发现“{name}”；“{p.RemainingText.Split('\n')[0]}”（全部 {FusionCatalog.Recipes.Count} 条、还剩 {remaining} 条未发现）")
                : StepOutcome.Fail($"配方书不对：已发现“{p?.BookText?.Replace("\n", " / ")}”、剩余“{p?.RemainingText}”");
        }

        // ── 刻录台量产 ────────────────────────────────────────────────────────────────

        private static StepOutcome TickBurnTarget(JourneyContext c)
        {
            ProductionPanelUIToolkit panel = ProductionPanelUIToolkit.Instance;
            string mix = c.Get("mixId");
            if (!ProductionPanelUIToolkit.IsOpen || panel == null)
            {
                return StepOutcome.Fail("通用面板没开");
            }
            if (!ProductionService.TryGet(St, ProductionPanelUIToolkit.BuildingId, out ProductionService.Producer pr) || !pr.IsBurner)
            {
                return StepOutcome.Fail("面板上的建筑不是固件刻录台");
            }
            if (pr.Rec.BurnTarget == mix)
            {
                c.SetInt("burn.sub0", HomeInventory.Stock(St, M.Substrate));
                c.SetInt("burn.mix0", M.ChipCount(mix));
                c.SetInt("burn.pa0", M.ChipCount(c.Get("fu.pa")));
                c.SetInt("burn.pb0", M.ChipCount(c.Get("fu.pb")));
                c.SetLong("burn.done0", pr.Rec.Completed);
                return StepOutcome.Done($"刻录目标选“{M.FwName(mix)}”：{panel.RecipeLineText.Replace("\n", " ")}（状态“{panel.StateText}”）");
            }
            DropdownField d = panel.BurnField;
            if (d == null || !panel.BurnRowVisible)
            {
                return StepOutcome.Fail("通用面板上没有刻录目标下拉");
            }
            bool? ok = P.PickableInView(d);
            if (ok == null)
            {
                return StepOutcome.Wait;
            }
            string want = d.choices.FirstOrDefault(x => x != null && x.StartsWith(M.FwName(mix), StringComparison.Ordinal));
            if (ok == false || want == null)
            {
                return StepOutcome.Retry($"刻录目标下拉点不到或没有“{M.FwName(mix)}”（{string.Join("、", d.choices)}）");
            }
            if (NowMsPick(c))
            {
                d.value = want;
            }
            return StepOutcome.Wait;
        }

        private static bool NowMsPick(JourneyContext c)
        {
            string k = FgjM3Common.SK(c, "pickAt");
            if (M.NowMs() - c.GetLong(k) < 600)
            {
                return false;
            }
            c.SetLong(k, M.NowMs());
            return true;
        }

        private static StepOutcome TickBurned(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            string mix = c.Get("mixId");
            BuildingRecord b = P.Bld(c, "burner");
            ProductionService.Producer pr = P.Prod(b);
            if (pr == null)
            {
                return StepOutcome.Fail("固件刻录台不见了");
            }
            if (M.ChipCount(mix) <= c.GetInt("burn.mix0"))
            {
                if ((int)(c.StepElapsed / 30) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 30));
                    c.Log($"刻录台：{ProductionService.StateText(pr)}（{ProductionService.ReasonText(St, pr)}）；仓库芯片基板 {HomeInventory.Stock(St, M.Substrate)}");
                }
                return StepOutcome.Wait;
            }
            int used = c.GetInt("burn.sub0") - HomeInventory.Stock(St, M.Substrate);
            bool parentsKept = M.ChipCount(c.Get("fu.pa")) == c.GetInt("burn.pa0") && M.ChipCount(c.Get("fu.pb")) == c.GetInt("burn.pb0");
            return used >= FusionCatalog.BurnSubstrate && parentsKept
                ? StepOutcome.Done($"刻录台刻出一枚“{M.FwName(mix)}”（固件库里现在 {M.ChipCount(mix)} 枚）：仓库输出口送上带的芯片基板用掉 {used} 件（每枚 {FusionCatalog.BurnSubstrate}），父固件一枚没动")
                : StepOutcome.Fail($"量产后：芯片基板用掉 {used}（应 ≥ {FusionCatalog.BurnSubstrate}）、父固件不变 {parentsKept}");
        }

        // ── 靶场 ──────────────────────────────────────────────────────────────────────

        private static string RangeId(JourneyContext c) => P.Bld(c, "range")?.BuildingId;

        private static void RecordResources(JourneyContext c)
        {
            c.SetInt("rng.tech0", St.TechData - (int)TechDataFlow.IncomeOf(St, TechDataFlow.BlackBox) + (int)TechDataFlow.ExpenseOf(St, TechDataFlow.Lab));
            c.SetInt("rng.machines0", St.MachineRecords?.Length ?? 0);
            c.SetInt("rng.sub0", HomeInventory.Stock(St, M.Substrate));
            c.SetInt("rng.hist0", St.Research.Range.History.Length);
        }

        private static StepOutcome TickRangeSent(JourneyContext c)
        {
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            string rid = RangeId(c);
            TestRangePanelUIToolkit panel = TestRangePanelUIToolkit.Instance;
            panel?.Refresh(force: true);
            bool running = TestRangeService.IsRunning(rid);
            int techNow = St.TechData - (int)TechDataFlow.IncomeOf(St, TechDataFlow.BlackBox) + (int)TechDataFlow.ExpenseOf(St, TechDataFlow.Lab);
            bool free = techNow == c.GetInt("rng.tech0") && (St.MachineRecords?.Length ?? 0) == c.GetInt("rng.machines0") && HomeInventory.Stock(St, M.Substrate) == c.GetInt("rng.sub0");
            bool notInSites = Campaign.Combat.CombatSites.Get(TestRangeService.SitePrefix + rid) == null;
            if (!(TestRangePanelUIToolkit.IsOpen && running && panel != null && panel.ProjectionRowCount == 1 && !GameRoot.HomeValley.IsCircuitBoardPanelOpen))
            {
                return StepOutcome.Retry($"点“送到靶场测试”后：靶场面板 {TestRangePanelUIToolkit.IsOpen}、测试中 {running}、投影 {panel?.ProjectionRowCount} 个、编辑器还开着 {GameRoot.HomeValley.IsCircuitBoardPanelOpen}（{FgjM1Journey.UiFail(c)}）");
            }
            return free && notInSites
                ? StepOutcome.Done($"点“送到靶场测试”：编辑器收起、靶场面板打开（“{panel.TitleText}”），投影“{panel.ProjectionRowText(0)}”在靶场里打靶；技术数据（扣掉同时入账 / 实验室取用的）、芯片基板、机器数都没变；投影不登记进战斗地点表（不进存档）")
                : StepOutcome.Fail($"投影花了资源或进了地点表：资源不变 {free}、不在地点表 {notInSites}");
        }

        private static StepOutcome TickRangeUplinked(JourneyContext c)
        {
            TestRangePanelUIToolkit panel = TestRangePanelUIToolkit.Instance;
            string rid = RangeId(c);
            if (TestRangeService.TryGetUplinkedProjection(out string bid, out int serial, out string label) && bid == rid)
            {
                string hud = SignalCoreHudUIToolkit.Instance?.LocationText ?? string.Empty;
                if (c.StepElapsed < 0.8 || (!hud.Contains(label) && c.StepElapsed < 3))
                {
                    return StepOutcome.Wait;
                }
                return hud.Contains(label)
                    ? StepOutcome.Done($"投影那一行点“接入”：信号进入投影“{label}”（HUD“{hud}”/“{SignalUplinkService.StatusLine(St)}”）；行写“{panel?.ProjectionRowText(0)}”")
                    : StepOutcome.Fail($"接入投影后信号位置 HUD 还写“{hud}”（应写投影“{label}”）");
            }
            if (!FgjM3Common.Once(c, "up", () => JourneyInput.ClickElement(panel?.ProjectionUplinkButton(0))))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "up") < 1500 ? StepOutcome.Wait : StepOutcome.Retry($"点“接入”后信号没进投影（“{panel?.MessageText}”）");
        }

        private static StepOutcome TickRangeRun(JourneyContext c, float seconds)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            string rid = RangeId(c);
            if (!TestRangeService.IsRunning(rid))
            {
                return StepOutcome.Fail("测试提前结束了");
            }
            float t = TestRangeService.ElapsedSeconds(rid);
            if (t < seconds)
            {
                return StepOutcome.Wait;
            }
            TestRangePanelUIToolkit panel = TestRangePanelUIToolkit.Instance;
            panel?.Refresh(force: true);
            string readings = panel?.ReadingsText ?? string.Empty;
            return readings.Length > 10
                ? StepOutcome.Done($"测试 {t:F0} 游戏秒：“{panel?.RunStateText}”；读数“{readings.Replace("\n", " / ")}”")
                : StepOutcome.Fail($"读数没刷新：“{readings}”");
        }

        private static StepOutcome TickRangeEnded(JourneyContext c, string key, bool uplinked)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            string rid = RangeId(c);
            RangeResultRecord last = St.Research.Range.History.LastOrDefault();
            if (TestRangeService.IsRunning(rid) || last == null)
            {
                return StepOutcome.Retry($"点“结束测试”后还在测试（{TestRangePanelUIToolkit.Instance?.MessageText}）");
            }
            c.SetInt(key, last.Serial);
            bool signalHome = !TestRangeService.TryGetUplinkedProjection(out _, out _, out _);
            return last.EndReason == (int)RangeEndReason.Player && last.Uplinked == uplinked && last.Seconds > 5f && signalHome
                ? StepOutcome.Done($"测试结束：记录 #{last.Serial}（{last.Seconds:F1} 秒、每秒伤害 {last.Dps:F1}、伤害 {last.Damage:F0}、发数 {last.Shots}、能耗 {last.Energy:F1}、反应 {last.Reactions.Length} 种、" +
                                   $"{(last.Uplinked ? "接入" : "未接入")}）；结束原因“{TestRangeService.EndReasonText(last.EndReason)}”；信号回到归还核心")
                : StepOutcome.Fail($"测试记录不对：原因 {last.EndReason}、接入 {last.Uplinked}（应 {uplinked}）、时长 {last.Seconds:F1}、信号回核心 {signalHome}");
        }

        private static StepOutcome TickRangeProject(JourneyContext c)
        {
            string rid = RangeId(c);
            TestRangePanelUIToolkit panel = TestRangePanelUIToolkit.Instance;
            if (TestRangeService.IsRunning(rid))
            {
                return c.StepElapsed < 0.8 ? StepOutcome.Wait : StepOutcome.Done($"点“投影”：“{panel?.ProjectionRowText(0)}”在靶场里打靶（测试开始）");
            }
            if (!TestRangePanelUIToolkit.IsOpen || panel == null)
            {
                return StepOutcome.Fail("靶场面板没开");
            }
            string bpName = HomeValleyFactory.BlueprintDisplayName(St, HomeValleyLayout.BlueprintErc003Id);
            DropdownField dd = panel.BlueprintField;
            if (!FgjM3Common.Done(c, "bp"))
            {
                int idx = dd?.choices?.FindIndex(x => x != null && x.Contains(bpName)) ?? -1;
                if (idx < 0 || !JourneyInput.IsClickable(dd))
                {
                    return StepOutcome.Retry($"蓝图下拉里没有“{bpName}”或点不到（{string.Join("、", dd?.choices ?? new List<string>())}）");
                }
                if (dd.index != idx)
                {
                    dd.value = dd.choices[idx];
                }
                FgjM3Common.Mark(c, "bp");
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, "proj", () => JourneyInput.ClickUitk(M.RangeHost, "TestRangeProject")))
            {
                return StepOutcome.Wait;
            }
            return FgjM3Common.SinceMs(c, "proj") < 1500 ? StepOutcome.Wait : StepOutcome.Retry($"点“投影”后没开始测试（“{panel.MessageText}”）");
        }

        private static StepOutcome TickRangeCompare(JourneyContext c)
        {
            TestRangePanelUIToolkit panel = TestRangePanelUIToolkit.Instance;
            if (panel == null || !TestRangePanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail("靶场面板没开");
            }
            int a = c.GetInt("rt1");
            int b = c.GetInt("rt2");
            IReadOnlyList<RangeResultRecord> history = TestRangeService.History(St);
            int IndexOf(int serial)
            {
                int k = 0;
                for (int i = history.Count - 1; i >= 0; i--)
                {
                    if (history[i] == null)
                    {
                        continue;
                    }
                    if (history[i].Serial == serial)
                    {
                        return k;
                    }
                    k++;
                }
                return -1;
            }
            // 面板对比的是哪两次：没选过时默认最近两次（TestRangeService.ComparePair），下拉里显示的值已经是它的话再选一次不会触发变更——按实际对比的那一对判断。
            bool pair = TestRangeService.ComparePair(St, out RangeResultRecord ra, out RangeResultRecord rb);
            int curA = pair ? ra.Serial : 0;
            int curB = pair ? rb.Serial : 0;
            if (curA == a && curB == b)
            {
                string text = panel.CompareText;
                return c.StepElapsed > 0.6 && text.Length > 0
                    ? StepOutcome.Done($"对比栏选 #{a}（接入）与 #{b}（未接入）：“{text.Replace("\n", " / ")}”")
                    : StepOutcome.Wait;
            }
            if (NowMsPick(c))
            {
                DropdownField fa = panel.CompareField(true);
                DropdownField fb = panel.CompareField(false);
                int ia = IndexOf(a);
                int ib = IndexOf(b);
                if (ia < 0 || ib < 0 || !JourneyInput.IsClickable(fa) || !JourneyInput.IsClickable(fb))
                {
                    return StepOutcome.Retry($"对比下拉点不到或找不到这两次测试（#{a} {ia}、#{b} {ib}）");
                }
                if (curA != a)
                {
                    fa.value = fa.choices[ia];
                }
                else if (curB != b)
                {
                    fb.value = fb.choices[ib];
                }
            }
            return StepOutcome.Wait;
        }

        // ── 存读档 ────────────────────────────────────────────────────────────────────

        private static StepOutcome TickPauseForSave(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (!PauseMenuUIToolkit.IsOpen)
            {
                return StepOutcome.Retry("按 Esc 后暂停菜单没有打开");
            }
            if (!TestRangeService.IsRunning(RangeId(c)))
            {
                return StepOutcome.Fail("存档前靶场应当在测试");
            }
            c.Set("pre", M.Digest(St, true));
            c.Set("preStable", M.Digest(St, false));
            c.SetLong("preTicks", GameClock.Ticks);
            c.SetInt("preHist", St.Research.Range.History.Length);
            return StepOutcome.Done($"暂停菜单打开（世界暂停），靶场在测试；存档前研发域：{c.Get("pre")}");
        }

        private static StepOutcome TickMenuAfterSave(JourneyContext c)
        {
            if (JourneyInput.FindActiveButton("m_btn_Load") == null || c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            LoadResult onDisk = CampaignSaveService.Load(c.GetInt("slot"));
            if (!onDisk.Success)
            {
                return StepOutcome.Fail($"刚保存的存档读不出来：{onDisk.Outcome}/{onDisk.Reason}");
            }
            CampaignState disk = onDisk.State;
            RangeResultRecord last = disk.Research?.Range?.History?.LastOrDefault();
            bool rangeOk = (disk.Research?.Range?.History?.Length ?? 0) == c.GetInt("preHist") + 1 && last != null && last.EndReason == (int)RangeEndReason.Saved;
            string want = c.Get("pre");
            string got = M.Digest(disk, true);
            string Strip(string d) => string.Join("｜", d.Split('｜').Where(x => !x.StartsWith("靶场记录=", StringComparison.Ordinal)));
            return rangeOk && Strip(got) == Strip(want)
                ? StepOutcome.Done($"回到主菜单；存档里：靶场测试按“存档”结束进了测试记录（#{last.Serial}，投影不进存档）；研发域与存档那一刻逐项一致：{Strip(got)}")
                : StepOutcome.Fail($"存档不对：靶场记录 {rangeOk}（{disk.Research?.Range?.History?.Length} 条，最后原因 {last?.EndReason}）；\n      存档前 {Strip(want)}\n      存档里 {Strip(got)}");
        }

        private static StepOutcome TickLoaded(JourneyContext c)
        {
            if (GameRoot.HomeValley == null || !GameRoot.HomeValley.IsActive)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            if (!FgjM3Common.Done(c, "cmp"))
            {
                string rid = RangeId(c);
                long dTicks = GameClock.Ticks - c.GetLong("preTicks");
                string stable = M.Digest(s, false);
                string pre = c.Get("preStable");
                string Strip(string d) => string.Join("｜", d.Split('｜').Where(x => !x.StartsWith("靶场记录=", StringComparison.Ordinal) && !x.StartsWith("情报=", StringComparison.Ordinal)));
                bool idle = !TestRangeService.IsRunning(rid) && TestRangeService.SiteOf(rid) == null && !TestRangeService.TryGetUplinkedProjection(out _, out _, out _);
                if (!idle || Strip(stable) != Strip(pre) || dTicks < 0 || dTicks > GameClock.StepHz * 5)
                {
                    return StepOutcome.Fail($"读档后：靶场空闲 {idle}（已走 {dTicks} 步）；\n      存档前 {Strip(pre)}\n      读档后 {Strip(stable)}");
                }
                c.Set("loadNote", $"读档后第一帧（已走 {dTicks} 步）：靶场空闲（没有投影、没在测试、信号在核心），研究 / 熔合 / 黑匣子 / 收支逐项一致");
                c.SetLong(FgjM3Common.SK(c, "p0"), s.Research.PointsProduced);
                c.SetLong(FgjM3Common.SK(c, "w0"), s.Research.BlackBoxes?.Boxes?.Sum(b => b.Work) ?? 0);
                FgjM3Common.Mark(c, "cmp");
                return StepOutcome.Wait;
            }
            JourneyCommon.ResumeIfAutoPaused(c);
            long work = s.Research.BlackBoxes?.Boxes?.Sum(b => b.Work) ?? 0;
            bool lab = s.Research.PointsProduced > c.GetLong(FgjM3Common.SK(c, "p0"));
            bool bb = work > c.GetLong(FgjM3Common.SK(c, "w0")) || (s.Research.BlackBoxes?.Boxes?.All(b => b.Done) ?? false);
            return lab && bb ? StepOutcome.Done($"{c.Get("loadNote")}；读档后实验室接着出研究点、陈列馆接着分析") : StepOutcome.Wait;
        }
    }
}
