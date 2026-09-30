using System;
using System.Collections.Generic;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG3-LOG-09（DEBT-FG0ARCH04-09；FG03 FGR-LOG-090“热更层每帧开销与物品数、传送带格数无关”）：建筑外观字段的变化集。
    ///
    /// 模拟改了某座建筑会影响画面的字段（施工状态、供电状态、关停、损毁、旋转）时调用 <see cref="Mark"/>（O(1)，不分配：建筑 ID 字符串是记录本身的）；
    /// 家园画面对账（<see cref="HomeValleyController"/>）每帧只重画变化集里的建筑，然后清空——画面开销按“这一帧变了几座”算，与建筑总数无关。
    /// 纯表现：不进存档、不影响模拟；镜头不在家园时变化集照样累积（上限 = 建筑数，同一座只记一次），回到家园时整份对账一次后清空。
    /// 漏报的改动（例如自检直接改字段）由对账的分帧轮询兜底（最迟 建筑数 ÷ home.visual_slice_buildings 帧内画出来）。
    /// </summary>
    public static class BuildingVisualFeed
    {
        private static readonly HashSet<string> PendingIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>累计报告次数（自检读：模拟侧确实在报）。</summary>
        public static long MarkCount { get; private set; }

        public static int Count => PendingIds.Count;

        /// <summary>这一帧待重画的建筑 ID（对账方遍历后调用 <see cref="Clear"/>；遍历期间不要 Mark）。</summary>
        public static HashSet<string> Pending => PendingIds;

        public static void Mark(BuildingRecord building)
        {
            if (building?.BuildingId == null)
            {
                return;
            }
            PendingIds.Add(building.BuildingId);
            MarkCount++;
        }

        public static void Clear() => PendingIds.Clear();
    }
}
