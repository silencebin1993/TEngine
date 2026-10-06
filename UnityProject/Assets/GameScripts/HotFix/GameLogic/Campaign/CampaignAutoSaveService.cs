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
        /// <summary>FG6-DEF-08：因核心已被摧毁而拒绝的存档次数（本进程；自检读）。</summary>
        public static int BlockedSaves { get; private set; }

        /// <summary>在当前 <see cref="CampaignSession"/> 的活动槽位上执行一次自动存档。
        /// 没有活动战役时直接返回 <see cref="SaveOutcome.NoActiveCampaign"/>，不抛异常、不新建战役。</summary>
        /// <param name="toast">false = 调用方自己发更具体的提示（FG6-DEF-08 突袭预警自动存档），不再发通用的“自动存档”轻提示，免得一次存档两条提示。</param>
        public static SaveResult SaveAuto(SaveReason reason, bool toast = true)
        {
            if (!CampaignSession.HasActiveCampaign)
            {
                TEngine.Log.Warning($"[CampaignAutoSaveService] SaveAuto({reason}) 调用时没有活动战役，已跳过。");
                return new SaveResult(SaveOutcome.NoActiveCampaign, "没有活动战役，跳过自动存档。");
            }

            SaveResult result = SaveWithExport(CampaignSession.ActiveSlotIndex, reason);
            if (result.Success && toast)
            {
                // ER8-CONTENT-01：本作只有自动存档（SaveReason.Manual 仍是预留），每次写盘成功给一条轻提示，
                // 玩家据此知道“现在退出不会丢进度”。
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.SaveComplete, "自动存档");
            }
            return result;
        }

        /// <summary>存档前同步 + 写盘的唯一公共入口（自动存档与 FG0-UX-01 暂停菜单“保存并返回主菜单”共用）。
        /// ER1-ID-01：机器记录唯一由 MachineRegistry 写回 CampaignState，任何存档前都过一次这里，
        /// 调用方不必自己记得导出。MachineRegistry 未绑定任何 SimWorld 会话时
        /// （如战役刚新建、尚未进过战斗）内存态是空集合，导出空数组，不是错误。
        /// FG0-ARCH-01 修复：家园在派遣 / 远征期间不再 Exit，机器实时位置只在表现对象上——
        /// 所以这里对**每一次**存档（自动档与手动档同一条路径）先把全部已载入地点的实时状态写回记录，
        /// 再导出，调用方不必再自己记得同步（旧的 <c>GameRoot.SyncActiveRegionForSave</c> 预同步变成幂等冗余）。</summary>
        public static SaveResult SaveWithExport(int slotIndex, SaveReason reason)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return new SaveResult(SaveOutcome.NoActiveCampaign, "没有活动战役，跳过存档。");
            }
            // FG6-DEF-08 复修（P2 必败档）：核心的内核耐久已到下限、只是还没轮到攻城对账判定（≤ siege.sync_seconds）时同样拒绝——
            // 否则这份档读回后第一次对账即判失败，失败页的“读取最近自动存档”会读到一份必败档。
            if (Regions.HomeValleySoftlockGuard.IsCoreDestroyed(state) || Defense.RaidResultService.CoreAtFloor(state))
            {
                // FG6-DEF-08（FGR-DEF-053）：核心被摧毁之后不再写任何存档（自动档与“保存并返回主菜单”同一入口），失败页的“读取最近自动存档”才读得到突袭前的安全档。
                BlockedSaves++;
                TEngine.Log.Info($"[CampaignAutoSaveService] 归还核心已被摧毁，拒绝存档（{reason}）。");
                return new SaveResult(SaveOutcome.CampaignLost, Localization.GameText.Get("save.blocked_core_lost"));
            }
            WorldSim.WorldSimulation.SyncAllForSave();
            MachineRegistry.ExportToCampaignState(state);
            return CampaignSaveService.Save(slotIndex, state, reason);
        }
    }
}
