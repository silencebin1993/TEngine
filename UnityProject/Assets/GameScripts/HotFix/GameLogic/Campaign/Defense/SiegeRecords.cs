using System;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG6-DEF-05（FG06 FGR-DEF-030～032；第 6 节“进行中的突袭（敌人状态、流场）”）：攻城的存档域。唯一写入口 <see cref="Defense.SiegeService"/>。
    /// 只加字段、不升域版本（ADR FG0-SAVE-01）：旧档没有 = 没有进行中的攻城。
    /// 敌人的位置 / 耐久 / 职能 / 撤退在家园战斗内核快照里（格式 11）；流场不存（格网 + 结构单位 + 集结点的纯函数，读档后同一输入重算）。
    /// </summary>
    [Serializable]
    public sealed class SiegeState
    {
        /// <summary>下一个建筑结构单位序号（不复用；内核外部键 = −(200 万 + 序号)）。</summary>
        public int NextSerial = 1;
        /// <summary>攻城期间进了内核的建筑（炮塔 / 防御建筑另有自己的记录）。</summary>
        public SiegeStructureRecord[] Structures = Array.Empty<SiegeStructureRecord>();
        /// <summary>攻城剧场：开着时的流场矩形（格，含两端）。</summary>
        public bool TheaterActive;
        public int MinX;
        public int MinY;
        public int MaxX;
        public int MaxY;
        /// <summary>施工中的虚影被溅射打掉的耐久（打满 = 施工中被摧毁，FG03 第 5 节）。</summary>
        public SiegeSiteDamageRecord[] SiteDamage = Array.Empty<SiegeSiteDamageRecord>();
        /// <summary>已经报过“正在破墙”的建筑 ID 与那一步（同一座只报一次；冷却后换目标才再报）。</summary>
        public string[] NotifiedBreaches = Array.Empty<string>();
        public long LastBreachNotifyTick = -1;
        /// <summary>累计：展开的单位、被消灭的、撤出的、被拆的建筑（统计 / 自检）。</summary>
        public long TotalUnfolded;
        public long TotalExited;
        public long TotalDestroyedBuildings;
    }

    /// <summary>一座进了内核的建筑（序号 ↔ 建筑 ID）。</summary>
    [Serializable]
    public sealed class SiegeStructureRecord
    {
        public int Serial;
        public string BuildingId = string.Empty;
    }

    /// <summary>一处施工虚影受到的溅射伤害。</summary>
    [Serializable]
    public sealed class SiegeSiteDamageRecord
    {
        public string BuildingId = string.Empty;
        public float Damage;
    }
}
