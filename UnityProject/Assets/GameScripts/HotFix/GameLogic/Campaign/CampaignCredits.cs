using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign.Regions;

namespace GameLogic.Campaign
{
    /// <summary>ER7-CREDITS-01 STORY-EXECUTION-CARDS.md：胜利页统计快照的唯一构建入口——"幸存/阵亡
    /// 编号、关键经历、使用过的两张跨派系蓝图、接管次数、出征次数、完成时间、区域清理结果均从正式
    /// Campaign/Machine/EventLedger 读取；零值也显示，不用占位或内部 ID"。本类只做只读聚合，不写
    /// 任何状态——"结算事件只写一次"的幂等性由 <see cref="HomeValleyBeacon"/> 的启动事件本身保证
    /// （胜利页只在 <see cref="HomeValleyBeacon.IsLaunched"/> 为真时展示，不需要再开第二个一次性
    /// 标记）。</summary>
    public static class CampaignCredits
    {
        public readonly struct MachineSummary
        {
            public readonly int DisplayNumber;
            public readonly bool IsAlive;
            public readonly string[] ExperienceDisplayNames;

            /// <summary>机型编号（erc_001 等），胜利页据此显示底盘图标（ER8-CONTENT-01）。</summary>
            public readonly string ChassisId;

            public MachineSummary(int displayNumber, bool isAlive, string[] experienceDisplayNames, string chassisId = null)
            {
                DisplayNumber = displayNumber;
                IsAlive = isAlive;
                ExperienceDisplayNames = experienceDisplayNames;
                ChassisId = chassisId;
            }
        }

        public readonly struct Snapshot
        {
            public readonly MachineSummary[] Machines;
            public readonly string[] CrossFactionBlueprintIds;
            public readonly int ControlTakeovers;
            public readonly int TotalExpeditionCount;
            public readonly float CompletionPlaySeconds;
            public readonly string SilentRuinsState;
            public readonly string FoundryOutpostState;

            public Snapshot(MachineSummary[] machines, string[] crossFactionBlueprintIds, int controlTakeovers,
                int totalExpeditionCount, float completionPlaySeconds, string silentRuinsState, string foundryOutpostState)
            {
                Machines = machines;
                CrossFactionBlueprintIds = crossFactionBlueprintIds;
                ControlTakeovers = controlTakeovers;
                TotalExpeditionCount = totalExpeditionCount;
                CompletionPlaySeconds = completionPlaySeconds;
                SilentRuinsState = silentRuinsState;
                FoundryOutpostState = foundryOutpostState;
            }

            public static readonly Snapshot Empty = new Snapshot(
                Array.Empty<MachineSummary>(), Array.Empty<string>(), 0, 0, 0f, "Locked", "Locked");
        }

        public static Snapshot Build(CampaignState state)
        {
            if (state == null)
            {
                return Snapshot.Empty;
            }

            // 幸存/阵亡编号 + 关键经历——全部机器（含阵亡），按编号排序，"零值也显示"：没有任何经历
            // 的机器给空数组而不是省略这条记录本身。
            MachineSummary[] machines = MachineRegistry.AllRecords
                .Where(m => m != null)
                .OrderBy(m => m.DisplayNumber)
                .Select(m => new MachineSummary(
                    m.DisplayNumber,
                    m.IsAlive,
                    (m.ExperienceFlags ?? Array.Empty<string>()).Select(MachineExperienceFlags.DisplayName).ToArray(),
                    m.ChassisId))
                .ToArray();

            // 使用过的跨派系蓝图——EventLedger 里 "exposure:cross_faction_firmware:{blueprintId}:{version}"
            // 前缀事件的 blueprintId 去重集合，唯一权威来源（同 ER6-EXPOSE-01 已验证的授予格式）。
            const string prefix = "exposure:cross_faction_firmware:";
            string[] crossFactionBlueprintIds = (state.EventLedger ?? Array.Empty<EventLedgerEntry>())
                .Where(e => e.EventId != null && e.EventId.StartsWith(prefix, StringComparison.Ordinal))
                .Select(e =>
                {
                    string rest = e.EventId.Substring(prefix.Length); // "{blueprintId}:{version}"
                    int lastColon = rest.LastIndexOf(':');
                    return lastColon > 0 ? rest.Substring(0, lastColon) : rest;
                })
                .Distinct()
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            int totalExpeditions = (FracturedCityRegion.Find(state)?.ExpeditionCount ?? 0)
                + (FoundryOutpostRegion.Find(state)?.ExpeditionCount ?? 0);

            string silentRuinsState = FracturedCityRegion.Find(state)?.State.ToString() ?? "Locked";
            string foundryOutpostState = FoundryOutpostRegion.Find(state)?.State.ToString() ?? "Locked";

            return new Snapshot(machines, crossFactionBlueprintIds, state.TotalControlTakeovers,
                totalExpeditions, state.PlaySeconds, silentRuinsState, foundryOutpostState);
        }
    }
}
