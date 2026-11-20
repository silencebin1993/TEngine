using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>fg.TbWorldEvent 一行的运行时视图（"none" 已换成空串）。</summary>
    public sealed class EventDirectorDef
    {
        public string Id;
        public bool Threat;
        public bool Major;
        public string Trigger;
        public double IntervalMinDays;
        public double IntervalMaxDays;
        public double CooldownDays;
        public int Weight;
        public int ActMin;
        public int ActMax;
        public string Group;
        public double WarningSeconds;
        public double DurationSeconds;
        public string Reward;
        public string RewardFaction;
        public bool StoryFirst;
        public bool HomeOnly;
        public float WakeRadius;
        public string NameKey;
        public string CodexId;
        public string Hook;
        public string SfxWarn;
        public int SortOrder;
    }

    /// <summary>
    /// 一种事件的玩法（FG12-EVT-02～06 各自登记一个）。导演负责何时、互斥、预警计时、通知、历史、奖励与唤醒；处理器负责事件本身（落点、单位、效果）。
    /// 处理器自己的状态进存档时放在各自的域里；导演只按 <see cref="EventDirectorInstanceRecord.Serial"/> 认实例。
    /// </summary>
    public abstract class EventDirectorHandler
    {
        public abstract string EventId { get; }

        /// <summary>额外的开始条件（例如“第一幕末起”）；不满足时 <paramref name="reasonKey"/> 给原因文本键。</summary>
        public virtual bool CanStart(CampaignState s, EventDirectorDef def, out string reasonKey)
        {
            reasonKey = null;
            return true;
        }

        /// <summary>开始预警前定地点（改 <see cref="EventDirectorInstanceRecord.X"/> / Y / RegionId；默认归还核心）。返回 false = 这次放弃（重新抽间隔）。</summary>
        public virtual bool Prepare(CampaignState s, EventDirectorInstanceRecord r) => true;

        public virtual void OnWarn(CampaignState s, EventDirectorInstanceRecord r) { }

        public virtual void OnStart(CampaignState s, EventDirectorInstanceRecord r) { }

        /// <summary>FG12-EVT-04：瞬发事件（开始那一步当场结束，例如轨道坠落）——导演不发“开始（时限）”与成功的“结束”通知，由处理器自己发的一条结果通知代替。</summary>
        public virtual bool Instant => false;

        /// <summary>
        /// 结束（<paramref name="outcome"/> 已定）；reward=fixed 的奖励在这里给。预警阶段被撤回（静默夜 / 剧情突袭后来才排上）也走这里，
        /// outcome = <see cref="EventDirectorService.OutcomePostponed"/>：收回 OnWarn 放出的效果、不给奖励；之后补上时是新的 Serial，重新 Prepare / OnWarn。
        /// </summary>
        public virtual void OnEnd(CampaignState s, EventDirectorInstanceRecord r, string outcome) { }

        /// <summary>FG12-EVT-03：结束前改写结果（写历史、发通知之前）。例如信号幽灵：时限到时机器已被击倒（瘫痪）= 已应对，不是超时。默认原样。</summary>
        public virtual string FinalOutcome(CampaignState s, EventDirectorInstanceRecord r, string outcome) => outcome;
    }

    /// <summary>
    /// FG12-EVT-01 事件导演（FG10 FGR-EVT-001～006，FGT-EVT-001 / 008）。与 FG19 的战斗事件总线（evt.*，内核里每步排空的队列）是两层东西（FGR-EMG-068）。
    /// - 事件入表（fg.TbWorldEvent）；随机事件按“锚点 + 抽到的间隔 ÷ 频率倍率”到期，倍率 = 难度 eventRate × 设置“事件频率”档位（改设置立即生效）。
    /// - 互斥（FGR-EVT-002）：重大威胁每 2 个游戏日最多一次、不与静默夜（含预警，按整个时间窗）与剧情突袭重叠；正向的家园事件不与正在逼近的突袭同在，
    ///   已经来了的在突袭预警时提前离开。被挡住的推迟到条件解除（只读静默夜与突袭导演的状态，不改它们的调度）。
    /// - 预警（FGR-EVT-003）按整数世界步计时：暂停不走、倍速同比例、观察与否一致。通知 / 提示音走 world_event（反馈时刻 WorldEventWarning / WorldEventNotice），
    ///   离家期间自动记进离家报告“事件”段（FG-GAP-099）；第一次发生触发引导钩子并解锁图鉴（FGR-EVT-004）；结束写事件历史。
    /// - 独立随机数流（FGR-EVT-005）：种子由世界种子派生，与序号一起进存档。
    /// - 完成走 <see cref="LootPickService.GrantEventPacket"/>（DEBT-FG8LOOT02-05）；开始时唤醒附近休眠据点走 <see cref="WorldOutpostSystem.WakeNow"/>（DEBT-FG0ARCH06-02）。
    /// 每步 O(进行中的事件数)；每 event.director.check_seconds 评估一次 O(事件种类 + 突袭计划数)。热更层，只看步序号与存档，与观察无关。
    /// </summary>
    public static class EventDirectorService
    {
        public const string KindThreat = "threat";
        public const string TriggerRandom = "random";
        public const string TriggerCondition = "condition";
        public const string TriggerSite = "site";
        public const string RewardPacket = "packet";
        public const string OutcomeSuccess = "success";
        public const string OutcomeFailed = "failed";
        public const string OutcomeExpired = "expired";
        public const string OutcomeLeftRaid = "left_raid";
        public const string OutcomeCancelled = "cancelled";
        public const string OutcomePostponed = "postponed";
        public const int FrequencyOff = 0;
        public const int FrequencyLow = 1;
        public const int FrequencyStandard = 2;
        public const int FrequencyHigh = 3;
        public const int StateWarned = 1;
        public const int StateActive = 2;
        public const string BlockSilentNight = "event_director.block.silent_night";
        public const string BlockStoryRaid = "event_director.block.story_raid";
        public const string BlockRaid = "event_director.block.raid";
        public const string BlockMajorGap = "event_director.block.major_gap";
        public const string BlockGroup = "event_director.block.group";
        public const string BlockCooldown = "event_director.block.cooldown";
        public const string BlockOff = "event_director.block.off";
        public const string ChangedEvent = "EventDirector.Changed";

        private const uint SaltSeed = 0x45564431u; // "EVD1"
        private const uint SaltGap = 0x45564432u;
        private const uint SaltPick = 0x45564433u;

        private static bool _installed;
        private static readonly Dictionary<string, EventDirectorHandler> Handlers = new Dictionary<string, EventDirectorHandler>(StringComparer.Ordinal);
        private static List<EventDirectorDef> _defs;
        private static List<EventDirectorDef> _defsOverride;
        private static readonly List<EventDirectorDef> CandScratch = new List<EventDirectorDef>(8);

        /// <summary>任何事件状态变了 +1（面板据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;
        public static bool Installed => _installed;
        public static int WarnCount { get; private set; }
        public static int StartCount { get; private set; }
        public static int EndCount { get; private set; }
        public static long Evaluations { get; private set; }
        public static double MaxEvaluateMs { get; private set; }

        public static void Install()
        {
            _installed = true;
            Revision++;
        }

        /// <summary>自检之间复位（不动存档）：断开、清空处理器与表覆盖。</summary>
        public static void ResetForTests()
        {
            _installed = false;
            Handlers.Clear();
            _defsOverride = null;
            _defs = null;
            WarnCount = StartCount = EndCount = 0;
            Evaluations = 0;
            MaxEvaluateMs = 0;
            Revision++;
        }

        /// <summary>FG12-EVT-02～06：登记一种事件的处理器（同一事件后登记的覆盖先登记的）。没有处理器的事件导演不安排。</summary>
        public static void Register(EventDirectorHandler handler)
        {
            if (handler != null && !string.IsNullOrEmpty(handler.EventId))
            {
                Handlers[handler.EventId] = handler;
                Revision++;
            }
        }

        public static void Unregister(string eventId)
        {
            if (eventId != null && Handlers.Remove(eventId))
            {
                Revision++;
            }
        }

        public static bool HasHandler(string eventId) => eventId != null && Handlers.ContainsKey(eventId);

        // ── 表 ─────────────────────────────────────────────────────────────────

        /// <summary>全部事件（按 sortOrder）。</summary>
        public static IReadOnlyList<EventDirectorDef> Defs
        {
            get
            {
                if (_defsOverride != null)
                {
                    return _defsOverride;
                }
                if (_defs == null)
                {
                    _defs = LoadDefs();
                }
                return _defs;
            }
        }

        /// <summary>自检：用夹具事件代替表（null = 恢复读表）。</summary>
        public static void OverrideDefsForTests(List<EventDirectorDef> defs)
        {
            _defsOverride = defs;
            Revision++;
        }

        public static void ReloadDefs() => _defs = null;

        public static EventDirectorDef Def(string id)
        {
            foreach (EventDirectorDef d in Defs)
            {
                if (d.Id == id)
                {
                    return d;
                }
            }
            return null;
        }

        private static List<EventDirectorDef> LoadDefs()
        {
            var list = new List<EventDirectorDef>(8);
            try
            {
                IReadOnlyList<GameConfig.fg.WorldEvent> rows = ConfigSystem.Instance?.Tables?.TbWorldEvent?.DataList;
                if (rows == null)
                {
                    Log.Warning("[EventDirector] fg.TbWorldEvent 缺失：事件导演不安排任何事件。");
                    return list;
                }
                foreach (GameConfig.fg.WorldEvent r in rows)
                {
                    if (r == null || string.IsNullOrEmpty(r.Id))
                    {
                        continue;
                    }
                    list.Add(new EventDirectorDef
                    {
                        Id = r.Id, Threat = r.Kind == KindThreat, Major = r.Major == 1, Trigger = r.Trigger,
                        IntervalMinDays = r.IntervalMinDays, IntervalMaxDays = Math.Max(r.IntervalMinDays, r.IntervalMaxDays), CooldownDays = Math.Max(0, r.CooldownDays),
                        Weight = Math.Max(1, r.Weight), ActMin = r.ActMin, ActMax = r.ActMax, Group = None(r.Group),
                        WarningSeconds = Math.Max(0, r.WarningSeconds), DurationSeconds = Math.Max(1, r.DurationSeconds), Reward = r.Reward, RewardFaction = None(r.RewardFaction),
                        StoryFirst = r.StoryFirst == 1, HomeOnly = r.HomeOnly == 1, WakeRadius = Math.Max(0, r.WakeRadius), NameKey = r.NameKey, CodexId = r.CodexId,
                        Hook = None(r.Hook), SfxWarn = None(r.SfxWarn), SortOrder = r.SortOrder,
                    });
                }
            }
            catch (Exception e)
            {
                Log.Error("[EventDirector] 读取 fg.TbWorldEvent 失败：" + e.Message);
            }
            list.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        private static string None(string v) => string.IsNullOrEmpty(v) || v == "none" ? string.Empty : v;

        // ── 调参（fg.TbHomeTuning event.director.*）──

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;
        public static long CheckTicks => Math.Max(1, GameClock.TicksFor(Math.Max(0.05, Tuning("event.director.check_seconds", 10f))));
        public static double MajorGapDays => Math.Max(0, Tuning("event.director.major_gap_days", 2f));
        public static int HistoryMax => Math.Max(8, (int)Math.Round(Tuning("event.director.history_max", 64f)));
        private static long DayTicks(double days) => GameClock.TicksFor(days * GameClock.DaySeconds);

        /// <summary>设置档位的倍率（关闭 0 / 少 0.5 / 标准 1 / 多 1.5）。</summary>
        public static float FrequencyMul(int level) => level switch
        {
            FrequencyOff => 0f,
            FrequencyLow => Math.Max(0.01f, Tuning("event.director.freq_low", 0.5f)),
            FrequencyHigh => Math.Max(0.01f, Tuning("event.director.freq_high", 1.5f)),
            _ => Math.Max(0.01f, Tuning("event.director.freq_standard", 1f)),
        };

        // ── 状态 ───────────────────────────────────────────────────────────────

        public static DirectorEventState StateOf(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            DirectorEventState d = s.DirectorEvents ??= new DirectorEventState();
            d.Schedules ??= Array.Empty<EventDirectorScheduleRecord>();
            d.Active ??= Array.Empty<EventDirectorInstanceRecord>();
            d.History ??= Array.Empty<EventDirectorHistoryRecord>();
            d.Seen ??= Array.Empty<string>();
            if (d.Frequency < FrequencyOff || d.Frequency > FrequencyHigh)
            {
                d.Frequency = FrequencyStandard;
            }
            if (d.NextSerial < 1)
            {
                d.NextSerial = 1;
            }
            if (!d.Seeded)
            {
                // FGR-EVT-005：与世界生成、掉落、突袭、天气的随机数分开——世界种子 + 盐派生，进存档。
                d.RngSeed = unchecked((int)WorldGenMath.Hash(unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed)), 0, 0, SaltSeed));
                d.Seeded = true;
            }
            return d;
        }

        public static int Frequency(CampaignState s) => StateOf(s)?.Frequency ?? FrequencyStandard;

        public static string FrequencyName(int level) => GameText.Get(level switch
        {
            FrequencyOff => "event_director.freq.off",
            FrequencyLow => "event_director.freq.low",
            FrequencyHigh => "event_director.freq.high",
            _ => "event_director.freq.standard",
        });

        /// <summary>FGR-EVT-006：改“事件频率”（立即生效：随机事件的到期按新倍率重算）。</summary>
        public static bool SetFrequency(CampaignState s, int level)
        {
            DirectorEventState d = StateOf(s);
            if (d == null || level < FrequencyOff || level > FrequencyHigh || d.Frequency == level)
            {
                return false;
            }
            d.Frequency = level;
            Revision++;
            GameEvent.Send(ChangedEvent);
            return true;
        }

        /// <summary>随机事件的总倍率 = 难度 eventRate × 设置档位（0 = 关闭）。</summary>
        public static double Rate(CampaignState s) => DifficultyService.EventRate(s) * FrequencyMul(Frequency(s));

        public static IReadOnlyList<EventDirectorHistoryRecord> History(CampaignState s) => StateOf(s)?.History ?? Array.Empty<EventDirectorHistoryRecord>();

        public static IReadOnlyList<EventDirectorInstanceRecord> Active(CampaignState s) => StateOf(s)?.Active ?? Array.Empty<EventDirectorInstanceRecord>();

        public static bool HasOccurred(CampaignState s, string eventId) => Array.IndexOf(StateOf(s)?.Seen ?? Array.Empty<string>(), eventId) >= 0;

        public static EventDirectorScheduleRecord Schedule(CampaignState s, string eventId)
        {
            foreach (EventDirectorScheduleRecord r in StateOf(s)?.Schedules ?? Array.Empty<EventDirectorScheduleRecord>())
            {
                if (r != null && r.EventId == eventId)
                {
                    return r;
                }
            }
            return null;
        }

        private static EventDirectorScheduleRecord EnsureSchedule(DirectorEventState d, string eventId)
        {
            foreach (EventDirectorScheduleRecord r in d.Schedules)
            {
                if (r != null && r.EventId == eventId)
                {
                    return r;
                }
            }
            var rec = new EventDirectorScheduleRecord { EventId = eventId };
            var list = new List<EventDirectorScheduleRecord>(d.Schedules) { rec };
            d.Schedules = list.ToArray();
            return rec;
        }

        private static double Rand01(DirectorEventState d, uint salt)
        {
            int serial = d.RngSerial++;
            return WorldGenMath.Hash(unchecked((uint)d.RngSeed), serial, 0, salt) / 4294967296.0;
        }

        private static void RollGap(DirectorEventState d, EventDirectorScheduleRecord rec, EventDirectorDef def, long anchor)
        {
            double days = def.IntervalMinDays + (def.IntervalMaxDays - def.IntervalMinDays) * Rand01(d, SaltGap);
            rec.AnchorTick = anchor;
            rec.GapTicks = Math.Max(1, DayTicks(days));
        }

        /// <summary>随机事件此刻的到期步（没有排程 / 频率关闭 = long.MaxValue）。</summary>
        public static long DueTick(CampaignState s, string eventId)
        {
            EventDirectorScheduleRecord rec = Schedule(s, eventId);
            double rate = Rate(s) * Endgame.PostgameService.EventRateMul(s, eventId);
            if (rec == null || rec.AnchorTick < 0 || rate <= 0)
            {
                return long.MaxValue;
            }
            return rec.AnchorTick + (long)Math.Ceiling(rec.GapTicks / rate);
        }

        // ── 规则（FGR-EVT-002）───────────────────────────────────────────────────

        /// <summary>
        /// 现在开始 <paramref name="def"/>（预警 + 时限这段时间）会不会违反互斥规则：返回挡住它的原因文本键，null = 可以。
        /// <paramref name="ignoreGap"/> = 已经在预警中的实例复查（只看静默夜与剧情突袭）。
        /// </summary>
        public static string BlockReason(CampaignState s, EventDirectorDef def, long tick, bool ignoreGap = false)
        {
            DirectorEventState d = StateOf(s);
            if (d == null || def == null)
            {
                return BlockGroup;
            }
            if (def.Major)
            {
                SilentNightState sn = s.Weather?.SilentNight;
                if (sn != null && (sn.Active || (sn.Warned && sn.NextDay > 0 && tick < sn.EndTick)))
                {
                    return BlockSilentNight;
                }
                if (!ignoreGap && sn != null && sn.NextDay > 0 && sn.EndTick > sn.WarnTick)
                {
                    long end = tick + GameClock.TicksFor(def.WarningSeconds + def.DurationSeconds);
                    if (tick < sn.EndTick && end > sn.WarnTick)
                    {
                        return BlockSilentNight; // 整段（预警 + 时限）与静默夜的时间窗（含预警）重叠
                    }
                }
                if (StoryRaidInProgress(s))
                {
                    return BlockStoryRaid;
                }
                if (!ignoreGap && d.LastMajorTick >= 0 && tick < d.LastMajorTick + DayTicks(MajorGapDays))
                {
                    return BlockMajorGap;
                }
            }
            if (ignoreGap)
            {
                return null;
            }
            if (def.HomeOnly && RaidHeadingHome(s))
            {
                return BlockRaid;
            }
            foreach (EventDirectorInstanceRecord r in d.Active)
            {
                if (r == null)
                {
                    continue;
                }
                EventDirectorDef other = Def(r.EventId);
                if (r.EventId == def.Id || (def.Group.Length > 0 && other != null && other.Group == def.Group))
                {
                    return BlockGroup;
                }
            }
            EventDirectorScheduleRecord rec = Schedule(s, def.Id);
            if (rec != null && rec.LastEndTick >= 0 && def.CooldownDays > 0 && tick < rec.LastEndTick + DayTicks(def.CooldownDays))
            {
                return BlockCooldown;
            }
            return null;
        }

        /// <summary>剧情突袭（fg.TbRaidStory / 剧情节点）已排定、预警或在路上 / 攻城中。</summary>
        public static bool StoryRaidInProgress(CampaignState s)
        {
            foreach (RaidPlanRecord p in RaidDirectorService.Plans(s))
            {
                // 按触发方式认剧情突袭（RequestStoryRaid 的 storyId 可以为空；合并进同一波的触发也算）。
                if (p != null && p.State <= RaidDirectorService.StateDeparted
                    && (p.Trigger == RaidCatalog.TriggerStory || (p.Triggers != null && Array.IndexOf(p.Triggers, RaidCatalog.TriggerStory) >= 0)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>有突袭已经预警或出发（正在逼近 / 在家园攻城）。</summary>
        public static bool RaidHeadingHome(CampaignState s)
        {
            foreach (RaidPlanRecord p in RaidDirectorService.Plans(s))
            {
                if (p != null && (p.State == RaidDirectorService.StateWarned || p.State == RaidDirectorService.StateDeparted))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ActOk(CampaignState s, EventDirectorDef def)
        {
            int act = SandboxService.EffectiveAct(s); // FG14-SBX-02：沙盒有效幕
            return act >= def.ActMin && act <= def.ActMax;
        }

        // ── 请求（条件触发 / 剧情节点 / 地图点位）─────────────────────────────────

        /// <summary>
        /// 处理器或剧情请求一次事件（蠕虫：震动值到 100；剧情节点：第一次信号幽灵；诱饵终端：玩家交互）。不立即开始：下一次评估时按互斥规则开始或推迟。
        /// “事件频率：关闭”时只接受剧情请求（<paramref name="story"/>）与地图点位事件（trigger=site），其余被拒并返回 false。
        /// </summary>
        public static bool Request(CampaignState s, string eventId, bool story = false, Vector2? at = null)
        {
            DirectorEventState d = StateOf(s);
            EventDirectorDef def = Def(eventId);
            if (d == null || def == null)
            {
                return false;
            }
            if (d.Frequency == FrequencyOff && !story && def.Trigger != TriggerSite)
            {
                d.Rejected++;
                return false;
            }
            EventDirectorScheduleRecord rec = EnsureSchedule(d, eventId);
            rec.Pending = true;
            rec.PendingStory |= story;
            rec.PendingAt = at.HasValue;
            rec.PendingX = at?.x ?? 0f;
            rec.PendingY = at?.y ?? 0f;
            d.NextCheckTick = Math.Min(d.NextCheckTick < 0 ? long.MaxValue : d.NextCheckTick, GameClock.Ticks);
            Revision++;
            return true;
        }

        /// <summary>FG12-EVT-03：reward=fixed 的处理器在 OnEnd 里给完奖励后记进这一条历史（事件历史面板写“获得……”）。没有这条历史 → false。</summary>
        public static bool SetHistoryReward(CampaignState s, int serial, int tech, string fragmentId)
        {
            EventDirectorHistoryRecord[] hist = StateOf(s)?.History ?? Array.Empty<EventDirectorHistoryRecord>();
            for (int i = hist.Length - 1; i >= 0; i--)
            {
                if (hist[i] != null && hist[i].Serial == serial)
                {
                    hist[i].RewardTech = Math.Max(0, tech);
                    hist[i].RewardFragment = fragmentId ?? string.Empty;
                    Revision++;
                    return true;
                }
            }
            return false;
        }

        /// <summary>处理器提前结束一个事件（击退 / 失败 / 夺回……）。完成（success）且 reward=packet 时给三选一。</summary>
        public static bool Complete(CampaignState s, int serial, string outcome)
        {
            DirectorEventState d = StateOf(s);
            EventDirectorInstanceRecord r = FindActive(d, serial);
            if (r == null)
            {
                return false;
            }
            Finish(s, d, r, string.IsNullOrEmpty(outcome) ? OutcomeSuccess : outcome, GameClock.Ticks);
            return true;
        }

        /// <summary>
        /// FG12-EVT-03 修复轮 1：处理器在开始那一刻发现条件不成立（例如信号幽灵的目标全部失效），把这次撤回成“推迟”——不进历史、
        /// 发生次数退回、请求留着（剧情请求仍是剧情请求，storyFirst 的第一次不被吃掉），条件满足后补上（新的 Serial）。与预警阶段撤回同一条路径。
        /// </summary>
        public static bool Retract(CampaignState s, int serial, string whyKey)
        {
            DirectorEventState d = StateOf(s);
            EventDirectorInstanceRecord r = FindActive(d, serial);
            EventDirectorDef def = r != null ? Def(r.EventId) : null;
            if (def == null)
            {
                return false;
            }
            Postpone(s, d, r, def, whyKey);
            return true;
        }

        private static EventDirectorInstanceRecord FindActive(DirectorEventState d, int serial)
        {
            foreach (EventDirectorInstanceRecord r in d?.Active ?? Array.Empty<EventDirectorInstanceRecord>())
            {
                if (r != null && r.Serial == serial)
                {
                    return r;
                }
            }
            return null;
        }

        // ── 推进 ─────────────────────────────────────────────────────────────────

        /// <summary>世界模拟每步调用：进行中的事件按整数步比较预警结束 / 时限（O(进行中数)）；每 check_seconds 评估一次到期、推迟与互斥。</summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            if (!_installed || s == null)
            {
                return;
            }
            DirectorEventState d = StateOf(s);
            if (d.Active.Length > 0)
            {
                // 倒序：Finish 会从数组里移走。
                for (int i = d.Active.Length - 1; i >= 0; i--)
                {
                    EventDirectorInstanceRecord r = i < d.Active.Length ? d.Active[i] : null;
                    if (r == null)
                    {
                        continue;
                    }
                    if (r.State == StateWarned && tick >= r.StartTick)
                    {
                        Activate(s, d, r, tick);
                    }
                    else if (r.State == StateActive && tick >= r.EndTick)
                    {
                        Finish(s, d, r, OutcomeExpired, tick);
                    }
                }
            }
            if (d.NextCheckTick >= 0 && tick < d.NextCheckTick)
            {
                return;
            }
            d.NextCheckTick = tick + CheckTicks;
            Evaluate(s, d, tick);
        }

        private static void Evaluate(CampaignState s, DirectorEventState d, long tick)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Evaluations++;
            // 1. 预警中的重大威胁：静默夜 / 剧情突袭后来才排上 → 撤回预警，推迟到之后（FGR-EVT-002 负向“蠕虫将要破土时静默夜开始”）。
            for (int i = d.Active.Length - 1; i >= 0; i--)
            {
                EventDirectorInstanceRecord r = i < d.Active.Length ? d.Active[i] : null; // 处理器回调可能改动 Active
                EventDirectorDef def = r != null ? Def(r.EventId) : null;
                if (r == null || def == null || r.State != StateWarned || !def.Major)
                {
                    continue;
                }
                string why = BlockReason(s, def, tick, ignoreGap: true);
                if (why != null)
                {
                    Postpone(s, d, r, def, why);
                }
            }
            // 2. 家园里的正向事件遇上逼近的突袭：提前离开（负向“商队停留期间突袭即将到来”）。
            if (RaidHeadingHome(s))
            {
                for (int i = d.Active.Length - 1; i >= 0; i--)
                {
                    EventDirectorInstanceRecord r = i < d.Active.Length ? d.Active[i] : null; // Finish 的 OnEnd 可能再结束别的实例
                    EventDirectorDef def = r != null ? Def(r.EventId) : null;
                    if (def != null && def.HomeOnly && !def.Threat)
                    {
                        Finish(s, d, r, OutcomeLeftRaid, tick);
                    }
                }
            }
            // 3. 到期 / 请求的事件：请求优先（按表顺序），随机的按权重抽一个。
            double rate = Rate(s);
            CandScratch.Clear();
            int weightSum = 0;
            foreach (EventDirectorDef def in Defs)
            {
                if (!Handlers.TryGetValue(def.Id, out EventDirectorHandler h) || !ActOk(s, def))
                {
                    continue;
                }
                EventDirectorScheduleRecord rec = EnsureSchedule(d, def.Id);
                bool randomOn = def.Trigger == TriggerRandom && !(def.StoryFirst && rec.Occurrences == 0);
                if (randomOn && rec.AnchorTick < 0)
                {
                    RollGap(d, rec, def, tick); // 第一次进入生效的幕：从现在起算（旧档 / 中途开放不追溯）
                }
                // “关闭”档：之前挂起 / 推迟的请求只留剧情与点位的（FGR-EVT-006），其余留着、改回档位后再补。
                bool pendingOn = rec.Pending && (d.Frequency != FrequencyOff || rec.PendingStory || def.Trigger == TriggerSite);
                // FG14-SBX-01（FGR-SBX-004“轨道坠落更频繁”）：后日谈里轨道坠落的随机间隔再 ÷ postgame.orbital_drop_rate（与 DueTick 同一口径）。
                double defRate = rate * Endgame.PostgameService.EventRateMul(s, def.Id);
                bool due = pendingOn || (randomOn && defRate > 0 && tick >= rec.AnchorTick + (long)Math.Ceiling(rec.GapTicks / defRate));
                if (!due)
                {
                    SetBlocked(rec, string.Empty);
                    continue;
                }
                string why = BlockReason(s, def, tick);
                if (why == null && !h.CanStart(s, def, out string reason))
                {
                    why = reason ?? BlockGroup;
                }
                if (why != null)
                {
                    if (SetBlocked(rec, why))
                    {
                        d.Postponed++;
                    }
                    continue;
                }
                SetBlocked(rec, string.Empty);
                if (pendingOn)
                {
                    TryStart(s, d, def, rec, h, tick);
                    continue;
                }
                CandScratch.Add(def);
                weightSum += def.Weight;
            }
            if (CandScratch.Count > 0)
            {
                EventDirectorDef pick = CandScratch[0];
                if (CandScratch.Count > 1)
                {
                    double roll = Rand01(d, SaltPick) * weightSum;
                    foreach (EventDirectorDef c in CandScratch)
                    {
                        roll -= c.Weight;
                        if (roll < 0)
                        {
                            pick = c;
                            break;
                        }
                    }
                }
                // 前面的请求可能刚开始了一个重大威胁：再查一次。
                if (BlockReason(s, pick, tick) == null)
                {
                    TryStart(s, d, pick, EnsureSchedule(d, pick.Id), Handlers[pick.Id], tick);
                }
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            MaxEvaluateMs = Math.Max(MaxEvaluateMs, ms);
        }

        private static bool SetBlocked(EventDirectorScheduleRecord rec, string key)
        {
            if (rec.BlockedKey == key)
            {
                return false;
            }
            rec.BlockedKey = key;
            Revision++;
            return key.Length > 0;
        }

        private static void TryStart(CampaignState s, DirectorEventState d, EventDirectorDef def, EventDirectorScheduleRecord rec, EventDirectorHandler h, long tick)
        {
            Vector2 core = SignalUplinkService.CorePosition(s);
            var r = new EventDirectorInstanceRecord
            {
                Serial = d.NextSerial,
                EventId = def.Id,
                Story = rec.PendingStory,
                WarnTick = tick,
                RegionId = HomeValleyLayout.RegionId,
                X = rec.Pending && rec.PendingAt ? rec.PendingX : core.x,
                Y = rec.Pending && rec.PendingAt ? rec.PendingY : core.y,
            };
            bool wasPending = rec.Pending, wasStory = rec.PendingStory, wasAt = rec.PendingAt;
            rec.Pending = false;
            rec.PendingStory = false;
            rec.PendingAt = false;
            if (!h.Prepare(s, r))
            {
                if (wasPending && (wasStory || def.Trigger == TriggerSite))
                {
                    // 剧情 / 点位请求不能丢（storyFirst 的第一次只能由它触发）：留着，下一次评估再试。
                    rec.Pending = true;
                    rec.PendingStory = wasStory;
                    rec.PendingAt = wasAt;
                }
                else
                {
                    RollGap(d, rec, def, tick); // 随机到期 / 条件请求这次没有合适的地点：放弃，重新抽间隔
                }
                Revision++;
                return;
            }
            d.NextSerial++;
            RollGap(d, rec, def, tick);
            rec.LastStartTick = tick;
            rec.Occurrences++;
            if (def.Major)
            {
                d.LastMajorTick = tick;
            }
            var list = new List<EventDirectorInstanceRecord>(d.Active) { r };
            d.Active = list.ToArray();
            d.Started++;
            StartCount++;
            FirstOccurrence(d, def);
            if (def.WarningSeconds > 0)
            {
                r.State = StateWarned;
                r.StartTick = tick + Math.Max(1, GameClock.TicksFor(def.WarningSeconds));
                WarnCount++;
                Cue(FeedbackCueId.WorldEventWarning, r, def, GameText.Format("event_director.notify.warn", Name(def),
                    CalendarService.ShortDuration((r.StartTick - tick) / (double)Math.Max(1, GameClock.StepHz))));
                h.OnWarn(s, r);
                Revision++;
                GameEvent.Send(ChangedEvent);
                return;
            }
            Activate(s, d, r, tick);
        }

        private static void FirstOccurrence(DirectorEventState d, EventDirectorDef def)
        {
            if (Array.IndexOf(d.Seen, def.Id) >= 0)
            {
                return;
            }
            var seen = new List<string>(d.Seen) { def.Id };
            d.Seen = seen.ToArray();
            // FGR-EVT-004“第一次发生时附一段简短说明”：只发引导钩子（说明内容在 FG15-UX-04）；钩子同时解锁图鉴条目。
            GuidanceHooks.Raise(GuidanceHooks.EventDirectorFirstEvent);
            if (def.Hook.Length > 0)
            {
                GuidanceHooks.Raise(def.Hook);
            }
        }

        private static void Activate(CampaignState s, DirectorEventState d, EventDirectorInstanceRecord r, long tick)
        {
            EventDirectorDef def = Def(r.EventId);
            r.State = StateActive;
            r.StartTick = tick;
            r.EndTick = tick + Math.Max(1, GameClock.TicksFor(def?.DurationSeconds ?? 1));
            WakeNearbyOutposts(s, def, r);
            if (def != null)
            {
                Handlers.TryGetValue(def.Id, out EventDirectorHandler h);
                if (h == null || !h.Instant)
                {
                    string dur = CalendarService.ShortDuration((r.EndTick - tick) / (double)Math.Max(1, GameClock.StepHz));
                    Cue(def.Threat ? FeedbackCueId.WorldEventWarning : FeedbackCueId.WorldEventNotice, r, def,
                        GameText.Format(def.Threat ? "event_director.notify.start" : "event_director.notify.arrive", Name(def), dur));
                }
                h?.OnStart(s, r);
            }
            Revision++;
            GameEvent.Send(ChangedEvent);
        }

        /// <summary>DEBT-FG0ARCH06-02：事件触发唤醒——开始时把 wakeRadius 格内休眠的敌方据点补算到现在、恢复完整模拟（与突袭导演同一入口）。只在事件开始那一步 O(据点数)。</summary>
        private static void WakeNearbyOutposts(CampaignState s, EventDirectorDef def, EventDirectorInstanceRecord r)
        {
            if (def == null || def.WakeRadius <= 0 || s.Raids?.Outposts == null)
            {
                return;
            }
            float r2 = def.WakeRadius * def.WakeRadius;
            foreach (OutpostRecord o in s.Raids.Outposts)
            {
                if (o == null || o.Destroyed || !o.Dormant)
                {
                    continue;
                }
                float dx = o.CellX - r.X;
                float dy = o.CellY - r.Y;
                if (dx * dx + dy * dy <= r2)
                {
                    WorldOutpostSystem.WakeNow(s, o.OutpostId, WorldOutpostSystem.WakeEvent);
                }
            }
        }

        private static void Postpone(CampaignState s, DirectorEventState d, EventDirectorInstanceRecord r, EventDirectorDef def, string why)
        {
            Remove(d, r);
            EventDirectorScheduleRecord rec = EnsureSchedule(d, def.Id);
            rec.Pending = true; // 条件解除后补上（不重新抽间隔）
            rec.PendingStory |= r.Story;
            rec.PendingAt = true;
            rec.PendingX = r.X;
            rec.PendingY = r.Y;
            rec.Occurrences = Math.Max(0, rec.Occurrences - 1);
            rec.BlockedKey = why;
            if (def.Major && d.LastMajorTick == r.WarnTick)
            {
                d.LastMajorTick = -1; // 撤回的预警不占“2 日一次”的名额
            }
            d.Postponed++;
            Cue(FeedbackCueId.WorldEventNotice, r, def, GameText.Format("event_director.notify.postponed", Name(def), BlockText(why)));
            if (Handlers.TryGetValue(def.Id, out EventDirectorHandler h))
            {
                h.OnEnd(s, r, OutcomePostponed); // 生命周期闭合：收回预警效果；补上时换新 Serial
            }
            Revision++;
            GameEvent.Send(ChangedEvent);
        }

        private static void Finish(CampaignState s, DirectorEventState d, EventDirectorInstanceRecord r, string outcome, long tick)
        {
            EventDirectorDef def = Def(r.EventId);
            Remove(d, r);
            if (Handlers.TryGetValue(r.EventId, out EventDirectorHandler fh))
            {
                outcome = fh.FinalOutcome(s, r, outcome) ?? outcome;
            }
            string packetId = string.Empty;
            if (outcome == OutcomeSuccess && def != null && def.Reward == RewardPacket)
            {
                // DEBT-FG8LOOT02-05：事件完成 → 三选一数据包（来源 = 事件），交给附近的己方机器，没有就留在原地。
                LootPackageRecord p = LootPickService.GrantEventPacket(s, def.RewardFaction, new Vector2(r.X, r.Y));
                packetId = p?.PacketId ?? string.Empty;
            }
            var h = new EventDirectorHistoryRecord
            {
                Serial = r.Serial, EventId = r.EventId, Story = r.Story, WarnTick = r.WarnTick, StartTick = r.State == StateActive ? r.StartTick : -1,
                EndTick = tick, Outcome = outcome, PacketId = packetId, RegionId = r.RegionId, X = r.X, Y = r.Y,
            };
            var hist = new List<EventDirectorHistoryRecord>(d.History) { h };
            int max = HistoryMax;
            if (hist.Count > max)
            {
                hist.RemoveRange(0, hist.Count - max);
            }
            d.History = hist.ToArray();
            EventDirectorScheduleRecord rec = EnsureSchedule(d, r.EventId);
            rec.LastEndTick = tick;
            d.Finished++;
            EndCount++;
            if (def != null)
            {
                Handlers.TryGetValue(def.Id, out EventDirectorHandler hd);
                if (hd == null || !hd.Instant || outcome != OutcomeSuccess || packetId.Length > 0)
                {
                    Cue(FeedbackCueId.WorldEventNotice, r, def, GameText.Format("event_director.notify.end", Name(def), OutcomeText(outcome),
                        packetId.Length > 0 ? GameText.Get("event_director.reward.packet") : string.Empty));
                }
                hd?.OnEnd(s, r, outcome);
            }
            Revision++;
            GameEvent.Send(ChangedEvent);
        }

        private static void Remove(DirectorEventState d, EventDirectorInstanceRecord r)
        {
            var list = new List<EventDirectorInstanceRecord>(d.Active);
            list.Remove(r);
            d.Active = list.ToArray();
        }

        /// <summary>通知 + 提示音：反馈时刻自动转成 world_event 通知（进通知历史、离家报告“事件”段），提示音用事件自己的（与突袭 / 静默夜不同）。</summary>
        private static void Cue(FeedbackCueId cue, EventDirectorInstanceRecord r, EventDirectorDef def, string detail)
        {
            LastCueSfx = def.SfxWarn.Length > 0 ? def.SfxWarn : null;
            FeedbackCues.RaiseLocated(cue, new Vector2(r.X, r.Y), detail, LastCueSfx);
        }

        /// <summary>自检读点：最近一次事件提示音。</summary>
        public static string LastCueSfx { get; private set; }

        // ── 文本 ─────────────────────────────────────────────────────────────────

        public static string Name(EventDirectorDef def) => def == null ? string.Empty : GameText.Get(def.NameKey);

        public static string Name(string eventId) => Name(Def(eventId)) is { Length: > 0 } n ? n : eventId;

        public static string OutcomeText(string outcome) => GameText.Get("event_director.outcome." + (string.IsNullOrEmpty(outcome) ? OutcomeCancelled : outcome));

        public static string BlockText(string key) => key == BlockMajorGap
            ? GameText.Format(key, MajorGapDays.ToString("0.#", CultureInfo.InvariantCulture))
            : GameText.Get(key);
    }
}
