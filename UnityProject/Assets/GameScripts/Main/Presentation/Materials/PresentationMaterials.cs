// PL-INF-01 → production/design/presentation-track/handoff/PL-INF-01.md
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BinGames.Presentation
{
    /// <summary>材质用途。正式 shader 接入时只改 <see cref="PresentationMaterials.ShaderName"/> 的映射。</summary>
    public enum PresentationMaterialUse
    {
        Lit,
        Unlit,
        Glow,
        Transparent,
    }

    /// <summary>
    /// PL 唯一的取材质入口（D23）：按 (用途, 颜色) 缓存共享材质；渲染管线判断只在这里。
    /// 缓存键是原样的 Color（浮点、不截断），HDR 颜色（分量 &gt; 1）各自一份材质。
    /// Glow 目前只是一个用途名：Built-in 下与 Unlit / Transparent 同一 shader，没有发光强度参数；第一张要 HDR 发光的卡补 _EmissionColor / HDR 并改 ShaderName 映射。
    /// PL 代码不得直接 <c>Shader.Find</c>（PresentationValidate.Materials 静态扫描断言）。
    /// 取到的材质是共享的：换颜色就整体换材质，不要改它的 color，也不要调 renderer.material。
    /// </summary>
    public static class PresentationMaterials
    {
        private static readonly Dictionary<(PresentationMaterialUse, Color), Material> Cache = new Dictionary<(PresentationMaterialUse, Color), Material>();

        /// <summary>FindShader 找不到时的退路：在 GraphicsSettings 的 Always Included 里，Player 一定在。</summary>
        public const string FallbackShader = "BinGames/SimInstancedUnlit";

        /// <summary>PL-RP-02：引用 URP Lit 的 Resources 材质，只为把 URP Lit 带进 Player 构建（运行时 Shader.Find 找得到）；不在运行时加载。</summary>
        public const string UrpLitKeepResource = "PL_UrpLitKeep";

        public static bool IsBuiltIn => GraphicsSettings.currentRenderPipeline == null;

        public static int Count => Cache.Count;

        /// <summary>用途 → shader 名（换管线 / 接正式 shader 只改这里）。</summary>
        public static string ShaderName(PresentationMaterialUse use)
        {
            // Unlit / Glow / Transparent 两套管线都共用主线已有的实例化无光照 shader（Alpha 混合、无 LightMode → URP 当 SRPDefaultUnlit 画）；正式 shader 来了再分。
            if (use != PresentationMaterialUse.Lit)
            {
                return "BinGames/SimInstancedUnlit";
            }
            return IsBuiltIn ? "Standard" : "Universal Render Pipeline/Lit"; // PL-RP-02：URP Lit 的 [MainColor] 是 _BaseColor，material.color 照常可用
        }

        /// <summary>
        /// 按用途找 shader（找不到记错误并退回 <see cref="FallbackShader"/>）。PL-RP-02 起 FG 里原写死 <c>Shader.Find("Standard")</c> 的建材质处都改调这里，
        /// 换管线只改 <see cref="ShaderName"/> 一处；颜色仍用 <c>material.color</c>（Standard 的 _Color / URP Lit 的 _BaseColor 都是 MainColor）。
        /// </summary>
        public static Shader FindShader(PresentationMaterialUse use)
        {
            string name = ShaderName(use);
            Shader shader = Shader.Find(name);
            if (shader == null)
            {
                // 退到 GraphicsSettings 常驻包含的实例化无光照 shader（两套管线都能画），不退回同名 Lit（Player 里被剥离时同样找不到）
                Debug.LogError($"[PresentationMaterials] 找不到 shader {name}（用途 {use}），退回 {FallbackShader}");
                shader = Shader.Find(FallbackShader);
            }
            return shader;
        }

        public static Material Get(PresentationMaterialUse use, Color color)
        {
            if (Cache.TryGetValue((use, color), out Material existing) && existing != null)
            {
                return existing;
            }
            var material = new Material(FindShader(use))
            {
                color = color,
                name = $"PL {use} {color}",
                hideFlags = HideFlags.DontSave,
                enableInstancing = true,
            };
            Cache[(use, color)] = material;
            return material;
        }

        /// <summary>释放全部缓存材质（整局卸载 / 自检收尾）。调用后旧引用失效。</summary>
        public static void ReleaseAll()
        {
            foreach (Material m in Cache.Values)
            {
                if (m != null)
                {
                    Object.DestroyImmediate(m);
                }
            }
            Cache.Clear();
        }
    }
}
