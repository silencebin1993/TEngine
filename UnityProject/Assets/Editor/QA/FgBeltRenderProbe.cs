using System;
using System.IO;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-ARCH-02：传送带着色器的真实 GPU 画面探针（DEBT-FG0ARCH02-08 的自动部分）。需要图形设备：
    /// 不带 -nographics 的 batchmode（或编辑器菜单）运行。用真实 BeltKernel + BeltRenderer + BeltInstanced 着色器把一段传送带
    /// 画进 RenderTexture，读回像素断言：背景是黑的、带面不是黑的、物品比带面亮、远景不画物品、堵塞格偏红（斜纹 + 红色）。
    /// 画面另存为 PNG 到 production/qa/evidence/（整夹 gitignore，只在本机）。退出码：0 通过、1 失败、2 没有图形设备。
    /// </summary>
    public static class FgBeltRenderProbe
    {
        private const int Size = 512;
        private const float Ortho = 6f;
        private static readonly Vector3 CamPos = new Vector3(5f, 20f, 0f);

        [MenuItem("BinGames/自检：FG 传送带 GPU 画面探针")]
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

            report.AppendLine("[传送带 GPU 画面] BeltInstanced 着色器真实绘制（FG0-ARCH-02）");
            report.AppendLine($"  · 图形设备 {SystemInfo.graphicsDeviceType}（{SystemInfo.graphicsDeviceName}），着色器等级 {SystemInfo.graphicsShaderLevel}");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                report.AppendLine("  · 没有图形设备（-nographics），跳过");
                Finish(report, 2);
                return;
            }

            ConfigSystem.Instance.Load();
            GridContent.Reload();
            var camGo = new GameObject("BeltProbeCamera");
            var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
            BeltRenderer renderer = null;
            try
            {
                Camera cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = Ortho;
                cam.transform.position = CamPos;
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 100f;
                cam.targetTexture = rt;
                cam.enabled = false;

                renderer = new BeltRenderer();
                Expect(renderer.GpuAvailable, $"着色器 {BeltRenderer.ShaderName} 在本设备上可用（{renderer.GpuUnavailableReason ?? "GPU 可用"}）");
                BeltRenderer.Settings st = BeltNetworkService.ReadRenderSettings();

                using (var k = new BeltKernel(BeltNetworkService.ReadConfig()))
                {
                    // 一条 10 格 T1 直线，头上两件物品在跑（未堵塞）。
                    for (int x = 0; x < 10; x++)
                    {
                        k.AddCell(x, 0, BeltDir.East, 0);
                    }
                    k.InsertItemAt(3, 0, 24000, 5);
                    k.InsertItemAt(6, 0, 24000, 9);
                    k.Step();

                    Texture2D near = Render(renderer, k, cam, rt, 12f, st);
                    Color bg = Px(near, 5f, 3f);
                    Color belt = Px(near, 1f, 0f);
                    k.TryGetCellInfo(3, 0, out BeltCellInfo c3);
                    float itemX = 3f + c3.Pos0 / (float)BeltConst.CellLength - 0.5f;
                    Color item = Px(near, itemX, 0f);
                    Expect(Max(bg) < 0.03f, $"背景是黑的（{Fmt(bg)}）");
                    Expect(Max(belt) > 0.05f, $"近景：带面被画出来（{Fmt(belt)}）");
                    Expect(Lum(item) > Lum(belt) + 0.1f, $"近景：物品方块画在带上、比带面亮（物品 {Fmt(item)} / 带面 {Fmt(belt)}）");
                    // 箭头朝向：箭头是尖朝前的 V 形——同一条亮纹在带中线上比在偏离中线处更靠前（+x）。
                    float center = BrightestX(near, 1, 0f);
                    float side = BrightestX(near, 1, 0.3f);
                    float lead = Mathf.Repeat(center - side, 0.5f);
                    Expect(lead > 0.05f && lead < 0.25f,
                        $"近景：箭头尖朝前进方向（东）——亮纹在中线处比偏离中线 0.3 格处靠前 {lead:F2} 格（反向时约 {0.5f - 0.156f:F2}）");
                    Save(near, "fg0-arch-02-gpu-near.png");

                    Texture2D far = Render(renderer, k, cam, rt, st.FlowOrthoEnter + 2f, st);
                    Color farItem = Px(far, itemX, 0f);
                    Expect(renderer.FarMode && renderer.LastItemInstances == 0 && Max(Px(far, 1f, 0f)) > 0.03f && Distance(farItem, item) > 0.1f,
                        $"远景：只画带面的流动贴图、不画物品（原物品位置 {Fmt(farItem)}，近景是 {Fmt(item)}）");
                    Save(far, "fg0-arch-02-gpu-far.png");

                    // 让整条带堵住（末端没接东西、源源不断地补货）。
                    k.AddSource(1, 0, 0, 7, 1, BeltConst.Unlimited);
                    k.StepMany(4000);
                    k.TryGetCellInfo(9, 0, out BeltCellInfo head);
                    Texture2D blocked = Render(renderer, k, cam, rt, st.FlowOrthoEnter + 2f, st);
                    Color red = AverageCell(blocked, 5, 0);
                    Color running = AverageCell(far, 5, 0);
                    Expect(head.Block == BeltBlock.EndOfBelt && red.r > red.g + 0.1f && red.r > running.r + 0.1f,
                        $"堵塞格偏红（斜纹 + 红色，FGR-LOG-025）：堵塞 {Fmt(red)}，运转中 {Fmt(running)}");
                    Save(blocked, "fg0-arch-02-gpu-blocked.png");
                    Object.DestroyImmediate(near);
                    Object.DestroyImmediate(far);
                    Object.DestroyImmediate(blocked);
                }
            }
            catch (Exception e)
            {
                fail++;
                report.AppendLine("  ✗ 探针抛异常：" + e);
            }
            finally
            {
                renderer?.Dispose();
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(camGo);
            }
            report.AppendLine($"  · [传送带 GPU 画面] 断言通过 {pass}，失败 {fail}");
            Finish(report, fail == 0 ? 0 : 1);
        }

        private static Texture2D Render(BeltRenderer r, BeltKernel k, Camera cam, RenderTexture rt, float orthoForMode, in BeltRenderer.Settings st)
        {
            r.Draw(k, cam, orthoForMode, 1f, st);
            cam.Render();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }

        /// <summary>世界 (x, z) → 像素（镜头朝下，屏幕右 = +x、上 = +z）。</summary>
        private static Color Px(Texture2D t, float x, float z)
        {
            int px = Mathf.Clamp(Mathf.RoundToInt((x - (CamPos.x - Ortho)) / (2f * Ortho) * Size), 0, Size - 1);
            int py = Mathf.Clamp(Mathf.RoundToInt((z - (CamPos.z - Ortho)) / (2f * Ortho) * Size), 0, Size - 1);
            return t.GetPixel(px, py);
        }

        /// <summary>第 <paramref name="cx"/> 格里、z = <paramref name="z"/> 这一行上最亮点的 x（世界坐标，格内 0.52～1.48 扫描）。</summary>
        private static float BrightestX(Texture2D t, int cx, float z)
        {
            float best = -1f;
            float bestX = cx;
            for (float x = cx - 0.46f; x <= cx + 0.46f; x += 0.01f)
            {
                float l = Lum(Px(t, x, z));
                if (l > best)
                {
                    best = l;
                    bestX = x;
                }
            }
            return bestX;
        }

        private static Color AverageCell(Texture2D t, int cx, int cz)
        {
            Color sum = Color.clear;
            int n = 0;
            for (float dx = -0.35f; dx <= 0.35f; dx += 0.05f)
            {
                for (float dz = -0.35f; dz <= 0.35f; dz += 0.05f)
                {
                    sum += Px(t, cx + dx, cz + dz);
                    n++;
                }
            }
            return sum / n;
        }

        private static float Max(Color c) => Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        private static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        private static float Distance(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
        private static string Fmt(Color c) => $"({c.r:F2},{c.g:F2},{c.b:F2})";

        private static void Save(Texture2D t, string name)
        {
            string dir = EvidenceDir();
            if (dir == null)
            {
                return;
            }
            File.WriteAllBytes(Path.Combine(dir, name), t.EncodeToPNG());
        }

        /// <summary>从工程目录往上找仓库根的 production/qa/evidence（影子工程与真工程都能找到）；找不到就不存图。</summary>
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
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }
    }
}
