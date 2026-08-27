using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.ArtBinding
{
    /// <summary>story-002：单个功能美术绑定槽，字段名与 DESIGN §2.2 JSON key 逐一对齐。
    /// 全 public 字段（非属性）以兼容 <see cref="JsonUtility"/>。</summary>
    [Serializable]
    public sealed class FeatureArtSlot
    {
        public string id;
        public string domain;
        public string key;
        public string role;
        public string bindKind;
        public string titleZh;
        public string purpose;
        public string howTo;
        public string expected;
        public string constraints;
        public string look;
        public string prompt;
        public string folderHint;
        public string location;
        public string package;
        public bool retired;
    }

    /// <summary>story-002：catalog 根对象，对应 <c>feature-art-catalog.json</c>。</summary>
    [Serializable]
    public sealed class FeatureArtCatalogData
    {
        public int version;
        public List<FeatureArtSlot> slots;
    }

    /// <summary>story-002：热更层 catalog DTO + 解析入口（DESIGN §2.3：类型放 GameLogic，与
    /// <see cref="GameLogic.MetabolicSlice.ContentCatalog.FxRecipeCatalog"/> 同层）。<see cref="Parse"/>
    /// 永不 throw——坏 JSON / 缺字段一律降级为空表，供 004 runtime-resolver 走白模路径。</summary>
    public static class FeatureArtCatalog
    {
        public static FeatureArtCatalogData Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Empty();
            }

            FeatureArtCatalogData data;
            try
            {
                data = JsonUtility.FromJson<FeatureArtCatalogData>(json);
            }
            catch (Exception)
            {
                return Empty();
            }

            if (data == null)
            {
                return Empty();
            }

            if (data.slots == null)
            {
                data.slots = new List<FeatureArtSlot>();
            }

            return data;
        }

        private static FeatureArtCatalogData Empty()
        {
            return new FeatureArtCatalogData { version = 1, slots = new List<FeatureArtSlot>() };
        }
    }
}
