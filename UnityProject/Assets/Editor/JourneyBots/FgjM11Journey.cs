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
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using GameLogic.View;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG11-E2E-01：M11 出口旅程 FGJ-M11（里程碑文档 FG-M11；00 契约 IC-REQ-021）。从主菜单“新建”出发（固定测试种子 42、标准难度），全程正式输入，
    /// RTS 口径：左键选择 / Shift 加选、右键地面 = 移动、右键敌人 = 攻击，按钮 / 快捷键只切状态；等待段用正式 3 倍速。
    ///
    /// 里程碑原文：进入热寂数据城，利用冷却塔管理热量 → 遭遇镜像体，换一套固件应对 → 击败余烬主机：槽位被劫持后夺回 → 拿到破译片段和超算残核 → 超控阵列升到 T3，信号核 5 槽。
    /// 承接 FG-GAP-235（热寂数据城区域片段：辐射区里接入 HUD 写热量上限 -25% → 冷却塔旁恢复、地图有范围圈 → 塔被打掉 → HUD 回落、范围圈消失）、
    /// FG-GAP-237（超频突袭片段：超频暴露贡献 → 正式预警写“特点：高速突击” → 到达 → 猎犬先扑发电设施 → 熔断者顶墙熔断 → 结算）。
    ///
    /// 轻量口径（用户 2026-10-12）：一条正向旅程走通核心循环，每个 M11 新系统至少被真实输入触发一次并断言结果；不为凑剧情改数值。
    /// 镜像体与槽位封锁的断言用共用事件计数（卡片“必须同时交付”）：镜像复制 = <see cref="CombatSite.MirrorDetectCopies"/>，槽位封锁 / 夺回 =
    /// <see cref="SignalSeizeService.SlotBlockedCount"/> / <see cref="SignalSeizeService.SlotRestoredCount"/>（与自检 FgOverclockFactionSelfCheck F1、FgEmberHostSelfCheck C1 / C2 同一计数）。
    /// 夹具都写在步骤标题与报告里；被测行为（辐射与冷却塔、HUD、地图圈、镜像侦测与复制、信号核换装、余烬主机三阶段、奖励、超控阵列升级、超频突袭）全走正式系统。
    /// 用法：bash tools/unity-journey.sh FGJ-M11；断点见 <see cref="Build"/> 的 CheckpointAfter（--from 续跑）。
    /// </summary>
    public static partial class FgjM11Journey
    {
        public const string Id = "FGJ-M11";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private const string Overclock = EnemyDefs.FactionOverclock;
        private const int SquadSize = 4;
        private const float SquadHp = 400f;
        private const string MirrorId = "fgjm11-mirror", NodeId = "fgjm11-node", TurretId = "fgjm11-turret";

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
                S("m11_fix", "开局夹具（DEBT-FG11E2E01-01）：第二幕进度——第 2 幕、铸造 / 澄净 / 静默领地平定、防腐涂层已到手（首领战与涂层主线由 FGJ-M9 / FGJ-M10 覆盖）；核心往热寂数据城外圈一座冷却塔一路建成的信号中继（每座过正式放置校验）；" +
                             $"{SquadSize} 台 ERC-003（铸造重炮 + 燃迹）停在辐射带外；信号核 1 号槽装过载（刻印 / 装槽由 FGJ-M5 覆盖），基元仓里另有一枚标记跳转芯片", 20, ApplyFixture, TickFixture),
                S("ghost1", "第一次信号幽灵（FG12-EVT-03，夹具进第 2 幕后剧情固定发生）：夺控一台己方机器 → 从机器列表接入它（反向接入）3 秒后夺回；不在覆盖里被拒就等 45 秒到时归还", 150, null, FgjM10Journey.TickFirstGhost),

                // ── 1. 热寂数据城：高温辐射与冷却塔（FG-GAP-235） ──
                S("sel", "左键点一台远征队、Shift 左键加选其余几台", 60, null, FgjM10Journey.TickSelectTeam, retries: 2),
                S("heat_in", "右键辐射带里离冷却塔远的地面（移动）：远征队走进高温辐射区", 180, null, TickHeatIn),
                S("heat_up", "命令栏机器列表点第一台：接入（镜头直控）——接入 HUD 热量一栏写“高温辐射：热量上限 -25%（到运转中的冷却塔旁可恢复）”", 40,
                    c => FgjM1Journey.ClickMachineList(c, AliveTeam(c)[0]), c => TickUplinkHeat(c, false), retries: 3),
                S("heat_exit", "按接入 / 退出接入键（默认 M）回战略视角", 20, c => JourneyInput.PressAction(GameActionId.ToggleCameraView), FgjM10Journey.TickBackToStrategy, retries: 2),
                S("sel2", "左键点一台远征队、Shift 左键加选其余几台", 60, null, FgjM10Journey.TickSelectTeam, retries: 2),
                S("tower_go", "右键冷却塔旁的地面（移动）：远征队走进运转中的冷却塔范围", 180, null, TickTowerGo),
                S("tower_up", "命令栏机器列表点第一台：接入——接入 HUD 热量一栏改写“冷却塔：热量上限已恢复”，内核热量上限回到 100%；地图上有这座塔的蓝色范围圈", 40,
                    c => FgjM1Journey.ClickMachineList(c, AliveTeam(c)[0]), c => TickUplinkHeat(c, true), retries: 3),

                S("tower_exit1", "按接入 / 退出接入键回战略视角", 20, c => JourneyInput.PressAction(GameActionId.ToggleCameraView), FgjM10Journey.TickBackToStrategy, retries: 2),
                S("sel_back", "左键点一台远征队、Shift 左键加选其余几台", 60, null, FgjM10Journey.TickSelectTeam, retries: 2),
                S("back_park", "右键辐射带外的停车处（移动）：远征队退出辐射带（离塔 50 格外，冷却塔那边的驻军看不到他们）", 180, null, TickBackPark),

                // ── 2. 遭遇镜像体，换一套固件应对 ──
                S("garrison_fix", "事件夹具（DEBT-FG11E2E01-02）：冷却塔另一侧登记一小股超频驻军——镜像体 + 算力节点（正式敌人表，EnemySpawnBuilder 真实生成；据点展开由 FGJ-M10 覆盖）", 15, ApplyGarrison, TickGarrison),
                S("mirror_up", "命令栏机器列表点第一台：接入（镜头直控）", 40, c => FgjM1Journey.ClickMachineList(c, AliveTeam(c)[0]), TickUplinkPlain, retries: 3),
                S("mirror_copy", "接入中：WASD 开到算力节点 10 格内，左键点它开火——重炮 + 过载打出熔穿过载（核心固件发动）→ 附近的镜像体复制它（共用计数 MirrorDetectCopies +1、复制通知、悬停“镜像复制品：熔穿过载”）", 120, null, TickMirrorCopy),
                S("mirror_exit", "按接入 / 退出接入键回战略视角", 20, c => JourneyInput.PressAction(GameActionId.ToggleCameraView), FgjM10Journey.TickBackToStrategy, retries: 2),
                S("sc_open", "按信号核键（默认 P）打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
                S("sc_swap", "换一套固件：点 1 号槽、点基元仓里的标记跳转、点“装入”——过载换下，镜像体手里只剩旧的熔穿过载复制品", 20, null,
                    c => FgjM5Common.TickEquipChip(c, 0, () => c.Get("chip.mark", string.Empty), "标记跳转换进信号核 1 号槽"), retries: 2),
                S("sc_close", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),
                S("sel3", "左键点一台远征队、Shift 左键加选其余存活的", 60, null, FgjM10Journey.TickSelectTeam, retries: 2),
                S("garrison_fight", "右键镜像体（攻击），打掉再右键算力节点、贴着塔身三面的速射炮台：驻军清空；燃迹的明火与熔融金属不认敌我，把旁边的冷却塔一起烧掉", 360, null, TickGarrisonFight),
                // FG-GAP-238 修复后（结构单位按占地接触斑块）：燃迹的明火压到塔身就会烧塔；波及没烧完时玩家按攻击键 + 左键塔身（显式攻击可摧毁的中立建筑）打掉它
                S("tower_attack", "按攻击键（默认 T）、左键点冷却塔塔身：远征队强制攻击这座中立的冷却塔遗址（右键仍只认敌人）；驻军战的明火已把它烧掉的话这一步直接记下", 60,
                    c => FgjM1Journey.PressIf(GameActionId.CommandAttack, !CoolingTowerService.IsDestroyed(St, TowerId(c))), TickTowerAttack, retries: 2),
                S("tower_down", "冷却塔遗址被摧毁：通知“冷却塔遗址被摧毁…不再恢复热量上限”，地图上的范围圈消失", 240, null, TickTowerDown),
                S("tower_up2", "命令栏机器列表点一台：接入——站在原塔旁，接入 HUD 热量一栏回落到“高温辐射：热量上限 -25%”", 40,
                    c => FgjM1Journey.ClickMachineList(c, AliveTeam(c)[0]), c => TickUplinkHeat(c, false), retries: 3),
                S("tower_exit", "按接入 / 退出接入键回战略视角", 20, c => JourneyInput.PressAction(GameActionId.ToggleCameraView), FgjM10Journey.TickBackToStrategy, retries: 2),
            };
            AddBossSteps(steps);
            AddHomeSteps(steps);
            return new JourneyDef
            {
                Id = Id,
                Title = "M11 出口：进入热寂数据城、利用冷却塔管理热量（塔被打掉后热量上限回落）→ 遭遇镜像体，换一套固件应对 → 击败余烬主机（槽位被封锁后夺回）→ 拿到破译片段和超算残核 → 超控阵列升到 T3、信号核 5 槽 → 回家应对一次超频突袭",
                Seed = TestSeed,
                TotalTimeoutSeconds = 2400,
                OnFinish = Cleanup,
                Version = 2, // FG15-PL-01 修复轮 4：入口段改为先看面板、再右键入口“走过去并进入”（步骤顺序变了，旧断点作废）
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

        private static Vector2 V(JourneyContext c, string key)
        {
            string[] f = c.Get(key, "0,0").Split(',');
            return new Vector2(float.Parse(f[0], CultureInfo.InvariantCulture), float.Parse(f[1], CultureInfo.InvariantCulture));
        }

        private static void SetV(JourneyContext c, string key, Vector2 v) => c.Set(key, F(v.x, "0.##") + "," + F(v.y, "0.##"));

        private static string Cell(Vector2 p) => $"({F(p.x, "0")},{F(p.y, "0")})";

        private static string TeamHp(JourneyContext c) => string.Join("、", Team(c).Select(id => MachineRegistry.TryGetRecord(id, out MachineRecord m)
            ? $"{Label(id)} {(m.IsAlive ? F(m.Health, "0") : "阵亡")}" : $"{id}?"));

        private static int NotifyCount(string typeId) => NotificationCenter.History.Count(n => n.Type != null && n.Type.Id == typeId);

        private static string LastNotify(string typeId) => NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == typeId)?.Text ?? string.Empty;

        private static string Short(string s) => s == null ? string.Empty : s.Length > 90 ? s.Substring(0, 90) + "…" : s;

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
            c.Log($"共用事件计数（全程）：镜像复制 {CombatSite.MirrorDetectCopies}、镜像拒绝 {CombatSite.MirrorRefused}、槽位封锁 {SignalSeizeService.SlotBlockedCount}、槽位夺回 {SignalSeizeService.SlotRestoredCount}、冷却塔摧毁 {CoolingTowerService.DestroyedCount}");
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM3Common.FrameReport("全程"));
            JourneyCommon.Cleanup(c);
        }

        // ── 开局夹具 ────────────────────────────────────────────────────────────────

        private static PlannedTerritory Oc => WorldGenService.PlanFor(St)?.Find(Overclock);

        private static Vector2 TowerPos(JourneyContext c) => V(c, "tw.at");

        private static string TowerId(JourneyContext c) => c.Get("tw.id", string.Empty);

        /// <summary>
        /// 开局夹具（DEBT-FG11E2E01-01，轻量口径）：第二幕进度——第 2 幕；铸造 / 澄净 / 静默领地平定（正式首领战由 FGJ-M9 / FGJ-M10 覆盖，余烬主机由本旅程第 3 段真打）；
        /// 核心往热寂数据城外圈辐射带里离核心最近的冷却塔一路建成的信号中继（每座过正式放置校验、揭开沿途的雾——建中继与探索由 FGJ-M8 覆盖）；
        /// 4 台 ERC-003（铸造重炮 + 燃迹，耐久 400）停在辐射带外；信号核 1 号槽装过载（刻印 / 装槽由 FGJ-M5 覆盖），基元仓里另放一枚标记跳转芯片（第 2 段换装用）。
        /// </summary>
        private static void ApplyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("roster", string.Empty).Length > 0)
            {
                return;
            }
            PlannedTerritory t = Oc;
            if (t == null || t.HazardWidth <= 0)
            {
                c.Set("fix.err", $"规划层没有超频领地 / 没有外圈辐射带（{t?.Id}，带宽 {t?.HazardWidth}）");
                return;
            }
            if (s.Progress != null)
            {
                c.SetInt("act0", s.Progress.Act);
                s.Progress.Act = Math.Max(Math.Max(2, t.Act), s.Progress.Act);
            }
            foreach (string f in new[] { EnemyDefs.FactionFoundry, EnemyDefs.FactionClarity, EnemyDefs.FactionSilent })
            {
                TerritoryControlService.MarkLeaderDefeated(s, f);
            }
            Vector2 core = CorePos;
            CoolingTowerService.Tower tw = CoolingTowerService.Towers(s).Where(x => x.TerritoryId == t.Id && x.Id.Contains("#belt"))
                .OrderBy(x => Vector2.Distance(core, new Vector2(x.X, x.Y))).FirstOrDefault();
            if (string.IsNullOrEmpty(tw.Id))
            {
                c.Set("fix.err", $"超频领地 {t.Id} 外圈辐射带上没有冷却塔");
                return;
            }
            var tp = new Vector2(tw.X, tw.Y);
            c.Set("tw.id", tw.Id);
            SetV(c, "tw.at", tp);
            Vector2 center = new Vector2(t.CenterX, t.CenterY);
            Vector2 u = (core - tp).normalized; // 塔 → 核心
            Vector2 perp = new Vector2(-u.y, u.x);

            // 停车处：辐射带外、朝核心一侧；辐射区里离塔远的点（走进辐射）；塔旁（塔圈里、靠核心一侧）。
            Vector2 park = tp + u * (t.HazardWidth * 0.5f + 14f);
            for (int i = 0; i < 20 && HeatRadiationService.ZoneAt(s, park) != null; i++)
            {
                park += u * 6f;
            }
            Vector2 p1 = tp;
            for (float side = CoolingTowerService.Radius + 12f; side < CoolingTowerService.Radius + 60f; side += 4f)
            {
                Vector2 cand = tp + perp * side;
                if (HeatRadiationService.ZoneAt(s, cand) != null && !CoolingTowerService.InActiveTower(s, cand) && SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(cand.x), Mathf.RoundToInt(cand.y)), 3).X != int.MinValue)
                {
                    p1 = cand;
                    break;
                }
            }
            Vector2 p2 = tp + u * 5f;
            SetV(c, "park", park);
            SetV(c, "p1", p1);
            SetV(c, "p2", p2);
            HomeGridService.RevealArea(s, tp, 70f); // 第二幕前沿着这条路探索过（探索由 FGJ-M1 / M8 覆盖）
            HomeGridService.RevealArea(s, p1, 30f);

            // 第二幕已完成防腐涂层主线（FGJ-M10 覆盖）：涂层蓝图记入事件账本，中继可建在 1～2 级污染上（FG-GAP-164 第 3 项）
            CampaignEventLedger.TryGrant(s, CampaignObjectiveTracker.BlueprintEventId(AcidMarshService.CoatingBlueprintId), "QuestBlueprint", s.PlaySeconds, "main.anticorrosion");
            AcidMarshService.InvalidateCoating();
            var relays = new List<string>();
            Vector2 last = FgjM10Journey.RelayChain(c, core, SignalCoverageService.CoreRadius, tp, "m11_relay", relays);
            SetV(c, "lastRelay", last);
            c.Set("fix.relays", string.Join(" ", relays));
            SignalCoverageService.Invalidate();

            // 第一幕已拿到 / 解析过的内容（重炮由 FGJ-M1、燃迹由 FGJ-EMG-R / FGJ-M10 覆盖来源）
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(new[] { ComponentCatalog.CompCannonId, "fw_burntrail" }).Distinct().ToArray();
            if (c.Get("bp.id", string.Empty).Length == 0)
            {
                // 重炮 + 燃迹 + 接入口（核心固件只经接入口生效，FGR-SIG-012；M9 同一保存入口）
                BlueprintCircuitBoard board = WithUplink(BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, new[] { "fw_burntrail" }));
                BlueprintSaveResult save = BlueprintEditorService.TrySave(s, board, null, "重炮燃迹接入口", saveAsNewRecord: true);
                c.Set("bp.id", save.Success ? save.BlueprintId : string.Empty);
                c.Set("bp.note", $"保存 {save.Success}（{save.FailureReason}）反应 {save.ReactionId}");
            }
            string bpId = c.Get("bp.id", string.Empty);
            if (bpId.Length == 0)
            {
                c.Set("fix.err", "保存“重炮燃迹接入口”蓝图失败：" + c.Get("bp.note"));
                return;
            }
            var ids = new List<int>();
            var used = new List<GridCell>();
            for (int i = 0; i < SquadSize; i++)
            {
                GridCell at = default;
                for (int tr = i; tr < i + 40; tr++)
                {
                    Vector2 side = perp * ((tr % 4 - 1.5f) * 3f) + u * ((tr / 4) * 3f);
                    at = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(park.x + side.x), Mathf.RoundToInt(park.y + side.y)), 10);
                    if (!used.Any(x => Math.Abs(x.X - at.X) + Math.Abs(x.Y - at.Y) < 2))
                    {
                        break;
                    }
                }
                used.Add(at);
                MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bpId, HomeValleyLayout.RegionId, new Vector2(at.X, at.Y), SquadHp, SquadHp, "Player", 1);
                if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
                {
                    MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
                    ids.Add(r.LogicId);
                }
            }
            c.Set("roster", string.Join(",", ids.Select(x => x.ToString(CultureInfo.InvariantCulture))));

            string ov = PrimitiveInventory.GrantBossFirmware(s, "fgjm11:overload", FirmwareCatalog.FwOverloadId);
            SignalCoreResult eq = SignalCoreService.TryEquip(s, ov, 0);
            c.Set("fix.eq", eq.Success ? "ok" : eq.Message);
            c.Set("chip.mark", PrimitiveInventory.GrantBossFirmware(s, "fgjm11:marktag", FirmwareCatalog.FwMarkTagId) ?? string.Empty);
        }

        internal static BlueprintCircuitBoard WithUplink(BlueprintCircuitBoard board)
        {
            for (int slot = 1; slot < BlueprintCircuitLayout.SlotCount && !board.HasUplink; slot++)
            {
                board.TrySetUplink(slot);
            }
            return board;
        }

        private static StepOutcome TickFixture(JourneyContext c)
        {
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            if (c.Get("fix.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail("夹具：" + c.Get("fix.err"));
            }
            List<int> team = Team(c);
            bool inKernel = team.Count == SquadSize && team.All(id => Home != null && Home.TryGetMachineUnit(id, out _));
            Vector2 park = V(c, "park"), p1 = V(c, "p1"), p2 = V(c, "p2"), tp = TowerPos(c);
            bool covPark = SignalCoverageService.Sample(HomeValleyLayout.RegionId, park).Covered;
            bool covP1 = SignalCoverageService.Sample(HomeValleyLayout.RegionId, p1).Covered;
            bool covP2 = SignalCoverageService.Sample(HomeValleyLayout.RegionId, p2).Covered;
            bool radP1 = HeatRadiationService.ZoneAt(s, p1) != null && !CoolingTowerService.InActiveTower(s, p1);
            bool towerUnit = Home != null && Home.TryGetEnemyUnit(CoolingTowerService.KeyOf(TowerId(c)), out int tu) && Home.UnitExists(tu);
            bool overload = SignalCoreService.SlotContentId(s, 0) == FirmwareCatalog.FwOverloadId;
            PlannedTerritory t = Oc;
            string geo = $"热寂数据城 {t?.Id} 中心 ({t?.CenterX},{t?.CenterY}) 半径 {t?.Radius}、外圈辐射带宽 {t?.HazardWidth}、第 {t?.Act} 幕；冷却塔 {TowerId(c)} {Cell(tp)}（离核心 {F(Vector2.Distance(CorePos, tp), "0")} 格）；" +
                         $"停车 {Cell(park)}、辐射点 {Cell(p1)}、塔旁 {Cell(p2)}；中继链 {c.Get("fix.relays")}";
            c.Log(geo);
            if (!inKernel || !covPark || !covP1 || !covP2 || !radP1 || !towerUnit || !overload || c.Get("chip.mark", string.Empty).Length == 0)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait
                    : StepOutcome.Fail($"夹具后：远征队进内核 {inKernel}（{team.Count} 台）；覆盖 停车 {covPark} / 辐射点 {covP1} / 塔旁 {covP2}；辐射点在辐射区且不在塔圈 {radP1}；冷却塔内核单位 {towerUnit}；" +
                                       $"1 号槽过载 {overload}（{c.Get("fix.eq")}）；标记跳转芯片 {c.Get("chip.mark")}；{geo}");
            }
            return StepOutcome.Done($"夹具生效：第 {c.GetInt("act0")} → {s.Progress.Act} 幕；三领地平定；{SquadSize} 台重炮燃迹停在辐射带外；信号核 1 号槽 = 过载；{geo}");
        }

        // ── 1. 高温辐射与冷却塔 ─────────────────────────────────────────────────────

        private static StepOutcome RightClickGround(JourneyContext c, Vector2 at)
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
            // 走到一半被卡住 / 命令被教义打断：每 20 秒再右键一次同一处。
            if (!JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) > 20000 && FgjM3Common.TryClick(at, 1))
            {
                c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
                c.SetInt("reclicks", c.GetInt("reclicks") + 1);
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickHeatIn(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            Vector2 p1 = V(c, "p1");
            List<int> team = AliveTeam(c);
            int inRad = team.Count(id => HeatRadiationService.ZoneAt(s, PosOf(id)) != null && !CoolingTowerService.InActiveTower(s, PosOf(id)));
            bool near = team.Count > 0 && team.All(id => Vector2.Distance(PosOf(id), p1) < 10f);
            if (inRad == team.Count && near && c.GetInt(FgjM3Common.SK(c, "clicked")) == 1)
            {
                return StepOutcome.Done($"右键 {Cell(p1)}：{team.Count} 台都走进高温辐射区（不在塔圈里）；{TeamHp(c)}");
            }
            return RightClickGround(c, p1);
        }

        private static StepOutcome TickTowerGo(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            Vector2 p2 = V(c, "p2");
            List<int> team = AliveTeam(c);
            int inTower = team.Count(id => CoolingTowerService.InActiveTower(s, PosOf(id)));
            if (inTower == team.Count && team.Count > 0 && c.GetInt(FgjM3Common.SK(c, "clicked")) == 1)
            {
                return StepOutcome.Done($"右键塔旁 {Cell(p2)}：{team.Count} 台都在运转中的冷却塔范围里（{F(CoolingTowerService.Radius, "0")} 格）；{TeamHp(c)}");
            }
            return RightClickGround(c, p2);
        }

        private static StepOutcome TickBackPark(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            Vector2 park = V(c, "park");
            List<int> team = AliveTeam(c);
            if (team.Count > 0 && team.All(id => Vector2.Distance(PosOf(id), park) < 15f) && c.GetInt(FgjM3Common.SK(c, "clicked")) == 1)
            {
                return StepOutcome.Done($"右键停车处 {Cell(park)}：{team.Count} 台退出辐射带，离塔 {F(Vector2.Distance(park, TowerPos(c)), "0")} 格");
            }
            return RightClickGround(c, park);
        }

        private static StepOutcome TickUplinkPlain(JourneyContext c)
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
            return c.StepElapsed < 1.5 ? StepOutcome.Wait : StepOutcome.Done($"接入 {Label(b)}（{Cell(PosOf(b))}）");
        }

        private static string HudHeatText() => JourneyInput.FindUitk<Label>("[SignalCoreHost]", "UplinkHudHeatText")?.text ?? string.Empty;

        private static bool TowerCircleOnMap(JourneyContext c)
        {
            Vector2 tp = TowerPos(c);
            var model = new WorldMapModel();
            var view = new WorldMapView { CenterX = (int)tp.x, CenterY = (int)tp.y, HalfWidth = 600, CanvasWidth = 900, CanvasHeight = 700 };
            model.Collect(St, view, 200);
            return model.Circles.Any(x => x.HazardId == "cooling_tower" && Math.Abs(x.X - tp.x) < 0.5f && Math.Abs(x.Y - tp.y) < 0.5f);
        }

        /// <summary>接入第一台存活机器：屏幕上的接入 HUD 热量一栏（UplinkHudHeatText 的真实文字）写辐射扣减 / 冷却塔恢复，且与内核热量上限同一口径。</summary>
        private static StepOutcome TickUplinkHeat(JourneyContext c, bool wantRestored)
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
            if (c.StepElapsed < 2.0)
            {
                return StepOutcome.Wait; // HUD 按整帧重建
            }
            CampaignState s = St;
            string text = HudHeatText();
            string restored = GameText.Get("cooling.hud.restored");
            string radiation = GameText.Format("cooling.hud.radiation", Mathf.RoundToInt((1f - HeatRadiationService.CapMult) * 100f).ToString());
            var snap = new UplinkHudSnapshot();
            UplinkHudModel.BuildVitals(s, b, snap);
            bool ok = wantRestored ? text.Contains(restored) && !text.Contains(radiation) : text.Contains(radiation) && !text.Contains(restored);
            if (!ok)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait
                    : StepOutcome.Fail($"接入 {Label(b)}（{Cell(PosOf(b))}）后 HUD 热量一栏“{text}”，应写“{(wantRestored ? restored : radiation)}”（HUD 模型 {snap.HeatCapNote}、热量上限 {F(snap.HeatMax)}）");
            }
            if (wantRestored)
            {
                c.Set("heat.max.tower", snap.HeatMax.ToString("R", CultureInfo.InvariantCulture));
                bool circle = TowerCircleOnMap(c);
                bool capUp = snap.HeatMax > GetF(c, "heat.max.rad") + 0.5f;
                if (!circle || !capUp)
                {
                    return StepOutcome.Fail($"塔旁 HUD 写恢复了，但地图范围圈 {circle}、热量上限 {F(GetF(c, "heat.max.rad"))} → {F(snap.HeatMax)}（应升回 100%）");
                }
                return StepOutcome.Done($"接入 {Label(b)}：HUD 热量一栏“{text}”；热量上限 {F(GetF(c, "heat.max.rad"))} → {F(snap.HeatMax)}；地图上有冷却塔 {TowerId(c)} 的蓝色范围圈");
            }
            if (c.Get("heat.max.rad", string.Empty).Length == 0)
            {
                c.Set("heat.max.rad", snap.HeatMax.ToString("R", CultureInfo.InvariantCulture));
            }
            bool after = c.Get("tower.down", string.Empty).Length > 0;
            if (after && TowerCircleOnMap(c))
            {
                return StepOutcome.Fail("冷却塔被摧毁后地图上还有它的范围圈");
            }
            return StepOutcome.Done($"接入 {Label(b)}（{Cell(PosOf(b))}）：HUD 热量一栏“{text}”；热量上限 {F(snap.HeatMax)}（×{F(HeatRadiationService.CapMult, "0.##")}）" +
                                    (after ? $"——站在已摧毁的塔 {TowerId(c)} 旁，上限回落，地图范围圈已消失" : string.Empty));
        }

        private static float GetF(JourneyContext c, string key) =>
            float.TryParse(c.Get(key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

        // ── 2. 镜像体 ───────────────────────────────────────────────────────────────

        /// <summary>
        /// 事件夹具（DEBT-FG11E2E01-02）：冷却塔另一侧（离塔心 3.5 格）一台算力节点、旁边一台镜像体，正式敌人表 + EnemySpawnBuilder 真实生成（武器 / 行为 / 镜像读法都按表）。
        /// 按种子生成的超频据点离覆盖远、驻军多，旅程只要“遭遇镜像体”这一段；据点醒来 / 展开由 FGJ-M10 覆盖。
        /// </summary>
        private static void ApplyGarrison(JourneyContext c)
        {
            CampaignState s = St;
            CombatSite site = Home;
            if (s == null || site == null || c.GetInt("gar.node") > 0)
            {
                return;
            }
            Vector2 tp = TowerPos(c);
            Vector2 u = (CorePos - tp).normalized;
            Vector2 perp = new Vector2(-u.y, u.x);
            c.SetInt("gar.node", Spawn(c, s, site, "enemy_oc_node", NodeId, tp - u * 3.5f));
            c.SetInt("gar.mirror", Spawn(c, s, site, "enemy_oc_mirror", MirrorId, tp - u * 3.5f + perp * 4f));
            // 五座速射炮台贴着塔身（离塔心 2 格，四面 + 斜前方）：最后打它们，燃迹的明火落在它脚下、压到塔身（FG-GAP-238：结构单位按占地接触），把塔一起烧掉。
            // 三座时实测塔只剩 60/400（run14），不赌临界结果：驻军夹具（DEBT-FG11E2E01-02）多放两座，明火总量约 1.6 倍
            c.SetInt("gar.turret", Spawn(c, s, site, "enemy_oc_turret", TurretId, tp - u * 2f));
            c.SetInt("gar.turret2", Spawn(c, s, site, "enemy_oc_turret", TurretId + "2", tp + perp * 2f));
            c.SetInt("gar.turret3", Spawn(c, s, site, "enemy_oc_turret", TurretId + "3", tp - perp * 2f));
            c.SetInt("gar.turret4", Spawn(c, s, site, "enemy_oc_turret", TurretId + "4", tp + u * 2f));
            c.SetInt("gar.turret5", Spawn(c, s, site, "enemy_oc_turret", TurretId + "5", tp - (u + perp).normalized * 2f));
            c.SetInt("copies0", CombatSite.MirrorDetectCopies);
            c.SetInt("mirrorNotes0", NotifyCount("overclock_mirror"));
            c.SetInt("core0", SignalUplinkService.CoreFiredCount);
        }

        private static int Spawn(JourneyContext c, CampaignState s, CombatSite site, string type, string id, Vector2 at)
        {
            GameConfig.fg.MechEnemy row = EnemyDefs.Get(type);
            GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(at.x), Mathf.RoundToInt(at.y)), 4);
            var rec = new RegionEnemyRecord
            {
                EnemyInstanceId = id, RegionId = HomeValleyLayout.RegionId, EnemyTypeId = type, Position = new Vector2(g.X, g.Y), Health = row.MaxHp, MaxHealth = row.MaxHp, IsAlive = true,
            };
            s.RegionEnemies = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Where(e => e.EnemyInstanceId != id).Append(rec).ToArray();
            if (!EnemySpawnBuilder.TryBuild(site, s, rec, RegionEnemySites.For(HomeValleyLayout.RegionId), out CombatSpawn sp))
            {
                c.Set("gar.err", type + " 按敌人表生成失败");
                return 0;
            }
            return site.SpawnEnemy(rec, sp);
        }

        private static StepOutcome TickGarrison(JourneyContext c)
        {
            if (c.Get("gar.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail("夹具：" + c.Get("gar.err"));
            }
            CombatSite site = Home;
            int node = c.GetInt("gar.node"), mirror = c.GetInt("gar.mirror");
            bool alive = site != null && site.Kernel.IsAlive(node) && site.Kernel.IsAlive(mirror);
            string hover = site?.MirrorHoverLine(St, mirror) ?? string.Empty;
            if (!alive || hover != GameText.Get("overclock.mirror.hover_none"))
            {
                return c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Fail($"驻军没生成 / 镜像体读法不对（节点 {node}、镜像体 {mirror} 存活 {alive}；悬停“{hover}”）");
            }
            site.Kernel.TryGetPosition(mirror, out double2 mp);
            return StepOutcome.Done($"超频驻军：算力节点 #{node}、镜像体 #{mirror} @ ({F(mp.x, "0")},{F(mp.y, "0")})，悬停“{hover}”");
        }

        /// <summary>接入中左键点算力节点开火（每 1.6 秒一次）：熔穿过载发动 → 镜像体复制（共用计数 MirrorDetectCopies）。</summary>
        private static StepOutcome TickMirrorCopy(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            CombatSite site = Home;
            int me = AliveTeam(c).FirstOrDefault();
            int node = c.GetInt("gar.node"), mirror = c.GetInt("gar.mirror");
            if (SignalPresence.CurrentMachineLogicId != me || WorldView.Director.Mode != ViewMode.Direct)
            {
                return StepOutcome.Fail($"信号不在 {Label(me)} 里（{SignalPresence.CurrentMachineLogicId}，镜头 {WorldView.Director.Mode}）");
            }
            int copies = CombatSite.MirrorDetectCopies - c.GetInt("copies0");
            int fired = SignalUplinkService.CoreFiredCount - c.GetInt("core0");
            if (copies > 0)
            {
                RegionEnemyRecord rec = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).FirstOrDefault(e => e.EnemyInstanceId == MirrorId);
                string hover = site.MirrorHoverLine(s, mirror) ?? string.Empty;
                string fwName = FirmwareKinds.DisplayName(rec?.MirrorCopyFirmware ?? string.Empty) ?? rec?.MirrorCopyFirmware;
                int notes = NotifyCount("overclock_mirror") - c.GetInt("mirrorNotes0");
                if (rec == null || rec.MirrorCopyFirmware != FirmwareCatalog.FwOverloadId || !site.UnitHoldsCopy(mirror) || notes <= 0 || !hover.Contains(fwName ?? "?") || fired <= 0)
                {
                    return StepOutcome.Fail($"镜像复制计数 +{copies}，但复制品 {rec?.MirrorCopyFirmware}（应为过载）、手上有复制品 {site.UnitHoldsCopy(mirror)}、复制通知 +{notes}、悬停“{hover}”、核心固件发动 +{fired}");
                }
                c.Set("mirror.hover", hover);
                return StepOutcome.Done($"{Label(me)} 打出熔穿过载（核心固件发动 +{fired}）：镜像体复制了它——共用计数 MirrorDetectCopies +{copies}，复制通知“{Short(LastNotify("overclock_mirror"))}”，悬停“{hover}”（{rec.MirrorCopyHeld} 条）");
            }
            if (!site.Kernel.IsAlive(node) && !site.Kernel.IsAlive(mirror))
            {
                return StepOutcome.Fail($"驻军已经被打光，镜像体还没复制到核心固件（核心固件发动 +{fired}）");
            }
            if (!site.Kernel.IsAlive(node))
            {
                node = mirror; // 节点先倒了：接着打镜像体本身（它就在旁边，同样会侦测到发动）
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "fireAt")) < 1600)
            {
                return StepOutcome.Wait;
            }
            site.Kernel.TryGetPosition(node, out double2 np);
            var nodeAt = new Vector2((float)np.x, (float)np.y);
            Vector2 mePos = PosOf(me);
            if (Vector2.Distance(mePos, nodeAt) > 10f)
            {
                Vector2 uu = (CorePos - TowerPos(c)).normalized;
                Vector2 side = new Vector2(-uu.y, uu.x) * 8f; // 节点在塔的另一侧：从侧面绕过去（直线开会顶在塔身上，塔是结构单位）
                Vector2 goal = Vector2.Distance(mePos, nodeAt + side) <= Vector2.Distance(mePos, nodeAt - side) ? nodeAt + side : nodeAt - side;
                FgjM1Journey.DriveToward(mePos, goal, 0.4);
                if ((int)(c.StepElapsed / 5) > c.GetInt(FgjM3Common.SK(c, "dlog")))
                {
                    c.SetInt(FgjM3Common.SK(c, "dlog"), (int)(c.StepElapsed / 5));
                    c.Log($"[{F(c.StepElapsed, "0")}s] 开向节点：{Label(me)} @ {Cell(mePos)}，离节点 {F(Vector2.Distance(mePos, nodeAt), "0.0")} 格；镜头 {WorldView.Director.Mode}；信号在 {SignalPresence.CurrentMachineLogicId}；按住 {JourneyInput.Holding}");
                }
                return StepOutcome.Wait;
            }
            JourneyInput.Click(new Vector2((float)np.x, (float)np.y), 0);
            c.SetLong(FgjM3Common.SK(c, "fireAt"), NowMs());
            c.SetInt("mirror.clicks", c.GetInt("mirror.clicks") + 1);
            if (c.GetInt("mirror.clicks") % 5 == 0)
            {
                c.Log($"[{F(c.StepElapsed, "0")}s] 点节点开火 {c.GetInt("mirror.clicks")} 次；核心固件发动 +{fired}；{Label(me)} @ {Cell(PosOf(me))} → 节点 ({F(np.x, "0")},{F(np.y, "0")})；{SignalUplinkService.LastFeedbackText}");
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickGarrisonFight(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CombatSite site = Home;
            int node = c.GetInt("gar.node"), mirror = c.GetInt("gar.mirror");
            if (AliveTeam(c).Count == 0)
            {
                return StepOutcome.Fail("远征队全灭");
            }
            int turret = new[] { c.GetInt("gar.turret"), c.GetInt("gar.turret2"), c.GetInt("gar.turret3"), c.GetInt("gar.turret4"), c.GetInt("gar.turret5") }.FirstOrDefault(x => x > 0 && site.Kernel.IsAlive(x));
            bool nodeAlive = site.Kernel.IsAlive(node), mirrorAlive = site.Kernel.IsAlive(mirror), turretAlive = turret > 0;
            if ((int)(c.StepElapsed / 5) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 5));
                c.Log($"[{F(c.StepElapsed, "0")}s] 镜像体 {mirrorAlive}、节点 {nodeAlive}；冷却塔 {TowerHp(c)}；{TeamHp(c)}；右键 {c.GetInt("gar.rc")} 次");
            }
            if (!nodeAlive && !mirrorAlive && !turretAlive)
            {
                return StepOutcome.Done($"右键攻击 {c.GetInt("gar.rc")} 次：镜像体与算力节点都被击毁；冷却塔 {TowerHp(c)}；{TeamHp(c)}");
            }
            if (!JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) > 4000)
            {
                int target = mirrorAlive ? mirror : nodeAlive ? node : turret;
                if (site.Kernel.TryGetPosition(target, out double2 p) && FgjM3Common.TryClick(new Vector2((float)p.x, (float)p.y), 1))
                {
                    c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                    c.SetInt("gar.rc", c.GetInt("gar.rc") + 1);
                }
            }
            return StepOutcome.Wait;
        }

        private static string TowerHp(JourneyContext c)
        {
            if (CoolingTowerService.IsDestroyed(St, TowerId(c)))
            {
                return "已摧毁";
            }
            CombatSite site = Home;
            return site != null && site.TryGetEnemyUnit(CoolingTowerService.KeyOf(TowerId(c)), out int u) && site.Kernel.TryGetUnit(u, out CombatUnitView v) ? "耐久 " + F(v.Health, "0") : "?";
        }

        private static float TowerHpValue(JourneyContext c)
        {
            CombatSite site = Home;
            return site != null && site.TryGetEnemyUnit(CoolingTowerService.KeyOf(TowerId(c)), out int u) && site.Kernel.TryGetUnit(u, out CombatUnitView v) ? v.Health : 0f;
        }

        private static StepOutcome TickTowerAttack(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (CoolingTowerService.IsDestroyed(St, TowerId(c)))
            {
                return StepOutcome.Done(c.GetInt(FgjM3Common.SK(c, "clicked")) == 1 ? "攻击命令下达后冷却塔被打空" : "驻军战里燃迹的明火已把冷却塔一起烧掉（波及），不用再下攻击命令");
            }
            if (c.StepElapsed < 0.4)
            {
                return StepOutcome.Wait;
            }
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 0)
            {
                if (!FgjM3Common.TryClick(TowerPos(c), 0))
                {
                    return StepOutcome.Wait;
                }
                c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
                c.Set("tw.hp0", F(TowerHpValue(c), "0.##"));
                return StepOutcome.Wait;
            }
            float hp0 = GetF(c, "tw.hp0"), hp = TowerHpValue(c);
            if (hp < hp0 - 1f)
            {
                return StepOutcome.Done($"攻击键 + 左键塔身：远征队攻击冷却塔遗址，耐久 {F(hp0, "0")} → {F(hp, "0")}；{TeamHp(c)}");
            }
            return c.StepElapsed < 25 ? StepOutcome.Wait : StepOutcome.Retry($"左键塔身后 25 秒冷却塔耐久没掉（{F(hp0, "0")} → {F(hp, "0")}）；{Squad?.RecentEvents?.LastOrDefault()}");
        }

        private static StepOutcome TickTowerDown(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.GetInt("notes0set") == 0)
            {
                c.SetInt("notes0set", 1);
            }
            if (!CoolingTowerService.IsDestroyed(St, TowerId(c)))
            {
                if ((int)(c.StepElapsed / 10) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 10));
                    c.Log($"[{F(c.StepElapsed, "0")}s] 冷却塔 {TowerHp(c)}");
                }
                return StepOutcome.Wait;
            }
            string note = LastNotify("heat_radiation");
            string want = GameText.Format("cooling.notify.destroyed", string.Empty).Substring(0, 4);
            bool circle = TowerCircleOnMap(c);
            if (!note.Contains(want) || circle || CoolingTowerService.InActiveTower(St, TowerPos(c)))
            {
                return StepOutcome.Fail($"冷却塔记为摧毁，但通知“{note}”、地图范围圈 {circle}、塔心仍在运转中的塔圈 {CoolingTowerService.InActiveTower(St, TowerPos(c))}");
            }
            c.Set("tower.down", "1");
            return StepOutcome.Done($"冷却塔遗址 {TowerId(c)} 被摧毁：通知“{note}”；地图范围圈消失；塔心不再在任何运转中的塔圈里");
        }
    }
}
