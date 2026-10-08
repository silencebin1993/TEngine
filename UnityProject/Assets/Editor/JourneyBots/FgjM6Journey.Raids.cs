using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;
using D = GameLogic.EditorTools.JourneyBots.FgjM6Common;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FGJ-M6 突袭段：第一次（1 级）突袭到达、六座炮塔挡住（打开结算面板看结果）→ 按预警条上的抵达点布置火墙（离抵达点最近的炮塔在炮塔面板换上“燃迹 + 接入口”新版本、
    /// 旁边的漏油陷阱发射器朝抵达点铺油膜带、燃油管线经缺口进圈）→ 第二波排定后关广播、出发远征 → 第二波预警：远征小窗点“跳回家园”、接入离第二波抵达点最近、带接入口的炮塔亲自瞄准开火 →
    /// 跳回远征队 → 第三波预警：点“留在远征队”、家园自己守（第二、三波至少一波火墙打出爆燃）→
    /// 撤离回家、离家报告里看两波的时间线与两次选择 → 被摧毁的建筑已由自动重建规则重建。
    /// </summary>
    public static partial class FgjM6Journey
    {
        private static Vector2 EvacPoint => FracturedCityLayout.EntryEvac.Position;

        private static IEnumerable<JourneyStep> RaidSteps() => new[]
        {
            // ── 第一次（1 级）突袭：炮塔挡住 ──
            S("w1_warn", "等第一次突袭的预警（开局宽限到第 5 个游戏日）：左上角预警条一行（倒计时、来自哪个方向），通知“突袭预警”", 2600, null, TickFirstWarned),
            S("exp_on1", "左键点信号塔，远征准备面板取消“关闭信号塔主动广播”（暴露重新上涨，引来下一波），点“关闭”", 60, null, c => D.TickBroadcast(c, false), retries: 2),
            S("w1_arrive", "第一次突袭部队到达家园、按编成展开攻城（到达时自动暂停就按暂停键继续）", 400, null, c => TickArrived(c, "plan1")),
            S("w1_end", "炮塔按目标模式开火：第一次突袭被打退（全歼或撤退），归还核心没事；点“突袭结算”通知弹出条：突袭历史面板显示这一份结算", 400, null, c => TickRaidEnded(c, "plan1", "res1", true)),
            S("w1_wreck", "突袭历史面板顶部“残骸去向”下拉选“送回收站”（废料是这时最紧的：回收站拆的废墟有限，敌方残骸送进回收站出废料）", 15, PickWreckRecycler, TickWreckRecycler, retries: 1),
            S("w1_close", "Esc 关闭突袭历史面板", 10, c => JourneyInput.PressAction(GameActionId.Cancel), TickResultClosed, retries: 1),

            // ── 火墙：漏油带 + 燃迹炮塔 ──
            S("fw_plan", "按第一次突袭预警条上的抵达点（敌人在那里展开攻城）规划火墙：离抵达点最近的那座炮塔改装成燃迹炮塔，它旁边放陷阱发射器、朝抵达点铺油膜带（越过屏障铺到圈外），燃油管线从储罐经屏障圈的缺口接过去", 30, null, TickFirewallPlan),
            S("build_open5", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
            S("m6_place5", "放燃油管线（按住左键分段拖，经缺口进圈）、陷阱发射器（旋转键转向抵达点）", 200, null,
                c => P.TickActions(c, "m6f", 40), retries: 2),
            S("build_close5", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("fw_built", "机器取料施工：燃油管线、陷阱发射器建成接电", 900, null, TickFirewallBuilt),
            S("fwt_open", "建造模式里左键点离抵达点最近的那座炮塔，通用面板上点“炮塔…”：炮塔面板打开", 30, null,
                c => M.TickSubPanel(c, "fwt", "PrTurret", () => TurretPanelUIToolkit.IsOpen, "炮塔面板打开"), retries: 2),
            S("fwt_bp", "炮塔面板“蓝图”下拉：已建的炮塔还钉在建造时的旧版本，选同一张蓝图的新版本那一行（燃迹 + 接入口）：立即换上燃迹、从管线进燃油", 20, null, TickTurretUpgraded, retries: 1),
            S("fwt_close", "Esc 关掉炮塔面板与通用面板", 15, null, c => M.TickCloseAll(c, () => !TurretPanelUIToolkit.IsOpen, "炮塔面板"), retries: 1),
            S("trap_open", "建造模式里左键点陷阱发射器，通用面板上点“陷阱…”：防御面板打开", 30, null,
                c => M.TickSubPanel(c, "trap", "PrDefense", () => DefensePanelUIToolkit.IsOpen, "防御面板（陷阱发射器）打开"), retries: 2),
            S("trap_fw", "固件下拉选“漏油”，铺设方式点“一条线”：发射器开始沿朝向铺油膜带", 20, null, TickTrapConfigured, retries: 1),
            S("trap_close", "Esc 关掉防御面板与通用面板", 15, null, c => M.TickCloseAll(c, () => !DefensePanelUIToolkit.IsOpen, "防御面板"), retries: 1),
            S("fw_build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("fw_lay", "火墙在工作：陷阱发射器一轮轮铺油膜带（消耗燃油），燃迹炮塔补给装满", 120, null, TickFirewallWorking),

            // ── 第二波排定后关广播，出发远征 ──
            S("w2_plan", "暴露回到 30 以上：突袭导演排定第二波（普通 1 级，按最短间隔排在第一波之后至少 1 个游戏日）", 900, null, TickSecondPlanned),
            S("exp_off2", "左键点信号塔，勾上“关闭信号塔主动广播”（不让第二波在出发前升级），点“关闭”", 60, null, c => D.TickBroadcast(c, true), retries: 2),
            S("fw_ready", "火墙就绪、第二波已排定（之后出发远征）", 10, null, TickFirewallReady),
            S("prep_open", "左键点信号塔打开远征准备面板", 15, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeSignalTower, !GameRoot.HomeValley.IsExpeditionPrepPanelOpen),
                FgjM1Journey.TickPrepOpen, retries: 2),
            S("prep_pick", "勾选出征名单（两台新机 + 一台满足人数）", 20, c => { c.SetInt("wetM", c.GetInt("m1")); c.SetInt("shockM", c.GetInt("m2")); }, FgjM2Common.TickRosterPicked),
            S("prep_depart", "点“出发”（有在办工作时确认中断）", 30, c => FgjM1Journey.ClickUi(c, D.PrepHost, "DepartButton"), FgjM1Journey.TickDeparted, retries: 1),
            S("exp_up", "机器列表点第一台新机：接入（信号跟着远征队）", 25, c => { c.SetInt("other", c.GetInt("m1")); FgjM1Journey.ClickMachineList(c, c.GetInt("m1")); }, TickUplinkedFc, retries: 3),

            // ── 第二波：跳回家园接入炮塔防守 ──
            S("w2_alert", "远征途中家园遇袭：远征 HUD 左上弹出紧急通知（倒计时、家园状态小窗：核心耐久、关键建筑、来袭敌人）", 900, null, c => TickAwayAlert(c, "plan2")),
            S("w2_jump", "点“跳回家园（H）”：信号回到归还核心、镜头飞回家园（远征队交给 AI）", 30, c => FgjM1Journey.ClickUi(c, D.RaidHost, "RaidAwayJump"), TickJumpedHome, retries: 1),
            S("exp_on2", "左键点信号塔，取消“关闭信号塔主动广播”（引来第三波），点“关闭”", 60, null, c => D.TickBroadcast(c, false), retries: 2),
            S("w2_turret", "看预警条上第二波的来袭方向，建造模式里左键点离它最近、带接入口的那座炮塔，通用面板上点“炮塔…”：炮塔面板打开", 30, ChooseUplinkTurret,
                c => M.TickSubPanel(c, "upt", "PrTurret", () => TurretPanelUIToolkit.IsOpen, "炮塔面板打开"), retries: 2),
            S("w2_uplink", "炮塔面板点“接入”：信号进入这座炮塔（突袭条换成炮塔读数：热量、耐久、补给）", 15, c => FgjM1Journey.ClickUi(c, D.TurretHost, "TurretUplink"), TickTurretUplinked, retries: 1),
            S("w2_unpanel", "Esc 关掉炮塔面板与通用面板，按建造键关掉建造模式（左键交给炮塔开火）", 20, null, TickUplinkCleared, retries: 1),
            S("w2_fire", "第二波到达：左键点进攻的敌人，接入的炮塔亲自瞄准开火（左键 = 亲自开火）；直到这一波被打退", 500, null, TickManualFire),
            S("w2_leave", "按接入键离开炮塔：信号回到归还核心", 10, c => JourneyInput.PressAction(GameActionId.ToggleCameraView), TickTurretLeft, retries: 1),
            S("w3_plan", "留在家园等第三波排定（按 3 倍速键；暴露回到 30 以上，导演按最短间隔排在第二波之后至少 1 个游戏日）", 900,
                c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), TickThirdPlanned),
            S("exp_off3", "左键点信号塔，勾上“关闭信号塔主动广播”（第三波已排定；不让暴露在它预警前涨过 60、并成 2 级），点“关闭”", 60, null, c => D.TickBroadcast(c, true), retries: 2),
            S("w2_back", "按跳回上一台机器键（默认 J）：跨地点远距离跳转回远征队", 30, c => JourneyInput.PressAction(GameActionId.JumpPreviousMachine), TickJumpedBack, retries: 2),

            // ── 第三波：留在远征队，家园自己守 ──
            S("w3_alert", "第三波预警：远征 HUD 又弹出紧急通知", 1200, null, c => TickAwayAlert(c, "plan3")),
            S("w3_stay", "点“留在远征队（Esc）”：通知收起、小窗留着写“你留在了远征队”，信号与镜头不动", 15, c => FgjM1Journey.ClickUi(c, D.RaidHost, "RaidAwayStay"), TickStayed, retries: 1),
            S("w3_end", "家园自己守：第三波到达、攻城、结束（镜头一直在远征地点）；炮塔 / 火墙 / 护盾 / 屏障自动守住归还核心", 900, null, c => TickRaidEnded(c, "plan3", "res3", false)),

            // ── 回家：离家报告里的突袭时间线、被摧毁的建筑已自动重建 ──
            S("drive_evac", "开着接入的机器（WASD）回撤离点", 120, null, TickDriveToEvac),
            S("evac_hold", "在撤离点按住交互键（默认 E）打开撤离清单", 40, c => JourneyInput.HoldAction(GameActionId.Interact, 1.6), FgjM1Journey.TickEvacPanel, retries: 3),
            S("evac_confirm", "点“确认撤离”：远征队返回家园（离家报告按设置自动打开，点关闭）", 30, c => FgjM1Journey.ClickUi(c, "[FracturedCityExpeditionReturnHost]", "ConfirmButton"),
                FgjM1Journey.TickReturnedHome, retries: 1),
            S("away_open", "按离家报告键（默认 Alt+H）打开离家报告：“突袭”一段有两波的时间线（预警 / 到达 / 展开 / 结束）与两次选择（跳回家园 / 留在远征队）", 30, null, TickAwayReport, retries: 1),
            S("away_close", "点“关闭”关闭离家报告", 10, c => FgjM1Journey.ClickUi(c, D.AwayHost, "AwayReportClose"), TickAwayClosed, retries: 1),
            // ── 废料不够重建：拆掉一座用不着的实验室换废料（研究都做完了；拆除全额返还投入）──
            S("scr_open", "废料不够重建时：按建造键打开建造模式（够就跳过）", 10, c => { if (NeedScrap(c)) { FgjM3Common.PressBuild(true); } },
                c => !NeedScrap(c) ? StepOutcome.Done($"废料 {St.Scrap} 够重建，不用拆") : FgjM3Common.TickBuild(c, true), retries: 1),
            S("scr_demo", "按拆除模式键（默认 X）", 10, c => { if (c.GetInt("scrDemo") == 1) { JourneyInput.PressToggleTo(GameActionId.DemolishMode, () => FgjM3Common.Mode.DemolishMode, true); } },
                c => c.GetInt("scrDemo") == 0 ? StepOutcome.Done("跳过") : c.StepElapsed < 0.4 ? StepOutcome.Wait : FgjM3Common.Mode.DemolishMode ? StepOutcome.Done("进入拆除模式") : StepOutcome.Retry("没进拆除模式"), retries: 1),
            S("scr_ask", "左键点第二座仿真实验室：标记拆除（有后果时先弹确认框写明）", 20, null, TickLabDemolishAsk, retries: 2),
            S("scr_ok", "弹了确认框就点“确认”：实验室标记拆除，机器上门拆、全额返还废料", 10, c => { if (c.GetInt("scrDemo") == 1 && UiConfirmDialog.IsOpen) { FgjM1Journey.ClickUi(c, "[UiKitOverlayHost]", "ConfirmOk"); } }, TickLabMarked, retries: 1),
            S("scr_demo_off", "再按拆除模式键退出、按建造键关闭建造模式", 10, c => { if (c.GetInt("scrDemo") == 1) { JourneyInput.PressToggleTo(GameActionId.DemolishMode, () => FgjM3Common.Mode.DemolishMode, false); } },
                c => c.GetInt("scrDemo") == 0 || !FgjM3Common.Mode.DemolishMode ? StepOutcome.Done("退出拆除模式") : c.StepElapsed < 0.4 ? StepOutcome.Wait : StepOutcome.Retry("没退出"), retries: 1),
            S("scr_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
            S("rebuilt", "被摧毁的建筑已经自动重建：两波里被摧毁的每一座都由自动重建规则派单、机器取料按原样重建好（规则日志可追溯）", 900, null, TickRebuilt),
        };

        // ── 第一次突袭 ────────────────────────────────────────────────────────────────

        private static StepOutcome TickFirstWarned(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            RaidPlanRecord p = RaidDirectorService.FindPlan(s, c.Get("plan1"));
            int tick = (int)(c.StepElapsed / 60);
            if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), tick);
                c.Log($"第 {GameClock.DayOf(GameClock.GameSeconds)} 日（第 {GameClock.Ticks} 步）：{D.PlanLine(p)}；暴露 {s.SignalExposure:F1}；废料 {s.Scrap}");
            }
            if (p == null)
            {
                return StepOutcome.Fail("第一次突袭的计划不见了");
            }
            if (p.State < RaidDirectorService.StateWarned)
            {
                return StepOutcome.Wait;
            }
            RaidWarningHudUIToolkit hud = RaidWarningHudUIToolkit.Instance;
            hud?.Refresh(force: true);
            string dir = RaidDirectorService.DirectionText(p);
            bool row = hud != null && hud.PanelVisible && hud.RowCount >= 1 && hud.RowText(0).Contains(dir);
            bool note = NotificationCenter.History.Any(e => e.Type?.Id == "raid_warning");
            long lead = p.ArrivalTick - p.WarnTick;
            if (!row || !note)
            {
                return c.StepElapsed < 60 ? StepOutcome.Wait : StepOutcome.Fail($"第一次突袭发了预警却没看到预警条 / 通知（预警条 {hud?.PanelVisible} / {hud?.RowCount} 行“{hud?.RowText(0)}”，通知 {note}）");
            }
            Vector2 at = hud.RowTarget(0);
            Vector2 cc = D.CoreCenter(s);
            c.Set("ap.x", (at.x - cc.x).ToString("R", CultureInfo.InvariantCulture));
            c.Set("ap.y", (at.y - cc.y).ToString("R", CultureInfo.InvariantCulture));
            bool ok = lead >= RaidDirectorService.MinWarningTicks(s) && p.FirstRaid && p.Level == 1;
            return ok
                ? StepOutcome.Done($"第 {GameClock.DayOf(GameClock.GameSeconds)} 日：第一次突袭预警——预警条“{hud.RowText(0).Replace("\n", " / ")}”（预计抵达点 {at}，来自{dir}）；" +
                                   $"预警到抵达 {RaidDirectorService.Duration(lead)}（≥ 最短预警 {RaidDirectorService.Duration(RaidDirectorService.MinWarningTicks(s))}）；{D.PlanLine(p)}")
                : StepOutcome.Fail($"第一次突袭预警不对：预警到抵达 {lead} 步（最短 {RaidDirectorService.MinWarningTicks(s)}）；{D.PlanLine(p)}");
        }

        private static StepOutcome TickArrived(JourneyContext c, string planKey)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            RaidResultRecord r = RaidResultService.FindByPlan(s, c.Get(planKey));
            if (r == null || r.UnfoldTick < 0)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"突袭部队到达、展开攻城 {r.Unfolded} 台（{RaidResultService.WaveLabel(r)}）；突袭条“{RaidWarningHudUIToolkit.Instance?.SpecStatusText}”；自动暂停 {c.GetInt("autoPauses")} 次后按暂停键继续");
        }

        /// <summary>
        /// 等 <paramref name="planKey"/> 那一波的结算结束：结果不是“核心被摧毁”、有击毁；第一波另外点“突袭结算”通知弹出条打开突袭历史面板核对（<paramref name="openPanel"/>）。
        /// 第三波（家园自己守）同时核对镜头一直在远征地点、被打死的家园机器黑匣子已回收。
        /// </summary>
        private static StepOutcome TickRaidEnded(JourneyContext c, string planKey, string resKey, bool openPanel)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            if (!openPanel && WorldView.ObservedSiteId == HomeValleyLayout.RegionId)
            {
                c.SetInt(FgjM3Common.SK(c, "homeFrames"), c.GetInt(FgjM3Common.SK(c, "homeFrames")) + 1);
            }
            RaidResultRecord r = RaidResultService.FindByPlan(s, c.Get(planKey));
            BuildingRecord core = D.Core(s);
            float coreHp = core != null ? BuildingOps.Durability(core) : 0f;
            float min = c.Get(FgjM3Common.SK(c, "coreMin")) is string m && float.TryParse(m, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? Mathf.Min(v, coreHp) : coreHp;
            c.Set(FgjM3Common.SK(c, "coreMin"), min.ToString("R", CultureInfo.InvariantCulture));
            if (RaidResultService.IsCoreLost(s))
            {
                return StepOutcome.Fail("归还核心被突袭摧毁（战役失败）：" + D.ResultLine(r));
            }
            if (r == null || r.EndTick < 0)
            {
                int tick = (int)(c.StepElapsed / 30);
                if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), tick);
                    c.Log($"攻城中：{D.ResultLine(r)}；核心耐久 {coreHp:F0}；镜头在 {WorldView.ObservedSiteId}");
                }
                return StepOutcome.Wait;
            }
            c.SetInt(resKey, r.Serial);
            string summary = $"{D.ResultLine(r)}；核心耐久最低 {min:F0}";
            if (r.Killed < 1 || r.Contrib.Length == 0)
            {
                return StepOutcome.Fail("突袭结束了但一台都没击毁：" + summary);
            }
            if (!openPanel)
            {
                // 家园自己守：被打死的家园机器黑匣子直接回收进陈列馆队列（DEBT-FG5RND06-03 的突袭那一段）。
                var boxes = new List<string>();
                foreach (RaidLossRecord loss in r.Losses.Where(x => x.Kind == "machine"))
                {
                    int id = int.TryParse(loss.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lid) ? lid : 0;
                    boxes.Add($"{loss.Name}：黑匣子{(BlackBoxService.Find(s, id) != null ? "已回收" : "没回收")}");
                }
                bool boxesOk = boxes.All(x => x.EndsWith("已回收", StringComparison.Ordinal));
                int homeFrames = c.GetInt(FgjM3Common.SK(c, "homeFrames"));
                if (!boxesOk || homeFrames > 0)
                {
                    return StepOutcome.Fail($"家园自己守的这一波：{summary}；黑匣子 [{string.Join("；", boxes)}]；镜头看了家园 {homeFrames} 帧（这一段应一直在远征地点）");
                }
                // 复审 P1（火墙始终没接敌）：第二、第三波至少有一波的结算里火墙打出了爆燃（油膜带 + 燃迹 = 阵地反应，FGT-DEF-002；里程碑“炮塔阵地自动打出反应”）。
                string react = DeflagrationLine(s, new[] { c.Get("plan2"), c.Get("plan3") }, out int deflag);
                if (deflag <= 0)
                {
                    return StepOutcome.Fail($"第二、三波火墙都没打出爆燃（阵地反应）：{summary}；{react}");
                }
                return StepOutcome.Done($"家园自己守住了（镜头一直在远征地点）：{summary}；{(boxes.Count > 0 ? "被打死的家园机器 " + string.Join("；", boxes) : "没有家园机器阵亡")}；{react}");
            }
            // 第一波：点“突袭结算”通知弹出条打开突袭历史面板（与通知中心同一个去处）。
            if (!FgjM3Common.Done(c, "toast"))
            {
                bool? clicked = D.ClickToast("raid_result", out NotificationEntry e);
                if (clicked == null)
                {
                    return c.StepElapsed < 300 ? StepOutcome.Wait : StepOutcome.Fail("结算之后没有“突袭结算”通知弹出条：" + summary);
                }
                if (clicked == false)
                {
                    return M.UiRetry("点不到“突袭结算”通知弹出条：");
                }
                c.Set(FgjM3Common.SK(c, "text"), e.Text);
                FgjM3Common.Mark(c, "toast");
                return StepOutcome.Wait;
            }
            RaidResultPanelUIToolkit p = RaidResultPanelUIToolkit.Instance;
            if (!RaidResultPanelUIToolkit.IsOpen || p == null)
            {
                return FgjM3Common.SinceMs(c, "toast") < 3000 ? StepOutcome.Wait : StepOutcome.Retry("点了“突袭结算”通知，突袭历史面板没打开");
            }
            p.Refresh();
            string rows = string.Join(" | ", Enumerable.Range(0, Math.Min(p.VisibleRowCount, 30)).Select(i => p.RowText(i).Replace("\n", " ")));
            bool shown = p.ShownKey == "R" + r.Serial && p.SummaryText.Contains(RaidResultService.OutcomeText(r)) && rows.Contains(GameText.Get("raid.result.sec.timeline"))
                         && !GameText.ContainsMarker(p.SummaryText + rows);
            int turretKills = r.Contrib.Where(x => x.Kind == "turret").Sum(x => x.Kills);
            return shown && turretKills > 0
                ? StepOutcome.Done($"第一次突袭被炮塔打退：{summary}（炮塔击毁 {turretKills} 台）；点通知“{c.Get(FgjM3Common.SK(c, "text"))}”：突袭历史面板“{p.SummaryText.Replace("\n", " ")}”，{p.VisibleRowCount} 行")
                : StepOutcome.Fail($"第一次突袭结算 / 面板不对：{summary}；面板显示 {p.ShownKey}、“{p.SummaryText}”；[{rows}]；炮塔击毁 {turretKills}");
        }

        private static StepOutcome TickResultClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            return !RaidResultPanelUIToolkit.IsOpen && !PauseMenuUIToolkit.IsOpen ? StepOutcome.Done("Esc 关闭突袭历史面板（暂停菜单没开）") : StepOutcome.Retry("突袭历史面板还开着（或弹出了暂停菜单）");
        }

        /// <summary>火墙打出的阵地反应：<paramref name="plans"/> 这几波结算里“爆燃”（油 + 火，reaction_deflagrate）的次数。</summary>
        private static string DeflagrationLine(CampaignState s, IEnumerable<string> plans, out int n)
        {
            n = 0;
            var lines = new List<string>();
            var want = new HashSet<string>(plans.Where(x => !string.IsNullOrEmpty(x)));
            foreach (RaidResultRecord r in RaidResultService.All(s).Where(x => x != null && want.Contains(x.PlanId)))
            {
                ReactionShareRecord d = r?.Reactions?.FirstOrDefault(x => x != null && x.ReactionId != null && x.ReactionId.Contains("deflagr"));
                if (d != null)
                {
                    n += d.Count;
                    lines.Add($"{r.PlanId} 爆燃 {d.Count} 次");
                }
            }
            return n > 0 ? "火墙打出阵地反应：" + string.Join("、", lines) : "火墙没打出爆燃";
        }

        // ── 火墙 ──────────────────────────────────────────────────────────────────────

        private static void PickWreckRecycler(JourneyContext c)
        {
            DropdownField dd = JourneyInput.FindUitk<DropdownField>(D.ResultHost, "RaidResultWreck");
            string want = RaidResultService.RouteText(RaidResultService.RouteRecycler);
            string choice = dd?.choices?.FirstOrDefault(x => x == want);
            c.Set("wreckChoice", choice ?? string.Empty);
            c.Set("wreckChoices", dd?.choices == null ? string.Empty : string.Join("／", dd.choices));
            if (dd != null && choice != null && JourneyInput.IsClickable(dd))
            {
                dd.value = choice; // 与玩家在弹出菜单里点那一项同一个 ChangeEvent 回调（选中即生效）
            }
        }

        private static StepOutcome TickWreckRecycler(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            RaidResultState st = RaidResultService.StateOf(St);
            if (st == null || st.WreckRouting != RaidResultService.RouteRecycler)
            {
                return StepOutcome.Retry($"残骸去向没改成送回收站（选项 [{c.Get("wreckChoices")}]，现在 {st?.WreckRouting}）");
            }
            return StepOutcome.Done($"残骸去向：{RaidResultService.RouteText(st.WreckRouting)}（仓库里的敌方残骸由机器送进回收站出废料）；废料 {St.Scrap}");
        }

        private static StepOutcome TickFirewallPlan(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            Vector2 cc = D.CoreCenter(s);
            var rel = new Vector2(float.Parse(c.Get("ap.x", "1"), CultureInfo.InvariantCulture), float.Parse(c.Get("ap.y", "0"), CultureInfo.InvariantCulture));
            Vector2 arrival = cc + rel;
            var others = new List<GridCell> { P.P(c, "ref.acid"), P.P(c, "ref.pump") };
            others.AddRange(P.LoadPath(c, "path.crude"));
            var turrets = D.TurretKeys.Select(k => (k, P.P(c, k))).ToList();
            // FG6-E2E-01 修复轮（复审 P1“火墙始终没接敌”）：火墙按真实来路放。第一轮按“管线最短的一侧”放在储罐那边（东），三波都从西边来、一次都没接敌；
            // 现在挑离抵达点最近的那座已建炮塔改装（不另造炮塔，废料只花在陷阱发射器与管线上），陷阱发射器贴着它朝抵达点铺油膜带。
            D.FirewallPlan fw = D.PlanFirewallNear(s, arrival, turrets, P.P(c, "ref.tank"), P.P(c, "ref.fuel"), others, out string why);
            if (fw == null)
            {
                return StepOutcome.Fail($"火墙规划不出来（抵达点 {arrival}）：{why}");
            }
            var trap = new P.Placed { Key = "trap", Type = D.Trap, Pivot = fw.TrapAt, Rot = fw.TrapRot, Label = D.Name(D.Trap) };
            var fwt = new P.Placed { Key = "fwt", Type = D.Turret, Pivot = fw.TurretAt, Rot = 0, Label = $"离抵达点最近的炮塔（{fw.TurretKey}）" };
            P.Remember(c, trap);
            P.Remember(c, fwt);
            c.Set("fwt.key", fw.TurretKey);
            P.SavePath(c, "path.fuel", fw.Pipe);
            var acts = new List<string>();
            int under = 0;
            if (fw.Underground)
            {
                // 缺口被传送带占着：圈外一段管线 → 一对地下管线口从屏障底下穿进圈（圈外口朝里、圈里口朝外，旋转键转向）→ 圈里一段管线。
                P.SavePath(c, "path.fuel1", fw.PipeOut);
                P.SavePath(c, "path.fuel2", fw.PipeIn);
                acts.Add(P.ActQ(P.PipeT1, "path.fuel1", $"燃油管线圈外一段（{fw.PipeOut.Count} 格）：储罐 → 屏障外侧"));
                acts.Add(P.ActT(D.UndergroundPipeTool, fw.UnderOut, fw.UnderDir, $"地下管线口（圈外 {D.Cell(fw.UnderOut)}，朝{FgjM3Common.DirName(fw.UnderDir)}）"));
                acts.Add(P.ActT(D.UndergroundPipeTool, fw.UnderIn, (fw.UnderDir + 2) & 3, $"地下管线口（圈里 {D.Cell(fw.UnderIn)}，朝{FgjM3Common.DirName((fw.UnderDir + 2) & 3)}）"));
                acts.Add(P.ActQ(P.PipeT1, "path.fuel2", $"燃油管线圈里一段（{fw.PipeIn.Count} 格）→ 炮塔与陷阱发射器之间"));
                under = 2;
            }
            else
            {
                acts.Add(P.ActQ(P.PipeT1, "path.fuel", $"燃油管线（{fw.Pipe.Count} 格）：储罐 → 炮塔与陷阱发射器之间"));
            }
            acts.Add(P.ActB(trap));
            P.SetActions(c, "m6f", acts);
            int scrapNeed = (fw.Pipe.Count - under) * PlanEntries.CostOf(P.PipeT1) + under * PlanEntries.CostOf(D.UndergroundPipeTool) + PlanEntries.CostOf(D.Trap);
            return StepOutcome.Done($"火墙（抵达点 {arrival}，相对核心 {rel}）：改装 {fw.TurretKey} {D.Cell(fw.TurretAt)}（离抵达点 {Vector2.Distance(new Vector2(fw.TurretAt.X + 0.5f, fw.TurretAt.Y + 0.5f), arrival):F1} 米）；" +
                                    $"陷阱发射器 {D.Cell(fw.TrapAt)} 朝{FgjM3Common.DirName(fw.TrapRot / 90)}，油膜带离抵达点 {fw.Cover:F1} 米；燃油管线 {fw.Pipe.Count - under} 格到 {D.Cell(fw.PipeEnd)}" +
                                    (fw.Underground ? $"（{D.Cell(fw.UnderOut)} → {D.Cell(fw.UnderIn)} 一对地下管线口从屏障底下穿进圈）" : string.Empty) + $"；要废料约 {scrapNeed}（现有 {s.Scrap}）");
        }

        private static StepOutcome TickFirewallBuilt(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            bool trap = P.BuildingReady(s, D.Trap, P.P(c, "trap"), out BuildingRecord tb);
            bool tur = P.BuildingReady(s, D.Turret, P.P(c, "fwt"), out BuildingRecord ub);
            List<GridCell> pipe = P.LoadPath(c, "path.fuel");
            bool pipes = P.RouteBuilt(s, pipe, false);
            if (!trap || !tur || !pipes)
            {
                int tick = (int)(c.StepElapsed / 60);
                if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), tick);
                    int built = pipe.Count(x => P.RouteBuilt(s, new List<GridCell> { x }, false));
                    c.Log($"火墙施工：陷阱发射器 {trap}、要改装的炮塔在 {tur}、燃油管线 {built}/{pipe.Count} 格；{D.WorkDiag(s)}");
                }
                return StepOutcome.Wait;
            }
            if (tb.PowerState != BuildingPowerState.Powered || ub.PowerState != BuildingPowerState.Powered)
            {
                return c.StepElapsed < 60 ? StepOutcome.Wait : StepOutcome.Fail($"火墙建成了但没电：陷阱 {tb.PowerState}、炮塔 {ub.PowerState}；{P.PowerLine(s)}");
            }
            TurretRecord r = TurretService.Find(s, ub.BuildingId);
            c.Set("trap.id", tb.BuildingId);
            c.Set("fwt.id", ub.BuildingId);
            return StepOutcome.Done($"火墙的陷阱发射器 {tb.BuildingId}（朝{FgjM3Common.DirName((int)GridMath.FacingOf(GridMath.NormalizeRotation(tb.Rotation)))}）与燃油管线 {pipe.Count} 格建成接电；" +
                                   $"要改装的炮塔 {ub.BuildingId} 还装着 v{r?.BlueprintVersion}（建造时的版本；蓝图现役 v{c.GetInt("bpBurn")}）");
        }

        /// <summary>炮塔面板“蓝图”下拉选同一张蓝图的新版本那一行（FG-GAP-115：已建炮塔换到新版本的入口）——与玩家在弹出菜单里点那一项同一个 ChangeEvent 回调。</summary>
        private static StepOutcome TickTurretUpgraded(JourneyContext c)
        {
            CampaignState s = St;
            TurretPanelUIToolkit p = TurretPanelUIToolkit.Instance;
            if (p == null || !TurretPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Fail("炮塔面板没开");
            }
            TurretRecord r = TurretService.Find(s, c.Get("fwt.id"));
            int want = c.GetInt("bpBurn");
            if (r != null && r.BlueprintVersion == want)
            {
                if (!FgjM3Common.Done(c, "pick") || FgjM3Common.SinceMs(c, "pick") < 800)
                {
                    return FgjM3Common.Done(c, "pick") ? StepOutcome.Wait : StepOutcome.Fail($"还没选就已经是 v{want}（这座炮塔建造时就装了新版本？）");
                }
                string supply = TurretService.SupplyLine(s, c.Get("fwt.id")) ?? string.Empty;
                return StepOutcome.Done($"蓝图下拉 [{string.Join(" / ", p.BlueprintChoices)}] 选“{p.BlueprintField?.value}”：这座炮塔换上 v{r.BlueprintVersion}（燃迹 + 接入口）；“{p.MessageText}”；补给“{supply.Replace("\n", " ")}”");
            }
            if (FgjM3Common.Done(c, "pick"))
            {
                return FgjM3Common.SinceMs(c, "pick") < 2500 ? StepOutcome.Wait : StepOutcome.Retry($"选了新版本那一行后炮塔仍是 v{r?.BlueprintVersion}（“{p.MessageText}”）");
            }
            DropdownField d = p.BlueprintField;
            string row = GameText.Format("turret.panel.blueprint_row", TurretService.BlueprintName(BlueprintEditorService.Find(s, r?.BlueprintId)), want.ToString(CultureInfo.InvariantCulture));
            string tag = GameText.Format("turret.panel.blueprint_row", string.Empty, want.ToString(CultureInfo.InvariantCulture));
            string choice = d?.choices?.FirstOrDefault(x => x == row) ?? d?.choices?.LastOrDefault(x => x.EndsWith(tag, StringComparison.Ordinal) && x != d.value);
            if (d == null || choice == null)
            {
                return StepOutcome.Fail($"蓝图下拉里没有“{row}”这一行（[{string.Join(" / ", p.BlueprintChoices)}]，炮塔现在 v{r?.BlueprintVersion}）");
            }
            if (!JourneyInput.IsClickable(d))
            {
                return M.UiRetry("蓝图下拉点不到：");
            }
            d.value = choice; // 与玩家在弹出菜单里点那一项同一个 ChangeEvent 回调（选中即生效）
            FgjM3Common.Mark(c, "pick");
            return StepOutcome.Wait;
        }
        private static StepOutcome TickTrapConfigured(JourneyContext c)
        {
            CampaignState s = St;
            DefensePanelUIToolkit p = DefensePanelUIToolkit.Instance;
            if (p == null || !DefensePanelUIToolkit.IsOpen || !p.TrapSectionVisible)
            {
                return StepOutcome.Fail("防御面板没开或没有陷阱一段");
            }
            p.Refresh(force: true);
            DefenseRecord r = DefenseService.Find(s, c.Get("trap.id"));
            if (r != null && r.TrapFirmware == D.FwOil && FgjM3Common.Done(c, "line"))
            {
                if (FgjM3Common.SinceMs(c, "line") < 800)
                {
                    return StepOutcome.Wait;
                }
                return r.TrapPattern == 0
                    ? StepOutcome.Done($"固件下拉选“{M.FwName(D.FwOil)}”、点“一条线”：陷阱发射器铺油膜带（补给“{p.TrapSupplyText.Replace("\n", " ")}”、“{p.TrapStatsText}”）")
                    : StepOutcome.Retry($"点了“一条线”铺设方式仍是 {r.TrapPattern}");
            }
            if (r == null || r.TrapFirmware != D.FwOil)
            {
                int idx = -1;
                for (int i = 0; i < p.FirmwareIds.Count; i++)
                {
                    if (p.FirmwareIds[i] == D.FwOil)
                    {
                        idx = i;
                    }
                }
                DropdownField d = p.FirmwareField;
                if (idx < 0 || d == null || idx >= d.choices.Count)
                {
                    return StepOutcome.Fail($"陷阱固件下拉里没有“{M.FwName(D.FwOil)}”（{string.Join("、", p.FirmwareChoices)}）");
                }
                if (FgjM3Common.Done(c, "pick"))
                {
                    return c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Retry($"选了漏油后陷阱固件仍是“{r?.TrapFirmware}”（“{p.MessageText}”）");
                }
                if (!JourneyInput.IsClickable(d))
                {
                    return M.UiRetry("陷阱固件下拉点不到：");
                }
                // 下拉选择：设值 = 玩家在弹出菜单里点那一项（同一个 ChangeEvent 回调）。
                d.value = d.choices[idx];
                FgjM3Common.Mark(c, "pick");
                return StepOutcome.Wait;
            }
            if (!FgjM3Common.Once(c, "line", () => JourneyInput.ClickElement(p.PatternLineButton)))
            {
                return M.UiRetry("点不到“一条线”：");
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickFirewallWorking(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            DefenseRecord r = DefenseService.Find(s, c.Get("trap.id"));
            TurretRecord t = TurretService.Find(s, c.Get("fwt.id"));
            if (c.GetInt(FgjM3Common.SK(c, "l0set")) == 0)
            {
                c.SetInt(FgjM3Common.SK(c, "l0set"), 1);
                c.SetLong(FgjM3Common.SK(c, "l0"), r?.TrapLays ?? 0);
            }
            long lays = (r?.TrapLays ?? 0) - c.GetLong(FgjM3Common.SK(c, "l0"));
            string supply = TurretService.SupplyLine(s, c.Get("fwt.id")) ?? string.Empty;
            bool fed = t != null && t.ConsumerIds.Length > 0;
            if (lays >= 3 && fed)
            {
                return StepOutcome.Done($"陷阱发射器又铺了 {lays} 轮油膜带（累计 {r.TrapLays} 轮，消耗燃油）；燃迹炮塔挂上管线补给（“{supply.Replace("\n", " ")}”）");
            }
            return c.StepElapsed < 110 ? StepOutcome.Wait : StepOutcome.Fail($"火墙没在工作：铺了 {lays} 轮、炮塔管线消费者 {t?.ConsumerIds.Length}（“{supply}”）；陷阱状态“{BuildingStatusService.Evaluate(s, HomeGridService.FindBuilding(s, c.Get("trap.id"))).Reason}”");
        }

        private static StepOutcome TickSecondPlanned(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            RaidPlanRecord p2 = D.LivePlans(s).FirstOrDefault(p => !p.FirstRaid && p.Trigger == RaidCatalog.TriggerExposure && p.State >= RaidDirectorService.StateScheduled);
            if (p2 == null)
            {
                int tick = (int)(c.StepElapsed / 30);
                if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), tick);
                    c.Log($"等第二波：暴露 {s.SignalExposure:F1}（广播{(s.SignalTowerBroadcastOff ? "已关闭" : "开着")}、用电需求 {s.PowerDemand:F0}）");
                }
                return StepOutcome.Wait;
            }
            RaidHistoryRecord h1 = RaidDirectorService.History(s).FirstOrDefault(x => x.PlanId == c.Get("plan1"));
            long gap = h1 != null ? p2.ArrivalTick - h1.ArrivalTick : -1;
            c.Set("plan2", p2.PlanId);
            return gap >= RaidDirectorService.DayTicks(RaidCatalog.MinIntervalDays) - 2 && p2.Level <= 2
                ? StepOutcome.Done($"暴露 {s.SignalExposure:F1} 越过 30：排定第二波 {D.PlanLine(p2)}；与第一波抵达相隔 {RaidDirectorService.Duration(gap)}（最短间隔 1 个游戏日）")
                : StepOutcome.Fail($"第二波不对：{D.PlanLine(p2)}；与第一波（排定抵达 {h1?.ArrivalTick}）相隔 {gap} 步");
        }

        /// <summary>第三波：第二波之外的、由暴露触发的普通突袭排定了（1 级；与第二波抵达相隔至少最短间隔）。</summary>
        private static StepOutcome TickThirdPlanned(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            string p2id = c.Get("plan2");
            RaidPlanRecord p3 = D.LivePlans(s).FirstOrDefault(p => p.PlanId != p2id && !p.FirstRaid && p.Trigger == RaidCatalog.TriggerExposure && p.State >= RaidDirectorService.StateScheduled);
            if (p3 == null)
            {
                int tick = (int)(c.StepElapsed / 30);
                if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), tick);
                    c.Log($"等第三波：暴露 {s.SignalExposure:F1}（广播{(s.SignalTowerBroadcastOff ? "已关闭" : "开着")}、{GameClock.Speed}x）；计划 [{string.Join("；", D.LivePlans(s).Select(D.PlanLine))}]");
                }
                return StepOutcome.Wait;
            }
            RaidHistoryRecord h2 = RaidDirectorService.History(s).FirstOrDefault(x => x.PlanId == p2id);
            RaidPlanRecord live2 = RaidDirectorService.FindPlan(s, p2id);
            long arrive2 = h2?.ArrivalTick ?? live2?.ArrivalTick ?? -1;
            long gap = arrive2 >= 0 ? p3.ArrivalTick - arrive2 : -1;
            c.Set("plan3", p3.PlanId);
            return gap >= RaidDirectorService.DayTicks(RaidCatalog.MinIntervalDays) - 2 && p3.Level == 1
                ? StepOutcome.Done($"暴露 {s.SignalExposure:F1} 越过 30：排定第三波 {D.PlanLine(p3)}；与第二波抵达相隔 {RaidDirectorService.Duration(gap)}（最短间隔 1 个游戏日）")
                : StepOutcome.Fail($"第三波不对：{D.PlanLine(p3)}；与第二波（抵达 {arrive2}）相隔 {gap} 步");
        }

        private static StepOutcome TickFirewallReady(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            return !FgjM3Common.BuildOpen && !GameRoot.HomeValley.IsExpeditionPrepPanelOpen && s.SignalTowerBroadcastOff
                ? StepOutcome.Done($"火墙就绪（第 {GameClock.DayOf(GameClock.GameSeconds)} 日）；第二波 {D.PlanLine(RaidDirectorService.FindPlan(s, c.Get("plan2")))}；广播已关闭，暴露 {s.SignalExposure:F1}")
                : StepOutcome.Fail("火墙就绪时界面不在普通战略视角或广播没关");
        }

        // ── 远征 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickUplinkedFc(JourneyContext c)
        {
            int b = c.GetInt("other");
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点机器列表失败：" + FgjM1Journey.UiFail(c));
            }
            if (SignalPresence.CurrentMachineLogicId != b || GameRoot.FracturedCity?.PossessedMachineLogicId != b)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {FgjM1Journey.Label(b)}");
            }
            return c.StepElapsed < 2.0 ? StepOutcome.Wait : StepOutcome.Done($"接入 {FgjM1Journey.Label(b)}（信号跟着远征队；家园在后台照常运行）");
        }

        private static StepOutcome TickAwayAlert(JourneyContext c, string planKey)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            RaidPlanRecord p = planKey == "plan3" && string.IsNullOrEmpty(c.Get("plan3"))
                ? D.LivePlans(s).FirstOrDefault(x => x.PlanId != c.Get("plan2") && !x.FirstRaid && x.State >= RaidDirectorService.StateWarned)
                : RaidDirectorService.FindPlan(s, c.Get(planKey));
            RaidWarningHudUIToolkit hud = RaidWarningHudUIToolkit.Instance;
            hud?.Refresh(force: true);
            HomeRaidAlertView v = hud?.Away;
            int tick = (int)(c.StepElapsed / 60);
            if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), tick);
                c.Log($"远征中（镜头 {WorldView.ObservedSiteId}）：暴露 {s.SignalExposure:F1}；计划 [{string.Join("；", D.LivePlans(s).Select(D.PlanLine))}]");
            }
            if (p == null || p.State < RaidDirectorService.StateWarned || v == null || !v.PopupVisible)
            {
                return StepOutcome.Wait;
            }
            if (planKey == "plan3")
            {
                c.Set("plan3", p.PlanId);
            }
            c.SetInt(planKey + ".wave", p.Wave);
            bool away = WorldView.ObservedSiteId != HomeValleyLayout.RegionId && HomeRaidAlertService.IsAway(s);
            bool texts = (v.CountdownText.Contains("后抵达家园") || v.CountdownText.Contains("正在攻打家园")) && v.CoreText.Contains("归还核心") && v.KeysText.Contains("关键建筑") && v.JumpButton != null && v.StayVisible;
            return away && texts
                ? StepOutcome.Done($"远征 HUD 弹出“{v.AlertTitleText}”——“{v.CountdownText}”；家园小窗“{v.CoreText}”/“{v.KeysText}”/“{v.EnemiesText}”；{D.PlanLine(p)}")
                : StepOutcome.Fail($"家园遇袭通知不对：在远征 {away}；“{v.CountdownText}”/“{v.CoreText}”/“{v.KeysText}”");
        }

        private static StepOutcome TickJumpedHome(JourneyContext c)
        {
            CampaignState s = St;
            bool home = WorldView.ObservedSiteId == HomeValleyLayout.RegionId && !SignalUplinkService.IsJumpingHome && SignalPresence.AtCore;
            if (!home)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Retry($"点“跳回家园”后没回到家园（镜头 {WorldView.ObservedSiteId}、信号在核心 {SignalPresence.AtCore}；{FgjM1Journey.UiFail(c)}；“{HomeRaidAlertService.LastMessage}”）");
            }
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            int wave = c.GetInt("plan2.wave");
            int decision = HomeRaidAlertService.DecisionOf(s, wave);
            return decision == HomeRaidAlertService.ChoiceJumpHome
                ? StepOutcome.Done($"跳回家园：信号在归还核心、镜头回到家园（远征队交给 AI），这一波的选择记为“跳回家园”（第 {wave} 波）；突袭条“{RaidWarningHudUIToolkit.Instance?.RowText(0)?.Replace("\n", " / ")}”")
                : StepOutcome.Fail($"跳回家园了，但第 {wave} 波的选择是 {decision}");
        }

        /// <summary>第二波来袭方向（预警条那一行的抵达点）最近、蓝图带接入口的炮塔（开局标好的接入口：六座炮塔都能接入；燃迹炮塔也能）。</summary>
        private static void ChooseUplinkTurret(JourneyContext c)
        {
            CampaignState s = St;
            RaidPlanRecord p2 = RaidDirectorService.FindPlan(s, c.Get("plan2"));
            Vector2 at = p2 != null ? new Vector2((float)p2.ArriveX, (float)p2.ArriveY) : D.CoreCenter(s);
            BuildingRecord best = null;
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || b.BuildingTypeId != D.Turret || b.ConstructionState != BuildingConstructionState.Operational)
                {
                    continue;
                }
                TurretRecord tr = TurretService.Find(s, b.BuildingId);
                BlueprintRecord bp = tr != null ? BlueprintEditorService.Find(s, tr.BlueprintId) : null;
                BlueprintVersionRecord v = bp?.Versions?.FirstOrDefault(x => x.Version == tr.BlueprintVersion);
                if (v == null || !BlueprintCircuitBoard.FromVersion(v).HasUplink)
                {
                    continue;
                }
                if (best == null || Vector2.Distance(b.Position, at) < Vector2.Distance(best.Position, at))
                {
                    best = b;
                }
            }
            if (best == null)
            {
                c.Set("upt.miss", "没有带接入口的炮塔");
                return;
            }
            P.Remember(c, new P.Placed { Key = "upt", Type = D.Turret, Pivot = new GridCell(best.GridX, best.GridY), Rot = 0, Label = "离来袭方向最近的炮塔" });
            c.Set("upt.id", best.BuildingId);
            c.Log($"第二波抵达点 {at}：接入离它最近、带接入口的炮塔 {best.BuildingId}（{D.Cell(new GridCell(best.GridX, best.GridY))}，距 {Vector2.Distance(best.Position, at):F1} 米）");
        }

        private static StepOutcome TickTurretUplinked(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            string id = c.Get("upt.id");
            if (!TurretUplink.IsUplinkedTo(s, id))
            {
                return c.StepElapsed < 4 ? StepOutcome.Wait : StepOutcome.Retry($"点“接入”后信号没进炮塔（{FgjM1Journey.UiFail(c)}；“{TurretService.LastFeedback}”）");
            }
            return StepOutcome.Done($"信号进入炮塔 {id}（HUD“{SignalUplinkService.StatusLine(s)}”；“{TurretService.LastFeedback}”）");
        }

        private static StepOutcome TickUplinkCleared(JourneyContext c)
        {
            CampaignState s = St;
            if (!TurretUplink.IsUplinkedTo(s, c.Get("upt.id")))
            {
                return StepOutcome.Fail("收拾面板时信号离开了炮塔：" + TurretService.LastFeedback);
            }
            if (!TurretPanelUIToolkit.IsOpen && !ProductionPanelUIToolkit.IsOpen && !FgjM3Common.BuildOpen)
            {
                return c.StepElapsed < 0.6 ? StepOutcome.Wait : StepOutcome.Done("炮塔面板、通用面板、建造模式都关了，信号仍在炮塔里（左键 = 亲自开火）");
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "act")) < 800)
            {
                return StepOutcome.Wait;
            }
            c.SetLong(FgjM3Common.SK(c, "act"), NowMs());
            if (TurretPanelUIToolkit.IsOpen)
            {
                JourneyInput.ClickUitk(D.TurretHost, "TurretClose");
            }
            else if (ProductionPanelUIToolkit.IsOpen)
            {
                JourneyInput.ClickUitk(P.PanelHost, "ProductionPanelClose");
            }
            else if (FgjM3Common.BuildOpen)
            {
                FgjM3Common.PressBuild(false);
            }
            return c.StepElapsed > 12 ? StepOutcome.Retry("面板 / 建造模式关不掉：" + JourneyInput.LastUiFailure) : StepOutcome.Wait;
        }

        /// <summary>
        /// 接入燃迹炮塔后：敌人进了射程就左键点它（亲自瞄准开火；每 0.8 真实秒一次，点之前光标指在敌人身上），直到这一波结算结束。
        /// 核对至少有一发是接入时亲自打出去的（开火结果“已开火 / 正在瞄准”），并且结果不是核心被摧毁。
        /// </summary>
        private static StepOutcome TickManualFire(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            if (RaidResultService.IsCoreLost(s))
            {
                return StepOutcome.Fail("归还核心被突袭摧毁（战役失败）");
            }
            string id = c.Get("upt.id");
            RaidResultRecord r = RaidResultService.FindByPlan(s, c.Get("plan2"));
            if (c.GetInt(FgjM3Common.SK(c, "f0set")) == 0)
            {
                c.SetInt(FgjM3Common.SK(c, "f0set"), 1);
                c.SetInt(FgjM3Common.SK(c, "fires0"), TurretUplink.InputFires);
                c.SetInt(FgjM3Common.SK(c, "kills0"), TurretService.Find(s, id)?.KillCount ?? 0);
            }
            // 上一次点击的结果（点之后下一帧读）。
            if (c.GetInt(FgjM3Common.SK(c, "pending")) == 1 && NowMs() - c.GetLong(FgjM3Common.SK(c, "clickAt")) > 300)
            {
                c.SetInt(FgjM3Common.SK(c, "pending"), 0);
                BinGames.Sim.Combat.CombatFireResult res = TurretUplink.LastFireResult;
                if (res == BinGames.Sim.Combat.CombatFireResult.Ok || res == BinGames.Sim.Combat.CombatFireResult.StillAiming)
                {
                    c.SetInt(FgjM3Common.SK(c, "ok"), c.GetInt(FgjM3Common.SK(c, "ok")) + 1);
                }
                else
                {
                    c.Set(FgjM3Common.SK(c, "lastFail"), res + "：" + TurretService.LastFeedback);
                }
            }
            if (r != null && r.EndTick >= 0)
            {
                c.SetInt("res2", r.Serial);
                int ok = c.GetInt(FgjM3Common.SK(c, "ok"));
                int clicks = TurretUplink.InputFires - c.GetInt(FgjM3Common.SK(c, "fires0"));
                int kills = (TurretService.Find(s, id)?.KillCount ?? 0) - c.GetInt(FgjM3Common.SK(c, "kills0"));
                if (ok < 1)
                {
                    return StepOutcome.Fail($"这一波结束了，接入的炮塔没有一发是亲自打出去的（左键 {clicks} 次，最后一次“{c.Get(FgjM3Common.SK(c, "lastFail"))}”）：{D.ResultLine(r)}");
                }
                return StepOutcome.Done($"第二波被打退：{D.ResultLine(r)}；接入的炮塔左键 {clicks} 次、亲自开火 {ok} 发、这一波击毁 {kills} 台");
            }
            if (!TurretUplink.IsUplinkedTo(s, id))
            {
                return StepOutcome.Fail("突袭进行中信号离开了炮塔：" + TurretService.LastFeedback);
            }
            if (GameClock.Paused || JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "clickAt")) < 800)
            {
                return StepOutcome.Wait; // 自动暂停中先等暂停键那一下生效（点击会把它盖掉）
            }
            BuildingRecord tb = HomeGridService.FindBuilding(s, id);
            float range = TurretService.RangeOfTurret(s, id);
            if (tb == null || range <= 0f)
            {
                return StepOutcome.Fail("接入的炮塔不见了或没有射程");
            }
            (int Id, Vector2 Pos) target = D.Raiders().Where(x => Vector2.Distance(x.Pos, tb.Position) <= range - 0.5f).OrderBy(x => Vector2.Distance(x.Pos, tb.Position)).FirstOrDefault();
            if (target.Id == 0)
            {
                return StepOutcome.Wait;
            }
            if (!JourneyInput.OnScreen(target.Pos, 0.05f))
            {
                FgjM3Common.PanTo(target.Pos);
                return StepOutcome.Wait;
            }
            JourneyInput.Click(target.Pos);
            c.SetLong(FgjM3Common.SK(c, "clickAt"), NowMs());
            c.SetInt(FgjM3Common.SK(c, "pending"), 1);
            return StepOutcome.Wait;
        }

        private static StepOutcome TickTurretLeft(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            return !TurretUplink.IsActive && SignalPresence.AtCore
                ? StepOutcome.Done($"按接入键离开炮塔：“{TurretService.LastFeedback}”，信号回到归还核心")
                : StepOutcome.Retry($"按接入键后信号还在炮塔里（“{TurretService.LastFeedback}”）");
        }

        private static StepOutcome TickJumpedBack(JourneyContext c)
        {
            int b = c.GetInt("m1");
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            bool back = SignalPresence.CurrentMachineLogicId == b && GameRoot.FracturedCity != null && GameRoot.FracturedCity.PossessedMachineLogicId == b
                        && WorldView.ObservedSiteId != HomeValleyLayout.RegionId;
            if (!back)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Retry($"按跳回上一台机器键后没回到 {FgjM1Journey.Label(b)}（“{SignalUplinkService.LastFeedbackText}”，冷却 {SignalUplinkService.JumpCooldownRemaining(St):F1} 秒）");
            }
            return StepOutcome.Done($"跨地点远距离跳转回远征队：信号在 {FgjM1Journey.Label(b)}（镜头 {WorldView.ObservedSiteId}）；家园的突袭照常在后台结算");
        }

        private static StepOutcome TickStayed(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            RaidWarningHudUIToolkit hud = RaidWarningHudUIToolkit.Instance;
            hud?.Refresh(force: true);
            HomeRaidAlertView v = hud?.Away;
            int wave = c.GetInt("plan3.wave");
            bool stayed = v != null && v.PanelVisible && !v.PopupVisible && v.ChosenText.Contains("留在远征队") && WorldView.ObservedSiteId != HomeValleyLayout.RegionId
                          && SignalPresence.CurrentMachineLogicId == c.GetInt("m1") && HomeRaidAlertService.DecisionOf(s, wave) == HomeRaidAlertService.ChoiceStay;
            return stayed
                ? StepOutcome.Done($"留在远征队：紧急通知收起，小窗留着（“{v.ChosenText}”），镜头还在远征地点、信号在 {FgjM1Journey.Label(c.GetInt("m1"))}；第 {wave} 波记为“留下”")
                : StepOutcome.Retry($"点“留在远征队”后状态不对：弹窗 {v?.PopupVisible}、“{v?.ChosenText}”、镜头 {WorldView.ObservedSiteId}、选择 {HomeRaidAlertService.DecisionOf(s, wave)}（{FgjM1Journey.UiFail(c)}）");
        }

        // ── 回家 ──────────────────────────────────────────────────────────────────────

        private static StepOutcome TickDriveToEvac(JourneyContext c)
        {
            FgjM2Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            int b = c.GetInt("other");
            if (!FgjM1Journey.FcPos(b, out Vector2 at))
            {
                return StepOutcome.Fail("找不到接入的机器");
            }
            float d = Vector2.Distance(at, EvacPoint);
            if (d <= FracturedCityController.InteractRange - 0.8f)
            {
                JourneyInput.ReleaseKeys();
                return StepOutcome.Done($"{FgjM1Journey.Label(b)} 开到撤离点（离撤离点 {d:F1} 格）");
            }
            if (GameClock.Paused)
            {
                return StepOutcome.Wait; // 自动暂停中：等暂停键那一下生效，别用开车的按键把它盖掉
            }
            if (!JourneyInput.Holding)
            {
                int stuck = c.GetInt(FgjM3Common.SK(c, "stuck"));
                float last = float.TryParse(c.Get(FgjM3Common.SK(c, "last")), NumberStyles.Float, CultureInfo.InvariantCulture, out float l) ? l : float.MaxValue;
                if (d > last - 0.2f)
                {
                    c.SetInt(FgjM3Common.SK(c, "stuck"), stuck + 1);
                }
                c.Set(FgjM3Common.SK(c, "last"), d.ToString("R", CultureInfo.InvariantCulture));
                FgjM1Journey.DriveToward(at, EvacPoint, 0.4, stuck % 4 == 3 ? 1 : 0);
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickAwayReport(JourneyContext c)
        {
            CampaignState s = St;
            if (!AwayReportPanelUIToolkit.IsOpen)
            {
                if (!FgjM3Common.Once(c, "open", () => { JourneyInput.PressAction(GameActionId.OpenAwayReport); return true; }))
                {
                    return StepOutcome.Wait;
                }
                return FgjM3Common.SinceMs(c, "open") < 5000 ? StepOutcome.Wait : StepOutcome.Retry("按离家报告键（默认 Alt+H）后离家报告没有打开");
            }
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            AwayReportPanelUIToolkit panel = AwayReportPanelUIToolkit.Instance;
            panel.Refresh();
            var rows = Enumerable.Range(0, panel.VisibleRowCount).Select(i => panel.RowText(i).Replace("\n", " ")).ToList();
            string all = string.Join(" | ", rows);
            string jump = GameText.Get("raid.result.tl.choice_jump");
            string stay = GameText.Get("raid.result.tl.choice_stay");
            int raidSec = rows.FindIndex(x => x.Contains(GameText.Get("away.section.raid")));
            int arrive = rows.Count(x => x.Contains(GameText.Get("raid.result.tl.warn")) || x.Contains("部队到达"));
            bool ok = raidSec >= 0 && all.Contains(jump) && all.Contains(stay) && arrive >= 2 && !GameText.ContainsMarker(panel.SummaryText + all);
            return ok
                ? StepOutcome.Done($"离家报告（“{panel.SummaryText.Replace("\n", " ")}”）“突袭”一段：两波的时间线与两次选择（“{jump}”“{stay}”）；突袭段 [{string.Join(" | ", rows.Skip(raidSec).Take(14))}]")
                : StepOutcome.Fail($"离家报告的突袭段不对：突袭段第 {raidSec} 行、跳回家园 {all.Contains(jump)}、留在远征队 {all.Contains(stay)}、预警 / 到达行 {arrive}；[{all}]");
        }

        private static StepOutcome TickAwayClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return !AwayReportPanelUIToolkit.IsOpen ? StepOutcome.Done("点“关闭”：离家报告关闭") : StepOutcome.Retry("离家报告还开着：" + FgjM1Journey.UiFail(c));
        }

        /// <summary>两波里被摧毁、还没重建好的东西在等料，而废料不够（回收站拆的废墟有限，敌方残骸送回收站出得慢）：要拆一座用不着的建筑换废料。</summary>
        private static bool NeedScrap(JourneyContext c)
        {
            if (c.GetInt("scrDecided") == 1)
            {
                return c.GetInt("scrDemo") == 1;
            }
            CampaignState s = St;
            bool waiting = false;
            foreach (string k in new[] { "res2", "res3" })
            {
                RaidResultRecord r = RaidResultService.Find(s, c.GetInt(k));
                foreach (RaidLossRecord x in r?.Losses ?? Array.Empty<RaidLossRecord>())
                {
                    BuildingRecord b = x.Kind == "building" || x.Kind == "turret" || x.Kind == "defense" ? HomeGridService.FindBuilding(s, x.Id) : null;
                    waiting |= b != null && b.ConstructionState != BuildingConstructionState.Operational;
                }
            }
            bool need = waiting && s.Scrap < 60;
            c.SetInt("scrDecided", 1);
            c.SetInt("scrDemo", need ? 1 : 0);
            return need;
        }

        private static StepOutcome TickLabDemolishAsk(JourneyContext c)
        {
            if (c.GetInt("scrDemo") == 0)
            {
                return StepOutcome.Done("跳过");
            }
            BuildingRecord lab = P.Bld(c, "lab2");
            if (lab == null)
            {
                return StepOutcome.Fail("第二座仿真实验室不见了");
            }
            c.Set("scrLab", lab.BuildingId);
            c.SetInt("scrScrap0", St.Scrap);
            if (!FgjM3Common.Once(c, "click", () => FgjM3Common.TryClick(FgjM3Common.Ground(new GridCell(lab.GridX, lab.GridY)))))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.SinceMs(c, "click") < 600)
            {
                return StepOutcome.Wait;
            }
            if (HomeGridService.IsMarkedForDemolish(St, lab.BuildingId))
            {
                // 没有要确认的后果（不断谁的电、不切断谁的路）就直接标记拆除，状态行写明。
                return StepOutcome.Done($"拆除模式点第二座实验室：直接标记拆除（“{HomeValleyBuildMode.Current?.StatusText?.Replace("\n", " / ")}”），机器上门拆、全额返还");
            }
            ConfirmRequest r = UiConfirmDialog.Current;
            if (r == null)
            {
                return StepOutcome.Retry($"拆除模式点实验室既没标记拆除也没弹确认框（“{HomeValleyBuildMode.Current?.StatusText}”）");
            }
            return StepOutcome.Done($"确认框“{r.Title}”：{string.Join("；", r.Consequences.Concat(r.Lines))}");
        }

        private static StepOutcome TickLabMarked(JourneyContext c)
        {
            if (c.GetInt("scrDemo") == 0)
            {
                return StepOutcome.Done("跳过");
            }
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            string id = c.Get("scrLab");
            return !UiConfirmDialog.IsOpen && HomeGridService.IsMarkedForDemolish(St, id)
                ? StepOutcome.Done($"点“确认”：{id} 标记拆除，机器上门拆（全额返还投入的废料）")
                : StepOutcome.Retry($"确认后没有标记拆除（{FgjM1Journey.UiFail(c)}）");
        }

        /// <summary>
        /// 两波（跳回家园那一波、家园自己守那一波）里被摧毁的建筑 / 炮塔 / 防御建筑：每一座都已经（或正在）由自动重建规则按原样重建——同一格、同一种建筑、建成可用；
        /// 规则日志里有这条规则对它的记录（FGR-BASE-020 可追溯）。被摧毁的一座都没有 = 旅程没能证明“自动重建”，如实失败。
        /// </summary>
        private static StepOutcome TickRebuilt(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            int rule = c.GetInt("rule");
            var lost = new List<RaidLossRecord>();
            foreach (string k in new[] { "res2", "res3" })
            {
                RaidResultRecord r = RaidResultService.Find(s, c.GetInt(k));
                if (r != null)
                {
                    lost.AddRange(r.Losses.Where(x => x.Kind == "building" || x.Kind == "turret" || x.Kind == "defense"));
                }
            }
            lost = lost.GroupBy(x => x.Id).Select(g => g.Last()).ToList();
            if (lost.Count == 0)
            {
                return StepOutcome.Fail($"两波里没有任何建筑被摧毁，没法证明自动重建（第二波：{D.ResultLine(RaidResultService.Find(s, c.GetInt("res2")))}；第三波：{D.ResultLine(RaidResultService.Find(s, c.GetInt("res3")))}）");
            }
            var waiting = new List<string>();
            var done = new List<string>();
            IReadOnlyList<RuleLogRecord> log = StandingRuleService.LogEntries(s);
            foreach (RaidLossRecord x in lost)
            {
                BuildingRecord b = HomeGridService.FindBuilding(s, x.Id);
                bool up = b != null && !HomeValleyController.IsPlannedGhost(b) && b.ConstructionState == BuildingConstructionState.Operational;
                bool traced = log.Any(e => e != null && e.Rule == rule && (e.EntityId ?? string.Empty).Contains(x.Id));
                if (up && traced)
                {
                    done.Add($"{x.Name}（{x.Id}）");
                }
                else
                {
                    waiting.Add($"{x.Name}：{(b == null ? "记录没了" : up ? "已建成" : b.ConstructionState.ToString())}、规则日志 {(traced ? "有" : "没有")}");
                }
            }
            if (waiting.Count == 0)
            {
                return StepOutcome.Done($"被摧毁的 {done.Count} 座已由自动重建 R{rule} 按原样重建：{string.Join("、", done)}；废料 {s.Scrap}");
            }
            int tick = (int)(c.StepElapsed / 30);
            if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), tick);
                string sites = string.Join("；", lost.Select(x => HomeGridService.FindBuilding(s, x.Id)).Where(b => b != null && b.ConstructionState != BuildingConstructionState.Operational)
                    .Select(b => HomeValleyConstruction.TryDescribeSite(s, new GridCell(b.GridX, b.GridY), out string st, out string sb) ? $"{st}：{sb?.Replace("\n", " / ")}" : $"{BuildingOps.NameOf(b)}：不在施工队列"));
                c.Log($"等重建：[{string.Join("；", waiting)}]；施工点 [{sites}]；残骸去向 {RaidResultService.RouteText(RaidResultService.StateOf(s)?.WreckRouting ?? 0)}；{D.WorkDiag(s)}");
            }
            return StepOutcome.Wait;
        }
    }
}
