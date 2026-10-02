using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinGames.EditorTools.CellArt;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    [Serializable]
    public sealed class FeatureArtImageTask
    {
        public string nodeId;
        public string stage = "概念图";
        public bool enabled = true;
        public int state; // 0 待制作，1 待确认，2 已完成，3 暂停
        public bool requiresReference;
        public string referenceNodeId = "";
    }

    [Serializable]
    public sealed class FeatureArtQueueSnapshot
    {
        public string designVersion;
        public bool unsavedWorkspace, unsavedCatalog, unsavedSources;
        public int total, offset;
        public List<FeatureArtTaskSnapshot> tasks = new List<FeatureArtTaskSnapshot>();
        public List<FeatureArtDocumentSnapshot> documents = new List<FeatureArtDocumentSnapshot>();
    }

    [Serializable]
    public sealed class FeatureArtTaskSnapshot
    {
        public int position;
        public string nodeId, title, stage, state, nodeKind, designId, faction, sourceId, slotId, notes, prompt;
        public string purpose, howTo, expected, constraints, look;
        public string sourceNotes, modelNotes, currentImagePath;
        public bool enabled, requiresReference, ready;
        public string referenceNodeId, referenceImagePath;
        public string[] documentReferences, dependencyIds, blockingReasons;
        public List<FeatureArtRequirementSnapshot> contextRequirements;
    }

    [Serializable]
    public sealed class FeatureArtRequirementSnapshot
    {
        public string nodeId, title, kind, notes;
        public string[] documentReferences;
    }

    [Serializable]
    public sealed class FeatureArtDocumentSnapshot
    {
        public string path, role, text, error;
    }

    /// <summary>只读当前编辑器数据；调用者按 ready 和 blockingReasons 决定是否制作，读取不会启动生成。</summary>
    public static class FeatureArtProduction
    {
        public static readonly string[] States = { "待制作", "待确认", "已完成", "暂停" };

        public static FeatureArtImageTask Add(FeatureArtWorkspace workspace, string nodeId, string stage = "概念图")
        {
            if (workspace.Find(nodeId) == null) throw new ArgumentException("资源节点不存在。");
            var existing = workspace.imageTasks.FirstOrDefault(t => t.nodeId == nodeId);
            if (existing != null) return existing;
            var task = new FeatureArtImageTask { nodeId = nodeId, stage = stage };
            workspace.imageTasks.Add(task);
            workspace.MarkContentChanged();
            return task;
        }

        public static void Move(FeatureArtWorkspace workspace, string nodeId, int targetIndex)
        {
            var index = workspace.imageTasks.FindIndex(t => t.nodeId == nodeId);
            if (index < 0) throw new ArgumentException("任务不存在。");
            targetIndex = Mathf.Clamp(targetIndex, 0, workspace.imageTasks.Count - 1);
            if (targetIndex == index) return;
            var task = workspace.imageTasks[index];
            workspace.imageTasks.RemoveAt(index);
            workspace.imageTasks.Insert(targetIndex, task);
            workspace.MarkContentChanged();
        }

        public static string ReferenceImage(FeatureArtBindingWindow window, string nodeId)
        {
            var node = window.Workspace.Find(nodeId);
            var asset = window.FindRegistryAsset(node?.sourceId);
            return string.IsNullOrEmpty(asset?.concept) ? "" : CellArtRegistryService.AbsOf(asset.concept) ?? "";
        }

        public static FeatureArtTaskSnapshot Describe(FeatureArtBindingWindow window, FeatureArtImageTask task, int position)
        {
            var workspace = window.Workspace;
            var node = workspace.Find(task.nodeId);
            var slot = window.FindSlot(node?.slotId);
            var sourceAsset = window.FindRegistryAsset(node?.sourceId);
            var blocked = new List<string>();
            if (!task.enabled) blocked.Add("任务已停用");
            if (task.state != 0) blocked.Add("当前状态为" + States[task.state]);
            if (node == null) blocked.Add("资源节点不存在");
            else
            {
                var ancestor = node;
                var seen = new HashSet<string>();
                while (ancestor != null && seen.Add(ancestor.id))
                {
                    if (ancestor.archived) { blocked.Add("资源或上级已归档"); break; }
                    ancestor = workspace.Find(ancestor.parentId);
                }
                if (string.IsNullOrEmpty(node.sourceId)) blocked.Add("尚未指定源登记标识");
                foreach (var dependency in node.dependencyIds)
                {
                    var dependencyTask = workspace.imageTasks.FirstOrDefault(t => t.nodeId == dependency);
                    if (dependencyTask != null && dependencyTask.state != 2)
                        blocked.Add("等待依赖完成：" + (workspace.Find(dependency)?.title ?? dependency));
                }
            }
            var referencePath = ReferenceImage(window, task.referenceNodeId);
            if (task.requiresReference && string.IsNullOrEmpty(task.referenceNodeId)) blocked.Add("需要先指定已确认的参考图节点");
            if (!string.IsNullOrEmpty(task.referenceNodeId))
            {
                var referenceTask = workspace.imageTasks.FirstOrDefault(t => t.nodeId == task.referenceNodeId);
                if (referenceTask != null && referenceTask.state != 2) blocked.Add("参考图任务尚未确认完成");
                if (string.IsNullOrEmpty(referencePath) || !File.Exists(referencePath)) blocked.Add("参考节点尚无有效概念图文件");
            }
            var context = new List<FeatureArtRequirementSnapshot>();
            var contextIds = new HashSet<string>();
            var parent = workspace.Find(node?.parentId);
            while (parent != null && contextIds.Add(parent.id))
            {
                context.Add(Context(workspace, parent));
                parent = workspace.Find(parent.parentId);
            }
            foreach (var id in node?.dependencyIds ?? new List<string>())
            {
                var dependency = workspace.Find(id);
                if (dependency != null && contextIds.Add(id)) context.Add(Context(workspace, dependency));
            }
            var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = FeatureArtDocuments.EffectiveSources(workspace, node).Concat(context.SelectMany(c => c.documentReferences))
                .Where(s => sourcePaths.Add(FeatureArtDocuments.PathOf(s))).ToArray();
            foreach (var source in sources)
            {
                try { if (!File.Exists(FeatureArtDocuments.Resolve(source))) blocked.Add("缺少文档：" + source); }
                catch (Exception e) { blocked.Add("文档路径无效：" + e.Message); }
            }
            return new FeatureArtTaskSnapshot
            {
                position = position, nodeId = task.nodeId, title = node?.title, stage = task.stage, state = States[task.state],
                nodeKind = node?.kind, designId = node?.designId, faction = node?.faction, sourceId = node?.sourceId,
                slotId = node?.slotId, notes = node?.notes, prompt = slot?.prompt, purpose = slot?.purpose,
                howTo = slot?.howTo, expected = slot?.expected, constraints = slot?.constraints, look = slot?.look,
                sourceNotes = sourceAsset?.notes, modelNotes = sourceAsset?.tripo_notes, currentImagePath = ReferenceImage(window, task.nodeId),
                enabled = task.enabled, requiresReference = task.requiresReference, ready = blocked.Count == 0,
                referenceNodeId = task.referenceNodeId, referenceImagePath = referencePath,
                documentReferences = sources, dependencyIds = node?.dependencyIds.ToArray() ?? Array.Empty<string>(), blockingReasons = blocked.ToArray(),
                contextRequirements = context,
            };
        }

        static FeatureArtRequirementSnapshot Context(FeatureArtWorkspace workspace, FeatureArtNode node) => new FeatureArtRequirementSnapshot
        {
            nodeId = node.id, title = node.title, kind = node.kind, notes = node.notes,
            documentReferences = FeatureArtDocuments.EffectiveSources(workspace, node),
        };

        public static FeatureArtQueueSnapshot Read(FeatureArtBindingWindow window, int offset, int limit, bool includeDocumentText)
        {
            if (window == null) throw new InvalidOperationException("请先打开美术资源与绑定编辑器。");
            var workspace = window.Workspace;
            workspace.Validate();
            var result = new FeatureArtQueueSnapshot
            {
                designVersion = workspace.designVersion, total = workspace.imageTasks.Count, offset = Math.Max(0, offset),
                unsavedWorkspace = window.IsWorkspaceDirty, unsavedCatalog = window.IsCatalogDirty, unsavedSources = window.IsRegistryDirty,
            };
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in workspace.imageTasks.Skip(result.offset).Take(Math.Max(0, limit)))
            {
                var entry = Describe(window, task, result.offset + result.tasks.Count + 1);
                result.tasks.Add(entry);
                foreach (var source in entry.documentReferences)
                {
                    var path = FeatureArtDocuments.PathOf(source);
                    if (!paths.Add(path)) continue;
                    var link = workspace.documentLinks.FirstOrDefault(l => string.Equals(l.path, path, StringComparison.OrdinalIgnoreCase));
                    var document = new FeatureArtDocumentSnapshot { path = path, role = link?.role ?? "节点出处" };
                    if (includeDocumentText)
                    {
                        try { document.text = FeatureArtDocuments.Read(path); }
                        catch (Exception e) { document.error = e.Message; }
                    }
                    result.documents.Add(document);
                }
            }
            return result;
        }

        // Unity MCP 可分批调用；原文不受界面预览的行数限制，未保存的节点/提示词修改也会读取。
        public static string ReadCurrentQueueJson(int offset = 0, int limit = 10, bool includeDocumentText = false)
        {
            var window = Resources.FindObjectsOfTypeAll<FeatureArtBindingWindow>().FirstOrDefault();
            return JsonUtility.ToJson(Read(window, offset, limit, includeDocumentText), true);
        }
    }
}
