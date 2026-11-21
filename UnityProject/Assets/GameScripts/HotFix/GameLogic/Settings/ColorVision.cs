using UnityEngine;

namespace GameLogic.Settings
{
    /// <summary>FG15-UX-02（FGR-UX-060“色盲模式：红绿、蓝黄、全色盲三种”）：色觉重映射矩阵。
    /// 红绿 / 蓝黄用“补偿重映射”（daltonize：把该类色觉丢失的那部分颜色差挪到还看得见的通道上），
    /// 全色盲直接转成明暗（只看明暗时游戏也不假装颜色有用：状态区分全靠形状，FGT-UX-007）。
    /// 世界侧写进着色器全局（CombatInstanced 的 cvdRemap），界面侧由 UiKit.uss 的 uk-cvd-* 类换色。</summary>
    public static class ColorVision
    {
        public const string ShaderOnId = "_BgCvdOn";
        public const string ShaderMatrixId = "_BgCvdMatrix";

        // Machado 2009 严重度 1.0 的模拟矩阵（行 = 输出 RGB）。
        private static readonly Matrix4x4 SimDeutan = Rows(0.367322f, 0.860646f, -0.227968f, 0.280085f, 0.672501f, 0.047413f, -0.011820f, 0.042940f, 0.968881f);
        private static readonly Matrix4x4 SimTritan = Rows(1.255528f, -0.076749f, -0.178779f, -0.078411f, 0.930809f, 0.147602f, 0.004733f, 0.691367f, 0.303900f);
        // 误差挪到哪些通道：红绿 → 挪进绿 / 蓝；蓝黄 → 挪进红 / 绿。
        private static readonly Matrix4x4 ShiftRedGreen = Rows(0f, 0f, 0f, 0.7f, 1f, 0f, 0.7f, 0f, 1f);
        private static readonly Matrix4x4 ShiftBlueYellow = Rows(1f, 0f, 0.7f, 0f, 1f, 0.7f, 0f, 0f, 0f);

        /// <summary>该模式的重映射矩阵（3×3 放在 4×4 左上角；关闭 = 单位阵）。</summary>
        public static Matrix4x4 MatrixFor(ColorblindMode mode)
        {
            switch (mode)
            {
                case ColorblindMode.RedGreen:
                    return Daltonize(SimDeutan, ShiftRedGreen);
                case ColorblindMode.BlueYellow:
                    return Daltonize(SimTritan, ShiftBlueYellow);
                case ColorblindMode.Achromat:
                    return Rows(0.299f, 0.587f, 0.114f, 0.299f, 0.587f, 0.114f, 0.299f, 0.587f, 0.114f);
                default:
                    return Matrix4x4.identity;
            }
        }

        /// <summary>该类色觉“看到的”颜色（自检用：比较重映射前后在模拟色觉下的颜色差）。全色盲 = 明暗。</summary>
        public static Color Simulate(Color c, ColorblindMode mode)
        {
            switch (mode)
            {
                case ColorblindMode.RedGreen:
                    return Mul(SimDeutan, c);
                case ColorblindMode.BlueYellow:
                    return Mul(SimTritan, c);
                case ColorblindMode.Achromat:
                    float y = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                    return new Color(y, y, y, c.a);
                default:
                    return c;
            }
        }

        public static Color Apply(Color c, ColorblindMode mode) => mode == ColorblindMode.Off ? c : Clamp(Mul(MatrixFor(mode), c));

        /// <summary>把当前模式写进着色器全局（世界里的头顶图标、物质斑块、效果、敌我色）。</summary>
        public static void ApplyToShaders(ColorblindMode mode)
        {
            Shader.SetGlobalFloat(ShaderOnId, mode == ColorblindMode.Off ? 0f : 1f);
            Shader.SetGlobalMatrix(ShaderMatrixId, MatrixFor(mode));
        }

        // C' = (I + S·(I − Sim))·C
        private static Matrix4x4 Daltonize(Matrix4x4 sim, Matrix4x4 shift)
        {
            Matrix4x4 err = Sub(Matrix4x4.identity, sim);
            Matrix4x4 m = Add(Matrix4x4.identity, shift * err);
            m.m33 = 1f;
            return m;
        }

        private static Matrix4x4 Rows(float a, float b, float c, float d, float e, float f, float g, float h, float i)
        {
            var m = Matrix4x4.identity;
            m.m00 = a; m.m01 = b; m.m02 = c;
            m.m10 = d; m.m11 = e; m.m12 = f;
            m.m20 = g; m.m21 = h; m.m22 = i;
            return m;
        }

        private static Matrix4x4 Sub(Matrix4x4 x, Matrix4x4 y)
        {
            var m = new Matrix4x4();
            for (int k = 0; k < 16; k++)
            {
                m[k] = x[k] - y[k];
            }
            return m;
        }

        private static Matrix4x4 Add(Matrix4x4 x, Matrix4x4 y)
        {
            var m = new Matrix4x4();
            for (int k = 0; k < 16; k++)
            {
                m[k] = x[k] + y[k];
            }
            return m;
        }

        private static Color Mul(Matrix4x4 m, Color c) => new Color(
            m.m00 * c.r + m.m01 * c.g + m.m02 * c.b,
            m.m10 * c.r + m.m11 * c.g + m.m12 * c.b,
            m.m20 * c.r + m.m21 * c.g + m.m22 * c.b, c.a);

        private static Color Clamp(Color c) => new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), c.a);
    }
}
