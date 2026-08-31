using System.Collections.Generic;
using GameLogic.ArtBinding;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>
    /// 局内单位走 <c>SimRenderer.RenderMeshInstanced</c>，不是 Hierarchy 里的 GameObject，
    /// Scene 点选不到是正常的。本工具把 catalog 里已绑的 InstancedMesh 临时实例化到场景，
    /// 方便看模/对轴心；对象带 DontSave，不进场景存档、不进热更包。
    /// </summary>
    public static class FeatureArtScenePreview
    {
        public const string RootName = "__FeatureArtPreview";

        [MenuItem("BinGames/功能美术/在场景预览已绑定模型", false, 53)]
        public static void SpawnBoundPreviews()
        {
            ClearPreviews();

            FeatureArtCatalogData data = FeatureArtCatalogIO.Load();
            if (data?.slots == null || data.slots.Count == 0)
            {
                EditorUtility.DisplayDialog("功能美术预览", "catalog 无槽位。", "OK");
                return;
            }

            var root = new GameObject(RootName);
            root.hideFlags = HideFlags.DontSave;
            Undo.RegisterCreatedObjectUndo(root, "FeatureArt preview root");

            int spawned = 0;
            float x = 0f;
            const float spacing = 2.5f;

            for (int i = 0; i < data.slots.Count; i++)
            {
                FeatureArtSlot slot = data.slots[i];
                if (slot == null || slot.retired
                    || string.IsNullOrEmpty(slot.location)
                    || !string.Equals(slot.bindKind, "InstancedMesh", System.StringComparison.Ordinal))
                {
                    continue;
                }

                GameObject source = FindRawGameObject(slot.location);
                if (source == null)
                {
                    Debug.LogWarning($"[FeatureArtPreview] 找不到 location={slot.location}（槽 {slot.id}）");
                    continue;
                }

                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
                if (instance == null)
                {
                    instance = Object.Instantiate(source);
                }

                instance.name = $"{slot.id}__{slot.location}";
                instance.hideFlags = HideFlags.DontSave;
                instance.transform.SetParent(root.transform, false);

                // 与运行时 FeatureArtVisualBinder.ComputeUnitScaleMul 对齐：
                // Instanced 只取 sharedMesh，FileScale×100 不进 bounds；预览要肉眼同尺寸。
                MeshFilter filter = instance.GetComponentInChildren<MeshFilter>(true);
                float scaleMul = 1f;
                if (filter != null && filter.sharedMesh != null)
                {
                    scaleMul = FeatureArtVisualBinder.ComputeUnitScaleMul(filter.sharedMesh);
                }

                instance.transform.localPosition = new Vector3(x, 0f, 0f);
                instance.transform.localRotation = Quaternion.identity;
                // 清掉 FBX 根上的 FileScale，改用与局内一致的 ScaleMul。
                instance.transform.localScale = Vector3.one * scaleMul;

                x += spacing;
                spawned++;
            }

            Selection.activeGameObject = root;
            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.FrameSelected();
            }

            EditorUtility.DisplayDialog(
                "功能美术预览",
                spawned == 0
                    ? "没有已绑定的 InstancedMesh 槽，或 location 在 Raw 里找不到。"
                    : $"已在场景生成 {spawned} 个预览物体（根节点 {RootName}）。\n" +
                      "可在 Hierarchy 点选。这些对象 DontSave，不会写入场景。\n" +
                      "局内对战仍是 GPU Instancing，不会出现这些物体。",
                "OK");
        }

        [MenuItem("BinGames/功能美术/清除场景预览", false, 54)]
        public static void ClearPreviews()
        {
            var found = new List<GameObject>();
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.name != RootName)
                {
                    continue;
                }

                if (EditorUtility.IsPersistent(go))
                {
                    continue;
                }

                found.Add(go);
            }

            for (int i = 0; i < found.Count; i++)
            {
                Object.DestroyImmediate(found[i]);
            }
        }

        /// <summary>AddressByFileName：在 Raw 下按「文件名去扩展名 == location」找 Model/Prefab。</summary>
        static GameObject FindRawGameObject(string location)
        {
            if (string.IsNullOrEmpty(location))
            {
                return null;
            }

            string[] guids = AssetDatabase.FindAssets($"{location} t:GameObject", new[]
            {
                "Assets/GameRes/Raw",
            });

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!string.Equals(fileName, location, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }

            return null;
        }
    }
}
