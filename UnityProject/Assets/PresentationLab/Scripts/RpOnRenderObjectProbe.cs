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

        private void OnRenderObject()
        {
            Calls++;
            if (Mat == null)
            {
                return;
            }
            Mat.SetPass(0);
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
