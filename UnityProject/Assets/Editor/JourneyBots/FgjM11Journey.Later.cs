using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameConfig.fg;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Campaign.Defense;
using GameConfig;
using Unity.Mathematics;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>FG11-E2E-01：FGJ-M11 第 3 段（余烬主机：数据中心入口 → 三阶段 → 槽位封锁后夺回 → 破译片段与超算残核）与第 4 段（家园：超控阵列升 T3、超频突袭）。</summary>
    public static partial class FgjM11Journey
    {
        private static readonly string[] Checkpoints = { "m11_fix", "back_park", "sc_close", "tower_exit", "boss_fix", "boss_won", "dc_out" };

        private const float BossFixtureHealth = 1500f;
        private const string Center = EmberHostBoss.SiteId;

        private static InteriorSiteController Dc => WorldSimulation.Interior(Center);

        private static PlannedAnchor Gate => InteriorSiteService.EntranceFor(St, Center);

        private static void AddBossSteps(List<JourneyStep> steps)
        {
            steps.AddRange(new[]
            {
                S("boss_fix", "首领夹具（DEBT-FG11E2E01-03）：中继链从链尾接着往数据中心入口推（每座过正式放置校验，推到 3 级污染的辐射带为止），链尾覆盖里建一座有电的 T2 信号塔覆盖到入口（FG-GAP-239）；远征队每台耐久 1500、回厂改造到第 2 版（只留重炮、不带燃迹——带燃迹持续开火会自燃锁火，FGJ-M10 同一正式打法）；" +
                              "信号核 2 号槽再装一枚过载（余烬主机封锁的是装着固件的槽）", 20, ApplyBossFixture, TickBossFixture),
                // FG15-PL-01 修复轮 4（旅程跟着系统改，M10-BASELINE 差异表）：FG15-UX-01 起选中机器后右键室内入口 = 走过去、到了就进（DEBT-FG8GEN02-10 RTS 右键情境命令）；
                // 旧打法“右键入口点（移动）→ 左键开面板 → 点进入”在第一步就被当成进入命令、远征队直接进了数据中心。改为先左键看入口面板（首领名与三阶段）、关掉，再选队右键入口。
                // 面板“进入”按钮由冒烟 PlaySmokeTest 覆盖（EntranceGo）。
                S("gate_open", "左键点数据中心入口：入口面板打开，写首领名与三阶段（远征队还在路上）", 20, null, TickGateOpen, retries: 2),
                S("gate_close", "点入口面板的“关闭”：面板收起", 10, c => JourneyInput.ClickUitk("[WorldPlacesHost]", "EntranceClose"),
                    c => !PanelOpen ? StepOutcome.Done("入口面板关闭") : c.StepElapsed < 2 ? StepOutcome.Wait : StepOutcome.Retry("点“关闭”后入口面板还开着"), retries: 1),
                S("sel_b", "左键点一台远征队、Shift 左键加选其余存活的", 60, null, FgjM10Journey.TickSelectTeam, retries: 2),
                S("gate_go", "右键数据中心入口（右键情境命令）：选中的远征队接到“走过去并进入”（字幕写明前往数据中心）", 30, null, TickGateGo),
                S("gate_enter", "远征队走到入口旁自动进入数据中心（室内地点），余烬主机进门即开战（阶段一·升频）", 240, null, TickGateEnter),
                S("sel_in", "数据中心里左键点一台、Shift 左键加选其余", 60, null, TickSelectInterior, retries: 2),
                S("boss_fight", "右键守卫 / 余烬主机（攻击）：阶段一升频 → 本体耐久到 60% 进阶段二·槽位封锁（共用计数 SlotBlockedCount +1、被封槽固件保留不生效）→ 右键数据尖刺逐根打掉 → 夺回（SlotRestoredCount +1）→ 阶段三熔毁倒计时 → 击毁", 900, null, TickBossFight),
                S("boss_won", "余烬主机击毁：奖励只发一次（核心固件“处决”、首领三选一数据包）、超频领地平定、剧情节点“破译”进资料库、关键材料“超算残核”进核心保管库；封锁全部解除", 30, null, TickBossWon),
                S("dc_exit_go", "右键数据中心出口旁的地面（移动）：远征队都走到出口旁（出口面板只带出口附近的机器）", 120, null, TickExitGo, retries: 1),
                S("dc_exit_open", "左键点数据中心出口：出口面板打开", 20, null, TickExitOpen, retries: 2),
                S("dc_out", "点出口面板的“离开”：远征队回到星球表面的入口旁", 60, null, TickExitDone, retries: 1),
            });
        }

        private static void AddHomeSteps(List<JourneyStep> steps)
        {
            steps.AddRange(new[]
            {
                S("home_fix", "家园夹具（DEBT-FG11E2E01-05）：超控阵列 T2 已建成供电、三级研究已完成（升 T3 只差超算残核与废料差额）；监听站建成供电；核心旁出厂 4 台重炮留守队；" +
                              "按真实暴露账本记一串超频固件的原始开火，暴露越过 30 → 突袭导演排出超频突袭", 30, ApplyHomeFixture, TickHomeFixture),
                S("home_focus", "按回到核心键（默认 Home）：镜头回家园", 10, c => JourneyInput.PressAction(GameActionId.FocusHomeCore), TickHomeFocused, retries: 1),
                S("arr_open", "按建造键进建造模式（手上不拿条目）、左键点超控阵列：它的通用面板打开（写超控阵列 T2）", 30, null, TickArrayPanel, retries: 2),
                S("arr_up", "点面板的“升级”：超算残核从核心保管库直接取料，机器送来废料差额、施工完成 → 超控阵列 T3、信号核 5 槽全部生效，面板写“最高级”", 240,
                    null, TickArrayT3, retries: 1),
                S("arr_close", "点面板关闭按钮", 10, c => FgjM1Journey.ClickUi(c, "[ProductionPanelHost]", "ProductionPanelClose"),
                    c => c.StepElapsed < 0.4 ? StepOutcome.Wait : !ProductionPanelUIToolkit.IsOpen ? StepOutcome.Done("生产面板关闭") : StepOutcome.Retry("面板没关"), retries: 2),
                S("build_exit", "再按建造键退出建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("home_sel", "左键点一台留守队、Shift 左键加选其余几台", 60, null, FgjM10Journey.TickSelectTeam, retries: 2),
                S("raid_warn", "超频突袭排定 → 出发预警（预警条写这一波；监听站情报到了时写“高速突击”特点）；夹具在突袭来路上放电塔 + 发电机 + 一段屏障（防御建造由 FGJ-M6 覆盖）", 600, null, TickOcRaidWarned),
                S("raid_hit", "超频突袭抵达：猎犬直扑发电设施——第一座掉耐久的建筑是发电类", 300, null, TickOcRaidHit),
                S("raid_fight", "右键离核心最近的敌人（攻击），打掉 / 跟丢再右键下一台：突袭结束，结算写明结果", 420, null, TickOcRaidCleared),
            });
        }

        // ── 4. 家园：超控阵列 T3 与超频突袭 ───────────────────────────────────────────

        private static Vector2 Core2 => FgSiegeSelfCheck.CoreCenter(St);

        private static GridCell? SpotNearCore(CampaignState s, string type, int r0)
        {
            GridCell core = HomeGridService.CorePivot(s);
            for (int r = r0; r < r0 + 40; r += 3)
            {
                for (int k = 0; k < 16; k++)
                {
                    float a = k * 22.5f * Mathf.Deg2Rad;
                    var g = new GridCell(core.X + Mathf.RoundToInt(Mathf.Cos(a) * r), core.Y + Mathf.RoundToInt(Mathf.Sin(a) * r));
                    if (HomeGridService.ValidatePlacement(s, type, g, 0, asPlayerPlacement: false, checkCost: false).Ok)
                    {
                        return g;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 家园夹具（DEBT-FG11E2E01-05，轻量口径）：超控阵列 T2 建成（建造 / 升 T2 由 FgOverrideArraySelfCheck 覆盖），三级研究直接记完成（研究由 FGJ-M5 覆盖）；
        /// 监听站建成；核心旁 4 台留守队；暴露按真实账本抬过 30（与 FgOverclockRaidSelfCheck B1 同一入口）。超算残核用第 3 段首领战真实发的那一件。
        /// </summary>
        private static void ApplyHomeFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("arr.id", string.Empty).Length > 0)
            {
                return;
            }
            FgProductionSelfCheck.PowerUp(s);
            s.Research.CompletedNodes = (s.Research.CompletedNodes ?? Array.Empty<string>()).Union(new[] { "signal.override_t1", "signal.override_t2", "signal.override_t3" }).ToArray();
            GridCell? at = SpotNearCore(s, OverrideArrayService.TypeId, 10);
            GridCell? intelAt = at.HasValue ? SpotNearCore(s, IntelService.TypeId, 18) : null;
            if (!at.HasValue || !intelAt.HasValue)
            {
                c.Set("home.err", $"核心附近放不下超控阵列（{at.HasValue}）/ 监听站（{intelAt.HasValue}）");
                return;
            }
            BuildingRecord arr = FgProductionSelfCheck.Built(s, OverrideArrayService.TypeId, "m11_arr", at.Value);
            arr.Tier = 2;
            arr.Health = BuildingOps.MaxDurability(OverrideArrayService.TypeId);
            for (int t = 1; t <= 2; t++)
            {
                foreach (BuildMaterialNeed n in BuildMaterials.For(OverrideArrayService.TypeId, t))
                {
                    BuildMaterials.AddInvested(arr, n.ResourceType, n.Amount);
                }
            }
            FgProductionSelfCheck.Built(s, IntelService.TypeId, "m11_intel", intelAt.Value);
            // 升 T3 耗电 +40（FgOverrideArraySelfCheck D4）：核心配电范围里再放两座固定发电机，留出余量（run20：没放时 T3 完工但缺电、只生效 2 槽）
            for (int g = 0; g < 2; g++)
            {
                GridCell? ga = SpotNearCore(s, HomeValleyLayout.BuildingTypeGenerator2, 14);
                if (ga.HasValue)
                {
                    FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeGenerator2, "m11_gen" + g, ga.Value);
                }
            }
            s.Scrap = Math.Max(s.Scrap, 600);
            HomeValleyPowerGrid.Recompute(s);
            c.Set("arr.id", arr.BuildingId);
            c.Set("roster.dc", c.Get("roster"));
            GridCell core = HomeGridService.CorePivot(s);
            BlueprintRecord bp = s.BlueprintRecords.FirstOrDefault(b => b.BlueprintId == c.Get("bp.id", string.Empty));
            var ids = new List<int>();
            for (int i = 0; i < 4 && bp != null; i++)
            {
                GridCell g = SiegeService.NearestPassable(new GridCell(core.X - 6 + i * 3, core.Y - 9), 10);
                MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp.BlueprintId, HomeValleyLayout.RegionId, new Vector2(g.X, g.Y), SquadHp, SquadHp, "Player", 2);
                if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
                {
                    MachineLoadoutRegistry.Register(s, rec.LogicId, bp.BlueprintId, 2);
                    ids.Add(r.LogicId);
                }
            }
            c.Set("roster", string.Join(",", ids.Select(x => x.ToString(CultureInfo.InvariantCulture))));
            c.SetInt("plans0", RaidDirectorService.Plans(s).Count);
            string fw = ConfigSystem.Instance.Tables.TbFirmwareKind.DataList.First(r => r != null && r.Faction == EnemyDefs.FactionOverclock).Id;
            for (int guard = 0; guard < 200 && s.SignalExposure < 31f; guard++)
            {
                CampaignExposureLedger.GrantRawFire(s, fw);
            }
            c.Set("home.fw", fw);
        }

        private static RaidPlanRecord OcPlan(JourneyContext c) =>
            RaidDirectorService.Plans(St).Skip(c.GetInt("plans0")).Where(p => p != null && p.Faction == EnemyDefs.FactionOverclock).OrderByDescending(p => p.Serial).FirstOrDefault();

        private static StepOutcome TickHomeFixture(JourneyContext c)
        {
            if (c.Get("home.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail("家园夹具：" + c.Get("home.err"));
            }
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            RaidPlanRecord p = OcPlan(c);
            BuildingRecord arr = HomeGridService.FindBuilding(s, c.Get("arr.id"));
            BuildingRecord intel = s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == IntelService.TypeId);
            string info = $"超控阵列 T{BuildingOps.TierOf(arr)}（{arr?.PowerState}）、信号核 {SignalCoreService.UnlockedSlots(s)} 槽；监听站 {intel?.PowerState}；暴露 {F(s.SignalExposure, "0.#")}（{c.Get("home.fw")}）；" +
                          $"电网 供 {F(HomeValleyPowerGrid.GetSummary(s).TotalSupply, "0")} / 需 {F(HomeValleyPowerGrid.GetSummary(s).TotalDemand, "0")}；留守队 {Team(c).Count} 台；超频突袭计划 {p?.PlanId ?? "无"}（{p?.Faction}，状态 {p?.State}）";
            if (p == null)
            {
                return c.StepElapsed < 15 ? StepOutcome.Wait : StepOutcome.Fail("暴露越过 30 后没有排出超频突袭：" + info);
            }
            return BuildingOps.TierOf(arr) == 2 && SignalCoreService.UnlockedSlots(s) == 4 && Team(c).Count == 4
                ? StepOutcome.Done("家园夹具：" + info)
                : StepOutcome.Fail("家园夹具不完整：" + info);
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

        private static StepOutcome TickArrayPanel(JourneyContext c)
        {
            BuildingRecord arr = HomeGridService.FindBuilding(St, c.Get("arr.id"));
            if (arr == null)
            {
                return StepOutcome.Fail("超控阵列不见了");
            }
            // 与 FGJ-M4 同一正式路径：建造模式里（手上不拿条目）左键点建筑 → 通用面板
            StepOutcome o = FgjM4Common.TickOpenPanel(c, OverrideArrayService.TypeId, new GridCell(arr.GridX, arr.GridY), "arr");
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            string line = ProductionPanelUIToolkit.OverrideArrayLines(St, arr);
            return StepOutcome.Done($"{o.Message}；“{Short(line.Replace("\n", " / "))}”；升级行“{Short(JourneyInput.FindUitk<Label>("[ProductionPanelHost]", "BpUpgradeLine")?.text ?? string.Empty)}”");
        }

        private static BuildingRecord LiveArray(CampaignState s) =>
            s.BuildingRecords.FirstOrDefault(b => b != null && b.BuildingTypeId == OverrideArrayService.TypeId && !HomeGridService.IsRelocationGhost(b) && b.ConstructionState == BuildingConstructionState.Operational);

        private static StepOutcome TickArrayT3(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            if (c.GetInt(FgjM3Common.SK(c, "up")) == 0)
            {
                if (FgjM3Common.ClickPanelBodyButton("[ProductionPanelHost]", "ProductionPanelBody", "BpUpgrade"))
                {
                    c.SetInt(FgjM3Common.SK(c, "up"), 1);
                    c.Log("点了生产面板的“升级”");
                }
                return StepOutcome.Wait;
            }
            BuildingRecord arr = LiveArray(s);
            int tier = BuildingOps.TierOf(arr);
            if ((int)(c.StepElapsed / 15) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 15));
                c.Log($"[{F(c.StepElapsed, "0")}s] 超控阵列 T{tier}；信号核 {SignalCoreService.UnlockedSlots(s)} 槽；废料 {s.Scrap}；超算残核在库 {HomeInventory.Stock(s, "supercomputer_core")}");
            }
            if (tier < 3)
            {
                bool planned = s.BuildingRecords.Any(b => b != null && b.BuildingTypeId == OverrideArrayService.TypeId && b.Tier == 3);
                return c.StepElapsed > 8 && !planned ? StepOutcome.Retry("点“升级”后没有生成 T3 升级施工") : StepOutcome.Wait;
            }
            HomeValleyPowerGrid.Recompute(s);
            string lines = ProductionPanelUIToolkit.OverrideArrayLines(s, arr);
            bool ok = SignalCoreService.UnlockedSlots(s) == 5 && SignalCoreService.ActiveSlots(s) == 5 && HomeInventory.Stock(s, "supercomputer_core") == 0 && lines.Contains("最高级");
            return ok
                ? StepOutcome.Done($"超控阵列升到 T3：超算残核从保管库取走、信号核 5 槽全部生效；面板“{Short(lines.Replace("\n", " / "))}”")
                : StepOutcome.Fail($"T3 完工但 信号核 {SignalCoreService.UnlockedSlots(s)}/{SignalCoreService.ActiveSlots(s)}（阵列 {arr?.PowerState}，电网 供 {F(HomeValleyPowerGrid.GetSummary(s).TotalSupply, "0")} / 需 {F(HomeValleyPowerGrid.GetSummary(s).TotalDemand, "0")}）、残核在库 {HomeInventory.Stock(s, "supercomputer_core")}、面板“{Short(lines)}”");
        }

        private static string WarnText()
        {
            VisualElement list = JourneyInput.FindUitk<VisualElement>(FgjM6Common.RaidHost, "RaidWarnList");
            return list == null ? string.Empty : string.Join(" / ", list.Query<TextElement>().ToList().Select(l => l.text).Where(t => !string.IsNullOrEmpty(t)));
        }

        private static StepOutcome TickOcRaidWarned(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            RaidPlanRecord plan = OcPlan(c);
            if (plan == null)
            {
                return StepOutcome.Fail("超频突袭计划不见了");
            }
            if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                c.Log($"[{F(c.StepElapsed, "0")}s] 计划 {plan.PlanId} 状态 {plan.State}、到达点 ({F(plan.ArriveX, "0")},{F(plan.ArriveY, "0")})；预警条“{Short(WarnText())}”");
            }
            // 夹具续（DEBT-FG11E2E01-05）：预警排在很久以后（最短间隔）——与 FGJ-M10 同样整体快进预警 / 出发 / 抵达，路线与集结时长不变。
            long wait = plan.WarnTick - GameClock.Ticks;
            if (plan.State == RaidDirectorService.StateScheduled && wait > GameClock.StepHz * 30L && c.GetInt("oc.ff") == 0)
            {
                long shift = wait - GameClock.StepHz * 5L;
                plan.WarnTick -= shift;
                plan.DepartTick -= shift;
                plan.ArrivalTick -= shift;
                c.SetInt("oc.ff", 1);
                c.Log($"夹具：超频计划 {plan.PlanId} 的预警还要 {F(wait / (double)GameClock.StepHz, "0")} 游戏秒 → 整体快进 {F(shift / (double)GameClock.StepHz, "0")} 游戏秒");
            }
            if ((plan.ArriveX != 0f || plan.ArriveY != 0f) && c.Get("oc.gen", string.Empty).Length == 0)
            {
                // 夹具续：突袭来路上（核心外 34 格，FgOverclockRaidSelfCheck C 段同一摆法）放电塔 + 发电机 + 一段屏障。
                CampaignState s = St;
                Vector2 dir = (new Vector2(plan.ArriveX, plan.ArriveY) - Core2).normalized;
                Vector2 at = Core2 + dir * 34f;
                BuildingRecord pole = FgSiegeSelfCheck.PlaceNear(s, "power_pole", at, Core2, 0.12f, 0.3f);
                BuildingRecord gen = FgSiegeSelfCheck.PlaceNear(s, "generator", at, Core2, 0.45f, 0.75f);
                BuildingRecord wall = FgSiegeSelfCheck.PlaceNear(s, "barrier_t1", at, Core2, 0.0f, 0.1f);
                HomeValleyPowerGrid.Recompute(s);
                c.Set("oc.gen", gen?.BuildingId ?? "无");
                c.Log($"夹具：来路 {Cell(at)} 放电塔 {pole?.BuildingId ?? "无"}、发电机 {gen?.BuildingId ?? "无"}、屏障 {wall?.BuildingId ?? "无"}");
            }
            if (plan.State < RaidDirectorService.StateWarned || c.Get("oc.gen", string.Empty).Length == 0)
            {
                return StepOutcome.Wait;
            }
            string warn = WarnText();
            bool intel = RaidDirectorService.IntelKnown(St, plan, GameClock.Ticks);
            if (warn.Length == 0)
            {
                return c.StepElapsed < 120 || plan.State < RaidDirectorService.StateDeparted ? StepOutcome.Wait : StepOutcome.Fail("突袭已出发，预警条还是空的");
            }
            if (intel && !warn.Contains("高速突击"))
            {
                return StepOutcome.Fail($"有监听站情报，但预警条没写“高速突击”：“{warn}”");
            }
            c.SetInt("res0", RaidResultService.StateOf(St)?.Results?.Length ?? 0);
            c.SetInt("fusers", plan.UnitIds.Select((id, i) => id.Contains("fuser") ? plan.UnitCounts[i] : 0).Sum());
            c.SetInt("burst0", OverclockCombatFeedback.BurstCues);
            c.SetInt("melt0", OverclockCombatFeedback.MeltWarnCues);
            c.Set("dur0", string.Join(";", St.BuildingRecords.Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId)
                .Select(b => b.BuildingId + "=" + F(DefenseService.DurabilityOf(St, b), "0.##"))));
            return StepOutcome.Done($"超频突袭 {plan.PlanId}（编成 {string.Join("/", plan.UnitIds.Select((id, i) => id + "×" + plan.UnitCounts[i]))}）发出预警：预警条“{Short(warn)}”；情报 {intel}" +
                                    (intel ? "（写着“高速突击”）" : "（监听站情报还没到，特点由 FgOverclockRaidSelfCheck B3 断言）"));
        }

        private static StepOutcome TickOcRaidHit(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            var dur0 = new Dictionary<string, float>();
            foreach (string kv in c.Get("dur0", string.Empty).Split(';'))
            {
                int eq = kv.LastIndexOf('=');
                if (eq > 0 && float.TryParse(kv.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                {
                    dur0[kv.Substring(0, eq)] = v;
                }
            }
            BuildingRecord first = s.BuildingRecords.FirstOrDefault(b => b != null && dur0.TryGetValue(b.BuildingId, out float d0) && DefenseService.DurabilityOf(s, b) < d0 - 0.01f);
            if (first == null)
            {
                RaidPlanRecord p = OcPlan(c);
                if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                    c.Log($"[{F(c.StepElapsed, "0")}s] 计划状态 {p?.State}（抵达第 {p?.ArrivalTick} 步，现在第 {GameClock.Ticks} 步）；还没有建筑掉耐久");
                }
                return StepOutcome.Wait;
            }
            byte cat = SiegeCatalog.CategoryOf(first.BuildingTypeId);
            return cat == BinGames.Sim.Combat.CombatSiegeConst.CatPower
                ? StepOutcome.Done($"超频突袭抵达：第一座掉耐久的是 {BuildingOps.NameOf(first)}（发电类）——超频突袭先扑发电设施（职能偏好表）")
                : StepOutcome.Fail($"第一座掉耐久的是 {first.BuildingTypeId}（类别 {cat}），不是发电类");
        }

        private static StepOutcome TickOcRaidCleared(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            CombatSite site = Home;
            RaidPlanRecord plan = OcPlan(c);
            // 结算记录按到达的队伍开（PlanId 可能为空）：取预警之后新开的那一份
            RaidResultRecord res = (RaidResultService.StateOf(s)?.Results ?? Array.Empty<RaidResultRecord>()).Skip(c.GetInt("res0")).LastOrDefault(r => r != null);
            if (AliveTeam(c).Count == 0)
            {
                return StepOutcome.Fail("留守队全灭");
            }
            if (res != null && res.Outcome != RaidResultService.OutcomeOpen)
            {
                int bursts = OverclockCombatFeedback.BurstCues - c.GetInt("burst0"), melts = OverclockCombatFeedback.MeltWarnCues - c.GetInt("melt0");
                return StepOutcome.Done($"超频突袭结束：结算 {res.Outcome}（击毁 {res.Killed}、自毁 {res.SelfDestructed}）；熔断者 {c.GetInt("fusers")} 台、熔断预警 {melts} 次、自爆 {bursts} 次；右键 {c.GetInt("oc.rc")} 次；{TeamHp(c)}");
            }
            // 留守队先守在屏障后面 40 秒（熔断者冲墙；屏障是超频突袭的正式反制），之后右键清场
            bool hold = c.StepElapsed < 40 && OverclockCombatFeedback.BurstCues == c.GetInt("burst0");
            if (!hold && !JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) > 4000)
            {
                int u = site != null ? site.FindNearestHostile(CorePos, 150f) : 0;
                if (u > 0 && site.Kernel.TryGetPosition(u, out double2 p) && FgjM3Common.TryClick(new Vector2((float)p.x, (float)p.y), 1))
                {
                    c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                    c.SetInt("oc.rc", c.GetInt("oc.rc") + 1);
                }
            }
            if ((int)(c.StepElapsed / 15) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 15));
                c.Log($"[{F(c.StepElapsed, "0")}s] 结算 {res?.Outcome ?? "未开"}；右键 {c.GetInt("oc.rc")} 次；{TeamHp(c)}");
            }
            return StepOutcome.Wait;
        }

        // ── 3. 余烬主机 ─────────────────────────────────────────────────────────────

        private static void ApplyBossFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("gate.at", string.Empty).Length > 0)
            {
                return;
            }
            PlannedAnchor gate = Gate;
            if (gate == null)
            {
                c.Set("boss.err", "规划层没有数据中心入口");
                return;
            }
            Vector2 e = TerritoryAnchorChecks.EntryOf(s, gate);
            SetV(c, "gate.at", e);
            HomeGridService.RevealArea(s, e, 40f);
            var relays = new List<string>();
            Vector2 last = FgjM10Journey.RelayChain(c, V(c, "lastRelay"), 200f, e, "m11_relay_dc", relays);
            c.Set("boss.relays", string.Join(" ", relays));
            SetV(c, "lastRelayDc", last);
            c.Set("boss.tower", SignalOutpost(c, last, e));
            BlueprintRecord bp = s.BlueprintRecords.FirstOrDefault(b => b.BlueprintId == c.Get("bp.id", string.Empty));
            if (bp != null && bp.Versions.All(v => v.Version != 2))
            {
                BlueprintCircuitBoard plain = WithUplink(BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>()));
                bp.Versions = bp.Versions.Append(plain.ToVersion(2, 0f)).ToArray();
            }
            foreach (int id in AliveTeam(c))
            {
                if (MachineRegistry.TryGetRecord(id, out MachineRecord m))
                {
                    m.MaxHealth = BossFixtureHealth;
                    m.Health = BossFixtureHealth;
                    if (bp != null)
                    {
                        m.BlueprintVersion = 2;
                        MachineLoadoutRegistry.Register(s, id, bp.BlueprintId, 2);
                    }
                }
            }
            if (SignalCoreService.SlotContentId(s, 1).Length == 0)
            {
                string ov = PrimitiveInventory.GrantBossFirmware(s, "fgjm11:overload2", FirmwareCatalog.FwOverloadId);
                SignalCoreResult eq = SignalCoreService.TryEquip(s, ov, 1);
                c.Set("boss.eq", eq.Success ? "ok" : eq.Message);
            }
            SignalCoverageService.Invalidate();
            c.SetInt("blocked0", SignalSeizeService.SlotBlockedCount);
            c.SetInt("restored0", SignalSeizeService.SlotRestoredCount);
            c.SetInt("grants0", EmberHostBoss.RewardGrants);
        }

        /// <summary>
        /// FG-GAP-239（ADR-REG-002 §3.1）：热寂数据城内部与外圈辐射带都是 3 级污染，中继塔进不去——正式出路是在外缘轻污染、已连通覆盖里建一座 T2 信号塔（500 格，要供电）
        /// 覆盖到数据中心入口。夹具在链尾 200 格内按正式放置校验找信号塔、固定发电机、电塔三块空地（施工 / 升级 / 接电由 FGJ-M4 / M6 覆盖），信号塔直接升到 T2。
        /// </summary>
        private static string SignalOutpost(JourneyContext c, Vector2 relay, Vector2 entrance)
        {
            CampaignState s = St;
            Vector2 dir = (entrance - relay).normalized;
            Vector2 perp = new Vector2(-dir.y, dir.x);
            string why = string.Empty;
            // 链尾 190 格内一圈圈找（朝入口的一侧多是 2 级以上污染，信号塔要 ≤ 1 级），按离入口由近到远试，离入口要在 T2 覆盖内留 30 格余量
            var cands = new List<Vector2>();
            for (float d = 20f; d <= 190f; d += 8f)
            {
                for (int a = 0; a < 36; a++)
                {
                    float ang = a * 10f * Mathf.Deg2Rad;
                    Vector2 q = relay + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * d;
                    if (Vector2.Distance(q, entrance) <= SignalCoverageService.TowerT2Radius - 30f)
                    {
                        cands.Add(q);
                    }
                }
            }
            foreach (Vector2 p in cands.OrderBy(q => Vector2.Distance(q, entrance)))
            {
                {
                    HomeGridService.RevealArea(s, p, 12f);
                    var tw = new GridCell(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y));
                    var gen = new GridCell(tw.X + Mathf.RoundToInt(perp.x * 7f), tw.Y + Mathf.RoundToInt(perp.y * 7f));
                    var pole = new GridCell(tw.X + Mathf.RoundToInt(perp.x * 3.5f), tw.Y + Mathf.RoundToInt(perp.y * 3.5f));
                    GridPlacementResult vt = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeSignalTower, tw, 0, asPlayerPlacement: false, checkCost: false);
                    if (!vt.Ok)
                    {
                        why = string.Join("/", vt.Reasons.Select(x => x.Code.ToString()));
                        continue;
                    }
                    GridPlacementResult vg = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeGenerator2, gen, 0, asPlayerPlacement: false, checkCost: false);
                    GridPlacementResult vp = HomeGridService.ValidatePlacement(s, "power_pole", pole, 0, asPlayerPlacement: false, checkCost: false);
                    if (!vg.Ok || !vp.Ok)
                    {
                        why = "发电机/电塔 " + string.Join("/", vg.Reasons.Concat(vp.Reasons).Select(x => x.Code.ToString()));
                        continue; // 三块占地互不重叠（塔 / 电塔 / 发电机沿垂线相隔 3.5、7 格），都过校验才一起建
                    }
                    BuildingRecord t = FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeSignalTower, "m11_dc_tower", tw);
                    FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeGenerator2, "m11_dc_gen", gen);
                    FgProductionSelfCheck.Built(s, "power_pole", "m11_dc_pole", pole);
                    t.Tier = 2;
                    HomeValleyPowerGrid.Recompute(s);
                    SignalCoverageService.Invalidate();
                    return $"T2 信号塔 ({tw.X},{tw.Y}) 离链尾 {F(Vector2.Distance(t.Position, relay), "0")}、离入口 {F(Vector2.Distance(t.Position, entrance), "0")}，供电 {t.PowerState}";
                }
            }
            return "[信号塔放不下：" + why + "]";
        }

        private static StepOutcome TickBossFixture(JourneyContext c)
        {
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait;
            }
            if (c.Get("boss.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail("夹具：" + c.Get("boss.err"));
            }
            Vector2 e = V(c, "gate.at");
            bool cov = SignalCoverageService.Sample(HomeValleyLayout.RegionId, e).Covered;
            string info = $"数据中心入口 {Gate?.Id} 入口点 {Cell(e)}（进入半径 {Gate?.EntryRadius}）；中继链续 {c.Get("boss.relays")}；{c.Get("boss.tower")}；入口在覆盖里 {cov}；2 号槽 {SignalCoreService.SlotContentId(St, 1)}（{c.Get("boss.eq", "已有")}）";
            c.Log(info);
            return cov ? StepOutcome.Done("首领夹具生效：" + info + $"；远征队 {TeamHp(c)}")
                : c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail("夹具后数据中心入口不在信号覆盖里（右键命令发不到）：" + info);
        }

        private static StepOutcome TickGateGo(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            List<int> team = AliveTeam(c);
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 1 && team.Count > 0 && InteriorApproachService.HasIntent(s))
            {
                List<int> ids = s.World.ApproachEnterIds.ToList();
                return team.All(ids.Contains) && s.World.ApproachEnterAnchorId == Gate?.Id
                    ? StepOutcome.Done($"右键入口 {Cell(V(c, "gate.at"))}：{team.Count} 台登记“到了就进”（{Gate?.Id}）；{TeamHp(c)}")
                    : StepOutcome.Fail($"右键入口后进入意图不对：入口 {s.World.ApproachEnterAnchorId}（应为 {Gate?.Id}），机器 [{string.Join("、", ids.Select(Label))}]，远征队 [{string.Join("、", team.Select(Label))}]");
            }
            return RightClickGround(c, V(c, "gate.at"));
        }

        private static VisualElement EntrancePanel => JourneyInput.FindUitk<VisualElement>("[WorldPlacesHost]", "EntranceRoot");

        private static bool PanelOpen => EntrancePanel != null && !EntrancePanel.ClassListContains("uk-hidden");

        private static StepOutcome TickGateOpen(JourneyContext c)
        {
            if (PanelOpen)
            {
                string title = JourneyInput.FindUitk<Label>("[WorldPlacesHost]", "EntranceTitle")?.text ?? string.Empty;
                string desc = JourneyInput.FindUitk<Label>("[WorldPlacesHost]", "EntranceInfo")?.text ?? string.Empty;
                return StepOutcome.Done($"入口面板打开：“{title}”｜{Short(desc.Replace('\n', ' '))}");
            }
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0)
            {
                if (!JourneyInput.Holding && FgjM3Common.TryClick(V(c, "gate.at"), 0))
                {
                    c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                }
                return StepOutcome.Wait;
            }
            return c.StepElapsed < 4 ? StepOutcome.Wait : StepOutcome.Retry("左键点入口后入口面板没打开");
        }

        private static StepOutcome TickGateEnter(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            InteriorSiteController dc = Dc;
            List<int> team = AliveTeam(c);
            bool inside = dc != null && team.Count > 0 && team.All(id => MachineRegistry.TryGetRecord(id, out MachineRecord m) && m.RegionId == Center);
            if (inside && EmberHostBoss.IsEngaged(St))
            {
                return c.StepElapsed < 2 ? StepOutcome.Wait
                    : StepOutcome.Done($"进入数据中心：{team.Count} 台在室内；余烬主机开战（{EmberHostBoss.PhaseName(EmberHostBoss.PhaseOf(St))}），入口说明 / 首领 HUD 写名称；进门前自动存档");
            }
            if (!inside && !InteriorApproachService.HasIntent(St) && !string.IsNullOrEmpty(InteriorApproachService.LastOutcome))
            {
                // 意图被取消 / 进入被拒（下了别的命令、走不到、TryEnter 拒绝）：如实失败，写明原因；进去了 LastOutcome 为空（室内载入那几帧照常等）。
                return StepOutcome.Fail($"“到了就进”意图没了却没进数据中心：{InteriorApproachService.LastOutcome}；{string.Join("、", team.Select(id => Label(id) + "@" + Cell(PosOf(id))))}");
            }
            if ((int)(c.StepElapsed / 15) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 15));
                List<int> near = InteriorSiteService.MachinesNearEntrance(St, Gate);
                c.Log($"[{F(c.StepElapsed, "0")}s] 入口旁 {near.Count}/{team.Count}；{string.Join("、", team.Select(id => Label(id) + "@" + Cell(PosOf(id))))}（镜头 {WorldView.ObservedSiteId}、暂停 {GameClock.Paused}）");
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickSelectInterior(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            InteriorSiteController dc = Dc;
            if (dc == null)
            {
                return StepOutcome.Fail("数据中心没载入");
            }
            List<int> team = AliveTeam(c);
            IReadOnlyList<int> sel = dc.SquadCommands.Selection;
            if (sel != null && team.Count > 0 && sel.Count == team.Count && team.All(sel.Contains))
            {
                return StepOutcome.Done($"室内选择集 [{string.Join("、", sel.Select(Label))}]");
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 700)
            {
                return StepOutcome.Wait;
            }
            int k = c.GetInt(FgjM3Common.SK(c, "k"));
            if (k >= team.Count)
            {
                return StepOutcome.Retry($"点完一遍后室内选择集是 [{string.Join("、", (sel ?? Array.Empty<int>()).Select(Label))}]");
            }
            HomeValleyMachineMarker m = dc.MachineMarkers.FirstOrDefault(x => x != null && x.LogicId == team[k]);
            if (m?.View == null)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail($"{Label(team[k])} 在数据中心里没有模型");
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

        /// <summary>
        /// 正式打法：阶段二本体不可伤，先右键数据尖刺；其余阶段先清守卫再打本体。每 3 秒按当前局面再右键一次（被打断 / 目标没了就换下一个）。
        /// 断言：封锁 / 夺回各发生（共用计数 +1）、封锁期间被封槽固件保留且不生效、夺回后同一槽恢复；看到阶段三；最后击毁。
        /// </summary>
        private static StepOutcome TickBossFight(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            InteriorSiteController dc = Dc;
            if (dc == null)
            {
                return StepOutcome.Fail("数据中心没载入（全灭被送出 / 撤出？）");
            }
            List<int> team = AliveTeam(c);
            if (team.Count == 0)
            {
                return StepOutcome.Fail("远征队在数据中心里全灭");
            }
            EmberPhase phase = EmberHostBoss.PhaseOf(s);
            EmberBossRecord r = EmberHostBoss.Record(s);
            int blocked = SignalSeizeService.SlotBlockedCount - c.GetInt("blocked0");
            int restored = SignalSeizeService.SlotRestoredCount - c.GetInt("restored0");
            if (phase == EmberPhase.Lockout && c.GetInt("lock.seen") == 0)
            {
                int slot = r.BlockedSlot;
                string fw = SignalCoreService.SlotContentId(s, slot);
                bool kept = fw.Length > 0 && SignalCoreService.ActiveContentIds(s)[slot].Length == 0 && SignalSeizeService.IsBlocked(s, slot);
                if (blocked < 1 || !kept)
                {
                    return StepOutcome.Fail($"进入阶段二·槽位封锁，但共用计数 SlotBlockedCount +{blocked}、第 {slot + 1} 槽“{fw}”保留不生效 {kept}");
                }
                c.SetInt("lock.seen", 1);
                c.SetInt("lock.slot", slot);
                c.Set("lock.fw", fw);
                c.Log($"[{F(c.StepElapsed, "0")}s] 阶段二·槽位封锁：第 {slot + 1} 槽（{fw}）被封锁，共用计数 SlotBlockedCount +{blocked}；HUD 状态行“{SignalSeizeService.HudLine(s)}”；数据尖刺 {EmberHostBoss.SpikesAlive(s)}");
            }
            if (c.GetInt("lock.seen") == 1 && c.GetInt("lock.back") == 0 && restored >= 1)
            {
                int slot = c.GetInt("lock.slot");
                bool back = !SignalSeizeService.IsBlocked(s, slot) && SignalCoreService.ActiveContentIds(s)[slot] == c.Get("lock.fw");
                if (!back)
                {
                    return StepOutcome.Fail($"尖刺打完、共用计数 SlotRestoredCount +{restored}，但第 {slot + 1} 槽没恢复生效（封锁 {SignalSeizeService.IsBlocked(s, slot)}）");
                }
                c.SetInt("lock.back", 1);
                c.Log($"[{F(c.StepElapsed, "0")}s] 夺回：数据尖刺全毁，第 {slot + 1} 槽恢复（共用计数 SlotRestoredCount +{restored}）；阶段 {EmberHostBoss.PhaseName(phase)}");
            }
            if (phase == EmberPhase.Meltdown)
            {
                c.SetInt("melt.seen", 1);
            }
            if ((int)(c.StepElapsed / 10) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 10));
                RegionEnemyRecord core0 = EmberHostBoss.Core(s);
                c.Log($"[{F(c.StepElapsed, "0")}s] {EmberHostBoss.PhaseName(phase)}：本体 {F(core0?.Health ?? 0, "0")}/{F(core0?.MaxHealth ?? 0, "0")}、尖刺 {EmberHostBoss.SpikesAlive(s)}、散热口 {r?.VentState}；{TeamHp(c)}；右键 {c.GetInt("boss.rc")} 次");
            }
            if (EmberHostBoss.IsDefeated(s))
            {
                if (c.GetInt("lock.seen") == 0 || c.GetInt("lock.back") == 0 || c.GetInt("melt.seen") == 0)
                {
                    return StepOutcome.Fail($"余烬主机被击毁，但没走完三阶段（封锁 {c.GetInt("lock.seen")}、夺回 {c.GetInt("lock.back")}、熔毁倒计时 {c.GetInt("melt.seen")}）");
                }
                return StepOutcome.Done($"右键攻击 {c.GetInt("boss.rc")} 次：升频 → 槽位封锁（第 {c.GetInt("lock.slot") + 1} 槽，SlotBlockedCount +{blocked}）→ 打掉 3 根数据尖刺夺回（SlotRestoredCount +{restored}）→ 熔毁倒计时 → 击毁；" +
                                        $"熔毁 {EmberHostBoss.Blasts} 次、倒计时警报 {EmberHostBoss.CountdownBeeps} 次；{TeamHp(c)}");
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) < 3000)
            {
                return StepOutcome.Wait;
            }
            Vector2 target;
            if (!PickBossTarget(c, dc, phase, out target))
            {
                return StepOutcome.Wait;
            }
            if (FgjM3Common.TryClick(target, 1))
            {
                c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                c.SetInt("boss.rc", c.GetInt("boss.rc") + 1);
            }
            return StepOutcome.Wait;
        }

        private static bool PickBossTarget(JourneyContext c, InteriorSiteController dc, EmberPhase phase, out Vector2 target)
        {
            target = default;
            CombatSite site = dc.Combat;
            Vector2 me = PosOf(AliveTeam(c)[0]);
            if (phase == EmberPhase.Lockout)
            {
                foreach (EnemyEncounter sp in EmberHostBoss.Spikes.OrderBy(x => site.TryGetEnemyPosition(x.Id, out Vector2 p) ? Vector2.Distance(me, p) : 1e9f))
                {
                    if (site.IsEnemyAlive(sp.Id) && site.TryGetEnemyPosition(sp.Id, out target))
                    {
                        return true;
                    }
                }
                return false;
            }
            foreach (EnemyEncounter g in EnemyDefs.Encounters(Center, EnemyDefs.ConditionBase).Where(x => x.Id != EmberHostBoss.CoreInstanceId && !EmberHostBoss.IsSpike(x.Id))
                         .OrderBy(x => site.TryGetEnemyPosition(x.Id, out Vector2 p) ? Vector2.Distance(me, p) : 1e9f))
            {
                if (site.IsEnemyAlive(g.Id) && site.TryGetEnemyPosition(g.Id, out target))
                {
                    return true;
                }
            }
            return site.TryGetEnemyPosition(EmberHostBoss.CoreInstanceId, out target);
        }

        private static StepOutcome TickBossWon(JourneyContext c)
        {
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            bool execute = s.PrimitiveChips.Count(p => p.CardDefId == "fw_execute" && p.Origin == PrimitiveInventory.OriginBoss) == 1;
            bool packet = LootPickService.Packets(s).Any(p => p.Source == LootPickService.SourceBoss);
            bool pacified = TerritoryControlService.Find(s, Overclock)?.LeaderDefeated == true;
            KeyMaterial keyCore = QuestCatalog.KeyMaterialOf("supercomputer_core");
            bool keyMat = keyCore != null && CampaignObjectiveTracker.HasKeyMaterial(s, keyCore);
            string nodeId = NarrativeCatalog.All.FirstOrDefault(en => en.Trigger == NarrativeCatalog.TriggerLeader + Overclock)?.Id ?? "-";
            bool narrative = NarrativeService.Has(s, nodeId);
            bool cleaned = (s.SignalCore.SlotBlocks?.Length ?? 0) == 0;
            int grants = EmberHostBoss.RewardGrants - c.GetInt("grants0");
            if (!(execute && packet && pacified && keyMat && narrative && cleaned && grants == 1))
            {
                return StepOutcome.Fail($"击毁后：处决 {execute}、三选一数据包 {packet}、超频平定 {pacified}、超算残核进保管库 {keyMat}、剧情节点“破译”（{nodeId}）{narrative}、封锁解除 {cleaned}、奖励发放 {grants} 次（应 1）");
            }
            return StepOutcome.Done($"奖励只发一次：核心固件“处决”、首领三选一数据包；超频领地平定；剧情节点 {nodeId}（破译片段）进资料库；关键材料“超算残核”在核心保管库；信号核封锁全部解除");
        }

        private static StepOutcome TickExitGo(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            InteriorSiteController dc = Dc;
            if (dc == null)
            {
                return StepOutcome.Fail("数据中心没载入");
            }
            List<int> team = AliveTeam(c);
            List<int> near = InteriorSiteService.MachinesNearExit(St, Center);
            if (team.Count > 0 && team.All(near.Contains) && c.GetInt(FgjM3Common.SK(c, "clicked")) == 1)
            {
                return StepOutcome.Done($"右键出口旁：{team.Count} 台都在出口 {F(InteriorSiteService.ExitRadius, "0")} 格内");
            }
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0 && c.StepElapsed > 0.4)
            {
                if (!JourneyInput.Holding && FgjM3Common.TryClick(dc.ExitPoint, 1))
                {
                    c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                }
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickExitOpen(JourneyContext c)
        {
            InteriorSiteController dc = Dc;
            if (dc == null)
            {
                return StepOutcome.Fail("数据中心没载入");
            }
            if (PanelOpen)
            {
                return StepOutcome.Done("出口面板打开：“" + (JourneyInput.FindUitk<Label>("[WorldPlacesHost]", "EntranceTitle")?.text ?? string.Empty) + "”");
            }
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0)
            {
                if (!JourneyInput.Holding && FgjM3Common.TryClick(dc.ExitPoint, 0))
                {
                    c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                }
                return StepOutcome.Wait;
            }
            return c.StepElapsed < 4 ? StepOutcome.Wait : StepOutcome.Retry("左键点出口后出口面板没打开");
        }

        private static StepOutcome TickExitDone(JourneyContext c)
        {
            List<int> team = AliveTeam(c);
            bool outside = team.Count > 0 && team.All(id => MachineRegistry.TryGetRecord(id, out MachineRecord m) && m.RegionId == HomeValleyLayout.RegionId);
            if (outside && Dc == null)
            {
                return c.StepElapsed < 2 ? StepOutcome.Wait : StepOutcome.Done($"离开数据中心：{team.Count} 台回到星球表面入口旁（{string.Join("、", team.Select(id => Cell(PosOf(id))))}）");
            }
            if (c.GetInt(FgjM3Common.SK(c, "go")) == 0)
            {
                if (JourneyInput.ClickUitk("[WorldPlacesHost]", "EntranceGo"))
                {
                    c.SetInt(FgjM3Common.SK(c, "go"), 1);
                }
                return StepOutcome.Wait;
            }
            return c.StepElapsed < 20 ? StepOutcome.Wait : StepOutcome.Fail($"点“离开”后没回到星球表面（在外 {outside}、室内仍载入 {Dc != null}）");
        }
    }
}
