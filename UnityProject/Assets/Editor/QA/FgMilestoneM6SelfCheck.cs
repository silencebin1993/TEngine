using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.EditorTools.JourneyBots;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG6-E2E-01 自检 [M6 出口]：里程碑出口的自动化部分里能在编辑模式下逐语义断言的那些（旅程本身在 Play 里由 tools/unity-journey.sh FGJ-M6 / FGJ-M6R 跑）。
    ///
    /// A. 旅程登记：FGJ-M6 覆盖出口旅程原文（第一座炮塔挡住第一次 1 级突袭、漏油带 + 燃迹炮塔火墙、出发远征、远征中家园遇袭两种选择、离家报告突袭时间线、自动重建），
    ///    三波突袭全是正式触发、旅程里没有突袭夹具；FGJ-M6R 覆盖 IC-REQ-022 六类（每类出错 → 恢复）并点名 FG6-DEF-08 移交的两条承接旅程（熔合回滚、黑匣子 → 研究）；种子不同；都登记断点。
    /// A2. 旅程源码只走输入通道：不直接调防御 / 突袭 / 接入 / 规则 / 面板等业务方法；FGJ-M6R 的三处进度夹具（研究、剧情突袭、芯片基板）只在登记的夹具方法里，FGJ-M6 一处都没有。
    /// B. B25：FGJ-M6 的家园防线规划（回收站、实验室、精炼链、六座炮塔、护盾、屏障圈）在 5 个种子上都找得到、逐件过放置校验（只差研究的算过）、互不重叠；
    ///    防线放下之后，火墙按 4 个方向的抵达点都规划得出（改装离抵达点最近的炮塔 + 陷阱发射器 + 经缺口的燃油管线）；FGJ-M6R 的完全围死规划在 2 个种子上找得到。
    /// B2. 防线规划守得住（FGT-DEF-001 / 003 / 005 合场，真实突袭导演 + 真实攻城 + 真实炮塔）：种子 42 与 1 上按 FGJ-M6 的规划布防、按旅程的广播开关节奏（五次），
    ///    三波突袭（都是 1 级）都守住、核心不丢；第二 / 三波有建筑损失（旅程要证明自动重建）。
    /// C. 缺口清零：门禁第一个里程碑是 M6 的缺口全部 Closed / 部分关闭、延后项全部 Closed / 部分关闭 / 顺延；本 Story 的登记与关闭在表里。
    /// D. 试玩包：FG-M6 的试玩脚本、通过标准对照表（FGR-BAL-061 M6：大多数人说突袭“紧张 / 有趣”而不是“烦”）、问卷与记录模板、问题清单模板齐全，目录登记“待用户试玩、不阻塞”。
    /// E. 需求覆盖（IC-REQ-020）：FG06 FGT-DEF-001～011 各有自检段且登记在全量自检，另有旅程步骤。
    /// F. FGT-DEF-010 性能场景（第 7 章规模，真实系统合场）：80 座真实炮塔 + 屏障圈 + 200 台真实攻城单位同时在场——世界一步、内核单步、流场增量更新（≤ 4 ms）、弹体峰值；
    ///    120 帧口径（3x 下每帧 1.5 个模拟步 ≤ 8.33 ms）。
    /// G1. 承接 DEBT-FG5E2E01-05：真实攻城单位打坏正在熔合的电路合成台 → 熔合事务回滚（父固件、芯片基板、技术数据守恒，通知“熔合中止”）。
    /// G2. 承接 DEBT-FG5RND06-03：真实攻城单位打死家园机器 → 黑匣子直接回收 → 陈列馆分析 1 个游戏日 → 技术数据入账 → 实验室把它转成研究点。
    /// H. FG-GAP-112：远征准备面板的暴露行、“关闭广播”、“关闭”“确认出发”在 Body 外（没有出征目标 / 远征在外时也能关广播、关面板）+ 布局探针。
    /// H3. FG-GAP-113：施工完工那一刻站在占地里的家园机器挪到最近的空格（不被关在墙里）；闸门放行己方，不挪。
    /// H2. 承接 DEBT-FG6DEF02-05 的“护盾 64 上限”：一个地点建满 CombatConst.MaxShields 座护盾后，多出来的那座不工作，状态行写明“已达上限”；拆掉一座后它自动补上。
    /// H4. FG-GAP-114（行为）：接入直控时要自动暂停的通知不暂停世界、照常进历史、世界照常推进；回到战略视角后同一类通知照常暂停。
    /// H5. 行进队伍路线失效只查这次变化的区块、到达半径里不算、挡在后面的路段走到挡点前再规划（M6 出口性能基线“施工高峰”卡顿的根因修复守护，ADR-QA-023 第 9 节）。
    /// </summary>
    public static class FgMilestoneM6SelfCheck
    {
        private const int Slot = 7;
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static int _seq;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/M6 出口")]
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
            Line("\n[M6 出口] FG6-E2E-01：旅程登记与正式输入、规划不依赖固定坐标、防线守得住、缺口清零、试玩包、需求覆盖、FGT-DEF-010 性能场景、突袭打坏熔合 / 打死机器的承接链路");
            Step(CheckJourneyCatalog);
            Step(CheckJourneySourcesUseInput);
            Step(CheckGapGate);
            Step(CheckPlaytestPack);
            Step(CheckRequirementCoverage);
            Step(CheckExposureRow);
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过 B / B2 / F / G 段（会改写世界）");
            }
            else
            {
                WorldChecks();
            }
            foreach (string p in PerfLines)
            {
                Line("  · 性能：" + p);
            }
            Line($"  · [M6 出口] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static void Step(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Fail($"{a.Method.Name} 抛异常：{e}");
            }
        }

        // ── A. 旅程登记 ────────────────────────────────────────────────────────────

        private static bool Wellformed(JourneyDef d) => d != null && d.Steps.Count > 0 && d.Steps.Select(x => x.Id).Distinct().Count() == d.Steps.Count
                                                        && d.Steps.All(x => x.TimeoutSeconds > 0 && x.Tick != null) && d.TotalTimeoutSeconds > 0;

        private static List<string> Missing(JourneyDef d, IEnumerable<(string What, string[] Ids)> map) =>
            map.Where(x => d == null || x.Ids.Any(id => d.Steps.All(s => s.Id != id))).Select(x => x.What).ToList();

        private static void CheckJourneyCatalog()
        {
            Line("  · A. 旅程登记：FGJ-M6 覆盖出口旅程原文；FGJ-M6R 覆盖 IC-REQ-022 六类与 FG6-DEF-08 移交的两条承接旅程（熔合回滚、黑匣子 → 研究）；种子不同；两条都登记断点");
            JourneyDef m6 = JourneyCatalog.Create(FgjM6Journey.Id);
            JourneyDef m6r = JourneyCatalog.Create(FgjM6ReverseJourney.Id);
            var exit = new (string What, string[] Ids)[]
            {
                ("造第一座炮塔（建造栏防御分类、默认蓝图、机器施工）", new[] { "m6_place1", "m6_built1" }),
                ("挡住第一次（1 级）突袭：正式暴露触发、开局宽限、预警、到达、炮塔打退、结算面板", new[] { "exp_first", "w1_warn", "w1_arrive", "w1_end", "w1_close" }),
                ("布置“漏油带 + 燃迹炮塔”火墙（按第一次突袭的抵达点：离抵达点最近的炮塔在炮塔面板换上燃迹新版本、旁边陷阱选漏油朝抵达点铺、燃油管线）", new[] { "ana_oil_ok", "ana_burn_ok", "cb_save", "fw_plan", "m6_place5", "fw_built", "fwt_bp", "trap_fw", "fw_lay" }),
                ("炮塔阵地自动打出反应：第二、三波至少一波火墙打出爆燃（FGT-DEF-002）", new[] { "w3_end" }),
                ("出发远征", new[] { "prep_open", "prep_pick", "prep_depart", "exp_up" }),
                ("远征途中家园遇袭：跳回家园接入炮塔防守（开局给默认炮塔蓝图标好接入口，接入离来袭方向最近的那座）", new[] { "cb0_mark", "cb0_save", "w2_alert", "w2_jump", "w2_turret", "w2_uplink", "w2_fire", "w2_leave", "w2_back" }),
                ("远征途中家园遇袭：信号留在远征队、家园自己守", new[] { "w3_alert", "w3_stay", "w3_end" }),
                ("回来看离家报告里的突袭时间线", new[] { "evac_confirm", "away_open", "away_close" }),
                ("被摧毁的建筑已经自动重建（研究 → 常驻规则 → 规则日志可追溯）", new[] { "wait_rebuild", "rules_new", "rebuilt" }),
                ("回补：屏障圈拖拽、护盾、炮塔批量目标模式（DEBT-FG6DEF02-05 / 01-08）", new[] { "m6_walls", "m6_place4", "tm_mode" }),
                ("回补：暴露上涨 → 突袭 → 防守走正式触发（DEBT-FG6DEF04-09）", new[] { "exp_first", "exp_off1", "exp_falling", "exp_on1", "w2_plan", "exp_off2", "exp_on2", "w3_plan", "exp_off3" }),
            };
            List<string> missing = Missing(m6, exit);
            Expect(Wellformed(m6) && missing.Count == 0 && m6.Seed == FgjM6Journey.TestSeed,
                $"FGJ-M6：{m6?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m6?.Seed}；出口旅程原文与承接 / 回补都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            var reverse = new (string What, string[] Ids)[]
            {
                ("资源不足（陷阱缺水 → 补上管线）", new[] { "r1_fw", "r1_short", "r1_fix", "r1_flow" }),
                ("路径失败（屏障圈没留闸门 → 放置预览警告机器到不了 → 改放闸门 → 敌方被圈死、己方放行）", new[] { "walls", "pf_hover", "pf_gate", "pf_built" }),
                ("暂停与倍速（攻城中）", new[] { "ra_siege", "ra_pause", "ra_frozen", "ra_resume", "ra_half", "ra_triple", "ra_wall" }),
                ("存读档（攻城中）", new[] { "rb_siege", "rb_esc", "rb_quit", "rb_menu", "rb_load", "rb_loaded", "rb_end" }),
                ("断链（攻城中、敌人进射程、炮塔正在开火时禁用全部发电机 → 炮塔缺电、开火次数不再增加 → 逐台启用 → 来电、恢复开火）", new[] { "rc_list", "rc_siege", "rc_engage", "rc_off0", "rc_nopower", "rc_on0", "rc_power" }),
                ("目标死亡（接入的机器被打死、全灭应急打印）", new[] { "rc_send_a", "rc_charge", "rc_attack" }),
                ("承接 DEBT-FG5E2E01-05：真实突袭打坏熔合中的合成台 → 回滚（复原 / 刻印父固件、合成台放在抵达点旁、正式熔合入队、攻城单位拆掉、回滚守恒、结算损失）", new[] { "fu_ana_ok", "fu_print_n2", "fu_plan", "fu_built", "fu_sim", "fu_formal", "fu_formal2", "rs_siege", "fu_rolled" }),
                ("承接 DEBT-FG5RND06-03：真实突袭打死家园机器 → 黑匣子 → 陈列馆分析 → 研究（第三波打死接入的机器、陈列馆与实验室、研究排队、分析完入账、研究点）", new[] { "rc_charge", "bb_built", "bb_rt_q", "bb_done" }),
            };
            List<string> missingR = Missing(m6r, reverse);
            Expect(Wellformed(m6r) && missingR.Count == 0 && m6r.Seed != m6.Seed,
                $"FGJ-M6R：{m6r?.Steps.Count} 步；六类反向场景齐全{(missingR.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missingR))}；种子 {m6r?.Seed} ≠ {m6?.Seed}（B25）");
            bool ck = m6 != null && m6r != null && m6.CheckpointAfter.Length >= 2 && m6r.CheckpointAfter.Length >= 1
                      && m6.CheckpointAfter.All(id => m6.Steps.Any(s => s.Id == id)) && m6r.CheckpointAfter.All(id => m6r.Steps.Any(s => s.Id == id));
            Expect(ck && JourneyCatalog.Ids.Contains(FgjM6Journey.Id) && JourneyCatalog.Ids.Contains(FgjM6ReverseJourney.Id) && JourneyCatalog.Ids.Contains(FgjM5Journey.Id),
                $"两条 M6 旅程登记在旅程登记表、前序旅程仍在；断点：FGJ-M6 [{string.Join(", ", m6?.CheckpointAfter ?? Array.Empty<string>())}]、FGJ-M6R [{string.Join(", ", m6r?.CheckpointAfter ?? Array.Empty<string>())}] 都是旅程里的步骤");
        }

        // ── A2. 旅程源码只走输入通道 ─────────────────────────────────────────────────

        /// <summary>业务方法在旅程源码里出现 = 绕开了玩家输入（M5 的清单 + M6 的防御 / 突袭 / 接入 / 规则 / 面板）。</summary>
        private static readonly string[] ForbiddenCalls =
        {
            "PlanHistory.Undo(", "PlanHistory.Place(", "PlanHistory.PlaceBeltPath(", "HomeGridService.TryPlace", "HomeGridService.TryRemove", "HomeGridService.TryToggleDemolish",
            "BeltNetworkService.TryPlace", "PipeNetworkService.TryPlace", "BeltPortService.TrySetFilter", "ExpeditionDepartureService.TryDepart", "CampaignSaveService.Save(",
            "UiConfirmDialog.Confirm(", "HomeValleyFactory.TryEnqueue", ".SetScrapSubstitute(", "BuildingOps.Try", "ProductionPanelUIToolkit.Open(", "ExpeditionReturnService.TryConfirm",
            "GameClock.SetPaused(", "GameClock.SetSpeed(", "GameClock.SkipForTests(", "HomeValleyPowerGrid.TrySet", "ResearchService.TryEnqueue", "ResearchTreePanelUIToolkit.Open",
            "FirmwareRestoreService.TryRestore(", "TurretService.Try", "TurretService.SetPlacementBlueprint(", "TurretUplink.Request(", "TurretUplink.Leave(", "TurretUplink.FireAt(",
            "DefenseService.Try", "DefenseService.PlanWall(", "HomeRaidAlertService.TryJumpHome(", "HomeRaidAlertService.Stay(", "SignalUplinkService.Request",
            "StandingRuleService.TryCreate(", "StandingRuleService.TrySetEnabled(", "CampaignExposureLedger.SetTowerBroadcastOff(", "CampaignExposureLedger.Grant",
            "TurretPanelUIToolkit.Open(", "DefensePanelUIToolkit.Open(", "RaidResultPanelUIToolkit.Open", "AwayReportPanelUIToolkit.Open", "RulesPanelUIToolkit.Open",
            "NotificationCenter.TryOpen(", "BuildingOps.ApplyDamage(", "MachineRegistry.ApplyDamage(", "MachineRegistry.SpawnMachine(", "WorldTransitSystem.Dispatch",
            "DifficultyService.Change(", "RaidWarningHudUIToolkit.Instance.Click", "HomeRaidAlertView.Click", ".ClickJump(", ".ClickStay(", ".ClickMode(", ".SelectFirmware(",
            ".ClickPattern(", ".ClickUplink(", ".SelectBlueprint(", ".CreateKind(",
        };

        private static readonly string[] JourneyFiles =
        {
            "FgjM6Common.cs", "FgjM6Journey.cs", "FgjM6Journey.Prep.cs", "FgjM6Journey.Raids.cs", "FgjM6ReverseJourney.cs",
        };

        /// <summary>夹具：只能出现在这些方法体里（DEBT 编号见 ADR-QA-023 第 5 节）。FGJ-M6 一处突袭夹具都没有。</summary>
        private static readonly (string Token, string Method, int Count, string Debt)[] Fixtures =
        {
            ("ResearchService.CompleteForTests(", "ApplyResearchFixture", 1, "FGJ-M6R 屏障 / 陷阱已研究 DEBT-FG6E2E01-02"),
            ("RaidDirectorService.RequestStoryRaid(", "ApplyRaidFixture", 1, "FGJ-M6R 剧情节点触发突袭 DEBT-FG6E2E01-03"),
            ("M.ApplySubstrateFixture(", "ApplySubstrateFixture", 1, "FGJ-M6R 芯片基板进仓库（与 FGJ-M5 同一个夹具）DEBT-FG5E2E01-02"),
        };

        private static void CheckJourneySourcesUseInput()
        {
            Line("  · A2. M6 旅程源码只走输入通道，不直接调业务方法；FGJ-M6R 的三处进度夹具只在登记的方法里，FGJ-M6 没有突袭夹具");
            var hits = new List<string>();
            var raw = new StringBuilder();
            var codeAll = new StringBuilder();
            string m6Code = string.Empty;
            foreach (string f in JourneyFiles)
            {
                string text = ReadRepo("TEngine/UnityProject/Assets/Editor/JourneyBots/" + f);
                if (text == null)
                {
                    Fail("找不到旅程源码 " + f);
                    return;
                }
                raw.Append(text);
                string code = StripComments(text);
                codeAll.Append(code);
                if (f != "FgjM6ReverseJourney.cs")
                {
                    m6Code += code;
                }
                hits.AddRange(ForbiddenCalls.Where(code.Contains).Select(x => f + ":" + x));
            }
            Expect(hits.Count == 0, $"旅程源码里没有业务方法调用（扫描 {ForbiddenCalls.Length} 种）{(hits.Count == 0 ? string.Empty : "；发现：" + string.Join("、", hits))}");
            string all = codeAll.ToString();
            var bad = new List<string>();
            var seen = new List<string>();
            foreach ((string token, string method, int count, string debt) in Fixtures)
            {
                int total = Count(all, token);
                string body = MethodBody(all, method);
                int n = body == null ? 0 : Count(body, token);
                seen.Add($"{debt}：{method} {n} 处");
                if (n != count || total != n)
                {
                    bad.Add($"{token} 在 {method} 里 {n} 处（应 {count}）、方法外 {total - n} 处");
                }
            }
            bool m6Clean = !m6Code.Contains("RequestStoryRaid(") && !m6Code.Contains("CompleteForTests(") && !m6Code.Contains("SkipForTests(") && !m6Code.Contains("Dispatch(");
            Expect(bad.Count == 0 && m6Clean,
                $"夹具只在登记的方法里（{string.Join("；", seen)}）；FGJ-M6 没有任何突袭 / 研究 / 跳时夹具（三波都是正式触发）：{m6Clean}{(bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad))}");
            string src = raw.ToString();
            string[] inputs =
            {
                "FgjM3Common.PressBuild(", "GameActionId.OpenResearch", "GameActionId.OpenRules", "GameActionId.OpenAwayReport", "GameActionId.TogglePause", "GameActionId.SpeedHalf",
                "GameActionId.SpeedTriple", "GameActionId.Cancel", "GameActionId.ToggleCameraView", "GameActionId.JumpPreviousMachine", "GameActionId.Interact",
                "JourneyInput.ClickElement(", "JourneyInput.ClickUitk(", "JourneyInput.Click(", "\"RaidAwayJump\"", "\"RaidAwayStay\"", "\"TurretUplink\"", "\"PrTurret\"", "\"PrDefense\"",
                "\"TowerBroadcastOffToggle\"", "\"RulesPanelClose\"", "\"DepartButton\"", "\"PauseSaveQuit\"", "\"m_btn_Load\"", "\"BpEnable\"", "\"BpRepair\"", "\"AwayReportClose\"",
                "\"SaveButton\"", "\"UplinkToggleButton\"",
            };
            List<string> miss = inputs.Where(x => !src.Contains(x)).ToList();
            bool rts = src.Contains("JourneyInput.Click(at, 1)") && src.Contains("TickManualFire");
            Expect(miss.Count == 0 && rts,
                "旅程用到的正式输入：建造 / 研发树 / 常驻规则 / 离家报告 / 暂停 / 倍速 / Esc / 接入键 / 跳回上一台 / 交互键，UI Toolkit 点击；远征小窗“跳回家园”“留在远征队”、炮塔面板“接入”、" +
                $"通用面板“炮塔… / 陷阱… / 启用禁用 / 重建”、远征准备面板广播开关与出发、存档 / 读取；RTS：右键敌人攻击、接入炮塔左键亲自开火（{rts}）{(miss.Count == 0 ? string.Empty : "；缺：" + string.Join("、", miss))}");
        }

        private static int Count(string text, string token)
        {
            int n = 0;
            for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
            {
                n++;
            }
            return n;
        }

        /// <summary>取 “void 方法名(” 的方法体（按花括号配对；源码已去掉注释）。没有返回 null。</summary>
        private static string MethodBody(string code, string method)
        {
            Match m = Regex.Match(code, @"void\s+" + Regex.Escape(method) + @"\s*\(");
            if (!m.Success)
            {
                return null;
            }
            int open = code.IndexOf('{', m.Index);
            int arrow = code.IndexOf("=>", m.Index, StringComparison.Ordinal);
            if (arrow >= 0 && (open < 0 || arrow < open))
            {
                int end = code.IndexOf(';', arrow);
                return end > arrow ? code.Substring(arrow, end - arrow + 1) : null;
            }
            if (open < 0)
            {
                return null;
            }
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{')
                {
                    depth++;
                }
                else if (code[i] == '}' && --depth == 0)
                {
                    return code.Substring(open, i - open + 1);
                }
            }
            return null;
        }

        /// <summary>取任意返回类型的方法 “名字(” 之后的第一个方法体（按花括号配对；源码已去掉注释）。没有返回 null。</summary>
        private static string MethodBodyAny(string code, string method)
        {
            Match m = Regex.Match(code, @"\b" + Regex.Escape(method) + @"\s*\(");
            if (!m.Success)
            {
                return null;
            }
            int open = code.IndexOf('{', m.Index);
            if (open < 0)
            {
                return null;
            }
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{')
                {
                    depth++;
                }
                else if (code[i] == '}' && --depth == 0)
                {
                    return code.Substring(open, i - open + 1);
                }
            }
            return null;
        }

        private static string StripComments(string code)
        {
            var sb = new StringBuilder(code.Length);
            foreach (string raw in code.Split('\n'))
            {
                string l = raw;
                int i = l.IndexOf("//", StringComparison.Ordinal);
                if (i >= 0 && !l.Substring(0, i).Contains("\""))
                {
                    l = l.Substring(0, i);
                }
                sb.Append(l).Append('\n');
            }
            return sb.ToString();
        }

        // ── C. 缺口清零 ────────────────────────────────────────────────────────────

        private static readonly Regex FirstMilestone = new Regex(@"M(\d+)");

        private static int FirstMilestoneOf(string s)
        {
            Match m = FirstMilestone.Match(s ?? string.Empty);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
        }

        private static void CheckGapGate()
        {
            Line("  · C. 里程碑出口：属于 FG-M6 的缺口全部 Closed / 部分关闭、延后项全部 Closed / 部分关闭 / 顺延（按门禁列的第一个里程碑判定）");
            string text = ReadRepo("production/design/full-game/FG-GAP-REGISTER.md");
            if (text == null)
            {
                Fail("找不到 production/design/full-game/FG-GAP-REGISTER.md（仓库根定位失败）");
                return;
            }
            var openGaps = new List<string>();
            var openDebts = new List<string>();
            int gapRows = 0, debtRows = 0, m6Rows = 0;
            var status = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                if (!raw.StartsWith("| FG-GAP-", StringComparison.Ordinal) && !raw.StartsWith("| DEBT-", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] cols = raw.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                string st = cols[cols.Length - 1];
                status[cols[0]] = st;
                if (raw.StartsWith("| FG-GAP-", StringComparison.Ordinal))
                {
                    gapRows++;
                    if (cols.Length >= 7 && FirstMilestoneOf(cols[5]) == 6)
                    {
                        m6Rows++;
                        if (!st.StartsWith("Closed", StringComparison.Ordinal) && !st.StartsWith("部分关闭", StringComparison.Ordinal))
                        {
                            openGaps.Add(cols[0]);
                        }
                    }
                    continue;
                }
                debtRows++;
                if (cols.Length < 9 || FirstMilestoneOf(cols[6]) != 6)
                {
                    continue;
                }
                m6Rows++;
                if (!st.StartsWith("Closed", StringComparison.Ordinal) && !st.StartsWith("部分关闭", StringComparison.Ordinal) && !st.StartsWith("顺延", StringComparison.Ordinal))
                {
                    openDebts.Add(cols[0]);
                }
            }
            Expect(gapRows > 100 && debtRows > 300 && m6Rows >= 15, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中门禁属于 FG-M6 的 {m6Rows} 行（解析没有漏行）");
            Expect(openGaps.Count == 0 && openDebts.Count == 0,
                openGaps.Count + openDebts.Count == 0
                    ? "属于 FG-M6 的缺口全部 Closed / 部分关闭（剩余部分另有门禁），延后项全部 Closed / 部分关闭 / 顺延（顺延写明理由与新门禁）"
                    : $"FG-M6 出口前还开着：缺口 [{string.Join("、", openGaps)}]，延后项 [{string.Join("、", openDebts)}]");
            string[] mine = { "DEBT-FG6E2E01-01", "DEBT-FG6E2E01-02", "DEBT-FG6E2E01-03", "DEBT-FG6E2E01-04", "FG-GAP-111" };
            string[] closedHere = { "DEBT-FG6DEF04-09", "DEBT-FG6DEF01-08", "DEBT-FG1E2E01-04", "DEBT-FG4E2E01-01", "DEBT-FG5E2E01-05", "DEBT-FG5RND06-03", "FG-GAP-112", "FG-GAP-113", "FG-GAP-114" };
            List<string> notClosed = closedHere.Where(id => !status.TryGetValue(id, out string v) || !v.StartsWith("Closed", StringComparison.Ordinal)).ToList();
            bool registered = mine.All(status.ContainsKey);
            bool pending = status.TryGetValue("DEBT-FG6E2E01-01", out string p1) && p1.Contains("待用户");
            Expect(registered && notClosed.Count == 0 && pending,
                "本 Story 的登记在表里（DEBT-FG6E2E01-01 待用户试玩、-02 反向旅程研究夹具、-03 反向旅程剧情突袭夹具、-04 屏障升级拖框与护盾过载旅程断言、FG-GAP-111 暴露与突袭强度的平衡观察）；" +
                $"由本 Story 关闭的 {closedHere.Length} 条都是 Closed" + (notClosed.Count == 0 ? string.Empty : "；没关：" + string.Join("、", notClosed)));
        }

        // ── D. 试玩包 ──────────────────────────────────────────────────────────────

        private static void CheckPlaytestPack()
        {
            Line("  · D. 试玩包：FG-M6 材料齐全（5 人试玩由用户择时进行，DEBT-FG6E2E01-01，不阻塞）");
            const string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M6-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M6-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M6-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M6-问题清单模板.md");
            string readme = ReadRepo(dir + "README.md");
            bool exist = script != null && table != null && form != null && issues != null && readme != null;
            Expect(exist, "四份材料（试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板）与目录说明都在");
            if (!exist)
            {
                return;
            }
            bool tasks = new[] { "炮塔", "突袭", "火墙", "远征", "跳回家园", "留在远征队", "离家报告", "自动重建" }.All(script.Contains);
            bool standard = table.Contains("紧张") && table.Contains("有趣") && table.Contains("烦") && table.Contains("5") && table.Contains("FGJ-M6");
            bool questions = form.Contains("FG06") && form.Contains("大功告成") && form.Contains("抉择") && form.Contains("频率") && form.Contains("DEBT-FG6DEF08-05");
            bool registered = readme.Contains("FG-M6") && readme.Contains("DEBT-FG6E2E01-01") && readme.Contains("不阻塞");
            Expect(tasks && standard && questions && registered,
                $"脚本按出口旅程给任务卡（{tasks}）；对照表写 FGR-BAL-061 的 M6 标准（5 人、紧张 / 有趣 vs 烦，{standard}）；问卷对照 FG06 第 9 节三问并带多队伍结算读数复核（DEBT-FG6DEF08-05，{questions}）；" +
                $"目录登记“待用户试玩、不阻塞”（{registered}）");
        }

        // ── E. 需求覆盖 ────────────────────────────────────────────────────────────

        private static void CheckRequirementCoverage()
        {
            Line("  · E. 需求覆盖（IC-REQ-020）：FGT-DEF-001～011 各有自检段且登记在全量自检，另有旅程步骤");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            const string qa = "TEngine/UnityProject/Assets/Editor/QA/";
            var map = new (string Test, string File, string Registered, string Journey, string Step)[]
            {
                ("FGT-DEF-001", "FgTurretSelfCheck.cs", "FgTurretSelfCheck", FgjM6Journey.Id, "w1_end"),
                ("FGT-DEF-002", "FgDefenseStructuresSelfCheck.cs", "FgDefenseStructuresSelfCheck", FgjM6Journey.Id, "w3_end"),
                ("FGT-DEF-003", "FgRaidDirectorSelfCheck.cs", "FgRaidDirectorSelfCheck", FgjM6Journey.Id, "w2_plan"),
                ("FGT-DEF-004", "FgRaidDirectorSelfCheck.cs", "FgRaidDirectorSelfCheck", FgjM6Journey.Id, "w1_warn"),
                ("FGT-DEF-005", "FgSiegeSelfCheck.cs", "FgSiegeSelfCheck", FgjM6ReverseJourney.Id, "ra_siege"),
                ("FGT-DEF-006", "FgHomeRaidAlertSelfCheck.cs", "FgHomeRaidAlertSelfCheck", FgjM6Journey.Id, "w3_stay"),
                ("FGT-DEF-007", "FgHomeRaidAlertSelfCheck.cs", "FgHomeRaidAlertSelfCheck", FgjM6Journey.Id, "w3_end"),
                ("FGT-DEF-008", "FgRepairDroneSelfCheck.cs", "FgRepairDroneSelfCheck", FgjM6Journey.Id, "rebuilt"),
                ("FGT-DEF-009", "FgRaidResultSelfCheck.cs", "FgRaidResultSelfCheck", FgjM6ReverseJourney.Id, "rb_loaded"),
                ("FGT-DEF-010", "FgMilestoneM6SelfCheck.cs", "FgMilestoneM6SelfCheck", FgjM6Journey.Id, "w2_fire"),
                ("FGT-DEF-011", "FgDifficultySelfCheck.cs", "FgDifficultySelfCheck", FgjM6Journey.Id, "difficulty"),
            };
            var bad = new List<string>();
            foreach ((string test, string file, string registered, string journey, string step) in map)
            {
                string src = ReadRepo(qa + file);
                bool journeyOk = JourneyCatalog.Create(journey)?.Steps.Any(s => s.Id == step) ?? false;
                if (src == null || !src.Contains(test) || !segments.Contains(registered) || !journeyOk)
                {
                    bad.Add($"{test}（{file} / {journey} {step}）");
                }
            }
            Expect(bad.Count == 0, $"{map.Length} 项自动验收各有自检段且登记在全量自检、各有旅程步骤" + (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
        }

        // ── H. 远征准备面板的暴露行（FG-GAP-112） ────────────────────────────────────

        private const string PrepUxml = "Assets/GameRes/Raw/UI/Expedition/ExpeditionPrepPanel.uxml";

        private static void CheckExposureRow()
        {
            Line("  · H. FG-GAP-112：暴露与“关闭广播”开关在远征准备面板 Body 外——还没有出征目标（没有 ERC-003）、远征队在外时也能开关（FGJ-M6 exp_off1 / exp_on2 真实点击）");
            string uxml = ReadRepo("TEngine/UnityProject/" + PrepUxml) ?? string.Empty;
            string code = ReadRepo("TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/UI/Expedition/ExpeditionPrepPanelUIToolkit.cs") ?? string.Empty;
            var outside = new List<string>();
            var inside = new List<string>();
            try
            {
                System.Xml.Linq.XElement root = System.Xml.Linq.XElement.Parse(uxml);
                System.Xml.Linq.XElement body = root.Descendants().FirstOrDefault(e => (string)e.Attribute("name") == "Body");
                foreach (string n in new[] { "ExposureRow", "TowerBroadcastOffToggle", "CloseButton", "DepartButton" })
                {
                    bool exists = root.Descendants().Any(e => (string)e.Attribute("name") == n);
                    bool inBody = body != null && body.Descendants().Any(e => (string)e.Attribute("name") == n);
                    (exists && !inBody ? outside : inside).Add(n);
                }
            }
            catch (Exception e)
            {
                inside.Add("UXML 解析失败：" + e.Message);
            }
            int show = code.IndexOf("_exposureRow.style.display", StringComparison.Ordinal);
            int depart = code.IndexOf("_departButton.style.display", StringComparison.Ordinal);
            int early = code.IndexOf("if (!snapshot.RegionReachable)", StringComparison.Ordinal);
            int set = code.IndexOf("_towerBroadcastOffToggle.SetValueWithoutNotify(", StringComparison.Ordinal);
            Expect(inside.Count == 0 && show >= 0 && depart >= 0 && set >= 0 && show < early && set < early && depart < early,
                $"UXML 里暴露行、“关闭广播”勾选框、“关闭”“确认出发”都在 Body 外（{string.Join("、", outside)}）；面板刷新在“没有出征目标 / 远征在外”提前返回之前就显示暴露行、同步开关、按目标显隐“确认出发”" +
                (inside.Count == 0 ? string.Empty : "；还在 Body 里 / 缺：" + string.Join("、", inside)));            string gameRoot = ReadRepo("TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Stage/GameRoot.cs") ?? string.Empty;
            string autoPause = MethodBodyAny(StripComments(gameRoot), "TryAutoPauseWorld") ?? string.Empty;
            Expect(autoPause.Contains("GameClock.DirectLocked") && autoPause.IndexOf("GameClock.DirectLocked", StringComparison.Ordinal) < autoPause.IndexOf("SetWorldPaused(true)", StringComparison.Ordinal),
                "FG-GAP-114（源码守护；行为断言见 H4）：通知自动暂停的判定 TryAutoPauseWorld 在接入直控（GameClock.DirectLocked）时不暂停——直控锁 1x、暂停键只在战略视角生效");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过布局探针");
                return;
            }
            string probe = UiToolkitLayoutProbe.Probe(PrepUxml, "ExpeditionPrepPanelRoot");
            Expect(probe.StartsWith("PASS", StringComparison.Ordinal), "ExpeditionPrepPanel.uxml 布局探针（四种分辨率 + 超长文字压测 + USS 体检）：" + probe.Split('\n').FirstOrDefault()
                + (probe.StartsWith("PASS", StringComparison.Ordinal) ? string.Empty : "\n" + string.Join("\n", probe.Split('\n').Skip(1).Take(12))));
        }

        // ── B / B2 / F / G：要起世界的几段 ─────────────────────────────────────────

        private static void WorldChecks()
        {
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            bool hadCamera = Camera.main != null;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            GameLanguage originalLanguage = GameSettings.Language;
            string dir = Path.Combine(Path.GetTempPath(), "bingames-fgm6-selfcheck-" + Guid.NewGuid().ToString("N"));
            string originalCodex = MechanicCodex.FilePathOverrideForTests;
            try
            {
                ReloadContent();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Directory.CreateDirectory(dir);
                CampaignSaveService.SaveDirectoryOverrideForTests = dir;
                MechanicCodex.FilePathOverrideForTests = Path.Combine(dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => 0.05f;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程），" +
                     $"Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}；逐单位 / 逐弹体 / 流场在 AOT 内核（Main/Sim），导演 / 结算 / 对账在热更层（Editor 下 Mono JIT，真机另测 FG15-SYS-02）");
                Step(CheckPlannerSeeds);
                Step(CheckDefensePlanHolds);
                Step(CheckPerfScene);
                Step(CheckRaidDestroysFusingSynth);
                Step(CheckRaidKillsMachineBlackBox);
                Step(CheckShieldCap);
                Step(CheckCompletionPush);
                Step(CheckDirectNoAutoPause);
                Step(CheckTransitInvalidation);
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
                ResetServices();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
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
                ItemDistribution.ResetForTests();
                ProductionService.ResetForTests();
                BuildingOps.ResetForTests();
                BuildMaterials.ResetForTests();
                HomeInventory.ResetSessionState();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodex;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
                UiEscapeStack.Clear();
                UiConfirmDialog.DiscardAll();
                GameSettings.SetLanguage(originalLanguage);
                if (!hadCamera && Camera.main != null)
                {
                    Object.DestroyImmediate(Camera.main.gameObject);
                }
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
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
        }

        private static void ReloadContent()
        {
            ConfigSystem.Instance.Load();
            GameText.Reload();
            GridContent.Reload();
            WorldGenContent.Reload();
            FgContentTables.Reload();
            ItemCatalog.Reload();
            ProducerCatalog.Reload();
            BuildingOps.Reload();
            BuildMaterials.Reload();
            ResearchCatalog.Reload();
            FusionCatalog.Reload();
            IntelCatalog.Reload();
            RaidCatalog.Reload();
            SiegeCatalog.Reload();
            TurretCatalog.Reload();
            DefenseCatalog.Reload();
            GameClock.ReloadTuning();
        }

        private static void ResetServices()
        {
            RaidResultService.ResetSessionState();
            SiegeService.ResetSessionState();
            DefenseService.ResetSessionState();
            TurretService.ResetSessionState();
            RepairDroneService.ResetSessionState();
            IntelService.ResetSessionState();
            RaidDirectorService.ResetSessionState();
            HomeRaidAlertService.ResetSession();
            StandingRuleService.ResetForTests();
            ResearchService.ResetForTests();
            FusionService.ResetSessionState();
            BlackBoxService.ResetSessionState();
            PowerEnvironment.ResetForTests();
            HomeValleyPowerGrid.ResetForTests();
        }

        /// <summary>与主菜单“新建”同一入口（v2 世界：规划层、家园区侦察巢），开局受损的建筑修好、废料充足、标准难度。不写死坐标（B25）。</summary>
        private static CampaignState NewWorld(int seed, int scrap = 4000)
        {
            ResetServices();
            BuildingOps.ResetForTests();
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            GameClock.SetPaused(false);
            GameClock.SetSpeed(1f);
            MachineRegistry.ResetForNewCampaign();
            HomeGridService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            ProductionService.ResetForTests();
            UiEscapeStack.Clear();
            _seq = 6600;
            CampaignState s = CampaignState.CreateNew("fgm6-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(WorldGenVersions.Current, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            WorldView.Observe(home.SiteId);
            s.Scrap = scrap;
            foreach (BuildingRecord b in s.BuildingRecords)
            {
                if (b != null && b.ConstructionState == BuildingConstructionState.Damaged)
                {
                    b.ConstructionState = BuildingConstructionState.Operational;
                    b.Health = BuildingOps.MaxDurability(b.BuildingTypeId);
                }
            }
            PrimitiveInventory.EnsureSeeded(s);
            GameLogic.Campaign.Signal.SignalCoreService.EnsureInitialized(s);
            HomeValleyPowerGrid.Recompute(s);
            WorldTransitSystem.ResetCountersForTests();
            WorldSimulation.StepMany(2);
            return s;
        }

        /// <summary>测试捷径：直接登记一座建成、满耐久的建筑（真实放置 / 施工由 FG3 / FG6 各段与旅程覆盖），位置由规划给出。</summary>
        private static BuildingRecord Register(CampaignState s, string type, GridCell c, int rotation = 0, bool sync = true)
        {
            GameConfig.fg.BuildingGrid g = GridContent.Building(type);
            HomeValleyLayout.PowerProfile.TryGetValue(type, out (float PowerDemand, int PowerPriority) prof);
            var r = new BuildingRecord
            {
                BuildingId = HomeValleyLayout.RegionId + ":" + type + "#m6x" + (_seq++).ToString(CultureInfo.InvariantCulture),
                BuildingTypeId = type,
                RegionId = HomeValleyLayout.RegionId,
                GridX = c.X,
                GridY = c.Y,
                Rotation = rotation,
                Position = GridMath.FootprintCenter(c, g.FootprintW, g.FootprintH, rotation),
                Health = BuildingOps.MaxDurability(type),
                ConstructionState = BuildingConstructionState.Operational,
                PowerPriority = prof.PowerPriority == 0 ? 1 : prof.PowerPriority,
                PowerState = BuildingPowerState.NotApplicable,
                Inventory = Array.Empty<CargoEntry>(),
                QueueIds = Array.Empty<string>(),
            };
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Append(r).ToArray();
            HomeGridService.MapFor(s);
            if (sync)
            {
                HomeValleyPowerGrid.Recompute(s);
                DefenseService.Sync(s);
                TurretService.Sync(s);
            }
            return r;
        }

        private static CombatSite Site => WorldSimulation.Home != null && WorldSimulation.Home.IsLoaded ? WorldSimulation.Home.Combat : null;

        // ── B. 规划不依赖固定坐标 ───────────────────────────────────────────────────

        private static void CheckPlannerSeeds()
        {
            Line("  · B. B25：FGJ-M6 的家园防线规划在 5 个种子上都找得到、逐件过放置校验（只差研究的算过）、互不重叠；防线放下之后火墙按 4 个方向的抵达点都规划得出；FGJ-M6R 的完全围死规划在 2 个种子上找得到");
            var lines = new List<string>();
            var bad = new List<string>();
            var sw = Stopwatch.StartNew();
            foreach (int seed in new[] { 42, 1, 7, 9301, 2026 })
            {
                CampaignState s = F.NewWorld(seed, true, 300);
                FgjM6Common.HomePlan hp = FgjM6Common.PlanHome(s, out string why);
                if (hp == null)
                {
                    bad.Add($"种子 {seed}：{why}");
                    continue;
                }
                string err = VerifyHome(s, hp);
                if (err != null)
                {
                    bad.Add($"种子 {seed}：{err}");
                    continue;
                }
                ResearchService.CompleteForTests(s, FgjM6Common.NodeBarrier, FgjM6Common.NodeTrap);
                // 复审修复：火墙在家园防线（建筑、发电机 2、精炼塔 / 废液池、屏障圈）都放下之后再规划——与旅程里火墙那一刻的格局一致（没铺的带 / 管线按规划格避开）。
                PlaceHomePlan(s, hp);
                var fws = new List<string>();
                Vector2 cc = FgjM6Common.CoreCenter(s);
                float ar = (float)WorldTransitSystem.ArrivalRadius;
                var turrets = hp.Plan.Buildings.Where(b => b.Type == FgjM6Common.Turret).Select(b => (b.Key, b.Pivot)).ToList();
                var others = new List<GridCell> { hp.Refinery.AcidPipe, hp.Refinery.Pump };
                others.AddRange(hp.Refinery.Crude);
                var belts = hp.Plan.Routes.SelectMany(r => r.Path).ToList();
                foreach (Vector2 d in new[] { new Vector2(-1f, -0.4f), new Vector2(1f, 0.2f), new Vector2(0.1f, 1f), new Vector2(0.3f, -1f) })
                {
                    // 抵达点 = 从这个方向来、进入到达半径的那一点（敌人在那里展开攻城）。
                    Vector2 arrival = cc + d.normalized * ar;
                    FgjM6Common.FirewallPlan fw = FgjM6Common.PlanFirewallNear(s, arrival, turrets, hp.Refinery.Tank, hp.Refinery.FuelPipe, others, out string fwhy, belts);
                    if (fw == null)
                    {
                        bad.Add($"种子 {seed} 火墙（抵达点朝 {d}）：{fwhy}");
                        continue;
                    }
                    fws.Add($"{fw.TurretKey}+{fw.Pipe.Count}格/{fw.Cover:F1}米");
                }
                lines.Add($"{seed}：{hp.Plan.Buildings.Count} 座 + 发电机 2 ×{hp.Plan.Generators.Count}、屏障 {hp.WallRuns.Count} 段 {hp.WallCells} 格、精炼塔离油井泵 {Mathf.Abs(hp.Refinery.Tower.X - hp.Refinery.Pump.X) + Mathf.Abs(hp.Refinery.Tower.Y - hp.Refinery.Pump.Y)} 格；火墙（改装炮塔 + 管线 / 油膜带离抵达点）{string.Join("、", fws)}");
            }
            Expect(bad.Count == 0, $"FGJ-M6 家园防线与火墙规划 5 个种子都成（{string.Join("；", lines)}；{sw.ElapsedMilliseconds} ms）{(bad.Count == 0 ? string.Empty : "；失败：" + string.Join("；", bad))}");
            var bad2 = new List<string>();
            var lines2 = new List<string>();
            foreach (int seed in new[] { 1, 42 })
            {
                CampaignState s = F.NewWorld(seed, true, 300);
                ResearchService.CompleteForTests(s, FgjM6Common.NodeBarrier, FgjM6Common.NodeTrap);
                FgjM6Common.ReversePlan rp = FgjM6Common.PlanReverse(s, out string why);
                if (rp == null)
                {
                    bad2.Add($"种子 {seed}：{why}");
                    continue;
                }
                // 完全围死：圈上敌方能走的格要么在屏障段里、要么是闸门（规划自洽）。
                HomeGridService.TryGetCoreBounds(s, out GridCell a, out GridCell b);
                var walls = new HashSet<long>(rp.WallRuns.SelectMany(w => FgjM6Common.Line(w.A, w.B)).Select(FgjM4Common.Key)) { FgjM4Common.Key(rp.GateAt) };
                int holes = FgjM6Common.RingCells(a, b, rp.RingMargin).Count(c => NavService.PassableNow(c.X, c.Y, BinGames.Sim.Nav.NavConst.ClassHostile) && !walls.Contains(FgjM4Common.Key(c)));
                if (holes > 0)
                {
                    bad2.Add($"种子 {seed}：圈上还有 {holes} 格敌方能走");
                    continue;
                }
                lines2.Add($"{seed}：外框外 {rp.RingMargin} 格、{rp.WallCells} 格屏障 + 闸门、抽水线 {rp.Water.Count} 格");
            }
            Expect(bad2.Count == 0, $"FGJ-M6R 完全围死规划在 2 个种子上都成（{string.Join("；", lines2)}）{(bad2.Count == 0 ? string.Empty : "；失败：" + string.Join("；", bad2))}");
        }

        /// <summary>规划逐件复核：建筑过放置校验（不看造价；只差研究的算过），屏障格过放置校验，建筑占地 / 带 / 屏障 / 发电机互不重叠。</summary>
        private static string VerifyHome(CampaignState s, FgjM6Common.HomePlan hp)
        {
            var seen = new Dictionary<long, string>();
            bool Claim(GridCell c, string who, out string clash)
            {
                long k = FgjM4Common.Key(c);
                if (seen.TryGetValue(k, out string other))
                {
                    clash = $"{FgjM4Common.Cell(c)} 被 {other} 与 {who} 同时占用";
                    return false;
                }
                seen[k] = who;
                clash = null;
                return true;
            }
            bool PlaceOk(string type, GridCell pivot)
            {
                GridPlacementResult r = HomeGridService.ValidatePlacement(s, type, pivot, 0, checkCost: false);
                return r.Ok || r.Reasons.All(x => x.Code == GridBlockReason.Locked);
            }
            foreach (FgjM4Common.Placed b in hp.Plan.Buildings)
            {
                if (!PlaceOk(b.Type, b.Pivot))
                {
                    return $"{b.Label} {FgjM4Common.Cell(b.Pivot)} 放不下";
                }
                foreach (GridCell c in FgjM4Common.Footprint(b.Type, b.Pivot, 0))
                {
                    if (!Claim(c, b.Key, out string clash))
                    {
                        return clash;
                    }
                }
            }
            foreach (GridCell g in hp.Plan.Generators)
            {
                foreach (GridCell c in FgjM4Common.Footprint(HomeValleyLayout.BuildingTypeGenerator2, g, 0))
                {
                    if (!Claim(c, "发电机2", out string clash))
                    {
                        return clash;
                    }
                }
            }
            foreach (FgjM4Common.Route r in hp.Plan.Routes)
            {
                foreach (GridCell c in r.Path)
                {
                    if (!Claim(c, r.Key, out string clash))
                    {
                        return clash;
                    }
                }
            }
            foreach (GridCell c in FgjM4Common.Footprint(FgjM4Common.Tower, hp.Refinery.Tower, 0).Concat(FgjM4Common.Footprint(FgjM4Common.Pond, hp.Refinery.Pond, 0)).Concat(hp.Refinery.Crude))
            {
                if (!Claim(c, "精炼链", out string clash))
                {
                    return clash;
                }
            }
            foreach ((GridCell a, GridCell b) in hp.WallRuns)
            {
                foreach (GridCell c in FgjM6Common.Line(a, b))
                {
                    if (!PlaceOk(FgjM6Common.Wall, c))
                    {
                        return $"屏障 {FgjM4Common.Cell(c)} 放不下";
                    }
                    if (!Claim(c, "屏障", out string clash))
                    {
                        return clash;
                    }
                }
            }
            return null;
        }

        // ── B2. 防线规划守得住 ─────────────────────────────────────────────────────

        private sealed class RaidRun
        {
            public readonly List<RaidPlanRecord> Plans = new List<RaidPlanRecord>();
            public readonly List<RaidResultRecord> Results = new List<RaidResultRecord>();
            public bool CoreLost;
            public float CoreMin = float.MaxValue;
            public string Toggles = string.Empty;
            public string Firewall;
        }

        /// <summary>
        /// 按 FGJ-M6 的规划把家园防线直接登记成建成的（测试捷径：放置 / 施工由旅程从正式入口走），按旅程的广播开关节奏推进到第 7.4 日：
        /// 第一次突袭排定后关广播 → 第一波预警时开 → 第二波排定后关 → 第二波预警时开 → 第三波排定后关。返回三波的计划与结算。
        /// </summary>
        private static RaidRun RunDefensePlan(int seed, out string why)
        {
            why = null;
            CampaignState s = NewWorld(seed);
            ResearchService.CompleteForTests(s, FgjM6Common.NodeRefinery, FgjM6Common.NodeBarrier, FgjM6Common.NodeShield, FgjM6Common.NodeTrap);
            FgjM6Common.HomePlan hp = FgjM6Common.PlanHome(s, out why);
            if (hp == null)
            {
                return null;
            }
            PlaceHomePlan(s, hp);
            var run = new RaidRun();
            var turrets = hp.Plan.Buildings.Where(b => b.Type == FgjM6Common.Turret).Select(b => (b.Key, b.Pivot)).ToList();
            var others = new List<GridCell> { hp.Refinery.AcidPipe, hp.Refinery.Pump };
            others.AddRange(hp.Refinery.Crude);
            var belts = hp.Plan.Routes.SelectMany(r => r.Path).ToList();
            BuildingRecord core = s.BuildingRecords.First(b => b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
            long end = RaidDirectorService.DayTicks(7.4);
            int phase = 0;
            string secondId = null;
            var seenPlans = new HashSet<string>();
            var seenResults = new HashSet<int>();
            while (GameClock.Ticks < end)
            {
                WorldSimulation.StepMany(300);
                float e = s.SignalExposure;
                RaidDirectorState d = RaidDirectorService.StateOf(s);
                List<RaidPlanRecord> live = FgjM6Common.LivePlans(s);
                RaidPlanRecord first = live.FirstOrDefault(p => p.FirstRaid);
                RaidPlanRecord second = secondId != null ? RaidDirectorService.FindPlan(s, secondId)
                    : live.Where(p => !p.FirstRaid && p.Trigger == RaidCatalog.TriggerExposure).OrderBy(p => p.Serial).FirstOrDefault();
                RaidPlanRecord third = secondId == null ? null : live.FirstOrDefault(p => p.PlanId != secondId && !p.FirstRaid && p.Trigger == RaidCatalog.TriggerExposure);
                if (phase == 0 && first != null && first.State >= RaidDirectorService.StateScheduled && e >= 35f)
                {
                    CampaignExposureLedger.SetTowerBroadcastOff(s, true);
                    phase = 1;
                    run.Toggles += $"关@{GameClock.Ticks} ";
                }
                else if (phase == 1 && (first == null || first.State >= RaidDirectorService.StateWarned))
                {
                    if (first != null && run.Firewall == null)
                    {
                        // 复审修复：火墙按第一次突袭的真实抵达点规划（与旅程 fw_plan 同一个规划入口）。
                        var at = new Vector2((float)first.ArriveX, (float)first.ArriveY);
                        FgjM6Common.FirewallPlan fw = FgjM6Common.PlanFirewallNear(s, at, turrets, hp.Refinery.Tank, hp.Refinery.FuelPipe, others, out string fwhy, belts);
                        run.Firewall = fw != null ? $"抵达点 {at} → 改装 {fw.TurretKey}、管线 {fw.Pipe.Count} 格、油膜带离抵达点 {fw.Cover:F1} 米" : "✗ " + fwhy;
                    }
                    CampaignExposureLedger.SetTowerBroadcastOff(s, false);
                    phase = 2;
                    run.Toggles += $"开@{GameClock.Ticks} ";
                }
                else if (phase == 2 && second != null && second.State >= RaidDirectorService.StateScheduled)
                {
                    CampaignExposureLedger.SetTowerBroadcastOff(s, true);
                    secondId = second.PlanId;
                    phase = 3;
                    run.Toggles += $"关@{GameClock.Ticks} ";
                }
                else if (phase == 3 && (second == null || second.State >= RaidDirectorService.StateWarned))
                {
                    CampaignExposureLedger.SetTowerBroadcastOff(s, false);
                    phase = 4;
                    run.Toggles += $"开@{GameClock.Ticks} ";
                }
                else if (phase == 4 && third != null && third.State >= RaidDirectorService.StateScheduled)
                {
                    CampaignExposureLedger.SetTowerBroadcastOff(s, true); // FGJ-M6 exp_off3：第三波排定后关，不让它在预警前并成 2 级
                    phase = 5;
                    run.Toggles += $"关@{GameClock.Ticks} ";
                }
                run.CoreMin = Mathf.Min(run.CoreMin, core.Health);
                foreach (RaidPlanRecord p in RaidDirectorService.Plans(s))
                {
                    if (p != null && p.State >= RaidDirectorService.StateScheduled && seenPlans.Add(p.PlanId))
                    {
                        run.Plans.Add(p);
                    }
                }
                foreach (RaidResultRecord r in RaidResultService.All(s))
                {
                    if (r != null && r.EndTick >= 0 && seenResults.Add(r.Serial))
                    {
                        run.Results.Add(r);
                    }
                }
                if (RaidResultService.IsCoreLost(s))
                {
                    run.CoreLost = true;
                    break;
                }
            }
            return run;
        }

        /// <summary>测试捷径：把 FGJ-M6 的家园规划直接登记成建成的（建筑、发电机 2、精炼塔 / 废液池、屏障圈；真实放置 / 施工由旅程走正式入口）。</summary>
        private static void PlaceHomePlan(CampaignState s, FgjM6Common.HomePlan hp)
        {
            foreach (GridCell g in hp.Plan.Generators)
            {
                Register(s, HomeValleyLayout.BuildingTypeGenerator2, g, sync: false);
            }
            foreach (FgjM4Common.Placed b in hp.Plan.Buildings)
            {
                Register(s, b.Type, b.Pivot, sync: false);
            }
            Register(s, FgjM4Common.Tower, hp.Refinery.Tower, sync: false);
            Register(s, FgjM4Common.Pond, hp.Refinery.Pond, sync: false);
            foreach ((GridCell a, GridCell b) in hp.WallRuns)
            {
                foreach (GridCell c in FgjM6Common.Line(a, b))
                {
                    Register(s, FgjM6Common.Wall, c, sync: false);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            DefenseService.Sync(s);
            TurretService.Sync(s);
            NavService.SyncGridChanges();
            WorldSimulation.StepMany(2);
        }
        private static void CheckDefensePlanHolds()
        {
            Line("  · B2. FGJ-M6 的防线规划守得住（真实导演 + 真实攻城 + 真实炮塔 / 护盾 / 屏障；种子 42 与 1，按旅程的广播开关节奏推进到第 7.4 日）");
            foreach (int seed in new[] { 42, 1 })
            {
                var sw = Stopwatch.StartNew();
                RaidRun run = RunDefensePlan(seed, out string why);
                if (run == null)
                {
                    Fail($"种子 {seed}：规划不出来：{why}");
                    continue;
                }
                RaidPlanRecord p1 = run.Plans.FirstOrDefault(p => p.FirstRaid);
                List<RaidResultRecord> results = run.Results.OrderBy(r => r.EndTick).ToList();
                int losses = results.Skip(1).Sum(r => r.LostBuildings + r.LostTurrets + r.LostDefenses);
                bool first = p1 != null && p1.Level == 1 && p1.Trigger == RaidCatalog.TriggerExposure && p1.ArrivalTick >= RaidDirectorService.GraceEndTick;
                bool three = results.Count >= 3 && results.Take(3).All(r => r.Outcome != "core_lost") && results.Take(3).Max(r => r.Level) <= 1;
                bool fwOk = run.Firewall != null && !run.Firewall.StartsWith("✗", StringComparison.Ordinal);
                Expect(!run.CoreLost && first && three && (losses > 0 || seed != FgjM6Journey.TestSeed) && fwOk,
                    $"种子 {seed}：三波都守住、核心不丢（核心耐久最低 {run.CoreMin:F0}）；首袭 {p1?.Level} 级 {p1?.Trigger}、抵达第 {p1?.ArrivalTick} 步（宽限终点 {RaidDirectorService.GraceEndTick}）；" +
                    $"结算 [{string.Join("；", results.Select(r => $"{r.PlanId} {r.Level} 级 {RaidResultService.OutcomeText(r)} 击毁 {r.Killed}/{r.Unfolded} 损失 {RaidResultService.LossTotal(r)}"))}]；" +
                    $"第二 / 三波建筑损失 {losses} 座（旅程种子 {FgjM6Journey.TestSeed} 上要 > 0，给自动重建留活）；广播开关 {run.Toggles}；火墙按第一次突袭的真实抵达点规划：{run.Firewall ?? "没算（第一次突袭没预警）"}（{sw.Elapsed.TotalSeconds:F0} 秒）");
            }
        }

        // ── F. FGT-DEF-010 性能场景 ────────────────────────────────────────────────

        private static void CheckPerfScene()
        {
            Line("  · F. FGT-DEF-010 性能场景（FG06 第 7 节：200 敌人、80 炮塔、流场增量更新 ≤ 4 ms；120 帧口径）：真实炮塔 + 真实屏障 + 真实攻城单位同时在场");
            CampaignState s = NewWorld(6610, scrap: 9000);
            ResearchService.CompleteForTests(s, FgjM6Common.NodeBarrier, FgjM6Common.NodeShield, FgjM6Common.NodeTrap);
            CombatSite site = Site;
            int want = (int)CombatSite.Tuning("combat.perf.turrets", 80);
            int enemies = SiegeCatalog.PerfUnits;
            GridCell core = HomeGridService.CorePivot(s);
            HomeGridService.TryGetCoreBounds(s, out GridCell cmin, out GridCell cmax);
            // 屏障圈：核心外框外 6 格那一圈能放的格（与 FGJ-M6 同一口径），T2。
            var walls = new List<BuildingRecord>();
            foreach (GridCell c in FgjM6Common.RingCells(cmin, cmax, 6))
            {
                if (NavService.PassableNow(c.X, c.Y, BinGames.Sim.Nav.NavConst.ClassHostile) && HomeGridService.ValidatePlacement(s, FgjM6Common.Wall, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                {
                    walls.Add(Register(s, FgjM6Common.Wall, c, sync: false));
                }
            }
            // 80 座炮塔：圈外每 3 格一座（按离核心由近到远），测试捷径让它们都吃到电（电网分配由电网自检覆盖）。
            var cells = new List<GridCell>();
            for (int dy = -16; dy <= 16; dy++)
            {
                for (int dx = -16; dx <= 16; dx++)
                {
                    cells.Add(new GridCell(core.X + dx * 3, core.Y + dy * 3));
                }
            }
            cells.Sort((p, q) => ((p.X - core.X) * (p.X - core.X) + (p.Y - core.Y) * (p.Y - core.Y)).CompareTo((q.X - core.X) * (q.X - core.X) + (q.Y - core.Y) * (q.Y - core.Y)));
            var turrets = new List<BuildingRecord>(want);
            foreach (GridCell cell in cells)
            {
                if (turrets.Count >= want)
                {
                    break;
                }
                if (Math.Max(Math.Abs(cell.X - core.X), Math.Abs(cell.Y - core.Y)) <= 9)
                {
                    continue;
                }
                if (HomeGridService.ValidatePlacement(s, FgjM6Common.Turret, cell, 0, asPlayerPlacement: false, checkCost: false).Ok)
                {
                    turrets.Add(Register(s, FgjM6Common.Turret, cell, sync: false));
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            foreach (BuildingRecord b in turrets)
            {
                b.PowerState = BuildingPowerState.Powered;
            }
            DefenseService.Sync(s);
            TurretService.Sync(s);
            NavService.SyncGridChanges();
            WorldSimulation.StepMany(2);
            // 200 台攻城单位分四个方向到达（编成：突击 60% / 攻城 30% / 维修 10%，与 FgSiegeSelfCheck P1 同一口径）。
            var sw = Stopwatch.StartNew();
            for (int g = 0; g < 4; g++)
            {
                int n = g == 3 ? enemies - enemies / 4 * 3 : enemies / 4;
                Vector2 at = FgSiegeSelfCheck.OutsidePoint(s, 40f, g * 4 + 1);
                FgSiegeSelfCheck.Arrive(s, at, "foundry", new[] { "foundry.strider", "foundry.armorbot", "foundry.repairbot" }, new[] { n * 6 / 10, n * 3 / 10, n - n * 6 / 10 - n * 3 / 10 });
            }
            double unfoldMs = sw.Elapsed.TotalMilliseconds;
            F.Seconds(3f);
            var step = new List<double>();
            var kernel = new List<double>();
            int peakProj = 0;
            int steps = GameClock.StepHz * 15;
            int aliveStart = site.SiegeUnitCount;
            int turretsInKernel = site.TurretUnitCount;
            for (int i = 0; i < steps; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                WorldSimulation.StepMany(1);
                step.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                kernel.Add(site.LastKernelMs);
                peakProj = Math.Max(peakProj, site.Kernel.ProjectileCount);
            }
            // 屏障变化（被拆 / 新建）时流场增量更新：拆掉 6 段墙、再补上 6 段，各走几步。
            double changeBefore = site.MaxSiegeChangeMs;
            foreach (BuildingRecord w in walls.Take(6).ToList())
            {
                BuildingOps.ApplyDamage(s, w.BuildingId, BuildingOps.MaxDurability(w.BuildingTypeId) + 1f);
            }
            F.Seconds(1f);
            double change = site.MaxSiegeChangeMs;
            step.Sort();
            double avg = step.Average();
            double p95 = step[(int)(step.Count * 0.95)];
            double kAvg = kernel.Average();
            double frame3x = avg * 1.5;
            PerfLines.Add($"FGT-DEF-010 合场：真实炮塔 {turrets.Count} 座（内核 {turretsInKernel}）+ 屏障 {walls.Count} 格 + 攻城单位 {aliveStart} 台（展开 {unfoldMs:F0} ms）：世界一步 平均 {avg:F3} / p95 {p95:F3} ms，" +
                          $"内核单步平均 {kAvg:F3} ms；弹体峰值 {peakProj}；流场增量更新最长 {change:F3} ms（拆墙前 {changeBefore:F3}）；3x 下每帧 1.5 步 ≈ {frame3x:F2} ms（120 帧预算 8.33 ms）；Editor batchmode");
            PerfGate.Expect(turrets.Count >= want * 9 / 10 && turretsInKernel >= turrets.Count * 9 / 10 && aliveStart >= enemies * 8 / 10 && peakProj > 0,
                $"F FGT-DEF-010：{turrets.Count} 座真实炮塔 + {aliveStart} 台真实攻城单位 + {walls.Count} 格屏障同时在场（第 7 节 80 / 200），弹体峰值 {peakProj}" +
                "（1,500 弹体的内核容量由 FgCombatKernelSelfCheck E 与性能基线覆盖）；世界一步平均 ×1.5 ≤ 8.33 ms（3x 下保 120 帧）、流场增量更新 ≤ 4 ms",
                new[]
                {
                    PerfGate.Le(frame3x, 8.33, "FGT-DEF-010 3x 每帧模拟 ms（120 帧）"),
                    PerfGate.Le(change, SiegeCatalog.PerfFieldUpdateMs, "FGT-DEF-010 流场增量更新最长 ms"),
                }, Expect, Line);
        }

        // ── G1. 真实攻城打坏正在熔合的合成台 ──────────────────────────────────────────

        private const string Burn = "fw_burntrail";
        private const string Oil = "fw_oilleak";
        private const string Substrate = "chip_substrate";

        private static void CheckRaidDestroysFusingSynth()
        {
            Line("  · G1. 承接 DEBT-FG5E2E01-05：真实攻城单位打坏正在熔合的电路合成台 → 熔合回滚、材料守恒");
            CampaignState s = NewWorld(6620);
            GridCell core = HomeGridService.CorePivot(s);
            // 合成台放在离核心约 20 格、核心配电范围里的空地，攻城单位就在它旁边展开。
            GridCell? at = F.FindFree(s, FusionCatalog.TypeId, 19f, 23f);
            if (!at.HasValue)
            {
                Fail("G1 测试准备：核心 19～23 格内放不下电路合成台");
                return;
            }
            BuildingRecord synth = Register(s, FusionCatalog.TypeId, at.Value);
            // 核心用一圈屏障围住（外框外 3 格）：突击 / 攻城职能够不着核心，按偏好退到“其它建筑”——离抵达点最近的就是合成台（FGR-DEF-030 偏好 + 距离）。
            HomeGridService.TryGetCoreBounds(s, out GridCell gmin, out GridCell gmax);
            int ringHoles = -1, ringMargin = 0;
            for (int m = 3; m <= 6 && ringHoles != 0; m++)
            {
                foreach (GridCell c in FgjM6Common.RingCells(gmin, gmax, m))
                {
                    if (NavService.PassableNow(c.X, c.Y, BinGames.Sim.Nav.NavConst.ClassHostile) && HomeGridService.ValidatePlacement(s, FgjM6Common.Wall, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        Register(s, FgjM6Common.Wall, c, sync: false);
                    }
                }
                DefenseService.Sync(s);
                NavService.SyncGridChanges();
                WorldSimulation.StepMany(2);
                ringMargin = m;
                ringHoles = FgjM6Common.RingCells(gmin, gmax, m).Count(c => NavService.PassableNow(c.X, c.Y, BinGames.Sim.Nav.NavConst.ClassHostile));
            }
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(new[] { Burn, Oil }).Distinct().ToArray();
            PrimitiveInventory.GrantCrafted(s, Burn);
            PrimitiveInventory.GrantCrafted(s, Oil);
            s.TechData = 200;
            HomeInventory.Add(s, Substrate, 6);
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
            WorldSimulation.StepMany(2);
            FusionService.Simulate(s, synth.BuildingId, Burn, Oil);
            var t0 = FusionTotals(s);
            FusionOpResult job = FusionService.EnqueueFormal(s, synth.BuildingId, Burn, Oil);
            F.Seconds(1f);
            Vector2 cc = FgjM6Common.CoreCenter(s);
            Vector2 out1 = synth.Position + (synth.Position - cc).normalized * 4f;
            GridCell gc = SiegeService.NearestPassable(NavService.CellOf(out1.x, out1.y), 6);
            int notes0 = NotificationCenter.History.Count(n => n.Type?.Id == "fusion_rolled_back");
            TransitGroupRecord g = FgSiegeSelfCheck.Arrive(s, new Vector2(gc.X, gc.Y), "foundry", new[] { "foundry.strider" }, new[] { 8 });
            bool destroyed = F.StepUntil(() =>
            {
                BuildingRecord b = FusionService.FindSynth(s, synth.BuildingId);
                return b == null || b.ConstructionState != BuildingConstructionState.Operational;
            }, 90);
            F.Seconds(1f);
            FusionJobRecord jr = job.Success ? FusionService.FindJob(s, job.JobId) : null;
            var t1 = FusionTotals(s);
            RaidResultRecord res = RaidResultService.All(s).LastOrDefault(r => r != null && r.GroupId == g.GroupId);
            bool lossRecorded = res != null && res.Losses.Any(x => x.Id == synth.BuildingId);
            bool rolled = jr != null && jr.State == FusionJobState.RolledBack && jr.Reason == "destroyed";
            bool notified = NotificationCenter.History.Count(n => n.Type?.Id == "fusion_rolled_back") > notes0;
            Expect(job.Success && destroyed && rolled && t1 == t0 && notified && lossRecorded,
                $"G1 核心用屏障围死（外框外 {ringMargin} 格，圈上敌方能走的格 {ringHoles}），8 台铸造步行机（突击职能，正式展开 / 按职能选目标，够不着核心就打最近的其它建筑）拆掉了正在熔合的合成台（{destroyed}，突袭结算记为损失 {lossRecorded}）：熔合事务回滚（{jr?.State} / {jr?.Reason}），" +
                $"父固件 / 芯片基板 / 技术数据守恒 {t0} → {t1}，通知“熔合中止” {notified}");
        }

        private static (int Burn, int Oil, int Sub, int Tech) FusionTotals(CampaignState s)
        {
            ItemDistribution.Invalidate();
            int sub = (int)ItemDistribution.Get(s, ItemCatalog.Find(Substrate)).Total;
            ItemDistribution.Invalidate();
            int tech = (int)ItemDistribution.Get(s, ItemCatalog.Find(ItemCatalog.TechDataId)).Total;
            int burn = (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Count(c => c != null && c.CardDefId == Burn);
            int oil = (s.PrimitiveChips ?? Array.Empty<PrimitiveChipRecord>()).Count(c => c != null && c.CardDefId == Oil);
            return (burn, oil, sub, tech);
        }

        // ── G2. 真实攻城打死家园机器 → 黑匣子 → 研究 ───────────────────────────────────

        private static void CheckRaidKillsMachineBlackBox()
        {
            Line("  · G2. 承接 DEBT-FG5RND06-03：真实攻城单位打死家园机器 → 黑匣子直接回收 → 陈列馆分析 → 技术数据入账 → 实验室转成研究点");
            CampaignState s = NewWorld(6630);
            GridCell? gal = F.FindFree(s, BlackBoxService.TypeId, 7f, 20f);
            GridCell? lab = F.FindFree(s, ResearchCatalog.LabTypeId, 7f, 20f);
            if (!gal.HasValue || !lab.HasValue)
            {
                Fail("G2 测试准备：放不下陈列馆 / 实验室");
                return;
            }
            Register(s, BlackBoxService.TypeId, gal.Value);
            BuildingRecord labRec = Register(s, ResearchCatalog.LabTypeId, lab.Value);
            GridCell? gen = F.FindFree(s, HomeValleyLayout.BuildingTypeGenerator2, 6f, 20f);
            if (gen.HasValue)
            {
                Register(s, HomeValleyLayout.BuildingTypeGenerator2, gen.Value); // 陈列馆 + 实验室要电：补一台发电机 2（开局电网带不动）
            }
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
            // 研究队列里排一个开局就能研究的节点，实验室把技术数据转成研究点（技术数据先清空：之后的点数只能来自黑匣子）。
            s.TechData = 0;
            bool queued = ResearchService.TryEnqueue(s, FgjM6Common.NodeBarrier, out string qwhy);
            Vector2 at = FgSiegeSelfCheck.OutsidePoint(s, 30f, 3);
            MachineOpResult m = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId, at + new Vector2(1.5f, 0f), 60f, 60f, "Player", 1);
            if (!m.Success)
            {
                Fail("G2 测试准备：放不下一台家园机器");
                return;
            }
            MachineLoadoutRegistry.Register(s, m.LogicId, HomeValleyLayout.BlueprintErc003Id, 1);
            WorldSimulation.StepMany(2);
            TransitGroupRecord g = FgSiegeSelfCheck.Arrive(s, at, "foundry", new[] { "foundry.strider" }, new[] { 10 });
            bool dead = F.StepUntil(() => !(MachineRegistry.TryGetRecord(m.LogicId, out MachineRecord r) && r.IsAlive), 90);
            BlackBoxRecord box = BlackBoxService.Find(s, m.LogicId);
            RaidResultRecord res = RaidResultService.All(s).LastOrDefault(r => r != null && r.GroupId == g.GroupId);
            bool loss = res != null && res.Losses.Any(x => x.Kind == "machine" && x.Id == m.LogicId.ToString(CultureInfo.InvariantCulture));
            // 清场（剩下的攻城单位撤走），然后走满一个游戏日让陈列馆分析完这个黑匣子。
            Site?.DespawnSiegeGroup(SiegeService.KeyOf(g));
            long points0 = s.Research?.PointsProduced ?? 0;
            long tech0 = s.TechData;
            F.StepUntil(() => BlackBoxService.Find(s, m.LogicId) is BlackBoxRecord bx && bx.Done, (int)(BlackBoxService.DaysPerBox * GameClock.DaySeconds) + 120);
            BlackBoxRecord done = BlackBoxService.Find(s, m.LogicId);
            // 黑匣子入账之后实验室才有技术数据可用（之前清零了）：等它转出研究点（T1 每游戏分钟 2 技术数据 → 1 研究点）。
            F.StepUntil(() => (s.Research?.PointsProduced ?? 0) > points0, 600);
            long gained = TechDataFlow.IncomeOf(s, TechDataFlow.BlackBox);
            long points1 = s.Research?.PointsProduced ?? 0;
            Expect(dead && box != null && loss && done != null && done.Done && done.PointsGranted == BlackBoxService.PointsPerBox && gained >= BlackBoxService.PointsPerBox && points1 > points0,
                $"G2 攻城单位打死家园机器 {m.LogicId}（{dead}，突袭结算记为机器损失 {loss}）：黑匣子直接回收（{box != null}）→ 陈列馆分析完（{done?.Done}，入账 {done?.PointsGranted} 点）→ " +
                $"技术数据收支“黑匣子”收入 {gained}、库存 {tech0} → {s.TechData} → 实验室转成研究点 {points0} → {points1}（{labRec.BuildingId}，{labRec.PowerState}；研究队列 {queued} {qwhy}）");
        }

        // ── H2. 护盾数量上限（DEBT-FG6DEF02-05）────────────────────────────────────────

        private static void CheckShieldCap()
        {
            int cap = BinGames.Sim.Combat.CombatConst.MaxShields;
            Line($"  · H2. 承接 DEBT-FG6DEF02-05：一个地点至多 {cap} 座护盾在工作；第 {cap + 1} 座不工作、状态行写明原因；拆掉一座后它自动补上");
            CampaignState s = NewWorld(6640, scrap: 9000);
            ResearchService.CompleteForTests(s, FgjM6Common.NodeBarrier, FgjM6Common.NodeShield);
            GridCell core = HomeGridService.CorePivot(s);
            var shields = new List<BuildingRecord>();
            for (int r = 8; r <= 60 && shields.Count < cap + 1; r += 3)
            {
                for (int dy = -r; dy <= r && shields.Count < cap + 1; dy += 3)
                {
                    for (int dx = -r; dx <= r && shields.Count < cap + 1; dx += 3)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var c = new GridCell(core.X + dx, core.Y + dy);
                        if (HomeGridService.ValidatePlacement(s, DefenseCatalog.ShieldTypeId, c, 0, asPlayerPlacement: false, checkCost: false).Ok)
                        {
                            shields.Add(Register(s, DefenseCatalog.ShieldTypeId, c, sync: false));
                        }
                    }
                }
            }
            if (shields.Count < cap + 1)
            {
                Fail($"H2 测试准备：只放下 {shields.Count} 座护盾（要 {cap + 1}）");
                return;
            }
            HomeValleyPowerGrid.Recompute(s);
            foreach (BuildingRecord b in shields)
            {
                b.PowerState = BuildingPowerState.Powered; // 测试捷径：都吃到电（电网分配由电网自检覆盖），只看内核护盾名额
            }
            DefenseService.Sync(s);
            F.Seconds(1f);
            List<BuildingRecord> capped = shields.Where(b => DefenseService.TryGetShieldReadout(s, b.BuildingId, out ShieldReadout ro) && ro.Capped).ToList();
            BuildingRecord last = capped.FirstOrDefault();
            string reason = last != null ? DefenseService.StatusOf(s, last).Reason ?? string.Empty : string.Empty; // 防御这一行（电够时状态行就是这一句；这里 65 座护盾远超测试电网，缺电那一行排在前面）
            string want = GameText.Format("bs.reason.shield_cap", cap);
            bool oneCapped = capped.Count == 1 && reason == want;
            // 拆掉一座在工作的：多出来的那座在下一步自动登记上、不再写“已达上限”。
            BuildingRecord working = shields.First(b => !capped.Contains(b));
            BuildingOps.ApplyDamage(s, working.BuildingId, BuildingOps.MaxDurability(working.BuildingTypeId) + 1f);
            F.Seconds(1f);
            bool refilled = last != null && DefenseService.TryGetShieldReadout(s, last.BuildingId, out ShieldReadout after) && !after.Capped;
            Expect(oneCapped && refilled,
                $"H2 {shields.Count} 座护盾（都有电）：{shields.Count - capped.Count} 座在工作、{capped.Count} 座不工作，状态行“{reason}”（应“{want}”）；拆掉一座在工作的之后它补上：{refilled}");
        }

        // ── H3. 完工让位（FG-GAP-113）──────────────────────────────────────────────

        private static void CheckCompletionPush()
        {
            Line("  · H3. FG-GAP-113：屏障完工那一刻，被挤进施工格的家园机器挪到最近的空格（FGJ-M6R 抓到：机器被关在建好的墙里，之后工单一直“路径卡住”）；闸门放行己方，不挪");
            CampaignState s = NewWorld(6650);
            ResearchService.CompleteForTests(s, FgjM6Common.NodeBarrier);
            CombatSite site = Site;
            MachineRecord worker = MachineRegistry.AllRecords.FirstOrDefault(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId);
            if (site == null || worker == null || !site.TryGetMachineMarker(worker.LogicId, out HomeValleyMachineMarker marker) || !site.TryGetMachinePosition(worker.LogicId, out Vector2 here))
            {
                Fail("H3 测试准备：家园没有可用的机器");
                return;
            }
            string Try(string type, out bool pushed, out string where)
            {
                pushed = false;
                where = string.Empty;
                GridCell? cell = F.FindFree(s, type, 3f, 9f);
                if (!cell.HasValue)
                {
                    return "放不下";
                }
                GridOpResult r = HomeGridService.TryPlace(s, type, cell.Value, 0);
                BuildingRecord b = r.Success ? HomeGridService.FindBuilding(s, r.BuildingId) : null;
                if (b == null)
                {
                    return "放置失败";
                }
                marker.SetPosition(new Vector2(cell.Value.X, cell.Value.Y)); // 测试捷径：机器被挤进施工格（真实情形是互相避让 / 刚好停在那里）
                WorldSimulation.StepMany(1);
                b.ConstructionState = BuildingConstructionState.Operational; // 完工那一刻（两条正式完工路径都调 OnConstructionCompleted，见 HomeValleyWorkOrders）
                DefenseService.OnConstructionCompleted(s, b);
                WorldSimulation.StepMany(2);
                site.TryGetMachinePosition(worker.LogicId, out Vector2 after);
                GridCell now = GridCell.FromWorld(after);
                pushed = now != cell.Value;
                where = $"{type} {cell.Value.X},{cell.Value.Y} → 机器在 {now.X},{now.Y}（挪走 {HomeGridService.LastMachinesPushed} 台）";
                return null;
            }
            string e1 = Try(DefenseCatalog.BarrierT2, out bool wallPushed, out string w1);
            string e2 = Try(DefenseCatalog.GateTypeId, out bool gatePushed, out string w2);
            Expect(e1 == null && e2 == null && wallPushed && !gatePushed,
                $"H3 屏障完工：{w1 ?? e1}（应挪走）；闸门完工：{w2 ?? e2}（放行己方，不挪）" + (string.IsNullOrEmpty(e1 + e2) ? string.Empty : $"；准备失败：{e1}{e2}"));
            Expect(ReadRepo("TEngine/UnityProject/Assets/GameScripts/HotFix/GameLogic/Campaign/Regions/HomeValleyWorkOrders.cs") is string wo && Count(StripComments(wo), "DefenseService.OnConstructionCompleted(state, building)") >= 2,
                "两条正式完工路径（新建完工、废墟重建完工）都经过 DefenseService.OnConstructionCompleted（完工让位挂在这里）");
        }

        // ── H4. 直控时不自动暂停（FG-GAP-114，行为断言）──────────────────────────────────

        private static void CheckDirectNoAutoPause()
        {
            Line("  · H4. FG-GAP-114（行为）：接入直控（GameClock.DirectLocked）时，要自动暂停的通知不暂停世界、照常进历史，世界照常推进；回到战略视角后同一类通知照常自动暂停");
            NewWorld(6660);
            NotifyTypeDef def = NotificationCatalog.AllTypes.FirstOrDefault(d => d != null && d.KeepInHistory && !NotificationCenter.HasDynamicAutoPauseDefault(d.Id) && NotificationCenter.IsAutoPauseEnabled(d));
            if (def == null)
            {
                Fail("H4 测试准备：通知类型表里没有默认自动暂停、进历史的类型");
                return;
            }
            Func<bool> keep = NotificationCenter.AutoPauseHandler;
            int Members() => NotificationCenter.History.Where(e => e != null && e.Type?.Id == def.Id).Sum(e => e.Count);
            try
            {
                // 正式流程里 Play 的处理器是 GameRoot.AutoPauseForNotification = Application.isPlaying && TryAutoPauseWorld()；编辑模式下直接挂同一个判定。
                NotificationCenter.AutoPauseHandler = GameRoot.TryAutoPauseWorld;
                GameClock.SetPaused(false);
                GameClock.SetDirectLocked(true); // 与 WorldView 在镜头进入接入视角时设的同一个标记
                int m0 = Members();
                int p0 = NotificationCenter.AutoPauseCount;
                NotificationCenter.Post(def.Id, "FG-GAP-114 自检：接入直控中", Vector3.zero);
                bool directRunning = !GameRoot.IsWorldPaused && !GameClock.Paused && NotificationCenter.AutoPauseCount == p0 && Members() == m0 + 1;
                long t1 = GameClock.Ticks;
                WorldSimulation.Frame(0.1f);
                bool moving = GameClock.Ticks > t1;
                GameClock.SetDirectLocked(false);
                NotificationCenter.Post(def.Id, "FG-GAP-114 自检：战略视角", Vector3.zero);
                bool strategyPaused = GameRoot.IsWorldPaused && NotificationCenter.AutoPauseCount == p0 + 1 && Members() == m0 + 2;
                long t2 = GameClock.Ticks;
                WorldSimulation.Frame(0.1f);
                bool frozen = GameClock.Ticks == t2;
                Expect(directRunning && moving && strategyPaused && frozen,
                    $"H4 通知“{def.Id}”（默认自动暂停）：接入直控时发出 → 世界没暂停（{directRunning}）、进了历史、之后一帧世界照常推进 {GameClock.Ticks - t1 > 0}（{t1} → {t2}）；" +
                    $"回到战略视角再发一次 → 自动暂停（{strategyPaused}，自动暂停计数 {p0} → {NotificationCenter.AutoPauseCount}）、之后一帧不推进（{frozen}）");
            }
            finally
            {
                NotificationCenter.AutoPauseHandler = keep;
                GameClock.SetDirectLocked(false);
                GameClock.SetPaused(false);
            }
        }

        // ── H5. 行进队伍路线失效只查这次变化的区块（M6 出口性能基线“施工高峰”卡顿的根因修复守护）─────────────

        private static void CheckTransitInvalidation()
        {
            Line("  · H5. 行进队伍路线失效（性能基线“施工高峰”卡顿根因修复，ADR-QA-023 第 9 节）：远处的地形变化不重查、到达半径里的路段被挡不算失效、" +
                 "挡在后面的路段不当场从远处重新要整条路线（走到挡点前的路点再规划，全程不踏进不可通行格、照常到达）");
            CampaignState s = NewWorld(6670);
            WorldPlan plan = WorldGenService.PlanFor(s);
            GridCell core = HomeGridService.CorePivot(s);
            PlannedTerritory far = plan?.Territories.OrderByDescending(t => (t.CenterX - core.X) * (t.CenterX - core.X) + (t.CenterY - core.Y) * (t.CenterY - core.Y)).FirstOrDefault();
            TransitGroupRecord g = far != null ? WorldTransitSystem.DispatchRaidFromTerritory(s, far.Id, 4, out _) : null;
            if (g == null)
            {
                Fail("H5 测试准备：派不出突袭");
                return;
            }
            for (int i = 0; i < 1200 && g.RouteState != WorldTransitSystem.RouteFollowing; i++)
            {
                WorldSimulation.StepMany(1);
            }
            double arrival = WorldTransitSystem.ArrivalRadius;
            if (g.RouteState != WorldTransitSystem.RouteFollowing || (g.RouteX?.Length ?? 0) < 4)
            {
                Fail($"H5 测试准备：突袭没拿到路线（{g.RouteState}，{g.RouteX?.Length} 个路点）");
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            byte cliff = GridContent.TerrainCode("cliff");
            int cs = NavService.Kernel.Config.ChunkSize;
            bool InArrival(double x, double y) => (x - g.TargetX) * (x - g.TargetX) + (y - g.TargetY) * (y - g.TargetY) <= arrival * arrival;
            double SegDist(double px, double py, double ax, double ay, double bx, double by)
            {
                double dx = bx - ax;
                double dy = by - ay;
                double l2 = dx * dx + dy * dy;
                double t = l2 <= 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / l2));
                double qx = ax + t * dx - px;
                double qy = ay + t * dy - py;
                return Math.Sqrt(qx * qx + qy * qy);
            }
            // 路线上每段离格子 (x, y) 的最近距离（含当前位置 → 下一个路点）
            double RouteDist(double x, double y)
            {
                double best = double.MaxValue;
                double ax = g.PosX;
                double ay = g.PosY;
                for (int k = g.RouteIndex; k < g.RouteX.Length; k++)
                {
                    best = Math.Min(best, SegDist(x, y, ax, ay, g.RouteX[k], g.RouteY[k]));
                    ax = g.RouteX[k];
                    ay = g.RouteY[k];
                }
                return best;
            }
            int d0 = WorldTransitSystem.DeferredInvalidations;
            int serialA = g.NavSerial;
            long passes0 = NavService.InvalidationPasses;
            // ① 远处的地形变化：离路线（每段）至少 3 个区块的一格变成悬崖 → 这一次变化过了失效检查，但这条路线一格都不重查、不失效。
            GridCell remote = new GridCell(core.X + cs * 3, core.Y + cs * 3);
            for (int tries = 0; tries < 16 && RouteDist(remote.X, remote.Y) < cs * 3; tries++)
            {
                remote = new GridCell(core.X + cs * (3 + tries) * (tries % 2 == 0 ? 1 : -1), core.Y - cs * (3 + tries));
            }
            map.SetTerrain(remote, cliff);
            WorldSimulation.StepMany(2);
            bool remoteOk = NavService.InvalidationPasses > passes0 && WorldTransitSystem.DeferredInvalidations == d0 && g.NavSerial == serialA && g.RouteState == WorldTransitSystem.RouteFollowing;
            // ② 到达半径里的路段被挡：路线最后一个路点（在核心旁边、到达半径里）变成悬崖 → 不算失效（进入半径就到达，不会走到那里）。
            int last = g.RouteX.Length - 1;
            bool lastInside = InArrival(g.RouteX[last], g.RouteY[last]);
            long passes1 = NavService.InvalidationPasses;
            if (lastInside)
            {
                map.SetTerrain(new GridCell(g.RouteX[last], g.RouteY[last]), cliff);
                WorldSimulation.StepMany(2);
            }
            bool insideOk = lastInside && NavService.InvalidationPasses > passes1 && WorldTransitSystem.DeferredInvalidations == d0 && g.NavSerial == serialA && g.RouteState == WorldTransitSystem.RouteFollowing;
            // ③ 挡在后面的路段：从当前位置往后数第 3 段起、在到达半径外、够长的一段，中点横着拉一道 9 格长、2 格厚的悬崖（不碰前面各段）→ 记为失效但不当场重新要路线。
            int seg = -1;
            var wall = new List<GridCell>();
            for (int k = g.RouteIndex + 2; k < g.RouteX.Length && seg < 0; k++)
            {
                double ax = g.RouteX[k - 1];
                double ay = g.RouteY[k - 1];
                double bx = g.RouteX[k];
                double by = g.RouteY[k];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                double mx = (ax + bx) * 0.5;
                double my = (ay + by) * 0.5;
                if (len < 12 || InArrival(mx, my) || InArrival(bx, by))
                {
                    continue;
                }
                double ux = (bx - ax) / len;
                double uy = (by - ay) / len;
                var cells = new List<GridCell>();
                for (int w = -4; w <= 4; w++)
                {
                    for (int th = 0; th < 2; th++)
                    {
                        cells.Add(NavService.CellOf(mx - uy * w + ux * th, my + ux * w + uy * th));
                    }
                }
                // 不碰前面的路段（当前位置起到第 k−1 个路点）：每格离那些段都 ≥ 3 格。
                bool clearOfPrefix = true;
                double px = g.PosX;
                double py = g.PosY;
                for (int j = g.RouteIndex; j < k && clearOfPrefix; j++)
                {
                    foreach (GridCell wc in cells)
                    {
                        if (SegDist(wc.X, wc.Y, px, py, g.RouteX[j], g.RouteY[j]) < 3)
                        {
                            clearOfPrefix = false;
                            break;
                        }
                    }
                    px = g.RouteX[j];
                    py = g.RouteY[j];
                }
                if (clearOfPrefix)
                {
                    seg = k;
                    wall = cells;
                }
            }
            if (seg < 0)
            {
                Fail($"H5 测试准备：路线 {g.RouteX.Length} 个路点里找不到能横拉悬崖的后段");
                return;
            }
            int deferred0 = WorldTransitSystem.DeferredInvalidations;
            int replans0 = WorldTransitSystem.WaypointReplans;
            int serial0 = g.NavSerial;
            foreach (GridCell wc in wall)
            {
                map.SetTerrain(wc, cliff);
            }
            WorldSimulation.StepMany(2);
            bool deferred = WorldTransitSystem.DeferredInvalidations == deferred0 + 1 && g.RouteState == WorldTransitSystem.RouteFollowing && g.NavSerial == serial0;
            // 走过去（测试加速，行进规则不变）：走到挡点前的路点时下一段不通 → 从那里重新要路线、绕过去，全程不踏进不可通行格，照常进入到达半径。
            g.Speed = 20f;
            bool neverBlocked = true;
            int steps = 0;
            for (; steps < 60 * 240 && g.State == TransitGroupState.Marching; steps++)
            {
                WorldSimulation.StepMany(1);
                GridCell at = NavService.CellOf(g.PosX, g.PosY);
                neverBlocked &= NavService.PassableNow(at.X, at.Y, BinGames.Sim.Nav.NavConst.ClassHostile);
            }
            bool replanned = WorldTransitSystem.WaypointReplans >= replans0 + 1 && g.NavSerial > serial0;
            bool arrived = g.State == TransitGroupState.Arrived && !g.Blocked;
            Expect(remoteOk && insideOk && deferred && replanned && neverBlocked && arrived,
                $"H5 远处 {remote.X},{remote.Y} 变成悬崖：失效检查跑了、这条路线不重查不失效（{remoteOk}）；到达半径里的最后一个路点变成悬崖：不算失效（{insideOk}，最后一个路点在半径里 {lastInside}）；" +
                $"第 {seg} 段中点横拉 {wall.Count} 格悬崖：记为失效但不当场重新要路线（{deferred}，推迟 {WorldTransitSystem.DeferredInvalidations - deferred0} 次）→ 走到挡点前的路点重新规划（{replanned}，路点重规划 {WorldTransitSystem.WaypointReplans - replans0} 次）、" +
                $"全程不踏进不可通行格 {neverBlocked}、第 {steps} 步进入到达半径（{g.State}，受阻 {g.Blocked}）");
        }

        // ── 工具 ────────────────────────────────────────────────────────────────

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(Application.dataPath);
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "production", "design", "full-game")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "TEngine", "UnityProject", "DesignDocs")))
                {
                    return dir.FullName;
                }
            }
            return null;
        }

        private static string ReadRepo(string relative)
        {
            string root = RepoRoot();
            string path = root == null ? null : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            return path != null && File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") : null;
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
            Debug.LogWarning("[M6 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
