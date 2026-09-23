using System;

namespace GameLogic.Campaign.Content
{
    /// <summary>ER4-BLP-01 STORY-EXECUTION-CARDS.md 第1条："内容目录区分已解锁、已携回待解析、未知，
    /// 未解锁不可选"。三态判定的唯一实现——蓝图编辑器（外层槽 Chassis/Primary/Utility/Structure/固件）
    /// 与电路板芯片装配都应通过本类判断"这条内容玩家现在能不能选"，不在各调用点各写一套解锁规则。</summary>
    public enum ContentUnlockState
    {
        /// <summary>可直接装配。<see cref="MechanicalContentSource.BaseBlueprint"/>（基础蓝图库，开局即有）、
        /// <see cref="MechanicalContentSource.Composite"/>（反应，非独立拾取，不占任何槽位）与
        /// <see cref="MechanicalContentSource.AlwaysBuiltHomeValley"/>（归还谷地开局建筑）恒为已解锁；
        /// 其余类别需要 <see cref="CampaignState.UnlockedContentIds"/> 命中对应内容 ID。</summary>
        Unlocked,

        /// <summary>已从地面/区域带回原始模块，但尚未经解析台确认——DEMO-CONTENT-LOCK.md §"回城解析"：
        /// "模块在仓库/搬运中/解析中/已解锁的不同状态"。本 Story 落地判定函数与判定依据
        /// （<see cref="CampaignState.EventLedger"/> 的 <see cref="PendingAnalysisEventId"/> 标记），
        /// 但落地真实"带回→解析台排队→解锁"完整流程属于 ER6-ANA-01（未归档模块与解析台，已在
        /// STORY-BOARD.md 排期）——该 Story 上线前，写入该标记的生产入口尚不存在，玩家真实路径下
        /// 这一分支恒不可达，不是本 Story 的缺口。</summary>
        RetrievedPendingAnalysis,

        /// <summary>玩家尚未接触过的内容——未解锁且无携回标记。</summary>
        Unknown,
    }

    public static class MechanicalContentUnlock
    {
        /// <summary>content 是否"现在可以选/装配"。找不到内容定义、或 <paramref name="contentId"/> 为空
        /// 一律不可选（防御性拒绝，不是"默认放行"）。</summary>
        public static bool IsUnlocked(CampaignState state, string contentId)
        {
            if (string.IsNullOrEmpty(contentId))
            {
                return false;
            }
            if (!MechanicalContentFacade.TryGet(contentId, out MechanicalContentDef def))
            {
                return false;
            }
            if (def.Source == MechanicalContentSource.BaseBlueprint
                || def.Source == MechanicalContentSource.Composite
                || def.Source == MechanicalContentSource.AlwaysBuiltHomeValley)
            {
                return true;
            }
            if (state?.UnlockedContentIds == null)
            {
                return false;
            }
            return Array.IndexOf(state.UnlockedContentIds, contentId) >= 0;
        }

        /// <summary>三态分类，供 UI 展示区分（编辑器内容目录分栏、锁定提示文案）。</summary>
        public static ContentUnlockState Classify(CampaignState state, string contentId)
        {
            if (IsUnlocked(state, contentId))
            {
                return ContentUnlockState.Unlocked;
            }
            return HasPendingAnalysisMarker(state, contentId)
                ? ContentUnlockState.RetrievedPendingAnalysis
                : ContentUnlockState.Unknown;
        }

        /// <summary>ER5/ER6 区域解析流程完成后应写入的 EventLedger 标记 ID 约定——"带回但未解析"这一刻
        /// 追加一条 <c>EventLedgerEntry{ EventId = PendingAnalysisEventId(contentId), Category =
        /// "ContentPendingAnalysis" }</c>；解析台确认解锁（写入 <see cref="CampaignState.UnlockedContentIds"/>）
        /// 那一刻不需要移除本标记——<see cref="IsUnlocked"/> 优先于 <see cref="HasPendingAnalysisMarker"/>
        /// 判定，一旦解锁分类结果自动跳过待解析分支，标记留档不影响正确性。</summary>
        public static string PendingAnalysisEventId(string contentId) => $"content_pending_analysis:{contentId}";

        private static bool HasPendingAnalysisMarker(CampaignState state, string contentId)
        {
            if (state?.EventLedger == null || string.IsNullOrEmpty(contentId))
            {
                return false;
            }
            string marker = PendingAnalysisEventId(contentId);
            foreach (EventLedgerEntry entry in state.EventLedger)
            {
                if (entry != null && string.Equals(entry.EventId, marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
