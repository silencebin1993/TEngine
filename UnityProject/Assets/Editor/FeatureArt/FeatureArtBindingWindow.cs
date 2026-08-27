using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameLogic.ArtBinding;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>story-010：Odin 左树 + 右栏功能美术绑定窗。树按「构筑 → 共享弹道语言 → 场上其它单位」
    /// 组织（PANEL-UX §3），不再是 003 的 domain 平铺。运行时 location 键、collector、JSON 存储不变。
    /// 菜单：BinGames → 功能美术绑定（<see cref="FeatureArtBindingMenu"/> 转调 <see cref="Open"/>，未改）。</summary>
    public sealed class FeatureArtBindingWindow : OdinMenuEditorWindow
    {
        const string RawPrefix = "Assets/GameRes/Raw/";

        /// <summary>PANEL-UX §3 器官→Family/Shape 映射（UI 呈现用，禁止从 OrganelleDef.AttackFamily 派生——
        /// 那是战斗侧枚举，与本表分组不是一回事，见 preflight-decisions.md「关键陷阱」）。</summary>
        static readonly (string OrganId, string GroupZh, string ShapeKey)[] AttackMethodEntries =
        {
            ("org_emitter", "远程", "Bolt"),
            ("org_lensbeam", "远程", "Beam"),
            ("org_orbitcilia", "远程", "Bolt"),
            ("org_cilia", "近战", "Melee"),
            ("org_spine", "近战", "Melee"),
            ("org_phago", "近战", "Melee"),
            ("org_pseudopod", "近战", "Melee"),
            ("org_drill", "近战", "Melee"),
            ("org_enzyme", "场", "Field"),
            ("org_osmotic", "场", "Field"),
            ("org_wave", "波", "Wave"),
            ("org_bud", "召唤类", "Spore"),
            ("org_mycelium", "召唤类", "Spore"),
        };

        static readonly string[] ShapeOrder = { "Bolt", "Beam", "Melee", "Field", "Wave", "Spore", "Arc" };

        static readonly (string Key, string TitleZh)[] SummonEntries =
        {
            ("spore", "跟随芽体"),
            ("phage", "追击噬菌"),
            ("mycelium", "固着炮台"),
        };

        FeatureArtCatalogData _data;
        bool _dirty;
        string _lastLog = "";
        List<HealthIssue> _healthIssues;

        readonly Dictionary<string, OrganPage> _organPages = new Dictionary<string, OrganPage>();
        readonly Dictionary<string, ShapePage> _shapePages = new Dictionary<string, ShapePage>();
        readonly Dictionary<string, SimpleMeshPage> _summonPages = new Dictionary<string, SimpleMeshPage>();

        public FeatureArtCatalogData Data => _data;
        public List<HealthIssue> HealthIssues => _healthIssues;

        public static void Open()
        {
            var w = GetWindow<FeatureArtBindingWindow>();
            w.titleContent = new GUIContent("Feature Art");
            w.minSize = new Vector2(980, 560);
            w.Show();
        }

        protected override OdinMenuTree BuildMenuTree()
        {
            if (_data == null)
            {
                Reload();
            }

            _organPages.Clear();
            _shapePages.Clear();
            _summonPages.Clear();

            var tree = new OdinMenuTree(false);
            tree.Config.DrawSearchToolbar = true;

            tree.Add("使用说明", new GuidePage());
            tree.Add("健康检查", new HealthCheckPage(this));
            tree.Add("玩家/本体", new PlayerPage(this));

            foreach (var entry in AttackMethodEntries)
            {
                var summonKey = entry.OrganId == "org_bud" ? "spore" : entry.OrganId == "org_mycelium" ? "mycelium" : null;
                var page = new OrganPage(this, entry.OrganId, entry.GroupZh, entry.ShapeKey, summonKey);
                _organPages[entry.OrganId] = page;
                var slot = FindSlot($"organ.{entry.OrganId}.mesh");
                var titleZh = slot?.titleZh?.Replace(" · 本体网格", "") ?? entry.OrganId;
                tree.Add($"攻击方式/{entry.GroupZh}/{titleZh} {StatusGlyph(slot)}", page);
            }

            foreach (var shape in ShapeOrder)
            {
                var page = new ShapePage(this, shape);
                _shapePages[shape] = page;
                tree.Add($"弹道语言/{shape}", page);
            }

            foreach (var s in SummonEntries)
            {
                var slot = FindSlot($"summon.{s.Key}.mesh");
                var page = new SimpleMeshPage(this, $"summon.{s.Key}.mesh", s.TitleZh, null);
                _summonPages[s.Key] = page;
                tree.Add($"召唤实体/{s.TitleZh} {StatusGlyph(slot)}", page);
            }

            var families = GameLogic.ArtBinding.FeatureArtVisualBinder.EnemyVisualFamilies;
            for (var i = 0; i < families.Length; i++)
            {
                var fam = families[i];
                var group = i < 12 ? "杂兵" : i < 15 ? "精英" : "首领";
                var slot = FindSlot($"enemy.{fam.Key}.mesh");
                var page = new SimpleMeshPage(this, $"enemy.{fam.Key}.mesh", fam.TitleZh, "一族共用，换色/缩放，不要每敌一模。");
                tree.Add($"敌人/{group}/{fam.TitleZh} {StatusGlyph(slot)}", page);
            }

            return tree;
        }

        protected override void OnBeginDrawEditors()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(48)))
                {
                    if (_dirty && !EditorUtility.DisplayDialog("未保存", "有未保存修改，丢弃并刷新？", "丢弃", "取消"))
                    {
                        // keep unsaved
                    }
                    else
                    {
                        Reload();
                        ForceMenuTreeRebuild();
                    }
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

            if (!string.IsNullOrEmpty(_lastLog))
            {
                EditorGUILayout.HelpBox(_lastLog, MessageType.None);
            }
        }

        public FeatureArtSlot FindSlot(string id) => _data?.slots?.FirstOrDefault(s => s.id == id);

        public void Log(string message) => _lastLog = message;

        public void MarkDirty() => _dirty = true;

        public void RunHealthCheck() => _healthIssues = FeatureArtHealthCheck.Run(_data);

        public void JumpToShape(string shapeKey)
        {
            if (_shapePages.TryGetValue(shapeKey, out var page))
            {
                SelectPageObject(page);
            }
        }

        public void JumpToOrgan(string organId)
        {
            if (_organPages.TryGetValue(organId, out var page))
            {
                SelectPageObject(page);
            }
        }

        public void JumpToSummon(string key)
        {
            if (_summonPages.TryGetValue(key, out var page))
            {
                SelectPageObject(page);
            }
        }

        /// <summary>Odin 此版本无 TrySelectMenuItemWithObject，改用 EnumerateTree 按 Value 引用查找 + Select。</summary>
        void SelectPageObject(object page)
        {
            var item = MenuTree.EnumerateTree(false).FirstOrDefault(i => ReferenceEquals(i.Value, page));
            item?.Select(false);
        }

        public List<(string OrganId, string TitleZh)> OrgansUsingShape(string shapeKey)
        {
            var result = new List<(string, string)>();
            foreach (var e in AttackMethodEntries)
            {
                if (e.ShapeKey != shapeKey)
                {
                    continue;
                }

                var slot = FindSlot($"organ.{e.OrganId}.mesh");
                result.Add((e.OrganId, slot?.titleZh?.Replace(" · 本体网格", "") ?? e.OrganId));
            }

            return result;
        }

        public List<string> OrgansSharingShape(string shapeKey, string excludeOrganId)
        {
            var result = new List<string>();
            foreach (var e in AttackMethodEntries)
            {
                if (e.ShapeKey != shapeKey || e.OrganId == excludeOrganId)
                {
                    continue;
                }

                var slot = FindSlot($"organ.{e.OrganId}.mesh");
                result.Add(slot?.titleZh?.Replace(" · 本体网格", "") ?? e.OrganId);
            }

            return result;
        }

        /// <summary>拖拽绑定：Raw 前缀校验 + bindKind 校验，写 location（story-003 原逻辑，未改）。</summary>
        public void TryBind(FeatureArtSlot slot, UnityEngine.Object picked)
        {
            if (slot == null || picked == null)
            {
                return;
            }

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

        /// <summary>拖拽/清空/复制共用的字段绘制（story-003「保存/清空」逻辑原样复用，Required 7）。</summary>
        public void DrawBindField(FeatureArtSlot slot, float previewHeight = 56)
        {
            if (slot == null)
            {
                EditorGUILayout.HelpBox("未同步", MessageType.None);
                return;
            }

            var type = ObjectFieldType(slot.bindKind);
            var current = ResolveCurrentAsset(slot, type);
            EditorGUI.BeginChangeCheck();
            var picked = EditorGUILayout.ObjectField(current, type, false, GUILayout.Height(previewHeight));
            if (EditorGUI.EndChangeCheck() && picked != null)
            {
                TryBind(slot, picked);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("清空", GUILayout.Width(48)))
                {
                    slot.location = "";
                    _dirty = true;
                    _lastLog = $"{slot.id} 已清空 location（look/prompt 保留）。";
                }

                if (GUILayout.Button("复制提示词", GUILayout.Width(80)))
                {
                    EditorGUIUtility.systemCopyBuffer = slot.prompt ?? "";
                    _lastLog = $"{slot.id} 提示词已复制。";
                }
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

        public static Type ObjectFieldType(string bindKind)
        {
            switch (bindKind)
            {
                case "MaterialOverride": return typeof(Material);
                case "PooledPrefab": return typeof(GameObject);
                default: return typeof(UnityEngine.Object);
            }
        }

        public static UnityEngine.Object ResolveCurrentAsset(FeatureArtSlot slot, Type type)
        {
            if (slot == null || string.IsNullOrEmpty(slot.location))
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

        static string StatusGlyph(FeatureArtSlot slot)
        {
            if (slot == null || slot.retired)
            {
                return "";
            }

            if (string.IsNullOrEmpty(slot.location))
            {
                return "○";
            }

            return HasFilenameConflict(slot.location) ? "✕" : "●";
        }

        void RunSync()
        {
            try
            {
                var added = FeatureArtSlotSync.Sync(_data);
                _dirty = true;
                _lastLog = $"同步完成：新增 {added} 槽（look/prompt 已按 LOOK-PROMPTS 覆盖；location 不动）。";
                ForceMenuTreeRebuild();
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

        // ---- 右栏页面（Odin 通过反射画这些 POCO；OnInspectorGUI 承载复用的绑定/校验逻辑）----

        sealed class GuidePage
        {
            [OnInspectorGUI]
            void Draw()
            {
                EditorGUILayout.LabelField("使用说明", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "选攻击方式 → 复制外形提示词做模型 → 复制开火提示词做特效 → 拖进同一页。\n\n" +
                    "1. 工具栏『从代码同步槽位』补齐新功能的空槽（look/prompt 每次都会按 LOOK-PROMPTS 覆盖，location 不动）；\n" +
                    "2. 选中左树『攻击方式』下的器官，同一页拖外形网格、复制开火四段提示词；\n" +
                    "3. 做好的资源放进建议目录（Raw 下），拖进对应槽的对象框即可写入 location；\n" +
                    "4. 点工具栏『保存』；\n" +
                    "5. Play 模式或『健康检查』核对。",
                    MessageType.Info);
                EditorGUILayout.HelpBox(
                    "空槽 = 白模，游戏照常能跑；拖 Assets/GameRes/Art/ 下资源会被拒绝——Art 是源文件，不进 YooAsset 热更包。\n" +
                    "location = 拖入资源文件名（去扩展名），Raw 全树文件名须全局唯一，撞名会被『健康检查』标红。",
                    MessageType.None);
            }
        }

        sealed class HealthCheckPage
        {
            readonly FeatureArtBindingWindow _window;

            public HealthCheckPage(FeatureArtBindingWindow window) => _window = window;

            [Button("运行健康检查")]
            void Run() => _window.RunHealthCheck();

            [OnInspectorGUI]
            void Draw()
            {
                var issues = _window.HealthIssues;
                if (issues == null)
                {
                    EditorGUILayout.HelpBox("尚未运行。", MessageType.Info);
                    return;
                }

                if (issues.Count == 0)
                {
                    var boundCount = _window.Data?.slots?.Count(s => !s.retired && !string.IsNullOrEmpty(s.location)) ?? 0;
                    EditorGUILayout.HelpBox($"全部通过，{boundCount} 个已绑定槽零异常", MessageType.Info);
                    return;
                }

                foreach (var issue in issues)
                {
                    EditorGUILayout.HelpBox($"{issue.SlotId}: {issue.Message}", MessageType.Error);
                }
            }
        }

        sealed class PlayerPage
        {
            readonly FeatureArtBindingWindow _window;

            public PlayerPage(FeatureArtBindingWindow window) => _window = window;

            FeatureArtSlot MeshSlot => _window.FindSlot("player.chassis.mesh");
            FeatureArtSlot MaterialSlot => _window.FindSlot("player.chassis.material");

            [OnInspectorGUI]
            void Draw()
            {
                var meshSlot = MeshSlot;
                EditorGUILayout.LabelField("玩家本体", EditorStyles.boldLabel);
                if (meshSlot != null && !string.IsNullOrEmpty(meshSlot.look))
                {
                    EditorGUILayout.LabelField(meshSlot.look, EditorStyles.wordWrappedLabel);
                }

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(240)))
                    {
                        EditorGUILayout.LabelField("网格", EditorStyles.miniBoldLabel);
                        _window.DrawBindField(meshSlot, 64);
                    }

                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(240)))
                    {
                        EditorGUILayout.LabelField("材质", EditorStyles.miniBoldLabel);
                        var mat = MaterialSlot;
                        if (mat != null && !string.IsNullOrEmpty(mat.look))
                        {
                            EditorGUILayout.HelpBox(mat.look, MessageType.None);
                        }

                        _window.DrawBindField(mat, 64);
                    }
                }
            }
        }

        sealed class OrganPage
        {
            readonly FeatureArtBindingWindow _window;
            readonly string _organId;
            readonly string _groupZh;
            readonly string _shapeKey;
            readonly string _summonKey;

            public OrganPage(FeatureArtBindingWindow window, string organId, string groupZh, string shapeKey, string summonKey)
            {
                _window = window;
                _organId = organId;
                _groupZh = groupZh;
                _shapeKey = shapeKey;
                _summonKey = summonKey;
            }

            FeatureArtSlot MeshSlot => _window.FindSlot($"organ.{_organId}.mesh");
            FeatureArtSlot ShapeSlot(string role) => _window.FindSlot($"shape.{_shapeKey}.{role}");

            [OnInspectorGUI, PropertyOrder(-30)]
            void DrawIdentity()
            {
                var slot = MeshSlot;
                var title = slot?.titleZh?.Replace(" · 本体网格", "") ?? _organId;
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"{_groupZh} · {_shapeKey}", EditorStyles.miniLabel);
                if (slot != null && !string.IsNullOrEmpty(slot.look))
                {
                    EditorGUILayout.LabelField(slot.look, EditorStyles.wordWrappedLabel);
                }

                EditorGUILayout.Space(4);
            }

            [BoxGroup("外形"), PreviewField(70), HideLabel, ShowInInspector, PropertyOrder(-20)]
            public UnityEngine.Object Mesh
            {
                get
                {
                    var slot = MeshSlot;
                    return slot == null ? null : FeatureArtBindingWindow.ResolveCurrentAsset(slot, FeatureArtBindingWindow.ObjectFieldType(slot.bindKind));
                }
                set
                {
                    if (value != null)
                    {
                        _window.TryBind(MeshSlot, value);
                    }
                }
            }

            [BoxGroup("外形"), Button("复制生模提示词"), PropertyOrder(-19)]
            void CopyPrompt()
            {
                EditorGUIUtility.systemCopyBuffer = MeshSlot?.prompt ?? "";
                _window.Log($"{_organId} 生模提示词已复制。");
            }

            [OnInspectorGUI, PropertyOrder(0)]
            void DrawFireTimeline()
            {
                EditorGUILayout.Space(4);

                if (_summonKey != null)
                {
                    EditorGUILayout.LabelField("召唤链接", EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox($"链到召唤实体 / {_summonKey}", MessageType.None);
                    if (GUILayout.Button($"跳到召唤实体 / {_summonKey}"))
                    {
                        _window.JumpToSummon(_summonKey);
                    }

                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("仍可绑定该 Shape 的命中/爆炸（召唤物命中语言）", EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        DrawRoleColumn("命中", "hit");
                        DrawRoleColumn("爆炸", "explode");
                    }

                    return;
                }

                EditorGUILayout.LabelField("开火时间轴", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawRoleColumn("枪口", "muzzle");
                    DrawRoleColumn("弹体", "projectile");
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawRoleColumn("命中", "hit");
                    DrawRoleColumn("爆炸", "explode");
                }

                var sharedWith = _window.OrgansSharingShape(_shapeKey, _organId);
                var info = sharedWith.Count > 0
                    ? $"{_shapeKey} 语言与 {string.Join("、", sharedWith)} 共用；改这里两边一起变。"
                    : $"{_shapeKey} 语言当前仅本器官使用。";
                EditorGUILayout.HelpBox(info, MessageType.Info);

                if (GUILayout.Button($"跳到弹道语言 / {_shapeKey}"))
                {
                    _window.JumpToShape(_shapeKey);
                }
            }

            void DrawRoleColumn(string labelZh, string role)
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(200)))
                {
                    EditorGUILayout.LabelField(labelZh, EditorStyles.miniBoldLabel);
                    _window.DrawBindField(ShapeSlot(role));
                }
            }
        }

        sealed class ShapePage
        {
            readonly FeatureArtBindingWindow _window;
            readonly string _shapeKey;

            public ShapePage(FeatureArtBindingWindow window, string shapeKey)
            {
                _window = window;
                _shapeKey = shapeKey;
            }

            [OnInspectorGUI, PropertyOrder(-10)]
            void DrawHeader()
            {
                EditorGUILayout.LabelField($"弹道语言 · {_shapeKey}", EditorStyles.boldLabel);
                var usedBy = _window.OrgansUsingShape(_shapeKey);
                if (usedBy.Count == 0)
                {
                    EditorGUILayout.HelpBox("暂无器官使用该 Shape（预留）。", MessageType.None);
                }
                else
                {
                    EditorGUILayout.LabelField("被谁使用：", EditorStyles.miniBoldLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        foreach (var (organId, titleZh) in usedBy)
                        {
                            if (GUILayout.Button(titleZh, GUILayout.Width(90)))
                            {
                                _window.JumpToOrgan(organId);
                            }
                        }
                    }
                }

                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(0)]
            void DrawRoles()
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawRole("枪口", "muzzle");
                    DrawRole("弹体", "projectile");
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawRole("命中", "hit");
                    DrawRole("爆炸", "explode");
                }
            }

            void DrawRole(string labelZh, string role)
            {
                var slot = _window.FindSlot($"shape.{_shapeKey}.{role}");
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(220)))
                {
                    EditorGUILayout.LabelField(labelZh, EditorStyles.miniBoldLabel);
                    _window.DrawBindField(slot);
                }
            }
        }

        sealed class SimpleMeshPage
        {
            readonly FeatureArtBindingWindow _window;
            readonly string _slotId;
            readonly string _fallbackTitle;
            readonly string _note;

            public SimpleMeshPage(FeatureArtBindingWindow window, string slotId, string fallbackTitle, string note)
            {
                _window = window;
                _slotId = slotId;
                _fallbackTitle = fallbackTitle;
                _note = note;
            }

            FeatureArtSlot Slot => _window.FindSlot(_slotId);

            [OnInspectorGUI]
            void Draw()
            {
                var slot = Slot;
                EditorGUILayout.LabelField(_fallbackTitle, EditorStyles.boldLabel);
                if (slot != null && !string.IsNullOrEmpty(slot.look))
                {
                    EditorGUILayout.LabelField(slot.look, EditorStyles.wordWrappedLabel);
                }

                if (!string.IsNullOrEmpty(_note))
                {
                    EditorGUILayout.HelpBox(_note, MessageType.None);
                }

                EditorGUILayout.Space(4);
                _window.DrawBindField(slot, 64);
            }
        }
    }
}
