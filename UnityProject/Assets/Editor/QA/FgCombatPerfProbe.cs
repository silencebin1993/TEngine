using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Grid;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-ARCH-03：战斗性能场景的真实 GPU 帧时间探针（FG14 第 4 节“200 敌人 + 80 炮塔 + 1,500 弹体，帧率 ≥ 60”）。需要图形设备：
    /// 不带 -nographics 的 batchmode（或编辑器菜单）运行，不在 unity-validate / 冒烟链路里（两者都是 -nographics）。
    /// 每一帧 = 战斗内核一步（60 Hz 世界 1x：一帧一步）+ 事件处理 + 实例化绘制提交 + 1920×1080 渲染 + 等 GPU 做完（读回 1 个像素强制同步），
    /// 统计 600 帧的帧时间。另读回一帧画面断言单位与弹体真的画出来了（阵营颜色像素数），画面存到 production/qa/evidence/（整夹 gitignore）。
    /// 退出码：0 通过、1 失败、2 没有图形设备。
    /// </summary>
    public static class FgCombatPerfProbe
    {
        private const int Width = 1920;
        private const int Height = 1080;
        private const int Frames = 600;

        [MenuItem("BinGames/自检：FG 战斗性能场景 GPU 帧时间探针")]
        public static void Run()
        {
            var report = new StringBuilder();
            int fail = 0;
            int pass = 0;
            void Expect(bool ok, string msg)
            {
                if (ok)
                {
                    pass++;
                    report.AppendLine("  ✓ " + msg);
                }
                else
                {
                    fail++;
                    report.AppendLine("  ✗ " + msg);
                }
            }

            report.AppendLine("[战斗 GPU 帧时间] 性能场景真实绘制（FG0-ARCH-03）");
            report.AppendLine($"  · 图形设备 {SystemInfo.graphicsDeviceType}（{SystemInfo.graphicsDeviceName}），CPU {SystemInfo.processorType}（{SystemInfo.processorCount} 线程），" +
                              $"Burst={(Unity.Burst.BurstCompiler.IsEnabled ? "开" : "关")}，Unity {Application.unityVersion}");
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                report.AppendLine("  · 没有图形设备（-nographics），跳过");
                Finish(report, 2);
                return;
            }

            ConfigSystem.Instance.Load();
            GridContent.Reload();
            var camGo = new GameObject("CombatProbeCamera");
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            var sync = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            CombatSite site = null;
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 46f; // 战略镜头最远缩放（camera.zoom_max_ortho）：整个战场都在画面里。
                cam.transform.position = new Vector3(0f, 60f, 0f);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.05f, 0.07f, 0.1f);
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 200f;
                cam.targetTexture = rt;
                cam.enabled = false;

                site = new CombatSite("gpu_probe", CombatSite.ConfigFromTuning());
                CombatBench.SpawnPerfScenario(site, Vector2.zero, (int)CombatSite.Tuning("combat.perf.enemies", 200),
                    (int)CombatSite.Tuning("combat.perf.turrets", 80), CombatBench.PerfSpec());
                const float dt = 1f / 60f;
                double t = 0;
                for (int i = 0; i < 600; i++)
                {
                    site.Step(dt, t);
                    t += dt;
                }
                // 预热：着色器、缓冲首次分配。
                for (int i = 0; i < 30; i++)
                {
                    RenderFrame(site, cam, rt, sync, ref t, dt);
                }
                var frameMs = new List<double>(Frames);
                var simMs = new List<double>(Frames);
                int minProj = int.MaxValue;
                var sw = new Stopwatch();
                for (int i = 0; i < Frames; i++)
                {
                    sw.Restart();
                    double sim = RenderFrame(site, cam, rt, sync, ref t, dt);
                    sw.Stop();
                    frameMs.Add(sw.Elapsed.TotalMilliseconds);
                    simMs.Add(sim);
                    minProj = Math.Min(minProj, site.Kernel.ProjectileCount);
                }
                frameMs.Sort();
                double avg = frameMs.Average();
                double p95 = frameMs[(int)(Frames * 0.95)];
                double max = frameMs[Frames - 1];
                int units = site.Renderer?.LastUnitInstances ?? 0;
                int projs = site.Renderer?.LastProjectileInstances ?? 0;
                report.AppendLine($"  · {Frames} 帧（{Width}×{Height}，每帧 1 个内核步 + 事件 + 实例化提交 + 渲染 + 等 GPU）：帧时间 平均 {avg:F2} ms / p95 {p95:F2} ms / 最大 {max:F2} ms " +
                                  $"≈ {1000.0 / avg:F0} 帧/秒；其中内核步 + 事件平均 {simMs.Average():F3} ms；画面上 {units} 个单位、{projs} 枚弹体（测量期间弹体最少 {minProj}）");
                Expect(site.Renderer != null && site.Renderer.GpuAvailable && site.Renderer.LastDrawCalls == 2,
                    $"实例化绘制可用：每帧 {site.Renderer?.LastDrawCalls} 次绘制调用画完全部单位与弹体（{site.Renderer?.GpuUnavailableReason ?? "GPU 可用"}）");
                Expect(minProj >= 1500 && units >= 250, $"规模：弹体始终 ≥ 1,500（最少 {minProj}），单位 {units}");
                Expect(p95 <= 1000.0 / 60.0, $"p95 帧时间 {p95:F2} ms ≤ 16.7 ms（≥ 60 帧/秒；本机配置高于推荐配置，换算见 ADR）");

                // 画面：读回一帧，数阵营颜色像素（己方青、敌方橙红），确认真的画出来了（每次渲染前都要提交一次实例化绘制）。
                site.FrameRender(cam, 0.5f);
                cam.Render();
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                int friend = 0;
                int foe = 0;
                int streak = 0;
                Color32[] px = tex.GetPixels32();
                foreach (Color32 c in px)
                {
                    if (c.b > 200 && c.g > 180 && c.r < 110)
                    {
                        friend++;
                    }
                    else if (c.r > 200 && c.g > 70 && c.g < 150 && c.b < 90)
                    {
                        foe++;
                    }
                    else if (c.r > 230 && c.g > 230 && c.b > 180)
                    {
                        streak++;
                    }
                }
                Expect(friend > 200 && foe > 200 && streak > 50,
                    $"画面里有炮塔（青色像素 {friend}）、突袭者（橙红像素 {foe}）、弹体亮线（{streak}）");
                string dir = EvidenceDir();
                if (dir != null)
                {
                    File.WriteAllBytes(Path.Combine(dir, "fg0-arch-03-gpu-perf.png"), tex.EncodeToPNG());
                }
                Object.DestroyImmediate(tex);
            }
            catch (Exception e)
            {
                fail++;
                report.AppendLine("  ✗ 探针抛异常：" + e);
            }
            finally
            {
                site?.Dispose();
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(sync);
                Object.DestroyImmediate(camGo);
            }
            report.AppendLine($"  · [战斗 GPU 帧时间] 断言通过 {pass}，失败 {fail}");
            Finish(report, fail == 0 ? 0 : 1);
        }

        /// <summary>一帧：内核一步 + 事件 + 绘制提交 + 渲染 + 读回 1 像素（强制等 GPU 做完这一帧）。返回内核 + 事件的耗时。</summary>
        private static double RenderFrame(CombatSite site, Camera cam, RenderTexture rt, Texture2D sync, ref double t, float dt)
        {
            var sw = Stopwatch.StartNew();
            site.Step(dt, t);
            t += dt;
            double sim = sw.Elapsed.TotalMilliseconds;
            site.FrameRender(cam, 0.5f);
            cam.Render();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            sync.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            sync.Apply();
            RenderTexture.active = prev;
            return sim;
        }

        private static string EvidenceDir()
        {
            var d = new DirectoryInfo(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
            for (int i = 0; i < 5 && d != null; i++, d = d.Parent)
            {
                string p = Path.Combine(d.FullName, "production", "qa", "evidence");
                if (Directory.Exists(p))
                {
                    return p;
                }
            }
            return null;
        }

        private static void Finish(StringBuilder report, int code)
        {
            Debug.Log(report.ToString());
            string dir = EvidenceDir();
            if (dir != null)
            {
                File.WriteAllText(Path.Combine(dir, "_combat-gpu-probe.txt"), report.ToString());
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }
    }
}
