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

        [InitializeOnLoadMethod]
        static void Warmup()
        {
            // 强制让程序集参与 Editor 启动扫描
        }
    }
}
