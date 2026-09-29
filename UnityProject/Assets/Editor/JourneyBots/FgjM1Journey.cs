using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Stage;
using GameLogic.UI.CircuitBoard;
using GameLogic.UI.Kit;
using GameLogic.UI.SignalCore;
using GameLogic.View;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG1-E2E-01：M1 出口旅程 FGJ-M1（ProjectA_FullGame_Milestones.md FG-M1）。
    /// 在家园编辑电路标出接入口，看双态预览 → 给信号核装上“过载” → 远征 → 接入重炮机，看到形变，打出熔穿过载 →
    /// 切到另一台机器，确认冷却没有重置 → （接入中存档 → 回主菜单读档，信号仍在那台机器里）→ 被干扰机断链，进入安全模式 →
    /// 跳回家园 → 再跳回远征队 → 远征队返回家园。
    ///
    /// 全程从主菜单“新建”出发、固定测试种子、走正式输入（<see cref="JourneyInput"/>：世界层键鼠经 InputRouter 读取后端；
    /// uGUI 按钮经 EventSystem；UI Toolkit 控件向面板派发真实指针事件并检查遮挡），不调业务方法冒充玩家。
    /// 家园前置用 Demo 的正式早期循环完成：点机器 + 右键下令修复发电机与信号塔、装配站生产 ERC-003（用刚保存的重炮接入口蓝图）、
    /// 点信号塔打开远征准备面板勾选名单出发。
    ///
    /// 与里程碑文字不同的两处（登记在 FG-GAP-REGISTER，DEBT-FG1E2E01-02 / -03，写进 ADR-QA-017）：
    /// - **进度夹具**：新档没有“铸造重炮”组件（Demo 里要先远征铸造前哨带回重炮模块、在解析台解析），开局后把它记为已解析（等同解析完成的解锁），其余全走正式入口。
    /// - **远征地点是破碎都市**：当前内容里只有破碎都市有干扰场（监听节点，FracturedCityRegion.IsPositionJammed），铸造前哨外围地图小于远征覆盖半径、
    ///   也没有干扰源，在那里“被干扰机断链 → 安全模式”不可能发生；熔穿过载是“重炮 + 接入口里的过载”的编译结果，与地点无关，破碎都市同样打得出。
    ///   正式世界的干扰机分布随 FG9-DATA-01（敌人数据）与 FG8（无缝世界）落地后改回里程碑原文的地点。
    /// </summary>
    public static class FgjM1Journey
    {
        public const string Id = "FGJ-M1";

        /// <summary>固定测试种子：与 FGJ-M0 同一颗（FgWorldGenSelfCheck 基准里有它）。</summary>
        public const int TestSeed = FgjM0Journey.TestSeed;

        /// <summary>驾驶进干扰场的目标点：监听节点正南 8 格（干扰半径 12 以内），离驻守干扰机 &gt; 攻击距离 8。按布局锚点算，不写死坐标。</summary>
        private static Vector2 JamProbe => FracturedCityLayout.ListeningNode.Position + new Vector2(0f, -8f);

        private static Vector2 EvacPoint => FracturedCityLayout.EntryEvac.Position;

        // 帧耗时采样（Play 期间不重载域，放静态字段）。
        private static readonly List<float> FrameMs = new List<float>(4096);
        private static int _lastFrame = -1;
        private static bool _sampling;

        public static JourneyDef Build() => new JourneyDef
        {
            Id = Id,
            Title = "M1 出口：编辑接入口 → 装过载 → 远征 → 接入重炮打出熔穿过载 → 切机冷却不重置 → 接入中存读档 → 干扰断链安全模式 → 跳回家园 → 跳回远征队 → 返回家园",
            Seed = TestSeed,
            TotalTimeoutSeconds = 900,
            OnFinish = Cleanup,
            Steps = new List<JourneyStep>
            {
                S("play", "打开 main.unity 并进入 Play", 90, JourneyCommon.EnterPlay, c => JourneyCommon.TickPlay(c, TestSeed, ResetStatics)),
                S("menu_new", "主菜单点“新建”（固定测试种子）", 150, null, JourneyCommon.TickMenuNew, retries: 1),
                S("new_game", "进入归还谷地", 120, null, JourneyCommon.TickNewGame),
                S("seed", "生成结果与该种子的基准一致", 30, null, c => JourneyCommon.TickSeed(c, TestSeed)),
                S("fixture", "进度夹具：记“铸造重炮模块已解析”（Demo 要先远征铸造前哨带回再解析）", 10, ApplyProgressFixture, TickFixture),

                // ── 家园前置（Demo 正式早期循环）──
                S("speed3", "按 3 倍速键", 10, c => PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), TickSpeed3, retries: 1),
                S("sel_worker_a", "左键点一台工程机", 15, c => ClickMachine(c, "workerA"), c => TickSelected(c, "workerA"), retries: 2),
                S("repair_gen", "右键点受损的发电机（情境命令：修复）", 15, c => RightClickBuilding(HomeValleyLayout.BuildingTypeGenerator),
                    c => TickRepairOrdered(c, HomeValleyLayout.BuildingTypeGenerator), retries: 2),
                S("sel_worker_b", "左键点另一台工程机", 15, c => ClickMachine(c, "workerB"), c => TickSelected(c, "workerB"), retries: 2),
                S("repair_tower", "右键点受损的信号塔", 15, c => RightClickBuilding(HomeValleyLayout.BuildingTypeSignalTower),
                    c => TickRepairOrdered(c, HomeValleyLayout.BuildingTypeSignalTower), retries: 2),
                S("wait_repairs", "等发电机与信号塔修好", 300, null, TickRepairsDone),

                // ── 第 1 步：在家园编辑电路标出接入口，看双态预览 ──
                S("cb_open", "点“蓝图编辑器”入口", 10, c => ClickUi(c, "[HomeValleyCircuitBoardHost]", "EntryToggleButton", when: () => !(GameRoot.HomeValley?.IsCircuitBoardPanelOpen ?? false)),
                    TickEditorOpen, retries: 1),
                S("cb_pick", "在蓝图列表点 ERC-003 蓝图", 10, ClickErc003Row, TickErc003Open, retries: 1),
                S("cb_primary", "主组件下拉选“铸造重炮”", 10, PickCannon, TickCannonPicked, retries: 1),
                S("cb_slot", "点选导线经过的空格", 10, ClickPathSlot, TickSlotPicked, retries: 1),
                S("cb_mark", "点“标为接入口”，看双态预览（信号核还空着）", 10, c => ClickUi(c, "[HomeValleyCircuitBoardHost]", "UplinkToggleButton"), TickMarkedEmptyCore, retries: 1),
                S("cb_save", "点“保存”", 10, c => ClickUi(c, "[HomeValleyCircuitBoardHost]", "SaveButton"), TickSaved, retries: 1),
                S("cb_close", "按 Esc 关闭蓝图编辑器", 10, c => PressIf(GameActionId.Cancel, GameRoot.HomeValley.IsCircuitBoardPanelOpen), TickEditorClosed, retries: 1),

                // ── 第 2 步：给信号核装上“过载” ──
                S("sc_open", "按信号核键（默认 P）打开信号核面板", 10, c => PressIf(GameActionId.OpenSignalCore, !SignalCoreHudUIToolkit.IsOpen), TickCoreOpen, retries: 1),
                S("sc_print", "刻印下拉选“过载”，点“刻印”", 10, PrintOverload, TickPrinted, retries: 1),
                S("sc_equip", "点“装入 1 号槽”", 10, c => ClickUi(c, "[SignalCoreHost]", "SignalEquip"), TickEquipped, retries: 1),
                S("sc_close", "再按信号核键关闭面板", 10, c => PressIf(GameActionId.OpenSignalCore, SignalCoreHudUIToolkit.IsOpen), TickCoreClosed, retries: 1),
                S("cb_recheck", "重开蓝图编辑器看 ERC-003 蓝图：“你接入时”一栏变成过载 + 熔穿过载", 15, ReopenEditor, TickPreviewWithOverload, retries: 1),
                S("cb_close_2", "按 Esc 关闭蓝图编辑器", 10, c => PressIf(GameActionId.Cancel, GameRoot.HomeValley.IsCircuitBoardPanelOpen), TickEditorClosed, retries: 1),

                // ── 生产重炮机 ──
                S("fac_open", "左键点装配站打开生产面板", 10, c => ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, !GameRoot.HomeValley.IsFactoryPanelOpen), TickFactoryOpen, retries: 1),
                S("fac_produce", "点“生产 ERC-003”（用刚保存的重炮接入口蓝图）", 10, ClickProduce, TickProduceQueued, retries: 1),
                S("fac_close", "再点装配站关闭生产面板", 10, c => ClickBuildingIf(HomeValleyLayout.BuildingTypeAssemblyStation, GameRoot.HomeValley.IsFactoryPanelOpen), TickFactoryClosed, retries: 1),
                S("wait_cannon", "等重炮机出厂", 240, null, TickCannonProduced),
                S("cannon_sel", "左键点重炮机", 15, c => { c.SetInt("cannonKey", c.GetInt("cannon")); ClickMachine(c, "cannonKey"); }, c => TickSelected(c, "cannonKey"), retries: 2),
                S("cannon_out", "右键点旁边的空地：新机驶出装配站出口", 15, DriveCannonOut, TickCannonOut, retries: 2),

                // ── 第 3 步：远征（破碎都市，见类注释）──
                S("prep_open", "左键点信号塔打开远征准备面板", 15, c => ClickBuildingIf(HomeValleyLayout.BuildingTypeSignalTower, !GameRoot.HomeValley.IsExpeditionPrepPanelOpen), TickPrepOpen, retries: 2),
                S("prep_pick", "勾选出征名单（重炮机 + 另外两台，满足人数 / 带宽 / 货位）", 20, null, TickRosterPicked),
                S("prep_depart", "点“出发”（有在办工作时确认中断）", 30, c => ClickUi(c, "[HomeValleyExpeditionPrepHost]", "DepartButton"), TickDeparted, retries: 1),

                // ── 第 4 步：接入重炮机，看到形变，打出熔穿过载 ──
                S("up_cannon", "机器列表点重炮机：跨地点远距离跳转后接入，机身形变", 25, c => ClickMachineList(c, c.GetInt("cannon")), TickCannonUplinked, retries: 3),
                S("drive_fire", "开着重炮机靠近侦察机（WASD），左键瞄准开火：打出熔穿过载", 60, c => BeginSampling(), TickFireMeltdown),

                // ── 第 5 步：切到另一台机器，冷却没有重置、热量留在机体 ──
                S("switch", "接入中按 Tab 切到另一台机器", 10, PressSwitch, TickSwitched, retries: 1),

                // ── 接入中存档 → 读取（DEBT-FG1SIG03-06）──
                S("save_esc", "按 Esc 打开暂停菜单（信号在机器里）", 10, c => PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseForSave, retries: 1),
                S("save_quit", "点“保存并返回主菜单”，确认", 15, c => ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), TickSaveConfirm),
                S("menu_back", "回到主菜单：存档里信号在那台机器里、冷却 / 热量 / 固件逐字段一致", 60, null, TickMenuAfterSave),
                S("load_list", "点“读取”，点刚才的存档槽", 20, c => ClickUgui(c, "m_btn_Load"), TickLoadSlot),
                S("loaded", "读档进入破碎都市：信号仍在那台机器里", 120, null, TickLoadedUplinked),

                // ── 第 6 步：被干扰机断链，进入安全模式 ──
                S("drive_jam", "开着机器（WASD）驶入监听节点的干扰场：宽限后断链，机器进入安全模式", 60, null, TickDriveIntoJam),
                S("select_safe", "战略视角左键点安全模式的机器", 15, c => ClickFcMachine(c, c.GetInt("other")), c => TickFcSelected(c, c.GetInt("other")), retries: 2),
                S("cmd_out", "右键撤离点附近的地面：安全模式的机器照常接令，驶出干扰场后自动退出安全模式", 60, c => RightClickFcGround(EvacPoint + new Vector2(0f, 1f)), TickSafeModeRecovered),

                // ── 第 7、8 步：跳回家园 → 再跳回远征队 ──
                S("jump_home", "按跳回家园键（默认 H）", 10, c => JourneyInput.PressAction(GameActionId.JumpHome), TickJumpedHome, retries: 1),
                S("jump_back", "按跳回上一台机器键（默认 J）：跨地点远距离跳转回远征队", 25, PressJumpBack, TickJumpedBack, retries: 1),

                // ── 第 9 步：远征队返回家园 ──
                S("evac_hold", "在撤离点按住交互键（默认 E）打开撤离清单", 20, c => JourneyInput.HoldAction(GameActionId.Interact, 1.6), TickEvacPanel, retries: 2),
                S("evac_confirm", "点“确认撤离”：远征队返回家园", 30, c => ClickUi(c, "[FracturedCityExpeditionReturnHost]", "ConfirmButton"), TickReturnedHome, retries: 1),
            },
        };

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static void ResetStatics()
        {
            FrameMs.Clear();
            _lastFrame = -1;
            _sampling = false;
        }

        // ── 通用小工具 ──────────────────────────────────────────────────────────────

        internal static CampaignState St => CampaignSession.Current;

        internal static string Label(int id) => SignalPresence.MachineLabel(id);

        internal static void PressIf(GameActionId action, bool condition)
        {
            if (condition)
            {
                JourneyInput.PressAction(action);
            }
        }

        internal static void ClickUi(JourneyContext c, string host, string element, Func<bool> when = null)
        {
            if (when != null && !when())
            {
                return;
            }
            if (!JourneyInput.ClickUitk(host, element))
            {
                c.Set("uiFail", JourneyInput.LastUiFailure);
            }
            else
            {
                c.Set("uiFail", string.Empty);
            }
        }

        internal static void ClickUgui(JourneyContext c, string name)
        {
            c.Set("uiFail", JourneyInput.ClickUgui(name) ? string.Empty : JourneyInput.LastUiFailure);
        }

        internal static string UiFail(JourneyContext c) => c.Get("uiFail", string.Empty);

        internal static Transform FindNamed(string name) =>
            Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault(t => t.name == name);

        internal static bool HomeMarker(int logicId, out HomeValleyMachineMarker m)
        {
            m = null;
            return GameRoot.HomeValley?.Combat != null && GameRoot.HomeValley.Combat.TryGetMachineMarker(logicId, out m) && m != null && m.View != null;
        }

        internal static bool FcMarker(int logicId, out HomeValleyMachineMarker m)
        {
            m = null;
            CombatSite site = GameRoot.FracturedCity?.Combat;
            return site != null && site.TryGetMachineMarker(logicId, out m) && m != null;
        }

        internal static bool FcPos(int logicId, out Vector2 p)
        {
            p = default;
            CombatSite site = GameRoot.FracturedCity?.Combat;
            return site != null && site.TryGetMachinePosition(logicId, out p);
        }

        internal static bool FcActive => GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsActive;

        // ── 进度夹具 ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 进度夹具（DEBT-FG1E2E01-02）：把“铸造重炮”记为已解锁（与解析台完成解析同一个写法：追加进 UnlockedContentIds）。
        /// 只补这一项 Demo 进度；修复、生产、出征、接入全走正式入口。
        /// </summary>
        private static void ApplyProgressFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null)
            {
                return;
            }
            s.UnlockedContentIds ??= Array.Empty<string>();
            if (Array.IndexOf(s.UnlockedContentIds, ComponentCatalog.CompCannonId) < 0)
            {
                s.UnlockedContentIds = s.UnlockedContentIds.Append(ComponentCatalog.CompCannonId).ToArray();
            }
            // 家园里的工程机（ERC-001）：左键点选、右键派修复。按编号取前两台，不写死编号。
            List<MachineRecord> home = MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && !m.IsInFactory && m.RegionId == HomeValleyLayout.RegionId)
                .OrderBy(m => m.LogicId).ToList();
            List<MachineRecord> workers = home.Where(m => m.ChassisId == HomeValleyLayout.Erc001ChassisId).ToList();
            if (workers.Count < 2)
            {
                workers = home;
            }
            c.SetInt("workerA", workers.Count > 0 ? workers[0].LogicId : 0);
            c.SetInt("workerB", workers.Count > 1 ? workers[1].LogicId : 0);
            c.SetInt("scrap0", s.Scrap);
            c.Log($"开局家园机器 {home.Count} 台：{string.Join("，", home.Select(m => $"#{m.DisplayNumber}({m.ChassisId})"))}；废料 {s.Scrap}");
        }

        private static StepOutcome TickFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || Array.IndexOf(s.UnlockedContentIds ?? Array.Empty<string>(), ComponentCatalog.CompCannonId) < 0)
            {
                return StepOutcome.Fail("夹具没有生效：铸造重炮未解锁");
            }
            if (c.GetInt("workerA") == 0 || c.GetInt("workerB") == 0)
            {
                return StepOutcome.Fail("家园里找不到两台可以派工的机器");
            }
            return StepOutcome.Done($"铸造重炮已记为解析完成（其余进度走正式入口）；工程机 {Label(c.GetInt("workerA"))}、{Label(c.GetInt("workerB"))}");
        }

        internal static StepOutcome TickSpeed3(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return Mathf.Approximately(GameClock.Speed, 3f) ? StepOutcome.Done("3 倍速") : StepOutcome.Retry($"倍速 {GameClock.Speed}x");
        }

        // ── 家园：点机器、右键修复 ────────────────────────────────────────────────────

        internal static void ClickMachine(JourneyContext c, string key)
        {
            int id = c.GetInt(key);
            if (HomeMarker(id, out HomeValleyMachineMarker m))
            {
                if (!JourneyCommon.PanToward(m.Position, 0.08f))
                {
                    c.Set("flyFirst", "1"); // 不在画面里：先按方向键把镜头平移过去（玩家的做法），下一次再点。
                    return;
                }
                JourneyInput.ClickWorld(m.View.transform.position);
            }
        }

        internal static StepOutcome TickSelected(JourneyContext c, string key)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            int id = c.GetInt(key);
            if (c.Get("flyFirst") == "1")
            {
                c.Set("flyFirst", string.Empty);
                return StepOutcome.Retry("机器不在画面里，先按方向键把镜头平移过去再点");
            }
            return GameRoot.HomeValley.SelectedMachineLogicId == id
                ? StepOutcome.Done($"左键选中 {Label(id)}")
                : StepOutcome.Retry($"左键后选中的是 {GameRoot.HomeValley.SelectedMachineLogicId}");
        }

        internal static void RightClickBuilding(string typeId)
        {
            Transform t = FindNamed("Building_" + typeId);
            if (t != null)
            {
                JourneyInput.ClickWorld(t.position, button: 1);
            }
        }

        internal static void ClickBuildingIf(string typeId, bool condition)
        {
            Transform t = FindNamed("Building_" + typeId);
            if (condition && t != null)
            {
                JourneyInput.ClickWorld(t.position);
            }
        }

        internal static WorkOrderRecord RepairOrder(string typeId) =>
            St?.WorkOrders?.LastOrDefault(o => o != null && o.Kind == WorkOrderKind.Repair && o.TargetId == HomeValleyLayout.RegionId + ":" + typeId
                                               && o.State != WorkOrderState.Cancelled && o.State != WorkOrderState.Failed);

        internal static BuildingRecord Building(string typeId) =>
            St?.BuildingRecords?.FirstOrDefault(b => b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == typeId);

        internal static StepOutcome TickRepairOrdered(JourneyContext c, string typeId)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            BuildingRecord b = Building(typeId);
            if (b != null && b.ConstructionState == BuildingConstructionState.Operational)
            {
                return StepOutcome.Done($"{typeId} 已经修好");
            }
            WorkOrderRecord o = RepairOrder(typeId);
            return o != null
                ? StepOutcome.Done($"右键下达修复工单（{typeId}，工单 {o.State}）")
                : StepOutcome.Retry($"右键后没有 {typeId} 的修复工单（{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}）");
        }

        internal static StepOutcome TickRepairsDone(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            BuildingRecord g = Building(HomeValleyLayout.BuildingTypeGenerator);
            BuildingRecord t = Building(HomeValleyLayout.BuildingTypeSignalTower);
            bool ok = g != null && g.ConstructionState == BuildingConstructionState.Operational
                      && t != null && t.ConstructionState == BuildingConstructionState.Operational;
            if (!ok)
            {
                // 工单被取消 / 失败（例如缺料）时如实报出来，不干等到超时。
                foreach (string type in new[] { HomeValleyLayout.BuildingTypeGenerator, HomeValleyLayout.BuildingTypeSignalTower })
                {
                    BuildingRecord b = Building(type);
                    if (b != null && b.ConstructionState != BuildingConstructionState.Operational && RepairOrder(type) == null && c.StepElapsed > 5)
                    {
                        return StepOutcome.Fail($"{type} 还没修好，修复工单却没了（废料 {St.Scrap}）");
                    }
                }
                return StepOutcome.Wait;
            }
            return StepOutcome.Done($"发电机与信号塔修好（{c.StepElapsed:F0} 秒，3 倍速），废料 {St.Scrap}");
        }

        // ── 第 1 步：电路编辑器 ────────────────────────────────────────────────────────

        internal static CircuitBoardPanelUIToolkit Panel()
        {
            GameObject host = GameObject.Find("[HomeValleyCircuitBoardHost]");
            return host != null ? host.GetComponent<CircuitBoardPanelUIToolkit>() : null;
        }

        internal static StepOutcome TickEditorOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            CircuitBoardPanelUIToolkit p = Panel();
            return GameRoot.HomeValley.IsCircuitBoardPanelOpen && p?.Board != null
                ? StepOutcome.Done($"蓝图编辑器打开（当前 {p.Board.ChassisId} / {p.Board.PrimaryId}）")
                : StepOutcome.Retry("点入口后蓝图编辑器没有打开：" + UiFail(c));
        }

        internal static BlueprintRecord Erc003Record() => St?.BlueprintRecords?.FirstOrDefault(b => b.BlueprintId == HomeValleyLayout.BlueprintErc003Id);

        internal static void ClickErc003Row(JourneyContext c)
        {
            BlueprintRecord r = Erc003Record();
            ScrollView list = JourneyInput.FindUitk<ScrollView>("[HomeValleyCircuitBoardHost]", "BlueprintList");
            UnityEngine.UIElements.Button row = list?.Query<UnityEngine.UIElements.Button>("OpenButton").ToList()
                .FirstOrDefault(b => r != null && b.text != null && b.text.StartsWith(r.DisplayName, StringComparison.Ordinal) && JourneyInput.IsClickable(b));
            c.Set("rowText", row?.text ?? string.Empty);
            c.Set("uiFail", row != null && JourneyInput.ClickElement(row) ? string.Empty : row == null ? "列表里找不到 ERC-003 蓝图" : JourneyInput.LastUiFailure);
        }

        internal static StepOutcome TickErc003Open(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            BlueprintCircuitBoard b = Panel()?.Board;
            return b != null && b.ChassisId == HomeValleyLayout.Erc003ChassisId
                ? StepOutcome.Done($"打开“{c.Get("rowText")}”（底盘 {b.ChassisId}、主组件 {b.PrimaryId}）")
                : StepOutcome.Retry($"没有打开 ERC-003 蓝图（{UiFail(c)}；当前底盘 {b?.ChassisId}）");
        }

        private static void PickCannon(JourneyContext c)
        {
            DropdownField dd = JourneyInput.FindUitk<DropdownField>("[HomeValleyCircuitBoardHost]", "PrimaryDropdown");
            string name = ComponentCatalog.All[ComponentCatalog.CompCannonId].DisplayName;
            string choice = dd?.choices?.FirstOrDefault(x => x != null && (x == name || x.StartsWith(name, StringComparison.Ordinal)));
            c.Set("cannonChoice", choice ?? string.Empty);
            c.Set("primaryChoices", dd?.choices == null ? string.Empty : string.Join("／", dd.choices));
            if (dd != null && choice != null && JourneyInput.IsClickable(dd))
            {
                // 下拉选择：设值 = 玩家在弹出菜单里点那一项（同一个 ChangeEvent 回调）。弹出菜单的逐项点击不在旅程范围内。
                dd.value = choice;
            }
        }

        private static StepOutcome TickCannonPicked(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            BlueprintCircuitBoard b = Panel()?.Board;
            if (c.Get("cannonChoice").Length == 0)
            {
                return StepOutcome.Fail($"主组件下拉里没有铸造重炮（{c.Get("primaryChoices")}）");
            }
            return b != null && b.PrimaryId == ComponentCatalog.CompCannonId
                ? StepOutcome.Done($"主组件换成“{c.Get("cannonChoice")}”")
                : StepOutcome.Retry($"选了“{c.Get("cannonChoice")}”，主组件仍是 {b?.PrimaryId}（{JourneyInput.FindUitk<Label>("[HomeValleyCircuitBoardHost]", "SaveResultLabel")?.text}）");
        }

        private static void ClickPathSlot(JourneyContext c)
        {
            BlueprintCircuitBoard board = Panel()?.Board;
            int target = -1;
            for (int i = 1; board != null && i < BlueprintCircuitLayout.SlotCount - 1; i++)
            {
                if (string.IsNullOrEmpty(board.SlotContentIds[i]) && board.IsOnSourceSinkPath(i))
                {
                    target = i;
                    break;
                }
            }
            c.SetInt("uplinkSlot", target);
            if (target > 0)
            {
                ClickUi(c, "[HomeValleyCircuitBoardHost]", "Slot" + target);
            }
        }

        private static StepOutcome TickSlotPicked(JourneyContext c)
        {
            int slot = c.GetInt("uplinkSlot", -1);
            if (slot <= 0)
            {
                return StepOutcome.Fail("当前蓝图没有导线经过的空格（按蓝图现找，不写死格号）");
            }
            if (c.StepElapsed < 0.4)
            {
                return StepOutcome.Wait;
            }
            CircuitUplinkView v = Panel()?.UplinkView;
            return v?.ToggleButton != null && v.ToggleButton.text == GameText.Get("circuit.uplink.mark")
                ? StepOutcome.Done($"点选 {slot} 号格，检查器出现“{v.ToggleButton.text}”")
                : StepOutcome.Retry($"点选 {slot} 号格后检查器没有“标为接入口”（{UiFail(c)}）");
        }

        private static StepOutcome TickMarkedEmptyCore(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            int slot = c.GetInt("uplinkSlot");
            CircuitBoardPanelUIToolkit p = Panel();
            CircuitUplinkView v = p?.UplinkView;
            if (p?.Board == null || !p.Board.HasUplink || p.Board.UplinkSlot != slot || !v.SlotShowsUplink(slot))
            {
                return StepOutcome.Retry($"{slot} 号格没有成为接入口（{UiFail(c)}）");
            }
            // 双态预览：信号核还空着——“你接入时”一栏也没有固件，但两栏都在，注解写明信号核是空的。
            bool twoColumns = v.Last != null && v.AiLine(0).Length > 0 && v.UplinkedLine(0).Length > 0;
            if (!twoColumns || v.Last.Uplinked.UplinkFirmwareIds.Length != 0 || v.Last.Ai.UplinkFirmwareIds.Length != 0)
            {
                return StepOutcome.Fail($"信号核空着时双态预览不对：AI“{v.AiLine(0)}”｜接入“{v.UplinkedLine(0)}”");
            }
            return StepOutcome.Done($"{slot} 号格成为接入口（格上菱形图标 +“{v.SlotTagText(slot)}”）；双态预览两栏：AI 驾驶“{v.AiLine(0)}”｜你接入时“{v.UplinkedLine(0)}”；核心行“{v.CoreLineText}”");
        }

        private static StepOutcome TickSaved(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            BlueprintRecord r = Erc003Record();
            BlueprintVersionRecord v = r?.Versions?.FirstOrDefault(x => x.Version == r.ActiveVersion);
            BlueprintCircuitBoard saved = v != null ? BlueprintCircuitBoard.FromVersion(v) : null;
            string label = JourneyInput.FindUitk<Label>("[HomeValleyCircuitBoardHost]", "SaveResultLabel")?.text ?? string.Empty;
            if (saved == null || !saved.HasUplink || saved.UplinkSlot != c.GetInt("uplinkSlot") || v.PrimaryId != ComponentCatalog.CompCannonId)
            {
                return StepOutcome.Retry($"保存后 ERC-003 蓝图的现役版本不是带接入口的重炮版（v{r?.ActiveVersion}：{v?.PrimaryId}，接入口 {saved?.UplinkSlot}；“{label}”）");
            }
            c.SetInt("bpVersion", v.Version);
            return StepOutcome.Done($"保存成功：ERC-003 蓝图现役 v{v.Version}（铸造重炮、{saved.UplinkSlot} 号格接入口，造价 {v.ScrapCost} 废料）；“{label}”");
        }

        internal static StepOutcome TickEditorClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            return !GameRoot.HomeValley.IsCircuitBoardPanelOpen && !PauseMenuUIToolkit.IsOpen
                ? StepOutcome.Done("Esc 关闭蓝图编辑器（暂停菜单没开）")
                : StepOutcome.Retry("Esc 后蓝图编辑器还开着（或弹出了暂停菜单）");
        }

        // ── 第 2 步：信号核 ──────────────────────────────────────────────────────────

        internal static StepOutcome TickCoreOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            return SignalCoreHudUIToolkit.IsOpen && hud != null && hud.PanelVisible
                ? StepOutcome.Done($"信号核面板打开（HUD“{hud.EntryText}”）")
                : StepOutcome.Retry("按信号核键后面板没有打开");
        }

        internal static void PrintOverload(JourneyContext c)
        {
            DropdownField print = JourneyInput.FindUitk<DropdownField>("[SignalCoreHost]", "SignalPrintChoice");
            string overload = GameText.Get("firmware.fw_overload.name");
            string choice = print?.choices?.FirstOrDefault(x => x.Contains(overload));
            c.Set("printChoice", choice ?? string.Empty);
            c.SetInt("scrapBeforePrint", St.Scrap);
            if (print != null && choice != null)
            {
                print.value = choice; // 下拉选择 = 设值（同一 ChangeEvent 回调）
                ClickUi(c, "[SignalCoreHost]", "SignalPrint");
            }
        }

        internal static StepOutcome TickPrinted(JourneyContext c)
        {
            if (c.Get("printChoice").Length == 0)
            {
                return StepOutcome.Fail("刻印下拉里没有过载");
            }
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            PrimitiveChipRecord chip = Campaign.Primitive.PrimitiveInventory.Find(St, hud?.SelectedPartId);
            int cost = SignalCoreService.FirmwareChipPrintScrap;
            return chip != null && chip.CardDefId == FirmwareCatalog.FwOverloadId && chip.State == PrimitiveChipState.Bag && St.Scrap == c.GetInt("scrapBeforePrint") - cost
                ? StepOutcome.Done($"刻印一枚过载芯片（进基元仓、被选中），扣 {cost} 废料")
                : StepOutcome.Retry($"刻印没成功（{hud?.FeedbackText}；{UiFail(c)}）");
        }

        internal static StepOutcome TickEquipped(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            return SignalCoreService.SlotContentId(St, 0) == FirmwareCatalog.FwOverloadId && SignalCoreService.SlotChip(St, 0)?.State == PrimitiveChipState.SignalCore
                ? StepOutcome.Done($"过载装进信号核 1 号槽（{SignalCoreService.SummaryText(St)}）")
                : StepOutcome.Retry($"装入没成功（{SignalCoreHudUIToolkit.Instance?.FeedbackText}；{UiFail(c)}）");
        }

        internal static StepOutcome TickCoreClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            return !SignalCoreHudUIToolkit.IsOpen && !PauseMenuUIToolkit.IsOpen ? StepOutcome.Done("信号核面板关闭") : StepOutcome.Retry("信号核面板还开着");
        }

        private static void ReopenEditor(JourneyContext c)
        {
            ClickUi(c, "[HomeValleyCircuitBoardHost]", "EntryToggleButton", when: () => !GameRoot.HomeValley.IsCircuitBoardPanelOpen);
            c.SetInt("rowClicked", 0);
        }

        /// <summary>FGT-SIG-002 在正式 UI 上：信号核装上过载之后，同一张蓝图的“你接入时”一栏插入过载、编译出熔穿过载；AI 驾驶一栏不变（空接入口、无具名反应）。</summary>
        private static StepOutcome TickPreviewWithOverload(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            CircuitBoardPanelUIToolkit p = Panel();
            if (p?.Board == null || !GameRoot.HomeValley.IsCircuitBoardPanelOpen)
            {
                return StepOutcome.Retry("蓝图编辑器没有打开：" + UiFail(c));
            }
            if (p.Board.ChassisId != HomeValleyLayout.Erc003ChassisId || p.Board.PrimaryId != ComponentCatalog.CompCannonId)
            {
                if (c.GetInt("rowClicked") == 0)
                {
                    c.SetInt("rowClicked", 1);
                    ClickErc003Row(c);
                }
                return StepOutcome.Wait;
            }
            CircuitUplinkView v = p.UplinkView;
            UplinkDualPreview last = v?.Last;
            bool ok = last != null && last.Uplinked.UplinkFirmwareIds.Contains(FirmwareCatalog.FwOverloadId) && last.Ai.UplinkFirmwareIds.Length == 0
                      && last.Uplinked.ReactionId == MechanicalReactionCatalog.ReactionMeltOverloadId && last.Ai.ReactionId != MechanicalReactionCatalog.ReactionMeltOverloadId
                      && v.UplinkedLineHighlighted(0) && v.DiffHighlighted;
            return ok
                ? StepOutcome.Done($"双态预览：AI 驾驶“{v.AiLine(0)}”｜你接入时“{v.UplinkedLine(0)}”（高亮）；差异“{v.DiffText.Replace("\n", " / ")}”")
                : StepOutcome.Fail($"装上过载后双态预览不对：接入固件 [{string.Join(",", last?.Uplinked.UplinkFirmwareIds ?? Array.Empty<string>())}]、反应 {last?.Uplinked.ReactionId}；AI 反应 {last?.Ai.ReactionId}");
        }

        // ── 生产重炮机 ──────────────────────────────────────────────────────────────

        internal static StepOutcome TickFactoryOpen(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            return GameRoot.HomeValley.IsFactoryPanelOpen ? StepOutcome.Done("生产面板打开") : StepOutcome.Retry("点装配站后生产面板没有打开");
        }

        private static void ClickProduce(JourneyContext c)
        {
            c.SetInt("queue0", St.FactoryQueues?.Length ?? 0);
            c.SetInt("erc003Before", MachineRegistry.AllRecords.Count(m => m != null && m.ChassisId == HomeValleyLayout.Erc003ChassisId));
            ClickUi(c, "[HomeValleyFactoryHost]", "ProduceBtn_Erc003");
        }

        private static StepOutcome TickProduceQueued(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            FactoryQueueItemRecord item = St.FactoryQueues?.LastOrDefault(q => q != null && q.Kind == FactoryQueueKind.Produce && q.BlueprintId == HomeValleyLayout.BlueprintErc003Id);
            return item != null && item.BlueprintVersion == c.GetInt("bpVersion")
                ? StepOutcome.Done($"生产队列：ERC-003 v{item.BlueprintVersion}（{item.State}，{item.Duration:F0} 秒）")
                : StepOutcome.Retry($"点“生产”后队列里没有 ERC-003 v{c.GetInt("bpVersion")}（{UiFail(c)}；{JourneyInput.FindUitk<Label>("[HomeValleyFactoryHost]", "ProduceHintLabel")?.text}）");
        }

        internal static StepOutcome TickFactoryClosed(JourneyContext c)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            return !GameRoot.HomeValley.IsFactoryPanelOpen ? StepOutcome.Done("生产面板关闭") : StepOutcome.Retry("生产面板还开着");
        }

        private static StepOutcome TickCannonProduced(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            // 新机出厂后停在装配站出口（IsInFactory，占着出口），等玩家给它第一条命令才驶出。
            MachineRecord cannon = MachineRegistry.AllRecords.FirstOrDefault(m => m != null && m.IsAlive
                && m.ChassisId == HomeValleyLayout.Erc003ChassisId && m.BlueprintId == HomeValleyLayout.BlueprintErc003Id && m.BlueprintVersion == c.GetInt("bpVersion")
                && m.RegionId == HomeValleyLayout.RegionId);
            if (cannon == null)
            {
                FactoryQueueItemRecord item = St.FactoryQueues?.LastOrDefault(q => q != null && q.Kind == FactoryQueueKind.Produce && q.BlueprintId == HomeValleyLayout.BlueprintErc003Id);
                if (item != null && (item.State == FactoryQueueState.WaitingResources || item.State == FactoryQueueState.WaitingPower
                                     || item.State == FactoryQueueState.OutputBlocked) && c.StepElapsed > 20 && c.GetInt("blockLogged") == 0)
                {
                    c.SetInt("blockLogged", 1);
                    c.Log($"生产受阻：{item.State} {item.BlockedReason}（废料 {St.Scrap}）");
                }
                if (item != null && (item.State == FactoryQueueState.Failed || item.State == FactoryQueueState.Cancelled))
                {
                    return StepOutcome.Fail($"重炮机生产 {item.State}：{item.BlockedReason}");
                }
                return StepOutcome.Wait;
            }
            if (!HomeMarker(cannon.LogicId, out _))
            {
                return StepOutcome.Wait; // 表现对象还没建好
            }
            c.SetInt("cannon", cannon.LogicId);
            return StepOutcome.Done($"重炮机 {Label(cannon.LogicId)} 出厂（ERC-003 v{cannon.BlueprintVersion}，{c.StepElapsed:F0} 秒），停在装配站出口（占着出口 = {cannon.IsInFactory}）");
        }

        /// <summary>右键点重炮机旁边的空地：新机接到第一条命令就驶出装配站出口（ReleaseFromFactory），之后才能出征。</summary>
        private static void DriveCannonOut(JourneyContext c)
        {
            int a = c.GetInt("cannon");
            if (!HomeMarker(a, out HomeValleyMachineMarker m))
            {
                return;
            }
            Vector2 goal = m.Position + new Vector2(0f, 5f);
            c.Set("outGoal", goal.x.ToString("R", CultureInfo.InvariantCulture) + "," + goal.y.ToString("R", CultureInfo.InvariantCulture));
            JourneyInput.Click(goal, button: 1);
        }

        private static StepOutcome TickCannonOut(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            int a = c.GetInt("cannon");
            if (!MachineRegistry.TryGetRecord(a, out MachineRecord r))
            {
                return StepOutcome.Fail("重炮机不见了");
            }
            return !r.IsInFactory
                ? StepOutcome.Done($"右键空地：{Label(a)} 接到第一条命令，驶出装配站出口")
                : StepOutcome.Retry($"右键后 {Label(a)} 仍占着装配站出口（{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}）");
        }

        // ── 第 3 步：远征 ──────────────────────────────────────────────────────────

        internal static StepOutcome TickPrepOpen(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            if (!GameRoot.HomeValley.IsExpeditionPrepPanelOpen)
            {
                return StepOutcome.Retry("点信号塔后远征准备面板没有打开");
            }
            ExpeditionDepartureService.PrepSnapshot snap = ExpeditionDepartureService.BuildPrepSnapshot(St);
            return snap.Target == ExpeditionDepartureService.ExpeditionTarget.SilentRuins
                ? StepOutcome.Done($"远征准备面板打开：目标破碎都市，信号带宽 {snap.BandwidthCapacity:F0}")
                : StepOutcome.Fail($"远征目标不是破碎都市（{snap.Target}，{snap.BlockedReason}）");
        }

        /// <summary>像玩家读名单表一样挑人：重炮机必去，再按编号挑可出征的机器，直到人数 / 带宽 / 货位 / 武器都满足（用面板同一个校验）。勾选走行上的勾选框。</summary>
        private static StepOutcome TickRosterPicked(JourneyContext c)
        {
            CampaignState s = St;
            if (c.Get("roster", string.Empty).Length == 0)
            {
                ExpeditionDepartureService.PrepSnapshot snap = ExpeditionDepartureService.BuildPrepSnapshot(s);
                int cannon = c.GetInt("cannon");
                List<ExpeditionDepartureService.MachineIntel> eligible = snap.Machines.Where(m => m.Eligible).OrderBy(m => m.BusyKind.HasValue).ThenBy(m => m.LogicId).ToList();
                List<int> chosen = null;
                foreach (IEnumerable<int> combo in Combos(eligible.Where(m => m.LogicId != cannon).Select(m => m.LogicId).ToList(), ExpeditionDepartureService.MinRosterSize - 1))
                {
                    var ids = new List<int> { cannon };
                    ids.AddRange(combo);
                    if (ExpeditionDepartureService.ValidateRoster(s, ids).Success)
                    {
                        chosen = ids;
                        break;
                    }
                }
                if (chosen == null)
                {
                    return StepOutcome.Fail($"找不到能出发的三人名单（可出征 {eligible.Count} 台；{string.Join("；", ExpeditionDepartureService.ValidateRoster(s, eligible.Take(3).Select(m => m.LogicId).ToList()).BlockingReasons)}）");
                }
                c.Set("roster", string.Join(",", chosen.Select(x => x.ToString(CultureInfo.InvariantCulture))));
                c.SetInt("rosterIdx", 0);
            }
            List<int> roster = Roster(c);
            int idx = c.GetInt("rosterIdx");
            if (idx < roster.Count)
            {
                if (c.StepElapsed < (idx + 1) * 0.6)
                {
                    return StepOutcome.Wait;
                }
                Toggle t = RowToggle(roster[idx]);
                if (t == null)
                {
                    return StepOutcome.Fail($"名单表里找不到 {Label(roster[idx])} 这一行");
                }
                // 这一行不在列表可见区（被滚出去、被下面的汇总文字盖住）：像玩家一样在列表上滚一下滚轮，下一帧再看。
                if (!JourneyInput.ScrollIntoView(JourneyInput.FindUitk<ScrollView>("[HomeValleyExpeditionPrepHost]", "MachineList"), t))
                {
                    c.SetInt("wheel", c.GetInt("wheel") + 1);
                    return c.GetInt("wheel") > 40 ? StepOutcome.Fail($"滚了 {c.GetInt("wheel")} 次滚轮，{Label(roster[idx])} 这一行仍不在列表可见区") : StepOutcome.Wait;
                }
                if (!t.value && !JourneyInput.ClickElement(t))
                {
                    return StepOutcome.Fail($"勾选 {Label(roster[idx])} 失败：{JourneyInput.LastUiFailure}");
                }
                c.SetInt("rosterIdx", idx + 1);
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < (roster.Count + 1) * 0.6)
            {
                return StepOutcome.Wait;
            }
            string summary = JourneyInput.FindUitk<Label>("[HomeValleyExpeditionPrepHost]", "SummaryLabel")?.text ?? string.Empty;
            string reasons = JourneyInput.FindUitk<Label>("[HomeValleyExpeditionPrepHost]", "ReasonsLabel")?.text ?? string.Empty;
            bool allOn = roster.All(id => RowToggle(id)?.value == true);
            return allOn && reasons.Length == 0
                ? StepOutcome.Done($"勾选 {string.Join("、", roster.Select(Label))}：“{summary}”")
                : StepOutcome.Fail($"勾选后名单不对：全部勾上 {allOn}；“{summary}”“{reasons}”");
        }

        internal static IEnumerable<IEnumerable<int>> Combos(List<int> items, int k)
        {
            if (k == 0)
            {
                yield return Enumerable.Empty<int>();
                yield break;
            }
            for (int i = 0; i <= items.Count - k; i++)
            {
                foreach (IEnumerable<int> rest in Combos(items.Skip(i + 1).ToList(), k - 1))
                {
                    yield return new[] { items[i] }.Concat(rest);
                }
            }
        }

        internal static List<int> Roster(JourneyContext c) =>
            c.Get("roster", string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();

        internal static Toggle RowToggle(int logicId)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec))
            {
                return null;
            }
            ScrollView list = JourneyInput.FindUitk<ScrollView>("[HomeValleyExpeditionPrepHost]", "MachineList");
            VisualElement row = list?.Query<VisualElement>("ExpeditionMachineRow").ToList()
                .FirstOrDefault(r => r.resolvedStyle.display != DisplayStyle.None && r.Q<Label>("Number")?.text == "#" + rec.DisplayNumber);
            return row?.Q<Toggle>("Select");
        }

        internal static StepOutcome TickDeparted(JourneyContext c)
        {
            if (FcActive)
            {
                if (c.StepElapsed < 3)
                {
                    return StepOutcome.Wait;
                }
                List<int> roster = Roster(c);
                bool all = roster.All(id => MachineRegistry.TryGetRecord(id, out MachineRecord r) && r.RegionId == FracturedCityLayout.RegionId);
                return all && GameRoot.FracturedCity.Combat != null && GameRoot.FracturedCity.Combat.MachineCount == roster.Count
                    ? StepOutcome.Done($"出发：{roster.Count} 台机器进入破碎都市，镜头飞过去；家园继续运行（家园机器 {GameRoot.HomeValley.LiveMachineCount} 台）")
                    : StepOutcome.Fail($"出发后名单里的机器没有都到破碎都市（内核里 {GameRoot.FracturedCity.Combat?.MachineCount} 台）");
            }
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            // 有在办工作的机器：面板出现“中断确认”，像玩家一样点确认。
            UnityEngine.UIElements.Button confirm = JourneyInput.FindUitk<UnityEngine.UIElements.Button>("[HomeValleyExpeditionPrepHost]", "ConfirmInterruptButton");
            if (confirm != null && JourneyInput.IsClickable(confirm) && c.GetInt("interruptClicked") == 0)
            {
                c.SetInt("interruptClicked", 1);
                c.Log("名单里有机器在办工作：点“确认中断”");
                JourneyInput.ClickElement(confirm);
                return StepOutcome.Wait;
            }
            if (c.StepElapsed > 8)
            {
                string reasons = JourneyInput.FindUitk<Label>("[HomeValleyExpeditionPrepHost]", "ReasonsLabel")?.text ?? string.Empty;
                return StepOutcome.Retry($"点“出发”后没有进入破碎都市（{UiFail(c)}；“{reasons}”）");
            }
            return StepOutcome.Wait;
        }

        // ── 第 4 步：接入重炮机 ────────────────────────────────────────────────────

        /// <summary>命令栏机器列表（候选条）里点一台：按按钮文字“#编号”找到按钮，派发真实指针事件。</summary>
        internal static void ClickMachineList(JourneyContext c, int logicId)
        {
            ScrollView strip = JourneyInput.FindUitk<ScrollView>("[RegionCommandBarHost]", "ControlCandidateStrip");
            if (strip == null || !MachineRegistry.TryGetRecord(logicId, out MachineRecord rec))
            {
                c.Set("uiFail", "命令栏机器列表不存在");
                return;
            }
            UnityEngine.UIElements.Button b = strip.Query<UnityEngine.UIElements.Button>().ToList().FirstOrDefault(x => x.text == "#" + rec.DisplayNumber);
            if (b != null)
            {
                // 不在可见区就滚一下滚轮（布局下一帧才更新：这一帧点不到会如实报“被挡住”，步骤重试时再滚、再点）。
                JourneyInput.ScrollIntoView(strip, b);
            }
            c.SetInt("far0", SignalUplinkService.FarJumpCount);
            c.Set("uiFail", b != null && JourneyInput.ClickElement(b) ? string.Empty : b == null ? $"列表里没有 #{rec.DisplayNumber}" : JourneyInput.LastUiFailure);
        }

        private static StepOutcome TickCannonUplinked(JourneyContext c)
        {
            int a = c.GetInt("cannon");
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点机器列表失败：" + UiFail(c));
            }
            if (SignalPresence.CurrentMachineLogicId != a || GameRoot.FracturedCity.PossessedMachineLogicId != a || WorldView.Director.Mode != ViewMode.Direct)
            {
                return c.StepElapsed < 6 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {Label(a)}（{SignalUplinkService.LastFeedbackText}）");
            }
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait; // 等 0.3 秒形变过渡走完
            }
            CombatSite site = GameRoot.FracturedCity.Combat;
            site.TryGetMachineWeapon(a, out MachineWeaponInfo info);
            bool morphOk = info.Uplinked && info.Morph != MorphMask.None && MachineMorphView.VisibleOf(a) == info.Morph && MachineMorphView.AnimatingCount == 0 && MachineMorphView.SignalBeamShownOf(a);
            bool reactionOk = site.Kernel.TryGetWeapon(info.WeaponIndex, out BinGames.Sim.Combat.CombatWeapon w) && w.Reaction == BinGames.Sim.Combat.CombatReaction.MeltOverload;
            UplinkHudView hud = SignalCoreHudUIToolkit.Instance?.UplinkHud;
            bool hudOk = hud != null && hud.Visible && hud.TitleText.Contains(Label(a)) && hud.SlotText(0).Contains(GameText.Get("firmware.fw_overload.name"));
            if (!morphOk || !reactionOk || !hudOk || SignalUplinkService.FarJumpCount != c.GetInt("far0") + 1)
            {
                JourneyCommon.ResumeIfAutoPaused(c);
                if (c.StepElapsed < 8)
                {
                    return StepOutcome.Wait;
                }
                FcMarker(a, out HomeValleyMachineMarker mk);
                return StepOutcome.Fail($"接入重炮机后状态不对：形变 {MachineMorph.Describe(info.Morph)}（可见 {MachineMorph.Describe(MachineMorphView.VisibleOf(a))}、光柱 {MachineMorphView.SignalBeamShownOf(a)}；" +
                                        $"表现已登记 {MachineMorphView.IsRegistered(a)}、目标 {MachineMorph.Describe(MachineMorphView.TargetOf(a))}、部件 {MachineMorphView.PartCountOf(a)}、过渡中 {MachineMorphView.AnimatingCount}、" +
                                        $"表现对象 {(mk?.View != null)}、暂停 {GameClock.Paused}、观察 {WorldView.ObservedSiteId}）、" +
                                        $"武器反应 {(reactionOk ? "熔穿过载" : "不是熔穿过载")}、HUD {hudOk}、远距离跳转 {SignalUplinkService.FarJumpCount - c.GetInt("far0")} 次");
            }
            c.SetLong("jumpReady", St.SignalCore.JumpCooldownReadyTick);
            return StepOutcome.Done($"跨地点远距离跳转后接入 {Label(a)}：机身形变 {MachineMorph.Describe(info.Morph)}（过渡走完、信号光柱亮）；武器编译出熔穿过载；" +
                                    $"接入 HUD“{hud.TitleText}｜{hud.SlotText(0)}”；跳转冷却 {JourneyCommon.F(SignalUplinkService.JumpCooldownRemaining(St), "0.0")} 秒");
        }

        /// <summary>
        /// 开着重炮机靠近一架侦察机（离它 9 格左右、在干扰场外），鼠标点它开火（重炮先瞄准 1 秒再开火）。直到内核报出熔穿过载（核心固件发动、冷却开始）。
        /// </summary>
        private static StepOutcome TickFireMeltdown(JourneyContext c)
        {
            SampleFrame();
            int a = c.GetInt("cannon");
            if (c.GetInt("fire0Set") == 0)
            {
                c.SetInt("fire0Set", 1);
                c.SetInt("core0", SignalUplinkService.CoreFiredCount);
                c.SetInt("cue0", FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload));
            }
            if (SignalUplinkService.CoreFiredCount > c.GetInt("core0") && FeedbackCues.CountOf(FeedbackCueId.ReactionMeltOverload) > c.GetInt("cue0"))
            {
                JourneyInput.ReleaseKeys();
                double cd = SignalUplinkService.CooldownRemaining(St, FirmwareCatalog.FwOverloadId);
                GameRoot.FracturedCity.Combat.TryGetMachineHeat(a, out float heat, out bool overheated);
                if (cd <= 0 || SignalPresence.CurrentMachineLogicId != a)
                {
                    return StepOutcome.Fail($"熔穿过载打出后冷却没有开始（剩 {cd:F1} 秒）或信号不在重炮机里");
                }
                c.Set("heatA0", heat.ToString("R", CultureInfo.InvariantCulture));
                string cap = FeedbackCues.ActiveCaptions.Where(x => x.Cue == FeedbackCueId.ReactionMeltOverload).Select(x => x.Text).FirstOrDefault() ?? string.Empty;
                return StepOutcome.Done($"{Label(a)} 打出熔穿过载（字幕“{cap}”）：核心固件发动，信号上的冷却 {cd:F1} 秒；重炮机热量 {heat:F0}{(overheated ? "（过热）" : string.Empty)}；开火 {c.GetInt("clicks")} 次点击");
            }
            if (!FcPos(a, out Vector2 me))
            {
                return StepOutcome.Fail("重炮机不见了");
            }
            CombatSite site = GameRoot.FracturedCity.Combat;
            RegionEnemyRecord target = null;
            Vector2 tp = default;
            float best = float.MaxValue;
            foreach (RegionEnemyRecord e in St.RegionEnemies ?? Array.Empty<RegionEnemyRecord>())
            {
                if (e == null || e.RegionId != FracturedCityLayout.RegionId || !site.IsEnemyAlive(e.EnemyInstanceId) || !site.TryGetEnemyPosition(e.EnemyInstanceId, out Vector2 p))
                {
                    continue;
                }
                // 只打干扰场外的敌人（干扰场里接入会断）。
                if (Vector2.Distance(p, FracturedCityLayout.ListeningNode.Position) <= FracturedCityLayout.JammerRadius + 2f)
                {
                    continue;
                }
                float d = Vector2.Distance(me, p);
                if (d < best)
                {
                    best = d;
                    target = e;
                    tp = p;
                }
            }
            if (target == null)
            {
                return StepOutcome.Fail("破碎都市里找不到干扰场外活着的敌人");
            }
            double now = c.StepElapsed;
            if (JourneyInput.Holding || now - c.GetLong("fireAtMs") / 1000.0 < 1.6)
            {
                return StepOutcome.Wait;
            }
            if (best > FracturedCityLayout.DirectAttackRange - 2.5f)
            {
                // 还太远：朝敌人方向开一小段（按住 0.35 秒，每次重新判断方向）。
                Vector2 goal = tp + (me - tp).normalized * 8.5f;
                if (Vector2.Distance(goal, FracturedCityLayout.ListeningNode.Position) < FracturedCityLayout.JammerRadius + 1.5f)
                {
                    goal = tp + new Vector2(0f, -8.5f);
                }
                DriveToward(me, goal, 0.35);
                return StepOutcome.Wait;
            }
            if (!JourneyInput.OnScreen(tp, 0.03f))
            {
                return StepOutcome.Fail($"侦察机 {target.EnemyInstanceId} 在射程内却不在画面里（镜头直控跟随）");
            }
            c.SetLong("fireAtMs", (long)(now * 1000));
            c.SetInt("clicks", c.GetInt("clicks") + 1);
            JourneyInput.Click(tp); // 直控左键：朝鼠标方向开火（内核：瞄准 1 秒后开火）
            return StepOutcome.Wait;
        }

        /// <summary>
        /// 接入时按方向键（按当前绑定）朝 <paramref name="goal"/> 开一小段：按目标方向落在八个方向里最近的那个选键（偏离不超过 22.5°），
        /// 贴着地形边走不动时（<paramref name="sidestep"/> 非 0）改按侧向的那个键绕一下。
        /// </summary>
        internal static void DriveToward(Vector2 me, Vector2 goal, double seconds, int sidestep = 0)
        {
            Vector2 d = goal - me;
            if (d.sqrMagnitude < 0.01f)
            {
                return;
            }
            Vector2 n = d.normalized;
            if (sidestep != 0)
            {
                n = new Vector2(-n.y, n.x) * sidestep; // 左 / 右转 90° 绕开
            }
            const float axis = 0.38f; // sin 22.5°
            var keys = new List<KeyCode>(2);
            if (n.x > axis)
            {
                keys.Add(Settings.GameSettings.KeyBindings.GetKey(GameActionId.MoveRight));
            }
            else if (n.x < -axis)
            {
                keys.Add(Settings.GameSettings.KeyBindings.GetKey(GameActionId.MoveLeft));
            }
            if (n.y > axis)
            {
                keys.Add(Settings.GameSettings.KeyBindings.GetKey(GameActionId.MoveForward));
            }
            else if (n.y < -axis)
            {
                keys.Add(Settings.GameSettings.KeyBindings.GetKey(GameActionId.MoveBack));
            }
            if (keys.Count > 0)
            {
                JourneyInput.HoldKeys(keys, seconds);
            }
        }

        // ── 第 5 步：切机 ────────────────────────────────────────────────────────────

        private static void PressSwitch(JourneyContext c)
        {
            if (SignalPresence.CurrentMachineLogicId != c.GetInt("cannon"))
            {
                return; // 已经切走了（上一次按键延迟生效）：不再按。
            }
            c.Set("cdBefore", SignalUplinkService.CooldownRemaining(St, FirmwareCatalog.FwOverloadId).ToString("R", CultureInfo.InvariantCulture));
            c.SetLong("cdReadyTick", CooldownTick());
            JourneyInput.PressAction(GameActionId.CycleControlTarget);
        }

        private static long CooldownTick() =>
            St?.SignalCore?.CoreCooldowns?.FirstOrDefault(x => x != null && x.ContentId == FirmwareCatalog.FwOverloadId)?.ReadyTick ?? 0;

        /// <summary>FGT-SIG-004：冷却跟着信号（到期步不变、剩余时间继续走，不清零也不重新计满），热量留在机体（重炮机的热量还在它身上，新机器没有继承）。</summary>
        private static StepOutcome TickSwitched(JourneyContext c)
        {
            if (c.StepElapsed < 1.2)
            {
                return StepOutcome.Wait;
            }
            int a = c.GetInt("cannon");
            int now = SignalPresence.CurrentMachineLogicId;
            if (now == 0 || now == a || GameRoot.FracturedCity.PossessedMachineLogicId != now)
            {
                return StepOutcome.Retry($"按 Tab 后信号没有切到别的机器（{SignalUplinkService.LastFeedbackText}）");
            }
            c.SetInt("other", now);
            double before = double.Parse(c.Get("cdBefore"), CultureInfo.InvariantCulture);
            double after = SignalUplinkService.CooldownRemaining(St, FirmwareCatalog.FwOverloadId);
            long tick = CooldownTick();
            CombatSite site = GameRoot.FracturedCity.Combat;
            site.TryGetMachineHeat(a, out float heatA, out _);
            site.TryGetMachineHeat(now, out float heatB, out _);
            site.TryGetMachineWeapon(a, out MachineWeaponInfo infoA);
            bool cdOk = tick == c.GetLong("cdReadyTick") && after > 0 && after < before;
            bool heatOk = heatA > 0f && heatB < heatA;
            bool aiOk = !infoA.Uplinked && site.Kernel.TryGetWeapon(infoA.WeaponIndex, out BinGames.Sim.Combat.CombatWeapon w) && w.Reaction == BinGames.Sim.Combat.CombatReaction.None;
            if (!cdOk || !heatOk || !aiOk)
            {
                return StepOutcome.Fail($"切机后：冷却 {before:F2} → {after:F2} 秒（到期步 {c.GetLong("cdReadyTick")} → {tick}）、热量 {Label(a)} {heatA:F0} / {Label(now)} {heatB:F0}、" +
                                        $"离开的重炮机回到 AI 配置 {aiOk}");
            }
            c.SetInt("slot", CampaignSession.ActiveSlotIndex);
            return StepOutcome.Done($"Tab：信号从 {Label(a)} 切到 {Label(now)}；过载冷却 {before:F2} → {after:F2} 秒（到期步不变 = 冷却跟着信号，没有重置）；" +
                                    $"热量留在机体：{Label(a)} {heatA:F0}、{Label(now)} {heatB:F0}；离开的重炮机回到 AI 配置（武器无具名反应）");
        }

        // ── 接入中存档 → 读取 ────────────────────────────────────────────────────────

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
            // 暂停菜单打开 = 世界暂停：这一刻的“接入状态”就是即将写进存档的状态。
            CampaignState s = St;
            int b = c.GetInt("other");
            if (s.SignalCore.UplinkMachineLogicId != b)
            {
                return StepOutcome.Fail($"存档前信号不在 {Label(b)} 里");
            }
            GameRoot.FracturedCity.Combat.TryGetMachineHeat(c.GetInt("cannon"), out float heatA, out _);
            c.Set("pre", SignalDigest(s, c.GetInt("cannon"), heatA));
            c.SetLong("preTicks", GameClock.Ticks);
            return StepOutcome.Done($"暂停菜单打开（世界暂停），信号在 {Label(b)} 里；存档前：{c.Get("pre")}");
        }

        /// <summary>信号相关状态的逐字段摘要（存读档对照用；全部是整数或原样字符串，没有浮点冷却）。</summary>
        private static string SignalDigest(CampaignState s, int cannon, float heatA)
        {
            SignalCoreState core = s.SignalCore;
            string cds = string.Join(";", (core.CoreCooldowns ?? Array.Empty<SignalCoreCooldownRecord>()).Where(x => x != null)
                .Select(x => x.ContentId + "@" + x.ReadyTick.ToString(CultureInfo.InvariantCulture)));
            return $"信号={core.UplinkMachineLogicId}@{core.UplinkSiteId}｜槽位={string.Join(",", core.SlotPartIds ?? Array.Empty<string>())}｜冷却={cds}" +
                   $"｜跳转冷却={core.JumpCooldownReadyTick.ToString(CultureInfo.InvariantCulture)}｜最近={string.Join(",", core.RecentUplinks ?? Array.Empty<int>())}" +
                   $"｜安全模式={string.Join(",", (core.SafeModes ?? Array.Empty<SignalSafeModeRecord>()).Select(m => m.LogicId + ":" + m.Reason))}" +
                   $"｜重炮热量={heatA.ToString("R", CultureInfo.InvariantCulture)}";
        }

        internal static StepOutcome TickSaveConfirm(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            if (UiConfirmDialog.IsOpen)
            {
                if (c.GetInt("okClicked") == 0)
                {
                    c.SetInt("okClicked", 1);
                    if (!JourneyInput.ClickUitk("[UiKitOverlayHost]", "ConfirmOk"))
                    {
                        return StepOutcome.Fail("确认框点“确认”失败：" + JourneyInput.LastUiFailure);
                    }
                }
                return StepOutcome.Wait;
            }
            if (c.GetInt("okClicked") == 1)
            {
                return StepOutcome.Done("二次确认后保存到当前槽位，经唯一回菜单出口返回主菜单");
            }
            return c.StepElapsed > 5 ? StepOutcome.Fail("点“保存并返回主菜单”后没有弹出二次确认：" + UiFail(c)) : StepOutcome.Wait;
        }

        private static StepOutcome TickMenuAfterSave(JourneyContext c)
        {
            if (JourneyInput.FindActiveButton("m_btn_Load") == null || c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            int slot = c.GetInt("slot");
            LoadResult onDisk = CampaignSaveService.Load(slot);
            if (!onDisk.Success)
            {
                return StepOutcome.Fail($"刚保存的存档读不出来：{onDisk.Outcome}/{onDisk.Reason}");
            }
            CampaignState s = onDisk.State;
            MachineRecord a = s.MachineRecords?.FirstOrDefault(m => m.LogicId == c.GetInt("cannon"));
            string digest = SignalDigest(s, c.GetInt("cannon"), a?.WeaponHeat ?? -1f);
            if (digest != c.Get("pre") || s.Clock.Ticks != c.GetLong("preTicks"))
            {
                return StepOutcome.Fail($"存档与存档那一刻的状态不一致：\n      存档前 {c.Get("pre")}（第 {c.GetLong("preTicks")} 步）\n      存档里 {digest}（第 {s.Clock.Ticks} 步）");
            }
            c.Set("disk", digest);
            return StepOutcome.Done($"回到主菜单；存档（槽位 {slot + 1}）里信号在 {Label(c.GetInt("other"))}、固件 / 冷却到期步 / 跳转冷却 / 重炮热量与存档那一刻逐字段一致");
        }

        internal static StepOutcome TickLoadSlot(JourneyContext c)
        {
            if (c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            if (c.GetInt("slotClicked2") == 1)
            {
                return StepOutcome.Done("点了存档槽的“读取”");
            }
            int slot = c.GetInt("slot");
            UnityEngine.UI.Button action = JourneyInput.FindActiveButton($"m_btn_Slot{slot}Action");
            if (action == null)
            {
                return c.StepElapsed > 8 ? StepOutcome.Fail($"找不到存档槽 {slot + 1} 的“读取”按钮（{UiFail(c)}）") : StepOutcome.Wait;
            }
            if (!JourneyInput.ClickUgui(action))
            {
                return StepOutcome.Fail("点存档槽失败：" + JourneyInput.LastUiFailure);
            }
            c.SetInt("slotClicked2", 1);
            return StepOutcome.Wait;
        }

        private static StepOutcome TickLoadedUplinked(JourneyContext c)
        {
            if (!FcActive)
            {
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            int b = c.GetInt("other");
            SignalCoreHudUIToolkit hud = SignalCoreHudUIToolkit.Instance;
            if (SignalPresence.CurrentMachineLogicId != b || GameRoot.FracturedCity.PossessedMachineLogicId != b || !WorldView.Director.HeadingDirect)
            {
                return StepOutcome.Fail($"读档后信号不在 {Label(b)} 里（当前 {SignalPresence.CurrentMachineLogicId}，接管 {GameRoot.FracturedCity.PossessedMachineLogicId}）");
            }
            CampaignState s = St;
            GameRoot.FracturedCity.Combat.TryGetMachineHeat(c.GetInt("cannon"), out float heatA, out _);
            string disk = c.Get("disk");
            string live = SignalDigest(s, c.GetInt("cannon"), heatA);
            // 读档后世界已经恢复运行几步：冷却 / 跳转冷却是“到期步”（绝对值）必须完全相同；热量只会继续散热（不会变多）。
            string strip(string d) => d.Substring(0, d.IndexOf("｜重炮热量=", StringComparison.Ordinal));
            float savedHeat = float.Parse(disk.Substring(disk.IndexOf("｜重炮热量=", StringComparison.Ordinal) + 6), CultureInfo.InvariantCulture);
            if (strip(disk) != strip(live) || heatA > savedHeat + 1e-3f)
            {
                return StepOutcome.Fail($"读档后信号状态不一致：\n      存档 {disk}\n      读档 {live}");
            }
            return StepOutcome.Done($"读档进入破碎都市：信号仍在 {Label(b)} 里（接管恢复、镜头直控；HUD“{hud?.LocationText}”）；固件、冷却到期步、跳转冷却与存档完全一致；" +
                                    $"重炮热量 {savedHeat:F1} → {heatA:F1}（读档后继续散热）");
        }

        // ── 第 6 步：干扰断链 → 安全模式 → 恢复 ──────────────────────────────────────────

        private static StepOutcome TickDriveIntoJam(JourneyContext c)
        {
            SampleFrame();
            int b = c.GetInt("other");
            CampaignState s = St;
            if (SignalLinkService.IsInSafeMode(s, b))
            {
                JourneyInput.ReleaseKeys();
                // 断链那一刻镜头开始拉回战略、头顶图标下一帧才挂上：等 1.5 秒再核对。
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
                bool badge = SignalLinkView.BadgeWanted(b);
                string feedback = SignalUplinkService.LastFeedbackText;
                string reason = SignalLinkService.ReasonName(SignalLinkBreakReason.Jammed);
                bool ok = rec.Reason == (int)SignalLinkBreakReason.Jammed && SignalPresence.AtCore && WorldView.Director.Mode == ViewMode.Strategy && badge
                          && c.GetInt("warned") == 1;
                FcPos(b, out Vector2 at);
                return ok
                    ? StepOutcome.Done($"{Label(b)} 驶入干扰场（离监听节点 {Vector2.Distance(at, FracturedCityLayout.ListeningNode.Position):F1} 格）：先出宽限预警，宽限后断链——信号弹回归还核心、镜头回战略；" +
                                       $"{Label(b)} 进入安全模式（原因：{reason}，头顶安全模式图标）；反馈“{feedback}”")
                    : StepOutcome.Fail($"断链后状态不对：原因 {rec.Reason}、信号在核心 {SignalPresence.AtCore}、镜头 {WorldView.Director.Mode}、图标 {badge}、见过宽限预警 {c.GetInt("warned")}");
            }
            if (!SignalPresence.AtCore && SignalPresence.CurrentMachineLogicId == b && MachineRegistry.TryGetRecord(b, out MachineRecord r0) && r0 != null
                && SignalLinkService.CauseAt(s, r0) == SignalLinkBreakReason.Jammed)
            {
                if (c.GetInt("warned") == 0)
                {
                    c.SetInt("warned", 1);
                    c.Log($"进入干扰场：宽限预警“{SignalCoreHudUIToolkit.Instance?.UplinkStatusText}”");
                }
            }
            if (SignalPresence.CurrentMachineLogicId != b)
            {
                return StepOutcome.Fail($"驶入干扰场前信号已不在 {Label(b)} 里（{SignalUplinkService.LastFeedbackText}）");
            }
            if (JourneyInput.Holding)
            {
                return StepOutcome.Wait;
            }
            if (FcPos(b, out Vector2 me))
            {
                DriveToward(me, JamProbe, 0.4);
            }
            return StepOutcome.Wait;
        }

        internal static void ClickFcMachine(JourneyContext c, int logicId)
        {
            if (FcMarker(logicId, out HomeValleyMachineMarker m) && m.View != null)
            {
                JourneyInput.ClickWorld(m.View.transform.position);
            }
        }

        private static StepOutcome TickFcSelected(JourneyContext c, int logicId)
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            IReadOnlyList<int> sel = GameRoot.FracturedCity.SquadCommands.Selection;
            return sel.Contains(logicId) ? StepOutcome.Done($"左键选中 {Label(logicId)}（安全模式中）") : StepOutcome.Retry($"左键后选中 [{string.Join(",", sel)}]");
        }

        private static void RightClickFcGround(Vector2 at) => JourneyInput.Click(at, button: 1);

        private static StepOutcome TickSafeModeRecovered(JourneyContext c)
        {
            SampleFrame();
            int b = c.GetInt("other");
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            CombatSite site = GameRoot.FracturedCity.Combat;
            if (c.GetInt("cmdChecked") == 0)
            {
                c.SetInt("cmdChecked", 1);
                if (!site.TryGetMachineUnit(b, out int unit) || !site.TryGetCommand(unit, out BinGames.Sim.Combat.CombatCommand cmd) || cmd.Kind != BinGames.Sim.Combat.CombatCommandKind.Move)
                {
                    return StepOutcome.Fail($"右键地面后安全模式的 {Label(b)} 没有接到移动命令（{GameRoot.FracturedCity.SquadCommands.RecentEvents.LastOrDefault()}）");
                }
                c.Log($"{Label(b)}（安全模式）接到移动命令，驶向撤离点");
            }
            if (SignalLinkService.IsInSafeMode(St, b))
            {
                return StepOutcome.Wait;
            }
            FcPos(b, out Vector2 at);
            return StepOutcome.Done($"{Label(b)} 驶出干扰场（离监听节点 {Vector2.Distance(at, FracturedCityLayout.ListeningNode.Position):F1} 格），条件消失 2 游戏秒后自动退出安全模式（{c.StepElapsed:F1} 秒）");
        }

        // ── 第 7、8 步：跳回家园、跳回远征队 ────────────────────────────────────────────

        private static StepOutcome TickJumpedHome(JourneyContext c)
        {
            if (c.StepElapsed < 1.2)
            {
                return StepOutcome.Wait;
            }
            if (c.GetInt("home0Set") == 0)
            {
                c.SetInt("home0Set", 1);
            }
            bool home = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive && SignalPresence.AtCore && WorldView.Director.Mode == ViewMode.Strategy;
            return home
                ? StepOutcome.Done($"按跳回家园键：镜头回到家园、信号在归还核心（“{SignalUplinkService.LastFeedbackText}”）；远征队留在破碎都市继续运行（{GameRoot.FracturedCity?.LiveMachineCount} 台）")
                : StepOutcome.Retry($"按跳回家园键后镜头没有回家园（{SignalUplinkService.LastFeedbackText}）");
        }

        private static void PressJumpBack(JourneyContext c)
        {
            c.SetInt("far1", SignalUplinkService.FarJumpCount);
            if (SignalPresence.CurrentMachineLogicId == 0)
            {
                JourneyInput.PressAction(GameActionId.JumpPreviousMachine);
            }
        }

        private static StepOutcome TickJumpedBack(JourneyContext c)
        {
            int b = c.GetInt("other");
            if (c.StepElapsed < 3)
            {
                return StepOutcome.Wait;
            }
            if (SignalPresence.CurrentMachineLogicId != b || !FcActive || WorldView.Director.Mode != ViewMode.Direct)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Retry($"按跳回上一台键后没有回到 {Label(b)}（{SignalUplinkService.LastFeedbackText}）");
            }
            return SignalUplinkService.FarJumpCount == c.GetInt("far1") + 1
                ? StepOutcome.Done($"按跳回上一台键：从家园跨地点远距离跳转（1.5 秒过渡）回到破碎都市的 {Label(b)}，镜头直控")
                : StepOutcome.Fail("回到了远征队，但没有记一次远距离跳转");
        }

        // ── 第 9 步：返回家园 ──────────────────────────────────────────────────────────

        internal static StepOutcome TickEvacPanel(JourneyContext c)
        {
            int b = c.GetInt("other");
            if (GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsEvacPanelOpen)
            {
                JourneyInput.ReleaseKeys();
                return StepOutcome.Done("按住交互键 1 秒：撤离清单打开");
            }
            if (JourneyInput.Holding)
            {
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            FcPos(b, out Vector2 at);
            float d = Vector2.Distance(at, EvacPoint);
            if (d > FracturedCityController.InteractRange - 0.5f)
            {
                // 还没到撤离点：开过去再按。
                DriveToward(at, EvacPoint, 0.4);
                return StepOutcome.Retry($"{Label(b)} 离撤离点 {d:F1} 格，先开过去");
            }
            return StepOutcome.Retry($"在撤离点按住交互键后撤离清单没有打开（离撤离点 {d:F1} 格；{GameRoot.FracturedCity?.Interact?.GetType().Name}）");
        }

        internal static StepOutcome TickReturnedHome(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            if (GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsLoaded)
            {
                return c.StepElapsed > 10 ? StepOutcome.Retry("点“确认撤离”后破碎都市还在（" + UiFail(c) + "）") : StepOutcome.Wait;
            }
            if (c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            List<int> roster = Roster(c);
            List<string> bad = roster.Where(id => !MachineRegistry.TryGetRecord(id, out MachineRecord r) || !r.IsAlive || r.RegionId != HomeValleyLayout.RegionId)
                .Select(Label).ToList();
            bool homeView = GameRoot.HomeValley != null && GameRoot.HomeValley.IsActive && SignalPresence.AtCore;
            if (bad.Count > 0 || !homeView)
            {
                return StepOutcome.Fail($"撤离后：没回家园的机器 [{string.Join("、", bad)}]；镜头在家园且信号在核心 {homeView}");
            }
            MachineRegistry.TryGetRecord(c.GetInt("other"), out MachineRecord br);
            return StepOutcome.Done($"远征队 {roster.Count} 台全部返回家园；信号在归还核心、镜头回家园；{Label(c.GetInt("other"))} 的机器经历：" +
                                    $"{MachineSignalExperience.Describe(St, br)}；{FrameReport()}");
        }

        // ── 帧耗时（参照口径，见证据：Editor batchmode 无图形设备）──────────────────────────

        private static void BeginSampling()
        {
            FrameMs.Clear();
            _lastFrame = -1;
            _sampling = true;
        }

        private static void SampleFrame()
        {
            if (!_sampling || Time.frameCount == _lastFrame)
            {
                return;
            }
            _lastFrame = Time.frameCount;
            FrameMs.Add(Time.unscaledDeltaTime * 1000f);
        }

        private static string FrameReport()
        {
            if (FrameMs.Count < 10)
            {
                return "帧耗时采样不足";
            }
            List<float> sorted = FrameMs.OrderBy(x => x).ToList();
            float p50 = sorted[sorted.Count / 2];
            float p95 = sorted[(int)(sorted.Count * 0.95)];
            float max = sorted[sorted.Count - 1];
            return $"接入 / 驾驶 / 战斗段帧耗时（Editor batchmode 无图形设备，{sorted.Count} 帧）p50 {p50:F2} ms、p95 {p95:F2} ms、最大 {max:F1} ms（120 帧预算 8.33 ms 仅作参照）";
        }

        // ── 收尾 ────────────────────────────────────────────────────────────────────

        private static void Cleanup(JourneyContext c, bool pass)
        {
            c.Log(JourneyCommon.UiStats());
            if (FrameMs.Count >= 10)
            {
                c.Log(FrameReport());
            }
            ResetStatics();
            JourneyCommon.Cleanup(c);
        }
    }
}
