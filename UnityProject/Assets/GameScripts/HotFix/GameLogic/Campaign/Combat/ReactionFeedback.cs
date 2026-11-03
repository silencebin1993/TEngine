using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>一个地点的反馈进给基线（<see cref="ReactionFeedback"/> 每步拿内核的累计值减它得出这一步的增量）。</summary>
    public sealed class ReactionFeedCursor
    {
        internal bool Primed;
        internal int Epoch;
        internal readonly int[] Count = new int[CombatConst.MaxReactions];
        internal readonly double[] Hostile = new double[CombatConst.MaxReactions]; // 与内核同为 double：长时间累计不丢精度
        internal double Total;
        internal readonly long[] Reading = new long[CombatConst.ReadingFeedKinds];
        /// <summary>EMG1-CHAIN-01：内核可靠计数槽（按稳定键）的基线。</summary>
        internal readonly int[] Emg = new int[EmgConst.RuleSlots];
        /// <summary>EMG2-DISC-01：Prime 那一刻已有的事件端口计数 / 子载荷代（基线：旧快照里早就发生过的不算“新发现”）。</summary>
        internal readonly bool[] PortSeen = new bool[EmgConst.EventKinds + 1];
        internal int GenSeen;
        /// <summary>EMG2-DISC-01（FGR-EMG-184）：同一“受伤者 + 留下者 + 来源”上次发友伤通知的统一时钟步（持续接触不刷屏）。</summary>
        internal readonly Dictionary<(int, int, int), long> FriendlyAt = new Dictionary<(int, int, int), long>();
    }

    /// <summary>
    /// FG2-FW-04（FG02 FGR-FW-043 反应反馈；FGT-FW-005；承接 DEBT-FG2FW03-01、DEBT-FG2FW02-02 的“读法弹字与音效”）：反应的玩家反馈与伤害归因的总入口。
    ///
    /// 每个模拟步 <see cref="CombatSite.Step"/> 之后调用 <see cref="Process"/>，读内核的累计计数（可靠：不依赖每步有上限、会被丢弃的提示事件），
    /// 按与上一步的差得出“这一步每条反应触发了几次、打在敌方身上多少额外伤害、敌方一共受了多少伤害、读法生成了几次”，然后：
    /// 1. 伤害归因（<see cref="ReactionAttribution"/>，存档）与反应日志（<see cref="ReactionLog"/>，本次会话）——与是否观察无关（FGR-BASE-021：后台结果与观察一致）。
    /// 2. 本存档里某条反应第一次报出名字：写首次触发记录（<see cref="CampaignState.ReactionFirstTriggers"/>，存档 = 反应图鉴解锁）。
    ///    镜头正在看这个地点时再短暂慢放（reaction.slowmo_seconds 真实秒、reaction.slowmo_factor 倍）与镜头轻推（各自受设置开关控制）。慢放只发生在记录新写入的那一次。
    /// 3. 反应名弹字（观察中的地点、设置开着）：同键聚合“短路 ×12”、每秒新开上限 reaction.popup_per_second（<see cref="ReactionPopups"/>）。
    /// 4. 音效与声音字幕：每步每条反应一次（同类 0.3 秒限流），不再逐次触发。
    /// 开销：每步 O(反应条数 + 3)，与单位数、触发次数无关（热更层性能红线）。
    /// </summary>
    public static class ReactionFeedback
    {
        /// <summary>自检可替换的真实时间源（弹字按真实秒）。</summary>
        public static Func<float> RealTime = () => Time.unscaledTime;

        public static int Revision { get; private set; }
        /// <summary>本进程累计写入的首次触发记录条数（自检）。</summary>
        public static int FirstTriggersRecorded { get; private set; }
        /// <summary>本进程累计的弹字推送次数（按反应 / 读法键；自检）。</summary>
        public static long PopupPushes { get; private set; }
        /// <summary>本进程累计处理到的反应次数（按增量；自检对照内核计数）。</summary>
        public static long ReactionsSeen { get; private set; }

        public static float SlowMotionSeconds => Math.Max(0f, ReactionPopups.Tuning("reaction.slowmo_seconds", 0.3f));
        public static float SlowMotionFactor => Mathf.Clamp(ReactionPopups.Tuning("reaction.slowmo_factor", 0.25f), 0.05f, 0.95f);
        public static float NudgeSeconds => Math.Max(0.05f, ReactionPopups.Tuning("reaction.nudge_seconds", 0.45f));
        public static float NudgeFraction => Mathf.Clamp(ReactionPopups.Tuning("reaction.nudge_fraction", 0.05f), 0.005f, 0.25f);
        /// <summary>弹字离地高度（米）。</summary>
        public static float PopupHeight => ReactionPopups.Tuning("reaction.popup_height", 1.6f);

        /// <summary>镜头正在观察这个地点（严格：观察地点未知时视为不观察——不在世界里的测试地点不会慢放 / 推镜头 / 弹字）。</summary>
        public static bool IsObserved(string siteId)
        {
            // FG5-RND-03：靶场的仿真地点在家园里——镜头看着家园时，靶场里的反应照样弹字 / 慢放。
            siteId = Economy.TestRangeService.HostSiteOf(siteId);
            string observed = FeedbackCues.ObservedSiteProvider?.Invoke();
            return !string.IsNullOrEmpty(siteId) && observed == siteId;
        }

        /// <summary>一个模拟步之后调用（<see cref="CombatSite.Step"/> 末尾）。
        /// <paramref name="countsOnly"/> = true：两步之间的手动开火后调用，只结算按游标取差值的部分（旧表 / 涌现计数、读法、伤害），
        /// 不碰反应装甲与友伤列表——它们没有游标，上一步已交出的条目要到下一步开始才去掉，开火时新加的条目随下一步一起交（否则重复通知）。</summary>
        public static void Process(CombatSite site, CampaignState state, bool countsOnly = false)
        {
            if (site == null || site.IsDisposed)
            {
                return;
            }
            CombatKernel k = site.Kernel;
            ReactionFeedCursor cur = site.FeedCursor;
            if (!cur.Primed || cur.Epoch != k.FeedEpoch)
            {
                Prime(k, cur);
                return;
            }
            bool observed = IsObserved(site.SiteId);
            float now = RealTime();

            double total = k.DamageDealtToHostile;
            double dTotal = total - cur.Total;
            cur.Total = total;
            if (dTotal > 0)
            {
                ReactionAttribution.AddDamage(state, site.SiteId, dTotal);
            }

            int n = Math.Min(k.ReactionRuleCount, CombatConst.MaxReactions);
            for (int i = 0; i < n; i++)
            {
                int c = k.ReactionCountOf(i);
                int dc = c - cur.Count[i];
                cur.Count[i] = c;
                double h = k.ReactionHostileDamageOf(i);
                double dh = Math.Max(0.0, h - cur.Hostile[i]);
                cur.Hostile[i] = h;
                if (dc <= 0)
                {
                    continue;
                }
                string rid = NamedReactionCatalog.IdOfRule(i);
                if (rid == null)
                {
                    continue;
                }
                OnReaction(site, state, k, i, rid, dc, dh, observed, now);
            }

            for (int kind = 0; kind < CombatConst.ReadingFeedKinds; kind++)
            {
                long c = k.ReadingFeedCountOf(kind);
                long d = c - cur.Reading[kind];
                cur.Reading[kind] = c;
                if (d <= 0)
                {
                    continue;
                }
                OnReading(k, kind, d, observed, now);
            }

            // EMG1-CHAIN-01（FGR-EMG-067）：涌现规则的可靠计数——只读脏列表（O(本步变化的槽)），沿用按稳定键的反应计数、首次触发记录与弹字聚合。
            int dirty = k.EmgDirtyCount;
            for (int j = 0; j < dirty; j++)
            {
                int slot = k.EmgDirtyAt(j);
                int c = k.EmgRuleCountAt(slot);
                int dc = c - cur.Emg[slot];
                cur.Emg[slot] = c;
                if (dc <= 0)
                {
                    continue;
                }
                EmgRuleSeen += dc;
                ReactionAttribution.AddInteractions(state, site.SiteId, dc); // EMG2-DISC-01（B19）：交互次数含没有图鉴名字的通道规则
                string rid = NamedReactionCatalog.IdOfKey(k.EmgRuleKeyAt(slot));
                if (rid == null)
                {
                    ReactionAttribution.AddReaction(state, site.SiteId, ReactionAttribution.RuleRowId(k.EmgRuleKeyAt(slot)), dc, 0); // B19“各交互次数”：无名规则也有行
                    continue;
                }
                EmgRuleFeed f = k.EmgRuleFeedAt(slot);
                var at = new Vector2((float)f.Pos.x, (float)f.Pos.y);
                OnCountedReaction(site, state, rid, dc, f.Src, f.Target, at, observed, now);
                site.OnDeclCoreReaction(state, f.Src, rid, dc, at); // DEBT-EMG1CHAIN01-06：核心固件冷却 / 暴露 / 提示与旧轨一致
            }
            if (countsOnly)
            {
                return;
            }

            // EMG1-FW-01（FGR-EMG-233 第 6 条）：反应装甲块数只在变化的步进脏列表（O(本步变化数)）——己方机器降到 0 块发一次信息级通知（同车聚合、可定位）。
            int eraDirty = k.EmgEraDirtyCount;
            for (int j = 0; j < eraDirty; j++)
            {
                OnEraChanged(site, state, k, k.EmgEraDirtyAt(j));
            }

            // EMG1-FW-01 / EMG2-DISC-01（FGR-EMG-184）：己方被己方留下的物质 / 能量 / 破片伤到——警告级友伤通知（同类聚合、可定位）；因果链“友伤”节点在内核记。O(本步友伤数)。
            int ff = k.EmgFriendlyCount;
            for (int j = 0; j < ff; j++)
            {
                OnFriendly(site, state, k, cur, k.EmgFriendlyAt(j));
            }

            // EMG2-DISC-01（FGR-EMG-182）：交互伤害按因果链平分到机器、按规则键统计（内核已按键 + 归属合并，O(本步不同组合数)）；最长链深。与是否观察无关。
            int rows = k.EmgAttrCount;
            for (int j = 0; j < rows; j++)
            {
                ReactionAttribution.AddInteractionDamage(state, site, k.EmgAttrAt(j));
            }
            if (k.EmgStepMaxDepth > 0)
            {
                ReactionAttribution.NoteChainDepth(state, site.SiteId, k.EmgStepMaxDepth);
            }
            CheckGrammar(site, state, k, cur); // EMG2-DISC-01（FGR-EMG-183 语法条目）：O(事件种类数)
        }

        /// <summary>语法发现：第一次组成某代子弹药、第一次触发某种事件。比“新出现的位 / 计数从 0 变正”，Prime 时已有的不算（读旧档不补弹）。</summary>
        private static void CheckGrammar(CombatSite site, CampaignState state, CombatKernel k, ReactionFeedCursor cur)
        {
            int gen = k.EmgGenSeenMask;
            int newGen = gen & ~cur.GenSeen;
            if (newGen != 0)
            {
                cur.GenSeen = gen;
                for (int g = 1; g <= EmgConst.MaxGen; g++)
                {
                    if ((newGen & (1 << g)) != 0)
                    {
                        ReactionDiscoveryService.RecordGrammar(state, ReactionDiscoveryService.GenEntry(g), site.SiteId);
                    }
                }
            }
            for (int e = 1; e <= EmgConst.EventKinds; e++)
            {
                if (!cur.PortSeen[e] && k.EmgPortCountOf((EmgEventKind)e) > 0)
                {
                    cur.PortSeen[e] = true;
                    ReactionDiscoveryService.RecordGrammar(state, ReactionDiscoveryService.EventEntry(e), site.SiteId);
                }
            }
        }

        /// <summary>本进程发出的友伤通知条数（自检）。</summary>
        public static long FriendlyPosted { get; private set; }

        private static void OnFriendly(CombatSite site, CampaignState state, CombatKernel k, ReactionFeedCursor cur, int3 hit)
        {
            // 只报己方受伤（敌人被敌人自己的破片打到不发通知）
            if (!k.TryGetUnit(hit.x, out CombatUnitView v) || v.Faction != CombatFaction.Player)
            {
                return;
            }
            // 留下它的单位已不在场：还登记着的机器算己方，其余（阵亡的敌人）不算友伤
            bool ownSide = k.TryGetUnit(hit.y, out CombatUnitView o) ? o.Faction == CombatFaction.Player : site.TryGetMachineOfUnit(hit.y, out _);
            if (!ownSide)
            {
                return;
            }
            // 物质按节拍持续扣血：同一组合每 emg.friendly.notify_seconds 游戏秒最多一条（破片一次一条，不节流）
            if (hit.z != EmgFriendlyCode.Fragment)
            {
                long gap = (long)(ReactionPopups.Tuning("emg.friendly.notify_seconds", 1f) * GameClock.StepHz);
                (int, int, int) key = (hit.x, hit.y, hit.z);
                if (cur.FriendlyAt.TryGetValue(key, out long at) && GameClock.Ticks >= at && GameClock.Ticks - at < gap)
                {
                    return;
                }
                if (cur.FriendlyAt.Count >= 256)
                {
                    cur.FriendlyAt.Clear(); // 单位 ID 只增不复用：只留近期的组合（清掉最多多发一条）
                }
                cur.FriendlyAt[key] = GameClock.Ticks;
            }
            double2 p = v.Position;
            string victim = EmgChainView.PartyName(site, state, hit.x), owner = EmgChainView.PartyName(site, state, hit.y);
            string detail = hit.z == EmgFriendlyCode.Fragment ? GameText.Format("emg.friendly.fragment", victim, owner)
                : hit.z == EmgFriendlyCode.Energy ? GameText.Format("emg.friendly.energy", victim, owner)
                : hit.z == EmgFriendlyCode.Reaction ? GameText.Format("emg.friendly.reaction", victim, owner)
                : GameText.Format("emg.friendly.matter", victim, owner, Emergence.EmgMatterCatalog.NameOf(hit.z));
            Notifications.NotificationCenter.Post("friendly_fire", detail, new Vector3((float)p.x, 0f, (float)p.y));
            FriendlyPosted++;
        }

        /// <summary>本进程经脏列表处理到的反应装甲块数变化次数 / 发出的耗尽通知条数（自检）。</summary>
        public static long EraChangesSeen { get; private set; }
        public static long EraDepletedPosted { get; private set; }

        private static void OnEraChanged(CombatSite site, CampaignState state, CombatKernel k, int unitId)
        {
            EraChangesSeen++;
            // 只报己方（机器 / 炮塔）的耗尽；敌人身上的反应装甲耗尽不发通知（悬停与因果链照样可查）
            if (!k.TryGetEra(unitId, out int intact, out _, out _, out float nextIn) || intact > 0 || !k.TryGetUnit(unitId, out CombatUnitView v)
                || v.Faction != CombatFaction.Player)
            {
                return;
            }
            double2 p = v.Position;
            string detail = GameText.Format("emg.era.depleted", EmgChainView.PartyName(site, state, unitId), nextIn.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            Notifications.NotificationCenter.Post("era_depleted", detail, new Vector3((float)p.x, 0f, (float)p.y));
            EraDepletedPosted++;
        }

        /// <summary>本进程经脏列表处理到的涌现规则次数（自检对照内核计数）。</summary>
        public static long EmgRuleSeen { get; private set; }

        /// <summary>纪元变了（读档 / 重新登记规则）或第一次：把内核当前的累计值当基线，不产生反馈。</summary>
        public static void Prime(CombatKernel k, ReactionFeedCursor cur)
        {
            for (int i = 0; i < CombatConst.MaxReactions; i++)
            {
                cur.Count[i] = k.ReactionCountOf(i);
                cur.Hostile[i] = k.ReactionHostileDamageOf(i);
            }
            cur.Total = k.DamageDealtToHostile;
            for (int kind = 0; kind < CombatConst.ReadingFeedKinds; kind++)
            {
                cur.Reading[kind] = k.ReadingFeedCountOf(kind);
            }
            for (int i = 0; i < EmgConst.RuleSlots; i++)
            {
                cur.Emg[i] = k.EmgRuleCountAt(i);
            }
            for (int e = 1; e <= EmgConst.EventKinds; e++)
            {
                cur.PortSeen[e] = k.EmgPortCountOf((EmgEventKind)e) > 0;
            }
            cur.GenSeen = k.EmgGenSeenMask;
            cur.Epoch = k.FeedEpoch;
            cur.Primed = true;
        }

        private static void OnReaction(CombatSite site, CampaignState state, CombatKernel k, int ruleIndex, string rid, int count, double hostileDamage,
            bool observed, float now)
        {
            ReactionsSeen += count;
            bool named = NamedReactionCatalog.IsNamed(state, rid);
            double2 p = k.ReactionLastPosOf(ruleIndex);
            var ground = new Vector2((float)p.x, (float)p.y);

            ReactionAttribution.AddReaction(state, site.SiteId, rid, count, hostileDamage);
            bool first = named && RecordFirst(state, rid, site.SiteId, new Vector3(ground.x, 0f, ground.y));

            int sourceId = k.ReactionLastSourceOf(ruleIndex);
            ReactionLogParty source = Party(site, state, sourceId);
            ReactionLog.Add(state, new ReactionLogEntry
            {
                Tick = GameClock.Ticks,
                SiteId = site.SiteId,
                SessionKind = ReactionAttribution.KindOfSite(state, site.SiteId),
                ReactionId = rid,
                Named = named,
                First = first,
                Count = count,
                Source = source,
                Target = Party(site, state, k.ReactionLastTargetOf(ruleIndex)),
                FirmwareIds = ParticipatingFirmware(site, source, k.ReactionRule(ruleIndex).Pair),
                Position = ground,
            });
            Revision++;

            if (!named)
            {
                return; // 未开放命名的反应照样生效、照样记归因与日志（显示“未知反应”），但不报名字、不弹字、不算首次触发。
            }
            string name = NamedReactionCatalog.NameOf(rid);
            // 声音与常显字幕：这一步这条反应一次（聚合）。声音同类最小间隔 0.3 秒由反馈目录限流；字幕用同一句“短路！”，字幕条按同文合并计数，不刷屏。
            FeedbackCues.RaiseAt(FeedbackCueId.TagReaction, ground, GameText.Format("reaction.cue", name));
            if (!observed)
            {
                return;
            }
            var world = new Vector3(ground.x, PopupHeight, ground.y);
            if (ReactionPopups.Push(PopupKey(rid), name, count, world, first, false, now))
            {
                PopupPushes++;
            }
            if (first)
            {
                if (GameSettings.ReactionSlowMotionEnabled)
                {
                    GameClock.BeginSlowMotion(SlowMotionSeconds, SlowMotionFactor);
                }
                CameraNudge.Begin(new Vector3(ground.x, 0f, ground.y), NudgeSeconds, NudgeFraction);
            }
        }

        /// <summary>装配反应（标记跳转 / 熔穿过载，内核发不丢的玩法事件 ReactionFired）：一次发动 = 一条日志、归因里记一次（这类反应的额外伤害不单独计，伤害 0）、
        /// 本存档第一次报出名字时同样写首次触发并慢放 / 推镜头 / 弹字。声音与字幕沿用它们自己的提示时刻（MarkJump / MeltOverload）。</summary>
        public static void OnAssemblyReaction(CombatSite site, CampaignState state, string reactionId, int sourceUnit, int targetUnit, Vector2 ground)
        {
            if (site == null || site.IsDisposed || string.IsNullOrEmpty(reactionId))
            {
                return;
            }
            OnCountedReaction(site, state, reactionId, 1, sourceUnit, targetUnit, ground, IsObserved(site.SiteId), RealTime());
        }

        /// <summary>
        /// 按次数结算一条没有标签规则行的反应（装配反应一次一条；EMG1-CHAIN-01 涌现规则按脏列表一次 <paramref name="count"/> 条）：日志与归因记 count 次（这类反应的额外伤害不单独计），
        /// 本存档第一次报出名字时写首次触发、镜头看着这个地点时慢放 / 推镜头；弹字同键聚合“标记跳转 ×200”、每秒新开上限沿用 reaction.popup_per_second。
        /// </summary>
        private static void OnCountedReaction(CombatSite site, CampaignState state, string reactionId, int count, int sourceUnit, int targetUnit, Vector2 ground, bool observed, float now)
        {
            ReactionsSeen += count;
            bool named = NamedReactionCatalog.IsNamed(state, reactionId);
            ReactionAttribution.AddReaction(state, site.SiteId, reactionId, count, 0);
            bool first = named && RecordFirst(state, reactionId, site.SiteId, new Vector3(ground.x, 0f, ground.y));
            ReactionLogParty source = Party(site, state, sourceUnit);
            // EMG2-REACT-01：已端口化的具名交互（表里还有沾染配对）参与固件按配对认、并补旧标签轨的声音 + 字幕；装配反应（无配对）沿用核心固件与它们自己的提示时刻。
            uint pair = NamedReactionCatalog.TryGet(reactionId, out GameConfig.fg.Reaction row) ? NamedReactionCatalog.BitOf(row.TagA) | NamedReactionCatalog.BitOf(row.TagB) : 0u;
            ReactionLog.Add(state, new ReactionLogEntry
            {
                Tick = GameClock.Ticks,
                SiteId = site.SiteId,
                SessionKind = ReactionAttribution.KindOfSite(state, site.SiteId),
                ReactionId = reactionId,
                Named = named,
                First = first,
                Count = count,
                Source = source,
                Target = targetUnit > 0 ? Party(site, state, targetUnit) : new ReactionLogParty { Known = true, LabelKey = "reaction.log.ground" }, // EMG2-REACT-02：斑块层结算没有目标单位
                FirmwareIds = pair != 0u ? ParticipatingFirmware(site, source, pair)
                    : FirmwareOf(site, source) is string[] fwOfSource ? AssemblyFirmware(fwOfSource) : Array.Empty<string>(),
                Position = ground,
            });
            Revision++;
            if (named)
            {
                GuidanceHooks.Raise(GuidanceHooks.ReactionFirstNamed);
                if (pair != 0u)
                {
                    FeedbackCues.RaiseAt(FeedbackCueId.TagReaction, ground, GameText.Format("reaction.cue", NamedReactionCatalog.NameOf(reactionId)));
                }
            }
            if (!named || !observed)
            {
                return;
            }
            if (ReactionPopups.Push(PopupKey(reactionId), NamedReactionCatalog.NameOf(reactionId), count, new Vector3(ground.x, PopupHeight, ground.y), first, false, now))
            {
                PopupPushes++;
            }
            if (first)
            {
                if (GameSettings.ReactionSlowMotionEnabled)
                {
                    GameClock.BeginSlowMotion(SlowMotionSeconds, SlowMotionFactor);
                }
                CameraNudge.Begin(new Vector3(ground.x, 0f, ground.y), NudgeSeconds, NudgeFraction);
            }
        }

        /// <summary>装配反应不读标签：参与的固件取这台机器生效固件里的核心 / 限制器类（标记跳转、过载都是核心固件带进来的）。</summary>
        private static string[] AssemblyFirmware(string[] firmware)
        {
            List<string> hits = null;
            foreach (string fw in firmware)
            {
                if (FirmwareKinds.IsCore(fw))
                {
                    (hits ??= new List<string>()).Add(fw);
                }
            }
            return hits != null ? hits.ToArray() : Array.Empty<string>();
        }

        private static void OnReading(CombatKernel k, int kind, long count, bool observed, float now)
        {
            double2 p = k.ReadingFeedPosOf(kind);
            var ground = new Vector2((float)p.x, (float)p.y);
            FeedbackCues.RaiseAt(ReadingCue(kind), ground);
            if (!observed)
            {
                return;
            }
            var world = new Vector3(ground.x, PopupHeight, ground.y);
            if (ReactionPopups.Push(ReadingKeys[kind], GameText.Get(ReadingNameKey(kind)), (int)Math.Min(int.MaxValue, count), world, false, true, now))
            {
                PopupPushes++;
            }
        }

        private static readonly Dictionary<string, string> PopupKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly string[] ReadingKeys = { "reading:0", "reading:1", "reading:2", "reading:3", "reading:4" };

        /// <summary>弹字的聚合键（按反应 ID 缓存，不每步拼字符串）。</summary>
        public static string PopupKey(string reactionId)
        {
            if (!PopupKeys.TryGetValue(reactionId, out string key))
            {
                key = "reaction:" + reactionId;
                PopupKeys[reactionId] = key;
            }
            return key;
        }

        public static FeedbackCueId ReadingCue(int kind) => kind switch
        {
            CombatConst.ReadingFeedZone => FeedbackCueId.ReadingZone,
            CombatConst.ReadingFeedEcho => FeedbackCueId.ReadingEcho,
            CombatConst.ReadingFeedThorns => FeedbackCueId.ReadingThorns,
            CombatConst.ReadingFeedEra => FeedbackCueId.ReadingThorns, // EMG1-FW-01：起爆沿用反伤音效钩子（占位音，DEBT-EMG1FW01-01）
            _ => FeedbackCueId.ReadingDrone,
        };

        public static string ReadingNameKey(int kind) => kind switch
        {
            CombatConst.ReadingFeedZone => "reading.popup.zone",
            CombatConst.ReadingFeedEcho => "reading.popup.echo",
            CombatConst.ReadingFeedThorns => "reading.popup.thorns",
            CombatConst.ReadingFeedEra => "reading.popup.era",
            _ => "reading.popup.drone",
        };

        // ─────────────────────────────── 首次触发 / 图鉴 ───────────────────────────────

        /// <summary>这条反应在本存档里报出过名字吗（= 反应图鉴条目已解锁）。</summary>
        public static bool IsFirstTriggered(CampaignState state, string reactionId) => FirstRecordOf(state, reactionId) != null;

        public static ReactionFirstTriggerRecord FirstRecordOf(CampaignState state, string reactionId)
        {
            ReactionFirstTriggerRecord[] all = state?.ReactionFirstTriggers;
            if (all == null || string.IsNullOrEmpty(reactionId))
            {
                return null;
            }
            foreach (ReactionFirstTriggerRecord r in all)
            {
                if (r != null && r.ReactionId == reactionId)
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>写首次触发记录 = 首次发现（FGR-EMG-190，唯一写入口）；已有返回 false（慢放 / 镜头推动 / “新发现”通知只在返回 true 的那一次）。</summary>
        public static bool RecordFirst(CampaignState state, string reactionId, string siteId, Vector3? at = null)
        {
            if (state == null || string.IsNullOrEmpty(reactionId) || IsFirstTriggered(state, reactionId))
            {
                return false;
            }
            var list = new List<ReactionFirstTriggerRecord>(state.ReactionFirstTriggers ?? Array.Empty<ReactionFirstTriggerRecord>())
            {
                new ReactionFirstTriggerRecord { ReactionId = reactionId, Tick = GameClock.Ticks, SiteId = siteId ?? string.Empty },
            };
            list.Sort((a, b) => string.CompareOrdinal(a.ReactionId, b.ReactionId));
            state.ReactionFirstTriggers = list.ToArray();
            FirstTriggersRecorded++;
            Revision++;
            // FG2-FW-05（FGR-UX-051）：统一图鉴的反应条目在第一次打出时解锁（机制条目跨存档；本存档的首次触发时刻仍记在上面）。
            Progression.MechanicCodex.Unlock(Progression.MechanicCodex.ReactionEntryId(reactionId));
            ReactionDiscoveryService.OnReactionDiscovered(state, reactionId, siteId, at);
            return true;
        }

        // ─────────────────────────────── 日志辅助 ───────────────────────────────

        internal static ReactionLogParty Party(CombatSite site, CampaignState state, int unitId)
        {
            var p = new ReactionLogParty();
            if (unitId <= 0)
            {
                return p;
            }
            // FG5-RND-03（FG-GAP-061）：地点起过名字的单位（训练靶、靶场投影与投影靶）按名字记，不按“敌方单位 / 机器”通用名。
            if (site.TryGetUnitLabel(unitId, out string labelKey, out string labelArg, out bool labelArgIsKey, out _))
            {
                p.Known = true;
                p.Kind = site.Kernel.TryGetUnit(unitId, out CombatUnitView lv) ? lv.Kind : CombatUnitKind.Structure;
                p.LabelKey = labelKey;
                p.LabelArg = labelArg;
                p.LabelArgIsKey = labelArgIsKey;
                p.UnitId = unitId;
                return p;
            }
            if (site.TryGetMachineOfUnit(unitId, out int logicId))
            {
                p.Known = true;
                p.Kind = CombatUnitKind.Machine;
                p.LogicId = logicId;
                return p;
            }
            if (site.TryGetEnemyOfUnit(unitId, out string enemyId))
            {
                p.Known = true;
                p.Kind = CombatUnitKind.Enemy;
                p.ContentId = site.EnemyTypeOf(state, enemyId);
                return p;
            }
            if (site.Kernel.TryGetUnit(unitId, out CombatUnitView v))
            {
                p.Known = true;
                p.Kind = v.Kind;
            }
            return p;
        }

        /// <summary>触发者（机器）这组参数里生效的固件中，产生的标签属于这条反应配料的那些（“参与的固件”）。</summary>
        private static string[] ParticipatingFirmware(CombatSite site, in ReactionLogParty source, uint pair)
        {
            string[] firmware = FirmwareOf(site, source);
            if (pair == 0u || firmware == null)
            {
                return Array.Empty<string>();
            }
            List<string> hits = null;
            uint shock = NamedReactionCatalog.BitOf("Shock"), fire = NamedReactionCatalog.BitOf("Fire");
            var one = new string[1];
            foreach (string fw in firmware)
            {
                // EMG2-REACT-02：电击 / 燃烧不再是这条固件的旧标签时，看它是否写同一端口的注入（连锁电弧写 en.elec、过载写 en.heat）
                one[0] = fw;
                if (((pair & shock) != 0u && Emergence.EmgCatalog.PartWriteAmount("en.elec", Emergence.EmgRole.Inject, one) > 0f)
                    || ((pair & fire) != 0u && Emergence.EmgCatalog.PartWriteAmount("en.heat", Emergence.EmgRole.Inject, one) > 0f))
                {
                    (hits ??= new List<string>()).Add(fw);
                    continue;
                }
                string[] tags = Emergence.EmgProgramCompiler.LiveTagsOf(fw);
                if (tags == null)
                {
                    continue;
                }
                foreach (string tag in tags)
                {
                    if ((NamedReactionCatalog.BitOf(tag) & pair) != 0u)
                    {
                        (hits ??= new List<string>()).Add(fw);
                        break;
                    }
                }
            }
            return hits != null ? hits.ToArray() : Array.Empty<string>();
        }

        /// <summary>触发者生效的固件：登记的机器读武器参数；靶场投影读地点给它记下的固件（FG5-RND-03）。其余为 null。</summary>
        private static string[] FirmwareOf(CombatSite site, in ReactionLogParty source)
        {
            if (source.UnitId > 0 && site.TryGetUnitLabel(source.UnitId, out _, out _, out string[] labelled))
            {
                return labelled;
            }
            if (source.Kind == CombatUnitKind.Machine && source.LogicId > 0 && site.TryGetMachineWeapon(source.LogicId, out MachineWeaponInfo info))
            {
                return info.FirmwareIds;
            }
            return null;
        }

        public static void ResetForTests()
        {
            FirstTriggersRecorded = 0;
            PopupPushes = 0;
            ReactionsSeen = 0;
            RealTime = () => Time.unscaledTime;
        }
    }
}
