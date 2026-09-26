using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG0-ARCH-03：地点表现对象（建筑方块、残骸、地面物、机器、敌人、标记、编队目标点）的纯色材质——按颜色共享、按需创建、成对释放。
    ///
    /// 地点不被观察时销毁全部表现对象、镜头飞回来再重建（ADR-ARC-003 第 5 节）。以前每个方块 <c>new Material</c> 一份，
    /// 销毁 GameObject 时材质不跟着释放，每飞一次镜头泄漏一批。现在同一着色器 + 同一颜色只有一份材质：
    /// 数量 = 画面用到的颜色种数，与建筑 / 敌人数量、观察切换次数无关；整个世界卸载时（<c>WorldSimulation.UnloadAll</c>）统一释放。
    ///
    /// 取到的材质是共享的：**换颜色一律整体换材质**（<c>renderer.sharedMaterial = ViewMaterials.Standard(新颜色)</c>），
    /// 不许改它的 <c>color</c>，也不要对这些渲染器调 <c>renderer.material</c>（getter 会复制出一份不受管的材质）。
    /// </summary>
    public static class ViewMaterials
    {
        private const string StandardShader = "Standard";

        private static readonly Dictionary<(string Shader, uint Color), Material> Cache = new Dictionary<(string, uint), Material>();

        /// <summary>当前缓存的材质份数（自检读：观察切换前后不变）。</summary>
        public static int Count => Cache.Count;

        /// <summary>Standard 着色器、指定颜色的共享材质（颜色按 8 位通道归并）。</summary>
        public static Material Standard(Color color) => Get(StandardShader, color);

        public static Material Get(string shaderName, Color color)
        {
            Color32 c = color;
            uint key = ((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a;
            if (Cache.TryGetValue((shaderName, key), out Material existing) && existing != null)
            {
                return existing;
            }
            var material = new Material(Shader.Find(shaderName))
            {
                color = color,
                name = $"ViewMaterial {shaderName} #{key:X8}",
                hideFlags = HideFlags.DontSave,
            };
            Cache[(shaderName, key)] = material;
            return material;
        }

        /// <summary>
        /// 换颜色（选中高亮、阵亡变灰、状态色）：命中盒渲染器连同它的占位剪影部件（<see cref="PlaceholderSilhouette"/> 的
        /// “Silhouette”子节点，部件与命中盒共用一份材质）一起换成该颜色的共享材质。不分配内存；颜色没变时什么都不改（每帧调用也安全）。
        /// </summary>
        public static void Recolor(Renderer renderer, Color color)
        {
            if (renderer == null)
            {
                return;
            }
            Material m = Standard(color);
            if (renderer.sharedMaterial != m)
            {
                renderer.sharedMaterial = m;
            }
            Transform parts = renderer.transform.Find(PlaceholderSilhouette.PartsName);
            if (parts == null)
            {
                return;
            }
            for (int i = 0; i < parts.childCount; i++)
            {
                Renderer r = parts.GetChild(i).GetComponent<Renderer>();
                if (r != null && r.sharedMaterial != m)
                {
                    r.sharedMaterial = m;
                }
            }
        }

        /// <summary>释放全部共享材质（整个世界卸载后调用：此时没有任何地点表现对象还引用它们）。</summary>
        public static void ReleaseAll()
        {
            foreach (Material m in Cache.Values)
            {
                UnityObjects.Release(m);
            }
            Cache.Clear();
        }
    }
}
