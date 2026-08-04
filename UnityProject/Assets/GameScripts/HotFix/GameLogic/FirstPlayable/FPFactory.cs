using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.FirstPlayable
{
    /// <summary>
    /// 白模工厂。全部用引擎原始几何体 + Standard 材质，无任何美术资源依赖，
    /// 因此不需要 YooAsset / LoadAssetAsync（Spec §12 允许首版白模走引擎几何体）。
    /// </summary>
    public static class FPFactory
    {
        private static readonly Dictionary<int, Material> MatCache = new Dictionary<int, Material>();
        private static readonly List<Material> Created = new List<Material>();
        private static Shader _shader;
        private static Font _font;

        public static readonly Color ColPlayer = new Color(0.35f, 0.85f, 1.0f);
        public static readonly Color ColFoodA = new Color(0.55f, 0.95f, 0.45f);
        public static readonly Color ColFoodB = new Color(0.95f, 0.85f, 0.30f);
        public static readonly Color ColThreat = new Color(0.95f, 0.35f, 0.35f);
        public static readonly Color ColHazard = new Color(0.75f, 0.30f, 0.85f);
        public static readonly Color ColGround = new Color(0.22f, 0.24f, 0.28f);
        public static readonly Color ColWall = new Color(0.42f, 0.45f, 0.52f);
        public static readonly Color ColHerbivore = new Color(0.60f, 0.80f, 0.55f);
        public static readonly Color ColPredator = new Color(0.95f, 0.50f, 0.30f);
        public static readonly Color ColElite = new Color(0.85f, 0.20f, 0.55f);
        public static readonly Color ColZap = new Color(0.45f, 0.90f, 1.0f);

        /// <summary>中文动态字体。UGUI 内置字体无 CJK 字形，必须走 OS 字体。</summary>
        public static Font CjkFont
        {
            get
            {
                if (_font != null)
                {
                    return _font;
                }
                _font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei", "微软雅黑", "SimHei", "SimSun", "Arial Unicode MS", "Segoe UI" }, 24);
                if (_font == null)
                {
                    _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                }
                return _font;
            }
        }

        private static Shader LitShader
        {
            get
            {
                if (_shader != null)
                {
                    return _shader;
                }
                _shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse")
                    ?? Shader.Find("Unlit/Color");
                return _shader;
            }
        }

        public static Material Mat(Color c)
        {
            int key = ((Color32)c).GetHashCode();
            if (MatCache.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }
            Material m = new Material(LitShader) { color = c };
            if (m.HasProperty("_Glossiness"))
            {
                m.SetFloat("_Glossiness", 0.1f);
            }
            MatCache[key] = m;
            Created.Add(m);
            return m;
        }

        /// <summary>创建无碰撞体的白模（全部判定走距离计算，不用物理）。</summary>
        public static GameObject Primitive(PrimitiveType type, string name, Color color,
            Vector3 pos, float scale, Transform parent)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Collider col = go.GetComponent<Collider>();
            if (col != null)
            {
                Object.Destroy(col);
            }
            go.GetComponent<MeshRenderer>().sharedMaterial = Mat(color);
            Transform t = go.transform;
            t.SetParent(parent, false);
            t.position = pos;
            t.localScale = Vector3.one * scale;
            return go;
        }

        public static GameObject Sphere(string name, Color color, Vector3 pos, float scale, Transform parent)
        {
            return Primitive(PrimitiveType.Sphere, name, color, pos, scale, parent);
        }

        /// <summary>地面 + 四面矮墙。墙只是视觉参考，实际边界靠坐标 Clamp。</summary>
        public static GameObject BuildArena(float halfSize, Transform parent, string name)
        {
            GameObject root = new GameObject(name);
            root.transform.SetParent(parent, false);

            GameObject ground = Primitive(PrimitiveType.Plane, "Ground", ColGround,
                Vector3.zero, 1f, root.transform);
            // Plane 原始尺寸为 10x10 单位
            ground.transform.localScale = new Vector3(halfSize * 0.2f, 1f, halfSize * 0.2f);

            float h = 1.2f;
            CreateWall(root.transform, new Vector3(0f, h * 0.5f, halfSize), new Vector3(halfSize * 2f, h, 0.6f));
            CreateWall(root.transform, new Vector3(0f, h * 0.5f, -halfSize), new Vector3(halfSize * 2f, h, 0.6f));
            CreateWall(root.transform, new Vector3(halfSize, h * 0.5f, 0f), new Vector3(0.6f, h, halfSize * 2f));
            CreateWall(root.transform, new Vector3(-halfSize, h * 0.5f, 0f), new Vector3(0.6f, h, halfSize * 2f));
            return root;
        }

        private static void CreateWall(Transform parent, Vector3 pos, Vector3 size)
        {
            GameObject w = Primitive(PrimitiveType.Cube, "Wall", ColWall, pos, 1f, parent);
            w.transform.localScale = size;
        }

        /// <summary>在场地内随机取点，且与已有点保持最小间距。</summary>
        public static Vector3 RandomPoint(float halfSize, float margin = 2f)
        {
            float r = halfSize - margin;
            return new Vector3(Random.Range(-r, r), 0f, Random.Range(-r, r));
        }

        public static Vector3 RandomPointAwayFrom(float halfSize, Vector3 avoid, float minDist, float margin = 2f)
        {
            for (int i = 0; i < 24; i++)
            {
                Vector3 p = RandomPoint(halfSize, margin);
                if ((p - avoid).sqrMagnitude >= minDist * minDist)
                {
                    return p;
                }
            }
            return RandomPoint(halfSize, margin);
        }

        public static Vector3 ClampToArena(Vector3 pos, float halfSize, float radius)
        {
            float lim = halfSize - radius - 0.3f;
            pos.x = Mathf.Clamp(pos.x, -lim, lim);
            pos.z = Mathf.Clamp(pos.z, -lim, lim);
            return pos;
        }

        public static void ReleaseMaterials()
        {
            for (int i = 0; i < Created.Count; i++)
            {
                if (Created[i] != null)
                {
                    Object.Destroy(Created[i]);
                }
            }
            Created.Clear();
            MatCache.Clear();
        }
    }
}
