using BinGames.Sim;
using GameLogic.Core;
using Unity.Mathematics;

namespace GameLogic.Ability.Executors
{
    /// <summary>
    /// 位移执行器：冲刺/突进/折返。直接改玩家坐标，不走内核的速度积分——
    /// 位移是瞬间挪位置的语义，不该被一帧的物理步长拖慢或被碰撞打断。
    /// </summary>
    public sealed class EffectDash : IEffectExecutor
    {
        public EffectKind Kind => EffectKind.Dash;

        public void Execute(EffectSpec spec, in EffectContext ctx)
        {
            if (ctx.Sim == null || !ctx.Sim.Running)
            {
                return;
            }

            float distance = EffectScaling.Value(spec, in ctx);
            float2 dir = math.normalizesafe(ctx.Direction, new float2(1f, 0f));
            float2 target = ctx.Origin + dir * distance;

            // 场地是以原点为中心的正方形，逐分量夹取比算距离再缩放简单可靠
            float half = ctx.Sim.ArenaHalfExtent;
            target = math.clamp(target, new float2(-half, -half), new float2(half, half));

            ctx.Sim.World?.SetPlayerPosition(target);

            Signals.Publish(new DashSignal
            {
                Direction = dir,
                Distance = distance,
            });

            // 贯穿：冲刺途中短暂无敌，避免冲进敌群被瞬间集火打死。
            // "短暂"目前只是加个状态位，多久算完全靠 StatusSystem 自己到期移除——
            // 这里没有计时器，如果 StatusSystem 一直不落地，这个无敌会一直挂着。
            if (EffectDealDamage.HasAffix(spec, AffixKind.Pierce))
            {
                ctx.Sim.ApplyStatusUnit(SimConst.PlayerIndex, SimStatus.Invulnerable, true);
            }
        }
    }
}
