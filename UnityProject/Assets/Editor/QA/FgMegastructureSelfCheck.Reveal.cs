using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Endgame;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Narrative;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Beacon;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using F = GameLogic.EditorTools.FgProductionSelfCheck;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG13-END-02 揭示的自动验收（FG11 FGR-END-010；第 8 节“演出可以跳过、可以在资料库重看”“玩家不建信标：没有时间压力”；第 9 节“揭示演出中退出游戏 → 读档后回到演出前的自动存档”；
    /// 验收 FGT-END-003“揭示：状态变化、前后自动存档”；承接 DEBT-FG9NAR01-01 的揭示部分：游戏内镜头断言）。
    /// 全部起真实系统：真实世界与家园、真实返航工地五个阶段从“差一点”走完（真实 Complete → AllCompletedEvent）、真 UXML 生产面板点“点亮信标…”、信标确认面板的“确认点亮”入口、
    /// 真演出播放器（真 UXML，逐帧 Tick、点跳过 + 确认框）、真文件存读档（演出前 / 后两份自动存档从磁盘读回核对）、真任务系统、真音乐导演、真 UXML 顶栏、真镜头导演（临时相机）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgRevealSelfCheck）；单段：bash tools/unity-validate.sh --segment FgRevealSelfCheck。
    /// </summary>
    public static partial class FgMegastructureSelfCheck
    {
        private static NarrativeCutscenePlayerUIToolkit _revealPlayer;

        /// <summary>没有任何按键、鼠标在屏幕外的输入替身（镜头断言不受真鼠标影响）。</summary>
        private sealed class IdleReader : IInputReader
        {
            public bool GetKey(KeyCode key) => false;
            public bool GetKeyDown(KeyCode key) => false;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }
        private static GameObject _revealPlayerGo;

        public static int RunReveal(StringBuilder report) =>
            RunCore(report, "揭示", "\n[揭示] 信标点火 → 点亮信标二次确认 → 揭示演出（镜头转向天空、三个正式名、伏笔接上）→ 轨道威胁 HUD / 任务更新 / 音乐切换 → 演出前后各自动存档；跳过、重看、演出中退出、负向、倍速（FG13-END-02，FGT-END-003）",
                new Action[] { RevealSetup, CheckRevealData, CheckRevealForeshadowTriggers, CheckRevealFlow, CheckRevealQuitMidway, CheckRevealCamera, CheckRevealNegatives, CheckRevealTiming, CheckRevealUnobserved, RevealTeardown });

        public static void RunRevealFromMenu()
        {
            var report = new StringBuilder();
            int fail = RunReveal(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        private static readonly string[] RevealCallbacks =
        {
            // 伏笔 → 揭示字幕里必须出现的回应（FGR-END-010“之前所有伏笔在这里接上”）。清单按 FG11 FGR-NAR-010 节点表逐行写（揭示之前的全部节点），
            // 另加第二幕监听截获的“清点”片段；CheckRevealData 还核“资料库里每个剧情节点（除揭示）都在这张清单里”，表里多出节点而揭示没回应就失败。
            "node.wake|点亮信标", "node.sky_eyes|天空就有了眼睛", "node.guns_up|炮口为什么朝着天空", "node.first_fragment|清点地表单元",
            "node.what_waits|在等的", "node.purify_salvation|净化即救赎", "event:signal_ghost|根权限", "node.purify_meaning|同一个词能指两件相反的事",
            "node.decipher|清除全部自治单元", "node.mega_blueprint|蓝图发来时满是热情", "node.mega.*|只剩程式化的回执",
            "intercept.fleet.act2.count|清点：百分之一百",
        };

        private static void RevealSetup()
        {
            UiTuningValues.Reload();
            NarrativeService.ResetForTests();
            NarrativeService.EnsureListening();
            CutsceneCamera.ResetSession();
            RaidAudioDirector.ResetSession();
            BeaconLaunchPanelUIToolkit.CloseReveal();
            VisualElement croot = F.MountUxml(UiKitFolder + "NarrativeCutscene.uxml", out _revealPlayerGo);
            _revealPlayer = _revealPlayerGo.AddComponent<NarrativeCutscenePlayerUIToolkit>();
            _revealPlayer.BindView(croot);
        }

        private static void RevealTeardown()
        {
            NarrativeService.AbortPlaying();
            NarrativeService.StopListeningForTests();
            NarrativeService.ResetForTests();
            NarrativeService.CutsceneSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.CutsceneEnter, toast: false);
            RevealService.AfterSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.RevealComplete, toast: false);
            NarrativeCutscenePlayerUIToolkit.InWorldOverrideForTests = false;
            BeaconLaunchPanelUIToolkit.CloseReveal();
            CutsceneCamera.ResetSession();
            RaidAudioDirector.ResetSession();
            if (_revealPlayerGo != null)
            {
                Object.DestroyImmediate(_revealPlayerGo);
            }
            _revealPlayer = null;
            _revealPlayerGo = null;
            if (Stage.GameRoot.IsWorldPaused)
            {
                Stage.GameRoot.SetWorldPaused(false);
            }
        }

        // ── 工具 ─────────────────────────────────────────────────────────────────

        /// <summary>真实走完五个阶段（每阶段从“差一点”开始，D1 同一做法）：最后一步发 AllCompletedEvent。</summary>
        /// <summary>主线“巨构蓝图”完成（正式完成入口发蓝图；任务记录记为已完成，“返航工程”按前置出现）。</summary>
        private static void MegaQuestDone(CampaignState s)
        {
            CompleteMegaQuest(s);
            if (CampaignObjectiveTracker.FindQuest(s, "main.megastructure") == null)
            {
                var rec = new ObjectiveRecord { ObjectiveId = "main.megastructure", DefId = "main.megastructure", State = ObjectiveState.Completed };
                s.ObjectiveRecords = (s.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>()).Append(rec).ToArray();
            }
            CampaignObjectiveTracker.Recompute(s);
        }

        private static Rig CompleteAll(CampaignState s, string key)
        {
            MegaQuestDone(s);
            CampaignObjectiveTracker.Recompute(s);
            Rig rig = Lay(s, key, 300, 6000);
            if (rig == null)
            {
                return null;
            }
            for (int i = 0; i < MegastructureService.StageCount; i++)
            {
                Prefill(s, 2, 50_000, Hz);
                if (!StepUntil(() => M(s).StageIndex > i, 30L * Hz))
                {
                    return null;
                }
            }
            WorldSimulation.StepMany(2);
            CampaignObjectiveTracker.Recompute(s);
            return rig;
        }

        /// <summary>负向用夹具：工地已建成、五个阶段记为完工（不发事件）。</summary>
        private static Rig AllDoneFixture(CampaignState s, string key)
        {
            MegaQuestDone(s);
            Rig rig = Lay(s, key, 0, 0);
            if (rig == null)
            {
                return null;
            }
            MegastructureState m = M(s);
            m.Done = MegastructureService.Stages.Select(st => new MegaStageDoneRecord { Id = st.Id, Tick = 1, Day = 1 }).ToArray();
            m.StageIndex = MegastructureService.StageCount;
            return rig;
        }

        private static void TickPlayer(float dt) => _revealPlayer.Tick(dt);

        /// <summary>真演出播放器逐帧播完（每帧 0.25 真实秒），返回帧数（上限 400 帧 = 100 秒）。</summary>
        private static int PlayToEnd()
        {
            int frames = 0;
            while (NarrativeCutscenePlayerUIToolkit.IsShowing && frames < 400)
            {
                TickPlayer(0.25f);
                frames++;
            }
            return frames;
        }

        private static bool SkipWithConfirm()
        {
            bool clicked = ClickButton(_revealPlayer.SkipButton);
            bool dialog = UiConfirmDialog.IsOpen;
            UiConfirmDialog.Confirm();
            return clicked && dialog && !NarrativeCutscenePlayerUIToolkit.IsShowing;
        }

        private static bool ClickButton(Button b)
        {
            if (b?.clickable == null || !b.enabledInHierarchy)
            {
                return false;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
            return true;
        }

        private static CampaignState ReadSlot(out string why)
        {
            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            why = rr.Success ? string.Empty : rr.Message;
            return rr.Success ? rr.State : null;
        }

        private static string QuestState(CampaignState s, string id) => CampaignObjectiveTracker.FindQuest(s, id)?.State.ToString() ?? "无";

        // ── R0 数据 ───────────────────────────────────────────────────────────────

        /// <summary>
        /// R0b（审查 P1 修复）：第二幕两条“净化”伏笔从正式入口投放——第一次进入沉淀湖（图鉴“进入领地”同一时机）→ 监听截获“净化即救赎”；
        /// 击败洗礼塔（领地平定事件）→ 核心备忘“净化的意义”；幂等。另核读档时信标仍待点亮会补发一次“信标待点亮”（审查 P2），同一份状态不重复。
        /// </summary>
        private static void CheckRevealForeshadowTriggers()
        {
            CampaignState s = NewWorld(8407);
            Progression.MechanicCodex.OnTerritoryEntered("clarity");
            bool salvation = NarrativeService.Has(s, "node.purify_salvation");
            int filed = NarrativeService.FiledCount;
            Progression.MechanicCodex.OnTerritoryEntered("clarity");
            Progression.MechanicCodex.OnTerritoryEntered("foundry");
            bool otherTerritoryNoEntry = NarrativeService.FiledCount == filed && !NarrativeService.Has(s, "node.purify_meaning");
            TEngine.GameEvent.Send(TerritoryControlService.LeaderDefeatedEvent, "clarity");
            bool meaning = NarrativeService.Has(s, "node.purify_meaning") && NarrativeService.FiledCount == filed + 1;
            bool acts = NarrativeCatalog.TryGet("node.purify_salvation", out NarrativeEntryDef d1) && d1.Act == 2 && d1.Node && d1.Source == NarrativeCatalog.SourceIntercept
                        && NarrativeCatalog.TryGet("node.purify_meaning", out NarrativeEntryDef d2) && d2.Act == 2 && d2.Node && d2.Source == NarrativeCatalog.SourceMemo;

            bool repost = false;
            string why = string.Empty;
            if (AllDoneFixture(s, "r0b") != null && RevealService.AwaitingConfirm(s))
            {
                CampaignAutoSaveService.SaveAuto(SaveReason.CutsceneEnter, toast: false);
                CampaignState loaded = ReadSlot(out why);
                int c0 = RevealService.BeaconReadyCount;
                // 夹具直接写完工记录（没走完工事件）= 相当于一份“完工后存的档”：进入战役补发一次，同一份状态再进入不重复。
                RevealService.OnCampaignEntered(s);
                RevealService.OnCampaignEntered(s);
                bool sameOnce = RevealService.BeaconReadyCount == c0 + 1;
                RevealService.OnCampaignEntered(loaded);
                bool loadedOnce = loaded != null && RevealService.AwaitingConfirm(loaded) && RevealService.BeaconReadyCount == c0 + 2;
                RevealService.OnCampaignEntered(loaded);
                repost = sameOnce && loadedOnce && RevealService.BeaconReadyCount == c0 + 2;
            }
            Expect(salvation && otherTerritoryNoEntry && meaning && acts && repost,
                $"R0b 第二幕伏笔从正式入口投放：进入沉淀湖 → “净化即救赎” {salvation}；进入别的领地不投放 / 重复进入不重复 {otherTerritoryNoEntry}；击败洗礼塔 → “净化的意义” {meaning}；" +
                $"两条都是第二幕剧情节点（截获 / 备忘）{acts}；读档时信标待点亮补发一次通知、同一状态不重复 {repost}{(why.Length > 0 ? "（读档：" + why + "）" : string.Empty)}");
        }

        private static void CheckRevealData()
        {
            bool entry = NarrativeCatalog.TryGet(RevealService.EntryId, out NarrativeEntryDef d) && d.IsCutscene && d.Camera == CutsceneCamera.ShotSky && d.Act == 3 && d.Node
                         && d.Trigger == RevealService.Trigger && d.Lines.Count >= 8 && NarrativeCatalog.Problems.Count == 0;
            string zh = d == null ? string.Empty : string.Join("", d.Lines.Select(l => GameText.Get(l.TextKey)));
            GameSettings.SetLanguage(GameLanguage.En);
            string en = d == null ? string.Empty : string.Join(" ", d.Lines.Select(l => GameText.Get(l.TextKey)));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool names = zh.Contains("方舟舰队") && zh.Contains("白潮") && zh.Contains("归乡号") && en.Contains("Ark Fleet") && en.Contains("White Tide") && en.Contains("Homecoming");
            bool spec = zh.Contains("归还核心任务完成。净化协议启动。");
            var missing = new List<string>();
            foreach (string cb in RevealCallbacks)
            {
                string[] kv = cb.Split('|');
                bool exists = kv[0] == "event:signal_ghost" ? GameLogic.Campaign.Env.EventDirectorService.Def("signal_ghost") != null
                    : kv[0].EndsWith("*", StringComparison.Ordinal) ? NarrativeCatalog.All.Any(e => e.Node && e.Id.StartsWith(kv[0].TrimEnd('*'), StringComparison.Ordinal))
                    : NarrativeCatalog.TryGet(kv[0], out _);
                if (!exists || !zh.Contains(kv[1]))
                {
                    missing.Add(kv[0]);
                }
            }
            // 资料库里每个剧情节点（node=1，除揭示本身）都要在上面的伏笔清单里（逐条核，不认实现自己的回应表）。
            foreach (NarrativeEntryDef e in NarrativeCatalog.All)
            {
                if (e.Node && e.Id != RevealService.EntryId && e.Id != EndingService.EntryId // FG13-END-06：结局在揭示之后，不是伏笔
                    && !RevealCallbacks.Any(cb => { string k = cb.Split('|')[0]; return k == e.Id || (k.EndsWith("*", StringComparison.Ordinal) && e.Id.StartsWith(k.TrimEnd('*'), StringComparison.Ordinal)); }))
                {
                    missing.Add("未列入伏笔清单的剧情节点 " + e.Id);
                }
            }
            // 别的剧情节点没有镜头（只有揭示与 FG13-END-06 的结局转向天空）。
            bool onlyReveal = NarrativeCatalog.All.Where(e => e.Camera != CutsceneCamera.ShotNone).Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(new[] { EndingService.EntryId, RevealService.EntryId }.OrderBy(x => x, StringComparer.Ordinal));
            GameConfig.fg.Quest home = QuestCatalog.Get("main.homecoming");
            GameConfig.fg.Quest purge = QuestCatalog.Get("main.purge");
            bool quests = home != null && home.Kind == QuestCatalog.KindEvent && home.Param == RevealService.RevealedEventId && home.Prereq == "main.megastructure" && home.TimeHours <= 0f
                          && purge != null && purge.Kind == QuestCatalog.KindEvent && purge.Param == "endgame_purge_cleared" && purge.Prereq == "main.homecoming";
            bool notify = NotificationCatalog.TryGetType(RevealService.OrbitalNotifyType, out _) && NotificationCatalog.TryGetType(RevealService.BeaconReadyNotifyType, out _);
            bool music = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/GameRes/Raw/Audios/Sfx/" + RaidAudioDirector.MusicOrbital + ".wav") != null;
            bool tuning = CutsceneCamera.Seconds > 0f && CutsceneCamera.ElevationDegrees > 0f && CutsceneCamera.ElevationDegrees <= 80f;
            Expect(entry && names && spec && missing.Count == 0 && onlyReveal && quests && notify && music && tuning,
                $"R0 数据：揭示 node.reveal 是第三幕剧情节点演出、触发 {RevealService.Trigger}、镜头 sky、{d?.Lines.Count ?? 0} 行字幕 {entry}；三个正式名（中 / 英）{names}；规格原句 {spec}；" +
                $"伏笔接上（{RevealCallbacks.Length} 条，缺 [{string.Join(",", missing)}]）；只有揭示 / 结局带镜头 {onlyReveal}；主线“返航工程 → 守住家园”两条 event 判定、无时限 {quests}；两种通知类型 {notify}；轨道威胁占位音乐 {music}；镜头调参 {tuning}");
        }

        // ── R1 正式路径（FGT-END-003）───────────────────────────────────────────────

        private static void CheckRevealFlow()
        {
            CampaignState s = NewWorld(8401);
            int ready0 = RevealService.BeaconReadyCount;
            Rig rig = CompleteAll(s, "r1");
            if (rig == null)
            {
                Fail("R1 返航工地五个阶段没能走完（或找不到空地）");
                return;
            }
            bool awaiting = RevealService.AwaitingConfirm(s) && RevealService.BeaconReadyCount == ready0 + 1 && CountOf(RevealService.BeaconReadyNotifyType) >= 1;
            string questBefore = QuestState(s, "main.homecoming");
            string questProg = CampaignObjectiveTracker.ProgressText(s, CampaignObjectiveTracker.FindQuest(s, "main.homecoming"));
            bool hudBefore = !RevealService.OrbitalThreatActive(s);

            // 第一次确认：真 UXML 生产面板选中返航工地 → “点亮信标…”。
            VisualElement proot = F.MountUxml(UiKitFolder + "ProductionPanel.uxml", out GameObject pgo);
            ProductionPanelUIToolkit.InWorldOverrideForTests = true;
            bool buttonShown = false, panelClosed = false, buttonAfter = true;
            string status = string.Empty;
            try
            {
                ProductionPanelUIToolkit panel = pgo.AddComponent<ProductionPanelUIToolkit>();
                panel.BindView(proot);
                ProductionPanelUIToolkit.Open(rig.Site.BuildingId);
                panel.Refresh();
                buttonShown = !panel.RevealButton.ClassListContains("bn-hidden") && panel.RevealButton.text.Contains("点亮信标");
                ClickButton(panel.RevealButton);
                panelClosed = !panel.PanelVisible && BeaconLaunchPanelUIToolkit.RevealOpen;
                status = BeaconLaunchPanelUIToolkit.RevealStatusText(s);
                // 第二次确认：挂真 UXML 的信标确认面板（审查 P2：组件路径要有断言）——打开即显示点亮模式的标题 / 状态 / 按钮；
                // 先点“关闭”收起（不投放），再打开（不残留上次文字），点“确认点亮”→ 揭示演出待播、面板收起。
                VisualElement broot = F.MountUxml("Assets/GameRes/Raw/UI/Beacon/BeaconLaunchPanel.uxml", out GameObject bgo);
                BeaconLaunchPanelUIToolkit.InWorldOverrideForTests = true;
                try
                {
                    var beacon = bgo.AddComponent<BeaconLaunchPanelUIToolkit>();
                    beacon.BindView(broot);
                    VisualElement bpanel = broot.Q<VisualElement>("BeaconLaunchPanelRoot");
                    Button bconfirm = broot.Q<Button>("ConfirmButton");
                    Button bclose = broot.Q<Button>("CloseButton");
                    Func<bool> revealMode = () => bpanel.ClassListContains("blp-root-visible") && broot.Q<Label>("Title").text == GameText.Get("mega.reveal.panel_title")
                                                  && broot.Q<Label>("StatusLabel").text == BeaconLaunchPanelUIToolkit.RevealStatusText(s)
                                                  && bconfirm.text == GameText.Get("mega.reveal.confirm") && bclose.text == GameText.Get("mega.reveal.close")
                                                  && bconfirm.style.display.value == DisplayStyle.Flex;
                    beacon.Tick();
                    bool shown = revealMode();
                    ClickButton(bclose);
                    beacon.Tick();
                    bool closedNoReveal = !BeaconLaunchPanelUIToolkit.RevealOpen && !bpanel.ClassListContains("blp-root-visible") && !RevealService.Pending(s);
                    broot.Q<Label>("StatusLabel").text = "旧文字";
                    BeaconLaunchPanelUIToolkit.OpenReveal(); // 与生产面板“点亮信标…”同一入口
                    beacon.Tick();
                    bool reopened = revealMode();
                    ClickButton(bconfirm);
                    beacon.Tick();
                    bool confirmed = !bpanel.ClassListContains("blp-root-visible");
                    panelClosed &= shown && closedNoReveal && reopened && confirmed && !BeaconLaunchPanelUIToolkit.RevealOpen && RevealService.Pending(s) && !RevealService.Revealed(s);
                    if (!(shown && closedNoReveal && reopened && confirmed))
                    {
                        Fail($"R1 信标确认面板（真 UXML）：打开显示点亮模式 {shown}、关闭不投放 {closedNoReveal}、重开立即刷新 {reopened}、确认后收起 {confirmed}");
                    }
                }
                finally
                {
                    BeaconLaunchPanelUIToolkit.InWorldOverrideForTests = false;
                    BeaconLaunchPanelUIToolkit.CloseReveal();
                    Object.DestroyImmediate(bgo);
                }
                ProductionPanelUIToolkit.Open(rig.Site.BuildingId);
                panel.Refresh();
                buttonAfter = !panel.RevealButton.ClassListContains("bn-hidden");
            }
            finally
            {
                ProductionPanelUIToolkit.InWorldOverrideForTests = false;
                ProductionPanelUIToolkit.Close();
                Object.DestroyImmediate(pgo);
            }
            bool statusOk = status.Contains("确认点亮信标") && status.Contains("自动存档") && status.Contains("资料库重看");

            // 演出：播放器下一帧开始，开始前自动存档（演出前的那一次）。
            int camBegins = CutsceneCamera.Begins;
            float speedBefore = GameClock.Speed;
            TickPlayer(0.016f);
            bool playing = NarrativeCutscenePlayerUIToolkit.IsShowing && _revealPlayer.Cutscene?.Def.Id == RevealService.EntryId && !_revealPlayer.Cutscene.Replay
                           && Stage.GameRoot.IsWorldPaused && CutsceneCamera.Begins == camBegins + 1 && NarrativeService.SkipNeedsConfirm;
            CampaignState pre = ReadSlot(out string why1);
            bool preSave = pre != null && pre.LastSaveReason == SaveReason.CutsceneEnter && pre.Progress.PendingCutscene == RevealService.EntryId
                           && !RevealService.Revealed(pre) && !NarrativeService.Has(pre, RevealService.EntryId) && MegastructureService.AllDone(pre);
            CampaignSession.Set(Slot, s); // 读盘只为核对，会话仍是正在玩的这一份
            // 演出期间世界暂停：世界步不走。
            long t0 = GameClock.Ticks;
            WorldSimulation.StepMany(0);
            int afterSaves0 = RevealService.AfterSaveCount;
            int frames = PlayToEnd();
            bool finished = !NarrativeCutscenePlayerUIToolkit.IsShowing && !Stage.GameRoot.IsWorldPaused && Mathf.Approximately(GameClock.Speed, speedBefore);

            // 状态变化：事件账本、任务更新、轨道威胁 HUD、音乐、通知；演出后的自动存档。
            bool revealed = RevealService.Revealed(s) && RevealService.OrbitalThreatActive(s) && NarrativeService.Has(s, RevealService.EntryId) && !RevealService.Pending(s);
            CampaignObjectiveTracker.Recompute(s);
            string homeAfter = QuestState(s, "main.homecoming");
            string purgeAfter = QuestState(s, "main.purge");
            bool quest = questBefore == "Active" && questProg.Contains("点亮信标") && homeAfter == "Completed" && purgeAfter == "Active";
            RaidAudioDirector.Tick(s, true, 1f, force: true);
            bool music = RaidAudioDirector.Phase == RaidAudioDirector.PhaseOrbital && RaidAudioDirector.CurrentMusic == RaidAudioDirector.MusicOrbital && !RaidAudioDirector.ThreatPhaseActive;
            bool notified = CountOf(RevealService.OrbitalNotifyType) >= 1;
            string hudText = string.Empty, hudEn = string.Empty;
            VisualElement wroot = F.MountUxml(UiKitFolder + "WorldBar.uxml", out GameObject wgo);
            try
            {
                var bar = wgo.AddComponent<WorldBarHudUIToolkit>();
                bar.BindView(wroot);
                bar.Refresh();
                hudText = bar.OrbitalThreatText;
                GameSettings.SetLanguage(GameLanguage.En);
                bar.Refresh();
                hudEn = bar.OrbitalThreatText;
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
            finally
            {
                Object.DestroyImmediate(wgo);
            }
            bool hud = hudBefore && hudText.Contains("轨道威胁") && hudEn.Contains("Orbital threat");
            bool afterSave = RevealService.AfterSaveCount == afterSaves0 + 1 && RevealService.LastAfterSave.Success;
            CampaignState post = ReadSlot(out string why2);
            bool postSave = post != null && post.LastSaveReason == SaveReason.RevealComplete && RevealService.Revealed(post) && NarrativeService.Has(post, RevealService.EntryId)
                            && string.IsNullOrEmpty(post.Progress.PendingCutscene) && QuestState(post, "main.homecoming") == "Completed";
            CampaignSession.Set(Slot, s);

            // 资料库重看：不改状态、不再存档、跳过不要确认；镜头同样转向天空。
            int saves1 = RevealService.AfterSaveCount, reveals1 = RevealService.RevealCount, cutSaves = NarrativeService.CutsceneSaveCount, ledger = s.EventLedger.Length;
            NarrativeCutscene replay = NarrativeService.BeginReplay(s, RevealService.EntryId);
            bool replayCam = replay != null && CutsceneCamera.Active && !NarrativeService.SkipNeedsConfirm;
            NarrativeService.FinishPlaying(skipped: true);
            bool replayOk = replayCam && RevealService.AfterSaveCount == saves1 && RevealService.RevealCount == reveals1 && NarrativeService.CutsceneSaveCount == cutSaves && s.EventLedger.Length == ledger;

            Expect(awaiting && buttonShown && panelClosed && !buttonAfter && statusOk && playing && preSave && finished && revealed && quest && music && notified && hud && afterSave && postSave && replayOk,
                $"R1 正式路径（FGT-END-003）：五个阶段真实完工 → “信标待点亮”通知、等待确认 {awaiting}；主线“返航工程”进行中、进度“{questProg}”；生产面板“点亮信标…” {buttonShown}（第一次确认）→ 信标确认面板 “{status.Replace("\n", " / ")}” {statusOk} → " +
                $"“确认点亮”（第二次）后待播、面板收起 {panelClosed}、按钮隐藏 {!buttonAfter}；演出开始：首次观看、世界暂停、镜头开始转向天空、跳过要确认 {playing}；演出前自动存档（磁盘读回：原因 CutsceneEnter、待播揭示、未揭示）{preSave}{why1}；" +
                $"逐帧播完 {frames} 帧（0.25 秒 / 帧）世界恢复、速度不变 {finished}；状态变化：揭示 / 轨道威胁 {revealed}，任务 返航工程 {homeAfter} / 守住家园 {purgeAfter} {quest}，音乐切到轨道威胁层 {music}，通知 {notified}，顶栏“{hudText}” / “{hudEn}” {hud}；" +
                $"演出后自动存档 {afterSave}（磁盘读回：原因 RevealComplete、已揭示、资料库有揭示、任务已更新 {postSave}{why2}）；资料库重看：镜头同样转向天空、跳过不要确认、不改状态不再存档 {replayOk}");
            _ = t0;
        }

        // ── R2 负向：演出中退出游戏（FG11 第 9 节）──────────────────────────────────

        private static void CheckRevealQuitMidway()
        {
            CampaignState s = NewWorld(8402);
            if (AllDoneFixture(s, "r2") == null)
            {
                Fail("R2 找不到放工地的空地");
                return;
            }
            bool confirmed = RevealService.Confirm(s, out string deny);
            TickPlayer(0.016f);
            TickPlayer(3.0f);
            int midLine = _revealPlayer.Cutscene?.LineIndex ?? -1;
            bool midway = NarrativeCutscenePlayerUIToolkit.IsShowing && _revealPlayer.Cutscene.LineIndex >= 1;
            int afterSaves0 = RevealService.AfterSaveCount;
            // 退出游戏：世界卸载、会话清空（没有任何存档写入）→ 播放器下一帧中止，不收录、不揭示。
            WorldSimulation.UnloadAll();
            CampaignSession.Clear();
            GameClock.ResetSession();
            TickPlayer(0.016f);
            bool aborted = !NarrativeCutscenePlayerUIToolkit.IsShowing && NarrativeService.Playing == null && !RevealService.Revealed(s) && RevealService.AfterSaveCount == afterSaves0;
            CampaignState l = ReadSlot(out string why);
            if (l == null)
            {
                Fail("R2 读档失败：" + why);
                return;
            }
            string pendingWhy = null;
            bool before = l.LastSaveReason == SaveReason.CutsceneEnter && RevealService.Pending(l) && !RevealService.Revealed(l) && !RevealService.AwaitingConfirm(l)
                          && !RevealService.CanConfirm(l, out pendingWhy) && pendingWhy == RevealService.DenyPending;
            CampaignSession.Set(Slot, l);
            HomeValleyController home = WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(home.SiteId);
            NarrativeService.OnCampaignEntered(l);
            TickPlayer(0.016f);
            bool replays = NarrativeCutscenePlayerUIToolkit.IsShowing && _revealPlayer.Cutscene.Def.Id == RevealService.EntryId && _revealPlayer.Cutscene.LineIndex == 0 && !_revealPlayer.Cutscene.Replay;
            // 这一次点跳过（首次观看：要确认）→ 状态照常落定、演出后存档。
            bool skipped = SkipWithConfirm();
            bool applied = RevealService.Revealed(l) && RevealService.AfterSaveCount == afterSaves0 + 1 && RevealService.LastAfterSave.Success && NarrativeService.Has(l, RevealService.EntryId);
            CampaignState post = ReadSlot(out string why2);
            bool saved = post != null && post.LastSaveReason == SaveReason.RevealComplete && RevealService.Revealed(post);
            CampaignSession.Set(Slot, l);
            NarrativeService.OnCampaignEntered(l);
            TickPlayer(0.016f);
            bool noAgain = !NarrativeCutscenePlayerUIToolkit.IsShowing && RevealService.AfterSaveCount == afterSaves0 + 1;
            Expect(confirmed && midway && aborted && before && replays && skipped && applied && saved && noAgain,
                $"R2 负向“揭示演出中退出游戏”（FG11 第 9 节）：确认点亮 {confirmed}{deny}，播放到第 {midLine} 行 {midway}；退出 → 中止、不揭示、不存档 {aborted}；" +
                $"读档回到演出前的自动存档（待播揭示、未揭示、不能再确认：{pendingWhy}）{before}；进入后从第一行重新播放（仍算首次）{replays}；确认跳过 {skipped} → 状态落定、演出后存档 {applied}（磁盘读回 {saved}{why2}）；再进入不再播放 {noAgain}");
        }

        // ── R3 游戏内镜头（DEBT-FG9NAR01-01 揭示部分）──────────────────────────────

        private static void CheckRevealCamera()
        {
            CampaignState s = NewWorld(8403);
            if (AllDoneFixture(s, "r3") == null)
            {
                Fail("R3 找不到放工地的空地");
                return;
            }
            var camGo = new GameObject("__FgRevealCamera") { hideFlags = HideFlags.HideAndDontSave };
            Camera cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 20f;
            camGo.transform.SetPositionAndRotation(new Vector3(3f, 30f, -18f), Quaternion.Euler(58f, 30f, 0f));
            Func<float> originalDt = CameraDirector.RealDeltaTime;
            CameraDirector.RealDeltaTime = () => 0.05f;
            var director = new CameraDirector();
            InputRouter.DebugSetReader(new IdleReader()); // 不碰真鼠标（batchmode 下真鼠标位置会触发边缘推屏）
            string detail = string.Empty;
            bool ok = false;
            try
            {
                CutsceneCamera.ResetSession();
                director.Bind(cam, (out float2 a) => { a = float2.zero; return false; }, new Vector3(0f, 30f, -18f), 60f, startInStrategy: true);
                director.Tick(false);
                Quaternion baseRot = cam.transform.rotation;
                Vector3 basePos = cam.transform.position;
                GameLogic.Campaign.Grid.GridCell focus0 = WorldView.CameraFocusCell(cam);
                float elev = CutsceneCamera.ElevationDegrees;
                int frames = Mathf.CeilToInt(CutsceneCamera.Seconds / 0.05f) + 4;

                // 揭示开始（正式待播入口）：镜头逐步抬起，最后视线高出地平线 elev 度、水平朝向不变、位置不变。
                RevealService.Confirm(s, out _);
                TickPlayer(0.016f);
                director.Tick(true);
                float firstUp = cam.transform.forward.y;
                bool gradual = firstUp < Mathf.Sin(elev * Mathf.Deg2Rad) - 0.05f && firstUp > (baseRot * Vector3.forward).y; // 第一帧只抬了一点（不是瞬间跳过去）
                // 倍速无关（审查 P2：在抬起途中比，不是饱和之后）：同样一帧真实时间，1x 与 3x 下程度各涨一样多（= 真实秒 / 转动时长）。
                float w0 = CutsceneCamera.Weight;
                GameClock.SetSpeed(3f);
                director.Tick(true);
                float w1 = CutsceneCamera.Weight;
                GameClock.SetSpeed(1f);
                director.Tick(true);
                float w2 = CutsceneCamera.Weight;
                float expectStep = 0.05f / CutsceneCamera.Seconds;
                bool speedFree = w2 < 1f && Mathf.Abs((w1 - w0) - expectStep) < 1e-4f && Mathf.Abs((w2 - w1) - expectStep) < 1e-4f;
                // 地面焦点（流式加载 / 地貌窗口 / 小地图）按抬起前的视线算：抬起途中焦点格不动（审查 P2）。
                bool focusMid = WorldView.CameraFocusCell(cam).Equals(focus0);
                for (int i = 0; i < frames; i++)
                {
                    director.Tick(true);
                }
                Vector3 f = cam.transform.forward;
                Vector3 bf = baseRot * Vector3.forward;
                var flat = new Vector3(f.x, 0f, f.z).normalized;
                var bflat = new Vector3(bf.x, 0f, bf.z).normalized;
                bool sky = f.y > Mathf.Sin(elev * Mathf.Deg2Rad) - 0.02f && Vector3.Dot(flat, bflat) > 0.995f && (cam.transform.position - basePos).sqrMagnitude < 1e-4f;
                bool focusSteady = focusMid && WorldView.CameraFocusCell(cam).Equals(focus0);
                // 跳过 → 镜头用同样的时长转回原来的角度。
                SkipWithConfirm();
                director.Tick(true);
                bool returning = cam.transform.forward.y < f.y - 1e-4f && cam.transform.forward.y > bf.y + 1e-4f;
                for (int i = 0; i < frames; i++)
                {
                    director.Tick(true);
                }
                float backAngle = Quaternion.Angle(cam.transform.rotation, baseRot);
                float drift = (cam.transform.position - basePos).magnitude;
                bool restored = backAngle < 0.05f && drift < 0.01f && !CutsceneCamera.Active;
                // 不带镜头的演出（苏醒）：镜头不动。
                int begins = CutsceneCamera.Begins;
                NarrativeService.Deliver(s, NarrativeCatalog.Find(NarrativeService.WakeId));
                TickPlayer(0.016f);
                for (int i = 0; i < 10; i++)
                {
                    director.Tick(true);
                }
                bool wakeStill = NarrativeCutscenePlayerUIToolkit.IsShowing && CutsceneCamera.Begins == begins && Quaternion.Angle(cam.transform.rotation, baseRot) < 0.05f;
                SkipWithConfirm();
                // 演出中途解绑（换地点 / 离开世界）：旋转先复原再解绑，不把抬起的角度留在相机上。
                NarrativeService.BeginReplay(s, RevealService.EntryId);
                for (int i = 0; i < 5; i++)
                {
                    director.Tick(true);
                }
                director.Unbind(resetInput: false);
                bool unbindRestored = Quaternion.Angle(cam.transform.rotation, baseRot) < 0.05f;
                // 中止（换战役 / 世界卸载）：演出镜头的程度立即清零，下一个世界第一帧不从抬起的状态开始（审查 P2）。
                bool abortReset = CutsceneCamera.Active && CutsceneCamera.Weight > 0f;
                NarrativeService.AbortPlaying();
                abortReset &= !CutsceneCamera.Active && CutsceneCamera.Weight == 0f;
                ok = gradual && sky && speedFree && focusSteady && returning && restored && wakeStill && unbindRestored && abortReset;
                detail = $"仰角 {elev:0} 度、{CutsceneCamera.Seconds:0.##} 秒：第一帧只抬一点 {gradual}（{firstUp:0.###}）；转向天空后视线 y={f.y:0.###}、水平朝向不变、位置不变 {sky}；" +
                         $"抬起途中 1x / 3x 每帧程度增量一致 {speedFree}（{w1 - w0:0.####} / {w2 - w1:0.####}，应为 {expectStep:0.####}）；抬起前后地面焦点格不变 {focusSteady}；中止后程度清零 {abortReset}；" +
                         $"跳过后逐步转回 {returning}、最后复原 {restored}（差 {backAngle:0.###} 度、位移 {drift:0.###}、仍在转 {CutsceneCamera.Active}、程度 {CutsceneCamera.Weight:0.###}、镜头模式 {director.Mode}）；苏醒（无镜头）镜头不动 {wakeStill}；中途解绑先复原 {unbindRestored}";
            }
            finally
            {
                director.Unbind(resetInput: false);
                CameraDirector.RealDeltaTime = originalDt;
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                CutsceneCamera.ResetSession();
                NarrativeService.AbortPlaying();
                Object.DestroyImmediate(camGo);
            }
            Expect(ok, "R3 游戏内镜头（FGR-END-010“镜头转向天空”，真镜头导演 + 临时相机，DEBT-FG9NAR01-01 揭示部分）：" + detail);
        }

        // ── R4 负向矩阵 ─────────────────────────────────────────────────────────────

        private static void CheckRevealNegatives()
        {
            CampaignState s = NewWorld(8404);
            Rig rig = AllDoneFixture(s, "r4");
            if (rig == null)
            {
                Fail("R4 找不到放工地的空地");
                return;
            }
            var rows = new List<string>();
            bool all = true;
            void Row(string name, bool ok)
            {
                rows.Add($"{name} {ok}");
                all &= ok;
            }
            // 还没全部完工：不能点亮，按钮不出现，拒绝写明原因。
            MegastructureState m = M(s);
            m.StageIndex = 3;
            int clicks = BeaconLaunchPanelUIToolkit.RevealConfirmClicks;
            bool deny1 = !BeaconLaunchPanelUIToolkit.ConfirmReveal() && BeaconLaunchPanelUIToolkit.LastRevealDeny == RevealService.DenyNotDone && !RevealService.Pending(s)
                         && BeaconLaunchPanelUIToolkit.RevealStatusText(s).Contains("还没完成") && !RevealService.AwaitingConfirm(s) && BeaconLaunchPanelUIToolkit.RevealConfirmClicks == clicks + 1;
            Row("未完工被拒（原因写明）", deny1);
            m.StageIndex = MegastructureService.StageCount;
            // 工地被摧毁：拒绝（写明找不到工地）。
            BuildingConstructionState cs0 = rig.Site.ConstructionState;
            rig.Site.ConstructionState = BuildingConstructionState.Destroyed;
            Row("工地被摧毁被拒", !RevealService.CanConfirm(s, out string r2) && r2 == RevealService.DenyNoSite);
            rig.Site.ConstructionState = cs0;
            // 另一段演出待播 / 正在播：拒绝（不连续接管画面两次）。
            NarrativeService.Deliver(s, NarrativeCatalog.Find(NarrativeService.WakeId));
            Row("另一段演出待播时被拒", !RevealService.CanConfirm(s, out string r3) && r3 == RevealService.DenyBusy);
            TickPlayer(0.016f);
            SkipWithConfirm();
            // 玩家不点亮：没有时间压力——两个游戏日后仍在等、没有揭示、任务没有时限、没有失败。
            CampaignObjectiveTracker.Recompute(s);
            long until = GameClock.Ticks + (long)(GameClock.DaySeconds * 2 * Hz);
            while (GameClock.Ticks < until)
            {
                WorldSimulation.StepMany(Math.Min(60 * Hz, (int)(until - GameClock.Ticks)));
            }
            CampaignObjectiveTracker.Recompute(s);
            ObjectiveRecord home = CampaignObjectiveTracker.FindQuest(s, "main.homecoming");
            Row("不点亮：两个游戏日后仍在等、任务无时限", RevealService.AwaitingConfirm(s) && !RevealService.Revealed(s) && home != null && home.State == ObjectiveState.Active && home.DeadlineTick < 0);
            // 确认两次：第二次被拒（待播），只投放一次。
            bool first = RevealService.Confirm(s, out _);
            bool second = RevealService.Confirm(s, out string r4);
            Row("连点两次只投放一次", first && !second && r4 == RevealService.DenyPending && NarrativeService.PendingCutscene(s)?.Id == RevealService.EntryId);
            TickPlayer(0.016f);
            SkipWithConfirm();
            // 已揭示：再确认被拒；状态落定只一次（重复进入战役 / 重复收录不再改、不再存档）。
            int saves = RevealService.AfterSaveCount;
            Row("已揭示再确认被拒", !RevealService.Confirm(s, out string r5) && r5 == RevealService.DenyDone && RevealService.Revealed(s));
            NarrativeService.OnCampaignEntered(s);
            TickPlayer(0.016f);
            Row("再进入战役不重播、不重复存档", !NarrativeCutscenePlayerUIToolkit.IsShowing && RevealService.AfterSaveCount == saves);
            // 揭示之后家园突袭照常切音乐（轨道威胁层之上），突袭结束回到轨道威胁层并响结束提示音。
            RaidAudioDirector.ResetSession();
            RaidAudioDirector.ApplyEvaluated(RaidAudioDirector.PhaseOrbital, false);
            int stings = RaidAudioDirector.StingCount;
            RaidAudioDirector.ApplyEvaluated(RaidAudioDirector.PhaseWarning, true);
            bool warnSting = RaidAudioDirector.StingCount == stings + 1 && RaidAudioDirector.LastSting == RaidAudioDirector.StingWarning;
            RaidAudioDirector.ApplyEvaluated(RaidAudioDirector.PhaseRaid, true);
            RaidAudioDirector.ApplyEvaluated(RaidAudioDirector.PhaseOrbital, false);
            Row("揭示后突袭预警 / 进行 / 结束的音乐与提示音", warnSting && RaidAudioDirector.CurrentMusic == RaidAudioDirector.MusicOrbital && RaidAudioDirector.LastSting == RaidAudioDirector.StingEnd);
            // 旧存档（揭示之前的版本）：没有揭示事件 → 不显示轨道威胁、音乐平静。
            var old = new CampaignState { Progress = new CampaignProgressState() };
            CampaignFgStateDomains.EnsureAll(old);
            Row("旧存档没有轨道威胁", !RevealService.OrbitalThreatActive(old) && !RevealService.AwaitingConfirm(old));
            // 面板在世界外自动收起。
            BeaconLaunchPanelUIToolkit.OpenReveal();
            Row("面板模式可以收起", BeaconLaunchPanelUIToolkit.RevealOpen);
            BeaconLaunchPanelUIToolkit.CloseReveal();
            Expect(all, "R4 负向矩阵：" + string.Join("；", rows));
        }

        // ── R5 暂停与 0.5x～3x ───────────────────────────────────────────────────────

        private static void CheckRevealTiming()
        {
            var results = new List<string>();
            bool ok = true;
            foreach (float speed in new[] { 0.5f, 1f, 3f })
            {
                CampaignState s = NewWorld(8405);
                if (AllDoneFixture(s, "r5") == null)
                {
                    Fail("R5 找不到放工地的空地");
                    return;
                }
                GameClock.SetSpeed(speed);
                GameClock.SetPaused(true); // 战略暂停中确认（确认本身不计时）
                bool confirmed = RevealService.Confirm(s, out _);
                GameClock.SetPaused(false);
                TickPlayer(0.016f);
                bool paused = Stage.GameRoot.IsWorldPaused;
                int frames = PlayToEnd();
                bool done = RevealService.Revealed(s) && !Stage.GameRoot.IsWorldPaused && Mathf.Approximately(GameClock.Speed, speed);
                results.Add($"{speed}x：确认 {confirmed}、演出中世界暂停 {paused}、{frames} 帧播完（真实秒，与倍速无关）、揭示后速度仍是 {GameClock.Speed}x {done}");
                ok &= confirmed && paused && done;
                results.Add($"帧数 {frames}");
                GameClock.SetSpeed(1f);
            }
            // 三种速度播完的帧数一致（字幕按真实秒）。
            string[] fr = results.Where(r => r.StartsWith("帧数")).ToArray();
            ok &= fr.Distinct().Count() == 1;
            Expect(ok, "R5 暂停与倍速：" + string.Join("；", results.Where(r => !r.StartsWith("帧数"))) + $"；三种速度帧数一致 {fr.Distinct().Count() == 1}");
        }

        // ── R6 不观察家园时一致 ─────────────────────────────────────────────────────

        private static void CheckRevealUnobserved()
        {
            var lines = new List<string>();
            foreach (bool observe in new[] { true, false })
            {
                CampaignState s = NewWorld(8406, observe);
                CompleteMegaQuest(s);
                Rig rig = Lay(s, "r6", 300, 6000);
                if (rig == null)
                {
                    Fail("R6 找不到放工地的空地");
                    return;
                }
                M(s).StageIndex = MegastructureService.StageCount - 1;
                int ready0 = RevealService.BeaconReadyCount;
                Prefill(s, 2, 50_000, Hz);
                bool done = StepUntil(() => MegastructureService.AllDone(s), 30L * Hz);
                lines.Add($"{(observe ? "观察" : "不观察")}：完工 {done}、信标待点亮通知 {RevealService.BeaconReadyCount - ready0}、等待确认 {RevealService.AwaitingConfirm(s)}");
            }
            Expect(lines.Count == 2 && lines[0].Substring(3) == lines[1].Substring(4) && lines[0].Contains("完工 True") && lines[0].Contains("通知 1") && lines[0].Contains("等待确认 True"),
                "R6 家园观察 / 不观察一致（信标点火 → 待点亮）：" + string.Join("；", lines));
        }
    }
}
