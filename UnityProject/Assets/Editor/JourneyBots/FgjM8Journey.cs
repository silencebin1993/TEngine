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
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using UnityEngine;
using P = GameLogic.EditorTools.JourneyBots.FgjM4Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG8-E2E-01：M8 出口旅程 FGJ-M8（里程碑文档 FG-M8；00 契约 IC-REQ-021）。从主菜单“新建”出发（固定测试种子 42、标准难度）、全程正式输入（ADR-QA-017 三条通道），
    /// RTS 口径：左键选择、右键地面 = 移动 / 右键敌人 = 攻击，按钮只切状态 / 发起动作；等待段用正式 3 倍速。
    ///
    /// 里程碑原文：在派遣检查单上看到“路线有一段走出信号覆盖”，沿途建一座信号中继塔 → 远征队沿地形行进到破碎都市 → 击杀两个精英，攒下两个数据包 →
    /// 一台机器阵亡，货物掉落，由另一台捡回 → 在领地边缘建一座前哨站（中继、维修、炮塔）→ 用投送舱把满载的机器和伤员回收到家园 →
    /// 回家处理数据包，货物按规则卸货 → 完成一个支线任务。
    ///
    /// 轻量口径（用户 2026-10-12）：一条正向旅程走通核心循环，每个 M8 新系统至少被真实输入触发一次并断言结果；不为凑剧情改数值。
    /// 进度夹具只有一处开局夹具（<see cref="ApplyFixture"/>：投送站研究、废料、远征队机器、破碎都市开放）与三处“事件夹具”（精英残部刷新、机器阵亡、投送站燃油 / 舱），
    /// 每处在步骤标题与报告里写明“夹具”，被测行为（交战、掉落、拾取、回收、卸货、分拣、任务判定）全走正式系统。
    /// </summary>
    public static partial class FgjM8Journey
    {
        public const string Id = "FGJ-M8";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private const string PrepHost = "[HomeValleyExpeditionPrepHost]";
        private const string WorldBarHost = "[WorldBarHost]";
        private const string RelayType = HomeValleyLayout.BuildingTypeSignalRelay;
        private const string RepairType = "repair_drone_station"; // 维修台（repair_bay）只能原地重建、不能新建；前哨站的“维修”是维修无人机站
        private const string TurretType = "turret_light";

        private static CampaignState St => CampaignSession.Current;

        private static CombatSite Home => GameRoot.HomeValley?.Combat;

        public static JourneyDef Build()
        {
            var steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, TickPlay),
                S("menu_new", "主菜单点“新建”", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "存档槽 → 新游戏设置（种子 = 固定测试种子 42，难度标准）→ 点“开始” → 进入归还谷地", 150, null, JourneyCommon.TickNewGame),
                S("seed", "种子进了存档；生成结果与该种子的基准一致；起始区四级保证满足", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("workers", "记下开局两台机器", 10, null, FgjM3Common.TickWorkers),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),

                // ── 家园前置：修仓库、信号塔、发电机（与 FGJ-M7 同一套正式输入：左键点机器、右键受损建筑 = 修复）──
                S("sel_a", "左键点一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_wh", "右键点受损的仓库（情境命令：修复）", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeWarehouse),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeWarehouse), retries: 2),
                S("sel_b", "左键点另一台机器", 20, c => FgjM1Journey.ClickMachine(c, "workerB"), c => FgjM1Journey.TickSelected(c, "workerB"), retries: 4),
                S("repair_tw", "右键点受损的信号塔（覆盖要靠它）", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeSignalTower),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeSignalTower), retries: 2),
                S("wait_rep1", "等仓库与信号塔修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeWarehouse, HomeValleyLayout.BuildingTypeSignalTower)),
                S("sel_a2", "左键点机器", 20, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 4),
                S("repair_gen", "右键点受损的发电机（投送站要电）", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("wait_rep2", "等发电机修好", 300, null, c => FgjM3Common.TickRepaired(c, HomeValleyLayout.BuildingTypeGenerator)),
                S("m8_fix", "开局夹具（DEBT-FG8E2E01-03）：研发“远征 · 投送站”“防御 · 维修无人机站”记为已研究、废料 +600、远征队四台（ERC-001 ×1 + ERC-003 ×3）出厂在核心旁、破碎都市开放", 20, ApplyFixture, TickFixture),
            };
            steps.AddRange(ExpeditionSteps());
            steps.AddRange(FieldSteps());
            steps.AddRange(HomeSteps());
            return new JourneyDef
            {
                Id = Id,
                Title = "M8 出口：派遣检查单看到路线走出覆盖 → 沿途建信号中继塔 → 远征队沿地形行进到破碎都市 → 击杀两个精英攒下两个数据包 → 一台阵亡、货物掉落由另一台捡回 → 领地边缘建前哨站 → 投送舱回收满载机器与伤员 → 回家分拣数据包、货物按规则卸货 → 完成一个支线任务",
                Seed = TestSeed,
                TotalTimeoutSeconds = 2400,
                OnFinish = Cleanup,
                Version = 1,
                CheckpointAfter = new[] { "m8_fix", "relay_built", "team_hold", "elite_killed", "outpost_built" },
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        internal static string F(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);

        internal static long NowMs() => FgjM2Common.NowMs();

        internal static string Label(int id) => FgjM1Journey.Label(id);

        internal static string Cell(GridCell g) => FgjM3Common.Cell(g);

        internal static List<int> Team(JourneyContext c) => FgjM1Journey.Roster(c);

        internal static Vector2 PosOf(int id) =>
            MachineRegistry.TryGetLivePosition(id, out Vector2 p) ? p : MachineRegistry.TryGetRecord(id, out MachineRecord m) && m != null ? m.WorldPosition : Vector2.zero;

        internal static bool Alive(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) && m != null && m.IsAlive;

        internal static Vector2 CorePos
        {
            get
            {
                GridCell core = HomeGridService.CorePivot(St);
                return new Vector2(core.X, core.Y);
            }
        }

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
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM3Common.FrameReport("全程"));
            JourneyCommon.Cleanup(c);
        }

        internal static float DistToRoute(IReadOnlyList<Vector2> route, Vector2 p)
        {
            float best = float.MaxValue;
            for (int i = 1; route != null && i < route.Count; i++)
            {
                Vector2 a = route[i - 1];
                Vector2 b = route[i];
                Vector2 ab = b - a;
                float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }

        /// <summary>路线按 <paramref name="step"/> 格等距采样（含起终点）。</summary>
        internal static List<Vector2> Densify(IReadOnlyList<Vector2> route, float step)
        {
            var o = new List<Vector2>();
            for (int i = 1; route != null && i < route.Count; i++)
            {
                Vector2 a = route[i - 1];
                Vector2 b = route[i];
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / step));
                for (int k = 0; k < n; k++)
                {
                    o.Add(Vector2.Lerp(a, b, k / (float)n));
                }
            }
            if (route != null && route.Count > 0)
            {
                o.Add(route[route.Count - 1]);
            }
            return o;
        }

        /// <summary>在 <paramref name="near"/> 附近（螺旋，最多 <paramref name="maxR"/> 格）找能放 <paramref name="type"/> 的格子（与 <paramref name="avoid"/> 不重叠）。</summary>
        internal static bool FindSpot(CampaignState s, string type, Vector2 near, int maxR, ISet<long> avoid, out GridCell pivot)
        {
            var c0 = new GridCell(Mathf.RoundToInt(near.x), Mathf.RoundToInt(near.y));
            for (int r = 0; r <= maxR; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var g = new GridCell(c0.X + dx, c0.Y + dy);
                        if (P.Placeable(s, type, g, 0, avoid))
                        {
                            pivot = g;
                            return true;
                        }
                    }
                }
            }
            pivot = default;
            return false;
        }

        internal static void Reserve(ISet<long> avoid, string type, GridCell pivot, int margin = 1)
        {
            foreach (GridCell f in P.Footprint(type, pivot, 0))
            {
                for (int dx = -margin; dx <= margin; dx++)
                {
                    for (int dy = -margin; dy <= margin; dy++)
                    {
                        avoid.Add(P.Key(new GridCell(f.X + dx, f.Y + dy)));
                    }
                }
            }
        }

        // ── 开局夹具 ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 开局夹具（DEBT-FG8E2E01-03，轻量口径）：M8 之前的进度——研发“远征 · 投送站”“防御 · 维修无人机站”（研发流程 FGJ-M5 / FgResearchSelfCheck 覆盖）、
        /// 废料（拆残骸 / 回收站 FGJ-M2～M4 覆盖）、远征队机器（装配站生产 FGJ-M4 / M6 覆盖，这里按正式出厂入口 MachineRegistry.SpawnMachine 放在核心旁）、
        /// 破碎都市开放（Demo 解锁条件 FGJ-M1 覆盖）。之后的派遣、建造、行军、交战、回收全走正式入口。
        /// </summary>
        private static void ApplyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null)
            {
                return;
            }
            ResearchService.CompleteForTests(s, "expedition.drop_station", "defense.repair_drone");
            c.SetInt("scrapFix0", s.Scrap);
            s.Scrap += 600;
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            if (c.Get("roster", string.Empty).Length > 0)
            {
                return;
            }
            GridCell core = HomeGridService.CorePivot(s);
            var ids = new List<int>();
            (string chassis, string bp)[] team =
            {
                (HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id),
                (HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id),
                (HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id),
                (HomeValleyLayout.Erc003ChassisId, HomeValleyLayout.BlueprintErc003Id),
            };
            for (int i = 0; i < team.Length; i++)
            {
                GridCell at = SiegeService.NearestPassable(new GridCell(core.X - 6 + i * 3, core.Y - 9), 10);
                MachineOpResult r = MachineRegistry.SpawnMachine(team[i].chassis, team[i].bp, HomeValleyLayout.RegionId, new Vector2(at.X, at.Y), 100f, 100f);
                if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
                {
                    MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
                    ids.Add(r.LogicId);
                }
            }
            c.Set("roster", string.Join(",", ids.Select(x => x.ToString(CultureInfo.InvariantCulture))));
        }

        private static StepOutcome TickFixture(JourneyContext c)
        {
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            List<int> team = Team(c);
            bool inKernel = team.Count == 4 && team.All(id => Home != null && Home.TryGetMachineUnit(id, out _));
            if (!inKernel)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail($"夹具出厂的远征队机器没有进家园战斗内核（{team.Count} 台）");
            }
            ExpeditionDepartureService.RosterValidation v = ExpeditionDepartureService.ValidateRoster(s, team);
            bool researched = HomeGridService.IsUnlocked(s, GridContent.Building(DropPodService.StationTypeId));
            if (!v.Success || !v.HasWeapon || v.TotalCargoSlots <= 0 || !researched)
            {
                return StepOutcome.Fail($"夹具后名单不能出发 / 缺武器 / 缺货舱 / 投送站未解锁：{v.Success} {v.HasWeapon} {v.TotalCargoSlots} {researched}（{string.Join("；", v.BlockingReasons)}）");
            }
            return StepOutcome.Done($"夹具生效：废料 {c.GetInt("scrapFix0")} → {s.Scrap}；投送站已解锁；远征队 {string.Join("、", team.Select(id => $"{Label(id)}({(MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m.ChassisId : "?")})"))}，" +
                                    $"货位 {v.TotalCargoSlots}、有武器；破碎都市 {FracturedCityRegion.Find(s)?.State}");
        }
    }
}
