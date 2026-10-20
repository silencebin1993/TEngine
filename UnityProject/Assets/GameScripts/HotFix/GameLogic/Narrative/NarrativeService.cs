using System;
using System.Collections.Generic;
using GameLogic.Campaign;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;

namespace GameLogic.Narrative
{
    /// <summary>
    /// FG9-NAR-01（FG11 FGR-NAR-001～004、FGR-NAR-010 前五个节点、第 8 / 9 / 10 节；FG13 FGU-38、FGR-UX-051）：叙事投放框架。
    ///
    /// - <b>四种投放</b>：核心备忘 / 数据终端 / 监听截获直接收进资料库并发一条“资料库新增”通知（不打断游戏，FGR-NAR-002）；
    ///   演出（cutscene）先记成“待播”（<see cref="CampaignProgressState.PendingCutscene"/>），由演出播放器在世界就绪时接管画面播放，
    ///   看完或跳过才收进资料库。核心备忘里由别的系统发过“核心备忘”通知的（memo: 触发，归还里程碑 / 领地收复）不再重复发通知。
    /// - <b>触发</b>：新游戏（<see cref="OnCampaignEntered"/>）、远征队第一次到达地点（<see cref="Campaign.Regions.ExpeditionMarchService.SiteArrivedEvent"/>）、
    ///   首领被击败（<see cref="Campaign.WorldSim.TerritoryControlService.LeaderDefeatedEvent"/>）、核心备忘（<see cref="Campaign.Env.PurificationService.CoreMemoEvent"/>）、
    ///   以及读取旧终端 / 截获舰队片段 / 解读数据核心（<see cref="FeedEvent"/>，参数就是触发串）。全部经 GameEvent，触发 → 条目 O(1)，同一条只收一次（幂等）。
    /// - <b>资料库按存档</b>（FGR-UX-051）：记录在 <see cref="CampaignProgressState.Narrative"/>，随战役存档；新存档为空。已读 / 未读也在存档里。
    /// - <b>演出可跳过、可重看、退出游戏回到演出前</b>（第 8、9 节）：首次播放前自动存档（<see cref="SaveReason.CutsceneEnter"/>，存档里记着待播），
    ///   中途退出游戏 → 读档后演出重新播放；首次观看跳过要确认，资料库里重看随时可跳过、不改存档。播放期间世界暂停，结束后恢复播放前的暂停状态。
    /// - <b>幕</b>：条目写明 setAct 的（“它在等什么 —— 第一幕结束”）收到后把战役推进到下一幕（只进不退）。
    ///
    /// 开销：触发 O(1)（字典）+ 记录追加 O(本存档资料数)；界面查询 O(资料数)，只在面板刷新时。与实体数无关，不按帧运行。
    /// </summary>
    public static class NarrativeService
    {
        /// <summary>收进资料库（参数：条目 ID）。</summary>
        public const string AddedEvent = "Narrative.Added";
        /// <summary>别的系统投放一条资料（参数：触发串，如 <c>terminal:terminal.lore.silent.a</c>）。</summary>
        public const string FeedEvent = "Narrative.Feed";
        /// <summary>战役推进到新的一幕（参数：幕）。</summary>
        public const string ActChangedEvent = "Narrative.ActChanged";
        public const string NotifyType = "narrative_new";
        public const string MemoNotifyType = "core_memo";
        public const string WakeId = "node.wake";

        public static int Revision { get; private set; } = 1;
        public static int FiledCount { get; private set; }
        public static int CutsceneBeginCount { get; private set; }
        public static int CutsceneFinishCount { get; private set; }
        public static int SkipCount { get; private set; }
        public static int ReplayCount { get; private set; }
        public static SaveResult LastCutsceneSave { get; private set; }
        public static int CutsceneSaveCount { get; private set; }

        /// <summary>界面层登记：请求打开资料库并选中某条（null = 最新一条未读）。</summary>
        public static event Action<string> OpenRequested;

        /// <summary>自检：演出前的自动存档（默认 = 正式自动存档入口）。</summary>
        public static Func<SaveResult> CutsceneSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.CutsceneEnter, toast: false);
        /// <summary>世界暂停读写（默认接 GameRoot；自检在编辑模式下可替换）。</summary>
        public static Func<bool> IsWorldPaused = () => Stage.GameRoot.IsWorldPaused;
        public static Action<bool> SetWorldPaused = p => Stage.GameRoot.SetWorldPaused(p);

        private static bool _listening;
        private static NarrativeCutscene _playing;
        private static CampaignState _playingState;
        private static bool _pausedBefore;
        private static string _lastSiteFiled;
        private static CampaignState _lastSiteState;

        /// <summary>正在播放的演出（没有 = null）。</summary>
        public static NarrativeCutscene Playing => _playing;

        // ── 绑定 ───────────────────────────────────────────────────────────

        /// <summary>订阅各系统的叙事事件（进程内一次）。</summary>
        public static void EnsureListening()
        {
            if (_listening)
            {
                return;
            }
            _listening = true;
            GameEvent.AddEventListener<string>(Campaign.Env.PurificationService.CoreMemoEvent, OnCoreMemo);
            GameEvent.AddEventListener<string>(Campaign.Regions.ExpeditionMarchService.SiteArrivedEvent, OnSiteArrived);
            GameEvent.AddEventListener<string>(Campaign.Regions.ExpeditionMarchService.SiteArrivalRevertedEvent, OnSiteArrivalReverted);
            GameEvent.AddEventListener<string>(Campaign.WorldSim.TerritoryControlService.LeaderDefeatedEvent, OnLeaderDefeated);
            GameEvent.AddEventListener<string>(FeedEvent, OnFeed);
        }

        /// <summary>自检收尾：取消订阅（让后面的自检段不受叙事投放影响；正式流程从不取消）。</summary>
        public static void StopListeningForTests()
        {
            if (!_listening)
            {
                return;
            }
            _listening = false;
            GameEvent.RemoveEventListener<string>(Campaign.Env.PurificationService.CoreMemoEvent, OnCoreMemo);
            GameEvent.RemoveEventListener<string>(Campaign.Regions.ExpeditionMarchService.SiteArrivedEvent, OnSiteArrived);
            GameEvent.RemoveEventListener<string>(Campaign.Regions.ExpeditionMarchService.SiteArrivalRevertedEvent, OnSiteArrivalReverted);
            GameEvent.RemoveEventListener<string>(Campaign.WorldSim.TerritoryControlService.LeaderDefeatedEvent, OnLeaderDefeated);
            GameEvent.RemoveEventListener<string>(FeedEvent, OnFeed);
        }

        public static bool Listening => _listening;

        private static void OnCoreMemo(string id) => Trigger(CampaignSession.Current, NarrativeCatalog.TriggerMemo + id);
        private static void OnSiteArrived(string siteId)
        {
            CampaignState s = CampaignSession.Current;
            string trigger = NarrativeCatalog.TriggerSite + siteId;
            bool filed = Trigger(s, trigger);
            // 审查修复（P1）：到达事务在载入地点之前投放（地点 Enter 的自动存档要带着它）；记下这一趟新收的条目，载入失败时撤回。
            _lastSiteFiled = filed && NarrativeCatalog.TryGetByTrigger(trigger, out NarrativeEntryDef def) ? def.Id : null;
            _lastSiteState = filed ? s : null;
        }

        private static void OnSiteArrivalReverted(string siteId)
        {
            CampaignState s = CampaignSession.Current;
            string id = _lastSiteFiled;
            bool mine = id != null && ReferenceEquals(_lastSiteState, s)
                        && NarrativeCatalog.TryGetByTrigger(NarrativeCatalog.TriggerSite + siteId, out NarrativeEntryDef def) && def.Id == id;
            _lastSiteFiled = null;
            _lastSiteState = null;
            if (mine)
            {
                Revoke(s, id);
            }
        }

        /// <summary>
        /// 撤回一条刚投放、所在事务又失败了的条目（只用于远征到达载入失败）：从资料库移除 / 清掉待播。不回退幕号（到达类条目不推进幕，表校验保证）。
        /// 已发出的“资料库新增”通知无法收回（点开时资料库里找不到这条，只是打开资料库）。
        /// </summary>
        internal static bool Revoke(CampaignState s, string id)
        {
            CampaignProgressState p = s?.Progress;
            if (p == null || string.IsNullOrEmpty(id))
            {
                return false;
            }
            if (p.PendingCutscene == id)
            {
                p.PendingCutscene = string.Empty;
                Revision++;
                return true;
            }
            int idx = Array.FindIndex(p.Narrative ?? Array.Empty<NarrativeEntryRecord>(), r => r != null && r.Id == id);
            if (idx < 0)
            {
                return false;
            }
            var list = new NarrativeEntryRecord[p.Narrative.Length - 1];
            Array.Copy(p.Narrative, 0, list, 0, idx);
            Array.Copy(p.Narrative, idx + 1, list, idx, p.Narrative.Length - idx - 1);
            p.Narrative = list;
            Revision++;
            Log.Info($"[NarrativeService] 远征到达载入失败，撤回刚投放的 {id}");
            return true;
        }
        private static void OnLeaderDefeated(string territoryId) => Trigger(CampaignSession.Current, NarrativeCatalog.TriggerLeader + territoryId);
        private static void OnFeed(string trigger) => Trigger(CampaignSession.Current, trigger);

        /// <summary>
        /// 进入战役（新建 / 读档 / 回滚 / 失败页读档）时调用：订阅事件；新游戏（统一时钟还没走过一步、资料库里没有“苏醒”）触发开场演出。
        /// 待播的演出（演出中途退出游戏的那份演出前存档）由播放器接着播。
        /// </summary>
        public static void OnCampaignEntered(CampaignState s)
        {
            EnsureListening();
            AbortPlaying();
            if (s?.Progress == null)
            {
                return;
            }
            CampaignFgStateDomains.EnsureAll(s);
            if (Has(s, WakeId) || s.Progress.PendingCutscene == WakeId)
            {
                return;
            }
            // 只有统一时钟还没走过一步的战役才是“新游戏”；已经玩过的存档（含叙事框架之前的旧档、回滚读回的档）不补播开场，也不改它的资料库
            // （读档 / 回滚前后状态逐字段一致）。
            if ((s.Clock?.Ticks ?? 0) == 0)
            {
                Trigger(s, NarrativeCatalog.TriggerNewGame);
            }
        }

        // ── 投放 ───────────────────────────────────────────────────────────

        /// <summary>按触发串投放（没有对应条目 / 已经收过 → false）。</summary>
        public static bool Trigger(CampaignState s, string trigger)
        {
            if (s?.Progress == null || !NarrativeCatalog.TryGetByTrigger(trigger, out NarrativeEntryDef def))
            {
                return false;
            }
            return Deliver(s, def);
        }

        /// <summary>投放一条条目：演出记成待播，其余直接收进资料库。已收过 / 已在待播 → false（幂等）。</summary>
        public static bool Deliver(CampaignState s, NarrativeEntryDef def)
        {
            if (s?.Progress == null || def == null || Has(s, def.Id))
            {
                return false;
            }
            CampaignFgStateDomains.EnsureAll(s);
            if (def.IsCutscene)
            {
                if (s.Progress.PendingCutscene == def.Id)
                {
                    return false;
                }
                if (!string.IsNullOrEmpty(s.Progress.PendingCutscene))
                {
                    // 同时只排一段首次演出：前一段还没播，这一段直接收进资料库（可重看），不连续接管画面两次。
                    File(s, def, read: false, notify: true);
                    return true;
                }
                s.Progress.PendingCutscene = def.Id;
                Revision++;
                return true;
            }
            File(s, def, read: false, notify: !def.Trigger.StartsWith(NarrativeCatalog.TriggerMemo, StringComparison.Ordinal));
            return true;
        }

        private static void File(CampaignState s, NarrativeEntryDef def, bool read, bool notify)
        {
            CampaignProgressState p = s.Progress;
            int act = def.Act > 0 ? def.Act : Math.Max(1, p.Act);
            var rec = new NarrativeEntryRecord
            {
                Id = def.Id,
                Serial = p.NarrativeSerial++,
                Act = act,
                Tick = GameClock.Ticks,
                Day = s.Clock != null && s.Clock.Day > 0 ? s.Clock.Day : 1,
                Read = read,
            };
            var list = new NarrativeEntryRecord[p.Narrative.Length + 1];
            Array.Copy(p.Narrative, list, p.Narrative.Length);
            list[p.Narrative.Length] = rec;
            p.Narrative = list;
            FiledCount++;
            Revision++;
            if (def.SetAct > p.Act)
            {
                p.Act = def.SetAct;
                GameEvent.Send(ActChangedEvent, p.Act);
                Log.Info($"[NarrativeService] 剧情节点 {def.Id}：战役进入第 {p.Act} 幕");
            }
            GuidanceHooks.Raise(GuidanceHooks.NarrativeFirstEntry);
            if (notify)
            {
                NotificationCenter.Post(NotifyType, GameText.Format("narrative.notify.detail", SourceName(def.Source), GameText.Get(def.TitleKey)));
            }
            GameEvent.Send(AddedEvent, def.Id);
        }

        // ── 演出 ───────────────────────────────────────────────────────────

        /// <summary>待播的首次演出（没有 = null）。</summary>
        public static NarrativeEntryDef PendingCutscene(CampaignState s) =>
            s?.Progress != null && !string.IsNullOrEmpty(s.Progress.PendingCutscene) ? NarrativeCatalog.Find(s.Progress.PendingCutscene) : null;

        /// <summary>
        /// 开始播放待播的首次演出：先自动存档（存档里记着待播 → 中途退出读档后重新播放），再暂停世界、接管画面。
        /// 待播的条目已不在表里时清掉待播（不卡住）。返回播放模型（null = 没有可播的）。
        /// </summary>
        public static NarrativeCutscene BeginPending(CampaignState s)
        {
            if (_playing != null || s?.Progress == null || string.IsNullOrEmpty(s.Progress.PendingCutscene))
            {
                return null;
            }
            NarrativeEntryDef def = PendingCutscene(s);
            if (def == null || Has(s, def.Id))
            {
                Log.Warning($"[NarrativeService] 待播演出 {s.Progress.PendingCutscene} 不在表里或已收录，清掉待播。");
                s.Progress.PendingCutscene = string.Empty;
                Revision++;
                return null;
            }
            LastCutsceneSave = CutsceneSaveHandler != null ? CutsceneSaveHandler() : default;
            CutsceneSaveCount++;
            return Begin(s, def, replay: false);
        }

        /// <summary>在资料库重看一段演出（本存档已收录的才可以；不存档、不改资料库）。</summary>
        public static NarrativeCutscene BeginReplay(CampaignState s, string id)
        {
            if (_playing != null || s == null || !Has(s, id) || !NarrativeCatalog.TryGet(id, out NarrativeEntryDef def) || !def.IsCutscene)
            {
                return null;
            }
            ReplayCount++;
            GuidanceHooks.Raise(GuidanceHooks.NarrativeFirstReplay);
            return Begin(s, def, replay: true);
        }

        private static NarrativeCutscene Begin(CampaignState s, NarrativeEntryDef def, bool replay)
        {
            _playing = new NarrativeCutscene(def, replay);
            _playingState = s;
            _pausedBefore = IsWorldPaused != null && IsWorldPaused();
            SetWorldPaused?.Invoke(true);
            CutsceneBeginCount++;
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.NarrativeFirstCutscene);
            return _playing;
        }

        /// <summary>跳过要不要确认：首次观看要（FGR-NAR-002），重看不要。</summary>
        public static bool SkipNeedsConfirm => _playing != null && !_playing.Replay;

        /// <summary>
        /// 演出结束（看完或跳过）：首次观看收进资料库（已读，发“资料库新增”通知说明可以重看）、清掉待播；恢复播放前的世界暂停状态。
        /// </summary>
        public static void FinishPlaying(bool skipped)
        {
            if (_playing == null)
            {
                return;
            }
            NarrativeCutscene c = _playing;
            CampaignState s = _playingState;
            _playing = null;
            _playingState = null;
            if (skipped)
            {
                SkipCount++;
                GuidanceHooks.Raise(GuidanceHooks.NarrativeFirstSkip);
            }
            if (!c.Replay && s?.Progress != null && ReferenceEquals(s, CampaignSession.Current))
            {
                if (s.Progress.PendingCutscene == c.Def.Id)
                {
                    s.Progress.PendingCutscene = string.Empty;
                }
                if (!Has(s, c.Def.Id))
                {
                    File(s, c.Def, read: true, notify: true);
                }
            }
            CutsceneFinishCount++;
            Revision++;
            if (!_pausedBefore)
            {
                SetWorldPaused?.Invoke(false);
            }
        }

        /// <summary>战役被换掉 / 世界卸载时中止播放：不收录、不改存档（待播仍在那份状态里），只恢复暂停状态。</summary>
        public static void AbortPlaying()
        {
            if (_playing == null)
            {
                return;
            }
            _playing = null;
            _playingState = null;
            Revision++;
            if (!_pausedBefore)
            {
                SetWorldPaused?.Invoke(false);
            }
        }

        /// <summary>正在播放的演出属于的战役（播放器据此发现战役被换掉）。</summary>
        public static CampaignState PlayingState => _playingState;

        // ── 资料库查询 ─────────────────────────────────────────────────────

        public static bool Has(CampaignState s, string id) => Find(s, id) != null;

        public static NarrativeEntryRecord Find(CampaignState s, string id)
        {
            NarrativeEntryRecord[] list = s?.Progress?.Narrative;
            if (list == null || string.IsNullOrEmpty(id))
            {
                return null;
            }
            foreach (NarrativeEntryRecord r in list)
            {
                if (r != null && r.Id == id)
                {
                    return r;
                }
            }
            return null;
        }

        public static int Count(CampaignState s) => s?.Progress?.Narrative?.Length ?? 0;

        public static int UnreadCount(CampaignState s)
        {
            int n = 0;
            foreach (NarrativeEntryRecord r in s?.Progress?.Narrative ?? Array.Empty<NarrativeEntryRecord>())
            {
                if (r != null && !r.Read)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>标为已读（阅读时）。返回是否这次才变成已读。</summary>
        public static bool MarkRead(CampaignState s, string id)
        {
            NarrativeEntryRecord r = Find(s, id);
            if (r == null || r.Read)
            {
                return false;
            }
            r.Read = true;
            Revision++;
            return true;
        }

        public static int MarkAllRead(CampaignState s)
        {
            int n = 0;
            foreach (NarrativeEntryRecord r in s?.Progress?.Narrative ?? Array.Empty<NarrativeEntryRecord>())
            {
                if (r != null && !r.Read)
                {
                    r.Read = true;
                    n++;
                }
            }
            if (n > 0)
            {
                Revision++;
            }
            return n;
        }

        /// <summary>资料页：按幕（0 = 全部）与来源（null = 全部）筛选，最新收到的在前。</summary>
        public static void Query(CampaignState s, int act, string source, List<NarrativeEntryRecord> into)
        {
            into.Clear();
            NarrativeEntryRecord[] list = s?.Progress?.Narrative ?? Array.Empty<NarrativeEntryRecord>();
            for (int i = list.Length - 1; i >= 0; i--)
            {
                NarrativeEntryRecord r = list[i];
                if (r == null || !NarrativeCatalog.TryGet(r.Id, out NarrativeEntryDef def))
                {
                    continue;
                }
                if ((act == 0 || r.Act == act) && (string.IsNullOrEmpty(source) || def.Source == source))
                {
                    into.Add(r);
                }
            }
            into.Sort((a, b) => b.Serial.CompareTo(a.Serial));
        }

        /// <summary>剧情回顾（FGR-NAR-003）：已经发生的剧情节点，按发生的先后。</summary>
        public static void Recap(CampaignState s, List<NarrativeEntryRecord> into)
        {
            into.Clear();
            foreach (NarrativeEntryRecord r in s?.Progress?.Narrative ?? Array.Empty<NarrativeEntryRecord>())
            {
                if (r != null && NarrativeCatalog.TryGet(r.Id, out NarrativeEntryDef def) && def.Node)
                {
                    into.Add(r);
                }
            }
            into.Sort((a, b) => a.Serial.CompareTo(b.Serial));
        }

        /// <summary>最新一条未读（没有未读时最新一条；空资料库 = null）。</summary>
        public static string NewestUnread(CampaignState s, string source = null)
        {
            NarrativeEntryRecord[] list = s?.Progress?.Narrative ?? Array.Empty<NarrativeEntryRecord>();
            NarrativeEntryRecord newest = null;
            NarrativeEntryRecord newestUnread = null;
            foreach (NarrativeEntryRecord r in list)
            {
                if (r == null || !NarrativeCatalog.TryGet(r.Id, out NarrativeEntryDef def) || (source != null && def.Source != source))
                {
                    continue;
                }
                if (newest == null || r.Serial > newest.Serial)
                {
                    newest = r;
                }
                if (!r.Read && (newestUnread == null || r.Serial > newestUnread.Serial))
                {
                    newestUnread = r;
                }
            }
            return (newestUnread ?? newest)?.Id;
        }

        // ── 文本 ───────────────────────────────────────────────────────────

        public static string SourceName(string source) => GameText.Get(NarrativeCatalog.SourceTextKey(source));

        public static string TitleOf(NarrativeEntryDef def) => def != null ? GameText.Get(def.TitleKey) : string.Empty;

        /// <summary>正文：监听截获以“无线电杂音 + 文字”呈现（FGR-NAR-001）。</summary>
        public static string BodyOf(NarrativeEntryDef def)
        {
            if (def == null)
            {
                return string.Empty;
            }
            string body = GameText.Get(def.BodyKey);
            if (def.Source == NarrativeCatalog.SourceIntercept)
            {
                string noise = GameText.Get("narrative.intercept.noise");
                return noise + "\n" + body + "\n" + noise;
            }
            return body;
        }

        public static string ActName(int act) => GameText.Format("narrative.act.n", act);

        // ── 打开资料库 ─────────────────────────────────────────────────────

        /// <summary>请求打开资料库并选中 <paramref name="id"/>（null = 最新一条未读）。没有界面登记时返回 false。</summary>
        public static bool Open(string id)
        {
            if (OpenRequested == null)
            {
                return false;
            }
            OpenRequested(id);
            return true;
        }

        public static void ResetForTests()
        {
            _playing = null;
            _playingState = null;
            _pausedBefore = false;
            FiledCount = 0;
            CutsceneBeginCount = 0;
            CutsceneFinishCount = 0;
            SkipCount = 0;
            ReplayCount = 0;
            CutsceneSaveCount = 0;
            LastCutsceneSave = default;
            Revision++;
        }
    }
}
