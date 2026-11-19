using UnityEngine;

namespace GameLogic.Narrative
{
    /// <summary>
    /// FG13-END-02（FG11 FGR-END-010“镜头转向天空”；FGR-NAR-001“结局演出：游戏内镜头 + 文字”；DEBT-FG9NAR01-01 揭示部分）：演出的游戏内镜头。
    /// - 演出开始（首次播放与资料库重看同一入口 <see cref="NarrativeService"/>.Begin）按条目的镜头（fg.TbNarrativeEntry.camera）<see cref="Begin"/>；结束 / 跳过 / 中止 <see cref="End"/>。
    /// - sky：镜头在 narrative.camera.sky_seconds 真实秒内从原来的角度抬起，转向天空（视线高出地平线 narrative.camera.sky_elevation_deg 度，水平朝向不变）；
    ///   结束后用同样的时长转回原来的角度。时间用真实秒（世界暂停、0.5x～3x 倍速都不影响）。
    /// - 只改镜头的旋转，不改位置与缩放：<see cref="View.CameraDirector"/> 每帧先撤掉上一帧的旋转、按模式算好干净的镜头，再叠上本帧的旋转（与震屏同一套“撤掉再叠”做法，不累积）。
    /// 每帧 O(1)。
    /// </summary>
    public static class CutsceneCamera
    {
        public const string ShotNone = "none";
        public const string ShotSky = "sky";

        private static bool _holding;

        /// <summary>当前（或正在复原的）镜头；没有 = <see cref="ShotNone"/>。</summary>
        public static string Shot { get; private set; } = ShotNone;
        /// <summary>转向天空的程度 0～1（0 = 原来的角度）。</summary>
        public static float Weight { get; private set; }
        /// <summary>本进程开始过几次镜头（自检读）。</summary>
        public static int Begins { get; private set; }

        public static bool Active => Weight > 0f || _holding;

        public static float Seconds => Mathf.Max(0.01f, Core.UiTuningValues.Get("narrative.camera.sky_seconds"));
        public static float ElevationDegrees => Mathf.Clamp(Core.UiTuningValues.Get("narrative.camera.sky_elevation_deg"), 1f, 80f);

        public static void Begin(string shot)
        {
            if (shot != ShotSky)
            {
                return;
            }
            Shot = shot;
            _holding = true;
            Begins++;
        }

        /// <summary>演出结束：开始转回原来的角度（不是瞬间跳回）。</summary>
        public static void End() => _holding = false;

        /// <summary>立即复原（离开世界 / 自检之间）。</summary>
        public static void ResetSession()
        {
            _tiltedCamera = null;
            _holding = false;
            Weight = 0f;
            Shot = ShotNone;
        }

        /// <summary>推进真实时间 <paramref name="realDt"/>，返回本帧的程度（0 = 不改镜头）。</summary>
        public static float Advance(float realDt)
        {
            if (!_holding && Weight <= 0f)
            {
                return 0f;
            }
            float step = Mathf.Max(0f, realDt) / Seconds;
            Weight = Mathf.Clamp01(_holding ? Weight + step : Weight - step);
            if (!_holding && Weight <= 0f)
            {
                Shot = ShotNone;
            }
            return Weight;
        }

        private static Camera _tiltedCamera;
        private static Quaternion _baseRotation;

        /// <summary>镜头导演叠旋转时记下叠之前的旋转（本帧）。</summary>
        public static void NoteTilt(Camera camera, Quaternion baseRotation)
        {
            _tiltedCamera = camera;
            _baseRotation = baseRotation;
        }

        /// <summary>
        /// 审查 P2：镜头“本来”的朝向——演出抬起期间给叠旋转之前的前向，否则给当前前向。
        /// 地面焦点（流式加载、地貌窗口、小地图）用它，演出抬头看天时焦点格不动。
        /// </summary>
        public static Vector3 BaseForward(Camera camera) =>
            camera != null && Active && ReferenceEquals(camera, _tiltedCamera) ? _baseRotation * Vector3.forward : camera != null ? camera.transform.forward : Vector3.down;

        /// <summary>按程度把原来的旋转 <paramref name="baseRotation"/> 抬向天空（缓入缓出）。</summary>
        public static Quaternion Apply(Quaternion baseRotation, float weight)
        {
            if (weight <= 0f)
            {
                return baseRotation;
            }
            Vector3 fwd = baseRotation * Vector3.forward;
            var flat = new Vector3(fwd.x, 0f, fwd.z);
            if (flat.sqrMagnitude < 1e-6f)
            {
                // 正俯视：用镜头的“上方”当水平朝向。
                Vector3 up = baseRotation * Vector3.up;
                flat = new Vector3(up.x, 0f, up.z);
                if (flat.sqrMagnitude < 1e-6f)
                {
                    flat = Vector3.forward;
                }
            }
            flat.Normalize();
            float e = ElevationDegrees * Mathf.Deg2Rad;
            Quaternion sky = Quaternion.LookRotation(flat * Mathf.Cos(e) + Vector3.up * Mathf.Sin(e), Vector3.up);
            return Quaternion.Slerp(baseRotation, sky, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(weight)));
        }
    }
}
