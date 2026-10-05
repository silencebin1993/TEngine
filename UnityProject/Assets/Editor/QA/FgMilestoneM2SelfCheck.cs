using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.EditorTools.JourneyBots;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using TEngine;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG2-E2E-01 自检 [M2 出口]：里程碑出口的自动化部分里，能在编辑模式下逐语义断言的那些。
    ///
    /// A. 数据复原（FG-GAP-050 的临时来源）：候选集合 = 表里获取途径为遗迹终端 / 杂兵残骸 / 精英 / 首领的固件（按表独立重算对账）；
    ///    拒绝顺序与原因（没有解析台 / 没电 / 已获得 / 不能在这里复原 / 技术数据不足，写明需要多少现有多少）且拒绝时什么都不变；
    ///    复原成功：技术数据按稀有度经账本消费事务扣掉、写解锁、敌方加密同时视为已破解（同一种固件的“未破解”标记去掉）、可刻印、可装进机器电路、
    ///    引导钩子与图鉴系统说明、再复原被拒；预检之后技术数据被别处花掉 → 复原被拒不变；真实存档往返；44 条固件全部拿得到。
    /// B. 引信弹迹（FG-GAP-043）：热更层按生效固件类别给武器打标记（引信类有、别的没有）、调参来自表；内核开火记弹迹（即时命中画线、弹体武器只闪光、
    ///    没有标记不记）；渲染实例（弹迹 = 弹体一路 B.w 10、闪光 = 区域一路 B.w 26）；按游戏时间到期（不步进 = 暂停不消失）；上限 128；
    ///    快照格式 8 存标记、格式 7 读成没有标记、弹迹本身不进快照与状态哈希；大量开火时有界。
    /// C. 旅程登记：FGJ-M2 覆盖出口旅程 4 步（外加数据复原与图鉴查看）；FGJ-M2R 覆盖 IC-REQ-022 六类、每类“出错 → 恢复”两端；两条种子不同。
    /// D. 缺口清零（里程碑出口第 5 条）：最迟里程碑是 M2 的缺口全部 Closed；首个门禁是 FG-M2 的延后项全部 Closed / 部分关闭 / 写明顺延；本 Story 登记的 4 条延后项在表里。
    /// E. 解析面板“数据复原”栏：UXML 里有下拉 / 按钮 / 说明；界面文字全部走文本键（中英都有、没有 ⟦⟧）；引导钩子与图鉴条目登记。
    /// </summary>
    public static class FgMilestoneM2SelfCheck
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
            Line("\n[M2 出口] FG2-E2E-01：数据复原（固件临时来源）、引信弹迹、旅程登记、缺口清零、解析面板");
            string dir = Path.Combine(Path.GetTempPath(), "bingames-m2exit-" + Guid.NewGuid().ToString("N"));
            string oldDir = CampaignSaveService.SaveDirectoryOverrideForTests;
            GameLanguage originalLanguage = GameSettings.Language;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.ResetForTests();
                StatusTagCatalog.Reload();
                CarrierReadings.Reload();
                NamedReactionCatalog.ResetForTests();
                FirmwareCatalog.Invalidate();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Directory.CreateDirectory(dir);
                CampaignSaveService.SaveDirectoryOverrideForTests = dir;
                MechanicCodex.ResetForTests();
                MechanicCodex.FilePathOverrideForTests = Path.Combine(dir, "codex_mechanics.json");
                FirmwareRestoreService.ResetForTests();
                Step(CheckRestoreCandidates);
                Step(CheckRestoreRefusals);
                Step(CheckRestoreSuccess);
                Step(CheckRestoreSaveLoad);
                Step(CheckAllObtainable);
                Step(CheckFuseTraceMapping);
                Step(CheckFuseTraceKernel);
                Step(CheckFuseTraceSnapshot);
                Step(CheckJourneyCatalog);
                Step(CheckGapGate);
                Step(CheckAnalysisPanelAndTexts);
            }
            catch (Exception e)
            {
                Fail($"[M2 出口] 自检抛异常：{e}");
            }
            finally
            {
                CampaignSaveService.SaveDirectoryOverrideForTests = oldDir;
                MechanicCodex.ResetForTests();
                FirmwareKinds.ResetForTests();
                CarrierReadings.ResetForTests();
                NamedReactionCatalog.ResetForTests();
                FirmwareCatalog.Invalidate();
                FirmwareRestoreService.ResetForTests();
                GameSettings.SetLanguage(originalLanguage);
                CampaignSession.Clear();
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception)
                {
                    // 临时目录删不掉不影响结论。
                }
            }
            Line($"  · [M2 出口] 断言通过 {_pass}，失败 {_fail}");
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

        // ── A. 数据复原 ────────────────────────────────────────────────────────────

        private static readonly string[] RestorableSources = { "relic", "salvage", "elite", "boss" };

        private static CampaignState NewState(int seed, bool bench, bool powered, int techData)
        {
            CampaignState s = CampaignState.CreateNew("fg2e2e01-" + seed, "Standard", seed);
            CampaignFgStateDomains.EnsureAll(s);
            if (bench)
            {
                s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>())
                    .Where(b => b == null || b.BuildingTypeId != HomeValleyLayout.BuildingTypeAnalysisBench).Concat(new[]
                    {
                        new BuildingRecord
                        {
                            BuildingId = "bench-test", BuildingTypeId = HomeValleyLayout.BuildingTypeAnalysisBench, RegionId = HomeValleyLayout.RegionId,
                            ConstructionState = BuildingConstructionState.Operational,
                            PowerState = powered ? BuildingPowerState.Powered : BuildingPowerState.Unpowered,
                            Position = new Vector2(-10f, -20f),
                        },
                    }).ToArray();
            }
            GiveTech(s, techData, "init");
            CampaignSession.Set(Slot, s);
            return s;
        }

        private static void GiveTech(CampaignState s, int amount, string tag)
        {
            if (amount <= 0)
            {
                return;
            }
            string tx = "selfcheck_tech_" + tag + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            CampaignEconomyLedger.ProposeProduce(s, tx, "selfcheck", CampaignEconomyLedger.ResourceTechData, amount);
            CampaignEconomyLedger.Reserve(s, tx);
            CampaignEconomyLedger.MarkRunning(s, tx);
            CampaignEconomyLedger.Commit(s, tx);
        }

        private static List<string> ExpectedRestorable()
        {
            var list = new List<string>();
            foreach (GameConfig.fg.FirmwareKind row in FirmwareKinds.Rows)
            {
                if (row != null && Array.IndexOf(RestorableSources, row.Source) >= 0)
                {
                    list.Add(row.Id);
                }
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static void CheckRestoreCandidates()
        {
            Line("  · A1. 候选集合 = 表里获取途径为遗迹终端 / 杂兵残骸 / 精英 / 首领的固件（按表独立重算对账）；基础蓝图库与 Demo 已有来源的不在其中");
            CampaignState s = NewState(2001, bench: true, powered: true, techData: 0);
            List<string> expect = ExpectedRestorable();
            List<string> got = FirmwareRestoreService.Candidates(s).OrderBy(x => x, StringComparer.Ordinal).ToList();
            int total = FirmwareCatalog.BaseCount; // FG5-RND-04：名表 44 条（混合固件只能熔合得到，不走数据复原）
            string[] kept = { FirmwareCatalog.FwMarkTagId, FirmwareCatalog.FwArmorPierceId };
            bool keptOut = kept.All(k => !got.Contains(k) && !FirmwareRestoreService.IsRestorable(k));
            int baseCount = FirmwareCatalog.All.Keys.Count(id => FirmwareKinds.SourceOf(id) == "base");
            bool baseOut = FirmwareCatalog.All.Keys.Where(id => FirmwareKinds.SourceOf(id) == "base").All(id => !got.Contains(id));
            Expect(total == 44 && expect.Count == 38 && got.SequenceEqual(expect) && keptOut && baseOut && baseCount == 4,
                $"新档可复原 {got.Count} 条 = 按表独立重算的 {expect.Count} 条（44 条里去掉基础蓝图库 {baseCount} 条与终端数据盒 / 技术缓存各 1 条）");
            int cost = got.Sum(FirmwareRestoreService.CostOf);
            int common = got.Count(x => FirmwareKinds.RarityOf(x) == "common");
            int rare = got.Count(x => FirmwareKinds.RarityOf(x) == "rare");
            int epic = got.Count(x => FirmwareKinds.RarityOf(x) == "epic");
            bool costs = got.All(x => FirmwareRestoreService.CostOf(x) == (FirmwareKinds.RarityOf(x) == "epic" ? Tun("firmware.restore.cost_epic")
                : FirmwareKinds.RarityOf(x) == "rare" ? Tun("firmware.restore.cost_rare") : Tun("firmware.restore.cost_common")));
            Expect(costs && cost == common * Tun("firmware.restore.cost_common") + rare * Tun("firmware.restore.cost_rare") + epic * Tun("firmware.restore.cost_epic"),
                $"代价按稀有度取表（普通 {Tun("firmware.restore.cost_common")} × {common}、精良 {Tun("firmware.restore.cost_rare")} × {rare}、稀有 {Tun("firmware.restore.cost_epic")} × {epic}），全部复原共 {cost} 技术数据");
            // 候选顺序：类别（引信 → 限制器 → 流体 → 电磁）再按 ID。
            List<string> ordered = FirmwareRestoreService.Candidates(s);
            bool sorted = true;
            for (int i = 1; i < ordered.Count; i++)
            {
                int a = (int)FirmwareKinds.CategoryOf(ordered[i - 1]);
                int b = (int)FirmwareKinds.CategoryOf(ordered[i]);
                sorted &= Rank(FirmwareKinds.CategoryOf(ordered[i - 1])) < Rank(FirmwareKinds.CategoryOf(ordered[i]))
                          || (Rank(FirmwareKinds.CategoryOf(ordered[i - 1])) == Rank(FirmwareKinds.CategoryOf(ordered[i])) && string.CompareOrdinal(ordered[i - 1], ordered[i]) < 0);
            }
            Expect(sorted, "候选按类别（引信 → 限制器 → 流体 → 电磁）再按 ID 排序，与固件库一致");
        }

        private static int Rank(FirmwareCategory c) => c == FirmwareCategory.Fuse ? 0 : c == FirmwareCategory.Limiter ? 1 : c == FirmwareCategory.Fluid ? 2 : 3;

        private static int Tun(string id) => GridContent.TryGetTuning(id, out float v) ? (int)Math.Round(v) : -1;

        private static string Snapshot(CampaignState s) =>
            $"{s.TechData}|{s.Scrap}|{string.Join(",", s.UnlockedContentIds ?? Array.Empty<string>())}|{s.ResourceTransactions?.Length ?? 0}";

        private static void CheckRestoreRefusals()
        {
            Line("  · A2. 拒绝：没有解析台 / 没电 / 已获得 / 不能在这里复原 / 技术数据不足 / 不是固件——原因写明、拒绝时什么都不变");
            string wet = "fw_coolant";
            CampaignState none = NewState(2002, bench: false, powered: false, techData: 20);
            FirmwareRestoreService.Result r0 = FirmwareRestoreService.Check(none, wet);
            CampaignState off = NewState(2003, bench: true, powered: false, techData: 20);
            FirmwareRestoreService.Result r1 = FirmwareRestoreService.Check(off, wet);
            Expect(!r0.Success && r0.Code == FirmwareRestoreService.CodeNoPower && !r1.Success && r1.Code == FirmwareRestoreService.CodeNoPower
                   && r1.Message == GameText.Get("analysis.restore.reason.no_power"),
                $"家园没有解析台、解析台没电：都拒绝“{r1.Message}”");
            CampaignState poor = NewState(2004, bench: true, powered: true, techData: 3);
            FirmwareRestoreService.Result r2 = FirmwareRestoreService.Check(poor, wet);
            string wantNoTech = GameText.Format("analysis.restore.reason.no_tech", FirmwareKinds.DisplayName(wet), 4, 3);
            Expect(!r2.Success && r2.Code == FirmwareRestoreService.CodeNoTech && r2.Message == wantNoTech && r2.Message.Contains("4") && r2.Message.Contains("3"),
                $"技术数据 3 < 4：拒绝“{r2.Message}”（写明需要多少、现有多少、怎么获得）");
            FirmwareRestoreService.Result r3 = FirmwareRestoreService.Check(poor, "fw_homing");
            FirmwareRestoreService.Result r4 = FirmwareRestoreService.Check(poor, FirmwareCatalog.FwMarkTagId);
            FirmwareRestoreService.Result r5 = FirmwareRestoreService.Check(poor, "not_a_firmware");
            Expect(r3.Code == FirmwareRestoreService.CodeAlready && r4.Code == FirmwareRestoreService.CodeNotRestorable
                   && r4.Message.Contains(FirmwareKinds.AcquireText(FirmwareCatalog.FwMarkTagId)) && r5.Code == FirmwareRestoreService.CodeNotFirmware,
                $"基础蓝图库的寻的：已获得；终端数据盒的标记跳转：“{r4.Message}”（指回原来的获得方式）；不是固件：拒绝");
            // 拒绝时什么都不变（TryRestore 同一套检查）。
            string before = Snapshot(poor);
            int rejected0 = FirmwareRestoreService.RejectedCount;
            int denied0 = Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied);
            FirmwareRestoreService.Result t = FirmwareRestoreService.TryRestore(poor, wet);
            Expect(!t.Success && Snapshot(poor) == before && FirmwareRestoreService.RejectedCount == rejected0 + 1
                   && Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied) == denied0 + 1,
                "被拒的复原：技术数据、废料、解锁表、账本流水都不变；记一次拒绝并出拒绝音");
        }

        private static void CheckRestoreSuccess()
        {
            Line("  · A3. 复原成功：账本消费事务扣技术数据、写解锁、敌方加密同时视为已破解、可刻印、可装进机器电路、钩子与图鉴、再复原被拒；预检后技术数据被花掉 → 被拒");
            CampaignState s = NewState(2005, bench: true, powered: true, techData: 12);
            string wet = "fw_coolant";
            string shock = "fw_arcchain";
            bool rawBefore = FirmwareKinds.IsRaw(s, shock);
            int restored0 = FirmwareRestoreService.RestoredCount;
            int cracked0 = RawFirmwareService.CrackedCount;
            int tx0 = s.ResourceTransactions?.Length ?? 0;
            FirmwareRestoreService.Result a = FirmwareRestoreService.TryRestore(s, wet);
            ResourceTransactionRecord tx = s.ResourceTransactions?.Skip(tx0).FirstOrDefault(x => x.ResourceType == CampaignEconomyLedger.ResourceTechData && x.Requested > 0);
            Expect(a.Success && s.TechData == 8 && s.UnlockedContentIds.Contains(wet) && tx != null && tx.State == ResourceTransactionState.Committed && Math.Abs(tx.Consumed - 4f) < 1e-4f
                   && a.Message == GameText.Format("analysis.restore.ok", FirmwareKinds.DisplayName(wet), 4),
                $"复原冷却液（普通，4）：“{a.Message}”；技术数据 12 → {s.TechData}，账本记一笔已提交的消费（{tx?.Consumed}）");
            FirmwareRestoreService.Result b = FirmwareRestoreService.TryRestore(s, shock);
            Expect(b.Success && rawBefore && !FirmwareKinds.IsRaw(s, shock) && RawFirmwareService.CrackedCount == cracked0 + 1 && s.TechData == 4,
                $"复原电弧（敌方加密）：复原前是“未破解”、复原后同一种固件一起去掉标记（与解析台破解同一回调）；技术数据 {s.TechData}");
            bool printable = SignalCoreService.PrintableFirmware(s).Contains(wet) && SignalCoreService.PrintableFirmware(s).Contains(shock);
            bool machine = FirmwareKinds.CanInstall(s, wet, FirmwareHost.MachineCircuit, out _) && FirmwareKinds.CanInstall(s, shock, FirmwareHost.MachineCircuit, out _);
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            CircuitOpResult set = board.TrySetFirmware(s, 0, shock);
            Expect(printable && machine && set.Success && FirmwareRestoreService.RestoredCount == restored0 + 2,
                "复原后两条都能在信号核刻印、能装进机器电路（蓝图固件槽选用成功）");
            Expect(GameSettings.HasSeenGuidanceHook(GuidanceHooks.FirmwareRestoreFirstDone) && MechanicCodex.IsUnlocked("codex.firmware.restore"),
                "第一次复原：引导钩子 firmware.restore.first_done 触发，图鉴“数据复原”系统说明解锁");
            List<string> cand = FirmwareRestoreService.Candidates(s);
            FirmwareRestoreService.Result again = FirmwareRestoreService.TryRestore(s, wet);
            Expect(!cand.Contains(wet) && !cand.Contains(shock) && !again.Success && again.Code == FirmwareRestoreService.CodeAlready && s.TechData == 4,
                $"复原过的从候选里消失；再复原被拒“{again.Message}”，不扣技术数据");
            // 预检通过后、确认前技术数据被别处花掉（确认框开着期间）：确认时按此刻状态重查 → 被拒，不变。
            string target = "fw_burntrail";
            bool pre = FirmwareRestoreService.Check(s, target).Success;
            string spend = "selfcheck_spend_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            CampaignEconomyLedger.ProposeConsume(s, spend, "selfcheck", CampaignEconomyLedger.ResourceTechData, 2);
            CampaignEconomyLedger.Reserve(s, spend);
            CampaignEconomyLedger.Commit(s, spend);
            string before = Snapshot(s);
            FirmwareRestoreService.Result late = FirmwareRestoreService.TryRestore(s, target);
            Expect(pre && !late.Success && late.Code == FirmwareRestoreService.CodeNoTech && Snapshot(s) == before && !s.UnlockedContentIds.Contains(target),
                $"预检通过（技术数据 4 ≥ 4）后被花掉 2：确认时重查被拒“{late.Message}”，什么都不变");
        }

        private static void CheckRestoreSaveLoad()
        {
            Line("  · A4. 真实存档往返：复原写的解锁、技术数据、账本流水逐项一致（不新增存档域，只用既有字段）");
            CampaignState s = NewState(2006, bench: true, powered: true, techData: 20);
            FirmwareRestoreService.TryRestore(s, "fw_coolant");
            FirmwareRestoreService.TryRestore(s, "fw_nitrogen");
            SaveResult w = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            LoadResult r = w.Success ? CampaignSaveService.Load(Slot) : default;
            bool ok = w.Success && r.Success && r.State.TechData == s.TechData && r.State.UnlockedContentIds.Contains("fw_coolant") && r.State.UnlockedContentIds.Contains("fw_nitrogen")
                      && (r.State.ResourceTransactions?.Length ?? 0) == (s.ResourceTransactions?.Length ?? 0) && !FirmwareKinds.IsRaw(r.State, "fw_nitrogen")
                      && !FirmwareRestoreService.Candidates(r.State).Contains("fw_coolant");
            Expect(ok, $"存档 → 读档：技术数据 {r.State?.TechData}、冷却液与液氮仍已复原（液氮仍视为已破解）、账本 {r.State?.ResourceTransactions?.Length} 笔，与存档前一致");
        }

        private static void CheckAllObtainable()
        {
            Line("  · A5. FG-M2 目标“44 条固件全部成为可玩内容”：每条都有获得方式，拿到后都能用（核心 → 信号核；常规 → 信号核与机器电路）");
            CampaignState s = NewState(2007, bench: true, powered: true, techData: 1000);
            var noSource = new List<string>();
            var unusable = new List<string>();
            int viaRestore = 0;
            int viaBase = 0;
            int viaDemo = 0;
            int machineOk = 0;
            int regular = 0;
            foreach (string id in FirmwareCatalog.BaseIds.OrderBy(x => x, StringComparer.Ordinal)) // FG5-RND-04：名表 44 条（混合固件由 FgFusionSelfCheck 覆盖）
            {
                string src = FirmwareKinds.SourceOf(id);
                if (src == "base")
                {
                    viaBase++;
                }
                else if (HomeValleyAnalysis.YieldTable.Values.Any(v => v.UnlockContentId == id))
                {
                    viaDemo++;
                    s.UnlockedContentIds = s.UnlockedContentIds.Append(id).ToArray(); // 与解析台完成同一写法（Demo 流程由 [解析台] 自检覆盖）
                }
                else if (FirmwareRestoreService.TryRestore(s, id).Success)
                {
                    viaRestore++;
                }
                else
                {
                    noSource.Add(id);
                    continue;
                }
                bool signal = FirmwareKinds.CanInstall(s, id, FirmwareHost.SignalCore, out _) && SignalCoreService.PrintableFirmware(s).Contains(id);
                if (!signal)
                {
                    unusable.Add(id);
                }
                if (!FirmwareKinds.IsCore(id))
                {
                    regular++;
                    if (FirmwareKinds.CanInstall(s, id, FirmwareHost.MachineCircuit, out _))
                    {
                        machineOk++;
                    }
                }
            }
            Expect(noSource.Count == 0 && unusable.Count == 0 && viaBase + viaDemo + viaRestore == 44,
                $"44 条：基础蓝图库 {viaBase}、Demo 已有来源 {viaDemo}、数据复原 {viaRestore}；拿到后全部能刻印、能放进信号核" +
                (noSource.Count + unusable.Count == 0 ? string.Empty : $"；拿不到 [{string.Join(",", noSource)}]、用不了 [{string.Join(",", unusable)}]"));
            Expect(machineOk == regular || regular - machineOk <= 1,
                $"常规固件 {regular} 条里 {machineOk} 条能装进机器电路（核心只属于信号，FGR-SIG-012）");
        }

        // ── B. 引信弹迹 ────────────────────────────────────────────────────────────

        private static CombatWeapon WeaponFor(params string[] firmware)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            board.TrySetUplink(2);
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, firmware ?? Array.Empty<string>());
            return CombatSite.MachineWeaponFrom(p);
        }

        private static void CheckFuseTraceMapping()
        {
            Line("  · B1. 热更层：按生效固件的类别（表 category 列）给武器打“引信弹迹”标记；调参来自表");
            CampaignSession.Set(Slot, NewState(2010, bench: false, powered: false, techData: 0));
            CombatWeapon homing = WeaponFor("fw_homing");
            CombatWeapon split = WeaponFor("fw_split");
            CombatWeapon coolant = WeaponFor("fw_coolant");
            CombatWeapon bare = WeaponFor();
            var fuseIds = FirmwareCatalog.BaseIds.Where(id => FirmwareKinds.CategoryOf(id) == FirmwareCategory.Fuse).ToList(); // 名表 44 条
            bool table = fuseIds.All(id => CombatSite.FuseTraceOf(new BlueprintCircuitPreview { FirmwareIds = new[] { id } }) == 1)
                         && FirmwareCatalog.BaseIds.Where(id => FirmwareKinds.CategoryOf(id) != FirmwareCategory.Fuse)
                             .All(id => CombatSite.FuseTraceOf(new BlueprintCircuitPreview { FirmwareIds = new[] { id } }) == 0);
            Expect(homing.FuseTrace == 1 && split.FuseTrace == 1 && coolant.FuseTrace == 0 && bare.FuseTrace == 0 && table && fuseIds.Count == 17,
                $"接入口插寻的 / 分裂（引信）→ 有标记；冷却液（流体）/ 空 → 没有；17 条引信类逐条有、其余 27 条逐条没有");
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            Expect(Math.Abs(cfg.FuseTraceSeconds - 0.15f) < 1e-5f && Math.Abs(CombatConfig.Default.FuseTraceSeconds - 0.15f) < 1e-5f,
                $"弹迹停留 {cfg.FuseTraceSeconds} 游戏秒（fg.TbHomeTuning combat.fuse_trace_seconds）");
        }

        private static CombatSpawn Machine(double2 at, int weapon) => new CombatSpawn
        {
            Kind = CombatUnitKind.Machine,
            Faction = CombatFaction.Player,
            Behavior = CombatBehavior.Commanded,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled,
            Position = at,
            Home = at,
            Radius = 0.4f,
            Speed = 4f,
            Health = 1e6f,
            MaxHealth = 1e6f,
            Weapon = weapon,
            BehaviorProfile = -1,
        };

        private static CombatSpawn Hostile(double2 at) => new CombatSpawn
        {
            Kind = CombatUnitKind.Enemy,
            Faction = CombatFaction.Hostile,
            Behavior = CombatBehavior.HoldFire,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
            Position = at,
            Home = at,
            Radius = 0.4f,
            Speed = 0f,
            Health = 1e9f,
            MaxHealth = 1e9f,
            Weapon = -1,
            BehaviorProfile = -1,
            Priority = 1,
        };

        private static void RunSteps(CombatKernel k, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                k.Step(1f / 60f, k.Time + 1f / 60f);
                k.ClearCues();
                k.DrainGameplay(4096, out _);
            }
        }

        private static void CheckFuseTraceKernel()
        {
            Line("  · B2. 内核：开火记弹迹（即时命中画线、弹体武器只闪光、没标记不记）→ 渲染实例 → 按游戏时间到期（不步进 = 暂停不消失）→ 上限 128");
            CampaignSession.Set(Slot, NewState(2011, bench: false, powered: false, techData: 0));
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using var k = new CombatKernel(cfg, 64);
            int wFuse = k.AddWeapon(WeaponFor("fw_homing"));
            int wPlain = k.AddWeapon(WeaponFor("fw_coolant"));
            var proj = new CombatWeapon
            {
                Mode = CombatWeaponMode.Projectile, HasOutput = 1, Range = 12f, Damage = 5f, Cooldown = 0.1f,
                ProjectileSpeed = 20f, ProjectileRadius = 0.2f, ProjectileLife = 2f, FuseTrace = 1,
            };
            int wProj = k.AddWeapon(proj);
            int mFuse = k.Spawn(Machine(new double2(0, 0), wFuse));
            int mPlain = k.Spawn(Machine(new double2(0, 2), wPlain));
            int mProj = k.Spawn(Machine(new double2(0, 4), wProj));
            int e = k.Spawn(Hostile(new double2(5, 1)));
            CombatFireResult f1 = k.FireAt(mPlain, e, k.Time);
            bool none = f1 == CombatFireResult.Ok && k.TraceCount == 0;
            CombatFireResult f2 = k.FireAt(mFuse, e, k.Time);
            k.TryGetTrace(0, out CombatShotTrace t0);
            k.TryGetUnit(mFuse, out CombatUnitView vm);
            k.TryGetUnit(e, out CombatUnitView ve);
            bool line = f2 == CombatFireResult.Ok && k.TraceCount == 1 && t0.Line == 1 && math.distance(t0.From, vm.Position) < 1e-6 && math.distance(t0.To, ve.Position) < 1e-6
                        && t0.Faction == (byte)CombatFaction.Player;
            CombatFireResult f3 = k.FireAt(mProj, e, k.Time);
            k.TryGetTrace(1, out CombatShotTrace t1);
            bool flash = f3 == CombatFireResult.Ok && k.TraceCount == 2 && t1.Line == 0;
            Expect(none && line && flash,
                $"冷却液武器开火不记弹迹；寻的武器即时命中记一条（炮口 → 命中点，己方）；带标记的弹体武器只记炮口闪光（弹体自己飞）——{f1}/{f2}/{f3}，共 {k.TraceCount} 条");

            var units = new NativeList<CombatInstance>(16, Allocator.Persistent);
            var projs = new NativeList<CombatInstance>(16, Allocator.Persistent);
            var fx = new NativeList<CombatInstance>(16, Allocator.Persistent);
            try
            {
                k.PrepareRender(units, projs, double2.zero);
                k.PrepareEffects(fx, double2.zero);
                int traceLines = 0;
                bool endpoints = false;
                for (int i = 0; i < projs.Length; i++)
                {
                    if (Math.Abs(projs[i].B.w - CombatConst.TraceInstanceKind) < 0.01f)
                    {
                        traceLines++;
                        endpoints = math.distance(projs[i].A.xy, (float2)ve.Position) < 1e-3f && math.distance(projs[i].A.zw, (float2)vm.Position) < 1e-3f && projs[i].B.y > 0.99f;
                    }
                }
                int flashes = 0;
                for (int i = 0; i < fx.Length; i++)
                {
                    if (Math.Abs(fx[i].B.w - CombatConst.MuzzleFlashKind) < 0.01f && Math.Abs(fx[i].B.z - CombatConst.MuzzleFlashColor) < 0.5f)
                    {
                        flashes++;
                    }
                }
                Expect(traceLines == 1 && endpoints && flashes == 2,
                    $"渲染缓冲：弹体一路 1 条弹迹（B.w = {CombatConst.TraceInstanceKind}，两端 = 命中点 / 炮口、剩余比例 1）；区域一路 2 个炮口闪光（B.w = {CombatConst.MuzzleFlashKind}，琥珀白）");

                // 到期：按游戏时间；不步进（暂停）时不消失。
                int before = k.TraceCount;
                bool pauseKeeps = k.TraceCount == before; // 没有 Step = 世界暂停：真实时间过去多久都一样
                RunSteps(k, 6); // 0.1 游戏秒 < 0.15
                int mid = k.TraceCount;
                RunSteps(k, 4); // 累计 0.167 游戏秒 > 0.15
                int after = k.TraceCount;
                k.PrepareRender(units, projs, double2.zero);
                bool cleared = true;
                for (int i = 0; i < projs.Length; i++)
                {
                    cleared &= Math.Abs(projs[i].B.w - CombatConst.TraceInstanceKind) > 0.01f;
                }
                Expect(pauseKeeps && before == 2 && mid == 2 && after == 0 && cleared,
                    $"按游戏时间到期：不步进时 {before} 条不动；0.1 游戏秒后还在（{mid}），0.167 游戏秒后全部消失（{after}），渲染缓冲里也没了（倍速 = 每真实秒多几步，同步变快）");

                // 上限：200 台带标记的机器同时开火 → 只留最新 128 条。
                for (int i = 0; i < 200; i++)
                {
                    int m = k.Spawn(Machine(new double2(-2 - i * 0.01, 0), wFuse));
                    k.FireAt(m, e, k.Time);
                }
                k.TryGetTrace(CombatConst.TraceCapacity - 1, out CombatShotTrace last);
                Expect(k.TraceCount == CombatConst.TraceCapacity && Math.Abs(last.From.x - (-2 - 199 * 0.01)) < 1e-6,
                    $"同一时刻 200 次带标记的开火：只留最新 {k.TraceCount} 条（上限 {CombatConst.TraceCapacity}，挤掉最老的），渲染与内存有界");

                // 大量开火时的开销（报告）：80 台带标记的机器每步都开火 120 步。
                var sw = Stopwatch.StartNew();
                int maxTraces = 0;
                for (int step = 0; step < 120; step++)
                {
                    for (int i = 0; i < 80; i++)
                    {
                        k.FireAt(mFuse, e, k.Time);
                    }
                    RunSteps(k, 1);
                    maxTraces = Math.Max(maxTraces, k.TraceCount);
                }
                sw.Stop();
                k.PrepareRender(units, projs, double2.zero);
                Expect(maxTraces <= CombatConst.TraceCapacity && projs.Length <= k.ProjectileCount + CombatConst.TraceCapacity,
                    $"每步 80 次带标记开火 × 120 步：弹迹最多 {maxTraces} 条、渲染实例 {projs.Length}（有界）；耗时 {sw.Elapsed.TotalMilliseconds:F1} ms（仅报告，Editor Mono）");
            }
            finally
            {
                units.Dispose();
                projs.Dispose();
                fx.Dispose();
            }
        }

        private static void CheckFuseTraceSnapshot()
        {
            Line("  · B3. 快照：格式 8 存武器的引信弹迹标记、格式 7 读成没有标记；弹迹本身是表现数据，不进快照与状态哈希");
            CampaignSession.Set(Slot, NewState(2012, bench: false, powered: false, techData: 0));
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using var k = new CombatKernel(cfg, 16);
            int w = k.AddWeapon(WeaponFor("fw_homing"));
            int m = k.Spawn(Machine(new double2(0, 0), w));
            int e = k.Spawn(Hostile(new double2(4, 0)));
            k.FireAt(m, e, k.Time);
            ulong h1 = k.StateHash();
            byte[] now8 = k.Serialize();
            byte[] old7 = k.SerializeFormatForTests(7);
            using var k2 = new CombatKernel(cfg, 16);
            CombatLoadResult r8 = k2.Load(now8);
            k2.TryGetWeapon(w, out CombatWeapon w8);
            ulong h2 = k2.StateHash();
            using var k3 = new CombatKernel(cfg, 16);
            CombatLoadResult r7 = k3.Load(old7);
            k3.TryGetWeapon(w, out CombatWeapon w7);
            Expect(CombatConst.FormatVersion >= 8 && r8 == CombatLoadResult.Ok && w8.FuseTrace == 1 && r7 == CombatLoadResult.Ok && w7.FuseTrace == 0,
                $"格式 {CombatConst.FormatVersion}：读回标记 = {w8.FuseTrace}；格式 7 的旧快照照常读、标记 = {w7.FuseTrace}（没有弹迹）");
            Expect(k.TraceCount == 1 && k2.TraceCount == 0 && h1 == h2,
                $"存档前有 {k.TraceCount} 条弹迹、读回后 {k2.TraceCount} 条；状态哈希存前 = 读后（弹迹不进哈希，观察与不观察、存读档结果一致）");
        }

        // ── C. 旅程登记 ────────────────────────────────────────────────────────────

        private static void CheckJourneyCatalog()
        {
            Line("  · C. 旅程登记：FGJ-M2 覆盖出口旅程 4 步；FGJ-M2R 覆盖 IC-REQ-022 六类（每类出错 → 恢复两端）；种子不同");
            JourneyDef m2 = JourneyCatalog.Create(FgjM2Journey.Id);
            JourneyDef m2r = JourneyCatalog.Create(FgjM2ReverseJourney.Id);
            bool Wellformed(JourneyDef d) => d != null && d.Steps.Count > 0 && d.Steps.Select(x => x.Id).Distinct().Count() == d.Steps.Count
                                               && d.Steps.All(x => x.TimeoutSeconds > 0 && x.Tick != null) && d.TotalTimeoutSeconds > 0;
            var exit = new (string Step, string Id)[]
            {
                ("从固件库筛选流体类", "lib_fluid"), ("在靶场前的预备区组合两台机器", "box"), ("触发第一幕开放的具名反应、首次慢放", "attack_dummy"),
                ("看到图鉴解锁", "codex_entry"), ("在统计里看到这条反应的伤害归因", "stats_open"),
                ("固件来源（FG-GAP-050）：数据复原", "ana_wet_ok"), ("远征场次记下反应", "fc_attack"),
            };
            List<string> missing = exit.Where(x => m2 == null || m2.Steps.All(s => s.Id != x.Id)).Select(x => x.Step).ToList();
            Expect(Wellformed(m2) && missing.Count == 0 && m2.Seed == FgjM2Journey.TestSeed,
                $"FGJ-M2：{m2?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m2?.Seed}；出口旅程各步都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            var reverse = new (string Kind, string[] Ids)[]
            {
                ("资源不足", new[] { "r1_nopower", "r1_power", "r1_wet_ok", "r1_notech" }), ("路径失败", new[] { "r2_bad", "r2_ok" }),
                ("暂停", new[] { "r3_order", "r3_frozen", "r3_resume2" }), ("目标死亡", new[] { "r4_dead", "r4_regen", "r4_again" }),
                ("存读档", new[] { "r5_menu", "r5_loaded", "r5_again" }), ("断网 / 失联", new[] { "r6_jam", "r6_attack", "r6_out" }),
            };
            List<string> missingR = reverse.Where(x => m2r == null || x.Ids.Any(id => m2r.Steps.All(s => s.Id != id))).Select(x => x.Kind).ToList();
            Expect(Wellformed(m2r) && missingR.Count == 0 && m2r.Seed != m2.Seed && m2r.Steps.Any(x => x.Id == "r1_cancel"),
                $"FGJ-M2R：{m2r?.Steps.Count} 步；六类反向场景齐全{(missingR.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missingR))}（另有确认框取消）；种子 {m2r?.Seed} ≠ {m2?.Seed}（B25）");
            string src = ReadRepo("TEngine/UnityProject/Assets/Editor/JourneyBots/FgjM2Common.cs") + ReadRepo("TEngine/UnityProject/Assets/Editor/JourneyBots/FgjM2Journey.cs");
            bool rts = src.Contains("button: 1") && src.Contains("JourneyInput.Drag(") && (src.Contains("ShiftClickWorld") || src.Contains("ShiftClickScreen")) && !src.Contains("IssueAttack(") && !src.Contains("DebugSelectMany(")
                       && !src.Contains("TryRestore(");
            Expect(rts, "旅程走 RTS 正式输入：右键下令（button: 1）、拖框选、Shift 加选（FG5-E2E-01 起点机器露出来的部分：ShiftClickScreen）；源码里不直接调编队命令 / 选择集 / 复原服务");
        }

        // ── D. 缺口清零 ────────────────────────────────────────────────────────────

        private static readonly Regex FirstMilestone = new Regex(@"M(\d+)");

        private static int FirstMilestoneOf(string s)
        {
            Match m = FirstMilestone.Match(s ?? string.Empty);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        private static void CheckGapGate()
        {
            Line("  · D. 里程碑出口第 5 条：属于 FG-M2 的缺口全部 Closed、延后项全部 Closed / 部分关闭 / 写明顺延（按门禁列的第一个里程碑判定）");
            string text = ReadRepo("production/design/full-game/FG-GAP-REGISTER.md");
            if (text == null)
            {
                Fail("找不到 production/design/full-game/FG-GAP-REGISTER.md（仓库根定位失败）");
                return;
            }
            var openGaps = new List<string>();
            var openDebts = new List<string>();
            int gapRows = 0;
            int debtRows = 0;
            int m2Rows = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var status = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                if (!raw.StartsWith("| FG-GAP-", StringComparison.Ordinal) && !raw.StartsWith("| DEBT-", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] cols = raw.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                string st = cols[cols.Length - 1];
                ids.Add(cols[0]);
                status[cols[0]] = st;
                if (raw.StartsWith("| FG-GAP-", StringComparison.Ordinal))
                {
                    gapRows++;
                    if (cols.Length >= 7 && FirstMilestoneOf(cols[5]) == 2)
                    {
                        m2Rows++;
                        if (!st.StartsWith("Closed", StringComparison.Ordinal))
                        {
                            openGaps.Add(cols[0]);
                        }
                    }
                    continue;
                }
                debtRows++;
                if (cols.Length < 9 || FirstMilestoneOf(cols[6]) != 2)
                {
                    continue;
                }
                m2Rows++;
                bool ok = st.StartsWith("Closed", StringComparison.Ordinal) || st.StartsWith("部分关闭", StringComparison.Ordinal) || st.StartsWith("顺延", StringComparison.Ordinal);
                if (!ok)
                {
                    openDebts.Add(cols[0]);
                }
            }
            Expect(gapRows > 50 && debtRows > 120 && m2Rows >= 10, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中属于 FG-M2 的 {m2Rows} 行（解析没有漏行）");
            Expect(openGaps.Count == 0 && openDebts.Count == 0,
                openGaps.Count + openDebts.Count == 0
                    ? "属于 FG-M2 的缺口全部 Closed，延后项全部 Closed / 部分关闭（剩余部分另有门禁）/ 写明顺延（待用户同意）"
                    : $"FG-M2 出口前还开着：缺口 [{string.Join("、", openGaps)}]，延后项 [{string.Join("、", openDebts)}]");
            bool mine = new[] { "DEBT-FG2E2E01-01", "DEBT-FG2E2E01-02", "DEBT-FG2E2E01-03", "DEBT-FG2E2E01-04" }.All(ids.Contains)
                        && status.TryGetValue("FG-GAP-043", out string g43) && g43.StartsWith("Closed", StringComparison.Ordinal)
                        && status.TryGetValue("FG-GAP-050", out string g50) && g50.StartsWith("Closed", StringComparison.Ordinal);
            Expect(mine, "FG-GAP-043（引信类看不出来）、FG-GAP-050（38 条固件拿不到）由本 Story 关闭；本 Story 的 4 条延后项（旅程夹具、临时渠道去留、靶场、弹迹美术）已登记");
        }

        // ── E. 解析面板与文本 ──────────────────────────────────────────────────────

        private static void CheckAnalysisPanelAndTexts()
        {
            Line("  · E. 解析面板“数据复原”栏：UXML 控件齐全；界面文字全部走文本键（中英都有）；引导钩子与图鉴条目登记");
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/GameRes/Raw/UI/Analysis/AnalysisPanel.uxml");
            VisualElement root = uxml != null ? uxml.CloneTree() : null;
            bool controls = root != null && root.Q<DropdownField>("RestoreDropdown") != null && root.Q<Button>("RestoreButton") != null
                            && root.Q<Label>("RestoreDetail") != null && root.Q<Label>("RestoreHint") != null && root.Q<Label>("RestoreTitle") != null;
            Expect(controls, "AnalysisPanel.uxml 有“数据复原”栏：标题、说明、下拉、复原按钮、选中固件说明");
            string[] keys =
            {
                "analysis.restore.title", "analysis.restore.hint", "analysis.restore.choice", "analysis.restore.button", "analysis.restore.empty",
                "analysis.restore.none_value", "analysis.restore.tech", "analysis.restore.detail.kind", "analysis.restore.detail.source", "analysis.restore.detail.tags", "analysis.restore.detail.reactions", "analysis.restore.confirm.title", "analysis.restore.confirm.body", "analysis.restore.confirm.ok",
                "analysis.restore.ok", "analysis.restore.reason.already", "analysis.restore.reason.not_restorable", "analysis.restore.reason.no_power",
                "analysis.restore.reason.no_tech", "analysis.restore.reason.none_selected", "codex.firmware.restore.title", "codex.firmware.restore.body", "codex.firmware.restore.hint",
            };
            var missing = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string key in keys)
                {
                    string v = GameText.Get(key);
                    if (string.IsNullOrEmpty(v) || v.Contains("⟦"))
                    {
                        missing.Add(lang + ":" + key);
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            CampaignState s = NewState(2020, bench: true, powered: true, techData: 0);
            string choice = FirmwareRestoreService.ChoiceText("fw_nitrogen");
            // 不剧透（与固件库详情同一规则）：短路还没在图鉴里解锁时写“？？？（尚未发现）”，解锁后写名字。
            MechanicCodex.ResetForTests();
            MechanicCodex.FilePathOverrideForTests = Path.Combine(Path.GetTempPath(), "bingames-m2exit-codex-" + Guid.NewGuid().ToString("N") + ".json");
            string conductName = NamedReactionCatalog.NameOf("reaction_conduct");
            string before = FirmwareRestoreService.DetailText(s, "fw_coolant");
            MechanicCodex.Unlock(MechanicCodex.ReactionEntryId("reaction_conduct"));
            string detail = FirmwareRestoreService.DetailText(s, "fw_coolant");
            bool hidden = !before.Contains(conductName) && before.Contains(GameText.Get("fwlib.detail.reaction_unknown"));
            bool texts = choice.Contains(FirmwareKinds.DisplayName("fw_nitrogen")) && choice.Contains("8") && !choice.Contains("⟦")
                         && detail.Contains(FirmwareKinds.AcquireText("fw_coolant")) && detail.Contains(conductName) && !detail.Contains("⟦")
                         && detail.Contains(StatusTagCatalog.NameOf("Wet"));
            Expect(missing.Count == 0 && texts && hidden,
                $"{keys.Length} 个文本键中英齐全{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}；下拉一行“{choice}”；" +
                $"冷却液说明（短路未发现时不剧透：“{before.Replace("\n", " / ")}”；图鉴解锁后：“{detail.Replace("\n", " / ")}”）");
            // 布局探针（B15）：数据复原栏填上真实的候选与最长的说明，中英 × UI 缩放最小 / 1 / 最大 × 四种分辨率，不溢出、不重叠。
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                FirmwareCatalog.Invalidate();
                List<string> ids = FirmwareRestoreService.Candidates(s);
                string longest = ids.Select(id => FirmwareRestoreService.DetailText(s, id)).OrderByDescending(x => x.Length).FirstOrDefault() ?? string.Empty;
                foreach (float scale in new[] { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") })
                {
                    string result = UiToolkitLayoutProbe.Probe("Assets/GameRes/Raw/UI/Analysis/AnalysisPanel.uxml", "AnalysisPanelRoot", stressFill: false, prepare: r =>
                    {
                        r.Q<VisualElement>("AnalysisPanelRoot")?.AddToClassList("ana-root-visible");
                        r.Q<Label>("RestoreTitle").text = GameText.Get("analysis.restore.title");
                        r.Q<Label>("RestoreHint").text = GameText.Get("analysis.restore.hint") + " " + GameText.Format("analysis.restore.tech", 999);
                        DropdownField dd = r.Q<DropdownField>("RestoreDropdown");
                        dd.choices = ids.Select(FirmwareRestoreService.ChoiceText).ToList();
                        // 压最长的一条：下拉文字最长时也不能盖住右边的“复原”按钮。
                        dd.index = dd.choices.Select((x, i) => (x.Length, i)).OrderByDescending(p => p.Length).First().i;
                        r.Q<Button>("RestoreButton").text = GameText.Get("analysis.restore.button");
                        r.Q<Label>("RestoreDetail").text = longest;
                        r.Q<Label>("ResultLabel").text = GameText.Format("analysis.restore.reason.no_tech", FirmwareKinds.DisplayName("fw_amplify"), 12, 4);
                    }, uiScale: scale);
                    bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                    Expect(pass, $"布局探针 AnalysisPanel.uxml（数据复原栏）[{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(700, result.Length)))}");
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            FirmwareCatalog.Invalidate();
            MechanicCodexEntry entry = MechanicCodex.Find("codex.firmware.restore");
            Expect(GuidanceHooks.Known.Contains(GuidanceHooks.FirmwareRestoreFirstDone) && entry != null && entry.Hooks.Contains(GuidanceHooks.FirmwareRestoreFirstDone),
                "引导钩子 firmware.restore.first_done 登记在 GuidanceHooks.Known；图鉴系统说明“数据复原”由它解锁（FG00 B14）");
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
            Debug.LogWarning("[M2 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
