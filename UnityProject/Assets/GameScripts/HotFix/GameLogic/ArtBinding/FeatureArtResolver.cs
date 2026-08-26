using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using TEngine;
using UnityEngine;

namespace GameLogic.ArtBinding
{
    /// <summary>story-004：功能美术 catalog 运行时加载/查找入口，只通过 <see cref="GameModule"/>
    /// 资源接口走 YooAsset；缺文件/坏 JSON 一律降级为空表，调用方走白模，不抛异常阻断关卡。</summary>
    public static class FeatureArtResolver
    {
        private const string CatalogLocation = "feature-art-catalog";

        private static readonly Dictionary<string, FeatureArtSlot> _slots = new();
        private static TextAsset _catalogAsset;
        private static bool _loaded;

        public static async UniTask LoadAsync(string packageName = "")
        {
            _slots.Clear();
            _loaded = false;

            TextAsset asset;
            try
            {
                asset = await GameModule.Resource.LoadAssetAsync<TextAsset>(CatalogLocation, packageName: packageName);
            }
            catch (Exception)
            {
                asset = null;
            }

            if (asset == null)
            {
                Log.Error("[FeatureArtResolver] 加载 feature-art-catalog 失败，功能美术全部走白模");
                _loaded = true;
                return;
            }

            _catalogAsset = asset;
            FeatureArtCatalogData data = FeatureArtCatalog.Parse(asset.text);
            foreach (FeatureArtSlot slot in data.slots)
            {
                if (slot == null || string.IsNullOrEmpty(slot.id))
                {
                    continue;
                }

                _slots[slot.id] = slot;
            }

            _loaded = true;
        }

        public static void Unload()
        {
            if (_catalogAsset != null)
            {
                GameModule.Resource.UnloadAsset(_catalogAsset);
                _catalogAsset = null;
            }

            _slots.Clear();
            _loaded = false;
        }

        public static bool IsBound(string id)
        {
            return _slots.TryGetValue(id, out FeatureArtSlot slot) && !string.IsNullOrEmpty(slot.location);
        }

        public static bool TryGetSlot(string id, out FeatureArtSlot slot)
        {
            return _slots.TryGetValue(id, out slot);
        }
    }
}
