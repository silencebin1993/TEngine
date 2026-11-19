using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
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
    /// FG13-END-03 清洗波次（FG11 FGR-END-020“2 个游戏日内来 3 波。无人舰群分批登陆；轨道打击周期性清空一片区域，落点提前 30 秒标出。每一波开始前自动存档”；验收 FGT-END-004）。
    ///
    /// - 开始：揭示状态落定（事件账本 <see cref="RevealService.RevealedEventId"/>）后的第一个世界步（旧档载入后同样从下一步开始）。
    /// - 波次：第 k 波在“开始后 purge.wave_k_days 个游戏日、且上一波打完至少 purge.wave_gap_seconds 游戏秒”时交给突袭导演
    ///   （<see cref="RaidDirectorService.RequestPurgeWave"/>：触发 purge、阵营白潮、等级 purge.wave_k_level、编成里至少一个登陆舱、目标家园、登陆点在核心外）；
    ///   之后照正式突袭流程走——筹备 → 预警（预警时自动存档 = 每波开始前的存档，<see cref="RaidResultService.OnRaidWarned"/>）→ 行进 → 攻城（登陆舱落地投放同一队伍的攻城单位）→ 结算。
    ///   导演那一波的计划结束（全歼 / 撤退）= 这一波打完。撑过第 3 波 = 事件账本 <see cref="ClearedEventId"/>（主线“守住家园”完成）+ <see cref="ClearedEvent"/>。
    /// - 轨道打击：开始后 purge.strike_first_seconds 起每 purge.strike_interval_seconds 排一次（<see cref="CombatSite.ScheduleOrbitalStrike"/>，倒计时 purge.strike_warn_seconds = 30 游戏秒，
    ///   地图与地面自动画落点圈和剩余秒数）；落点按（世界种子, 第几次, 建筑 ID）挑一座离核心够远的家园建筑；撑过全部波次后停止。
    /// - 失败与重试：核心被摧毁后不再写盘（<see cref="CampaignAutoSaveService"/>），失败页按 <see cref="PurgeWaveState.SavedRetryWave"/> 写明“读档后从第几波开始前重试”；
    ///   读回的存档里已撑过的波次不会再来、<see cref="ClearedEventId"/> 只授予一次。
    /// 只看统一时钟步数（暂停不走、倍速同比），与观察无关（家园内核一直在跑）。每步 O(1)；每游戏秒一次 O(计划数)；每次排打击 O(建筑数)。
    /// </summary>
    public static class PurgeWaveService
    {
        public const int WaveCount = 3;
        /// <summary>事件账本：撑过清洗波次（主线任务 main.purge 的完成条件，fgdata_quest.QUEST_EVENT_IDS）。</summary>
        public const string ClearedEventId = "endgame_purge_cleared";
        /// <summary>GameEvent：撑过全部清洗波次那一步发一次（FG13-END-04 登陆场从这里开始）。</summary>
        public const string ClearedEvent = "Endgame.PurgeCleared";
        /// <summary>每一波交给导演时的剧情 ID（记进计划 StoryId）：purge.wave.1～3。</summary>
        public const string WaveIdPrefix = "purge.wave.";
        public const string NotifyType = RevealService.OrbitalNotifyType;

        private const uint SaltStrike = 0x50524753u; // "PRGS"

        /// <summary>状态版本（HUD 刷新键）；自检读的本进程计数。</summary>
        public static int Revision { get; private set; }
        public static int StartCount { get; private set; }
        public static int LaunchCount { get; private set; }
        public static int StrikeScheduleCount { get; private set; }
        public static int GrantCount { get; private set; }
        public static int RetryCount { get; private set; }

        public static void ResetCountersForTests()
        {
            StartCount = 0;
            LaunchCount = 0;
            StrikeScheduleCount = 0;
            GrantCount = 0;
            RetryCount = 0;
        }

        // ── 调参 ─────────────────────────────────────────────────────────

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        // 调参键按波序预先拼好（等待发动期间每个世界步都会查，不在热更层每步分配字符串）。
        private static readonly string[] WaveDaysKeys = { "purge.wave_1_days", "purge.wave_2_days", "purge.wave_3_days" };
        private static readonly string[] WaveLevelKeys = { "purge.wave_1_level", "purge.wave_2_level", "purge.wave_3_level" };
        private static string WaveKey(string[] keys, int wave, string kind) =>
            wave >= 1 && wave <= keys.Length ? keys[wave - 1] : "purge.wave_" + wave.ToString(CultureInfo.InvariantCulture) + kind;

        public static double WaveDays(int wave) => Math.Max(0.0, Tuning(WaveKey(WaveDaysKeys, wave, "_days"), wave == 1 ? 0.05f : wave == 2 ? 0.6f : 1.15f));
        public static int WaveLevel(int wave) => Mathf.Clamp(Mathf.RoundToInt(Tuning(WaveKey(WaveLevelKeys, wave, "_level"), wave + 1)), 1, 4);
        public static float WaveGapSeconds => Mathf.Max(0f, Tuning("purge.wave_gap_seconds", 120f));
        public static float StrikeFirstSeconds => Mathf.Max(1f, Tuning("purge.strike_first_seconds", 90f));
        public static float StrikeIntervalSeconds => Mathf.Max(StrikeWarnSeconds + 1f, Tuning("purge.strike_interval_seconds", 240f));
        public static float StrikeWarnSeconds => Mathf.Max(0.8f, Tuning("purge.strike_warn_seconds", 30f));
        public static float StrikeRadius => Mathf.Max(0.5f, Tuning("purge.strike_radius", 7f));
        public static float StrikeDamage => Mathf.Max(0f, Tuning("purge.strike_damage", 160f));
        public static float StrikeBuilding => Mathf.Clamp01(Tuning("purge.strike_building", 0.4f));
        public static float StrikeCoreClearance => Mathf.Max(StrikeRadius + 1f, Tuning("purge.strike_core_clearance", 14f));

        // ── 读 ───────────────────────────────────────────────────────────

        public static PurgeWaveState StateOf(CampaignState s) => s?.Progress?.Purge;
        public static bool Started(CampaignState s) => (StateOf(s)?.StartTick ?? -1) >= 0;
        public static bool Done(CampaignState s) => s != null && CampaignEventLedger.Contains(s, ClearedEventId);
        /// <summary>清洗进行中（已开始、还没撑过全部波次）。</summary>
        public static bool Active(CampaignState s) => Started(s) && !Done(s);
        public static string WaveId(int wave) => WaveIdPrefix + wave.ToString(CultureInfo.InvariantCulture);

        /// <summary>现在读档会从第几波开始前重试（进行中的那一波，或下一波）；没在清洗中 = 0。</summary>
        public static int RetryWave(CampaignState s)
        {
            PurgeWaveState st = StateOf(s);
            return Active(s) && st != null ? Math.Min(WaveCount, st.Cleared + 1) : 0;
        }

        /// <summary>第 <paramref name="index"/>（0 起）波最早发动的那一步：开始 + 该波日数，且上一波打完至少隔 purge.wave_gap_seconds。</summary>
        public static long EarliestLaunchTick(PurgeWaveState st, int index)
        {
            long t = st.StartTick + RaidDirectorService.DayTicks(WaveDays(index + 1));
            if (st.LastClearTick >= 0)
            {
                t = Math.Max(t, st.LastClearTick + GameClock.TicksFor(WaveGapSeconds));
            }
            return Math.Max(t, st.RetryTick);
        }

        /// <summary>顶栏“轨道威胁”后面接的一段：波次进度与倒计时、下一次轨道打击落下的倒计时。没开始 = 空。</summary>
        public static string HudText(CampaignState s, long tick)
        {
            PurgeWaveState st = StateOf(s);
            if (st == null || st.StartTick < 0)
            {
                return string.Empty;
            }
            if (Done(s))
            {
                return GameText.Get("purge.hud.done");
            }
            string waves;
            if (st.Launched > st.Cleared)
            {
                waves = GameText.Format("purge.hud.active", st.Launched, WaveCount);
            }
            else
            {
                double sec = Math.Max(0, EarliestLaunchTick(st, st.Launched) - tick) / (double)Math.Max(1, GameClock.StepHz);
                waves = GameText.Format("purge.hud.wait", st.Cleared, WaveCount, Env.CalendarService.ShortDuration(sec));
            }
            double strike = NextStrikeLandsIn(s, st, tick);
            // 与通知、地图落点圈同一单位（游戏秒）。
            return strike >= 0 ? waves + " · " + GameText.Format("purge.hud.strike", Math.Ceiling(strike).ToString("0", CultureInfo.InvariantCulture)) : waves;
        }

        /// <summary>下一次轨道打击还有多少游戏秒落下：已排定的那次按内核倒计时，否则 = 到下一次排定 + 预警。没有 = −1。</summary>
        public static double NextStrikeLandsIn(CampaignState s, PurgeWaveState st, long tick)
        {
            CombatSite site = CombatSites.Get(HomeValleyLayout.RegionId);
            if (site != null && st.LastStrikeId > 0)
            {
                for (int i = 0; i < site.FleetStrikeCount; i++)
                {
                    BinGames.Sim.Combat.CombatFleetStrike x = site.FleetStrikeAt(i);
                    if (x.Id == st.LastStrikeId)
                    {
                        return Math.Max(0.0, x.At - site.KernelTime);
                    }
                }
            }
            if (st.NextStrikeTick < 0)
            {
                return -1;
            }
            return Math.Max(0, st.NextStrikeTick - tick) / (double)Math.Max(1, GameClock.StepHz) + StrikeWarnSeconds;
        }

        /// <summary>失败页加的一行：“读档后从第 n/3 波重试”（最近一次写盘若在那一波进行中，读回的是进行中的那一波）。最近一次写盘时不在清洗中 = 空。</summary>
        public static string FailureRetryText(CampaignState s)
        {
            int w = StateOf(s)?.SavedRetryWave ?? 0;
            return w <= 0 ? string.Empty : GameText.Format("failure.purge_retry", w, WaveCount, w - 1);
        }

        /// <summary>主线任务“守住家园”的进度文字（没开始 / 已完成 = null，按目标说明写）。</summary>
        public static string QuestProgressText(CampaignState s)
        {
            PurgeWaveState st = StateOf(s);
            return st == null || !Active(s) ? null : GameText.Format("quest.prog.purge", st.Cleared, WaveCount);
        }

        // ── 写 ───────────────────────────────────────────────────────────

        /// <summary>写盘前（<see cref="CampaignAutoSaveService.SaveWithExport"/> 唯一入口调用）：记下这份存档读回后从第几波开始前重试。</summary>
        public static void BeforeSave(CampaignState s)
        {
            PurgeWaveState st = StateOf(s);
            if (st != null)
            {
                st.SavedRetryWave = RetryWave(s);
            }
        }

        /// <summary>世界模拟的一个固定步（WorldSimulation 在突袭导演之后调用）。</summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            PurgeWaveState st = StateOf(s);
            if (st == null || st.DoneTick >= 0)
            {
                return;
            }
            if (st.StartTick < 0)
            {
                if (!RevealService.Revealed(s))
                {
                    return;
                }
                Start(s, st, tick);
            }
            if (HomeValleySoftlockGuard.IsCoreDestroyed(s))
            {
                return; // 失败页接管；读档后从存档里的那一波接着来
            }
            if (st.Launched > st.Cleared && (tick % Math.Max(1, GameClock.StepHz) == 0 || string.IsNullOrEmpty(st.ActivePlanId)))
            {
                Track(s, st, tick);
            }
            if (st.Cleared >= WaveCount)
            {
                Finish(s, st, tick);
                return;
            }
            if (st.Launched == st.Cleared && st.Launched < WaveCount && tick >= EarliestLaunchTick(st, st.Launched))
            {
                Launch(s, st, tick);
            }
            if (st.NextStrikeTick >= 0 && tick >= st.NextStrikeTick)
            {
                Strike(s, st, tick);
            }
        }

        private static void Start(CampaignState s, PurgeWaveState st, long tick)
        {
            st.StartTick = tick;
            st.NextStrikeTick = tick + GameClock.TicksFor(StrikeFirstSeconds);
            StartCount++;
            Revision++;
            Log.Info("[PurgeWaveService] 清洗波次开始");
            NotificationCenter.Post(NotifyType, GameText.Format("purge.notify.start", WaveCount));
        }

        private static void Launch(CampaignState s, PurgeWaveState st, long tick)
        {
            int wave = st.Launched + 1;
            if (!RaidDirectorService.RequestPurgeWave(s, EnemyDefsFleet, WaveLevel(wave), WaveId(wave)))
            {
                return;
            }
            st.Launched = wave;
            st.LaunchTick = tick;
            st.LaunchTicks[wave - 1] = tick;
            st.ActivePlanId = string.Empty;
            LaunchCount++;
            Revision++;
            NotificationCenter.Post(NotifyType, GameText.Format("purge.notify.wave", wave, WaveCount), new Vector3(HomeValleyLayout.Core.Position.x, 0f, HomeValleyLayout.Core.Position.y));
        }

        private static string EnemyDefsFleet => Content.EnemyDefs.FactionFleet;

        /// <summary>进行中的那一波：认出导演建的计划（StoryId = purge.wave.k）；计划结束（全歼 / 撤退 / 离场）= 打完。</summary>
        private static void Track(CampaignState s, PurgeWaveState st, long tick)
        {
            string waveId = WaveId(st.Launched);
            if (string.IsNullOrEmpty(st.ActivePlanId))
            {
                foreach (RaidPlanRecord p in RaidDirectorService.Plans(s))
                {
                    if (p != null && p.StoryId == waveId && !Cancelled(p))
                    {
                        st.ActivePlanId = p.PlanId;
                        Revision++;
                        return;
                    }
                }
                if (tick - st.LaunchTick > 2L * Math.Max(1, GameClock.StepHz))
                {
                    // 导演没有建出这一波（白潮没有编成等）：不算打完，隔 purge.wave_gap_seconds 重新发动（复审 P2）。
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
                // 计划被取消（派不出队伍等）：这一波没打，不计入打完，重新发动（复审 P1）。
                Retry(st, tick, "突袭计划被取消");
                return;
            }
            if (plan == null || plan.State >= RaidDirectorService.StateEnded)
            {
                Clear(s, st, tick);
            }
        }

        private static bool Cancelled(RaidPlanRecord p) => p.State == RaidDirectorService.StateCancelled || p.EndReason == RaidDirectorService.EndCancelled;

        /// <summary>计划已结束并移进突袭历史（导演每步清理）：按历史里的结束原因认“取消”。</summary>
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

        /// <summary>这一波退回“还没发动”：Cleared 不变，最早 purge.wave_gap_seconds 之后重新交给导演。</summary>
        private static void Retry(PurgeWaveState st, long tick, string why)
        {
            Log.Warning($"[PurgeWaveService] 第 {st.Launched} 波{why}，稍后重新发动");
            st.LaunchTicks[st.Launched - 1] = -1;
            st.Launched = st.Cleared;
            st.ActivePlanId = string.Empty;
            st.RetryTick = tick + GameClock.TicksFor(WaveGapSeconds);
            RetryCount++;
            Revision++;
        }

        private static void Clear(CampaignState s, PurgeWaveState st, long tick)
        {
            int wave = st.Launched;
            st.Cleared = wave;
            st.ClearTicks[wave - 1] = tick;
            st.LastClearTick = tick;
            st.ActivePlanId = string.Empty;
            Revision++;
            NotificationCenter.Post(NotifyType, GameText.Format("purge.notify.wave_cleared", wave, WaveCount));
        }

        private static void Finish(CampaignState s, PurgeWaveState st, long tick)
        {
            st.DoneTick = tick;
            st.NextStrikeTick = -1;
            if (st.LastStrikeId > 0)
            {
                // 撑过第 3 波后停止：还在倒计时的那一次也撤掉（复审 P2；已落下的取消返回 false，无副作用）。
                CombatSites.Get(HomeValleyLayout.RegionId)?.CancelOrbitalStrike(st.LastStrikeId);
            }
            Revision++;
            if (!CampaignEventLedger.TryGrant(s, ClearedEventId, "Endgame", s.PlaySeconds, "purge"))
            {
                return; // 已授予过（读档 / 重复调用）：不再发
            }
            GrantCount++;
            Log.Info("[PurgeWaveService] 撑过全部清洗波次");
            CampaignObjectiveTracker.Recompute(s); // 主线任务“守住家园”完成
            NotificationCenter.Post(NotifyType, GameText.Format("purge.notify.cleared", WaveCount));
            GameEvent.Send(ClearedEvent);
        }

        /// <summary>排一次轨道打击（倒计时 30 游戏秒，地图 / 地面画落点圈）。家园战斗内核还没在（不应发生）就下一游戏秒再试。</summary>
        private static void Strike(CampaignState s, PurgeWaveState st, long tick)
        {
            CombatSite site = CombatSites.Get(HomeValleyLayout.RegionId);
            if (site == null)
            {
                st.NextStrikeTick = tick + Math.Max(1, GameClock.StepHz);
                return;
            }
            PickStrikeTarget(s, st.StrikeCount, out Vector2 at, out string label);
            int id = site.ScheduleOrbitalStrike(at, StrikeRadius, StrikeDamage, StrikeWarnSeconds, StrikeBuilding);
            st.NextStrikeTick = tick + GameClock.TicksFor(StrikeIntervalSeconds);
            if (id <= 0)
            {
                return;
            }
            st.StrikeCount++;
            st.LastStrikeId = id;
            st.LastStrikeX = at.x;
            st.LastStrikeY = at.y;
            StrikeScheduleCount++;
            Revision++;
            NotificationCenter.Post(NotifyType, GameText.Format("purge.notify.strike", label, Mathf.RoundToInt(StrikeWarnSeconds).ToString(CultureInfo.InvariantCulture)),
                new Vector3(at.x, 0f, at.y));
        }

        /// <summary>
        /// 落点：离归还核心中心至少 purge.strike_core_clearance 米的家园建筑（没被摧毁），按（世界种子, 第几次, 建筑 ID）的哈希取最大的那座——与建筑记录的排列顺序无关（存读档一致）；
        /// 一座都没有时落在核心外 clearance 米、按哈希取方向。
        /// </summary>
        public static void PickStrikeTarget(CampaignState s, int serial, out Vector2 at, out string label)
        {
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            Vector2 core = HomeValleyLayout.Core.Position;
            BuildingRecord coreRec = null;
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore)
                {
                    coreRec = b;
                    break;
                }
            }
            if (coreRec != null)
            {
                core = CenterOf(coreRec);
            }
            float clear = StrikeCoreClearance;
            BuildingRecord best = null;
            uint bestScore = 0;
            Vector2 bestAt = default;
            foreach (BuildingRecord b in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                // 只选已开工的建筑：规划虚影（Planned / MaterialReserved）与废墟不当落点（复审 P2）。
                if (b == null || ReferenceEquals(b, coreRec) || b.ConstructionState < BuildingConstructionState.Building
                    || b.ConstructionState == BuildingConstructionState.Destroyed || string.IsNullOrEmpty(b.BuildingId))
                {
                    continue;
                }
                Vector2 c = CenterOf(b);
                if ((c - core).sqrMagnitude < clear * clear)
                {
                    continue;
                }
                uint score = WorldGenMath.Hash(seed, serial, StableHash(b.BuildingId), SaltStrike);
                if (best == null || score > bestScore || (score == bestScore && string.CompareOrdinal(b.BuildingId, best.BuildingId) < 0))
                {
                    best = b;
                    bestScore = score;
                    bestAt = c;
                }
            }
            if (best != null)
            {
                at = bestAt;
                label = Feedback.FeedbackCues.BuildingLabel(best.BuildingId);
                return;
            }
            double ang = WorldGenMath.Hash(seed, serial, 0, SaltStrike) / 4294967296.0 * Math.PI * 2.0;
            at = core + new Vector2((float)Math.Cos(ang), (float)Math.Sin(ang)) * clear;
            label = GameText.Get("purge.strike.open_ground");
        }

        private static Vector2 CenterOf(BuildingRecord b)
        {
            StandingRuleService.BoundsOf(b, out GridCell min, out GridCell max);
            return new Vector2((min.X + max.X + 1) * 0.5f, (min.Y + max.Y + 1) * 0.5f);
        }

        /// <summary>FNV-1a（与进程无关的稳定哈希；string.GetHashCode 每次进程不同）。</summary>
        private static int StableHash(string id)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (char ch in id)
                {
                    h = (h ^ ch) * 16777619u;
                }
                return (int)h;
            }
        }
    }
}
