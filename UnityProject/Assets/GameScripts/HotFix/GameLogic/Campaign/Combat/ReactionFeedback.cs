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
            string observed = FeedbackCues.ObservedSiteProvider?.Invoke();
            return !string.IsNullOrEmpty(siteId) && observed == siteId;
        }

        /// <summary>一个模拟步之后调用（<see cref="CombatSite.Step"/> 末尾）。</summary>
        public static void Process(CombatSite site, CampaignState state)
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
        }

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
            bool first = named && RecordFirst(state, rid, site.SiteId);

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
            ReactionsSeen++;
            bool named = NamedReactionCatalog.IsNamed(state, reactionId);
            ReactionAttribution.AddReaction(state, site.SiteId, reactionId, 1, 0);
            bool first = named && RecordFirst(state, reactionId, site.SiteId);
            ReactionLogParty source = Party(site, state, sourceUnit);
            ReactionLog.Add(state, new ReactionLogEntry
            {
                Tick = GameClock.Ticks,
                SiteId = site.SiteId,
                SessionKind = ReactionAttribution.KindOfSite(state, site.SiteId),
                ReactionId = reactionId,
                Named = named,
                First = first,
                Count = 1,
                Source = source,
                Target = Party(site, state, targetUnit),
                FirmwareIds = source.Kind == CombatUnitKind.Machine && site.TryGetMachineWeapon(source.LogicId, out MachineWeaponInfo info) && info.FirmwareIds != null
                    ? AssemblyFirmware(info.FirmwareIds) : Array.Empty<string>(),
                Position = ground,
            });
            Revision++;
            if (!named || !IsObserved(site.SiteId))
            {
                return;
            }
            float now = RealTime();
            if (ReactionPopups.Push(PopupKey(reactionId), NamedReactionCatalog.NameOf(reactionId), 1, new Vector3(ground.x, PopupHeight, ground.y), first, false, now))
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
        private static readonly string[] ReadingKeys = { "reading:0", "reading:1", "reading:2" };

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
            _ => FeedbackCueId.ReadingDrone,
        };

        public static string ReadingNameKey(int kind) => kind switch
        {
            CombatConst.ReadingFeedZone => "reading.popup.zone",
            CombatConst.ReadingFeedEcho => "reading.popup.echo",
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

        /// <summary>写首次触发记录；已有返回 false（慢放 / 镜头推动只在返回 true 的那一次）。</summary>
        public static bool RecordFirst(CampaignState state, string reactionId, string siteId)
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
            return true;
        }

        // ─────────────────────────────── 日志辅助 ───────────────────────────────

        private static ReactionLogParty Party(CombatSite site, CampaignState state, int unitId)
        {
            var p = new ReactionLogParty();
            if (unitId <= 0)
            {
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
            if (source.Kind != CombatUnitKind.Machine || source.LogicId <= 0 || pair == 0u
                || !site.TryGetMachineWeapon(source.LogicId, out MachineWeaponInfo info) || info.FirmwareIds == null)
            {
                return Array.Empty<string>();
            }
            List<string> hits = null;
            foreach (string fw in info.FirmwareIds)
            {
                string[] tags = FirmwareKinds.TagsOf(fw);
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

        public static void ResetForTests()
        {
            FirstTriggersRecorded = 0;
            PopupPushes = 0;
            ReactionsSeen = 0;
            RealTime = () => Time.unscaledTime;
        }
    }
}
