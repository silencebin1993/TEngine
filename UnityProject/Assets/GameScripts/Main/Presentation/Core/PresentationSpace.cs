// PL-INF-01 → production/design/presentation-track/handoff/PL-INF-01.md
using Unity.Mathematics;
using UnityEngine;

namespace BinGames.Presentation
{
    /// <summary>
    /// 表现坐标空间：double 精度世界原点 + 版本号。PL 代码把世界坐标交给渲染前一律经 <see cref="ToRender"/>，
    /// 浮动原点（FG15-SYS-02）接入时只改这里的 <see cref="Origin"/>，各模块收到 <see cref="RebuildReason.OriginShift"/> 重建或按 <see cref="Revision"/> 重算。
    /// </summary>
    public static class PresentationSpace
    {
        public static double3 Origin { get; private set; }

        /// <summary>每次设原点 +1。</summary>
        public static int Revision { get; private set; }

        public static void SetOrigin(double3 origin)
        {
            Origin = origin;
            Revision++;
            PresentationHost.Rebuild(RebuildReason.OriginShift);
        }

        public static Vector3 ToRender(double3 world)
        {
            double3 d = world - Origin;
            return new Vector3((float)d.x, (float)d.y, (float)d.z);
        }

        public static double3 FromRender(Vector3 render) => new double3(render.x, render.y, render.z) + Origin;
    }
}
