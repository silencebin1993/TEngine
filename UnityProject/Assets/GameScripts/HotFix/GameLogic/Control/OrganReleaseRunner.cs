using BinGames.Sim;
using GameLogic.Battle;
using Unity.Mathematics;

namespace GameLogic.Control
{
    /// <summary>
    /// 一件器官被释放时，**唯一**的落到内核那一步（M2-07）。
    ///
    /// ── 为什么要有这个类 ──
    /// 在它之前，同一具身体有两套战斗真相源：玩家直控它时走器官（弹体 / 扇形 / 区域 / 状态），
    /// 交还给 AI 之后走 <c>BehaviorArchetype.AttackDamage</c> 的瞬时扣血。两条路打出来的东西
    /// 形状不同、数值不同、能不能被躲开也不同——"接管一具身体"因此没有可比较的基准，
    /// 试玩反馈 #5「AI 与直控打法完全不同」说的就是这件事。
    ///
    /// 统一的办法不是把两套参数对齐（那只会变成两套半，且会各自漂移），
    /// 而是让两条路**调用同一个函数**。这个类就是那个函数。
    ///
    /// ── 边界 ──
    /// 它只管"把这一次释放递给内核"，不管：
    /// <list type="bullet">
    /// <item>能不能释放（失能 / 冷却 / 代谢 / 过载）——那是 <see cref="UnitVitalsRegistry"/> 与调用方的闸门；</item>
    /// <item>什么时候释放、朝谁释放——玩家那边是输入，AI 那边是内核的
    ///       <see cref="MinionFireOpportunity"/>；</item>
    /// <item>打出什么形状——那是 <see cref="OrganKernelActionTable"/> 从器官条目推出来的。</item>
    /// </list>
    /// 所以它是**无状态的**：没有字段、没有计时、不记"上一次"。任何想加在这里的状态
    /// 都会立刻变成第三个真相源。
    /// </summary>
    public static class OrganReleaseRunner
    {
        /// <summary>炮口前推距离，避免弹体一出生就和自己的碰撞体重叠。</summary>
        public const float MuzzleClearance = 0.2f;

        /// <summary>区域类器官"丢出去"的距离倍率（相对区域半径）。跟随型不用它。</summary>
        public const float ZoneThrowDistanceMul = 1.5f;

        /// <summary>
        /// 释放一次。返回 false 只有一个含义：**这件器官没有可释放的内核形态**。
        /// 不会退回一发通用弹——那会让所有单位打出同一种东西，"打出来的东西与这具身体一致"
        /// 当场失效，而且"这一发是哪来的"再也追不到源头。
        /// </summary>
        /// <param name="sim">内核桥。</param>
        /// <param name="status">状态系统；为 null 时状态类器官退回 <c>SimBridge.ApplyStatusArea</c>。</param>
        /// <param name="unitIndex">释放者的瞬时槽位（同帧有效）。区域跟随与 LogicId 归属要用。</param>
        /// <param name="origin">释放者位置。</param>
        /// <param name="radius">释放者半径，决定炮口前推。</param>
        /// <param name="act">这件器官的内核动作，由 <see cref="OrganKernelActionTable.Resolve"/> 得到。</param>
        /// <param name="aim">瞄准方向（世界 XZ）。零向量退化为 +X。</param>
        /// <param name="targetFaction">这一击打谁。玩家与友军都打 <see cref="SimFaction.Hostile"/>。</param>
        /// <param name="surgicalAim">弹体是否带精准瞄准语义（手术窗口）。</param>
        /// <param name="targetPart">
        /// 锁定的身体接点（M2-05b）。只有弹体形态用得上；其余形态是范围结算，没有"打哪个接点"可言。
        /// </param>
        public static bool Release(
            SimBridge sim,
            StatusSystem status,
            int unitIndex,
            float2 origin,
            float radius,
            in OrganKernelAction act,
            float2 aim,
            SimFaction targetFaction,
            bool surgicalAim,
            SimBodyPartSlot targetPart = SimBodyPartSlot.None)
        {
            if (sim == null || !act.IsValid)
            {
                return false;
            }

            float2 dir = math.normalizesafe(aim, new float2(1f, 0f));
            int sourceLogicId = ResolveLogicId(sim, unitIndex);

            switch (act.Kind)
            {
                case OrganKernelActionKind.Projectile:
                    sim.FireProjectile(
                        origin + dir * (radius + MuzzleClearance),
                        dir, act.Speed, act.Damage, act.Radius, act.Lifetime, act.Pierce,
                        targetFaction, act.ApplyStatus, sourceLogicId,
                        targetPart: targetPart, surgicalAim: surgicalAim);
                    break;

                case OrganKernelActionKind.Cone:
                    sim.DamageCone(origin, act.Radius, dir, act.HalfAngleDeg, act.Damage,
                        targetFaction, act.ApplyStatus, sourceLogicId: sourceLogicId);
                    break;

                case OrganKernelActionKind.Zone:
                    sim.SpawnZone(
                        act.FollowSelf ? origin : origin + dir * (act.Radius * ZoneThrowDistanceMul),
                        act.Radius, act.Seconds, act.Damage, act.Interval,
                        targetFaction, applyStatus: act.ApplyStatus, sourceLogicId: sourceLogicId,
                        followUnitIndex: act.FollowSelf ? unitIndex : SimConst.InvalidIndex);
                    break;

                case OrganKernelActionKind.Status:
                    if (status != null)
                    {
                        status.ApplyTimedArea(origin, act.Radius, act.ApplyStatus, act.Seconds);
                    }
                    else
                    {
                        sim.ApplyStatusArea(origin, act.Radius, act.ApplyStatus);
                    }
                    break;

                default:
                    return false;
            }

            return true;
        }

        /// <summary>取释放者的 LogicId，供内核把伤害归属回来源。解析不到时用 0（= 无归属）。</summary>
        private static int ResolveLogicId(SimBridge sim, int unitIndex)
        {
            SimSnapshot snapshot = sim.Snapshot;
            return unitIndex >= 0 && unitIndex < snapshot.Count && snapshot.LogicId.IsCreated
                ? snapshot.LogicId[unitIndex]
                : 0;
        }
    }
}
