using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER2-SCENE-01（2026-09-21 用户裁决"填完再结"补做）：归还谷地占位几何体上的机器
    /// 组件——选中/高亮 + 点选移动指令。这是归还谷地范围内的轻量 RTS 式"选中+下令"实现，直接用
    /// Transform 插值移动，不接 SimBridge/CellPlayerController：后者是"单一意识附体换人"模型
    /// （<c>CellPlayerController.Bind</c> 要求 StatSheet/AbilitySystem/ResourceWallet 这类玩家进化
    /// 属性，勘查确认与"多台平等工作机器"语义不符，见 evidence 文档），不是这里要的东西。
    /// 统一的 Direct/Strategy/Transition/Modal 输入域表、WASD 直控换乘仍是 ER2-INPUT-01 的范围，
    /// 本类只保证"选中的机器能被指哪打哪、到点自动干活"这条 Journey 本身真实可玩。</summary>
    public sealed class HomeValleyMachineMarker : MonoBehaviour
    {
        public int LogicId { get; private set; }
        public string ChassisId { get; private set; }
        public bool IsMoving { get; private set; }

        private Renderer _renderer;
        private Color _baseColor;
        private Vector3? _moveTarget;
        private const float MoveSpeed = 6f;
        private const float ArrivalDistance = 1.2f;
        private static readonly Color SelectedColor = new Color(1f, 0.85f, 0.2f, 1f);

        /// <summary>到达目的地后要自动触发的动作（修复某建筑 / 拆解某残骸）。null 表示纯移动，
        /// 不做任何自动交互——满足"编队/移动"与"工作"是两件独立的事这条语义。</summary>
        public System.Action PendingArrivalAction { get; private set; }

        public void Initialize(int logicId, string chassisId, Renderer renderer, Color baseColor)
        {
            LogicId = logicId;
            ChassisId = chassisId;
            _renderer = renderer;
            _baseColor = baseColor;
            SetSelected(false);
        }

        public void SetSelected(bool selected)
        {
            if (_renderer == null)
            {
                return;
            }
            _renderer.material.color = selected ? SelectedColor : _baseColor;
        }

        public void CommandMoveTo(Vector3 worldTarget, System.Action onArrive = null)
        {
            _moveTarget = worldTarget;
            IsMoving = true;
            PendingArrivalAction = onArrive;
        }

        /// <summary>ER2-INPUT-01：接管开始前调用，清空未完成的点选移动指令——直控（WASD）与
        /// CommandMoveTo 是两条独立的位移来源，同时生效会互相拉扯位置，接管期间只认前者。</summary>
        public void CancelCommandMove()
        {
            _moveTarget = null;
            IsMoving = false;
            PendingArrivalAction = null;
        }

        /// <summary>ER2-INPUT-01：WASD 直控移动。<paramref name="worldDirXZ"/> 已归一化的世界 XZ
        /// 方向（Y 分量忽略）。与 <see cref="CommandMoveTo"/> 用同一个 <see cref="MoveSpeed"/>，
        /// 手感一致——玩家切换"指哪打哪"和"亲自开"不应该感觉像换了台机器。</summary>
        public void DirectMove(Vector3 worldDirXZ, float dt)
        {
            if (worldDirXZ.sqrMagnitude <= 0.0001f)
            {
                return;
            }
            Vector3 pos = transform.position;
            Vector3 step = worldDirXZ.normalized * (MoveSpeed * dt);
            transform.position = new Vector3(pos.x + step.x, pos.y, pos.z + step.z);
        }

        /// <summary>由 <see cref="HomeValleyController.Update"/> 每帧驱动。到达后清空目标并执行
        /// 一次性到达动作（若有），返回值供调用方判断本帧是否发生了到达事件。</summary>
        public bool Tick(float dt)
        {
            if (!_moveTarget.HasValue)
            {
                return false;
            }

            Vector3 target = _moveTarget.Value;
            Vector3 pos = transform.position;
            Vector3 toTarget = target - pos;
            toTarget.y = 0f;

            if (toTarget.sqrMagnitude <= ArrivalDistance * ArrivalDistance)
            {
                transform.position = new Vector3(target.x, pos.y, target.z);
                _moveTarget = null;
                IsMoving = false;
                System.Action action = PendingArrivalAction;
                PendingArrivalAction = null;
                action?.Invoke();
                return true;
            }

            Vector3 step = toTarget.normalized * (MoveSpeed * dt);
            if (step.sqrMagnitude > toTarget.sqrMagnitude)
            {
                step = toTarget;
            }
            transform.position = pos + step;
            return false;
        }
    }
}
