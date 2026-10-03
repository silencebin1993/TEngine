using System;
using UnityEngine;

namespace BinGames.TerrainVisual
{
    public sealed class TerrainMeshData
    {
        public Vector3[] Vertices, Normals;
        public Color[] Colors;
        public Vector2[] Uvs;
        public int[] Indices;
        public bool HasWater;
    }

    public static class TerrainMeshBuilder
    {
        public static TerrainMeshData Build(ITerrainField field, int cx, int cz, int size, int step,
            int northStep, int eastStep, int southStep, int westStep, float waterLevel)
        {
            if (size < 1 || step < 1 || size % step != 0) throw new ArgumentException("地块尺寸必须整除网格步长。");
            int n = size / step, count = (n + 1) * (n + 1);
            var result = new TerrainMeshData { Vertices = new Vector3[count], Normals = new Vector3[count],
                Colors = new Color[count], Uvs = new Vector2[count], Indices = new int[n * n * 6] };
            double ox = (double)cx * size, oz = (double)cz * size;
            for (int z = 0; z <= n; z++) for (int x = 0; x <= n; x++)
            {
                int i = z * (n + 1) + x;
                double wx = ox + x * step, wz = oz + z * step;
                TerrainSample sample = field.Sample(wx, wz);
                float h = sample.Height;
                Vector3 normal = field.Normal(wx, wz);
                Color color = sample.Color;
                // Stitch only the finer edge onto the coarser neighbour's exact polyline.
                if (z == n && northStep > step) Stitch(field, wx, wz, northStep, true, ref h, ref normal, ref color);
                if (z == 0 && southStep > step) Stitch(field, wx, wz, southStep, true, ref h, ref normal, ref color);
                if (x == n && eastStep > step) Stitch(field, wx, wz, eastStep, false, ref h, ref normal, ref color);
                if (x == 0 && westStep > step) Stitch(field, wx, wz, westStep, false, ref h, ref normal, ref color);
                result.Vertices[i] = new Vector3(x * step, h, z * step);
                result.Normals[i] = normal; result.Colors[i] = color;
                result.Uvs[i] = new Vector2((float)wx, (float)wz);
                result.HasWater |= h < waterLevel;
            }
            int at = 0;
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
            {
                int a = z * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
                result.Indices[at++] = a; result.Indices[at++] = c; result.Indices[at++] = b;
                result.Indices[at++] = b; result.Indices[at++] = c; result.Indices[at++] = d;
            }
            return result;
        }
        private static void Stitch(ITerrainField f, double x, double z, int step, bool alongX,
            ref float height, ref Vector3 normal, ref Color color)
        {
            double p = alongX ? x : z, a = Math.Floor(p / step) * step;
            float t = (float)((p - a) / step);
            double ax = alongX ? a : x, az = alongX ? z : a;
            double bx = alongX ? a + step : x, bz = alongX ? z : a + step;
            TerrainSample sa = f.Sample(ax, az), sb = f.Sample(bx, bz);
            height = Mathf.Lerp(sa.Height, sb.Height, t);
            normal = Vector3.Lerp(f.Normal(ax, az), f.Normal(bx, bz), t).normalized;
            color = Color.Lerp(sa.Color, sb.Color, t);
        }
        public static Mesh Upload(TerrainMeshData data, string name)
        {
            var mesh = new Mesh { name = name };
            if (data.Vertices.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(data.Vertices); mesh.SetNormals(data.Normals); mesh.SetColors(data.Colors);
            mesh.SetUVs(0, data.Uvs); mesh.SetTriangles(data.Indices, 0); mesh.RecalculateBounds();
            return mesh;
        }
    }
}
