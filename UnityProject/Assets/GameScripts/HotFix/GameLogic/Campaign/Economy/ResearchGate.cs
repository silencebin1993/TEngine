using System;
using GameLogic.Localization;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-11（FGR-ECO-020“需要研究节点”；FG05 FGR-RND-011“槽位：超控阵列的各级”）：研究门槛的唯一判定入口。
    /// 建筑 / 等级的解锁规则写 <c>research:&lt;节点ID&gt;</c>（fg.TbBuildingGrid / fg.TbBuildingTier.unlockRule），<see cref="Grid.BuildCatalog.IsUnlocked(CampaignState, string)"/> 转到这里。
    ///
    /// 研发树（研究点、实验室、节点、队列）在 FG5-RND-01：它落地前 <see cref="TreeAvailable"/> = false，门槛视为已满足——与 ADR-LOG-001
    /// “研发树之前不显示永远解不开的条目”同一决定；界面照样写出“研究节点：信号 · 超控阵列 T1（研发树在后续版本开放，现在不需要研究）”，
    /// 让玩家知道以后要研究（DEBT-FG4ECO11-01）。FG5-RND-01 落地时把 <see cref="TreeAvailable"/> 接到研发树，并写 <see cref="ResearchState.CompletedNodes"/>。
    /// 没有逐帧逻辑；判定 O(已完成节点数)，只在建造菜单 / 面板刷新与放置时调用。
    /// </summary>
    public static class ResearchGate
    {
        public const string RulePrefix = "research:";

        /// <summary>自检注入“研发树已经开放”（真门槛）；为 null 时按真实状态（FG5-RND-01 之前 = false）。</summary>
        public static Func<bool> TreeAvailableOverrideForTests;

        /// <summary>研发树是否已经开放（FG5-RND-01 之前恒 false）。</summary>
        public static bool TreeAvailable => TreeAvailableOverrideForTests?.Invoke() ?? false;

        public static bool IsResearchRule(string rule) =>
            rule != null && rule.StartsWith(RulePrefix, StringComparison.Ordinal) && rule.Length > RulePrefix.Length;

        public static string NodeOf(string rule) => IsResearchRule(rule) ? rule.Substring(RulePrefix.Length) : null;

        /// <summary>这个研究节点算不算已完成（研发树开放前一律算）。</summary>
        public static bool IsCompleted(CampaignState state, string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || !TreeAvailable)
            {
                return true;
            }
            string[] done = state?.Research?.CompletedNodes;
            if (done == null)
            {
                return false;
            }
            for (int i = 0; i < done.Length; i++)
            {
                if (string.Equals(done[i], nodeId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>节点的玩家名（文本键 research.node.&lt;节点ID&gt;）。</summary>
        public static string NodeName(string nodeId) => string.IsNullOrEmpty(nodeId) ? string.Empty : GameText.Get("research.node." + nodeId);

        /// <summary>“研究节点：信号 · 超控阵列 T1（……）”：规则不是研究规则时返回空串。</summary>
        public static string Describe(CampaignState state, string rule)
        {
            string node = NodeOf(rule);
            if (node == null)
            {
                return string.Empty;
            }
            if (!TreeAvailable)
            {
                return GameText.Format("research.gate.pending", NodeName(node));
            }
            return GameText.Format(IsCompleted(state, node) ? "research.gate.done" : "research.gate.locked", NodeName(node));
        }

        public static void ResetForTests() => TreeAvailableOverrideForTests = null;
    }
}
