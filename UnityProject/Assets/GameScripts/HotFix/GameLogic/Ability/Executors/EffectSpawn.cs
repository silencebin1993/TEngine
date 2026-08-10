using BinGames.Sim;
using GameLogic.Spawning;
using GameLogic.Stats;
using Unity.Mathematics;

namespace GameLogic.Ability.Executors
{
    /// <summary>
    /// 生成执行器：孢子、分身、卵鞘一类"造出友方单位"的效果。
    ///
    /// 内核只认 SpawnRequest，不知道"附属体"是什么概念——热更层负责把
    /// EffectSpec 翻成具体的生成参数，逻辑 id 由 SimBridge 统一分配，
    /// 避免多处各自维护计数器导致 id 冲突。
    /// </summary>
    public sealed class EffectSpawn : IEffectExecutor
    {
        public EffectKind Kind => EffectKind.Spawn;

        public void Execute(EffectSpec spec, in EffectContext ctx)
        {
            if (ctx.Sim == null || !ctx.Sim.Running)
            {
                return;
            }

            int count = math.max(1, spec.Count);
            if (EffectDealDamage.HasAffix(spec, AffixKind.Proliferate))
            {
                count *= 2;
            }

            // MinionCap 限制"场上存活附属体总数"：向 MinionRegistry 申请配额，
            // 超出上限的部分直接裁掉（含 0），不做"回收最老附属体腾位"的驱逐逻辑。
            int minionCap = (int)(ctx.Stats?.Get(StatId.MinionCap) ?? 0f);
            MinionRegistry minions = ctx.Hub?.Get<MinionRegistry>();
            if (minions != null)
            {
                int granted = minions.Reserve(count, minionCap);
                if (granted < count)
                {
                    TEngine.Log.Info(
                        $"[EffectSpawn] MinionCap 已满，本次生成从 {count} 裁剪到 {granted}（cap={minionCap}）。");
                }
                count = granted;
            }

            if (count <= 0)
            {
                return;
            }

            float health = spec.Value > 0f ? spec.Value : 1f;
            float radius = spec.Radius > 0f ? spec.Radius : 0.4f;

            for (int i = 0; i < count; i++)
            {
                // 环形散开，避免所有单位重叠生成在同一点相互推挤
                float angle = (math.PI * 2f / count) * i;
                float2 offset = new float2(math.cos(angle), math.sin(angle)) * radius * 2f;

                ctx.Sim.Spawn(new SpawnRequest
                {
                    Position = ctx.Origin + offset,
                    Velocity = float2.zero,
                    Health = health,
                    Radius = radius,
                    MaxSpeed = 4f,
                    ArchetypeId = spec.SpawnEnemyId,
                    Faction = SimFaction.PlayerMinion,
                    InitialStatus = spec.Status,
                    LogicId = ctx.Sim.NextLogicId(),
                    VisualId = spec.SpawnEnemyId,
                });
            }
        }
    }
}
