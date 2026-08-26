using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Core;

namespace GameLogic.MetabolicSlice.Environment
{
    /// <summary>
    /// 环境残留反应表（§1.6，≥2 例）。经既有 RegisterReaction 注入，不改 ComposeEngine.Builtin.Catalog——
    /// 环境残留是玩法层语义，不进引擎内置目录。禁止 if (x is Acid && y is Grass) 式判定，只认 tag 集合。
    /// </summary>
    public static class EnvironmentReactionCatalog
    {
        public static void Register(Engine engine)
        {
            engine.RegisterReaction(new ReactionRule(
                "env_fire_wet_steam",
                new[] { "Fire", "Wet" },
                (evt, ctx) =>
                {
                    var next = evt.Clone();
                    next.Wet *= 0.5f;
                    if (next.Wet <= 0f) next.Tags.Remove("Wet");
                    next.Payload["LeaveResidue"] = new List<ResidueDeposit>
                    {
                        new ResidueDeposit("Steam", 1f, 3, ResidueTrigger.OnHit),
                    };
                    return next;
                },
                "火 + 湿地 -> 蒸汽残留：evt.Wet 减半，耗尽则去 Wet tag，留 Steam 残留（GDD §1.6 例1）"));

            engine.RegisterReaction(new ReactionRule(
                "env_oil_fire_burn",
                new[] { "Fire", "Oil" },
                (evt, ctx) =>
                {
                    var next = evt.Clone();
                    next.Burn += 2f;
                    next.Tags.Add("Burning");
                    next.Payload["LeaveResidue"] = new List<ResidueDeposit>
                    {
                        new ResidueDeposit("BurningGround", 1f, 3, ResidueTrigger.OnHit),
                    };
                    return next;
                },
                "先铺油再点火 -> 燃烧区：evt.Burn 增加、打上 Burning，留 BurningGround 残留（GDD §1.6 例2）"));

            // organ-gene-rebalance-v3 story-008 踩坑记录：曾尝试在此追加 Shock+Wet/Frozen+Physical/
            // Acid+SugarFilm 3 条，经 execute_code 实测发现 ComposeEngine.Builtin.Catalog.ReactionCatalog.
            // RegisterDefaults 已注册等价的 8 条内置反应（rx_shock_wet/frozen_physical_to_shatter/
            // rx_acid_sugar 等，MetabolicSliceBridge 构造时两个 Register 都调用），Pipeline.HeatSettleAndReactions
            // 按"初始 tag 集合一次性匹配全部规则"执行——追加同 tag 组合的新规则会与内置规则同批触发，
            // 造成 Damage 二次叠乘等重复结算 bug，故不重复注册，8 条 Tag 涌现断言直接跑内置表（见
            // EmergenceSmoke.cs 注释）。
        }
    }
}
