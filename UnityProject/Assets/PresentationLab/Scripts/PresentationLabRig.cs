// PL-INF-01 → production/design/presentation-track/handoff/PL-INF-01.md
using BinGames.Presentation;
using Unity.Mathematics;
using UnityEngine;

namespace BinGames.PresentationLab
{
    /// <summary>
    /// PL Lab 台子（只在编辑器里，不进构建）：程序生成 55° 俯视正交相机、1 格 = 1 米参照格、平地 / 坡 / 悬崖 / 水四块合成地面、
    /// 合成载具 / 斑块 / 事件发生器各一个占位。生成物不存进场景（场景文件里只有本组件），关掉组件或销毁台子即销毁。
    /// Play 下每帧用合成帧驱动 <see cref="PresentationHost"/>（Lab 没有战役，不走 GameLogic 的桥）。颜色是中性占位灰，不定画风。
    /// PL-INF-07 时钟测试台：Play 下登记探针模块（重建的接收者），左上角显示本帧时间字段与探针的重建计数 / 地点标记；
    /// 合成载具按 frame.SmoothGameSeconds 来回走（暂停停、倍速 / 慢放变速），用来肉眼看游戏时间。
    /// </summary>
    [ExecuteAlways]
    public sealed class PresentationLabRig : MonoBehaviour
    {
        public const float CameraPitch = 55f;
        public const int GridHalfCells = 16;

        public Camera LabCamera { get; private set; }
        private Transform _content;
        private Mesh _gridMesh;
        private double _gameSeconds;

        [Header("PL-INF-07 时钟测试台（Play 下改）")]
        public bool LabPaused;
        [Tooltip("倍速档，同 GameClock.Speeds：0.5 / 1 / 2 / 3")]
        public float LabSpeed = 1f;
        [Range(0.05f, 1f)] public float LabSlowMotion = 1f;
        private string _labSite = "lab";
        private PresentationProbeModule _probe;
        private Transform _vehicle;
        private string _hud;

        /// <summary>模拟读档：下一帧对全部启用模块 Rebuild(Reload)。</summary>
        [ContextMenu("PL-INF-07/模拟读档")]
        public void SimulateLoad() => PresentationHost.RequestRebuild(RebuildReason.Reload);

        /// <summary>模拟切地点：观察地点在 lab / lab_b 间切换，下一帧 Rebuild(SiteSwitch)。</summary>
        [ContextMenu("PL-INF-07/模拟切地点")]
        public void SimulateSiteSwitch()
        {
            _labSite = _labSite == "lab" ? "lab_b" : "lab";
            PresentationHost.RequestRebuild(RebuildReason.SiteSwitch);
        }

        private void OnEnable()
        {
            if (_content == null)
            {
                Build();
            }
        }

        // 物体被 SetActive(false) 时不清（激活切换期间 DestroyImmediate 会被 Unity 拒绝；子物体随父物体隐藏，重新激活沿用）；
        // 组件关掉、脚本重载、关场景时照常清。
        private void OnDisable()
        {
            StopProbe();
            if (gameObject.activeInHierarchy)
            {
                Clear();
            }
        }

        private void OnDestroy()
        {
            StopProbe();
            Clear();
        }

        private void StopProbe()
        {
            if (_probe == null)
            {
                return;
            }
            PresentationHost.Unregister(_probe);
            PresentationFeatureRegistry.Set(PresentationFeatureRegistry.ProbeId, false);
            _probe = null;
        }

        /// <summary>重建全部生成物（幂等）。</summary>
        public void Build()
        {
            Clear();
            _content = new GameObject("[LabContent]").transform;
            _content.SetParent(transform, false);

            var camGo = new GameObject("LabCamera");
            camGo.transform.SetParent(_content, false);
            LabCamera = camGo.AddComponent<Camera>();
            LabCamera.orthographic = true;
            LabCamera.orthographicSize = 12f;
            LabCamera.clearFlags = CameraClearFlags.SolidColor;
            LabCamera.backgroundColor = new Color(0.18f, 0.18f, 0.2f);
            camGo.transform.rotation = Quaternion.Euler(CameraPitch, 0f, 0f);
            camGo.transform.position = camGo.transform.rotation * new Vector3(0f, 0f, -40f);

            var lightGo = new GameObject("LabSun");
            lightGo.transform.SetParent(_content, false);
            Light sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            BuildGrid();

            Material ground = PresentationMaterials.Get(PresentationMaterialUse.Lit, new Color(0.55f, 0.55f, 0.55f));
            Material water = PresentationMaterials.Get(PresentationMaterialUse.Transparent, new Color(0.45f, 0.5f, 0.55f, 0.6f));
            Material prop = PresentationMaterials.Get(PresentationMaterialUse.Lit, new Color(0.75f, 0.75f, 0.75f));
            Material glow = PresentationMaterials.Get(PresentationMaterialUse.Glow, new Color(0.9f, 0.9f, 0.9f));

            // 四块合成地面，各 8×8 格，沿 X 排开：平地 / 坡（15°）/ 悬崖（高 4 米）/ 水面
            Block("Ground_Flat", PrimitiveType.Cube, new Vector3(-12f, -0.25f, 0f), new Vector3(8f, 0.5f, 8f), Quaternion.identity, ground);
            Block("Ground_Slope", PrimitiveType.Cube, new Vector3(-4f, 0.8f, 0f), new Vector3(8f, 0.5f, 8f), Quaternion.Euler(0f, 0f, 15f), ground);
            Block("Ground_Cliff", PrimitiveType.Cube, new Vector3(4f, 2f, 0f), new Vector3(8f, 4f, 8f), Quaternion.identity, ground);
            Block("Ground_Water", PrimitiveType.Cube, new Vector3(12f, -0.4f, 0f), new Vector3(8f, 0.2f, 8f), Quaternion.identity, water);

            // 占位：合成载具（1.6×0.8×2.4 米箱体）、斑块（直径 3 米圆片）、事件发生器（柱）
            _vehicle = Block("Synth_Vehicle", PrimitiveType.Cube, new Vector3(-12f, 0.4f, 0f), new Vector3(1.6f, 0.8f, 2.4f), Quaternion.identity, prop);
            Block("Synth_Patch", PrimitiveType.Cylinder, new Vector3(-12f, 0.01f, 3f), new Vector3(3f, 0.01f, 3f), Quaternion.identity, glow);
            Block("Synth_EventEmitter", PrimitiveType.Cylinder, new Vector3(-12f, 1f, -3f), new Vector3(0.6f, 1f, 0.6f), Quaternion.identity, prop);

            foreach (Transform t in _content.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.hideFlags = HideFlags.DontSave;
            }
        }

        public void Clear()
        {
            if (_content != null)
            {
                Kill(_content.gameObject);
            }
            if (_gridMesh != null)
            {
                Kill(_gridMesh);
            }
            _content = null;
            _gridMesh = null;
            LabCamera = null;
            _vehicle = null;
        }

        private static void Kill(Object o)
        {
            if (Application.isPlaying)
            {
                Destroy(o);
            }
            else
            {
                DestroyImmediate(o);
            }
        }

        private void Update()
        {
            if (!Application.isPlaying)
            {
                return;
            }
            if (_probe == null)
            {
                PresentationFeatureRegistry.Set(PresentationFeatureRegistry.ProbeId, true);
                _probe = new PresentationProbeModule();
                PresentationHost.Register(_probe);
            }
            bool paused = LabPaused || Time.timeScale == 0f;
            float gameDt = paused ? 0f : Time.unscaledDeltaTime * LabSpeed * LabSlowMotion;
            _gameSeconds += gameDt;
            // 仿 GameClock：60Hz 模拟步，GameSeconds 只在步上跳，SmoothGameSeconds 连续
            long ticks = (long)System.Math.Floor(_gameSeconds * 60.0);
            var frame = new PresentationFrame(Time.unscaledDeltaTime, gameDt, ticks / 60.0, ticks, (float)(_gameSeconds * 60.0 - ticks),
                paused, true, _labSite, LabCamera, LabSpeed, LabSlowMotion, _gameSeconds);
            PresentationHost.Tick(frame);
            if (_vehicle != null)
            {
                _vehicle.localPosition = PresentationSpace.ToRender(new double3(-12.0 + 2.5 * System.Math.Sin(frame.SmoothGameSeconds), 0.4, 0.0));
            }
            _hud = $"暂停 {frame.Paused}  倍速 {frame.Speed}  慢放 {frame.SlowMotion}\nGameDt {frame.GameDt:F4}  RealDt {frame.RealDt:F4}\n" +
                   $"GameSeconds {frame.GameSeconds:F3}  Smooth {frame.SmoothGameSeconds:F3}  StepAlpha {frame.StepAlpha:F2}\n" +
                   $"地点 {frame.ObservedSiteId}  地点标记 {_probe.SiteMarkerId}  Transient {_probe.TransientCount}\n" +
                   $"重建 Reload {_probe.RebuildCount(RebuildReason.Reload)}  SiteSwitch {_probe.RebuildCount(RebuildReason.SiteSwitch)}  " +
                   $"OriginShift {_probe.RebuildCount(RebuildReason.OriginShift)}  Settings {_probe.RebuildCount(RebuildReason.Settings)}";
        }

        private void OnGUI()
        {
            if (Application.isPlaying && _hud != null)
            {
                GUI.Label(new Rect(10f, 10f, 640f, 100f), _hud);
            }
        }

        private Transform Block(string name, PrimitiveType type, Vector3 pos, Vector3 scale, Quaternion rot, Material m)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(_content, false);
            go.transform.localPosition = PresentationSpace.ToRender(new double3(pos.x, pos.y, pos.z));
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = m;
            return go.transform;
        }

        /// <summary>1 格 = 1 米的参照格（线框网格，33×33 条线，中心在原点）。</summary>
        private void BuildGrid()
        {
            int n = GridHalfCells * 2 + 1;
            var verts = new Vector3[n * 4];
            var idx = new int[n * 4];
            for (int i = 0; i < n; i++)
            {
                float v = i - GridHalfCells;
                verts[i * 4 + 0] = new Vector3(v, 0.02f, -GridHalfCells);
                verts[i * 4 + 1] = new Vector3(v, 0.02f, GridHalfCells);
                verts[i * 4 + 2] = new Vector3(-GridHalfCells, 0.02f, v);
                verts[i * 4 + 3] = new Vector3(GridHalfCells, 0.02f, v);
            }
            for (int i = 0; i < idx.Length; i++)
            {
                idx[i] = i;
            }
            var mesh = _gridMesh = new Mesh { name = "LabGrid1m", hideFlags = HideFlags.DontSave };
            mesh.vertices = verts;
            mesh.SetIndices(idx, MeshTopology.Lines, 0);
            var go = new GameObject("Grid_1m");
            go.transform.SetParent(_content, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = PresentationMaterials.Get(PresentationMaterialUse.Unlit, new Color(0.85f, 0.85f, 0.85f, 0.5f));
        }
    }
}
