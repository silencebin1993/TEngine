namespace GameLogic.Campaign
{
    /// <summary>ER1-SAVE-01：ERD-SAV-002 六类自动存档点的统一服务入口。
    ///
    /// 本 Story 只建立"触发这六类保存点的统一调用约定"，不负责在尚不存在的业务系统里插桩调用——
    /// 六个触发点各自对应的正式游戏系统大多还没实现，接入时机由下列 TODO 指向的后续 Story 负责：
    ///   - HomeEntryComplete          → 已接入（ER2-SCENE-01，HomeValleyController.Enter）
    ///   - ExpeditionDepartConfirm    → ER5-EXP-01（准备与区域切换事务）
    ///   - ExpeditionResolutionComplete → ER5-RETURN-01（撤离、全灭与回城结算）
    ///   - BlueprintSaved            → ER4-BLP-01（蓝图记录与编辑器）
    ///   - BossEngageEnter           → ER7-CORE-01（供能节点与主核心状态机）
    ///   - BeaconLaunchEnter         → ER7-BEACON-01（导航信标和胜利）
    ///
    /// 调用约定：业务系统在确认自己已到达"稳定事务边界"（ERD-SAV-002："自动档不得在资源事务半提交或
    /// 区域切换中间写入"）之后，直接调用 <see cref="SaveAuto"/>，不做自动扫描/定时轮询。</summary>
    public static class CampaignAutoSaveService
    {
        /// <summary>在当前 <see cref="CampaignSession"/> 的活动槽位上执行一次自动存档。
        /// 没有活动战役时直接返回 <see cref="SaveOutcome.NoActiveCampaign"/>，不抛异常、不新建战役。</summary>
        public static SaveResult SaveAuto(SaveReason reason)
        {
            if (!CampaignSession.HasActiveCampaign)
            {
                TEngine.Log.Warning($"[CampaignAutoSaveService] SaveAuto({reason}) 调用时没有活动战役，已跳过。");
                return new SaveResult(SaveOutcome.NoActiveCampaign, "没有活动战役，跳过自动存档。");
            }

            // ER1-ID-01：机器记录唯一由 MachineRegistry 写回 CampaignState，任何触发点存档前都过一次这里，
            // 不需要各个 SaveAuto 调用方自己记得导出。MachineRegistry 未绑定任何 SimWorld 会话时
            // （如战役刚新建、尚未进过战斗）内存态是空集合，导出空数组，不是错误。
            MachineRegistry.ExportToCampaignState(CampaignSession.Current);

            return CampaignSaveService.Save(CampaignSession.ActiveSlotIndex, CampaignSession.Current, reason);
        }
    }
}
