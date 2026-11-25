using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using BinGames.EditorTools.CellArt;
using GameLogic.ArtBinding;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BinGames.EditorTools.FeatureArt
{
    // 与素材中心（tools/art-hub）共用的数据形状；字段名对应 manifest.json / index.json，多余字段被 JsonUtility 忽略。
    [Serializable] public sealed class HubImage { public string id, file, session, orig, role, view; public bool picked; }
    [Serializable] public sealed class HubModel { public string id, file, stage, orig; public int ver; public bool adopted; }
    [Serializable] public sealed class HubManifest { public List<HubImage> images = new List<HubImage>(); public List<HubModel> models = new List<HubModel>(); }
    [Serializable] public sealed class HubNodeSummary { public string id, adopted; public int concept, images, unsorted, models; public List<string> views = new List<string>(); }
    [Serializable] sealed class HubIndex { public List<HubNodeSummary> nodes = new List<HubNodeSummary>(); }
    [Serializable] sealed class HubText { public string text; }

    /// <summary>素材中心的编辑器端：读它写的 index.json / manifest.json（只读），启动服务、开网页、导入已采用模型。</summary>
    internal static class FeatureArtHub
    {
        public const int Port = 7870;
        const string RootKey = "BinGames.ArtHub.Root";
        const string ShowAllKey = "BinGames.ArtHub.ShowAllKinds";

        public static string Root
        {
            get => EditorPrefs.GetString(RootKey, "C:/AIArt/projects/BinGames/hub").Replace('\\', '/').TrimEnd('/');
            set => EditorPrefs.SetString(RootKey, value ?? "");
        }

        /// <summary>左侧树是否显示全部类型（默认只显示美术类节点，需求 / 状态 / 固件等说明节点收起）。</summary>
        public static bool ShowAllKinds
        {
            get => EditorPrefs.GetBool(ShowAllKey, false);
            set => EditorPrefs.SetBool(ShowAllKey, value);
        }

        public static string Url => "http://127.0.0.1:" + Port;
        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
        static string ServerScript => Path.Combine(RepoRoot, "tools", "art-hub", "server.py");

        // 美术类节点（与服务端 ART_KINDS 一致）
        static readonly HashSet<string> ArtKinds = new HashSet<string> { "成品模型", "源部件", "特效", "图标", "贴图", "概念图", "模型参考" };
        public static bool IsArtKind(string kind) => kind != null && ArtKinds.Contains(kind);

        /// <summary>美术类节点及其祖先的 id 集合；用于收起说明类节点。</summary>
        public static HashSet<string> ArtNodeIds(FeatureArtWorkspace ws)
        {
            var set = new HashSet<string>();
            foreach (var n in ws.nodes)
            {
                if (n.archived || !IsArtKind(n.kind)) continue;
                for (var cur = n; cur != null && set.Add(cur.id); cur = ws.Find(cur.parentId)) { }
            }
            return set;
        }

        // ───────────── 索引与清单（按文件时间缓存） ─────────────
        static Dictionary<string, HubNodeSummary> _map = new Dictionary<string, HubNodeSummary>();
        static DateTime _indexTime;
        static double _nextIndexCheck;
        public static int Revision { get; private set; }

        static void RefreshIndex()
        {
            if (EditorApplication.timeSinceStartup < _nextIndexCheck) return;
            _nextIndexCheck = EditorApplication.timeSinceStartup + 1.0;
            try
            {
                var p = Root + "/index.json";
                if (!File.Exists(p))
                {
                    if (_map.Count > 0) { _map = new Dictionary<string, HubNodeSummary>(); Revision++; }
                    return;
                }
                var t = File.GetLastWriteTimeUtc(p);
                if (t == _indexTime) return;
                _indexTime = t;
                var idx = JsonUtility.FromJson<HubIndex>(File.ReadAllText(p, Encoding.UTF8));
                _map = idx.nodes.GroupBy(n => n.id).ToDictionary(g => g.Key, g => g.First());
                Revision++;
            }
            catch (IOException) { /* 服务正在写，下一秒再读 */ }
            catch (ArgumentException) { }
        }

        public static HubNodeSummary Summary(string nodeId)
        {
            RefreshIndex();
            return nodeId != null && _map.TryGetValue(nodeId, out var s) ? s : null;
        }

        public static IEnumerable<HubNodeSummary> AllSummaries() { RefreshIndex(); return _map.Values; }

        static readonly Dictionary<string, (DateTime time, HubManifest man)> ManCache = new Dictionary<string, (DateTime, HubManifest)>();

        public static HubManifest Manifest(string nodeId)
        {
            var p = Root + "/assets/" + nodeId + "/manifest.json";
            try
            {
                if (!File.Exists(p)) return null;
                var t = File.GetLastWriteTimeUtc(p);
                if (ManCache.TryGetValue(nodeId, out var e) && e.time == t) return e.man;
                var m = JsonUtility.FromJson<HubManifest>(File.ReadAllText(p, Encoding.UTF8));
                ManCache[nodeId] = (t, m);
                return m;
            }
            catch (Exception) { return null; }
        }

        // ───────────── 缩略图 ─────────────
        static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();

        public static Texture2D Thumb(string relFile)
        {
            var p = Root + "/" + relFile;
            if (Textures.TryGetValue(p, out var t) && t != null) return t;
            if (!File.Exists(p)) return null;
            if (Textures.Count > 40) ClearTextures(); // shortcut: 简单整体清空，只会为当前对象画几张图
            t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            if (!t.LoadImage(File.ReadAllBytes(p))) { UnityEngine.Object.DestroyImmediate(t); return null; }
            Textures[p] = t;
            return t;
        }

        static void ClearTextures()
        {
            foreach (var t in Textures.Values) if (t != null) UnityEngine.Object.DestroyImmediate(t);
            Textures.Clear();
        }

        // ───────────── 服务状态 / 启动 ─────────────
        static volatile bool _up;
        static double _nextProbe;
        static bool _probing;

        public static bool Up { get { Probe(false); return _up; } }

        static async void Probe(bool force)
        {
            if (_probing || (!force && EditorApplication.timeSinceStartup < _nextProbe)) return;
            _probing = true;
            _nextProbe = EditorApplication.timeSinceStartup + 3;
            try
            {
                using (var c = new TcpClient())
                {
                    var connect = c.ConnectAsync("127.0.0.1", Port);
                    _up = await Task.WhenAny(connect, Task.Delay(300)) == connect && c.Connected;
                }
            }
            catch (Exception) { _up = false; }
            finally { _probing = false; }
        }

        public static void StartServer()
        {
            if (Up) return;
            if (!File.Exists(ServerScript))
            {
                EditorUtility.DisplayDialog("素材中心", "找不到 tools/art-hub/server.py。", "好");
                return;
            }
            foreach (var launcher in new[] { ("python", ""), ("py", "-3 ") })
            {
                try
                {
                    var psi = new ProcessStartInfo(launcher.Item1, launcher.Item2 + "\"" + ServerScript + "\"")
                    {
                        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(ServerScript),
                    };
                    psi.EnvironmentVariables["ART_HUB_ROOT"] = Root;
                    Process.Start(psi);
                    return;
                }
                catch (Exception) { /* 换下一个启动器 */ }
            }
            EditorUtility.DisplayDialog("素材中心", "启动失败：找不到 python。请先安装 Python 或手动运行 tools/art-hub/start.cmd。", "好");
        }

        /// <summary>打开网页（服务没起就先起）；nodeId 非空则直接定位到该对象。</summary>
        public static void OpenWeb(string nodeId = null)
        {
            var url = Url + (string.IsNullOrEmpty(nodeId) ? "" : "/#/node/" + nodeId);
            Probe(true);
            if (_up) { Application.OpenURL(url); return; }
            StartServer();
            var until = EditorApplication.timeSinceStartup + 10;
            EditorApplication.CallbackFunction wait = null;
            wait = () =>
            {
                Probe(true);
                if (_up) { EditorApplication.update -= wait; Application.OpenURL(url); }
                else if (EditorApplication.timeSinceStartup > until)
                {
                    EditorApplication.update -= wait;
                    Debug.LogWarning("[素材中心] 服务没有起来，请检查 python 与 tools/art-hub/server.py。");
                }
            };
            EditorApplication.update += wait;
        }

        public static void OpenFolder(string sub = "")
        {
            var p = Root + (string.IsNullOrEmpty(sub) ? "" : "/" + sub);
            if (Directory.Exists(p)) EditorUtility.RevealInFinder(p);
            else EditorUtility.DisplayDialog("素材中心", "素材库目录还不存在：" + p + "\n先启动一次服务并拖入图片。", "好");
        }

        /// <summary>向服务取"复制给出图 AI 的关键词"。服务没起返回 null。</summary>
        public static string FetchPrompt(string nodeId, bool branch)
        {
            if (!Up) return null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(Url + "/api/prompt/" + nodeId + "?scope=" + (branch ? "branch" : "self"));
                req.Timeout = 2500;
                using (var r = req.GetResponse())
                using (var s = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                    return JsonUtility.FromJson<HubText>(s.ReadToEnd()).text;
            }
            catch (Exception) { return null; }
        }

        // ───────────── 编辑器里有窗口开着时，素材中心一有变动就刷新 ─────────────
        [InitializeOnLoadMethod]
        static void Init() => EditorApplication.update += Tick;

        static int _seenRevision;

        static void Tick()
        {
            if (!EditorWindow.HasOpenInstances<FeatureArtBindingWindow>()) return;
            RefreshIndex();
            if (_seenRevision == Revision) return;
            _seenRevision = Revision;
            foreach (var w in Resources.FindObjectsOfTypeAll<FeatureArtBindingWindow>()) w.Repaint();
        }

        // ───────────── 导入已采用模型 → 整包入库 ─────────────
        /// <summary>把对象"采用"的 fbx/obj 放进 {folderHint}/{key}/{key}_src.*，再走既有的整包入库（烘 Prefab + 绑定）。</summary>
        public static void ImportAdopted(FeatureArtNode node, FeatureArtSlot slot)
        {
            var model = Manifest(node.id)?.models.FirstOrDefault(m => m.adopted);
            if (model == null) { EditorUtility.DisplayDialog("导入模型", "素材中心里这个对象还没有采用的模型。", "好"); return; }
            var ext = Path.GetExtension(model.file).ToLowerInvariant();
            if (ext != ".fbx" && ext != ".obj")
            {
                EditorUtility.DisplayDialog("导入模型", "整包入库目前只认 .fbx / .obj（当前是 " + ext + "）。\n请在 Tripo 导出时选 FBX，或让 Blender 转成 FBX 后再拖进素材中心。", "好");
                return;
            }
            if (!FeatureArtHunyuanGenerate.TryCanonical(slot, out var folderHint, out var canonical, out var error))
            {
                EditorUtility.DisplayDialog("导入模型", error, "好");
                return;
            }
            var pkg = FeatureArtHunyuanGenerate.PackageDir(folderHint, canonical);
            var assetPath = pkg + "/" + canonical + "_src" + ext;
            var abs = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
            if (File.Exists(abs) && !EditorUtility.DisplayDialog("覆盖母带模型", assetPath + " 已存在，用素材中心的 v" + model.ver.ToString("000") + " 覆盖？", "覆盖", "取消")) return;
            Directory.CreateDirectory(Path.GetDirectoryName(abs));
            File.Copy(Root + "/" + model.file, abs, true);
            CopyAdoptedSidecars(node.id, model, Root + "/" + model.file, Path.GetDirectoryName(abs), canonical);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(pkg);
            var results = FeatureArtPackageIngest.RunOnSelection();
            var sb = new StringBuilder();
            foreach (var r in results) sb.AppendLine((r.Ok ? "OK  " : "FAIL ") + r.Message);
            EditorUtility.DisplayDialog("导入采用模型并绑定", sb.ToString(), "好");
            if (FeatureArtPackageIngest.LastBoundPrefab != null) Selection.activeObject = FeatureArtPackageIngest.LastBoundPrefab;
        }

        static void CopyAdoptedSidecars(string nodeId, HubModel model, string srcModelAbs, string destDirAbs, string canonical)
        {
            if (string.IsNullOrEmpty(destDirAbs) || string.IsNullOrEmpty(canonical))
            {
                return;
            }

            Directory.CreateDirectory(destDirAbs);
            var prefix = canonical + "_";
            var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void CopyOne(string srcPath, string destName = null)
            {
                if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath))
                {
                    return;
                }

                var fileName = string.IsNullOrEmpty(destName) ? Path.GetFileName(srcPath) : destName;
                if (string.IsNullOrEmpty(fileName))
                {
                    return;
                }

                if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    fileName = prefix + fileName;
                }

                if (!copied.Add(fileName))
                {
                    return;
                }

                var dstPath = Path.Combine(destDirAbs, fileName);
                if (!string.Equals(Path.GetFullPath(srcPath), Path.GetFullPath(dstPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(srcPath, dstPath, true);
                }
            }

            var srcDir = Path.GetDirectoryName(srcModelAbs);
            if (!string.IsNullOrEmpty(srcDir) && Directory.Exists(srcDir))
            {
                foreach (var png in Directory.GetFiles(srcDir, prefix + "*.png"))
                {
                    CopyOne(png);
                }

                CopyOne(Path.Combine(srcDir, prefix + "rig.json"));
            }

            var rootFull = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            foreach (var rel in CollectManifestSidecarPaths(nodeId, model))
            {
                var candidate = Path.GetFullPath(Path.Combine(Root, rel));
                if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                {
                    continue;
                }

                var name = Path.GetFileName(candidate);
                if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("_rig.json", StringComparison.OrdinalIgnoreCase))
                {
                    CopyOne(candidate, name);
                }
            }
        }

        static IEnumerable<string> CollectManifestSidecarPaths(string nodeId, HubModel model)
        {
            var results = new List<string>();
            if (string.IsNullOrEmpty(nodeId))
            {
                return results;
            }

            var manifestPath = Root + "/assets/" + nodeId + "/manifest.json";
            if (!File.Exists(manifestPath))
            {
                return results;
            }

            try
            {
                var doc = CellArtJson.AsDict(CellArtJson.Deserialize(File.ReadAllText(manifestPath, Encoding.UTF8)));
                var models = CellArtJson.AsList(doc != null && doc.TryGetValue("models", out var mv) ? mv : null);
                if (models == null)
                {
                    return results;
                }

                Dictionary<string, object> target = null;
                foreach (var item in models)
                {
                    var d = CellArtJson.AsDict(item);
                    if (d == null)
                    {
                        continue;
                    }

                    var id = CellArtJson.Str(d.TryGetValue("id", out var iv) ? iv : null);
                    var file = CellArtJson.Str(d.TryGetValue("file", out var fv) ? fv : null);
                    if ((model != null && !string.IsNullOrEmpty(model.id) && string.Equals(id, model.id, StringComparison.Ordinal)) ||
                        (model != null && !string.IsNullOrEmpty(model.file) && string.Equals(file, model.file, StringComparison.Ordinal)))
                    {
                        target = d;
                        break;
                    }

                    if (target == null && CellArtJson.Bool(d.TryGetValue("adopted", out var av) ? av : null))
                    {
                        target = d;
                    }
                }

                if (target != null && target.TryGetValue("sidecars", out var sv) && sv != null)
                {
                    var list = CellArtJson.AsList(sv);
                    if (list != null)
                    {
                        foreach (var s in list)
                        {
                            var rel = CellArtJson.Str(s);
                            if (!string.IsNullOrEmpty(rel))
                            {
                                results.Add(rel);
                            }
                        }
                    }
                    else
                    {
                        var dict = CellArtJson.AsDict(sv);
                        if (dict != null)
                        {
                            foreach (var kv in dict)
                            {
                                var rel = CellArtJson.Str(kv.Value);
                                if (!string.IsNullOrEmpty(rel))
                                {
                                    results.Add(rel);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FeatureArtHub] 读取 sidecars 失败（{manifestPath}）：{e.Message}");
            }

            return results;
        }
    }
}
