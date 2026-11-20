// PL-VEH-02 → production/design/presentation-track/handoff/PL-VEH-02.md
using System;
using UnityEngine;

namespace BinGames.Presentation
{
    /// <summary>机器原型（ChassisCatalog.ResolveArchetype 的四种；解析不出时按轮式）。顺序 = <see cref="VehicleRigProfiles"/> 档位下标。</summary>
    public enum VehicleArchetype : byte
    {
        Wheel,
        Track,
        Hover,
        Fixed,
    }

    /// <summary>
    /// 一种原型的动作参数（档位文件 Presentation/Profiles/VehicleRig.json 的一节；Burst 作业直接读，字段全是 float）。
    /// 俯仰正值 = 车头下沉（前倾），负值 = 车头上抬；侧倾正值 = 向左倾（右转时外侧在左）。
    /// </summary>
    [Serializable]
    public struct VehicleRigProfile
    {
        /// <summary>车身最大转速（度 / 游戏秒）。0 = 不转（固定型）。</summary>
        public float turnRateDeg;
        /// <summary>加速点头：俯仰 = −k·加速度（度 / (米/秒²)），限 ±maxPitchDeg。</summary>
        public float pitchPerAccelDeg;
        public float maxPitchDeg;
        /// <summary>刹车前倾：减速度超过 brakeDecel（米/秒²）的部分按同一 k 前倾，限 brakePitchDeg。</summary>
        public float brakeDecel;
        public float brakePitchDeg;
        /// <summary>俯仰 / 侧倾回零的临界阻尼时间（游戏秒，偏差衰减到约 5%）。</summary>
        public float settleSeconds;
        /// <summary>转弯侧倾：侧倾 = g·角速度(弧度/秒)·速度(米/秒)（度），限 ±maxRollDeg。</summary>
        public float rollGainDeg;
        public float maxRollDeg;
        /// <summary>悬浮浮动：振幅（米）与频率（赫兹，游戏时间）。</summary>
        public float bobMeters;
        public float bobHz;
        /// <summary>俯仰 / 侧倾的转轴高度（相对机器表现原点 = 胶囊中心，米；白模车身中心约 −0.6）。</summary>
        public float pivotY;
        /// <summary>算 steer_angle 用的轴距（米）。</summary>
        public float wheelbase;
        /// <summary>加速度低通的时间常数（游戏秒，滤掉 60Hz 模拟步的台阶）。</summary>
        public float accelSmoothSeconds;
        /// <summary>低于这个速度（米/秒）不按速度方向改朝向（停稳时保持上一朝向，不抖）。</summary>
        public float minTurnSpeed;
        /// <summary>接地面半长 / 半宽（米，车身局部 z / x）：俯仰 / 侧倾时按它抬高，下沉的那一端不插进地面。白模取轮 / 履带 / 悬浮盘的外沿；正式资产按接地点改。</summary>
        public float footHalfLength;
        public float footHalfWidth;
    }

    /// <summary>档位文件的整体（JsonUtility 读写）。</summary>
    [Serializable]
    public sealed class VehicleRigProfileFile
    {
        /// <summary>俯仰 / 侧倾 / 浮动的总幅度倍率（评审包 3 档对比 = <see cref="reviewTiers"/>）。</summary>
        public float amplitude = 1f;
        public float[] reviewTiers = { 0.5f, 1f, 1.6f };
        public VehicleRigProfile wheel;
        public VehicleRigProfile track;
        public VehicleRigProfile hover;
        public VehicleRigProfile @fixed;
    }

    /// <summary>
    /// 当前生效的档位。默认值写在代码里（运行时唯一来源）；仓库里的 VehicleRig.json 与默认值逐字段相同（PresentationValidate.VehRig 断言），
    /// 调参面板（PL-INF-02）读文件后调 <see cref="Apply"/>。
    /// shortcut: 运行时不从磁盘读 JSON（GameScripts 不在资源收集里），PL-INF-02 建档位加载后改成读文件。
    /// </summary>
    public static class VehicleRigProfiles
    {
        public static VehicleRigProfileFile Defaults() => new VehicleRigProfileFile
        {
            amplitude = 1f,
            wheel = Make(240f, 0f, 0f, 0.8f, 0.85f),   // 白模轮心 z ±0.55、半径 0.25；x ±0.8 + 半宽 0.05
            track = Make(180f, 0f, 0f, 0.975f, 1.04f), // 白模履带长 1.95、外沿 x ±1.04
            hover = Make(300f, 0.03f, 0.6f, 0.85f, 0.85f), // 白模悬浮盘半径 0.85
            @fixed = default, // 固定型：不转、不点头、不倾、不浮
        };

        private static VehicleRigProfile Make(float turn, float bob, float bobHz, float footHalfLength, float footHalfWidth) => new VehicleRigProfile
        {
            footHalfLength = footHalfLength,
            footHalfWidth = footHalfWidth,
            turnRateDeg = turn,
            pitchPerAccelDeg = 0.4f,
            maxPitchDeg = 6f,
            brakeDecel = 2f,
            brakePitchDeg = 4f,
            settleSeconds = 0.35f,
            rollGainDeg = 0.3f,
            maxRollDeg = 4f,
            bobMeters = bob,
            bobHz = bobHz,
            pivotY = -0.6f,
            wheelbase = 1.1f,
            accelSmoothSeconds = 0.03f,
            minTurnSpeed = 0.3f,
        };

        public static VehicleRigProfileFile Current { get; private set; } = Defaults();

        /// <summary>每次 <see cref="Apply"/> +1（模块据此重传参数）。</summary>
        public static int Revision { get; private set; }

        public static void Apply(VehicleRigProfileFile file)
        {
            Current = file ?? Defaults();
            Revision++;
        }

        public static VehicleRigProfileFile Parse(string json) => JsonUtility.FromJson<VehicleRigProfileFile>(json);

        public static VehicleRigProfile Of(VehicleRigProfileFile f, VehicleArchetype a) =>
            a == VehicleArchetype.Track ? f.track : a == VehicleArchetype.Hover ? f.hover : a == VehicleArchetype.Fixed ? f.@fixed : f.wheel;
    }
}
