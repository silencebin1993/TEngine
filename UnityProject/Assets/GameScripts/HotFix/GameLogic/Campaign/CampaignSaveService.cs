using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    }

    [Serializable]
    public readonly struct SaveResult
    {
        public readonly SaveOutcome Outcome;
        public readonly string Message;

        public SaveResult(SaveOutcome outcome, string message)
        {
            Outcome = outcome;
            Message = message;
        }

        public bool Success => Outcome == SaveOutcome.Success;

        public static readonly SaveResult Ok = new SaveResult(SaveOutcome.Success, null);
    }

    public enum LoadOutcome
    {
        Success = 0,
        Empty = 1,
        Corrupt = 2,
        Incompatible = 3,
    }

    public readonly struct LoadResult
    {
        public readonly LoadOutcome Outcome;
        public readonly CampaignState State;
        public readonly string Message;

        public LoadResult(LoadOutcome outcome, CampaignState state, string message)
        {
            Outcome = outcome;
            State = state;
            Message = message;
        }

        public bool Success => Outcome == LoadOutcome.Success;
    }

    /// <summary>ER1-SAVE-01：存档卡四态元数据（STORY-EXECUTION-CARDS.md #ER1-SAVE-01 第一条）。</summary>
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
        public string ErrorMessage;
        public bool HasBackup;

        /// <summary>ER2-BOOT-01：ERD-UI-001 要求存档卡显示"最后区域"。当前唯一可达的正式区域是细胞阶段
        /// 占位（归还谷地正式场景是 ER2-SCENE-01 的范围），新战役未进入过任何区域时为 null/空。</summary>
        public string LastRegionId;
    }

    /// <summary>ERD-SAV-001 存档格式外层头部："schemaVersion、contentVersion、checksum、writtenAtUtc"，
    /// payload 整体作为字符串内嵌，使得头部校验不必先完整反序列化正文。</summary>
    [Serializable]
    internal sealed class CampaignSaveEnvelope
    {
        public int SchemaVersion;
        public int ContentVersion;
        public string Checksum;
        public string WrittenAtUtc;
        public string PayloadJson;
    }

    /// <summary>
    /// ER1-SAVE-01：CampaignState 的磁盘 IO 引擎。
    ///
    /// 落盘流程（ERD-SAV-001）：写 temp → flush → 校验/替换主档（<see cref="File.Replace"/> 一次系统调用
    /// 完成"用 temp 覆盖主档、旧主档转存为 bak"，天然原子，避免手写三段式留窗口）→ 主档不存在时退化为
    /// <see cref="File.Move"/>。任何一步失败都不改动已存在的主档/备份，只返回失败结果，不抛异常。
    ///
    /// 读取流程（ERD-SAV-003 的头部校验部分）：解析头部 → schemaVersion 比当前更新直接拒绝（Incompatible，
    /// 保留原文件）→ 重算 checksum 比对 payload → 反序列化正文。schemaVersion 比当前旧时目前只有 v1 一个
    /// 版本，迁移退化为"用当前 CampaignState 类型反序列化"，缺的字段由 JsonUtility 自动补 default，
    /// 并记一条日志；后续版本引入真正字段迁移时在 <see cref="MigratePayload"/> 里扩展。
    /// </summary>
    public static class CampaignSaveService
    {
        public const int CurrentSchemaVersion = 1;
        public const int CurrentContentVersion = 1;

        /// <summary>Demo 骨架先给 3 个固定槽位；不是最终产品的存档数量上限，只是本 Story 的最小可用范围。</summary>
        public const int SlotCount = 3;

        /// <summary>自检专用：把存档目录临时改到别处（用完置回 null）。负向旅程要制造损坏存档，绝不能碰玩家真实槽位。</summary>
        public static string SaveDirectoryOverrideForTests { get; set; }

        public static string SaveDirectory => SaveDirectoryOverrideForTests ?? Path.Combine(Application.persistentDataPath, "Campaigns");

        public static string SlotPath(int slotIndex) =>
            Path.Combine(SaveDirectory, $"campaign_slot{slotIndex}.json");

        public static string TempPath(int slotIndex) => SlotPath(slotIndex) + ".tmp";
        public static string BakPath(int slotIndex) => SlotPath(slotIndex) + ".bak";

        /// <summary>整份覆盖写入（不是追加）。落盘前调用 <see cref="CampaignState.NormalizeForSave"/>
        /// 保证稳定 ID 排序。永不抛出；失败原因见返回值。</summary>
        public static SaveResult Save(int slotIndex, CampaignState state, SaveReason reason)
        {
            if (state == null)
            {
                return new SaveResult(SaveOutcome.NoActiveCampaign, "CampaignState 为空，未写入。");
            }

            state.LastSaveReason = reason;
            state.SchemaVersion = CurrentSchemaVersion;
            state.ContentVersion = CurrentContentVersion;
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
            try
            {
                payloadJson = JsonUtility.ToJson(state);
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] CampaignState 序列化失败，槽位 {slotIndex} 未保存：{e.Message}");
                return new SaveResult(SaveOutcome.TempWriteFailed, e.Message);
            }

            var envelope = new CampaignSaveEnvelope
            {
                SchemaVersion = CurrentSchemaVersion,
                ContentVersion = CurrentContentVersion,
                Checksum = ComputeChecksum(payloadJson),
                WrittenAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                PayloadJson = payloadJson,
            };
            string fileJson = JsonUtility.ToJson(envelope);

            try
            {
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    writer.Write(fileJson);
                    writer.Flush();
                    fs.Flush(true);
                }
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 写 temp 失败，槽位 {slotIndex} 主档未改动：{e.Message}");
                TryDeleteQuiet(tempPath);
                return new SaveResult(SaveOutcome.TempWriteFailed, e.Message);
            }

            try
            {
                if (File.Exists(mainPath))
                {
                    // 单次系统调用：temp 覆盖主档，旧主档原子转存为 bak。半途失败时 Windows/NTFS
                    // 保证不产生"主档已被截断但未完成替换"的中间态。
                    File.Replace(tempPath, mainPath, bakPath, ignoreMetadataErrors: true);
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
                return new SaveResult(SaveOutcome.ReplaceFailed, e.Message);
            }

            return SaveResult.Ok;
        }

        /// <summary>读取存档。永不抛出；文件缺失/损坏/版本不兼容均返回带 <see cref="LoadOutcome"/> 的结果，
        /// 不偷偷新建战役、不吞掉错误。</summary>
        public static LoadResult Load(int slotIndex)
        {
            string mainPath = SlotPath(slotIndex);
            if (!TryReadEnvelope(mainPath, out CampaignSaveEnvelope envelope, out LoadOutcome failOutcome,
                    out string failMessage))
            {
                return new LoadResult(failOutcome, null, failMessage);
            }

            CampaignState state;
            try
            {
                state = JsonUtility.FromJson<CampaignState>(envelope.PayloadJson);
            }
            catch (Exception e)
            {
                return new LoadResult(LoadOutcome.Corrupt, null, $"正文解析失败：{e.Message}");
            }

            if (state == null)
            {
                return new LoadResult(LoadOutcome.Corrupt, null, "正文反序列化为 null。");
            }

            if (envelope.SchemaVersion < CurrentSchemaVersion)
            {
                TEngine.Log.Warning(
                    $"[CampaignSaveService] 槽位 {slotIndex} 存档 schemaVersion={envelope.SchemaVersion} 低于当前 " +
                    $"{CurrentSchemaVersion}，按稳定默认值补齐缺字段继续读取（JsonUtility 对缺失字段自动填 default）。");
                state = MigratePayload(envelope.SchemaVersion, state);
            }

            state.SchemaVersion = CurrentSchemaVersion;
            return new LoadResult(LoadOutcome.Success, state, null);
        }

        /// <summary>查询单个槽位的四态元数据，供主菜单存档卡渲染。永不抛出。</summary>
        public static CampaignSlotMetadata GetSlotMetadata(int slotIndex)
        {
            var meta = new CampaignSlotMetadata { SlotIndex = slotIndex, HasBackup = File.Exists(BakPath(slotIndex)) };
            string mainPath = SlotPath(slotIndex);

            if (!TryReadEnvelope(mainPath, out CampaignSaveEnvelope envelope, out LoadOutcome failOutcome,
                    out string failMessage))
            {
                meta.State = failOutcome == LoadOutcome.Empty ? CampaignSlotState.Empty
                    : failOutcome == LoadOutcome.Incompatible ? CampaignSlotState.Incompatible
                    : CampaignSlotState.Corrupt;
                meta.ErrorMessage = failMessage;
                if (failOutcome == LoadOutcome.Incompatible)
                {
                    meta.SchemaVersion = TryPeekSchemaVersion(mainPath);
                }
                return meta;
            }

            CampaignState state;
            try
            {
                state = JsonUtility.FromJson<CampaignState>(envelope.PayloadJson);
            }
            catch (Exception e)
            {
                meta.State = CampaignSlotState.Corrupt;
                meta.ErrorMessage = $"正文解析失败：{e.Message}";
                return meta;
            }

            if (state == null)
            {
                meta.State = CampaignSlotState.Corrupt;
                meta.ErrorMessage = "正文反序列化为 null。";
                return meta;
            }

            meta.State = CampaignSlotState.Ready;
            meta.CampaignId = state.CampaignId;
            meta.DifficultyId = state.DifficultyId;
            meta.CampaignPhase = state.CampaignPhase;
            meta.PlaySeconds = state.PlaySeconds;
            meta.WrittenAtUtc = envelope.WrittenAtUtc;
            meta.SchemaVersion = envelope.SchemaVersion;
            meta.ContentVersion = envelope.ContentVersion;
            meta.LastRegionId = state.CurrentRegionId;
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
        /// 没有任何 Ready 槽位时返回 -1，不静默新建战役。</summary>
        public static int ResolveContinueSlot()
        {
            int best = -1;
            DateTime bestTime = DateTime.MinValue;
            for (int i = 0; i < SlotCount; i++)
            {
                CampaignSlotMetadata meta = GetSlotMetadata(i);
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
                    best = i;
                    bestTime = t;
                }
            }

            return best;
        }

        /// <summary>坏档/版本不兼容时的显式恢复动作：把 bak 拷回主档位置。没有 bak 时返回 false，不抛异常。</summary>
        public static bool RestoreFromBak(int slotIndex)
        {
            string bakPath = BakPath(slotIndex);
            if (!File.Exists(bakPath))
            {
                TEngine.Log.Warning($"[CampaignSaveService] 槽位 {slotIndex} 没有可用备份，恢复失败。");
                return false;
            }

            try
            {
                File.Copy(bakPath, SlotPath(slotIndex), overwrite: true);
                return true;
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 槽位 {slotIndex} 从备份恢复失败：{e.Message}");
                return false;
            }
        }

        private static bool TryReadEnvelope(string mainPath, out CampaignSaveEnvelope envelope,
            out LoadOutcome failOutcome, out string failMessage)
        {
            envelope = null;
            failOutcome = LoadOutcome.Success;
            failMessage = null;

            if (!File.Exists(mainPath))
            {
                failOutcome = LoadOutcome.Empty;
                failMessage = "存档不存在。";
                return false;
            }

            string fileJson;
            try
            {
                fileJson = File.ReadAllText(mainPath, Encoding.UTF8);
            }
            catch (Exception e)
            {
                failOutcome = LoadOutcome.Corrupt;
                failMessage = $"读取失败：{e.Message}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(fileJson))
            {
                failOutcome = LoadOutcome.Corrupt;
                failMessage = "存档为空文件。";
                return false;
            }

            CampaignSaveEnvelope parsed;
            try
            {
                parsed = JsonUtility.FromJson<CampaignSaveEnvelope>(fileJson);
            }
            catch (Exception e)
            {
                failOutcome = LoadOutcome.Corrupt;
                failMessage = $"JSON 截断或损坏：{e.Message}";
                return false;
            }

            if (parsed == null || string.IsNullOrEmpty(parsed.PayloadJson))
            {
                failOutcome = LoadOutcome.Corrupt;
                failMessage = "存档头部缺失或 payload 为空。";
                return false;
            }

            if (parsed.SchemaVersion > CurrentSchemaVersion)
            {
                failOutcome = LoadOutcome.Incompatible;
                failMessage =
                    $"存档 schemaVersion={parsed.SchemaVersion} 比当前客户端 {CurrentSchemaVersion} 更新，拒绝读取，文件已保留。";
                return false;
            }

            string checksum = ComputeChecksum(parsed.PayloadJson);
            if (!string.Equals(checksum, parsed.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                failOutcome = LoadOutcome.Corrupt;
                failMessage = "校验和不匹配，存档可能已损坏。";
                return false;
            }

            envelope = parsed;
            return true;
        }

        /// <summary>v1 是当前唯一版本，暂无需要真正迁移的旧字段；预留扩展点，后续新增 schema 时在此
        /// 按 <paramref name="fromVersion"/> 补齐/改写字段，而不是散落在 Load 各处。</summary>
        private static CampaignState MigratePayload(int fromVersion, CampaignState state)
        {
            return state;
        }

        private static int TryPeekSchemaVersion(string mainPath)
        {
            try
            {
                string json = File.ReadAllText(mainPath, Encoding.UTF8);
                var envelope = JsonUtility.FromJson<CampaignSaveEnvelope>(json);
                return envelope?.SchemaVersion ?? -1;
            }
            catch
            {
                return -1;
            }
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

        private static string ComputeChecksum(string payload)
        {
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(payload ?? string.Empty));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }
    }
}
