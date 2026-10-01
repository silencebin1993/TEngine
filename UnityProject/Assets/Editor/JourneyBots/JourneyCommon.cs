using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Notifications;
using GameLogic.Stage;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG0-QA-01 / FG1-E2E-01：各条里程碑旅程共用的步骤——进 Play、主菜单“新建”（固定测试种子）、进入归还谷地、种子核对、
    /// 自动暂停时像玩家一样按暂停键继续、收尾。只放“怎么从正式入口进游戏”这类每条旅程都一样的部分，旅程自己的断言仍在各自文件里。
    /// </summary>
    public static class JourneyCommon
    {
        public static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick,
            int retries = 0) => new JourneyStep
        {
            Id = id,
            Title = title,
            TimeoutSeconds = timeout,
            MaxRetries = retries,
            OnEnter = enter,
            Tick = tick,
        };

        // ── 进入游戏 ────────────────────────────────────────────────────────────────

        public static void EnterPlay(JourneyContext c)
        {
            c.Set("saves", Path.Combine(Path.GetTempPath(), "bingames-journey-saves-" + Guid.NewGuid().ToString("N")));
            if (!EditorApplication.isPlaying)
            {
                // 从菜单跑时：先问要不要保存当前场景的修改（取消 = 不开旅程），别直接丢掉用户没保存的编辑。
                if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    c.Set("aborted", "1");
                    return;
                }
                EditorSceneManager.OpenScene("Assets/Scenes/main.unity", OpenSceneMode.Single);
                EditorApplication.EnterPlaymode();
            }
        }

        /// <summary>进 Play 后（域已重载）设置测试开关：存档目录改到临时目录、新建战役用固定测试种子。<paramref name="afterReload"/> 给旅程清自己的静态字段。</summary>
        public static StepOutcome TickPlay(JourneyContext c, int seed, Action afterReload = null)
        {
            if (c.Get("aborted") == "1")
            {
                return StepOutcome.Fail("当前场景有未保存的修改、保存被取消：旅程不开（不替你丢掉修改）");
            }
            if (!EditorApplication.isPlaying)
            {
                return StepOutcome.Wait;
            }
            string saves = c.Get("saves");
            Directory.CreateDirectory(saves);
            CampaignSaveService.SaveDirectoryOverrideForTests = saves;
            CampaignRandomService.SeedOverrideForTests = seed;
            JourneyInput.ResetCounters();
            afterReload?.Invoke();
            return StepOutcome.Done($"已进入 Play；存档目录改到临时目录 {saves}；新建战役的种子固定为 {seed}");
        }

        /// <summary>主菜单出现后点“新建”（uGUI，经 EventSystem）。</summary>
        public static StepOutcome TickMenuNew(JourneyContext c)
        {
            UnityEngine.UI.Button button = JourneyInput.FindActiveButton("m_btn_New");
            if (button == null || c.StepElapsed < 2)
            {
                return StepOutcome.Wait; // 等主菜单出现并稳定。
            }
            if (!JourneyInput.ClickUgui(button))
            {
                return StepOutcome.Retry("点“新建”失败：" + JourneyInput.LastUiFailure);
            }
            return StepOutcome.Done($"主菜单出现，点“新建”（{c.StepElapsed:F0} 秒）");
        }

        /// <summary>
        /// FG3-GEN-01：旅程新建战役时在新游戏设置里选的世界设置（分项代码）；null = 不改（全部标准档）。
        /// 例如 FGT-GEN-009 的极端设置旅程设为 R0O2P2D0S0（资源低 + 据点高 + 污染高 + 领地近）。
        /// </summary>
        public static string WorldSettingsForNewGame { get; set; }

        /// <summary>存档槽列表点空槽（有覆盖确认就点“是”）；新游戏设置出现时按 <see cref="WorldSettingsForNewGame"/> 点分项按钮、点“开始”；直到进入归还谷地。</summary>
        public static StepOutcome TickNewGame(JourneyContext c)
        {
            if (UI.Kit.NewGamePanelUIToolkit.IsOpen)
            {
                return TickNewGameSetup(c);
            }
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                if (c.StepElapsed < 3)
                {
                    return StepOutcome.Wait;
                }
                c.SetInt("slot", CampaignSession.ActiveSlotIndex);
                return StepOutcome.Done($"进入归还谷地（存档槽 {CampaignSession.ActiveSlotIndex + 1}）");
            }
            UnityEngine.UI.Button confirm = JourneyInput.FindActiveButton("m_btn_ConfirmYes");
            if (confirm != null)
            {
                JourneyInput.ClickUgui(confirm);
                c.Log("出现覆盖确认，点“是”");
                return StepOutcome.Wait;
            }
            UnityEngine.UI.Button slot = JourneyInput.FindActiveButton("m_btn_Slot0Action");
            if (slot != null && c.GetInt("slotClicked") == 0)
            {
                c.SetInt("slotClicked", 1);
                JourneyInput.ClickUgui(slot);
                c.Log("出现存档槽列表，点槽位 1");
            }
            return StepOutcome.Wait;
        }

        /// <summary>新游戏设置面板（UI Toolkit，真实点击）：按旅程要求点分项按钮，再点“开始”。</summary>
        private static StepOutcome TickNewGameSetup(JourneyContext c)
        {
            if (c.GetInt("setupDone") == 1)
            {
                return StepOutcome.Wait;
            }
            // 面板刚显示的那一帧还没排版（控件没有几何，点不到）：像玩家一样等它出来再点。
            long seen = c.GetLong("setupSeenMs");
            if (seen <= 0)
            {
                c.SetLong("setupSeenMs", Math.Max(1, (long)(c.StepElapsed * 1000)));
                return StepOutcome.Wait;
            }
            if (c.StepElapsed * 1000 - seen < 800)
            {
                return StepOutcome.Wait;
            }
            string want = WorldSettingsForNewGame;
            if (!string.IsNullOrEmpty(want) && want != WorldGenContent.DefaultPresetId)
            {
                for (int a = 0; a < WorldSettings.Axes.Length; a++)
                {
                    int level = want[a * 2 + 1] - '0';
                    if (!JourneyInput.ClickUitk("[NewGameHost]", $"NewGameLevel_{WorldSettings.Axes[a]}_{level}"))
                    {
                        return StepOutcome.Retry($"新游戏设置里点不到分项按钮 {WorldSettings.Axes[a]} 第 {level} 档：" + JourneyInput.LastUiFailure);
                    }
                }
                c.Log($"新游戏设置：点分项按钮选 {want}（{UI.Kit.NewGamePanelUIToolkit.Instance?.CurrentSettings().DisplayName()}）");
            }
            if (!JourneyInput.ClickUitk("[NewGameHost]", "NewGameStart"))
            {
                return StepOutcome.Retry("新游戏设置里点不到“开始”：" + JourneyInput.LastUiFailure);
            }
            c.SetInt("setupDone", 1);
            c.Log("新游戏设置出现（种子 = 固定测试种子），点“开始”");
            return StepOutcome.Wait;
        }

        /// <summary>生成结果与该种子的基准一致：种子 / 生成器版本 / 世界设置经主菜单“新建”原样进了存档；区块内容哈希等于
        /// FgWorldGenSelfCheck 的回归基准（标准设置时）并与按（种子, 版本, 设置）独立重算的世界逐块相同；规划层完整指纹一致。</summary>
        public static StepOutcome TickSeed(JourneyContext c, int seed)
        {
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = CampaignSession.Current;
            if (s?.World == null)
            {
                return StepOutcome.Fail("没有活动战役");
            }
            int version = s.World.GeneratorVersion;
            string wantSettings = string.IsNullOrEmpty(WorldSettingsForNewGame) ? WorldGenContent.DefaultPresetId : WorldSettingsForNewGame;
            if (s.RandomSeed != seed || s.World.WorldSeed != seed || version != WorldGenVersions.Current
                || s.World.WorldSettingsId != wantSettings)
            {
                return StepOutcome.Fail($"新档的种子 / 生成器版本 / 世界设置不对：战役种子 {s.RandomSeed}、世界种子 {s.World.WorldSeed}、" +
                                        $"版本 v{version}（当前 v{WorldGenVersions.Current}）、世界设置 {s.World.WorldSettingsId}");
            }
            IGridTerrainSource live = HomeGridService.MapFor(s)?.TerrainSource;
            if (live == null)
            {
                return StepOutcome.Fail("家园格网没有地形源");
            }
            var checkedChunks = new List<string>();
            GridCell core = HomeGridService.CorePivot(s);
            WorldGenContext reference = WorldGenContext.Build(seed, version, WorldSettings.Resolve(version, wantSettings), core, GridContent.TuningInt("grid.chunk_size"));
            foreach ((int cx, int cy) in new[] { (0, 0), (-1, -1), (2, -3), (-4, 3) })
            {
                ulong hl = HashOf(live, cx, cy);
                if (hl != HashOf(reference.Source, cx, cy))
                {
                    return StepOutcome.Fail($"区块 ({cx},{cy}) 与按（种子, v{version}, {wantSettings}）独立重算的世界不同");
                }
            }
            foreach (var b in FgWorldGenSelfCheck.Baseline)
            {
                if (b.seed != seed || b.version != version || b.surface != WorldGenContent.EarthSurfaceId || wantSettings != WorldGenContent.DefaultPresetId)
                {
                    continue;
                }
                ulong h = HashOf(live, b.cx, b.cy);
                if (h != b.hash)
                {
                    return StepOutcome.Fail($"区块 ({b.cx},{b.cy}) 内容哈希 {h:X16} ≠ 种子 {seed} 的基准 {b.hash:X16}");
                }
                checkedChunks.Add($"({b.cx},{b.cy})={h:X16}");
            }
            if (checkedChunks.Count == 0 && wantSettings == WorldGenContent.DefaultPresetId)
            {
                return StepOutcome.Fail($"FgWorldGenSelfCheck.Baseline 里没有种子 {seed}、v{version} 的地球表面基准（换测试种子时要同时补基准）");
            }
            string actual = WorldGenService.PlanFor(s)?.FullFingerprint();
            if (!string.Equals(reference.Plan.FullFingerprint(), actual, StringComparison.Ordinal))
            {
                return StepOutcome.Fail("规划层完整指纹（领地 / 河流 / 矿带 / 起始区保证点）与按种子独立重算的不一致");
            }
            StartGuaranteeReport rep = WorldGenService.PlanFor(s)?.StartReport;
            if (rep != null && !rep.AllSatisfied)
            {
                return StepOutcome.Fail("起始区保证不满足：" + string.Join("；", rep.Failures));
            }
            return StepOutcome.Done($"种子 {seed}、v{version}、世界设置 {s.World.WorldSettingsId}；与独立重算的世界逐块相同；区块哈希与基准一致 {string.Join(" ", checkedChunks)}；" +
                                    $"规划层完整指纹一致；起始区四级保证满足（局部重生成 {rep?.StampCount ?? 0} 项）");
        }

        private static ulong HashOf(IGridTerrainSource src, int cx, int cy)
        {
            int n = GridContent.TuningInt("grid.chunk_size");
            var t = new byte[n * n];
            var p = new byte[n * n];
            src.FillChunk(cx, cy, n, t, p);
            return WorldGenKernel.Hash64(t, p);
        }

        // ── 运行中 ──────────────────────────────────────────────────────────────────

        /// <summary>等待世界运行的步骤里：遇到紧急通知自动暂停（FGR-UX-020）就像玩家一样按暂停键继续（先读状态再按，1 秒内不连按）。</summary>
        public static void ResumeIfAutoPaused(JourneyContext c)
        {
            if (!GameClock.Paused)
            {
                return;
            }
            double last = c.GetLong("unpauseAtMs") / 1000.0;
            if (c.GetLong("unpauseAtMs") > 0 && c.StepElapsed - last < 1.0 && c.StepElapsed >= last)
            {
                return;
            }
            c.SetLong("unpauseAtMs", Math.Max(1, (long)(c.StepElapsed * 1000)));
            c.SetInt("autoPauses", c.GetInt("autoPauses") + 1);
            c.Log($"第 {GameClock.Ticks} 步自动暂停（{NotificationCenter.History.LastOrDefault()?.Type?.Id}），按暂停键继续");
            JourneyInput.PressToggleTo(GameActionId.TogglePause, () => !GameClock.Paused, true);
        }

        /// <summary>
        /// 镜头平移（玩家的做法：按住方向键）让 <paramref name="target"/> 进入画面：已经在画面里返回 true；否则按住朝它的方向键 0.3 真实秒、返回 false（下一次再看）。
        /// 只在战略视角里用。
        /// </summary>
        public static bool PanToward(Vector2 target, float margin = 0.15f)
        {
            if (JourneyInput.OnScreen(target, margin))
            {
                return true;
            }
            if (JourneyInput.Holding)
            {
                return false;
            }
            Unity.Mathematics.float2 f = Campaign.WorldSim.WorldView.Director.StrategyFocus;
            Vector2 d = target - new Vector2(f.x, f.y);
            var keys = new List<KeyCode>(2);
            if (Mathf.Abs(d.x) > 2f)
            {
                keys.Add(Settings.GameSettings.KeyBindings.GetKey(d.x > 0 ? GameActionId.StrategyPanRight : GameActionId.StrategyPanLeft));
            }
            if (Mathf.Abs(d.y) > 2f)
            {
                keys.Add(Settings.GameSettings.KeyBindings.GetKey(d.y > 0 ? GameActionId.StrategyPanUp : GameActionId.StrategyPanDown));
            }
            if (keys.Count > 0)
            {
                PanPresses++;
                JourneyInput.HoldKeys(keys, 0.3);
            }
            return false;
        }

        /// <summary>本次会话按方向键平移镜头的次数（写进报告）。</summary>
        public static int PanPresses { get; set; }

        /// <summary>UI 点击方式的统计行（写进报告：多少次经射线 / 拾取确认没被挡住，多少次射线打不中、未做遮挡检查）。</summary>
        public static string UiStats() =>
            $"UI 点击：UI Toolkit 面板指针事件 {JourneyInput.UitkClicks} 次（均经拾取确认没被挡住）；uGUI 经 EventSystem 射线确认最上层 {JourneyInput.UguiPickedClicks} 次、" +
            $"射线未命中（batchmode 不渲染）按按钮自身派发 {JourneyInput.UguiUnpickedClicks} 次；控件被世界悬停提示挡着、先把光标移离世界再点 {JourneyInput.TooltipRetreats} 次";

        // ── 收尾 ────────────────────────────────────────────────────────────────────

        public static void Cleanup(JourneyContext c)
        {
            JourneyInput.Release();
            CampaignRandomService.SeedOverrideForTests = null;
            CampaignSaveService.SaveDirectoryOverrideForTests = null;
            WorldSettingsForNewGame = null;
            string saves = c.Get("saves");
            try
            {
                if (!string.IsNullOrEmpty(saves) && Directory.Exists(saves))
                {
                    Directory.Delete(saves, true);
                }
            }
            catch (Exception)
            {
                // 临时目录删不掉不影响结论。
            }
        }

        public static string F(double v, string fmt = "0.##") => v.ToString(fmt, CultureInfo.InvariantCulture);
    }
}
