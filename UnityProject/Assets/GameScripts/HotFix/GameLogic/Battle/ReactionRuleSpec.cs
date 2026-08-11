using BinGames.Sim;
using GameLogic.Ability;

namespace GameLogic.Battle
{
    /// <summary>
    /// 反应矩阵规则定义。StatusA/StatusB 是无序对——
    /// <see cref="ReactionSystem"/> 按两者组合查表，与施加顺序无关。
    ///
    /// 例外：晶化"3 层×任意物理命中"规则（<c>StatusB == SimStatus.None</c>）不走这套
    /// 无序对查表，而是由 <see cref="ReactionSystem.OnTargetHit"/> 直接按 id 取用，
    /// 见 Cell_Stage_Spec.md §17.1 与 preflight Decision H2。
    ///
    /// ResultEffect 复用现有 <see cref="EffectSpec"/> 结构，通过
    /// <see cref="AbilitySystem.RunEffect"/> 执行——反应不新增执行链路。
    /// </summary>
    public sealed class ReactionRuleSpec
    {
        public int Id;
        public string Name;
        public string Desc;

        public SimStatus StatusA;
        public SimStatus StatusB;

        /// <summary>触发后是否清除 StatusA（相对 StatusA/StatusB 声明顺序，非命中顺序）。</summary>
        public bool ConsumeA;
        /// <summary>触发后是否清除 StatusB。</summary>
        public bool ConsumeB;

        /// <summary>同一目标再次触发本规则的最短间隔（秒）。</summary>
        public float Cooldown;

        public EffectSpec ResultEffect;
    }
}
