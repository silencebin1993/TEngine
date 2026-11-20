// PL-RP-01 → production/design/presentation-track/handoff/PL-RP-01.md
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BinGames.Presentation;
using BinGames.PresentationLab;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.View;
using GameLogic.View.Presentation;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// 后期档（环境变量 <c>PL_RP_POST</c> = none / bloom / ssao，缺省 none）：bloom = ACES 色调映射 + Bloom；ssao = 再开 Renderer 上的 SSAO 特性。
    /// 只建 1 个 URP Asset + 1 个 Renderer，档位在场景里切（全局 Volume 是本场景临时对象，SSAO 特性 SetActive），Built-in 下什么都不做。
    /// </summary>
    public static class RpTrialPost
    {
        public static readonly string[] Variants = { "none", "bloom", "ssao" };

        /// <summary>非空时覆盖环境变量（测量方法逐档切用）。</summary>
        public static string Override;

        public static string Variant
        {
            get
            {
                string v = Override ?? Environment.GetEnvironmentVariable("PL_RP_POST");
                return string.IsNullOrWhiteSpace(v) ? "none" : v.Trim().ToLowerInvariant();
            }
        }

        public static ScriptableRendererFeature Ssao(UniversalRendererData d) =>
            d == null ? null : d.rendererFeatures.FirstOrDefault(f => f != null && f.GetType().Name == "ScreenSpaceAmbientOcclusion");

        /// <summary>每个 Rp 场景方法最后调：开 / 关切的是垫片（pl.rptrial），再按后期档摆相机与全局 Volume。</summary>
        public static void Prepare(PresentationCaptureContext c)
        {
            c.Feature = PresentationFeatureRegistry.RpTrialId;
            c.Modules.Add(new RpTrialShim());
            if (PresentationMaterials.IsBuiltIn || c.Camera == null)
            {
                return;
            }
            ApplyPost(c.Camera, Variant);
        }

        /// <summary>按后期档摆相机、SSAO 特性与全局 Volume（编辑模式采集与 Play 测量共用）；返回本档建的 Volume 物体（none 档为 null，换档时由调用方销毁）。</summary>
        public static GameObject ApplyPost(Camera camera, string v)
        {
            if (Array.IndexOf(Variants, v) < 0)
            {
                throw new ArgumentException($"PL_RP_POST={v} 不认识（只认 {string.Join(" / ", Variants)}）");
            }
            UniversalAdditionalCameraData cam = camera.GetUniversalAdditionalCameraData();
            cam.renderPostProcessing = v != "none";
            cam.antialiasing = AntialiasingMode.None;
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RpTrialSetup.InTrialShadow ? RpTrialSetup.RendererPath : UrpValidate.RendererPath); // PL-RP-02：正式工程用正式 Renderer
            ScriptableRendererFeature ssao = Ssao(data);
            if (v == "ssao" && ssao == null)
            {
                throw new InvalidOperationException("试验 Renderer 上没有 SSAO 特性（先跑 pl-rp-shadow.sh setup）");
            }
            ssao?.SetActive(v == "ssao");
            if (v == "ssao")
            {
                // 11-20 修复轮查清 SSAO 档“与 Bloom 档逐像素相同”的根因：SSAO.hlsl 里视深超过 Falloff（默认 100 米）的像素直接返回 AO = 0，
                // 而战略镜头离地约 146 米（CameraDirector 的镜头距离），全屏都被剔掉。Falloff 提到 1000 米；深度来源改 Depth（只要深度缓冲，
                // 不依赖各 shader 的 DepthNormals pass：地形替身与 Sim 实例化 shader 都没有）；半径按 RTS 尺度取 2 米；AfterOpaque 让自写 / 替身 shader 也吃到 AO。
                var so = new SerializedObject(ssao);
                foreach ((string path, float value) in new[] { ("m_Settings.Radius", 2f), ("m_Settings.Intensity", 2f), ("m_Settings.Falloff", 1000f) })
                {
                    SerializedProperty p = so.FindProperty(path) ?? throw new InvalidOperationException("SSAO 设置里没有 " + path);
                    p.floatValue = value;
                }
                (so.FindProperty("m_Settings.AfterOpaque") ?? throw new InvalidOperationException("SSAO 设置里没有 AfterOpaque")).boolValue = true;
                (so.FindProperty("m_Settings.Source") ?? throw new InvalidOperationException("SSAO 设置里没有 Source")).intValue = 0; // DepthSource.Depth
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.ClearDirty(ssao);
                // Setup 用代码加的特性没写 m_RendererFeatureMap（编辑器检视面板才会写）：补上本地 ID，再让 Renderer 重建一次（特性重新 Create）
                var dso = new SerializedObject(data);
                SerializedProperty map = dso.FindProperty("m_RendererFeatureMap");
                if (map != null && map.arraySize != data.rendererFeatures.Count && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ssao, out string _, out long localId))
                {
                    map.arraySize = 1;
                    map.GetArrayElementAtIndex(0).longValue = localId;
                    dso.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            if (data != null)
            {
                data.SetDirty();
                EditorUtility.ClearDirty(data);
            }
            if (v == "none")
            {
                return null;
            }
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.Add<Tonemapping>(true).mode.Override(TonemappingMode.ACES);
            Bloom bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(0.6f);
            bloom.threshold.Override(1f);
            var go = new GameObject("pl_rp_volume");
            Volume vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 100f;
            vol.sharedProfile = profile;
            return go;
        }
    }

    /// <summary>PL-RP-01 对比机位（4 个）与一次性测量（帧耗、DrawCall / SetPass、品红来源、shader 兼容、GPU Resident Drawer、OnRenderObject）。</summary>
    public static class RpTrialCapture
    {
        public static readonly string[] SceneNames = { "RpHome", "RpHomeWide", "RpNightRaid", "RpFactory" };

        [PresentationCaptureScene("RpHome")]
        public static void RpHome(PresentationCaptureContext c)
        {
            SetCombatDrawHook(false);
            c.OpenHome();
            RpTrialPost.Prepare(c);
        }

        [PresentationCaptureScene("RpHomeWide")]
        public static void RpHomeWide(PresentationCaptureContext c)
        {
            SetCombatDrawHook(false);
            c.OpenHome();
            WorldView.Director.SetStrategyView(WorldView.Director.StrategyFocus, 46f);
            RpTrialPost.Prepare(c);
        }

        /// <summary>夜间来袭：开局家园跳到第 1 日 22 时（夜里），焦点旁按战斗性能场景的口径摆 40 个突袭者对 8 座炮塔，再推 2 秒让火力打起来。</summary>
        [PresentationCaptureScene("RpNightRaid")]
        public static void RpNightRaid(PresentationCaptureContext c)
        {
            c.OpenHome();
            CampaignState s = CampaignSession.Current;
            long target = GameClock.TicksFor(GameClock.SecondsAt(1, 22.0));
            if (target > GameClock.Ticks)
            {
                GameClock.SkipForTests(s, target - GameClock.Ticks);
            }
            int raiders = SpawnNightRaid();
            FgMachineMorphSelfCheck.EvidenceFrames(40);
            DayNightLighting.Tick(true);
            AssertNightRaid(raiders);
            SetCombatDrawHook(true);
            AssertCombatVisible(c.Camera);
            RpTrialPost.Prepare(c);
        }

        /// <summary>同 CombatBench.SpawnPerfScenario（焦点旁 8 座炮塔一圈、40 个突袭者分四路），但数回实际生成的突袭者（场景合理性断言用）。</summary>
        public static int SpawnNightRaid()
        {
            float2 f = WorldView.Director.StrategyFocus;
            var center = new Vector2(f.x + 6f, f.y - 2f);
            _raidCenter = center;
            CombatSite site = WorldSimulation.Home.Combat;
            CombatBench.Spec spec = CombatBench.PerfSpec();
            CombatBench.SpawnTurretRing(site, center, 22f, 8, spec);
            int spawned = 0;
            for (int g = 0; g < 4; g++)
            {
                float ang = g * Mathf.PI * 0.5f + 0.4f;
                spawned += CombatBench.SpawnRaidGroup(site, center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 45f, center, g == 3 ? 40 - spawned : 10, spec).Count;
            }
            return spawned;
        }

        private static Vector2 _raidCenter;

        /// <summary>夜袭 8 座炮塔的世界坐标（与 <see cref="SpawnNightRaid"/> 里的 SpawnTurretRing 同一公式）。</summary>
        public static Vector2[] NightRaidTurrets() =>
            Enumerable.Range(0, 8).Select(i => _raidCenter + new Vector2(Mathf.Cos(i / 8f * Mathf.PI * 2f), Mathf.Sin(i / 8f * Mathf.PI * 2f)) * 22f).ToArray();

        public static bool CombatHookOn { get; private set; }

        /// <summary>
        /// 第 2 轮审查根因（11-20 实测）：batchmode 编辑器里，游戏在 Update 阶段排进的 <c>Graphics.RenderMesh*</c>（CombatRenderer 的 RenderMeshPrimitives，连一个 Unlit 方块也一样）
        /// 到不了镜头——DrawCall 0、画面里没有；在镜头渲染前回调里提交、或提交后紧接手动 <c>Camera.Render</c> 则能画。所以试验采集在渲染前回调里替 CombatRenderer 再提交一次（Draw 只在内核版本变了才重填缓冲，重复调用无副作用）。
        /// shortcut: 只给 RpNightRaid 开（其余 3 个机位没重拍，画面里同样缺那十几个战斗单位）；真游戏（有 Game 视图 / Player）里 Update 提交是否正常没验证，切 B 的 PL-RP-02 要在真画面里看。
        /// </summary>
        public static void SetCombatDrawHook(bool on)
        {
            if (on == CombatHookOn)
            {
                return;
            }
            CombatHookOn = on;
            // Play：镜头渲染前回调里提交（实测有效）；编辑模式：采集管线每次手动 Render 前再调一次（回调路径在编辑模式没单独验证，重复提交只是同一批实例画两遍）
            PresentationCapture.BeforeRender = on ? DrawCombatFor : null;
            if (on)
            {
                Camera.onPreCull += DrawCombatFor;
                RenderPipelineManager.beginCameraRendering += DrawCombatSrp;
            }
            else
            {
                Camera.onPreCull -= DrawCombatFor;
                RenderPipelineManager.beginCameraRendering -= DrawCombatSrp;
            }
        }

        private static void DrawCombatSrp(ScriptableRenderContext ctx, Camera cam) => DrawCombatFor(cam);

        private static void DrawCombatFor(Camera cam)
        {
            CombatSite site = WorldSimulation.Home?.Combat;
            if (cam.cameraType != CameraType.Game || site == null || site.IsDisposed || site.Renderer == null)
            {
                return;
            }
            site.Renderer.Draw(site.Kernel, cam, GameClock.StepAlpha, double2.zero, CombatSite.Tuning("combat.render.unit_height", 0.6f));
        }

        /// <summary>手动渲染一帧，数 8 座炮塔投到画面上的中心像素有几个是炮塔的青色（占位外观 ≈ RGB 64/217/242，不受光照）。</summary>
        public static int VisibleTurrets(Camera cam)
        {
            RenderTexture prevTarget = cam.targetTexture;
            RenderTexture rt = prevTarget != null ? prevTarget : RenderTexture.GetTemporary(PresentationCapture.Width, PresentationCapture.Height, 24);
            cam.targetTexture = rt;
            if (!Application.isPlaying)
            {
                DrawCombatFor(cam); // 编辑模式要紧挨着 Render 提交（见 SetCombatDrawHook）
            }
            cam.Render();
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;
            int seen = 0;
            foreach (Vector2 p in NightRaidTurrets())
            {
                // 投影要在还原 targetTexture 之前做：还原成 null 后宽高比退回屏幕（batchmode 下不是 16:9），点会投偏（第 2 轮实测踩过）
                Vector3 vp = cam.WorldToViewportPoint(new Vector3(p.x, 0.6f, p.y));
                if (vp.z <= 0f || vp.x < 0f || vp.x >= 1f || vp.y < 0f || vp.y >= 1f)
                {
                    continue;
                }
                Color px = tex.GetPixel((int)(vp.x * rt.width), (int)(vp.y * rt.height));
                seen += px.b > 0.5f && px.g > 0.45f && px.r < 0.6f * px.g ? 1 : 0;
            }
            cam.targetTexture = prevTarget;
            if (prevTarget == null)
            {
                RenderTexture.ReleaseTemporary(rt);
            }
            Object.DestroyImmediate(tex);
            return seen;
        }

        /// <summary>夜袭可见性断言（第 2 轮审查：只数生成个数拦不住“画面里没有”）：本帧提交的单位实例 ≥ 48，且 8 座炮塔至少 3 座在画面上是炮塔色（11-20 实测两个管线都是 4/8：环上另 4 座被地形岩块 / 建筑挡住或压边；什么都没画时是 0）。不满足就抛。</summary>
        public static string AssertCombatVisible(Camera cam)
        {
            int seen = VisibleTurrets(cam);
            string state = CombatDrawState(cam, out int units, out _);
            if (units < 48 || seen < 3)
            {
                throw new InvalidOperationException($"RpNightRaid 战斗单位没上屏：炮塔可见 {seen}/8；{state}");
            }
            return $"炮塔可见 {seen}/8；{state}";
        }

        /// <summary>战斗实例化绘制的现状（第 2 轮审查：夜袭只断言了逻辑层生成数，画面里从未出现）：Renderer 是否建了、本帧提交的单位实例与绘制次数、首个单位在镜头里的视口坐标。</summary>
        public static string CombatDrawState(Camera cam, out int units, out int draws)
        {
            CombatSite site = WorldSimulation.Home?.Combat;
            BinGames.Sim.Combat.CombatRenderer r = site?.Renderer;
            units = r?.LastUnitInstances ?? -1;
            draws = r?.LastDrawCalls ?? -1;
            string first = "";
            if (r != null && units > 0 && cam != null)
            {
                int inView = 0;
                for (int i = 0; i < units; i++)
                {
                    float4 u = r.UnitInstances[i].A;
                    Vector3 v = cam.WorldToViewportPoint(new Vector3(u.x, 0.6f, u.y));
                    inView += v.z > 0f && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f ? 1 : 0;
                }
                float4 a = r.UnitInstances[units - 1].A;
                Vector3 vp = cam.WorldToViewportPoint(new Vector3(a.x, 0.6f, a.y));
                float2 f = WorldView.Director.StrategyFocus;
                Vector3 fv = cam.WorldToViewportPoint(new Vector3(f.x, 0f, f.y));
                first = $"，镜头内单位 {inView}，末个单位 ({a.x:F1}, {a.y:F1}) 视口 ({vp.x:F2}, {vp.y:F2}, 深 {vp.z:F1})，焦点 ({f.x:F1}, {f.y:F1}) 视口 ({fv.x:F2}, {fv.y:F2})";
            }
            return $"Renderer {(r == null ? "未建" : "已建")}，GPU {(r == null ? "-" : r.GpuAvailable ? "可用" : "不可用：" + r.GpuUnavailableReason)}，单位实例 {units}、弹体 {r?.LastProjectileInstances}、本帧提交 {draws} 次，"
                + $"敌存活 {site?.Kernel.CountAlive(BinGames.Sim.Combat.CombatFaction.Hostile)}{first}；镜头 {(cam == null ? "无" : $"{cam.name} 位置 {cam.transform.position} 正交 {cam.orthographic} 半高 {cam.orthographicSize:F1}")}";
        }

        /// <summary>夜袭机位的场景合理性（11-20 审查：同一进程第二次搭这个场景会退化成开局画面，此前只能靠人眼发现）：时刻在 22 点～次日 5 点、突袭者 = 40。不满足就抛（该组记 error）。</summary>
        public static void AssertNightRaid(int raiders)
        {
            double h = GameClock.HourOfDay(GameClock.GameSeconds);
            if (!(h >= 22.0 || h < 5.0) || raiders != 40)
            {
                throw new InvalidOperationException($"RpNightRaid 场景退化：时刻 {h:F1} 时（应 ≥ 22）、突袭者 {raiders}（应 40）");
            }
        }

        /// <summary>
        /// 后期大基地（简化版 FG-PERF-LATE）：种子 42 的开局家园上用 FgPerfBaseline 自己的 AddBuildings（800 座）与 SpawnMachines（120 台）铺量（反射调用，不改 FG 文件），
        /// 正交半高 46 看建筑区中部。与 FgPerfBaseline 的差别：开局走正式新开局（不是它的 CreateNew("fgperf-42")），没有传送带 / 管线 / 战斗 / 行进突袭。
        /// </summary>
        [PresentationCaptureScene("RpFactory")]
        public static void RpFactory(PresentationCaptureContext c)
        {
            SetCombatDrawHook(false);
            c.OpenHome(FgPerfBaseline.Seed);
            CampaignState s = CampaignSession.Current;
            GridCell core = HomeGridService.CorePivot(s);
            int buildings = (int)PerfPrivate("AddBuildings").Invoke(null, new object[] { s, core, FgPerfBaseline.BuildingTarget });
            int machines = (int)PerfPrivate("SpawnMachines").Invoke(null, new object[] { s, core, FgPerfBaseline.MachineTarget });
            FgMachineMorphSelfCheck.EvidenceFrames(4);
            WorldView.Director.SetStrategyView(new float2(core.X, core.Y - 70), 46f);
            Debug.Log($"[PL-RP] RpFactory 铺量：建筑 +{buildings}、机器 {machines}");
            RpTrialPost.Prepare(c);
        }

        public static MethodInfo PerfPrivate(string name) =>
            typeof(FgPerfBaseline).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException("FgPerfBaseline." + name + " 不见了（FG 改了性能场景）");

        // ── 测量（gpu 入口，只测一次） ──

        [Serializable]
        public sealed class SceneMeasure
        {
            public string scene, post, error;
            public double p50, p95;
            public int drawCalls, setPass, batches, statDrawCalls, statSetPass, remapped;
            public List<string> magentaBefore = new List<string>();
            public List<string> magentaAfter = new List<string>();
        }

        [Serializable]
        public sealed class ShaderInfo
        {
            public string name, path, urp;
            public int srpBatcher = -99; // 0 = 兼容；-99 = 未测（读不到）
        }

        [Serializable]
        public sealed class Result
        {
            public string pipeline, colorSpace, device;
            public List<SceneMeasure> scenes = new List<SceneMeasure>();
            public List<ShaderInfo> shaders = new List<ShaderInfo>();
            public int cubes, grdOffDrawCalls, grdOnDrawCalls, grdOffStatDraw, grdOnStatDraw;
            public double grdOffMs, grdOnMs;
            public string grdNote;
            public int onRenderObjectCalls;
            public bool onRenderObjectDrawn;
        }

        /// <summary>
        /// <c>PL_CAPTURE_OUT=<目录> bash tools/pl/pl-unity.sh gpu GameLogic.EditorTools.RpTrialCapture.MeasureFromCommandLine rp-measure</c>（URP 试验影子里经 pl-rp-shadow.sh run）。
        /// 写 <c>rp-measure-&lt;builtin|urp&gt;.json</c>；Built-in 下每机位测 1 档，URP 下 none / bloom / ssao 三档。
        /// </summary>
        public static void MeasureFromCommandLine()
        {
            int code = 0;
            try
            {
                string outDir = Environment.GetEnvironmentVariable("PL_CAPTURE_OUT");
                outDir = string.IsNullOrWhiteSpace(outDir) ? Path.Combine(PresentationCapture.ReviewRoot(), "PL-RP-01", "measure") : outDir;
                Directory.CreateDirectory(outDir);
                Result r = Measure();
                string file = Path.Combine(outDir, $"rp-measure-{(PresentationMaterials.IsBuiltIn ? "builtin" : "urp")}.json");
                File.WriteAllText(file, JsonUtility.ToJson(r, true));
                int errors = r.scenes.Count(x => x.error != null);
                Debug.Log($"[PL-RP 测量] 写 {file}：{r.scenes.Count} 组（出错 {errors}）、shader {r.shaders.Count} 个、GRD {r.grdOffDrawCalls}/{r.grdOnDrawCalls}、OnRenderObject 调用 {r.onRenderObjectCalls} 画出 {r.onRenderObjectDrawn}；结论：{(errors == 0 ? "测量完成" : "有出错组")}");
                code = errors == 0 ? 0 : 1;
            }
            catch (Exception e)
            {
                Debug.LogError("[PL-RP 测量] 抛异常：" + e);
                code = 1;
            }
            finally
            {
                RpTrialPost.Override = null;
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }

        public static Result Measure()
        {
            var r = new Result
            {
                pipeline = PresentationMaterials.IsBuiltIn ? "builtin" : GraphicsSettings.currentRenderPipeline.GetType().Name,
                colorSpace = QualitySettings.activeColorSpace.ToString(),
                device = SystemInfo.graphicsDeviceName,
            };
            string[] posts = PresentationMaterials.IsBuiltIn ? new[] { "-" } : RpTrialPost.Variants;
            foreach (string scene in SceneNames)
            {
                foreach (string post in posts)
                {
                    RpTrialPost.Override = post == "-" ? null : post;
                    r.scenes.Add(MeasureScene(scene, post));
                }
            }
            RpTrialPost.Override = null;
            r.shaders = ShaderAudit();
            CubeExperiments(r);
            return r;
        }

        public static SceneMeasure MeasureScene(string scene, string post, int warmup = 30, int frames = 120) // PL-RP-02 UrpPerf 复用
        {
            var m = new SceneMeasure { scene = scene, post = post };
            var c = new PresentationCaptureContext { FeatureOn = true };
            RenderTexture rt = null;
            Texture2D sync = null;
            try
            {
                PresentationCapture.Scenes[scene].Invoke(null, new object[] { c });
                m.magentaBefore = MagentaSources();
                rt = new RenderTexture(PresentationCapture.Width, PresentationCapture.Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
                sync = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                Camera cam = c.Camera;
                cam.aspect = (float)PresentationCapture.Width / PresentationCapture.Height;
                PresentationFeatureRegistry.ResetToDefaults();
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.HostId, true);
                PresentationFeatureRegistry.Set(c.Feature, true);
                foreach (IPresentationModule mod in c.Modules)
                {
                    PresentationHost.Register(mod);
                }
                using var draw = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
                using var setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
                using var batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
                var ms = new List<double>(frames);
                var sw = new Stopwatch();
                for (int i = 0; i < warmup + frames; i++)
                {
                    FgMachineMorphSelfCheck.EvidenceFrames(1);
                    PresentationHostBridge.TickManual(PresentationCapture.Dt, GameClock.FrameScaledDt);
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
                m.p50 = ms[ms.Count / 2];
                m.p95 = ms[(int)(ms.Count * 0.95)];
                m.drawCalls = (int)draw.LastValue;
                m.setPass = (int)setPass.LastValue;
                m.batches = (int)batches.LastValue;
                m.statDrawCalls = UnityStats.drawCalls;
                m.statSetPass = UnityStats.setPassCalls;
                m.remapped = c.Modules.OfType<RpTrialShim>().Sum(x => x.Remapped);
                m.magentaAfter = MagentaSources();
            }
            catch (Exception e)
            {
                m.error = e.ToString();
                Debug.LogError($"[PL-RP 测量] {scene}/{post} 出错：{e}");
            }
            finally
            {
                foreach (IPresentationModule mod in c.Modules)
                {
                    PresentationHost.Unregister(mod);
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
            return m;
        }

        /// <summary>已载入材质里当前管线画不了的（URP 下画成品红）：按 shader 汇总“shader 名 × 材质数（例：材质名）”。Built-in 下为空。</summary>
        public static List<string> MagentaSources() =>
            Resources.FindObjectsOfTypeAll<Material>()
                .Where(x => x != null && RpTrialShim.UrpVerdict(x.shader) == "error")
                .GroupBy(x => x.shader != null ? x.shader.name : "(无 shader)")
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} × {g.Count()}（例：{g.First().name}{(EditorUtility.IsPersistent(g.First()) ? "，" + AssetDatabase.GetAssetPath(g.First()) : "，运行时建")}）")
                .ToList();

        /// <summary>自写 shader（GameScripts / GameRes 特效 / 试验替身）：URP 判定 + SRP Batcher 兼容码（反射读编辑器内部 ShaderUtil，读不到记 -99 = 未测）。</summary>
        public static List<ShaderInfo> ShaderAudit()
        {
            MethodInfo srp = typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityCode", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            var dirs = new[] { "Assets/GameScripts", "Assets/GameRes/Raw/Effects", RpTrialSetup.Folder }.Where(AssetDatabase.IsValidFolder).ToArray();
            var list = new List<ShaderInfo>();
            foreach (string guid in AssetDatabase.FindAssets("t:Shader", dirs))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var s = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (s == null)
                {
                    continue;
                }
                var info = new ShaderInfo { name = s.name, path = path, urp = RpTrialShim.UrpVerdict(s) };
                try
                {
                    if (srp != null && srp.GetParameters().Length == 2)
                    {
                        info.srpBatcher = Convert.ToInt32(srp.Invoke(null, new object[] { s, ActiveSubShader(s) }));
                    }
                }
                catch (Exception)
                {
                    info.srpBatcher = -99;
                }
                list.Add(info);
            }
            return list;
        }

        [Serializable]
        public sealed class RenderCheck
        {
            public string name, path, verdict, how, error;
            public float magenta, drawn;
        }

        [Serializable]
        public sealed class RenderChecks
        {
            public string pipeline;
            public bool controlOk;
            public List<RenderCheck> shaders = new List<RenderCheck>();
        }

        /// <summary>
        /// 11-20 修复轮：自写 shader 的“URP 能画”此前只按 LightMode 标签静态判定。这里逐个实渲染：空场景里一台镜头对着一块 4 米方片，
        /// 用该 shader 的新材质画 1 个实例（支持实例化的走 <c>Graphics.RenderMeshInstanced</c>，即 FG 单位 / 传送带 / 管线的画法，否则 <c>RenderMesh</c>），
        /// 读回数品红像素比与“画出了东西”的像素比（背景纯黑）。不喂实例数据缓冲，所以“没画出东西”只说明缺数据、不算画不了。
        /// <c>PL_CAPTURE_OUT=<目录> bash tools/pl/pl-unity.sh gpu GameLogic.EditorTools.RpTrialCapture.ShaderRenderFromCommandLine rp-shader-render</c> → <c>rp-shader-render-&lt;builtin|urp&gt;.json</c>。
        /// </summary>
        public static void ShaderRenderFromCommandLine()
        {
            int code = 0;
            try
            {
                string outDir = Environment.GetEnvironmentVariable("PL_CAPTURE_OUT");
                outDir = string.IsNullOrWhiteSpace(outDir) ? Path.Combine(PresentationCapture.ReviewRoot(), "PL-RP-01", "measure") : outDir;
                Directory.CreateDirectory(outDir);
                var r = new RenderChecks { pipeline = PresentationMaterials.IsBuiltIn ? "builtin" : "urp" };
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var camGo = new GameObject("pl_rp_cam");
                Camera cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.transform.SetPositionAndRotation(new Vector3(0f, 0f, -5f), Quaternion.identity);
                var light = new GameObject("pl_rp_light").AddComponent<Light>();
                light.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(30f, 20f, 0f);
                Mesh quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
                var rt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
                var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                int slot = 0;
                foreach (ShaderInfo info in ShaderAudit())
                {
                    int layer = 8 + slot++ % 24; // 排队的绘制在本帧内一直有效：每个 shader 用自己的层、镜头只看这一层，前面的方片不串进来
                    cam.cullingMask = 1 << layer;
                    var rc = new RenderCheck { name = info.name, path = info.path, verdict = info.urp };
                    var mat = new Material(AssetDatabase.LoadAssetAtPath<Shader>(info.path)) { hideFlags = HideFlags.DontSave };
                    try
                    {
                        Matrix4x4 m = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * 4f); // 内置 Quad 正面朝 -Z（朝镜头）；第一次转了 180° 被背面剔除
                        mat.enableInstancing = true;
                        var rp = new RenderParams(mat) { camera = cam, layer = layer, worldBounds = new Bounds(Vector3.zero, Vector3.one * 10f) };
                        try
                        {
                            Graphics.RenderMeshInstanced(rp, quad, 0, new[] { m });
                            rc.how = "RenderMeshInstanced";
                        }
                        catch (Exception)
                        {
                            Graphics.RenderMesh(rp, quad, 0, m);
                            rc.how = "RenderMesh";
                        }
                        cam.targetTexture = rt;
                        cam.Render();
                        RenderTexture prev = RenderTexture.active;
                        RenderTexture.active = rt;
                        tex.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                        tex.Apply();
                        RenderTexture.active = prev;
                        cam.targetTexture = null;
                        Color32[] px = tex.GetPixels32();
                        rc.magenta = px.Count(c => c.r > 200 && c.g < 60 && c.b > 200) / (float)px.Length;
                        rc.drawn = px.Count(c => c.r > 3 || c.g > 3 || c.b > 3) / (float)px.Length;
                    }
                    catch (Exception e)
                    {
                        rc.error = e.Message;
                    }
                    finally
                    {
                        Object.DestroyImmediate(mat);
                    }
                    r.shaders.Add(rc);
                }
                Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
                // 对照：已知 URP 画不了的原地形 surface shader 必须测出品红，否则这套实渲染分不出“能画 / 品红”，结果不可用
                RenderCheck control = r.shaders.FirstOrDefault(x => x.name == "BinGames/Terrain/ContinuousGround");
                r.controlOk = PresentationMaterials.IsBuiltIn || (control != null && control.magenta > 0.05f);
                string file = Path.Combine(outDir, $"rp-shader-render-{r.pipeline}.json");
                File.WriteAllText(file, JsonUtility.ToJson(r, true));
                Debug.Log($"[PL-RP 实渲染] 写 {file}：" + string.Join("；", r.shaders.Select(x => $"{x.name} {x.how} 品红 {x.magenta:P1} 画出 {x.drawn:P1}{(x.error != null ? " 出错 " + x.error : "")}")) + $"；对照（原地形 shader 应品红）{(r.controlOk ? "✓" : "✗ 分不出，结果不可用")}；结论：完成");
            }
            catch (Exception e)
            {
                Debug.LogError("[PL-RP 实渲染] 抛异常：" + e);
                code = 1;
            }
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }

        private static int ActiveSubShader(Shader s)
        {
            MethodInfo m = typeof(ShaderUtil).GetMethod("GetShaderActiveSubshaderIndex", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            return m != null ? Convert.ToInt32(m.Invoke(null, new object[] { s })) : 0;
        }

        /// <summary>空场景 800 个 Lit 方块：DrawCall（URP 下 GPU Resident Drawer 关 / 开）+ OnRenderObject GL 叠层探针。</summary>
        private static void CubeExperiments(Result r)
        {
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Camera cam = Camera.main;
            cam.transform.SetPositionAndRotation(new Vector3(0f, 60f, -40f), Quaternion.Euler(55f, 0f, 0f));
            Material lit = PresentationMaterials.Get(PresentationMaterialUse.Lit, new Color(0.7f, 0.7f, 0.68f));
            Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            for (int i = 0; i < 800; i++)
            {
                var go = new GameObject("cube_" + i);
                go.transform.position = new Vector3((i % 40 - 20) * 2.5f, 0.5f, (i / 40 - 10) * 2.5f);
                go.AddComponent<MeshFilter>().sharedMesh = cube;
                go.AddComponent<MeshRenderer>().sharedMaterial = lit;
            }
            r.cubes = 800;
            var rt = new RenderTexture(PresentationCapture.Width, PresentationCapture.Height, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var px1 = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            try
            {
                // DrawCall 计数器在编辑模式手动渲染里读不到（11-20 实测 ProfilerRecorder / UnityStats 恒 0）：另记渲染 + 读回 1 像素的毫秒（30 帧中位数）作代理
                (int, int, double) Draw()
                {
                    using var draw = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
                    var ms = new List<double>();
                    var sw = new Stopwatch();
                    for (int k = 0; k < 40; k++)
                    {
                        sw.Restart();
                        cam.targetTexture = rt;
                        cam.Render();
                        RenderTexture prev0 = RenderTexture.active;
                        RenderTexture.active = rt;
                        px1.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                        px1.Apply();
                        RenderTexture.active = prev0;
                        cam.targetTexture = null;
                        sw.Stop();
                        if (k >= 10)
                        {
                            ms.Add(sw.Elapsed.TotalMilliseconds);
                        }
                    }
                    ms.Sort();
                    return ((int)draw.LastValue, UnityStats.drawCalls, ms[ms.Count / 2]);
                }
                (r.grdOffDrawCalls, r.grdOffStatDraw, r.grdOffMs) = Draw();
                if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
                {
                    urp.gpuResidentDrawerMode = GPUResidentDrawerMode.InstancedDrawing;
                    (r.grdOnDrawCalls, r.grdOnStatDraw, r.grdOnMs) = Draw();
                    r.grdNote = $"GRD 开后 {urp.gpuResidentDrawerMode}；Forward+ = {(AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RpTrialSetup.RendererPath)?.renderingMode == RenderingMode.ForwardPlus)}";
                    urp.gpuResidentDrawerMode = GPUResidentDrawerMode.Disabled;
                }
                else
                {
                    r.grdNote = "Built-in：没有 GPU Resident Drawer";
                }

                var probeGo = new GameObject("pl_rp_onrenderobject");
                probeGo.AddComponent<RpOnRenderObjectProbe>();
                RpOnRenderObjectProbe.Mat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.DontSave };
                RpOnRenderObjectProbe.Mat.SetInt("_ZTest", (int)CompareFunction.Always);
                RpOnRenderObjectProbe.Calls = 0;
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.Reinitialize(rt.width, rt.height);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                cam.targetTexture = null;
                // 探针在某个角上画 64×64 纯绿（翻不翻 y 都行）：数纯绿像素
                int green = tex.GetPixels32().Count(px => px.g > 200 && px.r < 60 && px.b < 60);
                r.onRenderObjectCalls = RpOnRenderObjectProbe.Calls;
                r.onRenderObjectDrawn = green >= 64 * 64 / 2;
            }
            finally
            {
                cam.targetTexture = null;
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
                Object.DestroyImmediate(px1);
                if (RpOnRenderObjectProbe.Mat != null)
                {
                    Object.DestroyImmediate(RpOnRenderObjectProbe.Mat);
                    RpOnRenderObjectProbe.Mat = null;
                }
            }
        }
    }
}
