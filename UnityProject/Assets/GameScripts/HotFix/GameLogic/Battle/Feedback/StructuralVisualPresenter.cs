using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.Core;
using GameLogic.MetabolicSlice.Bag;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// organelle-structural-tier story-003：结构器官复合渲染路径。
    ///
    /// 同 <see cref="CarrierBodyVisualPresenter"/> 骨架——轮询脏版本号（这里是
    /// <c>StructuralSlots.Version</c>，照抄 <see cref="GameLogic.MetabolicSlice.Carrier.CarrierRegistry.AssemblyVersion"/>
    /// 手法），脏了才 Refresh，O(1)。与它的区别：Carrier 本体是整体换 mesh（同一时刻只有一把在用），
    /// 结构器官是 chassis + 最多 4 个独立挂件子物体，全部同时可见（DESIGN §8）。
    ///
    /// 玩家全场只有一个实例，明确不复用 <see cref="GameLogic.Battle.SimRenderer"/> 的
    /// 「每 VisualId 一个 mesh」万级实例化批处理路径（那条路径为敌人设计，单 MeshFilter/零骨骼硬约束）。
    /// v1 无骨骼系统，4 个挂点是固定 local-offset 的空子物体（preflight-decisions R2）。
    /// </summary>
    public sealed class StructuralVisualPresenter : GameModuleBase
    {
        private const string PlaceholderAddress = "Cube";

        private static readonly VisualSlotTag[] AllTags =
        {
            VisualSlotTag.Armor, VisualSlotTag.Motility, VisualSlotTag.Vital, VisualSlotTag.Appendage,
        };

        public override int Priority => ModulePriority.Presentation;

        private SimBridge _sim;
        private int _lastVersion = -1;

        private GameObject _root;
        private readonly Dictionary<VisualSlotTag, Transform> _anchors = new Dictionary<VisualSlotTag, Transform>();
        private readonly Dictionary<VisualSlotTag, string> _cachedPartId = new Dictionary<VisualSlotTag, string>();
        private readonly Dictionary<VisualSlotTag, GameObject> _slotGo = new Dictionary<VisualSlotTag, GameObject>();

        public void Bind(SimBridge sim) => _sim = sim;

        public override void OnEnter()
        {
            _root = new GameObject("StructuralVisualPresenter_Root");
            CreateAnchor(VisualSlotTag.Armor, new Vector3(0f, 0.3f, 0f));
            CreateAnchor(VisualSlotTag.Motility, new Vector3(0f, 0.2f, -0.6f));
            CreateAnchor(VisualSlotTag.Vital, new Vector3(0f, 0.5f, 0f));
            CreateAnchor(VisualSlotTag.Appendage, new Vector3(0.6f, 0.2f, 0f));
        }

        private void CreateAnchor(VisualSlotTag tag, Vector3 localOffset)
        {
            var go = new GameObject("Anchor_" + tag);
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = localOffset;
            _anchors[tag] = go.transform;
        }

        public override void OnUpdate(float dt)
        {
            if (_sim == null || _root == null)
            {
                return;
            }

            float2 p = _sim.PlayerPosition;
            _root.transform.position = new Vector3(p.x, 0f, p.y);

            var slots = GameLogic.UI.Battle.MetabolicSlicePanel.Instance?.Structural;
            if (slots != null && slots.Version != _lastVersion)
            {
                Refresh(slots);
            }
        }

        private void Refresh(GameLogic.MetabolicSlice.Structural.StructuralSlots slots)
        {
            _lastVersion = slots.Version;

            for (int i = 0; i < AllTags.Length; i++)
            {
                VisualSlotTag tag = AllTags[i];
                var part = slots.Get(tag);
                string newPartId = part?.PartId;
                _cachedPartId.TryGetValue(tag, out string oldPartId);
                if (newPartId == oldPartId)
                {
                    continue;
                }

                if (_slotGo.TryGetValue(tag, out GameObject oldGo) && oldGo != null)
                {
                    UnityEngine.Object.Destroy(oldGo);
                }
                _slotGo.Remove(tag);
                _cachedPartId[tag] = newPartId;

                if (newPartId != null)
                {
                    LoadSlotAsync(tag, newPartId).Forget();
                }
            }
        }

        private async UniTaskVoid LoadSlotAsync(VisualSlotTag tag, string partId)
        {
            if (!_anchors.TryGetValue(tag, out Transform anchor) || anchor == null)
            {
                return;
            }

            GameObject go = await GameModule.Resource.LoadGameObjectAsync(PlaceholderAddress, anchor);

            // 加载期间该槽可能又变了（快速替换/卸下）：只在仍是这次请求对应的最新 partId 时才落地，
            // 否则丢弃，避免叠加成脏实例。
            if (go == null)
            {
                return;
            }
            if (!_cachedPartId.TryGetValue(tag, out string currentExpected) || currentExpected != partId)
            {
                UnityEngine.Object.Destroy(go);
                return;
            }

            _slotGo[tag] = go;
        }

        /// <summary>供 execute_code 反射/直调断言用（003 验收口径，见 preflight-decisions R6）——
        /// 当前 4 个 Anchor 下的活跃挂件数量。</summary>
        internal int DebugActiveSlotCount()
        {
            int count = 0;
            foreach (var kv in _slotGo)
            {
                if (kv.Value != null)
                {
                    count++;
                }
            }
            return count;
        }

        public override void OnExit()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
            }

            _anchors.Clear();
            _cachedPartId.Clear();
            _slotGo.Clear();
            _lastVersion = -1;
        }
    }
}
