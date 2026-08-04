using BinGames.Sim;
using GameLogic.Stats;
using Unity.Mathematics;

namespace GameLogic.Ability.Executors
{
    /// <summary>
    /// 伤害效果执行器。
    ///
    /// 本类是其余执行器的参考样板：
    /// - 只读 EffectSpec 与 EffectContext，不去 Hub 里翻别的系统
    /// - 数值缩放统一走 EffectScaling，不自己写倍率
    /// - 实际伤害提交给 SimBridge，不自己遍历单位（那是内核的活）
    /// </summary>
    public sealed class EffectDealDamage : IEffectExecutor
    {
        public EffectKind Kind => EffectKind.Damage;

        public void Execute(EffectSpec spec, in EffectContext ctx)
        {
            if (ctx.Sim == null || !ctx.Sim.Running)
            {
                return;
            }

            float amount = EffectScaling.Value(spec, in ctx);
            float radius = EffectScaling.Radius(spec, in ctx);

            // 词缀改写：导电系伤害额外吃 ElectricPower
            SimStatus apply = spec.Status;
            int chain = spec.Count;
            float chainRange = math.max(2f, radius);

            if (HasAffix(spec, AffixKind.Conductive))
            {
                apply |= SimStatus.Conductive;
                amount *= EffectScaling.ElectricMul(in ctx);
            }
            if (HasAffix(spec, AffixKind.Corrosive))
            {
                apply |= SimStatus.Corroded;
            }
            if (HasAffix(spec, AffixKind.Mark))
            {
                apply |= SimStatus.Marked;
            }
            if (HasAffix(spec, AffixKind.Crystallize))
            {
                apply |= SimStatus.Crystallized;
            }
            if (HasAffix(spec, AffixKind.Fear))
            {
                apply |= SimStatus.Feared;
            }
            if (HasAffix(spec, AffixKind.Chain) && ctx.Stats != null)
            {
                chain += ctx.Stats.GetInt(StatId.ChainBonus);
            }
            if (HasAffix(spec, AffixKind.Ricochet))
            {
                chain = math.max(chain, 1);
            }

            // 饥饿：生命越低伤害越高（最多 +60%）
            if (HasAffix(spec, AffixKind.Hunger) && ctx.Stats != null)
            {
                float maxHp = ctx.Stats.Get(StatId.MaxHealth);
                float pct = maxHp > 0f ? ctx.Sim.PlayerHealth / maxHp : 1f;
                amount *= 1f + (1f - math.saturate(pct)) * 0.6f;
            }

            // 过载：伤害 +50% 但自损 5% 当前生命
            if (HasAffix(spec, AffixKind.Overload))
            {
                amount *= 1.5f;
                ctx.Sim.DamagePlayer(ctx.Sim.PlayerHealth * 0.05f);
            }

            switch (spec.Shape)
            {
                case EffectShape.Target:
                    if (ctx.TargetIndex >= 0)
                    {
                        ctx.Sim.DamageUnit(ctx.TargetIndex, amount, apply,
                            chain, chainRange, 0.75f, ctx.SourceId);
                    }
                    break;

                case EffectShape.Self:
                case EffectShape.Circle:
                    ctx.Sim.DamageArea(ctx.Origin, math.max(0.2f, radius), amount,
                        spec.TargetFaction, apply, spec.RequireStatus,
                        chain, chainRange, 0.75f, ctx.SourceId);
                    break;

                case EffectShape.Point:
                    ctx.Sim.DamageArea(ctx.Origin + ctx.Direction * spec.Radius,
                        math.max(0.2f, radius), amount,
                        spec.TargetFaction, apply, spec.RequireStatus,
                        chain, chainRange, 0.75f, ctx.SourceId);
                    break;

                case EffectShape.Line:
                case EffectShape.Cone:
                    // 内核只有圆形范围原语。直线/锥形用沿途多个圆近似——
                    // 视觉上足够，且省掉内核里再加一套几何求解。
                    ApplyAlongLine(spec, in ctx, amount, radius, apply, chain, chainRange);
                    break;
            }
        }

        /// <summary>沿朝向铺若干个圆，近似直线/锥形。锥形的圆半径随距离增大。</summary>
        private static void ApplyAlongLine(EffectSpec spec, in EffectContext ctx,
            float amount, float radius, SimStatus apply, int chain, float chainRange)
        {
            float length = math.max(1f, spec.Radius);
            float step = math.max(0.6f, radius * 0.8f);
            int steps = math.clamp((int)(length / step), 1, 24);
            float2 dir = math.normalizesafe(ctx.Direction, new float2(1f, 0f));
            bool cone = spec.Shape == EffectShape.Cone;

            // 分摊伤害，避免一次直线打出 steps 倍伤害
            float per = amount / steps;

            for (int i = 0; i < steps; i++)
            {
                float t = (i + 0.5f) / steps;
                float2 p = ctx.Origin + dir * (length * t);
                float r = cone ? radius * (0.35f + t) : radius;
                ctx.Sim.DamageArea(p, math.max(0.2f, r), per,
                    spec.TargetFaction, apply, spec.RequireStatus,
                    i == 0 ? chain : 0, chainRange, 0.75f, ctx.SourceId);
            }
        }

        internal static bool HasAffix(EffectSpec spec, AffixKind affix)
        {
            if (spec.Affixes == null)
            {
                return false;
            }
            for (int i = 0; i < spec.Affixes.Length; i++)
            {
                if (spec.Affixes[i] == affix)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
