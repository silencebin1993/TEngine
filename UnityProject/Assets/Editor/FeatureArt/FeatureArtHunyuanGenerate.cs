using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BinGames.EditorTools.CellArt;
using GameLogic.ArtBinding;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>三视图 → 混元出模 → Raw 规范名 → TryBind + Save catalog。同时只跑一个任务。</summary>
    public static class FeatureArtHunyuanGenerate
    {
        const int MaxEncodedBytes = 6 * 1024 * 1024;
        const float PollSeconds = 5f;
        const float TimeoutSeconds = 20f * 60f;
        const string RawPrefix = "Assets/GameRes/Raw/";
        const string SendPrefsPrefix = "BinGames.Hunyuan.SendViews.";

        public static readonly string[] SendableKeys = { "concept", "front", "left", "right", "back" };
        static readonly string[] MultiViewKeys = { "left", "right", "back" };

        enum Phase
        {
            Idle,
            Submit,
            PollWait,
            Query,
            Download,
        }

        static Phase _phase = Phase.Idle;
        static FeatureArtBindingWindow _window;
        static FeatureArtSlot _slot;
        static UnityWebRequest _req;
        static string _jobId;
        static string _tempDownload;
        static string _tempWork;
        static float _startedAt;
        static float _nextPollAt;
        static string _canonicalName;
        static string _folderHint;
        static string _submitJson;
        static float _lastUiAt;
        static int _queryFails;
        static string _resumeDraft = "";
        static int _sidecarCount;

        public static bool IsBusy => _phase != Phase.Idle;

        public static string BusyHint =>
            IsBusy
                ? (string.IsNullOrEmpty(_jobId) ? "生成中…" : $"生成中… JobId={_jobId}")
                : "";

        static bool _autoAttachTried;

        [InitializeOnLoadMethod]
        static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.delayCall -= AutoAttachOnce;
            EditorApplication.delayCall += AutoAttachOnce;
        }

        static void AutoAttachOnce()
        {
            if (_autoAttachTried || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            _autoAttachTried = true;
            TryRepairRawEmbeddedMaps(out _);
        }

        public static bool CanSendKey(string key) =>
            key == "concept" || key == "front" || key == "left" || key == "right" || key == "back";

        public static bool HasViewFile(CellArtAsset asset, string key) =>
            TryResolveAbs(asset, RelOf(asset, key), out _, out _);

        public static bool HasMainImage(CellArtAsset asset) =>
            TryResolveAbs(asset, MainRel(asset), out _, out _);

        public static HashSet<string> GetSendSet(CellArtAsset asset)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var prefs = SendPrefsKey(asset);
            if (!EditorPrefs.HasKey(prefs))
            {
                if (HasViewFile(asset, "concept"))
                {
                    set.Add("concept");
                }

                return set;
            }

            var raw = EditorPrefs.GetString(prefs, "") ?? "";
            foreach (var part in raw.Split(','))
            {
                var key = part.Trim();
                if (CanSendKey(key))
                {
                    set.Add(key);
                }
            }

            return set;
        }

        public static bool IsSendChecked(CellArtAsset asset, string key) =>
            CanSendKey(key) && GetSendSet(asset).Contains(key);

        public static void SetSendChecked(CellArtAsset asset, string key, bool on)
        {
            if (!CanSendKey(key) || asset == null)
            {
                return;
            }

            var set = GetSendSet(asset);
            if (on)
            {
                set.Add(key);
            }
            else
            {
                set.Remove(key);
            }

            var sb = new StringBuilder();
            foreach (var k in SendableKeys)
            {
                if (!set.Contains(k))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(',');
                }

                sb.Append(k);
            }

            EditorPrefs.SetString(SendPrefsKey(asset), sb.ToString());
        }

        public static string SendSummary(CellArtAsset asset)
        {
            var set = GetSendSet(asset);
            var parts = new List<string>();
            if (set.Contains("front") && HasViewFile(asset, "front"))
            {
                parts.Add("front");
            }
            else if (set.Contains("concept") && HasViewFile(asset, "concept"))
            {
                parts.Add("concept");
            }

            foreach (var key in MultiViewKeys)
            {
                if (set.Contains(key) && HasViewFile(asset, key))
                {
                    parts.Add(key);
                }
            }

            return parts.Count == 0 ? "未勾有效图" : string.Join(" + ", parts);
        }

        public static bool WillSendMultiView(CellArtAsset asset)
        {
            if (FeatureArtHunyuanSettings.IsExpress(FeatureArtHunyuanSettings.SelectedModel))
            {
                return false;
            }

            var set = GetSendSet(asset);
            foreach (var key in MultiViewKeys)
            {
                if (set.Contains(key) && HasViewFile(asset, key))
                {
                    return true;
                }
            }

            return false;
        }

        public static string DisableReason(FeatureArtSlot slot, CellArtAsset asset)
        {
            if (slot == null || slot.retired)
            {
                return "此页没有可绑定的成品槽。";
            }

            if (!FeatureArtHunyuanSettings.HasApiKey)
            {
                return "先去左树「混元生3D」填 API Key。";
            }

            if (IsBusy)
            {
                return BusyHint;
            }

            if (FeatureArtHunyuanSettings.HasLastJob)
            {
                return "先拉取或放弃上次已扣费任务，避免再扣一次。";
            }

            if (!HasMainImage(asset))
            {
                if (HasViewFile(asset, "front") || HasViewFile(asset, "concept"))
                {
                    return "至少勾一张主图（front 或 concept）。";
                }

                return "至少需要 front 或 concept 一张图。";
            }

            if (!TryCanonical(slot, out _, out _, out var err))
            {
                return err;
            }

            return null;
        }

        public static void DrawButton(FeatureArtBindingWindow window, CellArtAsset asset, FeatureArtSlot target)
        {
            if (window == null || target == null)
            {
                return;
            }

            var reason = DisableReason(target, asset);
            var label = IsBusy ? "生成中…" : "用三视图生成模型并绑定";
            var multi = WillSendMultiView(asset);
            EditorGUILayout.HelpBox(
                "各图下方勾「发给混元」。默认只发概念图；正视和左右后要勾了才发（图越多积分越多）。本次：" +
                SendSummary(asset) + "。模型=" + FeatureArtHunyuanSettings.SelectedModel + "。",
                MessageType.None);
            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandWidth(false)))
            {
                EditorGUI.BeginDisabledGroup(reason != null);
                if (GUILayout.Button(label, GUILayout.Width(200)))
                {
                    Begin(window, target, asset);
                }

                EditorGUI.EndDisabledGroup();
                EditorGUI.BeginDisabledGroup(IsBusy);
                FeatureArtHunyuanSettings.DrawModelPopup(200);
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.LabelField(
                    FeatureArtHunyuanSettings.CreditHint(multi),
                    EditorStyles.miniLabel,
                    GUILayout.MinWidth(160));
                if (reason != null)
                {
                    EditorGUILayout.LabelField(reason, EditorStyles.miniLabel);
                }
            }

            if (!IsBusy && TryCanonical(target, out var folder, out var name, out _) &&
                File.Exists(AbsFromAsset(folder + "/" + name + ".fbx")))
            {
                if (GUILayout.Button("补抽贴图（不重新生成、不扣积分）", GUILayout.Width(220)))
                {
                    var n = TryRepairImportedMaps(folder, name, out var log);
                    window.Log(n > 0 ? log : (log ?? "没有抽出贴图。"));
                    window.Repaint();
                }
            }

            DrawProgress(window, target);
        }

        public static void DrawProgress(FeatureArtBindingWindow window, FeatureArtSlot target)
        {
            if (IsBusy)
            {
                var elapsed = Now() - _startedAt;
                var remain = Mathf.Max(0f, TimeoutSeconds - elapsed);
                var rect = GUILayoutUtility.GetRect(4, 18, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(rect, Mathf.Clamp01(elapsed / TimeoutSeconds),
                    PhaseLabel() + $"  {elapsed:0}s / 剩余 {remain:0}s  id={_jobId}");
                EditorGUILayout.HelpBox(
                    "云端 Normal + PBR 常要 3～15 分钟。积分是腾讯出模成功后扣的；本地超时不会退款。",
                    MessageType.Info);
                if (GUILayout.Button("取消等待（云端任务不停、不退积分）", GUILayout.Width(260)))
                {
                    CancelWait();
                }

                return;
            }

            if (FeatureArtHunyuanSettings.HasLastJob)
            {
                EditorGUILayout.HelpBox(
                    $"上次任务 id={FeatureArtHunyuanSettings.LastJobId}（{FeatureArtHunyuanSettings.LastSlotId}）。只下载，不再提交。",
                    MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("继续拉取上次任务", GUILayout.Width(160)))
                    {
                        ResumeLast(window, target);
                    }

                    if (GUILayout.Button("放弃任务号", GUILayout.Width(88)))
                    {
                        FeatureArtHunyuanSettings.ClearLastJob();
                    }

                    if (FeatureArtHunyuanSettings.HasLastFileUrl &&
                        GUILayout.Button("再下上次链接", GUILayout.Width(100)))
                    {
                        ResumeSavedUrl(window, target);
                    }

                    if (FeatureArtHunyuanSettings.HasLastFileUrl &&
                        GUILayout.Button("复制模型链接", GUILayout.Width(100)))
                    {
                        GUIUtility.systemCopyBuffer = FeatureArtHunyuanSettings.LastFileUrl;
                        window.Log("已复制模型链接，可用浏览器下载。不要发到聊天。");
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _resumeDraft = EditorGUILayout.TextField("任务 id", _resumeDraft, GUILayout.MinWidth(180));
                if (GUILayout.Button("用此 id 拉取", GUILayout.Width(100)) &&
                    !string.IsNullOrWhiteSpace(_resumeDraft))
                {
                    FeatureArtHunyuanSettings.RememberLast(
                        _resumeDraft.Trim(),
                        target != null ? target.id : "",
                        target != null ? (target.folderHint ?? "") : "",
                        "");
                    ResumeLast(window, target);
                }
            }
        }

        public static bool TryBuildSubmitJson(CellArtAsset asset, out string json, out string error) =>
            TryBuildSubmitJson(asset, GetSendSet(asset), out json, out error);

        public static bool TryBuildSubmitJson(
            CellArtAsset asset,
            ISet<string> sendKeys,
            out string json,
            out string error)
        {
            json = null;
            if (!TryEncodeImages(asset, sendKeys, out var mainUri, out var views, out error))
            {
                return false;
            }

            json = FeatureArtHunyuanClient.BuildSubmitJson(
                mainUri, views, FeatureArtHunyuanSettings.SelectedModel);
            return true;
        }

        public static bool TryCanonical(FeatureArtSlot slot, out string folderHint, out string fileName, out string error)
        {
            folderHint = null;
            fileName = null;
            error = null;
            if (slot == null)
            {
                error = "槽位为空。";
                return false;
            }

            folderHint = (slot.folderHint ?? "").Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(folderHint) ||
                !folderHint.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "folderHint 不在 Raw 下，拒绝落盘。";
                return false;
            }

            switch (slot.domain)
            {
                case "player":
                    fileName = "player_chassis";
                    return true;
                case "organ":
                    fileName = "organ_" + slot.key;
                    return true;
                case "summon":
                    fileName = "summon_" + slot.key;
                    return true;
                case "enemy":
                    fileName = "enemy_" + slot.key;
                    return true;
                case "shape":
                    if (slot.role != "projectile")
                    {
                        error = "弹道页只生成弹体。";
                        return false;
                    }

                    fileName = "shape_" + slot.key + "_projectile";
                    return true;
                default:
                    error = "此槽不支持一键出模。";
                    return false;
            }
        }

        static void Begin(FeatureArtBindingWindow window, FeatureArtSlot slot, CellArtAsset asset)
        {
            if (IsBusy)
            {
                window.Log("已有生成任务在跑。");
                return;
            }

            if (!TryCanonical(slot, out var folder, out var name, out var err))
            {
                window.Log(err);
                return;
            }

            if (!ConfirmOverwrite(slot, folder, name))
            {
                window.Log("已取消生成。");
                return;
            }

            if (!TryBuildSubmitJson(asset, out var json, out err))
            {
                window.Log(err);
                return;
            }

            window.Log("发给混元：" + SendSummary(asset) + " 模型=" +
                       FeatureArtHunyuanSettings.SelectedModel + " " +
                       FeatureArtHunyuanSettings.CreditHint(WillSendMultiView(asset)));

            var key = FeatureArtHunyuanSettings.GetApiKey();
            if (string.IsNullOrEmpty(key))
            {
                window.Log("先去左树「混元生3D」填 API Key。");
                return;
            }

            _window = window;
            _slot = slot;
            _folderHint = folder;
            _canonicalName = name;
            _submitJson = json;
            _jobId = null;
            _queryFails = 0;
            _startedAt = Now();
            _tempWork = Path.Combine(Path.GetTempPath(), "bingames-hy3d-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempWork);
            _tempDownload = Path.Combine(_tempWork, "download.bin");
            EditorPrefs.SetString(FeatureArtHunyuanSettings.LastModelKey,
                FeatureArtHunyuanSettings.SelectedModel);
            StartSubmit(key);
            window.Log("已提交混元任务。");
        }

        static bool ConfirmOverwrite(FeatureArtSlot slot, string folder, string name)
        {
            var destFbx = folder + "/" + name + ".fbx";
            var destObj = folder + "/" + name + ".obj";
            var destPrefab = folder + "/" + name + ".prefab";
            var destSrc = folder + "/" + name + "_src.fbx";
            var exists = File.Exists(AbsFromAsset(destFbx)) || File.Exists(AbsFromAsset(destObj)) ||
                         File.Exists(AbsFromAsset(destPrefab)) || File.Exists(AbsFromAsset(destSrc)) ||
                         HasSidecarFiles(folder, name);
            var rebound = !string.IsNullOrEmpty(slot.location) &&
                          !string.Equals(slot.location, name, StringComparison.Ordinal);
            if (!exists && !rebound)
            {
                return true;
            }

            var msg = rebound
                ? $"将覆盖 Raw 下 {name}（含贴图）并把 {slot.id} 从 {slot.location} 改绑为 {name}，继续？"
                : $"将覆盖 Raw 下 {name}（含贴图）并重绑，继续？";
            return EditorUtility.DisplayDialog("混元出模", msg, "继续", "取消");
        }

        static void StartSubmit(string key)
        {
            DisposeReq();
            _req = FeatureArtHunyuanClient.PostJson(FeatureArtHunyuanClient.SubmitUrl, _submitJson, key);
            _req.SendWebRequest();
            _phase = Phase.Submit;
        }

        static void Tick()
        {
            if (_phase == Phase.Idle)
            {
                return;
            }

            if (Now() - _lastUiAt > 0.5f)
            {
                _lastUiAt = Now();
                if (_window != null)
                {
                    _window.Repaint();
                }
            }

            if (Now() - _startedAt > TimeoutSeconds)
            {
                Fail($"生成超时（{TimeoutSeconds / 60f:0} 分钟）。");
                return;
            }

            if (_phase == Phase.PollWait)
            {
                if (Now() >= _nextPollAt)
                {
                    StartQuery();
                }

                return;
            }

            if (_req == null || !_req.isDone)
            {
                return;
            }

            switch (_phase)
            {
                case Phase.Submit:
                    OnSubmitDone();
                    break;
                case Phase.Query:
                    OnQueryDone();
                    break;
                case Phase.Download:
                    break;
            }
        }

        static void OnSubmitDone()
        {
            var body = FeatureArtHunyuanClient.SafeBody(_req);
            if (FeatureArtHunyuanClient.IsUnauthorized(_req))
            {
                DisposeReq();
                var apiErr = FeatureArtHunyuanClient.ExtractError(body);
                Fail("API Key 无效。请确认是 TokenHub 的 sk- Key，并已点「保存」。"
                     + (string.IsNullOrEmpty(apiErr) ? "" : " " + apiErr));
                return;
            }

            var transport = FeatureArtHunyuanClient.TransportError(_req);
            DisposeReq();
            if (transport != null && string.IsNullOrEmpty(body))
            {
                Fail(transport);
                return;
            }

            if (!FeatureArtHunyuanClient.TryReadJobId(body, out _jobId, out var err))
            {
                Fail(err ?? transport ?? "提交失败。");
                return;
            }

            FeatureArtHunyuanSettings.RememberLast(_jobId, _slot != null ? _slot.id : "", _folderHint, _canonicalName);
            Log($"已排队 id={_jobId}");
            _nextPollAt = Now() + PollSeconds;
            _phase = Phase.PollWait;
        }

        static void StartQuery()
        {
            var key = FeatureArtHunyuanSettings.GetApiKey();
            DisposeReq();
            _req = FeatureArtHunyuanClient.PostJson(
                FeatureArtHunyuanClient.QueryUrl,
                FeatureArtHunyuanClient.BuildQueryJson(_jobId, FeatureArtHunyuanSettings.LastModel),
                key);
            _req.SendWebRequest();
            _phase = Phase.Query;
        }

        static void OnQueryDone()
        {
            var transport = FeatureArtHunyuanClient.TransportError(_req);
            var body = FeatureArtHunyuanClient.SafeBody(_req);
            DisposeReq();
            if (transport != null && string.IsNullOrEmpty(body))
            {
                RetryQueryOrFail(transport);
                return;
            }

            if (!FeatureArtHunyuanClient.TryReadQuery(body, out var status, out var err, out var files))
            {
                RetryQueryOrFail(err ?? transport ?? "查询失败。");
                return;
            }

            _queryFails = 0;

            status = status.ToUpperInvariant();
            if (status == "WAIT" || status == "RUN")
            {
                Log($"{status} id={_jobId}");
                _nextPollAt = Now() + PollSeconds;
                _phase = Phase.PollWait;
                return;
            }

            if (status == "FAIL")
            {
                Fail(err ?? "混元任务失败。");
                return;
            }

            if (status != "DONE")
            {
                Fail("未知任务状态。");
                return;
            }

            var urls = FeatureArtHunyuanClient.OrderedFileUrls(files);
            if (urls.Count == 0)
            {
                Fail("任务完成但没有模型文件。");
                return;
            }

            FeatureArtHunyuanSettings.RememberFileUrl(urls[0]);
            _phase = Phase.Download;
            ImportFromUrls(urls);
        }

        static void ImportDownloadedFile()
        {
            var url = FeatureArtHunyuanSettings.LastFileUrl;
            ImportFromUrls(string.IsNullOrEmpty(url)
                ? new List<string>()
                : new List<string> { url });
        }

        static void ImportFromUrls(List<string> urls)
        {
            if (urls == null || urls.Count == 0)
            {
                Fail("没有可下载的模型地址。");
                return;
            }

            string lastErr = null;
            string glbOnly = null;
            for (var i = 0; i < urls.Count; i++)
            {
                var url = urls[i];
                FeatureArtHunyuanSettings.RememberFileUrl(url);
                Log($"下载 {i + 1}/{urls.Count} 主机={FeatureArtHunyuanClient.HostOf(url)}");
                var dest = Path.Combine(_tempWork, "download-" + i + ".bin");
                if (!FeatureArtHunyuanClient.TryDownload(url, dest, out var dlErr))
                {
                    lastErr = dlErr;
                    Log(dlErr);
                    continue;
                }

                var extractDir = Path.Combine(_tempWork, "extract-" + i);
                if (!FeatureArtHunyuanClient.TryMaterializeModel(dest, extractDir, out var modelPath, out var matErr))
                {
                    lastErr = matErr;
                    Log(matErr);
                    continue;
                }

                var ext = Path.GetExtension(modelPath).ToLowerInvariant();
                if (ext == ".glb")
                {
                    glbOnly = modelPath;
                    lastErr = "本次只给了 GLB，工程没有 GLB 导入器。";
                    continue;
                }

                try
                {
                    if (!ImportAndBind(modelPath, ext))
                    {
                        return;
                    }

                    FinishOk();
                    return;
                }
                catch (Exception e)
                {
                    Fail(e.Message);
                    return;
                }
            }

            Fail(lastErr ?? (glbOnly != null
                ? "本次只给了 GLB，工程没有 GLB 导入器。请重试或换 FBX/OBJ。"
                : "没有可用的 FBX/OBJ。"));
        }

        static bool ImportAndBind(string srcModel, string ext)
        {
            EnsureAssetFolder(_folderHint);
            var isPrefab = string.Equals(_slot.bindKind, "PooledPrefab", StringComparison.Ordinal);
            var modelAssetPath = isPrefab
                ? $"{_folderHint}/{_canonicalName}_src{ext}"
                : $"{_folderHint}/{_canonicalName}{ext}";
            if (!modelAssetPath.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Fail("拒绝写入 Art/ 或 Raw 以外。");
                return false;
            }

            var destAbs = AbsFromAsset(modelAssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destAbs) ?? _tempWork);
            ClearOldSidecars(_folderHint, _canonicalName);
            File.Copy(srcModel, destAbs, true);
            _sidecarCount = MaterializeMaps(srcModel, destAbs, _folderHint, _canonicalName);
            ImportMapAssets(_folderHint, _canonicalName);
            AssetDatabase.ImportAsset(modelAssetPath, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(modelAssetPath) is ModelImporter importer)
            {
                importer.meshCompression = ModelImporterMeshCompression.Off;
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
                importer.materialSearch = ModelImporterMaterialSearch.RecursiveUp;
                importer.SaveAndReimport();
            }

            RemapAndPaintMaps(modelAssetPath, _folderHint, _canonicalName);

            UnityEngine.Object bindTarget;
            if (isPrefab)
            {
                bindTarget = WriteProjectilePrefab(modelAssetPath);
                if (bindTarget == null)
                {
                    Fail("无法从模型抽出 Mesh 做成弹体 Prefab。");
                    return false;
                }
            }
            else
            {
                bindTarget = LoadBindableMesh(modelAssetPath);
                if (bindTarget == null)
                {
                    Fail("导入后没有 MeshFilter。");
                    return false;
                }
            }

            var before = _slot.location;
            _window.TryBind(_slot, bindTarget);
            if (!string.Equals(_slot.location, _canonicalName, StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(before))
                {
                    _slot.location = before;
                }

                Fail("绑定未写入规范 location。");
                return false;
            }

            _window.SaveCatalogNow();
            return true;
        }

        static UnityEngine.Object WriteProjectilePrefab(string modelAssetPath)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelAssetPath);
            var mesh = FindFirstMesh(model);
            if (mesh == null)
            {
                return null;
            }

            var prefabPath = $"{_folderHint}/{_canonicalName}.prefab";
            var go = new GameObject(_canonicalName);
            try
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                var srcGo = AssetDatabase.LoadAssetAtPath<GameObject>(modelAssetPath);
                var srcMr = srcGo != null ? srcGo.GetComponentInChildren<MeshRenderer>(true) : null;
                if (srcMr != null && srcMr.sharedMaterial != null)
                {
                    mr.sharedMaterial = srcMr.sharedMaterial;
                }

                return PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        static UnityEngine.Object LoadBindableMesh(string modelAssetPath)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(modelAssetPath);
            if (go != null && go.GetComponentInChildren<MeshFilter>(true) != null)
            {
                return go;
            }

            var assets = AssetDatabase.LoadAllAssetsAtPath(modelAssetPath);
            foreach (var a in assets)
            {
                if (a is Mesh)
                {
                    return a;
                }
            }

            return null;
        }

        static bool HasSidecarFiles(string folder, string name)
        {
            var dir = AbsFromAsset(folder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || string.IsNullOrEmpty(name))
            {
                return false;
            }

            foreach (var path in Directory.GetFiles(dir, name + "_*"))
            {
                if (FeatureArtHunyuanClient.IsSidecarExt(Path.GetExtension(path)))
                {
                    return true;
                }
            }

            return Directory.Exists(Path.Combine(dir, name + ".fbm")) ||
                   Directory.Exists(Path.Combine(dir, "output.fbm"));
        }

        static void ClearOldSidecars(string folder, string name)
        {
            var dir = AbsFromAsset(folder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || string.IsNullOrEmpty(name))
            {
                return;
            }

            foreach (var path in Directory.GetFiles(dir, name + "_*"))
            {
                var ext = Path.GetExtension(path);
                if (!FeatureArtHunyuanClient.IsSidecarExt(ext) &&
                    !string.Equals(ext, ".mat", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var asset = folder + "/" + Path.GetFileName(path);
                if (!AssetDatabase.DeleteAsset(asset))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch
                    {
                        // best-effort
                    }
                }
            }

            DeleteAssetOrDir(folder + "/" + name + ".fbm");
        }

        /// <summary>zip 散图 + FBX 内嵌 PNG → 规范名前缀（Address 不撞）+ 临时 output.fbm（给 FBX 对路径）。</summary>
        static int MaterializeMaps(string srcModel, string destFbxAbs, string folder, string name)
        {
            var count = 0;
            var extractDir = Path.GetDirectoryName(srcModel);
            foreach (var path in FeatureArtHunyuanClient.ListSidecarFiles(extractDir, srcModel))
            {
                if (WriteUniqueAndFbm(path, folder, name, Path.GetFileName(path)))
                {
                    count++;
                }
            }

            var tmp = Path.Combine(Path.GetTempPath(), "bingames-hy-fbm-" + Guid.NewGuid().ToString("N"));
            try
            {
                var fromSrc = FeatureArtHunyuanClient.ExtractEmbeddedPngs(srcModel, tmp, true);
                if (fromSrc == 0 && !string.IsNullOrEmpty(destFbxAbs))
                {
                    FeatureArtHunyuanClient.ExtractEmbeddedPngs(destFbxAbs, tmp, true);
                }

                if (Directory.Exists(tmp))
                {
                    foreach (var path in Directory.GetFiles(tmp, "*.png"))
                    {
                        if (WriteUniqueAndFbm(path, folder, name, Path.GetFileName(path)))
                        {
                            count++;
                        }
                    }
                }
            }
            finally
            {
                TryDeleteDir(tmp);
            }

            return count;
        }

        static bool WriteUniqueAndFbm(string srcAbs, string folder, string name, string originalName)
        {
            originalName = SanitizeFileName(originalName);
            if (string.IsNullOrEmpty(originalName))
            {
                return false;
            }

            var uniqueAsset = folder + "/" + name + "_" + originalName;
            var fbmAsset = folder + "/output.fbm/" + originalName;
            if (!uniqueAsset.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase) ||
                !fbmAsset.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            WriteAbsCopy(srcAbs, AbsFromAsset(uniqueAsset));
            WriteAbsCopy(srcAbs, AbsFromAsset(fbmAsset));
            return true;
        }

        static void WriteAbsCopy(string srcAbs, string destAbs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destAbs) ?? "");
            File.Copy(srcAbs, destAbs, true);
        }

        static void ImportMapAssets(string folder, string name)
        {
            var dir = AbsFromAsset(folder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return;
            }

            foreach (var path in Directory.GetFiles(dir, name + "_*"))
            {
                ImportOneMap(folder + "/" + Path.GetFileName(path));
            }

            var fbm = Path.Combine(dir, "output.fbm");
            if (!Directory.Exists(fbm))
            {
                return;
            }

            foreach (var path in Directory.GetFiles(fbm, "*.*"))
            {
                if (FeatureArtHunyuanClient.IsSidecarExt(Path.GetExtension(path)))
                {
                    ImportOneMap(folder + "/output.fbm/" + Path.GetFileName(path));
                }
            }
        }

        static void ImportOneMap(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) ||
                !assetPath.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            if (!(AssetImporter.GetAtPath(assetPath) is TextureImporter tex))
            {
                return;
            }

            var file = Path.GetFileName(assetPath) ?? "";
            if (file.IndexOf("_normal", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                tex.textureType = TextureImporterType.NormalMap;
                tex.SaveAndReimport();
            }
        }

        static void RemapAndPaintMaps(string modelAssetPath, string folder, string name)
        {
            if (AssetImporter.GetAtPath(modelAssetPath) is ModelImporter importer)
            {
                try
                {
                    importer.SearchAndRemapMaterials(
                        ModelImporterMaterialName.BasedOnMaterialName,
                        ModelImporterMaterialSearch.RecursiveUp);
                    importer.SaveAndReimport();
                }
                catch
                {
                    // SearchAndRemap 在部分 FBX 上会抛，仍走下面手绑
                }
            }

            ApplyPbrToImportedModel(modelAssetPath, folder, name);
            DeleteTempFbm(folder, name);
        }

        static int ApplyPbrToImportedModel(string modelAssetPath, string folder, string name)
        {
            var albedo = FindMap(folder, name, isAlbedo: true, "texture_pbr", "albedo", "diffuse", "basecolor", "base_color");
            var normal = FindMap(folder, name, isAlbedo: false, "normal", "nor");
            var metallic = FindMap(folder, name, isAlbedo: false, "metallic", "metalness");
            var painted = 0;
            var assets = AssetDatabase.LoadAllAssetsAtPath(modelAssetPath);
            foreach (var a in assets)
            {
                if (!(a is Material mat))
                {
                    continue;
                }

                PaintStandard(mat, albedo, normal, metallic);
                EditorUtility.SetDirty(mat);
                painted++;
            }

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(modelAssetPath);
            if (go != null)
            {
                foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (mr.sharedMaterial == null && albedo != null)
                    {
                        mr.sharedMaterial = NewHyMaterial(folder, name, albedo, normal, metallic);
                        EditorUtility.SetDirty(mr);
                        painted++;
                    }
                    else if (mr.sharedMaterial != null)
                    {
                        PaintStandard(mr.sharedMaterial, albedo, normal, metallic);
                        EditorUtility.SetDirty(mr.sharedMaterial);
                    }
                }
            }

            if (painted == 0 && albedo != null)
            {
                NewHyMaterial(folder, name, albedo, normal, metallic);
                painted = 1;
            }

            AssetDatabase.SaveAssets();
            return painted;
        }

        static Material NewHyMaterial(string folder, string name, Texture albedo, Texture normal, Texture metallic)
        {
            var matPath = folder + "/" + name + "_hy.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                var shader = Shader.Find("Standard");
                if (shader == null)
                {
                    return null;
                }

                mat = new Material(shader) { name = name + "_hy", enableInstancing = true };
                AssetDatabase.CreateAsset(mat, matPath);
            }

            PaintStandard(mat, albedo, normal, metallic);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void PaintStandard(Material mat, Texture albedo, Texture normal, Texture metallic)
        {
            if (mat == null)
            {
                return;
            }

            if (albedo != null && mat.HasProperty("_MainTex"))
            {
                mat.SetTexture("_MainTex", albedo);
            }

            if (albedo != null && mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", albedo);
            }

            if (albedo != null && mat.HasProperty("_ColorMap"))
            {
                mat.SetTexture("_ColorMap", albedo);
            }

            if (normal != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
            }

            if (normal != null && mat.HasProperty("_NormalMap"))
            {
                mat.SetTexture("_NormalMap", normal);
            }

            if (metallic != null && mat.HasProperty("_MetallicGlossMap"))
            {
                mat.SetTexture("_MetallicGlossMap", metallic);
                mat.EnableKeyword("_METALLICGLOSSMAP");
            }

            mat.enableInstancing = true;
        }

        static Texture2D FindMap(string folder, string name, bool isAlbedo, params string[] hints)
        {
            Texture2D fallback = null;
            var dir = AbsFromAsset(folder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return null;
            }

            foreach (var path in Directory.GetFiles(dir, name + "_*.png"))
            {
                var file = Path.GetFileNameWithoutExtension(path) ?? "";
                var lower = file.ToLowerInvariant();
                var asset = folder + "/" + Path.GetFileName(path);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(asset);
                if (tex == null)
                {
                    continue;
                }

                if (isAlbedo)
                {
                    if (lower.Contains("_normal") || lower.Contains("_roughness") ||
                        lower.Contains("_metallic") || lower.Contains("_metalness"))
                    {
                        continue;
                    }

                    foreach (var h in hints)
                    {
                        if (lower.Contains(h))
                        {
                            return tex;
                        }
                    }

                    if (fallback == null)
                    {
                        fallback = tex;
                    }
                }
                else
                {
                    foreach (var h in hints)
                    {
                        if (lower.Contains(h))
                        {
                            return tex;
                        }
                    }
                }
            }

            return fallback;
        }

        static void DeleteTempFbm(string folder, string name)
        {
            // 保留 output.fbm：FBX 引用的是这个相对路径，删了会再导成白模。
            // 多器官同目录时以 {canonical}_* 为准（ApplyPbr 绑的是规范名）。
            if (!string.IsNullOrEmpty(name))
            {
                DeleteAssetOrDir(folder + "/" + name + ".fbm");
            }
        }

        static void DeleteAssetOrDir(string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder))
            {
                return;
            }

            if (AssetDatabase.IsValidFolder(assetFolder))
            {
                AssetDatabase.DeleteAsset(assetFolder);
                return;
            }

            var abs = AbsFromAsset(assetFolder);
            if (Directory.Exists(abs))
            {
                TryDeleteDir(abs);
            }
        }

        public static int TryRepairRawEmbeddedMaps(out string log)
        {
            var total = 0;
            var parts = new List<string>();
            var guids = AssetDatabase.FindAssets("t:Model", new[] { "Assets/GameRes/Raw" });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) ||
                    !path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!NeedsMapRepair(path))
                {
                    continue;
                }

                var folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                var name = Path.GetFileNameWithoutExtension(path);
                var n = TryRepairImportedMaps(folder, name, out var one);
                if (n > 0)
                {
                    total += n;
                    parts.Add(name + "×" + n);
                }
                else if (!string.IsNullOrEmpty(one))
                {
                    parts.Add(name + "：" + one);
                }
            }

            log = total > 0
                ? "已补抽并绑贴图：" + string.Join("；", parts)
                : (parts.Count > 0 ? string.Join("；", parts) : "没有需要补抽的白模 FBX。");
            return total;
        }

        public static int TryRepairImportedMaps(string folder, string name, out string log)
        {
            log = null;
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name) ||
                !folder.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
            {
                log = "路径不在 Raw 下。";
                return 0;
            }

            var modelAssetPath = folder + "/" + name + ".fbx";
            var destAbs = AbsFromAsset(modelAssetPath);
            if (!File.Exists(destAbs))
            {
                log = "没有 " + name + ".fbx";
                return 0;
            }

            var maps = FindMap(folder, name, isAlbedo: true, "texture_pbr", "albedo", "diffuse") != null
                ? CountUniqueMaps(folder, name)
                : MaterializeMaps(destAbs, destAbs, folder, name);
            if (maps == 0)
            {
                maps = MaterializeMaps(destAbs, destAbs, folder, name);
            }

            ImportMapAssets(folder, name);
            AssetDatabase.ImportAsset(modelAssetPath, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(modelAssetPath) is ModelImporter importer)
            {
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
                importer.materialSearch = ModelImporterMaterialSearch.RecursiveUp;
                importer.SaveAndReimport();
            }

            RemapAndPaintMaps(modelAssetPath, folder, name);
            log = maps > 0
                ? name + " 抽出 " + maps + " 张贴图并绑到材质（对象框应不再全白）"
                : name + " 没有内嵌/旁路贴图";
            return maps;
        }

        static int CountUniqueMaps(string folder, string name)
        {
            var dir = AbsFromAsset(folder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return 0;
            }

            var n = 0;
            foreach (var path in Directory.GetFiles(dir, name + "_*"))
            {
                if (FeatureArtHunyuanClient.IsSidecarExt(Path.GetExtension(path)))
                {
                    n++;
                }
            }

            return n;
        }

        static bool NeedsMapRepair(string modelAssetPath)
        {
            var abs = AbsFromAsset(modelAssetPath);
            if (!File.Exists(abs))
            {
                return false;
            }

            var hasAlbedo = false;
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(modelAssetPath))
            {
                if (a is Material mat && mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null)
                {
                    hasAlbedo = true;
                    break;
                }
            }

            if (hasAlbedo)
            {
                return false;
            }

            var folder = Path.GetDirectoryName(modelAssetPath)?.Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(modelAssetPath);
            return FeatureArtHunyuanClient.HasEmbeddedPng(abs) || HasSidecarFiles(folder, name);
        }

        static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "map.png";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                var bad = false;
                for (var i = 0; i < invalid.Length; i++)
                {
                    if (c == invalid[i])
                    {
                        bad = true;
                        break;
                    }
                }

                sb.Append(bad ? '_' : c);
            }

            return sb.ToString();
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

        static void FinishOk()
        {
            var id = _slot != null ? _slot.id : "";
            var loc = _slot != null ? _slot.location : "";
            if (!string.IsNullOrEmpty(_jobId))
            {
                FeatureArtHunyuanSettings.RememberLast(
                    _jobId, id, _folderHint, _canonicalName, "已绑定");
            }

            FeatureArtHunyuanSettings.ClearLastJob();
            var maps = _sidecarCount;
            Cleanup();
            Log($"{id} → location={loc}" +
                (maps > 0
                    ? $"  贴图 {maps} 张（已绑到材质，规范名 {loc}_*）"
                    : "  （包内无贴图文件，也未从 FBX 抽出）"));
            if (_window != null)
            {
                _window.Repaint();
            }
        }

        static void Fail(string message)
        {
            if (!string.IsNullOrEmpty(_jobId))
            {
                var status = (message ?? "").Contains("超时")
                    ? "本地超时"
                    : (message ?? "").Contains("取消")
                        ? "已取消等待"
                        : "本地失败";
                FeatureArtHunyuanSettings.RememberLast(
                    _jobId, _slot != null ? _slot.id : "", _folderHint, _canonicalName, status);
            }

            var job = _jobId;
            Cleanup();
            var text = FeatureArtHunyuanClient.SanitizeForUi(message ?? "生成失败。");
            if (!string.IsNullOrEmpty(job))
            {
                text += $" 任务 id={job} 已记下，点「继续拉取上次任务」，不要再点生成。";
            }

            Debug.LogWarning("[混元生3D] " + text);
            if (_window != null)
            {
                _window.LogError(text);
                _window.Repaint();
            }
        }

        static void CancelWait()
        {
            Fail("已取消本地等待。");
        }

        static void RetryQueryOrFail(string reason)
        {
            _queryFails++;
            if (_queryFails >= 5)
            {
                Fail(reason);
                return;
            }

            Log($"查询暂时失败（{_queryFails}/5），稍后重试。");
            _nextPollAt = Now() + PollSeconds;
            _phase = Phase.PollWait;
        }

        static string PhaseLabel()
        {
            switch (_phase)
            {
                case Phase.Submit:
                    return "提交中";
                case Phase.PollWait:
                    return "排队/生成中";
                case Phase.Query:
                    return "查询状态";
                case Phase.Download:
                    return "下载模型";
                default:
                    return "空闲";
            }
        }

        static bool TryBeginResume(FeatureArtBindingWindow window, FeatureArtSlot fallback, out string error)
        {
            error = null;
            if (IsBusy)
            {
                error = "已有生成任务在跑。";
                return false;
            }

            var job = FeatureArtHunyuanSettings.LastJobId;
            if (string.IsNullOrEmpty(job) && !string.IsNullOrWhiteSpace(_resumeDraft))
            {
                job = _resumeDraft.Trim();
            }

            if (string.IsNullOrEmpty(job))
            {
                error = "没有可拉取的任务 id。";
                return false;
            }

            var slot = window != null ? window.FindSlot(FeatureArtHunyuanSettings.LastSlotId) : null;
            if (slot == null)
            {
                slot = fallback;
            }

            if (slot == null)
            {
                error = "找不到当时的成品槽。";
                return false;
            }

            var folder = FeatureArtHunyuanSettings.LastFolder;
            var name = FeatureArtHunyuanSettings.LastName;
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name))
            {
                if (!TryCanonical(slot, out folder, out name, out error))
                {
                    return false;
                }
            }

            _window = window;
            _slot = slot;
            _folderHint = folder;
            _canonicalName = name;
            _submitJson = null;
            _jobId = job;
            _queryFails = 0;
            _startedAt = Now();
            _tempWork = Path.Combine(Path.GetTempPath(), "bingames-hy3d-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempWork);
            _tempDownload = Path.Combine(_tempWork, "download.bin");
            FeatureArtHunyuanSettings.RememberLast(job, slot.id, folder, name);
            return true;
        }

        static void ResumeLast(FeatureArtBindingWindow window, FeatureArtSlot fallback)
        {
            if (!TryBeginResume(window, fallback, out var err))
            {
                window.LogError(err);
                return;
            }

            if (string.IsNullOrEmpty(FeatureArtHunyuanSettings.GetApiKey()))
            {
                Cleanup();
                window.LogError("先去左树「混元生3D」填 API Key。");
                return;
            }

            Log($"继续拉取 id={_jobId}");
            StartQuery();
        }

        static void ResumeSavedUrl(FeatureArtBindingWindow window, FeatureArtSlot fallback)
        {
            var url = FeatureArtHunyuanSettings.LastFileUrl;
            if (!FeatureArtHunyuanSettings.HasLastFileUrl)
            {
                window.LogError("没有记下的模型链接，请点「继续拉取上次任务」。");
                return;
            }

            if (!TryBeginResume(window, fallback, out var err))
            {
                window.LogError(err);
                return;
            }

            Log($"再下上次链接 主机={FeatureArtHunyuanClient.HostOf(url)}");
            _phase = Phase.Download;
            ImportFromUrls(new List<string> { url });
        }

        static float Now() => (float)EditorApplication.timeSinceStartup;

        static void Cleanup()
        {
            DisposeReq();
            _phase = Phase.Idle;
            _jobId = null;
            _slot = null;
            _folderHint = null;
            _canonicalName = null;
            _submitJson = null;
            _sidecarCount = 0;
            TryDeleteDir(_tempWork);
            _tempWork = null;
            _tempDownload = null;
        }

        static void DisposeReq()
        {
            if (_req != null)
            {
                _req.Dispose();
                _req = null;
            }
        }

        static void Log(string message)
        {
            if (_window != null)
            {
                _window.Log(message);
                _window.Repaint();
            }
        }

        static void TryDeleteDir(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // temp cleanup best-effort
            }
        }

        static string SendPrefsKey(CellArtAsset asset)
        {
            var id = asset != null && !string.IsNullOrEmpty(asset.id) ? asset.id : "_";
            return SendPrefsPrefix + id;
        }

        static string RelOf(CellArtAsset asset, string key)
        {
            if (asset == null || string.IsNullOrEmpty(key))
            {
                return null;
            }

            if (key == "concept")
            {
                return string.IsNullOrEmpty(asset.concept) ? null : asset.concept;
            }

            if (asset.views != null && asset.views.TryGetValue(key, out var rel) &&
                !string.IsNullOrEmpty(rel))
            {
                return rel;
            }

            return null;
        }

        static string MainRel(CellArtAsset asset) => MainRelFromSet(asset, GetSendSet(asset));

        static string MainRelFromSet(CellArtAsset asset, ISet<string> send)
        {
            if (send != null && send.Contains("front") && HasViewFile(asset, "front"))
            {
                return RelOf(asset, "front");
            }

            if (send != null && send.Contains("concept") && HasViewFile(asset, "concept"))
            {
                return RelOf(asset, "concept");
            }

            return null;
        }

        static bool TryResolveAbs(CellArtAsset asset, string rel, out string abs, out string error)
        {
            abs = null;
            error = null;
            if (asset == null || string.IsNullOrEmpty(rel))
            {
                error = "没有图。";
                return false;
            }

            abs = CellArtRegistryService.AbsOf(rel);
            if (string.IsNullOrEmpty(abs) || !File.Exists(abs))
            {
                error = "文件不存在。";
                abs = null;
                return false;
            }

            return true;
        }

        static bool TryEncodeImages(
            CellArtAsset asset,
            ISet<string> sendKeys,
            out string mainUri,
            out List<(string ViewType, string DataUri)> views,
            out string error)
        {
            mainUri = null;
            views = new List<(string, string)>();
            error = null;
            var send = sendKeys ?? GetSendSet(asset);
            var maxSide = 2048;
            while (maxSide >= 512)
            {
                views.Clear();
                var mainRel = MainRelFromSet(asset, send);
                if (!TryResolveAbs(asset, mainRel, out var mainAbs, out error))
                {
                    return false;
                }

                if (!TryEncodeFile(mainAbs, maxSide, out mainUri, out var total, out error))
                {
                    return false;
                }

                foreach (var key in MultiViewKeys)
                {
                    if (!send.Contains(key))
                    {
                        continue;
                    }

                    var rel = RelOf(asset, key);
                    if (!TryResolveAbs(asset, rel, out var abs, out _))
                    {
                        continue;
                    }

                    if (!TryEncodeFile(abs, maxSide, out var uri, out var len, out error))
                    {
                        return false;
                    }

                    total += len;
                    views.Add((key, uri));
                }

                if (total <= MaxEncodedBytes)
                {
                    return true;
                }

                maxSide /= 2;
            }

            error = "图太大。";
            mainUri = null;
            views.Clear();
            return false;
        }

        static bool TryEncodeFile(string abs, int maxSide, out string dataUri, out int encodedBytes, out string error)
        {
            dataUri = null;
            encodedBytes = 0;
            error = null;
            var ext = Path.GetExtension(abs).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp")
            {
                error = "仅支持 png/jpg/webp。";
                return false;
            }

            byte[] fileBytes;
            try
            {
                fileBytes = File.ReadAllBytes(abs);
            }
            catch
            {
                error = "读图失败。";
                return false;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(fileBytes, false))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                error = "无法解码图片。";
                return false;
            }

            var w = tex.width;
            var h = tex.height;
            var longSide = Mathf.Max(w, h);
            if (longSide < 128 || longSide > maxSide)
            {
                var target = longSide < 128 ? 128 : maxSide;
                var scale = target / (float)longSide;
                tex = ScaleTexture(tex, Mathf.Max(1, Mathf.RoundToInt(w * scale)),
                    Mathf.Max(1, Mathf.RoundToInt(h * scale)));
            }

            var png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            if (png == null || png.Length == 0)
            {
                error = "编码图片失败。";
                return false;
            }

            var b64 = Convert.ToBase64String(png);
            dataUri = "data:image/png;base64," + b64;
            encodedBytes = dataUri.Length;
            return true;
        }

        static Texture2D ScaleTexture(Texture2D src, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var dst = new Texture2D(w, h, TextureFormat.RGBA32, false);
            dst.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            dst.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(src);
            return dst;
        }

        static void EnsureAssetFolder(string assetFolder)
        {
            var parts = assetFolder.Replace('\\', '/').Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        static string AbsFromAsset(string assetPath)
        {
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(project, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }
}
