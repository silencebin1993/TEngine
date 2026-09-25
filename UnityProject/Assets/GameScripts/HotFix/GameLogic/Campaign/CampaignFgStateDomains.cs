using System;
using System.Collections.Generic;

namespace GameLogic.Campaign
{
    // FG0-SAVE-01（FGR-ARC-008）：存档 v2 新增的状态域。
    //
    // 这里只放"骨架"：每个域一个独立的可序列化类型、自带 DomainVersion，挂在 CampaignState 上并随存档往返。
    // 域里的业务字段由表中"承接 Story"各自加（加字段不需要升 SchemaVersion——JsonUtility 对缺失字段补默认值；
    // 改名、拆分、语义变化才需要写 CampaignSaveMigrations 里的逐级升级器）。
    //
    // 纪律：
    // - 每个域的唯一写入口是承接 Story 建立的系统；本 Story 之外不要直接改这些字段。
    // - 新建战役一律经 CampaignState.CreateNew；读档后缺失的域由 CampaignState.NormalizeForSave /
    //   CampaignFgStateDomains.EnsureAll 补成空域，保证任何代码拿到的都不是 null。
    // - 世界种子 / 生成器版本 / 区块差异（FGR-GEN-060/061）在 WorldGenState；只保存被修改过的区块。

    /// <summary>世界种子、生成器版本与各表面的区块差异（FG0-ARCH-05 / FG17 填写）。</summary>
    [Serializable]
    public sealed class WorldGenState
    {
        public int DomainVersion = 1;
        /// <summary>世界种子。新建战役时等于 <see cref="CampaignState.RandomSeed"/>；玩法随机流由它派生（FGR-ARC-010）。</summary>
        public int WorldSeed;
        /// <summary>生成器版本。0 = 尚未启用程序生成（沿用 Demo 的固定地图）；FG0-ARCH-05 起写入真实版本号，
        /// 读档时未修改的区块用这里记录的版本重新生成（FGR-GEN-061）。</summary>
        public int GeneratorVersion;
        /// <summary>世界设置（地图大小、资源丰度等）的预设 ID；FG0-ARCH-05 定义取值。</summary>
        public string WorldSettingsId = "default";
        /// <summary>被修改过的区块差异（FGR-GEN-060）。未修改的区块不进存档。</summary>
        public ChunkDiffRecord[] ChunkDiffs = Array.Empty<ChunkDiffRecord>();
    }

    /// <summary>一个被玩家或事件修改过的区块。<see cref="DiffPayload"/> 的编码由 FG0-ARCH-05 定义。</summary>
    [Serializable]
    public sealed class ChunkDiffRecord
    {
        public string SurfaceId;
        public int ChunkX;
        public int ChunkY;
        public string DiffPayload;
    }

    /// <summary>战役进度：幕、后日谈 / 沙盒标记（存档卡 FGR-SYS-007 显示）。幕推进由 FG11 叙事 Story 写入，
    /// 沙盒由 FG12 写入。0.2 起步内容整体相当于第一幕（GDD 0.2 §幕结构），新战役 Act = 1。</summary>
    [Serializable]
    public sealed class CampaignProgressState
    {
        public int DomainVersion = 1;
        public int Act = 1;
        public bool IsPostgame;
        public bool IsSandbox;
    }

    /// <summary>统一游戏时钟（FGR-ARC-009 / FGR-ENV-001，FG0-ARCH-01 填写）。<see cref="Day"/> = 0 表示
    /// 时钟尚未接入，存档卡不显示"第几日"。</summary>
    [Serializable]
    public sealed class GameClockState
    {
        public int DomainVersion = 1;
        public double GameSeconds;
        public int Day;
    }

    /// <summary>格网建造（FG0-ARCH-04 / FG03）。</summary>
    [Serializable]
    public sealed class GridState
    {
        public int DomainVersion = 1;
    }

    /// <summary>传送带与带上物品（FG0-ARCH-02 / FG03）。</summary>
    [Serializable]
    public sealed class BeltItemState
    {
        public int DomainVersion = 1;
    }

    /// <summary>管线与流体（FG03 管线 Story）。</summary>
    [Serializable]
    public sealed class PipeFluidState
    {
        public int DomainVersion = 1;
    }

    /// <summary>研究（FG05）。</summary>
    [Serializable]
    public sealed class ResearchState
    {
        public int DomainVersion = 1;
    }

    /// <summary>天气（FG07；时间在 <see cref="GameClockState"/>）。</summary>
    [Serializable]
    public sealed class WeatherState
    {
        public int DomainVersion = 1;
    }

    /// <summary>突袭（FG06）。</summary>
    [Serializable]
    public sealed class RaidState
    {
        public int DomainVersion = 1;
    }

    /// <summary>事件导演（FG10）。</summary>
    [Serializable]
    public sealed class DirectorEventState
    {
        public int DomainVersion = 1;
    }

    /// <summary>任务（FG08）。</summary>
    [Serializable]
    public sealed class QuestState
    {
        public int DomainVersion = 1;
    }

    /// <summary>常驻规则（FG04）。</summary>
    [Serializable]
    public sealed class StandingRuleState
    {
        public int DomainVersion = 1;
    }

    /// <summary>信号核（FG1-SIG-01 / FG01）。</summary>
    [Serializable]
    public sealed class SignalCoreState
    {
        public int DomainVersion = 1;
    }

    /// <summary>掉落数据包（FG08）。</summary>
    [Serializable]
    public sealed class LootPacketState
    {
        public int DomainVersion = 1;
    }

    /// <summary>统计（FG15 统计面板 / FG16 试玩数据）。</summary>
    [Serializable]
    public sealed class StatsState
    {
        public int DomainVersion = 1;
    }

    /// <summary>存档自身的历史（本 Story）：读档时的内容迁移通知，供通知中心历史（FG0-UX-01）回看。</summary>
    [Serializable]
    public sealed class SaveHistoryState
    {
        public int DomainVersion = 1;
        public SaveNoticeRecord[] Notices = Array.Empty<SaveNoticeRecord>();
    }

    /// <summary>FG0-UX-01（FGR-UX-020）：通知中心历史。按等级 / 类型筛选、点击定位都读这里；
    /// 只存类型 ID 与细节参数，正文在显示时按当前语言用文本键拼出。条数上限见 fg.TbUiTuning notify.history_capacity。</summary>
    [Serializable]
    public sealed class NotificationHistoryState
    {
        public int DomainVersion = 1;
        /// <summary>下一条通知的 ID（单调递增，读档后继续往后编，不会与历史撞号）。</summary>
        public long NextId = 1;
        /// <summary>已经转进通知历史的读档变更（<see cref="SaveNoticeRecord.NoticeId"/>），同一条不会转两次。</summary>
        public string[] ImportedSaveNoticeIds = Array.Empty<string>();
        public NotificationRecord[] Entries = Array.Empty<NotificationRecord>();
    }

    /// <summary>一条（可能是聚合后的）通知。</summary>
    [Serializable]
    public sealed class NotificationRecord
    {
        public long Id;
        /// <summary>fg.TbNotifyType 的类型 ID。</summary>
        public string TypeId;
        /// <summary>聚合了几次（“3 座建筑缺电”的 3）。</summary>
        public int Count = 1;
        /// <summary>来源说明的文本键与参数（FGR-BASE-020：由哪条规则 / 哪个系统触发）。空 = 系统事件。</summary>
        public string SourceKey = string.Empty;
        public string SourceArg = string.Empty;
        public NotificationMemberRecord[] Members = Array.Empty<NotificationMemberRecord>();
    }

    /// <summary>聚合里的一条成员：细节、位置、时间。</summary>
    [Serializable]
    public sealed class NotificationMemberRecord
    {
        /// <summary>细节参数（例如建筑名）。若它本身是文本键，显示时按当前语言解析。</summary>
        public string Detail = string.Empty;
        public bool HasLocation;
        public string RegionId = string.Empty;
        public float X;
        public float Y;
        public float Z;
        public string CreatedAtUtc = string.Empty;
        /// <summary>产生时的游戏日与游戏秒（统一时钟接入前为 0，界面改显示本地时间）。</summary>
        public int GameDay;
        public double GameSeconds;
    }

    /// <summary>一条读档通知。文本不落盘，只存文本键与参数，切换语言后回看仍是当前语言。</summary>
    [Serializable]
    public sealed class SaveNoticeRecord
    {
        /// <summary>稳定 ID（幂等：同一条迁移不会记两次）。</summary>
        public string NoticeId;
        public string TextKey;
        public string[] Args = Array.Empty<string>();
        /// <summary>产生通知时存档的内容版本 → 当前内容版本。</summary>
        public int FromContentVersion;
        public int ToContentVersion;
        public string CreatedAtUtc;
    }

    /// <summary>状态域登记表：名称、承接 Story、取值 / 补齐方式。自检据此逐个验证往返与缺失补齐，
    /// 新增域必须在这里登记，否则自检会发现 CampaignState 上有未登记的域字段。</summary>
    public static class CampaignFgStateDomains
    {
        public readonly struct DomainInfo
        {
            public readonly string FieldName;
            public readonly string OwnerStory;
            public readonly Func<CampaignState, object> Get;

            public DomainInfo(string fieldName, string ownerStory, Func<CampaignState, object> get)
            {
                FieldName = fieldName;
                OwnerStory = ownerStory;
                Get = get;
            }
        }

        public static readonly IReadOnlyList<DomainInfo> All = new[]
        {
            new DomainInfo(nameof(CampaignState.World), "FG0-ARCH-05（世界生成与区块流式加载）", s => s.World),
            new DomainInfo(nameof(CampaignState.Progress), "FG11 叙事 / FG12 沙盒", s => s.Progress),
            new DomainInfo(nameof(CampaignState.Clock), "FG0-ARCH-01（统一时钟）", s => s.Clock),
            new DomainInfo(nameof(CampaignState.Grid), "FG0-ARCH-04（格网建造）", s => s.Grid),
            new DomainInfo(nameof(CampaignState.Belts), "FG0-ARCH-02（传送带内核）", s => s.Belts),
            new DomainInfo(nameof(CampaignState.Pipes), "FG03 管线与流体", s => s.Pipes),
            new DomainInfo(nameof(CampaignState.Research), "FG05 研究", s => s.Research),
            new DomainInfo(nameof(CampaignState.Weather), "FG07 天气", s => s.Weather),
            new DomainInfo(nameof(CampaignState.Raids), "FG06 突袭", s => s.Raids),
            new DomainInfo(nameof(CampaignState.DirectorEvents), "FG10 事件导演", s => s.DirectorEvents),
            new DomainInfo(nameof(CampaignState.Quests), "FG08 任务", s => s.Quests),
            new DomainInfo(nameof(CampaignState.StandingRules), "FG04 常驻规则", s => s.StandingRules),
            new DomainInfo(nameof(CampaignState.SignalCore), "FG1-SIG-01（信号核）", s => s.SignalCore),
            new DomainInfo(nameof(CampaignState.LootPackets), "FG08 掉落数据包", s => s.LootPackets),
            new DomainInfo(nameof(CampaignState.Stats), "FG15 / FG16 统计", s => s.Stats),
            new DomainInfo(nameof(CampaignState.SaveHistory), "FG0-SAVE-01", s => s.SaveHistory),
            new DomainInfo(nameof(CampaignState.Notifications), "FG0-UX-01（通知中心历史）", s => s.Notifications),
        };

        /// <summary>把缺失（null）的域补成空域。读档后与存档前都会调用；已有数据的域原样保留。</summary>
        public static void EnsureAll(CampaignState s)
        {
            if (s == null)
            {
                return;
            }
            s.World ??= new WorldGenState();
            s.World.ChunkDiffs ??= Array.Empty<ChunkDiffRecord>();
            s.Progress ??= new CampaignProgressState();
            s.Clock ??= new GameClockState();
            s.Grid ??= new GridState();
            s.Belts ??= new BeltItemState();
            s.Pipes ??= new PipeFluidState();
            s.Research ??= new ResearchState();
            s.Weather ??= new WeatherState();
            s.Raids ??= new RaidState();
            s.DirectorEvents ??= new DirectorEventState();
            s.Quests ??= new QuestState();
            s.StandingRules ??= new StandingRuleState();
            s.SignalCore ??= new SignalCoreState();
            s.LootPackets ??= new LootPacketState();
            s.Stats ??= new StatsState();
            s.SaveHistory ??= new SaveHistoryState();
            s.SaveHistory.Notices ??= Array.Empty<SaveNoticeRecord>();
            s.Notifications ??= new NotificationHistoryState();
            s.Notifications.Entries ??= Array.Empty<NotificationRecord>();
            s.Notifications.ImportedSaveNoticeIds ??= Array.Empty<string>();
            foreach (NotificationRecord r in s.Notifications.Entries)
            {
                if (r != null)
                {
                    r.Members ??= Array.Empty<NotificationMemberRecord>();
                    r.SourceKey ??= string.Empty;
                    r.SourceArg ??= string.Empty;
                }
            }
        }
    }
}
