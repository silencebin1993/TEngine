using BinGames.Sim;
using GameLogic.Stats;
using Unity.Mathematics;

namespace GameLogic.Ability.Executors
{
    /// <summary>
    /// 投射物执行器。多发时用扇形均匀散开，而不是全部叠在同一条线上——
    /// 否则"数量加成"在视觉和判定上都等于没加。
    /// </summary>
    public sealed class EffectProjectile : IEffectExecutor
    {
        /// <summary>每多一发增加的散开角度（弧度）。数量越多散得越开，单发时不偏转。</summary>
        private const float FanSpreadPerShot = 0.18f;

        public EffectKind Kind => EffectKind.Projectile;

        public void Execute(EffectSpec spec, in EffectContext ctx)
        {
            if (ctx.Sim == null || !ctx.Sim.Running)
            {
                return;
            }

            float damage = EffectScaling.Value(spec, in ctx);
            float radius = EffectScaling.Radius(spec, in ctx);
            int count = math.max(1, spec.Count);
            if (ctx.Stats != null)
            {
                count += ctx.Stats.GetInt(StatId.ProjectileCount);
            }

            // 贯穿：提高穿透目标数而不是简单"更强"，符合词缀本身语义
            int pierce = EffectDealDamage.HasAffix(spec, AffixKind.Pierce) ? 3 : 1;

            SimStatus applyStatus = spec.Status;
            if (EffectDealDamage.HasAffix(spec, AffixKind.Conductive))
            {
                applyStatus |= SimStatus.Conductive;
            }

            float2 dir = math.normalizesafe(ctx.Direction, new float2(1f, 0f));
            float baseAngle = math.atan2(dir.y, dir.x);
            float totalSpread = FanSpreadPerShot * (count - 1);
            float startAngle = baseAngle - totalSpread * 0.5f;

            for (int i = 0; i < count; i++)
            {
                float angle = count == 1 ? baseAngle : startAngle + FanSpreadPerShot * i;
                float2 shotDir = new float2(math.cos(angle), math.sin(angle));

                ctx.Sim.FireProjectile(ctx.Origin, shotDir, 12f, damage,
                    math.max(0.1f, radius), 2.5f, pierce,
                    spec.TargetFaction, applyStatus, ctx.SourceId);
            }
        }
    }
}
