using System;
using System.Collections.Generic;
using GameLogic.Core;

namespace GameLogic.Campaign.Defense
{
    /// <summary>
    /// FG6-DEF-09（FGR-DEF-060“游戏中途可以修改”）：难度变了以后突袭导演怎么处理已有的计划（<see cref="DifficultyService.Change"/> 唯一调用方）。
    /// - 还没发预警的计划（筹备 / 已排定）：改成“只有剧情突袭”（建造者）→ 触发不是剧情的计划整条取消（普通 / 静默夜 / 巨构……与 HandleTrigger 同一口径，复审 P1），
    ///   第一次突袭的名额还回去（下一次普通突袭仍固定 1 级）；剧情计划吸收过的普通计划那部分预算去掉、名额同样归还。
    ///   否则预算按“新规模 ÷ 旧规模”缩放后重新编成（间隔加成、并波吸收的预算都保留：不从零重算，复审 P1），预算变了时出发前的预报作废、重新破译。
    ///   已经发出预警 / 已出发的计划不变（玩家已经看到了预警与编成）。
    /// - 4 级周期与骚扰的下一次时刻按频率比例缩放剩余时间（只看步序号：确定）。
    /// - 取消了计划时，最短间隔的参照（上一波普通 / 任意突袭的抵达）按剩下的计划与历史（排定的抵达步）重算，不让一波不会来的突袭继续推迟后面的突袭；
    ///   取消的后日谈计划归还清洗序号。
    /// </summary>
    public static partial class RaidDirectorService
    {
        /// <summary>
        /// 改成“只有剧情突袭”时这条计划会取消几波突袭（二次确认框与修改记录同一口径）：还没预警、触发不是剧情 → 整条（1 + 它吸收的普通计划）；
        /// 还没预警的剧情计划 → 它吸收的普通计划数；其余 0。
        /// </summary>
        public static int StoryOnlyCancelCount(RaidPlanRecord p)
        {
            if (p == null || p.State >= StateWarned)
            {
                return 0;
            }
            int absorbed = p.AbsorbedIds?.Length ?? 0;
            return p.Trigger != RaidCatalog.TriggerStory ? 1 + absorbed : absorbed;
        }

        /// <summary>改成“只有剧情突袭”时一共会取消几波还没预警的非剧情突袭（不改状态）。</summary>
        public static int StoryOnlyCancelCount(CampaignState s)
        {
            int n = 0;
            foreach (RaidPlanRecord p in StateOf(s)?.Plans ?? Array.Empty<RaidPlanRecord>())
            {
                n += StoryOnlyCancelCount(p);
            }
            return n;
        }

        /// <summary>难度变了（难度 ID 与自定义倍率已经写好；<paramref name="oldFrequency"/> / <paramref name="oldScale"/> 是修改前的倍率）。返回取消了几波非剧情突袭。</summary>
        public static int OnDifficultyChanged(CampaignState s, float oldFrequency, float oldScale)
        {
            RaidDirectorState d = StateOf(s);
            if (d == null)
            {
                return 0;
            }
            EnsureState(s);
            bool storyOnly = DifficultyService.StoryOnly(s);
            float newScale = DifficultyService.Scale(s);
            double ratio = oldScale > 0f ? newScale / (double)oldScale : 1.0;
            int cancelled = 0;
            bool anyPlanCancelled = false;
            List<RaidPlanRecord> postgameCancelled = null;
            foreach (RaidPlanRecord p in d.Plans)
            {
                if (p == null || p.State >= StateWarned)
                {
                    continue;
                }
                if (storyOnly && p.Trigger != RaidCatalog.TriggerStory)
                {
                    // 与 HandleTrigger 同一口径：建造者只有剧情突袭（静默夜 / 巨构虽不受最短间隔限制，也不是剧情）。
                    cancelled += StoryOnlyCancelCount(p);
                    p.State = StateCancelled;
                    p.EndReason = EndCancelled;
                    // 这一波（连同它吸收的普通计划）不会来：第一次突袭的名额 / 骚扰的“第一次突袭之后”都按没有来过算。
                    d.RaidCount = Math.Max(0, d.RaidCount - ((p.Exempt ? 0 : 1) + (p.AbsorbedIds?.Length ?? 0)));
                    MarkAbsorbedCancelled(d, p);
                    if (p.PostgameIndex >= 0)
                    {
                        (postgameCancelled ??= new List<RaidPlanRecord>(2)).Add(p);
                    }
                    PlanChanged(s, p);
                    anyPlanCancelled = true;
                    continue;
                }
                int before = p.Budget;
                bool stripped = false;
                if (storyOnly && (p.AbsorbedIds?.Length ?? 0) > 0)
                {
                    // 剧情计划吸收过普通计划：普通那部分不来了（预算去掉、名额归还、被吸收的计划改记取消），剧情部分照常（下面再按新规模缩放）。
                    cancelled += p.AbsorbedIds.Length;
                    d.RaidCount = Math.Max(0, d.RaidCount - p.AbsorbedIds.Length);
                    MarkAbsorbedCancelled(d, p);
                    p.Budget = Math.Max(1, p.Budget - Math.Max(0, p.AbsorbedBudget));
                    p.AbsorbedBudget = 0;
                    p.AbsorbedIds = Array.Empty<string>();
                    p.Triggers = new[] { p.Trigger };
                    stripped = true;
                    anyPlanCancelled = true;
                }
                if (Math.Abs(ratio - 1.0) > 1e-6)
                {
                    // 规模变了：按比例缩放（不从零重算——排定后间隔系数会变回 1、并波吸收的预算也会丢）。只改频率 / 预警时预算与编成不动，预报照常有效。
                    p.Budget = Math.Max(1, (int)Math.Round(p.Budget * ratio));
                    p.AbsorbedBudget = Math.Min(p.Budget, Math.Max(0, (int)Math.Round(p.AbsorbedBudget * ratio)));
                }
                if (stripped || p.Budget != before)
                {
                    Compose(s, p);
                    PlanChanged(s, p);
                }
            }
            if (postgameCancelled != null)
            {
                ReturnPostgameIndices(d, postgameCancelled);
            }
            if (anyPlanCancelled)
            {
                RecomputeArrivalAnchors(d);
            }
            float newFrequency = DifficultyService.Frequency(s);
            if (oldFrequency > 0f && Math.Abs(newFrequency - oldFrequency) > 1e-5f)
            {
                long now = GameClock.Ticks;
                double fr = oldFrequency / (double)newFrequency;
                if (d.Level4NextTick > now)
                {
                    d.Level4NextTick = now + (long)Math.Round((d.Level4NextTick - now) * fr);
                }
                if (d.HarassNextTick > now)
                {
                    d.HarassNextTick = now + (long)Math.Round((d.HarassNextTick - now) * fr);
                }
            }
            Touch();
            return cancelled;
        }

        /// <summary>被 <paramref name="keeper"/> 吸收的普通计划（还在计划表里的“已合并”，或已进历史的）改记“取消”：那一波不会来，不再算最短间隔的参照。</summary>
        private static void MarkAbsorbedCancelled(RaidDirectorState d, RaidPlanRecord keeper)
        {
            string[] ids = keeper.AbsorbedIds;
            if (ids == null || ids.Length == 0)
            {
                return;
            }
            foreach (RaidPlanRecord q in d.Plans)
            {
                if (q != null && q.State == StateMerged && Array.IndexOf(ids, q.PlanId) >= 0)
                {
                    q.EndReason = EndCancelled;
                }
            }
            foreach (RaidHistoryRecord h in d.History)
            {
                if (h != null && Array.IndexOf(ids, h.PlanId) >= 0)
                {
                    h.EndReason = EndCancelled;
                }
            }
        }

        /// <summary>取消的后日谈计划归还清洗序号：从最新的往回，序号正好是最后一个时计数减一（中间的空号不回收，避免与还在的计划撞号）。</summary>
        private static void ReturnPostgameIndices(RaidDirectorState d, List<RaidPlanRecord> cancelled)
        {
            bool moved = true;
            while (moved && d.PostgameCount > 0)
            {
                moved = false;
                foreach (RaidPlanRecord p in cancelled)
                {
                    if (p.PostgameIndex == d.PostgameCount - 1)
                    {
                        d.PostgameCount--;
                        moved = true;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 最短间隔的参照按还在的计划（取消的除外）与突袭历史重算。历史取排定的抵达步（没有时取实际到达步）：已并波、途中被全歼的普通突袭也算，
        /// 与排定时累计的口径一致（复审 P2）；取消的不算。
        /// </summary>
        private static void RecomputeArrivalAnchors(RaidDirectorState d)
        {
            long last = -1;
            long any = -1;
            foreach (RaidPlanRecord p in d.Plans)
            {
                if (p == null || p.State == StateCancelled || p.EndReason == EndCancelled || p.ArrivalTick < 0)
                {
                    continue;
                }
                any = Math.Max(any, p.ArrivalTick);
                if (!p.Exempt)
                {
                    last = Math.Max(last, p.ArrivalTick);
                }
            }
            foreach (RaidHistoryRecord h in d.History)
            {
                if (h == null || h.EndReason == EndCancelled)
                {
                    continue;
                }
                long at = h.ArrivalTick > 0 ? h.ArrivalTick : h.ArrivedTick;
                if (at < 0)
                {
                    continue;
                }
                any = Math.Max(any, at);
                if (!(RaidCatalog.TryGetTrigger(h.Trigger, out RaidTriggerDef def) && def.Exempt))
                {
                    last = Math.Max(last, at);
                }
            }
            d.LastArrivalTick = last;
            d.LastAnyArrivalTick = any;
        }
    }
}
