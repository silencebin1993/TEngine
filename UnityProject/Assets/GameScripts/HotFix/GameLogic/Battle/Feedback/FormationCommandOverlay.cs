using System.Collections.Generic;
using GameLogic.Command.Formation;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// M4-02：编队命令状态的纯 IMGUI 反馈层，屏幕固定位置列表，画法沿用
    /// <see cref="WhiteboxSquadOverlay"/> 的"纯 Debug、无美术"尺度——只读展示
    /// <see cref="FormationRegistry.AllFormations"/> 里每个有 <see cref="Formation.ActiveCommand"/>
    /// 的编队当前在执行什么、处于什么状态，不查询单位坐标、不做世界空间锚点跟随、不碰
    /// <c>SimBridge</c>，也绝不引用任何 <c>MetabolicSlice.*</c> 类型或调用
    /// <see cref="Formation"/>/<see cref="FormationRegistry"/> 任何写方法。
    ///
    /// 8 种 <see cref="FormationCommand.CommandKind"/> 各配一个颜色常量：在
    /// <see cref="WhiteboxSquadOverlay"/> 已有的 Move/Attack/Guard/Retreat 四色基础上，为
    /// OrganCategory/Carry/Occupy/Ambush 补四种新颜色，8 种互不重复。颜色查表暴露为
    /// <see cref="ColorFor"/>/<see cref="KindColors"/>（public static），供自检直接断言覆盖面，
    /// 不强制走真实 <c>OnGUI</c> 渲染管线测试。
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class FormationCommandOverlay : MonoBehaviour
    {
        private const float StartY = 200f;
        private const float RowHeight = 18f;
        private const float RowWidth = 360f;

        private static readonly Color MoveColor = new Color(0.4f, 0.8f, 1f, 0.75f);
        private static readonly Color AttackColor = new Color(1f, 0.4f, 0.35f, 0.8f);
        private static readonly Color GuardColor = new Color(1f, 0.85f, 0.3f, 0.75f);
        private static readonly Color RetreatColor = new Color(0.7f, 0.5f, 1f, 0.75f);
        private static readonly Color OrganCategoryColor = new Color(1f, 0.55f, 0.85f, 0.8f);
        private static readonly Color CarryColor = new Color(0.6f, 0.9f, 0.3f, 0.8f);
        private static readonly Color OccupyColor = new Color(0.95f, 0.65f, 0.15f, 0.8f);
        private static readonly Color AmbushColor = new Color(0.55f, 0.55f, 0.55f, 0.85f);

        /// <summary>8 种 CommandKind 的颜色查表，public 供 [34] 自检直接断言覆盖面。</summary>
        public static readonly IReadOnlyDictionary<FormationCommand.CommandKind, Color> KindColors =
            new Dictionary<FormationCommand.CommandKind, Color>
            {
                { FormationCommand.CommandKind.Move, MoveColor },
                { FormationCommand.CommandKind.Attack, AttackColor },
                { FormationCommand.CommandKind.Guard, GuardColor },
                { FormationCommand.CommandKind.Retreat, RetreatColor },
                { FormationCommand.CommandKind.OrganCategory, OrganCategoryColor },
                { FormationCommand.CommandKind.Carry, CarryColor },
                { FormationCommand.CommandKind.Occupy, OccupyColor },
                { FormationCommand.CommandKind.Ambush, AmbushColor },
            };

        private FormationRegistry _formations;
        private GUIStyle _labelStyle;

        public void Bind(FormationRegistry formations)
        {
            _formations = formations;
        }

        public static Color ColorFor(FormationCommand.CommandKind kind)
        {
            return KindColors.TryGetValue(kind, out Color color) ? color : Color.white;
        }

        private void OnGUI()
        {
            if (_formations == null)
            {
                return;
            }

            _labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 13 };

            float y = StartY;
            foreach (Formation formation in _formations.AllFormations)
            {
                FormationCommandEntry active = formation.ActiveCommand;
                if (active == null)
                {
                    continue;
                }

                Color previous = GUI.color;
                GUI.color = ColorFor(active.Command.Kind);
                GUI.Label(new Rect(12f, y, RowWidth, RowHeight),
                    $"{formation.Id}: {active.Command.Kind} [{active.State}]", _labelStyle);
                GUI.color = previous;
                y += RowHeight;
            }
        }
    }
}
