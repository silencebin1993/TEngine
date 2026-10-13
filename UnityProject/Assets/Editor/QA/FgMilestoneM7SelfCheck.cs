using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
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
using Pu = GameLogic.EditorTools.FgPurificationSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG7-E2E-01 自检 [M7 出口]：里程碑出口里能在编辑模式下逐语义断言的部分（正向旅程本身在 Play 里由 tools/unity-journey.sh FGJ-M7 跑）。
    /// 轻量口径（用户 2026-10-12）：IC-REQ-022 反向场景用起真实系统的单段自检覆盖——夹具只搭场景（放塔、排日程、围悬崖），被测行为全走真实系统（世界模拟、净化生产步、工单引擎与寻路内核、接入 / 编队命令、真文件存读档）。
    ///
    /// A. 旅程登记：FGJ-M7 覆盖里程碑原文五段 + FGT-ENV-008 + DEBT-FG7ENV05-07 的真实鼠标右键遗迹点；固定种子；断点都是旅程里的步骤。
    /// A2. 旅程源码只走输入通道（不直接调业务方法）；两处日程捷径（SilentNightService.PlanDayForTests、WeatherService.ScheduleForTests）只在 ApplyScheduleFixture 里，没有跳时 / 研究 / 突袭 / 污染 / 供液夹具。
    /// G1. 旅程抓到的产品修复（RegionSquadCommandSystem）：右键地面时所有选中机器都被拒（静默夜），命令栏最新一行是拒绝原因，不再补“已下达（0 台）”；有机器接令时照常写“已下达（N 台）”。
    /// G2. 测试捷径 PlanDayForTests 与正式排定同一套时刻 / 预警提前量 / 持续时间，只改日期；走正式流程预警、开始、结束，结束后照常按间隔排下一次；进行中 / 预警已过 / 非法日期拒绝。
    /// R1～R6. IC-REQ-022 反向场景（每类“出错 → 看得到原因 → 恢复”）：
    ///   R1 资源不足——净化塔没接冷却液：一格不降、状态缺流体、原因与通知 → 接上冷却液 → 下一次降级发生。
    ///   R2 目标死亡——右键遗迹点派去回收的机器在路上被摧毁：回收单不挂在死机器上、不发技术数据 → 别的机器接手回收完、技术数据只发一次。
    ///   R3 断网 / 失联——静默夜：接入被拒、右键移动全部被拒（命令栏写原因，即 G1）→ 静默夜结束后右键移动照常下达、接入成功。
    ///   R4 暂停与倍速——静默夜 + 酸雨同时进行：暂停 120 帧整份战役状态不动；0.5x / 3x 下静默夜剩余时间与酸雨结算都按游戏时间走，真实帧与倍速成反比。
    ///   R5 存读档——静默夜 + 酸雨 + 净化塔缺冷却液 + 夜里放的施工单暂缓：真文件存档 → 读档后逐项一致、接着跑与一直跑一致 → 读档后接上冷却液恢复净化（FGT-ENV-008）。
    ///   R6 路径失败——遗迹点四周围成悬崖：回收单“无法到达”转等待、发可定位通知、不发技术数据 → 拆掉悬崖后 30 秒重试 → 机器到场回收完。
    /// C. 缺口清零：门禁第一个里程碑是 M7 的缺口全部 Closed / 部分关闭、延后项全部 Closed / 部分关闭 / 顺延；本 Story 的登记与关闭在表里。
    /// D. 试玩包：FG-M7 四份材料齐全，目录登记“待用户试玩、不阻塞”。
    /// E. 需求覆盖（IC-REQ-020）：FG07 FGT-ENV-001～008 各有自检段且登记在全量自检，能进旅程的另有旅程步骤。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgMilestoneM7SelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgMilestoneM7SelfCheck.RunFromMenu。
    /// </summary>
    public static class FgMilestoneM7SelfCheck
    {
        private const int Slot = 7;
        private const float FrameDt = 1f / 30f;
        private const string SettingsPrefsKey = "BinGames.GameSettings.v1";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static float _fakeNow;

        [MenuItem("BinGames/QA/自检/M7 出口")]
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
            Line("\n[M7 出口] FG7-E2E-01：旅程登记与正式输入、日程捷径守护、命令栏拒绝原因、IC-REQ-022 六类反向（真实系统）、缺口清零、试玩包、FGT-ENV 需求覆盖");
            Step(CheckJourneyCatalog);
            Step(CheckJourneySourcesUseInput);
            Step(CheckGapGate);
            Step(CheckPlaytestPack);
            Step(CheckRequirementCoverage);
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过 G / R 段（会改写世界与存档目录）");
            }
            else
            {
                WorldChecks();
            }
            Line($"  · [M7 出口] 断言通过 {_pass}，失败 {_fail}");
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

        private static void CheckJourneyCatalog()
        {
            Line("  · A. 旅程登记：FGJ-M7 覆盖里程碑原文五段、FGT-ENV-008 与 DEBT-FG7ENV05-07；固定种子；断点都是旅程里的步骤");
            JourneyDef m7 = JourneyCatalog.Create(FgjM7Journey.Id);
            var exit = new (string What, string[] Ids)[]
            {
                ("从主菜单“新建”出发、固定种子", new[] { "menu_new", "new_game", "seed" }),
                ("看日历（正式排定的第一次静默夜）", new[] { "cal_open", "cal_close" }),
                ("为第一次静默夜做准备：储能（研发树真实排研究、建造栏放储能站）", new[] { "rt_queue", "wait_storage", "m7_place3", "storage_built" }),
                ("为第一次静默夜做准备：规则（常驻规则面板从预设新建“静默夜预案”、选储能站、启用）", new[] { "rules_open", "rules_new", "rules_target", "rules_enable", "rules_close" }),
                ("静默夜期间确认规则表里每一项（预警、预案、开始、接入、右键移动、新施工暂缓、改规则被拒、面板可用）", new[] { "sn_warn", "sn_plan", "sn_start", "sn_uplink", "sn_move", "sn_place", "sn_held", "sn_rule_toggle", "sn_cal_open" }),
                ("FGT-ENV-008：静默夜 + 酸雨中保存并返回主菜单、读档、逐项一致", new[] { "sn_esc", "sn_quit", "sn_menu", "sn_load", "sn_loaded" }),
                ("撑过去，捡回风暴残骸（结束总结弹出条、搬运单交付）", new[] { "sn_end", "debris_toast", "debris_home" }),
                ("酸雨中把关键产线盖上顶棚（建造模式真实拖拽、顶棚下不掉耐久）", new[] { "rain_start", "roof_plan", "roof_drag", "roof_built", "roof_hp" }),
                ("建净化塔（泵 + 调配站 + 稀土提取钻 + 冷却液管），归还面积真实增长", new[] { "m7_place1", "m7_built1", "drill_plan", "m7_place2", "coolant", "returned", "final" }),
                ("DEBT-FG7ENV05-07：真实鼠标右键遗迹点派工、右键已回收遗迹 = 移动且原工单保留", new[] { "relic_rc", "relic_done", "relic_rc2" }),
                ("等待段用正式倍速键", new[] { "speed3", "speed1", "speed3b" }),
            };
            List<string> missing = exit.Where(x => m7 == null || x.Ids.Any(id => m7.Steps.All(s => s.Id != id))).Select(x => x.What).ToList();
            Expect(Wellformed(m7) && missing.Count == 0 && m7.Seed == FgjM7Journey.TestSeed,
                $"FGJ-M7：{m7?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m7?.Seed}；里程碑原文与承接都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            bool ck = m7 != null && m7.CheckpointAfter.Length >= 1 && m7.CheckpointAfter.All(id => m7.Steps.Any(s => s.Id == id));
            Expect(ck && JourneyCatalog.Ids.Contains(FgjM7Journey.Id) && JourneyCatalog.Ids.Contains(FgjM6Journey.Id),
                $"FGJ-M7 登记在旅程登记表、前序旅程仍在；断点 [{string.Join(", ", m7?.CheckpointAfter ?? Array.Empty<string>())}] 都是旅程里的步骤");
        }

        // ── A2. 旅程源码只走输入通道 ─────────────────────────────────────────────────

        /// <summary>业务方法在旅程源码里出现 = 绕开了玩家输入（M6 的清单 + M7 的天气 / 静默夜 / 净化 / 遗迹 / 编队命令）。</summary>
        private static readonly string[] ForbiddenCalls =
        {
            "PlanHistory.Undo(", "PlanHistory.Place(", "PlanHistory.PlaceBeltPath(", "HomeGridService.TryPlace", "HomeGridService.TryRemove", "HomeGridService.TryToggleDemolish",
            "BeltNetworkService.TryPlace", "PipeNetworkService.TryPlace", "CampaignSaveService.Save(", "CampaignAutoSaveService.Save", "UiConfirmDialog.Confirm(", "BuildingOps.Try",
            "ProductionPanelUIToolkit.Open(", "GameClock.SetPaused(", "GameClock.SetSpeed(", "GameClock.SkipForTests(", "HomeValleyPowerGrid.TrySet", "ResearchService.TryEnqueue",
            "ResearchService.CompleteForTests(", "ResearchTreePanelUIToolkit.Open", "SignalUplinkService.Request", "StandingRuleService.TryCreate", "StandingRuleService.TrySetEnabled(",
            "StandingRuleService.TryAdd", "RulesPanelUIToolkit.Open", "NotificationCenter.TryOpen(", "MachineRegistry.ApplyDamage(", "MachineRegistry.SpawnMachine(",
            "RaidDirectorService.RequestStoryRaid(", "RoofService.Try", "RoofService.Place", "HomeExplorationService.OrderRecovery(", "HomeExplorationService.CompleteRecovery(",
            "ContextRelicForTests(", ".IssueMoveTo(", ".DebugSelect", "HomeValleyWorkOrders.CancelOrder(", "PurificationService.Repollute(", "SetPollution(", ".AddProducer(",
            "SilentNightService.ResetForTests(", "WeatherService.ResetForTests(", "CalendarPanelUIToolkit.Open", "MachineRoster.Try",
        };

        private static readonly string[] JourneyFiles = { "FgjM7Common.cs", "FgjM7Journey.cs", "FgjM7Journey.Night.cs" };

        /// <summary>两处日程捷径：只能出现在 ApplyScheduleFixture 里，各 1 处（ADR-QA-024 夹具表）。</summary>
        private static readonly (string Token, string Method, int Count)[] Fixtures =
        {
            ("SilentNightService.PlanDayForTests(", "ApplyScheduleFixture", 1),
            ("WeatherService.ScheduleForTests(", "ApplyScheduleFixture", 1),
        };

        private static void CheckJourneySourcesUseInput()
        {
            Line("  · A2. FGJ-M7 源码只走输入通道；两处日程捷径只在 ApplyScheduleFixture，没有跳时 / 研究 / 突袭 / 污染 / 供液夹具");
            var hits = new List<string>();
            var raw = new StringBuilder();
            var codeAll = new StringBuilder();
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
                hits.AddRange(ForbiddenCalls.Where(code.Contains).Select(x => f + ":" + x));
            }
            Expect(hits.Count == 0, $"旅程源码里没有业务方法调用（扫描 {ForbiddenCalls.Length} 种）{(hits.Count == 0 ? string.Empty : "；发现：" + string.Join("、", hits))}");
            string all = codeAll.ToString();
            var bad = new List<string>();
            var seen = new List<string>();
            foreach ((string token, string method, int count) in Fixtures)
            {
                int total = Count(all, token);
                string body = MethodBodyAny(all, method);
                int n = body == null ? 0 : Count(body, token);
                seen.Add($"{token.TrimEnd('(')} 在 {method} 里 {n} 处");
                if (n != count || total != n)
                {
                    bad.Add($"{token} 在 {method} 里 {n} 处（应 {count}）、方法外 {total - n} 处");
                }
            }
            Expect(bad.Count == 0, $"日程捷径只在登记的方法里（{string.Join("；", seen)}）{(bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad))}");
            string src = raw.ToString();
            string[] inputs =
            {
                "GameActionId.OpenCalendar", "GameActionId.OpenRules", "GameActionId.OpenResearch", "GameActionId.SpeedTriple", "GameActionId.SpeedNormal", "GameActionId.Cancel",
                "FgjM3Common.PressBuild(", "FgjM3Common.TryDrag(", "FgjM3Common.TryClick(p, 1)", "JourneyInput.PressToggleTo(", "\"PauseSaveQuit\"", "\"m_btn_Load\"", "\"RulesPanelClose\"",
            };
            List<string> miss = inputs.Where(x => !src.Contains(x)).ToList();
            Expect(miss.Count == 0,
                "旅程用到的正式输入：日历 / 常驻规则 / 研发树 / 倍速 / Esc 按键，建造模式点击与拖拽，右键（世界点击按钮 1：右键地面移动、右键遗迹点派工），UI Toolkit 开关点击，暂停菜单保存并返回、主菜单读取" +
                (miss.Count == 0 ? string.Empty : "；缺：" + string.Join("、", miss)));
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

        /// <summary>取任意返回类型的方法 “名字(” 之后的第一个方法体（按花括号配对；源码已去掉注释）。没有返回 null。</summary>
        private static string MethodBodyAny(string code, string method)
        {
            Match m = Regex.Match(code, @"\b" + Regex.Escape(method) + @"\s*\(\s*JourneyContext");
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

        /// <summary>修复轮 1：门禁 M7 的行写“部分关闭”时，只有写明“剩……”且门禁列另有晚于 M7 的里程碑（剩余部分的门禁）才算 M7 出口合格；
        /// 否则（例如正文已是“全部关闭”、或剩余部分仍压在 M7）按未关闭处理。</summary>
        private static bool PartialRemainderDeferred(string gate, string st)
        {
            if (!st.StartsWith("部分关闭", StringComparison.Ordinal) || !st.Contains("剩"))
            {
                return false;
            }
            foreach (Match m in FirstMilestone.Matches(gate ?? string.Empty))
            {
                if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 7)
                {
                    return true;
                }
            }
            return false;
        }

        private static void CheckGapGate()
        {
            Line("  · C. 里程碑出口：属于 FG-M7 的缺口全部 Closed / 部分关闭、延后项全部 Closed / 部分关闭 / 顺延（按门禁列的第一个里程碑判定）");
            string text = ReadRepo("production/design/full-game/FG-GAP-REGISTER.md");
            if (text == null)
            {
                Fail("找不到 production/design/full-game/FG-GAP-REGISTER.md（仓库根定位失败）");
                return;
            }
            var openGaps = new List<string>();
            var openDebts = new List<string>();
            var partialRows = new List<string>();
            int gapRows = 0, debtRows = 0, m7Rows = 0;
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
                    if (cols.Length >= 7 && FirstMilestoneOf(cols[5]) == 7)
                    {
                        m7Rows++;
                        if (st.StartsWith("部分关闭", StringComparison.Ordinal))
                        {
                            partialRows.Add(cols[0]);
                        }
                        if (!st.StartsWith("Closed", StringComparison.Ordinal) && !PartialRemainderDeferred(cols[5], st))
                        {
                            openGaps.Add(cols[0]);
                        }
                    }
                    continue;
                }
                debtRows++;
                if (cols.Length < 9 || FirstMilestoneOf(cols[6]) != 7)
                {
                    continue;
                }
                m7Rows++;
                if (st.StartsWith("部分关闭", StringComparison.Ordinal))
                {
                    partialRows.Add(cols[0]);
                }
                if (!st.StartsWith("Closed", StringComparison.Ordinal) && !PartialRemainderDeferred(cols[6], st) && !st.StartsWith("顺延", StringComparison.Ordinal))
                {
                    openDebts.Add(cols[0]);
                }
            }
            // 修复轮 1：门禁 M7 的“部分关闭”行逐条列出供人工核对（M7 部分已完成、剩余部分另有晚于 M7 的门禁）。
            Line("    门禁属于 FG-M7、状态“部分关闭”的行：" + (partialRows.Count == 0 ? "无" : string.Join("、", partialRows)));
            Expect(gapRows > 100 && debtRows > 300 && m7Rows >= 8, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中门禁属于 FG-M7 的 {m7Rows} 行（解析没有漏行）");
            Expect(openGaps.Count == 0 && openDebts.Count == 0,
                openGaps.Count + openDebts.Count == 0
                    ? "属于 FG-M7 的缺口全部 Closed / 部分关闭（剩余部分另有门禁），延后项全部 Closed / 部分关闭 / 顺延（顺延写明理由与新门禁）"
                    : $"FG-M7 出口前还开着：缺口 [{string.Join("、", openGaps)}]，延后项 [{string.Join("、", openDebts)}]");
            string[] mine = { "DEBT-FG7E2E01-01", "DEBT-FG7E2E01-02", "DEBT-FG7E2E01-03", "DEBT-FG7E2E01-04", "DEBT-FG7E2E01-05" };
            string[] closedHere = { "DEBT-FG7ENV05-07", "DEBT-FG0QA01-03" };
            List<string> notClosed = closedHere.Where(id => !status.TryGetValue(id, out string v) || !v.StartsWith("Closed", StringComparison.Ordinal)).ToList();
            bool registered = mine.All(status.ContainsKey);
            bool pending = status.TryGetValue("DEBT-FG7E2E01-01", out string p1) && p1.Contains("待用户");
            Expect(registered && notClosed.Count == 0 && pending,
                "本 Story 的登记在表里（DEBT-FG7E2E01-01 待用户试玩、-02 归还面积第一个里程碑可达性、-03 旅程两处日程捷径、-04 v4 基线存读档两项偏高、-05 每步托管分配增长）；" +
                $"由本 Story 关闭的 {closedHere.Length} 条都是 Closed" + (notClosed.Count == 0 ? string.Empty : "；没关：" + string.Join("、", notClosed)));
        }

        // ── D. 试玩包 ──────────────────────────────────────────────────────────────

        private static void CheckPlaytestPack()
        {
            Line("  · D. 试玩包：FG-M7 材料齐全（真人试玩由用户择时进行，DEBT-FG7E2E01-01，不阻塞）");
            const string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M7-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M7-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M7-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M7-问题清单模板.md");
            string readme = ReadRepo(dir + "README.md");
            bool exist = script != null && table != null && form != null && issues != null && readme != null;
            Expect(exist, "四份材料（试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板）与目录说明都在");
            if (!exist)
            {
                return;
            }
            bool tasks = new[] { "日历", "储能", "静默夜预案", "风暴残骸", "顶棚", "净化塔", "归还面积", "遗迹" }.All(script.Contains);
            bool standard = table.Contains("期待") && table.Contains("厌烦") && table.Contains("成就感") && table.Contains("维护压力") && table.Contains("FGJ-M7") && table.Contains("不是门禁");
            bool questions = form.Contains("FG07") && form.Contains("静默夜") && form.Contains("归还面积") && form.Contains("天气") && form.Contains("DEBT-FG7E2E01-02");
            bool registered = readme.Contains("FG-M7") && readme.Contains("DEBT-FG7E2E01-01") && readme.Contains("不阻塞");
            Expect(tasks && standard && questions && registered,
                $"脚本按出口旅程给任务卡（{tasks}）；对照表把 FG07 第 9 节三问拆成可计数观察（期待 / 厌烦、成就感、维护压力；不是门禁，{standard}）；问卷对照 FG07 并带归还面积里程碑可达性观察（DEBT-FG7E2E01-02，{questions}）；" +
                $"目录登记“待用户试玩、不阻塞”（{registered}）");
        }

        // ── E. 需求覆盖 ────────────────────────────────────────────────────────────

        private static void CheckRequirementCoverage()
        {
            Line("  · E. 需求覆盖（IC-REQ-020）：FG07 FGT-ENV-001～008 各有自检段且登记在全量自检，能进旅程的另有旅程步骤");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            const string qa = "TEngine/UnityProject/Assets/Editor/QA/";
            JourneyDef m7 = JourneyCatalog.Create(FgjM7Journey.Id);
            var map = new (string Test, string File, string Registered, string Step)[]
            {
                ("FGT-ENV-001", "FgDayNightSelfCheck.cs", "FgDayNightSelfCheck", "speed3"),
                ("FGT-ENV-002", "FgSilentNightSelfCheck.cs", "FgSilentNightSelfCheck", "sn_held"),
                ("FGT-ENV-003", "FgSilentNightSelfCheck.cs", "FgSilentNightSelfCheck", null), // 远征跨静默夜：M7 旅程没有远征（远征在 FG8），自检 R1 / X1
                ("FGT-ENV-004", "FgWeatherSelfCheck.cs", "FgWeatherSelfCheck", "roof_hp"),
                ("FGT-ENV-005", "FgPurificationSelfCheck.cs", "FgPurificationSelfCheck", "returned"),
                ("FGT-ENV-006", "FgPurificationSelfCheck.cs", "FgPurificationSelfCheck", null), // 再污染：化工突袭在 FG10（DEBT-FG7ENV04-02），自检 R1 经接口
                ("FGT-ENV-007", "FgExplorationSelfCheck.cs", "FgExplorationSelfCheck", "relic_seen"),
                ("FGT-ENV-008", "FgMilestoneM7SelfCheck.cs", "FgMilestoneM7SelfCheck", "sn_loaded"),
            };
            var bad = new List<string>();
            foreach ((string test, string file, string registered, string step) in map)
            {
                string src = ReadRepo(qa + file);
                bool journeyOk = step == null || (m7?.Steps.Any(s => s.Id == step) ?? false);
                if (src == null || !src.Contains(test) || !segments.Contains(registered) || !journeyOk)
                {
                    bad.Add($"{test}（{file} / {step ?? "无旅程步骤"}）");
                }
            }
            Expect(bad.Count == 0, $"{map.Length} 项自动验收各有自检段且登记在全量自检，6 项另有 FGJ-M7 旅程步骤（FGT-ENV-003 远征、006 化工再污染的触发方在后续里程碑）" +
                                   (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
        }

        // ── 世界段：G / R ─────────────────────────────────────────────────────────

        private static void WorldChecks()
        {
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            string savedPrefs = PlayerPrefs.GetString(SettingsPrefsKey, null);
            bool hadPrefs = PlayerPrefs.HasKey(SettingsPrefsKey);
            bool hadCamera = Camera.main != null;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            Func<bool> originalAutoPause = NotificationCenter.AutoPauseHandler;
            string originalCodexPath = MechanicCodex.FilePathOverrideForTests;
            string dir = Path.Combine(Path.GetTempPath(), "bingames-fgm7-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                Campaign.Content.FgContentTables.Reload();
                ItemCatalog.Reload();
                ProducerCatalog.Reload();
                GameClock.ReloadTuning();
                DayNightCycle.ReloadTuning();
                PlayerPrefs.DeleteKey(SettingsPrefsKey);
                GameSettings.Load();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = dir;
                Directory.CreateDirectory(dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(dir, "codex.json");
                MechanicCodex.Reload();
                CameraDirector.RealDeltaTime = () => FrameDt;
                NotificationCenter.AutoPauseHandler = null;
                GameRoot.BindWorldProviders();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType.Trim()}（{SystemInfo.processorCount} 线程）；" +
                     "世界段全部起真实系统（世界模拟、昼夜 / 静默夜 / 天气 / 净化正式入口 Install、工单引擎与寻路内核、编队命令、真文件存读档）；夹具只搭场景");
                Step(CheckPlanDayShortcut);
                Step(CheckResourceShortage);
                Step(CheckTargetDeath);
                Step(CheckSignalLoss);
                Step(CheckPauseAndSpeed);
                Step(CheckSaveLoad);
                Step(CheckPathFailure);
            }
            catch (Exception e)
            {
                Fail($"[M7 出口] 世界段抛异常：{e}");
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
                ResetEnv();
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
                ProductionService.ResetForTests();
                HomeGridService.Invalidate();
                BinGames.Sim.WorldGen.WorldGenKernel.ReleaseAll();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                MechanicCodex.FilePathOverrideForTests = originalCodexPath;
                MechanicCodex.Reload();
                MachineRegistry.ResetForNewCampaign();
                HomeValleyWorkOrders.ResetSessionState();
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
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
        }

        private static void ResetEnv()
        {
            PurificationService.ResetForTests();
            WeatherService.ResetForTests();
            RoofService.ResetForTests();
            SilentNightService.ResetForTests();
            StandingRuleService.ResetForTests();
            SignalUplinkService.ResetForTests();
            SignalLinkService.ResetForTests();
            CalendarService.ResetForTests();
            DayNightCycle.ResetForTests();
            PowerEnvironment.ResetForTests();
            HomeValleyPowerGrid.ResetForTests();
            IntelService.ResetSessionState();
            ResearchService.ResetForTests();
            OverlayService.ResetForTests();
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static int Hz => Math.Max(1, GameClock.StepHz);
        private static double HourS => GameClock.HourSeconds;

        /// <summary>新世界（家园、种子地形），昼夜 / 静默夜 / 天气 / 净化都走正式入口（与 GameRoot.Startup 同一组 Install）；开局发电机修好 + 核心旁一座发电机 2。</summary>
        private static CampaignState NewWorld(int seed)
        {
            ResetEnv();
            SignalUplinkService.RealTimeForTests = () => _fakeNow;
            CampaignState s = F.NewWorld(seed, true, 600);
            SignalCoreService.EnsureInitialized(s);
            SignalCoverageService.Invalidate();
            DayNightCycle.Install();
            SilentNightService.Install();
            WeatherService.Install();
            PurificationService.Install();
            F.PowerUp(s);
            WorldSimulation.StepMany(1);
            return s;
        }

        private static SilentNightState D(CampaignState s) => SilentNightService.StateOf(s);

        private static void SkipToTick(CampaignState s, long target)
        {
            if (target > GameClock.Ticks)
            {
                GameClock.SkipForTests(s, target - GameClock.Ticks);
            }
        }

        private static bool StepUntil(Func<bool> done, long maxSteps)
        {
            int chunk = Math.Max(1, Hz / 10);
            for (long i = 0; i < maxSteps; i += chunk)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(chunk);
            }
            return done();
        }

        /// <summary>逐步推进（边界步要精确到步时用）。</summary>
        private static bool StepEach(Func<bool> done, int maxSteps)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                if (done())
                {
                    return true;
                }
                WorldSimulation.StepMany(1);
            }
            return done();
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

        private static double HourOf(long tick) => GameClock.HourOfDay(tick / (double)Hz);

        private static int CountOf(string typeId) => NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == typeId).Sum(e => e.Count);

        private static NotificationEntry LastOf(string typeId) => NotificationCenter.History.LastOrDefault(e => e?.Type != null && e.Type.Id == typeId);

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string LastEvent(HomeValleyController home) => home.SquadCommands.RecentEvents.LastOrDefault() ?? string.Empty;

        /// <summary>场景夹具：开局信号塔修好通电（修理工单本身由 FG1-SIG-07 / FG4-ECO-05 覆盖），走到下一次覆盖评估——遗迹点在覆盖里被发现。</summary>
        private static void RepairTowerAndEvaluate(CampaignState s)
        {
            BuildingRecord t = s.BuildingRecords.First(b => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeSignalTower);
            t.ConstructionState = BuildingConstructionState.Operational;
            t.PowerState = BuildingPowerState.Powered;
            SignalCoverageService.Invalidate();
            int e0 = SignalCoverageService.EvaluateCount;
            int g = 0;
            while (SignalCoverageService.EvaluateCount == e0 && g++ < 120)
            {
                WorldSimulation.StepMany(1);
            }
        }

        private static List<WorldFeature> FoundRelics(CampaignState s, int half)
        {
            GridCell c = HomeGridService.CorePivot(s);
            var list = new List<WorldFeature>();
            WorldGenQuery.FeaturesIn(s, c.X - half, c.Y - half, c.X + half, c.Y + half, list);
            HomeGridMap map = HomeGridService.MapFor(s);
            return list.Where(f => f.Kind == WorldFeatureKind.Relic && HomeExplorationService.IsFound(s, f.Id) && map.IsExploredNoLoad(new GridCell(f.X, f.Y)))
                .OrderBy(f => f.Distance).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        }

        private static List<HomeValleyMachineMarker> Haulers(HomeValleyController home) =>
            home.MachineMarkersForTests.Where(x => x != null && x.IsValid && MachineRegistry.TryGetRecord(x.LogicId, out MachineRecord r) && r.IsAlive
                                                   && (r.ChassisId == HomeValleyLayout.Erc001ChassisId || r.ChassisId == HomeValleyLayout.Erc002ChassisId))
                .OrderBy(x => x.LogicId).ToList();

        private static Ray RayAt(WorldFeature f) => new Ray(new Vector3(f.X + 0.6f, 40f, f.Y), Vector3.down);

        /// <summary>中心 c 半径 r 内（dx² + dy² ≤ r²）的污染夹具：中心 2 级、其余 1 级（代表核心附近的轻度污染地带）。</summary>
        private static void Pollute(CampaignState s, GridCell c, int r)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int d2 = dx * dx + dy * dy;
                    if (d2 <= r * r)
                    {
                        map.SetPollution(new GridCell(c.X + dx, c.Y + dy), (byte)(d2 == 0 ? 2 : 1));
                    }
                }
            }
        }

        /// <summary>
        /// 与 FGJ-M7 同一对日程捷径（ADR-QA-024 夹具表）：静默夜改到“预警还在 6 个游戏小时以后”的最近一个夜里（只改日期），
        /// 酸雨排在静默夜开始前半个游戏小时开始、持续 0.5 日（覆盖整个静默夜）。之后全走正式推进。
        /// </summary>
        private static bool ScheduleNightAndRain(CampaignState s)
        {
            SilentNightState d = D(s);
            long now = GameClock.Ticks;
            int today = GameClock.DayOf(GameClock.GameSeconds);
            int day = -1;
            for (int k = today; k <= today + 3 && day < 0; k++)
            {
                SilentNightService.PlanTicks(k, out long w, out _, out _);
                if (w > now + GameClock.TicksFor(6 * HourS))
                {
                    day = k;
                }
            }
            if (day < 0 || !SilentNightService.PlanDayForTests(s, day))
            {
                return false;
            }
            double startIn = (d.StartTick - now) / (double)Hz - 0.5 * HourS;
            return WeatherService.ScheduleForTests(s, WeatherService.AcidRain, startIn, 0.5 * GameClock.DaySeconds);
        }

        /// <summary>跳到静默夜预警前，之后逐步推进（预警 → 酸雨开始 → 静默夜开始都走正式世界步），直到两者同时进行。</summary>
        private static bool EnterNightInRain(CampaignState s)
        {
            SilentNightState d = D(s);
            SkipToTick(s, d.WarnTick - 2);
            return StepUntil(() => d.Active && WeatherService.IsActive(s, WeatherService.AcidRain), d.StartTick - GameClock.Ticks + 4L * Hz);
        }

        // ── G2. PlanDayForTests 与正式排定一致 ─────────────────────────────────────

        private static void CheckPlanDayShortcut()
        {
            CampaignState s = NewWorld(7701);
            SilentNightState d = D(s);
            int formalDay = d.NextDay;
            long fw = d.WarnTick, fs = d.StartTick, fe = d.EndTick;
            bool badDay = !SilentNightService.PlanDayForTests(s, 0);
            bool planned = SilentNightService.PlanDayForTests(s, 2);
            SilentNightService.PlanTicks(2, out long w, out long st, out long en);
            bool same = planned && d.NextDay == 2 && d.WarnTick == w && d.StartTick == st && d.EndTick == en
                        && en - st == fe - fs && st - w == fs - fw && Math.Abs(HourOf(st) - HourOf(fs)) < 1e-3 && Math.Abs(HourOf(en) - HourOf(fe)) < 1e-3 && Math.Abs(HourOf(w) - HourOf(fw)) < 1e-3;
            bool window = StandingRuleService.TrySilentWindow(s, out long ws, out long we) && ws == st && we == en;
            bool cal = CalendarService.TryNext(s, CalendarKind.SilentNight, GameClock.GameSeconds, 3 * GameClock.DaySeconds, out CalendarEntry ce)
                       && Math.Abs(ce.StartSeconds - st / (double)Hz) < 1e-6;
            Expect(formalDay == 5 && badDay && same && window && cal,
                $"G2 日程捷径 PlanDayForTests（ADR-QA-024 夹具表 ①）：正式排定第 {formalDay} 日；改到第 2 日后预警 {GameClock.FormatHhMm(w / (double)Hz)} / 开始 {GameClock.FormatHhMm(st / (double)Hz)} / 结束 {GameClock.FormatHhMm(en / (double)Hz)}，" +
                $"与正式同一套时刻、预警提前量（{(st - w) / (double)Hz:0} 秒）与持续时间（{(en - st) / (double)Hz:0} 秒），只改日期 {same}；常驻规则时间窗 {window}、日历 {cal} 跟着改；第 0 日被拒 {badDay}");

            int warn0 = SilentNightService.WarnCount;
            SkipToTick(s, w - 3);
            bool warned = StepEach(() => d.Warned, 10) && SilentNightService.WarnCount == warn0 + 1 && CountOf("silent_night_warning") >= 1;
            SkipToTick(s, st - 3);
            bool started = StepEach(() => d.Active, 10);
            long startedAt = GameClock.Ticks;
            bool duringRejected = !SilentNightService.PlanDayForTests(s, 4);
            SkipToTick(s, en - 3);
            bool ended = StepEach(() => !d.Active, 10);
            bool pastRejected = !SilentNightService.PlanDayForTests(s, 2);
            Expect(warned && started && startedAt >= st && startedAt <= st + 2 && duringRejected && ended && d.Count == 1 && d.NextDay == 2 + formalDay && pastRejected,
                $"G2 改期后走正式流程：预警 {warned}、开始步 {startedAt}（应 {st}）、结束 {ended}；进行中再改期被拒 {duringRejected}；结束后照常按间隔排下一次（第 {d.NextDay} 日 = 2 + {formalDay}）；预警已过的日期被拒 {pastRejected}");
        }

        // ── R1 资源不足：净化塔缺冷却液 ─────────────────────────────────────────────

        private static void CheckResourceShortage()
        {
            CampaignState s = NewWorld(7711);
            Pu.Rig rig = Pu.LayTower(s, "m7r1", feed: false);
            if (rig == null)
            {
                Fail("R1 找不到放净化塔的空地");
                return;
            }
            HomeGridMap map = HomeGridService.MapFor(s);
            ProductionService.Producer p = F.P(s, rig.Tower);
            GridCell c = rig.Pivot;
            int radius = PurificationService.RadiusOf(rig.Tower);
            Pollute(s, c, radius);
            long stepT = PurificationService.StepTicks;
            int steps0 = PurificationService.StepCount;
            int stall0 = CountOf("purify_stalled");
            long ret0 = map.ReturnedCells;
            WorldSimulation.StepMany((int)(stepT * 1.2));
            string reason = F.Reason(s, p);
            string notice = LastOf("purify_stalled")?.Latest?.DetailText ?? string.Empty;
            bool stalled = p.State == ProdState.MissingFluid && p.Reason == ProdReason.NoCoolant && !p.Rec.PurifySupplied && PurificationService.StepCount == steps0
                           && map.GetPollution(c) == 2 && map.ReturnedCells == ret0 && CountOf("purify_stalled") == stall0 + 1 && reason.Contains("缺冷却液");
            Pu.Feed(rig, 20000);
            F.Resync(s);
            bool resumed = StepUntil(() => PurificationService.StepCount > steps0, stepT + 3L * Hz + 400);
            bool cleaner = map.GetPollution(c) == 1 && p.State == ProdState.Working && p.Rec.PurifySupplied;
            Expect(radius > 0 && stalled && resumed && cleaner,
                $"R1 资源不足（净化塔没接冷却液，种子 7711）：1.2 个降级周期里一格不降、归还面积不变，状态缺流体“{reason.Replace("\n", " / ")}”，通知一次“{notice}”{stalled} → " +
                $"接上冷却液 → {(resumed ? "下一次降级发生" : "没有恢复")}，中心格 2 → {map.GetPollution(c)} 级、状态 {p.State} {cleaner}");
        }

        // ── R2 目标死亡：回收遗迹的机器在路上被摧毁 ─────────────────────────────────

        private static void CheckTargetDeath()
        {
            CampaignState s = NewWorld(7712);
            RepairTowerAndEvaluate(s);
            HomeValleyController home = WorldSimulation.Home;
            List<WorldFeature> found = FoundRelics(s, 320);
            List<HomeValleyMachineMarker> haulers = Haulers(home);
            if (found.Count == 0 || haulers.Count < 2)
            {
                Fail($"R2 信号塔覆盖里找不到遗迹点（{found.Count}）或搬运机少于 2 台（{haulers.Count}）");
                return;
            }
            WorldFeature a = found[found.Count - 1]; // 覆盖里最远的一处：派工后还在路上
            HomeExplorationService.TryGetRelic(s, a.Id, out HomeExplorationService.RelicInfo ia);
            HomeValleyMachineMarker m = haulers[0];
            int victim = m.LogicId;
            long income0 = TechDataFlow.IncomeOf(s, TechDataFlow.Relic);
            bool handled = home.ContextRelicForTests(m, RayAt(a));
            WorkOrderRecord o = HomeExplorationService.ActiveOrder(s, a.Id);
            bool assigned = handled && o != null && o.AssignedMachineLogicId == victim;
            WorldSimulation.StepMany(Hz);
            bool enRoute = o != null && o.State == WorkOrderState.Reserved && !HomeExplorationService.IsRecovered(s, a.Id);
            int cue0 = Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.MachineDestroyed);
            int fail0 = Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Failure);
            string failType = NotificationCatalog.TypeForCue(Campaign.Feedback.FeedbackCueId.Failure)?.Id;
            int failNotes0 = failType == null ? 0 : CountOf(failType);
            MachineRegistry.ApplyDamage(victim, 1e6f);
            WorldSimulation.StepMany(Hz / 2 + 2);
            // 修复轮 1：阵亡提示音之外，还要有一条可定位的“工单中止”失败通知（HomeValleyWorkOrders 机器损失分支 RaiseLocated(Failure)）。
            NotificationEntry failNote = failType == null ? null : LastOf(failType);
            bool deathShown = Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.MachineDestroyed) == cue0 + 1
                              && Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Failure) > fail0
                              && failType != null && CountOf(failType) > failNotes0 && failNote?.Latest != null && failNote.Latest.HasLocation;
            string orderAfter = o == null ? "无" : $"{o.State}/{o.FailureReason ?? "无原因"}";
            bool dead = !MachineRegistry.TryGetRecord(victim, out MachineRecord vr) || !vr.IsAlive;
            WorkOrderRecord o2 = HomeExplorationService.ActiveOrder(s, a.Id);
            bool released = (o2 == null || o2.AssignedMachineLogicId != victim) && !HomeExplorationService.IsRecovered(s, a.Id)
                            && TechDataFlow.IncomeOf(s, TechDataFlow.Relic) == income0;
            string afterDeath = o2 == null ? $"原回收单结束（{orderAfter}），遗迹仍可回收" : $"回收单 {o2.State}、指派 {o2.AssignedMachineLogicId}";
            bool reorder = o2 != null || HomeExplorationService.OrderRecovery(s, a.Id, 0).Success;
            WorkOrderRecord o3 = HomeExplorationService.ActiveOrder(s, a.Id);
            bool done = StepUntil(() => HomeExplorationService.IsRecovered(s, a.Id), 900L * Hz);
            bool byOther = o3 != null && o3.State == WorkOrderState.Completed && o3.AssignedMachineLogicId != 0 && o3.AssignedMachineLogicId != victim;
            long paid = TechDataFlow.IncomeOf(s, TechDataFlow.Relic) - income0;
            WorldSimulation.StepMany(Hz * 5);
            bool once = paid == ia.Ring.TechData && TechDataFlow.IncomeOf(s, TechDataFlow.Relic) - income0 == paid;
            Expect(assigned && enRoute && dead && deathShown && released && reorder && done && byOther && once,
                $"R2 目标死亡（种子 7712，遗迹 {a.Id} 离核心 {a.Distance:0} 格）：右键派 #{victim} 去回收 {assigned}、在路上 {enRoute} → 机器被摧毁 {dead}（阵亡提示音 + 可定位失败通知“{failNote?.Latest?.DetailText}”{deathShown}）：{afterDeath}，不挂在死机器上、不发技术数据 {released} → " +
                $"{(o2 == null ? "重新下单进待分配池" : "工单回到待分配池")}，#{o3?.AssignedMachineLogicId} 接手回收完 {done && byOther}；技术数据只发一次 +{paid}（应 {ia.Ring.TechData}）");
        }

        // ── R3 断网 / 失联：静默夜（含 G1 命令栏） ───────────────────────────────────

        private static void CheckSignalLoss()
        {
            CampaignState s = NewWorld(7713);
            SilentNightState d = D(s);
            HomeValleyController home = WorldSimulation.Home;
            int[] ids = Haulers(home).Select(x => x.LogicId).Take(2).ToArray();
            if (ids.Length < 2)
            {
                Fail($"R3 家园搬运机少于 2 台（{ids.Length}）");
                return;
            }
            Vector2 core = SignalUplinkService.CorePosition(s);
            home.SquadCommands.DebugSelectMany(ids);
            home.SquadCommands.IssueMoveTo(core + new Vector2(6f, 4f), paused: false);
            string before = LastEvent(home);
            bool okBefore = before.Contains("已下达（" + ids.Length + " 台）");

            SkipToTick(s, d.StartTick - 2);
            bool night = StepUntil(() => d.Active, 10);
            Frames(2);
            UplinkRequestResult denied = SignalUplinkService.Request(ids[0], UplinkSource.MachineList, startCamera: false);
            int rej0 = RegionSquadCommandSystem.OutOfCoverageRejects;
            home.SquadCommands.DebugSelectMany(ids);
            home.SquadCommands.IssueMoveTo(core + new Vector2(-6f, 4f), paused: false);
            string nightEv = LastEvent(home);
            string sep = GameText.Get("signal.uplink.status.list_sep");
            string a = SignalPresence.MachineLabel(ids[0]), b = SignalPresence.MachineLabel(ids[1]);
            string n = ids.Length.ToString(CultureInfo.InvariantCulture);
            bool reasonLast = nightEv == GameText.Format("signal.command.silent_night", n, a + sep + b) || nightEv == GameText.Format("signal.command.silent_night", n, b + sep + a);
            bool g1 = reasonLast && !nightEv.Contains("已下达") && RegionSquadCommandSystem.OutOfCoverageRejects == rej0 + ids.Length;
            // 修复轮 1：暂停中下令（静默夜开始默认自动暂停）走 Issue 的暂停分支——同样不补“已排队（0 台）”、不放确认音。
            int ack0 = Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.CommandAck);
            int deny0 = Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied);
            home.SquadCommands.DebugSelectMany(ids);
            home.SquadCommands.IssueMoveTo(core + new Vector2(-6f, -4f), paused: true);
            string pausedEv = LastEvent(home);
            bool pausedReasonLast = pausedEv == GameText.Format("signal.command.silent_night", n, a + sep + b) || pausedEv == GameText.Format("signal.command.silent_night", n, b + sep + a);
            bool g1Paused = pausedReasonLast && !pausedEv.Contains("已排队")
                            && Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.CommandAck) == ack0
                            && Campaign.Feedback.FeedbackCues.CountOf(Campaign.Feedback.FeedbackCueId.Denied) == deny0 + 1;
            Expect(okBefore && night && !denied.Accepted && denied.Failure == UplinkFailure.SilentNight && g1 && g1Paused,
                $"R3 断网 / 失联（静默夜，种子 7713）：夜前右键移动“{before}”{okBefore}；静默夜里接入被拒“{denied.Text}”；右键移动 2 台全部被拒，命令栏最新一行是原因“{nightEv}”、" +
                $"不再补“已下达（0 台）”（G1：FGJ-M7 sn_move 抓到的产品修复）{g1}；暂停中右键移动同样只留原因“{pausedEv}”、不补“已排队（0 台）”、拒绝音 1 次、没有确认音 {g1Paused}");

            SkipToTick(s, d.EndTick - 2);
            bool ended = StepUntil(() => !d.Active, 10);
            StepUntil(() => !SignalLinkService.IsInSafeMode(s, ids[0]) && !SignalLinkService.IsInSafeMode(s, ids[1]), 6L * Hz);
            home.SquadCommands.DebugSelectMany(ids);
            home.SquadCommands.IssueMoveTo(core + new Vector2(6f, -4f), paused: false);
            string after = LastEvent(home);
            bool moveBack = after.Contains("已下达（" + ids.Length + " 台）") && home.SquadCommands.TryGetActiveCommandKind(ids[0], out RegionCommandKind k) && k == RegionCommandKind.Move;
            UplinkRequestResult up = SignalUplinkService.Request(ids[0], UplinkSource.MachineList, startCamera: false);
            int guard = 0;
            while (SignalUplinkService.IsPending && guard++ < 120)
            {
                Frames(1);
            }
            bool uplinked = up.Accepted && SignalPresence.CurrentMachineLogicId == ids[0];
            Expect(ended && moveBack && uplinked,
                $"R3 恢复：静默夜结束 {ended} → 右键移动照常下达“{after}”{moveBack}；接入 #{ids[0]} 成功 {uplinked}（{up.Text}）");
        }

        // ── R4 暂停与倍速：静默夜 + 酸雨同时进行 ─────────────────────────────────────

        private static void CheckPauseAndSpeed()
        {
            CampaignState s = NewWorld(7714);
            SilentNightState d = D(s);
            if (!ScheduleNightAndRain(s) || !EnterNightInRain(s))
            {
                Fail($"R4 没进入“静默夜 + 酸雨同时进行”（静默夜 {d.Active}、天气 {WeatherService.ActiveKind(s)}）");
                return;
            }
            WorldSimulation.StepMany(Hz * 3);
            WeatherFrontState wx = WeatherService.StateOf(s);
            GameClock.SetPaused(true);
            WorldSimulation.SyncAllForSave();
            string p0 = JsonUtility.ToJson(s) + JsonUtility.ToJson(d) + JsonUtility.ToJson(wx);
            long t0 = GameClock.Ticks;
            double rem0 = SilentNightService.RemainingSeconds(s);
            Frames(120);
            WorldSimulation.SyncAllForSave();
            bool held = JsonUtility.ToJson(s) + JsonUtility.ToJson(d) + JsonUtility.ToJson(wx) == p0 && GameClock.Ticks == t0 && SilentNightService.RemainingSeconds(s) == rem0;
            GameClock.SetPaused(false);
            var shown = new List<string>();
            bool byGameTime = true;
            var frameCounts = new Dictionary<float, int>();
            long seg = GameClock.TicksFor(3 * HourS / 60.0); // 3 个游戏分钟
            foreach (float speed in new[] { 0.5f, 3f })
            {
                GameClock.SetSpeed(speed);
                long from = GameClock.Ticks;
                double r0 = SilentNightService.RemainingSeconds(s);
                long acid0 = wx.AcidMinutes;
                long target = from + seg;
                int frames = 0;
                while (GameClock.Ticks < target && frames < 60 * 600)
                {
                    WorldSimulation.Frame(1f / 60f, target);
                    frames++;
                }
                long steps = GameClock.Ticks - from;
                double r1 = SilentNightService.RemainingSeconds(s);
                long acid = wx.AcidMinutes - acid0;
                bool ok = steps == seg && Math.Abs((r0 - r1) - steps / (double)Hz) < 1e-6 && acid >= 2 && acid <= 4 && d.Active && WeatherService.IsActive(s, WeatherService.AcidRain)
                          && Math.Abs(frames * speed * Hz / 60f - steps) <= 3f;
                byGameTime &= ok;
                frameCounts[speed] = frames;
                shown.Add($"{speed}x：{frames} 帧走 {steps} 步，静默夜剩余 {R(r0)}→{R(r1)} 秒，酸雨结算 +{acid} 个游戏分钟");
            }
            GameClock.SetSpeed(1f);
            double ratio = frameCounts[3f] > 0 ? frameCounts[0.5f] / (double)frameCounts[3f] : 0;
            Expect(held && byGameTime && Math.Abs(ratio - 6.0) < 0.2,
                $"R4 暂停与倍速（静默夜 + 酸雨同时进行，种子 7714）：暂停 120 帧整份战役状态 / 静默夜 / 天气逐字段不动、剩余时间不变 {held}；{string.Join("；", shown)}——都按游戏时间走，真实帧比 {ratio:0.00}（应 6）{byGameTime}");
        }

        // ── R5 存读档：静默夜 + 酸雨 + 净化塔缺冷却液 + 暂缓的施工单（FGT-ENV-008） ──────────

        private static string Snap(CampaignState s, string towerId, GridCell center, string ghostId, bool runtime = true)
        {
            BuildingRecord tower = HomeGridService.FindBuilding(s, towerId);
            ProductionService.Producer p = tower != null ? F.P(s, tower) : null;
            HomeGridMap map = HomeGridService.MapFor(s);
            WorkOrderRecord g = s.WorkOrders?.LastOrDefault(o => o != null && o.Kind == WorkOrderKind.Build && o.TargetId == ghostId);
            string pol = map.GetPollution(center).ToString(CultureInfo.InvariantCulture);
            return $"t={GameClock.Ticks}|silent={JsonUtility.ToJson(D(s))}|remain={R(SilentNightService.RemainingSeconds(s))}|active={SilentNightService.IsActive(s)}" +
                   $"|weather={JsonUtility.ToJson(WeatherService.StateOf(s))}|rain={WeatherService.IsActive(s, WeatherService.AcidRain)}" +
                   $"|purifier={(runtime ? p?.State + "/" + p?.Reason : "-")}/{p?.Rec.PurifyTicks}/{p?.Rec.PurifySupplied}|center={pol}|returned={map.ReturnedCells}|ghost={g?.State}/{g?.FailureReason}";
        }

        private static void CheckSaveLoad()
        {
            CampaignState s = NewWorld(7715);
            Pu.Rig rig = Pu.LayTower(s, "m7r5", feed: false);
            if (rig == null)
            {
                Fail("R5 找不到放净化塔的空地");
                return;
            }
            Pollute(s, rig.Pivot, PurificationService.RadiusOf(rig.Tower));
            string towerId = rig.Tower.BuildingId;
            if (!ScheduleNightAndRain(s) || !EnterNightInRain(s))
            {
                Fail($"R5 没进入“静默夜 + 酸雨同时进行”（静默夜 {D(s).Active}、天气 {WeatherService.ActiveKind(s)}）");
                return;
            }
            GridCell? at = F.FindFree(s, "power_pole", 6f, 22f);
            string ghost = null;
            if (at.HasValue)
            {
                GridOpResult placed = HomeGridService.TryPlace(s, "power_pole", at.Value, 0);
                ghost = placed.Outcome == GridOpResult.Kind.Placed ? placed.BuildingId : null;
            }
            WorldSimulation.StepMany(Hz * 2);
            string before = Snap(s, towerId, rig.Pivot, ghost);
            string beforeSaved = Snap(s, towerId, rig.Pivot, ghost, runtime: false);
            WorldSimulation.SyncAllForSave();
            SaveResult save = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            long until = GameClock.Ticks + GameClock.TicksFor(0.25 * HourS);
            // 生产状态（缺流体 / 原因）是运行时量，读档后第一次生产步才重新判定：读档那一刻比存档里的量，走一步后再比整份（含生产状态）。
            WorldSimulation.StepMany(1);
            string continuous1 = Snap(s, towerId, rig.Pivot, ghost);
            WorldSimulation.StepMany((int)(until - GameClock.Ticks));
            string continuous = Snap(s, towerId, rig.Pivot, ghost);
            string loaded = null, after = null;
            for (int round = 0; round < 2; round++)
            {
                WorldSimulation.UnloadAll();
                GameClock.ResetSession();
                HomeValleyPowerGrid.ResetForTests();
                ProductionService.ResetForTests();
                RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
                if (!rr.Success)
                {
                    Fail("R5 读档失败：" + rr.Message);
                    return;
                }
                CampaignState l = rr.State;
                CampaignSession.Set(Slot, l);
                HomeValleyController home = WorldSimulation.LoadHome(resume: true);
                WorldView.Observe(home.SiteId);
                string ld = Snap(l, towerId, rig.Pivot, ghost, runtime: false);
                WorldSimulation.StepMany(1);
                string ld1 = Snap(l, towerId, rig.Pivot, ghost);
                ld = ld == beforeSaved && ld1 == continuous1 ? before : ld + "\n读档后走一步 " + ld1 + "\n一直跑走一步 " + continuous1;
                WorldSimulation.StepMany((int)(until - GameClock.Ticks));
                string af = Snap(l, towerId, rig.Pivot, ghost);
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
            bool stateOk = before.Contains("active=True") && before.Contains("rain=True") && before.Contains("purifier=MissingFluid/NoCoolant/") && before.Contains("ghost=Waiting/" + SilentNightService.HoldReason);
            Expect(save.Success && ghost != null && stateOk && loaded == before && after == continuous,
                $"R5 存读档（FGT-ENV-008，种子 7715）：存档时静默夜 + 酸雨 + 净化塔缺冷却液 + 夜里放的施工单暂缓 {stateOk}；读档后逐项一致（静默夜窗口与剩余时间、天气段与酸雨结算进度、净化进度与缺液原因、归还面积、暂缓单）；" +
                "接着跑 0.25 游戏小时与不存档一直跑一致；同一存档读两次一致" + (stateOk ? string.Empty : $"\n存档时状态 {before}") +
                (loaded == before ? string.Empty : $"\n存档前 {before}\n读档后 {loaded}") + (after == continuous ? string.Empty : $"\n一直跑 {continuous}\n读档跑 {after}"));

            // 恢复：读档后接上冷却液，净化接着走（缺液暂停不是软锁）。
            CampaignState cur = CampaignSession.Current;
            BuildingRecord t2 = HomeGridService.FindBuilding(cur, towerId);
            ProductionService.Producer p2 = t2 != null ? F.P(cur, t2) : null;
            int steps0 = PurificationService.StepCount;
            Pu.Feed(rig, 20000);
            F.Resync(cur);
            bool resumed = p2 != null && StepUntil(() => PurificationService.StepCount > steps0, PurificationService.StepTicks + 3L * Hz + 400) && p2.Rec.PurifySupplied;
            Expect(resumed, $"R5 恢复：读档后接上冷却液，净化塔下一次降级照常发生（状态 {p2?.State}）");
        }

        // ── R6 路径失败：遗迹点被悬崖围住 ───────────────────────────────────────────

        private static void SetRing(CampaignState s, GridCell center, int r, byte terrain)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r)
                    {
                        map.SetTerrain(new GridCell(center.X + dx, center.Y + dy), terrain);
                    }
                }
            }
        }

        private static void CheckPathFailure()
        {
            CampaignState s = NewWorld(7716);
            RepairTowerAndEvaluate(s);
            HomeValleyController home = WorldSimulation.Home;
            List<WorldFeature> found = FoundRelics(s, 320);
            List<HomeValleyMachineMarker> haulers = Haulers(home);
            WorldFeature a = found.FirstOrDefault(f => f.Distance > 10f);
            if (a == null || haulers.Count == 0)
            {
                Fail($"R6 信号塔覆盖里找不到离核心 10 格外的遗迹点（{found.Count}）或搬运机（{haulers.Count}）");
                return;
            }
            HomeExplorationService.TryGetRelic(s, a.Id, out HomeExplorationService.RelicInfo ia);
            var center = new GridCell(a.X, a.Y);
            FgNavSelfCheck.CliffRing(s, center, 3);
            long income0 = TechDataFlow.IncomeOf(s, TechDataFlow.Relic);
            int notes0 = CountOf("unreachable");
            HomeValleyMachineMarker m = haulers[0];
            bool handled = home.ContextRelicForTests(m, RayAt(a));
            WorkOrderRecord o = HomeExplorationService.ActiveOrder(s, a.Id);
            bool failed = StepUntil(() => o != null && (HomeValleyWorkOrders.IsUnreachableReason(o.FailureReason) || o.FailureReason == HomeValleyWorkOrders.PathBlockedReason), 60L * Hz);
            NotificationEntry note = LastOf("unreachable");
            bool visible = failed && o.State == WorkOrderState.Waiting && o.AssignedMachineLogicId == 0 && !HomeExplorationService.IsRecovered(s, a.Id)
                           && TechDataFlow.IncomeOf(s, TechDataFlow.Relic) == income0
                           // 修复轮 1：不论哪条失败分支，都必须有一条可定位的“无法到达”通知（此前走 path-blocked 分支时不验通知）。
                           && CountOf("unreachable") == notes0 + 1 && note?.Latest != null && note.Latest.HasLocation;
            string why = o == null ? "没有回收单" : $"{o.State}/{o.FailureReason}";
            SetRing(s, center, 3, 0);
            bool done = StepUntil(() => HomeExplorationService.IsRecovered(s, a.Id), 240L * Hz);
            long paid = TechDataFlow.IncomeOf(s, TechDataFlow.Relic) - income0;
            Expect(handled && o != null && visible && done && paid == ia.Ring.TechData,
                $"R6 路径失败（种子 7716，遗迹 {a.Id} 四周围成悬崖）：右键派工 {handled} → 回收单“到不了”转等待、机器空出来（{why}），可定位通知“{note?.Latest?.DetailText}”、不发技术数据 {visible} → " +
                $"拆掉悬崖、30 秒重试 → 回收完 {done}，技术数据 +{paid}（应 {ia.Ring.TechData}）");
        }

        // ── 输出 ─────────────────────────────────────────────────────────────────

        private static string ReadRepo(string relative)
        {
            string root = F.LocateRepo();
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
            Debug.LogWarning("[M7 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
