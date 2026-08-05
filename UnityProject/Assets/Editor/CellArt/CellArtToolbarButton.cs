#if UNITY_6000_3_OR_NEWER

using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace BinGames.EditorTools.CellArt
{
    /// <summary>Unity 6 主工具栏 Cell Art 按钮（独立程序集，不依赖坏掉的 Assembly-CSharp-Editor）。</summary>
    public static class CellArtToolbarButton
    {
        [MainToolbarElement("BinGames/Cell Art Board", defaultDockIndex = -8,
            defaultDockPosition = MainToolbarDockPosition.Middle)]
        static MainToolbarElement Create()
        {
            var icon = EditorGUIUtility.IconContent("d_PreTextureRGB").image as Texture2D
                       ?? EditorGUIUtility.IconContent("Texture Icon").image as Texture2D;
            var content = new MainToolbarContent("Cell Art", icon, "细胞美术资源板");
            return new MainToolbarButton(content, CellArtBoardWindow.Open) { displayed = true };
        }
    }
}

#endif
