using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Objective;
using GameLogic.UI.SilentMind;
using GameLogic.View;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG10-E2E-01：M10 出口旅程 FGJ-M10（里程碑文档 FG-M10；00 契约 IC-REQ-021）。从主菜单“新建”出发（固定测试种子 42、标准难度），全程正式输入，
    /// RTS 口径：左键选择 / Shift 加选、右键地面 = 移动、右键敌人 = 攻击，按钮 / 快捷键只切状态；等待段用正式 3 倍速。
    ///
    /// 里程碑原文：完成防腐涂层主线 → 进入沉淀湖 → 利用化工自己的油迹打出爆燃 → 击败洗礼塔 → 回家应对一次化工突袭，重新净化被污染的区域。
    /// 承接 FG-GAP-163（沉淀湖区域片段）：第二幕无涂层右键进酸沼被拒（命令栏 + 字幕）→ 窗口内再下同样的命令强行进入 → 拿涂层后放行、腐蚀降到一成 → 首次进领地的阵营介绍。
    ///
    /// 轻量口径（用户 2026-10-12）：一条正向旅程走通核心循环，每个 M10 新系统至少被真实输入触发一次并断言结果；不为凑剧情改数值。
    /// 进度夹具两处，都写在步骤标题与报告里：开局夹具 <see cref="ApplyFixture"/>（第一幕结束后的进度：第 2 幕、铸造平定、通往沉淀湖的中继链、
    /// 湖外圈的仓库前哨、开到湖外的远征队）；家园夹具 <see cref="ApplyHomeFixture"/>（核心旁一座通电、接测试供冷却液点的净化塔——冷却液线由 FGJ-M7 覆盖；
    /// “针对净化塔”首次延迟 1 游戏日快进到点——延迟本身由 FgClarityRaidSelfCheck B1 断言）。被测行为（酸沼门槛、护送、涂层、阵营介绍、据点迎战与爆燃、
    /// 洗礼塔三阶段、突袭导演排定 / 出发 / 投放再污染、交战、重新净化）全走正式系统。
    /// 断言分三类（卡片承接，底稿 06 §3.4）：结构性（状态 / 阶段 / 是否发生）与名称·ID·次数进断言；数值只写进日志（反应触发计数在结尾集中打印一次，<see cref="ReactionNames"/> 是唯一映射处）。
    /// 用法：bash tools/unity-journey.sh FGJ-M10；断点 m10_fix / truck_go / op_fight / boss_won（--from 续跑）。
    /// </summary>
    public static class FgjM10Journey
    {
        public const string Id = "FGJ-M10";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private const string QuestId = "main.anticorrosion";
        private const string BossQuestId = "main.key.clarity";
        private const string Clarity = EnemyDefs.FactionClarity;
        private const int SquadSize = 8;
        private const int Vanguard = 1;
        private const string BpFire = "bp_fgjm10_fire";
        
        /// <summary>FGJ-M10 断言 / 日志里用到的具名反应 ID 与显示名（卡片承接：反应名与 ID 集中在一处映射；FG-EMG 阶段 2 回改时只改这里）。</summary>
        internal static readonly (string Id, string Name)[] ReactionNames =
        {
            ("reaction_deflagrate", "爆燃"),
            ("reaction_causticburn", "腐蚀灼烧"),
            ("reaction_syrup", "胶结"),
            ("reaction_dissolve", "溶蚀"),
            ("reaction_thermalshock", "热震"),
        };

        /// <summary>爆燃的反应 ID（取自 <see cref="ReactionNames"/>，FG-EMG 阶段 2 改名只改映射表一处）。</summary>
        private static string Deflagrate => ReactionNames[0].Id;

        private static CampaignState St => CampaignSession.Current;

        private static CombatSite Home => GameRoot.HomeValley?.Combat;

        private static RegionSquadCommandSystem Squad => GameRoot.HomeValley?.SquadCommands;

        public static JourneyDef Build()
        {
            var steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, TickPlay),
                S("menu_new", "主菜单点“新建”", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "存档槽 → 新游戏设置（种子 = 固定测试种子 42，难度标准）→ 点“开始” → 进入归还谷地", 150, null, JourneyCommon.TickNewGame),
                S("seed", "种子进了存档；生成结果与该种子的基准一致", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("workers", "记下开局两台机器", 10, null, FgjM3Common.TickWorkers),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("m10_fix", "开局夹具（DEBT-FG10E2E01-01）：第一幕结束后的进度——第 2 幕、铸造领地平定（主线“熄灭熔炉”完成 → “防腐涂层样品”出现）、核心到沉淀湖的信号中继链、" +
                             $"{SquadSize} 台 ERC-003（连射器 + 燃迹）远征队已开到接车点外", 20, ApplyFixture, TickFixture),

                // ── 1. 防腐涂层主线 + FG-GAP-163 区域片段 ──
                S("ml_open", "按任务日志键（默认 J）打开任务日志：主线“防腐涂层样品”在进行中", 10,
                    c => JourneyInput.PressToggleTo(GameActionId.ToggleMissionLog, () => MissionLogUIToolkit.IsOpen, true), TickLogOpen, retries: 1),
                S("ml_fly", "左键点这条主线，点“飞过去”：镜头飞到沉淀湖外圈的接车点", 20, null, TickFly),
                S("ml_close", "再按任务日志键关闭", 10, c => JourneyInput.PressToggleTo(GameActionId.ToggleMissionLog, () => MissionLogUIToolkit.IsOpen, false),
                    c => MissionLogUIToolkit.IsOpen ? StepOutcome.Wait : StepOutcome.Done("任务日志已关闭"), retries: 1),
                S("sel", "左键点一台远征队、Shift 左键加选其余几台", 60, null, TickSelectTeam, retries: 2),
                S("gate_refuse", "没有防腐涂层：右键酸沼带里的地面（往湖心）——命令被拒：命令栏与字幕写明哪片酸沼、腐蚀多快、蓝图来自主线“防腐涂层样品”，机器原地不动", 30, null, TickGateRefuse),
                S("gate_force", "确认窗口内再右键同一处（同样的命令）= 强行进入：远征队走进酸沼、每秒掉耐久", 60, null, TickGateForce),
                S("gate_back", "右键接车点那边的地面（从酸沼往外走不拦）：远征队退出酸沼", 90, null, TickGateBack),
                S("escort_spawn", "远征队到了接车点：装着防腐涂层样品的货运车出现（任务进入护送段，通知）", 90, null, TickEscortSpawned),
                S("truck_sel", "左键点货运车（只选它）", 30, null, TickTruckSelected, retries: 3),
                S("truck_go", "按回到核心键（默认 Home）镜头回家园，右键核心旁的地面：货运车开回家园 → 主线完成，防腐涂层蓝图到手（“防腐涂层已生效”）", 300, null, TickTruckDelivered),
                S("jump_team", "命令栏机器列表点一台远征队：接入（远距离跳转，镜头到湖外圈的远征队）", 40, c => FgjM1Journey.ClickMachineList(c, AliveTeam(c)[0]), TickJumpTeam, retries: 3),
                S("jump_exit", "按接入 / 退出接入键（默认 M）回战略视角（镜头留在远征队那里）", 20, c => JourneyInput.PressAction(GameActionId.ToggleCameraView), TickBackToStrategy, retries: 2),

                // ── 2. 进入沉淀湖：涂层放行 + 阵营介绍 ──
                S("sel2", "左键点一台远征队、Shift 左键加选其余几台", 60, null, TickSelectTeam, retries: 2),
                S("lake_in", "有涂层：右键酸沼另一侧的湖区地面——不再拦，远征队穿过酸沼，只掉一成", 150, null, TickLakeIn),
                S("intro_uplink", "命令栏机器列表点一台远征队：接入（镜头直控）——第一次进沉淀湖：阵营介绍通知一次", 40,
                    c => FgjM1Journey.ClickMachineList(c, AliveTeam(c)[0]), TickIntro, retries: 3),
                S("intro_exit", "按接入 / 退出接入键（默认 M）回战略视角", 20, c => JourneyInput.PressAction(GameActionId.ToggleCameraView), TickBackToStrategy, retries: 2),

                // ── 3. 利用化工自己的油迹打出爆燃 ──
                S("sel3", $"左键点一台远征队当先锋（{Vanguard} 台去叫醒据点、用燃迹慢慢把油污爬行者点着；其余留在后面，免得爬行者自爆 / 油迹爆燃一次波及全队）", 60, null, c => TickSelectSome(c, Vanguard), retries: 2),
                S("op_fix", "事件夹具（DEBT-FG10E2E01-03）：湖区覆盖边上登记一处小股驻军（≤ 6 人）的澄净据点（喷洒车 / 腐蚀蜂群 / 油污爬行者，按 fg.TbTerritoryControl 驻军编成）——" +
                            "按种子生成的湖区据点都在覆盖外、驻军 6～10，远征队收不到攻击命令", 10, ApplyOutpostFixture, TickOutpostFixture),
                S("op_go", "右键据点旁的地面：远征队走近，据点驻军醒来展开（喷洒车、腐蚀蜂群、油污爬行者）", 120, null, TickOutpostEngaged),
                S("op_fight", "右键离据点最近的敌人（攻击），打掉再右键下一台：燃迹把油污爬行者点着，着火的爬行者被击毁 → 爆燃（化工自己的油把同伴一起烧）；据点驻军清空", 360, null, TickOutpostCleared),

                // ── 4. 击败洗礼塔 ──
                S("sel4", "左键点一台远征队、Shift 左键加选其余存活的", 60, null, TickSelectTeam, retries: 2),
                S("ml_open2", "按任务日志键打开任务日志：澄净的主线（击败洗礼塔）在进行中", 10,
                    c => JourneyInput.PressToggleTo(GameActionId.ToggleMissionLog, () => MissionLogUIToolkit.IsOpen, true), c => MissionLogUIToolkit.IsOpen ? StepOutcome.Done("任务日志打开") : StepOutcome.Wait, retries: 1),
                S("ml_fly2", "左键点主线“击败洗礼塔”，点“飞过去”：镜头飞到沉淀湖湖心（湖心还没探索过，平移不过去）", 20, null, c => TickFlyTo(c, BossQuestId)),
                S("ml_close2", "再按任务日志键关闭", 10, c => JourneyInput.PressToggleTo(GameActionId.ToggleMissionLog, () => MissionLogUIToolkit.IsOpen, false),
                    c => MissionLogUIToolkit.IsOpen ? StepOutcome.Wait : StepOutcome.Done("任务日志已关闭"), retries: 1),
                S("boss_go", "右键湖心平台上的地面：远征队开进湖心、进场，洗礼塔开战（灌注阶段，HUD 显示名称与分阶段血条）", 240, null, TickBossEngaged, retries: 1),
                S("boss_fight", "右键反应塔（攻击），被打断就再右键：灌注 → 反应 → 泄压 → 击毁", 900, null, TickBossFight),
                S("boss_won", "洗礼塔击毁：奖励只发一次（反应增幅 + 流体固件、首领三选一数据包、反应核入库），澄净领地平定", 30, null, TickBossWon),

                // ── 5. 回家应对化工突袭、重新净化 ──
                S("home_fix", "家园夹具（DEBT-FG10E2E01-02）：远征队在湖心（回家 1500 格，突袭前赶不回来）——核心旁出厂 4 台连射器 + 燃迹的留守队；核心旁放一座通电、接测试供冷却液点的净化塔（冷却液线由 FGJ-M7 覆盖）；突袭导演为它排出“针对净化塔”的首次突袭（1 游戏日后）→ 快进到点", 30, ApplyHomeFixture, TickHomeFixture),
                S("home_focus", "按回到核心键（默认 Home）：镜头回家园", 10, c => JourneyInput.PressAction(GameActionId.FocusHomeCore), TickHomeFocused, retries: 1),
                S("home_sel", "左键点一台留守队、Shift 左键加选其余几台", 60, null, TickSelectTeam, retries: 2),
                S("go_home", "右键核心外侧的地面：留守队站到离净化塔 60 格的地方迎敌", 60, null, TickGoHome),
                S("raid_warn", "突袭导演排出澄净“针对净化塔”的计划 → 出发 → 预警通知；留守队已在核心外侧就位", 600, null, TickRaidWarned),
                S("raid_drop", "澄净突袭抵达：灭活无人机飞到净化塔投放 → 已净化区再污染（通知写明怎么重新净化、净化塔受污染、突袭结算记一笔）", 480, null, TickRepolluted),
                S("raid_fight", "右键离核心最近的敌人（攻击），打掉 / 跟丢再右键下一台：突袭结束（被击退或到时撤退）、剧场关闭，结算写明结果", 420, null, TickRaidCleared),
                S("repurify", "净化塔有电有冷却液，从中心往外重新净化：被再污染的塔心降回投放前的等级（重新净化路径写着这座塔）", 420, null, TickRepurified),
            };
            return new JourneyDef
            {
                Id = Id,
                Title = "M10 出口：完成防腐涂层主线（无涂层被酸沼拒绝 → 强行进入 → 护送样品车拿到涂层）→ 进入沉淀湖（阵营介绍）→ 利用化工自己的油迹打出爆燃 → 击败洗礼塔 → 回家应对一次化工突袭，重新净化被污染的区域",
                Seed = TestSeed,
                TotalTimeoutSeconds = 2700,
                OnFinish = Cleanup,
                Version = 1,
                CheckpointAfter = new[] { "m10_fix", "truck_go", "op_fight", "boss_won" },
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static string F(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);

        private static string Label(int id) => FgjM1Journey.Label(id);

        private static long NowMs() => FgjM2Common.NowMs();

        private static List<int> Team(JourneyContext c) => FgjM1Journey.Roster(c);

        private static bool Alive(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) && m != null && m.IsAlive;

        private static List<int> AliveTeam(JourneyContext c) => Team(c).Where(Alive).ToList();

        private static Vector2 PosOf(int id) => FgjM8Journey.PosOf(id);

        private static PlannedTerritory Lake => WorldGenService.PlanFor(St)?.Find(Clarity);

        private static Vector2 LakeCenter => Lake != null ? new Vector2(Lake.CenterX, Lake.CenterY) : Vector2.zero;

        private static Vector2 CorePos => FgjM8Journey.CorePos;

        private static ObjectiveRecord Quest => CampaignObjectiveTracker.FindQuest(St, QuestId);

        private static Vector2 V(JourneyContext c, string key)
        {
            string[] f = c.Get(key, "0,0").Split(',');
            return new Vector2(float.Parse(f[0], CultureInfo.InvariantCulture), float.Parse(f[1], CultureInfo.InvariantCulture));
        }

        private static float GetF(JourneyContext c, string key, float fallback = 0f) =>
            float.TryParse(c.Get(key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

        private static void SetF(JourneyContext c, string key, float v) => c.Set(key, v.ToString("R", CultureInfo.InvariantCulture));

        private static void SetV(JourneyContext c, string key, Vector2 v) => c.Set(key, F(v.x, "0.##") + "," + F(v.y, "0.##"));

        private static Vector2 Centroid(JourneyContext c)
        {
            List<int> team = AliveTeam(c);
            return team.Count == 0 ? Vector2.zero : team.Aggregate(Vector2.zero, (a, id) => a + PosOf(id)) / team.Count;
        }

        private static string TeamHp(JourneyContext c) => string.Join("、", Team(c).Select(id => MachineRegistry.TryGetRecord(id, out MachineRecord m)
            ? $"{Label(id)} {(m.IsAlive ? F(m.Health, "0") + "/" + F(m.MaxHealth, "0") : "阵亡")}" : "?"));

        private static float TeamHealth(JourneyContext c) => Team(c).Sum(id => MachineRegistry.TryGetRecord(id, out MachineRecord m) && m.IsAlive ? m.Health : 0f);

        private static int Cmd(int logicId)
        {
            CombatSite site = Home;
            return site != null && site.TryGetMachineUnit(logicId, out int u) && site.TryGetCommand(u, out CombatCommand cmd) ? (int)cmd.Kind : -1;
        }

        private static string LastNotify(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Text ?? string.Empty;

        private static int NotifyCount(string typeId) => NotificationCenter.History.Count(n => n.Type != null && n.Type.Id == typeId);

        private static string LastEvent() => Squad?.RecentEvents.LastOrDefault() ?? string.Empty;

        private static StepOutcome TickPlay(JourneyContext c) =>
            JourneyCommon.TickPlay(c, TestSeed, () =>
            {
                FgjM2Common.ResetSampling();
                FgjM3Common.ResetSampling();
                FgjM2Common.IsolateCodex(c);
                FgjM3Journey.IsolateLayoutLibrary(c);
            });

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(ReactionReport());
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM3Common.FrameReport("全程"));
            JourneyCommon.Cleanup(c);
        }

        /// <summary>M10 基线用：家园星球内核里每条具名反应的触发次数 + 爆燃 / 油迹点燃计数（只记录，不断言数值）。</summary>
        internal static string ReactionReport()
        {
            CombatSite site = Home;
            if (site?.Kernel == null)
            {
                return "具名反应计数：没有家园内核";
            }
            var parts = new List<string>();
            for (int i = 0; i < NamedReactionCatalog.TagRules.Count; i++)
            {
                string id = NamedReactionCatalog.IdOfRule(i);
                int n = site.Kernel.ReactionCountOf(i);
                if (n > 0)
                {
                    string name = ReactionNames.FirstOrDefault(r => r.Id == id).Name ?? NamedReactionCatalog.NameOf(id);
                    parts.Add($"{id}（{name}）{n}");
                }
            }
            return $"具名反应计数（FGJ-M10 全程，家园星球内核）：{(parts.Count == 0 ? "无" : string.Join("、", parts))}；爬行者爆燃 {site.Kernel.Counters.Deflagrations}、油迹点燃 {site.Kernel.Counters.SlicksIgnited}";
        }

        // ── 开局夹具 ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 开局夹具（DEBT-FG10E2E01-01，轻量口径）：第一幕结束之后的进度——
        /// 第 2 幕（第一幕流程由 FGJ-M9 覆盖）；铸造领地平定（TerritoryControlService.MarkLeaderDefeated，正式击败由 FGJ-M9 覆盖）→ 主线“熄灭熔炉”完成、“防腐涂层样品”按正式前置出现；
        /// 核心往湖心一路建成的信号中继塔（每座立在上一座覆盖里、尽量往湖心推，湖区污染 ≥ 2 不能建造就停；揭开沿途的雾；建中继与探索由 FGJ-M8 覆盖）；
        /// 燃迹固件的“连射器 + 燃迹”蓝图与 8 台装它的 ERC-003（出厂由 FGJ-M4 / M6 覆盖，这里按正式出厂入口放在接车点外 30 格）。
        /// </summary>
        private static void ApplyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("roster", string.Empty).Length > 0)
            {
                return;
            }
            if (s.Progress != null)
            {
                c.SetInt("act0", s.Progress.Act);
                s.Progress.Act = Math.Max(2, s.Progress.Act);
            }
            c.SetInt("foundryPacified", TerritoryControlService.MarkLeaderDefeated(s, EnemyDefs.FactionFoundry) ? 1 : 0);
            PlannedTerritory t = Lake;
            if (t == null || !CampaignObjectiveTracker.ResolveAnchorForTests(s, QuestId, out int px, out int py))
            {
                c.Set("fix.err", "规划层没有澄净领地 / 接车点解析不出");
                return;
            }
            var pickup = new Vector2(px, py);
            Vector2 core = CorePos;
            Vector2 center = LakeCenter;
            Vector2 toHome = (core - pickup).normalized;
            SetV(c, "pickup", pickup);

            // 中继链：从核心（覆盖 150）起，每一座立在上一座覆盖里、尽量往湖心推（中继 200）；湖区污染 ≥ 2 的格子不能建造，推不动就停。
            var relays = new List<string>();
            Vector2 last = core;
            float lastRadius = SignalCoverageService.CoreRadius;
            for (int n = 0; n < 16; n++)
            {
                Vector2 dir = (center - last).normalized;
                Vector2 perp = new Vector2(-dir.y, dir.x);
                GridCell? best = null;
                for (float d = lastRadius - 8f; d >= 40f && !best.HasValue; d -= 4f)
                {
                    HomeGridService.RevealArea(s, last + dir * d, 70f); // 第一幕沿这条路探索过（探索由 FGJ-M1 / M8 覆盖）
                    for (int k = 0; k <= 20 && !best.HasValue; k++)
                    {
                        float o = (k % 2 == 0 ? 1f : -1f) * (k + 1) / 2 * 6f;
                        Vector2 p = last + dir * d + perp * o;
                        if (Vector2.Distance(p, last) > lastRadius - 8f)
                        {
                            continue;
                        }
                        var g = new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
                        GridPlacementResult v = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeSignalRelay, g, 0, asPlayerPlacement: false, checkCost: false);
                        if (v.Ok)
                        {
                            best = g;
                        }
                        else if (k == 0)
                        {
                            _lastWhy = string.Join("/", v.Reasons.Select(x => x.Code.ToString()));
                        }
                    }
                }
                if (!best.HasValue || Vector2.Distance(new Vector2(best.Value.X, best.Value.Y), center) > Vector2.Distance(last, center) - 20f)
                {
                    relays.Add($"[推不动：{_lastWhy}]");
                    // 湖区污染 ≥ 2，玩家建不了中继（A 段交接登记缺口）：夹具硬放到湖心平台旁，让平台上能右键下令（DEBT-FG10E2E01-04）。
                    BaptismTowerBoss.TryArena(s, out Vector2 arena0);
                    for (int m = 0; m < 6 && Vector2.Distance(last, arena0) > 60f; m++)
                    {
                        Vector2 step = last + (arena0 - last).normalized * Mathf.Min(185f, Vector2.Distance(last, arena0) - 40f);
                        GridCell hg = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(step.x), Mathf.RoundToInt(step.y)), 12);
                        BuildingRecord hb = FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeSignalRelay, "m10_lakerelay" + m, hg);
                        last = hb.Position;
                        relays.Add($"<湖区硬放 ({hg.X},{hg.Y})>");
                    }
                    break;
                }
                BuildingRecord b = FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeSignalRelay, "m10_relay" + n, best.Value);
                last = b.Position;
                lastRadius = 200f;
                relays.Add($"({best.Value.X},{best.Value.Y})");
                if (Vector2.Distance(last, center) < 60f)
                {
                    break;
                }
            }
            SetV(c, "lastRelay", last);
            c.Set("fix.relays", string.Join(" ", relays));

            SignalCoverageService.Invalidate();

            // 远征队：连射器 + 燃迹（卡片“与燃迹组合”，FgClarityFactionSelfCheck G1 同一读法）。
            if (!s.BlueprintRecords.Any(b => b.BlueprintId == BpFire))
            {
                BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { "fw_burntrail" });
                s.BlueprintRecords = s.BlueprintRecords.Append(new BlueprintRecord
                {
                    BlueprintId = BpFire, DisplayName = "连射器燃迹", ActiveVersion = 1, Versions = new[] { board.ToVersion(1, 0f) },
                }).ToArray();
            }
            Vector2 park = pickup + toHome * 30f;
            SetV(c, "park", park);
            var ids = new List<int>();
            var used = new List<GridCell>();
            for (int i = 0; i < SquadSize; i++)
            {
                // 两排、相隔 3 格；落到同一格（原位不可走）就换下一个位置——两台叠在一起时左键只点得到上面那台。
                GridCell at = default;
                for (int tr = i; tr < i + 40; tr++)
                {
                    Vector2 side = new Vector2(-toHome.y, toHome.x) * ((tr % 4 - 1.5f) * 3f) + toHome * ((tr / 4) * 3f);
                    at = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(park.x + side.x), Mathf.RoundToInt(park.y + side.y)), 10);
                    if (!used.Any(u => Math.Abs(u.X - at.X) + Math.Abs(u.Y - at.Y) < 2))
                    {
                        break;
                    }
                }
                used.Add(at);
                MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, BpFire, HomeValleyLayout.RegionId, new Vector2(at.X, at.Y), 100f, 100f, "Player", 1);
                if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
                {
                    MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
                    ids.Add(r.LogicId);
                }
            }
            c.Set("roster", string.Join(",", ids.Select(x => x.ToString(CultureInfo.InvariantCulture))));
        }

        private static string _lastWhy = string.Empty;

        private static StepOutcome TickFixture(JourneyContext c)
        {
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait; // 任务重算 / 覆盖源重建 / 反应批次都按整秒
            }
            CampaignState s = St;
            if (c.Get("fix.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail("夹具：" + c.Get("fix.err"));
            }
            List<int> team = Team(c);
            bool inKernel = team.Count == SquadSize && team.All(id => Home != null && Home.TryGetMachineUnit(id, out _));
            ObjectiveRecord q = Quest;
            PlannedTerritory t = Lake;
            bool covPark = SignalCoverageService.Sample(HomeValleyLayout.RegionId, V(c, "park")).Covered;
            bool covCenter = SignalCoverageService.Sample(HomeValleyLayout.RegionId, LakeCenter).Covered;
            BaptismTowerBoss.TryArena(s, out Vector2 arena);
            bool covArena = SignalCoverageService.Sample(HomeValleyLayout.RegionId, arena).Covered;
            string geo = $"沉淀湖中心 ({t?.CenterX},{t?.CenterY}) 半径 {t?.Radius}、外圈酸沼带宽 {t?.HazardWidth}、外缘 {t?.OuterRadius}，离核心 {F(Vector2.Distance(CorePos, LakeCenter), "0")} 格；" +
                         $"接车点 {c.Get("pickup")}、远征队停在 {c.Get("park")}；湖心平台 ({F(arena.x, "0")},{F(arena.y, "0")})；中继链 {c.Get("fix.relays")}";
            c.Log(geo);
            if (!inKernel || q == null || q.State != ObjectiveState.Active || (s.Progress?.Act ?? 1) < 2 || !covPark)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait
                    : StepOutcome.Fail($"夹具后：远征队进内核 {inKernel}（{team.Count} 台）、主线“防腐涂层样品” {q?.State.ToString() ?? "没出现"}、第 {s.Progress?.Act} 幕、停车处在覆盖里 {covPark}；{geo}");
            }
            c.SetLong("ref0", RegionSquadCommandSystem.AcidGateRefusals);
            return StepOutcome.Done($"夹具生效：第 {c.GetInt("act0")} → {s.Progress.Act} 幕；铸造平定 {c.GetInt("foundryPacified") == 1}（主线“熄灭熔炉”{CampaignObjectiveTracker.FindQuest(s, "main.key.foundry")?.State}）；" +
                                    $"主线“{CampaignObjectiveTracker.QuestTitle(q)}”进行中（{CampaignObjectiveTracker.ProgressText(s, q)}）；还没有涂层 {!AcidMarshService.HasCoating(s)}；" +
                                    $"覆盖：停车处 {covPark}、湖心 {covCenter}、平台 {covArena}；远征队 {string.Join("、", team.Select(Label))}；{geo}");
        }

        // ── 1. 主线 + 酸沼门槛 ──────────────────────────────────────────────────────

        private static StepOutcome TickLogOpen(JourneyContext c)
        {
            if (!MissionLogUIToolkit.IsOpen)
            {
                return c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Retry("任务日志没打开");
            }
            ObjectiveRecord q = Quest;
            return q != null && q.State == ObjectiveState.Active
                ? StepOutcome.Done($"任务日志打开：主线“{CampaignObjectiveTracker.QuestTitle(q)}”进行中（{CampaignObjectiveTracker.WhereText(St, q)}）")
                : StepOutcome.Fail("任务日志里没有进行中的“防腐涂层样品”");
        }

        private static StepOutcome TickFly(JourneyContext c) => TickFlyTo(c, QuestId);

        private static StepOutcome TickFlyTo(JourneyContext c, string questId)
        {
            MissionLogUIToolkit ml = MissionLogUIToolkit.Instance;
            if (ml == null)
            {
                return StepOutcome.Fail("任务日志不见了");
            }
            Unity.Mathematics.float2 f = WorldView.Director.StrategyFocus;
            ObjectiveRecord fq = CampaignObjectiveTracker.FindQuest(St, questId);
            Vector2? where = fq != null ? CampaignObjectiveTracker.QuestLocation(St, fq) : null;
            if (where == null)
            {
                return StepOutcome.Fail($"任务 {questId} 不在 / 没有地点（{fq?.State}）");
            }
            Vector2 pickup = where.Value;
            if (c.GetInt(FgjM3Common.SK(c, "flown")) == 1)
            {
                return Vector2.Distance(new Vector2(f.x, f.y), pickup) < 40f
                    ? StepOutcome.Done($"点“飞过去”：镜头到 ({F(f.x, "0")},{F(f.y, "0")})，离任务地点 {F(Vector2.Distance(new Vector2(f.x, f.y), pickup), "0")} 格；反馈“{ml.MessageText}”")
                    : c.StepElapsed < 12 ? StepOutcome.Wait : StepOutcome.Fail($"点“飞过去”后镜头在 ({F(f.x, "0")},{F(f.y, "0")})，离任务地点 {F(Vector2.Distance(new Vector2(f.x, f.y), pickup), "0")} 格（“{ml.MessageText}”）");
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 800)
            {
                return StepOutcome.Wait;
            }
            c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
            if (ml.Selected != questId)
            {
                for (int col = 0; col < 3; col++)
                {
                    for (int i = 0; i < ml.RowCount(col); i++)
                    {
                        if (ml.RowView(col, i).Id == questId)
                        {
                            return JourneyInput.ClickElement(ml.RowElement(col, i)) || JourneyInput.LastUiTransient ? StepOutcome.Wait : StepOutcome.Retry("点任务那一行失败：" + JourneyInput.LastUiFailure);
                        }
                    }
                }
                return c.StepElapsed > 6 ? StepOutcome.Fail($"任务日志里没有 {questId} 这一行") : StepOutcome.Wait;
            }
            if (!MissionLogUIToolkit.ButtonShown(ml.FlyButton) || !JourneyInput.ClickElement(ml.FlyButton))
            {
                return JourneyInput.LastUiTransient ? StepOutcome.Wait : StepOutcome.Retry("点“飞过去”失败：" + JourneyInput.LastUiFailure);
            }
            c.SetInt(FgjM3Common.SK(c, "flown"), 1);
            return StepOutcome.Wait;
        }

        /// <summary>左键点第一台、Shift 左键加选其余存活的远征队成员（真实点击机器露出来的部分）。</summary>
        private static StepOutcome TickSelectTeam(JourneyContext c) => TickSelectSome(c, int.MaxValue);

        /// <summary>只选前 <paramref name="n"/> 台存活的（左键第一台、Shift 左键加选）。</summary>
        private static StepOutcome TickSelectSome(JourneyContext c, int n)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            List<int> team = AliveTeam(c).Take(n).ToList();
            IReadOnlyList<int> sel = Squad?.Selection;
            if (sel != null && team.Count > 0 && sel.Count == team.Count && team.All(sel.Contains))
            {
                return StepOutcome.Done($"选择集 [{string.Join("、", sel.Select(Label))}]（{team.Count} 台存活）");
            }
            if ((int)(c.StepElapsed / 10) > c.GetInt(FgjM3Common.SK(c, "slog")))
            {
                c.SetInt(FgjM3Common.SK(c, "slog"), (int)(c.StepElapsed / 10));
                float2 f = WorldView.Director.StrategyFocus;
                c.Log($"[{F(c.StepElapsed, "0")}s] 选队：第 {c.GetInt(FgjM3Common.SK(c, "k"))} 台；选择集 [{string.Join("、", (sel ?? Array.Empty<int>()).Select(Label))}]；镜头 ({F(f.x, "0")},{F(f.y, "0")})；" +
                      $"{string.Join("、", team.Select(id => $"{Label(id)}@{Cell(PosOf(id))} 模型 {FgjM1Journey.HomeMarker(id, out _)}"))}；按住 {JourneyInput.Holding}");
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 700)
            {
                return StepOutcome.Wait;
            }
            int k = c.GetInt(FgjM3Common.SK(c, "k"));
            if (k >= team.Count)
            {
                return StepOutcome.Retry($"点完一遍后选择集是 [{string.Join("、", (sel ?? Array.Empty<int>()).Select(Label))}]，应为 [{string.Join("、", team.Select(Label))}]");
            }
            int id = team[k];
            if (!FgjM1Journey.HomeMarker(id, out HomeValleyMachineMarker m))
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail($"{Label(id)} 不在家园战斗内核里 / 没有模型");
            }
            if (!JourneyCommon.PanToward(m.Position, 0.15f))
            {
                return StepOutcome.Wait;
            }
            if (!JourneyInput.TryVisiblePointOf(m.View.transform, out Vector3 screen, out _))
            {
                FgjM3Common.PanTo(m.Position);
                return StepOutcome.Wait;
            }
            if (k == 0)
            {
                JourneyInput.ClickScreen(screen);
            }
            else
            {
                JourneyInput.ShiftClickScreen(screen);
            }
            c.SetInt(FgjM3Common.SK(c, "k"), k + 1);
            c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
            return StepOutcome.Wait;
        }

        /// <summary>从远征队中心往湖心，第一格酸沼再往里 6 格（右键目标要在酸沼带里、画面里）。</summary>
        private static Vector2 BeltPoint(JourneyContext c)
        {
            Vector2 from = Centroid(c);
            Vector2 dir = (LakeCenter - from).normalized;
            for (float d = 2f; d < 400f; d += 1f)
            {
                Vector2 p = from + dir * d;
                if (AcidMarshService.ZoneAt(St, p) != null)
                {
                    return from + dir * (d + 6f);
                }
            }
            return from + dir * 30f;
        }

        private static StepOutcome TickGateRefuse(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0)
            {
                if (AcidMarshService.HasCoating(s))
                {
                    return StepOutcome.Fail("还没做主线就已经有防腐涂层");
                }
                Vector2 p = BeltPoint(c);
                SetV(c, "belt", p);
                c.SetLong("ref0", RegionSquadCommandSystem.AcidGateRefusals);
                c.SetLong("forced0", RegionSquadCommandSystem.AcidGateForced);
                c.SetInt("cap0", FeedbackCues.CaptionRevision);
                if (!JourneyInput.Holding && FgjM3Common.TryClick(p, 1))
                {
                    c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                    c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
                }
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 600)
            {
                return StepOutcome.Wait;
            }
            string ev = LastEvent();
            string cap = FeedbackCues.LastCaptionText ?? string.Empty;
            string title = GameText.Get("quest.main.anticorrosion.title");
            int moving = AliveTeam(c).Count(id => Cmd(id) == (int)CombatCommandKind.Move);
            bool refused = RegionSquadCommandSystem.AcidGateRefusals == c.GetLong("ref0") + 1 && RegionSquadCommandSystem.AcidGateForced == c.GetLong("forced0");
            bool told = ev.Contains(title) && cap.Contains(title) && FeedbackCues.CaptionRevision > c.GetInt("cap0") && FeedbackCues.LastCue == FeedbackCueId.Denied;
            if (!refused || !told || moving > 0)
            {
                return StepOutcome.Fail($"右键酸沼 {c.Get("belt")}：拒绝 {RegionSquadCommandSystem.AcidGateRefusals - c.GetLong("ref0")} 次 / 强行 {RegionSquadCommandSystem.AcidGateForced - c.GetLong("forced0")} 次；" +
                                        $"命令栏“{ev}”；字幕“{cap}”（提示 {FeedbackCues.LastCue}）；接到移动命令 {moving} 台");
            }
            c.SetLong("refuseAt", NowMs());
            SetF(c, "hp0", TeamHealth(c));
            return StepOutcome.Done($"右键酸沼带 {c.Get("belt")}（没有涂层）：命令被拒，命令栏“{ev}”，字幕同一句（拒绝音），机器原地不动");
        }

        private static StepOutcome TickGateForce(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0)
            {
                // 同一处、同样的命令，确认窗口（真实秒）内：等 1 秒再点（像玩家看完提示再点一次）。
                if (NowMs() - c.GetLong("refuseAt") < 1000 || JourneyInput.Holding)
                {
                    return StepOutcome.Wait;
                }
                if ((NowMs() - c.GetLong("refuseAt")) / 1000f > AcidMarshService.GateConfirmSeconds - 0.5f)
                {
                    return StepOutcome.Fail($"错过了确认窗口（{AcidMarshService.GateConfirmSeconds} 秒）");
                }
                if (FgjM3Common.TryClick(V(c, "belt"), 1))
                {
                    c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                    c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
                }
                return StepOutcome.Wait;
            }
            if (c.GetInt(FgjM3Common.SK(c, "checked")) == 0)
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 600)
                {
                    return StepOutcome.Wait;
                }
                c.SetInt(FgjM3Common.SK(c, "checked"), 1);
                int moving = AliveTeam(c).Count(id => Cmd(id) == (int)CombatCommandKind.Move);
                bool forced = RegionSquadCommandSystem.AcidGateForced == c.GetLong("forced0") + 1 && RegionSquadCommandSystem.AcidGateRefusals == c.GetLong("ref0") + 1;
                if (!forced || moving == 0)
                {
                    return StepOutcome.Fail($"窗口内再右键同一处：强行进入 {RegionSquadCommandSystem.AcidGateForced - c.GetLong("forced0")} 次、拒绝 {RegionSquadCommandSystem.AcidGateRefusals - c.GetLong("ref0")} 次、接到移动命令 {moving} 台（“{LastEvent()}”）");
                }
                c.Set("forceEv", LastEvent());
                return StepOutcome.Wait;
            }
            // 有机器进了酸沼：记下 2 个游戏秒里的耐久变化（只记录数值）。
            int inAcid = AliveTeam(c).Count(id => AcidMarshService.ZoneAt(s, PosOf(id)) != null);
            if (inAcid == 0)
            {
                return StepOutcome.Wait;
            }
            if (c.GetLong(FgjM3Common.SK(c, "acidTick")) == 0)
            {
                c.SetLong(FgjM3Common.SK(c, "acidTick"), GameClock.Ticks);
                SetF(c, FgjM3Common.SK(c, "acidHp"), TeamHealth(c));
                return StepOutcome.Wait;
            }
            if (GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "acidTick")) < GameClock.StepHz * 2)
            {
                return StepOutcome.Wait;
            }
            float lost = GetF(c, FgjM3Common.SK(c, "acidHp")) - TeamHealth(c);
            SetF(c, "acidLossNoCoat", lost);
            return lost > 0f
                ? StepOutcome.Done($"窗口内再右键同一处 = 强行进入（“{c.Get("forceEv")}”）：{inAcid} 台进了酸沼，2 个游戏秒全队掉耐久 {F(lost)}（没有涂层的重度腐蚀）")
                : StepOutcome.Fail($"进了酸沼却没掉耐久（{TeamHp(c)}）");
        }

        private static StepOutcome TickGateBack(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            Vector2 pickup = V(c, "pickup");
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0)
            {
                c.SetLong("ref1", RegionSquadCommandSystem.AcidGateRefusals);
                if (!JourneyInput.Holding && FgjM3Common.TryClick(pickup, 1))
                {
                    c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                }
                return StepOutcome.Wait;
            }
            if (RegionSquadCommandSystem.AcidGateRefusals != c.GetLong("ref1"))
            {
                return StepOutcome.Fail($"从酸沼往外走也被拦了（“{LastEvent()}”）");
            }
            int inAcid = AliveTeam(c).Count(id => AcidMarshService.ZoneAt(s, PosOf(id)) != null);
            bool near = AliveTeam(c).All(id => Vector2.Distance(PosOf(id), pickup) < 25f);
            return inAcid == 0 && near
                ? StepOutcome.Done($"右键接车点 {Cell(pickup)}：往外走不拦，远征队退出酸沼回到接车点（{TeamHp(c)}）")
                : StepOutcome.Wait;
        }

        private static string Cell(Vector2 p) => $"({F(p.x, "0")},{F(p.y, "0")})";

        private static StepOutcome TickEscortSpawned(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            ObjectiveRecord q = Quest;
            if (q == null || q.State != ObjectiveState.Active)
            {
                return StepOutcome.Fail($"主线“防腐涂层样品”不在进行中（{q?.State}）");
            }
            if (q.Stage != 1 || q.EscortLogicId <= 0 || !Alive(q.EscortLogicId))
            {
                return StepOutcome.Wait;
            }
            c.SetInt("truck", q.EscortLogicId);
            string note = GameText.Format("quest.notify.escort", CampaignObjectiveTracker.QuestTitle(q));
            bool told = FeedbackCues.LastCaptionText == note || NotificationCenter.History.Any(n => n.Text != null && n.Text.Contains(CampaignObjectiveTracker.QuestTitle(q)));
            return StepOutcome.Done($"货运车 {Label(q.EscortLogicId)} 出现在接车点 {Cell(PosOf(q.EscortLogicId))}：任务进入护送段（{CampaignObjectiveTracker.ProgressText(St, q)}；提示 {told}）");
        }

        private static StepOutcome TickTruckSelected(JourneyContext c)
        {
            int id = c.GetInt("truck");
            IReadOnlyList<int> sel = Squad?.Selection;
            if (sel != null && sel.Count == 1 && sel[0] == id)
            {
                return StepOutcome.Done($"左键选中货运车 {Label(id)}（只选它）");
            }
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 1)
            {
                return NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 800 ? StepOutcome.Wait : StepOutcome.Retry($"左键后选择集是 [{string.Join("、", (sel ?? Array.Empty<int>()).Select(Label))}]");
            }
            if (JourneyInput.Holding || !FgjM1Journey.HomeMarker(id, out HomeValleyMachineMarker m))
            {
                return StepOutcome.Wait;
            }
            if (!JourneyCommon.PanToward(m.Position, 0.15f))
            {
                return StepOutcome.Wait;
            }
            if (!JourneyInput.TryVisiblePointOf(m.View.transform, out Vector3 screen, out _))
            {
                FgjM3Common.PanTo(m.Position);
                return StepOutcome.Wait;
            }
            JourneyInput.ClickScreen(screen);
            c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
            c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
            return StepOutcome.Wait;
        }

        private static StepOutcome TickTruckDelivered(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            int truck = c.GetInt("truck");
            Vector2 depot = CorePos;
            Vector2 toPickup = (V(c, "pickup") - depot).normalized;
            if (c.GetInt(FgjM3Common.SK(c, "home")) == 0)
            {
                JourneyInput.PressAction(GameActionId.FocusHomeCore); // 镜头回家园（回到核心键），再右键核心旁的地面
                c.SetInt(FgjM3Common.SK(c, "home"), 1);
                return StepOutcome.Wait;
            }
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0)
            {
                c.SetInt("coat0", AcidMarshService.CoatingNotices);
                GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(depot.x + toPickup.x * 12f), Mathf.RoundToInt(depot.y + toPickup.y * 12f)), 8);
                if (!JourneyInput.Holding && c.StepElapsed > 1.0 && FgjM3Common.TryClick(new Vector2(g.X, g.Y), 1))
                {
                    c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                    c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
                }
                return StepOutcome.Wait;
            }
            if (c.GetInt(FgjM3Common.SK(c, "moveChecked")) == 0 && NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) > 700)
            {
                c.SetInt(FgjM3Common.SK(c, "moveChecked"), 1);
                if (Cmd(truck) != (int)CombatCommandKind.Move && Quest?.State == ObjectiveState.Active)
                {
                    return StepOutcome.Fail($"右键地面后货运车没有接到移动命令（“{LastEvent()}”）");
                }
            }
            ObjectiveRecord q = Quest;
            if (q == null || q.State == ObjectiveState.Failed)
            {
                return StepOutcome.Fail($"护送失败（{q?.State}；货运车 {TeamHpOf(truck)}）");
            }
            if (q.State != ObjectiveState.Completed)
            {
                return StepOutcome.Wait;
            }
            if (!AcidMarshService.HasCoating(s) || AcidMarshService.CoatingNotices <= c.GetInt("coat0") || Home == null || !Mathf.Approximately(Home.PlayerAcidResist, AcidMarshService.CoatingResist))
            {
                if (c.GetLong(FgjM3Common.SK(c, "doneAt")) == 0)
                {
                    c.SetLong(FgjM3Common.SK(c, "doneAt"), NowMs());
                }
                return NowMs() - c.GetLong(FgjM3Common.SK(c, "doneAt")) < 5000 ? StepOutcome.Wait
                    : StepOutcome.Fail($"主线完成但涂层没生效：有涂层 {AcidMarshService.HasCoating(s)}、通知 {AcidMarshService.CoatingNotices - c.GetInt("coat0")} 次、内核抗性 {Home?.PlayerAcidResist}");
            }
            return StepOutcome.Done($"货运车沿中继链开回家园（核心 {Cell(depot)}）：主线“{CampaignObjectiveTracker.QuestTitle(q)}”完成，防腐涂层蓝图到手；通知“{LastNotify("acid_coating")}”；星球内核里玩家机器的酸蚀抗性 = {F(Home.PlayerAcidResist, "0.##")}");
        }

        private static StepOutcome TickJumpTeam(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点机器列表失败：" + FgjM1Journey.UiFail(c));
            }
            int b = AliveTeam(c)[0];
            if (SignalPresence.CurrentMachineLogicId != b || WorldView.Director.Mode != ViewMode.Direct)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {Label(b)}（{SignalUplinkService.LastFeedbackText}）");
            }
            bool introduced = St.Factions?.TerritoriesIntroduced?.Contains(Clarity) == true;
            return c.StepElapsed < 2.0 ? StepOutcome.Wait
                : introduced ? StepOutcome.Fail($"接入湖外圈的 {Label(b)} 就发了沉淀湖的阵营介绍（还没进领地）")
                : StepOutcome.Done($"接入 {Label(b)}（远距离跳转 {SignalUplinkService.FarJumpCount - c.GetInt("far0")} 次）：镜头到湖外圈；还没进领地，不发阵营介绍");
        }

        private static string TeamHpOf(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? (m.IsAlive ? F(m.Health, "0") + "/" + F(m.MaxHealth, "0") : "阵亡") : "?";

        // ── 2. 进湖 + 阵营介绍 ──────────────────────────────────────────────────────

        private static StepOutcome TickLakeIn(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            PlannedTerritory t = Lake;
            Vector2 center = LakeCenter;
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0)
            {
                // 目标：酸沼带内缘再往里 10 格（湖区、不在酸里、能走）。
                Vector2 from = Centroid(c);
                Vector2 dir = (center - from).normalized;
                Vector2 target = center;
                bool seenAcid = false;
                for (float d = 2f; d < 600f; d += 1f)
                {
                    Vector2 p = from + dir * d;
                    bool acid = AcidMarshService.ZoneAt(s, p) != null;
                    seenAcid |= acid;
                    if (seenAcid && !acid)
                    {
                        GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(p.x + dir.x * 10f), Mathf.RoundToInt(p.y + dir.y * 10f)), 10);
                        target = new Vector2(g.X, g.Y);
                        break;
                    }
                }
                SetV(c, "lakeIn", target);
                c.SetLong("ref2", RegionSquadCommandSystem.AcidGateRefusals);
                if (!JourneyInput.Holding && FgjM3Common.TryClick(target, 1))
                {
                    c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                    c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
                }
                return StepOutcome.Wait;
            }
            if (c.GetInt(FgjM3Common.SK(c, "checked")) == 0)
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 700)
                {
                    return StepOutcome.Wait;
                }
                c.SetInt(FgjM3Common.SK(c, "checked"), 1);
                int moving = AliveTeam(c).Count(id => Cmd(id) == (int)CombatCommandKind.Move);
                if (RegionSquadCommandSystem.AcidGateRefusals != c.GetLong("ref2") || moving == 0)
                {
                    return StepOutcome.Fail($"有涂层后往湖区下令：拒绝 {RegionSquadCommandSystem.AcidGateRefusals - c.GetLong("ref2")} 次、接到移动命令 {moving} 台（“{LastEvent()}”）");
                }
            }
            // 穿过酸沼时记 2 个游戏秒的耐久变化（有涂层只掉一成，只记录数值）。
            int inAcid = AliveTeam(c).Count(id => AcidMarshService.ZoneAt(s, PosOf(id)) != null);
            if (inAcid > 0 && c.GetLong(FgjM3Common.SK(c, "acidTick")) == 0)
            {
                c.SetLong(FgjM3Common.SK(c, "acidTick"), GameClock.Ticks);
                SetF(c, FgjM3Common.SK(c, "acidHp"), TeamHealth(c));
                c.SetInt(FgjM3Common.SK(c, "acidN"), inAcid);
            }
            else if (c.GetLong(FgjM3Common.SK(c, "acidTick")) > 0 && GetF(c, "acidLossCoat", -1f) < 0f && GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "acidTick")) >= GameClock.StepHz * 2)
            {
                SetF(c, "acidLossCoat", GetF(c, FgjM3Common.SK(c, "acidHp")) - TeamHealth(c));
            }
            Vector2 lakeIn = V(c, "lakeIn");
            bool arrived = AliveTeam(c).All(id => Vector2.Distance(PosOf(id), lakeIn) < 14f && AcidMarshService.ZoneAt(s, PosOf(id)) == null);
            if (!arrived)
            {
                return StepOutcome.Wait;
            }
            bool inside = t != null && Vector2.Distance(Centroid(c), center) < t.OuterRadius;
            return inside
                ? StepOutcome.Done($"有涂层：右键湖区 {Cell(lakeIn)} 不再拦，远征队穿过酸沼进湖区（离湖心 {F(Vector2.Distance(Centroid(c), center), "0")} 格）；" +
                                   $"酸沼里 2 个游戏秒全队掉耐久：没涂层 {F(GetF(c, "acidLossNoCoat"))}（{c.GetInt("acidN", 0)} 台在酸里）→ 有涂层 {F(GetF(c, "acidLossCoat", 0f))}；{TeamHp(c)}")
                : StepOutcome.Fail($"到了 {Cell(lakeIn)} 却不在领地圈里（离湖心 {F(Vector2.Distance(Centroid(c), center), "0")}，外缘 {t?.OuterRadius}）");
        }

        private static StepOutcome TickIntro(JourneyContext c)
        {
            CampaignState s = St;
            if (c.StepElapsed < 0.6)
            {
                c.SetInt("intro0", TerritoryIntroService.Posted);
                c.SetInt("introN0", NotifyCount(TerritoryIntroService.NotifyType));
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点机器列表失败：" + FgjM1Journey.UiFail(c));
            }
            int b = AliveTeam(c)[0];
            if (SignalPresence.CurrentMachineLogicId != b || WorldView.Director.Mode != ViewMode.Direct)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {Label(b)}（{SignalUplinkService.LastFeedbackText}）");
            }
            bool introduced = s.Factions?.TerritoriesIntroduced?.Contains(Clarity) == true;
            if (!introduced || NotifyCount(TerritoryIntroService.NotifyType) <= c.GetInt("introN0"))
            {
                return c.StepElapsed < 12 ? StepOutcome.Wait : StepOutcome.Fail($"接入 {Label(b)}（在沉淀湖里）后没有阵营介绍（已介绍 [{string.Join(",", s.Factions?.TerritoriesIntroduced ?? Array.Empty<string>())}]）");
            }
            if (c.StepElapsed < 4)
            {
                return StepOutcome.Wait; // 再过几个整秒：只发一次
            }
            int posted = TerritoryIntroService.Posted - c.GetInt("intro0");
            return posted == 1
                ? StepOutcome.Done($"接入 {Label(b)}（镜头直控）：第一次进沉淀湖，阵营介绍通知一次（“{Short(LastNotify(TerritoryIntroService.NotifyType))}”），记进已介绍")
                : StepOutcome.Fail($"阵营介绍发了 {posted} 次");
        }

        private static string Short(string s) => s == null ? string.Empty : s.Length > 80 ? s.Substring(0, 80) + "…" : s;

        private static StepOutcome TickBackToStrategy(JourneyContext c)
        {
            if (c.StepElapsed < 1.2)
            {
                return StepOutcome.Wait;
            }
            return WorldView.Director.Mode == ViewMode.Strategy
                ? StepOutcome.Done($"回到战略视角（信号在核心 {SignalPresence.AtCore}）")
                : StepOutcome.Retry($"按 M 后镜头 {WorldView.Director.Mode}");
        }

        // ── 3. 据点与爆燃 ───────────────────────────────────────────────────────────

        private static OutpostRecord Outpost(JourneyContext c) => WorldOutpostSystem.Find(St, c.Get("op.id", string.Empty));

        /// <summary>
        /// 事件夹具（DEBT-FG10E2E01-03）：按种子生成的湖区据点（驻军 6～10）都在信号覆盖外——远征队收不到攻击命令。
        /// 在远征队旁、覆盖里、往湖心约 40 格处登记一处 1 级、小股驻军（含油污爬行者的最小人数，≤ 6）的澄净据点（WorldOutpostSystem.SpawnOutpost 同一记录，驻军编成按 fg.TbTerritoryControl 循环：喷洒车、腐蚀蜂群、油污爬行者），
        /// 不增援。醒来、展开、交战、油污爬行者着火爆燃、击毁全走正式系统。
        /// </summary>
        private static void ApplyOutpostFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (c.Get("op.id", string.Empty).Length > 0)
            {
                return;
            }
            Vector2 me = Centroid(c);
            Vector2 dir = (LakeCenter - me).normalized;
            for (int k = 0; k < 24; k++)
            {
                float ang = (k % 8) * 20f * Mathf.Deg2Rad * (k % 2 == 0 ? 1 : -1);
                float dist = 40f + (k / 8) * 8f;
                var d = new Vector2(dir.x * Mathf.Cos(ang) - dir.y * Mathf.Sin(ang), dir.x * Mathf.Sin(ang) + dir.y * Mathf.Cos(ang));
                GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(me.x + d.x * dist), Mathf.RoundToInt(me.y + d.y * dist)), 8);
                var gp = new Vector2(g.X, g.Y);
                if (!SignalCoverageService.Sample(HomeValleyLayout.RegionId, gp).Covered || AcidMarshService.ZoneAt(s, gp) != null)
                {
                    continue;
                }
                OutpostRecord o = WorldOutpostSystem.SpawnOutpost(s, Clarity, g, withPatrol: false);
                o.Kind = WorldOutpostSystem.KindTerritory;
                o.Tier = 1;
                // 驻军人数取编成里第一次出现油污爬行者的最小人数（≤ 6）。
                int n = 3;
                var types = new List<string>();
                for (; n < 6; n++)
                {
                    TerritoryControlService.GarrisonEnemyTypes(Clarity, n, types);
                    if (types.Contains("enemy_oil_crawler"))
                    {
                        break;
                    }
                }
                o.Garrison = n;
                o.GarrisonCap = n;
                o.ReinforceStopped = true;
                // 夹具据点离湖区中继群最近，到点的领地骚扰（territory.harass.units = 3）会从它抽人、把驻军抽到爬行者之前：本段推迟一次骚扰。
                TerritoryControlRecord tr = TerritoryControlService.Find(s, Clarity);
                if (tr != null)
                {
                    tr.NextHarassTick = Math.Max(tr.NextHarassTick, GameClock.Ticks + TerritoryControlService.HarassTicks);
                }
                c.Set("op.id", o.OutpostId);
                c.SetLong("defl0", Home.Kernel.Counters.Deflagrations);
                c.SetLong("slick0", Home.Kernel.Counters.SlicksIgnited);
                c.SetInt("defRule0", RuleCount(Deflagrate));
                return;
            }
        }

        private static StepOutcome TickOutpostFixture(JourneyContext c)
        {
            OutpostRecord o = Outpost(c);
            if (o == null)
            {
                return StepOutcome.Fail("远征队附近找不到覆盖里、不在酸里、能走的地方放据点");
            }
            var types = new List<string>();
            TerritoryControlService.GarrisonEnemyTypes(Clarity, o.Garrison, types);
            string gen = string.Join("、", WorldOutpostSystem.Outposts(St).Where(x => x != null && !x.Destroyed && x.TerritoryId == Clarity && x.Kind == WorldOutpostSystem.KindTerritory && x.OutpostId != o.OutpostId)
                .Select(x => $"{x.OutpostId}（等级 {x.Tier}、驻军 {x.Garrison}、覆盖 {SignalCoverageService.Sample(HomeValleyLayout.RegionId, new Vector2(x.CellX, x.CellY)).Covered}）"));
            return types.Contains("enemy_oil_crawler")
                ? StepOutcome.Done($"事件夹具：据点 {o.OutpostId}（{o.CellX},{o.CellY}，离远征队 {F(Vector2.Distance(new Vector2(o.CellX, o.CellY), Centroid(c)), "0")} 格，在覆盖里），驻军 {string.Join("、", types)}；按种子生成的湖区据点：{gen}")
                : StepOutcome.Fail($"据点驻军编成里没有油污爬行者（{string.Join("、", types)}）");
        }

        private static StepOutcome TickOutpostEngaged(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            OutpostRecord op = Outpost(c);
            var at = new Vector2(op.CellX, op.CellY);
            Vector2 toward = (Centroid(c) - at).normalized;
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0)
            {
                // 停在迎战半径（territory.engage.radius_cells）以内、离据点 16 格：叫醒驻军，又不贴脸（油污爬行者自爆 / 爆燃的范围只有几格）。
                GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(at.x + toward.x * 16f), Mathf.RoundToInt(at.y + toward.y * 16f)), 6);
                if (!JourneyInput.Holding && FgjM3Common.TryClick(new Vector2(g.X, g.Y), 1))
                {
                    c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                }
                return StepOutcome.Wait;
            }
            IReadOnlyList<int> vsel = Squad?.Selection ?? Array.Empty<int>();
            float nearest = vsel.Count == 0 ? -1f : vsel.Min(id => Vector2.Distance(PosOf(id), at));
            if ((int)(c.StepElapsed / 8) > c.GetInt(FgjM3Common.SK(c, "olog")))
            {
                c.SetInt(FgjM3Common.SK(c, "olog"), (int)(c.StepElapsed / 8));
                c.Log($"[{F(c.StepElapsed, "0")}s] 先锋 [{string.Join("、", vsel.Select(id => $"{Label(id)} 命令{Cmd(id)} 离据点 {F(Vector2.Distance(PosOf(id), at), "0")}"))}]；据点迎战 {op.Engaged}、休眠 {op.Dormant}；命令栏“{LastEvent()}”");
                // 走到了还没叫醒（停在迎战半径外）：再往里右键 5 格，最近到 8 格。
                int tries = c.GetInt(FgjM3Common.SK(c, "closer"));
                if (!op.Engaged && c.StepElapsed > 8 && tries < 3 && vsel.All(id => Cmd(id) != (int)CombatCommandKind.Move) && !JourneyInput.Holding)
                {
                    float r = 16f - 4f * (tries + 1);
                    GridCell g2 = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(at.x + toward.x * r), Mathf.RoundToInt(at.y + toward.y * r)), 6);
                    if (FgjM3Common.TryClick(new Vector2(g2.X, g2.Y), 1))
                    {
                        c.SetInt(FgjM3Common.SK(c, "closer"), tries + 1);
                    }
                }
            }
            if (op.Engaged && Home.CountSiegeGroup(op.EngageKey) > 0)
            {
                return StepOutcome.Done($"右键据点旁的地面（再靠近 {c.GetInt(FgjM3Common.SK(c, "closer"))} 次，最近 {F(nearest, "0")} 格）：先锋走近，据点 {op.OutpostId} 驻军醒来就地展开 {Home.CountSiegeGroup(op.EngageKey)} 台（{TeamHp(c)}）");
            }
            if (op.Destroyed)
            {
                return StepOutcome.Fail($"据点 {op.OutpostId} 还没迎战就没了");
            }
            return StepOutcome.Wait;
        }

        /// <summary>右键目标：离 <paramref name="near"/> 最近的活着的敌方单位，带引信的（油污爬行者）优先——像玩家一样先点冲过来自爆的那台。O(内核单位)，旅程机器人每 3 秒一次。</summary>
        private static int PickHostile(CombatSite site, Vector2 near, float radius)
        {
            int best = 0, bestFuse = 0;
            double d2 = radius * radius, d2Fuse = radius * radius;
            for (int i = 0; site?.Kernel != null && i < site.Kernel.SlotCount; i++)
            {
                CombatUnitView v = site.Kernel.ViewAt(i);
                if ((v.Flags & CombatUnitFlags.Alive) == 0 || v.Faction == CombatFaction.Player || v.Faction == CombatFaction.Neutral)
                {
                    continue;
                }
                double dx = v.Position.x - near.x, dy = v.Position.y - near.y, dd = dx * dx + dy * dy;
                if (dd < d2)
                {
                    d2 = dd;
                    best = v.Id;
                }
                if (dd < d2Fuse && site.TryGetClarity(v.Id, out CombatClarityUnit cl) && cl.Fuse != 0)
                {
                    d2Fuse = dd;
                    bestFuse = v.Id;
                }
            }
            return bestFuse != 0 ? bestFuse : best;
        }

        private static int RuleCount(string reactionId)
        {
            CombatSite site = Home;
            for (int i = 0; site?.Kernel != null && i < NamedReactionCatalog.TagRules.Count; i++)
            {
                if (NamedReactionCatalog.IdOfRule(i) == reactionId)
                {
                    return site.Kernel.ReactionCountOf(i);
                }
            }
            return 0;
        }

        private static StepOutcome TickOutpostCleared(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            OutpostRecord op = Outpost(c);
            CombatSite site = Home;
            if (op == null || site == null)
            {
                return StepOutcome.Fail("据点 / 家园内核不见了");
            }
            if (AliveTeam(c).Count == 0)
            {
                return StepOutcome.Fail("远征队全灭");
            }
            var at = new Vector2(op.CellX, op.CellY);
            int left = op.Engaged ? site.CountSiegeGroup(op.EngageKey) : 0;
            long defl = site.Kernel.Counters.Deflagrations - c.GetLong("defl0");
            long slick = site.Kernel.Counters.SlicksIgnited - c.GetLong("slick0");
            if ((int)(c.StepElapsed / 1.5) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 1.5));
                c.Log($"[{F(c.StepElapsed, "0")}s] 据点 {op.OutpostId} 迎战 {op.Engaged} 剩 {left} 驻军 {op.Garrison} 摧毁 {op.Destroyed}；爆燃 +{defl}、油迹点燃 +{slick}；{TeamHp(c)}；右键 {c.GetInt("op.rc")} 次");
            }
            if (left > 0 || (!op.Destroyed && op.Garrison > 0 && op.Engaged))
            {
                if (!JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) > 3000)
                {
                    int u = site.FindNearestHostile(at, 60f); // 先打离据点最近的（爬行者冲过来时身上挂了火，被击毁就爆燃：先锋要挨，后队不挨）
                    if (u > 0 && site.TryGetUnitPosition(u, out double2 p) && FgjM3Common.TryClick(new Vector2((float)p.x, (float)p.y), 1))
                    {
                        c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                        c.SetInt("op.rc", c.GetInt("op.rc") + 1);
                    }
                }
                return StepOutcome.Wait;
            }
            if (!op.Destroyed && op.Garrison > 0)
            {
                // 收拢回去了（附近没机器）：再右键据点那边的地面。
                if (!JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "reAt")) > 4000 && FgjM3Common.TryClick(at, 1))
                {
                    c.SetLong(FgjM3Common.SK(c, "reAt"), NowMs());
                }
                return StepOutcome.Wait;
            }
            if (defl <= 0 && RuleCount(Deflagrate) - c.GetInt("defRule0") <= 0)
            {
                return StepOutcome.Fail($"据点驻军清空了，但没有爬行者爆燃、具名爆燃计数也没增加（爬行者爆燃 +{defl}、油迹点燃 +{slick}；具名爆燃 {RuleCount(Deflagrate) - c.GetInt("defRule0")}；摧毁 {op.Destroyed}、驻军 {op.Garrison}、迎战 {op.Engaged}、展开 {op.UnfoldedCount}；{TeamHp(c)}；右键 {c.GetInt("op.rc")} 次）");
            }
            bool named = NamedReactionCatalog.IsNamed(St, Deflagrate);
            return StepOutcome.Done($"右键攻击 {c.GetInt("op.rc")} 次，据点 {op.OutpostId} 驻军清空（摧毁 {op.Destroyed}）：燃迹点着的油污爬行者被击毁 → 爆燃 {defl} 次、油迹点燃 {slick} 次；" +
                                    $"具名反应“爆燃”已开放 {named}、本段内核计数 +{RuleCount(Deflagrate) - c.GetInt("defRule0")}；{TeamHp(c)}");
        }

        // ── 4. 洗礼塔 ───────────────────────────────────────────────────────────────

        private static StepOutcome TickBossEngaged(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (!BaptismTowerBoss.TryArena(s, out Vector2 arena))
            {
                return StepOutcome.Fail("没有湖心平台锚点");
            }
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0)
            {
                c.SetInt("engages0", BaptismTowerBoss.Engages);
                c.SetInt("grants0", BaptismTowerBoss.RewardGrants);
                c.SetInt("packets0", LootPickService.Count(s));
                c.SetInt("core0", HomeInventory.Stock(s, "reactor_core"));
                Vector2 dir = (Centroid(c) - arena).normalized;
                GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(arena.x + dir.x * BaptismTowerBoss.PumpRing * 0.6f), Mathf.RoundToInt(arena.y + dir.y * BaptismTowerBoss.PumpRing * 0.6f)), 8);
                SetV(c, "arenaIn", new Vector2(g.X, g.Y));
                // 湖区按种子分布着 4～5 级据点（驻军 8～10）：直线开过去会沿途叫醒好几座、到平台前就打残。像玩家一样绕开它们：先规划绕行路点，逐个右键地面。
                List<Vector2> wps = c.Get("boss.wps", string.Empty).Length > 0 ? null : PlanDetour(Centroid(c), new Vector2(g.X, g.Y));
                if (wps != null)
                {
                    c.Set("boss.wps", string.Join(";", wps.Select(w => F(w.x, "0") + "," + F(w.y, "0"))));
                    c.SetInt("boss.wpi", 0);
                    c.Log($"绕开湖区据点 / 巡逻（迎战半径 {F(TerritoryControlService.EngageRadius, "0")} 格）规划路点 {wps.Count} 个：{c.Get("boss.wps")}");
                }
                string[] all = c.Get("boss.wps").Split(';');
                int wi = c.GetInt("boss.wpi");
                Vector2 wp = ParseV(all[wi]);
                bool last = wi >= all.Length - 1;
                if (c.GetInt(FgjM3Common.SK(c, "wpClicked")) == 0)
                {
                    if (!JourneyInput.Holding && FgjM3Common.TryClick(wp, 1))
                    {
                        c.SetInt(FgjM3Common.SK(c, "wpClicked"), 1);
                        c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
                        if (last)
                        {
                            c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                        }
                    }
                }
                else if (NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) > 1500 && (Vector2.Distance(Centroid(c), wp) < 14f || AliveTeam(c).All(id => Cmd(id) != (int)CombatCommandKind.Move)))
                {
                    c.Log($"[{F(c.StepElapsed, "0")}s] 到路点 {wi + 1}/{all.Length} {all[wi]}（重心离它 {F(Vector2.Distance(Centroid(c), wp), "0")} 格）；醒着迎战的据点 {EngagedOutposts()}；{TeamHp(c)}");
                    c.SetInt("boss.wpi", wi + 1);
                    c.SetInt(FgjM3Common.SK(c, "wpClicked"), 0);
                }
                if (AliveTeam(c).Count == 0)
                {
                    return StepOutcome.Fail("远征队在去平台的路上全灭");
                }
                else if ((int)(c.StepElapsed / 5) > c.GetInt(FgjM3Common.SK(c, "panLog")))
                {
                    c.SetInt(FgjM3Common.SK(c, "panLog"), (int)(c.StepElapsed / 5));
                    float2 f = WorldView.Director.StrategyFocus;
                    FgjM3Common.Clear(new Vector2(g.X, g.Y), out string why);
                    c.Log($"[{F(c.StepElapsed, "0")}s] 平移镜头去平台：镜头 ({F(f.x, "0")},{F(f.y, "0")})，目标 {FgjM3Common.Cell(g)}，{why}");
                }
                return StepOutcome.Wait;
            }
            if (c.GetInt(FgjM3Common.SK(c, "evLogged")) == 0 && NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) > 700)
            {
                c.SetInt(FgjM3Common.SK(c, "evLogged"), 1);
                int moving = AliveTeam(c).Count(id => Cmd(id) == (int)CombatCommandKind.Move);
                c.Log($"右键平台 {c.Get("arenaIn")}：{moving}/{AliveTeam(c).Count} 台接到移动命令；命令栏“{LastEvent()}”");
                if (moving == 0)
                {
                    return StepOutcome.Retry($"右键平台后没有机器接到移动命令（“{LastEvent()}”）");
                }
            }
            if ((int)(c.StepElapsed / 15) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 15));
                c.Log($"[{F(c.StepElapsed, "0")}s] 进场中：离平台 {F(Vector2.Distance(Centroid(c), arena), "0")} 格；阶段 {BaptismTowerBoss.PhaseOf(s)}；迎战中的据点 {EngagedOutposts()}；{TeamHp(c)}");
            }
            if (BaptismTowerBoss.PhaseOf(s) != BaptismPhase.Fill || !BaptismTowerBoss.IsFighting(s))
            {
                return AliveTeam(c).Count == 0 ? StepOutcome.Fail("远征队全灭") : StepOutcome.Wait;
            }
            bool hud = BaptismTowerHud.Compose(s, out string title, out float hpPct, out _, out string hpLine, out _) && title.Contains(BaptismTowerBoss.PhaseName(BaptismPhase.Fill));
            bool parts = Home.TryGetEnemyUnit(BaptismTowerBoss.TowerInstanceId, out _) && Enumerable.Range(0, BaptismTowerBoss.PumpCount).All(i => Home.TryGetEnemyUnit(BaptismTowerBoss.PumpInstanceId(i), out _));
            return hud && parts && BaptismTowerBoss.Engages == c.GetInt("engages0") + 1
                ? StepOutcome.Done($"右键平台 {c.Get("arenaIn")}：进场开战（第 {BaptismTowerBoss.Record(s).Attempt} 次挑战，灌注阶段），反应塔与 4 座泵站进内核；HUD“{title}”（{hpLine}）；首领音乐层 {RaidAudioDirector.CurrentMusic}")
                : StepOutcome.Fail($"开战后：HUD {hud}（“{title}”）、部件进内核 {parts}、开战次数 {BaptismTowerBoss.Engages - c.GetInt("engages0")}");
        }

        private static Vector2 ParseV(string s)
        {
            string[] p = s.Split(',');
            return new Vector2(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
        }

        private static string EngagedOutposts()
        {
            string[] e = WorldOutpostSystem.Outposts(St).Where(o => o != null && o.Engaged && !o.Destroyed).Select(o => o.OutpostId).ToArray();
            return e.Length == 0 ? "无" : string.Join("、", e);
        }

        /// <summary>
        /// 旅程规划辅助（不是产品代码）：从 <paramref name="from"/> 到 <paramref name="to"/> 的绕行路点，避开没被摧毁的据点（迎战半径 × 1.6 + 巡逻半径）与巡逻当前位置（迎战半径 × 1.6）。
        /// 8 格粗网格上的 8 邻接最短路，再按视线抽稀；绕不开时退回直线（只剩终点）。地形通行由机器自己的寻路处理。
        /// </summary>
        private static List<Vector2> PlanDetour(Vector2 from, Vector2 to)
        {
            const float step = 8f;
            float er = TerritoryControlService.EngageRadius * 1.6f;
            var zones = new List<(Vector2 P, float R)>();
            foreach (OutpostRecord o in WorldOutpostSystem.Outposts(St))
            {
                if (o != null && !o.Destroyed && o.Kind != WorldOutpostSystem.KindSite)
                {
                    zones.Add((new Vector2(o.CellX, o.CellY), er + Math.Max(0, o.PatrolRadius)));
                }
            }
            foreach (PatrolRecord p in WorldOutpostSystem.Patrols(St))
            {
                if (p != null)
                {
                    zones.Add((new Vector2((float)p.PosX, (float)p.PosY), er));
                }
            }
            bool Blocked(Vector2 v) => Vector2.Distance(v, from) > er && Vector2.Distance(v, to) > 12f && zones.Any(z => Vector2.Distance(v, z.P) < z.R);
            bool Clear(Vector2 a, Vector2 b)
            {
                int n = Mathf.CeilToInt(Vector2.Distance(a, b) / 4f);
                for (int i = 1; i < n; i++)
                {
                    if (Blocked(Vector2.Lerp(a, b, i / (float)n)))
                    {
                        return false;
                    }
                }
                return true;
            }
            var result = new List<Vector2>();
            if (Clear(from, to))
            {
                result.Add(to);
                return result;
            }
            Vector2 min = Vector2.Min(from, to) - Vector2.one * 200f;
            int w = Mathf.CeilToInt((Mathf.Max(from.x, to.x) + 200f - min.x) / step) + 1;
            int h = Mathf.CeilToInt((Mathf.Max(from.y, to.y) + 200f - min.y) / step) + 1;
            Vector2 At(int i) => min + new Vector2(i % w, i / w) * step;
            int Idx(Vector2 v) => Mathf.Clamp(Mathf.RoundToInt((v.y - min.y) / step), 0, h - 1) * w + Mathf.Clamp(Mathf.RoundToInt((v.x - min.x) / step), 0, w - 1);
            var dist = new float[w * h];
            var prev = new int[w * h];
            var blocked = new bool[w * h];
            for (int i = 0; i < dist.Length; i++)
            {
                dist[i] = float.MaxValue;
                prev[i] = -1;
                blocked[i] = Blocked(At(i));
            }
            int s0 = Idx(from), goal = Idx(to);
            dist[s0] = 0f;
            var open = new SortedSet<(float D, int I)>{ (0f, s0) };
            while (open.Count > 0)
            {
                (float d, int u) = open.Min;
                open.Remove(open.Min);
                if (u == goal)
                {
                    break;
                }
                int ux = u % w, uy = u / w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int vx = ux + dx, vy = uy + dy;
                        if ((dx == 0 && dy == 0) || vx < 0 || vy < 0 || vx >= w || vy >= h || blocked[vy * w + vx])
                        {
                            continue;
                        }
                        int v = vy * w + vx;
                        float nd = d + ((dx != 0 && dy != 0) ? 1.414f : 1f);
                        if (nd < dist[v])
                        {
                            open.Remove((dist[v], v));
                            dist[v] = nd;
                            prev[v] = u;
                            open.Add((nd, v));
                        }
                    }
                }
            }
            if (prev[goal] < 0)
            {
                result.Add(to);
                return result;
            }
            var path = new List<Vector2>();
            for (int i = goal; i >= 0; i = prev[i])
            {
                path.Add(At(i));
            }
            path.Reverse();
            path[path.Count - 1] = to;
            Vector2 cur = from;
            int k = 0;
            while (k < path.Count - 1)
            {
                int far = k + 1;
                for (int j = path.Count - 1; j > k + 1; j--)
                {
                    if (Clear(cur, path[j]))
                    {
                        far = j;
                        break;
                    }
                }
                cur = path[far];
                GridCell gc = far == path.Count - 1 ? new GridCell(Mathf.RoundToInt(cur.x), Mathf.RoundToInt(cur.y)) : SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(cur.x), Mathf.RoundToInt(cur.y)), 6);
                result.Add(new Vector2(gc.X, gc.Y));
                k = far;
            }
            return result;
        }

        private static StepOutcome TickBossFight(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            BaptismPhase phase = BaptismTowerBoss.PhaseOf(s);
            string seen = c.Get("phases", string.Empty);
            if (!seen.Contains(phase.ToString()))
            {
                c.Set("phases", seen + (seen.Length > 0 ? "→" : string.Empty) + phase);
                c.Log($"[{F(c.StepElapsed, "0")}s] 进入{BaptismTowerBoss.PhaseName(phase)}（{phase}）；{TeamHp(c)}");
            }
            if (phase == BaptismPhase.Defeated)
            {
                string ph = c.Get("phases", string.Empty);
                int iF = ph.IndexOf(nameof(BaptismPhase.Fill), StringComparison.Ordinal);
                int iR = ph.IndexOf(nameof(BaptismPhase.React), StringComparison.Ordinal);
                int iV = ph.IndexOf(nameof(BaptismPhase.Vent), StringComparison.Ordinal);
                if (iF < 0 || iR <= iF || iV <= iR)
                {
                    return StepOutcome.Fail($"洗礼塔被击毁，但没有依次经过灌注 → 反应 → 泄压（阶段 {ph}）");
                }
                return StepOutcome.Done($"右键反应塔 {c.GetInt("boss.rc")} 次：阶段 {c.Get("phases")}；灌注 {BaptismTowerBoss.FillsPoured} 片、主动反应 {BaptismTowerBoss.ReactionsTriggered} 次、泄压 {BaptismTowerBoss.VentsOpened} 次、复位 {BaptismTowerBoss.Resets} 次；{TeamHp(c)}");
            }
            if (AliveTeam(c).Count == 0)
            {
                return StepOutcome.Fail($"远征队全灭（阶段 {c.Get("phases")}；反应塔 {BaptismTowerBoss.Tower(s)?.Health:0}/{BaptismTowerBoss.Tower(s)?.MaxHealth:0}）");
            }
            if (!BaptismTowerBoss.IsEngaged(s))
            {
                return StepOutcome.Fail($"首领战被复位 / 没在打（阶段 {phase}，复位 {BaptismTowerBoss.Resets} 次）");
            }
            if ((int)(c.StepElapsed / 15) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 15));
                RegionEnemyRecord tw = BaptismTowerBoss.Tower(s);
                c.Log($"[{F(c.StepElapsed, "0")}s] {BaptismTowerBoss.PhaseName(phase)}：反应塔 {F(tw?.Health ?? 0, "0")}/{F(tw?.MaxHealth ?? 0, "0")}、泵站存活 {BaptismTowerBoss.PumpsAlive(s)}、伤害倍率 {F(BaptismTowerBoss.DamageMult(s), "0.##")}；{TeamHp(c)}；攻击中 {AliveTeam(c).Count(id => Cmd(id) == (int)CombatCommandKind.Attack)} 台");
            }
            // 有人没在打反应塔（被灌注 / 召来的小兵打断、刚开战）：右键反应塔（攻击）。
            int attacking = AliveTeam(c).Count(id => Cmd(id) == (int)CombatCommandKind.Attack);
            if (attacking < AliveTeam(c).Count && !JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) > 3000)
            {
                Vector2 tower = BaptismTowerBoss.PartPosition(s, BaptismTowerBoss.TowerInstanceId);
                if (FgjM3Common.TryClick(tower, 1))
                {
                    c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                    c.SetInt("boss.rc", c.GetInt("boss.rc") + 1);
                }
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickBossWon(JourneyContext c)
        {
            if (c.StepElapsed < 2.0)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            bool once = BaptismTowerBoss.RewardGrants == c.GetInt("grants0") + 1;
            LootPackageRecord packet = LootPickService.Packets(s).LastOrDefault(p => p != null && p.Source == LootPickService.SourceBoss);
            bool pick = packet != null && packet.Faction == Clarity && LootPickService.Count(s) >= c.GetInt("packets0") + 1;
            bool core = HomeInventory.Stock(s, "reactor_core") == c.GetInt("core0") + 1;
            bool fw = FirmwareLibrary.CountHeld(s, "fw_amplify", out _) > 0;
            bool pacified = TerritoryControlService.Find(s, Clarity)?.LeaderDefeated == true;
            bool gone = Home != null && !Home.TryGetEnemyUnit(BaptismTowerBoss.TowerInstanceId, out _);
            return once && pick && core && fw && pacified && gone
                ? StepOutcome.Done($"洗礼塔击毁：奖励发一次（固件反应增幅到手、首领三选一数据包 {packet.PacketId}、反应核 {c.GetInt("core0")} → {HomeInventory.Stock(s, "reactor_core")}），澄净领地平定；通知“{Short(LastNotify(BaptismTowerBoss.NotifyType))}”")
                : StepOutcome.Fail($"击毁后：奖励 {BaptismTowerBoss.RewardGrants - c.GetInt("grants0")} 次、首领数据包 {pick}、反应核 {core}、反应增幅 {fw}、平定 {pacified}、反应塔撤出内核 {gone}");
        }

        // ── 5. 回家：化工突袭、再污染、重新净化 ─────────────────────────────────────

        /// <summary>
        /// 家园夹具（DEBT-FG10E2E01-02）：核心配电范围里一座净化塔 + 南口两格管线 + 测试供冷却液点（FgPurificationSelfCheck.LayTower 同一测试台，“上游有调配站 / 储罐”；
        /// 冷却液线的正式施工由 FGJ-M7 覆盖）；发电机修好 + 一座发电机 2（供电由 FGJ-M7 / M8 覆盖）。之后突袭导演按正式规则排定“针对净化塔”的首次突袭（1 游戏日后），
        /// 夹具只把这 1 个游戏日的等待快进到点（延迟本身 FgClarityRaidSelfCheck B1 断言）；计划、筹备、出发、预警、攻城、投放、结算、净化全走正式系统。
        /// </summary>
        private static void ApplyHomeFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("pur.id", string.Empty).Length > 0)
            {
                return;
            }
            FgProductionSelfCheck.PowerUp(s);
            FgPurificationSelfCheck.Rig rig = FgPurificationSelfCheck.LayTower(s, "m10_purifier", true, 8000);
            if (rig?.Tower == null)
            {
                c.Set("pur.err", "核心配电范围里放不下净化塔测试台");
                return;
            }
            c.Set("roster.lake", c.Get("roster"));
            GridCell core = HomeGridService.CorePivot(s);
            var ids = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                GridCell at = SiegeService.NearestPassable(new GridCell(core.X - 6 + i * 3, core.Y - 9), 10);
                MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, BpFire, HomeValleyLayout.RegionId, new Vector2(at.X, at.Y), 100f, 100f, "Player", 1);
                if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
                {
                    MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
                    ids.Add(r.LogicId);
                }
            }
            c.Set("roster", string.Join(",", ids.Select(x => x.ToString(CultureInfo.InvariantCulture))));
            c.Set("pur.id", rig.Tower.BuildingId);
            c.SetInt("pur.x", rig.Pivot.X);
            c.SetInt("pur.y", rig.Pivot.Y);
            c.SetInt("trig0", s.Factions.ClarityRaidTriggers);
            c.SetInt("landed0", ClarityRaidService.Landed);
        }

        private static BuildingRecord Purifier(JourneyContext c) => HomeGridService.FindBuilding(St, c.Get("pur.id", string.Empty));

        private static GridCell PurCell(JourneyContext c) => new GridCell(c.GetInt("pur.x"), c.GetInt("pur.y"));

        private static StepOutcome TickHomeFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (c.Get("pur.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail("家园夹具：" + c.Get("pur.err"));
            }
            if (c.StepElapsed < 2.2)
            {
                return StepOutcome.Wait; // 澄净突袭服务按整秒排定
            }
            FactionState f = s.Factions;
            if (c.GetLong("due") == 0)
            {
                long due = f.ClarityRaidNextTick;
                long delay = RaidDirectorService.DayTicks(ClarityRaidService.FirstDelayDays);
                if (f.ClarityRaidTriggers == c.GetInt("trig0") && (due <= GameClock.Ticks || due - GameClock.Ticks > delay))
                {
                    return c.StepElapsed < 8 ? StepOutcome.Wait
                        : StepOutcome.Fail($"有净化塔 + 第 2 幕后没有排出“针对净化塔”的首次突袭（下次 {due}、现在 {GameClock.Ticks}、首次延迟 {delay} 步；澄净在地图上 {ClarityRaidService.ClarityVisible(s)}、净化塔 {ClarityRaidService.StandingPurifiers(s)} 座）");
                }
                c.SetLong("due", Math.Max(1, due));
                c.SetLong("dueLeft", due - GameClock.Ticks);
                if (f.ClarityRaidTriggers == c.GetInt("trig0") && due > GameClock.Ticks)
                {
                    f.ClarityRaidNextTick = GameClock.Ticks; // 夹具：快进首次延迟的剩余部分
                }
            }
            ProductionService.Producer pp = ProductionService.TryGet(s, c.Get("pur.id"), out ProductionService.Producer p) ? p : null;
            BuildingRecord b = Purifier(c);
            return StepOutcome.Done($"家园夹具：净化塔 {FgjM3Common.Cell(PurCell(c))}（{b?.PowerState}，{(pp != null ? ProductionService.StateText(pp) : "?")}）；突袭导演排了“针对净化塔”的首次突袭，" +
                                    $"还要 {F(c.GetLong("dueLeft") / (double)Math.Max(1, GameClock.StepHz), "0")} 游戏秒（≤ {ClarityRaidService.FirstDelayDays} 游戏日）→ 快进到点");
        }

        private static StepOutcome TickHomeFocused(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            float2 f = WorldView.Director.StrategyFocus;
            float d = Vector2.Distance(new Vector2(f.x, f.y), CorePos);
            return d < 40f ? StepOutcome.Done($"镜头回到家园（离核心 {F(d, "0")} 格）") : StepOutcome.Retry($"按回到核心键后镜头离核心 {F(d, "0")} 格");
        }

        private static StepOutcome TickGoHome(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            Vector2 core = CorePos;
            Vector2 pur = new Vector2(c.GetInt("pur.x"), c.GetInt("pur.y"));
            // 远征队停在核心外侧（离净化塔 60 格，朝湖的方向）：别在无人机投放之前就把它们打下来。
            Vector2 hold = core + (LakeCenter - core).normalized * 60f;
            GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(hold.x), Mathf.RoundToInt(hold.y)), 10);
            SetV(c, "hold", new Vector2(g.X, g.Y));
            IReadOnlyList<int> sel = Squad?.Selection;
            if (sel == null || !AliveTeam(c).All(sel.Contains))
            {
                return StepOutcome.Fail($"选择集里不是远征队了（[{string.Join("、", (sel ?? Array.Empty<int>()).Select(Label))}]）");
            }
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0)
            {
                if (!JourneyInput.Holding && FgjM3Common.TryClick(new Vector2(g.X, g.Y), 1))
                {
                    c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                    c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
                }
                return StepOutcome.Wait;
            }
            int moving = AliveTeam(c).Count(id => Cmd(id) == (int)CombatCommandKind.Move);
            return moving > 0
                ? StepOutcome.Done($"右键核心外侧 {FgjM3Common.Cell(g)}：留守队 {moving} 台接到移动命令（净化塔 {Cell(pur)}）")
                : c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Fail($"右键地面后远征队没接到移动命令（“{LastEvent()}”）");
        }

        private static RaidPlanRecord PurifyPlan(CampaignState s) =>
            s.Raids?.Director?.Plans?.LastOrDefault(p => p != null && p.Triggers != null && p.Triggers.Contains(RaidCatalog.TriggerPurification));

        private static StepOutcome TickRaidWarned(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            RaidPlanRecord plan = PurifyPlan(s);
            Vector2 hold = V(c, "hold");
            bool home = AliveTeam(c).All(id => Vector2.Distance(PosOf(id), hold) < 20f);
            if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                c.Log($"[{F(c.StepElapsed, "0")}s] 计划 {plan?.PlanId ?? "无"}（状态 {plan?.State}、阵营 {plan?.Faction}）；远征队离家 {F(Vector2.Distance(Centroid(c), hold), "0")} 格；{TeamHp(c)}");
            }
            if (plan == null || plan.Faction != RaidCatalog.FactionChemical)
            {
                return StepOutcome.Wait;
            }
            if (c.Get("plan.id", string.Empty).Length == 0)
            {
                c.Set("plan.id", plan.PlanId);
                c.Log($"突袭导演排出澄净“针对净化塔”的计划 {plan.PlanId}（触发 {string.Join("/", plan.Triggers)}，目标 {plan.TargetKind}:{plan.TargetId}）");
            }
            // 家园夹具续（DEBT-FG10E2E01-02）：计划已排定、但预警排在很久以后（突袭最短间隔从上一波抵达起算，RaidCatalog.MinIntervalDays）——
            // 与首次延迟同样快进：预警 / 出发 / 抵达整体前移同一段，路线与集结时长不变。排定本身由 FgClarityRaidSelfCheck 断言。
            long wait = plan.WarnTick - GameClock.Ticks;
            if (plan.State == RaidDirectorService.StateScheduled && wait > GameClock.StepHz * 30L && c.GetInt("plan.ff") == 0)
            {
                long shift = wait - GameClock.StepHz * 5L;
                plan.WarnTick -= shift;
                plan.DepartTick -= shift;
                plan.ArrivalTick -= shift;
                c.SetInt("plan.ff", 1);
                c.Log($"夹具：计划 {plan.PlanId} 的预警还要 {F(wait / (double)GameClock.StepHz, "0")} 游戏秒（最短间隔 {RaidCatalog.MinIntervalDays} 游戏日）→ 预警 / 出发 / 抵达整体快进 {F(shift / (double)GameClock.StepHz, "0")} 游戏秒");
            }
            if (plan.State < RaidDirectorService.StateWarned || !home)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"澄净“针对净化塔”的突袭 {plan.PlanId} 出发：预警“{Short(LastNotify("raid_warning"))}”；留守队站在 {Cell(hold)}（{TeamHp(c)}）");
        }

        private static StepOutcome TickRepolluted(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            GridCell center = PurCell(c);
            HomeGridMap map = HomeGridService.MapFor(s);
            if (c.GetInt(FgjM3Common.SK(c, "init")) == 0)
            {
                c.SetInt(FgjM3Common.SK(c, "init"), 1);
                c.SetInt("lvl0", map.GetPollution(center));
                c.SetInt("notes0", NotifyCount("pollution_raised"));
                c.SetInt("landedStep0", ClarityRaidService.Landed);
                c.SetLong("ret0", PurificationService.ReturnedArea(s));
            }
            if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                c.Log($"[{F(c.StepElapsed, "0")}s] 计划 {PurifyPlan(s)?.State}；剧场 {SiegeService.StateOf(s)?.TheaterActive}；投放目标 {Home?.PolluteTargetCount}；投放 {ClarityRaidService.Landed - c.GetInt("landedStep0")} 次；塔心污染 {map.GetPollution(center)}；{TeamHp(c)}");
            }
            if (ClarityRaidService.Landed <= c.GetInt("landedStep0"))
            {
                if (PurifyPlan(s) == null && !(SiegeService.StateOf(s)?.TheaterActive ?? false) && c.StepElapsed > 60)
                {
                    return StepOutcome.Fail("突袭结束了但没有一次投放（灭活无人机都被打下来了 / 没派）");
                }
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 2 + GetF(c, FgjM3Common.SK(c, "landT"), 0f) || GetF(c, FgjM3Common.SK(c, "landT"), -1f) < 0f)
            {
                if (GetF(c, FgjM3Common.SK(c, "landT"), -1f) < 0f)
                {
                    SetF(c, FgjM3Common.SK(c, "landT"), (float)c.StepElapsed);
                }
                return StepOutcome.Wait; // 通知 / 结算下一步才写
            }
            int lvl = map.GetPollution(center);
            BuildingRecord pur = Purifier(c);
            string note = LastNotify("pollution_raised");
            bool contaminated = pur != null && PurificationService.IsContaminated(s, pur.BuildingId);
            RaidResultRecord res = RaidResultService.StateOf(s)?.Results?.LastOrDefault(r => r != null && r.Repollutions > 0);
            string path = PurificationService.RepurifyPath(s, center, ClarityRaidService.RepolluteRadius);
            c.SetInt("lvlDrop", lvl);
            bool ok = lvl > c.GetInt("lvl0") && NotifyCount("pollution_raised") > c.GetInt("notes0") && note.Contains("重新净化") && contaminated && pur != null && path.Contains(BuildingOps.NameOf(pur));
            return ok
                ? StepOutcome.Done($"灭活无人机投放（{ClarityRaidService.Landed - c.GetInt("landedStep0")} 次）：塔心 {FgjM3Common.Cell(center)} 污染 {c.GetInt("lvl0")} → {lvl} 级（已净化区再污染），净化塔受污染；" +
                                   $"通知“{Short(note)}”；重新净化路径“{Short(path)}”；突袭结算记再污染 {res?.Repollutions ?? 0} 次 / {res?.RepollutedCells ?? 0} 格；归还面积 {c.GetLong("ret0")} → {PurificationService.ReturnedArea(s)}")
                : StepOutcome.Fail($"投放后：塔心 {c.GetInt("lvl0")} → {lvl} 级、通知 {NotifyCount("pollution_raised") - c.GetInt("notes0")} 条（“{Short(note)}”）、净化塔受污染 {contaminated}、路径“{Short(path)}”");
        }

        private static StepOutcome TickRaidCleared(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            CombatSite site = Home;
            if (AliveTeam(c).Count == 0)
            {
                return StepOutcome.Fail("远征队全灭");
            }
            bool theater = SiegeService.StateOf(s)?.TheaterActive ?? false;
            int u = PickHostile(site, CorePos, 150f);
            if ((int)(c.StepElapsed / 15) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 15));
                c.Log($"[{F(c.StepElapsed, "0")}s] 剧场 {theater}；最近敌人 #{u}；{TeamHp(c)}；右键 {c.GetInt("raid.rc")} 次");
            }
            if (!theater && u <= 0)
            {
                return c.GetInt("raid.rc") > 0 || c.StepElapsed > 3
                    ? StepOutcome.Done($"右键攻击 {c.GetInt("raid.rc")} 次：突袭结束、核心 150 格内没有敌人、剧场关闭；结算“{Short(LastNotify("raid_result"))}”；{TeamHp(c)}")
                    : StepOutcome.Wait;
            }
            if (u > 0 && !JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) > 3000 && site.TryGetUnitPosition(u, out double2 p)
                && FgjM3Common.TryClick(new Vector2((float)p.x, (float)p.y), 1))
            {
                c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                c.SetInt("raid.rc", c.GetInt("raid.rc") + 1);
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickRepurified(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            GridCell center = PurCell(c);
            int lvl = HomeGridService.MapFor(s).GetPollution(center);
            ProductionService.Producer pp = ProductionService.TryGet(s, c.Get("pur.id"), out ProductionService.Producer p) ? p : null;
            if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                c.Log($"[{F(c.StepElapsed, "0")}s] 塔心污染 {lvl}；净化塔 {(pp != null ? ProductionService.StateText(pp) + "：" + ProductionService.ReasonText(s, pp) : "?")}；路径“{Short(PurificationService.RepurifyPath(s, center, ClarityRaidService.RepolluteRadius))}”");
            }
            if (lvl > c.GetInt("lvl0"))
            {
                return StepOutcome.Wait;
            }
            return pp != null && pp.Rec.PurifySupplied
                ? StepOutcome.Done($"净化塔有电有冷却液：塔心从 {c.GetInt("lvlDrop")} 级重新净化回 {lvl} 级（投放前 {c.GetInt("lvl0")} 级）；归还面积 {PurificationService.ReturnedArea(s)}；{ProductionService.StateText(pp)}")
                : StepOutcome.Fail($"塔心降回 {lvl} 级，但净化塔不在净化（{(pp != null ? ProductionService.ReasonText(s, pp) : "?")}）");
        }
    }
}
