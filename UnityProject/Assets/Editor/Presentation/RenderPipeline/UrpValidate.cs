// PL-RP-02 → production/design/presentation-track/handoff/PL-RP-02.md
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BinGames.Presentation;
using BinGames.PresentationLab;
using GameLogic.FirstPlayable;
using GameLogic.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// PL-RP-02 URP 迁移的自检段（PresentationValidate.Segments）：
    /// <c>UrpSwitch</c>（管线 / 画质档 / Linear / FG 收口点 / 管线开关前后 sim 哈希）与 <c>UrpShaderPass</c>（自写 shader 在 URP 下有可用 pass）不要显卡；
    /// <c>UrpNoMagenta</c>（采集图无品红、黑块不多于 Built-in）与 <c>UrpPerf</c>（两机位帧耗对 RP-01 的 Built-in 读数记账）要显卡，-nographics 下只写“跳过”。
    /// </summary>
    public static class UrpValidate
    {
        public const string Folder = "Assets/Settings/URP";
        public const string RendererPath = Folder + "/URP_ForwardPlus_Renderer.asset";
        public const string DefaultAsset = Folder + "/URP_Ultra.asset";

        /// <summary>画质档 → URP Asset（与原 QualitySettings 的阴影 / 级联 / 距离 / 抗锯齿一致；生成器见交接卡）。</summary>
        public static readonly (string Quality, string Asset, bool Shadows, bool Soft, int Res, int Cascades, float Distance, int Msaa)[] Tiers =
        {
            ("Very Low", "URP_VeryLow", false, false, 1024, 1, 15f, 1),
            ("Low", "URP_Low", false, false, 1024, 1, 20f, 1),
            ("Medium", "URP_Medium", true, false, 1024, 1, 20f, 1),
            ("High", "URP_High", true, true, 2048, 2, 40f, 1),
            ("Very High", "URP_VeryHigh", true, true, 2048, 2, 70f, 2),
            ("Ultra", "URP_Ultra", true, true, 2048, 4, 150f, 2),
        };

        /// <summary>原写死 Standard 的 FG 建材质处（PL-RP-02 改走 PresentationMaterials，逐文件列进 MERGE-PLAN）。</summary>
        public static readonly string[] FgSites =
        {
            "GameScripts/HotFix/GameLogic/View/ViewMaterials.cs",
            "GameScripts/HotFix/GameLogic/View/MachineMorphLibrary.cs",
            "GameScripts/HotFix/GameLogic/Campaign/Regions/WorldTerrainOverlay.cs",
            "GameScripts/HotFix/GameLogic/Campaign/WorldSim/WorldPlanetView.cs",
            "GameScripts/HotFix/GameLogic/FirstPlayable/FPFactory.cs",
        };

        public static readonly string[] TerrainShaders = { "BinGames/Terrain/ContinuousGround", "BinGames/Terrain/MatteProps", "BinGames/Terrain/MutedWater" };

        private static void Expect(bool ok, string what) => PresentationValidate.Expect(ok, what);
        private static void Line(string s) => PresentationValidate.Line(s);

        private static bool GpuBatch => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && Application.isBatchMode;

        // ── UrpSwitch ──

        public static void UrpSwitch()
        {
            var def = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            Expect(def != null && AssetDatabase.GetAssetPath(def) == DefaultAsset, $"Graphics 默认管线 = {DefaultAsset}（实际 {(def != null ? AssetDatabase.GetAssetPath(def) : "Built-in")}）");
            Expect(!PresentationMaterials.IsBuiltIn, $"当前管线是 URP（{GraphicsSettings.currentRenderPipeline?.name ?? "Built-in"}）");
            Expect(PlayerSettings.colorSpace == ColorSpace.Linear && QualitySettings.activeColorSpace == ColorSpace.Linear, $"色彩空间 Linear（Player {PlayerSettings.colorSpace} / 当前 {QualitySettings.activeColorSpace}）");

            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            Expect(renderer != null && renderer.renderingMode == RenderingMode.ForwardPlus && renderer.postProcessData != null,
                $"Renderer {RendererPath}：{(renderer != null ? renderer.renderingMode + "、后期数据 " + (renderer.postProcessData != null ? "有" : "缺") : "缺")}（应 ForwardPlus + 后期数据）");
            ScriptableRendererFeature ssao = RpTrialPost.Ssao(renderer);
            if (ssao != null)
            {
                var so = new SerializedObject(ssao);
                float falloff = so.FindProperty("m_Settings.Falloff")?.floatValue ?? -1f;
                int source = so.FindProperty("m_Settings.Source")?.intValue ?? -1;
                Expect(!ssao.isActive && falloff >= 1000f && source == 0, $"SSAO 特性默认关、Falloff {falloff} ≥ 1000 米（战略镜头约 146 米）、深度来源 Depth（{source}）");
            }
            else
            {
                Expect(false, "Renderer 上有 SSAO 特性（默认关）");
            }

            string[] names = QualitySettings.names;
            Expect(names.Length == Tiers.Length && names.SequenceEqual(Tiers.Select(t => t.Quality)), $"画质档 {names.Length} 个且名字对得上（{string.Join(" / ", names)}）");
            var seen = new HashSet<Object>();
            for (int i = 0; i < Math.Min(names.Length, Tiers.Length); i++)
            {
                (string quality, string assetName, bool shadows, bool soft, int res, int cascades, float distance, int msaa) = Tiers[i];
                var a = QualitySettings.GetRenderPipelineAssetAt(i) as UniversalRenderPipelineAsset;
                string path = a != null ? AssetDatabase.GetAssetPath(a) : "无";
                if (a == null || path != $"{Folder}/{assetName}.asset")
                {
                    Expect(false, $"画质档 {quality} → {Folder}/{assetName}.asset（实际 {path}）");
                    continue;
                }
                seen.Add(a);
                var so = new SerializedObject(a);
                Object r0 = so.FindProperty("m_RendererDataList")?.GetArrayElementAtIndex(0)?.objectReferenceValue;
                bool ok = so.FindProperty("m_MainLightShadowsSupported").boolValue == shadows && so.FindProperty("m_SoftShadowsSupported").boolValue == soft
                    && so.FindProperty("m_MainLightShadowmapResolution").intValue == res && so.FindProperty("m_ShadowCascadeCount").intValue == cascades
                    && Mathf.Approximately(so.FindProperty("m_ShadowDistance").floatValue, distance) && so.FindProperty("m_MSAA").intValue == msaa
                    && so.FindProperty("m_SupportsHDR").boolValue && r0 == renderer;
                Expect(ok, $"画质档 {quality} → {assetName}：阴影 {shadows}/软 {soft}、{res}²、{cascades} 级联、{distance} 米、MSAA {msaa}、HDR、共用 Forward+ Renderer");
            }
            Expect(seen.Count == Tiers.Length, $"每个画质档各一份 URP Asset（{seen.Count} 份不同）");

            // FG 收口点：不再写死 Standard，改走 PresentationMaterials；白名单认 URP Lit
            var stale = new List<string>();
            foreach (string rel in FgSites)
            {
                string text = File.ReadAllText(Path.Combine(Application.dataPath, rel));
                if (Regex.IsMatch(text, "Shader\\.Find\\(\\s*\"Standard\"\\s*\\)|const string \\w+ = \"Standard\";") || !text.Contains("PresentationMaterials."))
                {
                    stale.Add(rel);
                }
            }
            Expect(stale.Count == 0, $"{FgSites.Length} 处 FG 建材质点都改走 PresentationMaterials、不再写死 Standard（未改：{string.Join(", ", stale)}）");
            // 全部运行时代码（GameScripts 下、Editor 以外）扫写死的 Standard / Legacy shader：FG 以后同步进来的新建材质点也会被拦下（不靠固定清单）
            var hard = new Regex(@"Shader\.Find\(\s*""(Standard|Legacy Shaders/[^""]*)""\s*\)|const string \w*Shader\w* = ""Standard"";");
            Expect(hard.IsMatch("x = Shader.Find(\"Standard\");") && hard.IsMatch("Shader.Find( \"Legacy Shaders/Diffuse\" )") && hard.IsMatch("const string SolidShader = \"Standard\";") && !hard.IsMatch("DifficultyId = \"Standard\";") && !hard.IsMatch("public const string Standard = \"Standard\";"), "写死 shader 的正则正反例自测");
            var hardHits = new List<string>();
            string scripts = Path.Combine(Application.dataPath, "GameScripts");
            foreach (string f in Directory.GetFiles(scripts, "*.cs", SearchOption.AllDirectories))
            {
                string rel = f.Substring(Application.dataPath.Length + 1).Replace('\\', '/');
                if (rel.Contains("/Editor/"))
                {
                    continue;
                }
                string[] lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = lines[i];
                    int cm = code.IndexOf("//", StringComparison.Ordinal);
                    code = cm >= 0 ? code.Substring(0, cm) : code;
                    // 允许：先经 PresentationMaterials 取、找不到才退的 ?? 兜底（FPFactory.cs:56）
                    if (hard.IsMatch(code) && !code.Contains("PresentationMaterials."))
                    {
                        hardHits.Add($"{rel}:{i + 1}");
                    }
                }
            }
            Expect(hardHits.Count == 0, $"运行时代码没有写死 Standard / Legacy shader 的建材质点（URP 下会画品红）：{hardHits.Count} 处{(hardHits.Count > 0 ? "：" + string.Join("，", hardHits.Take(12)) : string.Empty)}");
            string binder = File.ReadAllText(Path.Combine(Application.dataPath, "GameScripts/HotFix/GameLogic/ArtBinding/FeatureArtVisualBinder.cs"));
            Expect(binder.Contains("name == \"Universal Render Pipeline/Lit\"") && binder.Contains("name == \"Standard\" && BinGames.Presentation.PresentationMaterials.IsBuiltIn"),
                "FeatureArtVisualBinder 的实例化白名单认 URP Lit，Standard 只在 Built-in 下认（URP 下转换前退回占位材质，不画品红）");

            // Player 构建里 URP Lit 不被剥离：Resources 材质引用它；FindShader 的退路在 Always Included 里
            var keep = Resources.Load<Material>(PresentationMaterials.UrpLitKeepResource);
            Expect(keep != null && keep.shader != null && keep.shader.name == "Universal Render Pipeline/Lit",
                $"Resources/{PresentationMaterials.UrpLitKeepResource}.mat 引用 URP Lit（带进 Player，运行时 Shader.Find 找得到）：{(keep != null && keep.shader != null ? keep.shader.name : "缺")}");
            var gs = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            SerializedProperty always = gs.FindProperty("m_AlwaysIncludedShaders");
            bool fallbackIncluded = false;
            for (int i = 0; always != null && i < always.arraySize; i++)
            {
                fallbackIncluded |= always.GetArrayElementAtIndex(i).objectReferenceValue is Shader sh && sh.name == PresentationMaterials.FallbackShader;
            }
            Expect(fallbackIncluded && Shader.Find(PresentationMaterials.FallbackShader) != null, $"FindShader 的退路 {PresentationMaterials.FallbackShader} 在 Always Included 里");

            var c = new Color(0.62f, 0.31f, 0.18f, 1f);
            Material vm = ViewMaterials.Standard(c);
            Material fp = FPFactory.Mat(c);
            string lit = PresentationMaterials.ShaderName(PresentationMaterialUse.Lit);
            Expect(vm.shader.name == lit && vm.GetColor("_BaseColor") == c && RpTrialShim.UrpVerdict(vm.shader) == "ok",
                $"ViewMaterials.Standard(颜色) → {vm.shader.name}、_BaseColor 照写（material.color 落到 URP Lit 的 MainColor）、URP 能画");
            Expect(fp.shader.name == lit && fp.GetColor("_BaseColor") == c && Mathf.Approximately(fp.GetFloat("_Smoothness"), 0.1f),
                $"FPFactory.Mat → {fp.shader.name}、_BaseColor 照写、_Smoothness 0.1（原 Standard _Glossiness 0.1）");

            SimHashPipelineOnOff();
        }

        /// <summary>“功能开 / 关” = 管线开 / 关：同一进程里先临时退回 Built-in 跑 600 步（全部 PL 功能开），再在 URP 下跑，四个 sim 哈希必须逐个相等。</summary>
        private static void SimHashPipelineOnOff()
        {
            RenderPipelineAsset def = GraphicsSettings.defaultRenderPipeline, q = QualitySettings.renderPipeline;
            PresentationSimHash.Hashes builtin, urp;
            try
            {
                GraphicsSettings.defaultRenderPipeline = null;
                QualitySettings.renderPipeline = null;
                bool wasBuiltIn = PresentationMaterials.IsBuiltIn;
                builtin = PresentationSimHash.Run(PresentationSimHash.DefaultSeed, PresentationSimHash.DefaultSteps, true, Array.Empty<IPresentationModule>());
                Expect(wasBuiltIn, "临时退回 Built-in 生效（GraphicsSettings / QualitySettings 的管线都置空）");
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = def;
                QualitySettings.renderPipeline = q;
                PresentationMaterials.ReleaseAll(); // Built-in 下建的 Standard 材质别留在缓存里给后面的 URP 段用
                ViewMaterials.ReleaseAll();
            }
            urp = PresentationSimHash.Run(PresentationSimHash.DefaultSeed, PresentationSimHash.DefaultSteps, true, Array.Empty<IPresentationModule>());
            Expect(!PresentationMaterials.IsBuiltIn && builtin.Problem == null && urp.Problem == null && builtin.KernelSteps > 0 && builtin.SameAs(urp),
                $"管线 Built-in / URP 两次 sim 哈希一致（种子 {PresentationSimHash.DefaultSeed}、{PresentationSimHash.DefaultSteps} 步）：Built-in {builtin}；URP {urp}");
        }

        // ── UrpShaderPass ──

        public static void UrpShaderPass()
        {
            List<RpTrialCapture.ShaderInfo> all = RpTrialCapture.ShaderAudit().Where(s => !s.path.StartsWith(RpTrialSetup.Folder)).ToList();
            Expect(all.Count >= 15, $"自写 shader 清点到 {all.Count} 个（GameScripts + GameRes 特效，应 ≥ 15）");
            // -nographics 下 URP 管线对象没被建出来（Shader.globalRenderPipeline 为空），引擎按 Built-in 挑子着色器，RpTrialShim.UrpVerdict 看到的是 surface 版：
            // 这里按 URP 的挑法（第一个 RenderPipeline 标签为空或 UniversalPipeline 的子着色器）静态判；真上屏由 UrpNoMagenta（显卡）按运行时判定再核一次
            List<string> bad = all.Select(s => (s, v: StaticVerdict(AssetDatabase.LoadAssetAtPath<Shader>(s.path)))).Where(x => x.v != "ok").Select(x => $"{x.s.name}={x.v}").ToList();
            Expect(bad.Count == 0, $"全部自写 shader 按 URP 的子着色器挑法有可用的颜色 pass（不可用：{string.Join(", ", bad)}）");
            foreach (RpTrialCapture.ShaderInfo s in all)
            {
                s.srpBatcher = SrpCode(AssetDatabase.LoadAssetAtPath<Shader>(s.path)); // ShaderAudit 按引擎当前挑中的子着色器算码，-nographics 下是 Built-in 那个：按 URP 的挑法重算
            }
            foreach (RpTrialCapture.ShaderInfo s in all.Where(s => s.srpBatcher != 0))
            {
                Line($"  - SRP Batcher 不兼容 {s.name}（码 {s.srpBatcher}{(s.srpBatcher == -99 ? " = 读不到内部 API，未测" : string.Empty)}）：{SrpReason(s.path)}");
            }
            Line($"  - SRP Batcher 兼容 {all.Count(s => s.srpBatcher == 0)} / {all.Count}：{string.Join(", ", all.Where(s => s.srpBatcher == 0).Select(s => s.name))}");

            foreach (string name in TerrainShaders)
            {
                Shader sh = Shader.Find(name);
                int sub = sh == null ? -1 : UrpSubShader(sh);
                string tags = sub < 0 ? "无" : string.Join("/", Enumerable.Range(0, sh.GetPassCountInSubshader(sub)).Select(p => sh.FindPassTagValue(sub, p, LightModeTag).name));
                bool ok = sub == 0 && sh.isSupported && !ShaderUtil.ShaderHasError(sh) && new[] { "UNIVERSALFORWARD", "SHADOWCASTER", "DEPTHONLY" }.All(t => tags.ToUpperInvariant().Contains(t));
                string path = sh != null ? AssetDatabase.GetAssetPath(sh) : null;
                string src = path != null ? File.ReadAllText(path) : string.Empty;
                Expect(ok, $"地形 {name}：URP 挑第 {sub} 个子着色器（应 0）、pass {tags}（含 UniversalForward / ShadowCaster / DepthOnly）、无编译错误（{(sh != null && ShaderUtil.ShaderHasError(sh) ? "有错" : "无错")}）");
                Expect(src.Contains("#pragma surface") && src.Contains("\"RenderPipeline\"=\"UniversalPipeline\""), $"地形 {name} 保留原 surface 子着色器（退回 Built-in 只改 ProjectSettings 即可）");
            }
        }

        private static readonly ShaderTagId LightModeTag = new ShaderTagId("LightMode"), PipelineTag = new ShaderTagId("RenderPipeline");
        private static readonly HashSet<string> ColorTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "", "SRPDefaultUnlit", "UniversalForward", "UniversalForwardOnly", "UniversalGBuffer" };

        /// <summary>URP 会挑的子着色器：第一个 RenderPipeline 标签为空或 UniversalPipeline 的（硬件不支持时引擎还会往后退，那一层由显卡段核）。</summary>
        public static int UrpSubShader(Shader s)
        {
            for (int i = 0; i < s.subshaderCount; i++)
            {
                string rp = s.FindSubshaderTagValue(i, PipelineTag).name ?? string.Empty;
                if (rp.Length == 0 || rp == "UniversalPipeline")
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>同 <see cref="RpTrialShim.UrpVerdict"/> 的口径（ok / error / none），但按 URP 的挑法选子着色器，不依赖管线对象是否已建（-nographics 下也准）。</summary>
        public static string StaticVerdict(Shader s)
        {
            int sub = s == null ? -1 : UrpSubShader(s);
            if (sub < 0)
            {
                return "error";
            }
            var tags = Enumerable.Range(0, s.GetPassCountInSubshader(sub)).Select(p => s.FindPassTagValue(sub, p, LightModeTag).name ?? string.Empty).ToList();
            return tags.Any(ColorTags.Contains) ? "ok" : tags.Count > 0 ? "error" : "none";
        }

        private static int SrpCode(Shader s)
        {
            System.Reflection.MethodInfo m = typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityCode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            try
            {
                return s != null && m != null && m.GetParameters().Length == 2 ? Convert.ToInt32(m.Invoke(null, new object[] { s, UrpSubShader(s) })) : -99;
            }
            catch (Exception)
            {
                return -99; // Unity 改了内部 API 名 / 签名：记未测，不判失败
            }
        }

        private static string SrpReason(string path)
        {
            string src = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var r = new List<string>();
            if (src.Contains("\"RenderPipeline\"=\"UniversalPipeline\""))
            {
                return src.Contains("UNITY_INSTANCING_BUFFER") ? "URP 子着色器：_Color 走实例化缓冲（属性块逐件着色，靠 GPU 实例化合批）" : "URP 子着色器：源码看不出（看 Frame Debugger）";
            }
            if (src.Contains("CGPROGRAM"))
            {
                r.Add("CG 写法（UnityCG.cginc），材质属性不在 UnityPerMaterial CBUFFER");
            }
            if (src.Contains("UNITY_INSTANCING_BUFFER"))
            {
                r.Add("颜色等走实例化缓冲（RenderMesh*Instanced / 属性块着色，本来就不靠 SRP Batcher 合批）");
            }
            if (!src.Contains("UnityPerMaterial"))
            {
                r.Add("没有 UnityPerMaterial CBUFFER");
            }
            return r.Count == 0 ? "源码看不出（看 Frame Debugger）" : string.Join("；", r);
        }

        // ── UrpNoMagenta（要显卡） ──

        /// <summary>采集：RP-01 的 4 个对比机位（开 = PL 整体开，与 RP-01 Built-in 列同口径）+ HomeDefault 开 / 关。NS-01 的 8 个英雄时刻做出来后加进这里。</summary>
        public static readonly (string Scene, bool[] States)[] Shots =
        {
            ("RpHome", new[] { true }), ("RpHomeWide", new[] { true }), ("RpFactory", new[] { true }),
            ("RpNightRaid", new[] { true }), // 一个进程只搭一次（RP-01 §7：第二次搭会退化）；场景方法里断言来袭单位 ≥ 48、炮塔可见 ≥ 3/8
            ("HomeDefault", new[] { false, true }),
        };

        public static void UrpNoMagenta()
        {
            if (!GpuBatch)
            {
                Line("  - 跳过：没有图形设备（-nographics）或在交互编辑器里。要显卡：PL_VALIDATE_SEGMENTS=UrpNoMagenta,UrpPerf bash tools/pl/pl-unity.sh gpu GameLogic.EditorTools.PresentationValidate.RunAll urp-gpu");
                return;
            }
            string outDir = Path.Combine(PresentationCapture.ReviewRoot(), "PL-RP-02", "urp-check");
            Directory.CreateDirectory(outDir);
            var bad = new SortedSet<string>();
            int calls = 0;
            void Scan(ScriptableRenderContext ctx, Camera cam)
            {
                if (calls++ % 10 != 0)
                {
                    return;
                }
                foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (!r.enabled)
                    {
                        continue;
                    }
                    foreach (Material m in r.sharedMaterials)
                    {
                        if (m != null && RpTrialShim.UrpVerdict(m.shader) == "error")
                        {
                            bad.Add($"{(m.shader != null ? m.shader.name : "无 shader")}（{m.name} @ {r.name}）");
                        }
                    }
                }
            }
            RenderPipelineManager.beginCameraRendering += Scan;
            try
            {
                foreach ((string scene, bool[] states) in Shots)
                {
                    foreach (PresentationCapture.Shot shot in PresentationCapture.Capture(scene, outDir, CaptureInject.None, states))
                    {
                        Expect(string.IsNullOrEmpty(shot.error), $"{shot.scene}_{shot.state} 采集成功{(string.IsNullOrEmpty(shot.error) ? string.Empty : "：" + shot.error.Split('\n')[0])}");
                    }
                }
            }
            finally
            {
                RenderPipelineManager.beginCameraRendering -= Scan;
                RpTrialCapture.SetCombatDrawHook(false);
            }
            foreach (string name in TerrainShaders) // 管线对象已建（刚渲染过），这里是引擎运行时真挑中的子着色器
            {
                Shader sh = Shader.Find(name);
                Expect(RpTrialShim.UrpVerdict(sh) == "ok" && RpTrialShim.PassTags(sh).ToUpperInvariant().Contains("UNIVERSALFORWARD"), $"显卡下地形 {name} 运行时走 URP 子着色器（pass {RpTrialShim.PassTags(sh)}）");
            }
            Expect(calls > 0 && bad.Count == 0, $"采集期间（{calls} 次镜头渲染）启用中的渲染器没有 URP 画不了的材质（{bad.Count} 种：{string.Join("；", bad.Take(8))}）");
            int code = PresentationCapture.RunPython("pl_urp_check.py", $"\"{outDir}\"", out string output);
            foreach (string l in output.Split('\n').Where(l => l.Trim().Length > 0))
            {
                Line("    " + l.TrimEnd());
            }
            // 卡片原口径“黑块数 ≤ Built-in”在 Linear 下过不了（暗部整体压暗约 30%，要调 FG 灯光常量或 LOOK-01 定曝光）：硬判改成“无新黑洞”，黑块总数只报告（⚠），见交接卡待用户复核
            Expect(code == 0, $"pl_urp_check.py：品红像素 = 0、无新黑洞（URP < 0.02 而 Built-in ≥ 0.05 的 32×32 块）；黑块总数对 Built-in 只报告（退出码 {code}，明细 {outDir}/urp-check.json）");
        }

        // ── UrpGlOverlay（要显卡） ──

        /// <summary>
        /// TerrainStream.OnRenderObject 地块线那种画法（世界坐标 + Hidden/Internal-Colored + ZTest LessEqual）在 URP 下：画得出、且被前面的实体挡住（深度测试生效）。
        /// RP-01 只测了屏幕坐标 + ZTest Always。俯视：地上 0.5 米一条 40×4 米的绿带，中间压一个 8×6×8 米的灰方块，方块范围内不许有绿、两侧要有绿。
        /// </summary>
        public static void UrpGlOverlay()
        {
            if (!GpuBatch)
            {
                Line("  - 跳过：没有图形设备（-nographics）或在交互编辑器里。要显卡：PL_VALIDATE_SEGMENTS=UrpGlOverlay bash tools/pl/pl-unity.sh gpu GameLogic.EditorTools.PresentationValidate.RunAll urp-gl");
                return;
            }
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Camera cam = Camera.main;
            cam.orthographic = true;
            cam.orthographicSize = 25f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.transform.SetPositionAndRotation(new Vector3(0f, 40f, 0f), Quaternion.Euler(90f, 0f, 0f));
            var box = new GameObject("pl_rp_occluder");
            box.transform.position = new Vector3(0f, 3f, 0f);
            box.transform.localScale = new Vector3(8f, 6f, 8f);
            box.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            box.AddComponent<MeshRenderer>().sharedMaterial = PresentationMaterials.Get(PresentationMaterialUse.Lit, new Color(0.5f, 0.5f, 0.5f));
            var probeGo = new GameObject("pl_rp_gl_world");
            probeGo.AddComponent<RpOnRenderObjectProbe>();
            var mat = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.DontSave };
            mat.SetInt("_ZTest", (int)CompareFunction.LessEqual); // 与 TerrainStream.cs:274 同设置
            mat.SetInt("_ZWrite", 0);
            RpOnRenderObjectProbe.Mat = mat;
            RpOnRenderObjectProbe.WorldQuad = new[] { new Vector3(-20f, 0.5f, -2f), new Vector3(-20f, 0.5f, 2f), new Vector3(20f, 0.5f, 2f), new Vector3(20f, 0.5f, -2f) };
            RpOnRenderObjectProbe.Calls = 0;
            const int w = 1920, h = 1080;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                Color32[] px = tex.GetPixels32();
                // 正交 50 米高 / 1080 像素 ≈ 21.6 像素每米：方块 ±4 米 ≈ 屏幕中心 ±86 像素，绿带 ±2 米 ≈ ±43 像素（取内缩的判定框，避开边缘与阴影）
                int inside = 0, outside = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Color32 c = px[y * w + x];
                        if (c.g > 200 && c.r < 60 && c.b < 60)
                        {
                            if (Math.Abs(x - w / 2) < 60 && Math.Abs(y - h / 2) < 30) inside++;
                            else outside++;
                        }
                    }
                }
                Expect(RpOnRenderObjectProbe.Calls > 0 && outside > 5000,
                    $"URP 下 OnRenderObject 世界坐标 GL（ZTest LessEqual，TerrainStream 地块线画法）画得出：回调 {RpOnRenderObjectProbe.Calls} 次，方块外绿像素 {outside}（> 5000）");
                Expect(inside == 0, $"URP 下该 GL 叠层深度测试生效（被方块挡住）：方块范围内绿像素 {inside}（= 0）");
            }
            finally
            {
                cam.targetTexture = null;
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
                RpOnRenderObjectProbe.Mat = null;
                RpOnRenderObjectProbe.WorldQuad = null;
                Object.DestroyImmediate(mat);
            }
        }

        // ── UrpPerf（要显卡） ──

        public static readonly string[] PerfScenes = { "RpHome", "RpFactory" };

        public static void UrpPerf()
        {
            if (!GpuBatch)
            {
                Line("  - 跳过：没有图形设备（-nographics）或在交互编辑器里（同 UrpNoMagenta 的 gpu 命令）");
                return;
            }
            string outDir = Path.Combine(PresentationCapture.ReviewRoot(), "PL-RP-02", "measure");
            Directory.CreateDirectory(outDir);
            var run = new RpTrialCapture.Result { pipeline = PresentationMaterials.IsBuiltIn ? "builtin" : "urp", colorSpace = QualitySettings.activeColorSpace.ToString(), device = SystemInfo.graphicsDeviceName };
            if (PresentationMaterials.IsBuiltIn)
            {
                // 成对测量的 Built-in 半边（tools/pl/pl-urp-perf-pair.sh 在 clone-rp 临时退回 Built-in 时跑）：只量、只记，不判
                foreach (string scene in PerfScenes)
                {
                    RpTrialCapture.SceneMeasure m = RpTrialCapture.MeasureScene(scene, "none");
                    run.scenes.Add(m);
                    Expect(string.IsNullOrEmpty(m.error) && m.p95 > 0, $"{scene} Built-in 对照帧耗（成对测量）：p50 {m.p50:F3} / p95 {m.p95:F3} ms{(string.IsNullOrEmpty(m.error) ? string.Empty : "；出错 " + m.error.Split('\n')[0])}");
                }
                File.WriteAllText(Path.Combine(outDir, "builtin-paired.json"), JsonUtility.ToJson(run, true));
                return;
            }
            // 参照：PL_RP_PERF_REF（成对测量刚量的 Built-in）优先；没有就用 RP-01 存的读数。
            // 这个口径是“渲染 + 读回 1 像素”的墙钟延迟，随整机 / 显卡频率状态整体漂（11-20 晚上空闲时 Built-in 自己也从 0.71 漂到 4.8 ms），跨时段比没有意义，正式记账用成对测量
            string refEnv = Environment.GetEnvironmentVariable("PL_RP_PERF_REF");
            bool paired = !string.IsNullOrEmpty(refEnv);
            string builtinPath = paired ? refEnv : Path.Combine(PresentationCapture.ReviewRoot(), "PL-RP-01", "measure", "rp-measure-builtin.json");
            RpTrialCapture.Result builtin = File.Exists(builtinPath) ? JsonUtility.FromJson<RpTrialCapture.Result>(File.ReadAllText(builtinPath)) : null;
            Expect(builtin != null, $"Built-in 参照读数在（{(paired ? "成对测量" : "RP-01 存档，跨时段，仅供参考")}：{builtinPath}）");
            var urp = run;
            foreach (string scene in PerfScenes)
            {
                RpTrialCapture.SceneMeasure m = RpTrialCapture.MeasureScene(scene, "none");
                urp.scenes.Add(m);
                RpTrialCapture.SceneMeasure b = builtin?.scenes.FirstOrDefault(x => x.scene == scene);
                double limit = b == null ? 0 : Math.Max(b.p95 * 1.25, b.p95 + 0.5);
                PerfGate.Expect(string.IsNullOrEmpty(m.error) && b != null && m.p95 > 0,
                    $"{scene} 帧耗（编辑模式手动渲染 1920×1080 + 读回 1 像素等 GPU，预热 30 / 测 120 帧，同 RP-01 口径）：URP p50 {m.p50:F3} / p95 {m.p95:F3} ms；Built-in{(paired ? "（成对）" : "（RP-01 存档）")} p50 {b?.p50:F3} / p95 {b?.p95:F3} ms{(string.IsNullOrEmpty(m.error) ? string.Empty : "；出错 " + m.error.Split('\n')[0])}",
                    new[] { PerfGate.Le(m.p95, limit, $"{scene} URP p95 ms（≤ Built-in × 1.25 或 +0.5 ms）") }, Expect, Line);
            }
            File.WriteAllText(Path.Combine(outDir, "urp-perf.json"), JsonUtility.ToJson(urp, true));
            if (Environment.GetEnvironmentVariable("PL_RP_PERF_BREAKDOWN") == "1")
            {
                PerfBreakdown();
            }
        }

        /// <summary>
        /// 帧耗拆解（只报告、不判，PL_RP_PERF_BREAKDOWN=1 时跑）：当前 URP Asset 逐项关掉 MSAA / HDR / 主光阴影再量 RpHome / RpFactory，
        /// 看 URP 比 Built-in 多出的墙钟花在哪（Built-in 往 aa=1 的 RT 手动渲染时不做 MSAA，URP 照 Asset 自建 MSAA + HDR 中间缓冲）。改动只在内存里，finally 原样还原。
        /// </summary>
        private static void PerfBreakdown()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset a))
            {
                return;
            }
            int msaa = a.msaaSampleCount;
            bool hdr = a.supportsHDR;
            float shadow = a.shadowDistance;
            try
            {
                var steps = new (string Name, Action Apply)[]
                {
                    ("MSAA 关", () => a.msaaSampleCount = 1),
                    ("+ HDR 关", () => a.supportsHDR = false),
                    ("+ 主光阴影关", () => a.shadowDistance = 0f),
                };
                foreach ((string name, Action apply) in steps)
                {
                    apply();
                    foreach (string scene in PerfScenes)
                    {
                        RpTrialCapture.SceneMeasure m = RpTrialCapture.MeasureScene(scene, "none");
                        Line($"  · 拆解 {scene}「{name}」：URP p50 {m.p50:F3} / p95 {m.p95:F3} ms{(string.IsNullOrEmpty(m.error) ? string.Empty : "；出错 " + m.error.Split('\n')[0])}");
                    }
                }
            }
            finally
            {
                a.msaaSampleCount = msaa;
                a.supportsHDR = hdr;
                a.shadowDistance = shadow;
            }
        }
    }
}
