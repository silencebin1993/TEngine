using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Command;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 选择框、选中标记与命令路径的白模叠加层（M2-02 实施第 4、5 条）。
    ///
    /// 用 <see cref="GL"/> 直接画线而不是建 Mesh/Material 资源：这一层是纯调试可视化，
    /// 走资源模块就要配对加载/释放，给白模阶段增加一整条能泄漏的路径，不值得。
    /// 相机是俯视正交，所以世界空间的矩形在屏幕上就是矩形，选择框不需要额外的屏幕空间处理。
    ///
    /// 每帧代价 O(选择集)，选择集被 <see cref="SimConst.MaxSelectionSize"/> 卡在 64 以内，
    /// 与场上敌人规模无关——不触碰"热更层每帧不得 O(敌人数)"红线。
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class WhiteboxSquadOverlay : MonoBehaviour
    {
        private const float MarkerRadius = 0.9f;
        private const int MarkerSegments = 16;
        private const float GroundY = 0.05f;

        private static readonly Color SelectionBoxColor = new Color(0.35f, 0.95f, 0.55f, 0.9f);
        private static readonly Color SelectedUnitColor = new Color(0.35f, 0.95f, 0.55f, 0.8f);
        private static readonly Color MoveCommandColor = new Color(0.4f, 0.8f, 1f, 0.75f);
        private static readonly Color AttackCommandColor = new Color(1f, 0.4f, 0.35f, 0.8f);
        private static readonly Color GuardCommandColor = new Color(1f, 0.85f, 0.3f, 0.75f);
        private static readonly Color RetreatCommandColor = new Color(0.7f, 0.5f, 1f, 0.75f);

        private SimBridge _sim;
        private SquadCommandSystem _squad;
        private Material _lineMaterial;

        public void Bind(SimBridge sim, SquadCommandSystem squad)
        {
            _sim = sim;
            _squad = squad;
        }

        private void OnDestroy()
        {
            if (_lineMaterial == null)
            {
                return;
            }

            // 同 CellStageFlow.Exit 的理由：Edit 模式下只能 DestroyImmediate。
            if (Application.isPlaying)
            {
                Destroy(_lineMaterial);
            }
            else
            {
                DestroyImmediate(_lineMaterial);
            }
            _lineMaterial = null;
        }

        private void EnsureMaterial()
        {
            if (_lineMaterial != null)
            {
                return;
            }

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                return;
            }

            _lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            // 关掉深度写入但保留绘制：标记要压在地面之上，又不该挡住单位本体。
            _lineMaterial.SetInt("_ZWrite", 0);
        }

        private void OnRenderObject()
        {
            if (_sim == null || !_sim.Running || _squad == null)
            {
                return;
            }

            EnsureMaterial();
            if (_lineMaterial == null)
            {
                return;
            }

            _lineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);

            DrawSelectionBox();
            DrawSelectedUnitsAndCommands();

            GL.End();
            GL.PopMatrix();
        }

        private void DrawSelectionBox()
        {
            if (!_squad.IsDragging)
            {
                return;
            }

            Unity.Mathematics.float2 a = _squad.DragStartWorld;
            Unity.Mathematics.float2 b = _squad.DragCurrentWorld;
            float minX = Mathf.Min(a.x, b.x);
            float maxX = Mathf.Max(a.x, b.x);
            float minZ = Mathf.Min(a.y, b.y);
            float maxZ = Mathf.Max(a.y, b.y);

            GL.Color(SelectionBoxColor);
            var p0 = new Vector3(minX, GroundY, minZ);
            var p1 = new Vector3(maxX, GroundY, minZ);
            var p2 = new Vector3(maxX, GroundY, maxZ);
            var p3 = new Vector3(minX, GroundY, maxZ);
            Line(p0, p1);
            Line(p1, p2);
            Line(p2, p3);
            Line(p3, p0);
        }

        /// <summary>
        /// 选中单位的脚下环 + 它当前命令的指示线。
        ///
        /// 命令线在**直控视角下同样会画**——M2-02 实施第 5 条要求"直控时仍可看见已有编队命令"，
        /// 否则玩家一接管就不知道自己的部队正在执行什么，只能靠记。
        /// </summary>
        private void DrawSelectedUnitsAndCommands()
        {
            var selection = _squad.Selection;
            SimWorld world = _sim.World;
            if (world == null)
            {
                return;
            }

            for (int i = 0; i < selection.Count; i++)
            {
                SimEntityId id = selection[i];
                if (!world.TryGetUnitControlState(id, out SimUnitControlState state) || !state.IsAlive)
                {
                    continue;
                }

                var unitPos = new Vector3(state.Position.x, GroundY, state.Position.y);
                GL.Color(SelectedUnitColor);
                Ring(unitPos, MarkerRadius);

                if (!_sim.TryGetCommand(id, out UnitCommand cmd) || cmd.Kind == UnitCommandKind.None)
                {
                    continue;
                }

                GL.Color(ColorFor(cmd.Kind));

                Vector3 target;
                if (cmd.Kind == UnitCommandKind.Attack)
                {
                    // 攻击命令画到**目标当前位置**而不是下令时的坐标：目标在动，
                    // 画在旧坐标上会让玩家以为命令已经失效。
                    if (!world.TryGetUnitControlState(cmd.TargetEntity, out SimUnitControlState tgt) || !tgt.IsAlive)
                    {
                        continue;
                    }
                    target = new Vector3(tgt.Position.x, GroundY, tgt.Position.y);
                }
                else
                {
                    target = new Vector3(cmd.TargetPosition.x, GroundY, cmd.TargetPosition.y);
                }

                Line(unitPos, target);
                Ring(target, cmd.Kind == UnitCommandKind.Guard ? Mathf.Max(0.5f, cmd.ArriveRadius) : 0.5f);
            }
        }

        private static Color ColorFor(UnitCommandKind kind)
        {
            switch (kind)
            {
                case UnitCommandKind.Attack: return AttackCommandColor;
                case UnitCommandKind.Guard: return GuardCommandColor;
                case UnitCommandKind.Retreat: return RetreatCommandColor;
                default: return MoveCommandColor;
            }
        }

        private static void Line(Vector3 a, Vector3 b)
        {
            GL.Vertex(a);
            GL.Vertex(b);
        }

        private static void Ring(Vector3 center, float radius)
        {
            const float step = Mathf.PI * 2f / MarkerSegments;
            Vector3 prev = center + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= MarkerSegments; i++)
            {
                float a = step * i;
                Vector3 next = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                Line(prev, next);
                prev = next;
            }
        }
    }
}
