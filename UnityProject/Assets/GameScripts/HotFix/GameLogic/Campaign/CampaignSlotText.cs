using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Localization;

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
                case CampaignSlotState.DemoSave: return "Demo 存档";
                default: return "未知";
            }
        }

        // ── FG0-SAVE-01：存档卡新字段与失败原因（全部走文本键 save.* / difficulty.*）──────────────

        public static string SlotTitle(int slotIndex) => GameText.Format("save.slot.title", SlotNumber(slotIndex));

        /// <summary>"槽位 N：正文"（冒号随语言变化）。</summary>
        private static string Line(CampaignSlotMetadata meta, string body) => GameText.Format("save.slot.line", SlotTitle(meta.SlotIndex), body);

        /// <summary>难度显示名：文本键 difficulty.&lt;id 小写&gt;.name。</summary>
        public static string DifficultyName(string difficultyId) =>
            GameText.Get($"difficulty.{(string.IsNullOrEmpty(difficultyId) ? "standard" : difficultyId.ToLowerInvariant())}.name");

        /// <summary>存档卡第一行的正式版字段（FGR-SYS-007）：幕｜第几日（时钟接入后）｜难度｜种子｜世界设置｜后日谈 / 沙盒。</summary>
        public static string FgFields(CampaignSlotMetadata meta)
        {
            var parts = new List<string> { GameText.Format("save.card.act", Math.Max(1, meta.Act)) };
            if (meta.Day > 0)
            {
                parts.Add(GameText.Format("save.card.day", meta.Day));
            }
            parts.Add(DifficultyName(meta.DifficultyId));
            parts.Add(GameText.Format("save.card.seed", meta.WorldSeed.ToString(CultureInfo.InvariantCulture)));
            if (!string.IsNullOrEmpty(meta.WorldSettingsId))
            {
                // FG17 第 4 节：存档卡片显示种子和世界设置摘要。FG0-ARCH-05 之前的存档卡没有这个字段，不显示。
                parts.Add(GameText.Format("save.card.world", WorldGen.WorldGenService.PresetName(meta.GeneratorVersion, meta.WorldSettingsId)));
            }
            if (meta.IsPostgame)
            {
                parts.Add(GameText.Get("save.card.postgame"));
            }
            if (meta.IsSandbox)
            {
                parts.Add(GameText.Get("save.card.sandbox"));
            }
            return string.Join("｜", parts);
        }

        /// <summary>稳定原因码 → 玩家文字（FGR-SYS-003"提示原因"）。</summary>
        public static string ReasonText(SaveFailureReason reason, int schemaVersion = 0)
        {
            switch (reason)
            {
                case SaveFailureReason.MainMissing: return GameText.Get("save.reason.main_missing");
                case SaveFailureReason.ReadFailed: return GameText.Get("save.reason.read_failed");
                case SaveFailureReason.EmptyFile: return GameText.Get("save.reason.empty_file");
                case SaveFailureReason.Truncated: return GameText.Get("save.reason.truncated");
                case SaveFailureReason.Checksum: return GameText.Get("save.reason.checksum");
                case SaveFailureReason.Payload: return GameText.Get("save.reason.payload");
                case SaveFailureReason.MigrationMissing: return GameText.Format("save.reason.migration_missing", schemaVersion);
                case SaveFailureReason.MigrationFailed: return GameText.Format("save.reason.migration_failed", schemaVersion);
                case SaveFailureReason.DemoSave: return GameText.Get("save.reason.demo");
                case SaveFailureReason.Newer: return GameText.Get("save.reason.newer");
                default: return GameText.Get("save.reason.payload");
            }
        }

        /// <summary>备份那一行：可读取 / 没有 / 备份也坏了（附原因）。</summary>
        public static string BackupLine(CampaignSlotMetadata meta)
        {
            if (meta.HasBackup)
            {
                return GameText.Format("save.slot.backup_ready", SavedAt(meta.BackupWrittenAtUtc));
            }
            if (meta.BackupState == CampaignSlotState.Empty)
            {
                return GameText.Get("save.slot.backup_none");
            }
            return GameText.Format("save.slot.backup_bad", ReasonText(meta.BackupReason, meta.BackupSchemaVersion));
        }

        /// <summary>一张存档卡的完整文字（主菜单"读取"列表）。</summary>
        public static string CardText(CampaignSlotMetadata meta)
        {
            switch (meta.State)
            {
                case CampaignSlotState.Empty:
                    return Line(meta, GameText.Get("save.slot.empty"));
                case CampaignSlotState.Ready:
                    return Line(meta, FgFields(meta)) + "\n" + Summary(meta);
                case CampaignSlotState.DemoSave:
                    return Line(meta, GameText.Format("save.slot.demo_saved_at", SavedAt(meta.WrittenAtUtc))) + "\n" + GameText.Get("save.slot.demo");
                case CampaignSlotState.Incompatible when meta.FailureReason == SaveFailureReason.Newer:
                    return Line(meta, GameText.Format("save.slot.newer", meta.SchemaVersion, CampaignSaveService.EffectiveSchemaVersion));
                default:
                    return Line(meta, GameText.Format("save.slot.corrupt", ReasonText(meta.FailureReason, meta.SchemaVersion))) + "\n" + BackupLine(meta);
            }
        }

        /// <summary>这张卡的存档在正式版里读不了、也没有可读取的备份（Demo 存档、坏档且备份不可用、版本更新的存档）：
        /// 按钮是"新建于此槽"，点了先弹确认框，原文件另存为 *.keep-* 保留（永不自动删除存档），槽位不会被永久占住。</summary>
        public static bool StartsNewInSlot(CampaignSlotMetadata meta) =>
            meta.State == CampaignSlotState.DemoSave
            || ((meta.State == CampaignSlotState.Corrupt || meta.State == CampaignSlotState.Incompatible) && !meta.HasBackup);

        /// <summary>存档卡按钮：Empty 新建 / Ready 读取 / 坏档且有可读备份 → 读取备份 / 读不了又没有备份 → 新建于此槽（先确认）。</summary>
        public static bool ActionEnabled(CampaignSlotMetadata meta) =>
            meta.State == CampaignSlotState.Empty || meta.State == CampaignSlotState.Ready
            || meta.State == CampaignSlotState.DemoSave
            || meta.State == CampaignSlotState.Corrupt || meta.State == CampaignSlotState.Incompatible;

        public static string ActionLabel(CampaignSlotMetadata meta)
        {
            switch (meta.State)
            {
                case CampaignSlotState.Empty: return GameText.Get("save.action.new");
                case CampaignSlotState.Ready: return GameText.Get("save.action.load");
                case CampaignSlotState.DemoSave: return GameText.Get("save.action.new");
                case CampaignSlotState.Corrupt:
                case CampaignSlotState.Incompatible:
                    return meta.HasBackup ? GameText.Get("save.action.restore") : GameText.Get("save.action.new");
                default: return GameText.Get("save.action.unavailable");
            }
        }

        /// <summary>主菜单"继续"不可用时的原因。</summary>
        public static string ContinueUnavailable(bool anyDemoSave) =>
            GameText.Get(anyDemoSave ? "save.continue.demo_only" : "save.continue.none");

        /// <summary>覆盖确认正文（B04）：可读存档显示摘要；Demo / 读不出的存档说明原文件会另存保留。</summary>
        public static string OverwriteConfirm(CampaignSlotMetadata meta)
        {
            string title = SlotTitle(meta.SlotIndex);
            if (meta.State == CampaignSlotState.Ready)
            {
                return $"{GameText.Format("save.confirm.overwrite_head", title)}\n{FgFields(meta)}\n{Summary(meta)}\n\n{GameText.Get("save.confirm.overwrite_tail")}";
            }
            string keepName = System.IO.Path.GetFileName(CampaignSaveService.SlotPath(meta.SlotIndex)) + ".keep-*";
            string what = meta.State == CampaignSlotState.DemoSave
                ? GameText.Format("save.slot.demo_saved_at", SavedAt(meta.WrittenAtUtc))
                : ReasonText(meta.FailureReason, meta.SchemaVersion);
            return $"{GameText.Format("save.confirm.kept_head", title)}\n{what}\n\n{GameText.Format("save.confirm.kept_tail", keepName)}";
        }
    }
}
