using System;
using GameLogic.Campaign.WorldSim;
using GameLogic.Notifications;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// FG6-DEF-06（FG06 FGR-DEF-042“突袭到达时自动暂停（设置项，默认对前 3 次突袭开启）”；FG13 FGR-UX-021；FGT-UX-004 自动暂停部分）：
    /// 突袭到达的计数与“突袭到达”通知的自动暂停默认值。
    /// - 每一波第一次有队伍到达时 <see cref="RaidDirectorState.ArrivedWaveCount"/> + 1（同一波分几支先后到达只算一次；存档）。
    /// - 通知类型 raid_arrival 的默认值 = “这支队伍是本波第一支到达的，而且本存档的突袭波数 ≤ raid.auto_pause_first_raids”。玩家在通知设置里改过（开 / 关）就按玩家的（设置跨存档）。
    /// 推进只看模拟步（到达在固定步里发生），与观察 / 倍速无关；暂停由 <see cref="NotificationCenter.AutoPauseHandler"/> 执行（通知发出的那一步）。
    /// </summary>
    public static partial class RaidDirectorService
    {
        public const string ArrivalNotifyType = "raid_arrival";

        /// <summary>最近一次到达的队伍是不是本波第一支（只在到达通知发出的那一刻读；不进存档）。</summary>
        private static bool _lastArrivalNewWave;

        private static bool _arrivalDefaultRegistered;

        /// <summary>默认对前几次突袭自动暂停（fg.TbHomeTuning raid.auto_pause_first_raids，初值 3）。</summary>
        public static int AutoPauseFirstRaids => Math.Max(0, (int)Math.Round(GridTuningOr("raid.auto_pause_first_raids", 3f)));

        /// <summary>本存档已到达的突袭波数（设置界面写“已到达 n 次”）。</summary>
        public static int ArrivedWaves(CampaignState s) => StateOf(s)?.ArrivedWaveCount ?? 0;

        /// <summary>把“突袭到达”的自动暂停默认值接到本存档的突袭次数（幂等；GameRoot 启动与导演补域时都会调用）。</summary>
        public static void EnsureArrivalAutoPauseRegistered()
        {
            if (_arrivalDefaultRegistered)
            {
                return;
            }
            _arrivalDefaultRegistered = true;
            NotificationCenter.RegisterAutoPauseDefault(ArrivalNotifyType, ArrivalAutoPauseDefault);
        }

        /// <summary>
        /// “突袭到达”这一刻的自动暂停默认值：本波第一支到达、且本存档突袭波数 ≤ 上限时为 true。
        /// 没有在到达的那一刻（例如设置界面显示默认值时）= 下一次到达会不会暂停：已到达波数 &lt; 上限。
        /// </summary>
        public static bool ArrivalAutoPauseDefault(CampaignState s)
        {
            RaidDirectorState d = StateOf(s);
            int arrived = d?.ArrivedWaveCount ?? 0;
            if (_inArrival)
            {
                return _lastArrivalNewWave && arrived <= AutoPauseFirstRaids;
            }
            return arrived < AutoPauseFirstRaids;
        }

        private static bool _inArrival;

        /// <summary>
        /// 一支突袭队伍到达（<see cref="WorldTransitSystem"/> 在发“突袭到达”通知之前调用）：本波第一支到达时计数 + 1，并标记这次到达供自动暂停默认值读取。
        /// 返回是不是本波第一支。没有计划编号的队伍（测试 / 调试派出、旧档）按各自一波计。
        /// </summary>
        public static bool OnGroupArrived(CampaignState s, TransitGroupRecord g)
        {
            EnsureState(s);
            RaidDirectorState d = StateOf(s);
            if (d == null || g == null || g.Kind != TransitGroupKind.Raid)
            {
                _lastArrivalNewWave = false;
                return false;
            }
            RaidPlanRecord p = FindPlan(s, g.PlanId);
            int wave = p?.Wave ?? 0;
            if (wave <= 0 && !string.IsNullOrEmpty(g.PlanId))
            {
                // 同一波先到的一支已经打完、计划进了历史：按历史里的波次算（后到的一支仍是同一波，不重复计数）。
                foreach (RaidHistoryRecord h in d.History)
                {
                    if (h != null && h.PlanId == g.PlanId)
                    {
                        wave = h.Wave;
                    }
                }
            }
            bool fresh = wave <= 0 || Array.IndexOf(d.CountedWaves, wave) < 0;
            if (fresh)
            {
                d.ArrivedWaveCount++;
                if (wave > 0)
                {
                    int keep = Math.Min(7, d.CountedWaves.Length);
                    var next = new int[keep + 1];
                    Array.Copy(d.CountedWaves, d.CountedWaves.Length - keep, next, 0, keep);
                    next[keep] = wave;
                    d.CountedWaves = next;
                }
                Revision++;
            }
            _lastArrivalNewWave = fresh;
            return fresh;
        }

        /// <summary>在“突袭到达”通知发出期间把到达标记交给默认值决定者（WorldTransitSystem 用 using 包住 Post）。</summary>
        public static IDisposable ArrivalScope() => new ArrivalMark();

        private sealed class ArrivalMark : IDisposable
        {
            public ArrivalMark()
            {
                _inArrival = true;
            }

            public void Dispose()
            {
                _inArrival = false;
                _lastArrivalNewWave = false;
            }
        }

        private static float GridTuningOr(string id, float fallback) => Grid.GridContent.TryGetTuning(id, out float v) ? v : fallback;
    }
}
