using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>
    /// 混元整包母带（FBX/OBJ）→ 游戏可用 Prefab。
    /// Prefab：单 MeshFilter + MeshRenderer，材质为可换的 <c>{canonical}_runtime.mat</c>
    /// （Unity Built-in Standard + 整包贴图 + GPU Instancing）。禁止改成 SimBioGlass。
    /// 母带 FBX/OBJ 仍留在整包；catalog.location 绑 Prefab（文件名去扩展名仍 = canonical）。
    /// </summary>
    public static class FeatureArtGamePrefabBaker
    {
        public const string RuntimeMatSuffix = "_runtime";
        const string DefaultRuntimeShader = "Standard";
        const string RawPrefix = "Assets/GameRes/Raw/";

        /// <summary>story-018：改 internal 供 <see cref="FeatureArtPackageIngest"/> 判定选中目录是否为合法整包根的直接子文件夹。</summary>
        internal static readonly string[] PackageRoots =
        {
            "Assets/GameRes/Raw/Actor/Player",
            "Assets/GameRes/Raw/Actor/Organ",
            "Assets/GameRes/Raw/Actor/Summon",
            "Assets/GameRes/Raw/Actor/Enemy",
            "Assets/GameRes/Raw/Effects/Projectile",
        };

        /// <summary>在整包内烘焙/覆盖 <c>{canonical}.prefab</c> 与 <c>{canonical}_runtime.mat</c>。</summary>
        public static bool TryBakePackage(string packageDir, string canonical, out GameObject prefab, out string error)
        {
            prefab = null;
            error = null;
            packageDir = (packageDir ?? "").Replace('\\', '/').TrimEnd('/');
            canonical = (canonical ?? "").Trim();
            if (string.IsNullOrEmpty(packageDir) || string.IsNullOrEmpty(canonical))
            {
                error = "packageDir / canonical 为空。";
                return false;
            }

            if (!packageDir.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "整包必须在 Assets/GameRes/Raw/ 下。";
                return false;
            }

            // YooAsset AddressByFileName：{canonical}.fbx 与 {canonical}.prefab 会撞同一 address。
            // 母带必须叫 {canonical}_src.*，成品 Prefab 独占 {canonical}。
            EnsureMotherUsesSrcSuffix(packageDir, canonical);

            if (!TryFindSourceModel(packageDir, canonical, out var modelPath))
            {
                error = $"整包内找不到母带模型（{canonical}_src.fbx/.obj）。";
                return false;
            }

            var modelGo = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var mesh = FindFirstMesh(modelGo);
            if (mesh == null)
            {
                foreach (var a in AssetDatabase.LoadAllAssetsAtPath(modelPath))
                {
                    if (a is Mesh m)
                    {
                        mesh = m;
                        break;
                    }
                }
            }

            if (mesh == null)
            {
                error = $"母带无 Mesh：{modelPath}";
                return false;
            }

            EnsureAssetFolder(packageDir);
            var mat = EnsureRuntimeMaterial(packageDir, canonical, out var matError);
            if (mat == null)
            {
                error = matError ?? "无法创建 runtime 材质。";
                return false;
            }

            var prefabPath = $"{packageDir}/{canonical}.prefab";
            var go = new GameObject(canonical);
            try
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                prefab = PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }

            if (prefab == null)
            {
                error = $"SaveAsPrefabAsset 失败：{prefabPath}";
                return false;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            return prefab != null;
        }

        /// <summary>扫描 Raw 下已知域的整包子目录，凡有母带就重烘 Prefab。不扣积分。</summary>
        public static int BakeAllPackages(out string log)
        {
            var ok = 0;
            var fail = 0;
            var sb = new StringBuilder();
            foreach (var root in PackageRoots)
            {
                var abs = AbsFromAsset(root);
                if (!Directory.Exists(abs))
                {
                    continue;
                }

                foreach (var sub in Directory.GetDirectories(abs))
                {
                    var canonical = Path.GetFileName(sub);
                    if (string.IsNullOrEmpty(canonical) ||
                        canonical.EndsWith(".fbm", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var packageDir = (root + "/" + canonical).Replace('\\', '/');
                    if (!TryFindSourceModel(packageDir, canonical, out _))
                    {
                        continue;
                    }

                    if (TryBakePackage(packageDir, canonical, out _, out var err))
                    {
                        ok++;
                        sb.AppendLine("OK  " + packageDir);
                    }
                    else
                    {
                        fail++;
                        sb.AppendLine("FAIL " + packageDir + " — " + err);
                    }
                }
            }

            log = $"重烘完成：成功 {ok}，失败 {fail}。\n" + sb;
            return ok;
        }

        /// <summary>单包重烘（设置页/器官页按钮）。</summary>
        public static bool TryBakeOne(string packageDir, string canonical, out string log)
        {
            if (TryBakePackage(packageDir, canonical, out var prefab, out var err))
            {
                log = $"已烘焙 Prefab：{AssetDatabase.GetAssetPath(prefab)}（材质 {canonical}{RuntimeMatSuffix}.mat）";
                return true;
            }

            log = err ?? "烘焙失败。";
            return false;
        }

        public static string RuntimeMatAssetPath(string packageDir, string canonical) =>
            $"{packageDir.TrimEnd('/')}/{canonical}{RuntimeMatSuffix}.mat";

        public static string PrefabAssetPath(string packageDir, string canonical) =>
            $"{packageDir.TrimEnd('/')}/{canonical}.prefab";

        /// <summary>母带模型路径（Address 必须是 {canonical}_src，避免与 Prefab 撞名）。</summary>
        public static string MotherModelAssetPath(string packageDir, string canonical, string ext = ".fbx") =>
            $"{packageDir.TrimEnd('/')}/{canonical}_src{ext}";

        /// <summary>若存在与 Prefab 同地址的 <c>{canonical}.fbx/.obj</c>，改名为 <c>{canonical}_src.*</c>。</summary>
        public static bool EnsureMotherUsesSrcSuffix(string packageDir, string canonical)
        {
            packageDir = (packageDir ?? "").Replace('\\', '/').TrimEnd('/');
            canonical = (canonical ?? "").Trim();
            if (string.IsNullOrEmpty(packageDir) || string.IsNullOrEmpty(canonical))
            {
                return false;
            }

            var moved = false;
            foreach (var ext in new[] { ".fbx", ".obj" })
            {
                var bare = $"{packageDir}/{canonical}{ext}";
                var src = $"{packageDir}/{canonical}_src{ext}";
                if (!File.Exists(AbsFromAsset(bare)))
                {
                    continue;
                }

                if (File.Exists(AbsFromAsset(src)))
                {
                    // 已有 _src：删掉会撞 Address 的裸名母带（保留 _src）
                    AssetDatabase.DeleteAsset(bare);
                    moved = true;
                    continue;
                }

                var err = AssetDatabase.MoveAsset(bare, src);
                if (!string.IsNullOrEmpty(err))
                {
                    Debug.LogError($"[FeatureArtGamePrefabBaker] 母带改名失败 {bare} → {src}：{err}");
                    continue;
                }

                moved = true;
            }

            if (moved)
            {
                AssetDatabase.Refresh();
            }

            return moved;
        }

        /// <summary>批处理：所有整包母带改成 _src 后缀并重烘（修 Address 撞名）。</summary>
        public static int FixAddressCollisionsAndBake(out string log)
        {
            var renamed = 0;
            foreach (var root in PackageRoots)
            {
                var abs = AbsFromAsset(root);
                if (!Directory.Exists(abs))
                {
                    continue;
                }

                foreach (var sub in Directory.GetDirectories(abs))
                {
                    var canonical = Path.GetFileName(sub);
                    if (string.IsNullOrEmpty(canonical))
                    {
                        continue;
                    }

                    var packageDir = (root + "/" + canonical).Replace('\\', '/');
                    if (EnsureMotherUsesSrcSuffix(packageDir, canonical))
                    {
                        renamed++;
                    }
                }
            }

            var baked = BakeAllPackages(out var bakeLog);
            log = $"母带改名包数={renamed}；随后重烘：\n{bakeLog}";
            return baked;
        }

        static bool TryFindSourceModel(string packageDir, string canonical, out string modelPath)
        {
            foreach (var candidate in new[]
                     {
                         $"{packageDir}/{canonical}_src.fbx",
                         $"{packageDir}/{canonical}_src.obj",
                         // 兼容未改名旧包；调用方应先 EnsureMotherUsesSrcSuffix
                         $"{packageDir}/{canonical}.fbx",
                         $"{packageDir}/{canonical}.obj",
                     })
            {
                if (File.Exists(AbsFromAsset(candidate)))
                {
                    modelPath = candidate;
                    return true;
                }
            }

            modelPath = null;
            return false;
        }

        /// <summary>story-018 S5③：整包内找“唯一”一个既非 <c>{canonical}.*</c> 也非 <c>{canonical}_src.*</c>
        /// 的 .fbx/.obj（例如人手拖入的哈希文件名，如 180cf87f….obj）。0 个或多于 1 个都返回 false，
        /// 交给调用方判 FAIL，禁止瞎猜改名。</summary>
        public static bool TryFindSoleUnknownModel(string packageDir, string canonical, out string modelAssetPath, out string error)
        {
            modelAssetPath = null;
            error = null;
            packageDir = (packageDir ?? "").Replace('\\', '/').TrimEnd('/');
            canonical = (canonical ?? "").Trim();
            var dir = AbsFromAsset(packageDir);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                error = "整包目录不存在。";
                return false;
            }

            var candidates = new List<string>();
            foreach (var pattern in new[] { "*.fbx", "*.obj" })
            {
                foreach (var path in Directory.GetFiles(dir, pattern))
                {
                    var name = Path.GetFileNameWithoutExtension(path) ?? "";
                    if (string.Equals(name, canonical, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, canonical + "_src", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    candidates.Add(path);
                }
            }

            if (candidates.Count != 1)
            {
                error = candidates.Count == 0 ? "找不到母带模型。" : "多个未归类模型，无法判定唯一母带，不猜。";
                return false;
            }

            modelAssetPath = packageDir + "/" + Path.GetFileName(candidates[0]);
            return true;
        }

        static Material EnsureRuntimeMaterial(string packageDir, string canonical, out string error)
        {
            error = null;
            var matPath = RuntimeMatAssetPath(packageDir, canonical);
            var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (existing != null)
            {
                ApplyDefaultRuntimeLook(existing, packageDir, canonical);
                return existing;
            }

            var shader = Shader.Find(DefaultRuntimeShader);
            if (shader == null)
            {
                error = $"找不到着色器 {DefaultRuntimeShader}。";
                return null;
            }

            var mat = new Material(shader) { name = canonical + RuntimeMatSuffix };
            AssetDatabase.CreateAsset(mat, matPath);
            mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            ApplyDefaultRuntimeLook(mat, packageDir, canonical);
            return mat;
        }

        static void ApplyDefaultRuntimeLook(Material mat, string packageDir, string canonical)
        {
            if (mat == null)
            {
                return;
            }

            var shader = Shader.Find(DefaultRuntimeShader);
            if (shader != null && mat.shader != shader)
            {
                mat.shader = shader;
            }

            mat.color = Color.white;
            mat.enableInstancing = true;
            FeatureArtHunyuanGenerate.PaintPackageStandardMaps(mat, packageDir, canonical);
            EditorUtility.SetDirty(mat);
        }

        static Mesh FindFirstMesh(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            var mf = go.GetComponentInChildren<MeshFilter>(true);
            return mf != null ? mf.sharedMesh : null;
        }

        static void EnsureAssetFolder(string assetFolder)
        {
            assetFolder = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetFolder))
            {
                return;
            }

            var parts = assetFolder.Split('/');
            var cur = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(cur, parts[i]);
                }

                cur = next;
            }
        }

        static string AbsFromAsset(string assetPath)
        {
            assetPath = (assetPath ?? "").Replace('\\', '/');
            if (assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                var project = Directory.GetParent(Application.dataPath)?.FullName ?? "";
                return Path.GetFullPath(Path.Combine(project, assetPath));
            }

            return Path.GetFullPath(assetPath);
        }
    }
}
