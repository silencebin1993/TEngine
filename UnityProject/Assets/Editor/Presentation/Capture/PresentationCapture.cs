// PL-QA-01 → production/design/presentation-track/handoff/PL-QA-01.md
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BinGames.Presentation;
using BinGames.PresentationLab;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.View.Presentation;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>采集场景登记：<c>[PresentationCaptureScene("名字")] public static void 名字(PresentationCaptureContext c)</c>（任何编辑器类里都行，后续卡直接追加）。</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class PresentationCaptureSceneAttribute : Attribute
    {
        public readonly string Name;
        public PresentationCaptureSceneAttribute(string name) => Name = name;
    }

    /// <summary>注入缺陷（只打在“开”的那张、目标遮罩的渲染器上）：品红 = 换成缺 shader 的材质；冻结 = 宿主 GameDt 恒 0；下沉 = 目标整体下移 0.5 米。</summary>
    public enum CaptureInject
    {
        None,
        Magenta,
        Freeze,
        Sink,
    }

    /// <summary>场景方法拿到的上下文：打开场景、建世界、摆镜头、登记要采的模块与目标遮罩。开 / 关两次各调一次场景方法（各自从头建）。</summary>
    public sealed class PresentationCaptureContext
    {
        /// <summary>开 / 关成对切换的功能（默认宿主总开关 = “PL 整体 vs 现状”）。</summary>
        public string Feature = PresentationFeatureRegistry.HostId;
        /// <summary>本次是“开”还是“关”（只读，场景方法一般不用看）。</summary>
        public bool FeatureOn { get; internal set; }
        public Camera Camera;
        public int WarmupFrames = 30;
        /// <summary>有世界（main.unity + 家园）：每帧 WorldSimulation.Frame + TickManual；Lab 为 false：只喂 Synthetic 帧。</summary>
        public bool InWorld { get; private set; }
        public readonly List<IPresentationModule> Modules = new List<IPresentationModule>();
        /// <summary>目标遮罩（剪影像素、明度差、注入缺陷都按它）；null = 本场景没有目标。</summary>
        public Func<Renderer[]> MaskTargets;
        /// <summary>目标在 10 帧短片里应当在动（AddTarget 置 true；静止目标如建筑、地物保持 false）：pl_metrics 只对它判“动画冻结”。</summary>
        public bool TargetMoves;
        public int Seed { get; private set; }
        internal bool Evidence;
        /// <summary>世界内采集帧里“本帧模拟推进的游戏秒（Ticks 增量 × StepSeconds）≠ 未冻结时传给宿主的 GameDt”的帧数。应为 0。</summary>
        internal int ClockMismatchFrames;
        /// <summary>最近一帧模拟推进的游戏秒。</summary>
        internal double LastSimSeconds;

        /// <summary>真战役：打开正式启动场景 main.unity，固定种子新开局、家园载入并被游戏镜头观察（入口同 MorphEvidenceCapture / FgMachineMorphSelfCheck.NewHome）。</summary>
        public void OpenHome(int seed = PresentationCapture.DefaultSeed)
        {
            EditorSceneManager.OpenScene(PresentationCapture.MainScene, OpenSceneMode.Single);
            var prep = new StringBuilder();
            FgMachineMorphSelfCheck.EvidenceBegin(prep);
            Evidence = true;
            FgMachineMorphSelfCheck.EvidenceNewHome(seed);
            if (FgMachineMorphSelfCheck.EvidencePrepFailures > 0)
            {
                // 进 Shot.error，不出“残缺家园”的图（先例 MorphEvidenceCapture 同样判 ✗）
                throw new InvalidOperationException($"家园准备失败 {FgMachineMorphSelfCheck.EvidencePrepFailures} 项：" + prep.ToString().Replace("✓", "·").Replace("✗", "×"));
            }
            Seed = seed;
            InWorld = true;
            Camera = WorldView.EnsureCamera();
        }

        /// <summary>Lab 台子（PL-INF-01）：55° 俯视正交相机，内容由 PresentationLabRig 程序生成。</summary>
        public void OpenLab()
        {
            Scene scene = EditorSceneManager.OpenScene(PresentationValidate.LabScenePath, OpenSceneMode.Single);
            PresentationLabRig rig = scene.GetRootGameObjects().Select(g => g.GetComponent<PresentationLabRig>()).FirstOrDefault(r => r != null);
            if (rig == null)
            {
                throw new InvalidOperationException("Lab 场景里没有 PresentationLabRig");
            }
            rig.Build();
            Camera = rig.LabCamera;
            InWorld = false;
        }

        /// <summary>摆一个采集标定靶（<see cref="PresentationCaptureTarget"/>，功能 pl.capture），并把它设成本场景的目标遮罩与开 / 关切换的功能。</summary>
        public PresentationCaptureTarget AddTarget(Vector3 anchor)
        {
            var t = new PresentationCaptureTarget(anchor);
            Modules.Add(t);
            MaskTargets = () => t.Renderers;
            TargetMoves = true;
            Feature = PresentationFeatureRegistry.CaptureId;
            return t;
        }

        /// <summary>地面点：先向下射线找碰撞体，找不到按 y = 0（游戏的格网拾取同样按 y = 0 平面，见 ConstructionSiteView）。</summary>
        public static Vector3 Ground(float x, float z)
        {
            Physics.SyncTransforms();
            return Physics.Raycast(new Vector3(x, 500f, z), Vector3.down, out RaycastHit hit, 1000f) ? hit.point : new Vector3(x, 0f, z);
        }
    }

    /// <summary>
    /// PL-QA-01 无人值守画面采集：编辑模式确定性路线（基线用）。每个登记场景各拍“关 / 开”一对 1920×1080 + 0.5 倍缩略图，
    /// 有目标时另出剪影遮罩（目标去影子渲一张、隐藏目标再渲一张，逐像素不同处 = 目标的可见剪影，地形遮挡自然算进去），
    /// 再连拍 10 帧 0.5 倍短片帧（帧间差分 / GIF）；HDR（ARGBHalf）再渲一次数 NaN / Inf。元数据写 <c>capture.json</c>，
    /// 图像指标由 <c>tools/pl/pl_metrics.py</c> 算（写 metrics.json）。一条命令：<c>bash tools/pl/pl-capture.sh</c>。
    /// 时钟口径（PL-INF-07 交接卡 §0：GameDt = 本帧模拟推进的游戏秒）：世界内每帧经 FgMachineMorphSelfCheck.EvidenceFrames(1) 推进
    /// <c>WorldSimulation.Frame(0.05)</c>（它的 FrameDt = 0.05 秒，StepHz 60 时 = 3 步，含视图更新），再 <c>PresentationHostBridge.TickManual(0.05, GameClock.FrameScaledDt)</c>
    /// （= 0.05 游戏秒；RealDt 同自检 SetUp 里的 CameraDirector.RealDeltaTime）；每帧核对 Ticks 增量 × StepSeconds = GameDt（Shot.clockMismatchFrames）。
    /// Lab 每帧 <c>PresentationHost.Tick(Synthetic 帧, 0.05)</c>。10 帧短片 = 0.5 游戏秒。
    /// 必须在带显卡的 batchmode 里跑（pl-unity.sh gpu），不是 RTX 4070 出的图作废（退出码 3）。
    /// </summary>
    public static class PresentationCapture
    {
        public const string MainScene = "Assets/Scenes/main.unity";
        public const int DefaultSeed = 9201;
        public const int Width = 1920;
        public const int Height = 1080;
        public const int BurstFrames = 10;
        /// <summary>每个采集帧的秒数 = FgMachineMorphSelfCheck.FrameDt（EvidenceFrames 每帧推进的游戏秒），也是它 SetUp 里的 CameraDirector.RealDeltaTime。</summary>
        public const float Dt = 0.05f;
        public const string RequiredGpu = "RTX 4070";

        /// <summary>实际核对的显卡名：环境变量 PL_CAPTURE_REQUIRED_GPU 可覆盖（只用来测“不是 4070 → 退出码 3”这条路径）。</summary>
        public static string RequiredGpuName
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("PL_CAPTURE_REQUIRED_GPU");
                return string.IsNullOrWhiteSpace(env) ? RequiredGpu : env.Trim();
            }
        }
        public const float SinkMeters = 0.5f;

        // ── 首批采集场景 ──

        [PresentationCaptureScene("HomeDefault")]
        public static void HomeDefault(PresentationCaptureContext c) => c.OpenHome();

        /// <summary>同 MorphEvidenceCapture 的第二档：正交半高 46（铸造前哨默认被夹到的高度）。</summary>
        [PresentationCaptureScene("HomeWide")]
        public static void HomeWide(PresentationCaptureContext c)
        {
            c.OpenHome();
            WorldView.Director.SetStrategyView(WorldView.Director.StrategyFocus, 46f);
        }

        /// <summary>开局镜头 + 采集标定靶（注入缺陷与遮罩指标的被摄体）。开 / 关 = 标定靶有 / 无。</summary>
        [PresentationCaptureScene("HomeTarget")]
        public static void HomeTarget(PresentationCaptureContext c)
        {
            c.OpenHome();
            Unity.Mathematics.float2 f = WorldView.Director.StrategyFocus;
            c.AddTarget(PresentationCaptureContext.Ground(f.x + TargetOffset.x, f.y + TargetOffset.y));
        }

        /// <summary>标定靶相对开局镜头焦点的偏移（米，x / z）：避开核心建筑、留在画面中部偏右下。</summary>
        public static readonly Vector2 TargetOffset = new Vector2(8f, -4f);

        [PresentationCaptureScene("LabSmoke")]
        public static void LabSmoke(PresentationCaptureContext c)
        {
            c.OpenLab();
            c.AddTarget(new Vector3(-6f, 0f, -6f));
        }

        /// <summary>Play 路线冒烟（gpuplay 入口，卡片里的名字）：见 <see cref="PresentationPlayCapture.PlayHomeMenu"/>。</summary>
        public static void PlayHomeMenu() => PresentationPlayCapture.PlayHomeMenu();

        // ── 登记表 ──

        public static IReadOnlyDictionary<string, MethodInfo> Scenes
        {
            get
            {
                var d = new SortedDictionary<string, MethodInfo>(StringComparer.Ordinal);
                foreach (MethodInfo m in TypeCache.GetMethodsWithAttribute<PresentationCaptureSceneAttribute>())
                {
                    string name = m.GetCustomAttribute<PresentationCaptureSceneAttribute>().Name;
                    if (d.TryGetValue(name, out MethodInfo other))
                    {
                        throw new InvalidOperationException($"采集场景重名 {name}：{other.DeclaringType}.{other.Name} 与 {m.DeclaringType}.{m.Name}");
                    }
                    d[name] = m;
                }
                return d;
            }
        }

        // ── 输出元数据（capture.json，字段名稳定：FG15-ACC-04 复用） ──

        [Serializable]
        public sealed class Shot
        {
            public string scene;
            public string state; // off / on
            public string feature;
            public string inject; // None / Magenta / Freeze / Sink
            public string file; // <scene>_<state>.png（1920×1080）
            public string half; // <scene>_<state>_half.png（960×540）
            public string mask; // <scene>_<state>_mask.png（白 = 目标可见剪影；无目标为空）
            public string burst; // <scene>_<state>_burst/fNN.png（0.5 倍，BurstFrames + 1 帧）
            public int burstFrames;
            public int warmupFrames;
            public int seed;
            public bool orthographic;
            public float orthographicSize;
            public float fieldOfView;
            public Vector3 cameraPosition;
            public Vector3 cameraEuler;
            public int nanPixels;
            public int infPixels;
            public int maskPixels;
            public int targetRenderers;
            /// <summary>同一帧连渲两遍的不同像素数（应为 0：渲染本身确定、实例化绘制队列不被第一次渲染吃掉）。</summary>
            public int repeatDiffPixels;
            /// <summary>目标应当在动（场景声明）：pl_metrics 只对它判“动画冻结”。</summary>
            public bool expectMotion;
            /// <summary>最后一个采集帧模拟推进的游戏秒（世界内应 = 0.05）；Lab 为 0。</summary>
            public double simSecondsPerFrame;
            /// <summary>Ticks 增量 × StepSeconds ≠ 传给宿主 GameDt 的帧数（应为 0）。</summary>
            public int clockMismatchFrames;
            public string error;
        }

        [Serializable]
        public sealed class Run
        {
            public bool valid;
            public string invalidReason;
            public string device;
            public string deviceType;
            public string unity;
            public string pipeline;
            public string colorSpace;
            public string machine;
            public int width = Width;
            public int height = Height;
            public string startedUtc;
            public List<Shot> shots = new List<Shot>();
        }

        public static bool DeviceOk(out string device)
        {
            device = $"{SystemInfo.graphicsDeviceName}（{SystemInfo.graphicsDeviceType}）";
            return SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && SystemInfo.graphicsDeviceName.Contains(RequiredGpuName);
        }

        public static Run NewRun()
        {
            bool ok = DeviceOk(out string device);
            return new Run
            {
                valid = ok,
                invalidReason = ok ? null : $"采集无效：图形设备 {device} 不是 {RequiredGpuName}",
                device = SystemInfo.graphicsDeviceName,
                deviceType = SystemInfo.graphicsDeviceType.ToString(),
                unity = Application.unityVersion,
                pipeline = GraphicsSettings.currentRenderPipeline == null ? "builtin" : GraphicsSettings.currentRenderPipeline.GetType().Name,
                colorSpace = QualitySettings.activeColorSpace.ToString().ToLowerInvariant(),
                machine = Environment.MachineName,
                startedUtc = DateTime.UtcNow.ToString("o"),
            };
        }

        public static void WriteRun(Run run, string outDir) => File.WriteAllText(Path.Combine(outDir, "capture.json"), JsonUtility.ToJson(run, true));

        // ── 命令行入口 ──

        /// <summary>
        /// <c>PL_CAPTURE_SCENES</c>=A,B（缺省全部登记场景）、<c>PL_CAPTURE_OUT</c>=输出目录（缺省 D:/BinGames-PL-review/_capture/时间戳）、
        /// <c>PL_CAPTURE_INJECT</c>=None|Magenta|Freeze|Sink、<c>PL_CAPTURE_STATES</c>=off,on（缺省两张都拍）。退出码：0 成功、1 有场景出错、3 不是 RTX 4070（采集无效）。
        /// </summary>
        public static void RunFromCommandLine()
        {
            int code;
            try
            {
                code = RunFromEnvironment();
            }
            catch (Exception e)
            {
                Debug.LogError("[PL 采集] 抛异常：" + e);
                code = 1;
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }

        private static int RunFromEnvironment()
        {
            string outDir = Environment.GetEnvironmentVariable("PL_CAPTURE_OUT");
            if (string.IsNullOrWhiteSpace(outDir))
            {
                outDir = Path.Combine(ReviewRoot(), "_capture", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            }
            Directory.CreateDirectory(outDir);
            string names = Environment.GetEnvironmentVariable("PL_CAPTURE_SCENES");
            string[] scenes = string.IsNullOrWhiteSpace(names) ? Scenes.Keys.ToArray() : names.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            string injectText = Environment.GetEnvironmentVariable("PL_CAPTURE_INJECT");
            CaptureInject inject = string.IsNullOrWhiteSpace(injectText) ? CaptureInject.None : (CaptureInject)Enum.Parse(typeof(CaptureInject), injectText.Trim(), true);
            string statesText = Environment.GetEnvironmentVariable("PL_CAPTURE_STATES");
            bool[] states = string.IsNullOrWhiteSpace(statesText) ? new[] { false, true } : statesText.Split(',').Select(x => x.Trim() == "on").ToArray();

            Run run = NewRun();
            if (!run.valid)
            {
                WriteRun(run, outDir);
                Debug.LogError("[PL 采集] " + run.invalidReason + "；结论：采集无效（退出码 3）");
                return 3;
            }
            int errors = 0;
            foreach (string name in scenes)
            {
                foreach (Shot s in Capture(name, outDir, inject, states))
                {
                    run.shots.Add(s);
                    if (s.error != null)
                    {
                        errors++;
                    }
                }
            }
            WriteRun(run, outDir);
            Debug.Log($"[PL 采集] 输出 {outDir}：{run.shots.Count} 张（{string.Join(", ", run.shots.Select(s => s.scene + "_" + s.state + (s.error != null ? "✗" : "")))}），设备 {run.device}；" +
                      $"结论：{(errors == 0 ? "采集完成" : $"{errors} 张出错")}");
            return errors == 0 ? 0 : 1;
        }

        // ── 采集一个场景 ──

        /// <summary>按 <paramref name="states"/>（false = 关、true = 开）逐张从头建场景、预热、拍主图 / 缩略图 / 遮罩 / HDR、连拍短片帧。出错记在 Shot.error，不抛。</summary>
        public static List<Shot> Capture(string sceneName, string outDir, CaptureInject inject = CaptureInject.None, params bool[] states)
        {
            var shots = new List<Shot>();
            if (!Scenes.TryGetValue(sceneName, out MethodInfo method))
            {
                shots.Add(new Shot { scene = sceneName, error = $"没有登记采集场景 {sceneName}（已登记：{string.Join(", ", Scenes.Keys)}）" });
                return shots;
            }
            if (states == null || states.Length == 0)
            {
                states = new[] { false, true };
            }
            Directory.CreateDirectory(outDir);
            foreach (bool on in states)
            {
                var shot = new Shot { scene = sceneName, state = on ? "on" : "off", inject = (on ? inject : CaptureInject.None).ToString() };
                try
                {
                    CaptureOne(method, on, on ? inject : CaptureInject.None, outDir, shot);
                }
                catch (Exception e)
                {
                    shot.error = e.ToString();
                    Debug.LogError($"[PL 采集] {sceneName}_{shot.state} 出错：{e}");
                }
                shots.Add(shot);
            }
            return shots;
        }

        private static void CaptureOne(MethodInfo method, bool on, CaptureInject inject, string outDir, Shot shot)
        {
            var c = new PresentationCaptureContext { FeatureOn = on };
            RenderTexture rt = null, half = null;
            Texture2D tex = null, halfTex = null;
            Camera cam = null;
            Renderer[] targets = new Renderer[0];
            try
            {
                method.Invoke(null, new object[] { c });
                // 场景方法会 OpenScene（顺带卸掉没被引用的资源），所以 RT / 贴图在它之后再建
                rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
                half = new RenderTexture(Width / 2, Height / 2, 0, RenderTextureFormat.ARGB32);
                tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                halfTex = new Texture2D(Width / 2, Height / 2, TextureFormat.RGBA32, false);
                cam = c.Camera != null ? c.Camera : throw new InvalidOperationException("场景方法没有给出相机");
                cam.aspect = (float)Width / Height;
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.HostId, true);
                PresentationFeatureRegistry.Set(c.Feature, on);
                foreach (IPresentationModule m in c.Modules)
                {
                    PresentationHost.Register(m);
                }
                shot.feature = c.Feature;
                shot.seed = c.Seed;
                shot.warmupFrames = c.WarmupFrames;
                double gameSeconds = 0;
                for (int i = 0; i < c.WarmupFrames; i++)
                {
                    Step(c, inject, ref gameSeconds);
                    Render(cam, rt);
                }
                targets = c.MaskTargets?.Invoke() ?? new Renderer[0];
                shot.targetRenderers = targets.Length;
                string stem = $"{shot.scene}_{shot.state}";

                // 主图（注入只在渲染期间生效，渲完还原）
                using (new Injection(inject, targets))
                {
                    Render(cam, rt);
                    ReadBack(rt, tex);
                    File.WriteAllBytes(Path.Combine(outDir, shot.file = stem + ".png"), tex.EncodeToPNG());
                    Color32[] a = tex.GetPixels32();
                    Render(cam, rt);
                    ReadBack(rt, tex);
                    shot.repeatDiffPixels = CountDiff(a, tex.GetPixels32(), 0);
                    ReadBack(rt, tex); // 保持 tex = 主图
                    Graphics.Blit(rt, half);
                    ReadBack(half, halfTex);
                    File.WriteAllBytes(Path.Combine(outDir, shot.half = stem + "_half.png"), halfTex.EncodeToPNG());
                    CountNonFinite(cam, out shot.nanPixels, out shot.infPixels);
                    if (targets.Length > 0)
                    {
                        shot.mask = stem + "_mask.png";
                        shot.maskPixels = WriteMask(cam, rt, tex, targets, Path.Combine(outDir, shot.mask));
                    }
                }

                // 短片帧（0.5 倍）：第 0 帧 = 主图这一帧，之后每帧推进一次
                string burstDir = Path.Combine(outDir, shot.burst = stem + "_burst");
                Directory.CreateDirectory(burstDir);
                for (int k = 0; k <= BurstFrames; k++)
                {
                    if (k > 0)
                    {
                        Step(c, inject, ref gameSeconds);
                    }
                    using (new Injection(inject, targets))
                    {
                        Render(cam, rt);
                        Graphics.Blit(rt, half);
                        ReadBack(half, halfTex);
                        File.WriteAllBytes(Path.Combine(burstDir, $"f{k:00}.png"), halfTex.EncodeToPNG());
                    }
                }
                shot.burstFrames = BurstFrames + 1;
                shot.orthographic = cam.orthographic;
                shot.orthographicSize = cam.orthographicSize;
                shot.fieldOfView = cam.fieldOfView;
                shot.cameraPosition = cam.transform.position;
                shot.cameraEuler = cam.transform.eulerAngles;
                shot.expectMotion = c.TargetMoves && targets.Length > 0;
                shot.simSecondsPerFrame = c.LastSimSeconds;
                shot.clockMismatchFrames = c.ClockMismatchFrames;
            }
            finally
            {
                foreach (IPresentationModule m in c.Modules)
                {
                    PresentationHost.Unregister(m);
                }
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationHost.Reconcile();
                if (cam != null)
                {
                    cam.targetTexture = null;
                    cam.ResetAspect();
                }
                if (c.Evidence)
                {
                    FgMachineMorphSelfCheck.EvidenceEnd();
                }
                if (PresentationHost.ModuleCount == 0)
                {
                    PresentationHost.DestroyRoot();
                }
                foreach (RenderTexture r in new[] { rt, half })
                {
                    if (r != null)
                    {
                        r.Release();
                        Object.DestroyImmediate(r);
                    }
                }
                foreach (Texture2D t in new[] { tex, halfTex })
                {
                    if (t != null)
                    {
                        Object.DestroyImmediate(t);
                    }
                }
            }
        }

        /// <summary>推进一帧：世界内 = EvidenceFrames(1)（WorldSimulation.Frame(0.05)，含视图）+ TickManual(0.05, FrameScaledDt)；Lab = Synthetic 帧。冻结注入 = 宿主 GameDt 恒 0（不碰 sim）。</summary>
        private static void Step(PresentationCaptureContext c, CaptureInject inject, ref double gameSeconds)
        {
            bool freeze = inject == CaptureInject.Freeze;
            if (c.InWorld)
            {
                long ticks = GameClock.Ticks;
                FgMachineMorphSelfCheck.EvidenceFrames(1);
                float live = GameClock.FrameScaledDt;
                c.LastSimSeconds = (GameClock.Ticks - ticks) * (double)GameClock.StepSeconds;
                if (Math.Abs(c.LastSimSeconds - live) > 1e-4)
                {
                    c.ClockMismatchFrames++;
                }
                PresentationHostBridge.TickManual(Dt, freeze ? 0f : live);
            }
            else
            {
                float gameDt = freeze ? 0f : Dt;
                gameSeconds += gameDt;
                PresentationHost.Tick(new PresentationFrame(Dt, gameDt, gameSeconds, 0, 0f, false, false, null, null));
            }
        }

        private static void Render(Camera cam, RenderTexture rt)
        {
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
        }

        private static void ReadBack(RenderTexture rt, Texture2D tex)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
        }

        private static int CountDiff(Color32[] a, Color32[] b, int threshold)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (Math.Abs(a[i].r - b[i].r) > threshold || Math.Abs(a[i].g - b[i].g) > threshold || Math.Abs(a[i].b - b[i].b) > threshold)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>剪影遮罩：目标去影子渲一张、隐藏目标再渲一张，逐像素不同（任一通道差 &gt; 3/255）= 目标可见剪影（被地形 / 建筑挡住的不算）。返回剪影像素数。</summary>
        private static int WriteMask(Camera cam, RenderTexture rt, Texture2D tex, Renderer[] targets, string path)
        {
            ShadowCastingMode[] shadows = targets.Select(r => r.shadowCastingMode).ToArray();
            try
            {
                foreach (Renderer r in targets)
                {
                    r.shadowCastingMode = ShadowCastingMode.Off;
                }
                Render(cam, rt);
                ReadBack(rt, tex);
                Color32[] with = tex.GetPixels32();
                foreach (Renderer r in targets)
                {
                    r.forceRenderingOff = true;
                }
                Render(cam, rt);
                ReadBack(rt, tex);
                Color32[] without = tex.GetPixels32();
                int n = 0;
                for (int i = 0; i < with.Length; i++)
                {
                    bool hit = Math.Abs(with[i].r - without[i].r) > 3 || Math.Abs(with[i].g - without[i].g) > 3 || Math.Abs(with[i].b - without[i].b) > 3;
                    byte v = hit ? (byte)255 : (byte)0;
                    without[i] = new Color32(v, v, v, 255);
                    n += hit ? 1 : 0;
                }
                tex.SetPixels32(without);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                return n;
            }
            finally
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    targets[i].forceRenderingOff = false;
                    targets[i].shadowCastingMode = shadows[i];
                }
            }
        }

        /// <summary>HDR 再渲一次（ARGBHalf），按半精度位模式数 NaN / Inf（指数全 1：尾数非 0 = NaN、0 = Inf）。</summary>
        private static void CountNonFinite(Camera cam, out int nan, out int inf)
        {
            nan = 0;
            inf = 0;
            var hdr = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf) { antiAliasing = 1 };
            var t = new Texture2D(Width, Height, TextureFormat.RGBAHalf, false);
            bool hdrWas = cam.allowHDR;
            try
            {
                cam.allowHDR = true;
                Render(cam, hdr);
                ReadBack(hdr, t);
                NativeArray<ushort> raw = t.GetRawTextureData<ushort>();
                for (int i = 0; i < raw.Length; i += 4)
                {
                    bool n = false, f = false;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        ushort h = raw[i + ch];
                        if ((h & 0x7C00) == 0x7C00)
                        {
                            n |= (h & 0x03FF) != 0;
                            f |= (h & 0x03FF) == 0;
                        }
                    }
                    nan += n ? 1 : 0;
                    inf += f && !n ? 1 : 0;
                }
            }
            finally
            {
                cam.allowHDR = hdrWas;
                hdr.Release();
                Object.DestroyImmediate(hdr);
                Object.DestroyImmediate(t);
            }
        }

        /// <summary>注入缺陷的渲染期作用域：品红 = 目标渲染器临时换成缺 shader 的材质（Unity 缺 shader 时用的同一个错误 shader）；下沉 = 目标根节点临时下移 0.5 米。</summary>
        private readonly struct Injection : IDisposable
        {
            private readonly Renderer[] _targets;
            private readonly Material[][] _materials;
            private readonly Transform[] _roots;

            public Injection(CaptureInject kind, Renderer[] targets)
            {
                _targets = targets;
                _materials = null;
                _roots = null;
                if (kind == CaptureInject.Magenta)
                {
                    _materials = targets.Select(r => r.sharedMaterials).ToArray();
                    Material broken = BrokenMaterial;
                    foreach (Renderer r in targets)
                    {
                        r.sharedMaterials = Enumerable.Repeat(broken, r.sharedMaterials.Length).ToArray();
                    }
                }
                else if (kind == CaptureInject.Sink)
                {
                    _roots = targets.Select(r => r.transform.parent != null ? r.transform.parent : r.transform).Distinct().ToArray();
                    foreach (Transform t in _roots)
                    {
                        t.position += Vector3.down * SinkMeters;
                    }
                }
            }

            public void Dispose()
            {
                if (_materials != null)
                {
                    for (int i = 0; i < _targets.Length; i++)
                    {
                        _targets[i].sharedMaterials = _materials[i];
                    }
                }
                if (_roots != null)
                {
                    foreach (Transform t in _roots)
                    {
                        t.position += Vector3.up * SinkMeters;
                    }
                }
            }
        }

        private static Material _broken;

        // 注入“缺 shader”专用：故意绕过 PresentationMaterials（它会回退到 Standard、不出品红）
        private static Material BrokenMaterial => _broken != null ? _broken : _broken = new Material(Shader.Find("Hidden/InternalErrorShader")) { name = "PL capture inject: missing shader" };

        // ── GPU 毫秒成对测量 ──

        public struct GpuPair
        {
            public double OffP50, OffP95, OnP50, OnP95;
            public double DeltaP50 => OnP50 - OffP50;
            public double DeltaP95 => OnP95 - OffP95;
            public override string ToString() => $"关 p50 {OffP50:F3} / p95 {OffP95:F3} ms；开 p50 {OnP50:F3} / p95 {OnP95:F3} ms；增量 p50 {DeltaP50:+0.000;-0.000} / p95 {DeltaP95:+0.000;-0.000} ms";
        }

        /// <summary>
        /// 功能 <paramref name="featureId"/> 开 / 关的 GPU 毫秒增量（只测一次）：同一场景各从头建、预热 30 帧、测 120 帧；每帧只计“渲染 1920×1080 + 读回 1 像素等 GPU”
        /// （FgCombatPerfProbe 的口径），模拟推进不计时。调用方用 <c>PerfGate.Expect(…, new[] { PerfGate.Le(pair.DeltaP95, 预算) }, …)</c> 记账，超线不到 2 倍记警告。
        /// </summary>
        public static GpuPair MeasureGpuPair(string sceneName, string featureId, int warmup = 30, int frames = 120)
        {
            if (!Scenes.TryGetValue(sceneName, out MethodInfo method))
            {
                throw new ArgumentException("没有登记采集场景 " + sceneName);
            }
            (double p50, double p95) Measure(bool on)
            {
                var c = new PresentationCaptureContext { FeatureOn = on };
                RenderTexture rt = null;
                Texture2D sync = null;
                try
                {
                    method.Invoke(null, new object[] { c });
                    rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
                    sync = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    Camera cam = c.Camera;
                    cam.aspect = (float)Width / Height;
                    PresentationFeatureRegistry.ResetToDefaults();
                    PresentationFeatureRegistry.Set(PresentationFeatureRegistry.HostId, true);
                    PresentationFeatureRegistry.Set(featureId, on);
                    foreach (IPresentationModule m in c.Modules)
                    {
                        PresentationHost.Register(m);
                    }
                    var ms = new List<double>(frames);
                    var sw = new Stopwatch();
                    double gs = 0;
                    for (int i = 0; i < warmup + frames; i++)
                    {
                        Step(c, CaptureInject.None, ref gs);
                        sw.Restart();
                        cam.targetTexture = rt;
                        cam.Render();
                        RenderTexture prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        sync.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                        sync.Apply();
                        RenderTexture.active = prev;
                        cam.targetTexture = null;
                        sw.Stop();
                        if (i >= warmup)
                        {
                            ms.Add(sw.Elapsed.TotalMilliseconds);
                        }
                    }
                    ms.Sort();
                    return (ms[ms.Count / 2], ms[(int)(ms.Count * 0.95)]);
                }
                finally
                {
                    foreach (IPresentationModule m in c.Modules)
                    {
                        PresentationHost.Unregister(m);
                    }
                    PresentationFeatureRegistry.ResetToDefaults();
                    PresentationHost.Reconcile();
                    if (c.Camera != null)
                    {
                        c.Camera.targetTexture = null;
                        c.Camera.ResetAspect();
                    }
                    if (c.Evidence)
                    {
                        FgMachineMorphSelfCheck.EvidenceEnd();
                    }
                    if (PresentationHost.ModuleCount == 0)
                    {
                        PresentationHost.DestroyRoot();
                    }
                    if (rt != null)
                    {
                        rt.Release();
                        Object.DestroyImmediate(rt);
                    }
                    if (sync != null)
                    {
                        Object.DestroyImmediate(sync);
                    }
                }
            }
            (double offP50, double offP95) = Measure(false);
            (double onP50, double onP95) = Measure(true);
            return new GpuPair { OffP50 = offP50, OffP95 = offP95, OnP50 = onP50, OnP95 = onP95 };
        }

        // ── 路径与外部工具 ──

        /// <summary>仓库根：从 Application.dataPath 向上找含 tools/pl 的目录（主工程与 PL 影子工程都对）。</summary>
        public static string RepoRoot()
        {
            for (var d = new DirectoryInfo(Application.dataPath); d != null; d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, "tools", "pl")))
                {
                    return d.FullName;
                }
            }
            throw new DirectoryNotFoundException("从 " + Application.dataPath + " 向上找不到含 tools/pl 的仓库根");
        }

        /// <summary>评审输出根：环境变量 PL_REVIEW_ROOT，缺省仓库根旁边的 BinGames-PL-review（仓外）。</summary>
        public static string ReviewRoot()
        {
            string env = Environment.GetEnvironmentVariable("PL_REVIEW_ROOT");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            {
                return env;
            }
            return Path.Combine(Path.GetDirectoryName(RepoRoot()) ?? "", "BinGames-PL-review");
        }

        /// <summary>跑 tools/pl 下的 Python 脚本（指标与 SSIM 只在 Python 里算一份）；返回退出码，标准输出 + 错误输出进 <paramref name="output"/>。</summary>
        public static int RunPython(string script, string args, out string output)
        {
            var psi = new ProcessStartInfo("python", $"\"{Path.Combine(RepoRoot(), "tools", "pl", script)}\" {args}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using (Process p = Process.Start(psi))
                {
                    string err = null;
                    p.ErrorDataReceived += (_, e) => err += e.Data != null ? e.Data + "\n" : "";
                    p.BeginErrorReadLine();
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(300000))
                    {
                        p.Kill();
                        output = o + "\n（超时 300 秒被杀）";
                        return -1;
                    }
                    p.WaitForExit();
                    output = o + err;
                    return p.ExitCode;
                }
            }
            catch (Exception e)
            {
                output = "启动 python 失败：" + e.Message;
                return -1;
            }
        }
    }
}
