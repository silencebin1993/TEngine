using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Progression;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG9-ACT1-01 修复轮（第 1 轮审查 P1“验收：FGJ-M9 的前半段没有交付”）：FGJ-M9A —— 第一幕前半段，按正式节奏（不开旧规则开关）从主菜单新游戏走到
    /// “第一次远征归来 → 基础传送带开放 → 传送带指引出现 → 建造栏里选得中传送带”。整条 FGJ-M9（打到击败铸造主核心）由 FG9-E2E-01 在本旅程之后接着写。
    /// 步骤：开局（输入种子 42）→ 节奏台账记下“苏醒”、建造栏里传送带 T1 锁着（条目写解锁条件）→ 修仓库 / 发电机 / 信号塔、拆残骸、造第三台机器
    /// （复用 FGJ-M3 的同名步骤）→ 出征破碎都市（沿地形行军）→ 在地点里传送带仍锁 → 接入、撤离 → 远征队沿地形走回家园（正式到家结算）→
    /// 基础传送带开放：一条“新系统”通知、图鉴、传送带指引任务出现 → 建造栏里传送带 T1 不再写解锁条件。用法：bash tools/unity-journey.sh FGJ-M9A。
    /// </summary>
    public static class FgjM9FrontJourney
    {
        public const string Id = "FGJ-M9A";

        private static CampaignState St => CampaignSession.Current;

        public static JourneyDef Build()
        {
            JourneyDef m3 = FgjM3Journey.Build();
            JourneyStep Take(string id) => m3.Steps.First(s => s.Id == id);
            var steps = new List<JourneyStep>();
            foreach (string id in new[] { "play", "menu_new", "new_game", "seed", "workers" })
            {
                steps.Add(Take(id));
            }
            steps.Add(S("pace_wake", "节奏台账：开局第 0 步记下“苏醒”；传送带 T1 锁定；传送带指引还没出现（正式节奏，不开旧规则开关）", 20, null, TickWake));
            steps.Add(Take("speed3"));
            steps.Add(S("build_open", "按建造键（默认 B）打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1));
            steps.Add(S("belt_locked", "建造栏“物流”分类：传送带 T1 那一格写着解锁条件（第一次远征归来后开放）；点它只会得到虚影给出的原因（规则层锁着）", 20, null, TickBeltLocked, retries: 2));
            steps.Add(S("build_close", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1));
            foreach (string id in new[] { "sel_a", "repair_wh", "sel_b", "repair_gen", "wait_rep1", "sel_a2", "repair_tw", "sel_b2", "salvage", "salv_wait", "wait_rep2",
                         "fac_open", "fac_produce", "fac_close", "m3_wait", "m3_sel", "m3_out", "prep_open", "prep_pick" })
            {
                steps.Add(Take(id));
            }
            steps.Add(S("prep_depart", "点“出发”：远征队沿地形行军到破碎都市（跟随、3 倍速；途中被截断、队伍在覆盖外时由行军服务按最后的“前进”自动重试，FG-GAP-136）", 200, c => FgjM1Journey.ClickUi(c, "[HomeValleyExpeditionPrepHost]", "DepartButton"), TickDeparted, retries: 1));
            steps.Add(S("in_city", "到了破碎都市：节奏台账记下“第一次远征”；人还在地点里，传送带仍锁着（负向）", 20, null, TickInCity));
            steps.Add(S("uplink", "机器列表点一台远征机器：接入", 25, c => FgjM1Journey.ClickMachineList(c, c.GetInt("other")), TickUplinked, retries: 3));
            steps.Add(S("evac_hold", "开到撤离点按住交互键（默认 E）打开撤离清单", 40, c => JourneyInput.HoldAction(GameActionId.Interact, 1.6), FgjM1Journey.TickEvacPanel, retries: 3));
            steps.Add(S("evac_confirm", "点“确认撤离”：远征队离开破碎都市、镜头回家园", 30, c => FgjM1Journey.ClickUi(c, "[FracturedCityExpeditionReturnHost]", "ConfirmButton"), FgjM1Journey.TickReturnedHome, retries: 1));
            steps.Add(S("walk_home", "远征队沿地形走回家园（点 3 倍速等它到家，正式到家结算；到家后点回 1 倍速、关掉自动打开的离家报告）", 200, null, TickWalkHome));
            steps.Add(S("belts_open", "第一次远征归来：节奏台账记下“基础传送带开放”——一条“新系统”通知、图鉴“传送带”解锁、传送带指引任务出现、传送带 T1 可建", 20, null, TickBeltsOpen));
            steps.Add(S("build_open2", "按建造键打开建造模式", 10, c => FgjM3Common.PressBuild(true), c => FgjM3Common.TickBuild(c, true), retries: 1));
            steps.Add(S("belt_pick", "建造栏的传送带 T1：那一格不再写解锁条件（写造价），点它选中", 15, null, TickBeltOpenInMenu, retries: 2));
            steps.Add(S("build_close2", "按建造键关闭建造模式", 10, c => FgjM3Common.PressBuild(false), c => FgjM3Common.TickBuild(c, false), retries: 1));
            return new JourneyDef
            {
                Id = Id,
                Title = "M9 前半段：正式节奏开新档（种子 42）→ 传送带锁着 → 修建筑、造第三台机器 → 第一次远征破碎都市 → 走回家园 → 基础传送带开放（通知 / 图鉴 / 指引）→ 选得中传送带",
                Seed = FgjM3Journey.TestSeed,
                TotalTimeoutSeconds = 1500,
                OnFinish = m3.OnFinish,
                Version = 1,
                Steps = steps,
            };
        }

        private static JourneyStep S(string id, string title, double timeout, Action<JourneyContext> enter, Func<JourneyContext, StepOutcome> tick, int retries = 0) =>
            JourneyCommon.S(id, title, timeout, enter, tick, retries);

        private static bool BeltUnlocked() => BuildCatalog.TryGet(FgjM3Common.ToolBeltT1, out BuildEntry e) && BuildCatalog.IsUnlocked(St, e);

        private static StepOutcome TickWake(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            CampaignState s = St;
            ActPaceRecord wake = Act1PacingService.Find(s, "wake");
            bool legacyOff = !Act1PacingService.LegacyGatesOpenForJourneys;
            bool noGuide = CampaignObjectiveTracker.FindQuest(s, "guide.belt") == null;
            return wake != null && wake.Tick == 0 && legacyOff && !BeltUnlocked() && noGuide && !Act1PacingService.IsReached(s, "belts_open")
                ? StepOutcome.Done($"“苏醒”记在第 {wake.Tick} 步；正式节奏（旧规则开关关着）；传送带 T1 锁定（{GameLogic.Campaign.Economy.ResearchGate.Describe(s, "pace:belts_open")}）；传送带指引还没出现")
                : StepOutcome.Fail($"开局节奏不对：苏醒 {wake?.Tick.ToString() ?? "没记"}、旧规则开关 {Act1PacingService.LegacyGatesOpenForJourneys}、传送带解锁 {BeltUnlocked()}、指引已出现 {!noGuide}");
        }

        private static StepOutcome TickBeltLocked(JourneyContext c)
        {
            if (!FgjM3Common.Done(c, "pick"))
            {
                if (!FgjM3Common.PickEntry(FgjM3Common.ToolBeltT1, out string why, out bool scrolling))
                {
                    if (scrolling && c.GetInt(FgjM3Common.SK(c, "scrolls")) < 40)
                    {
                        c.SetInt(FgjM3Common.SK(c, "scrolls"), c.GetInt(FgjM3Common.SK(c, "scrolls")) + 1);
                        return StepOutcome.Wait;
                    }
                    return StepOutcome.Retry("点不到传送带 T1 那一格：" + (why ?? "滚了 40 次仍不在可见区"));
                }
                FgjM3Common.Mark(c, "pick");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            string text = string.Empty;
            for (int i = 0; hud != null && i < hud.ItemCount; i++)
            {
                if (hud.ItemId(i) == FgjM3Common.ToolBeltT1)
                {
                    text = JourneyInput.FindUitk<Button>(FgjM3Common.BuildHost, "BuildItem" + i)?.text ?? string.Empty;
                }
            }
            // 建造模式的设计：未解锁的条目也能选中，虚影给出原因、放不下（HomeValleyBuildMode.Select）——这里核对格子写着解锁条件、规则层仍锁着。
            string hint = GameText.Get("pace.hint.belts_open");
            bool selected = FgjM3Common.EntrySelected(FgjM3Common.ToolBeltT1);
            c.Set("belt.lockedText", text);
            return text.Contains(hint) && !BeltUnlocked()
                ? StepOutcome.Done($"传送带 T1 那一格写“{text.Replace("\n", " ")}”；点它{(selected ? "选中后虚影给出原因、放不下" : "没选中")}（规则层锁着）")
                : StepOutcome.Fail($"锁着的传送带 T1：格子文字“{text.Replace("\n", " ")}”（应含“{hint}”）、规则层已解锁 {BeltUnlocked()}");
        }

        /// <summary>开放之后：传送带 T1 那一格不再写解锁条件（改写造价），点它选中。</summary>
        private static StepOutcome TickBeltOpenInMenu(JourneyContext c)
        {
            if (!FgjM3Common.Done(c, "pick"))
            {
                if (!FgjM3Common.PickEntry(FgjM3Common.ToolBeltT1, out string why, out bool scrolling))
                {
                    if (scrolling && c.GetInt(FgjM3Common.SK(c, "scrolls")) < 40)
                    {
                        c.SetInt(FgjM3Common.SK(c, "scrolls"), c.GetInt(FgjM3Common.SK(c, "scrolls")) + 1);
                        return StepOutcome.Wait;
                    }
                    return StepOutcome.Retry("点不到传送带 T1 那一格：" + (why ?? "滚了 40 次仍不在可见区"));
                }
                FgjM3Common.Mark(c, "pick");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            string text = string.Empty;
            for (int i = 0; hud != null && i < hud.ItemCount; i++)
            {
                if (hud.ItemId(i) == FgjM3Common.ToolBeltT1)
                {
                    text = JourneyInput.FindUitk<Button>(FgjM3Common.BuildHost, "BuildItem" + i)?.text ?? string.Empty;
                }
            }
            string hint = GameText.Get("pace.hint.belts_open");
            bool selected = FgjM3Common.EntrySelected(FgjM3Common.ToolBeltT1);
            return selected && BeltUnlocked() && text.Length > 0 && !text.Contains(hint) && text != c.Get("belt.lockedText")
                ? StepOutcome.Done($"传送带 T1 那一格现在写“{text.Replace("\n", " ")}”（不再写解锁条件），点它选中")
                : StepOutcome.Fail($"开放后的传送带 T1：选中 {selected}、规则层解锁 {BeltUnlocked()}、格子文字“{text.Replace("\n", " ")}”");
        }

        private static StepOutcome TickDeparted(JourneyContext c)
        {
            // FG9-E2E-01：行军途中“路线被截断”（成员路径持续受阻、原地待命）时像玩家一样点行军条“前进”重新下令（与 FGJ-M8 同一写法，最多 3 次，报告逐次写明）；队伍在覆盖外发不了“前进”时由行军服务按最后的命令自动重试（FG-GAP-136 产品修复，截断夹具已删）。
            StepOutcome o = FgjM8Journey.TickDepart(c, 190);
            if (o.Status != JourneyStepStatus.Done)
            {
                return o;
            }
            List<int> roster = FgjM1Journey.Roster(c);
            c.SetInt("other", roster[0]);
            return o;
        }

        private static StepOutcome TickInCity(JourneyContext c)
        {
            if (c.StepElapsed < 2.0)
            {
                return StepOutcome.Wait; // 节奏台账每 1 游戏秒检查一次
            }
            CampaignState s = St;
            bool entered = Act1PacingService.IsReached(s, "first_expedition");
            bool stillLocked = !Act1PacingService.IsReached(s, "belts_open") && !BeltUnlocked();
            return entered && stillLocked
                ? StepOutcome.Done($"“第一次远征”记在第 {Act1PacingService.Find(s, "first_expedition").Tick} 步（{Act1PacingService.FormatMinute(Act1PacingService.MinuteOf(s, "first_expedition"))}）；人在破碎都市，传送带仍锁着")
                : StepOutcome.Fail($"进了破碎都市：第一次远征 {entered}、传送带仍锁 {stillLocked}");
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
            if (SignalPresence.CurrentMachineLogicId != b || GameRoot.FracturedCity?.PossessedMachineLogicId != b)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {FgjM3Common.Label(b)}");
            }
            return c.StepElapsed < 2.0 ? StepOutcome.Wait : StepOutcome.Done($"接入 {FgjM3Common.Label(b)}（准备开到撤离点）");
        }

        /// <summary>走回家园（与 M4 / M5 共用 FgjM1Journey.TickWalkHome）；还在返程路上时传送带不能先开放（“回到家园才开放”的负向）。</summary>
        private static StepOutcome TickWalkHome(JourneyContext c)
        {
            if (ExpeditionMarchService.IsMarching(St) && Act1PacingService.IsReached(St, "belts_open"))
            {
                return StepOutcome.Fail("远征队还在返程路上，基础传送带就开放了");
            }
            return FgjM1Journey.TickWalkHome(c);
        }

        private static StepOutcome TickBeltsOpen(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            ActPaceRecord rec = Act1PacingService.Find(s, "belts_open");
            if (rec == null)
            {
                return c.StepElapsed < 10 ? StepOutcome.Wait : StepOutcome.Fail("回到家园 10 秒后基础传送带仍没开放");
            }
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait; // 指引任务在到达那一刻 Recompute，通知中心下一帧显示
            }
            string title = GameText.Get("pace.belts_open.title");
            bool notified = NotificationCenter.History.Any(e => e.Type?.Id == Act1PacingService.NotifyOpen && (e.Text ?? string.Empty).Contains(title));
            bool guide = CampaignObjectiveTracker.FindQuest(s, "guide.belt") != null;
            bool codex = MechanicCodex.IsUnlocked("codex.logistics.belt");
            bool afterHome = rec.Tick >= c.GetLong("walk.tick") - 2L * GameClock.StepHz; // 到家那一帧里走了几步，节奏检查可能落在其中（返程路上没开放由 walk_home 每帧核对）
            return notified && guide && codex && BeltUnlocked() && afterHome && !rec.Migrated
                ? StepOutcome.Done($"“基础传送带开放”记在第 {rec.Tick} 步（到家第 {c.GetLong("walk.tick")} 步之后，{Act1PacingService.FormatMinute(Act1PacingService.MinuteOf(s, "belts_open"))}）：“新系统”通知、图鉴“传送带”、传送带指引任务都有，传送带 T1 可建")
                : StepOutcome.Fail($"基础传送带开放后：通知 {notified}、指引任务 {guide}、图鉴 {codex}、可建 {BeltUnlocked()}、到家之后才记 {afterHome}（第 {rec.Tick} 步）、不是迁移补记 {!rec.Migrated}");
        }
    }
}
