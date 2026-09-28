using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// FG1-SIG-03 接入、重编译、离开与防刷的自动验收（FG01 FGR-SIG-030～034；第 5 章负向矩阵；第 6 章存档；FGT-SIG-001、004、009）。
    /// 全部起真实系统：整个世界（家园 / 破碎都市 / 铸造前哨控制器 + 战斗内核 + 统一时钟 + 全局镜头）、真输入路由（接入键、Tab、Esc、空格）、
    /// 真 UXML HUD、真实存档文件——行为坏了会失败：
    /// A 数据与输入；B 正式输入完成接入与离开（FGT-SIG-001）；C 失败原因逐条（FGR-SIG-030）；D 负向矩阵（过渡中阵亡、Esc、战略暂停中发起、
    /// 1 秒 10 次、没有接入口、信号核为空、镜头飞走、地点卸载）；E 冷却跟着信号、热量留在机体（FGT-SIG-004，三台重炮真开火）；
    /// E2 提示事件打满时冷却照样开始、内核当场压住门控反应；F 防刷（FGR-SIG-034）；G 暂停与倍速；
    /// H 存读档（FGT-SIG-009 本 Story 场景：真实文件、过渡中存档、旧档、篡改）；I 性能；J 界面；K 命令栏机器列表（三个地点）与按钮说明。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgSignalUplinkSelfCheck
    {
        private const string UxmlPath = "Assets/GameRes/Raw/UI/UiKit/SignalCorePanel.uxml";
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 0;
        private const string BpCannonUp = "bp_selfcheck_sig03_cannon_up";
        private const string BpGunPlain = "bp_selfcheck_sig03_gun";
        private const float FrameDt = 0.05f;

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;
        private static readonly Reader Keys = new Reader();
        private static readonly List<SignalUplinkChange> Changes = new List<SignalUplinkChange>();
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/自检：FG 接入、重编译、离开与防刷")]
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
            Line("\n[接入] 接入、重编译、离开与防刷（FG1-SIG-03）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsig03-selfcheck-" + Guid.NewGuid().ToString("N"));
            Action<SignalUplinkChange> onChange = c => Changes.Add(c);
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.Reload();
                GameClock.ReloadTuning();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => FrameDt;
                NotificationCenter.AutoPauseHandler = null;
                SignalUplinkService.RealTimeForTests = () => _fakeNow;
                GameRoot.BindWorldProviders();
                TEngine.GameEvent.AddEventListener(SignalUplinkService.UplinkChangedEvent, onChange);
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "热更层在 Editor 下是 Mono JIT，真机走 HybridCLR 解释执行（数字只作量级参考，真机复测归 FG15-SYS-02）");

                Step(CheckData);
                Step(CheckFormalInput);
                Step(CheckFailureReasons);
                Step(CheckNegativeHome);
                Step(CheckNegativeRegions);
                Step(CheckCooldownFollowsSignal);
                Step(CheckCueFlood);
                Step(CheckAntiFarm);
                Step(CheckPauseSpeed);
                Step(CheckSaveLoad);
                Step(CheckPerformance);
                Step(CheckUi);
                Step(CheckCommandBar);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"接入自检抛异常：{e}");
            }
            finally
            {
                TEngine.GameEvent.RemoveEventListener(SignalUplinkService.UplinkChangedEvent, onChange);
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
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
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
            Line($"  · [接入] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A. 数据与输入 ────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据与输入：过渡秒数与核心固件冷却读表、代码用到的文本键中英齐全、接入 / 退出 / 循环切换快捷键在动作表里且可重绑");
            bool fromTable = GridContent.TryGetTuning("signal.uplink.transition_seconds", out float t);
            Expect(fromTable && Mathf.Approximately(t, 0.35f) && Mathf.Approximately(SignalUplinkService.TransitionSeconds, 0.35f),
                $"接入过渡读 fg.TbHomeTuning signal.uplink.transition_seconds = {t}（FGR-SIG-031 沿用 0.35 秒）");
            Expect(Mathf.Approximately(CameraDirector.TransitionSeconds, SignalUplinkService.TransitionSeconds) && Mathf.Approximately(CameraDirector.TransitionSeconds, t),
                $"镜头视角过渡读同一行调参（{CameraDirector.TransitionSeconds} 秒 = 接入提交 {SignalUplinkService.TransitionSeconds} 秒）：调表后两边不会分叉");

            var rows = FirmwareKinds.Rows.ToDictionary(r => r.Id, r => r.Cooldown);
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Core });
            float coreCd = FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwOverloadId);
            float regularCd = FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwHomingId);
            FirmwareKinds.ResetForTests();
            float prodCd = FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwOverloadId);
            Expect(rows.Count >= 6 && rows.TryGetValue(FirmwareCatalog.FwOverloadId, out float o) && Mathf.Approximately(o, 8f)
                   && rows.TryGetValue(FirmwareCatalog.FwMarkTagId, out float m) && Mathf.Approximately(m, 8f)
                   && Mathf.Approximately(coreCd, 8f) && regularCd == 0f && Mathf.Approximately(prodCd, 8f),
                $"固件种类表 cooldown 列：过载 / 标记跳转 8 秒；注入为核心时读到 {coreCd} 秒；FG1-SIG-05 起正式表里过载就是核心（{prodCd} 秒）；常规固件冷却为 0");

            string logic = Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic");
            string[] files = { "Campaign/Signal/SignalUplinkService.cs", "UI/RegionCommand/RegionCommandBarUIToolkit.cs", "UI/SignalCore/SignalCoreHudUIToolkit.cs" };
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                foreach (Match mt in Regex.Matches(File.ReadAllText(Path.Combine(logic, f)), "\"(signal\\.uplink\\.[a-z_.]+)\""))
                {
                    if (!GridContent.TryGetTuning(mt.Groups[1].Value, out _))
                    {
                        keys.Add(mt.Groups[1].Value); // 调参 ID（signal.uplink.transition_seconds）不是文本键。
                    }
                }
            }
            var missing = keys.Where(k => !GameText.Has(k) || GameText.ContainsMarker(GameText.Get(k, GameLanguage.En))
                                          || GameText.Get(k, GameLanguage.En) == GameText.Get(k, GameLanguage.ZhCn)).ToList();
            Expect(keys.Count >= 30 && missing.Count == 0,
                $"代码用到的 {keys.Count} 个 signal.uplink.* 文本键都在 fg.TbLocText 里、中英各有译文{(missing.Count == 0 ? string.Empty : "——缺：" + string.Join(",", missing))}");

            bool actions = true;
            var detail = new List<string>();
            foreach (GameActionId a in new[] { GameActionId.ToggleCameraView, GameActionId.CycleControlTarget })
            {
                bool ok = InputActionCatalog.TryGet(a, out InputActionDef def) && def.Status == InputActionStatus.Wired
                          && (def.Contexts & InputContext.Uplink) != 0 && InputActionCatalog.DefaultChord(a).IsBound;
                actions &= ok;
                detail.Add($"{a}={(ok ? InputActionCatalog.DefaultChord(a).Key.ToString() : "✗")}");
            }
            Expect(actions, $"动作表：接入 / 退出接入、循环切换机器都已接入玩法、属于接入上下文（{string.Join("，", detail)}）");
        }

        // ── B. 正式输入完成接入与离开（FGT-SIG-001）──────────────────────────────

        private static void CheckFormalInput()
        {
            Line("  · B. FGT-SIG-001：家园里选中机器 → 按接入键 → 0.35 秒过渡（信号仍在原处）→ 插入过载、重编译、形变事件、HUD → 再按一次离开 → 全部复原");
            CampaignState s = NewHome(8301);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int g = SpawnHome(BpGunPlain, new Vector2(6f, -4f));
            WorldSimulation.StepMany(2);
            HomeValleyController home = WorldSimulation.Home;
            CombatSite site = home.Combat;
            EquipOverload(s);
            VisualElement root = Mount(out GameObject go);
            SignalCoreHudUIToolkit hud = go.AddComponent<SignalCoreHudUIToolkit>();
            try
            {
                hud.BindView(root);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
                int aiIndex = WeaponIndexOf(site, a);
                CombatWeapon aiWeapon = WeaponOf(site, a);
                Expect(aiIndex >= 0 && aiWeapon.Mode == CombatWeaponMode.Cannon && aiWeapon.Reaction == CombatReaction.None,
                    $"接入前：机器 #{a}（重炮 + 接入口）AI 驾驶的武器没有反应（接入口按空槽，内核武器行 {aiIndex}）");

                Changes.Clear();
                Expect(home.TrySelectMachine(a), "左键选中的同一入口选中机器");
                Press(Key(GameActionId.ToggleCameraView));
                bool pending = SignalUplinkService.IsPending && SignalUplinkService.PendingTargetLogicId == a && SignalPresence.AtCore
                               && WorldView.Director.HeadingDirect && home.PossessedMachineLogicId == null && WeaponIndexOf(site, a) == aiIndex;
                hud.Refresh();
                string pendingText = hud.UplinkStatusText;
                Frames(3);
                bool stillPending = SignalUplinkService.IsPending && SignalPresence.AtCore;
                Frames(6);
                CombatWeapon upWeapon = WeaponOf(site, a);
                site.TryGetMachineWeapon(a, out MachineWeaponInfo upInfo);
                MachineCombatResolution res = MachineLoadoutRegistry.ResolveForPilot(s, a, s.RandomSeed);
                bool unitUsesIt = site.TryGetMachineUnit(a, out int ua) && site.Kernel.TryGetUnit(ua, out CombatUnitView va) && va.Weapon == upInfo.WeaponIndex;
                Expect(pending && stillPending && pendingText.Contains(SignalPresence.MachineLabel(a)),
                    $"按接入键：进入过渡（目标 #{a}），过渡中信号仍在归还核心、机器还是 AI 配置、镜头正飞向它；HUD“{pendingText}”");
                Expect(!SignalUplinkService.IsPending && SignalPresence.CurrentMachineLogicId == a && home.PossessedMachineLogicId == a
                       && WorldView.Director.Mode == ViewMode.Direct && s.SignalCore.UplinkMachineLogicId == a && s.SignalCore.UplinkSiteId == HomeValleyLayout.RegionId,
                    "0.35 秒后接入完成：信号在这台机器里（存档字段同步）、镜头直控跟随");
                Expect(upInfo.Uplinked && upWeapon.Reaction == CombatReaction.MeltOverload && upInfo.WeaponIndex != aiIndex && unitUsesIt
                       && res.Success && res.Preview.UplinkFirmwareIds.SequenceEqual(new[] { FirmwareCatalog.FwOverloadId }),
                    $"插入固件并重编译：接入口插入过载、战斗内核里这台机器换成“熔穿过载”武器（行 {aiIndex} → {upInfo.WeaponIndex}）");
                Expect(GuidanceHooks.Known.Contains(GuidanceHooks.SignalFirstUplink) && GameSettings.HasSeenGuidanceHook(GuidanceHooks.SignalFirstUplink),
                    $"第一次接入完成发出引导钩子 {GuidanceHooks.SignalFirstUplink}（已登记，引导内容在 FG15-UX-04）");
                SignalUplinkChange entered = Changes.LastOrDefault();
                Expect(Changes.Count == 1 && entered.CurrentLogicId == a && entered.PreviousLogicId == 0 && entered.MorphActive
                       && entered.InsertedFirmwareIds.Contains(FirmwareCatalog.FwOverloadId),
                    "形变事件：发出一次“信号到了 #a、插入过载、该变形”（FG1-VFX-01 订阅）");
                AdvanceRealTime(4f);
                hud.Refresh();
                string status = hud.UplinkStatusText;
                Expect(hud.LocationText == GameText.Format("signal.hud.in_machine", SignalPresence.MachineLabel(a))
                       && status.Contains(GameText.Get("firmware.fw_overload.name")) && !GameText.ContainsMarker(status),
                    $"HUD：“{hud.LocationText}”，状态行“{status}”");

                Press(Key(GameActionId.ToggleCameraView));
                Frames(10);
                CombatWeapon back = WeaponOf(site, a);
                site.TryGetMachineWeapon(a, out MachineWeaponInfo backInfo);
                SignalUplinkChange left = Changes.LastOrDefault();
                hud.Refresh();
                Expect(SignalPresence.AtCore && home.PossessedMachineLogicId == null && WorldView.Director.Mode == ViewMode.Strategy
                       && s.SignalCore.UplinkMachineLogicId == 0 && s.SignalCore.UplinkSiteId.Length == 0,
                    "再按接入 / 退出键：镜头拉回战略，信号回到归还核心（存档字段清零）");
                Expect(!backInfo.Uplinked && backInfo.WeaponIndex == aiIndex && back.Reaction == CombatReaction.None
                       && Changes.Count == 2 && left.PreviousLogicId == a && left.CurrentLogicId == 0 && !left.MorphActive,
                    $"离开：机器重编译回本地配置（武器行回到 {backInfo.WeaponIndex}，与接入前相同），形变复原事件");
                Expect(hud.LocationText == GameText.Get("signal.hud.at_core") && hud.UplinkStatusText.Contains(SignalPresence.MachineLabel(a)),
                    $"HUD：“{hud.LocationText}”，状态行“{hud.UplinkStatusText}”");

                // 过渡中冻结冲突输入（Direct → Direct 的 Tab 切换）：按住前进键，原机器在过渡期间不动。
                Press(Key(GameActionId.ToggleCameraView)); // 仍选中 a
                Frames(10);
                Vector2 before = MachinePos(site, a);
                Keys.Held.Add(Key(GameActionId.MoveForward));
                Keys.Held.Add(Key(GameActionId.Interact));
                Press(Key(GameActionId.CycleControlTarget));
                int tabTarget = SignalUplinkService.PendingTargetLogicId;
                Frames(3);
                Vector2 during = MachinePos(site, a);
                bool interactFrozen = SignalUplinkService.IsPending && home.PossessedMachineLogicId == a
                                      && home.Interact.LastFailure == RegionInteractFailure.UplinkTransition
                                      && home.Interact.PrimaryCandidate == null && home.Interact.Progress01 == 0f;
                bool markerCached = SignalUplinkService.PendingMarkerCached;
                Keys.Held.Remove(Key(GameActionId.Interact));
                Frames(8);
                Keys.Held.Clear();
                Expect(tabTarget != 0 && tabTarget != a && (during - before).sqrMagnitude < 1e-6f && SignalPresence.CurrentMachineLogicId == tabTarget,
                    $"接入中按 Tab：过渡到下一台 #{tabTarget}，过渡期间按住前进键原机器也不动（冻结冲突输入），之后信号在新机器里");
                Expect(interactFrozen && home.Interact.LastFailure != RegionInteractFailure.UplinkTransition,
                    $"过渡期间按住交互键：旧机器 #{a} 的交互让位（{RegionInteractFailure.UplinkTransition}，没有候选、进度 0，不能发起 / 推进 / 完成拆解装载）；过渡结束后恢复");
                Expect(markerCached, "过渡中每帧复核目标用发起时记下的表现对象（O(1)），不再每帧线性查本地点机器列表");
                site.TryGetMachineWeapon(a, out MachineWeaponInfo aAfterTab);
                Expect(!aAfterTab.Uplinked && aAfterTab.WeaponIndex == aiIndex, "Tab 离开的机器回到本地配置");
                Press(Key(GameActionId.ToggleCameraView));
                Frames(10);

                // 改绑“接入 / 退出接入”（B02 所有快捷键可重绑）：新键能接入、离开，旧键不再生效；恢复默认。
                RebindResult rb = RebindResult.Invalid;
                KeyCode freeKey = KeyCode.None;
                foreach (KeyCode k in new[] { KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Semicolon, KeyCode.Quote })
                {
                    rb = GameSettings.KeyBindings.TryRebind(GameActionId.ToggleCameraView, new InputChord(k), new List<GameActionId>());
                    if (rb == RebindResult.Ok)
                    {
                        freeKey = k;
                        break;
                    }
                }
                home.TrySelectMachine(a);
                Press(KeyCode.V);
                bool oldKeyIgnored = !SignalUplinkService.IsPending && SignalPresence.AtCore;
                Press(freeKey);
                Frames(10);
                bool newKeyUplinks = SignalPresence.CurrentMachineLogicId == a;
                Press(freeKey);
                Frames(10);
                bool newKeyLeaves = SignalPresence.AtCore;
                GameSettings.KeyBindings.ResetToDefault(GameActionId.ToggleCameraView);
                Expect(freeKey != KeyCode.None && oldKeyIgnored && newKeyUplinks && newKeyLeaves && Key(GameActionId.ToggleCameraView) == KeyCode.V,
                    $"改绑接入 / 退出键到 {freeKey}（{rb}）：旧键 V 不再触发，新键接入、再按离开；恢复默认回到 V");
            }
            finally
            {
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                Object.DestroyImmediate(go);
                Keys.Held.Clear();
            }
        }

        // ── C. 失败原因逐条（FGR-SIG-030）─────────────────────────────────────────

        private static void CheckFailureReasons()
        {
            Line("  · C. FGR-SIG-030 接入失败逐条原因：阵亡、不在当前地点、超出覆盖、被干扰、静默夜、在装配站、维修台、投送途中、面板挡着、没选中、已在里面、过渡中、没有可切换的");
            CampaignState s = NewHome(8302);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int d = SpawnHome(BpGunPlain, new Vector2(6f, -4f));
            int f = SpawnHome(BpGunPlain, new Vector2(8f, -4f));
            MachineOpResult away = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, BpGunPlain, FracturedCityLayout.RegionId, new Vector2(0f, -24f), 120f, 120f);
            WorldSimulation.StepMany(2);
            int deniedBefore = FeedbackCues.CountOf(FeedbackCueId.Denied);
            var texts = new Dictionary<UplinkFailure, string>();

            UplinkRequestResult R(int id) => SignalUplinkService.Request(id, UplinkSource.MachineList, startCamera: false);
            void Record(UplinkRequestResult r)
            {
                if (!r.Accepted)
                {
                    texts[r.Failure] = r.Text;
                }
            }

            MachineRegistry.ApplyDamage(d, 99999f);
            UplinkRequestResult dead = R(d);
            Record(dead);
            UplinkRequestResult other = R(away.LogicId);
            Record(other);
            SignalUplinkService.CoverageProvider = id => id != a;
            UplinkRequestResult coverage = R(a);
            Record(coverage);
            SignalUplinkService.CoverageProvider = null;
            SignalUplinkService.SilentNightProvider = () => true;
            UplinkRequestResult night = R(a);
            Record(night);
            SignalUplinkService.SilentNightProvider = null;
            MachineRegistry.TryGetRecord(f, out MachineRecord fr);
            fr.IsInFactory = true;
            UplinkRequestResult factory = R(f);
            Record(factory);
            fr.IsInFactory = false;
            SignalUplinkService.OnRepairBayProvider = id => id == a;
            UplinkRequestResult repair = R(a);
            Record(repair);
            SignalUplinkService.OnRepairBayProvider = null;
            SignalUplinkService.InDeliveryProvider = id => id == a;
            UplinkRequestResult delivery = R(a);
            Record(delivery);
            SignalUplinkService.InDeliveryProvider = null;
            object modalOwner = new object();
            InputRouter.PushModal(modalOwner);
            UplinkRequestResult modal = R(a);
            Record(modal);
            InputRouter.PopModal(modalOwner);
            bool noSelection = SignalUplinkService.RequestFromSelection(0);
            texts[UplinkFailure.NoSelection] = SignalUplinkService.LastFeedbackText;
            UplinkFailure noSelectionCode = SignalUplinkService.LastFailure;
            int deniedAfterLoud = FeedbackCues.CountOf(FeedbackCueId.Denied);

            Expect(!dead.Accepted && dead.Failure == UplinkFailure.Dead && dead.Text.Contains(SignalPresence.MachineLabel(d))
                   && other.Failure == UplinkFailure.OtherSite && coverage.Failure == UplinkFailure.OutOfCoverage && night.Failure == UplinkFailure.SilentNight
                   && factory.Failure == UplinkFailure.InFactory && repair.Failure == UplinkFailure.OnRepairBay && delivery.Failure == UplinkFailure.InDelivery
                   && modal.Failure == UplinkFailure.ModalBlocked && !noSelection && noSelectionCode == UplinkFailure.NoSelection
                   && texts[UplinkFailure.NoSelection].Contains(InputDisplay.ForAction(GameActionId.ToggleCameraView)),
                $"逐条拒绝：阵亡“{dead.Text}”、所在地点没在运行“{other.Text}”（FG1-SIG-07 起跨地点接入 = 远距离跳转，见 [覆盖网络] 段）、超出覆盖“{coverage.Text}”、静默夜“{night.Text}”、在装配站“{factory.Text}”、" +
                $"维修台“{repair.Text}”、投送途中“{delivery.Text}”、面板挡着“{modal.Text}”、没选中“{texts[UplinkFailure.NoSelection]}”");
            Expect(texts.Values.Distinct().Count() == texts.Count && texts.Values.All(x => x.Length > 0 && !GameText.ContainsMarker(x))
                   && deniedAfterLoud - deniedBefore == 9 && !SignalUplinkService.IsPending && SignalPresence.AtCore,
                $"{texts.Count} 条原因文本各不相同、都走文本键；每条拒绝都有拒绝音 + 字幕（{deniedAfterLoud - deniedBefore} 次）；拒绝后状态不变");

            // 已在里面 / 过渡中 / 没有可切换的：无害的重复操作，只写 HUD 原因、不出拒绝音。
            CommitVia(a);
            int deniedQuiet0 = FeedbackCues.CountOf(FeedbackCueId.Denied);
            UplinkRequestResult already = R(a);
            UplinkRequestResult start = R(f);
            UplinkRequestResult busy = R(a);
            SignalUplinkService.CancelByPlayer();
            Expect(already.Failure == UplinkFailure.AlreadyUplinked && start.Accepted && busy.Failure == UplinkFailure.Busy
                   && FeedbackCues.CountOf(FeedbackCueId.Denied) == deniedQuiet0 && SignalPresence.CurrentMachineLogicId == a,
                $"已在里面“{already.Text}”、过渡中再点“{busy.Text}”：只提示不出拒绝音，信号仍在 #{a}");

            // 刚按退出键、镜头正拉回战略时点机器列表：明确拒绝（不是先接受、镜头落地时再被当成“玩家退出”静默取消；低帧率下也不会接进去又立刻离开）。
            Frames(10);
            Press(Key(GameActionId.ToggleCameraView));
            bool leaving = WorldView.Director.InTransition && WorldView.Director.TransitionTarget == ViewMode.Strategy;
            int commitsLeaving = SignalUplinkService.CommitCount;
            UplinkRequestResult leavingReq = SignalUplinkService.Request(f, UplinkSource.MachineList);
            Frames(10);
            Expect(leaving && !leavingReq.Accepted && leavingReq.Failure == UplinkFailure.ViewLeaving
                   && leavingReq.Text == GameText.Get("signal.uplink.reason.view_leaving") && !GameText.ContainsMarker(leavingReq.Text)
                   && !SignalUplinkService.IsPending && SignalPresence.AtCore && SignalUplinkService.CommitCount == commitsLeaving
                   && WorldView.Director.Mode == ViewMode.Strategy,
                $"刚按退出键、镜头正拉回战略时点机器列表：拒绝“{leavingReq.Text}”；镜头落地后信号在归还核心，没有接入又立刻离开");
            UplinkRequestResult landed = SignalUplinkService.Request(f, UplinkSource.MachineList);
            Frames(10);
            Expect(landed.Accepted && SignalPresence.CurrentMachineLogicId == f, $"镜头落地后再点同一台：正常接入 #{f}");

            // 被干扰（真实干扰场）与 Tab 跳过被干扰的机器；只有一台时“没有可切换的”。
            WorldSimulation.UnloadAll();
            CampaignState s2 = NewState(8303);
            EquipOverload(s2);
            int x = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(-6f, -24f));
            int jam = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(2f, -24f));
            int z = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(6f, -24f));
            FracturedCityController city = OpenCity(s2, x, jam, z);
            WorldView.Observe(city.SiteId);
            Place(city.Combat, jam, FracturedCityLayout.ListeningNode.Position + new Vector2(3f, 0f));
            UplinkRequestResult jammed = R(jam);
            CommitVia(x);
            UplinkRequestResult tab = SignalUplinkService.RequestCycle();
            int tabTarget = SignalUplinkService.PendingTargetLogicId;
            Frames(10);
            Expect(jammed.Failure == UplinkFailure.Jammed && jammed.Text.Contains(SignalPresence.MachineLabel(jam))
                   && tab.Accepted && tabTarget == z && SignalPresence.CurrentMachineLogicId == z,
                $"被干扰“{jammed.Text}”（读内核里的实时位置）；接入中 Tab 跳过被干扰的 #{jam}、切到 #{z}，不会卡在同一台上");
            MachineRegistry.ApplyDamage(x, 99999f);
            MachineRegistry.ApplyDamage(jam, 99999f);
            WorldSimulation.StepMany(2);
            UplinkRequestResult none = SignalUplinkService.RequestCycle();
            Expect(none.Failure == UplinkFailure.NoCandidate && none.Text == GameText.Get("signal.uplink.reason.no_candidate"),
                $"只剩一台时 Tab：“{none.Text}”");
        }

        // ── D. 负向矩阵（家园）───────────────────────────────────────────────────

        private static void CheckNegativeHome()
        {
            Line("  · D. 第 5 章负向矩阵（家园）：过渡中目标阵亡（从核心 / 从另一台机器）、Esc 取消、战略暂停中发起、1 秒内连按 10 次、没有接入口、信号核为空、镜头飞走");
            CampaignState s = NewHome(8304);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int b = SpawnHome(BpGunPlain, new Vector2(6f, -4f));
            int c = SpawnHome(BpCannonUp, new Vector2(8f, -4f));
            WorldSimulation.StepMany(2);
            HomeValleyController home = WorldSimulation.Home;
            CombatSite site = home.Combat;
            EquipOverload(s);

            // D1 过渡中目标阵亡（信号在核心）
            int commits0 = SignalUplinkService.CommitCount;
            home.TrySelectMachine(a);
            Press(Key(GameActionId.ToggleCameraView));
            Frames(2);
            MachineRegistry.ApplyDamage(a, 99999f);
            Frames(12);
            Expect(SignalUplinkService.LastCancel == UplinkCancelReason.TargetDead && SignalPresence.AtCore && SignalUplinkService.CommitCount == commits0
                   && home.PossessedMachineLogicId == null && WorldView.Director.Mode == ViewMode.Strategy
                   && SignalUplinkService.LastFeedbackText.Contains(SignalPresence.MachineLabel(a)),
                $"过渡中目标阵亡：取消接入，信号留在归还核心，镜头回到战略；原因“{SignalUplinkService.LastFeedbackText}”");

            // D1b 从另一台机器出发、过渡中目标阵亡：信号留在原来的机器里。
            CommitVia(b);
            SignalUplinkService.Request(c, UplinkSource.MachineList);
            Frames(2);
            MachineRegistry.ApplyDamage(c, 99999f);
            Frames(12);
            site.TryGetMachineWeapon(b, out MachineWeaponInfo bInfo);
            Expect(SignalUplinkService.LastCancel == UplinkCancelReason.TargetDead && SignalPresence.CurrentMachineLogicId == b
                   && home.PossessedMachineLogicId == b && bInfo.Uplinked,
                $"从 #{b} 切去 #{c} 的过渡中 #{c} 阵亡：取消，信号仍在 #{b}（仍按接入结算）");

            // D2 Esc 取消过渡（HUD 在取消栈里压一层；Esc 先取消接入，不弹暂停菜单）
            int d = SpawnHome(BpCannonUp, new Vector2(10f, -4f));
            WorldSimulation.StepMany(2);
            VisualElement root = Mount(out GameObject go);
            SignalCoreHudUIToolkit hud = go.AddComponent<SignalCoreHudUIToolkit>();
            try
            {
                hud.BindView(root);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
                SignalUplinkService.Request(d, UplinkSource.MachineList);
                hud.Refresh();
                bool layer = UiEscapeStack.Top != null;
                Keys.Down = Key(GameActionId.Cancel);
                InputRouter.DebugClearConsumedKeys();
                UiKitInputPump.Process();
                Keys.Down = KeyCode.None;
                Frames(10);
                hud.Refresh();
                Expect(layer && SignalUplinkService.LastCancel == UplinkCancelReason.PlayerCancelled && SignalPresence.CurrentMachineLogicId == b
                       && !PauseMenuUIToolkit.IsOpen && UiEscapeStack.Count == 0,
                    $"过渡中按 Esc：取消接入（“{SignalUplinkService.LastFeedbackText}”），信号仍在 #{b}，暂停菜单没弹出，取消栈清空");

                // D3 战略暂停中发起：先离开到战略，按暂停，选中后按接入键 → 目标已确认，恢复运行后才过渡、插入。
                Press(Key(GameActionId.ToggleCameraView));
                Frames(10);
                Press(Key(GameActionId.TogglePause));
                bool paused = GameClock.Paused;
                home.TrySelectMachine(d);
                Press(Key(GameActionId.ToggleCameraView));
                bool waiting = SignalUplinkService.PendingWaitsForResume && SignalUplinkService.PendingTargetLogicId == d;
                Frames(20);
                hud.Refresh();
                string pausedText = hud.UplinkStatusText;
                bool heldWhilePaused = SignalUplinkService.IsPending && SignalPresence.AtCore && WorldView.Director.Mode == ViewMode.Strategy;
                Press(Key(GameActionId.TogglePause));
                Frames(12);
                Expect(paused && waiting && heldWhilePaused && pausedText == GameText.Format("signal.uplink.pending_paused", SignalPresence.MachineLabel(d))
                       && !GameClock.Paused && SignalPresence.CurrentMachineLogicId == d && WorldView.Director.Mode == ViewMode.Direct,
                    $"战略暂停中按接入键：目标已确认、镜头不动、1 秒暂停期间不插入（HUD“{pausedText}”）；恢复运行后过渡完成、信号在 #{d}");
            }
            finally
            {
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                Object.DestroyImmediate(go);
            }

            // D4 1 秒内连按 10 次：状态一致、不重复插入、机体状态与对照机一致。
            Press(Key(GameActionId.ToggleCameraView));
            Frames(10);
            int e = SpawnHome(BpCannonUp, new Vector2(12f, -4f));
            WorldSimulation.StepMany(2);
            SetHeat(site, e, 30f);
            SetHeat(site, d, 30f);
            int weaponsBefore = site.Kernel.WeaponCount;
            int accepted0 = SignalUplinkService.AcceptedCount;
            int commit0 = SignalUplinkService.CommitCount;
            int leave0 = SignalUplinkService.LeaveCount;
            double nextD0 = NextFire(site, d);
            home.TrySelectMachine(d);
            for (int i = 0; i < 20; i++)
            {
                if (i % 2 == 0)
                {
                    Press(Key(GameActionId.ToggleCameraView));
                }
                else
                {
                    Frames(1);
                }
            }
            Frames(20);
            int where = SignalPresence.CurrentMachineLogicId;
            site.TryGetMachineWeapon(d, out MachineWeaponInfo dInfo);
            MachineCombatResolution dr = MachineLoadoutRegistry.ResolveForPilot(s, d, s.RandomSeed);
            int commits = SignalUplinkService.CommitCount - commit0;
            int leaves = SignalUplinkService.LeaveCount - leave0;
            Expect((where == 0 || where == d) && (home.PossessedMachineLogicId ?? 0) == where && dInfo.Uplinked == (where == d)
                   && dr.Preview.UplinkFirmwareIds.Distinct().Count() == dr.Preview.UplinkFirmwareIds.Length && dr.Preview.UplinkFirmwareIds.Length <= UplinkCompiler.Quota
                   && commits - leaves == (where == d ? 1 : 0) && SignalUplinkService.AcceptedCount - accepted0 <= 3
                   && site.Kernel.WeaponCount - weaponsBefore <= 0,
                $"1 秒内连按 10 次接入 / 退出：过渡中的按键被忽略（接受 {SignalUplinkService.AcceptedCount - accepted0} 次、接入 {commits} 次、离开 {leaves} 次），" +
                $"最后信号在 {(where == 0 ? "归还核心" : "#" + where)}，与接管状态、武器一致；接入口没有重复插入；武器表不增长（{weaponsBefore}→{site.Kernel.WeaponCount}）");
            float hd = Heat(site, d);
            float he = Heat(site, e);
            Expect(NextFire(site, d) == nextD0 && Mathf.Abs(hd - he) < 1e-4f && NextFire(site, d) == NextFire(site, e),
                $"连按不产生额外收益：没有开火，热量与从未被接入的对照机一样按机体散热（{hd:F2} = {he:F2}），武器冷却不变");
            if (SignalPresence.CurrentMachineLogicId != 0)
            {
                Press(Key(GameActionId.ToggleCameraView));
                Frames(10);
            }

            // D5 没有接入口的机器：照样接入，不插入，提示；D6 信号核为空。
            CommitVia(b);
            string noPort = SignalUplinkService.SteadyStatus(s, b);
            site.TryGetMachineWeapon(b, out MachineWeaponInfo bw);
            SignalUplinkChange lastB = Changes.LastOrDefault();
            MachineCombatResolution bRes = MachineLoadoutRegistry.ResolveForPilot(s, b, s.RandomSeed);
            MachineCombatResolution bAi = MachineLoadoutRegistry.ResolveForAi(s, b, s.RandomSeed);
            Expect(SignalPresence.CurrentMachineLogicId == b && noPort == GameText.Get("signal.uplink.status.no_uplink") && !lastB.MorphActive
                   && bRes.Preview.UplinkFirmwareIds.Length == 0 && Mathf.Approximately(bRes.Preview.TotalNormalizedDamage, bAi.Preview.TotalNormalizedDamage),
                $"没有接入口的机器：照样接入（可移动、瞄准、主动作），不插入固件、不变形；HUD“{noPort}”");
            SignalCoreService.TryUnequip(s, 0);
            CommitVia(e);
            string empty = SignalUplinkService.SteadyStatus(s, e);
            site.TryGetMachineWeapon(e, out MachineWeaponInfo cw);
            Expect(SignalPresence.CurrentMachineLogicId == e && empty.StartsWith(GameText.Get("signal.uplink.status.core_empty").Split('（')[0])
                   && cw.Uplinked && WeaponOf(site, e).Reaction == CombatReaction.None,
                $"信号核为空时接入带接入口的机器：正常接入，接入口为空；HUD“{empty}”");
            EquipOverload(s);
        }

        // ── D'. 负向（跨地点）：镜头飞走、地点卸载 ─────────────────────────────────

        private static void CheckNegativeRegions()
        {
            Line("  · D'. 镜头飞到别的地点 / 远征地点卸载时，信号回到归还核心；接入过渡跟着取消");
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Core });
            CampaignState s = NewHome(8306);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int r1 = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(-6f, -24f));
            int r2 = SpawnRegion(FracturedCityLayout.RegionId, BpGunPlain, new Vector2(6f, -24f));
            WorldSimulation.StepMany(2);
            EquipOverload(s);
            FracturedCityController city = OpenCity(s, r1, r2);
            WorldView.Observe(WorldSimulation.Home.SiteId);
            CommitVia(a);
            bool inHome = SignalPresence.CurrentMachineLogicId == a;
            SignalUplinkService.OnReactionFired(s, a, MechanicalReactionCatalog.ReactionMeltOverloadId);
            double fireAt = GameClock.GameSeconds;
            WorldView.Observe(city.SiteId);
            Expect(inHome && SignalPresence.AtCore && WorldSimulation.Home.PossessedMachineLogicId == null,
                "信号在家园机器里时镜头飞到远征地点：离开（被离开的机器按命令 / 教义继续），信号回到归还核心");
            int guard = 0;
            while (s.SignalCore.CoreCooldowns.Length > 0 && guard++ < 60 * 20)
            {
                WorldSimulation.StepMany(1);
            }
            double cdAway = SignalUplinkService.LastCooldownExpiryGameSeconds - fireAt;
            FirmwareKinds.ResetForTests();
            Expect(Math.Abs(cdAway - 8.0) <= GameClock.StepSeconds + 1e-6,
                $"镜头不在家园（看着远征地点）时，信号上的核心固件冷却照样按游戏时间到期（{cdAway:F3} 秒 = 表值 8 秒；FGR-BASE-021）");
            CommitVia(r1);
            SignalUplinkService.Request(r2, UplinkSource.MachineList);
            bool pend = SignalUplinkService.IsPending;
            city.Exit(evacuateSuccess: false);
            Frames(2);
            Expect(pend && !SignalUplinkService.IsPending && SignalPresence.AtCore && s.SignalCore.UplinkSiteId.Length == 0,
                "信号在远征机器里、正在切往另一台时远征地点卸载：过渡取消，信号回到归还核心");
        }

        // ── E. 冷却跟着信号、热量留在机体（FGT-SIG-004）───────────────────────────

        private static void CheckCooldownFollowsSignal()
        {
            Line("  · E. FGT-SIG-004：铸造前哨三台重炮（接入口），信号核装过载（注入为核心固件）：冷却记在信号上、换机器不重置，热量留在各自机体");
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Core });
            try
            {
                CampaignState s = NewState(8310);
                EquipOverload(s);
                int ma = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-2f, -20f), 900f);
                int mb = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(2f, -20f), 900f);
                int mc = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(6f, -20f), 900f);
                FoundryOutpostController fo = OpenFoundry(s, ma, mb, mc);
                WorldView.Observe(fo.SiteId);
                CombatSite site = fo.Combat;
                string target = FoundryOutpostLayout.ArmorBotLeftSpawnId;
                RegionEnemyRecord armor = Enemy(s, target);
                Vector2 front = armor.Position + FoundryOutpostRegion.ArmorFacingOf(armor) * 6f;
                Place(site, ma, front);
                Place(site, mb, front + new Vector2(0.6f, 0f));
                Place(site, mc, front + new Vector2(-0.6f, 0f));
                float cd = FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwOverloadId);

                CommitVia(ma);
                int fired0 = SignalUplinkService.CoreFiredCount;
                float ha0 = Heat(site, ma);
                bool firedA = FireCannon(site, ma, target, out bool meltA);
                double t0 = GameClock.GameSeconds;
                double rem0 = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);
                float ha1 = Heat(site, ma);
                site.TryGetMachineWeapon(ma, out MachineWeaponInfo aInfo);
                Expect(firedA && meltA && SignalUplinkService.CoreFiredCount == fired0 + 1 && Math.Abs(rem0 - cd) < 0.02
                       && Mathf.Abs(ha1 - ha0 - (FracturedCityLayout.CannonBaseHeatPerShot + FracturedCityLayout.OverloadExtraHeatPerShot)) < 0.01f
                       && aInfo.ReactionSuppressed && WeaponOf(site, ma).Reaction == CombatReaction.None,
                    $"#{ma} 接入后开火：打出熔穿过载（核心固件发动），冷却 {rem0:F2} 秒记在信号上；机体积热 {ha0:F1}→{ha1:F1}（40 + 过载 25）；冷却中武器暂不带反应");

                CommitVia(mb);
                double t1 = GameClock.GameSeconds;
                double rem1 = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);
                float haAfterLeave = Heat(site, ma);
                float expectA = Mathf.Max(0f, ha1 - FracturedCityLayout.WeaponHeatDissipationPerSecond * (float)(t1 - t0));
                site.TryGetMachineWeapon(mb, out MachineWeaponInfo bInfo);
                site.TryGetMachineWeapon(ma, out MachineWeaponInfo aLeft);
                Expect(rem1 > 0 && Math.Abs((rem0 - rem1) - (t1 - t0)) < 0.02 && Mathf.Abs(haAfterLeave - expectA) < 0.05f && Heat(site, mb) < 1e-4f
                       && bInfo.Uplinked && bInfo.ReactionSuppressed && WeaponOf(site, mb).Reaction == CombatReaction.None && !aLeft.Uplinked,
                    $"跳到 #{mb}：冷却没有重置（{rem0:F2} → {rem1:F2}，只走了 {t1 - t0:F2} 游戏秒）；#{ma} 的热量留在它自己身上按散热下降（{haAfterLeave:F1}，期望 {expectA:F1}），#{mb} 仍是 0；#{mb} 的武器暂不带反应");

                float hb0 = Heat(site, mb);
                int melt0 = FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload);
                bool firedB = FireCannon(site, mb, target, out bool meltB);
                float hb1 = Heat(site, mb);
                Expect(firedB && !meltB && SignalUplinkService.CoreFiredCount == fired0 + 1 && FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload) == melt0
                       && Mathf.Abs(hb1 - hb0 - FracturedCityLayout.CannonBaseHeatPerShot) < 0.01f
                       && SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId) > 0,
                    $"冷却中 #{mb} 开火：普通重炮（没有熔穿过载、只积热 40：{hb0:F1}→{hb1:F1}），冷却不因开火重置");

                int guard = 0;
                while (s.SignalCore.CoreCooldowns.Length > 0 && guard++ < 60 * 20)
                {
                    WorldSimulation.StepMany(1);
                }
                double expired = SignalUplinkService.LastCooldownExpiryGameSeconds;
                site.TryGetMachineWeapon(mb, out MachineWeaponInfo bReady);
                Expect(Math.Abs(expired - (t0 + cd)) <= GameClock.StepSeconds + 1e-6 && s.SignalCore.CoreCooldowns.Length == 0
                       && !bReady.ReactionSuppressed && WeaponOf(site, mb).Reaction == CombatReaction.MeltOverload,
                    $"冷却按游戏时间到期（发动后 {expired - t0:F2} 秒，表值 {cd} 秒）：信号所在的 #{mb} 自动重新带上熔穿过载");

                CommitVia(mc);
                float hc0 = Heat(site, mc);
                float hbBefore = Heat(site, mb);
                bool firedC = FireCannon(site, mc, target, out bool meltC);
                Expect(firedC && meltC && SignalUplinkService.CoreFiredCount == fired0 + 2
                       && Mathf.Abs(Heat(site, mc) - hc0 - (FracturedCityLayout.CannonBaseHeatPerShot + FracturedCityLayout.OverloadExtraHeatPerShot)) < 0.01f
                       && Heat(site, mb) <= hbBefore + 1e-4f,
                    $"第三台 #{mc}：冷却结束后发动熔穿过载，热量只加在 #{mc} 身上（{hc0:F1}→{Heat(site, mc):F1}），#{mb} 的热量不受影响");

                // 离开再接入同一台：冷却不重置、反应仍被压住。
                double remBefore = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);
                CommitVia(ma);
                CommitVia(mc);
                double remAfter = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);
                site.TryGetMachineWeapon(mc, out MachineWeaponInfo cBack);
                Expect(remAfter < remBefore && remAfter > 0 && cBack.ReactionSuppressed,
                    $"离开 #{mc} 再接回来：冷却继续走（{remBefore:F2} → {remAfter:F2}），反应仍被压住");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }
        }

        // ── E2. 大规模战斗：提示事件打满时冷却照样开始；内核当场压住门控反应 ─────────────

        private static void CheckCueFlood()
        {
            Line("  · E2. 同一步提示事件已打满（≥ 每步上限）时接入机打出核心反应：冷却照样开始（反应发动走不丢的玩法事件）；事件还没处理时内核当场压住，第二发不带反应");
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Core });
            try
            {
                CampaignState s = NewState(8312);
                EquipOverload(s);
                int ma = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-2f, -20f), 900f);
                int gun = SpawnRegion(FoundryOutpostLayout.RegionId, BpGunPlain, new Vector2(2f, -20f), 900f);
                FoundryOutpostController fo = OpenFoundry(s, ma, gun);
                WorldView.Observe(fo.SiteId);
                CombatSite site = fo.Combat;
                string target = FoundryOutpostLayout.ArmorBotLeftSpawnId;
                RegionEnemyRecord armor = Enemy(s, target);
                armor.MaxHealth = 1e6f; // 靶子打不死（只数反应与冷却）。
                armor.Health = 1e6f;
                site.SyncEnemyFromRecord(armor);
                Vector2 front = armor.Position + FoundryOutpostRegion.ArmorFacingOf(armor) * 6f;
                Place(site, ma, front);
                Place(site, gun, front + new Vector2(0.6f, 0f));
                float cd = FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwOverloadId);
                int cap = site.Kernel.Config.MaxCueEventsPerStep;
                float overloadHeat = FracturedCityLayout.CannonBaseHeatPerShot + FracturedCityLayout.OverloadExtraHeatPerShot;

                CommitVia(ma);
                site.TryGetMachineUnit(ma, out int maUnit);
                site.TryGetMachineUnit(gun, out int gunUnit);
                site.TryGetEnemyUnit(target, out int tUnit);
                bool gatedBefore = (Unit(site, ma).Flags & CombatUnitFlags.ReactionGated) != 0 && WeaponOf(site, ma).Reaction == CombatReaction.MeltOverload;

                // 重炮先瞄准（正式入口：第一次开火请求开始 1 秒瞄准线），推到瞄准完成、还没开火。
                site.TryFireAtEnemy(ma, target, out _, out _);
                int guard = 0;
                while (Unit(site, ma).AimReadyAt > GameClock.GameSeconds && guard++ < 400)
                {
                    WorldSimulation.StepMany(1);
                }
                // 同一步里先灌满提示事件：另一台机器经内核开火入口连开 cap + 6 枪（开火 / 命中装甲提示），热更层还没处理。
                for (int i = 0; i < cap + 6; i++)
                {
                    site.Kernel.FireAt(gunUnit, tUnit, GameClock.GameSeconds);
                }
                int cuesQueued = site.Kernel.Cues.Length;
                long dropped0 = site.Kernel.Counters.CuesDropped;
                int meltCue0 = FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload);
                int fired0 = SignalUplinkService.CoreFiredCount;
                float h0 = Heat(site, ma);
                site.TryFireAtEnemy(ma, target, out CombatFireResult res, out _);
                float h1 = Heat(site, ma);
                long dropped1 = site.Kernel.Counters.CuesDropped;
                site.TryGetMachineWeapon(ma, out MachineWeaponInfo info);
                CombatUnitFlags f1 = Unit(site, ma).Flags;
                double rem = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);
                Expect(gatedBefore && cuesQueued >= cap && res == CombatFireResult.Ok && dropped1 > dropped0
                       && FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload) == meltCue0 && Mathf.Abs(h1 - h0 - overloadHeat) < 0.01f,
                    $"本步已有 {cuesQueued} 条提示事件（上限 {cap}）：这发重炮的熔穿过载在内核里照常生效（积热 {h0:F1}→{h1:F1}），它的提示事件被丢（丢弃计数 {dropped0}→{dropped1}，没有反应音效）");
                Expect(SignalUplinkService.CoreFiredCount == fired0 + 1 && Math.Abs(rem - cd) < 0.02 && info.ReactionSuppressed
                       && WeaponOf(site, ma).Reaction == CombatReaction.None && (f1 & (CombatUnitFlags.ReactionGated | CombatUnitFlags.ReactionSpent)) == 0,
                    $"提示事件被丢也不影响冷却：核心固件发动计数 +1、冷却 {rem:F2} 秒记在信号上、武器暂不带反应（门控标记随新参数清掉）");

                // 冷却结束后：直接经内核入口连开两发、中间不让热更层处理事件——第一发发动并当场压住，第二发是普通重炮；事件处理后只记一次冷却。
                guard = 0;
                while (s.SignalCore.CoreCooldowns.Length > 0 && guard++ < 60 * 20)
                {
                    WorldSimulation.StepMany(1);
                }
                bool regated = (Unit(site, ma).Flags & CombatUnitFlags.ReactionGated) != 0 && WeaponOf(site, ma).Reaction == CombatReaction.MeltOverload;
                double now = GameClock.GameSeconds;
                site.Kernel.SetWeaponState(maUnit, 0f, false, now, 0);
                CombatFireResult r1 = site.Kernel.FireAt(maUnit, tUnit, now);
                float ha = Heat(site, ma);
                bool spent = (Unit(site, ma).Flags & CombatUnitFlags.ReactionSpent) != 0;
                site.Kernel.SetWeaponState(maUnit, 0f, false, now, 0);
                CombatFireResult r2 = site.Kernel.FireAt(maUnit, tUnit, now);
                float hb = Heat(site, ma);
                int fired1 = SignalUplinkService.CoreFiredCount;
                site.ProcessEvents();
                double rem2 = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);
                Expect(regated && r1 == CombatFireResult.Ok && r2 == CombatFireResult.Ok && Mathf.Abs(ha - overloadHeat) < 0.01f && spent
                       && Mathf.Abs(hb - FracturedCityLayout.CannonBaseHeatPerShot) < 0.01f,
                    $"事件还没被处理时：第一发熔穿过载（积热 {ha:F1}）并被内核当场压住，第二发只是普通重炮（积热 {hb:F1}）");
                Expect(SignalUplinkService.CoreFiredCount == fired1 + 1 && Math.Abs(rem2 - cd) < 0.02 && (Unit(site, ma).Flags & CombatUnitFlags.ReactionSpent) == 0
                       && WeaponOf(site, ma).Reaction == CombatReaction.None,
                    $"事件处理后：只记一次发动、冷却 {rem2:F2} 秒，武器换成冷却中压住的参数");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }
        }

        // ── F. 防刷（FGR-SIG-034）──────────────────────────────────────────────────

        private static void CheckAntiFarm()
        {
            Line("  · F. FGR-SIG-034 防刷：接入没有“插入瞬间”效果；在三台重炮之间来回切换也打不出比冷却允许更多的核心反应");
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Core });
            try
            {
                CampaignState s = NewState(8311);
                EquipOverload(s);
                int ma = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-30f, -20f), 900f);
                int mb = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-32f, -20f), 900f);
                int mc = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-34f, -20f), 900f);
                int ctl = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-36f, -20f), 900f);
                FoundryOutpostController fo = OpenFoundry(s, ma, mb, mc, ctl);
                WorldView.Observe(fo.SiteId);
                CombatSite site = fo.Combat;
                string target = FoundryOutpostLayout.ArmorBotLeftSpawnId;
                RegionEnemyRecord armor = Enemy(s, target);
                float armorHp = armor.Health;
                SetHeat(site, ma, 50f);
                SetHeat(site, ctl, 50f);
                double nextA0 = NextFire(site, ma);
                int fired0 = SignalUplinkService.CoreFiredCount;
                int recompiles0 = SignalUplinkService.RecompileNotifyCount;
                for (int i = 0; i < 10; i++)
                {
                    CommitVia(ma);
                    CommitVia(mb);
                }
                Expect(NextFire(site, ma) == nextA0 && SignalUplinkService.CoreFiredCount == fired0 && Mathf.Approximately(armor.Health, armorHp)
                       && Mathf.Abs(Heat(site, ma) - Heat(site, ctl)) < 1e-4f && NextFire(site, ma) == NextFire(site, ctl)
                       && SignalUplinkService.RecompileNotifyCount - recompiles0 == 20 * 2 - 1,
                    $"同一台反复接入 / 离开 10 次：不开火、不发动、敌人不掉血；热量与对照机一致（{Heat(site, ma):F2}），武器冷却不变；每次只重编译进出的两台（{SignalUplinkService.RecompileNotifyCount - recompiles0} 次）");

                Vector2 front = armor.Position + FoundryOutpostRegion.ArmorFacingOf(armor) * 6f;
                Place(site, ma, front);
                Place(site, mb, front + new Vector2(0.6f, 0f));
                Place(site, mc, front + new Vector2(-0.6f, 0f));
                armor.MaxHealth = 1e6f; // 靶子打不死（只数反应次数，不让“目标阵亡”提前结束窗口）。
                armor.Health = 1e6f;
                site.SyncEnemyFromRecord(armor);
                int[] ring = { ma, mb, mc };
                double start = GameClock.GameSeconds;
                int fired1 = SignalUplinkService.CoreFiredCount;
                int shots = 0;
                for (int i = 0; GameClock.GameSeconds - start < 24.0 && i < 60; i++)
                {
                    int m = ring[i % ring.Length];
                    CommitVia(m);
                    if (FireCannon(site, m, target, out _))
                    {
                        shots++;
                    }
                }
                double window = GameClock.GameSeconds - start;
                int reactions = SignalUplinkService.CoreFiredCount - fired1;
                float cd = FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwOverloadId);
                int cap = (int)Math.Floor(window / cd) + 1;
                Expect(shots >= 6 && reactions >= 2 && reactions <= cap,
                    $"{window:F1} 游戏秒里在三台之间轮流接入并开火 {shots} 次：核心反应只发动 {reactions} 次（冷却 {cd} 秒允许的上限 {cap}；若冷却跟着机体会是每发都有）");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }
        }

        // ── G. 暂停与倍速 ────────────────────────────────────────────────────────

        private static void CheckPauseSpeed()
        {
            Line("  · G. 暂停与 0.5x～3x：接入过渡按真实时间 0.35 秒（与倍速无关、暂停不走）；核心固件冷却按游戏时间（暂停不走、各档倍速到期的游戏时刻一致）");
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Core });
            try
            {
                var frames = new List<string>();
                var gameTimes = new List<double>();
                bool framesOk = true;
                bool expiryOk = true;
                bool pauseOk = true;
                foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
                {
                    CampaignState s = NewHome(8320 + (int)(speed * 10));
                    int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
                    WorldSimulation.StepMany(2);
                    EquipOverload(s);
                    GameClock.SetSpeed(speed);
                    SignalUplinkService.Request(a, UplinkSource.MachineList);
                    int n = 0;
                    while (SignalUplinkService.IsPending && n < 40)
                    {
                        Frames(1);
                        n++;
                    }
                    frames.Add($"{speed}x:{n} 帧");
                    framesOk &= n >= 6 && n <= 8 && SignalPresence.CurrentMachineLogicId == a;

                    // 冷却从“发动”开始按游戏时间走：先在接入中发动（战斗事件的同一入口），再离开回战略（解除接入锁 1x）让倍速生效。
                    SignalUplinkService.OnReactionFired(s, a, MechanicalReactionCatalog.ReactionMeltOverloadId);
                    double fireAt = GameClock.GameSeconds;
                    GameClock.SetPaused(true);
                    double remPaused0 = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);
                    Frames(20);
                    double remPaused1 = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);
                    GameClock.SetPaused(false);
                    pauseOk &= remPaused0 > 0 && Math.Abs(remPaused0 - remPaused1) < 1e-9;
                    Press(Key(GameActionId.ToggleCameraView));
                    int guard = 0;
                    while (s.SignalCore.CoreCooldowns.Length > 0 && guard++ < 2000)
                    {
                        Frames(1);
                    }
                    double elapsed = SignalUplinkService.LastCooldownExpiryGameSeconds - fireAt;
                    gameTimes.Add(elapsed);
                    expiryOk &= Math.Abs(elapsed - FirmwareKinds.CoreCooldownSeconds(FirmwareCatalog.FwOverloadId)) <= GameClock.StepSeconds + 1e-6;
                    GameClock.SetSpeed(1f);
                }
                Expect(framesOk, $"接入过渡在各档倍速下都是约 0.35 真实秒（每帧 {FrameDt} 秒：{string.Join("，", frames)}）");
                Expect(pauseOk, "暂停 1 真实秒：核心固件冷却一点没走");
                Expect(expiryOk && gameTimes.Max() - gameTimes.Min() <= GameClock.StepSeconds + 1e-6,
                    $"0.5x / 1x / 2x / 3x 下冷却都在发动后同一游戏时长到期（{string.Join("，", gameTimes.Select(x => x.ToString("F3")))} 游戏秒）");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
            }
        }

        // ── H. 存读档（FGT-SIG-009 本 Story 场景）────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · H. FGT-SIG-009：存档时玩家在机器里（真实文件、按主菜单“继续”的顺序恢复）→ 信号仍在那台机器里，固件、冷却、热量完全一致；过渡中存档；旧档缺字段；篡改");
            FirmwareKinds.OverrideForTests(new Dictionary<string, FirmwareKind> { [FirmwareCatalog.FwOverloadId] = FirmwareKind.Core });
            try
            {
                CampaignState s = NewHome(8330);
                int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
                int b = SpawnHome(BpGunPlain, new Vector2(6f, -4f));
                WorldSimulation.StepMany(2);
                EquipOverload(s);
                CombatSite site = WorldSimulation.Home.Combat;
                CommitVia(a);
                SignalUplinkService.OnReactionFired(s, a, MechanicalReactionCatalog.ReactionMeltOverloadId);
                SetHeat(site, a, 37f);
                WorldSimulation.StepMany(30);
                double rem = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);
                float heat = Heat(site, a);
                double nextFire = NextFire(site, a);
                double gameSeconds = GameClock.GameSeconds;
                string slot0 = SignalCoreService.SlotPartId(s, 0);
                int takeovers = s.TotalControlTakeovers;
                MachineRegistry.TryGetRecord(a, out MachineRecord ra);
                int timesControlled = ra.TimesControlled;
                SaveNow();

                CampaignState l = LoadLikeMenu();
                CombatSite ls = WorldSimulation.Home?.Combat;
                ls.TryGetMachineWeapon(a, out MachineWeaponInfo info);
                MachineRegistry.TryGetRecord(a, out MachineRecord la);
                Expect(l != null && SignalPresence.CurrentMachineLogicId == a && WorldSimulation.Home.PossessedMachineLogicId == a && WorldView.Director.HeadingDirect,
                    $"读档后信号仍在 #{a} 里（接管状态恢复、镜头进直控）");
                Expect(SignalCoreService.SlotPartId(l, 0) == slot0 && info.Uplinked && info.ReactionSuppressed && WeaponOf(ls, a).Reaction == CombatReaction.None
                       && Math.Abs(GameClock.GameSeconds - gameSeconds) < 1e-9 && Math.Abs(SignalUplinkService.CooldownRemaining(l, FirmwareCatalog.FwOverloadId) - rem) < 1e-9
                       && Mathf.Abs(Heat(ls, a) - heat) < 1e-5f && Math.Abs(NextFire(ls, a) - nextFire) < 1e-9,
                    $"固件（1 号槽同一实例）、冷却（剩 {rem:F3} 秒）、热量（{heat:F2}）、武器冷却逐字段一致；武器仍按接入结算且反应在冷却中");
                Expect(l.TotalControlTakeovers == takeovers && la.TimesControlled == timesControlled,
                    $"读档恢复接入不算一次新的接入（接管统计 {takeovers}、机器接管次数 {timesControlled} 不变）");
                int commitsBefore = SignalUplinkService.CommitCount;
                bool again = SignalUplinkService.RestoreAfterLoad(); // 远征归来 / 回滚重进家园（GameRoot.ResumeHomeValley）也会调：必须幂等。
                Expect(again && SignalPresence.CurrentMachineLogicId == a && WorldSimulation.Home.PossessedMachineLogicId == a
                       && l.TotalControlTakeovers == takeovers && SignalUplinkService.CommitCount == commitsBefore,
                    "再次调用读档恢复（重进家园的同一入口）：幂等，不重复接管、不改统计");

                // 过渡中存档：读回来信号在原处。
                SignalUplinkService.Request(b, UplinkSource.MachineList);
                bool pend = SignalUplinkService.IsPending;
                SaveNow();
                LoadLikeMenu();
                Expect(pend && SignalPresence.CurrentMachineLogicId == a && !SignalUplinkService.IsPending,
                    $"接入过渡中存档：过渡不进存档，读回来信号仍在原来的 #{a}");

                // 篡改：指向已阵亡 / 不存在的机器 → 回到归还核心并说明。
                CampaignState t = CampaignSession.Current;
                t.SignalCore.UplinkMachineLogicId = 987654;
                t.SignalCore.UplinkSiteId = HomeValleyLayout.RegionId;
                SaveNow();
                CampaignState tl = LoadLikeMenu();
                Expect(SignalPresence.AtCore && tl.SignalCore.UplinkMachineLogicId == 0 && SignalUplinkService.LastFeedbackText.Length > 0
                       && !GameText.ContainsMarker(SignalUplinkService.LastFeedbackText),
                    $"篡改的信号位置（不存在的机器）：读档后回到归还核心，提示“{SignalUplinkService.LastFeedbackText}”");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }

            SignalCoreState old = JsonUtility.FromJson<SignalCoreState>("{\"DomainVersion\":1,\"SlotPartIds\":[\"\",\"\"],\"NextPresetSerial\":3}");
            Expect(old.UplinkMachineLogicId == 0 && old.UplinkSiteId == string.Empty && old.CoreCooldowns != null && old.CoreCooldowns.Length == 0 && old.NextPresetSerial == 3,
                "旧档（没有信号位置 / 冷却字段）读回来 = 信号在归还核心、没有冷却（只加字段、不升域版本）");
        }

        // ── I. 性能 ──────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · I. 性能：插入固件后的重编译 ≤ 2 ms（FG01 第 7 章），与机器总数无关；没有过渡时每帧开销可忽略");
            CampaignState s = NewHome(8340);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            WorldSimulation.StepMany(2);
            EquipOverload(s);
            CommitVia(a);
            int few = WorldSimulation.Home.Combat.MachineCount;
            double small = RecompileMs(a, 300);
            for (int i = 0; i < 60; i++)
            {
                SpawnHome(i % 2 == 0 ? BpCannonUp : BpGunPlain, new Vector2(-20f + (i % 10) * 2f, 10f + (i / 10) * 2f));
            }
            WorldSimulation.StepMany(2);
            int machines = WorldSimulation.Home.Combat.MachineCount;
            double big = RecompileMs(a, 300);
            var sw = Stopwatch.StartNew();
            const int ticks = 20000;
            for (int i = 0; i < ticks; i++)
            {
                SignalUplinkService.FrameTick(FrameDt);
            }
            double idle = sw.Elapsed.TotalMilliseconds / ticks;
            sw.Restart();
            int cycles = 0;
            for (int i = 0; i < 50; i++)
            {
                SignalUplinkService.Request(i % 2 == 0 ? a : a + 1, UplinkSource.MachineList, startCamera: false);
                while (SignalUplinkService.IsPending)
                {
                    SignalUplinkService.FrameTick(1f);
                }
                cycles++;
            }
            double commit = sw.Elapsed.TotalMilliseconds / cycles;
            PerfLines.Add($"接入后重编译（通知战斗内核重算这台的武器，含装配解析）：{small:F3} ms（家园 {few} 台）/ {big:F3} ms（{machines} 台）；" +
                          $"一次完整接入提交（校验 + 接管 + 两台重编译 + 事件）：{commit:F3} ms；没有过渡时每帧 {idle * 1000:F2} µs");
            Expect(small <= 2.0 && big <= 2.0 && big <= small * 3 + 0.05 && machines >= few + 60,
                $"重编译 {small:F3} ms（{few} 台）→ {big:F3} ms（{machines} 台），≤ 2 ms 且不随机器数增长");
            Expect(commit <= 4.0 && idle < 0.01, $"一次完整接入 {commit:F3} ms（两台重编译 + 事件），空闲每帧 {idle * 1000:F2} µs");
        }

        private static double RecompileMs(int logicId, int runs)
        {
            MachineLoadoutRegistry.NotifyChanged(logicId);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < runs; i++)
            {
                MachineLoadoutRegistry.NotifyChanged(logicId);
            }
            return sw.Elapsed.TotalMilliseconds / runs;
        }

        // ── J. 界面 ──────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            Line("  · J. 界面：HUD 状态行在归还核心时隐藏、接入中显示插入内容（中英）；机器列表按钮说明随重绑；未插入原因逐条");
            CampaignState s = NewHome(8350);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            WorldSimulation.StepMany(2);
            EquipOverload(s);
            string homing = PrintChip(s, FirmwareCatalog.FwHomingId);
            string split = PrintChip(s, FirmwareCatalog.FwSplitId);
            SignalCoreService.TryEquip(s, homing, 1);
            VisualElement root = Mount(out GameObject go);
            SignalCoreHudUIToolkit hud = go.AddComponent<SignalCoreHudUIToolkit>();
            try
            {
                hud.BindView(root);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
                AdvanceRealTime(5f);
                hud.Refresh();
                bool hiddenAtCore = hud.UplinkStatusText.Length == 0;
                CommitVia(a);
                AdvanceRealTime(5f);
                hud.Refresh();
                string zh = hud.UplinkStatusText;
                GameSettings.SetLanguage(GameLanguage.En);
                hud.Refresh();
                string en = hud.UplinkStatusText;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                MachineCombatResolution withHoming = MachineLoadoutRegistry.ResolveForPilot(s, a, s.RandomSeed);
                Expect(withHoming.Preview.UplinkFirmwareIds.SequenceEqual(new[] { FirmwareCatalog.FwOverloadId, FirmwareCatalog.FwHomingId }),
                    "信号核两枚固件按槽位顺序插入接入口（过载、寻的）");
                Expect(hiddenAtCore && zh.Contains(GameText.Get("firmware.fw_overload.name")) && zh.Contains(GameText.Get("firmware.fw_homing.name"))
                       && en.Contains("Overload") && !GameText.ContainsMarker(en) && !Regex.IsMatch(en, "[\\u4e00-\\u9fff]"),
                    $"状态行：归还核心时隐藏；接入后“{zh}”；英文“{en}”");
                // 配额 2：第 3 枚未插入并说明原因（需要第 3 槽：注入超控阵列等级 1）。
                SignalCoreService.OverrideArrayTierProvider = _ => 1;
                int recompiles = SignalUplinkService.RecompileNotifyCount;
                int changes = Changes.Count;
                SignalCoreService.TryUnequip(s, 1);
                Frames(1);
                bool reinserted = SignalUplinkService.RecompileNotifyCount > recompiles && Changes.Count > changes
                                  && Changes.Last().CurrentLogicId == a && Changes.Last().InsertedFirmwareIds.SequenceEqual(new[] { FirmwareCatalog.FwOverloadId });
                SignalCoreService.TryEquip(s, homing, 1);
                SignalCoreService.TryEquip(s, split, 2);
                Frames(1);
                Expect(reinserted && Changes.Last().InsertedFirmwareIds.SequenceEqual(new[] { FirmwareCatalog.FwOverloadId, FirmwareCatalog.FwHomingId }),
                    "接入中在家园改信号核（卸下寻的、再装回并加第 3 枚）：接入的机器下一帧就按新的信号核重新插入、重编译，并发出形变更新事件");
                AdvanceRealTime(5f);
                hud.Refresh();
                string over = hud.UplinkStatusText;
                SignalCoreService.OverrideArrayTierProvider = null;
                Expect(over.Contains(GameText.Format("circuit.uplink.skip.over_quota", GameText.Get("firmware.fw_split.name"), UplinkCompiler.Quota)),
                    $"超出接入口配额的固件逐条说明：“{over}”");
                string tip = GameText.Format("signal.uplink.list_tip", InputDisplay.ForAction(GameActionId.ToggleCameraView), InputDisplay.ForAction(GameActionId.CycleControlTarget));
                Expect(tip.Contains(InputDisplay.ForAction(GameActionId.ToggleCameraView)) && !GameText.ContainsMarker(tip), $"机器列表按钮说明：“{tip}”");
                Expect(root.Q<Label>("SignalUplinkStatus") != null, "HUD UXML 有接入状态行节点");

                // 布局探针：接入状态行（长文本压测，会换行不溢出），中英文 × UI 缩放 0.8 / 1 / 1.5 × 四种分辨率。
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    foreach (float scale in new[] { 0.8f, 1f, 1.5f })
                    {
                        string result = UiToolkitLayoutProbe.Probe(UxmlPath, "SignalUplinkStatus", stressFill: true, uiScale: scale);
                        bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                        Expect(pass, $"布局探针 SignalCorePanel.uxml#SignalUplinkStatus [{lang}] 缩放 {scale:0.#}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(500, result.Length)))}");
                    }
                }
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
            finally
            {
                SignalCoreService.OverrideArrayTierProvider = null;
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                Object.DestroyImmediate(go);
            }
        }

        // ── K. 命令栏机器列表（三个地点都有）、按钮说明随重绑 / 语言、HUD 销毁不留 Esc 层 ─────────

        private const string CommandBarUxmlPath = "Assets/GameRes/Raw/UI/RegionCommand/RegionCommandBar.uxml";

        private static void CheckCommandBar()
        {
            Line("  · K. 命令栏机器列表：铸造前哨外围也有、点一下直接接入（FG-GAP-029 关闭）；按钮说明随重绑与语言更新；接入过渡中 HUD 被销毁不留 Esc 层");
            CampaignState s = NewState(8360);
            EquipOverload(s);
            int m1 = SpawnRegion(FoundryOutpostLayout.RegionId, BpCannonUp, new Vector2(-2f, -20f), 900f);
            int m2 = SpawnRegion(FoundryOutpostLayout.RegionId, BpGunPlain, new Vector2(2f, -20f), 900f);
            FoundryOutpostController fo = OpenFoundry(s, m1, m2);
            WorldView.Observe(fo.SiteId);
            VisualElement root = MountUxml(CommandBarUxmlPath, out GameObject go);
            GameObject hudGo = null;
            SignalCoreHudUIToolkit awokenHud = null;
            MethodInfo awokenHudOnDestroy = null;
            var bar = go.AddComponent<GameLogic.UI.RegionCommand.RegionCommandBarUIToolkit>();
            try
            {
                bar.BindView(root);
                bar.Refresh();
                VisualElement panel = root.Q<VisualElement>("RegionCommandBarRoot");
                ScrollView strip = root.Q<ScrollView>("ControlCandidateStrip");
                Button ButtonOf(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord r)
                    ? strip.Query<Button>().ToList().FirstOrDefault(b => b.text == "#" + r.DisplayNumber) : null;
                Button b1 = ButtonOf(m1);
                Button b2 = ButtonOf(m2);
                Expect(GameRoot.FoundryOutpost == fo && fo.IsActive && panel != null && panel.style.display == DisplayStyle.Flex
                       && strip != null && strip.Query<Button>().ToList().Count == 2 && b1 != null && b2 != null,
                    $"镜头在铸造前哨外围：命令栏显示，机器列表列出这里的 2 台机器（{b1?.text}、{b2?.text}）");

                bool clicked = InvokeClickable(b1);
                bool pendingFromList = SignalUplinkService.IsPending && SignalUplinkService.PendingTargetLogicId == m1
                                       && SignalUplinkService.PendingSource == UplinkSource.MachineList;
                // FG1-SIG-07（FGR-SIG-051）：信号在归还核心（家园），目标在远征地点 = 跨地点的远距离跳转：过渡 1.5 秒（不是 0.35 秒）。
                bool far = SignalUplinkService.PendingIsFar;
                int waited = 0;
                while (SignalUplinkService.IsPending && waited++ < 60)
                {
                    Frames(1);
                }
                Frames(4);
                bar.Refresh();
                Expect(clicked && pendingFromList && far && waited >= 25 && SignalPresence.CurrentMachineLogicId == m1 && fo.PossessedMachineLogicId == m1
                       && WorldView.Director.Mode == ViewMode.Direct && b1.ClassListContains("cmd-candidate-btn-current"),
                    $"在铸造前哨点机器列表里的 {b1?.text}：与家园同一入口；信号从归还核心跳到远征地点算远距离跳转（过渡 {waited} 帧 ≈ 1.5 秒），信号进入 #{m1}、镜头直控，列表标出当前机器");

                // 按钮说明：按键名随重绑、文字随语言；已有按钮跟着改写。
                // 说明走运行时悬停提示（VisualElement.tooltip 只在编辑器界面生效）：模拟悬停那一行，读提示面板的内容。
                string tip0 = HoverTip(b1.parent);
                bool tip0Ok = tip0.Contains(InputDisplay.ForAction(GameActionId.ToggleCameraView)) && tip0.Contains(InputDisplay.ForAction(GameActionId.CycleControlTarget))
                              && !GameText.ContainsMarker(tip0);
                KeyCode freeKey = KeyCode.None;
                foreach (KeyCode k in new[] { KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.Keypad7, KeyCode.Keypad8 })
                {
                    if (GameSettings.KeyBindings.TryRebind(GameActionId.ToggleCameraView, new InputChord(k), new List<GameActionId>()) == RebindResult.Ok)
                    {
                        freeKey = k;
                        break;
                    }
                }
                bar.Refresh();
                string tipRebound = HoverTip(b2.parent);
                GameSettings.SetLanguage(GameLanguage.En);
                bar.Refresh();
                string tipEn = HoverTip(b1.parent);
                GameSettings.KeyBindings.ResetToDefault(GameActionId.ToggleCameraView);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                bar.Refresh();
                string tipBack = HoverTip(b1.parent);
                Expect(tip0Ok && freeKey != KeyCode.None && tipRebound.Replace(SignalPresence.MachineLabel(m2), SignalPresence.MachineLabel(m1)) != tip0 && tipRebound.Contains(InputDisplay.Key(freeKey))
                       && !Regex.IsMatch(tipEn, "[\\u4e00-\\u9fff]") && tipEn.Length > 0 && !GameText.ContainsMarker(tipEn) && tipBack == tip0,
                    $"机器列表悬停提示：“{tip0.Replace("\n", " / ")}”→ 改绑接入键到 {freeKey} 后已有按钮变成“{tipRebound.Replace("\n", " / ")}”→ 英文“{tipEn.Replace("\n", " / ")}”→ 恢复默认与中文后还原");

                // HUD 在接入过渡中被销毁：Esc 取消层随之移除，不会吃掉下一次 Esc。
                // 编辑模式下 Unity 不派发 Awake / OnDestroy：按 Play 里的真实生命周期手动调它们（出生登记事件监听 → 销毁时移除监听与 Esc 层）。
                VisualElement hudRoot = Mount(out hudGo);
                SignalCoreHudUIToolkit hud = hudGo.AddComponent<SignalCoreHudUIToolkit>();
                const BindingFlags lifecycle = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                MethodInfo awake = typeof(SignalCoreHudUIToolkit).GetMethod("Awake", lifecycle);
                MethodInfo onDestroy = typeof(SignalCoreHudUIToolkit).GetMethod("OnDestroy", lifecycle);
                awake?.Invoke(hud, null);
                awokenHud = hud;
                awokenHudOnDestroy = onDestroy;
                hud.BindView(hudRoot);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
                int escBefore = UiEscapeStack.Count;
                UplinkRequestResult toM2 = SignalUplinkService.Request(m2, UplinkSource.MachineList);
                hud.Refresh();
                bool pushed = UiEscapeStack.Count == escBefore + 1;
                onDestroy?.Invoke(hud, null);
                awokenHud = null;
                Object.DestroyImmediate(hudGo);
                hudGo = null;
                bool removed = UiEscapeStack.Count == escBefore;
                SignalUplinkService.CancelByPlayer();
                Expect(awake != null && onDestroy != null && toM2.Accepted && pushed && removed && SignalCoreHudUIToolkit.Instance == null,
                    $"接入过渡中 HUD 被销毁（卸载界面 / 回主菜单）：它压的“Esc 取消接入”层随之移除（{escBefore}→{escBefore + 1}→{UiEscapeStack.Count}）");
                Press(Key(GameActionId.ToggleCameraView));
                Frames(10);
            }
            finally
            {
                GameSettings.KeyBindings.ResetToDefault(GameActionId.ToggleCameraView);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                if (awokenHud != null)
                {
                    try
                    {
                        awokenHudOnDestroy?.Invoke(awokenHud, null); // 中途失败也移除它登记的事件监听，不漏到后面的自检。
                    }
                    catch (Exception e)
                    {
                        Fail("收尾：HUD OnDestroy 抛异常：" + e.Message);
                    }
                }
                if (hudGo != null)
                {
                    Object.DestroyImmediate(hudGo);
                }
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>与冒烟里点按钮同一方式：走按钮自己的 Clickable（和鼠标点击同一回调）。</summary>
        private static bool InvokeClickable(Button b)
        {
            if (b?.clickable == null)
            {
                return false;
            }
            MethodInfo invoke = typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new[] { typeof(EventBase) }, null);
            if (invoke == null)
            {
                return false;
            }
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        // ── 世界与机器 ────────────────────────────────────────────────────────────

        /// <summary>新战役 + 家园（经真实入口载入、镜头在家园）；发电机与装配站可用（刻印固件芯片要电）；两张测试蓝图。</summary>
        private static CampaignState NewHome(int seed)
        {
            ResetWorld();
            CampaignState s = CampaignState.CreateNew("fgsig03-" + seed, "Standard", seed);
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

        /// <summary>只建战役（不载入家园）：远征地点单独载入的测试用。装配站已供电、基元仓与信号核初始化。</summary>
        private static CampaignState NewState(int seed)
        {
            ResetWorld();
            CampaignState s = CampaignState.CreateNew("fgsig03-" + seed, "Standard", seed);
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
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            InputRouter.DebugSetReader(Keys);
            Keys.Down = KeyCode.None;
            Keys.Held.Clear();
            UiEscapeStack.Clear();
            SignalUplinkService.ResetForTests();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            Changes.Clear();
        }

        private static void AddBlueprints(CampaignState s)
        {
            BlueprintCircuitBoard cannon = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            CircuitOpResult up = cannon.TrySetUplink(2);
            if (!up.Success)
            {
                Fail("测试准备：重炮蓝图 2 号格标接入口失败：" + up.Message);
            }
            AddBlueprint(s, BpCannonUp, cannon);
            AddBlueprint(s, BpGunPlain, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()));
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        /// <summary>家园里刷一台测试机器，并像装配站出厂一样登记装配（HomeValleyFactory 出厂时登记；家园进场时补登记）。</summary>
        private static int SpawnHome(string bp, Vector2 at)
        {
            int id = SpawnRegion(HomeValleyLayout.RegionId, bp, HomeSpot(at));
            CircuitOpResult r = MachineLoadoutRegistry.Register(CampaignSession.Current, id, bp, 1);
            if (!r.Success)
            {
                Fail($"测试准备：登记装配失败：{r.Message}");
            }
            return id;
        }

        /// <summary>家园里的测试站位：以归还核心为原点的偏移，且离训练靶超过交战距离（未被接入的重炮不会自动去打靶，热量与开火计数可比）。</summary>
        private static Vector2 HomeSpot(Vector2 offset)
        {
            Vector2 core = HomeValleyLayout.Core.Position;
            Vector2 dummy = HomeValleyLayout.LowThreatTargetPosition;
            Vector2 away = (core - dummy).sqrMagnitude > 1e-4f ? (core - dummy).normalized : Vector2.right;
            Vector2 p = core + offset;
            int guard = 0;
            while (Vector2.Distance(p, dummy) < HomeValleyCombatTargets.EngageRange + 4f && guard++ < 20)
            {
                p += away * 3f;
            }
            return p;
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

        private static void EquipOverload(CampaignState s)
        {
            if (SignalCoreService.SlotContentId(s, 0) == FirmwareCatalog.FwOverloadId)
            {
                return;
            }
            string chip = s.PrimitiveChips?.FirstOrDefault(c => c != null && c.CardDefId == FirmwareCatalog.FwOverloadId && c.State == PrimitiveChipState.Bag)?.PartId
                          ?? PrintChip(s, FirmwareCatalog.FwOverloadId);
            Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
            SignalCoreResult r = SignalCoreService.TryEquip(s, chip, 0);
            SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
            if (!r.Success)
            {
                Fail("测试准备：过载装入 1 号槽失败：" + r.Message);
            }
        }

        private static string PrintChip(CampaignState s, string firmwareId)
        {
            SignalCoreResult r = SignalCoreService.TryPrintFirmwareChip(s, firmwareId);
            if (!r.Success)
            {
                Fail($"测试准备：刻印 {firmwareId} 失败（{r.Message}）");
            }
            return r.CreatedId;
        }

        /// <summary>经机器列表同一入口发起接入，推帧直到完成（Direct → Direct 或 战略 → 直控都走同一过渡）。</summary>
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

        /// <summary>重炮两段式开火（直控点击的正式入口 CombatSite.TryFireAtEnemy：先瞄准 1 秒，到点再开火）。返回是否真的开了火、这一发有没有熔穿过载。</summary>
        private static bool FireCannon(CombatSite site, int logicId, string enemyId, out bool melt)
        {
            // “这台真的开了火”按它自己的武器冷却判定（开火那一刻 NextFireAt 前移）；全局开火计数里还有敌人的开火。
            int melt0 = FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload);
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
                    melt = false;
                    return false;
                }
                WorldSimulation.StepMany(1);
            }
            melt = FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload) > melt0;
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

        /// <summary>与主菜单“读取 / 继续”（MainMenuUI → CampaignRestoreOrchestrator → GameRoot.ResumeCampaign）同一顺序：
        /// 卸载旧世界 → 读档 → 换会话 → 载入家园 → 镜头看家园 → 恢复接入（GameRoot.ResumeCampaign 末尾调的同一个方法）。</summary>
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

        // ── 输入与帧 ──────────────────────────────────────────────────────────────

        private static KeyCode Key(GameActionId action) => GameSettings.KeyBindings.GetKey(action);

        /// <summary>按一次键（本帧按下），推一帧。</summary>
        private static void Press(KeyCode key)
        {
            Keys.Down = key;
            Frames(1);
            Keys.Down = KeyCode.None;
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

        private static void AdvanceRealTime(float seconds) => _fakeNow += seconds;

        private static int WeaponIndexOf(CombatSite site, int logicId) =>
            site.TryGetMachineWeapon(logicId, out MachineWeaponInfo info) ? info.WeaponIndex : -1;

        private static CombatWeapon WeaponOf(CombatSite site, int logicId)
        {
            int idx = WeaponIndexOf(site, logicId);
            return idx >= 0 && site.Kernel.TryGetWeapon(idx, out CombatWeapon w) ? w : default;
        }

        private static CombatUnitView Unit(CombatSite site, int logicId) =>
            site.TryGetMachineUnit(logicId, out int unit) && site.Kernel.TryGetUnit(unit, out CombatUnitView v) ? v : default;

        private static float Heat(CombatSite site, int logicId) => Unit(site, logicId).Heat;

        private static double NextFire(CombatSite site, int logicId) => Unit(site, logicId).NextFireAt;

        private static Vector2 MachinePos(CombatSite site, int logicId) =>
            site.TryGetMachinePosition(logicId, out Vector2 p) ? p : new Vector2(float.NaN, float.NaN);

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

        private static RegionEnemyRecord Enemy(CampaignState s, string id) => s.RegionEnemies.FirstOrDefault(e => e.EnemyInstanceId == id);

        /// <summary>
        /// 模拟悬停：走 UiTooltip 的正式绑定（没 Attach 过的元素 NotifyEnter 直接忽略，所以这也核对了“挂上了运行时悬停提示”），
        /// 过了出现延迟后读提示面板要显示的内容（标题 + 正文）。VisualElement.tooltip 只在编辑器界面生效，不能拿它当玩家看到的说明。
        /// </summary>
        private static string HoverTip(VisualElement target)
        {
            Func<float> clock0 = UiTooltip.Clock;
            Func<bool> pin0 = UiTooltip.PinHeld;
            float t = 10000f;
            try
            {
                UiTooltip.Clock = () => t;
                UiTooltip.PinHeld = () => false;
                UiTooltip.Hide();
                if (target == null)
                {
                    return string.Empty;
                }
                UiTooltip.NotifyEnter(target);
                t += 1f;
                UiTooltip.Tick();
                TooltipContent c = UiTooltip.Content;
                return c == null ? string.Empty : (c.Title ?? string.Empty) + "\n" + (c.Body ?? string.Empty);
            }
            finally
            {
                UiTooltip.Hide();
                UiTooltip.Clock = clock0;
                UiTooltip.PinHeld = pin0;
            }
        }

        private static VisualElement Mount(out GameObject go) => MountUxml(UxmlPath, out go);

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgSignalUplinkSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private sealed class Reader : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();
            public bool GetKey(KeyCode key) => key == Down || Held.Contains(key);
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
