using UnityEngine;

namespace GameLogic.FirstPlayable
{
    /// <summary>阶段控制器统一接口。由 <see cref="FPGame"/> 驱动，不各自挂 MonoBehaviour。</summary>
    public interface IFPStage
    {
        void Enter(FPGame game);
        void Tick(float dt);
        void Exit();
    }

    /// <summary>
    /// First Playable 唯一入口与阶段状态机。
    ///
    /// 设计约束（与 Spec §12 的差异已确认）：
    /// - 独立场景 FirstPlayableDemo.unity 运行，不接 GameApp / Procedure 链，不改主流程。
    /// - 全部内容运行时代码生成，不依赖 YooAsset / HybridCLR / Luban，直接 Play 即可。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FPGame : MonoBehaviour
    {
        public static FPGame Instance { get; private set; }

        public FPRunData Run { get; private set; }
        public Camera Cam { get; private set; }
        public FPStage Stage { get; private set; }

        /// <summary>暂停玩法（微选择弹窗等）。UI 轮询不受影响。</summary>
        public bool Paused { get; set; }

        private IFPStage _current;
        private FPStage _pending;
        private bool _hasPending;

        private Transform _camTarget;
        private Vector3 _camOffset = new Vector3(0f, 30f, 0f);
        private Quaternion _camRot = Quaternion.Euler(90f, 0f, 0f);
        private float _camSize = 16f;
        private float _camFollowLerp = 8f;

        private static readonly float[] SpeedSteps = { 1f, 2f, 4f, 8f };
        private int _speedIndex;

        public float DebugSpeed => SpeedSteps[_speedIndex];

        private void Awake()
        {
            Instance = this;
            // 编辑器失焦时默认会冻结播放循环，导致长时间挂机验证中断。
            // 只在本 demo 运行期间打开，不改 ProjectSettings。
            Application.runInBackground = true;
            Run = new FPRunData();
            SetupCamera();
            SetupLight();
        }

        private void Start()
        {
            SwitchTo(FPStage.None);
        }

        private void SetupCamera()
        {
            Cam = Camera.main;
            if (Cam == null)
            {
                GameObject go = new GameObject("Main Camera", typeof(Camera));
                go.tag = "MainCamera";
                Cam = go.GetComponent<Camera>();
            }
            Cam.orthographic = true;
            Cam.orthographicSize = _camSize;
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = new Color(0.06f, 0.07f, 0.09f);
            Cam.nearClipPlane = 0.1f;
            Cam.farClipPlane = 200f;
            Cam.transform.SetPositionAndRotation(_camOffset, _camRot);
        }

        private void SetupLight()
        {
            Light existing = null;
            Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i].type == LightType.Directional)
                {
                    existing = lights[i];
                    break;
                }
            }

            if (existing == null)
            {
                GameObject go = new GameObject("Directional Light", typeof(Light));
                existing = go.GetComponent<Light>();
                existing.type = LightType.Directional;
            }
            existing.intensity = 1.05f;
            existing.transform.rotation = Quaternion.Euler(52f, -35f, 0f);
        }

        /// <summary>阶段自行配置镜头（细胞阶段俯视大范围，生物阶段拉近）。</summary>
        public void ConfigureCamera(Transform target, float orthoSize, Vector3 offset, Vector3 euler)
        {
            _camTarget = target;
            _camSize = orthoSize;
            _camOffset = offset;
            _camRot = Quaternion.Euler(euler);
            Cam.orthographicSize = orthoSize;
            Cam.transform.rotation = _camRot;
            if (target != null)
            {
                Cam.transform.position = target.position + _camOffset;
            }
        }

        private void Update()
        {
            PollDebugSpeed();

            float dt = Time.deltaTime;
            _current?.Tick(dt);

            if (_hasPending)
            {
                _hasPending = false;
                SwitchTo(_pending);
            }

            // 镜头用非缩放时间，F1 加速时跟随手感不变
            FollowCamera(Time.unscaledDeltaTime);
        }

        private void FollowCamera(float dt)
        {
            if (_camTarget == null)
            {
                return;
            }
            Vector3 want = _camTarget.position + _camOffset;
            Cam.transform.position = Vector3.Lerp(Cam.transform.position, want,
                1f - Mathf.Exp(-_camFollowLerp * dt));
        }

        private void PollDebugSpeed()
        {
            if (!Input.GetKeyDown(KeyCode.F1))
            {
                return;
            }
            _speedIndex = (_speedIndex + 1) % SpeedSteps.Length;
            Time.timeScale = SpeedSteps[_speedIndex];
        }

        /// <summary>
        /// 请求切换阶段。真正切换发生在本帧 Tick 之后，避免阶段在自己的 Tick 里销毁自身。
        /// FPStage.None 表示回主菜单，也是合法请求。
        /// </summary>
        public void GoTo(FPStage stage)
        {
            _pending = stage;
            _hasPending = true;
        }

        private void SwitchTo(FPStage stage)
        {
            _current?.Exit();
            _camTarget = null;
            Stage = stage;

            switch (stage)
            {
                case FPStage.Cell:
                    _current = new FPCellStage();
                    break;
                case FPStage.Build:
                    _current = new FPBuildStage();
                    break;
                case FPStage.Creature:
                    _current = new FPCreatureStage();
                    break;
                case FPStage.Result:
                    _current = new FPResultStage();
                    break;
                default:
                    _current = new FPMenuStage();
                    break;
            }
            Paused = false;
            _current.Enter(this);
        }

        /// <summary>结算界面需要的上下文，由结束阶段写入。</summary>
        public FPResultContext Result { get; set; }

        private void OnDestroy()
        {
            _current?.Exit();
            _current = null;
            Time.timeScale = 1f;
            FPFactory.ReleaseMaterials();
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
