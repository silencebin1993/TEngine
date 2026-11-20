// PL-VEH-02 → production/design/presentation-track/handoff/PL-VEH-02.md
using System.Collections.Generic;
using UnityEngine;

namespace BinGames.Presentation
{
    /// <summary>
    /// 挂点查找（art-rules 16 号 §5.2“查表”）：按 <c>skt_*</c> 名或三段键 <c>{挂点ID}/{资产ID}/{零件ID}</c>（底盘自己的节点挂点段为空，如 <c>/mch_player_tracked_base/p_hull</c>）找节点；
    /// 白模没有这些节点时按剪影部件的局部包围盒回退，**只算坐标、不建节点**（给 VFX-01 / 02 用）。
    /// 回退点（剪影局部坐标，车头 = +Z）：skt_ui_top = 顶面中心上 0.10 米；skt_hit = 包围盒中心；skt_fx_exhaust_01 = 车尾上缘中点。
    /// 回退坐标跟着 Rig 的转动（按剪影节点的变换换算到世界）。
    /// 性能：Find / TryResolve 逐个子节点读 name，每次调用都有 GC 分配、O(子节点数)——只在挂上 / 重建时调一次并缓存结果（Transform 或剪影局部坐标），不要每帧逐台调。
    /// </summary>
    public static class RigSockets
    {
        public const string UiTop = "skt_ui_top";
        public const string Hit = "skt_hit";
        public const string ExhaustFirst = "skt_fx_exhaust_01";
        public const float UiTopLift = 0.10f;

        private static readonly List<MeshFilter> Filters = new List<MeshFilter>(16);

        /// <summary>按名或三段键找节点（逐段在上一段节点之下深度优先找同名后代）；找不到返回 null。</summary>
        public static Transform Find(Transform root, string key)
        {
            if (root == null || string.IsNullOrEmpty(key))
            {
                return null;
            }
            if (key.IndexOf('/') < 0)
            {
                return FindDeep(root, key);
            }
            Transform cur = root;
            int start = 0;
            while (start <= key.Length && cur != null)
            {
                int slash = key.IndexOf('/', start);
                int end = slash < 0 ? key.Length : slash;
                if (end > start)
                {
                    cur = FindDeep(cur, key.Substring(start, end - start));
                }
                if (slash < 0)
                {
                    break;
                }
                start = slash + 1;
            }
            return cur == root ? null : cur;
        }

        /// <summary>挂点的世界坐标：有节点用节点；没有且是三个可回退的挂点之一时按 <paramref name="shape"/>（白模剪影节点）下部件的局部包围盒算。</summary>
        public static bool TryResolve(Transform root, Transform shape, string socket, out Vector3 world)
        {
            Transform t = Find(root, socket);
            if (t != null)
            {
                world = t.position;
                return true;
            }
            world = default;
            if (shape == null || !TryLocalBounds(shape, out Bounds b))
            {
                return false;
            }
            Vector3 local;
            switch (socket)
            {
                case UiTop:
                    local = new Vector3(b.center.x, b.max.y + UiTopLift, b.center.z);
                    break;
                case Hit:
                    local = b.center;
                    break;
                case ExhaustFirst:
                    local = new Vector3(b.center.x, b.max.y, b.min.z);
                    break;
                default:
                    return false;
            }
            world = shape.TransformPoint(local);
            return true;
        }

        /// <summary><paramref name="shape"/> 下全部网格在 shape 局部坐标里的包围盒（不受 Rig 转动影响）。</summary>
        public static bool TryLocalBounds(Transform shape, out Bounds bounds)
        {
            bounds = default;
            shape.GetComponentsInChildren(true, Filters);
            bool any = false;
            Matrix4x4 toShape = shape.worldToLocalMatrix;
            foreach (MeshFilter f in Filters)
            {
                Mesh m = f.sharedMesh;
                if (m == null)
                {
                    continue;
                }
                Bounds mb = m.bounds;
                Matrix4x4 mtx = toShape * f.transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    Vector3 p = mtx.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        bounds = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(p);
                    }
                }
            }
            Filters.Clear();
            return any;
        }

        private static Transform FindDeep(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c.name == name)
                {
                    return c;
                }
                Transform d = FindDeep(c, name);
                if (d != null)
                {
                    return d;
                }
            }
            return null;
        }
    }
}
