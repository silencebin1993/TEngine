using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using GameLogic.Campaign.Env;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-PL-01（承接 DEBT-FG7ENV03-04：天气粒子与雾的 GPU 开销没测）URP 带图形探针：同一机位 1920×1080，晴天 / 满强度沙暴各手动渲染 300 帧（预热 30），
    /// 量帧时间 p95（含读回 1 像素等 GPU），并断言 URP 下天气真的画出来：沙暴雾把远处方块的颜色拉向雾色、粒子在发射且材质 URP 能画；反例：晴天雾关、粒子 0。
    /// 帧时间按 120 帧一帧 8.33 ms 判：超线不到 2 倍记性能警告（FG-TOOL-01），2 倍以上失败。跑法：PROBE_ONLY=weather bash tools/unity-gpu-probes.sh。
    /// </summary>
    public static class FgWeatherGpuProbe
    {
        private const int Width = 1920;
        private const int Height = 1080;
        private const int Warmup = 30;
        private const int Frames = 300;
        private const float BudgetMs = 8.33f;

        [MenuItem("QA/自检/渲染/天气 GPU 开销探针")]
        public static void Run()
        {
            var report = new StringBuilder("[天气 GPU 开销] 沙暴雾 + 粒子在 URP 下的画面与帧时间（FG15-PL-01，DEBT-FG7ENV03-04）\n");
            int fail = 0, warn = 0;
            void Expect(bool ok, string msg)
            {
                report.AppendLine((ok ? "  ✓ " : "  ✗ ") + msg);
                fail += ok ? 0 : 1;
            }

            report.AppendLine($"  · 图形设备 {SystemInfo.graphicsDeviceType}（{SystemInfo.graphicsDeviceName}），管线 {(GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "Built-in")}，{GpuProbeColor.Note}");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Finish(report, 2);
                return;
            }
            Expect(PostProcessView.UrpActive, "当前管线是 URP");

            ConfigSystem.Instance.Load();
            var made = new List<Object>();
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            var one = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bool fogBefore = RenderSettings.fog;
            try
            {
                Material grey = PresentationMaterialsLit(new Color(0.45f, 0.45f, 0.45f));
                made.Add(grey);
                GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.transform.localScale = new Vector3(30f, 1f, 30f);
                GameObject far = GameObject.CreatePrimitive(PrimitiveType.Cube);
                far.transform.position = new Vector3(0f, 4f, 120f);
                far.transform.localScale = new Vector3(16f, 8f, 4f);
                foreach (GameObject g in new[] { ground, far })
                {
                    g.hideFlags = HideFlags.HideAndDontSave;
                    g.GetComponent<MeshRenderer>().sharedMaterial = grey;
                    made.Add(g);
                }
                var camGo = new GameObject("FgWeatherProbeCamera") { hideFlags = HideFlags.HideAndDontSave };
                made.Add(camGo);
                Camera cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.targetTexture = rt;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.2f, 0.3f, 0.45f);
                cam.farClipPlane = 400f;
                cam.transform.position = new Vector3(0f, 12f, -20f);
                cam.transform.LookAt(new Vector3(0f, 2f, 60f));

                WeatherView.ResetForTests();
                WeatherView.Apply(WeatherService.Clear, 0f);
                Color clearFar = Sample(cam, rt, one, far.transform.position);
                bool clearFog = RenderSettings.fog;
                ParticleSystem ps = Field<ParticleSystem>("_ps");
                int clearParticles = ps != null ? ps.particleCount : 0;
                float clearP95 = Measure(cam, rt, one, null);

                WeatherView.Apply(WeatherService.Sandstorm, 1f);
                ps = Field<ParticleSystem>("_ps");
                GameObject root = Field<GameObject>("_root");
                if (root != null)
                {
                    root.transform.position = cam.transform.position + cam.transform.forward * 25f + Vector3.up * 6f;
                }
                ps?.Simulate(3f, true, true);
                Color sandFar = Sample(cam, rt, one, far.transform.position);
                int sandParticles = ps != null ? ps.particleCount : 0;
                var psr = ps != null ? ps.GetComponent<ParticleSystemRenderer>() : null;
                string psrProblem = psr != null ? FgUrpMaterialAudit.Problem(psr.sharedMaterial) : "没有粒子渲染器";
                float sandP95 = Measure(cam, rt, one, ps);

                Color fogColor = RenderSettings.fogColor;
                float dClear = Dist(clearFar, fogColor), dSand = Dist(sandFar, fogColor);
                report.AppendLine($"  · 远处方块读回：晴天 {Fmt(clearFar)}、沙暴 {Fmt(sandFar)}；雾色 {Fmt(fogColor)}、雾密度 {WeatherView.FogDensity:0.0000}；粒子 晴天 {clearParticles} / 沙暴 {sandParticles}（发射率 {WeatherView.EmissionRate:0}）");
                report.AppendLine($"  · 帧时间 p95（{Width}×{Height}，{Frames} 帧手动渲染 + 读回 1 像素）：晴天 {clearP95:0.00} ms、沙暴 {sandP95:0.00} ms（天气开销 {sandP95 - clearP95:+0.00;-0.00} ms）");
                Expect(!clearFog && clearParticles == 0, $"反例：晴天不开雾、没有粒子（雾 {clearFog}、粒子 {clearParticles}）");
                Expect(RenderSettings.fog && dSand < dClear - 0.05f, $"URP 下沙暴雾生效：远处方块颜色向雾色靠拢（距雾色 {dClear:0.000} → {dSand:0.000}，至少近 0.05）");
                Expect(sandParticles > 100 && psrProblem == null, $"沙暴粒子在发射（{sandParticles} 颗 > 100）且粒子材质 URP 能画（{psrProblem ?? psr.sharedMaterial.shader.name}）");
                if (sandP95 <= BudgetMs)
                {
                    Expect(true, $"沙暴 p95 {sandP95:0.00} ms ≤ {BudgetMs} ms（120 帧）");
                }
                else if (sandP95 < BudgetMs * 2f)
                {
                    warn++;
                    report.AppendLine($"  ⚠ 性能警告：沙暴 p95 {sandP95:0.00} ms > {BudgetMs} ms（不到 2 倍，记警告不判失败；编辑器手动渲染口径，真实帧以 FG15-ACC-05 程序包为准）");
                }
                else
                {
                    Expect(false, $"沙暴 p95 {sandP95:0.00} ms ≥ 2 × {BudgetMs} ms");
                }
            }
            catch (System.Exception e)
            {
                Expect(false, "探针异常：" + e);
            }
            finally
            {
                WeatherView.ResetForTests();
                RenderSettings.fog = fogBefore;
                foreach (Object o in made)
                {
                    Object.DestroyImmediate(o);
                }
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(one);
            }
            report.AppendLine($"  · 性能警告 {warn} 条");
            Finish(report, fail == 0 ? 0 : 1);
        }

        private static Material PresentationMaterialsLit(Color c)
        {
            var m = new Material(BinGames.Presentation.PresentationMaterials.FindShader(BinGames.Presentation.PresentationMaterialUse.Lit)) { hideFlags = HideFlags.HideAndDontSave };
            m.color = c;
            if (m.HasProperty("_BaseColor"))
            {
                m.SetColor("_BaseColor", c);
            }
            return m;
        }

        private static T Field<T>(string name) where T : Object
        {
            FieldInfo f = typeof(WeatherView).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            return f != null ? f.GetValue(null) as T : null;
        }

        private static Color Sample(Camera cam, RenderTexture rt, Texture2D one, Vector3 world)
        {
            cam.Render();
            Vector3 sp = cam.WorldToScreenPoint(world);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            one.ReadPixels(new Rect(Mathf.Clamp(sp.x, 0, Width - 1), Mathf.Clamp(sp.y, 0, Height - 1), 1, 1), 0, 0);
            one.Apply();
            RenderTexture.active = prev;
            return GpuProbeColor.ToGammaEquivalent(one.GetPixel(0, 0));
        }

        private static float Measure(Camera cam, RenderTexture rt, Texture2D one, ParticleSystem ps)
        {
            var times = new List<double>(Frames);
            var sw = new Stopwatch();
            for (int i = 0; i < Warmup + Frames; i++)
            {
                sw.Restart();
                ps?.Simulate(1f / 120f, true, false);
                cam.Render();
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                one.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                one.Apply();
                RenderTexture.active = prev;
                sw.Stop();
                if (i >= Warmup)
                {
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            times.Sort();
            return (float)times[(int)(times.Count * 0.95f)];
        }

        private static float Dist(Color a, Color b) => Mathf.Sqrt((a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b));

        private static string Fmt(Color c) => $"({c.r:0.00},{c.g:0.00},{c.b:0.00})";

        private static void Finish(StringBuilder report, int code)
        {
            report.AppendLine(code == 0 ? "结论：PASS" : code == 2 ? "  · 没有图形设备（-nographics），跳过\n结论：跳过" : "结论：FAIL");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }
    }
}
