using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG15-PL-01（MERGE-PLAN §5.4 #1～#3）：GPU 探针的颜色阈值是在 Built-in / Gamma 下按读回像素标定的。切 Linear 后，shader 里写死 / 按向量传入的颜色常量
    /// 输出前多一次“线性 → sRGB”编码，读回整体变亮（炮塔 64/217/242 → 137/237/249，MERGE-PLAN §5.5）。这里按 sRGB 解码回“Gamma 模式下同一常量的读回值”再比：
    /// 阈值数值不改、检测力不变（Gamma 下原样返回）。颜色常量本身按 §5.5 只登记不改，常量改了以后这层换算跟着删。
    /// </summary>
    internal static class GpuProbeColor
    {
        public static bool Linear => QualitySettings.activeColorSpace == ColorSpace.Linear;

        public static Color ToGammaEquivalent(Color c) => Linear ? new Color(Mathf.GammaToLinearSpace(c.r), Mathf.GammaToLinearSpace(c.g), Mathf.GammaToLinearSpace(c.b), c.a) : c;

        public static Color32 ToGammaEquivalent(Color32 c) => Linear ? (Color32)ToGammaEquivalent((Color)c) : c;

        /// <summary>报告用：当前色彩空间与换算说明。</summary>
        public static string Note => Linear ? "色彩空间 Linear：读回像素按 sRGB 解码成 Gamma 等价值后比阈值（FG15-PL-01）" : "色彩空间 Gamma：读回像素原样比阈值";
    }
}
