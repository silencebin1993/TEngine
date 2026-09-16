using BinGames.Sim;
using Unity.Mathematics;

namespace GameLogic.Control
{
    /// <summary>谁在触发这一次器官释放。只允许影响输入权限/瞄准方式/教义许可，
    /// 不允许改变能力本体、资源扣除、冷却或弹道结果（CP-REQ-002/060）。</summary>
    public enum ControllerKind : byte
    {
        /// <summary>玩家直控自己本体。</summary>
        Player = 0,
        /// <summary>玩家直控一具被接管的友军身体。</summary>
        DirectFriendly = 1,
        /// <summary>AI 驱动的友军（未被接管）。</summary>
        AiFriendly = 2,
    }

    /// <summary>
    /// 一次器官释放的不可变发射上下文（M4-R00-02 队列①-1，CP-REQ-002 的 M1-4 最小裁剪版）。
    ///
    /// 只裁剪到本次统一真正用得上的字段：完整字段集（挂点/资源事务ID/友伤规则/暂停时间尺度）
    /// 是后续故事的债务，见 `production/design/m4-r00-02-item1-ability-truth-unification/DESIGN.md` §8。
    /// 当前仅作诊断/回归锚点使用，尚未成为唯一入口（那需要先完成 §5 分阶段计划的后续阶段）。
    /// </summary>
    public readonly struct EmissionContext
    {
        public readonly SimEntityId SourceEntityId;
        public readonly SimFaction SourceFaction;
        public readonly ControllerKind ControllerKind;
        public readonly string OrganId;
        public readonly float2 BodyPosition;
        public readonly float BodyRadius;
        public readonly float2 AimDirection;
        public readonly SimBodyPartSlot TargetPart;
        public readonly int AttackSequence;

        public EmissionContext(
            SimEntityId sourceEntityId,
            SimFaction sourceFaction,
            ControllerKind controllerKind,
            string organId,
            float2 bodyPosition,
            float bodyRadius,
            float2 aimDirection,
            SimBodyPartSlot targetPart,
            int attackSequence)
        {
            SourceEntityId = sourceEntityId;
            SourceFaction = sourceFaction;
            ControllerKind = controllerKind;
            OrganId = organId;
            BodyPosition = bodyPosition;
            BodyRadius = bodyRadius;
            AimDirection = aimDirection;
            TargetPart = targetPart;
            AttackSequence = attackSequence;
        }
    }
}
