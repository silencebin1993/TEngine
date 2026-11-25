using System.Linq;
using System.Text;
using BinGames.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-PL-01 负向（卡片“只还原 ProjectSettings 4 个文件后工程仍能编译、地形回落 Built-in 子着色器”）GPU 探针：
    /// 同一进程里把默认管线与画质档管线临时清空（= 只还原 GraphicsSettings / QualitySettings 的管线字段，Built-in），
    /// 3 个地形 shader 各画一块方片读回：Built-in 下走原 surface 子着色器、画得出且不是品红；材质工厂在 Built-in 下回到 Standard。
    /// 再切回 URP 画同一块：同样不是品红（反例：拿 Built-in 的 Standard 材质在 URP 下画，必须读出品红——证明这台探针认得出品红）。
    /// 跑法：PROBE_ONLY=fallback bash tools/unity-gpu-probes.sh（需要图形设备）。
    /// </summary>
    public static class FgBuiltinFallbackProbe
    {
        private const int Size = 64;
        private static readonly string[] TerrainShaders = { "BinGames/Terrain/ContinuousGround", "BinGames/Terrain/MatteProps", "BinGames/Terrain/MutedWater" };

        [MenuItem("QA/自检/渲染/只还原 ProjectSettings 的回落探针")]
        public static void Run()
        {
            var report = new StringBuilder("[Built-in 回落] 只还原 ProjectSettings 管线字段后地形 shader 回落 Built-in 子着色器（FG15-PL-01 负向）\n");
            int fail = 0;
            void Expect(bool ok, string msg)
            {
                report.AppendLine((ok ? "  ✓ " : "  ✗ ") + msg);
                fail += ok ? 0 : 1;
            }

            report.AppendLine($"  · 图形设备 {SystemInfo.graphicsDeviceType}（{SystemInfo.graphicsDeviceName}）");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                report.AppendLine("  · 没有图形设备（-nographics），跳过");
                Finish(report, 2);
                return;
            }

            RenderPipelineAsset savedDefault = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset savedQuality = QualitySettings.renderPipeline;
            Expect(savedDefault != null, $"起点是 URP（默认管线 {(savedDefault != null ? savedDefault.name : "空")}）");
            var camGo = new GameObject("FgFallbackProbeCamera") { hideFlags = HideFlags.HideAndDontSave };
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.hideFlags = HideFlags.HideAndDontSave;
            var lightGo = new GameObject("FgFallbackProbeLight") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            Material standardInUrp = null;
            Mesh colored = null;
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.targetTexture = rt;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.orthographic = true;
                cam.orthographicSize = 0.4f;
                cam.transform.position = new Vector3(0f, 0f, -2f);
                // 地貌道具 / 地面 shader 的颜色来自顶点色（TerrainStream 生成的网格都带），内置 Quad 没有顶点色：换一份带中灰顶点色的副本
                MeshFilter mf = quad.GetComponent<MeshFilter>();
                colored = Object.Instantiate(mf.sharedMesh);
                colored.colors = Enumerable.Repeat(new Color(0.6f, 0.6f, 0.6f, 1f), colored.vertexCount).ToArray();
                mf.sharedMesh = colored;
                // 地貌道具的色调 _Color 在实例化缓冲里，正式路径一律由 TerrainStream.CreateProp 用属性块给（裸材质在 URP 非实例化路径下读不到材质默认值）：照正式路径给
                var tint = new MaterialPropertyBlock();
                tint.SetColor("_Color", Color.white);
                quad.GetComponent<MeshRenderer>().SetPropertyBlock(tint);
                Light light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(30f, 20f, 0f);

                // ① 退回 Built-in
                GraphicsSettings.defaultRenderPipeline = null;
                QualitySettings.renderPipeline = null;
                Expect(GraphicsSettings.currentRenderPipeline == null, "管线字段清空后当前是 Built-in");
                Expect(PresentationMaterials.ShaderName(PresentationMaterialUse.Lit) == "Standard", $"材质工厂在 Built-in 下回到 Standard（{PresentationMaterials.ShaderName(PresentationMaterialUse.Lit)}）");
                foreach (string name in TerrainShaders)
                {
                    Shader sh = Shader.Find(name);
                    if (sh == null)
                    {
                        Expect(false, $"找得到地形 shader {name}");
                        continue;
                    }
                    var mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                    Color c = Draw(cam, quad, mat, rt, tex);
                    Object.DestroyImmediate(mat);
                    Expect(sh.isSupported && !Magenta(c) && Max(c) > 0.02f, $"Built-in：{name} 走 Built-in 子着色器画得出、不是品红（读回 {Fmt(c)}）");
                }

                // ② 切回 URP：同样的地形 shader 走 URP 子着色器；反例：Built-in Standard 材质在 URP 下必须是品红
                GraphicsSettings.defaultRenderPipeline = savedDefault;
                QualitySettings.renderPipeline = savedQuality;
                foreach (string name in TerrainShaders)
                {
                    Shader sh = Shader.Find(name);
                    if (sh == null)
                    {
                        continue;
                    }
                    var mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                    Color c = Draw(cam, quad, mat, rt, tex);
                    Object.DestroyImmediate(mat);
                    Expect(!Magenta(c) && Max(c) > 0.02f, $"URP：{name} 画得出、不是品红（读回 {Fmt(c)}）");
                }
                standardInUrp = new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave };
                Color bad = Draw(cam, quad, standardInUrp, rt, tex);
                Expect(Magenta(bad), $"反例：Built-in Standard 材质在 URP 下读出品红（{Fmt(bad)}）——探针认得出画坏的材质");
            }
            catch (System.Exception e)
            {
                Expect(false, "探针异常：" + e);
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = savedDefault;
                QualitySettings.renderPipeline = savedQuality;
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(quad);
                if (colored != null)
                {
                    Object.DestroyImmediate(colored);
                }
                Object.DestroyImmediate(lightGo);
                if (standardInUrp != null)
                {
                    Object.DestroyImmediate(standardInUrp);
                }
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
            }
            Finish(report, fail == 0 ? 0 : 1);
        }

        private static Color Draw(Camera cam, GameObject quad, Material mat, RenderTexture rt, Texture2D tex)
        {
            quad.GetComponent<MeshRenderer>().sharedMaterial = mat;
            cam.Render();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex.GetPixel(Size / 2, Size / 2);
        }

        private static bool Magenta(Color c) => c.r > 0.85f && c.b > 0.85f && c.g < 0.15f;

        private static float Max(Color c) => Mathf.Max(c.r, Mathf.Max(c.g, c.b));

        private static string Fmt(Color c) => $"({c.r:0.00},{c.g:0.00},{c.b:0.00})";

        private static void Finish(StringBuilder report, int code)
        {
            report.AppendLine(code == 0 ? "结论：PASS" : code == 2 ? "结论：跳过（无图形设备）" : "结论：FAIL");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }
    }
}
