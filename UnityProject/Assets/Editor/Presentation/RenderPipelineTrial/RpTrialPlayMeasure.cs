// PL-RP-01 → production/design/presentation-track/handoff/PL-RP-01.md
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BinGames.Presentation;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.EditorTools.JourneyBots;
using GameLogic.View;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// PL-RP-01 修复轮：Play 模式按真实帧数 DrawCall / SetPass / Batches（编辑模式手动 Camera.Render 不跨帧边界，ProfilerRecorder 恒 0）。
    /// 一个进程只搭一个机位（<c>PL_RP_SCENE</c>，避开“同一进程第二次搭夜袭会退化”），主菜单 → 新游戏 → 家园后按机位摆场景，
    /// 游戏镜头指到 1080p RT（batchmode 没有 Game 视图），垫片直接驱动（不经 PresentationHost），每档预热 60 帧、采 120 帧取中位数，并存一张图。
    /// URP 下逐档 none / bloom / ssao；RpHome 进程另做 800 方块的 GPU Resident Drawer 关 / 开（开的一侧反射读 GPUResidentDrawer.s_Instance 确认真的接管）。
    /// 跑法：<c>PL_RP_SCENE=RpHome PL_CAPTURE_OUT=… bash tools/pl/pl-unity.sh gpuplay GameLogic.EditorTools.RpTrialPlayMeasure.FromCommandLine rp-play-…</c>
    /// （URP 档经 pl-rp-shadow.sh run）。写 <c>rp-play-&lt;builtin|urp&gt;-&lt;机位&gt;.json</c> 与 <c>&lt;机位&gt;_&lt;档&gt;.png</c>；退出码 0 = 测完且场景合理、1 = 失败。
    /// </summary>
    [InitializeOnLoad]
    public static class RpTrialPlayMeasure
    {
        private const string Prefix = "BinGames.PLRPPlay.";
        private const int Warmup = 60, Frames = 120;
        private static JourneyRunner _runner;

        [Serializable]
        public sealed class Tier
        {
            public string post, shot;
            public int drawCalls, setPass, batches, drawCallsMax;
            public double cpuMs, gpuMs;
        }

        [Serializable]
        public sealed class Result
        {
            public string pipeline, colorSpace, device, scene, sanity;
            public List<Tier> tiers = new List<Tier>();
            public int cubes;
            public Tier grdOff, grdOn;
            public bool grdActive;
            public string grdNote;
        }

        static RpTrialPlayMeasure()
        {
            if (SessionState.GetString(Prefix + "active", "0") == "1")
            {
                _runner = new JourneyRunner(Build(SessionState.GetInt(Prefix + "seed", PresentationCapture.DefaultSeed)), new SessionJourneyStore(Prefix), new EditorJourneyClock());
                Hook();
            }
        }

        public static void FromCommandLine()
        {
            string scene = Environment.GetEnvironmentVariable("PL_RP_SCENE")?.Trim();
            string outDir = Environment.GetEnvironmentVariable("PL_CAPTURE_OUT");
            outDir = string.IsNullOrWhiteSpace(outDir) ? Path.Combine(PresentationCapture.ReviewRoot(), "PL-RP-01", "play") : outDir;
            Directory.CreateDirectory(outDir);
            if (Array.IndexOf(RpTrialCapture.SceneNames, scene) < 0)
            {
                Debug.LogError($"[PL-RP Play 测量] PL_RP_SCENE={scene} 不认识（只认 {string.Join(" / ", RpTrialCapture.SceneNames)}）；结论：失败");
                Exit(1);
                return;
            }
            int seed = scene == "RpFactory" ? FgPerfBaseline.Seed : PresentationCapture.DefaultSeed;
            SessionState.SetString(Prefix + "out", outDir);
            SessionState.SetString(Prefix + "scene", scene);
            SessionState.SetInt(Prefix + "seed", seed);
            _runner = new JourneyRunner(Build(seed), new SessionJourneyStore(Prefix), new EditorJourneyClock()) { ReportPath = Path.Combine(outDir, $"rp-play-{(PresentationMaterials.IsBuiltIn ? "builtin" : "urp")}-{scene}-journey.txt") };
            _runner.Start();
            Hook();
        }

        private static JourneyDef Build(int seed) => new JourneyDef
        {
            Id = "PL-RP-PLAY",
            Title = "PL-RP-01 Play 模式按真实帧数 DrawCall / SetPass",
            Seed = seed,
            TotalTimeoutSeconds = 1200,
            FailOnErrorLog = false,
            Steps =
            {
                JourneyCommon.S("enter", "开 main.unity 进 Play", 180, JourneyCommon.EnterPlay, c => JourneyCommon.TickPlay(c, seed)),
                JourneyCommon.S("menu", "主菜单点“新建”", 120, null, JourneyCommon.TickMenuNew, 2),
                JourneyCommon.S("newgame", "新游戏设置 → 进家园", 300, null, JourneyCommon.TickNewGame, 2),
                JourneyCommon.S("measure", "按机位摆场景、逐档按真实帧采计数", 600, null, TickMeasure),
            },
        };

        private static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (_runner == null)
            {
                EditorApplication.update -= Tick;
                return;
            }
            if (EditorApplication.isPlaying)
            {
                JourneyInput.KeepScripted();
                JourneyInput.WatchClickFrames();
            }
            if (_runner.Tick())
            {
                return;
            }
            bool pass = _runner.Result == JourneyRunner.ResultPass;
            EditorApplication.update -= Tick;
            Debug.Log($"[PL-RP Play 测量] {_runner.Result}：{(pass ? "完成" : _runner.FailStepId + " " + _runner.FailReason)}；结论：{(pass ? "测量完成" : "失败")}");
            _runner = null;
            Exit(pass ? 0 : 1);
        }

        private static void Exit(int code)
        {
            Cleanup();
            SessionState.SetString(Prefix + "active", "0");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }

        // ── 测量状态机（Play 里不再重载域，状态放静态字段） ──

        private static IEnumerator<string> _job;
        private static RpTrialShim _shim;
        private static RenderTexture _rt;
        private static ProfilerRecorder _draw, _setPass, _batches;

        private static StepOutcome TickMeasure(JourneyContext c)
        {
            try
            {
                _job ??= Run(SessionState.GetString(Prefix + "scene", "RpHome"), SessionState.GetString(Prefix + "out", "."));
                if (_job.MoveNext())
                {
                    return StepOutcome.Wait;
                }
                return StepOutcome.Done(_job.Current);
            }
            catch (Exception e)
            {
                return StepOutcome.Fail(e.Message + "\n" + e.StackTrace);
            }
        }

        /// <summary>每 yield 一次 = 等一帧。最后一个 Current 是结论文字。</summary>
        private static IEnumerator<string> Run(string scene, string outDir)
        {
            bool builtIn = PresentationMaterials.IsBuiltIn;
            var r = new Result
            {
                pipeline = builtIn ? "builtin" : GraphicsSettings.currentRenderPipeline.GetType().Name,
                colorSpace = QualitySettings.activeColorSpace.ToString(),
                device = SystemInfo.graphicsDeviceName,
                scene = scene,
            };
            Camera cam = Camera.main ?? throw new InvalidOperationException("进家园后没有 Camera.main");
            _rt = new RenderTexture(PresentationCapture.Width, PresentationCapture.Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            cam.targetTexture = _rt;
            _shim = new RpTrialShim();
            _shim.OnEnable();
            _draw = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");

            r.sanity = SetupScene(scene);
            for (int i = 0; i < 30; i++)
            {
                _shim.Tick(default);
                yield return null;
            }
            if (scene == "RpNightRaid")
            {
                RpTrialCapture.AssertNightRaid(_nightRaiders); // 推了 30 帧后时刻仍在夜里
                RpTrialCapture.SetCombatDrawHook(true); // 之后各档的计数与截图都含战斗绘制（根因见 SetCombatDrawHook）
                r.sanity += "；" + RpTrialCapture.AssertCombatVisible(cam);
            }

            string[] posts = builtIn ? new[] { "-" } : RpTrialPost.Variants;
            foreach (string post in posts)
            {
                GameObject vol = builtIn ? null : RpTrialPost.ApplyPost(cam, post);
                var tier = new Tier { post = post };
                IEnumerator<string> m = Sample(tier, cam, Path.Combine(outDir, $"{scene}_{(post == "-" ? "builtin" : post)}.png"));
                while (m.MoveNext())
                {
                    yield return null;
                }
                r.tiers.Add(tier);
                if (vol != null)
                {
                    Object.Destroy(vol.GetComponent<Volume>().sharedProfile);
                    Object.Destroy(vol);
                }
            }
            if (!builtIn)
            {
                RpTrialPost.ApplyPost(cam, "none");
            }

            if (scene == "RpHome")
            {
                IEnumerator<string> g = Cubes(r, cam);
                while (g.MoveNext())
                {
                    yield return null;
                }
            }

            string file = Path.Combine(outDir, $"rp-play-{(builtIn ? "builtin" : "urp")}-{scene}.json");
            File.WriteAllText(file, JsonUtility.ToJson(r, true));
            bool counted = r.tiers.All(t => t.drawCalls > 0 && t.setPass > 0);
            string summary = string.Join("；", r.tiers.Select(t => $"{t.post} DrawCall {t.drawCalls} / SetPass {t.setPass} / Batches {t.batches} / CPU {t.cpuMs:F2} ms / GPU {t.gpuMs:F2} ms"));
            Debug.Log($"[PL-RP Play 测量] {scene}（{r.pipeline}）：{summary}；场景 {r.sanity}；GRD {r.grdNote}；写 {file}");
            if (!counted)
            {
                throw new InvalidOperationException("Play 模式下计数器仍为 0：" + summary);
            }
            yield return $"{scene}：{summary}";
        }

        private static int _nightRaiders;

        private static string SetupScene(string scene)
        {
            CampaignState s = CampaignSession.Current ?? throw new InvalidOperationException("没有当前战役");
            switch (scene)
            {
                case "RpHomeWide":
                    WorldView.Director.SetStrategyView(WorldView.Director.StrategyFocus, 46f);
                    return "开局镜头，正交半高 46";
                case "RpNightRaid":
                {
                    long target = GameClock.TicksFor(GameClock.SecondsAt(1, 22.0));
                    if (target > GameClock.Ticks)
                    {
                        GameClock.SkipForTests(s, target - GameClock.Ticks);
                    }
                    _nightRaiders = RpTrialCapture.SpawnNightRaid();
                    RpTrialCapture.AssertNightRaid(_nightRaiders);
                    return $"第 1 日 {GameClock.HourOfDay(GameClock.GameSeconds):F1} 时，突袭者 {_nightRaiders}、炮塔 8";
                }
                case "RpFactory":
                {
                    GridCell core = HomeGridService.CorePivot(s);
                    int b = (int)RpTrialCapture.PerfPrivate("AddBuildings").Invoke(null, new object[] { s, core, FgPerfBaseline.BuildingTarget });
                    int m = (int)RpTrialCapture.PerfPrivate("SpawnMachines").Invoke(null, new object[] { s, core, FgPerfBaseline.MachineTarget });
                    // 修复轮：补上 FgPerfBaseline 同款的传送带（北面约 1.5 万格 + 物流节点）与管线（西面 30 个网络约 3000 格），走正式内核与实例化绘制
                    BeltKernel belts = BeltNetworkService.Kernel;
                    FgBeltNodeSelfCheck.BuildHugeWithNodes(belts, core.X - 160, core.Y + 48);
                    belts.EnsureTopology();
                    BeltNetworkService.ApplyGridLayer(s, HomeGridService.MapFor(s));
                    PipeKernel pipes = PipeNetworkService.Kernel;
                    FgPipeSelfCheck.BuildPerfNetworks(pipes, core.X - 700, core.Y + 48, 30);
                    PipeNetworkService.ApplyGridLayer(s, HomeGridService.MapFor(s));
                    WorldView.Director.SetStrategyView(new float2(core.X, core.Y - 70), 46f);
                    if (b < FgPerfBaseline.BuildingTarget / 2 || belts.CellCount < FgPerfBaseline.BeltCellTarget || pipes.CellCount < FgPerfBaseline.PipeCellTarget)
                    {
                        throw new InvalidOperationException($"RpFactory 铺量不足：建筑 +{b}、传送带 {belts.CellCount} 格、管线 {pipes.CellCount} 格");
                    }
                    return $"种子 {FgPerfBaseline.Seed}，建筑 +{b}、机器 {m}、传送带 {belts.CellCount} 格 / {belts.ItemCount} 件、管线 {pipes.CellCount} 格（传送带 / 管线在镜头北 / 西面画外，计入模拟与提交但多半被裁掉）";
                }
                default:
                    return "开局游戏镜头";
            }
        }

        /// <summary>预热 <see cref="Warmup"/> 帧、采 <see cref="Frames"/> 帧：ProfilerRecorder.LastValue 是上一帧的值（真实帧边界），CPU = unscaledDeltaTime，GPU = FrameTimingManager（读不到记 0）。</summary>
        private static IEnumerator<string> Sample(Tier t, Camera cam, string png)
        {
            var draw = new List<long>();
            var set = new List<long>();
            var bat = new List<long>();
            var cpu = new List<double>();
            var gpu = new List<double>();
            var timing = new FrameTiming[1];
            for (int i = 0; i < Warmup + Frames; i++)
            {
                _shim.Tick(default);
                FrameTimingManager.CaptureFrameTimings();
                yield return null;
                if (i < Warmup)
                {
                    continue;
                }
                draw.Add(_draw.LastValue);
                set.Add(_setPass.LastValue);
                bat.Add(_batches.LastValue);
                cpu.Add(Time.unscaledDeltaTime * 1000.0);
                if (FrameTimingManager.GetLatestTimings(1, timing) > 0)
                {
                    gpu.Add(timing[0].gpuFrameTime);
                }
            }
            t.drawCalls = (int)Median(draw);
            t.setPass = (int)Median(set);
            t.batches = (int)Median(bat);
            t.drawCallsMax = (int)draw.Max();
            t.cpuMs = Median(cpu);
            t.gpuMs = gpu.Count > 0 ? Median(gpu) : 0;
            if (png != null)
            {
                var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGBA32, false);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = cam.targetTexture;
                tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(png, tex.EncodeToPNG());
                Object.Destroy(tex);
                t.shot = Path.GetFileName(png);
            }
        }

        private static double Median(List<long> v) => v.Count == 0 ? 0 : v.OrderBy(x => x).ElementAt(v.Count / 2);

        private static double Median(List<double> v) => v.Count == 0 ? 0 : v.OrderBy(x => x).ElementAt(v.Count / 2);

        /// <summary>800 个 GameObject 方块（同一个工厂 Lit 材质）放在远处，游戏镜头停画、另一台镜头看方块：GRD 关 / 开各采一次。</summary>
        private static IEnumerator<string> Cubes(Result r, Camera gameCam)
        {
            var root = new GameObject("pl_rp_cubes");
            Material lit = PresentationMaterials.Get(PresentationMaterialUse.Lit, new Color(0.7f, 0.7f, 0.68f));
            Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var origin = new Vector3(20000f, 0f, 20000f);
            for (int i = 0; i < 800; i++)
            {
                var go = new GameObject("cube_" + i);
                go.transform.SetParent(root.transform, false);
                go.transform.position = origin + new Vector3((i % 40 - 20) * 2.5f, 0.5f, (i / 40 - 10) * 2.5f);
                go.AddComponent<MeshFilter>().sharedMesh = cube;
                go.AddComponent<MeshRenderer>().sharedMaterial = lit;
            }
            r.cubes = 800;
            var camGo = new GameObject("pl_rp_cube_cam");
            Camera cam = camGo.AddComponent<Camera>();
            cam.transform.SetPositionAndRotation(origin + new Vector3(0f, 60f, -40f), Quaternion.Euler(55f, 0f, 0f));
            cam.farClipPlane = 300f;
            cam.targetTexture = _rt;
            gameCam.enabled = false;
            try
            {
                r.grdOff = new Tier { post = "grd-off" };
                IEnumerator<string> a = Sample(r.grdOff, cam, null);
                while (a.MoveNext())
                {
                    yield return null;
                }
                if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
                {
                    GPUResidentDrawerMode before = urp.gpuResidentDrawerMode;
                    BatchRendererGroupStrippingMode strip = EditorGraphicsSettings.batchRendererGroupShaderStrippingMode;
                    // GRD 在编辑器里要求 BRG 变体“全部保留”（GPUResidentDrawer.Validator.cs:60），试验影子默认不是：临时改，采完还原
                    SetBrgStripping(BatchRendererGroupStrippingMode.KeepAll);
                    urp.gpuResidentDrawerMode = GPUResidentDrawerMode.InstancedDrawing;
                    try
                    {
                        for (int k = 0; k < 10; k++)
                        {
                            yield return null; // URP 每帧调 GPUResidentDrawer.ReinitializeIfNeeded
                        }
                        r.grdOn = new Tier { post = "grd-on" };
                        IEnumerator<string> b = Sample(r.grdOn, cam, null);
                        while (b.MoveNext())
                        {
                            yield return null;
                        }
                        r.grdActive = GrdInstance() != null;
                        r.grdNote = $"GRD 开：模式 {urp.gpuResidentDrawerMode}，GPUResidentDrawer 实例 {(r.grdActive ? "存在（已接管）" : "不存在（未确认生效：" + GrdWhyNot(urp) + "）")}；" +
                                    $"DrawCall 关 {r.grdOff.drawCalls} → 开 {r.grdOn.drawCalls}，Batches {r.grdOff.batches} → {r.grdOn.batches}";
                    }
                    finally
                    {
                        SetBrgStripping(strip);
                        urp.gpuResidentDrawerMode = before;
                        EditorUtility.ClearDirty(urp);
                    }
                }
                else
                {
                    r.grdNote = $"Built-in：没有 GPU Resident Drawer；800 方块 DrawCall {r.grdOff.drawCalls}、Batches {r.grdOff.batches}";
                }
            }
            finally
            {
                gameCam.enabled = true;
                Object.Destroy(camGo);
                Object.Destroy(root);
            }
        }

        /// <summary>EditorGraphicsSettings.batchRendererGroupShaderStrippingMode 只读：改图形设置资源的 m_BrgStripping（只在试验影子里跑，影子的 ProjectSettings 是独立副本）。</summary>
        private static void SetBrgStripping(BatchRendererGroupStrippingMode mode)
        {
            var so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            SerializedProperty p = so.FindProperty("m_BrgStripping") ?? throw new InvalidOperationException("图形设置里没有 m_BrgStripping");
            p.intValue = (int)mode;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>GRD 没起来时引擎自己给的原因（反射调 GPUResidentDrawer 的内部校验；读不到就说读不到）。</summary>
        private static string GrdWhyNot(UniversalRenderPipelineAsset urp)
        {
            try
            {
                Type t = GrdType();
                var why = new List<string>();
                foreach (MethodInfo m in t.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                             .Where(m => m.Name == "IsProjectSupported" || m.Name == "IsGPUResidentDrawerSupportedBySRP"))
                {
                    ParameterInfo[] ps = m.GetParameters();
                    object[] args = ps.Length == 2 ? new object[2] : ps.Length == 3 ? new object[] { ((IGPUResidentRenderPipeline)urp).gpuResidentDrawerSettings, null, null } : null;
                    if (args != null && !(bool)m.Invoke(null, args))
                    {
                        why.Add($"{m.Name}: {args[args.Length - 2]}");
                    }
                }
                return why.Count > 0 ? string.Join("；", why) : "两项校验都通过";
            }
            catch (Exception e)
            {
                return "读不到原因（" + e.GetType().Name + "）";
            }
        }

        // 按程序集名 Type.GetType 拿不到（11-20 第一次跑 NullReferenceException）：逐个程序集找
        private static Type GrdType() => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.Rendering.GPUResidentDrawer")).FirstOrDefault(t => t != null);

        private static object GrdInstance() =>
            GrdType()?.GetField("s_Instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

        private static void Cleanup()
        {
            _job = null;
            _shim?.OnDisable();
            _shim = null;
            foreach (ProfilerRecorder rec in new[] { _draw, _setPass, _batches })
            {
                if (rec.Valid)
                {
                    rec.Dispose();
                }
            }
            if (Camera.main != null && Camera.main.targetTexture == _rt)
            {
                Camera.main.targetTexture = null;
            }
            if (_rt != null)
            {
                _rt.Release();
                Object.Destroy(_rt);
                _rt = null;
            }
        }
    }
}
