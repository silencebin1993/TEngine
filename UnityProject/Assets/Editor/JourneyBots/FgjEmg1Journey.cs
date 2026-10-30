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
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.CircuitBoard;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using GameLogic.UI.SilentMind;
using GameLogic.View;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// EMG1-E2E-01：FG-EMG 阶段 1 出口旅程 FGJ-EMG-1（FG19 第 16 节；LANDING-PLAN 6.3）。从主菜单“新建”出发（固定测试种子 42、标准难度），全程正式输入，
    /// RTS 口径：左键选择、右键地面 = 移动、右键敌人 = 攻击；按钮 / 快捷键只切状态。
    ///
    /// 原文：新建存档 → 编一台 AI 机器 [分裂, 漏油]，看到每颗子弹药各漏一摊油 → 信号核 1 号槽放延时引信，接入该机器，程序树显示机器固件被装进子弹药 →
    /// 开火看到飞行中空爆 → 打开因果链查看来源 → 离开，在飞子弹药走完 → 存档读档一致。
    ///
    /// 轻量口径（用户 2026-10-12）：每个阶段 1 新系统至少被真实输入触发一次并断言结果——程序编译与程序树预览（蓝图编辑器两个固件下拉 + 接入口 → 保存）、
    /// 触发器与子载荷 / 排放（AI 开火：每颗子弹药各一摊油，按内核计数逐颗对账）、信号核注入与继承（刻印 + 装入 → 预览“你接入时”一栏）、
    /// 飞行弹体与定时空爆（直控开火）、因果链叠加（临时默认键 Ctrl+T 真实按键）、信号离开时在飞子弹药按原程序走完（FGR-EMG-037 第 1 条）、存读档（快照逐字节 + 状态哈希）。
    /// 进度夹具两处，写在步骤标题与报告里：<see cref="ApplyFixture"/>（三条固件记为已获得——机器人为省时用开发入口发放，但先断言每条在正常游戏里有来源；
    /// 刻印费用不足时补足）；<see cref="ApplySpawnFixture"/>（按刚保存的蓝图在核心旁出厂一台（出厂由 FGJ-M1 覆盖）+ 覆盖里放一处 1 级、1 人驻军的铸造据点当靶子）。
    /// 用法：bash tools/unity-journey.sh FGJ-EMG-1；断点 emg_fix / wait_gen / ai_oil / up_fire（--from 续跑）。
    /// </summary>
    public static class FgjEmg1Journey
    {
        public const string Id = "FGJ-EMG-1";

        public const int TestSeed = FgjM0Journey.TestSeed;

        internal const string Split = "fw_split", Oil = "fw_oilleak", Fuse = "fw_delayfuse";
        private const string Host = "[HomeValleyCircuitBoardHost]";
        private const string Foundry = EnemyDefs.FactionFoundry;

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
                S("emg_fix", "进度夹具：正式表 emg.stage = 1（阶段 1 生效）；分裂 / 漏油 / 延时引信与铸造重炮记为已获得（先断言每条在正常游戏里都有来源）", 15, ApplyFixture, TickFixture),

                // 刻印芯片要装配站有电：开局发电机是坏的，按正式入口修（FGJ-M1 同一路径），等待段 3 倍速
                S("speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("sel_worker", "左键点一台工程机", 15, c => FgjM1Journey.ClickMachine(c, "workerA"), c => FgjM1Journey.TickSelected(c, "workerA"), retries: 2),
                S("repair_gen", "右键点受损的发电机（情境命令：修复）", 15, c => FgjM1Journey.RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => FgjM1Journey.TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("wait_gen", "等发电机修好（3 倍速）", 300, null, TickGeneratorDone),

                // ── 1. 编一台 AI 机器 [分裂, 漏油] ──
                S("cb_open", "点“蓝图编辑器”入口", 10, c => FgjM1Journey.ClickUi(c, Host, "EntryToggleButton", when: () => !(GameRoot.HomeValley?.IsCircuitBoardPanelOpen ?? false)),
                    FgjM1Journey.TickEditorOpen, retries: 1),
                S("cb_pick", "在蓝图列表点 ERC-003 蓝图", 10, FgjM1Journey.ClickErc003Row, FgjM1Journey.TickErc003Open, retries: 1),
                S("cb_cannon", "主组件下拉选“铸造重炮”（射弹载体，射程 20：延时引信 0.6 秒 × 弹速 22 = 13 格处空爆，打得到更远的目标）", 10, PickCannon, TickCannon, retries: 1),
                S("cb_fw0", "固件 1 下拉选“分裂”", 10, c => PickFirmware(c, 0, Split), c => TickFirmware(c, 0, Split), retries: 1),
                S("cb_fw1", "固件 2 下拉选“漏油”：程序树（AI 驾驶一栏）根 = 分裂，子弹药一代 = 漏油", 10, c => PickFirmware(c, 1, Oil), TickAiTree, retries: 1),
                S("cb_slot", "点选导线经过的空格", 10, FgjM1Journey.ClickPathSlot, FgjM1Journey.TickSlotPicked, retries: 1),
                S("cb_mark", "点“标为接入口”，看双态预览（信号核还空着）", 10, c => FgjM1Journey.ClickUi(c, Host, "UplinkToggleButton"), FgjM1Journey.TickMarkedEmptyCore, retries: 1),
                S("cb_save", "点“保存”", 10, c => FgjM1Journey.ClickUi(c, Host, "SaveButton"), TickSaved, retries: 1),
                S("cb_close", "按 Esc 关闭蓝图编辑器", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, GameRoot.HomeValley.IsCircuitBoardPanelOpen), FgjM1Journey.TickEditorClosed, retries: 1),
                S("spawn_fix", "出厂夹具：按刚保存的蓝图在训练靶旁出厂一台 ERC-003（出厂由 FGJ-M1 覆盖）；同方向 36 格、覆盖里放一处 1 级、1 人驻军的铸造据点（接入后直控的远处靶子）", 20, ApplySpawnFixture, TickSpawnFixture),

                // ── 2. AI 开火：每颗子弹药各漏一摊油 ──
                S("sel", "左键点这台 ERC-003 选中", 15, c => FgjM1Journey.ClickMachine(c, "emg.m"), c => FgjM1Journey.TickSelected(c, "emg.m"), retries: 2),
                S("speed1", "点世界栏 1 倍速（下面按游戏秒对账）", 10, c => ClickSpeed("WorldSpeed1"), c => TickSpeed(c, 1f), retries: 1),
                S("ai_oil", "右键据点 A 前的地面叫醒驻军、右键驻军攻击；AI 按交战间隔开火：[分裂] 命中放出子弹药、每颗子弹药结算处各一摊油（内核计数逐颗对账）", 160, null, TickAiOil),
                S("chain_open", "按“因果链”键（临时默认 Ctrl+T）：叠加显示出现，写明来源", 15, c => JourneyInput.PressAction(GameActionId.CausalChain), TickChainOpen, retries: 2),
                S("chain_close", "再按一次关闭叠加", 10, c => JourneyInput.PressAction(GameActionId.CausalChain), c => c.StepElapsed < 0.6 ? StepOutcome.Wait : CausalChainOverlay.IsOpen ? StepOutcome.Retry("叠加还开着") : StepOutcome.Done($"因果链叠加关闭（{CausalChainOverlay.LineCount} 行）"), retries: 2),
                S("back", "右键出厂点地面，机器开回去（接入段从这里出发）", 95, RightClickHome, TickBack),

                // ── 3. 信号核 1 号槽放延时引信 ──
                S("sc_open", "按信号核键（默认 P）打开信号核面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreOpen, retries: 1),
                S("sc_print", "刻印下拉选“延时引信”，点“刻印”", 10, c => PrintFirmware(c, Fuse), c => TickPrinted(c, Fuse), retries: 1),
                S("sc_equip", "点“装入 1 号槽”", 10, c => FgjM1Journey.ClickUi(c, "[SignalCoreHost]", "SignalEquip"), c => TickEquipped(c, Fuse), retries: 1),
                S("sc_close", "再按信号核键关闭面板", 10, c => FgjM1Journey.PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), FgjM1Journey.TickCoreClosed, retries: 1),
                S("cb_reopen", "重开蓝图编辑器看 ERC-003：“你接入时”一栏根 = 延时引信，子弹药一代继承分裂、漏油（机器固件被装进子弹药）；AI 一栏不变", 15, ReopenEditor, TickUplinkTree, retries: 1),
                S("cb_close2", "按 Esc 关闭蓝图编辑器", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, GameRoot.HomeValley.IsCircuitBoardPanelOpen), FgjM1Journey.TickEditorClosed, retries: 1),

                // ── 4. 接入、开火空爆、离开 ──
                S("up", "命令栏机器列表点这台机器：接入（镜头直控），武器换成接入程序", 30, c => FgjM1Journey.ClickMachineList(c, c.GetInt("emg.m")), TickUplinked, retries: 3),
                S("up_fire", "直控开到离据点驻军 14～19 格处左键点它开火：根弹体飞出 → 立刻按接入 / 退出接入键（默认 M）离开；离开后在飞的弹体照原程序飞行中空爆、子弹药走完（武器已回 AI 程序）", 150, null, TickFireAndLeave, retries: 1),

                // ── 5. 存档读档一致 ──
                S("save_esc", "按 Esc 打开暂停菜单（世界暂停）：记下家园内核快照与状态哈希", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseForSave, retries: 1),
                S("save_quit", "点“保存并返回主菜单”，确认", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), FgjM1Journey.TickSaveConfirm),
                S("menu_back", "回到主菜单：存档里的家园内核快照与存档那一刻逐字节一致", 60, null, TickMenuAfterSave),
                S("load_list", "点“读取”，点刚才的存档槽", 20, c => FgjM1Journey.ClickUgui(c, "m_btn_Load"), FgjM1Journey.TickLoadSlot),
                S("loaded", "读档进入归还谷地：家园内核从快照恢复（不重建），快照读回的状态哈希 = 存档那一刻；机器仍按 AI 程序 [分裂, 漏油]", 120, null, TickLoaded),
            };
            return new JourneyDef
            {
                Id = Id,
                Title = "FG-EMG 阶段 1 出口：编 [分裂, 漏油] AI 机器 → 每颗子弹药各一摊油 → 信号核延时引信接入、程序树显示机器固件进子弹药 → 直控开火飞行中空爆 → 因果链 → 离开后在飞子弹药走完 → 存读档一致",
                Seed = TestSeed,
                TotalTimeoutSeconds = 1500,
                OnFinish = Cleanup,
                Version = 1,
                CheckpointAfter = new[] { "emg_fix", "wait_gen", "ai_oil", "up_fire" },
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static string F(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);

        private static string Label(int id) => FgjM1Journey.Label(id);

        private static long NowMs() => FgjM2Common.NowMs();

        private static string Name(string fw) =>
            fw == ComponentCatalog.CompCannonId ? ComponentCatalog.All[fw].DisplayName : GameText.Get("firmware." + fw + ".name");

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
                EmgBusStats st = site.Kernel.EmgBusStats;
                c.Log($"阶段 1 计数（家园内核全程）：分裂 {st.Splits}、子弹药 {st.SubSpawned}、飞行弹体出手 {st.FlightShots}、触发唤醒 {st.TriggerWakes}、链深截断 {st.DepthCut}、" +
                      $"事件入队 {st.Enqueued} / 处理 {st.Processed} / 丢失 {st.Lost}；定时事件 {site.Kernel.EmgPortCountOf(EmgEventKind.Timer)}、终结事件 {site.Kernel.EmgPortCountOf(EmgEventKind.Expire)}");
            }
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM3Common.FrameReport("全程"));
            JourneyCommon.Cleanup(c);
        }

        // ── 夹具 ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 开局夹具（轻量口径）：三条固件记为已获得（MechanicalContentUnlock 读的同一张已解锁表；获取由掉落 / 三选一 / 研发覆盖，FGT-EMG-006 另有自检）。
        /// 发放前逐条记下正常游戏来源（开局即有 / 三选一候选池 / 掉落表 / 杂兵残骸固件 / 数据复原），一条都没有就失败（卡片“须断言该固件在掉落 / 候选池里可得”）。
        /// </summary>
        private static void ApplyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("fix.src", string.Empty).Length > 0)
            {
                return;
            }
            var src = new List<string>();
            var missing = new List<string>();
            foreach (string fw in new[] { Split, Oil, Fuse, ComponentCatalog.CompCannonId })
            {
                var where = new List<string>();
                if (MechanicalContentUnlock.IsUnlocked(s, fw))
                {
                    where.Add("开局已有");
                }
                if (LootPickCatalog.Rows.Any(r => r?.Item?.Id == fw))
                {
                    where.Add("三选一候选池");
                }
                if (LootCatalog.Rows.Any(r => r?.Item?.Id == fw))
                {
                    where.Add("掉落表");
                }
                foreach (string f in new[] { EnemyDefs.FactionFoundry, EnemyDefs.FactionClarity, EnemyDefs.FactionSilent, FirmwareKinds.FactionReclaim })
                {
                    if (LootCatalog.EnemyFirmwareOf(f).Contains(fw))
                    {
                        where.Add(f + " 杂兵残骸");
                        break;
                    }
                }
                if (FirmwareRestoreService.IsRestorable(fw))
                {
                    where.Add($"数据复原（表来源 {FirmwareKinds.SourceOf(fw)}）");
                }
                if (fw == ComponentCatalog.CompCannonId && MechanicalContentFacade.TryGet(fw, out MechanicalContentDef cdef))
                {
                    where.Add($"远征带回解析（来源 {cdef.Source}，FGJ-M1 覆盖）");
                }
                bool obtainable = fw == ComponentCatalog.CompCannonId || FirmwareKinds.IsObtainable(fw);
                src.Add($"{Name(fw)}（{fw}）：{(where.Count == 0 ? "无" : string.Join("、", where))}；可获得 {obtainable}");
                if (where.Count == 0 || !obtainable)
                {
                    missing.Add(fw);
                }
            }
            c.Set("fix.src", string.Join("；", src));
            c.Set("fix.missing", string.Join(",", missing));
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(new[] { Split, Oil, Fuse, ComponentCatalog.CompCannonId }).Distinct().ToArray();
            MachineRecord worker = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && !m.IsInFactory && m.RegionId == HomeValleyLayout.RegionId)
                .OrderBy(m => m.ChassisId == HomeValleyLayout.Erc001ChassisId ? 0 : 1).ThenBy(m => m.LogicId).FirstOrDefault();
            c.SetInt("workerA", worker?.LogicId ?? 0);
            int need = SignalCoreService.FirmwareChipPrintScrap * 2;
            c.SetInt("fix.scrap0", s.Scrap);
            if (s.Scrap < need)
            {
                s.Scrap = need;
            }
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
            if (cat.RawStage != 1f || cat.EffectiveStage != 1)
            {
                return StepOutcome.Fail($"正式表 emg.stage = {cat.RawStage}（生效 {cat.EffectiveStage}），阶段 1 出口应为 1（DEBT-EMG1KRN01-02）");
            }
            bool decl = EmgCatalog.IsOwnerOnDeclTrack(Split) && EmgCatalog.IsOwnerOnDeclTrack(Oil) && EmgCatalog.IsOwnerOnDeclTrack(Fuse);
            return decl
                ? StepOutcome.Done($"正式表 emg.stage = 1（生效 1），三条固件走声明轨；来源：{c.Get("fix.src")}；废料 {c.GetInt("fix.scrap0")} → {St.Scrap}")
                : StepOutcome.Fail("阶段 1 下分裂 / 漏油 / 延时引信没有全部走声明轨");
        }

        private static StepOutcome TickGeneratorDone(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            BuildingRecord g = FgjM1Journey.Building(HomeValleyLayout.BuildingTypeGenerator);
            if (g != null && g.ConstructionState == BuildingConstructionState.Operational)
            {
                return StepOutcome.Done($"发电机修好（{F(c.StepElapsed, "0")} 秒，3 倍速），废料 {St.Scrap}");
            }
            return FgjM1Journey.RepairOrder(HomeValleyLayout.BuildingTypeGenerator) == null && c.StepElapsed > 5
                ? StepOutcome.Fail($"发电机还没修好，修复工单却没了（废料 {St.Scrap}）")
                : StepOutcome.Wait;
        }

        /// <summary>
        /// 出厂夹具：在家园训练靶旁 6 格（AI 交战半径 12 格内，按交战间隔自动出手——不还手，AI 段不掉血）出厂一台 ERC-003（刚保存的现役版本）；
        /// 再在同一方向离机器 36 格、覆盖里的地方放一处 1 级铸造据点（驻军 1、不增援，没人靠近时休眠）——接入后直控开过去当远处的靶子。
        /// </summary>
        private static void ApplySpawnFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt("emg.m") > 0)
            {
                return;
            }
            Vector2 dummy = HomeValleyLayout.LowThreatTargetPosition;
            Vector2 core = FgjM8Journey.CorePos;
            BlueprintRecord bp = FgjM1Journey.Erc003Record();
            // 8 个正方向（直控 WASD 的八向正好对上，开过去不会斜着蹭地形）：机器离训练靶 6 格，据点在同一方向再往外 36 格（迎战半径 18 格外、AI 射程外），
            // 两点之间 3 格宽的走廊全部能走。
            var why = new List<string>();
            for (int k = 0; k < 32 && c.GetInt("emg.m") == 0; k++)
            {
                float ang = (k % 8) * 45f * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                var mc = new GridCell(Mathf.RoundToInt(dummy.x + dir.x * (4f + 2f * (k / 8))), Mathf.RoundToInt(dummy.y + dir.y * (4f + 2f * (k / 8))));
                var mp = new Vector2(mc.X, mc.Y);
                var op = mp + dir * 36f;
                var oc = new GridCell(Mathf.RoundToInt(op.x), Mathf.RoundToInt(op.y));
                string bad = !Corridor(mp, op, out string block) ? "走廊不通 " + block
                    : Vector2.Distance(op, core) < 20f ? "据点离核心太近"
                    : !SignalCoverageService.Sample(HomeValleyLayout.RegionId, op).Covered ? "据点不在覆盖里"
                    : HomeGridService.BuildingAt(s, mc) != null ? "出厂点有建筑" : null;
                if (bad != null)
                {
                    why.Add($"{k % 8 * 45}°/{4 + 2 * (k / 8)}格：{bad}");
                    continue;
                }
                // 据点 A（AI 段：右键叫醒、右键攻击）：与据点 B 不同方向、离机器约 26 格（迎战半径 18 格外）、覆盖里、能走、离 B ≥ 40 格。
                GridCell? ac = null;
                for (int j = 2; j <= 6 && !ac.HasValue; j++)
                {
                    float a2 = ang + j * 45f * Mathf.Deg2Rad;
                    GridCell g = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(mp.x + Mathf.Cos(a2) * 26f), Mathf.RoundToInt(mp.y + Mathf.Sin(a2) * 26f)), 4);
                    var gp = new Vector2(g.X, g.Y);
                    if (Vector2.Distance(gp, op) >= 40f && Vector2.Distance(gp, mp) >= 22f && Vector2.Distance(gp, core) >= 20f && SignalCoverageService.Sample(HomeValleyLayout.RegionId, gp).Covered)
                    {
                        ac = g;
                    }
                }
                if (!ac.HasValue)
                {
                    why.Add($"{k % 8 * 45}°：找不到据点 A 的位置");
                    continue;
                }
                MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp.BlueprintId, HomeValleyLayout.RegionId, mp, 100f, 100f, "Player", bp.ActiveVersion,
                    bp.Versions.First(v => v.Version == bp.ActiveVersion).CompileSignature);
                if (!r.Success || !MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
                {
                    c.Set("spawn.err", "出厂失败：" + r.Message);
                    return;
                }
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
                OutpostRecord oa = WorldOutpostSystem.SpawnOutpost(s, Foundry, ac.Value, withPatrol: false);
                oa.Kind = WorldOutpostSystem.KindTerritory;
                oa.Tier = 1;
                oa.Garrison = 1;
                oa.GarrisonCap = 1;
                oa.ReinforceStopped = true;
                c.Set("opa.id", oa.OutpostId);
                c.Set("opa.at", $"{ac.Value.X},{ac.Value.Y}");
                OutpostRecord o = WorldOutpostSystem.SpawnOutpost(s, Foundry, oc, withPatrol: false);
                o.Kind = WorldOutpostSystem.KindTerritory;
                o.Tier = 1;
                o.Garrison = 1;
                o.GarrisonCap = 1;
                o.ReinforceStopped = true;
                c.SetInt("emg.m", r.LogicId);
                c.Set("roster", r.LogicId.ToString(CultureInfo.InvariantCulture));
                c.Set("op.id", o.OutpostId);
                c.Set("op.at", $"{oc.X},{oc.Y}");
                c.Set("m.at", $"{mc.X},{mc.Y}");
            }
            if (c.GetInt("emg.m") == 0)
            {
                c.Set("spawn.err", "训练靶旁找不到出厂点 + 走廊畅通、在覆盖里的据点位置：" + string.Join("；", why.Take(12)));
            }
        }

        /// <summary>两点之间的直线每 1 格都能走（己方通行类别）。</summary>
        private static bool Corridor(Vector2 a, Vector2 b, out string block)
        {
            block = null;
            Vector2 dir = (b - a).normalized;
            for (float t = 0f; t <= Vector2.Distance(a, b) + 0.01f; t += 1f)
            {
                Vector2 p = a + dir * t;
                if (!GameLogic.Campaign.Nav.NavService.PassableNow(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), BinGames.Sim.Nav.NavConst.ClassPlayer))
                {
                    block = $"({Mathf.RoundToInt(p.x)},{Mathf.RoundToInt(p.y)})";
                    return false;
                }
            }
            return true;
        }

        private static OutpostRecord Outpost(JourneyContext c) => WorldOutpostSystem.Find(St, c.Get("op.id", string.Empty));

        private static Vector2 OutpostPos(JourneyContext c) => ParseV(c.Get("op.at", "0,0"));

        private static Vector2 ParseV(string v)
        {
            string[] f = v.Split(',');
            return new Vector2(float.Parse(f[0], CultureInfo.InvariantCulture), float.Parse(f[1], CultureInfo.InvariantCulture));
        }

        private static StepOutcome TickSpawnFixture(JourneyContext c)
        {
            if (c.Get("spawn.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail(c.Get("spawn.err"));
            }
            if (c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            int id = c.GetInt("emg.m");
            CombatSite site = Home;
            if (site == null || !site.TryGetMachineUnit(id, out _) || !Program(site, id, out CombatWeapon w, out int prog))
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail($"{Label(id)} 没进家园内核 / 没有武器");
            }
            if (prog < 0)
            {
                return StepOutcome.Fail($"{Label(id)} 的武器没有程序（阶段 1 下 [分裂, 漏油] 应编出程序）");
            }
            c.SetInt("prog.ai", prog);
            return StepOutcome.Done($"出厂 {Label(id)}（{c.Get("m.at")}，ERC-003 v{FgjM1Journey.Erc003Record()?.ActiveVersion}）：武器程序下标 {prog}；据点 {c.Get("op.id")}（{c.Get("op.at")}，1 级、驻军 1、不增援）");
        }

        private static bool Program(CombatSite site, int logicId, out CombatWeapon w, out int prog)
        {
            w = default;
            prog = -1;
            if (site == null || !site.TryGetMachineWeapon(logicId, out MachineWeaponInfo info) || !site.Kernel.TryGetWeapon(info.WeaponIndex, out w))
            {
                return false;
            }
            prog = w.Program;
            return true;
        }

        // ── 1. 蓝图编辑器 ────────────────────────────────────────────────────────────

        private static void PickCannon(JourneyContext c)
        {
            DropdownField dd = JourneyInput.FindUitk<DropdownField>(Host, "PrimaryDropdown");
            string name = ComponentCatalog.All[ComponentCatalog.CompCannonId].DisplayName;
            string choice = dd?.choices?.FirstOrDefault(x => x != null && (x == name || x.StartsWith(name, StringComparison.Ordinal)));
            if (dd != null && choice != null && JourneyInput.IsClickable(dd))
            {
                dd.value = choice; // 下拉选择 = 设值（同一 ChangeEvent 回调）
            }
        }

        private static StepOutcome TickCannon(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            BlueprintCircuitBoard b = FgjM1Journey.Panel()?.Board;
            return b?.PrimaryId == ComponentCatalog.CompCannonId ? StepOutcome.Done($"主组件 = {ComponentCatalog.All[b.PrimaryId].DisplayName}（射弹载体）") : StepOutcome.Retry($"主组件仍是 {b?.PrimaryId}");
        }

        private static void PickFirmware(JourneyContext c, int slot, string fw)
        {
            DropdownField dd = JourneyInput.FindUitk<DropdownField>(Host, "Firmware" + slot + "Dropdown");
            string name = Name(fw);
            string choice = dd?.choices?.FirstOrDefault(x => x != null && x.Contains(name));
            c.Set("fwChoices", dd?.choices == null ? "（没有下拉）" : string.Join("／", dd.choices.Take(60)));
            if (dd != null && choice != null && JourneyInput.IsClickable(dd))
            {
                dd.value = choice;
            }
        }

        private static StepOutcome TickFirmware(JourneyContext c, int slot, string fw)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            BlueprintCircuitBoard b = FgjM1Journey.Panel()?.Board;
            return b != null && b.FirmwareSlots[slot] == fw
                ? StepOutcome.Done($"固件 {slot + 1} = {Name(fw)}")
                : StepOutcome.Retry($"固件 {slot + 1} 是 {b?.FirmwareSlots[slot]}，下拉选项：{c.Get("fwChoices")}");
        }

        private static string TreeText(ProgramTreeView v, int col) =>
            v == null ? "（没有程序树）" : string.Join(" / ", v.Rows(col).Select(r => new string('·', r.Depth) + r.Text));

        /// <summary>AI 驾驶一栏：根节点（Node 0）= 分裂，漏油在子弹药节点（Node ≥ 1）里、不在根节点。</summary>
        private static StepOutcome TickAiTree(JourneyContext c)
        {
            StepOutcome fw = TickFirmware(c, 1, Oil);
            if (fw.Status != JourneyStepStatus.Done)
            {
                return fw;
            }
            ProgramTreeView v = FgjM1Journey.Panel()?.ProgramTree;
            EmgProgramResult r = v?.Result(ProgramTreeView.ColumnAi);
            IReadOnlyList<EmgTreeRow> rows = v?.Rows(ProgramTreeView.ColumnAi);
            bool ok = r != null && !r.IsEmpty && rows != null
                      && rows.Any(x => x.Node == 0 && x.Kind == EmgTreeRowKind.Own && x.Text.Contains(Name(Split)))
                      && rows.Any(x => x.Node >= 1 && (x.Kind == EmgTreeRowKind.Own || x.Kind == EmgTreeRowKind.Inherited) && x.Text.Contains(Name(Oil)))
                      && !rows.Any(x => x.Node == 0 && x.Kind == EmgTreeRowKind.Own && x.Text.Contains(Name(Oil)));
            if (!ok)
            {
                return c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Fail($"程序树（AI 驾驶）不对：{TreeText(v, ProgramTreeView.ColumnAi)}");
            }
            c.Set("tree.ai", TreeText(v, ProgramTreeView.ColumnAi));
            return StepOutcome.Done($"固件 2 = 漏油；程序树（AI 驾驶）：{c.Get("tree.ai")}；摘要“{v.SummaryText}”");
        }

        private static StepOutcome TickSaved(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            BlueprintRecord r = FgjM1Journey.Erc003Record();
            BlueprintVersionRecord v = r?.Versions?.FirstOrDefault(x => x.Version == r.ActiveVersion);
            BlueprintCircuitBoard saved = v != null ? BlueprintCircuitBoard.FromVersion(v) : null;
            string label = JourneyInput.FindUitk<Label>(Host, "SaveResultLabel")?.text ?? string.Empty;
            if (saved == null || !saved.HasUplink || saved.UplinkSlot != c.GetInt("uplinkSlot") || saved.FirmwareSlots[0] != Split || saved.FirmwareSlots[1] != Oil
                || v.PrimaryId != ComponentCatalog.CompCannonId)
            {
                return StepOutcome.Retry($"保存后 ERC-003 现役版本不是 [分裂, 漏油] + 接入口（v{r?.ActiveVersion}：{v?.PrimaryId}、固件 {string.Join(",", saved?.FirmwareSlots ?? Array.Empty<string>())}、接入口 {saved?.UplinkSlot}；“{label}”）");
            }
            return StepOutcome.Done($"保存成功：ERC-003 现役 v{v.Version}（铸造重炮、固件 [分裂, 漏油]、{saved.UplinkSlot} 号格接入口）；“{label}”");
        }

        // ── 2. AI 开火：每颗子弹药各一摊油 ─────────────────────────────────────────────

        private static void ClickSpeed(string button)
        {
            Button b = JourneyInput.FindUitk<Button>("[WorldBarHost]", button);
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

        private static int OilZones(CombatKernel k)
        {
            uint oilBit = NamedReactionCatalog.BitOf("Oil");
            int n = 0;
            for (int i = 0; i < k.ZoneCount; i++)
            {
                if (k.TryGetZone(i, out CombatZone z) && (z.StatusMask & oilBit) != 0)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>某一时刻之后生成的油区（区域会到期：按生成时间数，不按净增）。</summary>
        private static int OilZonesSince(CombatKernel k, double t0)
        {
            uint oilBit = NamedReactionCatalog.BitOf("Oil");
            int n = 0;
            for (int i = 0; i < k.ZoneCount; i++)
            {
                if (k.TryGetZone(i, out CombatZone z) && (z.StatusMask & oilBit) != 0 && z.Born >= t0)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>在飞的子弹药（<paramref name="prog"/> &lt; 0 = 不限程序）。</summary>
        private static int LiveSubs(CombatKernel k, int prog = -1)
        {
            int n = 0;
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                if (k.TryGetProjectile(i, out CombatProjectile p) && (p.Flags & EmgProjFlags.Sub) != 0 && (prog < 0 || p.Prog == prog))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>
        /// 玩家右键据点 A 驻军下令攻击（<see cref="OrderAttackA"/>；重炮机不对家园训练靶自动开火），AI 按交战间隔开火。对账窗口：从第一颗子弹药出现起 1.5 游戏秒（漏油的油区活 6 秒，窗口内不会到期）：
        /// 窗口内生成的油区 + 仍在飞的子弹药 = 新增子弹药（每颗子弹药各一摊，不多不少；基线取在没有在飞子弹药的时刻），且新增子弹药 ≥ 3（[分裂] 一次 3 份）。
        /// </summary>
        private static StepOutcome TickAiOil(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CombatSite site = Home;
            int id = c.GetInt("emg.m");
            if (site == null || !MachineRegistry.TryGetRecord(id, out MachineRecord rec) || !rec.IsAlive)
            {
                return StepOutcome.Fail($"{Label(id)} 不在了");
            }
            CombatKernel k = site.Kernel;
            if (c.GetInt("ai.base") == 0)
            {
                if (LiveSubs(k) > 0)
                {
                    return StepOutcome.Wait; // 基线取在没有在飞子弹药的那一刻（之前放出的子弹药落地的油不算进本窗口）
                }
                c.SetInt("ai.base", 1);
                c.Set("ai.t0", k.Time.ToString("R", CultureInfo.InvariantCulture));
                c.SetLong("ai.sub0", k.EmgBusStats.SubSpawned);
                c.SetLong("ai.split0", k.EmgBusStats.Splits);
                c.SetInt("ai.oil0", OilZones(k));
                c.SetInt("ai.zone0", k.ZoneCount);
                c.SetLong("ai.exp0", k.EmgPortCountOf(EmgEventKind.Expire));
                c.SetLong("ai.hit0", k.Counters.ProjectilesHit);
                c.SetLong("ai.life0", k.Counters.ProjectilesExpired);
            }
            long subs = k.EmgBusStats.SubSpawned - c.GetLong("ai.sub0");
            if (subs > 0 && c.GetLong("ai.firstTick") == 0)
            {
                c.SetLong("ai.firstTick", Math.Max(1, GameClock.Ticks));
            }
            if (c.GetLong("ai.firstTick") > 0 && GameClock.Ticks - c.GetLong("ai.firstTick") >= GameClock.TicksFor(1.5) && subs >= 3)
            {
                int oil = OilZonesSince(k, double.Parse(c.Get("ai.t0"), CultureInfo.InvariantCulture));
                int live = LiveSubs(k);
                long splits = k.EmgBusStats.Splits - c.GetLong("ai.split0");
                string detail = $"分裂 {splits} 次、子弹药 {subs} 颗、新增油区 {oil} 摊、仍在飞 {live} 颗（第 {c.GetLong("ai.firstTick")}～{GameClock.Ticks} 步）";
                if (oil + live != subs || oil < 3 || splits < 1)
                {
                    return StepOutcome.Fail("子弹药与油区对不上：" + detail + $"；诊断：区域总数 {c.GetInt("ai.zone0")} → {k.ZoneCount}、终结事件 +{k.EmgPortCountOf(EmgEventKind.Expire) - c.GetLong("ai.exp0")}、" +
                                            $"弹体命中 +{k.Counters.ProjectilesHit - c.GetLong("ai.hit0")}、寿命到 / 撞障碍 +{k.Counters.ProjectilesExpired - c.GetLong("ai.life0")}、残骸 {k.WreckCount}");
                }
                Program(site, id, out _, out int prog);
                return StepOutcome.Done($"AI 对据点 A 驻军开火（{c.Get("ai.cmd")}；程序 {prog}，血 {F(rec.Health, "0")}）：{detail}——每颗子弹药各一摊油");
            }
            if (c.GetLong("ai.firstTick") == 0)
            {
                OrderAttackA(c, site, id);
            }
            return c.StepElapsed < 150 ? StepOutcome.Wait : StepOutcome.Fail($"150 秒内 AI 没对据点 A 驻军打出子弹药（子弹药 +{subs}、分裂 +{k.EmgBusStats.Splits - c.GetLong("ai.split0")}；{c.Get("ai.cmd")}）");
        }

        /// <summary>
        /// 玩家下令（RTS 口径）：选中这台机器 → 驻军还在休眠就右键据点 A 前 14 格的地面（移动，进迎战半径叫醒驻军）→ 醒了就右键那个驻军（攻击）；之后 AI 按交战间隔自己开火。每 3 秒补一次。
        /// </summary>
        private static void OrderAttackA(JourneyContext c, CombatSite site, int id)
        {
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "rcAt")) < 3000 || FgjM2Common.IsAttacking(site, id))
            {
                return;
            }
            c.SetLong(FgjM3Common.SK(c, "rcAt"), NowMs());
            if (GameRoot.HomeValley.SelectedMachineLogicId != id)
            {
                FgjM1Journey.ClickMachine(c, "emg.m");
                c.Set("ai.cmd", "重新选中机器");
                return;
            }
            Vector2 opa = ParseV(c.Get("opa.at", "0,0"));
            Vector2 me = FgjM8Journey.PosOf(id);
            int foe = site.FindNearestHostile(opa, 14f);
            if (foe > 0 && site.TryGetUnitPosition(foe, out double2 fp))
            {
                c.SetInt("ai.foe", foe);
                c.Set("ai.cmd", FgjM3Common.TryClick(new Vector2((float)fp.x, (float)fp.y), 1) ? $"右键据点 A 驻军（单位 {foe}）攻击" : "平移镜头找据点 A 驻军");
                return;
            }
            Vector2 ground = opa + (me - opa).normalized * 14f;
            c.Set("ai.cmd", FgjM3Common.TryClick(ground, 1) ? $"右键据点 A 前的地面 ({F(ground.x)},{F(ground.y)}) 移动（离据点 {F(Vector2.Distance(me, opa))} 格）" : "平移镜头找据点 A");
        }

        private static void RightClickHome(JourneyContext c)
        {
            if (!JourneyInput.Holding)
            {
                FgjM3Common.TryClick(ParseV(c.Get("m.at", "0,0")), 1);
            }
        }

        /// <summary>右键出厂点地面让机器开回去（接入段从出厂点出发去据点 B：走廊是按出厂点验的）；到了出厂点 5 格内、据点 A 的驻军不在 20 格内（没追过来）就算完。</summary>
        private static StepOutcome TickBack(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            int id = c.GetInt("emg.m");
            CombatSite site = Home;
            if (site == null || !MachineRegistry.TryGetRecord(id, out MachineRecord rec) || !rec.IsAlive)
            {
                return StepOutcome.Fail($"{Label(id)} 不在了");
            }
            Vector2 me = FgjM8Journey.PosOf(id), home = ParseV(c.Get("m.at", "0,0"));
            int foe = c.GetInt("ai.foe");
            bool chased = foe > 0 && site.TryGetUnitPosition(foe, out double2 fp) && Vector2.Distance(me, new Vector2((float)fp.x, (float)fp.y)) < 20f;
            float d = Vector2.Distance(me, home);
            if (d < 5f && !chased)
            {
                return StepOutcome.Done($"{Label(id)} 右键开回出厂点（离出厂点 {F(d)} 格，血 {F(rec.Health, "0")}），据点 A 驻军没追来");
            }
            if (!JourneyInput.Holding && NowMs() - c.GetLong(FgjM3Common.SK(c, "backAt")) > 4000)
            {
                c.SetLong(FgjM3Common.SK(c, "backAt"), NowMs());
                if (GameRoot.HomeValley.SelectedMachineLogicId != id)
                {
                    FgjM1Journey.ClickMachine(c, "emg.m");
                }
                else
                {
                    RightClickHome(c);
                }
            }
            return c.StepElapsed < 90 ? StepOutcome.Wait : StepOutcome.Fail($"90 秒没开回出厂点（离出厂点 {F(d)} 格，据点 A 驻军在 20 格内 {chased}）");
        }

        private static StepOutcome TickChainOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (!CausalChainOverlay.IsOpen)
            {
                return StepOutcome.Retry("按因果链键后叠加没有打开（" + FgjM1Journey.InputBlockers() + "）");
            }
            var lines = Enumerable.Range(0, CausalChainOverlay.LineCount).Select(CausalChainOverlay.LineText).ToList();
            string label = Label(c.GetInt("emg.m"));
            bool source = lines.Any(l => l.Contains(label) || l.Contains(Name(Split)) || l.Contains(Name(Oil)));
            string all = string.Join(" ｜ ", lines);
            if (lines.Count == 0 || !source)
            {
                return c.StepElapsed < 4 ? StepOutcome.Wait : StepOutcome.Fail($"因果链叠加打开了，但没有写明来源（{lines.Count} 行：{all}）");
            }
            return StepOutcome.Done($"按 {GameSettings.KeyBindings.GetChord(GameActionId.CausalChain)}：因果链叠加出现 {lines.Count} 行，写明来源：{all}");
        }

        // ── 3. 信号核 ────────────────────────────────────────────────────────────────

        private static void PrintFirmware(JourneyContext c, string fw)
        {
            DropdownField print = JourneyInput.FindUitk<DropdownField>("[SignalCoreHost]", "SignalPrintChoice");
            string choice = print?.choices?.FirstOrDefault(x => x.Contains(Name(fw)));
            c.Set("printChoice", choice ?? string.Empty);
            c.Set("printChoices", print?.choices == null ? string.Empty : string.Join("／", print.choices.Take(40)));
            c.SetInt("scrapBeforePrint", St.Scrap);
            if (print != null && choice != null)
            {
                print.value = choice;
                FgjM1Journey.ClickUi(c, "[SignalCoreHost]", "SignalPrint");
            }
        }

        private static StepOutcome TickPrinted(JourneyContext c, string fw)
        {
            if (c.Get("printChoice").Length == 0)
            {
                return StepOutcome.Fail($"刻印下拉里没有{Name(fw)}（{c.Get("printChoices")}）");
            }
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            PrimitiveChipRecord chip = Campaign.Primitive.PrimitiveInventory.Find(St, hud?.SelectedPartId);
            int cost = SignalCoreService.FirmwareChipPrintScrap;
            return chip != null && chip.CardDefId == fw && chip.State == PrimitiveChipState.Bag && St.Scrap == c.GetInt("scrapBeforePrint") - cost
                ? StepOutcome.Done($"刻印一枚{Name(fw)}芯片（进基元仓、被选中），扣 {cost} 废料")
                : StepOutcome.Retry($"刻印没成功（{hud?.FeedbackText}；{FgjM1Journey.UiFail(c)}）");
        }

        private static StepOutcome TickEquipped(JourneyContext c, string fw)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return SignalCoreService.SlotContentId(St, 0) == fw && SignalCoreService.SlotChip(St, 0)?.State == PrimitiveChipState.SignalCore
                ? StepOutcome.Done($"{Name(fw)}装进信号核 1 号槽（{SignalCoreService.SummaryText(St)}）")
                : StepOutcome.Retry($"装入没成功（{SignalCoreHudUIToolkit.Instance?.FeedbackText}；{FgjM1Journey.UiFail(c)}）");
        }

        private static void ReopenEditor(JourneyContext c)
        {
            FgjM1Journey.ClickUi(c, Host, "EntryToggleButton", when: () => !GameRoot.HomeValley.IsCircuitBoardPanelOpen);
            c.SetInt("rowClicked", 0);
        }

        /// <summary>“你接入时”一栏：接入在前（FGR-EMG-030）——根 = 延时引信（定时触发、放子弹药），机器固件分裂 / 漏油被继承进子弹药一代；AI 一栏仍是 [分裂, 漏油]。</summary>
        private static StepOutcome TickUplinkTree(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            CircuitBoardPanelUIToolkit p = FgjM1Journey.Panel();
            if (p?.Board == null || !GameRoot.HomeValley.IsCircuitBoardPanelOpen)
            {
                return StepOutcome.Retry("蓝图编辑器没有打开：" + FgjM1Journey.UiFail(c));
            }
            if (p.Board.FirmwareSlots[0] != Split)
            {
                if (c.GetInt("rowClicked") == 0)
                {
                    c.SetInt("rowClicked", 1);
                    FgjM1Journey.ClickErc003Row(c);
                }
                return StepOutcome.Wait;
            }
            ProgramTreeView v = p.ProgramTree;
            IReadOnlyList<EmgTreeRow> up = v.ColumnVisible(ProgramTreeView.ColumnUplinked) ? v.Rows(ProgramTreeView.ColumnUplinked) : null;
            bool ok = up != null
                      && up.Any(x => x.Node == 0 && x.Kind == EmgTreeRowKind.Own && x.Text.Contains(Name(Fuse)))
                      && up.Any(x => x.Node >= 1 && (x.Kind == EmgTreeRowKind.Own || x.Kind == EmgTreeRowKind.Inherited) && x.Text.Contains(Name(Split)))
                      && up.Any(x => x.Node >= 1 && (x.Kind == EmgTreeRowKind.Own || x.Kind == EmgTreeRowKind.Inherited) && x.Text.Contains(Name(Oil)))
                      && !up.Any(x => x.Node == 0 && x.Kind == EmgTreeRowKind.Own && (x.Text.Contains(Name(Split)) || x.Text.Contains(Name(Oil))))
                      && TreeText(v, ProgramTreeView.ColumnAi) == c.Get("tree.ai");
            if (!ok)
            {
                return c.StepElapsed < 4 ? StepOutcome.Wait
                    : StepOutcome.Fail($"“你接入时”程序树不对：{(up == null ? "这一栏没显示" : TreeText(v, ProgramTreeView.ColumnUplinked))}；AI 一栏 {TreeText(v, ProgramTreeView.ColumnAi)}");
            }
            return StepOutcome.Done($"“你接入时”一栏：{TreeText(v, ProgramTreeView.ColumnUplinked)}（分裂、漏油在子弹药节点里）；AI 一栏不变：{c.Get("tree.ai")}");
        }

        // ── 4. 接入、开火、离开 ──────────────────────────────────────────────────────

        private static StepOutcome TickUplinked(JourneyContext c)
        {
            int id = c.GetInt("emg.m");
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点机器列表失败：" + FgjM1Journey.UiFail(c));
            }
            if (SignalPresence.CurrentMachineLogicId != id || WorldView.Director.Mode != ViewMode.Direct)
            {
                return c.StepElapsed < 6 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {Label(id)}（{SignalUplinkService.LastFeedbackText}）");
            }
            if (c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            if (!Program(Home, id, out _, out int prog) || prog < 0 || prog == c.GetInt("prog.ai"))
            {
                return StepOutcome.Fail($"接入后武器程序 {prog}，应换成接入程序（AI 程序 {c.GetInt("prog.ai")}）");
            }
            c.SetInt("prog.up", prog);
            string slot = SignalCoreHudUIToolkit.Instance?.UplinkHud?.SlotText(0) ?? string.Empty;
            return slot.Contains(Name(Fuse))
                ? StepOutcome.Done($"接入 {Label(id)}（镜头直控）：武器程序 {c.GetInt("prog.ai")} → {prog}（接入程序）；接入 HUD 1 号槽“{slot}”")
                : StepOutcome.Fail($"接入 HUD 1 号槽没有延时引信（“{slot}”）");
        }

        /// <summary>
        /// 直控（WASD）开到离据点驻军约 0.62 × 弹速～射程之间，左键点它开火。看到接入程序的根弹体在飞（Prog = 接入程序、不是子弹药）就立刻按退出接入键；
        /// 离开后：武器回 AI 程序，同一颗根弹体飞到定时（0.6 秒）在空中放出子弹药（定时事件触发、子弹药出现在离机器 ≥ 12 格处 = 命中前空爆），这些子弹药按原程序走完、在飞弹体清零。
        /// </summary>
        private static StepOutcome TickFireAndLeave(JourneyContext c)
        {
            CombatSite site = Home;
            int id = c.GetInt("emg.m");
            int up = c.GetInt("prog.up");
            CombatKernel k = site.Kernel;
            string sk = FgjM3Common.SK(c, "phase");
            int phase = c.GetInt(sk);
            if (phase == 0)
            {
                // 接入已断（重试时）：重新接入
                if (SignalPresence.CurrentMachineLogicId != id)
                {
                    if (NowMs() - c.GetLong(FgjM3Common.SK(c, "upAt")) > 4000)
                    {
                        c.SetLong(FgjM3Common.SK(c, "upAt"), NowMs());
                        FgjM1Journey.ClickMachineList(c, id);
                    }
                    return StepOutcome.Wait;
                }
                if (c.GetInt("f.t0set") == 0)
                {
                    c.SetInt("f.t0set", 1);
                    c.SetLong("f.timer0", k.EmgPortCountOf(EmgEventKind.Timer));
                }
                if (CountRoot(k, up) > 0)
                {
                    JourneyInput.ReleaseKeys();
                    JourneyInput.PressAction(GameActionId.ToggleCameraView);
                    c.SetInt(sk, 1);
                    c.SetLong("f.leaveTick", GameClock.Ticks);
                    c.SetLong("f.split0", k.EmgBusStats.Splits);
                    c.SetInt("f.oil0", OilZones(k));
                    return StepOutcome.Wait;
                }
                if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "fireAt")) < 1200)
                {
                    return StepOutcome.Wait;
                }
                if ((int)(c.StepElapsed / 5) > c.GetInt(FgjM3Common.SK(c, "log")))
                {
                    c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 5));
                    Vector2 m0 = FgjM8Journey.PosOf(id);
                    c.Log($"[{F(c.StepElapsed, "0")}s] {Label(id)} 在 ({F(m0.x)},{F(m0.y)})、离据点 {F(Vector2.Distance(m0, OutpostPos(c)))} 格；据点驻军单位 {site.FindNearestHostile(OutpostPos(c), 10f)}；" +
                          $"接入 {SignalPresence.CurrentMachineLogicId == id}、镜头 {WorldView.Director.Mode}；{c.Get("f.geo")}；点击 {c.GetInt("f.clicks")} 次；按住 {JourneyInput.Holding}");
                }
                // 直控（WASD）开到离据点驻军 0.62 × 弹速～射程 - 0.5 格处（延时引信 0.6 秒 × 弹速才空爆：再近根弹体就先命中了），左键点它开火。
                Vector2 me = FgjM8Journey.PosOf(id);
                int foe = site.FindNearestHostile(OutpostPos(c), 10f); // 据点驻军（不是训练靶、不是别处的敌人）
                if (foe <= 0 || !site.TryGetUnitPosition(foe, out double2 fp))
                {
                    // 据点驻军还在休眠（没有内核单位）：直控往据点开，进迎战半径（18 格）叫醒它。
                    Vector2 op = OutpostPos(c);
                    if (Vector2.Distance(me, op) > 15f)
                    {
                        FgjM1Journey.DriveToward(me, op, 0.3);
                    }
                    c.SetLong(FgjM3Common.SK(c, "fireAt"), NowMs() - 800);
                    return c.StepElapsed < 60 ? StepOutcome.Wait : StepOutcome.Fail($"60 秒内据点 {c.Get("op.id")} 的驻军没醒（离据点 {F(Vector2.Distance(me, op))} 格）");
                }
                Program(site, id, out CombatWeapon uw, out _);
                var tp = new Vector2((float)fp.x, (float)fp.y);
                float d = Vector2.Distance(me, tp);
                float lo = uw.ProjectileSpeed * 0.62f, hi = uw.Range - 0.5f;
                c.Set("f.geo", $"射程 {F(uw.Range)}、弹速 {F(uw.ProjectileSpeed)}、开火距离 {F(d)} 格（窗口 {F(lo)}～{F(hi)}）");
                if (d > hi || d < lo)
                {
                    Vector2 goal = tp + (me - tp).normalized * ((lo + hi) * 0.5f);
                    FgjM1Journey.DriveToward(me, goal, 0.3);
                    c.SetLong(FgjM3Common.SK(c, "fireAt"), NowMs() - 800);
                    return c.StepElapsed < 40 ? StepOutcome.Wait : StepOutcome.Retry($"40 秒没开到开火距离（{c.Get("f.geo")}）");
                }
                if (!JourneyInput.OnScreen(tp, 0.03f))
                {
                    return StepOutcome.Fail($"敌人在开火距离内却不在画面里（{c.Get("f.geo")}）");
                }
                c.SetLong(FgjM3Common.SK(c, "fireAt"), NowMs());
                c.SetInt("f.clicks", c.GetInt("f.clicks") + 1);
                JourneyInput.Click(tp); // 直控左键点敌人：家园直控对敌方内核单位开火（EMG1-E2E-01 起）
                return c.StepElapsed < 50 ? StepOutcome.Wait : StepOutcome.Retry($"直控开火没看到接入程序的根弹体（点击 {c.GetInt("f.clicks")} 次；{c.Get("f.geo")}）");
            }
            // phase 1：已按离开
            if (phase == 1)
            {
                if (SignalPresence.CurrentMachineLogicId == id)
                {
                    return c.StepElapsed < 25 ? StepOutcome.Wait : StepOutcome.Fail("按退出接入键后信号还在机器里");
                }
                Program(site, id, out _, out int now);
                if (now != c.GetInt("prog.ai"))
                {
                    return StepOutcome.Fail($"离开后武器程序 {now}，应回到 AI 程序 {c.GetInt("prog.ai")}");
                }
                // 找空爆：接入程序的子弹药出现
                if (FindAirburst(k, up, FgjM8Journey.PosOf(id), c.GetLong("f.timer0"), out string burst))
                {
                    c.Set("f.burst", burst);
                    c.SetLong("f.burstTick", GameClock.Ticks);
                    c.SetInt(sk, 2);
                }
                else if (CountRoot(k, up) == 0 && LiveSubs(k, up) == 0)
                {
                    return StepOutcome.Retry($"离开后根弹体没在空中放出子弹药就没了（分裂 +{k.EmgBusStats.Splits - c.GetLong("f.split0")}、定时事件 +{k.EmgPortCountOf(EmgEventKind.Timer) - c.GetLong("f.timer0")}）");
                }
                return StepOutcome.Wait;
            }
            // phase 2：等接入程序的在飞弹体全部走完
            if (CountRoot(k, up) > 0 || LiveSubs(k, up) > 0)
            {
                return GameClock.Ticks - c.GetLong("f.burstTick") < GameClock.TicksFor(10) ? StepOutcome.Wait : StepOutcome.Fail("空爆后 10 游戏秒接入程序的子弹药还没走完");
            }
            long splits = k.EmgBusStats.Splits - c.GetLong("f.split0");
            int oil = OilZones(k) - c.GetInt("f.oil0");
            if (splits < 1 || oil < 1)
            {
                return StepOutcome.Fail($"离开后在飞子弹药没有按原程序结算（分裂 +{splits}、新增油区 {oil}）");
            }
            c.SetInt("slot", CampaignSession.ActiveSlotIndex);
            return StepOutcome.Done($"直控左键开火 {c.GetInt("f.clicks")} 次：根弹体在飞时按退出接入键离开（第 {c.GetLong("f.leaveTick")} 步），武器回 AI 程序 {c.GetInt("prog.ai")}；" +
                                    $"离开后同一颗根弹体飞行中空爆：{c.Get("f.burst")}；子弹药按原程序（{up}）走完：分裂 +{splits}、新增油区 {oil}、定时事件 +{k.EmgPortCountOf(EmgEventKind.Timer) - c.GetLong("f.timer0")}，在飞弹体清零");
        }

        /// <summary>接入程序在飞的根弹体（不是子弹药）。</summary>
        private static int CountRoot(CombatKernel k, int prog)
        {
            int n = 0;
            for (int i = 0; i < k.ProjectileCount; i++)
            {
                if (k.TryGetProjectile(i, out CombatProjectile p) && p.Prog == prog && (p.Flags & EmgProjFlags.Sub) == 0)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>空爆 = 定时事件已触发，且接入程序的子弹药出现在离机器 ≥ 12 格处（根弹体飞了约 0.6 秒、还没命中就放出子弹药）。</summary>
        private static bool FindAirburst(CombatKernel k, int prog, Vector2 me, long timer0, out string text)
        {
            text = null;
            long timers = k.EmgPortCountOf(EmgEventKind.Timer) - timer0;
            for (int i = 0; timers > 0 && i < k.ProjectileCount; i++)
            {
                if (!k.TryGetProjectile(i, out CombatProjectile p) || p.Prog != prog || (p.Flags & EmgProjFlags.Sub) == 0)
                {
                    continue;
                }
                var at = new Vector2((float)p.Pos.x, (float)p.Pos.y);
                float d = Vector2.Distance(at, me);
                if (d >= 12f)
                {
                    text = $"定时事件 +{timers}，第 {p.Gen} 代子弹药出现在 ({F(at.x)},{F(at.y)})，离机器 {F(d)} 格（子弹药已飞 {F(p.Age, "0.##")} 秒）";
                    return true;
                }
            }
            return false;
        }

        // ── 5. 存档读档 ──────────────────────────────────────────────────────────────

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
            c.Set("pre.sum", $"内核第 {site.Kernel.Steps} 步、状态哈希 {c.Get("pre.hash")}、油区 {OilZones(site.Kernel)}、弹体 {site.Kernel.ProjectileCount}、程序 {site.Kernel.ProgramCount}、残骸 {site.Kernel.WreckCount}");
            return StepOutcome.Done($"暂停菜单打开（世界暂停）；存档前：{c.Get("pre.sum")}；快照 {rec.Payload.Length} 字符");
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
                return StepOutcome.Fail($"存档里的家园内核快照与存档那一刻不一致（{(rec == null ? "没有这个地点" : $"第 {rec.KernelSteps} 步、{rec.Payload?.Length} 字符、摘要 {Fnv(rec.Payload)}")}；存档前 {c.Get("pre.sum")}、摘要 {c.Get("pre.payload")}）");
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
            int id = c.GetInt("emg.m");
            Program(site, id, out _, out int prog);
            bool aiBack = prog >= 0 && SignalPresence.AtCore;
            if (hash != c.Get("pre.hash") || !aiBack)
            {
                return StepOutcome.Fail($"读档后不一致：快照读回状态哈希 {hash}（存档那一刻 {c.Get("pre.hash")}）；{Label(id)} 武器程序 {prog}、信号在核心 {SignalPresence.AtCore}");
            }
            return StepOutcome.Done($"读档进入归还谷地：家园内核从快照恢复（现第 {site.Kernel.Steps} 步 ≥ 存档第 {c.GetLong("pre.steps")} 步），快照读回状态哈希 {hash} = 存档那一刻；" +
                                    $"{Label(id)} 武器仍有 AI 程序（下标 {prog}），信号在核心；油区 {OilZones(site.Kernel)}、弹体 {site.Kernel.ProjectileCount}");
        }
    }
}
