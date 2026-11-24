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
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.Localization;
using GameLogic.Campaign.Nav;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// EMGR-E2E-01：补功能批次 EMG-R 的正向出口旅程 FGJ-EMG-R（轻量口径 a）。从主菜单“新建”出发（固定测试种子 42、标准难度），全程正式输入，
    /// RTS 口径：左键选择、右键敌人 = 攻击、右键地面 = 移动；悬停读来源。
    ///
    /// 本批次每个玩家可见的新行为被真实输入触发一次并断言结果：
    /// ① EMGR-NAV-01：右键射程外、被悬崖围住（缺口在背面）的敌人 → 机器走层级寻路绕进缺口开火击毁，全程不踏进不可走格；
    /// ② EMGR-CMD-01：右键地面让带武器、没被接入的机器从敌人旁边开过 → 途中原地还手（敌人掉血）、命令始终是移动、走到目的地，悬停写“由教义触发：自卫交战”；
    /// ③ EMGR-MAT-01（+ RULE-01 热交换）：漏油车、燃迹车先后右键敌方结构单位 → 结构单位脚下的油、明火被内核接触结算（物质掩码记上、挂燃烧位、掉血）；
    /// ④ EMGR-TRIG-01：切割束机器右键敌人 → 每 0.5 秒切一段（3 游戏秒内 5～7 段；旧行为按编队间隔 1.2 秒约 2～3 段）；
    /// ⑤ EMGR-FIELD-01：入场引信机器右键场后面的敌人 → 弹体穿过电晕场时分裂；右键场中心的地面 → 机器身上记上电晕场位，存读档后仍在、不丢；右键场外 → 场位清掉；
    /// ⑥ 存读档：暂停菜单“保存并返回主菜单” → 读档，家园内核快照逐字节一致、读回状态哈希 = 存档那一刻（新快照段：自卫标记、场位、线索 / 计数表等随快照走）。
    /// 夹具（写在步骤标题里）：靶子 / 结构单位 / 电晕场 / 悬崖围栏与机器直接出厂（出厂、编辑器由 FGJ-M1 / FGJ-EMG-1 覆盖，场生成由 FGJ-EMG-3 覆盖）。
    /// 内部容量类（MAT-01 区域上限、FUSE-01 线索 128 格、TALLY-01 计数表 4096、TRIG-01 锚点 4096、GL-01 搬运索引与撤离归因、GL-02 突袭改目标 / 地形不截断 / 叠加层不封顶）
    /// 按轻量口径 b 由各卡单段自检覆盖（RunAll），不进 UI 旅程。
    /// 用法：bash tools/unity-journey.sh FGJ-EMG-R；断点 emg_fix / arena_fix / nav / cmd / burn / beam / walk_in / loaded（--from 续跑）。
    /// </summary>
    public static class FgjEmgRJourney
    {
        public const string Id = "FGJ-EMG-R";

        public const int TestSeed = FgjM0Journey.TestSeed;

        private const string Oil = "fw_oilleak", Burn = "fw_burntrail", EnterFuse = "fw_enterfuse";

        private const float SquadHp = 500f, Standoff = 24f;

        /// <summary>悬崖围栏半径（&gt; 家园机枪射程 12，必须绕进缺口才打得到）与机器离圆心的距离（围栏外 2 格）。</summary>
        private const int NavRing = 14, NavStand = 16;

        /// <summary>自卫区半宽（53×53）。</summary>
        private const int CmdHalf = 26;

        /// <summary>电晕场（夹具）：离靶子 8 格、半径 2.5、存续 1200 游戏秒；W 车出发点在场侧面 18 格（离靶子 &gt; 迎战半径 18）。</summary>
        private const float FieldAhead = 8f, FieldR = 2.5f, FieldSeconds = 1200f, WalkSide = 18f;

        private static readonly string[] Team = { "m.n", "m.c", "m.a", "m.b", "m.t", "m.f", "m.w" };

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
                S("emg_fix", "进度夹具：正式表 emg.stage = 4（FG-EMG 收尾）；旅程用到的正式固件 [漏油] / [燃迹] / [入场引信] 记为已获得（先断言每条在正常游戏里都有来源）", 15, ApplyFixture, TickFixture),
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("arena_fix", "出厂夹具：N 机枪（寻路）/ C 机枪（自卫）/ A 漏油 / B 燃迹 / T 切割束 / F 入场引信 / W 机枪各出厂一台；悬崖围栏（半径 14、背面留缺口，开局先揭开核心周围 120 格）围住一个敌人；" +
                    "自卫区 53×53 空地（找不到天然平地就整平）；3 处开阔地：不动不还手的敌人 / 敌方结构单位 / 电晕场（场生成由 FGJ-EMG-3 覆盖）", 30, ApplyArenaFixture, TickArenaFixture),

                // ── ① EMGR-NAV-01：右键攻击绕障 ──
                S("sel_n", "左键点 N 车", 15, c => FgjM1Journey.ClickMachine(c, "m.n"), c => FgjM1Journey.TickSelected(c, "m.n"), retries: 2),
                S("nav", "右键悬崖围栏里的敌人（攻击，射程外、直线被悬崖挡住）：N 车走层级寻路绕到背面缺口进去开火击毁，全程不踏进不可走格", 240, null, TickNav),

                // ── ② EMGR-CMD-01：移动途中按教义自卫 ──
                S("speed1", "点世界栏 1 倍速（悬停要在 C 车开过敌人旁边的几秒内完成）", 10, c => ClickSpeed("WorldSpeed1"), c => TickSpeed(c, 1f), retries: 1),
                S("sel_c", "左键点 C 车", 15, c => FgjM1Journey.ClickMachine(c, "m.c"), c => FgjM1Journey.TickSelected(c, "m.c"), retries: 2),
                S("cmd", "右键敌人另一头的地面（移动）：C 车开过敌人旁边时原地还手（敌人掉血），命令始终是移动、不追击，走到目的地；光标悬停 C 车，提示写“由教义触发：自卫交战”", 180, null, TickCmd),
                S("speed3b", "点世界栏 3 倍速", 10, c => ClickSpeed("WorldSpeed3"), c => TickSpeed(c, 3f), retries: 1),

                // ── ③ EMGR-MAT-01：敌方结构单位挨斑块 ──
                S("sel_a", "左键点 A 车（漏油）", 15, c => FgjM1Journey.ClickMachine(c, "m.a"), c => FgjM1Journey.TickSelected(c, "m.a"), retries: 2),
                S("oil", "右键敌方结构单位（攻击）：弹体终结处留下油斑块，结构单位脚下的油被内核接触结算（物质掩码记上油位）", 120, null, TickOil),
                S("sel_b", "左键点 B 车（燃迹）", 15, c => FgjM1Journey.ClickMachine(c, "m.b"), c => FgjM1Journey.TickSelected(c, "m.b"), retries: 2),
                S("burn", "右键敌方结构单位（攻击）：明火落在结构单位脚下（油挨明火被点燃），结构单位记上明火位、挂燃烧位、掉血", 120, null, TickBurn),

                // ── ④ EMGR-TRIG-01：切割束每 0.5 秒一段 ──
                S("sel_t", "左键点 T 车（切割束）", 15, c => FgjM1Journey.ClickMachine(c, "m.t"), c => FgjM1Journey.TickSelected(c, "m.t"), retries: 2),
                S("beam", "右键敌人（攻击）：切割束每 0.5 秒切一段，3 游戏秒内 5～7 段（旧行为按编队攻击间隔 1.2 秒只有 2～3 段）", 120, null, TickBeam),

                // ── ⑤ EMGR-FIELD-01：场的进场 / 离场 ──
                S("sel_f", "左键点 F 车（入场引信）", 15, c => FgjM1Journey.ClickMachine(c, "m.f"), c => FgjM1Journey.TickSelected(c, "m.f"), retries: 2),
                S("fuse", "右键电晕场后面的敌人（攻击）：弹体穿过电晕场时入场引信分裂（子载荷计数增加）", 120, null, TickFuse),
                S("sel_w", "左键点 W 车", 15, c => FgjM1Journey.ClickMachine(c, "m.w"), c => FgjM1Journey.TickSelected(c, "m.w"), retries: 2),
                S("walk_in", "右键电晕场中心的地面（移动）：W 车走进场，身上记上电晕场位（物质位不受影响）", 120, null, TickWalkIn),

                // ── ⑥ 存读档 ──
                S("save_esc", "按 Esc 打开暂停菜单（世界暂停）：记下家园内核快照与状态哈希", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseForSave, retries: 1),
                S("save_quit", "点“保存并返回主菜单”，确认", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), FgjM1Journey.TickSaveConfirm),
                S("menu_back", "回到主菜单：存档里的家园内核快照与存档那一刻逐字节一致", 60, null, TickMenuAfterSave),
                S("load_list", "点“读取”，点刚才的存档槽", 20, c => FgjM1Journey.ClickUgui(c, "m_btn_Load"), FgjM1Journey.TickLoadSlot),
                S("loaded", "读档进入归还谷地：家园内核从快照恢复、读回状态哈希 = 存档那一刻；W 车仍在场里、电晕场位还在", 120, null, TickLoaded),
                S("speed3c", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("sel_w2", "左键点 W 车", 15, c => FgjM1Journey.ClickMachine(c, "m.w"), c => FgjM1Journey.TickSelected(c, "m.w"), retries: 2),
                S("walk_out", "右键场外的地面（移动）：W 车走出电晕场，场位清掉", 120, c => c.SetInt("m.w.sent", 0), TickWalkOut),
            };
            return new JourneyDef
            {
                Id = Id,
                Title = "EMG-R 补功能批次出口：右键攻击绕悬崖 → 移动途中按教义自卫 → 敌方结构单位挨油与明火 → 切割束 0.5 秒一段 → 入场引信穿场分裂、机器进出场 → 存读档一致",
                Seed = TestSeed,
                TotalTimeoutSeconds = 1500,
                OnFinish = Cleanup,
                Version = 1,
                CheckpointAfter = new[] { "emg_fix", "arena_fix", "nav", "cmd", "burn", "beam", "walk_in", "loaded" },
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static string F(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);

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
                EmgBusStats b = site.Kernel.EmgBusStats;
                c.Log($"EMG-R 计数（家园内核全程）：子载荷 {b.SubSpawned}；自卫交战开始 {site.SelfDefenseEngagements} 次；活斑块 {site.Kernel.PatchCount}");
            }
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM3Common.FrameReport("全程"));
            JourneyCommon.Cleanup(c);
        }

        private static int Unit(JourneyContext c, string key) => Home != null && Home.TryGetMachineUnit(c.GetInt(key), out int u) ? u : 0;

        private static float Hp(CombatKernel k, int unit) => k.TryGetUnit(unit, out CombatUnitView v) ? v.Health : -1f;

        private static uint Mask(CombatKernel k, int unit) => k.TryGetUnitEmg(unit, out _, out EmgUnitState es) ? es.PrevMatterMask : 0u;

        private static bool HasStatus(CombatKernel k, int unit, int bit) =>
            bit >= 0 && bit < 31 && k.TryGetStatus(unit, out uint m, out _, out _, out _, out _) && (m & (1u << bit)) != 0u;

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

        // ── 夹具 ────────────────────────────────────────────────────────────────

        private static void ApplyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("fix.src", string.Empty).Length > 0)
            {
                return;
            }
            string[] ids = { Oil, Burn, EnterFuse };
            var src = new List<string>();
            var missing = new List<string>();
            foreach (string fw in ids)
            {
                string where = FgjEmg1Journey.SourcesOf(s, fw, out bool obtainable);
                src.Add($"{fw}：{where}；可获得 {obtainable}");
                if (!obtainable || where == "无")
                {
                    missing.Add(fw);
                }
            }
            c.Set("fix.src", string.Join("；", src));
            c.Set("fix.missing", string.Join(",", missing));
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(ids).Distinct().ToArray();
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
            bool decl = new[] { Oil, Burn, EnterFuse }.All(EmgCatalog.IsOwnerOnDeclTrack);
            if (cat.EffectiveStage < 4 || !decl)
            {
                return StepOutcome.Fail($"正式表 emg.stage = {cat.RawStage}（生效 {cat.EffectiveStage}），FG-EMG 收尾后应 = 4；3 条固件全走声明轨 {decl}");
            }
            return StepOutcome.Done($"正式表 emg.stage = {cat.RawStage}（生效 {cat.EffectiveStage}），3 条固件走声明轨；来源：{c.Get("fix.src")}");
        }

        /// <summary>悬崖围栏区：机器一侧的轴向（离核心近的那一侧），缺口在相反一侧。</summary>
        private static Vector2Int NavAxis(Vector2 center)
        {
            Vector2 toCore = FgjM8Journey.CorePos - center;
            return Mathf.Abs(toCore.x) >= Mathf.Abs(toCore.y) ? new Vector2Int(toCore.x >= 0f ? 1 : -1, 0) : new Vector2Int(0, toCore.y >= 0f ? 1 : -1);
        }

        private static bool Covered(Vector2 p) => SignalCoverageService.Sample(HomeValleyLayout.RegionId, p).Covered;

        private static BlueprintRecord Bp(CampaignState s, string id, string primary, params string[] fw) =>
            FgjEmg3Journey.AddBlueprint(s, "emgr-" + id, HomeValleyLayout.Erc003ChassisId, primary, null, null, fw, false);

        private static bool Spawn(JourneyContext c, CampaignState s, string key, BlueprintRecord bp, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp.BlueprintId, HomeValleyLayout.RegionId, at, SquadHp, SquadHp, "Player", 1, bp.Versions[0].CompileSignature);
            if (!r.Success)
            {
                c.Set("arena.err", $"{key} 出厂失败：{r.Message}");
                return false;
            }
            MachineLoadoutRegistry.Register(s, r.LogicId, bp.BlueprintId, 1);
            c.SetInt(key, r.LogicId);
            c.Set(key + ".at", GetV(at));
            return true;
        }

        /// <summary>
        /// 悬崖围栏区（FgNavSelfCheck 同款布景：35×35 空地，半径 14 的悬崖围一圈、背面留 3 格缺口）+ 4 处开阔地（覆盖里、互相 ≥ 36 格、离围栏区 ≥ 44 格）。
        /// 世界按种子生成，位置都是现算（B25）。机器出厂在圆心朝核心 24 格（迎战半径 18 格外，要下令才开打）。
        /// </summary>
        private static void ApplyArenaFixture(JourneyContext c)
        {
            CampaignState s = St;
            CombatSite site = Home;
            if (s == null || site == null || c.GetInt("m.n") > 0)
            {
                return;
            }
            GridCell corePivot = HomeGridService.CorePivot(s);
            HomeGridService.RevealArea(s, new Vector2(corePivot.X, corePivot.Y), 120f);
            GridCell? navArea = FgNavSelfCheck.FindOpenArea(s, NavRing + 3, 30, 70, cell =>
            {
                var center = new Vector2(cell.X, cell.Y);
                Vector2Int d = NavAxis(center);
                return Covered(center) && Covered(center + (Vector2)d * NavStand);
            });
            if (navArea == null)
            {
                c.Set("arena.err", "家园找不到做悬崖围栏的空地（覆盖里、35×35）");
                return;
            }
            GridCell nc = navArea.Value;
            var navCenter = new Vector2(nc.X, nc.Y);
            Vector2Int axis = NavAxis(navCenter);
            FgNavSelfCheck.CliffRing(s, nc, NavRing, 1, (dx, dy) => axis.x != 0 ? dx == -NavRing * axis.x && Math.Abs(dy) <= 1 : dy == -NavRing * axis.y && Math.Abs(dx) <= 1);

            // 自卫区：53×53 的空地（同悬崖围栏区的找法，找不到天然平地就整平），C 车从 (+24, +6) 开到 (−24, +6)，敌人在圆心：起终点离敌人 24.7 格（迎战半径 18 外），途中最近 6 格（机枪射程内）。
            GridCell? cmdArea = FgNavSelfCheck.FindOpenArea(s, CmdHalf, 30, 90, cell =>
                Math.Max(Math.Abs(cell.X - nc.X), Math.Abs(cell.Y - nc.Y)) >= CmdHalf + NavRing + 6
                && Covered(new Vector2(cell.X, cell.Y)) && Covered(new Vector2(cell.X + Standoff, cell.Y + 6)) && Covered(new Vector2(cell.X - Standoff, cell.Y + 6)));
            if (cmdArea == null)
            {
                c.Set("arena.err", "家园找不到做移动途中自卫的空地（覆盖里、53×53）");
                return;
            }
            var cmdCenter = new Vector2(cmdArea.Value.X, cmdArea.Value.Y);

            Vector2 core = FgjM8Journey.CorePos;
            // 4 个角色：0 自卫（上面的自卫区，toCore 记作 +x）/ 1 结构单位 / 2 切割束 / 3 场（电晕场在圆心朝核心 8 格，W 车在场侧面 18 格）。
            // 候选按半径由近及远、角度由小到大扫（确定性）；每个过了公共条件的候选填进第一个它满足额外条件的空角色。
            var slots = new (Vector2 center, Vector2 toCore)?[4];
            slots[0] = (cmdCenter, Vector2.right);
            var why = new List<string>();
            var tally = new Dictionary<string, int>();
            void Miss(string what, int radius, int deg)
            {
                tally[what] = tally.TryGetValue(what, out int n0) ? n0 + 1 : 1;
                if (why.Count < 8)
                {
                    why.Add($"{radius}格/{deg}°：{what}");
                }
            }
            // 圆心 / 出厂点要 4 格开阔（靶子与机器周围留空）；路线起终点只要那一格周围 1 格可走（路线本身另由 Corridor 判）
            string Probe(string name, Vector2 q, List<Vector2> taken, int open = 4)
            {
                if (taken.Any(t => Vector2.Distance(t, q) < Standoff - 2f))
                {
                    return name + "离别的靶场太近";
                }
                if (Vector2.Distance(q, navCenter) < NavRing + 20f)
                {
                    return name + "离悬崖围栏区太近";
                }
                if (Mathf.Max(Mathf.Abs(q.x - cmdCenter.x), Mathf.Abs(q.y - cmdCenter.y)) < CmdHalf + 18f)
                {
                    return name + "离自卫区太近";
                }
                if (!Covered(q))
                {
                    return name + "不在覆盖里";
                }
                return FgjEmg3Journey.OpenDisk(s, q, open) ? null : name + "不开阔";
            }
            for (int radius = 36; radius <= 120 && slots.Any(x => x == null); radius += 8)
            {
                for (int deg = 0; deg < 360 && slots.Any(x => x == null); deg += 15)
                {
                    var dir = new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad));
                    Vector2 p = core + dir * radius;
                    p = new Vector2(Mathf.Round(p.x), Mathf.Round(p.y));
                    Vector2 toCore = (core - p).normalized;
                    Vector2 side = new Vector2(-toCore.y, toCore.x);
                    Vector2 mp = p + toCore * Standoff;
                    var taken = slots.Skip(1).Where(x => x != null).Select(x => x.Value.center).ToList();
                    string bad = taken.Any(t => Vector2.Distance(t, p) < 36f) ? "圆心离别的靶场太近"
                        : Probe("圆心", p, taken) ?? Probe("出厂点", mp, taken)
                        ?? (FgjEmg1Journey.Corridor(mp, p, out string block) ? null : "走廊不通 " + block);
                    if (bad != null)
                    {
                        Miss(bad, radius, deg);
                        continue;
                    }
                    Vector2 field = p + toCore * FieldAhead, wFrom = field + side * WalkSide;
                    for (int r = 0; r < 4; r++)
                    {
                        if (slots[r] != null)
                        {
                            continue;
                        }
                        string extra = r == 3 ? Probe("电晕场", field, taken, 1) ?? Probe("W 车起点", wFrom, taken, 1) ?? (FgjEmg1Journey.Corridor(wFrom, field, out string b3) ? null : "W 车路线不通 " + b3)
                            : null;
                        if (extra == null)
                        {
                            slots[r] = (p, toCore);
                            break;
                        }
                        Miss(extra, radius, deg);
                    }
                }
            }
            if (slots.Any(x => x == null))
            {
                c.Set("arena.err", $"只凑齐 {slots.Count(x => x != null)} / 4 处靶场（空着的角色 {string.Join(",", Enumerable.Range(0, 4).Where(i => slots[i] == null))}）；原因统计 {string.Join("、", tally.Select(kv => kv.Key + " " + kv.Value))}；前几条：{string.Join("；", why)}");
                return;
            }
            var arenas = slots.Select(x => x.Value).ToList();
            Vector2 Side(int a) => new Vector2(-arenas[a].toCore.y, arenas[a].toCore.x);
            Vector2 Stand(int a) => arenas[a].center + arenas[a].toCore * Standoff;
            string gun = ComponentCatalog.CompGunId;
            Vector2 cStart = Stand(0) + Side(0) * 6f;
            Vector2 fieldAt = arenas[3].center + arenas[3].toCore * FieldAhead;
            var rows = new (string key, BlueprintRecord bp, Vector2 at)[]
            {
                ("m.n", Bp(s, "n", gun), navCenter + (Vector2)axis * NavStand),
                ("m.c", Bp(s, "c", gun), cStart),
                ("m.a", Bp(s, "a", gun, Oil), Stand(1) - Side(1) * 2f),
                ("m.b", Bp(s, "b", gun, Burn), Stand(1) + Side(1) * 2f),
                ("m.t", Bp(s, "t", ComponentCatalog.CompBeamId), Stand(2)),
                ("m.f", Bp(s, "f", gun, EnterFuse), Stand(3)),
                ("m.w", Bp(s, "w", gun), fieldAt + Side(3) * WalkSide),
            };
            foreach ((string key, BlueprintRecord bp, Vector2 at) in rows)
            {
                if (!Spawn(c, s, key, bp, at))
                {
                    return;
                }
            }
            CombatKernel k = site.Kernel;
            c.Set("tn.at", GetV(navCenter));
            c.SetInt("tn", FgjEmg3Journey.SpawnTarget(k, navCenter, CombatUnitKind.Enemy, 40f));
            c.Set("c.to", GetV(arenas[0].center - arenas[0].toCore * Standoff + Side(0) * 6f));
            string[] tk = { "tc", "ts", "tt", "tf" };
            for (int a = 0; a < 4; a++)
            {
                c.Set(tk[a] + ".at", GetV(arenas[a].center));
                c.SetInt(tk[a], FgjEmg3Journey.SpawnTarget(k, arenas[a].center, a == 1 ? CombatUnitKind.Structure : CombatUnitKind.Enemy, 8000f));
            }
            c.Set("field.at", GetV(fieldAt));
            c.SetInt("field.ok", k.SpawnField(EmgFieldConst.PortCorona, fieldAt.x, fieldAt.y, FieldR, FieldSeconds, 0, CombatFaction.Player) ? 1 : 0);
            c.Set("arena.ok", $"悬崖围栏区 ({nc.X},{nc.Y})、机器一侧 {axis}；自卫区 ({F(cmdCenter.x, "0")},{F(cmdCenter.y, "0")})；" + string.Join("；", arenas.Select((a, i) => $"靶场 {i + 1} ({F(a.center.x, "0")},{F(a.center.y, "0")})")));
        }

        private static StepOutcome TickArenaFixture(JourneyContext c)
        {
            if (c.Get("arena.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail(c.Get("arena.err"));
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
            if (c.GetInt("field.ok") != 1)
            {
                return StepOutcome.Fail("电晕场夹具没生成");
            }
            CombatKernel k = Home.Kernel;
            if (Team.Any(key => FgjM2Common.IsAttacking(Home, c.GetInt(key))))
            {
                return StepOutcome.Fail($"还没下令就打起来了：[{string.Join("、", Team.Where(key => FgjM2Common.IsAttacking(Home, c.GetInt(key))))}]");
            }
            Vector2 n = FgjM8Journey.PosOf(c.GetInt("m.n")), t = ParseV(c.Get("tn.at"));
            if (FgjEmg1Journey.Corridor(n, t, out _))
            {
                return StepOutcome.Fail($"悬崖围栏没挡住直线（N 车 {GetV(n)} → 敌人 {GetV(t)} 直线可走）：绕障用例不成立");
            }
            bool wInField = (Mask(k, Unit(c, "m.w")) & EmgFieldConst.FieldBits) != 0u;
            if (wInField)
            {
                return StepOutcome.Fail("W 车出厂就在场里（场位已置）：进场用例不成立");
            }
            return StepOutcome.Done($"{Team.Length} 台机器出厂（{string.Join("、", Team.Select(key => key + " #" + c.GetInt(key)))}）；N 车到敌人的直线被悬崖挡住；{c.Get("arena.ok")}");
        }

        // ── 下令 ────────────────────────────────────────────────────────────────

        private static bool OrderAttack(JourneyContext c, string machineKey, string targetKey) => FgjEmg3Journey.OrderAttack(c, machineKey, targetKey);

        private static bool OrderMove(JourneyContext c, string machineKey, Vector2 goal) => FgjEmg3Journey.OrderMove(c, machineKey, goal);

        // ── ① 右键攻击绕障 ───────────────────────────────────────────────────────

        private static StepOutcome TickNav(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatKernel k = Home.Kernel;
            int u = Unit(c, "m.n"), t = c.GetInt("tn");
            // 下令一次：接到攻击命令后不再补点（命令以“无法到达 / 受阻”结束要如实判失败，不能靠重复右键蒙过去）
            if (c.GetInt("nav.cmd") == 0)
            {
                if (!OrderAttack(c, "m.n", "tn"))
                {
                    return c.StepElapsed < 60 ? StepOutcome.Wait
                        : StepOutcome.Fail($"右键敌人 60 秒没接到攻击命令（选中 {GameRoot.HomeValley.SelectedMachineLogicId}、命令 {FgjM2Common.CommandOf(Home, c.GetInt("m.n"))}、最近事件 {string.Join(" / ", GameRoot.HomeValley.SquadCommands.RecentEvents.Skip(Math.Max(0, GameRoot.HomeValley.SquadCommands.RecentEvents.Count - 3)))}）");
                }
                c.SetInt("nav.cmd", 1);
            }
            if (k.IsAlive(t) && !FgjM2Common.IsAttacking(Home, c.GetInt("m.n")))
            {
                return StepOutcome.Fail($"攻击命令没打完就结束了（敌人还活着）：最近事件 {string.Join(" / ", GameRoot.HomeValley.SquadCommands.RecentEvents.Skip(Math.Max(0, GameRoot.HomeValley.SquadCommands.RecentEvents.Count - 3)))}；N 车在 {GetV(FgjM8Journey.PosOf(c.GetInt("m.n")))}");
            }
            if (k.TryGetNavState(u, out CombatNavState st, out _) && st == CombatNavState.Following)
            {
                c.SetInt("nav.routed", 1);
            }
            Vector2 me = FgjM8Journey.PosOf(c.GetInt("m.n"));
            GridCell cell = NavService.CellOf(me.x, me.y);
            if (!NavService.PassableNow(cell.X, cell.Y, BinGames.Sim.Nav.NavConst.ClassPlayer) && c.Get("nav.bad", string.Empty).Length == 0)
            {
                c.Set("nav.bad", $"{cell.X},{cell.Y}");
            }
            Vector2 tn = ParseV(c.Get("tn.at"));
            if (Mathf.Max(Mathf.Abs(me.x - tn.x), Mathf.Abs(me.y - tn.y)) < NavRing - 0.5f)
            {
                c.SetInt("nav.inside", 1); // 进了围栏（只能从背面缺口进）
            }
            if (k.IsAlive(t))
            {
                return StepOutcome.Wait;
            }
            if (c.GetInt("nav.routed") != 1 || c.GetInt("nav.inside") != 1 || c.Get("nav.bad", string.Empty).Length > 0)
            {
                return StepOutcome.Fail($"敌人被击毁，但寻路 {c.GetInt("nav.routed") == 1}、进过围栏 {c.GetInt("nav.inside") == 1}、踩到不可走格 {c.Get("nav.bad", "无")}（应走层级寻路从缺口进去、全程可走格）");
            }
            return StepOutcome.Done($"N 车射程外改走层级寻路（Following），绕到背面缺口进了围栏开火，第 {F(c.StepElapsed)} 秒击毁敌人，全程不踏进不可走格（FG-GAP-140）");
        }

        // ── ② 移动途中自卫 ───────────────────────────────────────────────────────

        private static StepOutcome TickCmd(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatSite site = Home;
            CombatKernel k = site.Kernel;
            int u = Unit(c, "m.c"), t = c.GetInt("tc");
            if (c.GetLong("cmd.hp0") == 0)
            {
                c.SetLong("cmd.hp0", (long)(Hp(k, t) * 100f));
                c.SetLong("cmd.eng0", site.SelfDefenseEngagements);
            }
            Vector2 goal = ParseV(c.Get("c.to"));
            if (!OrderMove(c, "m.c", goal))
            {
                return StepOutcome.Wait;
            }
            bool hasCmd = site.TryGetCommand(u, out CombatCommand cmd);
            if (hasCmd && cmd.Kind != CombatCommandKind.Move)
            {
                c.Set("cmd.other", cmd.Kind.ToString());
            }
            Vector2 me = FgjM8Journey.PosOf(c.GetInt("m.c"));
            string want = GameText.Get("doctrine.selfdefense");
            if (site.IsSelfDefending(u))
            {
                c.SetInt("cmd.engaged", 1);
                if (c.GetInt("cmd.hover") == 0)
                {
                    FgjM3Common.TryHover(me);
                    string body = UiTooltip.Content?.Body;
                    if (StatusTagHover.HoveredUnit == u && body != null && body.Contains(want))
                    {
                        c.SetInt("cmd.hover", 1);
                    }
                }
            }
            float hp0 = c.GetLong("cmd.hp0") / 100f, hp = Hp(k, t);
            bool arrived = Vector2.Distance(me, goal) <= 2f && !hasCmd;
            if (!arrived)
            {
                return c.StepElapsed < 170 ? StepOutcome.Wait : StepOutcome.Fail($"C 车没走到目的地：在 {GetV(me)}（目的地 {GetV(goal)}），命令 {(hasCmd ? cmd.Kind.ToString() : "无")}");
            }
            long eng = site.SelfDefenseEngagements - c.GetLong("cmd.eng0");
            if (hp >= hp0 - 0.01f || c.GetInt("cmd.engaged") != 1 || eng < 1 || c.Get("cmd.other", string.Empty).Length > 0 || c.GetInt("cmd.hover") != 1)
            {
                return StepOutcome.Fail($"C 车开过敌人旁边：敌人耐久 {F(hp0)} → {F(hp)}（应掉血）、正在自卫交战位 {c.GetInt("cmd.engaged") == 1}、自卫交战开始 +{eng}、命令中途变成 {c.Get("cmd.other", "（始终是移动）")}、" +
                                        $"悬停提示含“{want}” {c.GetInt("cmd.hover") == 1}（提示 {UiTooltip.Content?.Title}/{UiTooltip.Content?.Body}）");
            }
            return StepOutcome.Done($"C 车按右键移动开过敌人旁边：途中原地还手（敌人耐久 {F(hp0, "0")} → {F(hp, "0")}，自卫交战开始 +{eng} 次），命令始终是移动、不追击，走到目的地；悬停提示写“{want}”（FG-GAP-142、FGR-BASE-020）");
        }

        // ── ③ 敌方结构单位挨斑块 ─────────────────────────────────────────────────

        private static StepOutcome TickOil(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!OrderAttack(c, "m.a", "ts"))
            {
                return StepOutcome.Wait;
            }
            CombatKernel k = Home.Kernel;
            int s = c.GetInt("ts");
            uint oilBit = 1u << EmgMatterConst.Oil;
            if ((Mask(k, s) & oilBit) == 0u)
            {
                return c.StepElapsed < 100 ? StepOutcome.Wait : StepOutcome.Fail($"敌方结构单位脚下 100 秒没记上油位（物质掩码 {Mask(k, s):X}）：结构单位仍被接触结算跳过（FG-GAP-217）");
            }
            return StepOutcome.Done($"A 车攻击敌方结构单位：脚下留下油斑块，结构单位物质掩码记上油位（{Mask(k, s):X}，内核接触结算到结构单位）");
        }

        private static StepOutcome TickBurn(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatKernel k = Home.Kernel;
            int s = c.GetInt("ts");
            if (c.GetLong("burn.hp0") == 0)
            {
                c.SetLong("burn.hp0", (long)(Hp(k, s) * 100f));
            }
            if (!OrderAttack(c, "m.b", "ts"))
            {
                return StepOutcome.Wait;
            }
            uint fireBit = 1u << EmgMatterConst.Fire;
            int coat = k.MatterRow(EmgMatterConst.Fire).CoatBit;
            bool onFire = (Mask(k, s) & fireBit) != 0u, burning = HasStatus(k, s, coat);
            float hp0 = c.GetLong("burn.hp0") / 100f, hp = Hp(k, s);
            if (!onFire || !burning || hp >= hp0 - 0.01f)
            {
                return c.StepElapsed < 100 ? StepOutcome.Wait
                    : StepOutcome.Fail($"敌方结构单位挨明火：明火位 {onFire}、燃烧位 {coat}（{burning}）、耐久 {F(hp0)} → {F(hp)}（FGR-EMG-083 单位含敌人）");
            }
            return FgjEmg3Journey.Cleared(c, StepOutcome.Done($"B 车点火：明火落在敌方结构单位脚下，结构单位物质掩码记上明火位（{Mask(k, s):X}）、挂燃烧位 {coat}、耐久 {F(hp0, "0")} → {F(hp, "0")}"), "ts");
        }

        // ── ④ 切割束 0.5 秒一段 ─────────────────────────────────────────────────

        private static StepOutcome TickBeam(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (!OrderAttack(c, "m.t", "tt"))
            {
                return StepOutcome.Wait;
            }
            CombatKernel k = Home.Kernel;
            int t = c.GetInt("tt");
            long hp = (long)(Hp(k, t) * 100f);
            // DEBT-EMGREXIT-02（FG15-QA-03）：按攻击结果事件计段（出手者 = T 车的编队攻击“命中”），不再按目标掉血计——自卫、余火同时打在目标上不会被算进切割束段数。
            int hits = GameRoot.HomeValley.SquadCommands.HitOutcomes(c.GetInt("m.t"));
            if (c.GetInt("beam.init") == 0)
            {
                c.SetInt("beam.init", 1);
                c.SetInt("beam.h0", hits);
                c.SetLong("beam.hp0", hp);
                return StepOutcome.Wait;
            }
            double now = GameClock.GameSeconds;
            if (c.Get("beam.t0", string.Empty).Length == 0 && hits > c.GetInt("beam.h0"))
            {
                c.Set("beam.t0", now.ToString("R", CultureInfo.InvariantCulture));
                c.SetInt("beam.h1", hits); // 第一段（之后的段数 = 此后新增的命中结果事件）
            }
            if (c.Get("beam.t0", string.Empty).Length > 0)
            {
                c.SetInt("beam.n", hits - c.GetInt("beam.h1"));
            }
            if (c.Get("beam.t0", string.Empty).Length == 0)
            {
                return c.StepElapsed < 100 ? StepOutcome.Wait : StepOutcome.Fail("T 车 100 秒没切到敌人");
            }
            double t0 = double.Parse(c.Get("beam.t0"), CultureInfo.InvariantCulture);
            if (now - t0 < 3.0)
            {
                return StepOutcome.Wait;
            }
            int n = c.GetInt("beam.n");
            float dealt = (c.GetLong("beam.hp0") - hp) / 100f;
            if (n < 5 || n > 7)
            {
                return StepOutcome.Fail($"切割束第一段之后 {F(now - t0, "0.##")} 游戏秒切了 {n} 段（按 T 车的攻击结果事件计；应 5～7：每 0.5 秒一段，emg.trig.beam_interval）；目标累计掉血 {F(dealt)}");
            }
            return FgjEmg3Journey.Cleared(c, StepOutcome.Done($"T 车切割束：第一段之后 {F(now - t0, "0.##")} 游戏秒又切 {n} 段（按 T 车的攻击结果事件计，出手者 = T 车；每 0.5 秒一段，P9），目标累计掉血 {F(dealt)}"), "tt");
        }

        // ── ⑤ 场的进场 / 离场 ───────────────────────────────────────────────────

        private static StepOutcome TickFuse(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatKernel k = Home.Kernel;
            if (c.GetInt("fuse.init") == 0)
            {
                c.SetInt("fuse.init", 1);
                c.SetLong("fuse.s0", k.EmgBusStats.SubSpawned);
            }
            if (!OrderAttack(c, "m.f", "tf"))
            {
                return StepOutcome.Wait;
            }
            long subs = k.EmgBusStats.SubSpawned - c.GetLong("fuse.s0");
            if (subs < 1)
            {
                return c.StepElapsed < 100 ? StepOutcome.Wait : StepOutcome.Fail("F 车开火 100 秒，弹体穿过电晕场没有分裂（子载荷 +0）：弹体进场不发 evt.enter（FGR-EMG-060）");
            }
            FgjEmg1Journey.Program(Home, c.GetInt("m.f"), out _, out int prog);
            return FgjEmg3Journey.Cleared(c, StepOutcome.Done($"F 车 [入场引信] 的弹体穿过电晕场：分裂 {subs} 次（子载荷计数，武器程序 {prog}）"), "tf");
        }

        private static StepOutcome TickWalkIn(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            Vector2 field = ParseV(c.Get("field.at"));
            if (!OrderMove(c, "m.w", field))
            {
                return StepOutcome.Wait;
            }
            uint m = Mask(Home.Kernel, Unit(c, "m.w"));
            uint corona = EmgFieldConst.FieldBit(EmgFieldConst.PortCorona);
            if ((m & corona) == 0u || Home.TryGetCommand(Unit(c, "m.w"), out _))
            {
                return c.StepElapsed < 100 ? StepOutcome.Wait : StepOutcome.Fail($"W 车走到场中心（在 {GetV(FgjM8Journey.PosOf(c.GetInt("m.w")))}，场 {GetV(field)}）掩码 {m:X} 没有电晕场位 {corona:X}");
            }
            return StepOutcome.Done($"W 车走进电晕场：掩码 {m:X} 记上电晕场位 {corona:X}（物质位 {m & EmgFieldConst.MatterBits:X} 不受影响）");
        }

        private static StepOutcome TickWalkOut(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            Vector2 back = ParseV(c.Get("m.w.at"));
            if (!OrderMove(c, "m.w", back))
            {
                return StepOutcome.Wait;
            }
            uint m = Mask(Home.Kernel, Unit(c, "m.w"));
            if ((m & EmgFieldConst.FieldBits) != 0u || Vector2.Distance(FgjM8Journey.PosOf(c.GetInt("m.w")), back) > 2f)
            {
                return c.StepElapsed < 100 ? StepOutcome.Wait : StepOutcome.Fail($"W 车走出场：在 {GetV(FgjM8Journey.PosOf(c.GetInt("m.w")))}，掩码 {m:X}（场位应清掉）");
            }
            return StepOutcome.Done($"W 车走出电晕场：场位清掉（掩码 {m:X}）");
        }

        // ── ⑥ 存读档 ─────────────────────────────────────────────────────────────

        private static string Fnv(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char ch in s ?? string.Empty)
            {
                h = (h ^ ch) * 1099511628211UL;
            }
            return h.ToString("X16", CultureInfo.InvariantCulture);
        }

        private static StepOutcome TickPauseForSave(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (!PauseMenuUIToolkit.IsOpen)
            {
                return StepOutcome.Retry("按 Esc 后暂停菜单没有打开");
            }
            CombatSite site = Home;
            CombatSiteRecord rec = site.Snapshot();
            c.Set("pre.site", rec.SiteId);
            c.Set("pre.payload", Fnv(rec.Payload));
            c.SetInt("pre.len", rec.Payload.Length);
            c.Set("pre.hash", site.Kernel.StateHash().ToString("X16", CultureInfo.InvariantCulture));
            c.SetLong("pre.steps", site.Kernel.Steps);
            c.SetInt("slot", CampaignSession.ActiveSlotIndex);
            c.Set("pre.mask", Mask(site.Kernel, Unit(c, "m.w")).ToString("X", CultureInfo.InvariantCulture));
            return StepOutcome.Done($"暂停菜单打开（世界暂停）；存档前：内核第 {site.Kernel.Steps} 步、状态哈希 {c.Get("pre.hash")}、W 车掩码 {c.Get("pre.mask")}、活斑块 {site.Kernel.PatchCount}；快照 {rec.Payload.Length} 字符");
        }

        private static StepOutcome TickMenuAfterSave(JourneyContext c)
        {
            if (JourneyInput.FindActiveButton("m_btn_Load") == null || c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            LoadResult onDisk = CampaignSaveService.Load(c.GetInt("slot"));
            if (!onDisk.Success)
            {
                return StepOutcome.Fail($"刚保存的存档读不出来：{onDisk.Outcome}/{onDisk.Reason}");
            }
            CombatSiteRecord rec = onDisk.State.Combat?.Sites?.FirstOrDefault(x => x?.SiteId == c.Get("pre.site"));
            if (rec == null || Fnv(rec.Payload) != c.Get("pre.payload") || rec.Payload.Length != c.GetInt("pre.len") || rec.KernelSteps != c.GetLong("pre.steps"))
            {
                return StepOutcome.Fail($"存档里的家园内核快照与存档那一刻不一致（{(rec == null ? "没有这个地点" : $"第 {rec.KernelSteps} 步、{rec.Payload?.Length} 字符、摘要 {Fnv(rec.Payload)}")}；存档前摘要 {c.Get("pre.payload")}）");
            }
            return StepOutcome.Done($"回到主菜单；存档（槽位 {c.GetInt("slot") + 1}）里家园内核快照与存档那一刻逐字节一致（第 {rec.KernelSteps} 步、{rec.Payload.Length} 字符）");
        }

        private static StepOutcome TickLoaded(JourneyContext c)
        {
            CombatSite site = Home;
            if (site?.Kernel == null || !GameRoot.AnyRegionActive || c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            if (site.Kernel.Steps < c.GetLong("pre.steps"))
            {
                return StepOutcome.Fail($"读档后家园内核第 {site.Kernel.Steps} 步，早于存档的第 {c.GetLong("pre.steps")} 步（没有从快照恢复，被重建了）");
            }
            LoadResult onDisk = CampaignSaveService.Load(c.GetInt("slot"));
            CombatSiteRecord rec = onDisk.Success ? onDisk.State.Combat?.Sites?.FirstOrDefault(x => x?.SiteId == c.Get("pre.site")) : null;
            string hash;
            using (var fresh = new CombatKernel(site.Kernel.Config))
            {
                CombatLoadResult lr = rec == null ? CombatLoadResult.Truncated : fresh.Load(Convert.FromBase64String(rec.Payload));
                hash = lr == CombatLoadResult.Ok ? fresh.StateHash().ToString("X16", CultureInfo.InvariantCulture) : "读不回：" + lr;
            }
            uint corona = EmgFieldConst.FieldBit(EmgFieldConst.PortCorona);
            uint m = Mask(site.Kernel, Unit(c, "m.w"));
            if (hash != c.Get("pre.hash") || (m & corona) == 0u)
            {
                return StepOutcome.Fail($"读档后不一致：快照读回状态哈希 {hash}（存档那一刻 {c.Get("pre.hash")}）；W 车掩码 {m:X}（存档前 {c.Get("pre.mask")}，应仍有电晕场位 {corona:X}）");
            }
            return StepOutcome.Done($"读档进入归还谷地：家园内核从快照恢复（现第 {site.Kernel.Steps} 步 ≥ 存档第 {c.GetLong("pre.steps")} 步），快照读回状态哈希 {hash} = 存档那一刻；W 车掩码 {m:X} 仍有电晕场位");
        }
    }
}
