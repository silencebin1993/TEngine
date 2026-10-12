using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
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
    /// FG7-ENV-01 游戏日、昼夜与日历的自动验收（FG07 FGR-ENV-001～003、FG14 FGR-ARC-009、FG13 FGU-26；验收 FGT-ENV-001）。
    /// 全部起真实系统：真实统一时钟与世界模拟、真实家园电网（太阳能接昼夜走正式入口 <see cref="DayNightCycle.Install"/>）、真文件存读档、真 UXML 世界时间条 / 日历面板 / 暂停菜单、真实突袭导演、真实输入泵。
    /// D1 数据（调参 / 界面调参 / 文本中英与源数据逐条 / 日历键 / 图鉴 / 钩子）；N1 昼夜曲线（白天 14 分钟、夜晚 6 分钟、阶段、连续、下一次变化）；
    /// N2 太阳能接昼夜（白天 60 / 黄昏递减 / 夜晚 0 / 天亮恢复，悬停与 HUD 一致）；N3 跨过入夜：版本号、入夜计数、第一次入夜引导钩子；
    /// T1 FGT-ENV-001 暂停与 0.5x～3x（整份战役状态逐字段一致、所需真实帧与倍速成反比、暂停中一切计时不动）；T2 观察 / 不观察一致；
    /// S1 真文件存读档（黄昏中存，读档后光照 / 阶段 / 太阳能逐位一致、接着跑与不存档一致）；S2 夜间亮度下限设置（持久化、旧设置补默认、越界钳回、恢复默认）；
    /// L1 场景光照（亮度 = 下限 +（1 − 下限）× 光照、夜里冷色、亮度不变不写灯、离开世界恢复原光照、没有方向光不报错）；
    /// U1 世界时间条日历行（阶段 + 倒计时、点开日历）与日历面板（N 天时间轴、夜晚色块、“现在”竖线、已预警突袭的标记与说明、事件不变不重建、Alt+L 开关、Esc 关闭、不在世界里拒绝、英文、布局探针）；
    /// U2 日历来源扩展点（模拟静默夜 / 天气来源：HUD 摘要与时间轴同时出现，去掉后恢复）；U3 暂停菜单“画面”滑条；P1 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgDayNightSelfCheck）。
    /// </summary>
    public static class FgDayNightSelfCheck
    {
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const int Slot = 7;
        private const string Solar = "solar_array";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static int _seq = 7700;
        private static readonly List<string> PerfLines = new List<string>();
        /// <summary>D1 读到的本 Story 全部文本键（D2 逐字核对字体用）。</summary>
        private static readonly List<string> EnvTextKeys = new List<string>();
        private const string UiTextSettingsPath = "Assets/GameRes/Raw/UI/Fonts/BattleUITextSettings.asset";

        [MenuItem("BinGames/QA/自检/FG 昼夜与日历")]
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
            Line("\n[昼夜与日历] 游戏日、昼夜光照、太阳能接昼夜、日历 HUD 与日历面板、夜间亮度下限、倍速矩阵复查（FG7-ENV-01）");
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
            Light originalSun = RenderSettings.sun;
            Color originalAmbient = RenderSettings.ambientLight;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgdaynight-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                GameClock.ReloadTuning();
                DayNightCycle.ReloadTuning();
                // 从干净的本机设置开始（第一次入夜的引导钩子要能触发）；收尾还原玩家原来的设置。
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                HomeValleyPowerGrid.ResetForTests();
                PowerEnvironment.ResetForTests();
                CalendarService.ResetForTests();
                DayNightLighting.ResetForTests();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "昼夜 / 日历 / 光照在热更层（Editor 下 Mono JIT，真机 HybridCLR 解释执行），电网内核在 AOT；真机另测（FG15-SYS-02）");

                Step(CheckData);
                Step(CheckGlyphs);
                Step(CheckCycleMath);
                Step(CheckSolar);
                Step(CheckNightfall);
                Step(CheckTimingMatrix);
                Step(CheckObservedEqualsUnobserved);
                Step(CheckSaveLoad);
                Step(CheckSettings);
                Step(CheckLighting);
                Step(CheckHudAndCalendar);
                Step(CheckCalendarSources);
                Step(CheckPauseMenuDisplay);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"昼夜与日历自检抛异常：{e}");
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
                DayNightLighting.ResetForTests();
                RenderSettings.sun = originalSun;
                RenderSettings.ambientLight = originalAmbient;
                CalendarService.ResetForTests();
                DayNightCycle.ResetForTests();
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
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                CalendarPanelUIToolkit.InWorldOverrideForTests = false;
                CalendarPanelUIToolkit.Close();
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
            Line($"  · [昼夜与日历] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static double Hour => GameClock.HourSeconds;

        /// <summary>新世界（家园、种子地形），昼夜走正式入口接到太阳能，核心配电范围里放一座太阳能阵列（按种子地形找空地，B25）。</summary>
        private static CampaignState NewWorld(int seed, bool observe, out BuildingRecord solar)
        {
            HomeValleyPowerGrid.ResetForTests();
            PowerEnvironment.ResetForTests();
            DayNightCycle.ResetForTests();
            _seq = 7700;
            CampaignState s = FgProductionSelfCheck.NewWorld(seed, observe, 300);
            DayNightCycle.Install();
            solar = Place(s, Solar, 8f, 18f);
            return s;
        }

        private static BuildingRecord Place(CampaignState s, string typeId, float from, float to)
        {
            GridCell? at = FgProductionSelfCheck.FindFree(s, typeId, from, to);
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

        /// <summary>测试捷径：把统一时钟拨到第 <paramref name="day"/> 日 <paramref name="hour"/> 点（等于读一个更晚时刻的存档，见 <see cref="GameClock.SkipForTests"/>）。</summary>
        private static void SkipTo(CampaignState s, int day, double hour)
        {
            long target = GameClock.TicksFor(GameClock.SecondsAt(day, hour));
            if (target > GameClock.Ticks)
            {
                GameClock.SkipForTests(s, target - GameClock.Ticks);
            }
        }

        /// <summary>接了电网、但这一刻没电（缺电 / 断电）的建筑数。</summary>
        private static int Unpowered(CampaignState s) => (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Count(b => b != null && b.PowerState != BuildingPowerState.Powered && b.PowerState != BuildingPowerState.NotApplicable);

        private static float SolarOut(CampaignState s, BuildingRecord b) => b != null ? HomeValleyPowerGrid.AvailableSupplyOf(s, b.BuildingId) : -1f;

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private sealed class Keys : IInputReader
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

        private static void PressAlt(Keys keys, GameActionId action)
        {
            keys.Held.Add(KeyCode.LeftAlt);
            keys.Down = GameSettings.KeyBindings.GetKey(action);
            UiKitInputPump.ProcessLibraryKeys();
            keys.Down = KeyCode.None;
            keys.Held.Clear();
            InputRouter.DebugClearConsumedKeys();
        }

        private static bool Click(Button b)
        {
            if (b?.clickable == null || !b.enabledInHierarchy)
            {
                return false;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        // ── D1 数据 ───────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            string[] tuning = { "env.daynight.night_start_hour", "env.daynight.night_hours", "env.daynight.twilight_hours", "env.calendar.days",
                "env.light.night_tint_r", "env.light.night_tint_g", "env.light.night_tint_b", "env.light.apply_epsilon" };
            bool tune = tuning.All(id => GridContent.TryGetTuning(id, out _)) && GridContent.TryGetTuning("env.daynight.night_hours", out float nh) && Math.Abs(nh - 7.2f) < 1e-4f
                        && GridContent.TryGetTuning("env.daynight.night_start_hour", out float ns) && Math.Abs(ns - 20.4f) < 1e-4f
                        && GridContent.TryGetTuning("clock.day_seconds", out float ds) && Math.Abs(ds - 1200f) < 1e-3f;
            float lo = 0f, hi = 0f, de = 0f;
            bool ui = UiTuningValues.TryGet("env.night_brightness_min", out lo) && UiTuningValues.TryGet("env.night_brightness_max", out hi)
                      && UiTuningValues.TryGet("env.night_brightness_default", out de) && lo > 0f && lo <= de && de <= hi && hi <= 1f;
            // 文本：源数据（fgdata_env.ENV_TEXTS）逐条在运行时表里，中英都有、内容一致。
            string root = FgProductionSelfCheck.LocateRepo();
            (int code, string output) = FgProductionSelfCheck.RunPython(root,
                "-c \"import sys;sys.path.insert(0,'tools/cell_tables');import fgdata_env as e;print('\\n'.join('TX\\t%s\\t%s\\t%s' % (k, zh.replace('\\n','\\\\n'), en.replace('\\n','\\\\n')) for (k, zh, en) in e.ENV_TEXTS))\"");
            var rows = output.Replace("\r", string.Empty).Split('\n').Where(l => l.StartsWith("TX\t", StringComparison.Ordinal)).Select(l => l.Split('\t')).ToList();
            var bad = new List<string>();
            EnvTextKeys.Clear();
            foreach (string[] f in rows)
            {
                EnvTextKeys.Add(f[1]);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                string zh = GameText.Get(f[1]).Replace("\n", "\\n");
                GameSettings.SetLanguage(GameLanguage.En);
                string en = GameText.Get(f[1]).Replace("\n", "\\n");
                if (zh != f[2] || en != f[3])
                {
                    bad.Add(f[1]);
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool key = InputActionCatalog.TryGet(GameActionId.OpenCalendar, out InputActionDef def) && def.Status == InputActionStatus.Wired
                       && InputActionCatalog.DefaultChord(GameActionId.OpenCalendar).Key == KeyCode.L && (InputActionCatalog.DefaultChord(GameActionId.OpenCalendar).Mods & InputModifier.Alt) != 0;
            bool hooks = GuidanceHooks.Known.Contains(GuidanceHooks.EnvFirstNight) && GuidanceHooks.Known.Contains(GuidanceHooks.EnvCalendarFirstOpen)
                         && GuidanceHooks.Known.Distinct().Count() == GuidanceHooks.Known.Count;
            MechanicCodexEntry codex = MechanicCodex.Find("codex.env.daynight");
            bool codexOk = codex != null && codex.Hooks.Contains(GuidanceHooks.EnvFirstNight) && codex.Hooks.Contains(GuidanceHooks.EnvCalendarFirstOpen);
            Expect(tune && ui && code == 0 && rows.Count >= 35 && bad.Count == 0 && key && hooks && codexOk,
                $"D1 数据：昼夜 / 日历 / 光照 8 项调参（夜长 7.2 游戏小时 = 1x 下 6 分钟、20:24 入夜、1 游戏日 1200 秒）、夜间亮度下限范围 {lo:P0}～{hi:P0} 默认 {de:P0}；" +
                $"{rows.Count} 条新文本与源数据逐条一致、中英都有" + (bad.Count == 0 ? string.Empty : "（不符：" + string.Join("、", bad.Take(6)) + "）") +
                $"；日历键默认 Alt+L 已接入（{key}）；2 个引导钩子；图鉴“昼夜与日历”（{codexOk}）" + (code == 0 ? string.Empty : "；python：" + FgProductionSelfCheck.Tail(output)));
        }

        // ── D2 字形都在 UI 字体里 ─────────────────────────────────────────────────

        /// <summary>
        /// 全部 UiKit 面板用 BattleHudPanelSettings → BattleUITextSettings（默认字体 NotoSansSC SDF，动态图集，无回退字体，缺字不报警）。
        /// 字体里没有的字形会静默显示成缺字方框，所以阶段字形、日历事件字形、本 Story 全部文本（中英）逐字到字体（含回退）里核对。
        /// 先查字体资产已有的字符表，再到源字体文件里查（只读：FontEngine 读 cmap，不往图集里加字，不改 GameRes）。
        /// </summary>
        private static void CheckGlyphs()
        {
            var settings = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(UiTextSettingsPath);
            var fonts = new List<UnityEngine.TextCore.Text.FontAsset>();
            if (settings != null && settings.defaultFontAsset != null)
            {
                fonts.Add(settings.defaultFontAsset);
            }
            if (settings?.fallbackFontAssets != null)
            {
                fonts.AddRange(settings.fallbackFontAssets.Where(f => f != null));
            }
            var sources = new List<Font>();
            foreach (UnityEngine.TextCore.Text.FontAsset fa in fonts)
            {
                if (new SerializedObject(fa).FindProperty("m_SourceFontFile")?.objectReferenceValue is Font src)
                {
                    sources.Add(src);
                }
            }
            bool Has(char c)
            {
                foreach (UnityEngine.TextCore.Text.FontAsset fa in fonts)
                {
                    if (fa.HasCharacter(c, false, false))
                    {
                        return true;
                    }
                }
                foreach (Font src in sources)
                {
                    if (UnityEngine.TextCore.LowLevel.FontEngine.LoadFontFace(src) == UnityEngine.TextCore.LowLevel.FontEngineError.Success
                        && UnityEngine.TextCore.LowLevel.FontEngine.TryGetGlyphIndex(c, out uint index) && index != 0)
                    {
                        return true;
                    }
                }
                return false;
            }
            var text = new StringBuilder();
            foreach (DayPhase p in new[] { DayPhase.Day, DayPhase.Dusk, DayPhase.Night, DayPhase.Dawn })
            {
                text.Append(DayNightCycle.PhaseGlyph(p));
            }
            foreach (CalendarKind k in new[] { CalendarKind.Night, CalendarKind.Raid, CalendarKind.SilentNight, CalendarKind.Weather })
            {
                text.Append(CalendarPanelUIToolkit.Glyph(k));
            }
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string key in EnvTextKeys)
                {
                    text.Append(GameText.Get(key));
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            var missing = new SortedSet<char>();
            int checkedChars = 0;
            foreach (char c in text.ToString().Distinct())
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c) || char.IsSurrogate(c))
                {
                    continue;
                }
                checkedChars++;
                if (!Has(c))
                {
                    missing.Add(c);
                }
            }
            // 核对器自身不是恒真：原来用的 ☾（U+263E）/ ✦（U+2726）确实查得出缺字。
            bool detects = !Has('☾') && !Has('✦');
            string Hex(IEnumerable<char> cs) => string.Join(" ", cs.Select(c => $"U+{(int)c:X4}"));
            Expect(fonts.Count > 0 && sources.Count > 0 && EnvTextKeys.Count > 0 && missing.Count == 0 && detects,
                $"D2 字形都在 UI 字体里（FGR-ENV-002 / B15：夜里的阶段字形不能是缺字方框）：{fonts.Count} 个字体资产（含回退）、{sources.Count} 个源字体，" +
                $"阶段字形 {DayNightCycle.PhaseGlyph(DayPhase.Day)}{DayNightCycle.PhaseGlyph(DayPhase.Dusk)}{DayNightCycle.PhaseGlyph(DayPhase.Night)}{DayNightCycle.PhaseGlyph(DayPhase.Dawn)}、" +
                $"日历字形 {CalendarPanelUIToolkit.Glyph(CalendarKind.Night)}{CalendarPanelUIToolkit.Glyph(CalendarKind.Raid)}{CalendarPanelUIToolkit.Glyph(CalendarKind.SilentNight)}{CalendarPanelUIToolkit.Glyph(CalendarKind.Weather)}" +
                $" 与 {EnvTextKeys.Count} 条文本（中英）共 {checkedChars} 个不同字符逐字核对，缺字 {missing.Count} 个" + (missing.Count == 0 ? string.Empty : "（" + Hex(missing.Take(12)) + "）") +
                $"；核对器能查出 U+263E / U+2726 缺字（{detects}）");
        }

        // ── N1 昼夜曲线 ───────────────────────────────────────────────────────────

        private static void CheckCycleMath()
        {
            GameClock.ResetSession();
            DayNightCycle.ReloadTuning();
            double day = GameClock.DaySeconds;
            // 第 1 日 06:00 开局（clock.start_hour）：白天、光照 1。
            bool start = GameClock.DayOf(0) == 1 && DayNightCycle.PhaseAt(0) == DayPhase.Day && Mathf.Approximately(DayNightCycle.Daylight(0), 1f);
            // 扫一整天（每游戏秒）：夜晚 360 秒（6 分钟）、白天 840 秒（14 分钟，含黄昏 / 黎明各 50 秒）；光照连续（相邻一秒的变化 < 4%），夜晚恒为 0。
            int night = 0, dusk = 0, dawn = 0, dayCount = 0;
            float maxStep = 0f;
            bool nightZero = true;
            float prev = DayNightCycle.Daylight(0);
            for (int t = 0; t < (int)day; t++)
            {
                DayPhase p = DayNightCycle.PhaseAt(t);
                float l = DayNightCycle.Daylight(t);
                switch (p)
                {
                    case DayPhase.Night: night++; nightZero &= l == 0f; break;
                    case DayPhase.Dusk: dusk++; break;
                    case DayPhase.Dawn: dawn++; break;
                    default: dayCount++; break;
                }
                maxStep = Math.Max(maxStep, Math.Abs(l - prev));
                prev = l;
            }
            bool split = Math.Abs(night - 360) <= 1 && Math.Abs(dusk + dawn + dayCount - 840) <= 1 && Math.Abs(dusk - 50) <= 1 && Math.Abs(dawn - 50) <= 1 && nightZero && maxStep < 0.04f;
            // 关键时刻：19:54 黄昏中（0 < 光照 < 1）、20:24 入夜、03:36 黎明开始、04:36 白天；第 1 日 23:00 = 第 1 日、第 2 日 01:00 = 第 2 日。
            double t1954 = GameClock.SecondsAt(1, 19.9), t2024 = GameClock.SecondsAt(1, 20.4) + 0.5, t0336 = GameClock.SecondsAt(2, 3.6) + 0.5, t0436 = GameClock.SecondsAt(2, 4.6) + 0.5;
            float mid = DayNightCycle.Daylight(t1954);
            bool keys = DayNightCycle.PhaseAt(t1954) == DayPhase.Dusk && mid > 0.1f && mid < 0.9f && DayNightCycle.IsNight(t2024) && DayNightCycle.PhaseAt(t0336) == DayPhase.Dawn
                        && DayNightCycle.PhaseAt(t0436) == DayPhase.Day && GameClock.DayOf(GameClock.SecondsAt(1, 23)) == 1 && GameClock.DayOf(GameClock.SecondsAt(2, 1)) == 2
                        && GameClock.FormatHour(DayNightCycle.NightStartHour) == "20:24" && GameClock.FormatHour(DayNightCycle.NightEndHour) == "03:36";
            // 下一次变化：开局 06:00 → 14.4 游戏小时后入夜（1x 下 12 分钟）；夜里 → 夜晚结束。
            double toNight = DayNightCycle.NextChange(0, out bool n1);
            double toDay = DayNightCycle.NextChange(t2024, out bool n2);
            bool next = n1 && Math.Abs(toNight - 14.4 * Hour) < 0.01 && !n2 && Math.Abs(toDay - (7.2 * Hour - 0.5)) < 0.01;
            // 夜晚区间与日历一致：第 1 日的夜晚从 20:24 到第 2 日 03:36。
            DayNightCycle.NightOfDay(1, out double ns, out double ne);
            bool interval = Math.Abs(ns - GameClock.SecondsAt(1, 20.4)) < 1e-6 && Math.Abs(ne - GameClock.SecondsAt(2, 3.6)) < 1e-6;
            Expect(start && split && keys && next && interval,
                $"N1 昼夜曲线（FGR-ENV-001 / 003）：1x 下一天 {day / 60:0} 分钟——夜晚 {night} 秒、白天 {dusk + dawn + dayCount} 秒（黄昏 {dusk} / 黎明 {dawn}）；夜晚光照恒为 0，相邻一秒最大变化 {maxStep:P1}；" +
                $"19:54 黄昏光照 {mid:P0}，20:24 入夜，03:36 黎明，04:36 白天；开局 {toNight / Hour:0.0} 游戏小时后入夜，入夜后 {toDay / Hour:0.00} 小时天亮");

            // 负向：调参不合法（夜长 30 小时 / 入夜 -1 点 / 夜长加两段过渡占满一天）→ 回退规格初值 20.4 / 7.2 / 1 并告警（不静默），合法的注入照常生效。
            var bad = new List<string>();
            int warn0 = DayNightCycle.InvalidTuningCount;
            (string Id, float V)[][] cases =
            {
                new[] { ("env.daynight.night_hours", 30f) },
                new[] { ("env.daynight.night_start_hour", -1f) },
                new[] { ("env.daynight.night_hours", 20f), ("env.daynight.twilight_hours", 3f) },
            };
            try
            {
                foreach ((string Id, float V)[] c in cases)
                {
                    DayNightCycle.TuningOverrideForTests = id => c.Where(x => x.Id == id).Select(x => (float?)x.V).FirstOrDefault();
                    DayNightCycle.ReloadTuning();
                    if (Math.Abs(DayNightCycle.NightStartHour - 20.4) > 1e-9 || Math.Abs(DayNightCycle.NightHours - 7.2) > 1e-9 || GameClock.FormatHour(DayNightCycle.NightEndHour) != "03:36")
                    {
                        bad.Add(string.Join("+", c.Select(x => x.Id + "=" + x.V)));
                    }
                }
                int warned = DayNightCycle.InvalidTuningCount - warn0;
                DayNightCycle.TuningOverrideForTests = id => id == "env.daynight.night_start_hour" ? 21f : (float?)null;
                DayNightCycle.ReloadTuning();
                bool validApplied = Math.Abs(DayNightCycle.NightStartHour - 21.0) < 1e-9 && DayNightCycle.InvalidTuningCount - warn0 == cases.Length;
                Expect(bad.Count == 0 && warned == cases.Length && validApplied,
                    $"N1 负向：注入 {cases.Length} 组不合法调参，都回退到规格初值 20:24 入夜 / 夜长 7.2 小时并告警 {warned} 次" + (bad.Count == 0 ? string.Empty : "（未回退：" + string.Join("、", bad) + "）") +
                    $"；合法注入（21 点入夜）照常生效（{validApplied}）");
            }
            finally
            {
                DayNightCycle.TuningOverrideForTests = null;
                DayNightCycle.ReloadTuning();
            }
        }

        // ── N2 太阳能接昼夜 ───────────────────────────────────────────────────────

        private static void CheckSolar()
        {
            CampaignState s = NewWorld(7101, true, out BuildingRecord sol);
            if (sol == null)
            {
                Fail("N2 核心附近放不下太阳能阵列");
                return;
            }
            bool installed = DayNightCycle.Installed;
            FgProductionSelfCheck.Seconds(1.1f);
            float day = SolarOut(s, sol);
            float capDay = s.PowerCapacity;
            bool tipDay = HomeValleyPowerGrid.TryDescribeBuilding(s, sol, out string t1) && t1.Contains("白天");
            SkipTo(s, 1, 19.9);
            FgProductionSelfCheck.Seconds(1.1f);
            float dusk = SolarOut(s, sol);
            SkipTo(s, 1, 20.6);
            FgProductionSelfCheck.Seconds(1.1f);
            float night = SolarOut(s, sol);
            float capNight = s.PowerCapacity;
            bool tipNight = HomeValleyPowerGrid.TryDescribeBuilding(s, sol, out string t2) && t2.Contains("夜晚：不发电");
            string nightIcon = HomeValleyController.StateIconFor(sol);
            SkipTo(s, 2, 4.7);
            FgProductionSelfCheck.Seconds(1.1f);
            float back = SolarOut(s, sol);
            Expect(installed && Mathf.Approximately(day, 60f) && dusk > 5f && dusk < 55f && Mathf.Approximately(night, 0f) && Mathf.Approximately(capDay - capNight, 60f)
                   && Mathf.Approximately(back, 60f) && tipDay && tipNight && nightIcon == ContentIcons.StateIdle,
                $"N2 太阳能接昼夜（FG-GAP-096，正式入口 DayNightCycle.Install）：白天 {day}、19:54 黄昏 {dusk:0.#}、20:36 夜里 {night}（电网发电少 {capDay - capNight}，悬停“夜晚：不发电”，头顶待机标记）、次日 04:42 恢复 {back}");
        }

        // ── N3 跨过入夜 ───────────────────────────────────────────────────────────

        private static void CheckNightfall()
        {
            CampaignState s = NewWorld(7102, false, out _);
            bool hookBefore = GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstNight);
            SkipTo(s, 1, 20.38);
            int rev0 = DayNightCycle.Revision;
            int falls0 = DayNightCycle.NightfallCount;
            FgProductionSelfCheck.Seconds(2f); // 跨过 20:24
            bool crossed = DayNightCycle.NightfallCount == falls0 + 1 && DayNightCycle.Revision == rev0 + 1 && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvFirstNight);
            FgProductionSelfCheck.Seconds(2f); // 夜里：不再变
            bool steady = DayNightCycle.NightfallCount == falls0 + 1 && DayNightCycle.Revision == rev0 + 1;
            SkipTo(s, 2, 3.58);
            FgProductionSelfCheck.Seconds(2f); // 跨过 03:36：天亮（版本号再 +1，入夜计数不变）
            bool dawn = DayNightCycle.Revision == rev0 + 2 && DayNightCycle.NightfallCount == falls0 + 1;
            Expect(!hookBefore && crossed && steady && dawn,
                $"N3 跨过入夜（不被观察的家园同样）：20:24 那一游戏秒版本号 +1、入夜计数 +1、发出第一次入夜引导钩子 {GuidanceHooks.EnvFirstNight}；夜里不再变；03:36 天亮版本号再 +1" +
                $"（之前未见钩子 {!hookBefore}、跨过 {crossed}、夜里不变 {steady}、天亮 {dawn}；版本 {rev0}→{DayNightCycle.Revision}，入夜 {falls0}→{DayNightCycle.NightfallCount}）");
        }

        // ── T1 / T2 FGT-ENV-001 暂停与倍速、观察一致 ─────────────────────────────────

        /// <summary>T1 / T2 场景的新连锁：白天电够、精炼炉有电在生产；入夜太阳能归零 → 电网发电 < 用电 → 按优先级断电（本 Story 引入，各既有矩阵都没接昼夜）。</summary>
        private const int ScenarioSeconds = 80;
        private static string _lastFurnace = string.Empty;
        private static bool _lastFurnaceChain;

        /// <summary>
        /// 同一世界从第 1 日 19:10 起跑 2 + 80 游戏秒（白天 → 黄昏 → 夜晚），返回整份战役状态的 JSON。场景：开局家园（开局发电机是坏的）+ 一座太阳能阵列 + 一座正在烧废料的精炼炉（输入带有夹具送料、输出带收走）——
        /// 覆盖统一时钟、昼夜光照、太阳能递减到 0、电网结算、入夜缺电按优先级断电与生产计时；其余计时系统（研究、解析、熔合、破译、黑匣子、突袭导演、攻城等）的倍速矩阵在各自 Story 的自检段里（清单见证据第 4 节）。
        /// </summary>
        private static string RunScenario(int seed, bool observe, float speed, bool pauseFirst, out bool pausedHeld, out int frames)
        {
            CampaignState s = NewWorld(seed, observe, out BuildingRecord sol);
            pausedHeld = true;
            frames = 0;
            _lastFurnace = string.Empty;
            _lastFurnaceChain = false;
            if (sol == null)
            {
                return "放不下";
            }
            // 精炼炉（核心配电范围里找 7×3 空地）+ 输出口东侧 2 格传送带 → 夹具“下游仓库”（产出收走，不会堵塞停机），炉里预放 40 废料（10 炉 = 60 游戏秒的活）。
            GridCell? area = FgProductionSelfCheck.FindArea(s, 10, 3, 8f, 16f);
            BuildingRecord furnace = null;
            if (area.HasValue)
            {
                GridCell pivot = FgProductionSelfCheck.At(area.Value, 3, 1);
                furnace = FgProductionSelfCheck.Built(s, "refinery_furnace", "t1furnace", pivot);
                GridCell inBelt = FgProductionSelfCheck.At(pivot, -3, 0);
                GridCell outBelt = FgProductionSelfCheck.At(pivot, 2, 0);
                if (furnace != null && FgProductionSelfCheck.Belts(s, inBelt, BinGames.Sim.Logistics.BeltDir.East, 2)
                    && FgProductionSelfCheck.Belts(s, outBelt, BinGames.Sim.Logistics.BeltDir.East, 2))
                {
                    // 输入口西侧 2 格传送带由夹具“上游仓库”送废料（够烧整个窗口），输出收走。
                    FgProductionSelfCheck.TestSource(s, inBelt, "scrap", 400);
                    FgProductionSelfCheck.TestSink(s, FgProductionSelfCheck.At(outBelt, 2, 0));
                }
                else
                {
                    furnace = null;
                }
                FgProductionSelfCheck.Resync(s);
            }
            ProductionService.Producer fp = furnace != null ? FgProductionSelfCheck.P(s, furnace) : null;
            if (fp == null || !ProductionService.TrySetRecipe(s, furnace.BuildingId, "alloy_scrap", out _))
            {
                return "放不下";
            }
            ProductionService.Add(ref fp.Rec.In, "scrap", 40);
            // 19:10 起（还是白天、太阳能满发）：先跑 2 游戏秒，炉子有电在烧。
            SkipTo(s, 1, 19.1667);
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            BuildingPowerState powerStart = furnace.PowerState;
            int cutStart = Unpowered(s);
            bool enoughStart = s.PowerCapacity >= s.PowerDemand;
            string stateStart = ProductionService.StateText(fp) + $"，电网发电 {s.PowerCapacity:0.#} / 用电 {s.PowerDemand:0.#}，太阳能 {SolarOut(s, sol):0.#}";
            if (pauseFirst)
            {
                GameClock.SetPaused(true);
                WorldSimulation.SyncAllForSave();
                string p0 = JsonUtility.ToJson(s);
                long t0 = GameClock.Ticks;
                float l0 = DayNightCycle.Daylight(GameClock.GameSeconds);
                for (int i = 0; i < 120; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                }
                WorldSimulation.SyncAllForSave();
                pausedHeld = JsonUtility.ToJson(s) == p0 && GameClock.Ticks == t0 && DayNightCycle.Daylight(GameClock.GameSeconds) == l0;
                GameClock.SetPaused(false);
            }
            GameClock.SetSpeed(speed);
            long target = GameClock.Ticks + GameClock.StepHz * ScenarioSeconds;
            while (GameClock.Ticks < target && frames < 60 * 400)
            {
                WorldSimulation.Frame(1f / 60f, target);
                frames++;
            }
            GameClock.SetSpeed(1f);
            WorldSimulation.SyncAllForSave();
            int cutEnd = Unpowered(s);
            _lastFurnaceChain = powerStart == BuildingPowerState.Powered && enoughStart && cutStart == 0 && s.PowerCapacity < s.PowerDemand && cutEnd > 0;
            _lastFurnace = $"精炼炉 19:12 {powerStart}（{stateStart}，断电 {cutStart} 座）→ 20:48 {furnace.PowerState}（{ProductionService.StateText(fp)}，电网发电 {s.PowerCapacity:0.#} / 用电 {s.PowerDemand:0.#}，太阳能 {SolarOut(s, sol):0.#}，按优先级断电 {cutEnd} 座）";
            return Masked(JsonUtility.ToJson(s)) + "|light=" + R(DayNightCycle.Daylight(GameClock.GameSeconds)) + "|solar=" + R(SolarOut(s, sol)) + "|phase=" + DayNightCycle.PhaseAt(GameClock.GameSeconds);
        }

        /// <summary>开局时随机生成的编号（Guid，32 位十六进制：旧细胞阶段的芯片编号等）、通知的真实时间戳（*Utc）与游戏计时无关；模拟步之外发出的通知（开局搭场景、步末的目标重算）按镜头所在地点记地点（观察 / 不观察时不同，FG-GAP-117），比较前抹掉。</summary>
        private static string Masked(string json) => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(json, "[0-9a-f]{32}", "#"), "\"[A-Za-z]*Utc\":\"[^\"]*\"", "\"Utc\":\"#\""),
            "\"HasLocation\":(true|false),\"RegionId\":\"[^\"]*\"", "\"HasLocation\":$1,\"RegionId\":\"#\"");

        private static string FirstDiff(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < n && a[i] == b[i])
            {
                i++;
            }
            int from = Math.Max(0, i - 80);
            return $"第 {i} 字符：…{a.Substring(from, Math.Min(160, a.Length - from))}… ≠ …{b.Substring(from, Math.Min(160, b.Length - from))}…";
        }

        private static void CheckTimingMatrix()
        {
            string reference = null;
            bool same = true;
            bool paused = true;
            bool proportional = true;
            string diff = string.Empty;
            var shown = new List<string>();
            bool chain = true;
            string furnace = string.Empty;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                string snap = RunScenario(7111, true, speed, true, out bool held, out int frames);
                paused &= held;
                chain &= _lastFurnaceChain;
                furnace = _lastFurnace;
                reference ??= snap;
                if (snap != reference && diff.Length == 0)
                {
                    diff = $"{speed}x 与 0.5x 不同：" + FirstDiff(reference, snap);
                }
                same &= snap == reference;
                // 80 游戏秒 = 4800 步；每帧 1/60 真实秒 → 需要 4800 / 倍速 帧（同比例变快变慢）。
                proportional &= Math.Abs(frames * speed - ScenarioSeconds * GameClock.StepHz) <= 3f;
                shown.Add($"{speed}x {frames} 帧");
            }
            Expect(paused && same && proportional && chain && reference != "放不下" && reference.Contains("phase=Night"),
                $"T1 FGT-ENV-001（FGR-ARC-009 倍速矩阵复查：统一时钟、昼夜、太阳能、电网、入夜缺电断电与生产计时）：暂停中 120 帧整份战役状态、时间轴、光照都不动；{string.Join(" / ", shown)} 跑同样的 {ScenarioSeconds} 游戏秒" +
                $"（19:12 白天 → 黄昏 → 20:48 夜里：太阳能递减到 0、电网结算、入夜缺电按优先级断电——{furnace}），整份战役状态 JSON 逐字段一致，所需真实帧与倍速成反比；" +
                "其余计时系统的倍速矩阵见各自 Story 的自检段（证据第 4 节清单）" + (diff.Length == 0 ? string.Empty : "\n" + diff));
        }

        private static void CheckObservedEqualsUnobserved()
        {
            string seen = RunScenario(7112, true, 1f, false, out _, out _);
            string unseen = RunScenario(7112, false, 1f, false, out _, out _);
            Expect(seen == unseen && seen != "放不下",
                "T2 同一家园在观察与不观察时从白天跨过黄昏进入夜晚（含入夜缺电按优先级断电、正在生产的精炼炉），整份战役状态逐字段一致（FGR-BASE-021：光照只由游戏时钟算出）" + (seen == unseen ? string.Empty : "\n" + FirstDiff(seen, unseen)));
        }

        // ── S1 存读档 ─────────────────────────────────────────────────────────────

        private static string Snap(CampaignState s, BuildingRecord sol)
        {
            double t = GameClock.GameSeconds;
            return $"t={R(t)}|day={GameClock.DayOf(t)}|clock={s.Clock?.Day}/{R(s.Clock?.GameSeconds ?? -1)}|phase={DayNightCycle.PhaseAt(t)}|light={R(DayNightCycle.Daylight(t))}" +
                   $"|solar={R(SolarOut(s, sol))}|cap={R(s.PowerCapacity)}";
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(7121, true, out BuildingRecord sol);
            if (sol == null)
            {
                Fail("S1 放不下太阳能阵列");
                return;
            }
            SkipTo(s, 1, 19.8);
            FgProductionSelfCheck.Seconds(5f); // 黄昏中
            string before = Snap(s, sol);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            FgProductionSelfCheck.Seconds(60f); // 跨过入夜
            string continuous = Snap(s, sol);
            string loaded = null;
            string after = null;
            CampaignState l = null;
            string solId = sol.BuildingId;
            for (int round = 0; round < 2; round++)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                if (!rr.Success)
                {
                    Fail("S1 读档失败：" + rr.Message);
                    return;
                }
                l = rr.State;
                CampaignSession.Set(Slot, l);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                BuildingRecord lsol = l.BuildingRecords.FirstOrDefault(b => b.BuildingId == solId);
                string ld = Snap(l, lsol);
                FgProductionSelfCheck.Seconds(60f);
                string af = Snap(l, lsol);
                if (round == 0)
                {
                    loaded = ld;
                    after = af;
                }
                else if (ld != loaded || af != after)
                {
                    after = "第二次读档不同：" + af;
                }
            }
            Expect(save.Success && loaded == before && after == continuous && DayNightCycle.Installed,
                $"S1 真文件存读档：黄昏中存档（{before}），读档后游戏时刻 / 阶段 / 光照 / 太阳能 / 电网逐位一致；接着跑 60 游戏秒跨过入夜，与不存档一直跑一致；同一存档读两次一致（昼夜没有新的存档状态，全由游戏秒算出）" +
                (loaded == before ? string.Empty : $"\n存档前 {before}\n读档后 {loaded}") + (after == continuous ? string.Empty : $"\n一直跑 {continuous}\n读档跑 {after}"));
        }

        // ── S2 夜间亮度下限设置 ───────────────────────────────────────────────────

        private static void CheckSettings()
        {
            float lo = GameSettings.NightBrightnessMin, hi = GameSettings.NightBrightnessMax, de = GameSettings.NightBrightnessDefault;
            PlayerPrefs.DeleteKey(SettingsPrefsKey);
            GameSettings.Load();
            bool fresh = Mathf.Approximately(GameSettings.NightBrightnessFloor, de);
            GameSettings.SetNightBrightnessFloor(0.35f);
            GameSettings.Load();
            bool persisted = Mathf.Approximately(GameSettings.NightBrightnessFloor, 0.35f) && PlayerPrefs.GetString(SettingsPrefsKey, string.Empty).Contains("NightBrightnessFloor");
            // 旧设置（没有这个字段）→ 按默认补齐，其它设置不丢。
            string json = PlayerPrefs.GetString(SettingsPrefsKey, string.Empty);
            string legacy = System.Text.RegularExpressions.Regex.Replace(json, "\"NightBrightnessFloor\":[-0-9.eE]+,?", string.Empty);
            PlayerPrefs.SetString(SettingsPrefsKey, legacy);
            GameSettings.Load();
            bool legacyOk = !legacy.Contains("NightBrightnessFloor") && Mathf.Approximately(GameSettings.NightBrightnessFloor, de);
            // 越界钳回；恢复默认。
            GameSettings.SetNightBrightnessFloor(5f);
            bool clampHi = Mathf.Approximately(GameSettings.NightBrightnessFloor, hi);
            GameSettings.SetNightBrightnessFloor(-3f);
            bool clampLo = Mathf.Approximately(GameSettings.NightBrightnessFloor, lo);
            GameSettings.ResetNightBrightnessFloor();
            bool reset = Mathf.Approximately(GameSettings.NightBrightnessFloor, de);
            // 设置是本机偏好，不进战役存档（切换 / 删除存档不影响）。
            bool notInSave = !JsonUtility.ToJson(CampaignState.CreateNew("fgdn-probe", "Standard", 1)).Contains("NightBrightness");
            Expect(fresh && persisted && legacyOk && clampHi && clampLo && reset && notInSave,
                $"S2 夜间亮度下限（FGR-ENV-003 设置）：默认 {de:P0}；改成 35% 写进本机设置、重读一致；旧设置没有这个字段 → 按默认补齐；越界钳到 {lo:P0}～{hi:P0}；恢复默认；不进战役存档");
        }

        // ── L1 场景光照 ───────────────────────────────────────────────────────────

        private static void CheckLighting()
        {
            DayNightLighting.ResetForTests();
            var go = new GameObject("__FgDayNightSun") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                // 没有方向光：不报错、不写光照。
                RenderSettings.sun = null;
                go.SetActive(false);
                DayNightLighting.RealTimeForTests = () => 0f;
                bool noSun = !DayNightLighting.Apply(0f) && DayNightLighting.Sun == null;
                go.SetActive(true);
                Light sun = go.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 1.2f;
                sun.color = new Color(1f, 0.95f, 0.9f, 1f);
                RenderSettings.sun = sun;
                var ambient = new Color(0.2f, 0.22f, 0.26f, 1f);
                RenderSettings.ambientLight = ambient;
                DayNightLighting.RealTimeForTests = () => 5f; // 过了“每秒最多找一次”的间隔
                GameSettings.ResetNightBrightnessFloor();
                float floor = GameSettings.NightBrightnessFloor;
                bool dayWrote = DayNightLighting.Apply(1f);
                bool day = Mathf.Approximately(sun.intensity, 1.2f) && sun.color == new Color(1f, 0.95f, 0.9f, 1f) && DayNightLighting.Sun == sun && Mathf.Approximately(DayNightLighting.Brightness, 1f);
                DayNightLighting.Apply(0f);
                bool night = Mathf.Approximately(sun.intensity, 1.2f * floor) && sun.color.b > sun.color.r && Mathf.Approximately(RenderSettings.ambientLight.r, ambient.r * floor);
                int writes = DayNightLighting.ApplyCount;
                bool skip = !DayNightLighting.Apply(0f) && !DayNightLighting.Apply(0.0005f) && DayNightLighting.ApplyCount == writes;
                DayNightLighting.Apply(0.5f);
                bool half = Mathf.Approximately(sun.intensity, 1.2f * DayNightLighting.BrightnessFor(0.5f, floor));
                // 玩家把下限调到 30% / 100%：夜里分别更暗 / 完全不暗（设置版本号变了立即重写）。
                GameSettings.SetNightBrightnessFloor(0.3f);
                DayNightLighting.Apply(0f);
                bool darker = Mathf.Approximately(sun.intensity, 1.2f * 0.3f);
                GameSettings.SetNightBrightnessFloor(1f);
                DayNightLighting.Apply(0f);
                bool none = Mathf.Approximately(sun.intensity, 1.2f);
                GameSettings.ResetNightBrightnessFloor();
                // 离开世界：恢复场景原光照。
                DayNightLighting.Apply(0f);
                DayNightLighting.Tick(false);
                bool restored = Mathf.Approximately(sun.intensity, 1.2f) && sun.color == new Color(1f, 0.95f, 0.9f, 1f) && RenderSettings.ambientLight == ambient && Mathf.Approximately(DayNightLighting.Brightness, 1f);
                // 在世界里按统一时钟：拨到夜里 → Tick(true) 调暗。
                GameClock.ResetSession();
                GameClock.SkipForTests(null, GameClock.TicksFor(GameClock.SecondsAt(1, 22)));
                DayNightLighting.Tick(true);
                bool clockDriven = Mathf.Approximately(DayNightLighting.Brightness, floor) && Mathf.Approximately(sun.intensity, 1.2f * floor);
                Expect(noSun && dayWrote && day && night && skip && half && darker && none && restored && clockDriven,
                    $"L1 场景光照（FGR-ENV-003）：没有方向光不报错；白天原样；夜里亮度 = 下限 {floor:P0}（方向光偏冷色、环境光同比例）；黄昏按光照插值；亮度不变不写灯；下限 30% / 100% 立即生效；离开世界恢复原光照；在世界里跟统一时钟（22:00 → {DayNightLighting.Brightness:P0}）。只改场景光照，界面不受影响（UI Toolkit 面板不参与光照）");
            }
            finally
            {
                DayNightLighting.ResetForTests();
                GameClock.ResetSession();
                Object.DestroyImmediate(go);
            }
        }

        // ── U1 世界时间条日历行与日历面板 ───────────────────────────────────────────

        private static void CheckHudAndCalendar()
        {
            // 先卸掉前面各段留下的世界、清空战役：下面的“不在世界里”负向用例要真的在世界外跑。
            WorldSimulation.UnloadAll();
            CampaignSession.Clear();
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "WorldBar.uxml", out GameObject go);
            VisualElement croot = FgProductionSelfCheck.MountUxml(UiKitFolder + "CalendarPanel.uxml", out GameObject cgo);
            CalendarPanelUIToolkit.InWorldOverrideForTests = false;
            var keys = new Keys();
            try
            {
                var bar = go.AddComponent<WorldBarHudUIToolkit>();
                bar.BindView(root);
                var cal = cgo.AddComponent<CalendarPanelUIToolkit>();
                cal.BindView(croot);
                cal.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "CalendarDayRow.uxml"));
                // 负向：不在世界里（没有战役、没有载入的地点）——点日历行、按日历键都不打开，各给一次“不行”的提示音。
                InputRouter.Reset();
                InputRouter.DebugSetReader(keys);
                InputRouter.SetScope(InputScope.Strategy);
                bool outside = !GameRoot.AnyRegionActive && CampaignSession.Current == null && !CalendarPanelUIToolkit.InWorldOverrideForTests;
                int denied0 = Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied);
                bool clickedOutside = Click(bar.PhaseButton);
                bool openAfterClick = CalendarPanelUIToolkit.IsOpen;
                PressAlt(keys, GameActionId.OpenCalendar);
                bool openAfterKey = CalendarPanelUIToolkit.IsOpen;
                int deniedCues = Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied) - denied0;
                bool deniedOutside = outside && clickedOutside && !openAfterClick && !openAfterKey && deniedCues == 2;
                string deniedWhy = $"在世界外 {outside}、点到日历行 {clickedOutside}、点后打开 {openAfterClick}、按键后打开 {openAfterKey}、“不行”提示音 {deniedCues} 次";
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                CampaignState s = NewWorld(7131, true, out _);
                bar.Refresh();
                string dayRow = bar.PhaseText;
                bool dayOk = dayRow.Contains("☀") && dayRow.Contains("白天") && dayRow.Contains("14时24分后入夜") && !root.Q<Button>("WorldDayPhase").ClassListContains("wb-phase-night");
                // 夜里（跨过入夜后的下一个游戏分钟）：阶段、倒计时到天亮、夜里样式（字形 + 边框，不只靠颜色）。
                SkipTo(s, 1, 21.0);
                FgProductionSelfCheck.Seconds(1f);
                bar.Refresh();
                string nightRow = bar.PhaseText;
                bool nightOk = nightRow.Contains("●") && nightRow.Contains("夜晚") && nightRow.Contains("6时3") && nightRow.Contains("分后天亮") && root.Q<Button>("WorldDayPhase").ClassListContains("wb-phase-night")
                               && bar.BarVisible && bar.DayTimeText.Contains("21:0");
                // 突袭：开局宽限期过后排一波剧情突袭，推进到发出预警——日历上出现它的标记与说明（与预警条同一口径）。
                long grace = RaidDirectorService.GraceEndTick + RaidDirectorService.DayTicks(0.2);
                GameClock.SkipForTests(s, Math.Max(0, grace - GameClock.Ticks));
                WorldSimulation.StepMany(2);
                RaidDirectorService.RequestStoryRaid(s, "silent", 1);
                WorldSimulation.StepMany(2);
                RaidPlanRecord p = RaidDirectorService.Plans(s).OrderByDescending(x => x.Serial).FirstOrDefault();
                bool warned = p != null && FgProductionSelfCheck.StepUntil(() => p.State >= RaidDirectorService.StateScheduled, 120);
                if (warned)
                {
                    GameClock.SkipForTests(s, Math.Max(0, p.WarnTick - 2 - GameClock.Ticks));
                    warned = FgProductionSelfCheck.StepUntil(() => p.State >= RaidDirectorService.StateWarned, 20) && p.ArrivalTick > GameClock.Ticks;
                }
                CalendarPanelUIToolkit.InWorldOverrideForTests = true;
                // 点日历行打开（与日历键同一入口）。
                bool clicked = Click(bar.PhaseButton) && CalendarPanelUIToolkit.IsOpen && cal.PanelVisible && GameSettings.HasSeenGuidanceHook(GuidanceHooks.EnvCalendarFirstOpen);
                int days = CalendarService.Days;
                int today = GameClock.DayOf(GameClock.GameSeconds);
                int raidDay = p != null && p.ArrivalTick > 0 ? GameClock.DayOf(p.ArrivalTick / (double)GameClock.StepHz) - today : -1;
                string events = string.Join(" / ", cal.EventTexts);
                bool rows = cal.DayRowCount == days && cal.NowText.Contains("第 " + today + " 日") && events.Contains("入夜") && events.Contains("天亮")
                            && cal.MarkCount(0, "cal-seg-night") >= 1 && cal.NowLinePercent >= 0f && cal.NowLinePercent <= 100f;
                bool raidShown = warned && events.Contains("突袭 ×") && events.Contains("已预警") && raidDay >= 0 && raidDay < days && cal.MarkCount(raidDay, "cal-mark-raid") >= 1;
                bool pendingNote = cal.FooterText.Contains("静默夜与天气预报将在后续版本加入日历") && cal.FooterText.Contains("Alt");
                // 悬停提示成对释放（B18）：反复关开日历（每次打开都重建）后，UiTooltip 的静态绑定表不增长（旧标记重建前已解绑）。
                int bindingsOpen = UiTooltip.BindingCount;
                int marksOpen = cal.MarkCount(raidDay, "cal-mark-raid");
                int rebuildsBefore = cal.RebuildCount;
                for (int i = 0; i < 5; i++)
                {
                    CalendarPanelUIToolkit.Close();
                    CalendarPanelUIToolkit.Open();
                }
                bool noLeak = CalendarPanelUIToolkit.IsOpen && cal.RebuildCount == rebuildsBefore + 5 && UiTooltip.BindingCount == bindingsOpen && marksOpen >= 1
                              && cal.MarkCount(raidDay, "cal-mark-raid") == marksOpen;
                string leakWhy = $"关开 5 次重建 {cal.RebuildCount - rebuildsBefore} 次，突袭标记 {marksOpen} 个，提示绑定数 {bindingsOpen}→{UiTooltip.BindingCount}";
                // 事件集合不变：只移动“现在”竖线，不重建；游戏时间前进后竖线跟着走。
                int rebuilds = cal.RebuildCount;
                float line0 = cal.NowLinePercent;
                cal.Refresh();
                cal.Refresh();
                bool noRebuild = cal.RebuildCount == rebuilds;
                string rebuildWhy = noRebuild ? string.Empty : "\n重建原因：" + FirstDiff(cal.PreviousStructureKey ?? string.Empty, cal.StructureKeyNow ?? string.Empty);
                FgProductionSelfCheck.Seconds(5f);
                cal.Refresh();
                bool lineMoved = cal.NowLinePercent > line0;
                // 世界照常运行（日历不暂停）。
                bool running = !GameClock.Paused;
                // Esc 关闭（逐层返回）；Alt+L 打开 / 再按关闭。
                UiEscapeStack.CloseTop();
                bool escClosed = !CalendarPanelUIToolkit.IsOpen;
                InputRouter.Reset();
                InputRouter.DebugSetReader(keys);
                InputRouter.SetScope(InputScope.Strategy);
                PressAlt(keys, GameActionId.OpenCalendar);
                bool keyOpen = CalendarPanelUIToolkit.IsOpen;
                PressAlt(keys, GameActionId.OpenCalendar);
                bool keyClose = !CalendarPanelUIToolkit.IsOpen;
                // 英文：同一面板换语言后重建，没有缺键标记。
                GameSettings.SetLanguage(GameLanguage.En);
                CalendarPanelUIToolkit.Open();
                cal.Refresh();
                bar.Refresh();
                FgProductionSelfCheck.Seconds(1f);
                bar.Refresh();
                bool en = cal.NowText.StartsWith("Now:", StringComparison.Ordinal) && cal.EventTexts.Any(t => t.Contains("Night falls")) && (bar.PhaseText.Contains("night in") || bar.PhaseText.Contains("daybreak in"))
                          && !GameText.ContainsMarker(string.Join("|", cal.EventTexts) + cal.FooterText + cal.LegendText + bar.PhaseText);
                CalendarPanelUIToolkit.Close();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Expect(dayOk && nightOk, $"U1 世界时间条日历行（FGR-ENV-002）：白天“{dayRow}”；夜里“{nightRow}”（字形 + 夜里边框，不只靠颜色），时间条照常显示（{bar.DayTimeText}）");
                Expect(clicked && rows && pendingNote && noRebuild && lineMoved && running,
                    $"U1 日历面板（FGU-26）：点日历行打开（发出第一次打开日历钩子），{cal.DayRowCount} 个游戏日各一行（时间轴 + 夜晚色块 + 今天的“现在”竖线 {line0:0.#}%→{cal.NowLinePercent:0.#}%）；" +
                    $"事件不变不重建（{rebuilds}→{cal.RebuildCount}）；世界不暂停；页脚写明静默夜 / 天气还没有。事件：{Short(events)}" + rebuildWhy);
                Expect(deniedOutside, $"U1 负向：不在世界里（先卸载世界、清空战役）点日历行 / 按日历键都不打开，各给一次“不行”提示音（{deniedWhy}）");
                Expect(raidShown, $"U1 日历上的已知突袭：预警后第 {raidDay} 天那一行出现突袭标记 ⚠ 与说明（与预警条同一口径）（预警 {warned}）");
                Expect(noLeak, $"U1 悬停提示成对释放（B18）：日历每次重建前解绑旧标记的提示，反复关开不累积（{leakWhy}）");
                Expect(escClosed && keyOpen && keyClose && en, $"U1 Esc 关闭（{escClosed}）；日历键 {InputDisplay.ForAction(GameActionId.OpenCalendar)} 打开（{keyOpen}）/ 再按关闭（{keyClose}）；英文无缺键标记（{en}：{Short(cal.NowText + " | " + bar.PhaseText + " | " + string.Join(" / ", cal.EventTexts))}）");
                // 布局探针（四种分辨率 + 超长文字 + USS 体检），按 5 天的行结构填充。
                string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "CalendarPanel.uxml", "CalendarWindow", stressFill: true, prepare: rr =>
                {
                    rr.panel.visualTree.Q<VisualElement>("CalendarRoot")?.RemoveFromClassList("uk-hidden");
                    VisualElement daysBox = rr.panel.visualTree.Q<VisualElement>("CalendarDays");
                    for (int d = 0; d < 5; d++)
                    {
                        var row = new VisualElement();
                        row.AddToClassList("cal-day");
                        row.Add(Cls(new Label("第 1 日（今天）"), "cal-day-title"));
                        var b = new VisualElement();
                        b.AddToClassList("cal-bar");
                        row.Add(b);
                        row.Add(Cls(new Label("20:24  ● 夜晚：20:24 入夜，03:36 天亮；夜里太阳能阵列不发电"), "cal-event"));
                        daysBox?.Add(row);
                    }
                });
                string barProbe = UiToolkitLayoutProbe.Probe(UiKitFolder + "WorldBar.uxml", "WorldBar", stressFill: true, prepare: rr =>
                    rr.panel.visualTree.Q<VisualElement>("WorldBar")?.RemoveFromClassList("uk-hidden"));
                Expect(probe.Contains("PASS") && !probe.Contains("FAIL") && barProbe.Contains("PASS") && !barProbe.Contains("FAIL"),
                    "U1 日历面板与世界时间条（含日历行）布局探针：" + (probe.Contains("FAIL") ? probe : probe.Split('\n')[0]) + " / " + (barProbe.Contains("FAIL") ? barProbe : barProbe.Split('\n')[0]));
            }
            finally
            {
                CalendarPanelUIToolkit.Close();
                CalendarPanelUIToolkit.InWorldOverrideForTests = false;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(cgo);
            }
        }

        private static VisualElement Cls(VisualElement e, string cls)
        {
            e.AddToClassList(cls);
            return e;
        }

        private static string Short(string s) => s == null ? string.Empty : s.Length <= 220 ? s : s.Substring(0, 220) + "…";

        // ── U2 日历来源扩展点（FG7-ENV-02 / 03 接入点）──────────────────────────────

        private sealed class FakeSource : ICalendarSource
        {
            public CalendarKind Kind { get; set; }
            public double Start;
            public double End;
            public string Title;

            public void Collect(CampaignState state, double fromSeconds, double toSeconds, List<CalendarEntry> into)
            {
                if (End > fromSeconds && Start < toSeconds)
                {
                    into.Add(new CalendarEntry { Kind = Kind, StartSeconds = Start, EndSeconds = End, Title = Title, Detail = Title + "（自检模拟）", Id = "fake:" + Kind, Forecast = Kind == CalendarKind.Weather });
                }
            }
        }

        private static void CheckCalendarSources()
        {
            CampaignState s = NewWorld(7141, true, out _);
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "WorldBar.uxml", out GameObject go);
            VisualElement croot = FgProductionSelfCheck.MountUxml(UiKitFolder + "CalendarPanel.uxml", out GameObject cgo);
            CalendarPanelUIToolkit.InWorldOverrideForTests = true;
            double now = GameClock.GameSeconds;
            var silent = new FakeSource { Kind = CalendarKind.SilentNight, Start = now + 2 * Hour, End = now + 6 * Hour, Title = "静默夜" };
            var weather = new FakeSource { Kind = CalendarKind.Weather, Start = now - 0.1 * Hour, End = now + 10 * Hour, Title = "沙暴" };
            try
            {
                var bar = go.AddComponent<WorldBarHudUIToolkit>();
                bar.BindView(root);
                var cal = cgo.AddComponent<CalendarPanelUIToolkit>();
                cal.BindView(croot);
                cal.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "CalendarDayRow.uxml"));
                bar.Refresh();
                bool hiddenBefore = bar.CalendarExtraText.Length == 0 && root.Q<Label>("WorldCalendarExtra").ClassListContains("uk-hidden")
                                    && !CalendarService.HasSource(CalendarKind.SilentNight) && !CalendarService.HasSource(CalendarKind.Weather);
                CalendarService.Register(silent);
                CalendarService.Register(weather);
                CalendarService.Register(silent); // 重复注册只算一次
                FgProductionSelfCheck.Seconds(1f); // 下一个游戏分钟（时间条按分钟刷新）
                bar.Refresh();
                string extra = bar.CalendarExtraText;
                bool hud = extra.Contains("★ 静默夜") && extra.Contains("后") && extra.Contains("☁ 沙暴") && !root.Q<Label>("WorldCalendarExtra").ClassListContains("uk-hidden");
                CalendarPanelUIToolkit.Open();
                string events = string.Join(" / ", cal.EventTexts);
                int silentEntries = cal.EventTexts.Count(t => t.Contains("静默夜（自检模拟）"));
                bool panel = silentEntries == 1 && events.Contains("沙暴（自检模拟）") && events.Contains("（预报）") && cal.MarkCount(0, "cal-mark-silent") + cal.MarkCount(1, "cal-mark-silent") == 1
                             && cal.MarkCount(0, "cal-mark-weather") == 1 && !cal.FooterText.Contains("后续版本");
                CalendarPanelUIToolkit.Close();
                CalendarService.Unregister(silent);
                CalendarService.Unregister(weather);
                FgProductionSelfCheck.Seconds(1f);
                bar.Refresh();
                bool hiddenAfter = bar.CalendarExtraText.Length == 0 && !CalendarService.HasSource(CalendarKind.SilentNight);
                Expect(hiddenBefore && hud && panel && hiddenAfter,
                    $"U2 日历来源扩展点（FG7-ENV-02 / 03 只需注册来源）：没有静默夜 / 天气来源时时间条右侧不显示；注册后时间条写“{extra}”，日历时间轴出现 ★ / ☁ 标记与说明（天气标“预报”，正在进行的列在今天），页脚不再写“后续版本”；去掉后恢复。事件：{Short(events)}");
            }
            finally
            {
                CalendarService.Unregister(silent);
                CalendarService.Unregister(weather);
                CalendarPanelUIToolkit.Close();
                CalendarPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(cgo);
            }
        }

        // ── U3 暂停菜单“画面” ─────────────────────────────────────────────────────

        private static void CheckPauseMenuDisplay()
        {
            VisualElement root = FgProductionSelfCheck.MountUxml(UiKitFolder + "PauseMenu.uxml", out GameObject go);
            try
            {
                GameSettings.ResetNightBrightnessFloor();
                var menu = go.AddComponent<PauseMenuUIToolkit>();
                menu.BindView(root);
                Slider slider = menu.NightFloorSlider;
                bool range = slider != null && Mathf.Approximately(slider.lowValue, GameSettings.NightBrightnessMin) && Mathf.Approximately(slider.highValue, GameSettings.NightBrightnessMax)
                             && Mathf.Approximately(slider.value, GameSettings.NightBrightnessDefault) && menu.NightFloorLabelText.Contains("50%")
                             && root.Q<Label>("PauseDisplayTitle").text == "画面";
                slider.value = 0.3f; // 拖动立即生效
                bool applied = Mathf.Approximately(GameSettings.NightBrightnessFloor, 0.3f) && menu.NightFloorLabelText.Contains("30%");
                Click(menu.NightFloorResetButton);
                UiEscapeStack.Clear();
                menu.SyncNightFloor();
                bool reset = Mathf.Approximately(GameSettings.NightBrightnessFloor, GameSettings.NightBrightnessDefault) && Mathf.Approximately(slider.value, GameSettings.NightBrightnessDefault);
                Expect(range && applied && reset, $"U3 暂停菜单“画面”：夜间亮度下限滑条（范围 {GameSettings.NightBrightnessMin:P0}～{GameSettings.NightBrightnessMax:P0}、默认 50%），拖动立即生效、标签跟着变，“恢复默认亮度”复位");
            }
            finally
            {
                GameSettings.ResetNightBrightnessFloor();
                UiEscapeStack.Clear();
                Object.DestroyImmediate(go);
            }
        }

        // ── P1 性能 ───────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            CampaignState s = NewWorld(7151, true, out _);
            const int n = 200000;
            var sw = Stopwatch.StartNew();
            float acc = 0f;
            for (int i = 0; i < n; i++)
            {
                acc += DayNightCycle.Daylight(i * 0.37);
            }
            double lightUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            sw.Restart();
            for (int i = 1; i <= n; i++)
            {
                DayNightCycle.WorldStep(s, i, GameClock.StepHz);
            }
            double stepUs = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            VisualElement croot = FgProductionSelfCheck.MountUxml(UiKitFolder + "CalendarPanel.uxml", out GameObject cgo);
            CalendarPanelUIToolkit.InWorldOverrideForTests = true;
            double openMs, refreshMs;
            try
            {
                var cal = cgo.AddComponent<CalendarPanelUIToolkit>();
                cal.BindView(croot);
                cal.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "CalendarDayRow.uxml"));
                sw.Restart();
                CalendarPanelUIToolkit.Open();
                openMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                for (int i = 0; i < 200; i++)
                {
                    cal.Refresh();
                }
                refreshMs = sw.Elapsed.TotalMilliseconds / 200.0;
            }
            finally
            {
                CalendarPanelUIToolkit.Close();
                CalendarPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(cgo);
            }
            PerfLines.Add($"光照计算 {lightUs:F3} μs / 次，昼夜每步检查 {stepUs:F3} μs / 步，日历打开（重建 {CalendarService.Days} 天）{openMs:F2} ms，日历定时刷新（事件不变）{refreshMs:F3} ms（校验和 {acc:F0}）");
            PerfGate.Expect(true, $"P1 光照计算 {lightUs:F3} μs / 次（阈值 1 μs）、昼夜每步检查 {stepUs:F3} μs / 步（阈值 0.5 μs；每帧 O(1)，与实体数无关）",
                new[] { PerfGate.Le(lightUs, 1.0, "光照计算 μs"), PerfGate.Le(stepUs, 0.5, "昼夜每步检查 μs") }, Expect, Line);
            PerfGate.Expect(true, $"P1 日历打开 {openMs:F2} ms（阈值 20 ms）、事件不变时的定时刷新 {refreshMs:F3} ms（阈值 0.5 ms，每 0.25 真实秒一次）",
                new[] { PerfGate.Le(openMs, 20.0, "日历打开 ms"), PerfGate.Le(refreshMs, 0.5, "日历刷新 ms") }, Expect, Line);
        }

        // ── 断言 ─────────────────────────────────────────────────────────────────

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
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
            }
        }

        private static void Expect(bool ok, string message)
        {
            if (ok)
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
            _report?.AppendLine(text);
        }
    }
}
