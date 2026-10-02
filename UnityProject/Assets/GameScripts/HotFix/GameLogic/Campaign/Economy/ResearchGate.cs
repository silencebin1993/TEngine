using System;
using GameLogic.Localization;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-11（FGR-ECO-020“需要研究节点”；FG05 FGR-RND-011“槽位：超控阵列的各级”）：研究门槛的唯一判定入口。
    /// 建筑 / 等级的解锁规则写 <c>research:&lt;节点ID&gt;</c>（fg.TbBuildingGrid / fg.TbBuildingTier.unlockRule），<see cref="Grid.BuildCatalog.IsUnlocked(CampaignState, string)"/> 转到这里。
    ///
    /// FG5-RND-01：研发树已开放（<see cref="TreeAvailable"/> = true），门槛按 <see cref="ResearchState.CompletedNodes"/> 判定（由 <see cref="ResearchService"/> 写入）；
    /// 哪个条目要哪个节点由 fg.TbResearchNode.unlocks 决定（改表脚本回写到各条目的 unlockRule）。
    /// 自检里“研发树开放前写成的旧段”用 <see cref="LegacyGatesOpenForTests"/> 按开放前的规则跑（门槛视为已满足，见 ADR-RND-001）；
    /// 研究门槛本身由 FgResearchSelfCheck 与各段的门槛断言在开放状态下覆盖。
    /// 没有逐帧逻辑；判定 O(已完成节点数)，只在建造菜单 / 面板刷新与放置时调用。
    /// </summary>
    public static class ResearchGate
    {
        public const string RulePrefix = "research:";

        /// <summary>自检注入“研发树是否开放”；为 null 时按真实状态（FG5-RND-01 起开放，除非 <see cref="LegacyGatesOpenForTests"/>）。</summary>
        public static Func<bool> TreeAvailableOverrideForTests;

        /// <summary>全量自检里研发树开放前写成的旧段：按开放前的规则（门槛视为已满足）跑。由 CellFrameworkValidate 在整轮开始时打开、结束时关闭；
        /// 不受 <see cref="ResetForTests"/> 影响。真实游戏恒为 false。</summary>
        public static bool LegacyGatesOpenForTests;

        /// <summary>研发树是否已经开放（FG5-RND-01 起 = true）。</summary>
        public static bool TreeAvailable => TreeAvailableOverrideForTests?.Invoke() ?? !LegacyGatesOpenForTests;

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
