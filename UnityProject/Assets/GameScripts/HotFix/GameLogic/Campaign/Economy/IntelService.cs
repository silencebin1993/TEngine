using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG5-RND-05（FG05 FGR-RND-050～052；FG13 FGU-25；FGT-RND-008）：监听站与情报。唯一写入口（<see cref="IntelState"/>）。
    ///
    /// - 破译：全部工作中（建成、没禁用、有电）的监听站合成一个破译速度（<see cref="IntelCatalog.StackRateMilli"/>：第 1 座 100%、第 2 座 +50%、第 3 座 +25%，
    ///   表里没有的第 n 座 0）。每 eco.prod.step_ticks 个世界步，按 fg.TbIntelKind 的优先级挑第一类“现在需要”的情报推进；每类各自记进度（插队时保留）。
    ///   一座都没有工作时破译暂停、进度保留（FG05 负向“监听站被摧毁时正在破译的情报”），只告一次“破译中断”。
    /// - “现在需要”：突袭预报 = 有正在逼近家园、还没有有效预报的突袭部队；反制预览 = 没有有效的反制预览；首领弱点 = 有遭遇过、没击败、
    ///   没有有效弱点情报的首领；天气 = 监听站 T2 且天气系统已就绪（FG7-ENV-03 之前不会出现）；舰队片段 = 第 2 幕起且有没截获的片段。
    /// - 有效期：到期那一步在存档里标 <see cref="IntelRecord.Outdated"/>（不删除），突袭预报在部队到达 / 消失时也标已过时；同一类同一目标的新情报替代旧的；
    ///   每类最多保留 intel.keep_per_kind 条已过时的旧情报。
    /// - 突袭预报（FGT-RND-008）：阵营 = 部队出发地；规模 = 人数；方向与抵达点按部队沿地形的实际路线算到“第一次进入到达半径”的那一点
    ///   （<see cref="WorldTransitSystem.PredictArrival"/>，与逐步推进同一套几何）；时间窗口 = 预计抵达 ×(1 ± intel.raid_window_frac)，最小半宽 intel.raid_window_min_seconds。
    /// - 推进只看世界步序号（与观察、帧率、倍速无关；暂停时不走）。每步开销 O(监听站数 + 情报条数 + 行进队伍数)，都有上限；建筑列表只在换了数组时重建索引。
    /// </summary>
    public static class IntelService
    {
        public const string TypeId = IntelCatalog.TypeId;
        public const string SourcePost = "post";
        public const string SourceDataCore = "data_core";
        public const string SubjectExpedition = "expedition";
        public const string ReasonExpired = "expired";
        public const string ReasonArrived = "arrived";
        public const string ReasonGone = "gone";

        /// <summary>情报列表 / 进度 / 监听站变化时 +1（界面、地图据此刷新）。</summary>
        public static int Revision { get; private set; } = 1;
        public static int StepCount { get; private set; }
        public static double LastStepMs { get; private set; }
        public static double MaxStepMs { get; private set; }
        /// <summary>最近一次产出的情报（自检 / 冒烟读）。</summary>
        public static IntelRecord LastProduced { get; private set; }

        private static BuildingRecord[] _postIndexed;
        private static readonly List<BuildingRecord> PostList = new List<BuildingRecord>(4);
        private static bool _builtHookRaised;

        private static int StepTicks => Math.Max(1, GridContent.TuningInt("eco.prod.step_ticks"));

        public static void ResetSessionState()
        {
            _postIndexed = null;
            PostList.Clear();
            _builtHookRaised = false;
            StepCount = 0;
            LastStepMs = 0;
            MaxStepMs = 0;
            LastProduced = null;
            Revision++;
        }

        public static void ResetStepStats() => MaxStepMs = 0;

        // ─────────────────────────────── 存档域 ───────────────────────────────

        public static IntelState StateOf(CampaignState s) => s?.Research?.Intel;

        /// <summary>读档 / 新档时补全情报域（<see cref="CampaignFgStateDomains"/> 调）：数组补成空、序号至少 1、表里已删掉的情报类型的进度去掉（B10）。</summary>
        public static void EnsureState(CampaignState s)
        {
            if (s?.Research == null)
            {
                return;
            }
            IntelState f = s.Research.Intel ??= new IntelState();
            f.Records ??= Array.Empty<IntelRecord>();
            f.Progress ??= Array.Empty<IntelProgressRecord>();
            f.FragmentsHeard ??= Array.Empty<string>();
            f.CurrentKind ??= string.Empty;
            if (f.NextSerial < 1)
            {
                f.NextSerial = 1;
            }
            foreach (IntelRecord r in f.Records)
            {
                if (r != null)
                {
                    r.Regions ??= Array.Empty<string>();
                    r.Adaptations ??= Array.Empty<string>();
                    r.Subject ??= string.Empty;
                    r.OutdatedReason ??= string.Empty;
                }
            }
            if (IntelCatalog.All.Count > 0)
            {
                f.Records = Array.FindAll(f.Records, r => r != null && IntelCatalog.TryGet(r.Kind, out _));
                f.Progress = Array.FindAll(f.Progress, p => p != null && IntelCatalog.TryGet(p.Kind, out _));
            }
        }

        // ─────────────────────────────── 监听站 ───────────────────────────────

        public static bool IsPost(BuildingRecord b) => b != null && b.BuildingTypeId == TypeId;

        /// <summary>家园里的监听站（含缺电 / 禁用 / 被毁；不含搬迁虚影、规划虚影；按建筑 ID 排序 = “第几座”的顺序）。建筑数组换了才重建，平时 O(1)。</summary>
        public static IReadOnlyList<BuildingRecord> PostsOf(CampaignState s)
        {
            BuildingRecord[] records = s?.BuildingRecords;
            if (!ReferenceEquals(records, _postIndexed))
            {
                _postIndexed = records;
                PostList.Clear();
                foreach (BuildingRecord b in records ?? Array.Empty<BuildingRecord>())
                {
                    if (IsPost(b) && b.RegionId == HomeValleyLayout.RegionId && !HomeGridService.IsRelocationGhost(b) && !HomeValleyController.IsPlannedGhost(b))
                    {
                        PostList.Add(b);
                    }
                }
                PostList.Sort((a, c) => string.CompareOrdinal(a.BuildingId, c.BuildingId));
            }
            return PostList;
        }

        /// <summary>正在工作：建成、没禁用、有电（耗电建筑缺电 = 不工作）。</summary>
        public static bool IsWorking(BuildingRecord b) =>
            b != null && b.ConstructionState == BuildingConstructionState.Operational
                      && (b.PowerState == BuildingPowerState.Powered || b.PowerState == BuildingPowerState.NotApplicable && !NeedsPower());

        private static bool NeedsPower() =>
            HomeValleyLayout.PowerProfile.TryGetValue(TypeId, out (float PowerDemand, int PowerPriority) prof) && prof.PowerDemand > 0f;

        public static int WorkingCount(CampaignState s)
        {
            int n = 0;
            foreach (BuildingRecord b in PostsOf(s))
            {
                n += IsWorking(b) ? 1 : 0;
            }
            return n;
        }

        /// <summary>工作中的监听站里最高的等级（没有工作中的 = 0；T1 记 1）。</summary>
        public static int WorkingTier(CampaignState s)
        {
            int t = 0;
            foreach (BuildingRecord b in PostsOf(s))
            {
                if (IsWorking(b))
                {
                    t = Math.Max(t, Math.Max(1, b.Tier));
                }
            }
            return t;
        }

        /// <summary>当前破译速度（千分之一倍率：1000 = 一座监听站）。</summary>
        public static int RateMilli(CampaignState s) => IntelCatalog.StackRateMilli(TypeId, WorkingCount(s));

        /// <summary>“第 1 座 100% + 第 2 座 +50% + …”（B13 数值溯源；第几座起不再提速也写出来）。</summary>
        public static string RateBreakdown(int working)
        {
            var parts = new List<string>(4);
            int rows = IntelCatalog.StackRows(TypeId);
            for (int n = 1; n <= working; n++)
            {
                if (n == 1)
                {
                    parts.Add(GameText.Get("intel.panel.status.stack_first"));
                    continue;
                }
                if (n > rows)
                {
                    parts.Add(GameText.Format("intel.panel.status.stack_zero", n));
                    break;
                }
                parts.Add(GameText.Format("intel.panel.status.stack_part", n, Mathf.RoundToInt(IntelCatalog.StackBonus(TypeId, n) * 100f)));
            }
            return string.Join(" + ", parts);
        }

        public static string RateText(int rateMilli) => (rateMilli / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>电网结算之后（完工 / 启停 / 被毁，<c>HomeValleyPowerGrid</c> 调）：第一次有建成的监听站 → 引导钩子（图鉴条目随之解锁）。O(建筑数)，只在结算时。</summary>
        public static void OnPowerApplied(CampaignState s, bool notify)
        {
            Revision++;
            if (!notify || _builtHookRaised)
            {
                return;
            }
            foreach (BuildingRecord b in PostsOf(s))
            {
                if (b.ConstructionState == BuildingConstructionState.Operational)
                {
                    _builtHookRaised = true;
                    GuidanceHooks.Raise(GuidanceHooks.IntelFirstBuilt);
                    return;
                }
            }
        }

        // ─────────────────────────────── 进度 ───────────────────────────────

        private static IntelProgressRecord ProgressOf(IntelState f, string kind, bool create)
        {
            foreach (IntelProgressRecord p in f.Progress)
            {
                if (p != null && p.Kind == kind)
                {
                    return p;
                }
            }
            if (!create)
            {
                return null;
            }
            var rec = new IntelProgressRecord { Kind = kind };
            var list = new List<IntelProgressRecord>(f.Progress) { rec };
            list.Sort((a, b) => string.CompareOrdinal(a.Kind, b.Kind));
            f.Progress = list.ToArray();
            return rec;
        }

        /// <summary>破译一条这类情报要的进度量（一座监听站一步 = 1000）。</summary>
        public static long NeedWork(IntelKindDef kind) => Math.Max(1L, GameClock.TicksFor(kind.DecipherSeconds)) * 1000L;

        /// <summary>这类情报已破译的百分比（0～99）。</summary>
        public static int ProgressPercent(CampaignState s, string kind)
        {
            IntelState f = StateOf(s);
            IntelProgressRecord p = f == null ? null : ProgressOf(f, kind, false);
            if (p == null || !IntelCatalog.TryGet(kind, out IntelKindDef def))
            {
                return 0;
            }
            return (int)Math.Min(99, p.Work * 100 / NeedWork(def));
        }

        /// <summary>按当前速度这一类还要多少统一时钟步（速度为 0 = -1）。</summary>
        public static long TicksLeft(CampaignState s, string kind)
        {
            int rate = RateMilli(s);
            if (rate <= 0 || !IntelCatalog.TryGet(kind, out IntelKindDef def))
            {
                return -1;
            }
            IntelState f = StateOf(s);
            long done = f == null ? 0 : ProgressOf(f, kind, false)?.Work ?? 0;
            return Math.Max(0, (NeedWork(def) - done + rate - 1) / rate);
        }

        // ─────────────────────────────── 每个世界步 ───────────────────────────────

        /// <summary>世界模拟的一个固定步（WorldSimulation 在研究之后调用）：每 eco.prod.step_ticks 步推进一次。只看步序号。</summary>
        public static void WorldStep(CampaignState state, long ticksBefore, int worldHz)
        {
            if (state == null)
            {
                return;
            }
            int k = StepTicks;
            if (ticksBefore % k != 0)
            {
                return;
            }
            Step(state, k);
        }

        /// <summary>推进 <paramref name="ticks"/> 个世界步：先把到期 / 部队已到的情报标已过时，再按优先级破译（自检直接驱动；生产路径由 <see cref="WorldStep"/> 调）。</summary>
        public static void Step(CampaignState s, int ticks)
        {
            if (s == null || ticks <= 0)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            CampaignFgStateDomains.EnsureAll(s);
            IntelState f = StateOf(s);
            long now = GameClock.Ticks;
            UpdateOutdated(s, f, now);
            IntelKindDef target = PickTarget(s, now);
            string current = target?.Id ?? string.Empty;
            if (f.CurrentKind != current)
            {
                f.CurrentKind = current;
                Revision++;
            }
            if (target != null)
            {
                int rate = RateMilli(s);
                IntelProgressRecord p = ProgressOf(f, target.Id, rate > 0);
                if (rate <= 0)
                {
                    // 一座工作中的监听站都没有：暂停、进度保留；这一类已经破译了一部分时告一次（FG05 负向）。
                    if (p != null && p.Work > 0 && !f.InterruptNotified)
                    {
                        f.InterruptNotified = true;
                        string text = GameText.Format("intel.notify.interrupted", IntelCatalog.KindName(target.Id), ProgressPercent(s, target.Id));
                        NotificationCenter.Post("intel_interrupted", text);
                        GuidanceHooks.Raise(GuidanceHooks.IntelFirstInterrupted);
                        Revision++;
                    }
                }
                else
                {
                    f.InterruptNotified = false;
                    p.Work += (long)ticks * rate;
                    if (p.Work >= NeedWork(target))
                    {
                        p.Work = 0;
                        Produce(s, target, SourcePost, now);
                    }
                }
            }
            StepCount++;
            LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (LastStepMs > MaxStepMs)
            {
                MaxStepMs = LastStepMs;
            }
        }

        /// <summary>有效：没标已过时、还没到期。</summary>
        public static bool IsValid(IntelRecord r, long now) => r != null && !r.Outdated && now < r.ExpiresTick;

        private static void UpdateOutdated(CampaignState s, IntelState f, long now)
        {
            string trim = null;
            foreach (IntelRecord r in f.Records)
            {
                if (r == null || r.Outdated)
                {
                    continue;
                }
                string reason = null;
                if (r.Kind == IntelCatalog.KindRaid)
                {
                    TransitGroupRecord g = WorldTransitSystem.Find(s, r.Subject);
                    reason = g == null ? ReasonGone : g.State != TransitGroupState.Marching ? ReasonArrived : null;
                }
                else if (r.Kind == IntelCatalog.KindBoss && IntelCatalog.TryGetBoss(r.Subject, out IntelBossDef boss) && BossDefeated(s, boss))
                {
                    reason = ReasonGone;
                }
                if (reason == null && now >= r.ExpiresTick)
                {
                    reason = ReasonExpired;
                }
                if (reason == null)
                {
                    continue;
                }
                r.Outdated = true;
                r.OutdatedTick = now;
                r.OutdatedReason = reason;
                GuidanceHooks.Raise(GuidanceHooks.IntelFirstOutdated);
                Revision++;
                trim = trim == null || trim == r.Kind ? r.Kind : "*";
            }
            if (trim != null)
            {
                f.Records = TrimOutdated(f.Records, trim == "*" ? null : trim);
            }
        }

        // ─────────────────────────────── 需要破译什么 ───────────────────────────────

        /// <summary>按优先级第一类“现在需要破译”的情报；都不需要 = null。</summary>
        public static IntelKindDef PickTarget(CampaignState s, long now)
        {
            foreach (IntelKindDef k in IntelCatalog.All)
            {
                if (Needed(s, k, now, out _))
                {
                    return k;
                }
            }
            return null;
        }

        /// <summary>这一类现在需不需要破译；不需要时 <paramref name="why"/> = 面板上那句原因（B06）。</summary>
        public static bool Needed(CampaignState s, IntelKindDef k, long now, out string why)
        {
            why = null;
            if (k == null || s == null)
            {
                return false;
            }
            if (!k.IsReady)
            {
                why = GameText.Get("intel.kind_state.later");
                return false;
            }
            switch (k.ConditionKind)
            {
                case "raid":
                    if (NextRaid(s, now) != null)
                    {
                        return true;
                    }
                    why = HasValid(s, k.Id, null, now, out long left) ? Covered(left) : GameText.Get("intel.kind_state.no_raid");
                    return false;
                case "expedition":
                    if (!HasValid(s, k.Id, SubjectExpedition, now, out long cl))
                    {
                        return true;
                    }
                    why = Covered(cl);
                    return false;
                case "boss":
                    if (NextBoss(s, now) != null)
                    {
                        return true;
                    }
                    why = HasValid(s, k.Id, null, now, out long bl) ? Covered(bl) : GameText.Get("intel.kind_state.no_boss");
                    return false;
                case "tier":
                    // 数据来源就绪时（天气系统，FG7-ENV-03）才会走到这里：要求工作中的监听站等级。
                    if (WorkingTier(s) < k.ConditionArg)
                    {
                        why = GameText.Format("intel.kind_state.need_tier", k.ConditionArg);
                        return false;
                    }
                    why = GameText.Get("intel.kind_state.later");
                    return false;
                case "act":
                    if ((s.Progress?.Act ?? 1) < k.ConditionArg)
                    {
                        why = GameText.Format("intel.kind_state.need_act", k.ConditionArg);
                        return false;
                    }
                    if (NextFragment(s) != null)
                    {
                        return true;
                    }
                    why = GameText.Get("intel.kind_state.fragments_done");
                    return false;
                default:
                    return false;
            }
        }

        private static string Covered(long ticksLeft) => GameText.Format("intel.kind_state.covered", AwayReportService.Duration(ticksLeft));

        /// <summary>这一类（同一目标，<paramref name="subject"/> 为空 = 任意目标）有没有有效的情报；有时给出最长的剩余步数。</summary>
        public static bool HasValid(CampaignState s, string kind, string subject, long now, out long ticksLeft)
        {
            ticksLeft = 0;
            bool any = false;
            foreach (IntelRecord r in StateOf(s)?.Records ?? Array.Empty<IntelRecord>())
            {
                if (r != null && r.Kind == kind && (subject == null || r.Subject == subject) && IsValid(r, now))
                {
                    any = true;
                    ticksLeft = Math.Max(ticksLeft, r.ExpiresTick - now);
                }
            }
            return any;
        }

        /// <summary>正在逼近家园、还没有有效预报的第一支突袭部队（按队伍序号）。</summary>
        public static TransitGroupRecord NextRaid(CampaignState s, long now)
        {
            TransitGroupRecord best = null;
            foreach (TransitGroupRecord g in WorldTransitSystem.Groups(s))
            {
                if (g == null || g.Kind != TransitGroupKind.Raid || g.State != TransitGroupState.Marching || HasValid(s, IntelCatalog.KindRaid, g.GroupId, now, out _))
                {
                    continue;
                }
                if (best == null || g.NavKey < best.NavKey)
                {
                    best = g;
                }
            }
            return best;
        }

        /// <summary>遭遇过、没击败、还没有有效弱点情报的第一个首领。</summary>
        public static IntelBossDef NextBoss(CampaignState s, long now)
        {
            foreach (IntelBossDef b in IntelCatalog.Bosses)
            {
                if (BossEncountered(s, b) && !BossDefeated(s, b) && !HasValid(s, IntelCatalog.KindBoss, b.Id, now, out _))
                {
                    return b;
                }
            }
            return null;
        }

        public static bool BossEncountered(CampaignState s, IntelBossDef b) =>
            b?.Rule == IntelCatalog.BossRuleFoundryCore && FoundryOutpostCoreBoss.IsInitialized(FoundryOutpostRegion.Find(s));

        public static bool BossDefeated(CampaignState s, IntelBossDef b) =>
            b?.Rule == IntelCatalog.BossRuleFoundryCore && FoundryOutpostCoreBoss.GetState(FoundryOutpostRegion.Find(s)) == CoreBossState.Destroyed;

        /// <summary>当前幕能截获、还没截获的第一个舰队片段。</summary>
        public static IntelFragmentDef NextFragment(CampaignState s)
        {
            int act = s?.Progress?.Act ?? 1;
            string[] heard = StateOf(s)?.FragmentsHeard ?? Array.Empty<string>();
            foreach (IntelFragmentDef d in IntelCatalog.Fragments)
            {
                if (d.Act <= act && Array.IndexOf(heard, d.Id) < 0)
                {
                    return d;
                }
            }
            return null;
        }

        // ─────────────────────────────── 产出 ───────────────────────────────

        /// <summary>
        /// FGR-RND-021 情报部分（DEBT-FG5RND02-03）：解析台第一次解读一份数据核心时附带一条情报——按同样的优先级挑现在最需要的一类，
        /// 都不需要时刷新敌方反制预览（数据核心总能给出一条）。不消耗、不推进监听站的破译进度。返回这条情报。
        /// </summary>
        public static IntelRecord GrantFromDataCore(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            CampaignFgStateDomains.EnsureAll(s);
            long now = GameClock.Ticks;
            UpdateOutdated(s, StateOf(s), now);
            IntelKindDef k = PickTarget(s, now);
            if (k == null && !IntelCatalog.TryGet(IntelCatalog.KindCounter, out k))
            {
                return null;
            }
            return Produce(s, k, SourceDataCore, now);
        }

        private static IntelRecord Produce(CampaignState s, IntelKindDef k, string source, long now)
        {
            IntelState f = StateOf(s);
            var r = new IntelRecord { Kind = k.Id, Source = source, ProducedTick = now };
            long validity = GameClock.TicksFor(k.ValiditySeconds);
            switch (k.ConditionKind)
            {
                case "raid":
                    if (!FillRaid(s, r, now))
                    {
                        return null;
                    }
                    break;
                case "expedition":
                    r.Subject = SubjectExpedition;
                    r.Regions = new[] { FracturedCityLayout.RegionId, FoundryOutpostLayout.RegionId };
                    r.Adaptations = new[] { Content.AdaptationCatalog.None, EnemyAdaptationService.ComputeAdaptation(s) };
                    r.ExpiresTick = now + Math.Max(1, validity);
                    break;
                case "boss":
                    IntelBossDef boss = NextBoss(s, now);
                    if (boss == null)
                    {
                        return null;
                    }
                    r.Subject = boss.Id;
                    r.ExpiresTick = now + Math.Max(1, validity);
                    break;
                case "act":
                    IntelFragmentDef frag = NextFragment(s);
                    if (frag == null)
                    {
                        return null;
                    }
                    r.Subject = frag.Id;
                    r.ExpiresTick = now + Math.Max(1, validity);
                    var heard = new List<string>(f.FragmentsHeard) { frag.Id };
                    f.FragmentsHeard = heard.ToArray();
                    break;
                default:
                    return null; // 天气（FG7-ENV-03 之前没有数据来源）
            }
            r.Serial = f.NextSerial++;
            Replace(f, r);
            IntelProgressRecord p = ProgressOf(f, k.Id, true);
            p.Produced++;
            f.Produced++;
            LastProduced = r;
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.IntelFirstIntel);
            string summary = Summary(s, r, now);
            if (r.Kind == IntelCatalog.KindRaid)
            {
                GuidanceHooks.Raise(GuidanceHooks.IntelFirstRaidForecast);
                NotificationCenter.Post("intel_raid", summary, new Vector3(r.ArriveX, 0f, r.ArriveY));
            }
            else
            {
                NotificationCenter.Post("intel_new", GameText.Format("intel.notify.new", k.Name, summary));
            }
            return r;
        }

        private static bool FillRaid(CampaignState s, IntelRecord r, long now)
        {
            TransitGroupRecord g = NextRaid(s, now);
            if (g == null)
            {
                return false;
            }
            GridCell core = HomeGridService.CorePivot(s);
            WorldTransitSystem.PredictArrival(g, out double ax, out double ay);
            double dx = ax - core.X;
            double dy = ay - core.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6)
            {
                dx = g.PosX - core.X;
                dy = g.PosY - core.Y;
                len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
            }
            double eta = WorldTransitSystem.EtaSeconds(g);
            double half = Math.Max(eta * IntelCatalog.RaidWindowFrac, IntelCatalog.RaidWindowMinSeconds);
            r.Subject = g.GroupId;
            r.Faction = g.OriginId ?? string.Empty;
            r.Units = g.UnitCount;
            r.DirX = (float)(dx / len);
            r.DirY = (float)(dy / len);
            r.ArriveX = (float)ax;
            r.ArriveY = (float)ay;
            r.WindowFromTick = now + GameClock.TicksFor(Math.Max(0, eta - half));
            r.WindowToTick = now + GameClock.TicksFor(eta + half);
            r.ExpiresTick = Math.Max(now + 1, r.WindowToTick);
            return true;
        }

        /// <summary>同一类同一目标的旧情报被新的替代；每类已过时的旧情报超出上限时去掉最旧的（有效的不动）。</summary>
        private static void Replace(IntelState f, IntelRecord fresh)
        {
            var list = new List<IntelRecord>(f.Records.Length + 1);
            foreach (IntelRecord r in f.Records)
            {
                if (r != null && !(r.Kind == fresh.Kind && r.Subject == fresh.Subject))
                {
                    list.Add(r);
                }
            }
            list.Add(fresh);
            f.Records = TrimOutdated(list.ToArray(), fresh.Kind);
        }

        /// <summary>每类（<paramref name="kind"/> 为空 = 全部类）已过时的旧情报只留最新的 intel.keep_per_kind 条；有效的不动。</summary>
        private static IntelRecord[] TrimOutdated(IntelRecord[] records, string kind)
        {
            int keep = IntelCatalog.KeepPerKind;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var list = new List<IntelRecord>(records.Length);
            for (int i = records.Length - 1; i >= 0; i--)
            {
                IntelRecord r = records[i];
                if (r == null)
                {
                    continue;
                }
                if (r.Outdated && (kind == null || r.Kind == kind))
                {
                    counts.TryGetValue(r.Kind, out int n);
                    counts[r.Kind] = ++n;
                    if (n > keep)
                    {
                        continue;
                    }
                }
                list.Add(r);
            }
            list.Reverse();
            return list.Count == records.Length ? records : list.ToArray();
        }

        // ─────────────────────────────── 文字 ───────────────────────────────

        private static readonly string[] DirKeys = { "intel.dir.e", "intel.dir.ne", "intel.dir.n", "intel.dir.nw", "intel.dir.w", "intel.dir.sw", "intel.dir.s", "intel.dir.se" };

        /// <summary>8 方位（格网 +Y 为北）：0 东、1 东北、2 北……7 东南。</summary>
        public static int Octant(double dx, double dy)
        {
            double a = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            int o = (int)Math.Round(a / 45.0);
            return ((o % 8) + 8) % 8;
        }

        public static string DirectionName(float dx, float dy) => GameText.Get(DirKeys[Octant(dx, dy)]);

        /// <summary>一条情报的内容（一行；面板、通知、离家报告共用）。</summary>
        public static string Summary(CampaignState s, IntelRecord r, long now)
        {
            if (r == null)
            {
                return string.Empty;
            }
            switch (r.Kind)
            {
                case IntelCatalog.KindRaid:
                {
                    string origin = WorldTransitSystem.OriginName(r.Faction);
                    string dir = DirectionName(r.DirX, r.DirY);
                    if (!IsValid(r, now))
                    {
                        return GameText.Format("intel.raid.line_past", origin, r.Units, dir);
                    }
                    return GameText.Format("intel.raid.line", origin, r.Units, dir,
                        AwayReportService.Duration(Math.Max(0, r.WindowFromTick - now)), AwayReportService.Duration(Math.Max(0, r.WindowToTick - now)));
                }
                case IntelCatalog.KindCounter:
                {
                    var parts = new List<string>(r.Regions.Length);
                    for (int i = 0; i < r.Regions.Length; i++)
                    {
                        string site = Combat.CombatSites.SiteName(r.Regions[i]);
                        string id = i < r.Adaptations.Length ? r.Adaptations[i] : Content.AdaptationCatalog.None;
                        if (string.IsNullOrEmpty(id) || id == Content.AdaptationCatalog.None)
                        {
                            parts.Add(GameText.Format("intel.counter.region_none", site));
                            continue;
                        }
                        Content.AdaptationCatalog.AdaptationInfo info = Content.AdaptationCatalog.Describe(id);
                        parts.Add(GameText.Format("intel.counter.region", site, info.DisplayName, info.CounterHintText));
                    }
                    return string.Join("；", parts);
                }
                case IntelCatalog.KindBoss:
                {
                    if (!IntelCatalog.TryGetBoss(r.Subject, out IntelBossDef b))
                    {
                        return r.Subject;
                    }
                    return GameText.Format("intel.boss.line", b.Name, BossPhasesText(b), BossWeaknessText(b), GameText.Get(b.AdviceKey));
                }
                case IntelCatalog.KindFleet:
                    return IntelCatalog.TryGetFragment(r.Subject, out IntelFragmentDef d) ? GameText.Format("intel.fragment.line", GameText.Get(d.TextKey)) : r.Subject;
                default:
                    return r.Subject;
            }
        }

        /// <summary>首领阶段机制：数值来自首领本身的常量（单一真相）。</summary>
        public static string BossPhasesText(IntelBossDef b) =>
            GameText.Format(b.PhasesKey, Mathf.RoundToInt(FoundryOutpostLayout.Phase1TransitionHealthFraction * 100f),
                Mathf.RoundToInt(FoundryOutpostLayout.TransitionDurationSeconds), Mathf.RoundToInt(FoundryOutpostLayout.CoreLockoutWarnSeconds));

        public static string BossWeaknessText(IntelBossDef b) =>
            GameText.Format(b.WeaknessKey, Mathf.RoundToInt(FoundryOutpostLayout.Phase2BackHitBonusPct * 100f),
                Mathf.RoundToInt(FoundryOutpostLayout.MainCoreFrontalHalfAngleDeg * 2f));

        /// <summary>列表一行：[标记] 类名 · 有效（剩多久）/ 已过时（原因） · 来源，换行接内容。</summary>
        public static string RowText(CampaignState s, IntelRecord r, long now)
        {
            IntelCatalog.TryGet(r.Kind, out IntelKindDef k);
            string glyph = k?.Glyph ?? "?";
            string name = k?.Name ?? r.Kind;
            string source = GameText.Get(r.Source == SourceDataCore ? "intel.source.data_core" : "intel.source.post");
            string head = IsValid(r, now)
                ? GameText.Format("intel.panel.row_valid", glyph, name, AwayReportService.Duration(r.ExpiresTick - now), source)
                : GameText.Format("intel.panel.row_outdated", glyph, name, GameText.Get("intel.outdated." + (string.IsNullOrEmpty(r.OutdatedReason) ? ReasonExpired : r.OutdatedReason)), source);
            return head + "\n" + Summary(s, r, now);
        }

        /// <summary>某一类此刻的状态（面板“破译”段的一行）：在破译 / 等待 / 有效还剩多久 / 为什么不破译。</summary>
        public static string KindStateText(CampaignState s, IntelKindDef k, long now)
        {
            string state;
            if (Needed(s, k, now, out string why))
            {
                int pct = ProgressPercent(s, k.Id);
                state = pct > 0 ? GameText.Format("intel.kind_state.progress", pct) : GameText.Get("intel.kind_state.ready");
            }
            else
            {
                state = why ?? string.Empty;
            }
            return GameText.Format("intel.panel.kind_line", k.Glyph, k.Name, state);
        }

        /// <summary>面板顶部的状态句（几座在工作、速度构成、正在破译什么 / 暂停 / 空闲）。</summary>
        public static string StatusText(CampaignState s, long now)
        {
            int working = WorkingCount(s);
            IntelKindDef target = PickTarget(s, now);
            if (working <= 0)
            {
                if (target != null && ProgressPercent(s, target.Id) > 0)
                {
                    return GameText.Format("intel.panel.status.paused", target.Name, ProgressPercent(s, target.Id)) + "\n" + GameText.Get("intel.panel.status.no_post");
                }
                return GameText.Get("intel.panel.status.no_post");
            }
            int rate = RateMilli(s);
            string head = GameText.Format("intel.panel.status.posts", working, RateText(rate), RateBreakdown(working));
            if (target == null)
            {
                return head + "\n" + GameText.Get("intel.panel.status.idle");
            }
            return head + "\n" + GameText.Format("intel.panel.status.target", target.Name, ProgressPercent(s, target.Id), AwayReportService.Duration(TicksLeft(s, target.Id)));
        }

        // ─────────────────────────────── 建筑状态（B05）───────────────────────────────

        /// <summary>建筑面板 / 悬停 / “为什么不工作”：破译中（哪一类、进度、几座、速度）/ 空闲（现有情报都有效）。缺电 / 禁用 / 被毁由通用状态先报。</summary>
        public static BuildingStatus StatusOf(CampaignState s, BuildingRecord b)
        {
            long now = GameClock.Ticks;
            IntelKindDef target = PickTarget(s, now);
            if (target == null)
            {
                return new BuildingStatus(BuildingStatusKind.Idle, "intel.idle", GameText.Get("bs.reason.intel_idle"));
            }
            int working = WorkingCount(s);
            return new BuildingStatus(BuildingStatusKind.Working, "intel.working", GameText.Format("bs.reason.intel_working", target.Name,
                ProgressPercent(s, target.Id), working, RateText(RateMilli(s))));
        }

        // ─────────────────────────────── 面板 / 地图读点 ───────────────────────────────

        /// <summary>面板列表：<paramref name="kind"/> 为空 = 全部；有效的在前（快到期的先），已过时的在后（新的先）。</summary>
        public static List<IntelRecord> List(CampaignState s, string kind, long now)
        {
            var list = new List<IntelRecord>();
            foreach (IntelRecord r in StateOf(s)?.Records ?? Array.Empty<IntelRecord>())
            {
                if (r != null && (string.IsNullOrEmpty(kind) || r.Kind == kind))
                {
                    list.Add(r);
                }
            }
            list.Sort((a, b) =>
            {
                bool va = IsValid(a, now), vb = IsValid(b, now);
                if (va != vb)
                {
                    return va ? -1 : 1;
                }
                return va ? a.ExpiresTick.CompareTo(b.ExpiresTick) : b.Serial.CompareTo(a.Serial);
            });
            return list;
        }

        public static int ValidCount(CampaignState s, long now)
        {
            int n = 0;
            foreach (IntelRecord r in StateOf(s)?.Records ?? Array.Empty<IntelRecord>())
            {
                n += IsValid(r, now) ? 1 : 0;
            }
            return n;
        }

        public static bool IsNew(CampaignState s, IntelRecord r) => r != null && r.Serial > (StateOf(s)?.SeenSerial ?? 0);

        public static void MarkSeen(CampaignState s)
        {
            IntelState f = StateOf(s);
            if (f == null)
            {
                return;
            }
            int max = f.SeenSerial;
            foreach (IntelRecord r in f.Records)
            {
                if (r != null)
                {
                    max = Math.Max(max, r.Serial);
                }
            }
            if (max != f.SeenSerial)
            {
                f.SeenSerial = max;
                Revision++;
            }
        }

        public static IntelRecord Find(CampaignState s, int serial)
        {
            foreach (IntelRecord r in StateOf(s)?.Records ?? Array.Empty<IntelRecord>())
            {
                if (r != null && r.Serial == serial)
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>地图箭头（FGR-RND-051“突袭预报会在地图上标出来袭方向”）：有效的突袭预报，箭尾在来袭方向上离核心 intel.map_arrow_cells 格处，箭头指向核心。</summary>
        public static void CollectArrows(CampaignState s, List<IntelRecord> into)
        {
            into.Clear();
            long now = GameClock.Ticks;
            foreach (IntelRecord r in StateOf(s)?.Records ?? Array.Empty<IntelRecord>())
            {
                if (r != null && r.Kind == IntelCatalog.KindRaid && IsValid(r, now))
                {
                    into.Add(r);
                }
            }
        }

        /// <summary>箭尾（格）。</summary>
        public static Vector2 ArrowTail(CampaignState s, IntelRecord r)
        {
            GridCell core = HomeGridService.CorePivot(s);
            float len = IntelCatalog.MapArrowCells;
            return new Vector2(core.X + r.DirX * len, core.Y + r.DirY * len);
        }

        /// <summary>箭头尖（格）：停在核心外一小段，不盖住核心图标。</summary>
        public static Vector2 ArrowHead(CampaignState s, IntelRecord r)
        {
            GridCell core = HomeGridService.CorePivot(s);
            float len = IntelCatalog.MapArrowCells * 0.3f;
            return new Vector2(core.X + r.DirX * len, core.Y + r.DirY * len);
        }

        /// <summary>存读档 / 自检对照用的整份快照（逐字段）。</summary>
        public static string Snapshot(CampaignState s)
        {
            IntelState f = StateOf(s);
            if (f == null)
            {
                return "none";
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("serial=").Append(f.NextSerial).Append(" cur=").Append(f.CurrentKind).Append(" int=").Append(f.InterruptNotified)
                .Append(" seen=").Append(f.SeenSerial).Append(" prod=").Append(f.Produced).Append(" heard=").Append(string.Join(",", f.FragmentsHeard)).Append(" prog=");
            foreach (IntelProgressRecord p in f.Progress)
            {
                sb.Append(p.Kind).Append(':').Append(p.Work).Append(':').Append(p.Produced).Append(';');
            }
            sb.Append(" rec=");
            foreach (IntelRecord r in f.Records)
            {
                sb.Append(r.Serial).Append('|').Append(r.Kind).Append('|').Append(r.Subject).Append('|').Append(r.Source).Append('|').Append(r.ProducedTick).Append('|')
                    .Append(r.ExpiresTick).Append('|').Append(r.Outdated).Append('|').Append(r.OutdatedTick).Append('|').Append(r.OutdatedReason).Append('|')
                    .Append(r.Faction).Append('|').Append(r.Units).Append('|').Append(r.DirX.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.DirY.ToString("F4", CultureInfo.InvariantCulture)).Append('|').Append(r.WindowFromTick).Append('-').Append(r.WindowToTick).Append('|')
                    .Append(string.Join(",", r.Regions)).Append('|').Append(string.Join(",", r.Adaptations)).Append(';');
            }
            return sb.ToString();
        }
    }
}
