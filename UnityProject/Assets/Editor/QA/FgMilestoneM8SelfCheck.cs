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
    /// FG8-E2E-01 自检 [M8 出口]：里程碑出口里能在编辑模式下逐语义断言的部分（正向旅程本身在 Play 里由 tools/unity-journey.sh FGJ-M8 跑）。
    /// 轻量口径（用户 2026-10-12）：IC-REQ-022 反向场景与卡片承接的旅程片段用起真实系统的单段自检覆盖——夹具只搭场景（放投送站、围悬崖、跳日程、
    /// 覆盖注入），被测行为全走真实系统（世界模拟、家园战斗内核与层级寻路、远征出发 / 行军 / 命令、投送舱、管线燃油、领地控制、遗迹工单、真文件存读档）。
    ///
    /// A. 旅程登记：FGJ-M8 覆盖里程碑原文八段；固定种子；断点都是旅程里的步骤。
    /// A2. 旅程源码只走输入通道（不直接调业务方法）；五处夹具（开局 / 精英残部 / 阵亡 / 投送装舱 / 支线种类）各在登记的方法里。
    /// A3. FGJ-M1～M6 的出发步骤与 FGJ-M8 共用同一个“跟随行军”检查（FgjM1Journey.TickDeparted），FGJ-M8 实跑通过即覆盖它（DEBT-FG8EXP02-07 部分）。
    /// R1～R6. IC-REQ-022 反向场景（每类“出错 → 看得到原因 → 恢复”）：
    ///   R1 资源不足——投送站没有燃油：呼叫回收被拒、写明要多少升 / 有多少升、站状态“没有燃油”、不改世界 → 站边接上装燃油的管线 → 回收成功、远征按“随投送舱返回”结算。
    ///   R2 目标死亡——单台远征、回收舱在飞时它阵亡：舱到了不接别的、不装货、空返并在舱列表写“回收点没有要接的了”，远征按全灭结算，它货舱里的东西掉在原地 → 再呼叫一次回收，货物舱把它捡回家园。
    ///   R3 断网 / 失联——远征队走出覆盖：先提醒、出去后“返回”被拒（原因）、不能接入、继续执行最后的“前进” → 沿路铺中继回到覆盖 → 接入 / 待命恢复。
    ///   R4 暂停与倍速（FGT-EXP-012）——行军 + 投送舱在飞同时进行：暂停 120 帧整体不动；0.5x / 3x 与一直不观察的无头推进到同一步逐字段一致，真实帧数与倍速成反比；镜头跟随 / 飞回家园不改变结果。
    ///   R5 存读档（FGT-EXP-011）——远征队在领地里行军、投送舱在飞时真文件存档：读档后逐字段一致；读档接着跑与不读档接着跑一致；读两次一致。
    ///   R6 路径失败——锚点被悬崖围住：行军“路线被截断”停下待命、提示原因、不瞬移不软锁 → 改用投送舱从截断点把队伍接回家。
    /// L1. 领地片段（DEBT-FG8GEN02-08）：种子 1 / 42 / 20261017 各一次——新世界 → 真实出发行军 → 进入阵营领地的名称提示 → 远征队走到室内入口被拒并写明原因（行军中不能进室内）
    ///     → 家园机器右键地面走到入口进入 → 走到出口离开回到地表。
    /// L2. 领地控制片段（DEBT-FG8EXP03-05）：按种子挑铸造 / 静默领地里驻军最少的据点 → 行军进入领地 → 战斗机走近据点、驻军迎战 → 走近后右键攻击真实交战摧毁、控制度下降
    ///     → 平定（领地首领被击败：铸造主核心 / 寂听主脑的首领 Story 入口）→ 收复 → 精英残部刷新、走近醒来迎战。
    /// L3. 遗产片段（DEBT-FG8EXP04-04）：按正式放置结果选第一幕范围（250～900 格）的一处遗迹 → 右键遗迹派机器走过去回收 → 遗产装进货舱 → 开回核心入库 → 保管库展柜 / 收集进度。
    /// C. 缺口清零：门禁第一个里程碑是 M8 的缺口全部 Closed / 部分关闭（剩余另有门禁）/ 顺延（写明新门禁）；本 Story 的登记与关闭在表里。
    /// D. 试玩包：FG-M8 四份材料齐全，目录登记“待用户试玩、不阻塞”。
    /// E. 需求覆盖（IC-REQ-020）：FG08 FGT-EXP-001～012 各有自检段且登记在全量自检，能进旅程的另有旅程步骤。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgMilestoneM8SelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.FgMilestoneM8SelfCheck.RunFromMenu。
    /// </summary>
    public static partial class FgMilestoneM8SelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        [MenuItem("BinGames/QA/自检/M8 出口")]
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
            Line("\n[M8 出口] FG8-E2E-01：旅程登记与正式输入、夹具守护、前序旅程出发步骤、IC-REQ-022 六类反向（真实系统）、领地 / 领地控制 / 遗产片段、缺口清零、试玩包、FGT-EXP 需求覆盖");
            Step(CheckJourneyCatalog);
            Step(CheckJourneySourcesUseInput);
            Step(CheckEarlierDepartures);
            Step(CheckGapGate);
            Step(CheckPlaytestPack);
            Step(CheckRequirementCoverage);
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过 R / L 段（会改写世界与存档目录）");
            }
            else
            {
                WorldChecks();
            }
            Line($"  · [M8 出口] 断言通过 {_pass}，失败 {_fail}");
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
            Line("  · A. 旅程登记：FGJ-M8 覆盖里程碑原文八段；固定种子；断点都是旅程里的步骤");
            JourneyDef m8 = JourneyCatalog.Create(FgjM8Journey.Id);
            var exit = new (string What, string[] Ids)[]
            {
                ("从主菜单“新建”出发、固定种子", new[] { "menu_new", "new_game", "seed" }),
                ("派遣检查单上看到“路线有一段走出信号覆盖”", new[] { "prep_open", "prep_pick", "chk_cov" }),
                ("沿途建一座信号中继塔（建成后检查单不再提醒）", new[] { "relay_plan", "build_open", "relay_place", "relay_built", "chk_cov2" }),
                ("远征队沿地形行进到破碎都市（跟随 + 3 倍速、途中进入领地）", new[] { "depart", "march_terr" }),
                ("击杀两个精英，攒下两个数据包（右键地面走近、右键敌人攻击）", new[] { "elite_fix", "team_sel", "elite_move", "elite_killed" }),
                ("数据包在野外战略暂停中分拣", new[] { "field_pause", "field_pick", "field_close" }),
                ("一台机器阵亡，货物掉落，由另一台捡回", new[] { "death_fix", "pick_sel", "pickup" }),
                ("在领地边缘建一座前哨站（中继、维修、炮塔）", new[] { "out_plan", "build_open2", "out_place", "build_close2", "outpost_built" }),
                ("用投送舱把满载的机器和伤员回收到家园（战略地图选回收点）", new[] { "drop_fix", "drop_open", "drop_pick", "drop_go", "recalled" }),
                ("回家处理数据包，货物按规则卸货", new[] { "home_pick", "home_close" }),
                ("完成一个支线任务（任务日志接取 → 建前哨 → 完成）", new[] { "q_fix", "q_open", "q_accept", "q_place", "q_done", "quest_done" }),
                ("等待段用正式倍速键", new[] { "speed3" }),
            };
            List<string> missing = exit.Where(x => m8 == null || x.Ids.Any(id => m8.Steps.All(s => s.Id != id))).Select(x => x.What).ToList();
            Expect(Wellformed(m8) && missing.Count == 0 && m8.Seed == FgjM8Journey.TestSeed,
                $"FGJ-M8：{m8?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m8?.Seed}；里程碑原文都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            bool ck = m8 != null && m8.CheckpointAfter.Length >= 1 && m8.CheckpointAfter.All(id => m8.Steps.Any(s => s.Id == id));
            Expect(ck && JourneyCatalog.Ids.Contains(FgjM8Journey.Id) && JourneyCatalog.Ids.Contains(FgjM7Journey.Id),
                $"FGJ-M8 登记在旅程登记表、前序旅程仍在；断点 [{string.Join(", ", m8?.CheckpointAfter ?? Array.Empty<string>())}] 都是旅程里的步骤");
        }

        // ── A2. 旅程源码只走输入通道 ─────────────────────────────────────────────────

        /// <summary>业务方法在旅程源码里出现 = 绕开了玩家输入（M7 的清单 + M8 的出发 / 行军命令 / 投送 / 掉落 / 任务 / 镜头 / 存档捷径）。</summary>
        private static readonly string[] ForbiddenCalls =
        {
            "PlanHistory.Undo(", "PlanHistory.Place(", "HomeGridService.TryPlace", "HomeGridService.TryRemove", "BeltNetworkService.TryPlace", "PipeNetworkService.TryPlace",
            "CampaignSaveService.Save(", "CampaignAutoSaveService.Save", "UiConfirmDialog.Confirm(", "BuildingOps.Try", "GameClock.SetPaused(", "GameClock.SetSpeed(", "GameClock.SkipForTests(",
            "ResearchService.TryEnqueue", "SignalUplinkService.Request", "NotificationCenter.TryOpen(", "MachineRegistry.ApplyDamage(", "HomeExplorationService.OrderRecovery(",
            "ContextRelicForTests(", ".IssueMoveTo(", ".IssueAttack(", ".DebugSelect", "MachineRoster.Try", "ExpeditionDepartureService.TryDepart(", "ExpeditionMarchService.IssueCommand(",
            "DropPodService.TryRecall(", "DropPodService.TryLaunch(", "LootPickService.Grant(", "HomeValleyCargo.SpawnGroundItem(", "Kernel.SetPosition(", "SetUnitPosition(",
            "WorldView.FlyTo(", "WorldView.ToggleFollowExpedition(", "CampaignObjectiveTracker.Accept", "TerritoryControlService.MarkLeaderDefeated(", "WorldOutpostSystem.MarkDestroyed(",
            "SignalCoverageService.OverrideForTests", "PlaceRelayChain(",
        };

        private static readonly string[] JourneyFiles = { "FgjM8Journey.cs", "FgjM8Journey.Expedition.cs", "FgjM8Journey.Field.cs", "FgjM8Journey.Home.cs" };

        /// <summary>五处夹具（ADR-QA-025 第 4 节）：只能出现在登记的方法里。</summary>
        private static readonly (string Token, string Method, int Count)[] Fixtures =
        {
            ("ResearchService.CompleteForTests(", "ApplyFixture", 1),
            ("MachineRegistry.SpawnMachine(", "ApplyFixture", 1),
            ("WorldOutpostSystem.SpawnRemnant(", "ApplyEliteFixture", 1),
            ("Kernel.Damage(", "ApplyDeathFixture", 1),
            ("DropPodService.LoadPods(", "ApplyDropFixture", 1),
            ("HeldFuelMl = ", "ApplyDropFixture", 1),
            ("CampaignObjectiveTracker.OfferSideForTests(", "ApplyQuestFixture", 1),
        };

        private static void CheckJourneySourcesUseInput()
        {
            Line("  · A2. FGJ-M8 源码只走输入通道；五处夹具（开局 / 精英残部 / 阵亡 / 投送装舱 / 支线种类）只在登记的方法里");
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
                seen.Add($"{token.Trim().TrimEnd('(', '=').Trim()} 在 {method} 里 {n} 处");
                if (n != count || total != n)
                {
                    bad.Add($"{token} 在 {method} 里 {n} 处（应 {count}）、方法外 {total - n} 处");
                }
            }
            Expect(bad.Count == 0, $"夹具只在登记的方法里（{string.Join("；", seen)}）{(bad.Count == 0 ? string.Empty : "；问题：" + string.Join("；", bad))}");
            string src = raw.ToString();
            string[] inputs =
            {
                "GameActionId.SpeedTriple", "GameActionId.TogglePause", "GameActionId.ToggleMissionLog", "GameActionId.Cancel", "GameActionId.Interact", "FgjM3Common.PressBuild(",
                "FgjM3Common.TryClick(ground, 1)", "JourneyInput.ClickElementAt(", "JourneyInput.ShiftClickScreen(", "JourneyInput.HoldAction(", "\"DepartButton\"", "\"WorldMarchHold\"",
                "\"WorldMarchAdvance\"",
            };
            List<string> miss = inputs.Where(x => !src.Contains(x)).ToList();
            Expect(miss.Count == 0,
                "旅程用到的正式输入：倍速 / 暂停 / 任务日志 / Esc / 交互按键，建造模式点击，右键（世界点击按钮 1：右键地面移动、右键敌人攻击），战略地图画布上点回收点，Shift 加选，按住 E 撤离，" +
                "远征准备面板“出发”、行军条“待命 / 前进”" + (miss.Count == 0 ? string.Empty : "；缺：" + string.Join("、", miss)));
        }

        // ── A3. 前序旅程的出发步骤（DEBT-FG8EXP02-07）──────────────────────────────

        private static void CheckEarlierDepartures()
        {
            Line("  · A3. FGJ-M1～M6 的出发步骤与 FGJ-M8 共用同一个“跟随行军”检查（FgjM1Journey.TickDeparted），FGJ-M8 实跑覆盖它（DEBT-FG8EXP02-07 部分）");
            var files = new (string Journey, string[] Files)[]
            {
                ("FGJ-M1", new[] { "FgjM1Journey.cs" }), ("FGJ-M2", new[] { "FgjM2Journey.cs" }), ("FGJ-M3", new[] { "FgjM3Journey.cs" }),
                ("FGJ-M4", new[] { "FgjM4Journey.Later.cs" }), ("FGJ-M5", new[] { "FgjM5Journey.Expedition.cs" }), ("FGJ-M6", new[] { "FgjM6Journey.Raids.cs" }),
                ("FGJ-M8", new[] { "FgjM8Journey.Expedition.cs" }),
            };
            var bad = new List<string>();
            foreach ((string journey, string[] list) in files)
            {
                string code = string.Concat(list.Select(f => StripComments(ReadRepo("TEngine/UnityProject/Assets/Editor/JourneyBots/" + f) ?? string.Empty)));
                if (!code.Contains("TickDeparted") || !code.Contains("DepartButton"))
                {
                    bad.Add(journey);
                }
            }
            string m3 = StripComments(ReadRepo("TEngine/UnityProject/Assets/Editor/JourneyBots/FgjM3Journey.cs") ?? string.Empty);
            string m4 = StripComments(ReadRepo("TEngine/UnityProject/Assets/Editor/JourneyBots/FgjM4Journey.Later.cs") ?? string.Empty);
            bool wrappers = m3.Contains("FgjM1Journey.TickDeparted(c)") && m4.Contains("FgjM1Journey.TickDeparted(c)");
            Expect(bad.Count == 0 && wrappers,
                "7 条旅程的出发都是“点远征准备面板的出发按钮 → FgjM1Journey.TickDeparted（点跟随远征队、3 倍速、跟着走到目的地）”，M3 / M4 的包装也先调它" +
                (bad.Count == 0 ? string.Empty : "；不一致：" + string.Join("、", bad)));
        }

        // ── C. 缺口清零 ────────────────────────────────────────────────────────────

        private static readonly Regex FirstMilestone = new Regex(@"M(\d+)");

        private static int FirstMilestoneOf(string s)
        {
            Match m = FirstMilestone.Match(s ?? string.Empty);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
        }

        /// <summary>门禁 M8 的行写“部分关闭”时，只有写明“剩……”且门禁列另有晚于 M8 的里程碑（剩余部分的门禁）才算 M8 出口合格。</summary>
        private static bool PartialRemainderDeferred(string gate, string st)
        {
            if (!st.StartsWith("部分关闭", StringComparison.Ordinal) || !st.Contains("剩"))
            {
                return false;
            }
            foreach (Match m in FirstMilestone.Matches(gate ?? string.Empty))
            {
                if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 8)
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly string[] MineRegistered =
        {
            "DEBT-FG8E2E01-01", "DEBT-FG8E2E01-02", "DEBT-FG8E2E01-03", "DEBT-FG8E2E01-04", "DEBT-FG8E2E01-05", "FG-GAP-136", "FG-GAP-137", "FG-GAP-138", "FG-GAP-139", "FG-GAP-140",
        };

        private static readonly string[] ClosedHere =
        {
            "DEBT-FG7E2E01-04", "DEBT-FG8GEN02-08", "DEBT-FG8EXP03-05", "DEBT-FG8EXP04-04", "DEBT-FG0ARCH01-05", "DEBT-FG0ARCH01-10", "DEBT-FG8LOOT01-07", "DEBT-FG8LOOT02-06",
            "DEBT-FG8LOOT03-04",
        };

        private static void CheckGapGate()
        {
            Line("  · C. 里程碑出口：属于 FG-M8 的缺口全部 Closed / 部分关闭（剩余另有门禁），延后项全部 Closed / 部分关闭 / 顺延（按门禁列的第一个里程碑判定）");
            string text = ReadRepo("production/design/full-game/FG-GAP-REGISTER.md");
            if (text == null)
            {
                Fail("找不到 production/design/full-game/FG-GAP-REGISTER.md（仓库根定位失败）");
                return;
            }
            var openRows = new List<string>();
            var partialRows = new List<string>();
            int gapRows = 0, debtRows = 0, m8Rows = 0;
            var status = new Dictionary<string, string>(StringComparer.Ordinal);
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
                if (gate == null || FirstMilestoneOf(gate) != 8)
                {
                    continue;
                }
                m8Rows++;
                if (st.StartsWith("部分关闭", StringComparison.Ordinal))
                {
                    partialRows.Add(cols[0]);
                }
                bool ok = st.StartsWith("Closed", StringComparison.Ordinal) || PartialRemainderDeferred(gate, st) || (!gap && st.StartsWith("顺延", StringComparison.Ordinal));
                if (!ok)
                {
                    openRows.Add(cols[0]);
                }
            }
            Line("    门禁属于 FG-M8、状态“部分关闭”的行：" + (partialRows.Count == 0 ? "无" : string.Join("、", partialRows)));
            Expect(gapRows > 100 && debtRows > 300 && m8Rows >= 10, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中门禁属于 FG-M8 的 {m8Rows} 行（解析没有漏行）");
            Expect(openRows.Count == 0,
                openRows.Count == 0
                    ? "属于 FG-M8 的缺口全部 Closed / 部分关闭（剩余部分另有门禁），延后项全部 Closed / 部分关闭 / 顺延（顺延写明理由与新门禁）"
                    : $"FG-M8 出口前还开着：[{string.Join("、", openRows)}]");
            List<string> notClosed = ClosedHere.Where(id => !status.TryGetValue(id, out string v) || !v.StartsWith("Closed", StringComparison.Ordinal)).ToList();
            List<string> unregistered = MineRegistered.Where(id => !status.ContainsKey(id)).ToList();
            bool pending = status.TryGetValue("DEBT-FG8E2E01-01", out string p1) && p1.Contains("待用户");
            Expect(unregistered.Count == 0 && notClosed.Count == 0 && pending,
                "本 Story 的登记在表里（DEBT-FG8E2E01-01 待用户试玩、-02～-05 旅程夹具与支线种类、FG-GAP-136～140 旅程与自检观察到的缺口）；" +
                $"由本 Story 关闭的 {ClosedHere.Length} 条都是 Closed" + (unregistered.Count + notClosed.Count == 0 ? string.Empty : $"；没登记：{string.Join("、", unregistered)}；没关：{string.Join("、", notClosed)}"));
        }

        // ── D. 试玩包 ──────────────────────────────────────────────────────────────

        private static void CheckPlaytestPack()
        {
            Line("  · D. 试玩包：FG-M8 材料齐全（真人试玩由用户择时进行，DEBT-FG8E2E01-01，不阻塞）");
            const string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M8-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M8-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M8-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M8-问题清单模板.md");
            string readme = ReadRepo(dir + "README.md");
            bool exist = script != null && table != null && form != null && issues != null && readme != null;
            Expect(exist, "四份材料（试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板）与目录说明都在");
            if (!exist)
            {
                return;
            }
            bool tasks = new[] { "检查单", "中继塔", "行军", "精英", "数据包", "阵亡", "前哨站", "投送舱", "卸货", "支线" }.All(script.Contains);
            bool standard = table.Contains("出远门") && table.Contains("越远越难管") && table.Contains("投送") && table.Contains("归还") && table.Contains("FGJ-M8") && table.Contains("不是门禁");
            bool questions = form.Contains("FG08") && form.Contains("中继塔") && form.Contains("投送") && form.Contains("收复") && form.Contains("DEBT-FG8E2E01-05");
            bool registered = readme.Contains("FG-M8") && readme.Contains("DEBT-FG8E2E01-01") && readme.Contains("不阻塞");
            Expect(tasks && standard && questions && registered,
                $"脚本按出口旅程给任务卡（{tasks}）；对照表把 FG08 第 11 节四问拆成可计数观察（出远门、越远越难管、投送与行军、收复的归还感；不是门禁，{standard}）；" +
                $"问卷对照 FG08 并带支线难度观察（DEBT-FG8E2E01-05，{questions}）；目录登记“待用户试玩、不阻塞”（{registered}）");
        }

        // ── E. 需求覆盖 ────────────────────────────────────────────────────────────

        private static void CheckRequirementCoverage()
        {
            Line("  · E. 需求覆盖（IC-REQ-020）：FG08 FGT-EXP-001～012 各有自检段且登记在全量自检，能进旅程的另有旅程步骤");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            const string qa = "TEngine/UnityProject/Assets/Editor/QA/";
            JourneyDef m8 = JourneyCatalog.Create(FgjM8Journey.Id);
            var map = new (string Test, string File, string Registered, string Step)[]
            {
                ("FGT-EXP-001", "FgExpeditionChecklistSelfCheck.cs", "FgExpeditionChecklistSelfCheck", "chk_cov"),
                ("FGT-EXP-002", "FgExpeditionMarchSelfCheck.cs", "FgExpeditionMarchSelfCheck", "march_terr"),
                ("FGT-EXP-003", "FgDropPodSelfCheck.cs", "FgDropPodSelfCheck", "recalled"),
                ("FGT-EXP-004", "FgOutpostSelfCheck.cs", "FgOutpostSelfCheck", "outpost_built"),
                ("FGT-EXP-005", "FgTerritoryControlSelfCheck.cs", "FgTerritoryControlSelfCheck", null), // 领地控制完整链：本段 L2（旅程只演到精英残部）
                ("FGT-EXP-006", "FgLootPickSelfCheck.cs", "FgLootPickSelfCheck", "field_pick"),
                ("FGT-EXP-007", "FgCargoSelfCheck.cs", "FgCargoSelfCheck", "pickup"),
                ("FGT-EXP-008", "FgExpeditionSettlementSelfCheck.cs", "FgExpeditionSettlementSelfCheck", "pickup"),
                ("FGT-EXP-009", "FgQuestSelfCheck.cs", "FgQuestSelfCheck", "quest_done"),
                ("FGT-EXP-010", "FgLootProtocolSelfCheck.cs", "FgLootProtocolSelfCheck", "home_pick"),
                ("FGT-EXP-011", "FgMilestoneM8SelfCheck.World.cs", "FgMilestoneM8SelfCheck", null), // 行军中 / 投送途中 / 领地内存读档：本段 R5
                ("FGT-EXP-012", "FgMilestoneM8SelfCheck.World.cs", "FgMilestoneM8SelfCheck", "march_terr"),
            };
            var bad = new List<string>();
            foreach ((string test, string file, string registered, string step) in map)
            {
                string src = ReadRepo(qa + file);
                bool journeyOk = step == null || (m8?.Steps.Any(s => s.Id == step) ?? false);
                if (src == null || !src.Contains(test) || !segments.Contains(registered) || !journeyOk)
                {
                    bad.Add($"{test}（{file} / {step ?? "无旅程步骤"}）");
                }
            }
            Expect(bad.Count == 0, $"{map.Length} 项自动验收各有自检段且登记在全量自检，10 项另有 FGJ-M8 旅程步骤（FGT-EXP-005 完整链、011 存读档由本段 L2 / R5 覆盖）" +
                                   (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
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

        /// <summary>取任意返回类型的方法 “名字(JourneyContext” 之后的第一个方法体（按花括号配对；源码已去掉注释）。没有返回 null。</summary>
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
            Debug.LogWarning("[M8 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
