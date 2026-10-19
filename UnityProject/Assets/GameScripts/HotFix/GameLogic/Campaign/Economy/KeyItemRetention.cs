using System;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// D-13“关键材料不会因为全灭而永久丢失”（ProjectA_GDD D-13；FG08 FGR-EXP-032“关键材料永远不会消失”、FGR-LOOT-022“数据包……全灭时留在原地，和关键材料一样永不消失”）
    /// 的唯一判定入口。哪些东西算“关键物品”、它们在地上会不会过期、全灭时留不留在原地，都只在这里写一次：
    /// - 三选一数据包（FG8-LOOT-02 <see cref="LootPickService"/>）：携带者阵亡且同队没人时留在原地，永不过期；
    /// - 黑匣子（FG5-RND-06）：结论与这里一致（永不 Lost、掉在原地），但 BlackBoxService 目前仍是自己的实现，<b>待 FG8-EXP-05 改接本类</b>（FG-GAP-133）；
    /// - 关键材料（物品表 tier = key：监听阵列核心、熔炉之心、反应核、超算核心）：全灭结算与地上过期判定待 FG8-EXP-05 改接本类（FG-GAP-133）。
    /// 现在只有数据包（<see cref="LootPickService.OnMachineDied"/>）调用本类。FG8-EXP-05（返回与结算：地上普通战利品 1 个游戏日后被敌方回收、全灭结算）
    /// 必须让黑匣子、关键材料与战利品过期都用 <see cref="NeverExpires"/> / <see cref="StaysOnWipe"/> 判定豁免，不另写一套（ADR-LOOT-002 第 5 节）。纯函数，O(1)。
    /// </summary>
    public static class KeyItemRetention
    {
        public enum Kind
        {
            /// <summary>普通战利品：地上会过期（FG8-EXP-05），全灭时随货舱掉在原地。</summary>
            Ordinary = 0,
            KeyMaterial = 1,
            BlackBox = 2,
            DataPacket = 3,
        }

        /// <summary>物品表 tier = key 的物品（关键材料）。</summary>
        public const string KeyTier = "key";

        /// <summary>一件物品属于哪一类（关键材料按物品表的 tier；其它物品都是普通战利品）。黑匣子与数据包不是物品表里的物品，调用方直接用对应的 <see cref="Kind"/>。</summary>
        public static Kind OfItem(string itemId) =>
            !string.IsNullOrEmpty(itemId) && ItemCatalog.TryGet(itemId, out ItemDef d) && string.Equals(d.Tier, KeyTier, StringComparison.Ordinal) ? Kind.KeyMaterial : Kind.Ordinary;

        /// <summary>留在地上永不过期（“1 个游戏日后被敌方回收”不适用）。</summary>
        public static bool NeverExpires(Kind kind) => kind != Kind.Ordinary;

        /// <summary>全灭 / 携带者阵亡且没有同队机器可以接手时，留在原地等待取回（不随机器一起消失、不判遗失）。</summary>
        public static bool StaysOnWipe(Kind kind) => kind != Kind.Ordinary;

        /// <summary>携带者阵亡时，先交给同队还活着的机器（数据包：FGR-LOOT-022）；没有可交的再按 <see cref="StaysOnWipe"/> 留在原地。</summary>
        public static bool PassesToTeammate(Kind kind) => kind == Kind.DataPacket;
    }
}
