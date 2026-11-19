using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Endgame;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
using GameLogic.Settings;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using GameLogic.View;
using TEngine;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG13-END-06 结局、结算与纪念的自动验收（FG11 FGR-END-030～032；FG13 FGU-42；验收 FGT-END-008“统计正确；结语随统计变化；继续进入沙盒”；
    /// 卡片“演出可以跳过、可以重看”“负向：结算时存档”；承接 DEBT-FG9NAR01-01 结局镜头、DEBT-FG7ENV04-04 结局专属一句）。
    /// 起真实系统：真实世界与家园、真实机器登记 / 阵亡入口（黑匣子纪念墙）、真实突袭导演清理（击退计数）、真演出播放器与真结算面板（真 UXML，逐帧 Tick、点按钮）、
    /// 真文件存读档（演出前 / 结局落盘 / 后日谈三份自动存档从磁盘读回核对，结算期间存档被拒、磁盘文件不变）、真镜头导演（临时相机）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgEndingSelfCheck）；单段：bash tools/unity-validate.sh --segment FgEndingSelfCheck。
    /// </summary>
    public static partial class FgMegastructureSelfCheck
    {
        private static EndingSettlementPanelUIToolkit _settle;
        private static GameObject _settleGo;
        private static VisualElement _settleRoot;
        private static int _menuCalls;

        public static int RunEnding(StringBuilder report) =>
            RunCore(report, "结局", "\n[结局] 先遣旗舰被击退 → 结局演出（镜头转向天空、跳过 / 重看）→ 结局落盘 → 结算与纪念（幸存机器、纪念墙、五项统计、随统计变化的结语）→ 继续进入后日谈 / 返回主菜单；结算期间不存档（FG13-END-06，FGT-END-008）",
                new Action[] { EndingSetup, CheckEndingData, CheckEndingFlow, CheckEndingEpilogue, CheckEndingQuitAndReload, CheckEndingCamera, CheckEndingNegatives, CheckEndingTiming, CheckEndingLayout, EndingTeardown });

        public static void RunEndingFromMenu()
        {
            var report = new StringBuilder();
            int fail = RunEnding(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        private static void EndingSetup()
        {
            RevealSetup();
            EndingService.ResetForTests();
            EndingService.ReturnToMenuHandler = () => _menuCalls++;
            NarrativeCutscenePlayerUIToolkit.InWorldOverrideForTests = true;
            EndingSettlementPanelUIToolkit.InWorldOverrideForTests = true;
            VisualElement root = F.MountUxml(UiKitFolder + "EndingSettlement.uxml", out _settleGo);
            _settle = _settleGo.AddComponent<EndingSettlementPanelUIToolkit>();
            _settle.BindView(root);
            _settleRoot = root;
        }

        private static void EndingTeardown()
        {
            EndingService.AbortSettlement();
            EndingService.ResetForTests();
            EndingSettlementPanelUIToolkit.InWorldOverrideForTests = false;
            if (_settleGo != null)
            {
                Object.DestroyImmediate(_settleGo);
            }
            _settle = null;
            _settleGo = null;
            _settleRoot = null;
            UiEscapeStack.Clear();
            RevealTeardown();
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        private sealed class EndRig
        {
            public CampaignState S;
            public MachineRecord Named;
            public MachineRecord Linked;
            public MachineRecord Dead;
            public int RepelledBefore;
        }

        private const string BpEnd = "bp_selfcheck_ending";

        private static MachineRecord SpawnEnd(CampaignState s, Vector2 at)
        {
            if (s.BlueprintRecords == null || s.BlueprintRecords.All(b => b.BlueprintId != BpEnd))
            {
                BlueprintVersionRecord version = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>()).ToVersion(1, 0f);
                s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>())
                    .Append(new BlueprintRecord { BlueprintId = BpEnd, DisplayName = BpEnd, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
            }
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, BpEnd, HomeValleyLayout.RegionId, at, 100f, 100f, "Player", 1);
            if (!r.Success || !MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord m))
            {
                Fail("测试准备：登记机器失败：" + r.Message);
                return null;
            }
            MachineLoadoutRegistry.Register(s, r.LogicId, BpEnd, 1);
            return m;
        }

        /// <summary>一个打到先遣旗舰之前的战役：三台新机器（一台起名、一台被接入 9 次、一台阵亡进纪念墙）、两条已发现的熔合配方、两条结束的突袭（一条被全歼 = 击退、一条合并 = 不算）。</summary>
        private static EndRig EndFixture(int seed)
        {
            CampaignState s = NewWorld(seed);
            var rig = new EndRig { S = s };
            Vector2 core = HomeValleyLayout.Core.Position;
            rig.Named = SpawnEnd(s, core + new Vector2(6f, 0f));
            rig.Linked = SpawnEnd(s, core + new Vector2(-6f, 0f));
            rig.Dead = SpawnEnd(s, core + new Vector2(0f, 6f));
            if (rig.Named == null || rig.Linked == null || rig.Dead == null)
            {
                return null;
            }
            rig.Named.CustomName = "老铁";
            rig.Linked.SignalUplinkCount = 9; // 测试捷径：接入计数由 FG1-HUD-01 的 SignalUplinkService 正式写入（FgUplinkHudSelfCheck 覆盖）
            MachineRegistry.MarkDeadByLogicId(rig.Dead.LogicId); // 正式阵亡入口：纪念墙 / 黑匣子
            FusionState fu = s.Research.Fusion;
            fu.Discovered = FusionCatalog.Recipes.Take(2).Select(r => r.Id).ToArray(); // 测试捷径：配方发现由 FG5-RND-04 覆盖
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            rig.RepelledBefore = d.RepelledCount;
            d.Plans = d.Plans.Concat(new[]
            {
                new RaidPlanRecord { PlanId = "raid-end-a", State = RaidDirectorService.StateEnded, EndReason = RaidDirectorService.EndDestroyed },
                new RaidPlanRecord { PlanId = "raid-end-b", State = RaidDirectorService.StateEnded + 1, EndReason = RaidDirectorService.EndMerged },
            }).ToArray();
            WorldSimulation.StepMany(Hz); // 真实突袭导演推进：结束的计划移进历史、累加击退计数
            return rig;
        }

        /// <summary>先遣旗舰被击退（测试捷径：账本 + 事件，与 FlagshipService.Finish 同一对写法；首领战本身由 FgFlagshipSelfCheck 覆盖）。</summary>
        private static void DefeatFlagship(CampaignState s)
        {
            CampaignEventLedger.TryGrant(s, FlagshipService.DefeatedEventId, "Endgame", s.PlaySeconds, "flagship");
            GameEvent.Send(FlagshipService.DefeatedEvent);
        }

        private static DateTime SlotStamp() => File.Exists(CampaignSaveService.SlotPath(Slot)) ? File.GetLastWriteTimeUtc(CampaignSaveService.SlotPath(Slot)) : DateTime.MinValue;

        /// <summary>从旗舰被击退一路走到结算打开（演出跳过）。返回是否打开。</summary>
        private static bool ReachSettlement(CampaignState s)
        {
            DefeatFlagship(s);
            TickPlayer(0.016f);
            if (!NarrativeCutscenePlayerUIToolkit.IsShowing)
            {
                return false;
            }
            SkipWithConfirm();
            _settle.Tick();
            return EndingSettlementPanelUIToolkit.IsOpen;
        }

        // ── E0 数据 ───────────────────────────────────────────────────────────────

        private static void CheckEndingData()
        {
            bool entry = NarrativeCatalog.TryGet(EndingService.EntryId, out NarrativeEntryDef d) && d.IsCutscene && d.Camera == CutsceneCamera.ShotSky && d.Node && d.Act == 3
                         && d.Trigger == EndingService.Trigger && d.Lines.Count >= 4;
            string all = d == null ? string.Empty : string.Concat(d.Lines.Select(l => GameText.Get(l.TextKey)));
            string[] spec = { "主舰队仍停在轨道上", "人类仍在沉睡", "净化协议没有撤销", "还有东西可以归还", "先遣舰队被击退" };
            string[] missing = spec.Where(f => !all.Contains(f)).ToArray();
            string[] keys =
            {
                "ending.title", "ending.subtitle", "ending.stat.area", "ending.stat.days", "ending.stat.reactions", "ending.stat.fusion", "ending.stat.raids",
                "ending.epilogue.area", "ending.epilogue.area_none", "ending.epilogue.oldest", "ending.epilogue.none_alive", "ending.epilogue.linked", "ending.epilogue.linked_fallen",
                "ending.epilogue.close", "ending.epilogue.close_fallen", "ending.btn.continue", "ending.btn.menu", "ending.btn.replay", "save.blocked_settlement", "purify.ending_line",
            };
            string[] noKey = keys.Where(k => !GameText.Has(k)).ToArray();
            bool hook = GuidanceHooks.Known.Contains(GuidanceHooks.EndingFirstSettlement);
            bool uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "EndingSettlement.uxml") != null;
            Expect(entry && missing.Length == 0 && noKey.Length == 0 && hook && uxml,
                $"E0 数据：结局 node.ending 是第三幕剧情节点演出、触发 {EndingService.Trigger}、镜头 sky、{d?.Lines.Count ?? 0} 行字幕 {entry}；规格原意片段缺 [{string.Join(",", missing)}]；" +
                $"文本键缺 [{string.Join(",", noKey)}]；引导钩子 {hook}；结算 UXML {uxml}");
        }

        // ── E1 正式路径（FGT-END-008）─────────────────────────────────────────────

        private static void CheckEndingFlow()
        {
            EndRig rig = EndFixture(8601);
            if (rig == null)
            {
                return;
            }
            CampaignState s = rig.S;
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            bool raidCount = d.RepelledCount == rig.RepelledBefore + 1 && d.Plans.All(p => p.PlanId != "raid-end-a" && p.PlanId != "raid-end-b")
                             && EndingService.RaidsRepelled(s) == d.RepelledCount;

            // 1) 先遣旗舰被击退 → 结局演出待播 → 播放器开始播放，开始前自动存档（存档里记着待播）。
            DefeatFlagship(s);
            bool pending = EndingService.Pending(s) && !EndingService.Reached(s);
            TickPlayer(0.016f);
            bool playing = NarrativeCutscenePlayerUIToolkit.IsShowing && _revealPlayer.Cutscene?.Def.Id == EndingService.EntryId && !_revealPlayer.Cutscene.Replay
                           && CutsceneCamera.Shot == CutsceneCamera.ShotSky;
            CampaignState pre = ReadSlot(out string why0);
            bool preSave = pre != null && pre.LastSaveReason == SaveReason.CutsceneEnter && pre.Progress.PendingCutscene == EndingService.EntryId && !EndingService.Reached(pre);
            // 2) 首次观看跳过要确认；取消 = 继续看；确认 → 收进资料库 → 结局落盘（账本 + 自动存档）。
            bool clicked = ClickButton(_revealPlayer.SkipButton);
            bool dialog = UiConfirmDialog.IsOpen;
            UiConfirmDialog.Cancel();
            bool stillPlaying = NarrativeCutscenePlayerUIToolkit.IsShowing;
            var lines = new List<string>();
            int frames = 0;
            while (NarrativeCutscenePlayerUIToolkit.IsShowing && frames++ < 400)
            {
                TickPlayer(0.25f);
                if (NarrativeCutscenePlayerUIToolkit.IsShowing && !lines.Contains(_revealPlayer.LineText))
                {
                    lines.Add(_revealPlayer.LineText);
                }
            }
            bool watched = !NarrativeCutscenePlayerUIToolkit.IsShowing && NarrativeService.Has(s, EndingService.EntryId) && lines.Any(l => l.Contains("还有东西可以归还"));
            bool reached = EndingService.Reached(s) && EndingService.EndingCount == 1 && EndingService.EndingSaveCount == 1 && EndingService.LastEndingSave.Success;
            CampaignState post = ReadSlot(out string why1);
            bool postSave = post != null && post.LastSaveReason == SaveReason.EndingComplete && EndingService.Reached(post) && !post.Progress.IsPostgame
                            && string.IsNullOrEmpty(post.Progress.PendingCutscene) && NarrativeService.Has(post, EndingService.EntryId);
            Expect(raidCount && pending && playing && preSave && clicked && dialog && stillPlaying && watched && reached && postSave,
                $"E1a 结局演出（FGR-END-030）：击退计数 +1（合并不算）{raidCount}；旗舰被击退 → 待播 {pending}；播放器播放 + 镜头 sky {playing}；演出前自动存档（读回待播 node.ending）{preSave}（{why0}）；" +
                $"首次跳过弹确认 {clicked && dialog}、取消后继续看 {stillPlaying}；看完收进资料库、末行“还有东西可以归还” {watched}（{lines.Count} 行）；结局落盘（账本 + 自动存档 1 次）{reached}；" +
                $"读回结局存档（EndingComplete、已达成、未进后日谈、无待播）{postSave}（{why1}）");

            // 3) 结算打开：世界暂停、统计 / 名单 / 结语正确。
            DateTime stamp = SlotStamp();
            _settle.Tick();
            bool open = EndingSettlementPanelUIToolkit.IsOpen && _settle.PanelVisible && EndingService.SettlementOpen && Stage.GameRoot.IsWorldPaused;
            List<string> stats = _settle.StatTexts;
            long area = PurificationService.ReturnedArea(s);
            int days = s.Clock.Day > 0 ? s.Clock.Day : 1;
            int reactions = Campaign.Content.ReactionDiscoveryService.DiscoveredInteractionCount();
            bool statOk = stats.Count == 5
                          && stats[0] == GameText.Format("ending.stat.area", area.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
                          && stats[1] == GameText.Format("ending.stat.days", days)
                          && stats[2] == GameText.Format("ending.stat.reactions", reactions)
                          && stats[3] == GameText.Format("ending.stat.fusion", 2)
                          && stats[4] == GameText.Format("ending.stat.raids", rig.RepelledBefore + 1);
            List<MachineRecord> alive = MachineRegistry.AllRecords.Where(m => m.IsAlive).OrderBy(m => m.DisplayNumber).ToList();
            List<string> surv = _settle.SurvivorTexts;
            List<string> mem = _settle.MemorialTexts;
            bool survOk = surv.Count == alive.Count && surv.Contains(EndingService.SurvivorText(s, rig.Named)) && surv[0] == EndingService.SurvivorText(s, alive[0])
                          && surv.Any(t => t.Contains("老铁")) && !surv.Contains(EndingService.SurvivorText(s, rig.Dead));
            bool memOk = mem.Count == MachineRegistry.AllRecords.Count(m => !m.IsAlive) && mem.Contains(BlackBoxService.MemorialText(s, rig.Dead));
            List<string> epi = _settle.EpilogueTexts;
            MachineRecord oldest = alive.FirstOrDefault();
            bool epiOk = epi.Count >= 4 && epi.Any(t => oldest != null && t.Contains(MachineNaming.Long(oldest)))
                         && epi.Any(t => t.Contains(MachineNaming.Long(rig.Linked)) && t.Contains("9"))
                         && epi.Last() == GameText.Format("ending.epilogue.close_fallen", mem.Count)
                         && epi.All(t => !t.Contains("{") && t.Length > 0);
            Expect(open && statOk && survOk && memOk && epiOk,
                $"E1b 结算与纪念（FGR-END-031，FGT-END-008 统计正确）：打开且世界暂停 {open}；五项统计与数据源一致（面积 {area}、第 {days} 天、反应 {reactions}、配方 2、击退 {rig.RepelledBefore + 1}）{statOk} [{string.Join(" | ", stats)}]；" +
                $"幸存 {surv.Count}/{alive.Count} 台含起名的“老铁”、不含阵亡的 {survOk}；纪念墙 {mem.Count} 台含阵亡机器的纪念文本 {memOk}；结语（最老的 / 被接入最多 9 次 / 纪念墙人数）{epiOk} [{string.Join(" | ", epi)}]");

            // 4) 负向“结算时存档”：自动档、暂停菜单“保存并返回主菜单”都被拒、磁盘文件不变；Esc 不关结算。
            SaveResult auto = CampaignAutoSaveService.SaveAuto(SaveReason.RaidWarning, toast: false);
            SaveResult quit = PauseMenuUIToolkit.SaveForQuit(Slot);
            SaveResult cut = CampaignAutoSaveService.SaveAuto(SaveReason.CutsceneEnter, toast: false);
            bool esc = UiEscapeStack.CloseTop() && EndingSettlementPanelUIToolkit.IsOpen;
            bool blocked = auto.Outcome == SaveOutcome.SettlementOpen && quit.Outcome == SaveOutcome.SettlementOpen && cut.Outcome == SaveOutcome.SettlementOpen
                           && SlotStamp() == stamp && EndingService.BlockedSaves >= 3;
            // 5) 重看结局演出（结算里的按钮）：播放器盖在结算上，重看不存档、不改资料库；看完回到结算，仍不存档。
            int filed = s.Progress.Narrative.Length;
            int cutSaves = NarrativeService.CutsceneSaveCount;
            int rc0 = NarrativeService.ReplayCount;
            bool replayClicked = ClickButton(_settle.ReplayButton);
            string replayWhy = $"重看次数 {rc0}→{NarrativeService.ReplayCount}、正在播 {NarrativeService.Playing?.Def.Id}";
            bool replaying = NarrativeCutscenePlayerUIToolkit.IsShowing && _revealPlayer.Cutscene.Replay && _revealPlayer.Cutscene.Def.Id == EndingService.EntryId && !NarrativeService.SkipNeedsConfirm;
            _settle.Tick();
            bool escSkips = UiEscapeStack.Top == (object)_revealPlayer; // 重看期间 Esc 先落到演出（跳过重看），不被结算的阻挡层吞掉
            ClickButton(_revealPlayer.SkipButton); // 重看直接跳过，不弹确认
            _settle.Tick();
            escSkips &= UiEscapeStack.CloseTop() && EndingSettlementPanelUIToolkit.IsOpen; // 回到结算后 Esc 又被吞掉
            bool back = !NarrativeCutscenePlayerUIToolkit.IsShowing && !UiConfirmDialog.IsOpen && EndingSettlementPanelUIToolkit.IsOpen && s.Progress.Narrative.Length == filed
                        && NarrativeService.CutsceneSaveCount == cutSaves && SlotStamp() == stamp && Stage.GameRoot.IsWorldPaused;
            Expect(blocked && esc && replayClicked && replaying && back && escSkips,
                $"E1c 负向“结算时存档”与重看：自动档 / 保存并返回主菜单 / 演出档都返回 SettlementOpen、磁盘文件不变 {blocked}（{auto.Outcome}/{quit.Outcome}/{cut.Outcome}）；Esc 不关结算 {esc}；" +
                $"结算里重看结局演出（重看标记、跳过不确认）{replayClicked && replaying}（{replayWhy}）；看完回到结算、资料库与存档都不变、世界仍暂停 {back}；重看时 Esc 落到演出、回到结算后 Esc 再被吞 {escSkips}");

            // 6) 继续 → 后日谈：结算关、世界恢复、IsPostgame、之后的自动存档写盘（读回 PostgameStart + 后日谈）；之后不再打开结算。
            bool cont = ClickButton(_settle.ContinueButton);
            _settle.Tick();
            CampaignState pg = ReadSlot(out string why2);
            SaveResult after = CampaignAutoSaveService.SaveAuto(SaveReason.RaidWarning, toast: false);
            bool postgame = cont && !EndingSettlementPanelUIToolkit.IsOpen && !EndingService.SettlementOpen && s.Progress.IsPostgame && !Stage.GameRoot.IsWorldPaused
                            && pg != null && pg.LastSaveReason == SaveReason.PostgameStart && pg.Progress.IsPostgame && EndingService.Reached(pg) && after.Success && _menuCalls == 0
                            && CampaignSaveService.GetSlotMetadata(Slot).IsPostgame;
            Expect(postgame, $"E1d 继续进入后日谈（FGR-END-032，FGT-END-008“继续进入沙盒”）：结算关闭、世界恢复、IsPostgame、读回 PostgameStart 存档且存档卡显示后日谈、之后存档恢复正常、没有回主菜单 {postgame}（{why2}）");
        }

        // ── E2 结语随统计变化（FGR-END-031）──────────────────────────────────────

        private static void CheckEndingEpilogue()
        {
            EndRig rig = EndFixture(8602);
            if (rig == null)
            {
                return;
            }
            CampaignState s = rig.S;
            EndingStats a = EndingService.Stats(s);
            bool noMilestone = !a.EpilogueKeys.Contains("purify.ending_line") && a.EpilogueKeys.Contains("ending.epilogue.oldest") && a.EpilogueKeys.Contains("ending.epilogue.linked")
                               && a.MostLinked == rig.Linked && a.Oldest == MachineRegistry.AllRecords.Where(m => m.IsAlive).OrderBy(m => m.DisplayNumber).First();
            // 归还十万格（DEBT-FG7ENV04-04）：达成“成就”里程碑后多出专属一句（文本读 PurificationService.EndingLineKey）。
            ReturnMilestoneDef top = PurificationService.AllMilestones.FirstOrDefault(m => m.Reward == PurificationService.RewardAchievement);
            PurificationState pd = PurificationService.StateOf(s);
            if (top != null)
            {
                pd.Reached = (pd.Reached ?? Array.Empty<string>()).Append(top.Id).ToArray();
            }
            EndingStats b = EndingService.Stats(s);
            int li = b.EpilogueKeys.IndexOf("purify.ending_line");
            bool milestone = top != null && top.Cells == 100_000 && li >= 0 && b.Epilogue[li] == GameText.Get("purify.ending_line");
            // 被接入最多的那台阵亡 → 换成“没能看到今天”的那一句；全部阵亡 → 没有“最老的”，换成“没有一台活到今天”。
            MachineRegistry.MarkDeadByLogicId(rig.Linked.LogicId);
            EndingStats c = EndingService.Stats(s);
            bool linkedFallen = c.EpilogueKeys.Contains("ending.epilogue.linked_fallen") && !c.EpilogueKeys.Contains("ending.epilogue.linked") && c.MostLinked == rig.Linked;
            foreach (MachineRecord m in MachineRegistry.AllRecords.Where(m => m.IsAlive).ToList())
            {
                MachineRegistry.MarkDeadByLogicId(m.LogicId);
            }
            EndingStats e = EndingService.Stats(s);
            bool none = e.Oldest == null && e.Survivors.Count == 0 && e.EpilogueKeys.Contains("ending.epilogue.none_alive") && !e.EpilogueKeys.Contains("ending.epilogue.oldest")
                        && e.Fallen.Count == MachineRegistry.AllRecords.Count && e.Epilogue.Last() == GameText.Format("ending.epilogue.close_fallen", e.Fallen.Count);
            bool differ = !a.Epilogue.SequenceEqual(b.Epilogue) && !b.Epilogue.SequenceEqual(c.Epilogue) && !c.Epilogue.SequenceEqual(e.Epilogue);
            // 面积为 0 的档用“还没有一格被洗净”的那一句；同一份档把 3 格“生成时有污染”的格子洗到 0 级（格网唯一写入口 SetPollution）→ 面积 3、换成带面积与天数的那一句（正常通关的情形）。
            bool areaNone = a.ReturnedArea == 0 && a.EpilogueKeys[0] == "ending.epilogue.area_none" && a.Epilogue[0] == GameText.Format("ending.epilogue.area_none", "0", a.Days);
            HomeGridMap map = HomeGridService.MapFor(s);
            GridCell origin = GridCell.FromWorld(HomeValleyLayout.Core.Position + new Vector2(20f, 20f));
            for (int i = 0; i < 3; i++)
            {
                var cell = new GridCell(origin.X + i, origin.Y);
                map.SetPollution(cell, (byte)(map.GetPollution(cell) == 1 ? 2 : 1)); // 先改一次：区块记下生成基线（面积此时为 0，这一步不会动面积）
                HomeGridMap.Chunk ch = map.ChunkAt(cell, out int idx);
                ch.BasePollution[idx] = 1; // 测试捷径：生成基线有污染；净化塔逐级降污由 FgPurificationSelfCheck 覆盖
                map.SetPollution(cell, 1);
                map.SetPollution(cell, 0);
            }
            EndingStats f = EndingService.Stats(s);
            string areaText = GameText.Format("ending.epilogue.area", 3L.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), f.Days);
            bool areaSome = f.ReturnedArea == 3 && f.EpilogueKeys[0] == "ending.epilogue.area" && f.Epilogue[0] == areaText && areaText.Contains("3") && areaText.Contains(f.Days.ToString());
            bool areaLine = areaNone && areaSome;
            Expect(noMilestone && milestone && linkedFallen && none && differ && areaLine,
                $"E2 结语随统计变化（FGR-END-031，FGT-END-008）：未达十万格无专属句、有最老 / 被接入最多 {noMilestone}；达成十万格里程碑后多出 purify.ending_line {milestone}；" +
                $"被接入最多的阵亡 → 换句 {linkedFallen}；全部阵亡 → 没有最老、换“没有一台活到今天” {none}；四种情形结语互不相同 {differ}；面积 0 →“还没有一格被洗净” {areaNone}；洗净 3 格 → 面积 {f.ReturnedArea}、“{(f.Epilogue.Count > 0 ? f.Epilogue[0] : "")}” {areaSome}");
        }

        // ── E3 中途退出与读档 ─────────────────────────────────────────────────────

        private static void CheckEndingQuitAndReload()
        {
            EndRig rig = EndFixture(8603);
            if (rig == null)
            {
                return;
            }
            CampaignState s = rig.S;
            // 演出中途退出：读回演出前的存档 → 重新播放（结局没落盘）。
            DefeatFlagship(s);
            TickPlayer(0.016f);
            bool playing = NarrativeCutscenePlayerUIToolkit.IsShowing;
            QuitToDisk();
            TickPlayer(0.016f); // 世界卸载：播放器中止、不收录
            bool abortedClean = !NarrativeCutscenePlayerUIToolkit.IsShowing && !EndingService.Reached(s);
            CampaignState r1 = ReadSlot(out string w1);
            bool replays = false;
            if (r1 != null)
            {
                Enter(r1);
                TickPlayer(0.016f);
                replays = NarrativeCutscenePlayerUIToolkit.IsShowing && _revealPlayer.Cutscene?.Def.Id == EndingService.EntryId && !_revealPlayer.Cutscene.Replay && !EndingService.Reached(r1);
                SkipWithConfirm();
            }
            // 结算里返回主菜单：不存档、调回菜单；读回 → 结局已达成、未进后日谈 → 直接回到结算（不重播演出）。
            DateTime stamp = SlotStamp();
            _settle.Tick();
            bool open = EndingSettlementPanelUIToolkit.IsOpen;
            bool menu = ClickButton(_settle.MenuButton) && _menuCalls == 1 && !EndingService.SettlementOpen && SlotStamp() == stamp;
            _menuCalls = 0;
            CampaignState r2 = ReadSlot(out string w2);
            bool reopen = false;
            bool repSaved = false;
            if (r2 != null)
            {
                QuitToDisk();
                Enter(r2);
                TickPlayer(0.016f);
                bool noReplay = !NarrativeCutscenePlayerUIToolkit.IsShowing && string.IsNullOrEmpty(r2.Progress.PendingCutscene);
                _settle.Tick();
                reopen = noReplay && EndingSettlementPanelUIToolkit.IsOpen && EndingService.SettlementDue(r2) && _settle.Shown != null && _settle.Shown.Fallen.Count >= 1;
                // 击退计数器真进了存档（直接读字段，不经历史兜底）：读回 = 存档前（夹具里击退 1 条）。
                repSaved = RaidDirectorService.StateOf(r2).RepelledCount == rig.RepelledBefore + 1 && RaidDirectorService.StateOf(s).RepelledCount == rig.RepelledBefore + 1;
                ClickButton(_settle.ContinueButton);
            }
            // 旧档：旗舰已被击退、结局还没投放（本卡之前的档）→ 进入战役时补投放结局演出。
            CampaignState old = NewWorld(8604);
            // 计数器之前的旧档：RepelledCount = 0、历史已满且全是击退 → 第一次突袭结算先补下限再累加，历史挤掉旧条目后也不少算。
            RaidDirectorState od = RaidDirectorService.StateOf(old);
            int hmax = RaidCatalog.HistoryMax;
            od.RepelledCount = 0;
            od.History = Enumerable.Range(0, hmax).Select(i => new RaidHistoryRecord { PlanId = "raid-old-" + i, EndReason = RaidDirectorService.EndDestroyed }).ToArray();
            od.Plans = od.Plans.Append(new RaidPlanRecord { PlanId = "raid-old-new", State = RaidDirectorService.StateEnded, EndReason = RaidDirectorService.EndWithdrawn }).ToArray();
            bool oldFloor = EndingService.RaidsRepelled(old) == hmax;
            WorldSimulation.StepMany(Hz);
            bool backfill = oldFloor && od.History.Length == hmax && od.RepelledCount == hmax + 1 && EndingService.RaidsRepelled(old) == hmax + 1;
            CampaignEventLedger.TryGrant(old, FlagshipService.DefeatedEventId, "Endgame", old.PlaySeconds, "flagship");
            bool before = !EndingService.Pending(old);
            NarrativeService.OnCampaignEntered(old);
            bool oldSave = before && EndingService.Pending(old);
            NarrativeService.FinishPlaying(false);
            Expect(playing && abortedClean && replays && open && menu && reopen && repSaved && backfill && oldSave,
                $"E3 中途退出与读档：演出中退出 → 读回演出前存档重新播放（结局未落盘）{replays}（{w1}）；结算“返回主菜单”不存档、走回菜单入口 {open && menu}；" +
                $"读回结局存档 → 不重播演出、直接回到结算 {reopen}（{w2}）；击退计数器存读档一致 {repSaved}；无计数器旧档（历史 {hmax} 条全是击退）补下限后累加、历史挤掉旧条目也不少算 {backfill}（{od.RepelledCount}）；旧档（旗舰已击退、无结局）进入战役补投放 {oldSave}");
        }

        /// <summary>退出游戏（同 R2）：世界卸载、会话清空，不写存档。</summary>
        private static void QuitToDisk()
        {
            WorldSimulation.UnloadAll();
            CampaignSession.Clear();
            GameClock.ResetSession();
        }

        /// <summary>读档进入战役（同 R2）：设会话、载入家园、叙事 / 结局补投放。</summary>
        private static void Enter(CampaignState l)
        {
            CampaignSession.Set(Slot, l);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            NarrativeService.OnCampaignEntered(l);
        }

        // ── E4 结局镜头（DEBT-FG9NAR01-01 结局部分；同 FgRevealSelfCheck R3 做法）─────────────

        private static void CheckEndingCamera()
        {
            CampaignState s = NewWorld(8605);
            var camGo = new GameObject("__FgEndingCamera") { hideFlags = HideFlags.HideAndDontSave };
            Camera cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 20f;
            camGo.transform.SetPositionAndRotation(new Vector3(3f, 30f, -18f), Quaternion.Euler(58f, 30f, 0f));
            Func<float> originalDt = CameraDirector.RealDeltaTime;
            CameraDirector.RealDeltaTime = () => 0.05f;
            var director = new CameraDirector();
            InputRouter.DebugSetReader(new IdleReader());
            bool ok = false;
            string detail = string.Empty;
            try
            {
                CutsceneCamera.ResetSession();
                director.Bind(cam, (out float2 a) => { a = float2.zero; return false; }, new Vector3(0f, 30f, -18f), 60f, startInStrategy: true);
                director.Tick(false);
                float elev = CutsceneCamera.ElevationDegrees;
                int frames = Mathf.CeilToInt(CutsceneCamera.Seconds / 0.05f) + 4;
                for (int i = 0; i < frames; i++)
                {
                    director.Tick(true); // 先让镜头导演自己的位置收敛（与演出无关），再取基准：只核演出带来的变化
                }
                Quaternion baseRot = cam.transform.rotation;
                Vector3 basePos = cam.transform.position;
                DefeatFlagship(s);
                TickPlayer(0.016f);
                for (int i = 0; i < frames; i++)
                {
                    director.Tick(true);
                }
                Vector3 f = cam.transform.forward;
                Vector3 bf = baseRot * Vector3.forward;
                float dot = Vector3.Dot(new Vector3(f.x, 0f, f.z).normalized, new Vector3(bf.x, 0f, bf.z).normalized);
                float moved = (cam.transform.position - basePos).magnitude;
                bool sky = f.y > Mathf.Sin(elev * Mathf.Deg2Rad) - 0.02f && dot > 0.995f && moved < 0.01f;
                GameClock.SetSpeed(1f);
                SkipWithConfirm();
                for (int i = 0; i < frames; i++)
                {
                    director.Tick(true);
                }
                bool restored = Quaternion.Angle(cam.transform.rotation, baseRot) < 0.05f && !CutsceneCamera.Active;
                // 重看（结算 / 资料库）同一套镜头。
                int begins = CutsceneCamera.Begins;
                NarrativeService.BeginReplay(s, EndingService.EntryId);
                for (int i = 0; i < frames; i++)
                {
                    director.Tick(true);
                }
                bool replaySky = CutsceneCamera.Begins == begins + 1 && cam.transform.forward.y > Mathf.Sin(elev * Mathf.Deg2Rad) - 0.02f;
                NarrativeService.FinishPlaying(true);
                ok = sky && restored && replaySky;
                detail = $"演出中镜头转向天空（视线 y={f.y:0.###}（仰角 {elev:0}）、朝向点积 {dot:0.####}、位移 {moved:0.###}、水平朝向与位置不变；倍速无关由 FgRevealSelfCheck R3 同一套镜头代码覆盖）{sky}；跳过后复原 {restored}；重看同一套镜头 {replaySky}";
            }
            finally
            {
                director.Unbind(resetInput: false);
                CameraDirector.RealDeltaTime = originalDt;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                CutsceneCamera.ResetSession();
                NarrativeService.AbortPlaying();
                GameClock.SetSpeed(1f);
                Object.DestroyImmediate(camGo);
            }
            // 结算打开时世界已在播放器收尾后暂停 / 恢复由 E1 / E6 覆盖；这里收起可能打开的结算。
            EndingService.AbortSettlement();
            Expect(ok, "E4 结局镜头（FGR-END-030“镜头转向天空：主舰队仍停在轨道上”，DEBT-FG9NAR01-01 结局部分）：" + detail);
        }

        // ── E5 负向矩阵 ─────────────────────────────────────────────────────────────

        private static void CheckEndingNegatives()
        {
            CampaignState s = NewWorld(8606);
            int endings0 = EndingService.EndingCount;
            // 旗舰没被击退：事件来了也不投放；结算不会打开；继续 / 返回主菜单无效。
            GameEvent.Send(FlagshipService.DefeatedEvent);
            bool noDefeat = !EndingService.Pending(s) && !EndingService.Begin(s) && !EndingService.SettlementDue(s) && !EndingService.OpenSettlement(s)
                            && !EndingService.ContinuePostgame() && !EndingService.ReturnToMenu();
            _settle.Tick();
            noDefeat &= !EndingSettlementPanelUIToolkit.IsOpen;
            // 击退两次：只投放一次。
            DefeatFlagship(s);
            GameEvent.Send(FlagshipService.DefeatedEvent);
            bool once = EndingService.Pending(s) && !EndingService.Begin(s);
            // 演出还在播 / 待播时不打开结算（结局还没落盘）。
            TickPlayer(0.016f);
            _settle.Tick();
            bool notDuringCutscene = NarrativeCutscenePlayerUIToolkit.IsShowing && !EndingSettlementPanelUIToolkit.IsOpen && !EndingService.SettlementDue(s);
            SkipWithConfirm();
            // 确认框开着时不打开（等它关）。
            UiConfirmDialog.Show(new ConfirmRequest { Title = "test" });
            _settle.Tick();
            bool notDuringDialog = !EndingSettlementPanelUIToolkit.IsOpen;
            UiConfirmDialog.DiscardAll();
            _settle.Tick();
            bool opens = EndingSettlementPanelUIToolkit.IsOpen;
            // 别的战役状态的存档不受这份结算拦截；已打开时再开一次无效；结局只落盘一次。
            CampaignState other = CampaignState.CreateNew("fgend-other", "Standard", 1);
            bool otherFree = !EndingService.BlocksSave(other) && !EndingService.OpenSettlement(s) && EndingService.EndingCount == endings0 + 1;
            // 再次击退（账本已有）不重播、不重开。
            GameEvent.Send(FlagshipService.DefeatedEvent);
            bool noRepeat = !EndingService.Pending(s) && EndingService.Reached(s);
            // 战役被换掉（读别的档 / 回主菜单）：结算收起、不再拦截存档、世界恢复。
            CampaignSession.Set(Slot, other);
            _settle.Tick();
            bool aborted = !EndingSettlementPanelUIToolkit.IsOpen && !EndingService.SettlementOpen && !Stage.GameRoot.IsWorldPaused;
            CampaignSession.Set(Slot, s);
            // 已进后日谈的档不再打开结算。
            s.Progress.IsPostgame = true;
            _settle.Tick();
            bool postgameNo = !EndingSettlementPanelUIToolkit.IsOpen && !EndingService.SettlementDue(s);
            Expect(noDefeat && once && notDuringCutscene && notDuringDialog && opens && otherFree && noRepeat && aborted && postgameNo,
                $"E5 负向：没击退不投放 / 不开结算 / 按钮无效 {noDefeat}；击退两次只投放一次 {once}；演出中不开结算 {notDuringCutscene}；确认框开着不开 {notDuringDialog}、关了才开 {opens}；" +
                $"别的战役不受拦截、重复打开无效、结局只落盘一次 {otherFree}；再次击退不重播 {noRepeat}；战役被换掉 → 结算收起、恢复存档与世界 {aborted}；后日谈不再开结算 {postgameNo}");
        }

        // ── E7 布局探针（FGU-42；unity-ui-toolkit.md 验收：四种分辨率 + 超长文字 + USS 体检）───────────────

        /// <summary>中英文各开一次真结算（Render 出的真文字），复制到探针树，两列名单各压 40 行超长名字，按钮 / 提示用真文字（英文较长）。</summary>
        private static void CheckEndingLayout()
        {
            GameLanguage original = GameSettings.Language;
            var parts = new List<string>();
            bool all = true;
            try
            {
                foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
                {
                    GameSettings.SetLanguage(lang);
                    EndRig rig = EndFixture(lang == GameLanguage.En ? 8608 : 8607);
                    if (rig == null)
                    {
                        return;
                    }
                    bool open = ReachSettlement(rig.S);
                    string probe = UiToolkitLayoutProbe.Probe(UiKitFolder + "EndingSettlement.uxml", "EsWindow", stressFill: true, prepare: CopySettlementInto);
                    bool ok = open && probe.Contains("PASS") && !probe.Contains("FAIL");
                    all &= ok;
                    parts.Add($"{lang} 打开 {open}：" + (probe.Contains("FAIL") || !open ? probe : probe.Split('\n')[0]));
                    ClickButton(_settle.ContinueButton);
                }
            }
            finally
            {
                GameSettings.SetLanguage(original);
            }
            Expect(all, "E7 结算面板布局探针（四种分辨率 + 长名单 + 中英文真文字 + USS 体检）：" + string.Join("；", parts));
        }

        private static void CopySettlementInto(VisualElement target)
        {
            VisualElement probeRoot = target.parent ?? target;
            // 单行文字：标题、副标题、各节标题、提示、三个按钮——取真面板当前语言下 Render 出的文字。
            foreach (string n in new[] { "EsTitle", "EsSubtitle", "EsStatsHead", "EsEpilogueHead", "EsSurvivorsHead", "EsMemorialHead", "EsHint", "EsReplay", "EsMenu", "EsContinue" })
            {
                TextElement src = _settleRoot?.Q<TextElement>(n);
                TextElement dst = probeRoot.Q<TextElement>(n);
                if (src != null && dst != null)
                {
                    dst.text = src.text;
                }
            }
            // 行容器：照抄真行（同样的样式类）；两列名单再各补 40 行超长名字（名单条数不定）。
            string longName = string.Concat(Enumerable.Repeat("Wasteland-Reclaimer-Mk.IV ", 4));
            foreach (string n in new[] { "EsStats", "EsEpilogue", "EsSurvivors", "EsMemorial" })
            {
                VisualElement src = _settleRoot?.Q<VisualElement>(n);
                VisualElement dst = probeRoot.Q<VisualElement>(n);
                if (src == null || dst == null)
                {
                    continue;
                }
                dst.Clear();
                foreach (VisualElement row in src.Children())
                {
                    if (row is Label l)
                    {
                        var copy = new Label(l.text);
                        foreach (string c in l.GetClasses())
                        {
                            copy.AddToClassList(c);
                        }
                        dst.Add(copy);
                    }
                }
                if (n == "EsSurvivors" || n == "EsMemorial")
                {
                    for (int i = 0; i < 40; i++)
                    {
                        var row = new Label($"{longName}#{1000 + i} · {longName}");
                        row.AddToClassList("es-row");
                        dst.Add(row);
                    }
                }
            }
        }

        // ── E6 暂停与 0.5x～3x ───────────────────────────────────────────────────────

        private static void CheckEndingTiming()
        {
            var results = new List<string>();
            bool all = true;
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                foreach (bool pausedBefore in new[] { false, true })
                {
                    EndRig rig = EndFixture(8610 + (int)(speed * 10) + (pausedBefore ? 1 : 0));
                    if (rig == null)
                    {
                        return;
                    }
                    CampaignState s = rig.S;
                    GameClock.SetSpeed(speed);
                    Stage.GameRoot.SetWorldPaused(pausedBefore);
                    bool open = ReachSettlement(s);
                    EndingStats first = _settle.Shown;
                    long ticks = GameClock.Ticks;
                    for (int i = 0; i < 30; i++)
                    {
                        _settle.Tick();
                    }
                    bool held = EndingSettlementPanelUIToolkit.IsOpen && Stage.GameRoot.IsWorldPaused && GameClock.Ticks == ticks
                                && CampaignAutoSaveService.SaveAuto(SaveReason.RaidWarning, toast: false).Outcome == SaveOutcome.SettlementOpen;
                    EndingStats again = EndingService.Stats(s);
                    bool same = first != null && again.Days == first.Days && again.RaidsRepelled == first.RaidsRepelled && again.Epilogue.SequenceEqual(first.Epilogue);
                    ClickButton(_settle.ContinueButton);
                    bool restored = !EndingService.SettlementOpen && Stage.GameRoot.IsWorldPaused == pausedBefore && Mathf.Approximately(GameClock.Speed, speed) && s.Progress.IsPostgame;
                    bool ok = open && held && same && restored;
                    all &= ok;
                    results.Add($"{speed}x{(pausedBefore ? "+先暂停" : string.Empty)}:{(ok ? "✓" : $"✗(开{open}/停{held}/同{same}/复原{restored})")}");
                    Stage.GameRoot.SetWorldPaused(false);
                    GameClock.SetSpeed(1f);
                }
            }
            Expect(all, $"E6 暂停与倍速矩阵：结算期间世界暂停、时钟不走、存档被拒、统计不变；继续后恢复到打开前的暂停状态与倍速 [{string.Join(" ", results)}]");
        }
    }
}
