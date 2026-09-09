using System.Collections.Generic;

namespace GameLogic.Progression
{
    /// <summary>
    /// <see cref="Ability.RuleFlag"/> 的玩家可读短名（ui-visual-overhaul story-007）。
    ///
    /// 为什么需要：规则开关是玩家装到特定卡之后**真的改变了游戏规则**（连吃不再清空、尸体能二次吃…），
    /// 但在此之前全仓没有任何一个 UI/HUD/图鉴读过 <see cref="RuleFlags.Current"/>——规则变了，
    /// 玩家无从知道。这张表是把它搬上屏的最小成本部分：文案直接取 <c>RuleFlag</c> 枚举上
    /// **本来就写好的中文 XML 注释**，不新造世界观用语、不另开配表。
    ///
    /// 只做展示，不参与任何判定；新增枚举值时若漏登记，<see cref="Get"/> 回落成枚举名，
    /// 宁可显示英文也不静默吞掉一条"玩家看不见的规则"。
    /// </summary>
    public static class RuleFlagNames
    {
        private static readonly Dictionary<Ability.RuleFlag, string> _names =
            new Dictionary<Ability.RuleFlag, string>
            {
                [Ability.RuleFlag.CorpseEdible] = "尸体可二次吞噬",
                [Ability.RuleFlag.FailedDevourCorrodes] = "吞噬失败也腐蚀",
                [Ability.RuleFlag.ComboNeverResets] = "连吃不清空",
                [Ability.RuleFlag.DashPierces] = "冲刺穿敌",
                [Ability.RuleFlag.DashLeavesCurrent] = "冲刺留电流",
                [Ability.RuleFlag.CorpseConducts] = "放电沿尸体跳",
                [Ability.RuleFlag.MyceliumBoostsDevour] = "菌毯增益吞噬",
                [Ability.RuleFlag.AutoEscapeOnLowHp] = "濒死自动脱战",
                [Ability.RuleFlag.PollutionBecomesOverlord] = "污染满化霸主",
                [Ability.RuleFlag.MinionsFocusMarked] = "附属体集火标记",
                [Ability.RuleFlag.LargeTargetsSplit] = "大型目标裂成食物",
                [Ability.RuleFlag.ExecuteCausesFear] = "处决引发恐惧",
            };

        /// <summary>取短名；未登记时回落枚举名（不返回空串——空串会让整条规则从 UI 上消失）。</summary>
        public static string Get(Ability.RuleFlag flag)
        {
            return _names.TryGetValue(flag, out string name) ? name : flag.ToString();
        }
    }
}
