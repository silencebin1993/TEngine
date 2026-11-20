// PL-RP-01 → production/design/presentation-track/handoff/PL-RP-01.md
using System.Collections.Generic;
using System.Linq;
using BinGames.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// 渲染管线试验垫片（功能 pl.rptrial，只在试验采集 / 自检里登记）：URP 下每帧把已载入材质里 URP 画不了的 shader 换掉——
    /// Standard 系 → PL 材质工厂的 Lit（URP 下 = Universal Render Pipeline/Lit，颜色 / 贴图 / 光滑度 / 金属度 / 自发光照抄），
    /// 3 个地形 surface shader → 试验影子 <c>Assets/_PLRP/Shaders/</c> 里的最小替身（属性同名，换 shader 即可）。停用时全部换回。
    /// Built-in 下什么都不做。不碰 FG 文件：卡片原写的 ViewMaterials / MachineMorphLibrary / WorldTerrainOverlay 三处 #if 接缝改由这里在运行时统一换（同样的画面、零接缝），
    /// 真正切管线时（PL-RP-02）那三处要改成调工厂，见 RP-01 报告的改动面清单。
    /// </summary>
    [PresentationTime(TimeDomain.Real)]
    public sealed class RpTrialShim : IPresentationModule
    {
        /// <summary>原 shader 名 → 替身文件名（Assets/_PLRP/Shaders/&lt;名&gt;.shader，影子独有）。</summary>
        public static readonly Dictionary<string, string> StandIns = new Dictionary<string, string>
        {
            { "BinGames/Terrain/ContinuousGround", "TerrainGround" },
            { "BinGames/Terrain/MatteProps", "TerrainProps" },
            { "BinGames/Terrain/MutedWater", "TerrainWater" },
        };

        /// <summary>换过的材质 → 换之前的 shader 与整份快照（属性、关键字、队列；OnDisable 按它原样还原，Standard 透明 / 预乘的混合方式不被改坏）。</summary>
        private readonly Dictionary<Material, (Shader shader, Material snap)> _original = new Dictionary<Material, (Shader, Material)>();
        private readonly Dictionary<string, Shader> _standIn = new Dictionary<string, Shader>();
        private Shader _lit;

        public string Id => PresentationFeatureRegistry.RpTrialId;

        /// <summary>换过的材质数（自检与报告用）。</summary>
        public int Remapped => _original.Count;

        public static IEnumerable<string> StandInNames() => StandIns.Values.Select(n => n + (AssetDatabase.LoadAssetAtPath<Shader>($"{RpTrialSetup.ShaderFolder}/{n}.shader") != null ? "✓" : "（缺）"));

        public static bool IsStandardFamily(string shaderName) => shaderName == "Standard" || shaderName == "Standard (Specular setup)" || shaderName.StartsWith("Legacy Shaders/");

        public void OnEnable()
        {
            if (PresentationMaterials.IsBuiltIn)
            {
                return;
            }
            _lit = PresentationMaterials.Get(PresentationMaterialUse.Lit, Color.white).shader;
            foreach (KeyValuePair<string, string> kv in StandIns)
            {
                _standIn[kv.Key] = AssetDatabase.LoadAssetAtPath<Shader>($"{RpTrialSetup.ShaderFolder}/{kv.Value}.shader");
            }
            Sweep();
        }

        public void Tick(in PresentationFrame frame)
        {
            if (_lit != null)
            {
                Sweep(); // shortcut: 每帧扫全部已载入材质（编辑器试验用，不进游戏），真切管线时改工厂而不是扫
            }
        }

        public void Rebuild(RebuildReason reason) { }

        public void OnDisable()
        {
            foreach (KeyValuePair<Material, (Shader shader, Material snap)> kv in _original)
            {
                Material m = kv.Key;
                if (m != null)
                {
                    m.shader = kv.Value.shader;
                    m.CopyPropertiesFromMaterial(kv.Value.snap);
                    m.shaderKeywords = kv.Value.snap.shaderKeywords;
                    m.renderQueue = kv.Value.snap.renderQueue;
                    Keep(m);
                }
                Object.DestroyImmediate(kv.Value.snap);
            }
            _original.Clear();
            _snaps.Clear();
            _standIn.Clear();
            _lit = null;
        }

        private readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
        private static readonly int MainTex = Shader.PropertyToID("_MainTex"), BaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int ColorId = Shader.PropertyToID("_Color"), BaseColor = Shader.PropertyToID("_BaseColor");

        /// <summary>
        /// 属性块里按 Standard 旧名给的贴图 / 颜色（例：WorldTerrainOverlay.cs:798 每块地形用属性块给 _MainTex = 地形与迷雾图）抄到 URP Lit 的新名上；
        /// 不抄的话换成 URP Lit 的地面全是白的（11-20 第一次采集）。只碰已换成 Lit 的材质的渲染器。
        /// </summary>
        private void CarryPropertyBlocks()
        {
            foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                Material m = r.sharedMaterial;
                if (m == null || m.shader != _lit || !r.HasPropertyBlock())
                {
                    continue;
                }
                r.GetPropertyBlock(_mpb);
                bool changed = false;
                if (_mpb.HasTexture(MainTex) && _mpb.GetTexture(MainTex) != _mpb.GetTexture(BaseMap))
                {
                    _mpb.SetTexture(BaseMap, _mpb.GetTexture(MainTex));
                    changed = true;
                }
                if (_mpb.HasColor(ColorId) && _mpb.GetColor(ColorId) != _mpb.GetColor(BaseColor))
                {
                    _mpb.SetColor(BaseColor, _mpb.GetColor(ColorId));
                    changed = true;
                }
                if (changed)
                {
                    r.SetPropertyBlock(_mpb);
                }
            }
        }

        private void Sweep()
        {
            CarryPropertyBlocks();
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null || _original.ContainsKey(m) || _snaps.Contains(m))
                {
                    continue;
                }
                string name = m.shader.name;
                if (IsStandardFamily(name))
                {
                    _original[m] = (m.shader, Snapshot(m));
                    ToUrpLit(m, _lit);
                    Keep(m);
                }
                else if (_standIn.TryGetValue(name, out Shader s) && s != null)
                {
                    _original[m] = (m.shader, Snapshot(m));
                    m.shader = s;
                    Keep(m);
                }
            }
        }

        private readonly HashSet<Material> _snaps = new HashSet<Material>();

        private Material Snapshot(Material m)
        {
            var snap = new Material(m) { hideFlags = HideFlags.HideAndDontSave, renderQueue = m.renderQueue, shaderKeywords = m.shaderKeywords };
            _snaps.Add(snap); // 快照本身也是 Standard 材质：Sweep 跳过它
            return snap;
        }

        /// <summary>磁盘上的材质（GameRes 经联接就是工作树）只在内存里换：清掉脏标记，任何 SaveAssets 都不会把试验改动写回去。</summary>
        private static void Keep(Material m)
        {
            if (EditorUtility.IsPersistent(m))
            {
                EditorUtility.ClearDirty(m);
            }
        }

        /// <summary>Standard → URP Lit：换 shader 后照抄同义属性（URP Lit 留着 _Color / _MainTex 等旧名但不读它们）；透明模式（_Mode ≥ 2）照 URP 的透明面设置。</summary>
        public static void ToUrpLit(Material m, Shader lit)
        {
            Color color = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
            Texture tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            float gloss = m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness") : 0.5f;
            float metal = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f;
            bool emissive = m.IsKeywordEnabled("_EMISSION");
            Color emission = m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor") : Color.black;
            float mode = m.HasProperty("_Mode") ? m.GetFloat("_Mode") : 0f;
            int queue = m.renderQueue;
            m.shader = lit;
            m.SetColor("_BaseColor", color);
            m.SetTexture("_BaseMap", tex);
            m.SetFloat("_Smoothness", gloss);
            m.SetFloat("_Metallic", metal);
            if (emissive)
            {
                m.SetColor("_EmissionColor", emission);
                m.EnableKeyword("_EMISSION");
            }
            if (mode >= 0.5f && mode < 1.5f) // Standard Cutout → URP 镂空
            {
                m.SetFloat("_AlphaClip", 1f);
                m.SetFloat("_Cutoff", m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : 0.5f);
                m.EnableKeyword("_ALPHATEST_ON");
                m.renderQueue = (int)RenderQueue.AlphaTest;
            }
            if (mode >= 2f)
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = queue >= 3000 ? queue : (int)RenderQueue.Transparent;
            }
        }

        // ── URP 能不能画（品红来源判定） ──

        // 引擎返回的标签名是大写（FORWARDBASE），按不分大小写比
        private static readonly ShaderTagId LightMode = new ShaderTagId("LightMode");
        private static readonly HashSet<string> UrpColorTags = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "", "SRPDefaultUnlit", "UniversalForward", "UniversalForwardOnly", "UniversalGBuffer" };
        private static readonly HashSet<string> LegacyTags = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "Always", "ForwardBase", "PrepassBase", "Vertex", "VertexLMRGBM", "VertexLM", "Deferred" };

        /// <summary>
        /// 当前管线下这个 shader 的“活动子着色器”会被 URP 当成什么：<c>ok</c>（有 URP 会画的颜色 pass）/ <c>error</c>（只有旧管线 pass 或不支持 → URP 画成品红）/ <c>none</c>（没有颜色 pass）。
        /// Built-in 下恒 <c>ok</c>（不判）。
        /// </summary>
        public static string PassTags(Shader s) =>
            s == null ? "null" : string.Join("/", Enumerable.Range(0, s.passCount).Select(i => s.FindPassTagValue(i, LightMode).name ?? "∅")) + $"（supported {s.isSupported}）";

        public static string UrpVerdict(Shader s)
        {
            if (s == null)
            {
                return "error";
            }
            if (PresentationMaterials.IsBuiltIn)
            {
                return "ok";
            }
            if (!s.isSupported)
            {
                return "error";
            }
            bool color = false, legacy = false;
            for (int i = 0; i < s.passCount; i++)
            {
                string tag = s.FindPassTagValue(i, LightMode).name ?? string.Empty;
                color |= UrpColorTags.Contains(tag);
                legacy |= LegacyTags.Contains(tag);
            }
            return color ? "ok" : legacy ? "error" : "none";
        }
    }
}
