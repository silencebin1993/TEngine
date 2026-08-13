using System.Collections.Generic;
using BinGames.Sim;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 白模障碍可视化（story-009）。
    ///
    /// 障碍是纯静态的——生成后永不移动、永不消失，直到本局结束。不套用
    /// story-007/008 的 Presenter + 池化 + 每帧订阅三段式（<see cref="ZoneVisualPresenter"/>/
    /// <see cref="HealthBarPresenter"/> 那套是为了追踪"会变化的状态"而存在，套在静态数据上是过度设计）。
    /// 一次性 <see cref="Spawn"/> 对应数量的圆形 Mesh，局末 <see cref="Dispose"/> 统一销毁。
    ///
    /// 不透明纯色材质（区别于 <see cref="WhiteboxZoneVisual"/> 的半透明），
    /// 坐标 Y=-0.01f：Zone 圆盘 -0.02 &lt; 障碍 -0.01 &lt; 单位精灵 0 &lt; 血条 +0.02。
    /// </summary>
    public static class WhiteboxObstacleVisual
    {
        private const float DiscY = -0.01f;
        private const int CircleSegments = 20;
        private static readonly Color ObstacleColor = new Color(0.32f, 0.3f, 0.28f, 1f);

        private static GameObject _root;

        /// <summary>一次性生成障碍的白模表现。同局重复调用会先清理旧的。</summary>
        public static void Spawn(ObstacleSpec[] obstacles)
        {
            Dispose();

            if (obstacles == null || obstacles.Length == 0)
            {
                return;
            }

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }
            var matTemplate = new Material(shader) { color = ObstacleColor };

            _root = new GameObject("ObstacleVisual_Root");

            for (int i = 0; i < obstacles.Length; i++)
            {
                ObstacleSpec o = obstacles[i];
                var go = new GameObject($"Obstacle_{i}");
                go.transform.SetParent(_root.transform, false);
                go.transform.localPosition = new Vector3(o.Position.x, DiscY, o.Position.y);
                go.transform.localScale = Vector3.one * (o.Radius * 2f);

                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = BuildCircle(CircleSegments);
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = matTemplate;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
        }

        /// <summary>俯视三角扇圆盘，半径 0.5 单位圆；实际半径靠 localScale 缩放。
        /// 不复用 WhiteboxZoneVisual.BuildCircle：私有方法，且本类要不透明纯色，Zone 是半透明。</summary>
        private static Mesh BuildCircle(int segments)
        {
            var verts = new List<Vector3>(segments + 1) { Vector3.zero };
            var tris = new List<int>(segments * 3);

            for (int i = 0; i <= segments; i++)
            {
                float a = (float)i / segments * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f));
            }

            for (int i = 1; i <= segments; i++)
            {
                tris.Add(0);
                tris.Add(i);
                tris.Add(i + 1);
            }

            var m = new Mesh { name = "ObstacleVisualDisc" };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        public static void Dispose()
        {
            if (_root == null)
            {
                return;
            }

            var renderers = _root.GetComponentsInChildren<MeshRenderer>();
            if (renderers.Length > 0 && renderers[0].sharedMaterial != null)
            {
                Object.Destroy(renderers[0].sharedMaterial);
            }
            var filters = _root.GetComponentsInChildren<MeshFilter>();
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i].sharedMesh != null)
                {
                    Object.Destroy(filters[i].sharedMesh);
                }
            }

            Object.Destroy(_root);
            _root = null;
        }
    }
}
