using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG0-QA-01：M0 出口旅程 FGJ-M0（ProjectA_FullGame_Milestones.md）。
    /// 用固定测试种子从主菜单开新档，确认生成结果与该种子的基准一致 → 在格网上放置、旋转、拆除建筑 →
    /// 派一支编队沿地形行进 1,000 格，同时家园继续运行 → 镜头飞回家园看一眼 → 再飞回编队 → 编队返回家园；
    /// 全程的状态与不观察时的对照完全一致。
    ///
    /// 做法：
    /// - 全程走主菜单与正式输入（快捷键、建造栏按钮、框选、“移动”命令 + 点击目标、“撤退”命令）；不依赖固定坐标：
    ///   建造位置、编队成员、行进目标都按当前种子的地形现找（FGR-ARC-011）。
    /// - “不观察时的对照”：每次下命令都在战略暂停中进行，下完立即存档（出发档 S1、返程档 S2）并拍逐字段快照；
    ///   旅程结束后分别读 S1 / S2，**不经过镜头与输入**无头推进到观察组暂停的那一步（到达 T2 / 回家 T3），与观察组的快照逐字段比较。
    ///   同时比较“存档那一刻”与“读档之后”的快照（存读档往返逐字段一致）。
    ///
    /// 测试捷径（登记在 FG-GAP-REGISTER，DEBT-FG0QA01-*）：
    /// - 远征队用家园的机器编队代替：正式的“远征队在星球表面行进”属于 FG8-EXP-02（现在的远征出发只载入固定地点，不沿地形行进）。
    /// - “再飞回远征队”用 WorldView.FlyTo 飞到编队位置：家园编队还不是关注点（Tab 只轮换家园 / 远征地点 / 突袭），“跟随选中”键还没有实现。
    /// </summary>
    public static class FgjM0Journey
    {
        public const string Id = "FGJ-M0";

        /// <summary>固定测试种子：FgWorldGenSelfCheck 的生成器回归基准里有这颗种子（v1 地球表面区块 (0,0) 与 (31249,-31249)）。</summary>
        public const int TestSeed = 42;

        private const int MarchCells = 1000;
        private const int DepartSlot = 1;
        private const int ReturnSlot = 2;
        private const int ReplaySlot = 3;
        private const float ArrivedRadius = 8f;
        private const float HomeRadius = 24f;

        // 观察组快照（Play 期间不重载域，放静态字段；丢了就判失败）。
        private static Dictionary<string, string> _o1;
        private static Dictionary<string, string> _o2;
        private static Dictionary<string, string> _o2b;
        private static Dictionary<string, string> _o3;
        private static Dictionary<FeedbackCueId, int> _cue1;
        private static Dictionary<FeedbackCueId, int> _cue2;
        private static Dictionary<string, int> _note1;
        private static Dictionary<string, int> _note2;

        public static JourneyDef Build() => new JourneyDef
        {
            Id = Id,
            Title = "M0 出口：新档 → 建造 → 编队行进 1,000 格 → 镜头往返 → 返回；观察与不观察逐字段一致",
            Seed = TestSeed,
            TotalTimeoutSeconds = 840,
            OnFinish = Cleanup,
            Steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, EnterPlay, TickPlay),
                S("menu_new", "主菜单点“新建”（固定测试种子）", 150, null, TickMenuNew),
                S("new_game", "进入归还谷地", 120, null, TickNewGame),
                S("seed", "生成结果与该种子的基准一致", 30, null, TickSeed),

                S("b_pause", "按暂停键（战略暂停中规划建造）", 10, c => PressAction(GameActionId.TogglePause), TickPaused, retries: 1),
                S("b_open", "按建造菜单键打开建造模式，点建造栏第一项", 10, c => PressAction(GameActionId.OpenBuildMenu), TickBuildOpen, retries: 1),
                S("b_hover_a", "鼠标移到空地 A（虚影跟随）", 10, c => HoverBuildCell(c, "A"), c => TickHover(c, "A")),
                S("b_rotate_a", "按旋转键", 10, c => PressAction(GameActionId.Rotate, JourneyInput.ScreenOf(CellPos(c, "A"))), TickRotated, retries: 1),
                S("b_place_a", "左键放置（旋转 90° 的发电机）", 10, c => JourneyInput.Click(CellPos(c, "A")), c => TickPlaced(c, "A", 90f), retries: 1),
                S("b_hover_b", "鼠标移到空地 B", 10, c => HoverBuildCell(c, "B"), c => TickHover(c, "B")),
                S("b_rotate_b", "按旋转键转回 0°", 10, null, TickRotateBack),
                S("b_place_b", "左键放置第二座", 10, c => JourneyInput.Click(CellPos(c, "B")), c => TickPlaced(c, "B", 0f), retries: 1),
                S("b_demolish", "按拆除模式键", 10, c => PressAction(GameActionId.DemolishMode), TickDemolishMode, retries: 1),
                S("b_cancel_b", "拆除模式点第二座的虚影（取消规划，全额退款）", 10, c => JourneyInput.Click(CellPos(c, "B")), TickCancelled, retries: 1),
                S("b_esc", "Esc 退出建造模式", 10, c => PressAction(GameActionId.Cancel), TickBuildClosed, retries: 1),

                S("squad_select", "框选两台以上的机器组成编队", 15, BoxSelectSquad, TickSquadSelected, retries: 2),
                S("target", "按种子地形找 1,000 格外能走到的目标，镜头飞过去", 60, PickTargetAndFly, TickTargetView),
                S("arm_move", "按“移动”命令键（等点击选目标）", 10, c => PressAction(GameActionId.CommandMove), TickArmed, retries: 1),
                S("click_target", "左键点目标：编队接令（暂停中排队）；存出发档 S1", 10, c => JourneyInput.Click(TargetPos(c)), TickMoveIssued, retries: 1),
                S("cam_home", "按“回到归还核心”键：镜头飞回家园", 10, c => PressAction(GameActionId.FocusHomeCore), TickCameraHome, retries: 1),
                S("resume", "按暂停键继续", 10, c => PressAction(GameActionId.TogglePause), TickRunning, retries: 1),
                S("speed", "按 3 倍速键", 10, c => PressAction(GameActionId.SpeedTriple), TickSpeed, retries: 1),
                S("look_home", "镜头留在家园：家园继续运行，编队在视野外走远", 120, null, TickLookHome),
                S("fly_back", "镜头飞回编队（测试捷径：FlyTo）", 10, FlyToSquad, TickFlownToSquad),
                S("march", "编队沿地形走完 1,000 格（镜头每 15 秒跟上一次）", 480, null, TickMarch),
                S("pause_arrived", "到达后暂停，拍观察组快照（T2）", 10, PauseIfRunning, TickPausedAtArrival, retries: 1),
                S("retreat", "按“撤退”命令键：编队返回家园；存返程档 S2", 10, c => PressAction(GameActionId.CommandRetreat), TickRetreatIssued, retries: 1),
                S("cam_home_2", "按“回到归还核心”键", 10, c => PressAction(GameActionId.FocusHomeCore), TickCameraHome, retries: 1),
                S("resume_2", "按暂停键继续", 10, c => PressAction(GameActionId.TogglePause), TickRunning, retries: 1),
                S("return", "编队回到归还核心附近", 480, null, TickReturn),
                S("pause_home", "暂停，拍观察组快照（T3）", 10, PauseIfRunning, TickPausedAtHome, retries: 1),
                S("compare", "读 S1 / S2 无头推进到 T2 / T3：与观察组逐字段比较", 180, null, TickCompare),
            },
        };

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick,
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

        private static void EnterPlay(JourneyContext c)
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

        private static StepOutcome TickPlay(JourneyContext c)
        {
            if (c.Get("aborted") == "1")
            {
                return StepOutcome.Fail("当前场景有未保存的修改、保存被取消：旅程不开（不替你丢掉修改）");
            }
            if (!EditorApplication.isPlaying)
            {
                return StepOutcome.Wait;
            }
            // 进 Play 重载了域：测试用的静态开关在这里（重载之后）设置。
            string saves = c.Get("saves");
            Directory.CreateDirectory(saves);
            CampaignSaveService.SaveDirectoryOverrideForTests = saves;
            CampaignRandomService.SeedOverrideForTests = TestSeed;
            _o1 = _o2 = _o2b = _o3 = null;
            return StepOutcome.Done($"已进入 Play；存档目录改到临时目录 {saves}；新建战役的种子固定为 {TestSeed}");
        }

        private static StepOutcome TickMenuNew(JourneyContext c)
        {
            UnityEngine.UI.Button button = JourneyInput.FindActiveButton("m_btn_New");
            if (button == null || c.StepElapsed < 2)
            {
                return StepOutcome.Wait; // 等主菜单出现并稳定。
            }
            button.onClick.Invoke();
            return StepOutcome.Done($"主菜单出现，点“新建”（{c.StepElapsed:F0} 秒）");
        }

        private static StepOutcome TickNewGame(JourneyContext c)
        {
            if (GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive)
            {
                return c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Done("进入归还谷地");
            }
            UnityEngine.UI.Button confirm = JourneyInput.FindActiveButton("m_btn_ConfirmYes");
            if (confirm != null)
            {
                confirm.onClick.Invoke();
                c.Log("出现覆盖确认，点“是”");
                return StepOutcome.Wait;
            }
            UnityEngine.UI.Button slot = JourneyInput.FindActiveButton("m_btn_Slot0Action");
            if (slot != null && c.GetInt("slotClicked") == 0)
            {
                c.SetInt("slotClicked", 1);
                slot.onClick.Invoke();
                c.Log("出现存档槽列表，点槽位 0");
            }
            return StepOutcome.Wait;
        }

        /// <summary>生成结果与该种子的基准一致：种子 / 生成器版本 / 世界设置经主菜单“新建”原样进了存档；区块内容哈希等于
        /// FgWorldGenSelfCheck 的回归基准（同一算法，基准写死在那里，改生成器不升版本就会失败）；规划层与按种子独立重算的一致。</summary>
        private static StepOutcome TickSeed(JourneyContext c)
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
            if (s.RandomSeed != TestSeed || s.World.WorldSeed != TestSeed || version != WorldGenVersions.Current
                || s.World.WorldSettingsId != WorldGenContent.DefaultPresetId)
            {
                return StepOutcome.Fail($"新档的种子 / 生成器版本 / 世界设置不对：战役种子 {s.RandomSeed}、世界种子 {s.World.WorldSeed}、" +
                                        $"版本 v{version}（当前 v{WorldGenVersions.Current}）、世界设置 {s.World.WorldSettingsId}");
            }
            // 核对的是游戏此刻真正在用的地形源（家园格网装着的那一个），不是按存档参数新建一个：装错了源（版本 / 表面不对）这一步要能发现。
            IGridTerrainSource live = HomeGridService.MapFor(s)?.TerrainSource;
            if (live == null)
            {
                return StepOutcome.Fail("家园格网没有地形源");
            }
            var checkedChunks = new List<string>();
            foreach (var b in FgWorldGenSelfCheck.Baseline)
            {
                if (b.seed != TestSeed || b.version != version || b.surface != WorldGenContent.EarthSurfaceId)
                {
                    continue;
                }
                ulong h = HashOf(live, b.cx, b.cy);
                if (h != b.hash)
                {
                    return StepOutcome.Fail($"区块 ({b.cx},{b.cy}) 内容哈希 {h:X16} ≠ 种子 {TestSeed} 的基准 {b.hash:X16}");
                }
                checkedChunks.Add($"({b.cx},{b.cy})={h:X16}");
            }
            if (checkedChunks.Count == 0)
            {
                return StepOutcome.Fail($"FgWorldGenSelfCheck.Baseline 里没有种子 {TestSeed}、v{version} 的地球表面基准（换测试种子时要同时补基准）");
            }
            GridCell core = HomeGridService.CorePivot(s);
            string reference = WorldPlan.Compute(TestSeed, WorldGenContent.Version(version), WorldGenContent.Preset(version, WorldGenContent.DefaultPresetId), core.X, core.Y).Fingerprint();
            string actual = WorldGenService.PlanFor(s)?.Fingerprint();
            if (!string.Equals(reference, actual, StringComparison.Ordinal))
            {
                return StepOutcome.Fail("规划层指纹与按种子独立重算的不一致");
            }
            return StepOutcome.Done($"种子 {TestSeed}、v{version}、世界设置 {s.World.WorldSettingsId}；区块哈希与基准一致 {string.Join(" ", checkedChunks)}；规划层指纹一致");
        }

        private static ulong HashOf(IGridTerrainSource src, int cx, int cy)
        {
            int n = GridContent.TuningInt("grid.chunk_size");
            var t = new byte[n * n];
            var p = new byte[n * n];
            src.FillChunk(cx, cy, n, t, p);
            return WorldGenKernel.Hash64(t, p);
        }

        // ── 建造（正式输入：B 打开、点建造栏、悬停、R 旋转、左键放置、X 拆除模式点虚影取消、Esc 退出；全程战略暂停）──────────

        private static void PressAction(GameActionId action, Vector3? mouse = null) => JourneyInput.PressKey(GameSettings.KeyBindings.GetKey(action), mouse);

        private static StepOutcome TickPaused(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return GameClock.Paused ? StepOutcome.Done("战略暂停已开启") : StepOutcome.Retry("按了暂停键，世界没有暂停");
        }

        private static StepOutcome TickRunning(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return !GameClock.Paused ? StepOutcome.Done($"世界继续运行（第 {GameClock.Ticks} 步）") : StepOutcome.Retry("按了暂停键，世界仍在暂停");
        }

        private static StepOutcome TickBuildOpen(JourneyContext c)
        {
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            if (mode == null || !mode.IsOpen || InputRouter.ActiveContext != InputContext.Build || hud == null || !hud.PanelVisible)
            {
                return StepOutcome.Retry("建造模式没有打开（或输入上下文不是建造）");
            }
            if (!JourneyInput.ClickUitk("[BuildModeHudHost]", "BuildItem0") || mode.SelectedTypeId != HomeValleyLayout.BuildingTypeGenerator2)
            {
                return StepOutcome.Fail($"点建造栏第一项没有选中发电机（选中 {mode.SelectedTypeId}）");
            }
            c.SetInt("scrap0", CampaignSession.Current.Scrap);
            return StepOutcome.Done($"建造模式打开（输入上下文 = 建造，建造栏 {hud.ItemCount} 项），选中发电机");
        }

        private static Vector2 CellPos(JourneyContext c, string tag) => new Vector2(c.GetInt("cell" + tag + "X"), c.GetInt("cell" + tag + "Y"));

        private static GridCell CellOf(JourneyContext c, string tag) => new GridCell(c.GetInt("cell" + tag + "X"), c.GetInt("cell" + tag + "Y"));

        /// <summary>从归还核心向外按环找一块能放发电机、而且在画面内的空地（按当前种子的地形现找，不写死坐标）。</summary>
        private static void HoverBuildCell(JourneyContext c, string tag)
        {
            CampaignState s = CampaignSession.Current;
            GridCell core = HomeGridService.CorePivot(s);
            string type = HomeValleyLayout.BuildingTypeGenerator2;
            int offScreen = 0;
            int invalid = 0;
            var reasons = new Dictionary<string, int>();
            for (int r = 5; r <= 16; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var cell = new GridCell(core.X + dx, core.Y + dy);
                        if (!JourneyInput.OnScreen(new Vector2(cell.X, cell.Y), 0.15f))
                        {
                            offScreen++;
                            continue;
                        }
                        // A 要转 90° 再放：两个朝向都要能放（非方形占地转过去可能压到障碍）。
                        GridPlacementResult v0 = HomeGridService.ValidatePlacement(s, type, cell, 0);
                        GridPlacementResult v90 = v0.Ok ? HomeGridService.ValidatePlacement(s, type, cell, 90) : v0;
                        if (!v0.Ok || !v90.Ok)
                        {
                            invalid++;
                            string why = (v0.Ok ? v90 : v0).Reasons.Count > 0 ? (v0.Ok ? v90 : v0).Reasons[0].Code.ToString() : "?";
                            reasons[why] = reasons.TryGetValue(why, out int n) ? n + 1 : 1;
                            continue;
                        }
                        c.SetInt("cell" + tag + "X", cell.X);
                        c.SetInt("cell" + tag + "Y", cell.Y);
                        c.SetInt("cell" + tag + "Found", 1);
                        JourneyInput.Hover(new Vector2(cell.X, cell.Y));
                        return;
                    }
                }
            }
            c.SetInt("cell" + tag + "Found", 0);
            Camera cam = JourneyInput.Cam;
            c.Set("cell" + tag + "Miss", $"画面外 {offScreen}、不能放 {invalid}（{string.Join("，", reasons.Select(kv => kv.Key + " " + kv.Value))}）；" +
                                        $"镜头 {(cam == null ? "无" : $"{cam.pixelWidth}×{cam.pixelHeight} 正交 {cam.orthographicSize:F1} 位置 {cam.transform.position}")}");
        }

        private static StepOutcome TickHover(JourneyContext c, string tag)
        {
            if (c.GetInt("cell" + tag + "Found") == 0)
            {
                return StepOutcome.Fail("核心附近、画面内找不到能放发电机的空地：" + c.Get("cell" + tag + "Miss", string.Empty));
            }
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            GridCell cell = CellOf(c, tag);
            return mode != null && mode.HasHover && mode.HoverCell == cell && mode.Preview != null && mode.Preview.Ok
                ? StepOutcome.Done($"虚影吸附到格子 {cell}，预览合法")
                : StepOutcome.Fail($"虚影没有吸附到 {cell}（悬停 {mode?.HoverCell}，预览合法 = {mode?.Preview?.Ok}）");
        }

        private static StepOutcome TickRotated(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            return mode != null && mode.GhostRotation == 90 ? StepOutcome.Done("虚影旋转到 90°") : StepOutcome.Retry($"虚影朝向 {mode?.GhostRotation}°");
        }

        /// <summary>再按三次旋转键回到 0°（每次一帧，逐次核对）。</summary>
        private static StepOutcome TickRotateBack(JourneyContext c)
        {
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            if (mode == null)
            {
                return StepOutcome.Fail("建造模式不见了");
            }
            int presses = c.GetInt("rotPresses");
            double last = c.GetLong("rotAtMs") / 1000.0;
            if (presses > 0 && c.StepElapsed - last < 0.4)
            {
                return StepOutcome.Wait; // 等上一次按键生效再看朝向。
            }
            if (mode.GhostRotation == 0 && presses > 0)
            {
                return StepOutcome.Done($"按了 {presses} 次旋转键，虚影回到 0°");
            }
            if (presses >= 6)
            {
                return StepOutcome.Fail($"按了 {presses} 次旋转键，虚影朝向 {mode.GhostRotation}°");
            }
            PressAction(GameActionId.Rotate, JourneyInput.ScreenOf(CellPos(c, "B")));
            c.SetInt("rotPresses", presses + 1);
            c.SetLong("rotAtMs", (long)(c.StepElapsed * 1000));
            return StepOutcome.Wait;
        }

        private static StepOutcome TickPlaced(JourneyContext c, string tag, float rotation)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = CampaignSession.Current;
            BuildingRecord b = HomeGridService.BuildingAt(s, CellOf(c, tag));
            if (b == null)
            {
                return StepOutcome.Retry($"左键后格子 {CellOf(c, tag)} 上没有建筑（{HomeValleyBuildMode.Current?.StatusText}）");
            }
            if (b.ConstructionState != BuildingConstructionState.Planned || !Mathf.Approximately(b.Rotation, rotation))
            {
                return StepOutcome.Fail($"放下的建筑状态 {b.ConstructionState}、朝向 {b.Rotation}°（期望规划中、{rotation}°）");
            }
            // 放置时按建造配置预留废料（取消时全额退回）：否则“取消后废料 == 放第一座后的废料”恒成立，退款断言形同虚设。
            int before = tag == "A" ? c.GetInt("scrap0") : c.GetInt("scrapAfterA");
            int cost = HomeValleyLayout.BuildProfile.TryGetValue(HomeValleyLayout.BuildingTypeGenerator2, out (int ScrapCost, float Seconds) profile) ? profile.ScrapCost : 0;
            if (cost <= 0 || s.Scrap != before - cost)
            {
                return StepOutcome.Fail($"放置发电机应预留废料 {cost}：放置前 {before}、放置后 {s.Scrap}");
            }
            c.Set("building" + tag, b.BuildingId);
            c.SetInt("scrapAfter" + tag, s.Scrap);
            return StepOutcome.Done($"放下规划中的发电机（朝向 {b.Rotation}°），预留废料 {cost}（{before} → {s.Scrap}）");
        }

        private static StepOutcome TickDemolishMode(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            return mode != null && mode.IsOpen && mode.DemolishMode ? StepOutcome.Done("进入拆除模式") : StepOutcome.Retry("没有进入拆除模式");
        }

        private static StepOutcome TickCancelled(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = CampaignSession.Current;
            string id = c.Get("buildingB");
            bool gone = s.BuildingRecords.All(b => b.BuildingId != id) && HomeGridService.BuildingAt(s, CellOf(c, "B")) == null;
            if (!gone)
            {
                return StepOutcome.Retry($"第二座仍在（{HomeValleyBuildMode.Current?.StatusText}）");
            }
            int expected = c.GetInt("scrapAfterA");
            return s.Scrap == expected
                ? StepOutcome.Done($"取消规划：占格释放、废料全额退回（{s.Scrap}）；第一座保留")
                : StepOutcome.Fail($"取消后废料 {s.Scrap}，应退回到 {expected}");
        }

        private static StepOutcome TickBuildClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            HomeValleyBuildMode mode = HomeValleyBuildMode.Current;
            bool closed = (mode == null || !mode.IsOpen) && InputRouter.ActiveContext == InputContext.Strategy && !PauseMenuUIToolkit.IsOpen;
            if (!closed)
            {
                return StepOutcome.Retry("Esc 后建造模式没有退出（或打开了暂停菜单）");
            }
            BuildingRecord a = CampaignSession.Current.BuildingRecords.FirstOrDefault(b => b.BuildingId == c.Get("buildingA"));
            return a != null && GameClock.Paused
                ? StepOutcome.Done("退出建造模式，输入回到战略上下文；第一座（90°）仍在规划中，世界仍暂停")
                : StepOutcome.Fail("退出建造模式后第一座不见了或世界不再暂停");
        }

        // ── 编队 ────────────────────────────────────────────────────────────────────

        private static List<int> Squad(JourneyContext c) =>
            (c.Get("squad", string.Empty) ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();

        private static bool TryMarker(int logicId, out HomeValleyMachineMarker marker)
        {
            marker = null;
            return GameRoot.HomeValley?.Combat != null && GameRoot.HomeValley.Combat.TryGetMachineMarker(logicId, out marker) && marker != null;
        }

        private static Vector2 Centroid(IReadOnlyList<int> ids)
        {
            Vector2 sum = Vector2.zero;
            int n = 0;
            foreach (int id in ids)
            {
                if (TryMarker(id, out HomeValleyMachineMarker m))
                {
                    sum += m.Position;
                    n++;
                }
            }
            return n > 0 ? sum / n : Vector2.zero;
        }

        /// <summary>画面内一台机器（逻辑 ID 最小）和离它最近的一台：以两台为对角框选（框里另有机器也一起进编队）。</summary>
        private static void BoxSelectSquad(JourneyContext c)
        {
            var onScreen = new List<(int Id, Vector2 P)>();
            foreach (MachineRecord m in MachineRegistry.AllRecords.OrderBy(m => m.LogicId))
            {
                if (m == null || !m.IsAlive || m.IsInFactory || m.RegionId != HomeValleyLayout.RegionId || !TryMarker(m.LogicId, out HomeValleyMachineMarker mk))
                {
                    continue;
                }
                if (JourneyInput.OnScreen(mk.Position, 0.15f))
                {
                    onScreen.Add((m.LogicId, mk.Position));
                }
            }
            c.SetInt("onScreenMachines", onScreen.Count);
            if (onScreen.Count < 2)
            {
                return;
            }
            (int Id, Vector2 P) first = onScreen[0];
            (int Id, Vector2 P) second = onScreen.Skip(1).OrderBy(x => Vector2.Distance(x.P, first.P)).ThenBy(x => x.Id).First();
            c.Set("picks", first.Id.ToString(CultureInfo.InvariantCulture) + "," + second.Id.ToString(CultureInfo.InvariantCulture));
            var lo = new Vector2(Mathf.Min(first.P.x, second.P.x) - 1.5f, Mathf.Min(first.P.y, second.P.y) - 1.5f);
            var hi = new Vector2(Mathf.Max(first.P.x, second.P.x) + 1.5f, Mathf.Max(first.P.y, second.P.y) + 1.5f);
            JourneyInput.Drag(lo, hi);
        }

        private static StepOutcome TickSquadSelected(JourneyContext c)
        {
            if (c.GetInt("onScreenMachines") < 2)
            {
                return StepOutcome.Fail($"画面内只有 {c.GetInt("onScreenMachines")} 台能进编队的机器");
            }
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            IReadOnlyList<int> selection = GameRoot.HomeValley.SquadCommands.Selection;
            List<int> picks = c.Get("picks", string.Empty).Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();
            if (selection.Count < 2 || picks.Any(p => !selection.Contains(p)))
            {
                return StepOutcome.Retry($"框选后选中 {selection.Count} 台（应包含 #{string.Join(" #", picks)}）");
            }
            c.Set("squad", string.Join(",", selection.OrderBy(x => x).Select(x => x.ToString(CultureInfo.InvariantCulture))));
            return StepOutcome.Done($"框选出编队 {selection.Count} 台：#{string.Join(" #", selection.OrderBy(x => x))}");
        }

        private static Vector2 TargetPos(JourneyContext c) => new Vector2(c.GetInt("targetX"), c.GetInt("targetY"));

        /// <summary>从编队中心向外 1,000 格起、按 16 个方向找一块 5×5 都能走的地面，并用一个独立的寻路内核（按种子生成地形，
        /// 与游戏里的内核同一算法）确认从编队走得到（不是被围死的盆地）。找到后镜头飞过去（FGR-GEN-080：已探索区域之外也能飞到）。</summary>
        private static void PickTargetAndFly(JourneyContext c)
        {
            CampaignState s = CampaignSession.Current;
            List<int> squad = Squad(c);
            Vector2 center = Centroid(squad);
            GridCell from = NavService.CellOf(center.x, center.y);
            c.SetInt("startX", from.X);
            c.SetInt("startY", from.Y);
            c.SetInt("targetFound", 0);
            var src = HomeGridService.MapFor(s).TerrainSource as WorldTerrainSource;
            if (src == null || !NavService.IsBound)
            {
                return;
            }
            using var k = new NavKernel(NavService.ConfigFromTuning(), NavService.TerrainTable(), true, src.Params, src.Rects, src.Zones);
            var pts = new List<int2>();
            int tried = 0;
            for (int r = MarchCells; r <= MarchCells + 120 && c.GetInt("targetFound") == 0; r += 8)
            {
                for (int a = 0; a < 16; a++)
                {
                    double ang = a * Math.PI / 8;
                    var g = new GridCell(from.X + (int)Math.Round(Math.Cos(ang) * r), from.Y + (int)Math.Round(Math.Sin(ang) * r));
                    if (!AreaPassable(k, g, 2))
                    {
                        continue;
                    }
                    tried++;
                    var req = new NavRequest
                    {
                        OwnerTag = 9,
                        OwnerKey = 1,
                        Serial = tried,
                        Class = NavConst.ClassPlayer,
                        Flags = NavRequestFlags.None,
                        Start = new int2(from.X, from.Y),
                        Goal = new int2(g.X, g.Y),
                    };
                    NavResult res = k.FindNow(req, pts, onWorker: true, out double ms);
                    if (res.Status != NavStatus.Ok)
                    {
                        continue;
                    }
                    c.SetInt("targetX", g.X);
                    c.SetInt("targetY", g.Y);
                    c.SetInt("targetFound", 1);
                    c.Log($"目标 {g}：直线 {r} 格，预检路线长 {res.Length:F0} 格（独立内核 {ms:F1} ms，试了 {tried} 个候选）");
                    break;
                }
            }
            if (c.GetInt("targetFound") == 1)
            {
                WorldView.FlyTo(GameRoot.HomeValley.SiteId, TargetPos(c));
            }
        }

        /// <summary>在独立的寻路内核上探测（不碰观察组的实时内核镜像：探测 1,000 格外会在镜像里多生成区块，测试探针不该改被测对象）。</summary>
        private static bool AreaPassable(NavKernel k, GridCell g, int half)
        {
            for (int y = -half; y <= half; y++)
            {
                for (int x = -half; x <= half; x++)
                {
                    if (!k.Passable(g.X + x, g.Y + y, NavConst.ClassPlayer))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static Vector2 CameraFocus() => new Vector2(WorldView.Director.StrategyFocus.x, WorldView.Director.StrategyFocus.y);

        private static StepOutcome TickTargetView(JourneyContext c)
        {
            if (c.GetInt("targetFound") == 0)
            {
                return StepOutcome.Fail($"编队中心 ({c.GetInt("startX")},{c.GetInt("startY")}) 外 {MarchCells}～{MarchCells + 120} 格找不到走得到的空地");
            }
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            float d = Vector2.Distance(CameraFocus(), TargetPos(c));
            return d < 1.5f && JourneyInput.OnScreen(TargetPos(c), 0.2f)
                ? StepOutcome.Done($"镜头飞到 {MarchCells} 格外的目标（已探索区域之外，焦点 {CameraFocus()}）")
                : StepOutcome.Fail($"镜头没能飞到目标：焦点 {CameraFocus()} 离目标 {d:F1} 格（镜头范围把它钳回来了？）");
        }

        private static StepOutcome TickArmed(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return GameRoot.HomeValley.SquadCommands.ArmedKind == RegionCommandKind.Move
                ? StepOutcome.Done("移动命令待命：等点击选目标")
                : StepOutcome.Retry("按了“移动”键，没有进入待命");
        }

        private static StepOutcome TickMoveIssued(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            Vector2 target = TargetPos(c);
            foreach (int id in Squad(c))
            {
                if (!TryMarker(id, out HomeValleyMachineMarker m) || !m.Site.TryGetCommand(m.UnitId, out CombatCommand cmd)
                    || cmd.Kind != CombatCommandKind.Move || math.distance(cmd.Pos, new double2(target.x, target.y)) > 3.0)
                {
                    return StepOutcome.Retry($"机器 #{id} 没有接到去目标的移动命令（{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}）");
                }
            }
            if (!GameClock.Paused)
            {
                return StepOutcome.Fail("下令时世界不在暂停中（对照需要在同一步存档）");
            }
            if (!SaveNow(DepartSlot, out string failure))
            {
                return StepOutcome.Fail("出发档 S1 写入失败：" + failure);
            }
            _cue1 = CueCounts();
            _note1 = NoteCounts();
            _o1 = Snapshot(_cue1, _note1);
            c.SetLong("t1", GameClock.Ticks);
            return StepOutcome.Done($"编队 {Squad(c).Count} 台接令（暂停中排队，恢复后第一步执行）；出发档 S1 存于第 {GameClock.Ticks} 步（快照 {_o1.Count} 个字段）");
        }

        private static StepOutcome TickCameraHome(JourneyContext c)
        {
            if (c.StepElapsed < 1.2)
            {
                return StepOutcome.Wait;
            }
            float d = Vector2.Distance(CameraFocus(), HomeValleyLayout.Core.Position);
            return d < 1.5f && WorldView.ObservedSiteId == GameRoot.HomeValley.SiteId
                ? StepOutcome.Done($"镜头回到归还核心（焦点 {CameraFocus()}）")
                : StepOutcome.Retry($"镜头焦点 {CameraFocus()} 离核心 {d:F1} 格");
        }

        private static StepOutcome TickSpeed(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return Mathf.Approximately(GameClock.Speed, 3f) ? StepOutcome.Done("3 倍速") : StepOutcome.Retry($"倍速 {GameClock.Speed}x");
        }

        /// <summary>行进中遇到紧急通知自动暂停（FGR-UX-020）：像玩家一样按暂停键继续（暂停只是停在触发的那一步，不改变结果）。</summary>
        private static void ResumeIfAutoPaused(JourneyContext c)
        {
            if (!GameClock.Paused)
            {
                return;
            }
            double last = c.GetLong("unpauseAtMs") / 1000.0;
            if (c.StepElapsed - last < 1.0 && c.GetLong("unpauseAtMs") > 0)
            {
                return;
            }
            c.SetLong("unpauseAtMs", Math.Max(1, (long)(c.StepElapsed * 1000)));
            c.SetInt("autoPauses", c.GetInt("autoPauses") + 1);
            c.Log($"第 {GameClock.Ticks} 步自动暂停（{NotificationCenter.History.LastOrDefault()?.Type?.Id}），按暂停键继续");
            PressAction(GameActionId.TogglePause);
        }

        private static StepOutcome TickLookHome(JourneyContext c)
        {
            ResumeIfAutoPaused(c);
            List<int> squad = Squad(c);
            Vector2 start = new Vector2(c.GetInt("startX"), c.GetInt("startY"));
            float away = Vector2.Distance(Centroid(squad), start);
            if (c.StepElapsed < 3 || away < 120f)
            {
                return StepOutcome.Wait;
            }
            long ticks = GameClock.Ticks - c.GetLong("t1");
            bool homeView = WorldView.ObservedSiteId == GameRoot.HomeValley.SiteId && Vector2.Distance(CameraFocus(), HomeValleyLayout.Core.Position) < 1.5f;
            bool squadOffScreen = !JourneyInput.OnScreen(Centroid(squad), 0f);
            return homeView && squadOffScreen && ticks > 0 && WorldPlanetView.TerrainShown
                ? StepOutcome.Done($"镜头在家园（地貌层显示），家园照常推进 {ticks} 步；编队在视野外已离开出发点 {away:F0} 格")
                : StepOutcome.Fail($"看家园时状态不对：镜头在家园 {homeView}、编队在视野外 {squadOffScreen}、推进 {ticks} 步、地貌层 {WorldPlanetView.TerrainShown}");
        }

        private static void FlyToSquad(JourneyContext c)
        {
            Vector2 p = Centroid(Squad(c));
            c.Set("flyX", p.x.ToString("R", CultureInfo.InvariantCulture));
            c.Set("flyY", p.y.ToString("R", CultureInfo.InvariantCulture));
            WorldView.FlyTo(GameRoot.HomeValley.SiteId, p);
        }

        private static StepOutcome TickFlownToSquad(JourneyContext c)
        {
            ResumeIfAutoPaused(c);
            if (c.StepElapsed < 1.2)
            {
                return StepOutcome.Wait;
            }
            var fly = new Vector2(float.Parse(c.Get("flyX"), CultureInfo.InvariantCulture), float.Parse(c.Get("flyY"), CultureInfo.InvariantCulture));
            float d = Vector2.Distance(CameraFocus(), fly);
            return d < 1.5f
                ? StepOutcome.Done($"镜头飞到编队（焦点 {CameraFocus()}，离家园 {Vector2.Distance(fly, HomeValleyLayout.Core.Position):F0} 格）")
                : StepOutcome.Fail($"镜头没能飞到编队：焦点离编队 {d:F1} 格");
        }

        private static StepOutcome TickMarch(JourneyContext c)
        {
            ResumeIfAutoPaused(c);
            Vector2 target = TargetPos(c);
            List<int> squad = Squad(c);
            int arrived = 0;
            foreach (int id in squad)
            {
                if (c.GetInt("arr." + id) == 1)
                {
                    arrived++;
                    continue;
                }
                if (!TryMarker(id, out HomeValleyMachineMarker m))
                {
                    return StepOutcome.Fail($"机器 #{id} 不见了");
                }
                bool moving = m.Site.TryGetCommand(m.UnitId, out CombatCommand cmd) && cmd.Kind == CombatCommandKind.Move;
                float d = Vector2.Distance(m.Position, target);
                if (!moving && d <= ArrivedRadius)
                {
                    c.SetInt("arr." + id, 1);
                    arrived++;
                }
                else if (!moving)
                {
                    return StepOutcome.Fail($"机器 #{id} 的移动命令在离目标 {d:F0} 格处结束：{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}");
                }
            }
            if (arrived == squad.Count)
            {
                Vector2 start = new Vector2(c.GetInt("startX"), c.GetInt("startY"));
                float marched = Vector2.Distance(Centroid(squad), start);
                if (marched < MarchCells - 2 * ArrivedRadius)
                {
                    return StepOutcome.Fail($"编队到达，但离出发点只有 {marched:F0} 格");
                }
                return StepOutcome.Done($"编队 {squad.Count} 台全部到达：离出发点 {marched:F0} 格，用时 {(GameClock.Ticks - c.GetLong("t1")) / (double)GameClock.StepHz:F0} 游戏秒；" +
                                        $"途中自动暂停 {c.GetInt("autoPauses")} 次；镜头跟随 {c.GetInt("follows")} 次");
            }
            // 镜头每 15 秒跟上编队一次（远处区块随镜头流式生成，FG0-ARCH-05）。
            double lastFollow = c.GetLong("followAtMs") / 1000.0;
            if (c.StepElapsed - lastFollow >= 15)
            {
                c.SetLong("followAtMs", (long)(c.StepElapsed * 1000));
                c.SetInt("follows", c.GetInt("follows") + 1);
                WorldView.FlyTo(GameRoot.HomeValley.SiteId, Centroid(squad));
            }
            return StepOutcome.Wait;
        }

        private static void PauseIfRunning(JourneyContext c)
        {
            if (!GameClock.Paused)
            {
                PressAction(GameActionId.TogglePause);
            }
        }

        private static StepOutcome TickPausedAtArrival(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            if (!GameClock.Paused)
            {
                return StepOutcome.Retry("按了暂停键，世界没有暂停");
            }
            if (_o1 == null)
            {
                return StepOutcome.Fail("出发时的快照丢了（Play 期间发生了域重载？）");
            }
            _o2 = Snapshot(_cue1, _note1);
            c.SetLong("t2", GameClock.Ticks);
            if (!LogHome(c))
            {
                return StepOutcome.Fail("第一座发电机的规划在玩家没有取消的情况下消失了（编队命令 / 自动分配不应撤销建筑规划）");
            }
            return StepOutcome.Done($"第 {GameClock.Ticks} 步暂停，拍观察组快照（{_o2.Count} 个字段）");
        }

        private static StepOutcome TickRetreatIssued(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            foreach (int id in Squad(c))
            {
                if (!TryMarker(id, out HomeValleyMachineMarker m) || !m.Site.TryGetCommand(m.UnitId, out CombatCommand cmd) || cmd.Kind != CombatCommandKind.Retreat)
                {
                    IReadOnlyList<int> sel = GameRoot.HomeValley.SquadCommands.Selection;
                    return StepOutcome.Retry($"机器 #{id} 没有接到撤退命令（当前选中 {sel.Count} 台：{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}）");
                }
            }
            if (!GameClock.Paused || GameClock.Ticks != c.GetLong("t2"))
            {
                return StepOutcome.Fail($"下撤退命令时世界不在第 {c.GetLong("t2")} 步的暂停中");
            }
            if (!SaveNow(ReturnSlot, out string failure))
            {
                return StepOutcome.Fail("返程档 S2 写入失败：" + failure);
            }
            _cue2 = CueCounts();
            _note2 = NoteCounts();
            _o2b = Snapshot(_cue2, _note2);
            return StepOutcome.Done($"编队接到撤退命令（目标 = 归还核心）；返程档 S2 存于第 {GameClock.Ticks} 步");
        }

        private static StepOutcome TickReturn(JourneyContext c)
        {
            ResumeIfAutoPaused(c);
            List<int> squad = Squad(c);
            Vector2 home = HomeValleyLayout.Core.Position;
            foreach (int id in squad)
            {
                if (!TryMarker(id, out HomeValleyMachineMarker m))
                {
                    return StepOutcome.Fail($"机器 #{id} 不见了");
                }
                bool retreating = m.Site.TryGetCommand(m.UnitId, out CombatCommand cmd) && cmd.Kind == CombatCommandKind.Retreat;
                float d = Vector2.Distance(m.Position, home);
                if (retreating || d > HomeRadius)
                {
                    if (!retreating && c.GetInt("home." + id) == 0)
                    {
                        return StepOutcome.Fail($"机器 #{id} 的撤退命令在离核心 {d:F0} 格处结束：{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}");
                    }
                    return StepOutcome.Wait;
                }
                c.SetInt("home." + id, 1);
            }
            return StepOutcome.Done($"编队 {squad.Count} 台回到归还核心 {HomeRadius:F0} 格内，返程 {(GameClock.Ticks - c.GetLong("t2")) / (double)GameClock.StepHz:F0} 游戏秒");
        }

        private static StepOutcome TickPausedAtHome(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            if (!GameClock.Paused)
            {
                return StepOutcome.Retry("按了暂停键，世界没有暂停");
            }
            if (_o2b == null)
            {
                return StepOutcome.Fail("返程时的快照丢了（Play 期间发生了域重载？）");
            }
            _o3 = Snapshot(_cue2, _note2);
            c.SetLong("t3", GameClock.Ticks);
            if (!LogHome(c))
            {
                return StepOutcome.Fail("第一座发电机的规划在玩家没有取消的情况下消失了（撤退命令 / 自动分配不应撤销建筑规划）");
            }
            return StepOutcome.Done($"第 {GameClock.Ticks} 步暂停，拍观察组快照（{_o3.Count} 个字段）");
        }

        /// <summary>
        /// 家园那边在编队离开期间的情况。施工进度只记录、不断言（有没有空闲的工人去建第一座发电机取决于开局机器数与命令）；
        /// 但第一座发电机的规划必须还在（规划中 / 施工中 / 已建成都行）：玩家没有取消它，编队命令与自动分配都不能把它撤掉。
        /// </summary>
        private static bool LogHome(JourneyContext c)
        {
            CampaignState s = CampaignSession.Current;
            BuildingRecord a = s.BuildingRecords.FirstOrDefault(b => b.BuildingId == c.Get("buildingA"));
            int orders = s.WorkOrders?.Count(o => o != null && o.State != WorkOrderState.Completed && o.State != WorkOrderState.Cancelled) ?? 0;
            c.Log($"家园：第一座发电机 {(a == null ? "（记录不在了）" : a.ConstructionState.ToString())}；未完成工单 {orders} 张；家园机器 {GameRoot.HomeValley.LiveMachineCount} 台；统一时钟第 {GameClock.Ticks} 步");
            return a != null;
        }

        // ── 不观察时的对照 ───────────────────────────────────────────────────────────

        private static StepOutcome TickCompare(JourneyContext c)
        {
            if (_o1 == null || _o2 == null || _o2b == null || _o3 == null)
            {
                return StepOutcome.Fail("观察组快照不全（Play 期间发生了域重载？）");
            }
            long t1 = c.GetLong("t1");
            long t2 = c.GetLong("t2");
            long t3 = c.GetLong("t3");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!Replay(DepartSlot, t1, t2, out Dictionary<string, string> u1, out Dictionary<string, string> u2, out string f1))
            {
                return StepOutcome.Fail("对照组 1：" + f1);
            }
            double ms1 = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            if (!Replay(ReturnSlot, t2, t3, out Dictionary<string, string> u2b, out Dictionary<string, string> u3, out string f2))
            {
                return StepOutcome.Fail("对照组 2：" + f2);
            }
            double ms2 = sw.Elapsed.TotalMilliseconds;
            List<string> r1 = FgWorldSimSelfCheck.DiffKeys(_o1, u1);
            List<string> d2 = FgWorldSimSelfCheck.DiffKeys(_o2, u2);
            List<string> r2 = FgWorldSimSelfCheck.DiffKeys(_o2b, u2b);
            List<string> d3 = FgWorldSimSelfCheck.DiffKeys(_o3, u3);
            c.Log($"对照组 1：读 S1（第 {t1} 步）无头推进 {t2 - t1} 步，{ms1 / 1000:F1} 秒；对照组 2：读 S2（第 {t2} 步）无头推进 {t3 - t2} 步，{ms2 / 1000:F1} 秒");
            c.Log($"存读档往返：S1 差异 {r1.Count} 个，S2 差异 {r2.Count} 个{Sample(r1.Concat(r2))}");
            c.Log($"观察 vs 不观察：去程（T2）差异 {d2.Count} 个，返程（T3）差异 {d3.Count} 个{Sample(d2.Concat(d3))}");
            if (r1.Count + r2.Count + d2.Count + d3.Count > 0)
            {
                return StepOutcome.Fail($"状态不一致：存读档往返 {r1.Count} + {r2.Count} 个字段，观察 vs 不观察 {d2.Count} + {d3.Count} 个字段（详见上两行）");
            }
            return StepOutcome.Done($"全程与不观察的对照逐字段一致：去程 {_o2.Count} 个字段（第 {t2} 步）、返程 {_o3.Count} 个字段（第 {t3} 步）；两个存档读回后逐字段相同");
        }

        private static string Sample(IEnumerable<string> diffs)
        {
            List<string> list = diffs.Take(8).ToList();
            return list.Count == 0 ? string.Empty : "：" + string.Join(" | ", list);
        }

        /// <summary>真实存档路径（与暂停菜单“保存”相同：写回全部地点的实时状态、导出机器记录、写盘）。</summary>
        private static bool SaveNow(int slot, out string failure)
        {
            SaveResult r = CampaignAutoSaveService.SaveWithExport(slot, SaveReason.Manual);
            failure = r.Success ? null : r.Message;
            return r.Success;
        }

        /// <summary>对照组：读档（按主菜单“继续”的顺序恢复家园，镜头不看任何地点），拍读档后快照，无头推进到 <paramref name="toTick"/> 再拍一张。
        /// 读的是存档的拷贝（推进中的自动存档写进拷贝槽，不改动 S1 / S2）。</summary>
        private static bool Replay(int slot, long fromTick, long toTick, out Dictionary<string, string> atStart, out Dictionary<string, string> atEnd, out string failure)
        {
            atStart = null;
            atEnd = null;
            failure = null;
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            File.Copy(CampaignSaveService.SlotPath(slot), CampaignSaveService.SlotPath(ReplaySlot), true);
            string bak = CampaignSaveService.BakPath(ReplaySlot);
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(ReplaySlot);
            if (!r.Success)
            {
                failure = "读档失败：" + r.Message;
                return false;
            }
            CampaignSession.Set(ReplaySlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            if (GameClock.Ticks != fromTick)
            {
                failure = $"读档后时钟在第 {GameClock.Ticks} 步，存档时是第 {fromTick} 步";
                return false;
            }
            Dictionary<FeedbackCueId, int> cue = CueCounts();
            Dictionary<string, int> note = NoteCounts();
            atStart = Snapshot(cue, note);
            WorldSimulation.StepMany((int)(toTick - fromTick));
            atEnd = Snapshot(cue, note);
            return true;
        }

        private static Dictionary<FeedbackCueId, int> CueCounts()
        {
            var d = new Dictionary<FeedbackCueId, int>();
            foreach (FeedbackCueId id in Enum.GetValues(typeof(FeedbackCueId)))
            {
                d[id] = FeedbackCues.CountOf(id);
            }
            return d;
        }

        private static Dictionary<string, int> NoteCounts() =>
            NotificationCenter.History.Where(e => e?.Type != null).GroupBy(e => e.Type.Id).ToDictionary(g => g.Key, g => g.Sum(e => e.Count), StringComparer.Ordinal);

        /// <summary>存档状态的逐字段快照（与 FgWorldSimSelfCheck 同一口径：真实存档路径写回实时状态 + 导出机器记录 → JsonUtility → 拍平；
        /// GUID 片段按出现顺序编号）。排除按真实时间记录的内容（通知历史、存档历史）与存档元数据（最近一次存档原因：读档进家园会写一次自动存档）；
        /// 反馈时刻与各类通知只比较这一段里新增的次数。</summary>
        private static Dictionary<string, string> Snapshot(Dictionary<FeedbackCueId, int> cueBase, Dictionary<string, int> noteBase)
        {
            CampaignState s = CampaignSession.Current;
            WorldSimulation.SyncAllForSave();
            MachineRegistry.ExportToCampaignState(s);
            string json = JsonUtility.ToJson(s);
            var flat = new Dictionary<string, string>(StringComparer.Ordinal);
            FgWorldSimSelfCheck.MiniJson.Flatten(json, flat);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in flat.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (kv.Key.StartsWith("Notifications", StringComparison.Ordinal) || kv.Key.StartsWith("SaveHistory", StringComparison.Ordinal)
                    || kv.Key == "LastSaveReason")
                {
                    continue;
                }
                result[kv.Key] = FgWorldSimSelfCheck.HexId.Replace(kv.Value, m =>
                {
                    if (!ids.TryGetValue(m.Value, out string token))
                    {
                        token = "#id" + ids.Count.ToString(CultureInfo.InvariantCulture);
                        ids[m.Value] = token;
                    }
                    return token;
                });
            }
            foreach (KeyValuePair<FeedbackCueId, int> kv in CueCounts())
            {
                result["·cue." + kv.Key] = (kv.Value - cueBase[kv.Key]).ToString(CultureInfo.InvariantCulture);
            }
            foreach (KeyValuePair<string, int> kv in NoteCounts())
            {
                int delta = kv.Value - (noteBase.TryGetValue(kv.Key, out int b) ? b : 0);
                if (delta != 0)
                {
                    result["·notify." + kv.Key] = delta.ToString(CultureInfo.InvariantCulture);
                }
            }
            result["·clock.ticks"] = GameClock.Ticks.ToString(CultureInfo.InvariantCulture);
            return result;
        }

        // ── 收尾 ────────────────────────────────────────────────────────────────────

        private static void Cleanup(JourneyContext c, bool pass)
        {
            JourneyInput.Release();
            CampaignRandomService.SeedOverrideForTests = null;
            CampaignSaveService.SaveDirectoryOverrideForTests = null;
            _o1 = _o2 = _o2b = _o3 = null;
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
    }
}
