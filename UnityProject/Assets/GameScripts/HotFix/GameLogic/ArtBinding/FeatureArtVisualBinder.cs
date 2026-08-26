using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using TEngine;
using UnityEngine;

namespace GameLogic.ArtBinding
{
    /// <summary>story-005：纯加载工具函数，不持有任何 <see cref="GameLogic.Stage.CellStage.CellStageFlow"/>
    /// 状态。Mesh 来源固定走 Prefab：<see cref="GameModule.Resource"/> 的 <c>LoadAssetAsync&lt;GameObject&gt;</c>
    /// 拿到的是未实例化的 Prefab 资源引用，直接 <c>GetComponentInChildren&lt;MeshFilter&gt;().sharedMesh</c>
    /// 读取即可，不 Instantiate 进场景——这不是 resource-api.md 禁止的
    /// 「LoadAssetAsync&lt;GameObject&gt; + Instantiate」反模式（那条禁的是当 LoadGameObjectAsync 替代品
    /// 塞进场景；这里只读数据）。</summary>
    public static class FeatureArtVisualBinder
    {
        /// <summary>敌人 VisualId 族（16 条）。CellStageFlow.BuildVisuals()/ColorFor() 硬编码 switch 盘点得出，
        /// 供本文件运行时覆盖逻辑与 Editor FeatureArtSlotSync 共用同一份数据源，避免两处手抄表格走漂。</summary>
        public static readonly (int VisualId, string Key, string TitleZh)[] EnemyVisualFamilies =
        {
            (1, "vis_1", "浮游食团"),
            (2, "vis_2", "刺膜"),
            (3, "vis_3", "扫尾"),
            (4, "vis_4", "追猎"),
            (5, "vis_5", "噬菌"),
            (6, "vis_6", "硬壳"),
            (7, "vis_7", "导电"),
            (8, "vis_8", "腐败"),
            (9, "vis_9", "游隼"),
            (10, "vis_10", "毒棘"),
            (11, "vis_11", "菌丝"),
            (20, "vis_20", "残块"),
            (50, "vis_50", "精英·一级"),
            (51, "vis_51", "精英·二级"),
            (52, "vis_52", "精英·三级"),
            (90, "vis_90", "首领"),
        };

        /// <summary>召唤物真正生效的 VisualId。来源=CellStageFlow.BuildVisuals() 的 case 13/14/15，
        /// 若该 switch 改动这三个常量要同步改。</summary>
        public const int SummonSporeVisualId = 13;
        public const int SummonPhageVisualId = 14;
        public const int SummonMyceliumVisualId = 15;

        public readonly struct MeshLoadResult
        {
            public readonly bool Ok;
            public readonly Mesh Mesh;
            public readonly Material Material;

            public MeshLoadResult(bool ok, Mesh mesh, Material material)
            {
                Ok = ok;
                Mesh = mesh;
                Material = material;
            }
        }

        public readonly struct MaterialLoadResult
        {
            public readonly bool Ok;
            public readonly Material Material;

            public MaterialLoadResult(bool ok, Material material)
            {
                Ok = ok;
                Material = material;
            }
        }

        /// <summary>加载 location 指向的 Prefab，抽取 MeshFilter.sharedMesh（+ 若自带且支持 Instancing 的
        /// sharedMaterial，否则 Material 为 null 交调用方保留原材质）。异常/null/无 MeshFilter 一律
        /// Log.Error 一次并返回 Ok=false，不抛出、不中断调用方的其它槽处理（Required：错误 location 单槽白模，
        /// 关卡不卡死）。成功时把 Prefab 对象本身 append 进 track，由调用方在 Exit 时统一 UnloadAsset。</summary>
        public static async UniTask<MeshLoadResult> TryLoadInstancedMesh(string location, List<UnityEngine.Object> track)
        {
            if (string.IsNullOrEmpty(location))
            {
                return new MeshLoadResult(false, null, null);
            }

            GameObject prefab;
            try
            {
                prefab = await GameModule.Resource.LoadAssetAsync<GameObject>(location);
            }
            catch (Exception e)
            {
                Log.Error($"[FeatureArtVisualBinder] 槽绑定资源加载失败（{location}）：{e.Message}，回退白模");
                return new MeshLoadResult(false, null, null);
            }

            if (prefab == null)
            {
                Log.Error($"[FeatureArtVisualBinder] 槽绑定资源加载失败（{location}）：返回 null，回退白模");
                return new MeshLoadResult(false, null, null);
            }

            MeshFilter filter = prefab.GetComponentInChildren<MeshFilter>(true);
            if (filter == null || filter.sharedMesh == null)
            {
                Log.Error($"[FeatureArtVisualBinder] 槽绑定资源无 MeshFilter（{location}），回退白模");
                GameModule.Resource.UnloadAsset(prefab);
                return new MeshLoadResult(false, null, null);
            }

            track.Add(prefab);

            Material material = null;
            MeshRenderer renderer = prefab.GetComponentInChildren<MeshRenderer>(true);
            if (renderer != null && renderer.sharedMaterial != null && renderer.sharedMaterial.enableInstancing)
            {
                material = renderer.sharedMaterial;
            }

            return new MeshLoadResult(true, filter.sharedMesh, material);
        }

        /// <summary>加载独立材质覆盖槽。要求 <see cref="Material.enableInstancing"/> 为真，否则视为失败
        /// （Log.Error 一次，调用方保留原材质，不整体判绑定失败）。</summary>
        public static async UniTask<MaterialLoadResult> TryLoadMaterialOverride(string location, List<UnityEngine.Object> track)
        {
            if (string.IsNullOrEmpty(location))
            {
                return new MaterialLoadResult(false, null);
            }

            Material material;
            try
            {
                material = await GameModule.Resource.LoadAssetAsync<Material>(location);
            }
            catch (Exception e)
            {
                Log.Error($"[FeatureArtVisualBinder] 材质槽加载失败（{location}）：{e.Message}，回退原材质");
                return new MaterialLoadResult(false, null);
            }

            if (material == null)
            {
                Log.Error($"[FeatureArtVisualBinder] 材质槽加载失败（{location}）：返回 null，回退原材质");
                return new MaterialLoadResult(false, null);
            }

            if (!material.enableInstancing)
            {
                Log.Error($"[FeatureArtVisualBinder] 材质槽（{location}）未启用 GPU Instancing，回退原材质");
                GameModule.Resource.UnloadAsset(material);
                return new MaterialLoadResult(false, null);
            }

            track.Add(material);
            return new MaterialLoadResult(true, material);
        }
    }
}
