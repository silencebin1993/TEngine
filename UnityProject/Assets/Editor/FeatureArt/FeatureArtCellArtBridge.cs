using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BinGames.EditorTools.CellArt;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>功能页概念图/三视图：ObjectField 换绑，写回 registry.json（不是 catalog.location）。</summary>
    public static class FeatureArtCellArtBridge
    {
        public const string PlayerCellArtId = "player_base_core";

        static readonly string[] FixedViewKeys = { "front", "left", "right", "back" };
        static readonly Regex ViewKeyRe = new Regex("^[a-z0-9_]+$", RegexOptions.CultureInvariant);

        static readonly Dictionary<string, string> OrganMap = new Dictionary<string, string>
        {
            ["org_phago"] = "mod_devour_maw",
            ["org_cilia"] = "mod_agile_flagella",
            ["org_spine"] = "mod_corrupt_thorn_patch",
            ["org_bud"] = "mod_spore_cluster",
            ["org_mycelium"] = "mod_nest_anchor",
            ["org_emitter"] = "mod_emitter",
            ["org_lensbeam"] = "mod_lensbeam",
            ["org_orbitcilia"] = "mod_orbitcilia",
            ["org_pseudopod"] = "mod_pseudopod",
            ["org_drill"] = "mod_drill",
            ["org_enzyme"] = "mod_enzyme",
            ["org_osmotic"] = "mod_osmotic",
            ["org_wave"] = "mod_wave",
        };

        static readonly Dictionary<string, string> ShapeMap = new Dictionary<string, string>
        {
            ["Bolt"] = "proj_spike_bolt",
            ["Beam"] = "proj_volt_orb",
            ["Spore"] = "proj_spore_bud",
            ["Field"] = "proj_acid_drop",
            ["Melee"] = "proj_melee_crescent",
            ["Wave"] = "proj_wave_ring",
            ["Arc"] = "proj_arc_fan",
        };

        static readonly Dictionary<string, string> SummonMap = new Dictionary<string, string>
        {
            ["spore"] = "minion_spore_bud",
            ["mycelium"] = "mod_nest_anchor",
            ["phage"] = "minion_phage",
        };

        static readonly Dictionary<string, string> EnemyMap = new Dictionary<string, string>
        {
            ["vis_1"] = "enemy_blob_food",
            ["vis_2"] = "enemy_spiky_cell",
            ["vis_3"] = "enemy_cilia_sweeper",
            ["vis_4"] = "enemy_hunter",
            ["vis_5"] = "enemy_phage_blob",
            ["vis_6"] = "enemy_hardshell",
            ["vis_7"] = "enemy_jelly_conductive",
            ["vis_8"] = "enemy_spore_rot",
            ["vis_9"] = "enemy_hunter_sharp",
            ["vis_10"] = "enemy_spine_shooter",
            ["vis_11"] = "enemy_mycelium_pad",
            ["vis_20"] = "enemy_corpse_chunk",
            ["vis_50"] = "elite_devourer",
            ["vis_51"] = "elite_whip_king",
            ["vis_52"] = "elite_volt_hunter",
            ["vis_90"] = "boss_prokaryote_p1",
        };

        public static string OrganCellArtId(string organId) => OrganMap.TryGetValue(organId, out var id) ? id : null;
        public static string ShapeCellArtId(string shapeKey) => ShapeMap.TryGetValue(shapeKey, out var id) ? id : null;
        public static string SummonCellArtId(string key) => SummonMap.TryGetValue(key, out var id) ? id : null;
        public static string EnemyCellArtId(string visKey) => EnemyMap.TryGetValue(visKey, out var id) ? id : null;

        public static void DrawViews(FeatureArtBindingWindow window, string cellArtId, string titleZh = null)
        {
            if (window == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(cellArtId))
            {
                EditorGUILayout.HelpBox("此页没有源文件登记 id。", MessageType.None);
                return;
            }

            var registry = window.Registry;
            if (registry == null)
            {
                EditorGUILayout.HelpBox("registry.json 未加载。", MessageType.None);
                return;
            }

            var asset = window.GetWorkingAsset(cellArtId, titleZh);
            asset.views ??= new Dictionary<string, string>();

            var items = new List<(string label, string rel, Action<string> setRel, bool canDelete, Action onDelete)>();
            items.Add(("concept", asset.concept, rel => asset.concept = rel, false, null));
            foreach (var key in FixedViewKeys)
            {
                asset.views.TryGetValue(key, out var rel);
                var captured = key;
                items.Add((captured, rel, next => asset.views[captured] = next ?? "", false, null));
            }

            foreach (var key in asset.views.Keys.Where(k => !FixedViewKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList())
            {
                var captured = key;
                asset.views.TryGetValue(captured, out var rel);
                items.Add((captured, rel, next => asset.views[captured] = next ?? "", true, () =>
                {
                    window.CommitWorkingAsset(asset);
                    asset.views.Remove(captured);
                    window.MarkRegistryDirty();
                    window.Log($"{cellArtId} 已删除视图键 {captured}（文件未删）。");
                }));
            }

            const float CardW = 92f;
            const float Gap = 6f;
            var maxW = window.ContentMaxWidth();
            var cols = Mathf.Max(1, Mathf.FloorToInt((maxW + Gap) / (CardW + Gap)));
            var i = 0;
            while (i < items.Count)
            {
                using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandWidth(false)))
                {
                    var n = Mathf.Min(cols, items.Count - i);
                    for (var c = 0; c < n; c++)
                    {
                        var item = items[i + c];
                        DrawPathCard(window, asset, item.label, item.rel, item.setRel, item.canDelete, item.onDelete, CardW);
                        if (c < n - 1)
                        {
                            GUILayout.Space(Gap);
                        }
                    }
                }

                i += cols;
            }

            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandWidth(false)))
            {
                var pending = window.GetAddViewKey(cellArtId);
                EditorGUI.BeginChangeCheck();
                pending = EditorGUILayout.TextField(pending ?? "", GUILayout.Width(140));
                if (EditorGUI.EndChangeCheck())
                {
                    window.SetAddViewKey(cellArtId, pending);
                }

                if (GUILayout.Button("添加视图", GUILayout.Width(72)))
                {
                    TryAddView(window, asset, cellArtId, pending);
                }
            }
        }

        static void TryAddView(FeatureArtBindingWindow window, CellArtAsset asset, string cellArtId, string pending)
        {
            var key = (pending ?? "").Trim().ToLowerInvariant();
            if (!ViewKeyRe.IsMatch(key))
            {
                window.Log("视图键名只允许小写字母、数字、下划线。");
                return;
            }

            if (key == "concept" || FixedViewKeys.Contains(key) || asset.views.ContainsKey(key))
            {
                window.Log($"视图键 {key} 已在列表中。");
                return;
            }

            window.CommitWorkingAsset(asset);
            asset.views[key] = "";
            window.SetAddViewKey(cellArtId, "");
            window.MarkRegistryDirty();
            window.Log($"{cellArtId} 已添加视图键 {key}。");
        }

        static void DrawPathCard(
            FeatureArtBindingWindow window,
            CellArtAsset asset,
            string label,
            string rel,
            Action<string> setRel,
            bool canDelete,
            Action onDelete,
            float cardW)
        {
            const float Thumb = 72f;
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(cardW), GUILayout.MaxWidth(cardW), GUILayout.ExpandWidth(false)))
            {
                EditorGUILayout.LabelField(label, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(cardW));
                var tex = string.IsNullOrEmpty(rel)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<Texture2D>(CellArtRegistryService.AssetPathOf(rel));
                var thumb = GUILayoutUtility.GetRect(
                    Thumb, Thumb,
                    GUILayout.Width(Thumb),
                    GUILayout.Height(Thumb),
                    GUILayout.ExpandWidth(false));
                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(thumb, new Color(0.1f, 0.1f, 0.12f));
                    if (tex != null)
                    {
                        GUI.DrawTexture(thumb, tex, ScaleMode.ScaleToFit);
                    }
                }

                EditorGUI.BeginChangeCheck();
                var picked = (Texture2D)EditorGUILayout.ObjectField(
                    tex, typeof(Texture2D), false, GUILayout.Width(cardW));
                if (EditorGUI.EndChangeCheck())
                {
                    ApplyPicked(window, asset, label, picked, setRel);
                }

                using (new EditorGUILayout.HorizontalScope(GUILayout.Width(cardW)))
                {
                    if (GUILayout.Button("清空", EditorStyles.miniButton, GUILayout.Height(16)))
                    {
                        window.CommitWorkingAsset(asset);
                        setRel("");
                        window.MarkRegistryDirty();
                        window.Log($"{asset.id}.{label} 已清空。");
                    }

                    if (canDelete && GUILayout.Button("删", EditorStyles.miniButton, GUILayout.Width(28), GUILayout.Height(16)))
                    {
                        onDelete?.Invoke();
                    }
                }
            }
        }

        static void ApplyPicked(
            FeatureArtBindingWindow window,
            CellArtAsset asset,
            string label,
            Texture2D picked,
            Action<string> setRel)
        {
            if (picked == null)
            {
                window.CommitWorkingAsset(asset);
                setRel("");
                window.MarkRegistryDirty();
                window.Log($"{asset.id}.{label} 已清空。");
                return;
            }

            if (TryRelFromTexture(picked, out var next, out var error))
            {
                window.CommitWorkingAsset(asset);
                setRel(next);
                window.MarkRegistryDirty();
                window.Log($"{asset.id}.{label} → {next}");
            }
            else
            {
                window.Log(error);
            }
        }

        static bool TryRelFromTexture(Texture2D tex, out string rel, out string error)
        {
            rel = null;
            error = null;
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path))
            {
                error = "拒绝：无法解析资源路径。";
                return false;
            }

            var prefix = CellArtRegistryService.CellRelative + "/";
            if (!path.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                error = $"拒绝：概念图必须在 {CellArtRegistryService.CellRelative}/ 下（不能用 Raw/）。";
                return false;
            }

            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var abs = Path.GetFullPath(Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            rel = CellArtRegistryService.RelOf(abs);
            if (string.IsNullOrEmpty(rel))
            {
                error = "拒绝：请选择 Art/Cell 目录下的文件。";
                return false;
            }

            return true;
        }

    }
}
