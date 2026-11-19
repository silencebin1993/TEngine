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
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG12-E2E-01：M12 出口旅程 FGJ-M12（里程碑文档 FG-M12；00 契约 IC-REQ-021）。从主菜单“新建”出发（固定测试种子 42、标准难度），全程正式输入，
    /// RTS 口径：左键选择 / Shift 加选、右键地面 = 移动、右键敌人 = 攻击，按钮 / 快捷键只切状态；等待段用正式 3 倍速。
    ///
    /// 里程碑原文：采矿让震动值升到 80，看到警告 → 蠕虫破土，分节击毁 → 第一次信号幽灵，用反向接入夺回 → 轨道坠落，回收残骸 → 用标记器识别出一个诱饵终端 → 与商队完成一笔交易。
    /// 顺序：夹具进第 2 幕后第一次信号幽灵按剧情立刻出现（FGR-EVT-020），所以幽灵段排在最前，其余按原文顺序（轻量口径 a：不要求同一局按叙事顺序演）。
    /// 承接 FG12-EVT-03（幽灵段：选中被夺控的机器按接入键，3 秒后夺回、镜头不切直控、事件历史“已应对”与奖励）、FG12-EVT-04（坠落段：战略地图 + 小地图落点圈、
    /// 离核心 ≥ 12 格；右键把圈里的机器移出；撞击后圈内建筑受创、机器未受伤、残骸在落点、残骸处掉材料、历史“完成”）。
    ///
    /// 夹具（都写在步骤标题与报告里；被测行为全走正式系统）：开局第 2 幕进度与作战机器（DEBT-FG12E2E01-01）；重大威胁“2 游戏日一次”的间隔记到 2 日前（DEBT-FG12E2E01-02，
    /// 等两日由 FgEventDirectorSelfCheck 覆盖）；采矿区震动值此前已积到 79（提取钻建造与物流由 FGJ-M1 / FGJ-M4 覆盖）；坠落 / 商队经事件导演 Request 触发（卡片点名的调试入口）；
    /// 远征诱饵终端沿线的信号中继（建中继由 FGJ-M8 覆盖）；交易所需材料（经济由 FGJ-M4 覆盖）。
    /// 用法：bash tools/unity-journey.sh FGJ-M12；断点见 <see cref="Checkpoints"/>（--from 续跑）。
    /// </summary>
    public static class FgjM12Journey
    {
        public const string Id = "FGJ-M12";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private const int SquadSize = 6;
        private const float SquadHp = 600f;
        private const int DrillCount = 10;
        private const string PoleType = "power_pole";

        private static readonly string[] Checkpoints = { "m12_fix", "ghost_done", "worm_dead", "orbit_loot", "decoy_seen" };

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
                S("m12_fix", "开局夹具（DEBT-FG12E2E01-01）：第二幕进度——第 2 幕、铸造 / 澄净 / 静默领地平定（首领战由 FGJ-M9 / FGJ-M10 覆盖）", 20, ApplyFixture, TickFixture),

                // ── 1. 第一次信号幽灵：反向接入夺回（FG12-EVT-03 承接） ──
                S("ghost_seize", "第一次信号幽灵（FGR-EVT-020：进第 2 幕后剧情固定发生）：5 秒信号杂波预警 → 夺控一台己方机器（共用夺控事件 evt.seize）", 120, null, TickGhostSeized),
                S("ghost_sel", "左键点被夺控的那台机器（选中）", 60, null, TickSelectGhost, retries: 2),
                S("ghost_key", "按“接入 / 退出接入”键（默认 M）：反向接入（FG01 覆盖判定）——3 秒后夺回、镜头仍在战略视角（不切直控）", 40,
                    c => JourneyInput.PressAction(GameActionId.ToggleCameraView), TickGhostReclaim, retries: 1),
                S("ghost_done", "事件历史“已应对”、奖励一次（加密片段 + 技术数据）、共用夺控 / 归还事件计数各 +1", 30, null, TickGhostDone),

                // ── 2. 采矿震动 → 80 警告 → 蠕虫破土、分节击毁 ──
                S("team_fix", $"作战队夹具（DEBT-FG12E2E01-01）：第二幕家园的两座仓库（建造由 FGJ-M1 / M4 覆盖）；核心旁 {SquadSize} 台 ERC-003（铸造重炮）作战队（重炮来源由 FGJ-M1 覆盖；幽灵段之后才放出来，免得作战队的教义自卫把被夺控的工作机打瘫、反向接入作废）", 20, ApplyTeam, TickTeam),
                S("vib_fix", $"震动夹具：重大威胁间隔记到 2 日前（DEBT-FG12E2E01-02）；核心旁一片稀土矿脉上 {DrillCount} 座提取钻（建造由 FGJ-M1 / M4 覆盖），家园采矿区震动值此前已积到 79", 15, ApplyVibFixture, TickVibFixture),
                S("vib_warn", "提取钻工作把家园震动值推过 80：警告通知“掘进蠕虫会来……采矿区”，战略地图圈出震动最集中处", 120, null, TickVibWarn),
                S("w_sel", "左键点一台作战队、Shift 左键加选其余几台", 60, null, FgjM10Journey.TickSelectTeam, retries: 2),
                S("w_go", "右键震动最集中处旁的地面（移动）：作战队到采矿区旁布防", 120, null, TickWormGuard),
                S("worm_crack", "警告到点仍在 80 以上 → 事件导演开始蠕虫：破土前 30 秒地面开裂（通知 + 路线）", 120, null, TickWormCrack),
                S("worm_up", "蠕虫破土：8～12 节从最集中处钻出，该地点震动值归零", 90, null, TickWormUp),
                S("worm_fight", "右键钻出地面的蠕虫节（攻击）：分节击毁——每节掉稀有矿与组件，累计伤害够了整条倒下", 420, null, TickWormFight),
                S("worm_dead", "蠕虫被击杀：事件历史 success、三选一数据包、地上有蠕虫掉落", 30, null, TickWormDead),

                // ── 3. 轨道坠落：撤出落点圈、回收残骸（FG12-EVT-04 承接） ──
                S("orbit_plan", $"坠落准备：按（种子, 下一个事件序号）算出落点（OrbitalDropService.PickLanding，与导演同一函数）；落点圈里放一座电线杆（正式放置校验）", 15, ApplyOrbitPlan, TickOrbitPlan),
                S("o_sel", "左键点一台作战队、Shift 左键加选其余几台", 60, null, FgjM10Journey.TickSelectTeam, retries: 2),
                S("o_in", "右键落点处的地面（移动）：作战队站到之后的落点圈里", 120, null, TickOrbitIn),
                S("orbit_warn", "事件导演请求一次轨道坠落（EventDirectorService.Request，重大威胁间隔夹具 DEBT-FG12E2E01-02）：60 秒预警——战略地图与小地图都有落点圈与告警图标、落点离核心 ≥ 12 格", 60, ApplyOrbitRequest, TickOrbitWarn),
                S("o_out", "右键落点圈外靠核心一侧的地面（移动）：把圈里的机器全部撤出", 40, null, TickOrbitOut),
                S("orbit_hit", "撞击：圈内电线杆受重创、作战队未受伤、坠落残骸在落点、残骸处有材料掉落、事件历史“完成”", 60, null, TickOrbitHit),
                S("orbit_loot", "回收：坠落残骸处的材料由搬运单派机器运回家园仓库", 300, null, TickOrbitLoot),

                // ── 4. 标记器识别诱饵终端 ──
                S("decoy_fix", "诱饵夹具：离核心最近的一个诱饵终端（阵营领地里的旧终端，按种子抽中）沿线建信号中继（建中继由 FGJ-M8 覆盖）；一台带标记器的 ERC-003 停在终端外 25 格左右", 25, ApplyDecoyFixture, TickDecoyFixture),
                S("d_sel", "左键点带标记器的机器（选中）", 60, null, TickSelectMarker, retries: 2),
                S("d_go", "右键诱饵终端旁的地面（移动）：走到 10 格内", 240, null, TickDecoyGo),
                S("decoy_seen", "标记器扫描识别：通知“是诱饵终端，右键可安全拆除”，悬停 / 地图写“已识别”；没有触发伏击", 40, null, TickDecoySeen),

                // ── 5. 流浪商队交易 ──
                S("caravan_come", "事件导演请求一次流浪商队（EventDirectorService.Request）；仓库里有交易用的合金 / 精密零件 / 技术数据（经济夹具）：商队从迷雾外走来、停在家园附近", 120, ApplyCaravanRequest, TickCaravanArrived),
                S("caravan_open", "左键点主视图里的商队：打开交易面板", 60, null, TickCaravanOpen, retries: 2),
                S("caravan_buy", "交易面板点一件买得起的商品的“买入”：扣料、交付、页脚写买入结果", 30, null, TickCaravanBuy, retries: 2),
                S("caravan_close", "点交易面板的关闭", 15, c => JourneyInput.ClickUitk("[CaravanTradeHost]", "CaravanClose"), TickCaravanClosed, retries: 1),
            };
            return new JourneyDef
            {
                Id = Id,
                Title = "M12 出口：第一次信号幽灵反向接入夺回 → 采矿震动到 80 警告 → 蠕虫破土、分节击毁 → 轨道坠落撤出落点圈、回收残骸 → 标记器识别诱饵终端 → 与流浪商队完成一笔交易",
                Seed = TestSeed,
                TotalTimeoutSeconds = 2400,
                OnFinish = Cleanup,
                Version = 1,
                CheckpointAfter = Checkpoints,
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

        private static Vector2 CorePos => FgjM8Journey.CorePos;

        private static float Hp(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) && m != null ? m.Health : -1f;

        private static Vector2 V(JourneyContext c, string key)
        {
            string[] f = c.Get(key, "0,0").Split(',');
            return new Vector2(float.Parse(f[0], CultureInfo.InvariantCulture), float.Parse(f[1], CultureInfo.InvariantCulture));
        }

        private static void SetV(JourneyContext c, string key, Vector2 v) => c.Set(key, F(v.x, "0.##") + "," + F(v.y, "0.##"));

        private static string Cell(Vector2 p) => $"({F(p.x, "0")},{F(p.y, "0")})";

        private static string TeamHp(JourneyContext c) => string.Join("、", Team(c).Select(id => MachineRegistry.TryGetRecord(id, out MachineRecord m)
            ? $"{Label(id)} {(m.IsAlive ? F(m.Health, "0") : "阵亡")}" : $"{id}?"));

        private static List<string> EventTexts() =>
            NotificationCenter.History.Where(e => e.Type?.Id == "world_event").SelectMany(e => e.Members).Select(m => m.Detail ?? string.Empty).ToList();

        private static string Short(string s) => s == null ? string.Empty : s.Length > 100 ? s.Substring(0, 100) + "…" : s;

        private static EventDirectorInstanceRecord Inst(string eventId) => EventDirectorService.Active(St).FirstOrDefault(r => r != null && r.EventId == eventId);

        private static EventDirectorHistoryRecord LastHist(string eventId) => EventDirectorService.History(St).LastOrDefault(h => h != null && h.EventId == eventId);

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
            c.Log($"共用事件计数（全程）：夺控 {CombatSite.SeizeStarted}、归还 {CombatSite.SeizeEndedCount}；幽灵 夺控 {SignalGhostService.Seizes} / 夺回 {SignalGhostService.Reclaims} / 奖励 {SignalGhostService.Rewards}；" +
                  $"蠕虫破土 {WormEventService.Breakouts}；坠落撞击 {OrbitalDropService.Impacts}；诱饵 识别 {DecoyTerminalService.Identified} / 触发 {DecoyTerminalService.Sprung}；商队 到达 {CaravanService.Arrivals} / 买入 {CaravanService.Purchases}；" +
                  $"导演 预警 {EventDirectorService.WarnCount} / 开始 {EventDirectorService.StartCount} / 结束 {EventDirectorService.EndCount}");
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM3Common.FrameReport("全程"));
            JourneyCommon.Cleanup(c);
        }

        // ── 开局夹具 ────────────────────────────────────────────────────────────────

        internal static string SaveBlueprint(JourneyContext c, string key, string name, BlueprintCircuitBoard board)
        {
            if (c.Get(key, string.Empty).Length == 0)
            {
                BlueprintSaveResult save = BlueprintEditorService.TrySave(St, board, null, name, saveAsNewRecord: true);
                c.Set(key, save.Success ? save.BlueprintId : string.Empty);
                c.Set(key + ".note", $"保存 {save.Success}（{save.FailureReason}）");
            }
            return c.Get(key, string.Empty);
        }

        internal static int SpawnMachine(CampaignState s, string bpId, Vector2 at, float hp)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bpId, HomeValleyLayout.RegionId, at, hp, hp, "Player", 1);
            if (!r.Success || !MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                return 0;
            }
            MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            return r.LogicId;
        }

        /// <summary>开局夹具（DEBT-FG12E2E01-01）：第 2 幕、三领地平定；核心旁 6 台 ERC-003 铸造重炮作战队（重炮来源由 FGJ-M1 覆盖）。</summary>
        private static void ApplyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt("fix.done") == 1)
            {
                return;
            }
            if (s.Progress != null)
            {
                c.SetInt("act0", s.Progress.Act);
                s.Progress.Act = Math.Max(2, s.Progress.Act);
            }
            foreach (string f in new[] { EnemyDefs.FactionFoundry, EnemyDefs.FactionClarity, EnemyDefs.FactionSilent })
            {
                TerritoryControlService.MarkLeaderDefeated(s, f);
            }
            c.SetInt("fix.done", 1);
        }

        private static void ApplyTeam(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("roster", string.Empty).Length > 0)
            {
                return;
            }
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(new[] { ComponentCatalog.CompCannonId, ComponentCatalog.CompGunId, ComponentCatalog.FuncMarkerId }).Distinct().ToArray();
            string bpId = SaveBlueprint(c, "bp.id", "重炮作战", BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>()));
            if (bpId.Length == 0)
            {
                c.Set("fix.err", "保存“重炮作战”蓝图失败：" + c.Get("bp.id.note"));
                return;
            }
            Vector2 core = CorePos;
            var ids = new List<int>();
            var used = new List<GridCell>();
            for (int i = 0; i < SquadSize; i++)
            {
                GridCell at = default;
                for (int tr = i; tr < i + 40; tr++)
                {
                    at = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(core.x - 9f + (tr % 3) * 3f), Mathf.RoundToInt(core.y - 9f - (tr / 3) * 3f)), 10);
                    if (!used.Any(x => Math.Abs(x.X - at.X) + Math.Abs(x.Y - at.Y) < 2))
                    {
                        break;
                    }
                }
                used.Add(at);
                int id = SpawnMachine(s, bpId, new Vector2(at.X, at.Y), SquadHp);
                if (id > 0)
                {
                    ids.Add(id);
                }
            }
            c.Set("roster", string.Join(",", ids.Select(x => x.ToString(CultureInfo.InvariantCulture))));
            // 第二幕的家园已有一座仓库（建造由 FGJ-M1 / M4 覆盖）：没有仓库时固体材料容量为 0，地上的掉落只能“等仓库有空位”（return-waiting-space）。
            if (HomeInventory.Capacity(s, ItemCatalog.Find(ItemCatalog.ScrapId)) <= HomeValleyLayout.CoreCacheCapacity)
            {
                var whs = new List<string>();
                for (int i = 0; i < 2; i++)
                {
                    GridCell? wh = FgProductionSelfCheck.FindFree(s, HomeValleyLayout.BuildingTypeWarehouse, 5f, 20f);
                    whs.Add(wh.HasValue ? FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "m12wh" + i, wh.Value).BuildingId : "放不下");
                }
                c.Set("fix.wh", string.Join("、", whs));
            }
        }

        private static StepOutcome TickFixture(JourneyContext c) =>
            c.StepElapsed < 1.0 ? StepOutcome.Wait : StepOutcome.Done($"夹具生效：第 {c.GetInt("act0")} → {St.Progress.Act} 幕；铸造 / 澄净 / 静默领地平定");

        private static StepOutcome TickTeam(JourneyContext c)
        {
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait;
            }
            if (c.Get("fix.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail("夹具：" + c.Get("fix.err"));
            }
            List<int> team = Team(c);
            bool inKernel = team.Count == SquadSize && team.All(id => Home != null && Home.TryGetMachineUnit(id, out _));
            if (!inKernel)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail($"夹具后作战队没全进家园内核（{team.Count} 台）");
            }
            return StepOutcome.Done($"仓库 {c.Get("fix.wh", "原有")}（固体容量 {HomeInventory.Capacity(St, ItemCatalog.Find(CaravanService.AlloyId))}）；{SquadSize} 台重炮作战队在核心旁 [{string.Join("、", team.Select(id => Label(id) + "@" + Cell(PosOf(id))))}]");
        }

        // ── 1. 信号幽灵 ────────────────────────────────────────────────────────────

        private static StepOutcome TickGhostSeized(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            EventDirectorInstanceRecord r = SignalGhostService.Active(s);
            if (r != null && r.State == EventDirectorService.StateWarned && c.GetInt("gh.static") == 0)
            {
                c.SetInt("gh.static", SignalGhostService.StaticActive(s) ? 1 : 0);
            }
            if (r == null || r.State != EventDirectorService.StateActive || r.Target == 0 || !SignalSeizeService.IsSeized(r.Target))
            {
                return c.StepElapsed < 100 ? StepOutcome.Wait : StepOutcome.Fail($"进第 2 幕后 100 秒没出现被夺控的机器（实例 {r?.State}，历史 {LastHist(SignalGhostService.EventId)?.Outcome}）");
            }
            c.SetInt("gh.target", r.Target);
            c.SetInt("gh.serial", r.Serial);
            c.SetInt("gh.tech0", s.TechData);
            c.SetInt("gh.heard0", IntelService.StateOf(s)?.FragmentsHeard?.Length ?? 0);
            c.SetInt("gh.frag", IntelService.NextFragment(s) != null ? 1 : 0);
            c.SetInt("gh.seize0", CombatSite.SeizeStarted);
            c.SetInt("gh.release0", CombatSite.SeizeEndedCount);
            // 3 倍速下 5 秒预警只有约 1.7 秒真实时间，可能在上一步里就过去了：以导演实例的预警时长为准（杂波层画法由 FgSignalGhostSelfCheck D3 / D4 覆盖）。
            double warn = (r.StartTick - r.WarnTick) / (double)GameClock.StepHz;
            string msg = $"信号幽灵：预警（信号杂波）{F(warn, "0.0")} 秒{(c.GetInt("gh.static") == 1 ? "（本步观察到杂波）" : string.Empty)} → 夺控 {Label(r.Target)}（{Cell(PosOf(r.Target))}，来源信号幽灵）";
            return r.StartTick - r.WarnTick == GameClock.TicksFor(5) ? StepOutcome.Done(msg) : StepOutcome.Fail(msg + "（要求 5 秒预警）");
        }

        /// <summary>左键（第一台）/ Shift 左键（其余）点一组机器的模型，直到选择集正好是这组。</summary>
        internal static StepOutcome SelectMachines(JourneyContext c, List<int> ids, string what, bool keepPaused = false)
        {
            if (!keepPaused)
            {
                JourneyCommon.ResumeIfAutoPaused(c); // keepPaused：玩家主动战略暂停中点选（FGJ-M13 根权限阶段），不当成自动暂停去恢复
            }
            IReadOnlyList<int> sel = Squad?.Selection;
            if (sel != null && ids.Count > 0 && sel.Count == ids.Count && ids.All(sel.Contains) && c.GetInt(FgjM3Common.SK(c, "k")) >= ids.Count)
            {
                return StepOutcome.Done($"选中{what} [{string.Join("、", sel.Select(Label))}]");
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 700)
            {
                return StepOutcome.Wait;
            }
            int k = c.GetInt(FgjM3Common.SK(c, "k"));
            if (k >= ids.Count)
            {
                return StepOutcome.Retry($"点完一遍后选择集是 [{string.Join("、", (sel ?? Array.Empty<int>()).Select(Label))}]，应为 [{string.Join("、", ids.Select(Label))}]");
            }
            int id = ids[k];
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

        private static StepOutcome TickSelectGhost(JourneyContext c)
        {
            int t = c.GetInt("gh.target");
            if (!SignalSeizeService.IsSeized(t))
            {
                return StepOutcome.Fail($"选中前 {Label(t)} 已不在夺控中（45 秒到时 / 别的原因归还）");
            }
            return SelectMachines(c, new List<int> { t }, "被夺控的机器");
        }

        private static StepOutcome TickGhostReclaim(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            int t = c.GetInt("gh.target");
            if (c.GetLong(FgjM3Common.SK(c, "t0")) == 0)
            {
                c.SetLong(FgjM3Common.SK(c, "t0"), GameClock.Ticks);
                c.Set("gh.feedback", SignalUplinkService.LastFeedbackText ?? string.Empty);
            }
            if (WorldView.Director != null && (WorldView.Director.Mode == ViewMode.Direct || WorldView.Director.HeadingDirect))
            {
                c.SetInt("gh.camera", 1);
            }
            if (SignalSeizeService.IsSeized(t))
            {
                if (Home != null && Home.TryGetSeizedMachine(t, out _, out _, out float left, out _) && left > 0f)
                {
                    c.SetInt(FgjM3Common.SK(c, "began"), 1);
                    c.Set("gh.feedback", SignalUplinkService.LastFeedbackText ?? string.Empty);
                }
                if (Home != null && Home.TryGetSeizedMachine(t, out _, out bool downed, out _, out _) && downed)
                {
                    return StepOutcome.Fail($"{Label(t)} 在反向接入引导中被打瘫（瘫痪中的反向接入作废，FGR-EVT-022）");
                }
                if (c.StepElapsed > 4 && c.GetInt(FgjM3Common.SK(c, "began")) == 0)
                {
                    return StepOutcome.Retry($"按接入键后没有开始反向接入：反馈“{SignalUplinkService.LastFeedbackText}”、拒绝 {SignalUplinkService.LastFailure}；选择集 [{string.Join("、", (Squad?.Selection ?? Array.Empty<int>()).Select(Label))}]；" +
                                             $"暂停 {GameClock.Paused}、模态 {InputRouter.PanelModalOpen}、镜头 {WorldView.Director?.Mode}");
                }
                return c.StepElapsed < 30 ? StepOutcome.Wait : StepOutcome.Fail($"按键 30 秒后 {Label(t)} 仍被夺控（“{SignalUplinkService.LastFeedbackText}”）");
            }
            double took = (GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "t0"))) / (double)GameClock.StepHz;
            bool camOk = c.GetInt("gh.camera") == 0 && WorldView.Director.Mode == ViewMode.Strategy;
            bool timeOk = took >= 2.5 && took <= 4.0;
            string msg = $"按接入键（“{Short(c.Get("gh.feedback"))}”）→ {F(took, "0.00")} 游戏秒后 {Label(t)} 夺回；镜头一直在战略视角 {camOk}；当前接入 {SignalUplinkService.CurrentMachine(St)}";
            return camOk && timeOk && SignalUplinkService.CurrentMachine(St) != t ? StepOutcome.Done(msg) : StepOutcome.Fail(msg + "（要求 3 秒左右夺回、不切直控、信号不进那台机器）");
        }

        private static StepOutcome TickGhostDone(JourneyContext c)
        {
            CampaignState s = St;
            EventDirectorHistoryRecord h = LastHist(SignalGhostService.EventId);
            if (h == null || h.Serial != c.GetInt("gh.serial"))
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail("夺回后 10 秒信号幽灵还没进事件历史");
            }
            int tech = s.TechData - c.GetInt("gh.tech0");
            int frag = (IntelService.StateOf(s)?.FragmentsHeard?.Length ?? 0) - c.GetInt("gh.heard0");
            int wantTech = Mathf.RoundToInt(GridContent.TryGetTuning("event.signal_ghost.tech_data", out float tv) ? tv : 40f);
            bool fragOk = c.GetInt("gh.frag") == 1 ? frag == 1 : frag == 0;
            bool counts = CombatSite.SeizeEndedCount == c.GetInt("gh.release0") + 1 && SignalGhostService.Reclaims == 1 && SignalGhostService.Rewards == 1;
            string msg = $"事件历史 {EventDirectorService.OutcomeText(h.Outcome)}（{h.Outcome}，剧情 {h.Story}）；技术数据 +{tech}（应 +{wantTech}）、加密片段 +{frag}；共用计数 夺控 {CombatSite.SeizeStarted} / 归还 {CombatSite.SeizeEndedCount}；幽灵 夺回 {SignalGhostService.Reclaims} 奖励 {SignalGhostService.Rewards}";
            return h.Outcome == EventDirectorService.OutcomeSuccess && h.Story && tech >= wantTech && fragOk && counts ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        // ── 2. 震动与蠕虫 ──────────────────────────────────────────────────────────

        /// <summary>重大威胁“每 2 个游戏日最多一次”：把上一次记到 2 日前（DEBT-FG12E2E01-02；真实等两日由 FgEventDirectorSelfCheck 覆盖）。</summary>
        private static void SkipMajorGap(JourneyContext c, string tag)
        {
            DirectorEventState d = EventDirectorService.StateOf(St);
            if (d != null && d.LastMajorTick >= 0)
            {
                long gap = GameClock.TicksFor(GameClock.DaySeconds * EventDirectorService.MajorGapDays) + 1;
                c.Log($"{tag}：重大威胁间隔夹具 上次 {d.LastMajorTick} → {Math.Min(d.LastMajorTick, GameClock.Ticks - gap)}（现在 {GameClock.Ticks}）");
                d.LastMajorTick = Math.Min(d.LastMajorTick, GameClock.Ticks - gap);
            }
        }

        private static VibrationSiteRecord HomeVib => VibrationService.Site(VibrationService.StateOf(St), VibrationService.HomeSite, true);

        private static void ApplyVibFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt("vib.fixed") == 1)
            {
                return;
            }
            c.SetInt("vib.fixed", 1);
            SkipMajorGap(c, "震动夹具");
            FgProductionSelfCheck.PowerUp(s);
            var drills = new List<string>();
            for (int g = 0; g < 2; g++)
            {
                GridCell? o = FgProductionSelfCheck.FindArea(s, DrillCount / 2 * 3 + 1, 3, 10f, 30f);
                if (o == null)
                {
                    break;
                }
                for (int i = 0; i < DrillCount / 2; i++)
                {
                    GridCell cell = FgProductionSelfCheck.At(o.Value, i * 3, 1);
                    FgProductionSelfCheck.SetFootprint(s, FgjM4Common.Drill, cell, "ore_rare");
                    BuildingRecord b = FgProductionSelfCheck.Built(s, FgjM4Common.Drill, "m12drill" + g + "_" + i, cell);
                    drills.Add(b.BuildingId);
                }
            }
            c.Set("vib.drills", string.Join(",", drills));
            HomeVib.Value = 79;
            c.SetInt("vib.texts0", EventTexts().Count);
        }

        private static List<BuildingRecord> Drills(JourneyContext c) =>
            c.Get("vib.drills", string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(id => HomeGridService.FindBuilding(St, id)).Where(b => b != null).ToList();

        private static StepOutcome TickVibFixture(JourneyContext c)
        {
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            List<BuildingRecord> d = Drills(c);
            int working = VibrationService.WorkingDrills(VibrationService.HomeSite);
            if (d.Count != DrillCount)
            {
                return StepOutcome.Fail($"只放下 {d.Count} / {DrillCount} 座提取钻（核心旁找不到空地）");
            }
            if (working == 0)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Fail($"放下的 {DrillCount} 座提取钻没有一座在工作（生产状态 {FgProductionSelfCheck.P(St, d[0])?.State}）");
            }
            return StepOutcome.Done($"{DrillCount} 座提取钻（{working} 座工作中）在 {Cell(d[0].Position)} 一带；家园震动值 {F(HomeVib.Value, "0.0")}");
        }

        private static StepOutcome TickVibWarn(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            VibrationSiteRecord v = HomeVib;
            if (v.Level < VibrationService.LevelWarn)
            {
                return c.StepElapsed < 110 ? StepOutcome.Wait : StepOutcome.Fail($"震动值 {F(v.Value, "0.0")} 没到 80 警告（等级 {v.Level}，每分钟净变化 {F(v.LastNetPerMinute, "0.0")}）");
            }
            string warn = EventTexts().Skip(c.GetInt("vib.texts0")).FirstOrDefault(t => t.Contains("掘进蠕虫会来") && t.Contains("采矿区")) ?? string.Empty;
            var model = new WorldMapModel();
            GridCell core = HomeGridService.CorePivot(St);
            model.Collect(St, new WorldMapView { CenterX = core.X, CenterY = core.Y, HalfWidth = 200, CanvasWidth = 1000, CanvasHeight = 800 }, 256);
            bool marked = model.Items.Any(i => i.Id == "vibration:home");
            List<BuildingRecord> d = Drills(c);
            bool hotAtDrills = d.Count > 0 && Vector2.Distance(new Vector2(v.HotX, v.HotY), new Vector2(d.Average(b => b.Position.x), d.Average(b => b.Position.y))) < 12f;
            SetV(c, "hot", new Vector2(v.HotX, v.HotY));
            string msg = $"震动值 {F(v.Value, "0.0")} 过 80（等级 警告）：通知“{Short(warn)}”；战略地图标出最集中处 {marked}（{Cell(new Vector2(v.HotX, v.HotY))}，在提取钻一带 {hotAtDrills}）";
            return warn.Length > 0 && marked && hotAtDrills ? StepOutcome.Done(msg) : c.StepElapsed < 110 ? StepOutcome.Wait : StepOutcome.Fail(msg);
        }

        internal static StepOutcome RightClickGround(JourneyContext c, Vector2 at)
        {
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0)
            {
                if (!JourneyInput.Holding && FgjM3Common.TryClick(at, 1))
                {
                    c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                    c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
                }
                return StepOutcome.Wait;
            }
            if (!JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) > 20000 && FgjM3Common.TryClick(at, 1))
            {
                c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
                c.SetInt("reclicks", c.GetInt("reclicks") + 1);
            }
            return StepOutcome.Wait;
        }

        private static Vector2 GuardSpot(JourneyContext c)
        {
            Vector2 hot = V(c, "hot");
            Vector2 dir = (CorePos - hot).sqrMagnitude > 1f ? (CorePos - hot).normalized : Vector2.right;
            GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(hot.x + dir.x * 9f), Mathf.RoundToInt(hot.y + dir.y * 9f)), 8);
            return new Vector2(g.X, g.Y);
        }

        private static StepOutcome TickWormGuard(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            Vector2 at = GuardSpot(c);
            List<int> team = AliveTeam(c);
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 1 && team.Count > 0 && team.All(id => Vector2.Distance(PosOf(id), at) < 9f))
            {
                return StepOutcome.Done($"右键 {Cell(at)}：{team.Count} 台在采矿区旁（离最集中处 {F(Vector2.Distance(at, V(c, "hot")), "0")} 格）");
            }
            return RightClickGround(c, at);
        }

        private static WormRecord OnlyWorm => WormEventService.Worms(St).FirstOrDefault();

        private static StepOutcome TickWormCrack(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            EventDirectorInstanceRecord r = Inst(WormEventService.EventId);
            WormRecord w = OnlyWorm;
            if (r == null || w == null)
            {
                return c.StepElapsed < 110 ? StepOutcome.Wait
                    : StepOutcome.Fail($"80 警告后没有蠕虫（震动 {F(HomeVib.Value, "0.0")} 等级 {HomeVib.Level}；导演推迟原因 {EventDirectorService.Schedule(St, WormEventService.EventId)?.BlockedKey}）");
            }
            if (r.State == EventDirectorService.StateActive)
            {
                return StepOutcome.Fail("没观察到 30 秒破土预警就已经破土");
            }
            string crack = EventTexts().Skip(c.GetInt("vib.texts0")).FirstOrDefault(t => t.Contains("地面开裂") && t.Contains("采矿区")) ?? string.Empty;
            long warnSpan = r.StartTick - r.WarnTick;
            string msg = $"破土预警：{F(warnSpan / (double)GameClock.StepHz, "0")} 秒（应 30）；路线 {w.RouteX.Length} 个顶点；通知“{Short(crack)}”；家园还没有蠕虫 {Home.WormCount == 0}";
            return crack.Length > 0 && warnSpan == GameClock.TicksFor(30) && Home.WormCount == 0 && w.RouteX.Length >= 2 ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        private static StepOutcome TickWormUp(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            WormRecord w = OnlyWorm;
            if (w == null || w.State != WormEventService.StateActive || !Home.TryGetWorm(w.Chain, out CombatWorm cw))
            {
                return c.StepElapsed < 80 ? StepOutcome.Wait : StepOutcome.Fail("破土预警 80 秒后蠕虫还没钻出");
            }
            c.SetInt("w.segs", cw.SegCount);
            c.SetInt("w.chain", w.Chain);
            c.SetInt("w.ground0", WormLoot().Count);
            // 夹具收尾：破土后关停震动夹具的 10 座提取钻（通知里写的正式应对之一；建筑开关由 FGJ-M4 覆盖）——否则它们一直出矿、溢到地上被搬进仓库，把仓库塞满，后面坠落残骸的材料运不回来。
            foreach (BuildingRecord d in Drills(c))
            {
                d.ConstructionState = BuildingConstructionState.Disabled;
            }
            string msg = $"蠕虫破土：{cw.SegCount} 节（8～12）；家园震动值 {F(HomeVib.Value, "0.0")}（归零）";
            return cw.SegCount >= 8 && cw.SegCount <= 12 && HomeVib.Value < 0.1 ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        private static List<GroundItemRecord> WormLoot() =>
            (St.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g?.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith(FieldLootService.SalvagePrefix + LootCatalog.SourceWorm + ":", StringComparison.Ordinal)).ToList();

        private static StepOutcome TickWormFight(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CombatSite site = Home;
            EventDirectorHistoryRecord h = LastHist(WormEventService.EventId);
            if (h != null && OnlyWorm == null)
            {
                return h.Outcome == EventDirectorService.OutcomeSuccess
                    ? StepOutcome.Done($"右键攻击 {c.GetInt("w.rc")} 次：分节击毁 {VibrationService.StateOf(St).SegmentsKilled} 节，蠕虫倒下；{TeamHp(c)}")
                    : StepOutcome.Fail($"蠕虫结束但不是被击杀：{h.Outcome}（{EventDirectorService.OutcomeText(h.Outcome)}）；{TeamHp(c)}");
            }
            if (AliveTeam(c).Count == 0)
            {
                return StepOutcome.Fail("作战队全灭");
            }
            var segs = new List<int>();
            site.CopyWormSegments(c.GetInt("w.chain"), segs);
            int target = 0;
            float best = float.MaxValue;
            Vector2 center = AliveTeam(c).Aggregate(Vector2.zero, (a, id) => a + PosOf(id)) / Math.Max(1, AliveTeam(c).Count);
            Vector2 tp = default;
            foreach (int u in segs)
            {
                if (site.Kernel.TryGetUnit(u, out CombatUnitView v) && (v.Flags & CombatUnitFlags.Targetable) != 0 && v.Health > 0f)
                {
                    var p = new Vector2((float)v.Position.x, (float)v.Position.y);
                    float d = Vector2.Distance(p, center);
                    if (d < best)
                    {
                        best = d;
                        target = u;
                        tp = p;
                    }
                }
            }
            if ((int)(c.StepElapsed / 10) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 10));
                c.Log($"[{F(c.StepElapsed, "0")}s] 蠕虫 {segs.Count} 节、地上可打 {(target > 0 ? "有" : "无")}；已击毁 {VibrationService.StateOf(St).SegmentsKilled} 节；{TeamHp(c)}；右键 {c.GetInt("w.rc")} 次");
            }
            if (target > 0 && !JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) > 3000 && FgjM3Common.TryClick(tp, 1))
            {
                c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                c.SetInt("w.rc", c.GetInt("w.rc") + 1);
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickWormDead(JourneyContext c)
        {
            EventDirectorHistoryRecord h = LastHist(WormEventService.EventId);
            int ground = WormLoot().Count;
            VibrationState vs = VibrationService.StateOf(St);
            string msg = $"事件历史 {h?.Outcome}、数据包 {h?.PacketId}；击杀 {vs.Kills} 次、击毁 {vs.SegmentsKilled} / {c.GetInt("w.segs")} 节；地上蠕虫掉落 {ground} 堆（破土时 {c.GetInt("w.ground0")}）；蠕虫记录已移除 {OnlyWorm == null}";
            return h != null && h.Outcome == EventDirectorService.OutcomeSuccess && !string.IsNullOrEmpty(h.PacketId) && vs.Kills >= 1 && ground > c.GetInt("w.ground0") && OnlyWorm == null && Home.WormCount == 0
                ? StepOutcome.Done(msg) : c.StepElapsed < 20 ? StepOutcome.Wait : StepOutcome.Fail(msg);
        }

        // ── 3. 轨道坠落 ────────────────────────────────────────────────────────────

        private static void ApplyOrbitPlan(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt("o.planned") == 1)
            {
                return;
            }
            c.SetInt("o.planned", 1);
            int serial = EventDirectorService.StateOf(s).NextSerial;
            c.SetInt("o.serial", serial);
            if (!OrbitalDropService.PickLanding(s, serial, out Vector2 at))
            {
                c.Set("o.err", $"序号 {serial} 选不出落点");
                return;
            }
            SetV(c, "o.at", at);
            GridCell? pole = null;
            for (int r = 0; r <= 1 && !pole.HasValue; r++)
            {
                for (int dy = -r; dy <= r && !pole.HasValue; dy++)
                {
                    for (int dx = -r; dx <= r && !pole.HasValue; dx++)
                    {
                        var g = new GridCell(Mathf.FloorToInt(at.x) + dx, Mathf.FloorToInt(at.y) + dy);
                        if (HomeGridService.ValidatePlacement(s, PoleType, g, 0, asPlayerPlacement: false, checkCost: false).Ok)
                        {
                            pole = g;
                        }
                    }
                }
            }
            if (!pole.HasValue)
            {
                c.Set("o.err", $"落点 {Cell(at)} 旁 1 格内放不下电线杆");
                return;
            }
            BuildingRecord b = FgProductionSelfCheck.Built(s, PoleType, "m12pole", pole.Value);
            b.Health = BuildingOps.MaxDurability(PoleType);
            c.Set("o.pole", b.BuildingId);
            HomeGridService.RevealArea(s, at, 12f);
        }

        private static StepOutcome TickOrbitPlan(JourneyContext c)
        {
            if (c.Get("o.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail(c.Get("o.err"));
            }
            Vector2 at = V(c, "o.at");
            BuildingRecord pole = HomeGridService.FindBuilding(St, c.Get("o.pole"));
            return pole != null && Vector2.Distance(pole.Position, at) < OrbitalDropService.Radius - 0.5f
                ? StepOutcome.Done($"预计落点 {Cell(at)}（序号 {c.GetInt("o.serial")}，离核心 {F(Vector2.Distance(at, CorePos), "0.0")} 格）；电线杆 {Cell(pole.Position)} 在圈里")
                : StepOutcome.Fail($"电线杆不在落点圈里（{pole?.Position}）");
        }

        private static StepOutcome TickOrbitIn(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            Vector2 at = V(c, "o.at");
            List<int> team = AliveTeam(c);
            int inside = team.Count(id => Vector2.Distance(PosOf(id), at) < OrbitalDropService.Radius);
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 1 && inside >= 2 && c.StepElapsed > 8 && team.All(id => Vector2.Distance(PosOf(id), at) < 8f))
            {
                return StepOutcome.Done($"右键落点 {Cell(at)}：{inside} / {team.Count} 台在落点圈（半径 {F(OrbitalDropService.Radius)} 格）里");
            }
            return RightClickGround(c, at);
        }

        private static void ApplyOrbitRequest(JourneyContext c)
        {
            if (c.GetInt("o.req") == 1)
            {
                return;
            }
            c.SetInt("o.req", 1);
            SkipMajorGap(c, "坠落请求");
            c.SetInt("o.impacts0", OrbitalDropService.Impacts);
            c.SetInt("o.ok", EventDirectorService.Request(St, OrbitalDropService.EventId) ? 1 : 0);
        }

        private static bool MapShows(EventDirectorInstanceRecord r, Vector2 center, double half, float canvas)
        {
            var m = new WorldMapModel();
            m.Collect(St, new WorldMapView { CenterX = center.x, CenterY = center.y, HalfWidth = half, CanvasWidth = canvas, CanvasHeight = canvas }, 48);
            return m.Circles.Any(x => x.Impact && Math.Abs(x.X - r.X) < 1e-3 && Math.Abs(x.Y - r.Y) < 1e-3 && Mathf.Approximately(x.Radius, OrbitalDropService.Radius))
                   && m.Items.Any(i => i.Kind == WorldMapItemKind.RaidWarning && i.Id == "orbital:" + r.Serial);
        }

        private static StepOutcome TickOrbitWarn(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.GetInt("o.ok") == 0)
            {
                return StepOutcome.Fail("EventDirectorService.Request(orbital_drop) 被拒");
            }
            EventDirectorInstanceRecord r = Inst(OrbitalDropService.EventId);
            if (r == null || r.State != EventDirectorService.StateWarned)
            {
                return c.StepElapsed < 40 ? StepOutcome.Wait
                    : StepOutcome.Fail($"请求后 40 秒没进入预警（推迟原因 {EventDirectorService.Schedule(St, OrbitalDropService.EventId)?.BlockedKey}；实例 {r?.State}）");
            }
            var at = new Vector2(r.X, r.Y);
            float core = Vector2.Distance(at, CorePos);
            bool same = Vector2.Distance(at, V(c, "o.at")) < 0.01f;
            bool strategic = MapShows(r, at, 120, 900f);
            bool minimap = MapShows(r, at + new Vector2(10f, -6f), Math.Max(16.0, GridContent.Tuning("minimap.radius_cells")), 172f);
            long span = r.StartTick - r.WarnTick;
            c.SetInt("o.instSerial", r.Serial);
            foreach (int id in AliveTeam(c))
            {
                c.Set("o.hp." + id, F(Hp(id), "0.###"));
            }
            BuildingRecord pole = HomeGridService.FindBuilding(St, c.Get("o.pole"));
            c.Set("o.poleHp0", F(pole?.Health ?? -1f, "0.###"));
            string msg = $"预警 {F(span / (double)GameClock.StepHz, "0")} 秒（应 60）；落点 {Cell(at)} 与预计一致 {same}、离核心 {F(core, "0.0")} 格（≥ {F(OrbitalDropService.MinCoreDistance)}）；战略地图 {strategic} / 小地图 {minimap} 有落点圈与告警图标";
            return same && span == GameClock.TicksFor(60) && core >= OrbitalDropService.MinCoreDistance && strategic && minimap ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        private static StepOutcome TickOrbitOut(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            Vector2 at = V(c, "o.at");
            Vector2 dir = (CorePos - at).normalized;
            GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(at.x + dir.x * (OrbitalDropService.Radius + 7f)), Mathf.RoundToInt(at.y + dir.y * (OrbitalDropService.Radius + 7f))), 4);
            var to = new Vector2(g.X, g.Y);
            List<int> team = AliveTeam(c);
            if (Inst(OrbitalDropService.EventId) == null)
            {
                return StepOutcome.Fail($"作战队还没撤出就撞击了：{string.Join("、", team.Select(id => Label(id) + " 离落点 " + F(Vector2.Distance(PosOf(id), at), "0.0")))}");
            }
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 1 && team.All(id => Vector2.Distance(PosOf(id), at) > OrbitalDropService.Radius + 1.5f))
            {
                EventDirectorInstanceRecord r = Inst(OrbitalDropService.EventId);
                return StepOutcome.Done($"右键 {Cell(to)}：{team.Count} 台都撤出落点圈（最近 {F(team.Min(id => Vector2.Distance(PosOf(id), at)), "0.0")} 格），撞击前还剩 {F(OrbitalDropService.SecondsLeft(r), "0")} 秒");
            }
            return RightClickGround(c, to);
        }

        private static List<GroundItemRecord> OrbitLoot() =>
            (St.GroundItems ?? Array.Empty<GroundItemRecord>()).Where(g => g?.SalvageInstanceId != null && g.SalvageInstanceId.StartsWith(FieldLootService.SalvagePrefix + LootCatalog.SourceOrbital + ":", StringComparison.Ordinal)).ToList();

        private static StepOutcome TickOrbitHit(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (OrbitalDropService.Impacts <= c.GetInt("o.impacts0"))
            {
                return c.StepElapsed < 50 ? StepOutcome.Wait : StepOutcome.Fail("预警结束后没有撞击");
            }
            if (c.GetLong(FgjM3Common.SK(c, "t")) == 0)
            {
                c.SetLong(FgjM3Common.SK(c, "t"), NowMs());
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "t")) < 1500)
            {
                return StepOutcome.Wait; // 外部血量机器的伤害请求下一步才落到记录
            }
            Vector2 at = V(c, "o.at");
            BuildingRecord pole = HomeGridService.FindBuilding(St, c.Get("o.pole"));
            float max = BuildingOps.MaxDurability(PoleType);
            float frac = GridContent.TryGetTuning("event.orbital_drop.building_damage_frac", out float bf) ? bf : 0.8f;
            float loss = float.Parse(c.Get("o.poleHp0"), CultureInfo.InvariantCulture) - (pole?.Health ?? 0f);
            bool poleHit = pole == null || Math.Abs(loss - frac * max) < 1f;
            List<int> team = AliveTeam(c);
            bool unhurt = team.All(id => Hp(id) >= float.Parse(c.Get("o.hp." + id, "0"), CultureInfo.InvariantCulture) - 0.01f);
            bool wreck = Home.Kernel.PickWreck(new double2(at.x, at.y), 0.2f) >= 0 && OrbitalDropService.LastWreckId != 0;
            List<GroundItemRecord> loot = OrbitLoot();
            bool lootAt = loot.Count >= 1 && loot.All(g => Vector2.Distance(g.Position, at) < 5f);
            EventDirectorHistoryRecord h = LastHist(OrbitalDropService.EventId);
            bool hist = h != null && h.Serial == c.GetInt("o.instSerial") && h.Outcome == EventDirectorService.OutcomeSuccess;
            c.SetInt("o.loot0", loot.Count);
            string msg = $"撞击：电线杆 −{F(loss, "0")}（最大耐久 {F(max, "0")} × {frac}，{(pole == null ? "已毁" : "还剩 " + F(pole.Health, "0"))}）；作战队未受伤 {unhurt}（{TeamHp(c)}）；坠落残骸在落点 {wreck}；" +
                         $"残骸处 {loot.Count} 堆材料（{string.Join("、", loot.Select(g => g.ResourceType).Distinct())}）；事件历史 {(h != null ? EventDirectorService.OutcomeText(h.Outcome) : "无")}";
            return poleHit && unhurt && wreck && lootAt && hist ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        private static StepOutcome TickOrbitLoot(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            int left = OrbitLoot().Count;
            int haul = (St.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Count(o => o != null && o.Kind == WorkOrderKind.Haul);
            if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                var ids = new HashSet<string>(OrbitLoot().Select(g => g.SalvageInstanceId));
                string orders = string.Join("；", (St.WorkOrders ?? Array.Empty<WorkOrderRecord>()).Where(o => o != null && o.Kind == WorkOrderKind.Haul && o.TargetId != null && (o.TargetId.Contains("orbital") || o.State == WorkOrderState.Waiting)).Take(10)
                    .Select(o => $"{o.TargetId}/{o.SourceId}→{o.DestinationId} {o.State} 机{o.AssignedMachineLogicId} {o.FailureReason}"));
                string workers = string.Join("、", MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId && !Team(c).Contains(m.LogicId))
                    .Select(m => $"{Label(m.LogicId)}@{Cell(PosOf(m.LogicId))}"));
                string space = string.Join("、", OrbitLoot().Select(g => g.ResourceType).Concat(WormLoot().Select(g => g.ResourceType)).Distinct()
                    .Select(t => $"{t} 空位 {(ItemCatalog.Find(t) != null ? HomeInventory.Space(St, ItemCatalog.Find(t)) : -1)}"));
                c.Log($"[{F(c.StepElapsed, "0")}s] 仓库空位：{space}");
                c.Log($"[{F(c.StepElapsed, "0")}s] 坠落掉落剩 {left} / {c.GetInt("o.loot0")} 堆（{string.Join(",", ids)}）；搬运单 {haul} 张：{orders}；工作机 {workers}");
            }
            // 以“坠落掉落的搬运单完工”为准（掉落堆会就近合并，堆数变少不等于运走）。
            List<WorkOrderRecord> done = (St.WorkOrders ?? Array.Empty<WorkOrderRecord>())
                .Where(o => o != null && o.Kind == WorkOrderKind.Haul && o.State == WorkOrderState.Completed && (o.SourceId ?? string.Empty).Contains(FieldLootService.SalvagePrefix + LootCatalog.SourceOrbital + ":")).ToList();
            return done.Count > 0
                ? StepOutcome.Done($"回收：坠落残骸处的材料 {done.Count} 单已由 {string.Join("、", done.Select(o => Label(o.AssignedMachineLogicId)).Distinct())} 运回仓库（地上还剩 {left} 堆）；搬运单 {haul} 张")
                : StepOutcome.Wait;
        }

        // ── 4. 诱饵终端 ────────────────────────────────────────────────────────────

        private static WorldFeature Decoy(JourneyContext c)
        {
            WorldGenContext ctx = WorldGenService.ContextFor(St);
            return ctx != null ? WorldFeatures.FindById(ctx, c.Get("d.id", string.Empty)) : null;
        }

        private static void ApplyDecoyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("d.id", string.Empty).Length > 0)
            {
                return;
            }
            WorldGenContext ctx = WorldGenService.ContextFor(s);
            Vector2 core = CorePos;
            var feats = new List<WorldFeature>();
            WorldFeature best = null;
            foreach (PlannedTerritory t in ctx.Plan.Territories.Where(t => t.IsFaction))
            {
                feats.Clear();
                WorldFeatures.Query(ctx, t.CenterX - t.Radius, t.CenterY - t.Radius, t.CenterX + t.Radius, t.CenterY + t.Radius, feats);
                foreach (WorldFeature f in feats.Where(f => f.Kind == WorldFeatureKind.Terminal && DecoyTerminalService.IsDecoy(s, f) && !HomeExplorationService.IsRecovered(s, f.Id)))
                {
                    if (best == null || Vector2.Distance(new Vector2(f.X, f.Y), core) < Vector2.Distance(new Vector2(best.X, best.Y), core))
                    {
                        best = f;
                    }
                }
            }
            if (best == null)
            {
                c.Set("d.err", "这个种子的阵营领地里没有诱饵终端");
                return;
            }
            c.Set("d.id", best.Id);
            var tp = new Vector2(best.X, best.Y);
            var relays = new List<string>();
            Vector2 last = FgjM10Journey.RelayChain(c, core, SignalCoverageService.CoreRadius, tp, "m12_relay", relays);
            c.Set("d.relays", string.Join(" ", relays));
            Vector2 u = (core - tp).normalized;
            GridCell park = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(tp.x + u.x * 25f), Mathf.RoundToInt(tp.y + u.y * 25f)), 10);
            HomeGridService.RevealArea(s, new Vector2(park.X, park.Y), 30f);
            HomeGridService.RevealArea(s, tp, 8f);
            string bp = SaveBlueprint(c, "bp.mark", "标记器作战", BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, ComponentCatalog.FuncMarkerId, null, Array.Empty<string>()));
            int id = bp.Length > 0 ? SpawnMachine(s, bp, new Vector2(park.X, park.Y), SquadHp) : 0;
            c.SetInt("d.marker", id);
            SetV(c, "d.park", new Vector2(park.X, park.Y));
            c.SetInt("d.sprung0", DecoyTerminalService.Sprung);
            c.SetInt("d.ident0", DecoyTerminalService.Identified);
            c.SetInt("d.texts0", EventTexts().Count);
            if (id == 0)
            {
                c.Set("d.err", "带标记器的机器没造出来：" + c.Get("bp.mark.note"));
            }
        }

        private static Vector2 DecoyGoal(JourneyContext c)
        {
            WorldFeature f = Decoy(c);
            var tp = new Vector2(f.X, f.Y);
            Vector2 u = (V(c, "d.park") - tp).normalized;
            // 终端外 3.5～5 格、朝停车处一侧的可走格（不点终端本身：右键终端是“读取”交互，不是移动）。
            for (int k = 0; k < 24; k++)
            {
                float a = (k % 2 == 0 ? 1 : -1) * (k + 1) / 2 * 15f * Mathf.Deg2Rad;
                Vector2 d = new Vector2(u.x * Mathf.Cos(a) - u.y * Mathf.Sin(a), u.x * Mathf.Sin(a) + u.y * Mathf.Cos(a));
                GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(tp.x + d.x * 4f), Mathf.RoundToInt(tp.y + d.y * 4f)), 1);
                float dist = Vector2.Distance(new Vector2(g.X, g.Y), tp);
                if (g.X != int.MinValue && dist >= 3.5f && dist <= 5.5f)
                {
                    return new Vector2(g.X, g.Y);
                }
            }
            return tp + u * 4f;
        }

        private static StepOutcome TickDecoyFixture(JourneyContext c)
        {
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait;
            }
            if (c.Get("d.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail(c.Get("d.err"));
            }
            CampaignState s = St;
            WorldFeature f = Decoy(c);
            int m = c.GetInt("d.marker");
            bool found = (HomeExplorationService.StateOf(s)?.PoisFound ?? Array.Empty<string>()).Contains(f.Id);
            bool scan = DecoyTerminalService.CanScan(s, m);
            bool inKernel = Home != null && Home.TryGetMachineUnit(m, out _);
            Vector2 goal = DecoyGoal(c);
            bool cov = SignalCoverageService.Sample(HomeValleyLayout.RegionId, goal).Covered;
            string hover = DecoyTerminalService.HoverLine(s, f.Id, string.Empty) ?? string.Empty;
            string geo = $"诱饵终端 {f.Id} {Cell(new Vector2(f.X, f.Y))}（{f.TerritoryId}，破绽 {(DecoyTerminalService.VariantOf(s, f) == DecoyTerminalService.VariantMines ? "半埋地雷" : "焦痕")}，离核心 {F(Vector2.Distance(new Vector2(f.X, f.Y), CorePos), "0")} 格）；" +
                         $"中继 {c.Get("d.relays")}；标记器机器 {Label(m)}@{Cell(PosOf(m))}；目标点 {Cell(goal)} 覆盖 {cov}";
            if (!found || !scan || !inKernel || !cov)
            {
                return c.StepElapsed < 12 ? StepOutcome.Wait : StepOutcome.Fail($"夹具后：终端已发现 {found}、能扫描（端口 evt.marked）{scan}、机器进内核 {inKernel}、目标点在覆盖里 {cov}；{geo}");
            }
            return StepOutcome.Done($"{geo}；悬停“{Short(hover)}”（未识别前与普通终端同名，多一行细看破绽）");
        }

        private static StepOutcome TickSelectMarker(JourneyContext c) => SelectMachines(c, new List<int> { c.GetInt("d.marker") }, "带标记器的机器");

        private static StepOutcome TickDecoyGo(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            WorldFeature f = Decoy(c);
            int m = c.GetInt("d.marker");
            if (!Alive(m))
            {
                return StepOutcome.Fail($"带标记器的机器阵亡（{Cell(PosOf(m))}）");
            }
            if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                c.Log($"[{F(c.StepElapsed, "0")}s] {Label(m)}@{Cell(PosOf(m))} 离终端 {F(Vector2.Distance(PosOf(m), new Vector2(f.X, f.Y)), "0.0")} 格；右键 {c.GetInt(FgjM3Common.SK(c, "clicked"))}（补点 {c.GetInt("reclicks")}）；" +
                      $"选择集 [{string.Join("、", (Squad?.Selection ?? Array.Empty<int>()).Select(Label))}]；最近一次点击 {JourneyInput.LastWorldClickTrace}；命令反馈 {SignalUplinkService.LastFeedbackText}");
            }
            if (DecoyTerminalService.IsIdentified(s, f.Id))
            {
                return StepOutcome.Done($"右键 {Cell(DecoyGoal(c))}：{Label(m)} 走到离终端 {F(Vector2.Distance(PosOf(m), new Vector2(f.X, f.Y)), "0.0")} 格（扫描半径 {F(DecoyTerminalService.ScanRadius)}）");
            }
            return RightClickGround(c, DecoyGoal(c));
        }

        private static StepOutcome TickDecoySeen(JourneyContext c)
        {
            CampaignState s = St;
            WorldFeature f = Decoy(c);
            string note = EventTexts().Skip(c.GetInt("d.texts0")).FirstOrDefault(t => t.Contains("诱饵终端")) ?? string.Empty;
            string label = DecoyTerminalService.MapLabel(s, f.Id) ?? string.Empty;
            string hover = DecoyTerminalService.HoverLine(s, f.Id, string.Empty) ?? string.Empty;
            bool calm = DecoyTerminalService.Sprung == c.GetInt("d.sprung0") && Inst(DecoyTerminalService.EventId) == null && !DecoyTerminalService.IsSprung(s, f.Id);
            string msg = $"识别：通知“{Short(note)}”；地图“{label}”；悬停“{Short(hover)}”；识别计数 {c.GetInt("d.ident0")} → {DecoyTerminalService.Identified}；没触发伏击 {calm}";
            return note.Length > 0 && label.Contains("已识别") && calm && DecoyTerminalService.Identified == c.GetInt("d.ident0") + 1
                ? StepOutcome.Done(msg) : c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Fail(msg);
        }

        // ── 5. 流浪商队 ────────────────────────────────────────────────────────────

        private static void ApplyCaravanRequest(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt("cv.req") == 1)
            {
                return;
            }
            c.SetInt("cv.req", 1);
            foreach (string item in new[] { CaravanService.AlloyId, CaravanService.PrecisionId, ItemCatalog.TechDataId })
            {
                HomeInventory.Add(s, item, 120, clampToSpace: false);
            }
            c.SetInt("cv.buy0", CaravanService.Purchases);
            c.SetInt("cv.stock0", Stock());
            c.SetInt("cv.ok", EventDirectorService.Request(s, CaravanService.EventId) ? 1 : 0);
        }

        private static int Stock() =>
            new[] { CaravanService.AlloyId, CaravanService.PrecisionId, ItemCatalog.TechDataId }.Sum(id => HomeInventory.Stock(St, id));

        private static StepOutcome TickCaravanArrived(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.GetInt("cv.ok") == 0)
            {
                return StepOutcome.Fail("EventDirectorService.Request(caravan) 被拒");
            }
            EventDirectorInstanceRecord r = CaravanService.Present(St);
            if (r == null || !CaravanViews.Visible || CaravanService.Wares(St).Count == 0)
            {
                return c.StepElapsed < 110 ? StepOutcome.Wait
                    : StepOutcome.Fail($"请求后商队没到（实例 {Inst(CaravanService.EventId)?.State}；推迟原因 {EventDirectorService.Schedule(St, CaravanService.EventId)?.BlockedKey}；主视图可见 {CaravanViews.Visible}）");
            }
            Vector2 stop = new Vector2(r.X, r.Y);
            Vector3 view = CaravanViews.Position;
            if (Vector2.Distance(new Vector2(view.x, view.z), stop) > 1.5f)
            {
                return c.StepElapsed < 110 ? StepOutcome.Wait : StepOutcome.Fail($"商队主视图一直没走到停靠点 {Cell(stop)}（在 {view}）");
            }
            return StepOutcome.Done($"流浪商队停在 {Cell(stop)}（离核心 {F(Vector2.Distance(stop, CorePos), "0")} 格），带 {CaravanService.Wares(St).Count} 件商品：{string.Join("、", CaravanService.Wares(St).Select(w => CaravanService.WareName(CaravanService.Row(w.WareId))))}");
        }

        private static StepOutcome TickCaravanOpen(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (CaravanTradePanelUIToolkit.IsOpen)
            {
                return StepOutcome.Done($"左键点商队：交易面板打开（主视图点开 {CaravanViews.ClickOpens} 次）");
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 1500)
            {
                return StepOutcome.Wait;
            }
            Vector3 p = CaravanViews.Position;
            var g = new Vector2(p.x, p.z);
            if (!JourneyInput.OnScreen(g, 0.3f))
            {
                FgjM3Common.PanTo(g); // 商队放到画面中部再点（画面边缘可能压着命令栏 / 通知）
                return StepOutcome.Wait;
            }
            Vector3 screen = JourneyInput.ScreenOfWorld(p + Vector3.up);
            string cover = JourneyInput.UiCoverAt(screen);
            if (c.GetInt(FgjM3Common.SK(c, "n")) >= 4)
            {
                return StepOutcome.Retry($"点了 4 次商队没打开面板（主视图点开 {CaravanViews.ClickOpens} 次；商队 {p}、可见 {CaravanViews.Visible}；光标处界面“{cover}”；{JourneyInput.LastWorldClickTrace}）");
            }
            JourneyInput.ClickScreen(screen);
            c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
            c.SetInt(FgjM3Common.SK(c, "n"), c.GetInt(FgjM3Common.SK(c, "n")) + 1);
            return StepOutcome.Wait;
        }

        private static StepOutcome TickCaravanBuy(JourneyContext c)
        {
            if (CaravanService.Purchases > c.GetInt("cv.buy0"))
            {
                Label footer = JourneyInput.FindUitk<Label>("[CaravanTradeHost]", "CaravanFooter");
                string text = footer?.text ?? string.Empty;
                int stock = Stock();
                return text.Length > 0 && CaravanService.LastMessage.Length > 0 && text.StartsWith(CaravanService.LastMessage, StringComparison.Ordinal) && stock < c.GetInt("cv.stock0")
                    ? StepOutcome.Done($"买入 1 件：页脚“{text.Replace('\n', '；')}”；合金 + 精密零件 + 技术数据 {c.GetInt("cv.stock0")} → {stock}（扣了价格）；本局买入 {EventDirectorService.StateOf(St).CaravanBought} 件")
                    : c.StepElapsed < 5 ? StepOutcome.Wait : StepOutcome.Fail($"买入后页脚没写结果（“{text}” vs “{CaravanService.LastMessage}”）");
            }
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 1)
            {
                return c.StepElapsed < 6 ? StepOutcome.Wait : StepOutcome.Retry($"点了买入但没成交：“{CaravanService.LastMessage}”");
            }
            VisualElement root = JourneyInput.FindUitk<VisualElement>("[CaravanTradeHost]", "CaravanList");
            Button buy = root?.Query<Button>(className: "cv-buy").ToList().FirstOrDefault(b => b.enabledSelf && b.enabledInHierarchy);
            if (buy == null)
            {
                return c.StepElapsed < 5 ? StepOutcome.Wait : StepOutcome.Fail("交易面板里没有能点的“买入”按钮");
            }
            if (root is ScrollView sv && !JourneyInput.ScrollIntoView(sv, buy))
            {
                return StepOutcome.Wait;
            }
            if (JourneyInput.ClickElement(buy))
            {
                c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickCaravanClosed(JourneyContext c) =>
            !CaravanTradePanelUIToolkit.IsOpen ? StepOutcome.Done("交易面板已关闭") : c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Retry("点关闭后面板还开着");
    }
}
