using System.Collections.Generic;
using BinGames.Sim.Combat;
using BinGames.Sim.WorldGen;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Env
{
    /// <summary>
    /// FG12-EVT-03 信号幽灵（FG10 FGR-EVT-020～023，FGT-EVT-004；FG19 FGR-EMG-165；承接 DEBT-FG12EVT01-02 的幽灵部分、DEBT-EMG4HIJ02-01、DEBT-EMG4FW01-01 的幽灵用例）。
    /// 只是事件导演的 signal_ghost 处理器 + 一个来源：夺控 / 瘫痪 / 反向接入 / 到点归还全部沿用 EMG4-HIJ-02 的共用状态机
    /// （<see cref="SignalSeizeService"/> → 内核夺控表，事件 evt.seize / evt.release，存档在内核快照），这里不另造状态。
    /// - 触发：第二幕起（剧情节点把战役推进到第 2 幕之后）第一次由剧情请求（story = true，不受“事件频率：关闭”影响；<see cref="WorldStep"/> O(1)）；
    ///   之后由导演按表 3～6 游戏日随机安排（storyFirst 的随机门控在导演里）。
    /// - 过程：导演的 5 秒预警（屏幕信号杂波由 ReactionPopupHud 读 <see cref="StaticActive"/> 画）→ 开始时夺控一台己方机器 45 秒
    ///   （不选玩家正在接入 / 接入过渡中的、不选维修中的；远征队里的照常可选）。被夺控的机器用自己的装配按教义打原来的同伴（内核）。
    /// - 应对：反向接入（SignalUplinkService.Request 正式入口：覆盖判定 → 3 秒后夺回）；击倒 → 瘫痪不阵亡，事件结束时恢复；或等 45 秒。
    /// - 奖励（reward = fixed）：夺回或击倒（时限到时仍瘫痪）= 已应对 → OnEnd 给技术数据 + 下一段舰队加密片段（只给一次：导演每个实例只结束一次）。
    /// 选目标在事件开始那一刻 O(机器数)（与 WakeNearbyOutposts 同级的一次性开销，不每帧）。
    /// </summary>
    public static class SignalGhostService
    {
        public const string EventId = "signal_ghost";
        private const uint SaltPick = 0x53474831u; // "SGH1"

        private static readonly List<int> Cand = new List<int>(16);

        // ── 自检读数（会话内） ──
        public static int StoryRequests { get; private set; }
        public static int Seizes { get; private set; }
        public static int Reclaims { get; private set; }
        public static int Rewards { get; private set; }
        public static int NoTarget { get; private set; }

        /// <summary>自检：候选里有这台时强制选它（0 = 按种子抽）。</summary>
        public static int ForcedTargetForTests;

        public sealed class Handler : EventDirectorHandler
        {
            public override string EventId => SignalGhostService.EventId;
            public override bool Prepare(CampaignState s, EventDirectorInstanceRecord r) => SignalGhostService.Prepare(s, r);
            public override void OnStart(CampaignState s, EventDirectorInstanceRecord r) => SignalGhostService.OnStart(s, r);
            public override void OnEnd(CampaignState s, EventDirectorInstanceRecord r, string outcome) => SignalGhostService.OnEnd(s, r, outcome);
            public override string FinalOutcome(CampaignState s, EventDirectorInstanceRecord r, string outcome) => SignalGhostService.FinalOutcome(r, outcome);
        }

        public static void ResetSessionState()
        {
            StoryRequests = Seizes = Reclaims = Rewards = NoTarget = 0;
            ForcedTargetForTests = 0;
        }

        /// <summary>
        /// 世界步（O(1)，只看存档）：战役到了第 2 幕、信号幽灵还没发生过、也没有挂着的请求 / 进行中的实例 → 发剧情请求（FGR-EVT-020 第一次固定）。
        /// 第二幕剧情节点（NarrativeService 的 setAct = 2）一推进幕，下一步就请求；旧档已在第二幕却没来过的同样补上。
        /// </summary>
        public static void WorldStep(CampaignState s)
        {
            if (!EventDirectorService.Installed || s?.Progress == null || SandboxService.EffectiveAct(s) < 2 || !EventDirectorService.HasHandler(EventId))
            {
                return;
            }
            EventDirectorScheduleRecord rec = EventDirectorService.Schedule(s, EventId);
            if (rec != null && (rec.Occurrences > 0 || rec.Pending) || Active(s) != null)
            {
                return;
            }
            if (EventDirectorService.Request(s, EventId, story: true))
            {
                StoryRequests++;
            }
        }

        /// <summary>进行中（预警或夺控中）的信号幽灵实例；没有 = null。O(进行中事件数)。</summary>
        public static EventDirectorInstanceRecord Active(CampaignState s)
        {
            foreach (EventDirectorInstanceRecord r in EventDirectorService.Active(s))
            {
                if (r != null && r.EventId == EventId)
                {
                    return r;
                }
            }
            return null;
        }

        /// <summary>屏幕信号杂波（FGR-EVT-021 的 5 秒预警）：有信号幽灵在预警中。</summary>
        public static bool StaticActive(CampaignState s) => Active(s) is { State: EventDirectorService.StateWarned };

        // ── 处理器 ──────────────────────────────────────────────────────────

        private static bool Prepare(CampaignState s, EventDirectorInstanceRecord r)
        {
            Candidates(s, Cand);
            if (Cand.Count == 0)
            {
                NoTarget++;
                return false; // 剧情请求留着、下次评估再试；随机的重新抽间隔
            }
            r.Target = Pick(s, r.Serial, 0);
            Place(r);
            return true;
        }

        private static void OnStart(CampaignState s, EventDirectorInstanceRecord r)
        {
            // 预警这 5 秒里目标可能被接入 / 送修 / 阵亡：开始那一刻重新核对，不行就换一台。
            EmgSeizeResult res = Valid(s, r.Target) ? SignalSeizeService.Seize(s, r.Target, SignalSeizeService.SourceGhost, SignalSeizeService.SeizeSeconds) : EmgSeizeResult.NoUnit;
            for (int attempt = 1; res != EmgSeizeResult.Ok && attempt <= 4; attempt++)
            {
                Candidates(s, Cand);
                if (Cand.Count == 0)
                {
                    break;
                }
                r.Target = Pick(s, r.Serial, attempt);
                res = SignalSeizeService.Seize(s, r.Target, SignalSeizeService.SourceGhost, SignalSeizeService.SeizeSeconds);
            }
            if (res != EmgSeizeResult.Ok)
            {
                NoTarget++;
                r.Target = 0;
                if (r.Story)
                {
                    // 剧情的第一次（FGR-EVT-020）不能被这次作罢吃掉：撤回成推迟，剧情请求留着、发生次数退回，有可选的机器后照常补上。
                    EventDirectorService.Retract(s, r.Serial, "signal_ghost.block.no_target");
                    return;
                }
                FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventNotice, new Vector2(r.X, r.Y), GameText.Get("signal_ghost.notify.no_target"));
                EventDirectorService.Complete(s, r.Serial, EventDirectorService.OutcomeCancelled);
                return;
            }
            Seizes++;
            Place(r);
        }

        private static string FinalOutcome(EventDirectorInstanceRecord r, string outcome) =>
            outcome == EventDirectorService.OutcomeExpired && r.Target != 0 && SignalSeizeService.TryGetSeize(r.Target, out bool downed, out int src) && downed && src == SignalSeizeService.SourceGhost
                ? EventDirectorService.OutcomeSuccess // 时限到时它被击倒（瘫痪）着：玩家已应对
                : outcome;

        private static void OnEnd(CampaignState s, EventDirectorInstanceRecord r, string outcome)
        {
            if (r.Target != 0 && SignalSeizeService.TryGetSeize(r.Target, out _, out int src) && src == SignalSeizeService.SourceGhost)
            {
                SignalSeizeService.Release(r.Target); // 事件结束：仍被夺控的归还、瘫痪的恢复（FGR-EVT-022“事件结束后恢复”）
            }
            if (outcome != EventDirectorService.OutcomeSuccess)
            {
                return;
            }
            int tech = Mathf.Max(0, Mathf.RoundToInt(GridContent.TryGetTuning("event.signal_ghost.tech_data", out float v) ? v : 40f));
            GrantTech(s, "event:signal_ghost:" + r.Serial, tech);
            IntelFragmentDef frag = IntelService.GrantFleetFragment(s);
            EventDirectorService.SetHistoryReward(s, r.Serial, tech, frag?.Id);
            Rewards++;
            string reward = frag != null ? GameText.Format("event_director.reward.fixed", tech) : GameText.Format("event_director.reward.tech_only", tech);
            FeedbackCues.RaiseLocated(FeedbackCueId.WorldEventNotice, new Vector2(r.X, r.Y),
                GameText.Format("signal_ghost.notify.reward", r.Target != 0 ? SignalPresence.MachineLabel(r.Target) : string.Empty, reward));
        }

        /// <summary>
        /// 夺控结束（CombatSite 取到内核的 SeizeEnded 事件时调；被观察与否都一样）：是本次幽灵的目标就收尾——
        /// 反向接入夺回 / 瘫痪到期恢复 = 已应对，没人管到期 = 超时，其它（机器移除等）= 作罢。我们自己 Release 引起的结束此时实例已不在，忽略。
        /// </summary>
        public static void OnSeizeEnded(int logicId, int reason, bool wasDowned)
        {
            CampaignState s = CampaignSession.Current;
            EventDirectorInstanceRecord r = Active(s);
            if (r == null || r.State != EventDirectorService.StateActive || r.Target != logicId)
            {
                return;
            }
            if (reason == EmgHijackConst.EndReclaimed)
            {
                Reclaims++;
            }
            string outcome = reason == EmgHijackConst.EndReclaimed || wasDowned ? EventDirectorService.OutcomeSuccess
                : reason == EmgHijackConst.EndExpired ? EventDirectorService.OutcomeExpired : EventDirectorService.OutcomeCancelled;
            EventDirectorService.Complete(s, r.Serial, outcome);
        }

        // ── 选目标 ──────────────────────────────────────────────────────────

        /// <summary>FGR-EVT-021：活着、在某个已载入的战斗地点里有单位（家园 / 星球上的远征队 / 远征地点都算）、不是玩家正在接入或接入过渡中的、不在维修（送修单 / 维修台，与接入的“在维修台上”同一判定）、没被别的来源控制。按 LogicId 排序（确定性）。</summary>
        public static void Candidates(CampaignState s, List<int> into)
        {
            into.Clear();
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (Valid(s, m))
                {
                    into.Add(m.LogicId);
                }
            }
            into.Sort();
        }

        private static bool Valid(CampaignState s, int logicId) => MachineRegistry.TryGetRecord(logicId, out MachineRecord m) && Valid(s, m);

        private static bool Valid(CampaignState s, MachineRecord m) =>
            m != null && m.IsAlive && !SignalUplinkService.IsUplinked(s, m.LogicId) && SignalUplinkService.PendingTargetLogicId != m.LogicId
            && MachineRoster.RepairOrder(s, m) == null && !StandingRuleService.IsOnRepairBay(s, m.LogicId) && !(SignalUplinkService.OnRepairBayProvider?.Invoke(m.LogicId) ?? false)
            && !SignalSeizeService.IsSeized(m.LogicId)
            && CombatSites.Get(m.RegionId) is { } site && site.TryGetMachineUnit(m.LogicId, out _);

        private static int Pick(CampaignState s, int serial, int attempt)
        {
            if (ForcedTargetForTests != 0 && Cand.Contains(ForcedTargetForTests))
            {
                return ForcedTargetForTests;
            }
            uint seed = unchecked((uint)(s.World?.WorldSeed ?? s.RandomSeed));
            return Cand[(int)(WorldGenMath.Hash(seed, serial, attempt, SaltPick) % (uint)Cand.Count)];
        }

        private static void Place(EventDirectorInstanceRecord r)
        {
            if (MachineRegistry.TryGetRecord(r.Target, out MachineRecord m))
            {
                r.RegionId = m.RegionId;
                Vector2 p = MachineRegistry.TryGetLivePosition(r.Target, out Vector2 live) ? live : m.WorldPosition;
                r.X = p.x;
                r.Y = p.y;
            }
        }

        private static void GrantTech(CampaignState s, string tx, int amount)
        {
            if (amount <= 0 || CampaignEconomyLedger.Find(s, tx) != null)
            {
                return;
            }
            CampaignEconomyLedger.ProposeProduce(s, tx, EventId, CampaignEconomyLedger.ResourceTechData, amount);
            CampaignEconomyLedger.Reserve(s, tx);
            CampaignEconomyLedger.MarkRunning(s, tx);
            if (CampaignEconomyLedger.Commit(s, tx).Success)
            {
                HomeInventory.Touch();
            }
        }
    }
}
