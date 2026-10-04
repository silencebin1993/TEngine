using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace BinGames.TerrainVisual
{
    /// <summary>
    /// FG3-GEN-01：PCG 素材读取正式区块的已探索地形。只生成表现，不另造核心、资源、障碍或存档数据。
    /// 逐格工作在 AOT，只有区块内容或探索遮罩变化时调用；水面与岩石分别合并成一块网格。
    /// </summary>
    public sealed class CampaignTerrainChunk : IDisposable
    {
        private readonly GameObject _root;
        private Mesh _water, _rocks;
        public int WaterCellCount { get; private set; }
        public int RockCount { get; private set; }

        public CampaignTerrainChunk(Transform parent)
        {
            _root = new GameObject("PCG 地貌素材");
            _root.transform.SetParent(parent, false);
        }

        public void Rebuild(TerrainProfile profile, Mesh relief, uint seed, int baseX, int baseZ, int size,
            byte[] terrain, byte[] explored, byte[] pollution, byte cliffCode, byte waterCode)
        {
            Clear();
            Vector3[] heights = relief.vertices;
            int corners = (size + 1) * (size + 1);
            var waterVertices = new List<Vector3>();
            var waterUvs = new List<Vector2>();
            var waterColors = new List<Color>();
            var waterIndices = new List<int>();
            var rocks = new List<CombineInstance>();
            var rockColors = new List<Color>();
            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = z * size + x;
                    if (explored[i] == 0) continue;
                    float h = heights[corners + i].y;
                    if (terrain[i] == waterCode && profile.WaterMaterial && h < -.05f)
                    {
                        // 正式水域决定范围；水面位于正式河床之上、可行走平面之下。
                        float y = Mathf.Clamp(profile.WaterLevel, Mathf.Min(-.02f, h * .15f), -.02f);
                        int at = waterVertices.Count;
                        waterVertices.Add(new Vector3(x, y, z));
                        waterVertices.Add(new Vector3(x, y, z + 1));
                        waterVertices.Add(new Vector3(x + 1, y, z + 1));
                        waterVertices.Add(new Vector3(x + 1, y, z));
                        waterUvs.Add(new Vector2(baseX + x - .5f, baseZ + z - .5f));
                        waterUvs.Add(new Vector2(baseX + x - .5f, baseZ + z + .5f));
                        waterUvs.Add(new Vector2(baseX + x + .5f, baseZ + z + .5f));
                        waterUvs.Add(new Vector2(baseX + x + .5f, baseZ + z - .5f));
                        Color color = Color.Lerp(profile.Water, profile.Pollution, Mathf.Clamp01(pollution[i] / 3f) * .25f);
                        for (int v = 0; v < 4; v++) waterColors.Add(color);
                        waterIndices.Add(at); waterIndices.Add(at + 1); waterIndices.Add(at + 2);
                        waterIndices.Add(at); waterIndices.Add(at + 2); waterIndices.Add(at + 3);
                        WaterCellCount++;
                    }
                    // 岩石只放在已探索的悬崖格中心，不侵入可走格、不新增碰撞体。
                    uint hash = TerrainField.Hash(baseX + x, baseZ + z, unchecked((int)seed + 901));
                    if (terrain[i] != cliffCode || RockCount >= 32 || !profile.PropMaterial
                        || (hash & 255) / 255f > profile.PropDensity * .22f) continue;
                    Mesh rock = (hash & 256) != 0 && profile.RockVariantMesh ? profile.RockVariantMesh : profile.RockMesh;
                    if (!rock) continue;
                    float width = Mathf.Max(.01f, Mathf.Max(rock.bounds.size.x, rock.bounds.size.z));
                    float scale = Mathf.Min(.6f / width, .8f);
                    rocks.Add(new CombineInstance
                    {
                        mesh = rock,
                        transform = Matrix4x4.TRS(new Vector3(x + .5f, h - .08f, z + .5f),
                            Quaternion.Euler(0, hash % 360, 0), Vector3.one * scale),
                    });
                    Color tint = Color.Lerp(profile.Rock, profile.Pollution, Mathf.Clamp01(pollution[i] / 3f) * .25f);
                    for (int v = 0; v < rock.vertexCount; v++) rockColors.Add(tint);
                    RockCount++;
                }
            }
            if (waterVertices.Count > 0)
            {
                _water = new Mesh { name = "正式 PCG 水面" };
                _water.SetVertices(waterVertices);
                _water.SetUVs(0, waterUvs);
                _water.SetColors(waterColors);
                _water.SetTriangles(waterIndices, 0);
                _water.RecalculateNormals();
                Attach(_water, profile.WaterMaterial, "水面", true);
            }
            if (rocks.Count > 0)
            {
                _rocks = new Mesh { name = "正式 PCG 岩石", indexFormat = rockColors.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                _rocks.CombineMeshes(rocks.ToArray(), true, true);
                _rocks.SetColors(rockColors);
                Attach(_rocks, profile.PropMaterial, "岩石", false);
            }
        }

        private void Attach(Mesh mesh, Material material, string name, bool water)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = water ? ShadowCastingMode.Off : ShadowCastingMode.On;
            var properties = new MaterialPropertyBlock();
            properties.SetFloat("_CampaignMode", 1);
            properties.SetVector("_TerrainWorldOrigin", Vector4.zero);
            renderer.SetPropertyBlock(properties);
        }

        private void Clear()
        {
            WaterCellCount = RockCount = 0;
            for (int i = _root ? _root.transform.childCount - 1 : -1; i >= 0; i--)
            {
                GameObject child = _root.transform.GetChild(i).gameObject;
                child.SetActive(false);
                Release(child);
            }
            if (_water) Release(_water);
            if (_rocks) Release(_rocks);
            _water = _rocks = null;
        }

        public void Dispose()
        {
            Clear();
            Release(_root);
        }

        private static void Release(Object value)
        {
            if (!value) return;
            if (Application.isPlaying) Object.Destroy(value);
            else Object.DestroyImmediate(value);
        }
    }
}
