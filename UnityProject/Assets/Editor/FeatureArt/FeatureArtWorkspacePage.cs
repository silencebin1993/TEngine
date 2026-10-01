using System;
using System.Collections.Generic;
using System.Linq;
using BinGames.EditorTools.CellArt;
using GameLogic.ArtBinding;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>数据驱动的设计需求、资源树和成品绑定管理。</summary>
    public sealed class FeatureArtWorkspacePage
    {
        readonly FeatureArtBindingWindow _window;
        readonly FeatureArtNode _node;
        string _newTitle = "新资源";
        string _newKind = "部件";
        string _sourceRoot;
        string _outputFolder;
        internal string NodeId => _node?.id ?? "";
        readonly Dictionary<string, FeatureArtGui.TextArea> _textAreas = new Dictionary<string, FeatureArtGui.TextArea>();
        int _choicesRevision = -1, _dependencyCount = -1, _slotRevision = -1;
        string _choiceSlotId;
        string[] _parentIds, _parentLabels, _dependencyIds, _dependencyLabels, _slotIds, _slotLabels;
        List<FeatureArtNode> _children, _archived;
        List<string> _sourceList;
        string _sourceText;
        static readonly string[] BindKinds = { "InstancedMesh", "PooledPrefab", "MaterialOverride", "Image", "Texture", "AnimationClip", "AudioClip", "AssetReference" };
        static readonly string[] BindLabels = { "模型 / 网格 / 预制体", "池化特效预制体", "材质", "图标 / 图片", "贴图 / VAT", "动画片段", "音频", "其他资源" };

        public FeatureArtWorkspacePage(FeatureArtBindingWindow window, FeatureArtNode node = null)
        {
            _window = window;
            _node = node;
            _sourceRoot = window.Workspace.sourceRoot;
            _outputFolder = window.Workspace.outputFolder;
        }

        [OnInspectorGUI]
        void Draw()
        {
            PrepareChoices();
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(FeatureArtGui.Width)))
            {
                if (_node == null) DrawWorkspace();
                else DrawNode();
                DrawAdd();
            }
        }

        void DrawWorkspace()
        {
            EditorGUILayout.LabelField("资源树与目录", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("自由建立分类、概念图、部件、状态和成品的上下级关系。需求已按现行设计登记；数量和层数由你决定，素材缺失可留空。", MessageType.Info);
            FeatureArtGui.Label("设计基线：" + _window.Workspace.designVersion);
            _sourceRoot = FeatureArtGui.Field("源文件根目录", _sourceRoot);
            _outputFolder = FeatureArtGui.Field("新成品默认目录", _outputFolder);
            if (GUILayout.Button("应用目录设置"))
                _window.ApplyWorkspaceDirectories(_sourceRoot, _outputFolder);
            EditorGUILayout.HelpBox("更换源目录会读取该目录的 registry.json；缺少登记表时可从空表开始。不会移动资源。每个成品槽也可设置自己的输出目录。", MessageType.None);

            EditorGUI.BeginChangeCheck();
            _window.Workspace.autoRepairPackages = EditorGUILayout.Toggle("编译后自动修复整包材质", _window.Workspace.autoRepairPackages);
            if (EditorGUI.EndChangeCheck()) Changed(false);
            using (new EditorGUILayout.VerticalScope())
            {
                if (GUILayout.Button("补入未呈现的绑定槽"))
                {
                    var count = _window.Workspace.IncludeCatalog(_window.Data);
                    Changed(true);
                    _window.Log("已补入 " + count + " 个绑定槽；已有结构和说明保留。");
                }
                if (GUILayout.Button("打开源文件库")) _window.JumpToSourceLibrary();
                if (GUILayout.Button("健康检查")) _window.RunHealthCheck();
            }

            if (GUILayout.Button("补入源登记节点"))
            {
                var count = 0;
                foreach (var asset in _window.Registry?.assets ?? new List<CellArtAsset>())
                {
                    if (_window.Workspace.nodes.Any(n => n.sourceId == asset.id)) continue;
                    var node = _window.Workspace.Add("", asset.name_zh ?? asset.id, asset.kind ?? "资源");
                    node.sourceId = asset.id;
                    count++;
                }
                Changed(true);
                _window.Log("已补入 " + count + " 个源登记节点，可自行调整上下级。");
            }

            var registry = _window.Registry;
            if (registry != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("扫盘子目录（相对源文件根目录）", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                registry.dirs.concepts = FeatureArtGui.Field("概念图", registry.dirs.concepts);
                registry.dirs.meshes = FeatureArtGui.Field("模型", registry.dirs.meshes);
                registry.dirs.animations = FeatureArtGui.Field("动画", registry.dirs.animations);
                registry.dirs.vfx = FeatureArtGui.Field("特效", registry.dirs.vfx);
                registry.dirs.previews = FeatureArtGui.Field("预览", registry.dirs.previews);
                if (EditorGUI.EndChangeCheck()) _window.MarkRegistryDirty();
            }
            DrawArchived();
        }

        void DrawArchived()
        {
            if (_archived.Count == 0) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("已归档节点", EditorStyles.boldLabel);
            foreach (var node in _archived)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(node.title);
                    if (GUILayout.Button("恢复", GUILayout.Width(60)))
                    {
                        node.archived = false;
                        Changed(true);
                    }
                }
        }

        void DrawNode()
        {
            EditorGUILayout.LabelField(_node.title, EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true)) FeatureArtGui.Field("稳定标识", _node.id);
            EditorGUI.BeginChangeCheck();
            var title = FeatureArtGui.Field("名称", _node.title);
            if (title != _node.title) { _node.title = title; _window.Workspace.InvalidateStructure(); }
            _node.kind = FeatureArtGui.Field("类型（自定义）", _node.kind);
            _node.designId = FeatureArtGui.Field("设计标识", _node.designId);
            _node.faction = FeatureArtGui.Field("阵营", _node.faction);
            _node.requirementStatus = FeatureArtGui.Field("需求状态", _node.requirementStatus);
            _node.notes = TextArea("制作说明", _node.notes);
            if (!ReferenceEquals(_sourceList, _node.designSources))
            { _sourceList = _node.designSources; _sourceText = string.Join("\n", _sourceList); }
            var sourceText = TextArea("设计出处（每行一项）", _sourceText);
            if (sourceText != _sourceText)
            {
                _sourceText = sourceText;
                _sourceList = _node.designSources = sourceText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
            }
            if (EditorGUI.EndChangeCheck()) Changed(false);
            EditorGUILayout.LabelField("依赖资源", EditorStyles.boldLabel);
            for (var i = 0; i < _node.dependencyIds.Count; i++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    var dependencyId = _node.dependencyIds[i];
                    var dependency = _window.Workspace.Find(dependencyId);
                    if (GUILayout.Button(dependency?.title ?? dependencyId)) _window.SelectWorkspaceNode(dependency);
                    if (GUILayout.Button("移除依赖", GUILayout.Width(80))) { _node.dependencyIds.RemoveAt(i--); _dependencyCount = -1; Changed(false); }
                }
            var addDependency = FeatureArtGui.Popup("添加依赖", 0, _dependencyLabels);
            if (addDependency > 0) { _node.dependencyIds.Add(_dependencyIds[addDependency - 1]); _dependencyCount = -1; Changed(false); }
            DrawParent();
            using (new EditorGUILayout.VerticalScope())
            {
                if (GUILayout.Button("更新树中名称")) _window.RebuildWorkspaceTree();
                if (GUILayout.Button("归档此节点"))
                {
                    _node.archived = true;
                    Changed(true);
                    _window.Log("已归档节点及其树中分支；绑定和文件保留，可在资源树管理页恢复。");
                }
                if (GUILayout.Button("删除登记分支"))
                {
                    if (EditorUtility.DisplayDialog("删除资源登记", "删除此节点、下级登记及不再被引用的成品槽？实际图片、模型和源登记文件保留。", "删除登记", "取消"))
                    {
                        var removedSlots = _window.Workspace.RemoveBranch(_node.id);
                        _window.Data.slots.RemoveAll(s => removedSlots.Contains(s.id));
                        _window.MarkDirty();
                        Changed(true);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            EditorGUILayout.Space();
            DrawSource();
            EditorGUILayout.Space();
            DrawSlot();
            if (_children.Count > 0)
            {
                EditorGUILayout.LabelField("下级资源", EditorStyles.boldLabel);
                foreach (var child in _children)
                    if (FeatureArtGui.Button(child.title + "（" + child.kind + "）")) _window.SelectWorkspaceNode(child);
            }
        }

        void DrawParent()
        {
            var current = Array.IndexOf(_parentIds, _node.parentId ?? "");
            var next = FeatureArtGui.Popup("上级资源", current, _parentLabels);
            if (next != current)
            {
                _node.parentId = _parentIds[next];
                Changed(true);
            }
        }

        void DrawSource()
        {
            EditorGUILayout.LabelField("概念图与源文件", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _node.sourceId = FeatureArtGui.Field("源登记标识", _node.sourceId, true);
            if (EditorGUI.EndChangeCheck()) Changed(false);
            if (string.IsNullOrEmpty(_node.sourceId))
            {
                if (GUILayout.Button("为此节点建立源登记"))
                {
                    _node.sourceId = _node.id;
                    Changed(false);
                }
                return;
            }
            FeatureArtCellArtBridge.DrawViews(_window, _node.sourceId, _node.title, _window.FindSlot(_node.slotId));
        }

        void DrawSlot()
        {
            EditorGUILayout.LabelField("成品绑定", EditorStyles.boldLabel);
            var current = Array.IndexOf(_slotIds, _node.slotId ?? "");
            var next = FeatureArtGui.Popup("关联绑定槽", Math.Max(0, current), _slotLabels);
            if (next != current)
            {
                _node.slotId = _slotIds[next];
                Changed(false);
            }
            var slot = _window.FindSlot(_node.slotId);
            if (slot == null)
            {
                if (GUILayout.Button("新建成品绑定槽"))
                {
                    if (_window.Data == null) { _window.LogError("绑定表未加载，无法新建槽。"); return; }
                    slot = new FeatureArtSlot
                    {
                        id = "custom." + _node.id, domain = "custom", key = _node.id, role = "mesh",
                        titleZh = _node.title, bindKind = "InstancedMesh", folderHint = _window.Workspace.outputFolder,
                        location = "", package = "",
                    };
                    _window.Data.slots.Add(slot);
                    _node.slotId = slot.id;
                    _window.MarkDirty();
                    Changed(false);
                }
                return;
            }
            using (new EditorGUI.DisabledScope(true)) FeatureArtGui.Field("槽标识", slot.id);
            EditorGUI.BeginChangeCheck();
            slot.titleZh = FeatureArtGui.Field("槽名称", slot.titleZh);
            // 原有槽的 domain/key/role 是运行时查找键；新槽可自由定义。
            using (new EditorGUI.DisabledScope(!slot.id.StartsWith("custom.", StringComparison.Ordinal)))
            {
                slot.domain = FeatureArtGui.Field("用途域", slot.domain);
                slot.key = FeatureArtGui.Field("资源键 / 生成文件名", slot.key);
                slot.role = FeatureArtGui.Field("角色", slot.role);
            }
            var kindIndex = Array.IndexOf(BindKinds, slot.bindKind);
            if (kindIndex >= 0)
            {
                var selectedKind = FeatureArtGui.Popup("成品类型", kindIndex, BindLabels);
                // 已绑定时先清空再改类型，避免留下类型不匹配的旧 location。
                if (selectedKind != kindIndex && string.IsNullOrEmpty(slot.location)) slot.bindKind = BindKinds[selectedKind];
            }
            else EditorGUILayout.LabelField("成品类型", slot.bindKind ?? "未指定");
            var folder = FeatureArtGui.Field("成品输出目录", slot.folderHint, true);
            if (folder != slot.folderHint)
            {
                try { slot.folderHint = FeatureArtWorkspace.ValidateFolder(folder, "Assets/GameRes/Raw"); }
                catch (Exception e) { _window.LogError(e.Message); }
            }
            slot.retired = EditorGUILayout.Toggle("弃用成品槽", slot.retired);
            slot.purpose = TextArea("用途", slot.purpose);
            slot.howTo = TextArea("使用方式", slot.howTo);
            slot.expected = TextArea("交付要求", slot.expected);
            slot.constraints = TextArea("制作约束", slot.constraints);
            slot.look = TextArea("外观说明", slot.look);
            slot.prompt = TextArea("生成提示词", slot.prompt);
            if (EditorGUI.EndChangeCheck()) _window.MarkDirty();
            _window.DrawBindField(slot);
            FeatureArtGui.Label("当前绑定：" + (string.IsNullOrEmpty(slot.location) ? "未绑定" : slot.location));
        }

        void DrawAdd()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(_node == null ? "新增根节点" : "新增下级资源", EditorStyles.boldLabel);
            _newTitle = FeatureArtGui.Field("名称", _newTitle);
            _newKind = FeatureArtGui.Field("类型（自定义）", _newKind);
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_newTitle)))
                if (GUILayout.Button("添加"))
                {
                    var node = _window.Workspace.Add(_node?.id, _newTitle.Trim(), _newKind);
                    Changed(true);
                    _window.SelectWorkspaceNode(node);
                }
        }

        string TextArea(string label, string value)
        {
            if (!_textAreas.TryGetValue(label, out var area)) _textAreas[label] = area = new FeatureArtGui.TextArea();
            return area.Draw(label, value);
        }

        // Layout, repaint and focus events reuse these arrays until their underlying data changes.
        internal void ReleaseChoices()
        {
            _choicesRevision = _dependencyCount = _slotRevision = -1;
            _parentIds = _parentLabels = _dependencyIds = _dependencyLabels = _slotIds = _slotLabels = null;
            _children = _archived = null;
        }

        internal void PrepareChoices()
        {
            var workspace = _window.Workspace;
            var revision = workspace.StructureRevision;
            if (_choicesRevision != revision)
            {
                _choicesRevision = revision;
                _dependencyCount = -1;
                _archived = workspace.nodes.Where(n => n.archived).ToList();
                if (_node != null)
                {
                    _children = workspace.Children(_node.id).Where(n => !n.archived).ToList();
                    var parents = workspace.nodes.Where(n => n.id != _node.id && !n.archived && workspace.CanParent(_node.id, n.id)).ToList();
                    var ids = new List<string> { "" };
                    var labels = new List<string> { "无上级（根节点）" };
                    foreach (var parent in parents)
                    { ids.Add(parent.id); labels.Add(workspace.MenuPath(parent).Substring("资源树/".Length)); }
                    if (!ids.Contains(_node.parentId ?? ""))
                    { ids.Add(_node.parentId); labels.Add("当前上级：" + workspace.Find(_node.parentId)?.title); }
                    _parentIds = ids.ToArray();
                    _parentLabels = labels.ToArray();
                }
            }
            if (_node == null) return;
            if (_dependencyCount != _node.dependencyIds.Count)
            {
                _dependencyCount = _node.dependencyIds.Count;
                var existing = new HashSet<string>(_node.dependencyIds);
                var dependencies = workspace.nodes.Where(n => n.id != _node.id && !n.archived && !existing.Contains(n.id)).ToList();
                _dependencyIds = dependencies.Select(n => n.id).ToArray();
                _dependencyLabels = new[] { "选择资源…" }.Concat(dependencies.Select(n => n.title + " · " + n.id)).ToArray();
            }
            var slotRevision = _window.CatalogRevision;
            if (_slotRevision == slotRevision && _choiceSlotId == _node.slotId) return;
            _slotRevision = slotRevision;
            _choiceSlotId = _node.slotId;
            var slots = (_window.Data?.slots ?? new List<FeatureArtSlot>()).Where(s => s != null).ToList();
            var slotIds = new List<string> { "" };
            var slotLabels = new List<string> { "不关联成品槽" };
            foreach (var slot in slots) { slotIds.Add(slot.id); slotLabels.Add((slot.titleZh ?? slot.id) + " · " + slot.id); }
            if (!string.IsNullOrEmpty(_node.slotId) && !slotIds.Contains(_node.slotId))
            { slotIds.Add(_node.slotId); slotLabels.Add("缺失槽：" + _node.slotId); }
            _slotIds = slotIds.ToArray();
            _slotLabels = slotLabels.ToArray();
        }

        void Changed(bool rebuild)
        {
            _window.MarkWorkspaceDirty();
            if (rebuild) { _window.Workspace.InvalidateStructure(); _window.RebuildWorkspaceTree(); }
        }
    }
}
