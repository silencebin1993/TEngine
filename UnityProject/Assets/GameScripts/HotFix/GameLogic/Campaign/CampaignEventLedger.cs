using System;
using System.Collections.Generic;

namespace GameLogic.Campaign
{
    /// <summary>
    /// ER1-SAVE-02：<see cref="EventLedgerEntry"/>（ER1-SAVE-01 已定义骨架）的写入强制点——
    /// "相同 campaignId+eventId 重复写入不重复发放"（AC-SAV-005）。campaignId 本身由调用方持有的
    /// <see cref="CampaignState"/> 实例隐含（一份 state 只属于一个 campaignId），本类只在
    /// <see cref="CampaignState.EventLedger"/> 内部按 <see cref="EventLedgerEntry.EventId"/> 判重。
    ///
    /// 真正会调用本类发奖/解锁的业务系统（目标完成、蓝图保存、远征结算、Boss 阶段奖励等）大多数
    /// 尚未实现——本 Story 只补齐判重/拒绝这一段强制逻辑本身；各业务系统开工时必须经本类写入
    /// EventLedger，不得绕过直接 <c>state.EventLedger = ...</c> 拼数组。
    /// </summary>
    public static class CampaignEventLedger
    {
        /// <summary>是否已存在同 eventId 的记录。</summary>
        public static bool Contains(CampaignState state, string eventId)
        {
            if (state?.EventLedger == null || string.IsNullOrEmpty(eventId))
            {
                return false;
            }

            foreach (EventLedgerEntry entry in state.EventLedger)
            {
                if (entry != null && string.Equals(entry.EventId, eventId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>尝试记一条新的一次性事件。已存在同 <paramref name="eventId"/> 时拒绝
        /// （不追加、不覆盖、不更新 Payload），返回 <c>false</c>——这就是幂等强制点：调用方
        /// （发奖/解锁逻辑）必须先检查返回值，<c>false</c> 时不得继续发放任何奖励/解锁任何内容。
        /// 成功时返回 <c>true</c> 并把新记录追加进 <see cref="CampaignState.EventLedger"/>
        /// （排序由后续 <see cref="CampaignState.NormalizeForSave"/> 负责，这里不重复排序）。</summary>
        public static bool TryGrant(CampaignState state, string eventId, string category,
            float grantedAtPlaySeconds, string payload = null)
        {
            if (state == null || string.IsNullOrEmpty(eventId))
            {
                return false;
            }

            if (Contains(state, eventId))
            {
                return false;
            }

            var entry = new EventLedgerEntry
            {
                EventId = eventId,
                Category = category,
                GrantedAtPlaySeconds = grantedAtPlaySeconds,
                Payload = payload,
            };

            var list = new List<EventLedgerEntry>(state.EventLedger ?? Array.Empty<EventLedgerEntry>()) { entry };
            state.EventLedger = list.ToArray();
            return true;
        }
    }
}
