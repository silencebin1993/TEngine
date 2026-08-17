using Unity.Mathematics;

namespace GameLogic.MetabolicSlice.Combat
{
    /// <summary>
    /// story-003：Spin/Orbit 运动轴的纯函数轨迹层，供 <see cref="MetabolicSliceBridge"/> 的
    /// 延迟命中状态机与未来 004 Presenter 共用同一套公式，避免常量分叉。
    /// 公式与常量已在 Preflight D5 锁死，不得改动数值。
    /// </summary>
    public static class ComposeMotionMath
    {
        public const float MotionFlightDuration = 0.3f;
        public const float OrbitAngularSpeed = 5f;
        public const float SpinAngularSpeed = 12f;
        public const float OrbitRadiusPerUnit = 1.4f;
        public const float SpinArmRadius = 0.8f;

        /// <summary>相对发射原点的位移偏移（Orbit 决定半径，Spin 决定自转小臂角速度，二者可叠加）。</summary>
        public static float2 Offset(float phase, float spin, float orbit, float elapsed)
        {
            float orbitAngle = phase + math.sign(orbit == 0f ? 1f : orbit) * OrbitAngularSpeed * elapsed;
            float spinAngle = phase + math.sign(spin == 0f ? 1f : spin) * SpinAngularSpeed * elapsed;
            float orbitDist = OrbitRadiusPerUnit * math.abs(orbit);
            float spinDist = spin != 0f ? SpinArmRadius : 0f;

            float2 orbitOffset = orbitDist * new float2(math.cos(orbitAngle), math.sin(orbitAngle));
            float2 spinOffset = spinDist * new float2(math.cos(spinAngle), math.sin(spinAngle));
            return orbitOffset + spinOffset;
        }
    }
}
