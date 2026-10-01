namespace GameLogic.Campaign.Primitive
{
    /// <summary>
    /// FG4-ECO-03 留给 FG-M5（FG5-RND-04 熔合、配方书与线索）的接口占位：电路合成台的熔合与“发现后在固件刻录台量产”。
    /// 本 Story 不实现熔合：电路合成台保留 Demo 的闭环（<see cref="PrimitiveCraftStation"/> 的升级 / 拆解），固件刻录台只刻已破解的常规固件。
    /// FG5-RND-04 实现 <see cref="IFusionService"/> 并赋给 <see cref="Service"/> 后：
    /// - 刻录台的目标清单会多出“已发现的混合固件”（<see cref="Economy.ProductionService.IsBurnable"/> 已查 <see cref="IsBurnableFusion"/>）；
    /// - 每枚混合固件芯片消耗的芯片基板按 <see cref="IFusionService.BurnSubstrateCost"/>（FG05 初值 3），由 FG5-RND-04 接进刻录台的配方扣料。
    /// 没有实现时（现在）一切照旧，不显示任何“熔合”入口（不给玩家摆一个点了没用的按钮）。
    /// </summary>
    public interface IFusionService
    {
        /// <summary>这条混合固件的熔合配方已经发现（记入配方书），可以在刻录台量产。</summary>
        bool IsDiscovered(CampaignState state, string mixedFirmwareId);

        /// <summary>刻录一枚这种混合固件芯片要几块芯片基板（FG05：比常规固件贵，初值 3）。</summary>
        int BurnSubstrateCost(string mixedFirmwareId);
    }

    public static class FusionHooks
    {
        /// <summary>熔合服务（FG5-RND-04 赋值；现在为 null）。</summary>
        public static IFusionService Service;

        public static bool Available => Service != null;

        /// <summary>刻录台能不能量产这条混合固件（熔合服务不存在时恒 false）。</summary>
        public static bool IsBurnableFusion(CampaignState state, string firmwareId) =>
            Service != null && !string.IsNullOrEmpty(firmwareId) && Service.IsDiscovered(state, firmwareId);
    }
}
