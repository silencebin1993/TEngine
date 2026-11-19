// PL-QA-01 → production/design/presentation-track/handoff/PL-QA-01.md
using BinGames.Presentation;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// 采集标定靶（功能 <c>pl.capture</c>，只在采集 / 自检里登记，正式游戏不登记）：一队 10 台程序网格白模小履带车（车身 0.8×0.5×0.35 米 + 0.3 米桅杆），
    /// 绕锚点半径 3 米匀速转圈（按 <see cref="PresentationFrame.GameDt"/> 累加，GameDt 恒 0 时冻住）。
    /// 用途：给采集管线一个“有剪影、会动、贴地”的固定被摄体，注入缺陷（品红材质 / 动画冻结 / 下沉 0.5 米）都打在它身上。
    /// 车身矮于 0.5 米是有意的：下沉 0.5 米时车身整体没入地面，剪影只剩桅杆（判定见 pl_metrics.py inject-check）。
    /// </summary>
    [PresentationTime(TimeDomain.Game)]
    public sealed class PresentationCaptureTarget : IPresentationModule
    {
        public const int UnitCount = 10;
        public const float Radius = 3f;
        public const float AngularSpeed = 0.4f; // 弧度 / 游戏秒 = 1.2 米 / 秒
        private static readonly Color Neutral = new Color(0.72f, 0.72f, 0.70f); // 中性占位灰（② 卡不定画风）

        private readonly Vector3 _anchor;
        private readonly Transform[] _units = new Transform[UnitCount];
        private Transform _root;
        private double _angle;

        public PresentationCaptureTarget(Vector3 anchor) => _anchor = anchor;

        public string Id => PresentationFeatureRegistry.CaptureId;

        /// <summary>全部渲染器（采集的目标遮罩与注入都按它）；未启用时为空。</summary>
        public Renderer[] Renderers => _root != null ? _root.GetComponentsInChildren<Renderer>() : new Renderer[0];

        public void OnEnable()
        {
            Material m = PresentationMaterials.Get(PresentationMaterialUse.Lit, Neutral);
            _root = new GameObject("pl_capture_target").transform;
            _root.SetParent(PresentationHost.Root, false);
            _root.position = PresentationSpace.ToRender(new Unity.Mathematics.double3(_anchor.x, _anchor.y, _anchor.z));
            for (int i = 0; i < UnitCount; i++)
            {
                var unit = new GameObject("unit_" + i).transform;
                unit.SetParent(_root, false);
                Box(unit, "body", new Vector3(0f, 0.175f, 0f), new Vector3(0.5f, 0.35f, 0.8f), m);
                Box(unit, "skt_mast", new Vector3(0f, 0.5f, -0.2f), new Vector3(0.08f, 0.3f, 0.08f), m);
                _units[i] = unit;
            }
            Place();
        }

        public void OnDisable()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root.gameObject);
            }
            _root = null;
        }

        public void Tick(in PresentationFrame frame)
        {
            _angle += frame.GameDt * AngularSpeed;
            Place();
        }

        public void Rebuild(RebuildReason reason)
        {
            // 位置全由 _angle 推出，没有派生缓存；锚点固定。
        }

        private void Place()
        {
            for (int i = 0; i < UnitCount; i++)
            {
                float a = (float)(_angle + i * (2 * Mathf.PI / UnitCount));
                _units[i].localPosition = new Vector3(Mathf.Cos(a) * Radius, 0f, Mathf.Sin(a) * Radius);
                _units[i].localRotation = Quaternion.LookRotation(new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a))); // 沿切线前进
            }
        }

        private static void Box(Transform parent, string name, Vector3 pos, Vector3 size, Material m)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = m;
        }
    }
}
