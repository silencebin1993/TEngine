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
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using GameLogic.View;
using Unity.Mathematics;
using UnityEngine;
using D = GameLogic.EditorTools.JourneyBots.FgjM6Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// EMG2-E2E-01：FG-EMG 阶段 2 出口旅程 FGJ-EMG-2（FG19 第 16 节；LANDING-PLAN 6.3）。从主菜单“新建”出发（固定测试种子 42、标准难度），全程正式输入，
    /// RTS 口径：左键选择、右键敌人 = 攻击、右键地面 = 移动；直控左键开火。
    ///
    /// 原文：A 车铺油、B 车点火形成燃烧区 → 冷却液水洼通电，敌人连锁被电，己方误入时出现友伤通知 → 冻结敌人后重击碎裂成破片 → 第一次触发时“新发现”提示与图鉴解锁 →
    /// 家园突袭后残骸与冷渣留在防线前，下次突袭仍在。另测 FG-GAP-172：无人机舱机器先放出无人机、再被接入，已在场的无人机下一次开火就换成接入程序。
    ///
    /// 轻量口径（用户 2026-10-12）：世界层每个新系统被真实输入触发一次并断言结果。夹具写在步骤标题里：<see cref="ApplyFixture"/>（固件记为已获得，先断言正常游戏有来源）、
    /// <see cref="ApplySquadFixture"/>（蓝图与 7 台机器直接出厂（编辑器与出厂由 FGJ-EMG-1 / FGJ-M1 覆盖）+ 4 处不动不还手的敌方靶子，规则结果全走内核）、
    /// 两波突袭走剧情入口并快进预警（M6R / M10 同一做法；之后的预警、行进、攻城、结算全是正式流程）。
    /// 用法：bash tools/unity-journey.sh FGJ-EMG-2；断点 emg_fix / squad_fix / burn / shock / shatter / drone / raid1_end（--from 续跑）。
    /// </summary>
    public static class FgjEmg2Journey
    {
        public const string Id = "FGJ-EMG-2";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private const string Oil = "fw_oilleak", Fire = "fw_burntrail", Coolant = "fw_coolant", Arc = "fw_arcchain", Nitrogen = "fw_nitrogen", Overload = "fw_overload", Split = "fw_split", Pierce = "fw_pierce";
        private const string Deflag = "reaction_deflagrate", Conduct = "reaction_conduct", Shatter = "reaction_shatter", Melt = "reaction_meltoverload";

        /// <summary>夹具机器耐久（标准 100）：7 台机器要扛一波攻城打完，本旅程不验平衡（FG15-BAL-01）。</summary>
        private const float SquadHp = 500f;

        private static readonly string[] Team = { "m.a", "m.b", "m.c", "m.d", "m.e", "m.f", "m.h", "m.g" };

        private static CampaignState St => CampaignSession.Current;

        private static CombatSite Home => GameRoot.HomeValley?.Combat;

        public static JourneyDef Build()
        {
            var steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, TickPlay),
                S("menu_new", "主菜单点“新建”", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "存档槽 → 新游戏设置（种子 = 固定测试种子 42，难度标准）→ 点“开始” → 进入归还谷地", 150, null, JourneyCommon.TickNewGame),
                S("seed", "种子进了存档；生成结果与该种子的基准一致", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("emg_fix", "进度夹具：正式表 emg.stage = 2（阶段 2 生效）；旅程用到的固件记为已获得（先断言每条在正常游戏里都有来源）", 15, ApplyFixture, TickFixture),

                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("sel_worker", "左键点一台工程机", 15, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 2),
                S("repair_gen", "右键点受损的发电机（刻印芯片要装配站有电）", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("wait_gen", "等发电机修好（3 倍速）", 300, null, FgjEmg1Journey.TickGeneratorDone),
                S("sc_open", "按信号核键（默认 P）打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
                S("sc_print", "刻印下拉选“过载”，点“刻印”", 10, c => FgjEmg1Journey.PrintFirmware(c, Overload), c => FgjEmg1Journey.TickPrinted(c, Overload), retries: 1),
                S("sc_equip", "点“装入 1 号槽”", 10, c => FgjM1Journey.ClickUi(c, "[SignalCoreHost]", "SignalEquip"), c => FgjEmg1Journey.TickEquipped(c, Overload), retries: 1),
                S("sc_close", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),

                S("squad_fix", "出厂夹具：蓝图 A 漏油 / B 燃迹 / C 冷却液 / D 电弧 / E 无固件 / F 液氮 / H 重炮带接入口 / G 无人机舱带接入口各出厂一台（出厂由 FGJ-M1 覆盖）；覆盖里 4 处开阔地各放不动不还手的敌方靶子", 20, ApplySquadFixture, TickSquadFixture),

                // ── 1. A 车铺油、B 车点火 → 燃烧区；新发现 + 图鉴 ──
                S("sel_a", "左键点 A 车（漏油）", 15, c => FgjM1Journey.ClickMachine(c, "m.a"), c => FgjM1Journey.TickSelected(c, "m.a"), retries: 2),
                S("oil", "右键靶子 1（攻击）：弹体终结处留下油斑块", 120, null, TickOil),
                S("sel_b", "左键点 B 车（燃迹）", 15, c => FgjM1Journey.ClickMachine(c, "m.b"), c => FgjM1Journey.TickSelected(c, "m.b"), retries: 2),
                S("burn", "右键靶子 1（攻击）：B 车的明火点燃 A 车的油 → 爆燃（E01）形成燃烧区，斑块归属写着两台车", 120, null, TickBurn),
                S("discover", "第一次爆燃：信息级可定位“新发现”通知报名字；图鉴条目解锁；首次记录写进存档", 10, null, c => TickDiscovered(c, Deflag, "first")),
                S("codex_open", "按图鉴键（默认 C）打开图鉴", 10, c => FgjM1Journey.PressIf(GameActionId.OpenCodex, !MechanicCodexPanelUIToolkit.IsOpen), FgjM2Common.TickCodexOpen, retries: 1),
                S("codex_close", "再按图鉴键关闭图鉴", 10, c => FgjM1Journey.PressIf(GameActionId.OpenCodex, MechanicCodexPanelUIToolkit.IsOpen),
                    c => c.StepElapsed < 0.6 ? StepOutcome.Wait : MechanicCodexPanelUIToolkit.IsOpen ? StepOutcome.Retry("图鉴还开着") : StepOutcome.Done("图鉴关闭"), retries: 1),

                // ── 2. 冷却液水洼通电：敌人连锁被电，己方误入 → 友伤通知 ──
                S("sel_c", "左键点 C 车（冷却液）", 15, c => FgjM1Journey.ClickMachine(c, "m.c"), c => FgjM1Journey.TickSelected(c, "m.c"), retries: 2),
                S("puddle", "右键靶子 2（攻击）：命中处排出冷却液水洼，盖住挤在一起的 3 个靶子", 120, null, TickPuddle),
                S("sel_d", "左键点 D 车（电弧）", 15, c => FgjM1Journey.ClickMachine(c, "m.d"), c => FgjM1Journey.TickSelected(c, "m.d"), retries: 2),
                S("shock", "右键靶子 2（攻击）：电弧注入水洼 → 通电水洼（E15）把水洼里至少 2 个靶子连锁电上", 120, null, TickShock),
                S("sel_e", "左键点 E 车（没有固件，没人向它下令攻击）", 15, c => FgjM1Journey.ClickMachine(c, "m.e"), c => FgjM1Journey.TickSelected(c, "m.e"), retries: 2),
                S("friendly", "右键水洼对面的地面（移动）：E 车穿过通电水洼挨电 → 警告级可定位“友伤”通知写明谁电到谁", 90, null, TickFriendly),

                // ── 3. 冻结敌人后重击碎裂成破片 ──
                S("sel_r", "左键点 R 车（维修束）", 15, c => FgjM1Journey.ClickMachine(c, "m.r"), c => FgjM1Journey.TickSelected(c, "m.r"), retries: 4),
                S("repair", "右键受伤的 E 车（FG-GAP-175）：R 车开过去修，E 车耐久回升", 90, null, TickRepair),
                S("sel_f", "左键点 F 车（液氮）", 15, c => FgjM1Journey.ClickMachine(c, "m.f"), c => FgjM1Journey.TickSelected(c, "m.f"), retries: 2),
                S("freeze", "右键靶子 3（攻击）：命中处排液氮，靶子寒量攒满进入冻结相态", 120, null, TickFreeze),
                S("sel_h", "左键点 H 车（重炮）", 15, c => FgjM1Journey.ClickMachine(c, "m.h"), c => FgjM1Journey.TickSelected(c, "m.h"), retries: 2),
                S("shatter", "右键靶子 3（攻击）：重炮命中冻结的靶子 → 碎裂（P01）崩出破片、退出冻结", 150, null, TickShatter),
                S("discover2", "碎裂也是第一次：同样有“新发现”通知、图鉴解锁", 10, null, c => TickDiscovered(c, Shatter, "second")),

                // ── 4. FG-GAP-172：无人机舱先放无人机再接入 ──
                S("sel_g", "左键点 G 车（无人机舱）", 15, c => FgjM1Journey.ClickMachine(c, "m.g"), c => FgjM1Journey.TickSelected(c, "m.g"), retries: 2),
                S("dr_speed1", "点世界栏 1 倍速（无人机只活 8 游戏秒，接入要在这之内开火）", 10, c => ClickSpeed("WorldSpeed1"), c => TickSpeed(c, 1f), retries: 1),
                S("drones", "右键靶子 4（攻击）：放出无人机，按 AI 程序开火", 90, null, TickDrones),
                S("dr_up", "命令栏机器列表点 G 车：接入（镜头直控），武器换成带过载的接入程序", 30, c => FgjM1Journey.ClickMachineList(c, c.GetInt("m.g")), c => TickUplinked(c, "m.g"), retries: 3),
                S("dr_fire", "直控左键点靶子 4 开火：已在场的无人机被这次开火续命时换成接入程序（FG-GAP-172）", 45, null, TickDroneRefresh),
                S("dr_leave", "按接入 / 退出接入键（默认 M）离开；无人机按 FGR-EMG-037 剥离核心", 15, c => JourneyInput.PressAction(GameActionId.ToggleCameraView), TickDroneLeft, retries: 2),

                // ── 5. 家园突袭：残骸与冷渣留在防线前，下次突袭仍在 ──
                S("dr_speed3", "点世界栏 3 倍速", 10, c => ClickSpeed("WorldSpeed3"), c => TickSpeed(c, 3f), retries: 1),
                S("raid1_fix", "进度夹具：靶子清场；剧情节点触发一波突袭（M6R 同一入口），预警快进", 15, c => RequestRaid(c, "r1"), c => TickRaidRequested(c, "r1")),
                JourneyCommon.PureWait(S("raid1_warn", "突袭预警：预警条写明抵达点（到达时自动暂停就按暂停键继续）", 300, null, c => TickWarned(c, "r1"))),
                S("team", "左键点一台、Shift 左键加选其余几台", 60, null, TickSelectTeam, retries: 2),
                S("rally", "右键抵达点靠核心一侧的地面：全队开到防线", 15, null, TickRally),
                S("raid1_up", "命令栏机器列表点 H 车：接入（镜头直控），信号核里的过载生效", 30, c => FgjM1Journey.ClickMachineList(c, c.GetInt("m.h")), c => TickUplinked(c, "m.h"), retries: 3),
                S("raid1_arrive", "突袭部队到达、展开攻城", 400, null, c => TickSieging(c, "r1")),
                S("raid1_fight", "直控开到射程内左键点敌人开火：重炮 + 过载熔穿（P03）溅出熔融金属；敌人被击毁留下残骸；打完 / 到时撤退后熔融金属凝成冷渣", 600, null, TickRaidFight),
                S("raid1_end", "突袭结束（撤走的敌人路过时还在开火，余波算在这一步）：防线前有残骸、有冷渣；记下编号", 60, c => { if (SignalPresence.CurrentMachineLogicId != 0) JourneyInput.PressAction(GameActionId.ToggleCameraView); }, TickRaidEnd),

                S("raid2_fix", "进度夹具：再触发一波突袭（剧情入口），预警快进", 15, c => RequestRaid(c, "r2"), c => TickRaidRequested(c, "r2")),
                JourneyCommon.PureWait(S("raid2_arrive", "下一波部队到达、展开攻城：结算基线里上次的残骸与冷渣仍在，同样的编号还在内核里（FGR-EMG-150）", 500, null, TickSecondRaid)),
            };
            return new JourneyDef
            {
                Id = Id,
                Title = "FG-EMG 阶段 2 出口：A 车铺油、B 车点火成燃烧区 → 通电水洼连锁、己方误入友伤通知 → 冻结后重击碎裂 → 新发现与图鉴 → 突袭后残骸与冷渣留在防线前、下次突袭仍在",
                Seed = TestSeed,
                TotalTimeoutSeconds = 1500,
                OnFinish = Cleanup,
                Version = 1,
                CheckpointAfter = new[] { "emg_fix", "squad_fix", "burn", "shock", "shatter", "dr_leave", "raid1_end" },
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static string F(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);

        private static string Label(int id) => FgjM1Journey.Label(id);

        private static long NowMs() => FgjM2Common.NowMs();

        private static string GetV(Vector2 p) => F(p.x, "0.##") + "," + F(p.y, "0.##");

        private static Vector2 ParseV(string v)
        {
            string[] f = v.Split(',');
            return new Vector2(float.Parse(f[0], CultureInfo.InvariantCulture), float.Parse(f[1], CultureInfo.InvariantCulture));
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
            CombatSite site = Home;
            if (site?.Kernel != null)
            {
                CombatKernel k = site.Kernel;
                c.Log($"阶段 2 计数（家园内核全程）：爆燃 {Count(k, Deflag)}、通电水洼 {Count(k, Conduct)}、碎裂 {Count(k, Shatter)}、熔穿 {Count(k, Melt)}；活斑块 {k.PatchCount}（表长 {k.PatchSlots}）、残骸 {k.WreckCount}、破片累计 {k.EmgBusStats.FragSpawned}");
            }
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM3Common.FrameReport("全程"));
            JourneyCommon.Cleanup(c);
        }

        private static int Count(CombatKernel k, string reaction) => k.EmgRuleCountOfKey(NamedReactionCatalog.StableKey(reaction));

        private static CombatZone? PatchNear(CombatKernel k, byte matter, Vector2 at, float radius, int ruleKey = 0)
        {
            var p = new double2(at.x, at.y);
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter == matter && (ruleKey == 0 || z.RuleKey == ruleKey) && math.distance(z.Pos, p) <= radius + z.Radius)
                {
                    return z;
                }
            }
            return null;
        }

        // ── 夹具 ────────────────────────────────────────────────────────────────

        private static void ApplyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("fix.src", string.Empty).Length > 0)
            {
                return;
            }
            string[] ids = { Oil, Fire, Coolant, Arc, Nitrogen, Overload, Split, Pierce, ComponentCatalog.CompCannonId, ComponentCatalog.CompDroneBayId };
            var src = new List<string>();
            var missing = new List<string>();
            foreach (string fw in ids)
            {
                string where = FgjEmg1Journey.SourcesOf(s, fw, out bool obtainable);
                src.Add($"{fw}：{where}；可获得 {obtainable}");
                if (where == "无" || !obtainable)
                {
                    missing.Add(fw);
                }
            }
            c.Set("fix.src", string.Join("；", src));
            c.Set("fix.missing", string.Join(",", missing));
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(ids).Distinct().ToArray();
            MachineRecord worker = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && !m.IsInFactory && m.RegionId == HomeValleyLayout.RegionId)
                .OrderBy(m => m.ChassisId == HomeValleyLayout.Erc001ChassisId ? 0 : 1).ThenBy(m => m.LogicId).FirstOrDefault();
            c.SetInt("workerA", worker?.LogicId ?? 0);
            s.Scrap = Math.Max(s.Scrap, SignalCoreService.FirmwareChipPrintScrap * 2);
        }

        private static StepOutcome TickFixture(JourneyContext c)
        {
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            if (c.Get("fix.missing", string.Empty).Length > 0)
            {
                return StepOutcome.Fail($"有固件在正常游戏里没有来源：{c.Get("fix.missing")}（{c.Get("fix.src")}）");
            }
            EmgCompiled cat = EmgCatalog.Current;
            return cat.RawStage >= 2f && cat.EffectiveStage >= 2 // EMG3-CHS-01 起正式表缺省 3（阶段 2 机制照常生效）
                ? StepOutcome.Done($"正式表 emg.stage = {cat.RawStage}（生效 {cat.EffectiveStage} ≥ 2）；来源：{c.Get("fix.src")}")
                : StepOutcome.Fail($"正式表 emg.stage = {cat.RawStage}（生效 {cat.EffectiveStage}），阶段 2 出口应为 2");
        }

        private static bool OpenDisk(CampaignState s, Vector2 p, int r)
        {
            for (int x = -r; x <= r; x++)
            {
                for (int y = -r; y <= r; y++)
                {
                    int cx = Mathf.RoundToInt(p.x) + x, cy = Mathf.RoundToInt(p.y) + y;
                    if (!GameLogic.Campaign.Nav.NavService.PassableNow(cx, cy, BinGames.Sim.Nav.NavConst.ClassPlayer) || HomeGridService.BuildingAt(s, new GridCell(cx, cy)) != null)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static int SpawnTarget(CombatKernel k, Vector2 p) => k.Spawn(new CombatSpawn
        {
            ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled,
            Position = new double2(p.x, p.y), Home = new double2(p.x, p.y), Radius = 0.5f, Speed = 0f, Health = 8000f, MaxHealth = 8000f,
            Weapon = -1, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFraction = 0f, ArmorFacing = new float2(0f, -1f),
        });

        private static BlueprintRecord AddBlueprint(CampaignState s, string id, string primary, string[] firmware, bool uplink)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, primary, null, null, firmware);
            if (uplink && !Enumerable.Range(0, 9).Any(i => board.TrySetUplink(i).Success))
            {
                throw new InvalidOperationException($"夹具：{id} 没有能标接入口的格子");
            }
            var rec = new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { board.ToVersion(1, 0f) } };
            s.BlueprintRecords = s.BlueprintRecords.Where(b => b.BlueprintId != id).Append(rec).ToArray();
            return rec;
        }

        /// <summary>
        /// 找 4 处开阔地（覆盖里、互相 ≥ 36 格：物质互不串），靶子放圆心；机器出厂在圆心朝核心 24 格（迎战半径 18 格外，要右键才开打），左右错开。
        /// 世界按种子生成，位置都是现算（B25）。靶子不动不还手：只测世界规则，还手由 raid1 那一波覆盖。
        /// </summary>
        private static void ApplySquadFixture(JourneyContext c)
        {
            CampaignState s = St;
            CombatSite site = Home;
            if (s == null || site == null || c.GetInt("m.a") > 0)
            {
                return;
            }
            Vector2 core = FgjM8Journey.CorePos;
            const float standoff = 24f; // 迎战半径 18 格外：机器不会没接到命令就自己打起来
            var arenas = new List<(Vector2 center, Vector2 toCore)>();
            var why = new List<string>();
            for (int radius = 36; radius <= 120 && arenas.Count < 4; radius += 8)
            {
                for (int deg = 0; deg < 360 && arenas.Count < 4; deg += 15)
                {
                    var dir = new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad));
                    Vector2 p = core + dir * radius;
                    p = new Vector2(Mathf.Round(p.x), Mathf.Round(p.y));
                    // 第 4 处（接入直控开火）：机器在靶子正南，直控镜头里靶子在机器上方，不被底部指挥栏挡住。
                    Vector2 toCore = arenas.Count == 3 ? Vector2.down : (core - p).normalized;
                    Vector2 mp = p + toCore * standoff;
                    string bad = arenas.Any(a => Vector2.Distance(a.center, p) < 36f || Vector2.Distance(a.center, mp) < standoff - 2f || Vector2.Distance(a.center + a.toCore * standoff, p) < standoff - 2f) ? "离别的靶场太近"
                        : !SignalCoverageService.Sample(HomeValleyLayout.RegionId, p).Covered || !SignalCoverageService.Sample(HomeValleyLayout.RegionId, mp).Covered ? "不在覆盖里"
                        : !OpenDisk(s, p, 4) ? "圆心不开阔"
                        : !OpenDisk(s, mp, 4) ? "出厂点不开阔"
                        : !FgjEmg1Journey.Corridor(mp, p, out string block) ? "走廊不通 " + block : null;
                    if (bad != null)
                    {
                        if (why.Count < 12)
                        {
                            why.Add($"{radius}格/{deg}°：{bad}");
                        }
                        continue;
                    }
                    arenas.Add((p, toCore));
                }
            }
            if (arenas.Count < 4)
            {
                c.Set("squad.err", $"只找到 {arenas.Count} 处靶场：{string.Join("；", why)}");
                return;
            }
            BlueprintRecord Bp(string id, string primary, bool uplink, params string[] fw) => AddBlueprint(s, "emg2-" + id, primary, fw, uplink);
            string gun = ComponentCatalog.CompGunId;
            var rows = new (string key, BlueprintRecord bp, int arena, float side)[]
            {
                ("m.a", Bp("a", gun, false, Oil), 0, -2f), ("m.b", Bp("b", gun, false, Fire), 0, 2f),
                ("m.c", Bp("c", gun, false, Coolant), 1, -3f), ("m.d", Bp("d", gun, false, Arc), 1, 0f), ("m.e", Bp("e", gun, false), 1, 3f), 
                ("m.f", Bp("f", gun, false, Nitrogen), 2, -2f), ("m.h", Bp("h", ComponentCatalog.CompCannonId, true), 2, 2f),
                ("m.g", Bp("g", ComponentCatalog.CompDroneBayId, true, Split, Pierce), 3, 0f),
            };
            foreach ((string key, BlueprintRecord bp, int arena, float side) in rows)
            {
                (Vector2 center, Vector2 toCore) = arenas[arena];
                Vector2 at = center + toCore * standoff + new Vector2(-toCore.y, toCore.x) * side;
                BlueprintVersionRecord v = bp.Versions[0];
                MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp.BlueprintId, HomeValleyLayout.RegionId, at, SquadHp, SquadHp, "Player", 1, v.CompileSignature);
                if (!r.Success)
                {
                    c.Set("squad.err", $"{key} 出厂失败：{r.Message}");
                    return;
                }
                MachineLoadoutRegistry.Register(s, r.LogicId, bp.BlueprintId, 1);
                c.SetInt(key, r.LogicId);
            }
            // R 车（维修束）放在靶子另一侧、E 车穿过水洼后到的那一边：走过通电水洼会被电麻痹，修不了
            {
                BlueprintRecord rb = Bp("r", ComponentCatalog.CompRepairBeamId, false);
                MachineOpResult rr = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, rb.BlueprintId, HomeValleyLayout.RegionId, arenas[1].center - arenas[1].toCore * 9f, SquadHp, SquadHp, "Player", 1, rb.Versions[0].CompileSignature);
                if (!rr.Success)
                {
                    c.Set("squad.err", $"m.r 出厂失败：{rr.Message}");
                    return;
                }
                MachineLoadoutRegistry.Register(s, rr.LogicId, rb.BlueprintId, 1);
                c.SetInt("m.r", rr.LogicId);
            }
            CombatKernel k = site.Kernel;
            var cluster = new[] { new Vector2(0f, 0f), new Vector2(1.2f, 0f), new Vector2(-0.6f, 1.1f) };
            for (int a = 0; a < 4; a++)
            {
                Vector2 p = arenas[a].center;
                c.Set("t" + (a + 1) + ".at", GetV(p));
                c.SetInt("t" + (a + 1), SpawnTarget(k, p));
                if (a == 1)
                {
                    c.SetInt("t2b", SpawnTarget(k, p + cluster[1]));
                    c.SetInt("t2c", SpawnTarget(k, p + cluster[2]));
                }
            }
            c.Set("roster", string.Join(",", Team.Select(key => c.GetInt(key).ToString(CultureInfo.InvariantCulture))));
            c.Set("squad.ok", string.Join("；", arenas.Select((a, i) => $"靶场 {i + 1} ({F(a.center.x, "0")},{F(a.center.y, "0")})")));
        }

        private static StepOutcome TickSquadFixture(JourneyContext c)
        {
            if (c.Get("squad.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail(c.Get("squad.err"));
            }
            if (c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            foreach (string key in Team)
            {
                if (!Home.TryGetMachineUnit(c.GetInt(key), out _))
                {
                    return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail($"{key} 没进家园内核");
                }
            }
            CombatKernel k = Home.Kernel;
            if (Count(k, Deflag) + Count(k, Conduct) + Count(k, Shatter) > 0 || Team.Any(key => FgjM2Common.IsAttacking(Home, c.GetInt(key))))
            {
                return StepOutcome.Fail($"还没下令就打起来了（爆燃 {Count(k, Deflag)}、通电 {Count(k, Conduct)}、碎裂 {Count(k, Shatter)}；攻击中 [{string.Join("、", Team.Where(key => FgjM2Common.IsAttacking(Home, c.GetInt(key))))}]）");
            }
            return FgjEmg1Journey.Program(Home, c.GetInt("m.a"), out _, out int prog) && prog >= 0
                ? StepOutcome.Done($"8 台机器出厂（{string.Join("、", Team.Select(key => key + " #" + c.GetInt(key)))}）；{c.Get("squad.ok")}")
                : StepOutcome.Fail($"A 车武器没有程序（下标 {prog}）");
        }

        private static void ClickSpeed(string button)
        {
            UnityEngine.UIElements.Button b = JourneyInput.FindUitk<UnityEngine.UIElements.Button>("[WorldBarHost]", button);
            if (b != null && JourneyInput.IsClickable(b))
            {
                JourneyInput.ClickElement(b);
            }
        }

        private static StepOutcome TickSpeed(JourneyContext c, float speed)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return Mathf.Approximately(GameClock.Speed, speed) ? StepOutcome.Done($"{F(speed)} 倍速") : StepOutcome.Retry($"倍速 {GameClock.Speed}x");
        }

        // ── 下令 ────────────────────────────────────────────────────────────────

        /// <summary>选中的机器右键靶子下令攻击，每 2 秒最多点一次。返回“这台已在执行攻击命令”：下令之前的事不算数。</summary>
        private static bool OrderAttack(JourneyContext c, string machineKey, string targetKey)
        {
            CombatSite site = Home;
            int id = c.GetInt(machineKey);
            if (FgjM2Common.IsAttacking(site, id))
            {
                return true;
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) < 2000)
            {
                return false;
            }
            c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
            if (GameRoot.HomeValley.SelectedMachineLogicId != id)
            {
                FgjM1Journey.ClickMachine(c, machineKey);
                return false;
            }
            FgjM3Common.TryClick(ParseV(c.Get(targetKey + ".at")), 1);
            return false;
        }

        private static StepOutcome Cleared(JourneyContext c, string targetKey, StepOutcome done)
        {
            // 这一段的靶子清场：机器不再往这里打，下一段互不串（夹具）。
            Home.Kernel.Despawn(c.GetInt(targetKey));
            return done;
        }

        // ── 1. 燃烧区 ───────────────────────────────────────────────────────────

        private static StepOutcome TickOil(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!OrderAttack(c, "m.a", "t1"))
            {
                return StepOutcome.Wait;
            }
            CombatZone? z = PatchNear(Home.Kernel, EmgMatterConst.Oil, ParseV(c.Get("t1.at")), 3f);
            if (z == null)
            {
                return StepOutcome.Wait;
            }
            c.SetLong("oil.def0", Count(Home.Kernel, Deflag));
            return StepOutcome.Done($"A 车攻击靶子 1：靶子脚下油斑块（半径 {F(z.Value.Radius)}、{F(z.Value.Amount)} 份），爆燃计数 {c.GetLong("oil.def0")}");
        }

        private static StepOutcome TickBurn(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!OrderAttack(c, "m.b", "t1"))
            {
                return StepOutcome.Wait;
            }
            CombatKernel k = Home.Kernel;
            Vector2 at = ParseV(c.Get("t1.at"));
            long n = Count(k, Deflag);
            Home.TryGetMachineUnit(c.GetInt("m.a"), out int ua);
            Home.TryGetMachineUnit(c.GetInt("m.b"), out int ub);
            // 单位身上的沾染反应（C01）只归点火方（DEBT-EMG2DISC01-02）：这里要的是地面油被点着（E01）那块，归属是两台车。
            int key = NamedReactionCatalog.StableKey(Deflag);
            CombatZone? fire = null;
            for (int i = 0; i < k.PatchSlots && fire == null; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Fire && z.RuleKey == key && math.any(z.Owners == ua) && math.any(z.Owners == ub))
                {
                    fire = z;
                }
            }
            if (n <= c.GetLong("oil.def0") || fire == null)
            {
                return c.StepElapsed < 90 ? StepOutcome.Wait : StepOutcome.Fail($"爆燃 {n} 次，没有归属 A + B 两台车（单位 {ua} / {ub}）的燃烧区：交互伤害归因断了");
            }
            int4 o = fire.Value.Owners;
            k.TryGetUnit(c.GetInt("t1"), out CombatUnitView tv);
            return Cleared(c, "t1", StepOutcome.Done($"B 车点火：爆燃 {c.GetLong("oil.def0")} → {n} 次，明火斑块半径 {F(fire.Value.Radius)}，归属 {o}（A、B 两台）；靶子耐久 {F(tv.Health, "0")} / {F(tv.MaxHealth, "0")}"));
        }

        private static StepOutcome TickDiscovered(JourneyContext c, string reaction, string tag)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            var rec = ReactionFeedback.FirstRecordOf(s, reaction);
            bool codex = MechanicCodex.IsUnlocked(MechanicCodex.ReactionEntryId(reaction));
            string name = GameLogic.Localization.GameText.Get("reaction." + reaction.Substring("reaction_".Length) + ".name");
            // “新发现”同类聚合（语法发现与具名交互进同一条）：看这条的成员。
            NotificationEntry note = NotificationCenter.History.LastOrDefault(n => n.Type != null && n.Type.Id == ReactionDiscoveryService.NotifyType && n.Members.Any(m => m.Detail.Contains(name)));
            NotificationMember member = note?.Members.Last(m => m.Detail.Contains(name));
            if (rec == null || !codex || note == null || note.Level != NotifyLevel.Info || !member.HasLocation)
            {
                return c.StepElapsed < 6 ? StepOutcome.Wait : StepOutcome.Fail($"{name} 第一次结算：首次记录 {rec != null}、图鉴 {codex}、通知“{member?.Detail}”（级别 {note?.Level}、可定位 {member?.HasLocation}）；Posted {ReactionDiscoveryService.Posted}；历史末尾 [{string.Join(" | ", NotificationCenter.History.Reverse().Take(6).Select(n => n.Type?.Id + "：" + n.Latest?.Detail))}]");
            }
            return StepOutcome.Done($"“{member.Detail}”（信息级、可定位）；图鉴解锁；首次记录在 {rec.SiteId}（{tag}）");
        }

        // ── 2. 通电水洼 ─────────────────────────────────────────────────────────

        private static StepOutcome TickPuddle(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!OrderAttack(c, "m.c", "t2"))
            {
                return StepOutcome.Wait;
            }
            Vector2 at = ParseV(c.Get("t2.at"));
            CombatZone? z = PatchNear(Home.Kernel, EmgMatterConst.Coolant, at, 0f);
            if (z == null || z.Value.Radius < 1.6f)
            {
                return StepOutcome.Wait;
            }
            c.SetLong("puddle.n0", Count(Home.Kernel, Conduct));
            return StepOutcome.Done($"C 车攻击靶子 2：冷却液水洼半径 {F(z.Value.Radius)}、{F(z.Value.Amount)} 份，盖住靶子群");
        }

        private static StepOutcome TickShock(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!OrderAttack(c, "m.d", "t2"))
            {
                return StepOutcome.Wait;
            }
            CombatKernel k = Home.Kernel;
            int seen = c.GetInt("shock.seen");
            int[] ts = { c.GetInt("t2"), c.GetInt("t2b"), c.GetInt("t2c") };
            for (int i = 0; i < 3; i++)
            {
                // 攒满就进麻痹、累积量清零（FG19B §3）：两种都算被电。
                if (k.AccumOf(ts[i], EmgPhaseConst.Charge) > 0f || k.InPhase(ts[i], EmgPhaseConst.Charge))
                {
                    seen |= 1 << i;
                }
            }
            c.SetInt("shock.seen", seen);
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone cz) && cz.Matter == EmgMatterConst.Coolant && cz.Charge > 0f)
                {
                    c.SetInt("shock.patch", 1);
                }
            }
            int charged = (seen & 1) + ((seen >> 1) & 1) + ((seen >> 2) & 1);
            long n = Count(k, Conduct);
            if (charged < 2 || n <= c.GetLong("puddle.n0") || c.GetInt("shock.patch") == 0)
            {
                int tick = (int)(c.StepElapsed / 15);
                if (tick > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), tick);
                    Vector2 at = ParseV(c.Get("t2.at"));
                    var rows = new List<string>();
                    for (int i = 0; i < k.PatchSlots; i++)
                    {
                        if (k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Coolant)
                        {
                            rows.Add($"#{z.EmgId} 距{F(math.distance(z.Pos, new double2(at.x, at.y)), "0.#")} 半径{F(z.Radius)} 电{F(z.Charge, "0.##")} 份{F(z.Amount, "0.#")}");
                        }
                    }
                    Vector2 dp = FgjM8Journey.PosOf(c.GetInt("m.d"));
                    c.Log($"[{F(c.StepElapsed, "0")}s] 水洼 [{string.Join("；", rows)}]；D 车离靶 {F(Vector2.Distance(dp, at))}；靶子电 {F(k.AccumOf(ts[0], EmgPhaseConst.Charge), "0.##")}/{F(k.AccumOf(ts[1], EmgPhaseConst.Charge), "0.##")}/{F(k.AccumOf(ts[2], EmgPhaseConst.Charge), "0.##")}；被电靶子 {charged} / 3；通电水洼 {n} 次");
                }
                return StepOutcome.Wait;
            }
            c.SetInt("friendly.n0", NotificationCenter.History.Count(x => x.Type != null && x.Type.Id == "friendly_fire"));
            return StepOutcome.Done($"D 车电弧：通电水洼 {c.GetLong("puddle.n0")} → {n} 次，{charged} / 3 个靶子被电（积累量 {F(k.AccumOf(ts[0], EmgPhaseConst.Charge), "0.##")} / {F(k.AccumOf(ts[1], EmgPhaseConst.Charge), "0.##")} / {F(k.AccumOf(ts[2], EmgPhaseConst.Charge), "0.##")}）");
        }

        private static StepOutcome TickRepair(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatSite site = Home;
            int e = c.GetInt("m.e");
            if (!site.TryGetMachineUnit(e, out int ue) || !site.Kernel.TryGetUnit(ue, out CombatUnitView v))
            {
                return StepOutcome.Fail("E 车不在内核里");
            }
            if (c.GetInt("rp.sent") == 0)
            {
                if (!(v.Health < v.MaxHealth))
                {
                    return StepOutcome.Fail($"E 车没受伤（{F(v.Health)}/{F(v.MaxHealth)}），修复这一步没有意义");
                }
                // E 车还在靶子拾取半径（1.6 格）里时右键会被认成攻击靶子：等它走开
                if (site.TryGetMachineUnit(c.GetInt("m.r"), out int ur) && site.TryGetCommand(ur, out CombatCommand cmd) && cmd.Kind == CombatCommandKind.Attack && cmd.Target == ue)
                {
                    c.SetInt("rp.sent", 1);
                    c.SetLong("rp.hp0", (long)(v.Health * 100f));
                }
                else if (!JourneyInput.Holding && Vector2.Distance(FgjM8Journey.PosOf(e), ParseV(c.Get("t2.at"))) > 3f && NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) > 1500)
                {
                    c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                    FgjM3Common.TryClick(FgjM8Journey.PosOf(e), 1);
                }
                return StepOutcome.Wait;
            }
            if ((int)(c.StepElapsed / 10) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 10));
                c.Log($"[{F(c.StepElapsed, "0")}s] 已下令；E 车耐久 {F(v.Health)}（起点 {F(c.GetLong("rp.hp0") / 100.0)}），R 车位置 {FgjM8Journey.PosOf(c.GetInt("m.r"))}、E 车位置 {FgjM8Journey.PosOf(e)}");
            }
            return v.Health * 100f > c.GetLong("rp.hp0")
                ? StepOutcome.Done($"E 车耐久 {F(c.GetLong("rp.hp0") / 100.0)} → {F(v.Health)}（R 车右键下令修复）")
                : StepOutcome.Wait;
        }

        private static StepOutcome TickFriendly(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatSite site = Home;
            Vector2 at = ParseV(c.Get("t2.at"));
            int e = c.GetInt("m.e");
            Vector2 me = FgjM8Journey.PosOf(e);
            // 另外两个靶子先清场：点水洼边上的地面才不会被拾取半径（1.6 格）认成“右键敌人 = 攻击”。
            site.Kernel.Despawn(c.GetInt("t2b"));
            site.Kernel.Despawn(c.GetInt("t2c"));
            // 右键水洼对面的地面（圆心靶子的拾取半径之外）：E 车穿过水洼开过去。
            if (c.Get("fr.goal", string.Empty).Length == 0)
            {
                c.Set("fr.goal", GetV(at + (at - me).normalized * 6f));
            }
            long dist = (long)(Vector2.Distance(me, at) * 100f);
            long min = c.GetLong("fr.min");
            if (min == 0 || dist < min)
            {
                c.SetLong("fr.min", dist);
            }
            if (!JourneyInput.Holding && c.GetInt("fr.sent") == 0 && NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) > 1500)
            {
                c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                if (GameRoot.HomeValley.SelectedMachineLogicId != e)
                {
                    FgjM1Journey.ClickMachine(c, "m.e");
                }
                else if (FgjM3Common.TryClick(ParseV(c.Get("fr.goal")), 1))
                {
                    c.SetInt("fr.sent", 1);
                }
            }
            List<NotificationEntry> notes = NotificationCenter.History.Where(x => x.Type != null && x.Type.Id == "friendly_fire").ToList();
            if (notes.Count <= c.GetInt("friendly.n0") || c.GetLong("fr.min") > 230)
            {
                if ((int)(c.StepElapsed / 10) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 10));
                    c.Log($"[{F(c.StepElapsed, "0")}s] E 车离水洼圆心 {F(Vector2.Distance(me, at))}（最近 {F(c.GetLong("fr.min") / 100.0)}）；友伤通知 {notes.Count - c.GetInt("friendly.n0")} 条；下令 {c.GetInt("fr.sent")}；选中 {GameRoot.HomeValley.SelectedMachineLogicId}；E 车耐久 {(MachineRegistry.TryGetRecord(e, out MachineRecord er) ? er.Health : -1)}");
                }
                return StepOutcome.Wait;
            }
            NotificationEntry n = notes[notes.Count - 1];
            string text = n.Latest?.Detail ?? string.Empty;
            string party = site.TryGetMachineUnit(e, out int ue) ? EmgChainView.PartyName(site, St, ue) : string.Empty;
            if (n.Level != NotifyLevel.Warning || !n.HasAnyLocation || !text.Contains(party))
            {
                return StepOutcome.Fail($"友伤通知不对：“{text}”（级别 {n.Level}、可定位 {n.HasAnyLocation}，应写到 {party}）");
            }
            return Cleared(c, "t2", StepOutcome.Done($"E 车穿过通电水洼（离圆心最近 {F(c.GetLong("fr.min") / 100.0)} 格）：警告级可定位“{text}”"));
        }

        // ── 3. 冻结 → 碎裂 ──────────────────────────────────────────────────────

        private static StepOutcome TickFreeze(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!OrderAttack(c, "m.f", "t3"))
            {
                return StepOutcome.Wait;
            }
            CombatKernel k = Home.Kernel;
            if (!k.InPhase(c.GetInt("t3"), EmgPhaseConst.Chill))
            {
                return StepOutcome.Wait;
            }
            c.SetLong("frag0", k.EmgBusStats.FragSpawned);
            c.SetLong("shat0", Count(k, Shatter));
            return StepOutcome.Done($"F 车攻击靶子 3：靶子寒量 {F(k.AccumOf(c.GetInt("t3"), EmgPhaseConst.Chill), "0.##")}，进入冻结相态");
        }

        private static StepOutcome TickShatter(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!OrderAttack(c, "m.h", "t3"))
            {
                return StepOutcome.Wait;
            }
            CombatKernel k = Home.Kernel;
            long n = Count(k, Shatter);
            long frag = k.EmgBusStats.FragSpawned - c.GetLong("frag0");
            if (n <= c.GetLong("shat0") || frag <= 0)
            {
                return StepOutcome.Wait;
            }
            return Cleared(c, "t3", StepOutcome.Done($"H 车重炮命中冻结的靶子：碎裂 {c.GetLong("shat0")} → {n} 次，崩出破片 {frag} 片"));
        }

        /// <summary>直控左键点敌人：敌人正好被 HUD 挡住时点它旁边露出来的地方（直控拾取半径 2.5 格）。</summary>
        private static bool ClickEnemy(Vector2 tp)
        {
            foreach (Vector2 off in new[] { Vector2.zero, Vector2.up, Vector2.down, Vector2.left, Vector2.right, new Vector2(1.4f, 1.4f), new Vector2(-1.4f, -1.4f), new Vector2(1.4f, -1.4f), new Vector2(-1.4f, 1.4f) })
            {
                if (FgjM3Common.Clear(tp + off, out _))
                {
                    JourneyInput.Click(tp + off);
                    return true;
                }
            }
            return false;
        }

        // ── 4. FG-GAP-172 ───────────────────────────────────────────────────────

        private static List<CombatDroneFlash> DroneFlash(CombatKernel k, int ownerUnit)
        {
            var list = new List<CombatDroneFlash>();
            for (int i = 0; i < k.DroneCount; i++)
            {
                if (k.TryGetDroneFlash(i, out CombatDroneFlash f) && f.Owner == ownerUnit)
                {
                    list.Add(f);
                }
            }
            return list;
        }

        private static StepOutcome TickDrones(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!OrderAttack(c, "m.g", "t4"))
            {
                return StepOutcome.Wait;
            }
            CombatKernel k = Home.Kernel;
            if (!Home.TryGetMachineUnit(c.GetInt("m.g"), out int unit))
            {
                return StepOutcome.Fail("G 车不在内核里");
            }
            List<CombatDroneFlash> fl = DroneFlash(k, unit);
            if (fl.Count == 0)
            {
                return StepOutcome.Wait;
            }
            c.SetInt("dr.prog0", fl[0].Program);
            c.SetInt("dr.weapon0", fl[0].Weapon);
            c.Set("dr.ids", string.Join(",", fl.Select(x => x.Id.ToString(CultureInfo.InvariantCulture))));
            return StepOutcome.Done($"放出 {fl.Count} 架无人机：程序 {fl[0].Program}（AI 程序），持有 {string.Join("/", fl.Select(x => x.Held))} 条，武器下标 {fl[0].Weapon}");
        }

        private static StepOutcome TickUplinked(JourneyContext c, string key)
        {
            int id = c.GetInt(key);
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            if (SignalPresence.CurrentMachineLogicId != id || WorldView.Director.Mode != ViewMode.Direct)
            {
                return c.StepElapsed < 6 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {Label(id)}（{SignalUplinkService.LastFeedbackText}；{FgjM1Journey.UiFail(c)}）");
            }
            FgjEmg1Journey.Program(Home, id, out CombatWeapon w, out int prog);
            c.SetInt(key + ".prog", prog);
            return prog >= 0 ? StepOutcome.Done($"接入 {Label(id)}（镜头直控）：武器程序 {prog}、射程 {F(w.Range)}") : StepOutcome.Fail("接入后武器没有程序");
        }

        private static StepOutcome TickDroneRefresh(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatKernel k = Home.Kernel;
            int g = c.GetInt("m.g");
            if (!Home.TryGetMachineUnit(g, out int unit) || !FgjEmg1Journey.Program(Home, g, out CombatWeapon w, out _))
            {
                return StepOutcome.Fail("G 车不在内核里");
            }
            Vector2 tp = ParseV(c.Get("t4.at"));
            Vector2 me = FgjM8Journey.PosOf(g);
            if (Vector2.Distance(me, tp) > w.Range - 1f)
            {
                FgjM1Journey.DriveToward(me, tp + (me - tp).normalized * (w.Range - 3f), 0.3);
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "fireAt")) > 400)
            {
                c.SetLong(FgjM3Common.SK(c, "fireAt"), NowMs());
                c.SetInt("f.drclicks", c.GetInt("f.drclicks") + (ClickEnemy(tp) ? 1 : 0));
            }
            List<CombatDroneFlash> fl = DroneFlash(k, unit);
            HashSet<string> old = new HashSet<string>(c.Get("dr.ids").Split(','));
            List<CombatDroneFlash> kept = fl.Where(x => old.Contains(x.Id.ToString(CultureInfo.InvariantCulture))).ToList();
            if (kept.Count == 0)
            {
                return StepOutcome.Fail($"第一次开火前原来那几架无人机就到期了：已过 {F(c.StepElapsed)} 秒，左键 {c.GetInt("f.drclicks")} 次（在场 {fl.Count} 架）；离靶 {F(Vector2.Distance(me, tp))}，射程 {F(w.Range)}；{(FgjM3Common.Clear(tp, out string why) ? "靶子可点" : why)}");
            }
            if (kept.Any(x => x.Weapon == c.GetInt("dr.weapon0") || x.Program == c.GetInt("dr.prog0")))
            {
                return StepOutcome.Wait;
            }
                        return StepOutcome.Done($"接入后第一次开火的 {F(c.StepElapsed)} 秒内（含开到射程），在场 {kept.Count} 架都换成接入程序：武器下标 {c.GetInt("dr.weapon0")} → {kept[0].Weapon}，程序 {c.GetInt("dr.prog0")} → {kept[0].Program}，持有 {string.Join("/", kept.Select(x => x.Held))} 条（占用 {k.FlashUsed(unit)} / {k.FlashBandwidth}）");
        }

        private static IEnumerable<string> DroneUntilOf(CombatKernel k, int ownerUnit)
        {
            for (int i = 0; i < k.DroneCount; i++)
            {
                if (k.TryGetDrone(i, out CombatDrone dr) && dr.Owner == ownerUnit)
                {
                    yield return F(dr.Until, "0.##");
                }
            }
        }

        private static StepOutcome TickDroneLeft(JourneyContext c)
        {
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            int g = c.GetInt("m.g");
            if (SignalPresence.CurrentMachineLogicId == g)
            {
                return StepOutcome.Retry("信号还在 G 车里");
            }
            Home.TryGetMachineUnit(g, out int unit);
            List<CombatDroneFlash> fl = DroneFlash(Home.Kernel, unit);
            return Cleared(c, "t4", StepOutcome.Done($"离开 G 车：在场 {fl.Count} 架，剥离 {fl.Count(x => x.Stripped)} 架（FGR-EMG-037），武器回 AI 程序"));
        }

        // ── 5. 突袭 ─────────────────────────────────────────────────────────────

        /// <summary>剧情入口排一波 1 级突袭（DEBT-FG6E2E01-03 同一夹具；导演的暴露触发由 FGJ-M6 覆盖）。</summary>
        private static void RequestRaid(JourneyContext c, string key)
        {
            CampaignState s = St;
            if (c.Get(key + ".plan", string.Empty).Length > 0)
            {
                return;
            }
            c.SetInt(key + ".n0", RaidDirectorService.StateOf(s)?.NextPlanSerial ?? 0);
            RaidDirectorService.RequestStoryRaid(s, RaidDirectorService.MostStimulatedFaction(s), 1);
        }

        private static StepOutcome TickRaidRequested(JourneyContext c, string key)
        {
            CampaignState s = St;
            RaidPlanRecord p = D.LivePlans(s).Where(x => x.Exempt && x.Serial >= c.GetInt(key + ".n0")).OrderBy(x => x.Serial).FirstOrDefault();
            if (p == null)
            {
                return c.StepElapsed < 5 ? StepOutcome.Wait : StepOutcome.Fail("剧情触发后没有新的突袭计划");
            }
            long wait = p.WarnTick - GameClock.Ticks;
            if (wait > GameClock.StepHz * 10L && p.State == RaidDirectorService.StateScheduled)
            {
                long shift = wait - GameClock.StepHz * 3L;
                p.WarnTick -= shift;
                p.DepartTick -= shift;
                p.ArrivalTick -= shift;
            }
            c.Set(key + ".plan", p.PlanId);
            return StepOutcome.Done($"进度夹具：剧情节点触发一波突袭 {D.PlanLine(p)}（预警前的等待快进）");
        }

        private static StepOutcome TickWarned(JourneyContext c, string key)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            RaidPlanRecord p = RaidDirectorService.FindPlan(s, c.Get(key + ".plan"));
            if (p == null)
            {
                return StepOutcome.Fail("突袭计划不见了");
            }
            if (p.State < RaidDirectorService.StateWarned)
            {
                return StepOutcome.Wait;
            }
            RaidWarningHudUIToolkit hud = RaidWarningHudUIToolkit.Instance;
            hud?.Refresh(force: true);
            if (hud == null || !hud.PanelVisible || hud.RowCount < 1)
            {
                return c.StepElapsed < 60 ? StepOutcome.Wait : StepOutcome.Fail($"发了预警却没有预警条（{hud?.PanelVisible} / {hud?.RowCount}）");
            }
            c.Set(key + ".ap", GetV(hud.RowTarget(0)));
            return StepOutcome.Done($"预警条“{hud.RowText(0).Replace("\n", " / ")}”；{D.PlanLine(p)}");
        }

        private static StepOutcome TickSelectTeam(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            List<int> team = Team.Select(c.GetInt).Where(id => MachineRegistry.TryGetRecord(id, out MachineRecord r) && r.IsAlive).ToList();
            IReadOnlyList<int> sel = GameRoot.HomeValley?.SquadCommands?.Selection;
            string kk = FgjM3Common.SK(c, "k");
            if (sel != null && team.All(sel.Contains))
            {
                return StepOutcome.Done($"选中全队 {team.Count} 台");
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 400)
            {
                return StepOutcome.Wait;
            }
            int k = c.GetInt(kk);
            if (k >= team.Count)
            {
                return StepOutcome.Retry($"点完一遍后选择集 [{string.Join("、", (sel ?? Array.Empty<int>()).Select(Label))}]");
            }
            if (!FgjM1Journey.HomeMarker(team[k], out HomeValleyMachineMarker m))
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail($"{Label(team[k])} 没有模型");
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
            c.SetInt(kk, k + 1);
            c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
            return StepOutcome.Wait;
        }

        private static StepOutcome TickRally(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            Vector2 ap = ParseV(c.Get("r1.ap"));
            Vector2 core = FgjM8Journey.CorePos;
            Vector2 hold = ap + (core - ap).normalized * Mathf.Min(16f, Vector2.Distance(ap, core) * 0.5f);
            GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(hold.x), Mathf.RoundToInt(hold.y)), 10);
            if (JourneyInput.Holding || !FgjM3Common.TryClick(new Vector2(g.X, g.Y), 1))
            {
                return StepOutcome.Wait;
            }
            c.Set("hold", $"{g.X},{g.Y}");
            return StepOutcome.Done($"右键地面 ({g.X},{g.Y})：全队开到抵达点 {GetV(ap)} 靠核心一侧");
        }

        private static StepOutcome TickSieging(JourneyContext c, string key)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            RaidResultRecord r = RaidResultService.FindByPlan(St, c.Get(key + ".plan"));
            SiegeState st = SiegeService.StateOf(St);
            if (r == null || r.UnfoldTick < 0 || st == null || !st.TheaterActive || D.Raiders().Count == 0)
            {
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"部队到达、展开攻城 {r.Unfolded} 台（在场 {D.Raiders().Count} 台）");
        }

        /// <summary>直控 H 车：开到射程内，左键点最近的突袭单位。H 车打不动了（被打掉）也不算失败，AI 队友接着打；步骤结尾由 <see cref="TickRaidEnd"/> 判熔穿有没有发生。</summary>
        private static StepOutcome TickRaidFight(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CombatSite site = Home;
            CampaignState s = St;
            if (c.GetInt("slag.base") == 0)
            {
                c.SetInt("slag.base", 1);
                c.Set("slag0", string.Join(",", SlagIds(site.Kernel)));
                c.SetLong("melt0", Count(site.Kernel, Melt));
            }
            bool theater = SiegeService.StateOf(s)?.TheaterActive ?? false;
            List<(int Id, Vector2 Pos)> raiders = D.Raiders();
            int h = c.GetInt("m.h");
            bool up = SignalPresence.CurrentMachineLogicId == h;
            if ((int)(c.StepElapsed / 15) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 15));
                Vector2 hp = up ? FgjM8Journey.PosOf(h) : Vector2.zero;
                c.Log($"[{F(c.StepElapsed, "0")}s] 剧场 {theater}；突袭单位 {raiders.Count}；接入 {up} @{GetV(hp)}；开火 {c.GetInt("f.clicks")} 次；熔穿 {Count(site.Kernel, Melt)} 次；{TeamHp(c)}；{WreckLine(site.Kernel)}");
            }
            if (!theater && raiders.Count == 0)
            {
                return StepOutcome.Done($"突袭结束：直控开火 {c.GetInt("f.clicks")} 次，熔穿 {Count(site.Kernel, Melt)} 次，残骸 {site.Kernel.WreckCount}");
            }
            // 熔穿打出第一堆冷渣就退出接入：剩下的交给 AI 队友常规开火——熔融金属会把落在里面的残骸烧掉（FGR-EMG-113），最后几台要在没有热斑块的地方留下残骸。
            if (up && NewSlag(c, site.Kernel).Count > 0)
            {
                JourneyInput.ReleaseKeys();
                JourneyInput.PressAction(GameActionId.ToggleCameraView);
                return StepOutcome.Wait;
            }
            if (!up || raiders.Count == 0 || !FgjEmg1Journey.Program(site, h, out CombatWeapon w, out _))
            {
                return StepOutcome.Wait;
            }
            Vector2 me = FgjM8Journey.PosOf(h);
            (int id, Vector2 tp) = raiders.OrderBy(r => Vector2.Distance(r.Pos, me)).First();
            float d = Vector2.Distance(me, tp);
            if (d > w.Range - 1f)
            {
                FgjM1Journey.DriveToward(me, tp, 0.3);
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "fireAt")) > 600)
            {
                c.SetLong(FgjM3Common.SK(c, "fireAt"), NowMs());
                if (ClickEnemy(tp))
                {
                    c.SetInt("f.clicks", c.GetInt("f.clicks") + 1);
                }
                else
                {
                    FgjM1Journey.DriveToward(me, tp, 0.3); // 敌人出了直控画面 / 被界面挡着：开近一点
                }
            }
            return StepOutcome.Wait;
        }

        private static string TeamHp(JourneyContext c)
        {
            CombatKernel k = Home.Kernel;
            return "队伍 [" + string.Join(" ", Team.Select(key =>
            {
                int id = c.GetInt(key);
                if (!MachineRegistry.TryGetRecord(id, out MachineRecord r) || !r.IsAlive)
                {
                    return key.Substring(2) + "：毁";
                }
                string phase = Home.TryGetMachineUnit(id, out int u)
                    ? string.Concat(Enumerable.Range(0, EmgPhaseConst.Count).Where(i => k.InPhase(u, i)).Select(i => "相" + i)) + (k.AccumOf(u, EmgPhaseConst.Overheat) > 0f ? "热" + F(k.AccumOf(u, EmgPhaseConst.Overheat), "0.#") : string.Empty)
                    : string.Empty;
                return key.Substring(2) + "：" + F(r.Health, "0") + phase;
            })) + "]";
        }

        private static List<int> SlagIds(CombatKernel k)
        {
            var ids = new List<int>();
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && z.Matter == EmgMatterConst.Slag)
                {
                    ids.Add(z.EmgId);
                }
            }
            return ids;
        }

        /// <summary>这一波打出来的冷渣（靶场上目标过热漏出的熔融金属也会凝成冷渣，不算防线前的）。</summary>
        private static List<int> NewSlag(JourneyContext c, CombatKernel k)
        {
            HashSet<string> before = new HashSet<string>(c.Get("slag0", string.Empty).Split(','));
            return SlagIds(k).Where(id => !before.Contains(id.ToString(CultureInfo.InvariantCulture))).ToList();
        }

        private static string WreckLine(CombatKernel k)
        {
            var rows = new List<string>();
            for (int i = 0; i < k.WreckCount; i++)
            {
                if (k.TryGetWreck(i, out EmgWreck w))
                {
                    rows.Add($"#{w.Id}@{F(w.Pos.x, "0")},{F(w.Pos.y, "0")} 耐久{F(w.Hp, "0")}/{F(w.MaxHp, "0")} 温{F(w.Temp, "0")}{(w.Until < 1e12 ? " 到期" + F(w.Until, "0") : string.Empty)}");
                }
            }
            var mats = new List<string>();
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (k.TryGetPatch(i, out CombatZone z) && (z.Matter == EmgMatterConst.Molten || z.Matter == EmgMatterConst.Slag))
                {
                    mats.Add($"{(z.Matter == EmgMatterConst.Slag ? "渣" : "熔")}#{z.EmgId}@{F(z.Pos.x, "0")},{F(z.Pos.y, "0")} 份{F(z.Amount)} 温{F(z.Temp, "0")} 耐久{F(z.Hp, "0")}");
                }
            }
            return $"斑块 [{string.Join("；", mats)}]；残骸 [{string.Join("；", rows)}]（内核 {F(k.Time, "0")} 秒；生成 {k.WreckCounts.Born}、到期 {k.WreckCounts.Aged}、满容量 {k.WreckCounts.Capped}、被打掉 {k.WreckCounts.Destroyed}）";
        }

        private static StepOutcome TickRaidEnd(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatKernel k = Home.Kernel;
            RaidResultRecord r = RaidResultService.FindByPlan(St, c.Get("r1.plan"));
            List<int> slag = NewSlag(c, k);
            long melts = Count(k, Melt) - c.GetLong("melt0");
            var wrecks = new List<int>();
            for (int i = 0; i < k.WreckCount; i++)
            {
                if (k.TryGetWreck(i, out EmgWreck w))
                {
                    wrecks.Add(w.Id);
                }
            }
            if ((int)(c.StepElapsed / 6) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 6));
                c.Log($"[{F(c.StepElapsed, "0")}s] {TeamHp(c)}；{WreckLine(k)}");
            }
            if (slag.Count == 0 || wrecks.Count == 0 || melts <= 0)
            {
                return c.StepElapsed < 25 ? StepOutcome.Wait
                    : StepOutcome.Fail($"突袭后没有留下残骸 / 冷渣：残骸 {wrecks.Count}、这波的冷渣 {slag.Count}；这波熔穿 {melts} 次、结算留下 {r?.LeftWrecks} / {r?.LeftSolids}");
            }
            if (r == null || r.EndTick < 0)
            {
                return StepOutcome.Fail("这波突袭没有结算");
            }
            c.Log(TeamHp(c) + "；" + WreckLine(k));
            c.Set("left.slag", string.Join(",", slag));
            c.Set("left.wreck", string.Join(",", wrecks));
            return StepOutcome.Done($"防线前留下残骸 {wrecks.Count} 块、这波打出的冷渣 {slag.Count} 堆；结算记下留下残骸 {r.LeftWrecks}、固态斑块 {r.LeftSolids}；这波熔穿 {melts} 次");
        }

        private static StepOutcome TickSecondRaid(JourneyContext c)
        {
            StepOutcome o = TickSieging(c, "r2");
            if ((int)(c.StepElapsed / 60) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 60));
                c.Log($"[{F(c.StepElapsed, "0")}s] {WreckLine(Home.Kernel)}");
            }
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            CombatKernel k = Home.Kernel;
            RaidResultRecord r = RaidResultService.FindByPlan(St, c.Get("r2.plan"));
            List<int> wrecks = c.Get("left.wreck").Split(',').Select(int.Parse).ToList();
            List<int> slag = c.Get("left.slag").Split(',').Select(int.Parse).ToList();
            int w = wrecks.Count(id => k.WreckIndexOf(id) >= 0);
            int sl = slag.Count(id => k.PatchIndexOf(id) >= 0);
            c.Log(WreckLine(k));
            return r != null && r.BasePersistWrecks >= 1 && r.BasePersistSolids >= 1 && w >= 1 && sl >= 1
                ? StepOutcome.Done($"下一波到达时上次的残骸 {w} / {wrecks.Count} 块、冷渣 {sl} / {slag.Count} 堆仍在（同一批编号）；这次结算基线 {r.BasePersistWrecks} / {r.BasePersistSolids}")
                : StepOutcome.Fail($"下一波到达时上次的东西没了：残骸 {w} / {wrecks.Count}、冷渣 {sl} / {slag.Count}；基线 {r?.BasePersistWrecks} / {r?.BasePersistSolids}");
        }
    }
}
