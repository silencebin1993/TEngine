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
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.UI.Expedition;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using GameLogic.View;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG9-E2E-01：FGJ-M9 —— 第一幕全流程（加速版）。前半段原样复用 FGJ-M9A（正式节奏开新档 → 第一次远征归来 → 基础传送带开放），
    /// 之后一处开局夹具（<see cref="ApplyFixture"/>，DEBT-FG9E2E01-01：铸造前哨外围开放、核心门三灯的前置、出厂一支重炮远征队），
    /// 其余全走正式输入：远征准备面板选铸造前哨 → 沿地形行军进入（节奏“铸造前哨外围”、剧情“炮口朝上”、敌人按敌人表展开）→
    /// 接入一台、开进“干扰支援”的干扰场被断链（DEBT-FG1E2E01-03）→ 框选 / Shift 加选远征队、右键敌人 = 攻击清掉外围 → 右键核心区地面进场（首领战开始）→
    /// 右键两个供能节点 → 右键主核心，打过阶段一 / 二 / 对空形态（/ 狂暴）→ 主核心摧毁：铸造领地平定、节奏“铸造主核心”“第一幕结束”、剧情“它在等什么”、战役进入第 2 幕 →
    /// 暂停菜单“资料库”里能读到“它在等什么”。等待段用世界栏 3 倍速按钮。用法：bash tools/unity-journey.sh FGJ-M9。
    /// </summary>
    public static class FgjM9Journey
    {
        public const string Id = "FGJ-M9";

        private const string PrepHost = "[HomeValleyExpeditionPrepHost]";
        private const string WorldBarHost = "[WorldBarHost]";
        private const string FoundryTerritory = "foundry";
        private const int SquadSize = 4; // 信号带宽 8、每台 2（开局信号塔）

        private static CampaignState St => CampaignSession.Current;

        private static FoundryOutpostController Fo => GameRoot.FoundryOutpost;

        private static CombatSite Site => Fo?.Combat;

        public static JourneyDef Build()
        {
            JourneyDef front = FgjM9FrontJourney.Build();
            var steps = new List<JourneyStep>(front.Steps)
            {
                S("m9_fix", "开局夹具（DEBT-FG9E2E01-01）：铸造前哨外围开放、重炮组件已解析、技术数据 +40、信号暴露补到 60（敌方适应门槛）；按正式保存入口存“重炮 + 接入口”（研究熔穿过载）与“重炮”两张蓝图，" +
                            $"按正式出厂入口出厂一台装接入口版的 ERC-003 留守家园（核心门灯 3）与 {SquadSize} 台常规重炮远征队（铸造今天的反制 = 干扰支援）", 20, ApplyFixture, TickFixture),
                S("fo_prep_open", "左键点信号塔打开远征准备面板", 20, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeSignalTower, !GameRoot.HomeValley.IsExpeditionPrepPanelOpen),
                    TickPrepOpen, retries: 2),
                S("fo_prep_pick", $"名单表里逐行勾选远征队 {SquadSize} 台", 40, null, TickPick),
                S("fo_prep_dest", "目的地下拉框写着“铸造前哨”（破碎都市也可选）", 10, null, TickDestination),
                S("depart", "点“出发”：沿地形行军到铸造前哨（点“跟随远征队”+ 3 倍速；途中“路线被截断”时点行军条“前进”，队伍在覆盖外时由行军服务按最后的“前进”自动重试，FG-GAP-136）", 330, c => { ResetMarchVars(c); FgjM1Journey.ClickUi(c, PrepHost, "DepartButton"); }, c => FgjM8Journey.TickDepart(c, 320), retries: 1),
                S("fo_enter", "进了铸造前哨：节奏台账记下“铸造前哨外围”、资料库收到“炮口朝上”、外围敌人按敌人表展开、干扰支援的干扰场在场", 20, null, TickEntered),
                S("fo_uplink", "命令栏机器列表点一台：接入（镜头直控）", 25, c => { c.SetInt("other", Team(c)[0]); FgjM1Journey.ClickMachineList(c, Team(c)[0]); }, TickUplinked, retries: 3),
                S("fo_jam", "开着它（WASD）驶向“干扰支援”的干扰场：宽限预警后断链，信号回到归还核心、它进入安全模式（原因：干扰）", 90, null, TickDriveIntoJam),
                S("fo_select", "左键点一台、Shift 左键加选其余存活的远征队", 40, null, TickSelectTeam, retries: 2),
                S("fo_clear", "点 3 倍速；右键离队伍最近的外围敌人（右键敌人 = 攻击），打掉就右键下一台，清掉核心门前的守军", 420, null, TickClearOuter),
                S("fo_gate", "右键核心区大门后面的地面（右键地面 = 移动）：远征队过门进场，首领战开始（进场前自动存档、两个供能节点展开护盾）", 120, null, TickEnterArena),
                S("fo_nodes", "右键供能节点（攻击），打掉再右键另一个：护盾消失，主核心进入阶段一", 300, null, TickNodes),
                S("fo_core", "右键主核心（攻击）：阶段一 → 过渡 → 阶段二（区域封锁）→ 对空形态（/ 狂暴）→ 主核心摧毁", 600, null, TickCore),
                S("fo_won", "主核心摧毁：铸造领地平定，节奏台账记下“铸造主核心”“第一幕结束”，资料库收到“它在等什么”，战役进入第 2 幕，图鉴敌人“铸造主核心”解锁", 30, null, TickWon),
                S("lib_pause", "按 Esc 打开暂停菜单", 10, c => JourneyInput.PressAction(GameActionId.Cancel), TickPauseOpen, retries: 2),
                S("lib_open", "暂停菜单点“资料库”：列表里有“它在等什么”（第 1 幕的最后一条）与“炮口朝上”", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseLibrary"), TickLibrary, retries: 2),
                S("lib_close", "点资料库“关闭”，再按 Esc 回到游戏", 15, c => FgjM1Journey.ClickUi(c, "[NarrativeLibraryHost]", "NlClose"), TickLibraryClosed, retries: 2),
                S("codex_open", "按图鉴键打开图鉴", 10, c => FgjM1Journey.PressIf(GameActionId.OpenCodex, !MechanicCodexPanelUIToolkit.IsOpen), FgjM2Common.TickCodexOpen, retries: 1),
                S("codex_tab", "点“敌人”页签", 10, c => FgjM1Journey.ClickUi(c, "[MechanicCodexHost]", MechanicCodexPanelUIToolkit.TabButtonName(MechanicCodex.TabOf(CodexBoss))), TickCodexTab, retries: 1),
                S("codex_entry", "点“铸造主核心”条目：已解锁，写着正文", 10, ClickCodexEntry, TickCodexEntry, retries: 2),
                S("codex_close", "再按图鉴键关闭图鉴", 10, c => FgjM1Journey.PressIf(GameActionId.OpenCodex, MechanicCodexPanelUIToolkit.IsOpen), FgjM2Common.TickCodexClosed, retries: 1),
            };
            return new JourneyDef
            {
                Id = Id,
                Title = "M9 出口（第一幕，加速版）：FGJ-M9A 前半段 → 开局夹具 → 远征铸造前哨 → 被干扰机断链 → 清外围 → 进核心区 → 打掉两个供能节点 → 击败铸造主核心（阶段三）→ 第一幕结束、第 2 幕 → 资料库",
                Seed = FgjM3Journey.TestSeed,
                TotalTimeoutSeconds = 3000,
                OnFinish = front.OnFinish,
                Version = 1,
                CheckpointAfter = new[] { "m9_fix" },
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static string F(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);

        private static string Label(int id) => FgjM1Journey.Label(id);

        private static List<int> Team(JourneyContext c) => FgjM1Journey.Roster(c);

        private static long NowMs() => FgjM2Common.NowMs();

        private static bool Alive(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) && m != null && m.IsAlive;

        private static List<int> AliveTeam(JourneyContext c) => Team(c).Where(Alive).ToList();

        private static RegionRecord Region => FoundryOutpostRegion.Find(St);

        private static CoreBossState Boss => Region != null ? FoundryOutpostCoreBoss.GetState(Region) : CoreBossState.Locked;

        private static string TeamHp(JourneyContext c) => string.Join("、", Team(c).Select(id => MachineRegistry.TryGetRecord(id, out MachineRecord m)
            ? $"{Label(id)} {(m.IsAlive ? F(m.Health, "0") : "阵亡")}" : "?"));

        // ── 开局夹具 ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 开局夹具（DEBT-FG9E2E01-01，轻量口径“加速版”）：第一次远征归来之后到铸造前哨之间的进度——
        /// 铸造前哨外围开放（Demo 解锁条件“跨派系蓝图 + ERC-003 改造”由 FGJ-M2 cb_save / FgCoreFirmwareBoundarySelfCheck 覆盖）、
        /// 核心门灯 1“重炮组件已解析”（破碎都市数据盒解析由 FGJ-M1 / M2 覆盖）、灯 2 走正式保存入口 BlueprintEditorService.TrySave（扣技术数据研究熔穿过载）、
        /// 灯 3 = 按正式出厂入口出厂装这张蓝图的远征队（装配站生产由 FGJ-M4 / M6 覆盖）。之后的派遣、行军、断链、交战、首领战、结算全走正式系统。
        /// </summary>
        private static void ApplyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.Get("m9.bp", string.Empty).Length > 0)
            {
                return;
            }
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            RegionRecord fo = FoundryOutpostRegion.Find(s);
            if (fo != null && fo.State == RegionState.Locked)
            {
                fo.State = RegionState.Available;
            }
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Append(ComponentCatalog.CompCannonId).Distinct().ToArray();
            c.SetLong("tech0", (long)s.TechData);
            s.TechData += 40;
            c.Set("exp0", F(s.SignalExposure));
            s.SignalExposure = Mathf.Max(s.SignalExposure, CampaignExposureLedger.ThresholdAdaptationIntel);
            // 灯 2 / 灯 3：重炮 + 接入口蓝图（熔穿过载）装在一台留守家园的 ERC-003 上；远征队是不带接入口的重炮（常规配置），
            // 铸造的反制按“无明显专精 → 干扰支援”展开干扰机（EnemyAdaptationService / FoundryCounterService，暴露过 60 门槛）。
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>());
            CircuitOpResult up = board.TrySetUplink(2);
            BlueprintSaveResult save = BlueprintEditorService.TrySave(s, board, null, "重炮接入口", saveAsNewRecord: true);
            BlueprintSaveResult plain = BlueprintEditorService.TrySave(s,
                BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>()), null, "重炮", saveAsNewRecord: true);
            c.Set("m9.saveNote", $"标接入口 {up.Success}（{up.Message}）；保存“重炮接入口” {save.Success}（{save.FailureReason}）反应 {save.ReactionId} 扣技术数据 {save.TechDataCharged}；" +
                                 $"保存“重炮” {plain.Success}（{plain.FailureReason}）反应 {plain.ReactionId ?? "无"}");
            if (!save.Success || !plain.Success)
            {
                return;
            }
            c.Set("m9.bp", save.BlueprintId);
            GridCell core = HomeGridService.CorePivot(s);
            var ids = new List<int>();
            for (int i = 0; i <= SquadSize; i++)
            {
                BlueprintSaveResult use = i < SquadSize ? plain : save;
                BlueprintVersionRecord ver = BlueprintEditorService.Find(s, use.BlueprintId)?.Versions?.FirstOrDefault(v => v != null && v.Version == use.Version);
                GridCell at = SiegeService.NearestPassable(new GridCell(core.X - 8 + i * 3, core.Y - 9), 10);
                MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, use.BlueprintId, HomeValleyLayout.RegionId, new Vector2(at.X, at.Y), 100f, 100f,
                    blueprintVersion: use.Version, loadoutSignature: ver?.CompileSignature);
                if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
                {
                    MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
                    if (i < SquadSize)
                    {
                        ids.Add(r.LogicId);
                    }
                    else
                    {
                        c.SetInt("keeper", r.LogicId);
                    }
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
            CombatSite home = GameRoot.HomeValley?.Combat;
            bool inKernel = team.Count == SquadSize && team.All(id => home != null && home.TryGetMachineUnit(id, out _));
            if (!inKernel)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail($"夹具出厂的远征队没有进家园战斗内核（{team.Count} 台；{c.Get("m9.saveNote")}）");
            }
            FoundryOutpostRegion.CoreGateLights lights = FoundryOutpostRegion.ComputeCoreGateLights(s);
            ExpeditionDepartureService.RosterValidation v = ExpeditionDepartureService.ValidateRoster(s, team);
            bool reachable = ExpeditionDepartureService.ReachableTargets(s).Contains(ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost);
            // 只记录不断言：图鉴解锁会按本机设置里“见过的引导钩子”补解锁（GameSettings 不随旅程隔离，影子工程跑过别的旅程就会是已解锁）；“遭遇前锁着”由 FgCodexSelfCheck 覆盖。
            c.SetInt("codexBossBefore", MechanicCodex.IsUnlocked(CodexBoss) ? 1 : 0);
            string counter = FoundryCounterService.PeekLocked(s);
            if (!lights.AllReady || !v.Success || !v.HasWeapon || !reachable || counter != AdaptationCatalog.JammerSupport)
            {
                return StepOutcome.Fail($"夹具后：三灯 {lights.CannonAnalyzed}/{lights.OverloadBlueprintSaved}/{lights.MachineEquipped}、名单可出发 {v.Success}、有武器 {v.HasWeapon}、铸造前哨可派 {reachable}、今天铸造反制“{FoundryCounterService.CounterName(counter)}”（{counter}）" +
                                        $"（{string.Join("；", v.BlockingReasons)}；{c.Get("m9.saveNote")}）");
            }
            return StepOutcome.Done($"夹具生效：{c.Get("m9.saveNote")}；技术数据 {c.GetLong("tech0")} → {F(s.TechData, "0")}；信号暴露 {c.Get("exp0")} → {F(s.SignalExposure)}；" +
                                    $"核心门三灯全亮（灯 3 = 留守的 {Label(c.GetInt("keeper"))}）；铸造前哨 {Region?.State}；今天铸造反制“{FoundryCounterService.CounterName(counter)}”；远征队 {string.Join("、", team.Select(Label))}（ERC-003 重炮）");
        }

        // ── 派遣 ────────────────────────────────────────────────────────────────────

        /// <summary>名单表里逐行勾选（行上的勾选框，真实点击；不在可见区就滚滚轮）。</summary>
        private static StepOutcome TickPick(JourneyContext c)
        {
            List<int> roster = Team(c);
            // 面板记着上次的名单（第一次远征那三台）：先像玩家一样把不去的取消勾选。
            int[] pre = ExpeditionPrepPanelUIToolkit.Instance?.SelectedIdsForTests ?? Array.Empty<int>();
            int extra = pre.FirstOrDefault(id => !roster.Contains(id));
            if (extra != 0)
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, "unAt")) < 500)
                {
                    return StepOutcome.Wait;
                }
                Toggle u = FgjM1Journey.RowToggle(extra);
                if (u == null || (!JourneyInput.ScrollIntoView(JourneyInput.FindUitk<ScrollView>(PrepHost, "MachineList"), u)))
                {
                    return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail($"取消勾选 {Label(extra)}：找不到这一行 / 滚不进可见区（{JourneyInput.LastUiFailure}）");
                }
                if (u.value && !JourneyInput.ClickElement(u))
                {
                    return JourneyInput.LastUiTransient ? StepOutcome.Wait : StepOutcome.Fail($"取消勾选 {Label(extra)} 失败：{JourneyInput.LastUiFailure}");
                }
                c.SetLong(FgjM3Common.SK(c, "unAt"), NowMs());
                c.SetInt(FgjM3Common.SK(c, "unN"), c.GetInt(FgjM3Common.SK(c, "unN")) + 1);
                return StepOutcome.Wait;
            }
            int idx = c.GetInt(FgjM3Common.SK(c, "pick"));
            if (idx < roster.Count)
            {
                if (c.StepElapsed < (idx + 1) * 0.6)
                {
                    return StepOutcome.Wait;
                }
                Toggle t = FgjM1Journey.RowToggle(roster[idx]);
                if (t == null)
                {
                    return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Fail($"名单表里找不到 {Label(roster[idx])} 这一行");
                }
                if (!JourneyInput.ScrollIntoView(JourneyInput.FindUitk<ScrollView>(PrepHost, "MachineList"), t))
                {
                    int w = c.GetInt(FgjM3Common.SK(c, "wheel")) + 1;
                    c.SetInt(FgjM3Common.SK(c, "wheel"), w);
                    return w > 60 ? StepOutcome.Fail($"{Label(roster[idx])} 这一行滚不进可见区：{JourneyInput.LastUiFailure}") : StepOutcome.Wait;
                }
                if (!t.value && !JourneyInput.ClickElement(t))
                {
                    return JourneyInput.LastUiTransient ? StepOutcome.Wait : StepOutcome.Fail($"勾选 {Label(roster[idx])} 失败：{JourneyInput.LastUiFailure}");
                }
                c.SetInt(FgjM3Common.SK(c, "pick"), idx + 1);
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < (roster.Count + 1) * 0.6)
            {
                return StepOutcome.Wait;
            }
            int[] sel = ExpeditionPrepPanelUIToolkit.Instance?.SelectedIdsForTests ?? Array.Empty<int>();
            string reasons = JourneyInput.FindUitk<Label>(PrepHost, "ReasonsLabel")?.text ?? string.Empty;
            return roster.All(sel.Contains) && sel.Length == roster.Count && reasons.Length == 0
                ? StepOutcome.Done($"取消勾选上次名单里不去的 {c.GetInt(FgjM3Common.SK(c, "unN"))} 台，勾选 {string.Join("、", roster.Select(Label))}")
                : StepOutcome.Fail($"勾选后名单不对：选中 [{string.Join(",", sel)}]；“{reasons}”");
        }

        private static StepOutcome TickPrepOpen(JourneyContext c)
        {
            if (!JourneyInput.WorldClickSettled(c.StepElapsed, 0.8))
            {
                return StepOutcome.Wait;
            }
            if (!GameRoot.HomeValley.IsExpeditionPrepPanelOpen)
            {
                return StepOutcome.Retry("点信号塔后远征准备面板没有打开（选点：" + JourneyInput.LastVisiblePointNote + "；点击那两帧：" + JourneyInput.LastWorldClickTrace + "）");
            }
            ExpeditionDepartureService.PrepSnapshot snap = ExpeditionDepartureService.BuildPrepSnapshot(St);
            return StepOutcome.Done($"远征准备面板打开：默认目标 {snap.Target}，信号带宽 {snap.BandwidthCapacity:F0}");
        }

        private static StepOutcome TickDestination(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            DropdownField dd = JourneyInput.FindUitk<DropdownField>(PrepHost, "DestinationDropdown");
            string want = ExpeditionPrepPanelUIToolkit.DestinationOptionLabel(St, ExpeditionDepartureService.ExpeditionTarget.FoundryOutpost);
            if (dd == null)
            {
                return StepOutcome.Fail("远征准备面板没有目的地下拉框");
            }
            if (dd.value != want && dd.choices.Contains(want))
            {
                // 像玩家一样在下拉框里选（DropdownField 的选项弹层在 batchmode 里不渲染：用控件自己的取值入口，与选中一项同一回调）。
                dd.value = want;
                return StepOutcome.Wait;
            }
            return dd.value == want && dd.choices.Count >= 2
                ? StepOutcome.Done($"目的地“{dd.value}”（可选 {string.Join(" / ", dd.choices)}）")
                : StepOutcome.Fail($"目的地下拉框“{dd.value}”，选项 [{string.Join(" / ", dd.choices)}]，应为“{want}”");
        }

        /// <summary>第一次远征（FGJ-M9A）留下的行军旅程变量清零：这一趟同样要点“跟随远征队”、到达后点回 1 倍速。</summary>
        private static void ResetMarchVars(JourneyContext c)
        {
            foreach (string k in new[] { "marchFollowed", "marchSpeedBack", "interruptClicked", "adv.n", "fix136.n", "adv.blockedLog" })
            {
                c.SetInt(k, 0);
            }
        }

        // ── 铸造前哨：进场 ──────────────────────────────────────────────────────────

        private static StepOutcome TickEntered(JourneyContext c)
        {
            if (c.StepElapsed < 2.0)
            {
                return StepOutcome.Wait; // 节奏台账每 1 游戏秒检查一次
            }
            CampaignState s = St;
            CombatSite site = Site;
            if (site == null)
            {
                return StepOutcome.Fail("铸造前哨没有战斗内核");
            }
            ActPaceRecord pace = Act1PacingService.Find(s, "foundry_outer");
            bool memo = NarrativeService.Has(s, "node.guns_up");
            var kinds = new SortedSet<string>();
            int inKernel = 0;
            foreach (RegionEnemyRecord e in s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
            {
                if (e != null && e.IsAlive && e.RegionId == FoundryOutpostLayout.RegionId && site.TryGetEnemyUnit(e.EnemyInstanceId, out _))
                {
                    inKernel++;
                    kinds.Add(e.EnemyTypeId);
                }
            }
            var jams = new List<(string id, Vector2 at, float radius)>();
            EnemyJamFields.CollectActive(s, FoundryOutpostLayout.RegionId, jams);
            bool fromTable = kinds.All(k => EnemyDefs.TryGet(k, out _));
            bool notYet = (s.Progress?.Act ?? 1) == 1 && !Act1PacingService.IsReached(s, "foundry_core") && !NarrativeService.Has(s, "node.what_waits"); // 负向：进外围不算打完
            if (pace == null || !memo || inKernel == 0 || !fromTable || jams.Count == 0 || !notYet)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait
                    : StepOutcome.Fail($"进铸造前哨后：节奏“铸造前哨外围” {pace != null}、资料库“炮口朝上” {memo}、内核里的敌人 {inKernel}（{string.Join(",", kinds)}，都在敌人表 {fromTable}）、干扰场 {jams.Count}、还在第一幕且没提前记“铸造主核心” {notYet}");
            }
            c.Set("jam.id", jams[0].id);
            return StepOutcome.Done($"“铸造前哨外围”记在第 {pace.Tick} 步（{Act1PacingService.FormatMinute(Act1PacingService.MinuteOf(s, "foundry_outer"))}）；资料库收到“炮口朝上”；" +
                                    $"还在第 1 幕、“铸造主核心”未到；内核里 {inKernel} 个敌人（敌人表类型 {string.Join("、", kinds)}）；干扰场 {jams.Count} 个（{string.Join("、", jams.Select(j => $"{j.id} @({F(j.at.x)},{F(j.at.y)}) 半径 {F(j.radius)}"))}）");
        }

        private static StepOutcome TickUplinked(JourneyContext c)
        {
            int b = c.GetInt("other");
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点机器列表失败：" + FgjM1Journey.UiFail(c));
            }
            if (SignalPresence.CurrentMachineLogicId != b || Fo?.PossessedMachineLogicId != b || WorldView.Director.Mode != ViewMode.Direct)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {Label(b)}（{SignalUplinkService.LastFeedbackText}）");
            }
            return c.StepElapsed < 2.0 ? StepOutcome.Wait : StepOutcome.Done($"接入 {Label(b)}（镜头直控）");
        }

        /// <summary>开着接入的机器驶向干扰源：进入干扰场 → 宽限预警 → 断链（信号回归还核心、镜头回战略、机器进安全模式，原因：干扰）。DEBT-FG1E2E01-03。</summary>
        private static StepOutcome TickDriveIntoJam(JourneyContext c)
        {
            FgjM3Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            int b = c.GetInt("other");
            CampaignState s = St;
            if (SignalLinkService.IsInSafeMode(s, b))
            {
                JourneyInput.ReleaseKeys();
                if (c.GetLong("safeAtMs") == 0)
                {
                    c.SetLong("safeAtMs", Math.Max(1, (long)(c.StepElapsed * 1000)));
                    return StepOutcome.Wait;
                }
                if (c.StepElapsed - c.GetLong("safeAtMs") / 1000.0 < 1.5)
                {
                    return StepOutcome.Wait;
                }
                SignalSafeModeRecord rec = s.SignalCore.SafeModes.First(m => m.LogicId == b);
                bool ok = rec.Reason == (int)SignalLinkBreakReason.Jammed && SignalPresence.AtCore && WorldView.Director.Mode == ViewMode.Strategy && c.GetInt("warned") == 1
                          && SignalLinkService.LastBreakReason == SignalLinkBreakReason.Jammed && SignalLinkService.LastBreakLogicId == b;
                return ok
                    ? StepOutcome.Done($"{Label(b)} 驶入“干扰支援”的干扰场（{c.Get("jam.near")}）：先出宽限预警“{c.Get("jam.warn")}”，宽限后断链——信号回到归还核心、镜头回战略；" +
                                       $"{Label(b)} 进入安全模式（原因：{SignalLinkService.ReasonName(SignalLinkBreakReason.Jammed)}）；反馈“{SignalUplinkService.LastFeedbackText}”")
                    : StepOutcome.Fail($"断链后状态不对：原因 {rec.Reason}、信号在核心 {SignalPresence.AtCore}、镜头 {WorldView.Director.Mode}、见过宽限预警 {c.GetInt("warned")}、最近断链 {SignalLinkService.LastBreakReason}/{SignalLinkService.LastBreakLogicId}");
            }
            if (!Alive(b))
            {
                return StepOutcome.Fail($"{Label(b)} 还没进干扰场就被打掉了（{TeamHp(c)}）");
            }
            if (SignalPresence.CurrentMachineLogicId != b)
            {
                return StepOutcome.Fail($"驶入干扰场前信号已不在 {Label(b)} 里（{SignalUplinkService.LastFeedbackText}）");
            }
            var jams = new List<(string id, Vector2 at, float radius)>();
            EnemyJamFields.CollectActive(s, FoundryOutpostLayout.RegionId, jams);
            if (jams.Count == 0 || Site == null || !Site.TryGetMachinePosition(b, out Vector2 me))
            {
                return StepOutcome.Fail($"干扰场不见了（{jams.Count}）/ 找不到接入的机器");
            }
            (string id, Vector2 at, float radius) jam = jams.OrderBy(j => (j.at - me).sqrMagnitude).First();
            if (MachineRegistry.TryGetRecord(b, out MachineRecord r0) && SignalLinkService.CauseAt(s, r0) == SignalLinkBreakReason.Jammed && c.GetInt("warned") == 0)
            {
                c.SetInt("warned", 1);
                c.Set("jam.warn", SignalCoreHudUIToolkit.Instance?.UplinkStatusText ?? SignalLinkService.WarningLine ?? string.Empty);
                c.Set("jam.near", $"离干扰源 {jam.id} {F(Vector2.Distance(me, jam.at))} 格，半径 {F(jam.radius)}");
                c.Log($"进入干扰场：{c.Get("jam.near")}；宽限预警“{c.Get("jam.warn")}”");
            }
            if (!JourneyInput.Holding && c.GetInt("warned") == 0)
            {
                FgjM1Journey.DriveToward(me, jam.at, 0.4);
            }
            return StepOutcome.Wait;
        }

        // ── RTS：选队、右键攻击 ─────────────────────────────────────────────────────

        private static StepOutcome TickSelectTeam(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            List<int> team = AliveTeam(c);
            IReadOnlyList<int> sel = Fo.SquadCommands.Selection;
            if (team.Count > 0 && sel.Count == team.Count && team.All(sel.Contains))
            {
                return StepOutcome.Done($"选择集 [{string.Join("、", sel.Select(Label))}]（{team.Count} 台存活）");
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 700)
            {
                return StepOutcome.Wait;
            }
            int k = c.GetInt(FgjM3Common.SK(c, "k"));
            if (k >= team.Count)
            {
                return StepOutcome.Retry($"点完一遍后选择集是 [{string.Join("、", sel.Select(Label))}]，应为 [{string.Join("、", team.Select(Label))}]");
            }
            int id = team[k];
            if (Site == null || !Site.TryGetMachineMarker(id, out HomeValleyMachineMarker m) || m?.View == null)
            {
                return StepOutcome.Fail($"{Label(id)} 不在铸造前哨战斗内核里");
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

        private static void EnsureTriple(JourneyContext c)
        {
            if (Mathf.Approximately(GameClock.Speed, 3f) || NowMs() - c.GetLong("spd.at") < 1500)
            {
                return;
            }
            UnityEngine.UIElements.Button triple = JourneyInput.FindUitk<UnityEngine.UIElements.Button>(WorldBarHost, "WorldSpeed3");
            if (triple != null && JourneyInput.IsClickable(triple) && JourneyInput.ClickElement(triple))
            {
                c.SetLong("spd.at", NowMs());
                c.SetInt("spd.n", c.GetInt("spd.n") + 1);
            }
        }

        private static bool TryEnemyPos(string enemyId, out Vector2 p)
        {
            p = default;
            return Site != null && Site.TryGetEnemyPosition(enemyId, out p);
        }

        private static int Attacking(JourneyContext c, string enemyId)
        {
            CombatSite site = Site;
            if (site == null || !site.TryGetEnemyUnit(enemyId, out int target))
            {
                return 0;
            }
            int n = 0;
            foreach (int id in AliveTeam(c))
            {
                if (site.TryGetMachineUnit(id, out int u) && site.TryGetCommand(u, out CombatCommand cmd) && cmd.Kind == CombatCommandKind.Attack && site.Kernel.TryGetUnit(u, out CombatUnitView v) && v.CommandTarget == target)
                {
                    n++;
                }
            }
            return n;
        }

        private static Vector2 Centroid(JourneyContext c)
        {
            Vector2 sum = Vector2.zero;
            int n = 0;
            foreach (int id in AliveTeam(c))
            {
                if (Site != null && Site.TryGetMachinePosition(id, out Vector2 p))
                {
                    sum += p;
                    n++;
                }
            }
            return n > 0 ? sum / n : FoundryOutpostLayout.EntryEvac.Position;
        }

        /// <summary>右键一个敌人（攻击）：选择集里还有人没在打它、离上次右键 2.5 秒以上才再点。返回这次是否点了。</summary>
        private static bool RightClickEnemy(JourneyContext c, string enemyId)
        {
            string last = c.Get("rc.target", string.Empty);
            if (last.Length > 0)
            {
                int now = Attacking(c, last); // 右键之后内核下一步才换命令：每帧数一次“正在攻击上次右键那个目标”的台数
                if (now > c.GetInt("rc.maxAttack"))
                {
                    c.SetInt("rc.maxAttack", now);
                }
            }
            if (JourneyInput.Holding || NowMs() - c.GetLong("rc.at") < 2500 || !TryEnemyPos(enemyId, out Vector2 p))
            {
                return false;
            }
            int alive = AliveTeam(c).Count;
            if (Attacking(c, enemyId) >= alive && c.Get("rc.target") == enemyId)
            {
                return false;
            }
            // 选择集里有人阵亡 / 安全模式机器退出后，选择集还是那几台：RTS 里玩家直接右键即可（选择集只认活着的）。
            if (!FgjM3Common.TryClick(p, 1))
            {
                return false;
            }
            c.SetLong("rc.at", NowMs());
            c.Set("rc.target", enemyId);
            c.SetInt("rc.n", c.GetInt("rc.n") + 1);
            return true;
        }

        private static void Diagnose(JourneyContext c, string what)
        {
            if ((int)(c.StepElapsed / 10) <= c.GetInt(FgjM3Common.SK(c, "diag")))
            {
                return;
            }
            c.SetInt(FgjM3Common.SK(c, "diag"), (int)(c.StepElapsed / 10));
            RegionEnemyRecord core = FoundryOutpostRegion.FindEnemy(St, FoundryOutpostLayout.MainCoreId);
            c.Log($"[{F(c.StepElapsed, "0")}s] {what}；首领 {Boss}（主核心 {(core != null ? F(core.Health, "0") + "/" + F(core.MaxHealth, "0") : "-")}）；远征队 {TeamHp(c)}；右键 {c.GetInt("rc.n")} 次；倍速 {F(GameClock.Speed)}");
        }

        /// <summary>核心门前（外围、门线以南、Demo 布局宽度内）还活着的敌人。</summary>
        private static List<string> OuterHostiles()
        {
            var o = new List<string>();
            CampaignState s = St;
            foreach (RegionEnemyRecord e in s?.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
            {
                if (e == null || !e.IsAlive || e.RegionId != FoundryOutpostLayout.RegionId || !TryEnemyPos(e.EnemyInstanceId, out Vector2 p))
                {
                    continue;
                }
                if (p.y < FoundryOutpostLayout.CoreGateBlockLineY && Mathf.Abs(p.x) <= FoundryOutpostLayout.CameraBoundsHalfExtentX + 4f)
                {
                    o.Add(e.EnemyInstanceId);
                }
            }
            return o;
        }

        private static StepOutcome TickClearOuter(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            EnsureTriple(c);
            if (AliveTeam(c).Count == 0)
            {
                return StepOutcome.Fail("远征队全灭");
            }
            List<string> left = OuterHostiles();
            if (c.GetInt(FgjM3Common.SK(c, "n0")) == 0)
            {
                c.SetInt(FgjM3Common.SK(c, "n0"), left.Count + 1);
                c.Log($"核心门前的守军 {left.Count} 个：{string.Join("、", left)}");
            }
            Diagnose(c, $"外围剩 {left.Count}");
            if (left.Count == 0)
            {
                return c.GetInt("rc.n") > 0 && c.GetInt("rc.maxAttack") > 0
                    ? StepOutcome.Done($"核心门前的守军 {c.GetInt(FgjM3Common.SK(c, "n0")) - 1} 个全部打掉（右键攻击 {c.GetInt("rc.n")} 次，最多 {c.GetInt("rc.maxAttack")} 台同时攻击右键的目标）；远征队 {TeamHp(c)}")
                    : StepOutcome.Fail($"核心门前的守军没了，但右键攻击 {c.GetInt("rc.n")} 次、最多 {c.GetInt("rc.maxAttack")} 台接到攻击命令（右键敌人没变成攻击命令 / 一开始就没有守军）");
            }
            Vector2 at = Centroid(c);
            string target = left.OrderBy(id => TryEnemyPos(id, out Vector2 p) ? (p - at).sqrMagnitude : float.MaxValue).First();
            RightClickEnemy(c, target);
            return StepOutcome.Wait;
        }

        private static StepOutcome TickEnterArena(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            EnsureTriple(c);
            Vector2 goal = FoundryOutpostLayout.CoreZoneEntry.Position + new Vector2(0f, 1.5f);
            if (!FgjM3Common.Once(c, "rc", () => FgjM3Common.TryClick(goal, 1)))
            {
                return StepOutcome.Wait;
            }
            if (c.GetInt(FgjM3Common.SK(c, "moveChecked")) == 0 && FgjM3Common.SinceMs(c, "rc") > 600)
            {
                c.SetInt(FgjM3Common.SK(c, "moveChecked"), 1);
                int moving = AliveTeam(c).Count(id => Site.TryGetMachineUnit(id, out int u) && Site.TryGetCommand(u, out CombatCommand cmd) && cmd.Kind == CombatCommandKind.Move);
                c.Log($"右键核心区地面：{moving}/{AliveTeam(c).Count} 台接到移动命令（{Fo.SquadCommands.RecentEvents.LastOrDefault()}）");
                if (moving == 0)
                {
                    return StepOutcome.Retry($"右键地面后远征队没有接到移动命令（{Fo.SquadCommands.RecentEvents.LastOrDefault()}）");
                }
            }
            Diagnose(c, "进场中");
            RegionRecord r = Region;
            if (r != null && FoundryOutpostCoreBoss.IsInitialized(r) && Boss == CoreBossState.Shielded)
            {
                c.Set("boss.t0", GameClock.GameSeconds.ToString("R", CultureInfo.InvariantCulture));
                return StepOutcome.Done($"远征队过核心区大门（三灯全亮，门放行）：首领战开始——护盾阶段（HUD“{FoundryOutpostCoreBoss.DisplayPhaseText(r)}”），两个供能节点展开；远征队 {TeamHp(c)}");
            }
            if (AliveTeam(c).Count == 0)
            {
                return StepOutcome.Fail("远征队全灭");
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickNodes(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            EnsureTriple(c);
            if (AliveTeam(c).Count == 0)
            {
                return StepOutcome.Fail($"远征队在护盾阶段全灭（首领 {Boss}）");
            }
            CampaignState s = St;
            RegionEnemyRecord n1 = FoundryOutpostRegion.FindEnemy(s, FoundryOutpostLayout.CoreNode1Id);
            RegionEnemyRecord n2 = FoundryOutpostRegion.FindEnemy(s, FoundryOutpostLayout.CoreNode2Id);
            Diagnose(c, $"节点 1 {(n1 != null ? F(n1.Health, "0") : "-")}、节点 2 {(n2 != null ? F(n2.Health, "0") : "-")}");
            if (Boss != CoreBossState.Shielded)
            {
                bool bothDead = (n1 == null || !n1.IsAlive) && (n2 == null || !n2.IsAlive);
                return Boss == CoreBossState.Phase1 && bothDead
                    ? StepOutcome.Done($"两个供能节点都被打掉：护盾消失，主核心进入阶段一（“{FoundryOutpostCoreBoss.DisplayPhaseText(Region)}”）；远征队 {TeamHp(c)}")
                    : StepOutcome.Fail($"护盾阶段结束后：首领 {Boss}，节点都打掉 {bothDead}");
            }
            string target = n1 != null && n1.IsAlive ? FoundryOutpostLayout.CoreNode1Id : FoundryOutpostLayout.CoreNode2Id;
            RightClickEnemy(c, target);
            return StepOutcome.Wait;
        }

        private static StepOutcome TickCore(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            EnsureTriple(c);
            CoreBossState now = Boss;
            string seen = c.Get("core.seen", string.Empty);
            if (!seen.EndsWith(now.ToString(), StringComparison.Ordinal))
            {
                seen = seen.Length == 0 ? now.ToString() : seen + "→" + now;
                c.Set("core.seen", seen);
                RegionEnemyRecord coreRec = FoundryOutpostRegion.FindEnemy(St, FoundryOutpostLayout.MainCoreId);
                c.Log($"首领阶段：{seen}（首领战第 {F(BossSeconds(c), "0.0")} 游戏秒，主核心 {(coreRec != null ? F(coreRec.Health, "0") + "/" + F(coreRec.MaxHealth, "0") : "-")}；" +
                      $"HUD“{FoundryOutpostCoreBoss.DisplayPhaseText(Region)}”；远征队 {TeamHp(c)}；最多 {c.GetInt("rc.maxAttack")} 台同时攻击右键的目标）");
            }
            Diagnose(c, "主核心战");
            if (now == CoreBossState.Destroyed)
            {
                bool phase3 = seen.Contains(CoreBossState.AntiAir.ToString());
                bool phase2 = seen.Contains(CoreBossState.Phase2.ToString());
                return phase2 && phase3 && c.GetInt("rc.maxAttack") > 0
                    ? StepOutcome.Done($"主核心被摧毁：阶段 {seen}，首领战（进场到摧毁）{F(BossSeconds(c), "0.0")} 游戏秒（右键攻击累计 {c.GetInt("rc.n")} 次，最多 {c.GetInt("rc.maxAttack")} 台同时攻击右键的目标）；远征队 {TeamHp(c)}")
                    : StepOutcome.Fail($"主核心摧毁了，但阶段序列 {seen} 没经过阶段二 {phase2} / 对空形态 {phase3}，或右键攻击没变成攻击命令（最多 {c.GetInt("rc.maxAttack")} 台）");
            }
            if (AliveTeam(c).Count == 0)
            {
                return StepOutcome.Fail($"远征队在首领战全灭（阶段 {seen}）");
            }
            RightClickEnemy(c, FoundryOutpostLayout.MainCoreId);
            return StepOutcome.Wait;
        }

        private static double BossSeconds(JourneyContext c) =>
            double.TryParse(c.Get("boss.t0", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out double t0) ? GameClock.GameSeconds - t0 : -1;

        private static StepOutcome TickWon(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait; // 领地控制每游戏秒查一次剧情规则；节奏台账每秒检查
            }
            CampaignState s = St;
            bool pacified = TerritoryControlService.Find(s, FoundryTerritory)?.LeaderDefeated ?? false;
            ActPaceRecord core = Act1PacingService.Find(s, "foundry_core");
            ActPaceRecord end = Act1PacingService.Find(s, "act1_end");
            bool memo = NarrativeService.Has(s, "node.what_waits");
            int act = s.Progress?.Act ?? 1;
            bool codex = MechanicCodex.IsUnlocked(CodexBoss);
            bool reward = FoundryOutpostCoreBoss.RewardGrants > 0;
            if (!pacified || core == null || end == null || !memo || act < 2 || !codex || !reward)
            {
                return c.StepElapsed < 15 ? StepOutcome.Wait
                    : StepOutcome.Fail($"主核心摧毁后：领地平定 {pacified}、节奏“铸造主核心” {core != null}、“第一幕结束” {end != null}、资料库“它在等什么” {memo}、第 {act} 幕、图鉴“铸造主核心”解锁 {codex}、击败奖励 {reward}");
            }
            return StepOutcome.Done($"铸造领地平定；“铸造主核心”第 {core.Tick} 步（{Act1PacingService.FormatMinute(Act1PacingService.MinuteOf(s, "foundry_core"))}）、" +
                                    $"“第一幕结束”第 {end.Tick} 步（{Act1PacingService.FormatMinute(Act1PacingService.MinuteOf(s, "act1_end"))}）；资料库收到“它在等什么”；战役第 {act} 幕；图鉴敌人“铸造主核心”已解锁（夹具时 {(c.GetInt("codexBossBefore") == 1 ? "已按本机见过的钩子补解锁" : "还锁着")}）；击败奖励发放 {FoundryOutpostCoreBoss.RewardGrants} 次");
        }

        // ── 资料库 ──────────────────────────────────────────────────────────────────

        private static StepOutcome TickPauseOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            return PauseMenuUIToolkit.IsOpen ? StepOutcome.Done("暂停菜单打开（世界暂停）") : StepOutcome.Retry("按 Esc 后暂停菜单没有打开");
        }

        private static StepOutcome TickLibrary(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            NarrativeLibraryPanelUIToolkit lib = NarrativeLibraryPanelUIToolkit.Instance;
            if (lib == null || !NarrativeLibraryPanelUIToolkit.IsOpen || !lib.PanelVisible)
            {
                return StepOutcome.Retry("点“资料库”后资料库没有打开（" + FgjM1Journey.UiFail(c) + "）");
            }
            var ids = Enumerable.Range(0, lib.RowCount).Select(lib.RowId).ToList();
            int waits = ids.IndexOf("node.what_waits");
            bool guns = ids.Contains("node.guns_up");
            NarrativeEntryRecord rec = St.Progress.Narrative.FirstOrDefault(n => n != null && n.Id == "node.what_waits");
            int act = St.Progress.Act;
            return waits >= 0 && guns && rec != null && rec.Act == 1 && act >= 2
                ? StepOutcome.Done($"资料库 {lib.RowCount} 条（“{lib.CountText}”）：有“{lib.RowText(waits)}”（第 {rec.Act} 幕的最后一条；战役已在第 {act} 幕）与“炮口朝上”")
                : StepOutcome.Fail($"资料库里：它在等什么 {waits >= 0}（第 {rec?.Act} 幕）、炮口朝上 {guns}、战役第 {act} 幕；条目 [{string.Join(",", ids)}]");
        }

        private static readonly string CodexBoss = MechanicCodex.EnemyEntryId("enemy_core_boss");

        private static StepOutcome TickCodexTab(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            MechanicCodexPanelUIToolkit p = MechanicCodexPanelUIToolkit.Instance;
            return p != null && p.CurrentTab == MechanicCodex.TabOf(CodexBoss)
                ? StepOutcome.Done($"点“{p.CurrentTab}”页签：{p.CountText}")
                : StepOutcome.Retry("点页签后不在该页签：" + FgjM1Journey.UiFail(c));
        }

        private static void ClickCodexEntry(JourneyContext c)
        {
            MechanicCodexPanelUIToolkit p = MechanicCodexPanelUIToolkit.Instance;
            for (int i = 0; p != null && i < p.ItemCount; i++)
            {
                if (p.ItemId(i) == CodexBoss)
                {
                    Button b = p.ItemButton(i);
                    // 条目在长列表靠后：像玩家一样滚滚轮，滚到完整可见（下一帧布局更新）再点（最多 80 次）。
                    if (!JourneyInput.ScrollIntoView(JourneyInput.FindUitk<ScrollView>("[MechanicCodexHost]", "CodexList"), b))
                    {
                        c.SetInt("codex.wheel", c.GetInt("codex.wheel") + 1);
                        c.Set("uiFail", "scrolling");
                        return;
                    }
                    c.Set("uiFail", JourneyInput.ClickElement(b) ? string.Empty : JourneyInput.LastUiFailure);
                    return;
                }
            }
            c.Set("uiFail", "页签里没有“铸造主核心”条目");
        }

        private static StepOutcome TickCodexEntry(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c) == "scrolling" && c.GetInt("codex.wheel") < 80)
            {
                ClickCodexEntry(c);
                return StepOutcome.Wait;
            }
            MechanicCodexPanelUIToolkit p = MechanicCodexPanelUIToolkit.Instance;
            string title = MechanicCodex.Title(MechanicCodex.Find(CodexBoss));
            bool ok = p != null && p.SelectedId == CodexBoss && p.EntryTitleText.Contains(title) && p.EntryBodyText.Length > 0 && !GameText.ContainsMarker(p.EntryTitleText + p.EntryBodyText)
                      && MechanicCodex.IsUnlocked(CodexBoss);
            return ok
                ? StepOutcome.Done($"“{p.EntryTitleText}”已解锁：{p.EntryBodyText.Replace('\n', ' ').Substring(0, Math.Min(60, p.EntryBodyText.Length))}…")
                : StepOutcome.Retry($"图鉴条目：选中 {p?.SelectedId}、标题“{p?.EntryTitleText}”、解锁 {MechanicCodex.IsUnlocked(CodexBoss)}（{FgjM1Journey.UiFail(c)}）");
        }

        private static StepOutcome TickLibraryClosed(JourneyContext c)
        {
            if (NarrativeLibraryPanelUIToolkit.IsOpen)
            {
                return c.StepElapsed < 3 ? StepOutcome.Wait : StepOutcome.Retry("点关闭后资料库还开着（" + FgjM1Journey.UiFail(c) + "）");
            }
            if (PauseMenuUIToolkit.IsOpen)
            {
                if (NowMs() - c.GetLong(FgjM3Common.SK(c, "esc")) > 800)
                {
                    c.SetLong(FgjM3Common.SK(c, "esc"), NowMs());
                    JourneyInput.PressAction(GameActionId.Cancel);
                }
                return c.StepElapsed < 6 ? StepOutcome.Wait : StepOutcome.Retry("按 Esc 后暂停菜单关不掉");
            }
            return GameClock.Paused ? StepOutcome.Wait : StepOutcome.Done("资料库关闭、暂停菜单关闭，回到游戏（世界继续运行）");
        }
    }
}
