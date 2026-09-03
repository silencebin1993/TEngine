using System;
using System.Collections.Generic;
using System.IO;
using GameLogic.ArtBinding;
using UnityEditor;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>story-007：只读健康检查，不改 <see cref="FeatureArtCatalogData"/>。
    /// 独立实现路径解析/撞名逻辑，不跨类调用 <see cref="FeatureArtBindingWindow"/> 的 private 方法。</summary>
    public sealed class HealthIssue
    {
        public string SlotId;
        public string Message;
    }

    public static class FeatureArtHealthCheck
    {
        const string RawRoot = "Assets/GameRes/Raw";
        const string RawPrefix = "Assets/GameRes/Raw/";
        const string ArtPrefix = "Assets/GameRes/Art/";

        public static List<HealthIssue> Run(FeatureArtCatalogData data)
        {
            var issues = new List<HealthIssue>();
            if (data?.slots == null)
            {
                return issues;
            }

            foreach (var slot in data.slots)
            {
                if (slot.retired || string.IsNullOrEmpty(slot.location))
                {
                    continue;
                }

                var path = ResolveRawAssetPath(slot.location);
                if (string.IsNullOrEmpty(path))
                {
                    issues.Add(new HealthIssue { SlotId = slot.id, Message = $"location={slot.location} 找不到 Raw 下同名资源" });
                    continue;
                }

                if (!path.StartsWith(RawPrefix, StringComparison.Ordinal))
                {
                    issues.Add(new HealthIssue { SlotId = slot.id, Message = $"location={slot.location} 解出路径 {path} 不在 {RawPrefix} 下" });
                }

                if (HasFilenameConflict(slot.location))
                {
                    issues.Add(new HealthIssue { SlotId = slot.id, Message = $"location={slot.location} 与 Raw 下其它文件重名，撞 AddressByFileName" });
                }

                foreach (var dep in AssetDatabase.GetDependencies(path, true))
                {
                    if (dep == path)
                    {
                        continue;
                    }

                    if (dep.StartsWith(ArtPrefix, StringComparison.Ordinal))
                    {
                        issues.Add(new HealthIssue { SlotId = slot.id, Message = $"依赖指向 Art 源文件：{dep}，不会被热更收集" });
                    }
                    else if (!dep.StartsWith(RawPrefix, StringComparison.Ordinal)
                             && !dep.StartsWith("Packages/", StringComparison.Ordinal)
                             && dep.StartsWith("Assets/", StringComparison.Ordinal))
                    {
                        issues.Add(new HealthIssue { SlotId = slot.id, Message = $"依赖逃出 Raw：{dep}" });
                    }
                }
            }

            return issues;
        }

        static string ResolveRawAssetPath(string location)
        {
            var guids = AssetDatabase.FindAssets(location, new[] { RawRoot });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path))
                {
                    continue;
                }

                if (Path.GetFileNameWithoutExtension(path) == location)
                {
                    return path;
                }
            }

            return null;
        }

        static bool HasFilenameConflict(string location)
        {
            var guids = AssetDatabase.FindAssets("", new[] { RawRoot });
            var count = 0;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path))
                {
                    continue;
                }

                if (Path.GetFileNameWithoutExtension(path) == location)
                {
                    count++;
                }
            }

            return count > 1;
        }
    }
}
