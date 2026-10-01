using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
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
    /// FG4-ECO-07 机器名册、岗位与改名的自动验收（FG04 FGR-ECO-040 / 041 / 042；FG13 FGU-16 / 17；FG00 FGR-BASE-020 / 021；FGT-ECO-004；
    /// 负向“远征中的机器改岗位、名字过长或为空”；承接 DEBT-FG1HUD01-01 / FG1SIG04-04 / FG4ECO06-06 / FG0UX01-01 / FG3LOG02-08 / FG3LOG02-09）。
    /// 全部起真实系统：真实家园（世界模拟、工单派发与机器移动、维修台、常驻规则）、真实远征出发、真文件存读档、真 UXML 面板。
    /// A 数据（新表与源数据逐字段、枚举对照、文本中英、钩子、图鉴、按键、调参）；B 旧档与默认（缺字段 = 劳动 / 默认名，越界岗位与非法名字读档兜底）；
    /// C FGT-ECO-004（闲置机器不做事、劳动岗按优先级接活、远征出发后劳动力下降与“远征中”锁定）；D 改岗位的副作用（让出工单 / 物品守恒、补给规则的单子交给别的劳动机且规则不记“玩家改动”、
    /// 驻防维持与玩家接管暂停（接入与真实右键派工两条入口）、驻防点到不了时不补派且只通知一次、名册送修修满回原岗位、暂停中反复送修 ID 不重复且真文件读档成功、
    /// 闲置时规则不派并写原因、前哨劳动没有前哨站时拒绝、厂内拒绝、批量部分成功）；E 名字（合法 / 空 / 过长 / 非法字符 / 恢复默认、
    /// HUD · 通知 · 规则 · 结算同源、源码里不再自拼“#编号”）；F 名册排序与筛选；G 已结束工单清理与按机型取料；H 真文件存读档；I 暂停与 0.5x～3x；
    /// J 观察 / 不观察一致；K 面板（真 UXML：排序、筛选、行内改岗位、远征中禁用、批量、详情改名与原因、安全模式标记、顶栏劳动力、快捷键 + 布局探针）；L 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgMachineRosterSelfCheck）。
    /// </summary>
    public static class FgMachineRosterSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _seq = 9000;
        private static double _fakeNow;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 机器名册与岗位")]
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
            Line("\n[机器名册] 岗位、名册排序筛选与批量、改名与名字同源、劳动力、远征锁定、存读档、倍速、观察一致、面板（FG4-ECO-07）");
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
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgroster-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "岗位 / 名册 / 劳动力在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），机器移动在战斗内核（AOT）；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckDefaultsAndOldSaves);
                Step(CheckIdleDoesNothing);
                Step(CheckLaborPriority);
                Step(CheckExpeditionLaborDrop);
                Step(CheckRoleChangeReleasesWork);
                Step(CheckRuleSupplyRoleChange);
                Step(CheckGarrison);
                Step(CheckGarrisonUnreachable);
                Step(CheckRulePathBlocked);
                Step(CheckManualRepair);
                Step(CheckPausedResendSaveLoad);
                Step(CheckRulesSkipIdle);
                Step(CheckRoleNegatives);
                Step(CheckNames);
                Step(CheckNameSameSource);
                Step(CheckQuery);
                Step(CheckHistoryPruneAndCarry);
                Step(CheckSaveLoad);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckPanel);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"机器名册自检抛异常：{e}");
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
                StandingRuleService.ResetForTests();
                MachineRoster.ResetForTests();
                SignalPresence.ResetForTests();
                PowerEnvironment.ResetForTests();
                HomeValleyPowerGrid.ResetForTests();
                GameClock.ResetSession();
                StrategyClock.Reset();
                CameraDirector.RealDeltaTime = originalDelta;
                NotificationCenter.AutoPauseHandler = originalAutoPause;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                GridContent.ResetForTests();
                WorldGenContent.ResetForTests();
                ProducerCatalog.ResetForTests();
                ItemCatalog.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                RosterPanelUIToolkit.InWorldOverrideForTests = false;
                RosterPanelUIToolkit.Close();
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
            Line($"  · [机器名册] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static CampaignState NewWorld(int seed, bool observe = true, int scrap = 600)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            BuildingOps.ResetForTests();
            StandingRuleService.ResetForTests();
            MachineRoster.ResetForTests();
            _fakeNow = 0;
            MachineRoster.Clock = () => _fakeNow;
            _seq = 9000;
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, scrap);
            FgProductionSelfCheck.PowerUp(s);
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse && b.ConstructionState == BuildingConstructionState.Damaged)
                {
                    b.ConstructionState = BuildingConstructionState.Operational;
                    b.Health = BuildingOps.MaxDurability(b.BuildingTypeId);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            // 测试只看自己设的规则：删掉新战役默认开启的“远征卸货”。
            foreach (StandingRuleRecord r in StandingRuleService.Ordered(s).ToList())
            {
                StandingRuleService.TryDelete(s, r.Serial, out _);
            }
            return s;
        }

        /// <summary>测试捷径：直接登记一座已建成的建筑（真实放置 / 施工由 FG3 / FG4-ECO-05 覆盖），位置按种子地形找空地（B25）。</summary>
        private static BuildingRecord Place(CampaignState s, string typeId)
        {
            GridCell? at = FgProductionSelfCheck.FindFree(s, typeId, 8f, 26f);
            if (!at.HasValue)
            {
                return null;
            }
            BuildingGrid bg = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + typeId + "#" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = at.Value.X,
                GridY = at.Value.Y,
                Position = GridMath.FootprintCenter(at.Value, bg.FootprintW, bg.FootprintH, 0),
                Health = BuildingOps.MaxDurability(typeId),
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            return r;
        }

        private static BuildingRecord Bay(CampaignState s)
        {
            BuildingRecord bay = s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeRepairBay) ?? Place(s, "repair_bay");
            if (bay != null && bay.ConstructionState != BuildingConstructionState.Operational)
            {
                bay.ConstructionState = BuildingConstructionState.Operational;
                bay.Health = BuildingOps.MaxDurability(bay.BuildingTypeId);
            }
            HomeValleyPowerGrid.Recompute(s);
            return bay;
        }

        private static int Spawn(CampaignState s, string chassis, string blueprint, Vector2 offset, float hp = 120f)
        {
            Vector2 core = HomeValleyLayout.Core.Position;
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, core + offset, hp, hp);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            return r.Success ? r.LogicId : 0;
        }

        private static List<MachineRecord> HomeMachines() =>
            MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).OrderBy(m => m.LogicId).ToList();

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static void Seconds(float sec) => FgProductionSelfCheck.Seconds(sec);

        private static bool StepUntil(Func<bool> done, int maxGameSeconds) => FgProductionSelfCheck.StepUntil(done, maxGameSeconds);

        private static void Give(CampaignState s, string itemId, int n)
        {
            ItemCatalog.TryGet(itemId, out ItemDef d);
            HomeInventory.Add(s, d, n, clampToSpace: false);
        }

        private static int Stock(CampaignState s, string id) => HomeInventory.Stock(s, id);

        /// <summary>一张待分配的补给单（与常驻规则同一入口：开单即从库存预留，取消全额退回）。</summary>
        private static WorkOrderRecord Deliver(CampaignState s, BuildingRecord target, int amount = 4, int priority = 0)
        {
            Give(s, "alloy", amount);
            string id = "t-deliver-" + (_seq++).ToString(CultureInfo.InvariantCulture);
            HomeValleyWorkOrders.WorkOrderOpResult r = HomeValleyWorkOrders.TryCreateDeliverPool(s, id, target.BuildingId, "alloy", amount, 2f, 0);
            WorkOrderRecord o = r.Success ? HomeValleyWorkOrders.Find(s, id) : null;
            if (o != null)
            {
                o.Priority = priority;
                HomeValleyWorkOrders.MarkAssignmentDirty();
            }
            return o;
        }

        private static WorkOrderRecord Active(CampaignState s, int id) => HomeValleyWorkOrders.FindActiveOrderForMachine(s, id);

        private static Vector2 Pos(int id) => MachineRegistry.TryGetLivePosition(id, out Vector2 p) ? p : Rec(id)?.WorldPosition ?? Vector2.zero;

        private static void SetAll(CampaignState s, MachineRole role)
        {
            foreach (MachineRecord m in HomeMachines())
            {
                MachineRoster.TrySetRole(s, m.LogicId, role, out _);
            }
        }

        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        // ── A 数据 ─────────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string output) = FgProductionSelfCheck.RunPython(root, "tools/cell_tables/fgdata.py --dump");
            string[] lines = output.Replace("\r", string.Empty).Split('\n');
            string[] rr = lines.Where(l => l.StartsWith("RR\t", StringComparison.Ordinal)).ToArray();
            string[] rrRun = ConfigSystem.Instance.Tables.TbRosterRole.DataList
                .Select(r => string.Join("\t", "RR", r.Id, I(r.Code), r.NameKey, r.DescKey, I(r.Settable), I(r.TakesLabor), I(r.AutoActions), I(r.SortOrder))).ToArray();
            Expect(code == 0 && rr.Length == 7 && rr.SequenceEqual(rrRun), $"A1 fg.TbRosterRole（{rrRun.Length} 种岗位）运行时表与 fgdata_roster.py 逐字段一致（改了源数据没重新生成会失败）");

            var expect = new Dictionary<string, MachineRole>
            {
                ["labor"] = MachineRole.Labor, ["garrison"] = MachineRole.Garrison, ["outpost_labor"] = MachineRole.OutpostLabor,
                ["expedition_reserve"] = MachineRole.ExpeditionReserve, ["on_expedition"] = MachineRole.OnExpedition, ["in_repair"] = MachineRole.InRepair, ["idle"] = MachineRole.Idle,
            };
            bool enumOk = ConfigSystem.Instance.Tables.TbRosterRole.DataList.All(r => expect.TryGetValue(r.Id, out MachineRole m) && (int)m == r.Code)
                          && Enum.GetValues(typeof(MachineRole)).Length == 7 && (int)MachineRole.Labor == 0;
            int settable = MachineRoster.AllRoles.Count(MachineRoster.IsSettable);
            Expect(enumOk && MachineRoster.AllRoles.Count == 7 && settable == 6 && !MachineRoster.IsSettable(MachineRole.OnExpedition),
                $"A2 七种岗位（FGR-ECO-040）与 MachineRole 枚举逐项对应（0 = 劳动，旧档缺字段读成劳动）；名册里能选的 {settable} 种（卡片“六种岗位”，远征中只由系统进入）");

            string src = string.Join("\n", new[]
            {
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/MachineRoster.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/MachineNaming.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/Kit/RosterPanelUIToolkit.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/Kit/WorldBarHudUIToolkit.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Economy/StandingRuleText.cs",
                "TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/MachineRegistry.cs",
            }.Select(p => File.ReadAllText(Path.Combine(root, p))));
            var keys = Regex.Matches(src, "\"((?:roster|machine\\.feedback)\\.[a-z_\\.]+)\"").Cast<Match>().Select(m => m.Groups[1].Value)
                .Where(k => !k.EndsWith(".", StringComparison.Ordinal) && !GridContent.TryGetTuning(k, out _)).Distinct().ToList();
            foreach (MachineRole r in MachineRoster.AllRoles)
            {
                keys.Add(MachineRoster.Row(r).NameKey);
                keys.Add(MachineRoster.Row(r).DescKey);
            }
            var missing = keys.Where(k => string.IsNullOrEmpty(GameText.Get(k, GameLanguage.ZhCn)) || GameText.Get(k, GameLanguage.ZhCn).Contains("⟦")
                                          || string.IsNullOrEmpty(GameText.Get(k, GameLanguage.En)) || GameText.Get(k, GameLanguage.En).Contains("⟦")).Distinct().ToList();
            Expect(keys.Count >= 70 && missing.Count == 0, $"A3 文本键（B16）：源码引用的 {keys.Distinct().Count()} 个 roster.* / 岗位键中英都有" + (missing.Count == 0 ? string.Empty : "；缺：" + string.Join("、", missing.Take(10))));

            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.RosterPanelFirstOpen) && GuidanceHooks.Known.Contains(GuidanceHooks.RosterFirstRoleChange)
                         && GuidanceHooks.Known.Contains(GuidanceHooks.RosterFirstRename);
            CodexEntry cx = ConfigSystem.Instance.Tables.TbCodexEntry.GetOrDefault("codex.economy.roster");
            InputAction ia = ConfigSystem.Instance.Tables.TbInputAction.GetOrDefault(nameof(GameActionId.OpenRoster));
            Expect(hooks && cx != null && cx.Hooks.Contains("roster.first_role_change") && ia != null && ia.DefaultBinding == "N" && ia.Status == "wired" && ia.Owner == "none",
                "A4 引导钩子（第一次打开名册 / 改岗位 / 改名，内容在 FG15-UX-04）、图鉴“机器名册与岗位”、按键“机器名册”默认 N 已接入（DEBT-FG0UX01-01 收窄）");
            bool hasKeep = GridContent.TryGetTuning("work.history_keep", out float keep);
            bool hasCarry = GridContent.TryGetTuning("build.carry_per_trip.erc_001", out float c1);
            Expect(MachineNaming.MaxLength == 16 && hasKeep && keep >= 20 && hasCarry && c1 > 0,
                $"A5 调参：名字最多 {MachineNaming.MaxLength} 个字；已结束工单保留 {keep} 张；每趟取料按机型（erc_001 = {c1}）");
        }

        // ── B 默认与旧档 ───────────────────────────────────────────────────────────

        private static void CheckDefaultsAndOldSaves()
        {
            CampaignState s = NewWorld(7101);
            List<MachineRecord> ms = HomeMachines();
            bool defaults = ms.Count >= 2 && ms.All(m => m.Role == MachineRole.Labor && string.IsNullOrEmpty(m.CustomName) && MachineRoster.EffectiveRole(s, m) == MachineRole.Labor);
            // 旧档：没有 Role / CustomName 字段的机器记录 JSON。
            MachineRecord old = JsonUtility.FromJson<MachineRecord>("{\"LogicId\":5,\"DisplayNumber\":5,\"ChassisId\":\"erc_001\",\"BlueprintId\":\"bp_erc001\",\"IsAlive\":true}");
            bool oldOk = old != null && old.Role == MachineRole.Labor && string.IsNullOrEmpty(old.CustomName);
            // 读档兜底：越界岗位（含不该落盘的“远征中 / 维修中”）读成劳动；非法名字去掉非法字符、截到上限。
            CampaignState bad = s;
            MachineRecord a = ms[0];
            a.Role = (MachineRole)99;
            a.CustomName = "铁锤\n<b>" + new string('甲', 30);
            ms[1].Role = MachineRole.OnExpedition;
            MachineRegistry.ExportToCampaignState(bad);
            MachineRegistry.LoadFromCampaignState(bad);
            MachineRecord la = Rec(a.LogicId);
            MachineRecord lb = Rec(ms[1].LogicId);
            bool sanitized = la.Role == MachineRole.Labor && lb.Role == MachineRole.Labor && la.CustomName != null && !la.CustomName.Contains("\n") && !la.CustomName.Contains("<")
                             && MachineNaming.Length(la.CustomName) == MachineNaming.MaxLength && la.CustomName.StartsWith("铁锤", StringComparison.Ordinal);
            Expect(defaults && oldOk && sanitized,
                $"B 新机器默认“劳动”、默认名；旧档缺字段读成劳动 / 默认名；读档兜底：越界岗位与“远征中”读成劳动，名字去掉换行与 < >、截到 {MachineNaming.MaxLength} 字（{defaults}/{oldOk}/{sanitized}：{la.CustomName}）");
        }

        // ── C FGT-ECO-004 ──────────────────────────────────────────────────────────

        /// <summary>C1 闲置机器不做事（FGR-BASE-020）：全部机器设为闲置，有待分配的补给单也没人接、机器原地不动；一台改回劳动立刻接单送达。</summary>
        private static void CheckIdleDoesNothing()
        {
            CampaignState s = NewWorld(7111);
            BuildingRecord ws = Place(s, "parts_workshop");
            Seconds(1f);
            SetAll(s, MachineRole.Idle);
            Seconds(1f);
            List<MachineRecord> ms = HomeMachines();
            Dictionary<int, Vector2> before = ms.ToDictionary(m => m.LogicId, m => Pos(m.LogicId));
            WorkOrderRecord o = Deliver(s, ws);
            int stockAfterReserve = Stock(s, "alloy");
            Seconds(15f);
            bool untouched = o != null && o.State == WorkOrderState.Ready && o.AssignedMachineLogicId == 0
                             && ms.All(m => Active(s, m.LogicId) == null && Vector2.Distance(before[m.LogicId], Pos(m.LogicId)) < 0.05f);
            MachineRoster.LaborCount idleLabor = MachineRoster.ComputeLabor(s);
            bool ok = MachineRoster.TrySetRole(s, ms[0].LogicId, MachineRole.Labor, out string msg);
            bool taken = StepUntil(() => o.AssignedMachineLogicId == ms[0].LogicId, 5);
            bool delivered = StepUntil(() => o.State == WorkOrderState.Completed, 120);
            bool othersStill = ms.Skip(1).All(m => Active(s, m.LogicId) == null);
            Expect(untouched && idleLabor.Labor == 0 && ok && taken && delivered && othersStill,
                $"C1 FGT-ECO-004 闲置机器不做事：{ms.Count} 台都闲置时补给单 15 秒没人接、机器原地不动、劳动力 0；把 {MachineNaming.Short(ms[0])} 改回劳动（“{msg}”）后它接单送达，其余仍闲置" +
                $"（{untouched}/{taken}/{delivered}/{othersStill}）");
        }

        /// <summary>C2 劳动岗按优先级接活（沿用 ER3-WRK-02 确定性分配）：机器的“搬运”优先级高的先领；同一台机器先领优先级高的单。</summary>
        private static void CheckLaborPriority()
        {
            CampaignState s = NewWorld(7121);
            BuildingRecord ws = Place(s, "parts_workshop");
            Seconds(1f);
            List<MachineRecord> ms = HomeMachines();
            int a = ms[0].LogicId;
            int b = ms[1].LogicId;
            MachineRegistry.TrySetWorkPriority(a, WorkOrderKind.Haul, 1);
            MachineRegistry.TrySetWorkPriority(b, WorkOrderKind.Haul, 4);
            MachineRoster.TrySetRole(s, a, MachineRole.Idle, out _); // 先只留 b，验证“按优先级领单”。
            WorkOrderRecord low = Deliver(s, ws, 2, priority: 0);
            WorkOrderRecord high = Deliver(s, ws, 2, priority: 2);
            bool highFirst = StepUntil(() => high.AssignedMachineLogicId == b || low.AssignedMachineLogicId == b, 5) && high.AssignedMachineLogicId == b && low.AssignedMachineLogicId == 0;
            StepUntil(() => high.State == WorkOrderState.Completed, 120);
            // 两台都劳动、只有一张单：ER3-WRK-02 的确定性分配按机器编号（LogicId）依次为每台空闲机器挑它优先级最高的单——编号小的 a 先领（同一组输入每次结果相同）。
            MachineRoster.TrySetRole(s, a, MachineRole.Labor, out _);
            StepUntil(() => !HomeValleyWorkOrders.IsActive(low), 120);
            StepUntil(() => Active(s, a) == null && Active(s, b) == null, 120);
            WorkOrderRecord one = Deliver(s, ws, 2);
            bool byMachinePriority = StepUntil(() => one.AssignedMachineLogicId != 0, 5) && one.AssignedMachineLogicId == Math.Min(a, b);
            int oneTaker = one.AssignedMachineLogicId;
            // 搬运优先级 0 = 永久禁用这一类：编号小的 a 禁用后由 b 领。
            StepUntil(() => !HomeValleyWorkOrders.IsActive(one) && Active(s, a) == null && Active(s, b) == null, 120);
            MachineRegistry.TrySetWorkPriority(a, WorkOrderKind.Haul, 0);
            WorkOrderRecord two = Deliver(s, ws, 2);
            bool disabled = StepUntil(() => two.AssignedMachineLogicId != 0, 5) && two.AssignedMachineLogicId == b;
            Expect(highFirst && byMachinePriority && disabled,
                $"C2 FGT-ECO-004 劳动岗按优先级接活（沿用 ER3-WRK-02）：同一台机器先领优先级高的单；两台都空闲时按编号确定性分配；对“搬运”优先级为 0 的机器不派、由另一台领（{highFirst}/{byMachinePriority}/{disabled}；领单 #{oneTaker}，b = {b}）");
        }

        /// <summary>C3 远征出发后劳动力下降（真实出发）：出发前远征准备的预估 = 出发后的实际；远征中的机器岗位是“远征中”，改岗位被拒绝并说明。</summary>
        private static void CheckExpeditionLaborDrop()
        {
            CampaignState s = NewWorld(7131);
            Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -4f));
            Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -4f), 100f);
            Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(8f, -4f), 100f);
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower || b.BuildingTypeId == HomeValleyLayout.BuildingTypeGenerator))
                {
                    b.ConstructionState = BuildingConstructionState.Operational; // 测试捷径：信号塔修好（出发的带宽要够；修复流程由 FG4-ECO-05 覆盖）
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            WorldSimulation.StepMany(2);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            int[] roster = MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && m.ChassisId != HomeValleyLayout.Erc002ChassisId)
                .OrderByDescending(m => m.ChassisId == HomeValleyLayout.Erc003ChassisId).ThenByDescending(m => m.LogicId)
                .Take(3).Select(m => m.LogicId).ToArray();
            MachineRoster.LaborCount before = MachineRoster.ComputeLabor(s);
            string forecast = MachineRoster.LaborForecastText(s, roster);
            int predictedAfter = MachineRoster.ComputeLabor(s, new HashSet<int>(roster)).Labor;
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(roster, true);
            if (dep.Outcome != ExpeditionDepartureService.DepartureOutcome.Success)
            {
                Fail($"C3 真实派遣远征失败（{dep.Outcome}：{string.Join("，", dep.Reasons ?? Array.Empty<string>())}）");
                return;
            }
            WorldSimulation.StepMany(10);
            CampaignState now = CampaignSession.Current;
            MachineRoster.LaborCount after = MachineRoster.ComputeLabor(now);
            MachineRecord away = Rec(roster[0]);
            MachineRole stored = away.Role;
            bool locked = MachineRoster.EffectiveRole(now, away) == MachineRole.OnExpedition
                          && !MachineRoster.TrySetRole(now, away.LogicId, MachineRole.Idle, out string why) && why.Contains("远征") && away.Role == stored;
            string whyText = MachineRoster.TrySetRole(now, away.LogicId, MachineRole.Garrison, out string why2) ? "（竟然改成功了）" : why2;
            int batch = MachineRoster.TrySetRoles(now, new[] { away.LogicId, HomeMachines()[0].LogicId }, MachineRole.ExpeditionReserve, out string batchMsg);
            bool batchPartial = batch == 1 && batchMsg.Contains("1 台没改") && batchMsg.Contains("远征");
            int drop = MachineRoster.ForecastDropPercent(before.Labor, after.Labor);
            Expect(before.Labor == 5 && after.Labor == 2 && predictedAfter == after.Labor && forecast.Contains("5 → 2") && forecast.Contains(drop + "%") && locked && batchPartial,
                $"C3 FGT-ECO-004 真实出发后家园劳动力 {before.Labor} → {after.Labor}（预估“{forecast}”与实际一致）；远征中的机器岗位显示“远征中”，改岗位被拒绝：“{whyText}”；" +
                $"批量里含远征机器时其余照改：“{batchMsg}”（{locked}/{batchPartial}）");
            // 击杀记到机器名下（FGR-ECO-041 详情“击杀”）：远征地点内核里，以这台机器为来源给敌人致命一击（与机器武器命中同一条伤害路径），阵亡事件结算后它的击杀 +1，别的机器不变。
            CombatSite site = CombatSites.Get(FracturedCityLayout.RegionId);
            RegionEnemyRecord foe = now.RegionEnemies?.FirstOrDefault(x => x != null && x.IsAlive && x.RegionId == FracturedCityLayout.RegionId
                && site != null && site.TryGetEnemyUnit(x.EnemyInstanceId, out int eu) && site.Kernel.TryGetUnit(eu, out BinGames.Sim.Combat.CombatUnitView ev)
                && (ev.Flags & BinGames.Sim.Combat.CombatUnitFlags.ExternalHealth) == 0);
            int killsBefore = away.KillCount;
            int othersBefore = roster.Skip(1).Sum(id => Rec(id).KillCount);
            bool killed = false;
            if (foe != null && site.TryGetEnemyUnit(foe.EnemyInstanceId, out int foeUnit) && site.TryGetMachineUnit(away.LogicId, out int myUnit))
            {
                site.Kernel.Damage(foeUnit, 1e6f, myUnit);
                site.FlushPendingEvents();
                killed = !foe.IsAlive;
            }
            Expect(killed && away.KillCount == killsBefore + 1 && roster.Skip(1).Sum(id => Rec(id).KillCount) == othersBefore && RosterPanelUIToolkit.DetailText(now, away).Contains("击杀 " + away.KillCount),
                $"C3c 击杀记到机器名下：{MachineNaming.Short(away)} 给 {foe?.EnemyTypeId} 致命一击后击杀 {killsBefore} → {away.KillCount}，其余机器不变，详情页显示（{killed}）");
            // 返回家园后可以改（把记录移回家园 = 远征返回的落点，返回流程由 FG5 / FG8 的远征自检覆盖）。
            MachineRegistry.MoveToRegion(away, HomeValleyLayout.RegionId);
            bool back = MachineRoster.TrySetRole(now, away.LogicId, MachineRole.Idle, out string backMsg) && away.Role == MachineRole.Idle;
            Expect(back, $"C3b 机器回到家园后岗位恢复可改（“{backMsg}”）");
        }

        // ── D 改岗位的副作用 ─────────────────────────────────────────────────────────

        /// <summary>
        /// D1 劳动中的机器改为闲置：手上的补给单交还待分配池（预留的物品留在单上，仓库不增不减）、机器停下，另一台劳动机接着送到；
        /// 改为远征预备不接新单（且出现在远征预备名单里）；改回劳动又接活。
        /// </summary>
        private static void CheckRoleChangeReleasesWork()
        {
            CampaignState s = NewWorld(7141);
            BuildingRecord ws = Place(s, "parts_workshop");
            ProductionService.TrySetRecipe(s, ws.BuildingId, "part", out _);
            Seconds(1f);
            List<MachineRecord> ms = HomeMachines();
            int a = ms[0].LogicId;
            int b = ms[1].LogicId;
            MachineRoster.TrySetRole(s, b, MachineRole.Idle, out _);
            int stock0 = Stock(s, "alloy");
            WorkOrderRecord o = Deliver(s, ws, 6);
            bool moving = o != null && StepUntil(() => o.AssignedMachineLogicId == a && o.State == WorkOrderState.Reserved, 5);
            Seconds(0.5f);
            bool ok = MachineRoster.TrySetRole(s, a, MachineRole.Idle, out string msg);
            Seconds(1f);
            Vector2 p1 = Pos(a);
            Seconds(5f);
            bool stopped = Vector2.Distance(p1, Pos(a)) < 0.05f && Active(s, a) == null;
            bool pooled = o != null && o.State == WorkOrderState.Ready && o.AssignedMachineLogicId == 0 && o.ReservedItemAmount == 6 && Stock(s, "alloy") == stock0;
            // 另一台改回劳动：接手这张补给单送到。
            MachineRoster.TrySetRole(s, b, MachineRole.Labor, out _);
            bool takenOver = o != null && StepUntil(() => o.AssignedMachineLogicId == b, 5);
            bool delivered = takenOver && StepUntil(() => o.State == WorkOrderState.Completed, 120);
            // 改为远征预备：不接活（新单没人领）。
            MachineRoster.TrySetRole(s, b, MachineRole.Idle, out _);
            MachineRoster.TrySetRole(s, a, MachineRole.ExpeditionReserve, out _);
            WorkOrderRecord o2 = Deliver(s, ws, 2);
            Seconds(8f);
            bool reserveIdle = o2.AssignedMachineLogicId == 0 && MachineRoster.ReserveIds(s).SequenceEqual(new[] { a });
            MachineRoster.TrySetRole(s, a, MachineRole.Labor, out _);
            bool resumed = StepUntil(() => o2.AssignedMachineLogicId == a, 5);
            Expect(moving && ok && stopped && pooled && takenOver && delivered && reserveIdle && resumed && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RosterFirstRoleChange),
                $"D1 送货途中改为闲置（“{msg}”）：机器停下不再动，补给单回到待分配池、预留的 6 件留在单上（仓库 {stock0} → {Stock(s, "alloy")}），另一台劳动机接手送到；" +
                $"改为远征预备不接新单（且出现在远征预备名单里）；改回劳动又接活（{moving}/{stopped}/{pooled}/{takenOver}/{delivered}/{reserveIdle}/{resumed}）");
        }

        /// <summary>
        /// D1b 审查回归（P1）：真实的阈值补给规则派出的补给单，送货途中把搬运机改为闲置——补给单交还待分配池，规则的持有不被记为“玩家改动过”
        /// （此前按取消处理，规则判定“你改动了”并对这座建筑永久停供），这张单由另一台劳动机送达。
        /// </summary>
        private static void CheckRuleSupplyRoleChange()
        {
            CampaignState s = NewWorld(7145);
            BuildingRecord ws = Place(s, "parts_workshop");
            ProductionService.TrySetRecipe(s, ws.BuildingId, "part", out _);
            Seconds(1f);
            List<MachineRecord> ms = HomeMachines();
            int a = ms[0].LogicId;
            int b = ms[1].LogicId;
            MachineRoster.TrySetRole(s, b, MachineRole.Idle, out _);
            Give(s, "alloy", 30);
            StandingRuleService.TryCreate(s, StandingRuleService.KindSupply, out StandingRuleRecord r, out _);
            StandingRuleService.TrySetItem(s, r.Serial, "alloy", out _);
            StandingRuleService.TrySetThreshold(s, r.Serial, 5, out _);
            StandingRuleService.TrySetBatch(s, r.Serial, 10, out _);
            bool target = StandingRuleService.TryAddTarget(s, r.Serial, ws.BuildingId, out _);
            StandingRuleService.TrySetEnabled(s, r.Serial, true, out _);
            StandingRuleService.RequestEvaluation(s);
            WorkOrderRecord o = null;
            bool issued = StepUntil(() => (o = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>())
                              .LastOrDefault(x => x != null && x.Kind == WorkOrderKind.Deliver && x.RuleSerial == r.Serial && HomeValleyWorkOrders.IsActive(x))) != null
                                                && o.AssignedMachineLogicId == a, 10);
            Seconds(0.5f);
            bool changed = issued && MachineRoster.TrySetRole(s, a, MachineRole.Idle, out _);
            StandingRuleService.RequestEvaluation(s);
            Seconds(2f);
            RuleHoldRecord hold = o == null ? null : StandingRuleService.Holds(s).FirstOrDefault(h => h != null && h.OrderId == o.WorkOrderId);
            bool kept = hold != null && !hold.Overridden && HomeValleyWorkOrders.IsActive(o) && o.AssignedMachineLogicId == 0
                        && !StandingRuleService.LogEntries(s).Any(e => e.Key == "rules.log.override" && e.Rule == r.Serial);
            MachineRoster.TrySetRole(s, b, MachineRole.Labor, out _);
            bool deliveredByB = o != null && StepUntil(() => o.AssignedMachineLogicId == b, 5) && StepUntil(() => o.State == WorkOrderState.Completed, 120);
            bool supplied = StandingRuleService.LogEntries(s).Any(e => e.Key == "rules.log.supply_done" && e.Rule == r.Serial);
            Expect(target && issued && changed && kept && deliveredByB && supplied,
                $"D1b 补给规则 R{r.Serial} 的补给单在送货途中被改岗位：单子回到待分配池、规则持有没被记为“玩家改动过”（{kept}），由另一台劳动机送达并记“送到”" +
                $"（{issued}/{changed}/{deliveredByB}/{supplied}）");
        }

        /// <summary>D2 驻防：维持器派它去驻防点待命、不接劳动单；玩家接管 = 暂停不再拉回；名册再设一次恢复；换驻防点按新点派；规则送修替下后修完再回驻防。</summary>
        private static void CheckGarrison()
        {
            CampaignState s = NewWorld(7151);
            BuildingRecord ws = Place(s, "parts_workshop");
            BuildingRecord bay = Bay(s);
            Seconds(1f);
            List<MachineRecord> ms = HomeMachines();
            int g = ms[0].LogicId;
            MachineRoster.TrySetRole(s, ms[1].LogicId, MachineRole.Idle, out _);
            bool set = MachineRoster.TrySetRole(s, g, MachineRole.Garrison, out _);
            WorkOrderRecord go = null;
            bool issued = StepUntil(() => (go = Active(s, g)) != null, 3) && go.Kind == WorkOrderKind.Garrison && go.IssuerId == HomeValleyWorkOrders.RosterIssuer;
            bool holding = StepUntil(() => go.State == WorkOrderState.InProgress, 90);
            WorkOrderRecord d = Deliver(s, ws, 2);
            Seconds(6f);
            bool noLabor = d.AssignedMachineLogicId == 0 && Active(s, g) == go;
            string trace = MachineRoster.CurrentWorkText(s, Rec(g));
            // 换驻防点：按新点重新派。
            bool point = MachineRoster.TrySetGarrisonPoint(s, g, ws.BuildingId, out _);
            WorkOrderRecord go2 = null;
            bool moved = StepUntil(() => (go2 = Active(s, g)) != null && go2 != go, 3) && go2.TargetId == ws.BuildingId;
            // 玩家接入（接入 / 右键移动 / 停止 都调 OnMachinePossessed）：单子取消、暂停、不再拉回。
            HomeValleyWorkOrders.OnMachinePossessed(s, g);
            Seconds(6f);
            bool suspended = Active(s, g) == null && MachineRoster.GarrisonSuspended(s, Rec(g)) && MachineRoster.CurrentWorkText(s, Rec(g)).Contains("暂停");
            bool resume = MachineRoster.TrySetRole(s, g, MachineRole.Garrison, out string resumeMsg) && StepUntil(() => Active(s, g)?.Kind == WorkOrderKind.Garrison, 3);
            // D2b 审查回归（P1）：真实右键派工入口（HomeValleyController.TryContextWork，RTS 右键情境命令）——选中驻防中的机器右键一座虚影 = 玩家接管：
            // 驻防单记“玩家接管”、机器去施工；玩家撤掉这个规划后机器空出来，维持器也不把它拉回驻防点（此前走普通取消，活一结束就被拉回去）。
            WorkOrderRecord garrisonBefore = Active(s, g);
            bool clicked = RightClickGhost(s, g, out string hitName);
            WorkOrderRecord build = Active(s, g);
            bool tookBuild = clicked && build != null && build.Kind == WorkOrderKind.Build && garrisonBefore != null && garrisonBefore.Kind == WorkOrderKind.Garrison
                             && garrisonBefore.State == WorkOrderState.Cancelled && garrisonBefore.FailureReason == HomeValleyWorkOrders.PlayerTookOverReason;
            Seconds(4f);
            bool keptWorking = build != null && Active(s, g) == build && MachineRoster.GarrisonSuspended(s, Rec(g));
            if (build != null)
            {
                HomeValleyWorkOrders.CancelOrder(s, build.WorkOrderId, Pos(g)); // 玩家撤掉这个规划
            }
            Seconds(6f);
            bool notPulledBack = Active(s, g) == null && MachineRoster.GarrisonSuspended(s, Rec(g));
            bool resume2 = MachineRoster.TrySetRole(s, g, MachineRole.Garrison, out _) && StepUntil(() => Active(s, g)?.Kind == WorkOrderKind.Garrison, 3);
            Expect(tookBuild && keptWorking && notPulledBack && resume2,
                $"D2b 驻防中的机器被玩家右键派去施工（真实右键入口，射线点中“{hitName}”）：驻防单记为玩家接管、机器去施工；规划撤掉后机器空着，" +
                $"维持器不把它拉回驻防点；名册再设一次驻防恢复（{clicked}/{tookBuild}/{keptWorking}/{notPulledBack}/{resume2}）");
            // 规则送修替下驻防：修满后维持器再派回驻防点。
            MachineRegistry.ApplyDamage(g, Rec(g).MaxHealth * 0.6f);
            StandingRuleService.TryCreate(s, StandingRuleService.KindRepair, out StandingRuleRecord rule, out _);
            StandingRuleService.TrySetEnabled(s, rule.Serial, true, out _);
            StandingRuleService.RequestEvaluation(s);
            WorkOrderRecord rep = null;
            bool sent = StepUntil(() => (rep = Active(s, g)) != null && rep.Kind == WorkOrderKind.MachineRepair, 5);
            bool inRepair = sent && MachineRoster.EffectiveRole(s, Rec(g)) == MachineRole.InRepair;
            bool healed = sent && StepUntil(() => rep.State == WorkOrderState.Completed, 120);
            bool backToGarrison = StepUntil(() => Active(s, g)?.Kind == WorkOrderKind.Garrison, 5) && MachineRoster.EffectiveRole(s, Rec(g)) == MachineRole.Garrison;
            Expect(set && issued && holding && noLabor && trace.Contains("驻防") && point && moved && suspended && resume && inRepair && healed && backToGarrison,
                $"D2 驻防：去驻防点待命（“{trace}”）、不接劳动单；换驻防点按新点派；玩家接管后暂停不拉回；名册再设一次恢复（“{resumeMsg}”）；规则送修替下后修满回驻防" +
                $"（{issued}/{holding}/{noLabor}/{moved}/{suspended}/{resume}/{inRepair}/{healed}/{backToGarrison}）");
        }

        /// <summary>
        /// 真实右键派工入口（与玩家右键同一个方法 HomeValleyController.TryContextWork）：在空地放一座发电机虚影，镜头看向它，屏幕点 → 射线 → 右键派工
        /// （与 FgConstructionSelfCheck.CheckRightClickKeepsGhost 同一做法）。返回右键是否被当作工作命令处理。
        /// </summary>
        private static bool RightClickGhost(CampaignState s, int logicId, out string hitName)
        {
            hitName = "（没有家园、空地或虚影方块）";
            HomeValleyController home = WorldSimulation.Home;
            GridCell? at = FgProductionSelfCheck.FindFree(s, "generator_2", 8f, 26f);
            if (home == null || home.Combat == null || !at.HasValue)
            {
                return false;
            }
            GridOpResult placed = HomeGridService.TryPlace(s, "generator_2", at.Value, 0);
            if (string.IsNullOrEmpty(placed.BuildingId))
            {
                return false;
            }
            WorldSimulation.Frame(0.02f); // 建出虚影方块（观察中的家园画面）
            Transform ghostGo = GameObject.Find("[HomeValley]")?.transform.Find("Building_" + HomeValleyController.LocalKey(placed.BuildingId));
            Camera cam = WorldView.EnsureCamera();
            if (ghostGo == null || cam == null || !home.Combat.TryGetMachineMarker(logicId, out HomeValleyMachineMarker marker))
            {
                return false;
            }
            cam.transform.position = ghostGo.position + new Vector3(0f, 25f, -12f);
            cam.transform.LookAt(ghostGo.position);
            Physics.SyncTransforms();
            Vector3 pointer = cam.WorldToScreenPoint(ghostGo.position + Vector3.up * 0.2f);
            hitName = Physics.Raycast(cam.ScreenPointToRay(pointer), out RaycastHit hit, 500f) ? hit.collider.gameObject.name : "（射线没打中）";
            System.Reflection.MethodInfo ctx = typeof(HomeValleyController).GetMethod("TryContextWork",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return ctx != null && (bool)ctx.Invoke(home, new object[] { marker, pointer });
        }

        /// <summary>
        /// D2c 审查回归（P1）：驻防点被悬崖围死（到不了）——维持器只派一张驻防单，失败后不再补派：跑 60 秒在办工单数不增长、名册驻防单只有一张、
        /// “无法到达”通知只发一次，详情页写明原因；换个驻防点（归还核心）后按新点重新派并到位。
        /// </summary>
        private static void CheckGarrisonUnreachable()
        {
            CampaignState s = NewWorld(7153);
            Seconds(1f);
            GridCell? open = FgNavSelfCheck.FindOpenArea(s, 5, 12, 40);
            if (!open.HasValue)
            {
                Fail("D2c 找不到空地放被围死的驻防点");
                return;
            }
            BuildingGrid bg = GridContent.Building("parts_workshop");
            GridCell center = open.Value;
            GridCell origin = new GridCell(center.X - bg.FootprintW / 2, center.Y - bg.FootprintH / 2);
            BuildingRecord pen = PlaceAt(s, "parts_workshop", origin);
            FgNavSelfCheck.CliffRing(s, center, Math.Max(bg.FootprintW, bg.FootprintH) / 2 + 2);
            List<MachineRecord> ms = HomeMachines();
            int g = ms[0].LogicId;
            MachineRoster.TrySetRole(s, ms[1].LogicId, MachineRole.Idle, out _);
            int Notes() => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == "unreachable").Sum(n => n.Count);
            int ActiveCount() => (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(HomeValleyWorkOrders.IsActive);
            string prefix = "roster-garrison-" + I(g) + "-";
            int RosterGarrisons() => (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(o => o.WorkOrderId.StartsWith(prefix, StringComparison.Ordinal));
            int notes0 = Notes();
            bool set = MachineRoster.TrySetRole(s, g, MachineRole.Garrison, out _) && MachineRoster.TrySetGarrisonPoint(s, g, pen.BuildingId, out _);
            bool failed = StepUntil(() => MachineRoster.GarrisonBlocked(s, Rec(g)), 60);
            Seconds(5f);
            int active1 = ActiveCount();
            int orders1 = RosterGarrisons();
            Seconds(55f);
            int active2 = ActiveCount();
            int orders2 = RosterGarrisons();
            int notes = Notes() - notes0;
            string detail = MachineRoster.CurrentWorkText(s, Rec(g));
            bool noGrowth = failed && active2 <= active1 && orders1 == 1 && orders2 == 1 && Active(s, g) == null;
            // 换个驻防点（空 = 归还核心）：按新点重新派、到位待命。
            bool repoint = MachineRoster.TrySetGarrisonPoint(s, g, null, out _);
            WorkOrderRecord go = null;
            bool held = repoint && StepUntil(() => (go = Active(s, g)) != null && go.Kind == WorkOrderKind.Garrison && go.State == WorkOrderState.InProgress, 90);
            Expect(set && noGrowth && notes == 1 && detail.Contains("到不了驻防点") && held,
                $"D2c 驻防点被悬崖围死：维持器只派了 {orders2} 张驻防单（失败后不再补派），在办工单 {active1} → {active2} 不增长，“无法到达”通知 {notes} 条；" +
                $"详情页写“{detail}”；换驻防点为归还核心后重新派并到位（{failed}/{held}）");
        }

        /// <summary>
        /// D2d 复审回归（P1）：常驻“机器维修”规则派的送修单在路上持续受阻（寻路仍判可达，只是走不动）——真实赶路看门狗判失败（path-blocked）后，
        /// 规则持有按“仍在执行”保留：之后多次检查都不再补派、在办送修单为 0、通知只发一次（写明机器名字与“路上持续受阻”）；
        /// 关掉规则时照常收尾，再打开（条件仍成立）照常派出新单。此前 Status 只认“无法到达”，每次检查都开一张新单，机器原地“出发—卡住—失败—再出发”。
        /// </summary>
        private static void CheckRulePathBlocked()
        {
            CampaignState s = NewWorld(7157);
            Bay(s);
            Seconds(1f);
            List<MachineRecord> ms = HomeMachines();
            int a = ms[0].LogicId;
            for (int i = 1; i < ms.Count; i++)
            {
                MachineRoster.TrySetRole(s, ms[i].LogicId, MachineRole.Idle, out _);
            }
            MachineRegistry.ApplyDamage(a, Rec(a).MaxHealth * 0.6f);
            StandingRuleService.TryCreate(s, StandingRuleService.KindRepair, out StandingRuleRecord rule, out _);
            StandingRuleService.TrySetEnabled(s, rule.Serial, true, out _);
            StandingRuleService.RequestEvaluation(s);
            WorkOrderRecord rep = null;
            bool sent = StepUntil(() => (rep = Active(s, a)) != null && rep.Kind == WorkOrderKind.MachineRepair, 5);
            if (!sent || rep.State != WorkOrderState.Reserved)
            {
                Fail($"D2d 规则没有派出赶路中的送修单（{sent}/{rep?.State}）");
                return;
            }
            int Notes() => NotificationCenter.History.Where(n => n.Type != null && n.Type.Id == "unreachable").Sum(n => n.Count);
            HashSet<string> RepairIds() => new HashSet<string>((s.WorkOrders ?? Array.Empty<WorkOrderRecord>())
                .Where(o => o.Kind == WorkOrderKind.MachineRepair).Select(o => o.WorkOrderId));
            int ActiveRepairs() => (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(o => o.Kind == WorkOrderKind.MachineRepair && HomeValleyWorkOrders.IsActive(o));
            int notes0 = Notes();
            // 真实赶路看门狗：机器原地不动（路被挡住；寻路图上仍可达，不走“无法到达”）。
            Vector2 stuckAt = Pos(a);
            for (int i = 0; i < 40 && rep.State == WorkOrderState.Reserved; i++)
            {
                HomeValleyWorkOrders.Tick(s, 0.5f, id => id == a ? stuckAt : (Vector2?)null, id => { }, id => false, o => { });
            }
            bool blocked = rep.State == WorkOrderState.Failed && rep.FailureReason == HomeValleyWorkOrders.PathBlockedReason;
            HashSet<string> before = RepairIds();
            for (int i = 0; i < 4; i++)
            {
                StandingRuleService.Evaluate(s);
                Seconds(2f);
            }
            HashSet<string> after = RepairIds();
            int fresh = after.Count(id => !before.Contains(id));
            int active = ActiveRepairs();
            int notes = Notes() - notes0;
            string last = NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == "unreachable")?.Text ?? string.Empty;
            bool held = blocked && fresh == 0 && active == 0 && notes == 1 && last.Contains("持续受阻") && last.Contains(MachineNaming.Short(Rec(a)));
            // 条件结束（关规则）照常收尾；再打开、机器仍受伤 → 照常派出新单。
            StandingRuleService.TrySetEnabled(s, rule.Serial, false, out _);
            StandingRuleService.Evaluate(s);
            StandingRuleService.TrySetEnabled(s, rule.Serial, true, out _);
            StandingRuleService.Evaluate(s);
            WorkOrderRecord again = null;
            bool resent = StepUntil(() => (again = Active(s, a)) != null && again.Kind == WorkOrderKind.MachineRepair && again != rep, 5);
            Expect(held && resent,
                $"D2d 机器维修规则派的送修单路上持续受阻（真实赶路看门狗判 {rep.FailureReason}）：之后 4 次检查新开送修单 {fresh} 张、在办 {active} 张，" +
                $"通知 {notes} 条（“{last}”）；关规则收尾、再打开照常派出新单（{blocked}/{held}/{resent}）");
        }

        private static BuildingRecord PlaceAt(CampaignState s, string typeId, GridCell at)
        {
            BuildingGrid bg = GridContent.Building(typeId);
            HomeValleyLayout.PowerProfile.TryGetValue(typeId, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + typeId + "#" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = typeId,
                RegionId = HomeValleyLayout.RegionId,
                GridX = at.X,
                GridY = at.Y,
                Position = GridMath.FootprintCenter(at, bg.FootprintW, bg.FootprintH, 0),
                Health = BuildingOps.MaxDurability(typeId),
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            HomeValleyPowerGrid.Recompute(s);
            FgProductionSelfCheck.Resync(s);
            return r;
        }

        /// <summary>D3 名册送修（承接 DEBT-FG4ECO06-06“手动送修”）：维修中 = 去维修台修满后回原岗位；没有维修台 / 满血拒绝并说明。</summary>
        private static void CheckManualRepair()
        {
            CampaignState s = NewWorld(7161);
            Seconds(1f);
            List<MachineRecord> ms = HomeMachines();
            int m = ms[0].LogicId;
            bool full = !MachineRoster.TrySetRole(s, m, MachineRole.InRepair, out string fullMsg) && fullMsg.Contains("耐久已满");
            MachineRegistry.ApplyDamage(m, Rec(m).MaxHealth * 0.5f);
            bool noBay = s.BuildingRecords.All(b => b == null || b.BuildingTypeId != HomeValleyLayout.BuildingTypeRepairBay || b.ConstructionState != BuildingConstructionState.Operational)
                ? !MachineRoster.TrySetRole(s, m, MachineRole.InRepair, out string bayMsg) && bayMsg.Contains("维修台")
                : true;
            BuildingRecord bay = Bay(s);
            MachineRoster.TrySetRole(s, m, MachineRole.ExpeditionReserve, out _);
            bool sent = MachineRoster.TrySetRole(s, m, MachineRole.InRepair, out string msg);
            WorkOrderRecord o = Active(s, m);
            bool order = o != null && o.Kind == WorkOrderKind.MachineRepair && o.IssuerId == HomeValleyWorkOrders.RosterIssuer && o.TargetId == bay.BuildingId
                         && Rec(m).Role == MachineRole.ExpeditionReserve && MachineRoster.EffectiveRole(s, Rec(m)) == MachineRole.InRepair;
            bool again = !MachineRoster.TrySetRole(s, m, MachineRole.InRepair, out _);
            bool healed = o != null && StepUntil(() => o.State == WorkOrderState.Completed, 150) && Mathf.Approximately(Rec(m).Health, Rec(m).MaxHealth);
            string traced = o != null ? StandingRuleService.DescribeOrder(s, o) : string.Empty;
            bool back = MachineRoster.EffectiveRole(s, Rec(m)) == MachineRole.ExpeditionReserve;
            // 送修途中在名册里改选别的岗位 = 不修了：名册送修单取消，岗位立即生效。
            MachineRegistry.ApplyDamage(m, Rec(m).MaxHealth * 0.5f);
            MachineRoster.TrySetRole(s, m, MachineRole.InRepair, out _);
            WorkOrderRecord o2 = Active(s, m);
            bool changedMind = o2 != null && o2.Kind == WorkOrderKind.MachineRepair && MachineRoster.TrySetRole(s, m, MachineRole.Labor, out _)
                               && o2.State == WorkOrderState.Cancelled && MachineRoster.EffectiveRole(s, Rec(m)) == MachineRole.Labor;
            Expect(changedMind, "D3b 名册送修途中改选“劳动”：名册送修单取消、岗位立即变为劳动");
            Expect(full && noBay && sent && order && again && healed && back && traced.Contains("名册"),
                $"D3 名册送修：满血拒绝（“{fullMsg}”）、没有维修台拒绝；送修（“{msg}”）后岗位显示“维修中”、工单写“{traced}”、存档里的岗位不变；修满回到“远征预备”（{order}/{healed}/{back}）");
        }

        /// <summary>
        /// D3c 审查回归（P0）：暂停中（时钟不走）在名册里反复“维修中 → 劳动 → 维修中”——每张名册送修单 ID 都不同，全部工单 ID 唯一；
        /// 系统返还物的搬运单同一份地面物反复生成也不撞号（P2）；存档后用真文件读档（CampaignRestoreOrchestrator.Restore）成功（重复 ID 会被整档拒绝）。
        /// </summary>
        private static void CheckPausedResendSaveLoad()
        {
            CampaignState s = NewWorld(7165);
            Bay(s);
            Seconds(1f);
            int m = HomeMachines()[0].LogicId;
            MachineRegistry.ApplyDamage(m, Rec(m).MaxHealth * 0.5f);
            GameClock.SetPaused(true);
            long tick = GameClock.Ticks;
            var repairIds = new List<string>();
            bool cycles = true;
            for (int i = 0; i < 3; i++)
            {
                cycles &= MachineRoster.TrySetRole(s, m, MachineRole.InRepair, out _);
                repairIds.Add(Rec(m).RoleOrderId);
                if (i < 2)
                {
                    cycles &= MachineRoster.TrySetRole(s, m, MachineRole.Labor, out _);
                }
            }
            // 同一份返还地面物反复生成搬运单（生成 → 取消 → 再生成）。
            GroundItemRecord drop = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, HomeValleyLayout.Core.Position + new Vector2(6f, 6f),
                CampaignEconomyLedger.ResourceScrap, 5, "t-roster-drop");
            var haulIds = new List<string>();
            for (int i = 0; i < 3 && drop != null; i++)
            {
                HomeValleyWorkOrders.WorkOrderOpResult hr = HomeValleyWorkOrders.TryCreateHaulPool(s, drop.GroundItemId);
                haulIds.Add(hr.WorkOrderId);
                HomeValleyWorkOrders.CancelOrder(s, hr.WorkOrderId, drop.Position);
            }
            bool sameTick = GameClock.Ticks == tick;
            List<string> all = (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o != null).Select(o => o.WorkOrderId).ToList();
            bool unique = all.Count == all.Distinct(StringComparer.Ordinal).Count() && repairIds.Distinct(StringComparer.Ordinal).Count() == 3
                          && haulIds.Count == 3 && haulIds.Distinct(StringComparer.Ordinal).Count() == 3;
            bool inRepair = MachineRoster.EffectiveRole(s, Rec(m)) == MachineRole.InRepair && Active(s, m)?.WorkOrderId == repairIds[2];
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            GameClock.SetPaused(false);
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            HomeValleyPowerGrid.ResetForTests();
            ProductionService.ResetForTests();
            BuildingOps.ResetForTests();
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            bool restored = rr.Success && rr.State?.WorkOrders != null && rr.State.WorkOrders.Any(o => o.WorkOrderId == repairIds[2] && HomeValleyWorkOrders.IsActive(o));
            Expect(cycles && sameTick && unique && inRepair && save.Success && restored,
                $"D3c 暂停中（第 {tick} 步不动）反复送修三次：名册送修单 ID 各不相同（{string.Join(" / ", repairIds)}）；返还物搬运单反复生成也不撞号（{string.Join(" / ", haulIds)}）；" +
                $"全部 {all.Count} 张工单 ID 唯一；存档后真文件读档{(rr.Success ? "成功" : "失败：" + rr.Message)}，最后那张送修单仍在办");
        }

        /// <summary>D4 闲置的机器常驻规则也不派（FGR-BASE-020），规则上写明原因；劳动岗的照派。</summary>
        private static void CheckRulesSkipIdle()
        {
            CampaignState s = NewWorld(7171);
            Bay(s);
            Seconds(1f);
            List<MachineRecord> ms = HomeMachines();
            int idle = ms[0].LogicId;
            int labor = ms[1].LogicId;
            MachineRoster.TrySetRole(s, idle, MachineRole.Idle, out _);
            MachineRegistry.ApplyDamage(idle, Rec(idle).MaxHealth * 0.7f);
            StandingRuleService.TryCreate(s, StandingRuleService.KindRepair, out StandingRuleRecord rule, out _);
            StandingRuleService.TrySetEnabled(s, rule.Serial, true, out _);
            StandingRuleService.RequestEvaluation(s);
            Seconds(3f);
            bool skipped = Active(s, idle) == null;
            string issue = StandingRuleService.IssueText(rule) ?? string.Empty;
            MachineRegistry.ApplyDamage(labor, Rec(labor).MaxHealth * 0.7f);
            StandingRuleService.RequestEvaluation(s);
            bool laborSent = StepUntil(() => Active(s, labor)?.Kind == WorkOrderKind.MachineRepair, 5);
            Expect(skipped && issue.Contains("闲置") && laborSent,
                $"D4 机器维修规则（范围 = 全部机器）：闲置的受伤机器不派，规则写“{issue}”；劳动岗的受伤机器照派（{skipped}/{laborSent}）");
        }

        /// <summary>D5 负向：前哨劳动没有前哨站时拒绝（有前哨站后可设）；阵亡、厂内、远征中不可选、和现在一样都拒绝并说明，岗位不变。</summary>
        private static void CheckRoleNegatives()
        {
            CampaignState s = NewWorld(7181);
            Seconds(1f);
            List<MachineRecord> ms = HomeMachines();
            int a = ms[0].LogicId;
            bool outpost = !MachineRoster.TrySetRole(s, a, MachineRole.OutpostLabor, out string outMsg) && outMsg.Contains("前哨站") && Rec(a).Role == MachineRole.Labor;
            MachineRoster.HasOutpostProvider = _ => true;
            bool outpostOk = MachineRoster.TrySetRole(s, a, MachineRole.OutpostLabor, out _) && !MachineRoster.TakesLabor(Rec(a));
            MachineRoster.HasOutpostProvider = null;
            bool notSettable = !MachineRoster.TrySetRole(s, a, MachineRole.OnExpedition, out string nsMsg) && nsMsg.Contains("系统");
            MachineRoster.TrySetRole(s, a, MachineRole.Idle, out _);
            bool same = !MachineRoster.TrySetRole(s, a, MachineRole.Idle, out string sameMsg) && sameMsg.Contains("已经是");
            Rec(ms[1].LogicId).IsInFactory = true;
            bool factory = !MachineRoster.TrySetRole(s, ms[1].LogicId, MachineRole.Idle, out string fMsg) && fMsg.Contains("厂内");
            Rec(ms[1].LogicId).IsInFactory = false;
            MachineRegistry.MarkDeadByLogicId(ms[1].LogicId);
            bool dead = !MachineRoster.TrySetRole(s, ms[1].LogicId, MachineRole.Idle, out string dMsg) && dMsg.Contains("阵亡") && Rec(ms[1].LogicId).Role == MachineRole.Labor;
            bool unknown = !MachineRoster.TrySetRole(s, 98765, MachineRole.Idle, out string uMsg) && uMsg.Length > 0;
            bool emptyBatch = MachineRoster.TrySetRoles(s, Array.Empty<int>(), MachineRole.Idle, out string ebMsg) == 0 && ebMsg.Contains("勾选");
            Expect(outpost && outpostOk && notSettable && same && factory && dead && unknown && emptyBatch,
                $"D5 负向：前哨劳动（没有前哨站：“{outMsg}”；有了才可设）、“远征中”不能选（“{nsMsg}”）、和现在一样（“{sameMsg}”）、厂内（“{fMsg}”）、阵亡（“{dMsg}”）、" +
                $"未知机器、空批量（“{ebMsg}”）都拒绝并说明，岗位不变（{outpost}/{outpostOk}/{notSettable}/{same}/{factory}/{dead}/{unknown}/{emptyBatch}）");
        }

        // ── E 名字 ───────────────────────────────────────────────────────────────

        private static void CheckNames()
        {
            CampaignState s = NewWorld(7191);
            MachineRecord m = HomeMachines()[0];
            string defLong = MachineNaming.Long(m);
            string defShort = MachineNaming.Short(m);
            bool empty = !MachineNaming.TryRename(m.LogicId, "   ", out string e1) && e1.Contains("不能为空") && m.CustomName == null;
            bool emptyNull = !MachineNaming.TryRename(m.LogicId, null, out _);
            string seventeen = new string('长', 17);
            bool tooLong = !MachineNaming.TryRename(m.LogicId, seventeen, out string e2) && e2.Contains("16") && e2.Contains("17") && m.CustomName == null;
            bool control = !MachineNaming.TryRename(m.LogicId, "铁\n锤", out string e3) && e3.Contains("换行") && m.CustomName == null;
            bool markup = !MachineNaming.TryRename(m.LogicId, "<color=red>x</color>", out _) && m.CustomName == null;
            bool sixteen = MachineNaming.TryRename(m.LogicId, new string('好', 16), out _) && MachineNaming.Length(m.CustomName) == 16;
            bool emoji = MachineNaming.Length("铁锤🔧") == 3;
            bool ok = MachineNaming.TryRename(m.LogicId, "  铁锤  ", out string okMsg) && m.CustomName == "铁锤"
                      && MachineNaming.Long(m) == "铁锤 #" + m.DisplayNumber && MachineNaming.Short(m) == "铁锤 #" + m.DisplayNumber;
            bool sameName = !MachineNaming.TryRename(m.LogicId, "铁锤", out string e4) && e4.Contains("没有变化");
            bool hook = GameSettings.HasSeenGuidanceHook(GuidanceHooks.RosterFirstRename);
            bool reset = MachineNaming.TryResetName(m.LogicId, out _) && m.CustomName == null && MachineNaming.Long(m) == defLong && MachineNaming.Short(m) == defShort;
            bool unknown = !MachineNaming.TryRename(98765, "x", out _);
            Expect(empty && emptyNull && tooLong && control && markup && sixteen && emoji && ok && sameName && hook && reset && unknown,
                $"E1 改名：空 / 全空白（“{e1}”）、17 个字（“{e2}”）、含换行（“{e3}”）、富文本标记都拒绝且名字不变；正好 16 字、首尾空白去掉、表情算 1 个字；“{okMsg}”；" +
                $"同名“没有变化”；恢复默认名回到“{defLong}”（{empty}/{tooLong}/{control}/{markup}/{sixteen}/{ok}/{reset}）");
        }

        /// <summary>E2 名字同源（FGR-ECO-041；DEBT-FG1HUD01-01）：接入 HUD、命令栏 / 规则称呼、通知（击毁）、字幕、结算名单、工单面板都用同一个名字；源码里不再自拼“#编号”。</summary>
        private static void CheckNameSameSource()
        {
            CampaignState s = NewWorld(7201);
            List<MachineRecord> ms = HomeMachines();
            MachineRecord m = ms[1];
            MachineNaming.TryRename(m.LogicId, "铁锤", out _);
            SignalPresence.MachineOverrideForTests = () => m.LogicId;
            string hud = SignalPresence.LocationText();
            SignalPresence.MachineOverrideForTests = null;
            string label = SignalPresence.MachineLabel(m.LogicId);
            string rule = StandingRuleService.MachineLabel(m.LogicId);
            string arg = StandingRuleService.ResolveArg(s, "@m:" + m.LogicId.ToString(CultureInfo.InvariantCulture));
            string cue = GameLogic.Campaign.Feedback.FeedbackCues.MachineLabel(m.LogicId);
            CampaignCredits.Snapshot credits = CampaignCredits.Build(s);
            string credit = credits.Machines.FirstOrDefault(x => x.DisplayNumber == m.DisplayNumber).Label;
            // 审查回归（P1）：命令栏事件记录（真实入口：选中 → 右键移动 → 停止）与“无法到达”通知（直接移动命令寻路失败的同一回调）都写名字。
            RegionSquadCommandSystem squad = WorldSimulation.Home?.SquadCommands;
            string stopEvent = string.Empty;
            if (squad != null)
            {
                squad.DebugSelectMany(new[] { m.LogicId });
                squad.IssueMoveTo(HomeValleyLayout.Core.Position + new Vector2(4f, 4f), paused: false);
                squad.Stop();
                stopEvent = squad.RecentEvents.LastOrDefault() ?? string.Empty;
            }
            HomeValleyWorkOrders.OnWorkUnreachable(s, m.LogicId, BinGames.Sim.Nav.NavFailReason.Unreachable, HomeValleyLayout.Core.Position + new Vector2(30f, 30f));
            NotificationEntry unreachableNote = NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == "unreachable");
            string unreachableText = unreachableNote?.Latest?.DetailText ?? unreachableNote?.Text ?? string.Empty;
            bool eventsNamed = stopEvent.Contains("铁锤 #" + m.DisplayNumber) && unreachableText.Contains("铁锤 #" + m.DisplayNumber) && !unreachableText.Contains("机器 #");
            Expect(eventsNamed, $"E2b 命令栏事件“{stopEvent}”与“无法到达”通知“{unreachableText}”写机器名字（不再是“机器 #内部编号”）");
            int before = NotificationCenter.History.Sum(n => n.Count);
            MachineRegistry.MarkDeadByLogicId(m.LogicId);
            NotificationEntry note = NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == "machine_destroyed");
            string noteText = note?.Text ?? string.Empty;
            bool notified = noteText.Contains("铁锤") || (note?.Members?.Any(x => x.Detail.Contains("铁锤")) ?? false);
            string detail = RosterPanelUIToolkit.DetailText(s, m);
            // 源码扫描：热更层里不再有自拼“#” + DisplayNumber 的显示（唯一例外是 MachineNaming 本身与编号排序 / 结构体字段）。
            string root = Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic");
            string[] files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/FirstPlayable/") && !f.EndsWith("MachineNaming.cs", StringComparison.Ordinal)).ToArray();
            var offenders = new List<string>();
            // 审查回归（P1）：扩大扫描——“#” + 编号 / 内部 LogicId 的拼接与插值、写死“机器 #{”的事件文字、把内部 LogicId 当称呼传进文本模板，都算自拼称呼。
            var rx = new Regex(
                "\"#\"\\s*\\+\\s*[\\w.]*(DisplayNumber|[Ll]ogicId)\\b"
                + "|\\$\"[^\"]*#\\{[^}\"]*(DisplayNumber|[Ll]ogicId)[^}\"]*\\}"
                + "|机器 ?#\\{"
                + "|GameText\\.Format\\(\"[^\"]+\",\\s*[\\w.]*[Ll]ogicId(\\.ToString\\([^)]*\\))?\\s*[,)]");
            string[] positives =
            {
                "\"#\" + m.DisplayNumber", "\"#\" + logicId", "$\"机器 #{logicId} 已停止\"", "$\"#{rec.LogicId}\"", "$\"机器 #{id} 已停止\"",
                "GameText.Format(\"nav.squad.unreachable\", logicId.ToString(), x)", "GameText.Format(\"k\", m.LogicId)",
            };
            string[] negatives =
            {
                "MachineNaming.Short(logicId)", "$\"#{uid}_player_L{logicId}_V{visualId}\"", "GameText.Format(\"roster.detail.line_id\", m.DisplayNumber, x)",
                "GameText.Format(\"nav.squad.unreachable\", MachineNaming.Short(logicId), x)", "$\"　#{id}\"",
            };
            bool regexOk = positives.All(rx.IsMatch) && !negatives.Any(rx.IsMatch);
            foreach (string f in files)
            {
                if (rx.IsMatch(File.ReadAllText(f)))
                {
                    offenders.Add(Path.GetFileName(f));
                }
            }
            // 文本表源数据：模板里不能再写“机器 #{n}” / “Machine #{n}”（称呼由 MachineNaming 作为参数传入）。
            string tables = Path.Combine(FgProductionSelfCheck.LocateRepo(), "tools/cell_tables");
            var textRx = new Regex("(机器 ?#\\{\\d\\}|Machine ?#\\{\\d\\})");
            string[] dataFiles = Directory.Exists(tables) ? Directory.GetFiles(tables, "fgdata*.py") : Array.Empty<string>();
            foreach (string f in dataFiles)
            {
                if (textRx.IsMatch(File.ReadAllText(f)))
                {
                    offenders.Add(Path.GetFileName(f));
                }
            }
            Expect(regexOk && dataFiles.Length >= 10, $"E3 扫描规则自测：{positives.Length} 个正例全部命中、{negatives.Length} 个反例都不命中（{regexOk}）；文本源数据 {dataFiles.Length} 个文件");
            Expect(hud.Contains("铁锤 #" + m.DisplayNumber) && label == "铁锤 #" + m.DisplayNumber && rule == label && arg.Contains("铁锤") && cue == label
                   && credit == label && notified && detail.Contains("编号 #" + m.DisplayNumber),
                $"E2 名字同源：接入 HUD“{hud}”、命令栏 / 规则“{rule}”、规则日志参数“{arg}”、字幕“{cue}”、结算名单“{credit}”、击毁通知“{noteText}”一致（{notified}）");
            Expect(files.Length >= 300 && offenders.Count == 0,
                $"E3 扫描热更层 {files.Length} 个源文件：没有自拼“#编号”的显示，机器称呼一律走 MachineNaming" + (offenders.Count == 0 ? string.Empty : "；违规：" + string.Join("、", offenders)));
        }

        // ── F 名册排序与筛选 ───────────────────────────────────────────────────────

        private static void CheckQuery()
        {
            CampaignState s = NewWorld(7211);
            int c = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -6f));
            int d = Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(6f, -6f), 100f);
            WorldSimulation.StepMany(2);
            List<MachineRecord> ms = HomeMachines();
            int a = ms[0].LogicId;
            int b = ms[1].LogicId;
            MachineRegistry.ApplyDamage(c, Rec(c).MaxHealth * 0.5f);
            MachineRegistry.ApplyDamage(a, Rec(a).MaxHealth * 0.2f);
            MachineRegistry.TryMarkExperience(d, MachineExperienceFlags.FirstJob);
            MachineRegistry.TryMarkExperience(d, MachineExperienceFlags.Controlled);
            MachineRegistry.TryMarkExperience(b, MachineExperienceFlags.FirstJob);
            MachineRoster.TrySetRole(s, b, MachineRole.Idle, out _);
            MachineRoster.TrySetRole(s, c, MachineRole.Garrison, out _);
            MachineNaming.TryRename(d, "Alpha", out _);
            MachineNaming.TryRename(a, "Zulu", out _);
            List<int> Ids(MachineRole? role, string chassis, MachineRoster.StatusFilter st, MachineRoster.SortKey key, bool desc) =>
                MachineRoster.Query(s, role, chassis, st, key, desc).Select(m => m.LogicId).ToList();
            bool byInjury = Ids(null, null, MachineRoster.StatusFilter.All, MachineRoster.SortKey.Injury, true).Take(2).SequenceEqual(new[] { c, a });
            bool byExp = Ids(null, null, MachineRoster.StatusFilter.All, MachineRoster.SortKey.Experience, true)[0] == d;
            List<int> byName = Ids(null, null, MachineRoster.StatusFilter.All, MachineRoster.SortKey.Name, false);
            bool name = byName.First() == d && byName.Last() == a;
            List<int> byRole = Ids(null, null, MachineRoster.StatusFilter.All, MachineRoster.SortKey.Role, false);
            bool role = byRole.Last() == b && byRole.IndexOf(c) > byRole.IndexOf(a);
            List<int> byNumber = Ids(null, null, MachineRoster.StatusFilter.All, MachineRoster.SortKey.Number, true);
            bool number = byNumber.SequenceEqual(byNumber.OrderByDescending(id => Rec(id).DisplayNumber)) && byNumber.Count == 4;
            bool chassis = Ids(null, HomeValleyLayout.Erc003ChassisId, MachineRoster.StatusFilter.All, MachineRoster.SortKey.Number, false).SequenceEqual(new[] { c })
                           && MachineRoster.ChassisIds().Contains(HomeValleyLayout.Erc003ChassisId) && MachineRoster.ChassisLabel(HomeValleyLayout.Erc003ChassisId) != HomeValleyLayout.Erc003ChassisId;
            bool roleFilter = Ids(MachineRole.Idle, null, MachineRoster.StatusFilter.All, MachineRoster.SortKey.Number, false).SequenceEqual(new[] { b });
            MachineRegistry.MarkDeadByLogicId(d);
            bool dead = Ids(null, null, MachineRoster.StatusFilter.Dead, MachineRoster.SortKey.Number, false).SequenceEqual(new[] { d })
                        && !Ids(null, null, MachineRoster.StatusFilter.Active, MachineRoster.SortKey.Number, false).Contains(d);
            Seconds(3f);
            bool working = Ids(null, null, MachineRoster.StatusFilter.Working, MachineRoster.SortKey.Number, false).Contains(c); // 驻防单在办 = 工作中
            bool waiting = Ids(null, null, MachineRoster.StatusFilter.Waiting, MachineRoster.SortKey.Number, false).Contains(b);
            Expect(byInjury && byExp && name && role && number && chassis && roleFilter && dead && working && waiting,
                $"F 名册（FGU-16）：按伤势 / 经历 / 名字 / 岗位排序、升降序；按底盘、岗位、状态（在役 / 工作中 / 待命 / 阵亡）筛选，同值按编号（{byInjury}/{byExp}/{name}/{role}/{chassis}/{roleFilter}/{dead}/{working}/{waiting}）");
        }

        // ── G 已结束工单清理（DEBT-FG3LOG02-09）与按机型取料（DEBT-FG3LOG02-08）──────────────

        private static void CheckHistoryPruneAndCarry()
        {
            CampaignState s = NewWorld(7221);
            BuildingRecord ws = Place(s, "parts_workshop");
            Seconds(1f);
            GridContent.TryGetTuning("work.history_keep", out float keepF);
            int keep = (int)keepF;
            var list = new List<WorkOrderRecord>(s.WorkOrders ?? Array.Empty<WorkOrderRecord>());
            string protectedId = null;
            for (int i = 0; i < keep + 80; i++)
            {
                var o = new WorkOrderRecord
                {
                    WorkOrderId = "t-hist-" + i.ToString(CultureInfo.InvariantCulture), Kind = WorkOrderKind.Haul, State = i % 3 == 0 ? WorkOrderState.Cancelled : WorkOrderState.Completed,
                    TargetId = "x", IssuerId = "test", CreatedTick = i,
                };
                list.Add(o);
                if (i == 1)
                {
                    protectedId = o.WorkOrderId;
                }
            }
            s.WorkOrders = list.ToArray();
            // 资源事务：一大批早已结清的流水 + 一条被在办工单引用的已结清事务（不清）+ 一条还在预留中的（不动）。
            GridContent.TryGetTuning("ledger.history_keep", out float ledgerKeepF);
            int ledgerKeep = (int)ledgerKeepF;
            var tx = new List<ResourceTransactionRecord>(s.ResourceTransactions ?? Array.Empty<ResourceTransactionRecord>());
            for (int i = 0; i < ledgerKeep + 100; i++)
            {
                tx.Add(new ResourceTransactionRecord { TransactionId = "t-tx-" + i.ToString(CultureInfo.InvariantCulture), OwnerId = "x", ResourceType = "scrap", State = ResourceTransactionState.Committed });
            }
            tx.Add(new ResourceTransactionRecord { TransactionId = "t-tx-open", OwnerId = "x", ResourceType = "scrap", State = ResourceTransactionState.Reserved, Reserved = 5f });
            s.ResourceTransactions = tx.ToArray();
            MachineRecord m = HomeMachines()[0];
            m.RoleOrderId = protectedId; // 名册岗位单还引用着它：不清。
            WorkOrderRecord live = Deliver(s, ws, 2);
            live.ResourceTransactionId = "t-tx-0"; // 在办工单引用的事务（最早的那条）：不清。
            int liveIndexBefore = Array.IndexOf(s.WorkOrders, live);
            HomeValleyWorkOrders.MarkAssignmentDirty();
            Seconds(1f);
            int terminal = s.WorkOrders.Count(o => o.State == WorkOrderState.Completed || o.State == WorkOrderState.Cancelled || o.State == WorkOrderState.Failed);
            bool kept = HomeValleyWorkOrders.Find(s, protectedId) != null && HomeValleyWorkOrders.Find(s, "t-hist-0") == null
                        && HomeValleyWorkOrders.Find(s, "t-hist-" + (keep + 79).ToString(CultureInfo.InvariantCulture)) != null && HomeValleyWorkOrders.Find(s, live.WorkOrderId) == live;
            Expect(terminal <= keep && terminal >= keep - 2 && kept && HomeValleyWorkOrders.PrunedTotal > 0,
                $"G1 已结束工单清理（DEBT-FG3LOG02-09）：{keep + 80} 张历史单清到 {terminal} 张（上限 {keep}），清最早的；名册岗位还引用的那张保留；在办的单不动（下标 {liveIndexBefore} → {Array.IndexOf(s.WorkOrders, live)}）");
            int txTerminal = s.ResourceTransactions.Count(r => r.State == ResourceTransactionState.Committed || r.State == ResourceTransactionState.Cancelled || r.State == ResourceTransactionState.Failed);
            bool ledger = txTerminal <= ledgerKeep + 1 && CampaignEconomyLedger.Find(s, "t-tx-0") != null && CampaignEconomyLedger.Find(s, "t-tx-1") == null
                          && CampaignEconomyLedger.Find(s, "t-tx-open") != null && HomeValleyWorkOrders.PrunedLedgerTotal > 0;
            Expect(ledger, $"G1b 已结束资源事务清理（DEBT-FG3LOG02-09）：{ledgerKeep + 100} 条已结清流水清到 {txTerminal} 条（上限 {ledgerKeep}）；在办工单引用的那条与预留中的那条保留");
            bool carry = HomeValleyConstruction.CarryFor(Rec(HomeMachines()[0].LogicId)) == (int)GridContent.Tuning("build.carry_per_trip." + HomeMachines()[0].ChassisId)
                         && HomeValleyConstruction.CarryFor(new MachineRecord { ChassisId = "no_such_chassis" }) == HomeValleyConstruction.CarryPerTrip
                         && HomeValleyConstruction.CarryFor(null) == HomeValleyConstruction.CarryPerTrip;
            Expect(carry, $"G2 每趟取料按机型读表（DEBT-FG3LOG02-08）：{HomeMachines()[0].ChassisId} = {HomeValleyConstruction.CarryFor(HomeMachines()[0])}；表里没有的机型用统一值 {HomeValleyConstruction.CarryPerTrip}");
        }

        // ── H 存读档 ─────────────────────────────────────────────────────────────

        private static string Snapshot(CampaignState s)
        {
            var sb = new StringBuilder();
            foreach (MachineRecord m in MachineRegistry.AllRecords.Where(x => x != null).OrderBy(x => x.LogicId))
            {
                Vector2 p = Pos(m.LogicId);
                sb.Append(m.LogicId).Append(':').Append((int)m.Role).Append(':').Append(Dash(m.CustomName)).Append(':').Append(Dash(m.RolePointId)).Append(':')
                    .Append(Dash(m.RoleOrderId)).Append(':').Append((int)MachineRoster.EffectiveRole(s, m)).Append(':').Append(m.Health.ToString("F2", CultureInfo.InvariantCulture)).Append(':')
                    .Append(p.x.ToString("F2", CultureInfo.InvariantCulture)).Append(',').Append(p.y.ToString("F2", CultureInfo.InvariantCulture)).Append('|');
            }
            foreach (WorkOrderRecord o in (s.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(x => x != null).OrderBy(x => x.WorkOrderId, StringComparer.Ordinal))
            {
                if (o.IssuerId == HomeValleyWorkOrders.RosterIssuer || o.Kind == WorkOrderKind.Deliver)
                {
                    sb.Append(o.WorkOrderId).Append('=').Append(o.State).Append('/').Append(o.AssignedMachineLogicId).Append('/').Append(string.IsNullOrEmpty(o.FailureReason) ? "-" : o.FailureReason).Append(';');
                }
            }
            sb.Append("alloy=").Append(Stock(s, "alloy"));
            return sb.ToString();
        }

        private static string Dash(string v) => string.IsNullOrEmpty(v) ? "-" : v;

        /// <summary>场景：#1 劳动（送补给），#2 驻防（驻防点 = 零件工坊），#3 名册送修（远征预备），#4 闲置且被改名；三张补给单。</summary>
        private static bool LayScenario(CampaignState s)
        {
            BuildingRecord ws = Place(s, "parts_workshop");
            Bay(s);
            if (ws == null)
            {
                return false;
            }
            Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(5f, -5f), 100f);
            Spawn(s, HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id, new Vector2(7f, -5f), 100f);
            WorldSimulation.StepMany(2);
            List<MachineRecord> ms = HomeMachines();
            MachineRoster.TrySetRole(s, ms[1].LogicId, MachineRole.Garrison, out _);
            MachineRoster.TrySetGarrisonPoint(s, ms[1].LogicId, ws.BuildingId, out _);
            MachineRoster.TrySetRole(s, ms[2].LogicId, MachineRole.ExpeditionReserve, out _);
            MachineRegistry.ApplyDamage(ms[2].LogicId, Rec(ms[2].LogicId).MaxHealth * 0.6f);
            MachineRoster.TrySetRole(s, ms[2].LogicId, MachineRole.InRepair, out _);
            MachineRoster.TrySetRole(s, ms[3].LogicId, MachineRole.Idle, out _);
            MachineNaming.TryRename(ms[3].LogicId, "看门", out _);
            Deliver(s, ws, 3);
            Deliver(s, ws, 4, 1);
            Deliver(s, ws, 5);
            return true;
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(7231);
            if (!LayScenario(s))
            {
                Fail("H 放不下场景");
                return;
            }
            WorldSimulation.StepMany(GameClock.StepHz * 12 + 7);
            // 驻防的机器被玩家接管过：读档后仍是“暂停”。
            int g = HomeMachines()[1].LogicId;
            string before = Snapshot(s);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            WorldSimulation.StepMany(GameClock.StepHz * 60);
            string continuous = Snapshot(s);

            string RunFromSave(out string loaded)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
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
                WorldSimulation.StepMany(GameClock.StepHz * 60);
                return Snapshot(rr.State);
            }

            string first = RunFromSave(out string loaded1);
            string second = RunFromSave(out _);
            bool fields = before.Contains("看门") && before.Contains(":" + (int)MachineRole.Garrison + ":") && before.Contains("roster-repair-");
            Expect(save.Success && loaded1 == before && fields,
                "H1 真文件存读档：岗位、名字、驻防点、名册岗位单、名册送修单、补给单逐字段一致" + (loaded1 == before ? string.Empty : $"\n存档前：{before}\n读档后：{loaded1}"));
            Expect(first == continuous && second == first,
                "H2 读档后接着跑 60 游戏秒（送货、驻防、送修修满回原岗位），与不存档一直跑逐位一致；同一存档读两次结果一致" + (first == continuous ? string.Empty : $"\n一直跑：{continuous}\n读档跑：{first}"));
            // 暂停状态的驻防进存档：玩家用真实右键派工入口把驻防的机器派去施工（= 接管），存读档后维持器仍不拉回。
            CampaignState now = CampaignSession.Current;
            // 场景里唯一的劳动岗（#1，送补给）先改为闲置：免得它在右键之前自动领走新虚影的施工单（那样右键会被“已经有机器在做”拒绝）。
            MachineRoster.TrySetRole(now, HomeMachines()[0].LogicId, MachineRole.Idle, out _);
            bool clicked = RightClickGhost(now, g, out string hitName);
            bool tookOver = clicked && MachineRoster.GarrisonSuspended(now, Rec(g)) && Active(now, g)?.Kind == WorkOrderKind.Build;
            WorldSimulation.StepMany(GameClock.StepHz);
            WorldSimulation.SyncAllForSave();
            CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            RunFromSave(out _);
            bool stillSuspended = MachineRoster.GarrisonSuspended(CampaignSession.Current, Rec(g)) && Active(CampaignSession.Current, g)?.Kind != WorkOrderKind.Garrison;
            Expect(tookOver && stillSuspended,
                $"H3 玩家右键派去施工（真实右键入口，射线点中“{hitName}”）而暂停的驻防随存档保留：读档跑 60 秒维持器仍不把它拉回驻防点（{tookOver}/{stillSuspended}）");
        }

        // ── I / J 暂停、倍速、观察 ───────────────────────────────────────────────────

        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld)
        {
            CampaignState s = NewWorld(seed, observe: observe);
            pausedHeld = true;
            if (!LayScenario(s))
            {
                return "放不下";
            }
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                string p0 = Snapshot(s);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                pausedHeld = Snapshot(s) == p0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * 70;
            int frames = 0;
            while (GameClock.Ticks < target && frames < 60 * 500)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            return Snapshot(s);
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            string diff = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(7241, true, speed, true, out bool held);
                paused &= held;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"\n{speed}x：{snap}\n参照：{reference}";
                }
                same &= snap == reference;
            }
            Expect(paused && same && reference != "放不下" && reference.Contains("Completed"),
                "I 暂停中（120 帧）岗位维持、派工、送修都不动；0.5x / 1x / 2x / 3x 跑同样的 70 游戏秒，岗位、机器位置、驻防 / 送修 / 补给单逐字段一致" + $"（暂停不动 {paused}）" + diff);
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(7242, true, 1f, false, out _);
            string unseen = RunScenario(7242, false, 1f, false, out _);
            Expect(seen == unseen && seen != "放不下",
                "J 观察与不观察家园跑 70 游戏秒逐字段一致（FGR-BASE-021：岗位维持在派工评估里、送修与驻防在世界模拟步里）" + (seen == unseen ? string.Empty : $"\n观察：{seen}\n不观察：{unseen}"));
        }

        // ── K 面板 ───────────────────────────────────────────────────────────────

        private sealed class Keys : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool GetKey(KeyCode key) => key == Down;
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        private static void CheckPanel()
        {
            CampaignState s = NewWorld(7251);
            int c = Spawn(s, HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, new Vector2(4f, -6f));
            WorldSimulation.StepMany(2);
            List<MachineRecord> ms = HomeMachines();
            SignalCoreService.EnsureInitialized(s);
            s.SignalCore.SafeModes = (s.SignalCore.SafeModes ?? Array.Empty<SignalSafeModeRecord>())
                .Append(new SignalSafeModeRecord { LogicId = c, Reason = (int)SignalLinkBreakReason.OutOfCoverage, SinceTick = GameClock.Ticks }).ToArray(); // 夹具：进入安全模式的真实路径由 FgSignalLinkSelfCheck 覆盖
            MachineRegistry.ApplyDamage(c, Rec(c).MaxHealth * 0.4f);
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "RosterPanel.uxml", out GameObject go);
            RosterPanelUIToolkit.InWorldOverrideForTests = true;
            RosterPanelUIToolkit.Clock = () => _fakeNow;
            try
            {
                RosterPanelUIToolkit panel = go.AddComponent<RosterPanelUIToolkit>();
                panel.BindView(root);
                panel.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "RosterRow.uxml"));
                // 快捷键 N（全部上下文）打开。
                var keys = new Keys();
                InputRouter.Reset();
                InputRouter.DebugSetReader(keys);
                InputRouter.SetScope(InputScope.Strategy);
                keys.Down = GameSettings.KeyBindings.GetKey(GameActionId.OpenRoster);
                UiKitInputPump.ProcessLibraryKeys();
                keys.Down = KeyCode.None;
                InputRouter.DebugClearConsumedKeys();
                panel.Refresh();
                bool opened = RosterPanelUIToolkit.IsOpen && panel.PanelVisible && panel.VisibleRowCount == ms.Count && GameSettings.HasSeenGuidanceHook(GuidanceHooks.RosterPanelFirstOpen);
                int cRow = panel.RowIndexOf(c);
                bool safe = cRow >= 0 && panel.RowSafeVisible(cRow) && panel.RowText(cRow, "RoSafe").Contains("安全");
                // 排序下拉框：伤势降序 → 受伤的 c 在第一行。
                RosterPanelUIToolkit.PickForTests(panel.SortField, Array.IndexOf(MachineRoster.AllSortKeys, MachineRoster.SortKey.Injury));
                panel.ToggleSortDirection();
                bool sorted = panel.Sort == MachineRoster.SortKey.Injury && panel.Descending && panel.RowLogicId(0) == c;
                // 行内岗位下拉框：选“闲置”即生效。
                int aRow = panel.RowIndexOf(ms[0].LogicId);
                DropdownField rf = panel.RowRoleField(aRow);
                int idleAt = rf.choices.FindIndex(x => x.Contains("闲置"));
                RosterPanelUIToolkit.PickForTests(rf, idleAt);
                bool inline = Rec(ms[0].LogicId).Role == MachineRole.Idle && panel.MessageText.Contains("闲置");
                // 筛选：岗位 = 闲置 → 只剩这一台。
                RosterPanelUIToolkit.PickForTests(panel.RoleFilterField, panel.RoleFilterField.choices.FindIndex(x => x.Contains("闲置")));
                bool filtered = panel.VisibleRowCount == 1 && panel.RowLogicId(0) == ms[0].LogicId;
                RosterPanelUIToolkit.PickForTests(panel.RoleFilterField, 0);
                // 批量：全选可见 → 批量改为“远征预备”。
                panel.SelectAllShown();
                int selected = panel.SelectedCount;
                RosterPanelUIToolkit.PickForTests(panel.BatchRoleField, panel.BatchRoleField.choices.FindIndex(x => x.Contains("远征预备")));
                bool batch = selected == ms.Count && HomeMachines().All(m => m.Role == MachineRole.ExpeditionReserve) && panel.MessageText.Contains(ms.Count + " 台");
                // 详情：改名（空 → 原因；过长 → 原因；合法 → 列表与标题同步）。
                panel.ShowDetail(c);
                bool detail = panel.ShowingDetail && panel.DetailTitleText.Contains(MachineNaming.Long(Rec(c))) && panel.DetailInfoText.Contains("接入记录")
                              && panel.DetailInfoText.Contains("受伤记录") && panel.DetailInfoText.Contains("经历") && panel.DetailInfoText.Contains("击杀");
                panel.NameField.value = " ";
                bool emptyRejected = !panel.Rename(panel.NameField.value) && panel.DetailMessageText.Contains("不能为空") && Rec(c).CustomName == null;
                panel.NameField.value = new string('长', 20);
                bool longRejected = !panel.Rename(panel.NameField.value) && panel.DetailMessageText.Contains("最多 16") && Rec(c).CustomName == null;
                panel.NameField.value = "先锋";
                bool renamed = panel.Rename(panel.NameField.value) && panel.DetailTitleText.Contains("先锋 #") && SignalPresence.MachineLabel(c).StartsWith("先锋", StringComparison.Ordinal);
                // 详情里设为驻防：出现驻防点下拉框。
                RosterPanelUIToolkit.PickForTests(panel.DetailRoleField, panel.DetailRoleField.choices.FindIndex(x => x.Contains("驻防")));
                bool pointRow = Rec(c).Role == MachineRole.Garrison && panel.DetailPointVisible;
                panel.ShowDetail(0);
                bool backToList = !panel.ShowingDetail && panel.RowText(panel.RowIndexOf(c), "RoName").Contains("先锋");
                // 远征中的机器：行内岗位下拉框禁用并写明原因；详情页同样禁用。
                MachineRegistry.MoveToRegion(Rec(ms[1].LogicId), FracturedCityLayout.RegionId); // 夹具：真实出发由 C3 覆盖
                panel.SetFilters(null, null, MachineRoster.StatusFilter.All);
                int awayRow = panel.RowIndexOf(ms[1].LogicId);
                DropdownField awayField = panel.RowRoleField(awayRow);
                bool rowLocked = awayRow >= 0 && !awayField.enabledSelf && awayField.value.Contains("远征中") && panel.RowText(awayRow, "RoInfo").Contains("不能改");
                panel.ShowDetail(ms[1].LogicId);
                bool detailLocked = !panel.DetailRoleEnabled && panel.DetailRoleNoteText.Contains("返回家园后");
                panel.ShowDetail(0);
                string all = panel.CountText + panel.RowText(0, "RoName") + panel.RowText(0, "RoInfo") + panel.RowText(0, "RoStatus") + panel.MessageText;
                bool markers = !GameText.ContainsMarker(all);
                Expect(opened && safe && sorted && inline && filtered && batch && detail && emptyRejected && longRejected && renamed && pointRow && backToList && rowLocked && detailLocked && markers,
                    $"K1 机器名册面板（真 UXML，FGU-16 / 17）：N 键打开、每台一行、安全模式标记（DEBT-FG1SIG04-04）；排序（伤势降序）；行内改岗位（下拉框选中即生效）；按岗位筛选；全选 + 批量改岗位；" +
                    $"详情（经历 / 接入记录 / 击杀 / 受伤记录）；改名空 / 过长给原因、合法后标题与 HUD 同步；驻防显示驻防点；远征中的机器行内与详情页岗位都禁用并说明" +
                    $"（{opened}/{safe}/{sorted}/{inline}/{filtered}/{batch}/{detail}/{emptyRejected}/{longRejected}/{renamed}/{pointRow}/{backToList}/{rowLocked}/{detailLocked}/{markers}）");
                RosterPanelUIToolkit.Close();
                bool closed = !RosterPanelUIToolkit.IsOpen && !panel.PanelVisible;
                Expect(closed, "K2 关闭后面板隐藏、退出模态");
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "RosterPanel.uxml", "RosterPanelWindow");
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL"), "K3 名册面板布局探针（四种分辨率 + 超长文字 + USS 体检）：" + probe.Split('\n')[0]);
                string pauseProbe = UiToolkitLayoutProbe.Probe(UiKitFolder + "PauseMenu.uxml", "PauseMenuWindow");
                Expect(!pauseProbe.Contains("FAIL"), "K4 暂停菜单加了“机器名册”按钮后布局探针仍通过：" + pauseProbe.Split('\n')[0]);
            }
            finally
            {
                RosterPanelUIToolkit.Close();
                RosterPanelUIToolkit.InWorldOverrideForTests = false;
                RosterPanelUIToolkit.Clock = () => Time.realtimeSinceStartupAsDouble;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                Object.DestroyImmediate(go);
            }
            // 顶栏劳动力（FGR-ECO-042）：真 UXML 的世界条，数值与名册一致，点按钮打开名册。
            CampaignState s2 = NewWorld(7252);
            BuildingRecord ws = Place(s2, "parts_workshop");
            Seconds(1f);
            Deliver(s2, ws, 2);
            StepUntil(() => MachineRoster.ComputeLabor(s2).Busy == 1, 5);
            VisualElement wroot = FgProductionSelfCheck.MountUxml(UiKitFolder + "WorldBar.uxml", out GameObject wgo);
            try
            {
                WorldBarHudUIToolkit bar = wgo.AddComponent<WorldBarHudUIToolkit>();
                bar.BindView(wroot);
                _fakeNow += 10;
                bar.Refresh();
                MachineRoster.LaborCount lc = MachineRoster.ComputeLabor(s2);
                string text = bar.LaborText;
                bool labor = lc.Labor == HomeMachines().Count && text.Contains("劳动力 " + lc.Labor) && text.Contains("忙碌 " + lc.Busy) && text.Contains(lc.BusyPercent + "%");
                MachineRoster.TrySetRole(s2, HomeMachines()[1].LogicId, MachineRole.Idle, out _);
                bar.Refresh();
                bool updated = bar.LaborText.Contains("劳动力 " + (lc.Labor - 1));
                string wprobe = UiToolkitLayoutProbe.Probe(UiKitFolder + "WorldBar.uxml", "WorldBar");
                Expect(labor && updated && !wprobe.Contains("FAIL"),
                    $"K5 顶栏劳动力（FGR-ECO-042）：“{text}”与名册一致；改岗位后立刻更新为“{bar.LaborText}”；世界条布局探针：{wprobe.Split('\n')[0]}");
            }
            finally
            {
                Object.DestroyImmediate(wgo);
            }
        }

        // ── L 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(7261);
            BuildingRecord ws = Place(s, "parts_workshop");
            for (int i = 0; i < 58; i++)
            {
                Spawn(s, i % 2 == 0 ? HomeValleyLayout.Erc001ChassisId : HomeValleyLayout.Erc003ChassisId,
                    i % 2 == 0 ? HomeValleyLayout.BlueprintErc001Id : HomeValleyLayout.BlueprintErc003Id, new Vector2(-12f + (i % 12) * 2f, -14f - (i / 12) * 2f), 100f);
            }
            WorldSimulation.StepMany(2);
            List<MachineRecord> ms = HomeMachines();
            for (int i = 0; i < ms.Count; i++)
            {
                MachineRoster.TrySetRole(s, ms[i].LogicId, i % 4 == 1 ? MachineRole.Garrison : i % 4 == 2 ? MachineRole.Idle : MachineRole.Labor, out _);
                MachineNaming.TryRename(ms[i].LogicId, "机器" + i.ToString(CultureInfo.InvariantCulture), out _);
            }
            for (int i = 0; i < 20; i++)
            {
                Deliver(s, ws, 1);
            }
            Seconds(2f);
            const int Rounds = 200;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Rounds; i++)
            {
                MachineRoster.ComputeLabor(s);
            }
            double labor = sw.Elapsed.TotalMilliseconds / Rounds;
            sw.Restart();
            for (int i = 0; i < Rounds; i++)
            {
                MachineRoster.KeepRoles(s, null);
            }
            double keep = sw.Elapsed.TotalMilliseconds / Rounds;
            sw.Restart();
            for (int i = 0; i < 50; i++)
            {
                MachineRoster.Query(s, null, null, MachineRoster.StatusFilter.All, MachineRoster.SortKey.Experience, true);
            }
            double query = sw.Elapsed.TotalMilliseconds / 50;
            // 顶栏读数：缓存命中时 O(1)。
            MachineRoster.Labor(s);
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < 1000; i++)
            {
                MachineRoster.Labor(s);
            }
            double cachedUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency / 1000.0;
            PerfLines.Add($"{ms.Count} 台机器、{s.WorkOrders.Length} 张工单：劳动力现算 {labor:F3} ms；岗位维持 {keep:F3} ms（每 0.5 秒一次）；名册查询排序 {query:F3} ms（只在面板刷新时）；顶栏缓存命中 {cachedUs:F2} µs");
            PerfGate.Expect(ms.Count >= 60,
                $"L 性能：{ms.Count} 台机器劳动力现算 {labor:F3} ms（阈值 0.5，最多每秒一次）、岗位维持 {keep:F3} ms（阈值 0.5，每 0.5 秒一次）、名册查询 {query:F3} ms（阈值 3）、顶栏缓存命中 {cachedUs:F2} µs（阈值 5，O(1)）。Editor batchmode；真机 HybridCLR 另测（FG15-SYS-02）",
                new[] { PerfGate.Le(labor, 0.5, "劳动力 ms"), PerfGate.Le(keep, 0.5, "岗位维持 ms"), PerfGate.Le(query, 3.0, "名册查询 ms"), PerfGate.Le(cachedUs, 5.0, "顶栏缓存 µs") }, Expect, Line);
        }

        // ── 断言工具 ─────────────────────────────────────────────────────────────

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
                InputRouter.SetGameplayPaused(false);
                StrategyClock.Reset();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                MachineRoster.HasOutpostProvider = null;
                SignalPresence.MachineOverrideForTests = null;
                PowerEnvironment.ResetForTests();
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
