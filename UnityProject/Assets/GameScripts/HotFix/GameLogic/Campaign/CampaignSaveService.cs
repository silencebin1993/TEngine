using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>ER1-SAVE-01：一次 Save 调用的结果。<see cref="CampaignSaveService"/> 的公开方法
    /// 永不向调用方抛异常（AI-EXECUTION-PROTOCOL.md Done 口径："Load/Save 永不向 UI 抛异常"），
    /// 全部异常在内部捕获并转换成本枚举 + <see cref="SaveOutcome.Message"/>。</summary>
    public enum SaveOutcome
    {
        Success = 0,
        DirectoryNotWritable = 1,
        TempWriteFailed = 2,
        ReplaceFailed = 3,
        NoActiveCampaign = 4,
        /// <summary>FG0-SAVE-01：要覆盖的旧文件（Demo / 其他版本 / 读不出头部）另存保留失败，为了不丢存档放弃这次写入。</summary>
        PreserveFailed = 5,
    }

    [Serializable]
    public readonly struct SaveResult
    {
        public readonly SaveOutcome Outcome;
        public readonly string Message;
        /// <summary>FG0-SAVE-01：这次写入前被另存保留的旧文件路径（没有则为 null）。</summary>
        public readonly string PreservedPath;

        public SaveResult(SaveOutcome outcome, string message, string preservedPath = null)
        {
            Outcome = outcome;
            Message = message;
            PreservedPath = preservedPath;
        }

        public bool Success => Outcome == SaveOutcome.Success;

        public static readonly SaveResult Ok = new SaveResult(SaveOutcome.Success, null);
    }

    public enum LoadOutcome
    {
        Success = 0,
        Empty = 1,
        Corrupt = 2,
        /// <summary>存档来自更新的游戏版本，或缺少把它升到当前版本的升级器。</summary>
        Incompatible = 3,
        /// <summary>FG0-SAVE-01：Demo（0.1）存档。正式版不迁移（FGR-ARC-008），明确提示，文件不动。</summary>
        DemoSave = 4,
    }

    /// <summary>FG0-SAVE-01：读档失败 / 槽位不可用的**稳定原因码**（FGR-SYS-003"提示原因"），玩家文字见
    /// <see cref="CampaignSlotText.ReasonText"/>（文本键 save.reason.*）。</summary>
    public enum SaveFailureReason
    {
        None = 0,
        /// <summary>主档不存在，但备份还在（例如写入过程被打断、或文件被外部删除）。</summary>
        MainMissing = 1,
        ReadFailed = 2,
        EmptyFile = 3,
        /// <summary>JSON 截断 / 结构不完整——最常见于写入中途断电或被强制结束。</summary>
        Truncated = 4,
        Checksum = 5,
        /// <summary>头部完好、校验通过，但正文无法解析成战役状态。</summary>
        Payload = 6,
        MigrationMissing = 7,
        MigrationFailed = 8,
        DemoSave = 9,
        Newer = 10,
    }

    public readonly struct LoadResult
    {
        public readonly LoadOutcome Outcome;
        public readonly CampaignState State;
        public readonly string Message;
        public readonly SaveFailureReason Reason;
        /// <summary>文件里记录的存档格式版本（读不出时为 -1）。</summary>
        public readonly int FileSchemaVersion;
        /// <summary>失败发生在哪一级升级器（仅 MigrationMissing / MigrationFailed）。</summary>
        public readonly int MigrationFailedFromVersion;
        /// <summary>本次读档新产生的通知（例如已移除内容转成废料），调用方负责展示给玩家。</summary>
        public readonly SaveNoticeRecord[] Notices;

        public LoadResult(LoadOutcome outcome, CampaignState state, string message,
            SaveFailureReason reason = SaveFailureReason.None, int fileSchemaVersion = -1,
            int migrationFailedFromVersion = 0, SaveNoticeRecord[] notices = null)
        {
            Outcome = outcome;
            State = state;
            Message = message;
            Reason = reason;
            FileSchemaVersion = fileSchemaVersion;
            MigrationFailedFromVersion = migrationFailedFromVersion;
            Notices = notices ?? Array.Empty<SaveNoticeRecord>();
        }

        public bool Success => Outcome == LoadOutcome.Success;
    }

    /// <summary>ER1-SAVE-01 + FG0-SAVE-01：存档卡元数据（FGR-SYS-007）。</summary>
    [Serializable]
    public sealed class CampaignSlotMetadata
    {
        public int SlotIndex;
        public CampaignSlotState State;
        public string CampaignId;
        public string DifficultyId;
        public CampaignPhase CampaignPhase;
        public float PlaySeconds;
        public string WrittenAtUtc;
        public int SchemaVersion;
        public int ContentVersion;
        /// <summary>给开发看的具体错误（进日志）；玩家文字由 <see cref="FailureReason"/> 决定。</summary>
        public string ErrorMessage;
        /// <summary>有**可读取**的备份（Ready）。备份自身损坏 / 是 Demo 存档时为 false，原因见 <see cref="BackupReason"/>。</summary>
        public bool HasBackup;

        /// <summary>ER2-BOOT-01：ERD-UI-001 要求存档卡显示"最后区域"。当前唯一可达的正式区域是细胞阶段
        /// 占位（归还谷地正式场景是 ER2-SCENE-01 的范围），新战役未进入过任何区域时为 null/空。</summary>
        public string LastRegionId;

        // ── FG0-SAVE-01 新字段（FGR-SYS-007 / FGR-SYS-003）──
        public SaveFailureReason FailureReason;
        public int Act;
        /// <summary>第几日；0 = 统一时钟尚未接入，卡片不显示（FG0-ARCH-01 起有值）。</summary>
        public int Day;
        public int WorldSeed;
        /// <summary>FG0-ARCH-05：世界设置预设 ID 与生成器版本（存档卡显示世界设置摘要，FG17 第 4 节）。FG0-ARCH-05 之前的存档为空 / 0，卡片不显示。</summary>
        public string WorldSettingsId;
        public int GeneratorVersion;
        public bool IsPostgame;
        public bool IsSandbox;
        /// <summary>缩略图文件路径。尚未实现截图（DEBT-FG0SAVE01-01 → FG15-SYS-01），恒为 null。</summary>
        public string ThumbnailPath;
        /// <summary>备份文件本身的状态（Empty = 没有备份）。</summary>
        public CampaignSlotState BackupState;
        public SaveFailureReason BackupReason;
        public int BackupSchemaVersion;
        public string BackupWrittenAtUtc;
        /// <summary>备份是否经过完整校验。主档可读时为 false（只确认文件存在，列表性能）；主档不可用时为 true。</summary>
        public bool BackupVerified;
        /// <summary>写入存档的游戏版本（"0.2"；Demo 存档为空）。</summary>
        public string ProductVersion;
    }

    /// <summary>ERD-SAV-001 存档格式外层头部。FG0-SAVE-01（v2）起新增 <see cref="ProductVersion"/> 与
    /// <see cref="CardJson"/>（存档卡字段，列表不必反序列化整份正文），校验和覆盖正文与卡片两段。</summary>
    [Serializable]
    internal sealed class CampaignSaveEnvelope
    {
        public int SchemaVersion;
        public int ContentVersion;
        public string Checksum;
        public string WrittenAtUtc;
        public string ProductVersion;
        public string CardJson;
        public string PayloadJson;
    }

    /// <summary>存档卡片头（FGR-SYS-007），以 JSON 文本形式嵌在头部，参与校验。</summary>
    [Serializable]
    internal sealed class CampaignSaveCard
    {
        public string CampaignId;
        public string DifficultyId;
        public CampaignPhase CampaignPhase;
        public float PlaySeconds;
        public string LastRegionId;
        public int Act;
        public int Day;
        public int WorldSeed;
        public string WorldSettingsId;
        public int GeneratorVersion;
        public bool IsPostgame;
        public bool IsSandbox;
    }

    /// <summary>FG0-SAVE-01 自检专用：在存档流程的指定位置模拟"进程被强制结束"。</summary>
    public enum SaveCrashPoint
    {
        None = 0,
        /// <summary>临时文件只写了一半。</summary>
        MidTempWrite = 1,
        /// <summary>临时文件已完整落盘，还没替换主档。</summary>
        AfterTempWritten = 2,
    }

    /// <summary>模拟的进程强制结束。存档流程里的清理代码**不会**处理它（与真实被杀一样，不会走到 catch/清理），
    /// 只在自检注入 <see cref="CampaignSaveService.CrashPointForTests"/> 时抛出。</summary>
    public sealed class SimulatedSaveCrashException : Exception
    {
        public SimulatedSaveCrashException(SaveCrashPoint point) : base($"模拟进程在 {point} 处被强制结束") { }
    }

    /// <summary>
    /// ER1-SAVE-01 / FG0-SAVE-01：CampaignState 的磁盘 IO 引擎（存档 v2 骨架，FGR-ARC-008、FGR-SYS-001～004）。
    ///
    /// 落盘：写 temp（WriteThrough + Flush(true)）→ 主档不是当前格式（Demo / 其他版本 / 头部读不出）时先另存保留
    /// → <see cref="File.Replace"/> 一次系统调用"temp 覆盖主档、旧主档转存 bak"（NTFS 原子）→ 无主档时 <see cref="File.Move"/>。
    /// 任何一步失败都不改动已存在的主档 / 备份，只返回失败结果。**永不自动删除存档**：会被覆盖掉的不可读文件一律
    /// 另存为 <c>*.keep-*</c>；恢复备份前把坏掉的主档也另存保留。
    ///
    /// 读取：头部 → 版本分流（≤1 Demo：明确提示不迁移；&gt; 当前：拒绝；&lt; 当前：逐级升级器）→ 校验和 → 正文 →
    /// 补齐新状态域 → 内容对账（已移除内容转废料并通知，<see cref="SaveContentReconciler"/>）。
    /// </summary>
    public static class CampaignSaveService
    {
        /// <summary>FG0-SAVE-01：正式版 0.2 = 存档格式 v2（Demo 0.1 = v1，不迁移）。</summary>
        public const int CurrentSchemaVersion = 2;
        public const int CurrentContentVersion = 1;
        public const string ProductVersion = "0.2";

        /// <summary>Demo 骨架先给 3 个固定槽位；手动槽不限、自动存档轮换、快速存档属于 FG15-SYS-01（DEBT-FG0SAVE01-02）。</summary>
        public const int SlotCount = 3;

        /// <summary>自检专用：把存档目录临时改到别处（用完置回 null）。负向旅程要制造损坏存档，绝不能碰玩家真实槽位。</summary>
        public static string SaveDirectoryOverrideForTests { get; set; }

        /// <summary>自检专用：在存档流程里模拟进程被强制结束（FGT-SYS-001）。用完置回 None。</summary>
        public static SaveCrashPoint CrashPointForTests { get; set; }

        /// <summary>当前生效的存档格式版本（正式 = <see cref="CurrentSchemaVersion"/>；自检可换成假想的未来版本链）。</summary>
        public static int EffectiveSchemaVersion => CampaignSaveMigrations.Active.TargetVersion;

        public static string SaveDirectory => SaveDirectoryOverrideForTests ?? Path.Combine(Application.persistentDataPath, "Campaigns");

        public static string SlotPath(int slotIndex) =>
            Path.Combine(SaveDirectory, $"campaign_slot{slotIndex}.json");

        public static string TempPath(int slotIndex) => SlotPath(slotIndex) + ".tmp";
        public static string BakPath(int slotIndex) => SlotPath(slotIndex) + ".bak";

        /// <summary>被保留的旧文件：<c>campaign_slotN.json.keep-&lt;标签&gt;-&lt;UTC 时间&gt;</c>。</summary>
        public static string KeepPath(int slotIndex, string tag)
        {
            string basePath = SlotPath(slotIndex) + $".keep-{tag}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}";
            string path = basePath;
            for (int n = 2; File.Exists(path); n++)
            {
                path = basePath + "-" + n.ToString(CultureInfo.InvariantCulture); // 同一毫秒内多次另存也不重名、不覆盖
            }
            return path;
        }

        /// <summary>某槽位已保留的旧文件（按文件名排序）。</summary>
        public static string[] PreservedFiles(int slotIndex)
        {
            try
            {
                if (!Directory.Exists(SaveDirectory))
                {
                    return Array.Empty<string>();
                }
                string prefix = Path.GetFileName(SlotPath(slotIndex)) + ".keep-";
                return Directory.GetFiles(SaveDirectory, prefix + "*").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // ── 写 ───────────────────────────────────────────────────────────

        /// <summary>整份覆盖写入（不是追加）。落盘前调用 <see cref="CampaignState.NormalizeForSave"/>
        /// 保证稳定 ID 排序。永不抛出（自检注入的模拟崩溃除外）；失败原因见返回值。</summary>
        public static SaveResult Save(int slotIndex, CampaignState state, SaveReason reason)
        {
            if (state == null)
            {
                return new SaveResult(SaveOutcome.NoActiveCampaign, "CampaignState 为空，未写入。");
            }

            int schema = EffectiveSchemaVersion;
            state.LastSaveReason = reason;
            state.SchemaVersion = schema;
            state.ContentVersion = CurrentContentVersion;
            // FG0-ARCH-05（FGR-GEN-060）：把各表面被修改过的区块写进区块差异（只存修改；未修改的读档时按种子重新生成）。
            WorldGen.WorldGenService.CaptureDiffs(state);
            state.NormalizeForSave();

            string dir = SaveDirectory;
            string mainPath = SlotPath(slotIndex);
            string tempPath = TempPath(slotIndex);
            string bakPath = BakPath(slotIndex);

            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 存档目录不可写，槽位 {slotIndex} 未保存：{e.Message}");
                return new SaveResult(SaveOutcome.DirectoryNotWritable, e.Message);
            }

            string payloadJson;
            string cardJson;
            try
            {
                payloadJson = JsonUtility.ToJson(state);
                cardJson = JsonUtility.ToJson(BuildCard(state));
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] CampaignState 序列化失败，槽位 {slotIndex} 未保存：{e.Message}");
                return new SaveResult(SaveOutcome.TempWriteFailed, e.Message);
            }

            var envelope = new CampaignSaveEnvelope
            {
                SchemaVersion = schema,
                ContentVersion = CurrentContentVersion,
                Checksum = ComputeChecksum(payloadJson, cardJson),
                WrittenAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ProductVersion = ProductVersion,
                CardJson = cardJson,
                PayloadJson = payloadJson,
            };
            byte[] bytes = new UTF8Encoding(false).GetBytes(JsonUtility.ToJson(envelope));

            try
            {
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    if (CrashPointForTests == SaveCrashPoint.MidTempWrite)
                    {
                        fs.Write(bytes, 0, bytes.Length / 2);
                        fs.Flush(true);
                        throw new SimulatedSaveCrashException(SaveCrashPoint.MidTempWrite);
                    }
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
            }
            catch (Exception e) when (!(e is SimulatedSaveCrashException))
            {
                TEngine.Log.Warning($"[CampaignSaveService] 写 temp 失败，槽位 {slotIndex} 主档未改动：{e.Message}");
                TryDeleteQuiet(tempPath);
                return new SaveResult(SaveOutcome.TempWriteFailed, e.Message);
            }

            if (CrashPointForTests == SaveCrashPoint.AfterTempWritten)
            {
                throw new SimulatedSaveCrashException(SaveCrashPoint.AfterTempWritten);
            }

            // 永不自动删除存档（FGR-SYS-003）。File.Replace 会把旧主档转存为 bak、同时覆盖掉原来的 bak，所以写入前
            // 先把"会被覆盖、又不是本战役上一版"的文件另存：
            // - 新建战役（玩家已在确认框里同意占用此槽）：旧主档复制保留，旧备份整体挪成 keep-bak（主档存不存在都一样——
            //   主档丢失、只剩备份的槽位同样如此），替换时不再生成 bak：新战役从干净的槽位开始，bak 只会是本战役的上一版。
            // - 常规存档（自动存档等）：旧主档不是当前格式（Demo / 其他版本 / 头部读不出）→ 先另存一份；
            //   旧主档是当前格式但内容已坏（被外部改坏 / 截断）→ 整体挪成 keep-corrupt，完好的 bak 原样留着不被挤掉；
            //   现有 bak 不属于本战役（不含本战役 ID）→ 先另存为 keep-bak。
            bool newCampaign = reason == SaveReason.NewCampaign;
            string preserved = null;
            try
            {
                if (newCampaign)
                {
                    if (File.Exists(mainPath))
                    {
                        preserved = KeepPath(slotIndex, PreserveTagFor(mainPath, schema) ?? "replaced");
                        File.Copy(mainPath, preserved, overwrite: false);
                    }
                    if (File.Exists(bakPath))
                    {
                        File.Move(bakPath, KeepPath(slotIndex, "bak"));
                    }
                }
                else
                {
                    if (File.Exists(mainPath))
                    {
                        string tag = PreserveTagFor(mainPath, schema);
                        if (tag != null)
                        {
                            preserved = KeepPath(slotIndex, tag);
                            File.Copy(mainPath, preserved, overwrite: false);
                        }
                        else if (File.Exists(bakPath) && !IsKnownGoodMain(mainPath) && Probe(mainPath).Kind != CampaignSlotState.Ready)
                        {
                            preserved = KeepPath(slotIndex, "corrupt");
                            File.Move(mainPath, preserved);
                        }
                    }
                    if (File.Exists(mainPath) && File.Exists(bakPath) && !BakBelongsTo(bakPath, state.CampaignId))
                    {
                        File.Copy(bakPath, KeepPath(slotIndex, "bak"), overwrite: false);
                    }
                }
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 槽位 {slotIndex} 旧文件另存失败，放弃本次写入以免丢档：{e.Message}");
                TryDeleteQuiet(tempPath);
                return new SaveResult(SaveOutcome.PreserveFailed, e.Message);
            }

            try
            {
                if (File.Exists(mainPath))
                {
                    // 单次系统调用：temp 覆盖主档；常规存档时旧主档原子转存为 bak，新建战役时旧主档已另存、不生成 bak。
                    File.Replace(tempPath, mainPath, newCampaign ? null : bakPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, mainPath);
                }
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 替换主档失败，槽位 {slotIndex} 保留旧档：{e.Message}");
                TryDeleteQuiet(tempPath);
                return new SaveResult(SaveOutcome.ReplaceFailed, e.Message, preserved);
            }

            MarkKnownGoodMain(mainPath);
            return new SaveResult(SaveOutcome.Success, null, preserved);
        }

        // ── 文件戳：本进程写出 / 完整读过的主档，与读档失败记录 ──────────────────

        private struct FileStamp
        {
            public long Length;
            public DateTime WriteUtc;
        }

        private sealed class LoadFailureRecord
        {
            public FileStamp Stamp;
            public SaveFailureReason Reason;
            public int SchemaVersion;
        }

        /// <summary>本进程刚写出或完整校验读过的主档（长度 + 修改时间）。常规存档据此跳过"主档是否已坏"的整份校验，
        /// 只有文件在进程外被改动过时才整份读一遍。</summary>
        private static readonly Dictionary<string, FileStamp> KnownGoodMains = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);

        /// <summary>头部与校验和都通过、但读档在升级 / 正文 / 结构校验时失败的主档（按文件戳识别同一份文件）。
        /// 列表、"继续"、存档卡按钮都据此把它当损坏存档处理，"读取备份"才真正可用；文件一变（存档 / 恢复备份）自动失效。</summary>
        private static readonly Dictionary<string, LoadFailureRecord> LoadFailures = new Dictionary<string, LoadFailureRecord>(StringComparer.OrdinalIgnoreCase);

        private static bool TryStamp(string path, out FileStamp stamp)
        {
            stamp = default;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return false;
                }
                stamp = new FileStamp { Length = info.Length, WriteUtc = info.LastWriteTimeUtc };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void MarkKnownGoodMain(string mainPath)
        {
            LoadFailures.Remove(mainPath);
            if (TryStamp(mainPath, out FileStamp stamp))
            {
                KnownGoodMains[mainPath] = stamp;
            }
            else
            {
                KnownGoodMains.Remove(mainPath);
            }
        }

        private static bool IsKnownGoodMain(string mainPath) =>
            KnownGoodMains.TryGetValue(mainPath, out FileStamp known) && TryStamp(mainPath, out FileStamp now)
            && known.Length == now.Length && known.WriteUtc == now.WriteUtc;

        /// <summary>记录"主档头部可读、读档却失败"（升级器失败、正文坏、恢复编排的结构校验失败）。之后这个槽位在列表里显示为
        /// 损坏并完整校验备份，不再参与"继续"，存档卡按钮走"读取备份"而不是反复读同一份坏档。</summary>
        public static void RecordLoadFailure(int slotIndex, SaveFailureReason reason, int schemaVersion)
        {
            string mainPath = SlotPath(slotIndex);
            KnownGoodMains.Remove(mainPath);
            if (!TryStamp(mainPath, out FileStamp stamp))
            {
                LoadFailures.Remove(mainPath);
                return;
            }
            LoadFailures[mainPath] = new LoadFailureRecord
            {
                Stamp = stamp,
                Reason = reason == SaveFailureReason.None ? SaveFailureReason.Payload : reason,
                SchemaVersion = schemaVersion,
            };
        }

        private static LoadFailureRecord FindLoadFailure(string mainPath)
        {
            if (!LoadFailures.TryGetValue(mainPath, out LoadFailureRecord rec))
            {
                return null;
            }
            if (TryStamp(mainPath, out FileStamp now) && now.Length == rec.Stamp.Length && now.WriteUtc == rec.Stamp.WriteUtc)
            {
                return rec;
            }
            LoadFailures.Remove(mainPath); // 文件已变，旧失败记录作废
            return null;
        }

        /// <summary>备份是否属于这个战役：只读文件开头（卡片头在正文之前），看里面有没有本战役 ID。</summary>
        private static bool BakBelongsTo(string bakPath, string campaignId)
        {
            if (string.IsNullOrEmpty(campaignId))
            {
                return true;
            }
            try
            {
                using (var fs = new FileStream(bakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buf = new byte[4096];
                    int n = fs.Read(buf, 0, buf.Length);
                    return Encoding.UTF8.GetString(buf, 0, n).Contains(campaignId);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>备份文件在不在（不做校验）。主档可读时列表 / 恢复编排只需要知道这一点。</summary>
        public static bool BackupFileExists(int slotIndex) => File.Exists(BakPath(slotIndex));

        private static CampaignSaveCard BuildCard(CampaignState s) => new CampaignSaveCard
        {
            CampaignId = s.CampaignId,
            DifficultyId = s.DifficultyId,
            CampaignPhase = s.CampaignPhase,
            PlaySeconds = s.PlaySeconds,
            LastRegionId = s.CurrentRegionId,
            Act = s.Progress?.Act ?? 1,
            Day = s.Clock?.Day ?? 0,
            WorldSeed = s.World?.WorldSeed ?? s.RandomSeed,
            WorldSettingsId = s.World?.WorldSettingsId,
            GeneratorVersion = s.World?.GeneratorVersion ?? 0,
            IsPostgame = s.Progress?.IsPostgame ?? false,
            IsSandbox = s.Progress?.IsSandbox ?? false,
        };

        private static readonly Regex SchemaHeaderRegex = new Regex("\"SchemaVersion\"\\s*:\\s*(\\d+)", RegexOptions.CultureInvariant);

        /// <summary>只读文件开头判断格式版本（不读全文，不拖慢大存档的写入）：当前格式返回 null（不需要另存），
        /// 否则返回保留标签。</summary>
        private static string PreserveTagFor(string path, int currentSchema)
        {
            string head;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buf = new byte[256];
                    int n = fs.Read(buf, 0, buf.Length);
                    head = Encoding.UTF8.GetString(buf, 0, n);
                }
            }
            catch
            {
                return "unreadable";
            }
            Match m = SchemaHeaderRegex.Match(head);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            {
                return "unreadable";
            }
            if (v == currentSchema)
            {
                return null;
            }
            return v <= CampaignSaveMigrations.LastDemoSchemaVersion ? $"demo-v{v}" : $"v{v}";
        }

        // ── 读 ───────────────────────────────────────────────────────────

        /// <summary>读取存档。永不抛出；文件缺失 / 损坏 / Demo / 版本不兼容均返回带原因码的结果，
        /// 不偷偷新建战役、不吞掉错误、不改动磁盘上的任何文件。</summary>
        public static LoadResult Load(int slotIndex)
        {
            string mainPath = SlotPath(slotIndex);
            FileProbe probe = Probe(mainPath);
            SaveMigrationChain chain = CampaignSaveMigrations.Active;

            switch (probe.Kind)
            {
                case CampaignSlotState.Empty:
                    if (File.Exists(BakPath(slotIndex)))
                    {
                        bool bakReady = Probe(BakPath(slotIndex)).Kind == CampaignSlotState.Ready;
                        return new LoadResult(LoadOutcome.Corrupt, null, bakReady ? "主存档不存在，但备份可用。" : "主存档不存在，备份也无法读取。",
                            SaveFailureReason.MainMissing);
                    }
                    return new LoadResult(LoadOutcome.Empty, null, "存档不存在。");
                case CampaignSlotState.DemoSave:
                    return new LoadResult(LoadOutcome.DemoSave, null, probe.Message, SaveFailureReason.DemoSave, probe.SchemaVersion);
                case CampaignSlotState.Incompatible:
                    return new LoadResult(LoadOutcome.Incompatible, null, probe.Message, probe.Reason, probe.SchemaVersion,
                        probe.Reason == SaveFailureReason.MigrationMissing ? probe.MissingFrom : 0);
                case CampaignSlotState.Corrupt:
                    return new LoadResult(LoadOutcome.Corrupt, null, probe.Message, probe.Reason, probe.SchemaVersion);
            }

            CampaignSaveEnvelope envelope = probe.Envelope;
            string payload = envelope.PayloadJson;
            if (envelope.SchemaVersion < chain.TargetVersion)
            {
                SaveMigrationRun run = chain.Run(envelope.SchemaVersion, payload);
                if (!run.Success)
                {
                    bool missing = run.Outcome == SaveMigrationOutcome.Missing || run.Outcome == SaveMigrationOutcome.OutOfRange;
                    TEngine.Log.Warning($"[CampaignSaveService] 槽位 {slotIndex} 升级失败：{run.Message}（原文件未改动）");
                    RecordLoadFailure(slotIndex, missing ? SaveFailureReason.MigrationMissing : SaveFailureReason.MigrationFailed, envelope.SchemaVersion);
                    return new LoadResult(missing ? LoadOutcome.Incompatible : LoadOutcome.Corrupt, null, run.Message,
                        missing ? SaveFailureReason.MigrationMissing : SaveFailureReason.MigrationFailed,
                        envelope.SchemaVersion, run.FailedFromVersion);
                }
                TEngine.Log.Info($"[CampaignSaveService] 槽位 {slotIndex} 存档 v{envelope.SchemaVersion} 已逐级升级到 v{chain.TargetVersion}" +
                    $"（{string.Join("、", run.AppliedFromVersions.Select(v => $"v{v}→v{v + 1}"))}）；下次存档写入新格式，旧文件另存保留。");
                payload = run.PayloadJson;
            }

            CampaignState state;
            try
            {
                state = JsonUtility.FromJson<CampaignState>(payload);
            }
            catch (Exception e)
            {
                RecordLoadFailure(slotIndex, SaveFailureReason.Payload, envelope.SchemaVersion);
                return new LoadResult(LoadOutcome.Corrupt, null, $"正文解析失败：{e.Message}", SaveFailureReason.Payload, envelope.SchemaVersion);
            }

            if (state == null)
            {
                RecordLoadFailure(slotIndex, SaveFailureReason.Payload, envelope.SchemaVersion);
                return new LoadResult(LoadOutcome.Corrupt, null, "正文反序列化为 null。", SaveFailureReason.Payload, envelope.SchemaVersion);
            }

            CampaignFgStateDomains.EnsureAll(state);
            state.SchemaVersion = chain.TargetVersion;

            // FG0-ARCH-05（FGR-GEN-061 / 060）：生成器版本比本版本游戏新 → 版本更新（Newer）；已知表面的区块差异损坏 → 正文损坏。
            if (!WorldGen.WorldGenService.ValidateForLoad(state, out SaveFailureReason worldReason, out string worldMessage))
            {
                TEngine.Log.Warning($"[CampaignSaveService] 槽位 {slotIndex} 世界数据无法恢复：{worldMessage}（原文件未改动）");
                RecordLoadFailure(slotIndex, worldReason, envelope.SchemaVersion);
                return new LoadResult(worldReason == SaveFailureReason.Newer ? LoadOutcome.Incompatible : LoadOutcome.Corrupt, null, worldMessage,
                    worldReason, envelope.SchemaVersion);
            }

            SaveNoticeRecord[] notices;
            try
            {
                notices = SaveContentReconciler.Reconcile(state, envelope.ContentVersion, CurrentContentVersion);
            }
            catch (Exception e)
            {
                // 对账是"尽力而为"的内容迁移：失败时原样保留物品，不让整份存档打不开。
                TEngine.Log.Error($"[CampaignSaveService] 槽位 {slotIndex} 内容对账异常，物品原样保留：{e}");
                notices = Array.Empty<SaveNoticeRecord>();
            }
            state.ContentVersion = CurrentContentVersion;
            if (envelope.SchemaVersion == chain.TargetVersion)
            {
                MarkKnownGoodMain(mainPath); // 完整校验过的当前格式主档：之后的常规存档不必再整份校验它
            }
            else
            {
                LoadFailures.Remove(mainPath);
            }
            return new LoadResult(LoadOutcome.Success, state, null, SaveFailureReason.None, envelope.SchemaVersion, 0, notices);
        }

        /// <summary>查询单个槽位的存档卡元数据，供主菜单渲染。永不抛出。不执行升级与对账（只读头部与卡片）。</summary>
        public static CampaignSlotMetadata GetSlotMetadata(int slotIndex)
        {
            var meta = new CampaignSlotMetadata { SlotIndex = slotIndex };
            string mainPath = SlotPath(slotIndex);
            FileProbe main = Probe(mainPath);
            meta.State = main.Kind;
            meta.FailureReason = main.Reason;
            meta.ErrorMessage = main.Message;
            meta.SchemaVersion = main.SchemaVersion;
            meta.WrittenAtUtc = main.WrittenAtUtc;

            // 头部可读、但本进程里读这份文件失败过（升级 / 正文 / 结构校验）：按读档结果显示为损坏，
            // 不参与"继续"，按钮走"读取备份"（FGR-SYS-003）。
            LoadFailureRecord failed = main.Kind == CampaignSlotState.Ready ? FindLoadFailure(mainPath) : null;
            if (failed != null)
            {
                meta.State = failed.Reason == SaveFailureReason.MigrationMissing ? CampaignSlotState.Incompatible : CampaignSlotState.Corrupt;
                meta.FailureReason = failed.Reason;
                meta.SchemaVersion = failed.SchemaVersion;
                meta.ErrorMessage = "读档失败：" + failed.Reason;
            }

            // 备份只在主档不可用时完整校验（此时卡片要显示"能否读取备份"）；主档可读时只看备份文件在不在，
            // 省掉一次整文件读取与哈希（大存档的列表耗时减半）。
            string bakPath = BakPath(slotIndex);
            if (meta.State == CampaignSlotState.Ready)
            {
                bool exists = File.Exists(bakPath);
                meta.HasBackup = exists;
                meta.BackupState = exists ? CampaignSlotState.Ready : CampaignSlotState.Empty;
                meta.BackupVerified = false;
            }
            else
            {
                FileProbe bak = Probe(bakPath);
                meta.BackupState = bak.Kind;
                meta.BackupReason = bak.Reason;
                meta.BackupSchemaVersion = bak.SchemaVersion;
                meta.BackupWrittenAtUtc = bak.Envelope?.WrittenAtUtc;
                meta.HasBackup = bak.Kind == CampaignSlotState.Ready;
                meta.BackupVerified = true;
            }

            if (main.Kind == CampaignSlotState.Empty && meta.BackupState != CampaignSlotState.Empty)
            {
                // 主档没了但备份文件还在：不显示成"空槽"。备份可读 → "主存档丢失 + 可以读取备份"；备份也坏了 / 是 Demo /
                // 版本更新 → "主存档丢失 + 备份也无法使用（原因）"。在这里新建要先确认，旧备份另存为 keep-bak 保留。
                meta.State = CampaignSlotState.Corrupt;
                meta.FailureReason = SaveFailureReason.MainMissing;
                meta.ErrorMessage = meta.HasBackup ? "主存档不存在，但备份可用。" : "主存档不存在，备份也无法读取。";
                return meta;
            }
            if (meta.State != CampaignSlotState.Ready)
            {
                return meta;
            }

            CampaignSaveCard card = main.Card;
            meta.ContentVersion = main.Envelope.ContentVersion;
            meta.ProductVersion = main.Envelope.ProductVersion;
            meta.CampaignId = card.CampaignId;
            meta.DifficultyId = card.DifficultyId;
            meta.CampaignPhase = card.CampaignPhase;
            meta.PlaySeconds = card.PlaySeconds;
            meta.LastRegionId = card.LastRegionId;
            meta.Act = card.Act;
            meta.Day = card.Day;
            meta.WorldSeed = card.WorldSeed;
            meta.WorldSettingsId = card.WorldSettingsId;
            meta.GeneratorVersion = card.GeneratorVersion;
            meta.IsPostgame = card.IsPostgame;
            meta.IsSandbox = card.IsSandbox;
            meta.ThumbnailPath = null;
            return meta;
        }

        public static CampaignSlotMetadata[] GetAllSlotMetadata()
        {
            var result = new CampaignSlotMetadata[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                result[i] = GetSlotMetadata(i);
            }

            return result;
        }

        /// <summary>"继续"只指向最后一个安全可读档：在全部 Ready 槽位里取 WrittenAtUtc 最新的一个。
        /// 没有任何 Ready 槽位时返回 -1，不静默新建战役。Demo 存档不参与。</summary>
        public static int ResolveContinueSlot() => ResolveContinueSlot(GetAllSlotMetadata());

        /// <summary>用已经查好的一组元数据决定"继续"，避免主菜单一次刷新里把每个存档读好几遍。</summary>
        public static int ResolveContinueSlot(CampaignSlotMetadata[] metas)
        {
            int best = -1;
            DateTime bestTime = DateTime.MinValue;
            for (int i = 0; i < metas.Length; i++)
            {
                CampaignSlotMetadata meta = metas[i];
                if (meta.State != CampaignSlotState.Ready)
                {
                    continue;
                }

                if (!DateTime.TryParse(meta.WrittenAtUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out DateTime t))
                {
                    continue;
                }

                if (best == -1 || t > bestTime)
                {
                    best = meta.SlotIndex;
                    bestTime = t;
                }
            }

            return best;
        }

        /// <summary>有没有 Demo 存档（主菜单"继续"不可用时据此说明原因）。</summary>
        public static bool AnyDemoSave() => AnyDemoSave(GetAllSlotMetadata());

        public static bool AnyDemoSave(CampaignSlotMetadata[] metas) => metas.Any(m => m.State == CampaignSlotState.DemoSave);

        /// <summary>坏档时的显式恢复动作（玩家点"读取备份"）：备份必须可读取（Ready）才执行；主档存在时先把它
        /// 另存为 <c>*.keep-corrupt-*</c>（永不删除存档），再把备份拷回主档位置。失败返回 false，不抛异常。</summary>
        public static bool RestoreFromBak(int slotIndex) => RestoreFromBak(slotIndex, out _);

        public static bool RestoreFromBak(int slotIndex, out string preservedPath)
        {
            preservedPath = null;
            string bakPath = BakPath(slotIndex);
            FileProbe bak = Probe(bakPath);
            if (bak.Kind != CampaignSlotState.Ready)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 槽位 {slotIndex} 没有可读取的备份（{bak.Kind}/{bak.Reason}），恢复失败。");
                return false;
            }

            string mainPath = SlotPath(slotIndex);
            LoadFailures.Remove(mainPath);
            KnownGoodMains.Remove(mainPath);
            try
            {
                if (File.Exists(mainPath))
                {
                    preservedPath = KeepPath(slotIndex, "corrupt");
                    File.Move(mainPath, preservedPath);
                }
                File.Copy(bakPath, mainPath, overwrite: false);
                return true;
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 槽位 {slotIndex} 从备份恢复失败：{e.Message}");
                return false;
            }
        }

        // ── 文件探测 ─────────────────────────────────────────────────────

        private sealed class FileProbe
        {
            public CampaignSlotState Kind;
            public SaveFailureReason Reason;
            public string Message;
            public int SchemaVersion = -1;
            public int MissingFrom;
            public string WrittenAtUtc;
            public CampaignSaveEnvelope Envelope;
            public CampaignSaveCard Card;
        }

        /// <summary>读头部并分类（不反序列化正文）：Empty / Ready（含可逐级升级的旧正式版）/ DemoSave / Incompatible / Corrupt。</summary>
        private static FileProbe Probe(string path)
        {
            var p = new FileProbe();
            if (!File.Exists(path))
            {
                p.Kind = CampaignSlotState.Empty;
                p.Message = "存档不存在。";
                return p;
            }

            string fileJson;
            try
            {
                fileJson = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception e)
            {
                return Bad(p, SaveFailureReason.ReadFailed, $"读取失败：{e.Message}");
            }

            if (string.IsNullOrWhiteSpace(fileJson))
            {
                return Bad(p, SaveFailureReason.EmptyFile, "存档为空文件。");
            }

            CampaignSaveEnvelope env;
            try
            {
                env = JsonUtility.FromJson<CampaignSaveEnvelope>(fileJson);
            }
            catch (Exception e)
            {
                return Bad(p, SaveFailureReason.Truncated, $"JSON 截断或损坏：{e.Message}");
            }

            if (env == null || string.IsNullOrEmpty(env.PayloadJson))
            {
                return Bad(p, SaveFailureReason.Truncated, "存档头部缺失或 payload 为空。");
            }

            p.SchemaVersion = env.SchemaVersion;
            p.WrittenAtUtc = env.WrittenAtUtc;
            if (env.SchemaVersion < 1)
            {
                return Bad(p, SaveFailureReason.Payload, $"存档格式版本 {env.SchemaVersion} 无效。");
            }

            if (env.SchemaVersion <= CampaignSaveMigrations.LastDemoSchemaVersion)
            {
                p.Kind = CampaignSlotState.DemoSave;
                p.Reason = SaveFailureReason.DemoSave;
                p.Message = $"Demo（0.1）存档（格式 v{env.SchemaVersion}），正式版不迁移，文件已保留。";
                return p;
            }

            SaveMigrationChain chain = CampaignSaveMigrations.Active;
            if (env.SchemaVersion > chain.TargetVersion)
            {
                p.Kind = CampaignSlotState.Incompatible;
                p.Reason = SaveFailureReason.Newer;
                p.Message = $"存档 schemaVersion={env.SchemaVersion} 比当前客户端 {chain.TargetVersion} 更新，拒绝读取，文件已保留。";
                return p;
            }

            string checksum = ComputeChecksum(env.PayloadJson, env.CardJson);
            if (!string.Equals(checksum, env.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                return Bad(p, SaveFailureReason.Checksum, "校验和不匹配，存档可能已损坏。");
            }

            if (!chain.CanUpgradeFrom(env.SchemaVersion, out int missingFrom))
            {
                p.Kind = CampaignSlotState.Incompatible;
                p.Reason = SaveFailureReason.MigrationMissing;
                p.MissingFrom = missingFrom;
                p.Message = $"缺少升级器 v{missingFrom}→v{missingFrom + 1}，无法读取 v{env.SchemaVersion} 存档。";
                return p;
            }

            CampaignSaveCard card = null;
            try
            {
                card = string.IsNullOrEmpty(env.CardJson) ? null : JsonUtility.FromJson<CampaignSaveCard>(env.CardJson);
            }
            catch
            {
                card = null;
            }
            if (card == null)
            {
                return Bad(p, SaveFailureReason.Payload, "存档卡片头缺失或无法解析。");
            }

            p.Kind = CampaignSlotState.Ready;
            p.Envelope = env;
            p.Card = card;
            return p;
        }

        private static FileProbe Bad(FileProbe p, SaveFailureReason reason, string message)
        {
            p.Kind = CampaignSlotState.Corrupt;
            p.Reason = reason;
            p.Message = message;
            return p;
        }

        private static void TryDeleteQuiet(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 清理失败不影响主流程结果，忽略。
            }
        }

        /// <summary>校验和 = SHA256(正文 + \0 + 卡片)。自检构造"真实格式"的测试存档时也用它。</summary>
        public static string ComputeChecksum(string payload, string cardJson)
        {
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes((payload ?? string.Empty) + "\0" + (cardJson ?? string.Empty)));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }
    }
}
