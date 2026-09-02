using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameLogic.ArtBinding;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>story-018（HUNYUAN-3D §4.4）：Project 选中整包目录 → 对槽 + 规范化母带/贴图 →
    /// <see cref="FeatureArtGamePrefabBaker.TryBakePackage"/> → 复用 <see cref="FeatureArtBindingWindow.TryBindCore"/>
    /// 同一套 Raw 前缀 / bindKind 校验（禁止第二套校验逻辑）→ 立刻 <see cref="FeatureArtCatalogIO.Save"/>。
    /// 不经混元 API，不改 catalog schema / Collector / Resolver / Binder / Sim。</summary>
    public static class FeatureArtPackageIngest
    {
        /// <summary>单条整理结果，供菜单汇总对话框展示。</summary>
        public readonly struct Result
        {
            public readonly string PackageDir;
            public readonly bool Ok;
            public readonly string Message;

            public Result(string packageDir, bool ok, string message)
            {
                PackageDir = packageDir;
                Ok = ok;
                Message = message;
            }
        }

        /// <summary>最近一次成功绑定的 Prefab，供菜单收尾 <c>Selection.activeObject</c>。</summary>
        public static GameObject LastBoundPrefab { get; private set; }

        /// <summary>S1 validate：Selection 解析后是否存在至少 1 个合法整包（路径落在
        /// <see cref="FeatureArtGamePrefabBaker.PackageRoots"/> 的直接子文件夹）。不检查是否对得上槽——
        /// 那属于 S3，对不上时按反例走 FAIL 对话框，不在这里灰菜单。</summary>
        public static bool HasValidSelection()
        {
            foreach (var _ in ResolveSelectedPackageDirs())
            {
                return true;
            }

            return false;
        }

        /// <summary>菜单入口：对当前 Selection 里每个合法整包串行跑 S3～S7。</summary>
        public static List<Result> RunOnSelection()
        {
            LastBoundPrefab = null;
            var results = new List<Result>();
            var dirs = ResolveSelectedPackageDirs().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (dirs.Count == 0)
            {
                results.Add(new Result("", false,
                    "未选中任何合法整包（需是 Player/Organ/Summon/Enemy/Projectile 根下的直接子文件夹）。"));
                return results;
            }

            var data = FeatureArtCatalogIO.Load();
            foreach (var dir in dirs)
            {
                results.Add(IngestOne(data, dir));
            }

            return results;
        }

        /// <summary>对外主入口（story-018 指定签名）：单个整包目录整理 + 烘焙 + 绑定 + 存盘。</summary>
        public static bool TryIngestPackage(string packageDir, out string log)
        {
            LastBoundPrefab = null;
            var data = FeatureArtCatalogIO.Load();
            var result = IngestOne(data, packageDir);
            log = result.Message;
            return result.Ok;
        }

        static Result IngestOne(FeatureArtCatalogData data, string selectedDir)
        {
            selectedDir = (selectedDir ?? "").Replace('\\', '/').TrimEnd('/');
            if (!IsDirectChildOfPackageRoot(selectedDir))
            {
                return new Result(selectedDir, false,
                    $"{selectedDir}：不是合法整包（需是 Player/Organ/Summon/Enemy/Projectile 根下的直接子文件夹）。");
            }

            var folderName = Path.GetFileName(selectedDir);
            if (!TryFindSlotByFolderName(data, folderName, out var matched, out var canonical))
            {
                return new Result(selectedDir, false, $"{folderName}：夹名对不上任何槽 canonical。");
            }

            try
            {
                var packageDir = RenameFolderToExactCase(selectedDir, canonical);

                if (!TryNormalizeMother(packageDir, canonical, out var motherErr))
                {
                    return new Result(packageDir, false, $"{canonical}：{motherErr}");
                }

                NormalizeTextures(packageDir, canonical);

                if (!FeatureArtGamePrefabBaker.TryBakePackage(packageDir, canonical, out var prefab, out var bakeErr) ||
                    prefab == null)
                {
                    return new Result(packageDir, false, $"{canonical}：{bakeErr ?? "烘焙失败。"}");
                }

                var before = matched.location;
                var wasOverwrite = !string.IsNullOrEmpty(before) &&
                                   !string.Equals(before, canonical, StringComparison.Ordinal);
                if (!FeatureArtBindingWindow.TryBindCore(matched, prefab, out var bindReason))
                {
                    return new Result(packageDir, false, $"{canonical}：{bindReason}");
                }

                FeatureArtCatalogIO.Save(data);
                LastBoundPrefab = prefab;
                return new Result(packageDir, true,
                    wasOverwrite
                        ? $"{canonical}：已整理并覆盖绑定（原 location={before}）。catalog location={canonical}。"
                        : $"{canonical}：已整理并绑定。catalog location={canonical}。");
            }
            catch (Exception e)
            {
                Debug.LogError("[FeatureArtPackageIngest] " + e);
                return new Result(selectedDir, false, $"{canonical}：{e.Message}");
            }
        }

        /// <summary>S3：夹名（大小写不敏感）反查非 retired 槽，且必须与 <see cref="FeatureArtHunyuanGenerate.TryCanonical"/>
        /// 的 fileName 相等。禁止臆造新槽——找不到就是 FAIL，不猜。</summary>
        static bool TryFindSlotByFolderName(FeatureArtCatalogData data, string folderName,
            out FeatureArtSlot matched, out string canonical)
        {
            matched = null;
            canonical = null;
            foreach (var slot in data?.slots ?? Enumerable.Empty<FeatureArtSlot>())
            {
                if (slot == null || slot.retired)
                {
                    continue;
                }

                if (!FeatureArtHunyuanGenerate.TryCanonical(slot, out _, out var name, out _))
                {
                    continue;
                }

                if (!string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matched = slot;
                canonical = name;
                return true;
            }

            return false;
        }

        /// <summary>S4：磁盘夹名与 canonical 大小写不一致时改成精确 canonical。Windows 文件系统大小写不敏感，
        /// 仅大小写不同的改名要先过渡到临时名，否则 <see cref="AssetDatabase.MoveAsset"/> 会被当无操作。</summary>
        static string RenameFolderToExactCase(string oldDir, string exactName)
        {
            var parent = Path.GetDirectoryName(oldDir)?.Replace('\\', '/');
            var currentName = Path.GetFileName(oldDir);
            if (string.Equals(currentName, exactName, StringComparison.Ordinal))
            {
                return oldDir;
            }

            var targetPath = parent + "/" + exactName;
            if (string.Equals(currentName, exactName, StringComparison.OrdinalIgnoreCase))
            {
                var tempPath = parent + "/__ingest_case_tmp_" + Guid.NewGuid().ToString("N");
                var err1 = AssetDatabase.MoveAsset(oldDir, tempPath);
                if (!string.IsNullOrEmpty(err1))
                {
                    throw new IOException($"改名失败（临时名）：{err1}");
                }

                AssetDatabase.Refresh();
                var err2 = AssetDatabase.MoveAsset(tempPath, targetPath);
                if (!string.IsNullOrEmpty(err2))
                {
                    throw new IOException($"改名失败（{tempPath} → {targetPath}）：{err2}");
                }

                AssetDatabase.Refresh();
                return targetPath;
            }

            var err = AssetDatabase.MoveAsset(oldDir, targetPath);
            if (!string.IsNullOrEmpty(err))
            {
                throw new IOException($"改名失败（{oldDir} → {targetPath}）：{err}");
            }

            AssetDatabase.Refresh();
            return targetPath;
        }

        /// <summary>S5：母带规范化优先级——①已有 <c>{canonical}_src.*</c> ②裸 <c>{canonical}.*</c>
        /// （<see cref="FeatureArtGamePrefabBaker.EnsureMotherUsesSrcSuffix"/>）③恰好 1 个其它 .fbx/.obj
        /// （含哈希名）→ 改名为 <c>{canonical}_src.{ext}</c>。0 个或多于 1 个未归类模型 → FAIL，不猜。</summary>
        static bool TryNormalizeMother(string packageDir, string canonical, out string error)
        {
            error = null;
            var dir = AbsFromAsset(packageDir);
            foreach (var ext in new[] { ".fbx", ".obj" })
            {
                if (File.Exists(Path.Combine(dir, canonical + "_src" + ext)))
                {
                    return true;
                }
            }

            if (FeatureArtGamePrefabBaker.EnsureMotherUsesSrcSuffix(packageDir, canonical))
            {
                return true;
            }

            if (!FeatureArtGamePrefabBaker.TryFindSoleUnknownModel(packageDir, canonical, out var modelAssetPath, out var findErr))
            {
                error = findErr ?? "整包内找不到母带模型（0 个或多于 1 个未归类 .fbx/.obj）。";
                return false;
            }

            var ext2 = Path.GetExtension(modelAssetPath);
            var target = packageDir + "/" + canonical + "_src" + ext2;
            var moveErr = AssetDatabase.MoveAsset(modelAssetPath, target);
            if (!string.IsNullOrEmpty(moveErr))
            {
                error = $"母带改名失败（{modelAssetPath} → {target}）：{moveErr}";
                return false;
            }

            AssetDatabase.Refresh();
            return true;
        }

        /// <summary>S6：包内 .png/.jpg/.tga 若文件名尚未以 <c>{canonical}_</c> 开头则补前缀改名，仅保证
        /// Address 唯一 + 母带预览可 remap。材质阶段锁：禁止把这些贴图赋给 <c>{canonical}_runtime.mat</c>，
        /// 本方法只改名，不touch材质。</summary>
        static void NormalizeTextures(string packageDir, string canonical)
        {
            var dir = AbsFromAsset(packageDir);
            if (!Directory.Exists(dir))
            {
                return;
            }

            var prefix = canonical + "_";
            foreach (var ext in new[] { "*.png", "*.jpg", "*.jpeg", "*.tga" })
            {
                foreach (var path in Directory.GetFiles(dir, ext))
                {
                    var fileName = Path.GetFileName(path);
                    if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var assetPath = packageDir + "/" + fileName;
                    var targetAsset = packageDir + "/" + prefix + fileName;
                    var err = AssetDatabase.MoveAsset(assetPath, targetAsset);
                    if (!string.IsNullOrEmpty(err))
                    {
                        Debug.LogWarning($"[FeatureArtPackageIngest] 贴图改名失败 {assetPath} → {targetAsset}：{err}");
                    }
                }
            }

            AssetDatabase.Refresh();
        }

        /// <summary>S2：合法整包 = 路径落在 <see cref="FeatureArtGamePrefabBaker.PackageRoots"/> 的直接子文件夹。
        /// 根本身与更深子夹都非法。</summary>
        static bool IsDirectChildOfPackageRoot(string dir)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(dir)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent))
            {
                return false;
            }

            foreach (var root in FeatureArtGamePrefabBaker.PackageRoots)
            {
                if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
                {
                    return AssetDatabase.IsValidFolder(dir);
                }
            }

            return false;
        }

        /// <summary>S2：Selection.objects → 资产路径；文件则取父目录；去重前逐条按
        /// <see cref="IsDirectChildOfPackageRoot"/> 过滤，非法项静默跳过（由调用方在结果里报告，
        /// 这里只负责枚举候选，不负责报错文案）。用 <c>Selection.objects</c> 而不是
        /// <c>Selection.assetGUIDs</c>——后者在脚本改选（非真人点击 Project 窗口）时可能落后一帧，
        /// 仍指向旧选中项的父目录。</summary>
        static IEnumerable<string> ResolveSelectedPackageDirs()
        {
            foreach (var obj in Selection.objects)
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                path = path.Replace('\\', '/');
                var dir = AssetDatabase.IsValidFolder(path) ? path : Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                if (IsDirectChildOfPackageRoot(dir))
                {
                    yield return dir;
                }
            }
        }

        static string AbsFromAsset(string assetPath)
        {
            assetPath = (assetPath ?? "").Replace('\\', '/');
            var project = Directory.GetParent(Application.dataPath)?.FullName ?? "";
            return Path.GetFullPath(Path.Combine(project, assetPath));
        }
    }
}
