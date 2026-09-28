using System;
using System.IO;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.View;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG1-VFX-01 证据截图（卡片“验收：默认缩放下的截图证据”）。必须在带图形设备的 batchmode（不加 -nographics）里跑：
    ///
    /// 一、游戏内画面（主证据）：打开正式启动场景 <c>Assets/Scenes/main.unity</c>（游戏自己的平行光与环境光），用自检同一套入口
    /// 载入真实家园（程序化地形、建筑、机器表现、选中高亮等叠加层），全局镜头 <see cref="WorldView"/> 观察家园；
    /// 一台带接入口的重炮机、一台自带拖尾的连射器（AI 常驻喷口态）、一台不带固件的连射器并排。
    /// 用游戏的镜头按默认战略缩放（正交半高 30，以及铸造前哨默认被夹到的 46）拍“接入前”；再用真实接入键接入重炮机，
    /// 拍接入后的直控画面，并把同一台游戏镜头临时摆回同一战略位姿（不推进任何帧）拍“接入后”——离开时镜头拉回战略的过程、
    /// 以及 Tab / 机器列表切机时玩家在战略缩放下看到的就是这个画面。逐项统计重炮机周围 ±3 米“接入前 / 后”颜色明显不同的像素数。
    ///
    /// 二、部件矩阵（补充）：空场景里 6 个作战组件 × 8 种状态组合并排，便于逐套对照部件设计。
    ///
    /// 只写本机 production/qa/evidence/（整夹 gitignore），不写 Assets。仓库根按“向上找到同时含 production/qa 与 TEngine 的目录”确定，
    /// 找不到就报错、不落盘（主工程与影子工程都对）。
    /// 用法（影子工程，用户的 Editor 开着也能跑）：
    /// Unity.exe -batchmode -projectPath .unity-validate-clone -executeMethod GameLogic.EditorTools.MorphEvidenceCapture.Run -quit -logFile …
    /// </summary>
    public static class MorphEvidenceCapture
    {
        private const int Width = 1920;
        private const int Height = 1080;
        private const float Spacing = 5f;
        private const float DiffHalfMeters = 3f;
        private const string MainScene = "Assets/Scenes/main.unity";

        private static readonly MorphMask[] Columns =
        {
            MorphMask.None,
            MorphMask.Limiter,
            MorphMask.Fluid,
            MorphMask.Electromagnetic,
            MorphMask.Limiter | MorphMask.Fluid,
            MorphMask.Limiter | MorphMask.Electromagnetic,
            MorphMask.Fluid | MorphMask.Electromagnetic,
            MorphMask.All,
        };

        public static void Run()
        {
            string dir = ResolveEvidenceDir();
            Directory.CreateDirectory(dir);
            var report = new StringBuilder();
            report.AppendLine($"FG1-VFX-01 形变证据截图：Unity {Application.unityVersion}，图形设备 {SystemInfo.graphicsDeviceType}（{SystemInfo.graphicsDeviceName}），{Width}×{Height}，俯视正交");
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                report.AppendLine("没有图形设备（用了 -nographics？）——无法渲染");
                File.WriteAllText(Path.Combine(dir, "fg1-vfx-01-shots.txt"), report.ToString());
                return;
            }
            try
            {
                RunInGame(dir, report);
            }
            catch (Exception e)
            {
                report.AppendLine("游戏内截图抛异常：" + e);
            }
            try
            {
                RunGrid(dir, report);
            }
            catch (Exception e)
            {
                report.AppendLine("部件矩阵截图抛异常：" + e);
            }
            File.WriteAllText(Path.Combine(dir, "fg1-vfx-01-shots.txt"), report.ToString());
            Debug.Log(report.ToString());
        }

        /// <summary>BINGAMES_EVIDENCE_DIR 优先；否则从工程目录向上找仓库根（同时含 production/qa 与 TEngine）。找不到就抛异常，不在子仓库里乱落盘。</summary>
        private static string ResolveEvidenceDir()
        {
            string env = Environment.GetEnvironmentVariable("BINGAMES_EVIDENCE_DIR");
            if (!string.IsNullOrEmpty(env))
            {
                return env;
            }
            for (DirectoryInfo d = Directory.GetParent(Application.dataPath); d != null; d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, "production", "qa")) && Directory.Exists(Path.Combine(d.FullName, "TEngine")))
                {
                    return Path.Combine(d.FullName, "production", "qa", "evidence");
                }
            }
            throw new DirectoryNotFoundException($"从 {Application.dataPath} 向上找不到 BinGames 仓库根（含 production/qa 与 TEngine 的目录）；设置 BINGAMES_EVIDENCE_DIR 指定证据目录");
        }

        // ── 一、游戏内画面 ─────────────────────────────────────────────────────────────

        private static void RunInGame(string dir, StringBuilder report)
        {
            report.AppendLine("\n一、游戏内画面（main.unity 的平行光与环境光 + 真实家园 + WorldView 游戏镜头）");
            EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
            FgMachineMorphSelfCheck.EvidenceBegin(report);
            try
            {
                CampaignState s = FgMachineMorphSelfCheck.EvidenceNewHome(9201);
                int a = FgMachineMorphSelfCheck.EvidenceSpawnHome(FgMachineMorphSelfCheck.EvidenceBpCannonUp, new Vector2(4f, -4f));
                int g = FgMachineMorphSelfCheck.EvidenceSpawnHome(FgMachineMorphSelfCheck.EvidenceBpGunTrail, new Vector2(10f, -4f));
                int p = FgMachineMorphSelfCheck.EvidenceSpawnHome(FgMachineMorphSelfCheck.EvidenceBpGunPlain, new Vector2(16f, -4f));
                WorldSimulation.StepMany(2);
                FgMachineMorphSelfCheck.EvidenceEquipCore(s, FirmwareCatalog.FwOverloadId);
                FgMachineMorphSelfCheck.EvidenceFrames(20); // 地形 / 建筑 / 机器表现按正常帧建好
                HomeValleyController home = WorldSimulation.Home;
                bool selected = home != null && home.TrySelectMachine(a);
                FgMachineMorphSelfCheck.EvidenceFrames(2);

                Camera cam = WorldView.EnsureCamera();
                Vector3 posA = MachinePosition(a);
                Vector3 posG = MachinePosition(g);
                Vector3 posP = MachinePosition(p);
                var focus = new float2((posA.x + posP.x) * 0.5f, posA.z);
                report.AppendLine($"  重炮机 #{a}（带接入口，重炮）@({posA.x:0.0},{posA.z:0.0})；连射器 #{g}（机器电路自带拖尾，AI 常驻喷口态）@({posG.x:0.0},{posG.z:0.0})；" +
                                  $"连射器 #{p}（无固件，对照）@({posP.x:0.0},{posP.z:0.0})；左键选中的同一入口选中重炮机：{selected}");
                report.AppendLine($"  接入前：重炮机 {MachineMorph.Describe(MachineMorphView.VisibleOf(a))}；拖尾连射器 {MachineMorph.Describe(MachineMorphView.VisibleOf(g))}；" +
                                  $"对照连射器 {MachineMorph.Describe(MachineMorphView.VisibleOf(p))}；镜头模式 {WorldView.Director.Mode}");

                float[] orthos = { HomeValleyLayout.CameraBoundsHalfExtentZ, 46f };
                var before = new Texture2D[orthos.Length];
                var poses = new Vector3[orthos.Length];
                for (int i = 0; i < orthos.Length; i++)
                {
                    WorldView.Director.SetStrategyView(focus, orthos[i]); // 游戏镜头的正式战略摆位（焦点 + 正交半高）
                    poses[i] = cam.transform.position;
                    before[i] = Shoot(cam, dir, $"fg1-vfx-01-ingame-before-ortho{orthos[i]:0}.png", report, "接入前 · 战略");
                }
                WorldView.Director.SetStrategyView(focus, orthos[0]);

                FgMachineMorphSelfCheck.EvidencePressUplinkKey(); // 真实接入键（选中的机器）
                int n = 0;
                while (SignalUplinkService.IsPending && n++ < 40)
                {
                    FgMachineMorphSelfCheck.EvidenceFrames(1);
                }
                FgMachineMorphSelfCheck.EvidenceFrames(12); // 0.3 秒过渡走完
                bool uplinked = SignalPresence.CurrentMachineLogicId == a;
                report.AppendLine($"  接入后：信号在 #{SignalPresence.CurrentMachineLogicId}（{(uplinked ? "重炮机" : "不是重炮机")}）；重炮机 {MachineMorph.Describe(MachineMorphView.VisibleOf(a))}，" +
                                  $"形变态进度 {MachineMorphView.ProgressOf(a, MorphMask.Limiter):0.00}，信号光柱 {(MachineMorphView.SignalBeamShownOf(a) ? "亮" : "灭")}；镜头模式 {WorldView.Director.Mode}（正交半高 {cam.orthographicSize:0.0}）");
                Object.DestroyImmediate(Shoot(cam, dir, "fg1-vfx-01-ingame-uplinked-direct.png", report, "接入后 · 直控（游戏镜头原样）"));

                Vector3 keepPos = cam.transform.position;
                Quaternion keepRot = cam.transform.rotation;
                float keepOrtho = cam.orthographicSize;
                try
                {
                    for (int i = 0; i < orthos.Length; i++)
                    {
                        cam.transform.SetPositionAndRotation(poses[i], Quaternion.Euler(90f, 0f, 0f));
                        cam.orthographicSize = orthos[i];
                        Texture2D after = Shoot(cam, dir, $"fg1-vfx-01-ingame-uplinked-ortho{orthos[i]:0}.png", report, "接入后 · 同一战略位姿");
                        RectInt rA = BoxAround(cam, MachinePosition(a));
                        int diff = DiffPixels(before[i], rA, after, rA);
                        report.AppendLine($"    正交半高 {orthos[i]:0}：重炮机周围 ±{DiffHalfMeters:0} 米（{rA.width}×{rA.height}px）接入前 / 后明显不同的像素 {diff}px（{diff * 100f / Mathf.Max(1, rA.width * rA.height):0.0}%）");
                        RectInt rG = BoxAround(cam, MachinePosition(g));
                        RectInt rP = BoxAround(cam, MachinePosition(p));
                        report.AppendLine($"    正交半高 {orthos[i]:0}：拖尾连射器（喷口态）方框内自发光橙色像素 {GlowPixels(after, rG, MachineMorphLibrary.FluidGlow)}px，" +
                                          $"对照连射器 {GlowPixels(after, rP, MachineMorphLibrary.FluidGlow)}px；重炮机方框内红热像素 {GlowPixels(after, rA, MachineMorphLibrary.LimiterGlow)}px、青蓝（信号光柱）像素 {GlowPixels(after, rA, MachineMorphLibrary.EmGlow)}px");
                        Object.DestroyImmediate(after);
                    }
                }
                finally
                {
                    cam.transform.SetPositionAndRotation(keepPos, keepRot);
                    cam.orthographicSize = keepOrtho;
                }
                foreach (Texture2D t in before)
                {
                    Object.DestroyImmediate(t);
                }
                if (FgMachineMorphSelfCheck.EvidencePrepFailures > 0)
                {
                    report.AppendLine($"  ✗ 世界准备有 {FgMachineMorphSelfCheck.EvidencePrepFailures} 处失败（见上）");
                }
            }
            finally
            {
                FgMachineMorphSelfCheck.EvidenceEnd();
            }
        }

        private static Vector3 MachinePosition(int logicId)
        {
            Transform grp = MachineMorphView.GroupOf(logicId, MorphMask.Limiter);
            Transform host = grp != null && grp.parent != null ? grp.parent.parent : null;
            return host != null ? host.position : Vector3.zero;
        }

        private static Texture2D Shoot(Camera cam, string dir, string file, StringBuilder report, string what)
        {
            Texture2D tex = Render(cam);
            File.WriteAllBytes(Path.Combine(dir, file), tex.EncodeToPNG());
            report.AppendLine($"  {what}：正交半高 {cam.orthographicSize:0.0} → {file}");
            return tex;
        }

        /// <summary>机器周围 ±<see cref="DiffHalfMeters"/> 米在画面上的像素范围（按 1920×1080 的宽高比投影）。</summary>
        private static RectInt BoxAround(Camera cam, Vector3 world)
        {
            cam.aspect = (float)Width / Height;
            Vector3 v = cam.WorldToViewportPoint(world);
            cam.ResetAspect();
            float ppm = Height / (2f * cam.orthographicSize);
            float half = DiffHalfMeters * ppm;
            float cx = v.x * Width, cy = v.y * Height;
            int x0 = Mathf.Clamp(Mathf.RoundToInt(cx - half), 0, Width - 1);
            int x1 = Mathf.Clamp(Mathf.RoundToInt(cx + half), 0, Width - 1);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(cy - half), 0, Height - 1);
            int y1 = Mathf.Clamp(Mathf.RoundToInt(cy + half), 0, Height - 1);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>方框内与某自发光色接近（每通道差 ≤ 0.2）的像素数——无光照着色器画出来就是这个颜色，地形与车体很少碰巧是。</summary>
        private static int GlowPixels(Texture2D tex, RectInt r, Color glow)
        {
            int n = 0;
            for (int x = r.x; x < r.x + r.width; x++)
            {
                for (int y = r.y; y < r.y + r.height; y++)
                {
                    Color c = tex.GetPixel(x, y);
                    if (Mathf.Abs(c.r - glow.r) <= 0.2f && Mathf.Abs(c.g - glow.g) <= 0.2f && Mathf.Abs(c.b - glow.b) <= 0.2f)
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        // ── 二、部件矩阵（补充） ────────────────────────────────────────────────────────

        private static void RunGrid(string dir, StringBuilder report)
        {
            report.AppendLine("\n二、部件矩阵（补充；空场景、自设平行光，只用来逐套对照部件，不代表游戏画面）");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var lightGo = new GameObject("Sun");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, -30f, 0f);
            RenderSettings.ambientLight = new Color(0.45f, 0.47f, 0.5f);

            string[] rows = { "comp_gun", "comp_beam", "comp_repairbeam", "comp_cannon", "func_dash", "func_marker" };
            var bodyColor = new Color(0.7f, 0.75f, 0.8f); // 与家园机器同色（HomeValleyController.BuildMachineView）
            for (int r = 0; r < rows.Length; r++)
            {
                for (int c = 0; c < Columns.Length; c++)
                {
                    GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    go.name = $"M_{rows[r]}_{MachineMorph.Describe(Columns[c])}";
                    go.transform.position = CellCenter(r, c, rows.Length) + Vector3.up;
                    Renderer renderer = go.GetComponent<Renderer>();
                    renderer.sharedMaterial = ViewMaterials.Standard(bodyColor);
                    PlaceholderSilhouette.ApplyMachine(go, renderer, HomeValleyLayout.Erc003ChassisId);
                    bool isPrimary = rows[r].StartsWith("comp_");
                    var groups = new Transform[3];
                    MachineMorphView.BuildRig(go.transform, isPrimary ? rows[r] : null, isPrimary ? null : rows[r], out _, groups);
                    for (int i = 0; i < 3; i++)
                    {
                        bool on = (Columns[c] & MachineMorph.Categories[i]) != 0;
                        groups[i].gameObject.SetActive(on);
                        groups[i].localScale = Vector3.one;
                    }
                    Transform beam = groups[MachineMorph.IndexOf(MorphMask.Limiter)].Find(MachineMorphLibrary.SignalBeamName);
                    if (beam != null)
                    {
                        beam.gameObject.SetActive(true); // 矩阵按“接入中”画：形变态带信号光柱
                    }
                }
            }

            var camGo = new GameObject("StrategyCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.18f, 0.16f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 200f;
            camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 与 WorldView 战略镜头相同：正俯视
            camGo.transform.position = new Vector3(0f, 60f, 0f);

            foreach (float ortho in new[] { HomeValleyLayout.CameraBoundsHalfExtentZ, 46f, 8f })
            {
                cam.orthographicSize = ortho;
                cam.aspect = (float)Width / Height;
                Texture2D tex = Render(cam);
                string file = Path.Combine(dir, $"fg1-vfx-01-strategy-ortho{ortho:0}.png");
                File.WriteAllBytes(file, tex.EncodeToPNG());
                report.AppendLine($"\n正交半高 {ortho}（{(ortho >= 46f ? "铸造前哨默认，被缩放上限夹到 46" : ortho >= 30f ? "家园 / 破碎都市默认" : "近景参考，不是默认缩放")}）→ {Path.GetFileName(file)}");
                if (ortho >= 30f)
                {
                    for (int r = 0; r < rows.Length; r++)
                    {
                        var line = new StringBuilder($"  {rows[r],-16}");
                        RectInt baseCell = CellRect(cam, r, 0, rows.Length);
                        for (int c = 1; c < Columns.Length; c++)
                        {
                            int diff = DiffPixels(tex, baseCell, tex, CellRect(cam, r, c, rows.Length));
                            line.Append($" {MachineMorph.Describe(Columns[c])}={diff}px");
                        }
                        report.AppendLine(line.ToString());
                    }
                }
                Object.DestroyImmediate(tex);
            }
            report.AppendLine("\n列：none | limiter | fluid | em | limiter+fluid | limiter+em | fluid+em | 三类全开；行：6 个作战组件（前 4 个为主组件，后 2 个为功能组件单装）。");
            report.AppendLine("数字 = 与同一行“不变形”那一格相比颜色明显不同（任一通道差 > 0.12）的像素数。");
            MachineMorphLibrary.ReleaseAll();
        }

        private static Vector3 CellCenter(int r, int c, int rowCount) =>
            new Vector3((c - (Columns.Length - 1) * 0.5f) * Spacing, 0f, ((rowCount - 1) * 0.5f - r) * Spacing);

        /// <summary>格子在画面上的像素范围：相机正俯视、对准世界原点，每米 = 画面高 / (2 × 正交半高) 像素（不经相机 API，避免渲染后换回屏幕宽高比）。</summary>
        private static RectInt CellRect(Camera cam, int r, int c, int rowCount)
        {
            float ppm = Height / (2f * cam.orthographicSize);
            Vector3 center = CellCenter(r, c, rowCount);
            float cx = Width * 0.5f + center.x * ppm;
            float cy = Height * 0.5f + center.z * ppm;
            float half = Spacing * 0.5f * ppm;
            int x0 = Mathf.Clamp(Mathf.RoundToInt(cx - half), 0, Width - 1);
            int x1 = Mathf.Clamp(Mathf.RoundToInt(cx + half), 0, Width - 1);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(cy - half), 0, Height - 1);
            int y1 = Mathf.Clamp(Mathf.RoundToInt(cy + half), 0, Height - 1);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        private static int DiffPixels(Texture2D texA, RectInt a, Texture2D texB, RectInt b)
        {
            int w = Mathf.Min(a.width, b.width), h = Mathf.Min(a.height, b.height);
            int n = 0;
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    Color p = texA.GetPixel(a.x + x, a.y + y);
                    Color q = texB.GetPixel(b.x + x, b.y + y);
                    if (Mathf.Abs(p.r - q.r) > 0.12f || Mathf.Abs(p.g - q.g) > 0.12f || Mathf.Abs(p.b - q.b) > 0.12f)
                    {
                        n++;
                    }
                }
            }
            return n;
        }

        private static Texture2D Render(Camera cam)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            cam.aspect = (float)Width / Height;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            cam.ResetAspect();
            rt.Release();
            Object.DestroyImmediate(rt);
            return tex;
        }
    }
}
