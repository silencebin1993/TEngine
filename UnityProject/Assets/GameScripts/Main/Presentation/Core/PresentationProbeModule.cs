// PL-INF-01 → production/design/presentation-track/handoff/PL-INF-01.md
using UnityEngine;

namespace BinGames.Presentation
{
    /// <summary>
    /// 宿主自检探针（功能 ID <see cref="PresentationFeatureRegistry.ProbeId"/>）：启用时在 [PresentationRoot] 下建 64 个方块，每帧移动，停用时全部销毁。
    /// 只在自检 / Lab 里登记进宿主，正式游戏不登记。
    /// </summary>
    public sealed class PresentationProbeModule : IPresentationModule
    {
        public const int CubeCount = 64;

        private readonly Transform[] _cubes = new Transform[CubeCount];
        private float _t;

        public string Id => PresentationFeatureRegistry.ProbeId;

        public void OnEnable()
        {
            Material m = PresentationMaterials.Get(PresentationMaterialUse.Lit, new Color(0.6f, 0.6f, 0.6f));
            for (int i = 0; i < CubeCount; i++)
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.name = "pl_probe_" + i;
                go.GetComponent<MeshRenderer>().sharedMaterial = m;
                go.transform.SetParent(PresentationHost.Root, false);
                _cubes[i] = go.transform;
            }
        }

        public void OnDisable()
        {
            for (int i = 0; i < CubeCount; i++)
            {
                if (_cubes[i] != null)
                {
                    Object.DestroyImmediate(_cubes[i].gameObject);
                }
                _cubes[i] = null;
            }
        }

        public void Tick(in PresentationFrame frame)
        {
            _t += frame.RealDt;
            for (int i = 0; i < CubeCount; i++)
            {
                _cubes[i].localPosition = PresentationSpace.ToRender(new Unity.Mathematics.double3(i % 8, Mathf.Sin(_t + i), i / 8));
            }
        }

        public void Rebuild(RebuildReason reason)
        {
        }
    }
}
