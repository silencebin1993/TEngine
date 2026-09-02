using System.Text;
using UnityEditor;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>单独挂菜单，避免 EditorWindow 子类上的 MenuItem 偶发不被 TypeCache 收录。</summary>
    public static class FeatureArtBindingMenu
    {
        [MenuItem("BinGames/功能美术绑定", false, 52)]
        [MenuItem("Tools/Feature Art Binding", false, 2001)]
        public static void Open()
        {
            FeatureArtBindingWindow.Open();
        }

        [MenuItem("BinGames/功能美术/从整包重烘全部游戏 Prefab", false, 53)]
        public static void BakeAllGamePrefabs()
        {
            var n = FeatureArtGamePrefabBaker.FixAddressCollisionsAndBake(out var log);
            EditorUtility.DisplayDialog("功能美术", log ?? (n > 0 ? "已重烘。" : "没有可烘的。"), "好");
        }

        /// <summary>story-018（HUNYUAN-3D §4.4）：手拖整包（哈希模型+散贴图，未走混元按钮）选中后
        /// 整理+烘焙+绑定，不经混元 API。</summary>
        [MenuItem("Assets/功能美术/整理选中整包并绑定", false, 30)]
        [MenuItem("BinGames/功能美术/整理选中整包并绑定", false, 54)]
        public static void IngestSelectedPackages()
        {
            var results = FeatureArtPackageIngest.RunOnSelection();
            var sb = new StringBuilder();
            foreach (var r in results)
            {
                sb.AppendLine((r.Ok ? "OK  " : "FAIL ") + r.Message);
            }

            EditorUtility.DisplayDialog("整理选中整包并绑定", sb.ToString(), "好");
            if (FeatureArtPackageIngest.LastBoundPrefab != null)
            {
                Selection.activeObject = FeatureArtPackageIngest.LastBoundPrefab;
            }
        }

        [MenuItem("Assets/功能美术/整理选中整包并绑定", true)]
        [MenuItem("BinGames/功能美术/整理选中整包并绑定", true)]
        public static bool ValidateIngestSelectedPackages()
        {
            return FeatureArtPackageIngest.HasValidSelection();
        }

        [InitializeOnLoadMethod]
        static void Warmup()
        {
            // 强制让程序集参与 Editor 启动扫描
        }
    }
}
