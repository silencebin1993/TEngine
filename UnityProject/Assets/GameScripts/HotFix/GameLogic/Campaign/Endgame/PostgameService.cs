using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Endgame
{
    /// <summary>
    /// FG14-SBX-01 后日谈与周期清洗（FG12 FGR-SBX-001～005；FG16 第 5 节；验收 FGT-SBX-001～003；承接 DEBT-FG13END06-02）。
    ///
    /// - 开始：结算里点“继续”（<see cref="EndingService.ContinuePostgame"/> 把 <see cref="CampaignProgressState.IsPostgame"/> 置真后调 <see cref="OnStarted"/>）；
    ///   是后日谈、但状态还没开始的旧档在下一个世界步补开始。开始那一步起算坚守天数，第一次清洗在 postgame.purge_period_days 个游戏日后。
    /// - 周期清洗（FGR-SBX-002）：每 postgame.purge_period_days 个游戏日交给突袭导演一波（<see cref="RaidDirectorService.RequestPurgeWave"/>：触发 purge、阵营白潮、
    ///   突袭等级 postgame.purge_raid_level、清洗序号 n 从 0 起、预算 = raid.postgame_base × raid.postgame_growth^n，没有上限）；之后照正式突袭流程走
    ///   （筹备 → 预警（自动存档）→ 行进 → 攻城 → 结算、离家报告）。单位数上限与精英化、单位强度在 <see cref="RaidDirectorService.Compose"/>。
    ///   计划被取消 / 没建出来 = 这一次不算来过（最高等级按剩下的历史退回），60 游戏秒后用同一个序号重发（不跳档）；节拍从排定时刻起算，不因上一波打得久、也不因重发而推迟。
    /// - 坚守统计（FGR-SBX-003）：坚守天数、最高清洗等级（来过的）/ 撑过的最高等级、撑过次数、最佳单次防守（击毁最多、并列损失少）、累计击毁 / 损失、清洗历史。
    ///   数据与突袭结算同一份（<see cref="RaidResultService"/> 按计划编号汇总）；存档卡片显示坚守天数与最高等级（<see cref="CampaignSaveService"/>）。
    /// - 坚守结束（FGR-SBX-005）：核心被毁那一步记结束（进行中的那一次记“核心被毁”）；失败页换成坚守总结（<see cref="EndStatsText"/>），之后读取自动存档继续。
    /// - 后日谈内容（FGR-SBX-004）：轨道坠落随机间隔 ÷ postgame.orbital_drop_rate（<see cref="EventRateMul"/>）；坠落残骸按种子以 postgame.orbital_legacy_chance 带来一件遗产
    ///   （<see cref="HeritageService.DropFromOrbit"/>：先是后日谈专属的最后一组）。专属成就由 FG15-SYS-04 读这里的统计（DEBT-FG14SBX01-03）。
    /// 只看统一时钟步数（暂停不走、倍速同比），与观察无关（家园内核一直在跑）。每步 O(1)；每游戏秒一次（以及要发下一次之前）O(建筑数 + 计划数 + 结算记录数)。
    /// </summary>
    public static class PostgameService
    {
        /// <summary>每一次交给导演时的剧情 ID 前缀（记进计划 StoryId）：postgame.purge.n。</summary>
        public const string PurgeIdPrefix = "postgame.purge.";
        public const string NotifyType = RevealService.OrbitalNotifyType;
        public const string OutcomeOpen = "open";
        public const string OutcomeRepelled = "repelled";
        public const string OutcomeCoreLost = "core_lost";
        /// <summary>GameEvent：进入后日谈那一步发一次（说明面板打开）。</summary>
        public const string IntroEvent = "Endgame.PostgameIntro";

        private const uint SaltLegacy = 0x50474C47u; // "PGLG"

        public static int Revision { get; private set; }
        public static int Starts { get; private set; }
        public static int Launches { get; private set; }
        public static int Clears { get; private set; }
        public static int Retries { get; private set; }
        public static int Ends { get; private set; }
        public static int LegacyDrops { get; private set; }

        public static void ResetCountersForTests()
        {
            Starts = Launches = Clears = Retries = Ends = LegacyDrops = 0;
        }

        // ── 调参 ─────────────────────────────────────────────────────────

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        public static double PeriodDays => Math.Max(0.01, Tuning("postgame.purge_period_days", 7f));
        public static int RaidLevel => Mathf.Clamp(Mathf.RoundToInt(Tuning("postgame.purge_raid_level", 4f)), 1, 4);
        public static int HistoryMax => Math.Max(10, Mathf.RoundToInt(Tuning("postgame.history_max", 50f)));
        public static float OrbitalDropRate => Mathf.Max(1f, Tuning("postgame.orbital_drop_rate", 2f));
        public static float OrbitalLegacyChance => Mathf.Clamp01(Tuning("postgame.orbital_legacy_chance", 0.5f));
        /// <summary>计划被取消 / 没建出来时多久后重发（游戏秒）。</summary>
        public static float RetrySeconds => 60f;

        // ── 读 ───────────────────────────────────────────────────────────

        public static PostgameState StateOf(CampaignState s) => s?.Progress?.Postgame;
        public static bool IsPostgame(CampaignState s) => s?.Progress?.IsPostgame == true;
        public static bool Started(CampaignState s) => IsPostgame(s) && (StateOf(s)?.StartTick ?? -1) >= 0;
        public static bool Ended(CampaignState s) => (StateOf(s)?.EndTick ?? -1) >= 0;
        /// <summary>后日谈进行中（已开始、还没坚守结束）。</summary>
        public static bool Active(CampaignState s) => Started(s) && !Ended(s);
        public static string PurgeId(int index) => PurgeIdPrefix + index.ToString(CultureInfo.InvariantCulture);
        public static int LevelOf(int index) => index + 1;

        /// <summary>第 n 次清洗（0 起）的预算（难度规模 1 时）：raid.postgame_base × raid.postgame_growth^n（与导演 ComputeBudget 同一公式）。</summary>
        public static double BudgetOf(int index) => RaidCatalog.PostgameBase * Math.Pow(RaidCatalog.PostgameGrowth, Math.Max(0, index));

        /// <summary>坚守天数：进入后日谈到现在（坚守结束 = 到结束那一步）的整游戏日数。没开始 = 0。</summary>
        public static int HoldDays(CampaignState s, long tick)
        {
            PostgameState st = StateOf(s);
            if (st == null || st.StartTick < 0 || !IsPostgame(s))
            {
                return 0;
            }
            long end = st.EndTick >= 0 ? st.EndTick : tick;
            return (int)(Math.Max(0, end - st.StartTick) / Math.Max(1, RaidDirectorService.DayTicks(1)));
        }

        /// <summary>下一次清洗的等级（进行中 = 那一次的等级）。</summary>
        public static int NextLevel(CampaignState s)
        {
            PostgameState st = StateOf(s);
            return st == null ? 1 : st.ActiveIndex >= 0 ? LevelOf(st.ActiveIndex) : LevelOf(st.Launched);
        }

        /// <summary>下一次真正交给导演的那一步：重发等待中 = 重发时刻，否则 = 排定的节拍。</summary>
        public static long NextLaunchTick(PostgameState st) => st == null ? -1 : st.RetryTick >= 0 ? st.RetryTick : st.NextPurgeTick;

        public static IReadOnlyList<PostgamePurgeRecord> History(CampaignState s) =>
            (IReadOnlyList<PostgamePurgeRecord>)StateOf(s)?.History ?? Array.Empty<PostgamePurgeRecord>();

        /// <summary>事件导演的频率倍率：后日谈里轨道坠落 × postgame.orbital_drop_rate，其余 1。</summary>
        public static double EventRateMul(CampaignState s, string eventId) =>
            eventId == OrbitalDropService.EventId && Active(s) ? OrbitalDropRate : 1.0;

        /// <summary>顶栏“轨道威胁”后面接的一段（后日谈中）：下一次清洗的等级与倒计时 / 进行中。不在后日谈 = 空。</summary>
        public static string HudText(CampaignState s, long tick)
        {
            PostgameState st = StateOf(s);
            if (!Active(s) || st == null)
            {
                return string.Empty;
            }
            if (st.ActiveIndex >= 0)
            {
                return GameText.Format("postgame.hud.active", LevelOf(st.ActiveIndex));
            }
            double left = Math.Max(0, NextLaunchTick(st) - tick) / (double)Math.Max(1, GameClock.StepHz);
            return GameText.Format("postgame.hud.next", LevelOf(st.Launched), CalendarService.ShortDuration(left));
        }

        /// <summary>后日谈开始时的说明（多行，文本里不能有换行，按行拼）。</summary>
        public static string IntroText(CampaignState s, long tick)
        {
            PostgameState st = StateOf(s);
            double left = st != null && st.NextPurgeTick >= 0 ? Math.Max(0, NextLaunchTick(st) - tick) / (double)Math.Max(1, GameClock.StepHz) : PeriodDays * GameClock.DaySeconds;
            return GameText.Get("postgame.intro.l1") + "\n"
                   + GameText.Format("postgame.intro.l2", PeriodDays.ToString("0.#", CultureInfo.InvariantCulture), RaidCatalog.MaxUnits) + "\n"
                   + GameText.Get("postgame.intro.l3") + "\n"
                   + GameText.Get("postgame.intro.l4") + "\n"
                   + GameText.Format("postgame.intro.l5", CalendarService.ShortDuration(left));
        }

        public static string SummaryLine(CampaignState s, long tick)
        {
            PostgameState st = StateOf(s);
            return st == null ? string.Empty : GameText.Format("postgame.panel.summary", HoldDays(s, tick), st.Launched, st.Cleared, st.HighestLevel);
        }

        public static string BestLine(CampaignState s)
        {
            PostgameState st = StateOf(s);
            return st == null || st.BestLevel <= 0 ? GameText.Get("postgame.panel.best_none")
                : GameText.Format("postgame.panel.best", st.BestLevel, st.BestKilled, st.BestElite, st.BestLosses);
        }

        public static string TotalsLine(CampaignState s)
        {
            PostgameState st = StateOf(s);
            return st == null ? string.Empty : GameText.Format("postgame.panel.totals", st.TotalKilled, st.TotalElite, st.TotalLosses);
        }

        /// <summary>状态行：进行中 / 下一次（等级与倒计时）/ 坚守已结束。</summary>
        public static string StatusLine(CampaignState s, long tick)
        {
            PostgameState st = StateOf(s);
            if (st == null)
            {
                return string.Empty;
            }
            if (st.EndTick >= 0)
            {
                return GameText.Get("postgame.panel.ended");
            }
            if (st.ActiveIndex >= 0)
            {
                return GameText.Format("postgame.panel.active", LevelOf(st.ActiveIndex));
            }
            double left = Math.Max(0, NextLaunchTick(st) - tick) / (double)Math.Max(1, GameClock.StepHz);
            return GameText.Format("postgame.panel.next", LevelOf(st.Launched), CalendarService.ShortDuration(left));
        }

        public static string OutcomeText(string outcome) => GameText.Get("postgame.outcome." + (string.IsNullOrEmpty(outcome) ? OutcomeOpen : outcome));

        /// <summary>清洗历史的一行。</summary>
        public static string RowText(PostgamePurgeRecord r)
        {
            if (r == null)
            {
                return string.Empty;
            }
            double sec = Math.Max(0, r.LaunchTick) / (double)Math.Max(1, GameClock.StepHz);
            return GameText.Format("postgame.panel.row", GameClock.DayOf(sec), GameClock.FormatHhMm(sec), r.Level, r.Units, r.Elites,
                r.Strength.ToString("0.00", CultureInfo.InvariantCulture), OutcomeText(r.Outcome), r.Killed, r.Losses);
        }

        /// <summary>“坚守结束”总结页的统计段（失败页在后日谈里换用）：坚守天数、最高等级、撑过次数 + 最佳单次防守 + 累计。</summary>
        public static string EndStatsText(CampaignState s, long tick)
        {
            PostgameState st = StateOf(s);
            if (st == null)
            {
                return string.Empty;
            }
            return GameText.Format("postgame.end.stats", HoldDays(s, tick), st.HighestLevel, st.Cleared) + "\n" + BestLine(s) + "\n" + TotalsLine(s);
        }

        // ── 写 ───────────────────────────────────────────────────────────

        /// <summary>进入后日谈（结算点“继续”那一步，<see cref="EndingService.ContinuePostgame"/> 在写自动存档之前调用）：起算坚守、排第一次清洗、发说明。只第一次生效。</summary>
        public static void OnStarted(CampaignState s)
        {
            PostgameState st = StateOf(s);
            if (st == null || !IsPostgame(s) || st.StartTick >= 0)
            {
                return;
            }
            Begin(s, st, GameClock.Ticks);
            GameEvent.Send(IntroEvent);
        }

        private static void Begin(CampaignState s, PostgameState st, long tick)
        {
            st.StartTick = tick;
            st.NextPurgeTick = tick + RaidDirectorService.DayTicks(PeriodDays);
            Starts++;
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.PostgameFirstStart);
            NotificationCenter.Post(NotifyType, GameText.Format("postgame.notify.start", CalendarService.ShortDuration(PeriodDays * GameClock.DaySeconds)));
            Log.Info($"[PostgameService] 进入后日谈（第 {tick} 步），第一次清洗在第 {st.NextPurgeTick} 步");
        }

        /// <summary>世界模拟的一个固定步（WorldSimulation 在清洗波次 / 滩头 / 旗舰之后调用）。</summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            PostgameState st = StateOf(s);
            if (st == null || !IsPostgame(s) || st.EndTick >= 0)
            {
                return;
            }
            if (st.StartTick < 0)
            {
                Begin(s, st, tick); // 是后日谈、还没起算的旧档：从载入后这一步起算
            }
            bool second = tick % Math.Max(1, GameClock.StepHz) == 0;
            bool due = st.ActiveIndex < 0 && tick >= NextLaunchTick(st);
            // 核心被突袭摧毁由 OnCoreDestroyed 当场结束；这里只兜底（调试入口等）：每游戏秒查一次（查建筑表是 O(建筑数)），要发下一次之前再查一次
            if ((second || due) && HomeValleySoftlockGuard.IsCoreDestroyed(s))
            {
                End(s, st, tick);
                return;
            }
            if (st.ActiveIndex >= 0 && (second || string.IsNullOrEmpty(st.ActivePlanId)))
            {
                Track(s, st, tick);
            }
            if (due)
            {
                Launch(s, st, tick);
            }
        }

        private static void Launch(CampaignState s, PostgameState st, long tick)
        {
            int n = st.Launched;
            if (!RaidDirectorService.RequestPurgeWave(s, Content.EnemyDefs.FactionFleet, RaidLevel, PurgeId(n), n))
            {
                return;
            }
            st.Launched = n + 1;
            st.ActiveIndex = n;
            st.ActivePlanId = string.Empty;
            st.ActiveTick = tick;
            if (st.RetryTick >= 0)
            {
                st.RetryTick = -1; // 重发：节拍在第一次发出时已经排到下一次，不再顺延
            }
            else
            {
                // 节拍按排定时刻走：下一次 = 这一次排定的时刻 + 周期（追上旧档里落后的排程，不连发）。
                st.NextPurgeTick = Math.Max(st.NextPurgeTick + RaidDirectorService.DayTicks(PeriodDays), tick + 1);
            }
            var list = new List<PostgamePurgeRecord>(st.History) { new PostgamePurgeRecord { Index = n, Level = LevelOf(n), LaunchTick = tick } };
            int max = HistoryMax;
            if (list.Count > max)
            {
                list.RemoveRange(0, list.Count - max);
            }
            st.History = list.ToArray();
            Launches++;
            Revision++;
        }

        private static PostgamePurgeRecord ActiveRecord(PostgameState st)
        {
            for (int i = st.History.Length - 1; i >= 0; i--)
            {
                if (st.History[i] != null && st.History[i].Index == st.ActiveIndex && st.History[i].EndTick < 0)
                {
                    return st.History[i];
                }
            }
            return null;
        }

        /// <summary>进行中的那一次：认出导演建的计划（StoryId = postgame.purge.n）；计划结束（全歼 / 撤退）且结算记录都关了 = 撑过。</summary>
        private static void Track(CampaignState s, PostgameState st, long tick)
        {
            string id = PurgeId(st.ActiveIndex);
            PostgamePurgeRecord rec = ActiveRecord(st);
            if (string.IsNullOrEmpty(st.ActivePlanId))
            {
                foreach (RaidPlanRecord p in RaidDirectorService.Plans(s))
                {
                    if (p != null && p.StoryId == id && !Cancelled(p))
                    {
                        st.ActivePlanId = p.PlanId;
                        st.HighestLevel = Math.Max(st.HighestLevel, LevelOf(st.ActiveIndex)); // 计划建出来才算来过（取消重发的那一次见 Retry）
                        if (rec != null)
                        {
                            rec.PlanId = p.PlanId;
                            rec.Budget = p.Budget;
                            rec.Units = p.Occupancy > 0 ? p.Occupancy : p.UnitTotal;
                            int elites = 0;
                            foreach (int e in p.EliteCounts ?? Array.Empty<int>())
                            {
                                elites += e;
                            }
                            rec.Elites = elites;
                            rec.Strength = p.Strength;
                        }
                        NotificationCenter.Post(NotifyType, GameText.Format("postgame.notify.purge", LevelOf(st.ActiveIndex), p.Budget),
                            new Vector3(HomeValleyLayout.Core.Position.x, 0f, HomeValleyLayout.Core.Position.y));
                        Revision++;
                        return;
                    }
                }
                if (tick - st.ActiveTick > 2L * Math.Max(1, GameClock.StepHz))
                {
                    Retry(st, tick, "没有建出突袭计划");
                }
                return;
            }
            RaidPlanRecord plan = RaidDirectorService.FindPlan(s, st.ActivePlanId);
            if (plan != null && plan.State == RaidDirectorService.StateMerged && !string.IsNullOrEmpty(plan.MergedInto))
            {
                st.ActivePlanId = plan.MergedInto; // 并进了另一波：跟着那一波
                return;
            }
            if (plan != null ? Cancelled(plan) : CancelledInHistory(s, st.ActivePlanId))
            {
                Retry(st, tick, "突袭计划被取消");
                return;
            }
            if (plan != null && plan.State < RaidDirectorService.StateEnded)
            {
                return;
            }
            if (!SumResults(s, st.ActivePlanId, out int killed, out int elite, out int losses))
            {
                return; // 结算记录还没关（同一步里先结束计划、后关结算）：下一秒再看
            }
            Clear(st, rec, tick, killed, elite, losses);
        }

        private static bool Cancelled(RaidPlanRecord p) => p.State == RaidDirectorService.StateCancelled || p.EndReason == RaidDirectorService.EndCancelled;

        private static bool CancelledInHistory(CampaignState s, string planId)
        {
            IReadOnlyList<RaidHistoryRecord> hist = RaidDirectorService.History(s);
            for (int i = hist.Count - 1; i >= 0; i--)
            {
                if (hist[i] != null && hist[i].PlanId == planId)
                {
                    return hist[i].EndReason == RaidDirectorService.EndCancelled;
                }
            }
            return false;
        }

        /// <summary>这一波（计划编号）全部结算记录的击毁 / 精英 / 损失之和；有还没关的记录 = false。</summary>
        private static bool SumResults(CampaignState s, string planId, out int killed, out int elite, out int losses)
        {
            killed = elite = losses = 0;
            foreach (RaidResultRecord r in RaidResultService.All(s))
            {
                if (r == null || r.PlanId != planId)
                {
                    continue;
                }
                if (r.EndTick < 0)
                {
                    return false;
                }
                killed += r.Killed;
                elite += r.EliteKilled;
                losses += RaidResultService.LossTotal(r);
            }
            return true;
        }

        private static void Retry(PostgameState st, long tick, string why)
        {
            // 这一次不算来过：撤掉历史里这一行、序号退回（下一次用同一个 n，不跳档），隔 RetrySeconds 重发。
            var list = new List<PostgamePurgeRecord>(st.History);
            list.RemoveAll(r => r != null && r.Index == st.ActiveIndex && r.EndTick < 0);
            st.History = list.ToArray();
            st.HighestLevel = 0;
            foreach (PostgamePurgeRecord r in st.History)
            {
                st.HighestLevel = Math.Max(st.HighestLevel, r?.Level ?? 0); // 历史只裁最早的，等级随序号递增：剩下的最大值 = 真正来过的最高等级
            }
            st.Launched = st.ActiveIndex;
            st.ActiveIndex = -1;
            st.ActivePlanId = string.Empty;
            st.RetryTick = tick + GameClock.TicksFor(RetrySeconds); // NextPurgeTick 保持下一次的排定节拍
            Retries++;
            Revision++;
            Log.Warning($"[PostgameService] 第 {st.Launched + 1} 级清洗{why}，{RetrySeconds:0} 游戏秒后重发");
        }

        private static void Clear(PostgameState st, PostgamePurgeRecord rec, long tick, int killed, int elite, int losses)
        {
            int level = LevelOf(st.ActiveIndex);
            st.Cleared++;
            st.HighestCleared = Math.Max(st.HighestCleared, level);
            st.TotalKilled += killed;
            st.TotalElite += elite;
            st.TotalLosses += losses;
            if (st.BestLevel <= 0 || killed > st.BestKilled || (killed == st.BestKilled && losses < st.BestLosses))
            {
                st.BestLevel = level;
                st.BestKilled = killed;
                st.BestElite = elite;
                st.BestLosses = losses;
            }
            if (rec != null)
            {
                rec.EndTick = tick;
                rec.Outcome = OutcomeRepelled;
                rec.Killed = killed;
                rec.EliteKilled = elite;
                rec.Losses = losses;
            }
            st.ActiveIndex = -1;
            st.ActivePlanId = string.Empty;
            Clears++;
            Revision++;
            NotificationCenter.Post(NotifyType, GameText.Format("postgame.notify.cleared", level, killed, losses));
        }

        /// <summary>核心被突袭摧毁的那一步（<see cref="RaidResultService.OnCoreDestroyed"/> 关完进行中的结算之后调用）：当场结束坚守，
        /// 失败页第一次出现时“累计”里已含打掉核心的那一波（审查 P1：只靠每秒兜底，失败页先弹出、统计少一波且不再刷新）。</summary>
        public static void OnCoreDestroyed(CampaignState s)
        {
            PostgameState st = StateOf(s);
            if (st != null && IsPostgame(s) && st.StartTick >= 0 && st.EndTick < 0)
            {
                End(s, st, GameClock.Ticks);
            }
        }

        /// <summary>核心被毁：坚守结束（进行中的那一次记“核心被毁”，读数按已关的结算记录）。之后不再写盘（失败页 = 坚守总结）。</summary>
        private static void End(CampaignState s, PostgameState st, long tick)
        {
            st.EndTick = tick;
            PostgamePurgeRecord rec = st.ActiveIndex >= 0 ? ActiveRecord(st) : null;
            if (rec != null)
            {
                SumResults(s, st.ActivePlanId, out int killed, out int elite, out int losses);
                rec.EndTick = tick;
                rec.Outcome = OutcomeCoreLost;
                rec.Killed = killed;
                rec.EliteKilled = elite;
                rec.Losses = losses;
                st.TotalKilled += killed;
                st.TotalElite += elite;
                st.TotalLosses += losses;
            }
            Ends++;
            Revision++;
            Log.Info($"[PostgameService] 坚守结束（第 {tick} 步）：坚守 {HoldDays(s, tick)} 天，最高等级 {st.HighestLevel}，撑过 {st.Cleared} 次");
        }

        /// <summary>后日谈里一次轨道坠落撞击后（<see cref="OrbitalDropService"/> OnEnd）：按（种子, 实例序号）以 postgame.orbital_legacy_chance 在残骸处落下一件遗产。</summary>
        public static GameConfig.fg.Heritage OnOrbitalDrop(CampaignState s, Vector2 at, int serial)
        {
            if (!Active(s) || !RollsLegacy(s, serial))
            {
                return null;
            }
            GameConfig.fg.Heritage h = HeritageService.DropFromOrbit(s, at);
            if (h != null)
            {
                LegacyDrops++;
                Revision++;
            }
            return h;
        }

        public static bool RollsLegacy(CampaignState s, int serial) =>
            WorldGenMath.Hash(unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed)), serial, 0, SaltLegacy) / 4294967296.0 < OrbitalLegacyChance;
    }
}
