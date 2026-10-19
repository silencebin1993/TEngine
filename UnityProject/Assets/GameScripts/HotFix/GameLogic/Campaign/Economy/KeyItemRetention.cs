using System;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// D-13“关键材料不会因为全灭而永久丢失”（ProjectA_GDD D-13；FG08 FGR-EXP-032“关键材料永远不会消失”、FGR-LOOT-022“数据包……全灭时留在原地，和关键材料一样永不消失”）
    /// 的唯一判定入口。哪些东西算“关键物品”、它们在地上会不会过期、全灭时留不留在原地，都只在这里写一次：
    /// - 三选一数据包（FG8-LOOT-02 <see cref="LootPickService"/>）：携带者阵亡且同队没人时留在原地，永不过期；
    /// - 黑匣子（FG5-RND-06）与区域关键物（Demo 区域任务物、可选缓存、核心数据、人类遗产）：FG8-EXP-05 起携带者阵亡 / 全灭 / 没能撤离都按 <see cref="OfQuestItem"/> + <see cref="StaysOnWipe"/>
    ///   掉在原地（<see cref="BlackBoxService.OnMachineDied"/>、<see cref="BlackBoxService.DropFromCarrier"/>、两个 Demo 区域的撤离结算），不再判遗失（关闭 FG-GAP-133）；
    /// - 关键材料（物品表 tier = key：监听阵列核心、熔炉之心、反应核、超算核心）：掉在地上不过期（<see cref="GroundLootExpiry"/> 按 <see cref="OfItem"/> + <see cref="NeverExpires"/> 豁免）。
    /// 普通战利品（其余物品）落地后 1 个游戏日被敌方回收（FG8-EXP-05 <see cref="GroundLootExpiry"/>）。纯函数，O(1)。
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

        /// <summary>FG8-EXP-05：区域关键物条目属于哪一类——黑匣子是 <see cref="Kind.BlackBox"/>；其余区域关键物（Demo 任务物、可选缓存、核心数据、人类遗产）都按关键材料处理（任务线不会因全灭卡死）。</summary>
        public static Kind OfQuestItem(RegionQuestItemRecord q) =>
            q == null ? Kind.Ordinary : BlackBoxService.IsBlackBox(q) ? Kind.BlackBox : Kind.KeyMaterial;

        /// <summary>留在地上永不过期（“1 个游戏日后被敌方回收”不适用）。</summary>
        public static bool NeverExpires(Kind kind) => kind != Kind.Ordinary;

        /// <summary>全灭 / 携带者阵亡且没有同队机器可以接手时，留在原地等待取回（不随机器一起消失、不判遗失）。</summary>
        public static bool StaysOnWipe(Kind kind) => kind != Kind.Ordinary;

        /// <summary>携带者阵亡时，先交给同队还活着的机器（数据包：FGR-LOOT-022）；没有可交的再按 <see cref="StaysOnWipe"/> 留在原地。</summary>
        public static bool PassesToTeammate(Kind kind) => kind == Kind.DataPacket;
    }
}
