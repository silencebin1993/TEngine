using GameLogic.Settings;
using UnityEngine;

namespace GameLogic.Campaign.Feedback
{
    /// <summary>ER8-CONTENT-01 / AC-ACC-003：屏幕震动的唯一来源。
    ///
    /// 震动量（trauma，0～1）只由 <see cref="FeedbackCues"/> 在大事件时注入（重炮、击毁、Boss 阶段、
    /// 核心被毁……每个时刻的强度在 <see cref="FeedbackCueDef.Shake"/>），镜头导演每帧取一次偏移。
    /// 设置“屏幕震动”关闭时不注入、已有的震动立即清零——这些事件本来就同时有声音与字幕条，
    /// 关掉震动仍能判断命中、警报和 Boss 阶段。
    ///
    /// 偏移量 = trauma² × 视野 × 系数，按真实时间衰减；用 Perlin 噪声而不是随机数，画面是“晃”
    /// 而不是“闪跳”，对光敏玩家也更友好。</summary>
    public static class ScreenShake
    {
        /// <summary>每秒衰减的震动量：满震动约 0.6 秒平息。</summary>
        public const float DecayPerSecond = 1.6f;

        /// <summary>满震动时的最大偏移占正交视野半高的比例。</summary>
        public const float MaxOffsetFraction = 0.035f;

        private const float NoiseFrequency = 22f;

        private static float _trauma;
        private static float _noiseTime;

        public static float Trauma => _trauma;

        public static void AddTrauma(float amount)
        {
            if (amount <= 0f || !GameSettings.ScreenShakeEnabled)
            {
                return;
            }
            _trauma = Mathf.Clamp01(_trauma + amount);
        }

        /// <summary>取本帧偏移（相机平面内：右/上方向），并按 <paramref name="dt"/> 衰减。</summary>
        public static Vector3 Sample(float dt, float orthographicSize, Transform camera)
        {
            if (!GameSettings.ScreenShakeEnabled)
            {
                _trauma = 0f;
                return Vector3.zero;
            }
            if (_trauma <= 0f || camera == null)
            {
                return Vector3.zero;
            }

            _noiseTime += dt;
            float amplitude = _trauma * _trauma * orthographicSize * MaxOffsetFraction;
            float nx = Mathf.PerlinNoise(_noiseTime * NoiseFrequency, 0.37f) * 2f - 1f;
            float ny = Mathf.PerlinNoise(0.71f, _noiseTime * NoiseFrequency) * 2f - 1f;
            _trauma = Mathf.Max(0f, _trauma - DecayPerSecond * dt);
            return camera.right * (nx * amplitude) + camera.up * (ny * amplitude);
        }

        /// <summary>测试/切场用：立即清零。</summary>
        public static void Reset()
        {
            _trauma = 0f;
            _noiseTime = 0f;
        }
    }
}
