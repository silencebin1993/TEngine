using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameLogic.Campaign.Economy;
using GameLogic.EditorTools.JourneyBots;
using GameLogic.Localization;
using GameLogic.Settings;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG11-E2E-01 自检 [M11 出口]：里程碑出口里能在编辑模式下逐语义断言的部分（正向旅程本身在 Play 里由 tools/unity-journey.sh FGJ-M11 跑）。
    /// 轻量口径 b（用户 2026-10-12）：IC-REQ-022 反向场景用起真实系统的单段自检覆盖——超频段的六类反向在 M11 各 Story 的自检里都是真实系统
    /// （星球战斗内核、余烬主机状态机、信号核槽位封锁、突袭导演与攻城、真文件存读档、远征行军）跑出来的断言，已并入全量自检；本段 R 守护这张映射：
    /// 每一类至少一条断言仍在源码里、所在段仍登记在全量自检，删掉或改名任一条都会让本段失败（被测行为本身由那些段在同一次全量自检里执行）。
    ///
    /// A. 旅程登记：FGJ-M11 覆盖里程碑原文（热寂数据城与冷却塔 → 镜像体与换固件 → 余烬主机槽位封锁后夺回 → 破译片段与超算残核 → 超控阵列 T3、信号核 5 槽）
    ///    与承接片段（FG-GAP-235 区域片段、FG-GAP-237 突袭片段）；固定种子；断点都是旅程里的步骤；镜像体 / 槽位封锁用共用事件计数（卡片“必须同时交付”）。
    /// A2. FGJ-M11 源码只走输入通道；登记的夹具（DEBT-FG11E2E01-01 / 02 / 03 / 05）只在登记的方法里、恰好 N 处。
    /// R1～R6. IC-REQ-022 六类反向映射 + 里程碑风险“槽位劫持后无法归还” + 承接项（FG-GAP-238 / 239）。
    /// C. 缺口清零：门禁第一个里程碑是 M11 的缺口全部 Closed / 部分关闭（剩余另有门禁）/ 顺延；本 Story 的登记与关闭在表里；夹具债改承接写进 FG15-QA-01 卡片正文。
    /// D. 试玩包：FG-M11 四份材料齐全，目录登记“待用户试玩、不阻塞”。
    /// E. 需求覆盖（IC-REQ-020）：里程碑“必须完成”各项有自检段且登记在全量自检，能进旅程的另有 FGJ-M11 步骤。
    /// F. DEBT-FG5RND05-04：第二幕三条舰队信号片段已定稿（中英文、按顺序、不提前写出“净化协议”），FG11 叙事文档有实现注记。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgMilestoneM11SelfCheck）；单段：bash tools/unity-validate.sh --segment FgMilestoneM11SelfCheck。
    /// </summary>
    public static class FgMilestoneM11SelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;

        private const string Qa = "TEngine/UnityProject/Assets/Editor/QA/";
        private const string Bots = "TEngine/UnityProject/Assets/Editor/JourneyBots/";
        private const string Register = "production/design/full-game/FG-GAP-REGISTER.md";
        private const string Cards = "production/design/full-game/FG-STORY-CARDS.md";

        [MenuItem("QA/自检/M11 出口")]
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
            Line("\n[M11 出口] FG11-E2E-01：旅程登记与共用计数、正式输入与夹具守护、IC-REQ-022 六类反向（真实系统断言映射）与承接项、缺口清零、试玩包、里程碑需求覆盖、第二幕舰队片段定稿");
            Step(CheckJourneyCatalog);
            Step(CheckJourneySourcesUseInput);
            Step(CheckReverseScenarios);
            Step(CheckGapGate);
            Step(CheckPlaytestPack);
            Step(CheckRequirementCoverage);
            Step(CheckFleetFragments);
            Line($"  · [M11 出口] 断言通过 {_pass}，失败 {_fail}");
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

        private static string JourneyCode() =>
            StripComments((ReadRepo(Bots + "FgjM11Journey.cs") ?? string.Empty) + "\n" + (ReadRepo(Bots + "FgjM11Journey.Later.cs") ?? string.Empty));

        // ── A. 旅程登记 ────────────────────────────────────────────────────────────

        private static void CheckJourneyCatalog()
        {
            Line("  · A. 旅程登记：FGJ-M11 覆盖里程碑原文与 FG-GAP-235 / 237 片段；固定种子；断点都是旅程里的步骤；镜像体与槽位封锁用共用事件计数");
            JourneyDef m11 = JourneyCatalog.Create(FgjM11Journey.Id);
            var exit = new (string What, string[] Ids)[]
            {
                ("从主菜单“新建”出发、固定种子、3 倍速", new[] { "menu_new", "new_game", "seed", "speed3" }),
                ("进入热寂数据城、利用冷却塔管理热量（辐射区 HUD 写上限 -25% → 塔旁恢复）", new[] { "heat_in", "heat_up", "tower_go", "tower_up" }),
                ("FG-GAP-235：打掉一座塔 → HUD 回落、范围圈消失", new[] { "tower_attack", "tower_down", "tower_up2" }),
                ("遭遇镜像体，换一套固件应对", new[] { "garrison_fix", "mirror_copy", "sc_open", "sc_swap", "sc_close", "garrison_fight" }),
                ("击败余烬主机：槽位被封锁后夺回", new[] { "gate_enter", "boss_fight", "boss_won" }),
                ("拿到破译片段和超算残核、带出数据中心", new[] { "boss_won", "dc_out" }),
                ("超控阵列升到 T3、信号核 5 槽", new[] { "arr_open", "arr_up", "arr_close" }),
                ("FG-GAP-237：超频突袭正式预警 → 到达先扑发电设施 → 结算", new[] { "raid_warn", "raid_hit", "raid_fight" }),
            };
            List<string> missing = exit.Where(x => m11 == null || x.Ids.Any(id => m11.Steps.All(s => s.Id != id))).Select(x => x.What).ToList();
            bool wellformed = m11 != null && m11.Steps.Count > 0 && m11.Steps.Select(x => x.Id).Distinct().Count() == m11.Steps.Count
                              && m11.Steps.All(x => x.TimeoutSeconds > 0 && x.Tick != null) && m11.TotalTimeoutSeconds > 0;
            Expect(wellformed && missing.Count == 0 && m11.Seed == FgjM0Journey.TestSeed,
                $"FGJ-M11：{m11?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m11?.Seed}；里程碑原文与承接片段都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            bool ck = m11 != null && m11.CheckpointAfter.Length >= 1 && m11.CheckpointAfter.All(id => m11.Steps.Any(s => s.Id == id));
            Expect(ck && JourneyCatalog.Ids.Contains(FgjM11Journey.Id) && JourneyCatalog.Ids.Contains(FgjM10Journey.Id),
                $"FGJ-M11 登记在旅程登记表、前序旅程 FGJ-M10 仍在；断点 [{string.Join(", ", m11?.CheckpointAfter ?? Array.Empty<string>())}] 都是旅程里的步骤");
            // 共用事件计数：旅程与自检读同一个计数（镜像复制 / 槽位封锁 / 夺回），不在旅程里另造判定。
            string code = JourneyCode();
            string faction = ReadRepo(Qa + "FgOverclockFactionSelfCheck.cs") ?? string.Empty;
            string ember = ReadRepo(Qa + "FgEmberHostSelfCheck.cs") ?? string.Empty;
            bool shared = code.Contains("CombatSite.MirrorDetectCopies") && code.Contains("SignalSeizeService.SlotBlockedCount") && code.Contains("SignalSeizeService.SlotRestoredCount")
                          && faction.Contains("MirrorDetectCopies") && ember.Contains("SignalSeizeService.SlotBlockedCount - blockedCount0") && ember.Contains("SignalSeizeService.SlotRestoredCount - restoredCount0");
            Expect(shared, "镜像体与槽位封锁的断言用共用事件计数：FGJ-M11（mirror_copy / boss_fight）与自检 FgOverclockFactionSelfCheck F 段（MirrorDetectCopies）、FgEmberHostSelfCheck C1 / C2（SlotBlockedCount / SlotRestoredCount 各 +1）读同一计数");
        }

        // ── A2. 旅程源码只走输入通道 ─────────────────────────────────────────────────

        private static readonly string[] ForbiddenCalls =
        {
            "PlanHistory.Undo(", "PlanHistory.Place(", "HomeGridService.TryPlace", "HomeGridService.TryRemove", "CampaignSaveService.Save(", "UiConfirmDialog.Confirm(", "BuildingOps.Try",
            "GameClock.SetPaused(", "GameClock.SetSpeed(", "GameClock.SkipForTests(", "ResearchService.TryEnqueue", "ResearchService.CompleteForTests(", "SignalUplinkService.Request",
            "NotificationCenter.TryOpen(", "MachineRegistry.ApplyDamage(", ".IssueMoveTo(", ".IssueAttack(", ".DebugSelect", "ExpeditionDepartureService.TryDepart(",
            "ExpeditionMarchService.IssueCommand(", "Kernel.SetPosition(", "SetUnitPosition(", "Kernel.Damage(", "WorldView.FlyTo(", "WorldOutpostSystem.MarkDestroyed(",
            "SignalCoverageService.OverrideForTests", ".ApplyDamage(", "CampaignObjectiveTracker.Complete", "HomeInventory.Add(", "RaidDirectorService.RequestStoryRaid(",
            "WorldTransitSystem.Dispatch", "LootPickService.Grant(", "SignalSeizeService.BlockSlot(", "SignalSeizeService.UnblockSlot(", "EmberHostBoss.Try", "CoolingTowerService.MarkDestroyed(",
            "CombatSite.MirrorCopy(", "NarrativeService.Grant(",
            // 夹具 token：只在登记的方法里免扫（见 Fixtures）。
            "s.Progress.Act = ", "TerritoryControlService.MarkLeaderDefeated(", "CampaignEventLedger.TryGrant(", "BlueprintEditorService.TrySave(", "MachineRegistry.SpawnMachine(",
            "PrimitiveInventory.Grant", "SignalCoreService.TryEquip(", "HomeGridService.RevealArea(", "RelayChain(", "FgProductionSelfCheck.Built(", "FgProductionSelfCheck.PowerUp(",
            "CampaignExposureLedger.GrantRawFire(", "s.Research.CompletedNodes = ", "arr.Health = ", "= BossFixtureHealth", "EnemySpawnBuilder.", "plan.WarnTick -= ", "plan.DepartTick -= ",
        };

        /// <summary>夹具（ADR-QA-028 第 4 节）：token 只能出现在登记的方法体里，且恰好 N 处。</summary>
        private static readonly (string Token, string Method, int Count, string Debt)[] Fixtures =
        {
            ("s.Progress.Act = ", "ApplyFixture", 1, "开局第 2 幕 DEBT-FG11E2E01-01"),
            ("TerritoryControlService.MarkLeaderDefeated(", "ApplyFixture", 1, "铸造 / 澄净 / 静默平定 DEBT-FG11E2E01-01"),
            ("CampaignEventLedger.TryGrant(", "ApplyFixture", 1, "防腐涂层蓝图入账 DEBT-FG11E2E01-01"),
            ("BlueprintEditorService.TrySave(", "ApplyFixture", 1, "重炮燃迹接入口蓝图 DEBT-FG11E2E01-01"),
            ("MachineRegistry.SpawnMachine(", "ApplyFixture", 1, "远征队 4 台 DEBT-FG11E2E01-01"),
            ("PrimitiveInventory.Grant", "ApplyFixture", 2, "过载 + 标记跳转芯片 DEBT-FG11E2E01-01"),
            ("SignalCoreService.TryEquip(", "ApplyFixture", 1, "1 号槽装过载 DEBT-FG11E2E01-01"),
            ("HomeGridService.RevealArea(", "ApplyFixture", 2, "揭雾 DEBT-FG11E2E01-01"),
            ("RelayChain(", "ApplyFixture", 1, "中继链（每座过正式放置校验）DEBT-FG11E2E01-01"),
            ("EnemySpawnBuilder.", "Spawn", 1, "超频驻军（正式敌人表）DEBT-FG11E2E01-02"),
            ("PrimitiveInventory.Grant", "ApplyBossFixture", 1, "2 号槽第二枚过载 DEBT-FG11E2E01-03"),
            ("SignalCoreService.TryEquip(", "ApplyBossFixture", 1, "2 号槽装过载 DEBT-FG11E2E01-03"),
            ("= BossFixtureHealth", "ApplyBossFixture", 2, "远征队耐久 1500 DEBT-FG11E2E01-03"),
            ("HomeGridService.RevealArea(", "ApplyBossFixture", 1, "揭雾 DEBT-FG11E2E01-03"),
            ("RelayChain(", "ApplyBossFixture", 1, "链尾中继 DEBT-FG11E2E01-03"),
            ("FgProductionSelfCheck.Built(", "SignalOutpost", 3, "链尾 T2 信号塔 + 发电机 + 电塔 DEBT-FG11E2E01-03"),
            ("HomeGridService.RevealArea(", "SignalOutpost", 1, "揭雾 DEBT-FG11E2E01-03"),
            ("s.Research.CompletedNodes = ", "ApplyHomeFixture", 1, "三级研究记完成 DEBT-FG11E2E01-05"),
            ("FgProductionSelfCheck.Built(", "ApplyHomeFixture", 3, "超控阵列 T2 / 监听站 / 来路防线 DEBT-FG11E2E01-05"),
            ("arr.Health = ", "ApplyHomeFixture", 1, "阵列满耐久 DEBT-FG11E2E01-05"),
            ("FgProductionSelfCheck.PowerUp(", "ApplyHomeFixture", 1, "家园供电 DEBT-FG11E2E01-05"),
            ("MachineRegistry.SpawnMachine(", "ApplyHomeFixture", 1, "留守队 4 台 DEBT-FG11E2E01-05"),
            ("CampaignExposureLedger.GrantRawFire(", "ApplyHomeFixture", 1, "超频暴露按真实账本抬过 30 DEBT-FG11E2E01-05"),
            ("plan.WarnTick -= ", "TickOcRaidWarned", 1, "快进超频计划预警 DEBT-FG11E2E01-05"),
            ("plan.DepartTick -= ", "TickOcRaidWarned", 1, "快进超频计划出发 DEBT-FG11E2E01-05"),
        };

        private static void CheckJourneySourcesUseInput()
        {
            Line("  · A2. FGJ-M11 源码只走输入通道；夹具（DEBT-FG11E2E01-01 / 02 / 03 / 05）只在登记的方法里");
            string code = JourneyCode();
            if (!code.Contains("class FgjM11Journey"))
            {
                Fail("找不到旅程源码 FgjM11Journey.cs / FgjM11Journey.Later.cs");
                return;
            }
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
            Expect(hits.Count == 0, $"FGJ-M11 源码里没有业务方法调用（扫描 {ForbiddenCalls.Length} 种；登记夹具方法里的登记调用除外）{(hits.Count == 0 ? string.Empty : "；发现：" + string.Join("、", hits))}");
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
                "FgjM3Common.TryClick(", "JourneyInput.ShiftClickScreen(", "FgjM10Journey.TickSelectTeam", "FgjM1Journey.ClickMachineList(", "GameActionId.SpeedTriple",
                "GameActionId.ToggleCameraView", "GameActionId.OpenSignalCore", "GameActionId.CommandAttack", "GameActionId.FocusHomeCore", "FgjM4Common.TickOpenPanel(",
                "FgjM3Common.ClickPanelBodyButton(", "FgjM5Common.TickEquipChip(",
            };
            List<string> miss = inputs.Where(x => !code.Contains(x)).ToList();
            Expect(miss.Count == 0, "旅程用到的正式输入：世界点击（左键选 / 右键地面移动 / 右键敌人攻击）、Shift 加选、机器列表接入、3 倍速键、接入 / 退出键、信号核键、攻击键 + 左键、回到核心键、建造模式点建筑开面板、面板按钮、信号核换芯片" +
                                    (miss.Count == 0 ? string.Empty : "；缺：" + string.Join("、", miss)));
        }

        // ── R. IC-REQ-022 反向场景 ─────────────────────────────────────────────────

        internal static readonly (string Category, string Segment, string File, string Label)[] ReverseMap =
        {
            ("R1 条件不足：40 米外发动核心固件 → 镜像体不复制", "FgOverclockFactionSelfCheck", "FgOverclockFactionSelfCheck.cs", "F1 FGR-EMG-164：40 米外发动核心固件不复制"),
            ("R1 条件不足：第一幕超频领地还没出现 → 贡献全是超频也不由超频发动突袭", "FgOverclockRaidSelfCheck", "FgOverclockRaidSelfCheck.cs", "B2 负向：第一幕超频领地还没出现"),
            ("R1 条件不足：没有防腐涂层时 2 级污染上建中继被拦并写明涂层；3 级照旧被拦并写明出路", "FgHeatDataCitySelfCheck", "FgHeatDataCitySelfCheck.cs", "D1 FG-GAP-164（第 3 项"),
            ("R1 条件不足：只有一支 / 没有远征在外时“切换远征队”被拒", "FgMultiExpeditionSelfCheck", "FgExpeditionMarchSelfCheck.cs", "X1 两支分头出发（FG-GAP-016）"),
            ("R2 目标死亡：余烬主机战场上机器全灭 → 数据中心卸载、首领复位、可重试", "FgEmberHostSelfCheck", "FgEmberHostSelfCheck.cs", "E1 负向“全灭重试”"),
            ("R2 目标死亡：熔毁倒计时中击毁余烬主机 → 不再熔毁、没有残留封锁", "FgEmberHostSelfCheck", "FgEmberHostSelfCheck.cs", "D1 负向：倒计时第 20 秒击毁"),
            ("R2 目标死亡：熔断者熔断中被一口气打死不爆", "FgOverclockFactionSelfCheck", "FgOverclockFactionSelfCheck.cs", "B5 负向：熔断中被一口气打死不爆"),
            ("R2 目标死亡：冷却塔被摧毁 → 塔旁回到辐射上限", "FgHeatDataCitySelfCheck", "FgHeatDataCitySelfCheck.cs", "C2 负向（冷却塔被摧毁）"),
            ("R3 失联：余烬主机封锁信号核槽位 → 槽里固件失效、程序树重排、写原因", "FgEmberHostSelfCheck", "FgEmberHostSelfCheck.cs", "C1 FGT-FAC-006 / FGT-EMG-061 阶段二·槽位封锁"),
            ("R3 恢复：摧毁尖刺夺回槽位（拆 2 根仍封锁），同签名、冷却不重置", "FgEmberHostSelfCheck", "FgEmberHostSelfCheck.cs", "C2 摧毁尖刺夺回槽位"),
            ("R3 反夺控：装反夺控的机器挡住正式来源的封锁", "FgEmberHostSelfCheck", "FgEmberHostSelfCheck.cs", "I1 DEBT-EMG4FW01-01 反夺控正式入口"),
            ("R3 断网：远征队走出覆盖先提醒、走出后提示（行军系统与第一幕同一套）", "FgMilestoneM8SelfCheck", "FgMilestoneM8SelfCheck.World.cs", "R3 断网 / 失联（FGR-SIG-053）"),
            ("R4 暂停与倍速：暂停时熔断者不爆、0.5x～3x 同按游戏秒引爆", "FgOverclockFactionSelfCheck", "FgOverclockFactionSelfCheck.cs", "H1 暂停 6 秒熔断者不爆"),
            ("R4 暂停与倍速：暂停中打掉冷却塔、各倍速结算一致", "FgHeatDataCitySelfCheck", "FgHeatDataCitySelfCheck.cs", "C3 暂停 / 倍速"),
            ("R4 暂停与倍速：攻城中接触熔断的引信按游戏秒", "FgOverclockRaidSelfCheck", "FgOverclockRaidSelfCheck.cs", "E1 接触熔断的引信按游戏秒"),
            ("R4 暂停与倍速：余烬主机计时按游戏时间、观察与不观察一致", "FgEmberHostSelfCheck", "FgEmberHostSelfCheck.cs", "G1 B09 / FGR-BASE-021"),
            ("R4 暂停与倍速：暂停时注电不电解、状态哈希不变", "FgOverclockReactionSelfCheck", "FgOverclockReactionSelfCheck.cs", "暂停（0 秒步）注电"),
            ("R5 存读档：余烬主机阶段二中途存档 → 读档一致", "FgEmberHostSelfCheck", "FgEmberHostSelfCheck.cs", "F1 阶段二中途存档"),
            ("R5 存读档：余烬主机阶段三中途存档 → 熔毁倒计时接续", "FgEmberHostSelfCheck", "FgEmberHostSelfCheck.cs", "F2 阶段三中途存档"),
            ("R5 存读档：熔断中真文件存档 → 读档接着跑", "FgOverclockRaidSelfCheck", "FgOverclockRaidSelfCheck.cs", "F1 熔断中真文件存档"),
            ("R5 存读档：镜像复制品存读档", "FgOverclockFactionSelfCheck", "FgOverclockFactionSelfCheck.cs", "F3 DEBT-EMG4HIJ02-02：复制 → 存读档"),
            ("R5 存读档：两支远征在外时存读档", "FgMultiExpeditionSelfCheck", "FgExpeditionMarchSelfCheck.cs", "X4 两支在外时存读档（B10）"),
            ("R6 路径失败：敌方突袭者追击 L 形墙后的目标 → 寻路绕过去（FG-GAP-223）", "FgNavSelfCheck", "FgNavSelfCheck.cs", "FG-GAP-223 敌方突袭者追击 L 形墙后的目标"),
            ("R6 路径失败：目标被围死 → 放弃追击后限频重选（不刷寻路）", "FgNavSelfCheck", "FgNavSelfCheck.cs", "FG-GAP-223 围死目标"),
            ("R6 路径失败：远征地点被占 → 第 2 队在集结点等候", "FgMultiExpeditionSelfCheck", "FgExpeditionMarchSelfCheck.cs", "X3 同一时刻只能载入一个远征地点"),
            ("R6 路径失败：远征路线被截断 → 停下、提示、投送舱接回", "FgMilestoneM8SelfCheck", "FgMilestoneM8SelfCheck.World.cs", "R6 路径失败：锚点被悬崖围住"),
        };

        private static void CheckReverseScenarios()
        {
            Line("  · R. IC-REQ-022 六类反向（超频段）：每类至少一条起真实系统的断言仍在源码里、所在段登记在全量自检；里程碑风险“槽位劫持后无法归还”；承接项各有断言");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            var map = ReverseMap;
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
            // 里程碑风险“槽位劫持后无法归还”：夺回、击毁、全灭三条出口都清掉封锁，断言原文要写明。
            string ember = ReadRepo(Qa + "FgEmberHostSelfCheck.cs") ?? string.Empty;
            bool release = ember.Contains("拆完 3 根 → 立即恢复") && ember.Contains("没有残留封锁") && ember.Contains("封锁解除") && segments.Contains("FgEmberHostSelfCheck");
            JourneyDef m11 = JourneyCatalog.Create(FgjM11Journey.Id);
            bool journey = m11 != null && new[] { "boss_fight", "boss_won", "tower_down", "tower_up2" }.All(id => m11.Steps.Any(s => s.Id == id));
            Expect(release && journey, "里程碑风险“槽位劫持后无法归还”：夺回（C2 拆完尖刺立即恢复）、击毁（D1 没有残留封锁）、全灭（E1 首领复位、封锁解除）三条出口都有断言；FGJ-M11 boss_fight 真实输入走过封锁 → 夺回，boss_won 断言封锁全部解除；目标死亡的玩家可见反向（塔被打掉 → HUD 回落）在 tower_down / tower_up2");
            var carried = new (string What, string Segment, string File, string Label)[]
            {
                ("FG-GAP-238 冷却塔被明火烧（结构单位按占地接触斑块）", "FgHeatDataCitySelfCheck", "FgHeatDataCitySelfCheck.cs", "C4 冷却塔被明火烧（FG-GAP-238）"),
                ("FG-GAP-239 3 级污染上中继被拦时写明正式出路", "FgHeatDataCitySelfCheck", "FgHeatDataCitySelfCheck.cs", "并写明出路（FG-GAP-239）"),
                ("FG-GAP-237 预警有情报时写“高速突击”特点", "FgOverclockRaidSelfCheck", "FgOverclockRaidSelfCheck.cs", "B3 预警：有情报时行与悬停都写“高速突击”特点"),
                ("FG-GAP-237 熔断者离墙远时不熔断、顶墙集体自爆", "FgOverclockRaidSelfCheck", "FgOverclockRaidSelfCheck.cs", "D2 集体自爆"),
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
                if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 11)
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly string[] MineRegistered =
        {
            "DEBT-FG11E2E01-01", "DEBT-FG11E2E01-02", "DEBT-FG11E2E01-03", "DEBT-FG11E2E01-04", "DEBT-FG11E2E01-05", "DEBT-FG11E2E01-06",
            "FG-GAP-238", "FG-GAP-239", "FG-GAP-240", "FG-GAP-241", "FG-GAP-242", "FG-GAP-243", "FG-GAP-244",
        };

        private static readonly string[] ClosedHere = { "FG-GAP-235", "FG-GAP-237", "FG-GAP-238", "FG-GAP-239", "FG-GAP-241", "FG-GAP-242", "FG-GAP-243", "DEBT-FG5RND05-04" };

        private static readonly string[] FixtureDebts = { "DEBT-FG11E2E01-01", "DEBT-FG11E2E01-02", "DEBT-FG11E2E01-03", "DEBT-FG11E2E01-05" };

        private static void CheckGapGate()
        {
            Line("  · C. 里程碑出口：属于 FG-M11 的缺口全部 Closed / 部分关闭（剩余另有门禁）/ 顺延（按门禁列的第一个里程碑判定）；本 Story 的登记与关闭；夹具债改承接写进 FG15-QA-01 正文");
            string text = ReadRepo(Register);
            if (text == null)
            {
                Fail("找不到 " + Register + "（仓库根定位失败）");
                return;
            }
            var openRows = new List<string>();
            int gapRows = 0, debtRows = 0, m11Rows = 0;
            var status = new Dictionary<string, string>(StringComparer.Ordinal);
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                bool gap = raw.StartsWith("| FG-GAP-", StringComparison.Ordinal);
                if (!gap && !raw.StartsWith("| DEBT-", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] cols = raw.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                string st = cols[cols.Length - 1].TrimStart('*');
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
                owners[cols[0]] = gap ? (cols.Length >= 7 ? cols[4] : string.Empty) : (cols.Length >= 9 ? cols[5] : string.Empty);
                if (gate == null || FirstMilestoneOf(gate) != 11)
                {
                    continue;
                }
                m11Rows++;
                bool ok = st.StartsWith("Closed", StringComparison.Ordinal) || PartialRemainderDeferred(gate, st) || st.StartsWith("顺延", StringComparison.Ordinal);
                if (!ok)
                {
                    openRows.Add(cols[0]);
                }
            }
            Expect(gapRows > 200 && debtRows > 500 && m11Rows >= 20, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中门禁属于 FG-M11 的 {m11Rows} 行（解析没有漏行）");
            Expect(openRows.Count == 0,
                openRows.Count == 0
                    ? "属于 FG-M11 的缺口 / 延后项全部 Closed / 部分关闭（剩余另有门禁）/ 顺延（写明理由与新门禁）"
                    : $"FG-M11 出口前还开着：[{string.Join("、", openRows)}]");
            List<string> notClosed = ClosedHere.Where(id => !status.TryGetValue(id, out string v) || !v.StartsWith("Closed", StringComparison.Ordinal)).ToList();
            List<string> unregistered = MineRegistered.Where(id => !status.ContainsKey(id)).ToList();
            bool pending = status.TryGetValue("DEBT-FG11E2E01-06", out string p) && p.Contains("待用户");
            Expect(unregistered.Count == 0 && notClosed.Count == 0 && pending,
                $"本 Story 的登记在表里（{MineRegistered.Length} 条：夹具 DEBT-FG11E2E01-01～03 / 05、性能 -04、试玩 -06 待用户、FG-GAP-238～244）；由本 Story 关闭的 {ClosedHere.Length} 条都是 Closed" +
                (unregistered.Count + notClosed.Count == 0 ? string.Empty : $"；没登记：{string.Join("、", unregistered)}；没关：{string.Join("、", notClosed)}"));
            // 规则第 7 条：延后与承接必须写进承接卡的正文，只写在登记册里不算。
            string cards = ReadRepo(Cards) ?? string.Empty;
            int at = cards.IndexOf("### FG15-QA-01 ", StringComparison.Ordinal);
            int end = at < 0 ? -1 : cards.IndexOf("\n### ", at + 10, StringComparison.Ordinal);
            string qa01 = at < 0 ? string.Empty : end < 0 ? cards.Substring(at) : cards.Substring(at, end - at);
            List<string> uncarried = FixtureDebts.Where(id => !owners.TryGetValue(id, out string o) || !o.Contains("FG15-QA-01") || !qa01.Contains(id)).ToList();
            Expect(uncarried.Count == 0, $"夹具债 {FixtureDebts.Length} 条承接 FG15-QA-01（全流程回归包把 FGJ-M10 + FGJ-M11 串成一局），FG15-QA-01 卡片正文有承接行" +
                                         (uncarried.Count == 0 ? string.Empty : "；缺：" + string.Join("、", uncarried)));
        }

        // ── D. 试玩包 ──────────────────────────────────────────────────────────────

        private static void CheckPlaytestPack()
        {
            Line("  · D. 试玩包：FG-M11 材料齐全（可选试玩，真人试玩由用户择时进行，DEBT-FG11E2E01-06，不阻塞）");
            const string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M11-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M11-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M11-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M11-问题清单模板.md");
            string readme = ReadRepo(dir + "README.md");
            bool exist = script != null && table != null && form != null && issues != null && readme != null;
            Expect(exist, "四份材料（试玩脚本、通过标准对照表、问卷与记录模板、问题清单模板）与目录说明都在");
            if (!exist)
            {
                return;
            }
            int at = readme.IndexOf("## FG-M11 超频阵营", StringComparison.Ordinal);
            string section = at >= 0 ? readme.Substring(at) : string.Empty;
            bool registered = section.Contains("DEBT-FG11E2E01-06") && section.Contains("不阻塞") && section.Contains("FGJ-M11");
            bool covers = script.Contains("冷却塔") && script.Contains("镜像体") && script.Contains("余烬主机") && script.Contains("超控阵列") && script.Contains("超频突袭") && table.Contains("T8");
            Expect(registered && covers, $"目录登记“待用户试玩、不阻塞”并写明 FGJ-M11 断点存档（{registered}）；脚本覆盖里程碑原文五段 + 超频突袭、对照表 T1～T8（{covers}）");
        }

        // ── E. 需求覆盖 ────────────────────────────────────────────────────────────

        private static void CheckRequirementCoverage()
        {
            Line("  · E. 需求覆盖（IC-REQ-020）：FG-M11“必须完成”各项有自检段且登记在全量自检，能进旅程的另有 FGJ-M11 步骤");
            var segments = new HashSet<string>(CellFrameworkValidate.SegmentNames, StringComparer.Ordinal);
            JourneyDef m11 = JourneyCatalog.Create(FgjM11Journey.Id);
            var map = new (string What, string Segment, string Step)[]
            {
                ("超频单位与精英（FG11-FAC-01）", "FgOverclockFactionSelfCheck", "garrison_fight"),
                ("镜像体复制核心固件（FG11-FAC-01，FGR-EMG-164）", "FgOverclockFactionSelfCheck", "mirror_copy"),
                ("热寂数据城领地模板与手工锚点：高温辐射、冷却塔遗址（FG11-REG-01）", "FgHeatDataCitySelfCheck", "tower_up"),
                ("多支远征同时在外（FG11-REG-01，FG-GAP-016）", "FgMultiExpeditionSelfCheck", null), // 轻量口径：旅程全程一支远征队
                ("余烬主机（含槽位封锁，FG11-BOSS-01）", "FgEmberHostSelfCheck", "boss_won"),
                ("超频带来的端口源与具名交互（FG19B，首次发现即命名，FG11-REACT-01）", "FgOverclockReactionSelfCheck", null), // 旅程里重炮 + 燃迹打驻军会触发，计数只进日志
                ("超频突袭（FG09 第 7 章，FG11-RAID-01）", "FgOverclockRaidSelfCheck", "raid_fight"),
                ("超频专属语法与标志组件（FGR-FAC-008、009，FG11-FAC-01）", "FgOverclockFactionSelfCheck", null), // I2 / E1 逐项
                ("交互伤害归因补全（FG11-ATTR-01）", "FgEmgDiscoverySelfCheck", null),
                ("超控阵列升 T3、信号核 5 槽（FG4-ECO-11 + 超算残核）", "FgOverrideArraySelfCheck", "arr_up"),
            };
            var bad = new List<string>();
            foreach ((string what, string segment, string step) in map)
            {
                bool journeyOk = step == null || (m11?.Steps.Any(s => s.Id == step) ?? false);
                if (!segments.Contains(segment) || !journeyOk)
                {
                    bad.Add($"{what}（{segment} / {step ?? "无旅程步骤"}）");
                }
            }
            Expect(bad.Count == 0 && segments.Contains("FgMilestoneM11SelfCheck"),
                $"{map.Length} 项各有自检段且登记在全量自检，{map.Count(x => x.Step != null)} 项另有 FGJ-M11 旅程步骤；[M11 出口] 本身登记在全量自检" + (bad.Count == 0 ? string.Empty : "；缺：" + string.Join("、", bad)));
        }

        // ── F. DEBT-FG5RND05-04 第二幕舰队信号片段定稿 ─────────────────────────────────

        private static void CheckFleetFragments()
        {
            Line("  · F. DEBT-FG5RND05-04：第二幕三条舰队信号片段定稿——按顺序、中英文齐全、互不重复、不提前写出“净化协议”（FG11 FGR-NAR-010：那句属于击败余烬主机的破译片段）");
            List<IntelFragmentDef> act2 = IntelCatalog.Fragments.Where(f => f.Act == 2).OrderBy(f => f.SortOrder).ToList();
            var zh = new List<string>();
            var en = new List<string>();
            foreach (IntelFragmentDef f in act2)
            {
                zh.Add(GameText.TryGet(f.TextKey, GameLanguage.ZhCn, out string z) ? z ?? string.Empty : string.Empty);
                en.Add(GameText.TryGet(f.TextKey, GameLanguage.En, out string e) ? e ?? string.Empty : string.Empty);
            }
            bool order = act2.Select(f => f.Id).SequenceEqual(new[] { "fleet.act2.greeting", "fleet.act2.window", "fleet.act2.count" });
            bool texts = zh.Count == 3 && zh.All(x => !string.IsNullOrWhiteSpace(x)) && en.All(x => !string.IsNullOrWhiteSpace(x)) && zh.Distinct().Count() == 3 && en.Distinct().Count() == 3;
            bool noSpoiler = zh.All(x => !x.Contains("净化协议")) && en.All(x => x.IndexOf("purge protocol", StringComparison.OrdinalIgnoreCase) < 0 && x.IndexOf("purification protocol", StringComparison.OrdinalIgnoreCase) < 0);
            Expect(order && texts && noSpoiler, $"第二幕片段 {act2.Count} 条按顺序（{string.Join(" → ", act2.Select(f => f.Id))}）、中英文齐全且互不重复（{texts}）、不提前写出“净化协议”（{noSpoiler}）");
            string doc = ReadRepo("TEngine/UnityProject/DesignDocs/fullgame/FG11_Narrative_Endgame.md") ?? string.Empty;
            int at = doc.IndexOf("DEBT-FG5RND05-04", StringComparison.Ordinal);
            string note = at < 0 ? string.Empty : doc.Substring(at, Math.Min(600, doc.Length - at));
            bool documented = note.Contains("intel.fragment.act2") && note.Contains("FGR-NAR-004");
            Expect(documented, "FG11 叙事文档 FGR-NAR-010 节点表后有第二幕片段的实现注记（文本键、语气由温暖转程式化 FGR-NAR-004）");
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
            Debug.LogWarning("[M11 出口] " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
