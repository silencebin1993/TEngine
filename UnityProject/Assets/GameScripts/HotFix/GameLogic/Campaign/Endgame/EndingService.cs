using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Localization;
using GameLogic.Narrative;
using TEngine;

namespace GameLogic.Campaign.Endgame
{
    /// <summary>结算画面要显示的一份统计（打开结算时现算，只读既有数据源，不另存一份并行统计）。</summary>
    public sealed class EndingStats
    {
        /// <summary>归还面积（格）：<see cref="PurificationService.ReturnedArea"/>。</summary>
        public long ReturnedArea;
        /// <summary>游戏天数：统一时钟的游戏日。</summary>
        public int Days;
        /// <summary>发现的反应：组合图鉴里已解锁的具名交互（<see cref="Content.ReactionDiscoveryService.DiscoveredInteractionCount"/>，FG19 口径，跨存档 D-17）。</summary>
        public int Reactions;
        /// <summary>熔合配方数：本存档配方书里已发现的配方。</summary>
        public int FusionRecipes;
        /// <summary>击退的突袭次数：<see cref="EndingService.RaidsRepelled"/>。</summary>
        public int RaidsRepelled;
        /// <summary>幸存机器（按编号）。</summary>
        public readonly List<MachineRecord> Survivors = new List<MachineRecord>();
        /// <summary>纪念墙：全部阵亡机器（黑匣子陈列馆同一份名单，最近的在前）。</summary>
        public List<MachineRecord> Fallen = new List<MachineRecord>();
        /// <summary>最老的那台（幸存机器里编号最小的；一台都没剩 = null）。</summary>
        public MachineRecord Oldest;
        /// <summary>被接入最多的那台（信号进入次数最多，活着或阵亡都算；从没接入过 = null）。</summary>
        public MachineRecord MostLinked;
        /// <summary>结语（每行一句，已按统计挑好文本并格式化）。</summary>
        public readonly List<string> Epilogue = new List<string>();
        /// <summary>结语用到的文本键（自检核“结语随统计变化”）。</summary>
        public readonly List<string> EpilogueKeys = new List<string>();
    }

    /// <summary>
    /// FG13-END-06 结局、结算与纪念（FG11 FGR-END-030～032；FG13 FGU-42；验收 FGT-END-008）。
    ///
    /// - 结局演出（FGR-END-030）：先遣旗舰被击退（<see cref="FlagshipService.DefeatedEvent"/>）→ 投放资料库条目 node.ending（演出，镜头 sky：转向天空，主舰队仍停在轨道上；
    ///   最后一行是核心的最后一条备忘）。播放复用叙事框架：开始前自动存档（存档里记着待播，演出中退出 → 读档后重播）、可跳过（首次要确认）、之后在资料库或结算画面重看。
    /// - 结局落盘：演出收进资料库那一步（看完或跳过同一步）写事件账本 <see cref="EndingEventId"/>，<b>先自动存档一次</b>（<see cref="SaveReason.EndingComplete"/>），之后才打开结算——
    ///   读档不会重放结局，也不会丢结局标记。
    /// - 结算与纪念（FGR-END-031）：<see cref="Stats"/> 聚合幸存机器、纪念墙（黑匣子陈列馆）、五项统计与随统计变化的结语（归还十万格那一句读 <see cref="PurificationService.EndingLineKey"/>）。
    ///   结算期间世界暂停，<b>任何存档都被拒绝</b>（自动档、暂停菜单“保存并返回主菜单”同一入口 <see cref="CampaignAutoSaveService.SaveWithExport"/>，<see cref="SaveOutcome.SettlementOpen"/>）。
    /// - 之后（FGR-END-032）：继续 → 进入后日谈（<see cref="CampaignProgressState.IsPostgame"/> = true，存档卡显示“后日谈”，周期清洗等由 FG14-SBX-01 接上）、恢复暂停状态、自动存档（<see cref="SaveReason.PostgameStart"/>）；
    ///   返回主菜单 → 不存档直接回菜单（结局已在结算前落盘；下次读这份档会再次停在结算画面，重新选择）。
    /// - 成就（FGR-END-032“解锁对应成就”）：事件账本 <see cref="EndingEventId"/> 是唯一真相（随存档，旧档同样可补发）+ <see cref="EndingReachedEvent"/>；成就系统在 FG15-SYS-04 据此解锁。
    /// 每次调用 O(1)；统计 O(机器数 log 机器数)，只在打开结算时算一次。
    /// </summary>
    public static class EndingService
    {
        public const string EntryId = "node.ending";
        public const string Trigger = "endgame:ending";
        /// <summary>事件账本：结局已达成（结算前落盘；“通关”成就与后日谈的判据）。</summary>
        public const string EndingEventId = "endgame_ending";
        /// <summary>GameEvent：结局刚达成（成就系统 FG15-SYS-04 订阅）。</summary>
        public const string EndingReachedEvent = "Endgame.EndingReached";
        /// <summary>GameEvent：玩家在结算后选择继续（后日谈开始；FG14-SBX-01 订阅）。</summary>
        public const string PostgameStartedEvent = "Endgame.PostgameStarted";

        /// <summary>结局落盘的自动存档（默认 = 正式自动存档入口；自检可替换）。</summary>
        public static Func<SaveResult> EndingSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.EndingComplete, toast: false);
        /// <summary>选择继续之后的自动存档（默认 = 正式入口；自检可替换）。</summary>
        public static Func<SaveResult> PostgameSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.PostgameStart, toast: false);
        /// <summary>“返回主菜单”的执行者（默认 = GameRoot.EndRun，唯一回菜单出口；自检可替换）。</summary>
        public static Action ReturnToMenuHandler = () => Stage.GameRoot.EndRun();

        private static bool _listening;
        private static CampaignState _settlementState;
        private static bool _pausedBefore;

        public static int Revision { get; private set; }
        public static int EndingCount { get; private set; }
        public static int EndingSaveCount { get; private set; }
        public static SaveResult LastEndingSave { get; private set; }
        public static int SettlementOpenCount { get; private set; }
        public static int BlockedSaves { get; private set; }

        public static void EnsureListening()
        {
            if (_listening)
            {
                return;
            }
            _listening = true;
            GameEvent.AddEventListener(FlagshipService.DefeatedEvent, OnFlagshipDefeated);
            GameEvent.AddEventListener<string>(NarrativeService.AddedEvent, OnNarrativeAdded);
        }

        public static void StopListeningForTests()
        {
            if (!_listening)
            {
                return;
            }
            _listening = false;
            GameEvent.RemoveEventListener(FlagshipService.DefeatedEvent, OnFlagshipDefeated);
            GameEvent.RemoveEventListener<string>(NarrativeService.AddedEvent, OnNarrativeAdded);
        }

        public static void ResetForTests()
        {
            _settlementState = null;
            _pausedBefore = false;
            EndingCount = 0;
            EndingSaveCount = 0;
            SettlementOpenCount = 0;
            BlockedSaves = 0;
            LastEndingSave = default;
            EndingSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.EndingComplete, toast: false);
            PostgameSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.PostgameStart, toast: false);
            ReturnToMenuHandler = () => Stage.GameRoot.EndRun();
            Revision++;
        }

        // ── 读 ───────────────────────────────────────────────────────────

        /// <summary>结局已达成（结局演出已收进资料库、结局已落盘）。</summary>
        public static bool Reached(CampaignState s)
        {
            EventLedgerEntry[] ledger = s?.EventLedger;
            if (ledger == null)
            {
                return false;
            }
            // 结算面板每帧都问：账本只经 CampaignEventLedger.TryGrant 追加（追加 = 换新数组），按数组引用 + 长度缓存，没变时 O(1)。
            if (!ReferenceEquals(ledger, _reachedLedger) || ledger.Length != _reachedLength)
            {
                _reachedLedger = ledger;
                _reachedLength = ledger.Length;
                _reachedHit = CampaignEventLedger.Contains(s, EndingEventId);
            }
            return _reachedHit;
        }

        private static EventLedgerEntry[] _reachedLedger;
        private static int _reachedLength;
        private static bool _reachedHit;

        /// <summary>结局演出已投放、还没看完（存档里记着待播）。</summary>
        public static bool Pending(CampaignState s) => s?.Progress != null && s.Progress.PendingCutscene == EntryId;

        /// <summary>该停在结算画面：结局已达成、还没选“继续”进入后日谈。</summary>
        public static bool SettlementDue(CampaignState s) => Reached(s) && s.Progress != null && !s.Progress.IsPostgame;

        /// <summary>结算画面正开着（这期间拒绝任何存档）。</summary>
        public static bool SettlementOpen => _settlementState != null;

        /// <summary>存档拦截判据（<see cref="CampaignAutoSaveService.SaveWithExport"/> 调）：结算画面开着、且是这份战役。</summary>
        public static bool BlocksSave(CampaignState s)
        {
            if (_settlementState == null || !ReferenceEquals(_settlementState, s))
            {
                return false;
            }
            BlockedSaves++;
            return true;
        }

        /// <summary>
        /// 击退的突袭次数：突袭导演里结束原因是“被全歼”或“抵达后撤退”的突袭（含清洗波次）。计数器 <see cref="RaidDirectorState.RepelledCount"/> 在突袭结束时累加；
        /// 计数器之前的旧档按突袭历史（最多 raid.history_max 条）补一个下限，取两者大的。
        /// </summary>
        public static int RaidsRepelled(CampaignState s)
        {
            RaidDirectorState d = RaidDirectorService.StateOf(s);
            if (d == null)
            {
                return 0;
            }
            // 旧档在第一次突袭结算（RaidDirectorService.Cleanup）时把计数器补到历史下限；那之前这里按同一口径取大的。
            return Math.Max(d.RepelledCount, RaidDirectorService.RepelledInHistory(d));
        }

        /// <summary>结算统计、名单与结语（打开结算 / 自检时调；O(机器数 log 机器数)）。</summary>
        public static EndingStats Stats(CampaignState s)
        {
            var st = new EndingStats();
            if (s == null)
            {
                return st;
            }
            st.ReturnedArea = PurificationService.ReturnedArea(s);
            st.Days = s.Clock != null && s.Clock.Day > 0 ? s.Clock.Day : 1;
            st.Reactions = Content.ReactionDiscoveryService.DiscoveredInteractionCount();
            st.FusionRecipes = FusionService.DiscoveredRecipes(s, null).Count;
            st.RaidsRepelled = RaidsRepelled(s);
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null)
                {
                    continue;
                }
                if (m.IsAlive)
                {
                    st.Survivors.Add(m);
                }
                if (m.SignalUplinkCount > 0 && (st.MostLinked == null || m.SignalUplinkCount > st.MostLinked.SignalUplinkCount
                                                || (m.SignalUplinkCount == st.MostLinked.SignalUplinkCount && m.DisplayNumber < st.MostLinked.DisplayNumber)))
                {
                    st.MostLinked = m;
                }
            }
            st.Survivors.Sort((a, b) => a.DisplayNumber != b.DisplayNumber ? a.DisplayNumber.CompareTo(b.DisplayNumber) : a.LogicId.CompareTo(b.LogicId));
            st.Oldest = st.Survivors.Count > 0 ? st.Survivors[0] : null;
            st.Fallen = BlackBoxService.Memorial(byPlace: false);
            BuildEpilogue(s, st);
            return st;
        }

        private static void Add(EndingStats st, string key, params object[] args)
        {
            st.EpilogueKeys.Add(key);
            st.Epilogue.Add(args.Length == 0 ? GameText.Get(key) : GameText.Format(key, args));
        }

        private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>结语（FGR-END-031“只是文字差异，不是分支结局”）：面积与天数 → 归还十万格的专属一句 → 最老的那台 → 被接入最多的那台 → 收尾。</summary>
        private static void BuildEpilogue(CampaignState s, EndingStats st)
        {
            Add(st, st.ReturnedArea > 0 ? "ending.epilogue.area" : "ending.epilogue.area_none", N(st.ReturnedArea), st.Days);
            string milestone = PurificationService.EndingLineKey(s); // DEBT-FG7ENV04-04：归还 100,000 格的专属一句
            if (!string.IsNullOrEmpty(milestone))
            {
                Add(st, milestone);
            }
            if (st.Oldest != null)
            {
                Add(st, "ending.epilogue.oldest", MachineNaming.Long(st.Oldest));
            }
            else
            {
                Add(st, "ending.epilogue.none_alive");
            }
            if (st.MostLinked != null)
            {
                Add(st, st.MostLinked.IsAlive ? "ending.epilogue.linked" : "ending.epilogue.linked_fallen", MachineNaming.Long(st.MostLinked), st.MostLinked.SignalUplinkCount);
            }
            Add(st, st.Fallen.Count > 0 ? "ending.epilogue.close_fallen" : "ending.epilogue.close", st.Fallen.Count);
        }

        /// <summary>幸存机器一行：名字 · 编号 · 型号；经历（与纪念墙同一份经历文本）。</summary>
        public static string SurvivorText(CampaignState s, MachineRecord m)
        {
            if (m == null)
            {
                return string.Empty;
            }
            string model = MachineNaming.Model(m);
            string head = MachineNaming.HasCustomName(m)
                ? GameText.Format("blackbox.memorial.name", m.CustomName, m.DisplayNumber, model.Length > 0 ? model : GameText.Get("blackbox.memorial.no_model"))
                : GameText.Format("blackbox.memorial.name_plain", model.Length > 0 ? model : GameText.Get("blackbox.memorial.no_model"), m.DisplayNumber);
            return head + "\n" + GameText.Format("blackbox.memorial.history", MachineExperienceFlags.Join(m.ExperienceFlags), m.KillCount, m.JobsCompleted, m.ExpeditionsCompleted,
                MachineSignalExperience.Describe(s, m));
        }

        // ── 写 ───────────────────────────────────────────────────────────

        private static void OnFlagshipDefeated() => Begin(CampaignSession.Current);

        /// <summary>先遣旗舰被击退 → 投放结局演出（记成待播；播放器下一帧开始，开始前自动存档）。幂等。</summary>
        public static bool Begin(CampaignState s)
        {
            if (s == null || !FlagshipService.Done(s) || Reached(s) || Pending(s) || NarrativeService.Has(s, EntryId))
            {
                return false;
            }
            bool ok = NarrativeService.Trigger(s, Trigger);
            if (ok)
            {
                Revision++;
                Log.Info("[EndingService] 先遣舰队被击退：结局演出待播");
            }
            return ok;
        }

        /// <summary>
        /// 进入战役（读档 / 回滚）：先遣旗舰已被击退、结局还没投放的旧档 → 补投放结局演出；结局已达成的档由结算面板在世界就绪后打开结算。
        /// </summary>
        public static void OnCampaignEntered(CampaignState s)
        {
            if (_settlementState != null && !ReferenceEquals(_settlementState, s))
            {
                AbortSettlement();
            }
            Begin(s);
        }

        private static void OnNarrativeAdded(string id)
        {
            if (id == EntryId)
            {
                Apply(CampaignSession.Current);
            }
        }

        /// <summary>结局落盘（幂等）：事件账本 → 结局达成事件（成就）→ 自动存档。结算由面板在这之后打开。</summary>
        private static void Apply(CampaignState s)
        {
            if (s == null || !CampaignEventLedger.TryGrant(s, EndingEventId, "Endgame", s.PlaySeconds, EntryId))
            {
                return;
            }
            EndingCount++;
            Revision++;
            Log.Info("[EndingService] 结局达成：先遣舰队被击退，主舰队仍在轨道上");
            GameEvent.Send(EndingReachedEvent);
            LastEndingSave = EndingSaveHandler != null ? EndingSaveHandler() : default;
            EndingSaveCount++;
        }

        /// <summary>打开结算（结算面板调；结局没达成 / 已在后日谈 / 已经开着 → false）：暂停世界，开始拦截存档。</summary>
        public static bool OpenSettlement(CampaignState s)
        {
            if (_settlementState != null || !SettlementDue(s))
            {
                return false;
            }
            _settlementState = s;
            _pausedBefore = NarrativeService.IsWorldPaused != null && NarrativeService.IsWorldPaused();
            NarrativeService.SetWorldPaused?.Invoke(true);
            SettlementOpenCount++;
            Revision++;
            GameLogic.Core.GuidanceHooks.Raise(GameLogic.Core.GuidanceHooks.EndingFirstSettlement);
            return true;
        }

        private static void CloseSettlement()
        {
            _settlementState = null;
            Revision++;
            if (!_pausedBefore)
            {
                NarrativeService.SetWorldPaused?.Invoke(false);
            }
        }

        /// <summary>结算“继续”：进入后日谈（存档卡显示后日谈），恢复暂停状态，之后自动存档（结算关掉之后才存，不在结算中存）。</summary>
        public static bool ContinuePostgame()
        {
            CampaignState s = _settlementState;
            if (s?.Progress == null)
            {
                return false;
            }
            CloseSettlement();
            s.Progress.IsPostgame = true;
            Log.Info("[EndingService] 结算后选择继续：进入后日谈");
            GameEvent.Send(PostgameStartedEvent);
            PostgameSaveHandler?.Invoke();
            return true;
        }

        /// <summary>结算“返回主菜单”：不存档（结局已在结算前落盘），回菜单。</summary>
        public static bool ReturnToMenu()
        {
            if (_settlementState == null)
            {
                return false;
            }
            CloseSettlement();
            ReturnToMenuHandler?.Invoke();
            return true;
        }

        /// <summary>战役被换掉 / 世界卸载时收起结算（不改存档，只恢复暂停状态）。</summary>
        public static void AbortSettlement()
        {
            if (_settlementState != null)
            {
                CloseSettlement();
            }
        }
    }
}
