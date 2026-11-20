using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.WorldSim;
using GameLogic.Campaign.Endgame;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;
using Mega = GameLogic.EditorTools.FgMegastructureSelfCheck;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG14-E2E-01：M14 出口旅程 FGJ-M14（里程碑文档 FG-M14：“通关存档选择继续 → 撑过两次周期清洗 → 回主菜单用自定义种子开一局关闭突袭的沙盒，确认没有突袭”）。
    /// 从主菜单“新建”出发（固定测试种子 42、标准难度），全程正式输入：左键选择 / Shift 加选、右键地面 = 移动，按钮 / 快捷键只切状态；等待段用正式 3 倍速。
    ///
    /// 轻量口径 a：每个新核心系统至少被真实输入触发一次并断言结果——
    /// 通关存档（结局自动存档）→ 结算点“返回主菜单”→ 主菜单“读取”该槽 → 结算重新打开（读档回到结算，FG13-END-06）→ 点“继续”进入后日谈（FGR-SBX-001）→ 后日谈说明自动打开、点“知道了”→
    /// 第 1 / 2 级周期清洗按 7 日节拍交给突袭导演（FGR-SBX-002，预算逐级上升）→ 作战队逐台右键散开、守卫，撑过两次 → 暂停菜单“坚守记录”两行“撑过”（FGR-SBX-003）→
    /// “保存并返回主菜单”，存档卡写“后日谈 · 坚守 N 天 · 最高 2 级”→ 主菜单“沙盒模式”（FGR-SBX-010）→ 种子框输入自定义种子、突袭点“关闭”、点“开始”（FGR-SBX-011 / FGR-GEN-070）→
    /// 沙盒档种子 = 输入值、突袭关闭；正式触发入口（剧情报复 / 净化塔 / 巨构 / 暴露 100）与三个游戏日过去都没有一波突袭 → 暂停菜单“沙盒设置”打开。
    ///
    /// 夹具（都写在步骤标题与报告里；被测行为全走正式系统）：
    /// DEBT-FG14E2E01-01 通关存档：第三幕、终局链（揭示 / 三波清洗 / 滩头 / 先遣旗舰）记为已完成并写结局账本，经正式结局自动存档入口（EndingService.EndingSaveHandler）落盘
    /// （从主菜单打到结局由 FGJ-M13 覆盖）；核心旁 36 台 ERC-003 作战队（重炮 / 机枪各半，耐久 5000）、两座维修无人机站 + 维修件（同 FGJ-M13 的 DEBT-FG13E2E01-01）；事件频率“关闭”（事件由 FGJ-M12 覆盖）。
    /// DEBT-FG14E2E01-02 时间夹具：两次周期清洗之间的 7 个游戏日（3 倍速下每次约 47 分钟，两次超过轻量口径 30 分钟上限）用 GameClock.SkipForTests 跳到发动前 30 游戏秒
    /// （与 FgPostgameSelfCheck 同一做法；7 日节拍本身由 FgPostgameSelfCheck B2 断言“第 504002 / 1008002 步准时”），之后的集结 / 预警 / 来袭 / 交战全部按 3 倍速真实运行；
    /// 沙盒“三个游戏日”同样每日跳到日末前 20 游戏秒、再真实运行 20 游戏秒。
    /// 用法：bash tools/unity-journey.sh FGJ-M14；断点见 <see cref="Checkpoints"/>（--from 续跑）。
    /// </summary>
    public static class FgjM14Journey
    {
        public const string Id = "FGJ-M14";

        public const int TestSeed = FgjM0Journey.TestSeed;

        /// <summary>沙盒的自定义种子（玩家在种子框里输入的值，与固定测试种子 42 不同；选有生成器回归基准的旅程种子 1，“种子”一步才能逐块比对基准；B25：内容按种子生成，不依赖坐标）。</summary>
        public const int SandboxSeed = 1;

        private const int SquadSize = 36;
        private const float SquadHp = 5000f;

        private static readonly string[] Checkpoints = { "pg_intro", "p1_done", "p2_done" };

        private static CampaignState St => CampaignSession.Current;

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

                // ── 1. 通关存档 → 继续 ──
                S("e_fix", $"通关存档夹具（DEBT-FG14E2E01-01）：第三幕、终局链（揭示 / 三波清洗 / 滩头 / 先遣旗舰）记为已完成（从主菜单打到结局由 FGJ-M13 覆盖）；事件频率“关闭”（FGJ-M12 覆盖）；" +
                              $"核心旁 {SquadSize} 台 ERC-003 作战队（重炮 / 机枪各半，耐久 {SquadHp:0}）、两座维修无人机站 + 维修件（FGJ-M6 覆盖）", 40, ApplyFixture, TickFixture),
                S("e_save", "结局落盘：写结局账本 → 正式结局自动存档入口（EndingService.EndingSaveHandler）写进当前槽 → 结算面板自动打开、世界暂停、存档被拦", 30, ApplyEnding, TickEndingSaved),
                S("e_menu", "结算点“返回主菜单”（不存档，结局已落盘）→ 回到主菜单", 60, c => FgjM3Common.Once(c, "esmenu", () => JourneyInput.ClickUitk("[EndingSettlementHost]", "EsMenu")), TickMenuShown),
                S("e_load", "主菜单点“读取”，点通关存档的槽位", 20, c => FgjM1Journey.ClickUgui(c, "m_btn_Load"), FgjM1Journey.TickLoadSlot),
                S("e_loaded", "读档进入归还谷地：结局已达成、还没选“继续”→ 结算重新打开（统计 / 幸存机器 / 纪念墙）", 120, null, TickLoadedSettlement),
                S("e_continue", "结算点“继续”：进入后日谈、结算关闭、存档允许", 30, null, FgjM13Journey.TickContinue, retries: 2),
                S("pg_intro", "后日谈说明自动打开（7 日一次、上限 200 台、核心被毁即坚守结束）；顶栏“后日谈 · 第 1 级清洗 7 日后”；点“知道了”", 30, null, TickIntro, retries: 1),
                S("speed3b", "读档后按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
            };
            for (int w = 1; w <= 2; w++)
            {
                steps.AddRange(PurgeSteps(w));
            }
            steps.AddRange(new[]
            {
                S("pg_stats", "坚守统计：撑过 2 次、最高等级 2、坚守天数 ≥ 14、第 2 级预算高于第 1 级（等级递增）", 10, null, TickStats),
                S("rec_esc", "按 Esc 打开暂停菜单：后日谈里出现“坚守记录”", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseOpen(true), retries: 1),
                S("rec_open", "点“坚守记录”：坚守天数 / 撑过次数 / 最佳单次防守 / 累计，两行清洗历史都写“撑过”", 20, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PausePostgame"), TickRecord, retries: 1),
                S("rec_close", "点“关闭”回到暂停菜单", 10, c => FgjM1Journey.ClickUi(c, "[PostgameHost]", "PostgameClose"),
                    c => c.StepElapsed < 0.5 ? StepOutcome.Wait : !PostgamePanelUIToolkit.IsOpen && PauseMenuUIToolkit.IsOpen ? StepOutcome.Done("坚守记录关闭，暂停菜单仍开着") : StepOutcome.Retry("点“关闭”后坚守记录没关：" + FgjM1Journey.UiFail(c)), retries: 1),
                S("save_quit", "点“保存并返回主菜单”，确认", 15, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSaveQuit"), FgjM1Journey.TickSaveConfirm),
                S("menu_card", "回到主菜单：该槽存档卡写“后日谈 · 坚守 N 天 · 最高 2 级”", 60, null, TickCard),

                // ── 3. 自定义沙盒（关闭突袭）──
                S("sb_open", "主菜单点“沙盒模式”：新游戏面板以沙盒模式打开（沙盒设置段可见、开局前说明）", 30, c => FgjM1Journey.ClickUgui(c, "m_btn_Sandbox"), TickSandboxOpen, retries: 1),
                S("sb_setup", $"沙盒设置：点种子框输入自定义种子 {SandboxSeed}、突袭点“关闭”，点“开始”", 30, null, TickSandboxSetup, retries: 1),
                S("sb_enter", "进入沙盒世界", 150, null, JourneyCommon.TickNewGame),
                S("sb_check", "沙盒档：种子 = 输入值、生成结果与该种子的基准一致、突袭关闭、剧情关闭", 30, null, TickSandboxState),
                S("sb_speed3", "按 3 倍速键", 10, c => FgjM1Journey.PressIf(GameActionId.SpeedTriple, !Mathf.Approximately(GameClock.Speed, 3f)), FgjM1Journey.TickSpeed3, retries: 1),
                S("sb_noraid", "确认没有突袭：正式触发入口（剧情报复 / 净化塔 / 巨构完工 / 信号暴露 100）全被丢掉，三个游戏日过去、静默夜照常开始（时间夹具 DEBT-FG14E2E01-02）都没有一波：计划 0、突袭 0", 240, ApplyRaidTriggers, TickNoRaid),
                S("sb_esc", "按 Esc 打开暂停菜单：沙盒里出现“沙盒设置”", 10, c => FgjM1Journey.PressIf(GameActionId.Cancel, !PauseMenuUIToolkit.IsOpen), TickPauseOpen(false), retries: 1),
                S("sb_panel", "点“沙盒设置”：面板打开，突袭写“关闭”", 20, c => FgjM1Journey.ClickUi(c, "[PauseMenuHost]", "PauseSandbox"), TickSandboxPanel, retries: 1),
            });
            return new JourneyDef
            {
                Id = Id,
                Title = "M14 出口：通关存档读档 → 结算点“继续”进入后日谈 → 撑过两次周期清洗 → 坚守记录 → 回主菜单开自定义种子、关闭突袭的沙盒，确认没有突袭",
                Seed = TestSeed,
                TotalTimeoutSeconds = 2400,
                OnFinish = Cleanup,
                Version = 1,
                CheckpointAfter = Checkpoints,
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static string F(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);

        private static string Label(int id) => FgjM1Journey.Label(id);

        private static List<int> Team(JourneyContext c) => FgjM1Journey.Roster(c);

        private static bool Alive(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) && m != null && m.IsAlive;

        private static List<int> FreeTeam(JourneyContext c) => Team(c).Where(Alive).ToList();

        /// <summary>顶栏“轨道威胁”行玩家看到的文字（隐藏 = 空；隐藏时控件里留着上一局的旧文字，不算）。</summary>
        private static string OrbitalHud()
        {
            Label l = JourneyInput.FindUitk<Label>("[WorldBarHost]", "WorldOrbitalThreat");
            return l == null || l.ClassListContains("uk-hidden") ? string.Empty : l.text ?? string.Empty;
        }

        private static string Cell(Vector2 p) => $"({F(p.x, "0")},{F(p.y, "0")})";

        private static string TeamHp(JourneyContext c) => string.Join("、", Team(c).Select(id => MachineRegistry.TryGetRecord(id, out MachineRecord m)
            ? $"{Label(id)} {(m.IsAlive ? F(m.Health, "0") : "阵亡")}" : $"{id}?"));

        private static int LivePlans(CampaignState s) =>
            RaidDirectorService.StateOf(s)?.Plans.Count(p => p != null && p.State <= RaidDirectorService.StateDeparted) ?? 0;

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
            CampaignState s = St;
            PostgameState pg = PostgameService.StateOf(s);
            c.Log($"后日谈计数（全程，域内）：进入 {PostgameService.Starts}、发动 {PostgameService.Launches}、撑过 {PostgameService.Clears}、重发 {PostgameService.Retries}、坚守结束 {PostgameService.Ends}；" +
                  $"沙盒丢掉的突袭触发 {SandboxService.SuppressedRaidTriggers}；当前档 沙盒 {SandboxService.IsSandbox(s)}、后日谈 {pg?.Launched ?? -1} 发动 / {pg?.Cleared ?? -1} 撑过");
            c.Log(JourneyCommon.UiStats());
            c.Log(FgjM3Common.FrameReport("全程"));
            JourneyCommon.Cleanup(c);
        }

        // ── 1. 通关存档 ────────────────────────────────────────────────────────────

        private static void ApplyFixture(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt("fix.done") == 1)
            {
                return;
            }
            c.SetInt("fix.done", 1);
            long now = GameClock.Ticks;
            s.Progress.Act = 3;
            EventDirectorService.SetFrequency(s, EventDirectorService.FrequencyOff);
            // 终局链记为已完成（各服务按账本 / 自己的完成步判定；先写完成步再写揭示账本，揭示后清洗波次就不会再开始）。
            PurgeWaveState pw = PurgeWaveService.StateOf(s);
            pw.StartTick = pw.DoneTick = now;
            pw.Launched = pw.Cleared = PurgeWaveService.WaveCount;
            BeachheadService.StateOf(s).DoneTick = now;
            FlagshipState fs = FlagshipService.StateOf(s);
            fs.Phase = FlagshipService.PhaseDone;
            fs.StartTick = fs.DoneTick = now;
            foreach (string ev in new[] { RevealService.RevealedEventId, PurgeWaveService.ClearedEventId, BeachheadService.ClearedEventId, FlagshipService.DefeatedEventId })
            {
                CampaignEventLedger.TryGrant(s, ev, "Endgame", s.PlaySeconds, "fgjm14.fixture");
            }

            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(new[] { ComponentCatalog.CompCannonId, ComponentCatalog.CompGunId }).Distinct().ToArray();
            string bpId = FgjM12Journey.SaveBlueprint(c, "bp.id", "重炮作战", BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null, Array.Empty<string>()));
            string bpGun = FgjM12Journey.SaveBlueprint(c, "bp.gun", "机枪作战", BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()));
            if (bpId.Length == 0 || bpGun.Length == 0)
            {
                c.Set("fix.err", "保存作战蓝图失败：" + c.Get("bp.id.note") + " / " + c.Get("bp.gun.note"));
                return;
            }
            Vector2 core = FgjM8Journey.CorePos;
            var ids = new List<int>();
            var used = new List<GridCell>();
            for (int i = 0; i < SquadSize; i++)
            {
                GridCell at = default;
                for (int tr = i; tr < i + 40; tr++)
                {
                    at = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(core.x - 9f + (tr % 6) * 3f), Mathf.RoundToInt(core.y - 9f - (tr / 6) * 3f)), 10);
                    if (!used.Any(x => Math.Abs(x.X - at.X) + Math.Abs(x.Y - at.Y) < 2))
                    {
                        break;
                    }
                }
                used.Add(at);
                int id = FgjM12Journey.SpawnMachine(s, i % 2 == 0 ? bpId : bpGun, new Vector2(at.X, at.Y), SquadHp);
                if (id > 0)
                {
                    ids.Add(id);
                }
            }
            c.Set("roster", string.Join(",", ids.Select(x => x.ToString(CultureInfo.InvariantCulture))));

            var made = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                GridCell? at = FgProductionSelfCheck.FindFree(s, RepairDroneCatalog.StationTypeId, 6f, 14f);
                if (at.HasValue)
                {
                    BuildingRecord b = FgProductionSelfCheck.Built(s, RepairDroneCatalog.StationTypeId, "m14rep" + i, at.Value);
                    b.Health = BuildingOps.MaxDurability(RepairDroneCatalog.StationTypeId);
                    Mega.SetPower(b, true);
                    made.Add(FgjM3Common.Cell(at.Value));
                }
            }
            int kits = HomeInventory.Add(s, BuildingOps.RepairKitId, 200, clampToSpace: false);
            RepairDroneService.Sync(s);
            c.Set("def.note", string.Join("、", made) + $"；维修件 +{kits}");
        }

        private static StepOutcome TickFixture(JourneyContext c)
        {
            if (c.StepElapsed < 2.5)
            {
                return StepOutcome.Wait;
            }
            if (c.Get("fix.err", string.Empty).Length > 0)
            {
                return StepOutcome.Fail("夹具：" + c.Get("fix.err"));
            }
            CampaignState s = St;
            CombatSite home = GameRoot.HomeValley?.Combat;
            List<int> team = Team(c);
            bool inKernel = team.Count == SquadSize && team.All(id => home != null && home.TryGetMachineUnit(id, out _));
            if (!inKernel)
            {
                return c.StepElapsed < 12 ? StepOutcome.Wait : StepOutcome.Fail($"夹具后作战队没全进家园内核（{team.Count} 台）");
            }
            int stations = Stations().Count;
            string msg = $"第 {s.Progress.Act} 幕；揭示 {RevealService.Revealed(s)}、清洗 {PurgeWaveService.Done(s)}、滩头 {BeachheadService.Done(s)}、旗舰 {FlagshipService.Done(s)}；事件频率 {EventDirectorService.StateOf(s)?.Frequency}；" +
                         $"作战队 {team.Count} 台；维修站 {stations} 座（{c.Get("def.note")}）";
            return stations == 2 && RevealService.Revealed(s) && PurgeWaveService.Done(s) && FlagshipService.Done(s) ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        private static void ApplyEnding(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt("end.done") == 1)
            {
                return;
            }
            c.SetInt("end.done", 1);
            CampaignEventLedger.TryGrant(s, EndingService.EndingEventId, "Endgame", s.PlaySeconds, "fgjm14.fixture");
            SaveResult r = EndingService.EndingSaveHandler(); // 正式结局自动存档入口（结算打开之前写）
            c.Set("end.save", $"{r.Outcome}（{r.Message}）");
            c.SetInt("end.ok", r.Success ? 1 : 0);
            c.SetLong("end.ticks", GameClock.Ticks);
        }

        private static StepOutcome TickEndingSaved(JourneyContext c)
        {
            CampaignState s = St;
            if (c.GetInt("end.ok") != 1)
            {
                return StepOutcome.Fail("结局自动存档失败：" + c.Get("end.save"));
            }
            if (!EndingSettlementPanelUIToolkit.IsOpen)
            {
                return c.StepElapsed < 20 ? StepOutcome.Wait : StepOutcome.Fail($"结局已达成 {EndingService.Reached(s)}，结算面板 20 秒内没有自动打开");
            }
            // 探测：结算打开期间走正式自动存档入口，应被拦（不写盘）；A2 把它登记为探测夹具。
            SaveResult blocked = CampaignAutoSaveService.SaveAuto(SaveReason.PostgameStart, toast: false);
            string msg = $"结局账本 {EndingService.Reached(s)}；结局自动存档 {c.Get("end.save")}（槽位 {CampaignSession.ActiveSlotIndex + 1}，第 {c.GetLong("end.ticks")} 步）；结算自动打开、世界暂停 {GameClock.Paused}；结算期间再存档 → {blocked.Outcome}";
            return blocked.Outcome == SaveOutcome.SettlementOpen && GameClock.Paused ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        private static StepOutcome TickMenuShown(JourneyContext c)
        {
            if (JourneyInput.FindActiveButton("m_btn_Load") == null || c.StepElapsed < 2)
            {
                return c.StepElapsed < 50 ? StepOutcome.Wait : StepOutcome.Fail("点“返回主菜单”后没有回到主菜单：" + JourneyInput.LastUiFailure);
            }
            LoadResult onDisk = CampaignSaveService.Load(c.GetInt("slot"));
            if (!onDisk.Success)
            {
                return StepOutcome.Fail($"通关存档读不出来：{onDisk.Outcome}/{onDisk.Reason}");
            }
            CampaignState d = onDisk.State;
            bool ok = EndingService.Reached(d) && !d.Progress.IsPostgame && d.Clock.Ticks == c.GetLong("end.ticks");
            string msg = $"回到主菜单；槽位 {c.GetInt("slot") + 1} 的存档：结局 {EndingService.Reached(d)}、后日谈 {d.Progress.IsPostgame}、第 {d.Clock.Ticks} 步（落盘那一步 {c.GetLong("end.ticks")}）、存档原因 {d.LastSaveReason}";
            return ok ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        private static StepOutcome TickLoadedSettlement(JourneyContext c)
        {
            if (GameRoot.HomeValley == null || !GameRoot.HomeValley.IsActive || !EndingSettlementPanelUIToolkit.IsOpen)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            EndingStats st = EndingService.Stats(s);
            int survivors = Team(c).Count(Alive);
            string msg = $"读档进入归还谷地；结算重新打开（世界暂停 {GameClock.Paused}、存档被拦 {EndingService.BlocksSave(s)}）；统计 归还面积 {st.ReturnedArea}、{st.Days} 天、击退突袭 {st.RaidsRepelled}；幸存机器 {survivors}/{Team(c).Count}";
            return EndingService.BlocksSave(s) && survivors == SquadSize ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        // ── 2. 后日谈 ──────────────────────────────────────────────────────────────

        private static StepOutcome TickIntro(JourneyContext c)
        {
            CampaignState s = St;
            if (c.GetInt(FgjM3Common.SK(c, "ok")) == 1)
            {
                if (PostgamePanelUIToolkit.IsOpen)
                {
                    return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Retry("点“知道了”后说明没关");
                }
                JourneyCommon.ResumeIfAutoPaused(c);
                return StepOutcome.Done(c.Get("pg.intro"));
            }
            if (!PostgamePanelUIToolkit.IsOpen || !PostgamePanelUIToolkit.IntroMode || c.StepElapsed < 1)
            {
                return c.StepElapsed < 15 ? StepOutcome.Wait : StepOutcome.Fail($"进入后日谈后说明没有自动打开（后日谈 {PostgameService.Started(s)}）");
            }
            PostgameState st = PostgameService.StateOf(s);
            string intro = PostgamePanelUIToolkit.Instance?.IntroText ?? string.Empty;
            string hud = OrbitalHud();
            long gap = st.NextPurgeTick - st.StartTick;
            bool ok = PostgameService.Active(s) && gap == RaidDirectorService.DayTicks(PostgameService.PeriodDays) && intro.Contains("200") && hud.Contains("后日谈 · 第 1 级清洗");
            c.Set("pg.intro", $"说明 {intro.Split('\n').Length} 行（“{intro.Replace("\n", " / ")}”）；坚守起算第 {st.StartTick} 步、第一次清洗第 {st.NextPurgeTick} 步（隔 {F(gap / (double)RaidDirectorService.DayTicks(1), "0.##")} 日）；顶栏“{hud}”；存档原因 {s.LastSaveReason}");
            if (!ok)
            {
                return StepOutcome.Fail(c.Get("pg.intro"));
            }
            if (!JourneyInput.ClickUitk("[PostgameHost]", "PostgameOk"))
            {
                return StepOutcome.Retry("点不到“知道了”：" + JourneyInput.LastUiFailure);
            }
            c.SetInt(FgjM3Common.SK(c, "ok"), 1);
            return StepOutcome.Wait;
        }

        private static IEnumerable<JourneyStep> PurgeSteps(int w)
        {
            yield return S($"p{w}_skip", $"时间夹具（DEBT-FG14E2E01-02）：跳到第 {w} 级清洗发动前 30 游戏秒（7 日节拍由 FgPostgameSelfCheck B2 覆盖），顶栏倒计时", 20, c => SkipToPurge(c, w), c => TickSkipped(c, w));
            yield return JourneyCommon.PureWait(S($"p{w}_plan", $"第 {w} 级清洗按节拍交给突袭导演、建出计划（预算、精英、强度），读来袭方向、在核心外排 36 个散开的驻防点", 900, null, c => TickPurgePlan(c, w)));
            yield return S($"p{w}_spread", "逐台左键点选、右键各自的驻防点（移动；散开站位，免得熔穿漏出的熔融金属互相回灌热量）", 240, null, c => FgjM13Journey.TickSpread(c, w, Watch));
            yield return S($"p{w}_sel", "左键点第一台、Shift 左键加选全部作战队", 90, null, c => FgjM12Journey.SelectMachines(c, FreeTeam(c), "作战队"), retries: 2);
            yield return S($"p{w}_hold", "按守卫键（默认 G）：每台守自己的驻防点", 15, c => JourneyInput.PressAction(GameActionId.CommandGuard),
                c => c.StepElapsed < 1.0 ? StepOutcome.Wait : StepOutcome.Done($"第 {w} 级：选中 {GameRoot.HomeValley?.SquadCommands?.Selection?.Count ?? 0} 台驻防"), retries: 1);
            yield return S($"p{w}_fight", $"守住第 {w} 级清洗：登陆舱投放、作战队迎击、维修无人机修核心 → 这一次撑过", 900, null, c => TickPurgeFight(c, w));
            yield return S($"p{w}_done", $"第 {w} 级记为撑过：清洗历史一行（击毁 / 损失）、顶栏换成第 {w + 1} 级倒计时、下一次 = 排定 + 7 日", 30, null, c => TickPurgeDone(c, w));
        }

        private static void SkipToPurge(JourneyContext c, int w)
        {
            CampaignState s = St;
            PostgameState st = PostgameService.StateOf(s);
            long target = PostgameService.NextLaunchTick(st) - 30L * GameClock.StepHz;
            c.SetLong(FgjM3Common.SK(c, "from"), GameClock.Ticks);
            if (target > GameClock.Ticks)
            {
                GameClock.SkipForTests(s, target - GameClock.Ticks);
            }
            c.SetLong(FgjM3Common.SK(c, "to"), GameClock.Ticks);
        }

        private static StepOutcome TickSkipped(JourneyContext c, int w)
        {
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            JourneyCommon.ResumeIfAutoPaused(c);
            PostgameState st = PostgameService.StateOf(St);
            string hud = OrbitalHud();
            string msg = $"第 {c.GetLong(FgjM3Common.SK(c, "from"))} 步 → 第 {c.GetLong(FgjM3Common.SK(c, "to"))} 步（跳 {F((c.GetLong(FgjM3Common.SK(c, "to")) - c.GetLong(FgjM3Common.SK(c, "from"))) / (double)RaidDirectorService.DayTicks(1), "0.##")} 日）；" +
                         $"第 {w} 级排在第 {PostgameService.NextLaunchTick(st)} 步；顶栏“{hud}”";
            return hud.Contains($"第 {w} 级清洗") ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        private static StepOutcome TickPurgePlan(JourneyContext c, int w)
        {
            StepOutcome? bad = Watch(c);
            if (bad.HasValue)
            {
                return bad.Value;
            }
            CampaignState s = St;
            PostgameState st = PostgameService.StateOf(s);
            RaidPlanRecord plan = st.ActiveIndex == w - 1 && st.ActivePlanId.Length > 0 ? RaidDirectorService.FindPlan(s, st.ActivePlanId) : null;
            if (plan == null || plan.State < RaidDirectorService.StateScheduled)
            {
                return StepOutcome.Wait;
            }
            TransitGroupRecord g = (s.Raids?.InTransit ?? Array.Empty<TransitGroupRecord>()).FirstOrDefault(x => x != null && x.PlanId == plan.PlanId);
            Vector2 from = g != null ? new Vector2((float)g.PosX, (float)g.PosY) : new Vector2(plan.OriginX, plan.OriginY);
            FgjM13Journey.SetV(c, "pg.from", from);
            List<Vector2> posts = FgjM13Journey.SpreadPosts(from, FreeTeam(c).Count);
            PostgamePurgeRecord rec = st.History.LastOrDefault();
            c.SetInt("pg.budget" + w, plan.Budget);
            string hud = OrbitalHud();
            string msg = $"第 {w} 级清洗计划 {plan.PlanId}（剧情 ID {plan.StoryId}、等级 {plan.Level}、预算 {plan.Budget}、{rec?.Units} 台（精英 {rec?.Elites}，强度 ×{F(rec?.Strength ?? 0, "0.00")}）、状态 {plan.State}）；发动于第 {rec?.LaunchTick} 步；" +
                         $"出发地 {Cell(new Vector2(plan.OriginX, plan.OriginY))}，驻防点 {posts.Count} 个；顶栏“{hud}”";
            string kSeen = FgjM3Common.SK(c, "seen");
            if (c.GetLong(kSeen) == 0)
            {
                c.SetLong(kSeen, FgjM2Common.NowMs());
            }
            bool ok = plan.StoryId == PostgameService.PurgeId(w - 1) && rec != null && rec.Level == w && hud.Contains($"第 {w} 级清洗进行中");
            return ok ? StepOutcome.Done(msg) : FgjM2Common.NowMs() - c.GetLong(kSeen) < 5000 ? StepOutcome.Wait : StepOutcome.Fail(msg);
        }

        private static List<BuildingRecord> Stations() =>
            (St?.BuildingRecords ?? Array.Empty<BuildingRecord>()).Where(b => b != null && b.BuildingTypeId == RepairDroneCatalog.StationTypeId && b.ConstructionState == BuildingConstructionState.Operational).ToList();

        /// <summary>清洗期间每帧的共同检查：维修站供电夹具、后日谈没结束（核心完好）、作战队还有人、来犯者峰值、每 20 秒一行日志。返回非空 = 失败。</summary>
        private static StepOutcome? Watch(JourneyContext c)
        {
            foreach (BuildingRecord b in Stations())
            {
                if (b.PowerState != BuildingPowerState.Powered)
                {
                    Mega.SetPower(b, true); // 供电夹具（DEBT-FG14E2E01-01，同 FGJ-M13）
                    c.SetInt("def.reassert", c.GetInt("def.reassert") + 1);
                }
            }
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            CampaignState s = St;
            PostgameState st = PostgameService.StateOf(s);
            if (PostgameService.Ended(s) || HomeValleySoftlockGuard.IsCoreDestroyed(s))
            {
                return StepOutcome.Fail($"后日谈坚守结束（核心被毁）：第 {st.Launched} 次清洗；{TeamHp(c)}");
            }
            if (FreeTeam(c).Count == 0)
            {
                return StepOutcome.Fail("作战队全灭");
            }
            List<(int Id, Vector2 Pos)> raiders = FgjM6Common.Raiders();
            c.SetInt("pg.raiders_max", Math.Max(c.GetInt("pg.raiders_max"), raiders.Count));
            if ((int)(c.StepElapsed / 20) > c.GetInt(FgjM3Common.SK(c, "log")))
            {
                c.SetInt(FgjM3Common.SK(c, "log"), (int)(c.StepElapsed / 20));
                c.Log($"[{F(c.StepElapsed, "0")}s] 后日谈 {st.Launched} 发动 / {st.Cleared} 撑过；顶栏“{OrbitalHud()}”；在场来犯者 {raiders.Count}；核心耐久 {F(FgjM6Common.Core(s)?.Health ?? -1, "0")}；维修件 {HomeInventory.Stock(s, BuildingOps.RepairKitId)}；存活 {FreeTeam(c).Count}/{Team(c).Count}");
            }
            return null;
        }

        private static StepOutcome TickPurgeFight(JourneyContext c, int w)
        {
            StepOutcome? bad = Watch(c);
            if (bad.HasValue)
            {
                return bad.Value;
            }
            PostgameState st = PostgameService.StateOf(St);
            if (st.Cleared >= w)
            {
                return StepOutcome.Done($"第 {w} 级撑过（同时在场的来犯者最多 {c.GetInt("pg.raiders_max")} 台；右键来犯者 {c.GetInt("pg.rc" + w)} 次；维修站供电夹具补置 {c.GetInt("def.reassert")} 次；核心耐久 {F(FgjM6Common.Core(St)?.Health ?? -1, "0")}）；存活 {FreeTeam(c).Count}/{Team(c).Count}");
            }
            // 来犯者在核心外的集结点展开、到达后 raid.time_limit_hours（1 游戏小时 = 50 游戏秒）就撤：只守驻防点打不到几台（第 1 次实跑击毁 0、全部撤走）。
            // 正式打法：全队仍选中，右键离作战队最近的来犯者（RTS 右键敌人 = 攻击），每 3 秒或目标换了再右键一次。
            List<(int Id, Vector2 Pos)> raiders = FgjM6Common.Raiders();
            List<int> team = FreeTeam(c);
            if (raiders.Count > 0 && team.Count > 0 && !JourneyInput.Holding)
            {
                Vector2 center = team.Aggregate(Vector2.zero, (a, x) => a + FgjM8Journey.PosOf(x)) / team.Count;
                (int Id, Vector2 Pos) t = raiders.OrderBy(r => (r.Pos - center).sqrMagnitude).First();
                string kt = FgjM3Common.SK(c, "tgt"), ka = FgjM3Common.SK(c, "rcAt");
                if ((c.GetInt(kt) != t.Id || FgjM2Common.NowMs() - c.GetLong(ka) > 3000) && FgjM3Common.TryClick(t.Pos, 1))
                {
                    c.SetInt(kt, t.Id);
                    c.SetLong(ka, FgjM2Common.NowMs());
                    c.SetInt("pg.rc" + w, c.GetInt("pg.rc" + w) + 1);
                }
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickPurgeDone(JourneyContext c, int w)
        {
            if (c.StepElapsed < 1.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            PostgameState st = PostgameService.StateOf(s);
            PostgamePurgeRecord rec = st.History.FirstOrDefault(r => r != null && r.Level == w);
            string hud = OrbitalHud();
            long scheduledGap = rec == null ? -1 : st.NextPurgeTick - rec.LaunchTick;
            string msg = $"历史：“{PostgameService.RowText(rec)}”；结果 {rec?.Outcome}、击毁 {rec?.Killed}、损失 {rec?.Losses}；最高等级 {st.HighestLevel}；下一次第 {st.NextPurgeTick} 步（距这次发动 {F(scheduledGap / (double)RaidDirectorService.DayTicks(1), "0.##")} 日）；顶栏“{hud}”";
            bool ok = rec != null && rec.Outcome == PostgameService.OutcomeRepelled && rec.Killed > 0 && st.HighestLevel == w && st.ActiveIndex < 0
                      && hud.Contains($"第 {w + 1} 级清洗") && scheduledGap > RaidDirectorService.DayTicks(6.9) && scheduledGap <= RaidDirectorService.DayTicks(7);
            return ok ? StepOutcome.Done(msg) : c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail(msg);
        }

        private static StepOutcome TickStats(JourneyContext c)
        {
            CampaignState s = St;
            PostgameState st = PostgameService.StateOf(s);
            int days = PostgameService.HoldDays(s, GameClock.Ticks);
            int b1 = c.GetInt("pg.budget1"), b2 = c.GetInt("pg.budget2");
            string msg = $"“{PostgameService.SummaryLine(s, GameClock.Ticks)}”；{PostgameService.BestLine(s)}；{PostgameService.TotalsLine(s)}；预算 第 1 级 {b1} → 第 2 级 {b2}";
            return st.Cleared == 2 && st.Launched == 2 && st.HighestLevel == 2 && days >= 14 && b2 > b1 && b1 > 0 ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        private static Func<JourneyContext, StepOutcome> TickPauseOpen(bool postgame) => c =>
        {
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (!PauseMenuUIToolkit.IsOpen)
            {
                return StepOutcome.Retry("按 Esc 后暂停菜单没有打开");
            }
            Button b = JourneyInput.FindUitk<Button>("[PauseMenuHost]", postgame ? "PausePostgame" : "PauseSandbox");
            bool shown = b != null && !b.ClassListContains("uk-hidden");
            return shown ? StepOutcome.Done($"暂停菜单打开，“{b.text}”可见；世界暂停 {GameClock.Paused}") : StepOutcome.Fail($"暂停菜单里没有 {(postgame ? "坚守记录" : "沙盒设置")}");
        };

        private static StepOutcome TickRecord(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            PostgamePanelUIToolkit p = PostgamePanelUIToolkit.Instance;
            if (!PostgamePanelUIToolkit.IsOpen || PostgamePanelUIToolkit.IntroMode || p == null)
            {
                return StepOutcome.Retry("点“坚守记录”后面板没有以记录模式打开：" + FgjM1Journey.UiFail(c));
            }
            var rows = Enumerable.Range(0, p.VisibleRowCount).Select(p.RowText).ToList();
            string msg = $"汇总“{p.SummaryText}”；{p.BestText}；{p.TotalsText}；历史 {rows.Count} 行：{string.Join(" | ", rows)}";
            return rows.Count >= 2 && rows.Count(r => r.Contains("撑过")) >= 2 && p.SummaryText.Length > 0 ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        private static StepOutcome TickCard(JourneyContext c)
        {
            if (JourneyInput.FindActiveButton("m_btn_Sandbox") == null || c.StepElapsed < 2)
            {
                return StepOutcome.Wait;
            }
            CampaignSlotMetadata meta = CampaignSaveService.GetAllSlotMetadata().FirstOrDefault(m => m.SlotIndex == c.GetInt("slot"));
            string card = meta == null ? string.Empty : CampaignSlotText.FgFields(meta);
            string msg = $"槽位 {c.GetInt("slot") + 1} 存档卡“{card}”（后日谈 {meta?.IsPostgame}、坚守 {meta?.PostgameDays} 天、最高 {meta?.PostgameTopLevel} 级）";
            return meta != null && meta.IsPostgame && meta.PostgameDays >= 14 && meta.PostgameTopLevel == 2 && card.Contains("后日谈") ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        // ── 3. 沙盒 ────────────────────────────────────────────────────────────────

        private static StepOutcome TickSandboxOpen(JourneyContext c)
        {
            NewGamePanelUIToolkit p = NewGamePanelUIToolkit.Instance;
            if (!NewGamePanelUIToolkit.IsOpen || p == null || c.StepElapsed < 1)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Retry("点“沙盒模式”后新游戏面板没打开：" + FgjM1Journey.UiFail(c));
            }
            string msg = $"面板标题“{p.TitleText}”；沙盒模式 {p.IsSandboxMode}、沙盒设置段可见 {p.SandboxSectionVisible}；说明“{p.SandboxNoteText}”；开局说明“{p.StoryNoteText}”";
            return p.IsSandboxMode && p.SandboxSectionVisible ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        private static StepOutcome TickSandboxSetup(JourneyContext c)
        {
            NewGamePanelUIToolkit p = NewGamePanelUIToolkit.Instance;
            if (p == null || !NewGamePanelUIToolkit.IsOpen)
            {
                return c.GetInt(FgjM3Common.SK(c, "ph")) == 3 ? StepOutcome.Done(c.Get("sb.note")) : StepOutcome.Fail("新游戏面板不见了");
            }
            if (JourneyInput.Holding || FgjM2Common.NowMs() - c.GetLong(FgjM3Common.SK(c, "at")) < 500)
            {
                return StepOutcome.Wait;
            }
            switch (c.GetInt(FgjM3Common.SK(c, "ph")))
            {
                case 0:
                {
                    TextField field = JourneyInput.FindUitk<TextField>("[NewGameHost]", "NewGameSeed");
                    if (field == null || !JourneyInput.ClickElement(field))
                    {
                        return StepOutcome.Retry("点不到种子框：" + JourneyInput.LastUiFailure);
                    }
                    c.Set("sb.random", field.value);
                    field.value = SandboxSeed.ToString(CultureInfo.InvariantCulture); // 与在框里打字同一个值变化回调（FGJ-M3 同一做法）
                    break;
                }
                case 1:
                    if (!JourneyInput.ClickUitk("[NewGameHost]", $"NewGameSandbox_{SandboxService.Raids}_{SandboxService.RaidsOff}"))
                    {
                        return StepOutcome.Retry("点不到突袭“关闭”：" + JourneyInput.LastUiFailure);
                    }
                    break;
                case 2:
                {
                    Button off = p.SandboxOption(SandboxService.Raids, SandboxService.RaidsOff);
                    bool parsed = p.TryCurrentSeed(out int seed, out _) && seed == SandboxSeed;
                    if (off == null || !off.text.StartsWith("▸", StringComparison.Ordinal) || !parsed)
                    {
                        return StepOutcome.Fail($"选完后：突袭“关闭”按钮“{off?.text}”、种子解析 {parsed}");
                    }
                    c.Set("sb.note", $"种子框原值 {c.Get("sb.random")} → 输入 {SandboxSeed}；突袭按钮“{off.text}”；点“开始”");
                    if (!JourneyInput.ClickUitk("[NewGameHost]", "NewGameStart"))
                    {
                        return StepOutcome.Retry("点不到“开始”：" + JourneyInput.LastUiFailure);
                    }
                    break;
                }
                default:
                    return StepOutcome.Wait;
            }
            c.SetInt(FgjM3Common.SK(c, "ph"), c.GetInt(FgjM3Common.SK(c, "ph")) + 1);
            c.SetLong(FgjM3Common.SK(c, "at"), FgjM2Common.NowMs());
            return StepOutcome.Wait;
        }

        private static StepOutcome TickSandboxState(JourneyContext c)
        {
            CampaignState s = St;
            if (s?.World == null || c.StepElapsed < 1)
            {
                return StepOutcome.Wait;
            }
            string msg = $"沙盒 {SandboxService.IsSandbox(s)}、战役种子 {s.RandomSeed}、世界种子 {s.World.WorldSeed}、突袭关闭 {SandboxService.RaidsDisabled(s)}、剧情关闭 {SandboxService.StoryOff(s)}、" +
                         $"槽位 {CampaignSession.ActiveSlotIndex + 1}";
            if (!SandboxService.IsSandbox(s) || !SandboxService.RaidsDisabled(s) || !SandboxService.StoryOff(s))
            {
                return StepOutcome.Fail(msg);
            }
            StepOutcome seed = JourneyCommon.TickSeed(c, SandboxSeed);
            return seed.Status == JourneyStepStatus.Done ? StepOutcome.Done(msg + "；" + seed.Message) : seed;
        }

        private static void ApplyRaidTriggers(JourneyContext c)
        {
            CampaignState s = St;
            if (s == null || c.GetInt(FgjM3Common.SK(c, "fired")) == 1)
            {
                return;
            }
            c.SetInt(FgjM3Common.SK(c, "fired"), 1);
            c.SetInt("sb.sup0", SandboxService.SuppressedRaidTriggers);
            c.SetInt("sb.raids0", RaidDirectorService.StateOf(s)?.RaidCount ?? -1);
            // 正式触发入口（同 FgSandboxSelfCheck C1）：剧情报复、净化塔被盯上、巨构阶段完工、信号暴露拉满。
            RaidDirectorService.RequestStoryRaid(s, RaidCatalog.FactionChemical, 2, "fgjm14");
            RaidDirectorService.OnPurificationTowerTargeted(s);
            RaidDirectorService.OnMegastructureStageCompleted(s, 3);
            s.SignalExposure = 100f;
            c.SetLong(FgjM3Common.SK(c, "t0"), GameClock.Ticks);
        }

        private static StepOutcome TickNoRaid(JourneyContext c)
        {
            CampaignState s = St;
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM3Common.SampleFrame();
            int plans = LivePlans(s);
            if (plans > 0 || FgjM6Common.Raiders().Count > 0)
            {
                return StepOutcome.Fail($"关闭突袭的沙盒里出现突袭：计划 {plans}、在场来犯者 {FgjM6Common.Raiders().Count}");
            }
            string kd = FgjM3Common.SK(c, "day");
            int day = c.GetInt(kd);
            long since = GameClock.Ticks - c.GetLong(FgjM3Common.SK(c, "t0"));
            if (since < 20L * GameClock.StepHz)
            {
                return StepOutcome.Wait; // 每一段真实运行 20 游戏秒（3 倍速约 7 秒）
            }
            if (day < 3)
            {
                GameClock.SkipForTests(s, RaidDirectorService.DayTicks(1) - 40L * GameClock.StepHz); // 时间夹具（DEBT-FG14E2E01-02）：跳到这一日末前 20 游戏秒
                c.SetInt(kd, day + 1);
                c.SetLong(FgjM3Common.SK(c, "t0"), GameClock.Ticks);
                return StepOutcome.Wait;
            }
            SilentNightState night = SilentNightService.StateOf(s);
            if (day == 3)
            {
                // 剧情档里每次静默夜必定有一次突袭（RaidDirectorService.OnSilentNightStarted）：跳到下一次静默夜开始前 2 游戏秒（时间夹具），看它照常开始、但没有进攻（FG12 第 4 节）。
                c.SetLong("sb.night0", night.StartTick);
                if (night.StartTick - 2L * GameClock.StepHz > GameClock.Ticks)
                {
                    GameClock.SkipForTests(s, night.StartTick - 2L * GameClock.StepHz - GameClock.Ticks);
                }
                c.SetInt(kd, 4);
                c.SetLong(FgjM3Common.SK(c, "t0"), GameClock.Ticks);
                return StepOutcome.Wait;
            }
            if (day == 4)
            {
                c.SetInt("sb.night", SilentNightService.IsActive(s) ? 1 : 0);
                c.SetInt(kd, 5);
            }
            int suppressed = SandboxService.SuppressedRaidTriggers - c.GetInt("sb.sup0");
            int raids = RaidDirectorService.StateOf(s)?.RaidCount ?? -1;
            string hud = OrbitalHud();
            string msg = $"丢掉突袭触发 {suppressed} 次（剧情报复 / 净化塔 / 巨构完工 / 静默夜）；三个游戏日 + 静默夜后（第 {GameClock.Ticks} 步、第 {GameClock.DayOf(GameClock.GameSeconds)} 日）：静默夜照常开始 {c.GetInt("sb.night") == 1}（排在第 {c.GetLong("sb.night0")} 步），" +
                         $"计划 {plans}、突袭次数 {c.GetInt("sb.raids0")} → {raids}、暴露 {F(s.SignalExposure)}；顶栏轨道威胁行“{hud}”（沙盒没有终局 = 隐藏）";
            return suppressed >= 4 && raids == 0 && c.GetInt("sb.night") == 1 && hud.Length == 0 ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }

        private static StepOutcome TickSandboxPanel(JourneyContext c)
        {
            if (c.StepElapsed < 0.8)
            {
                return StepOutcome.Wait;
            }
            SandboxPanelUIToolkit p = SandboxPanelUIToolkit.Instance;
            if (!SandboxPanelUIToolkit.IsOpen || p == null)
            {
                return StepOutcome.Retry("点“沙盒设置”后面板没开：" + FgjM1Journey.UiFail(c));
            }
            Button off = JourneyInput.FindUitk<Button>("[SandboxHost]", $"SandboxOpt_{SandboxService.Raids}_{SandboxService.RaidsOff}");
            string msg = $"沙盒设置面板打开：状态“{p.StatusText}”；固定项“{p.FixedText}”；突袭“关闭”按钮“{off?.text ?? "（按名字没找到）"}”";
            return SandboxService.Get(St, SandboxService.Raids) == SandboxService.RaidsOff && off != null && off.text.StartsWith("▸", StringComparison.Ordinal) ? StepOutcome.Done(msg) : StepOutcome.Fail(msg);
        }
    }
}
