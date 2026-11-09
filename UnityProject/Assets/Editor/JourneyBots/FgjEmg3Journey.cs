using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.UI.SignalCore;
using GameLogic.View;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// EMG3-E2E-01：FG-EMG 阶段 3 出口旅程 FGJ-EMG-3（FG19 第 16 节；LANDING-PLAN 6.3）。从主菜单“新建”出发（固定测试种子 42、标准难度），全程正式输入，
    /// RTS 口径：左键选择、右键敌人 = 攻击、右键地面 = 移动；接入后直控左键开火。
    ///
    /// 原文：履带车把燃烧区拖成火线；悬浮车跨过燃烧区不受伤；中继机器在友机命中时放电；两台节拍机器相位对齐打出齐射；
    /// [若目标被标记, 复制, 分裂] 命中已标记目标放两组子弹药。卡片另要 [条件门·标记, 连锁]（元程序 + 旧数值动词）的行为验收。
    ///
    /// 轻量口径（用户 2026-10-12）：编成层每个新系统被真实输入触发一次并断言结果。夹具写在步骤标题里：<see cref="ApplyFixture"/>（正式固件记为已获得，先断言每条在正常游戏里有来源）、
    /// <see cref="ApplySquadFixture"/>（蓝图与机器直接出厂（编辑器与出厂由 FGJ-EMG-1 / FGJ-M1 覆盖）+ 不动不还手的敌方靶子 + 一块燃烧区（爆燃成燃烧区由 FGJ-EMG-2 覆盖）；规则结果全走内核）。
    /// 反向（中继限流、元程序上限的原因文本）按轻量口径 b 由单段自检覆盖（B 段）。
    /// 用法：bash tools/unity-journey.sh FGJ-EMG-3；断点 emg_fix / sc_close / squad_fix / hover / relay / volley / chain（--from 续跑）。
    /// </summary>
    public static class FgjEmg3Journey
    {
        public const string Id = "FGJ-EMG-3";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private const string Split = "fw_split", Arc = "fw_arcchain", IfMark = "fw_ifmark", Repeat = "fw_repeat", Clock = "fw_clock", Volley = "fw_volley";

        /// <summary>夹具机器耐久（标准 100）：履带车要穿过燃烧区，本旅程不验平衡（FG15-BAL-01）。</summary>
        private const float SquadHp = 500f;

        /// <summary>燃烧区（夹具）半径 / 份量 / 存续秒。</summary>
        private const float FireR = 3f, FireAmount = 6f, FireSeconds = 150f;

        private static readonly string[] Team = { "m.t", "m.v", "m.a", "m.b", "m.x", "m.y", "m.k", "m.l", "m.m" };

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
                S("emg_fix", "进度夹具：正式表 emg.stage ≥ 3（阶段 3 生效）；旅程用到的正式固件 / 部件记为已获得（先断言每条在正常游戏的掉落 / 候选池里有来源）", 15, ApplyFixture, TickFixture),

                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("sel_worker", "左键点一台工程机", 15, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 2),
                S("repair_gen", "右键点受损的发电机（刻印芯片要装配站有电）", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("wait_gen", "等发电机修好（3 倍速）", 300, null, FgjEmg1Journey.TickGeneratorDone),
                S("sc_open", "按信号核键（默认 P）打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
                S("sc_print", "刻印下拉选“条件门·标记”，点“刻印”", 10, c => FgjEmg1Journey.PrintFirmware(c, IfMark), c => FgjEmg1Journey.TickPrinted(c, IfMark), retries: 1),
                S("sc_equip", "点“装入 1 号槽”", 10, c => ClickSlotThenEquip(c, 0), c => TickEquipped(c, IfMark, 0), retries: 1),
                S("sc_print2", "刻印下拉选“双发指令”（复制），点“刻印”", 10, c => FgjEmg1Journey.PrintFirmware(c, Repeat), c => FgjEmg1Journey.TickPrinted(c, Repeat), retries: 1),
                S("sc_equip2", "点 2 号槽，再点“装入 2 号槽”：接入程序 = [条件门·标记, 双发指令] + 机器固件", 10, c => ClickSlotThenEquip(c, 1), c => TickEquipped(c, Repeat, 1), retries: 1),
                S("sc_close", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),

                S("squad_fix", "出厂夹具：T 履带（ERC-003）/ V 悬浮 / A 机枪无固件 / B 中继 + [分裂, 电弧] / X 中继 + [时钟脉冲, 齐射同步] / Y [时钟脉冲, 齐射同步] / K 标记器 / L [条件门·标记, 电弧] / M [分裂] 带接入口各出厂一台；" +
                    "4 处开阔地：燃烧区一块（夹具，爆燃成区由 FGJ-EMG-2 覆盖）+ 3 处不动不还手的敌方靶子", 20, ApplySquadFixture, TickSquadFixture),

                // ── 1. 底盘：履带拖火线、悬浮跨燃烧区 ──
                S("sel_t", "左键点 T 车（履带）", 15, c => FgjM1Journey.ClickMachine(c, "m.t"), c => FgjM1Journey.TickSelected(c, "m.t"), retries: 2),
                S("track", "右键燃烧区对面的地面（移动）：T 车碾过燃烧区，身后每 1.5 米分出一块明火，连成火线；着地挨烧掉血", 120, null, TickTrack),
                S("sel_v", "左键点 V 车（悬浮）", 15, c => FgjM1Journey.ClickMachine(c, "m.v"), c => FgjM1Journey.TickSelected(c, "m.v"), retries: 2),
                S("hover", "右键燃烧区对面的地面（移动）：V 车跨过燃烧区与火线，耐久不掉、身上不记明火", 120, null, TickHover),

                // ── 2. 中继：A 车命中 → B 车（装中继，没下令）放电 ──
                S("sel_a", "左键点 A 车（机枪，没有固件）", 15, c => FgjM1Journey.ClickMachine(c, "m.a"), c => FgjM1Journey.TickSelected(c, "m.a"), retries: 2),
                S("relay", "右键靶子 1（攻击）：A 车每次命中，旁边装中继的 B 车（没接到命令）也执行“命中时 [分裂, 电弧]”：放出带电弧的子弹药、靶子带电；因果链写“由中继转发：A 车命中”", 120, null, TickRelay),

                // ── 3. 节拍：两台节拍机器相位对齐 → 齐射 ──
                S("sel_xy", "左键点 X 车（中继 + [时钟脉冲, 齐射同步]），Shift 左键加选 Y 车（[时钟脉冲, 齐射同步]）", 30, null, TickSelectPair, retries: 2),
                S("volley", "右键靶子 2（攻击）：两台一起开到射程开火，X、Y 各一条同相位的时钟脉冲在同一拍执行 → 中继组相位对齐（tmp.sync），齐射同步向最近的两个敌人放子弹药", 120, null, TickVolley),

                // ── 4. 元程序：[条件门·标记, 电弧（连锁）] / [条件门·标记, 双发指令, 分裂] ──
                S("sel_l", "左键点 L 车（[条件门·标记, 电弧]）", 15, c => FgjM1Journey.ClickMachine(c, "m.l"), c => FgjM1Journey.TickSelected(c, "m.l"), retries: 2),
                S("nochain", "右键靶子 3（攻击）：靶子没被标记 → 条件门关着，电弧不连锁，旁边的靶子不掉血", 120, null, TickNoChain),
                S("sel_k", "左键点 K 车（标记器）", 15, c => FgjM1Journey.ClickMachine(c, "m.k"), c => FgjM1Journey.TickSelected(c, "m.k"), retries: 2),
                S("chain", "右键靶子 3（攻击）：K 车命中把靶子标记 → L 车的条件门打开，电弧连锁跳到旁边的靶子", 120, null, TickChain),
                S("up_m", "命令栏机器列表点 M 车：接入（镜头直控），接入程序 = [条件门·标记, 双发指令, 分裂]", 30, c => FgjM1Journey.ClickMachineList(c, c.GetInt("m.m")), c => TickUplinked(c, "m.m"), retries: 3),
                S("meta", "直控开到射程内左键点靶子 3 开火：命中已标记的靶子，[双发指令] 让 [分裂] 执行两次 → 每发放出两组（6 份）子弹药", 90, null, TickMeta),
                S("leave", "按接入 / 退出接入键（默认 M）离开 M 车", 15, c => JourneyInput.PressAction(GameActionId.ToggleCameraView), TickLeft, retries: 2),
            };
            return new JourneyDef
            {
                Id = Id,
                Title = "FG-EMG 阶段 3 出口：履带车把燃烧区拖成火线 → 悬浮车跨过燃烧区不受伤 → 中继机器在友机命中时放电 → 两台节拍机器相位对齐齐射 → [条件门·标记] 门控电弧连锁 → [条件门·标记, 双发指令, 分裂] 命中已标记目标放两组子弹药",
                Seed = TestSeed,
                TotalTimeoutSeconds = 1500,
                OnFinish = Cleanup,
                Version = 1,
                CheckpointAfter = new[] { "emg_fix", "sc_close", "squad_fix", "hover", "relay", "volley", "chain" },
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
            CombatKernel k = Home?.Kernel;
            if (k != null)
            {
                EmgBusStats b = k.EmgBusStats;
                c.Log($"阶段 3 计数（家园内核全程）：中继转发 {b.RelayForwarded}、限流 {b.RelayCapped}；出拍 {b.BeatsEmitted}、对齐 {b.BeatSyncs}、节拍放出 {b.BeatPulses}；子载荷 {b.SubSpawned}；活斑块 {k.PatchCount}");
            }
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM3Common.FrameReport("全程"));
            JourneyCommon.Cleanup(c);
        }

        private static int Unit(JourneyContext c, string key) => Home != null && Home.TryGetMachineUnit(c.GetInt(key), out int u) ? u : 0;

        private static CombatUnitView View(CombatKernel k, int unit) => k.TryGetUnit(unit, out CombatUnitView v) ? v : default;

        // ── 夹具 ────────────────────────────────────────────────────────────────

        private static void ApplyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("fix.src", string.Empty).Length > 0)
            {
                return;
            }
            string[] ids = { IfMark, Repeat, Clock, Volley, Split, Arc, ComponentCatalog.StructRelayId, ComponentCatalog.FuncMarkerId };
            var src = new List<string>();
            var missing = new List<string>();
            foreach (string fw in ids)
            {
                string where = FgjEmg1Journey.SourcesOf(s, fw, out bool obtainable);
                src.Add($"{fw}：{where}；可获得 {obtainable}");
                // 卡片：正式固件要在正常游戏里可得（获取口径同 EMG1-FW-02：杂兵残骸 / 数据复原（表来源 elite = 精英掉落、salvage = 残骸回收）/ 候选池 / 开局已有）
                if (!obtainable || where == "无")
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
            s.Scrap = Math.Max(s.Scrap, SignalCoreService.FirmwareChipPrintScrap * 3);
        }

        private static StepOutcome TickFixture(JourneyContext c)
        {
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            if (c.Get("fix.missing", string.Empty).Length > 0)
            {
                return StepOutcome.Fail($"有固件 / 部件在正常游戏里没有来源：{c.Get("fix.missing")}（{c.Get("fix.src")}）");
            }
            EmgCompiled cat = EmgCatalog.Current;
            bool decl = new[] { IfMark, Repeat, Clock, Volley, Split, Arc }.All(EmgCatalog.IsOwnerOnDeclTrack);
            if (cat.EffectiveStage < 3 || !decl)
            {
                return StepOutcome.Fail($"正式表 emg.stage = {cat.RawStage}（生效 {cat.EffectiveStage}），阶段 3 出口应 ≥ 3；6 条固件全走声明轨 {decl}");
            }
            return StepOutcome.Done($"正式表 emg.stage = {cat.RawStage}（生效 {cat.EffectiveStage}），6 条固件走声明轨；来源：{c.Get("fix.src")}");
        }

        private static void ClickSlotThenEquip(JourneyContext c, int slot)
        {
            FgjM1Journey.ClickUi(c, "[SignalCoreHost]", "SignalSlot" + slot);
            FgjM1Journey.ClickUi(c, "[SignalCoreHost]", "SignalEquip");
        }

        private static StepOutcome TickEquipped(JourneyContext c, string fw, int slot)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return SignalCoreService.SlotContentId(St, slot) == fw && SignalCoreService.SlotChip(St, slot)?.State == PrimitiveChipState.SignalCore
                ? StepOutcome.Done($"{FirmwareKinds.DisplayName(fw)}装进信号核 {slot + 1} 号槽（{SignalCoreService.SummaryText(St)}）")
                : StepOutcome.Retry($"装入没成功（{SignalCoreHudUIToolkit.Instance?.FeedbackText}；{FgjM1Journey.UiFail(c)}）");
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

        private static int SpawnTarget(CombatKernel k, Vector2 p)
        {
            int id = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled,
                Position = new double2(p.x, p.y), Home = new double2(p.x, p.y), Radius = 0.5f, Speed = 0f, Health = 8000f, MaxHealth = 8000f,
                Weapon = -1, BehaviorProfile = -1, Priority = 1, ArmorHalfAngleDeg = 90f, ArmorFraction = 0f, ArmorFacing = new float2(0f, -1f),
            });
            k.SetUnitProfile(id, EmgEntityProfiles.ForEnemy("material.steel", false), 0f); // 钢壳：导电，电弧能连锁 / 带电
            return id;
        }

        private static BlueprintRecord AddBlueprint(CampaignState s, string id, string chassis, string primary, string utility, string structure, string[] firmware, bool uplink)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(chassis, primary, utility, structure, firmware);
            if (uplink && !Enumerable.Range(0, 9).Any(i => board.TrySetUplink(i).Success))
            {
                throw new InvalidOperationException($"夹具：{id} 没有能标接入口的格子");
            }
            var rec = new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { board.ToVersion(1, 0f) } };
            s.BlueprintRecords = s.BlueprintRecords.Where(b => b.BlueprintId != id).Append(rec).ToArray();
            return rec;
        }

        /// <summary>
        /// 找 4 处开阔地（覆盖里、互相 ≥ 36 格），世界按种子生成，位置都是现算（B25）。机器出厂在圆心朝核心 24 格（迎战半径 18 格外，要右键才开打）；
        /// 第 1 处是燃烧区：T / V 车在燃烧区一侧 10 格，对面 10 格的直线要能走；第 4 处（接入直控开火）机器在靶子正南，直控镜头里靶子在机器上方。
        /// </summary>
        private static void ApplySquadFixture(JourneyContext c)
        {
            CampaignState s = St;
            CombatSite site = Home;
            if (s == null || site == null || c.GetInt("m.t") > 0)
            {
                return;
            }
            Vector2 core = FgjM8Journey.CorePos;
            const float standoff = 24f;
            var arenas = new List<(Vector2 center, Vector2 toCore)>();
            var why = new List<string>();
            for (int radius = 36; radius <= 120 && arenas.Count < 4; radius += 8)
            {
                for (int deg = 0; deg < 360 && arenas.Count < 4; deg += 15)
                {
                    var dir = new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad));
                    Vector2 p = core + dir * radius;
                    p = new Vector2(Mathf.Round(p.x), Mathf.Round(p.y));
                    Vector2 toCore = arenas.Count == 3 ? Vector2.down : (core - p).normalized;
                    Vector2 mp = p + toCore * standoff;
                    Vector2 side = new Vector2(-toCore.y, toCore.x);
                    string bad = arenas.Any(a => Vector2.Distance(a.center, p) < 36f || Vector2.Distance(a.center, mp) < standoff - 2f || Vector2.Distance(a.center + a.toCore * standoff, p) < standoff - 2f) ? "离别的靶场太近"
                        : !SignalCoverageService.Sample(HomeValleyLayout.RegionId, p).Covered || !SignalCoverageService.Sample(HomeValleyLayout.RegionId, mp).Covered ? "不在覆盖里"
                        : !OpenDisk(s, p, 4) ? "圆心不开阔"
                        : !OpenDisk(s, mp, 4) ? "出厂点不开阔"
                        : !FgjEmg1Journey.Corridor(mp, p, out string block) ? "走廊不通 " + block
                        : arenas.Count == 0 && !(FgjEmg1Journey.Corridor(p + toCore * 10f, p - toCore * 10f, out block) && FgjEmg1Journey.Corridor(p + toCore * 10f + side * 2f, p - toCore * 10f + side * 2f, out block))
                            ? "燃烧区两侧不通 " + block
                            : null;
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
            string gun = ComponentCatalog.CompGunId, trk = HomeValleyLayout.Erc003ChassisId;
            BlueprintRecord Bp(string id, string chassis, string utility, string structure, bool uplink, params string[] fw) => AddBlueprint(s, "emg3-" + id, chassis, gun, utility, structure, fw, uplink);
            Vector2 Side(int a) => new Vector2(-arenas[a].toCore.y, arenas[a].toCore.x);
            Vector2 Stand(int a, float side) => arenas[a].center + arenas[a].toCore * standoff + Side(a) * side;
            var rows = new (string key, string chassis, BlueprintRecord bp, Vector2 at)[]
            {
                ("m.t", trk, Bp("t", trk, null, null, false), arenas[0].center + arenas[0].toCore * 10f),
                ("m.v", ChassisCatalog.ChassisHoverId, Bp("v", ChassisCatalog.ChassisHoverId, null, null, false), arenas[0].center + arenas[0].toCore * 10f + Side(0) * 2f),
                ("m.a", trk, Bp("a", trk, null, null, false), Stand(1, -3f)),
                // B 不下令、停在迎战半径（18 格）外 20 格：A 开到机枪射程（12 格）开火时两车相距 < 中继范围 12 米
                ("m.b", trk, Bp("b", trk, null, ComponentCatalog.StructRelayId, false, Split, Arc), arenas[1].center + arenas[1].toCore * 20f + Side(1) * 3f),
                ("m.x", trk, Bp("x", trk, null, ComponentCatalog.StructRelayId, false, Clock, Volley), Stand(2, -3f)),
                ("m.y", trk, Bp("y", trk, null, null, false, Clock, Volley), Stand(2, 3f)),
                ("m.k", trk, Bp("k", trk, ComponentCatalog.FuncMarkerId, null, false), Stand(3, -4f)),
                ("m.l", trk, Bp("l", trk, null, null, false, IfMark, Arc), Stand(3, 4f)),
                ("m.m", trk, Bp("m", trk, null, null, true, Split), Stand(3, 0f)),
            };
            foreach ((string key, string chassis, BlueprintRecord bp, Vector2 at) in rows)
            {
                BlueprintVersionRecord v = bp.Versions[0];
                MachineOpResult r = MachineRegistry.SpawnMachine(chassis, bp.BlueprintId, HomeValleyLayout.RegionId, at, SquadHp, SquadHp, "Player", 1, v.CompileSignature);
                if (!r.Success)
                {
                    c.Set("squad.err", $"{key} 出厂失败：{r.Message}");
                    return;
                }
                MachineLoadoutRegistry.Register(s, r.LogicId, bp.BlueprintId, 1);
                c.SetInt(key, r.LogicId);
            }
            CombatKernel k = site.Kernel;
            Vector2 fire = arenas[0].center;
            c.Set("fire.at", GetV(fire));
            c.Set("fire.dir", GetV(arenas[0].toCore));
            c.SetInt("fire.ok", k.SpawnPatch(EmgMatterConst.Fire, new double2(fire.x, fire.y), FireR, FireAmount, FireSeconds) ? 1 : 0);
            for (int a = 1; a < 4; a++)
            {
                Vector2 p = arenas[a].center;
                c.Set("t" + a + ".at", GetV(p));
                c.SetInt("t" + a, SpawnTarget(k, p));
            }
            Vector2 t1 = arenas[1].center;
            for (int i = 0; i < 4; i++)
            {
                // 靶子 1 四周 1.6 米一圈：B 的子弹药从命中点向外散开，打到邻靶才能看到电弧带电
                float ang = i * Mathf.PI * 0.5f;
                c.SetInt("t1r" + i, SpawnTarget(k, t1 + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 1.6f));
            }
            Vector2 t2 = arenas[2].center, t3 = arenas[3].center;
            c.SetInt("t2b", SpawnTarget(k, t2 + new Vector2(2.2f, 0.8f))); // 齐射要两个最近的敌人
            c.Set("n3.at", GetV(t3 + new Vector2(2.5f, 0f)));
            c.SetInt("n3", SpawnTarget(k, t3 + new Vector2(2.5f, 0f))); // 电弧连锁的下一跳（6 米内）
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
                if (Unit(c, key) == 0)
                {
                    return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail($"{key} 没进家园内核");
                }
            }
            CombatKernel k = Home.Kernel;
            if (c.GetInt("fire.ok") != 1)
            {
                return StepOutcome.Fail("燃烧区夹具没生成（斑块层没开？）");
            }
            byte tt = k.TraitOf(Unit(c, "m.t")), tv = k.TraitOf(Unit(c, "m.v"));
            bool relayB = k.TryGetRelay(Unit(c, "m.b"), out _), relayX = k.TryGetRelay(Unit(c, "m.x"), out _), relayA = k.TryGetRelay(Unit(c, "m.a"), out _);
            if (tt != EmgTraitConst.TrackDrag || tv != EmgTraitConst.HoverFloat || !relayB || !relayX || relayA)
            {
                return StepOutcome.Fail($"底盘特性 / 中继登记不对：T {tt}（应碾压拖带 {EmgTraitConst.TrackDrag}）、V {tv}（应离地 {EmgTraitConst.HoverFloat}）；中继 B {relayB}、X {relayX}、A {relayA}（A 没装）");
            }
            if (Team.Any(key => FgjM2Common.IsAttacking(Home, c.GetInt(key))))
            {
                return StepOutcome.Fail($"还没下令就打起来了：[{string.Join("、", Team.Where(key => FgjM2Common.IsAttacking(Home, c.GetInt(key))))}]");
            }
            return StepOutcome.Done($"{Team.Length} 台机器出厂（{string.Join("、", Team.Select(key => key + " #" + c.GetInt(key)))}）；T 碾压拖带、V 离地；B、X 登记中继；{c.Get("squad.ok")}");
        }

        // ── 下令 ────────────────────────────────────────────────────────────────

        /// <summary>选中的机器右键靶子下令攻击，每 2 秒最多点一次。返回“这台已在执行攻击命令”。</summary>
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
            if (c.GetInt(machineKey + ".sent") == 1)
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
                c.SetInt(machineKey + ".sent", 1);
            }
            return false;
        }

        private static StepOutcome Cleared(JourneyContext c, StepOutcome done, params string[] targetKeys)
        {
            // 这一段的靶子清场：机器不再往这里打，下一段互不串（夹具）。
            foreach (string key in targetKeys)
            {
                Home.Kernel.Despawn(c.GetInt(key));
            }
            return done;
        }

        // ── 1. 底盘 ─────────────────────────────────────────────────────────────

        /// <summary>燃烧区外、离燃烧区中轴线 ≤ 2 格的明火斑块数（履带拖出来的火线）。</summary>
        private static int FireLine(CombatKernel k, Vector2 center, Vector2 dir, float lateral)
        {
            int n = 0;
            for (int i = 0; i < k.PatchSlots; i++)
            {
                if (!k.TryGetPatch(i, out CombatZone z) || z.Matter != EmgMatterConst.Fire)
                {
                    continue;
                }
                var p = new Vector2((float)z.Pos.x, (float)z.Pos.y) - center;
                float along = Vector2.Dot(p, dir);
                float off = Vector2.Dot(p, new Vector2(-dir.y, dir.x)) - lateral;
                if (p.magnitude > FireR + 0.3f && Mathf.Abs(off) <= 2f && Mathf.Abs(along) <= 12f)
                {
                    n++;
                }
            }
            return n;
        }

        private static StepOutcome Cross(JourneyContext c, string key, float lateral, Func<CombatKernel, int, Vector2, StepOutcome> judge)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            Vector2 at = ParseV(c.Get("fire.at")), dir = ParseV(c.Get("fire.dir"));
            Vector2 side = new Vector2(-dir.y, dir.x) * lateral;
            if (!OrderMove(c, key, at - dir * 10f + side))
            {
                return StepOutcome.Wait;
            }
            CombatKernel k = Home.Kernel;
            int u = Unit(c, key);
            Vector2 me = FgjM8Journey.PosOf(c.GetInt(key));
            long dist = (long)(Vector2.Distance(me, at) * 100f);
            if (c.GetLong(key + ".min") == 0 || dist < c.GetLong(key + ".min"))
            {
                c.SetLong(key + ".min", dist);
            }
            k.TryGetUnitEmg(u, out _, out EmgUnitState es);
            if ((es.PrevMatterMask & (1u << EmgMatterConst.Fire)) != 0u)
            {
                c.SetInt(key + ".burnt", 1);
            }
            bool across = Vector2.Dot(me - at, dir) < -FireR - 2f;
            if (!across)
            {
                return c.StepElapsed < 100 ? StepOutcome.Wait : StepOutcome.Fail($"{key} 没开过燃烧区：离圆心 {F(Vector2.Distance(me, at))}（最近 {F(c.GetLong(key + ".min") / 100.0)}）");
            }
            return judge(k, u, at);
        }

        private static StepOutcome TickTrack(JourneyContext c)
        {
            if (c.GetLong("t.hp0") == 0)
            {
                c.SetLong("t.hp0", (long)(View(Home.Kernel, Unit(c, "m.t")).Health * 100f));
                c.SetInt("t.line0", FireLine(Home.Kernel, ParseV(c.Get("fire.at")), ParseV(c.Get("fire.dir")), 0f));
            }
            return Cross(c, "m.t", 0f, (k, u, at) =>
            {
                int line = FireLine(k, at, ParseV(c.Get("fire.dir")), 0f) - c.GetInt("t.line0");
                float hp = View(k, u).Health, hp0 = c.GetLong("t.hp0") / 100f;
                if (line < 2 || c.GetLong("m.t.min") > (long)(FireR * 100f))
                {
                    return c.StepElapsed < 100 ? StepOutcome.Wait : StepOutcome.Fail($"T 车开过燃烧区（离圆心最近 {F(c.GetLong("m.t.min") / 100.0)} 格）只拖出 {line} 块明火（应 ≥ 2，碾压拖带 FGR-EMG-140）");
                }
                c.SetInt("track.line", line);
                return StepOutcome.Done($"T 车碾过燃烧区（离圆心最近 {F(c.GetLong("m.t.min") / 100.0)} 格）：燃烧区外沿行进线新增 {line} 块明火（火线）；着地对照：沾上明火 {c.GetInt("m.t.burnt") == 1}、耐久 {F(hp0, "0")} → {F(hp, "0")}");
            });
        }

        private static StepOutcome TickHover(JourneyContext c)
        {
            if (c.GetLong("v.hp0") == 0)
            {
                c.SetLong("v.hp0", (long)(View(Home.Kernel, Unit(c, "m.v")).Health * 100f));
            }
            return Cross(c, "m.v", 2f, (k, u, at) =>
            {
                float hp = View(k, u).Health, hp0 = c.GetLong("v.hp0") / 100f;
                byte chs = k.ChsOf(u);
                bool inFire = c.GetLong("m.v.min") <= (long)((FireR - 0.5f) * 100f);
                if (!inFire || hp < hp0 - 0.01f || c.GetInt("m.v.burnt") == 1 || (chs & EmgTraitConst.ChsAirborne) == 0)
                {
                    return StepOutcome.Fail($"V 车跨燃烧区：离圆心最近 {F(c.GetLong("m.v.min") / 100.0)} 格（应在燃烧区里 < {F(FireR - 0.5f)}）、耐久 {F(hp0, "0.#")} → {F(hp, "0.#")}、沾上明火 {c.GetInt("m.v.burnt") == 1}、chs {chs}（应离地）");
                }
                return StepOutcome.Done($"V 车（悬浮，chs.airborne）从燃烧区里穿过（离圆心最近 {F(c.GetLong("m.v.min") / 100.0)} 格）：耐久 {F(hp0, "0")} → {F(hp, "0")} 不掉、身上不记明火（对照 T 车沾火 {c.GetInt("m.t.burnt") == 1}）");
            });
        }

        // ── 2. 中继 ─────────────────────────────────────────────────────────────

        private static StepOutcome TickRelay(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatKernel k = Home.Kernel;
            if (c.GetInt("rl.init") == 0)
            {
                c.SetInt("rl.init", 1);
                c.SetLong("rl.f0", k.EmgBusStats.RelayForwarded);
                c.SetLong("rl.s0", k.EmgBusStats.SubSpawned);
            }
            if (!OrderAttack(c, "m.a", "t1"))
            {
                return StepOutcome.Wait;
            }
            int a = Unit(c, "m.a"), b = Unit(c, "m.b"), t = c.GetInt("t1");
            long fwd = k.EmgBusStats.RelayForwarded - c.GetLong("rl.f0"), subs = k.EmgBusStats.SubSpawned - c.GetLong("rl.s0");
            k.TryGetRelay(b, out EmgRelay rl);
            int[] ring = { t, c.GetInt("t1r0"), c.GetInt("t1r1"), c.GetInt("t1r2"), c.GetInt("t1r3") };
            float charge = ring.Max(x => k.AccumOf(x, EmgPhaseConst.Charge));
            bool charged = charge > 0f || ring.Any(x => k.InPhase(x, EmgPhaseConst.Charge));
            if (charged)
            {
                c.SetInt("rl.charged", 1);
            }
            charged |= c.GetInt("rl.charged") == 1;
            float ringLoss = ring.Skip(1).Sum(x => 8000f - View(k, x).Health);
            EmgChainRec rec = default;
            bool found = false;
            for (int i = 0; i < k.EmgChainRecordCount && !found; i++)
            {
                found = k.TryGetEmgChainRecent(i, out rec) && rec.Kind == EmgChainNode.Relay && rec.Aux == EmgSquadConst.AuxForward && rec.SrcUnit == a && rec.Other == b;
            }
            if (fwd < 1 || subs < 1 || !found || !charged)
            {
                return c.StepElapsed < 100 ? StepOutcome.Wait
                    : StepOutcome.Fail($"中继转发 {fwd} 次、子载荷 {subs} 份、链节点 {found}、靶子群最高电荷 {F(charge, "0.###")}、邻靶共掉血 {F(ringLoss)}；B 中继计数 已转发 {rl.Forwarded} / 限流 {rl.Dropped}；B 在攻击 {FgjM2Common.IsAttacking(Home, c.GetInt("m.b"))}");
            }
            if (FgjM2Common.IsAttacking(Home, c.GetInt("m.b")))
            {
                return StepOutcome.Fail("B 车自己开打了：中继转发要在 B 没接到命令时发生");
            }
            string text = EmgChainView.RelayNodeText(Home, St, rec);
            if (!text.StartsWith("由中继转发：", StringComparison.Ordinal) || !text.Contains(EmgChainView.PartyName(Home, St, a)))
            {
                return StepOutcome.Fail($"因果链文本不对：“{text}”（应写“由中继转发：{EmgChainView.PartyName(Home, St, a)} 命中”，FGR-BASE-020）");
            }
            return Cleared(c, StepOutcome.Done($"A 车命中 → 中继转发 {fwd} 次（B 中继计数 {rl.Forwarded}，限流 {rl.Dropped}）：B 车没接命令，放出 {subs} 份带电弧的子弹药，打到靶子群（邻靶共掉血 {F(ringLoss, "0")}），靶子带电（最高电荷 {F(charge, "0.###")}）；因果链“{text}”"), "t1", "t1r0", "t1r1", "t1r2", "t1r3");
        }

        // ── 3. 节拍 ─────────────────────────────────────────────────────────────

        private static StepOutcome TickSelectPair(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            int[] pair = { c.GetInt("m.x"), c.GetInt("m.y") };
            IReadOnlyList<int> sel = GameRoot.HomeValley?.SquadCommands?.Selection;
            string kk = FgjM3Common.SK(c, "k");
            if (sel != null && pair.All(sel.Contains))
            {
                return StepOutcome.Done($"选中 X、Y 两台（选择集 {sel.Count} 台）");
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 400)
            {
                return StepOutcome.Wait;
            }
            int k = c.GetInt(kk);
            if (k >= pair.Length)
            {
                return StepOutcome.Retry($"点完一遍后选择集 [{string.Join("、", (sel ?? Array.Empty<int>()).Select(Label))}]");
            }
            if (!FgjM1Journey.HomeMarker(pair[k], out HomeValleyMachineMarker m))
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail($"{Label(pair[k])} 没有模型");
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

        private static StepOutcome TickVolley(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatKernel k = Home.Kernel;
            bool ax = FgjM2Common.IsAttacking(Home, c.GetInt("m.x")), ay = FgjM2Common.IsAttacking(Home, c.GetInt("m.y"));
            if (!ax || !ay)
            {
                // 两台同时选中，一次右键同时下令（RTS 口径）；没下成每 2 秒补点一次
                if (!JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) > 2000)
                {
                    c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
                    FgjM3Common.TryClick(ParseV(c.Get("t2.at")), 1);
                }
                c.SetLong("b2.sync0", k.EmgBusStats.BeatSyncs);
                c.SetLong("b2.sub0", k.EmgBusStats.SubSpawned);
                c.SetLong("b2.hp0", (long)View(k, c.GetInt("t2")).Health);
                c.SetLong("b2.hpb0", (long)View(k, c.GetInt("t2b")).Health);
                return c.StepElapsed < 30 ? StepOutcome.Wait : StepOutcome.Fail($"右键靶子 2 后 X 攻击 {ax}、Y 攻击 {ay}");
            }
            long syncs = k.EmgBusStats.BeatSyncs - c.GetLong("b2.sync0"), subs = k.EmgBusStats.SubSpawned - c.GetLong("b2.sub0");
            long dmg = c.GetLong("b2.hp0") - (long)View(k, c.GetInt("t2")).Health, dmgB = c.GetLong("b2.hpb0") - (long)View(k, c.GetInt("t2b")).Health;
            if (syncs < 1)
            {
                CombatUnitView xv = View(k, Unit(c, "m.x")), yv = View(k, Unit(c, "m.y"));
                return c.StepElapsed < 60 ? StepOutcome.Wait
                    : StepOutcome.Fail($"X、Y 都在打靶子 2，相位对齐 {syncs} 次（应 ≥ 1）；出拍 {k.EmgBusStats.BeatsEmitted}、子载荷 +{subs}、靶子掉血 {dmg} / 第二近 {dmgB}；" +
                                       $"X 离靶 {F(Vector2.Distance(FgjM8Journey.PosOf(c.GetInt("m.x")), ParseV(c.Get("t2.at"))))} 热量 {F(xv.Heat)}、Y 离靶 {F(Vector2.Distance(FgjM8Journey.PosOf(c.GetInt("m.y")), ParseV(c.Get("t2.at"))))} 热量 {F(yv.Heat)}；" +
                                       $"X、Y 相距 {F(Vector2.Distance(FgjM8Journey.PosOf(c.GetInt("m.x")), FgjM8Journey.PosOf(c.GetInt("m.y"))))}（中继范围 12）；掩体吸收 {k.EmgBusStats.CoverAbsorbed}、撞障碍 {k.EmgBusStats.ObstacleHits}");
            }
            return Cleared(c, StepOutcome.Done($"X、Y 一次右键同时开火（X 装中继，Y 在 X 的中继范围里）：相位对齐 {syncs} 次，期间放出子弹药 {subs} 份；靶子群掉血：靶子 2 {dmg}、旁边的靶子 {dmgB}"), "t2", "t2b");
        }

        // ── 4. 元程序 ───────────────────────────────────────────────────────────

        private static StepOutcome TickNoChain(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!OrderAttack(c, "m.l", "t3"))
            {
                return StepOutcome.Wait;
            }
            CombatKernel k = Home.Kernel;
            int t = c.GetInt("t3"), n = c.GetInt("n3");
            if (c.GetLong("nc.hp0") == 0)
            {
                c.SetLong("nc.hp0", (long)View(k, t).Health);
                return StepOutcome.Wait;
            }
            FgjEmg1Journey.Program(Home, c.GetInt("m.l"), out CombatWeapon w, out _);
            float dmg = c.GetLong("nc.hp0") - View(k, t).Health;
            float nHp = View(k, n).Health;
            if (nHp < 8000f)
            {
                var recs = new List<string>();
                Vector2 np = ParseV(c.Get("n3.at"));
                for (int i = 0; i < k.EmgChainRecordCount && recs.Count < 12; i++)
                {
                    if (k.TryGetEmgChainRecent(i, out EmgChainRec r) && (r.Other == n || r.SrcUnit == n || math.distance(r.Pos, new double2(np.x, np.y)) < 1.5))
                    {
                        recs.Add($"{r.Kind}/键{r.Key}/源{r.SrcUnit}/他{r.Other}/辅{r.Aux}@({F(r.Pos.x)},{F(r.Pos.y)})");
                    }
                }
                return StepOutcome.Fail($"靶子 3 没被标记，旁边的靶子却掉血 {F(8000f - nHp)}：条件门没挡住电弧连锁；L 离靶 {F(Vector2.Distance(FgjM8Journey.PosOf(c.GetInt("m.l")), ParseV(c.Get("t3.at"))))}、" +
                    $"飞行 {w.SpawnFlight}、伤害 {F(w.Damage)}、连锁 {w.Reading.ChainHits} 跳 {F(w.Reading.ChainRange)} 米；靶子 3 电荷 {F(k.AccumOf(t, EmgPhaseConst.Charge), "0.###")}、邻靶电荷 {F(k.AccumOf(n, EmgPhaseConst.Charge), "0.###")}；邻靶附近链记录 [{string.Join("；", recs)}]（靶子 3 = {t}、邻靶 = {n}、L = {Unit(c, "m.l")}）");
            }
            if (View(k, t).MarkedUntil > k.Time)
            {
                return StepOutcome.Fail("靶子 3 还没人标记就是已标记状态");
            }
            if (dmg < w.Damage * 3f - 0.01f)
            {
                return c.StepElapsed < 100 ? StepOutcome.Wait : StepOutcome.Fail($"L 车打靶子 3：掉血 {F(dmg)}（不到 3 发 × {F(w.Damage)}）");
            }
            return StepOutcome.Done($"L 车命中没标记的靶子 3 至少 3 发（掉血 {F(dmg)}）：[条件门·标记] 关着，电弧不连锁，旁边的靶子耐久 {F(nHp, "0")} / 8000");
        }

        private static StepOutcome TickChain(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!OrderAttack(c, "m.k", "t3"))
            {
                return StepOutcome.Wait;
            }
            CombatKernel k = Home.Kernel;
            CombatUnitView tv = View(k, c.GetInt("t3"));
            float nHp = View(k, c.GetInt("n3")).Health;
            if (tv.MarkedUntil > k.Time && c.GetInt("ch.marked") == 0)
            {
                c.SetInt("ch.marked", 1);
                c.Set("ch.markAt", F(k.Time, "0.##"));
            }
            if (c.GetInt("ch.marked") == 0 || nHp >= 8000f)
            {
                return c.StepElapsed < 100 ? StepOutcome.Wait : StepOutcome.Fail($"K 车标记靶子 3：已标记 {c.GetInt("ch.marked") == 1}（到期 {F(tv.MarkedUntil, "0.#")}，现在 {F(k.Time, "0.#")}）；旁边的靶子耐久 {F(nHp)}（L 的电弧没连锁过去）");
            }
            return StepOutcome.Done($"K 车命中把靶子 3 标记（第 {c.Get("ch.markAt")} 秒）→ L 车的 [条件门·标记] 打开，电弧连锁跳到 2.5 米外的靶子（耐久 {F(nHp, "0")} / 8000）");
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
            return prog >= 0 ? StepOutcome.Done($"接入 {Label(id)}（镜头直控）：武器程序 {prog}、射程 {F(w.Range)}") : StepOutcome.Fail("接入后武器没有程序");
        }

        /// <summary>直控左键点敌人：敌人正好被 HUD 挡住时点它旁边露出来的地方（直控拾取半径 2.5 格）。</summary>
        private static bool ClickEnemy(Vector2 tp)
        {
            foreach (Vector2 off in new[] { Vector2.zero, Vector2.down, Vector2.left, new Vector2(-1.4f, -1.4f), Vector2.up })
            {
                if (FgjM3Common.Clear(tp + off, out _))
                {
                    JourneyInput.Click(tp + off);
                    return true;
                }
            }
            return false;
        }

        /// <summary>每一发（= 一条因果链：每次出手开一条新链）放出的子弹药：按弹体编号去重、按链归组。</summary>
        private static readonly Dictionary<int, int> MetaSubsByChain = new Dictionary<int, int>();

        private static readonly HashSet<int> MetaSeenSubs = new HashSet<int>();

        /// <summary>
        /// 直控开火：M 车放出的子弹药（Sub 标志、归属 M）按链归组 = 每一发的份数；只有 M 在放子载荷（前几段的靶子已清场，L 的电弧、K 的标记器不放子载荷）。
        /// 最新一条链可能还没放完，只判它之前的几发。
        /// </summary>
        private static StepOutcome TickMeta(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatKernel k = Home.Kernel;
            int m = c.GetInt("m.m"), u = Unit(c, "m.m");
            if (u == 0 || !FgjEmg1Journey.Program(Home, m, out CombatWeapon w, out _))
            {
                return StepOutcome.Fail("M 车不在内核里");
            }
            if (c.GetInt("mt.init") == 0)
            {
                c.SetInt("mt.init", 1);
                MetaSubsByChain.Clear();
                MetaSeenSubs.Clear();
                c.SetLong("mt.sub0", k.EmgBusStats.SubSpawned);
            }
            Vector2 tp = ParseV(c.Get("t3.at"));
            Vector2 me = FgjM8Journey.PosOf(m);
            if (Vector2.Distance(me, tp) > w.Range - 1f)
            {
                FgjM1Journey.DriveToward(me, tp + (me - tp).normalized * (w.Range - 3f), 0.3);
            }
            if (NowMs() - c.GetLong(FgjM3Common.SK(c, "fireAt")) > 400)
            {
                c.SetLong(FgjM3Common.SK(c, "fireAt"), NowMs());
                c.SetInt("mt.clicks", c.GetInt("mt.clicks") + (ClickEnemy(tp) ? 1 : 0));
            }
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                if (k.TryGetProjectile(i, out CombatProjectile p) && (p.Flags & EmgProjFlags.Sub) != 0 && p.Owner == u && MetaSeenSubs.Add(p.Id))
                {
                    MetaSubsByChain[p.Chain] = MetaSubsByChain.TryGetValue(p.Chain, out int n0) ? n0 + 1 : 1;
                }
            }
            bool marked = View(k, c.GetInt("t3")).MarkedUntil > k.Time;
            List<int> chains = MetaSubsByChain.Keys.OrderBy(x => x).ToList();
            string per = string.Join("/", chains.Select(x => MetaSubsByChain[x]));
            if (chains.Count < 3)
            {
                return c.StepElapsed < 80 ? StepOutcome.Wait
                    : StepOutcome.Fail($"直控左键 {c.GetInt("mt.clicks")} 次，按链归组的子弹药 [{per}]（子载荷总计 +{k.EmgBusStats.SubSpawned - c.GetLong("mt.sub0")}）；离靶 {F(Vector2.Distance(me, tp))}、射程 {F(w.Range)}；靶子已标记 {marked}");
            }
            if (!marked)
            {
                return StepOutcome.Fail($"开火时靶子 3 已不在标记状态（K 车停了？），每发 [{per}]");
            }
            int[] done = chains.Take(chains.Count - 1).Select(x => MetaSubsByChain[x]).ToArray();
            if (done.Any(x => x != 6))
            {
                return StepOutcome.Fail($"命中已标记的靶子每发放出 [{per}] 份（应每发 6 份 = [分裂] 3 份 × [双发指令] 2 组）");
            }
            return StepOutcome.Done($"直控 M 车左键开火命中已标记的靶子 3：已结束的 {done.Length} 发每发子弹药 [{string.Join("/", done)}] 份（6 = 两组 × 3，[若目标被标记, 复制, 分裂]），在途 1 发 {MetaSubsByChain[chains[chains.Count - 1]]} 份");
        }

        private static StepOutcome TickLeft(JourneyContext c)
        {
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            if (SignalPresence.CurrentMachineLogicId == c.GetInt("m.m"))
            {
                return StepOutcome.Retry("信号还在 M 车里");
            }
            return Cleared(c, StepOutcome.Done("离开 M 车，武器回 AI 程序（[分裂]）"), "t3", "n3");
        }
    }
}
