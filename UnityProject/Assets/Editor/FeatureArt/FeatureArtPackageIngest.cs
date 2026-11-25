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
                    "未选中任何合法整包（需位于绑定槽的成品输出目录下）。"));
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
                    $"{selectedDir}：不是合法整包（需位于绑定槽的成品输出目录下）。");
            }

            var folderName = Path.GetFileName(selectedDir);
            if (!TryFindSlotByFolderName(data, selectedDir, folderName, out var matched, out var canonical))
            {
                return new Result(selectedDir, false, $"{folderName}：目录和夹名未匹配到唯一的成品槽。");
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
                SyncWorkspaceRequirementStatus(matched.id, canonical);
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
        static bool TryFindSlotByFolderName(FeatureArtCatalogData data, string selectedDir, string folderName,
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

                if (!FeatureArtHunyuanGenerate.TryCanonical(slot, out var folder, out var name, out _))
                {
                    continue;
                }

                if (!string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(FeatureArtHunyuanGenerate.PackageDir(folder, name), selectedDir,
                        StringComparison.OrdinalIgnoreCase)) continue;
                if (matched != null) return false;

                matched = slot;
                canonical = name;
            }

            return matched != null;
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

        /// <summary>命令行批处理入口：解析 <c>-ingestPackages "dir1;dir2"</c>，逐包入库烘焙、打印校验摘要并更新
        /// <c>FeatureArtWorkspace.json</c> 状态为"已绑定"。成功退出 0，失败打印 <c>[INGEST_FAIL]</c> 并退出 1。</summary>
        public static void BatchIngestFromCommandLine()
        {
            var packagesArg = ReadCommandLineValue("-ingestPackages");
            if (string.IsNullOrWhiteSpace(packagesArg))
            {
                Debug.LogError("[INGEST_FAIL] 缺少命令行参数 -ingestPackages。");
                ExitBatch(1);
                return;
            }

            var packages = packagesArg
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Replace('\\', '/').Trim().TrimEnd('/'))
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (packages.Count == 0)
            {
                Debug.LogError("[INGEST_FAIL] -ingestPackages 未包含任何有效整包目录。");
                ExitBatch(1);
                return;
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var failed = false;
            foreach (var pkg in packages)
            {
                if (!TryIngestPackage(pkg, out var log))
                {
                    Debug.LogError($"[INGEST_FAIL] {pkg}：{log}");
                    failed = true;
                    continue;
                }

                if (!ValidateAndLogPackage(pkg, log, out var summary, out var err))
                {
                    Debug.LogError($"[INGEST_FAIL] {pkg}：{err}");
                    failed = true;
                    continue;
                }

                Debug.Log(summary);
            }

            if (failed)
            {
                Debug.LogError("[INGEST_FAIL] BatchIngestFromCommandLine 存在失败项。");
                ExitBatch(1);
                return;
            }

            Debug.Log($"[INGEST_OK] BatchIngestFromCommandLine 全部通过（{packages.Count} 包）。");
            ExitBatch(0);
        }

        static string ReadCommandLineValue(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        static void ExitBatch(int code)
        {
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }

        static bool ValidateAndLogPackage(string requestedPkgDir, string ingestLog, out string summary, out string error)
        {
            summary = null;
            error = null;

            var prefab = LastBoundPrefab;
            if (prefab == null)
            {
                error = "TryIngestPackage 返回成功但 LastBoundPrefab 为空。";
                return false;
            }

            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            var canonical = Path.GetFileNameWithoutExtension(prefabPath);
            var pkgDir = (Path.GetDirectoryName(prefabPath) ?? requestedPkgDir).Replace('\\', '/').TrimEnd('/');

            var mfs = prefab.GetComponentsInChildren<MeshFilter>(true);
            var mrs = prefab.GetComponentsInChildren<MeshRenderer>(true);
            if (mfs.Length != 1 || mrs.Length != 1 || mfs[0].gameObject != prefab || mrs[0].gameObject != prefab)
            {
                error = $"Prefab 根必须保留唯一 MeshFilter({mfs.Length}) + MeshRenderer({mrs.Length})。";
                return false;
            }

            var mesh = mfs[0].sharedMesh;
            var mat = mrs[0].sharedMaterial;
            if (mesh == null || mat == null)
            {
                error = $"Prefab 缺少 sharedMesh({mesh != null}) 或 sharedMaterial({mat != null})。";
                return false;
            }

            var bounds = mesh.bounds;
            var tris = mesh.triangles.Length / 3;
            var sockets = new List<string>();
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
            {
                if (t != prefab.transform && t.name.StartsWith("skt_", StringComparison.OrdinalIgnoreCase))
                {
                    sockets.Add($"{t.name}@{t.localPosition.ToString("F3")}");
                }
            }

            var mainTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            var bumpTex = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
            var emitTex = mat.HasProperty("_EmissionMap") ? mat.GetTexture("_EmissionMap") : null;
            var emitKeyword = mat.IsKeywordEnabled("_EMISSION");

            var bumpImporterType = "None";
            if (bumpTex != null)
            {
                var bumpPath = AssetDatabase.GetAssetPath(bumpTex);
                var ti = AssetImporter.GetAtPath(bumpPath) as TextureImporter;
                bumpImporterType = ti != null ? ti.textureType.ToString() : "MissingImporter";
            }

            var hasDiskAlbedo = false;
            var hasDiskNormal = false;
            var hasDiskEmission = false;
            var absDir = AbsFromAsset(pkgDir);
            var prefix = (canonical + "_").ToLowerInvariant();
            if (Directory.Exists(absDir))
            {
                foreach (var file in Directory.GetFiles(absDir, canonical + "_*.png"))
                {
                    var stem = (Path.GetFileNameWithoutExtension(file) ?? "").ToLowerInvariant();
                    var suffix = stem.StartsWith(prefix, StringComparison.Ordinal)
                        ? stem.Substring(canonical.Length)
                        : stem;
                    var asset = pkgDir + "/" + Path.GetFileName(file);
                    if (suffix.Contains("_normal") || suffix.Contains("_nor"))
                    {
                        hasDiskNormal = true;
                        var ti = AssetImporter.GetAtPath(asset) as TextureImporter;
                        if (ti == null || ti.textureType != TextureImporterType.NormalMap)
                        {
                            error = $"法线贴图 {asset} 的 TextureImporterType={ti?.textureType.ToString() ?? "null"}（期望 NormalMap）。";
                            return false;
                        }
                    }
                    else if (suffix.Contains("_emission") || suffix.Contains("_emissive") || suffix.Contains("_emit"))
                    {
                        hasDiskEmission = true;
                    }
                    else if (!suffix.Contains("_metallic") && !suffix.Contains("_metalness") && !suffix.Contains("_roughness"))
                    {
                        hasDiskAlbedo = true;
                    }
                }
            }

            if (!mat.enableInstancing)
            {
                error = $"材质 {mat.name} 未开启 enableInstancing。";
                return false;
            }

            if (hasDiskAlbedo && mainTex == null)
            {
                error = $"存在漫反射贴图但材质 _MainTex 为空。";
                return false;
            }

            if (hasDiskNormal && (bumpTex == null || bumpImporterType != TextureImporterType.NormalMap.ToString()))
            {
                error = $"存在法线贴图但 _BumpMap={bumpTex != null} NormalImporter={bumpImporterType}。";
                return false;
            }

            if (hasDiskEmission && (emitTex == null || !emitKeyword))
            {
                error = $"存在自发光贴图但 _EmissionMap={emitTex != null} _EMISSION={emitKeyword}。";
                return false;
            }

            var catalog = FeatureArtCatalogIO.Load();
            if (!TryFindSlotByFolderName(catalog, pkgDir, canonical, out var slot, out _))
            {
                error = $"保存后无法从 catalog 重新定位槽位：{pkgDir}";
                return false;
            }

            if (!string.Equals(slot.location, canonical, StringComparison.Ordinal))
            {
                error = $"catalog 槽 {slot.id} location={slot.location}，期望 {canonical}。";
                return false;
            }

            var wsStatus = "NoWorkspaceFile";
            if (File.Exists(FeatureArtWorkspaceStore.AbsolutePath))
            {
                var ws = FeatureArtWorkspaceStore.Load();
                var nodes = FindMatchingWorkspaceNodes(ws, slot.id, canonical);
                if (nodes.Count == 0)
                {
                    wsStatus = "NoMatchedNode";
                }
                else
                {
                    foreach (var n in nodes)
                    {
                        if (!string.Equals(n.requirementStatus, "已绑定", StringComparison.Ordinal))
                        {
                            error = $"Workspace 节点 {n.id} 状态为 {n.requirementStatus}（期望 已绑定）。";
                            return false;
                        }
                    }

                    wsStatus = $"已绑定({nodes.Count})";
                }
            }

            summary =
                $"[INGEST_SUMMARY] pkg={pkgDir} canonical={canonical} slot={slot.id} location={slot.location} " +
                $"tris={tris} sharedMesh.bounds.size=({bounds.size.x:F4}, {bounds.size.y:F4}, {bounds.size.z:F4}) " +
                $"sharedMesh.bounds.min=({bounds.min.x:F4}, {bounds.min.y:F4}, {bounds.min.z:F4}) " +
                $"skt_count={sockets.Count} sockets=[{string.Join(", ", sockets)}] " +
                $"_MainTex={(mainTex != null ? mainTex.name : "null")} " +
                $"_BumpMap={(bumpTex != null ? bumpTex.name : "null")} NormalImporter={bumpImporterType} " +
                $"_EmissionMap={(emitTex != null ? emitTex.name : "null")} _EMISSION={emitKeyword} " +
                $"instancing={mat.enableInstancing} workspace={wsStatus} | {ingestLog}";
            return true;
        }

        static int SyncWorkspaceRequirementStatus(string slotId, string canonical)
        {
            if (!File.Exists(FeatureArtWorkspaceStore.AbsolutePath))
            {
                return 0;
            }

            var ws = FeatureArtWorkspaceStore.Load();
            var matched = FindMatchingWorkspaceNodes(ws, slotId, canonical);
            if (matched.Count == 0)
            {
                return 0;
            }

            var changed = false;
            foreach (var node in matched)
            {
                if (!string.Equals(node.requirementStatus, "已绑定", StringComparison.Ordinal))
                {
                    node.requirementStatus = "已绑定";
                    changed = true;
                }
            }

            if (changed)
            {
                ws.MarkContentChanged();
                FeatureArtWorkspaceStore.Save();
            }

            var reloaded = FeatureArtWorkspaceStore.Load();
            foreach (var node in FindMatchingWorkspaceNodes(reloaded, slotId, canonical))
            {
                if (!string.Equals(node.requirementStatus, "已绑定", StringComparison.Ordinal))
                {
                    throw new IOException($"Reload 检查失败：Workspace 节点 {node.id} requirementStatus={node.requirementStatus}。");
                }
            }

            return matched.Count;
        }

        static List<FeatureArtNode> FindMatchingWorkspaceNodes(FeatureArtWorkspace ws, string slotId, string canonical)
        {
            var list = new List<FeatureArtNode>();
            if (ws?.nodes == null)
            {
                return list;
            }

            foreach (var n in ws.nodes)
            {
                if (n == null)
                {
                    continue;
                }

                if ((!string.IsNullOrEmpty(slotId) && string.Equals(n.slotId, slotId, StringComparison.Ordinal)) ||
                    (!string.IsNullOrEmpty(canonical) && string.Equals(n.id, canonical, StringComparison.Ordinal)))
                {
                    list.Add(n);
                }
            }

            return list;
        }

        static string AbsFromAsset(string assetPath)
        {
            assetPath = (assetPath ?? "").Replace('\\', '/');
            var project = Directory.GetParent(Application.dataPath)?.FullName ?? "";
            return Path.GetFullPath(Path.Combine(project, assetPath));
        }
    }
}
