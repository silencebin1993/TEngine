using UnityEditor;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>单独挂菜单，避免 EditorWindow 子类上的 MenuItem 偶发不被 TypeCache 收录
    /// （同 <see cref="BinGames.EditorTools.CellArt.CellArtMenu"/> 先例）。</summary>
    public static class FeatureArtBindingMenu
    {
        [MenuItem("BinGames/功能美术绑定", false, 52)]
        [MenuItem("Tools/Feature Art Binding", false, 2001)]
        public static void Open()
        {
            FeatureArtBindingWindow.Open();
        }

        [InitializeOnLoadMethod]
        static void Warmup()
        {
            // 强制让程序集参与 Editor 启动扫描
        }
    }
}
