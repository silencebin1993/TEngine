using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
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

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG1-E2E-01：M1 反向旅程 FGJ-M1R（00 契约 IC-REQ-022：资源不足、目标死亡、断链、暂停与倍速、存读档、路径失败，证明玩家能理解并恢复，而非软锁）。
    /// 从主菜单“新建”出发、走正式输入；换一颗测试种子（1，与 FGJ-M0 / M1 的 42 不同），证明旅程里的地点都按规则现找，不依赖固定坐标（B25）。
    ///
    /// 场景（每一段都先出错、看到原因、再用玩家能做的事恢复）：
    /// 1. 资源不足（电力）：开局发电机坏着，装配站没电——信号核面板刻印过载被拒绝并写明原因、不扣废料；右键修好发电机后再刻印成功，装进信号核。
    /// 2. 路径失败：右键点一个走不到的地方（按种子地形现找：目标格及附近都不可通行，或被围死）——命令结束、通知写明原因并可定位；再右键点走得到的地方，机器照常到达。
    /// 3. 暂停与倍速：战略暂停中发起接入——目标确认、镜头不动、世界不走；恢复运行后完成接入；接入中按 3 倍速——设置记住，但接入期间整个世界锁 1x；
    ///    离开后按 0.5x / 1x / 2x / 3x，每档的实际推进速度与档位一致。
    /// 4. 断链（走出覆盖）：派一台机器走到归还核心覆盖边缘，接入后开着它（WASD）驶出覆盖——边缘预警、宽限后断链、信号弹回核心、机器进入安全模式。
    /// 5. 存读档：安全模式中“保存并返回主菜单”再读档——安全模式记录（原因、进入的那一步）逐字段一致，头顶图标恢复。
    /// 6. 恢复：修好信号塔（运转且有电，覆盖 300 格）——那台机器回到覆盖里，2 游戏秒后自动退出安全模式；机器列表再接入它成功。
    /// 7. 目标死亡：接入中的机器被击毁（伤害夹具：家园还没有正式突袭，见 DEBT-FG1E2E01-04）——信号按死亡回弹规则弹到最近能接入的机器或回到核心，给原因与音效；
    ///    之后照常能离开 / 接入别的机器（不软锁）。
    /// </summary>
    public static class FgjM1ReverseJourney
    {
        public const string Id = "FGJ-M1R";

        /// <summary>与出口旅程不同的一颗测试种子（FgWorldGenSelfCheck 基准里有它）。</summary>
        public const int TestSeed = 1;

        public static JourneyDef Build() => new JourneyDef
        {
            Id = Id,
            Title = "M1 反向旅程：缺电刻印被拒→修发电机恢复｜路径失败→重下命令｜暂停中发起接入、接入锁 1x、各档倍速｜走出覆盖断链→安全模式中存读档→修信号塔恢复｜接入中机器阵亡→回弹不软锁",
            Seed = TestSeed,
            TotalTimeoutSeconds = 900,
            OnFinish = Cleanup,
            Steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, c => JourneyCommon.TickPlay(c, TestSeed)),
                S("menu_new", "主菜单点“新建”（测试种子 1）", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "进入归还谷地", 120, null, JourneyCommon.TickNewGame),
                S("seed", "生成结果与该种子的基准一致", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),

                // ── 1. 资源不足（电力）──
                S("r1_open", "按信号核键打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
                S("r1_print_denied", "发电机坏着时刻印过载：被拒绝并写明原因，不扣废料", 10, PrintOnce, TickPrintDenied),
                S("r1_close", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("r1_pick", "选定工程机", 10, PickWorkers, TickWorkers),
                S("r1_sel", "左键点工程机", 15, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 2),
                S("r1_repair", "右键点受损的发电机", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("r1_wait", "等发电机修好（恢复供电）", 240, null, c => TickBuilt(c, HomeValleyLayout.BuildingTypeGenerator)),
                S("r1_open_2", "再打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
                S("r1_print_ok", "有电之后刻印过载成功", 10, FgjM1Journey.PrintOverload, FgjM1Journey.TickPrinted, retries: 1),
                S("r1_equip", "装入 1 号槽", 10, c => FgjM1Journey.ClickUi(c, "[SignalCoreHost]", "SignalEquip"), FgjM1Journey.TickEquipped, retries: 1),
                S("r1_close_2", "关闭信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),

                // ── 3. 暂停与倍速 ──
                S("r3_sel", "左键点工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 12),
                S("r3_pause", "按暂停键（战略暂停）", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => GameClock.Paused, true), TickPausedNow, retries: 1),
                S("r3_uplink_paused", "暂停中按接入键：目标确认、镜头不动、世界不走", 10, c => FgjM1Journey.PressIf(GameActionId.ToggleCameraView, !SignalUplinkService.IsPending), TickPendingWhilePaused),
                S("r3_resume", "按暂停键恢复：过渡走完、接入完成", 10, c => JourneyInput.PressToggleTo(GameActionId.TogglePause, () => !GameClock.Paused, true), TickUplinkedAfterResume, retries: 1),
                S("r3_speed_locked", "接入中按 3 倍速键：档位记住，但世界锁 1x", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), c => TickRate(c, 1f, "接入中（锁 1x）")),
                S("r3_leave", "按接入 / 退出键离开", 10, c => FgjM1Journey.PressIf(GameActionId.ToggleCameraView, SignalPresence.CurrentMachineLogicId != 0), TickLeft, retries: 1),
                S("r3_rate3", "离开后 3 倍速生效", 10, null, c => TickRate(c, 3f, "离开后 3x")),
                S("r3_rate05", "按 0.5 倍速键", 10, c => JourneyInput.PressAction(GameActionId.SpeedHalf), c => TickRate(c, 0.5f, "0.5x")),
                S("r3_rate2", "按 2 倍速键", 10, c => JourneyInput.PressAction(GameActionId.SpeedDouble), c => TickRate(c, 2f, "2x")),
                S("r3_rate1", "按 1 倍速键", 10, c => JourneyInput.PressAction(GameActionId.SpeedNormal), c => TickRate(c, 1f, "1x")),
                S("r3_rate3b", "按 3 倍速键（后面的等待用 3x）", 10, c => JourneyInput.PressAction(GameActionId.SpeedTriple), c => TickRate(c, 3f, "3x")),

                // ── 4. 断链（走出覆盖）──
                S("r4_home", "按回到归还核心键", 10, c => JourneyInput.PressAction(GameActionId.FocusHomeCore), TickCameraHome, retries: 1),
                S("r4_sel", "左键点工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 12),
                S("r4_find", "按种子地形找一条从核心附近开出覆盖的路（离核心 150 格外），镜头平移到出发点", 90, FindEdge, c => TickPannedTo(c, "edge")),
                S("r4_go", "右键点出发点：机器走过去", 150, c => RightClickTarget(c, "edge"), TickAtEdge),
                S("r4_uplink", "按接入键接入它", 10, c => FgjM1Journey.PressIf(GameActionId.ToggleCameraView, SignalPresence.CurrentMachineLogicId == 0), TickUplinkedWorker, retries: 1),
                S("r4_drive", "开着它（WASD，沿探好的路）一路驶出覆盖：边缘预警 → 宽限后断链 → 安全模式", 180, null, TickDriveOut),
                S("r4_reject", "机器列表点覆盖外的那台：信号到不了（没有连通的覆盖路径），拒绝并写明原因", 15, c => FgjM1Journey.ClickMachineList(c, c.GetInt("workerA")), TickOutsideRejected, retries: 3),

                // ── 5. 存读档（安全模式中）──
                S("r5_esc", "按 Esc 打开暂停菜单", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseMenu, retries: 1),
                S("r5_save", "点“保存并返回主菜单”，确认", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), TickSaveConfirmed),
                S("r5_menu", "回到主菜单：存档里安全模式记录逐字段一致", 60, null, TickMenuDisk),
                S("r5_load", "点“读取”，点刚才的存档槽", 20, c => FgjM1Journey.ClickUgui(c, "m_btn_Load"), TickLoadClicked),
                S("r5_loaded", "读档进入家园：那台机器仍在安全模式、头顶图标恢复", 120, null, TickLoadedSafe),

                // ── 6. 恢复：修信号塔扩大覆盖 ──
                S("r6_speed", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("r6_home", "按回到归还核心键", 10, c => JourneyInput.PressAction(GameActionId.FocusHomeCore), TickCameraHome, retries: 1),
                S("r6_sel", "左键点家园里的工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 12),
                S("r6_repair", "右键点受损的信号塔", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeSignalTower),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeSignalTower), retries: 2),
                S("r6_wait", "等信号塔修好：覆盖扩大，那台机器自动退出安全模式", 300, null, TickRecovered),
                S("r6_relink", "机器列表点它：再次接入成功", 20, c => FgjM1Journey.ClickMachineList(c, c.GetInt("workerA")), TickRelinked, retries: 3),

                // ── 7. 目标死亡 ──
                S("r7_kill", "接入中的机器被击毁（伤害夹具）：信号回弹，给原因与音效", 15, KillUplinked, TickRebound),
                S("r7_after", "之后照常能离开 / 接入：不软锁", 20, LeaveOrUplinkAfterDeath, TickNotSoftlocked),

                // ── 8. 路径失败（放在修好信号塔之后：已探索范围随信号塔覆盖扩到 300 格，按种子地形才找得到走不到的地方）──
                S("r2_home0", "按回到归还核心键", 10, c => JourneyInput.PressAction(GameActionId.FocusHomeCore), TickCameraHome, retries: 1),
                S("r2_sel", "左键点另一台工程机", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 12),
                S("r2_find", "按种子地形找一个走不到的地方，镜头平移过去（方向键）", 60, FindUnreachable, c => TickPannedTo(c, "bad")),
                S("r2_click_bad", "右键点那里：命令结束，通知写明原因", 30, c => RightClickTarget(c, "bad"), TickUnreachableReported),
                S("r2_home", "按回到归还核心键（镜头回到工程机附近）", 10, c => JourneyInput.PressAction(GameActionId.FocusHomeCore), TickCameraHome, retries: 1),
                S("r2_click_ok", "再右键点一个走得到的地方：机器照常到达", 60, RightClickReachable, TickArrived),

            },
        };

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static CampaignState St => CampaignSession.Current;
        private static string Label(int id) => FgjM1Journey.Label(id);

        // ── 1. 资源不足（电力）──────────────────────────────────────────────────────

        private static void PrintOnce(JourneyContext c)
        {
            c.SetInt("scrapBefore", St.Scrap);
            c.SetInt("chipsBefore", St.PrimitiveChips?.Length ?? 0);
            DropdownField print = JourneyInput.FindUitk<DropdownField>("[SignalCoreHost]", "SignalPrintChoice");
            string overload = GameText.Get("firmware.fw_overload.name");
            string choice = print?.choices?.FirstOrDefault(x => x.Contains(overload));
            if (print != null && choice != null)
            {
                print.value = choice;
                FgjM1Journey.ClickUi(c, "[SignalCoreHost]", "SignalPrint");
            }
            else
            {
                c.Set("uiFail", "刻印下拉里没有过载");
            }
        }

        private static StepOutcome TickPrintDenied(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            BuildingRecord gen = FgjM1Journey.Building(HomeValleyLayout.BuildingTypeGenerator);
            string want = GameText.Get("signal.reason.print_no_power");
            bool denied = hud != null && hud.FeedbackIsError && hud.FeedbackText.Contains(want) && St.Scrap == c.GetInt("scrapBefore")
                          && (St.PrimitiveChips?.Length ?? 0) == c.GetInt("chipsBefore");
            if (gen == null || gen.ConstructionState == BuildingConstructionState.Operational)
            {
                return StepOutcome.Fail("前提不对：开局发电机没有坏（这颗种子的开局布局变了？）");
            }
            return denied
                ? StepOutcome.Done($"发电机坏着（装配站没电）：刻印被拒绝“{hud.FeedbackText}”，废料 {St.Scrap} 不变、没有多出芯片")
                : StepOutcome.Fail($"缺电刻印没有给出原因：“{hud?.FeedbackText}”（错误态 {hud?.FeedbackIsError}；{FgjM1Journey.UiFail(c)}）；废料 {c.GetInt("scrapBefore")} → {St.Scrap}");
        }

        private static void PickWorkers(JourneyContext c)
        {
            List<MachineRecord> home = MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && !m.IsInFactory && m.RegionId == HomeValleyLayout.RegionId)
                .OrderBy(m => m.LogicId).ToList();
            List<MachineRecord> workers = home.Where(m => m.ChassisId == HomeValleyLayout.Erc001ChassisId).ToList();
            if (workers.Count < 2)
            {
                workers = home;
            }
            c.SetInt("workerA", workers.Count > 0 ? workers[0].LogicId : 0);
            c.SetInt("workerB", workers.Count > 1 ? workers[1].LogicId : 0);
        }

        private static StepOutcome TickWorkers(JourneyContext c) =>
            c.GetInt("workerA") != 0 && c.GetInt("workerB") != 0
                ? StepOutcome.Done($"工程机 {Label(c.GetInt("workerA"))}、{Label(c.GetInt("workerB"))}")
                : StepOutcome.Fail("家园里找不到两台可以派工的机器");

        private static StepOutcome TickBuilt(JourneyContext c, string typeId)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            BuildingRecord b = FgjM1Journey.Building(typeId);
            if (b != null && b.ConstructionState == BuildingConstructionState.Operational)
            {
                return StepOutcome.Done($"{typeId} 修好（{c.StepElapsed:F0} 秒，3 倍速），废料 {St.Scrap}");
            }
            if (FgjM1Journey.RepairOrder(typeId) == null && c.StepElapsed > 5)
            {
                return StepOutcome.Fail($"{typeId} 还没修好，修复工单却没了（废料 {St.Scrap}）");
            }
            return StepOutcome.Wait;
        }

        // ── 2. 路径失败 ──────────────────────────────────────────────────────────────

        private static Vector2 Target(JourneyContext c, string tag) =>
            new Vector2(float.Parse(c.Get(tag + "X", "0"), CultureInfo.InvariantCulture), float.Parse(c.Get(tag + "Y", "0"), CultureInfo.InvariantCulture));

        private static void SetTarget(JourneyContext c, string tag, Vector2 p)
        {
            c.Set(tag + "X", p.x.ToString("R", CultureInfo.InvariantCulture));
            c.Set(tag + "Y", p.y.ToString("R", CultureInfo.InvariantCulture));
            c.SetInt(tag + "Found", 1);
        }

        private static NavKernel NewProbe(out string why)
        {
            why = null;
            var src = HomeGridService.MapFor(St)?.TerrainSource as WorldTerrainSource;
            if (src == null || !NavService.IsBound)
            {
                why = "家园地形源 / 寻路服务没有就绪";
                return null;
            }
            return new NavKernel(NavService.ConfigFromTuning(), NavService.TerrainTable(), true, src.Params, src.Rects, src.Zones);
        }

        private static bool Probe(NavKernel k, GridCell from, GridCell to, int serial, out NavResult res)
        {
            var pts = new List<int2>();
            var req = new NavRequest
            {
                OwnerTag = 9,
                OwnerKey = 2,
                Serial = serial,
                Class = NavConst.ClassPlayer,
                Flags = NavRequestFlags.None,
                Start = new int2(from.X, from.Y),
                Goal = new int2(to.X, to.Y),
            };
            res = k.FindNow(req, pts, onWorker: true, out _);
            return res.Status == NavStatus.Ok;
        }

        private static bool LivePos(int logicId, out Vector2 p)
        {
            p = default;
            return GameRoot.HomeValley?.Combat != null && GameRoot.HomeValley.Combat.TryGetMachinePosition(logicId, out p);
        }

        /// <summary>
        /// 从归还核心向外按环找一个走不到的地方（独立寻路内核，按种子生成地形，与游戏里的内核同一算法）：
        /// ① 目标格周围（正式寻路的目标吸附半径 +1 以内）全都不可通行 → 吸附不到（GoalBlocked）；② 或者目标格能走、却被围死（Unreachable）。
        /// 只在已探索的范围内找（镜头平移得过去）。找到后用独立内核从工程机所在格实际寻一次路确认失败。
        /// </summary>
        private static void FindUnreachable(JourneyContext c)
        {
            c.SetInt("badFound", 0);
            int w = c.GetInt("workerB");
            if (!LivePos(w, out Vector2 me))
            {
                c.Set("badMiss", "工程机不见了");
                return;
            }
            using NavKernel k = NewProbe(out string why);
            if (k == null)
            {
                c.Set("badMiss", why);
                return;
            }
            GridCell from = NavService.CellOf(me.x, me.y);
            GridCell core = HomeGridService.CorePivot(St);
            int radius = Math.Min(160, ExploredRadius(core) - 10);
            int snap = NavService.ConfigFromTuning().GoalSearchRadius + 1;
            int blocked = 0;
            int tried = 0;
            // ① 大片不可通行（水面 / 悬崖）：先便宜地查一圈，再实际寻路确认。
            for (int r = 12; r <= radius && tried < 60; r += 2)
            {
                for (int a = 0; a < 48; a++)
                {
                    double ang = a * Math.PI / 24;
                    var g = new GridCell(core.X + (int)Math.Round(Math.Cos(ang) * r), core.Y + (int)Math.Round(Math.Sin(ang) * r));
                    if (k.Passable(g.X, g.Y, NavConst.ClassPlayer) || !ExploredWithMargin(g, 10))
                    {
                        continue;
                    }
                    blocked++;
                    if (!AreaBlocked(k, g, snap))
                    {
                        continue;
                    }
                    tried++;
                    if (!Probe(k, from, g, tried, out NavResult res))
                    {
                        SetTarget(c, "bad", new Vector2(g.X, g.Y));
                        c.Log($"走不到的地方 {g}：离核心 {r} 格，周围 {snap} 格内都不可通行，独立寻路判定 {res.Status} / {res.Reason}（不可通行格 {blocked} 个，实测 {tried} 个）");
                        return;
                    }
                }
            }
            // ② 被围死的能走格（盆地）：能走、但从工程机寻路失败。
            for (int r = 12; r <= radius && tried < 200; r += 3)
            {
                for (int a = 0; a < 48; a++)
                {
                    double ang = a * Math.PI / 24;
                    var g = new GridCell(core.X + (int)Math.Round(Math.Cos(ang) * r), core.Y + (int)Math.Round(Math.Sin(ang) * r));
                    if (!k.Passable(g.X, g.Y, NavConst.ClassPlayer) || !ExploredWithMargin(g, 10) || !NearBlocked(k, g, 3))
                    {
                        continue;
                    }
                    tried++;
                    if (!Probe(k, from, g, 1000 + tried, out NavResult res))
                    {
                        SetTarget(c, "bad", new Vector2(g.X, g.Y));
                        c.Log($"走不到的地方 {g}：离核心 {r} 格，能走但被围死，独立寻路判定 {res.Status} / {res.Reason}（实测 {tried} 个）");
                        return;
                    }
                }
            }
            // ③ 自然地形里找不到（FG17：家园起始区按规划层保证可建造、可通行，这很正常）→ 地形夹具（DEBT-FG1E2E01-05）：
            //    在工程机附近的空地围一圈悬崖，造出一个被围死的盆地（与 [层级寻路] 自检 CheckSquadUnreachableAndFog 同一写法），点盆地中心。
            //    之后的右键、寻路失败、通知、恢复全是正式流程。
            if (TryCliffPocket(k, me, out GridCell pocket))
            {
                SetTarget(c, "bad", new Vector2(pocket.X, pocket.Y));
                c.SetInt("badFixture", 1);
                c.Log($"自然地形里找不到走不到的地方（已探索 {radius} 格内不可通行格 {blocked} 个、实测 {tried} 个都走得到——家园起始区按 FG17 保证可通行）；" +
                      $"地形夹具：在 {pocket} 周围 3 格围一圈悬崖造出被围死的盆地（DEBT-FG1E2E01-05）");
                return;
            }
            c.Set("badMiss", $"已探索的 {radius} 格内：不可通行格 {blocked} 个，大片不可通行 / 被围死的候选实测 {tried} 个，都能走到或吸附到附近能走的格；工程机附近也没有能围盆地的空地（种子 {TestSeed}）");
        }

        /// <summary>地形夹具：离工程机 12～22 格、画面可达的一块 9×9 空地（全可通行、没有建筑），把离中心 3 格的一圈改成悬崖。</summary>
        private static bool TryCliffPocket(NavKernel k, Vector2 worker, out GridCell center)
        {
            center = default;
            CampaignState s = St;
            HomeGridMap map = HomeGridService.MapFor(s);
            if (map == null)
            {
                return false;
            }
            GridCell w = NavService.CellOf(worker.x, worker.y);
            for (int r = 12; r <= 22; r += 2)
            {
                for (int a = 0; a < 16; a++)
                {
                    double ang = a * Math.PI / 8;
                    var g = new GridCell(w.X + (int)Math.Round(Math.Cos(ang) * r), w.Y + (int)Math.Round(Math.Sin(ang) * r));
                    if (!AreaPassable(k, g, 4) || !ExploredWithMargin(g, 8))
                    {
                        continue;
                    }
                    bool free = true;
                    for (int y = -4; y <= 4 && free; y++)
                    {
                        for (int x = -4; x <= 4 && free; x++)
                        {
                            free = HomeGridService.BuildingAt(s, new GridCell(g.X + x, g.Y + y)) == null;
                        }
                    }
                    if (!free)
                    {
                        continue;
                    }
                    byte cliff = GridContent.TerrainCode("cliff");
                    for (int dy = -3; dy <= 3; dy++)
                    {
                        for (int dx = -3; dx <= 3; dx++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == 3)
                            {
                                map.SetTerrain(new GridCell(g.X + dx, g.Y + dy), cliff);
                            }
                        }
                    }
                    center = g;
                    return true;
                }
            }
            return false;
        }

        /// <summary>已探索区从核心往外能到多远（开局探索圈；信号塔接上网络后按它的覆盖扩张的探索圈）。</summary>
        private static int ExploredRadius(GridCell core)
        {
            int best = GridContent.TuningInt("grid.explored_radius_start");
            foreach (ExploredAreaRecord a in St?.Grid?.Explored ?? Array.Empty<ExploredAreaRecord>())
            {
                if (a != null)
                {
                    double d = Math.Sqrt((double)(a.CenterX - core.X) * (a.CenterX - core.X) + (double)(a.CenterY - core.Y) * (a.CenterY - core.Y));
                    best = Math.Max(best, (int)(a.Radius - d));
                }
            }
            return best;
        }

        /// <summary>这一格在某个已探索圆里、离圆边至少 <paramref name="margin"/> 格（镜头平移得到、画面里点得到）。</summary>
        private static bool ExploredWithMargin(GridCell g, int margin)
        {
            foreach (ExploredAreaRecord a in St?.Grid?.Explored ?? Array.Empty<ExploredAreaRecord>())
            {
                if (a == null)
                {
                    continue;
                }
                double d = Math.Sqrt((double)(a.CenterX - g.X) * (a.CenterX - g.X) + (double)(a.CenterY - g.Y) * (a.CenterY - g.Y));
                if (d + margin <= a.Radius)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool AreaBlocked(NavKernel k, GridCell g, int half)
        {
            for (int y = -half; y <= half; y++)
            {
                for (int x = -half; x <= half; x++)
                {
                    if (k.Passable(g.X + x, g.Y + y, NavConst.ClassPlayer))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool NearBlocked(NavKernel k, GridCell g, int half)
        {
            for (int y = -half; y <= half; y++)
            {
                for (int x = -half; x <= half; x++)
                {
                    if (!k.Passable(g.X + x, g.Y + y, NavConst.ClassPlayer))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>镜头平移到目标（按住方向键，玩家的平移方式），直到目标在画面里。</summary>
        private static StepOutcome TickPannedTo(JourneyContext c, string tag)
        {
            if (c.GetInt(tag + "Found") == 0)
            {
                return StepOutcome.Fail("找不到目标：" + c.Get(tag + "Miss", string.Empty));
            }
            Vector2 t = Target(c, tag);
            if (JourneyInput.OnScreen(t, 0.2f))
            {
                JourneyInput.ReleaseKeys();
                return c.StepElapsed < 0.5 ? StepOutcome.Wait : StepOutcome.Done($"目标 ({t.x:F0},{t.y:F0}) 在画面里（按方向键平移 {c.GetInt("pans")} 次）");
            }
            if (JourneyInput.Holding)
            {
                return StepOutcome.Wait;
            }
            float2 f = WorldView.Director.StrategyFocus;
            Vector2 d = t - new Vector2(f.x, f.y);
            var keys = new List<KeyCode>(2);
            if (Mathf.Abs(d.x) > 4f)
            {
                keys.Add(GameSettings.KeyBindings.GetKey(d.x > 0 ? GameActionId.StrategyPanRight : GameActionId.StrategyPanLeft));
            }
            if (Mathf.Abs(d.y) > 4f)
            {
                keys.Add(GameSettings.KeyBindings.GetKey(d.y > 0 ? GameActionId.StrategyPanUp : GameActionId.StrategyPanDown));
            }
            if (keys.Count == 0)
            {
                return StepOutcome.Fail($"镜头焦点已到目标附近，目标却不在画面里（焦点 {f}，目标 {t}）");
            }
            c.SetInt("pans", c.GetInt("pans") + 1);
            JourneyInput.HoldKeys(keys, 0.3);
            return StepOutcome.Wait;
        }

        private static void RightClickTarget(JourneyContext c, string tag)
        {
            c.SetInt("unreach0", NotificationCenter.History.Where(e => e?.Type?.Id == "unreachable").Sum(e => e.Count));
            c.SetInt("denied0", FeedbackCues.CountOf(FeedbackCueId.Denied));
            JourneyInput.Click(Target(c, tag), button: 1);
        }

        private static StepOutcome TickUnreachableReported(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            int w = c.GetInt("workerB");
            int unreach = NotificationCenter.History.Where(e => e?.Type?.Id == "unreachable").Sum(e => e.Count);
            if (unreach <= c.GetInt("unreach0"))
            {
                return StepOutcome.Wait;
            }
            NotificationEntry note = NotificationCenter.History.LastOrDefault(e => e?.Type?.Id == "unreachable");
            CombatSite site = GameRoot.HomeValley.Combat;
            bool ended = !site.TryGetMachineUnit(w, out int unit) || !site.TryGetCommand(unit, out BinGames.Sim.Combat.CombatCommand cmd)
                         || cmd.Kind != BinGames.Sim.Combat.CombatCommandKind.Move;
            bool located = note != null && note.HasAnyLocation;
            return ended && located && FeedbackCues.CountOf(FeedbackCueId.Denied) > c.GetInt("denied0")
                ? StepOutcome.Done($"命令结束，不原地发呆：通知“{note.Text}”（可点击定位到目标点）+ 拒绝音；{Label(w)} 交还 AI")
                : StepOutcome.Fail($"走不到时反馈不全：命令已结束 {ended}、通知可定位 {located}、拒绝音 {FeedbackCues.CountOf(FeedbackCueId.Denied) - c.GetInt("denied0")}");
        }

        private static void RightClickReachable(JourneyContext c)
        {
            int w = c.GetInt("workerB");
            if (!LivePos(w, out Vector2 me))
            {
                return;
            }
            using NavKernel k = NewProbe(out _);
            GridCell from = NavService.CellOf(me.x, me.y);
            for (int r = 6; r <= 20 && k != null; r += 2)
            {
                for (int a = 0; a < 16; a++)
                {
                    double ang = a * Math.PI / 8;
                    var g = new GridCell(from.X + (int)Math.Round(Math.Cos(ang) * r), from.Y + (int)Math.Round(Math.Sin(ang) * r));
                    if (!k.Passable(g.X, g.Y, NavConst.ClassPlayer) || !JourneyInput.OnScreen(new Vector2(g.X, g.Y), 0.1f) || !Probe(k, from, g, 500 + a, out _))
                    {
                        continue;
                    }
                    SetTarget(c, "ok", new Vector2(g.X, g.Y));
                    JourneyInput.Click(new Vector2(g.X, g.Y), button: 1);
                    return;
                }
            }
        }

        private static StepOutcome TickArrived(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.GetInt("okFound") == 0)
            {
                return StepOutcome.Fail("工程机附近、画面内找不到走得到的地方");
            }
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            int w = c.GetInt("workerB");
            if (!LivePos(w, out Vector2 me))
            {
                return StepOutcome.Fail("工程机不见了");
            }
            float d = Vector2.Distance(me, Target(c, "ok"));
            return d <= 2.5f ? StepOutcome.Done($"再右键点走得到的地方：{Label(w)} 到达（离目标 {d:F1} 格）") : StepOutcome.Wait;
        }

        // ── 3. 暂停与倍速 ────────────────────────────────────────────────────────────

        private static StepOutcome TickPausedNow(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return GameClock.Paused ? StepOutcome.Done("战略暂停") : StepOutcome.Retry("按了暂停键，世界没有暂停");
        }

        private static StepOutcome TickPendingWhilePaused(JourneyContext c)
        {
            if (c.GetLong("pausedTicks") == 0)
            {
                c.SetLong("pausedTicks", GameClock.Ticks + 1); // +1：避免 0 与“没记”混淆
            }
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            string status = SignalCoreHudUIToolkit.Instance?.UplinkStatusText ?? string.Empty;
            bool ok = SignalUplinkService.PendingWaitsForResume && SignalPresence.AtCore && WorldView.Director.Mode == ViewMode.Strategy
                      && GameClock.Ticks + 1 == c.GetLong("pausedTicks") && status.Contains(Label(c.GetInt("workerA")));
            return ok
                ? StepOutcome.Done($"暂停中发起接入：目标已确认、镜头不动、世界停在第 {GameClock.Ticks} 步；HUD“{status}”")
                : StepOutcome.Fail($"暂停中发起接入不对：等待恢复 {SignalUplinkService.PendingWaitsForResume}、信号在核心 {SignalPresence.AtCore}、步数 {c.GetLong("pausedTicks") - 1} → {GameClock.Ticks}、HUD“{status}”");
        }

        private static StepOutcome TickUplinkedAfterResume(JourneyContext c)
        {
            if (c.StepElapsed < 1.2)
            {
                return StepOutcome.Wait;
            }
            int w = c.GetInt("workerA");
            return !GameClock.Paused && SignalPresence.CurrentMachineLogicId == w && WorldView.Director.Mode == ViewMode.Direct
                ? StepOutcome.Done($"恢复运行：过渡走完，接入 {Label(w)}（镜头直控）")
                : StepOutcome.Retry($"恢复后没有接入（暂停 {GameClock.Paused}，信号在 {SignalPresence.CurrentMachineLogicId}）");
        }

        /// <summary>量 1.5 真实秒里统一时钟走了多少步，换算成倍率，与期望比（±35%，batchmode 帧率抖动）。同时核对设置档位与实际倍率。</summary>
        private static StepOutcome TickRate(JourneyContext c, float want, string what)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            string kT = "rateT0." + c.StepIndex;
            string kW = "rateW0." + c.StepIndex;
            if (c.GetLong(kT) == 0)
            {
                c.SetLong(kT, GameClock.Ticks + 1);
                c.Set(kW, EditorApplication.timeSinceStartup.ToString("R", CultureInfo.InvariantCulture));
                return StepOutcome.Wait;
            }
            double w0 = double.Parse(c.Get(kW), CultureInfo.InvariantCulture);
            double wall = EditorApplication.timeSinceStartup - w0;
            if (wall < 1.5)
            {
                return StepOutcome.Wait;
            }
            long ticks = GameClock.Ticks - (c.GetLong(kT) - 1);
            double rate = ticks / wall / GameClock.StepHz;
            bool ok = Math.Abs(rate - want) <= want * 0.35 && !GameClock.Paused;
            string setting = $"档位 {GameClock.Speed}x、实际生效 {GameClock.EffectiveSpeed}x";
            return ok && Mathf.Approximately(GameClock.EffectiveSpeed, want)
                ? StepOutcome.Done($"{what}：{setting}；{wall:F1} 真实秒推进 {ticks} 步 ≈ {rate:F2}x")
                : StepOutcome.Fail($"{what}倍率不对：{setting}；{wall:F1} 真实秒推进 {ticks} 步 ≈ {rate:F2}x（期望 {want}x）");
        }

        private static StepOutcome TickLeft(JourneyContext c)
        {
            if (c.StepElapsed < 1.2)
            {
                return StepOutcome.Wait;
            }
            return SignalPresence.AtCore && WorldView.Director.Mode == ViewMode.Strategy
                ? StepOutcome.Done("离开：信号回到归还核心、镜头回战略")
                : StepOutcome.Retry("按退出键后信号还在机器里");
        }

        // ── 4. 断链（走出覆盖）──────────────────────────────────────────────────────

        /// <summary>
        /// 找一条开出覆盖的路：出发点在开局已探索区里（离核心 (探索半径 − 6) 格，镜头平移得到、右键点得到），终点在核心覆盖外 20 格；
        /// 用独立寻路内核（按种子生成地形，与游戏同一算法）从工程机到出发点、从出发点到终点各寻一次路，记下路点——接入后沿路点按 WASD 开出去。
        /// 此时信号塔还坏着：覆盖只有核心的一圈。按种子地形现找，不写死坐标。
        /// </summary>
        private static void FindEdge(JourneyContext c)
        {
            c.SetInt("edgeFound", 0);
            int w = c.GetInt("workerA");
            if (!LivePos(w, out Vector2 me))
            {
                c.Set("edgeMiss", "工程机不见了");
                return;
            }
            using NavKernel k = NewProbe(out string why);
            if (k == null)
            {
                c.Set("edgeMiss", why);
                return;
            }
            float coverR = SignalCoverageService.CoreRadius;
            GridCell core = HomeGridService.CorePivot(St);
            GridCell from = NavService.CellOf(me.x, me.y);
            float startR = GridContent.TuningInt("grid.explored_radius_start") - 6f;
            int tried = 0;
            for (int a = 0; a < 32 && tried < 32; a++)
            {
                double ang = a * Math.PI / 16;
                var dir = new Vector2((float)Math.Cos(ang), (float)Math.Sin(ang));
                Vector2 edge = new Vector2(core.X, core.Y) + dir * startR;
                Vector2 outside = new Vector2(core.X, core.Y) + dir * (coverR + 20f);
                var g = new GridCell(Mathf.RoundToInt(edge.x), Mathf.RoundToInt(edge.y));
                var g2 = new GridCell(Mathf.RoundToInt(outside.x), Mathf.RoundToInt(outside.y));
                if (!AreaPassable(k, g, 2) || !AreaPassable(k, g2, 2))
                {
                    continue;
                }
                tried++;
                if (!Probe(k, from, g, 900 + a, out _))
                {
                    continue;
                }
                var pts = new List<int2>();
                var req = new NavRequest
                {
                    OwnerTag = 9, OwnerKey = 3, Serial = 950 + a, Class = NavConst.ClassPlayer, Flags = NavRequestFlags.None,
                    Start = new int2(g.X, g.Y), Goal = new int2(g2.X, g2.Y),
                };
                NavResult res = k.FindNow(req, pts, onWorker: true, out _);
                float straight = coverR + 20f - startR;
                if (res.Status != NavStatus.Ok || pts.Count == 0 || res.Length > straight * 1.8f)
                {
                    continue;
                }
                // 路点：寻路结果已经是合并过共线段的拐点，全部按顺序开过去（跳过拐点会直线撞上地形）。
                var wps = new List<string>();
                for (int i = 0; i < pts.Count; i++)
                {
                    wps.Add(pts[i].x.ToString(CultureInfo.InvariantCulture) + "," + pts[i].y.ToString(CultureInfo.InvariantCulture));
                }
                wps.Add(g2.X.ToString(CultureInfo.InvariantCulture) + "," + g2.Y.ToString(CultureInfo.InvariantCulture));
                SetTarget(c, "edge", new Vector2(g.X, g.Y));
                c.Set("route", string.Join(";", wps));
                c.SetInt("wp", 0);
                c.Log($"出发点 {g}（离核心 {startR:F0} 格，开局已探索区里），终点 {g2}（核心覆盖 {coverR:F0} 格外 20 格），方向 {a * 360 / 32}°；探好的路长 {res.Length:F0} 格、{wps.Count} 个路点");
                return;
            }
            c.Set("edgeMiss", $"32 个方向里找不到从开局探索区开出覆盖的路（候选 {tried} 个）");
        }

        private static Vector2 Wp(string xy)
        {
            string[] p = xy.Split(',');
            return new Vector2(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture));
        }

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

        private static StepOutcome TickAtEdge(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            int w = c.GetInt("workerA");
            if (!LivePos(w, out Vector2 me))
            {
                return StepOutcome.Fail("工程机不见了");
            }
            float d = Vector2.Distance(me, Target(c, "edge"));
            if (d > 3f)
            {
                return StepOutcome.Wait;
            }
            float fromCore = Vector2.Distance(me, SignalUplinkService.CorePosition(St));
            return SignalCoverageService.Sample(HomeValleyLayout.RegionId, me, w).Covered
                ? StepOutcome.Done($"{Label(w)} 走到出发点（离核心 {fromCore:F0} 格，在覆盖里）")
                : StepOutcome.Fail($"{Label(w)} 还没驶出就已经在覆盖外了（离核心 {fromCore:F0} 格）");
        }

        private static StepOutcome TickUplinkedWorker(JourneyContext c)
        {
            if (c.StepElapsed < 1.2)
            {
                return StepOutcome.Wait;
            }
            int w = c.GetInt("workerA");
            return SignalPresence.CurrentMachineLogicId == w && WorldView.Director.Mode == ViewMode.Direct
                ? StepOutcome.Done($"接入 {Label(w)}（覆盖边缘，HUD 链路“{SignalCoreHudUIToolkit.Instance?.UplinkHud?.LinkText}”）")
                : StepOutcome.Retry($"没有接入（{SignalUplinkService.LastFeedbackText}）");
        }

        private static StepOutcome TickDriveOut(JourneyContext c)
        {
            int w = c.GetInt("workerA");
            CampaignState s = St;
            if (SignalLinkService.IsInSafeMode(s, w))
            {
                JourneyInput.ReleaseKeys();
                // 断链那一刻镜头开始拉回战略、头顶图标下一帧才挂上：等 1.5 秒再核对。
                if (c.GetLong("safeAtMs") == 0)
                {
                    c.SetLong("safeAtMs", Math.Max(1, (long)(c.StepElapsed * 1000)));
                    return StepOutcome.Wait;
                }
                if (c.StepElapsed - c.GetLong("safeAtMs") / 1000.0 < 1.5)
                {
                    return StepOutcome.Wait;
                }
                SignalSafeModeRecord rec = s.SignalCore.SafeModes.First(m => m.LogicId == w);
                LivePos(w, out Vector2 at);
                bool ok = rec.Reason == (int)SignalLinkBreakReason.OutOfCoverage && SignalPresence.AtCore && SignalLinkView.BadgeWanted(w) && c.GetInt("warned") == 1;
                c.SetLong("safeSince", rec.SinceTick);
                return ok
                    ? StepOutcome.Done($"{Label(w)} 驶出覆盖（离核心 {Vector2.Distance(at, SignalUplinkService.CorePosition(s)):F0} 格）：先有边缘预警，宽限后断链——信号弹回核心、" +
                                       $"{Label(w)} 进入安全模式（原因：{SignalLinkService.ReasonName(SignalLinkBreakReason.OutOfCoverage)}，头顶图标）；“{SignalUplinkService.LastFeedbackText}”")
                    : StepOutcome.Fail($"断链后状态不对：原因 {rec.Reason}、信号在核心 {SignalPresence.AtCore}、图标 {SignalLinkView.BadgeWanted(w)}、见过边缘预警 {c.GetInt("warned")}");
            }
            if (SignalPresence.CurrentMachineLogicId != w)
            {
                return StepOutcome.Fail($"驶出前信号已不在 {Label(w)} 里（{SignalUplinkService.LastFeedbackText}）");
            }
            string status = SignalCoreHudUIToolkit.Instance?.UplinkStatusText ?? string.Empty;
            if (c.GetInt("warned") == 0 && SignalLinkService.EdgeWarningActive)
            {
                c.SetInt("warned", 1);
                c.Log($"边缘预警：HUD“{status}”");
            }
            if (JourneyInput.Holding)
            {
                return StepOutcome.Wait;
            }
            if (LivePos(w, out Vector2 me))
            {
                string[] route = c.Get("route", string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                int wp = c.GetInt("wp");
                while (wp < route.Length - 1 && Vector2.Distance(me, Wp(route[wp])) < 2f)
                {
                    wp++;
                }
                c.SetInt("wp", wp);
                if (route.Length > 0)
                {
                    // 卡住检测：1.5 真实秒里离当前路点没近 0.5 格 → 侧向绕 0.6 秒（左右交替）。
                    Vector2 target = Wp(route[Math.Min(wp, route.Length - 1)]);
                    float dist = Vector2.Distance(me, target);
                    double now = c.StepElapsed;
                    double since = c.GetLong("progAtMs") / 1000.0;
                    float best = float.TryParse(c.Get("progBest", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out float pb) ? pb : float.MaxValue;
                    if (dist < best - 0.5f || c.GetInt("progWp") != wp)
                    {
                        c.Set("progBest", dist.ToString("R", CultureInfo.InvariantCulture));
                        c.SetLong("progAtMs", (long)(now * 1000));
                        c.SetInt("progWp", wp);
                        FgjM1Journey.DriveToward(me, target, 0.3);
                    }
                    else if (now - since > 1.5)
                    {
                        int side = c.GetInt("side") >= 0 ? 1 : -1;
                        c.SetInt("side", -side);
                        c.SetInt("sidesteps", c.GetInt("sidesteps") + 1);
                        c.Set("progBest", dist.ToString("R", CultureInfo.InvariantCulture));
                        c.SetLong("progAtMs", (long)(now * 1000));
                        FgjM1Journey.DriveToward(me, target, 0.6, side);
                    }
                    else
                    {
                        FgjM1Journey.DriveToward(me, target, 0.3);
                    }
                }
                int bucket = (int)(c.StepElapsed / 30);
                if (bucket > c.GetInt("driveLog"))
                {
                    c.SetInt("driveLog", bucket);
                    c.Log($"开了 {c.StepElapsed:F0} 秒：离核心 {Vector2.Distance(me, SignalUplinkService.CorePosition(s)):F0} 格，路点 {wp}/{route.Length}，位置 ({me.x:F0},{me.y:F0})，侧向绕行 {c.GetInt("sidesteps")} 次");
                }
            }
            return StepOutcome.Wait;
        }

        /// <summary>FGR-SIG-053 / FG01 第 5 章：目标机器不在与核心连通的覆盖网络里 → 拒绝，写明原因（信号的“路径失败”）；信号留在核心、镜头不动。</summary>
        private static StepOutcome TickOutsideRejected(JourneyContext c)
        {
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点机器列表失败：" + FgjM1Journey.UiFail(c));
            }
            int w = c.GetInt("workerA");
            string fb = SignalUplinkService.LastFeedbackText;
            bool rejected = SignalPresence.AtCore && !SignalUplinkService.IsPending && SignalUplinkService.LastFailure != UplinkFailure.None;
            return rejected
                ? StepOutcome.Done($"机器列表点覆盖外的 {Label(w)}：拒绝（{SignalUplinkService.LastFailure}）“{fb}”；信号留在归还核心")
                : StepOutcome.Fail($"覆盖外的机器没有被拒绝：信号在 {SignalPresence.CurrentMachineLogicId}、过渡中 {SignalUplinkService.IsPending}、最后原因 {SignalUplinkService.LastFailure}“{fb}”");
        }

        // ── 5. 存读档（安全模式中）──────────────────────────────────────────────────

        private static string SafeDigest(CampaignState s) =>
            string.Join(";", (s.SignalCore?.SafeModes ?? Array.Empty<SignalSafeModeRecord>())
                .Select(m => $"{m.LogicId}:{m.Reason}:{m.SinceTick.ToString(CultureInfo.InvariantCulture)}:{m.ClearSinceTick.ToString(CultureInfo.InvariantCulture)}"))
            + $"｜信号={s.SignalCore?.UplinkMachineLogicId}｜槽位={string.Join(",", s.SignalCore?.SlotPartIds ?? Array.Empty<string>())}";

        private static StepOutcome TickPauseMenu(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (!PauseMenuUIToolkit.IsOpen)
            {
                return StepOutcome.Retry("按 Esc 后暂停菜单没有打开");
            }
            c.Set("pre", SafeDigest(St));
            c.SetLong("preTicks", GameClock.Ticks);
            c.SetInt("slot", CampaignSession.ActiveSlotIndex);
            return StepOutcome.Done($"暂停菜单打开；存档前 {c.Get("pre")}");
        }

        private static StepOutcome TickSaveConfirmed(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            if (UiConfirmDialog.IsOpen)
            {
                if (c.GetInt("okClicked") == 0)
                {
                    c.SetInt("okClicked", 1);
                    if (!JourneyInput.ClickUitk("[UiKitOverlayHost]", "ConfirmOk"))
                    {
                        return StepOutcome.Fail("确认框点“确认”失败：" + JourneyInput.LastUiFailure);
                    }
                }
                return StepOutcome.Wait;
            }
            return c.GetInt("okClicked") == 1 ? StepOutcome.Done("确认保存，回主菜单") : c.StepElapsed > 5 ? StepOutcome.Fail("没有弹出二次确认") : StepOutcome.Wait;
        }

        private static StepOutcome TickMenuDisk(JourneyContext c)
        {
            if (JourneyInput.FindActiveButton("m_btn_Load") == null || c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            LoadResult onDisk = CampaignSaveService.Load(c.GetInt("slot"));
            if (!onDisk.Success)
            {
                return StepOutcome.Fail($"存档读不出来：{onDisk.Outcome}/{onDisk.Reason}");
            }
            string disk = SafeDigest(onDisk.State);
            return disk == c.Get("pre") && onDisk.State.Clock.Ticks == c.GetLong("preTicks")
                ? StepOutcome.Done($"存档里安全模式记录与存档那一刻逐字段一致（整数步）：{disk}")
                : StepOutcome.Fail($"存档与存档那一刻不一致：\n      存档前 {c.Get("pre")}\n      存档里 {disk}");
        }

        private static StepOutcome TickLoadClicked(JourneyContext c)
        {
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            if (c.GetInt("slotClicked2") == 1)
            {
                return StepOutcome.Done("点了存档槽的“读取”");
            }
            UnityEngine.UI.Button action = JourneyInput.FindActiveButton($"m_btn_Slot{c.GetInt("slot")}Action");
            if (action == null)
            {
                return c.StepElapsed > 8 ? StepOutcome.Fail("找不到存档槽的“读取”按钮") : StepOutcome.Wait;
            }
            if (!JourneyInput.ClickUgui(action))
            {
                return StepOutcome.Fail("点存档槽失败：" + JourneyInput.LastUiFailure);
            }
            c.SetInt("slotClicked2", 1);
            return StepOutcome.Wait;
        }

        private static StepOutcome TickLoadedSafe(JourneyContext c)
        {
            if (GameRoot.HomeValley == null || !GameRoot.HomeValley.IsActive || c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            int w = c.GetInt("workerA");
            string live = SafeDigest(St);
            string pre = c.Get("pre");
            return live == pre && SignalLinkService.IsInSafeMode(St, w) && SignalLinkView.BadgeWanted(w) && SignalPresence.AtCore
                ? StepOutcome.Done($"读档后 {Label(w)} 仍在安全模式（{live}），头顶图标恢复，信号在归还核心")
                : StepOutcome.Fail($"读档后安全模式不一致：\n      存档前 {pre}\n      读档后 {live}；图标 {SignalLinkView.BadgeWanted(w)}");
        }

        // ── 6. 恢复 ──────────────────────────────────────────────────────────────────

        private static StepOutcome TickCameraHome(JourneyContext c)
        {
            if (c.StepElapsed < 1.2)
            {
                return StepOutcome.Wait;
            }
            float2 f = WorldView.Director.StrategyFocus;
            float d = Vector2.Distance(new Vector2(f.x, f.y), HomeValleyLayout.Core.Position);
            return d < 2f ? StepOutcome.Done("镜头回到归还核心") : StepOutcome.Retry($"镜头焦点离核心 {d:F1} 格");
        }

        private static StepOutcome TickRecovered(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            int w = c.GetInt("workerA");
            BuildingRecord tower = FgjM1Journey.Building(HomeValleyLayout.BuildingTypeSignalTower);
            if (tower == null || tower.ConstructionState != BuildingConstructionState.Operational)
            {
                if (FgjM1Journey.RepairOrder(HomeValleyLayout.BuildingTypeSignalTower) == null && c.StepElapsed > 5)
                {
                    return StepOutcome.Fail($"信号塔还没修好，修复工单却没了（废料 {St.Scrap}）");
                }
                return StepOutcome.Wait;
            }
            if (c.GetInt("towerAt") == 0)
            {
                c.SetInt("towerAt", 1);
                c.Log($"信号塔修好（{c.StepElapsed:F0} 秒）");
            }
            if (SignalLinkService.IsInSafeMode(St, w))
            {
                return StepOutcome.Wait;
            }
            LivePos(w, out Vector2 at);
            return SignalCoverageService.Sample(HomeValleyLayout.RegionId, at, w).Covered && !SignalLinkView.BadgeWanted(w)
                ? StepOutcome.Done($"信号塔修好、覆盖扩大：{Label(w)}（没有挪动）回到覆盖里，条件消失 2 游戏秒后自动退出安全模式、头顶图标消失")
                : StepOutcome.Fail($"{Label(w)} 退出安全模式时却不在覆盖里（或图标没消失）");
        }

        private static StepOutcome TickRelinked(JourneyContext c)
        {
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait;
            }
            int w = c.GetInt("workerA");
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点机器列表失败：" + FgjM1Journey.UiFail(c));
            }
            return SignalPresence.CurrentMachineLogicId == w && WorldView.Director.Mode == ViewMode.Direct
                ? StepOutcome.Done($"机器列表点 {Label(w)}：再次接入成功（镜头直控）")
                : StepOutcome.Retry($"没有接入 {Label(w)}（{SignalUplinkService.LastFeedbackText}）");
        }

        // ── 7. 目标死亡 ────────────────────────────────────────────────────────────

        /// <summary>伤害夹具（DEBT-FG1E2E01-04）：对接入中的机器施加致死伤害，走生产代码的伤害入口 MachineRegistry.ApplyDamage（与战斗结算同一入口）。</summary>
        private static void KillUplinked(JourneyContext c)
        {
            int w = SignalPresence.CurrentMachineLogicId;
            c.SetInt("victim", w);
            c.SetInt("lost0", FeedbackCues.CountOf(FeedbackCueId.SignalLost));
            if (w != 0)
            {
                MachineRegistry.ApplyDamage(w, 999999f);
            }
        }

        private static StepOutcome TickRebound(JourneyContext c)
        {
            int v = c.GetInt("victim");
            if (v == 0)
            {
                return StepOutcome.Fail("击毁前信号不在机器里");
            }
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            bool dead = MachineRegistry.TryGetRecord(v, out MachineRecord r) && !r.IsAlive;
            int now = SignalPresence.CurrentMachineLogicId;
            bool reason = SignalLinkService.LastBreakReason == SignalLinkBreakReason.MachineDestroyed && FeedbackCues.CountOf(FeedbackCueId.SignalLost) > c.GetInt("lost0");
            bool noSafe = !SignalLinkService.IsInSafeMode(St, v);
            c.SetInt("reboundTo", now);
            return dead && reason && now != v && noSafe
                ? StepOutcome.Done($"{Label(v)} 被击毁：信号{(now == 0 ? "回到归还核心" : "按死亡回弹弹到 " + Label(now))}，原因与音效“{SignalUplinkService.LastFeedbackText}”；阵亡不产生安全模式")
                : StepOutcome.Fail($"阵亡回弹不对：阵亡 {dead}、原因 {SignalLinkService.LastBreakReason}、失联音 {reason}、信号在 {now}、安全模式 {!noSafe}");
        }

        private static void LeaveOrUplinkAfterDeath(JourneyContext c)
        {
            if (SignalPresence.CurrentMachineLogicId != 0)
            {
                JourneyInput.PressAction(GameActionId.ToggleCameraView); // 回弹到了别的机器：按退出键离开
                c.Set("after", "leave");
                return;
            }
            // 回到了核心：机器列表点另一台还活着的机器接入
            int other = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && !m.IsInFactory && m.RegionId == HomeValleyLayout.RegionId
                                                              && SignalUplinkService.Validate(St, m.LogicId, out _) == UplinkFailure.None)
                .Select(m => m.LogicId).OrderBy(x => x).FirstOrDefault();
            c.SetInt("afterTarget", other);
            c.Set("after", "uplink");
            if (other != 0)
            {
                FgjM1Journey.ClickMachineList(c, other);
            }
        }

        private static StepOutcome TickNotSoftlocked(JourneyContext c)
        {
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait;
            }
            if (c.Get("after") == "leave")
            {
                return SignalPresence.AtCore && WorldView.Director.Mode == ViewMode.Strategy
                    ? StepOutcome.Done($"回弹后的 {Label(c.GetInt("reboundTo"))} 照常能离开：信号回到归还核心、镜头回战略（没有软锁）")
                    : StepOutcome.Fail("回弹后按退出键离不开");
            }
            int t = c.GetInt("afterTarget");
            return t != 0 && SignalPresence.CurrentMachineLogicId == t
                ? StepOutcome.Done($"回到核心后照常能接入别的机器（{Label(t)}，没有软锁）")
                : StepOutcome.Fail($"回到核心后接入不了别的机器（{SignalUplinkService.LastFeedbackText}）");
        }

        // ── 收尾 ────────────────────────────────────────────────────────────────────

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(JourneyCommon.UiStats());
            JourneyCommon.Cleanup(c);
        }
    }
}
