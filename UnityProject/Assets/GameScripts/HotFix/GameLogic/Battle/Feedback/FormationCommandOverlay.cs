using System.Collections.Generic;
using GameLogic.Command.Formation;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// M4-02：编队命令状态的纯 IMGUI 反馈层，屏幕固定位置列表，画法沿用
    /// <see cref="WhiteboxSquadOverlay"/> 的"纯 Debug、无美术"尺度——展示
    /// <see cref="FormationRegistry.AllFormations"/> 里每个编队当前在执行什么、等待队列里排了
    /// 什么，不查询单位坐标、不做世界空间锚点跟随、不碰 <c>SimBridge</c>，也绝不引用任何
    /// <c>MetabolicSlice.*</c> 类型。
    ///
    /// M4-R00-02 队列⑥-25（FC-REQ-022/060"队列/取消/清空必须可见且可操作"）：本类新增队列可视化
    /// + 插队/取消/清空交互，因此**不再是纯只读**——会调用 <see cref="Formation.CancelQueuedCommand"/>
    /// /<see cref="Formation.ClearQueue"/>（仅这两个，不碰 <see cref="Formation.IssueCommand"/> 等
    /// 其它写方法，"插队"由既有 <see cref="SquadCommandSystem"/> 命令入口负责，本类只负责取消侧）。
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
        private const float ButtonWidth = 56f;
        private const float PendingIndent = 16f;

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

            // GUI.Button 会在同一次 OnGUI 里改队列内容——不在遍历 PendingCommands 的 for 循环内
            // 直接调用 Formation 的写方法，先记下这一帧要做哪一个动作，画完再统一执行，
            // 避免"边遍历边删"让后续行的下标读到已经挪位的条目。
            Formation cancelTargetFormation = null;
            int cancelTargetIndex = -1;
            Formation clearTargetFormation = null;

            float y = StartY;
            foreach (Formation formation in _formations.AllFormations)
            {
                FormationCommandEntry active = formation.ActiveCommand;
                IReadOnlyList<FormationCommandEntry> pending = formation.PendingCommands;
                if (active == null && pending.Count == 0)
                {
                    continue;
                }

                if (active != null)
                {
                    Color previous = GUI.color;
                    GUI.color = ColorFor(active.Command.Kind);
                    GUI.Label(new Rect(12f, y, RowWidth, RowHeight),
                        $"{formation.Id}: {active.Command.Kind} [{active.State}]", _labelStyle);
                    GUI.color = previous;
                    y += RowHeight;
                }

                for (int i = 0; i < pending.Count; i++)
                {
                    FormationCommandEntry entry = pending[i];
                    Color previous = GUI.color;
                    GUI.color = ColorFor(entry.Command.Kind);
                    GUI.Label(new Rect(12f + PendingIndent, y, RowWidth - ButtonWidth - PendingIndent, RowHeight),
                        $"  #{i + 1} {entry.Command.Kind} [{entry.State}]", _labelStyle);
                    GUI.color = previous;
                    if (GUI.Button(new Rect(12f + PendingIndent + RowWidth - ButtonWidth, y, ButtonWidth, RowHeight), "取消"))
                    {
                        cancelTargetFormation = formation;
                        cancelTargetIndex = i;
                    }
                    y += RowHeight;
                }

                if (pending.Count > 0)
                {
                    if (GUI.Button(new Rect(12f + PendingIndent, y, RowWidth - PendingIndent, RowHeight), "清空队列"))
                    {
                        clearTargetFormation = formation;
                    }
                    y += RowHeight;
                }
            }

            if (cancelTargetFormation != null)
            {
                cancelTargetFormation.CancelQueuedCommand(cancelTargetIndex);
            }
            if (clearTargetFormation != null)
            {
                clearTargetFormation.ClearQueue();
            }
        }
    }
}
