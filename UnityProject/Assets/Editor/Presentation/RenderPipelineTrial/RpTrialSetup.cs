// PL-RP-01 → production/design/presentation-track/handoff/PL-RP-01.md
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// 把 URP 试验影子（<c>.unity-validate-clone-rp</c>，<c>bash tools/pl/pl-rp-shadow.sh setup</c>）设成 URP：在影子自己的 <c>Assets/_PLRP/</c>（非联接目录）建 URP Asset + Renderer（Forward+，SSAO 特性默认关），
    /// Graphics 与全部画质档指向它，色彩空间切 Linear。只认试验影子的工程路径，在别的工程里调用直接拒绝（工作树的 ProjectSettings 一字不动）。
    /// </summary>
    public static class RpTrialSetup
    {
        public const string ShadowDirName = ".unity-validate-clone-rp";
        public const string Folder = "Assets/_PLRP";
        public const string AssetPath = Folder + "/PLRP_URP.asset";
        public const string RendererPath = Folder + "/PLRP_Renderer.asset";
        public const string ShaderFolder = Folder + "/Shaders";
        private const string PostProcessDataPath = "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset";

        public static bool InTrialShadow => Path.GetFileName(Path.GetDirectoryName(Application.dataPath) ?? string.Empty) == ShadowDirName;

        public static void Apply()
        {
            var log = new StringBuilder("[PL-RP 试验影子设置]\n");
            int code = 0;
            try
            {
                code = ApplyCore(log);
            }
            catch (Exception e)
            {
                log.AppendLine("✗ 抛异常：" + e);
                code = 1;
            }
            log.AppendLine(code == 0 ? "结论：试验影子已设成 URP（Forward+、Linear）" : "结论：设置失败");
            Debug.Log(log.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }

        private static int ApplyCore(StringBuilder log)
        {
            if (!InTrialShadow)
            {
                log.AppendLine($"✗ 拒绝：当前工程 {Path.GetDirectoryName(Application.dataPath)} 不是试验影子 {ShadowDirName}（绝不改工作树或 PL 影子 2 的设置）");
                return 2;
            }
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets", "_PLRP");
            }
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(data, RendererPath);
            }
            data.renderingMode = RenderingMode.ForwardPlus;
            if (data.postProcessData == null)
            {
                data.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(PostProcessDataPath);
            }
            if (RpTrialPost.Ssao(data) == null)
            {
                Type t = typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion");
                if (t != null && ScriptableObject.CreateInstance(t) is ScriptableRendererFeature f)
                {
                    f.name = "SSAO";
                    f.SetActive(false);
                    AssetDatabase.AddObjectToAsset(f, data);
                    data.rendererFeatures.Add(f);
                }
            }
            EditorUtility.SetDirty(data);
            log.AppendLine($"✓ Renderer {RendererPath}：{data.renderingMode}、后期数据 {(data.postProcessData != null ? "有" : "缺")}、SSAO 特性 {(RpTrialPost.Ssao(data) != null ? "有（默认关）" : "缺")}");

            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetPath);
            if (asset == null)
            {
                asset = UniversalRenderPipelineAsset.Create(data);
                AssetDatabase.CreateAsset(asset, AssetPath);
            }
            asset.supportsHDR = true;
            asset.msaaSampleCount = 1;
            asset.gpuResidentDrawerMode = GPUResidentDrawerMode.Disabled; // 只在 GRD 小实验里临时开
            EditorUtility.SetDirty(asset);

            GraphicsSettings.defaultRenderPipeline = asset;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = asset;
            }
            QualitySettings.SetQualityLevel(current, false);
            PlayerSettings.colorSpace = ColorSpace.Linear;
            AssetDatabase.ImportAsset(ShaderFolder, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.SaveAssets();

            bool ok = GraphicsSettings.defaultRenderPipeline == asset && QualitySettings.renderPipeline == asset && PlayerSettings.colorSpace == ColorSpace.Linear && data.postProcessData != null;
            log.AppendLine($"{(ok ? "✓" : "✗")} Graphics / {QualitySettings.names.Length} 个画质档指向 {AssetPath}；色彩空间 {PlayerSettings.colorSpace}；替身 shader {string.Join(", ", RpTrialShim.StandInNames())}");
            return ok ? 0 : 1;
        }
    }
}
