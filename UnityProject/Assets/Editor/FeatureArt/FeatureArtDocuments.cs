using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    [Serializable]
    public sealed class FeatureArtDocumentLink
    {
        public string path;
        public string role = "参考";
        public bool enabled = true;
        public bool applyToAll;
    }

    public static class FeatureArtDocuments
    {
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));

        public static string Resolve(string reference)
        {
            var path = PathOf(reference);
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new ArgumentException("文档路径必须相对工作台根目录。");
            var root = ProjectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var absolute = Path.GetFullPath(Path.Combine(root, path));
            if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("文档路径不能越出工作台目录。");
            return absolute;
        }

        public static string PathOf(string reference)
        {
            var value = (reference ?? "").Trim().Replace('\\', '/');
            var colon = value.LastIndexOf(':');
            return colon >= 0 && int.TryParse(value.Substring(colon + 1), out _) ? value.Substring(0, colon) : value;
        }

        public static string Read(string reference) => File.ReadAllText(Resolve(reference));

        public static void Open(string reference)
        {
            var path = Resolve(reference);
            if (!File.Exists(path)) throw new FileNotFoundException("文档不存在。", path);
            var value = reference ?? "";
            var colon = value.LastIndexOf(':');
            var line = colon >= 0 && int.TryParse(value.Substring(colon + 1), out var parsed) ? Math.Max(1, parsed) : 1;
            UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(path, line);
        }

        // Explicit refresh only: never scan directories or read full documents during an input event.
        public static int RefreshLinks(FeatureArtWorkspace workspace)
        {
            var candidates = new HashSet<string>(workspace.designDocuments.Select(PathOf), StringComparer.OrdinalIgnoreCase);
            foreach (var node in workspace.nodes) foreach (var source in node.designSources) candidates.Add(PathOf(source));
            foreach (var folder in workspace.documentRoots)
            {
                var absolute = Resolve(folder);
                if (!Directory.Exists(absolute)) continue;
                foreach (var file in Directory.EnumerateFiles(absolute, "*.md", SearchOption.AllDirectories))
                    candidates.Add(file.Substring(ProjectRoot.Length + 1).Replace('\\', '/'));
            }
            var known = new HashSet<string>(workspace.documentLinks.Select(l => l.path), StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var path in candidates.Where(p => !string.IsNullOrWhiteSpace(p)).OrderBy(p => p, StringComparer.Ordinal))
            {
                Resolve(path);
                if (known.Add(path))
                {
                    workspace.documentLinks.Add(new FeatureArtDocumentLink { path = path, role = path.Contains("/asset-cards/") ? "资产卡" : "参考" });
                    added++;
                }
                if (!workspace.designDocuments.Contains(path)) { workspace.designDocuments.Add(path); added++; }
                if (path.Contains("/asset-cards/"))
                {
                    var node = workspace.Find(Path.GetFileNameWithoutExtension(path));
                    if (node != null && !node.designSources.Any(s => PathOf(s) == path))
                    { node.designSources.Add(path); added++; }
                }
            }
            if (added > 0) workspace.MarkContentChanged();
            return added;
        }

        public static string[] EffectiveSources(FeatureArtWorkspace workspace, FeatureArtNode node)
        {
            var sources = new List<string>();
            foreach (var link in workspace.documentLinks) if (link.enabled && link.applyToAll) sources.Add(link.path);
            var seen = new HashSet<string>();
            while (node != null && seen.Add(node.id))
            {
                sources.AddRange(node.designSources);
                node = workspace.Find(node.parentId);
            }
            var disabled = new HashSet<string>(workspace.documentLinks.Where(l => !l.enabled).Select(l => l.path), StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return sources.Where(s => !disabled.Contains(PathOf(s)) && paths.Add(PathOf(s))).ToArray();
        }
    }

    public sealed class FeatureArtDocumentsPage
    {
        readonly FeatureArtBindingWindow _window;
        int _revision = -1, _selected;
        string[] _labels;
        string _roots, _newPath = "", _preview = "";
        bool _showPreview;
        Vector2 _scroll;
        readonly FeatureArtGui.TextArea _rootsArea = new FeatureArtGui.TextArea(), _previewArea = new FeatureArtGui.TextArea();
        public FeatureArtDocumentsPage(FeatureArtBindingWindow window) { _window = window; _roots = string.Join("\n", window.Workspace.documentRoots); }

        [Sirenix.OdinInspector.OnInspectorGUI]
        void Draw()
        {
            var workspace = _window.Workspace;
            FeatureArtGui.Label("设计文档：全局规范与每个节点的出处合并使用，读取原文时使用磁盘上的最新内容。");
            EditorGUILayout.HelpBox("探索资料默认仅供参考。勾选「作为所有出图任务的依据」才会全局应用；不会自动覆盖你编辑的提示词。", MessageType.Info);
            _roots = _rootsArea.Draw("自动发现目录（相对工作台，每行一个）", _roots);
            if (FeatureArtGui.Button("应用目录并刷新文档关联"))
            {
                try
                {
                    var roots = _roots.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Distinct().ToList();
                    foreach (var root in roots) FeatureArtDocuments.Resolve(root);
                    workspace.documentRoots = roots;
                    var count = FeatureArtDocuments.RefreshLinks(workspace);
                    _window.MarkWorkspaceDirty();
                    _window.Log("文档关联已刷新，新增 " + count + " 项。已有用途和节点说明保留。");
                }
                catch (Exception e) { _window.LogError(e.Message); }
            }
            _newPath = FeatureArtGui.Field("另加文档路径", _newPath, true);
            if (FeatureArtGui.Button("关联此文档"))
            {
                try
                {
                    var path = FeatureArtDocuments.PathOf(_newPath);
                    FeatureArtDocuments.Resolve(path);
                    if (!workspace.documentLinks.Any(l => l.path == path)) workspace.documentLinks.Add(new FeatureArtDocumentLink { path = path });
                    if (!workspace.designDocuments.Contains(path)) workspace.designDocuments.Add(path);
                    _window.MarkWorkspaceDirty();
                }
                catch (Exception e) { _window.LogError(e.Message); }
            }
            if (_revision != workspace.ContentRevision)
            {
                _revision = workspace.ContentRevision;
                _labels = workspace.documentLinks.Select(l => (l.enabled ? "" : "已停用 · ") + l.role + " · " + l.path).ToArray();
                _selected = Mathf.Clamp(_selected, 0, Math.Max(0, _labels.Length - 1));
            }
            if (_labels.Length == 0) return;
            var next = FeatureArtGui.Popup("关联文档 · " + _labels.Length + " 项", _selected, _labels);
            if (next != _selected) { _selected = next; _preview = ""; _showPreview = false; }
            var link = workspace.documentLinks[_selected];
            EditorGUI.BeginChangeCheck();
            link.role = FeatureArtGui.Field("用途（规范 / 设计 / 资产卡 / 探索等）", link.role);
            link.enabled = EditorGUILayout.Toggle("启用此关联", link.enabled);
            link.applyToAll = EditorGUILayout.Toggle("作为所有出图任务的依据", link.applyToAll);
            if (EditorGUI.EndChangeCheck()) _window.MarkWorkspaceDirty();
            FeatureArtGui.Label(link.path);
            if (FeatureArtGui.Button("打开完整原文")) Try(() => FeatureArtDocuments.Open(link.path));
            if (FeatureArtGui.Button("读取最新原文预览")) Try(() =>
            {
                var text = FeatureArtDocuments.Read(link.path);
                var lines = text.Split('\n');
                _preview = string.Join("\n", lines.Take(120));
                if (_preview.Length > 12000) _preview = _preview.Substring(0, 12000);
                _showPreview = true;
            });
            if (_showPreview)
            {
                FeatureArtGui.Label("原文预览（最多 120 行；完整内容可打开或由出图读取接口获取）");
                _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Width(FeatureArtGui.Width), GUILayout.Height(300));
                using (new FeatureArtGui.WidthScope(FeatureArtGui.Width - 20f))
                using (new EditorGUI.DisabledScope(true)) _previewArea.Draw("", _preview);
                GUILayout.EndScrollView();
            }
        }
        void Try(Action action) { try { action(); } catch (Exception e) { _window.LogError(e.Message); } }
    }
}
