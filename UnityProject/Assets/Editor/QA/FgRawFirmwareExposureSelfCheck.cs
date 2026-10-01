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
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
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
    /// FG1-SIG-06 裸跑敌方固件与暴露改写的自动验收（FG01 FGR-SIG-060～062、070、071；FG13 FGU-44；FGT-SIG-003、008；
    /// 卡片负向“裸跑固件放进机器或炮塔”“破解完成时固件正在信号核里”；Demo 暴露来源迁移与降暴露手段）。
    /// 全部用正式种类表与正式调参（不注入），起真实系统：整个世界（家园 / 破碎都市 / 铸造前哨控制器 + 战斗内核 + 统一时钟）、
    /// 真实接入入口、真实撤离结算（ResolveExtraction）、真实解析台队列（TryEnqueue + Tick）、真实开火（直控开火入口）、真实存档文件、真 UXML 界面：
    /// A 数据；B 宿主规则（FGT-SIG-003）；C 获得；D 裸跑代价（FGT-SIG-008：常规固件，积热 ×1.5、每次发动 +2、计次间隔）与破解时正插在信号核里；
    /// E 裸跑的核心固件（标记跳转，+2 而不是 +0.5；破解后冷却不重置、之后按 +0.5）；F 暴露改写（接入不计、各来源数值、异派技术、阈值、明细截断）；
    /// G 旧档迁移与存读档；H 暂停与 0.5x～3x；I 家园观察无关；J 性能；K 界面（未破解红框、暴露面板、快捷键、布局探针、文本键）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgRawFirmwareExposureSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UxmlPath = "Assets/GameRes/Raw/UI/UiKit/SignalCorePanel.uxml";
        private const int Slot = 0;
        private const string BpCannonUp = "bp_selfcheck_sig06_cannon_up";
        private const string BpGun = "bp_selfcheck_sig06_gun";
        private const string BpMarkUp = "bp_selfcheck_sig06_mark_up";
        private const string SalvagePierce = "sig06-salvage-pierce";
        private const string SalvageDatabox = "sig06-salvage-databox";
        private const float FrameDt = 0.05f;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;
        private static readonly Reader Keys = new Reader();
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 裸跑敌方固件与暴露")]
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
            Line("\n[裸跑与暴露] 裸跑敌方固件与暴露改写（FG1-SIG-06）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsig06-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.ResetForTests(); // 正式种类表，不注入
                UplinkReactionReadiness.ResetForTests();
                RawFirmwareService.ResetForTests();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => FrameDt;
                NotificationCenter.AutoPauseHandler = null;
                SignalUplinkService.RealTimeForTests = () => _fakeNow;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "热更层在 Editor 下是 Mono JIT，真机走 HybridCLR 解释执行（数字只作量级参考，真机复测归 FG15-SYS-02）");

                Step(CheckData);
                Step(CheckHostRules);
                Step(CheckAcquire);
                Step(CheckRawCost);
                Step(CheckRawCore);
                Step(CheckExposureRewrite);
                Step(CheckAlienTechNewFactions);
                Step(CheckMigrationAndSave);
                Step(CheckPauseSpeed);
                Step(CheckObservationIndependence);
                Step(CheckPerformance);
                Step(CheckUi);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"裸跑与暴露自检抛异常：{e}");
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
                SignalUplinkService.ResetForTests();
                SignalCoreService.ResetForTests();
                SignalPresence.ResetForTests();
                FirmwareKinds.ResetForTests();
                UplinkReactionReadiness.ResetForTests();
                RawFirmwareService.ResetForTests();
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
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
                MachineLoadoutRegistry.Clear();
                GameSettings.SetLanguage(originalLanguage);
                UiEscapeStack.Clear();
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
            Line($"  · [裸跑与暴露] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A. 数据 ─────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：fg.TbFirmwareKind 协议 / 来源阵营；裸跑与暴露来源的调参（FG16 初值）；快捷键登记");
            // FG2-FW-01：44 条固件按设计案 10 章的阵营技术类别分配协议与来源（己方 = 基础蓝图库、中立 = 人类遗产、其余敌方加密）；
            // Demo 的两条敌方固件（标记跳转 = 静默、装甲击穿 = 铸造）不变。
            var enemy = FirmwareCatalog.All.Keys.Where(FirmwareKinds.IsEnemyProtocol).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var enemyRows = FirmwareKinds.Rows.Where(r => r.Protocol == "enemy").Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var own = FirmwareKinds.Rows.Where(r => r.Protocol == "own").Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Expect(enemy.SequenceEqual(enemyRows) && enemy.Contains(FirmwareCatalog.FwArmorPierceId) && enemy.Contains(FirmwareCatalog.FwMarkTagId)
                   && FirmwareKinds.FactionOf(FirmwareCatalog.FwMarkTagId) == "silent" && FirmwareKinds.FactionOf(FirmwareCatalog.FwArmorPierceId) == "foundry"
                   && FirmwareKinds.FactionOf(FirmwareCatalog.FwOverloadId) == "reclaim"
                   && own.SequenceEqual(new[] { FirmwareCatalog.FwHomingId, FirmwareCatalog.FwOverloadId, FirmwareCatalog.FwSplitId, FirmwareCatalog.FwTrailId })
                   && FirmwareKinds.Rows.All(r => new[] { "own", "neutral", "enemy" }.Contains(r.Protocol)
                                                  && new[] { "reclaim", "relic", "silent", "foundry", "clarity", "overclock" }.Contains(r.Faction)
                                                  && GameText.Has("faction." + r.Faction)),
                $"敌方加密协议的固件 {enemy.Count} 条（含静默协议数据盒 → 标记跳转、铸造技术缓存 → 装甲击穿），己方 = 寻的 / 过载 / 分裂 / 拖尾，其余中立；阵营键都已登记且有名称");
            Expect(Mathf.Approximately(RawFirmwareService.ExposurePerFire, 2f) && Mathf.Approximately(RawFirmwareService.HeatMultiplier, 1.5f)
                   && Mathf.Approximately(RawFirmwareService.ChargeIntervalSeconds, 8f)
                   && Mathf.Approximately(CampaignExposureLedger.CoreFireDelta, 0.5f) && Mathf.Approximately(CampaignExposureLedger.AlienTechDelta, 2f)
                   && Mathf.Approximately(CampaignExposureLedger.NodeDestroyedDelta, 3f) && Mathf.Approximately(CampaignExposureLedger.NodeLinkCutDelta, -15f)
                   && Mathf.Approximately(CampaignExposureLedger.TowerBroadcastOffDeltaPer10Seconds, -2f) && Mathf.Approximately(CampaignExposureLedger.HighPowerCapPerHour, 3f)
                   && Mathf.Approximately(CampaignExposureLedger.MaxExposure, 100f),
                "调参（fg.TbHomeTuning）：裸跑 +2 / 积热 ×1.5 / 计次间隔 8 秒；核心发动 +0.5；异派 +2/远征；摧毁节点 +3；监听链 -15；塔关广播 -2/10 秒；高功率 ≤ +3/游戏小时；上限 100");
            Expect(InputActionCatalog.TryGet(GameActionId.OpenExposure, out InputActionDef def) && def.Status == InputActionStatus.Wired
                   && (def.Contexts & InputContext.Strategy) != 0 && (def.Contexts & InputContext.Uplink) != 0
                   && InputActionCatalog.DefaultChord(GameActionId.OpenExposure).Key == KeyCode.P
                   && (InputActionCatalog.DefaultChord(GameActionId.OpenExposure).Mods & InputModifier.Alt) != 0,
                "动作登记表：OpenExposure 默认 Alt+P、战略 + 接入上下文、已接入（可重绑）");
        }

        // ── B. 宿主规则（FGT-SIG-003）──────────────────────────────────────────

        private static void CheckHostRules()
        {
            Line("  · B. FGT-SIG-003 / 负向“裸跑固件放进机器或炮塔”：未破解的装甲击穿只能进信号核；机器电路三个入口与保存、炮塔规则、刻印都拒绝；破解后都放行");
            CampaignState s = NewState(8601);
            string pierce = FirmwareCatalog.FwArmorPierceId;
            string name = FirmwareKinds.DisplayName(pierce);
            bool core = FirmwareKinds.CanInstall(s, pierce, FirmwareHost.SignalCore, out _);
            bool machine = FirmwareKinds.CanInstall(s, pierce, FirmwareHost.MachineCircuit, out string machineKey);
            bool turret = FirmwareKinds.CanInstall(s, pierce, FirmwareHost.Turret, out string turretKey);
            bool coreTurret = FirmwareKinds.CanInstall(s, FirmwareCatalog.FwOverloadId, FirmwareHost.Turret, out string coreTurretKey);
            bool homingTurret = FirmwareKinds.CanInstall(s, FirmwareCatalog.FwHomingId, FirmwareHost.Turret, out _);
            Expect(FirmwareKinds.IsRaw(s, pierce) && core && !machine && machineKey == "signal.reason.raw_signal_only" && !turret && turretKey == "signal.reason.raw_turret"
                   && !coreTurret && coreTurretKey == "signal.reason.core_turret" && homingTurret,
                "宿主判定（唯一入口 FirmwareKinds.CanInstall）：未破解 → 信号核可以、机器电路 / 炮塔拒绝；核心固件进炮塔也拒绝；己方常规固件进炮塔放行");

            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            CircuitOpResult slotSet = board.TrySetFirmware(s, 0, pierce);
            CircuitOpResult chip = board.TryPlaceChip(3, pierce);
            board.FirmwareSlots[0] = pierce; // 旧草稿：绕过入口直接写进固件槽
            CircuitValidationResult validation = board.Validate();
            BlueprintSaveResult save = BlueprintEditorService.TrySave(s, board, "bp_selfcheck_sig06_raw_save", "raw", saveAsNewRecord: true);
            string rawText = GameText.Format("signal.reason.raw_signal_only", name);
            Expect(!slotSet.Success && slotSet.Code == BlueprintCircuitBoard.RawSignalOnlyCode && slotSet.Message == rawText
                   && !chip.Success && chip.Code == BlueprintCircuitBoard.RawSignalOnlyCode
                   && validation.Issues.Any(i => i.Code == CircuitIssueCode.FirmwareRawSignalOnly)
                   && !save.Success && (save.FailureReason ?? string.Empty).Contains(name),
                $"机器电路：固件槽拒绝“{slotSet.Message}”；3×3 电路格拒绝（{chip.Code}）；保存校验列出“{validation.Issues.FirstOrDefault(i => i.Code == CircuitIssueCode.FirmwareRawSignalOnly)?.Message}”；" +
                $"真实保存入口拒绝“{save.FailureReason}”");

            SignalCoreResult print = SignalCoreService.TryPrintFirmwareChip(s, pierce);
            string part = GrantRaw(s, FoundryOutpostLayout.RegionId, FoundryOutpostLayout.ArmorPierceCacheContentId, SalvagePierce);
            SignalCoreResult equip = Equip(s, part, 0);
            Expect(!print.Success && part != null && equip.Success && SignalCoreService.SlotContentId(s, 0) == pierce,
                $"不能量产：刻印未破解固件被拒绝（“{print.Message}”）；带回的那一枚能放进信号核 1 号槽（{equip.Message}）");

            // FGR-SIG-060：电路板面板基元仓 / 待领取下拉、基元合成面板待领取下拉共用的“▲未破解”后缀。
            string tagBefore = FirmwareKinds.RawTagSuffix(s, pierce);
            Expect(tagBefore == " " + GameText.Get("signal.core.raw_tag") && FirmwareKinds.RawTagSuffix(s, FirmwareCatalog.FwHomingId).Length == 0,
                $"下拉列表里的未破解标记：装甲击穿“{name}{tagBefore}”，己方寻的不带标记");
            Unlock(s, pierce);
            Expect(FirmwareKinds.RawTagSuffix(s, pierce).Length == 0, "破解后下拉列表里的“▲未破解”标记随之消失");
            bool machineAfter = FirmwareKinds.CanInstall(s, pierce, FirmwareHost.MachineCircuit, out string machineAfterKey);
            bool turretAfter = FirmwareKinds.CanInstall(s, pierce, FirmwareHost.Turret, out string turretAfterKey);
            board.FirmwareSlots[0] = null;
            CircuitOpResult slotAfter = board.TrySetFirmware(s, 0, pierce);
            SignalCoreResult printAfter = SignalCoreService.TryPrintFirmwareChip(s, pierce);
            board.FirmwareSlots[0] = pierce; // 旧草稿：破解后仍直接写进固件槽
            CircuitValidationResult validationAfter = board.Validate();
            BlueprintSaveResult saveAfter = BlueprintEditorService.TrySave(s, board, "bp_selfcheck_sig06_cracked_save", "cracked", saveAsNewRecord: true);
            board.FirmwareSlots[0] = null;
            bool impl = FirmwareKinds.HasMachineImplementation(pierce);
            // 行为断言（不是只查“原因码不是 raw”）：装甲击穿目前没有机器电路可编译实现（DEBT-FG1SIG06-07 → FG2-FW-01），破解后仍只能进信号核，
            // 唯一判定 CanInstall 与固件槽入口、保存校验、真实保存结论一致。FG2-FW-01 给它补上实现后，这条自动改成“固件槽成功 + 真实保存成功”。
            bool behaviourOk = impl
                ? machineAfter && slotAfter.Success && saveAfter.Success
                : !machineAfter && machineAfterKey == "signal.reason.no_machine_impl" && !turretAfter && turretAfterKey == "signal.reason.no_machine_impl"
                  && !slotAfter.Success && slotAfter.Code == BlueprintCircuitBoard.NoMachineImplCode && slotAfter.Message == GameText.Format("signal.reason.no_machine_impl", name)
                  && validationAfter.Issues.Any(i => i.Code == CircuitIssueCode.FirmwareNoMachineImpl) && !saveAfter.Success;
            Expect(!FirmwareKinds.IsRaw(s, pierce) && printAfter.Success && behaviourOk
                   && !validationAfter.Issues.Any(i => i.Code == CircuitIssueCode.FirmwareRawSignalOnly)
                   && FirmwareKinds.AfterCrackKey(pierce) == (impl ? "signal.raw.after_crack.machine" : "signal.raw.after_crack.signal_only"),
                $"破解后（FGR-SIG-062）：去掉未破解标记、可以刻印量产（{printAfter.Message}）；机器实现 {(impl ? "有" : "没有")} → 唯一判定“{machineAfterKey ?? "可装"}”、" +
                $"固件槽“{slotAfter.Code}：{slotAfter.Message}”、保存校验与真实保存“{saveAfter.FailureReason}”结论一致（不给“可以装进机器”的假承诺）");

            // 唯一判定与真实入口逐一对照：全部固件（己方 / 核心 / 破解后的敌方）CanInstall(机器电路) 与固件槽入口结论一致，原因码按失败原因映射。
            var mismatch = new List<string>();
            foreach (string id in FirmwareCatalog.All.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                Unlock(s, id);
                bool can = FirmwareKinds.CanInstall(s, id, FirmwareHost.MachineCircuit, out string key);
                CircuitOpResult r = board.TrySetFirmware(s, 1, id);
                if (can != r.Success || (!can && r.Code != FirmwareKinds.InstallFailureCode(key)))
                {
                    mismatch.Add($"{id}:{can}/{r.Code}");
                }
                board.TryClearFirmware(1);
            }
            CircuitOpResult notFw = board.TrySetFirmware(s, 1, PrimitiveInventory.DefaultChipContentId);
            CircuitOpResult homing = board.TrySetFirmware(s, 1, FirmwareCatalog.FwHomingId);
            board.TryClearFirmware(1);
            CircuitOpResult markTag = board.TrySetFirmware(s, 1, FirmwareCatalog.FwMarkTagId);
            Expect(mismatch.Count == 0 && FirmwareCatalog.All.Count >= 6 && !notFw.Success && notFw.Code == "firmware_unknown" && homing.Success
                   && !markTag.Success && markTag.Code == BlueprintCircuitBoard.CoreSignalOnlyCode && FirmwareKinds.AfterCrackKey(FirmwareCatalog.FwMarkTagId) == "signal.raw.after_crack.core",
                $"唯一判定 = 真实入口（{FirmwareCatalog.All.Count} 枚固件逐一对照，不一致 {mismatch.Count}：{string.Join("，", mismatch)}）；非固件 → {notFw.Code}（不再误报未破解）；" +
                $"己方寻的装得进；破解后的标记跳转仍是 {markTag.Code}（核心固件破解后也只属于信号）");
            board.TryClearFirmware(1);
        }

        // ── C. 获得 ──────────────────────────────────────────────────────────────

        private static void CheckAcquire()
        {
            Line("  · C. 获得：带回的敌方加密物品撤离成功 → 基元仓多一枚未破解固件；重复结算不重复发；随机体阵亡丢失的不发；仓满进待领取");
            CampaignState s = NewState(8611);
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            s.RegionQuestItems = new[]
            {
                new RegionQuestItemRecord { SalvageInstanceId = "c-ok", RegionId = FoundryOutpostLayout.RegionId, ContentId = FoundryOutpostLayout.ArmorPierceCacheContentId,
                    State = RegionQuestItemState.Carried, CarrierLogicId = 41 },
                new RegionQuestItemRecord { SalvageInstanceId = "c-lost", RegionId = FoundryOutpostLayout.RegionId, ContentId = FoundryOutpostLayout.ArmorPierceCacheContentId,
                    State = RegionQuestItemState.Carried, CarrierLogicId = 42 },
                new RegionQuestItemRecord { SalvageInstanceId = "c-armor", RegionId = FoundryOutpostLayout.RegionId, ContentId = FoundryOutpostLayout.ArmorCacheContentId,
                    State = RegionQuestItemState.Carried, CarrierLogicId = 41 },
            };
            int pickup0 = FeedbackCues.CountOf(FeedbackCueId.Pickup);
            FoundryOutpostRegion.ResolveExtraction(s, new[] { 41 });
            PrimitiveChipRecord[] granted = s.PrimitiveChips.Where(c => c.SourceSalvageId != null && c.SourceSalvageId.StartsWith("c-", StringComparison.Ordinal)).ToArray();
            int again = RawFirmwareService.GrantRecovered(s);
            FoundryOutpostRegion.ResolveExtraction(s, new[] { 41 });
            Expect(granted.Length == 1 && granted[0].SourceSalvageId == "c-ok" && granted[0].CardDefId == FirmwareCatalog.FwArmorPierceId
                   && granted[0].State == PrimitiveChipState.Bag && FirmwareKinds.IsRaw(s, granted[0].CardDefId)
                   && FeedbackCues.CountOf(FeedbackCueId.Pickup) == pickup0 + 1 && again == 0
                   && s.PrimitiveChips.Count(c => c.SourceSalvageId == "c-ok") == 1,
                "撤离结算（ResolveExtraction，真实入口）：带回的装甲击穿技术缓存 → 基元仓 1 枚未破解“装甲击穿”（有提示音与字幕）；阵亡丢失的、非固件的重甲缓存不发；再结算 / 补发都不重复");

            // 仓满：进待领取，不丢
            CampaignState f = NewState(8612);
            while (PrimitiveInventory.BagCount(f) < PrimitiveInventory.Capacity)
            {
                PrimitiveInventory.GrantCrafted(f, PrimitiveInventory.DefaultChipContentId);
            }
            string pendingPart = GrantRaw(f, FracturedCityLayout.RegionId, FracturedCityLayout.ProtocolDataboxContentId, "c-full");
            PrimitiveChipRecord pending = PrimitiveInventory.Find(f, pendingPart);
            Expect(pending != null && pending.State == PrimitiveChipState.Pending && pending.CardDefId == FirmwareCatalog.FwMarkTagId && FirmwareKinds.IsRaw(f, pending.CardDefId),
                "基元仓满时带回的未破解标记跳转进“待领取”（不静默丢件、不挤掉别的芯片）");
        }

        // ── D. 裸跑代价（FGT-SIG-008，常规固件）与“破解完成时正插在信号核里” ────────────────

        private static void CheckRawCost()
        {
            Line("  · D. FGT-SIG-008：信号带未破解的装甲击穿接入重炮机——积热 ×1.5（40 → 60）、开火即发动 +2、8 游戏秒内不重复计；AI 驾驶同蓝图无代价；破解时正插在信号核里 → 自动更新");
            CampaignState s = NewState(8621);
            string part = GrantRaw(s, FoundryOutpostLayout.RegionId, FoundryOutpostLayout.ArmorPierceCacheContentId, SalvagePierce);
            Equip(s, part, 0);
            int a = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-30f, -20f), 5000f);
            int ai = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-34f, -20f), 5000f);
            FoundryOutpostController fo = OpenFoundry(s, a, ai);
            WorldView.Observe(fo.SiteId);
            CombatSite site = fo.Combat;
            string left = FoundryOutpostLayout.ArmorBotLeftSpawnId;
            RegionEnemyRecord armor = PrepArmorTarget(s, site, left);
            CommitVia(a);
            MachineCombatResolution pilot = MachineLoadoutRegistry.ResolveForPilot(s, a, s.RandomSeed);
            site.TryGetMachineWeapon(a, out MachineWeaponInfo info);
            Expect(pilot.Preview.RawFirmwareIds.SequenceEqual(new[] { FirmwareCatalog.FwArmorPierceId }) && Mathf.Approximately(pilot.Preview.RawHeatMultiplier, 1.5f)
                   && Mathf.Approximately(pilot.Preview.HeatBudget, 60f) && Mathf.Approximately(WeaponOf(site, a).HeatPerShot, 60f) && info.RawGated
                   && (Unit(site, a).Flags & CombatUnitFlags.RawGated) != 0 && Mathf.Approximately(WeaponOf(site, ai).HeatPerShot, 40f)
                   && MachineLoadoutRegistry.ResolveForAi(s, ai, s.RandomSeed).Preview.RawFirmwareIds.Length == 0,
                "接入后：装甲击穿插在接入口（未破解，裸跑），编译热量预算 60、内核每发积热 60、带裸跑门控；同蓝图的 AI 驾驶机积热 40、没有裸跑（FGT-SIG-011 式对照）");

            Place(site, a, armor.Position + FoundryOutpostRegion.ArmorFacingOf(armor) * 6f);
            float e0 = s.SignalExposure;
            int fired0 = RawFirmwareService.RawFiredCount;
            SetHeat(site, a, 0f);
            bool shot1 = FireCannon(site, a, left);
            float heat1 = Heat(site, a);
            WorldSimulation.StepMany(1);
            float e1 = s.SignalExposure;
            SignalExposureEventRecord last = CampaignExposureLedger.RecentEvents(s, 1).FirstOrDefault();
            double remaining = RawFirmwareService.ChargeRemaining(s);
            Expect(shot1 && Mathf.Abs(heat1 - 60f) < 0.6f && Mathf.Abs(e1 - e0 - 2f) < 1e-3f && RawFirmwareService.RawFiredCount == fired0 + 1
                   && last != null && last.Kind == ExposureSourceKind.RawFire && last.Detail == FirmwareCatalog.FwArmorPierceId && last.Faction == "foundry"
                   && remaining > 6.0 && remaining <= 8.0 && (Unit(site, a).Flags & CombatUnitFlags.RawGated) == 0,
                $"第一发：积热 {heat1:F1}（40 × 1.5）、暴露 {e0:0.#} → {e1:0.#}（+2，来源“{CampaignExposureLedger.SourceText(last)}”，阵营铸造）；计次间隔剩 {remaining:F2} 游戏秒，门控已撤下");

            SetHeat(site, a, 0f);
            bool shot2 = FireCannon(site, a, left);
            float heat2 = Heat(site, a);
            WorldSimulation.StepMany(1);
            Expect(shot2 && Mathf.Abs(heat2 - 60f) < 0.6f && Mathf.Approximately(s.SignalExposure, e1) && RawFirmwareService.RawFiredCount == fired0 + 1,
                $"间隔内第二发（重炮 3 秒冷却后）：积热照样 {heat2:F1}，暴露不重复计（{s.SignalExposure:0.#}）——固件照常生效，只有暴露按次数计");

            int rearm0 = RawFirmwareService.RearmCount;
            int guard = 0;
            while (RawFirmwareService.ChargeRemaining(s) > 0 && guard++ < 1000)
            {
                WorldSimulation.StepMany(1);
            }
            WorldSimulation.StepMany(1);
            SetHeat(site, a, 0f);
            bool shot3 = FireCannon(site, a, left);
            WorldSimulation.StepMany(1);
            Expect(RawFirmwareService.RearmCount == rearm0 + 1 && shot3 && Mathf.Abs(s.SignalExposure - e1 - 2f) < 1e-3f && RawFirmwareService.RawFiredCount == fired0 + 2,
                $"间隔到了（游戏时间）重新带上门控，下一发再 +2（暴露 {s.SignalExposure:0.#}）——每次发动确定性增加，不靠随机");

            // 负向：破解完成时，那件固件正装在信号核里（接入中）。
            string partBefore = SignalCoreService.SlotPartId(s, 0);
            int cracked0 = RawFirmwareService.CrackedCount;
            bool done = CrackViaBench(s, SalvagePierce);
            WorldSimulation.StepMany(1);
            site.TryGetMachineWeapon(a, out MachineWeaponInfo infoAfter);
            Expect(done && !FirmwareKinds.IsRaw(s, FirmwareCatalog.FwArmorPierceId) && RawFirmwareService.CrackedCount == cracked0 + 1
                   && SignalCoreService.SlotPartId(s, 0) == partBefore && SignalUplinkService.IsUplinked(s, a)
                   && Mathf.Approximately(WeaponOf(site, a).HeatPerShot, 40f) && !infoAfter.RawGated && (Unit(site, a).Flags & CombatUnitFlags.RawGated) == 0
                   && SignalUplinkService.LastFeedbackText == GameText.Format("signal.raw.cracked", FirmwareKinds.DisplayName(FirmwareCatalog.FwArmorPierceId),
                       GameText.Get(FirmwareKinds.AfterCrackKey(FirmwareCatalog.FwArmorPierceId)))
                   // FG2-FW-02（DEBT-FG1SIG06-07 关闭）：装甲击穿有了原生读法，破解后“也能装进机器”（不再是“仍只能放进信号核”）。
                   && FirmwareKinds.HasMachineImplementation(FirmwareCatalog.FwArmorPierceId)
                   && SignalUplinkService.LastFeedbackText.Contains(GameText.Get("signal.raw.after_crack.machine")),
                $"破解完成时固件正插在信号核里（解析台真实队列）：同一件（{ShortId(partBefore)}）留在 1 号槽、信号仍在机器里，接入的机器立刻按已破解重编译（每发积热 40、没有裸跑门控）；HUD“{SignalUplinkService.LastFeedbackText}”");
            float e3 = s.SignalExposure;
            SetHeat(site, a, 0f);
            bool shot4 = FireCannon(site, a, left);
            float heat4 = Heat(site, a);
            WorldSimulation.StepMany(3);
            Expect(shot4 && Mathf.Abs(heat4 - 40f) < 0.6f && Mathf.Approximately(s.SignalExposure, e3),
                $"破解后开火：积热 {heat4:F1}、暴露不变（{s.SignalExposure:0.#}）——裸跑代价随标记一起消失");
        }

        // ── E. 裸跑的核心固件（标记跳转）───────────────────────────────────────────

        private static void CheckRawCore()
        {
            Line("  · E. 裸跑的核心固件：未破解的标记跳转打出一次 → 暴露 +2（按裸跑计，不另计 +0.5）并进入 8 秒冷却；破解时冷却不重置；之后每次发动按核心 +0.5");
            CampaignState s = NewState(8631);
            string part = GrantRaw(s, FracturedCityLayout.RegionId, FracturedCityLayout.ProtocolDataboxContentId, SalvageDatabox);
            Equip(s, part, 0);
            int m = SpawnRegion(FracturedCityLayout.RegionId, BpMarkUp, new Vector2(-2f, -24f), 5000f);
            FracturedCityController city = OpenCity(s, m);
            WorldView.Observe(city.SiteId);
            CombatSite site = city.Combat;
            CommitVia(m);
            Expect(WeaponOf(site, m).Reaction == CombatReaction.MarkJump && MachineLoadoutRegistry.ResolveForPilot(s, m, s.RandomSeed).Preview.RawFirmwareIds
                       .SequenceEqual(new[] { FirmwareCatalog.FwMarkTagId }) && (Unit(site, m).Flags & CombatUnitFlags.RawGated) == 0,
                "接入后：接入口插入未破解的标记跳转，内核武器带标记跳转反应（核心固件走反应门控，不带常规裸跑门控）");

            float e0 = s.SignalExposure;
            bool jumped = MarkJumpOnce(s, site, m);
            float e1 = s.SignalExposure;
            SignalExposureEventRecord last = CampaignExposureLedger.RecentEvents(s, 1).FirstOrDefault();
            double cd = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwMarkTagId);
            Expect(jumped && Mathf.Abs(e1 - e0 - 2f) < 1e-3f && last?.Kind == ExposureSourceKind.RawFire && last.Faction == "silent"
                   && CampaignExposureLedger.TotalAdded(s, ExposureSourceKind.CoreFire) <= 0f && cd > 6.0,
                $"标记跳转打出：暴露 {e0:0.#} → {e1:0.#}（裸跑 +2，来源“{CampaignExposureLedger.SourceText(last)}”，静默），冷却 {cd:F2} 秒");

            // 破解时冷却不重置（负向：破解完成时那件固件正在信号核里）。
            bool done = CrackViaBench(s, SalvageDatabox);
            double cdAfter = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwMarkTagId);
            Expect(done && !FirmwareKinds.IsRaw(s, FirmwareCatalog.FwMarkTagId) && Math.Abs(cdAfter - cd) < 0.25 && SignalCoreService.SlotPartId(s, 0) == part,
                $"破解完成（解析台）：标记去掉、同一件留在信号核、冷却不重置（破解前 {cd:F2} / 后 {cdAfter:F2} 游戏秒）");

            WorldSimulation.StepMany((int)Math.Ceiling(cdAfter * GameClock.StepHz) + 2);
            float e2 = s.SignalExposure;
            bool jumped2 = MarkJumpOnce(s, site, m);
            last = CampaignExposureLedger.RecentEvents(s, 1).FirstOrDefault();
            Expect(jumped2 && Mathf.Abs(s.SignalExposure - e2 - 0.5f) < 1e-3f && last?.Kind == ExposureSourceKind.CoreFire,
                $"冷却结束后再打出：按核心固件发动计 +0.5（{e2:0.#} → {s.SignalExposure:0.#}，来源“{CampaignExposureLedger.SourceText(last)}”）");
        }

        /// <summary>标记跳转打出一次：两台侦察机回满血、摆在一起，先各打一下挂标记，再打已标记目标（SIG-05 C2 同一路径）。返回有没有跳。</summary>
        private static bool MarkJumpOnce(CampaignState s, CombatSite site, int m)
        {
            string s1 = FracturedCityLayout.Scout1SpawnId;
            string s2 = FracturedCityLayout.Scout2SpawnId;
            foreach (string sid in new[] { s1, s2 })
            {
                RegionEnemyRecord rec = Enemy(s, sid);
                rec.MaxHealth = 1e5f;
                rec.Health = 1e5f;
                site.SyncEnemyFromRecord(rec);
            }
            PlaceEnemy(site, s1, new Vector2(-10f, -8f));
            PlaceEnemy(site, s2, new Vector2(-6f, -8f));
            int jump0 = FeedbackCues.CountOf(FeedbackCueId.ReactionMarkJump);
            FracturedCityRegion.TryAttackEnemy(s, m, s2, s.RandomSeed, isAiSource: false);
            FracturedCityRegion.TryAttackEnemy(s, m, s1, s.RandomSeed, isAiSource: false);
            FracturedCityRegion.TryAttackEnemy(s, m, s1, s.RandomSeed, isAiSource: false);
            WorldSimulation.StepMany(1);
            return FeedbackCues.CountOf(FeedbackCueId.ReactionMarkJump) > jump0;
        }

        // ── F2. FG2-FW-01 修复轮：新来源阵营（澄净 / 超频）也计“使用异派技术”────────────────────────

        /// <summary>
        /// FG2-FW-01 修复轮（审查 P1）：44 条固件带来了澄净（clarity）/ 超频（overclock）两个来源阵营。蓝图版本的派系标签改存阵营键，
        /// 暴露账本 FactionKeyOfTag 认全 5 个阵营（兼容旧存档的中文标签）。真实保存蓝图 → 登记机器 → 出发事务同一入口
        /// （GrantAlienTechForExpedition）逐一核对：归还底盘 + 澄净固件计 +2 且阵营 = clarity；归还 + 人类遗产（中立）不计；
        /// 澄净 + 超频（两个异族）计；旧存档中文标签（归还+超频）照样计；派系显示名走 faction.&lt;key&gt; 文本键随语言切换。
        /// </summary>
        private static void CheckAlienTechNewFactions()
        {
            Line("  · F2. 新来源阵营的异派技术（FG2-FW-01 修复轮）：派系标签存阵营键；澄净 / 超频固件与归还底盘同装计 +2；中立人类遗产不计；旧中文标签兼容；显示走文本键");
            CampaignState s = NewState(8642);
            const string oil = "fw_oilleak";   // 澄净（clarity），流体常规
            const string clock = "fw_clock";   // 超频（overclock），限制器常规
            const string pierce = "fw_pierce"; // 人类遗产（relic），中立协议
            Expect(FirmwareKinds.FactionOf(oil) == CampaignExposureLedger.FactionClarity && FirmwareKinds.FactionOf(clock) == CampaignExposureLedger.FactionOverclock
                   && FirmwareKinds.FactionOf(pierce) == "relic",
                $"测试前提：{oil} 阵营 {FirmwareKinds.FactionOf(oil)}、{clock} 阵营 {FirmwareKinds.FactionOf(clock)}、{pierce} 阵营 {FirmwareKinds.FactionOf(pierce)}");
            foreach (string id in new[] { oil, clock, pierce })
            {
                Unlock(s, id); // 已破解 / 已获得（敌方协议破解后才能进机器电路）
            }

            BlueprintVersionRecord SaveAndSpawn(string name, string[] firmware, out int logicId)
            {
                BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, firmware);
                BlueprintSaveResult save = BlueprintEditorService.TrySave(s, board, "bp_selfcheck_fw01_" + name, name, saveAsNewRecord: true);
                if (!save.Success)
                {
                    Fail($"测试准备：保存蓝图 {name}（{string.Join("+", firmware)}）失败：{save.FailureReason}");
                }
                BlueprintRecord rec = s.BlueprintRecords.FirstOrDefault(b => b.DisplayName == name);
                BlueprintVersionRecord ver = rec?.Versions?.LastOrDefault();
                logicId = SpawnRegion(HomeValleyLayout.RegionId, rec?.BlueprintId, new Vector2(3f, 1f));
                if (MachineRegistry.TryGetRecord(logicId, out MachineRecord mr) && mr != null && ver != null)
                {
                    mr.BlueprintVersion = ver.Version;
                }
                return ver;
            }

            string Tags(BlueprintVersionRecord v) => v?.FactionTags == null ? "（无版本）" : string.Join("+", v.FactionTags);

            // 1. 归还底盘 + 澄净固件：跨派系，出发计 +2，阵营 = clarity。
            BlueprintVersionRecord vOil = SaveAndSpawn("fw01_oil", new[] { oil }, out int mOil);
            float x0 = s.SignalExposure;
            bool oilGranted = CampaignExposureLedger.GrantAlienTechForExpedition(s, new[] { mOil }, FoundryOutpostLayout.RegionId, 21);
            SignalExposureEventRecord oilEvent = CampaignExposureLedger.RecentEvents(s, 1).FirstOrDefault();
            Expect(vOil != null && vOil.FactionTags.SequenceEqual(new[] { CampaignExposureLedger.FactionClarity, FirmwareKinds.FactionReclaim })
                   && oilGranted && Mathf.Abs(s.SignalExposure - x0 - 2f) < 1e-3f
                   && oilEvent != null && oilEvent.Kind == ExposureSourceKind.AlienTech && oilEvent.Faction == CampaignExposureLedger.FactionClarity,
                $"归还底盘 + 澄净固件 {oil}：版本派系标签 [{Tags(vOil)}]（阵营键）；出远征 → 使用异派技术 +2、记在澄净名下（{oilEvent?.Faction}）：{x0:0.#} → {s.SignalExposure:0.#}");

            // 2. 对照：归还底盘 + 人类遗产（中立）固件：只有一个派系，不计。
            BlueprintVersionRecord vPierce = SaveAndSpawn("fw01_pierce", new[] { pierce }, out int mPierce);
            float x1 = s.SignalExposure;
            bool pierceGranted = CampaignExposureLedger.GrantAlienTechForExpedition(s, new[] { mPierce }, FoundryOutpostLayout.RegionId, 22);
            Expect(vPierce != null && vPierce.FactionTags.SequenceEqual(new[] { FirmwareKinds.FactionReclaim }) && !pierceGranted
                   && Mathf.Approximately(s.SignalExposure, x1),
                $"对照：归还底盘 + 人类遗产 {pierce}（中立协议）：派系 [{Tags(vPierce)}]，不算跨派系，出远征不计（暴露 {x1:0.#} → {s.SignalExposure:0.#}）");

            // 3. 澄净 + 超频（两个异族阵营）：计，阵营取键序最小（clarity），与既有取值规则一致。
            BlueprintVersionRecord vMix = SaveAndSpawn("fw01_mix", new[] { oil, clock }, out int mMix);
            float x2 = s.SignalExposure;
            bool mixGranted = CampaignExposureLedger.GrantAlienTechForExpedition(s, new[] { mMix }, FoundryOutpostLayout.RegionId, 23);
            SignalExposureEventRecord mixEvent = CampaignExposureLedger.RecentEvents(s, 1).FirstOrDefault();
            Expect(vMix != null && vMix.FactionTags.Contains(CampaignExposureLedger.FactionOverclock) && vMix.FactionTags.Contains(CampaignExposureLedger.FactionClarity)
                   && mixGranted && Mathf.Abs(s.SignalExposure - x2 - 2f) < 1e-3f && mixEvent?.Faction == CampaignExposureLedger.FactionClarity,
                $"归还底盘 + 澄净 {oil} + 超频 {clock}：派系 [{Tags(vMix)}]；出远征 +2（阵营 {mixEvent?.Faction}）");

            // 4. 旧存档（修复前的版本存中文标签）：归还+超频 仍计，记在 overclock 名下。
            if (vPierce != null)
            {
                vPierce.FactionTags = new[] { "归还", "超频" };
            }
            float x3 = s.SignalExposure;
            bool legacyGranted = CampaignExposureLedger.GrantAlienTechForExpedition(s, new[] { mPierce }, FoundryOutpostLayout.RegionId, 24);
            SignalExposureEventRecord legacyEvent = CampaignExposureLedger.RecentEvents(s, 1).FirstOrDefault();
            Expect(legacyGranted && Mathf.Abs(s.SignalExposure - x3 - 2f) < 1e-3f && legacyEvent?.Faction == CampaignExposureLedger.FactionOverclock
                   && CampaignExposureLedger.FactionKeyOfTag("澄净") == CampaignExposureLedger.FactionClarity
                   && CampaignExposureLedger.FactionKeyOfTag("relic") == CampaignExposureLedger.FactionNone,
                $"旧存档版本的中文派系标签（归还+超频）：照样计 +2、阵营 {legacyEvent?.Faction}；“澄净”→clarity，人类遗产 relic → none");

            // 5. 与裸跑路径同一套阵营键：同一条澄净固件裸跑记在 clarity 名下（GrantRawFire 走 FactionOf）。
            CampaignExposureLedger.GrantRawFire(s, oil);
            SignalExposureEventRecord rawEvent = CampaignExposureLedger.RecentEvents(s, 1).FirstOrDefault();
            Expect(rawEvent?.Kind == ExposureSourceKind.RawFire && rawEvent.Faction == oilEvent?.Faction,
                $"同一条 {oil}：裸跑明细阵营（{rawEvent?.Faction}）与异派技术明细阵营（{oilEvent?.Faction}）一致");

            // 6. 显示名走 faction.<key> 文本键，随语言切换；旧中文标签也按当前语言显示。
            GameSettings.SetLanguage(GameLanguage.En);
            string enClarity = BlueprintCircuitBoard.FactionDisplayName(CampaignExposureLedger.FactionClarity);
            string enLegacy = BlueprintCircuitBoard.FactionDisplayName("超频");
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            string zhClarity = BlueprintCircuitBoard.FactionDisplayName(CampaignExposureLedger.FactionClarity);
            Expect(zhClarity == GameText.Get("faction.clarity", GameLanguage.ZhCn) && enClarity == GameText.Get("faction.clarity", GameLanguage.En)
                   && enLegacy == GameText.Get("faction.overclock", GameLanguage.En) && zhClarity != enClarity && !GameText.ContainsMarker(enClarity)
                   && !GameText.ContainsMarker(enLegacy),
                $"派系显示名走文本键：澄净 中文“{zhClarity}” / 英文“{enClarity}”；旧标签“超频”在英文下显示“{enLegacy}”");
        }

        // ── F. 暴露改写（FGR-SIG-070）────────────────────────────────────────────

        private static void CheckExposureRewrite()
        {
            Line("  · F. 暴露改写：接入本身与接入时长不计；跨阵营蓝图保存不计；异派技术每次远征 +2；节点 +3 / -15 两笔；塔关广播 -2/10 秒；高功率按用电换算；阈值照旧；明细截断而汇总不丢");
            CampaignState s = NewState(8641);
            int g = SpawnRegion(FoundryOutpostLayout.RegionId, BpGun, new Vector2(-30f, -40f), 1e6f);
            FoundryOutpostController fo = OpenFoundry(s, g);
            WorldView.Observe(fo.SiteId);
            CommitVia(g);
            WorldSimulation.StepMany(65 * GameClock.StepHz);
            Expect(SignalUplinkService.IsUplinked(s, g) && Mathf.Approximately(s.SignalExposure, 0f) && (s.SignalExposureEvents?.Length ?? 0) == 0,
                $"在远征地点接入一台机器 65 游戏秒（Demo 规则会 +10）：暴露 {s.SignalExposure:0.#}、没有任何来源记录——接入与接入时长不再计入");

            BlueprintCircuitBoard cross = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, ComponentCatalog.FuncMarkerId, null,
                Array.Empty<string>());
            Unlock(s, ComponentCatalog.FuncMarkerId);
            BlueprintSaveResult save = BlueprintEditorService.TrySave(s, cross, "bp_selfcheck_sig06_cross", "cross", saveAsNewRecord: true);
            Expect(save.Success && cross.ComputeFactionTags().Length >= 2 && Mathf.Approximately(s.SignalExposure, 0f),
                $"保存跨阵营蓝图（{string.Join("+", cross.ComputeFactionTags())}）：保存成功、暴露不变（Demo“首次使用异派固件 +10”不再按保存计）");

            // 异派技术：出发时按远征队计。
            BlueprintRecord crossRecord = s.BlueprintRecords.FirstOrDefault(b => b.DisplayName == "cross");
            BlueprintVersionRecord crossVersion = crossRecord?.Versions?.LastOrDefault();
            int c1 = SpawnRegion(HomeValleyLayout.RegionId, crossRecord?.BlueprintId, new Vector2(1f, 1f));
            MachineRegistry.TryGetRecord(c1, out MachineRecord cr);
            if (cr != null && crossVersion != null)
            {
                cr.BlueprintVersion = crossVersion.Version;
            }
            int plain = SpawnRegion(HomeValleyLayout.RegionId, BpGun, new Vector2(2f, 1f));
            float x0 = s.SignalExposure;
            bool none = CampaignExposureLedger.GrantAlienTechForExpedition(s, new[] { plain }, FoundryOutpostLayout.RegionId, 7);
            bool first = CampaignExposureLedger.GrantAlienTechForExpedition(s, new[] { plain, c1 }, FoundryOutpostLayout.RegionId, 7);
            bool dup = CampaignExposureLedger.GrantAlienTechForExpedition(s, new[] { c1 }, FoundryOutpostLayout.RegionId, 7);
            bool next = CampaignExposureLedger.GrantAlienTechForExpedition(s, new[] { c1 }, FoundryOutpostLayout.RegionId, 8);
            SignalExposureEventRecord alien = CampaignExposureLedger.RecentEvents(s, 1).First();
            Expect(!none && first && !dup && next && Mathf.Abs(s.SignalExposure - x0 - 4f) < 1e-3f && alien.Kind == ExposureSourceKind.AlienTech && alien.Faction == "silent"
                   && crossVersion != null && crossVersion.FactionTags.Length >= 2,
                $"使用异派技术（出发事务调用）：只有同阵营蓝图 → 不计；带跨阵营蓝图 → +2（“{CampaignExposureLedger.SourceText(alien)}”）；同一次远征不重复，下一次远征再 +2");

            float n0 = s.SignalExposure;
            int ev0 = s.SignalExposureEvents.Length;
            CampaignExposureLedger.GrantNodeDestroyed(s, "sig06-node");
            CampaignExposureLedger.GrantNodeDestroyed(s, "sig06-node");
            SignalExposureEventRecord[] nodeEvents = CampaignExposureLedger.RecentEvents(s, 2);
            float cutActual = Mathf.Max(0f, n0 + 3f - 15f) - (n0 + 3f);
            Expect(s.SignalExposureEvents.Length == ev0 + 2 && nodeEvents.Any(e => e.Kind == ExposureSourceKind.NodeDestroyed && Mathf.Approximately(e.Delta, 3f))
                   && nodeEvents.Any(e => e.Kind == ExposureSourceKind.NodeLinkCut && Mathf.Approximately(e.Delta, cutActual))
                   && Mathf.Approximately(s.SignalExposure, Mathf.Max(0f, Mathf.Max(0f, n0 + 3f) - 15f)),
                $"摧毁监听节点：两笔独立记录（摧毁敌方节点 +3、切断监听链名义 -15，明细按钳制后实际 {cutActual:0.#} 记），同一节点不重复：{n0:0.#} → {s.SignalExposure:0.#}");

            s.SignalExposure = 29f;
            int scout0 = s.ScoutTipCrossCount;
            CampaignExposureLedger.GrantNodeDestroyed(s, "sig06-node-2");
            Expect(s.ScoutTipCrossCount == scout0 + 1 && CampaignEventLedger.Contains(s, $"exposure_threshold_scout_tip:{s.ScoutTipCrossCount}"),
                "阈值照旧：29 + 3 越过 30 → 静默侦察提示事件记账（之后的 -15 把它降回去）");

            // 明细截断、汇总不丢（每笔都是真实变化：满 90 前每次 +2，测试在中途把暴露拨回 0 继续加）
            CampaignState h = NewState(8642);
            for (int i = 0; i < 80; i++)
            {
                if (h.SignalExposure >= 90f)
                {
                    h.SignalExposure = 0f;
                }
                CampaignExposureLedger.GrantRawFire(h, FirmwareCatalog.FwArmorPierceId);
            }
            Expect(h.SignalExposureEvents.Length == CampaignExposureLedger.HistoryMax
                   && Mathf.Approximately(CampaignExposureLedger.TotalAdded(h, ExposureSourceKind.RawFire), 160f)
                   && CampaignExposureLedger.RecentEvents(h, 1)[0].Seq == 80 && h.SignalExposureEvents.Min(e => e.Seq) == 80 - CampaignExposureLedger.HistoryMax + 1,
                $"80 次裸跑（每次真实 +2）：明细只留最近 {CampaignExposureLedger.HistoryMax} 条（序号 {h.SignalExposureEvents.Min(e => e.Seq)}～80），各来源累计 160 不随截断丢失");

            // 上限 100：累计按实际变化，封顶后不再记明细（FGR-SIG-070：累计不会超过真实暴露）
            CampaignState cap = NewState(8645);
            cap.SignalExposure = 99f;
            for (int i = 0; i < 5; i++)
            {
                CampaignExposureLedger.GrantRawFire(cap, FirmwareCatalog.FwArmorPierceId);
            }
            SignalExposureEventRecord[] capEvents = cap.SignalExposureEvents ?? Array.Empty<SignalExposureEventRecord>();
            Expect(Mathf.Approximately(cap.SignalExposure, 100f) && capEvents.Length == 1 && Mathf.Approximately(capEvents[0].Delta, 1f)
                   && Mathf.Approximately(CampaignExposureLedger.TotalAdded(cap, ExposureSourceKind.RawFire), 1f),
                $"暴露 99 时裸跑 5 次：封顶 100，只记 1 笔实际 +{(capEvents.Length > 0 ? capEvents[0].Delta : 0f):0.#}，累计 {CampaignExposureLedger.TotalAdded(cap, ExposureSourceKind.RawFire):0.#}（不按名义 +10 记）");

            // 长期关广播：暴露已 0 时塔关不写明细，真正的来源不会被挤出“最近来源”
            CampaignState quiet = NewState(8646);
            CampaignExposureLedger.GrantRawFire(quiet, FirmwareCatalog.FwArmorPierceId);
            CampaignExposureLedger.SetTowerBroadcastOff(quiet, true);
            for (int i = 0; i < 600; i++)
            {
                CampaignExposureLedger.TickTowerBroadcastOff(quiet, 1f); // 10 游戏分钟
            }
            SignalExposureEventRecord[] quietRecent = CampaignExposureLedger.RecentEvents(quiet, 12);
            float towerRemoved = (quiet.SignalExposureTotals ?? Array.Empty<SignalExposureTotalRecord>()).Where(t => t != null && t.Kind == ExposureSourceKind.TowerOff).Sum(t => t.Removed);
            Expect(Mathf.Approximately(quiet.SignalExposure, 0f) && quiet.SignalExposureEvents.Length == 2 && quietRecent.Any(e => e.Kind == ExposureSourceKind.RawFire)
                   && Mathf.Approximately(towerRemoved, 2f),
                $"关广播 10 游戏分钟：暴露 2 → 0 只记 1 笔塔关（-2），之后暴露为 0 的 59 个周期不写明细；最近来源仍能看到裸跑；塔关累计降低 {towerRemoved:0.#}（按实际）");

            // 各阵营贡献只算敌方阵营；己方核心固件 / 家园活动单列
            CampaignState fac = NewState(8647);
            CampaignExposureLedger.GrantCoreFire(fac, FirmwareCatalog.FwOverloadId, raw: false);
            CampaignExposureLedger.GrantRawFire(fac, FirmwareCatalog.FwArmorPierceId);
            IReadOnlyList<(string Faction, float Added)> contrib = CampaignExposureLedger.FactionContributions(fac);
            Expect(contrib.Count == 1 && contrib[0].Faction == "foundry" && Mathf.Approximately(contrib[0].Added, 2f)
                   && Mathf.Approximately(CampaignExposureLedger.OwnActivityAdded(fac), 0.5f)
                   && !CampaignExposureLedger.IsEnemyFaction("reclaim") && !CampaignExposureLedger.IsEnemyFaction(CampaignExposureLedger.FactionNone),
                $"各阵营贡献只列敌方阵营（{string.Join("、", contrib.Select(c => c.Faction + " +" + c.Added.ToString("0.#")))}）；己方过载发动 +0.5 单列为“己方与家园活动”，FG6-DEF-04 选突袭阵营不会选中己方");

            // 塔关广播与高功率（家园模拟步）
            CampaignState home = NewHome(8643);
            home.SignalExposure = 20f;
            CampaignExposureLedger.SetTowerBroadcastOff(home, true);
            home.PowerDemand = 150f;
            WorldSimulation.StepMany(50 * GameClock.StepHz);
            float towerSum = home.SignalExposureEvents.Where(e => e.Kind == ExposureSourceKind.TowerOff).Sum(e => e.Delta);
            SignalExposureEventRecord hp = home.SignalExposureEvents.FirstOrDefault(e => e.Kind == ExposureSourceKind.HighPower);
            Expect(Mathf.Approximately(towerSum, -10f) && hp != null && Mathf.Abs(hp.Delta - 1.5f) < 0.01f && hp.Detail == "150"
                   && Mathf.Abs(home.SignalExposure - (20f - 10f + 1.5f)) < 0.02f,
                $"家园 50 游戏秒（1 游戏小时）：塔关广播 5 笔共 {towerSum:0.#}；用电 150 → 高功率生产 +{hp?.Delta:0.##}（3 ×（150−100）/100，来源“{CampaignExposureLedger.SourceText(hp)}”）");
            CampaignExposureLedger.SetTowerBroadcastOff(home, false);
            home.PowerDemand = 90f;
            int hpCount = home.SignalExposureEvents.Count(e => e.Kind == ExposureSourceKind.HighPower);
            WorldSimulation.StepMany(50 * GameClock.StepHz);
            Expect(home.SignalExposureEvents.Count(e => e.Kind == ExposureSourceKind.HighPower) == hpCount && Mathf.Approximately(CampaignExposureLedger.HighPowerRatePerHour(90f), 0f)
                   && Mathf.Approximately(CampaignExposureLedger.HighPowerRatePerHour(250f), 3f),
                "用电 90（开局配置，未超阈值 100）：一个游戏小时不产生高功率暴露；用电 250 封顶 +3/小时");
        }

        // ── G. 旧档迁移与存读档 ──────────────────────────────────────────────────

        private static void CheckMigrationAndSave()
        {
            Line("  · G. 旧档（Demo 暴露规则、已带回未发放的加密固件）走真实文件读档：暴露值原样保留、旧来源补种类、直控累计停用、补发未破解固件；新状态逐字段往返");
            CampaignState s = NewHome(8651);
            s.ExposureRulesVersion = 0;
            s.SignalExposure = 37f;
            s.SignalExposureTotals = Array.Empty<SignalExposureTotalRecord>();
            s.SignalExposureEvents = new[]
            {
                Legacy("exposure:heavy_produced:12", "重型机生产", 8f, 10f),
                Legacy("exposure:direct_control_30s:1", "远征直控30秒", 5f, 20f),
                Legacy("exposure:direct_control_30s:2", "远征直控30秒", 5f, 30f),
                Legacy("exposure:cross_faction_firmware:bp_x:2", "异派固件首次使用", 10f, 40f),
                Legacy("exposure:node_combat:ln", "摧毁节点", 8f, 50f),
                Legacy("exposure:node_link_cut:ln", "监听链摧毁", -15f, 50f),
                Legacy("exposure:tower_off_tick:1", "塔关广播", -2f, 60f),
                Legacy("exposure:mystery:1", "神秘来源", 18f, 70f),
            };
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.Find(s).DirectControlAccumulatedSeconds = 17f;
            s.RegionQuestItems = new[]
            {
                new RegionQuestItemRecord { SalvageInstanceId = "g-databox", RegionId = FracturedCityLayout.RegionId, ContentId = FracturedCityLayout.ProtocolDataboxContentId,
                    State = RegionQuestItemState.Recovered },
            };
            SaveNow();
            CampaignState l = LoadLikeMenu();
            SignalExposureEventRecord[] ev = l?.SignalExposureEvents ?? Array.Empty<SignalExposureEventRecord>();
            string Kind(string id) => ev.FirstOrDefault(e => e.EventId == id)?.Kind;
            PrimitiveChipRecord chip = l?.PrimitiveChips?.FirstOrDefault(c => c.SourceSalvageId == "g-databox");
            Expect(l != null && Mathf.Approximately(l.SignalExposure, 37f) && l.ExposureRulesVersion == CampaignExposureLedger.CurrentRulesVersion
                   && Kind("exposure:heavy_produced:12") == ExposureSourceKind.LegacyHeavy && Kind("exposure:direct_control_30s:1") == ExposureSourceKind.LegacyDirectControl
                   && Kind("exposure:cross_faction_firmware:bp_x:2") == ExposureSourceKind.LegacyCrossFaction && Kind("exposure:node_combat:ln") == ExposureSourceKind.NodeDestroyed
                   && Kind("exposure:node_link_cut:ln") == ExposureSourceKind.NodeLinkCut && Kind("exposure:tower_off_tick:1") == ExposureSourceKind.TowerOff
                   && Kind("exposure:mystery:1") == ExposureSourceKind.LegacyOther && ev.All(e => e.Seq > 0)
                   && FracturedCityRegion.Find(l).DirectControlAccumulatedSeconds == 0f
                   && Mathf.Approximately(CampaignExposureLedger.TotalAdded(l, ExposureSourceKind.LegacyDirectControl), 10f)
                   && chip != null && chip.CardDefId == FirmwareCatalog.FwMarkTagId && FirmwareKinds.IsRaw(l, chip.CardDefId),
                $"读档后：暴露 {l?.SignalExposure:0.#}（原样保留）、规则版本 {l?.ExposureRulesVersion}；8 条旧明细补上来源种类（直控 / 重型机 / 首次异派 → 旧规则，节点 / 塔关按新种类，未知来源保留原文）；" +
                "直控累计清零停用；各来源累计由旧明细重建；已带回的静默协议数据盒补发一枚未破解标记跳转");

            // 再存再读：幂等（不重复迁移、不重复补发），新状态逐字段往返。
            l.SignalCore.RawChargeReadyTick = GameClock.TickAfter(5.5);
            l.HighPowerAccrued = 0.75f;
            l.HighPowerElapsedTicks = GameClock.TicksFor(12.5);
            CampaignExposureLedger.GrantRawFire(l, FirmwareCatalog.FwMarkTagId);
            string before = ExposureSnapshot(l);
            int chips = l.PrimitiveChips.Length;
            SaveNow();
            CampaignState l2 = LoadLikeMenu();
            string after = l2 != null ? ExposureSnapshot(l2) : "(读档失败)";
            Expect(l2 != null && before == after && l2.PrimitiveChips.Length == chips
                   && l2.SignalCore.RawChargeReadyTick == l.SignalCore.RawChargeReadyTick,
                $"再存再读：暴露值、明细（种类 / 细节 / 阵营 / 序号）、各来源累计、高功率窗口、裸跑计次时刻逐字段一致，芯片数不变（{chips}，不重复补发）");
            string zh = CampaignExposureLedger.SourceText(ev.First(e => e.EventId == "exposure:direct_control_30s:1"));
            GameSettings.SetLanguage(GameLanguage.En);
            string en = CampaignExposureLedger.SourceText(ev.First(e => e.EventId == "exposure:direct_control_30s:1"));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(zh.Contains("旧规则") && en.Contains("old rules"), $"旧来源按当前语言显示：“{zh}” / “{en}”");
        }

        private static SignalExposureEventRecord Legacy(string id, string source, float delta, float at) =>
            new SignalExposureEventRecord { EventId = id, Source = source, Delta = delta, AtPlaySeconds = at, ResultingExposure = 0f };

        private static string ExposureSnapshot(CampaignState s)
        {
            var sb = new StringBuilder();
            sb.Append(s.SignalExposure.ToString("R")).Append('|').Append(s.ExposureRulesVersion).Append('|').Append(s.ExposureEventSeq)
                .Append('|').Append(s.HighPowerAccrued.ToString("R")).Append('|').Append(s.HighPowerElapsedTicks)
                .Append('|').Append(s.SignalCore.RawChargeReadyTick);
            foreach (SignalExposureEventRecord e in s.SignalExposureEvents.OrderBy(e => e.Seq))
            {
                sb.Append('|').Append(e.EventId).Append(':').Append(e.Kind).Append(':').Append(e.Detail).Append(':').Append(e.Faction).Append(':').Append(e.Seq)
                    .Append(':').Append(e.Delta.ToString("R"));
            }
            foreach (SignalExposureTotalRecord t in s.SignalExposureTotals.OrderBy(t => t.Kind).ThenBy(t => t.Faction))
            {
                sb.Append("|T").Append(t.Kind).Append(':').Append(t.Faction).Append(':').Append(t.Added.ToString("R")).Append(':').Append(t.Removed.ToString("R"))
                    .Append(':').Append(t.Count);
            }
            return sb.ToString();
        }

        // ── H. 暂停与 0.5x～3x ────────────────────────────────────────────────────

        private static void CheckPauseSpeed()
        {
            Line("  · H. 暂停与 0.5x～3x：裸跑计次间隔与高功率累计都按游戏时间（暂停不走，各档倍速结果一致）");
            var detail = new List<string>();
            bool ok = true;
            bool pauseOk = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                CampaignState s = NewHome(8660 + (int)(speed * 10));
                s.PowerDemand = 200f;
                int rearm0 = RawFirmwareService.RearmCount;
                s.SignalCore.RawChargeReadyTick = GameClock.TickAfter(8.0);
                GameClock.SetPaused(true);
                Frames(40);
                pauseOk &= RawFirmwareService.RearmCount == rearm0 && s.SignalExposureEvents.Length == 0 && s.HighPowerElapsedTicks == 0;
                GameClock.SetPaused(false);
                GameClock.SetSpeed(speed);
                double start = GameClock.GameSeconds;
                double rearmAt = -1;
                int guard = 0;
                while (s.SignalExposureEvents.All(e => e.Kind != ExposureSourceKind.HighPower) && guard++ < 20000)
                {
                    Frames(1);
                    if (rearmAt < 0 && RawFirmwareService.RearmCount > rearm0)
                    {
                        rearmAt = GameClock.GameSeconds - start;
                    }
                }
                SignalExposureEventRecord hp = s.SignalExposureEvents.FirstOrDefault(e => e.Kind == ExposureSourceKind.HighPower);
                double hpAt = GameClock.GameSeconds - start;
                ok &= Math.Abs(rearmAt - 8.0) <= FrameDt * speed + GameClock.StepSeconds + 1e-4 && hp != null && Mathf.Abs(hp.Delta - 3f) < 0.01f
                      && Math.Abs(hpAt - 50.0) <= FrameDt * speed + GameClock.StepSeconds + 1e-4;
                detail.Add($"{speed}x:计次 {rearmAt:F2}s/高功率 {hpAt:F2}s +{hp?.Delta:0.##}");
                GameClock.SetSpeed(1f);
            }
            Expect(pauseOk, "暂停 40 帧：计次间隔不到期、高功率不累计");
            Expect(ok, $"0.5x / 1x / 2x / 3x：计次间隔都在 8 游戏秒到期、高功率都在 50 游戏秒记一笔 +3（{string.Join("，", detail)}；误差上限一帧）");
        }

        // ── I. 家园观察无关 ──────────────────────────────────────────────────────

        private static void CheckObservationIndependence()
        {
            Line("  · I. 家园不被观察时结果一致：高功率与塔关广播在后台照样按游戏时间结算，与盯着家园时逐笔相同");
            string Run(bool observeHome)
            {
                CampaignState s = NewHome(8671);
                s.SignalExposure = 40f;
                s.PowerDemand = 170f;
                CampaignExposureLedger.SetTowerBroadcastOff(s, true);
                if (!observeHome)
                {
                    FracturedCityController city = OpenCity(s);
                    WorldView.Observe(city.SiteId);
                }
                WorldSimulation.StepMany(120 * GameClock.StepHz);
                return string.Join(";", s.SignalExposureEvents.OrderBy(e => e.Seq).Select(e => $"{e.Kind}:{e.Delta:R}:{e.ResultingExposure:R}")) + "|" + s.SignalExposure.ToString("R");
            }
            string watched = Run(true);
            string background = Run(false);
            Expect(watched == background && watched.Contains(ExposureSourceKind.HighPower) && watched.Contains(ExposureSourceKind.TowerOff),
                $"120 游戏秒：盯着家园与镜头在破碎都市两次运行的暴露明细逐笔相同（{watched.Split(';').Length} 笔，末值 {watched.Split('|').Last()}）");
        }

        // ── J. 性能 ──────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · J. 性能：高功率累计每步 O(1)；一笔暴露写入（含明细截断）；裸跑结算（一次接入编译）只在开火计次时发生；面板重建只在键变化时");
            CampaignState s = NewState(8681);
            s.PowerDemand = 150f;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100000; i++)
            {
                CampaignExposureLedger.SimStepHighPower(s, GameClock.StepSeconds);
            }
            double stepUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 100000.0;
            sw.Restart();
            for (int i = 0; i < 1000; i++)
            {
                CampaignExposureLedger.GrantRawFire(s, FirmwareCatalog.FwArmorPierceId);
            }
            double applyUs = sw.Elapsed.TotalMilliseconds * 1000.0 / 1000.0;
            ExpectPerf(true,
                $"高功率累计 {stepUs:F3} µs/步（门槛 5 µs）；一笔暴露写入（明细满 {CampaignExposureLedger.HistoryMax} 条时截断）{applyUs:F1} µs（门槛 200 µs；每 8 游戏秒至多一笔）",
                PerfGate.Lt(stepUs, 5.0, "高功率累计 µs/步"), PerfGate.Lt(applyUs, 200.0, "暴露写入 µs"));
            PerfLines.Add($"高功率累计 {stepUs:F3} µs/步、暴露写入 {applyUs:F1} µs/笔——Editor 下 Mono JIT；真机 HybridCLR 解释执行预计慢数倍（真机复测归 FG15-SYS-02），均与机器数无关");
        }

        // ── K. 界面 ──────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            Line("  · K. 界面：信号核里“▲未破解”红框与悬停说明、双态预览的裸跑行、HUD“暴露 N”、暴露面板（正常 / 空 / 旧规则说明）、Alt+P 与 Esc、布局探针、文本键");
            CampaignState s = NewState(8691);
            string part = GrantRaw(s, FoundryOutpostLayout.RegionId, FoundryOutpostLayout.ArmorPierceCacheContentId, SalvagePierce);
            string bagPart = GrantRaw(s, FracturedCityLayout.RegionId, FracturedCityLayout.ProtocolDataboxContentId, SalvageDatabox);
            Equip(s, part, 0);
            SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
            VisualElement root = Mount(out GameObject go);
            SignalCoreHudUIToolkit hud = go.AddComponent<SignalCoreHudUIToolkit>();
            try
            {
                hud.BindView(root);
                hud.SetOpen(true);
                hud.Refresh();
                int bagIndex = Enumerable.Range(0, hud.VisibleBagItemCount).FirstOrDefault(i => hud.BagItemPartId(i) == bagPart);
                string tag = GameText.Get("signal.core.raw_tag");
                string tip = SignalCoreHudUIToolkit.RawTip(s, FirmwareCatalog.FwArmorPierceId);
                Expect(hud.SlotText(0).Contains(tag) && hud.SlotButton(0).ClassListContains("sc-item-raw") && hud.BagItemText(bagIndex).Contains(tag)
                       && hud.BagButton(bagIndex).ClassListContains("sc-item-raw") && tip.Contains("+2") && tip.Contains("1.5")
                       && !hud.SlotText(1).Contains(tag) && !hud.SlotButton(1).ClassListContains("sc-item-raw"),
                    $"信号核：1 号槽“{hud.SlotText(0)}”、基元仓“{hud.BagItemText(bagIndex)}”都带“▲未破解”与红框类；悬停说明写明代价“{tip.Trim().Replace("\n", " ")}”");
                Unlock(s, FirmwareCatalog.FwArmorPierceId);
                FirmwareKinds.NotifyCrackStateChanged();
                hud.Refresh();
                Expect(!hud.SlotText(0).Contains(tag) && !hud.SlotButton(0).ClassListContains("sc-item-raw") && hud.BagItemText(bagIndex).Contains(tag),
                    "破解装甲击穿后：1 号槽的标记与红框自动消失（同一件），基元仓里未破解的标记跳转仍标着");
                hud.SetOpen(false);

                // 双态预览：裸跑行与说明
                BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
                board.TrySetUplink(2);
                s.UnlockedContentIds = s.UnlockedContentIds.Where(id => id != FirmwareCatalog.FwArmorPierceId).ToArray();
                UplinkDualPreview dual = UplinkCompiler.CompileDual(board, new[] { FirmwareCatalog.FwArmorPierceId });
                string rawLine = GameText.Format("circuit.uplink.line.raw", FirmwareKinds.DisplayName(FirmwareCatalog.FwArmorPierceId), "2", "1.5");
                Expect(dual.UplinkedLines.Any(x => x.Text == rawLine) && dual.Notes.Any(n => n.Contains(FirmwareKinds.DisplayName(FirmwareCatalog.FwArmorPierceId)) && n.Contains("1.5"))
                       && Mathf.Approximately(dual.Ai.HeatBudget, 40f) && Mathf.Approximately(dual.Uplinked.HeatBudget, 60f)
                       && dual.Diff.Any(d => d.Kind == UplinkDiffKind.HeatChanged),
                    $"双态预览（接入前就知道代价）：接入栏“{rawLine}”，说明“{dual.Notes.FirstOrDefault(n => n.Contains("1.5"))}”，热量预算 40 → 60");

                // HUD 暴露按钮 + 面板
                hud.Refresh();
                string entry0 = hud.Exposure.EntryText;
                Click(hud.Exposure.EntryButton);
                hud.Refresh();
                bool emptyShown = hud.Exposure.PanelVisible && hud.Exposure.RecentEmptyVisible && hud.Exposure.RecentCount == 0 && UiEscapeStack.Count > 0;
                CampaignExposureLedger.GrantRawFire(s, FirmwareCatalog.FwArmorPierceId);
                CampaignExposureLedger.GrantCoreFire(s, FirmwareCatalog.FwOverloadId, raw: false);
                CampaignExposureLedger.GrantNodeDestroyed(s, "ui-node");
                s.SignalExposure = 35f;
                hud.Refresh();
                Expect(entry0 == GameText.Format("exposure.hud.button", "0") && emptyShown,
                    $"HUD“{entry0}”；点它打开暴露面板（模态、进 Esc 栈），没有记录时显示空状态“{GameText.Get("exposure.panel.recent_empty")}”");
                Expect(hud.Exposure.EntryText == GameText.Format("exposure.hud.button", "35") && hud.Exposure.RecentCount == 4 && !hud.Exposure.RecentEmptyVisible
                       && hud.Exposure.RecentText(0).Contains(GameText.Get("exposure.source.node_link_cut")) && hud.Exposure.RecentText(3).Contains("+2")
                       && hud.Exposure.FactionCount == 3 && hud.Exposure.FactionText(0).Contains("静默") && hud.Exposure.FactionText(1).Contains("铸造")
                       && hud.Exposure.FactionText(2) == GameText.Format("exposure.panel.own_item", "0.5") && !hud.Exposure.FactionText(0).Contains("归还")
                       && hud.Exposure.NextText.Contains("60") && hud.Exposure.RulesText.Contains("0.5") && hud.Exposure.RulesText.Contains("接入本身")
                       && hud.Exposure.LowerText.Contains("-15") && !hud.Exposure.LegacyNoteVisible,
                    $"面板：值“{hud.Exposure.ValueText}”、下一个阈值“{hud.Exposure.NextText}”；最近来源 {hud.Exposure.RecentCount} 条（新的在上：“{hud.Exposure.RecentText(0)}”）；" +
                    $"各阵营贡献 {hud.Exposure.FactionCount} 项（第一“{hud.Exposure.FactionText(0)}”）；怎么计 / 怎么降写明数值与“接入本身不计”");
                UiEscapeStack.CloseTop();
                Expect(!hud.Exposure.PanelVisible && !hud.Exposure.IsOpen, "Esc（取消栈顶）关闭暴露面板");

                // Alt+P（正式输入泵；编辑模式下没有 Awake，按信号核自检的做法把实例挂上）
                SetInstance(hud);
                InputRouter.Reset();
                InputRouter.DebugSetReader(Keys);
                InputRouter.SetScope(InputScope.Strategy);
                Keys.Held = KeyCode.LeftAlt;
                Keys.Down = KeyCode.P;
                InputRouter.DebugClearConsumedKeys();
                UiKitInputPump.ProcessWorldKeys();
                bool openedByKey = hud.Exposure.IsOpen && !SignalCoreHudUIToolkit.IsOpen;
                InputRouter.DebugClearConsumedKeys();
                UiKitInputPump.ProcessWorldKeys();
                bool closedByKey = !hud.Exposure.IsOpen;
                Keys.Held = KeyCode.None;
                InputRouter.DebugClearConsumedKeys();
                UiKitInputPump.ProcessWorldKeys();
                bool plainP = SignalCoreHudUIToolkit.IsOpen && !hud.Exposure.IsOpen;
                hud.SetOpen(false);
                Keys.Down = KeyCode.None;
                SetInstance(null);
                Expect(openedByKey && closedByKey && plainP, $"正式输入泵：Alt+P 打开暴露面板（信号核没被误开）{openedByKey}、再按一次关闭 {closedByKey}；单按 P 仍是信号核 {plainP}");

                // 旧规则说明
                CampaignExposureLedger.GrantRawFire(s, FirmwareCatalog.FwArmorPierceId);
                s.SignalExposureEvents = s.SignalExposureEvents.Append(new SignalExposureEventRecord
                {
                    EventId = "exposure:direct_control_30s:9", Kind = ExposureSourceKind.LegacyDirectControl, Delta = 5f, Faction = "none", Seq = 999,
                }).ToArray();
                hud.Exposure.SetOpen(true);
                hud.Refresh();
                Expect(hud.Exposure.LegacyNoteVisible && hud.Exposure.RecentText(0).Contains("旧规则"), $"旧档来源标“旧规则”并显示说明（“{hud.Exposure.RecentText(0)}”）");
                hud.Exposure.SetOpen(false);
            }
            finally
            {
                hud.SetOpen(false);
                hud.Exposure.SetOpen(false);
                SetInstance(null);
                Object.DestroyImmediate(go);
                InputRouter.Reset();
                InputRouter.DebugSetReader(Keys);
            }

            // 布局探针：暴露面板与 HUD 条，中英文 × UI 缩放极值 × 四种分辨率。
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (float scale in new[] { 0.8f, 1f, 1.5f })
                {
                    foreach (string target in new[] { "ExposurePanel", "SignalHudBar" })
                    {
                        string result = UiToolkitLayoutProbe.Probe(UxmlPath, target, stressFill: true, prepare: r =>
                        {
                            var probeGo = new GameObject("__probe_exposure") { hideFlags = HideFlags.HideAndDontSave };
                            SignalCoreHudUIToolkit h = probeGo.AddComponent<SignalCoreHudUIToolkit>();
                            h.BindView(r.panel.visualTree);
                            h.Exposure.SetOpen(true);
                            h.Refresh();
                            h.Exposure.SetOpen(false);
                            Object.DestroyImmediate(probeGo);
                        }, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"布局探针 SignalCorePanel.uxml#{target} [{lang}] 缩放 {scale:0.#}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(500, result.Length)))}");
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            SignalCoreHudUIToolkit.InWorldOverrideForTests = null;

            // 文本键：代码里用到的本 Story 文本键中英齐全、译文不同。
            string logic = Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic");
            string[] files =
            {
                "Campaign/CampaignExposureLedger.cs", "Campaign/Signal/RawFirmwareService.cs", "Campaign/Signal/FirmwareKinds.cs", "UI/SignalCore/ExposurePanelView.cs",
                "UI/SignalCore/SignalCoreHudUIToolkit.cs", "Campaign/Blueprint/UplinkCompiler.cs", "Campaign/Blueprint/BlueprintCircuitBoard.cs",
            };
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                foreach (Match mt in Regex.Matches(File.ReadAllText(Path.Combine(logic, f)),
                             "\"((?:exposure\\.|signal\\.raw\\.|signal\\.reason\\.raw|signal\\.reason\\.core_turret|signal\\.reason\\.not_firmware_host|signal\\.core\\.raw|circuit\\.uplink\\.(?:line|note)\\.raw|faction\\.)[a-z0-9_.]*)\""))
                {
                    string k = mt.Groups[1].Value;
                    // 调参 ID（exposure.* / signal.raw.* 同名前缀）不是文本键，按调参表排除。
                    if (!k.EndsWith(".", StringComparison.Ordinal) && !GridContent.TryGetTuning(k, out _))
                    {
                        keys.Add(k);
                    }
                }
            }
            foreach (string k in new[] { "faction.reclaim", "faction.silent", "faction.foundry", "faction.none" }.Concat(new[]
                     {
                         "exposure.source.high_power", "exposure.source.core_fire", "exposure.source.raw_fire", "exposure.source.alien_tech",
                         "exposure.source.node_destroyed", "exposure.source.node_link_cut", "exposure.source.tower_off", "exposure.source.legacy_heavy",
                         "exposure.source.legacy_cross_faction", "exposure.source.legacy_direct_control", "exposure.source.legacy_other",
                     }))
            {
                keys.Add(k);
            }
            var missing = keys.Where(k => !GameText.Has(k) || GameText.ContainsMarker(GameText.Get(k, GameLanguage.En))
                                          || (GameText.Get(k, GameLanguage.En) == GameText.Get(k, GameLanguage.ZhCn) && k != "exposure.panel.help")).ToList();
            Expect(keys.Count >= 40 && missing.Count == 0,
                $"代码用到的 {keys.Count} 个 FG1-SIG-06 文本键都在 fg.TbLocText、中英各有译文{(missing.Count == 0 ? string.Empty : "——缺：" + string.Join(",", missing))}");
        }

        // ── 世界与机器 ────────────────────────────────────────────────────────────

        private static CampaignState NewHome(int seed)
        {
            ResetWorld();
            CampaignState s = CampaignState.CreateNew("fgsig06-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            foreach (string type in new[] { HomeValleyLayout.BuildingTypeGenerator, HomeValleyLayout.BuildingTypeAssemblyStation })
            {
                BuildingRecord r = s.BuildingRecords.FirstOrDefault(x => x.BuildingTypeId == type);
                if (r != null)
                {
                    r.ConstructionState = BuildingConstructionState.Operational;
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            AddBlueprints(s);
            return s;
        }

        private static CampaignState NewState(int seed)
        {
            ResetWorld();
            CampaignState s = CampaignState.CreateNew("fgsig06-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            s.BuildingRecords = new[]
            {
                new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeAssemblyStation,
                    BuildingTypeId = HomeValleyLayout.BuildingTypeAssemblyStation,
                    RegionId = HomeValleyLayout.RegionId,
                    ConstructionState = BuildingConstructionState.Operational,
                    PowerState = BuildingPowerState.Powered,
                },
                new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeAnalysisBench,
                    BuildingTypeId = HomeValleyLayout.BuildingTypeAnalysisBench,
                    RegionId = HomeValleyLayout.RegionId,
                    ConstructionState = BuildingConstructionState.Operational,
                    PowerState = BuildingPowerState.Powered,
                },
            };
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            AddBlueprints(s);
            return s;
        }

        private static void ResetWorld()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            InputRouter.DebugSetReader(Keys);
            Keys.Down = KeyCode.None;
            Keys.Held = KeyCode.None;
            UiEscapeStack.Clear();
            SignalUplinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
        }

        private static void AddBlueprints(CampaignState s)
        {
            BlueprintCircuitBoard cannon = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            ExpectSetup(cannon.TrySetUplink(2), "重炮蓝图 2 号格标接入口");
            AddBlueprint(s, BpCannonUp, cannon);
            AddBlueprint(s, BpGun, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()));
            BlueprintCircuitBoard mark = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, ComponentCatalog.FuncMarkerId, null,
                Array.Empty<string>());
            ExpectSetup(mark.TrySetUplink(1), "标记蓝图 1 号格标接入口");
            AddBlueprint(s, BpMarkUp, mark);
        }

        private static void ExpectSetup(CircuitOpResult r, string what)
        {
            if (!r.Success)
            {
                Fail($"测试准备：{what}失败：{r.Message}");
            }
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        private static int SpawnRegion(string region, string bp, Vector2 at, float hp = 400f)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, region, at, hp, hp, "Player", 1);
            if (!r.Success)
            {
                Fail($"测试准备：登记机器失败：{r.Message}");
            }
            return r.LogicId;
        }

        private static FracturedCityController OpenCity(CampaignState s, params int[] ids)
        {
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            FracturedCityRegion.Find(s).State = RegionState.Available;
            return WorldSimulation.LoadFracturedCity(ids, resume: false);
        }

        private static FoundryOutpostController OpenFoundry(CampaignState s, params int[] ids)
        {
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            FoundryOutpostRegion.Find(s).State = RegionState.Available;
            return WorldSimulation.LoadFoundryOutpost(ids, resume: false);
        }

        private static RegionEnemyRecord PrepArmorTarget(CampaignState s, CombatSite site, string id)
        {
            FoundryOutpostRegion.TryDamageEnemy(s, FoundryOutpostLayout.RepairBotSpawnId, 1e6f);
            RegionEnemyRecord armor = Enemy(s, id);
            armor.MaxHealth = 1e6f;
            armor.Health = 1e6f;
            site.SyncEnemyFromRecord(armor);
            WorldSimulation.StepMany(1);
            return armor;
        }

        /// <summary>带回一件敌方加密物品（Recovered），经正式发放入口得到未破解固件芯片；返回芯片实例 ID。</summary>
        private static string GrantRaw(CampaignState s, string region, string questContentId, string salvageId)
        {
            s.RegionQuestItems = (s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()).Where(q => q.SalvageInstanceId != salvageId)
                .Append(new RegionQuestItemRecord { SalvageInstanceId = salvageId, RegionId = region, ContentId = questContentId, State = RegionQuestItemState.Recovered })
                .ToArray();
            RawFirmwareService.GrantRecovered(s);
            string part = s.PrimitiveChips?.FirstOrDefault(c => c.SourceSalvageId == salvageId)?.PartId;
            if (part == null)
            {
                Fail($"测试准备：{questContentId} 没有发放未破解固件");
            }
            return part;
        }

        private static SignalCoreResult Equip(CampaignState s, string partId, int slot)
        {
            Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
            SignalCoreResult r = SignalCoreService.TryEquip(s, partId, slot);
            SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
            if (!r.Success)
            {
                Fail($"测试准备：{partId} 装入 {slot + 1} 号槽失败：{r.Message}");
            }
            return r;
        }

        private static void Unlock(CampaignState s, string contentId)
        {
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(contentId).Distinct().ToArray();
        }

        /// <summary>送解析台（真实队列：入队 → 有电开工 → 完成），返回是否完成。</summary>
        private static bool CrackViaBench(CampaignState s, string salvageId)
        {
            HomeValleyAnalysis.AnalysisOpResult r = HomeValleyAnalysis.TryEnqueue(s, salvageId);
            if (!r.Success)
            {
                Fail($"测试准备：送解析台失败：{r.FailureReason}");
                return false;
            }
            for (int i = 0; i < 20; i++)
            {
                HomeValleyAnalysis.Tick(s, 2f);
                if (HomeValleyAnalysis.Find(s, r.QueueItemId)?.State == AnalysisQueueState.Completed)
                {
                    return true;
                }
            }
            return false;
        }

        private static void CommitVia(int logicId)
        {
            if (SignalPresence.CurrentMachineLogicId == logicId)
            {
                return;
            }
            UplinkRequestResult r = SignalUplinkService.Request(logicId, UplinkSource.MachineList);
            if (!r.Accepted)
            {
                Fail($"测试准备：接入 #{logicId} 被拒（{r.Failure}：{r.Text}）");
                return;
            }
            int n = 0;
            while (SignalUplinkService.IsPending && n++ < 40)
            {
                Frames(1);
            }
            if (SignalPresence.CurrentMachineLogicId != logicId)
            {
                Fail($"测试准备：接入 #{logicId} 没有完成（信号在 {SignalPresence.CurrentMachineLogicId}，最后反馈“{SignalUplinkService.LastFeedbackText}”）");
            }
        }

        /// <summary>重炮两段式开火（直控点击的正式入口 CombatSite.TryFireAtEnemy）。返回是否真的开了火。</summary>
        private static bool FireCannon(CombatSite site, int logicId, string enemyId)
        {
            double next0 = NextFire(site, logicId);
            int guard = 0;
            while (guard++ < 400)
            {
                site.TryFireAtEnemy(logicId, enemyId, out CombatFireResult res, out _);
                if (NextFire(site, logicId) > next0)
                {
                    break;
                }
                if (res != CombatFireResult.StillAiming && res != CombatFireResult.Cooldown && res != CombatFireResult.Overheated)
                {
                    return false;
                }
                WorldSimulation.StepMany(1);
            }
            return NextFire(site, logicId) > next0;
        }

        private static void SaveNow()
        {
            WorldSimulation.SyncAllForSave();
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("存档写入失败：" + r.Message);
            }
        }

        private static CampaignState LoadLikeMenu()
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            SignalUplinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            RestoreResult r = CampaignRestoreOrchestrator.Restore(Slot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(Slot, r.State);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            SignalUplinkService.RestoreAfterLoad();
            return r.State;
        }

        private static void Frames(int n)
        {
            for (int i = 0; i < n; i++)
            {
                InputRouter.DebugClearConsumedKeys();
                _fakeNow += FrameDt;
                WorldSimulation.Frame(FrameDt);
            }
        }

        private static CombatWeapon WeaponOf(CombatSite site, int logicId) =>
            site.TryGetMachineWeapon(logicId, out MachineWeaponInfo info) && info.WeaponIndex >= 0 && site.Kernel.TryGetWeapon(info.WeaponIndex, out CombatWeapon w)
                ? w : default;

        private static CombatUnitView Unit(CombatSite site, int logicId) =>
            site.TryGetMachineUnit(logicId, out int unit) && site.Kernel.TryGetUnit(unit, out CombatUnitView v) ? v : default;

        private static float Heat(CombatSite site, int logicId) => Unit(site, logicId).Heat;

        private static double NextFire(CombatSite site, int logicId) => Unit(site, logicId).NextFireAt;

        private static void SetHeat(CombatSite site, int logicId, float heat)
        {
            if (site.TryGetMachineUnit(logicId, out int unit) && site.Kernel.TryGetUnit(unit, out CombatUnitView v))
            {
                site.Kernel.SetWeaponState(unit, heat, false, v.AimReadyAt, v.NextFireAt);
            }
        }

        private static void Place(CombatSite site, int logicId, Vector2 at)
        {
            if (site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new double2(at.x, at.y));
            }
        }

        private static void PlaceEnemy(CombatSite site, string enemyId, Vector2 at)
        {
            if (site.TryGetEnemyUnit(enemyId, out int unit))
            {
                site.Kernel.SetPosition(unit, new double2(at.x, at.y));
            }
        }

        private static void SetInstance(SignalCoreHudUIToolkit hud)
        {
            typeof(SignalCoreHudUIToolkit).GetProperty("Instance")?.GetSetMethod(true)?.Invoke(null, new object[] { hud });
        }

        private static RegionEnemyRecord Enemy(CampaignState s, string id) => s.RegionEnemies.FirstOrDefault(e => e.EnemyInstanceId == id);

        private static string ShortId(string partId) => string.IsNullOrEmpty(partId) ? "-" : partId.Substring(Math.Max(0, partId.Length - 4));

        private static VisualElement Mount(out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgRawExposureSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        /// <summary>点一个 UI Toolkit 按钮：走按钮自己的 Clickable（与鼠标点击同一回调）。</summary>
        private static void Click(Button b)
        {
            if (b?.clickable == null)
            {
                Fail($"按钮 {b?.name ?? "（空）"} 没有 Clickable");
                return;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
        }

        private sealed class Reader : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public KeyCode Held = KeyCode.None;
            public bool GetKey(KeyCode key) => key == Down || key == Held;
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

        // ── 报告 ──────────────────────────────────────────────────────────────────

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
