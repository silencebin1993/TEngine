using System;
using System.IO;
using System.Runtime.CompilerServices;
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
        sealed class ReadVersion { public string Json; }
        static readonly ConditionalWeakTable<FeatureArtCatalogData, ReadVersion> ReadVersions = new();

        public static FeatureArtCatalogData Load()
        {
            var json = File.Exists(AbsolutePath) ? File.ReadAllText(AbsolutePath) : null;
            // 编辑器不得把损坏的文件降级为空表后覆盖，运行时仍保留原有白模降级行为。
            var data = json == null ? FeatureArtCatalog.Parse(null) : JsonUtility.FromJson<FeatureArtCatalogData>(json);
            if (data == null || data.slots == null)
                throw new InvalidDataException("绑定表格式无效，原文件未修改。");
            ReadVersions.Add(data, new ReadVersion { Json = json });
            return data;
        }

        public static void Save(FeatureArtCatalogData data)
        {
            var current = File.Exists(AbsolutePath) ? File.ReadAllText(AbsolutePath) : null;
            if (ReadVersions.TryGetValue(data, out var read))
            {
                if (!string.Equals(read.Json, current, StringComparison.Ordinal))
                    throw new IOException("绑定表已被其他程序修改，请刷新后再保存，避免覆盖他人的改动。");
            }
            else if (current != null)
                throw new IOException("绑定表已存在，不能用未加载的数据覆盖，请先刷新。");
            string json = JsonUtility.ToJson(data, true);
            Directory.CreateDirectory(Path.GetDirectoryName(AbsolutePath));
            File.WriteAllText(AbsolutePath, json);
            ReadVersions.Remove(data);
            ReadVersions.Add(data, new ReadVersion { Json = json });
            AssetDatabase.Refresh();
        }
    }
}
