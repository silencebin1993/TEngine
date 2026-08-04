using Unity.Mathematics;

namespace GameLogic.Ability.Executors
{
    /// <summary>
    /// 持续区域执行器：酸雾、菌毯、导电区一类"放一片东西在地上持续生效"的效果。
    ///
    /// Duration 由 <see cref="Battle.AreaZoneSystem"/> 驱动：本执行器只负责
    /// 把区域登记进去，之后由它按 tick 反复结算伤害与状态。
    /// AreaZoneSystem 缺失时退化为"放置瞬间打一次"，并且不假装它会持续。
    /// </summary>
    public sealed class EffectArea : IEffectExecutor
    {
        public EffectKind Kind => EffectKind.Area;

        public void Execute(EffectSpec spec, in EffectContext ctx)
        {
            if (ctx.Sim == null || !ctx.Sim.Running)
            {
                return;
            }

            float amount = EffectScaling.Value(spec, in ctx);
            float radius = EffectScaling.Radius(spec, in ctx);
            float2 origin = spec.Shape == EffectShape.Point
                ? ctx.Origin + ctx.Direction * spec.Radius
                : ctx.Origin;

            float duration = EffectScaling.Duration(spec, in ctx);
            radius = math.max(0.2f, radius);

            var zones = ctx.Hub?.Get<Battle.AreaZoneSystem>();

            if (zones != null && duration > 0f)
            {
                // 滞留词缀延长存续时间——这是菌毯/毒区类卡牌的主要强化维度
                if (EffectDealDamage.HasAffix(spec, AffixKind.Lingering))
                {
                    duration *= 1.6f;
                }

                zones.Spawn(new Battle.AreaZoneSystem.Zone
                {
                    Center = origin,
                    Radius = radius,
                    TimeLeft = duration,
                    // Value 是总伤害，摊到每个 tick 上
                    DamagePerTick = amount > 0f ? amount * 0.4f : 0f,
                    Status = spec.Status,
                    TargetFaction = spec.TargetFaction,
                    // 自身光环类跟随玩家，放置类留在原地
                    FollowPlayer = spec.Shape == EffectShape.Self,
                    Kind = ResolveKind(spec),
                    SourceId = ctx.SourceId,
                });
                return;
            }

            // 没有区域系统或无持续时间：退化为放置瞬间打一次，不假装会持续
            ctx.Sim.DamageArea(origin, radius, amount,
                spec.TargetFaction, spec.Status, spec.RequireStatus,
                0, math.max(2f, radius), 0.75f, ctx.SourceId);

            if (spec.Status != BinGames.Sim.SimStatus.None)
            {
                ctx.Sim.ApplyStatusArea(origin, radius, spec.Status, true, spec.TargetFaction);
            }
        }

        /// <summary>
        /// 由施加的状态推断区域类型，避免为此在 EffectSpec 上再加一个字段——
        /// 少一个字段就少一处配表出错的机会。
        /// </summary>
        private static Battle.AreaZoneSystem.ZoneKind ResolveKind(EffectSpec spec)
        {
            switch (spec.Status)
            {
                case BinGames.Sim.SimStatus.OnMycelium:
                    return Battle.AreaZoneSystem.ZoneKind.Mycelium;
                case BinGames.Sim.SimStatus.Conductive:
                    return Battle.AreaZoneSystem.ZoneKind.Conductive;
                case BinGames.Sim.SimStatus.Corroded:
                    return Battle.AreaZoneSystem.ZoneKind.Caustic;
                case BinGames.Sim.SimStatus.Slowed:
                    return Battle.AreaZoneSystem.ZoneKind.Roots;
                default:
                    return Battle.AreaZoneSystem.ZoneKind.Generic;
            }
        }
    }
}
