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
            /// <summary>相对白模单位球（半径 0.5）的缩放倍率。混元 FBX 网格数据常为 ~0.01
            /// 而 FileScale 的 ×100 只在 Transform 上、不会进 <see cref="Mesh.bounds"/>；
            /// Instanced 绘制只取 sharedMesh，必须用 ScaleMul 把可见尺寸拉回白模量级。</summary>
            public readonly float ScaleMul;

            public MeshLoadResult(bool ok, Mesh mesh, Material material, float scaleMul = 1f)
            {
                Ok = ok;
                Mesh = mesh;
                Material = material;
                ScaleMul = scaleMul;
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

        /// <summary>白模单位球半径（<see cref="BinGames.Sim.SimVisualLibrary"/> SphereUnit / BuildSphere），
        /// 用作绑定网格的目标半宽，使 <c>Radius * 2 * ScaleMul</c> 与现网体量一致。</summary>
        public const float UnitMeshRadius = 0.5f;

        /// <summary>加载 location 指向的 Prefab/FBX，抽取 MeshFilter.sharedMesh。
        /// 材质：仅当 sharedMaterial 是本项目 Instanced 着色器（SimBioGlass / SimInstancedUnlit）才带回，
        /// 混元导入的 Standard+PBR 不自动覆盖——否则会丢掉局内 BioGlass 调色，且贴图依赖易在热更路径丢引用。
        /// 异常/null/无 MeshFilter 一律 Log.Error 一次并返回 Ok=false。成功时 prefab 进 track。</summary>
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
            if (renderer != null && renderer.sharedMaterial != null
                && renderer.sharedMaterial.enableInstancing
                && IsSimInstancedShader(renderer.sharedMaterial.shader))
            {
                material = renderer.sharedMaterial;
            }

            float scaleMul = ComputeUnitScaleMul(filter.sharedMesh);
            return new MeshLoadResult(true, filter.sharedMesh, material, scaleMul);
        }

        /// <summary>按网格 bounds 最大半轴换算到 <see cref="UnitMeshRadius"/>；退化网格回退 1。</summary>
        public static float ComputeUnitScaleMul(Mesh mesh)
        {
            if (mesh == null)
            {
                return 1f;
            }

            Vector3 extents = mesh.bounds.extents;
            float maxExtent = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
            if (maxExtent < 1e-5f)
            {
                Log.Warning($"[FeatureArtVisualBinder] 网格 bounds 近零（{mesh.name}），ScaleMul 回退 1");
                return 1f;
            }

            return UnitMeshRadius / maxExtent;
        }

        private static bool IsSimInstancedShader(Shader shader)
        {
            if (shader == null)
            {
                return false;
            }

            string name = shader.name;
            return name == "BinGames/SimBioGlass" || name == "BinGames/SimInstancedUnlit";
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
