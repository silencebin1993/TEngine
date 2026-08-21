using GameLogic.Core;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 白模战斗反馈（story-002 默认实现）。
    ///
    /// 玩家受伤 = 全屏红闪 + 镜头推近脉冲；命中 / 击杀 / 吞噬 = 池化彩色闪光（尺寸与时长按事件区分）。
    /// 敌方受击形变仍由 SimRenderer._Impact 负责；本类补足「一眼能察觉」的层。
    /// 全部运行期程序化创建（不依赖 prefab/UI 资源），局末随 <see cref="Dispose"/> 一起清理。
    /// </summary>
    public sealed class WhiteboxCombatFeedback : ICombatFeedback, System.IDisposable
    {
        // ── 玩家受伤：全屏红闪 + 镜头推近 ──

        private const float HurtDuration = 0.55f;
        private const float HurtHold = 0.08f;
        private const float HurtPeakAlpha = 0.62f;
        private const float HurtFovPunch = 1.8f;
        // story-004：默认相机改透视后，orthoSize 推近对透视相机无效，改按 FOV 收窄；
        // 比例参照 orthoSize 16→推近 1.8（约 11%）换算到默认 FOV 50，非精确值，允许目测微调。
        private const float HurtFovPunchDegrees = 6f;
        private static readonly Color HurtColor = new Color(1f, 0.05f, 0.08f, 1f);

        private GameObject _overlayRoot;
        private Image _overlayImage;
        private float _hurtTimeLeft;
        private float _baseOrthoSize = -1f;
        private float _baseFov = -1f;
        private float _fovPunchLeft;

        // ── 命中 / 击杀 / 吞噬：池化闪光 ──

        private const int PoolSize = 24;
        private const float FlashY = 0.15f;

        private GameObject _poolRoot;
        private Transform[] _flashTf;
        private MeshRenderer[] _flashRenderer;
        private float[] _flashTimeLeft;
        private float[] _flashLife;
        private float[] _flashBaseScale;
        private Color[] _flashColor;
        private int _flashCursor;
        private Mesh _quadMesh;
        private Material _flashMatTemplate;

        // ── 伤害飘字（story-004）──

        private DamageNumberGUI _damageNumbers;

        private void EnsureDamageNumbers()
        {
            if (_damageNumbers != null)
            {
                return;
            }

            var go = new GameObject("CombatFeedback_DamageNumbers");
            _damageNumbers = go.AddComponent<DamageNumberGUI>();
        }

        /// <summary>
        /// <see cref="ICombatFeedback"/> 是纯 C# 类没有 OnGUI 入口，飘字用 IMGUI 世界坐标投影渲染
        /// （与 <see cref="GameLogic.UI.Battle.CellDebugHud"/> 同款 IMGUI 风格，不引入 TMP/prefab 依赖）。
        /// 数据入口仍是 <see cref="WhiteboxCombatFeedback.OnHit"/>，本类只负责池化 + 绘制。
        /// </summary>
        private sealed class DamageNumberGUI : MonoBehaviour
        {
            private const int PoolSize = 24;
            private const float RiseDistance = 1.0f;
            private const float Life = 0.7f;

            private struct Entry
            {
                public Vector3 WorldPos;
                public float Damage;
                public float TimeLeft;
            }

            private readonly Entry[] _entries = new Entry[PoolSize];
            private int _cursor;
            private GUIStyle _style;

            public void Spawn(Vector3 worldPos, float damage)
            {
                int idx = _cursor;
                _cursor = (_cursor + 1) % PoolSize;
                _entries[idx] = new Entry { WorldPos = worldPos, Damage = damage, TimeLeft = Life };
            }

            private void Update()
            {
                float dt = Time.deltaTime;
                for (int i = 0; i < PoolSize; i++)
                {
                    if (_entries[i].TimeLeft > 0f)
                    {
                        _entries[i].TimeLeft -= dt;
                    }
                }
            }

            private void OnGUI()
            {
                Camera cam = Camera.main;
                if (cam == null)
                {
                    return;
                }

                if (_style == null)
                {
                    _style = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                    };
                }

                for (int i = 0; i < PoolSize; i++)
                {
                    if (_entries[i].TimeLeft <= 0f)
                    {
                        continue;
                    }

                    float t = 1f - (_entries[i].TimeLeft / Life);
                    Vector3 pos = _entries[i].WorldPos + Vector3.up * (RiseDistance * t);
                    Vector3 sp = cam.WorldToScreenPoint(pos);
                    if (sp.z <= 0f)
                    {
                        continue;
                    }

                    float alpha = 1f - t * t;
                    _style.normal.textColor = new Color(1f, 0.9f, 0.3f, alpha);
                    var rect = new Rect(sp.x - 40f, Screen.height - sp.y - 20f, 80f, 30f);
                    GUI.Label(rect, _entries[i].Damage.ToString("0"), _style);
                }
            }
        }

        public void OnHit(HitSignal signal)
        {
            // 命中：短促白橙闪光，比击杀小但仍一眼可见（SimRenderer 形变之外再加一笔）
            float scale = math.clamp(1.8f + signal.Damage * 0.04f, 1.8f, 3.5f);
            SpawnFlash(signal.Position, new Color(1f, 0.75f, 0.25f, 1f), scale, 0.22f);

            // story-004：伤害飘字。全仓库此前无同类组件，本类即是「参考风格」的落点——
            // 白模程序化 + 池化，与上面 SpawnFlash 同一命中点，只是多一笔可读数字。
            EnsureDamageNumbers();
            _damageNumbers.Spawn(new Vector3(signal.Position.x, FlashY + 0.6f, signal.Position.y), signal.Damage);
        }

        public void OnPlayerHurt(PlayerHurtSignal signal)
        {
            EnsureOverlay();
            _hurtTimeLeft = HurtDuration;
            // 立刻打到峰值，避免首帧 alpha≈0 被当成「没反馈」
            if (_overlayImage != null)
            {
                _overlayImage.color = new Color(HurtColor.r, HurtColor.g, HurtColor.b, HurtPeakAlpha);
            }

            PunchCamera();
        }

        public void OnKill(KillSignal signal)
        {
            Color c = signal.WasBoss
                ? new Color(1f, 0.35f, 0.2f, 1f)
                : signal.WasElite
                    ? new Color(1f, 0.7f, 0.15f, 1f)
                    : new Color(1f, 0.95f, 0.45f, 1f);
            float scale = signal.WasBoss ? 7f : signal.WasElite ? 5.5f : 4.2f;
            SpawnFlash(signal.Position, c, scale, 0.45f);
        }

        public void OnDevour(DevourSignal signal)
        {
            // 吞噬：青绿大环，与击杀淡黄区分
            float scale = math.clamp(3.5f + signal.TargetVolume * 0.8f, 3.5f, 7f);
            SpawnFlash(signal.Position, new Color(0.35f, 1f, 0.7f, 1f), scale, 0.4f);
        }

        public void Tick(float dt)
        {
            TickHurtOverlay(dt);
            TickCameraPunch(dt);
            TickFlashPool(dt);
        }

        // ── 全屏红闪 ──

        private void EnsureOverlay()
        {
            if (_overlayRoot != null)
            {
                return;
            }

            _overlayRoot = new GameObject("CombatFeedback_HurtOverlay");
            var canvas = _overlayRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // 高于常规 UIWindow，保证受伤反馈不被 HUD 遮挡。
            canvas.sortingOrder = 1000;
            _overlayRoot.AddComponent<CanvasScaler>();

            var imgGo = new GameObject("Flash");
            imgGo.transform.SetParent(_overlayRoot.transform, false);
            _overlayImage = imgGo.AddComponent<Image>();
            _overlayImage.color = new Color(HurtColor.r, HurtColor.g, HurtColor.b, 0f);
            _overlayImage.raycastTarget = false;

            RectTransform rt = _overlayImage.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private void TickHurtOverlay(float dt)
        {
            if (_hurtTimeLeft <= 0f || _overlayImage == null)
            {
                return;
            }

            _hurtTimeLeft = Mathf.Max(0f, _hurtTimeLeft - dt);
            float t = _hurtTimeLeft / HurtDuration;
            // 前 HurtHold 秒保持峰值，之后二次缓出，肉眼更容易抓住
            float holdRatio = HurtHold / HurtDuration;
            float a;
            if (t > 1f - holdRatio)
            {
                a = HurtPeakAlpha;
            }
            else
            {
                float u = t / (1f - holdRatio);
                a = HurtPeakAlpha * (u * u);
            }

            _overlayImage.color = new Color(HurtColor.r, HurtColor.g, HurtColor.b, a);
        }

        // ── 镜头推近（FollowCamera 只改 position，不碰 orthoSize）──

        private void PunchCamera()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            _fovPunchLeft = HurtDuration;
            if (cam.orthographic)
            {
                if (_baseOrthoSize < 0f)
                {
                    _baseOrthoSize = cam.orthographicSize;
                }
                cam.orthographicSize = Mathf.Max(4f, _baseOrthoSize - HurtFovPunch);
            }
            else
            {
                if (_baseFov < 0f)
                {
                    _baseFov = cam.fieldOfView;
                }
                cam.fieldOfView = Mathf.Max(10f, _baseFov - HurtFovPunchDegrees);
            }
        }

        private void TickCameraPunch(float dt)
        {
            if (_fovPunchLeft <= 0f || (_baseOrthoSize < 0f && _baseFov < 0f))
            {
                return;
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                _fovPunchLeft = 0f;
                return;
            }

            _fovPunchLeft = Mathf.Max(0f, _fovPunchLeft - dt);
            float t = 1f - (_fovPunchLeft / HurtDuration);
            // 平滑回到基准视角
            if (cam.orthographic)
            {
                if (_baseOrthoSize < 0f)
                {
                    return;
                }
                cam.orthographicSize = Mathf.Lerp(_baseOrthoSize - HurtFovPunch, _baseOrthoSize, t * t);
                if (_fovPunchLeft <= 0f)
                {
                    cam.orthographicSize = _baseOrthoSize;
                }
            }
            else
            {
                if (_baseFov < 0f)
                {
                    return;
                }
                cam.fieldOfView = Mathf.Lerp(_baseFov - HurtFovPunchDegrees, _baseFov, t * t);
                if (_fovPunchLeft <= 0f)
                {
                    cam.fieldOfView = _baseFov;
                }
            }
        }

        // ── 池化闪光 ──

        private void EnsurePool()
        {
            if (_poolRoot != null)
            {
                return;
            }

            _quadMesh = BuildQuad();
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            _flashMatTemplate = new Material(shader) { color = Color.white };

            _poolRoot = new GameObject("CombatFeedback_FlashPool");
            _flashTf = new Transform[PoolSize];
            _flashRenderer = new MeshRenderer[PoolSize];
            _flashTimeLeft = new float[PoolSize];
            _flashLife = new float[PoolSize];
            _flashBaseScale = new float[PoolSize];
            _flashColor = new Color[PoolSize];

            for (int i = 0; i < PoolSize; i++)
            {
                var go = new GameObject($"Flash_{i}");
                go.transform.SetParent(_poolRoot.transform, false);
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = _quadMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = new Material(_flashMatTemplate);
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.enabled = false;

                _flashTf[i] = go.transform;
                _flashRenderer[i] = mr;
                _flashTimeLeft[i] = 0f;
                _flashLife[i] = 0.2f;
                _flashBaseScale[i] = 1f;
                _flashColor[i] = Color.white;
            }
        }

        private void SpawnFlash(float2 position, Color color, float scale, float life)
        {
            EnsurePool();

            int idx = _flashCursor;
            _flashCursor = (_flashCursor + 1) % PoolSize;

            _flashTf[idx].localPosition = new Vector3(position.x, FlashY, position.y);
            _flashTf[idx].localRotation = Quaternion.identity;
            _flashTf[idx].localScale = Vector3.one * scale * 0.55f;
            _flashRenderer[idx].enabled = true;
            _flashRenderer[idx].sharedMaterial.color = color;
            _flashTimeLeft[idx] = life;
            _flashLife[idx] = life;
            _flashBaseScale[idx] = scale;
            _flashColor[idx] = color;
        }

        private void TickFlashPool(float dt)
        {
            if (_flashTimeLeft == null)
            {
                return;
            }

            for (int i = 0; i < PoolSize; i++)
            {
                if (_flashTimeLeft[i] <= 0f)
                {
                    continue;
                }

                _flashTimeLeft[i] -= dt;
                if (_flashTimeLeft[i] <= 0f)
                {
                    _flashTimeLeft[i] = 0f;
                    _flashRenderer[i].enabled = false;
                    continue;
                }

                float u = 1f - (_flashTimeLeft[i] / _flashLife[i]);
                // 先胀后收 + 透明度衰减
                float s = Mathf.Lerp(0.55f, 1.15f, Mathf.Sin(u * Mathf.PI));
                _flashTf[i].localScale = Vector3.one * (_flashBaseScale[i] * s);

                Color c = _flashColor[i];
                c.a = (1f - u) * (1f - u);
                _flashRenderer[i].sharedMaterial.color = c;
            }
        }

        /// <summary>平面朝上 Quad；旋转由 SpawnFlash 设为俯视可见。</summary>
        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "CombatFeedbackFlashQuad" };
            m.SetVertices(new System.Collections.Generic.List<Vector3>
            {
                new Vector3(-0.5f, 0f, -0.5f),
                new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, -0.5f),
            });
            m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        public void Dispose()
        {
            if (_baseOrthoSize > 0f || _baseFov > 0f)
            {
                Camera cam = Camera.main;
                if (cam != null)
                {
                    if (cam.orthographic && _baseOrthoSize > 0f)
                    {
                        cam.orthographicSize = _baseOrthoSize;
                    }
                    else if (!cam.orthographic && _baseFov > 0f)
                    {
                        cam.fieldOfView = _baseFov;
                    }
                }
            }

            _baseOrthoSize = -1f;
            _baseFov = -1f;
            _fovPunchLeft = 0f;
            _hurtTimeLeft = 0f;

            if (_overlayRoot != null)
            {
                Object.Destroy(_overlayRoot);
                _overlayRoot = null;
                _overlayImage = null;
            }

            if (_poolRoot != null)
            {
                for (int i = 0; i < _flashRenderer.Length; i++)
                {
                    if (_flashRenderer[i] != null)
                    {
                        Object.Destroy(_flashRenderer[i].sharedMaterial);
                    }
                }

                Object.Destroy(_poolRoot);
                _poolRoot = null;
                _flashTf = null;
                _flashRenderer = null;
                _flashTimeLeft = null;
                _flashLife = null;
                _flashBaseScale = null;
                _flashColor = null;
            }

            if (_flashMatTemplate != null)
            {
                Object.Destroy(_flashMatTemplate);
                _flashMatTemplate = null;
            }

            if (_quadMesh != null)
            {
                Object.Destroy(_quadMesh);
                _quadMesh = null;
            }

            if (_damageNumbers != null)
            {
                Object.Destroy(_damageNumbers.gameObject);
                _damageNumbers = null;
            }
        }
    }
}
