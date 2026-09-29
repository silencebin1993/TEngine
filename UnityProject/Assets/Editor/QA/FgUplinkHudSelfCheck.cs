using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using GameConfig.fg;
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
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Common;
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
    /// FG1-HUD-01 接入 HUD、机器经历与引导钩子的自动验收（FG01 FGR-SIG-080～082、第 4 章补全清单；FG13 FGU-33、FGR-UX-040、第 8 节图鉴；
    /// 卡片负向：UI 缩放 150%、英文文本；验收：布局探针、引导只出现一次）。全部起真实系统：整个世界（家园控制器 + 战斗内核 + 统一时钟 + 全局镜头）、
    /// 正式接入入口、真实 UXML 界面与按钮、真实存档文件、真实图鉴文件——行为坏了会失败：
    /// A 数据（图鉴表 = 源数据逐字段、卡片点名的 5 条、钩子 / 链接合法、调参、动作表）；B 接入 HUD 正式链路（接入 → 槽位 / 机身 / 热量 / 电池 / 耐久 / 伤势 / 链路 / 暴露 / 经历 → 离开隐藏）；
    /// C 负向矩阵（冷却中、没有接入口、信号核为空、裸跑、过热、阵亡回弹、1 秒 10 次）；D 链路强度（强 / 中 / 弱 / 边缘 / 中断 / 不受限，地图边缘标记）；
    /// E 机器经历（次数、时长按统一时钟：暂停不走、0.5x～3x、Tab 切机、读档不重复计）；F 结算与机器详情文案；G 图鉴（解锁、持久化、坏文件、老玩家补解锁、面板、“?”入口、暂停菜单入口）；
    /// H 引导钩子只触发一次；I 接入镜头设置（缩放 / 跟随力度真改镜头、钳制、持久化、暂停菜单滑条与恢复默认）；J 跟随选中（F 键）；
    /// K 机器列表与地图标记（接入口 / 过热标记、悬停写机身状态与来源）；L 文案迁移（接管 / 直控 → 接入、离开音效钩子、英文无中文）；
    /// M 布局探针（100% / 150% × 中文 / 英文）；N 存读档；O 性能（与机器数无关、零分配）。已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgUplinkHudSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const int Slot = 0;
        private const string BpCannonUp = "bp_selfcheck_hud01_cannon_up";
        private const string BpGunPlain = "bp_selfcheck_hud01_gun_plain";
        private const string BpGunTrailUp = "bp_selfcheck_hud01_gun_trail_up";
        private const float FrameDt = 0.05f;
        private const string HudUxmlPath = "Assets/GameRes/Raw/UI/UiKit/SignalCorePanel.uxml";
        private const string CodexUxmlPath = "Assets/GameRes/Raw/UI/UiKit/MechanicCodexPanel.uxml";
        private const string PauseUxmlPath = "Assets/GameRes/Raw/UI/UiKit/PauseMenu.uxml";
        private const string CommandBarUxmlPath = "Assets/GameRes/Raw/UI/RegionCommand/RegionCommandBar.uxml";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;
        private static readonly Reader Keys = new Reader();
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/FG1-HUD-01 接入 HUD 自检")]
        public static void RunFromMenu()
        {
            var sb = new StringBuilder();
            int fails = Run(sb);
            Debug.Log(sb.ToString());
            Debug.Log(fails == 0 ? "[接入HUD] 全部通过" : $"[接入HUD] 失败 {fails} 项");
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            PerfLines.Clear();
            Line("\n[接入HUD] 接入 HUD、机器经历与引导钩子（FG1-HUD-01）");
            Snapshot snap = null;
            try
            {
                snap = SetUp();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "热更层在 Editor 下是 Mono JIT，真机走 HybridCLR 解释执行（数字只作量级参考，真机复测归 FG15-SYS-02）");
                Step(CheckData);
                Step(CheckHudFormal);
                Step(CheckNegative);
                Step(CheckLink);
                Step(CheckExperience);
                Step(CheckSettlementText);
                Step(CheckCodex);
                Step(CheckGuidanceOnce);
                Step(CheckCameraSettings);
                Step(CheckFollowSelection);
                Step(CheckListAndMap);
                Step(CheckTerminology);
                Step(CheckLayout);
                Step(CheckWorldPointerPassthrough);
                Step(CheckSaveLoad);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"接入 HUD 自检抛异常：{e}");
            }
            finally
            {
                TearDown(snap);
            }
            Line($"  · [接入HUD] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private sealed class Snapshot
        {
            public GameLanguage Language;
            public CampaignState Session;
            public int Slot;
            public string Prefs;
            public bool HadPrefs;
            public bool HadCamera;
            public Func<float> Delta;
            public Func<bool> AutoPause;
        }

        private static Snapshot SetUp()
        {
            var snap = new Snapshot
            {
                Language = GameSettings.Language,
                Session = CampaignSession.Current,
                Slot = CampaignSession.ActiveSlotIndex,
                Prefs = PlayerPrefs.GetString(SettingsPrefsKey, null),
                HadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey),
                HadCamera = Camera.main != null,
                Delta = CameraDirector.RealDeltaTime,
                AutoPause = NotificationCenter.AutoPauseHandler,
            };
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fghud01-selfcheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            ConfigSystem.Instance.Load();
            GameText.Reload();
            GridContent.Reload();
            WorldGenContent.Reload();
            FgContentTables.Reload();
            FirmwareKinds.Reload();
            GameClock.ReloadTuning();
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
            MechanicCodex.ResetForTests();
            MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex_mechanics.json");
            CameraDirector.RealDeltaTime = () => FrameDt;
            NotificationCenter.AutoPauseHandler = null;
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            GameRoot.BindWorldProviders();
            return snap;
        }

        private static void TearDown(Snapshot snap)
        {
            try
            {
                WorldSimulation.UnloadAll();
            }
            catch (Exception e)
            {
                Fail("收尾 UnloadAll 抛异常：" + e.Message);
            }
            SignalCoverageService.OverrideForTests = null;
            SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
            MechanicCodexPanelUIToolkit.InWorldOverrideForTests = false;
            MachineMorphView.ResetForTests();
            SignalUplinkService.ResetForTests();
            SignalLinkService.ResetForTests();
            SignalCoverageService.ResetForTests();
            SignalCoreService.ResetForTests();
            SignalPresence.ResetForTests();
            SignalLinkView.Clear();
            FirmwareKinds.ResetForTests();
            UplinkHudModel.ResetForTests();
            MechanicCodex.ResetForTests();
            GameClock.ResetSession();
            InputRouter.DebugSetReader(null);
            InputRouter.Reset();
            GridContent.ResetForTests();
            WorldGenContent.ResetForTests();
            HomeGridService.Invalidate();
            BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
            CampaignSaveService.SaveDirectoryOverrideForTests = null;
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            UiEscapeStack.Clear();
            UiTooltip.Hide();
            if (snap != null)
            {
                CameraDirector.RealDeltaTime = snap.Delta;
                NotificationCenter.AutoPauseHandler = snap.AutoPause;
                if (!snap.HadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
                if (snap.HadPrefs)
                {
                    PlayerPrefs.SetString(SettingsPrefsKey, snap.Prefs);
                }
                else
                {
                    PlayerPrefs.DeleteKey(SettingsPrefsKey);
                }
                GameSettings.Load();
                GameSettings.SetLanguage(snap.Language);
                if (snap.Session != null)
                {
                    CampaignSession.Set(snap.Slot, snap.Session);
                }
                else
                {
                    CampaignSession.Clear();
                }
            }
            try
            {
                if (_dir != null)
                {
                    Directory.Delete(_dir, true);
                }
            }
            catch
            {
                // 临时目录清理失败不影响结论。
            }
        }

        // ── A. 数据 ─────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：图鉴表 = 源数据、卡片点名的 5 条、钩子与链接合法；调参；动作表（跟随选中已接入、接入技能改承接）");
            // 源数据 → 运行时表逐字段（改了 fgdata_hud.py 没重新生成会失败）。
            string root = LocateRepo();
            var psi = new ProcessStartInfo("python", "tools/cell_tables/fgdata.py --dump")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = root,
                CreateNoWindow = true,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            var src = new List<string>();
            try
            {
                using (Process p = Process.Start(psi))
                {
                    string all = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(60000);
                    src = all.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("CX\t", StringComparison.Ordinal)).ToList();
                }
            }
            catch (Exception e)
            {
                Fail("跑 fgdata.py --dump 失败：" + e.Message.Replace("\0", string.Empty));
            }
            TbCodexEntry table = ConfigSystem.Instance.Tables?.TbCodexEntry;
            var runtime = table?.DataList.Select(r => string.Join("\t", "CX", r.Id, r.Tab, r.TitleKey, r.BodyKey, r.HintKey, r.Links, r.Hooks, r.SortOrder.ToString())).ToList()
                          ?? new List<string>();
            var diff = src.Except(runtime).Concat(runtime.Except(src)).ToList();
            Expect(src.Count >= 5 && src.Count == runtime.Count && diff.Count == 0,
                $"fg.TbCodexEntry {runtime.Count} 行与 fgdata_hud.py 源数据逐字段一致{(diff.Count > 0 ? "；不一致：" + string.Join(" | ", diff.Take(3)) : "")}");

            string[] required = { "codex.signal.uplink", "codex.signal.uplink_port", "codex.signal.core_firmware", "codex.signal.safe_mode", "codex.signal.raw" };
            var missing = required.Where(id => MechanicCodex.Find(id) == null).ToList();
            Expect(missing.Count == 0, $"卡片点名的图鉴条目齐全（信号接入 / 接入口 / 核心固件 / 安全模式 / 裸跑）{(missing.Count > 0 ? "；缺：" + string.Join("、", missing) : "")}");
            var badHooks = MechanicCodex.Entries.SelectMany(e => e.Hooks.Where(h => !GuidanceHooks.Known.Contains(h)).Select(h => e.Id + ":" + h)).ToList();
            var badLinks = MechanicCodex.Entries.SelectMany(e => e.Links.Where(l => MechanicCodex.Find(l) == null).Select(l => e.Id + ":" + l)).ToList();
            var badText = MechanicCodex.Entries.SelectMany(e => new[] { e.TitleKey, e.BodyKey, e.HintKey }).Where(k => !GameText.Has(k)).ToList();
            Expect(badHooks.Count == 0 && badLinks.Count == 0 && badText.Count == 0 && MechanicCodex.Entries.All(e => e.Hooks.Length > 0 && e.Links.Length > 0),
                $"{MechanicCodex.Entries.Count} 条都有解锁钩子（都是 GuidanceHooks.Known 里的真钩子）、相关条目都存在、标题 / 正文 / 获取提示文本键都在" +
                $"{(badHooks.Count + badLinks.Count + badText.Count > 0 ? "；问题：" + string.Join("、", badHooks.Concat(badLinks).Concat(badText).Take(5)) : "")}");
            bool t1 = GridContent.TryGetTuning("signal.link.strength_full_cells", out float full);
            bool t2 = GridContent.TryGetTuning("signal.link.strength_strong_percent", out float strong);
            bool t3 = GridContent.TryGetTuning("signal.link.strength_medium_percent", out float med);
            bool t4 = UiTuningValues.TryGet("camera.uplink_zoom_min", out float zmin);
            bool t5 = UiTuningValues.TryGet("camera.uplink_zoom_max", out float zmax);
            bool t6 = UiTuningValues.TryGet("camera.uplink_follow_min", out float fmin);
            bool t7 = UiTuningValues.TryGet("camera.uplink_follow_max", out float fmax);
            Expect(t1 && t2 && t3 && t4 && t5 && t6 && t7 && full > 0f && strong > med && med > 0f && zmin < 1f && zmax > 1f && fmin < 1f && fmax > 1f,
                $"调参入表：链路满格 {full} 格、强 ≥{strong}% / 中 ≥{med}%；接入镜头距离 {zmin}～{zmax}、跟随 {fmin}～{fmax}（默认 1 在范围内）");
            InputActionDef follow = InputActionCatalog.All.FirstOrDefault(d => d.Action == GameActionId.FollowSelection);
            var skills = InputActionCatalog.All.Where(d => d.Action >= GameActionId.DirectSkillSlot0 && d.Action <= GameActionId.DirectSkillSlot4).ToList();
            Expect(follow != null && follow.Status == InputActionStatus.Wired && skills.Count == 5
                   && skills.All(d => d.Status == InputActionStatus.Reserved && d.Owner == "FG15-UX-01"),
                "动作表：“跟随选中对象”改为已接入；“接入技能 1～5”仍是尚未开放、承接改为 FG15-UX-01（FG-GAP-046）");
        }

        // ── B. 接入 HUD 正式链路 ─────────────────────────────────────────────────

        private static void CheckHudFormal()
        {
            Line("  · B. 接入 HUD 正式链路（FGR-SIG-080 / FGU-33）：机器列表接入 → 槽位 / 机身 / 热量 / 电池 / 耐久 / 伤势 / 链路 / 暴露 / 经历 → 离开隐藏");
            CampaignState s = NewHome(9301);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            EquipCore(s, FirmwareCatalog.FwOverloadId, FirmwareCatalog.FwTrailId);
            Frames(2);
            WithHud(hud =>
            {
                hud.Refresh();
                UplinkHudView v = hud.UplinkHud;
                Expect(v.IsBound && !v.Visible, "信号在归还核心：接入 HUD 不显示");
                CommitVia(a);
                Frames(3);
                hud.Refresh();
                MachineRegistry.TryGetRecord(a, out MachineRecord rec);
                CombatSite site = WorldSimulation.Home.Combat;
                string label = SignalPresence.MachineLabel(a);
                string chassis = MechanicalContentFacade.ResolveChassisLabel(rec.ChassisId);
                string ov = FirmwareKinds.DisplayName(FirmwareCatalog.FwOverloadId);
                string tr = FirmwareKinds.DisplayName(FirmwareCatalog.FwTrailId);
                site.TryGetMachineWeapon(a, out MachineWeaponInfo info);
                Expect(v.Visible && v.TitleText.Contains(label) && v.TitleText.Contains(chassis),
                    $"接入后显示：机体编号与名字“{v.TitleText}”");
                Expect(v.SlotCount == SignalCoreService.UnlockedSlots(s) && v.SlotText(0).Contains(ov) && v.SlotText(0).Contains(GameText.Get("uplink.hud.state.active"))
                       && v.SlotText(1).Contains(tr) && v.SlotText(1).Contains(GameText.Get("uplink.hud.state.active")),
                    $"信号核各槽：“{v.SlotText(0)}”“{v.SlotText(1)}”（插进接入口、生效）");
                Expect(info.Morph != MorphMask.None && v.MorphText.Contains(UplinkHudModel.StateName(info.Morph)) && v.MorphText.Contains(ov),
                    $"机身状态与来源（FG-GAP-045，与战斗桥接层同一次解析 {MachineMorph.Describe(info.Morph)}）：“{v.MorphText}”");

                // 热量 / 过热（热量留在机体上：读战斗内核）
                SetHeat(site, a, 45f, false);
                hud.Refresh();
                string heat45 = v.HeatText;
                SetHeat(site, a, 120f, true);
                hud.Refresh();
                Expect(heat45.Contains("45/100") && !heat45.Contains(GameText.Format("uplink.hud.overheated", "60")) && v.HeatText.Contains("120/100")
                       && v.HeatText.Contains(GameText.Format("uplink.hud.overheated", "60")) && v.HeatWarn,
                    $"热量读战斗内核：“{heat45}”→ 过热“{v.HeatText}”（恢复线 60，警示样式）");
                SetHeat(site, a, 0f, false);

                // 电池 / 耐久 / 伤势 / 暴露（记录与战役状态）
                float cap = HomeValleyLayout.BatteryCapacity.TryGetValue(rec.ChassisId, out float c) ? c : 100f;
                rec.Battery = cap * 0.5f;
                rec.Health = rec.MaxHealth * 0.75f;
                rec.InjuryFlags = new[] { "自检伤痕" };
                s.SignalExposure = 12.5f;
                hud.Refresh();
                Expect(v.BatteryText.Contains("50%") && v.HealthText.Contains(Mathf.RoundToInt(rec.Health) + "/" + Mathf.RoundToInt(rec.MaxHealth))
                       && v.InjuryText.Contains("自检伤痕") && v.ExposureText.Contains("12.5"),
                    $"电池“{v.BatteryText}”、耐久“{v.HealthText}”、伤势“{v.InjuryText}”、暴露“{v.ExposureText}”都读正式数据");
                // 修复轮（审查 P2）：伤势存文本键 + 参数，显示随语言；旧档里的中文字面串原样显示。
                rec.InjuryFlags = new[] { MachineInjury.Combat(120f, 400f), "自检旧档伤痕" };
                GameSettings.SetLanguage(GameLanguage.En);
                hud.Refresh();
                string injEn = v.InjuryText;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                hud.Refresh();
                string injZh = v.InjuryText;
                Expect(injEn.Contains("Combat damage (HP 120/400)") && !injEn.Contains("战斗损伤") && injEn.Contains("自检旧档伤痕")
                       && injZh.Contains("战斗损伤（HP 120/400）") && rec.InjuryFlags[0] == "machine.injury.combat|120|400",
                    $"伤势按当前语言显示：英文“{injEn}”，中文“{injZh}”（存档里是 {rec.InjuryFlags[0]}，旧档字面串原样）");
                Expect(v.LinkText.Contains("100%") && v.LinkText.Contains(GameText.Get("uplink.hud.link.strong")) && Mathf.Approximately(v.LinkFillPercent, 100f),
                    $"核心旁：链路“{v.LinkText}”（条宽 {v.LinkFillPercent}%）");
                Expect(v.ExperienceText.Contains(GameText.Format("uplink.hud.experience", 1, "").Split('·')[0].Trim())
                       && v.KeysText.Contains(InputDisplay.ForAction(GameActionId.ToggleCameraView)) && v.KeysText.Contains(InputDisplay.ForAction(GameActionId.JumpHome)),
                    $"经历：“{v.ExperienceText}”；按键提示：“{v.KeysText}”");
                string helpTip = HoverTip(v.HelpButton);
                string heatTip = HoverTip(v.HeatElement);
                string linkTip = HoverTip(v.LinkElement);
                string slotTip = HoverTip(v.SlotLabel(0));
                Expect(helpTip.Contains(GameText.Get("codex.signal.uplink.title")) && heatTip.Contains("100") && heatTip.Contains("60")
                       && linkTip.Contains(UplinkHudModel.FullStrengthCells.ToString("0")) && slotTip.Contains(ov) && slotTip.Contains(FirmwareKinds.KindTip(GameLogic.Campaign.Signal.FirmwareKind.Core)),
                    $"悬停说明：“?”写明图鉴条目、热量写过热线 / 恢复线、链路写满格距离、槽位写种类说明（“{Short(helpTip)}”“{Short(heatTip)}”“{Short(linkTip)}”“{Short(slotTip)}”）");

                // 离开：隐藏
                LeaveByKey();
                hud.Refresh();
                Expect(!v.Visible, "按接入 / 退出键离开：接入 HUD 隐藏");
            });
        }

        // ── C. 负向矩阵 ─────────────────────────────────────────────────────────

        private static void CheckNegative()
        {
            Line("  · C. 负向：冷却中、没有接入口、信号核为空、裸跑、阵亡回弹、1 秒 10 次接入离开");
            CampaignState s = NewHome(9302);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int b = SpawnHome(BpGunPlain, new Vector2(9f, -4f));
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            Frames(2);
            WithHud(hud =>
            {
                UplinkHudView v = hud.UplinkHud;
                CommitVia(a);
                Frames(2);
                // 冷却中（冷却属于信号，按游戏时间）：写进正式冷却表，HUD 标“冷却 N 秒”，到点回到“生效”。
                s.SignalCore.CoreCooldowns = new[] { new SignalCoreCooldownRecord { ContentId = FirmwareCatalog.FwOverloadId, ReadyTick = GameClock.TickAfter(5.0) } };
                hud.Refresh();
                string cooling = v.SlotText(0);
                WorldSimulation.StepMany(60 * 6);
                hud.Refresh();
                Expect(cooling.Contains(GameText.Format("uplink.hud.state.cooling", "5")) && v.SlotText(0).Contains(GameText.Get("uplink.hud.state.active")),
                    $"核心固件冷却中：“{cooling}”→ 冷却结束“{v.SlotText(0)}”");

                // 没有接入口：切到 b（Tab 等价：机器列表点它）
                CommitVia(b);
                Frames(2);
                hud.Refresh();
                Expect(v.Visible && v.TitleText.Contains(SignalPresence.MachineLabel(b)) && v.NoteText == GameText.Get("uplink.hud.no_port")
                       && v.SlotText(0).Contains(GameText.Format("uplink.hud.state.not_inserted", GameText.Get("uplink.hud.reason.no_uplink"))),
                    $"没有接入口：说明“{v.NoteText}”，槽位“{v.SlotText(0)}”");

                // 信号核为空：卸下 → 接入口为空，说明怎么打开信号核
                LeaveByKey();
                Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
                SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
                SignalCoreService.TryUnequip(s, 0);
                SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
                CommitVia(a);
                Frames(2);
                hud.Refresh();
                Expect(v.NoteText == GameText.Format("uplink.hud.core_empty", InputDisplay.ForAction(GameActionId.OpenSignalCore))
                       && v.SlotText(0).Contains(GameText.Get("uplink.hud.state.empty")),
                    $"信号核为空：“{v.NoteText}”，槽位“{v.SlotText(0)}”");

                // 裸跑：带回加密固件（正式发放入口）装进信号核 → 标“裸跑”
                LeaveByKey();
                string raw = GrantRaw(s, FoundryOutpostLayout.RegionId, FoundryOutpostLayout.ArmorPierceCacheContentId, "salvage_hud01_pierce");
                SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
                SignalCoreResult er = SignalCoreService.TryEquip(s, raw, 0);
                SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
                CommitVia(a);
                Frames(2);
                hud.Refresh();
                Expect(er.Success && FirmwareKinds.IsRaw(s, FirmwareCatalog.FwArmorPierceId) && v.SlotText(0).Contains(GameText.Get("uplink.hud.state.raw")),
                    $"裸跑的敌方固件：槽位“{v.SlotText(0)}”标出“{GameText.Get("uplink.hud.state.raw")}”");

                // 阵亡：信号弹到最近能接入的机器，HUD 跟着换机器（没有合适的机器时回到核心、HUD 隐藏）
                MachineRegistry.ApplyDamage(a, 999999f);
                Frames(4);
                hud.Refresh();
                int r = SignalPresence.CurrentMachineLogicId;
                bool followed = r != a && (r == 0 ? !v.Visible : v.Visible && v.TitleText.Contains(SignalPresence.MachineLabel(r)));
                Expect(followed, $"接入中阵亡：HUD 跟着信号换到回弹的 {(r == 0 ? "归还核心（隐藏）" : SignalPresence.MachineLabel(r))}，不停留在阵亡的机器上");
            });

            // 1 秒 10 次接入 / 离开：状态一致、次数不多算
            CampaignState s2 = NewHome(9303);
            int c = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            EquipCore(s2, FirmwareCatalog.FwOverloadId);
            Frames(2);
            MachineRegistry.TryGetRecord(c, out MachineRecord rc);
            int commits0 = SignalUplinkService.CommitCount;
            for (int i = 0; i < 10; i++)
            {
                SignalUplinkService.Request(c, UplinkSource.MachineList);
                Frames(1);
                Press(Key(GameActionId.ToggleCameraView));
            }
            for (int i = 0; i < 40 && (SignalUplinkService.IsPending || WorldView.Director.Mode == ViewMode.Transition); i++)
            {
                Frames(1);
            }
            int commits = SignalUplinkService.CommitCount - commits0;
            Expect(rc.SignalUplinkCount == commits && commits <= 10,
                $"1 秒内连按 10 次接入 / 离开：真正完成 {commits} 次接入，与信号同行次数 {rc.SignalUplinkCount} 一致（不重复计、不多算）");
        }

        // ── D. 链路强度 ─────────────────────────────────────────────────────────

        private static void CheckLink()
        {
            Line("  · D. 链路强度（离覆盖边缘越近越弱）：强 / 中 / 弱 / 边缘（头顶标记 + 地面预警圈）/ 中断 / 不受覆盖限制");
            CampaignState s = NewHome(9304);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            Frames(2);
            float margin = 100f;
            bool bounded = true;
            SignalCoverageService.OverrideForTests = (region, pos) => new SignalCoverageSample(margin > 0f, bounded, margin, Vector2.zero, 150f, SignalCoverageSourceKind.Core);
            try
            {
                WithHud(hud =>
                {
                    UplinkHudView v = hud.UplinkHud;
                    CommitVia(a);
                    var seen = new List<string>();
                    string Probe(float m)
                    {
                        margin = m;
                        Frames(2);
                        hud.Refresh();
                        seen.Add($"{m}格→{v.LinkText}");
                        return v.LinkText;
                    }
                    string strong = Probe(100f);
                    string medium = Probe(30f);
                    string weak = Probe(16f);
                    string edge = Probe(10f);
                    bool edgeBadge = SignalLinkView.EdgeBadgeLogicId == a && SignalLinkView.RingVisible && !SignalLinkView.RingDanger && v.LinkWarn;
                    Expect(strong.Contains("100%") && strong.Contains(GameText.Get("uplink.hud.link.strong"))
                           && medium.Contains("50%") && medium.Contains(GameText.Get("uplink.hud.link.medium"))
                           && weak.Contains("27%") && weak.Contains(GameText.Get("uplink.hud.link.weak"))
                           && edge.Contains(GameText.Format("uplink.hud.link.edge", "10")) && edgeBadge,
                        $"强 / 中 / 弱 / 边缘：{string.Join("；", seen)}；边缘时头顶加倒三角标记、地面黄色预警圈、HUD 警示样式");
                    string lost = Probe(-5f);
                    bool lostShown = lost.Contains(GameText.Get("signal.link.reason_name.out_of_coverage")) && Mathf.Approximately(v.LinkFillPercent, 0f)
                                     && SignalLinkView.RingDanger && SignalLinkView.EdgeBadgeLogicId == a;
                    for (int i = 0; i < 60 && !SignalPresence.AtCore; i++)
                    {
                        Frames(1);
                    }
                    Frames(1); // 断链发生在那一帧的模拟步里：地图表现在下一帧撤标记
                    hud.Refresh();
                    Expect(lostShown && SignalPresence.AtCore && !v.Visible && SignalLinkView.EdgeBadgeLogicId == 0,
                        $"走出覆盖：“{lost}”（{v.LinkFillPercent}%，红圈 {SignalLinkView.RingDanger}，头顶标记挂在 {SignalLinkView.EdgeBadgeLogicId}）；宽限耗尽断链、信号弹回核心（在核心 {SignalPresence.AtCore}），HUD 与头顶标记撤掉");
                    margin = 100f;
                    bounded = false;
                    SignalLinkService.ResetForTests();
                    WorldSimulation.StepMany(60 * 3); // 安全模式条件消失 2 秒后退出
                    CommitVia(a);
                    Frames(2);
                    hud.Refresh();
                    Expect(v.LinkText == GameText.Get("uplink.hud.link.unbounded") && Mathf.Approximately(v.LinkFillPercent, 100f),
                        $"不受覆盖限制的地点：“{v.LinkText}”");
                });
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
            }
            // 档位纯计算的边界（与 HUD 同一个函数）
            UplinkLinkLevel l0 = UplinkHudModel.LevelOf(new SignalCoverageSample(true, true, UplinkHudModel.FullStrengthCells * 0.67f + 0.01f, Vector2.zero, 150f, SignalCoverageSourceKind.Core), SignalLinkBreakReason.None, out float s0);
            UplinkLinkLevel l1 = UplinkHudModel.LevelOf(new SignalCoverageSample(true, true, 5f, Vector2.zero, 150f, SignalCoverageSourceKind.Core), SignalLinkBreakReason.Jammed, out float s1);
            Expect(l0 == UplinkLinkLevel.Strong && s0 > 0.66f && l1 == UplinkLinkLevel.Lost && s1 == 0f, $"档位边界：67% 为强；干扰场宽限中为中断（{l0}/{l1}）");
        }

        // ── E. 机器经历 ─────────────────────────────────────────────────────────

        private static void CheckExperience()
        {
            Line("  · E. 机器经历“与信号同行”（FGR-SIG-082）：次数与时长按统一时钟，暂停不走、倍速按游戏时间、Tab 切机、离开后保留");
            CampaignState s = NewHome(9305);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int b = SpawnHome(BpGunPlain, new Vector2(9f, -4f));
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            Frames(2);
            MachineRegistry.TryGetRecord(a, out MachineRecord ra);
            MachineRegistry.TryGetRecord(b, out MachineRecord rb);
            Expect(ra.SignalUplinkCount == 0 && MachineSignalExperience.TotalTicks(s, ra) == 0 && MachineSignalExperience.Describe(s, ra) == GameText.Get("machine.exp.signal_none"),
                $"还没接入过：“{MachineSignalExperience.Describe(s, ra)}”");
            CommitVia(a);
            long t0 = GameClock.Ticks;
            WorldSimulation.StepMany(120);
            long d1 = MachineSignalExperience.TotalTicks(s, ra);
            Expect(ra.SignalUplinkCount == 1 && d1 == GameClock.Ticks - t0 && d1 >= 120,
                $"接入后 1 次；走 120 步累计 {d1} 步（= 统一时钟走过的步数 {GameClock.Ticks - t0}）");
            // 暂停不走
            GameClock.SetPaused(true);
            long pausedBefore = MachineSignalExperience.TotalTicks(s, ra);
            Frames(20);
            long pausedAfter = MachineSignalExperience.TotalTicks(s, ra);
            GameClock.SetPaused(false);
            // 倍速：接入中统一时钟锁 1x（GameClock.DirectLocked），时长仍 = 走过的游戏步数
            var speedOk = new List<string>();
            foreach (float speed in new[] { 0.5f, 2f, 3f })
            {
                GameClock.SetSpeed(speed);
                long before = MachineSignalExperience.TotalTicks(s, ra);
                long tk = GameClock.Ticks;
                Frames(10);
                long gained = MachineSignalExperience.TotalTicks(s, ra) - before;
                speedOk.Add($"{speed}x:+{gained}/{GameClock.Ticks - tk}");
                if (gained != GameClock.Ticks - tk)
                {
                    speedOk.Add("✗");
                }
            }
            GameClock.SetSpeed(1f);
            Expect(pausedAfter == pausedBefore && !speedOk.Contains("✗"),
                $"暂停 20 帧时长不变（{pausedBefore}→{pausedAfter}）；倍速下时长 = 统一时钟步数（{string.Join("，", speedOk)}）");
            // Tab 切机：a 这一段结算进记录，b +1
            long aBefore = MachineSignalExperience.TotalTicks(s, ra);
            Press(Key(GameActionId.CycleControlTarget));
            for (int i = 0; i < 30 && SignalPresence.CurrentMachineLogicId != b; i++)
            {
                Frames(1);
            }
            long aClosed = ra.SignalUplinkTicks;
            WorldSimulation.StepMany(60);
            Expect(SignalPresence.CurrentMachineLogicId == b && rb.SignalUplinkCount == 1 && ra.SignalUplinkCount == 1
                   && aClosed >= aBefore && MachineSignalExperience.TotalTicks(s, ra) == aClosed && MachineSignalExperience.TotalTicks(s, rb) >= 60,
                $"Tab 切到 {SignalPresence.MachineLabel(b)}：{SignalPresence.MachineLabel(a)} 这一段 {aClosed} 步进记录后不再增长，{SignalPresence.MachineLabel(b)} 次数 1、时长在走");
            LeaveByKey();
            CommitVia(a);
            Expect(ra.SignalUplinkCount == 2, $"离开后再接入：次数 2（“{MachineSignalExperience.Describe(s, ra)}”）");
            Expect(MachineSignalExperience.FormatDuration(59) == "0:59" && MachineSignalExperience.FormatDuration(3725) == "1:02:05",
                "时长格式：不到 1 小时 M:SS，否则 H:MM:SS");
        }

        // ── F. 结算与机器详情 ─────────────────────────────────────────────────────

        private static void CheckSettlementText()
        {
            Line("  · F. 结算（胜利页快照）与机器详情可见“与信号同行”，经历名走文本键");
            CampaignState s = NewHome(9306);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            Frames(2);
            CommitVia(a);
            WorldSimulation.StepMany(90);
            LeaveByKey();
            WorldSimulation.SyncAllForSave();
            MachineRegistry.TryGetRecord(a, out MachineRecord ra);
            CampaignCredits.Snapshot snap = CampaignCredits.Build(s);
            CampaignCredits.MachineSummary m = snap.Machines.FirstOrDefault(x => x.DisplayNumber == ra.DisplayNumber);
            Expect(m.SignalUplinkCount == 1 && m.SignalUplinkSeconds >= 1.4 && m.SignalExperienceText == MachineSignalExperience.Describe(s, ra)
                   && m.SignalExperienceText.Contains("1") && m.ExperienceDisplayNames.Contains(GameText.Get("machine.exp.controlled")),
                $"胜利页快照：{m.DisplayNumber} 号“{m.SignalExperienceText}”，经历“{string.Join("、", m.ExperienceDisplayNames ?? Array.Empty<string>())}”");
            GameSettings.SetLanguage(GameLanguage.En);
            string en = MachineSignalExperience.Describe(s, ra);
            string enFlag = MachineExperienceFlags.DisplayName(MachineExperienceFlags.Controlled);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(en.StartsWith("With the signal 1 times", StringComparison.Ordinal) && enFlag == "First time with the signal" && !Regex.IsMatch(en + enFlag, "[\\u4e00-\\u9fff]"),
                $"英文：“{en}”“{enFlag}”");
        }

        // ── G. 图鉴 ─────────────────────────────────────────────────────────────

        private static void CheckCodex()
        {
            Line("  · G. 图鉴：首次接触解锁、跨存档持久化、坏文件回落、老玩家补解锁、面板（剪影 / 相关条目 / Esc）、“?”与暂停菜单入口");
            string file = MechanicCodex.FilePathOverrideForTests;
            GameSettings.ResetAllToDefault(); // 清掉本机已见过的钩子
            MechanicCodex.Reload();
            if (File.Exists(file))
            {
                File.Delete(file);
            }
            Expect(MechanicCodex.UnlockedCount == 0 && !MechanicCodex.IsUnlocked("codex.signal.uplink"), "新玩家：机制图鉴全部未解锁");
            GuidanceHooks.Raise(GuidanceHooks.SignalFirstUplink);
            bool both = MechanicCodex.IsUnlocked("codex.signal.uplink") && MechanicCodex.IsUnlocked("codex.signal.uplink_port") && !MechanicCodex.IsUnlocked("codex.signal.raw");
            int saves = MechanicCodex.SaveCount;
            GuidanceHooks.Raise(GuidanceHooks.SignalFirstUplink);
            Expect(both && File.Exists(file) && MechanicCodex.SaveCount == saves, "第一次接入（钩子）→ 解锁“信号接入”“接入口”并写图鉴文件；再触发不重复写");
            MechanicCodex.Reload();
            Expect(MechanicCodex.IsUnlocked("codex.signal.uplink") && MechanicCodex.UnlockedCount == 2, "重新载入：从图鉴文件读回（跨存档，机制条目不按存档）");
            // 钩子已广播过、图鉴文件丢了：下一次接触照样解锁
            File.Delete(file);
            MechanicCodex.Reload();
            bool lostThenSeen = MechanicCodex.IsUnlocked("codex.signal.uplink"); // 本机设置里见过这个钩子 → 补解锁
            GameSettings.ResetAllToDefault();
            MechanicCodex.Reload();
            bool gone = !MechanicCodex.IsUnlocked("codex.signal.uplink");
            GuidanceHooks.Raise(GuidanceHooks.SignalFirstUplink);
            GuidanceHooks.Raise(GuidanceHooks.SignalFirstUplink); // 第二次：钩子不再广播，但解锁判定照做
            Expect(lostThenSeen && gone && MechanicCodex.IsUnlocked("codex.signal.uplink"), "老玩家：设置里见过的钩子载入时补解锁；图鉴文件与设置都清掉后，下一次接触重新解锁");
            File.WriteAllText(file, "{ 坏掉的 json");
            MechanicCodex.Reload();
            int afterCorrupt = MechanicCodex.UnlockedCount;
            Expect(afterCorrupt >= 0 && MechanicCodex.Entries.Count >= 5, $"图鉴文件损坏：不抛异常，按空图鉴继续（设置里的钩子补解锁 {afterCorrupt} 条）");

            // 面板
            VisualElement root = MountUxml(CodexUxmlPath, out GameObject go);
            var panel = go.AddComponent<MechanicCodexPanelUIToolkit>();
            MechanicCodexPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                panel.BindView(root);
                MechanicCodex.Open("codex.signal.raw", unlock: false);
                bool lockedShown = panel.PanelVisible && panel.SelectedId == "codex.signal.raw" && panel.EntryTitleText == GameText.Get("codex.panel.locked_title")
                                   && panel.EntryBodyText.Contains(GameText.Get("codex.signal.raw.hint")) && panel.RelatedCount == 0;
                panel.Select("codex.signal.uplink");
                bool open = panel.EntryTitleText == GameText.Get("codex.signal.uplink.title") && panel.EntryBodyText == InputDisplay.ExpandActionTokens(GameText.Get("codex.signal.uplink.body"))
                            && panel.RelatedCount == MechanicCodex.Find("codex.signal.uplink").Links.Length;
                // 修复轮（审查 P2，FG00 B02）：正文与脚注里的按键跟着改键变，不写死 Tab / Esc。
                string bodyBefore = panel.EntryBodyText;
                string footBefore = panel.FooterText;
                bool defaultKeys = bodyBefore.Contains(InputDisplay.ForAction(GameActionId.CycleControlTarget)) && footBefore.Contains(InputDisplay.ForAction(GameActionId.Cancel))
                                   && !bodyBefore.Contains(InputDisplay.ActionTokenPrefix) && !footBefore.Contains(InputDisplay.ActionTokenPrefix);
                GameSettings.ForceRebind(GameActionId.CycleControlTarget, new InputChord(KeyCode.F7));
                GameSettings.ForceRebind(GameActionId.Cancel, new InputChord(KeyCode.F8));
                panel.Refresh();
                string bodyAfter = panel.EntryBodyText;
                string footAfter = panel.FooterText;
                GameSettings.ResetKeyBindingsToDefault();
                panel.Refresh();
                Expect(defaultKeys && bodyAfter.Contains(InputDisplay.Key(KeyCode.F7)) && footAfter.Contains(InputDisplay.Key(KeyCode.F8))
                       && !bodyAfter.Contains(InputDisplay.Key(KeyCode.Tab)) && panel.EntryBodyText == bodyBefore,
                    $"改键后图鉴正文 / 脚注的按键跟着变（切换 Tab→F7、取消 Esc→F8），恢复默认后复原：“{Short(footAfter)}”");
                CheckActionTokensResolve();
                Click(panel.RelatedButton(0));
                bool jumped = panel.SelectedId == MechanicCodex.Find("codex.signal.uplink").Links[0];
                int lockedItems = Enumerable.Range(0, panel.ItemCount).Count(i => panel.ItemText(i) == GameText.Get("codex.panel.locked_title"));
                bool modal = InputRouter.IsModalOwner(panel);
                UiEscapeStack.CloseTop();
                Expect(lockedShown && open && jumped && lockedItems == panel.ItemCount - MechanicCodex.UnlockedCount && modal && !panel.PanelVisible && !MechanicCodexPanelUIToolkit.IsOpen,
                    $"面板：未解锁条目显示“？？？”与获取途径、不剧透链接；已解锁显示正文与 {MechanicCodex.Find("codex.signal.uplink").Links.Length} 个相关条目（点击跳转）；{lockedItems} 条剪影；模态，Esc 关闭");

                // 错误态：配置表不可用（模拟：条目为空）由 LoadError 驱动——这里核对正常时没有错误条
                Expect(!panel.ErrorVisible || MechanicCodex.LoadError != null, "配置表可用时不显示错误态");

                // “?”入口：接入 HUD 与暴露面板
                CampaignState s = NewHome(9307);
                int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
                EquipCore(s, FirmwareCatalog.FwOverloadId);
                Frames(2);
                WithHud(hud =>
                {
                    CommitVia(a);
                    hud.Refresh();
                    Click(hud.UplinkHud.HelpButton);
                    bool fromHud = panel.PanelVisible && panel.SelectedId == "codex.signal.uplink";
                    panel.SetOpen(false);
                    GameSettings.ResetAllToDefault();
                    MechanicCodex.Reload();
                    File.Delete(file);
                    MechanicCodex.Reload();
                    bool expLocked = !MechanicCodex.IsUnlocked("codex.signal.exposure");
                    Click(HudRoot?.Q<Button>("ExposureHelp"));
                    bool fromExposure = panel.PanelVisible && panel.SelectedId == "codex.signal.exposure" && MechanicCodex.IsUnlocked("codex.signal.exposure");
                    panel.SetOpen(false);
                    Expect(fromHud && expLocked && fromExposure,
                        "接入 HUD 的“?”打开图鉴“信号接入”；暴露面板的“?”打开“信号暴露”（从该界面打开即解锁，DEBT-FG1SIG06-06）");
                });
                // 暂停菜单“图鉴”
                VisualElement proot = MountUxml(PauseUxmlPath, out GameObject pgo);
                try
                {
                    var pause = pgo.AddComponent<PauseMenuUIToolkit>();
                    pause.BindView(proot);
                    Click(pause.CodexButton);
                    Expect(panel.PanelVisible && pause.CodexButton.text == GameText.Get("pause.codex"), $"暂停菜单“{pause.CodexButton.text}”打开图鉴（盖在暂停菜单上）");
                    panel.SetOpen(false);
                }
                finally
                {
                    Object.DestroyImmediate(pgo);
                }
            }
            finally
            {
                panel.SetOpen(false);
                MechanicCodexPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
        }

        // ── H. 引导钩子只触发一次 ─────────────────────────────────────────────────

        /// <summary>修复轮：全部文本（中英）里的 {act:动作名} 都能解析成动作，展开后不留占位；至少扫到图鉴里的 6 处。</summary>
        private static void CheckActionTokensResolve()
        {
            var bad = new List<string>();
            int tokens = 0;
            foreach (GameConfig.fg.LocText row in ConfigSystem.Instance.Tables.TbLocText.DataList)
            {
                foreach (string text in new[] { row.Zh, row.En })
                {
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }
                    for (int i = text.IndexOf(InputDisplay.ActionTokenPrefix, StringComparison.Ordinal); i >= 0;
                         i = text.IndexOf(InputDisplay.ActionTokenPrefix, i + 1, StringComparison.Ordinal))
                    {
                        tokens++;
                        int end = text.IndexOf('}', i);
                        string name = end < 0 ? "(未闭合)" : text.Substring(i + InputDisplay.ActionTokenPrefix.Length, end - i - InputDisplay.ActionTokenPrefix.Length);
                        if (end < 0 || !InputDisplay.TryParseAction(name, out _))
                        {
                            bad.Add(row.Key + ":" + name);
                        }
                    }
                    if (InputDisplay.ExpandActionTokens(text).Contains(InputDisplay.ActionTokenPrefix) && !bad.Any(b0 => b0.StartsWith(row.Key + ":", StringComparison.Ordinal)))
                    {
                        bad.Add(row.Key + ":展开后仍有占位");
                    }
                }
            }
            Expect(bad.Count == 0 && tokens >= 12, $"文本里的按键占位 {{act:动作名}} 共 {tokens} 处（中英）全部能解析并展开" + (bad.Count > 0 ? "：" + string.Join("，", bad) : string.Empty));
        }

        private static void CheckGuidanceOnce()
        {
            Line("  · H. 引导钩子（FGR-UX-040 / FG15-UX-04 的钩子）：首次接入、首次形变、首次安全模式各只广播一次");
            GameSettings.ResetAllToDefault();
            var raised = new List<string>();
            Action<string> h = id => raised.Add(id);
            GuidanceHooks.FirstRaised += h;
            try
            {
                CampaignState s = NewHome(9308);
                int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
                int b = SpawnHome(BpGunPlain, new Vector2(9f, -4f));
                EquipCore(s, FirmwareCatalog.FwOverloadId);
                Frames(2);
                CommitVia(a);
                Frames(10);
                LeaveByKey();
                CommitVia(a);
                Frames(10);
                CommitVia(b);
                LeaveByKey();
                int uplinkHooks = raised.Count(x => x == GuidanceHooks.SignalFirstUplink);
                int morphHooks = raised.Count(x => x == GuidanceHooks.MorphFirstSeen);
                // 安全模式两次（走出覆盖断链）
                float margin = -5f;
                SignalCoverageService.OverrideForTests = (r, p) => new SignalCoverageSample(margin > 0f, true, margin, Vector2.zero, 150f, SignalCoverageSourceKind.Core);
                int safeEnters = SignalLinkService.SafeModeEnterCount;
                for (int round = 0; round < 2; round++)
                {
                    margin = 100f;
                    Frames(2);
                    WorldSimulation.StepMany(60 * 3);
                    CommitVia(a);
                    margin = -5f;
                    for (int i = 0; i < 80 && !SignalPresence.AtCore; i++)
                    {
                        Frames(1);
                    }
                }
                SignalCoverageService.OverrideForTests = null;
                int safeHooks = raised.Count(x => x == GuidanceHooks.SafeModeFirstEnter);
                Expect(uplinkHooks == 1 && morphHooks == 1 && SignalLinkService.SafeModeEnterCount - safeEnters >= 2 && safeHooks == 1
                       && GameSettings.HasSeenGuidanceHook(GuidanceHooks.SignalFirstUplink) && !GuidanceHooks.Raise(GuidanceHooks.SignalFirstUplink),
                    $"接入 3 次 → “首次接入”钩子 {uplinkHooks} 次；形变 {morphHooks} 次；进入安全模式 {SignalLinkService.SafeModeEnterCount - safeEnters} 次 → 钩子 {safeHooks} 次；已记进本机设置，再触发返回 false");
                Expect(GuidanceHooks.Known.Contains(GuidanceHooks.SafeModeFirstEnter) && GuidanceHooks.Known.Distinct().Count() == GuidanceHooks.Known.Count,
                    $"新钩子已登记进 GuidanceHooks.Known（共 {GuidanceHooks.Known.Count} 个、无重复），FG15-UX-04 按表对照引导内容");
            }
            finally
            {
                GuidanceHooks.FirstRaised -= h;
                SignalCoverageService.OverrideForTests = null;
            }
        }

        // ── I. 接入镜头设置 ───────────────────────────────────────────────────────

        private static void CheckCameraSettings()
        {
            Line("  · I. 接入镜头设置：距离与跟随力度真改镜头、钳制在调参范围、持久化、暂停菜单滑条与恢复默认");
            GameSettings.ResetUplinkCamera();
            var camGo = new GameObject("__HudCamProbe") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 16f;
                float2 anchor = float2.zero;
                var d = new CameraDirector();
                d.Bind(cam, (out float2 an) => { an = anchor; return true; }, new Vector3(0f, 30f, -10f), 200f, startInStrategy: false, initialDirectOrthographicSize: 16f);
                d.Tick(false);
                float Moved(float strength)
                {
                    GameSettings.SetUplinkFollowStrength(strength);
                    anchor = float2.zero;
                    for (int i = 0; i < 80; i++)
                    {
                        d.Tick(false);
                    }
                    Vector3 from = cam.transform.position;
                    anchor = new float2(10f, 0f);
                    d.Tick(false);
                    return (cam.transform.position.x - from.x) / 10f;
                }
                float k1 = Moved(1f);
                float k2 = Moved(2f);
                float kHalf = Moved(0.5f);
                float e1 = 1f - Mathf.Exp(-8f * 1f * FrameDt);
                float e2 = 1f - Mathf.Exp(-8f * 2f * FrameDt);
                Expect(Mathf.Abs(k1 - e1) < 0.02f && Mathf.Abs(k2 - e2) < 0.02f && kHalf < k1 && k1 < k2,
                    $"跟随力度：一帧追上 {k1:0.000}（1x，理论 {e1:0.000}）/ {k2:0.000}（2x，理论 {e2:0.000}）/ {kHalf:0.000}（0.5x）");
                GameSettings.SetUplinkFollowStrength(1f);
                GameSettings.SetUplinkCameraZoom(1.5f);
                for (int i = 0; i < 120; i++)
                {
                    d.Tick(false);
                }
                float zoomed = cam.orthographicSize;
                GameSettings.SetUplinkCameraZoom(0.6f);
                for (int i = 0; i < 120; i++)
                {
                    d.Tick(false);
                }
                float near = cam.orthographicSize;
                GameSettings.SetUplinkCameraZoom(10f);
                float clamped = GameSettings.UplinkCameraZoom;
                GameSettings.SetUplinkFollowStrength(-3f);
                float clampedF = GameSettings.UplinkFollowStrength;
                Expect(Mathf.Abs(zoomed - 24f) < 0.1f && Mathf.Abs(near - 9.6f) < 0.1f && Mathf.Approximately(clamped, GameSettings.UplinkCameraZoomMax)
                       && Mathf.Approximately(clampedF, GameSettings.UplinkFollowMin),
                    $"镜头距离：150% → 正交半高 {zoomed:0.00}（16×1.5）；60% → {near:0.00}；超范围钳到 {clamped} / {clampedF}");
                GameSettings.SetUplinkCameraZoom(1.3f);
                GameSettings.SetUplinkFollowStrength(0.8f);
                GameSettings.Load();
                Expect(Mathf.Approximately(GameSettings.UplinkCameraZoom, 1.3f) && Mathf.Approximately(GameSettings.UplinkFollowStrength, 0.8f), "两项设置写进本机设置，重新读取后保持");
                d.Unbind();
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }

            // 世界里：接入后全局镜头按设置的距离
            CampaignState s = NewHome(9309);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            Frames(2);
            GameSettings.SetUplinkCameraZoom(1.4f);
            CommitVia(a);
            Frames(60);
            float want = WorldView.Director.DirectTargetSize;
            Expect(WorldView.Director.Mode == ViewMode.Direct && Mathf.Abs(WorldView.Camera.orthographicSize - want) < 0.2f,
                $"正式接入：全局镜头正交半高 {WorldView.Camera.orthographicSize:0.00} → 目标 {want:0.00}（基准 × 140%）");
            LeaveByKey();

            // 暂停菜单滑条
            VisualElement proot = MountUxml(PauseUxmlPath, out GameObject pgo);
            try
            {
                var pause = pgo.AddComponent<PauseMenuUIToolkit>();
                pause.BindView(proot);
                pause.CameraZoomSlider.value = 0.8f;
                pause.CameraFollowSlider.value = 1.5f;
                bool applied = Mathf.Approximately(GameSettings.UplinkCameraZoom, 0.8f) && Mathf.Approximately(GameSettings.UplinkFollowStrength, 1.5f)
                               && pause.CameraZoomLabelText.Contains("80%") && pause.CameraFollowLabelText.Contains("150%");
                Click(pause.CameraResetButton);
                Expect(applied && Mathf.Approximately(GameSettings.UplinkCameraZoom, 1f) && Mathf.Approximately(GameSettings.UplinkFollowStrength, 1f)
                       && Mathf.Approximately(pause.CameraZoomSlider.value, 1f) && pause.CameraZoomLabelText.Contains("100%")
                       && Mathf.Approximately(pause.CameraZoomSlider.lowValue, GameSettings.UplinkCameraZoomMin) && Mathf.Approximately(pause.CameraZoomSlider.highValue, GameSettings.UplinkCameraZoomMax),
                    $"暂停菜单：拖滑条立即生效（“{pause.CameraZoomLabelText}”），“恢复默认”回到 100%，滑条范围 = 调参表");
            }
            finally
            {
                GameSettings.ResetUplinkCamera();
                Object.DestroyImmediate(pgo);
            }
        }

        // ── J. 跟随选中 ─────────────────────────────────────────────────────────

        private static void CheckFollowSelection()
        {
            Line("  · J. 跟随选中对象（默认 F，战略上下文）：镜头跟随选中的机器、平移停止、没有选中时说明原因");
            CampaignState s = NewHome(9310);
            int a = SpawnHome(BpGunPlain, new Vector2(4f, -4f));
            Frames(2);
            CombatSite site = WorldSimulation.Home.Combat;
            site.Squad?.ClearSelection();
            int denied0 = FeedbackCues.CountOf(FeedbackCueId.Denied);
            Press(Key(GameActionId.FollowSelection));
            bool deniedNoSel = !WorldView.Director.IsFollowing && FeedbackCues.CountOf(FeedbackCueId.Denied) == denied0 + 1
                               && SignalUplinkService.LastFeedbackText == GameText.Format("camera.follow.nothing", InputDisplay.ForAction(GameActionId.FollowSelection));
            site.Squad?.SelectSingle(a);
            Press(Key(GameActionId.FollowSelection));
            bool following = WorldView.Director.IsFollowing && WorldView.FollowStartCount > 0;
            Vector2 target = HomeSpot(new Vector2(20f, 10f));
            Place(site, a, target);
            Frames(40);
            float dist = math.distance(WorldView.Director.StrategyFocus, new float2(target.x, target.y));
            int stops0 = WorldView.Director.FollowStopCount;
            Keys.Held = GameSettings.KeyBindings.GetKey(GameActionId.MoveForward);
            Frames(2);
            Keys.Held = KeyCode.None;
            bool stoppedByPan = !WorldView.Director.IsFollowing && WorldView.Director.FollowStopCount == stops0 + 1;
            Press(Key(GameActionId.FollowSelection));
            bool again = WorldView.Director.IsFollowing;
            Press(Key(GameActionId.FollowSelection));
            bool toggledOff = !WorldView.Director.IsFollowing;
            Expect(deniedNoSel && following && dist < 1.5f && stoppedByPan && again && toggledOff,
                $"没有选中：拒绝并说明；选中后按 F → 跟随（机器移到别处，镜头注视点跟到 {dist:0.00} 格内）；按平移键停止；再按 F 开、再按一次停");
            // FG1-E2E-01（FGJ-M1 旅程发现）：跟随中按“回到归还核心”——镜头飞回核心并停止跟随，不会在飞到之后又被跟随拽回机器。
            Press(Key(GameActionId.FollowSelection));
            bool followingAgain = WorldView.Director.IsFollowing;
            int stops1 = WorldView.Director.FollowStopCount;
            Press(Key(GameActionId.FocusHomeCore));
            Frames(90);
            Vector2 home = WorldSimulation.Home.DefaultFocus;
            float homeDist = math.distance(WorldView.Director.StrategyFocus, new float2(home.x, home.y));
            Expect(followingAgain && !WorldView.Director.IsFollowing && WorldView.Director.FollowStopCount == stops1 + 1 && homeDist < 1.5f,
                $"跟随中按“回到归还核心”：停止跟随，1.5 秒后镜头停在核心（离核心 {homeDist:0.00} 格），没有被拽回机器");
        }

        // ── K. 机器列表与地图标记 ─────────────────────────────────────────────────

        private static void CheckListAndMap()
        {
            Line("  · K. 机器列表（◇口 / 过热标记、悬停写接入口 / 机身状态与来源 / 经历）与地图上带接入口机器的标记（FGR-SIG-081）");
            CampaignState s = NewHome(9311);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            int b = SpawnHome(BpGunTrailUp, new Vector2(9f, -4f));
            int c = SpawnHome(BpGunPlain, new Vector2(14f, -4f));
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            Frames(3);
            CombatSite site = WorldSimulation.Home.Combat;
            VisualElement barRoot = MountUxml(CommandBarUxmlPath, out GameObject barGo);
            var bar = barGo.AddComponent<GameLogic.UI.RegionCommand.RegionCommandBarUIToolkit>();
            try
            {
                bar.BindView(barRoot);
                bar.Refresh();
                SetHeat(site, c, 120f, true);
                Frames(1);
                System.Threading.Thread.Sleep(300); // 过热标记按 0.25 真实秒的节拍读内核标志
                bar.Refresh();
                string tipB = HoverTip(bar.CandidateTooltipTarget(b));
                string tipC = HoverTip(bar.CandidateTooltipTarget(c));
                Expect(bar.CandidateShowsPort(a) && bar.CandidateShowsPort(b) && !bar.CandidateShowsPort(c) && bar.CandidateShowsOverheat(c) && !bar.CandidateShowsOverheat(a),
                    $"机器列表：带接入口的两台有“◇口”标记、普通机没有；过热的那台有“过热”标记（口 {bar.CandidateShowsPort(a)}/{bar.CandidateShowsPort(b)}/{bar.CandidateShowsPort(c)}，热 {bar.CandidateShowsOverheat(a)}/{bar.CandidateShowsOverheat(c)}）");
                Expect(tipB.Contains(GameText.Get("signal.list.port_line")) && tipB.Contains(GameText.Format("signal.list.morph_line", UplinkHudModel.Compose(MorphMask.Fluid, new[] { FirmwareCatalog.FwTrailId })))
                       && tipC.Contains(GameText.Get("signal.list.no_port_line")) && tipC.Contains(GameText.Format("signal.list.overheat_line", 120, 60))
                       && tipC.Contains(GameText.Get("machine.exp.signal_none")),
                    $"悬停：AI 自带拖尾的机器写“{GameText.Format("signal.list.morph_line", UplinkHudModel.Compose(MorphMask.Fluid, new[] { FirmwareCatalog.FwTrailId }))}”（FG-GAP-045：常驻喷口态有来源）；过热写散热线；写与信号同行经历");
                SetHeat(site, c, 0f, false);

                // 地图标记（战略视角显示、接入视角隐藏）
                Frames(2);
                bool strategyMarks = SignalLinkView.PortBadgeWanted(a) && SignalLinkView.PortBadgeWanted(b) && !SignalLinkView.PortBadgeWanted(c);
                CommitVia(a);
                Frames(12);
                bool hiddenInDirect = !SignalLinkView.PortBadgeWanted(a) && !SignalLinkView.PortBadgeWanted(b);
                LeaveByKey();
                Frames(12);
                bool back = SignalLinkView.PortBadgeWanted(a);
                Expect(strategyMarks && hiddenInDirect && back, "地图（战略视角）：带接入口的机器头侧菱形标记；接入视角里隐藏、回到战略再出现");
                Expect(GameText.Format("signal.cmd.uplinked", SignalPresence.MachineLabel(a)).StartsWith("接入：", StringComparison.Ordinal) && bar.ControlledLabelText == GameText.Get("signal.cmd.strategy"),
                    $"命令栏受控标签：战略时“{bar.ControlledLabelText}”，接入时“接入：型号 #编号”（不再显示“受控 / 蓝图 ID”）");
            }
            finally
            {
                Object.DestroyImmediate(barGo);
            }
        }

        // ── L. 文案迁移与音效钩子 ─────────────────────────────────────────────────

        private static void CheckTerminology()
        {
            Line("  · L. 文案迁移（FG-GAP-011）：接管 / 直控 → 接入；接入 / 离开音效钩子（FG-GAP-044）；新文本英文无中文");
            var fails = new List<string>();
            foreach (RegionControlFailure f in Enum.GetValues(typeof(RegionControlFailure)))
            {
                string t = RegionControlSystem.TextFor(f);
                if (t.Contains("接管") || t.Contains("直控") || GameText.ContainsMarker(t))
                {
                    fails.Add(f + ":" + t);
                }
            }
            foreach (string flag in MachineExperienceFlags.All)
            {
                string t = MachineExperienceFlags.DisplayName(flag);
                if (t.Contains("接管") || GameText.ContainsMarker(t) || t == flag)
                {
                    fails.Add(flag + ":" + t);
                }
            }
            FeedbackCueDef take = FeedbackCueCatalog.Get(FeedbackCueId.Takeover);
            FeedbackCueDef leave = FeedbackCueCatalog.Get(FeedbackCueId.UplinkLeave);
            Expect(fails.Count == 0 && take.ResolvedTag == "接入" && take.ResolvedCaption == "已接入" && leave != null && !string.IsNullOrEmpty(leave.SfxId)
                   && File.Exists(Path.Combine(Application.dataPath, "GameRes/Raw/Audios/Sfx/" + leave.SfxId + ".wav")),
                $"失败原因 / 经历名 / 接入字幕都叫“接入”；离开有自己的音效钩子（{leave?.SfxId}）{(fails.Count > 0 ? "；残留：" + string.Join("、", fails) : "")}");

            CampaignState s = NewHome(9312);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            Frames(2);
            int take0 = FeedbackCues.CountOf(FeedbackCueId.Takeover);
            int leave0 = FeedbackCues.CountOf(FeedbackCueId.UplinkLeave);
            CommitVia(a);
            LeaveByKey();
            Expect(FeedbackCues.CountOf(FeedbackCueId.Takeover) == take0 + 1 && FeedbackCues.CountOf(FeedbackCueId.UplinkLeave) == leave0 + 1,
                "正式接入 / 离开各发一次提交音效（接入音与机身形变同一时刻）");

            // 新文本：中英文都有、英文里没有中文、没有缺键标记
            TbLocText loc = ConfigSystem.Instance.Tables?.TbLocText;
            string[] prefixes = { "uplink.hud.", "codex.", "machine.exp.", "machine.status.", "machine.detail.", "morph.state.", "signal.list.", "pause.codex", "pause.camera",
                "region.control.fail.", "feedback.tag.", "feedback.caption.", "camera.follow.", "victory.", "expedition.return.row_status", "signal.cmd.", "factory.reason.uplinked", "signal.port_badge." };
            var keys = loc?.DataList.Select(r => r.Key).Where(k => prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal))).ToList() ?? new List<string>();
            var bad = new List<string>();
            foreach (string k in keys)
            {
                string zh = GameText.Get(k, GameLanguage.ZhCn);
                string en = GameText.Get(k, GameLanguage.En);
                if (string.IsNullOrEmpty(zh) || string.IsNullOrEmpty(en) || GameText.ContainsMarker(zh) || GameText.ContainsMarker(en) || Regex.IsMatch(en, "[\\u4e00-\\u9fff]"))
                {
                    bad.Add(k);
                }
            }
            Expect(keys.Count >= 120 && bad.Count == 0, $"本 Story 新增 {keys.Count} 个文本键中英都有、英文不含中文{(bad.Count > 0 ? "；问题：" + string.Join("、", bad.Take(6)) : "")}");
        }

        // ── M. 布局探针 ─────────────────────────────────────────────────────────

        private static void CheckLayout()
        {
            Line("  · M. 布局探针：接入 HUD / 图鉴 / 暂停菜单（接入镜头）/ 命令栏，UI 缩放 100% 与 150% × 中文与英文，四种分辨率，不溢出、不塌陷");
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (float scale in new[] { 1f, 1.5f })
                {
                    ProbeOne(HudUxmlPath, "SignalHudStack", FillHud, lang, scale);
                    ProbeOne(CodexUxmlPath, "CodexRoot", FillCodex, lang, scale);
                    ProbeOne(PauseUxmlPath, "PauseMenuRoot", FillPause, lang, scale);
                    ProbeOne(CommandBarUxmlPath, "RegionCommandBarRoot", FillCommandBar, lang, scale);
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
        }

        /// <summary>
        /// 修复轮（审查 P1）：接入 HUD 叠在接入视角的战场上方。它的文字、条、背景都不能让世界层以为“指针在 UI 上”——
        /// 否则直控开火（InputRouter.GetMouseButtonDown(0, Direct) / TryGetPointer）在顶部居中整块失灵。只有“?”按钮挡住世界点击。
        /// 走运行时同一判定：panel.Pick → UiWindowFocus.BlocksWorldPointer；再把它装进 InputRouter 的指针拦截，端到端读直控点击。
        /// </summary>
        private static void CheckWorldPointerPassthrough()
        {
            Line("  · N. 世界点击穿透：接入 HUD 的文字 / 条 / 背景不吞直控点击与指针，悬停说明照常；只有“?”挡住（UI 缩放 100% / 150%）");
            Func<bool> blocker0 = InputRouter.DebugUiPointerBlocker;
            var mouse = new MouseReader();
            GameObject go = null;
            try
            {
                foreach (float scale in new[] { 1f, 1.5f })
                {
                    VisualElement root = MountUxml(HudUxmlPath, out go, scale); // 设置里的“UI 缩放”落在 PanelSettings.scale（同布局探针）
                    var view = new UplinkHudView();
                    view.Bind(root); // 真实绑定：槽位行由代码建（带穿透类），悬停说明挂上
                    Unhide(root);
                    root.Q<VisualElement>("SignalCorePanel")?.AddToClassList("uk-hidden");
                    root.Q<VisualElement>("ExposurePanel")?.AddToClassList("uk-hidden");
                    string[] named = { "UplinkHudTitle", "UplinkHudMorph", "UplinkHudNote", "UplinkHudHeatText", "UplinkHudBatteryText", "UplinkHudHealthText",
                        "UplinkHudLinkText", "UplinkHudInjury", "UplinkHudExposure", "UplinkHudExperience", "UplinkHudKeys" };
                    foreach (string n in named)
                    {
                        Set(root, n, "接入 HUD 穿透自检 Uplink HUD passthrough");
                    }
                    for (int i = 0; i < SignalCoreService.MaxSlots; i++)
                    {
                        Label slot = view.SlotLabel(i);
                        if (slot != null)
                        {
                            slot.text = "槽位 " + (i + 1) + " · 自检";
                        }
                    }
                    view.HelpButton.text = "?";
                    // 条的填充宽度是运行时数据（Refresh 写百分比）；这里给一个值，才能量到填充块本身不挡点击
                    root.Q<VisualElement>("UplinkHudHeatFill").style.width = Length.Percent(60f);
                    UiToolkitLayoutProbe.ForceLayout(root);
                    IPanel panel = root.panel;
                    VisualElement hud = root.Q<VisualElement>("UplinkHud");

                    var blocked = new List<string>();
                    var noHover = new List<string>();
                    int probed = 0;
                    var targets = new List<VisualElement>();
                    foreach (string n in named)
                    {
                        targets.Add(root.Q<VisualElement>(n));
                    }
                    for (int i = 0; i < SignalCoreService.MaxSlots; i++)
                    {
                        targets.Add(view.SlotLabel(i));
                    }
                    targets.Add(root.Q<VisualElement>("UplinkHudHeatFill"));
                    foreach (VisualElement t in targets)
                    {
                        if (t == null || t.worldBound.width <= 0f || t.worldBound.height <= 0f)
                        {
                            blocked.Add((t?.name ?? "（缺）") + ":无布局");
                            continue;
                        }
                        probed++;
                        Vector2 c = t.worldBound.center;
                        if (UiWindowFocus.BlocksWorldPointerAt(panel, c))
                        {
                            blocked.Add(t.name);
                        }
                        if (t.ClassListContains(UiWindowFocus.PointerPassthroughClass) && panel.Pick(c) != t)
                        {
                            noHover.Add(t.name);
                        }
                    }
                    // HUD 背景（左上内边距处，不落在任何文字上）
                    Rect hb = hud.worldBound;
                    Vector2 bg = new Vector2(hb.xMin + 2f, hb.yMin + 2f);
                    bool bgBlocks = UiWindowFocus.BlocksWorldPointerAt(panel, bg);
                    Vector2 help = view.HelpButton.worldBound.center;
                    bool helpBlocks = UiWindowFocus.BlocksWorldPointerAt(panel, help);
                    // 对照：拿掉穿透类，同一处就会挡住——证明放行确实来自这个类，而不是探针测不到
                    VisualElement heat = root.Q<VisualElement>("UplinkHudHeatText");
                    heat.RemoveFromClassList(UiWindowFocus.PointerPassthroughClass);
                    bool heatBlocksWithoutClass = UiWindowFocus.BlocksWorldPointerAt(panel, heat.worldBound.center);
                    heat.AddToClassList(UiWindowFocus.PointerPassthroughClass);
                    Expect(probed >= 15 && blocked.Count == 0 && noHover.Count == 0 && !bgBlocks && helpBlocks && heatBlocksWithoutClass
                           && hb.width > 200f && hb.height > 60f,
                        $"[{scale * 100f:0}%] 接入 HUD（{hb.width:0}×{hb.height:0}）{probed} 处文字 / 条与背景都不挡世界点击，带悬停说明的仍能被拾取；“?”挡住；拿掉穿透类的对照会挡住"
                        + (blocked.Count > 0 ? "；挡住：" + string.Join("，", blocked) : string.Empty) + (noHover.Count > 0 ? "；失去悬停：" + string.Join("，", noHover) : string.Empty));

                    // 端到端：把同一判定装进 InputRouter，直控开火入口在 HUD 文字上照样收到点击与指针；在“?”上让位
                    InputRouter.Reset();
                    InputRouter.DebugSetReader(mouse);
                    InputRouter.SetScope(InputScope.Direct);
                    mouse.Down0 = true;
                    Vector2 at = heat.worldBound.center;
                    InputRouter.SetUiPointerBlocker(() => UiWindowFocus.BlocksWorldPointerAt(panel, at));
                    InputRouter.DebugClearConsumedKeys();
                    bool fireOnText = InputRouter.GetMouseButtonDown(0, InputScope.Direct);
                    bool pointerOnText = InputRouter.TryGetPointer(InputScope.Direct, out Vector3 _);
                    at = bg;
                    bool fireOnBg = InputRouter.GetMouseButtonDown(0, InputScope.Direct);
                    at = help;
                    bool fireOnHelp = InputRouter.GetMouseButtonDown(0, InputScope.Direct);
                    bool pointerOnHelp = InputRouter.TryGetPointer(InputScope.Direct, out Vector3 _);
                    mouse.Down0 = false;
                    Expect(fireOnText && pointerOnText && fireOnBg && !fireOnHelp && !pointerOnHelp,
                        $"[{scale * 100f:0}%] 指针在接入 HUD 文字 / 背景上：GetMouseButtonDown(0, Direct)={fireOnText}/{fireOnBg}、TryGetPointer={pointerOnText}（直控开火照常）；在“?”上={fireOnHelp}/{pointerOnHelp}（让位给按钮）");
                    Object.DestroyImmediate(go);
                    go = null;
                }
            }
            finally
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
                InputRouter.Reset();
                InputRouter.SetUiPointerBlocker(blocker0);
                InputRouter.DebugSetReader(Keys);
            }
        }

        private static void ProbeOne(string uxml, string rootName, Action<VisualElement> fill, GameLanguage lang, float scale)
        {
            string result = UiToolkitLayoutProbe.Probe(uxml, rootName, stressFill: true, prepare: fill, uiScale: scale);
            bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
            Expect(pass, $"布局探针 {Path.GetFileName(uxml)}#{rootName} [{lang} {scale * 100f:0}%]：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
        }

        private static void Unhide(VisualElement root)
        {
            root.Query<VisualElement>(className: "uk-hidden").ForEach(e => e.RemoveFromClassList("uk-hidden"));
        }

        private static void FillHud(VisualElement root)
        {
            Unhide(root);
            string fw = FirmwareKinds.DisplayName(FirmwareCatalog.FwArmorPierceId);
            Set(root, "SignalLocation", GameText.Format("signal.hud.in_machine", "ERC-003 #128"));
            Set(root, "SignalUplinkStatus", GameText.Format("signal.link.warn.out_of_coverage", "ERC-003 #128", "2"));
            Set(root, "UplinkHudTitle", GameText.Format("uplink.hud.title", "ERC-003 #128", MechanicalContentFacade.ResolveChassisLabel(HomeValleyLayout.Erc003ChassisId)));
            Set(root, "UplinkHudMorph", GameText.Format("uplink.hud.morph", UplinkHudModel.Compose(MorphMask.All, new[] { FirmwareCatalog.FwOverloadId, FirmwareCatalog.FwTrailId, FirmwareCatalog.FwMarkTagId })));
            Set(root, "UplinkHudNote", GameText.Format("uplink.hud.core_empty", "P"));
            VisualElement slots = root.Q<VisualElement>("UplinkHudSlots");
            slots?.Clear();
            string[] states =
            {
                GameText.Format("uplink.hud.state.cooling", "8"),
                GameText.Format("uplink.hud.state.not_inserted", GameText.Format("uplink.hud.reason.path_limit", 4)) + " · " + GameText.Get("uplink.hud.state.raw"),
                GameText.Format("uplink.hud.state.ineffective", GameText.Get("uplink.hud.reason.off_path")),
                GameText.Format("uplink.hud.state.not_inserted", GameText.Format("uplink.hud.reason.over_quota", 2)),
                GameText.Get("uplink.hud.state.empty"),
            };
            for (int i = 0; i < states.Length; i++)
            {
                var l = new Label(GameText.Format("uplink.hud.slot", (i + 1).ToString(), "◆ " + fw) + " · " + states[i]);
                l.AddToClassList("uh-slot");
                slots?.Add(l);
            }
            Set(root, "UplinkHudHeatText", GameText.Format("uplink.hud.heat", "120", "100") + " · " + GameText.Format("uplink.hud.overheated", "60"));
            Set(root, "UplinkHudBatteryText", GameText.Format("uplink.hud.battery", "100"));
            Set(root, "UplinkHudHealthText", GameText.Format("uplink.hud.health", "1200", "1200"));
            Set(root, "UplinkHudLinkText", GameText.Format("uplink.hud.link.grace", GameText.Get("signal.link.reason_name.out_of_coverage"), "2"));
            Set(root, "UplinkHudInjury", GameText.Format("uplink.hud.injury", MachineInjury.DescribeAll(new[] { MachineInjury.Combat(120f, 400f), MachineInjury.Combat(80f, 400f) })));
            Set(root, "UplinkHudExposure", GameText.Format("uplink.hud.exposure", "99.5"));
            Set(root, "UplinkHudExperience", GameText.Format("uplink.hud.experience", 128, "12:34:56"));
            Set(root, "UplinkHudKeys", GameText.Format("uplink.hud.keys", "Right Shift", "Tab", "Home", "Ctrl+Alt+P"));
            root.Q<VisualElement>("SignalCorePanel")?.AddToClassList("uk-hidden");
            root.Q<VisualElement>("ExposurePanel")?.AddToClassList("uk-hidden");
        }

        private static void FillCodex(VisualElement root)
        {
            Unhide(root);
            Set(root, "CodexTitle", GameText.Get("codex.panel.title"));
            Set(root, "CodexCount", GameText.Format("codex.panel.list_title", 10, 10));
            Set(root, "CodexError", GameText.Get("codex.panel.error"));
            var list = root.Q<ScrollView>("CodexList");
            foreach (MechanicCodexEntry e in MechanicCodex.Entries)
            {
                var b = new Button { text = GameText.Get(e.TitleKey) };
                b.AddToClassList("mw-btn");
                b.AddToClassList("cx-item");
                list?.Add(b);
            }
            MechanicCodexEntry longest = MechanicCodex.Entries.OrderByDescending(e => GameText.Get(e.BodyKey).Length).First();
            Set(root, "CodexEntryTitle", GameText.Get(longest.TitleKey));
            Set(root, "CodexEntryBody", GameText.Get(longest.BodyKey));
            Set(root, "CodexRelatedTitle", GameText.Get("codex.panel.related"));
            VisualElement related = root.Q<VisualElement>("CodexRelated");
            foreach (string link in longest.Links)
            {
                var b = new Button { text = GameText.Get(MechanicCodex.Find(link).TitleKey) };
                b.AddToClassList("mw-btn");
                b.AddToClassList("cx-link");
                related?.Add(b);
            }
            Set(root, "CodexFooter", GameText.Get("codex.panel.footer"));
            root.Q<Button>("CodexClose").text = GameText.Get("codex.panel.close");
        }

        private static void FillPause(VisualElement root)
        {
            Unhide(root);
            Set(root, "PauseMenuTitle", GameText.Get("ui.pause.title"));
            foreach ((string name, string key) in new[] { ("PauseResume", "ui.pause.resume"), ("PauseKeyBindings", "ui.pause.keybinds"), ("PauseNotifications", "ui.pause.notifications"),
                         ("PauseCodex", "pause.codex"), ("PauseSaveQuit", "ui.pause.save_and_quit"), ("PauseCopySeed", "ui.pause.copy_seed"), ("PauseCameraReset", "pause.camera_reset") })
            {
                Button b = root.Q<Button>(name);
                if (b != null)
                {
                    b.text = GameText.Get(key);
                }
            }
            root.Q<Button>("PauseGallery")?.AddToClassList("uk-hidden");
            Set(root, "PauseCameraTitle", GameText.Get("pause.camera_title"));
            Set(root, "PauseCameraZoomLabel", GameText.Format("pause.camera_zoom", "160"));
            Set(root, "PauseCameraFollowLabel", GameText.Format("pause.camera_follow", "200"));
            Set(root, "PauseWorldSeed", GameText.Format("ui.pause.world_seed", "1234567890"));
            Set(root, "PauseFeedback", GameText.Format("ui.pause.seed_copied", "1234567890"));
        }

        private static void FillCommandBar(VisualElement root)
        {
            Set(root, "ControlledUnitLabel", GameText.Format("signal.cmd.reconnecting", "ERC-003 #128"));
            var strip = root.Q<ScrollView>("ControlCandidateStrip");
            for (int i = 0; i < 3; i++)
            {
                var item = new VisualElement();
                item.AddToClassList("cmd-candidate-item");
                item.AddToClassList("cmd-candidate-item-port");
                item.AddToClassList("cmd-candidate-item-hot");
                var b = new Button { text = "#" + (120 + i) };
                b.AddToClassList("cmd-candidate-btn");
                item.Add(b);
                foreach ((string cls, string key) in new[] { ("cmd-candidate-port-tag", "signal.list.port_tag"), ("cmd-candidate-heat-tag", "signal.list.overheat_tag") })
                {
                    var l = new Label(GameText.Get(key));
                    l.AddToClassList(cls);
                    item.Add(l);
                }
                strip?.Add(item);
            }
        }

        private static void Set(VisualElement root, string name, string text)
        {
            Label l = root.Q<Label>(name);
            if (l != null)
            {
                l.text = text;
            }
        }

        // ── N. 存读档 ───────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · N. 真实文件存读档：接入中存档 → 读档后信号仍在那台、与信号同行次数不重复计、时长接着走；旧档（没有段起点）从读档那一刻开始计");
            CampaignState s = NewHome(9313);
            int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
            EquipCore(s, FirmwareCatalog.FwOverloadId);
            Frames(2);
            CommitVia(a);
            WorldSimulation.StepMany(90);
            MachineRegistry.TryGetRecord(a, out MachineRecord ra);
            int count = ra.SignalUplinkCount;
            long total = MachineSignalExperience.TotalTicks(s, ra);
            long since = s.SignalCore.UplinkSinceTick;
            SaveNow();
            CampaignState l = LoadLikeMenu();
            if (l == null)
            {
                return;
            }
            MachineRegistry.TryGetRecord(a, out MachineRecord la);
            long loadedTotal = MachineSignalExperience.TotalTicks(l, la);
            Frames(2);
            WorldSimulation.StepMany(30);
            long later = MachineSignalExperience.TotalTicks(l, la);
            Expect(SignalPresence.CurrentMachineLogicId == a && la.SignalUplinkCount == count && l.SignalCore.UplinkSinceTick == since
                   && loadedTotal == total && later - loadedTotal >= 30,
                $"读档：信号仍在 {SignalPresence.MachineLabel(a)}，次数 {la.SignalUplinkCount}（存档前 {count}），时长 {loadedTotal} 步（存档前 {total}），再走 30 步 → {later}");
            WithHud(hud =>
            {
                hud.Refresh();
                Expect(hud.UplinkHud.Visible && hud.UplinkHud.ExperienceText.Contains(count.ToString()), $"读档后接入 HUD 直接显示（“{hud.UplinkHud.ExperienceText}”）");
            });
            // 旧档：UplinkSinceTick 读成 0
            l.SignalCore.UplinkSinceTick = 0;
            long recTicks = la.SignalUplinkTicks;
            SaveNow();
            CampaignState o = LoadLikeMenu();
            if (o == null)
            {
                return;
            }
            MachineRegistry.TryGetRecord(a, out MachineRecord oa);
            Expect(o.SignalCore.UplinkSinceTick == GameClock.Ticks && MachineSignalExperience.TotalTicks(o, oa) == recTicks,
                $"FG1-HUD-01 之前的存档：这一段从读档那一刻开始计（段起点 = 当前步 {GameClock.Ticks}），不把整局时长算进去");
        }

        // ── O. 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · O. 性能：接入 HUD 每帧刷新与机器总数无关、状态不变时零分配零重建");
            double Measure(int extra, out long alloc, out int rebuilds)
            {
                CampaignState s = NewHome(9320 + extra);
                int a = SpawnHome(BpCannonUp, new Vector2(4f, -4f));
                for (int i = 0; i < extra; i++)
                {
                    SpawnHome(BpGunPlain, new Vector2(8f + (i % 20) * 2f, -8f - (i / 20) * 2f));
                }
                EquipCore(s, FirmwareCatalog.FwOverloadId);
                Frames(2);
                CommitVia(a);
                Frames(2);
                double ms = 0;
                long allocL = 0;
                int rebuildsL = 0;
                WithHud(hud =>
                {
                    hud.Refresh();
                    hud.Refresh();
                    int r0 = hud.UplinkHud.SlotRebuilds;
                    int w0 = hud.UplinkHud.VitalRewrites;
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < 1000; i++)
                    {
                        hud.UplinkHud.Refresh(CampaignSession.Current, true);
                    }
                    sw.Stop();
                    allocL = GC.GetAllocatedBytesForCurrentThread() - a0;
                    rebuildsL = hud.UplinkHud.SlotRebuilds - r0 + hud.UplinkHud.VitalRewrites - w0;
                    ms = sw.Elapsed.TotalMilliseconds / 1000.0;
                });
                alloc = allocL;
                rebuilds = rebuildsL;
                return ms;
            }
            double few = Measure(0, out long allocFew, out int rbFew);
            double many = Measure(200, out long allocMany, out int rbMany);
            PerfLines.Add($"接入 HUD 每帧刷新（状态不变）：1 台机器 {few * 1000:0.0} µs，201 台 {many * 1000:0.0} µs；分配 {allocFew}/{allocMany} 字节；重建 {rbFew}/{rbMany} 次（Editor batchmode，Mono JIT）");
            Expect(rbFew == 0 && rbMany == 0 && allocFew < 4096 && allocMany < 4096 && many < few * 3 + 0.02 && many < 0.1,
                $"状态不变时每帧 {few * 1000:0.0}/{many * 1000:0.0} µs、不重建、几乎不分配（{allocFew}/{allocMany} 字节 / 1000 帧）；200 台机器不放大开销");
        }

        // ── 世界与机器 ─────────────────────────────────────────────────────────

        /// <summary>仓库根（影子工程的工作目录在仓库根下的 .unity-validate-clone，往上找有 tools/cell_tables 的那一层）。</summary>
        private static string LocateRepo()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "cell_tables", "fgdata.py")))
                {
                    return dir.FullName;
                }
            }
            return Directory.GetCurrentDirectory();
        }

        private static GameObject _hudGo;
        private static VisualElement HudRoot;

        private static void WithHud(Action<SignalCoreHudUIToolkit> body)
        {
            VisualElement root = MountUxml(HudUxmlPath, out GameObject go);
            var hud = go.AddComponent<SignalCoreHudUIToolkit>();
            _hudGo = go;
            HudRoot = root;
            SignalCoreHudUIToolkit.InWorldOverrideForTests = () => true;
            try
            {
                hud.BindView(root);
                body(hud);
            }
            finally
            {
                hud.SetOpen(false);
                SignalCoreHudUIToolkit.InWorldOverrideForTests = null;
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
                _hudGo = null;
                HudRoot = null;
            }
        }

        private static CampaignState NewHome(int seed)
        {
            ResetWorld();
            CampaignState s = CampaignState.CreateNew("fghud01-" + seed, "Standard", seed);
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
            SignalCoverageService.Invalidate();
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
            SignalLinkService.ResetForTests();
            SignalCoverageService.ResetForTests();
            SignalLinkView.Clear();
            MachineMorphView.ResetForTests();
            UplinkHudModel.ResetForTests();
        }

        private static void AddBlueprints(CampaignState s)
        {
            BlueprintCircuitBoard cannon = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            ExpectPrep(cannon.TrySetUplink(2), "重炮蓝图 2 号格标接入口");
            AddBlueprint(s, BpCannonUp, cannon);
            AddBlueprint(s, BpGunPlain, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()));
            BlueprintCircuitBoard gunTrail = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { FirmwareCatalog.FwTrailId });
            ExpectPrep(gunTrail.TrySetUplink(2), "连射器 + 拖尾蓝图 2 号格标接入口");
            AddBlueprint(s, BpGunTrailUp, gunTrail);
        }

        private static void ExpectPrep(CircuitOpResult r, string what)
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

        private static int SpawnHome(string bp, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, HomeValleyLayout.RegionId, HomeSpot(at), 400f, 400f, "Player", 1);
            if (!r.Success)
            {
                Fail($"测试准备：登记机器失败：{r.Message}");
                return 0;
            }
            CircuitOpResult reg = MachineLoadoutRegistry.Register(CampaignSession.Current, r.LogicId, bp, 1);
            if (!reg.Success)
            {
                Fail($"测试准备：登记装配失败：{reg.Message}");
            }
            return r.LogicId;
        }

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

        private static void EquipCore(CampaignState s, params string[] firmwareIds)
        {
            Func<bool> old = SignalCoreService.ExpeditionUnderwayOverrideForTests;
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
            try
            {
                for (int i = 0; i < firmwareIds.Length; i++)
                {
                    if (SignalCoreService.SlotContentId(s, i) == firmwareIds[i])
                    {
                        continue;
                    }
                    string chip = s.PrimitiveChips?.FirstOrDefault(c => c != null && c.CardDefId == firmwareIds[i] && c.State == PrimitiveChipState.Bag)?.PartId;
                    if (chip == null)
                    {
                        SignalCoreResult pr = SignalCoreService.TryPrintFirmwareChip(s, firmwareIds[i]);
                        if (!pr.Success)
                        {
                            Fail($"测试准备：刻印 {firmwareIds[i]} 失败（{pr.Message}）");
                            continue;
                        }
                        chip = pr.CreatedId;
                    }
                    SignalCoreResult r = SignalCoreService.TryEquip(s, chip, i);
                    if (!r.Success)
                    {
                        Fail($"测试准备：{firmwareIds[i]} 装入 {i + 1} 号槽失败：{r.Message}");
                    }
                }
            }
            finally
            {
                SignalCoreService.ExpeditionUnderwayOverrideForTests = old;
            }
        }

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

        private static void CommitVia(int logicId)
        {
            if (SignalPresence.CurrentMachineLogicId == logicId)
            {
                return;
            }
            int guard = 0;
            while (WorldView.Director.Mode == ViewMode.Transition && guard++ < 40)
            {
                Frames(1);
            }
            UplinkRequestResult r = SignalUplinkService.Request(logicId, UplinkSource.MachineList);
            if (!r.Accepted)
            {
                Fail($"测试准备：接入 #{logicId} 被拒（{r.Failure}：{r.Text}）");
                return;
            }
            int n = 0;
            while ((SignalUplinkService.IsPending || WorldView.Director.Mode == ViewMode.Transition) && n++ < 60)
            {
                Frames(1);
            }
            if (SignalPresence.CurrentMachineLogicId != logicId)
            {
                Fail($"测试准备：接入 #{logicId} 没有完成（信号在 {SignalPresence.CurrentMachineLogicId}，最后反馈“{SignalUplinkService.LastFeedbackText}”）");
            }
        }

        private static void LeaveByKey()
        {
            int guard = 0;
            while (WorldView.Director.Mode != ViewMode.Direct && guard++ < 40)
            {
                Frames(1);
            }
            Press(Key(GameActionId.ToggleCameraView));
            int n = 0;
            while ((!SignalPresence.AtCore || WorldView.Director.Mode == ViewMode.Transition) && n++ < 60)
            {
                Frames(1);
            }
            if (!SignalPresence.AtCore)
            {
                Fail($"测试准备：按键离开没有完成（信号在 #{SignalPresence.CurrentMachineLogicId}）");
            }
        }

        private static void SetHeat(CombatSite site, int logicId, float heat, bool overheated)
        {
            if (site.TryGetMachineUnit(logicId, out int unit) && site.Kernel.TryGetUnit(unit, out BinGames.Sim.Combat.CombatUnitView v))
            {
                site.Kernel.SetWeaponState(unit, heat, overheated, v.AimReadyAt, v.NextFireAt);
            }
        }

        private static void Place(CombatSite site, int logicId, Vector2 at)
        {
            if (site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new double2(at.x, at.y));
            }
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
            InputRouter.DebugSetReader(Keys);
            return r.State;
        }

        private static KeyCode Key(GameActionId action) => GameSettings.KeyBindings.GetKey(action);

        private static string Short(string t) => (t ?? string.Empty).Replace("\n", " / ").Length > 60 ? (t ?? string.Empty).Replace("\n", " / ").Substring(0, 60) + "…" : (t ?? string.Empty).Replace("\n", " / ");

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

        private static VisualElement MountUxml(string uxmlPath, out GameObject go, float uiScale = 1f)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.scale = uiScale;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgUplinkHudSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
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
            MethodInfo invoke = typeof(Clickable).GetMethod("Invoke", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
        }

        private sealed class MouseReader : IInputReader
        {
            public bool Down0;
            public bool GetKey(KeyCode key) => false;
            public bool GetKeyDown(KeyCode key) => false;
            public bool GetMouseButtonDown(int button) => Down0 && button == 0;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(960f, 540f, 0f);
            public float MouseScrollDelta => 0f;
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

        // ── 报告 ────────────────────────────────────────────────────────────────

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
