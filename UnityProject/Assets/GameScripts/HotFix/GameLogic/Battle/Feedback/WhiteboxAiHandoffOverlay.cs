using BinGames.Sim;
using GameLogic.Control;
using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 接管交还的调试叠加层（M2-04a：里程碑 M2-04 实施第 5 条）。
    ///
    /// 显示的是**单位级行为原型 + 意图来源 + 当前命令 + 当前目标 + 是否在接管缓冲期**。
    /// 里程碑原话写的是"显示当前教义与目标"，但代码里不存在"教义"这个概念
    /// （全仓 Doctrine 零命中）；GDD §8.3 的六种编队教义是**编队级**概念、属 M3 的
    /// <c>PhenotypeTemplate</c> 层。口径差异见 <c>DesignDocs/migration/AI_Handoff_Contract.md</c> §6。
    ///
    /// 画法沿用 <see cref="WhiteboxSquadOverlay"/>：<see cref="GL"/> 直接画线 + IMGUI 写字，
    /// **不新建任何 UI 资源**——走资源模块就要配对加载/释放，给白模调试多一条能泄漏的路径不值得。
    ///
    /// 每帧代价 O(缓冲记录数 ≤ 8 + 1)，与场上单位数无关。默认关闭，F9 开关。
    /// </summary>
    [DefaultExecutionOrder(101)]
    public sealed class WhiteboxAiHandoffOverlay : MonoBehaviour
    {
        /// <summary>调试开关键。F10 已被 CellDebugHud 占用，F11 是压力测试，F12 是相机验证态。</summary>
        public const KeyCode ToggleKey = KeyCode.F9;

        private const float GroundY = 0.06f;
        private const float BufferRingRadius = 1.4f;
        private const int RingSegments = 18;

        private static readonly Color BufferColor = new Color(1f, 0.6f, 0.15f, 0.9f);
        private static readonly Color ControlledColor = new Color(0.4f, 0.9f, 1f, 0.85f);
        private static readonly Color TargetColor = new Color(1f, 0.35f, 0.3f, 0.8f);

        private SimBridge _sim;
        private AiHandoffSystem _handoff;
        private Material _lineMaterial;
        private GUIStyle _style;

        public void Bind(SimBridge sim, AiHandoffSystem handoff)
        {
            _sim = sim;
            _handoff = handoff;
        }

        private void Update()
        {
            if (_handoff == null)
            {
                return;
            }

            // 全局键：战略视角与直控视角下都该能开，不跟着 InputScope 走。
            if (InputRouter.ConsumeGlobalKeyDown(ToggleKey))
            {
                _handoff.DebugOverlayEnabled = !_handoff.DebugOverlayEnabled;
            }
        }

        private void OnDestroy()
        {
            if (_lineMaterial == null)
            {
                return;
            }

            // 同 WhiteboxSquadOverlay：Edit 模式下只能 DestroyImmediate。
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

        private bool Active => _sim != null && _sim.Running && _handoff != null &&
                               _handoff.DebugOverlayEnabled;

        private void OnRenderObject()
        {
            if (!Active)
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

            DrawUnit(_sim.ControlledUnitId, ControlledColor);
            for (int i = 0; i < _handoff.PendingHandoffCount; i++)
            {
                DrawUnit(_handoff.PendingHandoffUnit(i), BufferColor);
            }

            GL.End();
            GL.PopMatrix();
        }

        private void DrawUnit(SimEntityId unit, Color color)
        {
            if (!unit.IsValid)
            {
                return;
            }

            UnitAiDebugInfo info = _handoff.Describe(unit);
            if (!info.Valid)
            {
                return;
            }

            var pos = new Vector3(info.Position.x, GroundY, info.Position.y);
            GL.Color(color);
            Ring(pos, BufferRingRadius);

            if (info.CommandTarget.IsValid && _sim.World != null &&
                _sim.World.TryGetUnitControlState(info.CommandTarget, out SimUnitControlState target) &&
                target.IsAlive)
            {
                GL.Color(TargetColor);
                Line(pos, new Vector3(target.Position.x, GroundY, target.Position.y));
            }
            else if (info.HasCommandTargetPosition && info.Command != UnitCommandKind.None)
            {
                GL.Color(color);
                Line(pos, new Vector3(info.CommandTargetPosition.x, GroundY, info.CommandTargetPosition.y));
            }
        }

        private void OnGUI()
        {
            if (!Active)
            {
                return;
            }

            _style ??= new GUIStyle(GUI.skin.label) { fontSize = 12, richText = false };

            var rect = new Rect(12f, 200f, 460f, 20f);
            GUI.Label(rect, $"[F9] AI 交还调试 · 缓冲中 {_handoff.PendingHandoffCount} · " +
                            $"交还 {_handoff.HandoffCount} / 缓冲 {_handoff.BufferedHandoffCount} / " +
                            $"到期 {_handoff.BufferExpiredCount} / 位置抢救 {_handoff.SafePositionRescueCount}", _style);
            rect.y += 18f;

            rect = DrawLine(rect, _sim.ControlledUnitId, "直控");
            for (int i = 0; i < _handoff.PendingHandoffCount; i++)
            {
                rect = DrawLine(rect, _handoff.PendingHandoffUnit(i), "缓冲");
            }
        }

        private Rect DrawLine(Rect rect, SimEntityId unit, string tag)
        {
            if (!unit.IsValid)
            {
                return rect;
            }

            UnitAiDebugInfo info = _handoff.Describe(unit);
            if (!info.Valid)
            {
                return rect;
            }

            string target = info.CommandTarget.IsValid
                ? $"#{info.CommandTarget.Value}"
                : (info.HasCommandTargetPosition && info.Command != UnitCommandKind.None
                    ? $"({info.CommandTargetPosition.x:F1},{info.CommandTargetPosition.y:F1})"
                    : "-");
            string buffer = info.InHandoffBuffer
                ? $"缓冲 {info.BufferRemaining:F2}s/{info.Continuation}"
                : "-";

            GUI.Label(rect, $"{tag} #{unit.Value} · 原型 {info.Behavior} · 来源 {info.IntentSource} · " +
                            $"命令 {info.Command} · 目标 {target} · {buffer} · 编组 {GroupText(info.GroupMask)}",
                _style);
            rect.y += 18f;
            return rect;
        }

        private static string GroupText(int mask)
        {
            if (mask == 0)
            {
                return "-";
            }

            string text = string.Empty;
            for (int slot = 1; slot <= 9; slot++)
            {
                if ((mask & (1 << slot)) != 0)
                {
                    text += text.Length == 0 ? slot.ToString() : "," + slot;
                }
            }
            return text;
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
            _lineMaterial.SetInt("_ZWrite", 0);
        }

        private static void Line(Vector3 a, Vector3 b)
        {
            GL.Vertex(a);
            GL.Vertex(b);
        }

        private static void Ring(Vector3 center, float radius)
        {
            const float step = Mathf.PI * 2f / RingSegments;
            Vector3 prev = center + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= RingSegments; i++)
            {
                float a = step * i;
                Vector3 next = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                Line(prev, next);
                prev = next;
            }
        }
    }
}
