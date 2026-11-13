using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinGames.Sim.Combat;
using BinGames.Sim.Nav;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.View;
using GameLogic.Campaign.WorldSim;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// EMG4-E2E-01：FG-EMG 阶段 4 出口旅程 FGJ-EMG-4（FG19 第 16 节；LANDING-PLAN 6.3）。从主菜单“新建”出发（固定测试种子 42、标准难度），全程正式输入，
    /// RTS 口径：左键选择、右键敌人 = 攻击、右键地面 = 移动；建造模式工具按钮 + 左键地格；顶栏计数打开待分拣面板、点“选这个”。
    ///
    /// 原文：劫持一台敌机并看它带原生程序作战、期满自毁 → 冰冻水面过桥（水面地形未落地，按 EMG4-TER-01 / DEBT-EMG4TER01-01 移出本旅程，随 FG18-SEA-01）
    /// → 熔坑出现并由工作单填平 → 三选一显示“可产生 N 种新交互” → 重复拿到同一作战组件只多一件库存 → 模糊测试器常驻跑通。
    /// 模糊测试器常驻是开发工具，没有玩家输入：由 RunAll 的 FgEmergenceFuzzSelfCheck（C / G 段固定 500 例 + 随机批、死指令 0、与上一次比退化）覆盖，证据里写单跑结果。
    ///
    /// 轻量口径（用户 2026-10-12）：每个新系统被真实输入触发一次并断言结果。夹具写在步骤标题里：<see cref="ApplyFixture"/>（正式固件记为已获得，先断言在正常游戏里有来源）、
    /// <see cref="ApplySquadFixture"/>（蓝图与机器直接出厂 + 一台正式敌人表的杂兵（EnemySpawnBuilder 真实生成，不动的装甲机）+ 不还手的敌方靶子）、
    /// <see cref="ApplyPitFixture"/>（可熔地面上放一摊熔融金属，熔穿排放由 FGJ-EMG-2 覆盖；凝固成坑走内核 V06）、<see cref="ApplyLootFixture"/>（精英数据包，候选固定为
    /// 一件已掌握的作战组件 + 两条固件，精英掉落由 FGJ-M8 覆盖；分拣走正式面板）。
    /// 反向（FG19 第 16 节，出口核对补缺口 2026-11-12，取代 ADR-EMG-007 决定 2 的轻量口径 b）：<see cref="ApplyWallFixture"/>（熔坑格背后登记一座仓库、固化胶块围一圈只留熔坑格做缺口）
    /// → 熔坑补上缺口出堵路警告、点通知弹出条镜头飞到熔坑、填平后解除；<see cref="ApplySlotFixture"/>（信号核两槽装固件）→ 余烬主机封锁 1 号槽（正式来源随 FG11-BOSS-01，调它将来调用的同一入口）
    /// → 通知 / HUD / 信号核面板写原因、接入程序按空槽重排 → 按来源解除后恢复同签名。
    /// 用法：bash tools/unity-journey.sh FGJ-EMG-4；断点 emg_fix / squad_fix / hj_end / pit_fill / loot_fix（--from 续跑）。
    /// </summary>
    public static class FgjEmg4Journey
    {
        public const string Id = "FGJ-EMG-4";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private const string Reboot = "fw_reboot";

        /// <summary>被劫持的敌人：正式敌人表的装甲机（普通杂兵、不动、射程 9 米、非结构非外部血量 → 可劫持）。</summary>
        private const string FoeType = "enemy_armorbot";

        /// <summary>夹具机器耐久（标准 100），本旅程不验平衡（FG15-BAL-01）。</summary>
        private const float SquadHp = 500f;

        /// <summary>机器出厂离敌机的距离（迎战半径 18 格外，要右键才开打）、靶子在敌机背后的距离（装甲机射程 9 米内）。</summary>
        private const float Standoff = 24f, DummyBehind = 7f;

        private const float DummyHp = 8000f;

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
                S("emg_fix", "进度夹具：正式表 emg.stage ≥ 4（阶段 4 生效）；[重启注入] 记为已获得（先断言它在正常游戏的掉落 / 候选池里有来源）", 15, ApplyFixture, TickFixture),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),

                S("squad_fix", "出厂夹具：H 车（机枪 + [重启注入]）出厂；开阔地上按正式敌人表生成一台装甲机（杂兵、不动），它背后 7 米一个不还手的敌方靶子", 20, ApplySquadFixture, TickSquadFixture),

                // ── 1. 劫持：重启注入 → 带原生程序作战 → 期满自毁 ──
                S("sel_h", "左键点 H 车", 15, c => FgjM1Journey.ClickMachine(c, "m.h"), c => FgjM1Journey.TickSelected(c, "m.h"), retries: 2),
                S("hijack", "右键装甲机（攻击）：H 车开到射程开火，每次命中 [重启注入] 写入侵度，满阈值时打出致命一击 → 装甲机不阵亡、重启为己方（劫持），悬停写剩余秒数与“由教义触发”", 150, null, TickHijack),
                S("h_back", "右键 H 车出发点的地面（移动）：H 车退回；被劫持的装甲机不接受编队命令，按教义继续作战", 30, null, TickBack),
                S("hj_fight", "被劫持的装甲机用自己原生的武器（武器不变、不改刷劫持者程序）打射程内的敌方靶子：靶子掉血", 60, null, TickNativeFight),
                S("hj_end", "等期满（30 游戏秒，3 倍速）：装甲机自毁，劫持结束、击毁只结算一次（敌人记录阵亡、留残骸）", 60, null, TickExpire),

                // ── 2. 熔坑：出现 → 堵路警告（反向）→ 工作单填平 → 警告解除 ──
                S("wall_fix", "堵路夹具：离核心不远、背后开阔的可熔地面，往背离核心 5 格处登记一座仓库（建造由 FGJ-M3 覆盖），以它为心、半径 5 用固化胶块（固态阻路斑块）围一圈，只在这格可熔地面留缺口：缺口开着时不报堵路", 30, ApplyWallFixture, TickWallOpen),
                S("pit_fix", "地形夹具：缺口那格可熔地面（废墟里的铁板、混凝土楼板）上放一摊熔融金属（35 份；熔穿排放由 FGJ-EMG-2 覆盖）→ 凝固累计 ≥ 30 份，内核 V06 烧出熔坑：这一格不可通行、不能建造（原因写熔坑）", 60, ApplyPitFixture, TickPit),
                S("block_warn", "反向（FG19 第 16 节“永久改变堵路警告”）：熔坑补上缺口 → 警告级通知“熔坑堵死了归还核心到仓库的唯一通路……位置 (x, y)”、告警栏一行可定位；点这条通知弹出条，镜头飞到熔坑", 45, null, TickBlockWarn, retries: 1),
                S("build_open", "按建造键（默认 B）打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1),
                S("clear_tool", "点建造栏“清除场地物”工具", 10, c => FgjM1Journey.ClickUi(c, FgjM3Common.BuildHost, "BuildClearSite"), TickClearTool, retries: 2),
                S("clear_click", "左键点熔坑那一格：开“填平熔坑”工作单（复用清除工作单，预留废料，工单目标写“熔坑”）", 20, null, TickClearClick, retries: 2),
                S("build_close", "再按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1),
                S("pit_fill", "等空闲工程机领单到现场填平（3 倍速）：熔坑消失、这一格又能走、又能建造", 240, null, TickPitFilled),
                S("block_clear", "填平后下一次堵路检查：缺口重新打通，堵路警告与告警栏那一行解除", 40, null, TickBlockClear),

                // ── 3. 三选一：新交互提示 → 重复作战组件只多一件库存 ──
                S("loot_fix", "掉落夹具：仓库修好（新局仓库受损、容量 0，修建筑由 FGJ-M1 覆盖）；家园里一个精英数据包（候选 = 一件已掌握的作战组件 + 两条固件；精英掉落由 FGJ-M8 覆盖）", 15, ApplyLootFixture, TickLootFixture),
                S("pick_open", "点顶栏“待分拣”打开数据包面板：固件 / 部件卡片写“与你现有的固件 / 部件可产生 N 种新交互（已发现 M 种）”，重复作战组件卡片写“重复获得只加库存”", 25, null, TickPickOpen),
                S("pick_dup", "点重复作战组件那张卡片的“选这个”：作战组件库存 +1（多一台车能装），不进解析台、不改数值", 20, null, TickPickDup),
                S("pick_close", "Esc 关闭待分拣面板", 10, c => JourneyInput.PressAction(GameActionId.Cancel), TickPickClosed, retries: 1),

                // ── 4. 反向（FG19 第 16 节“槽位封锁后程序重排并恢复”）──
                S("sb_fix", "封锁夹具：信号核 1 号槽装 [分裂]（触发器）、2 号槽装 [寻的]（芯片入库与装槽由 FGJ-EMG-1 覆盖）：接入程序根 = 分裂、子弹药一代 = 寻的", 15, ApplySlotFixture, TickSlotFixture),
                S("sb_block", "余烬主机封锁 1 号槽（正式来源是 FG11-BOSS-01 的首领技能；这里调它将来调用的同一入口 SignalSeizeService.BlockSlot）：警告级通知“第 1 槽被余烬主机封锁……按空槽重编译，冷却不重置”，HUD 状态行写被封锁的槽", 15, ApplySlotBlock, TickSlotBlocked),
                S("sb_open", "按信号核键（默认 P）打开信号核面板：1 号槽写“被余烬主机封锁……它是触发器……下游指令回到上一层节点”；接入程序按空槽重排（根 = 寻的，没有子弹药节点）", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), TickSlotPanel, retries: 1),
                S("sb_lift", "首领被击毁，按来源解除（SignalSeizeService.UnblockAll(余烬主机)）：通知“第 1 槽已恢复”，面板 1 号槽回到 [分裂]、HUD 不再写封锁，接入程序与封锁前同签名", 15, ApplySlotLift, TickSlotLifted),
                S("sb_close", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),
            };
            return new JourneyDef
            {
                Id = Id,
                Title = "FG-EMG 阶段 4 出口：[重启注入] 劫持一台敌机 → 它带原生程序作战、期满自毁 → 熔坑出现、堵路警告（反向）、由工作单填平后解除 → 三选一显示“可产生 N 种新交互” → 重复拿到同一作战组件只多一件库存 → 槽位封锁后程序重排并恢复（反向）（冻结桥随水面 FG18-SEA-01；模糊测试器常驻由 RunAll 覆盖）",
                Seed = TestSeed,
                TotalTimeoutSeconds = 1200,
                OnFinish = Cleanup,
                Version = 2,
                CheckpointAfter = new[] { "emg_fix", "squad_fix", "hj_end", "pit_fill", "loot_fix" },
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static string F(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);

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
            LootPickCatalog.ResetForTests();
            CombatKernel k = Home?.Kernel;
            if (k != null)
            {
                c.Log($"阶段 4 计数（家园内核全程）：劫持中 {k.HijackCount}、熔坑 {k.PitCount}（形成 {k.PitCounts.Born}、填平 {k.PitCounts.Filled}）、残骸 {k.WreckCount}；本进程劫持开始 {CombatSite.HijackStarted}、结束 {CombatSite.HijackEndedCount}");
            }
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM3Common.FrameReport("全程"));
            JourneyCommon.Cleanup(c);
        }

        private static int Unit(JourneyContext c, string key) => Home != null && Home.TryGetMachineUnit(c.GetInt(key), out int u) ? u : 0;

        private static CombatUnitView View(CombatKernel k, int unit) => k.TryGetUnit(unit, out CombatUnitView v) ? v : default;

        private static bool Alive(CombatKernel k, int unit) => k.TryGetUnit(unit, out CombatUnitView v) && v.Alive;

        // ── 夹具 ────────────────────────────────────────────────────────────────

        private static void ApplyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("fix.src", string.Empty).Length > 0)
            {
                return;
            }
            string where = FgjEmg1Journey.SourcesOf(s, Reboot, out bool obtainable);
            c.Set("fix.src", $"{Reboot}：{where}；可获得 {obtainable}");
            c.SetInt("fix.ok", obtainable && where != "无" ? 1 : 0);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(Reboot).Distinct().ToArray();
            s.Scrap = Math.Max(s.Scrap, SitePersistService.ClearScrap * 6 + 200);
        }

        private static StepOutcome TickFixture(JourneyContext c)
        {
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            if (c.GetInt("fix.ok") != 1)
            {
                return StepOutcome.Fail($"[重启注入] 在正常游戏里没有来源（{c.Get("fix.src")}）");
            }
            EmgCompiled cat = EmgCatalog.Current;
            if (cat.EffectiveStage < 4 || !EmgCatalog.IsOwnerOnDeclTrack(Reboot))
            {
                return StepOutcome.Fail($"正式表 emg.stage = {cat.RawStage}（生效 {cat.EffectiveStage}），阶段 4 出口应 ≥ 4；[重启注入] 走声明轨 {EmgCatalog.IsOwnerOnDeclTrack(Reboot)}");
            }
            return StepOutcome.Done($"正式表 emg.stage = {cat.RawStage}（生效 {cat.EffectiveStage}），[重启注入] 走声明轨；来源：{c.Get("fix.src")}");
        }

        private static bool OpenDisk(CampaignState s, Vector2 p, int r)
        {
            for (int x = -r; x <= r; x++)
            {
                for (int y = -r; y <= r; y++)
                {
                    int cx = Mathf.RoundToInt(p.x) + x, cy = Mathf.RoundToInt(p.y) + y;
                    if (!NavService.PassableNow(cx, cy, NavConst.ClassPlayer) || HomeGridService.BuildingAt(s, new GridCell(cx, cy)) != null)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// 找一处开阔地（覆盖里、离核心 36～120 格），世界按种子生成，位置都是现算（B25）。机器出厂在敌机朝核心 24 格（迎战半径 18 格外，要右键才开打）；
        /// 靶子在敌机背后 7 米（装甲机射程 9 米内；离停在射程上的 H 车 ≥ 18 米）。
        /// </summary>
        private static void ApplySquadFixture(JourneyContext c)
        {
            CampaignState s = St;
            CombatSite site = Home;
            if (s == null || site == null || c.GetInt("m.h") > 0)
            {
                return;
            }
            Vector2 core = FgjM8Journey.CorePos;
            Vector2 center = default, toCore = default;
            bool found = false;
            var why = new List<string>();
            for (int radius = 36; radius <= 120 && !found; radius += 8)
            {
                for (int deg = 0; deg < 360 && !found; deg += 15)
                {
                    var dir = new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad));
                    Vector2 p = core + dir * radius;
                    p = new Vector2(Mathf.Round(p.x), Mathf.Round(p.y));
                    Vector2 tc = (core - p).normalized;
                    Vector2 mp = p + tc * Standoff, dp = p - tc * DummyBehind;
                    string bad = !SignalCoverageService.Sample(HomeValleyLayout.RegionId, p).Covered || !SignalCoverageService.Sample(HomeValleyLayout.RegionId, mp).Covered ? "不在覆盖里"
                        : !OpenDisk(s, p, 3) ? "敌机处不开阔"
                        : !OpenDisk(s, mp, 3) ? "出厂点不开阔"
                        : !OpenDisk(s, dp, 1) ? "靶子处不开阔"
                        : !FgjEmg1Journey.Corridor(mp, p, out string block) ? "走廊不通 " + block
                        : null;
                    if (bad != null)
                    {
                        if (why.Count < 12)
                        {
                            why.Add($"{radius}格/{deg}°：{bad}");
                        }
                        continue;
                    }
                    center = p;
                    toCore = tc;
                    found = true;
                }
            }
            if (!found)
            {
                c.Set("squad.err", "找不到开阔地：" + string.Join("；", why));
                return;
            }
            string gun = ComponentCatalog.CompGunId, trk = HomeValleyLayout.Erc003ChassisId;
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(trk, gun, null, null, new[] { Reboot });
            var bp = new BlueprintRecord { BlueprintId = "emg4-h", DisplayName = "emg4-h", ActiveVersion = 1, Versions = new[] { board.ToVersion(1, 0f) } };
            s.BlueprintRecords = s.BlueprintRecords.Where(b => b.BlueprintId != bp.BlueprintId).Append(bp).ToArray();
            Vector2 start = center + toCore * Standoff;
            MachineOpResult r = MachineRegistry.SpawnMachine(trk, bp.BlueprintId, HomeValleyLayout.RegionId, start, SquadHp, SquadHp, "Player", 1, bp.Versions[0].CompileSignature);
            if (!r.Success)
            {
                c.Set("squad.err", "H 车出厂失败：" + r.Message);
                return;
            }
            MachineLoadoutRegistry.Register(s, r.LogicId, bp.BlueprintId, 1);
            c.SetInt("m.h", r.LogicId);
            c.Set("h.start", GetV(start));

            // 正式敌人表的装甲机：记录 + EnemySpawnBuilder 真实生成（武器 / 行为 / 抗性都按表）
            GameConfig.fg.MechEnemy row = EnemyDefs.Get(FoeType);
            var rec = new RegionEnemyRecord
            {
                EnemyInstanceId = "emg4-e2e-foe", RegionId = HomeValleyLayout.RegionId, EnemyTypeId = FoeType, Position = center, Health = row.MaxHp, MaxHealth = row.MaxHp, IsAlive = true,
            };
            s.RegionEnemies = (s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()).Where(e => e.EnemyInstanceId != rec.EnemyInstanceId).Append(rec).ToArray();
            if (!EnemySpawnBuilder.TryBuild(site, s, rec, RegionEnemySites.For(HomeValleyLayout.RegionId), out CombatSpawn sp))
            {
                c.Set("squad.err", "装甲机按敌人表生成失败");
                return;
            }
            int foe = site.SpawnEnemy(rec, sp);
            CombatKernel k = site.Kernel;
            c.SetInt("foe", foe);
            c.SetInt("foe.w0", View(k, foe).Weapon);
            c.Set("foe.at", GetV(center));
            Vector2 dp2 = center - toCore * DummyBehind;
            int dummy = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled,
                Position = new double2(dp2.x, dp2.y), Home = new double2(dp2.x, dp2.y), Radius = 0.5f, Speed = 0f, Health = DummyHp, MaxHealth = DummyHp,
                Weapon = -1, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFraction = 0f, ArmorFacing = new float2(0f, -1f),
            });
            c.SetInt("dummy", dummy);
            c.Set("dummy.at", GetV(dp2));
            c.Set("squad.ok", $"开阔地 ({F(center.x, "0")},{F(center.y, "0")})，离核心 {F(Vector2.Distance(core, center), "0")} 格");
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
            if (Unit(c, "m.h") == 0)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail("H 车没进家园内核");
            }
            CombatKernel k = Home.Kernel;
            int foe = c.GetInt("foe");
            CombatUnitView v = View(k, foe);
            if (foe <= 0 || !v.Alive || v.Faction != CombatFaction.Hostile || c.GetInt("foe.w0") < 0 || c.GetInt("dummy") <= 0)
            {
                return StepOutcome.Fail($"装甲机 / 靶子没生成好：装甲机 #{foe} 存活 {v.Alive}、阵营 {v.Faction}、武器 {c.GetInt("foe.w0")}；靶子 #{c.GetInt("dummy")}");
            }
            if (k.EffectiveResist(foe, EmgPhaseConst.ResHack) >= 1f || Home.UnhackableHoverLine(foe) != null)
            {
                return StepOutcome.Fail($"装甲机是杂兵却不可劫持：入侵抗性 {F(k.EffectiveResist(foe, EmgPhaseConst.ResHack), "0.##")}、悬停“{Home.UnhackableHoverLine(foe)}”");
            }
            if (FgjM2Common.IsAttacking(Home, c.GetInt("m.h")))
            {
                return StepOutcome.Fail("还没下令 H 车就打起来了");
            }
            return StepOutcome.Done($"H 车 #{c.GetInt("m.h")} 出厂（程序 [重启注入]）；装甲机 #{foe}（{EnemyDefs.NameOf(FoeType)}，耐久 {F(v.Health)}，原生武器 {c.GetInt("foe.w0")}，入侵抗性 {F(k.EffectiveResist(foe, EmgPhaseConst.ResHack), "0.##")}）；靶子 #{c.GetInt("dummy")}；{c.Get("squad.ok")}");
        }

        // ── 下令 ────────────────────────────────────────────────────────────────

        /// <summary>选中的机器右键目标下令攻击，每 2 秒最多点一次。返回“这台已在执行攻击命令”。</summary>
        private static bool OrderAttack(JourneyContext c, string machineKey, string targetKey)
        {
            int id = c.GetInt(machineKey);
            if (FgjM2Common.IsAttacking(Home, id))
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

        /// <summary>选中的机器右键地面下令移动（只点一次成功的）。返回“命令已发出”。</summary>
        private static bool OrderMove(JourneyContext c, string machineKey, Vector2 goal)
        {
            if (c.GetInt(FgjM3Common.SK(c, machineKey + ".sent")) == 1)
            {
                return true;
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) < 1500)
            {
                return false;
            }
            c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
            if (GameRoot.HomeValley.SelectedMachineLogicId != c.GetInt(machineKey))
            {
                FgjM1Journey.ClickMachine(c, machineKey);
            }
            else if (FgjM3Common.TryClick(goal, 1))
            {
                c.SetInt(FgjM3Common.SK(c, machineKey + ".sent"), 1);
            }
            return false;
        }

        // ── 1. 劫持 ─────────────────────────────────────────────────────────────

        private static StepOutcome TickHijack(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatKernel k = Home.Kernel;
            int foe = c.GetInt("foe");
            if (c.GetInt("hj.init") == 0)
            {
                c.SetInt("hj.init", 1);
                c.SetInt("hj.s0", CombatSite.HijackStarted);
                c.SetInt("hj.e0", CombatSite.HijackEndedCount);
            }
            CombatUnitView v = View(k, foe);
            float hack = k.AccumOf(foe, EmgPhaseConst.Hack);
            c.Set("hj.max", F(Math.Max(hack, float.Parse(c.Get("hj.max", "0"), CultureInfo.InvariantCulture)), "0.###"));
            if (v.Alive && v.Behavior == CombatBehavior.Hijacked)
            {
                if (!k.TryGetHijack(foe, out _, out float remaining, out int held))
                {
                    return StepOutcome.Fail("装甲机行为是劫持，但内核劫持表里没有它");
                }
                RegionEnemyRecord rec = St.RegionEnemies.FirstOrDefault(e => e.EnemyInstanceId == "emg4-e2e-foe");
                string hover = Home.HijackHoverLine(foe) ?? string.Empty;
                c.SetLong("hj.t0", (long)(k.Time * 1000));
                c.SetInt("hj.wreck0", k.WreckCount);
                int started = CombatSite.HijackStarted - c.GetInt("hj.s0");
                if (v.Faction != CombatFaction.Player || started != 1 || rec == null || !rec.IsAlive || !hover.Contains(GameText.Get("emg.hijack.hover.doctrine")) || k.HijackFlashed(foe))
                {
                    return StepOutcome.Fail($"劫持状态不对：阵营 {v.Faction}（应己方）、劫持开始通知 {started} 次（应 1）、敌人记录存活 {rec?.IsAlive}（被劫持不算阵亡）、改刷 {k.HijackFlashed(foe)}（没装劫持中继应不改刷）、悬停“{hover.Replace('\n', '｜')}”");
                }
                return StepOutcome.Done($"H 车命中写入侵度（最高 {c.Get("hj.max")}），致命一击时满阈值 → 装甲机重启为己方（耐久 {F(v.Health)} / {F(v.MaxHealth)}、剩余 {F(remaining)} 秒、持有原生程序 {held} 条）；敌人记录仍存活、劫持通知 1 次；悬停“{hover.Replace('\n', '｜')}”");
            }
            if (!v.Alive)
            {
                return StepOutcome.Fail($"装甲机被打死却没被劫持：入侵度最高 {c.Get("hj.max")}（满阈值 1）、劫持被拒（上限 {CombatSite.HijackRefusedCap} / 抗性 {CombatSite.HijackRefusedResist}）");
            }
            OrderAttack(c, "m.h", "foe");
            return c.StepElapsed < 140 ? StepOutcome.Wait
                : StepOutcome.Fail($"H 车 140 秒没打出劫持：装甲机耐久 {F(v.Health)}、入侵度 {F(hack, "0.###")}（最高 {c.Get("hj.max")}）、H 车在攻击 {FgjM2Common.IsAttacking(Home, c.GetInt("m.h"))}");
        }

        private static StepOutcome TickBack(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            Vector2 start = ParseV(c.Get("h.start")), dummy = ParseV(c.Get("dummy.at"));
            if (!OrderMove(c, "m.h", start))
            {
                return StepOutcome.Wait;
            }
            CombatKernel k = Home.Kernel;
            int foe = c.GetInt("foe");
            CombatUnitView v = View(k, foe);
            if (!v.Alive || v.Behavior != CombatBehavior.Hijacked)
            {
                return StepOutcome.Fail($"H 车退回途中装甲机已不在劫持中（存活 {v.Alive}、行为 {v.Behavior}）：劫持时长不够看完原生作战");
            }
            float d = Vector2.Distance(FgjM8Journey.PosOf(c.GetInt("m.h")), dummy);
            if (d < 20f)
            {
                return c.StepElapsed < 25 ? StepOutcome.Wait : StepOutcome.Fail($"H 车 25 秒没退到离靶子 20 米外（{F(d)} 米）");
            }
            c.Set("hj.dhp0", F(View(k, c.GetInt("dummy")).Health, "0.###"));
            return StepOutcome.Done($"H 车退到离靶子 {F(d)} 米（机枪射程 12 米外，此后靶子掉血只能来自被劫持的装甲机）；装甲机没跟着命令走（不接受编队命令）");
        }

        private static StepOutcome TickNativeFight(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatKernel k = Home.Kernel;
            int foe = c.GetInt("foe"), dummy = c.GetInt("dummy");
            CombatUnitView v = View(k, foe);
            float hp0 = float.Parse(c.Get("hj.dhp0"), CultureInfo.InvariantCulture), hp = View(k, dummy).Health;
            float d = Vector2.Distance(FgjM8Journey.PosOf(c.GetInt("m.h")), ParseV(c.Get("dummy.at")));
            if (d < 13f)
            {
                return StepOutcome.Fail($"H 车又回到靶子 {F(d)} 米内，掉血归属不清");
            }
            if (!v.Alive || v.Behavior != CombatBehavior.Hijacked)
            {
                return StepOutcome.Fail($"靶子掉血前装甲机已不在劫持中（存活 {v.Alive}）：靶子 {F(hp0)} → {F(hp)}");
            }
            if (v.Weapon != c.GetInt("foe.w0"))
            {
                return StepOutcome.Fail($"被劫持的装甲机武器变了：{v.Weapon}（原生 {c.GetInt("foe.w0")}）；没装劫持中继应带原生程序");
            }
            if (hp < hp0 - 0.01f) // H 车已在机枪射程外（上面断言），掉血只能来自被劫持的装甲机
            {
                return StepOutcome.Done($"被劫持的装甲机用原生武器（{v.Weapon}）打射程内的敌方靶子：靶子 {F(hp0)} → {F(hp)}（H 车在 {F(d)} 米外）");
            }
            return c.StepElapsed < 50 ? StepOutcome.Wait : StepOutcome.Fail($"被劫持的装甲机没打靶子：靶子 {F(hp0)} → {F(hp)}，开火冷却 {F(v.Cycle, "0.##")}");
        }

        private static StepOutcome TickExpire(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatKernel k = Home.Kernel;
            int foe = c.GetInt("foe");
            if (Alive(k, foe))
            {
                return c.StepElapsed < 55 ? StepOutcome.Wait : StepOutcome.Fail($"劫持 {F(k.Time - c.GetLong("hj.t0") / 1000.0)} 游戏秒还没自毁（应 30 秒）");
            }
            double lived = k.Time - c.GetLong("hj.t0") / 1000.0;
            RegionEnemyRecord rec = St.RegionEnemies.FirstOrDefault(e => e.EnemyInstanceId == "emg4-e2e-foe");
            int ended = CombatSite.HijackEndedCount - c.GetInt("hj.e0");
            int wrecks = k.WreckCount - c.GetInt("hj.wreck0");
            if (ended != 1 || rec == null || rec.IsAlive || lived < 29f || lived > 31.5f || wrecks < 1 || k.HijackCount != 0)
            {
                return StepOutcome.Fail($"期满自毁不对：劫持后 {F(lived, "0.##")} 游戏秒阵亡（应约 30）、劫持结束 {ended} 次（应 1）、敌人记录存活 {rec?.IsAlive}（应阵亡）、新残骸 {wrecks}、仍在劫持 {k.HijackCount}");
            }
            return StepOutcome.Done($"劫持后 {F(lived, "0.##")} 游戏秒期满自毁：劫持结束 1 次、敌人记录按击毁结算（阵亡）、留残骸 {wrecks} 个");
        }

        // ── 2. 熔坑 ─────────────────────────────────────────────────────────────

        private static bool MeltableCode(byte code) => GridContent.TerrainByCode(code)?.Meltable == 1;

        /// <summary>离核心 from～to 格、本格与 4 邻格都是可熔地面、能走、没有建筑、在覆盖里、且满足 <paramref name="extra"/> 的格（按到核心距离由近到远，确定性）。</summary>
        private static GridCell? FindMeltable(CampaignState s, int from, int to, Func<GridCell, bool> extra = null)
        {
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell core = HomeGridService.CorePivot(s);
            for (int r = from; r <= to; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var cell = new GridCell(core.X + dx, core.Y + dy);
                        bool ok = true;
                        foreach (GridCell n in new[] { cell, new GridCell(cell.X + 1, cell.Y), new GridCell(cell.X - 1, cell.Y), new GridCell(cell.X, cell.Y + 1), new GridCell(cell.X, cell.Y - 1) })
                        {
                            byte t = map.GetTerrain(n);
                            ok &= MeltableCode(t) && (GridContent.TerrainByCode(t)?.NavCost ?? 0) > 0 && map.OccupantAt(n) == null;
                        }
                        if (ok && NavService.PassableNow(cell.X, cell.Y, NavConst.ClassPlayer) && SignalCoverageService.Sample(HomeValleyLayout.RegionId, new Vector2(cell.X, cell.Y)).Covered
                            && (extra == null || extra(cell)))
                        {
                            return cell;
                        }
                    }
                }
            }
            return null;
        }

        // ── 堵路夹具（反向：永久改变堵路警告，FGR-EMG-154；与 FgEmgTerrainSelfCheck K 段同一几何，缺口改成正落在两块胶块中点）──

        /// <summary>围圈：仓库在熔坑格背离核心 <see cref="WallR"/> 格处，<see cref="WallN"/> 个等分点里跳过熔坑格两侧最近的两个（熔坑格正落在它们中点），
        /// 其余各放一块固化胶块（半径 1.5，相邻两块中心 1.95 米）。缺口两侧胶块离熔坑格 2.9 米：空着时 1 格宽能过，熔坑（半径 1.5）补上就堵死。</summary>
        private const int WallN = 16;

        private const double WallR = 5;

        /// <summary>围圈的圆心（熔坑格背离核心 5 格）；圆心周围 9×9 能走、没有建筑（放得下 5×3 的仓库，圈里通到缺口）。</summary>
        private static bool WallSite(CampaignState s, GridCell g, GridCell core, out double2 center)
        {
            double2 dir = math.normalizesafe(new double2(g.X - core.X, g.Y - core.Y), new double2(1, 0));
            center = new double2(g.X, g.Y) + dir * WallR;
            return OpenDisk(s, new Vector2((float)center.x, (float)center.y), 4);
        }

        private static void ApplyWallFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt("wall.set") == 1)
            {
                return;
            }
            c.SetInt("wall.set", 1);
            GridCell core = HomeGridService.CorePivot(s);
            GridCell? g = FindMeltable(s, 8, 60, x => WallSite(s, x, core, out _));
            if (g == null)
            {
                c.Set("pit.err", "离核心 8～60 格找不到覆盖里、背后 5 格开阔（放得下仓库和围圈）的可熔地面");
                return;
            }
            WallSite(s, g.Value, core, out double2 cc);
            FgjM3Common.SetCell(c, "pit.g", g.Value);
            BuildingRecord store = FgProductionSelfCheck.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "emg4-pit-store", new GridCell((int)Math.Round(cc.x), (int)Math.Round(cc.y)));
            c.Set("wall.store", store.BuildingId);
            CombatKernel k = Home.Kernel;
            double phi = Math.Atan2(g.Value.Y - cc.y, g.Value.X - cc.x);
            int n = 0;
            for (int i = 1; i < WallN - 1; i++) // i = 0 与 WallN − 1 是熔坑格两侧最近的两个点 = 缺口
            {
                double a = phi + (i + 0.5) * Math.PI * 2 / WallN;
                n += k.SpawnPatch(EmgMatterConst.Cured, new double2(cc.x + WallR * Math.Cos(a), cc.y + WallR * Math.Sin(a)), 1.5f) ? 1 : 0;
            }
            c.SetInt("wall.n", n);
            c.Set("wall.at", $"仓库 ({F(cc.x, "0")},{F(cc.y, "0")})、缺口 {FgjM3Common.Cell(g.Value)}");
        }

        private static PersistBlockRecord StoreBlock(JourneyContext c) =>
            SoftlockService.StateOf(St)?.PersistBlocks.FirstOrDefault(r => r != null && r.Key == c.Get("wall.store"));

        private static StepOutcome TickWallOpen(JourneyContext c)
        {
            if (c.Get("pit.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail(c.Get("pit.err"));
            }
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.GetInt("wall.n") != WallN - 2)
            {
                return StepOutcome.Fail($"固化胶块只生成了 {c.GetInt("wall.n")} 块（应 {WallN - 2}）");
            }
            if (c.StepElapsed < 9) // 堵路检查每 10 游戏秒一次（3 倍速约 3.3 秒），等过两次
            {
                return StepOutcome.Wait;
            }
            PersistBlockRecord rec = StoreBlock(c);
            return rec != null
                ? StepOutcome.Fail($"缺口还开着就报了堵路（种类 {rec.Kind}、位置 ({F(rec.X)},{F(rec.Y)})）：围圈缺口太窄")
                : StepOutcome.Done($"{c.Get("wall.at")}；{WallN - 2} 块固化胶块围圈，缺口开着时不报堵路（堵路记录 {SoftlockService.StateOf(St).PersistBlocks.Length} 条，没有这座仓库）");
        }

        private static StepOutcome TickBlockWarn(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            PersistBlockRecord rec = StoreBlock(c);
            if (rec == null)
            {
                return c.StepElapsed < 35 ? StepOutcome.Wait : StepOutcome.Fail($"熔坑补上缺口 35 秒没报堵路（堵路记录 {SoftlockService.StateOf(s).PersistBlocks.Length} 条，熔坑 {Home.Kernel.PitCount} 个）");
            }
            string key = "persist:" + c.Get("wall.store");
            HomeValleyAlarms.AlertRecord alarm = HomeValleyAlarms.Collect(s).FirstOrDefault(a => a.Key == key);
            if (rec.Kind != EmgPersistItem.KindPit || !rec.Blocks || alarm.Key == null || !alarm.HasLocation)
            {
                return StepOutcome.Fail($"堵路记录不对：点名种类 {rec.Kind}（应熔坑 {EmgPersistItem.KindPit}）、阻路 {rec.Blocks}；告警栏这一行 {alarm.Key != null}、可定位 {alarm.HasLocation}");
            }
            if (!FgjM3Common.Done(c, "clk"))
            {
                bool? clicked = FgjM6Common.ClickToast(SitePersistService.NotifyBlocked, out NotificationEntry e);
                if (clicked == null)
                {
                    return c.StepElapsed < 40 ? StepOutcome.Wait : StepOutcome.Retry("堵路通知弹出条没出现 / 已经收起");
                }
                if (clicked == false)
                {
                    return StepOutcome.Retry("点不到堵路通知弹出条：" + JourneyInput.LastUiFailure);
                }
                c.Set(FgjM3Common.SK(c, "text"), e.Text ?? string.Empty);
                FgjM3Common.Mark(c, "clk");
                c.SetLong(FgjM3Common.SK(c, "at"), NowMs());
                return StepOutcome.Wait;
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 1500)
            {
                return StepOutcome.Wait;
            }
            string text = c.Get(FgjM3Common.SK(c, "text"));
            float2 f = WorldView.Director.StrategyFocus;
            float d = Vector2.Distance(new Vector2(f.x, f.y), new Vector2(rec.X, rec.Y));
            if (!text.Contains("熔坑") || d >= 8f)
            {
                return StepOutcome.Fail($"点了堵路通知弹出条：文字“{text}”（应写熔坑与位置）、镜头中心 ({F(f.x, "0")},{F(f.y, "0")}) 离熔坑 ({F(rec.X)},{F(rec.Y)}) {F(d)} 格（应 < 8）");
            }
            return StepOutcome.Done($"熔坑补上缺口 → 堵路警告“{text}”、告警栏“{alarm.RowText}”；点弹出条镜头飞到熔坑（镜头中心离它 {F(d)} 格）");
        }

        private static StepOutcome TickBlockClear(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            string key = "persist:" + c.Get("wall.store");
            bool rec = StoreBlock(c) != null, alarm = HomeValleyAlarms.Collect(St).Any(a => a.Key == key);
            if (rec || alarm)
            {
                return c.StepElapsed < 35 ? StepOutcome.Wait : StepOutcome.Fail($"熔坑填平 35 秒后堵路还在：记录 {rec}、告警栏 {alarm}");
            }
            return StepOutcome.Done("熔坑填平后下一次检查：缺口重新打通，这座仓库的堵路警告与告警栏那一行都解除");
        }

        private static void ApplyPitFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt("pit.set") == 1 || c.Get("pit.err", string.Empty).Length > 0)
            {
                return;
            }
            GridCell g = FgjM3Common.GetCell(c, "pit.g"); // 围圈缺口那一格（wall_fix 选定）
            c.SetInt("pit.set", 1);
            c.SetInt("pit.born0", Home.Kernel.PitCounts.Born);
            Home.Kernel.SpawnPatch(EmgMatterConst.Molten, new double2(g.X, g.Y), 1f, 35f);
        }

        private static int PitNear(CombatKernel k, GridCell g)
        {
            for (int i = 0; i < k.PitCount; i++)
            {
                EmgPit p = k.PitAt(i);
                if (math.distance(p.Pos, new double2(g.X, g.Y)) < 0.6)
                {
                    return p.Id;
                }
            }
            return 0;
        }

        private static StepOutcome TickPit(JourneyContext c)
        {
            if (c.Get("pit.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail(c.Get("pit.err"));
            }
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            CombatKernel k = Home.Kernel;
            GridCell g = FgjM3Common.GetCell(c, "pit.g");
            int id = PitNear(k, g);
            if (id == 0)
            {
                return c.StepElapsed < 55 ? StepOutcome.Wait : StepOutcome.Fail($"熔融金属凝固后没成坑：熔坑 {k.PitCount} 个（形成 {k.PitCounts.Born}、地面外 {k.PitCounts.OffGround}、上限 {k.PitCounts.Capped}），累计点 {k.PitAccCount}");
            }
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait; // 寻路镜像按阻路签名下一步转发
            }
            c.SetInt("pit.id", id);
            GridPlacementResult place = HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeWarehouse, g, 0);
            string why = place.Reasons.Where(r => r.Code == GridBlockReason.MeltPit).Select(r => r.Describe()).FirstOrDefault() ?? string.Empty;
            bool pass = NavService.PassableNow(g.X, g.Y, NavConst.ClassPlayer);
            if (pass || !why.Contains("熔坑"))
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail($"熔坑 #{id} 出现了，但这一格可通行 {pass}、建造原因“{why}”");
            }
            return StepOutcome.Done($"可熔地面 {FgjM3Common.Cell(g)} 上熔融金属凝固 → 熔坑 #{id}（形成 {k.PitCounts.Born - c.GetInt("pit.born0")} 个）：不可通行；建造被拒“{why}”");
        }

        private static StepOutcome TickClearTool(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            HomeValleyBuildMode m = FgjM3Common.Mode;
            return m != null && m.ClearSiteActive ? StepOutcome.Done("“清除场地物”工具选中") : StepOutcome.Retry("点了“清除场地物”工具没选中：" + FgjM1Journey.UiFail(c));
        }

        private static StepOutcome TickClearClick(JourneyContext c)
        {
            CampaignState s = St;
            GridCell g = FgjM3Common.GetCell(c, "pit.g");
            string target = SitePersistService.TargetOf(EmgPersistItem.KindPit, c.GetInt("pit.id"));
            WorkOrderRecord o = SitePersistService.ActiveOrder(s, target);
            if (o != null)
            {
                string desc = HomeValleyWorkOrders.DescribeTarget(s, o);
                return desc.Contains("熔坑")
                    ? StepOutcome.Done($"左键点熔坑格 {FgjM3Common.Cell(g)}：开清除工作单 {o.WorkOrderId}（{o.Kind}），工单目标“{desc}”，废料 {c.GetLong("pit.scrap0")} → {s.Scrap}")
                    : StepOutcome.Fail($"开了工作单但目标没写熔坑：“{desc}”");
            }
            if (c.GetInt(FgjM3Common.SK(c, "clicked")) == 1)
            {
                // 只点一次：同一格再点一次 = 取消（B03）
                return c.StepElapsed < 6 ? StepOutcome.Wait : StepOutcome.Retry("点熔坑格后没开工作单：" + FgjM1Journey.UiFail(c));
            }
            if (JourneyInput.Holding)
            {
                return StepOutcome.Wait;
            }
            c.SetLong("pit.scrap0", s.Scrap);
            if (FgjM3Common.TryClick(FgjM3Common.Ground(g), 0))
            {
                c.SetInt(FgjM3Common.SK(c, "clicked"), 1);
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickPitFilled(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            CombatKernel k = Home.Kernel;
            GridCell g = FgjM3Common.GetCell(c, "pit.g");
            int id = c.GetInt("pit.id");
            if (k.TryGetPersist(EmgPersistItem.KindPit, id, out _))
            {
                WorkOrderRecord o = SitePersistService.ActiveOrder(s, SitePersistService.TargetOf(EmgPersistItem.KindPit, id));
                return c.StepElapsed < 230 ? StepOutcome.Wait : StepOutcome.Fail($"熔坑 #{id} 230 秒没填平：工作单 {(o != null ? o.WorkOrderId + " " + o.Kind : "没了")}");
            }
            bool pass = NavService.PassableNow(g.X, g.Y, NavConst.ClassPlayer);
            bool build = !HomeGridService.ValidatePlacement(s, HomeValleyLayout.BuildingTypeWarehouse, g, 0).Has(GridBlockReason.MeltPit);
            if (!pass || !build)
            {
                return c.StepElapsed < 230 ? StepOutcome.Wait : StepOutcome.Fail($"熔坑 #{id} 没了，但这一格可通行 {pass}、建造不再报熔坑 {build}");
            }
            return StepOutcome.Done($"工程机领单填平熔坑 #{id}（填平计数 {k.PitCounts.Filled}）：{FgjM3Common.Cell(g)} 又能走、又能建造");
        }

        // ── 3. 三选一 ───────────────────────────────────────────────────────────

        private static LootCandidateRecord FwCand(string id) =>
            new LootCandidateRecord { Kind = LootPickCatalog.KindFirmware, ItemId = AnalysisCatalog.EncryptedFirmwareId, TargetId = id, Amount = 1, Tier = "common" };

        private static void ApplyLootFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("lp.id", string.Empty).Length > 0)
            {
                return;
            }
            LootPickDef dup = LootPickCatalog.Rows.FirstOrDefault(r => r.Kind == LootPickCatalog.KindModule && MechanicalContentFacade.TryGet(r.Target, out MechanicalContentDef d)
                                                                       && (d.Category == MechanicalContentCategory.MainComponent || d.Category == MechanicalContentCategory.FunctionComponent));
            if (dup == null)
            {
                c.Set("lp.err", "三选一表里没有作战组件候选");
                return;
            }
            // 仓库修好（新局的仓库是受损的、容量 0；修建筑由 FGJ-M1 覆盖），否则重复部件按“仓库已满”拆成技术数据（那条由 FgEmgLootHintSelfCheck C5 覆盖）
            FgLootPickSelfCheck.EnsureWarehouse(s);
            // 已掌握这件作战组件（以前解析过；第一次获得送解析台由 FGJ-M8 覆盖）
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(dup.Target).Distinct().ToArray();
            List<LootPickDef> fws = LootPickCatalog.Rows.Where(r => r.Kind == LootPickCatalog.KindFirmware && (r.Pool == dup.Pool || r.Pool == LootPickCatalog.PoolAny)
                                                                    && !MechanicalContentUnlock.IsUnlocked(s, r.Target))
                .OrderByDescending(r => LootInteractionHint.For(s, new LootPackageRecord { PacketId = "probe", Candidates = new[] { FwCand(r.Target) } })[0].New > 0 ? 1 : 0)
                .ThenBy(r => r.SortOrder).Take(2).ToList();
            if (fws.Count < 2)
            {
                c.Set("lp.err", $"作战组件 {dup.Target} 的池 {dup.Pool} 里固件候选不足 2 条");
                return;
            }
            var rows = new List<LootPickDef> { dup, fws[0], fws[1] };
            foreach (LootPickDef r in rows)
            {
                r.TierIndex = 0;
                r.Tier = "common";
            }
            c.SetInt("lp.cc0", HomeInventory.Stock(s, "combat_component"));
            c.SetInt("lp.mod0", HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId));
            c.SetInt("lp.dup0", LootPickService.DuplicatesStocked);
            c.Set("lp.target", dup.Target);
            c.Set("lp.row", dup.Id);
            // 数据包由家园里一台在核心 / 仓库旁的机器带着（带回家的精英数据包，同 FGJ-M8 回家后的分拣）；放地上要先派机器捡（loot.pick.reason.ground）
            MachineRecord holder = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && LootPickService.AtDepot(s, m)).OrderBy(m => m.LogicId).FirstOrDefault();
            if (holder == null)
            {
                c.Set("lp.err", "家园里没有一台在核心 / 仓库旁的机器能带数据包");
                return;
            }
            c.SetInt("lp.holder", holder.LogicId);
            LootPickCatalog.OverrideForTests(rows);
            LootPackageRecord p;
            try
            {
                p = holder != null ? LootPickService.Grant(s, LootPickService.SourceElite, dup.Pool == LootPickCatalog.PoolAny ? "silent" : dup.Pool, holder.LogicId, HomeValleyLayout.RegionId, FgjM8Journey.PosOf(holder.LogicId)) : null;
            }
            finally
            {
                LootPickCatalog.ResetForTests();
            }
            c.Set("lp.id", p?.PacketId ?? string.Empty);
        }

        private static StepOutcome TickLootFixture(JourneyContext c)
        {
            if (c.Get("lp.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail(c.Get("lp.err"));
            }
            LootPackageRecord p = LootPickService.Find(St, c.Get("lp.id"));
            if (p == null)
            {
                return StepOutcome.Fail("精英数据包没生成");
            }
            int i = Array.FindIndex(p.Candidates, x => x.RowId == c.Get("lp.row"));
            if (i < 0 || p.Candidates.Length != 3)
            {
                return StepOutcome.Fail($"数据包候选不对（{p.Candidates.Length} 个，作战组件行在 {i}）");
            }
            c.SetInt("lp.idx", i);
            return StepOutcome.Done($"精英数据包 {p.PacketId}：{string.Join("、", p.Candidates.Select(LootPickService.CandidateName))}（第 {i + 1} 张是已掌握的作战组件 {c.Get("lp.target")}）");
        }

        private static StepOutcome TickPickOpen(JourneyContext c)
        {
            CampaignState s = St;
            if (!LootPickPanelUIToolkit.IsOpen)
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, "openAt")) > 1500)
                {
                    c.SetLong(FgjM3Common.SK(c, "openAt"), NowMs());
                    FgjM1Journey.ClickUi(c, "[WorldBarHost]", "WorldPacketCount");
                }
                return c.StepElapsed > 12 ? StepOutcome.Fail("点顶栏计数后待分拣面板没开：" + FgjM1Journey.UiFail(c)) : StepOutcome.Wait;
            }
            LootPickPanelUIToolkit panel = LootPickPanelUIToolkit.Instance;
            if (c.StepElapsed < 1.0 || panel == null || string.IsNullOrEmpty(panel.CurrentPacketId))
            {
                return StepOutcome.Wait;
            }
            if (panel.CurrentPacketId != c.Get("lp.id"))
            {
                return StepOutcome.Fail($"面板打开的是数据包 {panel.CurrentPacketId}，不是夹具的 {c.Get("lp.id")}（待分拣 {LootPickService.Count(s)} 个）");
            }
            LootPackageRecord p = LootPickService.Find(s, panel.CurrentPacketId);
            LootInteractionHint.Hint[] hs = LootInteractionHint.For(s, p);
            int dup = c.GetInt("lp.idx");
            var lines = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                string want = LootInteractionHint.LineOf(hs[i]), shown = panel.CardHint(i);
                if (shown != want || shown.Length == 0)
                {
                    return StepOutcome.Fail($"第 {i + 1} 张卡片提示行“{shown}”，应为“{want}”");
                }
                lines.Add($"{panel.CardName(i)}：{shown.Split('\n')[0]}");
            }
            if (!Enumerable.Range(0, 3).Any(i => hs[i].New > 0 && panel.CardHint(i).StartsWith("与你现有的固件 / 部件可产生 " + hs[i].New.ToString(CultureInfo.InvariantCulture) + " 种新交互", StringComparison.Ordinal)))
            {
                return StepOutcome.Fail("没有一张卡片写“可产生 N 种新交互”（N > 0）：" + string.Join("；", lines));
            }
            string cmp = panel.CardCompare(dup);
            if (!cmp.Contains("重复获得只加库存") || cmp.Contains("仓库已满") || !panel.ChooseEnabled(dup))
            {
                return StepOutcome.Fail($"重复作战组件卡片对比行“{cmp.Replace('\n', '｜')}”（应写“重复获得只加库存”、仓库放得下）、能选 {panel.ChooseEnabled(dup)}（原因“{panel.ReasonText}”、去向“{panel.WhereText}”）");
            }
            return StepOutcome.Done($"待分拣面板打开数据包 {p.PacketId}：{string.Join("；", lines)}；第 {dup + 1} 张对比行“{cmp.Split('\n')[0]}”");
        }

        private static StepOutcome TickPickDup(JourneyContext c)
        {
            CampaignState s = St;
            LootPickPanelUIToolkit panel = LootPickPanelUIToolkit.Instance;
            string id = c.Get("lp.id");
            if (LootPickService.Find(s, id) != null)
            {
                if (c.GetInt(FgjM3Common.SK(c, "chose")) == 0)
                {
                    if (!JourneyInput.ClickUitk("[LootPickPanelHost]", "LootPickChoose" + c.GetInt("lp.idx").ToString(CultureInfo.InvariantCulture)))
                    {
                        return c.StepElapsed < 5 ? StepOutcome.Wait : StepOutcome.Fail("点“选这个”失败：" + JourneyInput.LastUiFailure);
                    }
                    c.SetInt(FgjM3Common.SK(c, "chose"), 1);
                }
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Fail($"点“选这个”后数据包 {id} 还在（反馈“{panel?.MessageText}”）");
            }
            int cc = HomeInventory.Stock(s, "combat_component"), mod = HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId);
            int stocked = LootPickService.DuplicatesStocked - c.GetInt("lp.dup0");
            if (cc != c.GetInt("lp.cc0") + 1 || mod != c.GetInt("lp.mod0") || stocked != 1)
            {
                return StepOutcome.Fail($"重复作战组件入库不对：作战组件库存 {c.GetInt("lp.cc0")} → {cc}（应 +1）、未解析模块 {c.GetInt("lp.mod0")} → {mod}（应不变）、重复入库计数 +{stocked}");
            }
            return StepOutcome.Done($"选了重复的 {c.Get("lp.target")}：作战组件库存 {c.GetInt("lp.cc0")} → {cc}（多一台车能装），未解析模块不变（{mod}），反馈“{panel?.MessageText}”");
        }

        private static StepOutcome TickPickClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return !LootPickPanelUIToolkit.IsOpen ? StepOutcome.Done("Esc 关闭待分拣面板") : StepOutcome.Retry("面板还开着");
        }

        // ── 4. 反向：槽位封锁 → 程序重排 → 恢复（FGT-EMG-061 / 010；与 FgEmgSeizeSelfCheck H 段同一组固件）──

        private const string CoreTrig = "fw_split", CoreNext = "fw_homing";

        private const int Ember = SignalSeizeService.SourceEmber;

        /// <summary>接入程序（信号核按槽位顺序、只含生效且没被封锁的槽，与接入结算 / 双态预览同一输入 ActiveContentIds）。</summary>
        private static EmgProgramResult CoreProgram(CampaignState s) =>
            EmgProgramCompiler.Compile(SignalCoreService.ActiveContentIds(s).Where(x => x.Length > 0).ToArray(), Array.Empty<string>(), FirmwareCarrier.Projectile);

        private static string Tree(EmgProgramResult r) =>
            string.Join(" | ", r.Nodes.Select(n => $"{n.Gen}代:" + string.Join("+", n.Entries.Select(e => r.Steps.First(st => st.Ordinal == e.Ordinal).FirmwareId.Replace("fw_", string.Empty)))));

        private static string LastNote(string typeId) => NotificationCenter.History.LastOrDefault(e => e.Type?.Id == typeId)?.Text ?? string.Empty;

        private static string HudSlotLine => GameText.Format("emg.seize.hud.slot", "1", SignalSeizeService.SourceName(Ember));

        private static void ApplySlotFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt("sb.set") == 1)
            {
                return;
            }
            c.SetInt("sb.set", 1);
            string[] fw = { CoreTrig, CoreNext };
            for (int i = 0; i < fw.Length; i++)
            {
                string part = PrimitiveInventory.AddBurnedChip(s, fw[i]);
                SignalCoreResult r = part != null ? SignalCoreService.TryEquip(s, part, i) : default;
                if (part == null || !r.Success)
                {
                    c.Set("sb.err", $"{fw[i]} 装进 {i + 1} 号槽失败（芯片 {part ?? "没入库"}：{r.Message}）");
                    return;
                }
            }
            EmgProgramResult p = CoreProgram(s);
            c.Set("sb.tree0", Tree(p));
            c.Set("sb.sig0", p.Signature);
        }

        private static StepOutcome TickSlotFixture(JourneyContext c)
        {
            if (c.Get("sb.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail(c.Get("sb.err"));
            }
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            string tree = c.Get("sb.tree0");
            return tree == "0代:split | 1代:homing"
                ? StepOutcome.Done($"信号核 1 号槽 [分裂]、2 号槽 [寻的]；接入程序 {tree}（分裂是触发器，寻的在它放出的子弹药一代）")
                : StepOutcome.Fail($"封锁前接入程序应为“0代:split | 1代:homing”，实际“{tree}”");
        }

        private static void ApplySlotBlock(JourneyContext c)
        {
            if (St == null || c.GetInt("sb.blocked") != 0)
            {
                return;
            }
            c.SetInt("sb.n0", SignalSeizeService.SlotBlockedCount);
            c.SetInt("sb.blocked", SignalSeizeService.BlockSlot(St, 0, Ember) ? 1 : -1);
        }

        private static StepOutcome TickSlotBlocked(JourneyContext c)
        {
            CampaignState s = St;
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            string note = LastNote("emg_slot_blocked"), hud = SignalCoreHudUIToolkit.Instance?.UplinkStatusText ?? string.Empty;
            bool ok = c.GetInt("sb.blocked") == 1 && SignalSeizeService.SlotBlockedCount == c.GetInt("sb.n0") + 1 && SignalSeizeService.IsBlocked(s, 0)
                      && SignalCoreService.ActiveContentIds(s)[0].Length == 0 && SignalCoreService.SlotContentId(s, 0) == CoreTrig
                      && note.Contains(GameText.Get("emg.seize.source.ember")) && hud.Contains(HudSlotLine);
            if (!ok)
            {
                return c.StepElapsed < 5 ? StepOutcome.Wait
                    : StepOutcome.Fail($"封锁 1 号槽不对：入口返回 {c.GetInt("sb.blocked")}、封锁 {SignalSeizeService.IsBlocked(s, 0)}、生效列表 1 号槽“{SignalCoreService.ActiveContentIds(s)[0]}”（应空）、固件仍在槽里 {SignalCoreService.SlotContentId(s, 0) == CoreTrig}；通知“{note}”；HUD“{hud.Replace('\n', '｜')}”（应含“{HudSlotLine}”）");
            }
            return StepOutcome.Done($"余烬主机封锁 1 号槽：通知“{note}”；HUD“{hud.Replace('\n', '｜')}”；固件仍在槽里、生效列表按空槽");
        }

        private static StepOutcome TickSlotPanel(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            if (!SignalCoreHudUIToolkit.IsOpen || hud == null || !hud.PanelVisible)
            {
                return StepOutcome.Retry("信号核面板没打开：" + FgjM1Journey.UiFail(c));
            }
            string slot = hud.SlotText(0), tree = Tree(CoreProgram(St));
            string reason = GameText.Format("emg.seize.slot.reason", SignalSeizeService.SourceName(Ember)), trig = GameText.Get("emg.seize.slot.trigger_note");
            if (!slot.Contains(reason) || !slot.Contains(trig) || hud.SlotText(1).Contains(reason))
            {
                return c.StepElapsed < 4 ? StepOutcome.Wait : StepOutcome.Fail($"面板 1 号槽“{slot.Replace('\n', '｜')}”应写“{reason}{trig}”；2 号槽“{hud.SlotText(1).Replace('\n', '｜')}”不应写封锁");
            }
            if (tree != "0代:homing")
            {
                return StepOutcome.Fail($"封锁触发器后接入程序应按空槽重排为“0代:homing”（下游回到上一层节点），实际“{tree}”（封锁前 {c.Get("sb.tree0")}）");
            }
            c.Set("sb.tree1", tree);
            return StepOutcome.Done($"信号核面板 1 号槽“{slot.Replace('\n', '｜')}”；接入程序重排 {c.Get("sb.tree0")} → {tree}");
        }

        private static void ApplySlotLift(JourneyContext c)
        {
            if (St == null || c.GetInt("sb.lifted") != 0)
            {
                return;
            }
            c.SetInt("sb.r0", SignalSeizeService.SlotRestoredCount);
            c.SetInt("sb.lifted", SignalSeizeService.UnblockAll(St, Ember) + 1); // +1：0 = 还没调
        }

        private static StepOutcome TickSlotLifted(JourneyContext c)
        {
            CampaignState s = St;
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            string slot = hud?.SlotText(0) ?? string.Empty, status = hud?.UplinkStatusText ?? string.Empty, note = LastNote("emg_slot_restored");
            EmgProgramResult p = CoreProgram(s);
            bool ok = c.GetInt("sb.lifted") - 1 == 1 && SignalSeizeService.SlotRestoredCount == c.GetInt("sb.r0") + 1 && !SignalSeizeService.IsBlocked(s, 0)
                      && SignalCoreService.ActiveContentIds(s)[0] == CoreTrig && note.Contains(GameText.Get("emg.seize.source.ember"))
                      && !slot.Contains(GameText.Get("emg.seize.slot.trigger_note")) && !status.Contains(HudSlotLine);
            if (!ok)
            {
                return c.StepElapsed < 5 ? StepOutcome.Wait
                    : StepOutcome.Fail($"解除不对：解除 {c.GetInt("sb.lifted") - 1} 槽（应 1）、仍封锁 {SignalSeizeService.IsBlocked(s, 0)}、生效列表 1 号槽“{SignalCoreService.ActiveContentIds(s)[0]}”；通知“{note}”；面板 1 号槽“{slot.Replace('\n', '｜')}”；HUD“{status.Replace('\n', '｜')}”");
            }
            if (Tree(p) != c.Get("sb.tree0") || p.Signature != c.Get("sb.sig0"))
            {
                return StepOutcome.Fail($"解除后接入程序应与封锁前同签名：{Tree(p)}（{p.Signature}），封锁前 {c.Get("sb.tree0")}（{c.Get("sb.sig0")}）");
            }
            return StepOutcome.Done($"按来源解除：通知“{note}”；面板 1 号槽“{slot.Replace('\n', '｜')}”；接入程序恢复 {Tree(p)}（与封锁前同签名）");
        }
    }
}
