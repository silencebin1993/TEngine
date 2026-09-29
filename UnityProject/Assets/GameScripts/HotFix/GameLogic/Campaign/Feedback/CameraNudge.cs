using GameLogic.Settings;
using UnityEngine;

namespace GameLogic.Campaign.Feedback
{
    /// <summary>FG2-FW-04（FGR-FW-043）：反应第一次触发时的“镜头轻推”——镜头朝反应发生处轻轻推过去一点再回来。
    ///
    /// - 与 <see cref="ScreenShake"/> 同一套叠加方式：镜头导演每帧先撤掉上一帧叠上去的偏移、按模式算好干净位置，最后叠上本帧偏移（不累积、不改镜头真正的位置与缩放）。
    /// - 偏移 = 朝目标方向的单位向量 × 正交视野半高 × 系数 × 包络（0 → 1 → 0 的正弦半周），按真实时间走（慢放 / 暂停时镜头照常响应）。
    /// - 设置“镜头推动”关闭时不接受新的推动、已有的立即清零（<see cref="GameSettings.ReactionCameraNudgeEnabled"/>）。
    /// 每帧 O(1)。</summary>
    public static class CameraNudge
    {
        private static Vector3 _target;
        private static float _elapsed;
        private static float _duration;
        private static float _fraction;
        private static bool _active;

        /// <summary>本进程累计开始推动的次数（自检断言）。</summary>
        public static int Starts { get; private set; }

        public static bool Active => _active;

        /// <summary>朝世界坐标 <paramref name="worldTarget"/> 推一次（<paramref name="seconds"/> 真实秒、最大偏移 = 视野半高 × <paramref name="fraction"/>）。设置关闭时不推，返回 false。</summary>
        public static bool Begin(Vector3 worldTarget, float seconds, float fraction)
        {
            if (!GameSettings.ReactionCameraNudgeEnabled || !(seconds > 0f) || !(fraction > 0f))
            {
                return false;
            }
            _target = worldTarget;
            _elapsed = 0f;
            _duration = seconds;
            _fraction = Mathf.Min(0.25f, fraction);
            _active = true;
            Starts++;
            return true;
        }

        /// <summary>取本帧偏移（在地面平面内，朝目标方向），并按 <paramref name="dt"/>（真实秒）推进。</summary>
        public static Vector3 Sample(float dt, float orthographicSize, Transform camera)
        {
            if (!_active || camera == null || !GameSettings.ReactionCameraNudgeEnabled)
            {
                _active = false;
                return Vector3.zero;
            }
            _elapsed += Mathf.Max(0f, dt);
            if (_elapsed >= _duration)
            {
                _active = false;
                return Vector3.zero;
            }
            Vector3 dir = OffsetDirection(camera.position, camera.forward, _target);
            float envelope = Mathf.Sin(Mathf.PI * Mathf.Clamp01(_elapsed / _duration));
            return dir * (orthographicSize * _fraction * envelope);
        }

        /// <summary>画面中心（镜头视线与地面 y = 0 的交点）到目标在地面平面上的方向（单位向量；重合时为零）。
        /// 斜视角镜头不能直接用镜头位置：镜头在注视点后上方，直接相减会把方向带偏向镜头前方。</summary>
        public static Vector3 OffsetDirection(Vector3 cameraPosition, Vector3 cameraForward, Vector3 target)
        {
            Vector3 center = cameraPosition;
            if (cameraForward.y < -1e-3f)
            {
                center = cameraPosition + cameraForward * (-cameraPosition.y / cameraForward.y);
            }
            Vector3 d = target - center;
            d.y = 0f;
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.zero;
        }

        public static void Reset()
        {
            _active = false;
            _elapsed = 0f;
        }

        /// <summary>自检用：清零计数。</summary>
        public static void ResetForTests()
        {
            Reset();
            Starts = 0;
        }
    }
}
