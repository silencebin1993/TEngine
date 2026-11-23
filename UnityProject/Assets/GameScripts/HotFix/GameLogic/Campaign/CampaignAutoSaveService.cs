using GameLogic.Localization;
namespace GameLogic.Campaign
{
    /// <summary>ER1-SAVE-01：ERD-SAV-002 六类自动存档点的统一服务入口。
    ///
    /// 本 Story 只建立"触发这六类保存点的统一调用约定"，不负责在尚不存在的业务系统里插桩调用——
    /// 六个触发点各自对应的正式游戏系统大多还没实现，接入时机由下列 TODO 指向的后续 Story 负责：
    ///   - HomeEntryComplete          → 已接入（ER2-SCENE-01，HomeValleyController.Enter）
    ///   - ExpeditionDepartConfirm    → ER5-EXP-01（准备与区域切换事务）
    ///   - ExpeditionResolutionComplete → ER5-RETURN-01（撤离、全灭与回城结算）
    ///   - BlueprintSaved            → 已接入（FG15-SYS-01，BlueprintEditorService.TrySave 成功后）
    ///   - BossEngageEnter           → 已接入（FoundryOutpostController / BaptismTowerBoss / InteriorSiteService）
    ///   - BeaconLaunchEnter         → 已接入（FG15-SYS-01，HomeValleyBeacon.TryStartLaunch 成功后）
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
        public static SaveResult SaveAuto(SaveReason reason, bool toast = true) => SaveAuto(reason, toast, null, null);

        /// <summary>FG15-SYS-02（DEBT-FG15SYS01-05）：出发 / 到达回滚基线。游戏中走后台写盘：<paramref name="copyAfterTo"/> 在写盘线程里紧接着复制基线，
        /// <paramref name="onWritten"/> 在主线程收到写盘结果（失败带内存快照，调用方据此中止出发 / 到达）；同步写（编辑模式自检）时两者都不用——返回值就是结果，调用方当场复制、判失败。</summary>
        public static SaveResult SaveAuto(SaveReason reason, bool toast, string copyAfterTo, System.Action<SaveResult, CampaignSaveService.SaveSnapshot> onWritten)
        {
            if (!CampaignSession.HasActiveCampaign)
            {
                TEngine.Log.Warning($"[CampaignAutoSaveService] SaveAuto({reason}) 调用时没有活动战役，已跳过。");
                return new SaveResult(SaveOutcome.NoActiveCampaign, GameText.Get("campaign.autosave.no_active_campaign_autosave"));
            }

            // FG15-SYS-01（FGR-SYS-001/002）：每次自动存档都顺带写一份轮换自动存档（本槽 3 份轮换）；游戏中主线程只拍快照、写盘放后台（不卡顿），
            // 只有出发 / 到达的回滚基线同步写（见 InBackground）。
            SaveResult result = SaveWithExport(CampaignSession.ActiveSlotIndex, reason, SaveCopyKind.Auto, InBackground(reason), copyAfterTo, onWritten);
            if (result.Success && toast)
            {
                // ER8-CONTENT-01：本作只有自动存档（SaveReason.Manual 仍是预留），每次写盘成功给一条轻提示，
                // 玩家据此知道“现在退出不会丢进度”。
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.SaveComplete, GameText.Get("campaign.autosave.autosave"));
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
        public static SaveResult SaveWithExport(int slotIndex, SaveReason reason) => SaveWithExport(slotIndex, reason, SaveCopyKind.None, false);

        /// <summary>自检专用：编辑模式下也走后台写入（正式游戏里 = Play 中，非事务边界的自动存档与快速存档都走后台）。</summary>
        public static bool ForceBackgroundForTests { get; set; }

        /// <summary>正式游戏里（Play）或自检打开 <see cref="ForceBackgroundForTests"/> 时为 true。玩家操作触发的自动存档（蓝图保存、启动信标）只在这时写：
        /// 编辑模式的自检直调服务时不写盘、不导出机器记录。</summary>
        public static bool InGame => ForceBackgroundForTests || UnityEngine.Application.isPlaying;

        /// <summary>FGR-SYS-002“主线程阻塞 ≤ 100 毫秒，其余在后台完成”：游戏中的自动存档都在后台写盘。
        /// 同步写的只有：新建战役、手动存档（主菜单 / 自检）。出发 / 到达的回滚基线（ExpeditionDepartConfirm）FG15-SYS-02 起也走后台（DEBT-FG15SYS01-05）：
        /// 主线程只拍快照，到达前基线在写盘线程里紧接着复制，写盘失败在主线程回调里从内存快照中止出发 / 到达；排队中不被后来的快照取代。编辑模式的自检默认同步（没有每帧取结果的循环），<see cref="ForceBackgroundForTests"/> 打开后台路径。</summary>
        public static bool InBackground(SaveReason reason)
        {
            switch (reason)
            {
                case SaveReason.NewCampaign:
                case SaveReason.Manual:
                    return false;
            }
            return InGame;
        }

        /// <summary>
        /// FG15-SYS-01（FGR-SYS-001“快速存档 1 个”）：写当前战役槽（最新进度）并另存一份快速存档；游戏中走后台写盘。暂停菜单“快速存档”与快捷键共用。
        /// </summary>
        /// <summary>最近一次导出存档在主线程上“同步各系统 + 截缩略图”的耗时（性能证据读）。</summary>
        public static string LastExportBreakdown { get; private set; }

        public static SaveResult QuickSave()
        {
            if (!CampaignSession.HasActiveCampaign)
            {
                return new SaveResult(SaveOutcome.NoActiveCampaign, GameText.Get("campaign.autosave.no_active_campaign_save"));
            }
            SaveResult r = SaveWithExport(CampaignSession.ActiveSlotIndex, SaveReason.QuickSave, SaveCopyKind.Quick, InBackground(SaveReason.QuickSave));
            Feedback.FeedbackCues.Raise(r.Success ? Feedback.FeedbackCueId.SaveComplete : Feedback.FeedbackCueId.Failure,
                r.Success ? GameText.Get("save.quick.done") : GameText.Format("save.quick.failed", r.Message ?? r.Outcome.ToString()));
            return r;
        }

        /// <summary>FG15-SYS-01：有没有可读的快速存档（快速读档键 / 暂停菜单按钮据此可用）。</summary>
        public static bool HasQuickSave() =>
            CampaignSession.HasActiveCampaign && CampaignSaveService.GetSlotMetadata(CampaignSaveService.QuickSlot(CampaignSession.ActiveSlotIndex)).State == CampaignSlotState.Ready;

        public static SaveResult SaveWithExport(int slotIndex, SaveReason reason, SaveCopyKind copy, bool background) =>
            SaveWithExport(slotIndex, reason, copy, background, null, null);

        public static SaveResult SaveWithExport(int slotIndex, SaveReason reason, SaveCopyKind copy, bool background,
            string copyAfterTo, System.Action<SaveResult, CampaignSaveService.SaveSnapshot> onWritten)
        {
            CampaignState state = CampaignSession.Current;
            if (state == null)
            {
                return new SaveResult(SaveOutcome.NoActiveCampaign, GameText.Get("campaign.autosave.no_active_campaign_save"));
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
            if (Endgame.EndingService.BlocksSave(state))
            {
                TEngine.Log.Info($"[CampaignAutoSaveService] 结局结算画面开着，拒绝存档（{reason}）。");
                return new SaveResult(SaveOutcome.SettlementOpen, Localization.GameText.Get("save.blocked_settlement"));
            }
            var esw = System.Diagnostics.Stopwatch.StartNew();
            WorldSim.WorldSimulation.SyncAllForSave();
            MachineRegistry.ExportToCampaignState(state);
            Endgame.PurgeWaveService.BeforeSave(state); // FG13-END-03：记下这份存档读回后从第几波清洗开始前重试（失败页写明）
            double syncMs = esw.Elapsed.TotalMilliseconds;
            SaveThumbnail.Shot thumb = SaveThumbnail.Capture(); // FG15-SYS-01：主线程只截 320×180 的像素，编码在写盘线程
            LastExportBreakdown = $"sync+export {syncMs:F1} / thumbnail capture {esw.Elapsed.TotalMilliseconds - syncMs:F1} ms"; // 只给性能证据读
            return background
                ? CampaignSaveService.SaveInBackground(slotIndex, state, reason, thumb, copy, copyAfterTo, onWritten)
                : CampaignSaveService.Save(slotIndex, state, reason, thumb, copy);
        }
    }
}
