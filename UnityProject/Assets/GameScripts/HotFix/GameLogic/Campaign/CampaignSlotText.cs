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
                case CampaignPhase.Landing: return GameText.Get("save.slot_text.landing_prep");
                case CampaignPhase.Rooted: return GameText.Get("save.slot_text.home_secured");
                case CampaignPhase.FirstExpedition: return GameText.Get("save.slot_text.first_expedition");
                case CampaignPhase.CrossCompiled: return GameText.Get("save.slot_text.first_compile_done");
                case CampaignPhase.FoundryScouting: return GameText.Get("save.slot_text.foundry_outskirts_scouting");
                case CampaignPhase.SecondCrossCompiled: return GameText.Get("save.slot_text.second_compile_done");
                case CampaignPhase.CoreAssault: return GameText.Get("save.slot_text.core_assault");
                case CampaignPhase.BeaconReady: return GameText.Get("save.slot_text.beacon_ready");
                case CampaignPhase.Completed: return GameText.Get("save.slot_text.completed");
                case CampaignPhase.Failed: return GameText.Get("save.slot_text.home_lost");
                default: return GameText.Get("save.slot_text.unknown_stage");
            }
        }

        /// <summary>“1 小时 02 分” / “12 分 05 秒”。</summary>
        public static string PlayTime(float seconds)
        {
            int total = Math.Max(0, (int)Math.Floor(seconds));
            int hours = total / 3600;
            int minutes = total % 3600 / 60;
            return hours > 0 ? GameText.Format("save.slot_text.0_h_1_00", hours, minutes) : GameText.Format("save.slot_text.0_min_1_00", minutes, total % 60);
        }

        /// <summary>存档写入时间（UTC 往返格式）→ 本地时间“2026-09-25 13:20”；解析不了写“时间未知”。</summary>
        public static string SavedAt(string writtenAtUtc)
        {
            if (!string.IsNullOrEmpty(writtenAtUtc)
                && DateTime.TryParse(writtenAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime utc))
            {
                return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            return GameText.Get("save.slot_text.time_unknown");
        }

        public static string RegionName(string regionId) =>
            string.IsNullOrEmpty(regionId) ? GameText.Get("save.slot_text.no_area_entered_yet") : CampaignObjectiveCatalog.RegionDisplayName(regionId);

        /// <summary>一张存档卡的两行摘要：阶段｜游戏时长｜最后区域，保存时间｜内容版本。</summary>
        public static string Summary(CampaignSlotMetadata meta) =>
            GameText.Format("save.slot_text.0_play_time_1", PhaseName(meta.CampaignPhase), PlayTime(meta.PlaySeconds), RegionName(meta.LastRegionId)) +
            GameText.Format("save.slot_text.saved_0_content_version", SavedAt(meta.WrittenAtUtc), meta.ContentVersion);

        public static string StateName(CampaignSlotState state)
        {
            switch (state)
            {
                case CampaignSlotState.Empty: return GameText.Get("save.slot.empty");
                case CampaignSlotState.Ready: return GameText.Get("save.slot_text.loadable");
                case CampaignSlotState.Corrupt: return GameText.Get("save.slot_text.save_damaged");
                case CampaignSlotState.Incompatible: return GameText.Get("save.slot_text.newer_than_the_game");
                case CampaignSlotState.DemoSave: return GameText.Get("save.slot_text.demo_save");
                default: return GameText.Get("circuit.panel.unknown");
            }
        }

        // ── FG0-SAVE-01：存档卡新字段与失败原因（全部走文本键 save.* / difficulty.*）──────────────

        /// <summary>“槽位 N”；副本是“槽位 N · 自动存档 k / 快速存档”（FG15-SYS-01）。</summary>
        public static string SlotTitle(int slotIndex)
        {
            string home = GameText.Format("save.slot.title", SlotNumber(CampaignSaveService.HomeOf(slotIndex)));
            switch (CampaignSaveService.KindOf(slotIndex))
            {
                case SaveSlotKind.Auto:
                    return GameText.Format("save.slot.copy_title", home, GameText.Format("save.kind.auto", CampaignSaveService.AutoNumber(slotIndex)));
                case SaveSlotKind.Quick:
                    return GameText.Format("save.slot.copy_title", home, GameText.Get("save.kind.quick"));
                default:
                    return home;
            }
        }

        /// <summary>FG15-SYS-01（FG-GAP-019）：真实游玩时长（不受倍速影响、不含暂停）；旧档没有记录时明说“未记录”，不显示成 0。</summary>
        public static string RealTime(CampaignSlotMetadata meta) =>
            meta.RealPlaySeconds > 0 ? GameText.Format("save.card.real_time", PlayTime((float)meta.RealPlaySeconds)) : GameText.Get("save.card.real_time_unknown");

        /// <summary>FG15-SYS-01（DEBT-FG3GEN01-02）：存档卡“复制种子”的文字。</summary>
        public static string SeedText(CampaignSlotMetadata meta) => meta.WorldSeed.ToString(CultureInfo.InvariantCulture);

        /// <summary>FG15-SYS-01（DEBT-FG3GEN01-02）：存档卡“复制分享短码”（种子 + 世界设置 + 生成器版本，与暂停菜单同一编码）；旧版本原型地形没有短码 = null。</summary>
        public static string ShareCode(CampaignSlotMetadata meta)
        {
            if (meta == null || meta.State != CampaignSlotState.Ready || meta.GeneratorVersion < 1 || string.IsNullOrEmpty(meta.WorldSettingsId))
            {
                return null;
            }
            try
            {
                return WorldGen.WorldSettings.EncodeShareCode(meta.WorldSeed, WorldGen.WorldSettings.Resolve(meta.GeneratorVersion, meta.WorldSettingsId));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写剪贴板（自检替换成记录）。</summary>
        public static Action<string> ClipboardHandler = text => UnityEngine.GUIUtility.systemCopyBuffer = text;

        public static bool Copy(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            ClipboardHandler?.Invoke(text);
            return true;
        }

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
            parts.Add(RealTime(meta)); // FG15-SYS-01（FG-GAP-019）：真实时长与第二行的游戏时长分开显示
            parts.Add(GameText.Format("save.card.seed", meta.WorldSeed.ToString(CultureInfo.InvariantCulture)));
            if (!string.IsNullOrEmpty(meta.WorldSettingsId))
            {
                // FG17 第 4 节：存档卡片显示种子和世界设置摘要。FG0-ARCH-05 之前的存档卡没有这个字段，不显示。
                parts.Add(GameText.Format("save.card.world", WorldGen.WorldGenService.PresetName(meta.GeneratorVersion, meta.WorldSettingsId)));
            }
            if (meta.IsPostgame)
            {
                // FG14-SBX-01（FGR-SBX-003）：后日谈存档卡显示坚守天数与最高清洗等级。
                parts.Add(GameText.Format("save.card.postgame_stats", meta.PostgameDays, meta.PostgameTopLevel));
            }
            if (meta.IsSandbox)
            {
                // FG14-SBX-02（FGR-SBX-011“存档卡片上清楚标出沙盒及其设置”）：沙盒 + 非标准的设置项。
                parts.Add(string.IsNullOrEmpty(meta.SandboxCode) ? GameText.Get("save.card.sandbox")
                    : GameText.Format("save.card.sandbox_settings", SandboxService.Describe(meta.SandboxCode)));
            }
            return string.Join(GameText.Get("expedition.intel.sep"), parts);
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
    /// <summary>FG15-SYS-01（FGR-SYS-006）：云存档冲突对话框的文字——两边各显示存档时间与游戏进度（幕、第几日、游戏时长）。</summary>
    public static class CloudConflictText
    {
        public static string Side(CloudSaveSide s) => s != null && s.Readable
            ? GameText.Format("save.cloud.side", CampaignSlotText.SavedAt(s.WrittenAtUtc), Math.Max(1, s.Act), s.Day, CampaignSlotText.PlayTime(s.PlaySeconds))
            : GameText.Format("save.cloud.unreadable", CampaignSlotText.SavedAt(s?.WrittenAtUtc));

        public static string Body(CloudSaveConflict c) =>
            GameText.Format("save.cloud.body", CampaignSlotText.SlotTitle(c.Slot)) + "\n" + GameText.Format("save.cloud.local", Side(c.Local)) + "\n"
            + GameText.Format("save.cloud.remote", Side(c.Cloud));
    }
}
