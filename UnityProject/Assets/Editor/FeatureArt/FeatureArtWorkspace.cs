using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameLogic.ArtBinding;
using BinGames.EditorTools.CellArt;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    [Serializable]
    public sealed class FeatureArtNode
    {
        public string id;
        public string parentId = "";
        public string title = "新资源";
        public string kind = "部件";
        public string sourceId = "";
        public string slotId = "";
        public string notes = "";
        public string designId = "";
        public string faction = "";
        public string requirementStatus = "待制作";
        public List<string> designSources = new List<string>();
        public List<string> dependencyIds = new List<string>();
        public bool archived;
    }

    /// <summary>仅供编辑器使用。层级、源目录和资源数量不参与运行时契约。</summary>
    [Serializable]
    public sealed class FeatureArtWorkspace
    {
        public int version = 1;
        public string sourceRoot = "Assets/GameRes/Art";
        public string outputFolder = "Assets/GameRes/Raw/Actor/Machine";
        public string designVersion = "";
        public List<string> designDocuments = new List<string>();
        public List<string> documentRoots = new List<string> { "production/design/art-rules" };
        public List<FeatureArtDocumentLink> documentLinks = new List<FeatureArtDocumentLink>();
        public List<FeatureArtImageTask> imageTasks = new List<FeatureArtImageTask>();
        public bool autoRepairPackages;
        public List<FeatureArtNode> nodes = new List<FeatureArtNode>();

        [NonSerialized] Dictionary<string, FeatureArtNode> _byId;
        [NonSerialized] Dictionary<string, List<FeatureArtNode>> _children;
        [NonSerialized] Dictionary<string, string> _labels;
        [NonSerialized] Dictionary<string, string> _paths;
        [NonSerialized] List<FeatureArtNode> _indexedNodes;
        [NonSerialized] int _indexedCount;
        [NonSerialized] int _revision;
        [NonSerialized] int _contentRevision;
        public int ContentRevision => _contentRevision;
        public void MarkContentChanged() => _contentRevision++;

        public int StructureRevision { get { EnsureIndex(); return _revision; } }

        public void InvalidateStructure() => _byId = null;

        void EnsureIndex()
        {
            if (_byId != null && ReferenceEquals(_indexedNodes, nodes) && _indexedCount == nodes.Count) return;
            _byId = new Dictionary<string, FeatureArtNode>(nodes.Count, StringComparer.Ordinal);
            _children = new Dictionary<string, List<FeatureArtNode>>(StringComparer.Ordinal);
            _labels = new Dictionary<string, string>(nodes.Count, StringComparer.Ordinal);
            _paths = new Dictionary<string, string>(nodes.Count, StringComparer.Ordinal);
            var names = new Dictionary<(string, string), int>();
            foreach (var node in nodes)
            {
                _byId[node.id] = node;
                var parentId = node.parentId ?? "";
                if (!_children.TryGetValue(parentId, out var children)) _children[parentId] = children = new List<FeatureArtNode>();
                children.Add(node);
                var key = (parentId, node.title ?? "");
                names.TryGetValue(key, out var count);
                names[key] = count + 1;
            }
            foreach (var node in nodes)
            {
                var suffix = names[(node.parentId ?? "", node.title ?? "")] > 1 ? node.id : node.id.Substring(Math.Max(0, node.id.Length - 6));
                _labels[node.id] = (node.title ?? "未命名").Replace('/', '／').Replace('\\', '＼') + " · " + suffix;
            }
            _indexedNodes = nodes;
            _indexedCount = nodes.Count;
            _revision++;
        }

        public FeatureArtNode Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            EnsureIndex();
            return _byId.TryGetValue(id, out var node) ? node : null;
        }

        public IReadOnlyList<FeatureArtNode> Children(string id)
        {
            EnsureIndex();
            return _children.TryGetValue(id ?? "", out var children) ? children : (IReadOnlyList<FeatureArtNode>)Array.Empty<FeatureArtNode>();
        }

        public FeatureArtNode Add(string parentId, string title, string kind)
        {
            if (!string.IsNullOrEmpty(parentId) && Find(parentId) == null)
                throw new ArgumentException("上级资源不存在。");
            var node = new FeatureArtNode
            {
                id = "art_" + Guid.NewGuid().ToString("N"), parentId = parentId ?? "",
                title = title, kind = kind,
            };
            nodes.Add(node);
            InvalidateStructure();
            return node;
        }

        public bool CanParent(string id, string parentId)
        {
            var seen = new HashSet<string> { id };
            while (!string.IsNullOrEmpty(parentId))
            {
                if (!seen.Add(parentId)) return false;
                var parent = Find(parentId);
                if (parent == null) return false;
                parentId = parent.parentId;
            }
            return true;
        }

        public string MenuPath(FeatureArtNode node)
        {
            if (node == null) return "资源树/";
            EnsureIndex();
            if (_paths.TryGetValue(node.id, out var cached)) return cached;
            var pending = new Stack<FeatureArtNode>();
            var seen = new HashSet<string>();
            var prefix = "资源树";
            while (node != null)
            {
                if (_paths.TryGetValue(node.id, out cached)) { prefix = cached; break; }
                if (!seen.Add(node.id)) throw new InvalidDataException("资源层级形成循环。");
                pending.Push(node);
                node = Find(node.parentId);
            }
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                prefix += "/" + _labels[current.id];
                _paths[current.id] = prefix;
            }
            return prefix;
        }

        public void Validate()
        {
            if (version != 1) throw new InvalidDataException("资源树版本不受支持，未覆盖原配置。");
            nodes ??= new List<FeatureArtNode>();
            designDocuments ??= new List<string>();
            documentRoots ??= new List<string> { "production/design/art-rules" };
            documentLinks ??= new List<FeatureArtDocumentLink>();
            imageTasks ??= new List<FeatureArtImageTask>();
            if (nodes.Any(n => n == null || string.IsNullOrEmpty(n.id)) ||
                nodes.Select(n => n.id).Distinct(StringComparer.Ordinal).Count() != nodes.Count)
                throw new InvalidDataException("资源树存在空节点或重复 id，未覆盖原配置。");
            foreach (var node in nodes)
            {
                if (!CanParent(node.id, node.parentId))
                    throw new InvalidDataException("资源树上级不存在或形成循环：" + node.title);
                node.designSources ??= new List<string>();
                node.dependencyIds ??= new List<string>();
                if (node.dependencyIds.Any(id => id == node.id || Find(id) == null))
                    throw new InvalidDataException("资源依赖不存在或指向自身：" + node.title);
            }
            sourceRoot = ValidateFolder(sourceRoot, "Assets/GameRes/Art");
            outputFolder = ValidateFolder(outputFolder, "Assets/GameRes/Raw");
            if (imageTasks.Any(t => t == null || Find(t.nodeId) == null || t.state < 0 || t.state > 3) ||
                imageTasks.Select(t => t.nodeId).Distinct().Count() != imageTasks.Count)
                throw new InvalidDataException("出图顺序存在重复、缺失节点或无效状态。");
            foreach (var task in imageTasks)
                if (!string.IsNullOrEmpty(task.referenceNodeId) && (task.referenceNodeId == task.nodeId || Find(task.referenceNodeId) == null))
                    throw new InvalidDataException("出图参考节点不存在或指向自身：" + task.nodeId);
            foreach (var root in documentRoots) FeatureArtDocuments.Resolve(root);
            foreach (var link in documentLinks)
                if (link == null || string.IsNullOrWhiteSpace(link.path)) throw new InvalidDataException("文档关联缺少路径。");
                else FeatureArtDocuments.Resolve(link.path);
            if (documentLinks.Select(l => l.path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != documentLinks.Count)
                throw new InvalidDataException("文档关联路径重复。");
        }

        public static string ValidateFolder(string path, string root)
        {
            var normalized = (path ?? "").Replace('\\', '/').Trim().TrimEnd('/');
            if (!(normalized == root || normalized.StartsWith(root + "/", StringComparison.Ordinal)) ||
                normalized.Split('/').Any(p => string.IsNullOrWhiteSpace(p) || p == "." || p == ".." ||
                    p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new ArgumentException("目录必须位于 " + root + " 下，且不能包含路径跳转。");
            return normalized;
        }

        /// <summary>只补尚未呈现的槽；不改槽、说明、绑定、用户结构或归档状态。</summary>
        public int IncludeCatalog(FeatureArtCatalogData catalog)
        {
            var added = 0;
            var represented = new HashSet<string>(nodes.Select(n => n.slotId), StringComparer.Ordinal);
            var groups = nodes.ToDictionary(n => n.id, StringComparer.Ordinal);
            foreach (var slot in catalog?.slots ?? Enumerable.Empty<FeatureArtSlot>())
            {
                if (slot == null || !represented.Add(slot.id)) continue;
                const string importedRoot = "imported_bindings";
                if (!groups.ContainsKey(importedRoot))
                {
                    var root = new FeatureArtNode { id = importedRoot, title = "已有绑定（可调整或归档）", kind = "分类" };
                    nodes.Add(root);
                    groups[root.id] = root;
                }
                var domainId = "import_domain_" + slot.domain;
                if (!groups.TryGetValue(domainId, out var domain))
                {
                    domain = new FeatureArtNode { id = domainId, parentId = importedRoot, title = slot.domain ?? "未分类", kind = "分类" };
                    nodes.Add(domain);
                    groups[domain.id] = domain;
                }
                nodes.Add(new FeatureArtNode
                {
                    id = "import_slot_" + slot.id, parentId = domain.id, title = slot.titleZh ?? slot.id,
                    kind = "资源", slotId = slot.id, archived = slot.retired,
                });
                added++;
            }
            if (added > 0) InvalidateStructure();
            return added;
        }

        /// <summary>只移除登记关系；实际图片和模型文件不受影响。</summary>
        public List<string> RemoveBranch(string id)
        {
            var removed = new HashSet<string> { id };
            bool changed;
            do
            {
                changed = false;
                foreach (var node in nodes)
                    if (removed.Contains(node.parentId) && removed.Add(node.id)) changed = true;
            }
            while (changed);
            var slots = nodes.Where(n => removed.Contains(n.id) && !string.IsNullOrEmpty(n.slotId))
                .Select(n => n.slotId).Distinct().ToList();
            nodes.RemoveAll(n => removed.Contains(n.id));
            foreach (var node in nodes) node.dependencyIds.RemoveAll(removed.Contains);
            imageTasks.RemoveAll(t => removed.Contains(t.nodeId));
            foreach (var task in imageTasks) if (removed.Contains(task.referenceNodeId)) task.referenceNodeId = "";
            MarkContentChanged();
            InvalidateStructure();
            return slots.Where(slot => !nodes.Any(n => n.slotId == slot)).ToList();
        }
    }

    public static class FeatureArtWorkspaceStore
    {
        public const string RelativePath = "ProjectSettings/FeatureArtWorkspace.json";
        public static string AbsolutePath => Path.Combine(Path.GetDirectoryName(Application.dataPath), RelativePath);
        static FeatureArtWorkspace _current;
        static string _loadedJson;
        public static FeatureArtWorkspace Current => _current ??= Load();

        public static FeatureArtWorkspace Load()
        {
            var json = File.Exists(AbsolutePath) ? File.ReadAllText(AbsolutePath) : null;
            var data = json == null ? new FeatureArtWorkspace() : JsonUtility.FromJson<FeatureArtWorkspace>(json);
            if (data == null) throw new InvalidDataException("无法读取资源树配置。");
            data.Validate();
            _loadedJson = json;
            CellArtRegistryService.SourceRoot = data.sourceRoot;
            return _current = data;
        }

        public static void Save()
        {
            Current.Validate();
            var json = File.Exists(AbsolutePath) ? File.ReadAllText(AbsolutePath) : null;
            if (!string.Equals(json, _loadedJson, StringComparison.Ordinal))
                throw new IOException("资源树配置已被其他程序修改，请刷新后再保存。");
            var next = JsonUtility.ToJson(Current, true) + "\n";
            File.WriteAllText(AbsolutePath, next);
            _loadedJson = next;
            CellArtRegistryService.SourceRoot = Current.sourceRoot;
        }
    }
}
