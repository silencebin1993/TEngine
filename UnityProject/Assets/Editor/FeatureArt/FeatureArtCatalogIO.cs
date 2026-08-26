using System;
using System.IO;
using GameLogic.ArtBinding;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>story-003 R4：读写 feature-art-catalog.json，不新增 GameLogic 层 Serialize 方法——
    /// <see cref="FeatureArtSlot"/>/<see cref="FeatureArtCatalogData"/> 全 public 字段，
    /// Editor 直接 <see cref="JsonUtility"/> 即可。</summary>
    public static class FeatureArtCatalogIO
    {
        public const string RelativePath = "Assets/GameRes/Raw/Configs/ArtBinding/feature-art-catalog.json";

        public static string AbsolutePath =>
            Application.dataPath + "/GameRes/Raw/Configs/ArtBinding/feature-art-catalog.json";

        public static FeatureArtCatalogData Load()
        {
            try
            {
                if (!File.Exists(AbsolutePath))
                {
                    return FeatureArtCatalog.Parse(null);
                }

                string json = File.ReadAllText(AbsolutePath);
                return FeatureArtCatalog.Parse(json);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return FeatureArtCatalog.Parse(null);
            }
        }

        public static void Save(FeatureArtCatalogData data)
        {
            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(AbsolutePath, json);
            AssetDatabase.Refresh();
        }
    }
}
