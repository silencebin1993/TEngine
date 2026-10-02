using System;
using System.Linq;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    public sealed class FeatureArtProductionPage
    {
        readonly FeatureArtBindingWindow _window;
        int _revision = -1, _structureRevision = -1, _addIndex, _checkRevision = -1, _catalogRevision = -1, _registryRevision = -1, _assetRevision = -1;
        string _selectedId, _search = "", _cachedSearch;
        string[] _nodeIds, _nodeLabels, _rowLabels, _referenceIds, _referenceLabels;
        int[] _visible;
        Vector2 _scroll;
        FeatureArtTaskSnapshot _check;

        public FeatureArtProductionPage(FeatureArtBindingWindow window) => _window = window;

        void Prepare()
        {
            var workspace = _window.Workspace;
            if (_structureRevision != workspace.StructureRevision)
            {
                _structureRevision = workspace.StructureRevision;
                _nodeIds = workspace.nodes.Select(n => n.id).ToArray();
                _nodeLabels = workspace.nodes.Select(n => n.title + " · " + n.id).ToArray();
                _referenceIds = new[] { "" }.Concat(_nodeIds).ToArray();
                _referenceLabels = new[] { "无参考节点" }.Concat(_nodeLabels).ToArray();
                _addIndex = Mathf.Clamp(_addIndex, 0, Math.Max(0, _nodeIds.Length - 1));
                _revision = -1;
            }
            if (_revision == workspace.ContentRevision && _cachedSearch == _search) return;
            _revision = workspace.ContentRevision;
            _cachedSearch = _search;
            _rowLabels = workspace.imageTasks.Select((t, i) => (i + 1) + ". " + (workspace.Find(t.nodeId)?.title ?? t.nodeId) + "\n" + t.stage + " · " + FeatureArtProduction.States[t.state] + (t.enabled ? "" : " · 已停用")).ToArray();
            _visible = Enumerable.Range(0, _rowLabels.Length).Where(i => string.IsNullOrWhiteSpace(_search) || _rowLabels[i].IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (_selectedId == null || !workspace.imageTasks.Any(t => t.nodeId == _selectedId)) _selectedId = workspace.imageTasks.FirstOrDefault()?.nodeId;
        }

        [OnInspectorGUI]
        void Draw()
        {
            Prepare();
            var workspace = _window.Workspace;
            FeatureArtGui.Label("出图顺序 · " + workspace.imageTasks.Count + " 项（按此顺序读取，可自由增减与调整）");
            EditorGUILayout.HelpBox("先确认母版和主体图，再做依赖它们的部件。修改顺序不会跳过参考图检查；缺图的任务会显示等待原因。", MessageType.Info);
            _search = FeatureArtGui.Field("搜索任务", _search);
            DrawRows();
            var selected = workspace.imageTasks.FirstOrDefault(t => t.nodeId == _selectedId);
            if (selected != null) DrawSelected(selected);
            EditorGUILayout.Space();
            FeatureArtGui.Label("从资源树添加任务（类型与数量由你决定）");
            if (_nodeIds.Length > 0)
            {
                _addIndex = FeatureArtGui.Popup("资源节点", _addIndex, _nodeLabels);
                if (FeatureArtGui.Button("加入出图顺序"))
                {
                    var task = FeatureArtProduction.Add(workspace, _nodeIds[_addIndex]);
                    _selectedId = task.nodeId;
                    _window.MarkWorkspaceDirty();
                }
            }
            if (FeatureArtGui.Button("复制完整任务顺序（不包含全文）"))
            {
                try { EditorGUIUtility.systemCopyBuffer = FeatureArtProduction.ReadCurrentQueueJson(0, workspace.imageTasks.Count); _window.Log("已复制当前顺序、说明、文档路径与等待原因。"); }
                catch (Exception e) { _window.LogError(e.Message); }
            }
        }

        void DrawRows()
        {
            const float rowHeight = 48f;
            const float height = 250f;
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Width(FeatureArtGui.Width), GUILayout.Height(height));
            var rect = GUILayoutUtility.GetRect(FeatureArtGui.Width - 20f, _visible.Length * rowHeight);
            var first = Mathf.Max(0, Mathf.FloorToInt(_scroll.y / rowHeight) - 1);
            var last = Math.Min(_visible.Length, first + Mathf.CeilToInt(height / rowHeight) + 3);
            for (var row = first; row < last; row++)
            {
                var index = _visible[row];
                var task = _window.Workspace.imageTasks[index];
                var button = new Rect(rect.x, rect.y + row * rowHeight, rect.width, rowHeight - 3);
                var previous = GUI.backgroundColor;
                if (task.nodeId == _selectedId) GUI.backgroundColor = new Color(0.5f, 0.7f, 1f);
                if (GUI.Button(button, _rowLabels[index], FeatureArtGui.ButtonStyle)) { _selectedId = task.nodeId; _check = null; }
                GUI.backgroundColor = previous;
            }
            GUILayout.EndScrollView();
        }

        void DrawSelected(FeatureArtImageTask task)
        {
            var workspace = _window.Workspace;
            var node = workspace.Find(task.nodeId);
            var position = workspace.imageTasks.IndexOf(task);
            FeatureArtGui.Label("当前任务：" + node?.title);
            EditorGUILayout.LabelField("位置（1～" + workspace.imageTasks.Count + "）");
            var target = EditorGUILayout.DelayedIntField(position + 1, GUILayout.Width(FeatureArtGui.Width));
            if (target != position + 1) { FeatureArtProduction.Move(workspace, task.nodeId, target - 1); _window.MarkWorkspaceDirty(); }
            using (new EditorGUILayout.HorizontalScope(GUILayout.Width(FeatureArtGui.Width)))
            {
                using (new EditorGUI.DisabledScope(position == 0)) if (GUILayout.Button("上移")) Move(task, position - 1);
                using (new EditorGUI.DisabledScope(position == workspace.imageTasks.Count - 1)) if (GUILayout.Button("下移")) Move(task, position + 1);
            }
            EditorGUI.BeginChangeCheck();
            task.enabled = EditorGUILayout.Toggle("启用此任务", task.enabled);
            task.stage = FeatureArtGui.Field("制作阶段（自定义）", task.stage);
            task.state = FeatureArtGui.Popup("制作状态", task.state, FeatureArtProduction.States);
            task.requiresReference = EditorGUILayout.Toggle("必须使用已确认的参考图", task.requiresReference);
            var current = Math.Max(0, Array.IndexOf(_referenceIds, task.referenceNodeId ?? ""));
            var next = FeatureArtGui.Popup("参考图来自资源节点", current, _referenceLabels);
            if (next != current)
            {
                if (_referenceIds[next] == task.nodeId) _window.LogError("不能把任务自身作为参考图。");
                else task.referenceNodeId = _referenceIds[next];
            }
            if (EditorGUI.EndChangeCheck()) _window.MarkWorkspaceDirty();
            if (_check == null || _checkRevision != workspace.ContentRevision || _catalogRevision != _window.CatalogRevision || _registryRevision != _window.RegistryRevision || _assetRevision != FeatureArtAssetCache.Revision)
            {
                _check = FeatureArtProduction.Describe(_window, task, workspace.imageTasks.IndexOf(task) + 1);
                _checkRevision = workspace.ContentRevision;
                _catalogRevision = _window.CatalogRevision;
                _registryRevision = _window.RegistryRevision;
                _assetRevision = FeatureArtAssetCache.Revision;
            }
            FeatureArtGui.Label(_check.ready ? "可开始制作" : string.Join("\n", _check.blockingReasons));
            if (FeatureArtGui.Button("重新检查参考图和文档文件")) _check = null;
            if (FeatureArtGui.Button("打开资源说明与绑定")) _window.SelectWorkspaceNode(node);
            if (FeatureArtGui.Button("从制作顺序移除此项"))
            {
                workspace.imageTasks.Remove(task);
                _window.MarkWorkspaceDirty();
                _check = null;
                _window.Log("已移除队列项，资源节点与文件保留。");
            }
        }

        void Move(FeatureArtImageTask task, int index) { FeatureArtProduction.Move(_window.Workspace, task.nodeId, index); _window.MarkWorkspaceDirty(); }
        public void Select(string nodeId) { _selectedId = nodeId; _search = ""; _check = null; }
    }
}
