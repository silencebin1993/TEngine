using System;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG12-EVT-02（FG10 FGR-EVT-010～013、第 12 节“家园和各区域的震动值”）：震动值按地点统计与掘进蠕虫的存档域。唯一写入口 <see cref="Env.VibrationService"/> / <see cref="Env.WormEventService"/>。
    /// 只加字段、不升域版本：旧档没有 = 空域；家园的旧震动值（<see cref="EconomyState.Vibration"/>）第一次推进时迁进家园那一行（<see cref="Migrated"/>）。
    /// </summary>
    [Serializable]
    public sealed class VibrationState
    {
        public int DomainVersion = 1;
        /// <summary>旧档的家园震动值已从 EconomyState.Vibration 迁入（之后那个字段不再写）。</summary>
        public bool Migrated;
        /// <summary>各地点（家园 / 己方前哨站 / 远征队）的震动值与阈值状态（按出现先后）。</summary>
        public VibrationSiteRecord[] Sites = Array.Empty<VibrationSiteRecord>();
        /// <summary>任何地点第一次到 50 已发过引导钩子。</summary>
        public bool OmenSeen;
        /// <summary>预警中 / 在地面上 / 正在潜回地下的蠕虫。</summary>
        public WormRecord[] Worms = Array.Empty<WormRecord>();
        public int NextChain = 1;
        /// <summary>下一次按地点检查阈值的世界步（-1 = 下一步）。</summary>
        public long NextCheckTick = -1;
        // ── 统计（总览 / 自检）──
        public int Blasts;
        public int Requests;
        public int Breakouts;
        public int Kills;
        public int Escapes;
        public int SegmentsKilled;
        public int BuildingsHit;
    }

    /// <summary>FG12-EVT-02：一个地点的震动值。</summary>
    [Serializable]
    public sealed class VibrationSiteRecord
    {
        /// <summary>home = 家园；op-N = 己方前哨站（<see cref="OwnOutpostRecord.OutpostId"/>）；team:N = 第 N 支远征队所在的地方。</summary>
        public string SiteId = string.Empty;
        public double Value;
        /// <summary>0 平静（&lt; 50）/ 1 预兆（≥ 50）/ 2 警告（≥ 80，<see cref="DueTick"/> 前没回落就请求）/ 3 已请求事件导演（等破土）。</summary>
        public int Level;
        public long WarnTick = -1;
        public long DueTick = -1;
        public long LastOmenTick = -1;
        /// <summary>最近一次算出的震动最集中处（星球格）与那片采矿区的提取钻座数（0 = 来自爆炸 / 远征队）。</summary>
        public float HotX;
        public float HotY;
        public int HotDrills;
        /// <summary>最近一次的每分钟净变化（来源合计 − 衰减；面板读）。</summary>
        public double LastNetPerMinute;
        /// <summary>这个地点累计的大规模爆炸次数与最近一次的世界步。</summary>
        public int BlastCount;
        public long LastBlastTick = -1;
        /// <summary>上次阈值检查以来到过 100（衰减会在两次检查之间把 100 拉回 99.9x，到过就算“到 100”，检查时取走）。</summary>
        public bool Reached;
    }

    /// <summary>FG12-EVT-02：一条掘进蠕虫（与事件导演的实例按 <see cref="Serial"/> 对应；内核里的链按 <see cref="Chain"/> 对应）。</summary>
    [Serializable]
    public sealed class WormRecord
    {
        public int Serial;
        public int Chain;
        public string SiteId = string.Empty;
        /// <summary>0 预警（裂纹）/ 1 在地面上活动 / 2 正在潜回地下离开。</summary>
        public int State;
        public float HotX;
        public float HotY;
        /// <summary>原因（悬停 / 通知用）：采矿区里的提取钻座数（0 = 大规模爆炸）。</summary>
        public int CauseDrills;
        public int Segments;
        public float[] RouteX = Array.Empty<float>();
        public float[] RouteY = Array.Empty<float>();
        /// <summary>已经掉过落的节（第 k 位）。</summary>
        public uint DroppedMask;
        /// <summary>上一步看到的破土次数与头部弧长（建筑受创按“新钻出的那一段 + 头部新扫过的一段”结算）。</summary>
        public int LastSurfacings;
        public double LastHeadS;
        public bool LastUp;
        public int BuildingsHit;
        /// <summary>这次钻出已经受过创的建筑（每次钻出每座只算一次；新钻出时清空）。</summary>
        public string[] HitIds = Array.Empty<string>();
        public long LeaveTick = -1;
    }
}
