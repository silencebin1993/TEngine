using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG3-LOG-03：满载传送带的真实 GPU 帧时间探针（120 帧最低标准；FG03 第 7 节 15,000 格 / 30,000 件；FG3-LOG-09 起场景含 340 个物流节点）。需要图形设备：
    /// 不带 -nographics 的 batchmode（tools/unity-gpu-probes.sh）或编辑器菜单运行，不在 unity-validate / 冒烟链路里（两者都是 -nographics）。
    /// 每一帧 = 按 120 帧 × 3x 倍速的节奏推进内核（每 2 帧 1 个内核步，最坏的常用倍速）+ 渲染缓冲重填 + 实例化绘制提交 + 1920×1080 渲染 + 等 GPU 做完（读回 1 个像素强制同步）。
    /// 近景（正交半高 30，逐物品实例）与远景（46，流动贴图）各测 600 帧；另读回一帧画面断言带面与物品真的画出来了。画面存 production/qa/evidence/（整夹 gitignore）。
    /// 退出码：0 通过、1 失败、2 没有图形设备。
    /// </summary>
    public static class FgBeltPerfProbe
    {
        private const int Width = 1920;
        private const int Height = 1080;
        private const int Frames = 600;

        [MenuItem("BinGames/自检：FG 满载传送带 GPU 帧时间探针")]
        public static void Run()
        {
            var report = new StringBuilder();
            PerfGate.ResetRun();
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

            report.AppendLine("[满载传送带 GPU 帧时间] 120 帧标准（FG3-LOG-03）");
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
            var camGo = new GameObject("BeltPerfProbeCamera");
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            var sync = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            BeltKernel kernel = null;
            BeltRenderer renderer = null;
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.05f, 0.07f, 0.1f);
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 200f;
                cam.targetTexture = rt;
                cam.enabled = false;

                kernel = new BeltKernel(BeltNetworkService.ReadConfig());
                // FG3-LOG-09（DEBT-FG3LOG04-07 的 GPU 部分）：换成含 340 个物流节点（分流器 / 合流器 / 地下传送带）的规模场景（15,045 格）。
                FgBeltNodeSelfCheck.BuildHugeWithNodes(kernel, 0, 0);
                kernel.StepMany(200);
                renderer = new BeltRenderer();
                BeltRenderer.Settings settings = BeltNetworkService.ReadRenderSettings();
                var results = new List<string>();
                bool allOk = true;
                var perf = new List<PerfGate.Metric>();
                foreach ((string name, float ortho, Vector3 at) in new[]
                         {
                             ("近景（正交 30，逐物品）", 30f, new Vector3(75f, 60f, 48f)),
                             ("远景（正交 46，流动贴图）", 46f, new Vector3(150f, 60f, 48f)),
                         })
                {
                    cam.orthographicSize = ortho;
                    cam.transform.position = at;
                    long frame = 0;
                    for (int i = 0; i < 30; i++)
                    {
                        RenderFrame(kernel, renderer, cam, rt, sync, settings, ortho, ref frame);
                    }
                    var ms = new List<double>(Frames);
                    var sw = new Stopwatch();
                    int items = 0;
                    for (int i = 0; i < Frames; i++)
                    {
                        sw.Restart();
                        RenderFrame(kernel, renderer, cam, rt, sync, settings, ortho, ref frame);
                        sw.Stop();
                        ms.Add(sw.Elapsed.TotalMilliseconds);
                        items = Math.Max(items, renderer.LastItemInstances);
                    }
                    ms.Sort();
                    double avg = ms.Average();
                    double p95 = ms[(int)(Frames * 0.95)];
                    double p99 = ms[(int)(Frames * 0.99)];
                    results.Add($"{name}：{Frames} 帧 平均 {avg:F2} ms / p95 {p95:F2} ms / p99 {p99:F2} ms / 最大 {ms[Frames - 1]:F2} ms ≈ {1000.0 / avg:F0} 帧/秒；" +
                                $"实例 {renderer.LastCellInstances:N0} 格 + {items:N0} 件，每帧 {renderer.LastDrawCalls} 次绘制调用，远景={renderer.FarMode}");
                    allOk &= renderer.GpuAvailable;
                    perf.Add(PerfGate.Le(p95, 1000.0 / 120.0, $"{name} p95 ms"));
                }
                foreach (string r in results)
                {
                    report.AppendLine("  · " + r);
                }
                Expect(renderer.GpuAvailable, $"实例化绘制可用（{renderer.GpuUnavailableReason ?? "GPU 可用"}）");
                Expect(kernel.CellCount >= 15000 && kernel.ItemCount >= 30000 && kernel.NodeCount >= 300, $"规模：{kernel.CellCount:N0} 格 / {kernel.ItemCount:N0} 件 / 物流节点 {kernel.NodeCount}");
                // FG-TOOL-01：只测一次；超线不到 2 倍记性能警告（不计失败），超 2 倍才失败。
                PerfGate.Expect(allOk, "近景与远景 p95 帧时间都 ≤ 8.33 ms（120 帧/秒；帧内含 3x 倍速的内核步、缓冲重填与上传、渲染与等 GPU）", perf.ToArray(), Expect, l => report.AppendLine(l));

                // 画面：近景读回一帧，数带面与物品像素。
                cam.orthographicSize = 30f;
                cam.transform.position = new Vector3(75f, 60f, 48f);
                renderer.Draw(kernel, cam, 30f, 0.5f, settings);
                cam.Render();
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                int belt = 0;
                int bright = 0;
                foreach (Color32 c in tex.GetPixels32())
                {
                    int sum = c.r + c.g + c.b;
                    if (sum > 90 && sum < 300)
                    {
                        belt++;
                    }
                    else if (sum >= 300)
                    {
                        bright++;
                    }
                }
                Expect(belt > Width * Height / 10 && bright > 5000, $"画面里有传送带（带面像素 {belt:N0}）与物品（亮像素 {bright:N0}）");
                string dir = EvidenceDir();
                if (dir != null)
                {
                    File.WriteAllBytes(Path.Combine(dir, "fg3-log-03-gpu-belt-perf.png"), tex.EncodeToPNG());
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
                renderer?.Dispose();
                kernel?.Dispose();
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(sync);
                Object.DestroyImmediate(camGo);
            }
            report.AppendLine($"  · [满载传送带 GPU 帧时间] 断言通过 {pass}，失败 {fail}");
            Finish(report, fail == 0 ? 0 : 1);
        }

        /// <summary>一帧：120 帧 × 3x = 每帧 1.5 个 60 Hz 世界步 = 每 2 帧 1 个 20 Hz 内核步；然后绘制 + 渲染 + 读回 1 像素（等 GPU 做完）。</summary>
        private static void RenderFrame(BeltKernel k, BeltRenderer r, Camera cam, RenderTexture rt, Texture2D sync, in BeltRenderer.Settings settings, float ortho, ref long frame)
        {
            if (frame % 2 == 0)
            {
                k.Step();
            }
            frame++;
            r.AnimationTime = frame / 120f * 3f;
            r.Draw(k, cam, ortho, (frame % 2) * 0.5f, settings);
            cam.Render();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            sync.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            sync.Apply();
            RenderTexture.active = prev;
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
                File.WriteAllText(Path.Combine(dir, "_belt-gpu-perf-probe.txt"), report.ToString());
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }
    }
}
