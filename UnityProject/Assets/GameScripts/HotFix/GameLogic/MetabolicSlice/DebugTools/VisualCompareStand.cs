using BinGames.Sim;
using UnityEngine;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// 任务二验收：3D 造型对比台。把 <see cref="SimVisualLibrary.AllArtIds"/> 里全部
    /// 器官/代谢模块/基元/召唤物/Carrier 装配挂件按网格摆开，供 <c>manage_scene screenshot</c>
    /// 同屏肉眼比对"两两可辨"。纯调试可视化，不进 Sim/热更包，同 <see cref="Battle.Feedback.WhiteboxObstacleVisual"/>
    /// 的"一次性生成 + Dispose 统一销毁"模式。
    /// </summary>
    public static class VisualCompareStand
    {
        private const float CellSpacing = 1.6f;
        private const int Columns = 6;
        private const float StandY = 1.2f;

        private static GameObject _root;

        /// <summary>在世界原点附近铺开全部造型；重复调用先清理旧的。返回生成的条目数。</summary>
        public static int Spawn(Vector3 origin)
        {
            Dispose();

            // 不用 SimBioGlass：那是给 Graphics.RenderMeshInstanced + MaterialPropertyBlock 用的
            // GPU Instancing 专用 shader，普通 MeshRenderer 直接挂会读不到逐实例数据，渲染出巨大团块。
            // 纯调试展台按 WhiteboxObstacleVisual 先例用不透明简单 shader。
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            _root = new GameObject("VisualCompareStand_Root");
            _root.transform.position = origin;

            string[] ids = SimVisualLibrary.AllArtIds;
            for (int i = 0; i < ids.Length; i++)
            {
                string artId = ids[i];
                int row = i / Columns;
                int col = i % Columns;
                var pos = new Vector3(col * CellSpacing, StandY, row * CellSpacing);

                var go = new GameObject($"Stand_{artId.Replace('/', '_')}");
                go.transform.SetParent(_root.transform, false);
                go.transform.localPosition = pos;

                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = SimVisualLibrary.BuildForArtId(artId);
                var mr = go.AddComponent<MeshRenderer>();
                var mat = new Material(shader) { color = CategoryColor(artId) };
                if (mat.HasProperty("_BodyAlpha")) { mat.SetFloat("_BodyAlpha", 1f); }
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;

                var labelGo = new GameObject("Label");
                labelGo.transform.SetParent(go.transform, false);
                labelGo.transform.localPosition = new Vector3(0, 0.7f, 0);
                labelGo.transform.localRotation = Quaternion.Euler(45f, 0f, 0f);
                var tm = labelGo.AddComponent<TextMesh>();
                tm.text = artId;
                tm.characterSize = 0.08f;
                tm.fontSize = 48;
                tm.anchor = TextAnchor.LowerCenter;
                tm.color = Color.white;
                var labelMr = labelGo.GetComponent<MeshRenderer>();
                if (labelMr != null) { labelMr.sortingOrder = 10; }
            }

            return ids.Length;
        }

        private static Color CategoryColor(string artId)
        {
            if (artId.StartsWith("org/")) { return new Color(0.55f, 0.92f, 0.68f, 1f); }
            if (artId.StartsWith("prim/energy")) { return new Color(1.00f, 0.92f, 0.35f, 1f); }
            if (artId.StartsWith("prim/mass")) { return new Color(0.72f, 0.72f, 0.78f, 1f); }
            if (artId.StartsWith("prim/light")) { return new Color(0.95f, 0.98f, 1.00f, 1f); }
            if (artId.StartsWith("prim/heat")) { return new Color(1.00f, 0.48f, 0.22f, 1f); }
            if (artId.StartsWith("summon/")) { return new Color(0.78f, 0.62f, 1.00f, 1f); }
            if (artId.StartsWith("carrier/")) { return new Color(0.35f, 0.98f, 0.72f, 1f); }
            return new Color(0.70f, 0.78f, 0.72f, 1f);
        }

        public static bool IsSpawned => _root != null;

        /// <summary>只销毁根节点；每格独立 Material 数量小（≤36）且从不写盘，不值得为它们手动
        /// Object.Destroy——那在编辑器部分场景会触发"Destroying assets is not permitted"报错
        /// （Unity 把运行期 new Material 误判成需要 DestroyImmediate 的持久资源）。随 GameObject
        /// 一起失去引用后交给 Unity 自然回收。</summary>
        public static void Dispose()
        {
            if (_root == null)
            {
                return;
            }

            Object.Destroy(_root);
            _root = null;
        }
    }
}
