// PL-RP-01 → production/design/presentation-track/handoff/PL-RP-01.md
using UnityEngine;

namespace BinGames.PresentationLab
{
    /// <summary>
    /// PL-RP-01 小实验：主线 3 处 OnRenderObject + GL 叠层（TerrainStream / WhiteboxSquadOverlay / WhiteboxAiHandoffOverlay）在当前管线下还画不画。
    /// 每次回调计数，并在屏幕左下角 64×64 像素画一块纯绿（与它们同一套 GL.LoadPixelMatrix + GL.Begin 画法），采集方读回像素判定。
    /// </summary>
    [ExecuteAlways] // 采集在编辑模式手动渲染：不加它 OnRenderObject 不会被调
    public sealed class RpOnRenderObjectProbe : MonoBehaviour
    {
        public static int Calls;
        /// <summary>GL 用的无光照材质（调用方给；PL 运行时代码不许 Shader.Find，见 PresentationValidate.Materials）。</summary>
        public static Material Mat;
        /// <summary>PL-RP-02：非空时改画这 4 个世界坐标顶点（不压像素矩阵），用来测 URP 下 OnRenderObject 的深度测试。</summary>
        public static Vector3[] WorldQuad;

        private void OnRenderObject()
        {
            Calls++;
            if (Mat == null)
            {
                return;
            }
            Mat.SetPass(0);
            if (WorldQuad != null)
            {
                // PL-RP-02：照 TerrainStream 地块线的画法（世界坐标、不压矩阵、材质 ZTest LessEqual），看 URP 下深度测试是否还生效
                GL.Begin(GL.QUADS);
                GL.Color(Color.green);
                foreach (Vector3 v in WorldQuad)
                {
                    GL.Vertex(v);
                }
                GL.End();
                return;
            }
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            GL.Begin(GL.QUADS);
            GL.Color(Color.green);
            GL.Vertex3(0, 0, 0);
            GL.Vertex3(0, 64, 0);
            GL.Vertex3(64, 64, 0);
            GL.Vertex3(64, 0, 0);
            GL.End();
            GL.PopMatrix();
        }
    }
}
