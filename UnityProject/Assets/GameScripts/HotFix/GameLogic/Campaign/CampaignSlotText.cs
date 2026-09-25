using System;
using System.Globalization;

namespace GameLogic.Campaign
{
    /// <summary>存档卡、覆盖确认、失败页共用的玩家文字。此前三处直接显示战役 ID、英文阶段枚举（Landing……）、
    /// “123s”、UTC 时间串和区域内部 ID（home_valley），失败页还把第一个槽位写成“第 0 槽”。</summary>
    public static class CampaignSlotText
    {
        /// <summary>玩家看到的槽位号（从 1 开始，与主菜单一致）。</summary>
        public static int SlotNumber(int slotIndex) => slotIndex + 1;

        public static string PhaseName(CampaignPhase phase)
        {
            switch (phase)
            {
                case CampaignPhase.Landing: return "着陆整备";
                case CampaignPhase.Rooted: return "家园站稳";
                case CampaignPhase.FirstExpedition: return "首次远征";
                case CampaignPhase.CrossCompiled: return "第一次编译完成";
                case CampaignPhase.FoundryScouting: return "铸造外围侦察";
                case CampaignPhase.SecondCrossCompiled: return "第二次编译完成";
                case CampaignPhase.CoreAssault: return "核心进攻";
                case CampaignPhase.BeaconReady: return "信标就绪";
                case CampaignPhase.Completed: return "已通关";
                case CampaignPhase.Failed: return "家园失守";
                default: return "未知阶段";
            }
        }

        /// <summary>“1 小时 02 分” / “12 分 05 秒”。</summary>
        public static string PlayTime(float seconds)
        {
            int total = Math.Max(0, (int)Math.Floor(seconds));
            int hours = total / 3600;
            int minutes = total % 3600 / 60;
            return hours > 0 ? $"{hours} 小时 {minutes:00} 分" : $"{minutes} 分 {total % 60:00} 秒";
        }

        /// <summary>存档写入时间（UTC 往返格式）→ 本地时间“2026-09-25 13:20”；解析不了写“时间未知”。</summary>
        public static string SavedAt(string writtenAtUtc)
        {
            if (!string.IsNullOrEmpty(writtenAtUtc)
                && DateTime.TryParse(writtenAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime utc))
            {
                return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            return "时间未知";
        }

        public static string RegionName(string regionId) =>
            string.IsNullOrEmpty(regionId) ? "尚未进入任何区域" : CampaignObjectiveCatalog.RegionDisplayName(regionId);

        /// <summary>一张存档卡的两行摘要：阶段｜游戏时长｜最后区域，保存时间｜内容版本。</summary>
        public static string Summary(CampaignSlotMetadata meta) =>
            $"{PhaseName(meta.CampaignPhase)}｜游戏时长 {PlayTime(meta.PlaySeconds)}｜{RegionName(meta.LastRegionId)}\n" +
            $"保存于 {SavedAt(meta.WrittenAtUtc)}｜内容版本 {meta.ContentVersion}";

        public static string StateName(CampaignSlotState state)
        {
            switch (state)
            {
                case CampaignSlotState.Empty: return "空槽";
                case CampaignSlotState.Ready: return "可读取";
                case CampaignSlotState.Corrupt: return "存档损坏";
                case CampaignSlotState.Incompatible: return "版本比游戏新";
                default: return "未知";
            }
        }
    }
}
