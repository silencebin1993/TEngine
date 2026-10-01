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
    /// - 选目标时镜头用 WorldView.FlyTo 飞到 1,000 格外的目标点（让目标出现在画面里好点击；玩家会平移过去）。
    /// - （FG1-E2E-01 起取消）“再飞回远征队”原先用 FlyTo 捷径：现在按“跟随选中对象”键（默认 F，FG1-HUD-01），镜头跟着编队走完全程（DEBT-FG1HUD01-06）。
    /// - FG1-SIG-07 起覆盖外的机器收不到命令（FGR-SIG-053）：选好目标后沿预检路线预置一串已建成的信号中继塔（场景夹具，出发档 S1 之前放好，
    ///   观察组与对照组完全一样），编队全程在覆盖里，到达后的“撤退”命令才收得到。玩家亲手铺中继的流程由 [覆盖网络] 自检与冒烟覆盖。
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
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, c => JourneyCommon.TickPlay(c, TestSeed, () => { _o1 = _o2 = _o2b = _o3 = null; })),
                S("menu_new", "主菜单点“新建”（固定测试种子）", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "进入归还谷地", 120, null, JourneyCommon.TickNewGame),
                S("seed", "生成结果与该种子的基准一致", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),

                // FG1-E2E-01（DEBT-FG0QA01-07）：切换类按键先读状态再决定按不按——重试时第一次按键已经生效也不会被切回去。
                S("b_pause", "按暂停键（战略暂停中规划建造）", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true), TickPaused, retries: 1),
                S("b_open", "按建造菜单键打开建造模式，点发电机所在的分类页签、再点发电机", 12,
                    c => JourneyInput.PressToggleTo(GameActionId.OpenBuildMenu, () => HomeValleyBuildMode.Current != null && HomeValleyBuildMode.Current.IsOpen, true), TickBuildOpen, retries: 1),
                S("b_hover_a", "鼠标移到空地 A（虚影跟随）", 10, c => HoverBuildCell(c, "A"), c => TickHover(c, "A")),
                S("b_rotate_a", "按旋转键", 10, c =>
                {
                    if (HomeValleyBuildMode.Current == null || HomeValleyBuildMode.Current.GhostRotation != 90)
                    {
                        PressAction(GameActionId.Rotate, JourneyInput.ScreenOf(CellPos(c, "A")));
                    }
                }, TickRotated, retries: 1),
                S("b_place_a", "左键放置（旋转 90° 的发电机）", 10, c => JourneyInput.Click(CellPos(c, "A")), c => TickPlaced(c, "A", 90f), retries: 1),
                S("b_hover_b", "鼠标移到空地 B", 10, c => HoverBuildCell(c, "B"), c => TickHover(c, "B")),
                S("b_rotate_b", "按旋转键转回 0°", 10, null, TickRotateBack),
                S("b_place_b", "左键放置第二座", 10, c => JourneyInput.Click(CellPos(c, "B")), c => TickPlaced(c, "B", 0f), retries: 1),
                S("b_demolish", "按拆除模式键", 10,
                    c => JourneyInput.PressToggleTo(GameActionId.DemolishMode, () => HomeValleyBuildMode.Current != null && HomeValleyBuildMode.Current.DemolishMode, true), TickDemolishMode, retries: 1),
                S("b_cancel_b", "拆除模式点第二座的虚影（取消规划，占格释放、废料不变）", 10, c => JourneyInput.Click(CellPos(c, "B")), TickCancelled, retries: 1),
                S("b_esc", "Esc 退出建造模式", 10, c =>
                {
                    if (HomeValleyBuildMode.Current != null && HomeValleyBuildMode.Current.IsOpen)
                    {
                        PressAction(GameActionId.Cancel);
                    }
                }, TickBuildClosed, retries: 1),

                S("squad_select", "框选两台以上的机器组成编队", 15, BoxSelectSquad, TickSquadSelected, retries: 2),
                S("target", "按种子地形找 1,000 格外能走到的目标，镜头飞过去", 60, PickTargetAndFly, TickTargetView),
                S("arm_move", "按“移动”命令键（等点击选目标）", 10, c =>
                {
                    if (GameRoot.HomeValley.SquadCommands.ArmedKind != RegionCommandKind.Move)
                    {
                        PressAction(GameActionId.CommandMove);
                    }
                }, TickArmed, retries: 1),
                S("click_target", "左键点目标：编队接令（暂停中排队）；存出发档 S1", 10, c => JourneyInput.Click(TargetPos(c)), TickMoveIssued, retries: 1),
                S("cam_home", "按“回到归还核心”键：镜头飞回家园", 10, c => PressAction(GameActionId.FocusHomeCore), TickCameraHome, retries: 1),
                S("resume", "按暂停键继续", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => !GameClock.Paused, true), TickRunning, retries: 1),
                S("speed", "按 3 倍速键", 10, c =>
                {
                    if (!Mathf.Approximately(GameClock.Speed, 3f))
                    {
                        PressAction(GameActionId.SpeedTriple);
                    }
                }, TickSpeed, retries: 1),
                S("look_home", "镜头留在家园：家园继续运行，编队在视野外走远", 120, null, TickLookHome),
                S("fly_back", "按“跟随选中对象”键（默认 F）：镜头飞回编队并跟随", 10, PressFollow, TickFlownToSquad, retries: 1),
                S("march", "编队沿地形走完 1,000 格（镜头全程跟随编队）", 480, null, TickMarch),
                S("pause_arrived", "到达后暂停，拍观察组快照（T2）", 10, PauseIfRunning, TickPausedAtArrival, retries: 1),
                S("retreat", "按“撤退”命令键：编队返回家园；存返程档 S2", 10, c => PressAction(GameActionId.CommandRetreat), TickRetreatIssued, retries: 1),
                S("cam_home_2", "按“回到归还核心”键", 10, c => PressAction(GameActionId.FocusHomeCore), TickCameraHome, retries: 1),
                S("resume_2", "按暂停键继续", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => !GameClock.Paused, true), TickRunning, retries: 1),
                S("return", "编队回到归还核心附近", 480, null, TickReturn),
                S("pause_home", "暂停，拍观察组快照（T3）", 10, PauseIfRunning, TickPausedAtHome, retries: 1),
                S("compare", "读 S1 / S2 无头推进到 T2 / T3：与观察组逐字段比较", 180, null, TickCompare),
            },
        };

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick,
            int retries = 0) => JourneyCommon.S(id, title, timeout, enter, tick, retries);

        // ── 建造（正式输入：B 打开、点建造栏、悬停、R 旋转、左键放置、X 拆除模式点虚影取消、Esc 退出；全程战略暂停）──────────

        private static void PressAction(GameActionId action, Vector3? mouse = null) => JourneyInput.PressAction(action, mouse);

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
            // FG3-E2E-01：FG3-LOG-01 起建造栏按分类分页（默认停在“物流”页），发电机不再是第一项——像玩家一样先点发电机所在的分类页签，
            // 再点列表里的发电机（列表下一帧才排好版；条目不在可见区时先滚滚轮）。
            if (mode.SelectedTypeId != HomeValleyLayout.BuildingTypeGenerator2)
            {
                // 点过条目后等半秒再看（点击下一帧才生效），不连点——连点同一项会把选中又切掉。
                double last = double.TryParse(c.Get("genPickAt"), NumberStyles.Float, CultureInfo.InvariantCulture, out double t0) ? t0 : -1;
                if (last >= 0 && c.StepElapsed >= last && c.StepElapsed - last < 0.5) // 重试时计时从 0 重来：比上次点击时刻还早就当作没点过
                {
                    return StepOutcome.Wait;
                }
                bool clicked = FgjM3Common.PickEntry(HomeValleyLayout.BuildingTypeGenerator2, out string why, out bool scrolling);
                if (clicked)
                {
                    c.Set("genPickAt", c.StepElapsed.ToString("R", CultureInfo.InvariantCulture));
                }
                if (!clicked && !scrolling)
                {
                    return c.StepElapsed < 6 ? StepOutcome.Wait : StepOutcome.Fail($"在建造栏里选不到发电机：{why}（当前分类 {hud.SelectedCategoryId}）");
                }
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Fail($"点了建造栏的发电机没有选中（选中 {mode.SelectedTypeId}；分类 {hud.SelectedCategoryId}）");
            }
            c.SetInt("scrap0", CampaignSession.Current.Scrap);
            return StepOutcome.Done($"建造模式打开（输入上下文 = 建造），点分类页签“{hud.SelectedCategoryId}”再点发电机（本页 {hud.ItemCount} 项），选中发电机");
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
            // FG3-LOG-02 起放下的是施工虚影：放下不扣料，材料由机器运到现场时才从库存扣（施工途中取消的退回由 FGJ-M3R r4 断言）。
            // 暂停中放下，废料必须一件不少——放下就扣料是回退。
            int before = tag == "A" ? c.GetInt("scrap0") : c.GetInt("scrapAfterA");
            if (s.Scrap != before)
            {
                return StepOutcome.Fail($"放下施工虚影不应扣料：放置前 {before}、放置后 {s.Scrap}");
            }
            c.Set("building" + tag, b.BuildingId);
            c.SetInt("scrapAfter" + tag, s.Scrap);
            return StepOutcome.Done($"放下规划中的发电机虚影（朝向 {b.Rotation}°）；暂停中放下不扣料（废料仍是 {s.Scrap}）");
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
                ? StepOutcome.Done($"取消规划：占格释放、废料不变（{s.Scrap}，虚影还没运料）；第一座保留")
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
                    int relays = PlaceRelayChain(s, k, from, pts);
                    c.Log($"场景夹具（FG1-SIG-07 覆盖外收不到命令）：沿预检路线预置 {relays} 座已建成的信号中继塔，编队全程在与核心连通的覆盖里");
                    break;
                }
            }
            if (c.GetInt("targetFound") == 1)
            {
                WorldView.FlyTo(GameRoot.HomeValley.SiteId, TargetPos(c));
            }
        }

        /// <summary>
        /// FG1-SIG-07 场景夹具：沿预检路线每 150 格放一座已建成的信号中继塔（离路线 6～14 格、2×2 都能通行的空地，不挡路），
        /// 第一座离起点 140 格（在归还核心的 150 格覆盖里），相邻两座都在彼此的 200 格覆盖里——整条链与核心连通。返回放了几座。
        /// </summary>
        private static int PlaceRelayChain(CampaignState s, NavKernel k, GridCell from, List<int2> route)
        {
            var poly = new List<Vector2> { new Vector2(from.X, from.Y) };
            foreach (int2 p in route)
            {
                poly.Add(new Vector2(p.x, p.y));
            }
            var spots = new List<Vector2>();
            float next = 140f;
            float walked = 0f;
            for (int i = 1; i < poly.Count; i++)
            {
                float len = Vector2.Distance(poly[i - 1], poly[i]);
                while (len > 1e-3f && walked + len >= next)
                {
                    spots.Add(Vector2.Lerp(poly[i - 1], poly[i], (next - walked) / len));
                    next += 150f;
                }
                walked += len;
            }
            spots.Add(poly[poly.Count - 1]);
            var records = new List<BuildingRecord>(s.BuildingRecords ?? Array.Empty<BuildingRecord>());
            int placed = 0;
            foreach (Vector2 spot in spots)
            {
                if (!TryRelayCell(k, spot, poly, out GridCell cell))
                {
                    continue;
                }
                records.Add(new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":journey_relay_" + placed,
                    BuildingTypeId = HomeValleyLayout.BuildingTypeSignalRelay,
                    RegionId = HomeValleyLayout.RegionId,
                    GridX = cell.X,
                    GridY = cell.Y,
                    Position = GridMath.FootprintCenter(cell, 2, 2, 0),
                    Health = 100f,
                    ConstructionState = BuildingConstructionState.Operational,
                    PowerState = BuildingPowerState.NotApplicable,
                });
                placed++;
            }
            s.BuildingRecords = records.ToArray();
            GameLogic.Campaign.Signal.SignalCoverageService.Invalidate();
            return placed;
        }

        private static bool TryRelayCell(NavKernel k, Vector2 spot, List<Vector2> poly, out GridCell cell)
        {
            for (int ring = 6; ring <= 14; ring += 2)
            {
                for (int a = 0; a < 8; a++)
                {
                    double ang = a * Math.PI / 4;
                    var c = new GridCell((int)Math.Round(spot.x + Math.Cos(ang) * ring), (int)Math.Round(spot.y + Math.Sin(ang) * ring));
                    if (!AreaPassable(k, c, 1) || DistanceToPolyline(new Vector2(c.X, c.Y), poly) < 4f)
                    {
                        continue;
                    }
                    cell = c;
                    return true;
                }
            }
            cell = default;
            return false;
        }

        private static float DistanceToPolyline(Vector2 p, List<Vector2> poly)
        {
            float best = float.MaxValue;
            for (int i = 1; i < poly.Count; i++)
            {
                Vector2 a = poly[i - 1];
                Vector2 b = poly[i];
                Vector2 ab = b - a;
                float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
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
        private static void ResumeIfAutoPaused(JourneyContext c) => JourneyCommon.ResumeIfAutoPaused(c);

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

        /// <summary>FG1-E2E-01（DEBT-FG1HUD01-06）：按“跟随选中对象”键（默认 F，可重绑）——编队仍是选中集合，镜头飞回编队中心并一路跟随。
        /// 先读状态再按（已经在跟随就不按，重试不会把跟随切掉）。</summary>
        private static void PressFollow(JourneyContext c)
        {
            if (!WorldView.Director.IsFollowing)
            {
                c.SetInt("followPresses", c.GetInt("followPresses") + 1);
                PressAction(GameActionId.FollowSelection);
            }
        }

        private static StepOutcome TickFlownToSquad(JourneyContext c)
        {
            ResumeIfAutoPaused(c);
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            if (!WorldView.Director.IsFollowing)
            {
                return StepOutcome.Retry($"按了跟随键，镜头没有进入跟随（{GameLogic.Campaign.Signal.SignalUplinkService.LastFeedbackText}）");
            }
            Vector2 p = Centroid(Squad(c));
            float d = Vector2.Distance(CameraFocus(), p);
            if (d >= 3f)
            {
                return c.StepElapsed < 6 ? StepOutcome.Wait : StepOutcome.Fail($"跟随中，镜头焦点离编队中心仍有 {d:F1} 格（镜头范围把它钳住了？）");
            }
            return StepOutcome.Done($"跟随键：镜头飞回编队并跟随（焦点离编队中心 {d:F1} 格，离家园 {Vector2.Distance(p, HomeValleyLayout.Core.Position):F0} 格）");
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
                                        $"途中自动暂停 {c.GetInt("autoPauses")} 次；跟随中断后重按跟随键 {c.GetInt("follows")} 次");
            }
            // 镜头全程跟随编队（远处区块随镜头流式生成，FG0-ARCH-05）。跟随意外断了（不该发生）就像玩家一样再按一次跟随键，并计数写进报告。
            if (!WorldView.Director.IsFollowing && c.StepElapsed - c.GetLong("refollowAtMs") / 1000.0 >= 2)
            {
                c.SetLong("refollowAtMs", (long)(c.StepElapsed * 1000));
                c.SetInt("follows", c.GetInt("follows") + 1);
                PressAction(GameActionId.FollowSelection);
            }
            return StepOutcome.Wait;
        }

        private static void PauseIfRunning(JourneyContext c) => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true);

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
            // 与正式读档一样：通知中心绑定到读回的战役（运行时由 GameRoot 下一帧的 NotificationCenter.Tick 重绑；无头重放在同一帧里读档、推进，
            // 不先绑定的话“读档那一刻”的通知计数还是观察组那一局的历史，推进中第一条通知才触发重绑，增量就算错了——FG3-LOG-02 起去程会发“没有劳动力”）。
            NotificationCenter.Bind(r.State);
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
            c.Log(JourneyCommon.UiStats());
            _o1 = _o2 = _o2b = _o3 = null;
            JourneyCommon.Cleanup(c);
        }
    }
}
