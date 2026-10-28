using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameLogic.EditorTools.JourneyBots;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG10-E2E-01 自检 [M10 出口]：里程碑出口里能在编辑模式下逐语义断言的部分（正向旅程本身在 Play 里由 tools/unity-journey.sh FGJ-M10 跑）。
    /// 轻量口径（用户 2026-10-12）：IC-REQ-022 反向场景用起真实系统的单段自检覆盖——化工段的六类反向在本里程碑各 Story 的自检里都是真实系统
    /// （星球战斗内核、首领状态机、突袭导演与攻城、真文件存读档、远征行军）跑出来的断言，已并入全量自检；本段 R 守护这张映射：
    /// 每一类至少一条断言仍在源码里、所在段仍登记在全量自检，删掉或改名任一条都会让本段失败（被测行为本身由那些段在同一次全量自检里执行）。
    ///
    /// A. 旅程登记：FGJ-M10 覆盖里程碑原文（防腐涂层主线 → 进入沉淀湖 → 用化工自己的油迹打出爆燃 → 击败洗礼塔 → 回家应对化工突袭、重新净化），
    ///    另含 FG-GAP-163 沉淀湖区域片段；固定种子；断点都是旅程里的步骤；反应名与 ID 只在 FgjM10Journey.ReactionNames 一处（卡片承接，底稿 06 §3.4）。
    /// A2. FGJ-M10 源码只走输入通道；登记的夹具（DEBT-FG10E2E01-01～04）只在登记的方法里。
    /// R1～R6. IC-REQ-022 反向场景映射（资源 / 条件不足、目标死亡、断网 / 失联、暂停与倍速、存读档、路径失败）+ 本里程碑承接项（FG-GAP-161、DEBT-FG8EXP02-03、DEBT-FG10BOSS01-03）。
    /// C. 缺口清零：门禁第一个里程碑是 M10 的缺口全部 Closed / 部分关闭（剩余另有门禁）/ 顺延；本 Story 的登记与关闭在表里。
    /// D. 试玩包：FG-M10 四份材料齐全，目录登记“待用户试玩、不阻塞”。
    /// E. 需求覆盖（IC-REQ-020）：里程碑“必须完成”六项各有自检段且登记在全量自检，能进旅程的另有 FGJ-M10 步骤；澄净标志组件（FGR-FAC-009）顺延 FG-GAP-150。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgMilestoneM10SelfCheck）；单段：bash tools/unity-validate.sh --segment FgMilestoneM10SelfCheck。
    /// </summary>
    public static class FgMilestoneM10SelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        private const string Qa = "TEngine/UnityProject/Assets/Editor/QA/";
        private const string Bots = "TEngine/UnityProject/Assets/Editor/JourneyBots/";
        private const string Register = "production/design/full-game/FG-GAP-REGISTER.md";

        [MenuItem("BinGames/QA/自检/M10 出口")]
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
            Line("\n[M10 出口] FG10-E2E-01：旅程登记与正式输入、夹具守护、IC-REQ-022 六类反向（真实系统断言映射）与承接项、缺口清零、试玩包、里程碑需求覆盖");
            Step(CheckJourneyCatalog);
            Step(CheckJourneySourcesUseInput);
            Step(CheckReverseScenarios);
            Step(CheckGapGate);
            Step(CheckPlaytestPack);
            Step(CheckRequirementCoverage);
            Line($"  · [M10 出口] 断言通过 {_pass}，失败 {_fail}");
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

        private static void CheckJourneyCatalog()
        {
            Line("  · A. 旅程登记：FGJ-M10 覆盖里程碑原文与 FG-GAP-163 区域片段；固定种子；断点都是旅程里的步骤；反应名只有一处映射");
            JourneyDef m10 = JourneyCatalog.Create(FgjM10Journey.Id);
            var exit = new (string What, string[] Ids)[]
            {
                ("从主菜单“新建”出发、固定种子、3 倍速", new[] { "menu_new", "new_game", "seed", "speed3" }),
                ("完成防腐涂层主线（任务日志飞过去 → 护送货运车回家）", new[] { "ml_open", "ml_fly", "escort_spawn", "truck_sel", "truck_go" }),
                ("FG-GAP-163：无涂层右键酸沼被拒 → 再下同样的命令强行进入 → 退出", new[] { "gate_refuse", "gate_force", "gate_back" }),
                ("进入沉淀湖、第一次进领地的阵营介绍", new[] { "lake_in", "intro_uplink", "intro_exit" }),
                ("利用化工自己的油迹打出爆燃（湖边据点）", new[] { "op_fix", "op_go", "op_fight" }),
                ("击败洗礼塔", new[] { "boss_go", "boss_fight", "boss_won" }),
                ("回家应对一次化工突袭、重新净化被污染的区域", new[] { "home_fix", "go_home", "raid_warn", "raid_drop", "raid_fight", "repurify" }),
            };
            List<string> missing = exit.Where(x => m10 == null || x.Ids.Any(id => m10.Steps.All(s => s.Id != id))).Select(x => x.What).ToList();
            bool wellformed = m10 != null && m10.Steps.Count > 0 && m10.Steps.Select(x => x.Id).Distinct().Count() == m10.Steps.Count
                              && m10.Steps.All(x => x.TimeoutSeconds > 0 && x.Tick != null) && m10.TotalTimeoutSeconds > 0;
            Expect(wellformed && missing.Count == 0 && m10.Seed == FgjM0Journey.TestSeed,
                $"FGJ-M10：{m10?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m10?.Seed}；里程碑原文与区域片段都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            bool ck = m10 != null && m10.CheckpointAfter.Length >= 1 && m10.CheckpointAfter.All(id => m10.Steps.Any(s => s.Id == id));
            Expect(ck && JourneyCatalog.Ids.Contains(FgjM10Journey.Id) && JourneyCatalog.Ids.Contains(FgjM9Journey.Id),
                $"FGJ-M10 登记在旅程登记表、前序旅程仍在；断点 [{string.Join(", ", m10?.CheckpointAfter ?? Array.Empty<string>())}] 都是旅程里的步骤");
            // 反应名映射集中在一处：除 ReactionNames 那一段外，旅程源码不再出现 "reaction_" 字面量（FG-EMG 阶段 2 回改只改这一处）。
            string code = StripComments(ReadRepo(Bots + "FgjM10Journey.cs") ?? string.Empty);
            int mapAt = code.IndexOf("ReactionNames =", StringComparison.Ordinal);
            int mapEnd = mapAt < 0 ? -1 : code.IndexOf("};", mapAt, StringComparison.Ordinal);
            string outside = mapAt < 0 || mapEnd < 0 ? code : code.Remove(mapAt, mapEnd - mapAt);
            Expect(mapAt >= 0 && FgjM10Journey.ReactionNames.Any(x => x.Id == "reaction_deflagrate") && !outside.Contains("\"reaction_"),
                $"反应名与 ID 只在 FgjM10Journey.ReactionNames 一处映射（{FgjM10Journey.ReactionNames.Length} 条，含爆燃 reaction_deflagrate）；旅程其余源码没有散落的 \"reaction_\" 字面量");
        }

        // ── A2. 旅程源码只走输入通道 ─────────────────────────────────────────────────

        private static readonly string[] ForbiddenCalls =
        {
            "PlanHistory.Undo(", "PlanHistory.Place(", "HomeGridService.TryPlace", "HomeGridService.TryRemove", "CampaignSaveService.Save(", "UiConfirmDialog.Confirm(", "BuildingOps.Try",
            "GameClock.SetPaused(", "GameClock.SetSpeed(", "GameClock.SkipForTests(", "ResearchService.TryEnqueue", "ResearchService.CompleteForTests(", "SignalUplinkService.Request",
            "NotificationCenter.TryOpen(", "MachineRegistry.ApplyDamage(", ".IssueMoveTo(", ".IssueAttack(", ".DebugSelect", "ExpeditionDepartureService.TryDepart(",
            "ExpeditionMarchService.IssueCommand(", "Kernel.SetPosition(", "SetUnitPosition(", "Kernel.Damage(", "WorldView.FlyTo(", "WorldOutpostSystem.MarkDestroyed(",
            "SignalCoverageService.OverrideForTests", ".ApplyDamage(", "BaptismTowerBoss.TryTransition(", "PurificationService.Repollute(", "CampaignObjectiveTracker.Complete",
            "HomeInventory.Add(", "RaidDirectorService.RequestStoryRaid(", "WorldTransitSystem.Dispatch", "LootPickService.Grant(",
            // 夹具 token：只在登记的方法里免扫（见 Fixtures）。
            "MachineRegistry.SpawnMachine(", "TerritoryControlService.MarkLeaderDefeated(", "FgProductionSelfCheck.Built(", "WorldOutpostSystem.SpawnOutpost(",
            "FgPurificationSelfCheck.LayTower(", "FgProductionSelfCheck.PowerUp(",
        };

        /// <summary>夹具（ADR-QA-027 第 4 节）：token 只能出现在登记的方法体里，且恰好 N 处。</summary>
        private static readonly (string Token, string Method, int Count, string Debt)[] Fixtures =
        {
            ("s.Progress.Act = ", "ApplyFixture", 1, "开局第 2 幕 DEBT-FG10E2E01-01"),
            ("TerritoryControlService.MarkLeaderDefeated(", "ApplyFixture", 1, "铸造平定 DEBT-FG10E2E01-01"),
            ("FgProductionSelfCheck.Built(", "ApplyFixture", 2, "中继链 + 湖区硬放中继 DEBT-FG10E2E01-01 / -04"),
            ("MachineRegistry.SpawnMachine(", "ApplyFixture", 1, "远征队 8 台 DEBT-FG10E2E01-01"),
            ("MachineRegistry.SpawnMachine(", "ApplyHomeFixture", 1, "留守队 4 台 DEBT-FG10E2E01-02"),
            ("WorldOutpostSystem.SpawnOutpost(", "ApplyOutpostFixture", 1, "湖边据点 DEBT-FG10E2E01-03"),
            ("NextHarassTick = ", "ApplyOutpostFixture", 1, "推后一次领地骚扰 DEBT-FG10E2E01-03"),
            ("FgPurificationSelfCheck.LayTower(", "ApplyHomeFixture", 1, "净化塔测试台 DEBT-FG10E2E01-02"),
            ("FgProductionSelfCheck.PowerUp(", "ApplyHomeFixture", 1, "家园供电 DEBT-FG10E2E01-02"),
            ("f.ClarityRaidNextTick = ", "TickHomeFixture", 1, "快进首次延迟 DEBT-FG10E2E01-02"),
            ("plan.WarnTick -= ", "TickRaidWarned", 1, "快进最短间隔 DEBT-FG10E2E01-02"),
        };

        private static void CheckJourneySourcesUseInput()
        {
            Line("  · A2. FGJ-M10 源码只走输入通道；夹具（DEBT-FG10E2E01-01～04）只在登记的方法里");
            string text = ReadRepo(Bots + "FgjM10Journey.cs");
            if (text == null)
            {
                Fail("找不到旅程源码 FgjM10Journey.cs");
                return;
            }
            string code = StripComments(text);
            string scan = code;
            foreach (var (token, method, _, _) in Fixtures)
            {
                string body = MethodBody(scan, method);
                if (body != null)
                {
                    scan = scan.Replace(body, body.Replace(token, string.Empty));
                }
            }
            List<string> hits = ForbiddenCalls.Where(scan.Contains).ToList();
            Expect(hits.Count == 0, $"FGJ-M10 源码里没有业务方法调用（扫描 {ForbiddenCalls.Length} 种；登记夹具方法里的登记调用除外）{(hits.Count == 0 ? string.Empty : "；发现：" + string.Join("、", hits))}");
            var bad = new List<string>();
            var seen = new List<string>();
            foreach ((string token, string method, int count, string debt) in Fixtures)
            {
                int total = Count(code, token);
                string body = MethodBody(code, method);
                int n = body == null ? 0 : Count(body, token);
                seen.Add($"{debt}：{method} {n} 处");
                // 同一 token 可以登记在多个方法里：方法外的出现数 = 总数 − 所有登记方法里的出现数之和。
                int registered = Fixtures.Where(x => x.Token == token).Sum(x => { string b = MethodBody(code, x.Method); return b == null ? 0 : Count(b, token); });
                if (n != count || total != registered)
                {
                    bad.Add($"{token} 在 {method} 里 {n} 处（应 {count}）、登记方法外 {total - registered} 处");
                }
            }
            Expect(bad.Count == 0, $"夹具只在登记的方法里（{string.Join("；", seen)}）{(bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad))}");
            string[] inputs =
            {
                "FgjM3Common.TryClick(", "JourneyInput.ShiftClickScreen(", "FgjM1Journey.ClickMachineList(", "GameActionId.SpeedTriple", "GameActionId.ToggleMissionLog",
                "GameActionId.FocusHomeCore", "JourneyInput.ClickElement(",
            };
            List<string> miss = inputs.Where(x => !code.Contains(x)).ToList();
            Expect(miss.Count == 0, "旅程用到的正式输入：世界点击（左键选 / 右键地面移动 / 右键敌人攻击）、Shift 加选、机器列表、3 倍速键、任务日志键、回到核心键、UI Toolkit 面板点击" +
                                    (miss.Count == 0 ? string.Empty : "；缺：" + string.Join("、", miss)));
        }

        // ── R. IC-REQ-022 反向场景 ─────────────────────────────────────────────────

        private static void CheckReverseScenarios()
        {
            Line("  · R. IC-REQ-022 六类反向（化工段）：每类至少一条起真实系统的断言仍在源码里、所在段登记在全量自检；能进旅程的另有 FGJ-M10 步骤；承接项各有断言");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            var map = new (string Category, string Segment, string File, string Label)[]
            {
                ("R1 条件不足：没有防腐涂层时往湖心下令 → 拒绝并说明蓝图来源", "FgSettlingLakeSelfCheck", "FgSettlingLakeSelfCheck.cs", "C2 负向（卡片“没有防腐涂层时尝试进入”）"),
                ("R1 资源不足：没有净化塔覆盖的污染 → 路径写“在附近放一座净化塔”", "FgClarityRaidSelfCheck", "FgClarityRaidSelfCheck.cs", "D3 没有净化塔覆盖、还有污染的地方"),
                ("R2 目标死亡：洗礼塔场上机器全灭 → 复位", "FgBaptismTowerSelfCheck", "FgBaptismTowerSelfCheck.cs", "F2 负向“全灭”"),
                ("R2 目标死亡：全灭后从首领战前自动存档重试", "FgBaptismTowerSelfCheck", "FgBaptismTowerSelfCheck.cs", "F3 负向“全灭重试”"),
                ("R2 目标死亡：澄净突袭队伍全灭 → 剧场关闭、投放目标清空", "FgClarityRaidSelfCheck", "FgClarityRaidSelfCheck.cs", "D5 澄净队伍全灭"),
                ("R3 断网 / 失联：远征队走出覆盖先提醒、走出后提示（行军系统与第一幕同一套）", "FgMilestoneM8SelfCheck", "FgMilestoneM8SelfCheck.World.cs", "R3 断网 / 失联（FGR-SIG-053）"),
                ("R3 恢复：铺中继回到覆盖、能接入", "FgMilestoneM8SelfCheck", "FgMilestoneM8SelfCheck.World.cs", "R3 恢复：沿路线铺"),
                ("R4 暂停与倍速：洗礼塔灌注预警按游戏时间", "FgBaptismTowerSelfCheck", "FgBaptismTowerSelfCheck.cs", "H1 倍速矩阵"),
                ("R4 暂停与倍速：暂停时酸沼不扣耐久、各倍速同扣", "FgClarityFactionSelfCheck", "FgClarityFactionSelfCheck.cs", "G2 暂停时酸沼不扣耐久"),
                ("R4 暂停与倍速：再污染在暂停 / 0.5x～3x / 不观察下逐位一致", "FgClarityRaidSelfCheck", "FgClarityRaidSelfCheck.cs", "H1 暂停 1.5 秒世界不动"),
                ("R4 暂停与倍速：涂层写入与介绍在暂停时不发生", "FgSettlingLakeSelfCheck", "FgSettlingLakeSelfCheck.cs", "E1 暂停时涂层不写入"),
                ("R5 存读档：洗礼塔首领战中存档 → 读档一致", "FgBaptismTowerSelfCheck", "FgBaptismTowerSelfCheck.cs", "G1 首领战中"),
                ("R5 存读档：无人机投放前真文件存档 → 读档续跑一致", "FgClarityRaidSelfCheck", "FgClarityRaidSelfCheck.cs", "D4 投放前真文件存档"),
                ("R5 存读档：涂层与介绍读档不补发", "FgSettlingLakeSelfCheck", "FgSettlingLakeSelfCheck.cs", "E2 存读档"),
                ("R5 存读档：化工反应开放状态读档后仍在", "FgClarityReactionSelfCheck", "FgClarityReactionSelfCheck.cs", "F2 存读档"),
                ("R6 路径失败：被拒后改下不进酸沼的命令，再进酸沼仍先拒绝（不残留放行）", "FgSettlingLakeSelfCheck", "FgSettlingLakeSelfCheck.cs", "C2b 负向：被拒后先下一条不进酸沼的命令"),
                ("R6 路径失败：远征路线被截断 → 停下、提示、投送舱接回", "FgMilestoneM8SelfCheck", "FgMilestoneM8SelfCheck.World.cs", "R6 路径失败：锚点被悬崖围住"),
            };
            var bad = new List<string>();
            var cats = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string category, string segment, string file, string label) in map)
            {
                string src = ReadRepo(Qa + file);
                if (src == null || !src.Contains(label) || !segments.Contains(segment))
                {
                    bad.Add($"{category.Substring(0, 2)}（{segment} / “{label}”）");
                }
                else
                {
                    cats.Add(category.Substring(0, 2));
                }
            }
            Expect(bad.Count == 0 && cats.Count == 6, $"六类反向各有起真实系统的断言（{map.Length} 条，覆盖 {string.Join(" / ", cats.OrderBy(x => x))}）且所在段都登记在全量自检" +
                                                    (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
            JourneyDef m10 = JourneyCatalog.Create(FgjM10Journey.Id);
            bool journey = m10 != null && new[] { "gate_refuse", "gate_force", "gate_back", "raid_drop", "repurify" }.All(id => m10.Steps.Any(s => s.Id == id));
            Expect(journey, "能进正向旅程的反向在 FGJ-M10 里另有真实输入步骤：无涂层右键酸沼被拒（gate_refuse）→ 再下同样的命令强行进入（gate_force）→ 退出（gate_back）；再污染（raid_drop）→ 重新净化（repurify）");
            var carried = new (string What, string Segment, string File, string Label)[]
            {
                ("FG-GAP-161 ① 澄净领地据点战观察 / 不观察逐位一致", "FgClarityRaidSelfCheck", "FgClarityRaidSelfCheck.cs", "W1 FG-GAP-161 ①"),
                ("FG-GAP-161 ② 远征在外时澄净无人机投放 → 离家报告", "FgClarityRaidSelfCheck", "FgClarityRaidSelfCheck.cs", "W3 FG-GAP-161 ②"),
                ("DEBT-FG8EXP02-03 行军中的远征队站上酸沼带掉耐久、提示一次", "FgClarityRaidSelfCheck", "FgClarityRaidSelfCheck.cs", "W2 DEBT-FG8EXP02-03"),
                ("DEBT-FG10BOSS01-03 遭遇过洗礼塔后监听站给弱点情报", "FgBaptismTowerSelfCheck", "FgBaptismTowerSelfCheck.cs", "B1b 监听站首领弱点情报"),
            };
            List<string> lost = carried.Where(x => !segments.Contains(x.Segment) || !(ReadRepo(Qa + x.File) ?? string.Empty).Contains(x.Label)).Select(x => x.What).ToList();
            Expect(lost.Count == 0, $"承接项 {carried.Length} 条各有起真实系统的断言、所在段登记在全量自检{(lost.Count == 0 ? string.Empty : "；缺：" + string.Join("、", lost))}");
        }

        // ── C. 缺口清零 ────────────────────────────────────────────────────────────

        private static readonly Regex FirstMilestone = new Regex(@"M(\d+)");

        private static int FirstMilestoneOf(string s)
        {
            Match m = FirstMilestone.Match(s ?? string.Empty);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
        }

        private static bool PartialRemainderDeferred(string gate, string st)
        {
            if (!st.StartsWith("部分关闭", StringComparison.Ordinal) || !st.Contains("剩"))
            {
                return false;
            }
            foreach (Match m in FirstMilestone.Matches(gate ?? string.Empty))
            {
                if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 10)
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly string[] MineRegistered =
        {
            "DEBT-FG10E2E01-01", "DEBT-FG10E2E01-02", "DEBT-FG10E2E01-03", "DEBT-FG10E2E01-04", "DEBT-FG10E2E01-05", "DEBT-FG10E2E01-06",
            "FG-GAP-164", "FG-GAP-165", "FG-GAP-166", "FG-GAP-167", "FG-GAP-168",
        };

        private static readonly string[] ClosedHere = { "FG-GAP-161", "FG-GAP-163", "DEBT-FG10BOSS01-03" };

        private static void CheckGapGate()
        {
            Line("  · C. 里程碑出口：属于 FG-M10 的缺口全部 Closed / 部分关闭（剩余另有门禁）/ 顺延（按门禁列的第一个里程碑判定）；FG-GAP-149 已登记改承接 EMG1-E2E-01（门禁晚于 M10）");
            string text = ReadRepo(Register);
            if (text == null)
            {
                Fail("找不到 " + Register + "（仓库根定位失败）");
                return;
            }
            var openRows = new List<string>();
            int gapRows = 0, debtRows = 0, m10Rows = 0;
            var status = new Dictionary<string, string>(StringComparer.Ordinal);
            var gates = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                bool gap = raw.StartsWith("| FG-GAP-", StringComparison.Ordinal);
                if (!gap && !raw.StartsWith("| DEBT-", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] cols = raw.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                string st = cols[cols.Length - 1];
                status[cols[0]] = st;
                if (gap)
                {
                    gapRows++;
                }
                else
                {
                    debtRows++;
                }
                string gate = gap ? (cols.Length >= 7 ? cols[5] : null) : (cols.Length >= 9 ? cols[6] : null);
                gates[cols[0]] = gate ?? string.Empty;
                if (gate == null || FirstMilestoneOf(gate) != 10)
                {
                    continue;
                }
                m10Rows++;
                bool ok = st.StartsWith("Closed", StringComparison.Ordinal) || PartialRemainderDeferred(gate, st) || st.StartsWith("顺延", StringComparison.Ordinal);
                if (!ok)
                {
                    openRows.Add(cols[0]);
                }
            }
            Expect(gapRows > 100 && debtRows > 300 && m10Rows >= 8, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中门禁属于 FG-M10 的 {m10Rows} 行（解析没有漏行）");
            Expect(openRows.Count == 0,
                openRows.Count == 0
                    ? "属于 FG-M10 的缺口 / 延后项全部 Closed / 部分关闭（剩余另有门禁）/ 顺延（写明理由与新门禁）"
                    : $"FG-M10 出口前还开着：[{string.Join("、", openRows)}]");
            // FG-GAP-149（FGJ-M6R 编排）：总控 2026-10-28 预先认可的兜底——同一根因（FG-GAP-168 突袭结算少记撤离阶段阵亡的机器）两轮不过，改承接 EMG1-E2E-01（门禁 EMG-1，晚于 M10），待用户复核。
            bool g149 = status.TryGetValue("FG-GAP-149", out string s149) && gates.TryGetValue("FG-GAP-149", out string gate149)
                        && FirstMilestoneOf(gate149) < 0 && gate149.Contains("EMG-1") && s149.Contains("改承接 EMG1-E2E-01") && s149.Contains("总控已认可") && s149.Contains("待用户复核")
                        && status.ContainsKey("FG-GAP-168") && gates.TryGetValue("FG-GAP-168", out string gate168) && gate168.Contains("EMG-1");
            Expect(g149, "FG-GAP-149 已登记、门禁晚于 M10：改承接 EMG1-E2E-01（门禁 EMG-1，总控已认可、待用户复核），根因 FG-GAP-168 同门禁登记");
            List<string> notClosed = ClosedHere.Where(id => !status.TryGetValue(id, out string v) || !v.StartsWith("Closed", StringComparison.Ordinal)).ToList();
            List<string> unregistered = MineRegistered.Where(id => !status.ContainsKey(id)).ToList();
            bool pending = status.TryGetValue("DEBT-FG10E2E01-06", out string p) && p.Contains("待用户");
            Expect(unregistered.Count == 0 && notClosed.Count == 0 && pending,
                $"本 Story 的登记在表里（{MineRegistered.Length} 条：夹具 DEBT-FG10E2E01-01～05、试玩 -06 待用户、FG-GAP-164～168）；由本 Story 关闭的 {ClosedHere.Length} 条都是 Closed" +
                (unregistered.Count + notClosed.Count == 0 ? string.Empty : $"；没登记：{string.Join("、", unregistered)}；没关：{string.Join("、", notClosed)}"));
        }

        // ── D. 试玩包 ──────────────────────────────────────────────────────────────

        private static void CheckPlaytestPack()
        {
            Line("  · D. 试玩包：FG-M10 材料齐全（可选试玩，真人试玩由用户择时进行，DEBT-FG10E2E01-06，不阻塞）");
            const string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M10-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M10-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M10-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M10-问题清单模板.md");
            string readme = ReadRepo(dir + "README.md");
            bool exist = script != null && table != null && form != null && issues != null && readme != null;
            Expect(exist, "四份材料（试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板）与目录说明都在");
            if (!exist)
            {
                return;
            }
            int at = readme.IndexOf("## FG-M10 化工阵营", StringComparison.Ordinal);
            string section = at >= 0 ? readme.Substring(at) : string.Empty;
            bool registered = section.Contains("DEBT-FG10E2E01-06") && section.Contains("不阻塞") && section.Contains("FGJ-M10");
            bool covers = script.Contains("酸沼") && script.Contains("爆燃") && script.Contains("洗礼塔") && script.Contains("重新净化") && table.Contains("T8");
            Expect(registered && covers, $"目录登记“待用户试玩、不阻塞”并写明 FGJ-M10 断点存档（{registered}）；脚本覆盖里程碑原文五段、对照表 T1～T8（{covers}）");
        }

        // ── E. 需求覆盖 ────────────────────────────────────────────────────────────

        private static void CheckRequirementCoverage()
        {
            Line("  · E. 需求覆盖（IC-REQ-020）：FG-M10“必须完成”六项各有自检段且登记在全量自检，能进旅程的另有 FGJ-M10 步骤；澄净标志组件顺延 FG-GAP-150");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            JourneyDef m10 = JourneyCatalog.Create(FgjM10Journey.Id);
            var map = new (string What, string Segment, string Step)[]
            {
                ("化工单位与精英（FG10-FAC-01）", "FgClarityFactionSelfCheck", "op_fight"),
                ("沉淀湖领地模板与手工锚点：酸沼危害、防腐涂层进入条件（FG10-REG-01）", "FgSettlingLakeSelfCheck", "gate_refuse"),
                ("洗礼塔（FG10-BOSS-01）", "FgBaptismTowerSelfCheck", "boss_won"),
                ("化工反应开放（FG10-REACT-01，原批次口径）", "FgClarityReactionSelfCheck", "op_fight"),
                ("化工突袭与再污染（FG10-RAID-01）", "FgClarityRaidSelfCheck", "raid_drop"),
                ("澄净专属语法（FGR-FAC-008，FG10-FAC-01）", "FgClarityFactionSelfCheck", null), // 喷淋 / 枢轴 / 自爆 / 投放：自检逐项；旅程里据点战与突袭也会触发
            };
            var bad = new List<string>();
            foreach ((string what, string segment, string step) in map)
            {
                bool journeyOk = step == null || (m10?.Steps.Any(s => s.Id == step) ?? false);
                if (!segments.Contains(segment) || !journeyOk)
                {
                    bad.Add($"{what}（{segment} / {step ?? "无旅程步骤"}）");
                }
            }
            Expect(bad.Count == 0 && segments.Contains("FgMilestoneM10SelfCheck"),
                $"{map.Length} 项各有自检段且登记在全量自检，5 项另有 FGJ-M10 旅程步骤；[M10 出口] 本身登记在全量自检" + (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
            string reg = ReadRepo(Register) ?? string.Empty;
            string row = reg.Split('\n').FirstOrDefault(x => x.StartsWith("| FG-GAP-150 ", StringComparison.Ordinal)) ?? string.Empty;
            string[] cols = row.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
            bool deferred = cols.Length >= 7 && cols[2].Contains("FGR-FAC-009") && cols[4].Contains("澄净") && FirstMilestoneOf(cols[5]) > 10 && !cols[6].StartsWith("Closed", StringComparison.Ordinal);
            Expect(deferred, $"澄净标志组件（FGR-FAC-009 后半）没有实现：已在 FG-GAP-150 登记、承接写明澄净、门禁晚于 M10（{deferred}），不冒充已覆盖");
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private static int Count(string text, string token)
        {
            int n = 0;
            for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
            {
                n++;
            }
            return n;
        }

        /// <summary>取方法 “名字(JourneyContext” 之后的第一个方法体（按花括号配对；源码已去掉注释）。没有返回 null。</summary>
        private static string MethodBody(string code, string method)
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
            Debug.LogWarning("[M10 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
