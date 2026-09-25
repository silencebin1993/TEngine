using System;
using UnityEditor;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>ER8-CONTENT-01：`GameRes/Raw/UI/Icons/` 下图标贴图的统一导入设置。
    /// UI 图标按显示尺寸直接采样，不要 mipmap（缩小时发糊）；边缘钳制防止相邻像素渗色；
    /// 保留透明通道。正式图标同名覆盖时自动沿用同一套设置，不用逐张手调。</summary>
    public sealed class IconTextureImportRules : AssetPostprocessor
    {
        public const string IconFolder = "Assets/GameRes/Raw/UI/Icons/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(IconFolder, StringComparison.Ordinal))
            {
                return;
            }
            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
        }
    }
}
