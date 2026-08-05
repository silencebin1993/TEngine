using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.CellArt
{
    /// <summary>单独挂菜单，避免 EditorWindow 子类上的 MenuItem 偶发不被 TypeCache 收录。</summary>
    public static class CellArtMenu
    {
        [MenuItem("BinGames/Cell Art Board %#m", false, 50)]
        [MenuItem("BinGames/美术资源板", false, 51)]
        [MenuItem("Tools/Cell Art Board", false, 2000)]
        public static void OpenBoard()
        {
            CellArtBoardWindow.Open();
        }

        [MenuItem("BinGames/美术资源板", true)]
        [MenuItem("BinGames/Cell Art Board", true)]
        [MenuItem("Tools/Cell Art Board", true)]
        public static bool OpenBoardValidate() => true;

        [InitializeOnLoadMethod]
        static void Warmup()
        {
            // 强制让程序集参与 Editor 启动扫描
        }
    }
}
