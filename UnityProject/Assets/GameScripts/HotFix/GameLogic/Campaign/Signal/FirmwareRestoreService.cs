using System;
using System.Collections.Generic;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.Campaign.Signal
{
    /// <summary>
    /// FG2-E2E-01（FG-GAP-050，M2 出口前决定的临时来源）：解析台“数据复原”——花技术数据复原一条固件的刻录数据。
    ///
    /// 为什么需要：FG-M2 的目标是“44 条固件全部成为可玩内容”，但 FG2-FW-01 迁入的 38 条固件在正常游戏里还没有掉落入口
    /// （杂兵残骸 / 精英三选一 / 阵营首领 / 遗迹终端的掉落体系在 FG8-LOOT-01）。本渠道让玩家在 FG8 之前就拿得到它们。
    ///
    /// 规则（全部数值在 fg.TbHomeTuning，firmware.restore.*）：
    /// - 能复原的：获取途径是遗迹终端 / 杂兵残骸 / 精英三选一 / 阵营首领的固件（表 source 列 relic / salvage / elite / boss）。
    ///   基础蓝图库（开局即有）与 Demo 已有来源的两条（终端数据盒的标记跳转、技术缓存的装甲击穿）不在其中，保留原来的获得方式。
    /// - 代价：技术数据，按稀有度（普通 / 精良 / 稀有）取值；走经济账本的消费事务（不足时账本拒绝、资源池不变）。
    /// - 前提：解析台已完工且有电（与解析队列同一判定）。
    /// - 结果：与“解析台破解 / 解析完成”同一个写法——内容 ID 写进 <see cref="CampaignState.UnlockedContentIds"/>
    ///   （敌方加密的同时视为已破解，<see cref="RawFirmwareService.OnCracked"/>）；之后在信号核刻印芯片、在蓝图固件槽里选用。
    ///   不直接发芯片：芯片仍由刻印产出（FG02 FGR-FW-060“固件芯片由刻录产出”，本 Demo 阶段的刻录入口是信号核刻印）。
    /// - 状态：只写既有存档字段（UnlockedContentIds、账本流水），不新增存档域。
    ///
    /// 这是过渡渠道：FG8-LOOT-01 的掉落体系上线后由它决定保留（作为技术数据的去处）、限制还是移除；写进 ADR-QA-018 与 DEBT-FG2E2E01-02。
    /// </summary>
    public static class FirmwareRestoreService
    {
        public const string CodeNoCampaign = "no_campaign";
        public const string CodeNotFirmware = "not_firmware";
        public const string CodeNotRestorable = "not_restorable";
        public const string CodeAlready = "already";
        public const string CodeNoPower = "no_power";
        public const string CodeNoTech = "no_tech";
        public const string CodeLedger = "ledger";

        /// <summary>复原成功的次数（自检 / 冒烟对账）。</summary>
        public static int RestoredCount { get; private set; }

        /// <summary>被拒绝的次数（自检 / 冒烟对账）。</summary>
        public static int RejectedCount { get; private set; }

        /// <summary>状态修订号：复原成功后 +1（面板据此刷新候选列表）。</summary>
        public static int Revision { get; private set; }

        public readonly struct Result
        {
            public readonly bool Success;
            public readonly string Code;
            public readonly string Message;

            private Result(bool success, string code, string message)
            {
                Success = success;
                Code = code;
                Message = message;
            }

            public static Result Ok(string message) => new Result(true, null, message);
            public static Result Fail(string code, string message) => new Result(false, code, message);
        }

        /// <summary>表 source 列里能走数据复原的获取途径。</summary>
        private static readonly string[] RestorableSources = { "relic", "salvage", "elite", "boss" };

        public static bool IsRestorableSource(string source) => source != null && Array.IndexOf(RestorableSources, source) >= 0;

        /// <summary>这条固件能不能走数据复原（与战役无关：只看表的获取途径）。</summary>
        public static bool IsRestorable(string firmwareId) =>
            FirmwareKinds.IsFirmware(firmwareId) && IsRestorableSource(FirmwareKinds.SourceOf(firmwareId));

        /// <summary>复原一条固件要多少技术数据（按稀有度，fg.TbHomeTuning firmware.restore.cost_*）。不是固件时 0。</summary>
        public static int CostOf(string firmwareId)
        {
            if (!FirmwareKinds.IsFirmware(firmwareId))
            {
                return 0;
            }
            switch (FirmwareKinds.RarityOf(firmwareId))
            {
                case "epic": return TuningInt("firmware.restore.cost_epic", 12);
                case "rare": return TuningInt("firmware.restore.cost_rare", 8);
                default: return TuningInt("firmware.restore.cost_common", 4);
            }
        }

        /// <summary>
        /// 当前还能复原的固件（能复原、还没解锁），按类别（引信 / 限制器 / 流体 / 电磁）再按固件 ID 排序——与固件库的类别顺序一致。O(固件种类数)，只在面板刷新时调。
        /// </summary>
        public static List<string> Candidates(CampaignState state)
        {
            var list = new List<string>();
            foreach (string id in FirmwareCatalog.All.Keys)
            {
                if (IsRestorable(id) && !MechanicalContentUnlock.IsUnlocked(state, id))
                {
                    list.Add(id);
                }
            }
            list.Sort((a, b) =>
            {
                int c = CategoryRank(FirmwareKinds.CategoryOf(a)).CompareTo(CategoryRank(FirmwareKinds.CategoryOf(b)));
                return c != 0 ? c : string.CompareOrdinal(a, b);
            });
            return list;
        }

        private static int CategoryRank(FirmwareCategory c) => c switch
        {
            FirmwareCategory.Fuse => 0,
            FirmwareCategory.Limiter => 1,
            FirmwareCategory.Fluid => 2,
            FirmwareCategory.Electromagnetic => 3,
            _ => 4,
        };

        /// <summary>解析台已完工且有电（与解析队列 <see cref="HomeValleyAnalysis"/> 的推进条件同一判定）。</summary>
        public static bool BenchReady(CampaignState state)
        {
            if (state?.BuildingRecords == null)
            {
                return false;
            }
            foreach (BuildingRecord b in state.BuildingRecords)
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeAnalysisBench
                    && b.ConstructionState == BuildingConstructionState.Operational && b.PowerState == BuildingPowerState.Powered)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>不改任何状态的预检（面板显示原因、确认框之前用）。顺序：战役 → 固件 → 能否复原 → 已获得 → 解析台 → 技术数据。</summary>
        public static Result Check(CampaignState state, string firmwareId)
        {
            if (state == null)
            {
                return Result.Fail(CodeNoCampaign, GameText.Get("signal.reason.no_campaign"));
            }
            if (!FirmwareKinds.IsFirmware(firmwareId))
            {
                return Result.Fail(CodeNotFirmware, GameText.Format("signal.reason.print_unknown", firmwareId ?? string.Empty));
            }
            string name = FirmwareKinds.DisplayName(firmwareId) ?? firmwareId;
            if (MechanicalContentUnlock.IsUnlocked(state, firmwareId))
            {
                return Result.Fail(CodeAlready, GameText.Format("analysis.restore.reason.already", name));
            }
            if (!IsRestorable(firmwareId))
            {
                return Result.Fail(CodeNotRestorable, GameText.Format("analysis.restore.reason.not_restorable", name, FirmwareKinds.AcquireText(firmwareId) ?? string.Empty));
            }
            if (!BenchReady(state))
            {
                return Result.Fail(CodeNoPower, GameText.Get("analysis.restore.reason.no_power"));
            }
            int cost = CostOf(firmwareId);
            if (state.TechData < cost)
            {
                return Result.Fail(CodeNoTech, GameText.Format("analysis.restore.reason.no_tech", name, cost, state.TechData));
            }
            return Result.Ok(GameText.Format("analysis.restore.confirm.body", cost, name, state.TechData));
        }

        /// <summary>
        /// 复原（玩家在确认框里点“复原”之后调用）。按此刻的状态重新检查一遍（确认框开着期间可能断电 / 技术数据被别处花掉）；
        /// 扣技术数据走账本消费事务，成功后写解锁、视为破解、发反馈。失败不改任何状态。
        /// </summary>
        public static Result TryRestore(CampaignState state, string firmwareId)
        {
            Result check = Check(state, firmwareId);
            if (!check.Success)
            {
                RejectedCount++;
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, check.Message);
                return check;
            }
            int cost = CostOf(firmwareId);
            string name = FirmwareKinds.DisplayName(firmwareId) ?? firmwareId;
            string txId = "firmware_restore_tx_" + firmwareId + "_" + GameClock.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            CampaignEconomyLedger.LedgerResult propose = CampaignEconomyLedger.ProposeConsume(state, txId, "analysis_bench", CampaignEconomyLedger.ResourceTechData, cost);
            CampaignEconomyLedger.LedgerResult reserve = propose.Success ? CampaignEconomyLedger.Reserve(state, txId) : propose;
            if (!reserve.Success)
            {
                CampaignEconomyLedger.Cancel(state, txId);
                RejectedCount++;
                string reason = GameText.Format("analysis.restore.reason.no_tech", name, cost, state.TechData);
                Feedback.FeedbackCues.Raise(Feedback.FeedbackCueId.Denied, reason);
                return Result.Fail(CodeLedger, reason);
            }
            CampaignEconomyLedger.MarkRunning(state, txId);
            CampaignEconomyLedger.Commit(state, txId);

            state.UnlockedContentIds ??= Array.Empty<string>();
            if (Array.IndexOf(state.UnlockedContentIds, firmwareId) < 0)
            {
                var ids = new List<string>(state.UnlockedContentIds) { firmwareId };
                state.UnlockedContentIds = ids.ToArray();
            }
            // 敌方加密的：同一种固件的所有实例一起去掉“未破解”标记（与解析台破解同一个回调）；己方 / 中立的只刷新界面。
            if (FirmwareKinds.IsEnemyProtocol(firmwareId))
            {
                RawFirmwareService.OnCracked(state, firmwareId);
            }
            else
            {
                FirmwareKinds.NotifyCrackStateChanged();
            }
            RestoredCount++;
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.FirmwareRestoreFirstDone);
            string message = GameText.Format("analysis.restore.ok", name, cost);
            Feedback.FeedbackCues.RaiseLocatedIfKnown(Feedback.FeedbackCueId.AnalysisComplete,
                Feedback.FeedbackCues.BuildingPositionOfType(state, HomeValleyLayout.BuildingTypeAnalysisBench), message,
                Feedback.FeedbackCues.BuildingTypeSfx(HomeValleyLayout.BuildingTypeAnalysisBench));
            CampaignObjectiveTracker.Recompute(state);
            return Result.Ok(message);
        }

        /// <summary>一条候选在下拉里的一行：名称｜代价（类别与稀有度在选中后的说明行里；下拉宽度有限，英文也放得下）。</summary>
        public static string ChoiceText(string firmwareId)
        {
            if (!FirmwareKinds.TryGetRow(firmwareId, out GameConfig.fg.FirmwareKind row))
            {
                return firmwareId ?? string.Empty;
            }
            return GameText.Format("analysis.restore.choice", GameText.Get(row.NameKey), CostOf(firmwareId));
        }

        /// <summary>选中一条时的说明：类别与稀有度、获取途径（正式来源）、产生的标签、能参与的具名反应。</summary>
        public static string DetailText(CampaignState state, string firmwareId)
        {
            if (!FirmwareKinds.TryGetRow(firmwareId, out GameConfig.fg.FirmwareKind row))
            {
                return string.Empty;
            }
            var tags = new List<string>();
            foreach (string t in FirmwareKinds.TagsOf(firmwareId))
            {
                tags.Add(StatusTagCatalog.NameOf(t) ?? t);
            }
            // 与固件库详情同一规则（B21、不剧透）：图鉴里已解锁（发现过）的反应写名字，其余写“？？？（尚未发现）”。
            var reactions = new List<string>();
            foreach (string rid in FirmwareLibrary.ReactionsOf(firmwareId))
            {
                reactions.Add(Progression.MechanicCodex.IsUnlocked(Progression.MechanicCodex.ReactionEntryId(rid))
                    ? NamedReactionCatalog.NameOf(rid)
                    : GameText.Get("fwlib.detail.reaction_unknown"));
            }
            string none = GameText.Get("analysis.restore.none_value");
            // 每行各一个文本键（文本值里不放换行：数据管线按行导出核对）。
            return GameText.Format("analysis.restore.detail.kind", GameText.Get("firmware.category." + row.Category), GameText.Get("firmware.rarity." + row.Rarity)) + "\n"
                   + GameText.Format("analysis.restore.detail.source", GameText.Get(row.AcquireKey)) + "\n"
                   + GameText.Format("analysis.restore.detail.tags", tags.Count > 0 ? string.Join(FirmwareLibrary.Sep, tags) : none) + "\n"
                   + GameText.Format("analysis.restore.detail.reactions", reactions.Count > 0 ? string.Join(FirmwareLibrary.Sep, reactions) : none);
        }

        private static int TuningInt(string id, int fallback) =>
            GridContent.TryGetTuning(id, out float v) && v > 0f && !float.IsInfinity(v) ? (int)Math.Round(v) : fallback;

        public static void ResetForTests()
        {
            RestoredCount = 0;
            RejectedCount = 0;
        }
    }
}
