using System.Collections.Generic;
using GameLogic.Campaign.WorldSim;
using GameLogic.View;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG9-ACT1-01：远征地点里生成内容的占位表现（用户 2026-09-30：美术先用最简占位，美术阶段整体替换；DEBT-FG9ACT101-01）。
    /// 悬崖 = 深褐色方块、水面 = 蓝色薄板、废墟碎石 = 灰色矮块（可以穿过）、驻军据点 = 红色地面圈 + 阵营色立柱、熔渣热区 = 橙色半透明圆盘；
    /// 同类合并成一张网格（一次绘制），只在地点被观察时建、离开观察时随根节点一起释放。
    /// </summary>
    public static class SiteGenVisuals
    {
        private static Mesh _cube;
        private static Mesh _cylinder;

        public const string RootName = "[SiteGenContent]";

        /// <summary>自检读点：上一次建了几个障碍 / 碎石 / 驻军标记 / 热区。</summary>
        public static int LastObstacles { get; private set; }
        public static int LastRubble { get; private set; }
        public static int LastGarrisonMarkers { get; private set; }
        public static int LastSlagDiscs { get; private set; }

        public static GameObject Build(Transform parent, CampaignState s, SiteGenContent c)
        {
            if (parent == null || c == null)
            {
                return null;
            }
            EnsureMeshes();
            var root = new GameObject(RootName);
            root.transform.SetParent(parent, false);

            var cliff = new List<Matrix4x4>(c.Obstacles.Count);
            var water = new List<Matrix4x4>(c.Obstacles.Count);
            for (int i = 0; i < c.Obstacles.Count; i++)
            {
                Vector3 o = c.Obstacles[i];
                bool isWater = Grid.GridContent.TerrainByCode(c.ObstacleTerrain[i])?.Id == "water";
                float d = o.z * 2f;
                if (isWater)
                {
                    water.Add(Matrix4x4.TRS(new Vector3(o.x, 0.02f, o.y), Quaternion.identity, new Vector3(d, 0.04f, d)));
                }
                else
                {
                    cliff.Add(Matrix4x4.TRS(new Vector3(o.x, 1.1f, o.y), Quaternion.identity, new Vector3(d, 2.2f, d)));
                }
            }
            var rubble = new List<Matrix4x4>(c.Rubble.Count);
            foreach (Vector2 p in c.Rubble)
            {
                rubble.Add(Matrix4x4.TRS(new Vector3(p.x, 0.25f, p.y), Quaternion.Euler(0f, (p.x * 37f + p.y * 17f) % 90f, 0f), new Vector3(1.4f, 0.5f, 1.1f)));
            }
            Merged(root.transform, "Cliffs", _cube, cliff, new Color(0.23f, 0.2f, 0.18f));
            Merged(root.transform, "Water", _cube, water, new Color(0.24f, 0.47f, 0.7f));
            Merged(root.transform, "Rubble", _cube, rubble, new Color(0.48f, 0.48f, 0.45f));

            BuildGarrisonMarkers(root.transform, s, c);
            root.AddComponent<SiteGenMarkerStamp>().Routed = SiteGarrisonService.RoutedGarrisons;

            var slag = new List<Matrix4x4>(c.SlagZones.Count);
            foreach (Vector3 z in c.SlagZones)
            {
                slag.Add(Matrix4x4.TRS(new Vector3(z.x, 0.025f, z.y), Quaternion.identity, new Vector3(z.z * 2f, 0.02f, z.z * 2f)));
            }
            Merged(root.transform, "SlagHeat", _cylinder, slag, new Color(0.95f, 0.45f, 0.1f, 0.4f));

            LastObstacles = cliff.Count + water.Count;
            LastRubble = rubble.Count;
            LastSlagDiscs = slag.Count;
            return root;
        }

        /// <summary>驻军标记（红圈 + 立柱）：只画还站着的驻军据点。</summary>
        private static void BuildGarrisonMarkers(Transform root, CampaignState s, SiteGenContent c)
        {
            var rings = new List<Matrix4x4>(c.Garrisons.Count);
            var posts = new List<Matrix4x4>(c.Garrisons.Count);
            foreach (SiteGarrisonPoint g in c.Garrisons)
            {
                OutpostRecord o = WorldOutpostSystem.Find(s, g.OutpostId);
                if (o != null && o.Destroyed)
                {
                    continue; // 已摧毁的据点不画驻军标记（地上的废料与残骸照常）
                }
                rings.Add(Matrix4x4.TRS(new Vector3(g.Local.x, 0.03f, g.Local.y), Quaternion.identity, new Vector3(7f, 0.03f, 7f)));
                posts.Add(Matrix4x4.TRS(new Vector3(g.Local.x, 1.6f, g.Local.y), Quaternion.identity, new Vector3(0.5f, 1.6f, 0.5f)));
            }
            Merged(root, "GarrisonRings", _cylinder, rings, new Color(0.75f, 0.2f, 0.18f, 0.45f));
            Merged(root, "GarrisonPosts", _cube, posts, c.TerritoryId == "foundry" ? new Color(0.71f, 0.4f, 0.11f) : new Color(0.49f, 0.56f, 0.64f));
            LastGarrisonMarkers = rings.Count;
        }

        /// <summary>
        /// 修复轮（第 1 轮审查 P2）：观察中摧毁了驻军据点，红圈 / 立柱当场消失——地点控制器每帧调用；只有“地点驻军被摧毁”计数变了才重建这两样（O(驻军据点数)），其余帧 O(1)。
        /// </summary>
        public static void SyncGarrisonMarkers(Transform parent, CampaignState s, SiteGenContent c)
        {
            if (parent == null || c == null || s == null)
            {
                return;
            }
            Transform root = parent.Find(RootName);
            SiteGenMarkerStamp stamp = root != null ? root.GetComponent<SiteGenMarkerStamp>() : null;
            if (stamp == null || stamp.Routed == SiteGarrisonService.RoutedGarrisons)
            {
                return;
            }
            stamp.Routed = SiteGarrisonService.RoutedGarrisons;
            foreach (string name in new[] { "GarrisonRings", "GarrisonPosts" })
            {
                Transform old = root.Find(name);
                if (old != null)
                {
                    UnityObjects.Release(old.gameObject);
                }
            }
            EnsureMeshes();
            BuildGarrisonMarkers(root, s, c);
        }

        private static void Merged(Transform parent, string name, Mesh unit, List<Matrix4x4> items, Color color)
        {
            if (items.Count == 0 || unit == null)
            {
                return;
            }
            var ci = new CombineInstance[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                ci[i] = new CombineInstance { mesh = unit, transform = items[i] };
            }
            var mesh = new Mesh { name = "SiteGen_" + name };
            if (items.Count * unit.vertexCount > 65000)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }
            mesh.CombineMeshes(ci, true, true);
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = ViewMaterials.Standard(color);
            go.AddComponent<SiteGenOwnedMesh>().Mesh = mesh;
        }

        private static void EnsureMeshes()
        {
            if (_cube == null)
            {
                GameObject c = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _cube = c.GetComponent<MeshFilter>().sharedMesh;
                UnityObjects.Release(c);
            }
            if (_cylinder == null)
            {
                GameObject c = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                _cylinder = c.GetComponent<MeshFilter>().sharedMesh;
                UnityObjects.Release(c);
            }
        }
    }

    /// <summary>驻军标记是按哪一次“地点驻军被摧毁”计数画的。</summary>
    public sealed class SiteGenMarkerStamp : MonoBehaviour
    {
        public int Routed;
    }

    /// <summary>合并出来的网格随对象一起释放（不留在内存里）。</summary>
    public sealed class SiteGenOwnedMesh : MonoBehaviour
    {
        public Mesh Mesh;

        private void OnDestroy()
        {
            if (Mesh != null)
            {
                UnityObjects.Release(Mesh);
                Mesh = null;
            }
        }
    }
}
