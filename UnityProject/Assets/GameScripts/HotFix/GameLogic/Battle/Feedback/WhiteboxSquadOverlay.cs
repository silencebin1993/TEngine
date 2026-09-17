using BinGames.Sim;
using GameLogic.Battle;
using GameLogic.Command;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;
using Formation = GameLogic.Command.Formation.Formation;
using FormationCommandEntry = GameLogic.Command.Formation.FormationCommandEntry;
using FormationCommandFailReason = GameLogic.Command.Formation.FormationCommandFailReason;
using FormationCommandState = GameLogic.Command.Formation.FormationCommandState;
using FormationRegistry = GameLogic.Command.Formation.FormationRegistry;

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
        /// <summary>M4-R00-02 队列⑥-26（FC-REQ-061）：战略视角标记"当前被直控成员"的颜色。
        /// 刻意与 <see cref="SelectedUnitColor"/>（绿色，代表"选中了"）区分开——直控标记与选择
        /// 是两个独立维度：可能选中了别人却仍在直控这一个，也可能直控这个但没选中它。</summary>
        private static readonly Color ControlledMarkerColor = new Color(1f, 0.85f, 0.15f, 0.95f);
        private const float ControlledMarkerRadius = 1.3f;

        private SimBridge _sim;
        private SquadCommandSystem _squad;
        private FormationRegistry _formations;
        private Material _lineMaterial;
        private GUIStyle _partLabelStyle;
        private GUIStyle _directFormationStyle;

        /// <summary><paramref name="formations"/> 供 FC-REQ-061 直控 HUD 查"当前受控单位属于
        /// 哪个编队"；可为 null（历史调用点/未接编队系统的测试场景），此时直控编队信息区块不显示，
        /// 其余既有行为不受影响。</summary>
        public void Bind(SimBridge sim, SquadCommandSystem squad, FormationRegistry formations = null)
        {
            _sim = sim;
            _squad = squad;
            _formations = formations;
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
            DrawControlledUnitMarker();

            GL.End();
            GL.PopMatrix();
        }

        /// <summary>
        /// M2-05b 白模反馈：RTS 的 P 键会循环攻击接点类别，必须把当前值显示出来，
        /// 否则输入虽然生效，玩家却无法知道下一条 Attack 会打哪里。
        /// 只在战略输入域显示，不创建任何 UI 资源。
        /// </summary>
        private void OnGUI()
        {
            if (_sim == null || !_sim.Running || _squad == null)
            {
                return;
            }

            if (InputRouter.Owns(InputScope.Strategy))
            {
                _partLabelStyle ??= new GUIStyle(GUI.skin.box)
                {
                    fontSize = 14,
                    alignment = TextAnchor.MiddleLeft,
                };
                GUI.Box(new Rect(12f, 164f, 330f, 28f),
                    $"[P] RTS 攻击接点：{PartLabel(_squad.PendingAttackPart)}", _partLabelStyle);
            }

            DrawDirectFormationBlock();
        }

        /// <summary>
        /// M4-R00-02 队列⑥-26（FC-REQ-061 战略/直控连续性）：直控视角下补显示所属编队的目标/
        /// 汇合方向/失败提示——玩家接管一具身体后镜头切走，不该因此看不到自己部队在干什么。
        /// </summary>
        private void DrawDirectFormationBlock()
        {
            if (_formations == null || !InputRouter.Owns(InputScope.Direct))
            {
                return;
            }

            SimWorld world = _sim.World;
            SimEntityId controlled = _sim.ControlledUnitId;
            if (world == null || !controlled.IsValid ||
                !world.TryGetUnitControlState(controlled, out SimUnitControlState state) || !state.IsAlive)
            {
                return;
            }

            Formation formation = _formations.FindFormationContaining(controlled);
            string text = BuildDirectFormationText(formation, world, state.Position);
            if (text == null)
            {
                // 直控单位不属于任何编队：不画这个区块，不是画一行"无编队"——
                // 大多数玩家旅程里直控单位从未被编过组，常驻一行空文案只会添乱。
                return;
            }

            _directFormationStyle ??= new GUIStyle(GUI.skin.box)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft,
            };
            GUI.Box(new Rect(12f, 196f, 460f, 28f), text, _directFormationStyle);
        }

        /// <summary>
        /// M4-R00-02 队列⑥-26：战略视角下标记"当前被直控成员"——与选中状态无关，选没选它都画。
        /// 直控视角下不画（那一刻镜头本身就锁在它身上，标记自己没有意义）。
        /// </summary>
        private void DrawControlledUnitMarker()
        {
            if (!InputRouter.Owns(InputScope.Strategy))
            {
                return;
            }

            SimWorld world = _sim.World;
            SimEntityId controlled = _sim.ControlledUnitId;
            if (world == null || !controlled.IsValid ||
                !world.TryGetUnitControlState(controlled, out SimUnitControlState state) || !state.IsAlive)
            {
                return;
            }

            GL.Color(ControlledMarkerColor);
            Ring(new Vector3(state.Position.x, GroundY, state.Position.y), ControlledMarkerRadius);
        }

        private static string PartLabel(SimBodyPartSlot slot)
        {
            return slot switch
            {
                SimBodyPartSlot.Primary => "Primary（主接点类别）",
                SimBodyPartSlot.Secondary => "Secondary（次接点类别）",
                _ => "整体（不指定）",
            };
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

        /// <summary>
        /// M4-R00-02 队列⑥-26（FC-REQ-061）：直控 HUD 编队信息文案。纯函数，供自检直接断言，
        /// 不强制走真实 OnGUI 渲染管线测试（同 <see cref="FormationCommandOverlay.ColorFor"/>
        /// 的既有先例）。<paramref name="formation"/> 为 null 时返回 null（不属于任何编队，
        /// 调用方据此决定"不画这个区块"而不是"画一行空文案"）。
        ///
        /// **"断粮"/"载荷被抢"两类关键失败提示不在本方法覆盖范围**——核实过全仓
        /// Endurance/续航/Carry/载荷均是死代码占位或压根不存在（见⑤-23核实结论、
        /// `SharedCapabilityCatalog.Carry=Placeholder`），没有数据可读，属于对应机制落地后
        /// 再补的债，不在本项编造假数据。
        /// </summary>
        public static string BuildDirectFormationText(Formation formation, SimWorld world, float2 controlledWorldPos)
        {
            if (formation == null)
            {
                return null;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append($"编队 {formation.Id}");

            FormationCommandEntry active = formation.ActiveCommand;
            if (active != null)
            {
                sb.Append($"　命令 {active.Command.Kind}[{active.State}]");
                if (active.State == FormationCommandState.Failed || active.State == FormationCommandState.Interrupted)
                {
                    sb.Append($"　原因 {FailReasonLabel(active.FailReason)}");
                }
            }
            else
            {
                sb.Append("　当前空闲");
            }

            if (world != null && TryComputeRallyDirection(formation, world, controlledWorldPos, out float bearingDeg))
            {
                // 四舍五入到整数度后再取模：浮点噪声可能让"正北偏一点点负角"算出 359.6xx°，
                // :F0 直接格式化会显示"360°"而不是"0°"——360 不是一个合法的罗盘读数，
                // 玩家会读成"这是哪个方向"。
                int bearingDegRounded = Mathf.RoundToInt(bearingDeg) % 360;
                sb.Append($"　汇合方向 {bearingDegRounded}°");
            }

            return sb.ToString();
        }

        /// <summary>汇合方向：编队有生效中的命令时指向命令目标（单位目标用其当前实时位置，
        /// 不是下令时的旧坐标，同 <see cref="DrawSelectedUnitsAndCommands"/> 对 Attack 目标的口径）；
        /// 编队空闲/没有可解析目标时指向编队成员的平均位置——玩家离开编队后最想知道"我的队伍在哪"。
        /// 角度按 0°=北（世界+Z）、顺时针增大计（俯视地面，+X 东、+Z 北，符合玩家读罗盘方位的直觉）。</summary>
        private static bool TryComputeRallyDirection(Formation formation, SimWorld world, float2 fromPos, out float bearingDeg)
        {
            bearingDeg = 0f;

            FormationCommandEntry active = formation.ActiveCommand;
            if (active != null && active.State == FormationCommandState.Active)
            {
                if (active.Command.TargetEntity.HasValue &&
                    world.TryGetUnitControlState(active.Command.TargetEntity.Value, out SimUnitControlState tgt) && tgt.IsAlive)
                {
                    return SetBearing(fromPos, tgt.Position, out bearingDeg);
                }
                if (active.Command.TargetPosition.HasValue)
                {
                    return SetBearing(fromPos, active.Command.TargetPosition.Value, out bearingDeg);
                }
            }

            return TryComputeMemberAveragePosition(formation, world, out float2 average)
                && SetBearing(fromPos, average, out bearingDeg);
        }

        private static bool TryComputeMemberAveragePosition(Formation formation, SimWorld world, out float2 average)
        {
            average = float2.zero;
            int count = 0;
            foreach (SimEntityId member in formation.Members)
            {
                if (!world.TryGetUnitControlState(member, out SimUnitControlState state) || !state.IsAlive)
                {
                    continue;
                }
                average += state.Position;
                count++;
            }

            if (count == 0)
            {
                return false;
            }
            average /= count;
            return true;
        }

        private static bool SetBearing(float2 fromPos, float2 toPos, out float bearingDeg)
        {
            float2 delta = toPos - fromPos;
            if (math.lengthsq(delta) < 0.0001f)
            {
                // 目标就在脚下：没有方向可言，不给一个抖动的随机角度误导玩家。
                bearingDeg = 0f;
                return false;
            }
            bearingDeg = math.degrees(math.atan2(delta.x, delta.y));
            if (bearingDeg < 0f)
            {
                bearingDeg += 360f;
            }
            return true;
        }

        private static string FailReasonLabel(FormationCommandFailReason reason)
        {
            return reason switch
            {
                FormationCommandFailReason.InvalidTarget => "目标失效",
                FormationCommandFailReason.Cancelled => "已取消",
                FormationCommandFailReason.PreemptedByOverride => "被新命令覆盖",
                FormationCommandFailReason.Stuck => "卡死",
                FormationCommandFailReason.NoValidAnchor => "找不到有效锚点",
                _ => "未知",
            };
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
