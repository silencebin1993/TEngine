using GameLogic.Core;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 培养皿环境液体地面：水体 shader 库驱动。
    /// 涟漪跟随玩家世界位置；支持 Style 切换与 Low/Med/High 质量档。
    /// </summary>
    public sealed class EnvFluidBackground : GameModuleBase
    {
        public enum Quality
        {
            Low = 0,
            Medium = 1,
            High = 2,
        }

        /// <summary>
        /// 水体库风格。前 1 条为既有胞元流体，后 4 条对应 EnvWater/ 下新移植 shader。
        /// </summary>
        public enum Style
        {
            /// <summary>BinGames/SimEnvFluid — Voronoi 胞元流体</summary>
            Cellular = 0,
            /// <summary>BinGames/EnvWater/Displace — 经典位移水面</summary>
            Displace = 1,
            /// <summary>BinGames/EnvWater/Caustics — 焦散多涟漪</summary>
            Caustics = 2,
            /// <summary>BinGames/EnvWater/InkGold — 黑底金纹水彩</summary>
            InkGold = 3,
            /// <summary>BinGames/EnvWater/Waves — 多层波峰泡沫</summary>
            Waves = 4,
        }

        private const string KeywordLow = "ENVFLUID_Q_LOW";
        private const string KeywordMed = "ENVFLUID_Q_MED";
        private const string KeywordHigh = "ENVFLUID_Q_HIGH";

        private static readonly string[] StyleShaderNames =
        {
            "BinGames/SimEnvFluid",
            "BinGames/EnvWater/Displace",
            "BinGames/EnvWater/Caustics",
            "BinGames/EnvWater/InkGold",
            "BinGames/EnvWater/Waves",
        };

        /// <summary>默认 Medium；可在 Enter 前或局内改，下一帧 ApplyQuality。</summary>
        public static Quality CurrentQuality { get; set; } = Quality.Medium;

        /// <summary>默认 Cellular；局内改会重建材质。</summary>
        public static Style CurrentStyle { get; set; } = Style.Cellular;

        public override int Priority => ModulePriority.Presentation;

        private SimBridge _sim;
        private float _arenaHalf = 90f;
        private GameObject _root;
        private MeshRenderer _mr;
        private Material _mat;
        private Quality _appliedQuality = (Quality)(-1);
        private Style _appliedStyle = (Style)(-1);

        public void Bind(SimBridge sim) => _sim = sim;

        /// <summary>在 SetupSim 里调用：按竞技场半宽生成地面。同局重复会先清理。</summary>
        public void Spawn(float arenaHalfExtent)
        {
            DisposeVisual();

            _arenaHalf = Mathf.Max(1f, arenaHalfExtent);
            float size = _arenaHalf * 2.2f;

            if (!TryCreateMaterial(CurrentStyle, out _mat))
            {
                TEngine.Log.Warning("[EnvFluidBackground] 水体库 shader 均不可用，回退 WhiteboxGroundAnchor");
                WhiteboxGroundAnchor.Spawn(_arenaHalf);
                return;
            }

            _root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _root.name = "EnvFluidBackground";
            Object.Destroy(_root.GetComponent<Collider>());

            _root.transform.localScale = new Vector3(size, 1f, size);
            _root.transform.localPosition = new Vector3(0f, -0.5f, 0f);

            ApplySharedProps(_mat);
            ApplyQualityKeywords(_mat, CurrentQuality);
            _appliedQuality = CurrentQuality;
            _appliedStyle = CurrentStyle;

            _mr = _root.GetComponent<MeshRenderer>();
            _mr.sharedMaterial = _mat;
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
        }

        public override void OnUpdate(float dt)
        {
            if (_root == null || _sim == null)
            {
                return;
            }

            if (CurrentStyle != _appliedStyle)
            {
                if (!TrySwapStyle(CurrentStyle))
                {
                    CurrentStyle = _appliedStyle;
                }
            }

            if (_mat == null)
            {
                return;
            }

            if (CurrentQuality != _appliedQuality)
            {
                ApplyQualityKeywords(_mat, CurrentQuality);
                _appliedQuality = CurrentQuality;
            }

            Unity.Mathematics.float2 p = _sim.PlayerPosition;
            _mat.SetVector("_PlayerWorldXZ", new Vector4(p.x, p.y, 0f, 0f));
        }

        public override void OnExit()
        {
            DisposeVisual();
            WhiteboxGroundAnchor.Dispose();
        }

        private bool TrySwapStyle(Style style)
        {
            if (!TryCreateMaterial(style, out Material next))
            {
                TEngine.Log.Warning("[EnvFluidBackground] 切换 Style 失败：" + style);
                return false;
            }

            ApplySharedProps(next);
            ApplyQualityKeywords(next, CurrentQuality);
            _appliedQuality = CurrentQuality;

            if (_mat != null)
            {
                Object.Destroy(_mat);
            }

            _mat = next;
            if (_mr != null)
            {
                _mr.sharedMaterial = _mat;
            }

            _appliedStyle = style;
            return true;
        }

        private void ApplySharedProps(Material mat)
        {
            mat.SetFloat("_ArenaHalf", _arenaHalf);
            mat.SetFloat("_RippleStrength", 1f);
        }

        private static bool TryCreateMaterial(Style style, out Material mat)
        {
            mat = null;
            int index = (int)style;
            if (index < 0 || index >= StyleShaderNames.Length)
            {
                index = 0;
            }

            Shader shader = Shader.Find(StyleShaderNames[index]);
            if (shader == null && index != 0)
            {
                shader = Shader.Find(StyleShaderNames[0]);
            }

            if (shader == null)
            {
                return false;
            }

            mat = new Material(shader);
            return true;
        }

        private void DisposeVisual()
        {
            if (_mat != null)
            {
                Object.Destroy(_mat);
                _mat = null;
            }

            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
            }

            _mr = null;
            _appliedQuality = (Quality)(-1);
            _appliedStyle = (Style)(-1);
        }

        private static void ApplyQualityKeywords(Material mat, Quality q)
        {
            if (mat == null)
            {
                return;
            }

            mat.DisableKeyword(KeywordLow);
            mat.DisableKeyword(KeywordMed);
            mat.DisableKeyword(KeywordHigh);

            switch (q)
            {
                case Quality.Low:
                    mat.EnableKeyword(KeywordLow);
                    break;
                case Quality.High:
                    mat.EnableKeyword(KeywordHigh);
                    break;
                default:
                    mat.EnableKeyword(KeywordMed);
                    break;
            }
        }

        /// <summary>供 execute_code / 调试菜单直调。</summary>
        public static void DebugSetQuality(Quality q) => CurrentQuality = q;

        /// <summary>供 execute_code / 调试菜单直调。</summary>
        public static void DebugSetStyle(Style style) => CurrentStyle = style;

        /// <summary>库内全部 shader 路径（验收/枚举用）。</summary>
        public static string[] DebugAllShaderNames() => (string[])StyleShaderNames.Clone();
    }
}
