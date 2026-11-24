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

        public static bool IsBuiltIn => GraphicsSettings.currentRenderPipeline == null;

        public static int Count => Cache.Count;

        /// <summary>用途 → shader 名（换管线 / 接正式 shader 只改这里）。</summary>
        public static string ShaderName(PresentationMaterialUse use)
        {
            if (IsBuiltIn)
            {
                // Unlit / Glow / Transparent 暂共用主线已有的实例化无光照 shader（Alpha 混合）；正式 shader 来了再分。
                return use == PresentationMaterialUse.Lit ? "Standard" : "BinGames/SimInstancedUnlit";
            }
            return use == PresentationMaterialUse.Lit ? "Universal Render Pipeline/Lit" : "Universal Render Pipeline/Unlit";
        }

        public static Material Get(PresentationMaterialUse use, Color color)
        {
            if (Cache.TryGetValue((use, color), out Material existing) && existing != null)
            {
                return existing;
            }
            string name = ShaderName(use);
            Shader shader = Shader.Find(name);
            if (shader == null)
            {
                Debug.LogError($"[PresentationMaterials] 找不到 shader {name}（用途 {use}），退回 Standard");
                shader = Shader.Find("Standard");
            }
            var material = new Material(shader)
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
