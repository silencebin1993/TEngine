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
        /// <summary>M4-R00-02 队列③-10（CP-REQ-004）：本次释放时该身体的最后有效朝向
        /// （<see cref="BinGames.Sim.SimSnapshot.BodyForward"/> 原样转发），与 <see cref="AimDirection"/>
        /// 是两个不同概念——瞄准可以临时偏转，朝向是"这具身体面朝哪"的持久状态。</summary>
        public readonly float2 BodyForward;
        public readonly float2 AimDirection;
        /// <summary>M4-R00-02 队列③-10（CP-REQ-003 第③级）：本次释放实际解析出的发射点
        /// （身体中心沿发射方向前推+越界/障碍推出之后的结果）。炮口 VFX/弹体起点应读这个字段，
        /// 不应该自己再算一遍——这正是 CP-REQ-003"必须读同一发射点"的落点。真实器官/底盘挂点
        /// （①②级）未实现，本字段今天恒等于③级兜底公式的结果。</summary>
        public readonly float2 EmitterPosition;
        public readonly SimBodyPartSlot TargetPart;
        public readonly int AttackSequence;

        public EmissionContext(
            SimEntityId sourceEntityId,
            SimFaction sourceFaction,
            ControllerKind controllerKind,
            string organId,
            float2 bodyPosition,
            float bodyRadius,
            float2 bodyForward,
            float2 aimDirection,
            float2 emitterPosition,
            SimBodyPartSlot targetPart,
            int attackSequence)
        {
            SourceEntityId = sourceEntityId;
            SourceFaction = sourceFaction;
            ControllerKind = controllerKind;
            OrganId = organId;
            BodyPosition = bodyPosition;
            BodyRadius = bodyRadius;
            BodyForward = bodyForward;
            AimDirection = aimDirection;
            EmitterPosition = emitterPosition;
            TargetPart = targetPart;
            AttackSequence = attackSequence;
        }
    }
}
