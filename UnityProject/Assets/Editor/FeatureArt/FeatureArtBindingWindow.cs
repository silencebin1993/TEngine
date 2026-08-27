using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameLogic.ArtBinding;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>
    /// story-003：功能美术绑定面板。按 domain 分类（玩家/器官/召唤/弹道与特效），
    /// 拖 Raw/ 下资源写 location，保存写回 feature-art-catalog.json。
    /// 菜单：BinGames → 功能美术绑定。对标 <see cref="BinGames.EditorTools.CellArt.CellArtBoardWindow"/> 套路。
    /// </summary>
    public sealed class FeatureArtBindingWindow : EditorWindow
    {
        const string RawPrefix = "Assets/GameRes/Raw/";

        static readonly string[] Categories = { "使用说明", "健康检查", "玩家", "器官", "召唤", "弹道与特效" };

        FeatureArtCatalogData _data;
        int _category;
        Vector2 _scroll;
        string _lastLog = "";
        bool _dirty;
        List<HealthIssue> _healthIssues;

        public static void Open()
        {
            var w = GetWindow<FeatureArtBindingWindow>("Feature Art");
            w.minSize = new Vector2(880, 520);
            w.Show();
            w.Reload();
        }

        void OnEnable() => Reload();

        void OnGUI()
        {
            DrawToolbar();

            if (_data == null)
            {
                EditorGUILayout.HelpBox("catalog 未加载。确认 " + FeatureArtCatalogIO.RelativePath + " 存在。", MessageType.Error);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawCategoryList(GUILayout.Width(140));
                using (new EditorGUILayout.VerticalScope())
                {
                    _scroll = EditorGUILayout.BeginScrollView(_scroll);
                    DrawCategoryContent();
                    EditorGUILayout.EndScrollView();
                }
            }

            if (!string.IsNullOrEmpty(_lastLog))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_lastLog, MessageType.None);
            }
        }

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(48)))
                {
                    if (_dirty && !EditorUtility.DisplayDialog("未保存", "有未保存修改，丢弃并刷新？", "丢弃", "取消"))
                    {
                        return;
                    }

                    Reload();
                }

                GUI.enabled = _dirty;
                if (GUILayout.Button("保存", EditorStyles.toolbarButton, GUILayout.Width(48)))
                {
                    Save();
                }

                GUI.enabled = true;

                if (GUILayout.Button("从代码同步槽位", EditorStyles.toolbarButton, GUILayout.Width(110)))
                {
                    RunSync();
                }

                if (GUILayout.Button("打开源文件板", EditorStyles.toolbarButton, GUILayout.Width(90)))
                {
                    BinGames.EditorTools.CellArt.CellArtBoardWindow.Open();
                }

                GUILayout.FlexibleSpace();
                var label = $"{_data?.slots?.Count ?? 0} 槽" + (_dirty ? " · 未保存" : "");
                GUILayout.Label(label, EditorStyles.miniLabel);
            }
        }

        void DrawCategoryList(params GUILayoutOption[] opts)
        {
            using (new EditorGUILayout.VerticalScope(opts))
            {
                for (var i = 0; i < Categories.Length; i++)
                {
                    var selected = i == _category;
                    var style = selected ? EditorStyles.miniButtonMid : EditorStyles.miniButton;
                    if (GUILayout.Toggle(selected, Categories[i], "Button"))
                    {
                        _category = i;
                    }
                }
            }
        }

        void DrawCategoryContent()
        {
            switch (_category)
            {
                case 0:
                    DrawGuide();
                    break;
                case 1:
                    DrawHealthCheck();
                    break;
                case 2:
                    DrawDomain("player", "玩家");
                    break;
                case 3:
                    DrawDomain("organ", "器官");
                    break;
                case 4:
                    DrawDomain("summon", "召唤");
                    break;
                case 5:
                    DrawShapeDomain();
                    break;
            }
        }

        void DrawGuide()
        {
            EditorGUILayout.LabelField("使用说明", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "本面板给人用：左侧按 domain 分类（玩家/器官/召唤/弹道与特效），每个槽位是一个功能美术占位。\n" +
                "把 Assets/GameRes/Raw/ 下的 Prefab/Mesh/Material 拖进对应槽的对象框即可写入 location；\n" +
                "空槽 = 白模，游戏照常能跑，不拖资源不会报错。\n" +
                "拖 Assets/GameRes/Art/ 下的资源会被拒绝——Art 是源文件，不进 YooAsset 热更包。\n" +
                "「从代码同步槽位」按当前代码内容增槽/标记已废弃（retired），绝不清空/覆盖你已经填好的 location 或 Brief。\n" +
                "「保存」写回 feature-art-catalog.json；「清空绑定」只清该槽 location，Brief 文案不动。",
                MessageType.None);
        }

        void DrawHealthCheck()
        {
            EditorGUILayout.LabelField("健康检查", EditorStyles.boldLabel);
            if (GUILayout.Button("运行健康检查"))
            {
                _healthIssues = FeatureArtHealthCheck.Run(_data);
            }

            if (_healthIssues == null)
            {
                EditorGUILayout.HelpBox("尚未运行。", MessageType.Info);
                return;
            }

            if (_healthIssues.Count == 0)
            {
                var boundCount = _data.slots.Count(s => !s.retired && !string.IsNullOrEmpty(s.location));
                var c = GUI.color;
                GUI.color = Color.green;
                EditorGUILayout.HelpBox($"全部通过，{boundCount} 个已绑定槽零异常", MessageType.Info);
                GUI.color = c;
                return;
            }

            foreach (var issue in _healthIssues)
            {
                var c = GUI.color;
                GUI.color = Color.red;
                EditorGUILayout.HelpBox($"{issue.SlotId}: {issue.Message}", MessageType.Error);
                GUI.color = c;
            }
        }

        void DrawDomain(string domain, string titleZh)
        {
            EditorGUILayout.LabelField(titleZh, EditorStyles.boldLabel);
            var slots = _data.slots.Where(s => s.domain == domain).OrderBy(s => s.id).ToList();
            if (slots.Count == 0)
            {
                EditorGUILayout.HelpBox("暂无槽位，点工具栏「从代码同步槽位」生成。", MessageType.Info);
                return;
            }

            foreach (var slot in slots)
            {
                DrawSlot(slot);
            }
        }

        void DrawShapeDomain()
        {
            EditorGUILayout.LabelField("弹道与特效", EditorStyles.boldLabel);
            var slots = _data.slots.Where(s => s.domain == "shape").ToList();
            if (slots.Count == 0)
            {
                EditorGUILayout.HelpBox("暂无槽位，点工具栏「从代码同步槽位」生成。", MessageType.Info);
                return;
            }

            var byShape = slots.GroupBy(s => s.key).OrderBy(g => g.Key);
            foreach (var group in byShape)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(group.Key, EditorStyles.boldLabel);
                foreach (var slot in group.OrderBy(s => s.role))
                {
                    DrawSlot(slot);
                }
            }
        }

        void DrawSlot(FeatureArtSlot slot)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(slot.titleZh, EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    DrawBadge(slot);
                }

                GUI.enabled = false;
                EditorGUILayout.TextField("id", slot.id);
                EditorGUILayout.TextField("bindKind", slot.bindKind);
                EditorGUILayout.TextField("folderHint", slot.folderHint);
                GUI.enabled = true;

                EditorGUI.BeginChangeCheck();
                slot.purpose = EditorGUILayout.TextField("purpose", slot.purpose ?? "");
                slot.howTo = EditorGUILayout.TextField("howTo", slot.howTo ?? "");
                slot.expected = EditorGUILayout.TextField("expected", slot.expected ?? "");
                slot.constraints = EditorGUILayout.TextField("constraints", slot.constraints ?? "");
                if (EditorGUI.EndChangeCheck())
                {
                    _dirty = true;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    var objType = ObjectFieldType(slot.bindKind);
                    var current = ResolveCurrentAsset(slot, objType);
                    EditorGUI.BeginChangeCheck();
                    var picked = EditorGUILayout.ObjectField("拖入资源", current, objType, false);
                    if (EditorGUI.EndChangeCheck() && picked != null)
                    {
                        TryBind(slot, picked);
                    }

                    if (GUILayout.Button("清空绑定", GUILayout.Width(70)))
                    {
                        slot.location = "";
                        _dirty = true;
                        _lastLog = $"{slot.id} 已清空 location（Brief 保留）。";
                    }
                }

                GUI.enabled = false;
                EditorGUILayout.TextField("location", slot.location ?? "");
                GUI.enabled = true;
            }
        }

        void DrawBadge(FeatureArtSlot slot)
        {
            if (slot.retired)
            {
                GUILayout.Label("已废弃", EditorStyles.miniLabel);
            }

            if (string.IsNullOrEmpty(slot.location))
            {
                GUILayout.Label("白模", EditorStyles.miniLabel);
            }
            else if (HasFilenameConflict(slot.location))
            {
                var c = GUI.color;
                GUI.color = Color.red;
                GUILayout.Label("无效：文件名冲突", EditorStyles.miniLabel);
                GUI.color = c;
            }
            else
            {
                var c = GUI.color;
                GUI.color = Color.green;
                GUILayout.Label("已绑定", EditorStyles.miniLabel);
                GUI.color = c;
            }
        }

        void TryBind(FeatureArtSlot slot, UnityEngine.Object picked)
        {
            try
            {
                var path = AssetDatabase.GetAssetPath(picked);
                if (string.IsNullOrEmpty(path) || !path.StartsWith(RawPrefix, StringComparison.Ordinal))
                {
                    _lastLog = $"拒绝：{slot.id} 所选资源不在 {RawPrefix} 下（{path}）。";
                    return;
                }

                if (!ValidateKind(slot.bindKind, picked, out var reason))
                {
                    _lastLog = $"拒绝：{slot.id} {reason}";
                    return;
                }

                slot.location = Path.GetFileNameWithoutExtension(path);
                slot.package = "";
                _dirty = true;
                _lastLog = $"{slot.id} → location={slot.location}";
            }
            catch (Exception e)
            {
                _lastLog = e.Message;
                Debug.LogError(e);
            }
        }

        static bool ValidateKind(string bindKind, UnityEngine.Object obj, out string reason)
        {
            switch (bindKind)
            {
                case "InstancedMesh":
                    if (obj is Mesh)
                    {
                        reason = null;
                        return true;
                    }

                    if (obj is GameObject go && go.GetComponentInChildren<MeshFilter>(true) != null)
                    {
                        reason = null;
                        return true;
                    }

                    reason = "InstancedMesh 需要 Mesh 或含 MeshFilter 的 GameObject。";
                    return false;
                case "MaterialOverride":
                    if (obj is Material)
                    {
                        reason = null;
                        return true;
                    }

                    reason = "MaterialOverride 需要 Material。";
                    return false;
                case "PooledPrefab":
                    if (obj is GameObject)
                    {
                        reason = null;
                        return true;
                    }

                    reason = "PooledPrefab 需要 GameObject Prefab。";
                    return false;
                default:
                    reason = $"未知 bindKind：{bindKind}";
                    return false;
            }
        }

        static Type ObjectFieldType(string bindKind)
        {
            switch (bindKind)
            {
                case "MaterialOverride": return typeof(Material);
                case "PooledPrefab": return typeof(GameObject);
                default: return typeof(UnityEngine.Object);
            }
        }

        static UnityEngine.Object ResolveCurrentAsset(FeatureArtSlot slot, Type type)
        {
            if (string.IsNullOrEmpty(slot.location))
            {
                return null;
            }

            var guids = AssetDatabase.FindAssets(slot.location, new[] { "Assets/GameRes/Raw" });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != slot.location)
                {
                    continue;
                }

                var asset = AssetDatabase.LoadAssetAtPath(path, type);
                if (asset != null)
                {
                    return asset;
                }
            }

            return null;
        }

        static bool HasFilenameConflict(string location)
        {
            if (string.IsNullOrEmpty(location))
            {
                return false;
            }

            var guids = AssetDatabase.FindAssets("", new[] { "Assets/GameRes/Raw" });
            var count = 0;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == location)
                {
                    count++;
                }
            }

            return count > 1;
        }

        void RunSync()
        {
            try
            {
                var added = FeatureArtSlotSync.Sync(_data);
                _dirty = true;
                _lastLog = $"同步完成：新增 {added} 槽（已存在槽只更新 retired 标记，location/Brief 不动）。";
            }
            catch (Exception e)
            {
                _lastLog = e.Message;
                Debug.LogError(e);
            }
        }

        void Reload()
        {
            try
            {
                _data = FeatureArtCatalogIO.Load();
                _dirty = false;
                _lastLog = $"已加载 {_data.slots.Count} 槽。";
            }
            catch (Exception e)
            {
                _data = null;
                _lastLog = e.Message;
                Debug.LogError(e);
            }

            Repaint();
        }

        void Save()
        {
            try
            {
                FeatureArtCatalogIO.Save(_data);
                _dirty = false;
                _lastLog = $"已保存 {FeatureArtCatalogIO.AbsolutePath}";
            }
            catch (Exception e)
            {
                _lastLog = e.Message;
                Debug.LogError(e);
            }
        }
    }
}
