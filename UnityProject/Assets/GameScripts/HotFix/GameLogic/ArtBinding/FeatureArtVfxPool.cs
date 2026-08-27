using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameLogic.MetabolicSlice.ContentCatalog;
using TEngine;
using UnityEngine;

namespace GameLogic.ArtBinding
{
    /// <summary>story-006：VFX Prefab 池——把已绑定的 <c>shape.{Shape}.{role}</c> 槽 Prefab
    /// 用 <see cref="GameModule.Resource"/>.LoadGameObjectAsync 逐实例预建成固定容量池，供
    /// <see cref="GameLogic.Battle.Feedback.WhiteboxComposeProjectileFeedback"/> 取用/归还。
    /// 与 <see cref="FeatureArtVisualBinder"/>（mesh-only 只读工具）不同——这里要的是可 Instantiate
    /// 的完整场景实例，不适合复用那套 Load+读 MeshFilter 的手法。</summary>
    public sealed class FeatureArtVfxPool
    {
        private const int CapacityPerSlot = FxRecipeCatalog.Global.VfxPrefabPoolCapacityPerSlot;

        private readonly Dictionary<string, GameObject[]> _instances = new();
        private readonly Dictionary<string, int> _cursor = new();
        private GameObject _poolContainer;

        public async UniTask LoadAsync(IEnumerable<string> slotIds)
        {
            foreach (string slotId in slotIds)
            {
                if (!FeatureArtResolver.TryGetSlot(slotId, out FeatureArtSlot slot) || string.IsNullOrEmpty(slot.location))
                {
                    continue;
                }

                if (_poolContainer == null)
                {
                    _poolContainer = new GameObject("FeatureArtVfxPool_Container");
                }

                var pooled = new GameObject[CapacityPerSlot];
                bool anyFailed = false;
                for (int i = 0; i < CapacityPerSlot; i++)
                {
                    GameObject go;
                    try
                    {
                        go = await GameModule.Resource.LoadGameObjectAsync(slot.location, _poolContainer.transform);
                    }
                    catch (Exception e)
                    {
                        Log.Error($"[FeatureArtVfxPool] VFX 槽加载失败（{slotId} -> {slot.location}）：{e.Message}，跳过该槽");
                        anyFailed = true;
                        break;
                    }

                    if (go == null)
                    {
                        Log.Error($"[FeatureArtVfxPool] VFX 槽加载失败（{slotId} -> {slot.location}）：返回 null，跳过该槽");
                        anyFailed = true;
                        break;
                    }

                    go.SetActive(false);
                    pooled[i] = go;
                }

                if (anyFailed)
                {
                    for (int i = 0; i < CapacityPerSlot; i++)
                    {
                        SafeDestroy(pooled[i]);
                    }
                    continue;
                }

                _instances[slotId] = pooled;
                _cursor[slotId] = 0;
            }
        }

        public bool IsBound(string slotId)
        {
            return _instances.ContainsKey(slotId);
        }

        /// <summary>轮转取一个池内实例并激活；未绑定返回 null。命中即视为占用（同现网
        /// <see cref="GameLogic.Battle.Feedback.WhiteboxComposeProjectileFeedback"/>._cursor 轮转手法一致，
        /// 允许提前抢占，不额外加占用锁）。</summary>
        public GameObject TryAcquire(string slotId)
        {
            if (!_instances.TryGetValue(slotId, out GameObject[] pooled))
            {
                return null;
            }

            int idx = _cursor[slotId];
            _cursor[slotId] = (idx + 1) % pooled.Length;

            GameObject go = pooled[idx];
            go.SetActive(true);
            return go;
        }

        public void Release(GameObject go)
        {
            if (go == null)
            {
                return;
            }
            go.SetActive(false);
        }

        /// <summary>仅供验收探针使用：某槽的池容量（不是当前活跃数）。未绑定返回 0。</summary>
        public int CountFor(string slotId)
        {
            return _instances.TryGetValue(slotId, out GameObject[] pooled) ? pooled.Length : 0;
        }

        public void Dispose()
        {
            foreach (GameObject[] pooled in _instances.Values)
            {
                for (int i = 0; i < pooled.Length; i++)
                {
                    SafeDestroy(pooled[i]);
                }
            }
            _instances.Clear();
            _cursor.Clear();

            SafeDestroy(_poolContainer);
            _poolContainer = null;
        }

        private static void SafeDestroy(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(obj);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(obj);
            }
        }
    }
}
