using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Diagnostics;
using UnityEngine;
using GameLogic.Localization;

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
        /// <summary>FG6-DEF-08（FGR-DEF-053）：归还核心已被摧毁（战役失败）——不再写盘，保住最近的安全存档供失败页读取。</summary>
        CampaignLost = 6,
        /// <summary>FG13-END-06（卡片负向“结算时存档”）：结局结算画面开着——结局已在结算前落盘，结算期间任何存档（自动档、保存并返回主菜单）都被拒绝。</summary>
        SettlementOpen = 7,
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
        /// <summary>FG14-SBX-01（FGR-SBX-003“这些数据显示在主菜单的存档卡片上”）：后日谈坚守天数与达到的最高清洗等级（不是后日谈 = 0）。</summary>
        public int PostgameDays;
        public int PostgameTopLevel;
        /// <summary>FG14-SBX-02：沙盒设置代码（<see cref="SandboxService.Code"/>；剧情档为空），存档卡显示“沙盒”及其设置。</summary>
        public string SandboxCode;
        /// <summary>FG15-SYS-01（DEBT-FG0SAVE01-01）：存档时截的缩略图（JPG 字节，嵌在存档头部；直接调 <see cref="CampaignSaveService.Save"/> 的自检档没有 = null）。</summary>
        public byte[] ThumbnailJpg;
        /// <summary>FG15-SYS-01（FG-GAP-019）：真实游玩时长（秒；0 = 旧档未记录）。<see cref="PlaySeconds"/> 是游戏时间。</summary>
        public double RealPlaySeconds;
        /// <summary>FG15-SYS-01（FGR-SYS-001）：这一份是战役槽本身（手动 / 最新）、轮换自动存档的第几份（1～3），还是快速存档。</summary>
        public SaveSlotKind Kind;
        /// <summary>所属战役槽（手动槽本身 = <see cref="SlotIndex"/>）。</summary>
        public int HomeSlot;
        /// <summary>列表只读了头部（卡片与长度校验通过，正文校验和留到读档）。false = 本次整份校验过。</summary>
        public bool HeaderOnly;
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
        /// <summary>FG15-SYS-01（DEBT-FG0SAVE01-08）：卡片 + 缩略图的校验和与正文长度——列表只读到正文之前的头部，用它们确认卡片完好、文件没被截断；
        /// 正文的完整校验（<see cref="Checksum"/>）留到读档。旧文件没有这两项 → 列表照旧整份校验。字段顺序 = 文件里的顺序，正文必须最后。</summary>
        public string CardChecksum;
        public long PayloadLength;
        public string CardJson;
        /// <summary>FG15-SYS-01（FGR-SYS-007 / DEBT-FG0SAVE01-01）：存档时的缩略图（JPG，Base64；没有 = 空）。</summary>
        public string ThumbnailJpg;
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
        /// <summary>FG14-SBX-01（FGR-SBX-003“这些数据显示在主菜单的存档卡片上”）：后日谈坚守天数与达到的最高清洗等级（不是后日谈 = 0）。</summary>
        public int PostgameDays;
        public int PostgameTopLevel;
        /// <summary>FG14-SBX-02：沙盒设置代码（<see cref="SandboxService.Code"/>；剧情档为空），存档卡显示“沙盒”及其设置。</summary>
        public string SandboxCode;
        /// <summary>FG15-SYS-01（FG-GAP-019）：真实游玩时长（秒）；旧卡缺省 0 = 未记录。</summary>
        public double RealPlaySeconds;
    }

    /// <summary>FG15-SYS-01（FGR-SYS-001）：一份存档文件属于哪一类。</summary>
    public enum SaveSlotKind
    {
        /// <summary>战役槽（手动存档、数量不限）：每次存档都写它，读“继续”读的就是它。</summary>
        Manual = 0,
        /// <summary>该战役槽的 3 份轮换自动存档之一（写满后覆盖其中最旧的一份，只覆盖本槽自己的旧代）。</summary>
        Auto = 1,
        /// <summary>该战役槽的快速存档（1 份）。</summary>
        Quick = 2,
    }

    /// <summary>FG15-SYS-01：一次存档顺带写哪种副本。</summary>
    public enum SaveCopyKind
    {
        None = 0,
        /// <summary>轮换自动存档（3 份轮换）。</summary>
        Auto = 1,
        /// <summary>快速存档（1 份）。</summary>
        Quick = 2,
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
        public SimulatedSaveCrashException(SaveCrashPoint point) : base(GameText.Format("save.svc.the_simulation_process_was", point)) { }
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
        /// <summary>内容版本：1 = 0.2 起始；2 = EMG1-PRG-01 程序编译器（固件按声明编成程序树，FGR-EMG-030）。低于它编译保存的蓝图版本在电路编辑器里提示重新保存（<see cref="Blueprint.BlueprintEditorService.IsCompiledOnOldContent"/>）；读档的内容对账不按版本号做事，只把它记进通知。</summary>
        public const int CurrentContentVersion = 2;
        public const string ProductVersion = "0.2";

        /// <summary>FG15-SYS-01（FGR-SYS-001）：手动槽（战役槽）不限数量，主菜单每页显示这么多张卡（列表分页）。槽位号 0、1、2 始终列出（空槽可新建）。</summary>
        public const int SlotCount = 3;
        public const int PageSize = SlotCount;

        /// <summary>FG15-SYS-01：每个战役槽的轮换自动存档份数（FGR-SYS-001“自动存档 3 个轮换”）。</summary>
        public const int AutoRotationCount = 3;

        /// <summary>副本（轮换自动存档 / 快速存档）的槽位号从这里起：<c>CopyBase + 战役槽 × 4 + k</c>（k = 0～2 自动存档，3 = 快速存档）。手动槽号小于它。</summary>
        public const int CopyBase = 1000000;

        public static bool IsCopy(int slotIndex) => slotIndex >= CopyBase;

        /// <summary>副本所属的战役槽（手动槽本身原样返回）。</summary>
        public static int HomeOf(int slotIndex) => slotIndex >= CopyBase ? (slotIndex - CopyBase) / 4 : slotIndex;

        /// <summary>战役槽 <paramref name="home"/> 的第 k 份副本（0～2 轮换自动存档，3 快速存档）。</summary>
        public static int CopySlot(int home, int k) => CopyBase + home * 4 + k;

        public static int AutoSlot(int home, int k) => CopySlot(home, k);

        public static int QuickSlot(int home) => CopySlot(home, AutoRotationCount);

        public static SaveSlotKind KindOf(int slotIndex) =>
            slotIndex < CopyBase ? SaveSlotKind.Manual : (slotIndex - CopyBase) % 4 < AutoRotationCount ? SaveSlotKind.Auto : SaveSlotKind.Quick;

        /// <summary>轮换自动存档的序号（1～3；不是自动存档 = 0）。</summary>
        public static int AutoNumber(int slotIndex) => KindOf(slotIndex) == SaveSlotKind.Auto ? (slotIndex - CopyBase) % 4 + 1 : 0;

        /// <summary>自检专用：把存档目录临时改到别处（用完置回 null）。负向旅程要制造损坏存档，绝不能碰玩家真实槽位。</summary>
        public static string SaveDirectoryOverrideForTests { get; set; }

        /// <summary>自检专用：在存档流程里模拟进程被强制结束（FGT-SYS-001）。用完置回 None。</summary>
        public static SaveCrashPoint CrashPointForTests { get; set; }

        /// <summary>当前生效的存档格式版本（正式 = <see cref="CurrentSchemaVersion"/>；自检可换成假想的未来版本链）。</summary>
        public static int EffectiveSchemaVersion => CampaignSaveMigrations.Active.TargetVersion;

        private static string _persistentDir;

        /// <summary>存档目录（玩家配置目录下的 Campaigns，FGR-SYS-001）。第一次在主线程取值后缓存（后台写入线程只用准备阶段算好的路径）。</summary>
        public static string SaveDirectory => SaveDirectoryOverrideForTests ?? (_persistentDir ??= Path.Combine(Application.persistentDataPath, "Campaigns"));

        public static string SlotPath(int slotIndex) => SlotPathIn(SaveDirectory, slotIndex);

        /// <summary>手动槽 <c>campaign_slotN.json</c>；副本 <c>campaign_slotN.autoK.json</c>（K = 1～3）/ <c>campaign_slotN.quick.json</c>。</summary>
        public static string SlotPathIn(string dir, int slotIndex) => Path.Combine(dir, FileNameOf(slotIndex));

        public static string FileNameOf(int slotIndex)
        {
            if (slotIndex < CopyBase)
            {
                return $"campaign_slot{slotIndex}.json";
            }
            int home = HomeOf(slotIndex);
            int k = (slotIndex - CopyBase) % 4;
            return k < AutoRotationCount ? $"campaign_slot{home}.auto{k + 1}.json" : $"campaign_slot{home}.quick.json";
        }

        public static string TempPath(int slotIndex) => SlotPath(slotIndex) + ".tmp";
        public static string BakPath(int slotIndex) => SlotPath(slotIndex) + ".bak";

        /// <summary>被保留的旧文件：<c>campaign_slotN.json.keep-&lt;标签&gt;-&lt;UTC 时间&gt;</c>（副本的保留文件也记在所属战役槽名下）。</summary>
        public static string KeepPath(int slotIndex, string tag) => KeepPathIn(SaveDirectory, slotIndex, tag);

        private static string KeepPathIn(string dir, int slotIndex, string tag)
        {
            string basePath = SlotPathIn(dir, HomeOf(slotIndex)) + $".keep-{tag}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}";
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
                string prefix = Path.GetFileName(SlotPath(HomeOf(slotIndex))) + ".keep-";
                return Directory.GetFiles(SaveDirectory, prefix + "*").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static readonly Regex SlotFileRegex = new Regex(@"^campaign_slot(\d+)\.", RegexOptions.CultureInvariant);

        /// <summary>FG15-SYS-01：磁盘上出现过的全部战役槽号（主档、备份、副本、保留文件任一存在即算），升序。只列目录、不读文件。</summary>
        public static int[] ExistingManualSlots()
        {
            WaitForPendingWrites();
            var set = new SortedSet<int>();
            try
            {
                if (Directory.Exists(SaveDirectory))
                {
                    foreach (string f in Directory.GetFiles(SaveDirectory, "campaign_slot*"))
                    {
                        Match m = SlotFileRegex.Match(Path.GetFileName(f));
                        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0 && n < CopyBase)
                        {
                            set.Add(n);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 列存档目录失败：{e.Message}");
            }
            return set.ToArray();
        }

        /// <summary>新建战役用的空槽：磁盘上没有任何同族文件的最小槽号（手动槽不限，不再出现“三槽都满”）。</summary>
        public static int NextFreeSlot()
        {
            int[] used = ExistingManualSlots();
            int n = 0;
            foreach (int u in used)
            {
                if (u != n)
                {
                    break;
                }
                n++;
            }
            return n;
        }

        /// <summary>主菜单分页列出的战役槽：0～2 始终在，加磁盘上的全部槽，末尾补一个空槽（总能新建）。</summary>
        public static int[] ListedManualSlots()
        {
            var set = new SortedSet<int>(ExistingManualSlots());
            for (int i = 0; i < SlotCount; i++)
            {
                set.Add(i);
            }
            set.Add(NextFreeSlot());
            return set.ToArray();
        }

        public static int PageCount(int[] listed) => Math.Max(1, (listed.Length + PageSize - 1) / PageSize);

        /// <summary>第 <paramref name="page"/> 页（从 0 开始，越界夹到首尾）的槽号，最多 <see cref="PageSize"/> 个。</summary>
        public static int[] PageSlots(int[] listed, int page)
        {
            int p = Math.Max(0, Math.Min(page, PageCount(listed) - 1));
            return listed.Skip(p * PageSize).Take(PageSize).ToArray();
        }

        /// <summary>战役槽的副本（3 份轮换自动存档 + 快速存档）元数据，只读头部。</summary>
        public static CampaignSlotMetadata[] CopyMetadata(int home)
        {
            var list = new CampaignSlotMetadata[AutoRotationCount + 1];
            for (int k = 0; k <= AutoRotationCount; k++)
            {
                list[k] = GetSlotMetadata(CopySlot(home, k));
            }
            return list;
        }

        // ── FG15-SYS-01（FG-GAP-013）：玩家亲手做的存档管理（永不自动删除；删除只由这里、且 UI 先二次确认）────────

        /// <summary>删除一个战役槽：主档、备份、临时文件、轮换自动存档、快速存档；保留文件（keep-*）不动（另有“清理保留文件”）。返回删掉的文件数。</summary>
        public static int DeleteSlot(int slotIndex)
        {
            WaitForPendingWrites();
            int home = HomeOf(slotIndex);
            var files = new List<string> { SlotPath(home), BakPath(home), TempPath(home) };
            for (int k = 0; k <= AutoRotationCount; k++)
            {
                files.Add(SlotPath(CopySlot(home, k)));
                files.Add(SlotPath(CopySlot(home, k)) + ".tmp");
            }
            int n = 0;
            foreach (string f in files)
            {
                try
                {
                    if (File.Exists(f))
                    {
                        File.Delete(f);
                        n++;
                    }
                }
                catch (Exception e)
                {
                    TEngine.Log.Warning($"[CampaignSaveService] 删除 {Path.GetFileName(f)} 失败：{e.Message}");
                }
                ForgetStamps(f);
            }
            TEngine.Log.Info($"[CampaignSaveService] 玩家删除了槽位 {home}（{n} 个文件；保留文件未动）。");
            return n;
        }

        /// <summary>删除一份副本（某份轮换自动存档或快速存档）。</summary>
        public static bool DeleteCopy(int copySlot)
        {
            WaitForPendingWrites();
            if (!IsCopy(copySlot))
            {
                return false;
            }
            string path = SlotPath(copySlot);
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }
                File.Delete(path);
                ForgetStamps(path);
                return true;
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 删除 {Path.GetFileName(path)} 失败：{e.Message}");
                return false;
            }
        }

        /// <summary>清理一个战役槽的保留文件（keep-*）。返回删掉的个数。</summary>
        public static int DeletePreservedFiles(int slotIndex)
        {
            int n = 0;
            foreach (string f in PreservedFiles(slotIndex))
            {
                try
                {
                    File.Delete(f);
                    n++;
                }
                catch (Exception e)
                {
                    TEngine.Log.Warning($"[CampaignSaveService] 删除保留文件 {Path.GetFileName(f)} 失败：{e.Message}");
                }
            }
            return n;
        }

        /// <summary>“打开存档目录”的实际动作（自检替换成记录路径）。</summary>
        public static Action<string> OpenDirectoryHandler = dir => Application.OpenURL("file:///" + dir.Replace('\\', '/'));

        public static string OpenSaveDirectory()
        {
            string dir = SaveDirectory;
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 存档目录不可用：{e.Message}");
            }
            OpenDirectoryHandler?.Invoke(dir);
            return dir;
        }

        /// <summary>
        /// FG15-SYS-01：从副本（某份自动存档 / 快速存档）读回的战役要接着写回它的战役槽——第一次存档前，把战役槽里更新的那份主档另存为
        /// keep-superseded（永不自动删除：读了旧的自动存档，后来的进度也不会被两次轮换挤掉）。主档与副本是同一份时不另存。由 <see cref="CampaignSession.Set"/> 调用。
        /// </summary>
        public static string PreserveHomeBeforeBranch(int copySlot)
        {
            if (!IsCopy(copySlot))
            {
                return null;
            }
            WaitForPendingWrites();
            int home = HomeOf(copySlot);
            string main = SlotPath(home);
            try
            {
                if (!File.Exists(main))
                {
                    return null;
                }
                FileProbe m = Probe(main, headerOnly: true);
                FileProbe c = Probe(SlotPath(copySlot), headerOnly: true);
                if (m.Envelope != null && c.Envelope != null && m.Envelope.Checksum == c.Envelope.Checksum)
                {
                    return null;
                }
                string keep = KeepPath(home, "superseded");
                File.Copy(main, keep, overwrite: false);
                return keep;
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 读副本前另存战役槽 {home} 主档失败：{e.Message}");
                return null;
            }
        }

        /// <summary>FG15-SYS-01（FG-GAP-019）：真实游玩时长——世界运行且未暂停时按真实时间累计（倍速不影响）。由 GameRoot.OnUpdate 调用，O(1)。</summary>
        public static void TickRealPlay(float realDt, bool worldRunning, bool paused)
        {
            CampaignState s = CampaignSession.Current;
            if (s == null || !worldRunning || paused || !(realDt > 0f))
            {
                return;
            }
            s.RealPlaySeconds += Math.Min(realDt, 1f); // 单帧卡顿（加载、断点）最多记 1 秒
        }

        // ── 写 ───────────────────────────────────────────────────────────

        /// <summary>整份覆盖写入（不是追加），同步完成。落盘前调用 <see cref="CampaignState.NormalizeForSave"/>
        /// 保证稳定 ID 排序。永不抛出（自检注入的模拟崩溃除外）；失败原因见返回值。排在前面的后台写入先落盘（同一文件不并发写）。</summary>
        public static SaveResult Save(int slotIndex, CampaignState state, SaveReason reason) =>
            Save(slotIndex, state, reason, null, SaveCopyKind.None);

        /// <summary>FG15-SYS-01：同步存档，可带缩略图与“顺带写一份轮换自动存档 / 快速存档”。</summary>
        public static SaveResult Save(int slotIndex, CampaignState state, SaveReason reason, SaveThumbnail.Shot thumb, SaveCopyKind copy)
        {
            var sw = Stopwatch.StartNew();
            WaitForPendingWrites();
            double waitMs = sw.Elapsed.TotalMilliseconds;
            if (!TryPrepare(slotIndex, state, reason, thumb, copy, out PreparedSave p, out SaveResult fail))
            {
                return fail;
            }
            double prepMs = sw.Elapsed.TotalMilliseconds - waitMs;
            SaveResult r = Commit(p);
            LastSyncSaveBreakdown = $"wait {waitMs:F1} / prepare {prepMs:F1} ({LastPrepareBreakdown}) / commit {sw.Elapsed.TotalMilliseconds - waitMs - prepMs:F1} ms ({LastCommitBreakdown})"; // 只给自检与性能证据读
            return r;
        }

        /// <summary>最近一次同步存档 / 落盘各段耗时（性能证据读）。</summary>
        public static string LastSyncSaveBreakdown { get; private set; }
        public static string LastCommitBreakdown { get; private set; }
        private const int ChecksumChunkChars = 16384;

        private static void HashUtf8(ref BinGames.Sim.BurstSha256.State sha, Encoder enc, string s, char[] chars, byte[] bytes, bool flushAtEnd)
        {
            int at = 0;
            do
            {
                int n = Math.Min(chars.Length, s.Length - at);
                s.CopyTo(at, chars, 0, n);
                at += n;
                int len = enc.GetBytes(chars, 0, n, bytes, 0, flushAtEnd && at >= s.Length);
                BinGames.Sim.BurstSha256.Update(ref sha, bytes, 0, len);
            }
            while (at < s.Length);
        }

        /// <summary>最近一次读档里“探测 + 整份校验”的耗时（性能证据读）。</summary>
        public static double LastLoadProbeMs { get; private set; }

        /// <summary>FG15-SYS-02：最近一次整份探测里校验和那一段的耗时（性能证据分项）。</summary>
        public static double LastLoadChecksumMs { get; private set; }

        /// <summary>
        /// FG15-SYS-01（FGR-SYS-002“主线程阻塞 ≤ 100 毫秒，其余在后台完成”）：主线程只做快照（区块差异、稳定排序、JSON 序列化），
        /// 校验和、缩略图编码、写临时文件、另存保留、原子替换、轮换副本都在后台线程完成。返回值只反映主线程那一段（被拒 / 序列化失败）；
        /// 写盘结果在 <see cref="PumpBackgroundResults"/>（每帧）里发失败提示。同一槽位排队中、还没开始写的旧请求被新快照取代（新快照含旧的全部进度）；
        /// 读档、列表、恢复备份、删除、同步写入都会先等后台写完（读写互斥）。
        /// </summary>
        public static SaveResult SaveInBackground(int slotIndex, CampaignState state, SaveReason reason, SaveThumbnail.Shot thumb, SaveCopyKind copy) =>
            SaveInBackground(slotIndex, state, reason, thumb, copy, null, null);

        /// <summary>FG15-SYS-02（DEBT-FG15SYS01-05）：出发 / 到达回滚基线也走后台——主线程只拍快照；<paramref name="copyAfterTo"/> 在写盘线程里紧接着复制基线，
        /// <paramref name="onWritten"/> 在主线程收到写盘结果（失败带内存快照）。</summary>
        public static SaveResult SaveInBackground(int slotIndex, CampaignState state, SaveReason reason, SaveThumbnail.Shot thumb, SaveCopyKind copy,
            string copyAfterTo, Action<SaveResult, SaveSnapshot> onWritten)
        {
            var sw = Stopwatch.StartNew();
            if (!TryPrepare(slotIndex, state, reason, thumb, copy, out PreparedSave p, out SaveResult fail, background: true))
            {
                return fail;
            }
            p.CopyAfterTo = copyAfterTo;
            p.OnWritten = onWritten;
            Enqueue(p);
            LastPrepareMs = sw.Elapsed.TotalMilliseconds;
            return new SaveResult(SaveOutcome.Success, null);
        }

        /// <summary>最近一次存档主线程快照各段耗时（FGT-SYS-003 自检与性能证据读）。</summary>
        public static string LastPrepareBreakdown { get; private set; }

        /// <summary>最近一次后台存档在主线程上花的毫秒数（FGT-SYS-003 自检读）。</summary>
        public static double LastPrepareMs { get; private set; }

        /// <summary>最近一次后台写盘的结果（主线程 <see cref="PumpBackgroundResults"/> 取走后写入）。</summary>
        public static SaveResult LastBackgroundResult { get; private set; } = SaveResult.Ok;
        public static double LastBackgroundCommitMs { get; private set; }
        public static int LastBackgroundThreadId { get; private set; }
        public static int BackgroundCommits { get; private set; }
        public static int BackgroundFailures { get; private set; }
        /// <summary>排队时被同一槽位更新的快照取代的请求数（自检读）。</summary>
        public static int CoalescedWrites { get; private set; }

        /// <summary>自检专用：后台写入开始前先停这么多毫秒（制造“写入进行中”的窗口，验证排队与读档互斥）。</summary>
        public static int BackgroundDelayMsForTests { get; set; }

        private sealed class PreparedSave
        {
            public int Slot;
            public SaveReason Reason;
            public int Schema;
            public string Dir;
            public string CampaignId;
            public string PayloadJson;
            /// <summary>后台存档：正文整串在写盘线程里才拼（<see cref="CampaignPayloadJson.SerializeDeferred"/>），拼好写回 <see cref="PayloadJson"/>。</summary>
            public Func<string> PayloadLater;
            public string CardJson;
            public SaveThumbnail.Shot Thumb;
            public SaveCopyKind Copy;
            public SaveCrashPoint Crash;
            public SaveResult Result;
            public double CommitMs;
            public int ThreadId;
            /// <summary>FG15-SYS-02（DEBT-FG15SYS01-05）：写盘完成后主线程回调（<see cref="PumpBackgroundResults"/> 里调；失败时带内存快照，调用方据此中止出发 / 到达）。</summary>
            public Action<SaveResult, SaveSnapshot> OnWritten;
            /// <summary>写盘成功后在写盘线程里紧接着把主档复制到这里（到达前回滚基线；排在下一份写入之前）。</summary>
            public string CopyAfterTo;
            /// <summary>不是存档：排在已排队的写入之后删除这个文件（回滚基线用完）。</summary>
            public string DeletePath;
        }

        /// <summary>FG15-SYS-02：后台写盘失败时交给回调的内存快照（与写进文件的正文逐字相同），<see cref="CampaignRestoreOrchestrator.RestoreSnapshot"/> 读回。</summary>
        public sealed class SaveSnapshot
        {
            internal int Schema;
            internal string PayloadJson;
            internal string CardJson;
        }

        private static bool _quitHooked;

        private static bool TryPrepare(int slotIndex, CampaignState state, SaveReason reason, SaveThumbnail.Shot thumb, SaveCopyKind copy,
            out PreparedSave prepared, out SaveResult fail, bool background = false)
        {
            prepared = null;
            fail = default;
            if (state == null)
            {
                fail = new SaveResult(SaveOutcome.NoActiveCampaign, GameText.Get("save.svc.campaignstate_is_empty_nothing"));
                return false;
            }
            if (!_quitHooked)
            {
                _quitHooked = true;
                Application.quitting += WaitForPendingWrites; // 退出游戏前把后台写入落完（不丢最后一次存档）
            }

            int schema = EffectiveSchemaVersion;
            state.LastSaveReason = reason;
            state.SchemaVersion = schema;
            state.ContentVersion = CurrentContentVersion;
            // FG0-ARCH-05（FGR-GEN-060）：把各表面被修改过的区块写进区块差异（只存修改；未修改的读档时按种子重新生成）。
            var phase = Stopwatch.StartNew();
            int gc0 = GC.CollectionCount(0);
            WorldGen.WorldGenService.CaptureDiffs(state);
            double diffMs = phase.Elapsed.TotalMilliseconds;
            state.NormalizeForSave();
            double sortMs = phase.Elapsed.TotalMilliseconds - diffMs;

            string dir = SaveDirectory;
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 存档目录不可写，槽位 {slotIndex} 未保存：{e.Message}");
                fail = new SaveResult(SaveOutcome.DirectoryNotWritable, e.Message);
                return false;
            }

            string payloadJson;
            Func<string> payloadLater;
            string cardJson;
            try
            {
                double t0 = phase.Elapsed.TotalMilliseconds;
                // FG15-SYS-02（DEBT-FG15SYS01-01）：按域并行，与整份序列化逐字节相同；后台存档把拼整串留给写盘线程（修复轮：主线程窗口少分配一份正文）
                payloadLater = CampaignPayloadJson.SerializeDeferred(state, canVerify: !background);
                payloadJson = background ? null : payloadLater();
                double t1 = phase.Elapsed.TotalMilliseconds;
                cardJson = JsonUtility.ToJson(BuildCard(state));
                LastPrepareBreakdown = $"diffs {diffMs:F1} / sort {sortMs:F1} / payload json {t1 - t0:F1} / card {phase.Elapsed.TotalMilliseconds - t1:F1} ms / gc {GC.CollectionCount(0) - gc0} (heap {GC.GetTotalMemory(false) >> 20} MB)"; // 只给自检与性能证据读
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] CampaignState 序列化失败，槽位 {slotIndex} 未保存：{e.Message}");
                fail = new SaveResult(SaveOutcome.TempWriteFailed, e.Message);
                return false;
            }
            prepared = new PreparedSave
            {
                Slot = slotIndex,
                Reason = reason,
                Schema = schema,
                Dir = dir,
                CampaignId = state.CampaignId,
                PayloadJson = payloadJson,
                PayloadLater = payloadLater,
                CardJson = cardJson,
                Thumb = thumb,
                Copy = IsCopy(slotIndex) ? SaveCopyKind.None : copy,
                Crash = CrashPointForTests,
            };
            return true;
        }

        /// <summary>落盘（任意线程；只用 System.IO 与按 <see cref="PreparedSave.Dir"/> 算的路径，不碰 Unity 主线程 API）。</summary>
        private static SaveResult Commit(PreparedSave p)
        {
            int slotIndex = p.Slot;
            int schema = p.Schema;
            string mainPath = SlotPathIn(p.Dir, slotIndex);
            string tempPath = mainPath + ".tmp";
            string bakPath = mainPath + ".bak";

            var cw = Stopwatch.StartNew();
            double thumbMs = 0;
            byte[] bytes;
            try
            {
                string thumb = p.Thumb?.EncodeBase64();
                thumbMs = cw.Elapsed.TotalMilliseconds;
                var envelope = new CampaignSaveEnvelope
                {
                    SchemaVersion = schema,
                    ContentVersion = CurrentContentVersion,
                    Checksum = ComputeChecksum(p.PayloadJson, p.CardJson),
                    WrittenAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ProductVersion = ProductVersion,
                    CardChecksum = ComputeChecksum(p.CardJson, thumb),
                    PayloadLength = p.PayloadJson.Length,
                    CardJson = p.CardJson,
                    ThumbnailJpg = thumb,
                    PayloadJson = p.PayloadJson,
                };
                bytes = new UTF8Encoding(false).GetBytes(JsonUtility.ToJson(envelope));
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 存档封装失败，槽位 {slotIndex} 未保存：{e.Message}");
                return new SaveResult(SaveOutcome.TempWriteFailed, e.Message);
            }

            try
            {
                if (!SaveFileCommit.WriteTemp(tempPath, bytes, p.Crash == SaveCrashPoint.MidTempWrite))
                {
                    throw new SimulatedSaveCrashException(SaveCrashPoint.MidTempWrite);
                }
            }
            catch (Exception e) when (!(e is SimulatedSaveCrashException))
            {
                TEngine.Log.Warning($"[CampaignSaveService] 写 temp 失败，槽位 {slotIndex} 主档未改动：{e.Message}");
                TryDeleteQuiet(tempPath);
                return new SaveResult(SaveOutcome.TempWriteFailed, e.Message);
            }

            if (p.Crash == SaveCrashPoint.AfterTempWritten)
            {
                throw new SimulatedSaveCrashException(SaveCrashPoint.AfterTempWritten);
            }
            double tempMs = cw.Elapsed.TotalMilliseconds;

            // 永不自动删除存档（FGR-SYS-003）。File.Replace 会把旧主档转存为 bak、同时覆盖掉原来的 bak，所以写入前
            // 先把"会被覆盖、又不是本战役上一版"的文件另存：
            // - 新建战役（玩家已在确认框里同意占用此槽）：旧主档复制保留，旧备份整体挪成 keep-bak（主档存不存在都一样——
            //   主档丢失、只剩备份的槽位同样如此），替换时不再生成 bak：新战役从干净的槽位开始，bak 只会是本战役的上一版；
            //   FG15-SYS-01：这个槽位旧战役的轮换自动存档 / 快速存档也挪成 keep-auto / keep-quick（新战役的轮换不会覆盖别的战役的档）。
            // - 常规存档（自动存档等）：旧主档不是当前格式（Demo / 其他版本 / 头部读不出）→ 先另存一份；
            //   旧主档是当前格式但内容已坏（被外部改坏 / 截断）→ 整体挪成 keep-corrupt，完好的 bak 原样留着不被挤掉；
            //   现有 bak 不属于本战役（不含本战役 ID）→ 先另存为 keep-bak。
            bool newCampaign = p.Reason == SaveReason.NewCampaign;
            string preserved = null;
            try
            {
                if (newCampaign)
                {
                    if (File.Exists(mainPath))
                    {
                        preserved = KeepPathIn(p.Dir, slotIndex, PreserveTagFor(mainPath, schema) ?? "replaced");
                        File.Copy(mainPath, preserved, overwrite: false);
                    }
                    if (File.Exists(bakPath))
                    {
                        File.Move(bakPath, KeepPathIn(p.Dir, slotIndex, "bak"));
                    }
                    if (!IsCopy(slotIndex))
                    {
                        for (int k = 0; k <= AutoRotationCount; k++)
                        {
                            string copyPath = SlotPathIn(p.Dir, CopySlot(slotIndex, k));
                            if (File.Exists(copyPath))
                            {
                                File.Move(copyPath, KeepPathIn(p.Dir, slotIndex, k < AutoRotationCount ? "auto" : "quick"));
                            }
                        }
                    }
                }
                else
                {
                    if (File.Exists(mainPath))
                    {
                        string tag = PreserveTagFor(mainPath, schema);
                        if (tag != null)
                        {
                            preserved = KeepPathIn(p.Dir, slotIndex, tag);
                            File.Copy(mainPath, preserved, overwrite: false);
                        }
                        else if (File.Exists(bakPath) && !IsKnownGoodMain(mainPath) && Probe(mainPath).Kind != CampaignSlotState.Ready)
                        {
                            preserved = KeepPathIn(p.Dir, slotIndex, "corrupt");
                            File.Move(mainPath, preserved);
                        }
                    }
                    if (File.Exists(mainPath) && File.Exists(bakPath) && !BakBelongsTo(bakPath, p.CampaignId))
                    {
                        File.Copy(bakPath, KeepPathIn(p.Dir, slotIndex, "bak"), overwrite: false);
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
                // 单次系统调用：temp 覆盖主档；常规存档时旧主档原子转存为 bak，新建战役时旧主档已另存、不生成 bak。
                SaveFileCommit.Replace(tempPath, mainPath, newCampaign ? null : bakPath);
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 替换主档失败，槽位 {slotIndex} 保留旧档：{e.Message}");
                TryDeleteQuiet(tempPath);
                return new SaveResult(SaveOutcome.ReplaceFailed, e.Message, preserved);
            }

            double replaceMs = cw.Elapsed.TotalMilliseconds;
            MarkKnownGoodMain(mainPath);
            if (p.Copy != SaveCopyKind.None)
            {
                WriteCopy(p, bytes);
            }
            LastCommitBreakdown = $"thumb {thumbMs:F1} / envelope+write temp {tempMs - thumbMs:F1} / preserve+replace {replaceMs - tempMs:F1} / copy {cw.Elapsed.TotalMilliseconds - replaceMs:F1}";
            return new SaveResult(SaveOutcome.Success, null, preserved);
        }

        /// <summary>
        /// FG15-SYS-01（FGR-SYS-001）：顺带写一份轮换自动存档（3 份里缺的那份，都在时覆盖本槽自己最旧的一份）或快速存档（1 份）。
        /// 只覆盖本槽自己的旧代：要写的位置上是别的战役的档（不含本战役 ID）→ 先挪成 keep 文件。写副本失败不影响主档（已落盘），只记日志。
        /// </summary>
        private static void WriteCopy(PreparedSave p, byte[] bytes)
        {
            int home = p.Slot;
            int target = p.Copy == SaveCopyKind.Quick ? QuickSlot(home) : OldestAutoSlot(p.Dir, home);
            string path = SlotPathIn(p.Dir, target);
            try
            {
                if (File.Exists(path) && !BakBelongsTo(path, p.CampaignId))
                {
                    File.Move(path, KeepPathIn(p.Dir, home, p.Copy == SaveCopyKind.Quick ? "quick" : "auto"));
                }
                SaveFileCommit.WriteAtomic(path, bytes, keepBackup: false);
                LastCopySlot = target;
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 槽位 {home} 的副本（{p.Copy}）写入失败（主档已保存）：{e.Message}");
                TryDeleteQuiet(path + ".tmp");
            }
        }

        /// <summary>最近一次写的副本槽位号（自检读）。</summary>
        public static int LastCopySlot { get; private set; } = -1;

        /// <summary>3 份轮换自动存档里该写的那份：先补缺，都在时取存档时间（头部）最早的一份。</summary>
        private static int OldestAutoSlot(string dir, int home)
        {
            int best = -1;
            DateTime bestTime = DateTime.MaxValue;
            for (int k = 0; k < AutoRotationCount; k++)
            {
                int slot = CopySlot(home, k);
                string path = SlotPathIn(dir, slot);
                if (!File.Exists(path))
                {
                    return slot;
                }
                FileProbe head = Probe(path, headerOnly: true);
                DateTime t = head.Kind == CampaignSlotState.Ready && DateTime.TryParse(head.WrittenAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime w)
                    ? w : DateTime.MinValue; // 读不出的那份最先被轮换掉（它本来就读不了）
                if (t < bestTime)
                {
                    bestTime = t;
                    best = slot;
                }
            }
            return best;
        }

        // ── 后台写入队列（单工作线程，按入队顺序）──────────────────────────

        private static readonly object QueueLock = new object();
        private static readonly List<PreparedSave> WriteQueue = new List<PreparedSave>();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<PreparedSave> Finished = new System.Collections.Concurrent.ConcurrentQueue<PreparedSave>();
        private static readonly ManualResetEventSlim WritesIdle = new ManualResetEventSlim(true);
        private static bool _workerRunning;

        /// <summary>还没写完的后台存档数（排队 + 正在写）。</summary>
        public static int PendingWrites
        {
            get
            {
                lock (QueueLock)
                {
                    return WriteQueue.Count + (_workerRunning ? 1 : 0);
                }
            }
        }

        private static void Enqueue(PreparedSave p)
        {
            lock (QueueLock)
            {
                // 回滚基线（出发 / 到达）不被后来的快照取代：它必须落盘（中止时读回、到达前基线要复制）；文件操作不合并。
                int i = p.DeletePath != null ? -1 : WriteQueue.FindIndex(q => q.Slot == p.Slot && q.Copy == p.Copy && q.Reason != SaveReason.NewCampaign
                                                                          && q.Reason != SaveReason.ExpeditionDepartConfirm && q.DeletePath == null);
                if (i >= 0)
                {
                    // 旧的那份删掉、新快照排到队尾：不同副本类型交错排队时（自动 1、快速、自动 2），战役槽主档最后停在最新的一份
                    WriteQueue.RemoveAt(i);
                    CoalescedWrites++;
                }
                WriteQueue.Add(p);
                WritesIdle.Reset();
                if (!_workerRunning)
                {
                    _workerRunning = true;
                    ThreadPool.QueueUserWorkItem(_ => WriteWorker());
                }
            }
        }

        private static void WriteWorker()
        {
            while (true)
            {
                PreparedSave p;
                lock (QueueLock)
                {
                    if (WriteQueue.Count == 0)
                    {
                        _workerRunning = false;
                        WritesIdle.Set();
                        return;
                    }
                    p = WriteQueue[0];
                    WriteQueue.RemoveAt(0);
                }
                if (BackgroundDelayMsForTests > 0)
                {
                    Thread.Sleep(BackgroundDelayMsForTests);
                }
                if (p.DeletePath != null)
                {
                    TryDeleteQuiet(p.DeletePath);
                    continue;
                }
                var sw = Stopwatch.StartNew();
                try
                {
                    p.PayloadJson ??= p.PayloadLater(); // 先拼好：写盘失败时主线程回调的内存快照也要这一份
                    p.Result = Commit(p);
                    if (p.Result.Success && p.CopyAfterTo != null)
                    {
                        File.Copy(SlotPathIn(p.Dir, p.Slot), p.CopyAfterTo, true);
                    }
                }
                catch (Exception e)
                {
                    p.Result = new SaveResult(SaveOutcome.TempWriteFailed, e.Message);
                }
                p.CommitMs = sw.Elapsed.TotalMilliseconds;
                p.ThreadId = Thread.CurrentThread.ManagedThreadId;
                Finished.Enqueue(p);
            }
        }

        /// <summary>等后台存档全部落盘（读档、列表、恢复备份、删除、同步写入、退出游戏前调用）。</summary>
        public static void WaitForPendingWrites()
        {
            WritesIdle.Wait();
        }

        /// <summary>FG15-SYS-02：排在已排队的写入之后删除文件（到达前回滚基线用完；没有排队的写入就当场删）。</summary>
        public static void DeleteAfterPendingWrites(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            if (PendingWrites == 0)
            {
                TryDeleteQuiet(path);
                return;
            }
            Enqueue(new PreparedSave { Slot = int.MinValue, DeletePath = path });
        }

        /// <summary>主线程每帧取走后台写盘结果：失败发一条提示（含原因），成功只计数。由 GameRoot.OnUpdate 调用。O(本帧完成数)。</summary>
        public static void PumpBackgroundResults()
        {
            while (Finished.TryDequeue(out PreparedSave p))
            {
                LastBackgroundResult = p.Result;
                LastBackgroundCommitMs = p.CommitMs;
                LastBackgroundThreadId = p.ThreadId;
                if (p.Result.Success)
                {
                    BackgroundCommits++;
                }
                else
                {
                    BackgroundFailures++;
                    TEngine.Log.Warning($"[CampaignSaveService] 后台存档写盘失败（槽位 {p.Slot}，{p.Reason}）：{p.Result.Outcome} {p.Result.Message}");
                    Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Failure, GameText.Format("save.background_failed", p.Result.Message ?? p.Result.Outcome.ToString()));
                }
                if (p.OnWritten == null)
                {
                    continue;
                }
                try
                {
                    p.OnWritten(p.Result, p.Result.Success ? null : new SaveSnapshot { Schema = p.Schema, PayloadJson = p.PayloadJson, CardJson = p.CardJson });
                }
                catch (Exception e)
                {
                    TEngine.Log.Error($"[CampaignSaveService] 写盘完成回调异常（槽位 {p.Slot}，{p.Reason}）：{e}");
                }
            }
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

        /// <summary>FG15-SYS-01：文件戳在后台写入线程与主线程都会读写，统一加这把锁。</summary>
        private static readonly object StampLock = new object();

        private static void MarkKnownGoodMain(string mainPath)
        {
            bool ok = TryStamp(mainPath, out FileStamp stamp);
            lock (StampLock)
            {
                LoadFailures.Remove(mainPath);
                if (ok)
                {
                    KnownGoodMains[mainPath] = stamp;
                }
                else
                {
                    KnownGoodMains.Remove(mainPath);
                }
            }
        }

        private static bool IsKnownGoodMain(string mainPath)
        {
            FileStamp known;
            lock (StampLock)
            {
                if (!KnownGoodMains.TryGetValue(mainPath, out known))
                {
                    return false;
                }
            }
            return TryStamp(mainPath, out FileStamp now) && known.Length == now.Length && known.WriteUtc == now.WriteUtc;
        }

        /// <summary>文件被删除 / 被外部替换后，丢掉它的文件戳与读档失败记录。</summary>
        private static void ForgetStamps(string path)
        {
            lock (StampLock)
            {
                KnownGoodMains.Remove(path);
                LoadFailures.Remove(path);
            }
        }

        /// <summary>记录"主档头部可读、读档却失败"（升级器失败、正文坏、恢复编排的结构校验失败）。之后这个槽位在列表里显示为
        /// 损坏并完整校验备份，不再参与"继续"，存档卡按钮走"读取备份"而不是反复读同一份坏档。</summary>
        public static void RecordLoadFailure(int slotIndex, SaveFailureReason reason, int schemaVersion)
        {
            string mainPath = SlotPath(slotIndex);
            bool ok = TryStamp(mainPath, out FileStamp stamp);
            lock (StampLock)
            {
                KnownGoodMains.Remove(mainPath);
                if (!ok)
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
        }

        private static LoadFailureRecord FindLoadFailure(string mainPath)
        {
            LoadFailureRecord rec;
            lock (StampLock)
            {
                if (!LoadFailures.TryGetValue(mainPath, out rec))
                {
                    return null;
                }
            }
            if (TryStamp(mainPath, out FileStamp now) && now.Length == rec.Stamp.Length && now.WriteUtc == rec.Stamp.WriteUtc)
            {
                return rec;
            }
            ForgetStamps(mainPath); // 文件已变，旧失败记录作废（已知完好戳此前已被 RecordLoadFailure 去掉）
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
            PostgameDays = Endgame.PostgameService.HoldDays(s, GameLogic.Core.GameClock.Ticks),
            PostgameTopLevel = s.Progress?.Postgame?.HighestLevel ?? 0,
            SandboxCode = SandboxService.Code(s),
            RealPlaySeconds = s.RealPlaySeconds,
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
            WaitForPendingWrites(); // FG15-SYS-01：读写互斥——后台还在写这个槽时先等它落盘，读到的总是完整的最新一份
            string mainPath = SlotPath(slotIndex);
            var lw = Stopwatch.StartNew();
            FileProbe probe = Probe(mainPath);
            LastLoadProbeMs = lw.Elapsed.TotalMilliseconds;

            switch (probe.Kind)
            {
                case CampaignSlotState.Empty:
                    if (File.Exists(BakPath(slotIndex)))
                    {
                        bool bakReady = Probe(BakPath(slotIndex)).Kind == CampaignSlotState.Ready;
                        return new LoadResult(LoadOutcome.Corrupt, null, bakReady ? GameText.Get("save.svc.the_main_save_is") : GameText.Get("save.svc.the_main_save_is_2"),
                            SaveFailureReason.MainMissing);
                    }
                    return new LoadResult(LoadOutcome.Empty, null, GameText.Get("save.svc.the_save_does_not"));
                case CampaignSlotState.DemoSave:
                    return new LoadResult(LoadOutcome.DemoSave, null, probe.Message, SaveFailureReason.DemoSave, probe.SchemaVersion);
                case CampaignSlotState.Incompatible:
                    return new LoadResult(LoadOutcome.Incompatible, null, probe.Message, probe.Reason, probe.SchemaVersion,
                        probe.Reason == SaveFailureReason.MigrationMissing ? probe.MissingFrom : 0);
                case CampaignSlotState.Corrupt:
                    // FG15-SYS-01：列表只读头部，正文校验和在这里才第一次算——登记读档失败，列表与“继续”随即把它当损坏存档（提供读取备份）。
                    RecordLoadFailure(slotIndex, probe.Reason, probe.SchemaVersion);
                    return new LoadResult(LoadOutcome.Corrupt, null, probe.Message, probe.Reason, probe.SchemaVersion);
            }

            LoadResult loaded = LoadEnvelope(slotIndex, probe.Envelope, fromFile: true);
            if (loaded.Success)
            {
                CampaignPayloadJson.VerifyOnce(loaded.State); // 本进程第一次读档时比对并行正文序列化（读档本来就阻塞），之后的后台存档才用并行版
            }
            return loaded;
        }

        /// <summary>FG15-SYS-02（DEBT-FG15SYS01-05）：从内存里的回滚基线快照读回（后台写盘失败时中止出发 / 到达用），之后与读档走同一段流程；
        /// 不登记读档失败、不标记主档（槽位文件可能不是这份快照）。</summary>
        internal static LoadResult LoadSnapshot(int slotIndex, SaveSnapshot snap)
        {
            if (snap == null || string.IsNullOrEmpty(snap.PayloadJson))
            {
                return new LoadResult(LoadOutcome.Empty, null, GameText.Get("save.svc.the_save_does_not"));
            }
            return LoadEnvelope(slotIndex, new CampaignSaveEnvelope { SchemaVersion = snap.Schema, ContentVersion = CurrentContentVersion, PayloadJson = snap.PayloadJson, CardJson = snap.CardJson }, fromFile: false);
        }

        private static LoadResult LoadEnvelope(int slotIndex, CampaignSaveEnvelope envelope, bool fromFile)
        {
            string mainPath = SlotPath(slotIndex);
            SaveMigrationChain chain = CampaignSaveMigrations.Active;
            string payload = envelope.PayloadJson;
            if (envelope.SchemaVersion < chain.TargetVersion)
            {
                SaveMigrationRun run = chain.Run(envelope.SchemaVersion, payload);
                if (!run.Success)
                {
                    bool missing = run.Outcome == SaveMigrationOutcome.Missing || run.Outcome == SaveMigrationOutcome.OutOfRange;
                    TEngine.Log.Warning($"[CampaignSaveService] 槽位 {slotIndex} 升级失败：{run.Message}（原文件未改动）");
                    if (fromFile) RecordLoadFailure(slotIndex, missing ? SaveFailureReason.MigrationMissing : SaveFailureReason.MigrationFailed, envelope.SchemaVersion);
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
                if (fromFile) RecordLoadFailure(slotIndex, SaveFailureReason.Payload, envelope.SchemaVersion);
                return new LoadResult(LoadOutcome.Corrupt, null, GameText.Format("save.svc.could_not_parse_the", e.Message), SaveFailureReason.Payload, envelope.SchemaVersion);
            }

            if (state == null)
            {
                if (fromFile) RecordLoadFailure(slotIndex, SaveFailureReason.Payload, envelope.SchemaVersion);
                return new LoadResult(LoadOutcome.Corrupt, null, GameText.Get("save.svc.the_save_content_deserialized"), SaveFailureReason.Payload, envelope.SchemaVersion);
            }

            CampaignFgStateDomains.EnsureAll(state);
            state.SchemaVersion = chain.TargetVersion;
            // FG9-DATA-01：旧档里首领部件的类型 boss_node / boss_core 改写成敌人表 ID（幂等）。
            Content.EnemyDefs.UpgradeLegacyTypeIds(state);

            // FG0-ARCH-05（FGR-GEN-061 / 060）：生成器版本比本版本游戏新 → 版本更新（Newer）；已知表面的区块差异损坏 → 正文损坏。
            if (!WorldGen.WorldGenService.ValidateForLoad(state, out SaveFailureReason worldReason, out string worldMessage))
            {
                TEngine.Log.Warning($"[CampaignSaveService] 槽位 {slotIndex} 世界数据无法恢复：{worldMessage}（原文件未改动）");
                if (fromFile) RecordLoadFailure(slotIndex, worldReason, envelope.SchemaVersion);
                return new LoadResult(worldReason == SaveFailureReason.Newer ? LoadOutcome.Incompatible : LoadOutcome.Corrupt, null, worldMessage,
                    worldReason, envelope.SchemaVersion);
            }

            SaveNoticeRecord[] notices;
            try
            {
                notices = SaveContentReconciler.Reconcile(state, envelope.ContentVersion, CurrentContentVersion);
                // FG5-RND-02（DEBT-FG0SAVE01-07 解析类）：解析台里引用的已移除内容。
                SaveNoticeRecord[] analysisNotices = SaveContentReconciler.ReconcileAnalysis(state, envelope.ContentVersion, CurrentContentVersion);
                if (analysisNotices.Length > 0)
                {
                    notices = notices.Concat(analysisNotices).ToArray();
                }
                // FG8-QST-01（DEBT-FG0SAVE01-07 任务物品类）：已移除的区域任务物转废料；引用已移除任务定义的任务标为失败。
                SaveNoticeRecord[] questNotices = SaveContentReconciler.ReconcileQuests(state, envelope.ContentVersion, CurrentContentVersion);
                if (questNotices.Length > 0)
                {
                    notices = notices.Concat(questNotices).ToArray();
                }
            }
            catch (Exception e)
            {
                // 对账是"尽力而为"的内容迁移：失败时原样保留物品，不让整份存档打不开。
                TEngine.Log.Error($"[CampaignSaveService] 槽位 {slotIndex} 内容对账异常，物品原样保留：{e}");
                notices = Array.Empty<SaveNoticeRecord>();
            }
            state.ContentVersion = CurrentContentVersion;
            try
            {
                // FG1-SIG-01（FG01 第 6 章：读档后不复制、不丢失固件实例）：修复信号核槽位与实例账的悬空引用。
                Signal.SignalCoreService.EnsureConsistent(state);
            }
            catch (Exception e)
            {
                TEngine.Log.Error($"[CampaignSaveService] 槽位 {slotIndex} 信号核一致性修复异常，原样保留：{e}");
            }
            try
            {
                // FG1-SIG-06：旧档暴露来源按正式版规则迁移（暴露值原样保留、旧明细补来源种类、直控累计停用）；
                // 旧档里已带回、还没发放的敌方加密固件补发到基元仓（幂等，不响提示）。
                CampaignExposureLedger.MigrateLegacy(state);
                // FG1-E2E-01（DEBT-FG1SIG07-05）：旧档里按游戏秒存的信号计时字段换成整数步（幂等）。
                Signal.SignalTimeMigration.Migrate(state);
                Signal.RawFirmwareService.GrantRecovered(state, notify: false);
            }
            catch (Exception e)
            {
                TEngine.Log.Error($"[CampaignSaveService] 槽位 {slotIndex} 暴露规则迁移 / 加密固件补发异常，原样保留：{e}");
            }
            if (!fromFile)
            {
                // 内存快照：不碰主档的读档记录
            }
            else if (envelope.SchemaVersion == chain.TargetVersion)
            {
                MarkKnownGoodMain(mainPath); // 完整校验过的当前格式主档：之后的常规存档不必再整份校验它
            }
            else
            {
                lock (StampLock)
                {
                    LoadFailures.Remove(mainPath);
                }
            }
            return new LoadResult(LoadOutcome.Success, state, null, SaveFailureReason.None, envelope.SchemaVersion, 0, notices);
        }

        /// <summary>查询单个槽位的存档卡元数据，供主菜单渲染。永不抛出。不执行升级与对账（只读头部与卡片）。</summary>
        public static CampaignSlotMetadata GetSlotMetadata(int slotIndex)
        {
            WaitForPendingWrites();
            var meta = new CampaignSlotMetadata { SlotIndex = slotIndex, Kind = KindOf(slotIndex), HomeSlot = HomeOf(slotIndex) };
            string mainPath = SlotPath(slotIndex);
            // FG15-SYS-01（DEBT-FG0SAVE01-08）：列表只读正文之前的头部（卡片校验和 + 长度与结尾检查），正文的完整校验留到读档；旧格式文件照旧整份校验。
            FileProbe main = Probe(mainPath, headerOnly: true);
            meta.HeaderOnly = main.HeaderOnly;
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
                meta.ErrorMessage = GameText.Get("save.svc.load_failed") + failed.Reason;
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
                meta.ErrorMessage = meta.HasBackup ? GameText.Get("save.svc.the_main_save_is") : GameText.Get("save.svc.the_main_save_is_2");
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
            meta.PostgameDays = card.PostgameDays;
            meta.PostgameTopLevel = card.PostgameTopLevel;
            meta.SandboxCode = card.SandboxCode;
            meta.RealPlaySeconds = card.RealPlaySeconds;
            meta.ThumbnailJpg = DecodeThumbnail(main.Envelope.ThumbnailJpg);
            return meta;
        }

        /// <summary>FG15-SYS-01：全部战役槽（0～2 + 磁盘上出现过的槽号 + 末尾一个空槽）的元数据，只读头部。“继续”在其中取最新的可读档。</summary>
        public static CampaignSlotMetadata[] GetAllSlotMetadata() => ListedManualSlots().Select(GetSlotMetadata).ToArray();

        private static byte[] DecodeThumbnail(string base64)
        {
            if (string.IsNullOrEmpty(base64))
            {
                return null;
            }
            try
            {
                return Convert.FromBase64String(base64);
            }
            catch
            {
                return null;
            }
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
            WaitForPendingWrites();
            preservedPath = null;
            string bakPath = BakPath(slotIndex);
            FileProbe bak = Probe(bakPath);
            if (bak.Kind != CampaignSlotState.Ready)
            {
                TEngine.Log.Warning($"[CampaignSaveService] 槽位 {slotIndex} 没有可读取的备份（{bak.Kind}/{bak.Reason}），恢复失败。");
                return false;
            }

            string mainPath = SlotPath(slotIndex);
            ForgetStamps(mainPath);
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
            public bool HeaderOnly;
        }

        private const string PayloadMarker = ",\"PayloadJson\":\"";
        /// <summary>头部（卡片 + 缩略图）最多读这么多字节；超过当作旧格式整份校验。</summary>
        private const int HeaderReadLimit = 1 << 20;

        private static FileProbe Probe(string path, bool headerOnly)
        {
            if (headerOnly)
            {
                FileProbe head = ProbeHeader(path);
                if (head != null)
                {
                    return head;
                }
            }
            return Probe(path);
        }

        /// <summary>
        /// FG15-SYS-01（DEBT-FG0SAVE01-08）：只读正文之前的头部分类。卡片 + 缩略图按 <see cref="CampaignSaveEnvelope.CardChecksum"/> 校验；
        /// 文件长度不足“头部 + 正文长度”或结尾不是完整的 JSON 收尾 → 截断。正文校验和留到读档（读档失败会登记，列表随即显示损坏）。
        /// 返回 null = 这份文件没有新头部（旧格式 / Demo / 读不出），调用方改走整份校验。
        /// </summary>
        private static FileProbe ProbeHeader(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            byte[] marker = Encoding.ASCII.GetBytes(PayloadMarker);
            string head;
            long rest;
            bool tailOk = false;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long length = fs.Length;
                    var acc = new MemoryStream();
                    var buf = new byte[16384];
                    int found = -1;
                    while (found < 0 && acc.Length < HeaderReadLimit)
                    {
                        int n = fs.Read(buf, 0, buf.Length);
                        if (n <= 0)
                        {
                            break;
                        }
                        int from = (int)Math.Max(0, acc.Length - marker.Length);
                        acc.Write(buf, 0, n);
                        found = IndexOf(acc.GetBuffer(), (int)acc.Length, marker, from);
                    }
                    if (found < 0)
                    {
                        return null;
                    }
                    head = Encoding.UTF8.GetString(acc.GetBuffer(), 0, found) + "}";
                    if (length >= 2)
                    {
                        fs.Seek(-2, SeekOrigin.End);
                        int a = fs.ReadByte();
                        int b = fs.ReadByte();
                        tailOk = a == 0x22 && b == 0x7D; // 正文字符串的收尾引号 + 信封的右花括号
                    }
                    rest = length - (found + marker.Length); // 正文（转义后）+ 收尾占用的字节
                }
            }
            catch
            {
                return null;
            }
            CampaignSaveEnvelope env;
            try
            {
                env = JsonUtility.FromJson<CampaignSaveEnvelope>(head);
            }
            catch
            {
                return null;
            }
            if (env == null || string.IsNullOrEmpty(env.CardChecksum) || env.SchemaVersion <= CampaignSaveMigrations.LastDemoSchemaVersion)
            {
                return null; // 旧格式 / Demo / 异常版本号：按整份路径给出原来的稳定原因
            }
            var p = new FileProbe { HeaderOnly = true, SchemaVersion = env.SchemaVersion, WrittenAtUtc = env.WrittenAtUtc };
            SaveMigrationChain chain = CampaignSaveMigrations.Active;
            if (env.SchemaVersion > chain.TargetVersion)
            {
                p.Kind = CampaignSlotState.Incompatible;
                p.Reason = SaveFailureReason.Newer;
                p.Message = GameText.Format("save.svc.save_schemaversion_0_is", env.SchemaVersion, chain.TargetVersion);
                return p;
            }
            if (!tailOk || rest < env.PayloadLength + 2)
            {
                return Bad(p, SaveFailureReason.Truncated, GameText.Get("save.svc.the_save_header_is"));
            }
            if (!string.Equals(ComputeChecksum(env.CardJson, env.ThumbnailJpg), env.CardChecksum, StringComparison.OrdinalIgnoreCase))
            {
                return Bad(p, SaveFailureReason.Checksum, GameText.Get("save.svc.checksum_mismatch_the_save"));
            }
            if (!chain.CanUpgradeFrom(env.SchemaVersion, out int missingFrom))
            {
                p.Kind = CampaignSlotState.Incompatible;
                p.Reason = SaveFailureReason.MigrationMissing;
                p.MissingFrom = missingFrom;
                p.Message = GameText.Format("save.svc.no_upgrader_v_0", missingFrom, missingFrom + 1, env.SchemaVersion);
                return p;
            }
            CampaignSaveCard card;
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
                return Bad(p, SaveFailureReason.Payload, GameText.Get("save.svc.the_save_card_header"));
            }
            p.Kind = CampaignSlotState.Ready;
            p.Envelope = env;
            p.Card = card;
            return p;
        }

        private static int IndexOf(byte[] data, int count, byte[] pattern, int from)
        {
            for (int i = from; i <= count - pattern.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j])
                {
                    j++;
                }
                if (j == pattern.Length)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>读头部并分类（不反序列化正文）：Empty / Ready（含可逐级升级的旧正式版）/ DemoSave / Incompatible / Corrupt。</summary>
        private static FileProbe Probe(string path)
        {
            var p = new FileProbe();
            if (!File.Exists(path))
            {
                p.Kind = CampaignSlotState.Empty;
                p.Message = GameText.Get("save.svc.the_save_does_not");
                return p;
            }

            string fileJson;
            try
            {
                fileJson = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception e)
            {
                return Bad(p, SaveFailureReason.ReadFailed, GameText.Format("save.svc.read_failed_0", e.Message));
            }

            if (string.IsNullOrWhiteSpace(fileJson))
            {
                return Bad(p, SaveFailureReason.EmptyFile, GameText.Get("save.svc.the_save_file_is"));
            }

            CampaignSaveEnvelope env;
            try
            {
                env = JsonUtility.FromJson<CampaignSaveEnvelope>(fileJson);
            }
            catch (Exception e)
            {
                return Bad(p, SaveFailureReason.Truncated, GameText.Format("save.svc.the_json_is_truncated", e.Message));
            }

            if (env == null || string.IsNullOrEmpty(env.PayloadJson))
            {
                return Bad(p, SaveFailureReason.Truncated, GameText.Get("save.svc.the_save_header_is"));
            }

            p.SchemaVersion = env.SchemaVersion;
            p.WrittenAtUtc = env.WrittenAtUtc;
            if (env.SchemaVersion < 1)
            {
                return Bad(p, SaveFailureReason.Payload, GameText.Format("save.svc.save_format_version_0", env.SchemaVersion));
            }

            if (env.SchemaVersion <= CampaignSaveMigrations.LastDemoSchemaVersion)
            {
                p.Kind = CampaignSlotState.DemoSave;
                p.Reason = SaveFailureReason.DemoSave;
                p.Message = GameText.Format("save.svc.demo_0_1_save", env.SchemaVersion);
                return p;
            }

            SaveMigrationChain chain = CampaignSaveMigrations.Active;
            if (env.SchemaVersion > chain.TargetVersion)
            {
                p.Kind = CampaignSlotState.Incompatible;
                p.Reason = SaveFailureReason.Newer;
                p.Message = GameText.Format("save.svc.save_schemaversion_0_is", env.SchemaVersion, chain.TargetVersion);
                return p;
            }

            var cw = Stopwatch.StartNew();
            string checksum = ComputeChecksum(env.PayloadJson, env.CardJson);
            LastLoadChecksumMs = cw.Elapsed.TotalMilliseconds;
            if (!string.Equals(checksum, env.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                return Bad(p, SaveFailureReason.Checksum, GameText.Get("save.svc.checksum_mismatch_the_save"));
            }

            if (!chain.CanUpgradeFrom(env.SchemaVersion, out int missingFrom))
            {
                p.Kind = CampaignSlotState.Incompatible;
                p.Reason = SaveFailureReason.MigrationMissing;
                p.MissingFrom = missingFrom;
                p.Message = GameText.Format("save.svc.no_upgrader_v_0", missingFrom, missingFrom + 1, env.SchemaVersion);
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
                return Bad(p, SaveFailureReason.Payload, GameText.Get("save.svc.the_save_card_header"));
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

        /// <summary>校验和 = SHA256(UTF-8(正文 + \0 + 卡片))。自检构造"真实格式"的测试存档时也用它。
        /// FG15-SYS-02（DEBT-FG15SYS01-04）：按 16K 字符分块编码、逐块喂 Burst 版 SHA-256（BinGames.Sim.BurstSha256，任意线程可用；托管 SHA256 在 Editor 只有约 37 MB/s），
        /// 不再拼接整份正文再整份转字节（2.3 MB 存档省掉约 7 MB 临时分配）；
        /// 字节序列与原写法逐字节相同（UTF-8 编码器跨块保留代理对状态），校验和不变——FgSaveRobustSelfCheck K 段对照原写法断言。</summary>
        public static string ComputeChecksum(string payload, string cardJson)
        {
            var sha = new BinGames.Sim.BurstSha256.State();
            BinGames.Sim.BurstSha256.Init(ref sha);
            Encoder enc = Encoding.UTF8.GetEncoder();
            var chars = new char[ChecksumChunkChars];
            var bytes = new byte[Encoding.UTF8.GetMaxByteCount(ChecksumChunkChars)];
            HashUtf8(ref sha, enc, payload ?? string.Empty, chars, bytes, false);
            chars[0] = '\0';
            int nul = enc.GetBytes(chars, 0, 1, bytes, 0, false);
            BinGames.Sim.BurstSha256.Update(ref sha, bytes, 0, nul);
            HashUtf8(ref sha, enc, cardJson ?? string.Empty, chars, bytes, true);
            byte[] hash = BinGames.Sim.BurstSha256.Final(ref sha);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }
    }
}
